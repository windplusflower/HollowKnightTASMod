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
using HollowKnightTAS.Diagnostics;
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
        var phaseOneTrace = args.Contains("--probe-canonical-phase1-trace");
        Require(!phaseOneTrace || (args.Contains("--probe-canonical-boundary") && gameplayOracle && nativeScheduler
            && args.Contains("--probe-native-physics") && args.Contains("--probe-serial-boundary-only")
            && !args.Contains("--checkpoint-prototype") && !args.Contains("--probe-export-video")),
            "Phase 1 route observation requires the full canonical probe and excludes checkpoint/video operations");
        Environment.SetEnvironmentVariable("HKTAS_LOADING_CANONICAL_PHASE1_TRACE", phaseOneTrace ? "1" : null);
        var s0TargetArgument = args.SingleOrDefault(value => value.StartsWith("--probe-s0-target=", StringComparison.Ordinal));
        Require(s0TargetArgument == null || phaseOneTrace, "S0-only preparation requires the protected Phase 1 observer");
        Environment.SetEnvironmentVariable("HKTAS_LOADING_S0_OBSERVING", s0TargetArgument != null ? "1" : null);
        var clockExperiment = args.Contains("--probe-clock-carry-experiment");
        var clockSourceArgument = args.SingleOrDefault(value => value.StartsWith("--probe-clock-apply-source=", StringComparison.Ordinal));
        Require(!clockExperiment || phaseOneTrace, "limited clock D experiment requires the protected observer");
        Require(clockSourceArgument == null || clockExperiment && s0TargetArgument != null, "clock D application requires zero-Movie preparation");
        Environment.SetEnvironmentVariable("HKTAS_LOADING_CLOCK_APPLY_EXPERIMENT", clockExperiment ? "1" : null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_CLOCK_APPLY_SOURCE", clockSourceArgument == null ? null : Arg("--probe-clock-apply-source"));
        var actualDExperiment = args.Contains("--probe-actual-d-experiment");
        var ordinalArgument = args.SingleOrDefault(value => value.StartsWith("--probe-actual-d-ordinal=", StringComparison.Ordinal));
        var sourceBoundaryOrdinal = ordinalArgument == null ? 1 : int.Parse(Arg("--probe-actual-d-ordinal"), System.Globalization.CultureInfo.InvariantCulture);
        Require(sourceBoundaryOrdinal >= 1 && sourceBoundaryOrdinal <= 64 && (actualDExperiment || ordinalArgument == null)
            && (sourceBoundaryOrdinal == 1 || !args.Contains("--probe-physics-follow-on")), "explicit bounded actual D source ordinal");
        Environment.SetEnvironmentVariable("HKTAS_LOADING_ACTUAL_D_ORDINAL", actualDExperiment ? sourceBoundaryOrdinal.ToString() : null);
        var d0Route = args.Contains("--probe-canonical-d0-route");
        Require(!d0Route || phaseOneTrace && s0TargetArgument == null && !actualDExperiment && !clockExperiment
            && !args.Any(value => value.StartsWith("--probe-derived-fixture=", StringComparison.Ordinal)),
            "D0 route uses complete boundary digests without S0 preparation, D transactions or derived fixtures");
        Environment.SetEnvironmentVariable("HKTAS_LOADING_CANONICAL_D0_ROUTE", d0Route ? "1" : null);
        var actualDStageArgument = args.SingleOrDefault(value => value.StartsWith("--probe-actual-d-stage=", StringComparison.Ordinal));
        var actualDStage = actualDStageArgument == null ? "" : Arg("--probe-actual-d-stage");
        var actualDRelayArgument = args.SingleOrDefault(value => value.StartsWith("--probe-actual-d-relay=", StringComparison.Ordinal));
        Require(actualDStage == "" && actualDRelayArgument == null || actualDExperiment && args.Contains("--probe-actual-d-suffix")
            && (actualDStage == "source" || actualDStage == "target") && actualDRelayArgument != null, "actual D stages require a single bounded memory relay and suffix observation");
        Require(!actualDExperiment || clockExperiment && s0TargetArgument != null && phaseOneTrace && clockSourceArgument == null,
            "actual D uses a bounded in-memory source packet and a fresh zero-Movie target");
        Environment.SetEnvironmentVariable("HKTAS_LOADING_ACTUAL_D_EXPERIMENT", actualDExperiment ? "1" : null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_ACTUAL_D_SOURCE", actualDExperiment && actualDStage != "target" ? "1" : null);
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
        var derivedArgument = args.SingleOrDefault(value => value.StartsWith("--probe-derived-fixture=", StringComparison.Ordinal));
        if (derivedArgument != null) {
            Require(!record && phaseOneTrace && actualDExperiment && args.Contains("--probe-actual-d-suffix")
                && (args.Contains("--probe-physics-follow-on") || sourceBoundaryOrdinal > 1),
                "derived input fixture is only a finite diagnostic, not a new recording");
            File.Copy(Arg("--probe-derived-fixture"), Path.Combine(output, "derived-fixture-provenance.json"));
            Log("DERIVED DIAGNOSTIC ONLY: old input samples retained; old expectations are extra comparisons, not an authoritative new-profile recording.");
        }
        var protectedSource = SequencePackage.Read(source);
        Require(protectedSource.InitialSaves != null, "loading probe binds protected initial saves from its source package");
        if (!record)
        {
            var replay = new MovieV2Codec().Parse(new StringReader(protectedSource.Movie), source).Document
                ?? throw new InvalidDataException("Invalid recorded source inputs.");
            Require(replay.Runs.All(run => !run.Authored)
                && replay.Header.NativeProfileId == MovieProtocolV2.SelectedNativeProfileId,
                derivedArgument == null ? "cold baseline uses a strict recording with the selected execution profile"
                    : "derived diagnostic inputs are strict and explicitly bind the selected profile; not a new recording");
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
        var directProtectedCold = !record && s0TargetArgument != null;
        if (directProtectedCold)
        {
            // Open the strict bound package before the ordinary verified
            // launcher prepares its protected shadow. An initial recording
            // process would be discarded in full by the old restart below.
            Require(Field<Process?>(app, "startupGame") == null && !boot.IsPending && !movies.IsPending,
                "protected cold launch starts without an owned game or active save session");
            await vm.OpenMovieFileAsync(source);
            Require(movies.SequenceInitialSaves?.Id == protectedSource.InitialSaves!.Id,
                "ordinary document open binds the exact source initial saves before cold launch");
        }
        await Field<Func<string, Task>>(vm, "launchGame")(@"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe");
        byte[]? actualDBytes = null;
        try
        {
            var oldPid = 0;
            if (!directProtectedCold)
            {
                Execute(vm.NewFullRunMovieCommand);
                await Command(vm.StepCommand);
                await Until(() => vm.SelectedSession?.Client.IsConnected == true, "loading probe Runtime connected", 120);
                oldPid = Field<Process>(app, "startupGame").Id;
            }
            else
            {
                Require(movies.IsPending && movies.Mode == "Unarmed" && boot.FullRunFaultCode == 0
                    && !Field<Process>(app, "startupGame").HasExited
                    && movies.SessionInitialSaves?.Id == protectedSource.InitialSaves!.Id,
                    "independent protected cold process uses the exact package baseline and original unarmed gate");
                movies.VerifyOriginalSavesUnchanged();
            }
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
                async Task RunRecordingTo(long target) {
                    var seek = await Field<AutomationBroker>(vm, "automationBroker").ExecuteHumanAsync(
                        AutomationCommandIds.FullRunSeek, AutomationScope.ControlPlayback,
                        new System.Collections.Generic.Dictionary<string, string> {
                            ["targetFrame"] = target.ToString(), ["expectedNativeFrame"] = boot.NativeCompletedFrames.ToString()
                        }, "Paused", null, CancellationToken.None);
                    Require(seek.Success, "normal recording seek accepted: " + seek.Detail);
                    await movies.RunAsync(boot.NativeCompletedFrames, CancellationToken.None);
                    await Until(() => { boot.Refresh(); if (boot.FullRunFaultCode != 0) throw new InvalidOperationException("recording native fault " + boot.FullRunFaultCode); return boot.IsWaiting; },
                        "scripted recording exact endpoint", 180);
                }
                MovieV2Document? segmentPrefix = null;
                Dictionary<string, string>? segmentPauseBefore = null, segmentPauseAfter = null;
                var recordingStartNative = boot.NativeCompletedFrames;
                if (d0Route) {
                    Require(first == 0 && end == 3150 && serialScenes, "segmented D0 recording uses the fixed original 3150-frame route");
                    await RunRecordingTo(1575);
                    segmentPauseBefore = await VideoRuntimeState(vm);
                    Require(Number(segmentPauseBefore, "movieFrame") == 1575 && segmentPauseBefore["sceneName"] == "GG_Workshop"
                        && segmentPauseBefore["mode"] == "Recording", "D0 intermediate pause is the original stable gameplay boundary");
                    await CheckSerialPauseAsync(vm, boot);
                    segmentPauseAfter = await VideoRuntimeState(vm);
                    var prefix = await VideoRuntimeMovie(vm, movies);
                    Require(prefix.Frame == 1575, "same recording session exports its exact intermediate prefix");
                    segmentPrefix = new MovieV2Codec().Parse(new StringReader(prefix.Movie), "<D0-recording-prefix>").Document
                        ?? throw new InvalidDataException("Invalid actual intermediate recording.");
                    await File.WriteAllTextAsync(Path.Combine(output, "recording-segment-prefix.hktas"), prefix.Movie);
                }
                await RunRecordingTo(serialScenes ? end - 2 : end);
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
                if (segmentPrefix != null) {
                    var remaining = 1575L; var prefixRuns = new List<NativeFrameRun>();
                    foreach (var run in strict.Runs) {
                        var take = Math.Min(remaining, run.RepeatCount);
                        if (take == 0) break;
                        prefixRuns.Add(new NativeFrameRun(take, run.Samples, run.Span, run.FramesPerSecond, run.Authored, run.RngSeed));
                        remaining -= take;
                    }
                    var codec = new MovieV2Codec();
                    var prefixEqual = remaining == 0 && codec.WriteCanonical(segmentPrefix)
                        == codec.WriteCanonical(new MovieV2Document("<D0-final-prefix>", strict.Header, prefixRuns));
                    Require(prefixEqual && strict.Runs.All(run => run.FramesPerSecond == 50 && run.RngSeed == null)
                        && Number(segmentPauseBefore!, "nativeFrame") == Number(segmentPauseAfter!, "nativeFrame")
                        && Number(segmentPauseBefore!, "movieFrame") == Number(segmentPauseAfter!, "movieFrame")
                        && recordingStartNative < Number(segmentPauseBefore!, "nativeFrame") && Number(segmentPauseAfter!, "nativeFrame") < boot.NativeCompletedFrames,
                        "same-session native recording preserves every prefix input/FPS and the intermediate frozen boundary");
                    await File.WriteAllTextAsync(Path.Combine(output, "recording-segment-proof.json"), JsonSerializer.Serialize(new {
                        diagnosticOnly = true, firstTarget = 1575, secondTarget = end - 2, finalFrame = actual.Frame,
                        recordingStartNative, segmentPauseBefore, segmentPauseAfter, finalNative = boot.NativeCompletedFrames,
                        prefixInputAndFpsEqual = prefixEqual, sameRecordingSession = true, perCommandWaitSeconds = 180,
                        phaseWatchdogSeconds = 240, hostDeadlineSeconds = 270, deadlineReset = false, d0Passed = false }));
                }
                var recordingName = MovieProtocolV2.SelectedNativeProfileId == NativeExecutionProfile.CanonicalBoundaryProfileId
                    ? "serial-scenes-canonical-boundary-recording.hktaspack"
                    : MovieProtocolV2.SelectedNativeProfileId == NativeExecutionProfile.CanonicalParticleProfileId
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
                if (!directProtectedCold)
                {
                    await vm.OpenMovieFileAsync(source);
                    if (s0TargetArgument != null) await Field<Func<Task>>(vm, "restartProtectedGame")();
                    else await vm.FrameMenuAsync("rebuild", end);
                    Require(Field<Process>(app, "startupGame").Id != oldPid, "independent cold replacement process");
                }
            }
            if (s0TargetArgument != null)
            {
                // The protected launcher parks before Runtime is bootstrapped.
                // Arm the real protected replay, then execute only its original
                // bootstrap PlayerLoop; StartInputAt pauses before Movie zero.
                var zeroMovie = new MovieV2Codec().Parse(new StringReader(protectedSource.Movie), source).Document
                    ?? throw new InvalidDataException("Invalid zero Movie source.");
                movies.ArmReplay(zeroMovie, 0);
                await movies.RunAsync(0, CancellationToken.None);
                await Until(() => { boot.Refresh(); if (boot.FullRunFaultCode != 0) throw new InvalidOperationException("S0 bootstrap fault " + boot.FullRunFaultCode); return boot.IsWaiting; },
                    "original R3 bootstrap parks before Movie zero", 120);
                await Until(() => vm.SelectedSession?.Client.IsConnected == true, "S0 replacement Runtime connected", 60);
                AtFrame(vm, boot, 0, "S0 preparation starts at actual Movie zero");
                var request = typeof(SceneCheckpointClient).GetMethod("RequestAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
                async Task<IReadOnlyDictionary<string, string>> Call(Dictionary<string, string> fields)
                {
                    fields["requestId"] = Guid.NewGuid().ToString("N");
                    return await (Task<IReadOnlyDictionary<string, string>>)request.Invoke(null,
                        new object[] { vm.SelectedSession!.Client, fields, CancellationToken.None })!;
                }
                IReadOnlyDictionary<string, string>? actualDHeader = null;
                async Task PhysicsFollowOn()
                {
                    if (!args.Contains("--probe-physics-follow-on")) return;
                    var arm = await Call(new Dictionary<string, string> { ["phase"] = "d-exp-arm-physics-follow-on" });
                    Require(arm["status"] == "PhysicsFollowOnArmed", "finite physics follow-on admitted after independent +32 acceptance");
                    boot.Refresh();
                    var seek = await Field<AutomationBroker>(vm, "automationBroker").ExecuteHumanAsync(
                        AutomationCommandIds.FullRunSeek, AutomationScope.ControlPlayback,
                        new Dictionary<string, string> { ["targetFrame"] = "1706", ["expectedNativeFrame"] = boot.NativeCompletedFrames.ToString() },
                        "Paused", null, CancellationToken.None);
                    Require(seek.Success, "ordinary Replay follow-on seek accepted: " + seek.Detail);
                    await movies.RunAsync(boot.NativeCompletedFrames, CancellationToken.None);
                    await Until(() => { boot.Refresh(); if (boot.FullRunFaultCode != 0) throw new InvalidOperationException("physics follow-on native fault " + boot.FullRunFaultCode); return boot.IsWaiting; },
                        "ordinary original Movie reaches finite physics follow-on endpoint", 120);
                    var state = await VideoRuntimeState(vm);
                    Require(state["movieFrame"] == "1706" && state["mismatchCount"] == "0" && state["error"] == "",
                        "original inputs reach exact 1706 without mismatch after independent +32 endpoint");
                    await File.WriteAllTextAsync(Path.Combine(output, "physics-follow-on-state.json"), JsonSerializer.Serialize(state));
                }
                if (actualDExperiment && actualDStage != "target") {
                    await movies.RunAsync(boot.NativeCompletedFrames, CancellationToken.None);
                    await Until(() => { boot.Refresh(); if (boot.FullRunFaultCode != 0) throw new InvalidOperationException("actual source D fault " + boot.FullRunFaultCode); return boot.IsWaiting; },
                        "actual source B captures both graphs and parks without executing target load", 180);
                    actualDHeader = await Call(new Dictionary<string, string> { ["phase"] = "d-exp-source-info" });
                    Require(actualDHeader["sourceOrdinal"] == sourceBoundaryOrdinal.ToString(), "source packet is sealed at the selected actual ordinal");
                    await File.WriteAllTextAsync(Path.Combine(output, "actual-d-source-header.json"), JsonSerializer.Serialize(actualDHeader));
                    var length = int.Parse(actualDHeader["length"], System.Globalization.CultureInfo.InvariantCulture);
                    Require(length > 0 && length <= 32 * 1024 * 1024, "in-memory D packet obeys its fixed bound");
                    actualDBytes = new byte[length];
                    for (var offset = 0; offset < length; offset += 262144) {
                        var part = await Call(new Dictionary<string, string> { ["phase"] = "d-exp-source-chunk", ["offset"] = offset.ToString() });
                        var bytes = Convert.FromBase64String(part["data"]);
                        Require(bytes.Length == Math.Min(262144, length - offset), "source D chunk length");
                        Buffer.BlockCopy(bytes, 0, actualDBytes, offset, bytes.Length);
                    }
                    Require(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(actualDBytes)).ToLowerInvariant() == actualDHeader["sha256"],
                        "complete in-memory source D hash");
                    if (args.Contains("--probe-actual-d-suffix")) {
                        var originalEnd = long.Parse(actualDHeader["movieFrame"], System.Globalization.CultureInfo.InvariantCulture) + 32;
                        await Call(new Dictionary<string, string> { ["phase"] = "d-exp-source-resume", ["endMovie"] = originalEnd.ToString() });
                        boot.Refresh(); await movies.RunAsync(boot.NativeCompletedFrames, CancellationToken.None);
                        await Until(() => { boot.Refresh(); if (boot.FullRunFaultCode != 0) throw new InvalidOperationException("source suffix native fault " + boot.FullRunFaultCode); return boot.IsWaiting; },
                            "original source runs the same SceneLoad and bounded 32-Movie suffix", 120);
                        var originalState = await VideoRuntimeState(vm);
                        Require(originalState["movieFrame"] == originalEnd.ToString() && originalState["mismatchCount"] == "0" && originalState["error"] == "",
                            "original source suffix has exact endpoint and no input mismatch");
                        await File.WriteAllTextAsync(Path.Combine(output, "actual-d-original-suffix-state.json"), JsonSerializer.Serialize(originalState));
                        await PhysicsFollowOn();
                    }
                    if (actualDStage == "source") {
                        var packet = new ActualDRelayPacket { Data = actualDBytes, Sha256 = actualDHeader["sha256"],
                            MovieFrame = long.Parse(actualDHeader["movieFrame"], System.Globalization.CultureInfo.InvariantCulture),
                            NativeFrame = long.Parse(actualDHeader["nativeFrame"], System.Globalization.CultureInfo.InvariantCulture) };
                        await ActualDMemoryRelay.ExchangeClient(Arg("--probe-actual-d-relay"), false, packet);
                        await Field<Func<bool, Task>>(vm, "finishRestorePresentation")(true);
                        Require(sourceHash == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(source))), "source stage input package bytes unchanged");
                        Log("ACTUAL D SOURCE STAGE COMPLETE; packet transferred only through current-user memory IPC; no qualification");
                        return;
                    }
                    var sourcePid = Field<Process>(app, "startupGame").Id;
                    // This source paused before its first target load, so the
                    // normal frame-menu completion has not released its cover.
                    // Finish the App's current presentation lease before the
                    // next normal protected replacement takes another lease.
                    await Field<Func<bool, Task>>(vm, "finishRestorePresentation")(true);
                    Environment.SetEnvironmentVariable("HKTAS_LOADING_ACTUAL_D_SOURCE", null);
                    await Field<Func<Task>>(vm, "restartProtectedGame")();
                    Require(Field<Process>(app, "startupGame").Id != sourcePid, "actual D target is another fresh process");
                    movies.ArmReplay(zeroMovie, 0); await movies.RunAsync(0, CancellationToken.None);
                    await Until(() => { boot.Refresh(); if (boot.FullRunFaultCode != 0) throw new InvalidOperationException("actual target bootstrap fault " + boot.FullRunFaultCode); return boot.IsWaiting; },
                        "actual target original bootstrap pauses before any Movie sample", 120);
                    await Until(() => vm.SelectedSession?.Client.IsConnected == true, "actual D replacement Runtime connected", 60);
                    AtFrame(vm, boot, 0, "actual D target remains Movie zero");
                }
                if (actualDStage == "target") {
                    var packet = await ActualDMemoryRelay.ExchangeClient(Arg("--probe-actual-d-relay"), true, null!);
                    actualDBytes = packet.Data;
                    actualDHeader = new Dictionary<string, string> { ["length"] = packet.Data.Length.ToString(), ["sha256"] = packet.Sha256,
                        ["movieFrame"] = packet.MovieFrame.ToString(), ["nativeFrame"] = packet.NativeFrame.ToString() };
                    await File.WriteAllTextAsync(Path.Combine(output, "actual-d-source-header.json"), JsonSerializer.Serialize(actualDHeader));
                    Require(actualDBytes.Length > 0 && actualDBytes.Length <= 32*1024*1024, "target stage receives bounded source D only through memory IPC");
                }
                var start = await Call(new Dictionary<string, string> { ["phase"] = "s0-start",
                    ["slot"] = Arg("--probe-s0-slot"), ["target"] = Arg("--probe-s0-target") });
                Require(start["movieFrame"] == "0" && start["consumedSamplesBefore"] == "0" && start["consumedSamplesAfter"] == "0",
                    "S0 preparation begins with real zero Movie and consumed sample counters");
                boot.Refresh();
                await movies.RunAsync(boot.NativeCompletedFrames, CancellationToken.None);
                await Until(() => { boot.Refresh(); if (boot.FullRunFaultCode != 0) throw new InvalidOperationException("S0 native fault " + boot.FullRunFaultCode); return boot.IsWaiting; },
                    "zero Movie preparation parks at arbitrary canonical B", 240);
                var receipt = await Call(new Dictionary<string, string> { ["phase"] = "s0-status" });
                Require(receipt["status"] == "PausedAtB" && receipt["movieFrame"] == "0"
                    && receipt["consumedSamplesAfter"] == receipt["consumedSamplesBefore"] && receipt["consumedSamplesAfter"] == "0"
                    && receipt["d1Passed"] == "false" && receipt["restoreQualified"] == "false",
                    "real zero Movie and sample counters preserved, no D applied or checkpoint qualified");
                await CheckSerialPauseAsync(vm, boot);
                await File.WriteAllTextAsync(Path.Combine(output, "s0-receipt.json"), JsonSerializer.Serialize(receipt));
                if (actualDBytes != null && actualDHeader != null) {
                    await Call(new Dictionary<string, string> { ["phase"] = "d-exp-import-begin", ["length"] = actualDBytes.Length.ToString(), ["sha256"] = actualDHeader["sha256"] });
                    for (var offset = 0; offset < actualDBytes.Length; offset += 262144)
                        await Call(new Dictionary<string, string> { ["phase"] = "d-exp-import-chunk", ["offset"] = offset.ToString(),
                            ["data"] = Convert.ToBase64String(actualDBytes, offset, Math.Min(262144, actualDBytes.Length - offset)) });
                    await Call(new Dictionary<string, string> { ["phase"] = "d-exp-import-finish" });
                    var result = await Call(new Dictionary<string, string> { ["phase"] = "d-exp-apply" });
                    Require(result["status"] == "ActualDAppliedPaused" && result["movieFrame"] == actualDHeader["movieFrame"]
                        && result["consumedSamples"] == "0" && result["restoreQualified"] == "false", "actual source D applied with raw readback and no consumed target Movie samples");
                    boot.Refresh(); var actualStopped = boot.NativeCompletedFrames; await Task.Delay(400); boot.Refresh();
                    Require(boot.IsWaiting && boot.NativeCompletedFrames == actualStopped && boot.FullRunFaultCode == 0,
                        "actual complete managed D application remains parked without PlayerLoop");
                    await File.WriteAllTextAsync(Path.Combine(output, "actual-d-receipt.json"), JsonSerializer.Serialize(new {
                        source = actualDHeader, result, diagnosticOnly = true, snapshotsWrittenToDisk = false, restoreQualified = false }));
                    if (args.Contains("--probe-actual-d-suffix")) {
                        var suffixEnd = long.Parse(actualDHeader["movieFrame"], System.Globalization.CultureInfo.InvariantCulture) + 32;
                        await Call(new Dictionary<string, string> { ["phase"] = "d-exp-resume", ["endMovie"] = suffixEnd.ToString() });
                        boot.Refresh(); await movies.RunAsync(boot.NativeCompletedFrames, CancellationToken.None);
                        await Until(() => { boot.Refresh(); if (boot.FullRunFaultCode != 0) throw new InvalidOperationException("actual D suffix native fault " + boot.FullRunFaultCode); return boot.IsWaiting; },
                            "original SceneLoad and bounded source Movie suffix reach a paused endpoint", 120);
                        var suffixState = await VideoRuntimeState(vm);
                        Require(suffixState["movieFrame"] == suffixEnd.ToString() && suffixState["mismatchCount"] == "0"
                            && suffixState["error"] == "", "actual D suffix has its exact Movie endpoint and no input mismatch");
                        await File.WriteAllTextAsync(Path.Combine(output, "actual-d-suffix-state.json"), JsonSerializer.Serialize(suffixState));
                        await PhysicsFollowOn();
                    }
                }
                if (clockSourceArgument != null)
                {
                    var applied = await Call(new Dictionary<string, string> { ["phase"] = "s0-clock-apply" });
                    Require(applied["status"] == "ClockAppliedPaused" && applied["movieFrame"] == "0"
                        && applied["consumedSamplesAfter"] == "0" && applied["restoreQualified"] == "false",
                        "actual limited clock D write and raw readback, still zero Movie and unqualified");
                    boot.Refresh();
                    var stopped = boot.NativeCompletedFrames;
                    await Task.Delay(400); boot.Refresh();
                    Require(boot.IsWaiting && boot.NativeCompletedFrames == stopped && boot.FullRunFaultCode == 0,
                        "clock D application stays natively parked without advancing any frame");
                    await File.WriteAllTextAsync(Path.Combine(output, "clock-d-receipt.json"), JsonSerializer.Serialize(applied));
                    if (args.Contains("--probe-clock-recanon")) {
                        var rebound = await Call(new Dictionary<string, string> { ["phase"] = "s0-clock-recanon" });
                        Require(rebound["status"] == "RecanonicalizedClockPaused" && rebound["movieFrame"] == "0"
                            && rebound["consumedSamplesAfter"] == "0" && rebound["restoreQualified"] == "false",
                            "same actual Canon rebinds S0 against imported clock without changing clock D");
                        boot.Refresh(); stopped = boot.NativeCompletedFrames; await Task.Delay(400); boot.Refresh();
                        Require(boot.IsWaiting && boot.NativeCompletedFrames == stopped && boot.FullRunFaultCode == 0,
                            "rebound Canon remains at its frozen original B without PlayerLoop");
                        await File.WriteAllTextAsync(Path.Combine(output, "clock-canon-receipt.json"), JsonSerializer.Serialize(rebound));
                    }
                }
                await File.WriteAllTextAsync(Path.Combine(output, "target-state.json"), JsonSerializer.Serialize(await VideoRuntimeState(vm)));
                Require(sourceHash == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(source))), "input package bytes unchanged");
                Log("S0 OBSERVATION COMPLETE; D1/D2/D3 not qualified");
                return;
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
        finally {
            if (actualDBytes != null) Array.Clear(actualDBytes, 0, actualDBytes.Length);
            movies.VerifyOriginalSavesUnchanged();
        }
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
