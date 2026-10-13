using System;
using System.Linq;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalPolygonCapacityTests
{
    private static byte[] Shape(int count)
    {
        var bytes = Enumerable.Repeat((byte)0x42, 160).ToArray();
        Array.Copy(BitConverter.GetBytes(2),0,bytes,8,4);
        Array.Copy(BitConverter.GetBytes(count),0,bytes,152,4);
        return bytes;
    }
    [TestMethod]
    public void OnlyInactiveVertexAndNormalSlotsChange()
    {
        foreach (var count in new[] { 3,4,8 })
        {
            var before = Shape(count); var source = (byte[])before.Clone();
            var after = CanonicalPolygonCapacity.Prepare(source);
            CollectionAssert.AreEqual(before, source);
            for (var i=0; i<160; i++)
            {
                var unused = i>=24+count*8 && i<88 || i>=88+count*8 && i<152;
                Assert.AreEqual(unused ? (byte)0 : before[i], after[i], "byte " + i);
            }
        }
    }
    [TestMethod]
    public void InvalidShapeAndActiveNaNRejectWithoutChangingInput()
    {
        foreach (var count in new[] { -1,0,2,9 })
            Assert.ThrowsExactly<System.IO.InvalidDataException>(() => CanonicalPolygonCapacity.Prepare(Shape(count)));
        var invalid = Shape(4); Array.Copy(BitConverter.GetBytes(float.NaN),0,invalid,24,4);
        var saved = (byte[])invalid.Clone();
        Assert.ThrowsExactly<System.IO.InvalidDataException>(() => CanonicalPolygonCapacity.Prepare(invalid));
        CollectionAssert.AreEqual(saved, invalid);
        Assert.ThrowsExactly<System.IO.InvalidDataException>(() => CanonicalPolygonCapacity.Prepare(new byte[159]));
    }
}
