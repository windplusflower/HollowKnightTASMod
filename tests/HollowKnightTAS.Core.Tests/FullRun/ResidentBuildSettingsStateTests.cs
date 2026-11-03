using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class ResidentBuildSettingsStateTests
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    [TestMethod]
    public void DependencyIncludesEveryOrderedListGuidFlagVersionAndGraphicsApi()
    {
        var api = new Api(); var baseline = api.Data(); var original = api.Encode(baseline); api.Complete(baseline);
        for (var i = 0; i < 5; i++) {
            var changed = api.Copy(baseline); var list = ((Array)Get<object>(changed, "Lists")).GetValue(i)!;
            Set(((Array)Get<object>(list, "Values")).GetValue(0)!, "Value", "other");
            CollectionAssert.AreNotEqual(original, api.Encode(changed)); api.Reject(() => api.Equal(baseline, changed));
        }
        foreach (var field in new[] { "Guid", "Flags" }) {
            var changed = api.Copy(baseline); ((byte[])Get<object>(changed, field))[0] ^= 1;
            api.Reject(() => api.Equal(baseline, changed));
        }
        var version = api.Copy(baseline); Set(Get<object>(version, "EngineVersion"), "Value", "changed"); api.Reject(() => api.Equal(baseline, version));
        var graphics = api.Copy(baseline); ((int[])Get<object>(graphics, "GraphicsApis"))[0] = -991; api.Reject(() => api.Equal(baseline, graphics));
    }
    [TestMethod]
    public void DependencyComparesAllCapacitiesLabelsOwnershipPaddingAndStringModes()
    {
        var api = new Api(); var source = api.Data();
        void Reject(Action<object> change) { var actual = api.Copy(source); change(actual); api.Complete(actual); api.Reject(() => api.Equal(source, actual)); }
        Reject(data => Set(data, "Padding", (byte)4));
        Reject(data => Set(Get<object>(data, "GraphicsStorage"), "Owned", false));
        Reject(data => Set(Get<object>(data, "GraphicsStorage"), "Capacity", 3));
        Reject(data => Set(Get<object>(data, "GraphicsStorage"), "Label", 75));
        Reject(data => ((byte[])Get<object>(Get<object>(data, "GraphicsStorage"), "Padding"))[0] = 9);
        Reject(data => ((byte[])Get<object>(Get<object>(data, "EngineVersion"), "Unused"))[0] = 7);
        Reject(data => Set(Get<object>(data, "EngineVersion"), "Label", 73));
        Reject(data => {
            var text = Get<object>(data, "EngineVersion"); Set(text, "Inline", false); Set(text, "Capacity", 0); Set(text, "Unused", new byte[12]);
        }); // Borrowed external strings are valid read-only dependencies, but not equivalent to inline strings.
        Reject(data => {
            var text = Get<object>(data, "EngineVersion"); Set(text, "Inline", false); Set(text, "Capacity", 123); Set(text, "Unused", new byte[12]);
        });
    }
    [TestMethod]
    public void MutationPolicyCannotBeBypassedByDeletingCoverageGaps()
    {
        var api = new Api(); var data = api.Data(); Set(data, "Ownership", "reallocate-owned-only"); Set(data, "CoverageGaps", Array.Empty<string>());
        api.Reject(() => api.Canonicalizable(data)); api.Reject(() => api.Complete(data)); api.Reject(() => api.Encode(data));
        var gaps = (string[])api.Call("RequiredGaps", data)!; CollectionAssert.Contains(gaps, "build-settings/unproven-native-storage-mutation-policy");
    }
    [TestMethod]
    public void AuthTokenUsesOnlyDigestAndTypedStorageMetadata()
    {
        var api = new Api(); var data = api.Data(); var encoded = api.Encode(data);
        Assert.IsFalse(Encoding.UTF8.GetString(encoded).Contains("synthetic-credential"));
        Assert.IsNull(Get<object>(data, "AuthToken").GetType().GetField("Value", All));
        var changed = api.Copy(data); ((byte[])Get<object>(Get<object>(changed, "AuthToken"), "Digest"))[0] ^= 1; api.Reject(() => api.Equal(data, changed));
        changed = api.Copy(data); Set(Get<object>(changed, "AuthToken"), "Label", 73); api.Reject(() => api.Equal(data, changed));
        changed = api.Copy(data); ((byte[])Get<object>(Get<object>(changed, "AuthToken"), "UnusedDigest"))[0] = 1; api.Reject(() => api.Equal(data, changed));
    }
    [TestMethod]
    public void ActualNativeManagerIdentityRequiresSizeAncestryAndPinnedRtti()
    {
        var api = new Api(); var type = api.NativeClass(); Assert.IsTrue(api.Owns(type));
        Set(type, "Size", 48); Assert.IsFalse(api.Owns(type)); Set(type, "Size", 352);
        Set(type, "GlobalManager", false); Assert.IsFalse(api.Owns(type)); Set(type, "GlobalManager", true);
        Set(type, "ObjectDerived", false); Assert.IsFalse(api.Owns(type)); Set(type, "ObjectDerived", true);
        Set(type, "Rva", 0x198ceb1); Assert.IsFalse(api.Owns(type));
    }
    [TestMethod]
    public void SnapshotRejectsMalformedBooleansSecretDigestStringStorageAndArrayCapacity()
    {
        var api = new Api(); var data = api.Data(); ((byte[])Get<object>(data, "Flags"))[14] = 2; api.Reject(() => api.Validate(data));
        data = api.Data(); Set(Get<object>(data, "GraphicsStorage"), "Capacity", 0); api.Reject(() => api.Validate(data));
        data = api.Data(); Set(Get<object>(data, "GraphicsStorage"), "Present", false); api.Reject(() => api.Validate(data));
        data = api.Data(); Set(Get<object>(data, "AuthToken"), "Digest", new byte[31]); api.Reject(() => api.Validate(data));
        data = api.Data(); Set(Get<object>(data, "EngineVersion"), "Unused", Array.Empty<byte>()); api.Reject(() => api.Validate(data));
        data = api.Data(); Set(data, "Lists", Array.CreateInstance(api.Row("StringArray").GetType(), 4)); api.Reject(() => api.Validate(data));
        data = api.Data(); Set(data, "Version", 1); api.Reject(() => api.Validate(data));
    }
    [TestMethod]
    public void S0EncodingReflectsActualTypedDependencyInsteadOfConstantPassBytes()
    {
        var api = new Api(); var data = api.Data(); var copy = api.Copy(data); api.Equal(data, copy);
        CollectionAssert.AreEqual(api.Encode(data), api.S0(data));
        ((int[])Get<object>(copy, "GraphicsApis"))[0] = 2; CollectionAssert.AreNotEqual(api.S0(data), api.S0(copy));
        copy = api.Copy(data); Set(copy, "CoverageGaps", new[] { "unresolved-native-state" }); api.Reject(() => api.Equal(data, copy));
    }
    [TestMethod]
    public void CopyDoesNotShareNestedMetadataValuesOrCredentialDigest()
    {
        var api = new Api(); var source = api.Data(); var copy = api.Copy(source);
        ((byte[])Get<object>(copy, "Guid"))[0] = 9; ((int[])Get<object>(copy, "GraphicsApis"))[0] = 99;
        ((byte[])Get<object>(Get<object>(copy, "AuthToken"), "Digest"))[0] ^= 1;
        var list = ((Array)Get<object>(copy, "Lists")).GetValue(0)!; Set(((Array)Get<object>(list, "Values")).GetValue(0)!, "Value", "other");
        Assert.AreEqual(0, ((byte[])Get<object>(source, "Guid"))[0]); Assert.AreEqual(1, ((int[])Get<object>(source, "GraphicsApis"))[0]);
        api.Complete(source); CollectionAssert.AreNotEqual(api.Encode(source), api.Encode(copy));
    }
    private static T Get<T>(object value, string field) => (T)value.GetType().GetField(field, All)!.GetValue(value)!;
    private static void Set(object value, string field, object data) => value.GetType().GetField(field, All)!.SetValue(value, data);
    private sealed class Api
    {
        private readonly Type type;
        private readonly Assembly runtime;
        internal Api()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory); while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
            Assert.IsNotNull(root); runtime = Assembly.LoadFrom(Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
            type = runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentBuildSettingsState", true)!;
        }
        internal object Row(string name) => Activator.CreateInstance(type.GetNestedType(name, All)!, true)!;
        private object Text(string value, int label)
        {
            var text = Row("Text"); Set(text, "Value", value); Set(text, "Label", label); Set(text, "Inline", true); Set(text, "Capacity", 15); Set(text, "Unused", new byte[19 - value.Length]); return text;
        }
        private object Storage(int count)
        {
            var storage = Row("ArrayStorage"); Set(storage, "Capacity", count); Set(storage, "Owned", true); Set(storage, "Present", count != 0); Set(storage, "Label", 74); return storage;
        }
        internal object Data()
        {
            var data = Row("Data"); Set(data, "Manager", "resident-native-global/198ceb0/ActualModule//BuildSettings");
            var lists = Array.CreateInstance(type.GetNestedType("StringArray", All)!, 5);
            for (var i = 0; i < 5; i++) {
                var list = Row("StringArray"); Set(list, "Storage", Storage(1)); var values = Array.CreateInstance(type.GetNestedType("Text", All)!, 1);
                values.SetValue(Text("list" + i, 74), 0); Set(list, "Values", values); lists.SetValue(list, i);
            }
            var secret = Row("Secret"); using (var sha = SHA256.Create()) Set(secret, "Digest", sha.ComputeHash(Encoding.UTF8.GetBytes("synthetic-credential")));
            Set(secret, "Length", 20); Set(secret, "Capacity", 20); Set(secret, "Label", 72); Set(secret, "UnusedLength", 12);
            using (var sha = SHA256.Create()) Set(secret, "UnusedDigest", sha.ComputeHash(new byte[12]));
            Set(data, "Lists", lists); Set(data, "AuthToken", secret); Set(data, "EngineVersion", Text("version", 72));
            Set(data, "GraphicsStorage", Storage(2)); Set(data, "GraphicsApis", new[] { 1, 0 }); return data;
        }
        internal object NativeClass()
        {
            var native = runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentNativeGlobalObjects+NativeClass", true)!;
            var value = Activator.CreateInstance(native, true)!; Set(value, "Rva", 0x198ceb0); Set(value, "Size", 352); Set(value, "Name", "BuildSettings");
            Set(value, "ObjectDerived", true); Set(value, "GlobalManager", true); return value;
        }
        internal object? Call(string name, params object[] values) => type.GetMethod(name, All)!.Invoke(null, values);
        internal bool Owns(object value) => (bool)Call("OwnsManager", value)!;
        internal object Copy(object value) => value.GetType().GetMethod("Copy", All)!.Invoke(value, null)!;
        internal byte[] Encode(object value) => (byte[])Call("EncodeDependencies", value)!;
        internal byte[] S0(object value) => (byte[])Call("EncodeS0", value)!;
        internal void Complete(object value) => Call("RequireComplete", value);
        internal void Canonicalizable(object value) => Call("RequireCanonicalizable", value);
        internal void Validate(object value) => Call("Validate", value);
        internal void Equal(object left, object right) => Call("RequireEqual", left, right);
        internal void Reject(Action action) { var error = Assert.ThrowsExactly<TargetInvocationException>(action); Assert.IsInstanceOfType<InvalidDataException>(error.GetBaseException()); }
    }
}
