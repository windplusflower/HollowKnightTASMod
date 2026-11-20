using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Interop;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.ViewModels;

internal static partial class StudioScenarioHarness
{
    static async Task RunOverlayStackingAsync()
    {
        var vm = (MainViewModel)app.MainWindow.DataContext;
        var boot = Field<StartupBootController>(app, "startupBoot");
        typeof(MainViewModel).GetField("activeSequenceDirectory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(vm, output);
        typeof(MainViewModel).GetField("worldlines", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(vm, new StudioTimelineStore(System.IO.Path.Combine(output, "timelines.json")));
        typeof(MainViewModel).GetField("activeAutoSaveSeconds", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(vm, 0);
        await Field<Func<string, Task>>(vm, "launchGame")(@"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe");
        vm.NewFullRunMovieCommand.Execute(null);
        await Command(vm.StepCommand);
        await Until(() => vm.SelectedSession?.Client.IsConnected == true, "Runtime connected", 120);
        await vm.FrameMenuAsync("seek", 120);
        var game = Field<Process>(app, "startupGame"); game.Refresh();
        var owner = game.MainWindowHandle;
        Require(owner != IntPtr.Zero, "real Unity window found");
        app.MainWindow.Show();
        var studio = new WindowInteropHelper(app.MainWindow).Handle;
        var controller = Field<InfoOverlayController>(vm, "infoController");
        vm.InfoSettings.Enabled = true;
        await Until(() => Field<InfoOverlayWindow?>(controller, "window")?.IsVisible == true, "real polling overlay visible");
        var info = Field<InfoOverlayWindow>(controller, "window");
        var infoHandle = new WindowInteropHelper(info).Handle;
        var collider = new ColliderOverlayWindow();
        try
        {
            collider.SetOwner(owner); collider.SetBounds(100, 100, 500, 300, 96); collider.ShowOverlay();
            var colliderHandle = new WindowInteropHelper(collider).Handle;
            app.MainWindow.Activate();
            await Task.Delay(200);
            Require(StackForeground() == studio, "Studio has actual foreground focus");
            var frame = boot.NativeCompletedFrames;
            for (var i = 0; i < 20; i++)
            {
                collider.ShowOverlay();
                await Task.Delay(100);
                CheckStack(studio, owner, "Studio above game while both overlays refresh");
                CheckStack(studio, infoHandle, "Studio above information overlay");
                CheckStack(studio, colliderHandle, "Studio above collider overlay");
                Require(StackForeground() == studio, "foreground stays Studio");
            }
            vm.InfoSettings.Enabled = false; collider.Hide();
            await Task.Delay(200);
            vm.InfoSettings.Enabled = true; collider.ShowOverlay();
            await Until(() => info.IsVisible, "information overlay re-enabled");
            await Task.Delay(300);
            CheckStack(studio, owner, "re-enabling does not raise game");
            CheckStack(studio, infoHandle, "re-enabled information remains below Studio");
            CheckStack(studio, colliderHandle, "re-enabled colliders remain below Studio");
            Require(StackForeground() == studio, "re-enabling preserves foreground");
            CheckStack(infoHandle, owner, "information remains above game");
            CheckStack(colliderHandle, owner, "colliders remain above game");
            boot.Refresh();
            Require(boot.NativeCompletedFrames == frame && boot.IsWaiting && boot.FullRunFaultCode == 0, "display test preserves paused frame and fault zero");
            Field<FullRunMovieCoordinator>(app, "fullRunMovies").VerifyOriginalSavesUnchanged();
        }
        finally { collider.Close(); vm.InfoSettings.Enabled = false; }
    }

    static void CheckStack(IntPtr above, IntPtr below, string label)
    {
        for (var window = StackWindow(above, 2); window != IntPtr.Zero; window = StackWindow(window, 2))
            if (window == below) { Log("PASS " + label); return; }
        throw new Exception(label);
    }
    [DllImport("user32.dll", EntryPoint = "GetWindow")] static extern IntPtr StackWindow(IntPtr window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] static extern IntPtr StackForeground();
}
