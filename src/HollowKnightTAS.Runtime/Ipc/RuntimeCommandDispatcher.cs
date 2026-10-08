using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using GlobalEnums;
using HollowKnightTAS.Core.Capabilities;
using HollowKnightTAS.Core.Automation;
using HollowKnightTAS.Core.Control;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Ipc;
using HollowKnightTAS.Core.Keyframes;
using HollowKnightTAS.Core.Ledger;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Core.Playback;
using HollowKnightTAS.Core.ReplaySave;
using HollowKnightTAS.Core.Recording;
using HollowKnightTAS.Core.Serialization;
using HollowKnightTAS.Core.State;
using HollowKnightTAS.Runtime.Companion;
using HollowKnightTAS.Runtime.Control;
using HollowKnightTAS.Runtime.Inspector;
using HollowKnightTAS.Runtime.Input;
using HollowKnightTAS.Runtime.FullRun;
using HollowKnightTAS.Runtime.Keyframes;
using HollowKnightTAS.Runtime.Playback;
using HollowKnightTAS.Runtime.ReplaySave;
using HollowKnightTAS.Runtime.State;
using HollowKnightTAS.Runtime.Verification;
using InControl;
using UnityEngine;

namespace HollowKnightTAS.Runtime.Ipc
{
    public sealed class RuntimeCommandDispatcher : IDisposable
    {
        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern void ExitProcess(uint exitCode);

        private const int MaximumMovieBytes = 32 * 1024 * 1024;
        private const int MaximumMovieChunks = 1024;

        private readonly RuntimeCommandQueue commands;
        private readonly NamedPipeRuntimeServer server;
        private readonly RuntimeReplayJournal journal;
        private readonly RuntimeFullRunSession? fullRunSession;
        private readonly Thread? fullRunWorker;
        private readonly RuntimeReplaySaveManager? replaySaves;
        private readonly RuntimeInspector? inspector;
        private readonly RuntimeStartupProfileAttestor startupAttestor;
        private readonly RuntimeControlService controls;
        private readonly string sessionId;
        private readonly string manifestSha256;
        private readonly bool nativeCapabilitiesRequested;
        private readonly AutomationMode automationMode;
        private readonly bool verificationModeRequested;
        private readonly string gameVersion;
        private readonly string apiVersion;
        private readonly KeyframeTierResolution keyframeResolution;
        private readonly long budgetTicks;
        private readonly Action<string, IReadOnlyDictionary<string, string>>
            emit;
        private readonly HashSet<string> subscriptions =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly RuntimeSnapshotCapture snapshotCapture =
            new RuntimeSnapshotCapture();
        private RuntimeCommandDispatcherRunner? runner;
        private SourceLifecycleReload? sourceLifecycleReload;
        private string lastNativeReloadPhase = "Idle";
        private string lastNativeReloadFailure = string.Empty;
        private bool gameManagerUpdateHookRegistered;
        private MovieUpload? upload;
        private ReplayLifecycleExecutionPlan? stagedLifecyclePlan;
        private string stagedLifecyclePlanHash = string.Empty;
        private MovieLifecycleExport? lifecycleExport;
        private ReplayRestoreHandle? restoreHandle;
        private ReplayRestorePhase? lastRestorePhase;
        private ReplayRestoreProgress? lastRestoreProgress;
        private PendingMovieSeek? pendingMovieSeek;
        private PendingColdBaseline? pendingColdBaseline;
        private long frameCount;
        private long? runUntilMovieTick;
        private bool recordingActive;
        private string claimedColdIntentSha256 = string.Empty;
        private string claimedColdClaimId = string.Empty;
        private ColdRestoreIntent? preparedColdIntent;
        private string preparedColdIntentSha256 = string.Empty;
        private string preparedColdClaimId = string.Empty;
        private bool coldSourceQuiesced;
        private bool coldSourceExitRequested;
        private bool gameExitRequested;
        private bool startupHandoffExitRequested;
        private Func<bool>? shutdownCompanionForExit;
        private readonly StartupHandoffGuard startupHandoff = new StartupHandoffGuard();

        public void ObserveStartupGameplay(bool active) => startupHandoff.ObserveGameplay(active);

        private string HandleStartupHandoff(IReadOnlyDictionary<string, string> fields)
        {
            RequireFields(fields, "requestId", "phase", "operationId", "processId", "processStartTimeUtcTicks");
            using (var process = Process.GetCurrentProcess())
            {
                if (fields["processId"] != process.Id.ToString(CultureInfo.InvariantCulture)
                    || fields["processStartTimeUtcTicks"] != process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture))
                    throw new InvalidOperationException("Startup handoff process identity mismatch.");
            }
            var manager = GameManager.instance;
            startupHandoff.ObserveGameplay(manager != null && manager.gameState == GameState.PLAYING);
            var controlled = Environment.GetEnvironmentVariable("HKTAS_CLOCK_STARTUP_LATCH") == "1";
            var title = manager != null && !manager.IsInSceneTransition
                && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Menu_Title"
                && HeroController.SilentInstance == null;
            if (fields["phase"] == "prepare")
                return startupHandoff.Prepare(fields["operationId"], controlled, title);
            if (fields["phase"] != "commit") throw new InvalidDataException("Unknown startup handoff phase.");
            startupHandoff.Commit(fields["operationId"], controlled, title);
            // This source is still at the title and has never entered a save.
            // End it after Dispatch has published the accepted IPC reply.
            // Application.Quit crashes during native shutdown on this build;
            // Mono's Environment.Exit stalls while unloading the domain.
            startupHandoffExitRequested = true;
            gameExitRequested = true;
            return "startup-handoff-exiting";
        }

        internal void ConfigureCompanionShutdownForExit(Func<bool> shutdown)
        {
            shutdownCompanionForExit = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
        }

        private void ExitApprovedGameProcess()
        {
            // The title source must keep Studio alive for the replacement.
            // An ordinary user quit removes its authenticated session and
            // honors ExitCompanionWithGame before bypassing Unity shutdown.
            if (!startupHandoffExitRequested)
            {
                try
                {
                    var acknowledged = shutdownCompanionForExit?.Invoke() == true;
                    emit("companion-shutdown-before-exit", new Dictionary<string, string>
                    {
                        ["acknowledged"] = acknowledged ? "true" : "false"
                    });
                }
                catch (Exception error)
                {
                    emit("companion-shutdown-before-exit-failed", new Dictionary<string, string>
                    {
                        ["reason"] = error.Message
                    });
                }
            }
            ExitProcess(0);
        }
        private bool pausedWindowExitPending;
        private bool disposed;
        private Media.RuntimeVideoCapture? videoCapture;
        private bool videoReplaysLoadedMovie;
        private string lastVideoState = string.Empty;

        public RuntimeCommandDispatcher(
            RuntimeCommandQueue commands,
            NamedPipeRuntimeServer server,
            RuntimeReplayJournal journal,
            RuntimeReplaySaveManager? replaySaves,
            RuntimeInspector? inspector,
            RuntimeStartupProfileAttestor startupAttestor,
            string sessionId,
            string manifestSha256,
            bool nativeCapabilitiesRequested,
            AutomationMode automationMode,
            bool verificationModeRequested,
            bool replayDeterministicRngEnabled,
            int replayDeterministicRngSeed,
            string gameVersion,
            string apiVersion,
            KeyframeTierResolution keyframeResolution,
            double mainThreadBudgetMilliseconds,
            Action<string, IReadOnlyDictionary<string, string>> emit,
            RuntimeFullRunSession? fullRunSession = null)
        {
            this.commands = commands;
            this.server = server;
            this.journal = journal;
            this.replaySaves = replaySaves;
            this.inspector = inspector;
            this.startupAttestor = startupAttestor
                                   ?? throw new ArgumentNullException(
                                       nameof(startupAttestor));
            this.sessionId = sessionId;
            this.manifestSha256 = manifestSha256;
            this.nativeCapabilitiesRequested =
                nativeCapabilitiesRequested;
            this.automationMode = automationMode;
            this.verificationModeRequested =
                verificationModeRequested;
            this.gameVersion = gameVersion
                               ?? throw new ArgumentNullException(
                                   nameof(gameVersion));
            this.apiVersion = apiVersion
                              ?? throw new ArgumentNullException(
                                  nameof(apiVersion));
            this.keyframeResolution = keyframeResolution
                                      ?? throw new ArgumentNullException(
                                          nameof(keyframeResolution));
            this.emit = emit;
            this.fullRunSession = fullRunSession;
            budgetTicks = Math.Max(
                1,
                (long)Math.Round(
                    mainThreadBudgetMilliseconds
                    * Stopwatch.Frequency
                    / 1000d));
            controls = new RuntimeControlService(
                sessionId,
                manifestSha256,
                journal,
                replayDeterministicRngEnabled,
                replayDeterministicRngSeed,
                PumpPausedBoundaryCommands,
                Publish);
            replaySaves?.ConfigureColdBoundaryCommandPump(
                PumpColdRestoreBoundaryCommands);
            replaySaves?.ConfigureColdTargetHandoff(
                controls.AdoptRestoredPauseBoundary);
            if (inspector != null)
            {
                inspector.SetMovieTickSource(
                    () => controls.AuthoritativeMovieTick);
                inspector.FrameSampled += OnWatchFrame;
            }

            // Do not allocate a persistent Unity object before the first
            // gameplay scene has finished loading. Even a non-gameplay
            // GameObject changes Unity instance allocation order and can
            // perturb the vanilla seated-bench Rigidbody baseline. Title
            // commands are drained from the existing GameManager update
            // boundary until the normal LateUpdate pump can be created.
            if (fullRunSession == null)
            {
                On.GameManager.Update += OnGameManagerUpdate;
                gameManagerUpdateHookRegistered = true;
            }
            else
            {
                fullRunWorker = new Thread(FullRunWorkerLoop)
                {
                    IsBackground = true,
                    Name = "HKTAS full-run IPC"
                };
                fullRunWorker.Start();
            }
        }

        public RuntimeControlService Controls => controls;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            commands.Clear();
            if (fullRunWorker != null && Thread.CurrentThread != fullRunWorker)
                fullRunWorker.Join(TimeSpan.FromSeconds(2));
            videoCapture?.Dispose();
            lifecycleExport = null;
            stagedLifecyclePlan = null;
            stagedLifecyclePlanHash = string.Empty;
            DetachSourceNativeLoadHook();
            try { sourceLifecycleReload?.InputLease?.Dispose(); }
            catch (Exception exception)
            {
                emit("native-reload-input-cleanup-failed", new Dictionary<string, string> { ["reason"] = exception.Message });
            }
            pendingColdBaseline = null;
            if (gameManagerUpdateHookRegistered)
            {
                On.GameManager.Update -= OnGameManagerUpdate;
                gameManagerUpdateHookRegistered = false;
            }

            if (inspector != null)
            {
                inspector.FrameSampled -= OnWatchFrame;
                inspector.SetMovieTickSource(null);
            }

            controls.Dispose();
            subscriptions.Clear();
            upload = null;
            if (runner != null)
            {
                UnityEngine.Object.Destroy(runner.gameObject);
                runner = null;
            }
        }

        private void OnGameManagerUpdate(
            On.GameManager.orig_Update original,
            GameManager self)
        {
            original(self);
            if (disposed)
            {
                return;
            }

            if (runner == null)
            {
                // Read-only title/gameplay traffic is drained through the
                // existing GameManager boundary. Do not allocate another
                // persistent Unity object merely because a Hero appeared:
                // that can still happen while the loaded scene is resolving
                // its initial physics state. The first real pause/playback
                // request creates its own original-frame controller; only
                // then is the normal LateUpdate dispatcher pump required.
                OnLateUpdate();
                if (IsGameplayRuntimeReady(self)
                    && controls.RuntimePumpRequired)
                {
                    EnsureRuntimePump();
                }
            }
        }

        private static bool IsGameplayRuntimeReady(GameManager manager)
        {
            return manager != null
                   && manager.gameState == GameState.PLAYING
                   && !manager.IsInSceneTransition
                   && HeroController.SilentInstance != null;
        }

        private void EnsureRuntimePump()
        {
            if (disposed || runner != null)
            {
                return;
            }

            var gameObject = new GameObject(
                "HollowKnightTAS.RuntimeCommandDispatcher");
            UnityEngine.Object.DontDestroyOnLoad(gameObject);
            runner =
                gameObject.AddComponent<RuntimeCommandDispatcherRunner>();
            runner.Initialize(this);
        }

        internal void OnLateUpdate()
        {
            if (disposed)
            {
                return;
            }
            PollSourceLifecycleReload(false);

            frameCount++;
            if (server.ConsumeDisconnectPending())
            {
                if (videoCapture?.IsActive == true) videoCapture.Fail("Companion disconnected during video export.");
                FailSourceLifecycleReload("Companion disconnected during native reload.");
                subscriptions.Clear();
                upload = null;
                if (restoreHandle.HasValue
                    && replaySaves != null)
                {
                    try
                    {
                        replaySaves.CancelRestore(
                            restoreHandle.Value);
                    }
                    catch
                    {
                        // Runtime control cleanup below remains mandatory.
                    }
                }

                restoreHandle = null;
                lastRestorePhase = null;
                lastRestoreProgress = null;
                pendingMovieSeek = null;
                runUntilMovieTick = null;
                recordingActive = false;
                if (sourceLifecycleReload == null) controls.CleanupForDisconnect();
                emit(
                    "companion-disconnected",
                    new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["cleanup"] = sourceLifecycleReload != null
                            ? "awaiting-native-lifecycle-safe-pause"
                            : controls.DisconnectCleanupPending
                            ? "awaiting-completed-frame-pause-before-input-cleanup"
                            : "input-bindings-cleaned-control-mode=" + controls.ControlMode
                });
            }

            PollPendingColdBaseline();
            var started = Stopwatch.GetTimestamp();
            var processed = 0;
            while (commands.TryDequeue(out var command))
            {
                Dispatch(command);
                processed++;
                if (gameExitRequested)
                {
                    ExitApprovedGameProcess();
                    return;
                }

                if (Stopwatch.GetTimestamp() - started >= budgetTicks)
                {
                    break;
                }
            }

            if (commands.Count > 0)
            {
                Publish(
                    IpcMessageTypes.Backpressure,
                    new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["detail"] =
                            "Main-thread command budget exhausted.",
                        ["pending"] = commands.Count.ToString(
                            CultureInfo.InvariantCulture),
                        ["processed"] = processed.ToString(
                            CultureInfo.InvariantCulture)
                    });
            }

            if (controls.TryTakeCompletedStep(out var step)
                && step != null)
            {
                Publish(
                    IpcMessageTypes.RuntimeModeChanged,
                    new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["controlMode"] =
                            controls.ControlMode.ToString(),
                        ["fixedTickDelta"] =
                            step.FixedTickDelta.ToString(
                                CultureInfo.InvariantCulture),
                        ["movieTickDelta"] =
                            step.MovieTickDelta.ToString(
                                CultureInfo.InvariantCulture),
                        ["playbackMode"] =
                            controls.PlaybackMode.ToString(),
                        ["reason"] = "step-completed",
                        ["visualTickDelta"] =
                            step.VisualTickDelta.ToString(
                                CultureInfo.InvariantCulture)
                    });
                // The paused game may never reach the periodic status frame.
                // Publish the completed boundary now so Studio and API clients
                // do not retain the pre-step Stepping snapshot indefinitely.
                PublishRuntimeStatus();
            }

            PollRestore();
            PollMovieSeek();
            PollRunUntil();
            if (frameCount % 120 == 0 && server.IsConnected)
            {
                PublishRuntimeStatus();
            }
        }

        private void PumpPausedBoundaryCommands()
        {
            if (disposed)
            {
                return;
            }
            // Observe disconnect before allowing a completed menu operation to
            // start the next native load.
            var disconnected = server.ConsumeDisconnectPending();
            if (disconnected)
                FailSourceLifecycleReload("Companion disconnected during native reload.");
            PollSourceLifecycleReload(true);
            if (controls.ControlMode != SimulationControlMode.Paused) return;

            PausedWindowMessagePump.ServiceSentMessages();
            if (disposed)
                return;

            // Complete the last input sample's binding cleanup before accepting
            // another batch or persisting a save. No extra game frame is run.
            if (controls.CompletePlaybackAtPausedBoundary())
            {
                PublishRuntimeStatus();
            }
            PollVideoExport();

            // Unity LateUpdate is blocked by this completed-frame guard.
            // Finish queued disk work even when no new command arrives.
            if (replaySaves?.PumpPausedPersistence() == true)
            {
                PublishRuntimeStatus();
            }

            PollPendingColdBaseline();

            if (TryExitFromPausedWindowShortcut())
                return;

            if (!disconnected && !commands.WaitForActivity(TimeSpan.FromMilliseconds(100)))
            {
                return;
            }

            if (disconnected || server.ConsumeDisconnectPending())
            {
                if (videoCapture?.IsActive == true) videoCapture.Fail("Companion disconnected during video export.");
                FailSourceLifecycleReload("Companion disconnected during native reload.");
                subscriptions.Clear();
                upload = null;
                restoreHandle = null;
                lastRestorePhase = null;
                lastRestoreProgress = null;
                pendingMovieSeek = null;
                runUntilMovieTick = null;
                recordingActive = false;
                if (sourceLifecycleReload == null) controls.CleanupForDisconnect();
                emit(
                    "companion-disconnected",
                    new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["cleanup"] = sourceLifecycleReload != null
                            ? "awaiting-native-lifecycle-safe-pause"
                            : "input-bindings-restored-pause-retained-at-completed-frame-boundary"
                    });
                return;
            }

            var started = Stopwatch.GetTimestamp();
            while (controls.ControlMode == SimulationControlMode.Paused
                   && commands.TryDequeue(out var command))
            {
                Dispatch(command, atCompletedFrameBoundary: true);
                if (controls.ControlMode != SimulationControlMode.Paused)
                    pausedWindowExitPending = false;
                if (coldSourceExitRequested)
                {
                    Application.Quit();
                    controls.ReleaseBoundaryForApplicationQuit();
                    return;
                }
                if (gameExitRequested)
                {
                    ExitApprovedGameProcess();
                    return;
                }

                if (Stopwatch.GetTimestamp() - started >= budgetTicks)
                {
                    break;
                }
            }

            if (controls.ControlMode == SimulationControlMode.Paused)
            {
                replaySaves?.PumpPausedPersistence();
            }
            PublishRuntimeStatus();
        }

        private void PumpColdRestoreBoundaryCommands()
        {
            if (disposed)
            {
                return;
            }

            // LateUpdate cannot run while this completed-frame guard blocks.
            // Publish BaselineReady/PausedAtTarget from the boundary itself so
            // the authenticated Companion can release the exact same frame.
            PollRestore();
            if (!commands.WaitForActivity(TimeSpan.FromMilliseconds(100)))
            {
                return;
            }

            if (server.ConsumeDisconnectPending())
            {
                if (restoreHandle.HasValue && replaySaves != null)
                {
                    try
                    {
                        replaySaves.CancelRestore(restoreHandle.Value);
                    }
                    catch
                    {
                        // The coordinator owns fail-closed cleanup.
                    }
                }

                restoreHandle = null;
                lastRestorePhase = null;
                lastRestoreProgress = null;
                pendingMovieSeek = null;
                controls.CleanupForDisconnect();
                emit(
                    "companion-disconnected",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["cleanup"] =
                            "cold-restore-cancelled-at-completed-frame-boundary"
                    });
                return;
            }

            if (commands.TryDequeue(out var command))
            {
                Dispatch(command);
                PollRestore();
            }
        }

        private void Dispatch(ValidatedRuntimeCommand command, bool allowBaselineCapture = true,
            bool atCompletedFrameBoundary = false)
        {
            var requestId = command.Fields.TryGetValue(
                "requestId",
                out var supplied)
                ? supplied
                : "sequence-"
                  + command.Sequence.ToString(
                      CultureInfo.InvariantCulture);
            try
            {
                var detail = Handle(command, allowBaselineCapture, atCompletedFrameBoundary);
                if (ReferenceEquals(pendingColdBaseline?.Command, command))
                    return; // Acknowledge this request only after persistence and revalidation.
                Publish(
                    IpcMessageTypes.CommandAccepted,
                    new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["command"] = command.MessageType,
                        ["detail"] = detail,
                        ["requestId"] = requestId
                    });
                emit(
                    "companion-command-accepted",
                    new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["command"] = command.MessageType,
                        ["requestId"] = requestId,
                        ["sequence"] =
                            command.Sequence.ToString(
                                CultureInfo.InvariantCulture)
                    });
            }
            catch (Exception exception)
            {
                var errorCode = GetCommandErrorCode(exception);
                Publish(
                    IpcMessageTypes.CommandRejected,
                    new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["command"] = command.MessageType,
                        ["detail"] = exception.Message,
                        ["errorCode"] = errorCode,
                        ["requestId"] = requestId
                    });
                emit(
                    "companion-command-rejected",
                    new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["command"] = command.MessageType,
                        ["error"] =
                            errorCode
                            + ":"
                            + exception.Message,
                        ["requestId"] = requestId
                    });
            }
        }

        private string Handle(ValidatedRuntimeCommand command, bool allowBaselineCapture, bool atCompletedFrameBoundary)
        {
            if (fullRunSession != null)
                return HandleFullRun(command);
            if (videoCapture?.IsActive == true && !IsAllowedDuringVideoExport(command.MessageType))
                throw new RuntimeCommandRejectionException("Busy", "Finish or cancel video export before changing the replay or restoring state.");
            if (sourceLifecycleReload != null && command.MessageType != IpcMessageTypes.RequestSnapshot
                && !(command.MessageType == IpcMessageTypes.QuitGame && CanExitFailedSourceLifecycle)
                && command.MessageType != IpcMessageTypes.Ping
                && command.MessageType != IpcMessageTypes.RequestStartupProfileAttestation)
                throw new RuntimeCommandRejectionException("Busy", "A native lifecycle operation is still running; query status.");
            if (IsColdRestoreBoundaryActive()
                && !IsAllowedColdBoundaryCommand(command.MessageType))
            {
                throw new RuntimeCommandRejectionException(
                    "ColdRestoreBoundary",
                    "The fresh process is frozen at a cold-restore boundary; this command is not safe there.");
            }

            switch (command.MessageType)
            {
                case IpcMessageTypes.StartVideoExport:
                    RequireFields(command.Fields, command.Fields.ContainsKey("replayLoadedMovie")
                        ? new[] { "ffmpegPath", "outputPath", "maximumFrames", "requestId", "replayLoadedMovie" }
                        : new[] { "ffmpegPath", "outputPath", "maximumFrames", "requestId" });
                    if (videoCapture?.IsActive == true) throw new InvalidOperationException("A video export is already active.");
                    if (controls.ControlMode != SimulationControlMode.Paused)
                        throw new InvalidOperationException("Pause at the sequence start before starting video export.");
                    var replayLoaded = command.Fields.TryGetValue("replayLoadedMovie", out var replayVideoText)
                        && bool.Parse(replayVideoText);
                    var replayCount = replayLoaded ? controls.LoadedMovieFrameCount : 0;
                    var videoMaximum = int.Parse(command.Fields["maximumFrames"], CultureInfo.InvariantCulture);
                    if (replayLoaded && videoMaximum <= replayCount)
                        throw new InvalidOperationException("Video safety limit must exceed the movie input count to allow scene transitions.");
                    videoCapture?.Dispose();
                    videoCapture = new Media.RuntimeVideoCapture(command.Fields["ffmpegPath"], command.Fields["outputPath"],
                        videoMaximum,
                        message => emit("video-export", new Dictionary<string, string> { ["detail"] = message }),
                        !replayLoaded, StopVideoPlayback, OnVideoFrameCompleted);
                    videoReplaysLoadedMovie = replayLoaded;
                    if (replayLoaded)
                    {
                        try
                        {
                            var replayStart = controls.StartReplay();
                            if (!replayStart.Success) throw new InvalidOperationException(replayStart.Error);
                            RequireSuccess(controls.Step(replayCount));
                        }
                        catch (Exception exception) { videoCapture.Fail(exception.Message); throw; }
                    }
                    return videoCapture.OperationId;
                case IpcMessageTypes.FinishVideoExport:
                case IpcMessageTypes.CancelVideoExport:
                    RequireFields(command.Fields, "operationId", "requestId");
                    if (videoCapture == null || videoCapture.OperationId != command.Fields["operationId"])
                        throw new InvalidOperationException("Video export operationId does not match.");
                    if (command.MessageType == IpcMessageTypes.CancelVideoExport)
                    {
                        videoCapture.Cancel();
                        StopVideoPlayback();
                    }
                    else
                    {
                        if (videoReplaysLoadedMovie) throw new InvalidOperationException("Sequence export finishes automatically; cancel to stop it early.");
                        videoCapture.Finish();
                    }
                    return videoCapture.OperationId;
                case IpcMessageTypes.UploadMovieBegin:
                    BeginUpload(command.Fields);
                    return "Movie upload started.";
                case IpcMessageTypes.UploadMovieChunk:
                    AppendUpload(command.Fields);
                    return "Movie chunk accepted.";
                case IpcMessageTypes.UploadMovieEnd:
                    var completingPlan = upload?.IsLifecyclePlan == true;
                    CompleteUpload(command.Fields);
                    return completingPlan ? "Lifecycle plan validated and staged for branch seek." : "Movie validated and loaded.";
                case IpcMessageTypes.StartReplay:
                    RequireFields(command.Fields, "requestId");
                    var start = controls.StartReplay();
                    if (!start.Success)
                    {
                        throw new InvalidOperationException(
                            start.Error);
                    }

                    return controls.DeferredReplayArmed
                           && !controls.DeferredActivationAttempted
                        ? "Replay armed for the T24 recording boundary."
                        : "Replay started.";
                case IpcMessageTypes.StopReplay:
                    RequireFields(command.Fields, "requestId");
                    var stop = controls.StopReplay();
                    if (!stop.Success)
                    {
                        throw new InvalidOperationException(
                            stop.Error);
                    }

                    return "Replay stopping.";
                case IpcMessageTypes.Pause:
                    RequireFields(command.Fields, "requestId");
                    if (videoReplaysLoadedMovie && controls.ControlMode == SimulationControlMode.Stepping)
                    {
                        controls.InterruptVideoStep();
                        return "Video export will pause at the completed frame boundary.";
                    }
                    return RequireSuccess(controls.Pause());
                case IpcMessageTypes.Step:
                    RequireFields(
                        command.Fields,
                        "boundary",
                        "count",
                        "requestId");
                    if (!string.Equals(
                            command.Fields["boundary"],
                            "movieTick",
                            StringComparison.Ordinal)
                        || !int.TryParse(
                            command.Fields["count"],
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var count)
                        || count < 1
                        || count > 10000)
                    {
                        throw new InvalidDataException(
                            "Step requires boundary=movieTick and count in [1,10000].");
                    }

                    return RequireSuccess(controls.Step(count));
                case IpcMessageTypes.RunInputBatch:
                    return RunInputBatch(command.Fields);
                case IpcMessageTypes.Resume:
                    RequireFields(command.Fields, "requestId");
                    if (videoReplaysLoadedMovie)
                    {
                        var remaining = checked(controls.LoadedMovieFrameCount - (int)(controls.LastReplayMovieTick + 1));
                        if (remaining <= 0) throw new InvalidOperationException("Video replay is already ending.");
                        return RequireSuccess(controls.Step(remaining));
                    }
                    return RequireSuccess(controls.Resume());
                case IpcMessageTypes.QuitGame:
                    RequireFields(command.Fields, "requestId");
                    return RequestPausedGameExit();
                case IpcMessageTypes.StartupHandoff:
                    return HandleStartupHandoff(command.Fields);
                case IpcMessageTypes.LoadGameSlot:
                    RequireFields(command.Fields, "requestId", "slot");
                    return LoadExistingGameSlot(command.Fields["slot"]);
                case IpcMessageTypes.ReloadGameSlot:
                    RequireFields(command.Fields, "requestId", "slot");
                    return BeginSourceLifecycleReload(command.Fields["slot"], atCompletedFrameBoundary);
                case IpcMessageTypes.Subscribe:
                    RequireFields(
                        command.Fields,
                        "requestId",
                        "stream");
                    RequireStream(command.Fields["stream"]);
                    subscriptions.Add(command.Fields["stream"]);
                    if (string.Equals(
                            command.Fields["stream"],
                            "watch",
                            StringComparison.Ordinal)
                        && inspector?.CurrentFrame != null)
                    {
                        // A seated cold-start fixture can remain at
                        // movieTick=-1 indefinitely. Replay the latest typed
                        // frame at subscription time so late external clients
                        // can still observe the exact pre-input state.
                        OnWatchFrame(inspector.CurrentFrame);
                    }
                    return "Subscribed " + command.Fields["stream"] + ".";
                case IpcMessageTypes.Unsubscribe:
                    RequireFields(
                        command.Fields,
                        "requestId",
                        "stream");
                    RequireStream(command.Fields["stream"]);
                    subscriptions.Remove(command.Fields["stream"]);
                    return "Unsubscribed " + command.Fields["stream"] + ".";
                case IpcMessageTypes.RequestSnapshot:
                    if (command.Fields.ContainsKey("statusOnly"))
                    {
                        RequireFields(command.Fields, "requestId", "statusOnly");
                        if (command.Fields["statusOnly"] != "true")
                            throw new InvalidDataException("statusOnly must be true.");
                        PublishRuntimeStatus(command.Fields["requestId"]);
                        return "Control status published.";
                    }
                    var includeCombatState = command.Fields.ContainsKey("includeCombatState");
                    if (includeCombatState)
                    {
                        RequireFields(command.Fields, "requestId", "includeCombatState");
                        if (command.Fields["includeCombatState"] != "true")
                            throw new InvalidDataException("includeCombatState must be true.");
                    }
                    else
                        RequireFields(command.Fields, "requestId");
                    PublishSnapshot(command.Fields["requestId"], includeCombatState);
                    return "Snapshot published.";
                case IpcMessageTypes.RequestMovie:
                    if (command.Fields.ContainsKey("exportChunk"))
                    {
                        RequireFields(command.Fields, "requestId", "exportId", "exportChunk");
                        if (lifecycleExport == null || command.Fields["exportId"] != lifecycleExport.Sha256)
                            throw new InvalidOperationException("Movie export expired or changed; request a new snapshot.");
                        var index = int.Parse(command.Fields["exportChunk"], CultureInfo.InvariantCulture);
                        Publish(IpcMessageTypes.MovieDocument, new Dictionary<string, string> {
                            ["requestId"] = command.Fields["requestId"], ["exportId"] = lifecycleExport.Sha256,
                            ["exportChunk"] = index.ToString(CultureInfo.InvariantCulture),
                            ["chunkBase64"] = Convert.ToBase64String(lifecycleExport.GetChunk(index))
                        });
                        return "Lifecycle movie chunk published.";
                    }
                    if (command.Fields.ContainsKey("includeLifecycle"))
                    {
                        RequireFields(command.Fields, "requestId", "includeLifecycle");
                        if (command.Fields["includeLifecycle"] != "true")
                            throw new InvalidDataException("includeLifecycle must be true.");
                        var frozenExport = journal.FreezeForReplaySave(journal.LastCommittedMovieTick);
                        if (!frozenExport.Success || frozenExport.BaselineBundleBytes == null)
                            throw new InvalidOperationException(frozenExport.Error);
                        var exportMovie = BuildJournalMovie(frozenExport);
                        var exportLog = frozenExport.Lifecycle ?? new ReplayLifecycleLog(
                            Sha256Utility.ComputeHex(frozenExport.BaselineBundleBytes), Array.Empty<ReplayLifecycleRecord>());
                        lifecycleExport = MovieLifecycleExport.Create(exportMovie, frozenExport.BaselineBundleBytes,
                            exportLog, frozenExport.LifecycleObjects);
                        Publish(IpcMessageTypes.MovieDocument, new Dictionary<string, string> {
                            ["requestId"] = command.Fields["requestId"], ["source"] = "journal",
                            ["exportId"] = lifecycleExport.Sha256,
                            ["exportBytes"] = lifecycleExport.Length.ToString(CultureInfo.InvariantCulture),
                            ["exportChunks"] = lifecycleExport.ChunkCount.ToString(CultureInfo.InvariantCulture),
                            ["effectiveMovieTick"] = frozenExport.EffectiveMovieTick.ToString(CultureInfo.InvariantCulture)
                        });
                        return "Complete journal export snapshot prepared.";
                    }
                    RequireFields(command.Fields, "requestId");
                    // A fresh managed session journals inputs before the user
                    // explicitly records or uploads a movie. Expose that input
                    // prefix without toggling recording or replacing an edited
                    // movie already loaded into the controls.
                    var requestedMovie = controls.Movie;
                    var requestedMovieSource = "loaded";
                    if (requestedMovie == null && journal.IsAvailable
                        && journal.LastCommittedMovieTick >= 0)
                    {
                        requestedMovie = BuildJournalMovie();
                        requestedMovieSource = "journal";
                    }
                    PublishMovie(
                        command.Fields["requestId"],
                        requestedMovie,
                        requestedMovieSource);
                    return "Movie published.";
                case IpcMessageTypes.RunUntil:
                    RequireFields(
                        command.Fields,
                        "requestId",
                        "targetMovieTick");
                    StartRunUntil(command.Fields["targetMovieTick"]);
                    return "Run-until started.";
                case IpcMessageTypes.StartRecording:
                    RequireFields(command.Fields, "requestId");
                    if (recordingActive)
                    {
                        throw new InvalidOperationException(
                            "Recording is already active.");
                    }

                    if (!journal.IsAvailable)
                    {
                        throw new InvalidOperationException(
                            "Replay journal is unavailable. Recording origin: "
                            + journal.RecordingOriginStatus + ". " + journal.RecordingOriginDetail);
                    }

                    recordingActive = true;
                    return "Recording started.";
                case IpcMessageTypes.StopRecording:
                    RequireFields(command.Fields, "requestId");
                    if (!recordingActive)
                    {
                        throw new InvalidOperationException(
                            "Recording is not active.");
                    }

                    var recorded = BuildJournalMovie();
                    controls.SetMovie(recorded);
                    recordingActive = false;
                    PublishMovie(
                        command.Fields["requestId"],
                        recorded,
                        "recorded");
                    return "Recording stopped and movie loaded.";
                case IpcMessageTypes.SetHeroPose:
                case IpcMessageTypes.SetPlayerResources:
                case IpcMessageTypes.CommitStateMutation:
                    throw new RuntimeCommandRejectionException(
                        "CapabilityRemoved", "Built-in debug state mutation has been removed.");
                case IpcMessageTypes.CreateReplaySave:
                    RequireFields(
                        command.Fields,
                        "label",
                        "requestId");
                    var request = RequireReplaySaves()
                        .RequestManualSave(command.Fields["label"]);
                    if (!request.Accepted)
                    {
                        throw new InvalidOperationException(
                            request.Error);
                    }

                    return "Replay-save request "
                           + request.RequestId
                           + " accepted.";
                case IpcMessageTypes.ListReplaySaves:
                    RequireFields(command.Fields, "requestId");
                    PublishReplaySaveCatalog(
                        command.Fields["requestId"]);
                    return "Replay-save catalog published.";
                case IpcMessageTypes.RestoreReplaySave:
                    RequireFields(
                        command.Fields,
                        "replaySaveId",
                        "requestId");
                    if (restoreHandle.HasValue)
                    {
                        throw new InvalidOperationException(
                            "A replay-save restore is already active.");
                    }

                    if (pendingMovieSeek != null)
                    {
                        throw new InvalidOperationException(
                            "A movie seek is already active.");
                    }

                    var prepareRestore =
                        controls.PrepareForReplayRestore();
                    if (!prepareRestore.Success)
                    {
                        throw new InvalidOperationException(
                            prepareRestore.Error);
                    }

                    restoreHandle = RequireReplaySaves()
                        .BeginRestore(
                            command.Fields["replaySaveId"]);
                    lastRestorePhase = null;
                    lastRestoreProgress = null;
                    return "Replay-save restore started.";
                case IpcMessageTypes.BeginColdRestore:
                    return BeginColdRestore(command.Fields);
                case IpcMessageTypes.PrepareColdRestore:
                    return PrepareColdRestore(command, allowBaselineCapture);
                case IpcMessageTypes.QuiesceColdRestoreSource:
                    return QuiesceColdRestoreSource(command.Fields);
                case IpcMessageTypes.ExitColdRestoreSource:
                    return ExitColdRestoreSource(command.Fields);
                case IpcMessageTypes.CancelColdRestoreSource:
                    return CancelColdRestoreSource(command.Fields);
                case IpcMessageTypes.ReleaseColdRestoreBaseline:
                    RequireFields(
                        command.Fields,
                        "operationId",
                        "requestId");
                    var coldHandle = RequireRestoreHandle();
                    if (!string.Equals(
                            coldHandle.Id,
                            command.Fields["operationId"],
                            StringComparison.Ordinal))
                    {
                        throw new RuntimeCommandRejectionException(
                            "PreconditionFailed",
                            "Cold-restore operation ID is stale.");
                    }

                    return RequireRestoreSuccess(
                        RequireReplaySaves()
                            .ReleaseColdRestoreBaseline(coldHandle));
                case IpcMessageTypes.SeekMovieTick:
                    throw new RuntimeCommandRejectionException(
                        "ColdRestoreRequired",
                        "Movie seek and branch rewind must be coordinated by Companion as a fresh-process cold restore.");
                case IpcMessageTypes.ApproveReplaySaveOverwrite:
                    RequireFields(
                        command.Fields,
                        "approved",
                        "requestId");
                    if (!bool.TryParse(
                            command.Fields["approved"],
                            out var approved))
                    {
                        throw new InvalidDataException(
                            "approved must be true or false.");
                    }

                    if (RequireReplaySaves().HasPendingSourceSlotApproval)
                    {
                        RequireSourceSlotApprovalBoundary();
                        return RequireReplaySaves().ResolveSourceSlotApproval(approved);
                    }
                    return RequireRestoreSuccess(
                        RequireReplaySaves()
                            .ApproveRestoreOverwrite(
                                RequireRestoreHandle(),
                                approved));
                case IpcMessageTypes.CancelReplaySaveRestore:
                    RequireFields(
                        command.Fields,
                        "requestId");
                    if (RequireReplaySaves().HasPendingSourceSlotApproval)
                        return RequireReplaySaves().ResolveSourceSlotApproval(false);
                    var cancelled = RequireRestoreSuccess(
                        RequireReplaySaves()
                            .CancelRestore(
                                RequireRestoreHandle()));
                    pendingMovieSeek = null;
                    return cancelled;
                case IpcMessageTypes.ResumeReplaySaveRestore:
                    RequireFields(
                        command.Fields,
                        "requestId");
                    if (pendingMovieSeek != null)
                    {
                        throw new InvalidOperationException(
                            "An active movie seek owns restore resume and tail replay.");
                    }

                    return RequireRestoreSuccess(
                        RequireReplaySaves()
                            .ResumeRestore(
                                RequireRestoreHandle()));
                case IpcMessageTypes.SetAutoSavePolicy:
                    SetAutoSavePolicy(command.Fields);
                    return "Auto-save policy updated.";
                case IpcMessageTypes.RequestCapabilityCatalog:
                    RequireFields(command.Fields, "requestId");
                    PublishCapabilityCatalog(
                        command.Fields["requestId"]);
                    return "Capability catalog published.";
                case IpcMessageTypes.RequestStartupProfileAttestation:
                    RequireFields(command.Fields, "requestId");
                    Publish(
                        IpcMessageTypes.StartupProfileAttestation,
                        startupAttestor.Capture().ToFields(
                            command.Fields["requestId"]));
                    return "Startup profile attestation published.";
                case IpcMessageTypes.ReportNativeEvidence:
                    ReportNativeEvidence(command.Fields);
                    return "Native capability evidence persisted.";
                case IpcMessageTypes.Ping:
                    RequireFields(command.Fields, "requestId");
                    Publish(
                        IpcMessageTypes.Pong,
                        new Dictionary<string, string>(
                            StringComparer.Ordinal)
                        {
                            ["requestId"] =
                                command.Fields["requestId"],
                            ["runtimeUtc"] =
                                DateTimeOffset.UtcNow.ToString(
                                    "O",
                                    CultureInfo.InvariantCulture)
                        });
                    return "Pong published.";
                default:
                    throw new InvalidDataException(
                        "Command is not implemented.");
            }
        }

        private void FullRunWorkerLoop()
        {
            var nextProgress = DateTime.UtcNow;
            while (!disposed)
            {
                commands.WaitForActivity(TimeSpan.FromMilliseconds(50));
                while (!disposed && commands.TryDequeue(out var command))
                {
                    Dispatch(command);
                    if (gameExitRequested) ExitApprovedGameProcess();
                }
                // Push progress independently of editor polling and paused snapshots.
                // This observer never advances, pauses, or changes the input stream.
                if (!disposed && server.IsConnected && DateTime.UtcNow >= nextProgress)
                {
                    nextProgress = DateTime.UtcNow.AddMilliseconds(200);
                    try { PublishFullRunState("runtime-progress"); }
                    catch (Exception exception)
                    {
                        emit("full-run-progress-failed", new Dictionary<string, string> { ["reason"] = exception.Message });
                    }
                }
            }
        }

        private string HandleFullRun(ValidatedRuntimeCommand command)
        {
            var session = fullRunSession ?? throw new InvalidOperationException("Full-run session is unavailable.");
            switch (command.MessageType)
            {
                case IpcMessageTypes.StartVideoExport:
                    var videoFields = new List<string> { "ffmpegPath", "outputPath", "maximumFrames", "requestId", "replayLoadedMovie" };
                    if (command.Fields.ContainsKey("endMovieFrame")) videoFields.Add("endMovieFrame");
                    if (command.Fields.ContainsKey("infoOverlay")) videoFields.Add("infoOverlay");
                    RequireFields(command.Fields, videoFields.ToArray());
                    return session.StartVideoExport(command.Fields["ffmpegPath"], command.Fields["outputPath"],
                        int.Parse(command.Fields["maximumFrames"], CultureInfo.InvariantCulture),
                        bool.Parse(command.Fields["replayLoadedMovie"]),
                        command.Fields.TryGetValue("endMovieFrame", out var videoEndFrame)
                            ? long.Parse(videoEndFrame, CultureInfo.InvariantCulture) : -1,
                        command.Fields.TryGetValue("infoOverlay", out var videoInfoOverlay) ? videoInfoOverlay : "");
                case IpcMessageTypes.CancelVideoExport:
                    RequireFields(command.Fields, "operationId", "requestId");
                    return session.CancelVideoExport(command.Fields["operationId"]);
                case IpcMessageTypes.FinishVideoExport:
                    throw new InvalidOperationException("v2 sequence export finishes automatically; cancel to stop it early.");
                case IpcMessageTypes.FullRunSeek:
                    RequireFields(command.Fields, "requestId", "targetFrame", "expectedNativeFrame");
                    session.SetPauseTarget(long.Parse(command.Fields["targetFrame"], CultureInfo.InvariantCulture),
                        long.Parse(command.Fields["expectedNativeFrame"], CultureInfo.InvariantCulture));
                    return "Movie pause target set.";
                case IpcMessageTypes.GetWorldSnapshot:
                    var world = session.ObserveWorld(command.Fields);
                    world["requestId"] = command.Fields["requestId"];
                    Publish(IpcMessageTypes.WorldSnapshot, world);
                    return "World snapshot published.";
                case IpcMessageTypes.GetObjectDetails:
                    var details = session.ObserveObject(command.Fields);
                    details["requestId"] = command.Fields["requestId"];
                    Publish(IpcMessageTypes.ObjectDetails, details);
                    return "Object details published.";
                case IpcMessageTypes.FullRunSnapshot:
                    RequireFields(command.Fields, "requestId");
                    Publish(IpcMessageTypes.FullRunMovieDocument, new Dictionary<string, string>
                    {
                        ["requestId"] = command.Fields["requestId"], ["available"] = "true",
                        ["path"] = session.SnapshotMovie(), ["movieId"] = string.Empty,
                        ["movieFrame"] = session.GetStatus().MovieFrame.ToString(CultureInfo.InvariantCulture)
                    });
                    return "Paused Movie snapshot saved.";
                case IpcMessageTypes.FullRunStatus:
                    RequireFields(command.Fields, "requestId");
                    PublishFullRunState(command.Fields["requestId"]);
                    return "Full-run state published.";
                case IpcMessageTypes.FullRunStop:
                    RequireFields(command.Fields, "requestId", "expectedNativeFrame");
                    if (!long.TryParse(command.Fields["expectedNativeFrame"], NumberStyles.None,
                            CultureInfo.InvariantCulture, out var expectedFrame))
                        throw new InvalidDataException("expectedNativeFrame is invalid.");
                    var stopped = session.Stop(expectedFrame);
                    if (!stopped.Success) throw new InvalidOperationException(stopped.Error);
                    PublishFullRunState(command.Fields["requestId"]);
                    PublishFullRunMovie(command.Fields["requestId"]);
                    return "Full-run session stopped at native frame " + expectedFrame + ".";
                case IpcMessageTypes.FullRunMovie:
                    RequireFields(command.Fields, "requestId");
                    PublishFullRunMovie(command.Fields["requestId"]);
                    return "Full-run movie location published.";
                case IpcMessageTypes.FullRunUpdateMovie:
                    RequireFields(command.Fields, "requestId", "moviePath", "expectedNativeFrame");
                    fullRunSession!.UpdateFutureMovie(command.Fields["moviePath"],
                        long.Parse(command.Fields["expectedNativeFrame"], CultureInfo.InvariantCulture));
                    return "Future Movie inputs updated at the paused boundary.";
                case IpcMessageTypes.QuitGame:
                    RequireFields(command.Fields, "requestId");
                    gameExitRequested = true;
                    return "Protected game exit requested.";
                case IpcMessageTypes.Ping:
                    RequireFields(command.Fields, "requestId");
                    Publish(IpcMessageTypes.Pong, new Dictionary<string, string>
                    {
                        ["requestId"] = command.Fields["requestId"]
                    });
                    return "pong";
                default:
                    throw new InvalidOperationException("v1 Runtime command is disabled during a full-run v2 session.");
            }
        }

        private void PublishFullRunState(string requestId)
        {
            var status = fullRunSession!.GetStatus();
            var fields = new Dictionary<string, string>
            {
                ["requestId"] = requestId,
                ["mode"] = status.Mode,
                ["nativeFrame"] = status.NativeFrame.ToString(CultureInfo.InvariantCulture),
                ["movieFrame"] = status.MovieFrame.ToString(CultureInfo.InvariantCulture),
                ["frameRate"] = fullRunSession!.ActiveFrameRate.ToString(CultureInfo.InvariantCulture),
                ["clockStepSeconds"] = fullRunSession.ClockStepSeconds.ToString("R", CultureInfo.InvariantCulture),
                ["skippedLoadFrames"] = status.SkippedLoadFrames.ToString(CultureInfo.InvariantCulture),
                ["frameBoundary"] = status.FrameBoundary,
                ["runtimeInputReady"] = status.RuntimeInputReady ? "true" : "false",
                ["mismatchCount"] = status.MismatchCount.ToString(CultureInfo.InvariantCulture),
                ["error"] = status.Error,
                ["sceneName"] = status.SceneName,
                ["saveSlot"] = status.SaveSlot.ToString(CultureInfo.InvariantCulture),
                ["heroX"] = status.HeroX,
                ["heroY"] = status.HeroY,
                ["respawnScene"] = status.RespawnScene,
                ["heroHealth"] = status.HeroHealth.ToString(CultureInfo.InvariantCulture),
                ["bossSceneEntered"] = status.BossSceneEntered ? "true" : "false",
                ["bossDeathObserved"] = status.BossDeathObserved ? "true" : "false",
                ["bossesDeadObserved"] = status.BossesDeadObserved ? "true" : "false",
                ["bossSceneCompleteObserved"] = status.BossSceneCompleteObserved ? "true" : "false",
                ["bossDeathFrame"] = status.BossDeathFrame.ToString(CultureInfo.InvariantCulture),
                ["bossSceneEntryMovieFrame"] = status.BossSceneEntryMovieFrame.ToString(CultureInfo.InvariantCulture)
            };
            foreach (var binding in fullRunSession.ReadBindingLabels()) fields[binding.Key] = binding.Value;
            foreach (var timing in fullRunSession.ReadFrameTiming()) fields[timing.Key] = timing.Value;
            fullRunSession.AppendVideoExportStatus(fields);
            Publish(IpcMessageTypes.FullRunState, fields);
        }

        private void PublishFullRunMovie(string requestId)
        {
            var path = fullRunSession!.RecordedMoviePath;
            var movie = fullRunSession.RecordedMovie;
            Publish(IpcMessageTypes.FullRunMovieDocument, new Dictionary<string, string>
            {
                ["requestId"] = requestId,
                ["available"] = movie == null ? "false" : "true",
                ["path"] = path,
                ["movieId"] = movie == null ? string.Empty : new MovieV2Codec().ComputeMovieId(movie)
            });
        }

        private void BeginUpload(
            IReadOnlyDictionary<string, string> fields)
        {
            var isPlan = fields.TryGetValue("payloadKind", out var payloadKind) && payloadKind == "lifecyclePlan";
            if (fields.ContainsKey("payloadKind") && !isPlan) throw new InvalidDataException("Unknown upload payload kind.");
            RequireFields(
                fields.Where(x => x.Key != "payloadKind").ToDictionary(x => x.Key, x => x.Value),
                "chunkCount",
                "movieId",
                "requestId",
                "totalBytes");
            if (!IpcIdentifier.IsValid(fields["requestId"], 128)
                || !MovieProtocolV1.IsLowerSha256(
                    fields["movieId"])
                || !int.TryParse(
                    fields["totalBytes"],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var totalBytes)
                || totalBytes <= 0
                || totalBytes > (isPlan ? ReplayLifecycleExecutionPlan.MaximumBytes : MaximumMovieBytes)
                || !int.TryParse(
                    fields["chunkCount"],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var chunkCount)
                || chunkCount <= 0
                || chunkCount > MaximumMovieChunks)
            {
                throw new InvalidDataException(
                    "Movie upload metadata is invalid.");
            }

            upload = new MovieUpload(
                fields["requestId"],
                fields["movieId"],
                totalBytes,
                chunkCount, isPlan);
        }

        private string RunInputBatch(
            IReadOnlyDictionary<string, string> fields)
        {
            RequireFields(
                fields,
                "batchMovieId",
                "expandedTicks",
                "expectedMovieTick",
                "expectedSceneEpoch",
                "requestId");
            if (!MovieProtocolV1.IsLowerSha256(fields["batchMovieId"])
                || !int.TryParse(
                    fields["expandedTicks"],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var expandedTicks)
                || expandedTicks < 1
                || expandedTicks
                   > MovieProtocolV1.DefaultMaxExpandedTicks
                || !long.TryParse(
                    fields["expectedMovieTick"],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var expectedMovieTick)
                || expectedMovieTick < 0
                || !int.TryParse(
                    fields["expectedSceneEpoch"],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var expectedSceneEpoch)
                || expectedSceneEpoch < 0)
            {
                throw new InvalidDataException(
                    "Input batch binding is invalid.");
            }

            if (controls.ControlMode
                != HollowKnightTAS.Core.Control.SimulationControlMode.Paused)
            {
                throw new InvalidOperationException(
                    "Input batches require a Paused Runtime.");
            }

            if (controls.PlaybackMode != PlaybackMode.Idle)
            {
                throw new InvalidOperationException(
                    "Input batches require idle playback.");
            }

            if (journal.CurrentSceneEpoch != expectedSceneEpoch
                || controls.AuthoritativeMovieTick != expectedMovieTick)
            {
                throw new InvalidOperationException(
                    "Input batch tick or scene epoch is stale.");
            }

            var movie = controls.Movie
                        ?? throw new InvalidOperationException(
                            "Upload a validated input batch first.");
            var canonical = new MovieCanonicalWriter();
            if (!string.Equals(
                    canonical.ComputeMovieId(movie),
                    fields["batchMovieId"],
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Loaded input batch movie ID does not match.");
            }

            long actualTicks = 0;
            foreach (var command in movie.Commands)
            {
                if (!(command is FrameRunCommand frames))
                {
                    throw new InvalidDataException(
                        "An adaptive input batch may contain frames commands only.");
                }

                actualTicks = checked(actualTicks + frames.FrameCount);
            }

            if (actualTicks != expandedTicks)
            {
                throw new InvalidDataException(
                    "Input batch expanded tick count does not match.");
            }

            var batch = controls.StartInputBatch(expandedTicks);
            if (!batch.Success)
            {
                throw new InvalidOperationException(batch.Error);
            }

            return "Input batch "
                   + fields["batchMovieId"]
                   + " scheduled for "
                   + expandedTicks.ToString(CultureInfo.InvariantCulture)
                   + " movie ticks.";
        }

        private void AppendUpload(
            IReadOnlyDictionary<string, string> fields)
        {
            RequireFields(
                fields,
                "base64",
                "index",
                "requestId");
            var active = upload
                         ?? throw new InvalidOperationException(
                             "No movie upload is active.");
            if (!string.Equals(
                    active.RequestId,
                    fields["requestId"],
                    StringComparison.Ordinal)
                || !int.TryParse(
                    fields["index"],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var index)
                || index != active.NextIndex)
            {
                throw new InvalidDataException(
                    "Movie chunk is out of order.");
            }

            byte[] chunk;
            try
            {
                chunk = Convert.FromBase64String(
                    fields["base64"]);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException(
                    "Movie chunk base64 is invalid.",
                    exception);
            }

            active.Append(chunk);
        }

        private void CompleteUpload(
            IReadOnlyDictionary<string, string> fields)
        {
            RequireFields(fields, "requestId");
            var active = upload
                         ?? throw new InvalidOperationException(
                             "No movie upload is active.");
            try
            {
                if (!string.Equals(
                        active.RequestId,
                        fields["requestId"],
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Movie upload request ID changed.");
                }

                var bytes = active.Complete();
                if (!string.Equals(
                        Sha256Utility.ComputeHex(bytes),
                        active.MovieId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Uploaded movie ID does not match canonical bytes.");
                }

                if (active.IsLifecyclePlan)
                {
                    var plan = ReplayLifecycleExecutionPlan.Deserialize(bytes, active.MovieId);
                    if (plan.Movie.Header.ManifestSha256 != manifestSha256)
                        throw new InvalidDataException("Lifecycle plan environment mismatch.");
                    stagedLifecyclePlan = plan;
                    stagedLifecyclePlanHash = active.MovieId;
                    return;
                }
                string source;
                try
                {
                    source = new UTF8Encoding(false, true)
                        .GetString(bytes);
                }
                catch (DecoderFallbackException exception)
                {
                    throw new InvalidDataException(
                        "Movie UTF-8 is invalid.",
                        exception);
                }

                var parse = new MovieParser().Parse(
                    new StringReader(source),
                    "<companion-upload>");
                if (!parse.Success || parse.Document == null)
                {
                    throw new InvalidDataException(
                        string.Join(
                            " | ",
                            parse.Diagnostics.Select(
                                value => value.ToString())));
                }

                var validation = new MovieValidator().Validate(
                    parse.Document,
                    new MovieValidationContext(
                        MovieProtocolV1.DefaultMaxExpandedTicks,
                        MovieProtocolV1.DefaultSemanticPaths,
                        manifestSha256));
                if (!validation.Success)
                {
                    throw new InvalidDataException(
                        string.Join(
                            " | ",
                            validation.Diagnostics.Select(
                                value => value.ToString())));
                }

                var canonical =
                    new MovieCanonicalWriter().WriteUtf8(
                        parse.Document);
                if (!canonical.SequenceEqual(bytes))
                {
                    throw new InvalidDataException(
                        "Uploaded movie is not canonical.");
                }

                controls.SetMovie(parse.Document);
                stagedLifecyclePlan = null;
                stagedLifecyclePlanHash = string.Empty;
            }
            finally
            {
                upload = null;
            }
        }

        private void PublishSnapshot(string requestId, bool includeCombatState = false)
        {
            var result = CaptureCurrentSnapshot();
            if (!result.Success
                || result.Snapshot == null
                || result.Sha256 == null)
            {
                throw new InvalidOperationException(
                    result.FailedProbeId
                    + ": "
                    + result.Error);
            }

            var fields = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["json"] =
                    SemanticSnapshotJson.Serialize(
                        result.Snapshot,
                        result.Sha256),
                ["movieTick"] =
                    controls.AuthoritativeMovieTick.ToString(
                        CultureInfo.InvariantCulture),
                ["requestId"] = requestId,
                ["sha256"] = result.Sha256,
                ["capturedAtUtc"] =
                    DateTimeOffset.UtcNow.ToString(
                        "O",
                        CultureInfo.InvariantCulture),
                ["controlMode"] =
                    controls.ControlMode.ToString(),
                ["sceneEpoch"] =
                    journal.CurrentSceneEpoch.ToString(
                        CultureInfo.InvariantCulture),
                ["mutationTransactionPending"] =
                    "false",
                ["phase"] = TickPhase.LateUpdateEnd.ToString(),
                ["playbackMode"] =
                    controls.PlaybackMode.ToString(),
                ["recordingActive"] =
                    recordingActive ? "true" : "false",
                ["runUntilMovieTick"] =
                    runUntilMovieTick?.ToString(
                        CultureInfo.InvariantCulture)
                    ?? string.Empty,
                ["verificationEligibility"] =
                    RuntimeVerificationEligibility.Status
            };
            if (includeCombatState)
            {
                var frame = (inspector ?? throw new InvalidOperationException("Inspector unavailable."))
                    .CaptureOnDemand(controls.AuthoritativeMovieTick);
                fields["combatStateJson"] = HollowKnightTAS.Core.Inspector.WatchFrameJson.Serialize(frame);
                fields["combatStateSequence"] = frame.Sequence.ToString(CultureInfo.InvariantCulture);
            }
            AppendPlaybackState(fields);
            AppendReplayRestoreState(fields);
            Publish(
                IpcMessageTypes.RuntimeStatus,
                fields);
        }

        private SnapshotCaptureResult CaptureCurrentSnapshot()
        {
            return snapshotCapture.Capture(
                new TickStamp(
                    InputManager.CurrentTick,
                    journal.CurrentVisualTick,
                    journal.CurrentFixedTick,
                    journal.CurrentSceneEpoch,
                    TickPhase.LateUpdateEnd));
        }

        private void StartRunUntil(string targetText)
        {
            var currentMovieTick = controls.AuthoritativeMovieTick;
            if (!long.TryParse(
                    targetText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var target)
                || target <= currentMovieTick
                || target > currentMovieTick
                            + 10000000)
            {
                throw new InvalidDataException(
                    "targetMovieTick must be within the next 10,000,000 ticks.");
            }

            if (runUntilMovieTick.HasValue)
            {
                throw new InvalidOperationException(
                    "A run-until request is already active.");
            }

            var resume = controls.Resume();
            if (!resume.Success
                && controls.ControlMode != SimulationControlMode.Running)
            {
                throw new InvalidOperationException(resume.Error);
            }

            runUntilMovieTick = target;
        }

        private void PollRunUntil()
        {
            if (!runUntilMovieTick.HasValue
                || controls.AuthoritativeMovieTick
                   < runUntilMovieTick.Value)
            {
                return;
            }

            var target = runUntilMovieTick.Value;
            runUntilMovieTick = null;
            var result = controls.Pause();
            Publish(
                IpcMessageTypes.RuntimeModeChanged,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["controlMode"] = controls.ControlMode.ToString(),
                    ["movieTick"] =
                        controls.AuthoritativeMovieTick.ToString(
                            CultureInfo.InvariantCulture),
                    ["mutationTransactionPending"] =
                        "false",
                    ["playbackMode"] =
                        controls.PlaybackMode.ToString(),
                    ["reason"] = result.Success
                        ? "run-until-reached"
                        : "run-until-pause-failed",
                    ["targetMovieTick"] =
                        target.ToString(
                            CultureInfo.InvariantCulture)
                });
        }

        private MovieDocument BuildJournalMovie(bool includeLifecycle = false)
        {
            var target = journal.LastCommittedMovieTick;
            if (target < 0)
            {
                throw new InvalidOperationException(
                    "No committed input exists.");
            }

            var frozen = includeLifecycle ? journal.FreezeForReplaySave(target) : journal.FreezeThrough(target);
            if (!frozen.Success)
            {
                throw new InvalidOperationException(frozen.Error);
            }

            return BuildJournalMovie(frozen);
        }

        private MovieDocument BuildJournalMovie(RuntimeReplayJournalFreezeResult frozen)
        {

            var records = new List<JournalRecord>();
            foreach (var bytes in frozen.SegmentBytes)
            {
                records.AddRange(
                    ReplayJournalSegmentCodec.Deserialize(bytes)
                        .Records);
            }

            return ReplayMovieBuilder.Build(
                records,
                frozen.Commands.Select(
                    value => new ReplayMovieEvent(
                        value.BeforeMovieTick,
                        new CheckpointCommand(
                            value.CheckpointIdentifier,
                            new MovieSourceSpan(
                                "automation-recording.hktas",
                                1,
                                1,
                                1)))),
                "automation-recording.hktas",
                gameVersion,
                apiVersion,
                manifestSha256,
                frozen.BaselineId,
                frozen.BaselineSemanticSha256);
        }

        private void PublishMovie(
            string requestId,
            MovieDocument? movie,
            string source)
        {
            if (movie == null)
            {
                Publish(
                    IpcMessageTypes.MovieDocument,
                    new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["available"] = "false",
                        ["movieBase64"] = string.Empty,
                        ["movieId"] = string.Empty,
                        ["requestId"] = requestId,
                        ["source"] = source
                    });
                return;
            }

            var writer = new MovieCanonicalWriter();
            var bytes = writer.WriteUtf8(movie);
            var base64 = Convert.ToBase64String(bytes);
            if (base64.Length
                > IpcPayloadCodec.MaximumFieldValueCharacters)
            {
                throw new InvalidOperationException(
                    "Movie exceeds automation-v1 single-response limit.");
            }

            Publish(
                IpcMessageTypes.MovieDocument,
                new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["available"] = "true",
                    ["movieBase64"] = base64,
                    ["movieId"] = writer.ComputeMovieId(movie),
                    ["requestId"] = requestId,
                    ["source"] = source
                });
        }

        private void PublishReplaySaveCatalog(string requestId)
        {
            var inspected = RequireReplaySaves().Inspect();
            var entries = inspected
                .Select(
                    entry =>
                        entry.Descriptor.ReplaySaveId
                        + " · "
                        + SanitizeLine(
                            entry.Descriptor.Label)
                        + " · "
                        + entry.Descriptor.Reason
                        + " · tick="
                        + entry.Descriptor.EffectiveMovieTick
                            .ToString(
                                CultureInfo.InvariantCulture)
                        + " · "
                        + entry.Status)
                .ToArray();
            Publish(
                IpcMessageTypes.ReplaySaveCatalog,
                new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["count"] = entries.Length.ToString(
                        CultureInfo.InvariantCulture),
                    ["catalogSchemaVersion"] = "1",
                    ["checkpointKind"] = "ReplayCheckpoint",
                    ["entries"] = string.Join("\n", entries),
                    ["entriesJson"] = BuildReplaySaveCatalogJson(
                        inspected),
                    ["restoreStrategy"] = "FullReplay",
                    ["requestId"] = requestId
                });
        }

        private static string BuildReplaySaveCatalogJson(
            IReadOnlyList<ReplaySaveCatalogEntry> entries)
        {
            var builder = new StringBuilder(
                Math.Max(256, entries.Count * 768));
            builder.Append('[');
            for (var index = 0; index < entries.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var entry = entries[index];
                var descriptor = entry.Descriptor;
                var automatic = descriptor.Reason
                                == ReplaySaveReason.AutomaticInterval;
                builder.Append("{\"automatic\":");
                builder.Append(automatic ? "true" : "false");
                builder.Append(",\"baselineId\":");
                CanonicalJsonWriter.AppendString(
                    builder,
                    descriptor.BaselineId);
                builder.Append(",\"baselineObjectSha256\":");
                CanonicalJsonWriter.AppendString(
                    builder,
                    descriptor.BaselineObjectSha256);
                builder.Append(",\"checkpointKind\":\"ReplayCheckpoint\"");
                builder.Append(",\"detail\":");
                CanonicalJsonWriter.AppendString(builder, entry.Detail);
                builder.Append(",\"effectiveMovieTick\":");
                builder.Append(
                    descriptor.EffectiveMovieTick.ToString(
                        CultureInfo.InvariantCulture));
                builder.Append(",\"estimatedReplayTicks\":");
                builder.Append(
                    checked(descriptor.EffectiveMovieTick + 1).ToString(
                        CultureInfo.InvariantCulture));
                builder.Append(",\"id\":");
                CanonicalJsonWriter.AppendString(
                    builder,
                    descriptor.ReplaySaveId);
                builder.Append(",\"integrity\":");
                CanonicalJsonWriter.AppendString(
                    builder,
                    entry.Status == ReplaySaveStatus.Ready
                        ? "valid"
                        : "invalid");
                builder.Append(",\"label\":");
                CanonicalJsonWriter.AppendString(builder, descriptor.Label);
                builder.Append(",\"movieId\":");
                CanonicalJsonWriter.AppendString(
                    builder,
                    descriptor.MovieObjectSha256);
                builder.Append(",\"parentLineage\":[]");
                builder.Append(",\"pinned\":");
                builder.Append(automatic ? "false" : "true");
                builder.Append(",\"reason\":");
                CanonicalJsonWriter.AppendString(
                    builder,
                    descriptor.Reason.ToString());
                builder.Append(",\"requestedAtMovieTick\":");
                builder.Append(
                    descriptor.RequestedAtMovieTick.ToString(
                        CultureInfo.InvariantCulture));
                builder.Append(",\"restoreStrategy\":\"FullReplay\"");
                builder.Append(",\"scene\":");
                CanonicalJsonWriter.AppendString(
                    builder,
                    descriptor.SceneName);
                builder.Append(",\"sceneEpoch\":");
                builder.Append(
                    descriptor.SceneEpoch.ToString(
                        CultureInfo.InvariantCulture));
                builder.Append(",\"status\":");
                CanonicalJsonWriter.AppendString(
                    builder,
                    entry.Status.ToString());
                builder.Append('}');
            }

            builder.Append(']');
            return builder.ToString();
        }

        private void SetAutoSavePolicy(
            IReadOnlyDictionary<string, string> fields)
        {
            RequireFields(
                fields,
                "enabled",
                "intervalMovieTicks",
                "requestId",
                "retentionCount");
            if (!bool.TryParse(
                    fields["enabled"],
                    out var enabled)
                || !long.TryParse(
                    fields["intervalMovieTicks"],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var interval)
                || !int.TryParse(
                    fields["retentionCount"],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var retention))
            {
                throw new InvalidDataException(
                    "Auto-save policy values are invalid.");
            }

            var result = RequireReplaySaves().SetAutoSavePolicy(
                new AutoSavePolicy(
                    enabled,
                    interval,
                    retention));
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error);
            }
        }

        private void PublishCapabilityCatalog(string requestId)
        {
            var nativeCatalog =
                NativeCapabilityCatalog.Create(
                    nativeCapabilitiesRequested);
            var catalog =
                "runtime.ipc-v1=available\n"
                + "runtime.playback=available\n"
                + "runtime.controlled-step=available\n"
                + "runtime.run-until=available\n"
                + "runtime.recording=available\n"
                + "runtime.deterministic-rng="
                + controls.ReplayDeterministicRngStatus
                + ";profile="
                + RuntimeControlService.DeterministicRngProfile
                + ";manifestBound=true\n"
                + "runtime.typed-mutation=removed"
                + ";verificationEligibility="
                + RuntimeVerificationEligibility.Status
                + "\n"
                + "runtime.inspector="
                + (inspector != null ? "available" : "disabled")
                + "\n"
                + "runtime.replay-save="
                + (replaySaves != null ? "available" : "disabled")
                + "\n"
                + "restore.full-replay="
                + (replaySaves != null ? "available" : "disabled")
                + "\n"
                + "restore.keyframe-tail="
                + (
                    keyframeResolution.AcceleratorRegistered
                        ? "replay-only"
                        : "disabled"
                )
                + ";tier="
                + keyframeResolution.Tier
                + ";captureAllowed="
                + (
                    keyframeResolution.CaptureAllowed
                        ? "true"
                        : "false"
                )
                + ";fallback=restore.full-replay\n"
                + "native-host=available-via-companion\n"
                + string.Join(
                    "\n",
                    nativeCatalog.Select(
                        item =>
                            item.CapabilityId
                            + "="
                            + (
                                item.CapabilityId
                                    == NativeCapabilityCatalog
                                        .ProcessObserve
                                && nativeCapabilitiesRequested
                                    ? "requested"
                                    : item.Status.ToString()
                                        .ToLowerInvariant()
                            )
                            + ";version="
                            + item.SemanticVersion.ToString(
                                CultureInfo.InvariantCulture)
                            + ";permissions="
                            + item.Permissions
                            + ";requiresNativeHost="
                            + (
                                item.RequiresNativeHost
                                    ? "true"
                                    : "false"
                            )
                            + ";requiresBridge="
                            + (
                                item.RequiresBridge
                                    ? "true"
                                    : "false"
                            )
                            + ";requiresSafeBarrier="
                            + (
                                item.RequiresSafeBarrier
                                    ? "true"
                                    : "false"
                            )
                            + ";os="
                            + string.Join(
                                ",",
                                item.SupportedOperatingSystems)
                            + ";arch="
                            + string.Join(
                                ",",
                                item.SupportedArchitectures)
                            + ";buildWhitelist="
                            + (
                                item.BuildWhitelistIds.Count == 0
                                    ? "none"
                                    : string.Join(
                                        ",",
                                        item.BuildWhitelistIds)
                            )
                            + ";maxDurationMs="
                            + item.MaximumDurationMilliseconds
                                .ToString(
                                    CultureInfo.InvariantCulture)
                            + ";maxMemoryBytes="
                            + item.MaximumMemoryBytes.ToString(
                                CultureInfo.InvariantCulture)
                            + ";maxDiskBytes="
                            + item.MaximumDiskBytes.ToString(
                                CultureInfo.InvariantCulture)
                            + ";evidence="
                            + item.EvidenceSha256
                            + ";fallback="
                            + item.Fallback));
            Publish(
                IpcMessageTypes.CapabilityCatalog,
                new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["catalog"] = catalog,
                    ["requestId"] = requestId
                });
            emit(
                "native-capability-catalog",
                new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["catalogVersion"] = "1",
                    ["nativeHost"] =
                        "available-via-companion",
                    ["processObserve"] =
                        nativeCapabilitiesRequested
                            ? "requested"
                            : "experimental-disabled",
                    ["processObservePermissions"] =
                        "ProcessQuery",
                    ["processObserveBuildWhitelist"] =
                        NativeCapabilityCatalog
                            .SupportedBuildId,
                    ["processObserveRequiresBridge"] =
                        "false",
                    ["processObserveMaximumDurationMs"] =
                        "10000",
                    ["checkpoint"] = "unsupported",
                    ["fallback"] = "runtime-t09"
                });
            Publish(
                IpcMessageTypes.RestoreAccelerationStatus,
                new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["detail"] =
                        "T14 tier="
                        + keyframeResolution.Tier
                        + "; reasons="
                        + string.Join(
                            ",",
                            keyframeResolution.ReasonCodes)
                        + "; T09 full replay remains authoritative.",
                    ["plan"] = "FullReplay",
                    ["status"] =
                        keyframeResolution.AcceleratorRegistered
                            ? "replay-only"
                            : "disabled",
                    ["tier"] =
                        keyframeResolution.Tier.ToString(),
                    ["requestId"] = requestId
                });
        }

        private void ReportNativeEvidence(
            IReadOnlyDictionary<string, string> fields)
        {
            if (!nativeCapabilitiesRequested)
            {
                throw new InvalidOperationException(
                    "Native capabilities were not requested for this session.");
            }

            NativeCapabilityEvidenceProtocolV1.Validate(fields);
            var persisted =
                new SortedDictionary<string, string>(
                    StringComparer.Ordinal);
            foreach (var field in fields)
            {
                persisted.Add(field.Key, field.Value);
            }
            emit("native-capability-evidence", persisted);
            Publish(
                IpcMessageTypes.NativeCapabilityEvidence,
                persisted);
        }

        private void RequireSourceSlotApprovalBoundary()
        {
            if (preparedColdIntent != null || restoreHandle.HasValue || pendingMovieSeek != null
                || controls.PlaybackMode != PlaybackMode.Idle
                || (controls.ControlMode != SimulationControlMode.Paused
                    && !RuntimePauseController.IsStableTitleMenu()))
                throw new RuntimeCommandRejectionException("Busy", "Source slot approval requires an idle paused or title-menu source.");
        }

        private string PrepareColdRestore(
            ValidatedRuntimeCommand command, bool allowBaselineCapture)
        {
            var fields = command.Fields;
            ReplayLifecycleExecutionPlan? selectedPlan = null;
            if (fields.TryGetValue("lifecyclePlanObjectSha256", out var requestedPlanHash) && requestedPlanHash.Length != 0)
            {
                if (fields["operationKind"] != ColdRestoreOperationKind.ApplyBranchAndSeek.ToString()
                    || stagedLifecyclePlan == null || requestedPlanHash != stagedLifecyclePlanHash)
                    throw new RuntimeCommandRejectionException("LifecyclePlanUnavailable", "Upload the exact lifecycle plan before branch seek.");
                selectedPlan = stagedLifecyclePlan;
            }
            RequireFields(
                fields.Where(x => x.Key != "lifecyclePlanObjectSha256").ToDictionary(x => x.Key, x => x.Value),
                "companionAssemblySha256",
                "expectedMovieTick",
                "expectedSceneEpoch",
                "intentId",
                "observerAssemblySha256",
                "operationKind",
                "operationId",
                "replaySaveId",
                "requesterSurface",
                "requestId",
                "startupProfileSha256",
                "targetMovieTick");
            if (pendingColdBaseline != null || preparedColdIntent != null
                || restoreHandle.HasValue
                || pendingMovieSeek != null)
            {
                throw new RuntimeCommandRejectionException(
                    "Busy",
                    "A cold-restore preparation, restore, or movie seek is already active.");
            }

            var sourceStartup = startupAttestor.Capture();
            var menuSource = fields["operationKind"] == ColdRestoreOperationKind.RestoreReplaySave.ToString()
                && RuntimePauseController.IsStableTitleMenu()
                && journal.LastCommittedMovieTick < 0;
            if (sourceStartup.Status
                    != StartupProfileAttestationStatus.Verified
                || !menuSource && sourceStartup.RootStatus
                    != StartupRecordingRootStatus.Verified)
            {
                throw new RuntimeCommandRejectionException(
                    "StartupUnverified",
                    "Cold-restore source requires a verified startup profile and recording root; actual="
                    + sourceStartup.Status
                    + "/"
                    + sourceStartup.RootStatus
                    + ".");
            }

            if (controls.ControlMode == SimulationControlMode.Running)
            {
                // A title-menu source has no live journal to seal. Validate the
                // selected save before arming its terminal pause below.
                var pause = menuSource ? null : controls.Pause();
                if (pause != null && !pause.Success)
                {
                    throw new RuntimeCommandRejectionException(
                        "PauseRejected",
                        pause.Error);
                }
            }
            else if (controls.ControlMode != SimulationControlMode.Paused)
            {
                throw new RuntimeCommandRejectionException(
                    "UnsafePhase",
                    "Cold-restore preparation requires Running or Paused unified control.");
            }

            var nowUtc = DateTimeOffset.UtcNow;
            if (!Enum.TryParse(
                    fields["operationKind"],
                    ignoreCase: false,
                    out ColdRestoreOperationKind operationKind)
                || !Enum.IsDefined(
                    typeof(ColdRestoreOperationKind),
                    operationKind)
                || !long.TryParse(
                    fields["targetMovieTick"],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var requestedTargetMovieTick)
                || !long.TryParse(
                    fields["expectedMovieTick"],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var expectedMovieTick)
                || !int.TryParse(
                    fields["expectedSceneEpoch"],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var expectedSceneEpoch)
                || operationKind
                   == ColdRestoreOperationKind.RestoreReplaySave
                   && (requestedTargetMovieTick != -1
                       || expectedMovieTick != -1
                       || expectedSceneEpoch != -1
                       || string.IsNullOrEmpty(fields["replaySaveId"]))
                || operationKind
                   != ColdRestoreOperationKind.RestoreReplaySave
                   && (requestedTargetMovieTick < 0
                       || expectedMovieTick < 0
                       || expectedSceneEpoch < 0
                       || !string.IsNullOrEmpty(fields["replaySaveId"])))
            {
                throw new InvalidDataException(
                    "Cold-restore operation kind, replay-save ID, or target tick is invalid.");
            }

            if (operationKind
                != ColdRestoreOperationKind.RestoreReplaySave
                && (controls.PlaybackMode != PlaybackMode.Idle
                    || controls.InputBatchActive))
            {
                throw new RuntimeCommandRejectionException(
                    "UnsafePhase",
                    "Cold movie seek requires playback idle and no active input batch.");
            }

            if (operationKind
                != ColdRestoreOperationKind.RestoreReplaySave
                && (controls.AuthoritativeMovieTick != expectedMovieTick
                    || journal.CurrentSceneEpoch != expectedSceneEpoch))
            {
                throw new RuntimeCommandRejectionException(
                    "PreconditionFailed",
                    "Cold movie seek tick or scene epoch is stale.");
            }

            selectedPlan?.ValidateSeek(manifestSha256, requestedTargetMovieTick);
            if (operationKind != ColdRestoreOperationKind.RestoreReplaySave && selectedPlan == null)
            {
                var movie = selectedPlan?.Movie ?? controls.Movie ?? throw new RuntimeCommandRejectionException(
                    "NoCurrentMovie", "Load a canonical movie before cold seek.");
                var saves = RequireReplaySaves();
                var baseline = saves.FindNearestCompatible(movie, requestedTargetMovieTick, baselineOnly: true,
                    exactBaselineObjectSha256: selectedPlan?.BaselineObjectSha256);
                if (!baseline.Success)
                {
                    if (baseline.Code != "NoCompatibleBaseline" || !allowBaselineCapture)
                        throw new RuntimeCommandRejectionException(baseline.Code, baseline.Detail);
                    if (controls.ControlMode != SimulationControlMode.Paused || saves.PendingCount != 0)
                        throw new RuntimeCommandRejectionException("Busy", "Baseline persistence requires an idle paused save boundary.");
                    var request = saves.RequestManualSave("History edit baseline");
                    if (!request.Accepted)
                        throw new RuntimeCommandRejectionException("BaselineSaveRejected", request.Error);
                    pendingColdBaseline = new PendingColdBaseline(command, request.RequestId, movie);
                    return "Persisting the history-edit baseline without advancing gameplay.";
                }
            }

            using (var process = Process.GetCurrentProcess())
            {
                var gameExecutable = process.MainModule?.FileName
                                     ?? throw new InvalidOperationException(
                                         "Current game executable path is unavailable.");
                var gameDirectory = Path.GetDirectoryName(gameExecutable)
                                    ?? throw new InvalidOperationException(
                                        "Current game directory is unavailable.");
                var build = new ColdRestoreBuildFingerprint(
                    manifestSha256,
                    Sha256Utility.ComputeFileHex(gameExecutable),
                    Sha256Utility.ComputeFileHex(
                        Path.Combine(gameDirectory, "UnityPlayer.dll")),
                    Sha256Utility.ComputeFileHex(
                        Path.Combine(
                            gameDirectory,
                            "hollow_knight_Data",
                            "Managed",
                            "Assembly-CSharp.dll")),
                    Sha256Utility.ComputeFileHex(
                        typeof(RuntimeCommandDispatcher).Assembly.Location),
                    fields["companionAssemblySha256"],
                    fields["startupProfileSha256"],
                    fields["observerAssemblySha256"]);
                preparedColdIntent = RequireReplaySaves()
                    .PrepareColdRestoreIntent(
                        operationKind,
                        fields["replaySaveId"],
                        operationKind
                        == ColdRestoreOperationKind.RestoreReplaySave
                            ? null
                            : selectedPlan?.Movie ?? controls.Movie
                              ?? throw new RuntimeCommandRejectionException(
                                  "NoCurrentMovie",
                                  "Load a canonical movie before cold seek."),
                        requestedTargetMovieTick,
                        fields["intentId"],
                        fields["operationId"],
                        sessionId,
                        process.Id,
                        new DateTimeOffset(
                            process.StartTime.ToUniversalTime()),
                        fields["requesterSurface"],
                        build,
                        nowUtc,
                        nowUtc.AddMinutes(10),
                        menuSource, selectedPlan);
            }

            if (menuSource && controls.ControlMode == SimulationControlMode.Running)
            {
                var pause = controls.PauseForMenuRestore();
                if (!pause.Success)
                {
                    preparedColdIntent = null;
                    throw new RuntimeCommandRejectionException("PauseRejected", pause.Error);
                }
            }

            var intentBytes = ColdRestoreIntentCodec.Serialize(
                preparedColdIntent);
            preparedColdIntentSha256 = Sha256Utility.ComputeHex(intentBytes);
            Publish(
                IpcMessageTypes.ColdRestoreIntentPrepared,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["intentBase64"] = Convert.ToBase64String(intentBytes),
                    ["intentSha256"] = preparedColdIntentSha256,
                    ["operationId"] = preparedColdIntent.OperationId,
                    ["sourceMovieTick"] =
                        preparedColdIntent.SourceCommittedMovieTick.ToString(
                            CultureInfo.InvariantCulture),
                    ["sourceSceneEpoch"] =
                        preparedColdIntent.SourceSceneEpoch.ToString(
                            CultureInfo.InvariantCulture)
                });
            return "Cold-restore intent prepared; source pause boundary is armed.";
        }

        private bool TryExitFromPausedWindowShortcut()
        {
            pausedWindowExitPending |= PausedWindowExitShortcut.IsRequested();
            if (!pausedWindowExitPending || gameExitRequested || coldSourceExitRequested)
                return false;
            // Keep a detected request while persistence finishes. Do not
            // bypass the same save/restore checks used by the API.
            if (replaySaves == null)
                return false;
            try
            {
                RequestPausedGameExit();
            }
            catch (RuntimeCommandRejectionException exception)
                when (exception.ErrorCode == "Busy" || exception.ErrorCode == "UnsafePhase")
            {
                // An active input run must not leave a delayed quit behind
                // after the user resumes it. Only pending disk/work is retried.
                if (exception.ErrorCode == "UnsafePhase")
                    pausedWindowExitPending = false;
                return false;
            }
            pausedWindowExitPending = false;
            emit("paused-window-exit", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source"] = "foreground-alt-f4"
            });
            ExitApprovedGameProcess();
            return true;
        }

        private bool CanExitFailedSourceLifecycle => sourceLifecycleReload != null
            && NativeLifecycleTerminationBoundary.CanExitFailedSource(
                sourceLifecycleReload.Failure.Length != 0, sourceLifecycleReload.Elapsed.Elapsed.TotalSeconds);

        private string RequestPausedGameExit()
        {
            if (CanExitFailedSourceLifecycle)
            {
                // Only exit the process. Never release the lifecycle lease to
                // resume gameplay or report an unfinished native load as ready.
                var pendingSaves = RequireReplaySaves();
                if (pendingSaves.PendingCount != 0 || pendingSaves.IsRestoreActive
                    || preparedColdIntent != null)
                    throw new RuntimeCommandRejectionException("Busy", "Persistence work must finish before exiting failed native reload.");
                gameExitRequested = true;
                emit("native-reload-exit-requested", new Dictionary<string, string> { ["reason"] = sourceLifecycleReload!.Failure });
                return "Exiting failed native reload; completed TAS saves remain available. Start a new recording session after exit.";
            }
            if (controls.ControlMode != SimulationControlMode.Paused
                || controls.PlaybackMode != PlaybackMode.Idle || controls.InputBatchActive)
                throw new RuntimeCommandRejectionException("UnsafePhase", "Pause and finish active input before exiting.");
            var saves = RequireReplaySaves();
            if (saves.PendingCount != 0 || saves.IsRestoreActive
                || preparedColdIntent != null || pendingMovieSeek != null
                || runUntilMovieTick.HasValue)
                throw new RuntimeCommandRejectionException("Busy", "Wait for pending save or restore work before exiting.");
            if (gameExitRequested)
                throw new RuntimeCommandRejectionException("DuplicateExit", "Game exit is already requested.");
            gameExitRequested = true;
            return "Game exit accepted at the paused boundary; no gameplay step requested.";
        }

        private string BeginSourceLifecycleReload(string slotText, bool atBoundary)
        {
            if (!atBoundary || controls.ControlMode != SimulationControlMode.Paused
                || !controls.UsesCompletedFrameBoundaryGate || controls.PlaybackMode != PlaybackMode.Idle
                || controls.InputBatchActive || !journal.IsAvailable || runner == null)
                throw new RuntimeCommandRejectionException("UnsafePhase", "Reload requires a verified paused recording boundary.");
            if (!int.TryParse(slotText, NumberStyles.None, CultureInfo.InvariantCulture, out var slot) || slot < 1 || slot > 4)
                throw new InvalidDataException("slot must be in [1,4].");
            var saves = RequireReplaySaves();
            if (saves.PendingCount != 0 || saves.IsRestoreActive || preparedColdIntent != null
                || pendingMovieSeek != null || runUntilMovieTick.HasValue || gameExitRequested)
                throw new RuntimeCommandRejectionException("Busy", "Finish pending work before reloading.");
            var path = SavePathResolver.Current.GetSlotPath(slot, ".dat");
            var size = new FileInfo(path).Length;
            if (size <= 0 || size > ReplayLifecycleLog.MaximumSlotBytes)
                throw new InvalidDataException("Existing slot is empty or too large.");
            var bytes = File.ReadAllBytes(path);
            var moddedPath = SavePathResolver.Current.GetSlotPath(slot, ".modded.json");
            byte[]? moddedBytes = null;
            if (File.Exists(moddedPath))
            {
                if (new FileInfo(moddedPath).Length > ReplayLifecycleLog.MaximumSlotBytes)
                    throw new InvalidDataException("Modded slot is too large.");
                moddedBytes = File.ReadAllBytes(moddedPath);
            }
            var movie = BuildJournalMovie(includeLifecycle: true);
            var tick = journal.LastCommittedMovieTick;
            var operation = new SourceLifecycleReload(slot, bytes, movie, tick,
                MoviePrefixIdentity.ComputeSha256(movie, checked(tick + 1)), journal.NextLifecycleSequence);
            operation.ModdedBytes = moddedBytes;
            operation.InputLease = new NativeLifecycleInputLease(
                InputHandler.Instance?.inputActions ?? throw new InvalidOperationException("Hero input is unavailable."),
                FailSourceLifecycleReload);
            try { journal.BeginLifecycleOperation(operation.Record(ReplayLifecycleKind.ReturnToMenu), movie, null); }
            catch { operation.InputLease.Dispose(); throw; }
            sourceLifecycleReload = operation;
            lastNativeReloadPhase = "ReturningToMenu";
            lastNativeReloadFailure = string.Empty;
            try
            {
                RequireSuccess(controls.Resume());
                runner.StartCoroutine(ObserveSourceMenuReturn(operation,
                    GameManager.instance.ReturnToMainMenu(GameManager.ReturnToMainMenuSaveModes.DontSave)));
            }
            catch (Exception exception) { FailSourceLifecycleReload(exception.Message); }
            return "Native reload accepted; wait for nativeReloadPhase=Completed before issuing more input.";
        }

        private void PollSourceLifecycleReload(bool atBoundary)
        {
            var operation = sourceLifecycleReload;
            if (operation == null) return;
            try
            {
                if (operation.Failure.Length == 0 && operation.Loading && operation.NativeCompleted && !operation.LoadSucceeded)
                    FailSourceLifecycleReload("Native LoadGame reported failure for slot " + operation.Slot + ".");
                if (operation.Failure.Length == 0 && operation.Elapsed.Elapsed.TotalSeconds > 120)
                    FailSourceLifecycleReload("Native reload exceeded its 120 second deadline.");
                var menu = RuntimePauseController.IsStableTitleMenu();
                var manager = GameManager.instance;
                var gameplay = manager != null && manager.gameState == GameState.PLAYING
                    && !manager.IsInSceneTransition && HeroController.SilentInstance?.gameObject.activeInHierarchy == true;
                if (operation.Failure.Length != 0)
                {
                    if (!NativeLifecycleTerminationBoundary.CanPause(operation.NativeStarted, operation.NativeCompleted,
                        operation.Loading, operation.LoadSucceeded, operation.CallbackFrame, Time.frameCount, menu, gameplay)) return;
                    // Do not stop a native loading coroutine halfway through.
                    // Pause when it reaches a safe destination, retaining failure.
                    if (controls.ControlMode != SimulationControlMode.Paused)
                    {
                        if (menu || gameplay) RequireSuccess(controls.PauseForSourceLifecycle(operation.Sequence));
                    }
                    else if (atBoundary)
                    {
                        DetachSourceNativeLoadHook();
                        operation.InputLease?.Dispose();
                        sourceLifecycleReload = null;
                        lastNativeReloadPhase = "Failed";
                        if (!server.IsConnected) controls.CleanupForDisconnect();
                        PublishRuntimeStatus();
                    }
                    return;
                }
                if (!operation.NativeCompleted || !journal.IsLifecycleDestinationReady) return;
                if (controls.ControlMode != SimulationControlMode.Paused)
                {
                    RequireSuccess(controls.PauseForSourceLifecycle(operation.Sequence));
                    return;
                }
                if (!atBoundary) return;
                if (operation.Loading) { DetachSourceNativeLoadHook(); operation.InputLease?.Dispose(); }
                journal.CompleteLifecycleOperation(operation.Sequence);
                if (operation.Loading)
                {
                    sourceLifecycleReload = null;
                    lastNativeReloadPhase = "Completed";
                    PublishRuntimeStatus();
                    return;
                }
                operation.Sequence++;
                journal.BeginLifecycleOperation(operation.Record(ReplayLifecycleKind.LoadSlot), operation.Movie, operation.SlotBytes, operation.ModdedBytes);
                operation.Loading = true;
                lastNativeReloadPhase = "LoadingSlot";
                operation.NativeStarted = false;
                operation.NativeCompleted = false;
                RequireSuccess(controls.Resume());
                On.GameManager.LoadGame += ObserveSourceNativeLoad;
                sourceNativeLoadHookAttached = true;
                var loadManager = GameManager.instance ?? throw new InvalidOperationException("Game manager is unavailable for native load.");
                operation.NativeStarted = true;
                loadManager.LoadGameFromUI(operation.Slot);
            }
            catch (Exception exception) { FailSourceLifecycleReload(exception.Message); }
        }

        private void FailSourceLifecycleReload(string reason)
        {
            var operation = sourceLifecycleReload;
            if (operation == null || operation.Failure.Length != 0) return;
            if (string.IsNullOrWhiteSpace(reason)) reason = "Native reload failed without an error detail.";
            operation.Failure = reason;
            lastNativeReloadFailure = reason;
            lastNativeReloadPhase = "FailedAwaitingPause";
            if (journal.ActiveLifecycleSequence is int sequence)
            {
                try { journal.FailLifecycleOperation(sequence, reason); }
                catch (Exception exception)
                {
                    lastNativeReloadFailure += "; failure persistence: " + exception.Message;
                }
            }
            emit("native-reload-failed", new Dictionary<string, string> { ["reason"] = reason });
        }

        private bool sourceNativeLoadHookAttached;
        private System.Collections.IEnumerator ObserveSourceMenuReturn(SourceLifecycleReload operation, System.Collections.IEnumerator native)
        {
            // StartCoroutine can fail before advancing this wrapper. Do not
            // wait for native completion when native code was never entered.
            operation.NativeStarted = true;
            yield return native;
            operation.NativeCompleted = true;
        }
        private void ObserveSourceNativeLoad(On.GameManager.orig_LoadGame original, GameManager self, int slot, Action<bool> callback)
        {
            var operation = sourceLifecycleReload;
            if (operation == null || !operation.Loading || operation.Slot != slot)
            { original(self, slot, callback); return; }
            original(self, slot, success =>
            {
                try { callback?.Invoke(success); }
                finally
                {
                    operation.LoadSucceeded = success;
                    operation.CallbackFrame = Time.frameCount;
                    operation.NativeCompleted = true;
                }
            });
        }
        private void DetachSourceNativeLoadHook()
        {
            if (!sourceNativeLoadHookAttached) return;
            On.GameManager.LoadGame -= ObserveSourceNativeLoad;
            sourceNativeLoadHookAttached = false;
        }

        private sealed class SourceLifecycleReload
        {
            public SourceLifecycleReload(int slot, byte[] bytes, MovieDocument movie, long tick, string prefix, int sequence)
            { Slot = slot; SlotBytes = bytes; Movie = movie; Tick = tick; Prefix = prefix; Sequence = sequence; }
            public int Slot { get; }
            public byte[] SlotBytes { get; }
            public byte[]? ModdedBytes { get; set; }
            public MovieDocument Movie { get; }
            public long Tick { get; }
            public string Prefix { get; }
            public int Sequence { get; set; }
            public bool Loading { get; set; }
            public bool NativeStarted { get; set; }
            public bool NativeCompleted { get; set; }
            public bool LoadSucceeded { get; set; }
            public int CallbackFrame { get; set; }
            public string Failure { get; set; } = string.Empty;
            public Stopwatch Elapsed { get; } = Stopwatch.StartNew();
            public NativeLifecycleInputLease? InputLease { get; set; }
            public ReplayLifecycleRecord Record(ReplayLifecycleKind kind) => new ReplayLifecycleRecord(
                Sequence, Tick, Prefix, kind, kind == ReplayLifecycleKind.LoadSlot ? Slot : 0,
                kind == ReplayLifecycleKind.LoadSlot ? Sha256Utility.ComputeHex(SlotBytes) : string.Empty,
                ReplayLifecycleOutcome.Waiting, 0, string.Empty,
                kind == ReplayLifecycleKind.LoadSlot ? (ModdedBytes == null ? string.Empty : Sha256Utility.ComputeHex(ModdedBytes)) : null);
        }

        private bool gameSlotLoadRequested;

        private string LoadExistingGameSlot(string slotText)
        {
            if (!int.TryParse(slotText, NumberStyles.None, CultureInfo.InvariantCulture, out var slot)
                || slot < 1 || slot > 4)
                throw new InvalidDataException("slot must be in [1,4].");
            if (controls.ControlMode != SimulationControlMode.Running
                || !RuntimePauseController.IsStableTitleMenu()
                || controls.PlaybackMode != PlaybackMode.Idle || controls.InputBatchActive
                || !journal.CanLoadInitialGameSlot)
                throw new RuntimeCommandRejectionException("UnsafePhase",
                    "Load an existing slot from a fresh, running title-menu session only.");
            var saves = RequireReplaySaves();
            if (gameSlotLoadRequested || gameExitRequested || saves.PendingCount != 0
                || saves.IsRestoreActive || preparedColdIntent != null || pendingMovieSeek != null
                || runUntilMovieTick.HasValue)
                throw new RuntimeCommandRejectionException("Busy", "Finish pending work before loading a game slot.");
            // This is the desktop slot convention shared by the baseline provider.
            // Do not create, copy, overwrite, or substitute another slot here.
            var path = SavePathResolver.Current.GetSlotPath(slot, ".dat");
            if (!File.Exists(path))
                throw new RuntimeCommandRejectionException("SaveSlotUnavailable", "The selected existing save slot is unavailable.");
            gameSlotLoadRequested = true;
            GameManager.instance.LoadGameFromUI(slot);
            return "Native game-slot load requested; wait for recording origin readiness before TAS input.";
        }

        private string QuiesceColdRestoreSource(
            IReadOnlyDictionary<string, string> fields)
        {
            RequireFields(
                fields,
                "claimId",
                "companionInstanceId",
                "intentSha256",
                "operationId",
                "requestId");
            VerifyPreparedColdSourceBinding(fields);
            if (coldSourceQuiesced)
            {
                throw new RuntimeCommandRejectionException(
                    "DuplicateQuiesce",
                    "Cold-restore source was already quiesced.");
            }

            if (controls.ControlMode != SimulationControlMode.Paused)
            {
                throw new RuntimeCommandRejectionException(
                    "UnsafePhase",
                    "Cold-restore source quiesce requires a completed-frame pause.");
            }

            if (controls.PlaybackMode == PlaybackMode.Replaying)
            {
                var stop = controls.StopReplay();
                if (!stop.Success)
                {
                    throw new InvalidOperationException(
                        "Source replay input could not be neutralized: "
                        + stop.Error);
                }
            }

            preparedColdClaimId = fields["claimId"];
            coldSourceQuiesced = true;
            Publish(
                IpcMessageTypes.ColdRestoreSourceQuiesced,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["claimId"] = preparedColdClaimId,
                    ["intentSha256"] = preparedColdIntentSha256,
                    ["operationId"] = preparedColdIntent!.OperationId,
                    ["sourceSessionId"] = sessionId
                });
            return "Cold-restore source is quiesced at a completed-frame boundary.";
        }

        private string ExitColdRestoreSource(
            IReadOnlyDictionary<string, string> fields)
        {
            RequireFields(
                fields,
                "claimId",
                "companionInstanceId",
                "intentSha256",
                "operationId",
                "requestId");
            VerifyPreparedColdSourceBinding(fields);
            if (!coldSourceQuiesced
                || !string.Equals(
                    preparedColdClaimId,
                    fields["claimId"],
                    StringComparison.Ordinal))
            {
                throw new RuntimeCommandRejectionException(
                    "SourceNotQuiesced",
                    "Cold-restore source must be quiesced by the same one-shot claim before exit.");
            }

            if (coldSourceExitRequested)
            {
                throw new RuntimeCommandRejectionException(
                    "DuplicateExit",
                    "Cold-restore source exit was already requested.");
            }

            coldSourceExitRequested = true;
            return "Cold-restore source exit accepted at the paused boundary.";
        }

        private void PollPendingColdBaseline()
        {
            var pending = pendingColdBaseline;
            if (pending == null) return;
            if (!server.IsConnected)
            {
                pendingColdBaseline = null;
                return;
            }
            var result = RequireReplaySaves().Operations.LastOrDefault(
                value => value.RequestId == pending.SaveRequestId);
            string? error = null;
            if (controls.ControlMode != SimulationControlMode.Paused
                || (!ReferenceEquals(controls.Movie, pending.Movie) && !ReferenceEquals(stagedLifecyclePlan?.Movie, pending.Movie)))
                error = "Source mode or movie changed while the history-edit baseline was saving.";
            else if (Stopwatch.GetTimestamp() >= pending.Deadline)
                error = "History-edit baseline persistence timed out; no cold restore was started.";
            else if (result == null) return;
            else if (result.Status != ReplaySaveStatus.Ready)
                error = "History-edit baseline persistence failed: " + result.Error;

            pendingColdBaseline = null;
            if (error == null)
            {
                // Re-run all startup, tick, scene and baseline checks. Never
                // enqueue a second capture if the saved baseline does not match.
                Dispatch(pending.Command, allowBaselineCapture: false);
                return;
            }
            Publish(IpcMessageTypes.CommandRejected, new Dictionary<string, string>
            {
                ["command"] = pending.Command.MessageType,
                ["requestId"] = pending.Command.Fields["requestId"],
                ["errorCode"] = "BaselinePersistenceFailed",
                ["detail"] = error
            });
        }

        private sealed class PendingColdBaseline
        {
            internal PendingColdBaseline(ValidatedRuntimeCommand command, string saveRequestId, MovieDocument movie)
            {
                Command = command;
                SaveRequestId = saveRequestId;
                Movie = movie;
                Deadline = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
            }
            internal ValidatedRuntimeCommand Command { get; }
            internal string SaveRequestId { get; }
            internal MovieDocument Movie { get; }
            internal long Deadline { get; }
        }

        private string CancelColdRestoreSource(
            IReadOnlyDictionary<string, string> fields)
        {
            RequireFields(
                fields,
                "claimId",
                "companionInstanceId",
                "intentSha256",
                "operationId",
                "requestId");
            VerifyPreparedColdSourceBinding(fields);
            if (coldSourceQuiesced || coldSourceExitRequested)
            {
                throw new RuntimeCommandRejectionException(
                    "SourceAlreadyQuiesced",
                    "A quiesced cold-restore source cannot be resumed as an unmodified source operation.");
            }

            var resume = controls.Resume();
            if (!resume.Success
                && controls.ControlMode != SimulationControlMode.Running)
            {
                throw new RuntimeCommandRejectionException(
                    "ResumeRejected",
                    resume.Error);
            }

            preparedColdIntent = null;
            preparedColdIntentSha256 = string.Empty;
            preparedColdClaimId = string.Empty;
            coldSourceQuiesced = false;
            coldSourceExitRequested = false;
            return "Cold-restore source preparation was cancelled and unified control resumed.";
        }

        private void VerifyPreparedColdSourceBinding(
            IReadOnlyDictionary<string, string> fields)
        {
            if (preparedColdIntent == null
                || !string.Equals(
                    preparedColdIntent.OperationId,
                    fields["operationId"],
                    StringComparison.Ordinal)
                || !string.Equals(
                    preparedColdIntentSha256,
                    fields["intentSha256"],
                    StringComparison.Ordinal))
            {
                throw new RuntimeCommandRejectionException(
                    "PreconditionFailed",
                    "Cold-restore source operation or intent hash is stale.");
            }

            ColdRestoreIntent.RequireIdentifier(
                fields["claimId"],
                "claimId");
            var companionInstanceId = ColdRestoreIntent.RequireIdentifier(
                fields["companionInstanceId"],
                "companionInstanceId");
            if (!string.Equals(
                    companionInstanceId,
                    server.AuthenticatedCompanionInstanceId,
                    StringComparison.Ordinal))
            {
                throw new RuntimeCommandRejectionException(
                    "CompanionBindingMismatch",
                    "Cold-restore source command did not come from the authenticated Companion.");
            }
        }

        private void StartMovieSeek(
            IReadOnlyDictionary<string, string> fields)
        {
            RequireFields(
                fields,
                "expectedMovieTick",
                "expectedSceneEpoch",
                "requestId",
                "targetMovieTick");
            if (pendingMovieSeek != null || restoreHandle.HasValue)
            {
                throw new RuntimeCommandRejectionException(
                    "Busy",
                    "Only one restore or movie seek may be active.");
            }

            if (!long.TryParse(
                    fields["targetMovieTick"],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var targetMovieTick)
                || targetMovieTick < 0
                || !long.TryParse(
                    fields["expectedMovieTick"],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var expectedMovieTick)
                || expectedMovieTick < 0
                || !int.TryParse(
                    fields["expectedSceneEpoch"],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var expectedSceneEpoch)
                || expectedSceneEpoch < 0)
            {
                throw new InvalidDataException(
                    "Movie seek tick or scene binding is invalid.");
            }

            if (controls.ControlMode != SimulationControlMode.Paused
                || controls.PlaybackMode != PlaybackMode.Idle
                || controls.InputBatchActive)
            {
                throw new RuntimeCommandRejectionException(
                    "UnsafePhase",
                    "Movie seek requires unified control to be Paused and playback idle.");
            }

            if (controls.AuthoritativeMovieTick != expectedMovieTick
                || journal.CurrentSceneEpoch != expectedSceneEpoch)
            {
                throw new RuntimeCommandRejectionException(
                    "PreconditionFailed",
                    "Movie seek tick or scene epoch is stale.");
            }

            var targetMovie = controls.Movie
                              ?? throw new RuntimeCommandRejectionException(
                                  "NoCurrentMovie",
                                  "Load a canonical movie before seeking.");
            var selection = RequireReplaySaves().FindNearestCompatible(
                targetMovie,
                targetMovieTick);
            if (!selection.Success || selection.Descriptor == null)
            {
                throw new RuntimeCommandRejectionException(
                    selection.Code,
                    selection.Detail);
            }

            var prepare = controls.PrepareForReplayRestore();
            if (!prepare.Success)
            {
                throw new RuntimeCommandRejectionException(
                    "UnsafePhase",
                    prepare.Error);
            }

            restoreHandle = RequireReplaySaves().BeginRestore(
                selection.Descriptor.ReplaySaveId);
            lastRestorePhase = null;
            lastRestoreProgress = null;
            pendingMovieSeek = new PendingMovieSeek(
                fields["requestId"],
                targetMovie,
                new MovieCanonicalWriter().ComputeMovieId(targetMovie),
                targetMovieTick,
                selection.Descriptor.ReplaySaveId,
                selection.Descriptor.EffectiveMovieTick);
            PublishMovieSeekProgress(
                pendingMovieSeek,
                "Restoring",
                "Restoring the nearest exact-prefix replay checkpoint.",
                false,
                string.Empty);
        }

        private string BeginColdRestore(
            IReadOnlyDictionary<string, string> fields)
        {
            RequireFields(
                fields,
                "claimId",
                "companionInstanceId",
                "intentBase64",
                "intentSha256",
                "requestId");
            if (restoreHandle.HasValue || pendingMovieSeek != null)
            {
                throw new RuntimeCommandRejectionException(
                    "Busy",
                    "Only one restore or movie seek may be active.");
            }

            if (!string.IsNullOrEmpty(claimedColdIntentSha256))
            {
                throw new RuntimeCommandRejectionException(
                    "DuplicateClaim",
                    "This fresh Runtime process already consumed a cold-restore claim.");
            }

            var companionInstanceId = ColdRestoreIntent.RequireIdentifier(
                fields["companionInstanceId"],
                "companionInstanceId");
            if (!string.Equals(
                    companionInstanceId,
                    server.AuthenticatedCompanionInstanceId,
                    StringComparison.Ordinal))
            {
                throw new RuntimeCommandRejectionException(
                    "CompanionBindingMismatch",
                    "Cold-restore claim was not submitted by the authenticated Companion instance.");
            }

            ColdRestoreIntent.RequireIdentifier(
                fields["claimId"],
                "claimId");
            byte[] intentBytes;
            try
            {
                intentBytes = Convert.FromBase64String(
                    fields["intentBase64"]);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException(
                    "Cold-restore intent is not canonical base64.",
                    exception);
            }

            if (intentBytes.Length == 0
                || intentBytes.Length > ColdRestoreIntentCodec.MaximumBytes)
            {
                throw new InvalidDataException(
                    "Cold-restore intent size is outside the allowed range.");
            }

            var intentSha256 = Sha256Utility.ComputeHex(intentBytes);
            if (!string.Equals(
                    intentSha256,
                    fields["intentSha256"],
                    StringComparison.Ordinal))
            {
                throw new RuntimeCommandRejectionException(
                    "IntentHashMismatch",
                    "Cold-restore intent hash does not match the claim.");
            }

            var intent = ColdRestoreIntentCodec.Deserialize(intentBytes);
            var targetStartup = startupAttestor.Capture();
            if (targetStartup.Status
                    != StartupProfileAttestationStatus.Verified
                || !string.Equals(
                    targetStartup.RunId,
                    intent.OperationId,
                    StringComparison.Ordinal))
            {
                throw new RuntimeCommandRejectionException(
                    "StartupUnverified",
                    "Cold target startup profile/run binding is not verified.");
            }
            var prepare = controls.PrepareForReplayRestore();
            if (!prepare.Success)
            {
                throw new RuntimeCommandRejectionException(
                    "UnsafePhase",
                    prepare.Error);
            }

            claimedColdIntentSha256 = intentSha256;
            claimedColdClaimId = fields["claimId"];
            restoreHandle = RequireReplaySaves().BeginColdRestore(
                intent,
                intent.BuildFingerprint,
                DateTimeOffset.UtcNow);
            lastRestorePhase = null;
            lastRestoreProgress = null;
            return "Fresh Runtime claimed cold-restore operation "
                   + intent.OperationId
                   + ".";
        }

        private void PollMovieSeek()
        {
            var seek = pendingMovieSeek;
            if (seek == null)
            {
                return;
            }

            try
            {
                if (seek.Phase == PendingMovieSeekPhase.Restoring)
                {
                    var progress = lastRestoreProgress;
                    if (progress == null)
                    {
                        return;
                    }

                    if (progress.Phase == ReplayRestorePhase.Failed
                        || progress.Phase == ReplayRestorePhase.Cancelled)
                    {
                        FailMovieSeek(
                            seek,
                            "RestoreFailed",
                            progress.Detail);
                        return;
                    }

                    if (progress.Phase != ReplayRestorePhase.Paused)
                    {
                        return;
                    }

                    if (progress.TargetMovieTick
                        != seek.CheckpointMovieTick
                        || progress.StrictSemanticEquivalent != true)
                    {
                        FailMovieSeek(
                            seek,
                            "RestoreNotEquivalent",
                            "Replay checkpoint did not reach its exact verified paused target.");
                        return;
                    }

                    var resumed = RequireReplaySaves().ResumeRestore(
                        RequireRestoreHandle());
                    if (!resumed.Success)
                    {
                        FailMovieSeek(
                            seek,
                            "RestoreHandoffFailed",
                            resumed.Error);
                        return;
                    }

                    restoreHandle = null;
                    lastRestorePhase = null;
                    lastRestoreProgress = resumed.Progress;
                    var pause = controls.Pause();
                    if (!pause.Success)
                    {
                        FailMovieSeek(
                            seek,
                            "RestoreHandoffFailed",
                            pause.Error);
                        return;
                    }

                    seek.Phase = PendingMovieSeekPhase.HandoffPausing;
                    PublishMovieSeekProgress(
                        seek,
                        "HandoffPausing",
                        "Verified restore was handed to unified paused control without permitting another input tick.",
                        false,
                        string.Empty);
                    return;
                }

                if (seek.Phase == PendingMovieSeekPhase.HandoffPausing)
                {
                    if (controls.ControlMode == SimulationControlMode.Faulted)
                    {
                        FailMovieSeek(
                            seek,
                            "ControlFault",
                            "Unified control faulted during restore handoff.");
                        return;
                    }

                    if (controls.ControlMode != SimulationControlMode.Paused)
                    {
                        return;
                    }

                    if (controls.AuthoritativeMovieTick
                        != seek.CheckpointMovieTick)
                    {
                        FailMovieSeek(
                            seek,
                            "TickMismatch",
                            "No-input restore handoff changed the authoritative "
                            + "movie tick: expected="
                            + seek.CheckpointMovieTick.ToString(
                                CultureInfo.InvariantCulture)
                            + " actual="
                            + controls.AuthoritativeMovieTick.ToString(
                                CultureInfo.InvariantCulture)
                            + ".");
                        return;
                    }

                    var tailTicks = seek.TargetMovieTick
                                    - seek.CheckpointMovieTick;
                    if (tailTicks == 0)
                    {
                        CompleteMovieSeek(seek);
                        return;
                    }

                    var tail = MovieInputSlice.Extract(
                        seek.TargetMovie,
                        checked(seek.CheckpointMovieTick + 1),
                        tailTicks,
                        "seek-tail.hktas");
                    controls.SetMovie(tail);
                    var run = controls.StartInputBatch(
                        checked((int)tailTicks));
                    if (!run.Success)
                    {
                        FailMovieSeek(
                            seek,
                            "TailReplayRejected",
                            run.Error);
                        return;
                    }

                    seek.Phase = PendingMovieSeekPhase.ReplayingTail;
                    PublishMovieSeekProgress(
                        seek,
                        "ReplayingTail",
                        "Replaying the modified canonical short tail from the compatible checkpoint.",
                        false,
                        string.Empty);
                    return;
                }

                if (seek.Phase == PendingMovieSeekPhase.ReplayingTail)
                {
                    if (controls.InputBatchActive
                        || controls.PlaybackMode != PlaybackMode.Idle)
                    {
                        return;
                    }

                    if (controls.ControlMode == SimulationControlMode.Paused
                        && controls.AuthoritativeMovieTick
                           == seek.TargetMovieTick)
                    {
                        CompleteMovieSeek(seek);
                        return;
                    }

                    FailMovieSeek(
                        seek,
                        "TailReplayMismatch",
                        "Short-tail replay stopped without reaching the exact target tick in Paused mode.");
                }
            }
            catch (Exception exception)
            {
                FailMovieSeek(
                    seek,
                    "SeekFault",
                    exception.GetType().Name + ": " + exception.Message);
            }
        }

        private void CompleteMovieSeek(PendingMovieSeek seek)
        {
            controls.SetMovie(seek.TargetMovie);
            PublishMovieSeekProgress(
                seek,
                "Completed",
                "Movie seek completed at the exact target tick under unified paused control.",
                true,
                string.Empty);
            pendingMovieSeek = null;
        }

        private void FailMovieSeek(
            PendingMovieSeek seek,
            string errorCode,
            string detail)
        {
            try
            {
                if (restoreHandle.HasValue && replaySaves != null)
                {
                    var progress = replaySaves.Poll(restoreHandle.Value);
                    if (progress.Phase == ReplayRestorePhase.Paused)
                    {
                        replaySaves.ResumeRestore(restoreHandle.Value);
                    }
                    else if (!progress.IsTerminal)
                    {
                        replaySaves.CancelRestore(restoreHandle.Value);
                    }
                }
            }
            catch
            {
                // Unified cleanup below remains mandatory.
            }

            restoreHandle = null;
            lastRestorePhase = null;
            controls.CleanupForDisconnect();
            controls.SetMovie(seek.TargetMovie);
            if (GameManager.instance != null
                && GameManager.instance.gameState == GameState.PLAYING)
            {
                controls.Pause();
            }

            PublishMovieSeekProgress(
                seek,
                "Failed",
                detail,
                true,
                errorCode);
            pendingMovieSeek = null;
        }

        private void PublishMovieSeekProgress(
            PendingMovieSeek seek,
            string phase,
            string detail,
            bool terminal,
            string errorCode)
        {
            Publish(
                IpcMessageTypes.MovieSeekProgress,
                new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["branchMovieId"] = seek.TargetMovieId,
                    ["checkpointMovieTick"] =
                        seek.CheckpointMovieTick.ToString(
                            CultureInfo.InvariantCulture),
                    ["currentMovieTick"] =
                        controls.AuthoritativeMovieTick.ToString(
                            CultureInfo.InvariantCulture),
                    ["detail"] = detail,
                    ["errorCode"] = errorCode,
                    ["phase"] = phase,
                    ["replaySaveId"] = seek.ReplaySaveId,
                    ["requestId"] = seek.RequestId,
                    ["restoreStrategy"] = "ReplayCheckpointPlusDeterministicTail",
                    ["targetMovieTick"] =
                        seek.TargetMovieTick.ToString(
                            CultureInfo.InvariantCulture),
                    ["terminal"] = terminal ? "true" : "false"
                });
        }

        private void PollRestore()
        {
            if (!restoreHandle.HasValue || replaySaves == null)
            {
                return;
            }

            ReplayRestoreProgress progress;
            try
            {
                progress = replaySaves.Poll(
                    restoreHandle.Value);
            }
            catch (Exception exception)
            {
                Publish(
                    IpcMessageTypes.Fault,
                    new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["detail"] =
                            "Replay restore polling failed: "
                            + exception.Message
                    });
                restoreHandle = null;
                return;
            }

            lastRestoreProgress = progress;
            if (lastRestorePhase != progress.Phase
                || progress.IsTerminal)
            {
                lastRestorePhase = progress.Phase;
                // Cold boundaries do not run ordinary LateUpdate status
                // publication. Expose phase changes to Studio/AI immediately.
                PublishRuntimeStatus();
                Publish(
                    IpcMessageTypes.ReplaySaveRestoreProgress,
                    new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["currentMovieTick"] =
                            progress.CurrentMovieTick.ToString(
                                CultureInfo.InvariantCulture),
                        ["actualSemanticSha256"] =
                            progress.ActualSemanticSha256,
                        ["actualVerificationSha256"] =
                            progress.ActualVerificationSha256,
                        ["bindingRestoreEquivalent"] =
                            OptionalBoolean(
                                progress.BindingRestoreEquivalent),
                        ["detail"] = progress.Detail,
                        ["expectedSemanticSha256"] =
                            progress.ExpectedSemanticSha256,
                        ["expectedVerificationSha256"] =
                            progress.ExpectedVerificationSha256,
                        ["fraction"] =
                            progress.Fraction.ToString(
                                "R",
                                CultureInfo.InvariantCulture),
                        ["equivalenceClass"] =
                            progress.EquivalenceClass.ToString(),
                        ["operationId"] = progress.OperationId,
                        ["nextMovieTick"] =
                            progress.NextMovieTick.ToString(
                                CultureInfo.InvariantCulture),
                        ["phase"] = progress.Phase.ToString(),
                        ["plan"] = "FullReplay",
                        ["restoreStrategy"] =
                            progress.Strategy.ToString(),
                        ["claimId"] =
                            progress.Strategy
                            == ReplayRestoreStrategy
                                .VanillaEquivalentColdReplay
                                ? claimedColdClaimId
                                : string.Empty,
                        ["intentSha256"] =
                            progress.Strategy
                            == ReplayRestoreStrategy
                                .VanillaEquivalentColdReplay
                                ? claimedColdIntentSha256
                                : string.Empty,
                        ["requiresOverwriteApproval"] =
                            progress.RequiresOverwriteApproval
                                ? "true"
                                : "false",
                        ["semanticProjectionId"] =
                            progress.SemanticProjectionId,
                        ["settingsRestoreEquivalent"] =
                            OptionalBoolean(
                                progress.SettingsRestoreEquivalent),
                        ["status"] = progress.Status.ToString(),
                        ["strictSemanticEquivalent"] =
                            OptionalBoolean(
                                progress.StrictSemanticEquivalent),
                        ["targetMovieTick"] =
                            progress.TargetMovieTick.ToString(
                                CultureInfo.InvariantCulture),
                        ["targetVerification"] =
                            progress.TargetVerification.ToString()
                    });
            }

            if (progress.IsTerminal)
            {
                restoreHandle = null;
                lastRestorePhase = null;
            }
        }

        private bool IsColdRestoreBoundaryActive()
        {
            return lastRestoreProgress != null
                   && lastRestoreProgress.Strategy
                      == ReplayRestoreStrategy.VanillaEquivalentColdReplay
                   && (lastRestoreProgress.Phase
                       == ReplayRestorePhase.BaselineReady
                       || lastRestoreProgress.Phase
                       == ReplayRestorePhase.PausedAtTarget);
        }

        private bool IsAllowedColdBoundaryCommand(string messageType)
        {
            if (lastRestoreProgress == null)
            {
                return false;
            }

            // The supervisor must attest the newly frozen baseline before it
            // can authorize release. Capture is read-only and must not require
            // releasing the very boundary whose startup root it verifies.
            if (messageType == IpcMessageTypes.Ping
                || messageType == IpcMessageTypes.RequestStartupProfileAttestation)
            {
                return true;
            }

            if (lastRestoreProgress.Phase
                == ReplayRestorePhase.BaselineReady)
            {
                return messageType
                       == IpcMessageTypes.ReleaseColdRestoreBaseline
                       || messageType
                       == IpcMessageTypes.CancelReplaySaveRestore;
            }

            return messageType == IpcMessageTypes.ResumeReplaySaveRestore
                   || messageType == IpcMessageTypes.RequestSnapshot
                   || messageType == IpcMessageTypes.RequestMovie
                   || messageType == IpcMessageTypes.ListReplaySaves
                   || messageType == IpcMessageTypes.Subscribe
                   || messageType == IpcMessageTypes.Unsubscribe;
        }

        private void OnWatchFrame(
            HollowKnightTAS.Core.Inspector.WatchFrame frame)
        {
            if (!subscriptions.Contains("watch"))
            {
                return;
            }

            Publish(
                IpcMessageTypes.WatchFrame,
                new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["json"] =
                        HollowKnightTAS.Core.Inspector.WatchFrameJson
                            .Serialize(frame),
                    ["movieTick"] =
                        frame.MovieTick.ToString(
                            CultureInfo.InvariantCulture),
                    ["sequence"] =
                        frame.Sequence.ToString(
                            CultureInfo.InvariantCulture)
                });
        }

        private void PublishRuntimeStatus(string? requestId = null)
        {
            var fields = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                    ["controlMode"] =
                        controls.ControlMode.ToString(),
                    ["movieTick"] =
                        controls.AuthoritativeMovieTick.ToString(
                            CultureInfo.InvariantCulture),
                    ["playbackMode"] =
                        controls.PlaybackMode.ToString(),
                    ["sceneEpoch"] =
                        journal.CurrentSceneEpoch.ToString(
                            CultureInfo.InvariantCulture),
                    ["automationMode"] = automationMode.ToString(),
                    ["debugMutationEnabled"] =
                        "false",
                    ["recordingActive"] =
                        recordingActive ? "true" : "false",
                    ["runUntilMovieTick"] =
                        runUntilMovieTick?.ToString(
                            CultureInfo.InvariantCulture)
                        ?? string.Empty,
                    ["verificationEligibility"] =
                        RuntimeVerificationEligibility.Status
            };
            AppendPlaybackState(fields);
            AppendReplayRestoreState(fields);
            fields["capturedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            if (requestId != null)
                fields["requestId"] = requestId;
            Publish(IpcMessageTypes.RuntimeStatus, fields);
        }

        private bool IsAllowedDuringVideoExport(string messageType)
        {
            if (videoReplaysLoadedMovie && (messageType == IpcMessageTypes.Step
                || messageType == IpcMessageTypes.RunInputBatch || messageType == IpcMessageTypes.RunUntil
                || messageType == IpcMessageTypes.StartReplay || messageType == IpcMessageTypes.StopReplay))
                return false;
            switch (messageType)
            {
                case IpcMessageTypes.FinishVideoExport:
                case IpcMessageTypes.CancelVideoExport:
                case IpcMessageTypes.Pause:
                case IpcMessageTypes.Resume:
                case IpcMessageTypes.Step:
                case IpcMessageTypes.RunInputBatch:
                case IpcMessageTypes.RunUntil:
                case IpcMessageTypes.StartReplay:
                case IpcMessageTypes.StopReplay:
                case IpcMessageTypes.RequestSnapshot:
                case IpcMessageTypes.RequestMovie:
                case IpcMessageTypes.RequestCapabilityCatalog:
                case IpcMessageTypes.RequestStartupProfileAttestation:
                case IpcMessageTypes.ListReplaySaves:
                case IpcMessageTypes.Subscribe:
                case IpcMessageTypes.Unsubscribe:
                case IpcMessageTypes.Ping:
                    return true;
                default: return false;
            }
        }

        private void StopVideoPlayback()
        {
            if (!videoReplaysLoadedMovie) return;
            videoReplaysLoadedMovie = false;
            if (controls.PlaybackMode != PlaybackMode.Idle) controls.StopReplay();
            if (controls.ControlMode == SimulationControlMode.Stepping) controls.InterruptVideoStep();
            else if (controls.ControlMode == SimulationControlMode.Running) controls.Pause();
        }

        private void OnVideoFrameCompleted()
        {
            if (!videoReplaysLoadedMovie) return;
            // Stop on the last rendered input frame, without a synthetic release frame.
            if (controls.PlaybackMode == PlaybackMode.Stopping || controls.LastPlaybackStopReason.HasValue)
                controls.InterruptVideoStep();
        }

        private void PollVideoExport()
        {
            if (videoCapture == null) return;
            if (videoReplaysLoadedMovie && videoCapture.State == "Capturing")
            {
                if (controls.LastPlaybackStopReason == PlaybackStopReason.Completed)
                {
                    videoReplaysLoadedMovie = false;
                    videoCapture.Finish();
                }
                else if (controls.LastPlaybackStopReason.HasValue)
                    videoCapture.Fail("Replay ended before completion: " + controls.LastPlaybackStopReason);
            }
            if (lastVideoState != videoCapture.State)
            {
                lastVideoState = videoCapture.State;
                PublishRuntimeStatus();
            }
        }

        private void AppendPlaybackState(
            IDictionary<string, string> fields)
        {
            videoCapture?.AppendStatus(fields);
            fields["recordingOriginStatus"] = journal.RecordingOriginStatus;
            fields["nativeReloadPhase"] = sourceLifecycleReload == null ? lastNativeReloadPhase
                : sourceLifecycleReload.Failure.Length != 0 ? "FailedAwaitingPause"
                : sourceLifecycleReload.Loading ? "LoadingSlot" : "ReturningToMenu";
            fields["nativeReloadFailure"] = sourceLifecycleReload?.Failure ?? lastNativeReloadFailure;
            fields["nativeReloadCanExit"] = CanExitFailedSourceLifecycle ? "true" : "false";
            // Journal predicates only: callers must also check menu/control
            // state and pending work before issuing LoadGameSlot.
            fields["recordingOriginCanLoadInitialSlot"] = journal.CanLoadInitialGameSlot ? "true" : "false";
            fields["recordingOriginAtSupportedAnchor"] = journal.CanPrepareRecordingOrigin ? "true" : "false";
            fields["recordingOriginSaveSlot"] = journal.CurrentSaveSlot.ToString(CultureInfo.InvariantCulture);
            fields["recordingOriginDetail"] = journal.RecordingOriginDetail;
            fields["initialRespawnPreparationStatus"] = journal.InitialRespawnPreparationStatus;
            fields["initialRespawnPreparationError"] = journal.InitialRespawnPreparationError;
            fields["initialRespawnInsertedWaitFrames"] = journal.InitialRespawnInsertedWaitFrames.ToString(CultureInfo.InvariantCulture);
            fields["initialRespawnPreparationStartFrame"] = journal.InitialRespawnPreparationStartFrame.ToString(CultureInfo.InvariantCulture);
            fields["initialRespawnNativeStartFrame"] = journal.InitialRespawnNativeStartFrame.ToString(CultureInfo.InvariantCulture);
            fields["isStableTitleMenu"] = RuntimePauseController.IsStableTitleMenu() ? "true" : "false";
            fields["movieTickSource"] = controls.MovieTickSource;
            fields["lastReplayMovieTick"] =
                controls.LastReplayMovieTick.ToString(
                    CultureInfo.InvariantCulture);
            fields["lastPlaybackStopReason"] =
                controls.LastPlaybackStopReason?.ToString()
                ?? string.Empty;
            fields["lastPlaybackFault"] =
                controls.LastPlaybackFault;
            fields["controlFault"] = controls.ControlFault;
            fields["lastControlAbortReason"] =
                controls.LastControlAbortReason;
            fields["lastStepInterruptionReason"] = controls.LastStepInterruptionReason;
            fields["disconnectCleanupPending"] = controls.DisconnectCleanupPending ? "true" : "false";
            fields["lastInterruptedStepRequestedTicks"] = controls.LastInterruptedStepRequestedTicks.ToString(CultureInfo.InvariantCulture);
            fields["lastInterruptedStepCommittedTicks"] = controls.LastInterruptedStepCommittedTicks.ToString(CultureInfo.InvariantCulture);
            fields["lastBindingRestoreEquivalent"] =
                OptionalBoolean(
                    controls.LastBindingRestoreEquivalent);
            fields["replayObservationCount"] =
                controls.ReplayObservationCount.ToString(
                    CultureInfo.InvariantCulture);
            fields["replaySuspendedRawInputTickCount"] =
                controls.ReplaySuspendedRawInputTickCount.ToString(
                    CultureInfo.InvariantCulture);
            fields["pauseLeaseReassertionCount"] =
                controls.PauseLeaseReassertionCount.ToString(
                    CultureInfo.InvariantCulture);
            fields["pausedBoundaryWaitCount"] =
                controls.PausedBoundaryWaitCount.ToString(
                    CultureInfo.InvariantCulture);
            fields["pausedBoundaryPumpCount"] =
                controls.PausedBoundaryPumpCount.ToString(
                    CultureInfo.InvariantCulture);
            fields["sceneTransitionPassThroughCount"] =
                controls.CompletedFrameSceneTransitionPassThroughCount
                    .ToString(CultureInfo.InvariantCulture);
            fields["sceneTransitionPassThroughActive"] =
                controls.CompletedFrameSceneTransitionPassThroughActive
                    ? "true"
                    : "false";
            fields["controlGateStrategyId"] =
                controls.ControlGateStrategyId;
            fields["usesCompletedFrameBoundaryGate"] =
                controls.UsesCompletedFrameBoundaryGate
                    ? "true"
                    : "false";
            fields["deferredRecordingArmEnabled"] =
                controls.DeferredRecordingArmEnabled
                    ? "true"
                    : "false";
            fields["deferredRecordingArmRunId"] =
                controls.DeferredRecordingArmRunId;
            fields["deferredRecordingArmReleaseConsumed"] =
                controls.DeferredRecordingArmReleaseConsumed
                    ? "true"
                    : "false";
            fields["deferredRecordingArmPauseArmed"] =
                controls.DeferredPauseArmed
                    ? "true"
                    : "false";
            fields["deferredRecordingArmReplayArmed"] =
                controls.DeferredReplayArmed
                    ? "true"
                    : "false";
            fields["deferredRecordingArmNeutralPreRollCompletedFrameCount"] =
                controls.DeferredNeutralPreRollCompletedFrameCount.ToString(
                    CultureInfo.InvariantCulture);
            fields["deferredRecordingArmActivationAttempted"] =
                controls.DeferredActivationAttempted
                    ? "true"
                    : "false";
            fields["deferredRecordingArmActivationSucceeded"] =
                controls.DeferredActivationSucceeded
                    ? "true"
                    : "false";
            fields["deferredRecordingArmActivationCount"] =
                controls.DeferredActivationCount.ToString(
                    CultureInfo.InvariantCulture);
            fields["deferredRecordingArmActivationError"] =
                controls.DeferredActivationError;
            fields["frozenHeroActionUpdateCount"] =
                controls.FrozenHeroActionUpdateCount.ToString(
                    CultureInfo.InvariantCulture);
            fields["advancedHeroActionUpdateCount"] =
                controls.AdvancedHeroActionUpdateCount.ToString(
                    CultureInfo.InvariantCulture);
            fields["heroActionPhaseRejectionCount"] =
                controls.HeroActionPhaseRejectionCount.ToString(
                    CultureInfo.InvariantCulture);
            fields["heroActionUpdateObserved"] =
                controls.HeroActionUpdateObserved
                    ? "true"
                    : "false";
            fields["lastHeroActionUpdateAdvanced"] =
                controls.LastHeroActionUpdateAdvanced
                    ? "true"
                    : "false";
            fields["lastHeroActionUpdateTick"] =
                controls.LastHeroActionUpdateTick.ToString(
                    CultureInfo.InvariantCulture);
            fields["ignoredFocusLossCount"] =
                controls.IgnoredFocusLossCount.ToString(
                    CultureInfo.InvariantCulture);
            fields["replayMismatchCount"] =
                controls.ReplayMismatchCount.ToString(
                    CultureInfo.InvariantCulture);
            fields["firstReplayMismatchMovieTick"] =
                controls.FirstReplayMismatchMovieTick.ToString(
                    CultureInfo.InvariantCulture);
            fields["firstReplayMismatchExpected"] =
                controls.FirstReplayMismatchExpected;
            fields["firstReplayMismatchActual"] =
                controls.FirstReplayMismatchActual;
            fields["lastReplayMismatchMovieTick"] =
                controls.LastReplayMismatchMovieTick.ToString(
                    CultureInfo.InvariantCulture);
            fields["lastReplayMismatchExpected"] =
                controls.LastReplayMismatchExpected;
            fields["lastReplayMismatchActual"] =
                controls.LastReplayMismatchActual;
            fields["replayPhysicalNoiseDetected"] =
                controls.ReplayPhysicalNoiseDetected
                    ? "true"
                    : "false";
            fields["journalPlaybackCaptureActive"] =
                controls.JournalPlaybackCaptureActive
                    ? "true"
                    : "false";
            fields["journalPlaybackCaptureError"] =
                controls.JournalPlaybackCaptureError;
            fields["replayDeterministicRngEnabled"] =
                controls.ReplayDeterministicRngEnabled
                    ? "true"
                    : "false";
            fields["replayDeterministicRngRequested"] =
                controls.ReplayDeterministicRngRequested
                    ? "true"
                    : "false";
            fields["replayDeterministicRngSeed"] =
                controls.ReplayDeterministicRngSeed.ToString(
                    CultureInfo.InvariantCulture);
            fields["replayDeterministicRngProfile"] =
                RuntimeControlService.DeterministicRngProfile;
            fields["replayDeterministicRngStatus"] =
                controls.ReplayDeterministicRngStatus;
            fields["replayDeterministicRngResetCount"] =
                controls.ReplayDeterministicRngResetCount.ToString(
                    CultureInfo.InvariantCulture);
            fields["lastReplayRngBeforeSha256"] =
                controls.LastReplayRngBeforeSha256;
            fields["lastReplayRngStateSha256"] =
                controls.LastReplayRngStateSha256;
            fields["lastReplayRngAppliedSeed"] =
                controls.LastReplayRngAppliedSeed.ToString(
                    CultureInfo.InvariantCulture);
            fields["lastReplayRngBoundary"] =
                controls.LastReplayRngBoundary;
            fields["lastReplayRngScene"] =
                controls.LastReplayRngScene;
        }

        private void Publish(
            string messageType,
            IReadOnlyDictionary<string, string> fields)
        {
            if (string.Equals(
                    messageType,
                    IpcMessageTypes.TickLedger,
                    StringComparison.Ordinal)
                && !subscriptions.Contains("ledger"))
            {
                return;
            }

            server.TryPublish(messageType, fields);
        }

        private void AppendReplayRestoreState(
            IDictionary<string, string> fields)
        {
            var progress = lastRestoreProgress;
            var saveOperation = replaySaves?.LastOperation;
            if (replaySaves != null)
            {
                var policy = replaySaves.AutoSavePolicy;
                fields["autoSaveEnabled"] = policy.Enabled ? "true" : "false";
                fields["autoSaveIntervalMovieTicks"] = policy.IntervalMovieTicks.ToString(CultureInfo.InvariantCulture);
                fields["autoSaveRetentionCount"] = policy.RetentionCount.ToString(CultureInfo.InvariantCulture);
            }
            fields["replaySavePendingCount"] =
                (replaySaves?.PendingCount ?? 0).ToString(CultureInfo.InvariantCulture);
            fields["replaySaveLastRequestId"] = saveOperation?.RequestId ?? string.Empty;
            fields["replaySaveLastStatus"] = saveOperation?.Status.ToString() ?? string.Empty;
            fields["replaySaveLastId"] = saveOperation?.ReplaySaveId ?? string.Empty;
            fields["replaySaveLastEffectiveMovieTick"] =
                saveOperation?.EffectiveMovieTick.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            fields["replaySaveLastError"] = saveOperation?.Error ?? string.Empty;
            fields["replaySaveRestoreActive"] =
                restoreHandle.HasValue ? "true" : "false";
            fields["replaySaveCapturePumpActive"] =
                replaySaves?.RuntimePumpActive == true ? "true" : "false";
            fields["replaySaveRestorePumpActive"] =
                replaySaves?.RestoreRuntimePumpActive == true
                    ? "true"
                    : "false";
            fields["replaySaveRestorePhase"] =
                progress?.Phase.ToString() ?? "Idle";
            fields["replaySaveRestoreStatus"] =
                progress?.Status.ToString() ?? string.Empty;
            fields["replaySaveRestoreCurrentMovieTick"] =
                progress?.CurrentMovieTick.ToString(
                    CultureInfo.InvariantCulture)
                ?? string.Empty;
            fields["replaySaveRestoreTargetMovieTick"] =
                progress?.TargetMovieTick.ToString(
                    CultureInfo.InvariantCulture)
                ?? string.Empty;
            fields["replaySaveRestoreNextMovieTick"] =
                progress?.NextMovieTick.ToString(
                    CultureInfo.InvariantCulture)
                ?? string.Empty;
            fields["replaySaveRestoreFraction"] =
                progress?.Fraction.ToString(
                    "R",
                    CultureInfo.InvariantCulture)
                ?? string.Empty;
            fields["replaySaveRestoreRequiresOverwriteApproval"] =
                progress == null
                    ? "false"
                    : progress.RequiresOverwriteApproval
                        ? "true"
                        : "false";
            fields["replaySaveRestoreDetail"] =
                progress?.Detail ?? string.Empty;
            if (replaySaves != null && replaySaves.HasPendingSourceSlotApproval)
            {
                fields["replaySaveRestorePhase"] = "AwaitingOverwriteApproval";
                fields["replaySaveRestoreRequiresOverwriteApproval"] = "true";
                fields["replaySaveRestoreDetail"] = replaySaves.SourceSlotApprovalDetail;
            }
            fields["replaySaveRestoreExpectedSemanticSha256"] =
                progress?.ExpectedSemanticSha256 ?? string.Empty;
            fields["replaySaveRestoreActualSemanticSha256"] =
                progress?.ActualSemanticSha256 ?? string.Empty;
            fields["replaySaveRestoreBindingEquivalent"] =
                OptionalBoolean(progress?.BindingRestoreEquivalent);
            fields["replaySaveRestoreSettingsEquivalent"] =
                OptionalBoolean(progress?.SettingsRestoreEquivalent);
            fields["replaySaveRestoreSemanticProjectionId"] =
                progress?.SemanticProjectionId ?? string.Empty;
            fields["replaySaveRestoreExpectedVerificationSha256"] =
                progress?.ExpectedVerificationSha256 ?? string.Empty;
            fields["replaySaveRestoreActualVerificationSha256"] =
                progress?.ActualVerificationSha256 ?? string.Empty;
            fields["replaySaveRestoreStrictSemanticEquivalent"] =
                OptionalBoolean(progress?.StrictSemanticEquivalent);
        }

        private static string OptionalBoolean(bool? value)
        {
            return !value.HasValue
                ? string.Empty
                : value.Value
                    ? "true"
                    : "false";
        }

        private RuntimeReplaySaveManager RequireReplaySaves()
        {
            return replaySaves
                   ?? throw new InvalidOperationException(
                       "T09 replay-save service is disabled.");
        }

        private ReplayRestoreHandle RequireRestoreHandle()
        {
            return restoreHandle
                   ?? throw new InvalidOperationException(
                       "No replay-save restore is active.");
        }

        private static string RequireRestoreSuccess(
            ReplayRestoreResult result)
        {
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    result.Error);
            }

            return result.Progress.Phase.ToString();
        }

        private static string RequireSuccess(
            HollowKnightTAS.Core.Control.ControlResult result)
        {
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error);
            }

            return result.Mode.ToString();
        }

        private static void RequireStream(string value)
        {
            if (!string.Equals(
                    value,
                    "watch",
                    StringComparison.Ordinal)
                && !string.Equals(
                    value,
                    "ledger",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Stream must be watch or ledger.");
            }
        }

        private static void RequireFields(
            IReadOnlyDictionary<string, string> fields,
            params string[] required)
        {
            if (fields.Count != required.Length
                || required.Any(
                    field => !fields.ContainsKey(field)))
            {
                throw new InvalidDataException(
                    "Command payload has unexpected fields.");
            }
        }

        private static string SanitizeLine(string value)
        {
            return value.Replace('\r', ' ')
                .Replace('\n', ' ');
        }

        private static string GetCommandErrorCode(Exception exception)
        {
            if (exception is SourceSlotApprovalRequiredException)
                return "ColdRestoreSlotApprovalRequired";
            if (exception is RuntimeCommandRejectionException rejection)
            {
                return rejection.ErrorCode;
            }

            if (exception is ArgumentException
                || exception is InvalidDataException
                || exception is FormatException
                || exception is OverflowException)
            {
                return "InvalidArguments";
            }

            return exception is InvalidOperationException
                ? "RuntimeRejected"
                : "RuntimeFault";
        }

        private sealed class RuntimeCommandRejectionException : Exception
        {
            public RuntimeCommandRejectionException(
                string errorCode,
                string message)
                : base(message)
            {
                if (!IpcIdentifier.IsValid(errorCode, 64))
                {
                    throw new ArgumentException(
                        "Runtime command error code is invalid.",
                        nameof(errorCode));
                }

                ErrorCode = errorCode;
            }

            public string ErrorCode { get; }
        }

        private enum PendingMovieSeekPhase
        {
            Restoring = 1,
            HandoffPausing = 2,
            ReplayingTail = 3
        }

        private sealed class PendingMovieSeek
        {
            public PendingMovieSeek(
                string requestId,
                MovieDocument targetMovie,
                string targetMovieId,
                long targetMovieTick,
                string replaySaveId,
                long checkpointMovieTick)
            {
                RequestId = requestId;
                TargetMovie = targetMovie;
                TargetMovieId = targetMovieId;
                TargetMovieTick = targetMovieTick;
                ReplaySaveId = replaySaveId;
                CheckpointMovieTick = checkpointMovieTick;
                Phase = PendingMovieSeekPhase.Restoring;
            }

            public string RequestId { get; }
            public MovieDocument TargetMovie { get; }
            public string TargetMovieId { get; }
            public long TargetMovieTick { get; }
            public string ReplaySaveId { get; }
            public long CheckpointMovieTick { get; }
            public PendingMovieSeekPhase Phase { get; set; }
        }

        private sealed class MovieUpload
        {
            private readonly MemoryStream stream;

            public MovieUpload(
                string requestId,
                string movieId,
                int totalBytes,
                int chunkCount, bool isLifecyclePlan = false)
            {
                RequestId = requestId;
                MovieId = movieId;
                TotalBytes = totalBytes;
                ChunkCount = chunkCount;
                IsLifecyclePlan = isLifecyclePlan;
                stream = new MemoryStream(totalBytes);
            }

            public string RequestId { get; }
            public string MovieId { get; }
            public int TotalBytes { get; }
            public int ChunkCount { get; }
            public bool IsLifecyclePlan { get; }
            public int NextIndex { get; private set; }

            public void Append(byte[] value)
            {
                if (NextIndex >= ChunkCount
                    || value.Length == 0
                    || stream.Length
                    > TotalBytes - value.Length)
                {
                    throw new InvalidDataException(
                        "Movie chunk exceeds declared bounds.");
                }

                stream.Write(value, 0, value.Length);
                NextIndex++;
            }

            public byte[] Complete()
            {
                if (NextIndex != ChunkCount
                    || stream.Length != TotalBytes)
                {
                    throw new InvalidDataException(
                        "Movie upload is incomplete.");
                }

                return stream.ToArray();
            }
        }
    }

    [DefaultExecutionOrder(31000)]
    internal sealed class RuntimeCommandDispatcherRunner :
        MonoBehaviour
    {
        private RuntimeCommandDispatcher? owner;

        public void Initialize(RuntimeCommandDispatcher value)
        {
            owner = value;
        }

        private void LateUpdate()
        {
            owner?.OnLateUpdate();
        }
    }
}
