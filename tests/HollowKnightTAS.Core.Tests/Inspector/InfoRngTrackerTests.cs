using System;
using HollowKnightTAS.Core.Inspector;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.Inspector
{
    [TestClass]
    public sealed class InfoRngTrackerTests
    {
        private static void Advance(ref uint s0, ref uint s1, ref uint s2, ref uint s3, int steps)
        {
            for (int i = 0; i < steps; i++)
                InfoRngTracker.StepXorshift128(ref s0, ref s1, ref s2, ref s3);
        }

        [TestMethod]
        public void TracksInitialBaselineZeroAndMultiStepAdvancement()
        {
            var tracker = new InfoRngTracker();
            Assert.IsFalse(tracker.HasValue);
            Assert.IsNull(tracker.Formatted);

            uint s0 = 0x12345678u, s1 = 0x9ABCDEF0u, s2 = 0x0F1E2D3Cu, s3 = 0x4B5A6978u;
            tracker.Observe(s0, s1, s2, s3);
            Assert.IsTrue(tracker.HasValue);
            Assert.AreEqual(0L, tracker.RollTimes);
            Assert.AreEqual(0, tracker.LastDelta);
            Assert.IsFalse(tracker.LastStepOverflowed);
            Assert.AreEqual("0 +0", tracker.Formatted);

            tracker.Observe(s0, s1, s2, s3);
            Assert.AreEqual(0L, tracker.RollTimes);
            Assert.AreEqual(0, tracker.LastDelta);
            Assert.IsFalse(tracker.LastStepOverflowed);
            Assert.AreEqual("0 +0", tracker.Formatted);

            Advance(ref s0, ref s1, ref s2, ref s3, 1);
            tracker.Observe(s0, s1, s2, s3);
            Assert.AreEqual(1L, tracker.RollTimes);
            Assert.AreEqual(1, tracker.LastDelta);
            Assert.IsFalse(tracker.LastStepOverflowed);
            Assert.AreEqual("1 +1", tracker.Formatted);

            Advance(ref s0, ref s1, ref s2, ref s3, 5);
            tracker.Observe(s0, s1, s2, s3);
            Assert.AreEqual(6L, tracker.RollTimes);
            Assert.AreEqual(5, tracker.LastDelta);
            Assert.IsFalse(tracker.LastStepOverflowed);
            Assert.AreEqual("6 +5", tracker.Formatted);
        }

        [TestMethod]
        public void ResyncsBaselineOnOverflowAndSyncBaseline()
        {
            var tracker = new InfoRngTracker();
            uint s0 = 1u, s1 = 2u, s2 = 3u, s3 = 4u;
            tracker.SyncBaseline(s0, s1, s2, s3);
            Assert.IsTrue(tracker.HasValue);
            Assert.AreEqual("0 +0", tracker.Formatted);

            Advance(ref s0, ref s1, ref s2, ref s3, 3);
            tracker.Observe(s0, s1, s2, s3, maxSteps: 10);
            Assert.AreEqual(3L, tracker.RollTimes);
            Assert.AreEqual(3, tracker.LastDelta);

            // Jump past maxSteps (e.g. Random.InitState or S0 discontinuity) without hanging.
            Advance(ref s0, ref s1, ref s2, ref s3, 25);
            tracker.Observe(s0, s1, s2, s3, maxSteps: 10);
            Assert.IsTrue(tracker.LastStepOverflowed);
            Assert.AreEqual(3L, tracker.RollTimes);
            Assert.AreEqual(0, tracker.LastDelta);
            Assert.AreEqual("3 +0", tracker.Formatted);

            // Subsequent normal steps resume from the newly synchronized baseline.
            Advance(ref s0, ref s1, ref s2, ref s3, 2);
            tracker.Observe(s0, s1, s2, s3, maxSteps: 10);
            Assert.IsFalse(tracker.LastStepOverflowed);
            Assert.AreEqual(5L, tracker.RollTimes);
            Assert.AreEqual(2, tracker.LastDelta);
            Assert.AreEqual("5 +2", tracker.Formatted);

            // SyncBaseline preserves RollTimes and resets LastDelta.
            tracker.SyncBaseline(0xAABBCCDDu, 0x11223344u, 0x55667788u, 0x99AABBCCu);
            Assert.AreEqual(5L, tracker.RollTimes);
            Assert.AreEqual(0, tracker.LastDelta);
            Assert.IsFalse(tracker.LastStepOverflowed);
        }

        [TestMethod]
        public void RejectsNonPositiveMaxStepsAndFormatsLargeCounts()
        {
            var tracker = new InfoRngTracker();
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => tracker.Observe(1u, 2u, 3u, 4u, 0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => tracker.Observe(1u, 2u, 3u, 4u, -5));
            Assert.AreEqual("1234 +5", InfoRngTracker.Format(1234L, 5));
        }
    }
}
