using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class ResidentManagedGraphTests
{
    // Uninitialized test-only wrappers use the real managed Unity base layout.
    // No Unity constructor, native getter, lifecycle or game process is used.
    private sealed class DeadOne : UnityEngine.Object { public object? Link; public int Number; }
    private sealed class DeadTwo : UnityEngine.Object { public object? Link; public string? Text; }
    private sealed class NativeOwner : UnityEngine.Object { public object? Link; public int Number; }
    private sealed class State
    {
        public object? First, Alias, Second;
        public Delegate? Callback, CallbackAlias;
        public object? Metadata;
    }
    private sealed class Closure
    {
        public Func<int, int>? Callback;
        public State? Owner;
        public int Number;
        public int Add(int value) => Number + value;
    }
    private sealed class OtherPointer { public IntPtr Value; }
    private class MetadataBase { public int Number = 0; }
    private sealed class MetadataChild : MetadataBase { }
    private struct PackedValue { public byte Flag; public float Scalar; public int Number; }
    private struct PointerValue { public IntPtr Pointer; }
    private struct ReferenceValue { public object? Owner; public int Number; }
    private sealed class PackedOwner
    {
        public readonly int Immutable;
        public int Mutable;
        public bool Flag;
        public Guid Token;
        public object? Link;
        public PackedOwner(int immutable) { Immutable = immutable; }
    }
    private static class GenericMethods<T>
    {
        public static U Echo<U>(U value) => value;
        public static string Prefix(string? prefix, string value) => (prefix ?? "null:") + value;
    }
    private static readonly FieldInfo Pointer = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static T Wrapper<T>(long pointer = 0) where T : UnityEngine.Object
    {
        var value = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        Pointer.SetValue(value, new IntPtr(pointer)); return value;
    }
    private static Dictionary<string, object> Roots(params (string key, object owner)[] values)
        => values.ToDictionary(value => value.key, value => value.owner, StringComparer.Ordinal);
    private static string? NoExternal(object _) => null;
    private static object NoBinding(string key) => throw new InvalidDataException("Unexpected binding: " + key);
    private static CanonicalCarryGraphCodec Codec(ResidentManagedTypeCatalog catalog, ResidentGraphPolicy policy)
        => new(catalog.Types, catalog.SchemaIdentity, residentPolicy: policy);
    private static (CanonicalCarryGraphData graph, ResidentManagedTypeCatalog catalog) Capture(Dictionary<string, object> roots, ResidentGraphPolicy policy)
    {
        var catalog = ResidentManagedTypeCatalog.Capture(roots, policy, NoExternal);
        var codec = Codec(catalog, policy);
        var graph = codec.Capture(roots, NoExternal);
        Assert.AreEqual(0, graph.Unsupported.Length);
        Assert.AreEqual(2, graph.Version);
        return (CanonicalCarryGraphWire.Decode(CanonicalCarryGraphWire.Encode(graph)), catalog);
    }

    [TestMethod]
    public void PackedClassFieldsRetainPreflightStaleGuardsAndReplay()
    {
        var source = new PackedOwner(17) { Mutable = -29, Flag = true, Token = new Guid("20ad81af-f8ee-402f-8a13-948c551ea3a7") };
        source.Link = source;
        var policy = new ResidentGraphPolicy(_ => null);
        var captured = Capture(Roots(("owner", source)), policy);
        var node = captured.graph.Nodes.Single();
        Assert.IsNotNull(node.Packed);
        Assert.AreEqual(1, node.Fields!.Length);
        Assert.IsTrue(node.Fields[0].Name.EndsWith("/Link", StringComparison.Ordinal));
        var target = new PackedOwner(3);
        var codec = Codec(captured.catalog, policy);
        var stale = codec.PrepareOwnedGraph(captured.graph, Roots(("owner", target)), NoBinding);
        target.Mutable = 1;
        Assert.ThrowsExactly<InvalidOperationException>(() => stale.Apply());
        Assert.AreEqual(3, target.Immutable);
        var plan = codec.PrepareOwnedGraph(captured.graph, Roots(("owner", target)), NoBinding,
            view => Assert.AreEqual(-29, view.Field(view.Root("owner"), typeof(PackedOwner), "Mutable")));
        plan.Apply();
        Assert.AreEqual(17, target.Immutable); Assert.IsTrue(target.Flag); Assert.AreEqual(source.Token, target.Token);
        Assert.AreSame(target, target.Link);
        plan.ReapplyOriginalGraph(() => { }, () => target.Mutable = 123);
        Assert.AreEqual(-29, target.Mutable); plan.VerifyReadback();
        node.Packed = node.Packed![..^1];
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareOwnedGraph(captured.graph, Roots(("owner", new PackedOwner(0))), NoBinding));
    }

    [TestMethod]
    public void PackedValueArraysKeepPaddingFloatBitsAndAliasWhileSemanticArraysStayExpanded()
    {
        var values = new[] { new PackedValue { Flag = 3, Scalar = -0f, Number = 17 } };
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(values, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { System.Runtime.InteropServices.Marshal.WriteByte(pin.AddrOfPinnedObject(), 1, 0xa7); }
        finally { pin.Free(); }
        var policy = new ResidentGraphPolicy(_ => null);
        var captured = Capture(Roots(("state", new State { First = values, Alias = values })), policy);
        var node = captured.graph.Nodes.Single(value => value.Kind == "value-vector");
        Assert.AreEqual(0xa7, node.Packed![1]);
        var target = new State(); var codec = Codec(captured.catalog, policy);
        var plan = codec.PrepareOwnedGraph(captured.graph, Roots(("state", target)), NoBinding);
        plan.Apply(); Assert.AreSame(target.First, target.Alias);
        var restored = (PackedValue[])target.First!;
        Assert.AreEqual(17, restored[0].Number);
        Assert.AreEqual(unchecked((int)0x80000000), BitConverter.SingleToInt32Bits(restored[0].Scalar));
        plan.VerifyReadback();
        restored[0].Number++;
        Assert.ThrowsExactly<InvalidOperationException>(plan.VerifyReadback);
        Assert.AreEqual(CanonicalCarryGraphCodec.MaximumPackedValues / 3, CanonicalCarryGraphCodec.ResidentArrayLimit(typeof(UnityEngine.RaycastHit2D[])));
        Assert.AreEqual(CanonicalCarryGraphCodec.MaximumPackedValues / 3, CanonicalCarryGraphCodec.ResidentArrayLimit(typeof(PointerValue[])));
        var pointers = new PointerValue[1]; pointers[0].Pointer = IntPtr.Zero;
        Assert.ThrowsExactly<InvalidDataException>(() => Capture(Roots(("state", new State { First = pointers })), policy));
        var root = new State();
        root.First = new[] { new ReferenceValue { Owner = root, Number = 9 } };
        var reference = Capture(Roots(("state", root)), policy);
        Assert.IsFalse(reference.graph.Nodes.Any(value => value.Kind == "value-vector"));
    }

    [TestMethod]
    public void InheritedMetadataPreservesReflectedTypeAndRepeatedAlias()
    {
        var declared = typeof(MetadataBase).GetField("Number")!;
        var reflected = typeof(MetadataChild).GetField("Number")!;
        var policy = new ResidentGraphPolicy(_ => null);
        var captured = Capture(Roots(("state", new State { First = declared, Alias = declared, Second = reflected })), policy);
        Assert.AreEqual(2, captured.graph.Externals.Length);
        var target = new State();
        var plan = Codec(captured.catalog, policy).PrepareOwnedGraph(captured.graph, Roots(("state", target)), NoBinding);
        plan.Apply(); Assert.AreSame(target.First, target.Alias);
        Assert.AreEqual(typeof(MetadataBase), ((FieldInfo)target.First!).ReflectedType);
        Assert.AreEqual(typeof(MetadataChild), ((FieldInfo)target.Second!).ReflectedType);
        Assert.AreNotSame(target.First, target.Second);
        plan.VerifyReadback();
    }

    [TestMethod]
    public void SourceReadbackDetectsAliasScalarAndPacketMutations()
    {
        var shared = new State(); var source = new State { First = shared, Alias = shared, Second = new[] { -0f } };
        var policy = new ResidentGraphPolicy(_ => null);
        var roots = Roots(("state", source));
        var catalog = ResidentManagedTypeCatalog.Capture(roots, policy, NoExternal);
        Action verify = null!;
        var codec = Codec(catalog, policy);
        var graph = codec.Capture(roots, NoExternal, capturedReadback: action => verify = action);
        verify();
        source.Alias = new State();
        Assert.ThrowsExactly<InvalidOperationException>(verify);
        source.Alias = shared;
        ((float[])source.Second!)[0] = 0f;
        Assert.ThrowsExactly<InvalidOperationException>(verify);
        ((float[])source.Second!)[0] = -0f;
        verify();
        roots["state"] = new State();
        Assert.ThrowsExactly<InvalidOperationException>(verify);
        roots["state"] = source;
        verify();
        graph.Schema += "-changed";
        Assert.ThrowsExactly<InvalidOperationException>(verify);
    }

    [TestMethod]
    public void ClosureExtensionDiscoversNewOwnersWithoutDroppingEarlierTypes()
    {
        var policy = new ResidentGraphPolicy(_ => null);
        var original = new State { First = new List<int> { 1, 2 } };
        var roots = Roots(("old", original));
        var cache = new ResidentManagedTypeCatalog.DiscoveryCache();
        var first = ResidentManagedTypeCatalog.Capture(roots, policy, NoExternal, cache: cache);
        roots.Add("new", new Closure { Owner = original, Number = 19 });
        var extended = ResidentManagedTypeCatalog.Capture(roots, policy, NoExternal, cache: cache);
        Assert.IsTrue(first.Types.All(type => extended.Types.Contains(type)));
        CollectionAssert.Contains(extended.Types, typeof(Closure));
        var graph = Codec(extended, policy).Capture(roots, NoExternal);
        Assert.AreEqual(0, graph.Unsupported.Length);
        var target = new State(); var closure = new Closure();
        var plan = Codec(extended, policy).PrepareOwnedGraph(graph, Roots(("old", target), ("new", closure)), NoBinding);
        plan.Apply(); Assert.AreSame(target, closure.Owner); Assert.AreEqual(19, closure.Number);
        plan.VerifyReadback();
    }

    [TestMethod]
    public void DiscoveryCountsExpandedOwnersAndKeepsTheDepthGuard()
    {
        var repeated = new State();
        var slots = Enumerable.Range(0, 12).Select(_ => new object[90000]).ToArray();
        foreach (var row in slots) row[0] = repeated;
        var root = new State { First = slots };
        var policy = new ResidentGraphPolicy(_ => null);
        var catalog = ResidentManagedTypeCatalog.Capture(Roots(("state", root)), policy, NoExternal);
        CollectionAssert.Contains(catalog.Types, typeof(State));
        var tail = root;
        for (var i = 0; i < 130; i++) { var next = new State(); tail.Second = next; tail = next; }
        Assert.ThrowsExactly<InvalidDataException>(() => ResidentManagedTypeCatalog.Capture(Roots(("state", root)), policy, NoExternal));
    }

    [TestMethod]
    public void EmptyReferenceListKeepsIdentityWhileNonemptyBackingArrayIsTraversed()
    {
        var empty = new List<State>();
        var root = new State();
        var nested = new State { First = empty, Alias = root };
        root.First = new List<State> { nested };
        root.Second = new List<State> { nested };
        root.Alias = empty;
        var policy = new ResidentGraphPolicy(_ => null);
        var captured = Capture(Roots(("state", root)), policy);
        var target = new State();
        var plan = Codec(captured.catalog, policy).PrepareOwnedGraph(captured.graph, Roots(("state", target)), NoBinding);
        plan.Apply();
        var first = (List<State>)target.First!;
        var second = (List<State>)target.Second!;
        Assert.AreNotSame(first, second);
        Assert.AreSame(first.Single(), second.Single());
        Assert.AreSame(target, first.Single().Alias);
        Assert.AreSame(target.Alias, first.Single().First);
        Assert.AreEqual(0, ((List<State>)target.Alias!).Count);
        plan.VerifyReadback();
    }

    [TestMethod]
    public void ScalarListsKeepTheirBackingStorageAndSynchronizationAliases()
    {
        var numbers = new List<int>(7) { 12 };
        var words = new List<string>(5) { "state" };
        var sync = ((System.Collections.ICollection)numbers).SyncRoot;
        var policy = new ResidentGraphPolicy(_ => null);
        var source = new State { First = numbers, Second = words, Alias = sync };
        var captured = Capture(Roots(("state", source)), policy);
        var target = new State();
        var plan = Codec(captured.catalog, policy).PrepareOwnedGraph(captured.graph, Roots(("state", target)), NoBinding);
        plan.Apply();
        var restored = (List<int>)target.First!;
        Assert.AreSame(((System.Collections.ICollection)restored).SyncRoot, target.Alias);
        Assert.AreEqual(7, restored.Capacity);
        Assert.AreEqual(12, restored.Single());
        Assert.AreEqual(5, ((List<string>)target.Second!).Capacity);
        Assert.AreEqual("state", ((List<string>)target.Second!).Single());
        plan.VerifyReadback();
    }

    [TestMethod]
    public void ResidentGraphRetainsDistinctNodesAcrossLegacyCountBoundary()
    {
        var first = Enumerable.Range(0, 50000).Select(_ => new object()).ToArray();
        var second = Enumerable.Range(0, 50000).Select(_ => new object()).ToArray();
        second[0] = first[0];
        var policy = new ResidentGraphPolicy(_ => null);
        var source = new State { First = first, Second = second };
        var captured = Capture(Roots(("state", source)), policy);
        Assert.AreEqual(100002, captured.graph.Nodes.Length);
        var revision = captured.graph.WireRevision;
        var packed = CanonicalCarryGraphWire.Encode(captured.graph);
        captured.graph.WireRevision = 4;
        var fixedWidth = CanonicalCarryGraphWire.Encode(captured.graph);
        Assert.IsTrue(packed.Length < fixedWidth.Length);
        CollectionAssert.AreEqual(fixedWidth, CanonicalCarryGraphWire.Encode(CanonicalCarryGraphWire.Decode(fixedWidth)));
        captured.graph.WireRevision = revision;
        CollectionAssert.AreEqual(packed, CanonicalCarryGraphWire.Encode(captured.graph));
        var target = new State();
        var plan = Codec(captured.catalog, policy).PrepareOwnedGraph(captured.graph, Roots(("state", target)), NoBinding);
        plan.Apply();
        var restoredFirst = (object[])target.First!;
        var restoredSecond = (object[])target.Second!;
        Assert.AreSame(restoredFirst[0], restoredSecond[0]);
        Assert.AreEqual(99999, restoredFirst.Concat(restoredSecond).Distinct(ReferenceEqualityComparer.Instance).Count());
        plan.VerifyReadback();
    }

    [TestMethod]
    public void PackedNativeOutputUsesByteBudgetAndPreservesArrayAlias()
    {
        var bytes = new byte[200000]; bytes[0] = 19; bytes[bytes.Length - 1] = 203;
        var policy = new ResidentGraphPolicy(_ => null);
        var captured = Capture(Roots(("state", new State { First = bytes, Alias = bytes })), policy);
        var target = new State();
        var plan = Codec(captured.catalog, policy).PrepareOwnedGraph(captured.graph, Roots(("state", target)), NoBinding);
        plan.Apply();
        Assert.AreSame(target.First, target.Alias);
        CollectionAssert.AreEqual(bytes, (byte[])target.First!);
        plan.VerifyReadback();
    }

    [TestMethod]
    public void PrimitiveVectorsKeepExactBitsAndAliasAcrossReplayOfPreparedGraph()
    {
        var values = new[] { BitConverter.Int32BitsToSingle(unchecked((int)0xffc00123)), -0f };
        var source = new State { First = values, Alias = values };
        var policy = new ResidentGraphPolicy(_ => null);
        var captured = Capture(Roots(("state", source)), policy);
        var vector = captured.graph.Nodes.Single(node => node.Kind == "primitive-vector");
        var target = new State();
        var codec = Codec(captured.catalog, policy);
        var plan = codec.PrepareOwnedGraph(captured.graph, Roots(("state", target)), NoBinding);
        plan.Apply();
        Assert.AreSame(target.First, target.Alias);
        var copy = (float[])target.First!;
        Assert.AreEqual(BitConverter.SingleToInt32Bits(values[0]), BitConverter.SingleToInt32Bits(copy[0]));
        Assert.AreEqual(BitConverter.SingleToInt32Bits(values[1]), BitConverter.SingleToInt32Bits(copy[1]));
        copy[0] = 1f;
        Assert.ThrowsExactly<InvalidOperationException>(() => plan.VerifyReadback());
        vector.Packed = new byte[3];
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareOwnedGraph(captured.graph, Roots(("state", new State())), NoBinding));
    }

    [TestMethod]
    public void Arbitrary_destroyed_types_keep_complete_fields_cycles_and_shared_identity()
    {
        var source = new State(); var first = Wrapper<DeadOne>(); var second = Wrapper<DeadTwo>();
        first.Number = 71; first.Link = second; second.Text = "dead"; second.Link = source;
        source.First = first; source.Alias = first; source.Second = second;
        var policy = new ResidentGraphPolicy(_ => null);
        var captured = Capture(Roots(("state", source)), policy);
        Assert.AreEqual(2, captured.graph.Nodes.Count(node => node.Kind == "destroyed-unity"));
        var manifest = ResidentManagedTypeCatalog.DecodeManifest(ResidentManagedTypeCatalog.ManifestBytes(captured.catalog.Manifest));
        var targetCatalog = ResidentManagedTypeCatalog.ResolveManifest(manifest, policy); var target = new State();
        var plan = Codec(targetCatalog, policy).PrepareOwnedGraph(captured.graph, Roots(("state", target)), NoBinding);
        Assert.IsNull(target.First); plan.Apply();
        var restoredFirst = (DeadOne)target.First!; var restoredSecond = (DeadTwo)target.Second!;
        Assert.AreNotSame(first, restoredFirst); Assert.AreNotSame(second, restoredSecond);
        Assert.AreSame(restoredFirst, target.Alias); Assert.AreSame(restoredSecond, restoredFirst.Link);
        Assert.AreSame(target, restoredSecond.Link); Assert.AreEqual(71, restoredFirst.Number); Assert.AreEqual("dead", restoredSecond.Text);
        Assert.AreEqual(IntPtr.Zero, Pointer.GetValue(restoredFirst)); Assert.AreEqual(IntPtr.Zero, Pointer.GetValue(restoredSecond));
        plan.VerifyReadback();
        restoredFirst.Number++;
        Assert.ThrowsExactly<InvalidOperationException>(plan.VerifyReadback);
    }

    [TestMethod]
    public void Live_native_owners_pair_by_directory_binding_and_keep_target_pointer()
    {
        var sourceOwner = Wrapper<NativeOwner>(0x1111); sourceOwner.Number = 42;
        var source = new State { First = sourceOwner, Alias = sourceOwner }; sourceOwner.Link = source;
        var sourcePolicy = new ResidentGraphPolicy(value => ReferenceEquals(value, sourceOwner) ? "resident/owner/0" : null);
        var captured = Capture(Roots(("state", source), ("component", sourceOwner)), sourcePolicy);
        Assert.AreEqual(1, captured.graph.Nodes.Count(node => node.Kind == "native-owner"));
        var targetOwner = Wrapper<NativeOwner>(0x2222); targetOwner.Number = -1; var target = new State();
        var targetPolicy = new ResidentGraphPolicy(value => ReferenceEquals(value, targetOwner) ? "resident/owner/0" : null);
        var targetCatalog = ResidentManagedTypeCatalog.ResolveManifest(captured.catalog.Manifest, targetPolicy);
        var plan = Codec(targetCatalog, targetPolicy).PrepareOwnedGraph(captured.graph, Roots(("state", target), ("component", targetOwner)), NoBinding);
        Assert.AreEqual(-1, targetOwner.Number); plan.Apply();
        Assert.AreEqual(42, targetOwner.Number); Assert.AreEqual(new IntPtr(0x2222), Pointer.GetValue(targetOwner));
        Assert.AreSame(targetOwner, target.First); Assert.AreSame(targetOwner, target.Alias); Assert.AreSame(target, targetOwner.Link);
        Pointer.SetValue(targetOwner, new IntPtr(0x3333));
        Assert.ThrowsExactly<InvalidOperationException>(plan.VerifyReadback);
    }

    [TestMethod]
    public void Delegate_closure_cycles_multicast_order_and_aliases_are_generic()
    {
        var source = new State(); var closure = new Closure { Number = 15, Owner = source };
        Func<int, int> single = closure.Add; var combined = (Func<int, int>)Delegate.Combine(single, single);
        source.First = closure; source.Callback = combined; source.CallbackAlias = combined; closure.Callback = combined;
        var policy = new ResidentGraphPolicy(_ => null); var captured = Capture(Roots(("state", source)), policy);
        Assert.AreEqual(1, captured.graph.Nodes.Count(node => node.Kind == "delegate"));
        var target = new State(); var targetCatalog = ResidentManagedTypeCatalog.ResolveManifest(captured.catalog.Manifest, policy);
        var plan = Codec(targetCatalog, policy).PrepareOwnedGraph(captured.graph, Roots(("state", target)), NoBinding); plan.Apply();
        var restored = (Closure)target.First!;
        Assert.AreSame(target, restored.Owner); Assert.AreSame(target.Callback, target.CallbackAlias); Assert.AreSame(target.Callback, restored.Callback);
        Assert.AreEqual(2, target.Callback!.GetInvocationList().Length);
        foreach (var call in target.Callback.GetInvocationList()) Assert.AreSame(restored, call.Target);
        Assert.AreEqual(20, ((Func<int, int>)target.Callback)(5));
        plan.VerifyReadback();
    }

    [TestMethod]
    public void Closed_generic_methods_and_closed_static_null_targets_roundtrip_loaded_metadata()
    {
        Func<string, string> generic = GenericMethods<int>.Echo<string>;
        var method = typeof(GenericMethods<int>).GetMethod(nameof(GenericMethods<int>.Prefix))!;
        var closedNull = (Func<string, string>)Delegate.CreateDelegate(typeof(Func<string, string>), null, method, true);
        var source = new State { Callback = generic, CallbackAlias = closedNull, Metadata = generic.Method };
        var policy = new ResidentGraphPolicy(_ => null); var captured = Capture(Roots(("state", source)), policy);
        var target = new State(); var catalog = ResidentManagedTypeCatalog.ResolveManifest(captured.catalog.Manifest, policy);
        Codec(catalog, policy).PrepareOwnedGraph(captured.graph, Roots(("state", target)), NoBinding).Apply();
        Assert.AreEqual("abc", ((Func<string, string>)target.Callback!)("abc"));
        Assert.AreEqual("null:abc", ((Func<string, string>)target.CallbackAlias!)("abc"));
        Assert.AreEqual(generic.Method, (MethodInfo)target.Metadata!);
    }

    [TestMethod]
    public void Missing_loaded_module_and_omitted_managed_field_are_rejected_before_application()
    {
        var source = new State { First = new Closure { Number = 7 } }; var policy = new ResidentGraphPolicy(_ => null);
        var captured = Capture(Roots(("state", source)), policy);
        var manifest = ResidentManagedTypeCatalog.DecodeManifest(ResidentManagedTypeCatalog.ManifestBytes(captured.catalog.Manifest));
        manifest.Types.First(entry => entry.Type.Kind == "type").Type.Module = Guid.Empty.ToString("D");
        Assert.ThrowsExactly<InvalidDataException>(() => ResidentManagedTypeCatalog.ResolveManifest(manifest, policy));
        var stateNode = captured.graph.Nodes.Single(node => node.Type == CanonicalCarryGraphCodec.Identity(typeof(State)));
        stateNode.Fields = stateNode.Fields!.Skip(1).ToArray();
        var target = new State(); var catalog = ResidentManagedTypeCatalog.ResolveManifest(captured.catalog.Manifest, policy);
        Assert.ThrowsExactly<InvalidDataException>(() => Codec(catalog, policy).PrepareOwnedGraph(captured.graph, Roots(("state", target)), NoBinding));
        Assert.IsNull(target.First);
        Assert.ThrowsExactly<InvalidDataException>(() => ResidentManagedTypeCatalog.Capture(
            Roots(("pointer", new OtherPointer { Value = IntPtr.Zero })), policy, NoExternal));
    }
}
