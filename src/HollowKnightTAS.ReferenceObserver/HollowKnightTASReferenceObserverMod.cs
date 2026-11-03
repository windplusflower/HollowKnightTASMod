using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using GlobalEnums;
using HollowKnightTAS.Core.Control;
using HollowKnightTAS.Core.Diagnostics;
using HollowKnightTAS.Core.Verification;
using HollowKnightTAS.GameObservation;
using Modding;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using USceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowKnightTAS.ReferenceObserver
{
    /// <summary>
    /// Test-only no-TAS observer. It samples through a shared read-only
    /// library and never injects input, changes time, loads a save, or writes
    /// any Hero, PlayerData, Animator, Rigidbody2D, FSM, or RNG state.
    /// </summary>
    public sealed class HollowKnightTASReferenceObserverMod : Mod
    {
        private ReferenceObserverSession? session;

        public static int GetResidentRandomProducerAbi() => 1;

        public HollowKnightTASReferenceObserverMod()
            : base("HollowKnightTAS.ReferenceObserver")
        {
        }

        public override string GetVersion()
        {
            return "0.1.0";
        }

        public override void Initialize(
            Dictionary<string, Dictionary<string, GameObject>>
                preloadedObjects)
        {
            var options = ReferenceObserverOptions.Parse(
                Environment.GetCommandLineArgs());
            if (options == null)
            {
                Log("Reference observer idle: no T24 arguments.");
                return;
            }

            session = new ReferenceObserverSession(
                options,
                message => Log(message),
                message => LogError(message));
            session.Start();
            ModHooks.ApplicationQuitHook += OnApplicationQuit;
        }

        private void OnApplicationQuit()
        {
            session?.OnApplicationQuit();
        }
    }

    internal sealed class ReferenceObserverSession
    {
        private const int RequiredSemanticStableUpdates = 10;
        private const float RequiredAbsoluteTimeTarget = 512f;
        private const float RequiredRecordingAbsoluteTimeTarget = 768f;
        private const int RequiredRecordingFramePhaseModulo = 4;
        private const int RequiredRecordingFramePhase = 0;
        private const int ExternalRandomSynchronizationSeed = 1212896321;
        private const string ExternalClockProfile =
            "external-unity-startup-continuous-clock-v40-native-scene-lifecycle";
        private const string ExternalRandomSynchronizationPolicy =
            "unity-init-state-at-root-only-native-scene-lifecycle-v19";
        private static readonly FieldInfo SaveSlotField =
            typeof(SaveSlotButton).GetField(
                "saveSlot",
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(
                typeof(SaveSlotButton).FullName,
                "saveSlot");

        private readonly ReferenceObserverOptions options;
        private readonly Action<string> log;
        private readonly Action<string> logError;
        private readonly VanillaEquivalenceSampler sampler =
            new VanillaEquivalenceSampler();
        private readonly List<VanillaEquivalenceFrame> frames =
            new List<VanillaEquivalenceFrame>();
        private readonly Queue<InputPhaseObservation>
            inputPhasePrelude = new Queue<InputPhaseObservation>();
        private readonly List<InputPhaseObservation>
            inputPhaseObservations = new List<InputPhaseObservation>();
        private ReferenceObserverEarlyRunner? earlyRunner;
        private ReferenceObserverLateRunner? lateRunner;
        private ReferenceObserverCompletedFrameRunner? completedFrameRunner;
        private long visualTick;
        private long fixedTick;
        private int stableUpdates;
        private bool hasReadinessFixedTick;
        private long readinessFixedTick;
        private long readinessFixedSteps;
        private string readinessBlocker = "session-not-ready";
        private bool ready;
        private bool baselineCaptured;
        private bool recording;
        private bool complete;
        private bool frameOpenAtUpdateBegin;
        private string lastGameplaySceneName = string.Empty;
        private int sceneInputWarmupUpdates;
        private bool readinessCompletionPending;
        private bool frameCapturePending;
        private long pendingCaptureVisualTick;
        private long pendingCaptureFixedTick;
        private VanillaEquivalenceInputSample? pendingCaptureInputSample;
        private HeroController? pendingReadinessHero;
        private PlayerData? pendingReadinessPlayer;
        private HutongGames.PlayMaker.Fsm? pendingReadinessBenchFsm;
        private HeroAnimationController? pendingReadinessAnimation;
        private Rigidbody2D? pendingReadinessRigidbody;
        private string lastWaitingScene = string.Empty;
        private string lastSelectedObject = string.Empty;
        private int lastSelectedSaveSlot;
        private long lastReadyStatusVisualTick;
        private long lastWaitingStatusVisualTick;
        private long inputPhaseSequence;
        private int inputPhasePostBudget;
        private bool inputPhaseTriggered;
        private bool hooksRegistered;
        private string inputPhaseObservationError = string.Empty;
        private EventWaitHandle? inputSyncStart;
        private EventWaitHandle? inputSyncFrameReady;
        private EventWaitHandle? inputSyncApplied;
        private EventWaitHandle? randomSyncRequest;
        private EventWaitHandle? randomSyncApplied;
        private EventWaitHandle? recordingRandomSyncRequest;
        private EventWaitHandle? recordingRandomSyncApplied;
        private EventWaitHandle? clockPayloadReady;
        private EventWaitHandle? recordingArmRelease;
        private bool clockPayloadReadySignaled;
        private bool recordingArmBoundaryReached;
        private bool recordingArmReleased;
        private float recordingArmBoundaryTimeRaw;
        private float recordingArmBoundaryFixedTimeRaw;
        private int recordingArmBoundaryFrameCount;
        private int recordingArmBoundaryFramePhase;
        private bool inputSyncActive;
        private bool inputSyncPrimed;
        private bool inputSyncCaptureEnabled;
        private bool inputSyncHandshakeCompletedThisVisualUpdate;
        private int inputSyncPrimeCount;
        private bool randomSynchronizationApplied;
        private bool randomSynchronizationOriginCaptured;
        private bool recordingRandomSynchronizationApplied;
        private bool recordingRandomSynchronizationOriginCaptured;
        private int inputSyncFrameCount;
        private string externalClockProfileId = string.Empty;
        private string externalRngPayloadPolicyId = string.Empty;
        private bool externalRngSynchronizationRequested;
        private bool externalRecordingRngSynchronizationRequested;
        private bool externalRecordingRngSynchronizationApplied;
        private int externalRngResetCount;
        private int externalRngTransitionStartCount;
        private int externalRngGameplayReadyCount;
        private int externalRngFirstGameplayReadyFrameCount;
        private int externalRngFirstGameplayReadyFramePhase;
        private int externalRngGameplayReadyAlignmentHoldUpdateCount;
        private int externalRngSceneEpoch;
        private int externalRngLastAppliedSeed;
        private string externalRngLastBoundary = string.Empty;
        private string externalRngLastScene = string.Empty;
        private int externalRngFaultCode;
        private bool externalSceneRngPending;
        private bool externalSceneClockExclusionActive;
        private int externalSceneClockExclusionBeginCount;
        private int externalSceneClockExclusionFinishCount;
        private int externalSceneClockExclusionFrozenTimeUpdateCount;
        private int externalSceneClockExclusionFaultCode;
        private int externalSceneClockExclusionPreSynchronizationCount;
        private int externalSceneFramePhaseModulo;
        private int externalSceneFramePhaseTarget;
        private int externalRecordingRootRequestObservedFrameCount;
        private int externalRecordingRootFramePhase;
        private bool externalRecordingPhaseNormalizationActive;
        private bool externalRecordingPhaseNormalizationCompleted;
        private int externalRecordingPhaseNormalizationBeginCount;
        private int externalRecordingPhaseNormalizationHoldFrameCount;
        private int externalRecordingPhaseNormalizationReleaseCount;
        private int externalRecordingPhaseNormalizationLastFrameCount;
        private int externalRecordingPhaseNormalizationLastFramePhase;
        private int externalRecordingPhaseNormalizationHeldTimeBits;
        private long externalRecordingPhaseNormalizationHeldTimeDoubleBits;
        private int externalRecordingPhaseNormalizationRemainingNormalFrameCount;
        private int externalRecordingPhaseNormalizationRestoreFramePhase;
        private int externalRecordingPhaseNormalizationFaultCode;
        private int externalSceneActivationAlignmentBeginCount;
        private int externalSceneActivationAlignmentHoldFrameCount;
        private int externalSceneActivationAlignmentReleaseCount;
        private int externalSceneActivationAlignmentFirstFrameCount;
        private int externalSceneActivationAlignmentFirstFramePhase;
        private int externalSceneActivationAlignmentLastFrameCount;
        private int externalSceneActivationAlignmentLastFramePhase;
        private int externalSceneFinishAlignmentBeginCount;
        private int externalSceneFinishAlignmentHoldFrameCount;
        private int externalSceneFinishAlignmentReleaseCount;
        private int externalSceneFinishAlignmentLastFrameCount;
        private int externalSceneFinishAlignmentLastFramePhase;
        private int externalSceneFrameAlignmentFaultCode;
        private uint externalClockBridgeAbi;
        private int externalClockBridgeStatus;
        private bool externalRuntimeVirtualClockRegistered;
        private int externalVirtualClockPaused;
        private int externalVirtualClockPauseCount;
        private int externalVirtualClockResumePending;
        private int externalVirtualClockResumeRequestCount;
        private int externalVirtualClockResumeCount;
        private bool externalDeterministicClockEnabled;
        private long externalDeterministicClockFrequency;
        private long externalDeterministicClockStepTicks;
        private long externalDeterministicClockAnchor;
        private int externalDeterministicClockFrameAdvanceCount;
        private int externalDeterministicClockEnableFaultCode;
        private int externalDeterministicClockAdvanceFaultCode;
        private int externalDeterministicClockLastUnityFrameCount;
        private int externalDeterministicClockDuplicateTimeUpdateSkipCount;
        private int externalDeterministicClockSceneLoadFrameSkipCount;
        private int externalDeterministicClockUnityFrameFaultCode;
        private string externalRealtimeEpochNormalizationPolicyId =
            string.Empty;
        private bool externalRealtimeEpochNormalizationApplied;
        private int externalRealtimeEpochNormalizationCount;
        private long externalRealtimeEpochNormalizationGameTimeBits;
        private long externalRealtimeEpochNormalizationBeforeBits;
        private long externalRealtimeEpochNormalizationTargetBits;
        private long externalRealtimeEpochNormalizationAfterBits;
        private long externalRealtimeEpochNormalizationCanonicalOffsetSeconds;
        private long externalRealtimeEpochNormalizationDeltaTicks;
        private int externalRealtimeEpochNormalizationFaultCode;
        private bool externalDoublePhaseCalibrationApplied;
        private int externalDoublePhaseCalibrationAttempts;
        private long externalDoublePhaseInitialResidualBits;
        private long externalDoublePhaseFinalResidualBits;
        private int externalDoublePhaseDownwardQuantizationCount;
        private int externalDoublePhaseLastCorrectionBits;
        private int externalDoublePhaseCalibrationFaultCode;
        private bool externalStartupClockHookInstalled;
        private bool externalStartupClockLatchEnabled;
        private uint externalStartupClockHookThreadId;
        private int externalStartupClockVirtualQpcCallCount;
        private int externalStartupClockHandoffAdoptCount;
        private int externalStartupClockFaultCode;
        private bool externalTimeUpdateResumeBoundaryInstalled;
        private int externalTimeUpdateResumeBoundaryInstallCount;
        private int externalTimeUpdateResumeBoundaryCallbackCount;
        private int externalTimeUpdateResumeBoundaryCommitCount;
        private int externalTimeUpdateResumeCommitFaultCode;
        private int externalPlayerLoopPostLateUpdateIndex;
        private int externalPlayerLoopTimeUpdateIndex;
        private int externalPlayerLoopResumeBoundaryIndex;
        private int externalPlayerLoopWaitForPresentationIndex;
        private string externalPlayerLoopBoundaryError = string.Empty;
        private Type? externalClockControllerType;

        public ReferenceObserverSession(
            ReferenceObserverOptions options,
            Action<string> log,
            Action<string> logError)
        {
            this.options = options;
            this.log = log;
            this.logError = logError;
        }

        public void Start()
        {
            Directory.CreateDirectory(options.OutputDirectory);
            var syncPrefix =
                "HollowKnightTAS.T24.InputSync." + options.RunId;
            inputSyncStart = new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                syncPrefix + ".start");
            inputSyncFrameReady = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                syncPrefix + ".frame-ready");
            inputSyncApplied = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                syncPrefix + ".input-applied");
            var randomSyncPrefix =
                "HollowKnightTAS.T24.RngSync." + options.RunId;
            randomSyncRequest = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                randomSyncPrefix + ".request");
            randomSyncApplied = new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                randomSyncPrefix + ".applied");
            recordingRandomSyncRequest = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                randomSyncPrefix + ".recording-request");
            recordingRandomSyncApplied = new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                randomSyncPrefix + ".recording-applied");
            clockPayloadReady = new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                "HollowKnightTAS.T24.ClockPayloadReady." + options.RunId);
            recordingArmRelease = new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                "HollowKnightTAS.T24.RecordingArm." + options.RunId);
            WriteStatus("waiting-for-external-ui-load", string.Empty);

            On.InControl.PlayerActionSet.Update +=
                OnPlayerActionSetUpdate;
            On.InControl.InputManager.UpdateInternal +=
                OnInputManagerUpdateInternal;
            On.HutongGames.PlayMaker.Actions.ListenForRight.OnUpdate +=
                OnListenForRightUpdate;
            On.HutongGames.PlayMaker.Fsm.Event_FsmEvent +=
                OnFsmEvent;
            On.HutongGames.PlayMaker.Fsm.EnterState +=
                OnFsmEnterState;
            CompletedFrameBoundarySignal.Reached +=
                OnCompletedFrameBoundary;
            hooksRegistered = true;

            var early = new GameObject(
                "HollowKnightTAS.ReferenceObserver.Early");
            UnityEngine.Object.DontDestroyOnLoad(early);
            earlyRunner = early.AddComponent<ReferenceObserverEarlyRunner>();
            earlyRunner.Initialize(this);

            var late = new GameObject(
                "HollowKnightTAS.ReferenceObserver.Late");
            UnityEngine.Object.DontDestroyOnLoad(late);
            lateRunner = late.AddComponent<ReferenceObserverLateRunner>();
            lateRunner.Initialize(this);

            var completedFrame = new GameObject(
                "HollowKnightTAS.ReferenceObserver.CompletedFrame");
            UnityEngine.Object.DontDestroyOnLoad(completedFrame);
            completedFrameRunner = completedFrame.AddComponent<
                ReferenceObserverCompletedFrameRunner>();
            completedFrameRunner.Initialize(this);
            log(
                "T24 reference observer started runId="
                + options.RunId
                + " mode="
                + options.Mode);
        }

        public void OnEarlyUpdate()
        {
            if (complete)
            {
                return;
            }

            // Match RuntimePlaybackController's vanilla gameplay gate. Loading
            // duration is an asynchronous wall-clock pause, not a movie tick;
            // consuming input there makes identical no-Mod runs diverge solely
            // because one scene load happened to take more Unity updates.
            frameOpenAtUpdateBegin = IsReferenceGameplayTickOpen();
            inputSyncCaptureEnabled = false;
            inputSyncHandshakeCompletedThisVisualUpdate = false;
            if (!inputSyncActive
                && inputSyncStart?.WaitOne(0) == true)
            {
                inputSyncActive = true;
                SignalRandomRequest(0);
            }
            if (!randomSynchronizationApplied
                && randomSyncApplied?.WaitOne(0) == true)
            {
                try
                {
                    CaptureExternalRandomSynchronizationOrigin();
                }
                catch (Exception exception)
                {
                    Complete(
                        false,
                        "external-rng-origin-capture-failed: "
                        + exception.GetType().Name
                        + ": "
                        + exception.Message);
                    return;
                }
            }
            TryCaptureExternalRecordingRandomSynchronizationOrigin();
            visualTick++;
        }

        private void OnInputManagerUpdateInternal(
            On.InControl.InputManager.orig_UpdateInternal original)
        {
            if (!complete
                && inputSyncActive
                && ready
                && randomSynchronizationApplied
                && frameOpenAtUpdateBegin
                && !inputSyncHandshakeCompletedThisVisualUpdate
                && frames.Count < options.MaxTicks)
            {
                inputSyncHandshakeCompletedThisVisualUpdate = true;
                var captureThisFrame = inputSyncPrimed;
                inputSyncFrameReady?.Set();
                if (inputSyncApplied == null
                    || !inputSyncApplied.WaitOne(TimeSpan.FromSeconds(10)))
                {
                    Complete(false, "external-input-sync-timeout");
                }
                else if (captureThisFrame)
                {
                    inputSyncCaptureEnabled = true;
                    inputSyncFrameCount++;
                }
                else
                {
                    inputSyncPrimed = true;
                    inputSyncPrimeCount++;
                }
            }

            original();
        }

        private bool IsReferenceGameplayTickOpen()
        {
            var manager = GameManager.instance;
            var hero = HeroController.SilentInstance;
            var input = InputHandler.Instance;
            var scene = USceneManager.GetActiveScene();
            var sceneName = scene.name ?? string.Empty;

            if (recording
                && !string.IsNullOrEmpty(lastGameplaySceneName)
                && !string.Equals(
                    lastGameplaySceneName,
                    sceneName,
                    StringComparison.Ordinal))
            {
                sceneInputWarmupUpdates = 2;
            }
            lastGameplaySceneName = sceneName;

            if (Time.timeScale <= 0f
                || manager == null
                || manager.gameState != GameState.PLAYING
                || manager.IsInSceneTransition
                || hero == null
                || !hero.gameObject.activeInHierarchy
                || input == null
                || input.inputActions == null
                || !scene.IsValid()
                || !scene.isLoaded
                || string.IsNullOrEmpty(sceneName)
                || hero.cState.transitioning)
            {
                return false;
            }

            if (!hero.acceptingInput)
            {
                var animation =
                    hero.GetComponent<HeroAnimationController>();
                if (string.Equals(
                    animation?.animator?.CurrentClip?.name,
                    "Challenge Start",
                    StringComparison.Ordinal))
                {
                    return false;
                }
            }

            if (sceneInputWarmupUpdates > 0)
            {
                sceneInputWarmupUpdates--;
                return false;
            }

            return true;
        }

        public void OnEarlyFixedUpdate()
        {
            if (!complete)
            {
                fixedTick++;
            }
        }

        public void OnLateUpdate()
        {
            if (complete)
            {
                return;
            }

            try
            {
                if (!clockPayloadReadySignaled
                    && string.Equals(
                        USceneManager.GetActiveScene().name,
                        "Menu_Title",
                        StringComparison.Ordinal))
                {
                    clockPayloadReady?.Set();
                    clockPayloadReadySignaled = true;
                }
                if (readinessCompletionPending)
                {
                    Complete(
                        false,
                        "completed-frame-boundary-missing-before-next-readiness-update");
                    return;
                }
                if (!ready)
                {
                    ObserveFixtureReadiness();
                    if (!ready
                        && visualTick - lastWaitingStatusVisualTick >= 10)
                    {
                        lastWaitingStatusVisualTick = visualTick;
                        WriteStatus(
                            "waiting-for-external-ui-load",
                            string.Empty);
                    }
                    return;
                }
                if (!frameOpenAtUpdateBegin)
                {
                    return;
                }
                var actions = InputHandler.Instance?.inputActions;
                var benchFsm = FindFixtureBenchFsm();
                if (actions != null && benchFsm != null)
                {
                    TryObserveInputPhase(
                        "Observer.LateUpdate",
                        actions,
                        actions.UpdateTick,
                        benchFsm.GameObject?.name ?? string.Empty,
                        benchFsm.Name ?? string.Empty,
                        benchFsm.ActiveStateName ?? string.Empty);
                }
                if (!recording)
                {
                    if (!randomSynchronizationApplied
                        && randomSyncApplied?.WaitOne(0) == true)
                    {
                        CaptureExternalRandomSynchronizationOrigin();
                    }
                    if (!randomSynchronizationApplied)
                    {
                        if (visualTick - lastReadyStatusVisualTick >= 10)
                        {
                            lastReadyStatusVisualTick = visualTick;
                            WriteStatus(
                                "waiting-for-external-rng-sync",
                                string.Empty);
                        }
                        return;
                    }
                    if (!recordingArmReleased)
                    {
                        if (Time.time < RequiredRecordingAbsoluteTimeTarget)
                        {
                            if (visualTick - lastReadyStatusVisualTick >= 10)
                            {
                                lastReadyStatusVisualTick = visualTick;
                                WriteStatus(
                                    "waiting-for-recording-time-target",
                                    string.Empty);
                            }
                            return;
                        }
                        if (!recordingArmBoundaryReached)
                        {
                            if (Time.time > RequiredRecordingAbsoluteTimeTarget)
                            {
                                Complete(
                                    false,
                                    "recording-absolute-time-target-missed");
                                return;
                            }
                            if (Time.fixedTime
                                != RequiredRecordingAbsoluteTimeTarget)
                            {
                                Complete(
                                    false,
                                    "recording-fixed-time-target-mismatch");
                                return;
                            }
                            var framePhase = PositiveModulo(
                                Time.frameCount,
                                RequiredRecordingFramePhaseModulo);
                            if (framePhase != RequiredRecordingFramePhase)
                            {
                                Complete(
                                    false,
                                    "recording-frame-phase-normalization-failed");
                                return;
                            }
                            recordingArmBoundaryTimeRaw = Time.time;
                            recordingArmBoundaryFixedTimeRaw = Time.fixedTime;
                            recordingArmBoundaryFrameCount = Time.frameCount;
                            recordingArmBoundaryFramePhase = framePhase;
                            recordingArmBoundaryReached = true;
                            SignalRandomRequest(1);
                            WriteStatus(
                                "waiting-for-recording-arm-release",
                                string.Empty);
                        }
                        if (recordingArmRelease == null
                            || !recordingArmRelease.WaitOne(
                                TimeSpan.FromSeconds(120)))
                        {
                            Complete(false, "recording-arm-release-timeout");
                            return;
                        }
                        recordingArmReleased = true;
                        return;
                    }
                    if (!TryCaptureExternalRecordingRandomSynchronizationOrigin())
                    {
                        if (visualTick - lastReadyStatusVisualTick >= 10)
                        {
                            lastReadyStatusVisualTick = visualTick;
                            WriteStatus(
                                "waiting-for-recording-rng-sync",
                                string.Empty);
                        }
                        return;
                    }
                    // A synchronized OS-keyboard run primes logical tick 0
                    // before setting the named start event. The two operations
                    // can straddle EarlyUpdate. Do not let LateUpdate infer the
                    // start from the visible key and capture an unacknowledged
                    // first frame; every recorded frame must follow the named
                    // frame-ready/input-applied handshake.
                    if (options.RequireExternalInputSync
                        && (!inputSyncActive || !inputSyncCaptureEnabled))
                    {
                        if (visualTick - lastReadyStatusVisualTick >= 10)
                        {
                            lastReadyStatusVisualTick = visualTick;
                            WriteStatus("ready-for-input", string.Empty);
                        }
                        return;
                    }
                    if (!inputSyncActive
                        && !VanillaEquivalenceSampler.HasGameplayInput())
                    {
                        if (visualTick - lastReadyStatusVisualTick >= 10)
                        {
                            lastReadyStatusVisualTick = visualTick;
                            WriteStatus("ready-for-input", string.Empty);
                        }
                        return;
                    }
                    BeginRecording();
                }

                ArmCompletedFrameCapture(
                    VanillaEquivalenceSampler.CaptureInput(actions!));
            }
            catch (Exception exception)
            {
                logError("T24 reference observer fault: " + exception);
                Complete(
                    false,
                    exception.GetType().Name + ": " + exception.Message);
            }
        }

        public void OnApplicationQuit()
        {
            Complete(
                false,
                "process-exit-before-complete");
        }

        private void ObserveFixtureReadiness()
        {
            var currentScene =
                USceneManager.GetActiveScene().name ?? string.Empty;
            CaptureMenuSelection(
                out var selectedObject,
                out var selectedSaveSlot);
            var menuInputEdge = HasMenuInputEdge();
            if (!string.Equals(
                    currentScene,
                    lastWaitingScene,
                    StringComparison.Ordinal)
                || !string.Equals(
                    selectedObject,
                    lastSelectedObject,
                    StringComparison.Ordinal)
                || selectedSaveSlot != lastSelectedSaveSlot
                || menuInputEdge)
            {
                lastWaitingScene = currentScene;
                lastSelectedObject = selectedObject;
                lastSelectedSaveSlot = selectedSaveSlot;
                lastWaitingStatusVisualTick = visualTick;
                WriteStatus("waiting-for-external-ui-load", string.Empty);
            }
            var hero = HeroController.SilentInstance;
            var manager = GameManager.instance;
            var player = PlayerData.instance;
            var benchFsm = FindFixtureBenchFsm();
            var sleeping =
                benchFsm?.Variables.FindFsmBool("Sleeping");
            var animation = hero?.GetComponent<HeroAnimationController>();
            var rigidbody = hero?.GetComponent<Rigidbody2D>();
            var fixedRemainder = Time.time - Time.fixedTime;
            string blocker;
            if (hero == null)
            {
                blocker = "hero-unavailable";
            }
            else if (manager == null)
            {
                blocker = "game-manager-unavailable";
            }
            else if (player == null)
            {
                blocker = "player-data-unavailable";
            }
            else if (benchFsm == null)
            {
                blocker = "bench-fsm-unavailable";
            }
            else if (animation?.animator == null)
            {
                blocker = "hero-animation-unavailable";
            }
            else if (rigidbody == null)
            {
                blocker = "hero-rigidbody-unavailable";
            }
            else if (manager.gameState != GameState.PLAYING)
            {
                blocker = "game-not-playing";
            }
            else if (manager.IsInSceneTransition)
            {
                blocker = "scene-transition";
            }
            else if (!hero.gameObject.activeInHierarchy)
            {
                blocker = "hero-inactive";
            }
            else if (!string.Equals(
                         currentScene,
                         "GG_Workshop",
                         StringComparison.Ordinal))
            {
                blocker = "wrong-scene";
            }
            else if (!player.atBench)
            {
                blocker = "not-at-bench";
            }
            else if (!string.Equals(
                         benchFsm.ActiveStateName,
                         "Resting",
                         StringComparison.Ordinal))
            {
                blocker = "bench-not-resting";
            }
            else if (!string.Equals(
                         animation.animator.CurrentClip?.name,
                         "Sit Fall Asleep",
                         StringComparison.Ordinal))
            {
                blocker = "wrong-animation";
            }
            else if (animation.animator.CurrentFrame != 2)
            {
                blocker = "wrong-animation-frame";
            }
            else if (sleeping?.Value != true)
            {
                blocker = "bench-not-sleeping";
            }
            else if (fixedRemainder != 0f)
            {
                blocker = "fixed-phase-not-zero";
            }
            else if (Time.time < RequiredAbsoluteTimeTarget)
            {
                blocker = "waiting-for-absolute-time-target";
            }
            else if (!hasReadinessFixedTick
                     && Time.time
                        > RequiredAbsoluteTimeTarget
                          + Time.fixedDeltaTime
                            * (RequiredSemanticStableUpdates + 3))
            {
                blocker = "absolute-time-target-missed";
            }
            else if (VanillaEquivalenceSampler.HasGameplayInput())
            {
                blocker = "gameplay-input-active";
            }
            else if (hero.acceptingInput)
            {
                blocker = "hero-accepting-input";
            }
            else if (!hero.controlReqlinquished)
            {
                blocker = "hero-control-not-relinquished";
            }
            else if (animation.controlEnabled)
            {
                blocker = "hero-animation-control-enabled";
            }
            else if (hero.hero_state != ActorStates.airborne)
            {
                blocker = "wrong-hero-actor-state";
            }
            else if (!rigidbody.simulated)
            {
                blocker = "rigidbody-not-simulated";
            }
            else if (rigidbody.bodyType != RigidbodyType2D.Dynamic)
            {
                blocker = "wrong-rigidbody-body-type";
            }
            else if (rigidbody.velocity != Vector2.zero)
            {
                blocker = "rigidbody-moving";
            }
            else
            {
                blocker = string.Empty;
            }
            if (!string.IsNullOrEmpty(blocker))
            {
                readinessBlocker = blocker;
                readinessFixedSteps = 0;
                stableUpdates = 0;
                hasReadinessFixedTick = false;
                return;
            }
            if (hero == null
                || player == null
                || benchFsm == null
                || animation?.animator == null
                || rigidbody == null)
            {
                throw new InvalidOperationException(
                    "Fixture readiness passed with a missing component.");
            }

            if (!hasReadinessFixedTick)
            {
                readinessBlocker = "establishing-fixed-tick";
                readinessFixedSteps = 0;
                readinessFixedTick = fixedTick;
                hasReadinessFixedTick = true;
                stableUpdates = 0;
                return;
            }
            var fixedSteps = fixedTick - readinessFixedTick;
            readinessFixedSteps = fixedSteps;
            readinessFixedTick = fixedTick;
            if (fixedSteps != 1)
            {
                readinessBlocker = "fixed-step-count";
                stableUpdates = 0;
                return;
            }

            stableUpdates++;
            if (stableUpdates < RequiredSemanticStableUpdates)
            {
                readinessBlocker = "stabilizing-semantic-fixture";
                return;
            }

            pendingReadinessHero = hero;
            pendingReadinessPlayer = player;
            pendingReadinessBenchFsm = benchFsm;
            pendingReadinessAnimation = animation;
            pendingReadinessRigidbody = rigidbody;
            readinessCompletionPending = true;
            readinessBlocker = "awaiting-completed-frame-boundary";
        }

        private void OnCompletedFrameBoundary()
        {
            if (complete)
            {
                return;
            }

            try
            {
                if (readinessCompletionPending)
                {
                    var hero = pendingReadinessHero;
                    var player = pendingReadinessPlayer;
                    var benchFsm = pendingReadinessBenchFsm;
                    var animation = pendingReadinessAnimation;
                    var rigidbody = pendingReadinessRigidbody;
                    readinessCompletionPending = false;
                    pendingReadinessHero = null;
                    pendingReadinessPlayer = null;
                    pendingReadinessBenchFsm = null;
                    pendingReadinessAnimation = null;
                    pendingReadinessRigidbody = null;
                    if (hero == null
                        || player == null
                        || benchFsm == null
                        || animation?.animator == null
                        || rigidbody == null)
                    {
                        throw new InvalidOperationException(
                            "Completed-frame readiness lost a required component.");
                    }

                    WriteBaseline(
                        hero,
                        player,
                        benchFsm,
                        animation,
                        rigidbody);
                    baselineCaptured = true;
                    ready = true;
                    readinessBlocker = string.Empty;
                    lastReadyStatusVisualTick = visualTick;
                    // RNG synchronization defines logical tick zero for both
                    // unsynchronized physical input and frame-handshaked
                    // input. Request it before advertising that input is safe;
                    // otherwise a fast physical edge can reach LateUpdate
                    // before the clock payload has acknowledged the snapshot.
                    SignalRandomRequest(0);
                    WriteStatus(
                        "waiting-for-external-rng-sync",
                        string.Empty);
                }

                if (!frameCapturePending)
                {
                    return;
                }

                var captureVisualTick = pendingCaptureVisualTick;
                var captureFixedTick = pendingCaptureFixedTick;
                var captureInputSample = pendingCaptureInputSample
                    ?? throw new InvalidOperationException(
                        "Committed Hero input sample is unavailable.");
                frameCapturePending = false;
                pendingCaptureInputSample = null;
                frames.Add(
                    sampler.Capture(
                        frames.Count,
                        captureVisualTick,
                        captureFixedTick,
                        captureInputSample));
                if (frames.Count % 10 == 0)
                {
                    WriteStatus("recording", string.Empty);
                }
                if (frames.Count >= options.MaxTicks)
                {
                    Complete(true, string.Empty);
                }
            }
            catch (Exception exception)
            {
                logError(
                    "T24 completed-frame observer fault: " + exception);
                Complete(
                    false,
                    exception.GetType().Name + ": " + exception.Message);
            }
        }

        private void WriteBaseline(
            HeroController hero,
            PlayerData player,
            HutongGames.PlayMaker.Fsm benchFsm,
            HeroAnimationController animation,
            Rigidbody2D rigidbody)
        {
            var animator = animation.animator;
            var position = hero.transform.position;
            var bodyPosition = rigidbody.position;
            var velocity = rigidbody.velocity;
            var timeRaw = Time.time;
            var fixedTimeRaw = Time.fixedTime;
            var builder = new StringBuilder(1024);
            builder.Append("{\"schemaVersion\":2,\"runId\":");
            AppendString(builder, options.RunId);
            builder.Append(",\"mode\":");
            AppendString(builder, options.Mode);
            builder.Append(",\"samplingBoundary\":");
            AppendString(
                builder,
                CompletedFrameBoundarySignal.BoundaryId);
            builder.Append(",\"fixture\":\"GG_Workshop.RestBench (1)\"");
            builder.Append(",\"fixtureReadinessBoundary\":\"semantic-idle\"");
            builder.Append(",\"fixtureReadinessRequiredUpdates\":");
            builder.Append(
                RequiredSemanticStableUpdates.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"fixtureReadinessAbsoluteTimeTarget\":");
            AppendFloat32(builder, RequiredAbsoluteTimeTarget);
            builder.Append(",\"recordingAbsoluteTimeTarget\":");
            AppendFloat32(builder, RequiredRecordingAbsoluteTimeTarget);
            builder.Append(",\"visualTick\":");
            builder.Append(
                visualTick.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"fixedTick\":");
            builder.Append(
                fixedTick.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"scene\":\"GG_Workshop\"");
            builder.Append(",\"gameState\":\"PLAYING\"");
            builder.Append(",\"sceneTransition\":false");
            builder.Append(",\"atBench\":");
            builder.Append(player.atBench ? "true" : "false");
            builder.Append(",\"benchFsmState\":");
            AppendString(builder, benchFsm.ActiveStateName ?? string.Empty);
            builder.Append(",\"benchSleeping\":");
            builder.Append(
                benchFsm.Variables.FindFsmBool("Sleeping")?.Value == true
                    ? "true"
                    : "false");
            builder.Append(",\"gameplayInputActive\":");
            builder.Append(
                VanillaEquivalenceSampler.HasGameplayInput()
                    ? "true"
                    : "false");
            builder.Append(",\"heroActorState\":");
            AppendString(
                builder,
                hero!.hero_state.ToString());
            builder.Append(",\"heroAcceptingInput\":");
            builder.Append(hero.acceptingInput ? "true" : "false");
            builder.Append(",\"heroControlRelinquished\":");
            builder.Append(
                hero.controlReqlinquished ? "true" : "false");
            builder.Append(",\"heroAnimationControlEnabled\":");
            builder.Append(
                animation.controlEnabled ? "true" : "false");
            builder.Append(",\"heroAnimationClip\":");
            AppendString(
                builder,
                animator.CurrentClip?.name ?? string.Empty);
            builder.Append(",\"heroAnimationFrame\":");
            builder.Append(
                animator.CurrentFrame.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"heroPositionX\":");
            AppendFloat32(builder, position.x);
            builder.Append(",\"heroPositionY\":");
            AppendFloat32(builder, position.y);
            builder.Append(",\"heroPositionZ\":");
            AppendFloat32(builder, position.z);
            builder.Append(",\"rigidbodyPositionX\":");
            AppendFloat32(builder, bodyPosition.x);
            builder.Append(",\"rigidbodyPositionY\":");
            AppendFloat32(builder, bodyPosition.y);
            builder.Append(",\"rigidbodyVelocityX\":");
            AppendFloat32(builder, velocity.x);
            builder.Append(",\"rigidbodyVelocityY\":");
            AppendFloat32(builder, velocity.y);
            builder.Append(",\"heroFacingScaleX\":");
            AppendFloat32(builder, hero.transform.localScale.x);
            builder.Append(",\"rigidbodySimulated\":");
            builder.Append(rigidbody.simulated ? "true" : "false");
            builder.Append(",\"rigidbodyBodyType\":");
            AppendString(builder, rigidbody.bodyType.ToString());
            builder.Append(",\"targetFrameRate\":");
            builder.Append(
                Application.targetFrameRate.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"vSyncCount\":");
            builder.Append(
                QualitySettings.vSyncCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"deltaTime\":");
            AppendFloat32(builder, Time.deltaTime);
            builder.Append(",\"fixedDeltaTime\":");
            AppendFloat32(builder, Time.fixedDeltaTime);
            builder.Append(",\"captureDeltaTime\":");
            AppendFloat32(builder, Time.captureDeltaTime);
            builder.Append(",\"timeRaw\":");
            AppendFloat32(builder, timeRaw);
            builder.Append(",\"fixedTimeRaw\":");
            AppendFloat32(builder, fixedTimeRaw);
            builder.Append(",\"timeMinusFixed\":");
            AppendFloat32(builder, timeRaw - fixedTimeRaw);
            builder.Append('}');
            WriteAtomic(
                Path.Combine(options.OutputDirectory, "baseline.json"),
                builder.ToString());
        }

        private void CaptureExternalRandomSynchronizationOrigin()
        {
            var controller = TryResolveExternalClockControllerType();
            if (controller == null)
            {
                throw new InvalidOperationException(
                    "External clock payload type is unavailable at RNG ack.");
            }
            if (!ReadStaticBoolean(
                    controller,
                    "RandomSynchronizationSnapshotAvailable"))
            {
                throw new InvalidOperationException(
                    "External clock payload acknowledged RNG without a snapshot.");
            }

            sampler.SetUnityRandomSynchronizationOrigin(
                ReadStaticInt32(controller, "RandomSynchronizationStateS0"),
                ReadStaticInt32(controller, "RandomSynchronizationStateS1"),
                ReadStaticInt32(controller, "RandomSynchronizationStateS2"),
                ReadStaticInt32(controller, "RandomSynchronizationStateS3"));
            externalClockControllerType = controller;
            RefreshExternalClockTelemetry(
                refreshDoublePhaseCalibration: true);
            if (!string.Equals(
                    externalClockProfileId,
                    ExternalClockProfile,
                    StringComparison.Ordinal)
                || !string.Equals(
                    externalRngPayloadPolicyId,
                    ExternalRandomSynchronizationPolicy,
                    StringComparison.Ordinal)
                || !externalRngSynchronizationRequested
                || externalRngResetCount != 1
                || externalRngTransitionStartCount != 0
                || externalRngGameplayReadyCount != 0
                || externalRngSceneEpoch != 0
                || externalRngLastAppliedSeed
                    != ExternalRandomSynchronizationSeed
                || !string.Equals(
                    externalRngLastBoundary,
                    "root",
                    StringComparison.Ordinal)
                || string.IsNullOrEmpty(externalRngLastScene)
                || externalRngFaultCode != 0
                || externalSceneRngPending
                || externalSceneClockExclusionActive
                || externalSceneClockExclusionPreSynchronizationCount != 0
                || externalSceneClockExclusionBeginCount
                    != externalSceneClockExclusionPreSynchronizationCount
                || externalSceneClockExclusionFinishCount
                    != externalSceneClockExclusionPreSynchronizationCount
                || externalSceneClockExclusionFrozenTimeUpdateCount != 0
                || externalSceneClockExclusionFaultCode != 0
                || externalSceneFramePhaseModulo != 4
                || externalSceneFramePhaseTarget != 0
                || externalRecordingRootRequestObservedFrameCount != -1
                || externalRecordingRootFramePhase != -1
                || externalRecordingPhaseNormalizationActive
                || externalRecordingPhaseNormalizationCompleted
                || externalRecordingPhaseNormalizationBeginCount != 0
                || externalRecordingPhaseNormalizationHoldFrameCount != 0
                || externalRecordingPhaseNormalizationReleaseCount != 0
                || externalRecordingPhaseNormalizationLastFrameCount != -1
                || externalRecordingPhaseNormalizationLastFramePhase != -1
                || externalRecordingPhaseNormalizationHeldTimeBits != 0
                || externalRecordingPhaseNormalizationHeldTimeDoubleBits != 0L
                || externalRecordingPhaseNormalizationRemainingNormalFrameCount
                    != -1
                || externalRecordingPhaseNormalizationRestoreFramePhase != -1
                || externalRecordingPhaseNormalizationFaultCode != 0
                || externalSceneActivationAlignmentBeginCount != 0
                || externalSceneActivationAlignmentReleaseCount != 0
                || externalSceneFinishAlignmentBeginCount != 0
                || externalSceneFinishAlignmentReleaseCount != 0
                || externalSceneFrameAlignmentFaultCode != 0
                || externalClockBridgeAbi != 10u
                || externalClockBridgeStatus != 2
                || !externalDeterministicClockEnabled
                || externalDeterministicClockFrequency <= 0
                || externalDeterministicClockStepTicks <= 0
                || externalDeterministicClockFrequency
                    % externalDeterministicClockStepTicks != 0
                || externalDeterministicClockFrequency
                    / externalDeterministicClockStepTicks != 50
                || externalDeterministicClockAnchor <= 0
                || externalDeterministicClockFrameAdvanceCount <= 0
                || externalDeterministicClockEnableFaultCode != 0
                || externalDeterministicClockAdvanceFaultCode != 0
                || externalDeterministicClockLastUnityFrameCount <= 0
                || externalDeterministicClockSceneLoadFrameSkipCount != 0
                || externalDeterministicClockUnityFrameFaultCode != 0
                || !string.Equals(
                    externalRealtimeEpochNormalizationPolicyId,
                    "root-game-minus-rounded-startup-offset-qpc-grid-v3",
                    StringComparison.Ordinal)
                || !externalRealtimeEpochNormalizationApplied
                || externalRealtimeEpochNormalizationCount <= 0
                || externalRealtimeEpochNormalizationGameTimeBits == 0L
                || externalRealtimeEpochNormalizationBeforeBits == 0L
                || externalRealtimeEpochNormalizationTargetBits == 0L
                || externalRealtimeEpochNormalizationAfterBits
                    != externalRealtimeEpochNormalizationTargetBits
                || externalRealtimeEpochNormalizationFaultCode != 0
                || !externalDoublePhaseCalibrationApplied
                || externalDoublePhaseCalibrationAttempts <= 0
                || externalDoublePhaseCalibrationFaultCode != 0
                || externalDoublePhaseFinalResidualBits != 0L
                || !externalStartupClockHookInstalled
                || !externalStartupClockLatchEnabled
                || externalStartupClockHookThreadId == 0u
                || externalStartupClockVirtualQpcCallCount <= 0
                || externalStartupClockHandoffAdoptCount != 1
                || externalStartupClockFaultCode != 0
                || !externalTimeUpdateResumeBoundaryInstalled
                || externalTimeUpdateResumeBoundaryInstallCount != 1
                || externalTimeUpdateResumeBoundaryCallbackCount <= 0
                || externalTimeUpdateResumeCommitFaultCode != 0
                || externalPlayerLoopPostLateUpdateIndex < 0
                || externalPlayerLoopTimeUpdateIndex < 0
                || externalPlayerLoopResumeBoundaryIndex < 0
                || externalPlayerLoopWaitForPresentationIndex
                    != externalPlayerLoopResumeBoundaryIndex + 1
                || !string.IsNullOrEmpty(externalPlayerLoopBoundaryError)
                || (options.Mode != "vanilla-reference"
                    && !externalRuntimeVirtualClockRegistered))
            {
                throw new InvalidOperationException(
                    "External virtual-clock bridge registration is invalid.");
            }
            randomSynchronizationOriginCaptured = true;
            randomSynchronizationApplied = true;
        }

        private bool TryCaptureExternalRecordingRandomSynchronizationOrigin()
        {
            if (recordingRandomSynchronizationApplied)
            {
                return true;
            }
            if (recordingRandomSyncApplied?.WaitOne(0) != true)
            {
                return false;
            }

            try
            {
                var controller = TryResolveExternalClockControllerType();
                if (controller == null)
                {
                    throw new InvalidOperationException(
                        "External clock payload type is unavailable at "
                        + "recording RNG ack.");
                }
                if (!ReadStaticBoolean(
                        controller,
                        "RandomSynchronizationSnapshotAvailable"))
                {
                    throw new InvalidOperationException(
                        "External clock payload acknowledged recording RNG "
                        + "without a snapshot.");
                }

                sampler.SetUnityRandomSynchronizationOrigin(
                    ReadStaticInt32(
                        controller,
                        "RandomSynchronizationStateS0"),
                    ReadStaticInt32(
                        controller,
                        "RandomSynchronizationStateS1"),
                    ReadStaticInt32(
                        controller,
                        "RandomSynchronizationStateS2"),
                    ReadStaticInt32(
                        controller,
                        "RandomSynchronizationStateS3"));
                RefreshExternalClockTelemetry(
                    refreshDoublePhaseCalibration: false);
                if (!externalRecordingRngSynchronizationRequested
                    || !externalRecordingRngSynchronizationApplied
                    || externalRngResetCount != 2
                    || externalRngTransitionStartCount != 0
                    || externalRngGameplayReadyCount != 0
                    || externalRngSceneEpoch != 0
                    || externalRngLastAppliedSeed
                        != ExternalRandomSynchronizationSeed
                    || !string.Equals(
                        externalRngLastBoundary,
                        "recording-root",
                        StringComparison.Ordinal)
                    || !string.Equals(
                        externalRngLastScene,
                        "GG_Workshop",
                        StringComparison.Ordinal)
                    || externalRngFaultCode != 0
                    || externalSceneFramePhaseModulo != 4
                    || externalSceneFramePhaseTarget != 0
                    || externalRecordingRootRequestObservedFrameCount
                        != recordingArmBoundaryFrameCount + 1
                    || externalRecordingRootFramePhase != 0
                    || externalRecordingPhaseNormalizationActive
                    || !externalRecordingPhaseNormalizationCompleted
                    || externalRecordingPhaseNormalizationBeginCount != 1
                    || externalRecordingPhaseNormalizationHoldFrameCount < 1
                    || externalRecordingPhaseNormalizationHoldFrameCount > 8
                    || externalRecordingPhaseNormalizationReleaseCount != 1
                    || externalRecordingPhaseNormalizationFaultCode != 0
                    || externalRecordingPhaseNormalizationLastFrameCount <= 0
                    || externalRecordingPhaseNormalizationHeldTimeBits == 0
                    || externalRecordingPhaseNormalizationHeldTimeDoubleBits == 0L
                    || externalRecordingPhaseNormalizationRemainingNormalFrameCount
                        < 1
                    || externalRecordingPhaseNormalizationRemainingNormalFrameCount
                        > 16
                    || externalRecordingPhaseNormalizationRestoreFramePhase < 0
                    || externalRecordingPhaseNormalizationRestoreFramePhase >= 4
                    || externalRecordingPhaseNormalizationLastFramePhase
                        != externalRecordingPhaseNormalizationRestoreFramePhase
                    || externalSceneActivationAlignmentBeginCount != 0
                    || externalSceneActivationAlignmentReleaseCount != 0
                    || externalSceneFinishAlignmentBeginCount != 0
                    || externalSceneFinishAlignmentReleaseCount != 0
                    || externalSceneFrameAlignmentFaultCode != 0)
                {
                    throw new InvalidOperationException(
                        "External recording RNG synchronization is invalid.");
                }

                recordingRandomSynchronizationOriginCaptured = true;
                recordingRandomSynchronizationApplied = true;
                return true;
            }
            catch (Exception exception)
            {
                Complete(
                    false,
                    "external-recording-rng-origin-capture-failed: "
                    + exception.GetType().Name
                    + ": "
                    + exception.Message);
                return false;
            }
        }

        private void SignalRandomRequest(int kind)
        {
            // Preserve the original disposed/null sender behavior. Actual
            // requests go through the same authority as Runtime full runs.
            if ((kind == 0 ? randomSyncRequest : recordingRandomSyncRequest) == null) return;
            var matches = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name == "HollowKnightTAS.ClockPayload").ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Actual random producer payload is absent or ambiguous.");
            var controller = TryResolveExternalClockControllerType();
            if (controller == null || controller.Assembly != matches[0]) throw new InvalidOperationException("Actual random producer payload identity changed.");
            var abi = controller.GetMethod("GetResidentRandomProducerAbi", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            var request = controller.GetMethod("RequestResidentRandom", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null);
            if (abi == null || abi.ReturnType != typeof(int) || request == null || request.ReturnType != typeof(void)
                || (int)abi.Invoke(null, null)! != 1) throw new InvalidOperationException("Update ClockPayload: shared random producer admission is unavailable.");
            request.Invoke(null, new object[] { kind });
        }

        private Type? TryResolveExternalClockControllerType()
        {
            if (externalClockControllerType != null)
            {
                return externalClockControllerType;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.Equals(
                        assembly.GetName().Name,
                        "HollowKnightTAS.ClockPayload",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                externalClockControllerType = assembly.GetType(
                    "HollowKnightTAS.ClockPayload.ClockController",
                    throwOnError: false,
                    ignoreCase: false);
                break;
            }
            return externalClockControllerType;
        }

        private static bool ReadStaticBoolean(Type? type, string propertyName)
        {
            return (bool)ReadStaticProperty(type, propertyName);
        }

        private static int ReadStaticInt32(Type? type, string propertyName)
        {
            return (int)ReadStaticProperty(type, propertyName);
        }

        private static long ReadStaticInt64(Type? type, string propertyName)
        {
            return (long)ReadStaticProperty(type, propertyName);
        }

        private static uint ReadStaticUInt32(Type? type, string propertyName)
        {
            return (uint)ReadStaticProperty(type, propertyName);
        }

        private static string ReadStaticString(Type? type, string propertyName)
        {
            return (string)ReadStaticProperty(type, propertyName);
        }

        private void RefreshExternalClockTelemetry(
            bool refreshDoublePhaseCalibration)
        {
            if (TryResolveExternalClockControllerType() == null)
            {
                return;
            }

            externalClockBridgeAbi = ReadStaticUInt32(
                externalClockControllerType,
                "BridgeAbi");
            externalClockProfileId = ReadStaticString(
                externalClockControllerType,
                "ActiveProfileId");
            externalRngPayloadPolicyId = ReadStaticString(
                externalClockControllerType,
                "ActiveRandomSynchronizationPolicyId");
            externalRngSynchronizationRequested = ReadStaticBoolean(
                externalClockControllerType,
                "RandomSynchronizationRequested");
            externalRecordingRngSynchronizationRequested = ReadStaticBoolean(
                externalClockControllerType,
                "RecordingRandomSynchronizationRequested");
            externalRecordingRngSynchronizationApplied = ReadStaticBoolean(
                externalClockControllerType,
                "RecordingRandomSynchronizationApplied");
            externalRngResetCount = ReadStaticInt32(
                externalClockControllerType,
                "RandomSynchronizationResetCount");
            externalRngTransitionStartCount = ReadStaticInt32(
                externalClockControllerType,
                "RandomSynchronizationTransitionStartCount");
            externalRngGameplayReadyCount = ReadStaticInt32(
                externalClockControllerType,
                "RandomSynchronizationGameplayReadyCount");
            externalRngFirstGameplayReadyFrameCount = ReadStaticInt32(
                externalClockControllerType,
                "RandomSynchronizationFirstGameplayReadyFrameCount");
            externalRngFirstGameplayReadyFramePhase = ReadStaticInt32(
                externalClockControllerType,
                "RandomSynchronizationFirstGameplayReadyFramePhase");
            externalRngGameplayReadyAlignmentHoldUpdateCount =
                ReadStaticInt32(
                    externalClockControllerType,
                    "RandomSynchronizationGameplayReadyAlignmentHoldUpdateCount");
            externalRngSceneEpoch = ReadStaticInt32(
                externalClockControllerType,
                "RandomSynchronizationSceneEpoch");
            externalRngLastAppliedSeed = ReadStaticInt32(
                externalClockControllerType,
                "RandomSynchronizationLastAppliedSeed");
            externalRngLastBoundary = ReadStaticString(
                externalClockControllerType,
                "RandomSynchronizationLastBoundary");
            externalRngLastScene = ReadStaticString(
                externalClockControllerType,
                "RandomSynchronizationLastScene");
            externalRngFaultCode = ReadStaticInt32(
                externalClockControllerType,
                "RandomSynchronizationFaultCode");
            externalSceneRngPending = ReadStaticBoolean(
                externalClockControllerType,
                "SceneRandomSynchronizationPending");
            externalSceneClockExclusionActive = ReadStaticBoolean(
                externalClockControllerType,
                "SceneClockExclusionActive");
            externalSceneClockExclusionBeginCount = ReadStaticInt32(
                externalClockControllerType,
                "SceneClockExclusionBeginCount");
            externalSceneClockExclusionFinishCount = ReadStaticInt32(
                externalClockControllerType,
                "SceneClockExclusionFinishCount");
            externalSceneClockExclusionFrozenTimeUpdateCount =
                ReadStaticInt32(
                    externalClockControllerType,
                    "SceneClockExclusionFrozenTimeUpdateCount");
            externalSceneClockExclusionFaultCode = ReadStaticInt32(
                externalClockControllerType,
                "SceneClockExclusionFaultCode");
            externalSceneClockExclusionPreSynchronizationCount =
                ReadStaticInt32(
                    externalClockControllerType,
                    "SceneClockExclusionPreSynchronizationCount");
            externalSceneFramePhaseModulo = ReadStaticInt32(
                externalClockControllerType,
                "SceneFramePhaseModuloValue");
            externalSceneFramePhaseTarget = ReadStaticInt32(
                externalClockControllerType,
                "SceneFramePhaseTargetValue");
            externalRecordingRootRequestObservedFrameCount = ReadStaticInt32(
                externalClockControllerType,
                "RecordingRootRequestObservedFrameCount");
            externalRecordingRootFramePhase = ReadStaticInt32(
                externalClockControllerType,
                "RecordingRootFramePhase");
            externalRecordingPhaseNormalizationActive = ReadStaticBoolean(
                externalClockControllerType,
                "RecordingPhaseNormalizationActive");
            externalRecordingPhaseNormalizationCompleted = ReadStaticBoolean(
                externalClockControllerType,
                "RecordingPhaseNormalizationCompleted");
            externalRecordingPhaseNormalizationBeginCount = ReadStaticInt32(
                externalClockControllerType,
                "RecordingPhaseNormalizationBeginCount");
            externalRecordingPhaseNormalizationHoldFrameCount = ReadStaticInt32(
                externalClockControllerType,
                "RecordingPhaseNormalizationHoldFrameCount");
            externalRecordingPhaseNormalizationReleaseCount = ReadStaticInt32(
                externalClockControllerType,
                "RecordingPhaseNormalizationReleaseCount");
            externalRecordingPhaseNormalizationLastFrameCount = ReadStaticInt32(
                externalClockControllerType,
                "RecordingPhaseNormalizationLastFrameCount");
            externalRecordingPhaseNormalizationLastFramePhase = ReadStaticInt32(
                externalClockControllerType,
                "RecordingPhaseNormalizationLastFramePhase");
            externalRecordingPhaseNormalizationHeldTimeBits = ReadStaticInt32(
                externalClockControllerType,
                "RecordingPhaseNormalizationHeldTimeBits");
            externalRecordingPhaseNormalizationHeldTimeDoubleBits =
                ReadStaticInt64(
                    externalClockControllerType,
                    "RecordingPhaseNormalizationHeldTimeDoubleBits");
            externalRecordingPhaseNormalizationRemainingNormalFrameCount =
                ReadStaticInt32(
                    externalClockControllerType,
                    "RecordingPhaseNormalizationRemainingNormalFrameCount");
            externalRecordingPhaseNormalizationRestoreFramePhase =
                ReadStaticInt32(
                    externalClockControllerType,
                    "RecordingPhaseNormalizationRestoreFramePhase");
            externalRecordingPhaseNormalizationFaultCode = ReadStaticInt32(
                externalClockControllerType,
                "RecordingPhaseNormalizationFaultCode");
            externalSceneActivationAlignmentBeginCount = ReadStaticInt32(
                externalClockControllerType,
                "SceneActivationAlignmentBeginCount");
            externalSceneActivationAlignmentHoldFrameCount = ReadStaticInt32(
                externalClockControllerType,
                "SceneActivationAlignmentHoldFrameCount");
            externalSceneActivationAlignmentReleaseCount = ReadStaticInt32(
                externalClockControllerType,
                "SceneActivationAlignmentReleaseCount");
            externalSceneActivationAlignmentFirstFrameCount = ReadStaticInt32(
                externalClockControllerType,
                "SceneActivationAlignmentFirstFrameCount");
            externalSceneActivationAlignmentFirstFramePhase = ReadStaticInt32(
                externalClockControllerType,
                "SceneActivationAlignmentFirstFramePhase");
            externalSceneActivationAlignmentLastFrameCount = ReadStaticInt32(
                externalClockControllerType,
                "SceneActivationAlignmentLastFrameCount");
            externalSceneActivationAlignmentLastFramePhase = ReadStaticInt32(
                externalClockControllerType,
                "SceneActivationAlignmentLastFramePhase");
            externalSceneFinishAlignmentBeginCount = ReadStaticInt32(
                externalClockControllerType,
                "SceneFinishAlignmentBeginCount");
            externalSceneFinishAlignmentHoldFrameCount = ReadStaticInt32(
                externalClockControllerType,
                "SceneFinishAlignmentHoldFrameCount");
            externalSceneFinishAlignmentReleaseCount = ReadStaticInt32(
                externalClockControllerType,
                "SceneFinishAlignmentReleaseCount");
            externalSceneFinishAlignmentLastFrameCount = ReadStaticInt32(
                externalClockControllerType,
                "SceneFinishAlignmentLastFrameCount");
            externalSceneFinishAlignmentLastFramePhase = ReadStaticInt32(
                externalClockControllerType,
                "SceneFinishAlignmentLastFramePhase");
            externalSceneFrameAlignmentFaultCode = ReadStaticInt32(
                externalClockControllerType,
                "SceneFrameAlignmentFaultCode");
            externalClockBridgeStatus = ReadStaticInt32(
                externalClockControllerType,
                "BridgeStatus");
            externalRuntimeVirtualClockRegistered = ReadStaticBoolean(
                externalClockControllerType,
                "RuntimeVirtualClockRegistered");
            externalVirtualClockPaused = ReadStaticInt32(
                externalClockControllerType,
                "VirtualClockPaused");
            externalVirtualClockPauseCount = ReadStaticInt32(
                externalClockControllerType,
                "VirtualClockPauseCount");
            externalVirtualClockResumePending = ReadStaticInt32(
                externalClockControllerType,
                "VirtualClockResumePending");
            externalVirtualClockResumeRequestCount = ReadStaticInt32(
                externalClockControllerType,
                "VirtualClockResumeRequestCount");
            externalVirtualClockResumeCount = ReadStaticInt32(
                externalClockControllerType,
                "VirtualClockResumeCount");
            externalDeterministicClockEnabled = ReadStaticBoolean(
                externalClockControllerType,
                "DeterministicClockEnabled");
            externalDeterministicClockFrequency = ReadStaticInt64(
                externalClockControllerType,
                "DeterministicClockFrequency");
            externalDeterministicClockStepTicks = ReadStaticInt64(
                externalClockControllerType,
                "DeterministicClockStepTicks");
            externalDeterministicClockAnchor = ReadStaticInt64(
                externalClockControllerType,
                "DeterministicClockAnchor");
            externalDeterministicClockFrameAdvanceCount = ReadStaticInt32(
                externalClockControllerType,
                "DeterministicClockFrameAdvanceCount");
            externalDeterministicClockEnableFaultCode = ReadStaticInt32(
                externalClockControllerType,
                "DeterministicClockEnableFaultCode");
            externalDeterministicClockAdvanceFaultCode = ReadStaticInt32(
                externalClockControllerType,
                "DeterministicClockAdvanceFaultCode");
            externalDeterministicClockLastUnityFrameCount = ReadStaticInt32(
                externalClockControllerType,
                "DeterministicClockLastUnityFrameCount");
            externalDeterministicClockDuplicateTimeUpdateSkipCount =
                ReadStaticInt32(
                    externalClockControllerType,
                    "DeterministicClockDuplicateTimeUpdateSkipCount");
            externalDeterministicClockSceneLoadFrameSkipCount =
                ReadStaticInt32(
                    externalClockControllerType,
                    "DeterministicClockSceneLoadFrameSkipCount");
            externalDeterministicClockUnityFrameFaultCode = ReadStaticInt32(
                externalClockControllerType,
                "DeterministicClockUnityFrameFaultCode");
            externalRealtimeEpochNormalizationPolicyId = ReadStaticString(
                externalClockControllerType,
                "ActiveRealtimeEpochNormalizationPolicyId");
            externalRealtimeEpochNormalizationApplied = ReadStaticBoolean(
                externalClockControllerType,
                "RealtimeEpochNormalizationApplied");
            externalRealtimeEpochNormalizationCount = ReadStaticInt32(
                externalClockControllerType,
                "RealtimeEpochNormalizationCount");
            externalRealtimeEpochNormalizationGameTimeBits = ReadStaticInt64(
                externalClockControllerType,
                "RealtimeEpochNormalizationGameTimeBits");
            externalRealtimeEpochNormalizationBeforeBits = ReadStaticInt64(
                externalClockControllerType,
                "RealtimeEpochNormalizationBeforeBits");
            externalRealtimeEpochNormalizationTargetBits = ReadStaticInt64(
                externalClockControllerType,
                "RealtimeEpochNormalizationTargetBits");
            externalRealtimeEpochNormalizationAfterBits = ReadStaticInt64(
                externalClockControllerType,
                "RealtimeEpochNormalizationAfterBits");
            externalRealtimeEpochNormalizationCanonicalOffsetSeconds =
                ReadStaticInt64(
                    externalClockControllerType,
                    "RealtimeEpochNormalizationCanonicalOffsetSeconds");
            externalRealtimeEpochNormalizationDeltaTicks = ReadStaticInt64(
                externalClockControllerType,
                "RealtimeEpochNormalizationDeltaTicks");
            externalRealtimeEpochNormalizationFaultCode = ReadStaticInt32(
                externalClockControllerType,
                "RealtimeEpochNormalizationFaultCode");
            if (refreshDoublePhaseCalibration)
            {
                externalDoublePhaseCalibrationApplied = ReadStaticBoolean(
                    externalClockControllerType,
                    "DoublePhaseCalibrationApplied");
                externalDoublePhaseCalibrationAttempts = ReadStaticInt32(
                    externalClockControllerType,
                    "DoublePhaseCalibrationAttempts");
                externalDoublePhaseInitialResidualBits = ReadStaticInt64(
                    externalClockControllerType,
                    "DoublePhaseInitialResidualBits");
                externalDoublePhaseFinalResidualBits = ReadStaticInt64(
                    externalClockControllerType,
                    "DoublePhaseFinalResidualBits");
                externalDoublePhaseDownwardQuantizationCount = ReadStaticInt32(
                    externalClockControllerType,
                    "DoublePhaseDownwardQuantizationCount");
                externalDoublePhaseLastCorrectionBits = ReadStaticInt32(
                    externalClockControllerType,
                    "DoublePhaseLastCorrectionBits");
                externalDoublePhaseCalibrationFaultCode = ReadStaticInt32(
                    externalClockControllerType,
                    "DoublePhaseCalibrationFaultCode");
            }
            externalStartupClockHookInstalled = ReadStaticBoolean(
                externalClockControllerType,
                "StartupHookInstalled");
            externalStartupClockLatchEnabled = ReadStaticBoolean(
                externalClockControllerType,
                "StartupLatchEnabled");
            externalStartupClockHookThreadId = ReadStaticUInt32(
                externalClockControllerType,
                "StartupHookThreadId");
            externalStartupClockVirtualQpcCallCount = ReadStaticInt32(
                externalClockControllerType,
                "StartupVirtualQpcCallCount");
            externalStartupClockHandoffAdoptCount = ReadStaticInt32(
                externalClockControllerType,
                "StartupHandoffAdoptCount");
            externalStartupClockFaultCode = ReadStaticInt32(
                externalClockControllerType,
                "StartupFaultCode");
            externalTimeUpdateResumeBoundaryInstalled = ReadStaticBoolean(
                externalClockControllerType,
                "TimeUpdateResumeBoundaryInstalled");
            externalTimeUpdateResumeBoundaryInstallCount = ReadStaticInt32(
                externalClockControllerType,
                "TimeUpdateResumeBoundaryInstallCount");
            externalTimeUpdateResumeBoundaryCallbackCount = ReadStaticInt32(
                externalClockControllerType,
                "TimeUpdateResumeBoundaryCallbackCount");
            externalTimeUpdateResumeBoundaryCommitCount = ReadStaticInt32(
                externalClockControllerType,
                "TimeUpdateResumeBoundaryCommitCount");
            externalTimeUpdateResumeCommitFaultCode = ReadStaticInt32(
                externalClockControllerType,
                "TimeUpdateResumeCommitFaultCode");
            externalPlayerLoopPostLateUpdateIndex = ReadStaticInt32(
                externalClockControllerType,
                "PlayerLoopPostLateUpdateIndex");
            externalPlayerLoopTimeUpdateIndex = ReadStaticInt32(
                externalClockControllerType,
                "PlayerLoopTimeUpdateIndex");
            externalPlayerLoopResumeBoundaryIndex = ReadStaticInt32(
                externalClockControllerType,
                "PlayerLoopResumeBoundaryIndex");
            externalPlayerLoopWaitForPresentationIndex = ReadStaticInt32(
                externalClockControllerType,
                "PlayerLoopWaitForPresentationIndex");
            externalPlayerLoopBoundaryError = ReadStaticString(
                externalClockControllerType,
                "PlayerLoopBoundaryError");
        }

        private void AppendExternalRngTelemetry(StringBuilder builder)
        {
            builder.Append(",\"externalClockProfileId\":");
            AppendString(builder, externalClockProfileId);
            builder.Append(",\"externalRngPayloadPolicyId\":");
            AppendString(builder, externalRngPayloadPolicyId);
            builder.Append(",\"externalRngSynchronizationRequested\":");
            builder.Append(
                externalRngSynchronizationRequested ? "true" : "false");
            builder.Append(",\"externalRecordingRngSynchronizationRequested\":");
            builder.Append(
                externalRecordingRngSynchronizationRequested
                    ? "true"
                    : "false");
            builder.Append(",\"externalRecordingRngSynchronizationApplied\":");
            builder.Append(
                externalRecordingRngSynchronizationApplied
                    ? "true"
                    : "false");
            builder.Append(",\"externalRngResetCount\":");
            builder.Append(
                externalRngResetCount.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"externalRngTransitionStartCount\":");
            builder.Append(
                externalRngTransitionStartCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRngGameplayReadyCount\":");
            builder.Append(
                externalRngGameplayReadyCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRngFirstGameplayReadyFrameCount\":");
            builder.Append(
                externalRngFirstGameplayReadyFrameCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRngFirstGameplayReadyFramePhase\":");
            builder.Append(
                externalRngFirstGameplayReadyFramePhase.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRngGameplayReadyAlignmentHoldUpdateCount\":");
            builder.Append(
                externalRngGameplayReadyAlignmentHoldUpdateCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRngSceneEpoch\":");
            builder.Append(
                externalRngSceneEpoch.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"externalRngLastAppliedSeed\":");
            builder.Append(
                externalRngLastAppliedSeed.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRngLastBoundary\":");
            AppendString(builder, externalRngLastBoundary);
            builder.Append(",\"externalRngLastScene\":");
            AppendString(builder, externalRngLastScene);
            builder.Append(",\"externalRngFaultCode\":");
            builder.Append(
                externalRngFaultCode.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"externalSceneRngPending\":");
            builder.Append(externalSceneRngPending ? "true" : "false");
            builder.Append(",\"externalSceneClockExclusionActive\":");
            builder.Append(
                externalSceneClockExclusionActive ? "true" : "false");
            builder.Append(",\"externalSceneClockExclusionBeginCount\":");
            builder.Append(
                externalSceneClockExclusionBeginCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalSceneClockExclusionFinishCount\":");
            builder.Append(
                externalSceneClockExclusionFinishCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalSceneClockExclusionFrozenTimeUpdateCount\":");
            builder.Append(
                externalSceneClockExclusionFrozenTimeUpdateCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalSceneClockExclusionFaultCode\":");
            builder.Append(
                externalSceneClockExclusionFaultCode.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalSceneClockExclusionPreSynchronizationCount\":");
            builder.Append(
                externalSceneClockExclusionPreSynchronizationCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalSceneFramePhaseModulo\":");
            builder.Append(
                externalSceneFramePhaseModulo.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalSceneFramePhaseTarget\":");
            builder.Append(
                externalSceneFramePhaseTarget.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRecordingRootRequestObservedFrameCount\":");
            builder.Append(
                externalRecordingRootRequestObservedFrameCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRecordingRootFramePhase\":");
            builder.Append(
                externalRecordingRootFramePhase.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRecordingPhaseNormalizationActive\":");
            builder.Append(
                externalRecordingPhaseNormalizationActive ? "true" : "false");
            builder.Append(",\"externalRecordingPhaseNormalizationCompleted\":");
            builder.Append(
                externalRecordingPhaseNormalizationCompleted ? "true" : "false");
            builder.Append(",\"externalRecordingPhaseNormalizationBeginCount\":");
            builder.Append(
                externalRecordingPhaseNormalizationBeginCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRecordingPhaseNormalizationHoldFrameCount\":");
            builder.Append(
                externalRecordingPhaseNormalizationHoldFrameCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRecordingPhaseNormalizationReleaseCount\":");
            builder.Append(
                externalRecordingPhaseNormalizationReleaseCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRecordingPhaseNormalizationLastFrameCount\":");
            builder.Append(
                externalRecordingPhaseNormalizationLastFrameCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRecordingPhaseNormalizationLastFramePhase\":");
            builder.Append(
                externalRecordingPhaseNormalizationLastFramePhase.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRecordingPhaseNormalizationHeldTimeBits\":");
            AppendInt32Hex(
                builder,
                externalRecordingPhaseNormalizationHeldTimeBits);
            builder.Append(",\"externalRecordingPhaseNormalizationHeldTimeDoubleBits\":");
            AppendInt64Hex(
                builder,
                externalRecordingPhaseNormalizationHeldTimeDoubleBits);
            builder.Append(",\"externalRecordingPhaseNormalizationRemainingNormalFrameCount\":");
            builder.Append(
                externalRecordingPhaseNormalizationRemainingNormalFrameCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRecordingPhaseNormalizationRestoreFramePhase\":");
            builder.Append(
                externalRecordingPhaseNormalizationRestoreFramePhase.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRecordingPhaseNormalizationFaultCode\":");
            builder.Append(
                externalRecordingPhaseNormalizationFaultCode.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalSceneActivationAlignmentBeginCount\":");
            builder.Append(
                externalSceneActivationAlignmentBeginCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalSceneActivationAlignmentHoldFrameCount\":");
            builder.Append(
                externalSceneActivationAlignmentHoldFrameCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalSceneActivationAlignmentReleaseCount\":");
            builder.Append(
                externalSceneActivationAlignmentReleaseCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalSceneActivationAlignmentFirstFrameCount\":");
            builder.Append(
                externalSceneActivationAlignmentFirstFrameCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalSceneActivationAlignmentFirstFramePhase\":");
            builder.Append(
                externalSceneActivationAlignmentFirstFramePhase.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalSceneActivationAlignmentLastFrameCount\":");
            builder.Append(
                externalSceneActivationAlignmentLastFrameCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalSceneActivationAlignmentLastFramePhase\":");
            builder.Append(
                externalSceneActivationAlignmentLastFramePhase.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalSceneFinishAlignmentBeginCount\":");
            builder.Append(
                externalSceneFinishAlignmentBeginCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalSceneFinishAlignmentHoldFrameCount\":");
            builder.Append(
                externalSceneFinishAlignmentHoldFrameCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalSceneFinishAlignmentReleaseCount\":");
            builder.Append(
                externalSceneFinishAlignmentReleaseCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalSceneFinishAlignmentLastFrameCount\":");
            builder.Append(
                externalSceneFinishAlignmentLastFrameCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalSceneFinishAlignmentLastFramePhase\":");
            builder.Append(
                externalSceneFinishAlignmentLastFramePhase.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalSceneFrameAlignmentFaultCode\":");
            builder.Append(
                externalSceneFrameAlignmentFaultCode.ToString(
                    CultureInfo.InvariantCulture));
        }

        private static object ReadStaticProperty(
            Type? type,
            string propertyName)
        {
            if (type == null)
            {
                throw new InvalidOperationException(
                    "External clock payload type is unavailable.");
            }
            var property = type.GetProperty(
                               propertyName,
                               BindingFlags.Public | BindingFlags.Static)
                           ?? throw new MissingMemberException(
                               type.FullName,
                               propertyName);
            return property.GetValue(null, null)
                   ?? throw new InvalidOperationException(
                       "External clock payload property is null: "
                       + propertyName);
        }

        private void Complete(bool success, string error)
        {
            if (complete)
            {
                return;
            }
            complete = true;
            try
            {
                if (!WriteTrace())
                {
                    success = false;
                    error = "Reference trace exceeded its byte limit; evidence is incomplete.";
                }
                WriteResult(success, error);
                WriteStatus(success ? "complete" : "failed", error);
            }
            catch (Exception writeException)
            {
                logError(
                    "T24 reference observer evidence write failed: "
                    + writeException);
            }
            finally
            {
                UnregisterHooks();
                DestroyRunners();
                inputSyncStart?.Dispose();
                inputSyncStart = null;
                inputSyncFrameReady?.Dispose();
                inputSyncFrameReady = null;
                inputSyncApplied?.Dispose();
                inputSyncApplied = null;
                randomSyncRequest?.Dispose();
                randomSyncRequest = null;
                randomSyncApplied?.Dispose();
                randomSyncApplied = null;
                recordingRandomSyncRequest?.Dispose();
                recordingRandomSyncRequest = null;
                recordingRandomSyncApplied?.Dispose();
                recordingRandomSyncApplied = null;
                clockPayloadReady?.Dispose();
                clockPayloadReady = null;
                recordingArmRelease?.Dispose();
                recordingArmRelease = null;
                if (options.ExitWhenComplete)
                {
                    Application.Quit();
                }
            }
        }

        private bool WriteTrace()
        {
            var traceComplete = BoundedJsonLinesFile.Write(
                Path.Combine(options.OutputDirectory, "trace.jsonl"),
                frames.Select(frame => VanillaEquivalenceFrameJson.Serialize(frame)));
            var phaseComplete = BoundedJsonLinesFile.Write(
                Path.Combine(
                    options.OutputDirectory,
                    "input-phase.jsonl"),
                inputPhaseObservations.Select(observation => observation.Serialize()));
            return traceComplete && phaseComplete;
        }

        private void WriteResult(bool success, string error)
        {
            // The payload resets scene-local phase telemetry when the active
            // scene changes. The recording-boundary calibration snapshot was
            // already validated before logical tick zero, so preserve that
            // evidence while refreshing counters that legitimately advance
            // throughout a multi-scene capture.
            RefreshExternalClockTelemetry(
                refreshDoublePhaseCalibration: false);
            var builder = new StringBuilder(512);
            builder.Append("{\"schemaVersion\":1,\"runId\":");
            AppendString(builder, options.RunId);
            builder.Append(",\"mode\":");
            AppendString(builder, options.Mode);
            builder.Append(",\"samplingBoundary\":");
            AppendString(
                builder,
                CompletedFrameBoundarySignal.BoundaryId);
            builder.Append(",\"success\":");
            builder.Append(success ? "true" : "false");
            builder.Append(",\"frameCount\":");
            builder.Append(
                frames.Count.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"inputInjected\":false");
            builder.Append(",\"timeWritten\":false");
            builder.Append(",\"gameplayStateWritten\":false");
            builder.Append(",\"saveLoadedByObserver\":false");
            builder.Append(",\"baselineCaptured\":");
            builder.Append(baselineCaptured ? "true" : "false");
            builder.Append(",\"externalInputSynchronized\":");
            builder.Append(inputSyncActive ? "true" : "false");
            builder.Append(",\"externalInputSynchronizationRequired\":");
            builder.Append(
                options.RequireExternalInputSync ? "true" : "false");
            builder.Append(",\"externalInputSynchronizedFrames\":");
            builder.Append(
                inputSyncFrameCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalInputSynchronizationPrimeFrames\":");
            builder.Append(
                inputSyncPrimeCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRngSynchronized\":");
            builder.Append(
                randomSynchronizationApplied ? "true" : "false");
            builder.Append(",\"externalRngSynchronizationOriginCaptured\":");
            builder.Append(
                randomSynchronizationOriginCaptured ? "true" : "false");
            builder.Append(",\"recordingRngSynchronized\":");
            builder.Append(
                recordingRandomSynchronizationApplied ? "true" : "false");
            builder.Append(",\"recordingRngSynchronizationOriginCaptured\":");
            builder.Append(
                recordingRandomSynchronizationOriginCaptured
                    ? "true"
                    : "false");
            builder.Append(",\"externalRngSynchronizationPolicy\":");
            AppendString(builder, ExternalRandomSynchronizationPolicy);
            builder.Append(",\"externalRngSeed\":");
            builder.Append(
                ExternalRandomSynchronizationSeed.ToString(
                    CultureInfo.InvariantCulture));
            AppendExternalRngTelemetry(builder);
            builder.Append(",\"externalClockBridgeAbi\":");
            builder.Append(
                externalClockBridgeAbi.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalClockBridgeStatus\":");
            builder.Append(
                externalClockBridgeStatus.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRuntimeVirtualClockRegistered\":");
            builder.Append(
                externalRuntimeVirtualClockRegistered ? "true" : "false");
            builder.Append(",\"externalVirtualClockPaused\":");
            builder.Append(
                externalVirtualClockPaused.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalVirtualClockPauseCount\":");
            builder.Append(
                externalVirtualClockPauseCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalVirtualClockResumePending\":");
            builder.Append(
                externalVirtualClockResumePending.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalVirtualClockResumeRequestCount\":");
            builder.Append(
                externalVirtualClockResumeRequestCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalVirtualClockResumeCount\":");
            builder.Append(
                externalVirtualClockResumeCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalDeterministicClockEnabled\":");
            builder.Append(
                externalDeterministicClockEnabled ? "true" : "false");
            builder.Append(",\"externalDeterministicClockFrequency\":");
            builder.Append(
                externalDeterministicClockFrequency.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalDeterministicClockStepTicks\":");
            builder.Append(
                externalDeterministicClockStepTicks.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalDeterministicClockAnchor\":");
            builder.Append(
                externalDeterministicClockAnchor.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalDeterministicClockFrameAdvanceCount\":");
            builder.Append(
                externalDeterministicClockFrameAdvanceCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalDeterministicClockEnableFaultCode\":");
            builder.Append(
                externalDeterministicClockEnableFaultCode.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalDeterministicClockAdvanceFaultCode\":");
            builder.Append(
                externalDeterministicClockAdvanceFaultCode.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalDeterministicClockLastUnityFrameCount\":");
            builder.Append(
                externalDeterministicClockLastUnityFrameCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalDeterministicClockDuplicateTimeUpdateSkipCount\":");
            builder.Append(
                externalDeterministicClockDuplicateTimeUpdateSkipCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalDeterministicClockSceneLoadFrameSkipCount\":");
            builder.Append(
                externalDeterministicClockSceneLoadFrameSkipCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalDeterministicClockUnityFrameFaultCode\":");
            builder.Append(
                externalDeterministicClockUnityFrameFaultCode.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRealtimeEpochNormalizationPolicyId\":");
            AppendString(builder, externalRealtimeEpochNormalizationPolicyId);
            builder.Append(",\"externalRealtimeEpochNormalizationApplied\":");
            builder.Append(
                externalRealtimeEpochNormalizationApplied ? "true" : "false");
            builder.Append(",\"externalRealtimeEpochNormalizationCount\":");
            builder.Append(
                externalRealtimeEpochNormalizationCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRealtimeEpochNormalizationGameTimeBits\":");
            AppendInt64Hex(
                builder,
                externalRealtimeEpochNormalizationGameTimeBits);
            builder.Append(",\"externalRealtimeEpochNormalizationBeforeBits\":");
            AppendInt64Hex(
                builder,
                externalRealtimeEpochNormalizationBeforeBits);
            builder.Append(",\"externalRealtimeEpochNormalizationTargetBits\":");
            AppendInt64Hex(
                builder,
                externalRealtimeEpochNormalizationTargetBits);
            builder.Append(",\"externalRealtimeEpochNormalizationAfterBits\":");
            AppendInt64Hex(
                builder,
                externalRealtimeEpochNormalizationAfterBits);
            builder.Append(",\"externalRealtimeEpochNormalizationCanonicalOffsetSeconds\":");
            builder.Append(
                externalRealtimeEpochNormalizationCanonicalOffsetSeconds.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRealtimeEpochNormalizationDeltaTicks\":");
            builder.Append(
                externalRealtimeEpochNormalizationDeltaTicks.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRealtimeEpochNormalizationFaultCode\":");
            builder.Append(
                externalRealtimeEpochNormalizationFaultCode.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalDoublePhaseCalibrationApplied\":");
            builder.Append(
                externalDoublePhaseCalibrationApplied ? "true" : "false");
            builder.Append(",\"externalDoublePhaseCalibrationAttempts\":");
            builder.Append(
                externalDoublePhaseCalibrationAttempts.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalDoublePhaseInitialResidualBits\":");
            AppendInt64Hex(
                builder,
                externalDoublePhaseInitialResidualBits);
            builder.Append(",\"externalDoublePhaseInitialResidual\":");
            AppendFloat64(
                builder,
                BitConverter.Int64BitsToDouble(
                    externalDoublePhaseInitialResidualBits));
            builder.Append(",\"externalDoublePhaseFinalResidualBits\":");
            AppendInt64Hex(
                builder,
                externalDoublePhaseFinalResidualBits);
            builder.Append(",\"externalDoublePhaseFinalResidual\":");
            AppendFloat64(
                builder,
                BitConverter.Int64BitsToDouble(
                    externalDoublePhaseFinalResidualBits));
            builder.Append(",\"externalDoublePhaseDownwardQuantizationCount\":");
            builder.Append(
                externalDoublePhaseDownwardQuantizationCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalDoublePhaseLastCorrectionBits\":");
            AppendInt32Hex(
                builder,
                externalDoublePhaseLastCorrectionBits);
            builder.Append(",\"externalDoublePhaseCalibrationFaultCode\":");
            builder.Append(
                externalDoublePhaseCalibrationFaultCode.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"recordingArmBoundaryReached\":");
            builder.Append(recordingArmBoundaryReached ? "true" : "false");
            builder.Append(",\"recordingArmReleased\":");
            builder.Append(recordingArmReleased ? "true" : "false");
            builder.Append(",\"recordingArmBoundaryTimeRaw\":");
            AppendFloat32(builder, recordingArmBoundaryTimeRaw);
            builder.Append(",\"recordingArmBoundaryFixedTimeRaw\":");
            AppendFloat32(builder, recordingArmBoundaryFixedTimeRaw);
            builder.Append(",\"recordingArmBoundaryFrameCount\":");
            builder.Append(
                recordingArmBoundaryFrameCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"recordingArmBoundaryFramePhase\":");
            builder.Append(
                recordingArmBoundaryFramePhase.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"recordingFramePhaseModulo\":");
            builder.Append(
                RequiredRecordingFramePhaseModulo.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"recordingFramePhaseTarget\":");
            builder.Append(
                RequiredRecordingFramePhase.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"recordingAbsoluteTimeTarget\":");
            AppendFloat32(builder, RequiredRecordingAbsoluteTimeTarget);
            builder.Append(",\"externalStartupClockHookInstalled\":");
            builder.Append(
                externalStartupClockHookInstalled ? "true" : "false");
            builder.Append(",\"externalStartupClockLatchEnabled\":");
            builder.Append(
                externalStartupClockLatchEnabled ? "true" : "false");
            builder.Append(",\"externalStartupClockHookThreadId\":");
            builder.Append(
                externalStartupClockHookThreadId.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalStartupClockVirtualQpcCallCount\":");
            builder.Append(
                externalStartupClockVirtualQpcCallCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalStartupClockHandoffAdoptCount\":");
            builder.Append(
                externalStartupClockHandoffAdoptCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalStartupClockFaultCode\":");
            builder.Append(
                externalStartupClockFaultCode.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalTimeUpdateResumeBoundaryInstalled\":");
            builder.Append(
                externalTimeUpdateResumeBoundaryInstalled ? "true" : "false");
            builder.Append(",\"externalTimeUpdateResumeBoundaryInstallCount\":");
            builder.Append(
                externalTimeUpdateResumeBoundaryInstallCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalTimeUpdateResumeBoundaryCallbackCount\":");
            builder.Append(
                externalTimeUpdateResumeBoundaryCallbackCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalTimeUpdateResumeBoundaryCommitCount\":");
            builder.Append(
                externalTimeUpdateResumeBoundaryCommitCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalTimeUpdateResumeCommitFaultCode\":");
            builder.Append(
                externalTimeUpdateResumeCommitFaultCode.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalPlayerLoopPostLateUpdateIndex\":");
            builder.Append(
                externalPlayerLoopPostLateUpdateIndex.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalPlayerLoopTimeUpdateIndex\":");
            builder.Append(
                externalPlayerLoopTimeUpdateIndex.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalPlayerLoopResumeBoundaryIndex\":");
            builder.Append(
                externalPlayerLoopResumeBoundaryIndex.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalPlayerLoopWaitForPresentationIndex\":");
            builder.Append(
                externalPlayerLoopWaitForPresentationIndex.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalPlayerLoopBoundaryError\":");
            AppendString(builder, externalPlayerLoopBoundaryError);
            builder.Append(",\"inputPhaseObservationError\":");
            AppendString(builder, inputPhaseObservationError);
            builder.Append(",\"error\":");
            AppendString(builder, error);
            builder.Append('}');
            WriteAtomic(
                Path.Combine(options.OutputDirectory, "result.json"),
                builder.ToString());
        }

        private void WriteStatus(string phase, string error)
        {
            RefreshExternalClockTelemetry(
                refreshDoublePhaseCalibration: false);
            var builder = new StringBuilder(384);
            builder.Append("{\"schemaVersion\":1,\"runId\":");
            AppendString(builder, options.RunId);
            builder.Append(",\"mode\":");
            AppendString(builder, options.Mode);
            builder.Append(",\"samplingBoundary\":");
            AppendString(
                builder,
                CompletedFrameBoundarySignal.BoundaryId);
            builder.Append(",\"phase\":");
            AppendString(builder, phase);
            builder.Append(",\"frames\":");
            builder.Append(
                frames.Count.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"scene\":");
            AppendString(
                builder,
                USceneManager.GetActiveScene().name ?? string.Empty);
            var hero = HeroController.SilentInstance;
            var player = PlayerData.instance;
            var animation = hero?.GetComponent<HeroAnimationController>();
            builder.Append(",\"atBench\":");
            builder.Append(player?.atBench == true ? "true" : "false");
            builder.Append(",\"heroAcceptingInput\":");
            builder.Append(
                hero?.acceptingInput == true ? "true" : "false");
            builder.Append(",\"heroAnimationClip\":");
            AppendString(
                builder,
                animation?.animator?.CurrentClip?.name ?? string.Empty);
            builder.Append(",\"heroAnimationFrame\":");
            builder.Append(
                (animation?.animator?.CurrentFrame ?? -1).ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"targetFrameRate\":");
            builder.Append(
                Application.targetFrameRate.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"vSyncCount\":");
            builder.Append(
                QualitySettings.vSyncCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"captureDeltaTime\":");
            builder.Append(
                Time.captureDeltaTime.ToString(
                    "R",
                    CultureInfo.InvariantCulture));
            builder.Append(",\"fixedDeltaTime\":");
            builder.Append(
                Time.fixedDeltaTime.ToString(
                    "R",
                    CultureInfo.InvariantCulture));
            builder.Append(",\"timeRawDouble\":");
            AppendFloat64(builder, Time.timeAsDouble);
            builder.Append(",\"fixedTimeRawDouble\":");
            AppendFloat64(builder, Time.fixedTimeAsDouble);
            builder.Append(",\"realtimeSinceStartupRawDouble\":");
            AppendFloat64(builder, Time.realtimeSinceStartupAsDouble);
            builder.Append(",\"timeMinusFixed\":");
            builder.Append(
                (Time.time - Time.fixedTime).ToString(
                    "R",
                    CultureInfo.InvariantCulture));
            CaptureMenuSelection(
                out var selectedObject,
                out var selectedSaveSlot);
            builder.Append(",\"selectedObject\":");
            AppendString(builder, selectedObject);
            builder.Append(",\"selectedSaveSlot\":");
            builder.Append(
                selectedSaveSlot.ToString(CultureInfo.InvariantCulture));
            var actions = InputHandler.Instance?.inputActions;
            var benchFsm = FindFixtureBenchFsm();
            builder.Append(",\"benchFsmState\":");
            AppendString(
                builder,
                benchFsm?.ActiveStateName ?? string.Empty);
            builder.Append(",\"benchSleeping\":");
            builder.Append(
                benchFsm?.Variables.FindFsmBool("Sleeping")?.Value == true
                    ? "true"
                    : "false");
            builder.Append(",\"readinessBlocker\":");
            AppendString(builder, readinessBlocker);
            builder.Append(",\"readinessStableUpdates\":");
            builder.Append(
                stableUpdates.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"readinessBoundary\":\"semantic-idle\"");
            builder.Append(",\"readinessRequiredUpdates\":");
            builder.Append(
                RequiredSemanticStableUpdates.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"readinessAbsoluteTimeTarget\":");
            AppendFloat32(builder, RequiredAbsoluteTimeTarget);
            builder.Append(",\"recordingAbsoluteTimeTarget\":");
            AppendFloat32(builder, RequiredRecordingAbsoluteTimeTarget);
            builder.Append(",\"recordingArmBoundaryReached\":");
            builder.Append(recordingArmBoundaryReached ? "true" : "false");
            builder.Append(",\"recordingArmReleased\":");
            builder.Append(recordingArmReleased ? "true" : "false");
            builder.Append(",\"recordingArmBoundaryTimeRaw\":");
            AppendFloat32(builder, recordingArmBoundaryTimeRaw);
            builder.Append(",\"recordingArmBoundaryFixedTimeRaw\":");
            AppendFloat32(builder, recordingArmBoundaryFixedTimeRaw);
            builder.Append(",\"recordingArmBoundaryFrameCount\":");
            builder.Append(
                recordingArmBoundaryFrameCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"recordingArmBoundaryFramePhase\":");
            builder.Append(
                recordingArmBoundaryFramePhase.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"recordingFramePhaseModulo\":");
            builder.Append(
                RequiredRecordingFramePhaseModulo.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"recordingFramePhaseTarget\":");
            builder.Append(
                RequiredRecordingFramePhase.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"readinessTimeRaw\":");
            AppendFloat32(builder, Time.time);
            builder.Append(",\"readinessFixedTimeRaw\":");
            AppendFloat32(builder, Time.fixedTime);
            builder.Append(",\"readinessFixedSteps\":");
            builder.Append(
                readinessFixedSteps.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"readinessGameplayInputActive\":");
            builder.Append(
                VanillaEquivalenceSampler.HasGameplayInput()
                    ? "true"
                    : "false");
            builder.Append(",\"externalInputSyncActive\":");
            builder.Append(inputSyncActive ? "true" : "false");
            builder.Append(",\"externalInputSyncRequired\":");
            builder.Append(
                options.RequireExternalInputSync ? "true" : "false");
            builder.Append(",\"externalInputSyncFrames\":");
            builder.Append(
                inputSyncFrameCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalInputSyncPrimeFrames\":");
            builder.Append(
                inputSyncPrimeCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRngSynchronized\":");
            builder.Append(
                randomSynchronizationApplied ? "true" : "false");
            builder.Append(",\"externalRngSynchronizationOriginCaptured\":");
            builder.Append(
                randomSynchronizationOriginCaptured ? "true" : "false");
            builder.Append(",\"recordingRngSynchronized\":");
            builder.Append(
                recordingRandomSynchronizationApplied ? "true" : "false");
            builder.Append(",\"recordingRngSynchronizationOriginCaptured\":");
            builder.Append(
                recordingRandomSynchronizationOriginCaptured
                    ? "true"
                    : "false");
            builder.Append(",\"externalRngSynchronizationPolicy\":");
            AppendString(builder, ExternalRandomSynchronizationPolicy);
            builder.Append(",\"externalRngSeed\":");
            builder.Append(
                ExternalRandomSynchronizationSeed.ToString(
                    CultureInfo.InvariantCulture));
            AppendExternalRngTelemetry(builder);
            builder.Append(",\"externalRealtimeEpochNormalizationPolicyId\":");
            AppendString(builder, externalRealtimeEpochNormalizationPolicyId);
            builder.Append(",\"externalRealtimeEpochNormalizationApplied\":");
            builder.Append(
                externalRealtimeEpochNormalizationApplied ? "true" : "false");
            builder.Append(",\"externalRealtimeEpochNormalizationCount\":");
            builder.Append(
                externalRealtimeEpochNormalizationCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRealtimeEpochNormalizationGameTimeBits\":");
            AppendInt64Hex(
                builder,
                externalRealtimeEpochNormalizationGameTimeBits);
            builder.Append(",\"externalRealtimeEpochNormalizationBeforeBits\":");
            AppendInt64Hex(
                builder,
                externalRealtimeEpochNormalizationBeforeBits);
            builder.Append(",\"externalRealtimeEpochNormalizationTargetBits\":");
            AppendInt64Hex(
                builder,
                externalRealtimeEpochNormalizationTargetBits);
            builder.Append(",\"externalRealtimeEpochNormalizationAfterBits\":");
            AppendInt64Hex(
                builder,
                externalRealtimeEpochNormalizationAfterBits);
            builder.Append(",\"externalRealtimeEpochNormalizationCanonicalOffsetSeconds\":");
            builder.Append(
                externalRealtimeEpochNormalizationCanonicalOffsetSeconds.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRealtimeEpochNormalizationDeltaTicks\":");
            builder.Append(
                externalRealtimeEpochNormalizationDeltaTicks.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalRealtimeEpochNormalizationFaultCode\":");
            builder.Append(
                externalRealtimeEpochNormalizationFaultCode.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalDoublePhaseCalibrationApplied\":");
            builder.Append(
                externalDoublePhaseCalibrationApplied ? "true" : "false");
            builder.Append(",\"externalDoublePhaseCalibrationAttempts\":");
            builder.Append(
                externalDoublePhaseCalibrationAttempts.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalDoublePhaseInitialResidualBits\":");
            AppendInt64Hex(
                builder,
                externalDoublePhaseInitialResidualBits);
            builder.Append(",\"externalDoublePhaseInitialResidual\":");
            AppendFloat64(
                builder,
                BitConverter.Int64BitsToDouble(
                    externalDoublePhaseInitialResidualBits));
            builder.Append(",\"externalDoublePhaseFinalResidualBits\":");
            AppendInt64Hex(
                builder,
                externalDoublePhaseFinalResidualBits);
            builder.Append(",\"externalDoublePhaseFinalResidual\":");
            AppendFloat64(
                builder,
                BitConverter.Int64BitsToDouble(
                    externalDoublePhaseFinalResidualBits));
            builder.Append(",\"externalDoublePhaseDownwardQuantizationCount\":");
            builder.Append(
                externalDoublePhaseDownwardQuantizationCount.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"externalDoublePhaseLastCorrectionBits\":");
            AppendInt32Hex(
                builder,
                externalDoublePhaseLastCorrectionBits);
            builder.Append(",\"externalDoublePhaseCalibrationFaultCode\":");
            builder.Append(
                externalDoublePhaseCalibrationFaultCode.ToString(
                    CultureInfo.InvariantCulture));
            var statusRigidbody = hero?.GetComponent<Rigidbody2D>();
            builder.Append(",\"readinessHeroPositionX\":");
            AppendFloat32(builder, hero?.transform.position.x ?? 0f);
            builder.Append(",\"readinessHeroPositionY\":");
            AppendFloat32(builder, hero?.transform.position.y ?? 0f);
            builder.Append(",\"readinessRigidbodyPositionX\":");
            AppendFloat32(builder, statusRigidbody?.position.x ?? 0f);
            builder.Append(",\"readinessRigidbodyPositionY\":");
            AppendFloat32(builder, statusRigidbody?.position.y ?? 0f);
            builder.Append(",\"readinessRigidbodyVelocityX\":");
            AppendFloat32(builder, statusRigidbody?.velocity.x ?? 0f);
            builder.Append(",\"readinessRigidbodyVelocityY\":");
            AppendFloat32(builder, statusRigidbody?.velocity.y ?? 0f);
            builder.Append(",\"menuUpPressed\":");
            builder.Append(
                actions?.up.WasPressed == true ? "true" : "false");
            builder.Append(",\"menuDownPressed\":");
            builder.Append(
                actions?.down.WasPressed == true ? "true" : "false");
            builder.Append(",\"menuSubmitPressed\":");
            builder.Append(
                actions?.menuSubmit.WasPressed == true
                    ? "true"
                    : "false");
            builder.Append(",\"menuSubmitBindings\":");
            AppendString(
                builder,
                actions == null
                    ? string.Empty
                    : FormatBindings(actions.menuSubmit));
            builder.Append(",\"menuDownBindings\":");
            AppendString(
                builder,
                actions == null
                    ? string.Empty
                    : FormatBindings(actions.down));
            builder.Append(",\"menuUpBindings\":");
            AppendString(
                builder,
                actions == null
                    ? string.Empty
                    : FormatBindings(actions.up));
            builder.Append(",\"leftBindings\":");
            AppendString(
                builder,
                actions == null
                    ? string.Empty
                    : FormatBindings(actions.left));
            builder.Append(",\"rightBindings\":");
            AppendString(
                builder,
                actions == null
                    ? string.Empty
                    : FormatBindings(actions.right));
            builder.Append(",\"jumpBindings\":");
            AppendString(
                builder,
                actions == null
                    ? string.Empty
                    : FormatBindings(actions.jump));
            builder.Append(",\"attackBindings\":");
            AppendString(
                builder,
                actions == null
                    ? string.Empty
                    : FormatBindings(actions.attack));
            builder.Append(",\"dashBindings\":");
            AppendString(
                builder,
                actions == null
                    ? string.Empty
                    : FormatBindings(actions.dash));
            builder.Append(",\"castBindings\":");
            AppendString(
                builder,
                actions == null
                    ? string.Empty
                    : FormatBindings(actions.cast));
            builder.Append(",\"quickCastBindings\":");
            AppendString(
                builder,
                actions == null
                    ? string.Empty
                    : FormatBindings(actions.quickCast));
            builder.Append(",\"superDashBindings\":");
            AppendString(
                builder,
                actions == null
                    ? string.Empty
                    : FormatBindings(actions.superDash));
            builder.Append(",\"dreamNailBindings\":");
            AppendString(
                builder,
                actions == null
                    ? string.Empty
                    : FormatBindings(actions.dreamNail));
            builder.Append(",\"error\":");
            AppendString(builder, error);
            builder.Append('}');
            WriteAtomic(
                Path.Combine(options.OutputDirectory, "status.json"),
                builder.ToString());
        }

        private void DestroyRunners()
        {
            if (earlyRunner != null)
            {
                UnityEngine.Object.Destroy(earlyRunner.gameObject);
                earlyRunner = null;
            }
            if (lateRunner != null)
            {
                UnityEngine.Object.Destroy(lateRunner.gameObject);
                lateRunner = null;
            }
            if (completedFrameRunner != null)
            {
                UnityEngine.Object.Destroy(completedFrameRunner.gameObject);
                completedFrameRunner = null;
            }
        }

        private void OnPlayerActionSetUpdate(
            On.InControl.PlayerActionSet.orig_Update original,
            InControl.PlayerActionSet self,
            ulong updateTick,
            float deltaTime)
        {
            var actions = InputHandler.Instance?.inputActions;
            var isHeroActions = actions != null
                                && ReferenceEquals(actions, self);
            if (isHeroActions)
            {
                TryObserveInputPhase(
                    "PlayerActionSet.before",
                    actions!,
                    updateTick,
                    string.Empty,
                    string.Empty,
                    string.Empty);
            }

            try
            {
                original(self, updateTick, deltaTime);
            }
            finally
            {
                if (isHeroActions)
                {
                    TryObserveInputPhase(
                        "PlayerActionSet.after",
                        actions!,
                        updateTick,
                        string.Empty,
                        string.Empty,
                        string.Empty);
                    TryCaptureExternalRecordingRandomSynchronizationOrigin();
                    TryArmCaptureAfterCommittedHeroActions(
                        VanillaEquivalenceSampler.CaptureInput(actions!));
                }
            }
        }

        private void TryArmCaptureAfterCommittedHeroActions(
            VanillaEquivalenceInputSample inputSample)
        {
            if (complete
                || !ready
                || !frameOpenAtUpdateBegin
                || !randomSynchronizationApplied
                || !recordingRandomSynchronizationApplied)
            {
                return;
            }
            if (!recording)
            {
                if (options.RequireExternalInputSync
                    && (!inputSyncActive || !inputSyncCaptureEnabled))
                {
                    return;
                }
                if (!inputSyncActive
                    && !VanillaEquivalenceSampler.HasGameplayInput())
                {
                    return;
                }
                BeginRecording();
            }
            ArmCompletedFrameCapture(inputSample);
        }

        private void BeginRecording()
        {
            recording = true;
            WriteStatus("recording", string.Empty);
        }

        private void ArmCompletedFrameCapture(
            VanillaEquivalenceInputSample inputSample)
        {
            if (frameCapturePending)
            {
                if (pendingCaptureVisualTick == visualTick)
                {
                    return;
                }
                Complete(
                    false,
                    "completed-frame-boundary-missing-before-next-input-commit");
                return;
            }
            pendingCaptureVisualTick = visualTick;
            pendingCaptureFixedTick = fixedTick;
            pendingCaptureInputSample = inputSample;
            frameCapturePending = true;
        }

        private void OnListenForRightUpdate(
            On.HutongGames.PlayMaker.Actions.ListenForRight.orig_OnUpdate
                original,
            HutongGames.PlayMaker.Actions.ListenForRight self)
        {
            var actions = InputHandler.Instance?.inputActions;
            if (actions != null)
            {
                TryObserveInputPhase(
                    "ListenForRight.before",
                    actions,
                    actions.UpdateTick,
                    self.Fsm?.GameObject?.name ?? string.Empty,
                    self.Fsm?.Name ?? string.Empty,
                    self.Fsm?.ActiveStateName ?? string.Empty);
            }

            try
            {
                original(self);
            }
            finally
            {
                if (actions != null)
                {
                    TryObserveInputPhase(
                        "ListenForRight.after",
                        actions,
                        actions.UpdateTick,
                        self.Fsm?.GameObject?.name ?? string.Empty,
                        self.Fsm?.Name ?? string.Empty,
                        self.Fsm?.ActiveStateName ?? string.Empty);
                }
            }
        }

        private void OnFsmEvent(
            On.HutongGames.PlayMaker.Fsm.orig_Event_FsmEvent original,
            HutongGames.PlayMaker.Fsm self,
            HutongGames.PlayMaker.FsmEvent fsmEvent)
        {
            var isFixture = IsFixtureBenchFsm(self);
            var actions = InputHandler.Instance?.inputActions;
            if (isFixture && actions != null)
            {
                TryObserveInputPhase(
                    "BenchFsm.Event.before:"
                    + (fsmEvent?.Name ?? string.Empty),
                    actions,
                    actions.UpdateTick,
                    self.GameObject?.name ?? string.Empty,
                    self.Name ?? string.Empty,
                    self.ActiveStateName ?? string.Empty);
            }

            try
            {
                original(self, fsmEvent);
            }
            finally
            {
                if (isFixture && actions != null)
                {
                    TryObserveInputPhase(
                        "BenchFsm.Event.after:"
                        + (fsmEvent?.Name ?? string.Empty),
                        actions,
                        actions.UpdateTick,
                        self.GameObject?.name ?? string.Empty,
                        self.Name ?? string.Empty,
                        self.ActiveStateName ?? string.Empty);
                }
            }
        }

        private void OnFsmEnterState(
            On.HutongGames.PlayMaker.Fsm.orig_EnterState original,
            HutongGames.PlayMaker.Fsm self,
            HutongGames.PlayMaker.FsmState state)
        {
            var isFixture = IsFixtureBenchFsm(self);
            var actions = InputHandler.Instance?.inputActions;
            if (isFixture && actions != null)
            {
                TryObserveInputPhase(
                    "BenchFsm.Enter.before:"
                    + (state?.Name ?? string.Empty),
                    actions,
                    actions.UpdateTick,
                    self.GameObject?.name ?? string.Empty,
                    self.Name ?? string.Empty,
                    self.ActiveStateName ?? string.Empty);
            }

            try
            {
                original(self, state);
            }
            finally
            {
                if (isFixture && actions != null)
                {
                    TryObserveInputPhase(
                        "BenchFsm.Enter.after:"
                        + (state?.Name ?? string.Empty),
                        actions,
                        actions.UpdateTick,
                        self.GameObject?.name ?? string.Empty,
                        self.Name ?? string.Empty,
                        self.ActiveStateName ?? string.Empty);
                }
            }
        }

        private void TryObserveInputPhase(
            string phase,
            HeroActions actions,
            ulong updateTick,
            string gameObject,
            string fsm,
            string state)
        {
            try
            {
                if (complete || !ready)
                {
                    return;
                }

                var right = actions.right;
                var observation = new InputPhaseObservation(
                    ++inputPhaseSequence,
                    visualTick,
                    frames.Count,
                    phase,
                    updateTick,
                    actions.UpdateTick,
                    right.UpdateTick,
                    right.IsPressed,
                    right.WasPressed,
                    right.WasReleased,
                    gameObject,
                    fsm,
                    state);

                if (!inputPhaseTriggered)
                {
                    inputPhasePrelude.Enqueue(observation);
                    while (inputPhasePrelude.Count > 32)
                    {
                        inputPhasePrelude.Dequeue();
                    }

                    if (!right.IsPressed
                        && !right.WasPressed
                        && !right.WasReleased)
                    {
                        return;
                    }

                    inputPhaseTriggered = true;
                    inputPhaseObservations.AddRange(inputPhasePrelude);
                    inputPhasePrelude.Clear();
                    inputPhasePostBudget = 512;
                    return;
                }

                if (inputPhasePostBudget <= 0)
                {
                    return;
                }

                inputPhaseObservations.Add(observation);
                inputPhasePostBudget--;
            }
            catch (Exception exception)
            {
                if (string.IsNullOrEmpty(inputPhaseObservationError))
                {
                    inputPhaseObservationError =
                        exception.GetType().Name
                        + ": "
                        + exception.Message;
                }
            }
        }

        private void UnregisterHooks()
        {
            if (!hooksRegistered)
            {
                return;
            }

            hooksRegistered = false;
            On.InControl.PlayerActionSet.Update -=
                OnPlayerActionSetUpdate;
            On.InControl.InputManager.UpdateInternal -=
                OnInputManagerUpdateInternal;
            On.HutongGames.PlayMaker.Actions.ListenForRight.OnUpdate -=
                OnListenForRightUpdate;
            On.HutongGames.PlayMaker.Fsm.Event_FsmEvent -=
                OnFsmEvent;
            On.HutongGames.PlayMaker.Fsm.EnterState -=
                OnFsmEnterState;
            CompletedFrameBoundarySignal.Reached -=
                OnCompletedFrameBoundary;
        }

        private static HutongGames.PlayMaker.Fsm? FindFixtureBenchFsm()
        {
            var gameObject = GameObject.Find("RestBench (1)");
            if (gameObject == null)
            {
                return null;
            }

            foreach (var component in
                     gameObject.GetComponents<PlayMakerFSM>())
            {
                if (string.Equals(
                        component.FsmName,
                        "Bench Control",
                        StringComparison.Ordinal))
                {
                    return component.Fsm;
                }
            }

            return null;
        }

        private static bool IsFixtureBenchFsm(
            HutongGames.PlayMaker.Fsm fsm)
        {
            return fsm != null
                   && string.Equals(
                       fsm.Name,
                       "Bench Control",
                       StringComparison.Ordinal)
                   && string.Equals(
                       fsm.GameObject?.name,
                       "RestBench (1)",
                       StringComparison.Ordinal)
                   && string.Equals(
                       USceneManager.GetActiveScene().name,
                       "GG_Workshop",
                       StringComparison.Ordinal);
        }

        private static void WriteAtomic(string path, string value)
        {
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(
                    temporary,
                    value,
                    new UTF8Encoding(false, true));
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Replace(temporary, path, null);
                        }
                        else
                        {
                            File.Move(temporary, path);
                        }
                        return;
                    }
                    catch (IOException) when (attempt < 19)
                    {
                        System.Threading.Thread.Sleep(2);
                    }
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        private static void CaptureMenuSelection(
            out string selectedObject,
            out int selectedSaveSlot)
        {
            selectedObject = string.Empty;
            selectedSaveSlot = 0;
            var eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                return;
            }

            try
            {
                var selected = eventSystem.currentSelectedGameObject;
                if (selected == null)
                {
                    return;
                }

                selectedObject = selected.name ?? string.Empty;
                var slot = selected.GetComponent<SaveSlotButton>();
                if (slot == null)
                {
                    return;
                }

                selectedSaveSlot = Convert.ToInt32(
                    SaveSlotField.GetValue(slot),
                    CultureInfo.InvariantCulture) + 1;
            }
            catch (MissingReferenceException)
            {
                // Scene teardown can invalidate a Unity object between the
                // EventSystem lookup and its native name/component access.
                // Menu selection is diagnostic only, so report no selection
                // without aborting the read-only gameplay capture.
                selectedObject = string.Empty;
                selectedSaveSlot = 0;
            }
            catch (NullReferenceException)
            {
                // Unity 2017 can surface the same destroyed-native-object
                // race as a managed NullReferenceException from GetName.
                selectedObject = string.Empty;
                selectedSaveSlot = 0;
            }
        }

        private static bool HasMenuInputEdge()
        {
            var actions = InputHandler.Instance?.inputActions;
            return actions != null
                   && (actions.up.WasPressed
                       || actions.down.WasPressed
                       || actions.left.WasPressed
                       || actions.right.WasPressed
                       || actions.menuSubmit.WasPressed
                       || actions.menuCancel.WasPressed);
        }

        private static string FormatBindings(InControl.PlayerAction action)
        {
            return string.Join(
                "|",
                System.Linq.Enumerable.Select(
                    action.UnfilteredBindings,
                    binding => binding.Name ?? string.Empty));
        }

        private static int PositiveModulo(int value, int modulo)
        {
            var remainder = value % modulo;
            return remainder < 0 ? remainder + modulo : remainder;
        }

        private static void AppendString(
            StringBuilder builder,
            string value)
        {
            builder.Append('"');
            foreach (var character in value ?? string.Empty)
            {
                switch (character)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    default:
                        builder.Append(character);
                        break;
                }
            }
            builder.Append('"');
        }

        private static void AppendFloat32(
            StringBuilder builder,
            float value)
        {
            var bits = unchecked(
                (uint)BitConverter.ToInt32(
                    BitConverter.GetBytes(value),
                    0));
            builder.Append("{\"value\":");
            builder.Append(
                value.ToString("R", CultureInfo.InvariantCulture));
            builder.Append(",\"canonicalHex\":\"");
            builder.Append(
                bits.ToString("x8", CultureInfo.InvariantCulture));
            builder.Append("\"}");
        }

        private static void AppendFloat64(
            StringBuilder builder,
            double value)
        {
            var bits = unchecked(
                (ulong)BitConverter.DoubleToInt64Bits(value));
            builder.Append("{\"value\":");
            builder.Append(
                value.ToString("R", CultureInfo.InvariantCulture));
            builder.Append(",\"canonicalHex\":\"");
            builder.Append(
                bits.ToString("x16", CultureInfo.InvariantCulture));
            builder.Append("\"}");
        }

        private static void AppendInt64Hex(
            StringBuilder builder,
            long value)
        {
            builder.Append('"');
            builder.Append(
                unchecked((ulong)value).ToString(
                    "x16",
                    CultureInfo.InvariantCulture));
            builder.Append('"');
        }

        private static void AppendInt32Hex(
            StringBuilder builder,
            int value)
        {
            builder.Append('"');
            builder.Append(
                unchecked((uint)value).ToString(
                    "x8",
                    CultureInfo.InvariantCulture));
            builder.Append('"');
        }

        private sealed class InputPhaseObservation
        {
            public InputPhaseObservation(
                long sequence,
                long visualTick,
                int frameIndex,
                string phase,
                ulong updateTick,
                ulong actionSetTick,
                ulong actionTick,
                bool held,
                bool pressed,
                bool released,
                string gameObject,
                string fsm,
                string state)
            {
                Sequence = sequence;
                VisualTick = visualTick;
                FrameIndex = frameIndex;
                Phase = phase;
                UpdateTick = updateTick;
                ActionSetTick = actionSetTick;
                ActionTick = actionTick;
                Held = held;
                Pressed = pressed;
                Released = released;
                GameObject = gameObject;
                Fsm = fsm;
                State = state;
            }

            public long Sequence { get; }
            public long VisualTick { get; }
            public int FrameIndex { get; }
            public string Phase { get; }
            public ulong UpdateTick { get; }
            public ulong ActionSetTick { get; }
            public ulong ActionTick { get; }
            public bool Held { get; }
            public bool Pressed { get; }
            public bool Released { get; }
            public string GameObject { get; }
            public string Fsm { get; }
            public string State { get; }

            public string Serialize()
            {
                var builder = new StringBuilder(384);
                builder.Append("{\"schemaVersion\":1,\"sequence\":");
                builder.Append(
                    Sequence.ToString(CultureInfo.InvariantCulture));
                builder.Append(",\"visualTick\":");
                builder.Append(
                    VisualTick.ToString(CultureInfo.InvariantCulture));
                builder.Append(",\"frameIndex\":");
                builder.Append(
                    FrameIndex.ToString(CultureInfo.InvariantCulture));
                builder.Append(",\"phase\":");
                AppendString(builder, Phase);
                builder.Append(",\"updateTick\":");
                builder.Append(
                    UpdateTick.ToString(CultureInfo.InvariantCulture));
                builder.Append(",\"actionSetTick\":");
                builder.Append(
                    ActionSetTick.ToString(CultureInfo.InvariantCulture));
                builder.Append(",\"actionTick\":");
                builder.Append(
                    ActionTick.ToString(CultureInfo.InvariantCulture));
                builder.Append(",\"held\":");
                builder.Append(Held ? "true" : "false");
                builder.Append(",\"pressed\":");
                builder.Append(Pressed ? "true" : "false");
                builder.Append(",\"released\":");
                builder.Append(Released ? "true" : "false");
                builder.Append(",\"gameObject\":");
                AppendString(builder, GameObject);
                builder.Append(",\"fsm\":");
                AppendString(builder, Fsm);
                builder.Append(",\"state\":");
                AppendString(builder, State);
                builder.Append('}');
                return builder.ToString();
            }
        }
    }

    [DefaultExecutionOrder(-32000)]
    internal sealed class ReferenceObserverEarlyRunner : MonoBehaviour
    {
        private ReferenceObserverSession? owner;

        public void Initialize(ReferenceObserverSession value)
        {
            owner = value;
        }

        private void Update()
        {
            owner?.OnEarlyUpdate();
        }

        private void FixedUpdate()
        {
            owner?.OnEarlyFixedUpdate();
        }
    }

    [DefaultExecutionOrder(31000)]
    internal sealed class ReferenceObserverLateRunner : MonoBehaviour
    {
        private ReferenceObserverSession? owner;

        public void Initialize(ReferenceObserverSession value)
        {
            owner = value;
        }

        private void LateUpdate()
        {
            owner?.OnLateUpdate();
        }
    }

    internal sealed class ReferenceObserverCompletedFrameRunner :
        MonoBehaviour
    {
        private ReferenceObserverSession? owner;
        private readonly WaitForEndOfFrame endOfFrame =
            new WaitForEndOfFrame();

        public void Initialize(ReferenceObserverSession value)
        {
            owner = value;
            StartCoroutine(PublishCompletedFrames());
        }

        private IEnumerator PublishCompletedFrames()
        {
            while (owner != null)
            {
                yield return endOfFrame;
                CompletedFrameBoundarySignal.Publish();
            }
        }
    }
}
