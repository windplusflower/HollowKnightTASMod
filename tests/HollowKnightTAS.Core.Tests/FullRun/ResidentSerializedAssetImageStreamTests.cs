using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class ResidentSerializedAssetImageStreamTests
{
    private const BindingFlags All = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    [TestMethod]
    public void IndependentSliceCrossesChunkBoundaryAndPreservesBinaryBytes()
    {
        var api = new Api(); var bytes = Enumerable.Range(0, 150000).Select(index => (byte)(index * 17)).ToArray();
        using var first = new MemoryStream(bytes, false); using var second = new MemoryStream(bytes, false);
        CollectionAssert.AreEqual(bytes.Skip(65530).Take(70000).ToArray(), api.Read(first, second, bytes.Length, 65530, 70000));
    }
    [TestMethod]
    public void IndependentSliceHashRejectsChangedBytesButNotBytesOutsideSlice()
    {
        var api = new Api(); var bytes = new byte[100000]; var changed = (byte[])bytes.Clone(); changed[999] = 8;
        using var first = new MemoryStream(bytes, false); using var second = new MemoryStream(changed, false);
        api.Reject(() => api.Read(first, second, bytes.Length, 900, 70000));
        CollectionAssert.AreEqual(new byte[20], api.Read(first, second, bytes.Length, 5000, 20));
    }
    [TestMethod]
    public void SliceRejectsBoundsLengthAndSharedStreamWithoutOverflow()
    {
        var api = new Api(); using var first = new MemoryStream(new byte[100], false); using var second = new MemoryStream(new byte[100], false);
        api.Reject(() => api.Read(first, second, 100, ulong.MaxValue, 1));
        api.Reject(() => api.Read(first, second, 100, 99, 2));
        api.Reject(() => api.Read(first, second, 100, 0, -1));
        api.Reject(() => api.Read(first, second, 101, 0, 1));
        api.Reject(() => api.Read(first, first, 100, 0, 1));
        CollectionAssert.AreEqual(Array.Empty<byte>(), api.Read(first, second, 100, 100, 0));
    }
    [TestMethod]
    public void VirtualArchiveAndRelativePathsNeverBecomeFilesystemGuesses()
    {
        var api = new Api(); Assert.IsFalse(api.Absolute("archive:/CAB-1/asset")); Assert.IsFalse(api.Absolute("resources.assets"));
        Assert.IsFalse(api.Absolute(@"\\.\PhysicalDrive0")); Assert.IsFalse(api.Absolute(@"\\?\C:\asset"));
        Assert.IsTrue(api.Absolute(@"D:\data\actual.assets"));
    }
    [TestMethod]
    public void SameSizeSameBytesDifferentFilesHaveDifferentHandleIdentity()
    {
        var api = new Api(); var root = Path.Combine(Path.GetTempPath(), "hktas-source-identity-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            var first = Path.Combine(root, "first.bin"); var second = Path.Combine(root, "second.bin"); File.WriteAllBytes(first, new byte[9]); File.WriteAllBytes(second, new byte[9]);
            var firstIdentity = api.Call("ReadFileIdentity", first)!; var secondIdentity = api.Call("ReadFileIdentity", second)!;
            api.Call("RequireFileIdentity", firstIdentity, firstIdentity); api.Reject(() => api.Call("RequireFileIdentity", firstIdentity, secondIdentity));
        } finally { File.Delete(Path.Combine(root, "first.bin")); File.Delete(Path.Combine(root, "second.bin")); Directory.Delete(root); }
    }
    [TestMethod]
    public void ActualInstalledLooseV22FileSliceMatchesIndependentOfflineFileRead()
    {
        const string path = @"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight_Data\Resources\unity_builtin_extra";
        if (!File.Exists(path)) { Assert.Inconclusive("installed fixture is unavailable; no archive reader is inferred"); return; }
        var api = new Api(); var length = new FileInfo(path).Length;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); var header = new byte[12]; Assert.AreEqual(12, stream.Read(header, 0, header.Length));
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 22 }, header.Skip(8).Take(4).ToArray());
        var expected = new byte[70000]; stream.Position = 4090; var read = 0; while (read < expected.Length) { var count = stream.Read(expected, read, expected.Length - read); Assert.IsTrue(count > 0); read += count; }
        CollectionAssert.AreEqual(expected, (byte[])api.Call("ReadFilesystemSlice", path, length, 4090UL, expected.Length)!);
    }
    [TestMethod]
    public void BatchDeniesWritesAndNextObservationReadsNewBytes()
    {
        var api = new Api(); var path = Path.Combine(Path.GetTempPath(), "hktas-batch-" + Guid.NewGuid().ToString("N"));
        try {
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            using ((IDisposable)api.Call("BeginObservation")!) {
                CollectionAssert.AreEqual(new byte[] { 1, 2 }, (byte[])api.Call("ReadFilesystemSlice", path, 3L, 0UL, 2)!);
                Assert.ThrowsExactly<IOException>(() => File.WriteAllBytes(path, new byte[] { 4, 5, 6 }));
                CollectionAssert.AreEqual(new byte[] { 2, 3 }, (byte[])api.Call("ReadFilesystemSlice", path, 3L, 1UL, 2)!);
            }
            File.WriteAllBytes(path, new byte[] { 4, 5, 6 });
            using ((IDisposable)api.Call("BeginObservation")!)
                CollectionAssert.AreEqual(new byte[] { 4, 5 }, (byte[])api.Call("ReadFilesystemSlice", path, 3L, 0UL, 2)!);
        } finally { File.Delete(path); }
    }
    [TestMethod]
    public void BatchCachedMetadataStillRejectsNativeDriftAndDoesNotSurviveNextBatch()
    {
        var api = new Api(); var pointer = Marshal.AllocHGlobal(8);
        try {
            Marshal.WriteInt64(pointer, 17);
            var batch = api.Call("BeginObservation")!;
            try {
                var memory = batch.GetType().GetField("Memory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(batch)!;
                var read = memory.GetType().GetMethod("Read", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var first = (byte[])read.Invoke(memory, new object[] { pointer, 8 })!;
                Assert.AreSame(first, read.Invoke(memory, new object[] { pointer, 8 }));
                Marshal.WriteInt64(pointer, 18);
                api.Reject(() => batch.GetType().GetMethod("RequireUnchanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(batch, null));
                Marshal.WriteInt64(pointer, 17);
            } finally { ((IDisposable)batch).Dispose(); }
            Marshal.WriteInt64(pointer, 18);
            using var next = (IDisposable)api.Call("BeginObservation")!;
            var nextMemory = next.GetType().GetField("Memory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(next)!;
            var bytes = (byte[])nextMemory.GetType().GetMethod("Read", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(nextMemory, new object[] { pointer, 8 })!;
            Assert.AreEqual(18L, BitConverter.ToInt64(bytes, 0));
        } finally { Marshal.FreeHGlobal(pointer); }
    }
    private sealed class Api
    {
        private readonly Type type;
        internal Api()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory); while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
            Assert.IsNotNull(root); type = Assembly.LoadFrom(Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
                .GetType("HollowKnightTAS.Runtime.FullRun.ResidentSerializedAssetImage", true)!;
        }
        internal object? Call(string name, params object[] values) => type.GetMethod(name, All)!.Invoke(null, values);
        internal byte[] Read(Stream first, Stream second, long length, ulong offset, int count) => (byte[])Call("ReadVerifiedSlice", first, second, length, offset, count, (Action)(() => { }))!;
        internal bool Absolute(string value) => (bool)Call("AbsoluteFilesystemPath", value)!;
        internal void Reject(Action action) { var error = Assert.ThrowsExactly<TargetInvocationException>(action); Assert.IsInstanceOfType<InvalidDataException>(error.GetBaseException()); }
    }
}
