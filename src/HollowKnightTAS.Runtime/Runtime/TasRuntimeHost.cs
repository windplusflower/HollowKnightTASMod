using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using HollowKnightTAS.Core.Diagnostics;
using HollowKnightTAS.Core.Keyframes;
using HollowKnightTAS.Core.Manifest;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Core.ReplaySave;
using HollowKnightTAS.Runtime.Automation;
using HollowKnightTAS.Runtime.Companion;
using HollowKnightTAS.Runtime.FullRun;
using HollowKnightTAS.Runtime.Control;
using HollowKnightTAS.Runtime.Input;
using HollowKnightTAS.Runtime.Inspector;
using HollowKnightTAS.Runtime.Keyframes;
using HollowKnightTAS.Runtime.Manifest;
using HollowKnightTAS.Runtime.Playback;
using HollowKnightTAS.Runtime.ReplaySave;
using HollowKnightTAS.Runtime.Rng;
using HollowKnightTAS.Runtime.Settings;
using HollowKnightTAS.Runtime.State;
using HollowKnightTAS.Runtime.Timing;
using Modding;
using UnityEngine;

namespace HollowKnightTAS.Runtime.Runtime
{
    public sealed class TasRuntimeHost : IDisposable
    {
        private readonly TasGlobalSettings settings;
        private readonly TasGlobalSettings persistentSettings;
        private readonly Action<string> logInfo;
        private readonly Action<string> logDebug;
        private readonly Action<string> logWarning;
        private readonly Action<string> logError;
        private readonly RuntimeFullRunSession? fullRunSession;
        private JsonLinesEventSink? eventSink;
        private RuntimeAutomationReadyProbe? automationReadyProbe;
        private RuntimeFinalTasReadyProbe? finalTasReadyProbe;
        private InputPhaseProbe? inputPhaseProbe;
        private RuntimeClockProbe? runtimeClockProbe;
        private RuntimeSnapshotProbeExperiment? snapshotProbeExperiment;
        private RuntimeReplayJournal? replayJournal;
        private RuntimePlaybackProbeExperiment? playbackProbeExperiment;
        private RuntimeStepProbeExperiment? stepProbeExperiment;
        private RuntimeReplaySaveManager? replaySaveManager;
        private ReplaySaveMenuController? replaySaveMenu;
        private RuntimeReplaySaveProbeExperiment? replaySaveProbeExperiment;
        private RuntimeRngProbe? rngProbe;
        private RuntimeRandomTrace? randomTrace;
        private RuntimeRenderRandomIsolation? renderRandomIsolation;
        private RuntimeRngProbeExperiment? rngProbeExperiment;
        private RuntimeInspector? runtimeInspector;
        private RuntimeInspectorProbeExperiment?
            inspectorProbeExperiment;
        private RuntimeCompanionService? companionService;
        private string? sessionId;
        private string? sessionDirectory;
        private long sequence;
        private int started;
        private int stopped;

        public TasRuntimeHost(
            TasGlobalSettings settings,
            Action<string> logInfo,
            Action<string> logDebug,
            Action<string> logWarning,
            Action<string> logError,
            RuntimeFullRunSession? fullRunSession = null)
        {
            persistentSettings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.settings = persistentSettings.CloneNormalized();
            this.logInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
            this.logDebug = logDebug ?? throw new ArgumentNullException(nameof(logDebug));
            this.logWarning = logWarning ?? throw new ArgumentNullException(nameof(logWarning));
            this.logError = logError ?? throw new ArgumentNullException(nameof(logError));
            this.fullRunSession = fullRunSession;
        }

        public string? SessionId => sessionId;
        public string? SessionDirectory => sessionDirectory;
        public VerificationPreflightResult? Verification { get; private set; }

        public void Start()
        {
            if (Interlocked.CompareExchange(ref started, 1, 0) != 0)
            {
                logDebug("T01 runtime host start ignored because the session is already active.");
                return;
            }

            sessionId = CreateSessionId();
            sessionDirectory = SavePathResolver.Current.GetTasDataPath(
                "sessions", sessionId);
            Directory.CreateDirectory(sessionDirectory);

            var flushTimeout = TimeSpan.FromMilliseconds(settings.ExitFlushTimeoutMilliseconds);
            eventSink = new JsonLinesEventSink(
                Path.Combine(sessionDirectory, "events.jsonl"),
                settings.EventQueueCapacity,
                flushTimeout,
                budgetDirectory: Path.GetDirectoryName(sessionDirectory));

            logInfo("session-start sessionId=" + sessionId);
            Emit(
                "session-start",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["runtimeVersion"] = HollowKnightTASMod.Version,
                    ["verificationRequested"] = settings.VerificationModeRequested ? "true" : "false"
                });

            var loadedMods = RuntimeEnvironmentReader.CaptureLoadedMods();
            Verification = VerificationPreflight.Evaluate(
                settings.VerificationModeRequested);
            RuntimeRngCapabilityResolution? rngCapability = null;
            var rngCodecId = "unsupported";
            var rngCoverage = "resolution-fault";
            try
            {
                rngCapability = RuntimeRngCapabilityResolver.Resolve();
                rngCodecId = rngCapability.CodecId;
                rngCoverage = rngCapability.CoverageId;
            }
            catch (Exception exception)
            {
                logError(
                    "T10 RNG capability resolution faulted and was disabled: "
                    + exception.Message);
            }

            var manifest = RuntimeEnvironmentReader.Read(
                settings,
                Verification,
                loadedMods,
                rngCodecId,
                rngCoverage);
            var manifestBytes = ManifestCanonicalizer.Serialize(manifest);
            var manifestHash = ManifestCanonicalizer.ComputeSha256(manifest);

            WriteAtomic(Path.Combine(sessionDirectory, "manifest.json"), manifestBytes);
            WriteAtomic(
                Path.Combine(sessionDirectory, "manifest.sha256"),
                new UTF8Encoding(false).GetBytes(manifestHash + "\n"));

            Emit(
                "manifest-written",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["manifestSha256"] = manifestHash,
                    ["schemaVersion"] = manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture)
                });

            if (fullRunSession != null)
            {
                if (!settings.CompanionEnabled)
                    throw new InvalidOperationException("Full-run Studio IPC is disabled.");
                if (fullRunSession.Mode == "Recording")
                    fullRunSession.SetRecordingHeader(new MovieV2Header(
                        manifest.GameVersion, manifest.ModdingApiVersion,
                        HollowKnightTASMod.Version, MovieProtocolV2.SelectedNativeProfileId,
                        MovieProtocolV2.ActionSchemaId, fullRunSession.MouseEnabled,
                        manifestHash, Screen.width, Screen.height));
                replayJournal = new RuntimeReplayJournal(sessionDirectory,
                    manifestHash, logDebug, logWarning, logError,
                    new DesktopSaveSlotBaselineProvider(), ReplaySaveSnapshotCapture.Create());
                var inactiveKeyframes = KeyframeEligibilityProbe.Resolve(false,
                    rngCoverage, Verification.UnexpectedMods);
                var modRoot = Path.GetDirectoryName(typeof(HollowKnightTASMod).Assembly.Location)
                    ?? throw new InvalidOperationException("Runtime assembly directory is unavailable.");
                companionService = new RuntimeCompanionService(settings, modRoot, sessionId,
                    manifestHash, manifest.GameVersion, manifest.ModdingApiVersion,
                    replayJournal, null, null, inactiveKeyframes,
                    logInfo, logWarning, logError, Emit, fullRunSession);
                companionService.Start();
                Emit("full-run-runtime-ready", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["mode"] = fullRunSession.Mode,
                    ["nativeFrame"] = fullRunSession.GetStatus().NativeFrame
                        .ToString(CultureInfo.InvariantCulture)
                });
                return;
            }

            var keyframeResolution =
                KeyframeEligibilityProbe.Resolve(
                    settings.EnableSemanticKeyframes,
                    rngCoverage,
                    Verification.UnexpectedMods);
            IReplayRestoreAccelerator? restoreAccelerator = null;
            if (keyframeResolution.Requested)
            {
                restoreAccelerator =
                    new ReplayOnlyRestoreAccelerator(
                        keyframeResolution.ReasonCodes);
            }
            var replayTimingReady = true;
            if (settings.ReplaySaveEnabled
                && settings.ReplaySaveDeterministicTimingEnabled)
            {
                // Legacy settings may still request the old profile. Do not
                // apply it: captureDeltaTime, targetFrameRate and vSync are
                // gameplay environment inputs, and changing only the TAS run
                // violates T24 vanilla equivalence.
                logWarning(
                    "ReplaySaveDeterministicTimingEnabled was ignored by "
                    + "the T24 vanilla-equivalence gate; effective profile=none.");
            }

            replayJournal = new RuntimeReplayJournal(
                sessionDirectory,
                manifestHash,
                logDebug,
                logWarning,
                logError,
                new DesktopSaveSlotBaselineProvider(),
                ReplaySaveSnapshotCapture.Create());
            replayJournal.Start();
            renderRandomIsolation = new RuntimeRenderRandomIsolation(logInfo);
            try { randomTrace = RuntimeRandomTrace.TryStart(sessionId, replayJournal); }
            catch (Exception exception) { logWarning("Optional RNG trace unavailable: " + exception.Message); }
            Emit(
                "shadow-journal-started",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["outputDirectoryName"] = "journal"
                });
            if (rngCapability != null)
            {
                try
                {
                    rngProbe = new RuntimeRngProbe(
                        sessionDirectory,
                        sessionId,
                        replayJournal,
                        rngCapability,
                        logInfo,
                        logWarning,
                        logError);
                    Emit(
                        "rng-capability-ready",
                        new Dictionary<string, string>(
                            StringComparer.Ordinal)
                        {
                            ["codecId"] = rngProbe.CodecId,
                            ["coverage"] = rngProbe.CoverageId,
                            ["hooksAttached"] =
                                rngProbe.HooksAttached ? "true" : "false",
                            ["diagnosticsEnabled"] = "false",
                            ["outputDirectoryName"] = "rng"
                        });
                }
                catch (Exception exception)
                {
                    rngProbe?.Dispose();
                    rngProbe = null;
                    logError(
                        "T10 RNG diagnostics could not start: "
                        + exception.Message);
                }
            }
            try
            {
                runtimeInspector = new RuntimeInspector(
                    sessionDirectory,
                    replayJournal,
                    rngProbe,
                    settings.InspectorEnabled,
                    settings.InspectorOverlayEnabled,
                    settings.InspectorExportEnabled,
                    settings.InspectorSampleEveryMovieTicks,
                    settings.InspectorExportEverySamples,
                    settings.InspectorExportQueueCapacity,
                    flushTimeout,
                    logInfo,
                    logWarning,
                    logError,
                    Emit);
                Emit(
                    "inspector-started",
                    new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["enabled"] =
                            runtimeInspector.Enabled ? "true" : "false",
                        ["exportEnabled"] =
                            settings.InspectorExportEnabled
                                ? "true"
                                : "false",
                        ["outputDirectoryName"] = "inspector",
                        ["sampleEveryMovieTicks"] =
                            settings.InspectorSampleEveryMovieTicks
                                .ToString(
                                    CultureInfo.InvariantCulture)
                    });
            }
            catch (Exception exception)
            {
                runtimeInspector?.Dispose();
                runtimeInspector = null;
                logError(
                    "T11 Inspector could not start: "
                    + exception.Message);
            }
            Emit(
                "verification-preflight",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["allowed"] = Verification.Allowed ? "true" : "false",
                    ["reason"] = Verification.Reason,
                    ["requested"] = Verification.Requested ? "true" : "false",
                    ["unexpectedMods"] = string.Join(",", Verification.UnexpectedMods)
                });

            if (settings.ReplaySaveEnabled && replayTimingReady)
            {
                var baselineProvider =
                    new DesktopSaveSlotBaselineProvider();
                var replaySaveStore =
                    new ContentAddressedReplaySaveStore(
                        SavePathResolver.Current.GetTasDataPath(
                            "replay-saves", "v1"),
                        manifestHash);
                var restoreCoordinator =
                    new RuntimeReplayRestoreCoordinator(
                        replaySaveStore,
                        baselineProvider,
                        settings.DedicatedTasSaveSlot,
                        sessionId,
                        manifestHash,
                        replayJournal,
                        logInfo,
                        logWarning,
                        logError,
                        restoreAccelerator);
                replaySaveManager = new RuntimeReplaySaveManager(
                    replayJournal,
                    replaySaveStore,
                    new RuntimeReplaySaveCapture(
                        manifestBytes,
                        manifestHash,
                        manifest.GameVersion,
                        manifest.ModdingApiVersion),
                    new AutoSavePolicy(
                        settings.ReplaySaveAutoEnabled,
                        settings.ReplaySaveAutoIntervalMovieTicks,
                        settings.ReplaySaveAutoRetentionCount),
                    restoreCoordinator,
                    logInfo,
                    logWarning,
                    logError);
                replaySaveManager.AutoSavePolicyChanged += policy =>
                {
                    // Preserve only these operational preferences in the Mod's
                    // existing IGlobalSettings object. Execution settings remain
                    // frozen in the session copy; no game save is modified.
                    persistentSettings.ReplaySaveAutoEnabled = policy.Enabled;
                    persistentSettings.ReplaySaveAutoIntervalMovieTicks = policy.IntervalMovieTicks;
                    persistentSettings.ReplaySaveAutoRetentionCount = policy.RetentionCount;
                    logInfo("Auto-save policy updated; global settings retain it on normal exit.");
                };
                replaySaveManager.Start();
                if (settings.ReplaySaveOverlayEnabled)
                {
                    replaySaveMenu = new ReplaySaveMenuController(
                        replaySaveManager,
                        logWarning);
                }

                replaySaveProbeExperiment =
                    RuntimeReplaySaveProbeExperiment.TryStart(
                        sessionDirectory,
                        manifestHash,
                        replaySaveManager,
                        replayJournal,
                        logInfo,
                        logWarning,
                        logError);

                Emit(
                    "replay-save-service-started",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["autoEnabled"] =
                            settings.ReplaySaveAutoEnabled ? "true" : "false",
                        ["autoIntervalMovieTicks"] =
                            settings.ReplaySaveAutoIntervalMovieTicks.ToString(
                                CultureInfo.InvariantCulture),
                        ["autoRetentionCount"] =
                            settings.ReplaySaveAutoRetentionCount.ToString(
                                CultureInfo.InvariantCulture),
                        ["dedicatedTasSaveSlot"] =
                            settings.DedicatedTasSaveSlot.ToString(
                                CultureInfo.InvariantCulture),
                        ["deterministicTimingEnabled"] =
                            "false",
                        ["deterministicTimingRequested"] =
                            settings.ReplaySaveDeterministicTimingEnabled
                                ? "true"
                                : "false",
                        ["timingProfile"] = "none"
                    });
            }

            keyframeResolution =
                keyframeResolution.WithAcceleratorRegistration(
                    replaySaveManager != null
                    && restoreAccelerator != null);
            Emit(
                "semantic-keyframe-tier",
                new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["acceleratorRegistered"] =
                        keyframeResolution.AcceleratorRegistered
                            ? "true"
                            : "false",
                    ["captureAllowed"] =
                        keyframeResolution.CaptureAllowed
                            ? "true"
                            : "false",
                    ["enabled"] =
                        settings.EnableSemanticKeyframes
                            ? "true"
                            : "false",
                    ["reasonCodes"] =
                        string.Join(
                            ",",
                            keyframeResolution.ReasonCodes),
                    ["restorePlan"] = "FullReplay",
                    ["tier"] =
                        keyframeResolution.Tier.ToString()
                });

            if (settings.CompanionEnabled)
            {
                try
                {
                    var assemblyLocation =
                        typeof(HollowKnightTASMod)
                            .Assembly.Location;
                    var modRoot =
                        Path.GetDirectoryName(
                            assemblyLocation)
                        ?? throw new InvalidOperationException(
                            "Runtime assembly directory is unavailable.");
                    companionService =
                        new RuntimeCompanionService(
                            settings,
                            modRoot,
                            sessionId,
                            manifestHash,
                            manifest.GameVersion,
                            manifest.ModdingApiVersion,
                            replayJournal,
                            replaySaveManager,
                            runtimeInspector,
                            keyframeResolution,
                            logInfo,
                            logWarning,
                            logError,
                            Emit);
                    companionService.Start();
                }
                catch (Exception exception)
                {
                    companionService?.Dispose();
                    companionService = null;
                    logError(
                        "T12 Companion/IPC degraded; core Runtime "
                        + "remains available: "
                        + exception);
                    Emit(
                        "companion-service-fault",
                        new Dictionary<string, string>(
                            StringComparer.Ordinal)
                        {
                            ["exceptionType"] =
                                exception.GetType().Name,
                            ["fallback"] =
                                "runtime-and-replay-save-remain-available"
                        });
                }
            }
            else
            {
                Emit(
                    "companion-service-disabled",
                    new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["pipeCreated"] = "false",
                        ["processCreated"] = "false"
                    });
            }

            if (Verification.Requested && !Verification.Allowed)
            {
                logWarning(
                    "Verification mode rejected: "
                    + Verification.Reason
                    + "; unexpectedMods="
                    + string.Join(",", Verification.UnexpectedMods));
            }
            else
            {
                logDebug("T01 environment manifest and verification preflight completed.");
            }

            rngProbeExperiment = RuntimeRngProbeExperiment.TryStart(
                sessionDirectory,
                rngProbe,
                replayJournal,
                logInfo,
                logWarning,
                logError);
            if (rngProbeExperiment != null)
            {
                Emit(
                    "rng-probe-started",
                    new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["outputDirectoryName"] = "rng/probe"
                });
            }

            automationReadyProbe = RuntimeAutomationReadyProbe.TryStart(
                sessionDirectory,
                logInfo,
                logDebug,
                logError);
            if (automationReadyProbe != null)
            {
                Emit(
                    "automation-ready-probe-started",
                    new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["outputDirectoryName"] =
                            "automation-ready"
                    });
            }

            finalTasReadyProbe = RuntimeFinalTasReadyProbe.TryStart(
                sessionDirectory,
                logInfo,
                logDebug,
                logError);
            if (finalTasReadyProbe != null)
            {
                Emit(
                    "final-tas-ready-probe-started",
                    new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["outputDirectoryName"] =
                            "final-tas-ready"
                    });
            }

            inputPhaseProbe = InputPhaseProbe.TryStart(
                sessionId,
                sessionDirectory,
                logInfo,
                logDebug,
                logWarning,
                logError);
            if (inputPhaseProbe != null)
            {
                Emit(
                    "input-phase-probe-started",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["outputDirectoryName"] = "input-phase"
                    });
            }

            runtimeClockProbe = RuntimeClockProbe.TryStart(
                sessionId,
                manifestHash,
                sessionDirectory,
                logInfo,
                logDebug,
                logWarning,
                logError);
            if (runtimeClockProbe != null)
            {
                Emit(
                    "timing-probe-started",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["outputDirectoryName"] = "timing"
                });
            }

            inspectorProbeExperiment =
                RuntimeInspectorProbeExperiment.TryStart(
                    sessionDirectory,
                    runtimeInspector,
                    replayJournal,
                    logInfo,
                    logError);
            if (inspectorProbeExperiment != null)
            {
                Emit(
                    "inspector-probe-started",
                    new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["outputDirectoryName"] =
                            "inspector/probe"
                    });
            }

            snapshotProbeExperiment = RuntimeSnapshotProbeExperiment.TryStart(
                sessionDirectory,
                logInfo,
                logDebug,
                logWarning,
                logError);
            if (snapshotProbeExperiment != null)
            {
                Emit(
                    "state-probe-started",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["outputDirectoryName"] = "state"
                });
            }

            playbackProbeExperiment = RuntimePlaybackProbeExperiment.TryStart(
                sessionDirectory,
                manifestHash,
                replayJournal,
                rngProbe,
                logInfo,
                logDebug,
                logWarning,
                logError);
            if (playbackProbeExperiment != null)
            {
                Emit(
                    "playback-probe-started",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["outputDirectoryName"] = "playback"
                });
            }

            stepProbeExperiment = RuntimeStepProbeExperiment.TryStart(
                sessionDirectory,
                manifestHash,
                logInfo,
                logDebug,
                logWarning,
                logError);
            if (stepProbeExperiment != null)
            {
                Emit(
                    "step-probe-started",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["outputDirectoryName"] = "step"
                    });
            }
        }

        public void Stop(string reason)
        {
            if (Volatile.Read(ref started) == 0
                || Interlocked.CompareExchange(ref stopped, 1, 0) != 0)
            {
                return;
            }

            var sink = eventSink;
            try
            {
                companionService?.Dispose();
                companionService = null;
                stepProbeExperiment?.Dispose();
                stepProbeExperiment = null;
                inspectorProbeExperiment?.Dispose();
                inspectorProbeExperiment = null;
                rngProbeExperiment?.Dispose();
                rngProbeExperiment = null;
                runtimeInspector?.Dispose();
                runtimeInspector = null;
                playbackProbeExperiment?.Dispose();
                playbackProbeExperiment = null;
                snapshotProbeExperiment?.Dispose();
                snapshotProbeExperiment = null;
                replaySaveProbeExperiment?.Dispose();
                replaySaveProbeExperiment = null;
                replaySaveMenu?.Dispose();
                replaySaveMenu = null;
                replaySaveManager?.Dispose();
                replaySaveManager = null;
                rngProbe?.Dispose();
                rngProbe = null;
                randomTrace?.Dispose();
                randomTrace = null;
                renderRandomIsolation?.Dispose();
                renderRandomIsolation = null;
                replayJournal?.Dispose();
                replayJournal = null;
                runtimeClockProbe?.Dispose();
                runtimeClockProbe = null;
                inputPhaseProbe?.Dispose();
                inputPhaseProbe = null;
                automationReadyProbe?.Dispose();
                automationReadyProbe = null;
                finalTasReadyProbe?.Dispose();
                finalTasReadyProbe = null;
                if (sink != null)
                {
                    Emit(
                        "session-stop",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["droppedEvents"] = sink.DroppedCount.ToString(CultureInfo.InvariantCulture),
                            ["reason"] = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason
                        });
                    sink.Flush(TimeSpan.FromMilliseconds(settings.ExitFlushTimeoutMilliseconds));
                }
            }
            catch (Exception exception)
            {
                logError("Failed to flush T01 evidence: " + exception);
            }
            finally
            {
                sink?.Dispose();
                eventSink = null;
                logInfo("session-stop sessionId=" + (sessionId ?? "unknown") + " reason=" + reason);
            }
        }

        public void Dispose()
        {
            Stop("dispose");
        }

        private void Emit(string eventType, IReadOnlyDictionary<string, string> fields)
        {
            var sink = eventSink;
            if (sink == null || sessionId == null)
            {
                return;
            }

            sink.Emit(
                new StructuredEvent(
                    1,
                    sessionId,
                    Interlocked.Increment(ref sequence),
                    eventType,
                    DateTimeOffset.UtcNow,
                    fields));
        }

        private static string CreateSessionId()
        {
            return DateTimeOffset.UtcNow.ToString(
                       "yyyyMMdd'T'HHmmss.fffffff'Z'",
                       CultureInfo.InvariantCulture)
                   + "-"
                   + Guid.NewGuid().ToString("N");
        }

        private static void WriteAtomic(string destinationPath, byte[] bytes)
        {
            var temporaryPath = destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                if (File.Exists(destinationPath))
                {
                    File.Replace(temporaryPath, destinationPath, null);
                }
                else
                {
                    File.Move(temporaryPath, destinationPath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }
}
