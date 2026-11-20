using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using HollowKnightTAS.Companion.Automation;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.ViewModels;
using HollowKnightTAS.Core.Automation;
using HollowKnightTAS.Core.Input;
using HollowKnightTAS.Core.Movie;

// --video-export --ffmpeg=<absolute ffmpeg.exe> --headless --scenario-output=<new directory>
// Optional after the title checks: --video-movie=<existing v2 fixture>
// --video-range-start=1400 --video-range-end=3354 (both are completed Movie boundaries).
// All controls below are the real Studio commands. Only the modal file picker is replaced.
internal static partial class StudioScenarioHarness
{
    static readonly List<object> videoEvidence = new();

    static void VideoEvidence(string scenario, object details)
    {
        var entry = new { scenario, details };
        videoEvidence.Add(entry);
        var json = JsonSerializer.Serialize(entry);
        Log("VIDEO " + json);
        Console.WriteLine("VIDEO " + json);
    }

    static string VideoHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    static long MovieFrames(string movie) => TimelineTree.Parse(movie).Runs.Sum(r => r.RepeatCount);
    static long Number(IReadOnlyDictionary<string, string> status, string key)
        => long.Parse(status[key], CultureInfo.InvariantCulture);

    static void Execute(ICommand command)
    {
        Require(command.CanExecute(null), "synchronous command enabled");
        command.Execute(null);
    }

    static async Task<Dictionary<string, string>> VideoRuntimeState(MainViewModel vm)
    {
        var result = await Field<AutomationBroker>(vm, "automationBroker").ExecuteHumanAsync(
            AutomationCommandIds.FullRunStatus, AutomationScope.ObserveStatus,
            null, string.Empty, null, CancellationToken.None);
        if (!result.Success) throw new InvalidOperationException("Runtime status failed: " + result.Detail);
        // Exclude connection/session metadata; retain the actual movie and native/video boundaries.
        return result.Data.Where(p => p.Key.StartsWith("videoExport.", StringComparison.Ordinal)
            || p.Key is "mode" or "movieFrame" or "nativeFrame" or "paused" or "faultCode" or "skippedLoadFrames"
                or "sceneName" or "mismatchCount" or "error" or "heroHealth" or "bossSceneEntered"
                or "bossDeathObserved" or "bossesDeadObserved" or "bossSceneCompleteObserved"
                or "bossDeathFrame" or "bossSceneEntryMovieFrame")
            .ToDictionary(p => p.Key, p => p.Value);
    }

    static async Task<(long Frame, string Movie)> VideoRuntimeMovie(MainViewModel vm, FullRunMovieCoordinator movies)
    {
        var result = await Field<AutomationBroker>(vm, "automationBroker").ExecuteHumanAsync(
            AutomationCommandIds.FullRunSnapshot, AutomationScope.MovieRead,
            null, "Paused", null, CancellationToken.None);
        if (!result.Success) throw new InvalidOperationException("Runtime snapshot failed: " + result.Detail);
        var path = Path.GetFullPath(result.Data["path"]);
        Require(path.StartsWith(Path.GetFullPath(movies.ShadowRoot) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) && new FileInfo(path).Length <= MovieProtocolV2.MaximumSourceUtf8Bytes,
            "Runtime movie snapshot remains inside owned shadow storage");
        return (long.Parse(result.Data["movieFrame"], CultureInfo.InvariantCulture), await File.ReadAllTextAsync(path));
    }

    static async Task<Dictionary<string, string>> WaitForVideoState(MainViewModel vm,
        Func<Dictionary<string, string>, bool> ready, string detail, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var state = await VideoRuntimeState(vm);
            if (ready(state)) return state;
            if (state.TryGetValue("videoExport.state", out var phase) && phase == "Failed")
                throw new InvalidOperationException(detail + ": " + JsonSerializer.Serialize(state));
            if (DateTime.UtcNow > deadline) throw new TimeoutException(detail + ": " + JsonSerializer.Serialize(state));
            await Task.Delay(100);
        }
    }

    static async Task ChooseVideoNode(MainViewModel vm, int id)
    {
        vm.SelectTimelineNode(id);
        await Until(() => vm.IsInputGridInteractive && vm.SelectedTimelineNode?.Id == id, "select video endpoint " + id);
    }

    static void AppendNeutralDraft(MainViewModel vm, int frames)
    {
        var before = MovieFrames(vm.MovieText);
        vm.GridStart = before.ToString(CultureInfo.InvariantCulture);
        vm.GridInsertCount = frames.ToString(CultureInfo.InvariantCulture);
        Execute(vm.InsertGridCommand);
        Require(MovieFrames(vm.MovieText) == before + frames, "neutral future draft appended");
    }

    static Dictionary<string, string> VideoHeaderIdentity(MovieV2Header header) => new()
    {
        [nameof(header.Format)] = header.Format,
        [nameof(header.Version)] = header.Version.ToString(CultureInfo.InvariantCulture),
        [nameof(header.TickUnit)] = header.TickUnit,
        [nameof(header.GameVersion)] = header.GameVersion,
        [nameof(header.ApiVersion)] = header.ApiVersion,
        [nameof(header.ModVersion)] = header.ModVersion,
        [nameof(header.NativeProfileId)] = header.NativeProfileId,
        [nameof(header.ActionSchemaId)] = header.ActionSchemaId,
        [nameof(header.MouseEnabled)] = header.MouseEnabled.ToString(),
        [nameof(header.ViewportWidth)] = header.ViewportWidth.ToString(CultureInfo.InvariantCulture),
        [nameof(header.ViewportHeight)] = header.ViewportHeight.ToString(CultureInfo.InvariantCulture)
    };

    static async Task RunBattleVideoAsync(MainViewModel vm, StartupBootController boot,
        FullRunMovieCoordinator movies, MovieV2Header observedHeader, string fixturePath,
        long start, long end, Action<string> chooseOutput)
    {
        fixturePath = Path.GetFullPath(fixturePath);
        Require(new FileInfo(fixturePath).Length <= MovieProtocolV2.MaximumSourceUtf8Bytes,
            "optional battle fixture is within the Movie source size limit");
        var originalBytes = await File.ReadAllBytesAsync(fixturePath);
        var originalSha256 = Convert.ToHexString(SHA256.HashData(originalBytes));
        var codec = new MovieV2Codec();
        var parsed = codec.Parse(new StringReader(new UTF8Encoding(false, true).GetString(originalBytes).TrimStart('\uFEFF')), fixturePath);
        Require(parsed.Success && parsed.Document != null, "optional battle fixture parses as v2");
        var source = parsed.Document!;
        var report = new MovieV2Validator().Validate(source, MovieV2ValidationContext.CreateDefault());
        Require(report.Success, "optional battle fixture passes normal Movie validation");
        var frames = source.Runs.Sum(r => r.RepeatCount);
        Require(start > 0 && start < end && end < frames,
            "optional battle range is nonempty and retains future input; set --video-range-start/end if needed");
        Require(source.Runs.All(r => r.FramesPerSecond == 50), "optional battle Movie is constant 50 fps");

        var sourceIdentity = VideoHeaderIdentity(source.Header);
        var observedIdentity = VideoHeaderIdentity(observedHeader);
        var differences = sourceIdentity.Where(p => observedIdentity[p.Key] != p.Value)
            .Select(p => new { field = p.Key, fixture = p.Value, observed = observedIdentity[p.Key] }).ToArray();
        var environmentEvidence = new
        {
            fixturePath, originalSha256, frames,
            originalHeader = source.Header, observedHeader,
            permittedReplacement = nameof(MovieV2Header.EnvironmentSha256), identityDifferences = differences,
            startMovieFrame = start, endMovieFrame = end
        };
        await File.WriteAllTextAsync(Path.Combine(output, "battle-environment.json"),
            JsonSerializer.Serialize(environmentEvidence, new JsonSerializerOptions { WriteIndented = true }));
        VideoEvidence("battle-environment", environmentEvidence);
        Require(differences.Length == 0, "battle fixture identity must match the observed startup header: "
            + JsonSerializer.Serialize(differences));

        // Copy the source's complete header and runs. Only the explicitly authorized environment identity changes.
        var old = source.Header;
        var candidateHeader = new MovieV2Header(old.GameVersion, old.ApiVersion, old.ModVersion,
            old.NativeProfileId, old.ActionSchemaId, old.MouseEnabled, observedHeader.EnvironmentSha256,
            old.ViewportWidth, old.ViewportHeight);
        var candidate = codec.WriteCanonical(new MovieV2Document(source.SourceName, candidateHeader, source.Runs));
        var originalCanonical = codec.WriteCanonical(source);
        Require(candidate.Substring(candidate.IndexOf('\n')) == originalCanonical.Substring(originalCanonical.IndexOf('\n')),
            "candidate preserves every canonical input run and changes no RNG profile or timing");
        var candidatePath = Path.GetFullPath(Path.Combine(output, "battle-candidate.hktas"));
        Require(!string.Equals(candidatePath, fixturePath, StringComparison.OrdinalIgnoreCase) && !File.Exists(candidatePath),
            "battle candidate is a new file separate from the original fixture");
        using (var file = new FileStream(candidatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(file, new UTF8Encoding(false))) await writer.WriteAsync(candidate);
        await vm.OpenMovieFileAsync(candidatePath);
        VideoEvidence("battle-candidate", new { path = candidatePath, candidateSha256 = VideoHash(candidate),
            originalEnvironmentSha256 = old.EnvironmentSha256, candidateEnvironmentSha256 = candidateHeader.EnvironmentSha256,
            frames, inputRunsUnchanged = true, otherHeaderFieldsUnchanged = true });

        var fullOutput = Path.Combine(output, "battle-complete.mp4");
        chooseOutput(fullOutput);
        await Command(vm.StartVideoExportCommand).WaitAsync(TimeSpan.FromMinutes(8));
        var full = await VideoRuntimeState(vm);
        Require(full.GetValueOrDefault("videoExport.state") == "Completed" && File.Exists(fullOutput)
            && Number(full, "videoExport.startMovieFrame") == 0 && Number(full, "videoExport.endMovieFrame") == frames
            && Number(full, "movieFrame") == frames && full.GetValueOrDefault("mode") == "Completed"
            && Number(full, "faultCode") == 0 && Number(full, "mismatchCount") == 0,
            "real battle full export reaches the complete sequence without Runtime faults or input mismatches");
        Require(vm.MovieText == candidate, "real battle full export retains the complete candidate draft");
        VideoEvidence("battle-complete", new { output = fullOutput, state = full, movieFrames = frames,
            candidateSha256 = VideoHash(candidate) });

        await vm.FrameMenuAsync("rebuild", start);
        AtFrame(vm, boot, start, "real battle range start reached by normal replay");
        await vm.FrameMenuAsync("save", -1);
        var a = vm.SelectedTimelineNode!;
        Require(a.Frame == start && !a.IsUnverified && !a.IsBranchTip && a.Movie == candidate,
            "battle start node is observed and retains the complete sequence");
        var startState = await VideoRuntimeState(vm);
        await vm.FrameMenuAsync("seek", end);
        AtFrame(vm, boot, end, "real battle range end reached by normal replay");
        await vm.FrameMenuAsync("save", -1);
        var b = vm.SelectedTimelineNode!;
        Require(b.Frame == end && !b.IsUnverified && !b.IsBranchTip && b.Movie == candidate
            && vm.SelectedTimelineTree!.PathTo(b.Id).Any(n => n.Id == a.Id),
            "battle end node is an observed descendant and retains future sequence input");
        var endState = await VideoRuntimeState(vm);
        Execute(vm.ClearVideoRangeCommand);
        await ChooseVideoNode(vm, a.Id); Execute(vm.MarkVideoStartCommand);
        await ChooseVideoNode(vm, b.Id); Execute(vm.MarkVideoEndCommand);
        var rangeOutput = Path.Combine(output, "battle-range.mp4");
        chooseOutput(rangeOutput);
        await Command(vm.ExportTimelineVideoCommand).WaitAsync(TimeSpan.FromMinutes(8));
        var range = await VideoRuntimeState(vm);
        var loaded = await VideoRuntimeMovie(vm, movies);
        Require(range.GetValueOrDefault("videoExport.state") == "Completed" && File.Exists(rangeOutput)
            && Number(range, "videoExport.startMovieFrame") == start && Number(range, "videoExport.endMovieFrame") == end
            && Number(range, "movieFrame") == end && loaded.Frame == end
            && Number(range, "faultCode") == 0 && Number(range, "mismatchCount") == 0,
            "real battle interval export ends exactly at the selected descendant without Runtime faults or input mismatches");
        Require(loaded.Movie == candidate && vm.MovieText == candidate && MovieFrames(loaded.Movie) == frames,
            "battle range retains the full loaded Movie and editor draft after the selected end");
        Require(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fixturePath))) == originalSha256,
            "original battle fixture bytes remain unchanged");
        VideoEvidence("battle-range", new { output = rangeOutput, state = range, startState, endState,
            startNode = a.Id, endNode = b.Id, loadedMovieFrames = frames,
            loadedMovieSha256 = VideoHash(loaded.Movie), originalFixtureUnchanged = true });
    }

    static async Task RunVideoExportAsync(string[] args)
    {
        var vm = (MainViewModel)app.MainWindow.DataContext;
        var boot = Field<StartupBootController>(app, "startupBoot");
        var movies = Field<FullRunMovieCoordinator>(app, "fullRunMovies");
        var pickerField = typeof(MainViewModel).GetField("videoExportFilePicker", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalPicker = pickerField.GetValue(vm);
        var ffmpeg = Path.GetFullPath(args.Single(a => a.StartsWith("--ffmpeg=", StringComparison.Ordinal)).Split('=', 2)[1]);
        Require(File.Exists(ffmpeg), "explicit FFmpeg executable exists");
        var target = "";
        var pickerCalls = 0;
        pickerField.SetValue(vm, (Func<(string Ffmpeg, string Output)?>)(() =>
        {
            pickerCalls++;
            Require(target.Length > 0 && !File.Exists(target), "picker returns a new MP4 destination");
            return (ffmpeg, target);
        }));
        var originalFps = vm.DefaultFrameRate;
        var originalMouseEnabled = vm.GameMouseEnabled;
        var battleFixture = args.SingleOrDefault(a => a.StartsWith("--video-movie=", StringComparison.Ordinal));
        var success = false;
        string? failure = null;
        var storePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HollowKnightTAS", "studio-frame-saves", "timelines.json");
        vm.InitializeWorldlines();
        var previous = new StudioTimelineStore(storePath).Library;
        // Empty frame-zero placeholders can be used by Studio's normal New command; preserve every existing history.
        var previousTrees = previous.Trees.Where(t => t.Nodes.Count > 1 || t.Nodes[0].Movie.Length > 0)
            .ToDictionary(t => t.Id, t => VideoHash(JsonSerializer.Serialize(t)));
        var previousSlots = JsonSerializer.Serialize(previous.QuickSlots);
        var previousTreeIds = previous.Trees.Select(t => t.Id).ToArray();
        try
        {
            if (vm.DefaultFrameRate != "50") vm.DefaultFrameRate = "50";
            if (battleFixture != null)
            {
                var fixturePath = Path.GetFullPath(battleFixture.Split('=', 2)[1]);
                Require(new FileInfo(fixturePath).Length <= MovieProtocolV2.MaximumSourceUtf8Bytes,
                    "optional fixture fits the source limit before reading its mouse setting");
                var fixtureMouseEnabled = TimelineTree.Parse(await File.ReadAllTextAsync(fixturePath)).Header.MouseEnabled;
                if (vm.GameMouseEnabled != fixtureMouseEnabled) vm.GameMouseEnabled = fixtureMouseEnabled;
                Require(vm.GameMouseEnabled == fixtureMouseEnabled, "initial recording uses the optional fixture mouse setting");
                VideoEvidence("mouse-recording-setting", new { originalMouseEnabled, fixtureMouseEnabled });
            }
            await Field<Func<string, Task>>(vm, "launchGame")(
                args.FirstOrDefault(a => a.StartsWith("--game=", StringComparison.Ordinal))?.Split('=', 2)[1]
                ?? @"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe");
            Require(boot.IsWaiting && boot.NativeCompletedFrames == 0, "video scenario starts at protected native zero");
            Execute(vm.NewFullRunMovieCommand);
            await Command(vm.StepCommand);
            boot.Refresh();
            Log($"BOOTSTRAP native={boot.NativeCompletedFrames} waiting={boot.IsWaiting} fault={boot.FullRunFaultCode} status={vm.Status}");
            try { await Until(() => vm.SelectedSession?.Client.IsConnected == true, "video Runtime connected", 120); }
            catch
            {
                var registry = Field<SessionRegistry>(app, "sessions");
                Log($"REGISTRATION connected={registry.ConnectedCount} error={registry.LastRegistrationError} status={vm.Status}");
                throw;
            }
            await vm.PollInputGridProgressAsync();
            var initial = await VideoRuntimeMovie(vm, movies);
            var header = TimelineTree.Parse(initial.Movie).Header;
            Require(header.EnvironmentSha256 != "none", "neutral fixture uses the installed Runtime environment header");
            async Task OpenNeutral(int frames, string name)
            {
                var variableRate = args.Contains("--variable-video");
                int Rate(int i) => i % 120 == 119 ? 1 : i % 120 < 24 ? 50
                    : i % 120 < 48 ? 60 : i % 120 < 72 ? 120 : i % 120 < 96 ? 1000 : 59;
                var movie = new MovieV2Codec().WriteCanonical(new MovieV2Document("<video-scenario>", header,
                    variableRate ? Enumerable.Range(0, frames).Select(i => new NativeFrameRun(1,
                        Array.Empty<GameInputSample>(), new MovieSourceSpan("<video-scenario>", 1, 1, 1), Rate(i), true))
                    : new[] { new NativeFrameRun(frames, Array.Empty<GameInputSample>(),
                        new MovieSourceSpan("<video-scenario>", 1, 1, 1), 50, true) }));
                var path = Path.Combine(output, name + ".hktas");
                await File.WriteAllTextAsync(path, movie);
                await vm.OpenMovieFileAsync(path);
                Require(MovieFrames(vm.MovieText) == frames, "opened complete neutral title movie " + frames);
            }

            // Complete sequence: the current game may be elsewhere, but export must begin before Movie input zero.
            await OpenNeutral(120, "full-title");
            target = Path.Combine(output, "full-title.mp4");
            var fullDraft = vm.MovieText;
            await Command(vm.StartVideoExportCommand).WaitAsync(TimeSpan.FromMinutes(2));
            var full = await VideoRuntimeState(vm);
            Require(full.GetValueOrDefault("videoExport.state") == "Completed" && File.Exists(target), "full export publishes MP4");
            Require(Number(full, "videoExport.startMovieFrame") == 0 && Number(full, "videoExport.endMovieFrame") == 120
                && Number(full, "movieFrame") == 120 && Number(full, "videoExport.frames") >= 120
                && full.GetValueOrDefault("videoExport.timing") == "per-frame-game-clock",
                "full export reaches the complete 0 to 120 interval using the game clock");
            Require(vm.MovieText == fullDraft && !vm.IsVideoExportBusy, "full export retains the editor draft and unlocks editing");
            VideoEvidence("complete-movie", new { state = full, output = target, movieSha256 = VideoHash(fullDraft) });

            // Ordinary Play must restart the editor draft and consume input instead of remaining paused at zero.
            var exportedPid = Field<Process>(app, "startupGame").Id;
            await Command(vm.TogglePauseCommand);
            var playing = await WaitForVideoState(vm, s => s.TryGetValue("movieFrame", out var frame)
                && long.Parse(frame, CultureInfo.InvariantCulture) > 0, "ordinary Play advances after export");
            if (!boot.IsWaiting) await Command(vm.TogglePauseCommand);
            Require(Field<Process>(app, "startupGame").Id != exportedPid, "ordinary Play cold-restarts the editor draft after export");
            VideoEvidence("ordinary-play-after-export", new { priorPid = exportedPid, currentPid = Field<Process>(app, "startupGame").Id, state = playing });

            // Both endpoints are observed normal replay boundaries; B retains unused future Movie input.
            await vm.FrameMenuAsync("rebuild", 20);
            AtFrame(vm, boot, 20, "range endpoint A reached normally");
            AppendNeutralDraft(vm, 40); // 160 total, including future draft.
            await vm.FrameMenuAsync("save", -1);
            var a = vm.SelectedTimelineNode!;
            Require(a.Frame == 20 && !a.IsBranchTip && !a.IsUnverified, "A is an observed explicit node");
            await vm.FrameMenuAsync("seek", 60);
            AtFrame(vm, boot, 60, "range endpoint B reached normally");
            AppendNeutralDraft(vm, 10); // B stores 170 frames; its export ends at 60.
            await vm.FrameMenuAsync("save", -1);
            var b = vm.SelectedTimelineNode!;
            Require(b.Frame == 60 && !b.IsBranchTip && !b.IsUnverified
                && vm.SelectedTimelineTree!.PathTo(b.Id).Any(n => n.Id == a.Id), "B is A's observed descendant");
            var rangeMovie = b.Movie;
            AppendNeutralDraft(vm, 10); // Unsaved editor draft is now 180, deliberately different from B's 170.
            var draft = vm.MovieText;
            var undo = Field<Stack<string>>(vm, "gridUndo").ToArray();
            var redo = Field<Stack<string>>(vm, "gridRedo").ToArray();
            var hadEdits = Field<bool>(vm, "gridHasUserEdits");
            Require(hadEdits && undo.Length > 0 && MovieFrames(draft) > MovieFrames(rangeMovie), "range test includes future draft and Undo history");
            await ChooseVideoNode(vm, b.Id);
            Execute(vm.MarkVideoStartCommand);
            await ChooseVideoNode(vm, a.Id);
            Execute(vm.MarkVideoEndCommand);
            Require(vm.VideoStartNodeId == a.Id && vm.VideoEndNodeId == b.Id, "reverse endpoint selection normalizes to ancestor order");
            target = Path.Combine(output, "timeline-range.mp4");
            await Command(vm.ExportTimelineVideoCommand).WaitAsync(TimeSpan.FromMinutes(2));
            var range = await VideoRuntimeState(vm);
            var loaded = await VideoRuntimeMovie(vm, movies);
            Require(range.GetValueOrDefault("videoExport.state") == "Completed" && File.Exists(target), "timeline range publishes MP4");
            Require(Number(range, "videoExport.startMovieFrame") == 20 && Number(range, "videoExport.endMovieFrame") == 60
                && Number(range, "movieFrame") == 60 && loaded.Frame == 60 && Number(range, "videoExport.frames") >= 40,
                "range capture stops at B rather than its future draft");
            Require(loaded.Movie == rangeMovie && MovieFrames(loaded.Movie) == 170,
                "Runtime retains the full 170-frame descendant Movie");
            Require(vm.MovieText == draft && Field<bool>(vm, "gridHasUserEdits") == hadEdits
                && Field<Stack<string>>(vm, "gridUndo").SequenceEqual(undo)
                && Field<Stack<string>>(vm, "gridRedo").SequenceEqual(redo), "range export preserves unsaved 180-frame draft and Undo/Redo stacks");
            VideoEvidence("timeline-range-reversed", new { state = range, output = target, startNode = a.Id, endNode = b.Id,
                loadedFrames = MovieFrames(loaded.Movie), loadedMovieSha256 = VideoHash(loaded.Movie),
                editorFrames = MovieFrames(vm.MovieText), editorMovieSha256 = VideoHash(vm.MovieText), undoEntries = undo.Length });

            // A harmless title-screen DreamNail input creates a real sibling branch; no debug state is written.
            vm.GridStart = "25"; vm.GridCount = "1"; vm.GridAction = TasAction.DreamNail;
            Execute(vm.ToggleGridCommand);
            await vm.FrameMenuAsync("rebuild", 60);
            AtFrame(vm, boot, 60, "sibling branch reached normally");
            await vm.FrameMenuAsync("save", -1);
            var sibling = vm.SelectedTimelineNode!;
            Require(!sibling.IsUnverified && sibling.ParentId == a.Id && b.ParentId == a.Id, "negative case has two observed sibling nodes");
            Execute(vm.ClearVideoRangeCommand);
            await ChooseVideoNode(vm, b.Id); Execute(vm.MarkVideoStartCommand);
            await ChooseVideoNode(vm, sibling.Id); Execute(vm.MarkVideoEndCommand);
            var beforePid = Field<Process>(app, "startupGame").Id;
            var beforeNative = boot.NativeCompletedFrames;
            var beforePicker = pickerCalls;
            target = Path.Combine(output, "invalid-siblings.mp4");
            await Command(vm.ExportTimelineVideoCommand);
            Require(!vm.IsVideoExportBusy && Field<Process>(app, "startupGame").Id == beforePid
                && boot.NativeCompletedFrames == beforeNative && pickerCalls == beforePicker && !File.Exists(target)
                && vm.VideoExportStatus.Contains("祖先", StringComparison.Ordinal), "unrelated endpoints are rejected before picker or restart");
            VideoEvidence("sibling-rejected", new { first = b.Id, second = sibling.Id, pid = beforePid, nativeFrame = beforeNative, status = vm.VideoExportStatus });

            // Cancel when Studio announces preparation, before any capture is started.
            var preparationCancellation = false;
            PropertyChangedEventHandler prepareHandler = (_, e) =>
            {
                if (e.PropertyName != nameof(vm.VideoExportStatus) || preparationCancellation
                    || !vm.VideoExportStatus.StartsWith("准备导出", StringComparison.Ordinal)) return;
                preparationCancellation = true;
                Require(!Field<bool>(vm, "videoCaptureStarted"), "preparation cancellation precedes capture");
                Execute(vm.CancelVideoExportCommand);
            };
            target = Path.Combine(output, "prepare-cancel.mp4");
            vm.PropertyChanged += prepareHandler;
            try { await Command(vm.StartVideoExportCommand).WaitAsync(TimeSpan.FromMinutes(2)); }
            finally { vm.PropertyChanged -= prepareHandler; }
            Require(preparationCancellation && !File.Exists(target) && !vm.IsVideoExportBusy
                && vm.VideoExportStatus.Contains("已取消", StringComparison.Ordinal), "preparation cancellation publishes no MP4");
            VideoEvidence("preparation-cancel", new { output = target, fileExists = File.Exists(target), status = vm.VideoExportStatus });

            // Pause, Continue in the same process, pause again, and cancel an active capture.
            await OpenNeutral(1000, "capture-cancel");
            target = Path.Combine(output, "capture-cancel.mp4");
            var captureTask = Command(vm.StartVideoExportCommand);
            await Until(() => Field<bool>(vm, "videoCaptureStarted") || captureTask.IsCompleted,
                "capture started for pause/cancel", 100);
            Require(!captureTask.IsCompleted && Field<bool>(vm, "videoCaptureStarted"), "capture is still active");
            await WaitForVideoState(vm, s => s.GetValueOrDefault("videoExport.state") == "Capturing"
                && Number(s, "videoExport.frames") >= 3, "video has sampled real frames");
            await Command(vm.TogglePauseCommand);
            var paused = await VideoRuntimeState(vm);
            Require(boot.IsWaiting && paused.GetValueOrDefault("videoExport.state") == "Capturing", "Pause keeps capture open");
            await Task.Delay(400);
            var stillPaused = await VideoRuntimeState(vm);
            Require(Number(paused, "movieFrame") == Number(stillPaused, "movieFrame")
                && Number(paused, "nativeFrame") == Number(stillPaused, "nativeFrame")
                && Number(paused, "videoExport.frames") == Number(stillPaused, "videoExport.frames"),
                "paused capture adds neither movie nor native nor video frames");
            var capturePid = Field<Process>(app, "startupGame").Id;
            await Command(vm.TogglePauseCommand);
            var resumed = await WaitForVideoState(vm, s => s.GetValueOrDefault("videoExport.state") == "Capturing"
                && Number(s, "videoExport.frames") >= Number(stillPaused, "videoExport.frames") + 3
                && Number(s, "movieFrame") > Number(stillPaused, "movieFrame"),
                "Continue advances the same video capture");
            await Command(vm.TogglePauseCommand);
            var pausedAgain = await VideoRuntimeState(vm);
            VideoEvidence("capture-resume-boundary", new { paused, stillPaused, resumed, pausedAgain,
                capturePid, currentPid = Field<Process>(app, "startupGame").Id, waiting = boot.IsWaiting });
            Require(Field<Process>(app, "startupGame").Id == capturePid && boot.IsWaiting
                && pausedAgain.GetValueOrDefault("videoExport.state") == "Capturing"
                && Number(pausedAgain, "movieFrame") > Number(stillPaused, "movieFrame")
                && Number(pausedAgain, "nativeFrame") > Number(stillPaused, "nativeFrame")
                && Number(pausedAgain, "videoExport.frames") > Number(stillPaused, "videoExport.frames")
                && pausedAgain["videoExport.operationId"] == stillPaused["videoExport.operationId"],
                "Continue advances the existing capture without a game restart or new export operation");
            await Command(vm.CancelVideoExportCommand);
            await captureTask.WaitAsync(TimeSpan.FromMinutes(1));
            var cancelled = await VideoRuntimeState(vm);
            Require(cancelled.GetValueOrDefault("videoExport.state") == "Cancelled" && !File.Exists(target)
                && !vm.IsVideoExportBusy, "capture cancellation publishes no MP4");
            VideoEvidence("capture-pause-continue-cancel", new { paused, stillPaused, resumed, pausedAgain, cancelled,
                capturePid, output = target, fileExists = File.Exists(target) });

            if (battleFixture != null)
            {
                long ReadBoundary(string name, long fallback) => args.SingleOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal)) is string value
                    ? long.Parse(value.Split('=', 2)[1], CultureInfo.InvariantCulture) : fallback;
                await RunBattleVideoAsync(vm, boot, movies, header, battleFixture.Split('=', 2)[1],
                    ReadBoundary("--video-range-start", 1400), ReadBoundary("--video-range-end", 3354), path => target = path);
            }

            await vm.SaveCurrentBranchAsync(closing: true);
            var current = new StudioTimelineStore(storePath).Library;
            Require(previousTreeIds.All(id => current.Trees.Any(t => t.Id == id)), "all prior timeline IDs remain");
            Require(previousTrees.All(p => current.Trees.Any(t => t.Id == p.Key && VideoHash(JsonSerializer.Serialize(t)) == p.Value))
                && JsonSerializer.Serialize(current.QuickSlots) == previousSlots, "existing timeline histories and quick slots remain unchanged");
            movies.VerifyOriginalSavesUnchanged();
            VideoEvidence("preservation", new { priorTrees = previousTreeIds.Length, priorHistories = previousTrees.Count,
                finalTrees = current.Trees.Count, originalSavesUnchanged = true });
            success = true;
        }
        catch (Exception exception) { failure = exception.ToString(); throw; }
        finally
        {
            if (vm.IsVideoExportBusy)
            {
                try { await vm.CancelVideoExportAndWaitAsync(); }
                catch (Exception cleanup) { VideoEvidence("cleanup-error", new { detail = cleanup.Message }); }
            }
            pickerField.SetValue(vm, originalPicker);
            if (vm.DefaultFrameRate != originalFps) vm.DefaultFrameRate = originalFps;
            Exception? restoreFailure = null;
            if (vm.GameMouseEnabled != originalMouseEnabled)
            {
                try
                {
                    // The normal settings setter rejects changes while a Movie is armed.
                    // End only this App-owned session through its normal Quit command before restoring the preference.
                    if (movies.IsArmed)
                    {
                        await Command(vm.QuitGameCommand);
                        await Until(() => !movies.IsArmed, "owned game exits before restoring the mouse preference");
                    }
                    vm.GameMouseEnabled = originalMouseEnabled;
                    Require(vm.GameMouseEnabled == originalMouseEnabled, "original mouse recording preference restored");
                    VideoEvidence("mouse-setting-restored", new { originalMouseEnabled });
                }
                catch (Exception exception)
                {
                    restoreFailure = exception;
                    success = false;
                    VideoEvidence("mouse-setting-restore-failed", new { detail = exception.Message });
                }
            }
            var report = new { success, failure, scenarios = videoEvidence, pickerCalls,
                scope = "real App, VM commands, protected launcher and Runtime; modal file picker replaced only" };
            await File.WriteAllTextAsync(Path.Combine(output, "video-export-report.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            if (restoreFailure != null && failure == null) throw new InvalidOperationException("Mouse preference restoration failed.", restoreFailure);
        }
    }
}
