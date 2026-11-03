using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using UnityEngine;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentManagedNativeWrappersTests
{
    private sealed class Holder { public object? First, Second; public Pair Nested; }
    private struct Pair { public object? First, Second; }
    private sealed class CurveWithState : AnimationCurve { public object? Link; public int Marker; }
    private sealed class CurveWithPointer : AnimationCurve { public IntPtr OtherNative = IntPtr.Zero; }
    private sealed class CurveWithInlineState : AnimationCurve { public Pair Inline; }

    [TestMethod]
    public void DiscoveryKeepsManagedAliasesDistinctFromSharedNativePointers()
    {
        var api = new Api(); var first = Wrapper<CurveWithState>(101); var second = Wrapper<AnimationCurve>(101); var gradient = Wrapper<Gradient>(202);
        first.Link = gradient; first.Marker = 43;
        var root = new Holder { First = first, Second = first, Nested = new Pair { First = second, Second = gradient } };
        var found = api.Discover(new Dictionary<string, object> { ["any-game-owner"] = root });
        var wrappers = (object[])Get(found, "Wrappers"); var pointers = (IntPtr[])Get(found, "Pointers");
        Assert.AreEqual(3, wrappers.Length); Assert.AreEqual(1, wrappers.Count(value => ReferenceEquals(value, first)));
        Assert.IsTrue(wrappers.Contains(second)); Assert.IsTrue(wrappers.Contains(gradient));
        Assert.AreEqual(2, pointers.Count(value => value == new IntPtr(101)), "Native aliases must not collapse distinct managed wrapper identities.");
        CollectionAssert.AreEquivalent(new[] { 1, 1, 2 }, (int[])Get(found, "Kinds"));
    }

    [TestMethod]
    public void ExecutionBindingsStopTraversalButWrapperBindingsRemainDiscoverable()
    {
        var api = new Api(); var curve = Wrapper<AnimationCurve>(0); var control = new Holder { First = curve };
        var roots = new Dictionary<string, object> { ["control"] = control };
        var hidden = api.Discover(roots, value => ReferenceEquals(value, control) ? "resident-execution/current-driver" : null);
        Assert.AreEqual(0, ((Array)Get(hidden, "Wrappers")).Length);
        var direct = api.Discover(new Dictionary<string, object> { ["root"] = new Holder { First = curve } },
            value => ReferenceEquals(value, curve) ? "resident-native-wrapper/object/000000" : null);
        Assert.AreSame(curve, ((object[])Get(direct, "Wrappers"))[0]); Assert.AreEqual(IntPtr.Zero, ((IntPtr[])Get(direct, "Pointers"))[0]);
    }

    [TestMethod]
    public void DiscoveryTraversesStructArraysAndBoxedCyclesWithoutExposingValueViewsAsOwners()
    {
        var api = new Api(); var curve = Wrapper<AnimationCurve>(0); object boxed = new Pair { First = curve };
        typeof(Pair).GetField(nameof(Pair.Second))!.SetValue(boxed, boxed);
        var root = new Holder { First = boxed, Second = new[] { new Pair { First = curve, Second = boxed } } };
        var access = api.Runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentValueAccess", true)!;
        var isView = access.GetMethod("IsView", BindingFlags.Static | BindingFlags.NonPublic)!;
        var found = api.Discover(new Dictionary<string, object> { ["root"] = root }, value => {
            Assert.IsFalse((bool)isView.Invoke(null, new[] { value })!, "A by-value view cannot acquire external/native owner identity."); return null;
        });
        var wrappers = (object[])Get(found, "Wrappers"); Assert.AreEqual(1, wrappers.Length); Assert.AreSame(curve, wrappers[0]);
    }

    [TestMethod]
    public void WrapperFieldSlotsKeepCompositeValueViewsAndReferenceAliasesAcrossReadback()
    {
        var api = new Api(); var alias = new object(); var curve = Wrapper<CurveWithInlineState>(0);
        curve.Inline = new Pair { First = alias, Second = alias };
        var owner = api.Runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentManagedNativeWrappers", true)!;
        var row = api.Rows("ManagedField").GetType().GetElementType()!;
        var descriptor = Activator.CreateInstance(row, true)!; var field = typeof(CurveWithInlineState).GetField(nameof(CurveWithInlineState.Inline))!;
        var slot = Activator.CreateInstance(owner.GetNestedType("FieldSlot", All)!, All, null, new object[] { descriptor, field, curve }, null)!;
        var access = api.Runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentValueAccess", true)!; var read = access.GetMethod("ReadField", All)!;
        var original = Get(slot, "Original"); var storage = Get(slot, "Slot"); var valueField = (FieldInfo)Get(slot, "Value");
        var copied = read.Invoke(null, new object[] { storage, valueField });
        Assert.IsTrue((bool)owner.GetMethod("SameSlot", All)!.Invoke(null, new[] { original, copied, typeof(Pair) })!);
        var first = read.Invoke(null, new object[] { copied!, typeof(Pair).GetField(nameof(Pair.First))! });
        var second = read.Invoke(null, new object[] { copied!, typeof(Pair).GetField(nameof(Pair.Second))! });
        Assert.AreSame(alias, first); Assert.AreSame(first, second);
        curve.Inline = new Pair { First = alias, Second = new object() };
        var changed = read.Invoke(null, new object[] { curve, field });
        Assert.IsFalse((bool)owner.GetMethod("SameSlot", All)!.Invoke(null, new[] { original, changed, typeof(Pair) })!);
    }

    [TestMethod]
    public void SubclassFieldsStayInTheUnifiedGraphAndNativePointerOwnershipCannotBeHidden()
    {
        var api = new Api(); var row = api.Describe(typeof(CurveWithState), 0, ""); var fields = ((Array)Get(row, "Fields")).Cast<object>().ToArray();
        Assert.AreEqual(3, fields.Length); Assert.AreEqual(1, fields.Count(field => (string)Get(field, "Storage") == "native-owner"));
        Assert.AreEqual(2, fields.Count(field => (string)Get(field, "Storage") == "D"));
        Assert.IsTrue(fields.Where(field => (string)Get(field, "Storage") == "D").All(field => ((string)Get(field, "Root")).Length != 0));
        var data = api.Data(); Set(data, "Wrappers", api.Rows("Wrapper", row)); api.Validate(data);
        var nativePointer = fields.Single(field => (string)Get(field, "Storage") == "native-owner"); Set(nativePointer, "Storage", "D"); api.RejectValidate(data);

        var unknown = api.Data(); var pointerRow = api.Describe(typeof(CurveWithPointer), 0, ""); Set(unknown, "Wrappers", api.Rows("Wrapper", pointerRow)); api.RejectValidate(unknown);
        Set(unknown, "CoverageGaps", new[] { "managed-wrapper-pointer-field:resident-native-wrapper/object/000000/OtherNative" });
        api.Validate(unknown); api.Reject(() => api.Static("RequireCompleteCoverage", unknown));
    }

    [TestMethod]
    public void CurveCanonChangesOnlyStorageDependencyAndPreservesEveryCacheAndKeyBit()
    {
        var api = new Api(); var curve = api.Curve(2, 7); var caches = Enumerable.Range(0, 56).Select(index => (byte)(index * 3)).ToArray();
        Array.Clear(caches, 0, 4); Array.Clear(caches, 28, 4);
        var keys = Enumerable.Range(0, 56).Select(index => (byte)(255 - index)).ToArray(); Set(curve, "Caches", caches); Set(curve, "Keys", keys);
        Set(curve, "PreInfinity", 0); Set(curve, "PostInfinity", 3); Set(curve, "RotationOrder", 5);
        var data = api.WithNative(typeof(AnimationCurve), 1, curve); Set(data, "CoverageGaps", new[] { "curve-storage-capacity:resident-native-wrapper/native/000000" }); api.Validate(data);
        var normalized = api.Static("NormalizeStorage", data); api.Static("RequireCompleteCoverage", normalized);
        api.Static("RequireCanonicalState", normalized);
        var result = Get(((Array)Get(normalized, "Natives")).GetValue(0)!, "Curve");
        Assert.AreEqual(2, Get(result, "Capacity")); CollectionAssert.AreEqual(caches, (byte[])Get(result, "Caches")); CollectionAssert.AreEqual(keys, (byte[])Get(result, "Keys"));
        Assert.AreEqual(0, Get(result, "PreInfinity")); Assert.AreEqual(3, Get(result, "PostInfinity")); Assert.AreEqual(5, Get(result, "RotationOrder")); Assert.AreEqual(7, Get(curve, "Capacity"));
        var bad = api.Curve(1, 1); Set(bad, "Caches", new byte[55]); api.Reject(() => api.Static("ValidateCurve", bad));
        bad = api.Curve(1, 1); Set(bad, "Keys", new byte[29]); api.Reject(() => api.Static("ValidateCurve", bad));
        bad = api.Curve(1, 1); Set(bad, "PreInfinity", 4); api.Reject(() => api.Static("ValidateCurve", bad));
        bad = api.Curve(1, 1); var badIndex = (byte[])Get(bad, "Caches"); BitConverter.GetBytes(1).CopyTo(badIndex, 28);
        api.Reject(() => api.Static("ValidateCurve", bad));
    }

    [TestMethod]
    public void LoopCurveCacheIsSemanticHistoryBecauseColdRecomputationChangesTheNextFloatBits()
    {
        // Pinned UnityPlayer Evaluate 73cb63..73cb95 uses the previous cache
        // start; 73cc1c..73ccb1 computes a new loop offset on a cache miss;
        // CalculateCacheData 73b750 adds that offset to each key time.
        // Every helper forces the corresponding scalar SSE rounding boundary.
        var first = Float(0x40b1f034); var last = Float(0x40be1cc1);
        var previousTime = Float(0x421bf9c2); var nextTime = Float(0x421bca6d);
        var warm = LoopInterval(previousTime, first, last); var cold = LoopInterval(nextTime, first, last);
        Assert.IsTrue(nextTime >= warm.Start && nextTime < warm.End);
        Assert.AreEqual(0x421aa282, Bits(warm.Start)); Assert.AreEqual(0x421aa284, Bits(cold.Start));
        // Key values are 0 and duration, with both tangents 1 and no weights.
        // Exact instruction order gives (a,b,c,d)=(0,0,1,0).
        var duration = Sub(last, first); var reciprocalSquare = Div(1, Mul(duration, duration));
        var a = Div(Mul(Sub(Sub(Add(duration, duration), duration), duration), reciprocalSquare), duration);
        var b = Mul(Sub(Sub(Sub(Add(Add(duration, duration), duration), duration), duration), duration), reciprocalSquare);
        Assert.AreEqual(0, Bits(a)); Assert.AreEqual(0, Bits(b));
        var warmOutput = Polynomial(Sub(nextTime, warm.Start), a, b, 1, 0);
        var coldOutput = Polynomial(Sub(nextTime, cold.Start), a, b, 1, 0);
        Assert.AreEqual(0x3e93f580, Bits(warmOutput)); Assert.AreEqual(0x3e93f480, Bits(coldOutput));
        Assert.AreNotEqual(Bits(warmOutput), Bits(coldOutput), "Cold cache replacement changes gameplay-visible float bits even for finite unweighted keys.");
    }

    [TestMethod]
    public void CanonicalStorageHasTypedAllocatorCountsFlagsAndPaddingWithoutReplacingCurveD()
    {
        var api = new Api(); var curve = api.Curve(2, 5); var padding = Enumerable.Repeat((byte)0xa7, 8).ToArray();
        Set(curve, "StoragePadding", padding); Set(curve, "KeyMemoryLabel", 0x39);
        var data = api.WithNative(typeof(AnimationCurve), 1, curve);
        Set(data, "CoverageGaps", new[] { "curve-storage-capacity:resident-native-wrapper/native/000000" });
        api.Validate(data); api.Reject(() => api.Static("RequireCanonicalState", data));
        var normalized = api.Static("NormalizeStorage", data); api.Static("RequireCanonicalState", normalized);
        var actual = Get(((Array)Get(normalized, "Natives")).GetValue(0)!, "Curve");
        Assert.AreEqual(2, Get(actual, "KeyCount")); Assert.AreEqual(2, Get(actual, "Capacity")); Assert.AreEqual(0, Get(actual, "KeyStorageFlags"));
        Assert.AreEqual(0x4a, Get(actual, "KeyMemoryLabel")); Assert.AreEqual(0, ((byte[])Get(actual, "UnusedKeys")).Length);
        CollectionAssert.AreEqual(new byte[8], (byte[])Get(actual, "StoragePadding")); CollectionAssert.AreEqual(padding, (byte[])Get(curve, "StoragePadding"));
        Set(actual, "KeyCount", 1); api.RejectValidate(normalized); Set(actual, "KeyCount", 2);
        Set(actual, "KeyMemoryLabel", 0x39); api.Validate(normalized); api.Reject(() => api.Static("RequireCanonicalState", normalized));
        api.Static("RequireCanonicalState", api.Static("NormalizeStorage", normalized));
        Set(actual, "KeyStorageFlags", 1); api.RejectValidate(normalized);
        Set(normalized, "CoverageGaps", new[] { "curve-borrowed-key-storage:resident-native-wrapper/native/000000" });
        api.Validate(normalized); api.Reject(() => api.Static("RequireCanonicalState", api.Static("NormalizeStorage", normalized)));

        var foreign = api.WithNative(typeof(AnimationCurve), 1, api.Curve(0, 0));
        Set(foreign, "CoverageGaps", new[] { "curve-storage-capacity:resident-native-wrapper/native/999999" });
        api.Reject(() => api.Static("RequireCanonicalState", api.Static("NormalizeStorage", foreign)));
    }

    [TestMethod]
    public void SheetAliasesPreserveReferenceOwnershipWhileCurveAliasesRemainUnqualified()
    {
        var api = new Api(); var data = api.Data(); var native = api.Row("Native"); Set(native, "Key", "resident-native-wrapper/native/000000"); Set(native, "Kind", 0); Set(native, "References", 3);
        Set(native, "Sheet", Activator.CreateInstance(api.Runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentShaderPropertySheet+Data", true)!, true)!);
        Set(native, "SheetStorage", api.Row("SheetStorageData"));
        Set(data, "Natives", api.Rows("Native", native)); Set(data, "Wrappers", api.Rows("Wrapper", api.Describe(typeof(MaterialPropertyBlock), 0, "resident-native-wrapper/native/000000"), api.Describe(typeof(MaterialPropertyBlock), 1, "resident-native-wrapper/native/000000")));
        api.RejectValidate(data); Set(data, "CoverageGaps", new[] { "property-sheet-shared-native-references:resident-native-wrapper/native/000000" }); api.Validate(data);
        var normalized = api.Static("NormalizeStorage", data); api.Static("RequireCompleteCoverage", normalized);
        Assert.AreEqual(2, Get(((Array)Get(normalized, "Natives")).GetValue(0)!, "References")); Assert.AreEqual(2, ((Array)Get(normalized, "Wrappers")).Length);

        var curves = api.WithNative(typeof(AnimationCurve), 1, api.Curve(0, 0)); var curveNative = ((Array)Get(curves, "Natives")).GetValue(0)!;
        Set(curveNative, "References", 2); Set(curves, "Wrappers", api.Rows("Wrapper", api.Describe(typeof(AnimationCurve), 0, "resident-native-wrapper/native/000000"), api.Describe(typeof(AnimationCurve), 1, "resident-native-wrapper/native/000000")));
        api.RejectValidate(curves); Set(curves, "CoverageGaps", new[] { "unowned-native-wrapper-alias:resident-native-wrapper/native/000000" }); api.Validate(curves);
        var stillUnqualified = api.Static("NormalizeStorage", curves); api.Reject(() => api.Static("RequireCompleteCoverage", stillUnqualified));
    }

    [TestMethod]
    public void GradientIncludesAllEightSlotsAndRejectsTruncatedOrOrphanedNativeData()
    {
        var api = new Api(); var gradient = api.Row("GradientData"); Set(gradient, "Keys", Enumerable.Range(0, 128).Select(index => (byte)index).ToArray());
        Set(gradient, "ColorTimes", new byte[16]); Set(gradient, "AlphaTimes", new byte[16]); Set(gradient, "ColorCount", 2); Set(gradient, "AlphaCount", 2); Set(gradient, "Mode", 1);
        var data = api.WithNative(typeof(Gradient), 2, gradient); api.Static("RequireCompleteCoverage", data);
        Set(gradient, "StoragePadding", new byte[] { 0xde, 0xad }); api.Reject(() => api.Static("RequireCanonicalState", data));
        var normalized = api.Static("NormalizeStorage", data); api.Static("RequireCanonicalState", normalized);
        CollectionAssert.AreEqual(new byte[2], (byte[])Get(Get(((Array)Get(normalized, "Natives")).GetValue(0)!, "Gradient"), "StoragePadding"));
        var clone = JsonConvert.DeserializeObject(JsonConvert.SerializeObject(data), data.GetType())!; var restored = Get(((Array)Get(clone, "Natives")).GetValue(0)!, "Gradient");
        CollectionAssert.AreEqual((byte[])Get(gradient, "Keys"), (byte[])Get(restored, "Keys"));
        Set(restored, "Keys", new byte[32]); api.RejectValidate(clone);
        Set(gradient, "ColorCount", 9); api.RejectValidate(data); Set(gradient, "ColorCount", 2);
        Set(data, "Wrappers", api.Rows("Wrapper", api.Describe(typeof(Gradient), 0, ""))); api.RejectValidate(data);
    }

    [TestMethod]
    public void PropertySheetStorageRebuildsTypedPackingAndHashDependenciesWithoutLosingSemanticValues()
    {
        var api = new Api(); var sheetType = api.Runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentShaderPropertySheet", true)!;
        var sheet = Activator.CreateInstance(sheetType.GetNestedType("Data", All)!, true)!;
        var propertyType = sheetType.GetNestedType("Property", All)!; var properties = Array.CreateInstance(propertyType, 2);
        for (var index = 0; index < 2; index++) {
            var property = Activator.CreateInstance(propertyType, true)!; Set(property, "Name", index == 0 ? "_A" : "_Z");
            Set(property, "Elements", 1); Set(property, "Values", BitConverter.GetBytes(index + 7)); properties.SetValue(property, index);
        }
        Set(sheet, "Properties", properties);
        var storage = api.Row("SheetStorageData"); Set(storage, "MemoryLabel", 0x33); Set(storage, "Padding", Enumerable.Repeat((byte)0xbc, 11).ToArray());
        Set(storage, "TypeStarts", new[] { 0, 2, 2, 2, 2, 2, 2 }); Set(storage, "Names", new[] { "_Z", "_A" });
        Set(storage, "Descriptors", new uint[] { 0x100000, 0x100004 });
        Set(storage, "PackingIsCanonical", false); Set(storage, "HashIsCurrent", false); Set(storage, "LayoutHashIsCurrent", false);
        var arrays = ((Array)Get(storage, "Arrays")).Cast<object>().ToArray();
        for (var index = 0; index < arrays.Length; index++) {
            var count = index == 2 ? 8 : 2; var stride = index == 2 ? 1 : 4;
            Set(arrays[index], "Count", count); Set(arrays[index], "Capacity", count + 3); Set(arrays[index], "Allocated", true);
            Set(arrays[index], "UnusedBytes", Enumerable.Repeat((byte)0x89, 3 * stride).ToArray());
        }
        var native = api.Row("Native"); Set(native, "Key", "resident-native-wrapper/native/000000"); Set(native, "Kind", 0); Set(native, "References", 1);
        Set(native, "Sheet", sheet); Set(native, "SheetStorage", storage);
        var data = api.Data(); Set(data, "Natives", api.Rows("Native", native)); Set(data, "Wrappers", api.Rows("Wrapper", api.Describe(typeof(MaterialPropertyBlock), 0, "resident-native-wrapper/native/000000")));
        api.Validate(data); api.Reject(() => api.Static("RequireCanonicalState", data));
        var normalized = api.Static("NormalizeStorage", data); api.Static("RequireCanonicalState", normalized);
        var result = Get(((Array)Get(normalized, "Natives")).GetValue(0)!, "SheetStorage");
        CollectionAssert.AreEqual(new[] { "_A", "_Z" }, (string[])Get(result, "Names")); Assert.AreEqual(0x39, Get(result, "MemoryLabel"));
        Assert.AreEqual(JsonConvert.SerializeObject(sheet), JsonConvert.SerializeObject(Get(((Array)Get(normalized, "Natives")).GetValue(0)!, "Sheet")));
        Assert.IsTrue((bool)Get(result, "HashIsCurrent")); Assert.IsTrue((bool)Get(result, "LayoutHashIsCurrent"));
        Set(result, "HashIsCurrent", false); api.Reject(() => api.Static("RequireCanonicalState", normalized));
        Set(arrays[0], "Flags", 1); api.RejectValidate(data);
        Set(data, "CoverageGaps", new[] { "property-sheet-borrowed-storage:resident-native-wrapper/native/000000/0" });
        api.Validate(data); api.Reject(() => api.Static("RequireCanonicalState", api.Static("NormalizeStorage", data)));
    }

    [TestMethod]
    public void PropertySheetHashUsesThePinnedNativeXxh32Algorithm()
    {
        var api = new Api();
        Assert.AreEqual(0x02cc5d05U, api.Static("SheetHash", Array.Empty<byte>(), 0U));
        Assert.AreEqual(0x550d7456U, api.Static("SheetHash", System.Text.Encoding.ASCII.GetBytes("a"), 0U));
        Assert.AreEqual(0x32d153ffU, api.Static("SheetHash", System.Text.Encoding.ASCII.GetBytes("abc"), 0U));
        var first = Enumerable.Range(0, 96).Select(value => (byte)value).ToArray(); var second = (byte[])first.Clone(); second[65] ^= 1;
        Assert.AreNotEqual(api.Static("SheetHash", first, 0x8f37154bU), api.Static("SheetHash", second, 0x8f37154bU));
        Assert.AreNotEqual(api.Static("SheetHash", first, 0U), api.Static("SheetHash", first, 0x8f37154bU));
    }

    private static T Wrapper<T>(long pointer) where T : class
    {
        var result = (T)RuntimeHelpers.GetUninitializedObject(typeof(T)); GC.SuppressFinalize(result);
        for (var type = typeof(T); type != null; type = type.BaseType) {
            var field = type.GetField("m_Ptr", All | BindingFlags.DeclaredOnly); if (field == null) continue;
            field.SetValue(result, new IntPtr(pointer)); return result;
        }
        throw new InvalidOperationException("Wrapper fixture has no native pointer field.");
    }
    private static float Float(int bits) => BitConverter.Int32BitsToSingle(bits);
    private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
    [MethodImpl(MethodImplOptions.NoInlining)] private static float Add(float a, float b) => a + b;
    [MethodImpl(MethodImplOptions.NoInlining)] private static float Sub(float a, float b) => a - b;
    [MethodImpl(MethodImplOptions.NoInlining)] private static float Mul(float a, float b) => a * b;
    [MethodImpl(MethodImplOptions.NoInlining)] private static float Div(float a, float b) => a / b;
    private static (float Start, float End) LoopInterval(float time, float first, float last)
    {
        var distance = Sub(time, first); var period = Sub(last, first);
        var wrapped = Add(Sub(distance, Mul((float)Math.Floor(Div(distance, period)), period)), first);
        wrapped = Math.Min(Math.Max(wrapped, first), last); var offset = Sub(time, wrapped);
        return (Add(first, offset), Add(last, offset));
    }
    private static float Polynomial(float time, float a, float b, float c, float d) => Add(Mul(Add(Mul(Add(Mul(time, a), b), time), c), time), d);
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static object Get(object value, string field) => value.GetType().GetField(field, All)!.GetValue(value)!;
    private static void Set(object value, string field, object? data) => value.GetType().GetField(field, All)!.SetValue(value, data);
    private sealed class Api
    {
        internal readonly Assembly Runtime;
        private readonly Type type;
        private readonly object policy;
        internal Api()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory); while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
            Assert.IsNotNull(root); Runtime = Assembly.LoadFrom(Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
            type = Runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentManagedNativeWrappers", true)!;
            var policyType = Runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentGraphPolicy", true)!;
            policy = Activator.CreateInstance(policyType, All, null, new object?[] { (Func<object, string?>)(_ => null), null, null }, null)!;
        }
        internal object Row(string name) => Activator.CreateInstance(type.GetNestedType(name, All)!, true)!;
        internal Array Rows(string name, params object[] values) { var rows = Array.CreateInstance(type.GetNestedType(name, All)!, values.Length); for (var index = 0; index < values.Length; index++) rows.SetValue(values[index], index); return rows; }
        internal object Data() => Row("Data");
        internal object Describe(Type actual, int index, string native) => Static("DescribeWrapper", actual, "resident-native-wrapper/object/" + index.ToString("D6"), native);
        internal object Curve(int count, int capacity) { var row = Row("CurveData"); Set(row, "Caches", new byte[56]); Set(row, "Keys", new byte[count * 28]); Set(row, "Capacity", capacity); Set(row, "KeyCount", count); Set(row, "KeyStorageFlags", capacity == 0 ? 1 : 0); Set(row, "UnusedKeys", new byte[(capacity - count) * 28]); Set(row, "PreInfinity", 2); Set(row, "PostInfinity", 2); Set(row, "RotationOrder", 4); return row; }
        internal object WithNative(Type wrapperType, int kind, object value)
        {
            var data = Data(); var native = Row("Native"); Set(native, "Key", "resident-native-wrapper/native/000000"); Set(native, "Kind", kind); Set(native, "References", 1); Set(native, kind == 1 ? "Curve" : "Gradient", value);
            Set(data, "Wrappers", Rows("Wrapper", Describe(wrapperType, 0, "resident-native-wrapper/native/000000"))); Set(data, "Natives", Rows("Native", native)); return data;
        }
        internal object Discover(IReadOnlyDictionary<string, object> roots, Func<object, string?>? external = null) => Static("Discover", roots, policy, external ?? (_ => null));
        internal object Static(string name, params object[] values) => type.GetMethod(name, All)!.Invoke(null, values)!;
        internal void Validate(object data) => Static("Validate", data);
        internal void RejectValidate(object data) => Reject(() => Validate(data));
        internal void Reject(Action action) { var error = Assert.ThrowsExactly<TargetInvocationException>(action); Assert.IsInstanceOfType<InvalidDataException>(error.GetBaseException()); }
    }
}
