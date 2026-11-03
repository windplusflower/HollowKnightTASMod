using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using GlobalEnums;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Ledger;
using HollowKnightTAS.Core.Serialization;
using Modding;
using UnityEngine;
using UnityEngine.SceneManagement;
using USceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowKnightTAS.Runtime.Timing
{
    public sealed class RuntimeClockProbe : IDisposable
    {
        private const int SchemaVersion = 1;
        private static readonly TickPhase[] MainVisualPhases =
        {
            TickPhase.VisualUpdateBegin,
            TickPhase.InControlCommitted,
            TickPhase.HeroUpdateBeforeOriginal,
            TickPhase.LateUpdateEnd
        };

        private readonly string sessionId;
        private readonly string manifestSha256;
        private readonly TimingProbeOptions options;
        private readonly Action<string> logInfo;
        private readonly Action<string> logDebug;
        private readonly Action<string> logWarning;
        private readonly Action<string> logError;
        private readonly string outputDirectory;
        private readonly TickLedgerJsonlSink ledgerSink;
        private readonly List<TickLedgerRecord> records = new(8192);
        private readonly SortedDictionary<int, int> fixedStepHistogram = new();
        private readonly InControlClockProbe inputClock;
        private readonly SceneEpochTracker sceneTracker;

        private RuntimeClockProbeRunner? runner;
        private HeroController? observedHero;
        private TimingProfileSnapshot originalProfile;
        private TimingProfileSnapshot appliedProfile;
        private TimingProfileSnapshot restoredProfile;
        private bool profileApplied;
        private bool profileRestored;
        private bool restoreEquivalent = true;
        private bool hooksRegistered;
        private bool attached;
        private bool stopped;
        private bool exitRequested;
        private bool exitIssued;
        private bool initialLoadRequested;
        private bool completionPending;
        private bool deathRequested;
        private bool deathEntered;
        private bool respawnEntered;
        private bool respawnCompleted;
        private bool returnToMenuRequested;
        private bool menuSceneSeen;
        private bool reloadRequested;
        private bool reenteredInitialScene;
        private bool lifecycleComplete;
        private bool previousHazardDeath;
        private bool previousHazardRespawning;
        private string initialScene = string.Empty;
        private string stopReason = "NotStopped";
        private float startedAtRealtime;
        private float attachedAtRealtime;
        private float menuSceneSeenAtRealtime;
        private float reloadAfterRealtime;
        private long sequence;
        private long visualTick;
        private long fixedTick;
        private long fixedTickAtPreviousVisual;
        private long firstDroppedSequence;
        private long lastDroppedSequence;
        private int pendingDroppedCount;
        private int inputObservationCount;

        private RuntimeClockProbe(
            string sessionId,
            string manifestSha256,
            string sessionDirectory,
            TimingProbeOptions options,
            Action<string> logInfo,
            Action<string> logDebug,
            Action<string> logWarning,
            Action<string> logError)
        {
            this.sessionId = sessionId;
            this.manifestSha256 = manifestSha256;
            this.options = options;
            this.logInfo = logInfo;
            this.logDebug = logDebug;
            this.logWarning = logWarning;
            this.logError = logError;
            outputDirectory = Path.Combine(
                sessionDirectory,
                "timing",
                options.RunId);
            Directory.CreateDirectory(outputDirectory);
            ledgerSink = new TickLedgerJsonlSink(
                Path.Combine(outputDirectory, "ledger.jsonl"),
                32768,
                TimeSpan.FromSeconds(5),
                budgetDirectory: Path.GetDirectoryName(Path.GetFullPath(sessionDirectory)));
            inputClock = new InControlClockProbe(OnInControlCommitted);
            sceneTracker = new SceneEpochTracker(
                OnSceneLoadRequested,
                OnActiveSceneChanged);
        }

        public string OutputDirectory => outputDirectory;
        public bool IsStopped => stopped;

        public static RuntimeClockProbe? TryStart(
            string sessionId,
            string manifestSha256,
            string sessionDirectory,
            Action<string> logInfo,
            Action<string> logDebug,
            Action<string> logWarning,
            Action<string> logError)
        {
            var parse = TimingProbeOptions.Parse(Environment.GetCommandLineArgs());
            if (!parse.Requested)
            {
                return null;
            }

            if (parse.Error != null || parse.Options == null)
            {
                throw new InvalidOperationException(
                    "Invalid T03 timing probe arguments: " + parse.Error);
            }

            var probe = new RuntimeClockProbe(
                sessionId,
                manifestSha256,
                sessionDirectory,
                parse.Options,
                logInfo,
                logDebug,
                logWarning,
                logError);
            probe.Start();
            return probe;
        }

        public void Dispose()
        {
            if (!stopped)
            {
                Stop("ApplicationQuit");
            }

            if (runner != null && !exitIssued)
            {
                UnityEngine.Object.Destroy(runner.gameObject);
                runner = null;
            }
        }

        internal void OnVisualUpdate()
        {
            try
            {
                if (exitRequested && !exitIssued)
                {
                    exitIssued = true;
                    Application.Quit();
                    return;
                }

                if (stopped)
                {
                    return;
                }

                var elapsed = Time.realtimeSinceStartup - startedAtRealtime;
                if (!attached && elapsed > 90f)
                {
                    Stop("StartupTimeout");
                    return;
                }

                if (attached
                    && Time.realtimeSinceStartup - attachedAtRealtime > 240f)
                {
                    Stop("RunTimeout");
                    return;
                }

                if (!initialLoadRequested
                    && elapsed >= 2f
                    && options.AutoLoadSlot > 0
                    && string.Equals(
                        USceneManager.GetActiveScene().name,
                        "Menu_Title",
                        StringComparison.Ordinal)
                    && GameManager.instance != null)
                {
                    initialLoadRequested = true;
                    GameManager.instance.LoadGameFromUI(options.AutoLoadSlot);
                    return;
                }

                if (!attached)
                {
                    TryAttach();
                    return;
                }

                visualTick++;
                var fixedSteps = checked((int)(fixedTick - fixedTickAtPreviousVisual));
                fixedTickAtPreviousVisual = fixedTick;
                fixedStepHistogram.TryGetValue(fixedSteps, out var count);
                fixedStepHistogram[fixedSteps] = count + 1;
                Emit(TickPhase.VisualUpdateBegin, fixedSteps, string.Empty);

                if (options.Profile == TimingProfile.PScene)
                {
                    AdvanceSceneLifecycle();
                }
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        internal void OnFixedUpdate()
        {
            if (stopped || !attached)
            {
                return;
            }

            try
            {
                fixedTick++;
                Emit(TickPhase.FixedUpdateBegin, -1, string.Empty);
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        internal void OnLateUpdate()
        {
            if (stopped || !attached)
            {
                return;
            }

            try
            {
                Emit(TickPhase.LateUpdateEnd, -1, string.Empty);

                if (completionPending
                    && (options.Profile != TimingProfile.PScene
                        || lifecycleComplete))
                {
                    Stop("Completed");
                    return;
                }

                if (options.Profile == TimingProfile.PScene
                    && menuSceneSeen
                    && !lifecycleComplete
                    && Time.realtimeSinceStartup - menuSceneSeenAtRealtime > 60f)
                {
                    Stop("LifecycleTimeout");
                    return;
                }

                if (options.BusyLoadMilliseconds > 0)
                {
                    ApplyBusyLoad(options.BusyLoadMilliseconds);
                }
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        private void Start()
        {
            startedAtRealtime = Time.realtimeSinceStartup;
            inputClock.Start();
            sceneTracker.Start();
            ModHooks.HeroUpdateHook += OnHeroUpdate;
            hooksRegistered = true;

            var gameObject = new GameObject("HollowKnightTAS.RuntimeClockProbe");
            UnityEngine.Object.DontDestroyOnLoad(gameObject);
            runner = gameObject.AddComponent<RuntimeClockProbeRunner>();
            runner.Initialize(this);
            logInfo(
                "T03 timing probe created profile="
                + options.ProfileId
                + " runId="
                + options.RunId
                + " targetInputTicks="
                + options.TargetInputTicks.ToString(CultureInfo.InvariantCulture));
        }

        private void TryAttach()
        {
            var hero = HeroController.instance;
                if (hero != null && UnityEngine.Time.frameCount % 50 == 0) {
                    var heroPos = hero.transform.position;
                    var doors = TransitionPoint.TransitionPoints != null ? string.Join(", ", System.Linq.Enumerable.Select(TransitionPoint.TransitionPoints, tp => tp.name + "@(" + tp.transform.position.x.ToString("F1") + "," + tp.transform.position.y.ToString("F1") + ")->" + tp.targetScene)) : "";
                    Modding.Logger.Log($"[TRACE frame={UnityEngine.Time.frameCount}] Scene={GameManager.instance?.sceneName}, Hero=({heroPos.x:F1},{heroPos.y:F1}), Doors=[{doors}]");
                }
            var gameManager = GameManager.instance;
            if (hero == null
                || gameManager == null
                || gameManager.gameState != GameState.PLAYING
                || !hero.gameObject.activeInHierarchy)
            {
                return;
            }

            sceneTracker.ResetForRecording();
            initialScene = sceneTracker.CurrentScene;
            visualTick = 0;
            fixedTick = 0;
            fixedTickAtPreviousVisual = 0;
            inputObservationCount = 0;
            originalProfile = TimingProfileSnapshot.Capture();
            ApplyProfile();
            appliedProfile = TimingProfileSnapshot.Capture();
            profileApplied = true;
            attached = true;
            attachedAtRealtime = Time.realtimeSinceStartup;
            observedHero = hero;
            previousHazardDeath = hero.cState.hazardDeath;
            previousHazardRespawning = hero.cState.hazardRespawning;

            Emit(
                TickPhase.ProfileApplied,
                -1,
                "targetFrameRate="
                + appliedProfile.TargetFrameRate.ToString(CultureInfo.InvariantCulture)
                + ";vSyncCount="
                + appliedProfile.VSyncCount.ToString(CultureInfo.InvariantCulture)
                + ";busyLoadMilliseconds="
                + options.BusyLoadMilliseconds.ToString(CultureInfo.InvariantCulture));
            WriteProfileFile();
            WriteAtomic(
                Path.Combine(outputDirectory, "probe.ready"),
                new UTF8Encoding(false).GetBytes(
                    options.ProfileId + " " + options.RunId + "\n"));
            logDebug(
                "T03 timing probe attached scene="
                + initialScene
                + " inputTick="
                + inputClock.CurrentTick.ToString(CultureInfo.InvariantCulture));
        }

        private void ApplyProfile()
        {
            QualitySettings.vSyncCount = 0;
            switch (options.Profile)
            {
                case TimingProfile.P30:
                    Application.targetFrameRate = 30;
                    break;
                default:
                    Application.targetFrameRate = 60;
                    break;
            }
        }

        private void OnInControlCommitted(ulong inputTick, float deltaTime)
        {
            if (stopped || !attached)
            {
                return;
            }

            try
            {
                inputObservationCount++;
                Emit(
                    TickPhase.InControlCommitted,
                    -1,
                    "inControlDeltaTime="
                    + deltaTime.ToString("R", CultureInfo.InvariantCulture));
                if (inputObservationCount >= options.TargetInputTicks)
                {
                    completionPending = true;
                }
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        private void OnHeroUpdate()
        {
            if (stopped || !attached)
            {
                return;
            }

            try
            {
                var hero = HeroController.instance;
                if (hero == null)
                {
                    return;
                }

                if (!ReferenceEquals(observedHero, hero))
                {
                    observedHero = hero;
                    previousHazardDeath = hero.cState.hazardDeath;
                    previousHazardRespawning = hero.cState.hazardRespawning;
                }

                Emit(TickPhase.HeroUpdateBeforeOriginal, -1, string.Empty);
                var hazardDeath = hero.cState.hazardDeath;
                var hazardRespawning = hero.cState.hazardRespawning;
                if (!previousHazardDeath && hazardDeath)
                {
                    deathEntered = true;
                    Emit(TickPhase.HeroHazardDeathEntered, -1, string.Empty);
                }

                if (!previousHazardRespawning && hazardRespawning)
                {
                    respawnEntered = true;
                    Emit(TickPhase.HeroHazardRespawnEntered, -1, string.Empty);
                }

                if (previousHazardRespawning && !hazardRespawning)
                {
                    respawnCompleted = true;
                    Emit(TickPhase.HeroHazardRespawnCompleted, -1, string.Empty);
                }

                previousHazardDeath = hazardDeath;
                previousHazardRespawning = hazardRespawning;
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        private void AdvanceSceneLifecycle()
        {
            var deathThreshold = Math.Max(30, options.TargetInputTicks / 5);
            var sceneThreshold = Math.Max(
                deathThreshold + 120,
                options.TargetInputTicks * 45 / 100);

            var hero = HeroController.instance;
            if (!deathRequested
                && inputObservationCount >= deathThreshold
                && hero != null
                && !hero.cState.hazardDeath
                && !hero.cState.hazardRespawning)
            {
                deathRequested = true;
                Emit(
                    TickPhase.HeroHazardDeathRequested,
                    -1,
                    "hazardType=spikes;damage=1");
                hero.TakeDamage(null, CollisionSide.bottom, 1, 2);
            }

            if (!returnToMenuRequested
                && respawnCompleted
                && inputObservationCount >= sceneThreshold
                && GameManager.instance != null)
            {
                returnToMenuRequested = true;
                Emit(
                    TickPhase.SceneLoadRequested,
                    -1,
                    "probe=return-to-menu-dont-save");
                runner?.StartCoroutine(
                    GameManager.instance.ReturnToMainMenu(
                        GameManager.ReturnToMainMenuSaveModes.DontSave));
            }

            if (menuSceneSeen
                && !reloadRequested
                && Time.realtimeSinceStartup >= reloadAfterRealtime
                && GameManager.instance != null
                && string.Equals(
                    USceneManager.GetActiveScene().name,
                    "Menu_Title",
                    StringComparison.Ordinal)
                && GameManager.instance.gameState == GameState.MAIN_MENU)
            {
                reloadRequested = true;
                Emit(
                    TickPhase.SceneLoadRequested,
                    -1,
                    "probe=reload-slot-"
                    + options.AutoLoadSlot.ToString(CultureInfo.InvariantCulture)
                    + ";gameState="
                    + GameManager.instance.gameState);
                GameManager.instance.LoadGameFromUI(options.AutoLoadSlot);
            }

            if (reenteredInitialScene)
            {
                var currentHero = HeroController.instance;
                if (currentHero != null
                    && currentHero.gameObject.activeInHierarchy
                    && GameManager.instance != null
                    && GameManager.instance.gameState == GameState.PLAYING)
                {
                    lifecycleComplete = deathEntered
                                        && respawnEntered
                                        && respawnCompleted
                                        && menuSceneSeen
                                        && reloadRequested;
                }
            }
        }

        private void OnSceneLoadRequested(string targetScene)
        {
            if (stopped || !attached)
            {
                return;
            }

            try
            {
                Emit(
                    TickPhase.SceneLoadRequested,
                    -1,
                    "hookTarget=" + SanitizeDetail(targetScene));
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        private void OnActiveSceneChanged(
            Scene previous,
            Scene current,
            int epoch)
        {
            if (stopped || !attached)
            {
                return;
            }

            try
            {
                Emit(
                    TickPhase.ActiveSceneChanged,
                    -1,
                    "from="
                    + SanitizeDetail(previous.name)
                    + ";to="
                    + SanitizeDetail(current.name)
                    + ";epoch="
                    + epoch.ToString(CultureInfo.InvariantCulture));

                if (options.Profile != TimingProfile.PScene)
                {
                    return;
                }

                if (string.Equals(current.name, "Menu_Title", StringComparison.Ordinal))
                {
                    menuSceneSeen = true;
                    menuSceneSeenAtRealtime = Time.realtimeSinceStartup;
                    reloadAfterRealtime = menuSceneSeenAtRealtime + 2f;
                }
                else if (menuSceneSeen
                         && string.Equals(
                             current.name,
                             initialScene,
                             StringComparison.Ordinal))
                {
                    reenteredInitialScene = true;
                }
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        private void Emit(
            TickPhase phase,
            int fixedStepsSincePreviousVisual,
            string detail)
        {
            if (pendingDroppedCount > 0 && phase != TickPhase.LedgerGap)
            {
                TryEmitPendingGap();
            }

            var record = CreateRecord(
                ++sequence,
                phase,
                fixedStepsSincePreviousVisual,
                detail);
            records.Add(record);
            if (!ledgerSink.TryEmit(record))
            {
                NoteDropped(record.Sequence);
            }
        }

        private TickLedgerRecord CreateRecord(
            long recordSequence,
            TickPhase phase,
            int fixedStepsSincePreviousVisual,
            string detail)
        {
            return new TickLedgerRecord(
                recordSequence,
                sessionId,
                manifestSha256,
                options.RunId,
                options.ProfileId,
                new TickStamp(
                    inputClock.CurrentTick,
                    visualTick,
                    fixedTick,
                    sceneTracker.CurrentEpoch,
                    phase),
                fixedStepsSincePreviousVisual,
                Time.time,
                Time.fixedTime,
                Time.time - Time.fixedTime,
                Time.deltaTime,
                Time.unscaledDeltaTime,
                Time.timeScale,
                Time.realtimeSinceStartup,
                sceneTracker.CurrentScene,
                SanitizeDetail(detail));
        }

        private void NoteDropped(long droppedSequence)
        {
            if (pendingDroppedCount == 0)
            {
                firstDroppedSequence = droppedSequence;
            }

            lastDroppedSequence = droppedSequence;
            pendingDroppedCount++;
        }

        private void TryEmitPendingGap()
        {
            if (pendingDroppedCount == 0)
            {
                return;
            }

            var originalFirst = firstDroppedSequence;
            var originalLast = lastDroppedSequence;
            var originalCount = pendingDroppedCount;
            var gap = CreateRecord(
                ++sequence,
                TickPhase.LedgerGap,
                -1,
                "firstDroppedSequence="
                + originalFirst.ToString(CultureInfo.InvariantCulture)
                + ";lastDroppedSequence="
                + originalLast.ToString(CultureInfo.InvariantCulture)
                + ";droppedCount="
                + originalCount.ToString(CultureInfo.InvariantCulture));
            records.Add(gap);
            if (ledgerSink.TryEmit(gap))
            {
                pendingDroppedCount = 0;
                firstDroppedSequence = 0;
                lastDroppedSequence = 0;
            }
            else
            {
                NoteDropped(gap.Sequence);
            }
        }

        private void Stop(string reason)
        {
            if (stopped)
            {
                return;
            }

            stopped = true;
            stopReason = reason;
            UnregisterHooks();

            try
            {
                RestoreProfile();
                if (attached)
                {
                    Emit(
                        TickPhase.ProfileRestored,
                        -1,
                        "equivalent=" + (restoreEquivalent ? "true" : "false"));
                }

                ledgerSink.Flush(TimeSpan.FromSeconds(5));
                if (pendingDroppedCount > 0)
                {
                    TryEmitPendingGap();
                    ledgerSink.Flush(TimeSpan.FromSeconds(5));
                }

                var validation = TickLedgerValidator.Validate(records);
                var signature = CalculatePhaseOrderSignature();
                var signatureHash = string.IsNullOrEmpty(signature)
                    ? string.Empty
                    : Sha256Utility.ComputeUtf8Hex(signature);
                var lifecyclePass = options.Profile != TimingProfile.PScene
                                    || lifecycleComplete;
                var runPass = string.Equals(reason, "Completed", StringComparison.Ordinal)
                              && inputObservationCount >= options.TargetInputTicks
                              && validation.IsValid
                              && ledgerSink.DroppedCount == 0
                              && restoreEquivalent
                              && lifecyclePass
                              && !string.IsNullOrEmpty(signature);

                restoredProfile = TimingProfileSnapshot.Capture();
                WriteProfileFile();
                WriteInvariantsFile(
                    validation,
                    signature,
                    signatureHash,
                    lifecyclePass);
                WriteResultFile(
                    validation,
                    signature,
                    signatureHash,
                    lifecyclePass,
                    runPass);
                WriteVerdictFile(validation, signatureHash, lifecyclePass, runPass);

                logInfo(
                    "T03 timing probe stopped profile="
                    + options.ProfileId
                    + " reason="
                    + reason
                    + " runPass="
                    + (runPass ? "true" : "false")
                    + " inputObservations="
                    + inputObservationCount.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception exception)
            {
                logError("T03 timing probe stop failed: " + exception);
            }
            finally
            {
                ledgerSink.Dispose();
                exitRequested = options.ExitOnComplete;
            }
        }

        private void RestoreProfile()
        {
            if (!profileApplied || profileRestored)
            {
                return;
            }

            Application.targetFrameRate = originalProfile.TargetFrameRate;
            QualitySettings.vSyncCount = originalProfile.VSyncCount;
            Time.timeScale = SingleBits.ToSingle(originalProfile.TimeScaleBits);
            Time.fixedDeltaTime =
                SingleBits.ToSingle(originalProfile.FixedDeltaTimeBits);
            restoredProfile = TimingProfileSnapshot.Capture();
            restoreEquivalent = originalProfile.Equals(restoredProfile);
            profileRestored = true;
            if (!restoreEquivalent)
            {
                logWarning("T03 profile restoration was not bit-equivalent.");
            }
        }

        private void UnregisterHooks()
        {
            if (!hooksRegistered)
            {
                return;
            }

            hooksRegistered = false;
            ModHooks.HeroUpdateHook -= OnHeroUpdate;
            inputClock.Dispose();
            sceneTracker.Dispose();
        }

        private void Fail(Exception exception)
        {
            if (stopped)
            {
                return;
            }

            logError("T03 timing probe exception: " + exception);
            Stop("ProbeException-" + exception.GetType().Name);
        }

        private string CalculatePhaseOrderSignature()
        {
            var patterns = records
                .Where(record => MainVisualPhases.Contains(record.Stamp.Phase))
                .GroupBy(
                    record => new
                    {
                        record.Stamp.SceneEpoch,
                        record.Stamp.VisualTick
                    })
                .Select(
                    group => group
                        .OrderBy(record => record.Sequence)
                        .Select(record => record.Stamp.Phase)
                        .ToArray())
                .Where(
                    phases => MainVisualPhases.All(
                        phase => phases.Count(item => item == phase) == 1))
                .Select(phases => string.Join(">", phases.Select(item => item.ToString())))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            return string.Join("||", patterns);
        }

        private void WriteProfileFile()
        {
            var builder = new StringBuilder(1024);
            builder.Append('{');
            AppendNumber(builder, "schemaVersion", SchemaVersion);
            AppendString(builder, "runId", options.RunId);
            AppendString(builder, "profile", options.ProfileId);
            AppendNumber(builder, "targetInputTicks", options.TargetInputTicks);
            AppendNumber(
                builder,
                "busyLoadMilliseconds",
                options.BusyLoadMilliseconds);
            AppendSnapshot(builder, "original", originalProfile);
            AppendSnapshot(builder, "applied", appliedProfile);
            AppendSnapshot(
                builder,
                "restored",
                profileRestored ? restoredProfile : default);
            AppendBoolean(builder, "restoreAttempted", profileRestored);
            AppendBoolean(builder, "restoreEquivalent", restoreEquivalent);
            builder.Append('}');
            WriteAtomic(
                Path.Combine(outputDirectory, "profile.json"),
                new UTF8Encoding(false).GetBytes(builder.ToString()));
        }

        private void WriteInvariantsFile(
            LedgerValidationReport validation,
            string signature,
            string signatureHash,
            bool lifecyclePass)
        {
            var builder = new StringBuilder(1024);
            builder.Append('{');
            AppendNumber(builder, "schemaVersion", SchemaVersion);
            AppendBoolean(builder, "validatorPass", validation.IsValid);
            AppendString(builder, "validatorError", validation.Error.ToString());
            AppendNumber(builder, "validatorErrorRecordIndex", validation.RecordIndex);
            AppendNumber(builder, "validatorErrorSequence", validation.Sequence);
            AppendString(builder, "validatorMessage", validation.Message);
            AppendNumber(builder, "droppedCount", ledgerSink.DroppedCount);
            AppendString(builder, "phaseOrderSignature", signature);
            AppendString(builder, "phaseOrderSignatureSha256", signatureHash);
            AppendBoolean(builder, "lifecyclePass", lifecyclePass);
            AppendBoolean(builder, "restoreEquivalent", restoreEquivalent);
            builder.Append('}');
            WriteAtomic(
                Path.Combine(outputDirectory, "invariants.json"),
                new UTF8Encoding(false).GetBytes(builder.ToString()));
        }

        private void WriteResultFile(
            LedgerValidationReport validation,
            string signature,
            string signatureHash,
            bool lifecyclePass,
            bool runPass)
        {
            var builder = new StringBuilder(2048);
            builder.Append('{');
            AppendNumber(builder, "schemaVersion", SchemaVersion);
            AppendString(builder, "sessionId", sessionId);
            AppendString(builder, "manifestSha256", manifestSha256);
            AppendString(builder, "runId", options.RunId);
            AppendString(builder, "profile", options.ProfileId);
            AppendString(builder, "stopReason", stopReason);
            AppendNumber(builder, "targetInputTicks", options.TargetInputTicks);
            AppendNumber(builder, "inputObservationCount", inputObservationCount);
            AppendNumber(builder, "recordCount", records.Count);
            AppendNumber(builder, "visualTickCount", visualTick);
            AppendNumber(builder, "fixedTickCount", fixedTick);
            AppendNumber(builder, "sceneEpoch", sceneTracker.CurrentEpoch);
            AppendNumber(builder, "droppedCount", ledgerSink.DroppedCount);
            AppendBoolean(builder, "validatorPass", validation.IsValid);
            AppendString(builder, "validatorError", validation.Error.ToString());
            AppendString(builder, "validatorMessage", validation.Message);
            AppendString(builder, "phaseOrderSignature", signature);
            AppendString(builder, "phaseOrderSignatureSha256", signatureHash);
            AppendHistogram(builder);
            AppendBoolean(builder, "deathRequested", deathRequested);
            AppendBoolean(builder, "deathEntered", deathEntered);
            AppendBoolean(builder, "respawnEntered", respawnEntered);
            AppendBoolean(builder, "respawnCompleted", respawnCompleted);
            AppendBoolean(builder, "menuSceneSeen", menuSceneSeen);
            AppendBoolean(builder, "reloadRequested", reloadRequested);
            AppendBoolean(builder, "reenteredInitialScene", reenteredInitialScene);
            AppendBoolean(builder, "lifecyclePass", lifecyclePass);
            AppendBoolean(builder, "restoreEquivalent", restoreEquivalent);
            AppendBoolean(builder, "runPass", runPass);
            builder.Append('}');
            WriteAtomic(
                Path.Combine(outputDirectory, "result.json"),
                new UTF8Encoding(false).GetBytes(builder.ToString()));
        }

        private void WriteVerdictFile(
            LedgerValidationReport validation,
            string signatureHash,
            bool lifecyclePass,
            bool runPass)
        {
            var lines = new[]
            {
                "# T03 Timing Probe Verdict",
                string.Empty,
                "- Profile: `" + options.ProfileId + "`",
                "- Run: `" + options.RunId + "`",
                "- Stop reason: `" + stopReason + "`",
                "- Input observations: " + inputObservationCount.ToString(CultureInfo.InvariantCulture),
                "- Visual ticks: " + visualTick.ToString(CultureInfo.InvariantCulture),
                "- Fixed ticks: " + fixedTick.ToString(CultureInfo.InvariantCulture),
                "- Scene epoch: " + sceneTracker.CurrentEpoch.ToString(CultureInfo.InvariantCulture),
                "- Dropped records: " + ledgerSink.DroppedCount.ToString(CultureInfo.InvariantCulture),
                "- Validator: " + validation.IsValid + " (`" + validation.Error + "`)",
                "- Phase-order SHA-256: `" + signatureHash + "`",
                "- Lifecycle pass: " + lifecyclePass,
                "- Restore equivalent: " + restoreEquivalent,
                "- Run pass: " + runPass,
                string.Empty
            };
            WriteAtomic(
                Path.Combine(outputDirectory, "verdict.md"),
                new UTF8Encoding(false).GetBytes(string.Join("\n", lines)));
        }

        private void AppendHistogram(StringBuilder builder)
        {
            AppendPropertyPrefix(builder, "fixedStepsHistogram");
            builder.Append('{');
            var first = true;
            foreach (var item in fixedStepHistogram)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                CanonicalJsonWriter.AppendString(
                    builder,
                    item.Key.ToString(CultureInfo.InvariantCulture));
                builder.Append(':');
                builder.Append(item.Value.ToString(CultureInfo.InvariantCulture));
                first = false;
            }

            builder.Append('}');
        }

        private static void AppendSnapshot(
            StringBuilder builder,
            string name,
            TimingProfileSnapshot value)
        {
            AppendPropertyPrefix(builder, name);
            builder.Append('{');
            AppendNumber(builder, "targetFrameRate", value.TargetFrameRate);
            AppendNumber(builder, "vSyncCount", value.VSyncCount);
            AppendString(
                builder,
                "timeScale",
                SingleBits.ToSingle(value.TimeScaleBits)
                    .ToString("R", CultureInfo.InvariantCulture));
            AppendNumber(builder, "timeScaleBits", value.TimeScaleBits);
            AppendString(
                builder,
                "fixedDeltaTime",
                SingleBits.ToSingle(value.FixedDeltaTimeBits)
                    .ToString("R", CultureInfo.InvariantCulture));
            AppendNumber(
                builder,
                "fixedDeltaTimeBits",
                value.FixedDeltaTimeBits);
            builder.Append('}');
        }

        private static void AppendString(
            StringBuilder builder,
            string name,
            string value)
        {
            AppendPropertyPrefix(builder, name);
            CanonicalJsonWriter.AppendString(builder, value ?? string.Empty);
        }

        private static void AppendNumber(
            StringBuilder builder,
            string name,
            long value)
        {
            AppendPropertyPrefix(builder, name);
            builder.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendBoolean(
            StringBuilder builder,
            string name,
            bool value)
        {
            AppendPropertyPrefix(builder, name);
            builder.Append(value ? "true" : "false");
        }

        private static void AppendPropertyPrefix(StringBuilder builder, string name)
        {
            if (builder[builder.Length - 1] != '{')
            {
                builder.Append(',');
            }

            CanonicalJsonWriter.AppendString(builder, name);
            builder.Append(':');
        }

        private static string SanitizeDetail(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
        }

        private static void ApplyBusyLoad(int milliseconds)
        {
            var durationTicks = Math.Max(
                1L,
                Stopwatch.Frequency * milliseconds / 1000L);
            var started = Stopwatch.GetTimestamp();
            while (Stopwatch.GetTimestamp() - started < durationTicks)
            {
                System.Threading.Thread.SpinWait(256);
            }
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

    [DefaultExecutionOrder(-32000)]
    internal sealed class RuntimeClockProbeRunner : MonoBehaviour
    {
        private RuntimeClockProbe? owner;

        internal void Initialize(RuntimeClockProbe value)
        {
            owner = value;
        }

        private void Update()
        {
            owner?.OnVisualUpdate();
        }

        private void FixedUpdate()
        {
            owner?.OnFixedUpdate();
        }

        private void LateUpdate()
        {
            owner?.OnLateUpdate();
        }
    }

    internal enum TimingProfile
    {
        P60,
        P30,
        PLoad,
        PScene
    }

    internal readonly struct TimingProfileSnapshot : IEquatable<TimingProfileSnapshot>
    {
        public TimingProfileSnapshot(
            int targetFrameRate,
            int vSyncCount,
            int timeScaleBits,
            int fixedDeltaTimeBits)
        {
            TargetFrameRate = targetFrameRate;
            VSyncCount = vSyncCount;
            TimeScaleBits = timeScaleBits;
            FixedDeltaTimeBits = fixedDeltaTimeBits;
        }

        public int TargetFrameRate { get; }
        public int VSyncCount { get; }
        public int TimeScaleBits { get; }
        public int FixedDeltaTimeBits { get; }

        public static TimingProfileSnapshot Capture()
        {
            return new TimingProfileSnapshot(
                Application.targetFrameRate,
                QualitySettings.vSyncCount,
                SingleBits.FromSingle(Time.timeScale),
                SingleBits.FromSingle(Time.fixedDeltaTime));
        }

        public bool Equals(TimingProfileSnapshot other)
        {
            return TargetFrameRate == other.TargetFrameRate
                   && VSyncCount == other.VSyncCount
                   && TimeScaleBits == other.TimeScaleBits
                   && FixedDeltaTimeBits == other.FixedDeltaTimeBits;
        }

        public override bool Equals(object? value)
        {
            return value is TimingProfileSnapshot other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = TargetFrameRate;
                hash = (hash * 397) ^ VSyncCount;
                hash = (hash * 397) ^ TimeScaleBits;
                hash = (hash * 397) ^ FixedDeltaTimeBits;
                return hash;
            }
        }
    }

    internal sealed class TimingProbeOptions
    {
        private TimingProbeOptions(
            TimingProfile profile,
            string runId,
            int autoLoadSlot,
            int targetInputTicks,
            bool exitOnComplete)
        {
            Profile = profile;
            RunId = runId;
            AutoLoadSlot = autoLoadSlot;
            TargetInputTicks = targetInputTicks;
            ExitOnComplete = exitOnComplete;
        }

        public TimingProfile Profile { get; }
        public string ProfileId => Profile switch
        {
            TimingProfile.P60 => "P60",
            TimingProfile.P30 => "P30",
            TimingProfile.PLoad => "PLOAD",
            TimingProfile.PScene => "PSCENE",
            _ => throw new ArgumentOutOfRangeException()
        };
        public string RunId { get; }
        public int AutoLoadSlot { get; }
        public int TargetInputTicks { get; }
        public bool ExitOnComplete { get; }
        public int BusyLoadMilliseconds => Profile == TimingProfile.PLoad ? 24 : 0;

        public static TimingProbeParseResult Parse(string[] arguments)
        {
            var profileText = ReadValue(arguments, "--hktas-timing-probe=");
            if (profileText == null)
            {
                return TimingProbeParseResult.NotRequested();
            }

            TimingProfile profile;
            switch (profileText.ToUpperInvariant())
            {
                case "P60":
                    profile = TimingProfile.P60;
                    break;
                case "P30":
                    profile = TimingProfile.P30;
                    break;
                case "PLOAD":
                    profile = TimingProfile.PLoad;
                    break;
                case "PSCENE":
                    profile = TimingProfile.PScene;
                    break;
                default:
                    return TimingProbeParseResult.Invalid(
                        "profile must be P60, P30, PLOAD, or PSCENE");
            }

            var runId = ReadValue(arguments, "--hktas-timing-probe-run=")
                        ?? profileText.ToLowerInvariant()
                        + "-"
                        + Guid.NewGuid().ToString("N");
            if (!IsSafeIdentifier(runId))
            {
                return TimingProbeParseResult.Invalid(
                    "run id must contain only letters, digits, dot, dash, or underscore");
            }

            var slotText = ReadValue(arguments, "--hktas-timing-probe-slot=") ?? "2";
            if (!int.TryParse(
                    slotText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var slot)
                || slot < 1
                || slot > 4)
            {
                return TimingProbeParseResult.Invalid("slot must be in [1, 4]");
            }

            var ticksText = ReadValue(
                                arguments,
                                "--hktas-timing-probe-input-ticks=")
                            ?? "1000";
            if (!int.TryParse(
                    ticksText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var ticks)
                || ticks < 100
                || ticks > 100000)
            {
                return TimingProbeParseResult.Invalid(
                    "input tick count must be in [100, 100000]");
            }

            return TimingProbeParseResult.Valid(
                new TimingProbeOptions(
                    profile,
                    runId,
                    slot,
                    ticks,
                    HasFlag(arguments, "--hktas-timing-probe-exit")));
        }

        private static string? ReadValue(string[] arguments, string prefix)
        {
            return arguments
                .FirstOrDefault(
                    value => value.StartsWith(
                        prefix,
                        StringComparison.OrdinalIgnoreCase))
                ?.Substring(prefix.Length);
        }

        private static bool HasFlag(string[] arguments, string flag)
        {
            return arguments.Any(
                value => string.Equals(
                    value,
                    flag,
                    StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsSafeIdentifier(string value)
        {
            return value.Length > 0
                   && value.Length <= 64
                   && value.All(
                       character => char.IsLetterOrDigit(character)
                                    || character == '.'
                                    || character == '-'
                                    || character == '_');
        }
    }

    internal sealed class TimingProbeParseResult
    {
        private TimingProbeParseResult(
            bool requested,
            TimingProbeOptions? options,
            string? error)
        {
            Requested = requested;
            Options = options;
            Error = error;
        }

        public bool Requested { get; }
        public TimingProbeOptions? Options { get; }
        public string? Error { get; }

        public static TimingProbeParseResult NotRequested()
        {
            return new TimingProbeParseResult(false, null, null);
        }

        public static TimingProbeParseResult Valid(TimingProbeOptions options)
        {
            return new TimingProbeParseResult(true, options, null);
        }

        public static TimingProbeParseResult Invalid(string error)
        {
            return new TimingProbeParseResult(true, null, error);
        }
    }
}
