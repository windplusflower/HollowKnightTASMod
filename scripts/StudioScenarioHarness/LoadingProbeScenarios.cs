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
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.ViewModels;
using HollowKnightTAS.Core.Automation;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Companion.Automation;

internal static partial class StudioScenarioHarness
{
    // A completed trace or new recording is not a P0/full-state PASS.
    static async Task RunLoadingProbeAsync(string[] args)
    {
        string Arg(string name) => args.Single(a => a.StartsWith(name + "=", StringComparison.Ordinal)).Split('=', 2)[1];
        var first = long.Parse(Arg("--probe-first"));
        var end = long.Parse(Arg("--probe-end"));
        var primaryProbe = !args.Contains("--probe-disable-primary");
        var gameplayOracle = args.Contains("--probe-gameplay-oracle");
        var windowsArgument = args.SingleOrDefault(value => value.StartsWith("--probe-oracle-windows=", StringComparison.Ordinal));
        var oracleWindows = windowsArgument == null ? null : Arg("--probe-oracle-windows");
        Require(windowsArgument == null || (gameplayOracle && !string.IsNullOrEmpty(oracleWindows)
            && !args.Contains("--probe-serial-boundary-only")), "declared oracle windows need the gameplay oracle and cannot be empty or candidate-only");
        var nativeScheduler = args.Contains("--probe-native-scheduler");
        var serialScenes = args.Contains("--probe-serial-scenes");
        if (serialScenes) Require(end >= 2, "serial control checks need at least two Movie frames");
        Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE", primaryProbe ? "1" : null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_GAMEPLAY_ORACLE", gameplayOracle ? "1" : null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_ORACLE_WINDOWS", oracleWindows);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_NATIVE_SCHEDULER", nativeScheduler ? "1" : null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_NATIVE_PHYSICS", args.Contains("--probe-native-physics") ? "1" : null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_NATIVE_PHYSICS_ONLY", args.Contains("--probe-native-physics-only") ? "1" : null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_NATIVE_PHYSICS_DETAILS", args.Contains("--probe-native-physics-details") ? "1" : null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_SERIAL_BOUNDARY_ONLY", args.Contains("--probe-serial-boundary-only") ? "1" : null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_BOUNDARY_INVENTORY", args.Contains("--probe-boundary-inventory") ? "1" : null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_CANONICAL_BOUNDARY", args.Contains("--probe-canonical-boundary") ? "1" : null);
        // All canonical diagnostics capture the actual clock portion of D.
        // This enables the existing bounded native codec, never qualification.
        if (args.Contains("--probe-canonical-boundary")) Environment.SetEnvironmentVariable("HKTAS_SCENE_CHECKPOINT_EXPERIMENT", "1");
        Environment.SetEnvironmentVariable("HKTAS_LOADING_VIRTUAL_AUDIO", args.Contains("--probe-virtual-audio") ? "1" : null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_ISOLATED_ONLINE", args.Contains("--probe-isolated-online") ? "1" : null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_CANONICAL_PARTICLES", args.Contains("--probe-canonical-particles") ? "1" : null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_VERIFY_ONLINE", args.Contains("--probe-isolated-online") ? "1" : null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_ORACLE_FIELD_DETAILS", args.Contains("--probe-field-details") ? "1" : null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE_FIRST", first.ToString());
        Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE_END", end.ToString());
        Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE_LIGHT", args.Contains("--probe-light") ? "1" : null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE_CALLS", args.Contains("--probe-random-calls") ? "1" : null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE_ENGINE_LAYOUT", args.Contains("--probe-engine-layout") ? "1" : null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE_CLOCK_ROUNDTRIP", args.Contains("--probe-clock-roundtrip") ? "1" : null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_SYNCHRONOUS_FETCH", args.Contains("--probe-synchronous-fetch") ? "1" : null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_CANONICAL_STARTUP_CLOCK", args.Contains("--probe-canonical-startup-clock") ? "1" : null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_MOD_WALL_CLOCK", args.Contains("--probe-mod-wall-clock") ? "1" : null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_SERIAL_SCENES", args.Contains("--probe-serial-scenes") ? "1" : null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT", output);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_DISABLE_LEGACY_CALIBRATION", args.Contains("--probe-disable-legacy-calibration") ? "1" : null);
        if (args.Contains("--probe-random-calls"))
        {
            Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE_CALL_FIRST", Arg("--probe-call-first"));
            Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE_CALL_END", Arg("--probe-call-end"));
        }
        var vm = (MainViewModel)app.MainWindow.DataContext;
        var boot = Field<StartupBootController>(app, "startupBoot");
        var movies = Field<FullRunMovieCoordinator>(app, "fullRunMovies");
        // Preserve the exact owned shadows even when the watchdog terminates
        // the process before normal trace export. Never copy save contents.
        var ownedShadows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var shadowTracker = new System.Threading.Timer(_ =>
        {
            try
            {
                var shadow = movies.ShadowRoot;
                if (string.IsNullOrEmpty(shadow)) return;
                lock (logLock)
                {
                    if (!ownedShadows.Add(shadow)) return;
                    if (ownedShadows.Count > 8) throw new InvalidOperationException("Probe owned-shadow budget exceeded.");
                    File.WriteAllText(Path.Combine(output, "probe-shadows.json"), JsonSerializer.Serialize(
                        new { diagnosticOnly = true, shadows = ownedShadows.OrderBy(value => value).ToArray() }));
                }
            }
            catch (Exception exception) { Log("SHADOW TRACKING FAILED " + exception.Message); }
        }, null, 0, 250);
        void Set(string name, object value) => typeof(MainViewModel).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);
        Set("worldlines", new StudioTimelineStore());
        Set("initialSaveCacheRoot", Path.Combine(output, "initial-saves"));
        Set("activeSequenceDirectory", output);
        Set("activeAutoSaveSeconds", 0);
        var source = Path.GetFullPath(Arg("--probe-movie"));
        var sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(source)));
        var record = args.Contains("--probe-record-inputs");
        var protectedSource = SequencePackage.Read(source);
        Require(protectedSource.InitialSaves != null, "loading probe binds protected initial saves from its source package");
        if (!record)
        {
            var replay = new MovieV2Codec().Parse(new StringReader(protectedSource.Movie), source).Document
                ?? throw new InvalidDataException("Invalid recorded source inputs.");
            Require(replay.Runs.All(run => !run.Authored)
                && replay.Header.NativeProfileId == MovieProtocolV2.SelectedNativeProfileId,
                "cold baseline uses a strict recording with the selected execution profile");
        }
        if (record)
        {
            Require(NativeExecutionProfile.IsCanonicalStartupProfile(MovieProtocolV2.SelectedNativeProfileId)
                || MovieProtocolV2.SelectedNativeProfileId == NativeExecutionProfile.LegacyProfileId,
                "new recording uses a registered execution profile");
            var original = protectedSource;
            Require(original.InitialSaves != null, "recording source binds the protected initial saves");
            var parsed = new MovieV2Codec().Parse(new StringReader(original.Movie), source).Document
                ?? throw new InvalidDataException("Invalid source inputs.");
            Require(parsed.Runs.All(run => run.FramesPerSecond == 50 && run.RngSeed == null)
                && parsed.Runs.Sum(run => run.RepeatCount) >= end, "bounded 50 FPS input script without RNG seeds");
            movies.SequenceInitialSaves = original.InitialSaves;
            Set("gameMouseEnabled", parsed.Header.MouseEnabled);
            Set("defaultFrameRate", "50");
            var old = parsed.Header;
            var header = new MovieV2Header(old.GameVersion, old.ApiVersion, old.ModVersion,
                MovieProtocolV2.SelectedNativeProfileId, old.ActionSchemaId, old.MouseEnabled,
                old.EnvironmentSha256, old.ViewportWidth, old.ViewportHeight);
            var runs = parsed.Runs.Select(run => new NativeFrameRun(run.RepeatCount, run.Samples,
                run.Span, run.FramesPerSecond, authored: true)).Concat(new[]
                { new NativeFrameRun(1, Array.Empty<GameInputSample>(), new MovieSourceSpan("<probe-tail>", 1, 1, 1), authored: true) });
            var script = Path.Combine(output, "recording-inputs.hktas");
            await File.WriteAllTextAsync(script, new MovieV2Codec().WriteCanonical(new MovieV2Document(script, header, runs)));
            Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE_RECORD_INPUTS", script);
        }
        else Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE_RECORD_INPUTS", null);
        await Field<Func<string, Task>>(vm, "launchGame")(@"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe");
        try
        {
            Execute(vm.NewFullRunMovieCommand);
            await Command(vm.StepCommand);
            await Until(() => vm.SelectedSession?.Client.IsConnected == true, "loading probe Runtime connected", 120);
            var oldPid = Field<Process>(app, "startupGame").Id;
            if (record)
            {
                await Until(() =>
                    {
                        boot.Refresh();
                        if (boot.FullRunFaultCode != 0)
                            throw new InvalidOperationException("Recording bootstrap native fault " + boot.FullRunFaultCode);
                        return boot.IsWaiting;
                    },
                    "recording bootstrap reaches a paused native boundary", 120);
                var seek = await Field<AutomationBroker>(vm, "automationBroker").ExecuteHumanAsync(
                    AutomationCommandIds.FullRunSeek, AutomationScope.ControlPlayback,
                    new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["targetFrame"] = (serialScenes ? end - 2 : end).ToString(),
                        ["expectedNativeFrame"] = boot.NativeCompletedFrames.ToString()
                    }, "Paused", null, CancellationToken.None);
                Require(seek.Success, "normal recording seek accepted: " + seek.Detail);
                await movies.RunAsync(boot.NativeCompletedFrames, CancellationToken.None);
                await Until(() => { boot.Refresh(); if (boot.FullRunFaultCode != 0) throw new InvalidOperationException("recording native fault " + boot.FullRunFaultCode); return boot.IsWaiting; },
                    "scripted recording exact endpoint", 180);
                if (serialScenes)
                {
                    var pausedRuntime = await VideoRuntimeState(vm);
                    Require(Number(pausedRuntime, "movieFrame") == end - 2,
                        "serial recording Runtime reached the exact pre-step endpoint");
                    await Until(() => Field<long>(vm, "currentFullRunMovieFrame") == end - 2,
                        "Studio display catches up to the paused Runtime");
                    AtFrame(vm, boot, end - 2, "serial recording pauses before final two steps");
                    await CheckSerialPauseAsync(vm, boot);
                    for (var frame = end - 1; frame <= end; frame++)
                    {
                        await Command(vm.StepCommand);
                        await Until(() => { boot.Refresh(); return boot.IsWaiting && Field<long>(vm, "currentFullRunMovieFrame") == frame; },
                            "serial recording single Movie step");
                        AtFrame(vm, boot, frame, "serial recording advances exactly one Movie frame");
                    }
                }
                var actual = await VideoRuntimeMovie(vm, movies);
                var strict = new MovieV2Codec().Parse(new StringReader(actual.Movie), "<new-recording>").Document
                    ?? throw new InvalidDataException("Invalid native recording.");
                Require(actual.Frame == end && strict.Runs.Sum(run => run.RepeatCount) == end
                    && strict.Runs.All(run => !run.Authored)
                    && strict.Header.NativeProfileId == MovieProtocolV2.SelectedNativeProfileId,
                    "fresh strict native recording has exact length and selected profile identity");
                var recordingName = MovieProtocolV2.SelectedNativeProfileId == NativeExecutionProfile.CanonicalParticleProfileId
                    ? "serial-scenes-v12-particles-recording.hktaspack"
                    : MovieProtocolV2.SelectedNativeProfileId == NativeExecutionProfile.IsolatedOnlineProfileId
                    ? "serial-scenes-v12-isolated-recording.hktaspack"
                    : MovieProtocolV2.SelectedNativeProfileId == NativeExecutionProfile.VirtualAudioSerialProfileId
                    ? "serial-scenes-v12-audio-recording.hktaspack"
                    : MovieProtocolV2.SelectedNativeProfileId == NativeExecutionProfile.SerialSceneProfileId
                    ? "serial-scenes-v12-recording.hktaspack"
                    : MovieProtocolV2.SelectedNativeProfileId == MovieProtocolV2.CanonicalModClockNativeProfileId
                        ? "canonical-startup-v12-mod-clock-recording.hktaspack"
                        : MovieProtocolV2.SelectedNativeProfileId == NativeExecutionProfile.LegacyProfileId
                            ? "legacy-v3-recording.hktaspack" : "canonical-startup-v12-recording.hktaspack";
                await SequencePackage.WriteAsync(Path.Combine(output, recordingName),
                    actual.Movie, movies.SessionInitialSaves);
            }
            else
            {
                await vm.OpenMovieFileAsync(source);
                await vm.FrameMenuAsync("rebuild", end);
                Require(Field<Process>(app, "startupGame").Id != oldPid, "independent cold replacement process");
            }
            AtFrame(vm, boot, end, "loading probe exact endpoint");
            var state = await VideoRuntimeState(vm);
            Require(state["mismatchCount"] == "0" && state["error"] == "", "probe execution has no input mismatch");
            if (serialScenes)
            {
                await CheckSerialPauseAsync(vm, boot);
                var serialTrace = Path.Combine(output, "serial-scenes-" + Field<Process>(app, "startupGame").Id + ".jsonl");
                var serialRows = File.ReadLines(serialTrace).Select(line => JsonDocument.Parse(line)).ToArray();
                try
                {
                    var transitions = serialRows.Where(row => row.RootElement.GetProperty("phase").GetString() == "unload-requested")
                        .Select(row => row.RootElement.GetProperty("ordinal").GetInt32()).ToArray();
                    Require(transitions.Length > 0, "serial policy executed at least one scene transition");
                    foreach (var ordinal in transitions)
                    {
                        var phases = serialRows.Where(row => row.RootElement.GetProperty("ordinal").GetInt32() == ordinal)
                            .Select(row => row.RootElement.GetProperty("phase").GetString()).ToArray();
                        Require(phases.SequenceEqual(new[] { "before-unload", "unload-requested", "native-empty-boundary",
                            "target-load-start", "target-activation-synchronized", "duplicate-source-unload-suppressed", "staging-roots-cleared", "target-finished", "complete" }),
                            "serial transition has complete ordered unload / native boundary / target load evidence: " + ordinal);
                    }
                }
                finally { foreach (var row in serialRows) row.Dispose(); }
            }
            await File.WriteAllTextAsync(Path.Combine(output, "target-state.json"), JsonSerializer.Serialize(state));
            foreach (var traceName in (primaryProbe ? new[] { "loading-boundary-probe.jsonl" } : Array.Empty<string>())
                .Concat(gameplayOracle ? new[] { "loading-gameplay-oracle.jsonl" } : Array.Empty<string>()))
            {
                var traces = Directory.GetFiles(movies.ShadowRoot, traceName, SearchOption.AllDirectories);
                Require(traces.Length == 1, "one " + traceName + " in current protected shadow");
                File.Copy(traces[0], Path.Combine(output, traceName), false);
                foreach (var candidate in Directory.GetFiles(Path.GetDirectoryName(traces[0])!, "loading-candidate-*-inventory.json"))
                    if (!File.Exists(Path.Combine(output, Path.GetFileName(candidate))))
                        File.Copy(candidate, Path.Combine(output, Path.GetFileName(candidate)), false);
                foreach (var name in new[] { "engine-clock-layout.json", "engine-clock-roundtrip.json", "loading-state-inventory.json", "loading-oracle-progress.json", "loading-oracle-stage-progress.json", "loading-field-digests.json" })
                {
                    var extra = Path.Combine(Path.GetDirectoryName(traces[0])!, name);
                    if (File.Exists(extra) && !File.Exists(Path.Combine(output, name))) File.Copy(extra, Path.Combine(output, name), false);
                }
                using var footer = JsonDocument.Parse(File.ReadLines(traces[0]).Last());
                Require(footer.RootElement.GetProperty("kind").GetString() == "footer"
                    && footer.RootElement.GetProperty("success").GetBoolean(), "bounded " + traceName + " completed without sampling failure");
            }
            if (gameplayOracle) Require(File.Exists(Path.Combine(output, "loading-state-inventory.json")), "independent field inventory exported");
            Require(sourceHash == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(source))),
                "input package bytes unchanged");
            await File.WriteAllTextAsync(Path.Combine(output, "source-identity.json"), JsonSerializer.Serialize(new
                { source, sourceHash, first, end, record, primaryProbe, gameplayOracle, nativeScheduler, serialScenes,
                    compiledFieldReaders = Environment.GetEnvironmentVariable("HKTAS_LOADING_ORACLE_REFLECTION_FIELDS") != "1",
                    packedLeafArrays = Environment.GetEnvironmentVariable("HKTAS_LOADING_ORACLE_LEGACY_ARRAYS") != "1",
                    nativeProfile = MovieProtocolV2.SelectedNativeProfileId,
                    modWallClock = args.Contains("--probe-mod-wall-clock"),
                    fieldDetails = args.Contains("--probe-field-details"),
                    gameProcessId = Field<Process>(app, "startupGame").Id,
                    shadow = movies.ShadowRoot, phaseP0Passed = false }));
            Log("DISCOVERY TRACE COMPLETE; full-state P0 gate not asserted");
            var holdArgument = args.SingleOrDefault(a => a.StartsWith("--probe-hold-seconds=", StringComparison.Ordinal));
            if (holdArgument != null)
            {
                var seconds = int.Parse(holdArgument.Split('=', 2)[1]);
                Require(seconds >= 0 && seconds <= 120, "bounded endpoint screenshot hold");
                var captureType = typeof(MainViewModel).Assembly.GetType("HollowKnightTAS.Companion.Services.GameWindowCapture", true)!;
                var gameProcess = Field<Process>(app, "startupGame");
                gameProcess.Refresh();
                var image = await (Task<BitmapSource>)captureType.GetMethod("CaptureAsync", BindingFlags.Static | BindingFlags.Public)!
                    .Invoke(null, new object[] { gameProcess.MainWindowHandle })!;
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var file = File.Create(Path.Combine(output, "endpoint.png"))) encoder.Save(file);
                Log("SCREENSHOT HOLD seconds=" + seconds);
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                await CheckSerialPauseAsync(vm, boot);
            }
            if (args.Contains("--probe-export-video"))
            {
                Require(!record, "MP4 validation uses an existing strict package");
                var picker = typeof(MainViewModel).GetField("videoExportFilePicker", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var previousPicker = picker.GetValue(vm);
                var destination = Path.Combine(output, "serial-profile.mp4");
                var encoderPath = Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg", "ffmpeg.exe");
                Require(File.Exists(encoderPath) && !File.Exists(destination), "bundled encoder and fresh MP4 path");
                var draft = vm.MovieText;
                try
                {
                    picker.SetValue(vm, (Func<(string Ffmpeg, string Output)?>)(() => (encoderPath, destination)));
                    await Command(vm.StartVideoExportCommand).WaitAsync(TimeSpan.FromMinutes(2));
                    var video = await VideoRuntimeState(vm);
                    await File.WriteAllTextAsync(Path.Combine(output, "video-state.json"), JsonSerializer.Serialize(video));
                    Require(video.GetValueOrDefault("videoExport.state") == "Completed" && File.Exists(destination), "serial profile publishes MP4");
                    Require(Number(video, "videoExport.startMovieFrame") == 0
                        && Number(video, "videoExport.endMovieFrame") == MovieFrames(draft)
                        && video.GetValueOrDefault("videoExport.timing") == "per-frame-game-clock",
                        "serial MP4 covers the complete editor movie with game-clock timing");
                    Require(video["mismatchCount"] == "0" && video["error"] == "" && vm.MovieText == draft,
                        "serial MP4 preserves strict input execution and editor draft");
                }
                finally { picker.SetValue(vm, previousPicker); }
            }
        }
        finally { movies.VerifyOriginalSavesUnchanged(); }
    }

    static async Task CheckSerialPauseAsync(MainViewModel vm, StartupBootController boot)
    {
        var before = await VideoRuntimeState(vm);
        await Task.Delay(400);
        boot.Refresh();
        var after = await VideoRuntimeState(vm);
        Require(boot.IsWaiting && boot.FullRunFaultCode == 0
            && Number(before, "movieFrame") == Number(after, "movieFrame")
            && Number(before, "nativeFrame") == Number(after, "nativeFrame")
            && after["mismatchCount"] == "0" && after["error"] == "",
            "serial scene profile remains paused without Movie/native advancement");
        Log("SERIAL PAUSE movie=" + Number(after, "movieFrame") + " native=" + Number(after, "nativeFrame"));
    }
}
