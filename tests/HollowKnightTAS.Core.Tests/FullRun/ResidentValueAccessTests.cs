using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class ResidentValueAccessTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private enum Unsigned : ulong { Zero, Max = ulong.MaxValue }
    private readonly struct Inner
    {
        public readonly object? Alias;
        public readonly int Number;
        public readonly float Special;
        public Inner(object? alias, int number, float special = 0) { Alias = alias; Number = number; Special = special; }
    }
    private readonly struct Outer
    {
        public readonly Inner Value;
        public readonly Unsigned Mode;
        public Outer(Inner value, Unsigned mode) { Value = value; Mode = mode; }
    }
    private sealed class Owner
    {
        public readonly Outer Value;
        public Outer? Maybe, Empty;
        public Outer[] Items = Array.Empty<Outer>();
        public Outer?[] OptionalItems = Array.Empty<Outer?>();
        public Unsigned Mode;
        public Unsigned[] Modes = Array.Empty<Unsigned>();
        public object? Alias, BoxA, BoxB, BoxC;
        public Owner(Outer value) { Value = value; }
    }
    private sealed class GenericOwner<T> { public T Value = default!; }
    private static FieldInfo Field(Type type, string name) => type.GetField(name, Fields)!;
    private static Dictionary<string, object> Roots(Owner owner) => new() { ["owner"] = owner };
    private static object NoExternal(string key) => throw new InvalidDataException("Unexpected binding: " + key);
    [TestMethod]
    public void NestedReadonlyValueViewsKeepReferenceAliasesAndCopyEveryField()
    {
        var alias = new object(); var value = new Outer(new Inner(alias, 71, BitConverter.Int32BitsToSingle(unchecked((int)0x80000000))), Unsigned.Max);
        var source = new Owner(value); var target = new Owner(default);
        var view = ResidentValueAccess.ReadField(source, Field(typeof(Owner), nameof(Owner.Value)))!;
        Assert.IsTrue(ResidentValueAccess.IsView(view)); Assert.AreEqual(typeof(Outer), ResidentValueAccess.TypeOf(view));
        var nested = ResidentValueAccess.ReadField(view, Field(typeof(Outer), nameof(Outer.Value)))!;
        Assert.AreSame(alias, ResidentValueAccess.ReadField(nested, Field(typeof(Inner), nameof(Inner.Alias))));
        ResidentValueAccess.WriteField(target, Field(typeof(Owner), nameof(Owner.Value)), view);
        Assert.AreSame(alias, target.Value.Value.Alias); Assert.AreEqual(71, target.Value.Value.Number);
        Assert.AreEqual(unchecked((int)0x80000000), BitConverter.SingleToInt32Bits(target.Value.Value.Special)); Assert.AreEqual(Unsigned.Max, target.Value.Mode);
    }
    [TestMethod]
    public void FreshReadsNeverCacheObservedFieldsAndEarlierViewsRemainIndependent()
    {
        var source = new GenericOwner<Inner> { Value = new Inner(new object(), 31) }; var field = Field(source.GetType(), "Value");
        var first = ResidentValueAccess.ReadField(source, field)!; source.Value = new Inner(new object(), 83);
        var second = ResidentValueAccess.ReadField(source, field)!;
        Assert.AreEqual(31, ResidentValueAccess.ReadField(first, Field(typeof(Inner), "Number")));
        Assert.AreEqual(83, ResidentValueAccess.ReadField(second, Field(typeof(Inner), "Number")));
        Assert.AreNotSame(ResidentValueAccess.ReadField(first, Field(typeof(Inner), "Alias")), ResidentValueAccess.ReadField(second, Field(typeof(Inner), "Alias")));
    }
    [TestMethod]
    public void ClosedGenericReferenceOwnersUseTheirActualFieldLayouts()
    {
        var numeric = new GenericOwner<int> { Value = 91 }; var reference = new GenericOwner<object> { Value = new object() }; var composite = new GenericOwner<Inner> { Value = new Inner(reference.Value, 103) };
        for (var repeat = 0; repeat < 3; repeat++) {
            Assert.AreEqual(91, ResidentValueAccess.ReadField(numeric, Field(numeric.GetType(), "Value")));
            Assert.AreSame(reference.Value, ResidentValueAccess.ReadField(reference, Field(reference.GetType(), "Value")));
            var value = ResidentValueAccess.ReadField(composite, Field(composite.GetType(), "Value"))!;
            Assert.AreSame(reference.Value, ResidentValueAccess.ReadField(value, Field(typeof(Inner), "Alias")));
        }
    }
    [TestMethod]
    public void EnumFieldsAndArraysUseExactUnderlyingUnsignedBits()
    {
        var source = new Owner(default) { Mode = Unsigned.Max, Modes = new[] { Unsigned.Max } }; var target = new Owner(default) { Modes = new Unsigned[1] };
        var value = ResidentValueAccess.ReadField(source, Field(typeof(Owner), "Mode"))!;
        Assert.AreEqual(typeof(Unsigned), ResidentValueAccess.TypeOf(value)); Assert.AreEqual(ulong.MaxValue, ResidentValueAccess.Scalar(value));
        ResidentValueAccess.WriteField(target, Field(typeof(Owner), "Mode"), value); Assert.AreEqual(Unsigned.Max, target.Mode);
        ResidentValueAccess.WriteArray(target.Modes, 0, ResidentValueAccess.ReadArray(source.Modes, 0)); Assert.AreEqual(Unsigned.Max, target.Modes[0]);
    }
    [TestMethod]
    public void NullableFieldsAndStructArraysPreserveNormalBoxingWireSemantics()
    {
        var alias = new object(); var data = new Outer(new Inner(alias, 57), Unsigned.Max);
        var source = new Owner(default) { Maybe = data, Items = new[] { data }, OptionalItems = new Outer?[] { data, null } };
        var target = new Owner(default) { Items = new Outer[1], OptionalItems = new Outer?[2] };
        var maybe = ResidentValueAccess.ReadField(source, Field(typeof(Owner), "Maybe"))!;
        Assert.AreEqual(typeof(Outer), ResidentValueAccess.TypeOf(maybe)); Assert.IsNull(ResidentValueAccess.ReadField(source, Field(typeof(Owner), "Empty")));
        ResidentValueAccess.WriteField(target, Field(typeof(Owner), "Maybe"), maybe); ResidentValueAccess.WriteField(target, Field(typeof(Owner), "Empty"), null);
        ResidentValueAccess.WriteArray(target.Items, 0, ResidentValueAccess.ReadArray(source.Items, 0));
        for (var index = 0; index < 2; index++) ResidentValueAccess.WriteArray(target.OptionalItems, index, ResidentValueAccess.ReadArray(source.OptionalItems, index));
        Assert.AreSame(alias, target.Maybe!.Value.Value.Alias); Assert.IsFalse(target.Empty.HasValue); Assert.AreSame(alias, target.Items[0].Value.Alias);
        Assert.AreSame(alias, target.OptionalItems[0]!.Value.Value.Alias); Assert.IsFalse(target.OptionalItems[1].HasValue);
    }
    [TestMethod]
    public void NullableFalseNondefaultHiddenValueFailsWithoutMutatingSource()
    {
        var source = new Owner(default); var nullable = typeof(Outer?); var value = nullable.GetFields(Fields).Single(field => field.Name.Equals("value", StringComparison.OrdinalIgnoreCase));
        var dirty = new DynamicMethod("test_dirty_nullable", typeof(void), new[] { typeof(Owner) }, typeof(ResidentValueAccessTests).Module, true);
        var il = dirty.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldflda, Field(typeof(Owner), "Empty")); il.Emit(OpCodes.Ldflda, value);
        il.Emit(OpCodes.Ldflda, Field(typeof(Outer), "Value")); il.Emit(OpCodes.Ldc_I4, 123); il.Emit(OpCodes.Stfld, Field(typeof(Inner), "Number")); il.Emit(OpCodes.Ret);
        ((Action<Owner>)dirty.CreateDelegate(typeof(Action<Owner>)))(source);
        Assert.IsFalse(source.Empty.HasValue); Assert.AreEqual(123, source.Empty.GetValueOrDefault().Value.Number);
        Assert.ThrowsExactly<InvalidDataException>(() => ResidentValueAccess.ReadField(source, Field(typeof(Owner), "Empty")));
        Assert.IsFalse(source.Empty.HasValue); Assert.AreEqual(123, source.Empty.GetValueOrDefault().Value.Number);
    }
    [TestMethod]
    public void TypedFromValueTransfersCompositeStorageWithoutCreatingAnotherProtocol()
    {
        var alias = new object(); var source = new Inner(alias, 133); var view = ResidentValueAccess.FromValue(source);
        Assert.AreEqual(typeof(Inner), ResidentValueAccess.TypeOf(view)); Assert.AreSame(alias, ResidentValueAccess.ReadField(view, Field(typeof(Inner), "Alias")));
        Assert.AreEqual(133, ResidentValueAccess.ReadField(view, Field(typeof(Inner), "Number")));
        var restored = ResidentValueAccess.ToValue<Inner>(view); Assert.AreSame(alias, restored.Alias); Assert.AreEqual(133, restored.Number);
        var owner = new Owner(default) { Mode = Unsigned.Max };
        Assert.AreEqual(Unsigned.Max, ResidentValueAccess.ToValue<Unsigned>(ResidentValueAccess.ReadField(owner, Field(typeof(Owner), "Mode"))));
        Assert.IsFalse(ResidentValueAccess.ToValue<Outer?>(null).HasValue);
    }
    [TestMethod]
    public void ResidentGraphRoundtripRetainsValueCopiesBoxedAliasesNullableAndExactWire()
    {
        var alias = new object(); var data = new Outer(new Inner(alias, 71, BitConverter.Int32BitsToSingle(unchecked((int)0x80000000))), Unsigned.Max);
        object box = new Inner(alias, 59); var source = new Owner(data) { Alias = alias, Maybe = data, Items = new[] { data, data }, OptionalItems = new Outer?[] { data, null },
            Mode = Unsigned.Max, Modes = new[] { Unsigned.Max }, BoxA = box, BoxB = box, BoxC = new Inner(alias, 59) };
        var policy = new ResidentGraphPolicy(_ => null); var roots = Roots(source);
        var catalog = ResidentManagedTypeCatalog.Capture(roots, policy, _ => null); var codec = new CanonicalCarryGraphCodec(catalog.Types, catalog.SchemaIdentity, residentPolicy: policy);
        var graph = codec.Capture(roots, _ => null); Assert.AreEqual(0, graph.Unsupported.Length); var bytes = codec.Encode(graph);
        var target = new Owner(default); var plan = codec.PrepareOwnedGraph(codec.Decode(bytes), Roots(target), NoExternal); plan.Apply(); plan.VerifyReadback();
        Assert.AreSame(target.BoxA, target.BoxB); Assert.AreNotSame(target.BoxA, target.BoxC); Assert.AreSame(target.Alias, ((Inner)target.BoxA!).Alias);
        Assert.AreSame(target.Alias, target.Value.Value.Alias); Assert.AreSame(target.Alias, target.Items[1].Value.Alias); Assert.AreSame(target.Alias, target.Maybe!.Value.Value.Alias);
        CollectionAssert.AreEqual(bytes, codec.Encode(codec.Capture(Roots(target), _ => null)));
    }
}
