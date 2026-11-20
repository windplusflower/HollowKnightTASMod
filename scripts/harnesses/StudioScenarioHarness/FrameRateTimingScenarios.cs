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
    // Observe Unity's actual update clock as well as the native/video clock.
    // Neutral title-screen input; saves and sequence data use the normal shadow path.
    static async Task RunFrameRateTimingAsync(string[] args)
    {
        var vm = (MainViewModel)app.MainWindow.DataContext;
        var boot = Field<StartupBootController>(app, "startupBoot");
        var movies = Field<FullRunMovieCoordinator>(app, "fullRunMovies");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(MainViewModel).GetField(name, flags)!.SetValue(vm, value);
        Set("worldlines", new StudioTimelineStore(Path.Combine(output, "timelines.json")));
        Set("initialSaveCacheRoot", Path.Combine(output, "initial-saves"));
        Set("activeSequenceDirectory", output);
        Set("activeAutoSaveSeconds", 0);
        Set("defaultFrameRate", "50"); // Keep the user's persisted default untouched.
        var evidence = new List<object>();
        async Task<Dictionary<string, string>> State(string label)
        {
            var result = await Field<AutomationBroker>(vm, "automationBroker").ExecuteHumanAsync(
                AutomationCommandIds.FullRunStatus, AutomationScope.ObserveStatus, null, "", null, CancellationToken.None);
            Require(result.Success, label + " status available");
            var state = result.Data.ToDictionary(p => p.Key, p => p.Value);
            evidence.Add(new { label, state });
            await File.WriteAllTextAsync(Path.Combine(output, "states.json"),
                JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            Require(state["mismatchCount"] == "0" && boot.FullRunFaultCode == 0,
                label + " no native fault/input mismatch");
            return state;
        }
        double Number(Dictionary<string, string> state, string name)
            => double.Parse(state[name], CultureInfo.InvariantCulture);
        void Timing(Dictionary<string, string> state, decimal fps, string label)
        {
            var step = Number(state, "clockStepSeconds");
            Require(Math.Abs(step - 1d / (double)fps) < 0.00000011, label + " native duration");
            Require(Math.Abs(Number(state, "unityUnscaledDeltaSeconds") - step) < 0.00000011,
                label + " Unity unscaled duration matches native/video duration");
            Require(Number(state, "unityTimeScale") == 1, label + " normal game time scale");
            Require(Math.Abs(Number(state, "unityDeltaSeconds") - step) < 0.00000011,
                label + " Unity gameplay duration matches native/video duration");
            Require(Math.Abs(Number(state, "unityUpdateDeltaSeconds") - step) < 0.00000011,
                label + " GameManager Update uses the selected duration");
            Require(Number(state, "unityCaptureDeltaSeconds") == 0,
                label + " no separate capture clock overrides native gameplay timing");
            Require(Number(state, "unityTargetFrameRate") == (double)decimal.Ceiling(fps),
                label + " playback pacing follows selected FPS");
        }
        var picker = typeof(MainViewModel).GetField("videoExportFilePicker", flags)!;
        var oldPicker = picker.GetValue(vm);
        try
        {
            await Field<Func<string, Task>>(vm, "launchGame")(@"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe");
            Execute(vm.NewFullRunMovieCommand);
            await Command(vm.StepCommand);
            await Until(() => vm.SelectedSession?.Client.IsConnected == true, "timing Runtime connected", 120);
            await vm.PollInputGridProgressAsync();
            var recorded = await VideoRuntimeMovie(vm, movies);
            var source = TimelineTree.Parse(recorded.Movie);
            vm.MovieText = new MovieV2Codec().WriteCanonical(new MovieV2Document("frame-rate-timing", source.Header,
                new[] { new NativeFrameRun(235, Array.Empty<GameInputSample>(),
                    new MovieSourceSpan("timing", 1, 1, 1), 50, true) }));
            vm.RefreshGridCommand.Execute(null);
            foreach (var range in new[] { (10, 100, "100"), (110, 25, "25"), (135, 100, "99.999") })
            {
                vm.GridStart = range.Item1.ToString(CultureInfo.InvariantCulture);
                vm.GridCount = range.Item2.ToString(CultureInfo.InvariantCulture);
                vm.SelectedFrameRate = range.Item3;
                vm.SetFrameRateCommand.Execute(null);
                Require(vm.InputRows[range.Item1].FramesPerSecond == MovieFrameRate.Parse(range.Item3),
                    "Studio selection applies " + range.Item3 + " FPS");
            }
            var edited = vm.MovieText;
            var package = Path.Combine(output, "mixed-timing.hktaspack");
            await SequencePackage.WriteAsync(package, edited, movies.SequenceInitialSaves!);
            await File.WriteAllTextAsync(Path.Combine(output, "mixed-timing.hktas"), edited);
            await vm.OpenMovieFileAsync(package);
            await vm.FrameMenuAsync("rebuild", 10);
            await File.WriteAllTextAsync(Path.Combine(output, "shadow-root.txt"), movies.ShadowRoot);
            AtFrame(vm, boot, 10, "50 FPS prefix");
            var previous = await State("50 FPS");
            Timing(previous, 50, "50 FPS");
            foreach (var segment in new[] { (110L, 100m, 100), (135L, 25m, 25), (235L, 99.999m, 100) })
            {
                var label = segment.Item2.ToString(CultureInfo.InvariantCulture) + " FPS";
                var firstFrame = long.Parse(previous["movieFrame"], CultureInfo.InvariantCulture) + 1;
                await vm.FrameMenuAsync("seek", firstFrame);
                AtFrame(vm, boot, firstFrame, label + " first frame");
                Timing(await State(label + " first frame"), segment.Item2, label + " first frame");
                // Manual forward execution: compare elapsed Unity time across the segment.
                await vm.FrameMenuAsync("seek", segment.Item1);
                AtFrame(vm, boot, segment.Item1, label);
                var state = await State(label);
                Timing(state, segment.Item2, label);
                Require(Math.Abs(Number(state, "unityExecutedMovieSeconds") - Number(previous, "unityExecutedMovieSeconds")
                    - segment.Item3 / (double)segment.Item2) < 0.000002,
                    label + " simulated elapsed time equals Movie/video time");
                var native = boot.NativeCompletedFrames;
                await Task.Delay(150);
                var paused = await State(label + " paused");
                Require(boot.NativeCompletedFrames == native
                    && Number(paused, "unityTimeSeconds") == Number(state, "unityTimeSeconds"),
                    label + " paused queries do not advance Unity time");
                previous = state;
            }
            var ffmpeg = args.SingleOrDefault(a => a.StartsWith("--ffmpeg=", StringComparison.Ordinal))?.Split('=', 2)[1]
                ?? BundledFfmpeg.Resolve();
            var video = Path.Combine(output, "mixed-timing.mp4");
            picker.SetValue(vm, (Func<(string Ffmpeg, string Output)?>)(() => (ffmpeg, video)));
            await Command(vm.StartVideoExportCommand).WaitAsync(TimeSpan.FromMinutes(2));
            var export = await State("export");
            Require(export["videoExport.state"] == "Completed" && export["movieFrame"] == "235" && File.Exists(video),
                "mixed timing MP4 completed");
            Require(vm.MovieText == edited, "export preserves the Studio FPS edits");
            movies.VerifyOriginalSavesUnchanged();
        }
        finally
        {
            picker.SetValue(vm, oldPicker);
            movies.VerifyOriginalSavesUnchanged();
        }
    }
}
