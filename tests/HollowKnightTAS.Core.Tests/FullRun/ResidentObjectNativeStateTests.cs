using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class ResidentObjectNativeStateTests
{
    [TestMethod]
    public void ComponentAndGameObjectMustAgreeInBothDirections()
    {
        var api = new Api(); var data = api.Valid(); api.Call("Validate", data);
        api.Set(api.Rows(data).GetValue(0)!, "GameObject", "other");
        api.Reject("Validate", data);
    }
    [TestMethod]
    public void ActiveNodeCannotBeDuplicatedAcrossNativeManagerLists()
    {
        var api = new Api(); var data = api.Valid();
        api.Set(data, "ActiveLists", new[] { new[] { "g" }, new[] { "g" }, Array.Empty<string>() });
        api.Reject("Validate", data);
    }
    [TestMethod]
    public void ClearingGapStringsCannotAdmitUnboundEventOrCollectedHandle()
    {
        var api = new Api(); var data = api.Valid(); var row = api.Rows(data).GetValue(0)!;
        var entry = api.New("Event"); api.Set(entry, "Callback", 123); api.Set(entry, "UserData", "unresolved");
        api.Set(row, "Events", api.Array("Event", entry)); api.Reject("RequireComplete", data);
        api.Set(row, "Events", api.Array("Event")); api.Set(row, "HandleAllocated", true);
        api.Reject("RequireComplete", data);
    }
    [TestMethod]
    public void ProcessLocalAllocatorBitsAndInsufficientComponentStorageAreRejected()
    {
        var api = new Api(); var data = api.Valid(); var row = api.Rows(data).GetValue(0)!;
        api.Set(row, "Flags", 1U); api.Reject("Validate", data);
        api.Set(row, "Flags", 0U); var go = api.Get(api.Rows(data).GetValue(1)!, "Object");
        api.Set(go, "ComponentCapacity", 0); api.Reject("Validate", data);
    }
    [TestMethod]
    public void ReadbackIncludesOrderedEventAliasesAndCachedActivity()
    {
        var api = new Api(); var expected = api.Valid(); var actual = api.Valid(); api.Call("RequireEqual", expected, actual);
        var go = api.Get(api.Rows(actual).GetValue(1)!, "Object"); ((byte[])api.Get(go, "State"))[7] = 0xff;
        api.Reject("RequireEqual", expected, actual);
    }
    private sealed class Api
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private readonly Type type;
        internal Api()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
            Assert.IsNotNull(root);
            var assembly = Assembly.LoadFrom(Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
            type = assembly.GetType("HollowKnightTAS.Runtime.FullRun.ResidentObjectNativeState", true)!;
        }
        internal object New(string name) => Activator.CreateInstance(type.GetNestedType(name, All)!, true)!;
        internal Array Array(string name, params object[] values)
        {
            var result = System.Array.CreateInstance(type.GetNestedType(name, All)!, values.Length);
            for (var index = 0; index < values.Length; index++) result.SetValue(values[index], index);
            return result;
        }
        internal object Get(object value, string field) => value.GetType().GetField(field, All)!.GetValue(value)!;
        internal void Set(object value, string field, object content) => value.GetType().GetField(field, All)!.SetValue(value, content);
        internal Array Rows(object data) => (Array)Get(data, "Rows");
        internal object Valid()
        {
            var data = New("Data"); var component = New("Row"); var owner = New("Row"); var go = New("GameObjectData");
            Set(component, "Owner", "c"); Set(component, "Type", "actual-component-type"); Set(component, "Vtable", 1); Set(component, "Kind", 1); Set(component, "GameObject", "g");
            Set(owner, "Owner", "g"); Set(owner, "Type", "actual-gameobject-type"); Set(owner, "Vtable", 2); Set(owner, "Kind", 2);
            Set(go, "ComponentCapacity", 1); Set(go, "Components", new[] { "c" }); Set(go, "Types", new uint[] { 42 }); Set(go, "State", new byte[16]);
            Set(owner, "Object", go); Set(data, "Rows", Array("Row", component, owner)); return data;
        }
        internal void Call(string method, params object[] args) => type.GetMethod(method, All)!.Invoke(null, args);
        internal void Reject(string method, params object[] args)
        {
            var error = Assert.ThrowsExactly<TargetInvocationException>(() => Call(method, args));
            Assert.IsInstanceOfType<InvalidDataException>(error.GetBaseException());
        }
    }
}
