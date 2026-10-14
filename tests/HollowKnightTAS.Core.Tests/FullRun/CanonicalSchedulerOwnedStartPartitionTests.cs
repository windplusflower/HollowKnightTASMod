using System.IO;
using System.Linq;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalSchedulerOwnedStartPartitionTests
{
    private static CanonicalSchedulerCarry StartQueue() => new() { SourceNative = 10, Timestamp = 7,
        Callbacks = new[] { "pool/Fsm", "charms/5/Fsm", "charms/25/Fsm", "lights/Fsm" }
            .Select((owner, order) => new CanonicalSchedulerCarry.Callback { Order = order, Owner = owner,
                Call = "UnityPlayer:8ac680", Cleanup = "none", Frame = -1, Mode = 7, WakeTimeBits = 123 }).ToArray() };

    [TestMethod]
    public void PartitionPreservesSourceAndRemainingOrderAndRawValues()
    {
        var source = StartQueue(); var remaining = CanonicalSchedulerOwnedStartPartition.WithoutOwnedStarts(source, "charms/");
        CollectionAssert.AreEqual(new[] { "pool/Fsm", "lights/Fsm" }, remaining.Callbacks.Select(value => value.Owner).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1 }, remaining.Callbacks.Select(value => value.Order).ToArray());
        foreach (var value in remaining.Callbacks) Assert.AreEqual(123L, value.WakeTimeBits);
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, source.Callbacks.Select(value => value.Order).ToArray());
        Assert.AreNotSame(source.Callbacks[0], remaining.Callbacks[0]);
        Assert.AreEqual(source.Timestamp, remaining.Timestamp);
    }
    [TestMethod]
    public void OwnedFolderCannotAbsorbAnotherCallbackFamily()
    {
        var source = StartQueue(); source.Callbacks[1].Call = "UnityPlayer:629d80";
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalSchedulerOwnedStartPartition.WithoutOwnedStarts(source, "charms/"));
    }
    [TestMethod]
    public void PrefixCannotAbsorbSiblingWithSimilarName()
    {
        var source = StartQueue(); source.Callbacks[0].Owner = "charms-other/Fsm";
        var remaining = CanonicalSchedulerOwnedStartPartition.WithoutOwnedStarts(source, "charms/");
        Assert.AreEqual("charms-other/Fsm", remaining.Callbacks[0].Owner);
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalSchedulerOwnedStartPartition.WithoutOwnedStarts(source, "charms"));
    }
    [TestMethod]
    public void EmptyOwnedDirectoryStillCoversEverySourceCallback()
    {
        var source = StartQueue(); var remaining = CanonicalSchedulerOwnedStartPartition.WithoutOwnedStarts(source, "absent/");
        Assert.AreEqual(source.Callbacks.Length, remaining.Callbacks.Length);
        CollectionAssert.AreEqual(source.Callbacks.Select(value => value.Owner).ToArray(), remaining.Callbacks.Select(value => value.Owner).ToArray());
    }
}
