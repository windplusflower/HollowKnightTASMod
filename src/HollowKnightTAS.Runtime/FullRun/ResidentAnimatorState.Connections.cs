using System;
using System.Collections.Generic;
using System.Linq;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAnimatorState
    {
        internal sealed class Topology
        {
            public int Owner;
            public byte[] Timing = Array.Empty<byte>();
            public byte Flags, RecursingRaw;
            public bool Recursing, ResolverHandle, NameStorage;
            public string Name = "";
            public Node[] Nodes = Array.Empty<Node>();
            public LocalHandle[] Roots=Array.Empty<LocalHandle>(), Retired=Array.Empty<LocalHandle>();
            public Storage[] Arrays=Array.Empty<Storage>();
        }
        internal sealed class Node
        {
            public int Vtable, PlayerType, Controller = -1;
            public byte[] Modes=Array.Empty<byte>(), Clock=Array.Empty<byte>(), AnimationFlags=Array.Empty<byte>();
            public short MethodTable;
            public bool Evaluator, ManagedHandle, JobReflection, JobData;
            public int? EmptyInlineJobDataOffset;
            public Connections? Ports;
            public PoseState? Pose;
            public MixerState? Mixer;
            public LayerMixerState? Layer;
            public ClipState? Clip;
        }
        internal sealed class MixerState
        {
            public string Asset = "";
            public int HeapVtable, BlockVtable, HeapLabel, BlockLabel;
            public bool BlockStorage;
            public int Weight = -1, Mask = -1, State = -1;
            public byte[] ActiveWeights = Array.Empty<byte>();
            public int[] ActivePlayables = Array.Empty<int>();
            public Storage WeightStorage = new Storage(), PlayableStorage = new Storage();
            public byte[] Subtype = Array.Empty<byte>();
            public int MachineOwner = -1, MachineIndex = -1;
        }
        internal sealed class PoseState
        {
            public string Asset = "";
            public int State = -1;
            public int HeapVtable, BlockVtable, HeapLabel, BlockLabel;
            public bool BlockStorage;
            public byte[] Flags = new byte[3];
        }
        internal sealed class Connections
        {
            public int Bucket;
            public Storage InputStorage=new Storage(), OutputStorage=new Storage();
            public InPort[] Inputs=Array.Empty<InPort>();
            public int[] Outputs=Array.Empty<int>();
        }
        internal sealed class InPort { public int From=-1, Port, WeightBits; }
        internal sealed class LocalHandle { public int Node=-1; public bool Flag; }
        private sealed class GraphView
        {
            internal readonly Topology Data;
            internal readonly List<IntPtr> Nodes = new List<IntPtr>();
            internal readonly Dictionary<IntPtr,int> Indices = new Dictionary<IntPtr,int>();
            private readonly IntPtr graph;
            private readonly List<IntPtr> inputs = new List<IntPtr>(), outputs = new List<IntPtr>();
            private readonly IntPtr roots, retired;
            internal GraphView(int owner,IntPtr graph,MemoryReader reader,Func<IntPtr,int> vtable,Dictionary<IntPtr,int> controllers)
            {
                byte[] ArrayBytes(byte[] header,int field,int stride) {
                    var storage=StorageAt(header,field); var count=M.U64(header,field+16);
                    if(count>(ulong)storage.Capacity || storage.Capacity!=0 && !storage.Allocated) throw Error("invalid graph vector extent");
                    return reader.Read(M.Ptr(header,field),checked((int)count*stride));
                }
                this.graph=graph; var bytes=reader.Read(graph,232);
                Data=new Topology {Owner=owner,Timing=M.Slice(bytes,24,4).Concat(M.Slice(bytes,32,24)).ToArray(),Flags=bytes[152],Recursing=bytes[184]!=0,RecursingRaw=bytes[184],
                    ResolverHandle=HandlePresent(bytes,160),Name=M.String(IntPtr.Add(graph,192)),NameStorage=M.Ptr(bytes,192)!=IntPtr.Zero,
                    Arrays=new[]{StorageAt(bytes,120),StorageAt(bytes,72)}};
                var head=IntPtr.Add(graph,56); var last=head; var cursor=M.Ptr(bytes,64); var headers=new List<byte[]>();
                while (cursor!=head) {
                    if (cursor.ToInt64()<=8 || Nodes.Count==MaximumEntries) throw Error("graph playable list escaped its bound");
                    var p=IntPtr.Subtract(cursor,8); var n=reader.Read(p,192); var handle=M.Ptr(n,40);
                    if (M.Ptr(n,8)!=last || M.Ptr(n,32)!=graph || Indices.ContainsKey(p) || handle!=IntPtr.Zero && M.Ptr(reader.Read(IntPtr.Add(handle,40),8),0)!=p)
                        throw Error("graph playable lacks reciprocal list/graph/handle");
                    Indices.Add(p,Nodes.Count); Nodes.Add(p); headers.Add(n); last=cursor; cursor=M.Ptr(n,16);
                }
                if (M.Ptr(bytes,56)!=last) throw Error("graph playable list tail differs");
                int Index(IntPtr p) => p==IntPtr.Zero ? -1 : Indices.TryGetValue(p,out var index) ? index : UnknownEdge;
                var lists=new HashSet<IntPtr>();
                Data.Nodes=new Node[Nodes.Count];
                for (int i=0;i<Nodes.Count;i++) {
                    var n=headers[i]; var p=Nodes[i]; var row=new Node { Vtable=vtable(M.Ptr(n,0)), PlayerType=M.I32(n,24),
                        Controller=controllers.TryGetValue(p,out var active) ? active : -1, Modes=M.Slice(n,56,12),Clock=M.Slice(n,80,58),
                        MethodTable=BitConverter.ToInt16(n,168),Evaluator=M.Ptr(n,72)!=IntPtr.Zero,ManagedHandle=HandlePresent(n,144),
                        JobReflection=M.Ptr(n,176)!=IntPtr.Zero,JobData=M.Ptr(n,184)!=IntPtr.Zero };
                    row.EmptyInlineJobDataOffset = CaptureEmptyInlineJobDataOffset(row.Vtable,p,M.Ptr(n,176),M.Ptr(n,184));
                    if (AnimationFlagBytes(row.Vtable) != 0) row.AnimationFlags = reader.Read(IntPtr.Add(p,256),5);
                    Data.Nodes[i]=row; var ports=M.Ptr(n,48);
                    if (ports==IntPtr.Zero) { inputs.Add(IntPtr.Zero); outputs.Add(IntPtr.Zero); continue; }
                    if (!lists.Add(ports)) throw Error("a connection-list allocation is claimed by multiple live playables");
                    var c=reader.Read(ports,104); inputs.Add(M.Ptr(c,32)); outputs.Add(M.Ptr(c,64));
                    row.Ports=new Connections {Bucket=M.I32(c,96),InputStorage=StorageAt(c,32),OutputStorage=StorageAt(c,64),
                        Inputs=Decode(ArrayBytes(c,32,16),16,(v,j)=>new InPort {From=Index(M.Ptr(v,j)),WeightBits=M.I32(v,j+8),Port=M.I32(v,j+12)}),
                        Outputs=Decode(ArrayBytes(c,64,8),8,(v,j)=>Index(M.Ptr(v,j)))};
                }
                LocalHandle Handle(byte[] array,int at) {
                    var h=M.Ptr(array,at); var version=M.U32(array,at+8);
                    var result=new LocalHandle {Flag=(version&1)!=0, Node=version==0 ? -1 : UnknownEdge};
                    if (h!=IntPtr.Zero) {
                        var n=reader.Read(h,48); var p=M.Ptr(n,40);
                        result.Node=M.U32(n,32)==(version&~1U) && Indices.TryGetValue(p,out var slot) && M.Ptr(headers[slot],40)==h ? slot : UnknownEdge;
                    }
                    return result;
                }
                roots=M.Ptr(bytes,120); retired=M.Ptr(bytes,72);
                Data.Roots=Decode(ArrayBytes(bytes,120,16),16,Handle); Data.Retired=Decode(ArrayBytes(bytes,72,16),16,Handle);
            }
            private static bool HandlePresent(byte[] bytes,int offset) => M.U64(bytes,offset)!=ulong.MaxValue || M.Ptr(bytes,offset+16)!=IntPtr.Zero;
            internal void CaptureBodies(Reader reader)
            {
                for (int index = 0; index < Nodes.Count; index++) {
                    var node = Data.Nodes[index];
                    if (node.Vtable == 0x15d8b80) node.Clip = CaptureClip(Nodes[index], node, reader);
                    if (node.Vtable == 0x15d9380) node.Layer = CaptureLayerMixer(Nodes[index], node, reader);
                    if (node.Vtable == 0x15d8aa8 || node.Vtable == 0x15d97a8) {
                        var mixer = reader.Memory.Read(IntPtr.Add(Nodes[index], 272), 88);
                        var allocation = reader.Memory.Read(IntPtr.Add(Nodes[index], 192), 76);
                        var subtype = reader.Memory.Read(IntPtr.Add(Nodes[index], 360), node.Vtable == 0x15d8aa8 ? 24 : 5);
                        node.Mixer = new MixerState {
                            Asset = reader.Id<UnityEngine.Object>(M.I32(allocation,72)),
                            HeapVtable = reader.Rva(M.Ptr(allocation,0)), BlockVtable = reader.Rva(M.Ptr(allocation,16)),
                            HeapLabel = M.I32(allocation,8), BlockLabel = M.I32(allocation,56),
                            BlockStorage = M.Ptr(allocation,24) != IntPtr.Zero || M.Ptr(allocation,32) != IntPtr.Zero || M.U64(allocation,48) != 0,
                            // DeallocateBindings frees these allocations without
                            // clearing their pointer slots. The flag owns their lifetime.
                            Weight = node.AnimationFlags[4] == 1 ? reader.Live.Avatars!.Retained(M.Ptr(mixer, 0), AvatarKind.Weight) : -1,
                            Mask = node.AnimationFlags[4] == 1 ? reader.Live.Avatars!.Retained(M.Ptr(mixer, 8), AvatarKind.Mask) : -1,
                            State = node.AnimationFlags[4] == 1 ? reader.Live.Avatars!.Retained(M.Ptr(mixer, 16), AvatarKind.NodeState) : -1,
                            WeightStorage = StorageAt(mixer, 24), PlayableStorage = StorageAt(mixer, 56),
                            ActiveWeights = reader.ArrayBytes(mixer, 24, 4),
                            ActivePlayables = Decode(reader.ArrayBytes(mixer, 56, 8), 8, (bytes, at) => {
                                var pointer = M.Ptr(bytes, at);
                                return pointer == IntPtr.Zero ? -1 : Indices.TryGetValue(pointer, out var slot) ? slot : UnknownEdge;
                            }),
                            Subtype = node.Vtable == 0x15d8aa8 ? M.Slice(subtype,0,2).Concat(M.Slice(subtype,4,8)).ToArray() : subtype
                        };
                        if (node.Vtable == 0x15d8aa8)
                            reader.Live.Memories!.MachineBinding(M.Ptr(subtype,16),out node.Mixer.MachineOwner,out node.Mixer.MachineIndex);
                    }
                    if (node.Vtable != 0x15d9608) continue;
                    var body = reader.Memory.Read(IntPtr.Add(Nodes[index], 264), 19);
                    var allocators = reader.Memory.Read(IntPtr.Add(Nodes[index], 192), 64);
                    node.Pose = new PoseState {
                        Asset = reader.Id<UnityEngine.Object>(M.I32(body, 0)),
                        HeapVtable = reader.Rva(M.Ptr(allocators, 0)), BlockVtable = reader.Rva(M.Ptr(allocators, 16)),
                        HeapLabel = M.I32(allocators, 8), BlockLabel = M.I32(allocators, 56),
                        // heapPtr is uninitialized until a block exists. The
                        // constructor initializes both owners and blockSize.
                        BlockStorage = M.Ptr(allocators, 24) != IntPtr.Zero || M.Ptr(allocators, 32) != IntPtr.Zero || M.U64(allocators, 48) != 0,
                        // The constructor leaves this pointer uninitialized.
                        // AllocateBindings owns it only while +260 is set.
                        State = node.AnimationFlags[4] == 1
                            ? reader.Live.Avatars!.Retained(M.Ptr(body, 8), AvatarKind.NodeState) : -1,
                        Flags = M.Slice(body, 16, 3)
                    };
                }
            }
            internal void Canon(Action admitted, Playable[]? playables = null)
            {
                // Graph roots and retired handles are POD HHandles. Their
                // native handle owners stay registered while only the vector
                // backing is resized, using the same allocator as other arrays.
                var counts = new[] { Data.Roots.Length, Data.Retired.Length };
                var offsets = new[] { 120, 72 };
                for (int index = 0; index < counts.Length; index++) {
                    var storage = Data.Arrays[index];
                    if (Compact(storage, counts[index])) continue;
                    ResizePod(IntPtr.Add(graph, offsets[index]), counts[index], 16, 8, storage.Label, admitted);
                    SetCompact(storage, counts[index]);
                }
                static void ZeroStoragePadding(IntPtr header, Action admit) {
                    admit();
                    M.Write(IntPtr.Add(header, 12), new byte[4]);
                }
                for (int i = 0; i < Nodes.Count; i++) {
                    var row = Data.Nodes[i];
                    var ptr = Nodes[i];
                    if (row.Ports != null) {
                        var ports = M.Ptr(IntPtr.Add(ptr, 48));
                        if (row.Ports.InputStorage.Padding) { ZeroStoragePadding(IntPtr.Add(ports, 32), admitted); row.Ports.InputStorage.Padding = false; }
                        if (row.Ports.OutputStorage.Padding) { ZeroStoragePadding(IntPtr.Add(ports, 64), admitted); row.Ports.OutputStorage.Padding = false; }
                    }
                    if (row.Layer != null) {
                        if (row.Layer.ParameterStorage.Padding) { ZeroStoragePadding(IntPtr.Add(ptr, 192 + 144), admitted); row.Layer.ParameterStorage.Padding = false; }
                        if (row.AnimationFlags.Length > 4 && row.AnimationFlags[4] == 1 && row.Layer.StateStorage.Padding) {
                            ZeroStoragePadding(IntPtr.Add(ptr, 192 + 80), admitted);
                            row.Layer.StateStorage.Padding = false;
                        }
                    }
                    if (row.Mixer != null) {
                        if (row.Mixer.WeightStorage.Padding) { ZeroStoragePadding(IntPtr.Add(ptr, 272 + 24), admitted); row.Mixer.WeightStorage.Padding = false; }
                        if (row.Mixer.PlayableStorage.Padding) { ZeroStoragePadding(IntPtr.Add(ptr, 272 + 56), admitted); row.Mixer.PlayableStorage.Padding = false; }
                    }
                    if (playables != null && row.Controller >= 0 && row.Controller < playables.Length
                        && playables[row.Controller]?.Dependency is { } dep && dep.Behaviours.Padding) {
                        ZeroStoragePadding(IntPtr.Add(ptr, 336), admitted);
                        dep.Behaviours.Padding = false;
                    }
                }
            }
            internal void Prepare(Topology source,List<PendingWrite> writes)
            {
                Reject(GraphScalarGaps(source).Concat(GraphScalarGaps(Data)));
                if (Data.Owner!=source.Owner || Data.Nodes.Length!=source.Nodes.Length || Json(Data.Arrays)!=Json(source.Arrays) || Data.Name!=source.Name
                    || source.Recursing || Data.Recursing || source.ResolverHandle || Data.ResolverHandle || source.NameStorage!=Data.NameStorage) throw Error("native graph allocation or owner differs");
                void Bits(IntPtr address,byte[] desired) => writes.Add(new PendingWrite(address,M.Read(address,desired.Length),desired));
                void Handles(IntPtr address,LocalHandle[] wanted,LocalHandle[] before) {
                    if (wanted.Length!=before.Length) throw Error("graph handle array extent differs");
                    for (int i=0;i<wanted.Length;i++) {
                        var target=wanted[i]; var value=new byte[12];
                        if (target.Node>=0) {
                            var h=M.Ptr(IntPtr.Add(Nodes[target.Node],40));
                            if (h==IntPtr.Zero) throw Error("graph root/retirement playable lacks native handle");
                            var generation=M.U32(M.Read(IntPtr.Add(h,32),4),0);
                            BitConverter.GetBytes(h.ToInt64()).CopyTo(value,0); BitConverter.GetBytes((generation&~1U)|(target.Flag?1U:0)).CopyTo(value,8);
                        } else if (target.Node!=-1 || target.Flag) throw Error("stale graph root/retirement handle");
                        Bits(IntPtr.Add(address,i*16),value);
                    }
                }
                Bits(IntPtr.Add(graph,24),M.Slice(source.Timing,0,4)); Bits(IntPtr.Add(graph,32),M.Slice(source.Timing,4,24)); Bits(IntPtr.Add(graph,152),new[]{source.Flags});
                Handles(roots,source.Roots,Data.Roots); Handles(retired,source.Retired,Data.Retired);
                for (int i=0;i<Nodes.Count;i++) {
                    var s=source.Nodes[i];var b=Data.Nodes[i];var ptr=Nodes[i];
                    if (s.Vtable!=b.Vtable || s.PlayerType!=b.PlayerType || s.Controller!=b.Controller || s.Evaluator || b.Evaluator || s.ManagedHandle || b.ManagedHandle
                        || s.JobReflection || b.JobReflection || s.JobData != b.JobData
                        || s.JobData && !KnownEmptyInlineJobData(s) || b.JobData && !KnownEmptyInlineJobData(b)
                        || s.EmptyInlineJobDataOffset != b.EmptyInlineJobDataOffset || s.MethodTable!=b.MethodTable || (s.Ports==null)!=(b.Ports==null))
                        throw Error("target graph node subtype or resource binding differs");
                    Bits(IntPtr.Add(ptr,56),s.Modes);
                    // The controller owner writes this same time block once.
                    if (s.Controller<0) {
                        Bits(IntPtr.Add(ptr,80),s.Clock);
                        if (AnimationFlagBytes(s.Vtable) != 0) PrepareAnimationFlags(s.AnimationFlags,b.AnimationFlags,IntPtr.Add(ptr,256),writes);
                    }
                    if ((s.Pose == null) != (b.Pose == null)) throw Error("pose playable body presence differs");
                    if (s.Pose != null) {
                        var poseDiffs = new List<string>();
                        if (s.Pose.Asset != b.Pose!.Asset) poseDiffs.Add($"Asset({s.Pose.Asset}!={b.Pose.Asset})");
                        if (s.Pose.State != b.Pose.State) poseDiffs.Add($"State({s.Pose.State}!={b.Pose.State})");
                        if (s.Pose.BlockStorage || b.Pose.BlockStorage) poseDiffs.Add($"BlockStorage({s.Pose.BlockStorage},{b.Pose.BlockStorage})");
                        if (s.Pose.HeapVtable != b.Pose.HeapVtable) poseDiffs.Add($"HeapVt({s.Pose.HeapVtable:x}!={b.Pose.HeapVtable:x})");
                        if (s.Pose.BlockVtable != b.Pose.BlockVtable) poseDiffs.Add($"BlockVt({s.Pose.BlockVtable:x}!={b.Pose.BlockVtable:x})");
                        if (s.Pose.HeapLabel != b.Pose.HeapLabel) poseDiffs.Add($"HeapLbl({s.Pose.HeapLabel}!={b.Pose.HeapLabel})");
                        if (s.Pose.BlockLabel != b.Pose.BlockLabel) poseDiffs.Add($"BlockLbl({s.Pose.BlockLabel}!={b.Pose.BlockLabel})");
                        if (poseDiffs.Count != 0)
                            throw Error($"pose playable asset or state allocation differs [graphOwner={Data.Owner}, node={i}]: {string.Join("; ", poseDiffs)}");
                        Bits(IntPtr.Add(ptr,280),s.Pose.Flags);
                    }
                    if ((s.Mixer == null) != (b.Mixer == null)) throw Error("mixer playable body presence differs");
                    if ((s.Layer == null) != (b.Layer == null)) throw Error("layer mixer body presence differs");
                    if ((s.Clip == null) != (b.Clip == null)) throw Error("clip typed body presence differs");
                    if (s.Clip != null) PrepareClip(ptr,s.Clip,b.Clip!,writes);
                    if (s.Layer != null) PrepareLayerMixer(ptr, s.Layer, b.Layer!, writes);
                    if (s.Mixer != null) {
                        var wanted = s.Mixer; var before = b.Mixer!;
                        var mixerDiffs = new List<string>();
                        if (wanted.Weight != before.Weight) mixerDiffs.Add($"Weight({wanted.Weight}!={before.Weight})");
                        if (wanted.Mask != before.Mask) mixerDiffs.Add($"Mask({wanted.Mask}!={before.Mask})");
                        if (wanted.State != before.State) mixerDiffs.Add($"State({wanted.State}!={before.State})");
                        if (wanted.Asset != before.Asset) mixerDiffs.Add($"Asset({wanted.Asset}!={before.Asset})");
                        if (wanted.HeapVtable != before.HeapVtable) mixerDiffs.Add($"HeapVt({wanted.HeapVtable:x}!={before.HeapVtable:x})");
                        if (wanted.BlockVtable != before.BlockVtable) mixerDiffs.Add($"BlockVt({wanted.BlockVtable:x}!={before.BlockVtable:x})");
                        if (wanted.HeapLabel != before.HeapLabel) mixerDiffs.Add($"HeapLbl({wanted.HeapLabel}!={before.HeapLabel})");
                        if (wanted.BlockLabel != before.BlockLabel) mixerDiffs.Add($"BlockLbl({wanted.BlockLabel}!={before.BlockLabel})");
                        if (wanted.BlockStorage || before.BlockStorage) mixerDiffs.Add($"BlockStorage({wanted.BlockStorage},{before.BlockStorage})");
                        if (wanted.MachineOwner != before.MachineOwner) mixerDiffs.Add($"MachineOwner({wanted.MachineOwner}!={before.MachineOwner})");
                        if (wanted.MachineIndex != before.MachineIndex) mixerDiffs.Add($"MachineIndex({wanted.MachineIndex}!={before.MachineIndex})");
                        if (Json(wanted.WeightStorage) != Json(before.WeightStorage)) mixerDiffs.Add($"WeightStorage({Json(wanted.WeightStorage)}!={Json(before.WeightStorage)})");
                        if (Json(wanted.PlayableStorage) != Json(before.PlayableStorage)) mixerDiffs.Add($"PlayableStorage({Json(wanted.PlayableStorage)}!={Json(before.PlayableStorage)})");
                        if (wanted.ActiveWeights.Length / 4 > before.WeightStorage.Capacity) mixerDiffs.Add($"ActiveWeightsCap({wanted.ActiveWeights.Length / 4}>{before.WeightStorage.Capacity})");
                        if (wanted.ActivePlayables.Length > before.PlayableStorage.Capacity) mixerDiffs.Add($"ActivePlayablesCap({wanted.ActivePlayables.Length}>{before.PlayableStorage.Capacity})");
                        if (mixerDiffs.Count != 0)
                            throw Error($"mixer playable native allocation differs [graphOwner={Data.Owner}, node={i}, vt={s.Vtable:x}]: {string.Join("; ", mixerDiffs)}");
                        if (wanted.ActiveWeights.Length != before.ActiveWeights.Length)
                            Bits(IntPtr.Add(ptr, 296 + 16), BitConverter.GetBytes((ulong)(wanted.ActiveWeights.Length / 4)));
                        if (wanted.ActiveWeights.Length != 0)
                            Bits(M.Ptr(IntPtr.Add(ptr, 296)), wanted.ActiveWeights);
                        if (wanted.ActivePlayables.Length != before.ActivePlayables.Length)
                            Bits(IntPtr.Add(ptr, 328 + 16), BitConverter.GetBytes((ulong)wanted.ActivePlayables.Length));
                        var active = M.Ptr(IntPtr.Add(ptr,328));
                        for (int j = 0; j < wanted.ActivePlayables.Length; j++) {
                            int slot = wanted.ActivePlayables[j];
                            var local = slot == -1 ? IntPtr.Zero : slot >= 0 && slot < Nodes.Count ? Nodes[slot] : throw Error("mixer playable is outside owning graph");
                            Bits(IntPtr.Add(active,j*8),BitConverter.GetBytes(local.ToInt64()));
                        }
                        if (s.Vtable == 0x15d8aa8) {
                            Bits(IntPtr.Add(ptr,360),M.Slice(wanted.Subtype,0,2));
                            Bits(IntPtr.Add(ptr,364),M.Slice(wanted.Subtype,2,8));
                        } else Bits(IntPtr.Add(ptr,360),wanted.Subtype);
                    }
                    if (s.Ports==null) continue;
                    var a=s.Ports;var old=b.Ports!;
                    if (a.Bucket!=old.Bucket || Json(a.InputStorage)!=Json(old.InputStorage) || Json(a.OutputStorage)!=Json(old.OutputStorage)
                        || a.Inputs.Length!=old.Inputs.Length || a.Outputs.Length!=old.Outputs.Length)
                        throw Error($"native connection pool/port array extent differs [graphOwner={Data.Owner}, node={i}]: Bucket({a.Bucket}!={old.Bucket}) InStor({Json(a.InputStorage)}!={Json(old.InputStorage)}) OutStor({Json(a.OutputStorage)}!={Json(old.OutputStorage)}) In({a.Inputs.Length}!={old.Inputs.Length}) Out({a.Outputs.Length}!={old.Outputs.Length})");
                    IntPtr NodePtr(int index) => index==-1 ? IntPtr.Zero : index>=0 && index<Nodes.Count ? Nodes[index] : throw Error("connection is outside owning graph");
                    for (int j=0;j<a.Inputs.Length;j++) {
                        var port=a.Inputs[j]; var v=BitConverter.GetBytes(NodePtr(port.From).ToInt64()).Concat(BitConverter.GetBytes(port.WeightBits)).Concat(BitConverter.GetBytes(port.Port)).ToArray();
                        Bits(IntPtr.Add(inputs[i],j*16),v);
                    }
                    for (int j=0;j<a.Outputs.Length;j++) Bits(IntPtr.Add(outputs[i],j*8),BitConverter.GetBytes(NodePtr(a.Outputs[j]).ToInt64()));
                }
            }
        }
        internal static void ValidateTopology(Topology graph)
        {
            if (graph==null || graph.Owner<0 || graph.Timing==null || graph.Timing.Length!=28 || !Text(graph.Name) || graph.Nodes==null || graph.Nodes.Length>MaximumEntries
                || graph.Arrays==null || graph.Arrays.Length!=2 || graph.Roots==null || graph.Retired==null) throw Error("graph topology directory differs");
            void Storage(Storage s,int count) { if (s==null || count>s.Capacity || s.Capacity>MaximumEntries || s.Capacity!=0 && !s.Allocated) throw Error("graph array allocation differs"); }
            void Handle(LocalHandle h) { if (h==null || h.Node<UnknownEdge || h.Node>=graph.Nodes.Length || h.Node==-1 && h.Flag) throw Error("graph handle index differs"); }
            Storage(graph.Arrays[0],graph.Roots.Length);Storage(graph.Arrays[1],graph.Retired.Length);
            foreach (var h in graph.Roots.Concat(graph.Retired)) Handle(h);
            for (int i=0;i<graph.Nodes.Length;i++) {
                var row=graph.Nodes[i];
                if (row==null || row.Modes==null || row.Modes.Length!=12 || row.Clock==null || row.Clock.Length!=58 || row.AnimationFlags==null || row.AnimationFlags.Length!=AnimationFlagBytes(row.Vtable) || row.Controller < -1) throw Error("playable base fields differ");
                if (row.EmptyInlineJobDataOffset.HasValue && !KnownEmptyInlineJobData(row))
                    throw Error("playable empty inline job-data owner/type differs");
                if (row.Layer != null) ValidateLayerMixer(row);
                if (row.Clip != null) ValidateClip(row);
                if (row.Pose != null && (row.Vtable != 0x15d9608 || !Text(row.Pose.Asset) || row.Pose.State < -1
                    || row.Pose.Flags == null || row.Pose.Flags.Length != 3 || row.AnimationFlags[4] == 0 && row.Pose.State != -1
                    || row.AnimationFlags[4] == 1 && row.Pose.State == -1))
                    throw Error("pose playable typed body differs");
                if (row.Mixer != null) {
                    var mixer = row.Mixer;
                    if (row.Vtable != 0x15d8aa8 && row.Vtable != 0x15d97a8 || mixer.ActiveWeights == null || mixer.ActiveWeights.Length % 4 != 0
                        || !Text(mixer.Asset) || mixer.Subtype == null || mixer.Subtype.Length != (row.Vtable == 0x15d8aa8 ? 10 : 5)
                        || mixer.MachineOwner < UnknownEdge || mixer.MachineIndex < -1 || (mixer.MachineOwner < 0) != (mixer.MachineIndex == -1)
                        || row.Vtable == 0x15d97a8 && (mixer.MachineOwner != -1 || mixer.MachineIndex != -1)
                        || mixer.ActivePlayables == null || mixer.ActivePlayables.Any(slot => slot < UnknownEdge || slot >= graph.Nodes.Length)
                        || mixer.Weight < -1 || mixer.Mask < -1 || mixer.State < -1
                        || row.AnimationFlags[4] == 0 && (mixer.Weight != -1 || mixer.Mask != -1 || mixer.State != -1)
                        || row.AnimationFlags[4] == 1 && (mixer.Weight == -1 || mixer.Mask == -1 || mixer.State == -1))
                        throw Error("mixer playable typed body differs");
                    Storage(mixer.WeightStorage,mixer.ActiveWeights.Length/4);
                    Storage(mixer.PlayableStorage,mixer.ActivePlayables.Length);
                }
                var ports=row.Ports; if (ports==null) continue;
                if (ports.Inputs==null || ports.Outputs==null) throw Error("connection members absent");
                Storage(ports.InputStorage,ports.Inputs.Length);Storage(ports.OutputStorage,ports.Outputs.Length);
                foreach (var input in ports.Inputs) {
                    if (input==null || input.From<UnknownEdge || input.From>=graph.Nodes.Length) throw Error("connection input index differs");
                    if (input.From<0) continue;
                    var opposite=graph.Nodes[input.From]?.Ports?.Outputs;
                    if (opposite==null || input.Port<0 || input.Port>=opposite.Length || opposite[input.Port]!=i) throw Error("input/output connection is not reciprocal");
                }
                for (int j=0;j<ports.Outputs.Length;j++) {
                    var target=ports.Outputs[j];
                    if (target<UnknownEdge || target>=graph.Nodes.Length || target>=0 && !(graph.Nodes[target]?.Ports?.Inputs?.Any(p=>p!=null && p.From==i && p.Port==j)??false))
                        throw Error("output has no reciprocal input port");
                }
            }
        }
        private static IEnumerable<string> GraphGaps(Topology graph)
        {
            foreach (var gap in GraphScalarGaps(graph)) yield return gap;
            if (graph.Recursing || graph.ResolverHandle) yield return "graph-evaluation-or-managed-resolver";
            // The name is a target-local string dependency. Prepare requires
            // both its text and storage presence to match and never copies its pointer.
            if (M.I32(graph.Timing,16)!=graph.Nodes.Length) yield return "graph-node-count-vs-intrusive-directory";
            if (graph.Arrays.Any(storage => !OwnedStorage(storage))) yield return "graph-root-retirement-storage-owner";
            if (!Compact(graph.Arrays[0],graph.Roots.Length) || !Compact(graph.Arrays[1],graph.Retired.Length)) yield return "graph-root-retirement-vector-storage";
            if (graph.Roots.Concat(graph.Retired).Any(h=>h.Node==UnknownEdge)) yield return "stale-root-or-retirement-handle";
            foreach (var n in graph.Nodes) {
                if (n.Vtable!=0x16a23b8 && n.Controller<0 && n.Pose==null && n.Mixer==null && n.Layer==null && n.Clip==null) yield return "native-playable-subtype:"+n.Vtable.ToString("x");
                if (n.Clip != null) foreach (var gap in ClipGaps(n.Clip)) yield return gap;
                if (n.Layer != null) foreach (var gap in LayerMixerGaps(n.Layer)) yield return gap;
                if (n.Pose?.Asset == Unbound) yield return "pose-playable-asset-without-binding";
                if (n.Mixer != null && (!OwnedStorage(n.Mixer.WeightStorage) || !OwnedStorage(n.Mixer.PlayableStorage)))
                    yield return "mixer-vector-storage-owner";
                if (n.Mixer != null && (n.Mixer.WeightStorage.Padding || n.Mixer.PlayableStorage.Padding))
                    yield return "playable-vector-padding-requires-common-canon";
                if (n.Mixer != null && n.Mixer.ActivePlayables.Contains(UnknownEdge)) yield return "mixer-active-playable-outside-owning-graph";
                if (n.Mixer != null && (n.Mixer.HeapVtable != 0x15d5b60 || n.Mixer.BlockVtable != 0x15d6cf8
                    || n.Mixer.HeapLabel != 30 || n.Mixer.BlockLabel != 30 || n.Mixer.BlockStorage)) yield return "mixer-playable-native-allocator-dependency";
                if (n.Mixer?.Asset == Unbound) yield return "mixer-playable-asset-without-binding";
                if (n.Mixer?.MachineOwner == UnknownEdge) yield return "mixer-state-machine-without-owner";
                if (n.Pose != null && (n.Pose.HeapVtable != 0x15d5b60 || n.Pose.BlockVtable != 0x15d6cf8
                    || n.Pose.HeapLabel != 30 || n.Pose.BlockLabel != 30 || n.Pose.BlockStorage))
                    yield return "pose-playable-native-allocator-dependency";
                if (n.Evaluator || n.ManagedHandle || n.JobReflection || n.JobData && !KnownEmptyInlineJobData(n))
                    yield return "playable-native-job-script-or-property-producer";
                if (n.Ports!=null && (n.Ports.Inputs.Any(p=>p.From==UnknownEdge) || n.Ports.Outputs.Contains(UnknownEdge))) yield return "connection-endpoint-outside-owning-graph";
                if (n.Ports!=null && (!OwnedStorage(n.Ports.InputStorage) || !OwnedStorage(n.Ports.OutputStorage))) yield return "connection-vector-storage-owner";
                if (n.Ports!=null && (n.Ports.InputStorage.Padding || n.Ports.OutputStorage.Padding)) yield return "playable-vector-padding-requires-common-canon";
            }
        }
    }
}
