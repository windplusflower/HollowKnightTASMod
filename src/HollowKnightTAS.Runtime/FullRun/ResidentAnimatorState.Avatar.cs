using System;
using System.Collections.Generic;
using System.Linq;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAnimatorState
    {
        internal enum AvatarKind { Constant, Input, Output, Memory, Workspace, Binding, TrsPose, AffinePose, Values, Mask, Motion, HumanPose, HumanMask, Skeleton, Human, Hand, NodeState, Weight, SkeletonMask, ClipMemory, ClipOutput, MotionReference }
        private static readonly AvatarKind[] DatasetEdges = {AvatarKind.Constant, AvatarKind.Input, AvatarKind.Output, AvatarKind.Memory, AvatarKind.Workspace, AvatarKind.Binding};
        private static readonly AvatarKind[] OutputEdges = {AvatarKind.Values, AvatarKind.Mask, AvatarKind.TrsPose, AvatarKind.AffinePose,
            AvatarKind.Motion, AvatarKind.HumanPose, AvatarKind.HumanPose, AvatarKind.HumanPose};
        private static readonly AvatarKind[] WorkspaceEdges = {AvatarKind.TrsPose, AvatarKind.TrsPose, AvatarKind.TrsPose};
        private static readonly AvatarKind[] InputEdges = {AvatarKind.Mask, AvatarKind.Input, AvatarKind.HumanMask, AvatarKind.Values, AvatarKind.Values};
        private static readonly AvatarKind[] ConstantEdges = {AvatarKind.Skeleton, AvatarKind.TrsPose, AvatarKind.TrsPose, AvatarKind.Human, AvatarKind.Skeleton, AvatarKind.TrsPose};
        private static readonly AvatarKind[] HumanEdges = {AvatarKind.Skeleton, AvatarKind.TrsPose, AvatarKind.Hand, AvatarKind.Hand};
        private static readonly AvatarKind[] BindingEdges = {AvatarKind.TrsPose};
        private static readonly AvatarKind[] NodeStateEdges = {AvatarKind.Values, AvatarKind.Mask, AvatarKind.Motion, AvatarKind.HumanPose, AvatarKind.HumanPose};
        internal sealed class Dataset
        {
            public int[] Edges = {-1,-1,-1,-1,-1,-1};
            public int MemorySize;
            public byte OwnsAvatar, Initialized;
        }
        internal sealed class EvaluationInput
        {
            public bool Dormant;
            public int[] Edges = {-1,-1,-1,-1,-1};
            public byte[] Flags = new byte[4], Versions = new byte[8];
            public string Root = "";
            public int NullRootIndex;
        }
        internal sealed class AvatarNode
        {
            public AvatarKind Kind;
            public int[] Edges = Array.Empty<int>(), Extents = Array.Empty<int>();
            public byte[][] Fields = Array.Empty<byte[]>();
            public string[] References = Array.Empty<string>();
            public bool ReadOnly;
            public ValueIntegerChannels? Integers;
            public bool? MotionReferenceValid;
        }
        private sealed partial class AvatarGraph
        {
            internal readonly List<AvatarNode> Nodes = new List<AvatarNode>();
            internal readonly List<IntPtr> Pointers = new List<IntPtr>();
            internal readonly List<List<IntPtr>> Cells = new List<List<IntPtr>>();
            private readonly Dictionary<IntPtr, int> seen = new Dictionary<IntPtr, int>();
            private readonly MemoryReader reader;
            private readonly Func<IntPtr,string> transform;
            private readonly Func<int,string> rootGameObject;
            private readonly List<List<IntPtr>> refSlots = new List<List<IntPtr>>();
            internal AvatarGraph(MemoryReader reader, Func<IntPtr,string> transform, Func<int,string> rootGameObject)
            { this.reader = reader; this.transform = transform; this.rootGameObject = rootGameObject; }
            internal int ValueIndex(IntPtr pointer)
            {
                if (!seen.TryGetValue(pointer,out var index)) return -1;
                if (Nodes[index].Kind != AvatarKind.Values) throw Error("controller bank has incompatible avatar allocation type");
                return index;
            }
            internal int Retained(IntPtr pointer,AvatarKind kind) => Node(pointer,kind);
            internal int MotionReference(IntPtr pointer, bool valid) => Node(pointer, AvatarKind.MotionReference, false, -1, valid);
            internal int ClipOutput(IntPtr pointer, int count) => Node(pointer,AvatarKind.ClipOutput,false,count);
            internal Dataset Set(byte[] owner, int offset)
            {
                var size = M.U64(owner, offset + 48);
                if (size > MaximumBytes) throw Error("avatar dataset allocator size/flags differ");
                var result = new Dataset { MemorySize = (int)size, OwnsAvatar = owner[offset + 56], Initialized = owner[offset + 57] };
                for (int i = 0; i < DatasetEdges.Length; i++) result.Edges[i] = Node(M.Ptr(owner, offset + i * 8), DatasetEdges[i], i == 0);
                return result;
            }
            internal EvaluationInput Input(byte[] owner, int offset, Func<IntPtr,int,string> transformAccess, bool dormant)
            {
                if (dormant) return new EvaluationInput { Dormant = true };
                var hierarchy = M.Ptr(owner, offset + 64); var index = M.I32(owner, offset + 72);
                var value = new EvaluationInput { Flags = new[] {owner[offset], owner[offset + 24], owner[offset + 40], owner[offset + 41]},
                    Versions = M.Slice(owner, offset + 80, 8), Root = hierarchy == IntPtr.Zero ? "" : transformAccess(hierarchy, index),
                    NullRootIndex = hierarchy == IntPtr.Zero ? index : 0 };
                var links = new[] {8,16,32,48,56};
                for (int i = 0; i < links.Length; i++) value.Edges[i] = Node(M.Ptr(owner, offset + links[i]), InputEdges[i]);
                return value;
            }
            private int Node(IntPtr pointer, AvatarKind kind, bool readOnly = false, int outputCount = -1, bool? motionReferenceValid = null)
            {
                if (pointer == IntPtr.Zero) return -1;
                var referenceValid = kind == AvatarKind.MotionReference ? motionReferenceValid ?? true : (bool?)null;
                if (seen.TryGetValue(pointer, out var prior)) {
                    if (Nodes[prior].Kind != kind) throw Error("shared avatar allocation has incompatible native types");
                    if (Nodes[prior].MotionReferenceValid != referenceValid) throw Error("shared MotionXReference has conflicting lifetimes");
                    if (kind == AvatarKind.ClipOutput && Nodes[prior].Extents[0] != outputCount) throw Error("shared clip output extent differs");
                    Nodes[prior].ReadOnly |= readOnly; return prior;
                }
                if (Nodes.Count == MaximumEntries) throw Error("avatar allocation graph exceeds maximum");
                var index = Nodes.Count; var node = new AvatarNode { Kind = kind, ReadOnly = readOnly, MotionReferenceValid = referenceValid };
                var cells = new List<IntPtr>(); var fields = new List<byte[]>(); var edges = new List<int>(); var extents = new List<int>();
                var refs = new List<string>(); var slots = new List<IntPtr>();
                seen.Add(pointer, index); Nodes.Add(node); Pointers.Add(pointer); Cells.Add(cells); refSlots.Add(slots);
                void Field(IntPtr at, byte[] data) { cells.Add(at); fields.Add(data); }
                void Span(int offset, int length) { var at = IntPtr.Add(pointer, offset); Field(at, reader.Read(at, length)); }
                void Banks(int[] strides) {
                    var header = reader.Read(pointer, strides.Length * 16);
                    for (int i = 0; i < strides.Length; i++) {
                        var count = Count(M.U32(header, i * 16)); extents.Add(count);
                        var at = Relative(pointer, header, i * 16 + 8);
                        if (count != 0 && at == IntPtr.Zero) throw Error("missing storage for " + kind + " bank " + i + " with count=" + count);
                        // Dynamic animation integer channels can carry native
                        // object IDs. Their remap table needs its own codec.
                        if (kind == AvatarKind.Values && i == 4) { integerCells.Add(index,at); integerBytes.Add(index,reader.Read(at,checked(count*4))); continue; }
                        Field(at, reader.Read(at, checked(count * strides[i])));
                    }
                }
                void Edges(AvatarKind[] types) {
                    var links = reader.Read(pointer, types.Length * 8);
                    for (int i = 0; i < types.Length; i++) edges.Add(Node(M.Ptr(links, i * 8), types[i]));
                }
                void RelativeEdges(byte[] header, AvatarKind[] types, params int[] offsets) {
                    for (int i = 0; i < types.Length; i++) edges.Add(Node(Relative(pointer,header,offsets[i]),types[i],true));
                }
                void RelativeBank(byte[] header, int countOffset, int valueOffset, int stride) {
                    var count = Count(M.U32(header, countOffset)); extents.Add(count);
                    var at = Relative(pointer,header,valueOffset); Field(at, DirectArray(reader,at,count,stride));
                }
                switch (kind) {
                    case AvatarKind.Constant: {
                        var header = reader.Read(pointer,176); RelativeEdges(header,ConstantEdges,0,8,16,40,144,152);
                        RelativeBank(header,24,32,4); RelativeBank(header,48,56,4); RelativeBank(header,64,72,4);
                        RelativeBank(header,160,168,4); Span(80,4); Span(96,48); break;
                    }
                    case AvatarKind.Human: {
                        var header = reader.Read(pointer,320); RelativeEdges(header,HumanEdges,48,56,64,72);
                        Span(0,48); Span(80,235); break;
                    }
                    case AvatarKind.Hand: Span(0,60); break;
                    case AvatarKind.Skeleton: {
                        var header = reader.Read(pointer,40);
                        RelativeBank(header,0,8,8); RelativeBank(header,0,16,4);
                        var count = Count(M.U32(header,24)); extents.Add(count);
                        var axes = Relative(pointer,header,32); var bytes = DirectArray(reader,axes,count,96);
                        // math::Axes has eight trailing alignment bytes per
                        // element. Only its 88 bytes of fields are portable.
                        var packed = new byte[checked(count * 88)];
                        for (int i = 0; i < count; i++) Buffer.BlockCopy(bytes, i * 96, packed, i * 88, 88);
                        Field(axes,packed); break;
                    }
                    case AvatarKind.Binding: {
                        var header = reader.Read(pointer,48);
                        int skeleton = Size(M.U64(header,0)), exposed = Size(M.U64(header,24)); extents.Add(skeleton); extents.Add(exposed);
                        var skeletonBase = M.Ptr(header,8); var exposedBase = M.Ptr(header,32);
                        var transforms = DirectArray(reader,skeletonBase,skeleton,8);
                        for (int i = 0; i < skeleton; i++) { slots.Add(IntPtr.Add(skeletonBase,i * 8)); refs.Add(transform(M.Ptr(transforms,i * 8))); }
                        // Ordinary binding writer c2107/c2110/c2118 stores the
                        // root Transform's GameObject instance ID here. The PDB
                        // name animatorInstanceId does not describe its real family.
                        // Unknown nonzero IDs still require registered GameObject provenance.
                        // The ordinary constructor stores AvatarRoot.gameObject's ID.
                        // The optimized constructor has zero skeleton bindings and
                        // leaves this word unwritten. A nonzero word in that shape
                        // cannot be inferred to be an ID from the current animator.
                        slots.Add(IntPtr.Add(pointer,16));
                        refs.Add(skeleton == 0 ? "" : rootGameObject(M.I32(header,16)));
                        var rows = DirectArray(reader,exposedBase,exposed,16);
                        for (int i = 0; i < exposed; i++) {
                            slots.Add(IntPtr.Add(exposedBase,i * 16)); refs.Add(transform(M.Ptr(rows,i * 16)));
                            Field(IntPtr.Add(exposedBase,i * 16 + 8), M.Slice(rows,i * 16 + 8,8));
                        }
                        edges.Add(Node(M.Ptr(header,40),AvatarKind.TrsPose,true)); break;
                    }
                    case AvatarKind.Input: Span(0,21); break;
                    case AvatarKind.Output: Edges(OutputEdges); break;
                    case AvatarKind.Memory: Span(0,166); break;
                    case AvatarKind.Workspace: Edges(WorkspaceEdges); Span(32,51); break;
                    case AvatarKind.TrsPose: Banks(new[] {48}); break;
                    case AvatarKind.AffinePose: Banks(new[] {64}); break;
                    case AvatarKind.Values: Banks(ParameterStrides); break;
                    case AvatarKind.Mask: Banks(new[] {1,1,1,1,1}); break;
                    case AvatarKind.Weight: Banks(new[] {4,4,4,4,4}); break;
                    case AvatarKind.SkeletonMask: Banks(new[] {8}); break;
                    case AvatarKind.ClipMemory: {
                        var header = reader.Read(pointer,28);
                        var count = Count(M.U32(header,8)); extents.Add(count);
                        extents.Add(Count(M.U32(header,24)));
                        Span(12,8);
                        var at = M.Ptr(header,0); Field(at,DirectArray(reader,at,count,20)); break;
                    }
                    case AvatarKind.ClipOutput: {
                        if (outputCount < 0 || outputCount > MaximumEntries) throw Error("clip output lacks curve extent");
                        extents.Add(outputCount);
                        var at = M.Ptr(reader.Read(pointer,8),0); Field(at,DirectArray(reader,at,outputCount,4)); break;
                    }
                    case AvatarKind.MotionReference:
                        if (referenceValid == true) Span(0,242);
                        break;
                    case AvatarKind.HumanMask: Span(0,12); break;
                    case AvatarKind.Motion: Span(0,4); Span(16,36); Span(64,212); Span(288,48); break;
                    case AvatarKind.HumanPose:
                        Span(0,80);
                        for (int i = 0; i < 4; i++) { Span(80 + i * 96,56); Span(144 + i * 96,20); }
                        Span(464,508); Span(976,336); break;
                    case AvatarKind.NodeState: {
                        var header = reader.Read(pointer, 56);
                        var offsets = new[] { 0, 8, 16, 32, 40 };
                        for (int edge = 0; edge < offsets.Length; edge++)
                            edges.Add(Node(M.Ptr(header, offsets[edge]), NodeStateEdges[edge]));
                        Span(24, 1); Span(48, 1); break;
                    }
                    default: throw Error("unknown avatar graph domain");
                }
                node.Fields = fields.ToArray(); node.Edges = edges.ToArray(); node.Extents = extents.ToArray(); node.References = refs.ToArray(); return index;
            }
            private static int Size(ulong count) => count <= MaximumEntries ? (int)count : throw Error("avatar binding count exceeds bound");
            internal IntPtr ResolveBorrowedEdge(int nodeIndex, AvatarKind expectedKind)
            {
                if (nodeIndex == -1) return IntPtr.Zero;
                if (nodeIndex < 0 || nodeIndex >= Pointers.Count || Nodes[nodeIndex].Kind != expectedKind)
                    throw Error("evaluation input edge kind or node index differs");
                return Pointers[nodeIndex];
            }
            internal void Prepare(AvatarNode[] source, List<PendingWrite> writes, Func<string,bool,IntPtr> resolve, HashSet<int> controllerValues, Func<string,int> resolveObject)
            {
                Reject(source.Concat(Nodes).SelectMany(AvatarNodeScalarGaps));
                if (source.Length != Nodes.Count) throw Error("target avatar allocation closure differs");
                for (var index = 0; index < source.Length; index++) {
                    var row = source[index]; var before = Nodes[index];
                    if (row.Kind != before.Kind || row.ReadOnly != before.ReadOnly || row.MotionReferenceValid != before.MotionReferenceValid || !row.Edges.SequenceEqual(before.Edges)
                        || !row.Extents.SequenceEqual(before.Extents) || row.Fields.Length != before.Fields.Length) throw Error("target avatar type, alias or allocation differs");
                    for (var slot = 0; slot < row.References.Length; slot++) {
                        bool isRootGameObject = slot == row.Extents[0]; var old = before.References[slot]; var next = row.References[slot];
                        if (old == Unbound || next == Unbound) throw Error("unbound avatar/transform target dependency");
                        if (isRootGameObject && row.Extents[0] == 0) {
                            if (old.Length != 0 || next.Length != 0) throw Error("inactive avatar root-ID slot carries a resource key");
                            continue;
                        }
                        var prior = old.Length == 0 ? IntPtr.Zero : resolve(old,isRootGameObject);
                        var target = next.Length == 0 ? IntPtr.Zero : resolve(next,isRootGameObject);
                        byte[] Storage(IntPtr value) => isRootGameObject ? value == IntPtr.Zero ? new byte[4] : M.Read(IntPtr.Add(value,8),4) : BitConverter.GetBytes(value.ToInt64());
                        writes.Add(new PendingWrite(refSlots[index][slot],Storage(prior),Storage(target)));
                    }
                    PrepareValueIntegers(index,row,before,writes,controllerValues,resolveObject);
                    for (var field = 0; field < row.Fields.Length; field++) {
                        if (controllerValues.Contains(index)) continue; // Alias is written once by the owning ControllerMemory.
                        if (row.ReadOnly) {
                            if (!row.Fields[field].SequenceEqual(before.Fields[field])) throw Error("avatar shared read-only dependency has different content");
                        } else writes.Add(new PendingWrite(Cells[index][field], before.Fields[field], row.Fields[field]));
                    }
                }
            }
        }
        private static HashSet<int> ControllerOwnedAvatarValues(Data data)
        {
            var result = new HashSet<int>();
            foreach (var player in data.Playables) {
                int index = player.SharedAvatarValues;
                if (index == -1) continue;
                if (index < 0 || index >= data.AvatarNodes.Length || player.Memory?.ParameterBanks == null)
                    throw Error("avatar/controller value alias lacks its live parameter bank owner");
                var row = data.AvatarNodes[index]; var banks = player.Memory.ParameterBanks;
                if (row.Kind != AvatarKind.Values) throw Error("avatar/controller value alias has another native kind");
                for (int bank = 0, field = 0; bank < banks.Length; bank++) {
                    if (row.Extents[bank] != banks[bank].Length / ParameterStrides[bank]
                        || bank != 4 && !row.Fields[field++].SequenceEqual(banks[bank])) throw Error("shared avatar/controller value bank contents differ");
                }
                if (row.Integers != null && (row.Integers.Objects.Length != 0 || !row.Integers.ControllerParameters
                    || !row.Integers.Ordinary.SequenceEqual(banks[4]))) throw Error("shared controller integer payload differs from its unique writer");
                if (!result.Add(index)) throw Error("one avatar value allocation has multiple independently owned controller memories");
            }
            return result;
        }
        private static void ValidateAvatar(Data data)
        {
            var nodes = data.AvatarNodes;
            if (nodes == null || nodes.Length > MaximumEntries) throw Error("avatar node directory shape differs");
            var used = new HashSet<int>(); var readOnly = new HashSet<int>();
            void Visit(int[] edges, AvatarKind[] types) {
                if (edges == null || edges.Length != types.Length) throw Error("avatar typed edge directory differs");
                for (int i = 0; i < edges.Length; i++) {
                    int index = edges[i]; if (index == -1) continue;
                    if (index < 0 || index >= nodes.Length || nodes[index] == null || nodes[index].Kind != types[i]) throw Error("avatar edge kind/index differs");
                    if (ReferenceEquals(types,DatasetEdges) && i == 0
                        || ReferenceEquals(types,ConstantEdges) || ReferenceEquals(types,HumanEdges) || ReferenceEquals(types,BindingEdges)) readOnly.Add(index);
                    if (!used.Add(index)) continue;
                    var child = nodes[index];
                    Visit(child.Edges, child.Kind == AvatarKind.Output ? OutputEdges : child.Kind == AvatarKind.Workspace ? WorkspaceEdges :
                        child.Kind == AvatarKind.Constant ? ConstantEdges : child.Kind == AvatarKind.Human ? HumanEdges : child.Kind == AvatarKind.Binding ? BindingEdges
                        : child.Kind == AvatarKind.NodeState ? NodeStateEdges : Array.Empty<AvatarKind>());
                }
            }
            foreach (var row in data.Rows) {
                if (row.Datasets == null || row.Datasets.Length != 2 || row.Inputs == null || row.Inputs.Length != 2) throw Error("main/sampling Animator domains differ");
                foreach (var set in row.Datasets) {
                    if (set == null || set.MemorySize < 0 || set.MemorySize > MaximumBytes) throw Error("avatar dataset extent differs");
                    Visit(set.Edges, DatasetEdges);
                    if (set.OwnsAvatar == 1 && set.Edges[0] == -1)
                        throw Error("owned AvatarConstant has no typed allocation root");
                }
                for (var inputIndex = 0; inputIndex < row.Inputs.Length; inputIndex++) {
                    var input = row.Inputs[inputIndex];
                    if (input == null || !Text(input.Root) || input.Root.Length != 0 && input.NullRootIndex != 0 || input.Flags == null || input.Flags.Length != 4
                        || input.Versions == null || input.Versions.Length != 8) throw Error("Animator evaluation input shape differs");
                    if (input.Dormant && (row.Datasets[inputIndex].Initialized != 0 || row.Datasets[inputIndex].OwnsAvatar != 0
                        || row.Datasets[inputIndex].MemorySize != 0 || row.Datasets[inputIndex].Edges.Any(edge => edge != -1)
                        || input.Edges.Any(edge => edge != -1) || input.Flags.Any(value => value != 0) || input.Versions.Any(value => value != 0)
                        || input.Root.Length != 0 || input.NullRootIndex != 0)) throw Error("dormant input retains active data");
                    if (row.Evaluation[inputIndex].Dormant != input.Dormant) throw Error("input and evaluation cache lifetime differ");
                    Visit(input.Edges, InputEdges);
                    Visit(new[] {row.Evaluation[inputIndex].Backing.DefaultValues},new[] {AvatarKind.Values});
                }
                ValidateGenericDomains(row,nodes);
                if (row.Retained!=null) {
                    Visit(new[] {row.Retained.Backup},new[] {AvatarKind.Values});
                    foreach (var frame in row.Retained.Frames) Visit(new[] {frame.Avatar},new[] {AvatarKind.Memory});
                }
            }
            if (data.Graphs == null || data.Graphs.Any(graph => graph == null || graph.Nodes == null || graph.Nodes.Any(node => node == null))) throw Error("avatar playable owner directory differs");
            foreach (var graph in data.Graphs) foreach (var node in graph.Nodes) {
                if (node.Clip != null) {
                    Visit(node.Clip.Caches,ClipCacheKinds);
                    var reference = node.Clip.Caches[2];
                    if (reference != -1 && nodes[reference].MotionReferenceValid !=
                        (node.Clip.Fields[0][4] == 1 && node.Clip.Constant.Length != 0))
                        throw Error("clip MotionXReference validity differs from its root-processing phase");
                    if (node.Clip.MachineOwner >= 0 && (node.Clip.MachineOwner >= data.Playables.Length
                        || data.Playables[node.Clip.MachineOwner].Memory == null
                        || node.Clip.MachineIndex >= data.Playables[node.Clip.MachineOwner].Memory!.Machines.Length))
                        throw Error("clip state-machine owner or index differs");
                }
                if (node.Pose != null) Visit(new[] { node.Pose.State }, new[] { AvatarKind.NodeState });
                if (node.Layer != null) {
                    var layer = node.Layer;
                    foreach (int state in layer.States) Visit(new[] {state},new[] {AvatarKind.NodeState});
                    foreach (int mask in layer.Masks) Visit(new[] {mask},new[] {AvatarKind.Mask});
                    Visit(new[] {layer.LastValues},new[] {AvatarKind.Values});
                    foreach (var parameter in layer.Parameters) Visit(new[] {parameter.Skeleton},new[] {AvatarKind.SkeletonMask});
                }
                if (node.Mixer != null) {
                    var mixer = node.Mixer;
                    Visit(new[] { mixer.Weight, mixer.Mask, mixer.State }, new[] { AvatarKind.Weight, AvatarKind.Mask, AvatarKind.NodeState });
                    if (mixer.MachineOwner >= 0 && (mixer.MachineOwner >= data.Playables.Length
                        || data.Playables[mixer.MachineOwner].Memory == null || mixer.MachineIndex < 0
                        || mixer.MachineIndex >= data.Playables[mixer.MachineOwner].Memory!.Machines.Length))
                        throw Error("mixer state-machine owner or index differs");
                }
            }
            foreach (var player in data.Playables) Visit(new[] {player.ResetValues},new[] {AvatarKind.Values});
            if (used.Count != nodes.Length) throw Error("avatar node has no actual owner");
            for (int i = 0; i < nodes.Length; i++) {
                var row = nodes[i]; if (row.ReadOnly != readOnly.Contains(i)) throw Error("avatar read-only ownership differs");
                if (row.Kind != AvatarKind.MotionReference && row.MotionReferenceValid != null) throw Error("non-reference avatar node owns MotionXReference lifetime");
                if (row.Fields == null || row.Extents == null || row.Extents.Any(n => n < 0 || n > MaximumEntries) || !Keys(row.References)
                    || row.Kind != AvatarKind.Binding && row.References.Length != 0) throw Error("avatar field extent differs");
                var lengths = new List<int>(); int[] strides;
                switch (row.Kind) {
                    case AvatarKind.Output: strides = Array.Empty<int>(); break;
                    case AvatarKind.Constant:
                        if (row.Extents.Length != 4) throw Error("avatar constant relative table count differs");
                        lengths.AddRange(row.Extents.Select(n => n * 4)); lengths.AddRange(new[] {4,48}); strides = Array.Empty<int>(); break;
                    case AvatarKind.Skeleton:
                        if (row.Extents.Length != 3 || row.Extents[0] != row.Extents[1]) throw Error("skeleton node and ID directories differ");
                        strides = new[] {8,4,88}; break;
                    case AvatarKind.Binding:
                        if (row.Extents.Length != 2 || row.References.Length != row.Extents[0] + 1 + row.Extents[1]) throw Error("avatar binding reference count differs");
                        if (row.Extents[0] == 0 && row.References[0].Length != 0)
                            throw Error("inactive avatar root-ID slot carries a resource key");
                        lengths.AddRange(Enumerable.Repeat(8,row.Extents[1])); strides = Array.Empty<int>(); break;
                    case AvatarKind.Human: lengths.AddRange(new[] {48,235}); strides = Array.Empty<int>(); break;
                    case AvatarKind.Hand: lengths.Add(60); strides = Array.Empty<int>(); break;
                    case AvatarKind.Input: lengths.Add(21); strides = Array.Empty<int>(); break;
                    case AvatarKind.Workspace: lengths.Add(51); strides = Array.Empty<int>(); break;
                    case AvatarKind.Memory: lengths.Add(166); strides = Array.Empty<int>(); break;
                    case AvatarKind.TrsPose: strides = new[] {48}; break;
                    case AvatarKind.AffinePose: strides = new[] {64}; break;
                    case AvatarKind.Values: strides = ParameterStrides; break;
                    case AvatarKind.Mask: strides = new[] {1,1,1,1,1}; break;
                    case AvatarKind.Weight: strides = new[] {4,4,4,4,4}; break;
                    case AvatarKind.SkeletonMask: strides = new[] {8}; break;
                    case AvatarKind.ClipMemory:
                        if (row.Extents.Length != 2) throw Error("clip memory count dependencies differ");
                        lengths.AddRange(new[] {8,checked(row.Extents[0] * 20)});
                        strides = Array.Empty<int>(); break;
                    case AvatarKind.ClipOutput: strides = new[] {4}; break;
                    case AvatarKind.MotionReference:
                        if (!row.MotionReferenceValid.HasValue) throw Error("legacy MotionXReference lacks explicit lifetime");
                        if (row.MotionReferenceValid.Value) lengths.Add(242);
                        strides = Array.Empty<int>(); break;
                    case AvatarKind.HumanMask: lengths.Add(12); strides = Array.Empty<int>(); break;
                    case AvatarKind.NodeState: lengths.AddRange(new[] {1,1}); strides = Array.Empty<int>(); break;
                    case AvatarKind.Motion: lengths.AddRange(new[] {4,36,212,48}); strides = Array.Empty<int>(); break;
                    case AvatarKind.HumanPose: lengths.AddRange(new[] {80,56,20,56,20,56,20,56,20,508,336}); strides = Array.Empty<int>(); break;
                    default: throw Error("unknown avatar node type");
                }
                if (row.Kind != AvatarKind.Constant && row.Kind != AvatarKind.Binding && row.Kind != AvatarKind.ClipMemory && row.Extents.Length != strides.Length) throw Error("avatar bank count differs");
                for (int b = 0; b < strides.Length; b++) if (row.Kind != AvatarKind.Values || b != 4) lengths.Add(checked(row.Extents[b] * strides[b]));
                if (lengths.Count != row.Fields.Length || lengths.Where((length, b) => row.Fields[b] == null || row.Fields[b].Length != length).Any()) throw Error("avatar native extent differs");

            }
            ValidateValueChannels(data);
        }
    }
}
