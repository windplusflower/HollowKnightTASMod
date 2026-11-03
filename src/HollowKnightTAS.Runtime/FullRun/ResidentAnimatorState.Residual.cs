using System;
using System.Collections.Generic;
using System.Linq;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAnimatorState
    {
        private static readonly int[] ResidualArrays = {360,1160,1200,1232,1488,1664};
        private static readonly int[] ResidualStrides = {40,8,4,4,8,16};
        private static readonly int[] Allocators = {416,504,552,648,696,1456};
        internal sealed class Residual
        {
            public Storage[] Arrays = Array.Empty<Storage>();
            public int[] AllocatorRvas = Array.Empty<int>(), Labels = Array.Empty<int>(), DestroyRvas = Array.Empty<int>(), SetLabels = Array.Empty<int>();
            public int Backup = -1, SamplingConstant = -1, SamplingMemory, PlaybackVtable, FrameCapacity;
            public bool FrameAllocated,FramePadding;
            public Bound[] Bound = Array.Empty<Bound>();
            public Frame[] Frames = Array.Empty<Frame>();
            public Fence Pose = new Fence();
            public Fence[] Jobs = Array.Empty<Fence>();
        }
        internal sealed class Frame { public int Avatar = -1, Memory, TimeBits; }
        internal sealed class Fence { public int Group = -1; public uint Version; public ulong Tag; }
        internal sealed class Bound { public int Output = -1, Graph = -1, Playable = -1, Port; public bool OutputFlag, PlayableFlag; }
        private sealed class ResidualView
        {
            internal readonly Residual Data;
            internal readonly IntPtr FrameBase, Owner;
            private readonly Live live;
            internal ResidualView(Reader reader,Row row,MemoryDirectory directory,Dictionary<IntPtr,int> jobs)
            {
                live=reader.Live; Owner = M.Owner(reader.DirectoryOwner(row.Owner)); var bytes = reader.Memory.Read(Owner,1696);
                var result = new Residual {Arrays=ResidualArrays.Select(offset => StorageAt(bytes,offset)).ToArray(),
                    AllocatorRvas=Allocators.Select(offset => reader.Rva(M.Ptr(bytes,offset))).ToArray(),Labels=Allocators.Select(offset => M.I32(bytes,offset+8)).ToArray(),
                    SetLabels=new[] {M.I32(bytes,528),M.I32(bytes,672)},DestroyRvas=new[] {reader.Rva(M.Ptr(bytes,536)),reader.Rva(M.Ptr(bytes,680))},
                    Backup=reader.Live.Avatars!.Retained(M.Ptr(bytes,432),AvatarKind.Values),SamplingConstant=directory.Constant(M.Ptr(bytes,984)),
                    SamplingMemory=directory.Memory(M.Ptr(bytes,992)),PlaybackVtable=reader.Rva(M.Ptr(bytes,1408)) };
                FrameBase = M.Ptr(bytes,1416); var end = M.Ptr(bytes,1424); var cap = M.Ptr(bytes,1432);
                long length = end.ToInt64()-FrameBase.ToInt64(), capacity=cap.ToInt64()-FrameBase.ToInt64();
                if (length < 0 || capacity < length || capacity > 24L * MaximumEntries || length % 24 != 0 || capacity % 24 != 0 || capacity != 0 && FrameBase == IntPtr.Zero)
                    throw Error("recorder frame vector has invalid begin/end/capacity");
                result.FrameCapacity = (int)(capacity / 24);result.FrameAllocated=FrameBase!=IntPtr.Zero;
                var frames=reader.Memory.Read(FrameBase,(int)length);
                for(int i=20;i<frames.Length;i+=24) if(M.U32(frames,i)!=0) result.FramePadding=true;
                result.Frames = Decode(frames,24,(array,at) => new Frame {
                    Avatar=reader.Live.Avatars!.Retained(M.Ptr(array,at),AvatarKind.Memory),Memory=directory.Memory(M.Ptr(array,at+8)),TimeBits=M.I32(array,at+16) });
                Fence Job(byte[] buffer,int at) {
                    var p = M.Ptr(buffer,at); var value = new Fence {Version=M.U32(buffer,at+8)};
                    if (p != IntPtr.Zero) {
                        if (!jobs.TryGetValue(p,out var id)) jobs.Add(p,id=jobs.Count);
                        value.Group=id; value.Tag=M.U64(reader.Memory.Read(p,16),8);
                    }
                    return value;
                }
                result.Pose=Job(bytes,568); result.Jobs=Decode(reader.ArrayBytes(bytes,1664,16),16,Job);
                int Handle(byte[] buffer,int at,Dictionary<IntPtr,int> owners,int reciprocal,out bool flag) {
                    var handle=M.Ptr(buffer,at); var version=M.U32(buffer,at+8); flag=(version&1)!=0;
                    if (handle==IntPtr.Zero) return version==0 ? -1 : UnknownEdge;
                    var node=reader.Memory.Read(handle,48);
                    if (M.U32(node,32)!=(version&~1U)) return UnknownEdge;
                    var objectPointer=M.Ptr(node,40);
                    return owners.TryGetValue(objectPointer,out var id) && M.Ptr(reader.Memory.Read(IntPtr.Add(objectPointer,reciprocal),8),0)==handle ? id : UnknownEdge;
                }
                result.Bound=Decode(reader.ArrayBytes(bytes,360,40),40,(array,at) => {
                    var value=new Bound {Port=M.I32(array,at+32)};
                    value.Output=Handle(array,at,reader.Live.Outputs,32,out value.OutputFlag);
                    var handle=M.Ptr(array,at+16); var version=M.U32(array,at+24); value.PlayableFlag=(version&1)!=0;
                    if (handle!=IntPtr.Zero) {
                        var h=reader.Memory.Read(handle,48); value.Playable=UnknownEdge;
                        if (M.U32(h,32)==(version&~1U)) {
                            var p=M.Ptr(h,40);
                            foreach (var graph in live.Topologies) if (graph.Indices.TryGetValue(p,out var node) && M.Ptr(reader.Memory.Read(IntPtr.Add(p,40),8),0)==handle) {
                                value.Graph=graph.Data.Owner; value.Playable=node; break;
                            }
                        }
                    } else if (version!=0) value.Playable=UnknownEdge;
                    return value;
                });
                Data=result;
            }
            internal Action Prepare(Residual source,List<PendingWrite> writes,Action admitted)
            {
                if (source.Frames.Length != Data.Frames.Length || source.Jobs.Length != Data.Jobs.Length) throw Error("recorder/fence allocation extent differs");
                for (var i=0;i<source.Frames.Length;i++)
                    writes.Add(new PendingWrite(IntPtr.Add(FrameBase,i*24+16),BitConverter.GetBytes(Data.Frames[i].TimeBits),BitConverter.GetBytes(source.Frames[i].TimeBits)));
                void Version(Fence before,Fence after,IntPtr at) {
                    if (before.Group!=-1 || after.Group!=-1) throw Error("non-null native producer fence lacks transferable job ownership");
                    writes.Add(new PendingWrite(at,BitConverter.GetBytes(before.Version),BitConverter.GetBytes(after.Version)));
                }
                Version(Data.Pose,source.Pose,IntPtr.Add(Owner,576)); var array=M.Ptr(IntPtr.Add(Owner,1664));
                for (int i=0;i<source.Jobs.Length;i++) Version(Data.Jobs[i],source.Jobs[i],IntPtr.Add(array,i*16+8));
                if (Json(source.Bound)==Json(Data.Bound)) return ()=>{};
                if (source.Arrays[0].Label!=Data.Arrays[0].Label) throw Error("bound playable vector allocator differs");
                var graphNodes=live.Topologies.ToDictionary(g=>g.Data.Owner);
                var outputOwners=live.Outputs.ToDictionary(pair=>pair.Value,pair=>pair.Key);
                var result=new byte[checked(source.Bound.Length*40)];
                void Handle(int offset,IntPtr target,int nodeOffset,bool flag) {
                    if (target==IntPtr.Zero) {if (flag) throw Error("absent bound handle has nonzero flags"); return;}
                    var handle=M.Ptr(IntPtr.Add(target,nodeOffset)); var h=M.Read(handle,48);
                    if (M.Ptr(h,40)!=target) throw Error("target bound handle lost its reciprocal node");
                    BitConverter.GetBytes(handle.ToInt64()).CopyTo(result,offset);
                    BitConverter.GetBytes((M.U32(h,32)&~1U)|(flag?1U:0U)).CopyTo(result,offset+8);
                }
                for (int i=0;i<source.Bound.Length;i++) {
                    var b=source.Bound[i];
                    Handle(i*40,b.Output==-1 ? IntPtr.Zero : outputOwners[b.Output],32,b.OutputFlag);
                    Handle(i*40+16,b.Playable==-1 ? IntPtr.Zero : graphNodes[b.Graph].Nodes[b.Playable],40,b.PlayableFlag);
                    BitConverter.GetBytes(b.Port).CopyTo(result,i*40+32);
                }
                return ()=> {
                    admitted();var v=IntPtr.Add(Owner,360);ResizePod(v,source.Bound.Length,40,8,source.Arrays[0].Label,admitted);
                    M.Write(M.Ptr(v),result);admitted();
                };
            }
        }
        private sealed class MemoryDirectory
        {
            // Zero null, positive (one-based) live playable owner, negative
            // independent captured allocation. Frame aliases and sampling
            // state use the same transfer IDs, never a heap offset.
            internal readonly Dictionary<IntPtr,int> Playables;
            internal readonly List<ControllerMemory> Additional = new List<ControllerMemory>();
            internal readonly List<IntPtr> Pointers = new List<IntPtr>();
            private readonly Dictionary<IntPtr,int> memories=new Dictionary<IntPtr,int>(), constants=new Dictionary<IntPtr,int>();
            private readonly Reader reader;
            internal MemoryDirectory(Reader reader)
            {
                this.reader=reader; Playables=new Dictionary<IntPtr,int>();
                for (int i=0;i<reader.Live.Playables.Count;i++) {
                    var p=reader.Live.Playables[i]; Playables.Add(p,i);
                    var bytes=reader.Memory.Read(IntPtr.Add(p,296),24); var memory=M.Ptr(bytes,16); var constant=M.Ptr(bytes,0);
                    if (memory!=IntPtr.Zero) memories.Add(memory,i+1);
                    if (constant!=IntPtr.Zero && !constants.ContainsKey(constant)) constants.Add(constant,i);
                }
            }
            internal int Constant(IntPtr pointer) => pointer==IntPtr.Zero ? -1 : constants.TryGetValue(pointer,out var index) ? index : UnknownEdge;
            internal int Memory(IntPtr pointer)
            {
                if (pointer==IntPtr.Zero) return 0;
                if (memories.TryGetValue(pointer,out var id)) return id;
                if (Additional.Count==MaximumEntries) throw Error("archived controller memory count exceeded");
                Additional.Add(ReadMemory(pointer,reader.Memory)); Pointers.Add(pointer); id=-Additional.Count; memories.Add(pointer,id); return id;
            }
        }
        private static bool FencePending(Fence f) => f.Group >= 0 && (f.Tag == unchecked(f.Version-2U) || f.Tag == unchecked(f.Version-1U));
        private static void ValidateResidual(Data data)
        {
            if (data.ExtraMemories==null || data.ExtraMemories.Length>MaximumEntries) throw Error("extra controller memory directory differs");
            foreach (var memory in data.ExtraMemories) ValidateMemory(memory);
            var seen=new HashSet<int>();
            void Memory(int value) {
                if (value < -data.ExtraMemories.Length || value > data.Playables.Length || value>0 && data.Playables[value-1].Memory==null) throw Error("recorded controller memory lacks a typed owner");
                if (value<0) seen.Add(value);
            }
            foreach (var row in data.Rows) {
                var r=row.Retained; if (r==null) continue;
                if (r.Arrays==null || r.Arrays.Length!=6 || r.AllocatorRvas==null || r.AllocatorRvas.Length!=6 || r.Labels==null || r.Labels.Length!=6
                    || r.SetLabels==null || r.SetLabels.Length!=2 || r.DestroyRvas==null || r.DestroyRvas.Length!=2 || r.Frames==null || r.FrameCapacity<r.Frames.Length || r.FrameCapacity>MaximumEntries
                    || r.Jobs==null || r.Jobs.Length>MaximumEntries || r.Bound==null || r.Bound.Length>MaximumEntries || r.SamplingConstant<UnknownEdge || r.SamplingConstant>=data.Playables.Length)
                    throw Error("native Animator allocator/recorder/sampling layout differs");
                var counts=ResidualCounts(r,row);
                for (int i=0;i<6;i++) {
                    var s=r.Arrays[i]; if (s==null || s.Capacity<counts[i] || s.Capacity>MaximumEntries || s.Capacity!=0 && !s.Allocated) throw Error("Animator native array ownership/extent differs");
                }
                foreach (var entry in r.Bound)
                    if (entry==null || entry.Output<UnknownEdge || entry.Output>=data.Rows.Length || entry.Output>=0 && data.Rows[entry.Output].Output!=entry.Output
                        || entry.Playable<UnknownEdge || entry.Playable<0 && entry.Graph!=-1 || entry.Playable>=0 && !(data.Graphs?.Any(g=>g!=null && g.Owner==entry.Graph && g.Nodes!=null && entry.Playable<g.Nodes.Length)??false))
                        throw Error("bound playable output/graph node alias differs");
                foreach (var f in r.Jobs.Concat(new[]{r.Pose})) if (f==null || f.Group < -1 || f.Group==-1 && f.Tag!=0) throw Error("job fence identity/tag differs");
                Memory(r.SamplingMemory); foreach (var frame in r.Frames) { if (frame==null) throw Error("recorder frame absent"); Memory(frame.Memory); }
            }
            if (seen.Count!=data.ExtraMemories.Length) throw Error("unowned recorded/sampling controller memory");
        }
        private static int[] ResidualCounts(Residual r,Row row) => new[] {r.Bound.Length,row.Cached.Length,row.Renderers.Length,row.Clearing.Length,row.CachedClips.Length,r.Jobs.Length};
        private static IEnumerable<string> ResidualGaps(Residual? r,Row row)
        {
            if (r==null) { yield return "retained-allocator-recorder-and-jobs-witness-absent"; yield break; }
            if (r.AllocatorRvas.Any(rva=>rva!=0x15d5b60) || r.PlaybackVtable!=0x15d5b70 || r.Labels.Any(label=>label!=30)
                || r.SetLabels.Any(label=>label!=30) || r.DestroyRvas.Any(rva=>rva!=0)) yield return "unbound-native-allocator-function";
            if (r.SamplingConstant==UnknownEdge) yield return "sampling-controller-constant-unbound";
            if (r.Bound.Any(b=>b.Output==UnknownEdge || b.Playable==UnknownEdge)) yield return "bound-playable-not-owned-by-graph-directory";
            if (r.FrameAllocated!=(r.FrameCapacity!=0)) yield return "recorder-vector-owner";
            if (r.FrameCapacity!=r.Frames.Length || r.FramePadding) yield return "recorder-frame-storage-requires-common-canon";
            if (!ValidRecorder(row.Fields[6],r.Frames)) yield return "recorder-cursor-or-active-frame-memory";
            var counts=ResidualCounts(r,row);
            for (int i=0;i<6;i++) if (!Compact(r.Arrays[i],counts[i]))
                yield return OwnedStorage(r.Arrays[i]) ? "retained-vector-requires-common-canon" : "retained-native-array-owner";
            foreach (var f in r.Jobs.Concat(new[]{r.Pose})) {
                if (f.Group>=0) yield return FencePending(f) ? "outstanding-animator-job-producer" : "completed-animator-job-group-binding";
            }
        }
        private static bool ValidRecorder(byte[] state,Frame[] frames)
        {
            int count=M.I32(state,0), start=M.I32(state,4), stop=M.I32(state,8), cursor=M.I32(state,12);
            if (count < -1 || count>0 && count!=frames.Length || start < -1 || stop < -1 || cursor < -1
                || start>=frames.Length || stop>=frames.Length || cursor>=frames.Length) return false;
            if (start==-1 || stop==-1) return start==-1 && stop==-1 && cursor==-1;
            if (cursor==-1 || count<0 || count==0 && stop<start) return false;
            for (int i=start;;i=(i+1)%frames.Length) {
                if (frames[i].Memory==0) return false;
                if (i==stop) return true;
            }
        }
    }
}
