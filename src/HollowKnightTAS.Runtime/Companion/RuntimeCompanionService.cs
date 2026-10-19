using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Core.Ipc;
using HollowKnightTAS.Core.Automation;
using HollowKnightTAS.Runtime.Inspector;
using HollowKnightTAS.Runtime.Ipc;
using HollowKnightTAS.Runtime.FullRun;
using HollowKnightTAS.Runtime.Keyframes;
using HollowKnightTAS.Runtime.Playback;
using HollowKnightTAS.Runtime.ReplaySave;
using HollowKnightTAS.Runtime.Settings;
using UnityEngine;

namespace HollowKnightTAS.Runtime.Companion
{
    public sealed class RuntimeCompanionService : IDisposable
    {
        private const int OverlayStableGameplayUpdateCount = 30;
        private readonly TasGlobalSettings settings;
        private readonly RuntimeCommandQueue commands;
        private readonly NamedPipeRuntimeServer server;
        private readonly RuntimeCommandDispatcher dispatcher;
        private readonly CompanionLauncher launcher;
        private readonly RuntimeStartupProfileAttestor startupAttestor;
        private readonly Action<
            string,
            IReadOnlyDictionary<string, string>> emit;
        private RuntimeCompanionServiceRunner? runner;
        private bool gameManagerUpdateHookRegistered;
        private int stableGameplayUpdates;
        private string stableGameplayScene = string.Empty;
        private int started;
        private int disposed;

        public RuntimeCompanionService(
            TasGlobalSettings settings,
            string modRoot,
            string sessionId,
            string manifestSha256,
            string gameVersion,
            string apiVersion,
            RuntimeReplayJournal replayJournal,
            RuntimeReplaySaveManager? replaySaveManager,
            RuntimeInspector? inspector,
            KeyframeTierResolution keyframeResolution,
            Action<string> logInfo,
            Action<string> logWarning,
            Action<string> logError,
            Action<
                string,
                IReadOnlyDictionary<string, string>> emit,
            RuntimeFullRunSession? fullRunSession = null)
        {
            this.settings =
                (settings
                 ?? throw new ArgumentNullException(
                     nameof(settings)))
                .CloneNormalized();
            this.emit = emit
                        ?? throw new ArgumentNullException(
                            nameof(emit));
            if (!this.settings.CompanionEnabled)
            {
                throw new InvalidOperationException(
                    "Companion service is disabled.");
            }

            var token = new byte[32];
            var pipeRandom = new byte[16];
            using (var random =
                   RandomNumberGenerator.Create())
            {
                random.GetBytes(token);
                random.GetBytes(pipeRandom);
            }

            var pipeName =
                "HollowKnightTAS.Runtime."
                + sessionId
                + "."
                + ToLowerHex(pipeRandom);
            var registration =
                new CompanionSessionRegistration(
                    sessionId,
                    Process.GetCurrentProcess().Id,
                    Process.GetCurrentProcess()
                        .StartTime
                        .ToUniversalTime()
                        .Ticks,
                    manifestSha256,
                    Sha256Utility.ComputeFileHex(
                        typeof(HollowKnightTASMod)
                            .Assembly.Location),
                    Sha256Utility.ComputeFileHex(
                        typeof(CompanionSessionRegistration)
                            .Assembly.Location),
                    pipeName,
                    CompanionProtocolMetadata
                        .SupportedProtocols,
                    this.settings
                        .EnableNativeCapabilities,
                    token,
                    AutomationMode.ApprovedControl,
                    false);
            commands = new RuntimeCommandQueue(
                this.settings
                    .CompanionCommandQueueCapacity);
            server = new NamedPipeRuntimeServer(
                sessionId,
                registration.GameProcessId,
                pipeName,
                token,
                commands,
                this.settings
                    .CompanionOutboundQueueCapacity,
                logInfo,
                logWarning,
                logError);
            startupAttestor = new RuntimeStartupProfileAttestor();
            replaySaveManager?.ConfigureStartupProfileAttestor(
                startupAttestor);
            dispatcher = new RuntimeCommandDispatcher(
                commands,
                server,
                replayJournal,
                replaySaveManager,
                inspector,
                startupAttestor,
                sessionId,
                manifestSha256,
                this.settings.EnableNativeCapabilities,
                AutomationMode.ApprovedControl,
                this.settings.VerificationModeRequested,
                this.settings.ReplayDeterministicRngEnabled,
                this.settings.ReplayDeterministicRngSeed,
                gameVersion,
                apiVersion,
                keyframeResolution
                ?? throw new ArgumentNullException(
                    nameof(keyframeResolution)),
                this.settings
                    .CompanionMainThreadBudgetMilliseconds,
                emit,
                fullRunSession);
            launcher = new CompanionLauncher(
                modRoot,
                registration,
                server,
                TimeSpan.FromMilliseconds(
                    this.settings
                        .CompanionHandshakeTimeoutMilliseconds),
                logInfo,
                logWarning,
                emit);
            dispatcher.ConfigureCompanionShutdownForExit(() =>
            {
                using (var timeout = new CancellationTokenSource(
                           TimeSpan.FromSeconds(1)))
                {
                    return launcher.RequestSessionShutdownAsync(
                            this.settings.ExitCompanionWithGame,
                            timeout.Token)
                        .GetAwaiter().GetResult();
                }
            });
        }

        public CompanionLaunchSnapshot Status =>
            launcher.Snapshot;

        public void Start()
        {
            ThrowIfDisposed();
            if (Interlocked.Exchange(ref started, 1) != 0)
            {
                return;
            }

            server.Start();
            On.GameManager.Update += OnGameManagerUpdate;
            gameManagerUpdateHookRegistered = true;
            // The canonical execution profile shares this service entry for
            // recording, full replay and restore. Its TAS-owned hotkey pump
            // must not depend on spending thirty updates in a gameplay scene.
            // The existing hook yields hotkey handling to this same runner.
            if (settings.CompanionOverlayEnabled && NativeExecutionProfile.UsesCanonicalBoundary(MovieProtocolV2.SelectedNativeProfileId))
                EnsureRuntimePump();
            emit(
                "companion-service-started",
                new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["autoStart"] =
                        settings.AutoStartCompanion
                            ? "true"
                            : "false",
                    ["nativeCapabilitiesRequested"] =
                        settings.EnableNativeCapabilities
                            ? "true"
                            : "false",
                    ["automationMode"] =
                        nameof(AutomationMode.ApprovedControl),
                    ["debugMutationEnabled"] =
                        "false",
                    ["commandQueueCapacity"] =
                        settings
                            .CompanionCommandQueueCapacity
                            .ToString(
                                CultureInfo.InvariantCulture),
                    ["mainThreadBudgetMilliseconds"] =
                        settings
                            .CompanionMainThreadBudgetMilliseconds
                            .ToString(
                                "R",
                                CultureInfo.InvariantCulture),
                    ["outboundQueueCapacity"] =
                        settings
                            .CompanionOutboundQueueCapacity
                            .ToString(
                                CultureInfo.InvariantCulture),
                    ["pipeCreated"] = "true"
                });
            if (settings.AutoStartCompanion)
            {
                launcher.StartAuto();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            if (gameManagerUpdateHookRegistered)
            {
                On.GameManager.Update -= OnGameManagerUpdate;
                gameManagerUpdateHookRegistered = false;
            }

            if (runner != null)
            {
                UnityEngine.Object.Destroy(
                    runner.gameObject);
                runner = null;
            }

            server.BeginShutdown();
            dispatcher.Dispose();
            using (var timeout =
                   new CancellationTokenSource(
                       TimeSpan.FromSeconds(1)))
            {
                try
                {
                    launcher.RequestSessionShutdownAsync(
                            settings.ExitCompanionWithGame,
                            timeout.Token)
                        .Wait(TimeSpan.FromMilliseconds(1100));
                }
                catch (AggregateException)
                {
                }
            }

            launcher.Dispose();
            server.Dispose();
            emit(
                "companion-service-stopped",
                new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["commandRejectedCount"] =
                        commands.RejectedCount.ToString(
                            CultureInfo.InvariantCulture),
                    ["exitCompanionRequested"] =
                        settings.ExitCompanionWithGame
                            ? "true"
                            : "false",
                    ["outboundRejectedCount"] =
                        server.OutboundRejectedCount
                            .ToString(
                                CultureInfo.InvariantCulture)
                });
        }

        private void OnGameManagerUpdate(
            On.GameManager.orig_Update original,
            GameManager self)
        {
            original(self);
            dispatcher.ObserveStartupGameplay(self != null && self.gameState == GlobalEnums.GameState.PLAYING);
            var scene = UnityEngine.SceneManagement.SceneManager
                .GetActiveScene().name ?? string.Empty;
            startupAttestor.TrySignalPayloadReady(scene);
            if (Volatile.Read(ref disposed) != 0 || runner != null)
            {
                return;
            }

            HandleHotkey(launcher);
            if (!settings.CompanionOverlayEnabled)
            {
                return;
            }

            if (self == null
                || self.gameState != GlobalEnums.GameState.PLAYING
                || self.IsInSceneTransition
                || HeroController.SilentInstance == null)
            {
                stableGameplayUpdates = 0;
                stableGameplayScene = scene;
                return;
            }

            if (!string.Equals(
                    stableGameplayScene,
                    scene,
                    StringComparison.Ordinal))
            {
                stableGameplayScene = scene;
                stableGameplayUpdates = 0;
            }

            stableGameplayUpdates++;
            if (stableGameplayUpdates >= OverlayStableGameplayUpdateCount)
            {
                EnsureRuntimePump();
            }
        }

        private void EnsureRuntimePump()
        {
            if (Volatile.Read(ref disposed) != 0 || runner != null)
            {
                return;
            }

            var gameObject = new GameObject(
                "HollowKnightTAS.RuntimeCompanionService");
            UnityEngine.Object.DontDestroyOnLoad(gameObject);
            runner =
                gameObject.AddComponent<RuntimeCompanionServiceRunner>();
            runner.Initialize(launcher);
        }

        internal static void HandleHotkey(CompanionLauncher active)
        {
            if (active == null
                || !UnityEngine.Input.GetKeyDown(KeyCode.F10))
            {
                return;
            }

            if (UnityEngine.Input.GetKey(KeyCode.LeftShift)
                || UnityEngine.Input.GetKey(KeyCode.RightShift))
            {
                active.DisableForSession();
            }
            else
            {
                active.ManualStartOrReconnect();
            }
        }

        private static string ToLowerHex(byte[] bytes)
        {
            var characters =
                new char[bytes.Length * 2];
            const string alphabet =
                "0123456789abcdef";
            for (var index = 0;
                 index < bytes.Length;
                 index++)
            {
                characters[index * 2] =
                    alphabet[bytes[index] >> 4];
                characters[index * 2 + 1] =
                    alphabet[bytes[index] & 15];
            }

            return new string(characters);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(RuntimeCompanionService));
            }
        }
    }

    internal sealed class RuntimeCompanionServiceRunner :
        MonoBehaviour
    {
        private CompanionLauncher? launcher;
        public void Initialize(CompanionLauncher value)
        {
            launcher = value;
        }

        private void Update()
        {
            var active = launcher;
            if (active != null)
            {
                RuntimeCompanionService.HandleHotkey(active);
            }
        }

    }
}
