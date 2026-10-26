using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace HollowKnightTAS.Core.Tests.FullRun;
[TestClass]
public sealed class CanonicalGeoSourceQueueContractTests
{
    private const string Prefix = "world/Geo", Geo = Prefix + ":PlayMakerFSM[0]";
    private static CanonicalSchedulerCarry.Callback Start(string owner, int order) => new() {
        Owner = owner, Order = order, Call = "UnityPlayer:8ac680", Cleanup = "none", Frame = -1, Mode = 7 };
    private static CanonicalSchedulerCarry Queue(params CanonicalSchedulerCarry.Callback[] callbacks) => new() {
        SourceNative = 100, Timestamp = 10, Callbacks = callbacks };
    private static Dictionary<string, CanonicalGeoSourceQueueContract.OwnerState> Owners() => new() {
        [Geo] = new() { Lifecycle = new byte[] { 0, 0, 1, 0, 0 } } };
    private static (CanonicalSchedulerCarry Source, CanonicalSchedulerCarry Actual, string[] Border) PendingBorder()
    {
        var border = new[] { "Pane Name", "Pane Name L", "Pane Name R" }.SelectMany(leaf =>
            new[] { "TMPro.TextContainer", "TMPro.TextMeshPro", "ChangeFontByLanguage" }.Select(type =>
                "DontDestroyOnLoad/_GameCameras[0]/HudCamera[0]/Inventory[0]/Border[0]/" + leaf + "[0]:" + type + "[0]")).ToArray();
        var source = Queue(new[] { "other/A", "other/B" }.Concat(border).Select((name, index) => Start(name, index)).ToArray());
        var actual = Queue(border.Concat(new[] { "other/A", Geo, "other/B" }).Select((name, index) => Start(name, index)).ToArray());
        return (source, actual, border);
    }
    [TestMethod]
    public void PendingBorder_changes_only_relative_insertion_position_until_final_original_birth()
    {
        var (source, actual, border) = PendingBorder();
        CollectionAssert.AreEqual(new[] { 10 }, CanonicalGeoSourceQueueContract.Select(source, actual, Prefix, Owners(), border));
        var wrongFinal = Queue(border.Concat(new[] { "other/A", "other/B" }).Select((name, index) => Start(name, index)).ToArray());
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalSchedulerRestorePlan.Prepare(source, wrongFinal));
        CanonicalSchedulerRestorePlan.Prepare(source, Queue(source.Callbacks.Select((item, index) => Start(item.Owner, index)).ToArray()));
    }
    [TestMethod]
    public void PendingBorder_external_lookalike_nonBorder_order_payload_and_membership_still_reject()
    {
        for (var mode = 0; mode < 10; mode++) {
            var (source, actual, border) = PendingBorder();
            if (mode == 0) actual.Callbacks[0].Owner += "/foreign-same-type";
            if (mode == 1) { actual.Callbacks[9].Owner = "other/B"; actual.Callbacks[11].Owner = "other/A"; }
            if (mode == 2) actual.Callbacks[0].WakeTimeBits++;
            if (mode == 3) actual.Callbacks[0].Call = "foreign-callback";
            if (mode == 4) actual.Callbacks[0].Rate = 1;
            if (mode == 5) { var name = actual.Callbacks[0].Owner; actual.Callbacks[0].Owner = actual.Callbacks[1].Owner; actual.Callbacks[1].Owner = name; }
            if (mode == 6) border = border.Take(8).ToArray();
            if (mode == 7) actual.Callbacks[9].WakeTimeBits++;
            if (mode == 8) actual = Queue(actual.Callbacks.Concat(new[] { Start("DontDestroyOnLoad/_GameCameras[0]/HudCamera[0]/Inventory[0]/Border[0]/Pane Name[0]:UnboldIfCJK[0]", actual.Callbacks.Length) }).ToArray());
            if (mode == 9) actual = Queue(actual.Callbacks.Skip(1).Select((item, index) => Start(item.Owner, index)).ToArray());
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalGeoSourceQueueContract.Select(source, actual, Prefix, Owners(), border));
        }
    }
    [TestMethod]
    public void Frozen_directory_reuse_retains_the_exact_name_object_and_strong_native_owner_witness()
    {
        var owner = new object(); var name = new string("world/Geo:PlayMakerFSM[0]".ToCharArray());
        Assert.AreSame(name, CanonicalGeoSourceQueueContract.ResolveFrozenOwner(owner, new IntPtr(17), name, owner, new IntPtr(17), true));
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalGeoSourceQueueContract.ResolveFrozenOwner(owner, new IntPtr(17), name, new object(), new IntPtr(17), true));
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalGeoSourceQueueContract.ResolveFrozenOwner(owner, new IntPtr(17), name, owner, new IntPtr(18), true));
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalGeoSourceQueueContract.ResolveFrozenOwner(owner, new IntPtr(17), name, owner, new IntPtr(17), false));
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalGeoSourceQueueContract.ResolveFrozenOwner(null!, new IntPtr(17), name, null!, new IntPtr(17), true));
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalGeoSourceQueueContract.ResolveFrozenOwner(owner, IntPtr.Zero, name, owner, IntPtr.Zero, true));
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalGeoSourceQueueContract.ResolveFrozenOwner(owner, new IntPtr(17), "", owner, new IntPtr(17), true));
    }
    [TestMethod]
    public void Only_exact_source_absent_inactive_produced_start_is_selected_without_changing_other_nodes()
    {
        var first = Start("world/Other:A[0]", 0); var last = Start("world/Other:B[0]", 1);
        var source = Queue(first, last);
        var actualFirst = Start(first.Owner, 0); var extra = Start(Geo, 1); var actualLast = Start(last.Owner, 2);
        var actual = Queue(actualFirst, extra, actualLast);
        CollectionAssert.AreEqual(new[] { 1 }, CanonicalGeoSourceQueueContract.Select(source, actual, Prefix, Owners()));
        Assert.AreSame(extra, actual.Callbacks[1]); Assert.AreEqual(2, actualLast.Order);
        Assert.AreEqual(0, CanonicalGeoSourceQueueContract.Select(source, Queue(first, last), Prefix, Owners()).Length);
        var awakeButInactive = Owners(); awakeButInactive[Geo].Lifecycle[0] = 1;
        CollectionAssert.AreEqual(new[] { 1 }, CanonicalGeoSourceQueueContract.Select(source, actual, Prefix, awakeButInactive));
    }
    [TestMethod]
    public void Active_added_started_unknown_duplicate_and_source_pending_owners_are_rejected()
    {
        for (var mode = 0; mode < 7; mode++) {
            var source = Queue(); var actual = Queue(Start(Geo, 0)); var owners = Owners();
            if (mode == 0) owners[Geo].Active = true;
            if (mode == 1) owners[Geo].Lifecycle[4] = 1;
            if (mode == 2) owners[Geo].Lifecycle[1] = 1;
            if (mode == 3) actual = Queue(Start(Prefix + "/Unknown:A[0]", 0));
            if (mode == 4) actual = Queue(Start(Geo, 0), Start(Geo, 1));
            if (mode == 5) source = Queue(Start(Geo, 0));
            if (mode == 6) actual.Callbacks[0].Call = "unknown";
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalGeoSourceQueueContract.Select(source, actual, Prefix, owners));
        }
    }
    [TestMethod]
    public void Non_geo_membership_order_scalar_timestamp_and_prefix_lookalike_differences_remain_guarded()
    {
        for (var mode = 0; mode < 5; mode++) {
            var source = Queue(Start("world/Other:A[0]", 0), Start("world/Other:B[0]", 1));
            var actual = Queue(Start("world/Other:A[0]", 0), Start(Geo, 1), Start("world/Other:B[0]", 2));
            if (mode == 0) actual.Callbacks[2].Owner = Prefix + "Other:A[0]";
            if (mode == 1) { actual.Callbacks[0].Owner = "world/Other:B[0]"; actual.Callbacks[2].Owner = "world/Other:A[0]"; }
            if (mode == 2) actual.Callbacks[0].WakeTimeBits = BitConverter.DoubleToInt64Bits(1.0);
            if (mode == 3) actual.Timestamp++;
            if (mode == 4) actual = Queue(Start(Geo, 0));
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalGeoSourceQueueContract.Select(source, actual, Prefix, Owners()));
        }
    }
}
