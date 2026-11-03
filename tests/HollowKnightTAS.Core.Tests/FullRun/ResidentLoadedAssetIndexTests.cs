using System;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class ResidentLoadedAssetIndexTests
{
    private const BindingFlags All = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static Type Api()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
        Assert.IsNotNull(root);
        return Assembly.LoadFrom(Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
            .GetType("HollowKnightTAS.Runtime.FullRun.ResidentLoadedAssetIndex", true)!;
    }
    // Pinned native leaf 87eb0 (SHA c4da4650...) was executed offline with
    // synthetic hash tables: deleted slot -> same-hash different ID -> match.
    // No game process, managed wrapper, or ScriptingWrapperFor was involved.
    [DataTestMethod]
    [DataRow(int.MinValue, 0x7e7b3c12U, 120)]
    [DataRow(-901, 0x6f60a2cbU, 96)]
    [DataRow(-1, 0xfe64c182U, 72)]
    [DataRow(1, 0xb48681b6U, 24)]
    [DataRow(77, 0xb45a98e7U, 168)]
    [DataRow(int.MaxValue, 0xeb5744b1U, 24)]
    public void SignedIdsDeletedSlotsAndCollisionsMatchActualNativeLeaf(int id, uint expectedHash, int nativeOffset)
    {
        var api = Api();
        Assert.AreEqual(expectedHash, (uint)api.GetMethod("Hash", All)!.Invoke(null, new object[] { id })!);
        var header = new byte[32]; Put(header, 8, 56U); Put(header, 12, 2U);
        var nodes = new byte[192]; for (var i = 0; i < 8; i++) Put(nodes, i * 24, uint.MaxValue);
        var first = (int)(expectedHash & 56); Put(nodes, first * 3, 0xfffffffeU);
        var second = (first + 8) & 56; Put(nodes, second * 3, expectedHash & 0xfffffffcU); Put(nodes, second * 3 + 8, unchecked((uint)(id ^ 1)));
        Put(nodes, nativeOffset, expectedHash & 0xfffffffcU); Put(nodes, nativeOffset + 8, unchecked((uint)id));
        Buffer.BlockCopy(BitConverter.GetBytes(0x12345678L), 0, nodes, nativeOffset + 16, 8);
        var reads = 0;
        byte[] Read(int offset) { reads++; var row = new byte[24]; Buffer.BlockCopy(nodes, offset, row, 0, 24); return row; }
        Assert.AreEqual(new IntPtr(0x12345678), Find(api, header, id, Read));
        Assert.AreEqual(3, reads);
    }
    [TestMethod]
    public void EmptyBucketStopsWithoutScanningOrCreatingAnOwner()
    {
        var header = new byte[32]; Put(header, 8, 56U); var reads = 0;
        byte[] Read(int offset) { reads++; var node = new byte[24]; Put(node, 0, uint.MaxValue); return node; }
        Assert.AreEqual(IntPtr.Zero, Find(Api(), header, 9, Read)); Assert.AreEqual(1, reads);
    }
    [TestMethod]
    public void CorruptFullTableCannotProbeForever()
    {
        var header = new byte[32]; Put(header, 8, 56U); Put(header, 12, 8U); var reads = 0;
        byte[] Read(int offset) { reads++; var node = new byte[24]; Put(node, 0, 0xfffffffeU); return node; }
        var error = Assert.ThrowsExactly<TargetInvocationException>(() => Find(Api(), header, 9, Read));
        Assert.IsInstanceOfType<InvalidDataException>(error.GetBaseException()); Assert.AreEqual(8, reads);
    }
    private static IntPtr Find(Type api, byte[] header, int id, Func<int, byte[]> read)
        => (IntPtr)api.GetMethod("FindIdPointer", All)!.Invoke(null, new object[] { header, id, read })!;
    private static void Put(byte[] bytes, int offset, uint value) => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, 4);
}
