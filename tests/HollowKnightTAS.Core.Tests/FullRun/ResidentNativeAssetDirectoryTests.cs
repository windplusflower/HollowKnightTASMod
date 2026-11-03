using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class ResidentNativeAssetDirectoryTests
{
    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protection);
    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protection, out uint previous);
    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint flags);
    [TestMethod]
    public void NativeNamesStopAtPageBoundaryAndReadbackChecksOnlyWitnessedBytes()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Pinned Windows memory-reader fixture.");
        var page = Environment.SystemPageSize;
        var memory = VirtualAlloc(IntPtr.Zero,(UIntPtr)(page*2),0x3000,4);
        Assert.AreNotEqual(IntPtr.Zero,memory);
        try {
            var end = IntPtr.Add(memory,page);
            Assert.IsTrue(VirtualProtect(end,(UIntPtr)page,1,out _));
            var api = new Api(); var live = api.Row("Live"); var row = api.Row("Row");
            Set(live,"Admitted",(Action)(() => { }));
            var data = Enumerable.Repeat((byte)97,320).Append((byte)0).ToArray();
            var name = IntPtr.Subtract(end,data.Length);
            Marshal.Copy(data,0,name,data.Length); api.Call("ReadName",live,row,name);
            CollectionAssert.AreEqual(data[..^1],Get<byte[]>(row,"Name"));
            var witness = Get<System.Collections.Generic.List<Tuple<IntPtr,byte[]>>>(live,"Witness").Single();
            Assert.AreEqual(name,witness.Item1); CollectionAssert.AreEqual(data,witness.Item2);
            var verifier = Activator.CreateInstance(api.Runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory+Readback",true)!,true)!;
            var matches = verifier.GetType().GetMethod("Matches", All)!;
            Assert.IsTrue((bool)matches.Invoke(verifier,new object[]{name,data})!);
            Marshal.WriteByte(name,319,98);
            Assert.IsFalse((bool)matches.Invoke(verifier,new object[]{name,data})!);
            Assert.IsTrue((bool)matches.Invoke(verifier,new object[]{name,data[..16]})!);
        } finally { VirtualFree(memory,UIntPtr.Zero,0x8000); }
    }
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    [TestMethod]
    public void SerializedProvenanceDoesNotAuthorizeUnknownNativeBody()
    {
        var api = new Api(); var data = api.Data(); var row = api.Rows(data).GetValue(0)!;
        Set(row, "Kind", "unsupported"); Set(row, "Values", Array.Empty<byte>()); Set(row, "Padding", Array.Empty<byte>());
        api.Validate(data); Set(data, "CoverageGaps", Array.Empty<string>()); api.Reject(() => api.Complete(data));
        Assert.IsTrue(api.Gaps(data).Any(gap => gap.Contains("missing-native-body")));
    }
    [TestMethod]
    public void SourceBindingUsesActualPathLocalIdAndTypeWithoutNamesOrInstanceIds()
    {
        var api = new Api(); var row = api.Rows(api.Data()).GetValue(0)!; var source = Get<object>(row, "Source");
        var first = api.Key(source); Set(source, "LocalIdentifier", -991L); Assert.AreNotEqual(first, api.Key(source));
        Set(source, "SourcePath", "actual/other-file"); Assert.AreNotEqual(first, api.Key(source));
        Assert.IsFalse(api.Key(source).Contains("actual/other-file")); // Compact key; complete original identity is still in Source.
    }
    [TestMethod]
    public void SourceCannotSubstituteAnotherActualNativeTypeOrDuplicateIdentity()
    {
        var api = new Api(); var data = api.Data(); var row = api.Rows(data).GetValue(0)!;
        Set(Get<object>(row, "Source"), "NativeTypeRva", 0x1968310); api.Reject(() => api.Validate(data));
        data = api.Data(); row = api.Rows(data).GetValue(0)!; Set(data, "Rows", api.Array("Row", row, row)); api.Reject(() => api.Validate(data));
    }
    [TestMethod]
    public void UnresolvedPPtrCannotBeErasedByClearingCoverageGaps()
    {
        var api = new Api(); var data = api.Data(); Set(api.Rows(data).GetValue(0)!, "Owner", "@unresolved-native-owner");
        Set(data, "CoverageGaps", Array.Empty<string>()); api.Reject(() => api.Complete(data));
    }
    [TestMethod]
    public void TextBodyCapturesBinaryStringButDoesNotClaimStorageRestoration()
    {
        var api = new Api(); var data = api.Data(); var row = api.Rows(data).GetValue(0)!;
        Set(row, "Kind", "text-asset"); Set(row, "TypeRva", 0x1968310); Set(row, "TypeKey", "1968310/Core//TextAsset"); Set(row, "Size", 96); Set(row, "Vtable", 0x1696fa8);
        var source = Get<object>(row, "Source"); Set(source, "NativeTypeRva", 0x1968310); Set(row, "Binding", api.Key(source));
        Set(row, "Values", Array.Empty<byte>()); Set(row, "Padding", Array.Empty<byte>());
        var text = api.Row("TextStorage"); Set(text, "Bytes", new byte[] { 0x41, 0, 0xff }); Set(text, "Inline", true); Set(text, "Capacity", 15); Set(text, "Label", 72); Set(text, "Unused", new byte[16]); Set(row, "Text", text);
        api.Validate(data); CollectionAssert.Contains(api.Gaps(data), "native-asset/text-storage-restoration-unproven"); api.Reject(() => api.Complete(data));
        Set(text, "Unused", new byte[15]); api.Reject(() => api.Validate(data));
    }
    [TestMethod]
    public void NativeMaterialRestoreSeparatesWritableValuesFromReadonlyNameAndImage()
    {
        var api = new Api(); var source = api.Data(); var target = api.Copy(source);
        ((byte[])Get<object>(api.Rows(target).GetValue(0)!, "Values"))[0] = 9; api.Readonly(source, target); api.Reject(() => api.Equal(source, target));
        target = api.Copy(source); var row = api.Rows(target).GetValue(0)!; Set(row, "Name", new byte[] { 8 }); Set(row, "NamePresent", true); api.Reject(() => api.Readonly(source, target));
        target = api.Copy(source); Set(Get<object>(api.Rows(target).GetValue(0)!, "Source"), "Hash", new string('b', 64)); api.Reject(() => api.Readonly(source, target));
    }
    [TestMethod]
    public void NativeBodyCannotStandInForMissingIndependentObject48Coverage()
    {
        var api = new Api(); var data = api.Data(); api.Complete(data);
        var objects = Activator.CreateInstance(api.Runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentObjectNativeState+Data", true)!, true)!;
        api.Reject(() => api.Call("RequireBaseCoverage", data, objects));
    }
    [TestMethod]
    public void NativeDirectoryCopyPreservesIndependentSourceAndBodyBytes()
    {
        var api = new Api(); var source = api.Data(); var copy = api.Copy(source);
        ((byte[])Get<object>(api.Rows(copy).GetValue(0)!, "Padding"))[0] = 91;
        ((byte[])Get<object>(Get<object>(api.Rows(copy).GetValue(0)!, "Source"), "TypeHash"))[0] = 8;
        Assert.AreEqual(0, ((byte[])Get<object>(api.Rows(source).GetValue(0)!, "Padding"))[0]);
        Assert.AreEqual(0, ((byte[])Get<object>(Get<object>(api.Rows(source).GetValue(0)!, "Source"), "TypeHash"))[0]);
    }
    [TestMethod]
    public void ProjectionUsesExactRegisteredRowReferenceInsteadOfSameTypeOrVtable()
    {
        var api = new Api(); var type = api.Runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentNativeGlobalObjects+Row", true)!;
        var first = Activator.CreateInstance(type, true)!; var second = Activator.CreateInstance(type, true)!;
        Set(first, "TypeRva", 0x1986550); Set(second, "TypeRva", 0x1986550); Set(first, "Vtable", 0x1701768); Set(second, "Vtable", 0x1701768);
        var rows = System.Array.CreateInstance(type, 2); rows.SetValue(first, 0); rows.SetValue(second, 1);
        Assert.AreEqual(1, (int)api.Call("ExactRegisteredRowIndex", rows, second)!);
        var lookalike = Activator.CreateInstance(type, true)!; Set(lookalike, "TypeRva", 0x1986550); Set(lookalike, "Vtable", 0x1701768);
        api.Reject(() => api.Call("ExactRegisteredRowIndex", rows, lookalike));
        rows.SetValue(first, 1); api.Reject(() => api.Call("ExactRegisteredRowIndex", rows, first));
    }
    [TestMethod]
    public void MaterialPaddingIsCommonS0AndOwnerPPtrIsReadonlyDependency()
    {
        var api = new Api(); var source = api.Data(); var target = api.Copy(source);
        ((byte[])Get<object>(api.Rows(target).GetValue(0)!, "Padding"))[0] = 5;
        api.Call("RequireCanonicalizableBodies", target); api.Reject(() => api.Complete(target));
        Set(target, "CoverageGaps", Array.Empty<string>()); api.Reject(() => api.Complete(target));
        target = api.Copy(source); Set(api.Rows(target).GetValue(0)!, "Owner", "actual-target-owner");
        api.Reject(() => api.Readonly(source, target));
        target = api.Copy(source); ((byte[])Get<object>(api.Rows(target).GetValue(0)!, "Values"))[0] = 9;
        CollectionAssert.AreEqual((byte[])api.Call("EncodeS0", source)!, (byte[])api.Call("EncodeS0", target)!);
    }
    private static T Get<T>(object value, string field) => (T)value.GetType().GetField(field, All)!.GetValue(value)!;
    private static void Set(object value, string field, object data) => value.GetType().GetField(field, All)!.SetValue(value, data);
    private sealed class Api
    {
        internal readonly Assembly Runtime;
        private readonly Type type;
        internal Api()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory); while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
            Assert.IsNotNull(root); Runtime = Assembly.LoadFrom(Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
            type = Runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentNativeAssetDirectory", true)!;
        }
        internal object Row(string name) => Activator.CreateInstance(type.GetNestedType(name, All)!, true)!;
        internal Array Array(string name, params object[] values) { var result = System.Array.CreateInstance(type.GetNestedType(name, All)!, values.Length); for (var i = 0; i < values.Length; i++) result.SetValue(values[i], i); return result; }
        internal Array Rows(object data) => Get<Array>(data, "Rows");
        internal object Data()
        {
            var data = Row("Data"); var row = Row("Row"); Set(row, "TypeRva", 0x1986550); Set(row, "TypeKey", "1986550/Physics2D//PhysicsMaterial2D");
            var slots = (string[])Runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentBuiltinTextureRoots",true)!.GetMethod("DeclaredOrigins", All)!.Invoke(null,null)!;
            Set(data,"BuiltinSlotOwners",slots.Select(_ => "").ToArray());
            Set(row, "Size", 72); Set(row, "Vtable", 0x1701768); Set(row, "Kind", "physics-material-2d"); Set(row, "Persistent", true); Set(row, "Values", new byte[8]); Set(row, "Padding", new byte[4]);
            var source = Activator.CreateInstance(Runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentSerializedAssetImage+Data", true)!, true)!;
            Set(source, "SourcePath", "actual/source-file"); Set(source, "LocalIdentifier", 987654321L); Set(source, "NativeTypeRva", 0x1986550); Set(source, "Hash", new string('a', 64)); Set(source, "TypeHash", new byte[16]); Set(source, "Length", 64);
            Set(row, "Source", source); Set(row, "Binding", Key(source)); Set(data, "Rows", Array("Row", row)); return data;
        }
        internal object? Call(string name, params object[] values) => type.GetMethod(name, All)!.Invoke(null, values);
        internal string Key(object source) => (string)Call("SourceKey", source)!;
        internal object Copy(object data) => data.GetType().GetMethod("Copy", All)!.Invoke(data, null)!;
        internal void Validate(object data) => Call("Validate", data);
        internal void Complete(object data) => Call("RequireCompleteBodies", data);
        internal string[] Gaps(object data) => (string[])Call("RequiredGaps", data)!;
        internal void Equal(object source, object target) => Call("RequireEqual", source, target);
        internal void Readonly(object source, object target) => Call("RequireReadonlyDependencies", source, target);
        internal void Reject(Action action) { var error = Assert.ThrowsExactly<TargetInvocationException>(action); Assert.IsInstanceOfType<InvalidDataException>(error.GetBaseException()); }
    }
}
