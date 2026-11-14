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
        private const int ResidentMagic = 0x32474352; // RCG2: graph uses resident-only tags
        private const int CompactResidentMagic = 0x33474352; // RCG3: omit absent value columns
        private const int LayoutResidentMagic = 0x34474352; // RCG4: reuse a type's field layout
        private const int OrdinalResidentMagic = 0x35474352; // RCG5: bounded, minimal-width signed ordinals
        private const int FieldsResidentMagic = 0x36474352; // RCG6: compact value fields, explicit reference slots
        private const int MaximumStrings = 1000000;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal static byte[] Encode(CanonicalCarryGraphData value)
        {
            if (value == null || value.Roots == null || value.Nodes == null || value.Externals == null || value.Unsupported == null
                || value.Roots.Length > CanonicalCarryGraphCodec.MaximumNodes || value.Nodes.Length > ((value.WireRevision == 5 || value.WireRevision == 6) ? CanonicalCarryGraphCodec.MaximumPackedNodes : value.WireRevision == 3 || value.WireRevision == 4 ? CanonicalCarryGraphCodec.MaximumResidentNodes : CanonicalCarryGraphCodec.MaximumNodes)
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
            var valueLimit = (value.WireRevision == 5 || value.WireRevision == 6) ? CanonicalCarryGraphCodec.MaximumPackedValues : CanonicalCarryGraphCodec.MaximumValues;
            var values = 0;
            void InternValue(CanonicalCarryGraphData.Value item)
            {
                if (++values > valueLimit) throw new InvalidDataException("Carry wire value count exceeds its bound.");
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
            if (table.Count > MaximumStrings || table.Sum(text => (long)text.Length) > SceneCheckpointPacket.MaximumPayloadBytes)
                throw new InvalidDataException("Carry wire string table exceeds its bound.");
            var strings = table.OrderBy(text => text, StringComparer.Ordinal).ToArray();
            var ids = strings.Select((text, index) => new { text, id = index + 1 }).ToDictionary(pair => pair.text, pair => pair.id, StringComparer.Ordinal);
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Utf8, true);
            void Require() { if (stream.Length > SceneCheckpointPacket.MaximumPayloadBytes) throw new InvalidDataException("Carry wire payload exceeds its bound."); }
            var packedFields = value.WireRevision == 6;
            var packed = packedFields || value.WireRevision == 5;
            void Integer(int number) {
                if (!packed) { writer.Write(number); return; }
                uint bits = unchecked((uint)((number << 1) ^ (number >> 31)));
                while (bits >= 128) { writer.Write((byte)((bits & 127) | 128)); bits >>= 7; }
                writer.Write((byte)bits);
            }
            void Id(string? text) => Integer(text == null ? 0 : ids[text]);
            void OptionalCount(int? count) => Integer(count ?? -1);
            var extended = false;
            var compact = packed || value.WireRevision == 3 || value.WireRevision == 4;
            var layoutWire = packed || value.WireRevision == 4;
            if (compact ? value.Version != 2 : value.WireRevision != 0) throw new InvalidDataException("Unsupported carry wire revision.");
            void Item(CanonicalCarryGraphData.Value item)
            {
                var tag = Kind(item.Kind); extended |= tag > 5;
                if (compact) {
                    writer.Write((byte)(tag | (item.Type == null ? 0 : 16) | (item.Text == null ? 0 : 32) | (item.Reference == 0 ? 0 : 64)));
                    if (item.Type != null) Id(item.Type);
                    if (item.Text != null) Id(item.Text);
                    if (item.Reference != 0) Integer(item.Reference);
                    Require();
                    return;
                }
                writer.Write(tag); Id(item.Type); Id(item.Text); Integer(item.Reference); Require();
            }
            writer.Write(Magic); writer.Write(value.Version); writer.Write(strings.Length);
            foreach (var text in strings)
            {
                var bytes = Utf8.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes); Require();
            }
            Id(value.Schema); writer.Write(value.Roots.Length);
            foreach (var root in value.Roots) { Id(root.Binding); Integer(root.Node); }
            writer.Write(value.Nodes.Length);
            var layouts = new Dictionary<string, CanonicalCarryGraphData.Field[]>(StringComparer.Ordinal);
            foreach (var node in value.Nodes)
            {
                var tag = NodeKind(node.Kind); extended |= tag > 4;
                Integer(node.Id); Id(node.Type); writer.Write(tag); Id(node.Text);
                if ((node.Kind == "primitive-vector" || node.Kind == "value-vector")) {
                    if (value.Version != 2 || node.Packed == null || node.Fields != null || node.Items != null || node.Text != null
                        || node.Packed.Length > SceneCheckpointPacket.MaximumPayloadBytes - stream.Length - sizeof(int))
                        throw new InvalidDataException("Invalid resident primitive vector payload.");
                    writer.Write(node.Packed.Length); writer.Write(node.Packed);
                    continue;
                }
                var fieldPayload = node.Kind == "object" || node.Kind == "native-owner" || node.Kind == "destroyed-unity";
                if (packedFields && fieldPayload) {
                    writer.Write(node.Packed != null);
                    if (node.Packed != null) { writer.Write(node.Packed.Length); writer.Write(node.Packed); Require(); }
                } else if (node.Packed != null) throw new InvalidDataException("Unexpected field payload.");
                var reuseLayout = false;
                if (layoutWire && node.Fields != null) {
                    if (layouts.TryGetValue(node.Type, out var saved)) {
                        reuseLayout = saved.Length == node.Fields.Length;
                        for (var index = 0; reuseLayout && index < saved.Length; index++)
                            reuseLayout = saved[index].Name == node.Fields[index].Name;
                    } else layouts.Add(node.Type, node.Fields);
                }
                OptionalCount(reuseLayout ? -2 : node.Fields?.Length);
                if (node.Fields != null) foreach (var field in node.Fields) {
                    if (!reuseLayout) Id(field.Name);
                    Item(field.Value);
                }
                OptionalCount(node.Items?.Length);
                if (node.Items != null) foreach (var item in node.Items) Item(item);
                Require();
            }
            writer.Write(value.Externals.Length);
            foreach (var external in value.Externals) { Id(external.Binding); Id(external.Type); }
            writer.Write(value.Unsupported.Length);
            foreach (var issue in value.Unsupported) Id(issue);
            if (extended || compact) { stream.Position = 0; writer.Write(packedFields ? FieldsResidentMagic : packed ? OrdinalResidentMagic : layoutWire ? LayoutResidentMagic : compact ? CompactResidentMagic : ResidentMagic); }
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
                var magic = reader.ReadInt32();
                var packedFields = magic == FieldsResidentMagic;
                var packed = packedFields || magic == OrdinalResidentMagic;
                var valueLimit = packed ? CanonicalCarryGraphCodec.MaximumPackedValues : CanonicalCarryGraphCodec.MaximumValues;
                int Integer() {
                    if (!packed) return reader.ReadInt32();
                    uint bits = 0;
                    for (int shift = 0; shift <= 28; shift += 7) {
                        var part = reader.ReadByte();
                        if (shift == 28 && part > 15 || shift != 0 && part == 0)
                            throw new InvalidDataException("Invalid compact ordinal.");
                        bits |= (uint)(part & 127) << shift;
                        if (part < 128) return unchecked((int)((bits >> 1) ^ (uint)-(int)(bits & 1)));
                    }
                    throw new InvalidDataException("Unterminated compact ordinal.");
                }
                var layoutWire = packed || magic == LayoutResidentMagic;
                var compact = layoutWire || magic == CompactResidentMagic;
                if (!compact && magic != Magic && magic != ResidentMagic) throw new InvalidDataException("Carry wire magic differs.");
                var extended = false;
                byte ReadTag(byte oldMaximum) {
                    var tag = reader.ReadByte();
                    if (tag > oldMaximum) {
                        if (magic == Magic) throw new InvalidDataException("Resident tag in a legacy carry graph.");
                        extended = true;
                    }
                    return tag;
                }
                var version = reader.ReadInt32();
                if (compact && version != 2) throw new InvalidDataException("Compact resident graph version differs.");
                var strings = new string[Count(MaximumStrings)]; long characters = 0;
                var usedStrings = new bool[strings.Length];
                for (var i = 0; i < strings.Length; i++)
                {
                    var size = Count(SceneCheckpointPacket.MaximumPayloadBytes); var text = Utf8.GetString(reader.ReadBytes(size));
                    characters += text.Length;
                    if (characters > SceneCheckpointPacket.MaximumPayloadBytes
                        || i > 0 && StringComparer.Ordinal.Compare(strings[i - 1], text) >= 0)
                        throw new InvalidDataException("Carry wire string table is unordered, duplicated or too large.");
                    strings[i] = text;
                }
                string? Id()
                {
                    var id = Integer();
                    if (id < 0 || id > strings.Length) throw new InvalidDataException("Carry wire string identity is invalid.");
                    if (id != 0) usedStrings[id - 1] = true;
                    return id == 0 ? null : strings[id - 1];
                }
                var values = 0;
                CanonicalCarryGraphData.Value Item()
                {
                    if (++values > valueLimit) throw new InvalidDataException("Carry wire value count exceeds its bound.");
                    if (compact) {
                        var tag = reader.ReadByte();
                        if ((tag & 128) != 0) throw new InvalidDataException("Unknown compact value flag.");
                        var item = new CanonicalCarryGraphData.Value { Kind = Kind((byte)(tag & 15)) };
                        if ((tag & 16) != 0) item.Type = Id() ?? throw new InvalidDataException("Empty compact type column.");
                        if ((tag & 32) != 0) item.Text = Id() ?? throw new InvalidDataException("Empty compact text column.");
                        if ((tag & 64) != 0 && (item.Reference = Integer()) == 0)
                            throw new InvalidDataException("Empty compact reference column.");
                        return item;
                    }
                    return new CanonicalCarryGraphData.Value { Kind = Kind(ReadTag(5)), Type = Id(), Text = Id(), Reference = reader.ReadInt32() };
                }
                int? OptionalCount(bool allowLayout = false)
                {
                    var count = Integer();
                    if (count == -1) return null;
                    if (allowLayout && count == -2) return -2;
                    if (count < 0 || count > valueLimit || count > stream.Length - stream.Position)
                        throw new InvalidDataException("Carry wire optional count is invalid.");
                    return count;
                }
                var value = new CanonicalCarryGraphData { Version = version, WireRevision = packedFields ? 6 : packed ? 5 : layoutWire ? 4 : compact ? 3 : 0, Schema = Id() ?? throw new InvalidDataException("Carry schema is null."),
                    Roots = new CanonicalCarryGraphData.Root[Count(CanonicalCarryGraphCodec.MaximumNodes)] };
                for (var i = 0; i < value.Roots.Length; i++) value.Roots[i] = new CanonicalCarryGraphData.Root {
                    Binding = Id() ?? throw new InvalidDataException("Carry root binding is null."), Node = Integer() };
                value.Nodes = new CanonicalCarryGraphData.Node[Count(packed ? CanonicalCarryGraphCodec.MaximumPackedNodes : compact ? CanonicalCarryGraphCodec.MaximumResidentNodes : CanonicalCarryGraphCodec.MaximumNodes)];
                var layouts = new Dictionary<string, CanonicalCarryGraphData.Field[]>(StringComparer.Ordinal);
                for (var i = 0; i < value.Nodes.Length; i++)
                {
                    var node = new CanonicalCarryGraphData.Node { Id = Integer(),
                        Type = Id() ?? throw new InvalidDataException("Carry node type is null."), Kind = NodeKind(ReadTag(4)), Text = Id() };
                    if ((node.Kind == "primitive-vector" || node.Kind == "value-vector")) {
                        if (version != 2 || node.Text != null) throw new InvalidDataException("Unexpected primitive vector tag.");
                        node.Packed = reader.ReadBytes(Count(SceneCheckpointPacket.MaximumPayloadBytes));
                        value.Nodes[i] = node;
                        continue;
                    }
                    if (packedFields && (node.Kind == "object" || node.Kind == "native-owner" || node.Kind == "destroyed-unity")) {
                        var present = reader.ReadByte();
                        if (present > 1) throw new InvalidDataException("Invalid packed field flag.");
                        if (present != 0) node.Packed = reader.ReadBytes(Count(SceneCheckpointPacket.MaximumPayloadBytes));
                    }
                    var fields = OptionalCount(layoutWire);
                    if (fields.HasValue)
                    {
                        var reuseLayout = fields == -2;
                        layouts.TryGetValue(node.Type, out var saved);
                        if (reuseLayout) {
                            if (saved == null || saved.Length > stream.Length - stream.Position || saved.Length > valueLimit - values)
                                throw new InvalidDataException("Invalid compact field layout reference.");
                            fields = saved.Length;
                        }
                        node.Fields = new CanonicalCarryGraphData.Field[fields.Value];
                        for (var j = 0; j < node.Fields.Length; j++) node.Fields[j] = new CanonicalCarryGraphData.Field {
                            Name = reuseLayout ? saved![j].Name : Id() ?? throw new InvalidDataException("Carry field name is null."), Value = Item() };
                        if (layoutWire && !reuseLayout) {
                            if (saved == null) layouts.Add(node.Type, node.Fields);
                            else if (saved.Select(field => field.Name).SequenceEqual(node.Fields.Select(field => field.Name)))
                                throw new InvalidDataException("Repeated compact field layout.");
                        }
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
                if ((!compact && extended != (magic == ResidentMagic)) || stream.Position != stream.Length || usedStrings.Any(used => !used))
                    throw new InvalidDataException("Carry wire payload is trailing or noncanonical.");
                return value;
            }
            catch (Exception error) when (error is EndOfStreamException || error is DecoderFallbackException || error is OverflowException)
            { throw new InvalidDataException("Carry wire payload is truncated or invalid.", error); }
        }
        private static byte Kind(string value) => value == "null" ? (byte)0 : value == "scalar" ? (byte)1 : value == "value" ? (byte)2
            : value == "node" ? (byte)3 : value == "external" ? (byte)4 : value == "unsupported" ? (byte)5
            : value == "instance-id" ? (byte)6
            : throw new InvalidDataException("Unknown carry value kind.");
        private static string Kind(byte value) => value == 0 ? "null" : value == 1 ? "scalar" : value == 2 ? "value"
            : value == 3 ? "node" : value == 4 ? "external" : value == 5 ? "unsupported" : value == 6 ? "instance-id" : throw new InvalidDataException("Unknown carry value kind.");
        private static byte NodeKind(string value) => value == "object" ? (byte)0 : value == "array" ? (byte)1 : value == "boxed-scalar" ? (byte)2
            : value == "ease-callback" ? (byte)3
            : value == "boss-challenge-callback" ? (byte)4
            : value == "destroyed-unity" ? (byte)5 : value == "native-owner" ? (byte)6 : value == "delegate" ? (byte)7 : value == "primitive-vector" ? (byte)8 : value == "value-vector" ? (byte)9
            : throw new InvalidDataException("Unknown carry node kind.");
        private static string NodeKind(byte value) => value == 0 ? "object" : value == 1 ? "array" : value == 2 ? "boxed-scalar"
            : value == 3 ? "ease-callback"
            : value == 4 ? "boss-challenge-callback"
            : value == 5 ? "destroyed-unity" : value == 6 ? "native-owner" : value == 7 ? "delegate" : value == 8 ? "primitive-vector" : value == 9 ? "value-vector"
            : throw new InvalidDataException("Unknown carry node kind.");
    }
}
