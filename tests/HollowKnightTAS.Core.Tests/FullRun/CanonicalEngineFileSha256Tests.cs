using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalEngineFileSha256Tests
{
    private static void Windows()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("The pinned engine validator uses the Windows provider.");
    }
    [TestMethod]
    public void ReusedAssetProviderHashesEachActualArrayAndRejectsUseAfterClose()
    {
        Windows();
        using var hasher = new CanonicalEngineFileSha256.BufferHasher();
        foreach (var value in new[] { Array.Empty<byte>(), Encoding.UTF8.GetBytes("shared asset"), new byte[250000] })
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant(), hasher.Hex(value));
        hasher.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => hasher.Hex(Array.Empty<byte>()));
    }
    [TestMethod]
    [DataRow("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [DataRow("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public void Windows_provider_matches_known_sha256_vectors(string value, string expected)
    {
        Windows(); using var stream = new MemoryStream(Encoding.ASCII.GetBytes(value), false);
        Assert.AreEqual(expected, Convert.ToHexString(CanonicalEngineFileSha256.ComputeHash(stream)).ToLowerInvariant());
    }
    [TestMethod]
    public void Complete_short_reads_and_block_boundaries_match_independent_full_hash()
    {
        Windows(); var random = new Random(1817);
        foreach (var size in new[] { 0, 1, 55, 56, 63, 64, 65, 131071, 131072, 131073, 262143, 262144, 262145 }) {
            var bytes = new byte[size]; random.NextBytes(bytes); var expected = SHA256.HashData(bytes);
            foreach (var step in new[] { 7, 1021, 131072 }) {
                using var stream = new ShortStream(bytes, step);
                CollectionAssert.AreEqual(expected, CanonicalEngineFileSha256.ComputeHash(stream));
                Assert.AreEqual((long)size, stream.Position);
            }
        }
    }
    [TestMethod]
    public void Read_failure_rejects_and_does_not_poison_later_provider_use()
    {
        Windows();
        for (var i = 0; i < 8; i++) {
            using var failed = new FailingStream();
            Assert.ThrowsExactly<IOException>(() => CanonicalEngineFileSha256.ComputeHash(failed));
            using var good = new MemoryStream(new byte[] { 1, 2, 3 });
            CollectionAssert.AreEqual(SHA256.HashData(new byte[] { 1, 2, 3 }), CanonicalEngineFileSha256.ComputeHash(good));
        }
    }
    private sealed class ShortStream(byte[] data, int step) : MemoryStream(data, false)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(step, count));
    }
    private sealed class FailingStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("injected read failure");
    }
}
