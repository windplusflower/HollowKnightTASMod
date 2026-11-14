using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Ref = HollowKnightTAS.Runtime.FullRun.ResidentManagedTypeCatalog.TypeReference;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Deterministic first-use string IDs. A negative ID introduces its exact
    // UTF-8 byte length; a nonnegative ID can refer only to a prior string.
    internal static class ResidentMetadataWire
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        internal sealed class Writer : IDisposable
        {
            private readonly MemoryStream buffer = new MemoryStream();
            private readonly Dictionary<string, int> ids = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly int limit;
            internal readonly BinaryWriter Out;
            internal Writer(int limit) { this.limit = limit; Out = new BinaryWriter(buffer, Utf8, true); }
            internal void Text(string value)
            {
                if (ids.TryGetValue(value, out var id)) { Out.Write(id); return; }
                var length = Utf8.GetByteCount(value);
                if (length > 262144 || ids.Count >= 1000000 || buffer.Length + length + 4 > limit)
                    throw new InvalidDataException("Resident metadata string table exceeds its bound.");
                ids.Add(value, ids.Count); Out.Write(~length); Out.Write(Utf8.GetBytes(value));
            }
            internal void Type(Ref t, int depth = 0)
            {
                if (t == null || depth > 32 || t.Arguments == null || t.Arguments.Length > 64) throw new InvalidDataException("Static type reference exceeds its bound.");
                Text(t.Kind); Text(t.Name); Text(t.Assembly); Text(t.Module); Out.Write(t.Token); Out.Write(t.Rank); Out.Write(t.Position);
                Out.Write(t.SzArray); Out.Write(t.Element != null); if (t.Element != null) Type(t.Element, depth + 1);
                Out.Write(t.Arguments.Length); foreach (var arg in t.Arguments) Type(arg, depth + 1);
            }
            internal byte[] Finish()
            {
                Out.Flush();
                if (buffer.Length > limit) throw new InvalidDataException("Resident metadata domain exceeds its bound.");
                return buffer.ToArray();
            }
            public void Dispose() { Out.Dispose(); buffer.Dispose(); }
        }
        internal sealed class Reader : IDisposable
        {
            private readonly MemoryStream buffer;
            private readonly List<string> table = new List<string>();
            private readonly HashSet<string> declared = new HashSet<string>(StringComparer.Ordinal);
            internal readonly BinaryReader In;
            internal Reader(byte[] bytes, int limit)
            {
                if (bytes == null || bytes.Length == 0 || bytes.Length > limit) throw new InvalidDataException("Resident metadata domain size differs.");
                buffer = new MemoryStream(bytes, false); In = new BinaryReader(buffer, Utf8, true);
            }
            internal string Text()
            {
                var id = In.ReadInt32();
                if (id >= 0) return id < table.Count ? table[id] : throw new InvalidDataException("Resident metadata string reference is forward or missing.");
                var length = ~id;
                if (length > 262144 || length > buffer.Length - buffer.Position || table.Count >= 1000000)
                    throw new InvalidDataException("Resident metadata string length exceeds its bound.");
                var value = Utf8.GetString(In.ReadBytes(length));
                if (!declared.Add(value)) throw new InvalidDataException("Resident metadata string is redeclared.");
                table.Add(value); return value;
            }
            internal int Count(int maximum, int minimumBytes)
            {
                var n = In.ReadInt32();
                if (n < 0 || n > maximum || (long)n * minimumBytes > buffer.Length - buffer.Position)
                    throw new InvalidDataException("Resident metadata count exceeds its bound.");
                return n;
            }
            private int typeNodes;
            internal Ref Type(int depth = 0)
            {
                if (depth > 32 || ++typeNodes > 200000) throw new InvalidDataException("Static type reference exceeds its bound.");
                var t = new Ref { Kind = Text(), Name = Text(), Assembly = Text(), Module = Text(),
                    Token = In.ReadInt32(), Rank = In.ReadInt32(), Position = In.ReadInt32(), SzArray = Bit() };
                if (Bit()) t.Element = Type(depth + 1);
                t.Arguments = new Ref[Count(64, 34)];
                for (var j = 0; j < t.Arguments.Length; j++) t.Arguments[j] = Type(depth + 1);
                return t;
            }
            internal bool Bit() { var b = In.ReadByte(); return b <= 1 ? b != 0 : throw new InvalidDataException("Resident metadata boolean is not canonical."); }
            internal void End() { if (buffer.Position != buffer.Length) throw new InvalidDataException("Resident metadata has trailing bytes."); }
            public void Dispose() { In.Dispose(); buffer.Dispose(); }
        }
    }
}
