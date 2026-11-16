using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
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
        private AutomationCommandEnvelope? activeExternalCommand;
        private Func<AutomationCommandEnvelope, Task<AutomationResultEnvelope>>? activeExternalExecute;

        private Task<AutomationResultEnvelope> ExecuteStudioOrHumanAsync(
            string commandId,
            string scope,
            IReadOnlyDictionary<string, string>? arguments,
            string expectedRuntimeMode,
            long? expectedMovieTick,
            CancellationToken cancellationToken)
        {
            if (activeExternalCommand != null && activeExternalExecute != null)
                return activeExternalExecute(StudioCommand(activeExternalCommand, commandId, scope, arguments));
            return automationBroker.ExecuteHumanAsync(commandId, scope, arguments, expectedRuntimeMode, expectedMovieTick, cancellationToken);
        }

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
            if (id == AutomationCommandIds.GetStatus || id == AutomationCommandIds.FullRunStatus)
            {
                var statusResult = await execute(command);
                if (!statusResult.Success) return statusResult;
                var enriched = new Dictionary<string, string>(statusResult.Data, StringComparer.Ordinal);
                EnrichStudioStatusFields(enriched);
                return StudioResult(command, true, statusResult.ResultCode, statusResult.Detail, enriched);
            }

            if (id == AutomationCommandIds.StudioGetMovie)
            {
                return await ExecuteStudioGetMovieAsync(command);
            }

            if (id == AutomationCommandIds.StudioGetTimeline)
            {
                return ExecuteStudioGetTimeline(command);
            }

            if (id == AutomationCommandIds.QuitGame)
            {
                activeExternalCommand = command;
                activeExternalExecute = execute;
                try
                {
                    await QuitGameAsync();
                    return StudioResult(command, true, "Ok", Status);
                }
                catch (Exception ex)
                {
                    return StudioResult(command, false, "PreconditionFailed", ex.Message);
                }
                finally
                {
                    activeExternalCommand = null;
                    activeExternalExecute = null;
                }
            }

            var snapshot = id == AutomationCommandIds.FullRunSnapshot;
            var update = id == AutomationCommandIds.FullRunUpdateMovie;
            var transport = id == AutomationCommandIds.FullRunStep || id == AutomationCommandIds.FullRunPlay;
            var recording = id == AutomationCommandIds.BeginFullRunRecording;
            var replay = id == AutomationCommandIds.BeginFullRunReplay;
            var stop = id == AutomationCommandIds.FullRunStop;
            var studioManaged = id == AutomationCommandIds.StudioEditInputs
                || id == AutomationCommandIds.StudioFrameAction
                || id == AutomationCommandIds.StudioSaveTimelineNode
                || id == AutomationCommandIds.StudioRestoreTimelineNode
                || id == AutomationCommandIds.StudioSwitchWorldline
                || id == AutomationCommandIds.StudioDeleteTimelineNode
                || id == AutomationCommandIds.StudioSaveSequence
                || id == AutomationCommandIds.StudioOpenSequence;
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

                if (studioManaged)
                {
                    activeExternalCommand = command;
                    activeExternalExecute = execute;
                    AutomationResultEnvelope studioResponse;
                    try
                    {
                        studioResponse = await ExecuteStudioManagedCommandAsync(command, key);
                    }
                    finally
                    {
                        activeExternalCommand = null;
                        activeExternalExecute = null;
                    }
                    if (studioResponse.Success)
                    {
                        var syncState = await execute(StudioCommand(command, AutomationCommandIds.FullRunStatus, AutomationScope.ObserveStatus));
                        if (syncState.Success) HandleTypedEvent(IpcMessageTypes.FullRunState, syncState.Data);
                        if (SelectedSession?.Client is { IsConnected: true } connectedClient)
                            EnsureAutoSceneCheckpointCapture(connectedClient);
                        TrackGridFrame(CurrentGridFrame, true);
                        Status = GridStatus;
                    }
                    return studioResponse;
                }

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
                {
                    if (command.Arguments.TryGetValue("movieBase64", out var providedBase64) && !string.IsNullOrEmpty(providedBase64))
                    {
                        incoming = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(providedBase64));
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(MovieText))
                            return StudioResult(command, false, "InvalidMovie", "Studio has no loaded v2 movie to replay.");
                        incoming = MovieText;
                        var stagedArguments = new Dictionary<string, string>(command.Arguments, StringComparer.Ordinal)
                        {
                            ["movieBase64"] = Convert.ToBase64String(new UTF8Encoding(false).GetBytes(incoming))
                        };
                        command = new AutomationCommandEnvelope(command.RequestId, command.IdempotencyKey, command.ClientId,
                            command.SessionId, command.ManifestSha256, command.CommandId, command.RequiredScope,
                            command.LeaseId, command.ExpectedRuntimeMode, command.ExpectedMovieTick, IpcPayloadCodec.Serialize(stagedArguments));
                    }
                }

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

                var completedReplay = fullRunMovies?.Mode == "Completed";
                if (transport && (gridHasUserEdits || draftRequiresRestart || completedReplay))
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
                    var draftDoc = movieEditor.ValidateAny(text).V2Document!;
                    if (!MovieV2Prefix.Matches(movieEditor.ValidateAny(runtimeText).V2Document!, draftDoc, frame))
                        return StudioResult(command, false, "StudioReplayRequired", "The Studio draft changes past input; replay it first.");
                    if (completedReplay)
                    {
                        if (InputGridEditor.Count(draftDoc) <= frame)
                            return StudioResult(command, false, "PreconditionFailed", "当前帧之后没有输入，请先补充空帧。");
                        MovieText = gridSource = text;
                        await RestartDraftAtAsync(frame);
                        startupBoot?.Refresh();
                        if (command.Arguments.ContainsKey("expectedNativeFrame"))
                        {
                            var updatedArgs = new Dictionary<string, string>(command.Arguments, StringComparer.Ordinal)
                            {
                                ["expectedNativeFrame"] = (startupBoot?.NativeCompletedFrames ?? 0).ToString(CultureInfo.InvariantCulture)
                            };
                            command = new AutomationCommandEnvelope(command.RequestId, command.IdempotencyKey, command.ClientId,
                                command.SessionId, command.ManifestSha256, command.CommandId, command.RequiredScope,
                                command.LeaseId, command.ExpectedRuntimeMode, command.ExpectedMovieTick, IpcPayloadCodec.Serialize(updatedArgs));
                        }
                    }
                    else
                    {
                        var applied = await execute(StudioCommand(command, AutomationCommandIds.FullRunUpdateMovie,
                            AutomationScope.ControlPlayback, new Dictionary<string, string>
                            {
                                ["moviePath"] = await WriteStudioMovieAsync(text),
                                ["expectedNativeFrame"] = command.Arguments["expectedNativeFrame"]
                            }));
                        if (!applied.Success) return StudioResult(command, false, applied.ResultCode, applied.Detail, applied.Data);
                        AcceptExternalStudioMovie(text, false);
                    }
                }

                var previousInitialSaves = sequenceInitialSaves;
                if (replay)
                    SetSequenceInitialSaves(fullRunMovies?.SessionInitialSaves);

                var response = await execute(command);
                if (!response.Success)
                {
                    if (replay) SetSequenceInitialSaves(previousInitialSaves);
                    return response;
                }
                if (incoming != null)
                {
                    AcceptExternalStudioMovie(incoming, !replay);
                    RememberExternalDraft(key);
                    if (replay)
                    {
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
                {
                    AcceptExternalStudioMovie(ReadProtectedStudioMovie(stoppedPath), false);
                    ValidateMovie(false);
                }

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
                if (SelectedSession?.Client is { IsConnected: true } client)
                    EnsureAutoSceneCheckpointCapture(client);
                TrackGridFrame(CurrentGridFrame, true);
                Status = GridStatus = "AI 操作已同步到 Studio。";
                return response;
            }
            finally { SetGridApplying(false); }
        }

        private void EnrichStudioStatusFields(IDictionary<string, string> fields)
        {
            fields["studioMovieFrame"] = CurrentGridFrame.ToString(CultureInfo.InvariantCulture);
            fields["studioTotalFrames"] = gridTotalFrames.ToString(CultureInfo.InvariantCulture);
            fields["studioDraftRequiresRestart"] = draftRequiresRestart ? "true" : "false";
            fields["studioHasUserEdits"] = gridHasUserEdits ? "true" : "false";
            fields["activeTreeId"] = activeDraftTree ?? string.Empty;
            fields["activeBranchTipId"] = activeDraftTip?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            fields["selectedWorldlineId"] = selectedWorldline?.Id.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            fields["selectedTimelineNodeId"] = selectedTimelineNode?.Id.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            fields["timelineNodeCount"] = (selectedTimelineTree?.Nodes.Count ?? 0).ToString(CultureInfo.InvariantCulture);
            fields["sceneCheckpointCount"] = (selectedTimelineTree?.Checkpoints.Count ?? 0).ToString(CultureInfo.InvariantCulture);
        }

        private async Task<AutomationResultEnvelope> ExecuteStudioGetMovieAsync(AutomationCommandEnvelope command)
        {
            if (!gridApplying && !sequenceSaving && !IsRestorePresentationFrozen
                && fullRunMovies?.Mode == "Recording" && startupBoot?.IsWaiting == true)
            {
                await SyncRecordingGridAsync();
            }

            var parsed = movieEditor.ValidateAny(MovieText);
            if (!parsed.Success || parsed.V2Document == null)
                return StudioResult(command, false, "InvalidMovie", "Studio does not currently have a valid v2 movie.");

            var doc = parsed.V2Document;
            var total = InputGridEditor.Count(doc);
            var startFrame = command.Arguments.TryGetValue("startFrame", out var s)
                ? long.Parse(s, CultureInfo.InvariantCulture)
                : 0L;
            var frameCount = command.Arguments.TryGetValue("frameCount", out var c)
                ? long.Parse(c, CultureInfo.InvariantCulture)
                : Math.Min(500L, Math.Max(0L, total - startFrame));
            var includeFullText = command.Arguments.TryGetValue("includeFullText", out var inc) && inc == "true";

            var sliceCount = startFrame < total ? Math.Min(frameCount, total - startFrame) : 0L;
            var codec = new MovieV2Codec();
            var sliceText = sliceCount > 0
                ? codec.WriteCanonical(SliceV2(doc, startFrame, sliceCount))
                : string.Empty;

            RememberExternalDraft(command.SessionId + ":" + command.ClientId);

            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["movieId"] = codec.ComputeMovieId(doc),
                ["totalFrames"] = total.ToString(CultureInfo.InvariantCulture),
                ["currentFrame"] = CurrentGridFrame.ToString(CultureInfo.InvariantCulture),
                ["fps"] = (doc.Runs.FirstOrDefault()?.FramesPerSecond ?? ParseFrameRate(DefaultFrameRate)).ToString("0.######", CultureInfo.InvariantCulture),
                ["mouseEnabled"] = doc.Header.MouseEnabled ? "true" : "false",
                ["customKeys"] = string.Join(",", doc.Header.CustomKeys),
                ["draftRequiresRestart"] = draftRequiresRestart ? "true" : "false",
                ["hasUserEdits"] = gridHasUserEdits ? "true" : "false",
                ["sliceStartFrame"] = startFrame.ToString(CultureInfo.InvariantCulture),
                ["sliceFrameCount"] = sliceCount.ToString(CultureInfo.InvariantCulture),
                ["sliceText"] = sliceText
            };
            if (includeFullText)
                data["movieText"] = MovieText;
            return StudioResult(command, true, "Ok", "Studio movie returned.", data);
        }

        private AutomationResultEnvelope ExecuteStudioGetTimeline(AutomationCommandEnvelope command)
        {
            InitializeWorldlines();
            if (worldlines == null)
                return StudioResult(command, false, "StudioUnavailable", TimelineTreeStatus);

            var tree = selectedTimelineTree;
            var treeId = tree?.Id ?? string.Empty;
            var pathNodes = tree != null && selectedWorldline != null
                ? (IEnumerable<TimelineNode>)tree.PathTo(selectedWorldline.Id)
                : Array.Empty<TimelineNode>();
            var storeCheckpoints = tree != null && tree.Id.Length == 32 && SceneCheckpoints != null
                ? SceneCheckpoints.CatalogTree(tree.Id)
                : Array.Empty<HollowKnightTAS.Core.SceneCheckpoints.SceneCheckpointHeader>();

            var payload = new
            {
                activeTreeId = activeDraftTree ?? worldlines.Library.ActiveTreeId,
                selectedTreeId = treeId,
                treeName = tree?.Name ?? string.Empty,
                selectedWorldlineId = selectedWorldline?.Id,
                selectedTimelineNodeId = selectedTimelineNode?.Id,
                nodes = ((IEnumerable<TimelineNode>?)tree?.Nodes ?? Array.Empty<TimelineNode>()).Select(n => new
                {
                    id = n.Id,
                    parentId = n.ParentId,
                    frame = n.Frame,
                    label = n.Label,
                    isBranchTip = n.IsBranchTip,
                    isUnverified = n.IsUnverified
                }),
                leaves = (tree?.Leaves ?? Enumerable.Empty<TimelineNode>()).Select(n => new
                {
                    id = n.Id,
                    frame = n.Frame,
                    label = n.Label
                }),
                path = pathNodes.Select(n => new
                {
                    id = n.Id,
                    parentId = n.ParentId,
                    frame = n.Frame,
                    label = n.Label
                }),
                checkpoints = ((IEnumerable<TimelineCheckpoint>?)tree?.Checkpoints ?? Array.Empty<TimelineCheckpoint>()).Select(c => new
                {
                    checkpointId = c.CheckpointId,
                    frame = c.Frame,
                    transitionId = c.TransitionId,
                    inputPrefixSha256 = c.InputPrefixSha256
                }),
                storeCheckpoints = storeCheckpoints.Select(h => new
                {
                    checkpointId = h.CheckpointId,
                    captureMovieFrame = h.CaptureMovieFrame,
                    resumeMovieFrame = h.ResumeMovieFrame,
                    captureNativeFrame = h.CaptureNativeFrame,
                    resumeNativeFrame = h.ResumeNativeFrame,
                    transitionId = h.TransitionId,
                    payloadBytes = h.PayloadBytes,
                    branchId = h.Scope.BranchId,
                    restoreQualified = h.RestoreQualified
                })
            };

            var json = JsonSerializer.Serialize(payload);
            return StudioResult(command, true, "Ok", "Studio timeline returned.", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["activeTreeId"] = activeDraftTree ?? worldlines.Library.ActiveTreeId,
                ["selectedTreeId"] = treeId,
                ["selectedWorldlineId"] = selectedWorldline?.Id.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ["selectedTimelineNodeId"] = selectedTimelineNode?.Id.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ["nodeCount"] = (tree?.Nodes.Count ?? 0).ToString(CultureInfo.InvariantCulture),
                ["checkpointCount"] = (tree?.Checkpoints.Count ?? 0).ToString(CultureInfo.InvariantCulture),
                ["storeCheckpointCount"] = storeCheckpoints.Count.ToString(CultureInfo.InvariantCulture),
                ["timelineJson"] = json
            });
        }

        private async Task<AutomationResultEnvelope> ExecuteStudioManagedCommandAsync(
            AutomationCommandEnvelope command,
            string clientKey)
        {
            try
            {
                switch (command.CommandId)
                {
                    case AutomationCommandIds.StudioEditInputs:
                        return ExecuteStudioEditInputs(command, clientKey);

                    case AutomationCommandIds.StudioFrameAction:
                    {
                        var targetFrame = long.Parse(command.Arguments["targetFrame"], CultureInfo.InvariantCulture);
                        var action = command.Arguments.TryGetValue("action", out var act) ? act : "auto";
                        if (action == "auto")
                        {
                            if (fullRunMovies?.IsPending != true)
                                throw new InvalidOperationException("帧存档与定位需要从 Studio 启动全流程会话。");
                            if (startupBoot?.IsWaiting != true)
                                await fullRunMovies.PauseForDocumentChangeAsync(CancellationToken.None);
                            var current = CurrentGridFrame;
                            if (startupBoot?.NativeCompletedFrames > 0)
                            {
                                var snap = await ReadReadyFrameSnapshotAsync();
                                current = snap.Frame;
                            }
                            if (!draftRequiresRestart && targetFrame == current)
                            {
                                return StudioResult(command, true, "Ok", "Already at target frame.", new Dictionary<string, string>(StringComparer.Ordinal)
                                {
                                    ["action"] = "noop",
                                    ["currentMovieFrame"] = current.ToString(CultureInfo.InvariantCulture),
                                    ["nativeFrame"] = (startupBoot?.NativeCompletedFrames ?? 0).ToString(CultureInfo.InvariantCulture),
                                    ["usedCheckpointId"] = "none",
                                    ["usedCheckpointFrame"] = "-1"
                                });
                            }
                            action = !draftRequiresRestart
                                && !(gridHasUserEdits && earliestGridEdit < current)
                                && targetFrame > current
                                ? "seek"
                                : "rebuild";
                        }

                        lastRestoredCheckpointId = null;
                        lastRestoredCheckpointFrame = -1;
                        await FrameMenuCoreAsync(action, targetFrame);
                        return StudioResult(command, true, "Ok", GridStatus, new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["action"] = action,
                            ["currentMovieFrame"] = CurrentGridFrame.ToString(CultureInfo.InvariantCulture),
                            ["nativeFrame"] = (startupBoot?.NativeCompletedFrames ?? 0).ToString(CultureInfo.InvariantCulture),
                            ["usedCheckpointId"] = lastRestoredCheckpointId ?? "none",
                            ["usedCheckpointFrame"] = lastRestoredCheckpointFrame.ToString(CultureInfo.InvariantCulture),
                            ["gridStatus"] = GridStatus
                        });
                    }

                    case AutomationCommandIds.StudioSaveTimelineNode:
                    {
                        var targetFrame = command.Arguments.TryGetValue("frame", out var f)
                            ? long.Parse(f, CultureInfo.InvariantCulture)
                            : -1L;
                        await FrameMenuCoreAsync("save", targetFrame);
                        return StudioResult(command, true, "Ok", TimelineTreeStatus, new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["activeTreeId"] = activeDraftTree ?? string.Empty,
                            ["nodeId"] = selectedTimelineNode?.Id.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                            ["worldlineId"] = selectedWorldline?.Id.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                            ["frame"] = (selectedTimelineNode?.Frame ?? targetFrame).ToString(CultureInfo.InvariantCulture),
                            ["label"] = selectedTimelineNode?.Label ?? string.Empty,
                            ["checkpointCount"] = (selectedTimelineTree?.Checkpoints.Count ?? 0).ToString(CultureInfo.InvariantCulture)
                        });
                    }

                    case AutomationCommandIds.StudioRestoreTimelineNode:
                    {
                        if (fullRunMovies?.IsPending != true)
                            throw new InvalidOperationException("请先从 Studio 启动受控游戏。");
                        InitializeWorldlines();
                        if (worldlines == null)
                            throw new InvalidOperationException(TimelineTreeStatus);

                        TimelineTree? tree = null;
                        if (command.Arguments.TryGetValue("treeId", out var treeId) && !string.IsNullOrEmpty(treeId))
                            tree = worldlines.Library.Trees.FirstOrDefault(t => t.Id == treeId)
                                ?? throw new InvalidOperationException("找不到指定的时间线树：" + treeId);
                        else
                            tree = selectedTimelineTree;

                        if (tree == null || tree == freshTimeline || tree.Nodes.Count == 0)
                            throw new InvalidOperationException("当前没有可恢复的时间线存档节点。");

                        TimelineNode? leaf = null;
                        if (command.Arguments.TryGetValue("worldlineId", out var leafText))
                        {
                            var leafId = int.Parse(leafText, CultureInfo.InvariantCulture);
                            leaf = tree.Nodes.FirstOrDefault(n => n.Id == leafId)
                                ?? throw new InvalidOperationException("找不到指定的世界线分支：" + leafId);
                        }

                        TimelineNode? node = null;
                        if (command.Arguments.TryGetValue("nodeId", out var nodeText))
                        {
                            var nodeId = int.Parse(nodeText, CultureInfo.InvariantCulture);
                            node = tree.Nodes.FirstOrDefault(n => n.Id == nodeId)
                                ?? throw new InvalidOperationException("找不到指定的时间线节点：" + nodeId);
                        }
                        else if (command.Arguments.TryGetValue("frame", out var frameText))
                        {
                            var frame = long.Parse(frameText, CultureInfo.InvariantCulture);
                            var searchPath = leaf != null
                                ? tree.PathTo(leaf.Id)
                                : (selectedWorldline != null && tree.Nodes.Any(n => n.Id == selectedWorldline.Id)
                                    ? tree.PathTo(selectedWorldline.Id)
                                    : tree.Nodes);
                            node = searchPath.LastOrDefault(n => n.Frame == frame && n.Movie.Length != 0)
                                ?? tree.Nodes.LastOrDefault(n => n.Frame == frame && n.Movie.Length != 0)
                                ?? throw new InvalidOperationException($"找不到第 {frame} 帧的时间线节点。");
                        }
                        else
                        {
                            node = selectedTimelineNode;
                            if (node == null || !tree.Nodes.Any(n => n.Id == node.Id))
                                throw new InvalidOperationException("当前未选中有效时间线节点。");
                        }

                        if (leaf == null)
                        {
                            if (selectedWorldline != null && tree.Nodes.Any(n => n.Id == selectedWorldline.Id)
                                && tree.PathTo(selectedWorldline.Id).Any(n => n.Id == node.Id))
                            {
                                leaf = selectedWorldline;
                            }
                            else
                            {
                                leaf = tree.Leaves.LastOrDefault(l => tree.PathTo(l.Id).Any(n => n.Id == node.Id))
                                    ?? throw new InvalidOperationException("无法解析节点所属世界线。");
                            }
                        }

                        lastRestoredCheckpointId = null;
                        lastRestoredCheckpointFrame = -1;
                        await RestoreTimelineCoreAsync(tree, leaf, node);
                        RememberExternalDraft(clientKey);
                        return StudioResult(command, true, "Ok", TimelineTreeStatus, new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["activeTreeId"] = activeDraftTree ?? tree.Id,
                            ["restoredNodeId"] = node.Id.ToString(CultureInfo.InvariantCulture),
                            ["restoredWorldlineId"] = leaf.Id.ToString(CultureInfo.InvariantCulture),
                            ["restoredFrame"] = node.Frame.ToString(CultureInfo.InvariantCulture),
                            ["usedCheckpointId"] = lastRestoredCheckpointId ?? "none",
                            ["usedCheckpointFrame"] = lastRestoredCheckpointFrame.ToString(CultureInfo.InvariantCulture),
                            ["currentMovieFrame"] = CurrentGridFrame.ToString(CultureInfo.InvariantCulture),
                            ["nativeFrame"] = (startupBoot?.NativeCompletedFrames ?? 0).ToString(CultureInfo.InvariantCulture)
                        });
                    }

                    case AutomationCommandIds.StudioSwitchWorldline:
                    {
                        InitializeWorldlines();
                        var treeId = command.Arguments.TryGetValue("treeId", out var t) && !string.IsNullOrEmpty(t)
                            ? t
                            : (selectedTimelineTree?.Id ?? activeDraftTree
                                ?? throw new InvalidOperationException("当前没有可切换的时间线树。"));
                        var leafId = int.Parse(command.Arguments["leafId"], CultureInfo.InvariantCulture);
                        await SwitchWorldlineCoreAsync(treeId, leafId);
                        RememberExternalDraft(clientKey);
                        return StudioResult(command, true, "Ok", Status, new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["selectedTreeId"] = selectedTimelineTree?.Id ?? treeId,
                            ["selectedWorldlineId"] = selectedWorldline?.Id.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                            ["selectedTimelineNodeId"] = selectedTimelineNode?.Id.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                            ["draftRequiresRestart"] = draftRequiresRestart ? "true" : "false"
                        });
                    }

                    case AutomationCommandIds.StudioDeleteTimelineNode:
                    {
                        InitializeWorldlines();
                        if (selectedTimelineTree == null || selectedTimelineTree == freshTimeline)
                            throw new InvalidOperationException("当前没有可删除节点的时间线。");
                        var nodeId = int.Parse(command.Arguments["nodeId"], CultureInfo.InvariantCulture);
                        var node = selectedTimelineTree.Nodes.FirstOrDefault(n => n.Id == nodeId)
                            ?? throw new InvalidOperationException("找不到指定的时间线节点：" + nodeId);
                        if (selectedWorldline == null || !selectedTimelineTree.PathTo(selectedWorldline.Id).Any(n => n.Id == nodeId))
                            selectedWorldline = selectedTimelineTree.Leaves.Last(l => selectedTimelineTree.PathTo(l.Id).Any(p => p.Id == nodeId));
                        selectedTimelineNode = node;
                        var (removedNodes, prunedCheckpoints) = DeleteTimelineSelectionCore(requireConfirmation: false);
                        return StudioResult(command, true, "Ok", TimelineTreeStatus, new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["removedNodes"] = removedNodes.ToString(CultureInfo.InvariantCulture),
                            ["prunedCheckpoints"] = prunedCheckpoints.ToString(CultureInfo.InvariantCulture),
                            ["remainingNodeCount"] = (selectedTimelineTree?.Nodes.Count ?? 0).ToString(CultureInfo.InvariantCulture)
                        });
                    }

                    case AutomationCommandIds.StudioSaveSequence:
                    {
                        var path = Path.GetFullPath(command.Arguments["path"]);
                        var dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        sequenceSaving = true;
                        try
                        {
                            await SaveSequenceToPathAsync(path);
                        }
                        finally
                        {
                            sequenceSaving = false;
                        }
                        return StudioResult(command, true, "Ok", "序列已保存：" + path, new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["path"] = path,
                            ["activeTreeId"] = activeDraftTree ?? string.Empty,
                            ["checkpointCount"] = (activeDraftTree != null && activeDraftTree.Length == 32
                                ? SceneCheckpoints.GetTreePackets(activeDraftTree).Count
                                : 0).ToString(CultureInfo.InvariantCulture)
                        });
                    }

                    case AutomationCommandIds.StudioOpenSequence:
                    {
                        var path = Path.GetFullPath(command.Arguments["path"]);
                        await OpenMovieFileWithinGridAsync(path);
                        RememberExternalDraft(clientKey);
                        return StudioResult(command, true, "Ok", Status, new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["path"] = path,
                            ["totalFrames"] = gridTotalFrames.ToString(CultureInfo.InvariantCulture),
                            ["activeTreeId"] = activeDraftTree ?? string.Empty,
                            ["draftRequiresRestart"] = draftRequiresRestart ? "true" : "false",
                            ["checkpointCount"] = (activeDraftTree != null && activeDraftTree.Length == 32
                                ? SceneCheckpoints.GetTreePackets(activeDraftTree).Count
                                : 0).ToString(CultureInfo.InvariantCulture)
                        });
                    }

                    default:
                        return StudioResult(command, false, "CapabilityMismatch", "Unsupported studio command: " + command.CommandId);
                }
            }
            catch (Exception ex)
            {
                return StudioResult(command, false, "PreconditionFailed", ex.Message);
            }
        }

        private AutomationResultEnvelope ExecuteStudioEditInputs(AutomationCommandEnvelope command, string clientKey)
        {
            var op = command.Arguments["operation"];
            var doc = GridAny().V2Document
                ?? throw new InvalidOperationException("当前没有有效的 v2 Movie。");
            var timelineEditor = new MovieV2TimelineEditor();

            SuppressBulkGridSelection = true;
            try
            {
                switch (op)
                {
                    case "paint":
                    {
                        if (!command.Arguments.TryGetValue("startFrame", out var sText)
                            || !command.Arguments.TryGetValue("action", out var action)
                            || !command.Arguments.TryGetValue("held", out var heldText))
                            throw new InvalidOperationException("paint 需要 startFrame、action 和 held。");
                        var start = long.Parse(sText, CultureInfo.InvariantCulture);
                        var end = command.Arguments.TryGetValue("endFrame", out var eText)
                            ? long.Parse(eText, CultureInfo.InvariantCulture)
                            : start;
                        var total = InputGridEditor.Count(doc);
                        var maxFrame = Math.Max(start, end);
                        if (maxFrame >= total)
                            AppendGridBlankFrames(maxFrame + 1, withinGridOperation: true);
                        PaintGridCore(start, end, action, heldText == "true", withinGridOperation: true);
                        break;
                    }

                    case "set_runs":
                    {
                        if (!command.Arguments.TryGetValue("runs", out var runsJson))
                            throw new InvalidOperationException("set_runs 需要 runs JSON 数组。");
                        using var parsedJson = JsonDocument.Parse(runsJson);
                        if (parsedJson.RootElement.ValueKind != JsonValueKind.Array)
                            throw new InvalidOperationException("runs 必须是 JSON 数组。");
                        var items = parsedJson.RootElement.EnumerateArray().ToArray();
                        if (items.Length == 0)
                            throw new InvalidOperationException("runs 不能为空。");

                        if (items[0].TryGetProperty("startFrame", out _))
                        {
                            foreach (var item in items)
                            {
                                var start = item.GetProperty("startFrame").GetInt64();
                                var end = item.TryGetProperty("endFrame", out var endEl) ? endEl.GetInt64() : start;
                                var held = !item.TryGetProperty("held", out var heldEl) || heldEl.GetBoolean();
                                var total = InputGridEditor.Count(GridAny().V2Document!);
                                if (Math.Max(start, end) >= total)
                                    AppendGridBlankFrames(Math.Max(start, end) + 1, withinGridOperation: true);
                                if (item.TryGetProperty("actions", out var actionsEl) && actionsEl.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var act in actionsEl.EnumerateArray())
                                        PaintGridCore(start, end, act.GetString()!, held, withinGridOperation: true);
                                }
                                else if (item.TryGetProperty("action", out var actEl))
                                {
                                    PaintGridCore(start, end, actEl.GetString()!, held, withinGridOperation: true);
                                }
                                else
                                {
                                    throw new InvalidOperationException("每个区间项需要 action 或 actions。");
                                }
                            }
                        }
                        else
                        {
                            var start = command.Arguments.TryGetValue("startFrame", out var sText)
                                ? long.Parse(sText, CultureInfo.InvariantCulture)
                                : 0L;
                            var defaultFps = doc.Runs.FirstOrDefault()?.FramesPerSecond ?? ParseFrameRate(DefaultFrameRate);
                            var newRuns = new List<NativeFrameRun>();
                            long spanFrames = 0;
                            foreach (var item in items)
                            {
                                var frames = item.GetProperty("frames").GetInt64();
                                if (frames < 1) throw new InvalidOperationException("frames 必须 >= 1。");
                                var fps = item.TryGetProperty("fps", out var fpsEl)
                                    ? MovieFrameRate.Parse(fpsEl.GetRawText())
                                    : defaultFps;
                                int? rngSeed = item.TryGetProperty("rngSeed", out var seedEl) && seedEl.ValueKind == JsonValueKind.Number
                                    ? seedEl.GetInt32()
                                    : null;
                                var actionNames = item.TryGetProperty("actions", out var actionsEl) && actionsEl.ValueKind == JsonValueKind.Array
                                    ? actionsEl.EnumerateArray().Select(a => a.GetString()!).ToArray()
                                    : Array.Empty<string>();
                                var samples = BuildSamplesForActions(actionNames);
                                newRuns.Add(new NativeFrameRun(frames, samples, new MovieSourceSpan("<mcp-edit>", 1, 1, 1), fps, true, rngSeed));
                                spanFrames += frames;
                            }

                            var replaceCount = command.Arguments.TryGetValue("count", out var cntText)
                                ? long.Parse(cntText, CultureInfo.InvariantCulture)
                                : spanFrames;
                            var currentDoc = GridAny().V2Document!;
                            var total = InputGridEditor.Count(currentDoc);
                            if (start > total)
                            {
                                AppendGridBlankFrames(start, withinGridOperation: true);
                                currentDoc = GridAny().V2Document!;
                                total = InputGridEditor.Count(currentDoc);
                            }
                            var effectiveReplace = Math.Min(Math.Max(0, replaceCount), Math.Max(0, total - start));
                            GridStart = start.ToString(CultureInfo.InvariantCulture);
                            GridCount = spanFrames.ToString(CultureInfo.InvariantCulture);
                            EditGrid(timelineEditor.ReplaceFrames(currentDoc, start, effectiveReplace, newRuns));
                        }
                        break;
                    }

                    case "insert_frames":
                    {
                        var start = command.Arguments.TryGetValue("startFrame", out var sText)
                            ? long.Parse(sText, CultureInfo.InvariantCulture) : GridIndex();
                        var count = long.Parse(command.Arguments["count"], CultureInfo.InvariantCulture);
                        var fps = command.Arguments.TryGetValue("fps", out var fText)
                            ? MovieFrameRate.Parse(fText) : ParseFrameRate(DefaultFrameRate);
                        GridStart = start.ToString(CultureInfo.InvariantCulture);
                        GridCount = count.ToString(CultureInfo.InvariantCulture);
                        EditGrid(timelineEditor.InsertFrames(doc, start, new[]
                        {
                            new NativeFrameRun(count, Array.Empty<GameInputSample>(), new MovieSourceSpan("<blank>", 1, 1, 1), fps, true)
                        }));
                        break;
                    }

                    case "delete_frames":
                    {
                        var start = command.Arguments.TryGetValue("startFrame", out var sText)
                            ? long.Parse(sText, CultureInfo.InvariantCulture) : GridIndex();
                        var count = long.Parse(command.Arguments["count"], CultureInfo.InvariantCulture);
                        GridStart = start.ToString(CultureInfo.InvariantCulture);
                        GridCount = count.ToString(CultureInfo.InvariantCulture);
                        EditGrid(timelineEditor.DeleteFrames(doc, start, count));
                        break;
                    }

                    case "append_blank_frames":
                    {
                        var minTotal = command.Arguments.TryGetValue("minimumTotal", out var mText)
                            ? long.Parse(mText, CultureInfo.InvariantCulture)
                            : InputGridEditor.Count(doc) + (command.Arguments.TryGetValue("count", out var cText)
                                ? long.Parse(cText, CultureInfo.InvariantCulture) : 500L);
                        AppendGridBlankFrames(minTotal, withinGridOperation: true);
                        break;
                    }

                    case "set_fps":
                    {
                        var start = command.Arguments.TryGetValue("startFrame", out var sText)
                            ? long.Parse(sText, CultureInfo.InvariantCulture) : 0L;
                        var count = command.Arguments.TryGetValue("count", out var cText)
                            ? long.Parse(cText, CultureInfo.InvariantCulture) : 1L;
                        var fps = MovieFrameRate.Parse(command.Arguments["fps"]);
                        GridStart = start.ToString(CultureInfo.InvariantCulture);
                        GridCount = count.ToString(CultureInfo.InvariantCulture);
                        EditGridDocument(MovieV2RangeEditor.SetFrameRate(doc, start, count, fps), withinGridOperation: true);
                        break;
                    }

                    case "set_rng_seed":
                    {
                        var start = long.Parse(command.Arguments["startFrame"], CultureInfo.InvariantCulture);
                        var seed = command.Arguments.TryGetValue("rngSeed", out var seedText) ? seedText : string.Empty;
                        if (!TrySetGridRngSeed(start, seed, withinGridOperation: true))
                            throw new InvalidOperationException(GridStatus);
                        break;
                    }

                    case "undo":
                        RestoreGridHistory(gridUndo, gridRedo);
                        break;

                    case "redo":
                        RestoreGridHistory(gridRedo, gridUndo);
                        break;

                    default:
                        throw new InvalidOperationException("Unsupported studioEditInputs operation: " + op);
                }
            }
            finally
            {
                SuppressBulkGridSelection = false;
            }

            RememberExternalDraft(clientKey);
            var updatedDoc = GridAny().V2Document!;
            var updatedTotal = InputGridEditor.Count(updatedDoc);
            return StudioResult(command, true, "Ok", GridStatus, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["operation"] = op,
                ["movieId"] = new MovieV2Codec().ComputeMovieId(updatedDoc),
                ["totalFrames"] = updatedTotal.ToString(CultureInfo.InvariantCulture),
                ["earliestGridEdit"] = earliestGridEdit == long.MaxValue ? "-1" : earliestGridEdit.ToString(CultureInfo.InvariantCulture),
                ["draftRequiresRestart"] = draftRequiresRestart ? "true" : "false",
                ["gridHasUserEdits"] = gridHasUserEdits ? "true" : "false"
            });
        }

        private static IReadOnlyList<GameInputSample> BuildSamplesForActions(IEnumerable<string> actions)
        {
            var channelArrays = new Dictionary<GameInputChannel, short[]>();
            var customSamples = new List<GameInputSample>();
            foreach (var raw in actions)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var action = raw.Trim();
                if (CustomKeyInput.TryAction(action, out var key))
                {
                    if (!customSamples.Any(s => s.Values[0] == key))
                        customSamples.Add(CustomKeyInput.Sample(key, true));
                    continue;
                }

                var matched = false;
                foreach (var channel in new[] { GameInputChannel.Hero, GameInputChannel.PreMenu, GameInputChannel.Binder })
                {
                    var index = MovieV2RangeEditor.ActionIndex(channel, action);
                    if (index < 0) continue;
                    matched = true;
                    if (!channelArrays.TryGetValue(channel, out var arr))
                    {
                        arr = new short[MovieProtocolV2.ExpectedValueCount(channel)];
                        channelArrays[channel] = arr;
                    }
                    arr[index] = short.MaxValue;
                }

                if (!matched)
                    throw new InvalidOperationException("未知输入动作：" + action);
            }

            var result = new List<GameInputSample>();
            foreach (var channel in new[] { GameInputChannel.Hero, GameInputChannel.PreMenu, GameInputChannel.Binder })
            {
                if (channelArrays.TryGetValue(channel, out var arr))
                    result.Add(new GameInputSample(channel, arr, null));
            }
            result.AddRange(customSamples);
            return result;
        }

        private string MergeStudioSnapshot(string runtimeText, long frame)
        {
            var runtime = movieEditor.ValidateAny(runtimeText).V2Document
                ?? throw new InvalidDataException("Runtime Movie is invalid.");
            var draft = movieEditor.ValidateAny(MovieText).V2Document;
            if (draft == null) return runtimeText;
            var total = InputGridEditor.Count(draft);
            var runs = (gridHasUserEdits && earliestGridEdit < frame ? draft.Runs
                : MovieV2Prefix.Take(runtime, frame).Runs.Concat(total > frame
                    ? SliceV2(draft, frame, total - frame).Runs
                    : Array.Empty<NativeFrameRun>())).ToArray();
            // Keep keys added in Studio; the runtime header only knows keys from its last update.
            return new MovieV2Codec().WriteCanonical(new MovieV2Document(draft.SourceName,
                CustomKeyInput.WithUsedKeys(runtime.Header, draft.Header.CustomKeys, runs), runs));
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
