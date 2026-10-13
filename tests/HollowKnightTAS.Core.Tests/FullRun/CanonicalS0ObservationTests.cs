using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    public sealed class CanonicalS0ObservationTests
    {
        private sealed class State { public float Bits; public object? Left; public object? Right; public object? Unknown; public int GetterCalls = 0; public object Dangerous => throw new Exception("getter called"); }
        private sealed class Carry { public int Value; public object? Extra; }
        private static CanonicalS0Observation Reader() => new CanonicalS0Observation(
            (owner, field) => owner is State && field.Name != "Unknown" ? CanonicalS0Observation.Ownership.S0 : CanonicalS0Observation.Ownership.Unknown,
            value => null,
            type => type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).OrderBy(x => x.Name, StringComparer.Ordinal).ToArray(),
            (owner, field) => field.GetValue(owner));
        private static CanonicalS0Observation.Root S(string key, object value) => new CanonicalS0Observation.Root(key, value, CanonicalS0Observation.Ownership.S0);
        private static CanonicalS0Observation.Root D(string key, object value) => new CanonicalS0Observation.Root(key, value, CanonicalS0Observation.Ownership.D);
        [TestMethod]
        public void CarryValuesAndUnrelatedGraphSizeCannotChangeS0()
        {
            var carry = new Carry { Value = 1 }; var state = new State { Bits = 1, Left = carry, Right = carry };
            var first = Reader().Capture(new[] { S("S", state), D("D/owner", carry) });
            carry.Value = 991; carry.Extra = new[] { new Carry(), new Carry() };
            var second = Reader().Capture(new[] { D("D/unrelated", new Carry()), D("D/owner", carry), S("S", state) });
            Assert.IsTrue(CanonicalS0Comparison.Compare(first, second).ObservedEqual);
            Assert.AreEqual(first.Sha256, second.Sha256);
        }
        [TestMethod]
        public void EverySingleFloatBitChangesTheObservedField()
        {
            var state = new State(); var baseline = Reader().Capture(new[] { S("S", state) });
            for (var bit = 0; bit < 32; bit++)
            {
                state.Bits = BitConverter.ToSingle(BitConverter.GetBytes(1u << bit), 0);
                var result = CanonicalS0Comparison.Compare(baseline, Reader().Capture(new[] { S("S", state) }));
                Assert.IsFalse(result.ObservedEqual, "bit " + bit);
                StringAssert.Contains(result.FirstDifference!, "::Bits");
            }
        }
        [TestMethod]
        public void CarrySlotRebindingAndAliasesAcrossS0RootsAreVisible()
        {
            var one = new Carry(); var two = new Carry(); var firstState = new State { Left = one }; var otherState = new State { Left = one };
            var roots = new[] { S("A", firstState), S("B", otherState), D("D/one", one), D("D/two", two) };
            var baseline = Reader().Capture(roots); otherState.Left = two;
            var changed = CanonicalS0Comparison.Compare(baseline, Reader().Capture(roots));
            Assert.IsFalse(changed.ObservedEqual); StringAssert.Contains(changed.FirstDifference!, "B.");
        }
        [TestMethod]
        public void SameLogicalBindingStillRetainsDistinctObjectAliases()
        {
            var one = new Carry(); var two = new Carry(); var state = new State { Left = one, Right = one };
            CanonicalS0Observation reader() => new CanonicalS0Observation((o, f) => f.Name == "Left" || f.Name == "Right" ? CanonicalS0Observation.Ownership.D : CanonicalS0Observation.Ownership.Unknown,
                o => o is Carry ? "registered-owner" : null,
                t => t.GetFields(), (o, f) => f.GetValue(o));
            var baseline = reader().Capture(new[] { S("S", state) }); state.Right = two;
            Assert.IsFalse(CanonicalS0Comparison.Compare(baseline, reader().Capture(new[] { S("S", state) })).ObservedEqual);
        }
        [TestMethod]
        public void CrossDomainRootAliasSetIsPreserved()
        {
            var one = new Carry(); var two = new Carry(); var state = new State { Left = one };
            var baseline = Reader().Capture(new[] { S("S", state), D("D/a", one), D("D/b", one) });
            var changed = Reader().Capture(new[] { S("S", state), D("D/a", one), D("D/b", two) });
            Assert.IsFalse(CanonicalS0Comparison.Compare(baseline, changed).ObservedEqual);
        }
        [TestMethod]
        public void ExplicitS0RootSharingCarryIdentityStillObservesS0Fields()
        {
            var state = new State(); var roots = new[] { S("S", state), D("D", state) };
            var before = Reader().Capture(roots); state.Bits = 1;
            var after = Reader().Capture(roots);
            Assert.IsFalse(CanonicalS0Comparison.Compare(before, after).ObservedEqual);
            Assert.IsTrue(before.Fields.Any(x => x.Key.EndsWith(".$D-root-bindings", StringComparison.Ordinal)));
        }
        [TestMethod]
        public void CarryEdgeSeenBeforeExplicitS0RootDoesNotSuppressExpansion()
        {
            var state = new State(); var edge = new State { Left = state };
            CanonicalS0Observation reader() => new CanonicalS0Observation((o, f) => ReferenceEquals(o, edge) && f.Name == "Left"
                ? CanonicalS0Observation.Ownership.D : CanonicalS0Observation.Ownership.S0, o => null,
                t => t.GetFields(), (o, f) => f.GetValue(o));
            var roots = new[] { S("A", edge), S("Z", state), D("D", state) };
            var first = reader().Capture(roots); state.Bits = 1;
            Assert.IsFalse(CanonicalS0Comparison.Compare(first, reader().Capture(roots)).ObservedEqual);
        }
        [TestMethod]
        public void UnknownZeroFieldOwnerIsReportedOpen()
        {
            var sample = Reader().Capture(new[] { S("S", new object()) });
            Assert.IsTrue(sample.Unclassified.Any(x => x.Contains("owner-unclassified")));
        }
        [TestMethod]
        public void DecimalScaleAndSignedZeroBitsAreRetained()
        {
            var first = Reader().Capture(new[] { S("S", new decimal(0, 0, 0, false, 0)) });
            var second = Reader().Capture(new[] { S("S", new decimal(0, 0, 0, true, 7)) });
            Assert.IsFalse(CanonicalS0Comparison.Compare(first, second).ObservedEqual);
        }
        [TestMethod]
        public void EqualStringContentsDoNotEraseReferenceAliases()
        {
            var text = new string(new[] { 'a', 'b' }); var state = new State { Left = text, Right = text };
            var first = Reader().Capture(new[] { S("S", state) }); state.Right = new string(new[] { 'a', 'b' });
            Assert.IsFalse(CanonicalS0Comparison.Compare(first, Reader().Capture(new[] { S("S", state) })).ObservedEqual);
        }
        [TestMethod]
        public void UnknownFieldsAreOpenWithoutReadingAndDoNotMaskObservedDifference()
        {
            var state = new State { Unknown = new Carry() }; var first = Reader().Capture(new[] { S("S", state) });
            Assert.IsTrue(first.Unclassified.Any(x => x.Contains("::Unknown")));
            state.Bits = 2; var comparison = CanonicalS0Comparison.Compare(first, Reader().Capture(new[] { S("S", state) }));
            Assert.IsFalse(comparison.ObservedEqual); Assert.IsTrue(comparison.OpenUnclassified.Length > 0);
            Assert.IsFalse(comparison.D1Passed); Assert.IsFalse(comparison.RestoreQualified);
        }
        [TestMethod]
        public void UnknownCarryOwnerIsExplicitlyOpen()
        {
            var state = new State { Left = new Carry() }; var sample = Reader().Capture(new[] { S("S", state) });
            Assert.IsTrue(sample.Unclassified.Any(x => x.Contains("Carry::Value")));
        }
        [TestMethod]
        public void RepeatedCapturePreservesObjectsAndNeverEvaluatesProperties()
        {
            var carry = new Carry { Value = 42 }; var state = new State { Bits = -0.0f, Left = carry, Right = carry };
            var reader = Reader(); var roots = new[] { S("S", state), D("D", carry) };
            var first = reader.Capture(roots); var second = reader.Capture(roots);
            Assert.AreEqual(first.Sha256, second.Sha256); Assert.AreSame(carry, state.Left); Assert.AreEqual(42, carry.Value); Assert.AreEqual(0, state.GetterCalls);
        }
    }
}
