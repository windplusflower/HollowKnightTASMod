using System;
using System.Threading;
using System.Linq;
using System.Reflection;
using HollowKnightTAS.Core.Ipc;
using UnityEngine;

namespace HollowKnightTAS.Runtime.Companion
{
    // Drives only the existing startup handshake. It does not capture a journal
    // origin or establish source/target equivalence by itself.
    internal sealed class RuntimeRecordingRootCoordinator : IDisposable
    {
        private readonly RuntimeStartupProfileAttestor startupAttestor;
        private readonly string expectedRunId;
        private bool coldRootRequestSignaled;
        private bool coldRecordingRootRequestSignaled;
        private string detail = string.Empty;
        private readonly bool chooseDynamicRoot;
        private readonly bool configureRoot;
        private bool rootConfigured;
        private bool calibrationStarted;
        private Type? calibrationController;
        public bool PreparationStarted => calibrationStarted || RootRequested;
        private HollowKnightTAS.Runtime.Input.HeroInputAdapter? preparationInput;
        private HeroActions? preparationActions;

        public RuntimeRecordingRootCoordinator(
            RuntimeStartupProfileAttestor attestor, string runId,
            double? rootBoundarySeconds = null, bool chooseDynamicRoot = false)
        {
            startupAttestor = attestor ?? throw new ArgumentNullException(nameof(attestor));
            if (!IpcIdentifier.IsValid(runId, 96))
                throw new ArgumentException("Startup run ID is invalid.", nameof(runId));
            expectedRunId = runId;
            if (rootBoundarySeconds.HasValue && chooseDynamicRoot)
                throw new ArgumentException("A source-selected root cannot also supply a saved root.");
            this.chooseDynamicRoot = chooseDynamicRoot;
            configureRoot = chooseDynamicRoot || rootBoundarySeconds.HasValue;
            RootBoundarySeconds = rootBoundarySeconds ?? StartupProfileContract.RecordingAbsoluteTime;
            RecordingRootConfiguration.Validate(RootBoundarySeconds);
        }

        public bool RootRequested => coldRootRequestSignaled;
        public int? RecordingBoundaryFrame { get; private set; }
        public double RootBoundarySeconds { get; private set; }
        public string Detail => detail;

        public bool Advance()
        {
            var attestor = startupAttestor;
            if (attestor == null)
            {
                throw new InvalidOperationException("Cold restore has no startup profile attestor.");
            }

            var attestation = attestor.Capture();
            if (!string.Equals(
                    attestation.RunId,
                    expectedRunId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Cold target startup run ID does not match the operation.");
            }
            if (attestation.Status
                    == StartupProfileAttestationStatus.Unverified
                || attestation.Status
                    == StartupProfileAttestationStatus.Faulted
                || attestation.RootStatus
                    == StartupRecordingRootStatus.Faulted)
            {
                throw new InvalidOperationException("Cold target startup profile/root is not eligible: "
                    + attestation.Status
                    + "/"
                    + attestation.RootStatus
                    + "; rngFault=" + attestation.RandomSynchronizationFaultCode
                    + "; realtimeFault=" + attestation.RealtimeEpochNormalizationFaultCode
                    + "; phaseFault=" + attestation.RecordingPhaseNormalizationFaultCode
                    + "; " + ReadPhaseDiagnostic()
                    + ".");
            }
            if (attestation.Status
                != StartupProfileAttestationStatus.Verified)
            {
                detail =
                    "Waiting for verified ClockStartup payload handoff before recording root.";
                return false;
            }
            if (configureRoot)
            {
                var actions = InputHandler.Instance?.inputActions
                    ?? throw new InvalidOperationException("Hero input actions are unavailable during root preparation.");
                if (preparationInput == null)
                {
                    preparationInput = new HollowKnightTAS.Runtime.Input.HeroInputAdapter();
                    preparationInput.Attach(actions);
                    preparationActions = actions;
                    preparationInput.Prepare(HollowKnightTAS.Core.Input.InputSample.FromHeld(
                        0, HollowKnightTAS.Core.Input.TasAction.None, HollowKnightTAS.Core.Input.TasAction.None));
                }
                else if (!ReferenceEquals(preparationActions, actions))
                    throw new InvalidOperationException("Hero action set changed during root preparation.");
            }
            if (configureRoot && !rootConfigured)
            {
                if (!PrepareClockPhase()) return false;
                if (chooseDynamicRoot)
                    RootBoundarySeconds = RecordingRootConfiguration.Choose(Time.timeAsDouble, Time.fixedDeltaTime);
                ConfigurePayloadRoot();
                rootConfigured = true;
            }
            if (attestation.RootStatus
                == StartupRecordingRootStatus.Verified)
            {
                detail =
                    "Startup profile and recording root are verified at the exact baseline.";
                return true;
            }

            if (!coldRootRequestSignaled)
            {
                if (Time.time >= RootBoundarySeconds)
                    throw new InvalidOperationException(
                        "The startup profile recording boundary has already passed; no root request was sent.");
                SignalColdRootEvent(".request");
                coldRootRequestSignaled = true;
                detail =
                    "Exact baseline matched; requested symmetric startup RNG root.";
                return false;
            }

            if (!coldRecordingRootRequestSignaled)
            {
                if (!attestation.RandomSynchronizationSnapshotAvailable
                    || !attestation.RealtimeEpochNormalizationApplied)
                {
                    detail =
                        "Waiting for startup RNG/realtime root before recording phase.";
                    return false;
                }

                var time = Time.time;
                if (time < RootBoundarySeconds)
                {
                    detail =
                        "Waiting for the frozen recording-root time/phase boundary.";
                    return false;
                }
                if (FloatBits(time)
                        != FloatBits(
                            (float)RootBoundarySeconds)
                    // Dynamic roots preserve Unity's sub-fixed-step remainder.
                    // Exact target game/fixed clock equality is checked against
                    // the persisted journal origin before replay is released.
                    || (configureRoot
                        ? !RecordingRootConfiguration.IsValidPhysicsPhase(
                            Time.timeAsDouble, Time.fixedTimeAsDouble, Time.fixedDeltaTime)
                        : FloatBits(Time.fixedTime) != FloatBits((float)RootBoundarySeconds))
                    || PositiveModulo(
                        Time.frameCount,
                        StartupProfileContract.RecordingFramePhaseModulo)
                       != StartupProfileContract.RecordingFramePhaseTarget
                    || !attestation.RecordingPhaseNormalizationCompleted)
                {
                    throw new InvalidOperationException(string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Recording root boundary mismatch: target={0:R}; time={1:R}; fixedTime={2:R}; "
                        + "timeDouble={3:R}; fixedDouble={4:R}; frame={5}; phase={6}; normalized={7}.",
                        RootBoundarySeconds, time, Time.fixedTime, Time.timeAsDouble,
                        Time.fixedTimeAsDouble, Time.frameCount,
                        PositiveModulo(Time.frameCount, StartupProfileContract.RecordingFramePhaseModulo),
                        attestation.RecordingPhaseNormalizationCompleted));
                }

                SignalColdRootEvent(".recording-request");
                RecordingBoundaryFrame = Time.frameCount;
                coldRecordingRootRequestSignaled = true;
                detail =
                    "Recording-root request emitted at the exact frozen boundary.";
                return false;
            }

            detail =
                "Waiting for ClockPayload to acknowledge the recording root.";
            return false;
        }

        private void SignalColdRootEvent(string suffix)
        {
            if (suffix != ".request" && suffix != ".recording-request") throw new ArgumentOutOfRangeException(nameof(suffix));
            HollowKnightTAS.Runtime.FullRun.ResidentRngProducerClient.Request(suffix == ".request" ? 0 : 1);
        }

        private bool PrepareClockPhase()
        {
            if (!calibrationStarted)
            {
                calibrationController = AppDomain.CurrentDomain.GetAssemblies()
                    .SingleOrDefault(value => value.GetName().Name == "HollowKnightTAS.ClockPayload")
                    ?.GetType("HollowKnightTAS.ClockPayload.ClockController")
                    ?? throw new InvalidOperationException("ClockPayload is unavailable for recording preparation.");
                var begin = calibrationController.GetMethod("BeginRecordingClockCalibration",
                    BindingFlags.Public | BindingFlags.Static)
                    ?? throw new InvalidOperationException("Update ClockStartup: recording clock preparation is unsupported.");
                begin.Invoke(null, null);
                calibrationStarted = true;
            }
            var fault = calibrationController!.GetProperty("DoublePhaseCalibrationFaultCode")?.GetValue(null, null);
            if (fault is not int code || code != 0)
                throw new InvalidOperationException("Recording clock calibration failed: " + fault);
            if (calibrationController.GetProperty("DoublePhaseCalibrationApplied")?.GetValue(null, null) is not true)
            {
                detail = "Preparing a shared game/physics clock phase before choosing the recording root.";
                return false;
            }
            if (BitConverter.DoubleToInt64Bits(Time.timeAsDouble)
                != BitConverter.DoubleToInt64Bits(Time.fixedTimeAsDouble))
                throw new InvalidOperationException("Recording clock phase changed before root configuration.");
            return true;
        }

        private void ConfigurePayloadRoot()
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(
                value => value.GetName().Name == "HollowKnightTAS.ClockPayload")
                ?? throw new InvalidOperationException("ClockPayload is unavailable for root configuration.");
            var controller = assembly.GetType("HollowKnightTAS.ClockPayload.ClockController", true)!;
            var configure = controller.GetMethod("ConfigureRecordingRoot", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(double) }, null)
                ?? throw new InvalidOperationException("ClockPayload does not support persisted recording roots; update the startup bundle.");
            configure.Invoke(null, new object[] { RootBoundarySeconds });
            var readback = controller.GetProperty("ConfiguredRecordingRootSeconds", BindingFlags.Public | BindingFlags.Static);
            if (readback?.GetValue(null, null) is not double actual || actual != RootBoundarySeconds)
                throw new InvalidOperationException("ClockPayload recording-root readback differs from the requested value.");
        }

        private static string ReadPhaseDiagnostic()
        {
            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
                    value => value.GetName().Name == "HollowKnightTAS.ClockPayload");
                return assembly?.GetType("HollowKnightTAS.ClockPayload.ClockController")
                    ?.GetProperty("RecordingPhaseNormalizationDiagnostic", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null, null) as string ?? "phase diagnostic unavailable";
            }
            catch { return "phase diagnostic unavailable"; }
        }

        public void Dispose()
        {
            try
            {
                if (calibrationStarted && !rootConfigured)
                    calibrationController?.GetMethod("CancelRecordingClockCalibration")?.Invoke(null, null);
            }
            finally
            {
                RestorePreparationInput();
            }
        }

        private void RestorePreparationInput()
        {
            var input = preparationInput;
            preparationInput = null;
            preparationActions = null;
            if (input == null) return;
            var restored = input.DetachAndRestore();
            if (!restored.Equivalent)
                throw new InvalidOperationException("Recording preparation input bindings were not restored exactly.");
        }

        private static int FloatBits(float value)
        {
            return BitConverter.ToInt32(
                BitConverter.GetBytes(value),
                0);
        }

        private static int PositiveModulo(int value, int modulo)
        {
            var result = value % modulo;
            return result < 0 ? result + modulo : result;
        }

    }
}
