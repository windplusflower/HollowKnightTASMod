using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class ResidentPhysicsSettingsStateTests
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    [TestMethod]
    public void SettingsValueImageCoversEveryBodyFieldAndExcludesOnlyDeclaredCppPadding()
    {
        var api = new Api(); var padding = new[] { 117, 118, 119, 126, 127 };
        for (var offset = 52; offset < 260; offset++) {
            var native = new byte[296]; native[offset] = 0xa5; var values = api.Extract(native);
            Assert.AreEqual(208, values.Length);
            Assert.AreEqual(padding.Contains(offset) ? 0 : 1, values.Count(value => value != 0), "Native offset " + offset);
            Assert.AreEqual(padding.Contains(offset) ? (byte)0 : (byte)0xa5, values[offset - 52]);
        }
        api.Reject(() => api.Extract(new byte[295]));
    }
    [TestMethod]
    public void Box2DDependenciesMatchPinnedSseRoundingAndPreserveAllJobOptions()
    {
        var api = new Api(); var values = new byte[208];
        var offsets = new[] { 88, 68, 72, 76, 80, 84, 92, 96, 100, 104, 108 };
        var inputs = new[] { 0.01f, 1f, 0.2f, 8f, 100f, 360f, 0.2f, 0.75f, 0.5f, 0.01f, 2f };
        for (var index = 0; index < offsets.Length; index++) Buffer.BlockCopy(BitConverter.GetBytes(inputs[index]), 0, values, offsets[index] - 52, 4);
        var jobs = Enumerable.Range(0, 64).Select(index => (byte)(index + 17)).ToArray(); jobs[2] = jobs[3] = 0;
        Buffer.BlockCopy(jobs, 0, values, 124 - 52, jobs.Length);
        var actual = api.Build(values); var expected = new uint[] { 0x3c23d70a, 0x3f800000, 0x3e4ccccd, 0x3e0efa35, 0x42c80000,
            0x40c90fdb, 0x3e4ccccd, 0x3f400000, 0x3f000000, 0x3c23d70a, 0x3d0efa35, 0x461c4000, 0x421de9e7 };
        CollectionAssert.AreEqual(expected, Enumerable.Range(0, 13).Select(index => BitConverter.ToUInt32(actual, index * 4)).ToArray());
        CollectionAssert.AreEqual(jobs, actual.Skip(52).ToArray());
        Buffer.BlockCopy(BitConverter.GetBytes(0x80000000U), 0, values, 68 - 52, 4);
        Assert.AreEqual(0x80000000U, BitConverter.ToUInt32(api.Build(values), 4));
    }
    [TestMethod]
    public void DAndDependencyAreSeparateFromHistoryIndependentStorageS0()
    {
        var api = new Api(); var source = api.Data(); var changed = api.Copy(source);
        var expectedS0 = api.Encode(source, "EncodeS0");
        Get<byte[]>(changed, "Values")[0] = 0x80; // Exact gravity bits are D, not Box2D scalar globals.
        Get<uint[]>(changed, "LayerCollisionMatrix")[31] = 0x80000001;
        Set(changed, "DefaultMaterialPresent", true); Set(changed, "DefaultMaterial", "resident-asset/actual-material");
        api.Complete(changed); CollectionAssert.AreEqual(expectedS0, api.Encode(changed, "EncodeS0"));
        Assert.IsFalse(api.Encode(source, "EncodeValues").SequenceEqual(api.Encode(changed, "EncodeValues")));
        CollectionAssert.AreEqual(api.Encode(source, "EncodeDependency"), api.Encode(changed, "EncodeDependency"));
        api.Reject(() => api.Equal(source, changed));
    }
    [TestMethod]
    public void OnlyPaddingAndOwnedAllocationCapacityCanBeRepairedByCommonCanon()
    {
        var api = new Api(); var value = api.Data(); var originalD = api.Encode(value, "EncodeValues");
        Get<byte[]>(value, "Padding")[10] = 0x74; Get<byte[]>(value, "Box2D")[54] = 0xc3; Set(value, "MatrixCapacity", 64);
        api.Canonicalizable(value); api.Reject(() => api.Complete(value));
        CollectionAssert.AreEqual(originalD, api.Encode(value, "EncodeValues"));
        Set(value, "CoverageGaps", Array.Empty<string>()); api.Reject(() => api.Complete(value));
        Get<byte[]>(value, "Padding")[10] = 0; Get<byte[]>(value, "Box2D")[54] = 0; Set(value, "MatrixCapacity", 32); api.Complete(value);
        Set(value, "MatrixOwnsStorage", false); api.Reject(() => api.Canonicalizable(value));
        Set(value, "MatrixOwnsStorage", true); Set(value, "LayerCollisionMatrix", new uint[31]); api.Reject(() => api.Canonicalizable(value));
    }
    [TestMethod]
    public void ClearedGapStringsCannotAdmitUnboundMaterialOrModifiedBox2DDependencies()
    {
        var api = new Api(); var value = api.Data(); Set(value, "DefaultMaterialPresent", true);
        api.Validate(value); api.Reject(() => api.Canonicalizable(value)); Set(value, "DefaultMaterial", "resident-asset/material"); api.Complete(value);
        Get<byte[]>(value, "Box2D")[0] ^= 1; Set(value, "CoverageGaps", Array.Empty<string>());
        api.Reject(() => api.Canonicalizable(value)); Get<byte[]>(value, "Box2D")[0] ^= 1;
        Get<byte[]>(value, "Values")[68 - 52] = 1; api.Reject(() => api.Complete(value));
        Set(value, "Box2D", api.Build(Get<byte[]>(value, "Values"))); api.Complete(value);
    }
    [TestMethod]
    public void NativeSettingsSchemaRejectsWrongBodyKindsAndPreservesExtraSerializedMatrixRows()
    {
        var api = new Api(); var value = api.Data(); Set(value, "LayerCollisionMatrix", new uint[37]); Set(value, "MatrixCapacity", 37); api.Complete(value);
        var copy = api.Copy(value); Get<uint[]>(value, "LayerCollisionMatrix")[36] = 123;
        api.Reject(() => api.Equal(value, copy));
        Get<byte[]>(value, "Values")[112 - 52] = 2; api.Reject(() => api.Validate(value)); Get<byte[]>(value, "Values")[112 - 52] = 0;
        Get<byte[]>(value, "Values")[117 - 52] = 1; api.Reject(() => api.Validate(value)); Get<byte[]>(value, "Values")[117 - 52] = 0;
        Buffer.BlockCopy(BitConverter.GetBytes(3), 0, Get<byte[]>(value, "Values"), 120 - 52, 4); api.Reject(() => api.Validate(value));
        Set(value, "Values", new byte[208]); Set(value, "Manager", "resident-native-global/1986420/Physics2DManager/wrong"); api.Reject(() => api.Validate(value));
    }
    [TestMethod]
    public void SettingsManagerOwnershipRequiresActualRegisteredRttiAndFullSize()
    {
        var api = new Api(); var type = api.NativeClass(); Assert.IsTrue(api.Owns(type));
        Set(type, "Size", 184); Assert.IsFalse(api.Owns(type)); Set(type, "Size", 296);
        Set(type, "Rva", 0x1a17ee8); Assert.IsFalse(api.Owns(type)); Set(type, "Rva", 0x1986420);
        Set(type, "ObjectDerived", false); Assert.IsFalse(api.Owns(type)); Set(type, "ObjectDerived", true);
        Set(type, "Module", "Core"); Assert.IsFalse(api.Owns(type));
    }

    private static T Get<T>(object value, string name) => (T)value.GetType().GetField(name, All)!.GetValue(value)!;
    private static void Set(object value, string name, object item) => value.GetType().GetField(name, All)!.SetValue(value, item);
    private sealed class Api
    {
        private readonly Assembly runtime;
        private readonly Type type;
        internal Api()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory); while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
            Assert.IsNotNull(root); runtime = Assembly.LoadFrom(Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
            type = runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentPhysicsSettingsState", true)!;
        }
        internal object Data()
        {
            var data = Activator.CreateInstance(type.GetNestedType("Data", All)!, true)!;
            Set(data, "Manager", "resident-native-global/1986420/Physics2D//Physics2DSettings"); Set(data, "MatrixCapacity", 32);
            Set(data, "MatrixOwnsStorage", true); Set(data, "LayerCollisionMatrix", new uint[32]); Set(data, "Box2D", Build(Get<byte[]>(data, "Values"))); return data;
        }
        internal object NativeClass()
        {
            var value = Activator.CreateInstance(runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentNativeGlobalObjects", true)!.GetNestedType("NativeClass", All)!, true)!;
            Set(value, "Rva", 0x1986420); Set(value, "Name", "Physics2DSettings"); Set(value, "Module", "Physics2D"); Set(value, "Size", 296);
            Set(value, "ObjectDerived", true); Set(value, "GlobalManager", true); return value;
        }
        internal bool Owns(object value) => (bool)type.GetMethod("OwnsManager", All)!.Invoke(null, new[] { value })!;
        internal byte[] Extract(byte[] value) => (byte[])type.GetMethod("ExtractValues", All)!.Invoke(null, new object[] { value })!;
        internal byte[] Build(byte[] value) => (byte[])type.GetMethod("BuildBox2D", All)!.Invoke(null, new object[] { value })!;
        internal byte[] Encode(object value, string name) => (byte[])type.GetMethod(name, All)!.Invoke(null, new[] { value })!;
        internal object Copy(object value) => value.GetType().GetMethod("Copy", All)!.Invoke(value, null)!;
        internal void Validate(object value) => type.GetMethod("Validate", All)!.Invoke(null, new[] { value });
        internal void Complete(object value) => type.GetMethod("RequireComplete", All)!.Invoke(null, new[] { value });
        internal void Canonicalizable(object value) => type.GetMethod("RequireCanonicalizable", All)!.Invoke(null, new[] { value });
        internal void Equal(object expected, object actual) => type.GetMethod("RequireEqual", All)!.Invoke(null, new[] { expected, actual });
        internal void Reject(Action action) { var error = Assert.ThrowsExactly<TargetInvocationException>(action); Assert.IsInstanceOfType<InvalidDataException>(error.GetBaseException()); }
    }
}
