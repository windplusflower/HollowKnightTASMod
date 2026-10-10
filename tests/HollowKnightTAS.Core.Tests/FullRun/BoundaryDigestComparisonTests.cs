using System;
using System.Collections.Generic;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    public sealed class BoundaryDigestComparisonTests
    {
        private static BoundaryDigestSnapshot Digest(object[]? roots = null, object[]? scheduler = null,
            object[]? physics = null, SortedDictionary<string, string>? groups = null, string[]? gaps = null)
            => new BoundaryDigestSnapshot(groups ?? new SortedDictionary<string, string> { ["time"] = "f32:00000000" },
                roots ?? new object[] { new { id = "DDOL/Knight", type = "Hero", digest = "a" } },
                gaps ?? Array.Empty<string>(), scheduler ?? Array.Empty<object>(), physics ?? Array.Empty<object>(), Array.Empty<string>());

        [TestMethod]
        public void IdenticalObservedDataNeverClaimsClosedCoverage()
        {
            var comparison = BoundaryDigestComparison.Compare(Digest(gaps: new[] { "native-animator-state" }),
                Digest(gaps: new[] { "native-animator-state" }));
            Assert.IsTrue(comparison.ObservedEqual);
            Assert.IsFalse(comparison.CompleteStateCoverage);
            Assert.IsNull(comparison.FirstDifference);
            CollectionAssert.Contains(comparison.OpenDomains, "native-animator-state");
        }

        [TestMethod]
        public void RootDifferenceWinsOverMatchingAggregateGroups()
        {
            var comparison = BoundaryDigestComparison.Compare(Digest(), Digest(roots: new object[] {
                new { id = "DDOL/Knight", type = "Hero", digest = "b" } }));
            Assert.IsFalse(comparison.ObservedEqual);
            Assert.AreEqual("root:DDOL/Knight[record=0].digest", comparison.FirstDifference);
        }

        [TestMethod]
        public void RepeatedRootRecordsArePreserved()
        {
            var one = new { id = "same", type = "FSM", digest = "a" };
            var comparison = BoundaryDigestComparison.Compare(Digest(roots: new object[] { one, one }),
                Digest(roots: new object[] { one, new { id = "same", type = "FSM", digest = "b" } }));
            Assert.AreEqual("root:same[record=1].digest", comparison.FirstDifference);
        }

        [TestMethod]
        public void NativeCallbackOrderAndDeadlineAreCompared()
        {
            var comparison = BoundaryDigestComparison.Compare(Digest(scheduler: new object[] { new { kind = "callback", deadline = "f64:0000000000000000" } }),
                Digest(scheduler: new object[] { new { kind = "callback", deadline = "f64:0000000000000001" } }));
            Assert.AreEqual("nativeScheduler[0].deadline", comparison.FirstDifference);
        }

        [TestMethod]
        public void NativePhysicsAndRemovedRootsAreCompared()
        {
            Assert.AreEqual("nativePhysics.count", BoundaryDigestComparison.Compare(Digest(),
                Digest(physics: new object[] { new { kind = "proxy", id = "Hero" } })).FirstDifference);
            Assert.AreEqual("roots.count", BoundaryDigestComparison.Compare(Digest(), Digest(roots: Array.Empty<object>())).FirstDifference);
        }

        [TestMethod]
        public void FloatingBitsUseExactEquality()
        {
            var comparison = BoundaryDigestComparison.Compare(Digest(), Digest(groups: new SortedDictionary<string, string> { ["time"] = "f32:00000001" }));
            Assert.AreEqual("groups.time", comparison.FirstDifference);
        }

        [TestMethod]
        public void CallerMutationCannotChangeCapturedRecords()
        {
            var sourceRoot = new JObject { ["id"] = "DDOL/Knight", ["digest"] = "a" };
            var sourceGroups = new SortedDictionary<string, string> { ["time"] = "f32:00000000" };
            var captured = Digest(roots: new object[] { sourceRoot }, groups: sourceGroups);
            sourceRoot["digest"] = "b"; sourceGroups["time"] = "changed";
            var same = Digest(roots: new object[] { new JObject { ["id"] = "DDOL/Knight", ["digest"] = "a" } });
            Assert.IsTrue(BoundaryDigestComparison.Compare(captured, same).ObservedEqual);
        }

        [TestMethod]
        public void TamperingWithCapturedStateIsRejected()
        {
            var captured = Digest(); captured.Groups["time"] = "changed";
            Assert.ThrowsExactly<InvalidOperationException>(() => BoundaryDigestComparison.Compare(captured, Digest()));
        }

        [TestMethod]
        public void OversizedProofDataIsRejected()
            => Assert.ThrowsExactly<InvalidOperationException>(() => Digest(roots: new object[] {
                new { id = "huge", payload = new string('x', 16 * 1024 * 1024) } }));
    }
}
