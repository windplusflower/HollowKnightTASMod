using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    public sealed class CanonicalS0ClockDependencyComparisonTests
    {
        private const string Clock = "R2/HeroLight.activeState.HutongGames.PlayMaker.FsmState::<RealStartTime>k__BackingField";
        private static CanonicalS0ObservationSnapshot Sample(string path, string bits, string owner)
        {
            var result = new CanonicalS0ObservationSnapshot();
            result.Fields.Add(path, bits); result.FieldOwnership.Add(path, owner);
            return result;
        }
        [TestMethod]
        public void KeepsOriginalClockDifferenceWhileComparingIndependentS0()
        {
            var result = CanonicalS0Comparison.Compare(Sample(Clock, "f32:00-00-80-3F", "Dependency"), Sample(Clock, "f32:00-00-00-40", "Dependency"));
            Assert.IsFalse(result.ObservedEqual); Assert.IsTrue(result.ObservedS0Equal);
            Assert.AreEqual(Clock, result.FirstDifference); Assert.AreEqual(1, result.ClockDependencyDifferences.Length);
            Assert.AreEqual("f32:00-00-80-3F", result.ClockDependencyDifferences[0].Expected);
            Assert.IsFalse(result.ClockDependencyDifferences[0].RestoreBindingQualified);
            Assert.IsFalse(result.D1Passed); Assert.IsFalse(result.RestoreQualified);
        }
        [TestMethod]
        public void ChangedIndependentS0FloatStillFails()
        {
            var result = CanonicalS0Comparison.Compare(Sample("R2/scale.x", "f32:00-00-00-00", "S0"), Sample("R2/scale.x", "f32:00-00-00-80", "S0"));
            Assert.IsFalse(result.ObservedS0Equal); Assert.AreEqual("R2/scale.x", result.FirstS0Difference);
        }
        [TestMethod]
        public void UnknownClockLikeFieldCannotEscapeComparison()
        {
            var result = CanonicalS0Comparison.Compare(Sample("unknown.startTime", "f32:00-00-80-3F", "Dependency"), Sample("unknown.startTime", "f32:00-00-00-40", "Dependency"));
            Assert.IsFalse(result.ObservedS0Equal); Assert.AreEqual(0, result.ClockDependencyDifferences.Length);
        }
        [TestMethod]
        public void OwnershipMismatchCannotEscapeComparison()
        {
            var result = CanonicalS0Comparison.Compare(Sample(Clock, "f32:00-00-80-3F", "Dependency"), Sample(Clock, "f32:00-00-80-3F", "S0"));
            Assert.IsFalse(result.ObservedS0Equal); Assert.AreEqual(Clock, result.FirstS0Difference);
        }
        [TestMethod]
        public void NonfiniteClockBitsCannotEscapeComparison()
        {
            var result = CanonicalS0Comparison.Compare(Sample(Clock, "f32:00-00-80-3F", "Dependency"), Sample(Clock, "f32:00-00-C0-7F", "Dependency"));
            Assert.IsFalse(result.ObservedS0Equal); Assert.AreEqual(0, result.ClockDependencyDifferences.Length);
        }
        [TestMethod]
        public void WaitStartTimeCannotEscapeWithoutOriginalEntryContract()
        {
            const string wait = "R2/action.HutongGames.PlayMaker.Actions.Wait::startTime";
            var result = CanonicalS0Comparison.Compare(Sample(wait, "f32:00-00-80-3F", "Dependency"), Sample(wait, "f32:00-00-00-40", "Dependency"));
            Assert.IsFalse(result.ObservedS0Equal); Assert.AreEqual(0, result.ClockDependencyDifferences.Length);
        }
    }
}
