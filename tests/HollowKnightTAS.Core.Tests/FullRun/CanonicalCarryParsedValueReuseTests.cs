using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UnityEngine;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalCarryParsedValueReuseTests
{
    private sealed class Owner
    {
        public Vector3 A, B;
        public Vector3[] Points = Array.Empty<Vector3>();
        public int I, J;
        public int[] Numbers = Array.Empty<int>();
        public byte[] Bytes = Array.Empty<byte>();
        public object? BoxA, BoxB, Alias;
    }
    private static CanonicalCarryGraphCodec Codec() => new(new[] { typeof(Owner), typeof(Vector3), typeof(Vector3[]), typeof(int[]), typeof(byte[]) }, "parsed-template-contract-v1");
    private static Dictionary<string, object> Roots(Owner value) => new() { ["owner"] = value };
    private static object NoExternal(string key) => throw new InvalidDataException(key);
    private static Owner Source(Vector3 value)
    {
        object first = 7, second = 7;
        return new Owner { A = value, B = value, Points = Enumerable.Repeat(value, 64).ToArray(), I = 7, J = 7,
            Numbers = Enumerable.Repeat(7, 64).ToArray(), Bytes = Enumerable.Repeat((byte)165, 64).ToArray(), BoxA = first, BoxB = second, Alias = first };
    }
    [TestMethod]
    public void Repeated_value_templates_preserve_raw_wire_nan_signed_zero_and_distinct_boxes()
    {
        foreach (var bits in new[] { 0, unchecked((int)0x80000000), 0x7fc12345, unchecked((int)0xffc12345), 0x7f800000, unchecked((int)0xff800000) }) {
            var codec = Codec(); var source = Source(new Vector3(BitConverter.Int32BitsToSingle(bits), -0f, 1f));
            var graph = codec.Capture(Roots(source), _ => null); var wire = codec.Encode(graph); var target = new Owner();
            var plan = codec.PrepareGraph(codec.Decode(wire), Roots(target), NoExternal, view => {
                Assert.AreNotSame(view.Field(target, typeof(Owner), nameof(Owner.A)), view.Field(target, typeof(Owner), nameof(Owner.B)));
                Assert.AreNotSame(view.Field(target, typeof(Owner), nameof(Owner.I)), view.Field(target, typeof(Owner), nameof(Owner.J)));
            });
            plan.Apply(); plan.VerifyReadback();
            Assert.AreNotSame(target.BoxA, target.BoxB); Assert.AreSame(target.BoxA, target.Alias);
            CollectionAssert.AreEqual(wire, codec.Encode(codec.Capture(Roots(target), _ => null)));
        }
    }
    [TestMethod]
    public void Prepared_inline_box_mutation_cannot_modify_another_slot_or_the_private_template()
    {
        var codec = Codec(); var source = Source(Vector3.zero); var target = new Owner();
        var plan = codec.PrepareGraph(codec.Capture(Roots(source), _ => null), Roots(target), NoExternal, view => {
            var first = view.Field(target, typeof(Owner), nameof(Owner.A));
            typeof(Vector3).GetField("x")!.SetValue(first, 9f);
        });
        plan.Apply(); plan.VerifyReadback();
        Assert.AreEqual(9f, target.A.x); Assert.AreEqual(0f, target.B.x);
        Assert.IsTrue(target.Points.All(value => value.x == 0));
    }
    [TestMethod]
    public void Unique_values_beyond_cache_bound_keep_original_results_and_late_noncanonical_values_reject()
    {
        var codec = Codec(); var source = Source(Vector3.zero);
        source.Numbers = Enumerable.Range(0, 4200).ToArray();
        var graph = codec.Capture(Roots(source), _ => null); var wire = codec.Encode(graph); var target = new Owner();
        var plan = codec.PrepareGraph(codec.Decode(wire), Roots(target), NoExternal); plan.Apply(); plan.VerifyReadback();
        CollectionAssert.AreEqual(wire, codec.Encode(codec.Capture(Roots(target), _ => null)));
        var malformed = codec.Decode(wire);
        var numbers = malformed.Nodes.Single(node => node.Kind == "array" && node.Type == CanonicalCarryGraphCodec.Identity(typeof(int[])));
        numbers.Items![1].Text = "00";
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareGraph(malformed, Roots(new Owner()), NoExternal));
    }
}
