using System;
using System.IO;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class ResidentDirectorFenceTests
{
    private static byte[] Header()
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        foreach (var v in new uint[]{0x31464452,4,200,1}) w.Write(v);
        foreach (var v in new ulong[]{4,19,30,30,2,3,1120}) w.Write(v);
        foreach (var v in new uint[]{0,1,1,0,9,9,153,51,7,0}) w.Write(v);
        foreach (var v in new ulong[]{3,3,27,27}) w.Write(v);
        w.Write(0u); w.Write(0u);
        foreach (var v in new ulong[]{5,5,8,8,2}) w.Write(v);
        w.Write(0u); w.Write(0u);
        return stream.ToArray();
    }
    [TestMethod]
    public void BlockedWaitersCanBeAdmittedWithoutGrantingRestoreCoverage()
    {
        var projected = ResidentDirectorFence.Read(Header(),4,19,9,out var witness);
        Assert.AreEqual(30UL,witness.Issued); Assert.AreEqual(1120UL,witness.Bytes);
        Assert.AreEqual(3,ResidentDirectorFence.Gaps(projected).Length);
        Assert.ThrowsExactly<InvalidDataException>(() => ResidentDirectorFence.RequireComplete(projected));
        projected.RequiredGaps=0;
        Assert.ThrowsExactly<InvalidDataException>(() => ResidentDirectorFence.Gaps(projected));
    }
    [TestMethod]
    public void BalancedGroupOrCallbackDriftInsideTheSameEpochIsRejected()
    {
        ResidentDirectorFence.Read(Header(),4,19,9,out var first);
        foreach (var offset in new[]{112,128,152,168}) {
            var changed = Header(); changed[offset]++; changed[offset+8]++;
            ResidentDirectorFence.Read(changed,4,19,9,out var next);
            Assert.ThrowsExactly<InvalidDataException>(() => first.RequireSame(next));
        }
    }
    [TestMethod]
    public void CrossEpochReceiptUnfinishedWriterAndForgedCompletionAreRejected()
    {
        foreach (var offset in new[]{4,12,16,24,40,72,76,80,84,88,92,96,100,104,108,112,120,128,136,144,148,152,160,168,176,192,196}) {
            var bytes = Header(); bytes[offset] ^= 1;
            Assert.ThrowsExactly<InvalidDataException>(() => ResidentDirectorFence.Read(bytes,4,19,9,out _));
        }
    }
}
