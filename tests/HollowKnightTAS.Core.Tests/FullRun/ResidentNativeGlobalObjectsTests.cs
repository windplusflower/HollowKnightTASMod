using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class ResidentNativeGlobalObjectsTests
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    [TestMethod]
    public void NativeIdHashUsesItsActualScaledMaskAndPreservesSignedIds()
    {
        var api = new Api(); var header = Header(24, 2); var bytes = Nodes(4, 24);
        U32(bytes, 0, 0); I32(bytes, 8, -123); I64(bytes, 16, 0x1000);
        U32(bytes, 24, 0xfffffffe); // Tombstone must not become an Object.
        U32(bytes, 48, 0x1234); I32(bytes, 56, 901); I64(bytes, 64, 0x2000);
        Assert.AreEqual(96, api.Length(header, true)); var rows = api.Decode(header, bytes, true);
        Assert.AreEqual(2, rows.Length); Assert.AreEqual(-123, Get<int>(rows.GetValue(0)!, "InstanceId"));
        Assert.AreEqual(new IntPtr(0x2000), Get<IntPtr>(rows.GetValue(1)!, "Pointer"));
        U32(header, 12, 1); api.Reject(() => api.Decode(header, bytes, true));
        U32(header, 12, 2); I32(bytes, 56, -123); api.Reject(() => api.Decode(header, bytes, true));
        I32(bytes, 56, 901); I64(bytes, 64, 0x1000); api.Reject(() => api.Decode(header, bytes, true));
        I64(bytes, 64, 0); api.Reject(() => api.Decode(header, bytes, true));
    }
    [TestMethod]
    public void NativeTypeSetHasDifferentScalingAndCannotAcceptMalformedCapacity()
    {
        var api = new Api(); var header = Header(48, 1); var bytes = Nodes(4, 16);
        U32(bytes, 32, 0x45678); I64(bytes, 40, 0x7000);
        Assert.AreEqual(64, api.Length(header, false)); Assert.AreEqual(1, api.Decode(header, bytes, false).Length);
        U32(header, 8, 32); api.Reject(() => api.Length(header, false)); // Three slots is not a native hash capacity.
        U32(header, 8, uint.MaxValue); api.Reject(() => api.Length(header, false));
        U32(header, 8, 48); U32(header, 12, 5); api.Reject(() => api.Length(header, false));
        U32(header, 12, 1); api.Reject(() => api.Decode(header, bytes.Take(63).ToArray(), false));
    }
    [TestMethod]
    public void GlobalManagerIdentityRequiresActualObjectAncestryRatherThanManagerNameOrObjectSizedMemory()
    {
        var api = new Api(); var data = api.Directory(); api.Complete(data);
        var classes = (Array)Get<object>(data, "Classes"); var rows = (Array)Get<object>(data, "Rows"); var row = rows.GetValue(0)!;
        Set(row, "TypeRva", 0x1900010); Set(row, "Binding", "resident-native-global/1900010/Core//LooksLikeManager");
        api.Reject(() => api.Validate(data));
        var nonObject = classes.GetValue(3)!; Set(nonObject, "ObjectDerived", true); Set(nonObject, "GlobalManager", true);
        api.Reject(() => api.Validate(data));
        Set(nonObject, "BaseRva", 0x194d180); Set(classes.GetValue(0)!, "DescendantCount", 4); Set(classes.GetValue(1)!, "DescendantCount", 3);
        api.Complete(data); // It becomes valid only with the actual registered ancestry and its type interval.
    }
    [TestMethod]
    public void OptionalBuildSettingsCoverageFollowsRegisteredOwnersNotJustTypeMetadata()
    {
        var api = new Api(); var data = api.Directory();
        var runtime = data.GetType().Assembly;
        var check = runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentBoundaryProcessState", true)!.GetMethod("ValidateBuildSettings", All)!;
        var dto = runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentBuildSettingsState+Data", true)!;
        var none = Array.CreateInstance(dto, 0);
        void Verify(Array values) => check.Invoke(null, new object[] { data, values });
        Verify(none);
        var manager = ((Array)Get<object>(data, "Classes")).GetValue(2)!;
        Set(manager, "Rva", 0x198ceb0); Set(manager, "Name", "BuildSettings"); Set(manager, "Size", 352);
        // The type is registered, but no object of that type exists yet.
        Verify(none);
        var owner = ((Array)Get<object>(data, "Rows")).GetValue(0)!;
        Set(owner, "TypeRva", 0x198ceb0); Set(owner, "Binding", "resident-native-global/198ceb0/Core//BuildSettings");
        api.Reject(() => Verify(none));
        var wrong = Array.CreateInstance(dto, 1); wrong.SetValue(Activator.CreateInstance(dto, true), 0);
        api.Reject(() => Verify(wrong));
        Set(owner, "Kind", "unbound"); api.Reject(() => Verify(none));
    }
    [TestMethod]
    public void ClearingCoverageStringsCannotHideUnboundOrMisclassifiedManagerOwners()
    {
        var api = new Api(); var data = api.Directory(); var row = ((Array)Get<object>(data, "Rows")).GetValue(0)!;
        Set(row, "Kind", "unbound"); Set(row, "Binding", ""); Set(data, "CoverageGaps", Array.Empty<string>());
        api.Validate(data); api.Reject(() => api.Complete(data));
        Set(row, "Kind", "asset"); Set(row, "Binding", "resident-asset/incorrect-manager");
        api.Validate(data); api.Reject(() => api.Complete(data));
    }
    [TestMethod]
    public void GlobalDirectoryCopyDoesNotAliasNativeClassOrOwnerManifest()
    {
        var api = new Api(); var data = api.Directory(); var copy = api.Copy(data);
        Set(((Array)Get<object>(data, "Classes")).GetValue(2)!, "Name", "changed");
        Set(((Array)Get<object>(data, "Rows")).GetValue(0)!, "Binding", "changed");
        api.Complete(copy); api.Reject(() => api.Validate(data));
    }

    private static byte[] Header(uint scaledMask, uint size) { var bytes = new byte[32]; U32(bytes, 8, scaledMask); U32(bytes, 12, size); return bytes; }
    private static byte[] Nodes(int count, int stride) { var bytes = new byte[count * stride]; for (var index = 0; index < count; index++) U32(bytes, index * stride, uint.MaxValue); return bytes; }
    private static void U32(byte[] bytes, int offset, uint value) => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, 4);
    private static void I32(byte[] bytes, int offset, int value) => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, 4);
    private static void I64(byte[] bytes, int offset, long value) => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, 8);
    private static T Get<T>(object value, string name) => (T)value.GetType().GetField(name, All)!.GetValue(value)!;
    private static void Set(object value, string name, object item) => value.GetType().GetField(name, All)!.SetValue(value, item);
    private sealed class Api
    {
        private readonly Type type;
        internal Api()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory); while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
            Assert.IsNotNull(root); var runtime = Assembly.LoadFrom(Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
            type = runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentNativeGlobalObjects", true)!;
        }
        private object Row(string name) => Activator.CreateInstance(type.GetNestedType(name, All)!, true)!;
        private Array Rows(string name, params object[] values) { var array = Array.CreateInstance(type.GetNestedType(name, All)!, values.Length); for (var index = 0; index < values.Length; index++) array.SetValue(values[index], index); return array; }
        internal int Length(byte[] header, bool ids) => (int)type.GetMethod("HashByteLength", All)!.Invoke(null, new object[] { header, ids })!;
        internal Array Decode(byte[] header, byte[] nodes, bool ids) => (Array)type.GetMethod("DecodeHashRows", All)!.Invoke(null, new object[] { header, nodes, ids })!;
        internal void Validate(object value) => type.GetMethod("Validate", All)!.Invoke(null, new[] { value });
        internal void Complete(object value) => type.GetMethod("RequireCompleteDirectory", All)!.Invoke(null, new[] { value });
        internal object Copy(object value) => value.GetType().GetMethod("Copy", All)!.Invoke(value, null)!;
        internal object Directory()
        {
            object Class(int rva, int parent, string name, int index, int descendants, bool isObject, bool manager, bool abstractType = false) {
                var value = Row("NativeClass"); Set(value, "Rva", rva); Set(value, "BaseRva", parent); Set(value, "Name", name); Set(value, "Module", "Core");
                Set(value, "Index", index); Set(value, "DescendantCount", descendants); Set(value, "Size", 48); Set(value, "ObjectDerived", isObject);
                Set(value, "GlobalManager", manager); Set(value, "Abstract", abstractType); return value;
            }
            var data = Row("Data"); Set(data, "Classes", Rows("NativeClass",
                Class(0x194b230, 0, "Object", 0, 3, true, false, true), Class(0x194d180, 0x194b230, "GameManager", 1, 2, true, true, true),
                Class(0x1900000, 0x194d180, "ActualManager", 2, 1, true, true), Class(0x1900010, 0, "LooksLikeManager", 3, 1, false, false)));
            var owner = Row("Row"); Set(owner, "TypeRva", 0x1900000); Set(owner, "Vtable", 0x1600000); Set(owner, "Kind", "global");
            Set(owner, "Binding", "resident-native-global/1900000/Core//ActualManager"); Set(data, "Rows", Rows("Row", owner)); Set(data, "RegisteredCount", 1); return data;
        }
        internal void Reject(Action action) { var error = Assert.ThrowsExactly<TargetInvocationException>(action); Assert.IsInstanceOfType<InvalidDataException>(error.GetBaseException()); }
    }
}
