using System;
using System.Collections.Generic;
using System.IO;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalSceneMappingCarryTests
{
    private static CanonicalSceneMappingCarry.Data Source() => new() { Items = new[] { new CanonicalSceneMappingCarry.Mapping {
        Order = 0, MethodModuleMvid = CanonicalSceneMappingCarry.ModuleMvid, MethodDeclaringType = CanonicalSceneMappingCarry.ClosureType,
        MethodName = CanonicalSceneMappingCarry.HandlerName, MethodToken = CanonicalSceneMappingCarry.HandlerToken,
        MapZone = 33, ManualMapTrigger = true, UsesCurrentPlayerData = false } } };
    [TestMethod]
    public void Mapping_flags_are_read_from_saved_data_before_an_original_target_callback_is_changed()
    {
        var codec = new CanonicalCarryGraphCodec(CanonicalSceneMappingCarry.DataTypes, "mapping-v1");
        var source = Source(); var graph = codec.Capture(new Dictionary<string, object> { [CanonicalSceneMappingCarry.Root] = source }, _ => null);
        var target = new CanonicalSceneMappingCarry.Data(); CanonicalSceneMappingCarry.Data? decoded = null;
        codec.PrepareApply(graph, new Dictionary<string, object> { [CanonicalSceneMappingCarry.Root] = target },
            _ => throw new InvalidDataException(), view => decoded = CanonicalSceneMappingCarry.FromPrepared(view));
        Assert.AreEqual(0, target.Items.Length); Assert.AreEqual(33, decoded!.Items[0].MapZone); Assert.IsTrue(decoded.Items[0].ManualMapTrigger);
        Assert.IsFalse(decoded.Items[0].UsesCurrentPlayerData);
        source.Items[0].MapZone = 4; Assert.AreEqual(33, decoded.Items[0].MapZone);
    }
    [TestMethod]
    public void Current_and_detached_PlayerData_roles_are_saved_explicitly_and_not_inferred_from_a_delegate_type()
    {
        var source = Source(); var detached = CanonicalSceneMappingCarry.Copy(source);
        source.Items[0].UsesCurrentPlayerData = true; var current = CanonicalSceneMappingCarry.Copy(source);
        Assert.IsFalse(detached.Items[0].UsesCurrentPlayerData); Assert.IsTrue(current.Items[0].UsesCurrentPlayerData);
    }
    [TestMethod]
    public void Empty_original_mapping_chain_is_explicitly_preserved_without_a_synthetic_handler()
    {
        var source = new CanonicalSceneMappingCarry.Data(); var copy = CanonicalSceneMappingCarry.Copy(source);
        Assert.AreEqual(0, copy.Items.Length);
    }
    [TestMethod]
    public void Empty_source_owner_selection_uses_the_real_saved_graph_and_keeps_pending_binding_path()
    {
        var codec = new CanonicalCarryGraphCodec(CanonicalSceneMappingCarry.DataTypes, "mapping-v1");
        foreach (var pending in new[] { false, true }) {
            var graph = codec.Capture(new Dictionary<string, object> { [CanonicalSceneMappingCarry.Root] = pending ? Source() : new CanonicalSceneMappingCarry.Data() }, _ => null);
            var before = CanonicalCarryGraphWire.Encode(graph);
            Assert.AreEqual(!pending, CanonicalSceneMappingCarry.IsEmptySourceGraph(graph));
            CollectionAssert.AreEqual(before, CanonicalCarryGraphWire.Encode(graph));
        }
    }
    [TestMethod]
    [DataRow("version")]
    [DataRow("missing-root")]
    [DataRow("extra-detached-root")]
    [DataRow("array-type")]
    [DataRow("array-reference")]
    [DataRow("field-registry")]
    public void Empty_source_owner_selection_rejects_unproven_or_inconsistent_saved_layout(string fault)
    {
        var codec = new CanonicalCarryGraphCodec(CanonicalSceneMappingCarry.DataTypes, "mapping-v1");
        var graph = codec.Capture(new Dictionary<string, object> { [CanonicalSceneMappingCarry.Root] = new CanonicalSceneMappingCarry.Data() }, _ => null);
        var node = graph.Nodes[graph.Roots[0].Node - 1];
        if (fault == "version") node.Fields![1].Value.Text = "2";
        if (fault == "missing-root") graph.Roots = Array.Empty<CanonicalCarryGraphData.Root>();
        if (fault == "extra-detached-root") graph.Roots = new[] { graph.Roots[0], new CanonicalCarryGraphData.Root { Binding = CanonicalSceneMappingCarry.DetachedPlayerRoot, Node = node.Id } };
        if (fault == "array-type") node.Fields![0].Value.Type = CanonicalCarryGraphCodec.Identity(typeof(object[]));
        if (fault == "array-reference") node.Fields![0].Value.Reference = 0;
        if (fault == "field-registry") node.Fields![1].Name += "-foreign";
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalSceneMappingCarry.IsEmptySourceGraph(graph));
    }
    [TestMethod]
    [DataRow("module")]
    [DataRow("method")]
    [DataRow("type")]
    [DataRow("order")]
    [DataRow("token")]
    [DataRow("multiple")]
    public void Unregistered_callback_identity_or_multiplicity_cannot_be_accepted_as_a_destroyed_visual_reference(string fault)
    {
        var data = Source();
        if (fault == "module") data.Items[0].MethodModuleMvid = Guid.Empty.ToString("D");
        if (fault == "method") data.Items[0].MethodName = "other-callback";
        if (fault == "type") data.Items[0].MethodDeclaringType = "other-closure";
        if (fault == "order") data.Items[0].Order = 1;
        if (fault == "token") data.Items[0].MethodToken++;
        if (fault == "multiple") data.Items = new[] { data.Items[0], data.Items[0] };
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalSceneMappingCarry.Copy(data));
    }
}
