using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HollowKnightTAS.Companion.Controls;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.ViewModels;
using HollowKnightTAS.Core.Movie;

internal static partial class StudioScenarioHarness
{
    static async Task RunBossFsmAsync(string[] args)
    {
        var vm = (MainViewModel)app.MainWindow.DataContext;
        var boot = Field<StartupBootController>(app, "startupBoot");
        var movies = Field<FullRunMovieCoordinator>(app, "fullRunMovies");
        void Set(string name, object value) => typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);
        Set("worldlines", new StudioTimelineStore(Path.Combine(output, "timelines.json")));
        Set("initialSaveCacheRoot", Path.Combine(output, "initial-saves")); Set("activeSequenceDirectory", output);
        var request = typeof(MainViewModel).GetMethod("RequestRuntimeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        async Task<string> Snapshot(string label, string view, string[]? targets = null)
        {
            boot.Refresh(); long before = boot.NativeCompletedFrames;
            var fields = new Dictionary<string, string> { ["requestId"] = "fsm-live-" + Guid.NewGuid().ToString("N"), ["view"] = view };
            if (targets != null) fields["watches"] = JsonSerializer.Serialize(targets);
            var result = await (Task<IReadOnlyDictionary<string, string>>)request.Invoke(vm, new object[] { vm.SelectedSession!.Client, fields, CancellationToken.None })!;
            await File.WriteAllTextAsync(Path.Combine(output, label + ".json"), result["snapshotJson"]);
            boot.Refresh(); Require(boot.IsWaiting && boot.NativeCompletedFrames == before, label + " paused query does not advance native frame");
            return result["snapshotJson"];
        }
        await Field<Func<string, Task>>(vm, "launchGame")(@"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe");
        Execute(vm.NewFullRunMovieCommand); await Command(vm.StepCommand);
        await Until(() => vm.SelectedSession?.Client.IsConnected == true, "FSM Runtime connected", 120);
        await vm.PollInputGridProgressAsync(); var observed = await VideoRuntimeMovie(vm, movies);
        var baseline = SequencePackage.Read(args.Single(a => a.StartsWith("--fsm-baseline=", StringComparison.Ordinal)).Split('=', 2)[1]);
        var source = TimelineTree.Parse(baseline.Movie);
        var candidate = new MovieV2Document("boss-fsm-candidate", TimelineTree.Parse(observed.Movie).Header,
            source.Runs.Select(r => new NativeFrameRun(r.RepeatCount, r.Samples, r.Span, r.FramesPerSecond, true, r.RngSeed)));
        var path = Path.Combine(output, "candidate.hktaspack");
        await SequencePackage.WriteAsync(path, new MovieV2Codec().WriteCanonical(candidate), baseline.InitialSaves!);
        await vm.OpenMovieFileAsync(path);
        await vm.FrameMenuAsync("rebuild", 2340); AtFrame(vm, boot, 2340, "baseline reaches battle");
        var baselineInfo = InfoOverlayModel.Decode(await Snapshot("baseline-2340", "info"));
        var baselineState = await VideoRuntimeState(vm);
        await vm.FrameMenuAsync("rebuild", 2300); AtFrame(vm, boot, 2300, "boss graph start");
        var panel = (FsmViewerPanel)((TabItem)app.MainWindow.FindName("FsmViewerTab")).Content;
        panel.DataContext = vm;
        panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var controller = Field<FsmViewerController>(panel, "controller");
        await Until(() => controller.Objects.Any(o => o.Enemy), "enemy catalog available");
        var boss = controller.Objects.FirstOrDefault(o => o.Enemy && o.Active && o.Path.Contains("Marmu", StringComparison.OrdinalIgnoreCase))
            ?? controller.Objects.First(o => o.Enemy && o.Active);
        Log("BOSS " + boss);
        ((ComboBox)panel.FindName("Objects")).SelectedItem = boss;
        typeof(FsmViewerPanel).GetMethod("AddObject", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, new object[] { panel, new RoutedEventArgs() });
        // A second machine exercises simultaneous display even for bosses with one attached FSM.
        var additional = controller.Targets.FirstOrDefault(t => !t.Selected && t.Path.Trim('/') == "Knight" && t.Name == "Spell Control");
        if (additional != null) additional.Selected = true;
        await Until(() => controller.Cards.Count >= 2 && controller.Cards.Values.All(c => c.Graph != null), "multiple FSM graphs loaded");
        var selected = controller.Targets.Where(t => t.Selected).ToArray();
        var initialIds = selected.Select(t => t.Id).ToArray();
        using var raw = JsonDocument.Parse(await Snapshot("graphs-2300", "fsms", initialIds));
        Require(controller.Cards.Values.Any(c => c.Live && c.Current.Length > 0), "live state highlight available");
        foreach (var fsm in raw.RootElement.GetProperty("fsms").EnumerateArray())
        {
            var card = controller.Cards[fsm.GetProperty("id").GetString()!];
            Require(card.Graph!.Nodes.Length == fsm.GetProperty("graph").GetProperty("states").GetArrayLength()
                && card.Current == fsm.GetProperty("activeState").GetString(), "graph and current state match runtime capture: " + card.Target.Name);
        }
        var versions = controller.Cards.Values.Select(c => c.Target.Id + "|" + c.Version).ToArray();
        using var cached = JsonDocument.Parse(await Snapshot("cached-2300", "fsms", versions));
        Require(cached.RootElement.GetProperty("fsms").EnumerateArray().All(f => !f.TryGetProperty("graph", out _)), "unchanged topology omitted from refresh");
        panel.Measure(new Size(1320, 820)); panel.Arrange(new Rect(0, 0, 1320, 820)); panel.UpdateLayout();
        await Task.Delay(150); panel.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1320, 820, 96, 96, PixelFormats.Pbgra32); bitmap.Render(panel);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(output, "boss-fsm-panel.png"))) encoder.Save(stream);
        boot.Refresh(); long paused = boot.NativeCompletedFrames; await Task.Delay(550); boot.Refresh();
        Require(boot.NativeCompletedFrames == paused && boot.IsWaiting, "five refresh intervals preserve paused frame");
        await Command(vm.StepCommand); await vm.PollInputGridProgressAsync();
        using var stepped = JsonDocument.Parse(await Snapshot("stepped-2301", "fsms", initialIds));
        Require(stepped.RootElement.GetProperty("movieFrame").GetInt64() == 2301, "step publishes next completed Movie frame");
        await Until(() => controller.Status.StartsWith("Movie 2301 "), "viewer follows stepped frame");
        await vm.FrameMenuAsync("seek", 2340); AtFrame(vm, boot, 2340, "play with viewer");
        await Until(() => controller.Status.StartsWith("Movie 2340 "), "viewer follows playback boundary");
        var observedInfo = InfoOverlayModel.Decode(await Snapshot("observed-2340", "info"));
        foreach (var key in new[]{"room","x","y","vx","vy","health","soul","dash","shade"})
            if (baselineInfo.TryGetValue(key, out var a)) Require(observedInfo.TryGetValue(key, out var b) && a.ToString() == b.ToString(), "same input with viewer off/on: " + key);
        var observedState = await VideoRuntimeState(vm);
        Require(observedState["faultCode"] == "0", "no Runtime fault");
        await vm.FrameMenuAsync("rebuild", 2300); AtFrame(vm, boot, 2300, "cold restore with viewer");
        await Until(() => controller.Cards.Count >= 2 && controller.Cards.Values.All(c => c.Graph != null && !initialIds.Contains(c.Target.Id))
            && controller.Status.StartsWith("Movie 2300 "), "rebound graphs use new instance IDs");
        Require(controller.Cards.Values.Any(c => c.Live), "restored current state highlighted");
        await vm.FrameMenuAsync("rebuild", 1500); AtFrame(vm, boot, 1500, "return to another scene");
        await Until(() => controller.Targets.All(t => t.ObjectId != boss.Id) && controller.Cards.Values.All(c => c.Target.Path != boss.Path), "old boss graphs discarded after scene change");
        panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Require(Field<FsmViewerController?>(panel, "controller") == null, "leaving tab disposes polling controller");
        movies.VerifyOriginalSavesUnchanged();
    }
}
