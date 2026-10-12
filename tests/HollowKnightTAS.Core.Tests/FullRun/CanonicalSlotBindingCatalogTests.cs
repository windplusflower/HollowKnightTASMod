using System;
using System.IO;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalSlotBindingCatalogTests
{
    [TestMethod]
    public void Registers_reference_identity_without_calling_game_equality()
    {
        var first = new EqualHandle(); var second = new EqualHandle(); var slots = new CanonicalSlotBindingCatalog();
        slots.Add("state/0", first); slots.Add("state/1", second); slots.Seal();
        Assert.AreEqual("state/0", slots.Bind(first)); Assert.AreEqual("state/1", slots.Bind(second));
        Assert.AreSame(first, slots.Resolve("state/0")); Assert.AreSame(second, slots.Resolve("state/1"));
        Assert.AreEqual(2, slots.Count);
    }

    [TestMethod]
    public void Rejects_ambiguous_keys_and_unimplemented_multi_slot_aliases_before_mutation()
    {
        var first = new object(); var slots = new CanonicalSlotBindingCatalog(); slots.Add("state/0", first);
        Assert.ThrowsExactly<InvalidDataException>(() => slots.Add("state/0", new object()));
        Assert.ThrowsExactly<InvalidDataException>(() => slots.Add("state/1", first));
        Assert.AreEqual(1, slots.Count); slots.Seal(); Assert.AreSame(first, slots.Resolve("state/0"));
    }

    [TestMethod]
    public void Only_sealed_local_slots_can_resolve_and_unknown_slots_are_never_guessed()
    {
        var slots = new CanonicalSlotBindingCatalog(); var owner = new object(); slots.Add("state/0:Init", owner);
        Assert.ThrowsExactly<InvalidDataException>(() => slots.Bind(owner));
        Assert.ThrowsExactly<InvalidDataException>(() => slots.Resolve("state/0:Init"));
        slots.Seal(); Assert.IsNull(slots.Bind(new object()));
        Assert.ThrowsExactly<InvalidDataException>(() => slots.Resolve("state/0:init"));
        Assert.ThrowsExactly<InvalidDataException>(() => slots.Add("state/1", new object()));
        Assert.ThrowsExactly<InvalidDataException>(() => slots.Seal());
    }

    [TestMethod]
    public void Rejects_invalid_keys_values_and_oversized_catalog_without_partial_registration()
    {
        var slots = new CanonicalSlotBindingCatalog();
        Assert.ThrowsExactly<InvalidDataException>(() => slots.Add("", new object()));
        Assert.ThrowsExactly<InvalidDataException>(() => slots.Add("a\0b", new object()));
        Assert.ThrowsExactly<InvalidDataException>(() => slots.Add(new string('x', 16385), new object()));
        Assert.ThrowsExactly<InvalidDataException>(() => slots.Add("value", 1));
        Assert.AreEqual(0, slots.Count);
        for (var index = 0; index < CanonicalSlotBindingCatalog.MaximumBindings; index++) slots.Add(index.ToString(), new object());
        Assert.ThrowsExactly<InvalidDataException>(() => slots.Add("overflow", new object()));
        Assert.AreEqual(CanonicalSlotBindingCatalog.MaximumBindings, slots.Count);
    }

    private sealed class EqualHandle
    {
        public override bool Equals(object? obj) => throw new InvalidOperationException("Game equality must not be invoked.");
        public override int GetHashCode() => throw new InvalidOperationException("Game hash must not be invoked.");
    }
}
