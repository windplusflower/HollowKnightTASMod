using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using HollowKnightTAS.Companion.Automation;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.ViewModels;
using HollowKnightTAS.Core.Automation;
using HollowKnightTAS.Core.Movie;

internal static partial class StudioScenarioHarness
{
    static async Task RunReplayDivergenceAsync(string[] args)
    {
        var vm = (MainViewModel)app.MainWindow.DataContext;
        var boot = Field<StartupBootController>(app, "startupBoot");
        var movies = Field<FullRunMovieCoordinator>(app, "fullRunMovies");
        void Set(string name, object value) => typeof(MainViewModel).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);
        Set("worldlines", new StudioTimelineStore(Path.Combine(output, "timelines.json")));
        Set("initialSaveCacheRoot", Path.Combine(output, "initial-saves"));
        Set("activeSequenceDirectory", output);
        var source = args.Single(a => a.StartsWith("--divergence-sequence=")).Split('=', 2)[1];
        var copy = Path.Combine(output, "input.hktaspack");
        var initialSaves = args.SingleOrDefault(a => a.StartsWith("--divergence-initial-saves="))?.Split('=', 2)[1];
        if (initialSaves == null) File.Copy(source, copy, false);
        else
        {
            var snapshot = new InitialSaveSnapshot(Directory.GetFiles(initialSaves, "user*")
                .Select(path => new KeyValuePair<string, byte[]>(Path.GetFileName(path), File.ReadAllBytes(path))));
            await SequencePackage.WriteAsync(copy, File.ReadAllText(source), snapshot);
        }
        await vm.OpenMovieFileAsync(copy);
        var originalMovie = vm.MovieText;
        await File.WriteAllTextAsync(Path.Combine(output, "original-movie.hktas"), originalMovie);
        await Field<Func<string, Task>>(vm, "launchGame")(@"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe");
        if (args.Contains("--divergence-video-fault"))
        {
            var picker = typeof(MainViewModel).GetField("videoExportFilePicker", BindingFlags.Instance | BindingFlags.NonPublic)!;
            picker.SetValue(vm, (Func<(string Ffmpeg, string Output)?>)(() =>
                (BundledFfmpeg.Resolve(), Path.Combine(output, "fault-video.mp4"))));
            var heartbeat = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            heartbeat.Tick += (_, _) => Log("VIDEO UI responsive busy=" + vm.IsVideoExportBusy + " status=" + vm.VideoExportStatus);
            heartbeat.Start();
            try
            {
                await Command(vm.StartVideoExportCommand);
                var state = await VideoRuntimeState(vm);
                await File.WriteAllTextAsync(Path.Combine(output, "fault-status.json"), JsonSerializer.Serialize(state));
                Require(state.GetValueOrDefault("videoExport.state") == "Failed" && boot.FullRunFaultCode != 0,
                    "video fault probe reproduced a replay failure");
                if (args.Contains("--divergence-fault-window"))
                    await ProbeFaultedWindowAsync(vm, boot, args.Contains("--require-fault-responsive"));
                await Task.Delay(15000);
                movies.VerifyOriginalSavesUnchanged();
                Log("VIDEO failure command returned and UI delay completed");
            }
            finally { heartbeat.Stop(); }
            return;
        }
        if (args.Contains("--divergence-startup-pause"))
        {
            await Task.Delay(35000);
            boot.Refresh();
            Require(boot.IsWaiting && boot.NativeCompletedFrames == 0 && boot.FullRunFaultCode == 0,
                "waiting at startup frame zero does not advance or fault the clock");
        }
        var observations = new Dictionary<string, Dictionary<string, string>>();

        async Task Capture(string label, bool screenshot = true)
        {
            await Until(() => vm.SelectedSession?.Client.IsConnected == true, "Runtime connected", 60);
            await vm.PollInputGridProgressAsync();
            var result = await Field<AutomationBroker>(vm, "automationBroker").ExecuteHumanAsync(
                AutomationCommandIds.FullRunStatus, AutomationScope.ObserveStatus,
                null, string.Empty, null, CancellationToken.None);
            Require(result.Success, label + " status: " + result.Detail);
            observations[label] = result.Data.Where(p => p.Key is "movieFrame" or "sceneName"
                or "heroX" or "heroY" or "heroHealth" or "saveSlot" or "frameBoundary"
                or "faultCode" or "mismatchCount").ToDictionary(p => p.Key, p => p.Value);
            await File.WriteAllTextAsync(Path.Combine(output, label + ".json"), JsonSerializer.Serialize(result.Data));
            var watchFields = new Dictionary<string, string> {
                ["requestId"] = Guid.NewGuid().ToString("N"), ["view"] = "info",
                ["watches"] = JsonSerializer.Serialize(new[] { "game.gameState", "game.entryGateName",
                    "hero.isEnteringFirstLevel", "hero.transitionState", "hero.cState.transitioning",
                    "component(\"/Sequence\", \"OpeningSequence\").isAsync",
                    "component(\"/Sequence\", \"OpeningSequence\").isLevelReady",
                    "component(\"/Sequence\", \"OpeningSequence\").chainSequence.currentSequenceIndex" }) };
            var request = typeof(MainViewModel).GetMethod("RequestRuntimeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var observation = await (Task<IReadOnlyDictionary<string, string>>)request.Invoke(vm,
                new object[] { vm.SelectedSession!.Client, watchFields, CancellationToken.None })!;
            await File.WriteAllTextAsync(Path.Combine(output, label + "-info.json"), observation["snapshotJson"]);
            if (Environment.GetEnvironmentVariable("HKTAS_CINEMATIC_ACCEPTANCE") == "1")
            {
                var traces = Directory.GetFiles(Path.Combine(movies.ShadowRoot, "HollowKnightTAS", "sessions"),
                    "replay-state-trace.csv", SearchOption.AllDirectories);
                if (traces.Length == 1)
                    File.Copy(traces[0], Path.Combine(output, "trace-" + movies.ShadowRoot.Split(Path.DirectorySeparatorChar).Last() + ".csv"), true);
            }
            if (screenshot && !args.Contains("--divergence-no-screenshots"))
            {
                var game = Field<Process>(app, "startupGame"); game.Refresh();
                var capture = typeof(RestorePresentation).Assembly.GetType("HollowKnightTAS.Companion.Services.GameWindowCapture")!;
                var image = await (Task<BitmapSource>)capture.GetMethod("CaptureAsync")!.Invoke(null, new object[] { game.MainWindowHandle })!;
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = File.Create(Path.Combine(output, label + ".png"))) encoder.Save(stream);
            }
            movies.VerifyOriginalSavesUnchanged();
            Log(label + " movie=" + result.Data["movieFrame"] + " native=" + boot.NativeCompletedFrames + " shadow=" + movies.ShadowRoot);
        }

        var target = int.Parse(args.SingleOrDefault(a => a.StartsWith("--divergence-target="))?.Split('=', 2)[1] ?? "700");
        var start = int.Parse(args.SingleOrDefault(a => a.StartsWith("--divergence-start="))?.Split('=', 2)[1] ?? "100");
        // Step across the save-load transition, then compare independent cold restores.
        await Command(vm.StepCommand);
        if (!args.Contains("--divergence-restore-only"))
        {
            await vm.FrameMenuAsync("seek", start);
            AtFrame(vm, boot, start, "reach manual-step starting boundary");
            await Capture("step-" + start);
            for (var frame = start + 1; frame <= target; frame++)
            {
                await Command(vm.StepCommand);
                await Capture("step-" + frame, frame % 50 == 0 || frame is 114 or 640 or 641 || frame == target);
            }
        }
        if (args.Contains("--divergence-play-sample"))
        {
            await vm.FrameMenuAsync("rebuild", start);
            AtFrame(vm, boot, start, "continuous playback starting boundary");
            await vm.FrameMenuAsync("seek", target);
            if (args.Contains("--divergence-fault-probe") && boot.FullRunFaultCode != 0)
            {
                await File.WriteAllTextAsync(Path.Combine(output, "fault-status.json"),
                    JsonSerializer.Serialize(await VideoRuntimeState(vm)));
                if (args.Contains("--divergence-fault-window"))
                    await ProbeFaultedWindowAsync(vm, boot, args.Contains("--require-fault-responsive"));
                for (var second = 0; second < 15; second++)
                {
                    await Task.Delay(1000);
                    Log("FAULT UI dispatcher responsive " + second);
                }
                if (args.Contains("--divergence-recover-after-fault"))
                {
                    await vm.FrameMenuAsync("rebuild", 0);
                    AtFrame(vm, boot, 0, "cold rebuild recovers from terminal replay fault");
                    await Command(vm.StepCommand);
                    AtFrame(vm, boot, 0, "recovered game bootstraps a fresh movie");
                    var recoveredNativeFrame = boot.NativeCompletedFrames;
                    await Command(vm.StepCommand);
                    Require(boot.NativeCompletedFrames == recoveredNativeFrame + 1 && boot.IsWaiting && boot.FullRunFaultCode == 0,
                        "recovered game accepts one normal native frame step");
                }
                movies.VerifyOriginalSavesUnchanged();
                return;
            }
            AtFrame(vm, boot, target, "continuous playback target boundary");
            await Capture("play-" + target);
        }
        var repeats = int.Parse(args.SingleOrDefault(a => a.StartsWith("--divergence-repeats="))?.Split('=', 2)[1] ?? "3");
        for (var i = 0; i < repeats; i++)
        {
            await vm.FrameMenuAsync("rebuild", target);
            AtFrame(vm, boot, target, "repeated restore to gameplay boundary");
            await Capture("restore-" + i + "-" + target);
            if (args.Contains("--divergence-pause-probe"))
            {
                var pausedFrame = boot.NativeCompletedFrames;
                await Task.Delay(2000);
                boot.Refresh();
                Require(boot.NativeCompletedFrames == pausedFrame && boot.IsWaiting && boot.FullRunFaultCode == 0,
                    "paused restore remains at the same native boundary");
                await File.WriteAllTextAsync(Path.Combine(output, "pause-boundary-" + i + ".json"),
                    JsonSerializer.Serialize(new { before = pausedFrame, after = boot.NativeCompletedFrames,
                        waiting = boot.IsWaiting, fault = boot.FullRunFaultCode, stepEnabled = vm.StepCommand.CanExecute(null) }));
                await Capture("after-pause-" + i + "-" + target, false);
            }
        }
        await File.WriteAllTextAsync(Path.Combine(output, "observed-movie.hktas"), vm.MovieText);
        VerifyOriginalMovieInput(originalMovie, vm.MovieText);
        movies.VerifyOriginalSavesUnchanged();
        var targetStates = observations.Where(p => p.Value["movieFrame"] == target.ToString())
            .ToDictionary(p => p.Key, p => p.Value);
        var distinctStates = targetStates.Values.Select(v => JsonSerializer.Serialize(v.OrderBy(p => p.Key)))
            .Distinct().Count();
        await File.WriteAllTextAsync(Path.Combine(output, "comparison.json"),
            JsonSerializer.Serialize(new { target, distinctStates, observations = targetStates }));
        Log("DIAGNOSTIC comparison distinct target states=" + distinctStates
            + "; harness completion is not a determinism pass");
        if (args.Contains("--verify-cinematics"))
        {
            Require(distinctStates == 1, "cutscene step/play/restore target state matches");
            Require(targetStates.Values.All(v => v["faultCode"] == "0" && v["mismatchCount"] == "0"),
                "cutscene samples have no native or input faults");
            var traces = Directory.GetFiles(output, "trace-*.csv");
            Require(traces.Length >= 2, "independent cinematic traces captured");
            Dictionary<string, string[]> ReadTrace(string path) => File.ReadLines(path).Skip(1)
                .Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => line.Split(','))
                .ToDictionary(row => row[1]);
            var baselineTrace = ReadTrace(traces[0]);
            foreach (var path in traces.Skip(1))
            {
                var current = ReadTrace(path);
                Require(current.ContainsKey(target.ToString()), "cinematic trace reaches target");
                var absoluteClockDifferences = current.Values.Count(row => baselineTrace.TryGetValue(row[1], out var before)
                    && !before.Skip(9).Take(4).SequenceEqual(row.Skip(9).Take(4)));
                Log("DIAGNOSTIC absolute Unity clock differences=" + absoluteClockDifferences);
                var mismatch = current.Values.FirstOrDefault(row => baselineTrace.TryGetValue(row[1], out var before)
                    && (!before.Skip(1).Take(8).SequenceEqual(row.Skip(1).Take(8))
                        || !before.Skip(13).SequenceEqual(row.Skip(13))));
                Require(mismatch == null, "scene, physics, frame deltas and cinematic clock match across "
                    + current.Count + " Movie rows; first mismatch=" + (mismatch?[1] ?? "none"));
            }
        }
        if (args.Contains("--verify-first-level"))
        {
            Require(!observations.Values.Any(v => v["sceneName"] == "Opening_Sequence"
                && v["frameBoundary"] == "GameplayInput"), "first-level loading never appears as gameplay input");
            Require(targetStates.Count >= repeats + (args.Contains("--divergence-restore-only") ? 0 : 1),
                "all requested target observations captured");
            Require(distinctStates == 1, "step and repeated restore have identical target gameplay state");
            Require(targetStates.Values.All(v => v["sceneName"] == "Tutorial_01"
                && v["faultCode"] == "0" && v["mismatchCount"] == "0"),
                "all targets reached the first gameplay scene without input/native faults");
        }
    }

    static async Task ProbeFaultedWindowAsync(MainViewModel vm, StartupBootController boot, bool requireResponsive)
    {
        var game = Field<Process>(app, "startupGame"); game.Refresh();
        var window = game.MainWindowHandle;
        var native = boot.NativeCompletedFrames;
        var fault = boot.FullRunFaultCode;
        var gate = Field<StartupBootGate>(boot, "gate");
        Require(window != IntPtr.Zero && fault != 0, "probe owns a faulted game window");
        async Task<bool> Responsive() => await Task.Run(() => FaultWindowMessage(window, 0, IntPtr.Zero,
            IntPtr.Zero, 0x0002, 1000, out _) != IntPtr.Zero);
        var before = await Responsive();
        Log("FAULT WINDOW before move responsive=" + before);
        // Unity's title-bar modal loop can reenter PerformMainLoop. A fault
        // must pump this loop without running another simulation frame.
        PostInfoMessage(window, 0x0112, new IntPtr(0xF010), IntPtr.Zero);
        await Task.Delay(200);
        PostInfoMessage(window, 0x0100, new IntPtr(0x27), IntPtr.Zero);
        await Task.Delay(250);
        PostInfoMessage(window, 0x0100, new IntPtr(0x1B), IntPtr.Zero);
        var after = await Responsive();
        Log("FAULT WINDOW after move responsive=" + after);
        await File.WriteAllTextAsync(Path.Combine(output, "fault-window.json"), JsonSerializer.Serialize(new
        { before, after, nativeBefore = native, nativeAfter = boot.NativeCompletedFrames,
            faultBefore = fault, faultAfter = boot.FullRunFaultCode, saveGuard = gate.SaveGuardArmed }));
        Require(boot.NativeCompletedFrames == native && boot.FullRunFaultCode == fault && gate.SaveGuardArmed == 1,
            "fault interaction never advances simulation or clears fault/save protection");
        if (requireResponsive)
        {
            Require(before && after, "faulted game keeps servicing window messages");
            app.MainWindow.Show(); app.MainWindow.Activate();
            var info = new InfoOverlayWindow();
            var colliders = new ColliderOverlayWindow();
            try
            {
                info.SetOwner(window); info.SetBounds(100, 100, 500, 300, 96); info.ShowOverlay();
                colliders.SetOwner(window); colliders.SetBounds(100, 100, 500, 300, 96); colliders.ShowOverlay();
                await Task.Delay(300);
                info.Hide(); colliders.Hide();
                Log("PASS Studio and owned overlays can show and hide after a native fault");
            }
            finally { info.Close(); colliders.Close(); }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    static extern IntPtr FaultWindowMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    static void VerifyOriginalMovieInput(string original, string observed)
    {
        var codec = new MovieV2Codec();
        var before = codec.Parse(new StringReader(original), "original").Document
            ?? throw new InvalidDataException("Original probe movie is invalid.");
        var after = codec.Parse(new StringReader(observed), "observed").Document
            ?? throw new InvalidDataException("Observed probe movie is invalid.");
        var count = before.Runs.Sum(run => run.RepeatCount);
        var afterCount = after.Runs.Sum(run => run.RepeatCount);
        Require(afterCount == count || afterCount == count + 500,
            "only the normal 500-frame editor padding may extend the sequence");
        Require(codec.WriteCanonical(MovieV2Prefix.Take(after, count)) == codec.WriteCanonical(before),
            "all original movie input and metadata remain unchanged");
        long end = 0;
        foreach (var run in after.Runs)
        {
            end += run.RepeatCount;
            if (end > count)
                Require(run.Authored && run.Samples.Count == 0 && !run.RngSeed.HasValue,
                    "editor tail padding contains no input or RNG commands");
        }
    }
}
