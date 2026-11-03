using System;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class ResidentFmodSystemStateTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private const ulong Clock = 0x1234567889abcdefUL;

    [TestMethod]
    public void SystemsRequireBothGlobalCellAndList()
    {
        var api = new Api();
        var data = api.Data(0, api.Row(3)); api.Validate(data, true);
        Set(data, "ListPresent", false); api.Reject(data, true);
        Set(data, "ListPresent", true); Set(data, "CellPresent", false); api.Reject(data, true);
    }

    [TestMethod]
    public void DuplicateSystemIndicesAreRejected()
    {
        var api = new Api();
        var data = api.Data(0, api.Row(3), api.Row(11)); api.Validate(data, true);
        var rows = (Array)Get(data, "Rows"); Set(rows.GetValue(1)!, "Index", 3U);
        api.Reject(data, true);
    }

    [TestMethod]
    public void ManagerPresenceAndOrdinalMustAgree()
    {
        var api = new Api(); var data = api.Data(0, api.Row(3), api.Row(11));
        api.Validate(data, true); api.Reject(data, false);
        Set(data, "ManagerRow", -1); api.Reject(data, true); api.Validate(data, false);
        Set(data, "ManagerRow", 2); api.Reject(data, true);
        Set(data, "ManagerRow", -2); api.Reject(data, false);
        var empty = api.Data(-1); api.Validate(empty, false); api.Reject(empty, true);
    }

    [TestMethod]
    public void NativeAliasesMustBeDenseInFirstAppearanceOrder()
    {
        var api = new Api(); var row = api.Row(3);
        Set(row, "Output", 2); api.Reject(api.Data(0, row), true);
        Set(row, "Output", 1); Set(row, "Software", 3); api.Reject(api.Data(0, row), true);
        Set(row, "Software", -1); api.Reject(api.Data(0, row), true);
    }

    [TestMethod]
    public void TwoSystemsMayShareOutputAndOneSystemMayShareNativeAliases()
    {
        var api = new Api(); var first = api.Row(3); var second = api.Row(11);
        Set(first, "Output", 1); Set(first, "Software", 1); Set(first, "Emulated", 2);
        Set(second, "Output", 1); Set(second, "Software", 2); Set(second, "Emulated", 2);
        var data = api.Data(1, first, second); api.Validate(data, true);
        Assert.AreEqual(Clock, (ulong)Get(first, "DspClock"));
        Assert.AreEqual(Clock, (ulong)Get(second, "DspClock"));
        Assert.AreEqual(0x12345678U, (uint)(Clock >> 32));
        Assert.AreEqual(0x89abcdefU, unchecked((uint)Clock));
    }

    private static object Get(object value, string field) => value.GetType().GetField(field, All)!.GetValue(value)!;
    private static void Set(object value, string field, object item) => value.GetType().GetField(field, All)!.SetValue(value, item);

    private sealed class Api
    {
        private readonly Type type;
        internal Api()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
            Assert.IsNotNull(root);
            var assembly = Assembly.LoadFrom(Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
            type = assembly.GetType("HollowKnightTAS.Runtime.FullRun.ResidentFmodSystemState", true)!;
        }
        private object New(string name) => Activator.CreateInstance(type.GetNestedType(name, All)!, true)!;
        internal object Row(uint index)
        {
            var row = New("Row"); Set(row, "Index", index); Set(row, "DspClock", Clock); Set(row, "Initialized", true);
            return row;
        }
        internal object Data(int manager, params object[] rows)
        {
            var data = New("Data"); Set(data, "CellPresent", true); Set(data, "ListPresent", true); Set(data, "ManagerRow", manager);
            var array = Array.CreateInstance(type.GetNestedType("Row", All)!, rows.Length);
            for (var i = 0; i < rows.Length; i++) array.SetValue(rows[i], i);
            Set(data, "Rows", array); return data;
        }
        internal void Validate(object data, bool managerHasSystem) => type.GetMethod("Validate", All)!.Invoke(null, new object[] { data, managerHasSystem });
        internal void Reject(object data, bool managerHasSystem)
        {
            var error = Assert.ThrowsExactly<TargetInvocationException>(() => Validate(data, managerHasSystem));
            Assert.IsNotNull(error.InnerException);
            Assert.IsTrue(error.InnerException is InvalidOperationException || error.InnerException is InvalidDataException,
                "Expected a DTO validation rejection, not a reflection or dependency error: " + error.InnerException);
        }
    }
}
