using System;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class ResidentMonoNativeStateTests
{
    [TestMethod]
    public void ScriptCacheMustPreserveMethodsOrderAndSharedOwnership()
    {
        var api = new Api(); var expected = api.Cache(); var actual = api.Cache();
        api.Call("RequireCacheEqual", expected, actual);
        api.Set(actual, "Methods", new[] { api.Metadata(typeof(string).GetMethod("ToString", Type.EmptyTypes)!), "" });
        api.Reject("RequireCacheEqual", expected, actual);
        api.Set(actual, "Methods", new[] { "", api.Metadata(typeof(string).GetMethod("ToString", Type.EmptyTypes)!) });
        api.Set(actual, "ReferenceCount", 2); api.Reject("RequireCacheEqual", expected, actual);
    }
    [TestMethod]
    public void MethodSlotsCannotContainOtherKindsOfMetadata()
    {
        var api = new Api(); var data = api.Cache(); api.Set(data, "Methods", new[] { api.Metadata(typeof(string)) });
        api.Reject("ValidateCache", data);
    }
    [TestMethod]
    public void SharedMetadataStillChecksEachOwnersCacheAndCoverage()
    {
        var api = new Api();
        var first = api.Valid(false); var second = api.Valid(false);
        var cache = api.Cache(); api.Set(cache, "Class", api.Get(first, "Type"));
        api.Set(first, "Cache", cache); api.Set(second, "Cache", cache);
        var rows = Array.CreateInstance(first.GetType(), 2);
        rows.SetValue(first, 0); rows.SetValue(second, 1);
        api.Call("ValidateMany", rows, true);
        api.Set(second, "AudioFilter", true);
        api.Reject("ValidateMany", rows, false);
        api.Set(second, "CoverageGaps", new[] { "mono/audio-filter" });
        api.Call("ValidateMany", rows, false);
        api.Reject("ValidateMany", rows, true);
        api.Set(cache, "Methods", new[] { api.Metadata(typeof(string)) });
        api.Reject("ValidateMany", rows, false);
    }
    [TestMethod]
    public void ErasingCoverageCannotAdmitSerializationOrNativeGuiState()
    {
        var api = new Api(); var data = api.Valid(false); api.Call("RequireComplete", data);
        api.Set(data, "GuiState", true); api.Reject("RequireComplete", data);
        api.Set(data, "GuiState", false); api.Set(data, "CacheMethodsMatch", false); api.Reject("RequireComplete", data);
    }
    [TestMethod]
    public void ScriptableRegistrationMustRemainExplicitAndNameCopyIndependent()
    {
        var api = new Api(); var data = api.Valid(true); var clone = api.Call("Copy", data)!;
        api.Set(data, "Name", "changed"); api.Reject("RequireEqual", data, clone);
        ((int[])api.Get(clone, "ScriptableNodes"))[2] = 2; api.Reject("RequireComplete", clone);
    }
    [TestMethod]
    public void NativeNameRejectsNulAndUtf8ByteOverflow()
    {
        var api = new Api(); var data = api.Valid(false); api.Set(data, "Name", "a\0b"); api.Reject("Validate", data);
        api.Set(data, "Name", new string('汉', 6000)); api.Reject("Validate", data);
    }
    private sealed class Api
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private readonly Type type, mono, scriptable;
        internal Api()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
            Assert.IsNotNull(root);
            var directory = Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release");
            var assembly = Assembly.LoadFrom(Path.Combine(directory, "HollowKnightTAS.dll"));
            type = assembly.GetType("HollowKnightTAS.Runtime.FullRun.ResidentMonoNativeState", true)!;
            mono = typeof(UnityEngine.MonoBehaviour); scriptable = typeof(UnityEngine.ScriptableObject);
        }
        internal object New(string name) => Activator.CreateInstance(type.GetNestedType(name, All)!, true)!;
        internal object Get(object value, string name) => value.GetType().GetField(name, All)!.GetValue(value)!;
        internal void Set(object value, string name, object item) => value.GetType().GetField(name, All)!.SetValue(value, item);
        internal object? Call(string name, params object[] args) => type.GetMethod(name, All)!.Invoke(null, args);
        internal string Metadata(object value) => (string)Call("Meta", value)!;
        internal object Cache()
        {
            var result = New("CacheData"); Set(result, "Class", Metadata(typeof(string))); Set(result, "ReferenceCount", 1);
            Set(result, "MethodCapacity", 2); Set(result, "IdentifierMatches", true);
            Set(result, "Methods", new[] { "", Metadata(typeof(string).GetMethod("ToString", Type.EmptyTypes)!) }); return result;
        }
        internal object Valid(bool asset)
        {
            var result = New("Data"); Set(result, "Owner", "actual-instance"); Set(result, "Type", Metadata(asset ? scriptable : mono));
            Set(result, "Name", "原生名字"); Set(result, "ReferenceVtable", 123); Set(result, "CacheMethodsMatch", true); Set(result, "Scriptable", asset);
            if (asset) { Set(result, "ScriptableFlags", new byte[7]); Set(result, "ScriptableNodes", new int[5]); }
            return result;
        }
        internal void Reject(string name, params object[] args)
        {
            var error = Assert.ThrowsExactly<TargetInvocationException>(() => Call(name, args));
            Assert.IsInstanceOfType<InvalidDataException>(error.GetBaseException());
        }
    }
}
