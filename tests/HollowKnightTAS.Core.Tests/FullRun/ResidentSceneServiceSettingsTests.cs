using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class ResidentSceneServiceSettingsTests
{
    // Three independently addressed node buffers, just like a target-local
    // std::list: the portable witness must depend on order/bindings, not on
    // addresses. Corrupt reverse links must fail even with a valid count.
    [TestMethod]
    public void ControllerMembershipUsesBothListDirectionsAndOwnsOnlyUnusedBytes()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
        Assert.IsNotNull(root);
        var api = Assembly.LoadFrom(Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
            .GetType("HollowKnightTAS.Runtime.FullRun.ResidentSceneServiceSettings", true)!.GetMethod("ReadControllerList", BindingFlags.Static|BindingFlags.NonPublic)!;
        var nodes = new Dictionary<IntPtr,byte[]>(); var padding = new List<(long, int)>();
        void Node(long ptr, long next, long prev, int id)
        {
            var bytes = new byte[24];
            BitConverter.GetBytes(next).CopyTo(bytes,0); BitConverter.GetBytes(prev).CopyTo(bytes,8);
            BitConverter.GetBytes(id).CopyTo(bytes,16); nodes[(IntPtr)ptr]=bytes;
        }
        string[] Read(ulong size) => (string[])api.Invoke(null, new object[] { (IntPtr)100L, size,
            (Func<IntPtr,int,byte[]>)((ptr,count) => nodes[ptr].Take(count).ToArray()),
            (Func<int,string>)(id => "controller/" + id),
            (Action<IntPtr,byte[]>)((ptr,bytes) => padding.Add((ptr.ToInt64(),bytes.Length))), (Action)(() => {}) })!;
        void Reject(ulong n) => Assert.IsInstanceOfType<InvalidDataException>(
            Assert.ThrowsExactly<TargetInvocationException>(() => Read(n)).GetBaseException());
        Node(100,200,300,-123); Node(200,300,100,9); Node(300,100,200,3);
        CollectionAssert.AreEqual(new[]{"controller/9","controller/3"},Read(2));
        CollectionAssert.AreEqual(new[]{(116L,8),(220L,4),(320L,4)},padding.ToArray());
        Reject(1); Reject(3);
        Node(300,100,100,3); Reject(2);
        Node(100,100,100,-123); CollectionAssert.AreEqual(Array.Empty<string>(), Read(0));
        Node(100,100,200,-123); Reject(0);
    }
}
