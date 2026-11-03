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
            public Connections? Ports;
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
                    if (M.Ptr(n,8)!=last || M.Ptr(n,32)!=graph || Indices.ContainsKey(p) || handle==IntPtr.Zero || M.Ptr(reader.Read(IntPtr.Add(handle,40),8),0)!=p)
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
                            var h=M.Ptr(IntPtr.Add(Nodes[target.Node],40)); var generation=M.U32(M.Read(IntPtr.Add(h,32),4),0);
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
                        || s.JobReflection || b.JobReflection || s.JobData || b.JobData || s.MethodTable!=b.MethodTable || (s.Ports==null)!=(b.Ports==null))
                        throw Error("target graph node subtype or resource binding differs");
                    Bits(IntPtr.Add(ptr,56),s.Modes);
                    // The controller owner writes this same time block once.
                    if (s.Controller<0) {
                        Bits(IntPtr.Add(ptr,80),s.Clock);
                        if (AnimationFlagBytes(s.Vtable) != 0) PrepareAnimationFlags(s.AnimationFlags,b.AnimationFlags,IntPtr.Add(ptr,256),writes);
                    }
                    if (s.Ports==null) continue;
                    var a=s.Ports;var old=b.Ports!;
                    if (a.Bucket!=old.Bucket || Json(a.InputStorage)!=Json(old.InputStorage) || Json(a.OutputStorage)!=Json(old.OutputStorage)
                        || a.Inputs.Length!=old.Inputs.Length || a.Outputs.Length!=old.Outputs.Length) throw Error("native connection pool/port array extent differs");
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
            if (graph.NameStorage) yield return "graph-name-native-storage";
            if (M.I32(graph.Timing,16)!=graph.Nodes.Length) yield return "graph-node-count-vs-intrusive-directory";
            if (!Compact(graph.Arrays[0],graph.Roots.Length) || !Compact(graph.Arrays[1],graph.Retired.Length)) yield return "graph-root-retirement-vector-storage";
            if (graph.Roots.Concat(graph.Retired).Any(h=>h.Node==UnknownEdge)) yield return "stale-root-or-retirement-handle";
            foreach (var n in graph.Nodes) {
                if (n.Vtable!=0x16a23b8 && n.Controller<0) yield return "native-playable-subtype:"+n.Vtable.ToString("x");
                if (n.Evaluator || n.ManagedHandle || n.JobReflection || n.JobData) yield return "playable-native-job-script-or-property-producer";
                if (n.Ports!=null && (n.Ports.Inputs.Any(p=>p.From==UnknownEdge) || n.Ports.Outputs.Contains(UnknownEdge))) yield return "connection-endpoint-outside-owning-graph";
                if (n.Ports!=null && (!OwnedStorage(n.Ports.InputStorage) || !OwnedStorage(n.Ports.OutputStorage))) yield return "connection-vector-storage-owner";
            }
        }
    }
}
