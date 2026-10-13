using System;
using System.IO;
using System.Linq;
using HollowKnightTAS.Runtime.Timing;
using Newtonsoft.Json;

namespace HollowKnightTAS.Runtime.FullRun
{
    // A bounded actual D write experiment. It has no product checkpoint or
    // resume permission: every other D domain remains unapplied/unqualified.
    internal sealed class CanonicalClockApplyExperiment
    {
        internal const string Variable = "HKTAS_LOADING_CLOCK_APPLY_EXPERIMENT";
        public int Schema = 1;
        public long NativeFrame;
        public CanonicalClockCarry Clock = new CanonicalClockCarry();
        public CanonicalFsmTimeCarry.Data FsmTime = new CanonicalFsmTimeCarry.Data();
        public bool RestoreQualified => false;
        public bool CompleteStateCoverage => false;
        internal static bool Enabled => Environment.GetEnvironmentVariable(Variable) == "1";
        internal static CanonicalClockApplyExperiment Capture(long native) => new CanonicalClockApplyExperiment {
            NativeFrame = native, Clock = CanonicalClockCarry.Capture(native), FsmTime = CanonicalFsmTimeCarry.Capture() };
        internal static CanonicalClockApplyExperiment Read(string path)
        {
            if (!Enabled || new FileInfo(path).Length > 65536) throw new InvalidDataException("Bounded clock experiment is disabled or oversized.");
            var value = JsonConvert.DeserializeObject<CanonicalClockApplyExperiment>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Missing clock experiment source.");
            if (value.Schema != 1 || value.NativeFrame < 0 || value.Clock == null || value.FsmTime == null)
                throw new InvalidDataException("Unknown clock experiment source schema.");
            value.Clock.Validate(value.NativeFrame);
            return value;
        }
        internal void Apply(NativeFullRunFrameClock clock, long targetNative, Action<byte[], long> importRandom,
            Action<byte[], long> importObserver)
        {
            if (!Enabled || !clock.IsPaused || clock.IsFinished || clock.CurrentFrameIndex != targetNative)
                throw new InvalidOperationException("Clock D application requires its original parked target B.");
            Clock.Validate(NativeFrame);
            var imported = false;
            var fsm = CanonicalFsmTimeCarry.PrepareApply(FsmTime,
                () => imported && clock.IsPaused && !clock.IsFinished && clock.CurrentFrameIndex == NativeFrame);
            // Random import checks the old target cursor. Native import changes
            // the cursor; observer/FsmTime then require that actual source cursor.
            importRandom(Clock.Random, targetNative);
            clock.ImportContinuation(Clock.Native, targetNative);
            importObserver(Clock.Observer, NativeFrame);
            imported = true;
            fsm.Apply();
            var actual = CanonicalClockCarry.Capture(NativeFrame);
            if (!fsm.ReadBackMatches || !actual.Native.SequenceEqual(Clock.Native)
                || !actual.Random.SequenceEqual(Clock.Random) || !actual.Observer.SequenceEqual(Clock.Observer)
                || !clock.IsPaused || clock.CurrentFrameIndex != NativeFrame)
                throw new InvalidDataException("Applied clock D failed its complete raw readback.");
        }
    }
}
