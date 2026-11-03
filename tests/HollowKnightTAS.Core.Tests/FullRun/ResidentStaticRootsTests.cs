using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
[DoNotParallelize]
public sealed class ResidentStaticRootsTests
{
    private sealed class State { public object? Link; public int Number; }
    private struct ValueState { public object? Link; public float SignedZero; }
    private static class Fields
    {
        public static State? First, Alias;
        public static int Counter;
        public static ValueState Value;
    }
    private static class InitializerWitness { public static int Count; }
    private static class Dormant
    {
        public static object Value;
        static Dormant() { InitializerWitness.Count++; Value = new object(); throw new InvalidOperationException("Must remain dormant."); }
    }
    private static class Generic<T> { public static object? Value; }
    private static class Other<T> { public static void Witness<TMethod>() { } }
    private sealed class Outer<TOuter>
    {
        internal sealed class Nested<TInner>
        {
            public static object? Value;
            public static void Method<TMethod>() { }
        }
    }
    private static class ThreadLocal { [ThreadStatic] public static object? Value; }
    private static class ReadonlyFields
    {
        public static readonly int Counter = 73;
        public static readonly object Identity = new object();
        public static readonly ValueState Value = new ValueState { Link = Identity, SignedZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)) };
        public static readonly double NaN = BitConverter.Int64BitsToDouble(unchecked((long)0x7ff8000000000042));
    }
    private sealed class Reader : ResidentStaticRoots.IInitializationReader
    {
        internal ResidentStaticRoots.Observation[] Values;
        internal Reader(params ResidentStaticRoots.Observation[] values) { Values = values; }
        public string AbiIdentity => "offline-static-reader-v1";
        public ResidentStaticRoots.Observation[] Read(Assembly[] gameplayModules) => Values;
    }
    private sealed class ChangingReader : ResidentStaticRoots.IInitializationReader
    {
        private int reads;
        public string AbiIdentity => "offline-changing-reader";
        public ResidentStaticRoots.Observation[] Read(Assembly[] gameplayModules)
            => new[] { new ResidentStaticRoots.Observation(typeof(Fields), ++reads == 1
                ? ResidentStaticRoots.Initialization.Dormant : ResidentStaticRoots.Initialization.Initialized) };
    }
    private sealed class MetadataShapesReader : ResidentStaticRoots.IInitializationReader, ResidentStaticRoots.IMetadataShapeReader
    {
        internal ResidentStaticRoots.Observation[] Values = Array.Empty<ResidentStaticRoots.Observation>();
        internal Type[] Shapes = Array.Empty<Type>();
        internal int ShapeReads;
        public string AbiIdentity => "offline-storage-and-metadata";
        public ResidentStaticRoots.Observation[] Read(Assembly[] gameplayModules) => Values;
        public Type[] ReadPreparationShapes(Assembly[] gameplayModules)
        {
            // A cache hint may hide/reveal a type with existing storage. It
            // must never be the source of initialized-state ownership.
            return ++ShapeReads % 2 == 0 ? Shapes : Array.Empty<Type>();
        }
    }
    private static ResidentStaticRoots.Observation Initialized(Type type) => new(type, ResidentStaticRoots.Initialization.Initialized);
    private static ResidentStaticRoots.ModuleOwnership Ownership(Assembly assembly) => assembly == typeof(ResidentStaticRootsTests).Assembly
        ? ResidentStaticRoots.ModuleOwnership.Gameplay : ResidentStaticRoots.ModuleOwnership.Runtime;
    private static ResidentStaticRoots.CaptureResult Capture(Reader reader) => ResidentStaticRoots.Capture(Ownership, reader);
    private static Dictionary<string, object> WithOwner(ResidentStaticRoots.CaptureResult roots, State owner)
    {
        var result = roots.ManagedRoots.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        result.Add("component-owner", owner); return result;
    }

    [TestMethod]
    public void Static_slots_restore_null_values_struct_bits_and_aliases_with_component_graph()
    {
        var originalFirst = Fields.First; var originalAlias = Fields.Alias; var originalCounter = Fields.Counter; var originalValue = Fields.Value;
        try {
            var reader = new Reader(Initialized(typeof(Fields)));
            var sourceOwner = new State { Number = 73 }; sourceOwner.Link = sourceOwner;
            Fields.First = sourceOwner; Fields.Alias = sourceOwner; Fields.Counter = 93;
            Fields.Value = new ValueState { Link = sourceOwner, SignedZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)) };
            var source = Capture(reader); var sourceRoots = WithOwner(source, sourceOwner);
            var policy = new ResidentGraphPolicy(_ => null);
            var catalog = ResidentManagedTypeCatalog.Capture(sourceRoots, policy, _ => null);
            var codec = new CanonicalCarryGraphCodec(catalog.Types, catalog.SchemaIdentity, residentPolicy: policy);
            var graph = codec.Capture(sourceRoots, _ => null);
            source.RequireUnchanged(codec);
            var targetOwner = new State { Number = -1 }; Fields.First = null; Fields.Alias = targetOwner; Fields.Counter = -1; Fields.Value = default;
            var target = Capture(reader);
            var manifest = ResidentStaticRoots.DecodeManifest(ResidentStaticRoots.ManifestBytes(source.Snapshot));
            var plan = ResidentStaticRoots.Prepare(manifest, target, () => { }); plan.RequireComplete();
            var graphPlan = codec.PrepareOwnedGraph(graph, WithOwner(target, targetOwner), key => throw new InvalidOperationException(key));
            graphPlan.Apply(); plan.Apply(); plan.Verify(codec); graphPlan.VerifyReadback();
            Assert.AreSame(targetOwner, Fields.First); Assert.AreSame(targetOwner, Fields.Alias); Assert.AreSame(targetOwner, Fields.Value.Link);
            Assert.AreSame(targetOwner, targetOwner.Link); Assert.AreEqual(73, targetOwner.Number); Assert.AreEqual(93, Fields.Counter);
            Assert.AreEqual(unchecked((int)0x80000000), BitConverter.SingleToInt32Bits(Fields.Value.SignedZero));
            Fields.First = new State(); Assert.ThrowsExactly<InvalidDataException>(() => plan.Verify(codec));
        } finally { Fields.First = originalFirst; Fields.Alias = originalAlias; Fields.Counter = originalCounter; Fields.Value = originalValue; }
    }

    [TestMethod]
    public void Dormant_type_inventory_never_reads_static_values_or_runs_initializer()
    {
        var before = InitializerWitness.Count;
        var captured = Capture(new Reader(new ResidentStaticRoots.Observation(typeof(Dormant), ResidentStaticRoots.Initialization.Dormant)));
        Assert.AreEqual(before, InitializerWitness.Count);
        Assert.AreEqual(0, captured.ManagedRoots.Count);
        Assert.AreEqual("Dormant", captured.Snapshot.Types.Single().Initialization);
        Assert.AreEqual(nameof(Dormant.Value), captured.Snapshot.Types.Single().Fields.Single().Name);
    }

    [TestMethod]
    public void Initializer_state_mismatch_is_explicit_gap_before_writes()
    {
        var source = Capture(new Reader(new ResidentStaticRoots.Observation(typeof(Fields), ResidentStaticRoots.Initialization.Dormant)));
        var target = Capture(new Reader(Initialized(typeof(Fields))));
        var plan = ResidentStaticRoots.Prepare(source.Snapshot, target, () => Assert.Fail("No writes are admitted."));
        Assert.IsTrue(plan.Unsupported.Any(value => value.StartsWith("static-initialization-differs:", StringComparison.Ordinal)));
        Assert.ThrowsExactly<InvalidDataException>(plan.Apply);
    }

    [TestMethod]
    public void PartiallyConstructedClassesKeepGenericParameterOwnersAndHaveNoValueSlots()
    {
        var first = typeof(Generic<>).GetGenericArguments()[0];
        var second = typeof(Other<>).GetGenericArguments()[0];
        var method = typeof(Other<>).GetMethod("Witness")!.GetGenericArguments()[0];
        Type Open(Type arg) => typeof(Generic<>).MakeGenericType(typeof(Dictionary<,>).MakeGenericType(arg.MakeArrayType(), method));
        var types = new[] { Open(first), Open(second) };
        var inventory = Capture(new Reader(types.Select(type => new ResidentStaticRoots.Observation(type, ResidentStaticRoots.Initialization.Dormant)).ToArray()));
        var decoded = ResidentStaticRoots.DecodeManifest(ResidentStaticRoots.ManifestBytes(inventory.Snapshot));
        CollectionAssert.AreEquivalent(types, decoded.Types.Select(row => ResidentManagedTypeCatalog.ResolveType(row.Type)).ToArray());
        Assert.AreEqual(2, decoded.Types.Select(row => row.Key).Distinct().Count()); Assert.AreEqual(0, inventory.ManagedRoots.Count);
        inventory.RequireInitializationUnchanged();
        var malformed = ResidentManagedTypeCatalog.DescribeType(method); malformed.Position++;
        Assert.ThrowsExactly<InvalidDataException>(() => ResidentManagedTypeCatalog.ResolveType(malformed));
    }

    [TestMethod]
    public void Closed_generic_static_slots_remain_distinct_and_thread_storage_is_rejected()
    {
        var oldInt = Generic<int>.Value; var oldString = Generic<string>.Value;
        try {
            Generic<int>.Value = new State { Number = 1 }; Generic<string>.Value = new State { Number = 2 };
            var captured = Capture(new Reader(Initialized(typeof(Generic<int>)), Initialized(typeof(Generic<string>)), Initialized(typeof(ThreadLocal))));
            Assert.AreEqual(2, captured.ManagedRoots.Count);
            Assert.AreEqual(3, captured.Snapshot.Types.Length);
            Assert.IsTrue(captured.Unsupported.Single().StartsWith("thread-static:", StringComparison.Ordinal));
            var plan = ResidentStaticRoots.Prepare(captured.Snapshot, captured, () => { });
            Assert.ThrowsExactly<InvalidDataException>(plan.RequireComplete);
        } finally { Generic<int>.Value = oldInt; Generic<string>.Value = oldString; }
    }

    [TestMethod]
    public void Nested_open_generic_parameters_resolve_real_type_and_method_metadata_owners()
    {
        var definition = typeof(Outer<>.Nested<>);
        var arguments = definition.GetGenericArguments();
        var methodParameter = definition.GetMethod("Method")!.GetGenericArguments()[0];
        foreach (var parameter in arguments.Append(methodParameter)) {
            var descriptor = ResidentManagedTypeCatalog.DescribeType(parameter);
            Assert.AreEqual(parameter, ResidentManagedTypeCatalog.ResolveType(descriptor));
            Assert.AreEqual(parameter.GenericParameterPosition, descriptor.Position);
            Assert.AreEqual(parameter.Module.ModuleVersionId.ToString("D"), descriptor.Module);
        }
        var open = definition.MakeGenericType(arguments[0], methodParameter.MakeArrayType());
        var capture = Capture(new Reader(new ResidentStaticRoots.Observation(open, ResidentStaticRoots.Initialization.Dormant)));
        Assert.AreEqual(0, capture.ManagedRoots.Count);
        Assert.AreEqual(open, ResidentManagedTypeCatalog.ResolveType(capture.Snapshot.Types.Single().Type));
        var first = ResidentManagedTypeCatalog.DescribeType(methodParameter);
        first.Token = 0; first.Position = 999;
        var second = ResidentManagedTypeCatalog.DescribeType(methodParameter);
        Assert.AreEqual(methodParameter, ResidentManagedTypeCatalog.ResolveType(second));
    }

    [TestMethod]
    public void Raw_inventory_guard_is_order_independent_and_detects_closed_generic_replacement()
    {
        var first = new ResidentStaticRoots.Observation(typeof(Generic<int>), ResidentStaticRoots.Initialization.Dormant);
        var second = new ResidentStaticRoots.Observation(typeof(Generic<string>), ResidentStaticRoots.Initialization.Dormant);
        var reader = new Reader(first, second); var capture = Capture(reader);
        reader.Values = new[] { second, first }; capture.RequireInitializationUnchanged();
        reader.Values = new[] { first, new ResidentStaticRoots.Observation(typeof(Generic<long>), ResidentStaticRoots.Initialization.Dormant) };
        Assert.ThrowsExactly<InvalidDataException>(capture.RequireInitializationUnchanged);
    }

    [TestMethod]
    public void Dormant_inventory_guard_detects_new_vtable_before_initializer_runs()
    {
        var reader = new Reader(new ResidentStaticRoots.Observation(typeof(Generic<>), ResidentStaticRoots.Initialization.Dormant, nativeVtableAbsent: true));
        var capture = Capture(reader);
        reader.Values = new[] { new ResidentStaticRoots.Observation(typeof(Generic<>), ResidentStaticRoots.Initialization.Dormant) };
        Assert.ThrowsExactly<InvalidDataException>(capture.RequireInitializationUnchanged);
    }

    [TestMethod]
    public void Field_signatures_describe_pointer_and_byref_layout_without_allocating_value_slots()
    {
        var scalar = ResidentStaticRoots.FieldSignature(typeof(int));
        Assert.AreEqual("pointer(" + scalar + ")", ResidentStaticRoots.FieldSignature(typeof(int).MakePointerType()));
        Assert.AreEqual("byref(" + scalar + ")", ResidentStaticRoots.FieldSignature(typeof(int).MakeByRefType()));
        var parameter = typeof(Generic<>).GetGenericArguments()[0];
        var open = ResidentStaticRoots.FieldSignature(parameter.MakeArrayType().MakePointerType());
        StringAssert.StartsWith(open, "pointer(");
        StringAssert.Contains(open, "\"Kind\":\"var\"");
    }

    [TestMethod]
    public void Cached_static_schema_does_not_cache_values_or_expose_mutable_manifest_dtos()
    {
        var original = Fields.Counter;
        try {
            var reader = new Reader(Initialized(typeof(Fields)));
            Fields.Counter = 17; var first = Capture(reader);
            first.Snapshot.Types[0].Type.Token = 0;
            first.Snapshot.Types[0].Fields[0].Signature = "tampered";
            Fields.Counter = 19; var second = Capture(reader);
            Assert.AreEqual(typeof(Fields), ResidentManagedTypeCatalog.ResolveType(second.Snapshot.Types[0].Type));
            Assert.AreNotEqual("tampered", second.Snapshot.Types[0].Fields[0].Signature);
            var counter = second.Bindings.Single(binding => binding.Field.Name == nameof(Fields.Counter));
            Assert.AreEqual(19, counter.Value.GetValue(counter.Slot));
        } finally { Fields.Counter = original; }
    }

    [TestMethod]
    public void Metadata_preparation_does_not_read_dormant_static_values_and_rejects_initializer_changes()
    {
        var before = InitializerWitness.Count;
        ResidentStaticRoots.PrepareMetadata(Ownership, new Reader(new ResidentStaticRoots.Observation(typeof(Dormant), ResidentStaticRoots.Initialization.Dormant)));
        Assert.AreEqual(before, InitializerWitness.Count);
        Assert.ThrowsExactly<InvalidDataException>(() => ResidentStaticRoots.PrepareMetadata(Ownership, new ChangingReader()));
    }

    [TestMethod]
    public void Module_difference_diagnostic_identifies_exact_gained_and_lost_mvids()
    {
        Assert.AreEqual("gained=[B/new];lost=[B/old]", ResidentStaticRoots.ModuleDifference(new[] { "A/same", "B/old" }, new[] { "B/new", "A/same" }));
    }

    [TestMethod]
    public void Initialized_generic_storage_is_independent_of_metadata_cache_visibility()
    {
        var original = Generic<int>.Value;
        try {
            var sentinel = new State { Number = 37 }; Generic<int>.Value = sentinel;
            var reader = new MetadataShapesReader { Values = new[] { Initialized(typeof(Generic<int>)) }, Shapes = new[] { typeof(Generic<int>), typeof(Generic<string>) } };
            ResidentStaticRoots.PrepareMetadata(Ownership, reader);
            ResidentStaticRoots.PrepareMetadata(Ownership, reader);
            var capture = ResidentStaticRoots.Capture(Ownership, reader);
            Assert.AreEqual(1, capture.ManagedRoots.Count);
            Assert.AreEqual(typeof(Generic<int>), ResidentManagedTypeCatalog.ResolveType(capture.Snapshot.Types.Single().Type));
            var binding = capture.Bindings.Single(); Assert.AreSame(sentinel, binding.Value.GetValue(binding.Slot));
            reader.Shapes = Array.Empty<Type>(); capture.RequireInitializationUnchanged();
            reader.Values = Array.Empty<ResidentStaticRoots.Observation>();
            Assert.ThrowsExactly<InvalidDataException>(capture.RequireInitializationUnchanged);
            Assert.ThrowsExactly<InvalidDataException>(() => ResidentStaticRoots.RequireMetadataStateUnchanged(new[] { Initialized(typeof(Generic<int>)) }, reader.Values));
        } finally { Generic<int>.Value = original; }
    }

    [TestMethod]
    public void Authoritative_storage_rejects_true_initialization_gain_and_vtable_identity_replacement()
    {
        var before = Initialized(typeof(Generic<int>)); var old = new[] { before };
        Assert.ThrowsExactly<InvalidDataException>(() => ResidentStaticRoots.RequireMetadataStateUnchanged(old, new[] { before, Initialized(typeof(Generic<string>)) }));
        var type = typeof(Generic<int>); var nativeClass = IntPtr.Add(type.TypeHandle.Value, -0xb8);
        var allocated = new ResidentStaticRoots.Observation(type, ResidentStaticRoots.Initialization.Initialized, false, nativeClass, default, new IntPtr(10));
        var replaced = new ResidentStaticRoots.Observation(type, ResidentStaticRoots.Initialization.Initialized, false, nativeClass, default, new IntPtr(11));
        Assert.ThrowsExactly<InvalidDataException>(() => ResidentStaticRoots.RequireMetadataStateUnchanged(new[] { allocated }, new[] { replaced }));
        var reader = new Reader(allocated); var capture = Capture(reader); reader.Values = new[] { replaced };
        Assert.ThrowsExactly<InvalidDataException>(capture.RequireInitializationUnchanged);
    }

    [TestMethod]
    public void Domain_lifetime_directory_rejects_wrong_domain_class_slot_and_flag_corruption()
    {
        var domain = new IntPtr(10); var table = new IntPtr(20); var klass = new IntPtr(30);
        Assert.IsTrue(ResidentStaticRoots.DomainVtableBackreferencesMatch(domain, table, klass, domain, 1, table));
        Assert.IsTrue(ResidentStaticRoots.DomainVtableBackreferencesMatch(domain, table, klass, domain, 0, table));
        Assert.IsFalse(ResidentStaticRoots.DomainVtableBackreferencesMatch(domain, table, klass, new IntPtr(11), 1, table));
        Assert.IsFalse(ResidentStaticRoots.DomainVtableBackreferencesMatch(domain, table, klass, domain, 1, new IntPtr(21)));
        Assert.IsFalse(ResidentStaticRoots.DomainVtableBackreferencesMatch(domain, table, IntPtr.Zero, domain, 1, table));
        Assert.IsFalse(ResidentStaticRoots.DomainVtableBackreferencesMatch(domain, table, klass, domain, 2, table));
    }

    [TestMethod]
    public void Typed_static_accessors_handle_readonly_inline_values_reference_aliases_and_nan_bits()
    {
        var capture = Capture(new Reader(Initialized(typeof(ReadonlyFields))));
        foreach (var binding in capture.Bindings) Assert.IsTrue(binding.Access.Same(binding.Slot), binding.Key);
        var counter = capture.Bindings.Single(binding => binding.Field.Name == nameof(ReadonlyFields.Counter));
        try {
            counter.Value.SetValue(counter.Slot, 99);
            Assert.IsFalse(counter.Access.Same(counter.Slot));
            counter.Access.Write(counter.Slot);
            Assert.IsTrue(counter.Access.Same(counter.Slot));
            Assert.AreEqual(99, counter.Field.GetValue(null));
        } finally {
            counter.Value.SetValue(counter.Slot, 73); counter.Access.Write(counter.Slot);
        }
        var inline = capture.Bindings.Single(binding => binding.Field.Name == nameof(ReadonlyFields.Value));
        var value = (ValueState)inline.Value.GetValue(inline.Slot)!;
        Assert.AreSame(ReadonlyFields.Identity, value.Link);
        Assert.AreEqual(unchecked((int)0x80000000), BitConverter.SingleToInt32Bits(value.SignedZero));
        var nan = capture.Bindings.Single(binding => binding.Field.Name == nameof(ReadonlyFields.NaN));
        Assert.AreEqual(unchecked((long)0x7ff8000000000042), BitConverter.DoubleToInt64Bits((double)nan.Value.GetValue(nan.Slot)!));
    }

    [TestMethod]
    public void Initialization_diff_separates_dormant_boxing_vtable_materialization_from_cctor_changes()
    {
        var type = typeof(ValueState); var klass = IntPtr.Add(type.TypeHandle.Value, -0xb8);
        var absent = new ResidentStaticRoots.Observation(type, ResidentStaticRoots.Initialization.Dormant, true, klass);
        var materialized = new ResidentStaticRoots.Observation(type, ResidentStaticRoots.Initialization.Dormant, false, klass, default, new IntPtr(1234));
        var diff = ResidentStaticRoots.InitializationDifference(new[] { absent }, new[] { materialized });
        StringAssert.Contains(diff, "changed(1)"); StringAssert.Contains(diff, "state=Dormant"); StringAssert.Contains(diff, "valueType=True");
        StringAssert.Contains(diff, "vtable=4d2"); StringAssert.Contains(diff, type.Module.ModuleVersionId.ToString("D"));
    }

    [TestMethod]
    public void Initialization_inventory_change_after_capture_is_detected()
    {
        var reader = new Reader(new ResidentStaticRoots.Observation(typeof(Fields), ResidentStaticRoots.Initialization.Dormant));
        var captured = Capture(reader); reader.Values = new[] { Initialized(typeof(Fields)) };
        Assert.ThrowsExactly<InvalidDataException>(captured.RequireInitializationUnchanged);
    }

    [TestMethod]
    public void Static_manifest_rejects_duplicate_types_and_noncanonical_payload()
    {
        var captured = Capture(new Reader(new ResidentStaticRoots.Observation(typeof(Fields), ResidentStaticRoots.Initialization.Dormant)));
        var bytes = ResidentStaticRoots.ManifestBytes(captured.Snapshot);
        Assert.ThrowsExactly<InvalidDataException>(() => ResidentStaticRoots.DecodeManifest(bytes.Concat(new byte[] { 32 }).ToArray()));
        Assert.ThrowsExactly<InvalidDataException>(() => ResidentStaticRoots.DecodeManifest(bytes.Take(bytes.Length - 1).ToArray()));
        var forward = (byte[])bytes.Clone(); Array.Copy(BitConverter.GetBytes(12345), 0, forward, 8, 4);
        Assert.ThrowsExactly<InvalidDataException>(() => ResidentStaticRoots.DecodeManifest(forward));
        var initialField = captured.Snapshot.Types[0].Fields[0]; var oldKey = initialField.Key;
        initialField.Key = "wrong owner"; Assert.ThrowsExactly<InvalidDataException>(() => ResidentStaticRoots.ManifestBytes(captured.Snapshot));
        initialField.Key = oldKey;
        captured.Snapshot.Types = new[] { captured.Snapshot.Types[0], captured.Snapshot.Types[0] };
        Assert.ThrowsExactly<InvalidDataException>(() => ResidentStaticRoots.ManifestBytes(captured.Snapshot));
    }
}
