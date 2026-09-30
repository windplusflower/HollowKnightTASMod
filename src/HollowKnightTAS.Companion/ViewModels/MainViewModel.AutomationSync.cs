using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Core.Automation;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Ipc;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.Companion.ViewModels
{
    public sealed partial class MainViewModel
    {
        private readonly Dictionary<string, string> externalDraftBases = new();

        private Task<AutomationResultEnvelope> DispatchStudioFullRunCommandAsync(
            AutomationCommandEnvelope command,
            Func<AutomationCommandEnvelope, Task<AutomationResultEnvelope>> execute)
        {
            var dispatcher = Application.Current?.Dispatcher;
            return dispatcher != null && !dispatcher.CheckAccess()
                ? dispatcher.InvokeAsync(() => StudioFullRunCommandAsync(command, execute)).Task.Unwrap()
                : StudioFullRunCommandAsync(command, execute);
        }

        private async Task<AutomationResultEnvelope> StudioFullRunCommandAsync(
            AutomationCommandEnvelope command,
            Func<AutomationCommandEnvelope, Task<AutomationResultEnvelope>> execute)
        {
            var id = command.CommandId;
            var snapshot = id == AutomationCommandIds.FullRunSnapshot;
            var update = id == AutomationCommandIds.FullRunUpdateMovie;
            var transport = id == AutomationCommandIds.FullRunStep || id == AutomationCommandIds.FullRunPlay;
            var recording = id == AutomationCommandIds.BeginFullRunRecording;
            var replay = id == AutomationCommandIds.BeginFullRunReplay;
            var stop = id == AutomationCommandIds.FullRunStop;
            var mutation = command.RequiredScope.StartsWith("control.", StringComparison.Ordinal);
            if (!mutation && !snapshot) return await execute(command);
            if (gridApplying || sequenceSaving || IsRestorePresentationFrozen)
                return StudioResult(command, false, "StudioBusy", "Studio is applying or saving an edit; observe again after it completes.");
            SetGridApplying(true);
            try
            {
                // Reserve the editor before draining an already-running background
                // read/save. The timer cannot start another while gridApplying is set.
                var idleDeadline = DateTime.UtcNow.AddSeconds(3);
                while (savingBranch || recordingGridSync)
                {
                    if (DateTime.UtcNow >= idleDeadline)
                        return StudioResult(command, false, "StudioBusy", "Studio background synchronization has not finished.");
                    await Task.Delay(25);
                }
                // Remember the exact draft observed by this client. A native-frame
                // precondition alone cannot detect edits made while paused.
                var key = command.SessionId + ":" + command.ClientId;
                if (update && ((externalDraftBases.TryGetValue(key, out var observed) && observed != Sha256Utility.ComputeUtf8Hex(MovieText))
                    || (!externalDraftBases.ContainsKey(key) && gridHasUserEdits)))
                    return StudioResult(command, false, "StudioDraftChanged", "Read fullRunSnapshot again before replacing the edited Studio draft.");

                string? incoming = null;
                if (update)
                {
                    incoming = ReadProtectedStudioMovie(command.Arguments["moviePath"]);
                    var stagedArguments = new Dictionary<string, string>(command.Arguments)
                    { ["moviePath"] = await WriteStudioMovieAsync(incoming) };
                    command = new AutomationCommandEnvelope(command.RequestId, command.IdempotencyKey, command.ClientId,
                        command.SessionId, command.ManifestSha256, command.CommandId, command.RequiredScope,
                        command.LeaseId, command.ExpectedRuntimeMode, command.ExpectedMovieTick, IpcPayloadCodec.Serialize(stagedArguments));
                }
                if (replay)
                    incoming = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(command.Arguments["movieBase64"]));

                if (snapshot)
                {
                    if (startupBoot?.IsWaiting != true)
                        return StudioResult(command, false, "PreconditionFailed", "Pause before reading the Studio draft.");
                    var result = await execute(command);
                    if (!result.Success) return result;
                    var frame = long.Parse(result.Data["movieFrame"], CultureInfo.InvariantCulture);
                    var text = MergeStudioSnapshot(ReadProtectedStudioMovie(result.Data["path"]), frame);
                    var path = await WriteStudioMovieAsync(text);
                    RefreshObservedStudioMovie(text);
                    RememberExternalDraft(key);
                    var data = new Dictionary<string, string>(result.Data)
                    {
                        ["path"] = path,
                        ["movieId"] = new MovieV2Codec().ComputeMovieId(movieEditor.ValidateAny(text).V2Document!),
                        ["studioDraft"] = "true"
                    };
                    return StudioResult(command, true, "Ok", "Studio draft snapshot returned.", data);
                }

                if (transport && (gridHasUserEdits || draftRequiresRestart))
                {
                    if (draftRequiresRestart)
                        return StudioResult(command, false, "StudioReplayRequired", "The Studio draft changes past input; replay it to the desired frame first.");
                    // Use the caller's already-authorized control lease, not a second
                    // human lease. The exact native-frame precondition is preserved.
                    var raw = await execute(StudioCommand(command, AutomationCommandIds.FullRunSnapshot, AutomationScope.MovieRead));
                    if (!raw.Success) return StudioResult(command, false, raw.ResultCode, raw.Detail, raw.Data);
                    var frame = long.Parse(raw.Data["movieFrame"], CultureInfo.InvariantCulture);
                    var runtimeText = ReadProtectedStudioMovie(raw.Data["path"]);
                    var text = MergeStudioSnapshot(runtimeText, frame);
                    if (!MovieV2Prefix.Matches(movieEditor.ValidateAny(runtimeText).V2Document!,
                        movieEditor.ValidateAny(text).V2Document!, frame))
                        return StudioResult(command, false, "StudioReplayRequired", "The Studio draft changes past input; replay it first.");
                    var applied = await execute(StudioCommand(command, AutomationCommandIds.FullRunUpdateMovie,
                        AutomationScope.ControlPlayback, new Dictionary<string, string>
                        {
                            ["moviePath"] = await WriteStudioMovieAsync(text),
                            ["expectedNativeFrame"] = command.Arguments["expectedNativeFrame"]
                        }));
                    if (!applied.Success) return StudioResult(command, false, applied.ResultCode, applied.Detail, applied.Data);
                    AcceptExternalStudioMovie(text, false);
                }

                var response = await execute(command);
                if (!response.Success) return response;
                if (id == AutomationCommandIds.QuitGame) return response;
                if (incoming != null)
                {
                    AcceptExternalStudioMovie(incoming, true);
                    RememberExternalDraft(key);
                    if (replay)
                    {
                        SetSequenceInitialSaves(fullRunMovies?.SessionInitialSaves);
                        ResetSequenceSaveTarget(null);
                        StartTimeline(MovieText);
                    }
                }
                if (recording)
                {
                    SetSequenceInitialSaves(fullRunMovies?.SessionInitialSaves);
                    ResetSequenceSaveTarget(null);
                    var header = new MovieV2Header("unknown", "unknown", "unknown",
                        MovieProtocolV2.SelectedNativeProfileId, MovieProtocolV2.ActionSchemaId,
                        command.Arguments["mouseEnabled"] == "true", "none", 0, 0);
                    var fps = command.Arguments.TryGetValue("fps", out var rate) ? MovieFrameRate.Parse(rate) : 50;
                    AcceptExternalStudioMovie(new MovieV2Codec().WriteCanonical(new MovieV2Document("<studio-draft>", header,
                        new[] { new NativeFrameRun(500, Array.Empty<GameInputSample>(), new MovieSourceSpan("<blank>", 1, 1, 1), fps, true) })), false);
                    StartTimeline(MovieText);
                }
                if (stop && response.Data.TryGetValue("path", out var stoppedPath))
                    AcceptExternalStudioMovie(ReadProtectedStudioMovie(stoppedPath), true);

                // Await a fresh state before acknowledging the external operation.
                // This updates frame highlight, playback labels and command enablement
                // even when the native frame did not change (pause/update/seek).
                var state = await execute(StudioCommand(command, AutomationCommandIds.FullRunStatus, AutomationScope.ObserveStatus));
                if (state.Success) HandleTypedEvent(IpcMessageTypes.FullRunState, state.Data);
                if (startupBoot?.IsWaiting == true && fullRunMovies?.Mode == "Recording")
                {
                    var captured = await execute(StudioCommand(command, AutomationCommandIds.FullRunSnapshot, AutomationScope.MovieRead));
                    if (captured.Success)
                    {
                        var frame = long.Parse(captured.Data["movieFrame"], CultureInfo.InvariantCulture);
                        RefreshObservedStudioMovie(MergeStudioSnapshot(ReadProtectedStudioMovie(captured.Data["path"]), frame));
                    }
                }
                TrackGridFrame(CurrentGridFrame, true);
                Status = GridStatus = "AI 操作已同步到 Studio。";
                return response;
            }
            finally { SetGridApplying(false); }
        }

        private string MergeStudioSnapshot(string runtimeText, long frame)
        {
            var runtime = movieEditor.ValidateAny(runtimeText).V2Document
                ?? throw new InvalidDataException("Runtime Movie is invalid.");
            var draft = movieEditor.ValidateAny(MovieText).V2Document;
            if (draft == null) return runtimeText;
            var total = InputGridEditor.Count(draft);
            var runs = gridHasUserEdits && earliestGridEdit < frame ? draft.Runs
                : MovieV2Prefix.Take(runtime, frame).Runs.Concat(total > frame
                    ? SliceV2(draft, frame, total - frame).Runs
                    : Array.Empty<NativeFrameRun>());
            return new MovieV2Codec().WriteCanonical(new MovieV2Document(draft.SourceName, runtime.Header, runs));
        }

        private void RememberExternalDraft(string key)
        {
            if (externalDraftBases.Count >= 64 && !externalDraftBases.ContainsKey(key)) externalDraftBases.Clear();
            externalDraftBases[key] = Sha256Utility.ComputeUtf8Hex(MovieText);
        }

        private void RefreshObservedStudioMovie(string text)
        {
            var pendingEdits = gridHasUserEdits;
            var firstEdit = earliestGridEdit;
            var restart = draftRequiresRestart;
            AcceptExternalStudioMovie(text, false);
            gridHasUserEdits = pendingEdits;
            earliestGridEdit = firstEdit;
            draftRequiresRestart = restart;
        }

        private string ReadProtectedStudioMovie(string path)
        {
            var full = Path.GetFullPath(path);
            if (fullRunMovies == null || !full.StartsWith(Path.GetFullPath(fullRunMovies.ShadowRoot)
                    + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || new FileInfo(full).Length > MovieProtocolV2.MaximumSourceUtf8Bytes)
                throw new InvalidDataException("Movie path or size is invalid.");
            var text = File.ReadAllText(full, new UTF8Encoding(false, true));
            var parsed = movieEditor.ValidateAny(text);
            if (!parsed.Success || parsed.V2Document == null) throw new InvalidDataException("Movie is invalid.");
            return new MovieV2Codec().WriteCanonical(parsed.V2Document);
        }

        private async Task<string> WriteStudioMovieAsync(string text)
        {
            var directory = Path.Combine(fullRunMovies!.ShadowRoot, "HollowKnightTAS");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "studio-" + Guid.NewGuid().ToString("N") + ".hktas");
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
            return path;
        }

        private void AcceptExternalStudioMovie(string text, bool undoable)
        {
            if (undoable && text != MovieText) { PushGridHistory(gridUndo, MovieText); gridRedo.Clear(); }
            MovieText = gridSource = text;
            gridHasUserEdits = false;
            earliestGridEdit = long.MaxValue;
            draftRequiresRestart = false;
            recordingGridNativeFrame = startupBoot?.NativeCompletedFrames ?? -1;
            RefreshInputGrid();
        }

        private static AutomationCommandEnvelope StudioCommand(AutomationCommandEnvelope source, string id, string scope,
            IReadOnlyDictionary<string, string>? arguments = null)
        {
            var request = "studio-sync-" + Guid.NewGuid().ToString("N");
            return new AutomationCommandEnvelope(request, request, source.ClientId, source.SessionId, source.ManifestSha256,
                id, scope, source.LeaseId, source.ExpectedRuntimeMode, null,
                IpcPayloadCodec.Serialize(arguments ?? new Dictionary<string, string>()));
        }

        private static AutomationResultEnvelope StudioResult(AutomationCommandEnvelope command, bool success, string code,
            string detail, IReadOnlyDictionary<string, string>? data = null)
            => new(command.RequestId, success, code, detail, command.SessionId, command.ManifestSha256, -1,
                IpcPayloadCodec.Serialize(data ?? new Dictionary<string, string>()));
    }
}
