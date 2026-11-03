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
public sealed class ResidentInstanceIdGraphTests
{
    private sealed class State
    {
        public object? Collision, CollisionAlias, Contacts, Nested;
        public int Ordinary;
    }
    private struct NestedValue<T> { public T Value; public int Ordinary; public NestedValue(T value, int ordinary) { Value = value; Ordinary = ordinary; } }
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Type Physics(string type, bool threeD = false) => Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory,
        threeD ? "UnityEngine.PhysicsModule.dll" : "UnityEngine.Physics2DModule.dll")).GetType("UnityEngine." + type, true)!;
    private static void Set(object value, string name, object next) => value.GetType().GetField(name, Fields)!.SetValue(value, next);
    private static object? Get(object value, string name) => value.GetType().GetField(name, Fields)!.GetValue(value);
    private static Dictionary<string, object> Roots(State value) => new(StringComparer.Ordinal) { ["state"] = value };
    private static object NoBinding(string key) => throw new InvalidDataException("Unexpected external: " + key);
    private static CanonicalCarryGraphCodec Codec(ResidentManagedTypeCatalog catalog, ResidentGraphPolicy policy)
        => new(catalog.Types, catalog.SchemaIdentity, residentPolicy: policy);
    private static ResidentGraphPolicy SourcePolicy(bool threeD = false) => new(_ => null, (id, target) => {
        if (id == -17) { Assert.AreEqual(threeD ? "UnityEngine.Collider" : "UnityEngine.Collider2D", target.FullName); return "directory/collider[71]"; }
        if (id == 41) { Assert.AreEqual("UnityEngine.Rigidbody2D", target.FullName); return "directory/body[29]"; }
        return null;
    });
    private static ResidentGraphPolicy TargetPolicy(bool threeD = false) => new(_ => null, (id, target) => {
        if (id == 808) { Assert.AreEqual(threeD ? "UnityEngine.Collider" : "UnityEngine.Collider2D", target.FullName); return "directory/collider[71]"; }
        if (id == -909) { Assert.AreEqual("UnityEngine.Rigidbody2D", target.FullName); return "directory/body[29]"; }
        return null;
    }, (binding, target) => binding == "directory/collider[71]" ? 808 : binding == "directory/body[29]" ? -909 : 0);

    private static State CollisionState()
    {
        var collision = RuntimeHelpers.GetUninitializedObject(Physics("Collision2D"));
        var point = Activator.CreateInstance(Physics("ContactPoint2D"))!;
        Set(point, "m_Collider", -17); Set(point, "m_Rigidbody", 41); Set(point, "m_Enabled", 1);
        var points = Array.CreateInstance(point.GetType(), 2); points.SetValue(point, 0); points.SetValue(point, 1);
        Set(collision, "m_Collider", -17); Set(collision, "m_Rigidbody", 41); Set(collision, "m_ContactCount", 1);
        Set(collision, "m_ReusedContacts", points); Set(collision, "m_Enabled", 1);
        var nestedType = typeof(NestedValue<>).MakeGenericType(point.GetType()); var nested = Activator.CreateInstance(nestedType)!;
        Set(nested, "Value", point); Set(nested, "Ordinary", -17);
        var nestedArray = Array.CreateInstance(nestedType, 1); nestedArray.SetValue(nested, 0);
        return new State { Collision = collision, CollisionAlias = collision, Contacts = points, Nested = nestedArray, Ordinary = -17 };
    }

    [TestMethod]
    public void Shared_collision_arrays_and_nested_structs_rebind_ids_before_semantic_readback()
    {
        var source = CollisionState(); var sourcePolicy = SourcePolicy(); var catalog = ResidentManagedTypeCatalog.Capture(Roots(source), sourcePolicy, _ => null);
        var sourceCodec = Codec(catalog, sourcePolicy); var graph = sourceCodec.Capture(Roots(source), _ => null);
        Assert.AreEqual(0, graph.Unsupported.Length); Assert.IsNull(Get(source.Collision!, "m_LegacyContacts"), "Capture must not call the mutating contacts getter.");
        var idFields = graph.Nodes.SelectMany(node => node.Fields ?? Array.Empty<CanonicalCarryGraphData.Field>()).Where(field => field.Value.Kind == "instance-id").ToArray();
        Assert.IsTrue(idFields.Length >= 12); Assert.IsTrue(idFields.Any(field => field.Value.Text == ""));
        Assert.IsTrue(catalog.Manifest.Types.SelectMany(type => type.Fields).Any(field => field.Storage.StartsWith("native-instance-id:", StringComparison.Ordinal)));
        var pointIdentity = CanonicalCarryGraphCodec.Identity(Physics("ContactPoint2D"));
        Assert.IsFalse(graph.Nodes.SelectMany(node => node.Items ?? Array.Empty<CanonicalCarryGraphData.Value>()).Any(value => value.Type == pointIdentity && value.Kind == "value"));
        var frozen = sourceCodec.Encode(graph); graph = sourceCodec.Decode(frozen);
        var targetPolicy = TargetPolicy(); var target = new State(); var targetCodec = Codec(ResidentManagedTypeCatalog.ResolveManifest(catalog.Manifest, targetPolicy), targetPolicy);
        var plan = targetCodec.PrepareOwnedGraph(graph, Roots(target), NoBinding); plan.Apply(); plan.VerifyReadback();
        Assert.AreSame(target.Collision, target.CollisionAlias); Assert.AreSame(target.Contacts, Get(target.Collision!, "m_ReusedContacts"));
        Assert.AreEqual(808, Get(target.Collision!, "m_Collider")); Assert.AreEqual(-909, Get(target.Collision!, "m_Rigidbody"));
        Assert.AreEqual(-17, target.Ordinary); Assert.IsNull(Get(target.Collision!, "m_LegacyContacts"));
        var restoredPoint = ((Array)target.Contacts!).GetValue(1)!; Assert.AreEqual(808, Get(restoredPoint, "m_Collider")); Assert.AreEqual(1, Get(restoredPoint, "m_Enabled"));
        var nested = ((Array)target.Nested!).GetValue(0)!; Assert.AreEqual(808, Get(Get(nested, "Value")!, "m_Collider")); Assert.AreEqual(-17, Get(nested, "Ordinary"));
        CollectionAssert.AreEqual(frozen, targetCodec.Encode(targetCodec.Capture(Roots(target), _ => null)), "Different process IDs must produce the same canonical graph.");
        Set(restoredPoint, "m_Collider", 0); ((Array)target.Contacts).SetValue(restoredPoint, 1);
        Assert.ThrowsExactly<InvalidOperationException>(plan.VerifyReadback);
    }

    [TestMethod]
    public void Nonzero_ids_without_a_directory_mapper_are_explicit_coverage_gaps()
    {
        var source = CollisionState(); var policy = new ResidentGraphPolicy(_ => null);
        var catalog = ResidentManagedTypeCatalog.Capture(Roots(source), policy, _ => null); var graph = Codec(catalog, policy).Capture(Roots(source), _ => null);
        Assert.IsTrue(graph.Unsupported.Any(value => value.Contains("instance ID", StringComparison.Ordinal)));
        Assert.IsFalse(graph.Nodes.SelectMany(node => node.Fields ?? Array.Empty<CanonicalCarryGraphData.Field>()).Any(field => field.Name.EndsWith("/m_Collider", StringComparison.Ordinal) && field.Value.Kind == "scalar"));
    }

    [TestMethod]
    public void Semantic_fields_cannot_be_imported_as_raw_scalars_or_attached_to_ordinary_ints()
    {
        var source = CollisionState(); var policy = SourcePolicy(); var catalog = ResidentManagedTypeCatalog.Capture(Roots(source), policy, _ => null);
        var codec = Codec(catalog, policy); var bytes = codec.Encode(codec.Capture(Roots(source), _ => null)); var targetPolicy = TargetPolicy();
        var targetCodec = Codec(ResidentManagedTypeCatalog.ResolveManifest(catalog.Manifest, targetPolicy), targetPolicy); var target = new State { Ordinary = 1234 };
        var raw = codec.Decode(bytes); var semantic = raw.Nodes.First(node => node.Fields?.Any(field => field.Value.Kind == "instance-id") == true);
        var semanticIndex = Array.FindIndex(semantic.Fields!, field => field.Value.Kind == "instance-id");
        semantic.Fields![semanticIndex].Value.Kind = "scalar"; semantic.Fields[semanticIndex].Value.Text = "-17";
        Assert.ThrowsExactly<InvalidDataException>(() => targetCodec.PrepareOwnedGraph(raw, Roots(target), NoBinding)); Assert.AreEqual(1234, target.Ordinary); Assert.IsNull(target.Collision);
        var forged = codec.Decode(bytes); var ordinary = forged.Nodes.Single(node => node.Type == CanonicalCarryGraphCodec.Identity(typeof(State)));
        var ordinaryIndex = Array.FindIndex(ordinary.Fields!, field => field.Name.EndsWith("/Ordinary", StringComparison.Ordinal));
        ordinary.Fields![ordinaryIndex].Value.Kind = "instance-id"; ordinary.Fields[ordinaryIndex].Value.Text = "directory/collider[71]";
        Assert.ThrowsExactly<InvalidDataException>(() => targetCodec.PrepareOwnedGraph(forged, Roots(target), NoBinding)); Assert.AreEqual(1234, target.Ordinary);
    }

    [TestMethod]
    public void Missing_target_and_wrong_reverse_binding_fail_before_live_writes()
    {
        var source = CollisionState(); var policy = SourcePolicy(); var catalog = ResidentManagedTypeCatalog.Capture(Roots(source), policy, _ => null);
        var graph = Codec(catalog, policy).Capture(Roots(source), _ => null); var target = new State { Ordinary = 1234 };
        foreach (var targetPolicy in new[] { new ResidentGraphPolicy(_ => null, (_, _) => null, (_, _) => 0),
            new ResidentGraphPolicy(_ => null, (_, _) => "directory/wrong", (_, _) => 808) }) {
            var codec = Codec(ResidentManagedTypeCatalog.ResolveManifest(catalog.Manifest, targetPolicy), targetPolicy);
            Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareOwnedGraph(graph, Roots(target), NoBinding)); Assert.AreEqual(1234, target.Ordinary); Assert.IsNull(target.Collision);
        }
    }

    [TestMethod]
    public void Raycast_and_three_dimensional_contacts_use_their_actual_native_field_semantics()
    {
        foreach (var item in new[] { (name: "RaycastHit2D", threeD: false, fields: new[] { "m_Collider" }),
            (name: "RaycastHit", threeD: true, fields: new[] { "m_Collider" }),
            (name: "ContactPoint", threeD: true, fields: new[] { "m_ThisColliderInstanceID", "m_OtherColliderInstanceID" }) }) {
            var type = Physics(item.name, item.threeD); var value = Activator.CreateInstance(type)!;
            foreach (var field in item.fields) Set(value, field, -17);
            var array = Array.CreateInstance(type, 1); array.SetValue(value, 0); var source = new State { Contacts = array };
            var sourcePolicy = SourcePolicy(item.threeD); var catalog = ResidentManagedTypeCatalog.Capture(Roots(source), sourcePolicy, _ => null);
            var sourceCodec = Codec(catalog, sourcePolicy); var graph = sourceCodec.Capture(Roots(source), _ => null); Assert.AreEqual(0, graph.Unsupported.Length);
            var targetPolicy = TargetPolicy(item.threeD); var target = new State(); var targetCodec = Codec(ResidentManagedTypeCatalog.ResolveManifest(catalog.Manifest, targetPolicy), targetPolicy);
            targetCodec.PrepareOwnedGraph(graph, Roots(target), NoBinding).Apply();
            foreach (var field in item.fields) Assert.AreEqual(808, Get(((Array)target.Contacts!).GetValue(0)!, field));
            CollectionAssert.AreEqual(sourceCodec.Encode(graph), targetCodec.Encode(targetCodec.Capture(Roots(target), _ => null)));
        }
    }
}
