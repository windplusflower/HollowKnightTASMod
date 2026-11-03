using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentCameraCanvasState
    {
        // S0 owns the complete temporary texture pool, including allocations
        // already returned by previous cameras. The native Cleanup destroys
        // every free/taken texture; therefore all actual owners must first pass
        // the caller's complete world alias exclusion. An unknown GPU buffer
        // pool remains a hard gap rather than being swept along with textures.
        // Current frames and VR mode are retained D; native storage is local.
        internal sealed class RenderPool
        {
            public bool Present;
            public int VrUsage, TemporaryCounter, TextureFrame, BufferFrame;
            public int FreeCapacity, FreeDeleted, BufferCapacity, BufferEntries, BufferDeleted;
            public PoolBucket[] Free = Array.Empty<PoolBucket>();
            public PoolTexture[] Taken = Array.Empty<PoolTexture>();
        }
        internal sealed class PoolBucket
        {
            public uint Hash;
            public byte[] Descriptor = Array.Empty<byte>();
            public PoolTexture[] Textures = Array.Empty<PoolTexture>();
        }
        internal sealed class PoolTexture
        {
            public TransientTexture Texture = new TransientTexture();
            public byte[] Descriptor = Array.Empty<byte>();
            public int LastUsedFrame;
        }
        private static IntPtr RenderPoolPointer()
        {
            Function<Unary>(0x663010, "488b05f9253b01c3");
            return M.Ptr(IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, 0x1a15610));
        }
        private static RenderPool CaptureRenderPool(Reader reader)
        {
            var pointer = RenderPoolPointer(); if (pointer == IntPtr.Zero) return new RenderPool();
            var bytes = reader.Read(pointer, 104);
            var data = new RenderPool { Present = true, VrUsage = M.I32(bytes, 48), TemporaryCounter = M.I32(bytes, 52),
                TextureFrame = M.I32(bytes, 56), BufferFrame = M.I32(bytes, 96) };
            var entries = new HashSet<IntPtr>();
            PoolTexture Entry(IntPtr node, byte[]? descriptor, string key) {
                if (node == IntPtr.Zero || !entries.Add(node) || entries.Count > Maximum) throw Error("temporary texture entry is duplicated or exceeds bounds");
                var entry = reader.Read(node, 24); var owner = reader.RenderTexturePointer(M.I32(entry, 20)); var native = reader.Read(owner, 408);
                if (M.Ptr(native, 392) != node || M.Ptr(entry, 0) == IntPtr.Zero || M.Ptr(entry, 8) == IntPtr.Zero
                    || M.Ptr(reader.Read(IntPtr.Add(M.Ptr(entry, 0), 8), 8), 0) != node || M.Ptr(reader.Read(M.Ptr(entry, 8), 8), 0) != node)
                    throw Error("temporary texture entry owner or reciprocal links differ");
                var actual = M.Slice(native, 280, 52);
                if (descriptor != null && !descriptor.SequenceEqual(actual)) throw Error("free texture descriptor and hash bucket differ");
                return new PoolTexture { Texture = reader.Transient(owner, key), Descriptor = actual, LastUsedFrame = M.I32(entry, 16) };
            }
            var free = HashSlots(reader, pointer, 72, out data.FreeCapacity, out data.FreeDeleted); var buckets = new List<PoolBucket>();
            for (var index = 0; index < free.Length; index++) {
                var bucket = free[index]; var descriptor = M.Slice(bucket, 8, 52); var head = M.Ptr(bucket, 64);
                if (head == IntPtr.Zero) throw Error("temporary texture bucket has no circular list");
                var textures = new List<PoolTexture>(); var node = head;
                do {
                    textures.Add(Entry(node, descriptor, "pool/free/" + index + "/" + textures.Count)); node = M.Ptr(reader.Read(IntPtr.Add(node, 8), 8), 0);
                } while (node != head);
                buckets.Add(new PoolBucket { Hash = M.U32(bucket, 0), Descriptor = descriptor, Textures = textures.ToArray() });
            }
            data.Free = buckets.ToArray();
            var sentinel = IntPtr.Add(pointer, 32); var list = reader.Read(sentinel, 16); var current = M.Ptr(list, 8); var previous = sentinel; var taken = new List<PoolTexture>();
            while (current != sentinel) {
                var entry = reader.Read(current, 24); if (M.Ptr(entry, 0) != previous) throw Error("taken temporary texture order differs");
                taken.Add(Entry(current, null, "pool/taken/" + taken.Count)); previous = current; current = M.Ptr(entry, 8);
            }
            if (M.Ptr(list, 0) != previous) throw Error("taken temporary texture tail differs"); data.Taken = taken.ToArray();
            data.BufferEntries = HashSlots(reader, IntPtr.Add(pointer, 64), 32, out data.BufferCapacity, out data.BufferDeleted).Length;
            return data;
        }
        private static byte[][] HashSlots(Reader reader, IntPtr pointer, int stride, out int capacity, out int deleted)
        {
            var header = reader.Read(pointer, 32);
            var address = M.Ptr(header, 0);
            var sentinel = IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, 0x1723b3c);
            if ((M.U32(header, 8) == 0) != (address == sentinel))
                throw Error("render pool scaled mask and static sentinel disagree");
            var length = ResidentCoreHashTable.StorageBytes(header, stride, Maximum);
            var rows = ResidentCoreHashTable.Decode(header, reader.Read(address, length), stride, Maximum);
            capacity = rows.Capacity;
            deleted = rows.Deleted;
            return rows.Live;
        }
        private static void ValidateRenderPool(Data data)
        {
            var pool = data.RenderTextures;
            if (pool == null || pool.Free == null || pool.Taken == null || pool.Free.Length + pool.Taken.Length > Maximum
                || pool.VrUsage < 0 || pool.VrUsage > 2 || pool.FreeCapacity < pool.Free.Length || pool.FreeCapacity > Maximum
                || pool.FreeDeleted < 0 || pool.FreeDeleted > pool.FreeCapacity - pool.Free.Length || pool.BufferEntries < 0 || pool.BufferEntries > pool.BufferCapacity
                || pool.BufferCapacity > Maximum || pool.BufferDeleted < 0 || pool.BufferDeleted > pool.BufferCapacity - pool.BufferEntries)
                throw Error("temporary render-pool shape differs");
            var aliases = new HashSet<string>(StringComparer.Ordinal);
            void Texture(PoolTexture row) {
                if (row == null || row.Descriptor == null || row.Descriptor.Length != 52) throw Error("temporary texture descriptor differs");
                ValidateTransient(row.Texture);
                if (row.Texture.Alias.Length == 0 || !aliases.Add(row.Texture.Alias)) throw Error("temporary texture pool repeats an allocation");
            }
            foreach (var bucket in pool.Free) {
                if (bucket == null || bucket.Hash >= uint.MaxValue - 1 || bucket.Descriptor == null || bucket.Descriptor.Length != 52 || bucket.Textures == null || bucket.Textures.Length == 0)
                    throw Error("temporary texture free bucket differs");
                foreach (var row in bucket.Textures) { Texture(row); if (!bucket.Descriptor.SequenceEqual(row.Descriptor)) throw Error("temporary texture free descriptor differs"); }
            }
            foreach (var row in pool.Taken) Texture(row);
            if (aliases.Count > Maximum) throw Error("temporary texture pool exceeds bounds");
            if (!pool.Present && (pool.VrUsage != 0 || pool.TemporaryCounter != 0 || pool.TextureFrame != 0 || pool.BufferFrame != 0
                || pool.FreeCapacity != 0 || pool.FreeDeleted != 0 || pool.BufferCapacity != 0 || pool.BufferEntries != 0 || pool.BufferDeleted != 0 || aliases.Count != 0))
                throw Error("absent render pool has retained native state");
        }
        private static void CanonRenderPool(Action admitted)
        {
            var pointer = RenderPoolPointer(); if (pointer == IntPtr.Zero) return;
            // The caller rejects nonempty GfxBuffer buckets. Full Cleanup can
            // then reset the empty buffer hash allocation as well as textures.
            admitted(); Function<Unary>(0x661090, "48895c240848896c2410488974241848897c2420415441564157")(pointer);
            M.Write(IntPtr.Add(pointer, 52), BitConverter.GetBytes(0)); admitted();
        }
        private static void ApplyRenderPool(RenderPool source, Action admitted)
        {
            var pointer = RenderPoolPointer(); if (source.Present != (pointer != IntPtr.Zero)) throw Error("target render-pool lifetime differs");
            if (pointer == IntPtr.Zero) return;
            admitted(); M.Write(IntPtr.Add(pointer, 48), BitConverter.GetBytes(source.VrUsage).Concat(BitConverter.GetBytes(0)).Concat(BitConverter.GetBytes(source.TextureFrame)).ToArray());
            M.Write(IntPtr.Add(pointer, 96), BitConverter.GetBytes(source.BufferFrame)); admitted();
        }
    }
}
