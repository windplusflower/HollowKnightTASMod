using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.ViewModels;

internal static partial class StudioScenarioHarness
{
    static async Task RunReplayPerformanceAsync(string[] args)
    {
        var vm = (MainViewModel)app.MainWindow.DataContext;
        var boot = Field<StartupBootController>(app, "startupBoot");
        var movies = Field<FullRunMovieCoordinator>(app, "fullRunMovies");
        void Set(string name, object value) => typeof(MainViewModel).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);
        Set("worldlines", new StudioTimelineStore(Path.Combine(output, "timelines.json")));
        Set("initialSaveCacheRoot", Path.Combine(output, "initial-saves"));
        Set("activeSequenceDirectory", output);
        await Field<Func<string, Task>>(vm, "launchGame")(@"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe");
        Execute(vm.NewFullRunMovieCommand);
        await Command(vm.StepCommand);
        await Until(() => vm.SelectedSession?.Client.IsConnected == true, "performance Runtime connected", 120);
        var path = args.Single(a => a.StartsWith("--performance-movie=")).Split('=', 2)[1];
        await vm.OpenMovieFileAsync(path);
        var target = long.Parse(args.Single(a => a.StartsWith("--performance-frame=")).Split('=', 2)[1]);
        var originalPid = Field<Process>(app, "startupGame").Id;
        var watch = Stopwatch.StartNew();
        await vm.FrameMenuAsync("rebuild", target);
        AtFrame(vm, boot, target, "performance restore exact frame");
        Require(Field<Process>(app, "startupGame").Id != originalPid, "performance restore replaced process");
        Log("RESTORE milliseconds=" + watch.Elapsed.TotalMilliseconds);
        var state = await VideoRuntimeState(vm);
        await File.WriteAllTextAsync(Path.Combine(output, "target-state.json"), System.Text.Json.JsonSerializer.Serialize(state));
        var before = boot.NativeCompletedFrames;
        await Task.Delay(250);
        boot.Refresh();
        Require(boot.IsWaiting && boot.NativeCompletedFrames == before, "target remains paused");
        movies.VerifyOriginalSavesUnchanged();
        File.WriteAllText(Path.Combine(output, "shadow-root.txt"), movies.ShadowRoot);
        foreach (var trace in Directory.GetFiles(movies.ShadowRoot, "boss-trace.csv", SearchOption.AllDirectories))
            File.Copy(trace, Path.Combine(output, "boss-trace.csv"), true);
        var capture = typeof(RestorePresentation).Assembly.GetType("HollowKnightTAS.Companion.Services.GameWindowCapture")!;
        var image = await (Task<System.Windows.Media.Imaging.BitmapSource>)capture.GetMethod("CaptureAsync")!
            .Invoke(null, new object[] { Field<Process>(app, "startupGame").MainWindowHandle })!;
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using (var stream = File.Create(Path.Combine(output, "target.png"))) encoder.Save(stream);
        var reports = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HollowKnightTAS", "performance");
        foreach (var name in new[] { "launch", "restart", "restore" })
            File.Copy(Path.Combine(reports, "last-" + name + ".json"), Path.Combine(output, name + ".json"), true);
        Require(!vm.IsRestorePresentationFrozen, "restore presentation released");
        if (args.Contains("--verify-draw-state"))
        {
            await VerifyDrawContinuationAsync(vm, boot, movies, target);
            return;
        }
        await Command(vm.StepCommand);
        await Until(() => Field<long>(vm, "currentFullRunMovieFrame") == target + 1,
            "continued Step progress notification", 10);
        AtFrame(vm, boot, target + 1, "continued Step advances exactly one frame");
        if (MovieFrames(vm.MovieText) > target + 20)
        {
            await vm.FrameMenuAsync("seek", target + 20);
            AtFrame(vm, boot, target + 20, "visible future seek reaches exact target");
            var futureImage = await (Task<System.Windows.Media.Imaging.BitmapSource>)capture.GetMethod("CaptureAsync")!
                .Invoke(null, new object[] { Field<Process>(app, "startupGame").MainWindowHandle })!;
            var futureEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            futureEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(futureImage));
            using var stream = File.Create(Path.Combine(output, "after-seek.png"));
            futureEncoder.Save(stream);
        }
        movies.VerifyOriginalSavesUnchanged();
    }
}
