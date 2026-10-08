using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    public sealed class LoadingFieldDigestTests
    {
        private static LoadingFieldDigest NewDigest() => new LoadingFieldDigest(value => null, type => false);

        [TestMethod]
        public void ReusedDigestKeepsThePinnedTokenEncodingAcrossCapturesAndDiagnostics()
        {
            using var digest = NewDigest();
            var owner = new Derived(1.25f, 7);
            const string expected = "833ab8aa506014bc5e544e4be8b829d7a29f210d8cb32043bde52ad06758a4ba";
            Assert.AreEqual(expected, digest.Capture(owner, "root").Sha256);
            digest.Capture(new[] { 1, 2, 3 }, "another-root");
            Assert.AreEqual(expected, digest.Capture(owner, "root", includeFieldDiagnostics: true).Sha256);
            Assert.AreEqual(expected, digest.Capture(owner, "root").Sha256);
        }

        [TestMethod]
        public void NestedFieldDiagnosticsPreserveTheObservedDigestAndOnlyExportHashes()
        {
            var owner = new Derived(1.25f, 7);
            var digest = NewDigest();
            var plain = digest.Capture(owner, "root");
            var detailed = digest.Capture(owner, "root", includeFieldDiagnostics: true);
            Assert.AreEqual(plain.Sha256, detailed.Sha256);
            Assert.AreEqual(0, plain.FieldDigests.Length);
            Assert.IsTrue(detailed.FieldDigests.Length > 0);
            foreach (var item in detailed.FieldDigests)
            {
                var properties = item.GetType().GetProperties();
                CollectionAssert.AreEquivalent(new[] { "path", "type", "digest" }, properties.Select(p => p.Name).ToArray());
                Assert.AreEqual(64, ((string)properties.Single(p => p.Name == "digest").GetValue(item)!).Length);
            }
            Assert.AreEqual(0, digest.Capture(owner, "root").FieldDigests.Length);
        }

        [TestMethod]
        public void FieldReadsDoNotInvokeGettersEnumeratorsOrToString()
        {
            var result = NewDigest().Capture(new HostileObject(), "root");
            Assert.AreEqual(64, result.Sha256.Length);
            Assert.AreEqual(0, result.Unsupported.Count);
        }

        [TestMethod]
        public void ObserverSubscriptionsDoNotHideGameplayCallbackOrderOrTargetState()
        {
            var observer = new ObservationListener();
            var game = new GameplayListener(7);
            var digest = new LoadingFieldDigest(value => null, type => false,
                observerCallback: callback => callback.Method.DeclaringType == typeof(ObservationListener));
            var empty = digest.Capture(new EventOwner(null), "root").Sha256;
            Assert.AreEqual(empty, digest.Capture(new EventOwner(observer.Observe), "root").Sha256);
            Assert.AreNotEqual(NewDigest().Capture(new EventOwner(null), "root").Sha256,
                NewDigest().Capture(new EventOwner(observer.Observe), "root").Sha256);

            Action callbacks = game.First;
            callbacks += game.Second;
            var expected = digest.Capture(new EventOwner(callbacks), "root").Sha256;
            Action withObservers = observer.Observe;
            withObservers += game.First;
            withObservers += observer.Observe;
            withObservers += game.Second;
            Assert.AreEqual(expected, digest.Capture(new EventOwner(withObservers), "root").Sha256);
            Action reordered = game.Second;
            reordered += game.First;
            Assert.AreNotEqual(expected, digest.Capture(new EventOwner(reordered), "root").Sha256);
            game.Value++;
            Assert.AreNotEqual(expected, digest.Capture(new EventOwner(withObservers), "root").Sha256);
        }

        [TestMethod]
        public void CyclesAndSharedReferencesRemainDeterministic()
        {
            Node Graph() { var node = new Node(); node.A = node; node.B = node; return node; }
            Assert.AreEqual(NewDigest().Capture(Graph(), "root").Sha256, NewDigest().Capture(Graph(), "root").Sha256);
            var changed = Graph(); changed.B = new Node();
            Assert.AreNotEqual(NewDigest().Capture(Graph(), "root").Sha256, NewDigest().Capture(changed, "root").Sha256);
        }

        [TestMethod]
        public void SharedRootGraphCapturesAliasesValuesAndDoesNotLeakBetweenSamples()
        {
            string[] Capture(bool alias, int value)
            {
                using var digest = NewDigest();
                var first = new Derived(1.25f, value);
                var second = alias ? first : new Derived(1.25f, value);
                digest.BeginSharedGraph();
                try { return new[] { digest.Capture(first, "a").Sha256, digest.Capture(second, "b").Sha256 }; }
                finally { digest.EndSharedGraph(); }
            }
            CollectionAssert.AreEqual(Capture(true, 7), Capture(true, 7));
            CollectionAssert.AreNotEqual(Capture(true, 7), Capture(false, 7));
            CollectionAssert.AreNotEqual(Capture(true, 7), Capture(true, 8));

            using var reused = NewDigest();
            var owner = new Derived(1.25f, 7);
            var independent = reused.Capture(owner, "root");
            reused.BeginSharedGraph();
            Assert.AreEqual(independent.Sha256, reused.Capture(owner, "root").Sha256);
            Assert.IsTrue(reused.Capture(owner, "root").Nodes < independent.Nodes);
            reused.EndSharedGraph();
            Assert.AreEqual(independent.Sha256, reused.Capture(owner, "root").Sha256);
        }

        [TestMethod]
        public void ExtraFieldDiagnosticsCannotChangeTheSharedRootGraph()
        {
            string Next(bool diagnostic)
            {
                using var digest = NewDigest();
                var owner = new Node { A = new Node(), B = new Node() };
                digest.BeginSharedGraph();
                try
                {
                    digest.Capture(owner, "a");
                    if (diagnostic) digest.CaptureFields(new Node { A = new Node() }, "extra", "A");
                    return digest.Capture(new Node { A = owner, B = new Node() }, "b").Sha256;
                }
                finally { digest.EndSharedGraph(); }
            }
            Assert.AreEqual(Next(false), Next(true));
        }

        [TestMethod]
        public void RawFloatBitsAndInheritedPrivateFieldsAffectDigest()
        {
            var digest = NewDigest();
            Assert.AreNotEqual(digest.Capture(new Derived(0f, 1), "root").Sha256,
                digest.Capture(new Derived(BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)), 1), "root").Sha256);
            Assert.AreNotEqual(digest.Capture(new Derived(0f, 1), "root").Sha256,
                digest.Capture(new Derived(0f, 2), "root").Sha256);
        }

        [TestMethod]
        public void NativePointersAreExplicitOmissionsAndGraphOverflowRejects()
        {
            var digest = NewDigest();
            var first = digest.Capture(new IntPtr(12), "coroutine");
            Assert.AreEqual("coroutine:native-pointer", first.Unsupported.Single());
            Assert.AreEqual(first.Sha256, digest.Capture(new IntPtr(42), "coroutine").Sha256);
            Assert.ThrowsExactly<InvalidOperationException>(() => digest.Capture(new int[50001], "too-large"));
        }

        [TestMethod]
        public void DeferredPathsKeepNestedOmissionsAndRecoverAfterARejectedCapture()
        {
            using var digest = NewDigest();
            var owner = new Dictionary<string, object> { ["ports"] = new[] { new IntPtr(12), new IntPtr(42) } };
            CollectionAssert.AreEqual(new[] { "root.value[0][0]:native-pointer", "root.value[0][1]:native-pointer" },
                digest.Capture(owner, "root").Unsupported.ToArray());
            var error = Assert.ThrowsExactly<InvalidOperationException>(() => digest.Capture(new int[50001], "too-large"));
            StringAssert.Contains(error.Message, "too-large[49999]");
            CollectionAssert.AreEqual(new[] { "next.value[0][0]:native-pointer", "next.value[0][1]:native-pointer" },
                digest.Capture(owner, "next", includeFieldDiagnostics: true).Unsupported.ToArray());
        }

        [TestMethod]
        public void DictionaryIdentityPreservesContentOrderAndFreeSlotsWithoutHashCaches()
        {
            var first = new Dictionary<Key, int>(2) { [new Key(1, 5)] = 7, [new Key(2, 8)] = 9 };
            var second = new Dictionary<Key, int>(100) { [new Key(1, 25)] = 7, [new Key(2, 80)] = 9 };
            LoadingFieldDigest Digest() => new LoadingFieldDigest(value => value is Key key ? "key:" + key.Id : null, type => false);
            var expected = Digest().Capture(first, "pool").Sha256;
            Assert.AreEqual(expected, Digest().Capture(second, "pool").Sha256);
            second[new Key(2, 80)] = 10;
            Assert.AreNotEqual(expected, Digest().Capture(second, "pool").Sha256);
            var reordered = new Dictionary<Key, int> { [new Key(2, 8)] = 9, [new Key(1, 5)] = 7 };
            Assert.AreNotEqual(expected, Digest().Capture(reordered, "pool").Sha256);
            first.Remove(first.Keys.First());
            var withoutFreeSlot = new Dictionary<Key, int> { [new Key(2, 8)] = 9 };
            Assert.AreNotEqual(Digest().Capture(first, "pool").Sha256, Digest().Capture(withoutFreeSlot, "pool").Sha256);
        }

        [TestMethod]
        public void FieldIdentityDoesNotHideOtherScalarState()
        {
            var digest = new LoadingFieldDigest(value => null, type => false,
                (owner, field, value) => field.Name == "ProcessBinding" ? "binding:slot" : null);
            Assert.AreEqual(digest.Capture(new Binding(1, 7), "root").Sha256,
                digest.Capture(new Binding(2, 7), "root").Sha256);
            Assert.AreNotEqual(digest.Capture(new Binding(1, 7), "root").Sha256,
                digest.Capture(new Binding(2, 8), "root").Sha256);
        }

        [TestMethod]
        public void HashSetRebindingPreservesSlotsValuesCapacityAndFreeReuse()
        {
            LoadingFieldDigest Digest() => new LoadingFieldDigest(value => value is Key key ? "key:" + key.Id : null, type => false);
            var first = new HashSet<Key> { new Key(1, 5), new Key(2, 5) };
            var second = new HashSet<Key> { new Key(1, 25), new Key(2, 80) };
            using var digest = Digest();
            var expected = digest.Capture(first, "root").Sha256;
            Assert.AreEqual(expected, digest.Capture(second, "root").Sha256);
            Assert.AreNotEqual(expected, digest.Capture(new HashSet<Key> { new Key(2, 5), new Key(1, 5) }, "root").Sha256);
            Assert.AreNotEqual(expected, digest.Capture(new HashSet<Key> { new Key(1, 5), new Key(3, 5) }, "root").Sha256);
            second.EnsureCapacity(100);
            Assert.AreNotEqual(expected, digest.Capture(second, "root").Sha256);
            first.Remove(first.First());
            var removed = digest.Capture(first, "root").Sha256;
            Assert.AreNotEqual(expected, removed);
            first.Add(new Key(1, 5));
            Assert.AreNotEqual(expected, digest.Capture(first, "root").Sha256);
        }

        private sealed class Binding
        {
            internal readonly int ProcessBinding, Value;
            internal Binding(int binding, int value) { ProcessBinding = binding; Value = value; }
        }
        private sealed class EventOwner
        {
            private readonly Action? callback;
            internal EventOwner(Action? callback) { this.callback = callback; }
        }
        private sealed class ObservationListener
        {
            internal void Observe() => throw new InvalidOperationException("An observation callback was invoked.");
        }
        private sealed class GameplayListener
        {
            internal int Value;
            internal GameplayListener(int value) { Value = value; }
            internal void First() => throw new InvalidOperationException("A gameplay callback was invoked.");
            internal void Second() => throw new InvalidOperationException("A gameplay callback was invoked.");
        }
        private sealed class Key
        {
            internal readonly int Id;
            private readonly int processHash;
            internal Key(int id, int hash) { Id = id; processHash = hash; }
            public override int GetHashCode() => processHash;
            public override bool Equals(object? other) => other is Key key && key.Id == Id;
        }

        private sealed class HostileObject : IEnumerable
        {
            public int ThrowingProperty => throw new InvalidOperationException("getter invoked");
            public IEnumerator GetEnumerator() => throw new InvalidOperationException("enumerator invoked");
            public override string ToString() => throw new InvalidOperationException("ToString invoked");
        }
        private sealed class Node { internal Node? A; internal Node? B; }
        private class Base
        {
            private readonly int inherited;
            internal Base(int value) { inherited = value; }
        }
        private sealed class Derived : Base
        {
            private readonly float number;
            internal Derived(float value, int parent) : base(parent) { number = value; }
        }
    }
}
