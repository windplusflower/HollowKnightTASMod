using System;
using System.Collections.Generic;
using System.Linq;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAnimatorState
    {
        internal sealed class DirectorQueues
        {
            public byte[] Frame = new byte[68], Fixed = new byte[68], Flags = new byte[7];
            public Stage[] Stages = Enumerable.Range(0,7).Select(_ => new Stage()).ToArray();
            public GraphHandle[] Notifications = Array.Empty<GraphHandle>(), Deferred = Array.Empty<GraphHandle>(), Commands = Array.Empty<GraphHandle>();
            public int[] Opcodes = Array.Empty<int>(), OutputCounts = Array.Empty<int>();
            public bool CommandStack;
        }
        internal sealed class Stage
        {
            public Job[] Parallel = Array.Empty<Job>(), Serial = Array.Empty<Job>();
            public CallbackGroup[] Groups = Array.Empty<CallbackGroup>();
        }
        internal sealed class Job { public int Graph = -1; public byte[] Time = Array.Empty<byte>(); }
        internal sealed class CallbackGroup
        {
            public int CallbackRva;
            public OutputRef[] Outputs = Array.Empty<OutputRef>();
        }
        internal sealed class OutputRef { public int Graph = -1, Index = -1, Animator = -1; }
        internal sealed class GraphHandle
        {
            // Versions are target-local generations. The native comparison
            // masks out bit 0; store its independent handle flag separately.
            public int State, Graph = -1;
            public bool Flag;
        }
        private sealed class QueueView
        {
            internal readonly DirectorQueues Data = new DirectorQueues();
            private readonly MemoryReader reader;
            private readonly IntPtr manager;
            private readonly Dictionary<IntPtr,int> graphs;
            private readonly Dictionary<IntPtr,int> ownedOutputs;
            private readonly Dictionary<IntPtr,OutputRef> outputs = new Dictionary<IntPtr,OutputRef>();
            private readonly Dictionary<int,IntPtr> graphPointers;
            private readonly Dictionary<(int,int),IntPtr> outputPointers = new Dictionary<(int,int),IntPtr>();
            private readonly IntPtr[,] jobs = new IntPtr[7,2];
            private readonly List<IntPtr>[] groups = Enumerable.Range(0,7).Select(_ => new List<IntPtr>()).ToArray();
            internal QueueView(IntPtr manager, IntPtr engine, int moduleSize, Dictionary<IntPtr,int> graphs, Dictionary<IntPtr,int> ownedOutputs, int rowCount, MemoryReader reader)
            {
                this.manager = manager; this.graphs = graphs; this.ownedOutputs = ownedOutputs; this.reader = reader;
                graphPointers = graphs.ToDictionary(pair => pair.Value,pair => pair.Key);
                Data.OutputCounts = Enumerable.Repeat(-1,rowCount).ToArray();
                foreach (var graph in graphs) OutputDirectory(graph.Key,graph.Value);
                var body = reader.Read(manager,1120);
                Data.Frame = Time(body,0); Data.Fixed = Time(body,72); Data.Flags = M.Slice(body,984,7);
                for (int stage = 0; stage < 7; stage++) {
                    var item = Data.Stages[stage];
                    item.Parallel = Jobs(body,stage,0); item.Serial = Jobs(body,stage,1);
                    int offset = 592 + stage * 24; var first = M.Ptr(body,offset);
                    long length = M.Ptr(body,offset + 8).ToInt64() - first.ToInt64();
                    long capacity = M.Ptr(body,offset + 16).ToInt64() - first.ToInt64();
                    if (length < 0 || capacity < length || capacity > MaximumEntries * 40L || capacity % 40 != 0 || length % 40 != 0 || capacity != 0 && first == IntPtr.Zero)
                        throw Error("director callback-group vector has invalid extent");
                    var bytes = reader.Read(first,(int)length);
                    item.Groups = Decode(bytes,40,(buffer,slot) => {
                        var callback = M.Ptr(buffer,slot);
                        long rva = callback.ToInt64() - engine.ToInt64();
                        var entries = Array(buffer,slot + 8,8,out var data);
                        groups[stage].Add(data);
                        return new CallbackGroup { CallbackRva = callback == IntPtr.Zero ? 0 : rva > 0 && rva < moduleSize ? (int)rva : -1,
                            Outputs = Decode(entries,8,(array,at) => {
                                var output = M.Ptr(array,at); return outputs.TryGetValue(output,out var known) ? known : new OutputRef();
                            }) };
                    });
                }
                Data.Notifications = Decode(Array(body,840,16,out _),16,Handle);
                Data.Deferred = Decode(Array(body,872,16,out _),16,Handle);
                var stack = M.Ptr(body,1080); Data.CommandStack = stack != IntPtr.Zero;
                if (Data.CommandStack) {
                    var commands = new List<GraphHandle>(); var opcodes = new List<int>(); var seen = new HashSet<IntPtr>();
                    // AtomicStack uses a 16-byte {top, ABA counter} compare-
                    // exchange. Observe both halves, leave mutation to the
                    // future admitted producer/allocator interlock.
                    var cursor = M.Ptr(reader.Read(stack,16),0);
                    while (cursor != IntPtr.Zero) {
                        if (!seen.Add(cursor) || seen.Count > MaximumEntries) throw Error("director command stack is cyclic/oversized");
                        var entry = reader.Read(cursor,32);
                        var opcode = BitConverter.ToInt64(entry,24);
                        if (opcode < int.MinValue || opcode > int.MaxValue) throw Error("director opcode is not a native enum value");
                        commands.Add(Handle(entry,8)); opcodes.Add((int)opcode); cursor = M.Ptr(entry,0);
                    }
                    Data.Commands = commands.ToArray(); Data.Opcodes = opcodes.ToArray();
                }
            }
            private void OutputDirectory(IntPtr graph,int key)
            {
                var head = IntPtr.Add(graph,104); var root = reader.Read(head,16); var prev = head; var cursor = M.Ptr(root,8);
                int count = 0;
                while (cursor != head) {
                    if (cursor.ToInt64() < 8 || outputs.Count >= MaximumEntries) throw Error("graph output list exceeds bounds");
                    // PlayableGraph::GetOutput converts the ListElement at
                    // output+8 back into the complete polymorphic output.
                    var output = IntPtr.Subtract(cursor,8); var bytes = reader.Read(output,48);
                    if (outputs.ContainsKey(output) || M.Ptr(bytes,8) != prev || M.Ptr(bytes,40) != graph) throw Error("graph output list/owning graph differs");
                    outputs.Add(output,new OutputRef { Graph = key, Index = count, Animator = ownedOutputs.TryGetValue(output,out var owner) ? owner : -1 });
                    outputPointers.Add((key,count),output); count++;
                    prev = cursor; cursor = M.Ptr(bytes,16);
                }
                if (M.Ptr(root,0) != prev) throw Error("graph output tail differs");
                Data.OutputCounts[key] = count;
            }
            private byte[] Array(byte[] owner,int offset,int stride,out IntPtr data)
            {
                ulong count = M.U64(owner,offset + 16), capacity = M.U64(owner,offset + 24) >> 1;
                data = M.Ptr(owner,offset);
                if (count > capacity || capacity > MaximumEntries || capacity != 0 && data == IntPtr.Zero) throw Error("director native queue exceeds bounds");
                return reader.Read(data,checked((int)count * stride));
            }
            private Job[] Jobs(byte[] body,int stage,int lane)
            {
                var bytes = Array(body,(lane == 0 ? 144 : 368) + stage * 32,80,out jobs[stage,lane]);
                return Decode(bytes,80,(buffer,index) => new Job { Graph = graphs.TryGetValue(M.Ptr(buffer,index+72),out var graph) ? graph : -1, Time = Time(buffer,index) });
            }
            private GraphHandle Handle(byte[] bytes,int index)
            {
                var node = M.Ptr(bytes,index); var version = M.U32(bytes,index + 8);
                var handle = new GraphHandle { Flag = (version & 1) != 0 };
                if (node == IntPtr.Zero) { handle.State = version == 0 ? 0 : 3; return handle; }
                var storage = reader.Read(IntPtr.Add(node,32),16);
                if (M.U32(storage,0) != (version & ~1U)) { handle.State = 2; return handle; }
                var graph = M.Ptr(storage,8);
                if (graph == IntPtr.Zero) { handle.State = 3; return handle; }
                if (M.Ptr(reader.Read(IntPtr.Add(graph,16),8),0) != node) throw Error("director queued graph handle has no reciprocal graph node");
                handle.State = 1; handle.Graph = graphs.TryGetValue(graph,out var key) ? key : -1; return handle;
            }
            private static byte[] Time(byte[] bytes,int offset) => M.Slice(bytes,offset,12).Concat(M.Slice(bytes,offset+16,56)).ToArray();
            internal void Prepare(DirectorQueues source,List<PendingWrite> writes)
            {
                Reject(BooleanRange(source.Flags,1,6,"DirectorManager",985).Concat(BooleanRange(Data.Flags,1,6,"DirectorManager",985)));
                void Bits(IntPtr at, byte[] value) => writes.Add(new PendingWrite(at,M.Read(at,value.Length),value));
                void Frame(IntPtr at, byte[] time) { Bits(at,M.Slice(time,0,12)); Bits(IntPtr.Add(at,16),M.Slice(time,12,56)); }
                if (!source.OutputCounts.SequenceEqual(Data.OutputCounts) || source.CommandStack != Data.CommandStack || Json(source.Commands) != Json(Data.Commands)
                    || !source.Opcodes.SequenceEqual(Data.Opcodes) || Json(source.Notifications) != Json(Data.Notifications) || Json(source.Deferred) != Json(Data.Deferred))
                    throw Error("director command/notification scheduling or graph output topology differs");
                if (source.Flags[2] != 0 || source.Flags[3] != 0 || Data.Flags[2] != 0 || Data.Flags[3] != 0) throw Error("cannot restore director while executing a stage");
                Frame(manager,source.Frame); Frame(IntPtr.Add(manager,72),source.Fixed); Bits(IntPtr.Add(manager,984),source.Flags);
                for (int stage = 0; stage < 7; stage++) {
                    var saved = source.Stages[stage]; var before = Data.Stages[stage];
                    for (int lane = 0; lane < 2; lane++) {
                        var list = lane == 0 ? saved.Parallel : saved.Serial;
                        if (list.Length != (lane == 0 ? before.Parallel : before.Serial).Length)
                            throw Error($"director native stage queue allocation differs [stage={stage}, lane={lane}, src={list.Length}, tgt={(lane == 0 ? before.Parallel : before.Serial).Length}, srcGraphs=({string.Join(",",list.Select(x=>x.Graph))}), tgtGraphs=({string.Join(",", (lane == 0 ? before.Parallel : before.Serial).Select(x=>x.Graph))})]");
                        for (int i = 0; i < list.Length; i++) {
                            var at = IntPtr.Add(jobs[stage,lane],i*80); Frame(at,list[i].Time);
                            if (!graphPointers.TryGetValue(list[i].Graph,out var graph)) throw Error("queued graph outside owned director graph directory");
                            Bits(IntPtr.Add(at,72),BitConverter.GetBytes(graph.ToInt64()));
                        }
                    }
                    if (saved.Groups.Length != before.Groups.Length)
                        throw Error($"director callback group directory differs [stage={stage}, src={saved.Groups.Length}, tgt={before.Groups.Length}]");
                    for (int i = 0; i < saved.Groups.Length; i++) {
                        var group = saved.Groups[i];
                        if (group.CallbackRva != before.Groups[i].CallbackRva || group.Outputs.Length != before.Groups[i].Outputs.Length)
                            throw Error("director stage callback or output queue allocation differs");
                        for (int j = 0; j < group.Outputs.Length; j++) {
                            var item = group.Outputs[j];
                            if (!outputPointers.TryGetValue((item.Graph,item.Index),out var output) || !ownedOutputs.TryGetValue(output,out var animator) || item.Animator != animator)
                                throw Error("director process output lacks a reciprocal Animator/graph binding");
                            Bits(IntPtr.Add(groups[stage][i],j*8),BitConverter.GetBytes(output.ToInt64()));
                        }
                    }
                }
            }
        }
        private static void ValidateQueues(Data data)
        {
            var q = data.Registry.Queues;
            if (q == null || q.Frame == null || q.Frame.Length != 68 || q.Fixed == null || q.Fixed.Length != 68 || q.Flags == null || q.Flags.Length != 7
                || q.Stages == null || q.Stages.Length != 7 || q.Notifications == null || q.Deferred == null || q.Commands == null
                || q.Opcodes == null || q.Opcodes.Length != q.Commands.Length || q.Commands.Length > MaximumEntries || !q.CommandStack && q.Commands.Length != 0 || q.OutputCounts == null)
                throw Error("director timing/queue schema differs");
            if (q.OutputCounts.Length != (data.Registry.Present ? data.Rows.Length : 0)) throw Error("director graph output directory extent differs");
            for (int i = 0; i < q.OutputCounts.Length; i++)
                if (q.OutputCounts[i] < -1 || q.OutputCounts[i] > MaximumEntries || (q.OutputCounts[i] >= 0) != (data.Rows[i].Graph == i))
                    throw Error("director graph output directory identity differs");
            bool Valid(int owner) => owner == -1 || owner >= 0 && owner < q.OutputCounts.Length && q.OutputCounts[owner] >= 0;
            foreach (var list in new[] {q.Notifications,q.Deferred,q.Commands}) {
                if (list.Length > MaximumEntries || list.Any(h => h == null || h.State < 0 || h.State > 3 || !Valid(h.Graph) || h.State != 1 && h.Graph != -1 || h.State == 0 && h.Flag))
                    throw Error("director queued graph handle shape differs");
            }
            foreach (var stage in q.Stages) {
                if (stage == null || stage.Groups == null || stage.Groups.Length > MaximumEntries) throw Error("director stage directory differs");
                foreach (var list in new[] {stage.Parallel,stage.Serial})
                    if (list == null || list.Length > MaximumEntries || list.Any(job => job == null || !Valid(job.Graph) || job.Time == null || job.Time.Length != 68)) throw Error("director stage job differs");
                foreach (var group in stage.Groups)
                    if (group == null || group.CallbackRva < -1 || group.Outputs == null || group.Outputs.Length > MaximumEntries
                        || group.Outputs.Any(o => o == null || !Valid(o.Graph) || o.Animator < -1 || o.Animator >= data.Rows.Length
                            || o.Animator >= 0 && (data.Rows[o.Animator].Output != o.Animator || data.Rows[o.Animator].Graph != o.Graph)
                            || o.Graph == -1 && (o.Index != -1 || o.Animator != -1) || o.Graph >= 0 && (o.Index < 0 || o.Index >= q.OutputCounts[o.Graph])))
                        throw Error("director callback/output reference differs");
            }
            if (!data.Registry.Present && (q.CommandStack || q.Stages.Any(s => s.Groups.Length + s.Serial.Length + s.Parallel.Length != 0)
                || q.Notifications.Length + q.Deferred.Length != 0 || q.Frame.Concat(q.Fixed).Concat(q.Flags).Any(b => b != 0))) throw Error("absent director retained queue/timing state");
        }
    }
}
