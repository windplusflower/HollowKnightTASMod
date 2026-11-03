using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentShaderPropertySheetTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static object Get(object owner, string name) => owner.GetType().GetField(name, Flags)!.GetValue(owner)!;
    private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Flags)!.SetValue(owner, value);
    private sealed class Api
    {
        private readonly Type api;
        internal Api()
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo != null && !File.Exists(Path.Combine(repo.FullName, "HollowKnightTAS.sln"))) repo = repo.Parent;
            Assert.IsNotNull(repo);
            api = Assembly.LoadFrom(Path.Combine(repo.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
                .GetType("HollowKnightTAS.Runtime.FullRun.ResidentShaderPropertySheet", true)!;
        }
        internal object Row(string type, params (string Name, object Value)[] fields)
        {
            var result = Activator.CreateInstance(api.GetNestedType(type, Flags)!, true)!;
            foreach (var field in fields) Set(result, field.Name, field.Value); return result;
        }
        internal Array Array(string type, params object[] values)
        {
            var result = System.Array.CreateInstance(api.GetNestedType(type, Flags)!, values.Length);
            for (var index = 0; index < values.Length; index++) result.SetValue(values[index], index); return result;
        }
        internal object Fixture()
        {
            var scalar = Row("Property", ("Name", "custom/fade"), ("Kind", 0), ("Elements", 1), ("Values", BitConverter.GetBytes(unchecked((int)0x80000000))));
            var vector = Row("Property", ("Name", "custom/size"), ("Kind", 1), ("Elements", 1), ("Flags", 2U),
                ("Values", new[] { unchecked((int)0x7fc12345), 2, 3, 4 }.SelectMany(BitConverter.GetBytes).ToArray()));
            var texture = Row("TextureValue", ("Binding", "asset/origin/one"), ("AssignedDimension", 2), ("RequiredDimension", 1),
                ("TexelSize", "custom/size"));
            var sampler = Row("Property", ("Name", "custom/image"), ("Kind", 3), ("Elements", 1), ("Textures", Array("TextureValue", texture)));
            return Row("Data", ("AllowAddProperty", true), ("Properties", Array("Property", scalar, vector, sampler)));
        }
        internal object Call(string name, params object[] values) => api.GetMethod(name, Flags)!.Invoke(null, values)!;
        internal void Reject(string name, params object[] values)
        {
            var error = Assert.ThrowsExactly<TargetInvocationException>(() => Call(name, values));
            Assert.IsInstanceOfType<InvalidDataException>(error.InnerException);
        }
    }
    [TestMethod]
    public void TargetIdsAreReboundWhileFloatBitsAndAuxiliaryVectorAliasesRemainExact()
    {
        var api = new Api(); var data = api.Fixture();
        var ids = new Func<string, int>(name => name == "custom/fade" ? -101 : name == "custom/size" ? 77 : 504);
        var layout = api.Call("BuildLayout", data, ids, new Func<string, int>(_ => 9001));
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 2, 3, 3, 3 }, (int[])Get(layout, "Starts"));
        CollectionAssert.AreEqual(new[] { -101, 77, 504 }, (int[])Get(layout, "NameIds"));
        CollectionAssert.AreEqual(new uint[] { 0x00100000, 0x80100004, 0x00100014 }, (uint[])Get(layout, "Descriptors"));
        var bytes = (byte[])Get(layout, "Values"); Assert.AreEqual(56, bytes.Length);
        Assert.AreEqual(unchecked((int)0x80000000), BitConverter.ToInt32(bytes, 0));
        Assert.AreEqual(unchecked((int)0x7fc12345), BitConverter.ToInt32(bytes, 4));
        Assert.AreEqual(9001, BitConverter.ToInt32(bytes, 20));
        Assert.AreEqual(-1, BitConverter.ToInt32(bytes, 44));
        Assert.AreEqual(0, BitConverter.ToInt32(bytes, 48));
        Assert.AreEqual(-1, BitConverter.ToInt32(bytes, 52));
    }
    [TestMethod]
    public void MissingTexturesAndCollapsedPropertyIdsCannotBecomeValidTargetBindings()
    {
        var api = new Api(); var data = api.Fixture();
        api.Reject("BuildLayout", data, new Func<string, int>(_ => 9), new Func<string, int>(_ => 15));
        api.Reject("BuildLayout", data, new Func<string, int>(name => name == "custom/fade" ? 101 : name == "custom/size" ? 102 : 103), new Func<string, int>(_ => 0));
        var vector = ((Array)Get(data, "Properties")).GetValue(1)!; Set(vector, "Name", "different vector");
        api.Reject("Validate", data);
    }
    [TestMethod]
    public void UnknownNativeBufferResourcesRemainUnsupportedAfterReportedGapsAreErased()
    {
        var api = new Api(); var buffer = api.Row("Property", ("Name", "arbitrary compute buffer"), ("Kind", 4), ("Elements", 1));
        var data = api.Row("Data", ("Properties", api.Array("Property", buffer)));
        api.Call("Validate", data); api.Reject("RequireCompleteCoverage", data);
    }
    [TestMethod]
    public void SealedPropertyCopyPreservesArrayValuesAndRejectsTruncatedPayloads()
    {
        var api = new Api(); var data = api.Fixture(); var copy = api.Call("Copy", data);
        var original = ((Array)Get(data, "Properties")).GetValue(0)!;
        ((byte[])Get(original, "Values"))[0] = 1;
        api.Reject("RequireEqual", data, copy);
        Set(original, "Values", new byte[3]); api.Reject("Validate", data); api.Call("Validate", copy);
    }
}
