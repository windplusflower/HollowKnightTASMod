using System;
using System.Collections.Generic;
using System.IO;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalDataPreservationGuardTests
{
    private sealed class Data { public int Value; public Data? Other; public int Getter => throw new InvalidOperationException(); }
    private static CanonicalCarryGraphCodec Codec() => new(new[] { typeof(Data) }, "preservation-v1");
    [TestMethod]
    public void Exact_data_and_owner_preservation_is_a_read_only_audit_without_game_getters()
    {
        var data = new Data { Value = 17 }; data.Other = data; var owner = new object();
        var roots = new Dictionary<string, object> { ["data"] = data }; var scalars = new Dictionary<string, string> { ["gravity"] = "f32:00000000" };
        var audit = new CanonicalDataPreservationGuard(Codec(), () => roots, () => scalars, () => new[] { owner });
        audit.Verify(); Assert.AreEqual(17, data.Value); Assert.AreSame(data, data.Other);
        audit.Verify(); Assert.AreEqual(17, data.Value);
    }
    [TestMethod]
    public void Canonical_operations_cannot_change_nested_player_or_scene_data()
    {
        var child = new Data { Value = 29 }; var data = new Data { Other = child };
        var roots = new Dictionary<string, object> { ["data"] = data };
        var audit = new CanonicalDataPreservationGuard(Codec(), () => roots, () => new Dictionary<string, string>(), () => Array.Empty<object>());
        child.Value = 31; Assert.ThrowsExactly<InvalidOperationException>(() => audit.Verify());
        Assert.AreEqual(31, child.Value); // Audit never repairs or hides the change.
    }
    [TestMethod]
    public void Equal_cloned_roots_or_replaced_native_owners_are_rejected()
    {
        var data = new Data { Value = 17 }; var owner = new object(); var roots = new Dictionary<string, object> { ["data"] = data };
        var owners = new[] { owner };
        var audit = new CanonicalDataPreservationGuard(Codec(), () => roots, () => new Dictionary<string, string>(), () => owners);
        roots["data"] = new Data { Value = 17 }; Assert.ThrowsExactly<InvalidOperationException>(() => audit.Verify());
        roots["data"] = data; owners[0] = new object(); Assert.ThrowsExactly<InvalidOperationException>(() => audit.Verify());
    }
    [TestMethod]
    public void A_single_changed_hero_float_bit_is_rejected()
    {
        var roots = new Dictionary<string, object> { ["data"] = new Data() };
        var scalars = new Dictionary<string, string> { ["velocity"] = "f32:00000000" };
        var audit = new CanonicalDataPreservationGuard(Codec(), () => roots, () => scalars, () => Array.Empty<object>());
        scalars["velocity"] = "f32:80000000";
        Assert.ThrowsExactly<InvalidOperationException>(() => audit.Verify()); Assert.AreEqual("f32:80000000", scalars["velocity"]);
    }
    [TestMethod]
    public void Unresolved_preservation_data_is_not_silently_treated_as_equal()
    {
        var codec = new CanonicalCarryGraphCodec(new[] { typeof(Data) }, "preservation-v1", portability: value => value is Data ? "unresolved" : null);
        Assert.ThrowsExactly<InvalidDataException>(() => new CanonicalDataPreservationGuard(codec,
            () => new Dictionary<string, object> { ["data"] = new Data() }, () => new Dictionary<string, string>(), () => Array.Empty<object>()));
    }
}
