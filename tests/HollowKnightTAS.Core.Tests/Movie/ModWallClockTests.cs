using System;
using HollowKnightTAS.Core.Movie;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.Movie
{
    [TestClass]
    public sealed class ModWallClockTests
    {
        [TestMethod]
        public void RealtimeUsesFixedLocalEpochAndPreservesTimerIntervals()
        {
            var origin = ModWallClock.LocalNow(0);
            Assert.AreEqual(new DateTime(2000, 1, 1), origin);
            Assert.AreEqual(DateTimeKind.Local, origin.Kind);
            Assert.AreEqual(1000d, (ModWallClock.LocalNow(11.25) - ModWallClock.LocalNow(10.25)).TotalMilliseconds);
            Assert.AreEqual(1L, (ModWallClock.LocalNow(0.0000001) - origin).Ticks);
        }

        [TestMethod]
        public void InvalidOrUnrepresentableTimeRejects()
        {
            foreach (var value in new[] { -1d, double.NaN, double.PositiveInfinity, double.MaxValue })
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ModWallClock.LocalNow(value));
        }
    }
}
