using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class ResidentTagManagerStateTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    [TestMethod]
    public void CanonicalTreesCoverNonPerfectSizesAndRejectCyclesOrWrongBlackHeight()
    {
        var api = new Api();
        foreach (var count in Enumerable.Range(0, 129).Concat(new[] { 255, 256, 1024, 65536 })) {
            var shape = api.Shape(count, out var root); Assert.AreEqual(count * 4, shape.Length); api.ValidateShape(shape, root);
        }
        var bad = api.Shape(7, out var badRoot); bad[badRoot * 4] = badRoot; api.Reject(() => api.ValidateShape(bad, badRoot));
        bad = api.Shape(7, out badRoot); bad[3] = 0; api.Reject(() => api.ValidateShape(bad, badRoot));
        bad = api.Shape(7, out badRoot); bad[1] = -1; api.Reject(() => api.ValidateShape(bad, badRoot));
        api.Reject(() => api.ValidateShape(new int[3], -1));
    }

    [TestMethod]
    public void ValidHistoryDependentTreeShapeMustPassCommonCanonBeforeAdmission()
    {
        var api = new Api(); var data = api.Data(4); api.Complete(data);
        var tags = Get<object>(data, "Tags"); Set(tags, "Root", 2);
        Set(tags, "Shape", new[] { -1, 1, -1, 0, 0, 2, -1, 1, 1, -1, 3, 1, -1, 2, -1, 1 });
        api.Canonicalizable(data); api.Reject(() => api.Complete(data));
        Set(data, "CoverageGaps", Array.Empty<string>()); api.Reject(() => api.Complete(data));
        Set(tags, "Shape", api.Shape(4, out var root)); Set(tags, "Root", root); api.Complete(data);
    }

    [TestMethod]
    public void BothNativeTagMapsAndEveryNamedLayerMustAgreeIndependently()
    {
        var api = new Api(); var data = api.Data(2);
        var inverse = Get<Array>(Get<object>(data, "TagNames"), "Rows");
        Set(Get<object>(inverse.GetValue(1)!, "Name"), "Value", "Other0001");
        api.Reject(() => api.Canonicalizable(data));
        data = api.Data(2); var names = Get<Array>(data, "LayerNames"); names.SetValue(api.Text("CustomLayer"), 31);
        api.Reject(() => api.Canonicalizable(data));
        Set(data, "Layers", api.Map(false, new[] { "CustomLayer" }, new uint[] { 31 })); api.Complete(data);
        Set(Get<Array>(Get<object>(data, "Layers"), "Rows").GetValue(0)!, "Id", 32U);
        api.Reject(() => api.Canonicalizable(data));
    }

    [TestMethod]
    public void CompleteDIncludesActualIdsOrderDefaultIndexAndLockedFlags()
    {
        var api = new Api(); var source = api.Data(1); var changed = api.Copy(source);
        var sortingType = api.Nested("SortingLayer"); var layers = Array.CreateInstance(sortingType, 2);
        layers.SetValue(api.Sorting("Back", 77, true), 0); layers.SetValue(api.Sorting("Front", uint.MaxValue, false), 1);
        Set(changed, "SortingLayers", layers); Set(changed, "SortingCapacity", 2); Set(changed, "DefaultSortingIndex", 1);
        api.Complete(changed); CollectionAssert.AreEqual(api.Encode(source, "EncodeS0"), api.Encode(changed, "EncodeS0"));
        Assert.IsFalse(api.Encode(source, "EncodeValues").SequenceEqual(api.Encode(changed, "EncodeValues")));
        api.Reject(() => api.Equal(source, changed));
        var reordered = api.Copy(changed); var ordered = Get<Array>(reordered, "SortingLayers");
        var first = ordered.GetValue(0); ordered.SetValue(ordered.GetValue(1), 0); ordered.SetValue(first, 1);
        api.Reject(() => api.Equal(changed, reordered));
        var unlocked = api.Copy(changed); Set(Get<Array>(unlocked, "SortingLayers").GetValue(0)!, "Locked", false);
        api.Reject(() => api.Equal(changed, unlocked));
    }

    [TestMethod]
    public void StringAndContainerStorageIsCanonicalWithoutDiscardingLogicalValues()
    {
        var api = new Api(); var data = api.Data(1); var original = api.Encode(data, "EncodeValues");
        Set(data, "SortingCapacity", 20); Get<byte[]>(data, "Padding")[7] = 31;
        var text = Get<object>(Get<Array>(Get<object>(data, "Tags"), "Rows").GetValue(0)!, "Name");
        Set(text, "Label", 73); Get<byte[]>(text, "Unused")[0] = 49;
        api.Canonicalizable(data); api.Reject(() => api.Complete(data));
        CollectionAssert.AreEqual(original, api.Encode(data, "EncodeValues"));
        Set(data, "RootAllocatorLabel", 57); api.Reject(() => api.Canonicalizable(data));
        Set(data, "RootAllocatorLabel", 56); Set(data, "SortingOwnsStorage", false); api.Reject(() => api.Canonicalizable(data));
        Set(data, "SortingOwnsStorage", true); Set(data, "SortingAllocatorLabel", 75); api.Reject(() => api.Canonicalizable(data));
    }

    [TestMethod]
    public void NativeStringKeyOrderingUsesUtf8BytesAndRetainsUnicodeNames()
    {
        var api = new Api(); const string low = "\ue000", high = "\U00010000";
        Assert.IsTrue(StringComparer.Ordinal.Compare(low, high) > 0);
        Assert.IsTrue(api.Compare(low, high) < 0);
        var data = api.Data(0);
        Set(data, "Tags", api.Map(false, new[] { low, high }, new uint[] { 4, 9 }));
        Set(data, "TagNames", api.Map(true, new[] { low, high }, new uint[] { 4, 9 })); api.Complete(data);
        Set(data, "Tags", api.Map(false, new[] { high, low }, new uint[] { 9, 4 })); api.Reject(() => api.Validate(data));
    }

    [TestMethod]
    public void ClearedGapTextCannotBypassMissingNativeDataOrInvalidStrings()
    {
        var api = new Api(); var data = api.Data(1); Set(data, "DefaultSortingIndex", 1);
        Set(data, "CoverageGaps", Array.Empty<string>()); api.Reject(() => api.Complete(data));
        data = api.Data(1); Set(Get<Array>(data, "LayerNames").GetValue(0)!, "Value", "a\0b"); api.Reject(() => api.Validate(data));
        data = api.Data(1); Set(Get<object>(data, "Tags"), "SentinelPadding", new byte[53]); api.Reject(() => api.Validate(data));
        data = api.Data(1); Set(data, "Manager", "resident-native-global/195f170/Other//TagManager"); api.Reject(() => api.Validate(data));
    }

    [TestMethod]
    public void ManagerOwnershipRequiresFullNativeTypeAndContextContract()
    {
        var api = new Api(); var value = api.NativeClass(); Assert.IsTrue(api.Owns(value));
        Set(value, "Size", 48); Assert.IsFalse(api.Owns(value)); Set(value, "Size", 1400);
        Set(value, "Rva", 0x195f171); Assert.IsFalse(api.Owns(value)); Set(value, "Rva", 0x195f170);
        Set(value, "GlobalManager", false); Assert.IsFalse(api.Owns(value)); Set(value, "GlobalManager", true);
        Set(value, "Stripped", true); Assert.IsFalse(api.Owns(value));
    }

    private static T Get<T>(object value, string name) => (T)value.GetType().GetField(name, All)!.GetValue(value)!;
    private static void Set(object value, string name, object field) => value.GetType().GetField(name, All)!.SetValue(value, field);
    private sealed class Api
    {
        private readonly Assembly runtime;
        private readonly Type type;
        internal Api()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory); while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
            Assert.IsNotNull(root); runtime = Assembly.LoadFrom(Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
            type = runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentTagManagerState", true)!;
        }
        internal Type Nested(string name) => type.GetNestedType(name, All)!;
        private object New(string name) => Activator.CreateInstance(Nested(name), true)!;
        internal object Text(string value)
        {
            var result = New("TextValue"); var length = Encoding.UTF8.GetByteCount(value); Set(result, "Value", value); Set(result, "Label", 72);
            Set(result, "Inline", length < 16); Set(result, "Capacity", length < 16 ? 15 : length); Set(result, "Unused", new byte[length < 16 ? 19 - length : 12]); return result;
        }
        internal object Map(bool numeric, string[] names, uint[] ids)
        {
            var result = New("MapData"); Set(result, "NumericKey", numeric); var rows = Array.CreateInstance(Nested("MapRow"), names.Length);
            for (var index = 0; index < names.Length; index++) { var row = New("MapRow"); Set(row, "Name", Text(names[index])); Set(row, "Id", ids[index]); rows.SetValue(row, index); }
            Set(result, "Rows", rows); Set(result, "Shape", Shape(rows.Length, out var root)); Set(result, "Root", root); Set(result, "Padding", new byte[rows.Length * 10]); return result;
        }
        internal object Sorting(string name, uint id, bool locked)
        { var result = New("SortingLayer"); Set(result, "Name", Text(name)); Set(result, "Id", id); Set(result, "Locked", locked); return result; }
        internal object Data(int tags)
        {
            var result = New("Data"); Set(result, "Manager", "resident-native-global/195f170/Core//TagManager"); Set(result, "RootAllocatorLabel", 56);
            Set(result, "SortingAllocatorLabel", 74); Set(result, "SortingOwnsStorage", true); Set(result, "SortingCapacity", 1);
            var names = Enumerable.Range(0, tags).Select(index => "Tag" + index.ToString("D6")).ToArray(); var ids = Enumerable.Range(0, tags).Select(index => (uint)index).ToArray();
            Set(result, "Tags", Map(false, names, ids)); Set(result, "TagNames", Map(true, names, ids)); Set(result, "Layers", Map(false, Array.Empty<string>(), Array.Empty<uint>()));
            var layers = Array.CreateInstance(Nested("TextValue"), 32); for (var index = 0; index < 32; index++) layers.SetValue(Text(""), index); Set(result, "LayerNames", layers);
            var sorting = Array.CreateInstance(Nested("SortingLayer"), 1); sorting.SetValue(Sorting("Default", 0, false), 0); Set(result, "SortingLayers", sorting); return result;
        }
        internal object NativeClass()
        {
            var value = Activator.CreateInstance(runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentNativeGlobalObjects", true)!.GetNestedType("NativeClass", All)!, true)!;
            Set(value, "Rva", 0x195f170); Set(value, "Name", "TagManager"); Set(value, "Module", "Core"); Set(value, "Size", 1400);
            Set(value, "ObjectDerived", true); Set(value, "GlobalManager", true); return value;
        }
        private object? Call(string method, params object[] args) => type.GetMethod(method, All)!.Invoke(null, args);
        internal int[] Shape(int count, out int root) { var args = new object[] { count, 0 }; var result = (int[])Call("CanonicalTreeShape", args)!; root = (int)args[1]; return result; }
        internal void ValidateShape(int[] shape, int root) => Call("ValidateTreeShape", shape, root);
        internal int Compare(string a, string b) => (int)Call("CompareNames", a, b)!;
        internal object Copy(object value) => value.GetType().GetMethod("Copy", All)!.Invoke(value, null)!;
        internal byte[] Encode(object value, string method) => (byte[])Call(method, value)!;
        internal void Validate(object value) => Call("Validate", value);
        internal void Canonicalizable(object value) => Call("RequireCanonicalizable", value);
        internal void Complete(object value) => Call("RequireComplete", value);
        internal void Equal(object expected, object actual) => Call("RequireEqual", expected, actual);
        internal bool Owns(object value) => (bool)Call("OwnsManager", value)!;
        internal void Reject(Action action) { var error = Assert.ThrowsExactly<TargetInvocationException>(action); Assert.IsInstanceOfType<InvalidDataException>(error.GetBaseException()); }
    }
}
