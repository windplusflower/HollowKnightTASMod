using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UnityEngine;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalVector2RayCarryTests
{
    private sealed class Rays
    {
        public List<Vector2>? Bottom, Left, Right, Top;
        public Vector2[]? OutsideArray;
    }

    [TestMethod]
    public void FourRayCachesPreserveListAndArrayAliasesDormantCapacityVersionAndFloatBits()
    {
        var items = typeof(List<Vector2>).GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var version = typeof(List<Vector2>).GetField("_version", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var bottom = new List<Vector2>(7) { new(1, 2), new(3, 4) };
        var backing = (Vector2[])items.GetValue(bottom)!;
        backing[6] = new Vector2(BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)),
            BitConverter.Int32BitsToSingle(unchecked((int)0x7fc00137)));
        var right = new List<Vector2> { new(5, 6) };
        items.SetValue(right, backing);
        version.SetValue(bottom, int.MaxValue); version.SetValue(right, -7);
        var source = new Rays { Bottom = bottom, Left = bottom, Right = right, Top = new List<Vector2>(), OutsideArray = backing };
        var target = new Rays();
        var codec = new CanonicalCarryGraphCodec(new[] { typeof(Rays), typeof(List<Vector2>), typeof(Vector2[]), typeof(Vector2) }, "vector2-ray-cache-contract-v1");
        Dictionary<string, object> Roots(Rays value) => new() { ["rays"] = value };
        object NoBinding(string key) => throw new InvalidDataException(key);
        var graph = codec.Capture(Roots(source), _ => null);
        Assert.AreEqual(0, graph.Unsupported.Length);
        var bytes = codec.Encode(graph);
        var plan = codec.PrepareGraph(codec.Decode(bytes), Roots(target), NoBinding);
        plan.Apply(); plan.VerifyReadback();
        Assert.AreSame(target.Bottom, target.Left);
        Assert.AreNotSame(target.Bottom, target.Right);
        Assert.AreSame(items.GetValue(target.Bottom!), items.GetValue(target.Right!));
        Assert.AreSame(target.OutsideArray, items.GetValue(target.Bottom!));
        Assert.AreEqual(2, target.Bottom!.Count); Assert.AreEqual(1, target.Right!.Count);
        Assert.AreEqual(7, target.Bottom.Capacity); Assert.AreEqual(7, target.Right.Capacity);
        Assert.AreEqual(int.MaxValue, version.GetValue(target.Bottom)); Assert.AreEqual(-7, version.GetValue(target.Right));
        Assert.AreEqual(unchecked((int)0x80000000), BitConverter.SingleToInt32Bits(target.OutsideArray![6].x));
        Assert.AreEqual(unchecked((int)0x7fc00137), BitConverter.SingleToInt32Bits(target.OutsideArray[6].y));
        Assert.AreSame(items.GetValue(source.Top!), items.GetValue(target.Top!));
        CollectionAssert.AreEqual(bytes, codec.Encode(codec.Capture(Roots(target), _ => null)));
    }
}
