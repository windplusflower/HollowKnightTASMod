using System;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentRendererState
    {
        internal sealed class ParticleRendererData
        {
            public NativeBuffer[] StripIndices = Array.Empty<NativeBuffer>();
            public bool DrawCacheDirty, InstanceBufferPresent;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void WaitRenderJobs();
        private static readonly byte[] EmptyParticleDraw = ParticleDrawTemplate();
        private static byte[] ParticleDrawTemplate()
        {
            var bytes = new byte[824];
            // GeometryJobFence uses the invalid index, not zero. The constructor
            // writes these three sentinels at native +1632/+1636/+1640.
            foreach (var offset in new[] { 704, 708, 712 }) Buffer.BlockCopy(BitConverter.GetBytes(uint.MaxValue), 0, bytes, offset, 4);
            return bytes;
        }
        private static void CaptureParticleRenderer(Row row, IntPtr owner, byte[] bytes, SourceBindings references)
        {
            row.Meshes = Enumerable.Range(0, 4).Select(slot => references.Id<Mesh>(M.I32(bytes, 912 + slot * 4))).ToArray();
            row.CachedMeshes = Enumerable.Range(0, 4).Select(slot => references.Pointer<Mesh>(M.Ptr(bytes, 656 + slot * 8))).ToArray();
            row.ConsumerLinked = Enumerable.Range(0, 4).Select(slot => Linked(bytes, 688 + slot * 24, owner, 0)).ToArray();
            row.Particle = new ParticleRendererData {
                StripIndices = Enumerable.Range(0, 4).Select(slot => {
                    var values = ReadArray(owner, 784 + slot * 32, 4, out var capacity);
                    return new NativeBuffer { Bytes = values, Capacity = capacity };
                }).ToArray(),
                DrawCacheDirty = !M.Slice(bytes, 928, 824).SequenceEqual(EmptyParticleDraw),
                InstanceBufferPresent = M.Ptr(bytes, 1624) != IntPtr.Zero
            };
            // ParticleSystemRendererData ends its AABB at +204, then aligns the
            // first Mesh pointer to +208. These four bytes carry no field.
            Array.Clear(row.Scalars[3], 204, 4);
        }
        private static void ValidateParticleRenderer(Row row)
        {
            if (row.Kind != "ParticleSystemRenderer") {
                if (row.Particle != null) throw Error("non-particle renderer contains particle renderer storage");
                return;
            }
            if (row.Particle == null || row.Particle.StripIndices == null || row.Particle.StripIndices.Length != 4)
                throw Error("particle renderer index storage is absent");
            foreach (var buffer in row.Particle.StripIndices) {
                if (buffer == null) throw Error("particle renderer index buffer is absent");
                Bytes(buffer.Bytes, buffer.Capacity, 4);
            }
            for (var offset = 64; offset < 72; offset++)
                if (row.Scalars[3][offset] > 1) throw Error("particle renderer boolean ABI differs");
        }
        private static readonly System.Collections.Generic.Dictionary<IntPtr, byte[]> PreservedDrawCaches = new System.Collections.Generic.Dictionary<IntPtr, byte[]>();
        internal static void StashParticleDrawCache(IntPtr owner)
        {
            var current = M.Read(IntPtr.Add(owner, 928), 824);
            if (!current.SequenceEqual(EmptyParticleDraw))
            {
                PreservedDrawCaches[owner] = current;
            }
        }
        internal static void RestoreParticleDrawCaches()
        {
            foreach (var pair in PreservedDrawCaches)
            {
                M.Write(IntPtr.Add(pair.Key, 928), pair.Value);
            }
            PreservedDrawCaches.Clear();
        }
        private static void CanonParticleDrawCaches(Data expected, ResidentObjectDirectory directory, ResidentMutationScope scope, Action admitted)
        {
            var rows = expected.Rows.Where(row => row.Kind == "ParticleSystemRenderer" && !row.Execution).ToArray();
            if (rows.Length == 0) return;
            scope.Require(); admitted();
            Function<WaitRenderJobs>(0x887150, "4883ec288b0da67b0e01ff157077d300488bc8488b104883")();
            foreach (var row in rows) {
                scope.Require(); admitted(); var owner = M.Owner(directory.Resolve(row.Owner));
                if (M.Ptr(IntPtr.Add(owner, 1624)) != IntPtr.Zero) throw Error("particle renderer owns a GPU instance buffer requiring transfer");
                StashParticleDrawCache(owner);
                // DrawCallData holds per-render borrowed mesh/VBO views. The
                // deleting destructor releases the four owned strip arrays, not
                // these views. After render jobs drain, discard only the views;
                // PrepareForRender regenerates them from retained simulation,
                // renderer settings and Mesh bindings before the next draw.
                M.Write(IntPtr.Add(owner, 928), EmptyParticleDraw);
                row.Particle!.DrawCacheDirty = false;
            }
        }
    }
}
