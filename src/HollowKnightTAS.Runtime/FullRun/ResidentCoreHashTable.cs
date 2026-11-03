using System;
using System.Collections.Generic;
using System.IO;

namespace HollowKnightTAS.Runtime.FullRun
{
    // The two core::hash_map instantiations owned by RenderBufferManager.
    // m_numUnusedLeft is an insertion budget. A deleted slot can be reused
    // without consuming this budget. Only the shared sentinel's hash is owned.
    internal static class ResidentCoreHashTable
    {
        internal sealed class Rows
        {
            internal int Capacity, Deleted;
            internal byte[][] Live = Array.Empty<byte[]>();
        }

        private static int Capacity(byte[] header, int stride, int maximum)
        {
            if (header == null || header.Length != 32 || maximum <= 0 || (stride != 72 && stride != 32))
                throw Error("invalid header or node layout");
            var mask = BitConverter.ToUInt32(header, 8);
            if (mask == 0) return 0;
            var scale = stride == 72 ? 8U : 32U;
            var capacity = (ulong)mask / scale + 1;
            if (mask % scale != 0 || (capacity & (capacity - 1)) != 0 || capacity > (uint)maximum ||
                capacity * (uint)stride > 32 * 1024 * 1024)
                throw Error("scaled mask is not a bounded power-of-two table");
            return (int)capacity;
        }

        internal static int StorageBytes(byte[] header, int stride, int maximum)
        {
            var capacity = Capacity(header, stride, maximum);
            return capacity == 0 ? sizeof(uint) : checked(capacity * stride);
        }

        internal static Rows Decode(byte[] header, byte[] slots, int stride, int maximum)
        {
            var capacity = Capacity(header, stride, maximum);
            var expectedBytes = capacity == 0 ? sizeof(uint) : checked(capacity * stride);
            if (slots == null || slots.Length != expectedBytes) throw Error("node storage length differs");
            var size = BitConverter.ToUInt32(header, 12);
            var budget = BitConverter.ToUInt32(header, 20);
            if (capacity == 0) {
                if (size != 0 || budget != 0 || BitConverter.ToUInt32(slots, 0) != uint.MaxValue)
                    throw Error("static sentinel or its empty header differs");
                return new Rows();
            }
            var live = new List<byte[]>();
            int deleted = 0;
            for (int offset = 0; offset < slots.Length; offset += stride) {
                var hash = BitConverter.ToUInt32(slots, offset);
                if (hash == uint.MaxValue) continue;
                if (hash == uint.MaxValue - 1) { deleted++; continue; }
                var row = new byte[stride];
                Buffer.BlockCopy(slots, offset, row, 0, stride);
                live.Add(row);
            }
            if (live.Count != size || (long)live.Count + deleted + budget != 2L * capacity / 3)
                throw Error("slot census differs from size or insertion budget (capacity=" + capacity +
                    ", size=" + size + ", live=" + live.Count + ", deleted=" + deleted + ", budget=" + budget + ")");
            return new Rows { Capacity = capacity, Deleted = deleted, Live = live.ToArray() };
        }

        private static InvalidDataException Error(string message) => new InvalidDataException("Resident core hash table: " + message);
    }
}
