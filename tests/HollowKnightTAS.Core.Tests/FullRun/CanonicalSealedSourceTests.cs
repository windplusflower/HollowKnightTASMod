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
}
