using System;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;
[TestClass]
public sealed class ResidentTargetAssetLookupTests
{
    private static Type Api() {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
        Assert.IsNotNull(root);
        return Assembly.LoadFrom(Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
            .GetType("HollowKnightTAS.Runtime.FullRun.ResidentTargetAssetLookup", true)!;
    }
    private static int Find(byte[] header, int file, long local, Func<IntPtr, byte[]> read)
        => (int)Api().GetMethod("FindMappedId", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { header, file, local, read })!;
    private static void Put(byte[] bytes, int offset, long value) => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, 8);
    [TestMethod] public void ExistingRangeReturnsOnlyRegisteredIdsWithoutReadingAnyObject() {
        var header = new byte[144]; Buffer.BlockCopy(BitConverter.GetBytes(100),0,header,132,4);
        Buffer.BlockCopy(BitConverter.GetBytes(108),0,header,136,4); Buffer.BlockCopy(BitConverter.GetBytes(7),0,header,140,4);
        byte[] Forbidden(IntPtr _) => throw new AssertFailedException("range lookup must not materialize or read an Object");
        Assert.AreEqual(106, Find(header,7,3,Forbidden)); Assert.AreEqual(0, Find(header,7,5,Forbidden));
    }
    [TestMethod] public void ExactSignedTreeKeyAndParentBackReferenceAreRequired() {
        var header = new byte[144]; Put(header,80,1000); Put(header,88,1); Buffer.BlockCopy(BitConverter.GetBytes(-1),0,header,140,4);
        var sentinel = new byte[56]; sentinel[25]=1; Put(sentinel,8,2000);
        var node = new byte[56]; Put(node,0,1000); Put(node,8,1000); Put(node,16,1000);
        Buffer.BlockCopy(BitConverter.GetBytes(4),0,node,32,4); Put(node,40,-44); Buffer.BlockCopy(BitConverter.GetBytes(-901),0,node,48,4);
        byte[] Read(IntPtr p) => p.ToInt64()==1000 ? sentinel : node;
        Assert.AreEqual(-901,Find(header,4,-44,Read)); Assert.AreEqual(0,Find(header,4,-43,Read));
        Put(node,8,3000); Assert.ThrowsExactly<TargetInvocationException>(()=>Find(header,4,-44,Read));
    }
    [TestMethod] public void AmbiguousOrAbsentProvenanceNeverChoosesAConvenientOwner() {
        var method=Api().GetMethod("UniqueSourceId",BindingFlags.Static|BindingFlags.NonPublic)!;
        Assert.AreEqual(-1,(int)method.Invoke(null,new object[]{new[]{-1,-1,0}})!);
        Assert.ThrowsExactly<TargetInvocationException>(()=>method.Invoke(null,new object[]{new[]{1,2}}));
        Assert.ThrowsExactly<TargetInvocationException>(()=>method.Invoke(null,new object[]{Array.Empty<int>()}));
    }
}
