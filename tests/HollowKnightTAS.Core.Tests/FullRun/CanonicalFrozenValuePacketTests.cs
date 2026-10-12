using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalFrozenValuePacketTests
{
    private enum Driver : ushort { Minimum = 0, Maximum = ushort.MaxValue }
    private sealed class Configuration
    {
        public float DeadZone;
        public string Name = "";
        public Configuration? Alias;
        public int Getter => throw new InvalidOperationException("Configuration getters must never run.");
    }
    private static FieldInfo[] Fields(Type type) => type == typeof(Configuration)
        ? CanonicalCarryGraphCodec.Fields(type) : throw new InvalidDataException("Unknown field owner.");
    private static byte[] Capture(params object[] roots) => CanonicalFrozenValuePacket.Capture(roots, Fields, type => {
        if (type != typeof(Configuration) && type != typeof(string) && type != typeof(float) && type != typeof(int)
            && type != typeof(ushort) && type != typeof(Driver) && !type.IsArray)
            throw new InvalidDataException("Unknown type.");
    });
    [TestMethod]
    public void Equal_configuration_in_distinct_process_owners_has_equal_full_packets_without_running_getters()
    {
        var left = new Configuration { Name = "profile", DeadZone = 0.25f }; left.Alias = left;
        var right = new Configuration { Name = "profile", DeadZone = 0.25f }; right.Alias = right;
        CollectionAssert.AreEqual(Capture(left), Capture(right));
        right.DeadZone = 0.5f; CollectionAssert.AreNotEqual(Capture(left), Capture(right));
    }
    [TestMethod]
    public void Shared_configuration_aliases_and_cycles_are_not_replaced_by_equal_clones()
    {
        var shared = new Configuration { Name = "shared" }; var equal = new Configuration { Name = "shared" };
        CollectionAssert.AreNotEqual(Capture(new object[] { shared, shared }), Capture(new object[] { shared, equal }));
        shared.Alias = shared; var owners = new List<object>();
        CanonicalFrozenValuePacket.Capture(new object[] { shared }, Fields, _ => { }, owners.Add);
        Assert.AreEqual(2, owners.Count); Assert.AreSame(shared, owners[0]); Assert.AreSame(shared.Name, owners[1]);
    }
    [TestMethod]
    public void Float_bits_and_utf16_code_units_are_preserved_including_negative_zero_and_surrogates()
    {
        var left = new Configuration { Name = "\ud800", DeadZone = 0f };
        var right = new Configuration { Name = "\ufffd", DeadZone = 0f };
        CollectionAssert.AreNotEqual(Capture(left), Capture(right));
        right.Name = left.Name; right.DeadZone = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
        CollectionAssert.AreNotEqual(Capture(left), Capture(right));
        left.DeadZone = BitConverter.Int32BitsToSingle(unchecked((int)0x7fc00001));
        right.DeadZone = BitConverter.Int32BitsToSingle(unchecked((int)0x7fc00002));
        CollectionAssert.AreNotEqual(Capture(left), Capture(right));
    }
    [TestMethod]
    public void Same_values_with_replaced_nested_owners_are_detectable_by_the_reference_ledger()
    {
        var nested = new Configuration(); var root = new Configuration { Alias = nested };
        var owners = new List<object>(); var before = CanonicalFrozenValuePacket.Capture(new object[] { root }, Fields, _ => { }, owners.Add);
        root.Alias = new Configuration(); var current = new List<object>();
        var after = CanonicalFrozenValuePacket.Capture(new object[] { root }, Fields, _ => { }, current.Add);
        CollectionAssert.AreEqual(before, after); Assert.AreEqual(owners.Count, current.Count);
        Assert.IsFalse(ReferenceEquals(owners[1], current[1]));
    }
    [TestMethod]
    public void Unknown_types_and_noncanonical_array_shapes_are_rejected()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => Capture(new object()));
        Assert.ThrowsExactly<InvalidDataException>(() => Capture(new int[1, 1]));
        Assert.ThrowsExactly<InvalidDataException>(() => Capture(Array.CreateInstance(typeof(int), new[] { 1 }, new[] { 2 })));
    }
    [TestMethod]
    public void Configuration_cannot_expand_the_existing_input_packet_budget()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => Capture(new Configuration { Name = new string('x', 524289) }));
        Assert.ThrowsExactly<InvalidDataException>(() => Capture(new int[20001]));
        Assert.ThrowsExactly<InvalidDataException>(() => Capture(new object[4097]));
    }
    [TestMethod]
    public void Original_unsigned_short_enum_storage_preserves_all_raw_values()
    {
        CollectionAssert.AreEqual(Capture(Driver.Maximum), Capture((Driver)ushort.MaxValue));
        CollectionAssert.AreNotEqual(Capture(Driver.Minimum), Capture(Driver.Maximum));
        CollectionAssert.AreNotEqual(Capture((Driver)1234), Capture((ushort)1234));
    }
}
