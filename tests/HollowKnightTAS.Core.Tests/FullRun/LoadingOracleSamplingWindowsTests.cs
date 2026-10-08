using System;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    public sealed class LoadingOracleSamplingWindowsTests
    {
        [TestMethod]
        public void DefaultKeepsTheEntireDeclaredWindow()
        {
            var plan = LoadingOracleSamplingWindows.Parse(null, 1400, 3150);
            Assert.IsFalse(plan.ExplicitWindows);
            Assert.AreEqual(1750L, plan.GameplayFrames);
            Assert.IsFalse(plan.Contains(1399));
            Assert.IsTrue(plan.Contains(1400));
            Assert.IsTrue(plan.Contains(3149));
            Assert.IsFalse(plan.Contains(3150));
        }

        [TestMethod]
        public void ShortWindowsCaptureEveryFrameInsideEachIntervalAndNothingInTheGaps()
        {
            var plan = LoadingOracleSamplingWindows.Parse("1418:1424,2196:2203,2912:2919", 1400, 4000);
            Assert.IsTrue(plan.ExplicitWindows);
            Assert.AreEqual(20L, plan.GameplayFrames);
            foreach (var frame in new[] { 1418L, 1423, 2196, 2202, 2912, 2918 })
                Assert.IsTrue(plan.Contains(frame));
            foreach (var frame in new[] { 1400L, 1417, 1424, 2195, 2203, 2911, 2919, 3999 })
                Assert.IsFalse(plan.Contains(frame));
        }

        [TestMethod]
        public void EveryIntervalRetainsItsOwnCompleteInventoryEndpoints()
        {
            var plan = LoadingOracleSamplingWindows.Parse("1418:1424,2196:2203", 1400, 4000);
            Assert.IsTrue(plan.IsEndpoint(1418, "before"));
            Assert.IsTrue(plan.IsEndpoint(1423, "after"));
            Assert.IsTrue(plan.IsEndpoint(2196, "before"));
            Assert.IsTrue(plan.IsEndpoint(2202, "after"));
            Assert.IsFalse(plan.IsEndpoint(1418, "after"));
            Assert.IsFalse(plan.IsEndpoint(1423, "before"));
            Assert.IsFalse(plan.IsEndpoint(1418, "event"));
        }

        [TestMethod]
        public void InvalidDeclarationsAreRejectedInsteadOfSkippingSamples()
        {
            foreach (var text in new[] { "", ",", "1", "1418:1424,", "1418:1418", "1399:1401", "3999:4001",
                "1418:1424,1423:1425", "2196:2203,1418:1424", "+1418:1424", "1418 :1424", "1:2:3",
                "999999999999999999999999:4000" })
                Assert.ThrowsExactly<ArgumentException>(() => LoadingOracleSamplingWindows.Parse(text, 1400, 4000), text);
        }
    }
}
