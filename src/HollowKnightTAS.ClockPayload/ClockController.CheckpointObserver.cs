using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HollowKnightTAS.Core.SceneCheckpoints;
using UnityEngine;
using UnityEngine.LowLevel;
using TimeUpdate = UnityEngine.PlayerLoop.TimeUpdate;
using WaitForLastPresentationAndUpdateTime = UnityEngine.PlayerLoop.TimeUpdate.WaitForLastPresentationAndUpdateTime;

namespace HollowKnightTAS.ClockPayload
{
    public static partial class ClockController
    {
        private static bool checkpointClockObserverImported;
        [DllImport("HollowKnightTAS.ClockBridge.dll", EntryPoint = "HktasClockBridge_ExportContinuation", CallingConvention = CallingConvention.Cdecl)]
        private static extern int ExportObserverNativeContinuation([Out] byte[] bytes, uint length);

        private static void RequireCheckpointClockObserverBoundary(bool importing)
        {
            RequireCheckpointRandomBoundary();
            var mode = HktasClockBridge_GetFullRunMode();
            if (mode < 0 || mode > 2 || importing && mode != 0
                || !HollowKnightTAS.Core.Movie.NativeExecutionProfile.SupportsSceneCheckpoints(
                    HollowKnightTAS.Core.Movie.NativeExecutionProfile.SelectedProfileId)
                || !fullRunLegacyCalibrationDisabled || !deterministicClockEnabled || !runtimeVirtualClockRegistered
                || runtimeVirtualClockBoundaryType == null
                || runtimeVirtualClockBoundaryType.FullName != "HollowKnightTAS.Runtime.Control.RuntimeVirtualClockBoundary"
                || runtimeVirtualClockBoundaryType.Assembly.GetName().Name != "HollowKnightTAS"
                || !timeUpdateResumeBoundaryInstalled || Volatile.Read(ref timeUpdateResumeRequested) != 0
                || recordingClockCalibrationRequested || recordingPhaseNormalizationActive || recordingPhaseNormalizationLatchPending
                || sceneRandomSynchronizationPending || sceneClockExclusionActive || playerLoopBoundaryError.Length != 0
                || Volatile.Read(ref deterministicClockEnableFaultCode) != 0 || Volatile.Read(ref deterministicClockAdvanceFaultCode) != 0
                || Volatile.Read(ref deterministicClockUnityFrameFaultCode) != 0 || Volatile.Read(ref timeUpdateResumeCommitFaultCode) != 0
                || Volatile.Read(ref randomSynchronizationFaultCode) != 0 || Volatile.Read(ref sceneClockExclusionFaultCode) != 0
                || Volatile.Read(ref sceneFrameAlignmentFaultCode) != 0 || Volatile.Read(ref recordingPhaseNormalizationFaultCode) != 0
                || Volatile.Read(ref realtimeEpochNormalizationFaultCode) != 0 || Volatile.Read(ref doublePhaseCalibrationFaultCode) != 0)
                throw new InvalidOperationException("Checkpoint clock observer requires the original healthy canonical clock boundary.");
            RequireOriginalTimeUpdateHook();
        }

        private static void RequireOriginalTimeUpdateHook()
        {
            var top = PlayerLoop.GetCurrentPlayerLoop().subSystemList;
            if (top == null || top.Count(system => system.type == typeof(TimeUpdate)) != 1
                || playerLoopTimeUpdateIndex < 0 || playerLoopTimeUpdateIndex >= top.Length
                || top[playerLoopTimeUpdateIndex].type != typeof(TimeUpdate))
                throw new InvalidOperationException("Checkpoint clock observer TimeUpdate binding differs.");
            var systems = top[playerLoopTimeUpdateIndex].subSystemList;
            if (systems == null || systems.Count(system => system.type == typeof(TimeUpdateResumeBoundaryMarker)) != 1
                || systems.Count(system => system.type == typeof(WaitForLastPresentationAndUpdateTime)) != 1
                || playerLoopResumeBoundaryIndex < 0 || playerLoopWaitForPresentationIndex != playerLoopResumeBoundaryIndex + 1
                || playerLoopWaitForPresentationIndex >= systems.Length
                || systems[playerLoopResumeBoundaryIndex].type != typeof(TimeUpdateResumeBoundaryMarker)
                || systems[playerLoopWaitForPresentationIndex].type != typeof(WaitForLastPresentationAndUpdateTime))
                throw new InvalidOperationException("Checkpoint clock observer original hook order differs.");
            var callback = systems[playerLoopResumeBoundaryIndex].updateDelegate;
            PlayerLoopSystem.UpdateFunction original = OnBeforeUnityTimeUpdate;
            if (callback == null || callback.Target != null || callback.GetInvocationList().Length != 1 || !callback.Equals(original))
                throw new InvalidOperationException("Checkpoint clock observer original hook delegate differs.");
        }

        private static CheckpointClockObserverState CaptureClockObserver() => new CheckpointClockObserverState(
            deterministicClockUnityFrameObserved, sceneClockExclusionActive, deterministicClockLastUnityFrameCount,
            Volatile.Read(ref deterministicClockAdvanceSequence), Time.frameCount);
        private static byte[] ReadObserverNativeContinuation()
        {
            var bytes = new byte[1128];
            if (ExportObserverNativeContinuation(bytes, (uint)bytes.Length) != 1)
                throw new InvalidOperationException("Checkpoint clock observer native boundary read failed.");
            return bytes;
        }
        public static byte[] ExportCheckpointClockObserver()
        {
            RequireCheckpointClockObserverBoundary(false);
            var state = CaptureClockObserver(); var nativeFrame = HktasClockBridge_GetCompletedPlayerLoops();
            if (nativeFrame > long.MaxValue) throw new InvalidOperationException("Checkpoint clock observer native cursor overflowed.");
            state.RequireNativeReadback(ReadObserverNativeContinuation(), (long)nativeFrame, Time.frameCount);
            state.RequireReadback(CaptureClockObserver()); RequireCheckpointClockObserverBoundary(false);
            return state.Encode();
        }
        public static byte[] ExportCheckpointNativeClock()
        {
            RequireCheckpointClockObserverBoundary(false);
            var bytes = ReadObserverNativeContinuation();
            var frame = HktasClockBridge_GetCompletedPlayerLoops();
            if (frame > long.MaxValue) throw new InvalidOperationException("Checkpoint native cursor overflowed.");
            CaptureClockObserver().RequireNativeReadback(bytes, (long)frame, Time.frameCount);
            return bytes;
        }
        public static void ImportCheckpointClockObserver(byte[] bytes, long expectedNative)
        {
            RequireCheckpointClockObserverBoundary(true);
            if (checkpointClockObserverImported || !checkpointRandomImported || expectedNative < 0
                || HktasClockBridge_GetCompletedPlayerLoops() != (ulong)expectedNative)
                throw new InvalidOperationException("Checkpoint clock observer import requires the single paused source continuation.");
            var state = CheckpointClockObserverState.Decode(bytes);
            if (state.SceneClockExclusionActive || state.AdvanceSequence != Volatile.Read(ref deterministicClockAdvanceSequence))
                throw new InvalidDataException("Checkpoint clock observer source exclusion or random sequence differs.");
            state.RequireNativeReadback(ReadObserverNativeContinuation(), expectedNative, Time.frameCount);
            // Consume before the first live write. Faults/readback failures are
            // terminal and cannot be erased by retrying or rebuilding a getter.
            checkpointClockObserverImported = true;
            deterministicClockUnityFrameObserved = state.UnityFrameObserved;
            deterministicClockLastUnityFrameCount = state.LastUnityFrameCount;
            Volatile.Write(ref deterministicClockAdvanceSequence, state.AdvanceSequence);
            sceneClockExclusionActive = state.SceneClockExclusionActive;
            state.RequireReadback(CaptureClockObserver());
            state.RequireNativeReadback(ReadObserverNativeContinuation(), expectedNative, Time.frameCount);
            RequireCheckpointClockObserverBoundary(true);
        }
    }
}
