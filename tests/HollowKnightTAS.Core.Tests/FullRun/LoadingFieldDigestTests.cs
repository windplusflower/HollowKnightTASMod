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
        public void RawCollectionGuardIncludesBucketsAndUnusedSlotsWithoutChangingDefaultDigest()
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            foreach (var owner in new object[] { new Dictionary<int, int>(17) { [1] = 2 }, new HashSet<int>(17) { 1 } }) {
                using var logical = NewDigest();
                using var raw = new LoadingFieldDigest(_ => null, _ => false, rawCollectionFields: true);
                string Hash(LoadingFieldDigest reader) {
                    var result = reader.Capture(owner, "collection", expandRoot: true);
                    Assert.AreEqual(0, result.Unsupported.Count);
                    return result.Sha256;
                }
                var logicalBefore = Hash(logical); var rawBefore = Hash(raw);
                var buckets = (int[])owner.GetType().GetField("_buckets", flags)!.GetValue(owner)!;
                var originalBucket = buckets[^1]; buckets[^1] = 314;
                Assert.AreEqual(logicalBefore, Hash(logical));
                Assert.AreNotEqual(rawBefore, Hash(raw));
                buckets[^1] = originalBucket;
                Assert.AreEqual(rawBefore, Hash(raw));

                var entries = (Array)owner.GetType().GetField("_entries", flags)!.GetValue(owner)!;
                var last = entries.GetValue(entries.Length - 1)!;
                var value = last.GetType().GetFields(flags).Single(field => field.Name is "value" or "Value");
                var oldValue = value.GetValue(last); value.SetValue(last, 271);
                entries.SetValue(last, entries.Length - 1);
                Assert.AreEqual(logicalBefore, Hash(logical));
                Assert.AreNotEqual(rawBefore, Hash(raw));
                value.SetValue(last, oldValue); entries.SetValue(last, entries.Length - 1);
                Assert.AreEqual(rawBefore, Hash(raw));
            }
        }

        [TestMethod]
        public void ExactCollectionTypeObservesRawEntryFreeAndTailAliasesWithoutChangingOtherCollections()
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var live = new List<Node> { new Node() }; var free = new List<Node> { new Node() }; var tail = live;
            var owner = new Dictionary<string, List<Node>>(17) { ["live"] = live, ["removed"] = free };
            owner.Remove("removed");
            var entries = (Array)owner.GetType().GetField("_entries", flags)!.GetValue(owner)!;
            var valueField = entries.GetType().GetElementType()!.GetField("value", flags)!;
            void SetRetainedValue(int index, object value) {
                var entry = entries.GetValue(index)!; valueField.SetValue(entry, value); entries.SetValue(entry, index);
            }
            SetRetainedValue(1, free); SetRetainedValue(entries.Length - 1, tail);
            var slots = new List<(string Path, object Value)>();
            using var selected = new LoadingFieldDigest(_ => null, _ => false,
                slotObservation: (container, field, index, value, path) => {
                    if (container.GetType().IsValueType && field == valueField && value != null) slots.Add((path, value));
                }, rawCollectionType: type => type == owner.GetType());
            using var raw = new LoadingFieldDigest(_ => null, _ => false, rawCollectionFields: true);
            var captured = selected.Capture(owner, "registry", expandRoot: true);
            var expectedSlots = new[] { ("registry._entries[0].value", (object)live),
                ("registry._entries[1].value", (object)free), ($"registry._entries[{entries.Length - 1}].value", (object)tail) };
            Assert.AreEqual(0, captured.Unsupported.Count);
            Assert.AreEqual(raw.Capture(owner, "registry", expandRoot: true).Sha256, captured.Sha256);
            CollectionAssert.AreEqual(expectedSlots, slots.ToArray());
            slots.Clear();
            var references = selected.CaptureReferences(owner, "registry", expandRoot: true);
            Assert.AreEqual(0, references.Unsupported.Count);
            Assert.AreEqual(captured.Nodes, references.Nodes);
            CollectionAssert.AreEqual(expectedSlots, slots.ToArray());

            using var logical = NewDigest();
            foreach (var other in new object[] { new Dictionary<string, object>(17) { ["live"] = live }, new HashSet<Node>(17) { new Node() } })
                Assert.AreEqual(logical.Capture(other, "other", expandRoot: true).Sha256,
                    selected.Capture(other, "other", expandRoot: true).Sha256);
        }

        [TestMethod]
        public void OwnedSlotPathFilterKeepsActualAliasesOrderHashAndBudgetRejection()
        {
            var owned = new Node(); owned.A = owned; owned.B = owned;
            var root = Enumerable.Range(0, 1000).Select(index => (object)new PathCell {
                Alias = index % 97 == 0 ? owned : null, Number = index, Text = "full-value-" + index,
                Extra = new object[] { index, index % 83 == 0 ? owned : new Node() } }).ToArray();
            bool Owns(object? value) => ReferenceEquals(value, owned);
            var expected = new List<(object Owner, string? Field, int Index, object? Value, string Path)>();
            var actual = new List<(object Owner, string? Field, int Index, object? Value, string Path)>();
            LoadingFieldDigest Reader(bool filtered, List<(object, string?, int, object?, string)> slots)
                => new LoadingFieldDigest(_ => null, _ => false,
                    slotObservation: (owner, field, index, value, path) => { if (Owns(value)) slots.Add((owner, field?.Name, index, value, path)); },
                    observeSlotValue: filtered ? Owns : null);
            using var full = Reader(false, expected); using var lazy = Reader(true, actual);
            var expectedDigest = full.Capture(root, "same-root", true); var actualDigest = lazy.Capture(root, "same-root", true);
            Assert.AreEqual(expectedDigest.Sha256, actualDigest.Sha256); Assert.AreEqual(expectedDigest.Nodes, actualDigest.Nodes);
            CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray());
            foreach (var rejected in new object[] { new object[50000], new string('x', 2097152) }) {
                var first = Assert.ThrowsExactly<InvalidOperationException>(() => full.Capture(rejected, "same-limit", true));
                var second = Assert.ThrowsExactly<InvalidOperationException>(() => lazy.Capture(rejected, "same-limit", true));
                Assert.AreEqual(first.Message, second.Message);
            }
            (long Ticks, long Bytes) Measure(LoadingFieldDigest reader, List<(object, string?, int, object?, string)> slots) {
                slots.Clear(); var allocated = GC.GetAllocatedBytesForCurrentThread(); var clock = System.Diagnostics.Stopwatch.StartNew();
                for (var i = 0; i < 30; i++) reader.Capture(root, "equivalent-root", true);
                clock.Stop(); return (clock.ElapsedTicks, GC.GetAllocatedBytesForCurrentThread() - allocated);
            }
            var eager = Measure(full, expected); var filtered = Measure(lazy, actual);
            Console.WriteLine($"Equivalent full-hash 30x1000-owner slot workload: eagerTicks={eager.Ticks}; filteredTicks={filtered.Ticks}; eagerAllocated={eager.Bytes}; filteredAllocated={filtered.Bytes}");
            Assert.IsTrue(filtered.Bytes < eager.Bytes);
        }

        private sealed class PathCell { internal Node? Alias; internal int Number; internal string Text = ""; internal object? Extra; }

        [TestMethod]
        public void ReferenceOnlyReadKeepsEveryObservedSlotReferenceAliasAndOrder()
        {
            var cycle = new Node(); cycle.A = cycle; cycle.B = cycle;
            var listener = new GameplayListener(7);
            var root = new object[] { cycle, cycle, new EventOwner(listener.First),
                new Dictionary<string, object> { ["alias"] = cycle, ["listener"] = listener },
                new Value<float>(BitConverter.Int32BitsToSingle(unchecked((int)0x7fc00123))), new IntPtr(123),
                new byte[] { 0, 9, 10, 99, 100, 255 }, new[] { int.MinValue, 0, int.MaxValue },
                new[] { -0f, float.NaN, float.PositiveInfinity, 3.25f },
                new[] { -0d, double.NaN, double.NegativeInfinity, 7.75d } };
            var fullSlots = new List<(object Owner, string? Field, int Index, object? Value, string Path)>();
            var onlySlots = new List<(object Owner, string? Field, int Index, object? Value, string Path)>();
            var fullRefs = new List<object>(); var onlyRefs = new List<object>();
            LoadingFieldDigest Reader(List<(object, string?, int, object?, string)> slots, List<object> refs)
                => new LoadingFieldDigest(value => { refs.Add(value); return value is GameplayListener ? "actual-listener" : null; }, _ => false,
                    slotObservation: (owner, field, index, value, path) => slots.Add((owner, field?.Name, index, value, path)),
                    sharedFieldReaders: ReferenceEquals(slots, onlySlots));
            using var full = Reader(fullSlots, fullRefs); using var only = Reader(onlySlots, onlyRefs);
            full.BeginSharedGraph(); only.BeginSharedGraph();
            try {
                foreach (var name in new[] { "root", "same-shared-root" }) {
                    var expected = full.Capture(root, name, expandRoot: true);
                    var actual = only.CaptureReferences(root, name, expandRoot: true);
                    Assert.AreEqual(expected.Nodes, actual.Nodes);
                    CollectionAssert.AreEqual(expected.Unsupported.ToArray(), actual.Unsupported.ToArray());
                }
            } finally { full.EndSharedGraph(); only.EndSharedGraph(); }
            Assert.AreEqual(fullSlots.Count, onlySlots.Count);
            for (var i = 0; i < fullSlots.Count; i++) {
                var expected = fullSlots[i]; var actual = onlySlots[i];
                Assert.AreEqual(expected.Field, actual.Field); Assert.AreEqual(expected.Index, actual.Index); Assert.AreEqual(expected.Path, actual.Path);
                if (!expected.Owner.GetType().IsValueType) Assert.AreSame(expected.Owner, actual.Owner);
                else Assert.AreEqual(expected.Owner, actual.Owner);
                if (expected.Value == null || expected.Value.GetType().IsValueType) Assert.AreEqual(expected.Value, actual.Value);
                else Assert.AreSame(expected.Value, actual.Value);
            }
            Assert.AreEqual(fullRefs.Count, onlyRefs.Count);
            for (var i = 0; i < fullRefs.Count; i++) Assert.AreSame(fullRefs[i], onlyRefs[i]);
            Assert.AreEqual(full.Capture(root, "normal").Sha256, only.Capture(root, "normal").Sha256);
        }

        [TestMethod]
        public void ReferenceOnlyReadKeepsExactDepthNodeAndCharacterBudgetRejection()
        {
            var chain = new Node(); var tail = chain;
            for (var i = 0; i < 45; i++) { tail.A = new Node(); tail = tail.A; }
            foreach (var root in new object[] { chain, new object[50000], new object[50001],
                new byte[50000], new int[50000], new float[50000], new double[50000],
                new string('x', 2 * 1024 * 1024) }) {
                using var full = NewDigest(); using var only = NewDigest();
                var expected = Assert.ThrowsExactly<InvalidOperationException>(() => full.Capture(root, "budget", true));
                var actual = Assert.ThrowsExactly<InvalidOperationException>(() => only.CaptureReferences(root, "budget", true));
                Assert.AreEqual(expected.Message, actual.Message);
            }
            using var digest = NewDigest();
            foreach (var length in new[] { 0, 9, 10, 99, 100, 1999900, 2097000 }) {
                var value = new string('x', length);
                Assert.AreEqual(digest.Capture(value, "near-budget").Nodes, digest.CaptureReferences(value, "near-budget").Nodes);
            }
            var low = 2097000; var high = 2 * 1024 * 1024;
            while (high - low > 1) {
                var middle = (low + high) / 2;
                try { digest.Capture(new string('x', middle), "exact-edge"); low = middle; }
                catch (InvalidOperationException) { high = middle; }
            }
            Assert.AreEqual(digest.Capture(new string('x', low), "exact-edge").Nodes,
                digest.CaptureReferences(new string('x', low), "exact-edge").Nodes);
            var fullEdge = Assert.ThrowsExactly<InvalidOperationException>(() => digest.Capture(new string('x', high), "exact-edge"));
            var referenceEdge = Assert.ThrowsExactly<InvalidOperationException>(() => digest.CaptureReferences(new string('x', high), "exact-edge"));
            Assert.AreEqual(fullEdge.Message, referenceEdge.Message);
        }

        [TestMethod]
        public void ReferenceOnlyEquivalentRootWorkloadReportsTimeAndAllocationSavings()
        {
            var shared = new Node(); shared.A = shared; shared.B = shared;
            var root = new object[] { shared, shared, new string('x', 24000), new string('y', 12000), new Value<double>(-0.0),
                new Dictionary<string, object> { ["first"] = shared, ["text"] = new string('z', 18000) } };
            using var full = NewDigest(); using var only = NewDigest();
            full.Capture(root, "warm"); only.CaptureReferences(root, "warm");
            (long Ticks, long Bytes) Measure(bool references) {
                var before = GC.GetAllocatedBytesForCurrentThread(); var timer = System.Diagnostics.Stopwatch.StartNew();
                for (var i = 0; i < 400; i++) {
                    if (references) only.CaptureReferences(root, "equivalent"); else full.Capture(root, "equivalent");
                }
                timer.Stop(); return (timer.ElapsedTicks, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            var hashed = Measure(false); var slots = Measure(true);
            Console.WriteLine($"Equivalent 400-root workload: hashTicks={hashed.Ticks}; referenceTicks={slots.Ticks}; hashAllocated={hashed.Bytes}; referenceAllocated={slots.Bytes}");
            Assert.IsTrue(slots.Bytes < hashed.Bytes / 2, "The reference-only reader still allocated the unused token/hash buffers.");
        }

        [TestMethod]
        public void SharedReferenceReaderKeepsLegacyScalarLengthsAndFreshValues()
        {
            var scalars = new ScalarCell { Single = BitConverter.Int32BitsToSingle(unchecked((int)0xffc12345)),
                Double = BitConverter.Int64BitsToDouble(unchecked((long)0xfff8123456789abc)), Decimal = -123.4500m };
            var aliases = Enumerable.Range(0, 1200).Select(_ => new Node()).ToArray();
            var root = new object[] { scalars, aliases, aliases.Reverse().ToArray(),
                -0f, -0d, decimal.MinValue, decimal.MaxValue, new decimal(0, 0, 0, true, 28), "中文\0\ud800",
                true, false, '\0', '\ud800', '中',
                sbyte.MinValue, sbyte.MaxValue, byte.MinValue, byte.MaxValue,
                short.MinValue, short.MaxValue, ushort.MinValue, ushort.MaxValue,
                int.MinValue, int.MaxValue, uint.MinValue, uint.MaxValue,
                long.MinValue, long.MaxValue, ulong.MinValue, ulong.MaxValue,
                new[] { uint.MinValue, uint.MaxValue }, new sbyte[] { -128, -1, 0, 127 },
                new[] { ArrayIntBits.Negative, ArrayIntBits.Maximum }, new[] { ArrayByteBits.All },
                new[] { -1000, -999, -100, -99, -10, -9, -1, 0, 1, 9, 10, 99, 100, 999, 1000 } };
            var text = typeof(LoadingFieldDigest).GetField("text", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            using var legacy = NewDigest();
            for (var round = 0; round < 3; round++) {
                using var cached = new LoadingFieldDigest(_ => null, _ => false, sharedFieldReaders: true);
                var full = legacy.Capture(root, "scalar-width", true);
                var expectedCharacters = ((System.Text.StringBuilder)text.GetValue(legacy)!).Length;
                var actual = cached.CaptureReferences(root, "scalar-width", true);
                Assert.AreEqual(expectedCharacters, actual.Characters);
                Assert.AreEqual(full.Nodes, actual.Nodes);
                CollectionAssert.AreEqual(full.Unsupported.ToArray(), actual.Unsupported.ToArray());
                Assert.AreEqual(full.Sha256, cached.Capture(root, "scalar-width", true).Sha256);
                scalars.Single = round; scalars.Double = -round; scalars.Decimal += 1m;
                ((int[])root[root.Length - 1])[0] = round;
            }
        }
        private sealed class ScalarCell { internal float Single; internal double Double; internal decimal Decimal; }

        [TestMethod]
        public void FullHashMetadataReuseKeepsCompleteTokenBytesAliasesDiagnosticsAndFailures()
        {
            var cycle = new Node(); cycle.A = cycle; cycle.B = cycle;
            var listener = new GameplayListener(13);
            var root = new object[] { cycle, cycle, new EventOwner(listener.First),
                new ScalarCell { Single = BitConverter.Int32BitsToSingle(unchecked((int)0xffc12345)), Double = -0d, Decimal = -123.4500m },
                new Dictionary<string, object> { ["中文\0"] = cycle, ["listener"] = listener }, new IntPtr(123) };
            var text = typeof(LoadingFieldDigest).GetField("text", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            using var original = new LoadingFieldDigest(_ => null, _ => false, sharedFieldReaders: false);
            using var cached = new LoadingFieldDigest(_ => null, _ => false, sharedFieldReaders: true);
            original.BeginSharedGraph(); cached.BeginSharedGraph();
            try {
                foreach (var path in new[] { "root", "second-shared-root" }) {
                    var expected = original.Capture(root, path, true, includeFieldDiagnostics: true);
                    var expectedBytes = System.Text.Encoding.UTF8.GetBytes(text.GetValue(original)!.ToString()!);
                    var actual = cached.Capture(root, path, true, includeFieldDiagnostics: true);
                    CollectionAssert.AreEqual(expectedBytes, System.Text.Encoding.UTF8.GetBytes(text.GetValue(cached)!.ToString()!));
                    Assert.AreEqual(expected.Sha256, actual.Sha256); Assert.AreEqual(expected.Nodes, actual.Nodes);
                    CollectionAssert.AreEqual(expected.Unsupported.ToArray(), actual.Unsupported.ToArray());
                    CollectionAssert.AreEqual(expected.FieldDigests, actual.FieldDigests);
                }
            } finally { original.EndSharedGraph(); cached.EndSharedGraph(); }
            var chain = new Node(); var tail = chain;
            for (var i = 0; i < 45; i++) { tail.A = new Node(); tail = tail.A; }
            foreach (var rejected in new object[] { chain, new object[50001], new string('x', 2097152) }) {
                var first = Assert.ThrowsExactly<InvalidOperationException>(() => original.Capture(rejected, "same-budget", true));
                var second = Assert.ThrowsExactly<InvalidOperationException>(() => cached.Capture(rejected, "same-budget", true));
                Assert.AreEqual(first.Message, second.Message);
            }
        }

        [TestMethod]
        public void IncomingReaderMetadataReuseReportsEquivalentRepeatedWalkCost()
        {
            var root = Enumerable.Range(0, 500).Select(i => new object[] {
                new ScalarCell { Single = i / 7f, Double = i / 11d, Decimal = i / 13m },
                new Value<int>(i), new Value<long>(-i), new PathCell { Number = i, Text = "entry" + i } }).ToArray();
            (long Ticks, long Bytes, int Characters) Measure(bool shared) {
                var before = GC.GetAllocatedBytesForCurrentThread(); var timer = System.Diagnostics.Stopwatch.StartNew();
                var characters = 0;
                for (var i = 0; i < 40; i++) {
                    using var reader = new LoadingFieldDigest(_ => null, _ => false, sharedFieldReaders: shared);
                    characters = reader.CaptureReferences(root, "complete-incoming", true).Characters;
                }
                timer.Stop(); return (timer.ElapsedTicks, GC.GetAllocatedBytesForCurrentThread() - before, characters);
            }
            Measure(false); Measure(true);
            var original = Measure(false); var cached = Measure(true);
            Assert.AreEqual(original.Characters, cached.Characters);
            Console.WriteLine($"40 fresh-reader complete incoming walks: perReaderTicks={original.Ticks}; sharedMetadataTicks={cached.Ticks}; perReaderBytes={original.Bytes}; sharedMetadataBytes={cached.Bytes}");
        }

        [TestMethod]
        public void DynamicHookMethodsAreReportedAsUnsupportedWithoutExecutingThem()
        {
            var method = new System.Reflection.Emit.DynamicMethod("hook", typeof(void), Type.EmptyTypes);
            var il = method.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(Type.EmptyTypes)!);
            il.Emit(System.Reflection.Emit.OpCodes.Throw);
            using var digest = NewDigest();
            var result = digest.Capture(new object[] { method, method.CreateDelegate(typeof(Action)) }, "scheduler");
            Assert.IsTrue(result.Unsupported.Any(path => path.Contains("dynamic-member", StringComparison.Ordinal)));
        }

        [TestMethod]
        public void DirectFieldReadsMatchReflectionForPrivateBasesStructsAndReferenceGraphs()
        {
            var cycle = new Node(); cycle.A = cycle; cycle.B = cycle;
            var negativeZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
            var nan = BitConverter.Int32BitsToSingle(unchecked((int)0x7fc00123));
            var cases = new object[] { new Derived(negativeZero, 7), cycle, new HostileObject(),
                new Value<float>(nan), new { raw = new[] { negativeZero, nan }, nullable = (int?)7 },
                new Value<double>(BitConverter.Int64BitsToDouble(unchecked((long)0xfff8000000000123))),
                new Value<double>(BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000))),
                new Value<UnsignedBits>(UnsignedBits.All), new Value<SignedBits>(SignedBits.Minimum),
                new Dictionary<string, object> { ["struct"] = new Value<decimal>(1.25m), ["alias"] = cycle },
                new EventOwner(new GameplayListener(7).First) };
            using var direct = new LoadingFieldDigest(value => null, type => false, compiledFieldReaders: true);
            using var reflection = new LoadingFieldDigest(value => null, type => false, compiledFieldReaders: false);
            foreach (var value in cases)
            {
                var expected = reflection.Capture(value, "root", includeFieldDiagnostics: true);
                var actual = direct.Capture(value, "root", includeFieldDiagnostics: true);
                Assert.AreEqual(expected.Sha256, actual.Sha256);
                Assert.AreEqual(expected.Nodes, actual.Nodes);
                CollectionAssert.AreEqual(expected.Unsupported.ToArray(), actual.Unsupported.ToArray());
                CollectionAssert.AreEqual(expected.FieldDigests.Select(FieldDigestText).ToArray(),
                    actual.FieldDigests.Select(FieldDigestText).ToArray());
            }
            string FieldDigestText(object record) => string.Join("|", record.GetType().GetProperties()
                .OrderBy(property => property.Name).Select(property => property.Name + ":" + property.GetValue(record)));
        }

        [TestMethod]
        public void CachedDirectReadersObserveNewOwnersAndChangedValues()
        {
            var field = typeof(GameplayListener).GetField("Value", System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance)!;
            var reader = new LoadingFieldReader(true);
            var owner = new GameplayListener(7);
            Assert.AreEqual(7, reader.Read(field, owner));
            owner.Value = 8;
            Assert.AreEqual(8, reader.Read(field, owner));
            Assert.AreEqual(9, reader.Read(field, new GameplayListener(9)));
        }

        [TestMethod]
        public void DirectFloatingFieldsNeverBypassAnUnregisteredIdentityRule()
        {
            using var digest = new LoadingFieldDigest(_ => null, _ => false,
                fieldIdentity: (_, field, _) => field.FieldType == typeof(float) ? "registered-float-binding" : null);
            var first = digest.Capture(new Value<float>(1), "identity");
            var second = digest.Capture(new Value<float>(2), "identity");
            Assert.AreEqual(first.Sha256, second.Sha256);
            Assert.AreEqual(first.Nodes, second.Nodes);
        }

        [TestMethod]
        public void AnonymousCopySchemaIsIndependentOfCompilerTypeNumberAndFieldDeclarationOrder()
        {
            var first = new { number = 7, value = -0.0f };
            var reordered = new { value = -0.0f, number = 7 };
            Assert.AreNotEqual(first.GetType(), reordered.GetType());
            using var digest = NewDigest();
            Assert.AreEqual(digest.Capture(first, "root").Sha256, digest.Capture(reordered, "root").Sha256);
            Assert.AreEqual(digest.Capture(new[] { first }, "array").Sha256,
                digest.Capture(new[] { reordered }, "array").Sha256);
            Assert.AreEqual(digest.Capture(new List<object> { first }, "list").Sha256,
                digest.Capture(new List<object> { reordered }, "list").Sha256);
            Assert.AreNotEqual(digest.Capture(first, "root").Sha256,
                digest.Capture(new { number = 7L, value = -0.0f }, "root").Sha256);
            Assert.AreNotEqual(digest.Capture(first, "root").Sha256,
                digest.Capture(new { renamed = 7, value = -0.0f }, "root").Sha256);
        }

        [TestMethod]
        public void ForeignAnonymousGameplayTypesRetainTheirActualIdentity()
        {
            var assembly = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
                new System.Reflection.AssemblyName("P0ForeignAnonymousTest"), System.Reflection.Emit.AssemblyBuilderAccess.Run);
            var builder = assembly.DefineDynamicModule("test").DefineType("<>f__AnonymousType999");
            builder.SetCustomAttribute(new System.Reflection.Emit.CustomAttributeBuilder(
                typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute).GetConstructor(Type.EmptyTypes)!, Array.Empty<object>()));
            builder.DefineField("value", typeof(int), System.Reflection.FieldAttributes.Private);
            var type = builder.CreateType()!;
            using var identities = new LoadingObservationTypeIdentity();
            Assert.AreEqual(type.FullName, identities.Bind(type));
        }

        [TestMethod]
        public void InternedFieldSchemaRetainsPrivateDeclaringOwnersAndValueTypes()
        {
            using var digest = NewDigest();
            digest.Capture(new Derived(1.25f, 7), "root");
            var schema = digest.Schema();
            object Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value)!;
            Assert.AreEqual("interned-field-schema-v2", Property(schema, "encoding"));
            var names = (string[])Property(schema, "names");
            var records = ((Array)Property(schema, "types")).Cast<object>().ToArray();
            var derived = records.Single(record => names[(int)Property(record, "type")] == typeof(Derived).FullName);
            var fields = ((Array)Property(derived, "fields")).Cast<object>().ToArray();
            Assert.AreEqual(2, fields.Length);
            var number = fields.Single(field => (string)Property(field, "field") == "number");
            Assert.AreEqual(typeof(Derived).FullName, names[(int)Property(number, "owner")]);
            Assert.AreEqual(typeof(float).FullName, names[(int)Property(number, "type")]);
            var inherited = fields.Single(field => (string)Property(field, "field") == "inherited");
            Assert.AreEqual(typeof(Base).FullName, names[(int)Property(inherited, "owner")]);
            Assert.AreEqual(typeof(int).FullName, names[(int)Property(inherited, "type")]);
        }

        private readonly struct Value<T>
        {
            private readonly T scalar;
            internal Value(T value) { scalar = value; }
        }

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
        public void GlobalReferenceNumberOffsetsChangeOpaqueRootsButNotIndependentFieldValues()
        {
            (string Global, string Local) Capture(bool prefix, bool changedValue)
            {
                using var digest = NewDigest();
                var shared = new Node();
                if (changedValue) shared.A = new Node();
                var owner = new Node { A = shared, B = shared };
                digest.BeginSharedGraph();
                try {
                    if (prefix) digest.Capture(new Node { A = new Node() }, "unrelated-prefix", true);
                    var global = digest.Capture(owner, "fixed-owner", true).Sha256;
                    var local = digest.CaptureFields(owner, "fixed-owner", "A", "B").Sha256;
                    return (global, local);
                }
                finally { digest.EndSharedGraph(); }
            }
            var source = Capture(false, false);
            var target = Capture(true, false);
            Assert.AreNotEqual(source.Global, target.Global);
            Assert.AreEqual(source.Local, target.Local);
            Assert.AreNotEqual(source.Local, Capture(true, true).Local);
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
        private enum UnsignedBits : ulong { All = ulong.MaxValue }
        private enum SignedBits : long { Minimum = long.MinValue }
        private enum ArrayIntBits : int { Negative = -1, Maximum = int.MaxValue }
        private enum ArrayByteBits : byte { All = byte.MaxValue }
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
