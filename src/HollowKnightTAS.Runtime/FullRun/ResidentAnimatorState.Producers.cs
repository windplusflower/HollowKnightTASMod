using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAnimatorState
    {
        internal sealed class Producers
        {
            public bool LocalServices;
            public Bucket[] Buckets=Array.Empty<Bucket>();
            public FreeStack[] Handles=Array.Empty<FreeStack>();
            public int GrowthBits, GrowNextFrame;
            public bool Tracker;
            public ulong CommandTag;
            public ClassMethod[] ClassMap=Array.Empty<ClassMethod>();
            public string[][] Methods=Array.Empty<string[]>(); public string[] NullMethods=Array.Empty<string>();
            public Storage MethodStorage=new Storage();
        }
        internal sealed class Bucket
        {
            public bool Created;
            public ulong Tag;
            public int Allocated, InFlight;
            public FreeConnections[] Free=Array.Empty<FreeConnections>();
        }
        internal sealed class FreeConnections
        {
            public Storage Inputs=new Storage(),Outputs=new Storage();
            public int InputCount, OutputCount;
        }
        internal sealed class FreeStack
        {
            public bool Created;
            public ulong Tag;
            public uint[] Versions=Array.Empty<uint>();
            public bool[] RetainsObject=Array.Empty<bool>();
        }
        internal sealed class ClassMethod {public string Key="";public short Index;}
        private static Producers CaptureLocalServices(IntPtr manager, MemoryReader reader)
        {
            // NullMethods is a process-local metadata dependency. Free pools,
            // class caches and allocation history are not resident graph D.
            var bytes = reader.Read(IntPtr.Add(manager,904),80);
            var methods = new string[10];
            for (int i=0;i<methods.Length;i++) {
                var pointer = M.Ptr(bytes,i*8);
                if (pointer == IntPtr.Zero) { methods[i] = ""; continue; }
                var method = ResidentScriptingHandles.ReadMethod(pointer) ?? throw Error("Director null method has no Mono target");
                methods[i] = method.Module.ModuleVersionId.ToString("D")+"/"+method.MetadataToken.ToString("x8")+"/"+method.DeclaringType?.AssemblyQualifiedName
                    +"/"+string.Join(",",method.GetGenericArguments().Select(t=>t.AssemblyQualifiedName??t.ToString()));
            }
            return new Producers { LocalServices = true, NullMethods = methods };
        }
        private static Producers CaptureProducers(IntPtr manager,MemoryReader reader)
        {
            var bytes=reader.Read(manager,1120);var p=new Producers {GrowthBits=M.I32(bytes,992),GrowNextFrame=M.I32(bytes,996),Tracker=M.Ptr(bytes,1112)!=IntPtr.Zero,
                Buckets=new Bucket[5],Handles=new FreeStack[3],MethodStorage=StorageAt(bytes,776)};
            var allocations=new HashSet<IntPtr>();var stacks=new HashSet<IntPtr>();
            List<byte[]> Walk(IntPtr stack,int length,out ulong tag) {
                var nodes=new List<byte[]>();tag=0;
                if (stack==IntPtr.Zero) return nodes;
                if (!stacks.Add(stack)) throw Error("DirectorManager atomic stacks share an allocation");
                var head=reader.Read(stack,16);var cursor=M.Ptr(head,0);tag=M.U64(head,8);
                while(cursor!=IntPtr.Zero) {
                    if (!allocations.Add(cursor) || allocations.Count>MaximumEntries) throw Error("DirectorManager free nodes overlap or exceed bound");
                    var entry=reader.Read(cursor,length);nodes.Add(entry);cursor=M.Ptr(entry,0);
                }
                return nodes;
            }
            for (int i=0;i<5;i++) {
                var stack=M.Ptr(bytes,1040+i*8);var b=new Bucket {Created=stack!=IntPtr.Zero,Allocated=M.I32(bytes,1000+i*4),InFlight=M.I32(bytes,1020+i*4)};
                var entries=Walk(stack,104,out b.Tag);
                b.Free=entries.Select(e=>{
                    if (M.I32(e,96)!=i) throw Error("native connection list in wrong free bucket");
                    return new FreeConnections {Inputs=StorageAt(e,32), Outputs=StorageAt(e,64),InputCount=Count64(M.U64(e,48)),OutputCount=Count64(M.U64(e,80))};
                }).ToArray();p.Buckets[i]=b;
            }
            for (int i=0;i<3;i++) {
                var stack=M.Ptr(bytes,1088+i*8);var h=new FreeStack {Created=stack!=IntPtr.Zero};var entries=Walk(stack,48,out h.Tag);
                h.Versions=entries.Select(e=>M.U32(e,32)).ToArray();h.RetainsObject=entries.Select(e=>M.Ptr(e,40)!=IntPtr.Zero).ToArray();p.Handles[i]=h;
            }
            var command=M.Ptr(bytes,1080);if (command!=IntPtr.Zero) {
                if (!stacks.Add(command)) throw Error("command stack aliases a free stack");
                p.CommandTag=M.U64(reader.Read(command,16),8);
            }
            p.ClassMap=ReadClasses(M.Ptr(bytes,760),Count64(M.U64(bytes,768)),reader);
            int count=Count64(M.U64(bytes,792));
            if (count>p.MethodStorage.Capacity) throw Error("Director method tables exceed capacity");
            var tables=reader.Read(M.Ptr(bytes,776),checked(count*80));
            string[] MethodTable(byte[] data,int offset) {
                var keys=new string[10];
                for (int i=0;i<10;i++) {
                    var ptr=M.Ptr(data,offset+i*8);
                    if (ptr==IntPtr.Zero) {keys[i]="";continue;}
                    var method=ResidentScriptingHandles.ReadMethod(ptr) ?? throw Error("Director scripting method has no Mono target");
                    keys[i]=method.Module.ModuleVersionId.ToString("D")+"/"+method.MetadataToken.ToString("x8")+"/"+method.DeclaringType?.AssemblyQualifiedName
                        +"/"+string.Join(",",method.GetGenericArguments().Select(t=>t.AssemblyQualifiedName??t.ToString()));
                }
                return keys;
            }
            p.Methods=Decode(tables,80,MethodTable);p.NullMethods=MethodTable(bytes,904);return p;
        }
        private static int Count64(ulong n) => n<=MaximumEntries ? (int)n : throw Error("Director allocation count exceeds bound");
        private static ClassMethod[] ReadClasses(IntPtr head,int count,MemoryReader reader)
        {
            var root=reader.Read(head,48);
            if (root[25]!=1 || root[24]!=1) throw Error("Director scripting map has no native tree sentinel");
            var top=M.Ptr(root,8);if (count==0) {
                if (M.Ptr(root,0)!=head || top!=head || M.Ptr(root,16)!=head) throw Error("empty scripting cache retains tree entries");
                return Array.Empty<ClassMethod>();
            }
            var pending=new Stack<(IntPtr Vertex,IntPtr Parent,ulong Low,ulong High)>();pending.Push((top,head,0,ulong.MaxValue));
            var seen=new HashSet<IntPtr>();var result=new List<ClassMethod>();IntPtr minimum=IntPtr.Zero,maximum=IntPtr.Zero;
            while (pending.Count!=0) {
                var v=pending.Pop();if (v.Vertex==head) continue;
                if (!seen.Add(v.Vertex) || seen.Count>count) throw Error("Director scripting class cache is cyclic or oversized");
                var node=reader.Read(v.Vertex,48);var key=M.Ptr(node,32);ulong order=M.U64(node,32);
                if (node[25]!=0 || node[24]>1 || M.Ptr(node,8)!=v.Parent || order<v.Low || order>v.High) throw Error("Director scripting class map tree ordering differs");
                string identity="<null>";
                if(key!=IntPtr.Zero) {
                    var type=ResidentScriptingHandles.ReadClass(key) ?? throw Error("native class has no loaded type identity");
                    identity=type.Module.ModuleVersionId.ToString("D")+"/"+type.MetadataToken.ToString("x8")+"/"+type.AssemblyQualifiedName;
                }
                result.Add(new ClassMethod {Key=identity,Index=BitConverter.ToInt16(node,40)});
                if (v.Low==0 && M.Ptr(node,0)==head) minimum=v.Vertex;
                if (v.High==ulong.MaxValue && M.Ptr(node,16)==head) maximum=v.Vertex;
                if (M.Ptr(node,0)!=head) pending.Push((M.Ptr(node,0),v.Vertex,v.Low,checked(order-1)));
                if (M.Ptr(node,16)!=head) pending.Push((M.Ptr(node,16),v.Vertex,checked(order+1),v.High));
            }
            if (result.Count!=count || M.Ptr(root,0)!=minimum || M.Ptr(root,16)!=maximum) throw Error("Director scripting class extrema/count differ");
            return result.OrderBy(r=>r.Key,StringComparer.Ordinal).ToArray();
        }
        private static void ValidateProducers(Producers p)
        {
            if (p != null && p.LocalServices) {
                if (p.Buckets == null || p.Buckets.Length != 0 || p.Handles == null || p.Handles.Length != 0
                    || p.GrowthBits != 0 || p.GrowNextFrame != 0 || p.Tracker || p.CommandTag != 0
                    || p.ClassMap == null || p.ClassMap.Length != 0 || p.Methods == null || p.Methods.Length != 0
                    || p.MethodStorage == null || p.MethodStorage.Label != 30 || !p.MethodStorage.Borrowed
                    || p.MethodStorage.Capacity != 0 || p.MethodStorage.Allocated || p.MethodStorage.Padding
                    || !Keys(p.NullMethods) || p.NullMethods.Length != 10)
                    throw Error("Director local service dependency differs");
                return;
            }
            if (p==null || p.Buckets==null || p.Buckets.Length!=5 || p.Handles==null || p.Handles.Length!=3 || p.MethodStorage==null || p.ClassMap==null
                || p.Methods==null || p.Methods.Length>MaximumEntries || p.MethodStorage.Capacity<p.Methods.Length || p.MethodStorage.Capacity>MaximumEntries
                || p.ClassMap.Length>MaximumEntries || !Keys(p.NullMethods) || p.NullMethods.Length!=10 || p.Methods.Any(m=>!Keys(m) || m.Length!=10)) throw Error("Director producer directory differs");
            string? previous=null;foreach(var c in p.ClassMap) {
                if(c==null || !Text(c.Key) || c.Key.Length==0 || StringComparer.Ordinal.Compare(previous,c.Key)>=0) throw Error("Director scripting class binding differs");previous=c.Key;
            }
            foreach(var bucket in p.Buckets) {
                if (bucket==null || bucket.Free==null || bucket.Allocated<0 || bucket.Allocated>MaximumEntries || bucket.InFlight<0 || bucket.InFlight>bucket.Allocated
                    || bucket.Free.Length!=bucket.Allocated-bucket.InFlight || !bucket.Created && (bucket.Allocated!=0 || bucket.Tag!=0)) throw Error("Director connection-pool ownership counts disagree");
                foreach(var item in bucket.Free)
                    if(item==null || item.Inputs==null || item.Outputs==null || item.InputCount<0 || item.OutputCount<0 || item.InputCount>item.Inputs.Capacity
                        || item.OutputCount>item.Outputs.Capacity || item.Inputs.Capacity>MaximumEntries || item.Outputs.Capacity>MaximumEntries)
                        throw Error("Director free connection vector extent differs");
            }
            foreach(var h in p.Handles)
                if(h==null || h.Versions==null || h.RetainsObject==null || h.Versions.Length>MaximumEntries || h.RetainsObject.Length!=h.Versions.Length
                    || !h.Created && (h.Versions.Length!=0 || h.Tag!=0)) throw Error("Director free handle stack shape differs");
        }
        private static IEnumerable<string> ProducerGaps(Data data)
        {
            var p=data.Registry.Producers;
            if (p==null) {if(data.Registry.Present) yield return "director-producer-ownership-witness-absent";yield break;}
            if (p.LocalServices) {
                if (p.NullMethods.Any(k => k.Length != 0)) yield return "director-native-null-method-dependency";
                if (data.Graphs.SelectMany(graph => graph.Nodes).Any(node => node.MethodTable >= 1))
                    yield return "director-live-managed-method-cache-dependency";
                yield break;
            }
            if (p.Tracker) yield return "director-debug-allocation-tracker";
            if (p.ClassMap.Length!=0 || p.Methods.Length!=0 || p.NullMethods.Any(k=>k.Length!=0)) yield return "director-managed-class-and-method-cache-rebuild";
            var inFlight=new int[5];
            foreach (var graph in data.Graphs) foreach (var node in graph.Nodes) if (node.Ports!=null) {
                int bucket=node.Ports.Bucket;
                if (bucket<0 || bucket>=inFlight.Length) {yield return "director-live-connection-bucket-unbound";continue;}
                inFlight[bucket]++;
            }
            for (int i=0;i<5;i++) if (p.Buckets[i].InFlight!=inFlight[i]) yield return "director-inflight-connection-outside-owned-graphs";
            if (p.Handles.Any(h=>h.RetainsObject.Any(value=>value))) yield return "director-free-handle-still-references-native-object";
            // Counts and an ABA witness establish observation only. A newly
            // scheduled native producer can mutate the command/free stacks
            // after preflight. Their ownership requires a real admission fence.
            if (data.Registry.Queues.CommandStack || p.Handles.Any(h=>h.Created) || p.Buckets.Any(b=>b.Created)) yield return "director-atomic-producer-admission";
        }
    }
}
