using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HollowKnightTAS.Core.SceneCheckpoints;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Flat, bounded data with an interned string table. Graph identity lives in
    // explicit node ordinals, never serializer $id values or process addresses.
    internal static class CanonicalCarryGraphWire
    {
        private const int Magic = 0x44434348; // HCCD
        private const int MaximumStrings = 1000000;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal static byte[] Encode(CanonicalCarryGraphData value)
        {
            if (value == null || value.Roots == null || value.Nodes == null || value.Externals == null || value.Unsupported == null
                || value.Roots.Length > CanonicalCarryGraphCodec.MaximumNodes || value.Nodes.Length > CanonicalCarryGraphCodec.MaximumNodes
                || value.Externals.Length > CanonicalCarryGraphCodec.MaximumNodes || value.Unsupported.Length > CanonicalCarryGraphCodec.MaximumValues)
                throw new InvalidDataException("Invalid carry wire graph shape or count.");
            // Deduplicate repeated field/type strings before sorting once.
            // The resulting ordinal table and every encoded byte are unchanged.
            var table = new HashSet<string>(StringComparer.Ordinal);
            void Intern(string? text) { if (text != null) table.Add(text); }
            Intern(value.Schema);
            foreach (var root in value.Roots) Intern(root.Binding);
            foreach (var external in value.Externals) { Intern(external.Binding); Intern(external.Type); }
            foreach (var issue in value.Unsupported) Intern(issue);
            var values = 0;
            void InternValue(CanonicalCarryGraphData.Value item)
            {
                if (++values > CanonicalCarryGraphCodec.MaximumValues) throw new InvalidDataException("Carry wire value count exceeds its bound.");
                Intern(item.Type); Intern(item.Text);
            }
            foreach (var node in value.Nodes)
            {
                if (node == null) throw new InvalidDataException("Carry node is null.");
                Intern(node.Type); Intern(node.Text);
                if (node.Fields != null) foreach (var field in node.Fields) { Intern(field.Name); InternValue(field.Value); }
                if (node.Items != null) foreach (var item in node.Items) InternValue(item);
            }
            // Keep the previous data codec's 8 MiB character budget, now for
            // the lossless table instead of repeatedly emitted field names.
            if (table.Count > MaximumStrings || table.Sum(text => (long)text.Length) > SceneCheckpointPacket.MaximumPayloadBytes / 4)
                throw new InvalidDataException("Carry wire string table exceeds its bound.");
            var strings = table.OrderBy(text => text, StringComparer.Ordinal).ToArray();
            var ids = strings.Select((text, index) => new { text, id = index + 1 }).ToDictionary(pair => pair.text, pair => pair.id, StringComparer.Ordinal);
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Utf8, true);
            void Require() { if (stream.Length > SceneCheckpointPacket.MaximumPayloadBytes) throw new InvalidDataException("Carry wire payload exceeds its bound."); }
            void Id(string? text) => writer.Write(text == null ? 0 : ids[text]);
            void OptionalCount(int? count) => writer.Write(count ?? -1);
            void Item(CanonicalCarryGraphData.Value item)
            {
                writer.Write(Kind(item.Kind)); Id(item.Type); Id(item.Text); writer.Write(item.Reference); Require();
            }
            writer.Write(Magic); writer.Write(value.Version); writer.Write(strings.Length);
            foreach (var text in strings)
            {
                var bytes = Utf8.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes); Require();
            }
            Id(value.Schema); writer.Write(value.Roots.Length);
            foreach (var root in value.Roots) { Id(root.Binding); writer.Write(root.Node); }
            writer.Write(value.Nodes.Length);
            foreach (var node in value.Nodes)
            {
                writer.Write(node.Id); Id(node.Type); writer.Write(NodeKind(node.Kind)); Id(node.Text);
                OptionalCount(node.Fields?.Length);
                if (node.Fields != null) foreach (var field in node.Fields) { Id(field.Name); Item(field.Value); }
                OptionalCount(node.Items?.Length);
                if (node.Items != null) foreach (var item in node.Items) Item(item);
                Require();
            }
            writer.Write(value.Externals.Length);
            foreach (var external in value.Externals) { Id(external.Binding); Id(external.Type); }
            writer.Write(value.Unsupported.Length);
            foreach (var issue in value.Unsupported) Id(issue);
            writer.Flush(); Require(); return stream.ToArray();
        }

        internal static CanonicalCarryGraphData Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > SceneCheckpointPacket.MaximumPayloadBytes)
                throw new InvalidDataException("Carry wire payload size is invalid.");
            try
            {
                using var stream = new MemoryStream(bytes, false);
                using var reader = new BinaryReader(stream, Utf8, true);
                int Count(int maximum)
                {
                    var count = reader.ReadInt32();
                    if (count < 0 || count > maximum || count > stream.Length - stream.Position)
                        throw new InvalidDataException("Carry wire count is invalid.");
                    return count;
                }
                if (reader.ReadInt32() != Magic) throw new InvalidDataException("Carry wire magic differs.");
                var version = reader.ReadInt32();
                var strings = new string[Count(MaximumStrings)]; long characters = 0;
                var usedStrings = new bool[strings.Length];
                for (var i = 0; i < strings.Length; i++)
                {
                    var size = Count(SceneCheckpointPacket.MaximumPayloadBytes); var text = Utf8.GetString(reader.ReadBytes(size));
                    characters += text.Length;
                    if (characters > SceneCheckpointPacket.MaximumPayloadBytes / 4
                        || i > 0 && StringComparer.Ordinal.Compare(strings[i - 1], text) >= 0)
                        throw new InvalidDataException("Carry wire string table is unordered, duplicated or too large.");
                    strings[i] = text;
                }
                string? Id()
                {
                    var id = reader.ReadInt32();
                    if (id < 0 || id > strings.Length) throw new InvalidDataException("Carry wire string identity is invalid.");
                    if (id != 0) usedStrings[id - 1] = true;
                    return id == 0 ? null : strings[id - 1];
                }
                var values = 0;
                CanonicalCarryGraphData.Value Item()
                {
                    if (++values > CanonicalCarryGraphCodec.MaximumValues) throw new InvalidDataException("Carry wire value count exceeds its bound.");
                    return new CanonicalCarryGraphData.Value { Kind = Kind(reader.ReadByte()), Type = Id(), Text = Id(), Reference = reader.ReadInt32() };
                }
                int? OptionalCount()
                {
                    var count = reader.ReadInt32();
                    if (count == -1) return null;
                    if (count < 0 || count > CanonicalCarryGraphCodec.MaximumValues || count > stream.Length - stream.Position)
                        throw new InvalidDataException("Carry wire optional count is invalid.");
                    return count;
                }
                var value = new CanonicalCarryGraphData { Version = version, Schema = Id() ?? throw new InvalidDataException("Carry schema is null."),
                    Roots = new CanonicalCarryGraphData.Root[Count(CanonicalCarryGraphCodec.MaximumNodes)] };
                for (var i = 0; i < value.Roots.Length; i++) value.Roots[i] = new CanonicalCarryGraphData.Root {
                    Binding = Id() ?? throw new InvalidDataException("Carry root binding is null."), Node = reader.ReadInt32() };
                value.Nodes = new CanonicalCarryGraphData.Node[Count(CanonicalCarryGraphCodec.MaximumNodes)];
                for (var i = 0; i < value.Nodes.Length; i++)
                {
                    var node = new CanonicalCarryGraphData.Node { Id = reader.ReadInt32(),
                        Type = Id() ?? throw new InvalidDataException("Carry node type is null."), Kind = NodeKind(reader.ReadByte()), Text = Id() };
                    var fields = OptionalCount();
                    if (fields.HasValue)
                    {
                        node.Fields = new CanonicalCarryGraphData.Field[fields.Value];
                        for (var j = 0; j < node.Fields.Length; j++) node.Fields[j] = new CanonicalCarryGraphData.Field {
                            Name = Id() ?? throw new InvalidDataException("Carry field name is null."), Value = Item() };
                    }
                    var items = OptionalCount();
                    if (items.HasValue)
                    {
                        node.Items = new CanonicalCarryGraphData.Value[items.Value];
                        for (var j = 0; j < node.Items.Length; j++) node.Items[j] = Item();
                    }
                    value.Nodes[i] = node;
                }
                value.Externals = new CanonicalCarryGraphData.External[Count(CanonicalCarryGraphCodec.MaximumNodes)];
                for (var i = 0; i < value.Externals.Length; i++) value.Externals[i] = new CanonicalCarryGraphData.External {
                    Binding = Id() ?? throw new InvalidDataException("Carry external binding is null."),
                    Type = Id() ?? throw new InvalidDataException("Carry external type is null.") };
                value.Unsupported = new string[Count(CanonicalCarryGraphCodec.MaximumValues)];
                for (var i = 0; i < value.Unsupported.Length; i++) value.Unsupported[i] = Id() ?? throw new InvalidDataException("Carry gap is null.");
                // Every wire integer has fixed little-endian width, every tag
                // has one encoding, and strict UTF-8 plus ordinal unique sorting
                // fixes the string bytes and IDs. Encode interns exactly the
                // strings reached by Id, so completeness is the remaining
                // canonical condition. Preserve the original full acceptance
                // set without allocating and writing the same packet again.
                if (stream.Position != stream.Length || usedStrings.Any(used => !used))
                    throw new InvalidDataException("Carry wire payload is trailing or noncanonical.");
                return value;
            }
            catch (Exception error) when (error is EndOfStreamException || error is DecoderFallbackException || error is OverflowException)
            { throw new InvalidDataException("Carry wire payload is truncated or invalid.", error); }
        }
        private static byte Kind(string value) => value == "null" ? (byte)0 : value == "scalar" ? (byte)1 : value == "value" ? (byte)2
            : value == "node" ? (byte)3 : value == "external" ? (byte)4 : value == "unsupported" ? (byte)5
            : throw new InvalidDataException("Unknown carry value kind.");
        private static string Kind(byte value) => value == 0 ? "null" : value == 1 ? "scalar" : value == 2 ? "value"
            : value == 3 ? "node" : value == 4 ? "external" : value == 5 ? "unsupported" : throw new InvalidDataException("Unknown carry value kind.");
        private static byte NodeKind(string value) => value == "object" ? (byte)0 : value == "array" ? (byte)1 : value == "boxed-scalar" ? (byte)2
            : value == "ease-callback" ? (byte)3
            : throw new InvalidDataException("Unknown carry node kind.");
        private static string NodeKind(byte value) => value == 0 ? "object" : value == 1 ? "array" : value == 2 ? "boxed-scalar"
            : value == 3 ? "ease-callback"
            : throw new InvalidDataException("Unknown carry node kind.");
    }
}
