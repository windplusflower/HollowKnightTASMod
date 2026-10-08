using System;
using System.Collections.Generic;
using System.Globalization;
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

internal static partial class StudioScenarioHarness
{
    // Neutral title-screen inputs, real installed App/Runtime and protected saves.
    // Histories, initial-save cache and autosaves are confined to scenario output.
    static async Task RunFractionalFpsAsync(string[] args)
    {
        Environment.SetEnvironmentVariable("HKTAS_FULL_RUN_RNG_TRACE", "1");
        var vm = (MainViewModel)app.MainWindow.DataContext;
        var boot = Field<StartupBootController>(app, "startupBoot");
        var movies = Field<FullRunMovieCoordinator>(app, "fullRunMovies");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(MainViewModel).GetField(name, flags)!.SetValue(vm, value);
        Set("worldlines", new StudioTimelineStore(Path.Combine(output, "timelines.json")));
        Set("initialSaveCacheRoot", Path.Combine(output, "initial-saves"));
        Set("activeSequenceDirectory", output);
        Set("activeAutoSaveSeconds", 0);
        var picker = typeof(MainViewModel).GetField("videoExportFilePicker", flags)!;
        var oldPicker = picker.GetValue(vm);
        var evidence = new List<object>();
        void UnityTiming(Dictionary<string, string> state, string label)
        {
            double Number(string name) => double.Parse(state[name], CultureInfo.InvariantCulture);
            Require(Math.Abs(Number("unityUpdateDeltaSeconds") - Number("clockStepSeconds")) < 0.00000011,
                label + " gameplay Update uses the native fractional duration");
            Require(Number("unityCaptureDeltaSeconds") == 0 && Number("unityTimeScale") == 1,
                label + " native clock governs normal gameplay time");
        }
        async Task<Dictionary<string, string>> State(string label)
        {
            var result = await Field<AutomationBroker>(vm, "automationBroker").ExecuteHumanAsync(
                AutomationCommandIds.FullRunStatus, AutomationScope.ObserveStatus, null, "", null, CancellationToken.None);
            Require(result.Success, label + " status available");
            var state = result.Data.ToDictionary(p => p.Key, p => p.Value);
            Require(state["mismatchCount"] == "0" && boot.FullRunFaultCode == 0, label + " no native fault/input mismatch");
            evidence.Add(new { label, state });
            await File.WriteAllTextAsync(Path.Combine(output, "states.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            return state;
        }
        async Task<string[]> Trace(string name)
        {
            var result = await Field<AutomationBroker>(vm, "automationBroker").ExecuteHumanAsync(
                AutomationCommandIds.FullRunSnapshot, AutomationScope.MovieRead, null, "Paused", null, CancellationToken.None);
            Require(result.Success, "snapshot for trace");
            var snapshot = Path.GetFullPath(result.Data["path"]);
            Require(snapshot.StartsWith(Path.GetFullPath(movies.ShadowRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "trace inside shadow");
            var path = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(snapshot)!)!, "movie-rng-trace.csv");
            var text = await File.ReadAllTextAsync(path);
            await File.WriteAllTextAsync(Path.Combine(output, name + ".csv"), text);
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        }
        try
        {
            Set("defaultFrameRate", "99.999"); // Keep the user's persisted default untouched.
            await Field<Func<string, Task>>(vm, "launchGame")(@"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe");
            Require(boot.IsWaiting && boot.NativeCompletedFrames == 0, "protected frame zero");
            Execute(vm.NewFullRunMovieCommand);
            await Command(vm.StepCommand);
            await Until(() => vm.SelectedSession?.Client.IsConnected == true, "fractional Runtime connected", 120);
            await vm.PollInputGridProgressAsync();
            await vm.FrameMenuAsync("seek", 160);
            AtFrame(vm, boot, 160, "record 160 fractional Movie frames");
            var recorded = await VideoRuntimeMovie(vm, movies);
            var movie = TimelineTree.Parse(recorded.Movie);
            Require(recorded.Frame == 160 && movie.Runs.All(r => r.FramesPerSecond == 99.999m && !r.Authored), "strict recording preserves 99.999 in every run");
            var recordState = await State("recorded");
            Require(decimal.Parse(recordState["frameRate"], CultureInfo.InvariantCulture) == 99.999m, "Runtime reports fractional FPS");
            Require(Math.Abs(double.Parse(recordState["clockStepSeconds"], CultureInfo.InvariantCulture) - 1d / 99.999d) < 0.00000011, "actual native step reflects 99.999");
            UnityTiming(recordState, "recorded");
            var pausedNative = boot.NativeCompletedFrames;
            await Task.Delay(400);
            var paused = await State("paused");
            Require(boot.NativeCompletedFrames == pausedNative && paused["movieFrame"] == "160", "pause preserves native and Movie counts");
            if (args.Contains("--package-smoke")) await RunPackageSmokeAsync();
            var package = Path.Combine(output, "recorded-99.999.hktaspack");
            await SequencePackage.WriteAsync(package, recorded.Movie, movies.SequenceInitialSaves!);
            await File.WriteAllTextAsync(Path.Combine(output, "recorded-99.999.hktas"), recorded.Movie);
            await vm.OpenMovieFileAsync(package);
            await vm.FrameMenuAsync("rebuild", 120);
            AtFrame(vm, boot, 120, "first strict cold replay");
            UnityTiming(await State("cold-first"), "cold-first");
            var first = await Trace("cold-first");
            Require(first.Length == 240, "120 frames have before/after RNG observations");
            await vm.FrameMenuAsync("rebuild", 120);
            AtFrame(vm, boot, 120, "second strict cold replay");
            UnityTiming(await State("cold-second"), "cold-second");
            Require(first.SequenceEqual(await Trace("cold-second")), "two cold replays have identical per-frame RNG states");
            Require((await VideoRuntimeMovie(vm, movies)).Movie == recorded.Movie, "cold replay retains exact original recording");
            var runs = new[] { (99.999m, 40), (59.94m, 30), (120.125m, 30), (50m, 20) }
                .Select(pair => new NativeFrameRun(pair.Item2, Array.Empty<GameInputSample>(),
                    new MovieSourceSpan("fractional", 1, 1, 1), pair.Item1, true));
            var mixed = new MovieV2Codec().WriteCanonical(new MovieV2Document("fractional-mixed", movie.Header, runs));
            var mixedPackage = Path.Combine(output, "mixed.hktaspack");
            await SequencePackage.WriteAsync(mixedPackage, mixed, movies.SequenceInitialSaves!);
            await File.WriteAllTextAsync(Path.Combine(output, "mixed.hktas"), mixed);
            await vm.OpenMovieFileAsync(mixedPackage);
            var ffmpeg = args.SingleOrDefault(a => a.StartsWith("--ffmpeg=", StringComparison.Ordinal))?.Split('=', 2)[1]
                ?? BundledFfmpeg.Resolve();
            var video = Path.Combine(output, "mixed.mp4");
            picker.SetValue(vm, (Func<(string Ffmpeg, string Output)?>)(() => (ffmpeg, video)));
            await Command(vm.StartVideoExportCommand).WaitAsync(TimeSpan.FromMinutes(2));
            var export = await State("export");
            Require(export["videoExport.state"] == "Completed" && export["movieFrame"] == "120" && File.Exists(video), "mixed fractional export completed");
            Require(vm.MovieText == mixed, "export preserves fractional draft");
            movies.VerifyOriginalSavesUnchanged();
            Log("FRACTIONAL FPS LIVE PASSED");
        }
        finally
        {
            picker.SetValue(vm, oldPicker);
            movies.VerifyOriginalSavesUnchanged();
        }
    }
}
