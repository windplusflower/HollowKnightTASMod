using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Core.SceneCheckpoints;
using HollowKnightTAS.Runtime.Input;
using HollowKnightTAS.Runtime.Timing;

namespace HollowKnightTAS.Runtime.FullRun
{
    [Serializable]
    internal sealed class CheckpointContinuationData
    {
        public int Version = 2;
        public long MovieFrame, NativeFrame, SkippedLoadingFrames;
        public string MoviePrefixSha256 = "";
        public Dictionary<string, string> ExecutionFiles = null!;
        public int TimingRunIndex;
        public long TimingRunStart;
        public decimal ActiveFrameRate, RecordingFrameRate;
        public bool TitleReadySeen, FirstLevelActivationPending;
        public string RandomSeedScene = "";
        public long LastGameUpdateNativeFrame;
        public double GameUpdateDeltaSeconds, GameUpdateTimeSeconds, ExecutedMovieSeconds;
        public byte[] NativeClock = null!, Random = null!, ClockObserver = null!;
        public CheckpointInputState Input = null!;
        public CheckpointMouseState Mouse = null!;
        public CheckpointManagedWorldData ManagedWorld = null!;
        public CheckpointSceneLoadState SceneLoad = null!;
        public CanonicalInputEpoch InputEpoch = null!;
        internal void Validate(long movieFrame, long nativeFrame)
        {
            if (Version != 2 || MovieFrame != movieFrame || NativeFrame != nativeFrame || MovieFrame < 0 || NativeFrame < 0
                || SkippedLoadingFrames < 0 || !MovieProtocolV1.IsLowerSha256(MoviePrefixSha256)
                || TimingRunIndex < 0 || TimingRunStart < 0 || TimingRunStart > MovieFrame
                || !MovieFrameRate.IsValid(ActiveFrameRate) || !MovieFrameRate.IsValid(RecordingFrameRate)
                || NativeClock == null || NativeClock.Length != NativeFullRunFrameClock.ContinuationBytes
                || BitConverter.ToUInt32(NativeClock, 0) != 0x31435448 || BitConverter.ToInt32(NativeClock, 4) != 1
                || BitConverter.ToInt32(NativeClock, 8) != NativeClock.Length || BitConverter.ToUInt64(NativeClock, 80) != (ulong)NativeFrame
                || Input == null || Mouse == null || ManagedWorld == null || SceneLoad == null || Input.MovieFrame != MovieFrame)
                throw new InvalidDataException("Invalid checkpoint continuation binding, cursor or native schema.");
            if (InputEpoch == null) throw new InvalidDataException("Checkpoint input epoch is absent.");
            InputEpoch.RequireNativeContinuation(NativeClock);
            InputEpoch.RequireControlOrigin();
            if (double.IsNaN(GameUpdateDeltaSeconds) || double.IsInfinity(GameUpdateDeltaSeconds) || GameUpdateDeltaSeconds < 0
                || double.IsNaN(GameUpdateTimeSeconds) || double.IsInfinity(GameUpdateTimeSeconds)
                || double.IsNaN(ExecutedMovieSeconds) || double.IsInfinity(ExecutedMovieSeconds) || ExecutedMovieSeconds < 0
                || LastGameUpdateNativeFrame < -1 || LastGameUpdateNativeFrame > NativeFrame)
                throw new InvalidDataException("Checkpoint managed gameplay clocks differ from their source cursor.");
            var clockObserver = CheckpointClockObserverState.Decode(ClockObserver);
            clockObserver.RequireNativeReadback(NativeClock, NativeFrame, clockObserver.CaptureUnityFrameCount);
            if (ExecutionFiles == null || ExecutionFiles.Count != 4 || new[] { "runtime", "core", "clockPayload", "clockBridge" }
                .Any(name => !ExecutionFiles.TryGetValue(name, out var hash) || !MovieProtocolV1.IsLowerSha256(hash)))
                throw new InvalidDataException("Checkpoint execution file identities are incomplete.");
            var random = CheckpointRandomState.Decode(Random);
            if (random.AdvanceSequence != BitConverter.ToInt32(NativeClock, 88))
                throw new InvalidDataException("Managed/native clock observer sequences differ.");
        }
    }
}
