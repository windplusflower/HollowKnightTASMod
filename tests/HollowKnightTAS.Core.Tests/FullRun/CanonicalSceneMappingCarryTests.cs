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
