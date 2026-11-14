using System;
using System.Collections.Generic;
using System.Linq;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAnimatorState
    {
        // Content dependencies retain their target-local allocations. The nullable
        // payload distinguishes legacy presence-only DTOs from fully observed tables.
        internal sealed class SetBindingDependency
        {
            public int NonConstantTransforms, TransformCount, ConstantCount;
            public bool TransformsPresent, ConstantsPresent, FullSchemaPresent;
            public byte[] Transforms = Array.Empty<byte>(), Constants = Array.Empty<byte>(), FullSchema = Array.Empty<byte>();
            public int GravityIndex, IntegerStride;
            public byte RootMotion;
            public List<ClipCurveMapDependency> ClipBindings = new List<ClipCurveMapDependency>();
        }
        internal sealed class ClipCurveMapDependency
        {
            public bool[] Present = new bool[9];
            public ushort[][] Indices = Enumerable.Range(0,8).Select(_ => Array.Empty<ushort>()).ToArray();
            public string[] Objects = Array.Empty<string>();
        }
        private static byte[] BindingBuffer(Reader reader,IntPtr pointer,int count,int stride)
        {
            if (count < 0 || count > MaximumEntries || count != 0 && pointer == IntPtr.Zero) throw Error("animation binding dependency storage differs");
            return reader.Memory.Read(pointer,checked(count * stride));
        }
        private static SetBindingDependency CaptureSetBindingDependency(Reader reader,byte[] set)
        {
            var result = new SetBindingDependency {
                NonConstantTransforms = BindingSize(M.U64(set,48)), TransformCount = BindingSize(M.U64(set,56)),
                ConstantCount = BindingSize(M.U64(set,72)), TransformsPresent = M.Ptr(set,64) != IntPtr.Zero,
                ConstantsPresent = M.Ptr(set,80) != IntPtr.Zero
            };
            result.Transforms = BindingBuffer(reader,M.Ptr(set,64),result.TransformCount,8);
            result.Constants = BindingBuffer(reader,M.Ptr(set,80),result.ConstantCount,4);
            return result;
        }
        private static int BindingSize(ulong size) => size <= MaximumEntries ? (int)size : throw Error("animation binding dependency exceeds bound");
        private static void CaptureAnimationSetDependency(Reader reader,byte[] info,SetBindingDependency value)
        {
            var schema = M.Ptr(info,16); value.FullSchemaPresent = schema != IntPtr.Zero;
            if (value.FullSchemaPresent) value.FullSchema = reader.BankSnapshot(schema,12);
            value.GravityIndex = M.I32(info,24); value.IntegerStride = M.I32(info,28); value.RootMotion = info[32];
        }
        // CreateBindingValueArrayConstant produces dense per-type indices. The
        // rotation category is shared by quaternion/euler/order arrays; type3
        // includes both integer and Object curves. No clip count can size these.
        private static int[] ClipBindingCounts(byte[] schema)
        {
            BankSize(schema,12);
            var types = new uint[] {1,3,6,7,8};
            var indices = types.ToDictionary(type => type,_ => new HashSet<uint>());
            for (int offset = 0; offset < schema.Length; offset += 12) {
                var type = M.U32(schema,offset+4); var index = M.U32(schema,offset+8);
                if (!indices.TryGetValue(type,out var set) || index >= MaximumEntries || !set.Add(index))
                    throw Error("animation set binding schema type or duplicate index differs");
            }
            foreach (var set in indices.Values)
                if (set.Any(index => index >= set.Count)) throw Error("animation set binding schema indices are not dense");
            int f = indices[1].Count, i = indices[3].Count, p = indices[6].Count, q = indices[7].Count, s = indices[8].Count;
            return new[] {p,q,q,q,s,f,i,i};
        }
        private static ClipCurveMapDependency CaptureClipCurveMapDependency(Reader reader,IntPtr clip,byte[] entries,int offset,int[] counts)
        {
            var value = new ClipCurveMapDependency();
            for (int i = 0; i < 8; i++) {
                var pointer = M.Ptr(entries,offset+i*8); value.Present[i] = pointer != IntPtr.Zero;
                // Null clip entries own no arrays even though the set has values.
                var bytes = BindingBuffer(reader,pointer,clip == IntPtr.Zero ? 0 : counts[i],2);
                if (clip == IntPtr.Zero && pointer != IntPtr.Zero) throw Error("null animation clip retains curve binding storage");
                value.Indices[i] = Enumerable.Range(0,bytes.Length/2).Select(index => BitConverter.ToUInt16(bytes,index*2)).ToArray();
            }
            var remap = M.Ptr(entries,offset+64); value.Present[8] = remap != IntPtr.Zero;
            if (clip == IntPtr.Zero) {
                if (remap != IntPtr.Zero) throw Error("null animation clip retains integer remap");
                return value;
            }
            var mapping = reader.Memory.Read(IntPtr.Add(clip,480),32);
            if (remap != M.Ptr(mapping,0)) throw Error("animation clip integer remap is not its PPtr mapping");
            var count = BindingSize(M.U64(mapping,16)); var storage = StorageAt(mapping,0);
            if (count > storage.Capacity || storage.Allocated != (storage.Capacity != 0))
                throw Error("animation clip PPtr mapping allocation extent differs");
            var ids = BindingBuffer(reader,remap,count,4);
            value.Objects = Enumerable.Range(0,count).Select(index => reader.Id<NativeObject>(M.I32(ids,index*4))).ToArray();
            return value;
        }
        private static void ValidateSetBindingDependency(Backing backing)
        {
            var value = backing.SetDependency;
            if (value == null) return; // legacy DTO remains rejected by its existing gap
            if (!backing.SetPresent || value.Transforms == null || value.Constants == null || value.FullSchema == null || value.ClipBindings == null
                || value.TransformCount < 0 || value.TransformCount > MaximumEntries || value.NonConstantTransforms < 0 || value.NonConstantTransforms > value.TransformCount
                || value.ConstantCount < 0 || value.ConstantCount > MaximumEntries
                || value.Transforms.Length != checked(value.TransformCount*8) || value.Constants.Length != checked(value.ConstantCount*4)
                || value.TransformCount != 0 && !value.TransformsPresent || value.ConstantCount != 0 && !value.ConstantsPresent
                || !value.FullSchemaPresent && value.FullSchema.Length != 0)
                throw Error("animation set dependency shape differs");
            var counts = ClipBindingCounts(value.FullSchema);
            if (!backing.ClipSetPresent && (value.FullSchemaPresent || value.FullSchema.Length != 0 || value.ClipBindings.Count != 0
                || value.GravityIndex != 0 || value.IntegerStride != 0 || value.RootMotion != 0)) throw Error("absent animation set owns dependency payload");
            if (backing.ClipSetPresent && (value.IntegerStride != 4 || value.RootMotion > 1 || value.ClipBindings.Count != backing.Clips.Length))
                throw Error("animation set scalar dependency differs");
            for (int index = 0; index < value.ClipBindings.Count; index++) {
                var clip = value.ClipBindings[index];
                if (clip == null || clip.Present == null || clip.Present.Length != 9 || clip.Indices == null || clip.Indices.Length != 8
                    || !Keys(clip.Objects) || clip.Objects.Length > MaximumEntries || clip.Objects.Length != 0 && !clip.Present[8])
                    throw Error("animation clip dependency shape differs");
                bool absent = backing.Clips[index].Length == 0;
                for (int slot = 0; slot < 8; slot++) {
                    var table = clip.Indices[slot];
                    if (table == null || table.Length != (absent ? 0 : counts[slot]) || table.Length != 0 && !clip.Present[slot])
                        throw Error("animation clip index dependency count differs");
                }
                int mask = 0; for (int slot = 0; slot < 9; slot++) if (clip.Present[slot]) mask |= 1 << slot;
                if (mask != backing.ClipMaps[index] || absent && (mask != 0 || clip.Objects.Length != 0))
                    throw Error("animation clip dependency pointer presence differs");
            }
        }
    }
}

