using System;
using System.Text;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace HollowKnightTAS.Core.Tests.FullRun;
[TestClass]
public sealed class CanonicalSealedSourceTests
{
    private sealed class Header { public int Schema = 2, Movie = 1421; public string Session = "original"; }
    [TestMethod]
    public void Encoded_bytes_and_metadata_share_the_exact_single_seal_without_export_decode()
    {
        var header = new Header(); var encodes = 0;
        string Meta(Header h) => h.Schema + ":" + h.Movie + ":" + h.Session;
        var source = new CanonicalSealedSource<Header>(header, h => { encodes++; return Encoding.UTF8.GetBytes(Meta(h)); }, Meta);
        Assert.AreEqual(Meta(header), Encoding.UTF8.GetString(source.Bytes));
        for (var i = 0; i < 80; i++) Assert.AreSame(header, source.Require(source.Bytes));
        Assert.AreEqual(1, encodes);
    }
    [TestMethod]
    public void Replaced_bytes_mutated_bytes_or_changed_sealed_metadata_permanently_clear_export()
    {
        for (var mode = 0; mode < 5; mode++) {
            var header = new Header();
            var source = new CanonicalSealedSource<Header>(header, h => new byte[] { 1, 2, 3 }, h => h.Schema + ":" + h.Movie + ":" + h.Session);
            var bytes = source.Bytes;
            if (mode == 0) bytes = (byte[])bytes.Clone();
            if (mode == 1) bytes[0]++;
            if (mode == 2) header.Schema++;
            if (mode == 3) header.Movie++;
            if (mode == 4) header.Session = "changed";
            Assert.ThrowsExactly<InvalidOperationException>(() => source.Require(bytes));
            Assert.ThrowsExactly<InvalidOperationException>(() => _ = source.Bytes);
            Assert.ThrowsExactly<InvalidOperationException>(() => source.Require(bytes));
        }
    }
    private static CanonicalSealedSource<Header> Chunked(Header header)
        => new CanonicalSealedSource<Header>(header, _ => new byte[] { 1, 2, 3, 4, 5, 6, 7 }, h => h.Schema + ":" + h.Movie + ":" + h.Session, 3);
    [TestMethod]
    public void Ordered_export_copies_exact_chunks_and_short_tail_then_requires_whole_packet()
    {
        var header = new Header(); var source = Chunked(header); var current = source.Bytes;
        Assert.AreSame(header, source.Require(current));
        for (var offset = 0; offset < current.Length; offset += 3) {
            Assert.AreSame(header, source.ReadChunk(current, offset, out var chunk));
            CollectionAssert.AreEqual(current.AsSpan(offset, Math.Min(3, current.Length - offset)).ToArray(), chunk);
            Assert.AreNotSame(current, chunk);
        }
        Assert.AreSame(header, source.RequireCompleted(current));
    }
    [TestMethod]
    public void Tampering_with_pending_or_already_sent_bytes_never_completes_and_clears_packet()
    {
        for (var position = 0; position < 7; position++) {
            var source = Chunked(new Header()); var current = source.Bytes;
            source.ReadChunk(current, 0, out var sent);
            current[position]++;
            if (position < 3) {
                CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, sent);
                source.ReadChunk(current, 3, out _); source.ReadChunk(current, 6, out _);
                Assert.ThrowsExactly<InvalidOperationException>(() => source.RequireCompleted(current));
            } else {
                var offset = position < 6 ? 3 : 6;
                if (offset == 6) source.ReadChunk(current, 3, out _);
                Assert.ThrowsExactly<InvalidOperationException>(() => source.ReadChunk(current, offset, out _));
            }
            CollectionAssert.AreEqual(new byte[7], current);
            Assert.ThrowsExactly<InvalidOperationException>(() => _ = source.Bytes);
        }
    }
    [TestMethod]
    public void Chunk_identity_metadata_order_repeat_and_incomplete_fail_permanently()
    {
        for (var mode = 0; mode < 9; mode++) {
            var header = new Header(); var source = Chunked(header); var original = source.Bytes; var current = original;
            var offset = 0;
            if (mode == 0) current = (byte[])original.Clone();
            if (mode == 1) header.Schema++;
            if (mode == 2) header.Movie++;
            if (mode == 3) header.Session = "changed";
            if (mode == 4) offset = 3;
            if (mode == 5) { source.ReadChunk(current, 0, out _); offset = 0; }
            if (mode == 6) offset = -3;
            if (mode == 7) offset = 7;
            if (mode == 8) Assert.ThrowsExactly<InvalidOperationException>(() => source.RequireCompleted(current));
            else Assert.ThrowsExactly<InvalidOperationException>(() => source.ReadChunk(current, offset, out _));
            CollectionAssert.AreEqual(new byte[7], original);
            Assert.ThrowsExactly<InvalidOperationException>(() => source.Require(original));
        }
    }
}
