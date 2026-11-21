using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using HollowKnightTAS.Core.Ipc;

namespace HollowKnightTAS.Runtime.Companion
{
    public sealed class RuntimeStartupProfileAttestor
    {
        private const string PayloadAssemblyName =
            "HollowKnightTAS.ClockPayload";
        private const string PayloadControllerType =
            "HollowKnightTAS.ClockPayload.ClockController";

        private readonly int processId;
        private readonly long processStartTimeUtcTicks;
        private readonly string runId;
        private readonly bool launchBindingValid;
        private int payloadReadySignaled;
        private static bool profileMismatchWarned;
        private static bool bridgeAbiMismatchWarned;
        private static bool bridgeStatusMismatchWarned;

        public RuntimeStartupProfileAttestor()
        {
            using (var process = Process.GetCurrentProcess())
            {
                processId = process.Id;
                processStartTimeUtcTicks = process.StartTime
                    .ToUniversalTime()
                    .Ticks;
            }

            runId = ReadRunId(Environment.GetCommandLineArgs());
            launchBindingValid = !string.IsNullOrEmpty(runId)
                                 && string.Equals(
                                     Environment.GetEnvironmentVariable(
                                         StartupProfileContract
                                             .RunIdEnvironment),
                                     runId,
                                     StringComparison.Ordinal)
                                 && string.Equals(
                                     Environment.GetEnvironmentVariable(
                                         StartupProfileContract
                                             .StartupLatchEnvironment),
                                     "1",
                                     StringComparison.Ordinal);
        }

        public string RunId => runId;

        public void TrySignalPayloadReady(string activeScene)
        {
            if (!launchBindingValid
                || !string.Equals(
                    activeScene,
                    "Menu_Title",
                    StringComparison.Ordinal)
                || Interlocked.CompareExchange(
                    ref payloadReadySignaled,
                    1,
                    0) != 0)
            {
                return;
            }

            try
            {
                using (var ready = new EventWaitHandle(
                           false,
                           EventResetMode.ManualReset,
                           StartupProfileContract.PayloadReadyEventPrefix
                           + runId))
                {
                    ready.Set();
                }
            }
            catch
            {
                Interlocked.Exchange(ref payloadReadySignaled, -1);
            }
        }

        public StartupProfileAttestation Capture()
        {
            if (!launchBindingValid)
            {
                return Empty(
                    StartupProfileAttestationStatus.Unverified,
                    StartupRecordingRootStatus.Unavailable,
                    "launch-binding-unverified");
            }

            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(
                    value => string.Equals(
                        value.GetName().Name,
                        PayloadAssemblyName,
                        StringComparison.Ordinal));
            if (assembly == null)
            {
                return Empty(
                    Volatile.Read(ref payloadReadySignaled) < 0
                        ? StartupProfileAttestationStatus.Faulted
                        : StartupProfileAttestationStatus.Pending,
                    StartupRecordingRootStatus.Pending,
                    Volatile.Read(ref payloadReadySignaled) < 0
                        ? "payload-ready-signal-failed"
                        : string.Empty);
            }

            try
            {
                var controller = assembly.GetType(
                                     PayloadControllerType,
                                     throwOnError: true,
                                     ignoreCase: false)
                                 ?? throw new TypeLoadException(
                                     PayloadControllerType);
                var profile = Read<string>(controller, "ActiveProfileId");
                var bridgeAbi = Read<uint>(controller, "BridgeAbi");
                var bridgeStatus = Read<int>(controller, "BridgeStatus");
                var startupHookInstalled = Read<bool>(
                    controller,
                    "StartupHookInstalled");
                var startupLatchEnabled = Read<bool>(
                    controller,
                    "StartupLatchEnabled");
                var deterministicClockEnabled = Read<bool>(
                    controller,
                    "DeterministicClockEnabled");
                var runtimeVirtualClockRegistered = Read<bool>(
                    controller,
                    "RuntimeVirtualClockRegistered");
                var timeUpdateResumeBoundaryInstalled = Read<bool>(
                    controller,
                    "TimeUpdateResumeBoundaryInstalled");
                var startupFaultCode = Read<int>(
                    controller,
                    "StartupFaultCode");
                var deterministicClockEnableFaultCode = Read<int>(
                    controller,
                    "DeterministicClockEnableFaultCode");
                var deterministicClockAdvanceFaultCode = Read<int>(
                    controller,
                    "DeterministicClockAdvanceFaultCode");
                var deterministicClockUnityFrameFaultCode = Read<int>(
                    controller,
                    "DeterministicClockUnityFrameFaultCode");
                var timeUpdateResumeCommitFaultCode = Read<int>(
                    controller,
                    "TimeUpdateResumeCommitFaultCode");
                var playerLoopBoundaryError = Read<string>(
                    controller,
                    "PlayerLoopBoundaryError");
                var randomSnapshotAvailable = Read<bool>(
                    controller,
                    "RandomSynchronizationSnapshotAvailable");
                var randomResetCount = Read<int>(
                    controller,
                    "RandomSynchronizationResetCount");
                var recordingRandomApplied = Read<bool>(
                    controller,
                    "RecordingRandomSynchronizationApplied");
                var realtimeEpochApplied = Read<bool>(
                    controller,
                    "RealtimeEpochNormalizationApplied");
                var recordingPhaseCompleted = Read<bool>(
                    controller,
                    "RecordingPhaseNormalizationCompleted");
                var randomFaultCode = Read<int>(
                    controller,
                    "RandomSynchronizationFaultCode");
                var realtimeEpochFaultCode = Read<int>(
                    controller,
                    "RealtimeEpochNormalizationFaultCode");
                var recordingPhaseFaultCode = Read<int>(
                    controller,
                    "RecordingPhaseNormalizationFaultCode");

                var startupFaulted = bridgeStatus < 0
                                     || startupFaultCode != 0
                                     || deterministicClockEnableFaultCode != 0
                                     || deterministicClockAdvanceFaultCode != 0
                                     || deterministicClockUnityFrameFaultCode != 0
                                     || timeUpdateResumeCommitFaultCode != 0
                                     || !string.IsNullOrEmpty(
                                         playerLoopBoundaryError);
                if (!string.Equals(
                             profile,
                             StartupProfileContract.ProfileId,
                             StringComparison.Ordinal)
                    && !profileMismatchWarned)
                {
                    profileMismatchWarned = true;
                    Modding.Logger.LogWarn("[HKTAS] ClockPayload ProfileId differs from contract (expected="
                        + StartupProfileContract.ProfileId + ", actual=" + profile + "); allowing startup.");
                }
                if (bridgeAbi != StartupProfileContract.BridgeAbi && !bridgeAbiMismatchWarned)
                {
                    bridgeAbiMismatchWarned = true;
                    Modding.Logger.LogWarn("[HKTAS] ClockBridge ABI differs from contract (expected="
                        + StartupProfileContract.BridgeAbi + ", actual=" + bridgeAbi + "); allowing startup.");
                }
                if (bridgeStatus > StartupProfileContract.ReadyBridgeStatus && !bridgeStatusMismatchWarned)
                {
                    bridgeStatusMismatchWarned = true;
                    Modding.Logger.LogWarn("[HKTAS] ClockBridge status differs from contract ready code (expected="
                        + StartupProfileContract.ReadyBridgeStatus + ", actual=" + bridgeStatus + "); allowing startup.");
                }

                StartupProfileAttestationStatus status;
                if (startupFaulted)
                {
                    status = StartupProfileAttestationStatus.Faulted;
                }
                else if (bridgeStatus >= 0 && bridgeStatus < StartupProfileContract.ReadyBridgeStatus)
                {
                    status = StartupProfileAttestationStatus.Pending;
                }
                else if (!startupHookInstalled
                         || !startupLatchEnabled
                         || !deterministicClockEnabled
                         || !runtimeVirtualClockRegistered
                         || !timeUpdateResumeBoundaryInstalled)
                {
                    status = StartupProfileAttestationStatus.Faulted;
                }
                else
                {
                    status = StartupProfileAttestationStatus.Verified;
                }

                var rootFaulted = randomFaultCode != 0
                                  || realtimeEpochFaultCode != 0
                                  || recordingPhaseFaultCode != 0;
                var rootStatus = rootFaulted
                    ? StartupRecordingRootStatus.Faulted
                    : status == StartupProfileAttestationStatus.Verified
                      && randomSnapshotAvailable
                      && randomResetCount >= 2
                      && recordingRandomApplied
                      && realtimeEpochApplied
                      && recordingPhaseCompleted
                        ? StartupRecordingRootStatus.Verified
                        : status == StartupProfileAttestationStatus.Faulted
                            ? StartupRecordingRootStatus.Faulted
                            : StartupRecordingRootStatus.Pending;

                return new StartupProfileAttestation(
                    status,
                    rootStatus,
                    runId,
                    processId,
                    processStartTimeUtcTicks,
                    profile,
                    bridgeAbi,
                    bridgeStatus,
                    startupHookInstalled,
                    startupLatchEnabled,
                    deterministicClockEnabled,
                    runtimeVirtualClockRegistered,
                    timeUpdateResumeBoundaryInstalled,
                    startupFaultCode,
                    deterministicClockEnableFaultCode,
                    deterministicClockAdvanceFaultCode,
                    deterministicClockUnityFrameFaultCode,
                    timeUpdateResumeCommitFaultCode,
                    playerLoopBoundaryError,
                    randomSnapshotAvailable,
                    randomResetCount,
                    recordingRandomApplied,
                    realtimeEpochApplied,
                    recordingPhaseCompleted,
                    randomFaultCode,
                    realtimeEpochFaultCode,
                    recordingPhaseFaultCode);
            }
            catch (Exception exception)
            {
                return Empty(
                    StartupProfileAttestationStatus.Faulted,
                    StartupRecordingRootStatus.Faulted,
                    "attestation-reflection-"
                    + exception.GetType().Name);
            }
        }

        private StartupProfileAttestation Empty(
            StartupProfileAttestationStatus status,
            StartupRecordingRootStatus rootStatus,
            string error)
        {
            return new StartupProfileAttestation(
                status,
                rootStatus,
                runId,
                processId,
                processStartTimeUtcTicks,
                string.Empty,
                0,
                0,
                false,
                false,
                false,
                false,
                false,
                0,
                0,
                0,
                0,
                0,
                error,
                false,
                0,
                false,
                false,
                false,
                0,
                0,
                0);
        }

        private static T Read<T>(Type controller, string propertyName)
        {
            var property = controller.GetProperty(
                               propertyName,
                               BindingFlags.Public
                               | BindingFlags.Static)
                           ?? throw new MissingMemberException(
                               controller.FullName,
                               propertyName);
            var value = property.GetValue(null, null);
            if (!(value is T typed))
            {
                throw new InvalidCastException(propertyName);
            }

            return typed;
        }

        private static string ReadRunId(string[] arguments)
        {
            var values = arguments
                .Where(
                    value => value.StartsWith(
                        StartupProfileContract.CommandLineRunPrefix,
                        StringComparison.Ordinal))
                .Select(
                    value => value.Substring(
                        StartupProfileContract
                            .CommandLineRunPrefix.Length))
                .ToArray();
            return values.Length == 1
                   && IpcIdentifier.IsValid(values[0], 96)
                ? values[0]
                : string.Empty;
        }
    }
}
