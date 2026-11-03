using System;
using System.Globalization;
using System.IO;
using System.Text;
using Ref = HollowKnightTAS.Runtime.FullRun.ResidentManagedTypeCatalog.TypeReference;
using Statics = HollowKnightTAS.Runtime.FullRun.ResidentStaticRoots;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class ResidentStaticManifestWire
    {
        private const int Magic = 0x32545352, Limit = 8 * 1024 * 1024;
        internal static string SlotKey(string key, int token) => "resident-static/" + key + "/" + token.ToString("x8", CultureInfo.InvariantCulture);
        internal static byte[] Encode(Statics.Data data)
        {
            using var w = new ResidentMetadataWire.Writer(Limit); var b = w.Out;
            void Type(Ref t, int depth)
            {
                if (t == null || depth > 32 || t.Arguments == null || t.Arguments.Length > 64) throw new InvalidDataException("Static type reference exceeds its bound.");
                w.Text(t.Kind); w.Text(t.Name); w.Text(t.Assembly); w.Text(t.Module); b.Write(t.Token); b.Write(t.Rank); b.Write(t.Position);
                b.Write(t.SzArray); b.Write(t.Element != null); if (t.Element != null) Type(t.Element, depth + 1);
                b.Write(t.Arguments.Length); foreach (var arg in t.Arguments) Type(arg, depth + 1);
            }
            b.Write(Magic); b.Write(data.Version); w.Text(data.RuntimeAbi); b.Write(data.Modules.Length);
            foreach (var module in data.Modules) { w.Text(module.Assembly); w.Text(module.Module); w.Text(module.Ownership); }
            b.Write(data.Types.Length);
            foreach (var row in data.Types) {
                Type(row.Type, 0); var shortKey = row.Key == row.Type.Module + "/" + row.Type.Name;
                b.Write(shortKey); if (!shortKey) w.Text(row.Key); w.Text(row.Initialization);
                b.Write(row.Fields.Length);
                foreach (var field in row.Fields) {
                    if (field.Key != SlotKey(row.Key, field.Token)) throw new InvalidDataException("Static field binding differs from its declaring owner and token.");
                    b.Write(field.Token); b.Write(field.Attributes); w.Text(field.Name); w.Text(field.Signature); w.Text(field.Storage);
                }
            }
            b.Write(data.Unsupported.Length); foreach (var gap in data.Unsupported) w.Text(gap);
            return w.Finish();
        }
        internal static Statics.Data Decode(byte[] bytes)
        {
            try {
                using var r = new ResidentMetadataWire.Reader(bytes, Limit); var b = r.In;
                if (b.ReadInt32() != Magic) throw new InvalidDataException("Unknown resident static manifest.");
                var nodes = 0; long keyChars = 0;
                Ref Type(int depth)
                {
                    if (depth > 32 || ++nodes > 200000) throw new InvalidDataException("Static type reference exceeds its bound.");
                    var t = new Ref { Kind = r.Text(), Name = r.Text(), Assembly = r.Text(), Module = r.Text(),
                        Token = b.ReadInt32(), Rank = b.ReadInt32(), Position = b.ReadInt32(), SzArray = r.Bit() };
                    if (r.Bit()) t.Element = Type(depth + 1);
                    t.Arguments = new Ref[r.Count(64, 34)];
                    for (var j = 0; j < t.Arguments.Length; j++) t.Arguments[j] = Type(depth + 1);
                    return t;
                }
                var data = new Statics.Data { Version = b.ReadInt32(), RuntimeAbi = r.Text(), Modules = new Statics.ModuleData[r.Count(4096,12)] };
                for (var i = 0; i < data.Modules.Length; i++) data.Modules[i] = new Statics.ModuleData { Assembly = r.Text(), Module = r.Text(), Ownership = r.Text() };
                data.Types = new Statics.TypeData[r.Count(100000,43)]; var totalFields = 0;
                for (var i = 0; i < data.Types.Length; i++) {
                    var row = data.Types[i] = new Statics.TypeData { Type = Type(0) };
                    row.Key = r.Bit() ? row.Type.Module + "/" + row.Type.Name : r.Text();
                    row.Initialization = r.Text(); row.Fields = new Statics.FieldData[r.Count(100000 - totalFields,20)];
                    totalFields += row.Fields.Length;
                    if (row.Key.Length > 16384 || (keyChars += ((long)row.Key.Length + 25) * (row.Fields.Length + 1)) > 32L * 1024 * 1024)
                        throw new InvalidDataException("Static binding expansion exceeds its bound.");
                    for (var j = 0; j < row.Fields.Length; j++) {
                        var token = b.ReadInt32();
                        row.Fields[j] = new Statics.FieldData { Key = SlotKey(row.Key, token), Token = token, Attributes = b.ReadInt32(),
                            Name = r.Text(), Signature = r.Text(), Storage = r.Text() };
                    }
                }
                data.Unsupported = new string[r.Count(100000,4)];
                for (var i = 0; i < data.Unsupported.Length; i++) data.Unsupported[i] = r.Text();
                r.End(); return data;
            } catch (Exception e) when (e is EndOfStreamException || e is DecoderFallbackException) {
                throw new InvalidDataException("Malformed resident static manifest.", e);
            }
        }
    }
}
