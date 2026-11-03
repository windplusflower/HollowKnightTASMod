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
