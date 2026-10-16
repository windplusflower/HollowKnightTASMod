using System;
using System.Collections.Generic;
using System.IO;
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
