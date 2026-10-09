using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Companion.Automation;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.ViewModels;
using HollowKnightTAS.Core.Automation;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Core.SceneCheckpoints;

internal static partial class StudioScenarioHarness
{
    // This deliberately uses state application IPC, never FrameMenu's cold rebuild.
    // Only metadata/receipts reach disk; the checkpoint payload stays in Studio memory.
    static async Task RunCheckpointPrototypeAsync(string[] args)
    {
        string Arg(string name) => args.Single(a => a.StartsWith(name + "=", StringComparison.Ordinal)).Split('=', 2)[1];
        var end = long.Parse(Arg("--probe-end"));
        var after = long.Parse(args.SingleOrDefault(a => a.StartsWith("--checkpoint-after=", StringComparison.Ordinal))?.Split('=', 2)[1] ?? "0");
        var source = Path.GetFullPath(Arg("--probe-movie"));
        var policies = new Dictionary<string, string>
        {
            ["HKTAS_LOADING_SYNCHRONOUS_FETCH"] = "--probe-synchronous-fetch",
            ["HKTAS_LOADING_CANONICAL_STARTUP_CLOCK"] = "--probe-canonical-startup-clock",
            ["HKTAS_LOADING_MOD_WALL_CLOCK"] = "--probe-mod-wall-clock",
            ["HKTAS_LOADING_SERIAL_SCENES"] = "--probe-serial-scenes",
            ["HKTAS_LOADING_VIRTUAL_AUDIO"] = "--probe-virtual-audio",
            ["HKTAS_LOADING_ISOLATED_ONLINE"] = "--probe-isolated-online",
            ["HKTAS_LOADING_CANONICAL_PARTICLES"] = "--probe-canonical-particles",
            ["HKTAS_LOADING_DISABLE_LEGACY_CALIBRATION"] = "--probe-disable-legacy-calibration"
        };
        foreach (var policy in policies) Environment.SetEnvironmentVariable(policy.Key, args.Contains(policy.Value) ? "1" : null);
        Environment.SetEnvironmentVariable("HKTAS_SCENE_CHECKPOINT_EXPERIMENT", "1");
        // Three frames before the trigger, the trigger, and three at K. This
        // existing passive probe hashes data without enumerating the object graph.
        var windowText = args.SingleOrDefault(value => value.StartsWith("--checkpoint-windows=", StringComparison.Ordinal))?.Split('=', 2)[1];
        var sampleEnd = string.IsNullOrEmpty(windowText) ? long.Parse(Arg("--probe-first")) + 7
            : long.Parse(windowText.Split(',').Last().Split(':')[1]);
        Require(sampleEnd <= end, "short state window lies inside the source run");
        Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE", "1");
        Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE_LIGHT", null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_GAMEPLAY_ORACLE", null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_ORACLE_WINDOWS", null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE_FIRST", Arg("--probe-first"));
        Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE_END", sampleEnd.ToString());
        Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE_WINDOWS", windowText);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE_CHECKPOINT_K", "1");
        Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE_RECORD_INPUTS", null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE_CALLS", args.Contains("--probe-random-calls") ? "1" : null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE_CALL_FIRST", args.Contains("--probe-random-calls") ? Arg("--probe-call-first") : null);
        Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE_CALL_END", args.Contains("--probe-random-calls") ? Arg("--probe-call-end") : null);
        var vm = (MainViewModel)app.MainWindow.DataContext;
        var boot = Field<StartupBootController>(app, "startupBoot");
        var movies = Field<FullRunMovieCoordinator>(app, "fullRunMovies");
        void Set(string name, object value) => typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);
        Set("worldlines", new StudioTimelineStore(Path.Combine(output, "timelines.json")));
        Set("initialSaveCacheRoot", Path.Combine(output, "initial-saves"));
        Set("activeSequenceDirectory", output);
        Set("activeAutoSaveSeconds", 0);
        var package = SequencePackage.Read(source);
        var movie = new MovieV2Codec().Parse(new StringReader(package.Movie), source).Document ?? throw new InvalidDataException("Invalid prototype Movie.");
        Require(package.InitialSaves != null && movie.Runs.All(run => !run.Authored)
            && movie.Header.NativeProfileId == MovieProtocolV2.SelectedNativeProfileId && end <= movie.Runs.Sum(run => run.RepeatCount)
            && after >= 0 && after < end,
            "prototype uses the unchanged strict Movie and its protected initial saves");
        movies.SequenceInitialSaves = package.InitialSaves;
        vm.MovieText = new MovieV2Codec().WriteCanonical(movie);
        Set("gameMouseEnabled", movie.Header.MouseEnabled); Set("defaultFrameRate", "50");
        var ownedShadows = new List<string>();
        void RememberShadow()
        {
            if (!ownedShadows.Contains(movies.ShadowRoot)) ownedShadows.Add(movies.ShadowRoot);
            Require(ownedShadows.Count <= 8, "owned shadow budget");
            File.WriteAllText(Path.Combine(output, "probe-shadows.json"), JsonSerializer.Serialize(new { diagnosticOnly = true, shadows = ownedShadows }));
        }
        void ProbeDirectory(string name)
        {
            var path = Path.Combine(output, name); Directory.CreateDirectory(path);
            Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT", path);
        }
        async Task WaitPaused(string detail)
        {
            await Until(() => { boot.Refresh(); if (boot.FullRunFaultCode != 0) throw new InvalidOperationException(detail + ": native fault=" + boot.FullRunFaultCode);
                return boot.IsWaiting || movies.Gate?.IsFullRunFinished == true; }, detail, 120);
        }
        async Task Bootstrap()
        {
            Require(boot.IsWaiting && boot.NativeCompletedFrames == 0, "replacement starts at native zero");
            movies.ArmReplay(movie, 0);
            await movies.RunAsync(0, CancellationToken.None);
            await WaitPaused("bootstrap pauses before Movie input zero");
            await Until(() => vm.SelectedSession?.Client.IsConnected == true, "prototype Runtime connected", 60);
            Require((await VideoRuntimeState(vm))["movieFrame"] == "0", "preparation has no executed Movie history");
            RememberShadow();
        }
        async Task Seek(long frame)
        {
            var result = await Field<AutomationBroker>(vm, "automationBroker").ExecuteHumanAsync(
                AutomationCommandIds.FullRunSeek, AutomationScope.ControlPlayback,
                new Dictionary<string, string> { ["targetFrame"] = frame.ToString(), ["expectedNativeFrame"] = boot.NativeCompletedFrames.ToString() },
                "Paused", null, CancellationToken.None);
            Require(result.Success, "exact Movie target accepted: " + result.Detail);
        }
        void Receipt(string file, IReadOnlyDictionary<string, string> state)
        { File.WriteAllText(Path.Combine(output, file + ".json"), JsonSerializer.Serialize(state)); Log(file + " " + JsonSerializer.Serialize(state)); }

        ProbeDirectory("source");
        await Field<Func<string, Task>>(vm, "launchGame")(@"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe");
        await Bootstrap();
        if (after > 0)
        {
            await Seek(after); await movies.RunAsync(boot.NativeCompletedFrames, CancellationToken.None);
            await WaitPaused("source reaches requested checkpoint capture interval");
            Require((await VideoRuntimeState(vm))["movieFrame"] == after.ToString(), "source exact capture selection frame");
        }
        var sourceGame = Field<Process>(app, "startupGame");
        var scope = vm.SceneCheckpoints.ActivateBranch(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        var binding = new SceneCheckpointBinding(vm.SelectedSession!.Client.EnvironmentManifestSha256,
            SceneCheckpointBinding.HashInitialSaves(movies.OriginalHashes), MovieProtocolV2.SelectedNativeProfileId);
        var capture = vm.CreateSceneCheckpointClient(vm.SelectedSession.Client);
        var lease = await capture.ArmAsync(scope, binding, SceneCheckpointTrigger.ManualNextEligibleLoadBoundary, CancellationToken.None);
        await Seek(end);
        await movies.RunAsync(boot.NativeCompletedFrames, CancellationToken.None);
        var packet = await capture.ReceiveAsync(lease, TimeSpan.FromMinutes(2), CancellationToken.None);
        Receipt("capture-header", packet.Header.ToFields());
        Require(packet.Header.CaptureMovieFrame >= after && packet.Header.ResumeMovieFrame + 3 <= end,
            "captured boundary belongs to the requested interval and has a three-frame suffix");
        await WaitPaused("source capture endpoint");
        var sourceEnd = await VideoRuntimeState(vm); Receipt("source-full-runtime", sourceEnd);
        Require(sourceEnd["movieFrame"] == end.ToString() && sourceEnd["faultCode"] == "0" && sourceEnd["mismatchCount"] == "0",
            "source exact endpoint without fault or input mismatch");
        var sourcePid = sourceGame.Id;
        ProbeDirectory("replacement");
        await Field<Func<Task>>(vm, "restartProtectedGame")();
        Require(Process.GetProcessesByName("hollow_knight").All(process => process.Id != sourcePid)
            && Field<Process>(app, "startupGame").Id != sourcePid,
            "source process exited; only the Studio-owned memory packet survives");
        await Bootstrap();
        var replacementBinding = new SceneCheckpointBinding(vm.SelectedSession!.Client.EnvironmentManifestSha256,
            SceneCheckpointBinding.HashInitialSaves(movies.OriginalHashes), MovieProtocolV2.SelectedNativeProfileId);
        Require(binding.Equals(replacementBinding), "replacement environment, initial saves and profile match");
        var restore = vm.CreateSceneCheckpointClient(vm.SelectedSession.Client);
        await restore.StageAsync(packet.Header.CheckpointId, scope, binding, CancellationToken.None);
        await restore.StartRestorePrototypeAsync(packet.Header.CheckpointId, scope, binding, CancellationToken.None);
        await movies.RunAsync(boot.NativeCompletedFrames, CancellationToken.None);
        await WaitPaused("replacement reconstructed actual C without replaying Movie history");
        Receipt("prepared-c", await restore.RestoreStatusAsync(packet.Header.CheckpointId, CancellationToken.None));
        await restore.ApplyRestorePrototypeAsync(packet.Header.CheckpointId, scope, binding, CancellationToken.None);
        boot.Refresh();
        await movies.RunAsync(boot.NativeCompletedFrames, CancellationToken.None);
        await WaitPaused("replacement reaches captured K");
        var atK = await restore.RestoreStatusAsync(packet.Header.CheckpointId, CancellationToken.None);
        Receipt("restored-k", atK);
        Require(atK["status"] == "PausedAtK" && atK["stateApplied"] == "true"
            && atK["movieFrame"] == packet.Header.ResumeMovieFrame.ToString() && atK["nativeFrame"] == packet.Header.ResumeNativeFrame.ToString()
            && atK["firstConsumedMovieFrame"] == "-1" && atK["consumedMovieFrames"] == "0", "T=K applies checkpoint and consumes zero Movie input");
        await Seek(packet.Header.ResumeMovieFrame + 3);
        await movies.RunAsync(boot.NativeCompletedFrames, CancellationToken.None);
        await WaitPaused("three-frame checkpoint suffix");
        var suffix = await restore.RestoreStatusAsync(packet.Header.CheckpointId, CancellationToken.None);
        Receipt("restored-suffix", suffix);
        var runtime = await VideoRuntimeState(vm); Receipt("restored-runtime", runtime);
        Require(suffix["firstConsumedMovieFrame"] == packet.Header.ResumeMovieFrame.ToString()
            && suffix["consumedMovieFrames"] == "3" && runtime["mismatchCount"] == "0" && runtime["error"] == "", "unchanged suffix consumes exactly [K,K+3) with no mismatch");
        if (end > packet.Header.ResumeMovieFrame + 3)
        {
            await Seek(end); await movies.RunAsync(boot.NativeCompletedFrames, CancellationToken.None);
            await WaitPaused("remaining unchanged checkpoint suffix");
            var fullSuffix = await restore.RestoreStatusAsync(packet.Header.CheckpointId, CancellationToken.None);
            Receipt("restored-full-suffix", fullSuffix);
            var endState = await VideoRuntimeState(vm); Receipt("restored-full-runtime", endState);
            Require(fullSuffix["movieFrame"] == end.ToString() && fullSuffix["consumedMovieFrames"] == (end - packet.Header.ResumeMovieFrame).ToString()
                && endState["mismatchCount"] == "0" && endState["error"] == "", "checkpoint consumes the entire unchanged [K,T) suffix");
        }
        movies.VerifyOriginalSavesUnchanged();
        Log("PROTOTYPE ONLY: world-state oracle and the >=4000-frame / >=3-transition matrix remain required; restoreQualified=false");
    }
}
