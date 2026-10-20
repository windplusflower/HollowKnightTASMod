using System;
using System.IO;
using System.Linq;
using System.Text;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalCarryWireCanonicalityTests
{
    private static bool Accepted(byte[] bytes, bool legacy)
    {
        try {
            var data = legacy ? LegacyCanonicalCarryGraphWire.Decode(bytes) : CanonicalCarryGraphWire.Decode(bytes);
            CollectionAssert.AreEqual(bytes, CanonicalCarryGraphWire.Encode(data), "Accepted packet must reencode byte for byte.");
            return true;
        } catch (InvalidDataException) { return false; }
    }
    private static void Compare(byte[] bytes)
        => Assert.AreEqual(Accepted(bytes, true), Accepted(bytes, false), "Reader acceptance differs: " + Convert.ToHexString(bytes));
    private static CanonicalCarryGraphData Empty() => new() {
        Schema = "", Roots = Array.Empty<CanonicalCarryGraphData.Root>(), Nodes = Array.Empty<CanonicalCarryGraphData.Node>(),
        Externals = Array.Empty<CanonicalCarryGraphData.External>(), Unsupported = Array.Empty<string>() };

    [TestMethod]
    public void Every_single_byte_substitution_and_truncation_matches_original_reader()
    {
        var graph = Empty();
        graph.Schema = "\0é😀\ud7ff\ue000\ufeff";
        graph.Roots = new[] { new CanonicalCarryGraphData.Root { Binding = "r", Node = 1 } };
        graph.Nodes = new[] { new CanonicalCarryGraphData.Node { Id = 1, Type = "t", Kind = "object", Fields = new[] {
            new CanonicalCarryGraphData.Field { Name = "", Value = new CanonicalCarryGraphData.Value { Kind = "scalar", Type = "t", Text = "", Reference = 0 } } },
            Items = new[] { new CanonicalCarryGraphData.Value { Kind = "node", Type = "t", Reference = 1 } } } };
        var wire = CanonicalCarryGraphWire.Encode(graph);
        for (var i = 0; i < wire.Length; i++) {
            for (var b = 0; b <= 255; b++) { var changed = (byte[])wire.Clone(); changed[i] = (byte)b; Compare(changed); }
            Compare(wire.Take(i).ToArray());
        }
        Compare(wire.Concat(new byte[] { 0 }).ToArray());
    }

    [TestMethod]
    public void Random_valid_grammar_including_semantically_invalid_graphs_matches_original_reader()
    {
        var random = new Random(1872026);
        var text = new[] { "", "a", "z", "é", "😀", "\0", "\ufeff", "01", "+1", "-1" };
        string Any() => text[random.Next(text.Length)];
        string? Optional() => random.Next(4) == 0 ? null : Any();
        CanonicalCarryGraphData.Value Item() => new() { Kind = new[] { "null", "scalar", "value", "node", "external", "unsupported" }[random.Next(6)],
            Type = Optional(), Text = Optional(), Reference = random.Next(-3, 10) };
        for (var n = 0; n < 1000; n++) {
            var graph = Empty(); graph.Version = random.Next(-3, 8); graph.Schema = Any();
            graph.Roots = Enumerable.Range(0, random.Next(8)).Select(_ => new CanonicalCarryGraphData.Root { Binding = Any(), Node = random.Next(-2, 20) }).ToArray();
            graph.Nodes = Enumerable.Range(0, random.Next(15)).Select(_ => new CanonicalCarryGraphData.Node {
                Id = random.Next(-3, 20), Type = Any(), Kind = new[] { "object", "array", "boxed-scalar" }[random.Next(3)], Text = Optional(),
                Fields = random.Next(3) == 0 ? null : Enumerable.Range(0, random.Next(8)).Select(_ => new CanonicalCarryGraphData.Field { Name = Any(), Value = Item() }).ToArray(),
                Items = random.Next(3) == 0 ? null : Enumerable.Range(0, random.Next(8)).Select(_ => Item()).ToArray() }).ToArray();
            graph.Externals = Enumerable.Range(0, random.Next(5)).Select(_ => new CanonicalCarryGraphData.External { Binding = Any(), Type = Any() }).ToArray();
            graph.Unsupported = Enumerable.Range(0, random.Next(5)).Select(_ => Any()).ToArray();
            var bytes = CanonicalCarryGraphWire.Encode(graph); Compare(bytes);
            for (var mutation = 0; mutation < 8; mutation++) { var changed = (byte[])bytes.Clone(); changed[random.Next(changed.Length)] ^= (byte)(1 << random.Next(8)); Compare(changed); }
        }
    }

    // Raw packets exercise conditions that Encode cannot produce. Graph
    // semantics (duplicate roots/fields, IDs, scalar text/type compatibility)
    // deliberately remain the responsibility of PrepareOwnedGraph in both readers.
    private static byte[] Raw(byte[][] strings, int schema, int fields = -1, int items = -1, byte nodeKind = 0, byte itemKind = 0)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(0x44434348); w.Write(1); w.Write(strings.Length);
        foreach (var s in strings) { w.Write(s.Length); w.Write(s); }
        w.Write(schema); w.Write(0); w.Write(1);
        w.Write(1); w.Write(schema); w.Write(nodeKind); w.Write(0); w.Write(fields);
        if (fields > 0) { w.Write(schema); w.Write(itemKind); w.Write(0); w.Write(0); w.Write(0); }
        w.Write(items);
        if (items > 0) { w.Write(itemKind); w.Write(0); w.Write(0); w.Write(0); }
        w.Write(0); w.Write(0); return stream.ToArray();
    }

    [TestMethod]
    public void Structural_malformations_and_unused_string_table_entries_match_original_reader()
    {
        byte[] U(string s) => Encoding.UTF8.GetBytes(s);
        foreach (var strings in new[] {
            new[] { U("a"), U("b") }, new[] { U("b"), U("a") }, new[] { U("a"), U("a") },
            new[] { new byte[] { 0xc0, 0x80 } }, new[] { new byte[] { 0xed, 0xa0, 0x80 } },
            new[] { new byte[] { 0xf4, 0x90, 0x80, 0x80 } }, new[] { U(""), U("a") }, new[] { U("a") } })
        foreach (var schema in new[] { -1, 0, 1, 2, int.MaxValue })
        foreach (var counts in new[] { (-1, -1), (0, 0), (-2, -1), (1, 1), (int.MaxValue, -1) })
            Compare(Raw(strings, schema, counts.Item1, counts.Item2));
        foreach (var tag in new byte[] { 3, 5, 6, 255 }) {
            Compare(Raw(new[] { U("a") }, 1, nodeKind: tag));
            Compare(Raw(new[] { U("a") }, 1, items: 1, itemKind: tag));
        }
    }
}
