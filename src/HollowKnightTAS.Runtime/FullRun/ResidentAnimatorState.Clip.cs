using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAnimatorState
    {
        internal static int MaximumClipObservers => MaximumEntries;
        private sealed class ClipBindingDependency
        {
            internal string Key = "";
        }
        private sealed class ClipConstantDependency
        {
            internal string Asset = "";
            internal int Curves;
        }
        internal sealed class ClipState
        {
            public string Asset = "", Clip = "", Observed = "", Constant = "", Binding = "";
            public int HeapVtable, BlockVtable, MemoryVtable, HeapLabel, BlockLabel, ObserverCallback;
            public bool ObserverSelf;
            public string[] ObserverPeers = Array.Empty<string>();
            public int MachineOwner = -1, MachineIndex = -1;
            public int[] Caches = Enumerable.Repeat(-1,9).ToArray();
            public byte[][] Fields = ClipFieldSizes.Select(size => new byte[size]).ToArray();
            public ClipBlocks Blocks = new ClipBlocks();
        }
        internal sealed class ClipBlocks
        {
            public int Reserve, Cursor;
            public int[] Sizes = Array.Empty<int>(), HeadOffsets = Array.Empty<int>();
        }
        private static readonly int[] ClipFieldOffsets = {368,376,396,400,404,412,424,496,508};
        private static readonly int[] ClipFieldSizes = {5,17,4,1,8,1,36,9,4};
        private static readonly AvatarKind[] ClipCacheKinds = {
            AvatarKind.ClipMemory, AvatarKind.ClipOutput, AvatarKind.MotionReference,
            AvatarKind.Motion, AvatarKind.Motion, AvatarKind.ClipMemory, AvatarKind.ClipOutput,
            AvatarKind.TrsPose, AvatarKind.TrsPose
        };
        private static void RegisterClipDependency(Reader reader, IntPtr clip, IntPtr binding, string key, string asset)
        {
            // The table entry is owned by the live Animator binding set.
            // Keep its first traversal identity when several views share it.
            if (!reader.Live.ClipBindings.ContainsKey(binding))
                reader.Live.ClipBindings.Add(binding,new ClipBindingDependency { Key = key });
            if (clip == IntPtr.Zero) return;
            var body = reader.Memory.Read(IntPtr.Add(clip,432),12);
            var constant = M.Ptr(body,0);
            if (constant == IntPtr.Zero || M.I32(body,8) <= 0) return;
            if (reader.Live.ClipConstants.ContainsKey(constant)) return;
            if (M.I32(body,8) < 2400 || M.I32(body,8) > MaximumBytes) throw Error("clip constant extent differs");
            var header = reader.Memory.Read(constant,1528);
            var values = Relative(constant,header,1520);
            long relative = checked(values.ToInt64() - constant.ToInt64());
            if (values == IntPtr.Zero || relative < 0 || relative > M.I32(body,8) - 72)
                throw Error("clip curve header escaped native constant extent");
            var curves = reader.Memory.Read(values,72);
            int count = checked(Count(M.U32(curves,16)) + Count(M.U32(curves,28)) + Count(M.U32(curves,56)));
            if (count > MaximumEntries) throw Error("clip curve count exceeds native constant");
            reader.Live.ClipConstants.Add(constant,new ClipConstantDependency { Asset = asset, Curves = count });
        }
        private static ClipBlocks CaptureClipBlocks(byte[] body, MemoryReader reader)
        {
            int Size(ulong value) => value <= MaximumBytes ? (int)value : throw Error("clip allocator extent exceeds bound");
            var result = new ClipBlocks { Reserve = Size(M.U64(body,240)) };
            var current = M.Ptr(body,224);
            var pointer = M.Ptr(body,216);
            var seen = new HashSet<IntPtr>(); var sizes = new List<int>(); var headOffsets = new List<int>();
            long totalBytes = 0;
            IntPtr last = IntPtr.Zero, lastHead = IntPtr.Zero;
            while (pointer != IntPtr.Zero) {
                if (seen.Count == MaximumEntries || !seen.Add(pointer)) throw Error("clip block chain escaped its bound");
                var block = reader.Read(pointer,24); var head = M.Ptr(block,8); int size = Size(M.U64(block,16));
                long alignment = head.ToInt64() - pointer.ToInt64();
                if ((pointer.ToInt64() & 15) != 0 || (alignment != 24 && alignment != 32)) throw Error("clip block alignment differs");
                totalBytes = checked(totalBytes + size);
                if (totalBytes > MaximumBytes) throw Error("clip block chain exceeds native byte bound");
                sizes.Add(size); headOffsets.Add((int)alignment); last = pointer; lastHead = head; pointer = M.Ptr(block,0);
            }
            if (current != last) throw Error("clip allocator current block differs from chain tail");
            if (last != IntPtr.Zero) {
                long cursor = M.Ptr(body,232).ToInt64() - lastHead.ToInt64();
                if (cursor < 0 || cursor > sizes[sizes.Count-1]) throw Error("clip allocator cursor escaped storage");
                result.Cursor = (int)cursor;
            }
            // Reset frees the blocks but leaves heapPtr dangling.
            result.Sizes = sizes.ToArray(); result.HeadOffsets = headOffsets.ToArray(); return result;
        }
        private static ClipState CaptureClip(IntPtr pointer, Node node, Reader reader)
        {
            var body = reader.Memory.Read(pointer,512);
            var observed = M.Ptr(body,472);
            var result = new ClipState {
                Asset = reader.Id<UnityEngine.Object>(M.I32(body,264)), Clip = reader.Key<AnimationClip>(M.Ptr(body,464)),
                Observed = observed == IntPtr.Zero ? "" : reader.Key<AnimationClip>(IntPtr.Subtract(observed,56)),
                ObserverSelf = M.Ptr(body,480) == (observed == IntPtr.Zero ? IntPtr.Zero : pointer),
                ObserverCallback = reader.Rva(M.Ptr(body,488)),
                HeapVtable = reader.Rva(M.Ptr(body,192)), BlockVtable = reader.Rva(M.Ptr(body,208)),
                HeapLabel = M.I32(body,200), BlockLabel = M.I32(body,248), MemoryVtable = reader.Rva(M.Ptr(body,272)),
                Blocks = CaptureClipBlocks(body,reader.Memory),
                Fields = ClipFieldOffsets.Select((offset,index) => M.Slice(body,offset,ClipFieldSizes[index])).ToArray()
            };
            if (observed != IntPtr.Zero) {
                var known = new Dictionary<IntPtr,string>();
                foreach (var topology in reader.Live.Topologies) {
                    for (int index=0; index<topology.Nodes.Count; index++) {
                        int offset = topology.Data.Nodes[index].Vtable == PlayableVtable ? 416
                            : topology.Data.Nodes[index].Vtable == 0x15d8b80 ? 472 : -1;
                        if (offset >= 0) known.Add(IntPtr.Add(topology.Nodes[index],offset),ControllerObserverKey(topology.Data.Owner,index,offset));
                    }
                }
                var observers = reader.ArrayBytes(reader.Memory.Read(observed,32),0,8);
                result.ObserverPeers = Decode(observers,8,(array,index)=>known.TryGetValue(M.Ptr(array,index),out var key) ? key : Unbound);
            }
            reader.Live.Memories!.MachineBinding(M.Ptr(body,416),out result.MachineOwner,out result.MachineIndex);
            // Deallocation leaves borrowed root-motion slots behind. Follow
            // caches only while the native binding owner is alive.
            if (node.AnimationFlags[4] != 1) return result;
            var constant = M.Ptr(body,280); var binding = M.Ptr(body,288);
            reader.Live.ClipConstants.TryGetValue(constant,out var dependency);
            result.Constant = constant == IntPtr.Zero ? "" : dependency?.Asset ?? Unbound;
            result.Binding = binding == IntPtr.Zero ? "" : reader.Live.ClipBindings.TryGetValue(binding,out var bound) ? bound.Key : Unbound;
            for (int index = 0; index < ClipCacheKinds.Length; index++) {
                var cache = M.Ptr(body,296+index*8);
                if (ClipCacheKinds[index] == AvatarKind.ClipOutput) {
                    if (cache != IntPtr.Zero && dependency == null) throw Error("clip output has no native constant dependency");
                    result.Caches[index] = reader.Live.Avatars!.ClipOutput(cache,dependency?.Curves ?? 0);
                } else if (ClipCacheKinds[index] == AvatarKind.MotionReference) {
                    // Allocation reserves raw bytes. Both virtual processing
                    // entrypoints evaluate root motion before consuming these
                    // bytes unless rootProcessed already records that pass.
                    result.Caches[index] = reader.Live.Avatars!.MotionReference(cache,body[372] == 1 && constant != IntPtr.Zero);
                } else result.Caches[index] = reader.Live.Avatars!.Retained(cache,ClipCacheKinds[index]);
            }
            return result;
        }
        private static void ValidateClip(Node node)
        {
            var clip = node.Clip!;
            if (node.Vtable != 0x15d8b80 || !Keys(new[] {clip.Asset,clip.Clip,clip.Observed,clip.Constant,clip.Binding})
                || clip.Caches == null || clip.Caches.Length != 9 || clip.Caches.Any(index => index < -1)
                || clip.Fields == null || clip.Fields.Length != ClipFieldSizes.Length
                || clip.Fields.Where((field,index) => field == null || field.Length != ClipFieldSizes[index]).Any()
                || clip.Blocks == null || clip.Blocks.Sizes == null || clip.Blocks.Sizes.Length > MaximumEntries
                || clip.Blocks.HeadOffsets == null || clip.Blocks.HeadOffsets.Length != clip.Blocks.Sizes.Length
                || clip.Blocks.HeadOffsets.Any(offset => offset != 24 && offset != 32)
                || clip.Blocks.Sizes.Sum(size => (long)size) > MaximumBytes
                || clip.Blocks.Sizes.Any(size => size < 0 || size > MaximumBytes) || clip.Blocks.Reserve < 0 || clip.Blocks.Reserve > MaximumBytes
                || clip.Blocks.Cursor < 0 || clip.Blocks.Cursor > (clip.Blocks.Sizes.LastOrDefault())
                || clip.MachineOwner < UnknownEdge || clip.MachineIndex < -1 || (clip.MachineOwner < 0) != (clip.MachineIndex == -1))
                throw Error("clip playable typed body differs");
            if (node.AnimationFlags[4] == 0 && (clip.Caches.Any(index => index != -1) || clip.Constant.Length != 0 || clip.Binding.Length != 0 || clip.Blocks.Sizes.Length != 0))
                throw Error("dormant clip retains owned bindings");
        }
        private static IEnumerable<string> ClipGaps(ClipState clip)
        {
            if (new[] {clip.Asset,clip.Clip,clip.Observed,clip.Constant,clip.Binding}.Contains(Unbound)) yield return "clip-playable-unbound-asset-or-binding";
            if (clip.HeapVtable != 0x15d5b60 || clip.BlockVtable != 0x15d6cf8 || clip.MemoryVtable != 0x15d8b78
                || clip.HeapLabel != 30 || clip.BlockLabel != 30) yield return "clip-playable-native-allocator-dependency";
            if (clip.ObserverPeers == null || clip.ObserverPeers.Contains(Unbound)) yield return "clip-animationasset-observer-directory-not-covered";
            if (!clip.ObserverSelf || clip.ObserverCallback != (clip.Observed.Length == 0 ? 0 : 0x10b1e0)) yield return "clip-asset-observer-owner-or-callback";
            if (clip.MachineOwner == UnknownEdge) yield return "clip-state-machine-without-owner";
            foreach (var pair in new[] {(0,4),(1,16),(3,0),(5,0)})
                foreach (var gap in BooleanRange(clip.Fields[pair.Item1],pair.Item2,1,"ClipMemory",ClipFieldOffsets[pair.Item1]+pair.Item2)) yield return gap;
            foreach (var gap in BooleanRange(clip.Fields[7],0,9,"ClipPlayable",496)) yield return gap;
        }
        private static void PrepareClip(IntPtr pointer, ClipState source, ClipState target, List<PendingWrite> writes)
        {
            Reject(ClipGaps(source).Concat(ClipGaps(target)));
            if (!source.ObserverPeers.SequenceEqual(target.ObserverPeers)) throw Error("clip AnimationAsset observer alias directory differs");
            if (source.Asset != target.Asset || source.Clip != target.Clip || source.Observed != target.Observed
                || source.Constant != target.Constant || source.Binding != target.Binding || source.MachineOwner != target.MachineOwner
                || source.MachineIndex != target.MachineIndex || !source.Caches.SequenceEqual(target.Caches) || Json(source.Blocks) != Json(target.Blocks))
                throw Error("clip asset, binding, cache alias or native allocation differs");
            for (int index = 0; index < ClipFieldOffsets.Length; index++) {
                var at = IntPtr.Add(pointer,ClipFieldOffsets[index]);
                writes.Add(new PendingWrite(at,M.Read(at,ClipFieldSizes[index]),source.Fields[index]));
            }
        }
    }
}
