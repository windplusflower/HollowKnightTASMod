using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAnimatorState
    {
        internal sealed class Data
        {
            public int Version = 1;
            public Row[] Rows = Array.Empty<Row>();
            public Playable[] Playables = Array.Empty<Playable>();
            public ControllerAsset[] Assets = Array.Empty<ControllerAsset>();
            public AvatarNode[] AvatarNodes = Array.Empty<AvatarNode>();
            public Director Registry = new Director();
            public UserSlot[] Users = Array.Empty<UserSlot>();
            public ControllerMemory[] ExtraMemories = Array.Empty<ControllerMemory>();
            public Topology[] Graphs=Array.Empty<Topology>();
        }
        internal sealed class Row
        {
            public string Owner = "", Controller = "", Avatar = "", Root = "", BinderClip = "";
            public string[] CachedClips = Array.Empty<string>(), Renderers = Array.Empty<string>(), Clearing = Array.Empty<string>();
            public byte[][] Fields = Array.Empty<byte[]>();
            public byte[] Padding = Array.Empty<byte>();
            public int Primary = -1, Graph = -1;
            public int Output = -1;
            public int[] Cached = Array.Empty<int>();
            public bool StaleGraph, StaleOutput;
            public bool GraphFlag, OutputFlag;
            public Dataset[] Datasets = {new Dataset(),new Dataset()};
            public EvaluationInput[] Inputs = {new EvaluationInput(),new EvaluationInput()};
            public Handles Binder = new Handles();
            public Evaluation[] Evaluation = {new Evaluation(),new Evaluation()};
            public Residual? Retained;
        }
        internal sealed class Playable
        {
            public string Controller = "";
            public byte[] Clock = Array.Empty<byte>();
            public ControllerMemory? Memory;
            public ControllerRuntime Runtime = new ControllerRuntime();
            public int SharedAvatarValues = -1;
        }
        internal sealed class Director
        {
            public bool Present;
            // An owned graph is identified by the first sorted Animator row
            // referring to it. External graph slots remain explicit hard gaps.
            public int[] Playing = Array.Empty<int>(), Stopped = Array.Empty<int>();
            public DirectorQueues Queues = new DirectorQueues();
            public Producers? Producers;
        }
        internal sealed class ControllerAsset
        {
            public string Binding = "", Kind = "", Base = "";
            public string[] Clips = Array.Empty<string>(), OriginalClips = Array.Empty<string>(), OverridePairs = Array.Empty<string>();
        }
        private const string Unbound = "#unresolved-native-object";
        private const int AnimatorVtable = 0x15d6520, PlayableVtable = 0x15d8f08;
        // Offsets from the pinned PDB. Preserve scalar bits (including signed
        // zero and NaN payloads), without pointers, padding or process handles.
        private static readonly int[] Offsets = { 56, 168, 392, 396, 1000, 1192, 1440, 1472, 1528 };
        private static readonly int[] Sizes = { 2, 1, 2, 12, 157, 4, 16, 14, 4 };
        private static readonly int[] PaddingSlots = {394,395,1157,1158,1159,1486,1487};
        private sealed class Live
        {
            internal readonly Data Data = new Data();
            internal readonly List<IntPtr> Playables = new List<IntPtr>();
            internal readonly Dictionary<IntPtr, int> Graphs = new Dictionary<IntPtr, int>();
            internal readonly Dictionary<IntPtr, int> Outputs = new Dictionary<IntPtr, int>();
            internal readonly List<EvalView[]> Evaluation = new List<EvalView[]>();
            internal IntPtr Manager;
            internal AvatarGraph? Avatars;
            internal QueueView? Queues;
            internal UsersView? Users;
            internal MemoryDirectory? Memories;
            internal readonly List<ResidualView> Retained = new List<ResidualView>();
            internal readonly List<GraphView> Topologies=new List<GraphView>();
            internal Action RequireFrozen = () => { };
            internal Func<int,string> FloatObject = _ => throw Error("no native object identity table");
        }
        private sealed class Reader
        {
            internal readonly IntPtr Engine;
            private readonly int moduleSize;
            internal readonly MemoryReader Memory = new MemoryReader();
            internal readonly Live Live = new Live();
            private readonly Dictionary<int, IntPtr> objects;
            private HashSet<IntPtr>? registeredOwners;
            private Dictionary<int,int>? floatIds;
            private readonly Func<object, string?> bind;
            private readonly Dictionary<IntPtr, int> indices = new Dictionary<IntPtr, int>();
            private readonly List<Playable> playables = new List<Playable>();
            private readonly List<IntPtr> controllerValues = new List<IntPtr>();
            private readonly Dictionary<IntPtr, ControllerAsset> controllers = new Dictionary<IntPtr, ControllerAsset>();
            private readonly ResidentObjectDirectory directory;
            private Dictionary<(IntPtr,int),string>? transforms;
            private Dictionary<IntPtr,string>? hierarchies;
            internal Reader(ResidentObjectDirectory directory, Func<object, string?> bind)
            {
                this.bind = bind; this.directory = directory; Live.Avatars = new AvatarGraph(Memory,Key<Transform>,Id<GameObject>); Live.FloatObject = FloatObjectId;
                var engine = LoadingEngineClockProbe.RequirePinnedEngine(); Engine = engine.BaseAddress; moduleSize = engine.ModuleMemorySize;
                // Reading IDToPointer and borrowing the existing GCHandle does
                // not load assets or manufacture a managed wrapper in Capture.
                var map = M.Ptr(Memory.Read(IntPtr.Add(Engine, 0x19d7be8), 8), 0);
                var head = Memory.Read(map, 32);
                var nodes = Memory.Read(M.Ptr(head, 0), ResidentNativeGlobalObjects.HashByteLength(head, true));
                objects = ResidentNativeGlobalObjects.DecodeHashRows(head, nodes, true).ToDictionary(row => row.InstanceId, row => row.Pointer);
            }
            internal string TransformAccess(IntPtr hierarchy, int index)
            {
                if (transforms == null) {
                    transforms = new Dictionary<(IntPtr,int),string>(); hierarchies = new Dictionary<IntPtr,string>();
                    foreach (var pair in directory.NativeBindings.Where(pair => pair.Value is Transform).OrderBy(pair => pair.Key,StringComparer.Ordinal)) {
                        var access = Memory.Read(IntPtr.Add(M.Owner(pair.Value),56),12); var root = M.Ptr(access,0);
                        if (root != IntPtr.Zero) {
                            transforms.Add((root,M.I32(access,8)),pair.Key);
                            if (!hierarchies.ContainsKey(root)) hierarchies.Add(root,pair.Key);
                        }
                    }
                }
                return transforms.TryGetValue((hierarchy,index),out var key) ? key : Unbound;
            }
            internal string Hierarchy(IntPtr pointer)
            {
                if (pointer == IntPtr.Zero) return "";
                if (hierarchies == null) _ = TransformAccess(pointer,-1);
                return hierarchies!.TryGetValue(pointer,out var key) ? key : Unbound;
            }
            internal byte[] BankSnapshot(IntPtr constant,int stride) => Memory.Bank(constant,Memory.Read(constant,16),0,stride);
            internal NativeObject DirectoryOwner(string key) => directory.Resolve(key);
            internal bool Registered(IntPtr pointer)
            {
                if (registeredOwners == null) registeredOwners = new HashSet<IntPtr>(objects.Values);
                return registeredOwners.Contains(pointer);
            }
            internal bool InEngine(IntPtr address,int bytes)
            {
                long rva = address.ToInt64()-Engine.ToInt64(); return rva >= 0 && rva <= moduleSize - bytes;
            }
            internal int Rva(IntPtr address) => address == IntPtr.Zero ? 0 : InEngine(address,1) ? checked((int)(address.ToInt64()-Engine.ToInt64())) : -1;
            internal string Id<T>(int id) where T : NativeObject
                => id == 0 ? "" : objects.TryGetValue(id, out var pointer) ? Key<T>(pointer) : Unbound;
            internal string FloatObjectId(int bits)
            {
                if (bits == 0) return "";
                if (floatIds == null) {
                    // ReadPropertySceneHandles converts instance ID to
                    // float32. A rounded collision cannot identify an asset.
                    floatIds = IndexFloatObjectIds(objects.Keys);
                }
                return floatIds.TryGetValue(bits,out var unique) && unique != 0 ? Id<NativeObject>(unique) : Unbound;
            }
            internal string ControllerId(int id)
                => id == 0 ? "" : objects.TryGetValue(id, out var pointer) ? Controller(pointer) : Unbound;
            internal string Controller(IntPtr pointer)
            {
                if (pointer == IntPtr.Zero) return "";
                if (controllers.TryGetValue(pointer, out var previous)) return previous.Binding;
                var key = Key<RuntimeAnimatorController>(pointer);
                if (key == Unbound) return key;
                if (controllers.Count == MaximumEntries) throw Error("native controller asset count exceeded");
                var row = new ControllerAsset { Binding = key };
                controllers.Add(pointer, row);
                var vtable = M.Ptr(Memory.Read(pointer, 8), 0);
                string[] Clips(byte[] bytes, int offset) => Decode(ArrayBytes(bytes, offset, 4), 4, (array, slot) => Id<AnimationClip>(M.I32(array, slot)));
                if (vtable == IntPtr.Add(Engine, 0x171e6d0)) {
                    row.Kind = "controller"; row.Clips = Clips(Memory.Read(pointer, 416), 168);
                } else if (vtable == IntPtr.Add(Engine, 0x15d6778)) {
                    var bytes = Memory.Read(pointer, 320); row.Kind = "override";
                    row.Clips = Clips(bytes, 288); row.OriginalClips = Clips(bytes, 256);
                    row.OverridePairs = Decode(ArrayBytes(bytes, 176, 8), 4, (array, slot) => Id<AnimationClip>(M.I32(array, slot)));
                    // Capture follows override bases iteratively in Finish.
                    row.Base = Id<RuntimeAnimatorController>(M.I32(bytes, 168));
                } else row.Kind = "unknown";
                return key;
            }
            internal string Key<T>(IntPtr pointer) where T : NativeObject
            {
                if (pointer == IntPtr.Zero) return "";
                var id = M.I32(M.Read(IntPtr.Add(pointer, 8), 4), 0);
                if (!objects.TryGetValue(id, out var known) || known != pointer) throw Error("reference is outside the pinned native ID table");
                // Native registration/provenance remains authoritative when Unity
                // has never issued a managed wrapper. Recheck the required family
                // on every edge; a prior Object edge cannot authorize a typed one.
                return ResidentNativeAssetReference.BindPointer(pointer,typeof(T),bind);
            }
            internal byte[] ArrayBytes(byte[] owner, int field, int stride)
            {
                var count = M.U64(owner, field + 16); var cap = M.U64(owner, field + 24) >> 1;
                var pointer = M.Ptr(owner, field);
                if (count > cap || cap > MaximumEntries || cap != 0 && pointer == IntPtr.Zero) throw Error("native Animator array shape differs");
                return Memory.Read(pointer, checked((int)count * stride));
            }
            internal int ControllerPlayable(IntPtr pointer)
            {
                if (pointer == IntPtr.Zero) return -1;
                if (indices.TryGetValue(pointer, out var index)) return index;
                if (indices.Count == MaximumEntries) throw Error("controller playable count exceeded");
                var bytes = Memory.Read(pointer, 456);
                if (M.Ptr(bytes, 0) != IntPtr.Add(Engine, PlayableVtable)) throw Error("controller playable vtable differs");
                var memory = M.Ptr(bytes, 312);
                var value = new Playable { Controller = Controller(M.Ptr(bytes, 280)), Clock = M.Slice(bytes, 80, 58),
                    Memory = memory == IntPtr.Zero ? null : ReadMemory(memory, Memory), Runtime = ReadRuntime(pointer, bytes, Memory) };
                index = playables.Count; indices.Add(pointer, index); Live.Playables.Add(pointer); playables.Add(value);
                controllerValues.Add(memory == IntPtr.Zero ? IntPtr.Zero : Relative(memory,Memory.Read(memory,48),32));
                return index;
            }
            internal void Finish()
            {
                Live.Users = new UsersView(this); Live.Data.Users = Live.Users.Rows;
                var jobs = new Dictionary<IntPtr,int>(); Live.Memories = new MemoryDirectory(this);
                foreach (var graph in Live.Graphs.OrderBy(pair=>pair.Value)) Live.Topologies.Add(new GraphView(graph.Value,graph.Key,Memory,Rva,Live.Memories.Playables));
                Live.Data.Graphs=Live.Topologies.Select(t=>t.Data).ToArray();
                foreach (var row in Live.Data.Rows) {
                    var view = new ResidualView(this,row,Live.Memories,jobs); row.Retained = view.Data; Live.Retained.Add(view);
                }
                Live.Data.ExtraMemories = Live.Memories.Additional.ToArray();
                Live.Data.Playables = playables.ToArray();
                Live.Data.AvatarNodes = Live.Avatars!.Nodes.ToArray();
                for (int i = 0; i < playables.Count; i++) playables[i].SharedAvatarValues = Live.Avatars.ValueIndex(controllerValues[i]);
                // Walk override base references iteratively. Cycles/aliases
                // are identified by object address within this capture only.
                var pending = new Queue<IntPtr>(controllers.Keys);
                var visited = new HashSet<IntPtr>();
                while (pending.Count != 0) {
                    var pointer = pending.Dequeue(); if (!visited.Add(pointer) || controllers[pointer].Kind != "override") continue;
                    var id = M.I32(Memory.Read(IntPtr.Add(pointer, 168), 4), 0);
                    if (objects.TryGetValue(id, out var parent) && Controller(parent) != Unbound) pending.Enqueue(parent);
                }
                Live.Data.Assets = controllers.Values.OrderBy(value => value.Binding, StringComparer.Ordinal).ToArray();
                Live.Manager = M.Ptr(Memory.Read(IntPtr.Add(Engine, 0x196f0c0), 8), 0);
                var registry = Live.Data.Registry;
                registry.Present = Live.Manager != IntPtr.Zero;
                var registered = new HashSet<IntPtr>();
                if (registry.Present) {
                    registry.Playing = List(IntPtr.Add(Live.Manager, 808), registered);
                    registry.Stopped = List(IntPtr.Add(Live.Manager, 824), registered);
                    Live.Queues = new QueueView(Live.Manager,Engine,moduleSize,Live.Graphs,Live.Outputs,Live.Data.Rows.Length,Memory);
                    registry.Queues = Live.Queues.Data;
                    registry.Producers=CaptureProducers(Live.Manager,Memory);
                }
                Memory.Verify(); Live.RequireFrozen = Memory.Verify;
            }
            private int[] List(IntPtr head, HashSet<IntPtr> seen)
            {
                var root = Memory.Read(head, 16); var last = head;
                var next = M.Ptr(root, 8); var owners = new List<int>();
                while (next != head) {
                    if (seen.Count == MaximumEntries || !seen.Add(next)) throw Error("director graph registration is repeated/oversized");
                    var node = Memory.Read(next, 16);
                    if (M.Ptr(node, 0) != last) throw Error("director graph backward registration differs");
                    owners.Add(Live.Graphs.TryGetValue(next, out var owner) ? owner : -1);
                    last = next; next = M.Ptr(node, 8);
                }
                if (M.Ptr(root, 0) != last) throw Error("director graph tail differs");
                return owners.ToArray();
            }
        }
        internal static Data Capture(ResidentObjectDirectory directory, Func<object,string?> bind) => Observe(directory, bind).Data;
        private static Live Observe(ResidentObjectDirectory directory, Func<object,string?> bind)
        {
            if (directory == null || bind == null) throw new ArgumentNullException();
            var owners = directory.ManagedRoots.Where(pair => pair.Value.GetType() == typeof(Animator)).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
            if (owners.Length == 0) return new Live();
            var reader = new Reader(directory,bind); var rows = new Row[owners.Length];
            for (var index = 0; index < rows.Length; index++) {
                var owner = (Animator)owners[index].Value;
                var bytes = reader.Memory.Read(M.Owner(owner), 1696);
                if (M.Ptr(bytes, 0) != IntPtr.Add(reader.Engine, AnimatorVtable)) throw Error("actual Animator vtable differs");
                var row = new Row { Owner = owners[index].Key, Controller = reader.ControllerId(M.I32(bytes, 412)),
                    Avatar = reader.Id<Avatar>(M.I32(bytes, 408)), Root = reader.Id<Transform>(M.I32(bytes, 1568)),
                    BinderClip = reader.Key<AnimationClip>(M.Ptr(bytes, 160)), Primary = reader.ControllerPlayable(M.Ptr(bytes, 1520)),
                    Fields = Offsets.Select((offset, field) => M.Slice(bytes, offset, Sizes[field])).ToArray(),
                    Padding = PaddingSlots.Select(offset => bytes[offset]).ToArray() };
                row.CachedClips = Decode(reader.ArrayBytes(bytes, 1488, 8), 8, (array, offset) => reader.Key<AnimationClip>(M.Ptr(array, offset)));
                row.Renderers = Decode(reader.ArrayBytes(bytes, 1200, 4), 4, (array, offset) => reader.Id<Renderer>(M.I32(array, offset)));
                row.Clearing = Decode(reader.ArrayBytes(bytes, 1232, 4), 4, (array, offset) => reader.Id<Renderer>(M.I32(array, offset)));
                row.Cached = Decode(reader.ArrayBytes(bytes, 1160, 8), 8, (array, offset) => reader.ControllerPlayable(M.Ptr(array, offset)));
                var avatars = reader.Live.Avatars!;
                row.Binder = CaptureHandles(reader,bytes);
                row.Datasets = new[] {avatars.Set(bytes,440), avatars.Set(bytes,584)};
                row.Inputs = new[] {avatars.Input(bytes,1576,reader.TransformAccess), avatars.Input(bytes,896,reader.TransformAccess)};
                var views = new[] {new EvalView(M.Owner(owner),bytes,176,440,520,reader),new EvalView(M.Owner(owner),bytes,712,584,664,reader)};
                row.Evaluation = views.Select(view => view.Data).ToArray(); reader.Live.Evaluation.Add(views);
                var handle = M.Ptr(bytes, 1536);
                row.GraphFlag = (M.U32(bytes,1544) & 1) != 0; row.StaleGraph = handle == IntPtr.Zero && M.U32(bytes,1544) != 0;
                if (handle != IntPtr.Zero) {
                    var node = reader.Memory.Read(handle, 48);
                    row.StaleGraph = M.U32(node, 32) != (M.U32(bytes, 1544) & ~1U);
                    if (!row.StaleGraph) {
                        var graph = M.Ptr(node, 40);
                        if (graph == IntPtr.Zero || M.Ptr(reader.Memory.Read(IntPtr.Add(graph, 16), 8), 0) != handle) throw Error("graph handle and graph node disagree");
                        if (!reader.Live.Graphs.TryGetValue(graph, out row.Graph)) reader.Live.Graphs.Add(graph, row.Graph = index);
                    }
                }
                var outputNode = M.Ptr(bytes,1552);
                row.OutputFlag = (M.U32(bytes,1560) & 1) != 0; row.StaleOutput = outputNode == IntPtr.Zero && M.U32(bytes,1560) != 0;
                if (outputNode != IntPtr.Zero) {
                    var node = reader.Memory.Read(outputNode,48); var output = M.Ptr(node,40);
                    row.StaleOutput = M.U32(node,32) != (M.U32(bytes,1560) & ~1U) || output == IntPtr.Zero;
                    if (!row.StaleOutput) {
                        var data = reader.Memory.Read(output,216);
                        if (M.Ptr(data,0) != IntPtr.Add(reader.Engine,0x15d8cd8) || M.Ptr(data,32) != outputNode)
                            throw Error("Animator internal output has another native subtype or handle");
                        if (M.Ptr(data,208) == M.Owner(owner) && reader.Live.Graphs.TryGetValue(M.Ptr(data,40),out var graph) && graph == row.Graph) {
                            if (reader.Live.Outputs.ContainsKey(output)) throw Error("Animator output is claimed by multiple owners");
                            reader.Live.Outputs.Add(output,row.Output = index);
                        } else row.StaleOutput = true;
                    }
                }
                rows[index] = row;
            }
            reader.Live.Data.Rows = rows; reader.Finish(); ValidateOwners(reader.Live.Data, directory.CopySnapshot());
            MarkAnimatorScalars(reader,ScalarGaps(reader.Live.Data));
            return reader.Live;
        }
        private static T[] Decode<T>(byte[] array, int stride, Func<byte[],int,T> item)
        {
            var result = new T[array.Length / stride];
            for (var index = 0; index < result.Length; index++) result[index] = item(array, index * stride);
            return result;
        }
        private static InvalidDataException Error(string reason) => new InvalidDataException("Resident Animator: " + reason);
    }
}
