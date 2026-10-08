using System;
using System.Linq;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class LoadingRigidbodyValueBitsTests
{
    [TestMethod]
    public void PassivePrimaryPreservesExistingBodyBitsInsteadOfTransformCoordinates()
    {
        var owner = new byte[296];
        var body = new byte[200];
        var bits = new[] { unchecked((int)0x80000000), 0x7fc01234, unchecked((int)0xffc05678), 0x3f800001 };
        var offsets = new[] { 0xc, 0x10, 0x40, 0x44 };
        for (var i = 0; i < bits.Length; i++) Buffer.BlockCopy(BitConverter.GetBytes(bits[i]), 0, body, offsets[i], 4);
        var value = LoadingRigidbodyValueBits.Decode(owner, body, 999f, 888f);
        CollectionAssert.AreEqual(bits, new[] { value.X, value.Y, value.Vx, value.Vy }.Select(BitConverter.SingleToInt32Bits).ToArray());
    }

    [TestMethod]
    public void StaticAndBodylessVelocityMatchPinnedGetterBranches()
    {
        var owner = new byte[296];
        var body = new byte[200];
        Buffer.BlockCopy(BitConverter.GetBytes(2), 0, owner, 0x5c, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(7f), 0, body, 0xc, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(8f), 0, body, 0x10, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(9f), 0, body, 0x40, 4);
        var value = LoadingRigidbodyValueBits.Decode(owner, body, 999f, 888f);
        Assert.AreEqual(7f, value.X); Assert.AreEqual(8f, value.Y);
        Assert.AreEqual(0, BitConverter.SingleToInt32Bits(value.Vx));
        Assert.AreEqual(0, BitConverter.SingleToInt32Bits(value.Vy));
        value = LoadingRigidbodyValueBits.Decode(owner, null, -0f, 123f);
        Assert.AreEqual(BitConverter.SingleToInt32Bits(-0f), BitConverter.SingleToInt32Bits(value.X));
        Assert.AreEqual(123f, value.Y); Assert.AreEqual(0f, value.Vx); Assert.AreEqual(0f, value.Vy);
    }

    [TestMethod]
    public void RawValueCopiesRetainOpaqueBytesAndExcludeNativePointers()
    {
        var owner = Enumerable.Range(0, 296).Select(i => unchecked((byte)i)).ToArray();
        var body = Enumerable.Range(0, 200).Select(i => unchecked((byte)(255 - i))).ToArray();
        var ownerCopy = LoadingRigidbodyValueBits.CopyOwnerValues(owner);
        var bodyCopy = LoadingRigidbodyValueBits.CopyBodyValues(body);
        Assert.AreEqual(168, ownerCopy.Length); Assert.AreEqual(130, bodyCopy.Length);
        foreach (var offset in Enumerable.Range(120, 32).Concat(Enumerable.Range(280, 16))) owner[offset] ^= 0xff;
        foreach (var offset in Enumerable.Range(88, 56).Concat(Enumerable.Range(188, 12))) body[offset] ^= 0xff;
        CollectionAssert.AreEqual(ownerCopy, LoadingRigidbodyValueBits.CopyOwnerValues(owner));
        CollectionAssert.AreEqual(bodyCopy, LoadingRigidbodyValueBits.CopyBodyValues(body));
        owner[200] ^= 0xff; body[160] ^= 0xff;
        Assert.IsFalse(ownerCopy.SequenceEqual(LoadingRigidbodyValueBits.CopyOwnerValues(owner)));
        Assert.IsFalse(bodyCopy.SequenceEqual(LoadingRigidbodyValueBits.CopyBodyValues(body)));
    }

    [TestMethod]
    public void UnexpectedNativeRangeFailsClosed()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => LoadingRigidbodyValueBits.Decode(new byte[295], null, 0, 0));
        Assert.ThrowsExactly<InvalidOperationException>(() => LoadingRigidbodyValueBits.Decode(new byte[296], new byte[199], 0, 0));
        Assert.ThrowsExactly<InvalidOperationException>(() => LoadingRigidbodyValueBits.CopyBodyValues(new byte[201]));
    }
}
