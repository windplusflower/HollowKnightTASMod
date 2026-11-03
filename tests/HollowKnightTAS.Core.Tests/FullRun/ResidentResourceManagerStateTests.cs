using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class ResidentResourceManagerStateTests
{
    private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    [TestMethod]
    public void CompleteReadonlyDependencyComparesArrayTreeStringAndSourceMetadata()
    {
        var api = new Api(); var data = api.Data(); api.Complete(data);
        void Different(Action<object> change) { var other = api.Copy(data); change(other); api.Complete(other); api.Reject(() => api.Equal(data, other)); }
        Different(other => ((byte[])Get<object>(other, "Padding"))[0] = 1);
        Different(other => ((byte[])Get<object>(other, "SentinelPadding"))[0] = 1);
        Different(other => Set(Get<object>(other, "DependencyStorage"), "Capacity", 2));
        Different(other => Set(Get<object>(other, "DependencyStorage"), "Owned", false));
        Different(other => Set(Get<object>(other, "DependencyStorage"), "Label", 75));
        Different(other => ((byte[])Get<object>(Get<object>(other, "DependencyStorage"), "Padding"))[0] = 1);
        Different(other => ((byte[])Get<object>(api.Items(other, "Dependencies").GetValue(0)!, "Padding"))[0] = 1);
        Different(other => ((byte[])Get<object>(api.Items(other, "Container").GetValue(0)!, "Padding"))[0] = 1);
        Different(other => ((byte[])Get<object>(Get<object>(api.Items(other, "Container").GetValue(0)!, "Name"), "Unused"))[0] = 1);
        Different(other => Set(Get<object>(api.Items(other, "References").GetValue(0)!, "Source"), "Hash", new string('b', 64)));
    }
    [TestMethod]
    public void PendingReloadAndUnknownMutationPolicyRemainDerivedHardGaps()
    {
        var api = new Api(); var data = api.Data(); Set(data, "NeedsReload", true); Set(data, "CoverageGaps", Array.Empty<string>());
        api.Reject(() => api.Complete(data)); CollectionAssert.Contains(api.Gaps(data), "resource-manager/pending-reload");
        data = api.Data(); Set(data, "Ownership", "rebuild-storage"); api.Reject(() => api.Complete(data));
    }
    [TestMethod]
    public void MissingMappedPPtrSourceCannotBeHiddenByClearingCoverageGaps()
    {
        var api = new Api(); var data = api.Data(); var reference = api.Items(data, "References").GetValue(0)!;
        var source = Activator.CreateInstance(api.Runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentSerializedAssetImage+Data", true)!, true)!;
        Set(source, "CoverageGaps", new[] { "serialized-image/no-source-identifier" }); Set(reference, "Source", source); Set(reference, "Key", "");
        Set(data, "CoverageGaps", Array.Empty<string>()); api.Reject(() => api.Complete(data));
    }
    [TestMethod]
    public void LoadedSourceIsNotProofOfCoveredNativeBody()
    {
        var api = new Api(); var data = api.Data(); var reference = api.Items(data, "References").GetValue(0)!;
        Set(reference, "Loaded", true); api.Reject(() => api.Complete(data));
        Set(reference, "NativeBinding", "actual/native-binding"); api.Complete(data);
        var globals = Activator.CreateInstance(api.Runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentNativeGlobalObjects+Data", true)!, true)!;
        api.Reject(() => api.Call("RequireReferenceCoverage", data, globals));
    }
    [TestMethod]
    public void InvalidTreeAndPPtrIndexCannotBecomeValidDependency()
    {
        var api = new Api(); var data = api.Data(); ((int[])Get<object>(data, "Shape"))[1] = 0; api.Reject(() => api.Validate(data));
        data = api.Data(); Set(api.Items(data, "Container").GetValue(0)!, "Object", 1); api.Reject(() => api.Validate(data));
        data = api.Data(); Set(Get<object>(data, "DependencyStorage"), "Present", false); api.Reject(() => api.Validate(data));
    }
    [TestMethod]
    public void ReadonlyS0EncodingContainsActualDependencyRatherThanConstantPass()
    {
        var api = new Api(); var data = api.Data(); var other = api.Copy(data);
        CollectionAssert.AreEqual((byte[])api.Call("EncodeValues", data)!, (byte[])api.Call("EncodeS0", data)!);
        var text = Get<object>(api.Items(other, "Container").GetValue(0)!, "Name"); ((byte[])Get<object>(text, "Value"))[0] ^= 1;
        CollectionAssert.AreNotEqual((byte[])api.Call("EncodeS0", data)!, (byte[])api.Call("EncodeS0", other)!);
    }
    [TestMethod]
    public void ActualResourceManagerIdentityMatchesPinnedRttiAndBodySize()
    {
        var api = new Api(); var type = Activator.CreateInstance(api.Runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentNativeGlobalObjects+NativeClass", true)!, true)!;
        Set(type, "Rva", 0x1967280); Set(type, "Size", 104); Set(type, "Name", "ResourceManager"); Set(type, "Module", "Core"); Set(type, "ObjectDerived", true); Set(type, "GlobalManager", true);
        Assert.IsTrue((bool)api.Call("OwnsManager", type)!); Set(type, "Size", 48); Assert.IsFalse((bool)api.Call("OwnsManager", type)!);
    }
    [TestMethod]
    public void CopyRetainsIndependentNestedStorageAndReferences()
    {
        var api = new Api(); var data = api.Data(); var copy = api.Copy(data);
        ((byte[])Get<object>(Get<object>(api.Items(copy, "References").GetValue(0)!, "Source"), "TypeHash"))[0] = 9;
        ((int[])Get<object>(api.Items(copy, "Dependencies").GetValue(0)!, "References"))[0] = -1;
        Assert.AreEqual(0, ((byte[])Get<object>(Get<object>(api.Items(data, "References").GetValue(0)!, "Source"), "TypeHash"))[0]);
        Assert.AreEqual(0, ((int[])Get<object>(api.Items(data, "Dependencies").GetValue(0)!, "References"))[0]); api.Complete(data);
    }
    private static T Get<T>(object value, string field) => (T)value.GetType().GetField(field, All)!.GetValue(value)!;
    private static void Set(object value, string field, object data) => value.GetType().GetField(field, All)!.SetValue(value, data);
    private sealed class Api
    {
        internal readonly Assembly Runtime;
        private readonly Type type;
        internal Api() {
            var root = new DirectoryInfo(AppContext.BaseDirectory); while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
            Assert.IsNotNull(root); Runtime = Assembly.LoadFrom(Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll")); type = Runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentResourceManagerState", true)!;
        }
        internal object Row(string name) => Activator.CreateInstance(type.GetNestedType(name, All)!, true)!;
        private Array Rows(string name, params object[] values) { var array = Array.CreateInstance(type.GetNestedType(name, All)!, values.Length); for (var i = 0; i < values.Length; i++) array.SetValue(values[i], i); return array; }
        internal Array Items(object data, string field) => Get<Array>(data, field);
        private object Storage(int count) { var value = Row("Storage"); Set(value, "Capacity", count); Set(value, "Owned", true); Set(value, "Present", count != 0); Set(value, "Label", 74); return value; }
        internal object Data() {
            var data = Row("Data"); Set(data, "Manager", "resident-native-global/1967280/Core//ResourceManager");
            var reference = Row("Reference"); var source = Activator.CreateInstance(Runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentSerializedAssetImage+Data", true)!, true)!;
            Set(source, "SourcePath", "actual/source"); Set(source, "LocalIdentifier", 12L); Set(source, "NativeTypeRva", 0x1986550); Set(source, "Hash", new string('a', 64)); Set(source, "TypeHash", new byte[16]); Set(source, "Length", 32);
            var key = Runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentNativeAssetDirectory", true)!.GetMethod("SourceKey", All)!.Invoke(null, new[] { source })!;
            Set(reference, "Source", source); Set(reference, "Key", key); Set(data, "References", Rows("Reference", reference));
            var dependency = Row("Dependency"); Set(dependency, "Object", 0); Set(dependency, "References", new[] { 0, -1 }); Set(dependency, "Storage", Storage(2));
            Set(data, "Dependencies", Rows("Dependency", dependency)); Set(data, "DependencyStorage", Storage(1));
            var text = Row("Text"); Set(text, "Value", new byte[] { 110, 97, 109, 101 }); Set(text, "Inline", true); Set(text, "Capacity", 15); Set(text, "Label", 72); Set(text, "Unused", new byte[15]);
            var entry = Row("Entry"); Set(entry, "Name", text); Set(entry, "Object", 0); Set(data, "Container", Rows("Entry", entry)); Set(data, "Root", 0); Set(data, "Shape", new[] { -1, -1, -1, 1 }); return data;
        }
        internal object? Call(string name, params object[] values) => type.GetMethod(name, All)!.Invoke(null, values);
        internal object Copy(object data) => data.GetType().GetMethod("Copy", All)!.Invoke(data, null)!;
        internal void Validate(object data) => Call("Validate", data);
        internal void Complete(object data) => Call("RequireComplete", data);
        internal string[] Gaps(object data) => (string[])Call("RequiredGaps", data)!;
        internal void Equal(object first, object second) => Call("RequireEqual", first, second);
        internal void Reject(Action action) { var error = Assert.ThrowsExactly<TargetInvocationException>(action); Assert.IsInstanceOfType<InvalidDataException>(error.GetBaseException()); }
    }
}
