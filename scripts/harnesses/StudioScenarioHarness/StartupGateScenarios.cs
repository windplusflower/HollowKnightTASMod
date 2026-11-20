using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Companion.ViewModels;
using System.Reflection;
using System.Runtime.InteropServices;

internal static partial class StudioScenarioHarness
{
    static async Task RunStartupRestoreAsync()
    {
        var vm = (MainViewModel)app.MainWindow.DataContext;
        var boot = Field<StartupBootController>(app, "startupBoot");
        var movies = Field<FullRunMovieCoordinator>(app, "fullRunMovies");
        void Set(string name, object value) => typeof(MainViewModel).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);
        Set("worldlines", new StudioTimelineStore(Path.Combine(output, "timelines.json")));
        Set("initialSaveCacheRoot", Path.Combine(output, "initial-saves"));
        Set("activeSequenceDirectory", output);
        using var monitor = new Timer(_ =>
        {
            try
            {
                var gate = Field<StartupBootGate?>(boot, "gate");
                if (gate != null) Log($"GATE ack={gate.IsAcknowledged} native={gate.NativeCompletedFrames} guard={gate.SaveGuardArmed} fault={gate.FullRunFaultCode}");
            }
            catch (ObjectDisposedException) { }
        }, null, 0, 500);
        await Field<Func<string, Task>>(vm, "launchGame")(@"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe");
        Execute(vm.NewFullRunMovieCommand);
        await Command(vm.StepCommand);
        await Until(() => vm.SelectedSession?.Client.IsConnected == true, "startup Runtime connected", 120);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await vm.FrameMenuAsync("rebuild", 0);
            AtFrame(vm, boot, 0, "real covered cold restore " + attempt);
            await Command(vm.StepCommand);
        }
        movies.VerifyOriginalSavesUnchanged();
    }

    static async Task RunStartupGateAsync(string[] args)
    {
        var run = "startup-" + Guid.NewGuid().ToString("N");
        var saves = ProtectedSaveSession.Prepare(run,
            @"C:\Users\33361\AppData\LocalLow\Team Cherry\Hollow Knight", Path.Combine(output, "shadow"));
        using var gate = new StartupBootGate(frameBased: true, fullRun: true);
        gate.SetProtectedSaveSession(saves);
        var profile = VerifiedStartupProfile.Load(Path.Combine(AppContext.BaseDirectory, "ClockStartup"),
            @"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe");
        var launcher = new VerifiedGameLauncher(profile);
        using (var handle = await launcher.LaunchInteractiveAsync(run, TimeSpan.FromSeconds(60), CancellationToken.None, gate,
            hideWindow: args.Contains("--startup-hidden")))
        {
            await File.WriteAllTextAsync(Path.Combine(output, "launcher.json"), handle.LauncherEvidence);
            var timer = Stopwatch.StartNew();
            if (args.Contains("--startup-delay"))
            {
                // Delay only this test-owned process, before its first PlayerLoop.
                // This models slow OS/I/O scheduling without changing game state.
                Require(!gate.IsAcknowledged, "slow-start simulation begins before native acknowledgement");
                var process = OpenStartupProcess(0x0800, false, handle.ProcessId);
                Require(process != IntPtr.Zero, "open owned process for startup timing test");
                var suspended = false;
                try
                {
                    Require(SuspendStartupProcess(process) == 0, "hold test startup");
                    suspended = true;
                    var wait = gate.WaitForStartupAsync(() => handle.HasExited, TimeSpan.FromSeconds(60), CancellationToken.None);
                    await Task.Delay(TimeSpan.FromSeconds(12));
                    Require(!wait.IsCompleted, "startup beyond old 10-second deadline remains pending");
                    Require(ResumeStartupProcess(process) == 0, "resume test startup");
                    suspended = false;
                    await wait;
                }
                finally
                {
                    if (suspended) ResumeStartupProcess(process);
                    CloseStartupProcess(process);
                }
            }
            while (!gate.IsAcknowledged && !handle.HasExited && timer.Elapsed < TimeSpan.FromSeconds(35))
            {
                Log($"STARTUP elapsed={timer.Elapsed.TotalSeconds:F2} native={gate.NativeCompletedFrames} guard={gate.SaveGuardArmed} fault={gate.FullRunFaultCode}");
                await Task.Delay(500);
            }
            Log($"STARTUP result elapsed={timer.Elapsed.TotalSeconds:F2} ack={gate.IsAcknowledged} waiting={gate.IsWaiting} exited={handle.HasExited} native={gate.NativeCompletedFrames} guard={gate.SaveGuardArmed} fault={gate.FullRunFaultCode}");
            Require(gate.IsAcknowledged && gate.IsWaiting && gate.NativeCompletedFrames == 0, "native startup pauses at zero");
        }
        foreach (var save in saves.OriginalSha256)
            Require(Sha256Utility.ComputeHex(File.ReadAllBytes(Path.Combine(saves.Descriptor.OriginalRoot, save.Key))) == save.Value,
                "original unchanged " + save.Key);
    }
    [DllImport("kernel32.dll", EntryPoint = "OpenProcess")] static extern IntPtr OpenStartupProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", EntryPoint = "CloseHandle")] static extern bool CloseStartupProcess(IntPtr process);
    [DllImport("ntdll.dll", EntryPoint = "NtSuspendProcess")] static extern int SuspendStartupProcess(IntPtr process);
    [DllImport("ntdll.dll", EntryPoint = "NtResumeProcess")] static extern int ResumeStartupProcess(IntPtr process);
}
