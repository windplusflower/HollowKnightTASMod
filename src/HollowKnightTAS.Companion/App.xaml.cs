using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.Automation;
using HollowKnightTAS.Companion.ViewModels;
using HollowKnightTAS.Core.Automation;

namespace HollowKnightTAS.Companion
{
    public partial class App : Application
    {
        private readonly CancellationTokenSource shutdown =
            new CancellationTokenSource();
        private SingleInstanceCoordinator? singleInstance;
        private SessionRegistry? sessions;
        private ControlPipeServer? controlServer;
        private ColdRestoreSupervisor? coldRestoreSupervisor;
        private AutomationBroker? automationBroker;
        private bool shutdownWhenColdRestoreStops;
        private StartupBootController? startupBoot;
        private FullRunMovieCoordinator? fullRunMovies;
        private System.Windows.Threading.DispatcherTimer? startupBootTimer;
        private System.Diagnostics.Process? startupGame;
        private readonly RestorePresentation restorePresentation = new();
        private AutomaticStartupHandoff? automaticStartup;
        private StudioApplicationLog? applicationLog;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            applicationLog = new StudioApplicationLog(DiagnosticLogExporter.LocalRoot);
            System.Diagnostics.Trace.Listeners.Add(applicationLog);
            DispatcherUnhandledException += (_, args) => System.Diagnostics.Trace.WriteLine("Studio dispatcher exception: " + args.Exception);
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            System.Diagnostics.Trace.WriteLine("Studio started " + typeof(App).Assembly.GetName().Version);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var bootstrap =
                    await BootstrapRegistrationReader.TryReadAsync(
                        e.Args,
                        shutdown.Token);
                singleInstance = new SingleInstanceCoordinator();
                var launchArgument = e.Args.FirstOrDefault(value =>
                    value.StartsWith("--launch-game=", StringComparison.OrdinalIgnoreCase));
                if (!singleInstance.IsPrimary)
                {
                    if (bootstrap != null)
                    {
                        var forwarded =
                            await singleInstance.ForwardRegistrationAsync(
                                bootstrap,
                                TimeSpan.FromSeconds(5),
                                shutdown.Token);
                        Shutdown(forwarded ? 0 : 3);
                    }
                    else if (launchArgument != null)
                    {
                        var launched = await singleInstance.ForwardLaunchAsync(
                            launchArgument.Substring("--launch-game=".Length), shutdown.Token);
                        Shutdown(launched ? 0 : 5);
                    }
                    else
                    {
                        Shutdown(0);
                    }

                    return;
                }

                DiagnosticLogExporter.PreserveGameLogs();
                var coldRestoreRoot = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "HollowKnightTAS",
                    "cold-restore");
                var coldIdentity =
                    ColdRestoreSupervisorIdentity.LoadOrCreate(
                        Path.Combine(coldRestoreRoot, "identity"));
                sessions = new SessionRegistry(
                    coldIdentity.CompanionInstanceId);
                var coldStore = new ColdRestoreIntentStore(
                    Path.Combine(coldRestoreRoot, "store"),
                    coldIdentity.CompanionInstanceId,
                    coldIdentity.ClaimSecret);
                var launchReceiptStore = new VerifiedLaunchReceiptStore(
                    Path.Combine(coldRestoreRoot, "launch-receipts"),
                    coldIdentity.CompanionInstanceId,
                    coldIdentity.ClaimSecret);
                var recovery = coldStore.Recover();
                if (recovery.IncompleteTransactionIds.Count != 0
                    || recovery.CorruptOperationIds.Count != 0)
                {
                    Console.Error.WriteLine(
                        "ColdRestoreRecoveryAttention:transactions="
                        + recovery.IncompleteTransactionIds.Count
                        + ";corrupt="
                        + recovery.CorruptOperationIds.Count);
                }

                coldRestoreSupervisor = new ColdRestoreSupervisor(
                    sessions,
                    coldStore,
                    coldIdentity,
                    new PackagedColdRestoreLaunchEnvironmentResolver(
                        Path.Combine(
                            AppContext.BaseDirectory,
                            "ClockStartup"),
                        typeof(App).Assembly.Location,
                        launchReceiptStore));
                coldRestoreSupervisor.ActivityChanged +=
                    OnColdRestoreActivityChanged;
                controlServer = new ControlPipeServer(
                    singleInstance.ControlPipeName,
                    sessions,
                    () => (coldRestoreSupervisor == null || !coldRestoreSupervisor.IsActive)
                          && automaticStartup?.IsActive != true);
                controlServer.ExitRequested +=
                    OnControlExitRequested;
                controlServer.Start();
                if (bootstrap != null)
                {
                    if (!await sessions.RegisterAsync(
                            bootstrap,
                            shutdown.Token))
                    {
                        Console.Error.WriteLine(
                            "BootstrapRegistrationFailed:"
                            + sessions.LastRegistrationError);
                        Shutdown(4);
                        return;
                    }
                }

                startupBoot = new StartupBootController();
                fullRunMovies = new FullRunMovieCoordinator(startupBoot);
                automationBroker = new AutomationBroker(
                    sessions,
                    coldRestoreSupervisor,
                    fullRunMovies: fullRunMovies);
                {
                    startupBootTimer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(100)
                    };
                    startupBootTimer.Tick += (_, _) =>
                    {
                        if (startupGame != null && (!startupBoot.IsPending || startupGame.HasExited))
                        {
                            var gameExited = startupGame.HasExited;
                            startupGame.Dispose();
                            startupGame = null;
                            if (startupBoot.IsPending) startupBoot.Dispose();
                            if (gameExited && fullRunMovies != null)
                            {
                                try { fullRunMovies.VerifyOriginalSavesUnchanged(); }
                                catch (Exception auditFault)
                                {
                                    MainWindow?.Dispatcher.BeginInvoke(new Action(() =>
                                        (MainWindow?.DataContext as MainViewModel)?.ReportStartupStatus(
                                            "原始存档审计失败：" + auditFault.Message)));
                                }
                                fullRunMovies.ClearAfterExit();
                                automationBroker?.EndFullRunEndpoint();
                            }
                        }
                        startupBoot.Refresh();
                    };
                    startupBootTimer.Start();
                }
                Func<string, Task> launchGameAsync = async gamePath =>
                    {
                        // A controlled game always has a visible editor, including
                        // sessions started by automation from a hidden Studio host.
                        if (MainWindow != null && !MainWindow.IsVisible) MainWindow.Show();
                        using var timing = new ReplayPerformanceTrace("launch");
                        var existingGames = System.Diagnostics.Process.GetProcessesByName("hollow_knight");
                        try
                        {
                            if (existingGames.Any(process => !process.HasExited))
                                throw new InvalidOperationException("请先在游戏内正常保存并退出，然后启动 TAS 游戏。");
                        }
                        finally
                        {
                            foreach (var process in existingGames) process.Dispose();
                        }
                        var profile = await Task.Run(() => VerifiedStartupProfile.Load(
                            Path.Combine(AppContext.BaseDirectory, "ClockStartup"), gamePath), shutdown.Token);
                        timing.Mark("profile-verified");
                        var launcher = new VerifiedGameLauncher(profile, launchReceiptStore);
                        var gate = fullRunMovies?.PrepareLaunch();
                        timing.Mark("protected-saves-prepared");
                        try
                        {
                            using var handle = await launcher.LaunchInteractiveAsync(
                                "interactive-" + Guid.NewGuid().ToString("N"),
                                TimeSpan.FromSeconds(60), shutdown.Token, gate, restorePresentation.IsActive);
                            timing.Mark("injector-returned");
                            if (gate != null)
                            {
                                // Unity initialization and save-guard setup can outlast the
                                // injector. A slow first PlayerLoop is not a capability failure.
                                await gate.WaitForStartupAsync(() => handle.HasExited,
                                    TimeSpan.FromSeconds(60), shutdown.Token);
                                automationBroker?.BindFullRunEndpoint(gate.Token,
                                    profile.StartupProfileSha256,
                                    AutomationMode.ApprovedControl);
                                if (startupBoot!.IsPending)
                                    startupGame = System.Diagnostics.Process.GetProcessById(handle.ProcessId);
                                if (startupGame != null && restorePresentation.IsActive)
                                    restorePresentation.AttachTarget(startupGame);
                                startupBoot!.Refresh();
                                timing.Mark("native-gate-ready");
                            }
                            handle.ReleaseSupervision();
                            timing.Complete();
                        }
                        catch
                        {
                            automationBroker?.EndFullRunEndpoint();
                            startupBoot?.Dispose();
                            startupGame?.Dispose();
                            startupGame = null;
                            throw;
                        }
                    };
                var viewModel = new MainViewModel(sessions, new MovieEditorService(),
                    new CapabilityBroker(), new NativeHostLauncher(), automationBroker, launchGameAsync,
                    startupBoot, fullRunMovies, () =>
                    {
                        if (startupBoot?.IsPending != true
                            || (startupBoot.IsWaiting != true && startupBoot.FullRunFaultCode == 0)
                            || startupGame == null)
                            throw new InvalidOperationException("受控游戏尚未停在可退出的启动帧。");
                        if (!startupGame.HasExited) startupGame.Kill();
                    }, async () =>
                    {
                        using var timing = new ReplayPerformanceTrace("restart");
                        if (startupGame == null || startupBoot == null || fullRunMovies == null
                            || (!startupBoot.IsWaiting && !fullRunMovies.IsTerminal))
                            throw new InvalidOperationException("重放重启需要受控游戏停在帧边界。");
                        fullRunMovies.VerifyOriginalSavesUnchanged();
                        timing.Mark("source-save-audit");
                        var process = startupGame;
                        var path = process.MainModule?.FileName ?? throw new InvalidOperationException("游戏路径不可用。");
                        // A failed gate cannot service capture/style messages.
                        // The original saves are still checked before replacing
                        // this exact App-owned process through the normal launcher.
                        if (startupBoot.FullRunFaultCode == 0)
                            await restorePresentation.BeginAsync(process);
                        timing.Mark("source-cover-ready");
                        startupGame = null;
                        try
                        {
                            if (!process.HasExited) process.Kill();
                            await process.WaitForExitAsync();
                        }
                        finally { process.Dispose(); }
                        timing.Mark("source-exited");
                        fullRunMovies.VerifyOriginalSavesUnchanged();
                        startupBoot.Dispose();
                        fullRunMovies.ClearAfterExit();
                        automationBroker.EndFullRunEndpoint();
                        timing.Mark("source-cleanup-and-audit");
                        await launchGameAsync(path);
                        timing.Complete();
                    }, async success =>
                    {
                        try { if (success) await restorePresentation.CompleteAsync(); }
                        finally
                        {
                            if (startupBoot?.FullRunFaultCode != 0) restorePresentation.AbandonFaultedTarget();
                            else restorePresentation.Dispose();
                        }
                    });
                automaticStartup = new AutomaticStartupHandoff(sessions, Dispatcher,
                    gamePath => Task.Run(() => VerifiedStartupProfile.Load(
                        Path.Combine(AppContext.BaseDirectory, "ClockStartup"), gamePath).RequireStartupFrameGate(), shutdown.Token),
                    launchGameAsync,
                    () => coldRestoreSupervisor.IsActive || startupBoot!.IsPending,
                    viewModel.ReportStartupStatus);
                var window = new MainWindow
                {
                    DataContext = viewModel
                };
                MainWindow = window;
                controlServer.LaunchGameAsync = path => Dispatcher.InvokeAsync(() => launchGameAsync(path)).Task.Unwrap();
                window.Closed += OnMainWindowClosed;
                if (!e.Args.Any(
                        value => string.Equals(
                            value,
                            "--headless",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    window.Show();
                }
                automaticStartup.Check();

                if (launchArgument != null)
                    await launchGameAsync(launchArgument.Substring("--launch-game=".Length));

                var exitSeconds = ReadPositiveIntArgument(
                    e.Args,
                    "--exit-after-seconds=");
                if (exitSeconds > 0)
                {
                    _ = ExitAfterAsync(exitSeconds);
                }
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.WriteLine("Studio startup failed: " + exception);
                MessageBox.Show(
                    "HollowKnightTAS Companion failed to start.\n\n"
                    + exception.Message,
                    "HollowKnightTAS Companion",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(2);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            System.Diagnostics.Trace.WriteLine("Studio exit cleanup: begin");
            shutdown.Cancel();
            restorePresentation.Dispose();
            automaticStartup?.Dispose();
            startupBootTimer?.Stop();
            System.Diagnostics.Trace.WriteLine("Studio exit cleanup: owned game begin");
            // Close the exact process owned by this Studio before releasing its gate.
            // A paused Unity process cannot reliably service WM_CLOSE.
            if (startupGame != null)
            {
                try
                {
                    if (!startupGame.HasExited)
                    {
                        startupGame.Kill();
                        startupGame.WaitForExit(5000);
                    }
                }
                catch (InvalidOperationException) { /* Already exited. */ }
            }
            startupBoot?.Dispose();
            startupGame?.Dispose();
            System.Diagnostics.Trace.WriteLine("Studio exit cleanup: owned game complete");
            if (controlServer != null)
            {
                controlServer.ExitRequested -=
                    OnControlExitRequested;
            }
            if (coldRestoreSupervisor != null)
            {
                coldRestoreSupervisor.ActivityChanged -=
                    OnColdRestoreActivityChanged;
            }
            controlServer?.Dispose();
            System.Diagnostics.Trace.WriteLine("Studio exit cleanup: control server complete");
            automationBroker?.Dispose();
            System.Diagnostics.Trace.WriteLine("Studio exit cleanup: automation broker complete");
            coldRestoreSupervisor?.Dispose();
            System.Diagnostics.Trace.WriteLine("Studio exit cleanup: cold restore complete");
            sessions?.Dispose();
            singleInstance?.Dispose();
            shutdown.Dispose();
            System.Diagnostics.Trace.WriteLine("Studio exit cleanup: sessions complete; preserve logs begin");
            DiagnosticLogExporter.PreserveGameLogs();
            System.Diagnostics.Trace.WriteLine("Studio exit " + e.ApplicationExitCode);
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            if (applicationLog != null)
            {
                System.Diagnostics.Trace.Listeners.Remove(applicationLog);
                applicationLog.Dispose();
            }
            base.OnExit(e);
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
            System.Diagnostics.Trace.WriteLine("Studio unhandled exception: " + e.ExceptionObject);

        private void OnControlExitRequested(
            object? sender,
            EventArgs eventArgs)
        {
            Dispatcher.BeginInvoke(
                new Action(() => Shutdown(0)));
        }

        private void OnMainWindowClosed(
            object? sender,
            EventArgs eventArgs)
        {
            if (sender is Window window)
            {
                window.Closed -= OnMainWindowClosed;
            }

            if (coldRestoreSupervisor?.IsActive == true)
            {
                shutdownWhenColdRestoreStops = true;
                return;
            }

            Shutdown(0);
        }

        private void OnColdRestoreActivityChanged(
            object? sender,
            EventArgs eventArgs)
        {
            if (!shutdownWhenColdRestoreStops
                || coldRestoreSupervisor?.IsActive == true)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() => Shutdown(0)));
        }

        private async Task ExitAfterAsync(int seconds)
        {
            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(seconds),
                    shutdown.Token);
                Dispatcher.Invoke(
                    () =>
                    {
                        if (coldRestoreSupervisor?.IsActive == true)
                        {
                            shutdownWhenColdRestoreStops = true;
                        }
                        else
                        {
                            Shutdown(0);
                        }
                    });
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static int ReadPositiveIntArgument(
            string[] arguments,
            string prefix)
        {
            var value = arguments.FirstOrDefault(
                argument => argument.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase));
            return value != null
                   && int.TryParse(
                       value.Substring(prefix.Length),
                       out var parsed)
                   && parsed > 0
                ? parsed
                : 0;
        }

    }
}
