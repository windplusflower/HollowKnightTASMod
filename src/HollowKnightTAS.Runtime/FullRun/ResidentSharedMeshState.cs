using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    // The pinned player's SharedMeshData is also retained by CanvasRenderer and
    // Canvas instructions without any surviving Unity Mesh wrapper. This is the
    // CPU value domain, not a source address or a new asset identity. Callers
    // own the complete retain/release graph and target allocation lifetime.
    internal static class ResidentSharedMeshState
    {
        private const int MaximumBytes = 32 * 1024 * 1024, MaximumSlots = 100000;
        private static readonly int[] Offsets = { 160, 200, 232, 264, 296, 328, 360 };
        private static readonly int[] Strides = { 1, 56, 64, 4, 16, 32, 4 };
        private static readonly int[] Alignments = { 1, 4, 16, 4, 16, 16, 4 };
        internal sealed class Data
        {
            public int References, IndexFormat;
            public byte[] VertexState = Array.Empty<byte>(), Vertices = Array.Empty<byte>();
            public Buffer[] Buffers = Array.Empty<Buffer>();
            public bool BlendShapes;
        }
        internal sealed class Buffer
        {
            public int Capacity;
            public byte[] Bytes = Array.Empty<byte>();
        }
        internal static Data Capture(IntPtr pointer)
        {
            LoadingEngineClockProbe.RequirePinnedEngine();
            var bytes = M.Read(pointer, 400); var length = M.U64(bytes, 128);
            if (length > MaximumBytes) throw Error("vertex storage exceeds the package bound");
            var state = M.Slice(bytes, 8, 136);
            Array.Clear(state, 116, 4); Array.Clear(state, 131, 5); // ABI padding only.
            var data = new Data { References = M.I32(bytes, 4), IndexFormat = M.I32(bytes, 192), VertexState = state,
                Vertices = M.Read(M.Ptr(bytes, 152), (int)length), BlendShapes = M.Ptr(bytes, 392) != IntPtr.Zero,
                Buffers = Offsets.Select((offset, index) => ReadBuffer(pointer, offset, Strides[index])).ToArray() };
            if (!M.Read(pointer, 400).SequenceEqual(bytes)) throw Error("shared CPU geometry changed during capture");
            Validate(data); return data;
        }
        private static Buffer ReadBuffer(IntPtr owner, int offset, int stride)
        {
            var header = M.Read(IntPtr.Add(owner, offset), 32); var size = M.U64(header, 16); var capacity = M.U64(header, 24) >> 1;
            if (size > capacity || capacity > MaximumSlots || capacity * (ulong)stride > MaximumBytes)
                throw Error("shared array exceeds the native bound");
            var bytes = M.Read(M.Ptr(header, 0), checked((int)size * stride));
            if (!M.Read(IntPtr.Add(owner, offset), 32).SequenceEqual(header)) throw Error("shared array changed during capture");
            return new Buffer { Capacity = (int)capacity, Bytes = bytes };
        }
        internal static void Validate(Data data)
        {
            if (data == null || data.References <= 0 || data.References > MaximumSlots || data.IndexFormat < 0 || data.IndexFormat > 1
                || data.VertexState == null || data.VertexState.Length != 136 || data.Vertices == null || data.Vertices.Length > MaximumBytes
                || data.Buffers == null || data.Buffers.Length != Offsets.Length || M.U64(data.VertexState, 120) != (ulong)data.Vertices.Length)
                throw Error("shared CPU geometry schema differs");
            if (data.VertexState.Skip(116).Take(4).Concat(data.VertexState.Skip(131).Take(5)).Any(value => value != 0)
                || data.VertexState.Skip(128).Take(3).Any(value => value > 1)) throw Error("vertex descriptor padding or flags differ");
            for (var index = 0; index < Offsets.Length; index++) {
                var buffer = data.Buffers[index]; var stride = Strides[index];
                if (buffer == null || buffer.Bytes == null || buffer.Bytes.Length % stride != 0 || buffer.Capacity < buffer.Bytes.Length / stride
                    || buffer.Capacity > MaximumSlots || (long)buffer.Capacity * stride > MaximumBytes) throw Error("shared native array shape differs");
            }
            var vertices = M.U32(data.VertexState, 112);
            for (var stream = 0; stream < 4; stream++)
                if ((ulong)M.U32(data.VertexState, 60 + stream * 12) + (ulong)M.U32(data.VertexState, 64 + stream * 12) * vertices > (ulong)data.Vertices.Length)
                    throw Error("vertex stream exceeds its storage");
        }
        internal static string[] RequiredCoverageGaps(Data data)
        {
            Validate(data); var gaps = new List<string>();
            if (data.BlendShapes) gaps.Add("shared-mesh-blend-shape-cache");
            if (data.VertexState[128] != 0 || data.VertexState[129] != 0) gaps.Add("shared-mesh-external-compute-buffer");
            return gaps.ToArray();
        }
        internal static void RequireCompleteCoverage(Data data)
        {
            var gaps = RequiredCoverageGaps(data);
            if (gaps.Length != 0) throw Error("shared CPU geometry coverage is incomplete: " + string.Join(",", gaps));
        }
        internal static void ApplyToUniqueStorage(Data data, IntPtr pointer, Action admitted)
        {
            RequireCompleteCoverage(data); if (admitted == null) throw new ArgumentNullException(nameof(admitted)); admitted();
            var before = M.Read(pointer, 400);
            if (M.I32(before, 4) != 1 || M.Ptr(before, 392) != IntPtr.Zero) throw Error("target shared geometry is not a unique empty-blend allocation");
            var oldLength = M.U64(before, 128);
            if (oldLength > MaximumBytes) throw Error("target vertex allocation exceeds the native bound");
            var temporary = Marshal.AllocHGlobal(32);
            try {
                var header = new byte[32]; System.Buffer.BlockCopy(before, 152, header, 0, 8); System.Buffer.BlockCopy(before, 144, header, 8, 4);
                System.Buffer.BlockCopy(BitConverter.GetBytes(oldLength << 1), 0, header, 24, 8);
                M.Write(temporary, header); M.Reserve(temporary, data.Vertices.Length, 1, 16, admitted);
                var vertices = M.Ptr(temporary); M.Write(vertices, data.Vertices);
                M.Write(IntPtr.Add(pointer, 152), BitConverter.GetBytes(vertices.ToInt64()));
            } finally { Marshal.FreeHGlobal(temporary); }
            // Allocator label and reference count are target-owned dependencies.
            M.Write(IntPtr.Add(pointer, 8), data.VertexState); M.Write(IntPtr.Add(pointer, 192), BitConverter.GetBytes(data.IndexFormat));
            for (var index = 0; index < Offsets.Length; index++) {
                admitted(); var header = IntPtr.Add(pointer, Offsets[index]); var value = data.Buffers[index];
                M.Write(IntPtr.Add(header, 16), BitConverter.GetBytes(0UL)); M.Reserve(header, value.Capacity, Strides[index], Alignments[index], admitted);
                if (M.U64(M.Read(header, 32), 24) >> 1 != (ulong)value.Capacity) throw Error("target shared array capacity differs");
                M.Write(M.Ptr(header), value.Bytes); M.Write(IntPtr.Add(header, 16), BitConverter.GetBytes((ulong)(value.Bytes.Length / Strides[index])));
            }
            var expected = Copy(data); expected.References = 1; RequireEqual(expected, Capture(pointer)); admitted();
        }
        internal static void RequireEqual(Data expected, Data actual)
        { Validate(expected); Validate(actual); if (Json(expected) != Json(actual)) throw Error("shared CPU geometry readback differs"); }
        internal static Data Copy(Data data) => Newtonsoft.Json.JsonConvert.DeserializeObject<Data>(Json(data)) ?? throw Error("shared geometry copy is absent");
        private static string Json(object value) => Newtonsoft.Json.JsonConvert.SerializeObject(value);
        private static InvalidDataException Error(string reason) => new InvalidDataException("Resident shared mesh: " + reason);
    }
}
