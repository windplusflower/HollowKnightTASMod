using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Modding;
using UnityEngine;
using UPlayerLoop = UnityEngine.LowLevel.PlayerLoop;
using PlayerLoopSystem = UnityEngine.LowLevel.PlayerLoopSystem;
using PostLateUpdate = UnityEngine.PlayerLoop.PostLateUpdate;
using TimeUpdate = UnityEngine.PlayerLoop.TimeUpdate;
using WaitForLastPresentationAndUpdateTime =
    UnityEngine.PlayerLoop.TimeUpdate.WaitForLastPresentationAndUpdateTime;
using USceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowKnightTAS.ClockPayload
{
    /// <summary>
    /// Fixed-build T24 environment payload. The external injector loads this
    /// exact assembly into both the reference and TAS processes. It changes
    /// Unity clock configuration. The T24 path seeds RNG only at its root and
    /// recording boundary. The separate full-run v2 path seeds at each scene's
    /// first input boundary and isolates rendering RNG so variable loading and
    /// rendering work cannot shift gameplay RNG. Scene transitions retain their
    /// native lifecycle. It never writes Hero, FSM, Animator,
    /// Rigidbody2D, enemy, or resource state; it only identifies the Hero
    /// action-set update boundary and never writes input state.
    /// </summary>
    public static class ClockController
    {
        public const string ProfileId =
            "external-unity-startup-continuous-clock-v40-native-scene-lifecycle";
        public const string VirtualClockProviderId =
            "native.clock.pause-wall-time-exclusion.experimental.v5";
        public const string RandomSynchronizationPolicyId =
            "unity-init-state-at-root-only-native-scene-lifecycle-v19";
        public const int RandomSynchronizationSeed = 1212896321;
        public const string RealtimeEpochNormalizationPolicyId =
            "root-game-minus-rounded-startup-offset-qpc-grid-v3";
        private const int MaximumDoublePhaseCalibrationAttempts = 128;
        private const int MaximumRealtimeEpochNormalizationAttempts = 32;
        private const float RecordingPhaseNormalizationCaptureDeltaTime =
            1.0e-20f;
        private static float RecordingAbsoluteTimeTarget = 768f;
        public static double ConfiguredRecordingRootSeconds => RecordingAbsoluteTimeTarget;
        public static string RecordingPhaseNormalizationDiagnostic { get; private set; } = string.Empty;
        private static bool recordingRootConfigured;
        private static bool recordingClockCalibrationRequested;

        public static void BeginRecordingClockCalibration()
        {
            if (recordingClockCalibrationRequested) return;
            if (recordingRootConfigured || randomSynchronizationRequested
                || randomSynchronizationApplied || recordingRandomSynchronizationApplied)
                throw new InvalidOperationException("Clock calibration must precede the recording root.");
            recordingClockCalibrationRequested = true;
            calibrationAttempts = 0;
            doublePhaseInitialResidualCaptured = false;
            doublePhaseCalibrationApplied = false;
            doublePhaseCalibrationFaultCode = 0;
            applied = false;
        }

        public static void CancelRecordingClockCalibration()
        {
            if (!recordingClockCalibrationRequested) return;
            recordingClockCalibrationRequested = false;
            Time.captureDeltaTime = Time.fixedDeltaTime;
            applied = true;
        }

        public static void ConfigureRecordingRoot(double seconds)
        {
            HollowKnightTAS.Core.Ipc.RecordingRootConfiguration.Validate(seconds);
            if (recordingRootConfigured)
            {
                if (seconds != ConfiguredRecordingRootSeconds)
                    throw new InvalidOperationException("Recording root is immutable once configured.");
                return;
            }
            if (randomSynchronizationRequested || randomSynchronizationApplied
                || recordingPhaseNormalizationActive || recordingRandomSynchronizationApplied)
                throw new InvalidOperationException("Recording root cannot change after the root handshake starts.");
            if (seconds <= Time.timeAsDouble + 1d)
                throw new InvalidOperationException("Insufficient time remains to prepare the saved recording root.");
            RecordingAbsoluteTimeTarget = (float)seconds;
            recordingRootConfigured = true;
            recordingClockCalibrationRequested = false;
        }
        private const int SceneFramePhaseModulo = 4;
        private const int SceneFramePhaseTarget = 0;
        private static readonly FieldInfo RandomState0Field =
            RandomStateField("s0");
        private static readonly FieldInfo RandomState1Field =
            RandomStateField("s1");
        private static readonly FieldInfo RandomState2Field =
            RandomStateField("s2");
        private static readonly FieldInfo RandomState3Field =
            RandomStateField("s3");

        private static int registered;
        private static bool applied;
        private static bool randomSynchronizationApplied;
        private static bool randomSynchronizationRequested;
        private static bool originalCaptured;
        private static int calibrationAttempts;
        private static bool doublePhaseInitialResidualCaptured;
        private static bool doublePhaseCalibrationApplied;
        private static long doublePhaseInitialResidualBits;
        private static long doublePhaseFinalResidualBits;
        private static int doublePhaseDownwardQuantizationCount;
        private static int doublePhaseLastCorrectionBits;
        private static int doublePhaseCalibrationFaultCode;
        private static int beforeCaptureDeltaTimeBits;
        private static int beforeTargetFrameRate;
        private static int beforeVSyncCount;
        private static string calibratedScene = string.Empty;
        private static EventWaitHandle? randomSynchronizationRequest;
        private static EventWaitHandle? randomSynchronizationAcknowledged;
        private static EventWaitHandle? fullRunRandomRequest;
        private static EventWaitHandle? fullRunRandomAcknowledged;
        private static bool fullRunRenderIsolationEnabled;
        private static bool fullRunRenderInitialized;
        private static bool fullRunRenderEntered;
        private static UnityEngine.Random.State fullRunRenderState;
        private static EventWaitHandle?
            recordingRandomSynchronizationRequest;
        private static EventWaitHandle?
            recordingRandomSynchronizationAcknowledged;
        private static bool recordingRandomSynchronizationRequested;
        private static bool recordingRandomSynchronizationApplied;
        private static int recordingRootRequestObservedFrameCount = -1;
        private static int recordingRootFramePhase = -1;
        private static bool recordingPhaseNormalizationActive;
        private static bool recordingPhaseNormalizationCompleted;
        private static bool recordingPhaseNormalizationLatchPending;
        private static int recordingPhaseNormalizationCaptureDeltaTimeBits;
        private static int recordingPhaseNormalizationHeldTimeBits;
        private static long recordingPhaseNormalizationHeldTimeDoubleBits;
        private static int recordingPhaseNormalizationRemainingNormalFrameCount =
            -1;
        private static int recordingPhaseNormalizationRestoreFramePhase = -1;
        private static int recordingPhaseNormalizationBeginCount;
        private static int recordingPhaseNormalizationHoldFrameCount;
        private static int recordingPhaseNormalizationReleaseCount;
        private static int recordingPhaseNormalizationLastFrameCount = -1;
        private static int recordingPhaseNormalizationLastFramePhase = -1;
        private static int recordingPhaseNormalizationFaultCode;
        private static bool randomSynchronizationSnapshotAvailable;
        private static int randomSynchronizationStateS0;
        private static int randomSynchronizationStateS1;
        private static int randomSynchronizationStateS2;
        private static int randomSynchronizationStateS3;
        private static int randomSynchronizationResetCount;
        private static int randomSynchronizationTransitionStartCount;
        private static int randomSynchronizationGameplayReadyCount;
        private static int randomSynchronizationFirstGameplayReadyFrameCount =
            -1;
        private static int randomSynchronizationFirstGameplayReadyFramePhase =
            -1;
        private static int
            randomSynchronizationGameplayReadyAlignmentHoldUpdateCount;
        private static int randomSynchronizationSceneEpoch;
        private static int randomSynchronizationLastAppliedSeed;
        private static string randomSynchronizationLastBoundary =
            string.Empty;
        private static string randomSynchronizationLastScene = string.Empty;
        private static int randomSynchronizationFaultCode;
        private static bool sceneRandomSynchronizationPending;
        private static bool sceneClockExclusionActive;
        private static int sceneClockExclusionCaptureDeltaTimeBits;
        private static int sceneClockExclusionBeginCount;
        private static int sceneClockExclusionFinishCount;
        private static int sceneClockExclusionFrozenTimeUpdateCount;
        private static int sceneClockExclusionFaultCode;
        private static int sceneClockExclusionPreSynchronizationCount;
        private static int sceneActivationAlignmentBeginCount;
        private static int sceneActivationAlignmentHoldFrameCount;
        private static int sceneActivationAlignmentReleaseCount;
        private static int sceneActivationAlignmentFirstFrameCount = -1;
        private static int sceneActivationAlignmentFirstFramePhase = -1;
        private static int sceneActivationAlignmentLastFrameCount = -1;
        private static int sceneActivationAlignmentLastFramePhase = -1;
        private static int sceneFinishAlignmentBeginCount;
        private static int sceneFinishAlignmentHoldFrameCount;
        private static int sceneFinishAlignmentReleaseCount;
        private static int sceneFinishAlignmentLastFrameCount = -1;
        private static int sceneFinishAlignmentLastFramePhase = -1;
        private static int sceneFrameAlignmentFaultCode;
        private static bool runtimeVirtualClockRegistered;
        private static Type? runtimeVirtualClockBoundaryType;
        private static bool timeUpdateResumeBoundaryInstalled;
        private static int timeUpdateResumeBoundaryInstallCount;
        private static int timeUpdateResumeBoundaryCallbackCount;
        private static int timeUpdateResumeBoundaryCommitCount;
        private static int timeUpdateResumeRequested;
        private static int timeUpdateResumeCommitFaultCode;
        private static bool deterministicClockEnabled;
        private static int deterministicClockEnableFaultCode;
        private static int deterministicClockAdvanceFaultCode;
        private static int deterministicClockAdvanceSequence;
        private static bool deterministicClockUnityFrameObserved;
        private static int deterministicClockLastUnityFrameCount;
        private static int deterministicClockDuplicateTimeUpdateSkipCount;
        private static int deterministicClockSceneLoadFrameSkipCount;
        private static int deterministicClockUnityFrameFaultCode;
        private static bool realtimeEpochNormalizationApplied;
        private static int realtimeEpochNormalizationCount;
        private static long realtimeEpochNormalizationGameTimeBits;
        private static long realtimeEpochNormalizationBeforeBits;
        private static long realtimeEpochNormalizationTargetBits;
        private static long realtimeEpochNormalizationAfterBits;
        private static long realtimeEpochNormalizationCanonicalOffsetSeconds;
        private static long realtimeEpochNormalizationDeltaTicks;
        private static int realtimeEpochNormalizationFaultCode;
        private static int playerLoopPostLateUpdateIndex = -1;
        private static int playerLoopTimeUpdateIndex = -1;
        private static int playerLoopResumeBoundaryIndex = -1;
        private static int playerLoopWaitForPresentationIndex = -1;
        private static string playerLoopBoundaryError = string.Empty;

        public static bool Applied => applied;
        public static bool RandomSynchronizationSnapshotAvailable =>
            randomSynchronizationSnapshotAvailable;
        public static bool RandomSynchronizationRequested =>
            randomSynchronizationRequested;
        public static bool RecordingRandomSynchronizationRequested =>
            recordingRandomSynchronizationRequested;
        public static bool RecordingRandomSynchronizationApplied =>
            recordingRandomSynchronizationApplied;
        public static int RandomSynchronizationStateS0 =>
            randomSynchronizationStateS0;
        public static int RandomSynchronizationStateS1 =>
            randomSynchronizationStateS1;
        public static int RandomSynchronizationStateS2 =>
            randomSynchronizationStateS2;
        public static int RandomSynchronizationStateS3 =>
            randomSynchronizationStateS3;
        public static string ActiveProfileId => ProfileId;
        public static string ActiveRandomSynchronizationPolicyId =>
            RandomSynchronizationPolicyId;
        public static int RandomSynchronizationResetCount =>
            randomSynchronizationResetCount;
        public static int RandomSynchronizationTransitionStartCount =>
            randomSynchronizationTransitionStartCount;
        public static int RandomSynchronizationGameplayReadyCount =>
            randomSynchronizationGameplayReadyCount;
        public static int RandomSynchronizationFirstGameplayReadyFrameCount =>
            randomSynchronizationFirstGameplayReadyFrameCount;
        public static int RandomSynchronizationFirstGameplayReadyFramePhase =>
            randomSynchronizationFirstGameplayReadyFramePhase;
        public static int
            RandomSynchronizationGameplayReadyAlignmentHoldUpdateCount =>
                randomSynchronizationGameplayReadyAlignmentHoldUpdateCount;
        public static int RandomSynchronizationSceneEpoch =>
            randomSynchronizationSceneEpoch;
        public static int RandomSynchronizationLastAppliedSeed =>
            randomSynchronizationLastAppliedSeed;
        public static string RandomSynchronizationLastBoundary =>
            randomSynchronizationLastBoundary;
        public static string RandomSynchronizationLastScene =>
            randomSynchronizationLastScene;
        public static int RandomSynchronizationFaultCode =>
            randomSynchronizationFaultCode;
        public static bool SceneRandomSynchronizationPending =>
            sceneRandomSynchronizationPending;
        public static bool SceneClockExclusionActive =>
            sceneClockExclusionActive;
        public static int SceneClockExclusionBeginCount =>
            sceneClockExclusionBeginCount;
        public static int SceneClockExclusionFinishCount =>
            sceneClockExclusionFinishCount;
        public static int SceneClockExclusionFrozenTimeUpdateCount =>
            sceneClockExclusionFrozenTimeUpdateCount;
        public static int SceneClockExclusionFaultCode =>
            sceneClockExclusionFaultCode;
        public static int SceneClockExclusionPreSynchronizationCount =>
            sceneClockExclusionPreSynchronizationCount;
        public static int SceneFramePhaseModuloValue => SceneFramePhaseModulo;
        public static int SceneFramePhaseTargetValue => SceneFramePhaseTarget;
        public static int RecordingRootRequestObservedFrameCount =>
            recordingRootRequestObservedFrameCount;
        public static int RecordingRootFramePhase => recordingRootFramePhase;
        public static bool RecordingPhaseNormalizationActive =>
            recordingPhaseNormalizationActive;
        public static bool RecordingPhaseNormalizationCompleted =>
            recordingPhaseNormalizationCompleted;
        public static int RecordingPhaseNormalizationBeginCount =>
            recordingPhaseNormalizationBeginCount;
        public static int RecordingPhaseNormalizationHoldFrameCount =>
            recordingPhaseNormalizationHoldFrameCount;
        public static int RecordingPhaseNormalizationReleaseCount =>
            recordingPhaseNormalizationReleaseCount;
        public static int RecordingPhaseNormalizationLastFrameCount =>
            recordingPhaseNormalizationLastFrameCount;
        public static int RecordingPhaseNormalizationLastFramePhase =>
            recordingPhaseNormalizationLastFramePhase;
        public static int RecordingPhaseNormalizationHeldTimeBits =>
            recordingPhaseNormalizationHeldTimeBits;
        public static long RecordingPhaseNormalizationHeldTimeDoubleBits =>
            recordingPhaseNormalizationHeldTimeDoubleBits;
        public static int RecordingPhaseNormalizationRemainingNormalFrameCount =>
            recordingPhaseNormalizationRemainingNormalFrameCount;
        public static int RecordingPhaseNormalizationRestoreFramePhase =>
            recordingPhaseNormalizationRestoreFramePhase;
        public static int RecordingPhaseNormalizationFaultCode =>
            recordingPhaseNormalizationFaultCode;
        public static int SceneActivationAlignmentBeginCount =>
            sceneActivationAlignmentBeginCount;
        public static int SceneActivationAlignmentHoldFrameCount =>
            sceneActivationAlignmentHoldFrameCount;
        public static int SceneActivationAlignmentReleaseCount =>
            sceneActivationAlignmentReleaseCount;
        public static int SceneActivationAlignmentFirstFrameCount =>
            sceneActivationAlignmentFirstFrameCount;
        public static int SceneActivationAlignmentFirstFramePhase =>
            sceneActivationAlignmentFirstFramePhase;
        public static int SceneActivationAlignmentLastFrameCount =>
            sceneActivationAlignmentLastFrameCount;
        public static int SceneActivationAlignmentLastFramePhase =>
            sceneActivationAlignmentLastFramePhase;
        public static int SceneFinishAlignmentBeginCount =>
            sceneFinishAlignmentBeginCount;
        public static int SceneFinishAlignmentHoldFrameCount =>
            sceneFinishAlignmentHoldFrameCount;
        public static int SceneFinishAlignmentReleaseCount =>
            sceneFinishAlignmentReleaseCount;
        public static int SceneFinishAlignmentLastFrameCount =>
            sceneFinishAlignmentLastFrameCount;
        public static int SceneFinishAlignmentLastFramePhase =>
            sceneFinishAlignmentLastFramePhase;
        public static int SceneFrameAlignmentFaultCode =>
            sceneFrameAlignmentFaultCode;
        public static bool RuntimeVirtualClockRegistered =>
            runtimeVirtualClockRegistered;
        public static uint BridgeAbi => HktasClockBridge_GetAbi();
        public static int BridgeStatus => HktasClockBridge_GetStatus();
        public static int VirtualClockPaused =>
            HktasClockBridge_GetVirtualClockPaused();
        public static int VirtualClockPauseCount =>
            HktasClockBridge_GetVirtualClockPauseCount();
        public static int VirtualClockResumePending =>
            HktasClockBridge_GetVirtualClockResumePending();
        public static int VirtualClockResumeRequestCount =>
            HktasClockBridge_GetVirtualClockResumeRequestCount();
        public static int VirtualClockResumeCount =>
            HktasClockBridge_GetVirtualClockResumeCount();
        public static bool DeterministicClockEnabled =>
            HktasClockBridge_GetDeterministicClockEnabled() == 1;
        public static long DeterministicClockFrequency =>
            HktasClockBridge_GetDeterministicClockFrequency();
        public static long DeterministicClockStepTicks =>
            HktasClockBridge_GetDeterministicClockStepTicks();
        public static long DeterministicClockAnchor =>
            HktasClockBridge_GetDeterministicClockAnchor();
        public static int DeterministicClockFrameAdvanceCount =>
            HktasClockBridge_GetDeterministicClockFrameAdvanceCount();
        public static bool StartupHookInstalled =>
            HktasClockBridge_GetStartupHookInstalled() == 1;
        public static bool StartupLatchEnabled =>
            HktasClockBridge_GetStartupLatchEnabled() == 1;
        public static uint StartupHookThreadId =>
            HktasClockBridge_GetStartupHookThreadId();
        public static int StartupVirtualQpcCallCount =>
            HktasClockBridge_GetStartupVirtualQpcCallCount();
        public static int StartupHandoffAdoptCount =>
            HktasClockBridge_GetStartupHandoffAdoptCount();
        public static int StartupFaultCode =>
            HktasClockBridge_GetStartupFaultCode();
        public static int DeterministicClockEnableFaultCode =>
            Volatile.Read(ref deterministicClockEnableFaultCode);
        public static int DeterministicClockAdvanceFaultCode =>
            Volatile.Read(ref deterministicClockAdvanceFaultCode);
        public static int DeterministicClockLastUnityFrameCount =>
            deterministicClockLastUnityFrameCount;
        public static int DeterministicClockDuplicateTimeUpdateSkipCount =>
            deterministicClockDuplicateTimeUpdateSkipCount;
        public static int DeterministicClockSceneLoadFrameSkipCount =>
            deterministicClockSceneLoadFrameSkipCount;
        public static int DeterministicClockUnityFrameFaultCode =>
            deterministicClockUnityFrameFaultCode;
        public static string ActiveRealtimeEpochNormalizationPolicyId =>
            RealtimeEpochNormalizationPolicyId;
        public static bool RealtimeEpochNormalizationApplied =>
            realtimeEpochNormalizationApplied;
        public static int RealtimeEpochNormalizationCount =>
            realtimeEpochNormalizationCount;
        public static long RealtimeEpochNormalizationGameTimeBits =>
            realtimeEpochNormalizationGameTimeBits;
        public static long RealtimeEpochNormalizationBeforeBits =>
            realtimeEpochNormalizationBeforeBits;
        public static long RealtimeEpochNormalizationTargetBits =>
            realtimeEpochNormalizationTargetBits;
        public static long RealtimeEpochNormalizationAfterBits =>
            realtimeEpochNormalizationAfterBits;
        public static long RealtimeEpochNormalizationCanonicalOffsetSeconds =>
            realtimeEpochNormalizationCanonicalOffsetSeconds;
        public static long RealtimeEpochNormalizationDeltaTicks =>
            realtimeEpochNormalizationDeltaTicks;
        public static int RealtimeEpochNormalizationFaultCode =>
            realtimeEpochNormalizationFaultCode;
        public static bool TimeUpdateResumeBoundaryInstalled =>
            timeUpdateResumeBoundaryInstalled;
        public static int TimeUpdateResumeBoundaryInstallCount =>
            Interlocked.CompareExchange(
                ref timeUpdateResumeBoundaryInstallCount,
                0,
                0);
        public static int TimeUpdateResumeBoundaryCallbackCount =>
            Interlocked.CompareExchange(
                ref timeUpdateResumeBoundaryCallbackCount,
                0,
                0);
        public static int TimeUpdateResumeBoundaryCommitCount =>
            Interlocked.CompareExchange(
                ref timeUpdateResumeBoundaryCommitCount,
                0,
                0);
        public static int TimeUpdateResumeCommitFaultCode =>
            Interlocked.CompareExchange(
                ref timeUpdateResumeCommitFaultCode,
                0,
                0);
        public static int PlayerLoopPostLateUpdateIndex =>
            playerLoopPostLateUpdateIndex;
        public static int PlayerLoopTimeUpdateIndex =>
            playerLoopTimeUpdateIndex;
        public static int PlayerLoopResumeBoundaryIndex =>
            playerLoopResumeBoundaryIndex;
        public static int PlayerLoopWaitForPresentationIndex =>
            playerLoopWaitForPresentationIndex;
        public static string PlayerLoopBoundaryError =>
            playerLoopBoundaryError;
        public static bool DoublePhaseCalibrationApplied =>
            doublePhaseCalibrationApplied;
        public static int DoublePhaseCalibrationAttempts =>
            calibrationAttempts;
        public static long DoublePhaseInitialResidualBits =>
            doublePhaseInitialResidualBits;
        public static long DoublePhaseFinalResidualBits =>
            doublePhaseFinalResidualBits;
        public static int DoublePhaseDownwardQuantizationCount =>
            doublePhaseDownwardQuantizationCount;
        public static int DoublePhaseLastCorrectionBits =>
            doublePhaseLastCorrectionBits;
        public static int DoublePhaseCalibrationFaultCode =>
            doublePhaseCalibrationFaultCode;

        public static void Bootstrap()
        {
            if (Interlocked.Exchange(ref registered, 1) != 0)
            {
                return;
            }

            InitializeRandomSynchronization();
            fullRunRenderIsolationEnabled = string.Equals(
                Environment.GetEnvironmentVariable("HKTAS_FULL_RUN_V2"),
                "1", StringComparison.Ordinal);
            if (fullRunRenderIsolationEnabled)
                On.UnityStandardAssets.ImageEffects.FastNoise.DrawNoiseQuadGrid +=
                    OnFullRunDrawNoise;
            TryInstallTimeUpdateResumeBoundary();
            On.GameManager.Update += OnGameManagerUpdate;
            On.InControl.PlayerActionSet.Update +=
                OnPlayerActionSetUpdate;
            ModHooks.HeroUpdateHook += OnHeroUpdate;
            ModHooks.ApplicationQuitHook += OnApplicationQuit;
            TryRegisterRuntimeVirtualClockBoundary();
        }

        private static void OnGameManagerUpdate(
            On.GameManager.orig_Update original,
            GameManager self)
        {
            TryApply();
            original(self);
        }

        private static void OnHeroUpdate()
        {
            TryApply();
        }

        private static void TryApply()
        {
            var fixedDeltaTime = Time.fixedDeltaTime;
            if (float.IsNaN(fixedDeltaTime)
                || float.IsInfinity(fixedDeltaTime)
                || fixedDeltaTime <= 0f)
            {
                return;
            }

            var targetFrameRate = checked(
                (int)Math.Round(
                    1d / fixedDeltaTime,
                    MidpointRounding.AwayFromZero));
            if (targetFrameRate <= 0 || targetFrameRate > 1000)
            {
                return;
            }


            TryEnableDeterministicMainThreadClock(
                fixedDeltaTime,
                targetFrameRate);
            TryRegisterRuntimeVirtualClockBoundary();

            if (!originalCaptured)
            {
                beforeCaptureDeltaTimeBits =
                    FloatBits.FromSingle(Time.captureDeltaTime);
                beforeTargetFrameRate = Application.targetFrameRate;
                beforeVSyncCount = QualitySettings.vSyncCount;
                originalCaptured = true;
            }

            var sceneName =
                USceneManager.GetActiveScene().name ?? string.Empty;
            if (!string.Equals(
                    calibratedScene,
                    sceneName,
                    StringComparison.Ordinal))
            {
                calibratedScene = sceneName;
                calibrationAttempts = 0;
                doublePhaseInitialResidualCaptured = false;
                doublePhaseCalibrationApplied = false;
                doublePhaseInitialResidualBits = 0;
                doublePhaseFinalResidualBits = 0;
                doublePhaseDownwardQuantizationCount = 0;
                doublePhaseLastCorrectionBits = 0;
                doublePhaseCalibrationFaultCode = 0;
                applied = false;
            }

            QualitySettings.vSyncCount = 0;
            if (fullRunRenderIsolationEnabled)
            {
                // Full-run QPC timing owns gameplay time and playback pacing.
                // A fixed capture step bypasses that clock and desynchronizes MP4.
                Time.captureDeltaTime = 0f;
                applied = true;
                return;
            }
            Application.targetFrameRate = targetFrameRate;
            if (applied)
            {
                // TryApply runs from more than one vanilla hook. Once the
                // recording-root phase normalizer owns captureDeltaTime, every
                // maintenance pass must preserve its phase-only delta instead
                // of overwriting it with the ordinary fixed step.
                Time.captureDeltaTime = recordingPhaseNormalizationActive
                    ? RecordingPhaseNormalizationCaptureDeltaTime
                    : fixedDeltaTime;
                return;
            }
            if (!RecordingClockCalibrationPolicy.IsAllowed(
                    recordingClockCalibrationRequested,
                    fullRunRenderIsolationEnabled || recordingRootConfigured || randomSynchronizationRequested
                        || randomSynchronizationApplied || recordingRandomSynchronizationApplied,
                    string.Equals(sceneName, "GG_Workshop", StringComparison.Ordinal)))
            {
                calibrationAttempts = 0;
                Time.captureDeltaTime = fixedDeltaTime;
                applied = true;
                return;
            }

            var fixedRemainder =
                Time.timeAsDouble - Time.fixedTimeAsDouble;
            if (double.IsNaN(fixedRemainder)
                || double.IsInfinity(fixedRemainder))
            {
                doublePhaseCalibrationFaultCode = -1;
                applied = false;
                return;
            }
            if (!doublePhaseInitialResidualCaptured)
            {
                doublePhaseInitialResidualBits =
                    BitConverter.DoubleToInt64Bits(fixedRemainder);
                doublePhaseInitialResidualCaptured = true;
            }
            doublePhaseFinalResidualBits =
                BitConverter.DoubleToInt64Bits(fixedRemainder);
            if (doublePhaseFinalResidualBits != 0L)
            {
                if (calibrationAttempts
                    >= MaximumDoublePhaseCalibrationAttempts)
                {
                    doublePhaseCalibrationFaultCode = -2;
                    applied = false;
                    return;
                }
                if (!DoublePhaseCorrection.TryCalculate(
                        fixedRemainder,
                        fixedDeltaTime,
                        out var correctionFloat,
                        out var adjustedToPredecessor,
                        out var correctionFaultCode))
                {
                    doublePhaseCalibrationFaultCode = correctionFaultCode;
                    applied = false;
                    return;
                }
                if (adjustedToPredecessor)
                {
                    doublePhaseDownwardQuantizationCount++;
                }
                applied = false;
                calibrationAttempts++;
                doublePhaseLastCorrectionBits =
                    FloatBits.FromSingle(correctionFloat);
                Time.captureDeltaTime = correctionFloat;
                return;
            }
            doublePhaseCalibrationApplied = true;
            Time.captureDeltaTime = fixedDeltaTime;

            applied =
                FloatBits.FromSingle(Time.captureDeltaTime)
                == FloatBits.FromSingle(fixedDeltaTime)
                && Application.targetFrameRate == targetFrameRate
                && QualitySettings.vSyncCount == 0
                && doublePhaseCalibrationApplied
                && doublePhaseCalibrationFaultCode == 0;
            // Keep observing the read-only clock phase. Scene loading can
            // move Unity's Time.time/Time.fixedTime remainder by one fixed
            // step after the title-screen profile is applied; calibration is
            // limited to the committed T24 fixture before recording begins.
        }

        private static void OnPlayerActionSetUpdate(
            On.InControl.PlayerActionSet.orig_Update original,
            InControl.PlayerActionSet self,
            ulong updateTick,
            float deltaTime)
        {
            if (self is HeroActions)
                TrySynchronizeFullRunRandom();
            if (ReferenceEquals(InputHandler.Instance?.inputActions, self))
            {
                TrySynchronizeRandom();
                // The observer emits the recording-root request from the exact
                // 768.00 / global-phase-0 completed frame. Consume that request
                // before the following normal TimeUpdate is audited; otherwise
                // expected post-root progress to 768.02 is misclassified as a
                // missed normalization boundary.
                TrySynchronizeRecordingRandom();
                TryNormalizeRecordingBoundaryPhase();
            }
            original(self, updateTick, deltaTime);
        }


        private static int PositiveModulo(int value, int modulo)
        {
            var result = value % modulo;
            return result < 0 ? result + modulo : result;
        }


        private static void OnApplicationQuit()
        {
            Restore();
        }

        private static void InitializeRandomSynchronization()
        {
            var runId = ReadReferenceRunId();
            if (string.IsNullOrEmpty(runId))
            {
                return;
            }

            var prefix = "HollowKnightTAS.T24.RngSync." + runId;
            randomSynchronizationRequest = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                prefix + ".request");
            randomSynchronizationAcknowledged = new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                prefix + ".applied");
            recordingRandomSynchronizationRequest = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                prefix + ".recording-request");
            recordingRandomSynchronizationAcknowledged =
                new EventWaitHandle(
                    false,
                    EventResetMode.ManualReset,
                    prefix + ".recording-applied");
            if (string.Equals(Environment.GetEnvironmentVariable("HKTAS_FULL_RUN_V2"),
                    "1", StringComparison.Ordinal))
            {
                var fullRunPrefix = "HollowKnightTAS.V2.RngSync." + runId;
                fullRunRandomRequest = new EventWaitHandle(false,
                    EventResetMode.AutoReset, fullRunPrefix + ".request");
                fullRunRandomAcknowledged = new EventWaitHandle(false,
                    EventResetMode.ManualReset, fullRunPrefix + ".applied");
            }
        }

        private static void TrySynchronizeFullRunRandom()
        {
            if (fullRunRandomRequest?.WaitOne(0) != true)
                return;
            ApplyRandomSynchronization(RandomSynchronizationSeed,
                "full-run-scene", USceneManager.GetActiveScene().name ?? string.Empty,
                randomSynchronizationResetCount);
            fullRunRandomAcknowledged?.Set();
        }

        private static void OnFullRunDrawNoise(
            On.UnityStandardAssets.ImageEffects.FastNoise.orig_DrawNoiseQuadGrid original,
            RenderTexture source, RenderTexture destination, Material material,
            Texture2D noise, int pass, int frameMultiple)
        {
            if (!fullRunRenderIsolationEnabled || randomSynchronizationResetCount == 0
                || fullRunRenderEntered)
            {
                original(source, destination, material, noise, pass, frameMultiple);
                return;
            }
            var gameplayState = UnityEngine.Random.state;
            if (!fullRunRenderInitialized)
            {
                fullRunRenderState = gameplayState;
                fullRunRenderInitialized = true;
            }
            fullRunRenderEntered = true;
            try
            {
                UnityEngine.Random.state = fullRunRenderState;
                original(source, destination, material, noise, pass, frameMultiple);
            }
            finally
            {
                try { fullRunRenderState = UnityEngine.Random.state; }
                finally
                {
                    try { UnityEngine.Random.state = gameplayState; }
                    finally { fullRunRenderEntered = false; }
                }
            }
        }

        private static void TryRegisterRuntimeVirtualClockBoundary()
        {
            if (HktasClockBridge_GetAbi() != 10u
                || !timeUpdateResumeBoundaryInstalled
                || !deterministicClockEnabled
                || Volatile.Read(ref deterministicClockEnableFaultCode) != 0
                || Volatile.Read(ref deterministicClockAdvanceFaultCode) != 0)
            {
                return;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.Equals(
                        assembly.GetName().Name,
                        "HollowKnightTAS",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var type = assembly.GetType(
                    "HollowKnightTAS.Runtime.Control."
                    + "RuntimeVirtualClockBoundary",
                    throwOnError: false,
                    ignoreCase: false);
                var register = type?.GetMethod(
                    "Register",
                    BindingFlags.Public | BindingFlags.Static);
                if (register == null)
                {
                    return;
                }

                var registered = register.Invoke(
                    null,
                    new object[]
                    {
                        VirtualClockProviderId,
                        new Action(BeginMainThreadPause),
                        new Action(EndMainThreadPause)
                    });
                if (registered is bool value && value)
                {
                    runtimeVirtualClockBoundaryType = type;
                    runtimeVirtualClockRegistered = true;
                }
                return;
            }
        }

        private static void BeginMainThreadPause()
        {
            if (!timeUpdateResumeBoundaryInstalled
                || Volatile.Read(ref timeUpdateResumeRequested) != 0
                || HktasClockBridge_GetVirtualClockResumePending() != 0
                || Volatile.Read(ref timeUpdateResumeCommitFaultCode) != 0)
            {
                throw new InvalidOperationException(
                    "The TimeUpdate resume boundary is not clean at pause begin.");
            }
            var result = HktasClockBridge_BeginMainThreadPause();
            if (result < 0)
            {
                throw new InvalidOperationException(
                    "Clock Bridge could not begin the main-thread pause: "
                    + result);
            }
        }

        private static void EndMainThreadPause()
        {
            if (!timeUpdateResumeBoundaryInstalled
                || Volatile.Read(ref timeUpdateResumeRequested) != 0
                || Volatile.Read(ref timeUpdateResumeCommitFaultCode) != 0)
            {
                throw new InvalidOperationException(
                    "The TimeUpdate resume boundary cannot accept a request.");
            }
            var result = HktasClockBridge_EndMainThreadPause();
            if (result != 1)
            {
                throw new InvalidOperationException(
                    "Clock Bridge could not request the main-thread resume: "
                    + result);
            }
            Volatile.Write(ref timeUpdateResumeRequested, 1);
        }

        private static void TryInstallTimeUpdateResumeBoundary()
        {
            try
            {
                var loop = UPlayerLoop.GetCurrentPlayerLoop();
                var topLevel = loop.subSystemList;
                if (topLevel == null)
                {
                    playerLoopBoundaryError =
                        "Current PlayerLoop has no top-level systems.";
                    return;
                }

                var postLateUpdateIndex = -1;
                var timeUpdateIndex = -1;
                var postLateUpdateCount = 0;
                var timeUpdateCount = 0;
                for (var index = 0; index < topLevel.Length; index++)
                {
                    if (topLevel[index].type == typeof(PostLateUpdate))
                    {
                        postLateUpdateIndex = index;
                        postLateUpdateCount++;
                    }
                    if (topLevel[index].type == typeof(TimeUpdate))
                    {
                        timeUpdateIndex = index;
                        timeUpdateCount++;
                    }
                }
                if (postLateUpdateCount != 1
                    || timeUpdateCount != 1)
                {
                    playerLoopBoundaryError =
                        "PlayerLoop requires one PostLateUpdate and one TimeUpdate.";
                    return;
                }

                var timeUpdate = topLevel[timeUpdateIndex];
                var timeSystems = timeUpdate.subSystemList;
                if (timeSystems == null)
                {
                    playerLoopBoundaryError =
                        "TimeUpdate has no subsystems.";
                    return;
                }

                var waitIndex = -1;
                var waitCount = 0;
                var markerCount = 0;
                for (var index = 0; index < timeSystems.Length; index++)
                {
                    if (timeSystems[index].type
                        == typeof(WaitForLastPresentationAndUpdateTime))
                    {
                        waitIndex = index;
                        waitCount++;
                    }
                    if (timeSystems[index].type
                        == typeof(TimeUpdateResumeBoundaryMarker))
                    {
                        markerCount++;
                    }
                }
                if (waitCount != 1 || markerCount != 0)
                {
                    playerLoopBoundaryError =
                        "TimeUpdate requires one wait subsystem and no existing marker.";
                    return;
                }

                var replacement =
                    new PlayerLoopSystem[timeSystems.Length + 1];
                Array.Copy(timeSystems, 0, replacement, 0, waitIndex);
                replacement[waitIndex] = new PlayerLoopSystem
                {
                    type = typeof(TimeUpdateResumeBoundaryMarker),
                    updateDelegate = OnBeforeUnityTimeUpdate
                };
                Array.Copy(
                    timeSystems,
                    waitIndex,
                    replacement,
                    waitIndex + 1,
                    timeSystems.Length - waitIndex);
                timeUpdate.subSystemList = replacement;
                topLevel[timeUpdateIndex] = timeUpdate;
                loop.subSystemList = topLevel;
                UPlayerLoop.SetPlayerLoop(loop);

                playerLoopPostLateUpdateIndex = postLateUpdateIndex;
                playerLoopTimeUpdateIndex = timeUpdateIndex;
                playerLoopResumeBoundaryIndex = waitIndex;
                playerLoopWaitForPresentationIndex = waitIndex + 1;
                playerLoopBoundaryError = string.Empty;
                timeUpdateResumeBoundaryInstalled = true;
                Interlocked.Increment(
                    ref timeUpdateResumeBoundaryInstallCount);
            }
            catch (Exception exception)
            {
                playerLoopBoundaryError =
                    exception.GetType().Name + ":" + exception.Message;
                timeUpdateResumeBoundaryInstalled = false;
            }
        }

        private static void OnBeforeUnityTimeUpdate()
        {
            Interlocked.Increment(
                ref timeUpdateResumeBoundaryCallbackCount);
            if (Volatile.Read(ref timeUpdateResumeRequested) == 0)
            {
                AdvanceDeterministicFrameClockForObservedUnityFrame(
                    excludeSceneLoadFrame: false);
                return;
            }

            var result = HktasClockBridge_CommitMainThreadResume();
            if (result != 1)
            {
                Interlocked.CompareExchange(
                    ref timeUpdateResumeCommitFaultCode,
                    result == 0 ? int.MinValue : result,
                    0);
                return;
            }

            Volatile.Write(ref timeUpdateResumeRequested, 0);
            Interlocked.Increment(
                ref timeUpdateResumeBoundaryCommitCount);
            AdvanceDeterministicFrameClockForObservedUnityFrame(
                excludeSceneLoadFrame: false);
        }

        private static void AdvanceDeterministicFrameClockForObservedUnityFrame(
            bool excludeSceneLoadFrame)
        {
            if (!deterministicClockEnabled
                || Volatile.Read(ref deterministicClockAdvanceFaultCode) != 0
                || Volatile.Read(ref deterministicClockUnityFrameFaultCode) != 0)
            {
                return;
            }

            var current = Time.frameCount;
            if (current < 0)
            {
                Volatile.Write(
                    ref deterministicClockUnityFrameFaultCode,
                    int.MinValue);
                return;
            }

            if (!deterministicClockUnityFrameObserved)
            {
                deterministicClockUnityFrameObserved = true;
                deterministicClockLastUnityFrameCount = current;
                return;
            }

            if (current == deterministicClockLastUnityFrameCount)
            {
                Interlocked.Increment(
                    ref deterministicClockDuplicateTimeUpdateSkipCount);
                return;
            }
            if (current < deterministicClockLastUnityFrameCount)
            {
                Volatile.Write(ref deterministicClockUnityFrameFaultCode, -1);
                return;
            }

            var frameDelta = current - deterministicClockLastUnityFrameCount;
            deterministicClockLastUnityFrameCount = current;
            if (excludeSceneLoadFrame)
            {
                Interlocked.Add(
                    ref deterministicClockSceneLoadFrameSkipCount,
                    frameDelta);
                return;
            }

            // TimeUpdate can be entered more than once inside one rendered
            // frame (for example through level-load callbacks). Unity's
            // frameCount is the read-only committed-frame identity, so QPC is
            // advanced exactly once per new frame and never for duplicate
            // callbacks. If a callback was temporarily absent, replay every
            // observed frame rather than collapsing elapsed virtual time.
            if (frameDelta > 10000)
            {
                Volatile.Write(ref deterministicClockUnityFrameFaultCode, -2);
                return;
            }
            for (var index = 0; index < frameDelta; index++)
            {
                AdvanceDeterministicFrameClock();
                if (Volatile.Read(ref deterministicClockAdvanceFaultCode) != 0)
                {
                    return;
                }
            }
        }

        private static void TryEnableDeterministicMainThreadClock(
            float fixedDeltaTime,
            int targetFrameRate)
        {
            if (deterministicClockEnabled
                || Volatile.Read(ref deterministicClockEnableFaultCode) != 0
                || !timeUpdateResumeBoundaryInstalled)
            {
                return;
            }

            if (HktasClockBridge_GetAbi() != 10u)
            {
                Volatile.Write(
                    ref deterministicClockEnableFaultCode,
                    int.MinValue);
                return;
            }
            var bridgeStatus = HktasClockBridge_GetStatus();
            if (bridgeStatus < 0)
            {
                Volatile.Write(
                    ref deterministicClockEnableFaultCode,
                    bridgeStatus);
                return;
            }
            if (bridgeStatus != 2)
            {
                return;
            }

            var frequency = System.Diagnostics.Stopwatch.Frequency;
            if (frequency <= 0
                || targetFrameRate <= 0
                || frequency % targetFrameRate != 0
                || Math.Abs(
                    (double)fixedDeltaTime
                    - (1d / targetFrameRate)) > 0.000001d)
            {
                Volatile.Write(
                    ref deterministicClockEnableFaultCode,
                    int.MinValue);
                return;
            }
            var stepTicks = frequency / targetFrameRate;

            var result =
                HktasClockBridge_EnableDeterministicMainThreadClock(
                    stepTicks,
                    frequency);
            if (result == -1)
            {
                return;
            }
            if (result != 1 && result != 2)
            {
                Volatile.Write(
                    ref deterministicClockEnableFaultCode,
                    result == 0 ? int.MinValue : result);
                return;
            }
            deterministicClockEnabled = true;
        }

        private static void AdvanceDeterministicFrameClock()
        {
            if (!deterministicClockEnabled
                || Volatile.Read(ref deterministicClockAdvanceFaultCode) != 0)
            {
                return;
            }

            var sequence = checked(deterministicClockAdvanceSequence + 1);
            var result =
                HktasClockBridge_AdvanceDeterministicFrameClock(sequence);
            if (result != 1)
            {
                Volatile.Write(
                    ref deterministicClockAdvanceFaultCode,
                    result == 0 ? int.MinValue : result);
                return;
            }
            Volatile.Write(ref deterministicClockAdvanceSequence, sequence);
        }

        private static void RemoveTimeUpdateResumeBoundary()
        {
            if (!timeUpdateResumeBoundaryInstalled)
            {
                return;
            }

            try
            {
                var loop = UPlayerLoop.GetCurrentPlayerLoop();
                var topLevel = loop.subSystemList;
                if (topLevel == null)
                {
                    return;
                }

                for (var topIndex = 0;
                     topIndex < topLevel.Length;
                     topIndex++)
                {
                    if (topLevel[topIndex].type != typeof(TimeUpdate))
                    {
                        continue;
                    }

                    var timeUpdate = topLevel[topIndex];
                    var systems = timeUpdate.subSystemList;
                    if (systems == null)
                    {
                        continue;
                    }

                    var markerIndex = -1;
                    var markerCount = 0;
                    for (var index = 0; index < systems.Length; index++)
                    {
                        if (systems[index].type
                            == typeof(TimeUpdateResumeBoundaryMarker))
                        {
                            markerIndex = index;
                            markerCount++;
                        }
                    }
                    if (markerCount != 1)
                    {
                        continue;
                    }

                    var replacement =
                        new PlayerLoopSystem[systems.Length - 1];
                    Array.Copy(
                        systems,
                        0,
                        replacement,
                        0,
                        markerIndex);
                    Array.Copy(
                        systems,
                        markerIndex + 1,
                        replacement,
                        markerIndex,
                        systems.Length - markerIndex - 1);
                    timeUpdate.subSystemList = replacement;
                    topLevel[topIndex] = timeUpdate;
                    loop.subSystemList = topLevel;
                    UPlayerLoop.SetPlayerLoop(loop);
                    break;
                }
            }
            finally
            {
                timeUpdateResumeBoundaryInstalled = false;
            }
        }

        private static void TrySynchronizeRandom()
        {
            if (!randomSynchronizationRequested
                && randomSynchronizationRequest?.WaitOne(0) == true)
            {
                // The request event is AutoReset, while exact realtime-epoch
                // convergence can require several PlayerActionSet updates.
                // Latch the request so every retry remains on this same safe
                // input boundary without asking the observer to pulse again.
                randomSynchronizationRequested = true;
            }
            if (randomSynchronizationApplied
                || randomSynchronizationAcknowledged == null
                || !randomSynchronizationRequested)
            {
                return;
            }

            if (!TryNormalizeRealtimeEpochAtRoot())
            {
                return;
            }
            ApplyRandomSynchronization(
                RandomSynchronizationSeed,
                "root",
                USceneManager.GetActiveScene().name ?? string.Empty,
                0);
            randomSynchronizationApplied = true;
            randomSynchronizationAcknowledged.Set();
        }

        private static bool TryNormalizeRealtimeEpochAtRoot()
        {
            if (realtimeEpochNormalizationApplied)
            {
                return true;
            }
            if (realtimeEpochNormalizationFaultCode != 0)
            {
                return false;
            }

            try
            {
                if (!deterministicClockEnabled
                    || HktasClockBridge_GetAbi() != 10u)
                {
                    realtimeEpochNormalizationFaultCode = -1;
                    return false;
                }

                var frequency = HktasClockBridge_GetDeterministicClockFrequency();
                var gameTime = Time.timeAsDouble;
                var realtimeBefore = Time.realtimeSinceStartupAsDouble;
                if (frequency <= 0
                    || double.IsNaN(gameTime)
                    || double.IsInfinity(gameTime)
                    || double.IsNaN(realtimeBefore)
                    || double.IsInfinity(realtimeBefore)
                    || gameTime < 0d
                    || realtimeBefore < 0d)
                {
                    realtimeEpochNormalizationFaultCode = -2;
                    return false;
                }

                if (realtimeEpochNormalizationCount == 0)
                {
                    var startupOffset = gameTime - realtimeBefore;
                    realtimeEpochNormalizationCanonicalOffsetSeconds =
                        checked(
                            (long)Math.Round(
                                startupOffset,
                                MidpointRounding.AwayFromZero));
                    realtimeEpochNormalizationBeforeBits =
                        BitConverter.DoubleToInt64Bits(realtimeBefore);
                }
                var unquantizedTarget = gameTime
                    - realtimeEpochNormalizationCanonicalOffsetSeconds;
                var targetClockTicks = checked(
                    (long)Math.Round(
                        unquantizedTarget * frequency,
                        MidpointRounding.AwayFromZero));
                var target = targetClockTicks / (double)frequency;
                var deltaSeconds = target - realtimeBefore;
                if (double.IsNaN(target)
                    || double.IsInfinity(target)
                    || target < 0d
                    || Math.Abs(deltaSeconds) > 0.5000001d)
                {
                    realtimeEpochNormalizationFaultCode = -3;
                    return false;
                }

                realtimeEpochNormalizationGameTimeBits =
                    BitConverter.DoubleToInt64Bits(gameTime);
                realtimeEpochNormalizationTargetBits =
                    BitConverter.DoubleToInt64Bits(target);
                realtimeEpochNormalizationAfterBits =
                    BitConverter.DoubleToInt64Bits(realtimeBefore);
                if (realtimeEpochNormalizationAfterBits
                    == realtimeEpochNormalizationTargetBits)
                {
                    realtimeEpochNormalizationApplied = true;
                    return true;
                }
                if (realtimeEpochNormalizationCount
                    >= MaximumRealtimeEpochNormalizationAttempts)
                {
                    realtimeEpochNormalizationFaultCode = -5;
                    return false;
                }

                var realtimeClockTicks = checked(
                    (long)Math.Round(
                        realtimeBefore * frequency,
                        MidpointRounding.AwayFromZero));
                var deltaTicks = checked(
                    targetClockTicks - realtimeClockTicks);
                if (deltaTicks == 0L)
                {
                    deltaTicks = target > realtimeBefore ? 1L : -1L;
                }
                var result =
                    HktasClockBridge_ShiftDeterministicMainThreadClock(
                        deltaTicks);
                if (result != 1)
                {
                    realtimeEpochNormalizationFaultCode =
                        result == 0 ? int.MinValue : result;
                    return false;
                }

                realtimeEpochNormalizationDeltaTicks = checked(
                    realtimeEpochNormalizationDeltaTicks + deltaTicks);
                realtimeEpochNormalizationCount++;
                return false;
            }
            catch (Exception)
            {
                realtimeEpochNormalizationFaultCode = -4;
                return false;
            }
        }

        private static void TryNormalizeRecordingBoundaryPhase()
        {
            if (!randomSynchronizationApplied
                || recordingRandomSynchronizationRequested
                || recordingRandomSynchronizationApplied
                || recordingPhaseNormalizationFaultCode != 0)
            {
                return;
            }

            var fixedDeltaTime = Time.fixedDeltaTime;
            if (float.IsNaN(fixedDeltaTime)
                || float.IsInfinity(fixedDeltaTime)
                || fixedDeltaTime <= 0f)
            {
                recordingPhaseNormalizationFaultCode = -1;
                return;
            }

            if (recordingPhaseNormalizationCompleted)
            {
                if (Time.time >= RecordingAbsoluteTimeTarget)
                {
                    var targetPhase = PositiveModulo(
                        Time.frameCount,
                        SceneFramePhaseModulo);
                    if (FloatBits.FromSingle(Time.time)
                            != FloatBits.FromSingle(
                                RecordingAbsoluteTimeTarget)
                        || targetPhase != SceneFramePhaseTarget)
                    {
                        recordingPhaseNormalizationFaultCode = -3;
                    }
                }
                return;
            }

            // A captureDeltaTime property read-back precedes the TimeUpdate in
            // which Unity actually consumes that value. Arm well before the
            // root and keep the tiny delta requested until timeAsDouble has the
            // same bit pattern on two consecutive input updates. Only that
            // unchanged pair proves that a phase-only TimeUpdate was consumed.
            // Then derive how many normal updates remain until 768.00. A restore
            // observed in this input update governs the following TimeUpdate,
            // so the target phase is based on exactly that remaining count and
            // lands on global phase 0 without writing Time.
            if (recordingPhaseNormalizationActive)
            {
                if (recordingPhaseNormalizationLatchPending)
                {
                    var observedTimeDouble = Time.timeAsDouble;
                    var observedTimeDoubleBits =
                        BitConverter.DoubleToInt64Bits(observedTimeDouble);
                    if (recordingPhaseNormalizationHeldTimeDoubleBits == 0L
                        || observedTimeDoubleBits
                        != recordingPhaseNormalizationHeldTimeDoubleBits)
                    {
                        if (recordingPhaseNormalizationHeldTimeDoubleBits != 0L)
                        {
                            var previousObservedTimeDouble =
                                BitConverter.Int64BitsToDouble(
                                    recordingPhaseNormalizationHeldTimeDoubleBits);
                            var observedDelta = observedTimeDouble
                                                - previousObservedTimeDouble;
                            if (observedDelta <= 0d
                                || observedDelta
                                > 1.5d * fixedDeltaTime)
                            {
                                RestoreRecordingPhaseNormalizationCaptureDeltaTime();
                                recordingPhaseNormalizationFaultCode = -9;
                                return;
                            }
                        }

                        recordingPhaseNormalizationHeldTimeBits =
                            FloatBits.FromSingle(Time.time);
                        recordingPhaseNormalizationHeldTimeDoubleBits =
                            observedTimeDoubleBits;
                        recordingPhaseNormalizationHoldFrameCount++;
                        return;
                    }

                    recordingPhaseNormalizationLatchPending = false;
                    var heldTimeDouble = observedTimeDouble;
                    var remainingNormalFrames = checked((int)Math.Round(
                        (RecordingAbsoluteTimeTarget - heldTimeDouble)
                        / fixedDeltaTime,
                        MidpointRounding.AwayFromZero));
                    var projectedTimeDouble = heldTimeDouble
                                              + remainingNormalFrames
                                              * (double)fixedDeltaTime;
                    if (remainingNormalFrames < 1
                        || remainingNormalFrames > 16
                        || FloatBits.FromSingle((float)projectedTimeDouble)
                            != FloatBits.FromSingle(
                                RecordingAbsoluteTimeTarget))
                    {
                        RestoreRecordingPhaseNormalizationCaptureDeltaTime();
                        recordingPhaseNormalizationFaultCode = -2;
                        RecordingPhaseNormalizationDiagnostic = string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "remainingFrames={0}; held={1:R}; projected={2:R}; target={3:R}; fixedDelta={4:R}",
                            remainingNormalFrames, heldTimeDouble, projectedTimeDouble,
                            RecordingAbsoluteTimeTarget, fixedDeltaTime);
                        return;
                    }
                    recordingPhaseNormalizationRemainingNormalFrameCount =
                        remainingNormalFrames;
                    recordingPhaseNormalizationRestoreFramePhase =
                        PositiveModulo(
                            -remainingNormalFrames,
                            SceneFramePhaseModulo);
                    // Unlike a property read-back, the unchanged double pair
                    // above proves that the tiny delta was consumed. It is now
                    // safe to restore on the selected global phase.
                }
                else if (FloatBits.FromSingle(Time.time)
                         != recordingPhaseNormalizationHeldTimeBits)
                {
                    RestoreRecordingPhaseNormalizationCaptureDeltaTime();
                    recordingPhaseNormalizationFaultCode = -9;
                    return;
                }

                recordingPhaseNormalizationHoldFrameCount++;
                var framePhase = PositiveModulo(
                    Time.frameCount,
                    SceneFramePhaseModulo);
                if (framePhase
                    != recordingPhaseNormalizationRestoreFramePhase)
                {
                    return;
                }

                RestoreRecordingPhaseNormalizationCaptureDeltaTime();
                if (recordingPhaseNormalizationFaultCode != 0)
                {
                    return;
                }
                recordingPhaseNormalizationReleaseCount++;
                recordingPhaseNormalizationLastFrameCount = Time.frameCount;
                recordingPhaseNormalizationLastFramePhase = framePhase;
                recordingPhaseNormalizationCompleted = true;
                return;
            }

            var armThreshold = RecordingAbsoluteTimeTarget
                               - 8f * fixedDeltaTime;
            if (Time.time < armThreshold)
            {
                return;
            }
            if (Time.time >= RecordingAbsoluteTimeTarget)
            {
                recordingPhaseNormalizationFaultCode = -10;
                return;
            }

            if (FloatBits.FromSingle(Time.captureDeltaTime)
                != FloatBits.FromSingle(fixedDeltaTime))
            {
                recordingPhaseNormalizationFaultCode = -4;
                return;
            }

            recordingPhaseNormalizationCaptureDeltaTimeBits =
                FloatBits.FromSingle(Time.captureDeltaTime);
            Time.captureDeltaTime =
                RecordingPhaseNormalizationCaptureDeltaTime;
            if (FloatBits.FromSingle(Time.captureDeltaTime)
                != FloatBits.FromSingle(
                    RecordingPhaseNormalizationCaptureDeltaTime))
            {
                recordingPhaseNormalizationCaptureDeltaTimeBits = 0;
                recordingPhaseNormalizationFaultCode = -5;
                return;
            }
            recordingPhaseNormalizationActive = true;
            recordingPhaseNormalizationLatchPending = true;
            recordingPhaseNormalizationBeginCount++;
        }

        private static void RestoreRecordingPhaseNormalizationCaptureDeltaTime()
        {
            if (recordingPhaseNormalizationCaptureDeltaTimeBits == 0)
            {
                recordingPhaseNormalizationActive = false;
                recordingPhaseNormalizationFaultCode = -6;
                return;
            }

            var captureDeltaTime = FloatBits.ToSingle(
                recordingPhaseNormalizationCaptureDeltaTimeBits);
            Time.captureDeltaTime = captureDeltaTime;
            recordingPhaseNormalizationActive = false;
            recordingPhaseNormalizationLatchPending = false;
            recordingPhaseNormalizationCaptureDeltaTimeBits = 0;
            if (FloatBits.FromSingle(Time.captureDeltaTime)
                != FloatBits.FromSingle(captureDeltaTime))
            {
                recordingPhaseNormalizationFaultCode = -7;
            }
        }

        private static void TrySynchronizeRecordingRandom()
        {
            if (!recordingRandomSynchronizationRequested
                && recordingRandomSynchronizationRequest?.WaitOne(0) == true)
            {
                recordingRandomSynchronizationRequested = true;
                recordingRootRequestObservedFrameCount = Time.frameCount;
                // The observer raises the recording-root request from the
                // completed LateUpdate immediately before this Hero action-set
                // update. The preceding normalization guarantees that completed
                // root is the fixed global scene phase without writing Time or
                // any gameplay state.
                recordingRootFramePhase = PositiveModulo(
                    recordingRootRequestObservedFrameCount - 1,
                    SceneFramePhaseModulo);
                if (recordingRootFramePhase != SceneFramePhaseTarget
                    || recordingPhaseNormalizationActive
                    || !recordingPhaseNormalizationCompleted)
                {
                    recordingPhaseNormalizationFaultCode = -8;
                }
            }
            if (!randomSynchronizationApplied
                || recordingRandomSynchronizationApplied
                || recordingRandomSynchronizationAcknowledged == null
                || !recordingRandomSynchronizationRequested)
            {
                return;
            }

            ApplyRandomSynchronization(
                RandomSynchronizationSeed,
                "recording-root",
                USceneManager.GetActiveScene().name ?? string.Empty,
                0);
            recordingRandomSynchronizationApplied = true;
            recordingRandomSynchronizationAcknowledged.Set();
        }

        private static void ApplyRandomSynchronization(
            int appliedSeed,
            string boundary,
            string scene,
            int epoch)
        {
            UnityEngine.Random.InitState(appliedSeed);
            object boxed = UnityEngine.Random.state;
            randomSynchronizationStateS0 =
                ReadRandomState(RandomState0Field, boxed);
            randomSynchronizationStateS1 =
                ReadRandomState(RandomState1Field, boxed);
            randomSynchronizationStateS2 =
                ReadRandomState(RandomState2Field, boxed);
            randomSynchronizationStateS3 =
                ReadRandomState(RandomState3Field, boxed);
            randomSynchronizationSnapshotAvailable = true;
            randomSynchronizationResetCount++;
            randomSynchronizationLastAppliedSeed = appliedSeed;
            randomSynchronizationLastBoundary = boundary;
            randomSynchronizationLastScene = scene;
            randomSynchronizationSceneEpoch = epoch;
        }

        private static string ReadReferenceRunId()
        {
            const string prefix = "--hktas-reference-run=";
            foreach (var argument in Environment.GetCommandLineArgs())
            {
                if (!argument.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var value = argument.Substring(prefix.Length);
                return IsIdentifier(value) ? value : string.Empty;
            }

            return string.Empty;
        }

        private static bool IsIdentifier(string value)
        {
            if (value.Length < 1 || value.Length > 96)
            {
                return false;
            }

            foreach (var character in value)
            {
                if (!(character >= 'a' && character <= 'z')
                    && !(character >= 'A' && character <= 'Z')
                    && !(character >= '0' && character <= '9')
                    && character != '-'
                    && character != '_')
                {
                    return false;
                }
            }

            return true;
        }

        public static void Restore()
        {
            if (fullRunRenderIsolationEnabled)
                On.UnityStandardAssets.ImageEffects.FastNoise.DrawNoiseQuadGrid -=
                    OnFullRunDrawNoise;
            fullRunRenderIsolationEnabled = false;
            fullRunRenderInitialized = false;
            fullRunRenderEntered = false;
            if (runtimeVirtualClockRegistered
                && runtimeVirtualClockBoundaryType != null)
            {
                runtimeVirtualClockBoundaryType.GetMethod(
                        "Unregister",
                        BindingFlags.Public | BindingFlags.Static)
                    ?.Invoke(null, new object[] { VirtualClockProviderId });
            }
            runtimeVirtualClockRegistered = false;
            runtimeVirtualClockBoundaryType = null;
            RemoveTimeUpdateResumeBoundary();
            if (originalCaptured)
            {
                Time.captureDeltaTime =
                    FloatBits.ToSingle(beforeCaptureDeltaTimeBits);
                Application.targetFrameRate = beforeTargetFrameRate;
                QualitySettings.vSyncCount = beforeVSyncCount;
            }

            applied = false;
            originalCaptured = false;
            calibrationAttempts = 0;
            calibratedScene = string.Empty;
            randomSynchronizationApplied = false;
            randomSynchronizationRequested = false;
            recordingRandomSynchronizationRequested = false;
            recordingRandomSynchronizationApplied = false;
            recordingRootRequestObservedFrameCount = -1;
            recordingRootFramePhase = -1;
            recordingPhaseNormalizationActive = false;
            recordingPhaseNormalizationCompleted = false;
            recordingPhaseNormalizationLatchPending = false;
            recordingPhaseNormalizationCaptureDeltaTimeBits = 0;
            recordingPhaseNormalizationHeldTimeBits = 0;
            recordingPhaseNormalizationHeldTimeDoubleBits = 0L;
            recordingPhaseNormalizationRemainingNormalFrameCount = -1;
            recordingPhaseNormalizationRestoreFramePhase = -1;
            recordingPhaseNormalizationBeginCount = 0;
            recordingPhaseNormalizationHoldFrameCount = 0;
            recordingPhaseNormalizationReleaseCount = 0;
            recordingPhaseNormalizationLastFrameCount = -1;
            recordingPhaseNormalizationLastFramePhase = -1;
            recordingPhaseNormalizationFaultCode = 0;
            randomSynchronizationSnapshotAvailable = false;
            randomSynchronizationStateS0 = 0;
            randomSynchronizationStateS1 = 0;
            randomSynchronizationStateS2 = 0;
            randomSynchronizationStateS3 = 0;
            randomSynchronizationResetCount = 0;
            randomSynchronizationTransitionStartCount = 0;
            randomSynchronizationGameplayReadyCount = 0;
            randomSynchronizationFirstGameplayReadyFrameCount = -1;
            randomSynchronizationFirstGameplayReadyFramePhase = -1;
            randomSynchronizationGameplayReadyAlignmentHoldUpdateCount = 0;
            randomSynchronizationSceneEpoch = 0;
            randomSynchronizationLastAppliedSeed = 0;
            randomSynchronizationLastBoundary = string.Empty;
            randomSynchronizationLastScene = string.Empty;
            randomSynchronizationFaultCode = 0;
            sceneRandomSynchronizationPending = false;
            if (sceneClockExclusionActive
                && sceneClockExclusionCaptureDeltaTimeBits != 0)
            {
                Time.captureDeltaTime = FloatBits.ToSingle(
                    sceneClockExclusionCaptureDeltaTimeBits);
            }
            sceneClockExclusionActive = false;
            sceneClockExclusionCaptureDeltaTimeBits = 0;
            sceneClockExclusionBeginCount = 0;
            sceneClockExclusionFinishCount = 0;
            sceneClockExclusionFrozenTimeUpdateCount = 0;
            sceneClockExclusionFaultCode = 0;
            sceneClockExclusionPreSynchronizationCount = 0;
            sceneActivationAlignmentBeginCount = 0;
            sceneActivationAlignmentHoldFrameCount = 0;
            sceneActivationAlignmentReleaseCount = 0;
            sceneActivationAlignmentFirstFrameCount = -1;
            sceneActivationAlignmentFirstFramePhase = -1;
            sceneActivationAlignmentLastFrameCount = -1;
            sceneActivationAlignmentLastFramePhase = -1;
            sceneFinishAlignmentBeginCount = 0;
            sceneFinishAlignmentHoldFrameCount = 0;
            sceneFinishAlignmentReleaseCount = 0;
            sceneFinishAlignmentLastFrameCount = -1;
            sceneFinishAlignmentLastFramePhase = -1;
            sceneFrameAlignmentFaultCode = 0;
            deterministicClockUnityFrameObserved = false;
            deterministicClockLastUnityFrameCount = 0;
            deterministicClockDuplicateTimeUpdateSkipCount = 0;
            deterministicClockSceneLoadFrameSkipCount = 0;
            deterministicClockUnityFrameFaultCode = 0;
            realtimeEpochNormalizationApplied = false;
            realtimeEpochNormalizationCount = 0;
            realtimeEpochNormalizationGameTimeBits = 0L;
            realtimeEpochNormalizationBeforeBits = 0L;
            realtimeEpochNormalizationTargetBits = 0L;
            realtimeEpochNormalizationAfterBits = 0L;
            realtimeEpochNormalizationCanonicalOffsetSeconds = 0L;
            realtimeEpochNormalizationDeltaTicks = 0L;
            realtimeEpochNormalizationFaultCode = 0;
            randomSynchronizationRequest?.Dispose();
            randomSynchronizationRequest = null;
            randomSynchronizationAcknowledged?.Dispose();
            randomSynchronizationAcknowledged = null;
            fullRunRandomRequest?.Dispose();
            fullRunRandomRequest = null;
            fullRunRandomAcknowledged?.Dispose();
            fullRunRandomAcknowledged = null;
            recordingRandomSynchronizationRequest?.Dispose();
            recordingRandomSynchronizationRequest = null;
            recordingRandomSynchronizationAcknowledged?.Dispose();
            recordingRandomSynchronizationAcknowledged = null;
        }

        [DllImport(
            "HollowKnightTAS.ClockBridge.dll",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern uint HktasClockBridge_GetAbi();

        [DllImport(
            "HollowKnightTAS.ClockBridge.dll",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_GetStatus();

        [DllImport(
            "HollowKnightTAS.ClockBridge.dll",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_BeginMainThreadPause();

        [DllImport(
            "HollowKnightTAS.ClockBridge.dll",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_EndMainThreadPause();

        [DllImport(
            "HollowKnightTAS.ClockBridge.dll",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_CommitMainThreadResume();

        [DllImport(
            "HollowKnightTAS.ClockBridge.dll",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_EnableDeterministicMainThreadClock(
            long stepTicks,
            long expectedFrequency);

        [DllImport(
            "HollowKnightTAS.ClockBridge.dll",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_ShiftDeterministicMainThreadClock(
            long deltaTicks);

        [DllImport(
            "HollowKnightTAS.ClockBridge.dll",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_AdvanceDeterministicFrameClock(
            int sequence);

        [DllImport(
            "HollowKnightTAS.ClockBridge.dll",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_GetVirtualClockPaused();

        [DllImport(
            "HollowKnightTAS.ClockBridge.dll",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_GetVirtualClockPauseCount();

        [DllImport(
            "HollowKnightTAS.ClockBridge.dll",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_GetVirtualClockResumePending();

        [DllImport(
            "HollowKnightTAS.ClockBridge.dll",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_GetVirtualClockResumeRequestCount();

        [DllImport(
            "HollowKnightTAS.ClockBridge.dll",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_GetVirtualClockResumeCount();

        [DllImport(
            "HollowKnightTAS.ClockBridge.dll",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_GetDeterministicClockEnabled();

        [DllImport(
            "HollowKnightTAS.ClockBridge.dll",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern long HktasClockBridge_GetDeterministicClockFrequency();

        [DllImport(
            "HollowKnightTAS.ClockBridge.dll",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern long HktasClockBridge_GetDeterministicClockStepTicks();

        [DllImport(
            "HollowKnightTAS.ClockBridge.dll",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern long HktasClockBridge_GetDeterministicClockAnchor();

        [DllImport(
            "HollowKnightTAS.ClockBridge.dll",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_GetDeterministicClockFrameAdvanceCount();

        [DllImport(
            "HollowKnightTAS.ClockBridge.dll",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_GetStartupHookInstalled();

        [DllImport(
            "HollowKnightTAS.ClockBridge.dll",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_GetStartupLatchEnabled();

        [DllImport(
            "HollowKnightTAS.ClockBridge.dll",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern uint HktasClockBridge_GetStartupHookThreadId();

        [DllImport(
            "HollowKnightTAS.ClockBridge.dll",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_GetStartupVirtualQpcCallCount();

        [DllImport(
            "HollowKnightTAS.ClockBridge.dll",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_GetStartupHandoffAdoptCount();

        [DllImport(
            "HollowKnightTAS.ClockBridge.dll",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_GetStartupFaultCode();

        private sealed class TimeUpdateResumeBoundaryMarker
        {
        }

        private static FieldInfo RandomStateField(string name)
        {
            return typeof(UnityEngine.Random.State).GetField(
                       name,
                       BindingFlags.Instance
                       | BindingFlags.Public
                       | BindingFlags.NonPublic)
                   ?? throw new MissingFieldException(
                       typeof(UnityEngine.Random.State).FullName,
                       name);
        }

        private static int ReadRandomState(FieldInfo field, object boxed)
        {
            return (int)(field.GetValue(boxed)
                         ?? throw new InvalidOperationException(
                             "Unity RNG synchronization state is unavailable."));
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)]
            private float single;

            [FieldOffset(0)]
            private int integer;

            public static int FromSingle(float value)
            {
                return new FloatBits { single = value }.integer;
            }

            public static float ToSingle(int value)
            {
                return new FloatBits { integer = value }.single;
            }
        }
    }
}
