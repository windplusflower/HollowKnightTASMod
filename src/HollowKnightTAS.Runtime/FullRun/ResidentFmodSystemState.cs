using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Read-only system directory, anchored independently by AudioManager's
    // FMOD::System handle. These fields do not stand in for the channel/DSP
    // graph or physical output. No FMOD function or lazy getter runs here.
    internal static class ResidentFmodSystemState
    {
        private const int MaximumSystems = 1024;
        internal sealed class Data
        {
            public bool CellPresent, ListPresent;
            public int ManagerRow = -1;
            public Row[] Rows = Array.Empty<Row>();
        }

        internal sealed class Row
        {
            public uint Index, Flags, DspBlockSize, DspBufferSize;
            public ulong DspClock;
            // Clock timestamps are witnessed words in FMOD's own millisecond
            // domain. Restoring them needs a clock-domain mapping.
            public uint ClockTimestamp, LastTimestamp, LastDeviceCheck;
            public bool Initialized, PluginsLoaded;
            public int Output, Emulated, Software, OutputType, OutputFormat, OutputRate;
            public int InputChannels, OutputChannels, SoftwareChannels, HardwareChannels;
        }

        internal static Data Capture(IntPtr engine, IntPtr managerSystem, Func<IntPtr,int,byte[]> read, Action<IntPtr,uint,IntPtr>? observeSystem = null)
        {
            // Full extents of SystemI::{getInstance,validate,getDSPClock}. The
            // native leaves are independently exercised on multi-system lists.
            Code(0x10b3b60, 94, "32da5ec85bf05d8ef5e3afeebf93616c0a536e94176633f813869302a7b66c1c");
            Code(0x10b9780, 73, "4e6e0ad6e7aa0ab8a3121f58b15dfc9f4a5bd5587f00608aa5780363bec2be1e");
            Code(0x10b3750, 30, "4c51396a7b2c6640e3322295ef4e2f2bf24fff8fe9cbdc352cd2bf79e1e11d40");
            void Code(int rva, int length, string expected) {
                using var sha = SHA256.Create();
                var bytes = read(IntPtr.Add(engine,rva), length);
                if (!string.Equals(BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", ""), expected, StringComparison.OrdinalIgnoreCase))
                    throw Error("system directory or clock getter ABI differs");
            }

            var cell = M.Ptr(read(IntPtr.Add(engine, 0x198ede0), 8), 0);
            var list = cell == IntPtr.Zero ? IntPtr.Zero : M.Ptr(read(cell, 8), 0);
            var result = new Data { CellPresent = cell != IntPtr.Zero, ListPresent = list != IntPtr.Zero };
            var rows = new List<Row>();
            if (result.ListPresent) {
                var head = IntPtr.Add(list, 8);
                var ends = read(head, 16);
                var previous = head;
                var current = M.Ptr(ends, 0);
                var nodes = new HashSet<IntPtr>();
                var aliases = new Dictionary<IntPtr,int>();
                int Alias(IntPtr address) {
                    if (address == IntPtr.Zero) return 0;
                    if (!aliases.TryGetValue(address, out var alias)) aliases.Add(address, alias = aliases.Count + 1);
                    return alias;
                }
                while (current != head) {
                    if (current == IntPtr.Zero || rows.Count == MaximumSystems || !nodes.Add(current))
                        throw Error("system list contains a null link, cycle or too many members");
                    var links = read(current, 16);
                    if (M.Ptr(links, 8) != previous) throw Error("system list previous link differs");
                    var owner = IntPtr.Subtract(current, 8);
                    if (owner == managerSystem) result.ManagerRow = rows.Count;
                    var life = read(IntPtr.Add(owner, 33), 2);
                    var output = read(IntPtr.Add(owner, 1976), 72);
                    var counters = read(IntPtr.Add(owner, 38416), 20);
                    rows.Add(new Row {
                        Index = M.U32(counters, 4), Flags = M.U32(read(IntPtr.Add(owner, 48), 4), 0),
                        Initialized = Flag(life[0]), PluginsLoaded = Flag(life[1]),
                        Output = Alias(M.Ptr(output, 0)), Emulated = Alias(M.Ptr(output, 40)), Software = Alias(M.Ptr(output, 48)),
                        OutputType = M.I32(output, 8), OutputFormat = M.I32(output, 12), OutputRate = M.I32(output, 16),
                        InputChannels = M.I32(output, 24), OutputChannels = M.I32(output, 28),
                        DspBlockSize = M.U32(output, 60), DspBufferSize = M.U32(output, 64),
                        ClockTimestamp = M.U32(output, 56), DspClock = M.U64(read(IntPtr.Add(owner, 3192), 8), 0),
                        LastTimestamp = M.U32(counters, 0), SoftwareChannels = M.I32(counters, 8),
                        HardwareChannels = M.I32(counters, 12), LastDeviceCheck = M.U32(counters, 16)
                    });
                    observeSystem?.Invoke(owner, M.U32(counters,4), M.Ptr(output,0));
                    previous = current;
                    current = M.Ptr(links, 0);
                }
                if (M.Ptr(ends, 8) != previous) throw Error("system list tail differs");
            }
            result.Rows = rows.ToArray();
            Validate(result, managerSystem != IntPtr.Zero);
            ResidentFailureEvidence.Mark("FMOD systems=" + rows.Count + ", AudioManager row=" + result.ManagerRow +
                ", outputs=" + string.Join(",", rows.Select(row => row.OutputType + "/" + row.OutputRate)));
            return result;
        }

        internal static void Validate(Data data, bool managerHasSystem)
        {
            if (data == null || data.Rows == null || data.Rows.Length > MaximumSystems ||
                data.ListPresent && !data.CellPresent || !data.ListPresent && data.Rows.Length != 0 ||
                data.ManagerRow < -1 || data.ManagerRow >= data.Rows.Length || (data.ManagerRow >= 0) != managerHasSystem)
                throw Error("AudioManager handle and system directory disagree");
            var indices = new HashSet<uint>();
            int highestAlias = 0;
            foreach (var row in data.Rows) {
                if (row == null || !indices.Add(row.Index)) throw Error("system index is absent or repeated");
                foreach (var alias in new[] { row.Output, row.Emulated, row.Software }) {
                    if (alias < 0 || alias > highestAlias + 1) throw Error("output resource aliases are not consecutive");
                    highestAlias = Math.Max(highestAlias, alias);
                }
            }
        }

        private static bool Flag(byte value) => value <= 1 ? value != 0 : throw Error("invalid native boolean");
        private static InvalidDataException Error(string detail) => new InvalidDataException("Resident FMOD: " + detail);
    }
}
