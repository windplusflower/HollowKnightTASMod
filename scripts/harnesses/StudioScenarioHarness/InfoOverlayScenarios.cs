using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.ViewModels;
using HollowKnightTAS.Core.Movie;

internal static partial class StudioScenarioHarness
{
    static async Task RunInfoOverlayAsync(string[] args)
    {
        var advanced = args.Contains("--info-custom");
        var timing = args.Contains("--info-timing");
        var expressions = advanced ? new[] { "hero.dashCooldownTimer", "player.geo", "position.x", "game.gameState",
            "component(\"/Knight\", \"HeroController\").jump_steps", "fsm(\"/Knight\", \"Spell Control\", \"MP Cost\")", "hero.noSuchField" } : Array.Empty<string>();
        if (timing) expressions = new[] { "rt - 12.5", "gt - 1.25", "x + 2", "hero.dashCooldownTimer * 1000" };
        var vm = (MainViewModel)app.MainWindow.DataContext;
        var boot = Field<StartupBootController>(app, "startupBoot");
        var movies = Field<FullRunMovieCoordinator>(app, "fullRunMovies");
        typeof(MainViewModel).GetField("worldlines", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, new StudioTimelineStore(Path.Combine(output, "timelines.json")));
        typeof(MainViewModel).GetField("initialSaveCacheRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, Path.Combine(output, "initial-saves"));
        typeof(MainViewModel).GetField("activeSequenceDirectory", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, output);
        var controller = Field<InfoOverlayController>(vm, "infoController");
        async Task<IReadOnlyDictionary<string, JsonElement>> Snapshot(string label)
        {
            var before = boot.NativeCompletedFrames;
            var request = typeof(MainViewModel).GetMethod("RequestRuntimeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var fields = new Dictionary<string, string> { ["requestId"] = "info-test-" + Guid.NewGuid().ToString("N"), ["view"] = "info",
                ["watches"] = JsonSerializer.Serialize(expressions) };
            var task = (Task<IReadOnlyDictionary<string, string>>)request.Invoke(vm, new object[] { vm.SelectedSession!.Client, fields, CancellationToken.None })!;
            var result = await task;
            await File.WriteAllTextAsync(Path.Combine(output, label + ".json"), result["snapshotJson"]);
            boot.Refresh(); Require(boot.NativeCompletedFrames == before && boot.IsWaiting, label + " observation does not advance paused native frame");
            return InfoOverlayModel.Decode(result["snapshotJson"]);
        }
        await Field<Func<string, Task>>(vm, "launchGame")(@"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe");
        Execute(vm.NewFullRunMovieCommand);
        await Command(vm.StepCommand);
        await Until(() => vm.SelectedSession?.Client.IsConnected == true, "info Runtime connected", 120);
        await vm.PollInputGridProgressAsync();
        var observed = await VideoRuntimeMovie(vm, movies);
        if (args.Contains("--info-video-only")) { await RunInfoVideoAsync(vm, observed.Movie, args); movies.VerifyOriginalSavesUnchanged(); return; }
        var title = await Snapshot("title");
        Require(!title.ContainsKey("x"), "title has no stale hero coordinates");
        var baselinePath = args.Single(a => a.StartsWith("--info-baseline=", StringComparison.Ordinal)).Split('=', 2)[1];
        var baseline = SequencePackage.Read(baselinePath);
        var saveRoot = args.SingleOrDefault(a => a.StartsWith("--info-initial-saves="))?.Split('=', 2)[1];
        if (saveRoot != null)
            baseline = baseline with { InitialSaves = new InitialSaveSnapshot(Directory.GetFiles(saveRoot, "user*")
                .Select(path => new KeyValuePair<string, byte[]>(Path.GetFileName(path), File.ReadAllBytes(path)))) };
        var source = TimelineTree.Parse(baseline.Movie);
        var candidate = new MovieV2Document("info-display-candidate", TimelineTree.Parse(observed.Movie).Header,
            source.Runs.Select(r => new NativeFrameRun(r.RepeatCount, r.Samples, r.Span, r.FramesPerSecond, true, r.RngSeed)));
        var candidatePath = Path.Combine(output, "info-candidate.hktaspack");
        await SequencePackage.WriteAsync(candidatePath, new MovieV2Codec().WriteCanonical(candidate), baseline.InitialSaves!);
        await vm.OpenMovieFileAsync(candidatePath);
        if (args.Contains("--info-timing-trace-only"))
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var game = Field<System.Diagnostics.Process>(app, "startupGame"); game.Refresh();
                if (InfoIsIconic(game.MainWindowHandle)) { InfoShowWindow(game.MainWindowHandle, 4); await Task.Delay(200); }
                await vm.FrameMenuAsync("rebuild", 1500);
                AtFrame(vm, boot, 1500, "timing trace replay " + attempt);
                await Snapshot("timing-replay-" + attempt);
                if (args.Contains("--info-timing-resume-between"))
                {
                    await Command(vm.StepCommand);
                    await vm.FrameMenuAsync("seek", 1520);
                    AtFrame(vm, boot, 1520, "timing replay resumes before next cold rebuild");
                }
            }
            movies.VerifyOriginalSavesUnchanged();
            return;
        }
        await vm.FrameMenuAsync("rebuild", 1500);
        AtFrame(vm, boot, 1500, "gameplay reached under new Runtime identity");
        var values = await Snapshot("gameplay-1500");
        Require(values.ContainsKey("x") && values.ContainsKey("dash") && values.ContainsKey("shade"), "hero coordinates and both cooldowns present");
        await Until(() => Field<InfoOverlayWindow?>(controller, "window")?.IsVisible == true, "overlay visibly attached to game");
        var overlay = Field<InfoOverlayWindow>(controller, "window");
        if (advanced)
        {
            Require(vm.InfoSettings.Anchor == "右上" && vm.InfoSettings.MarginX == 8 && vm.InfoSettings.MarginY == 8, "default panel uses top-right corner");
            Require(values["watch:position.x"].GetDouble() == values["x"].GetDouble(), "custom vector component matches preset coordinate");
            Require(values["watch:hero.dashCooldownTimer"].ValueKind == JsonValueKind.Number, "custom private field available");
            Require(values["watch:player.geo"].ValueKind == JsonValueKind.Number, "custom PlayerData field available");
            Require(values["watch:game.gameState"].GetString() == "PLAYING", "custom GameManager enum available");
            Require(values["watch:component(\"/Knight\", \"HeroController\").jump_steps"].ValueKind == JsonValueKind.Number, "explicit component query available");
            Require(values["watch:fsm(\"/Knight\", \"Spell Control\", \"MP Cost\")"].GetInt32() == 33, "FSM variable read from backing fields");
            Require(values["watch:hero.noSuchField"].ValueKind == JsonValueKind.Null, "one invalid field does not fail other rows");
            vm.AddInfoItem(InfoOverlayModel.Fields.Single(f => f.Id == "custom"));
            vm.InfoSettings.Items.Last().Expression = "player.geo";
            vm.InfoSettings.Items.Last().Label = "Geo";
            vm.InfoSettings.Items.Last().Precision = 0;
            await Task.Delay(600);
            Require(InfoOverlaySettings.Load(Path.Combine(output, "info-settings.json")).Items.Last().Expression == "player.geo", "custom expression persists through real VM");
            await MoveInfoGameWhileObservationsWait(vm, boot, overlay);
        }
        if (timing)
        {
            Require(values["rt"].GetDouble() > values["gt"].GetDouble() && values["gt"].GetDouble() > 0, "RT includes loading and GT advances during gameplay");
            Require(Math.Abs(values["watch:rt - 12.5"].GetDouble() - (values["rt"].GetDouble() - 12.5)) < 1e-8, "RT expression offset evaluated in live Runtime");
            Require(Math.Abs(values["watch:x + 2"].GetDouble() - values["x"].GetDouble() - 2) < 1e-5, "preset expression arithmetic evaluated in live Runtime");
            vm.InfoSettings.Items.Clear();
            foreach (var id in new[] { "frame", "rt", "gt", "x" }) vm.AddInfoItem(InfoOverlayModel.Fields.Single(f => f.Id == id));
            vm.InfoSettings.Items[1].Expression = "rt - 12.5";
            vm.InfoSettings.Items[1].Label = "RT offset";
            vm.InfoSettings.Items[3].Expression = "x + 2";
            vm.InfoSettings.Items[3].Label = "X plus 2";
            await Task.Delay(600);
        }
        var pausedNative = boot.NativeCompletedFrames;
        await Task.Delay(550);
        boot.Refresh(); Require(boot.NativeCompletedFrames == pausedNative, "automatic overlay polling leaves game paused");
        var canvas = (Canvas)overlay.Content; canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(canvas.ActualWidth), (int)Math.Ceiling(canvas.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(output, "live-overlay.png"))) encoder.Save(stream);
        vm.InfoSettings.Enabled = false;
        await Until(() => !overlay.IsVisible, "toggle hides overlay");
        vm.InfoSettings.Enabled = true;
        await Until(() => overlay.IsVisible, "toggle restores overlay");
        await Command(vm.StepCommand); await vm.PollInputGridProgressAsync();
        var stepped = await Snapshot("gameplay-1501");
        Require(stepped["frame"].GetInt64() == 1501, "step publishes next Movie frame");
        if (timing)
        {
            Require(Math.Abs(stepped["rt"].GetDouble() - values["rt"].GetDouble() - .02) < 1e-7, "one 50 FPS gameplay step adds 20ms RT");
            Require(Math.Abs(stepped["gt"].GetDouble() - values["gt"].GetDouble() - .02) < 1e-7, "one gameplay step adds 20ms GT");
        }
        vm.InfoSettings.Items[0].Label = "Test frame"; vm.InfoSettings.Items.Move(0, 2);
        vm.InfoSettings.Anchor = "右下"; vm.InfoSettings.FontSize = 18;
        await Task.Delay(600);
        var stored = InfoOverlaySettings.Load(Path.Combine(output, "info-settings.json"));
        Require(stored.Items[2].Label == "Test frame" && stored.Anchor == "右下", "settings and order persist through real VM");
        await vm.FrameMenuAsync("seek", 1520);
        AtFrame(vm, boot, 1520, "playback advances with overlay enabled");
        await Snapshot("gameplay-1520");
        await vm.FrameMenuAsync("rebuild", 1500);
        AtFrame(vm, boot, 1500, "cold restore reconnects overlay");
        var restored = await Snapshot("restored-1500");
        foreach (var key in new[] { "room", "x", "y", "dash", "shade", "health", "soul" })
            Require(restored[key].ToString() == values[key].ToString(), "restored display matches " + key);
        await Until(() => overlay.IsVisible, "overlay visible after restore");
        if (timing)
        {
            Log("TIMING REPLAY " + JsonSerializer.Serialize(new { beforeRt = values["rt"].GetDouble(), afterRt = restored["rt"].GetDouble(), beforeGt = values["gt"].GetDouble(), afterGt = restored["gt"].GetDouble() }));
            Require(restored["gt"].GetDouble() > 0 && restored["gt"].GetDouble() <= restored["rt"].GetDouble(), "restored GT remains positive and bounded by RT");
            await RunInfoVideoAsync(vm, observed.Movie, args);
        }
        movies.VerifyOriginalSavesUnchanged();
        Require(boot.FullRunFaultCode == 0, "no native fault after display controls");
    }

    static async Task RunInfoVideoAsync(MainViewModel vm, string observedMovie, string[] args)
    {
        if (args.Contains("--info-video-only"))
        {
            vm.InfoSettings.Items.Clear();
            foreach (var id in new[] { "frame", "rt", "gt" }) vm.AddInfoItem(InfoOverlayModel.Fields.Single(f => f.Id == id));
        }
            var picker = typeof(MainViewModel).GetField("videoExportFilePicker", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var original = picker.GetValue(vm);
            var ffmpeg = args.Single(a => a.StartsWith("--ffmpeg=")).Split('=', 2)[1];
            // Export a short real-game title sequence through the same VM command as the button.
            var neutral = new MovieV2Document("info-video", TimelineTree.Parse(observedMovie).Header,
                new[] { new NativeFrameRun(30, Array.Empty<HollowKnightTAS.Core.Movie.GameInputSample>(), new MovieSourceSpan("info-video", 1, 1, 1), 50, true) });
            var neutralPath = Path.Combine(output, "info-video.hktas");
            await File.WriteAllTextAsync(neutralPath, new MovieV2Codec().WriteCanonical(neutral));
            await vm.OpenMovieFileAsync(neutralPath);
            try
            {
                foreach (var include in new[] { true, false })
                {
                    vm.InfoSettings.IncludeInVideo = include;
                    vm.InfoSettings.Enabled = !include;
                    var target = Path.Combine(output, include ? "info-on.mp4" : "info-off.mp4");
                    picker.SetValue(vm, (Func<(string Ffmpeg, string Output)?>)(() => (ffmpeg, target)));
                    await Command(vm.StartVideoExportCommand).WaitAsync(TimeSpan.FromMinutes(2));
                    var state = await VideoRuntimeState(vm);
                    Log("VIDEO STATUS " + vm.VideoExportStatus + " " + JsonSerializer.Serialize(state));
                    Require(state.GetValueOrDefault("videoExport.state") == "Completed" && File.Exists(target), "real MP4 export completes with info=" + include);
                    await File.WriteAllTextAsync(Path.Combine(output, "video-" + include + ".json"), JsonSerializer.Serialize(state));
                }
            }
            finally { picker.SetValue(vm, original); }
    }

    static async Task MoveInfoGameWhileObservationsWait(MainViewModel vm, StartupBootController boot, InfoOverlayWindow overlay)
    {
        var game = Field<System.Diagnostics.Process>(app, "startupGame"); game.Refresh(); var hwnd = game.MainWindowHandle;
        var overlayHandle = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
        GetInfoWindowRect(hwnd, out var before);
        var nativeFrame = boot.NativeCompletedFrames;
        // Exercise Windows' actual modal move loop while the Runtime cannot service observations.
        PostInfoMessage(hwnd, 0x0112, new IntPtr(0xF010), IntPtr.Zero); // WM_SYSCOMMAND / SC_MOVE
        try
        {
            PostInfoMessage(hwnd, 0x0100, new IntPtr(0x27), IntPtr.Zero); // select move direction
            await Until(() => overlay.IsOwnerMoving, "native window entered modal move loop", 5);
            await Task.Delay(2300); // longer than the observation timeout
            for (var i = 0; i < 4; i++)
            {
                // Posted keyboard messages enter the system move loop but do not reliably
                // move Unity's window. Change its geometry while that real loop stays open.
                Require(SetInfoWindowPos(hwnd, IntPtr.Zero, before.Left + (i + 1) * 19,
                    before.Top + (i + 1) * 11, 0, 0, 0x4015), "request game geometry during modal move");
                await Task.Delay(100);
                GetInfoClientRect(hwnd, out var client); var origin = new InfoPoint(); InfoClientToScreen(hwnd, ref origin);
                GetInfoWindowRect(overlayHandle, out var shown);
                Require(overlay.IsVisible && shown.Left == origin.X && shown.Top == origin.Y
                    && shown.Right - shown.Left == client.Right && shown.Bottom - shown.Top == client.Bottom,
                    "overlay remains aligned before releasing move " + i);
            }
            GetInfoWindowRect(hwnd, out var moved);
            Require(moved.Left != before.Left || moved.Top != before.Top, "game changed position during modal move");
            boot.Refresh(); Require(boot.NativeCompletedFrames == nativeFrame, "moving window did not advance native game frame");
        }
        finally { PostInfoMessage(hwnd, 0x0100, new IntPtr(0x0D), IntPtr.Zero); PostInfoMessage(hwnd, 0x0101, new IntPtr(0x0D), IntPtr.Zero); }
        await Until(() => !overlay.IsOwnerMoving, "native move loop finished", 5);
    }
    [StructLayout(LayoutKind.Sequential)] struct InfoRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct InfoPoint { public int X, Y; }
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] static extern bool PostInfoMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")] static extern bool GetInfoWindowRect(IntPtr window, out InfoRect bounds);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")] static extern bool SetInfoWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetClientRect")] static extern bool GetInfoClientRect(IntPtr window, out InfoRect bounds);
    [DllImport("user32.dll", EntryPoint = "ClientToScreen")] static extern bool InfoClientToScreen(IntPtr window, ref InfoPoint point);
    [DllImport("user32.dll", EntryPoint = "IsIconic")] static extern bool InfoIsIconic(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "ShowWindow")] static extern bool InfoShowWindow(IntPtr window, int command);
}
