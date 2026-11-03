using System;
using System.IO;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class ResidentDirectorFence
    {
        internal const int Bytes = 200;
        internal sealed class Data
        {
            public uint Version, Edges, Functions, RequiredGaps;
        }

        // V4 tracks physical job groups and their enrolled descendants through dispatch.
        // The original function entries cover direct and indirect
        // callers; it does not prove all inlined writers, jobs or other mutators.
        // A packet cannot silently upgrade the native capability by clearing bits.
        internal static void Validate(Data data)
        {
            if (data == null || data.Version != 4 || data.Edges != 153 || data.Functions != 51 || data.RequiredGaps != 7)
                throw Error("native capability differs from the pinned producer ABI");
        }
        internal static string[] Gaps(Data data)
        {
            Validate(data);
            return new[] { "director-indirect-or-inlined-writers", "director-worker-dispatch", "director-unenrolled-mutators" };
        }
        internal static void RequireComplete(Data data)
        {
            var gaps = Gaps(data);
            if (gaps.Length != 0) throw Error(string.Join(";", gaps));
        }
        // Process-local only: these values may differ between identical worlds.
        internal readonly struct Witness
        {
            internal readonly ulong Issued, Bytes, Groups, Callbacks, Lineages, LineageCallbacks, Nested;
            internal Witness(ulong issued, ulong bytes, ulong groups, ulong callbacks, ulong lineages, ulong lineageCallbacks, ulong nested)
            { Issued = issued; Bytes = bytes; Groups = groups; Callbacks = callbacks; Lineages = lineages; LineageCallbacks = lineageCallbacks; Nested = nested; }
            internal void RequireSame(Witness other)
            {
                if (Issued != other.Issued || Bytes != other.Bytes || Groups != other.Groups || Callbacks != other.Callbacks ||
                    Lineages != other.Lineages || LineageCallbacks != other.LineageCallbacks || Nested != other.Nested)
                    throw Error("Director producer advanced within the held epoch");
            }
        }
        internal static Data Read(byte[] bytes, ulong token, ulong? started, uint thread, out Witness witness)
        {
            if (bytes == null || bytes.Length != Bytes || !BitConverter.IsLittleEndian) throw Error("native receipt length/endian differs");
            uint Word(int at) => BitConverter.ToUInt32(bytes,at);
            ulong Wide(int at) => BitConverter.ToUInt64(bytes,at);
            witness = new Witness(Wide(32), Wide(64), Wide(112), Wide(128), Wide(152), Wide(168), Wide(184));
            if (Word(0) != 0x31464452 || Word(4) != 4 || Word(8) != Bytes || Word(12) != 1
                || token == 0 || Wide(16) != token || (started.HasValue && Wide(24) != started.Value) || witness.Issued != Wide(40) || witness.Bytes < 8
                || witness.Groups != Wide(120) || witness.Callbacks != Wide(136) || Word(144) != 0 || Word(148) != 0
                || witness.Lineages != Wide(160) || witness.LineageCallbacks != Wide(176) || witness.Nested > witness.Lineages
                || Word(192) != 0 || Word(196) != 0
                || Word(72) != 0 || Word(76) != 1 || Word(80) != 1 || Word(84) != 0
                || thread == 0 || Word(88) != thread || Word(92) != thread || Word(108) != 0) throw Error("invalid held epoch, ownership or witness");
            var data = new Data { Version = Word(4), Edges = Word(96), Functions = Word(100), RequiredGaps = Word(104) };
            Validate(data); return data;
        }
        private static InvalidDataException Error(string message) => new InvalidDataException("Resident Director: " + message);
    }
}
