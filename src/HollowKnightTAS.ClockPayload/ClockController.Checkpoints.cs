using System;
using System.Runtime.InteropServices;
using HollowKnightTAS.Core.SceneCheckpoints;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.ClockPayload
{
    public static partial class ClockController
    {
        private static bool checkpointRandomImported;
        [DllImport("HollowKnightTAS.ClockBridge.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_GetFullRunMode();
        [DllImport("HollowKnightTAS.ClockBridge.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong HktasClockBridge_GetCompletedPlayerLoops();
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        private static void RequireCheckpointRandomBoundary()
        {
            if ((!NativeExecutionProfile.SupportsSceneCheckpoints(NativeExecutionProfile.SelectedProfileId)
                    && Environment.GetEnvironmentVariable("HKTAS_SCENE_CHECKPOINT_EXPERIMENT") != "1")
                || !fullRunRenderIsolationEnabled || fullRunRenderEntered
                || GetCurrentThreadId() != (uint)StartupHookThreadId)
                throw new InvalidOperationException("Random continuation requires the controlled engine boundary.");
        }
        public static byte[] ExportCheckpointRandomState()
        {
            RequireCheckpointRandomBoundary();
            return CapturePortableRandomState();
        }
        private static byte[] CapturePortableRandomState()
        {
            var fields = new[] { RandomState0Field, RandomState1Field, RandomState2Field, RandomState3Field };
            var words = new int[12]; object gameplay = UnityEngine.Random.state, render = fullRunRenderState;
            for (var i = 0; i < 4; i++)
            { words[i] = ReadRandomState(fields[i], gameplay); words[i + 4] = ReadRandomState(fields[i], render); }
            words[8] = randomSynchronizationStateS0; words[9] = randomSynchronizationStateS1;
            words[10] = randomSynchronizationStateS2; words[11] = randomSynchronizationStateS3;
            return new CheckpointRandomState(words, fullRunRenderInitialized, randomSynchronizationSnapshotAvailable,
                randomSynchronizationResetCount, randomSynchronizationSceneEpoch, randomSynchronizationLastAppliedSeed,
                deterministicClockAdvanceSequence, randomSynchronizationLastBoundary, randomSynchronizationLastScene).Encode();
        }
        public static void ImportCheckpointParticleRandomState(uint[] source, long expectedNativeFrame)
        {
            RequireCheckpointRandomBoundary();
            if (!checkpointRandomImported || !NativeExecutionProfile.UsesCanonicalBoundary(NativeExecutionProfile.SelectedProfileId)
                || expectedNativeFrame < 0 || HktasClockBridge_GetFullRunMode() != 0
                || HktasClockBridge_GetCompletedPlayerLoops() != (ulong)expectedNativeFrame)
                throw new InvalidOperationException("Particle continuation requires the imported original paused canonical D boundary.");
            EarlyModWallClockBootstrap.RestoreCheckpointParticleRandomState(source);
        }
        public static void ImportCheckpointRandomState(byte[] bytes, long expectedNativeFrame)
        {
            RequireCheckpointRandomBoundary();
            if (checkpointRandomImported || expectedNativeFrame < 0 || HktasClockBridge_GetFullRunMode() != 0
                || HktasClockBridge_GetCompletedPlayerLoops() != (ulong)expectedNativeFrame)
                throw new InvalidOperationException("Random continuation import requires a fresh paused restore transaction.");
            var state = CheckpointRandomState.Decode(bytes);
            ApplyPortableRandomState(state);
            checkpointRandomImported = true;
        }
        private static void ApplyPortableRandomState(CheckpointRandomState state)
        {
            var words = state.CopyWords();
            var fields = new[] { RandomState0Field, RandomState1Field, RandomState2Field, RandomState3Field };
            object gameplay = default(UnityEngine.Random.State), render = default(UnityEngine.Random.State);
            for (var i = 0; i < 4; i++) { fields[i].SetValue(gameplay, words[i]); fields[i].SetValue(render, words[i + 4]); }
            // Restore words directly. InitState and scene-epoch advancement stay
            // at their original first-input/explicit-seed call sites.
            UnityEngine.Random.state = (UnityEngine.Random.State)gameplay;
            fullRunRenderState = (UnityEngine.Random.State)render; fullRunRenderInitialized = state.RenderInitialized;
            randomSynchronizationSnapshotAvailable = state.SnapshotAvailable;
            randomSynchronizationStateS0 = words[8]; randomSynchronizationStateS1 = words[9];
            randomSynchronizationStateS2 = words[10]; randomSynchronizationStateS3 = words[11];
            randomSynchronizationResetCount = state.ResetCount; randomSynchronizationSceneEpoch = state.SceneEpoch;
            randomSynchronizationLastAppliedSeed = state.LastSeed; randomSynchronizationLastBoundary = state.Boundary;
            randomSynchronizationLastScene = state.Scene; deterministicClockAdvanceSequence = state.AdvanceSequence;
        }
    }
}
