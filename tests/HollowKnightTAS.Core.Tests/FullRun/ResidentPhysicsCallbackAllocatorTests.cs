using System;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class ResidentPhysicsCallbackAllocatorTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static Type Api()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !File.Exists(Path.Combine(repo.FullName, "HollowKnightTAS.sln"))) repo = repo.Parent;
        Assert.IsNotNull(repo);
        return Assembly.LoadFrom(Path.Combine(repo.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll")).GetType("HollowKnightTAS.Runtime.FullRun.ResidentPhysicsCallbackAllocator", true)!;
    }
    private static object Data(Type api) => Activator.CreateInstance(api.GetNestedType("Data", Flags)!, true)!;
    private static void Set(object data, string field, object value) => data.GetType().GetField(field, Flags)!.SetValue(data, value);
    [TestMethod]
    public void AbsentAllocatorAllowsNoRows()
    {
        var api = Api(); var data = Data(api);
        Set(data, "AllocChunk", -1); Set(data, "DeallocChunk", -1);
        api.GetMethod("Validate", Flags)!.Invoke(null, new[] { data, (object)0 });
    }
    [TestMethod]
    public void FreeChainCannotAliasActiveRow()
    {
        var api = Api(); var data = Data(api);
        Set(data, "Present", true); Set(data, "Rows", new[] { 0 });
        Set(data, "FreeState", new[] { new byte[] { 0, 1, 0, 255 } });
        var error = Assert.ThrowsExactly<TargetInvocationException>(() => api.GetMethod("Validate", Flags)!.Invoke(null, new[] { data, (object)1 }));
        Assert.IsInstanceOfType<InvalidOperationException>(error.InnerException);
    }
    [TestMethod]
    public void UnlistedOccupiedSlotsAreRejected()
    {
        var api = Api(); var data = Data(api);
        Set(data, "Present", true); Set(data, "FreeState", new[] { new byte[] { 255, 0, 255 } });
        Assert.ThrowsExactly<TargetInvocationException>(() => api.GetMethod("Validate", Flags)!.Invoke(null, new[] { data, (object)0 }));
    }
    [TestMethod]
    public void MultiChunkDirectoryAllowsArbitraryRetiredFreeOrder()
    {
        var api = Api(); var data = Data(api);
        var free = new byte[258]; free[0] = 254; free[1] = 255;
        for (int i = 0; i < 255; ++i) free[i + 2] = (byte)(254 - i);
        free[257] = 73; // Historical terminal is a byte, not a pointer or sentinel.
        Set(data, "Present", true); Set(data, "AllocChunk", 1); Set(data, "DeallocChunk", 0);
        Set(data, "FreeState", new[] { free, (byte[])free.Clone() });
        api.GetMethod("Validate", Flags)!.Invoke(null, new[] { data, (object)0 });
    }
    [TestMethod]
    public void FreeChainCannotRepeatSlots()
    {
        var api = Api(); var data = Data(api);
        Set(data, "Present", true); Set(data, "FreeState", new[] { new byte[] { 3, 2, 3, 3, 255 } });
        var error = Assert.ThrowsExactly<TargetInvocationException>(() => api.GetMethod("Validate", Flags)!.Invoke(null, new[] { data, (object)0 }));
        StringAssert.Contains(error.InnerException!.Message, "free slot");
    }
}
