using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Pinned Unity PDB 0334be90-63dd-4644-bee1-fc8d56c840c2/1.
    // D: asset bindings, drawing settings, retained matrices/bounds and visibility.
    // Dependency: target native pointers, mesh/sprite consumer lists, scene handles,
    // sorting-group/streaming indices and frame indices. No source pointer is data.
    // S0: type layout and registry implementation; this adapter never resets D to
    // obtain equality. Property blocks are ResidentRendererPropertyBlocks' domain.
    // CompleteCoverage is deliberately derived from the implemented domains, not
    // trusted from a payload's editable list of diagnostic coverage gaps.
    internal static partial class ResidentRendererState
    {
        private const int MaximumSlots = 100000;
        // SharedRendererData+232 names 12 bitfields from m_RendererType[0:5]
        // through m_DynamicOccludee[21]. The next named field starts at +236.
        private const uint NamedDrawingBits = 0x003fffffU;
        internal sealed class Data
        {
            public int Version = 2;
            public Row[] Rows = Array.Empty<Row>();
            public SceneState Scene = new SceneState();
            public UpdateRegistry Updates = new UpdateRegistry();
            public ConsumerList[] Consumers = Array.Empty<ConsumerList>();
            public SpriteRegistryState Sprites = new SpriteRegistryState();
            public string[] CoverageGaps = Array.Empty<string>();
        }
        internal sealed class Row
        {
            public string Owner = "", Kind = "";
            public ResidentObjectDirectory.TypeRecord Type = new ResidentObjectDirectory.TypeRecord();
            public bool Execution;
            // Exact scalar ranges are fixed by actual native type, never supplied
            // as arbitrary write offsets by a package.
            public byte[][] Scalars = Array.Empty<byte[]>();
            public string[] Materials = Array.Empty<string>(), Meshes = Array.Empty<string>(), CachedMeshes = Array.Empty<string>();
            public string StaticBatchRoot = "", ProbeAnchor = "", ProbeAnchorCached = "", LightProbeVolume = "", LodGroup = "";
            public int SceneHandle = -1, LastLightProbe;
            public int MaterialCapacity;
            public bool[] ConsumerLinked = Array.Empty<bool>();
            public string Sprite = "", CachedSprite = "";
            public bool SpriteDeformed, SpriteTilingFence, SpriteNineSlice;
            public bool SpriteTilingJobDone = true;
            public SpriteCacheState? SpriteCache;
            public ResidentShaderPropertySheet.Data? SecondaryProperties;
            public int SecondaryReferenceCount;
            public string[] Bones = Array.Empty<string>();
            public string RootBone = "", Animator = "", Cloth = "";
            public int BoneCapacity, WeightCapacity, OptimizedBoneCapacity, SkinVertexCapacity, SkinHandle;
            public byte[] Weights = Array.Empty<byte>(), OptimizedBones = Array.Empty<byte>(), SkinVertices = Array.Empty<byte>();
            public ushort OptimizedRootBone;
            public string[] OpaqueStates = Array.Empty<string>();
        }
        internal sealed class SceneState
        {
            public SceneNode[] Nodes = Array.Empty<SceneNode>(), Intermediate = Array.Empty<SceneNode>();
            public byte[] Bounds = Array.Empty<byte>(), IntermediateBounds = Array.Empty<byte>(), Visibility = Array.Empty<byte>();
            public PendingNode[] Pending = Array.Empty<PendingNode>();
            public int[] Removal = Array.Empty<int>();
            public int[] Capacities = new int[7];
            public int PreventChanges, ForbidIntermediate;
            public bool RequestPvsRebuild, HasGateState, HasUmbraTome;
        }
        internal sealed class SceneNode
        {
            public string Owner = "";
            public byte[] State = Array.Empty<byte>();
        }
        internal sealed class PendingNode
        {
            public string Owner = "";
            public bool AddBeforeRemove;
        }
        internal sealed class UpdateRegistry
        {
            public UpdateSystem[] Systems = Array.Empty<UpdateSystem>();
            public ulong InterestMask;
            public int MotionVectorFrame;
        }
        internal sealed class UpdateSystem
        {
            public bool Registered;
            public int ChangeSystem;
            public ulong CombinedSystems;
            public int[] FunctionRvas = new int[5];
        }
        internal sealed class ConsumerList
        {
            public string Asset = "", Kind = "";
            public Consumer[] Entries = Array.Empty<Consumer>();
        }
        internal sealed class Consumer
        {
            public string Owner = "", OtherRole = "";
            public int Slot;
        }
        private sealed class Range
        {
            internal readonly int Offset, Count;
            internal Range(int offset, int count) { Offset = offset; Count = count; }
        }
        private static readonly Range[] Common = { new Range(64, 244), new Range(424, 7), new Range(432, 12) };
        private static readonly Range[] MeshExtra = { new Range(472, 1) };
        private static readonly Range[] SpriteExtra = { new Range(492, 18), new Range(512, 8), new Range(536, 25), new Range(592, 26) };
        private static readonly Range[] SkinExtra = { new Range(536, 1), new Range(624, 32), new Range(712, 5), new Range(768, 1) };
        private static Range[] Ranges(string kind) => kind == "MeshFilter" ? Array.Empty<Range>()
            : Common.Concat(kind == "MeshRenderer" ? MeshExtra : kind == "SpriteRenderer" ? SpriteExtra
                : kind == "SkinnedMeshRenderer" ? SkinExtra : Array.Empty<Range>()).ToArray();

        // This resolver indexes actual loaded objects of the required native
        // type. It never substitutes a name or loads an asset from a payload path.
        private sealed class SourceBindings
        {
            private readonly ResidentObjectDirectory directory;
            private readonly Func<object, string?> bind;
            internal string OtherOwner(IntPtr pointer,int type)
            {
                var key = ResidentNativeAssetReference.Bind(pointer,type,typeof(NativeObject),bind);
                return key == ResidentNativeAssetReference.Missing ? throw Error("asset consumer has no portable registered owner") : key;
            }
            internal readonly Dictionary<string, IntPtr> Bound = new Dictionary<string,IntPtr>(StringComparer.Ordinal);
            private readonly Dictionary<int,IntPtr> idPointers = new Dictionary<int,IntPtr>();
            private readonly Dictionary<(IntPtr,Type),string> pointerKeys = new Dictionary<(IntPtr,Type),string>();
            internal void RequireUnchanged()
            {
                foreach (var slot in idPointers) {
                    if (ResidentLoadedAssetIndex.RegisteredPointer(slot.Key,out var verify) != slot.Value) throw Error("asset reference changed its registration");
                    verify();
                }
                foreach (var slot in pointerKeys)
                    if (ResidentNativeAssetReference.BindPointer(slot.Key.Item1,slot.Key.Item2,bind) != slot.Value) throw Error("asset reference changed its portable binding");
            }
            internal SourceBindings(ResidentObjectDirectory directory, Func<object, string?> bind) { this.directory = directory; this.bind = bind; }
            internal string Id<T>(int id) where T : NativeObject
            {
                if (id == 0) return "";
                if (!idPointers.TryGetValue(id,out var pointer)) {
                    pointer = ResidentLoadedAssetIndex.RegisteredPointer(id,out var verify); verify(); idPointers.Add(id,pointer);
                }
                return Pointer<T>(pointer);
            }
            internal string Pointer<T>(IntPtr pointer) where T : NativeObject
            {
                if (pointer == IntPtr.Zero) return "";
                var identity = (pointer,typeof(T));
                if (pointerKeys.TryGetValue(identity,out var known)) return known;
                var key = ResidentNativeAssetReference.BindPointer(pointer,typeof(T),bind); pointerKeys.Add(identity,key);
                if (Bound.TryGetValue(key,out var old) && old != pointer) throw Error("native binding collapsed two owners");
                Bound[key] = pointer; return key;
            }
            internal string Renderer(IntPtr pointer, bool baseRenderer)
            {
                if (pointer == IntPtr.Zero) return "";
                var native = baseRenderer ? IntPtr.Subtract(pointer, 56) : pointer;
                var key = Pointer<Renderer>(native);
                if (!directory.NativeBindings.ContainsKey(key)) throw Error("registered renderer lies outside the complete resident directory");
                return key;
            }
        }
        private static string Kind(Component owner) => owner.GetType() == typeof(MeshFilter) ? "MeshFilter"
            : owner.GetType() == typeof(MeshRenderer) ? "MeshRenderer" : owner.GetType() == typeof(SpriteRenderer) ? "SpriteRenderer"
            : owner.GetType() == typeof(SkinnedMeshRenderer) ? "SkinnedMeshRenderer" : "UnsupportedRenderer";
        internal static Data Capture(ResidentObjectDirectory directory, Func<object, string?> bind)
        {
            if (directory == null || bind == null) throw new ArgumentNullException();
            LoadingEngineClockProbe.RequirePinnedEngine();
            var references = new SourceBindings(directory, bind); var result = new Data(); var rows = new List<Row>();
            foreach (var pair in directory.NativeBindings.Where(pair => pair.Value is Renderer || pair.Value is MeshFilter).OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
                var owner = (Component)pair.Value; var native = M.Owner(owner); var kind = Kind(owner);
                var size = kind == "MeshFilter" ? 64 : kind == "MeshRenderer" ? 568 : kind == "SpriteRenderer" ? 624 : kind == "SkinnedMeshRenderer" ? 776 : 448;
                var bytes = M.Read(native, size); var opaque = new List<string>();
                var row = new Row { Owner = pair.Key, Kind = kind, Type = ResidentObjectDirectory.TypeRecord.Capture(owner.GetType()),
                    Execution = directory.Ownership.IsExecution(owner), Scalars = Ranges(kind).Select(range => M.Slice(bytes, range.Offset, range.Count)).ToArray() };
                if (kind == "MeshFilter") row.Meshes = new[] { references.Id<Mesh>(M.I32(bytes, 56)) };
                else {
                    var materialIds = ReadArray(native, 320, 4, out row.MaterialCapacity);
                    row.Materials = Ints(materialIds).Select(references.Id<Material>).ToArray();
                    row.SceneHandle = M.I32(bytes, 352); row.LastLightProbe = M.I32(bytes, 396);
                    row.StaticBatchRoot = references.Id<Transform>(M.I32(bytes, 392));
                    row.ProbeAnchor = references.Id<Transform>(M.I32(bytes, 400)); row.ProbeAnchorCached = references.Id<Transform>(M.I32(bytes, 404));
                    row.LightProbeVolume = references.Id<GameObject>(M.I32(bytes, 408)); row.LodGroup = references.Pointer<LODGroup>(M.Ptr(bytes, 416));
                    if (kind == "MeshRenderer") {
                        row.Meshes = new[] { 552, 556, 560 }.Select(offset => references.Id<Mesh>(M.I32(bytes, offset))).ToArray();
                        row.CachedMeshes = new[] { 448, 456, 464 }.Select(offset => references.Pointer<Mesh>(M.Ptr(bytes, offset))).ToArray();
                        row.ConsumerLinked = new[] { 480, 504, 528 }.Select(offset => Linked(bytes, offset, native, 0)).ToArray();
                    } else if (kind == "SpriteRenderer") {
                        row.Sprite = references.Id<Sprite>(M.I32(bytes, 488)); row.CachedSprite = references.Pointer<Sprite>(M.Ptr(bytes, 456));
                        row.ConsumerLinked = new[] { Linked(bytes, 464, native, 448) };
                        var sheet = M.Ptr(bytes, 520); row.SecondaryProperties = sheet == IntPtr.Zero ? null : ResidentShaderPropertySheet.Capture(sheet, bind);
                        row.SecondaryReferenceCount = sheet == IntPtr.Zero ? 0 : M.I32(M.Read(IntPtr.Add(sheet, 12), 4), 0);
                        row.SpriteDeformed = M.Ptr(bytes, 528) != IntPtr.Zero;
                        row.SpriteTilingFence = M.Slice(bytes, 568, 12).Any(value => value != 0);
                        row.SpriteTilingJobDone = Function<FenceDone>(0x725ac0, "40534883ec30488bd9e86217000084c0742648833b007420")(IntPtr.Add(native, 568));
                        row.SpriteNineSlice = M.Ptr(bytes, 584) != IntPtr.Zero;
                        row.SpriteCache = row.SpriteNineSlice ? CaptureSpriteCache(M.Ptr(bytes, 584), references) : null;
                    } else if (kind == "SkinnedMeshRenderer") {
                        row.Meshes = new[] { references.Id<Mesh>(M.I32(bytes, 468)) };
                        row.CachedMeshes = new[] { references.Pointer<Mesh>(M.Ptr(bytes, 472)) };
                        row.Bones = Ints(ReadArray(native, 480, 4, out row.BoneCapacity)).Select(references.Id<Transform>).ToArray();
                        row.RootBone = references.Id<Transform>(M.I32(bytes, 512));
                        row.OptimizedBones = ReadArray(native, 544, 2, out row.OptimizedBoneCapacity); row.OptimizedRootBone = BitConverter.ToUInt16(bytes, 576);
                        row.Animator = references.Pointer<Component>(M.Ptr(bytes, 584)); row.Cloth = references.Pointer<Component>(M.Ptr(bytes, 720));
                        row.Weights = ReadArray(native, 592, 4, out row.WeightCapacity); row.SkinVertices = ReadArray(native, 680, 1, out row.SkinVertexCapacity);
                        row.ConsumerLinked = new[] { Linked(bytes, 744, native, 0) }; row.SkinHandle = M.I32(bytes, 772);
                        if (M.Slice(bytes, 448, 12).Concat(M.Slice(bytes, 464, 4)).Any(value => value != 0)) opaque.Add("skin-job-fences");
                        foreach (var item in new[] { (520, "skin-cloth-job"), (528, "skin-job-info"), (656, "skin-vertex-gpu"), (664, "skin-raytrace-gpu"),
                            (672, "skin-last-vertex-gpu"), (728, "skin-pose-gpu"), (736, "skin-compute-pose-gpu") })
                            if (M.Ptr(bytes, item.Item1) != IntPtr.Zero) opaque.Add(item.Item2);
                    }
                }
                row.OpaqueStates = opaque.OrderBy(value => value, StringComparer.Ordinal).ToArray();
                if (!M.Read(native, size).SequenceEqual(bytes)) throw Error("native renderer changed during capture");
                rows.Add(row);
            }
            ResidentFailureEvidence.Mark("renderer scene");
            result.Rows = rows.ToArray(); result.Scene = CaptureScene(references); result.Updates = CaptureUpdates();
            ResidentFailureEvidence.Mark("renderer asset users");
            result.Consumers = CaptureConsumers(result.Rows, directory, references);
            ResidentFailureEvidence.Mark("renderer sprite registry");
            result.Sprites = CaptureSpriteRegistry(directory, bind);
            references.RequireUnchanged();
            result.CoverageGaps = RequiredCoverageGaps(result); Validate(result); return result;
        }
        private static bool Linked(byte[] bytes, int offset, IntPtr owner, int dataOffset)
        {
            var previous = M.Ptr(bytes, offset); var next = M.Ptr(bytes, offset + 8);
            if ((previous == IntPtr.Zero) != (next == IntPtr.Zero) || M.Ptr(bytes, offset + 16) != IntPtr.Add(owner, dataOffset))
                throw Error("native asset consumer node is invalid");
            if (previous != IntPtr.Zero && (M.Ptr(IntPtr.Add(previous, 8)) != IntPtr.Add(owner, offset) || M.Ptr(next) != IntPtr.Add(owner, offset)))
                throw Error("native asset consumer links are not reciprocal");
            return previous != IntPtr.Zero;
        }
        private static byte[] ReadArray(IntPtr owner, int offset, int stride, out int capacity)
        {
            var header = M.Read(IntPtr.Add(owner, offset), 32); var size = M.U64(header, 16); var cap = M.U64(header, 24) >> 1;
            if (size > cap || cap > MaximumSlots || cap * (ulong)stride > 32 * 1024 * 1024) throw Error("renderer array exceeds bounds");
            capacity = (int)cap; return M.Read(M.Ptr(header, 0), checked((int)size * stride));
        }
        private static int[] Ints(byte[] bytes) => Enumerable.Range(0, bytes.Length / 4).Select(index => M.I32(bytes, index * 4)).ToArray();
        private static IntPtr ScenePointer()
        {
            var engine = LoadingEngineClockProbe.RequirePinnedEngine(); var entry = IntPtr.Add(engine.BaseAddress, 0x5d6870);
            if (!M.Read(entry, 8).SequenceEqual(new byte[] { 0x48, 0x8b, 0x05, 0x89, 0xa0, 0x38, 0x01, 0xc3 })) throw Error("renderer scene getter ABI differs");
            var pointer = M.Ptr(IntPtr.Add(engine.BaseAddress, 0x1960900));
            return pointer == IntPtr.Zero ? throw Error("renderer scene singleton is absent") : pointer;
        }
        private static SceneState CaptureScene(SourceBindings references)
        {
            var pointer = ScenePointer(); var before = M.Read(pointer, 256); var state = new SceneState();
            SceneNode[] Nodes(int offset, int capacityIndex) {
                var data = ReadArray(pointer, offset, 24, out state.Capacities[capacityIndex]);
                return Enumerable.Range(0, data.Length / 24).Select(index => new SceneNode {
                    Owner = references.Renderer(M.Ptr(data, index * 24), true), State = M.Slice(data, index * 24 + 8, 16) }).ToArray();
            }
            state.Intermediate = Nodes(0, 0); state.IntermediateBounds = ReadArray(pointer, 32, 24, out state.Capacities[1]);
            state.ForbidIntermediate = M.I32(before, 64);
            var pending = ReadArray(pointer, 72, 16, out state.Capacities[2]);
            state.Pending = Enumerable.Range(0, pending.Length / 16).Select(index => new PendingNode {
                Owner = references.Renderer(M.Ptr(pending, index * 16), false), AddBeforeRemove = Bool(pending[index * 16 + 8]) }).ToArray();
            state.Removal = Ints(ReadArray(pointer, 104, 4, out state.Capacities[3]));
            state.Nodes = Nodes(136, 4); state.Bounds = ReadArray(pointer, 168, 24, out state.Capacities[5]);
            state.Visibility = ReadArray(pointer, 200, 1, out state.Capacities[6]);
            state.HasGateState = M.Ptr(before, 232) != IntPtr.Zero; state.HasUmbraTome = M.Ptr(before, 240) != IntPtr.Zero;
            state.PreventChanges = M.I32(before, 248); state.RequestPvsRebuild = Bool(before[252]);
            if (!M.Read(pointer, 256).SequenceEqual(before)) throw Error("renderer scene changed during capture"); return state;
        }
        private static IntPtr UpdatePointer()
        {
            var engine = LoadingEngineClockProbe.RequirePinnedEngine(); var pointer = M.Ptr(IntPtr.Add(engine.BaseAddress, 0x1a12db8));
            return pointer == IntPtr.Zero ? throw Error("renderer update manager is absent") : pointer;
        }
        private static UpdateRegistry CaptureUpdates()
        {
            var engine = LoadingEngineClockProbe.RequirePinnedEngine(); var pointer = UpdatePointer(); var bytes = M.Read(pointer, 1104);
            int Code(int offset) {
                var function = M.Ptr(bytes, offset); if (function == IntPtr.Zero) return 0;
                var rva = function.ToInt64() - engine.BaseAddress.ToInt64();
                if (rva <= 0 || rva >= engine.ModuleMemorySize) throw Error("renderer update callback is outside the pinned engine"); return (int)rva;
            }
            var result = new UpdateRegistry { InterestMask = M.U64(bytes, 1088), MotionVectorFrame = M.I32(bytes, 1096),
                Systems = Enumerable.Range(0, 17).Select(index => new UpdateSystem {
                    Registered = Bool(bytes[index * 64]), ChangeSystem = M.I32(bytes, index * 64 + 16), CombinedSystems = M.U64(bytes, index * 64 + 24),
                    FunctionRvas = new[] { 8, 32, 40, 48, 56 }.Select(offset => Code(index * 64 + offset)).ToArray() }).ToArray() };
            if (!M.Read(pointer, 1104).SequenceEqual(bytes)) throw Error("renderer update registry changed during capture"); return result;
        }
        private static int[] ConsumerOffsets(string kind) => kind == "MeshRenderer" ? new[] { 480, 504, 528 }
            : kind == "SkinnedMeshRenderer" ? new[] { 744 } : kind == "SpriteRenderer" ? new[] { 464 } : Array.Empty<int>();
        private static ConsumerList[] CaptureConsumers(Row[] rows, ResidentObjectDirectory directory, SourceBindings references)
        {
            var assets = rows.SelectMany(row => row.CachedMeshes.Select(key => (key, "Mesh")))
                .Concat(rows.Where(row => row.Kind == "SpriteRenderer").Select(row => (row.CachedSprite, "Sprite")))
                .Where(value => value.Item1.Length != 0).Distinct().OrderBy(value => value.Item2, StringComparer.Ordinal).ThenBy(value => value.Item1, StringComparer.Ordinal);
            var nodes = IndexConsumerNodes(directory,rows);
            return assets.Select(asset => new ConsumerList { Asset = asset.Item1, Kind = asset.Item2,
                Entries = ReadConsumers(references.Bound[asset.Item1],asset.Item2,nodes,references) }).ToArray();
        }
        private sealed class ConsumerNode
        {
            internal string Key = "", Kind = "";
            internal int Slot;
            internal IntPtr Owner;
        }
        private static Dictionary<IntPtr,ConsumerNode> IndexConsumerNodes(ResidentObjectDirectory directory,Row[] rows)
        {
            var nodes = new Dictionary<IntPtr, ConsumerNode>();
            foreach (var row in rows) {
                var offsets = ConsumerOffsets(row.Kind); if (offsets.Length == 0) continue;
                var native = M.Owner(directory.Resolve(row.Owner));
                for (var slot = 0; slot < offsets.Length; slot++) nodes.Add(IntPtr.Add(native, offsets[slot]),
                    new ConsumerNode { Key = row.Owner, Kind = row.Kind, Slot = slot, Owner = native });
            }
            return nodes;
        }
        private static Consumer[] ReadConsumers(NativeObject asset, string kind, ResidentObjectDirectory directory, Row[] rows)
            => ReadConsumers(M.Owner(asset),kind,IndexConsumerNodes(directory,rows),null);
        private static Consumer[] ReadConsumers(IntPtr pointer,string kind,Dictionary<IntPtr,ConsumerNode> nodes,SourceBindings? references)
        {
            var sentinel = IntPtr.Add(pointer, kind == "Mesh" ? 368 : 280);
            var first = M.Read(sentinel, 16); var previous = sentinel; var current = M.Ptr(first, 8); var visited = new HashSet<IntPtr>(); var entries = new List<Consumer>();
            while (current != sentinel) {
                if (current == IntPtr.Zero || !visited.Add(current) || visited.Count > MaximumSlots) throw Error(kind + " consumer list is cyclic or exceeds bounds");
                var bytes = M.Read(current, 24);
                if (M.Ptr(bytes, 0) != previous) throw Error(kind + " consumer list previous link disagrees with traversal");
                var payload = M.Ptr(bytes,16);
                if (nodes.TryGetValue(current,out var node)) {
                    var sprite = node.Kind == "SpriteRenderer";
                    if (sprite != (kind == "Sprite") || payload != IntPtr.Add(node.Owner,sprite ? 448 : 0) ||
                        M.Ptr(IntPtr.Add(node.Owner,sprite ? 456 : node.Kind == "MeshRenderer" ? 448+8*node.Slot : 472)) != pointer)
                        throw Error(kind + " registered scene consumer owner, slot or cached asset differs");
                    entries.Add(new Consumer { Owner = node.Key, Slot = node.Slot });
                } else {
                    if (references == null) throw Error(kind + " mutation would affect an owner outside the enrolled renderer rows");
                    entries.Add(ReadOtherConsumer(kind,current,payload,pointer,references));
                }
                if (!M.Read(current,24).SequenceEqual(bytes)) throw Error(kind + " consumer node changed during observation");
                previous = current; current = M.Ptr(bytes, 8);
            }
            if (M.Ptr(first, 0) != previous || !M.Read(sentinel, 16).SequenceEqual(first)) throw Error(kind + " consumer sentinel differs"); return entries.ToArray();
        }
        // The intrusive node carries an Object* (Mesh) or SpriteUser* (Sprite).
        // Verify the actual registered owner, PDB slot and cached asset before
        // exporting any identity. No address or per-process instance ID is data.
        private static Consumer ReadOtherConsumer(string kind,IntPtr node,IntPtr payload,IntPtr asset,SourceBindings references)
        {
            if (payload == IntPtr.Zero) throw Error(kind + " consumer has a null data pointer");
            var sprite = kind == "Sprite";
            var owner = sprite ? IntPtr.Subtract(payload,448) : payload;
            var type = ResidentNativeAssetReference.NativeClass(owner,0x194b230);
            var role = ""; var slot = 0; var baseAt = owner;
            int nodeOffset, cacheOffset;
            if (sprite && (type == ResidentLoadedAssetIndex.SpriteRendererClass || type == 0x194ab30)) {
                role = type == ResidentLoadedAssetIndex.SpriteRendererClass ? "SpriteRenderer" : "SpriteMask"; nodeOffset = 464; cacheOffset = 456;
            } else if (!sprite && type == 0x19494c0) {
                role = "MeshRenderer"; var delta = node.ToInt64()-owner.ToInt64();
                if (delta != 480 && delta != 504 && delta != 528) throw Error("unrecognized MeshRenderer consumer slot");
                slot = (int)(delta-480)/24; nodeOffset = (int)delta; cacheOffset = 448+8*slot;
            } else if (!sprite && type == 0x1949470) { role = "SkinnedMeshRenderer"; nodeOffset = 744; cacheOffset = 472; }
            else if (!sprite && type == 0x1949f70) { role = "MeshCollider"; nodeOffset = 128; cacheOffset = 120; }
            else if (!sprite && type == 0x1949cb0) {
                role = "ParticleSystem.Shape"; baseAt = M.Ptr(IntPtr.Add(owner,80)); nodeOffset = 1048; cacheOffset = 856;
            } else throw Error(kind + " consumer has unsupported native owner type RVA " + type.ToString("x"));
            if (baseAt == IntPtr.Zero || node != IntPtr.Add(baseAt,nodeOffset)) throw Error(kind + " " + role + " consumer node is not in its declared slot");
            if (role == "MeshCollider" ? M.I32(M.Read(IntPtr.Add(baseAt,cacheOffset),4),0) != M.I32(M.Read(IntPtr.Add(asset,8),4),0)
                : M.Ptr(IntPtr.Add(baseAt,cacheOffset)) != asset) throw Error(kind + " " + role + " consumer cache differs from the list asset");
            return new Consumer { Owner = references.OtherOwner(owner,type), OtherRole = role, Slot = slot };
        }
        // Asset-closure preflight also calls this for Meshes with no current
        // renderer cache reference. Such assets are not in Data.Consumers, but
        // a nonempty unbound ObjectUsers list must still reject common Canon.
        // The ordered list itself remains exclusively owned by this adapter.
        internal static void RequireMeshConsumersCovered(ResidentObjectDirectory directory, Mesh mesh)
        {
            if (directory == null || !ResidentObjectDirectory.IsLive(mesh) || mesh.GetType() != typeof(Mesh)) throw Error("Mesh consumer coverage lacks its exact live owner");
            var rows = directory.NativeBindings.Where(pair => pair.Value is Renderer)
                .Select(pair => new Row { Owner = pair.Key, Kind = Kind((Component)pair.Value) }).ToArray();
            var pointer = M.Owner(mesh);
            foreach (var consumer in ReadConsumers(mesh, "Mesh", directory, rows)) {
                var owner = (Component)directory.Resolve(consumer.Owner); var kind = Kind(owner); var native = M.Owner(owner);
                if (kind != "MeshRenderer" && kind != "SkinnedMeshRenderer") throw Error("Mesh ObjectUsers contains an unsupported native consumer type");
                var offset = kind == "MeshRenderer" ? 448 + consumer.Slot * 8 : 472;
                if (M.Ptr(IntPtr.Add(native, offset)) != pointer || directory.Ownership.IsExecution(owner))
                    throw Error("Mesh ObjectUsers has a foreign cache binding or execution owner");
            }
        }

        internal sealed class Plan
        {
            private readonly Data source, before;
            private readonly ResidentObjectDirectory directory;
            private readonly ResidentMutationScope scope;
            private readonly Func<string, NativeObject> resolve;
            private readonly Func<object, string?> bind;
            private readonly Action admission;
            private readonly HashSet<string> changedConsumers = new HashSet<string>(StringComparer.Ordinal);
            private bool used, applied;
            internal Plan(Data source, ResidentObjectDirectory directory, ResidentMutationScope scope,
                Func<string, NativeObject> resolve, Func<object, string?> bind, Action admitted)
            {
                RequireCompleteCoverage(source); this.source = Copy(source);
                this.directory = directory ?? throw new ArgumentNullException(nameof(directory)); this.scope = scope ?? throw new ArgumentNullException(nameof(scope));
                this.resolve = resolve ?? throw new ArgumentNullException(nameof(resolve)); this.bind = bind ?? throw new ArgumentNullException(nameof(bind));
                admission = admitted ?? throw new ArgumentNullException(nameof(admitted)); admission(); before = Capture(directory, bind); RequireCompleteCoverage(before);
                if (source.Rows.Length != before.Rows.Length) throw Error("complete renderer directory differs");
                for (var index = 0; index < source.Rows.Length; index++) {
                    var row = source.Rows[index]; var target = before.Rows[index];
                    if (row.Owner != target.Owner || row.Type.Key != target.Type.Key || row.Execution != target.Execution) throw Error("renderer native binding or execution ownership differs");
                    if (row.Execution && Json(row) != Json(target)) throw Error("execution renderer must already match");
                    if (!(directory.Resolve(row.Owner) is Component owner) || !row.Execution && !scope.Owns(owner)) throw Error("renderer is outside mutation scope");
                    foreach (var mesh in row.Meshes.Concat(row.CachedMeshes)) Check<Mesh>(mesh);
                    foreach (var material in row.Materials) Check<Material>(material);
                    foreach (var bone in row.Bones.Concat(new[] { row.StaticBatchRoot, row.ProbeAnchor, row.ProbeAnchorCached, row.RootBone })) Check<Transform>(bone);
                    Check<GameObject>(row.LightProbeVolume); Check<LODGroup>(row.LodGroup); Check<Sprite>(row.Sprite); Check<Sprite>(row.CachedSprite);
                    Check<Component>(row.Animator); Check<Component>(row.Cloth);
                    if (row.SpriteCache != null) PreflightSpriteCache(row.SpriteCache, target.SpriteCache, resolve);
                }
                var first = CopyRegistry(source.Updates); var second = CopyRegistry(before.Updates); first.MotionVectorFrame = second.MotionVectorFrame = 0;
                if (Json(first) != Json(second)) throw Error("renderer update callback/type registry differs between source and target");
                var sourceLists = source.Consumers.ToDictionary(ListKey, StringComparer.Ordinal); var targetLists = before.Consumers.ToDictionary(ListKey, StringComparer.Ordinal);
                foreach (var key in sourceLists.Keys.Concat(targetLists.Keys).Distinct())
                    if (!sourceLists.TryGetValue(key, out var wanted) || !targetLists.TryGetValue(key, out var existing) || Json(wanted) != Json(existing)) changedConsumers.Add(key);
                var execution = new HashSet<string>(source.Rows.Where(row => row.Execution).Select(row => row.Owner), StringComparer.Ordinal);
                foreach (var list in source.Consumers.Concat(before.Consumers).Where(list => changedConsumers.Contains(ListKey(list))))
                    if (list.Entries.Any(entry => entry.OtherRole.Length != 0 || execution.Contains(entry.Owner))) throw Error("changing a shared asset consumer list would overwrite a dependency outside the participating renderer rows");
                var preflightNodes = IndexConsumerNodes(directory,before.Rows); var preflightReferences = new SourceBindings(directory,bind);
                foreach (var list in source.Consumers) {
                    if (list.Kind == "Mesh") Check<Mesh>(list.Asset); else Check<Sprite>(list.Asset);
                    var current = ReadConsumers(Pointer(list.Asset),list.Kind,preflightNodes,preflightReferences);
                    if (targetLists.TryGetValue(ListKey(list), out var existing)) {
                        if (Json(current) != Json(existing.Entries)) throw Error("target asset consumer identity changed during preflight");
                    } else if (current.Length != 0) throw Error("source asset has target consumers outside the captured directory");
                }
                PreflightSpriteRegistry(source.Sprites, before.Sprites, directory, resolve);
            }
            private void Check<T>(string binding) where T : NativeObject
            { if (binding.Length != 0) _ = ResidentNativeAssetReference.ResolveTyped(binding,typeof(T),resolve); }
            private int Id(string binding) => binding.Length == 0 ? 0 : M.I32(M.Read(IntPtr.Add(Pointer(binding),8),4),0);
            private IntPtr Pointer(string binding) => ResidentNativeAssetReference.ResolveTyped(binding,typeof(NativeObject),resolve);
            private void Int(IntPtr owner, int offset, int value) => M.Write(IntPtr.Add(owner, offset), BitConverter.GetBytes(value));
            private void Ptr(IntPtr owner, int offset, IntPtr value) => M.Write(IntPtr.Add(owner, offset), BitConverter.GetBytes(value.ToInt64()));
            private byte[] Ids(string[] bindings) => bindings.SelectMany(binding => BitConverter.GetBytes(Id(binding))).ToArray();
            private void Array(IntPtr owner, int offset, byte[] bytes, int capacity, int stride, int alignment)
            {
                scope.Require(); admission(); var header = IntPtr.Add(owner, offset);
                // Every array here has trivial scalar/PPtr/native-pointer cells.
                // No element destructor is bypassed. reserve is Unity's exact
                // buffer reallocator, including shrink; size is reset first so
                // borrowed storage cannot copy past the new source capacity.
                M.Write(IntPtr.Add(header, 16), BitConverter.GetBytes(0UL)); M.Reserve(header, capacity, stride, alignment, admission);
                if ((M.U64(M.Read(header, 32), 24) >> 1) != (ulong)capacity) throw Error("renderer array allocator did not establish exact capacity");
                M.Write(M.Ptr(header), bytes); M.Write(IntPtr.Add(header, 16), BitConverter.GetBytes((ulong)(bytes.Length / stride)));
            }
            internal void Apply()
            {
                if (used) throw Error("renderer plan is single-use"); used = true; scope.Require(); admission();
                try {
                    RequireEqual(before, Capture(directory, bind));
                    var rowByKey = source.Rows.ToDictionary(row => row.Owner, StringComparer.Ordinal);
                    foreach (var list in before.Consumers.Where(list => changedConsumers.Contains(ListKey(list))))
                        foreach (var entry in list.Entries) Detach(Node(entry, rowByKey));
                    foreach (var row in source.Rows) {
                        if (row.Execution) continue; scope.Require(); admission();
                        var native = M.Owner(directory.Resolve(row.Owner));
                        if (row.Kind == "MeshFilter") { Int(native, 56, Id(row.Meshes[0])); continue; }
                        Array(native, 320, Ids(row.Materials), row.MaterialCapacity, 4, 4);
                        Int(native, 352, row.SceneHandle); Int(native, 392, Id(row.StaticBatchRoot)); Int(native, 396, row.LastLightProbe);
                        Int(native, 400, Id(row.ProbeAnchor)); Int(native, 404, Id(row.ProbeAnchorCached)); Int(native, 408, Id(row.LightProbeVolume)); Ptr(native, 416, Pointer(row.LodGroup));
                        if (row.Kind == "MeshRenderer") {
                            for (var slot = 0; slot < 3; slot++) { Int(native, 552 + slot * 4, Id(row.Meshes[slot])); Ptr(native, 448 + slot * 8, Pointer(row.CachedMeshes[slot])); }
                        } else if (row.Kind == "SpriteRenderer") {
                            Int(native, 488, Id(row.Sprite)); Ptr(native, 456, Pointer(row.CachedSprite));
                            ApplySecondarySheet(native, row.SecondaryProperties, resolve, bind, admission);
                            if (row.SpriteCache != null) ApplySpriteCache(M.Ptr(IntPtr.Add(native, 584)), row.SpriteCache, resolve, admission);
                        } else if (row.Kind == "SkinnedMeshRenderer") {
                            Int(native, 468, Id(row.Meshes[0])); Ptr(native, 472, Pointer(row.CachedMeshes[0]));
                            Array(native, 480, Ids(row.Bones), row.BoneCapacity, 4, 4); Int(native, 512, Id(row.RootBone));
                            Array(native, 544, row.OptimizedBones, row.OptimizedBoneCapacity, 2, 2); M.Write(IntPtr.Add(native, 576), BitConverter.GetBytes(row.OptimizedRootBone));
                            Ptr(native, 584, Pointer(row.Animator)); Array(native, 592, row.Weights, row.WeightCapacity, 4, 4);
                            Array(native, 680, row.SkinVertices, row.SkinVertexCapacity, 1, 16); Ptr(native, 720, Pointer(row.Cloth)); Int(native, 772, row.SkinHandle);
                        } else throw Error("unsupported renderer reached mutation");
                        var ranges = Ranges(row.Kind);
                        for (var index = 0; index < ranges.Length; index++) M.Write(IntPtr.Add(native, ranges[index].Offset), row.Scalars[index]);
                    }
                    foreach (var list in source.Consumers.Where(list => changedConsumers.Contains(ListKey(list)))) {
                        var sentinel = IntPtr.Add(Pointer(list.Asset), list.Kind == "Mesh" ? 368 : 280); var header = M.Read(sentinel, 16);
                        if (M.Ptr(header, 0) != sentinel || M.Ptr(header, 8) != sentinel) throw Error("asset consumer sentinel retained an unowned node");
                        var nodes = list.Entries.Select(entry => Node(entry, rowByKey)).ToArray();
                        for (var index = 0; index < nodes.Length; index++) {
                            Ptr(nodes[index], 0, index == 0 ? sentinel : nodes[index - 1]); Ptr(nodes[index], 8, index + 1 == nodes.Length ? sentinel : nodes[index + 1]);
                        }
                        Ptr(sentinel, 0, nodes.Length == 0 ? sentinel : nodes[nodes.Length - 1]); Ptr(sentinel, 8, nodes.Length == 0 ? sentinel : nodes[0]);
                    }
                    ApplyScene(source.Scene); Int(UpdatePointer(), 1096, source.Updates.MotionVectorFrame);
                    ApplySpriteRegistry(source.Sprites, resolve, admission);
                    applied = true; Verify();
                } catch (Exception error) { scope.Fault(error); throw; }
            }
            internal void Verify()
            { if (!applied) throw Error("renderer verification precedes application"); scope.Require(); admission(); RequireEqual(source, Capture(directory, bind)); admission(); }
            private IntPtr Node(Consumer entry, Dictionary<string, Row> rows)
                => IntPtr.Add(M.Owner(directory.Resolve(entry.Owner)), ConsumerOffsets(rows[entry.Owner].Kind)[entry.Slot]);
            private void Detach(IntPtr node)
            {
                scope.Require(); admission(); var header = M.Read(node, 16); var previous = M.Ptr(header, 0); var next = M.Ptr(header, 8);
                if (previous == IntPtr.Zero || next == IntPtr.Zero || M.Ptr(IntPtr.Add(previous, 8)) != node || M.Ptr(next) != node)
                    throw Error("target consumer is no longer linked reciprocally");
                Ptr(previous, 8, next); Ptr(next, 0, previous); Ptr(node, 0, IntPtr.Zero); Ptr(node, 8, IntPtr.Zero);
            }
            private void ApplyScene(SceneState scene)
            {
                var native = ScenePointer();
                byte[] Nodes(SceneNode[] nodes) {
                    var bytes = new byte[nodes.Length * 24];
                    for (var index = 0; index < nodes.Length; index++) {
                        var pointer = nodes[index].Owner.Length == 0 ? IntPtr.Zero : IntPtr.Add(Pointer(nodes[index].Owner), 56);
                        Buffer.BlockCopy(BitConverter.GetBytes(pointer.ToInt64()), 0, bytes, index * 24, 8); Buffer.BlockCopy(nodes[index].State, 0, bytes, index * 24 + 8, 16);
                    }
                    return bytes;
                }
                Array(native, 0, Nodes(scene.Intermediate), scene.Capacities[0], 24, 8); Array(native, 32, scene.IntermediateBounds, scene.Capacities[1], 24, 4);
                Int(native, 64, scene.ForbidIntermediate);
                var pending = new byte[scene.Pending.Length * 16];
                for (var index = 0; index < scene.Pending.Length; index++) {
                    Buffer.BlockCopy(BitConverter.GetBytes(Pointer(scene.Pending[index].Owner).ToInt64()), 0, pending, index * 16, 8);
                    pending[index * 16 + 8] = scene.Pending[index].AddBeforeRemove ? (byte)1 : (byte)0;
                }
                Array(native, 72, pending, scene.Capacities[2], 16, 8);
                Array(native, 104, scene.Removal.SelectMany(BitConverter.GetBytes).ToArray(), scene.Capacities[3], 4, 4);
                Array(native, 136, Nodes(scene.Nodes), scene.Capacities[4], 24, 8); Array(native, 168, scene.Bounds, scene.Capacities[5], 24, 4);
                Array(native, 200, scene.Visibility, scene.Capacities[6], 1, 1); Int(native, 248, scene.PreventChanges);
                M.Write(IntPtr.Add(native, 252), new[] { scene.RequestPvsRebuild ? (byte)1 : (byte)0 });
            }
        }
        internal static Plan Prepare(Data source, ResidentObjectDirectory directory, ResidentMutationScope scope,
            Func<string, NativeObject> resolve, Func<object, string?> bind, Action admitted) => new Plan(source, directory, scope, resolve, bind, admitted);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NativeUnary(IntPtr owner);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NativePair(IntPtr manager, IntPtr owner);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CalculateBounds(IntPtr output, IntPtr renderer, IntPtr mesh);
        internal sealed class MeshConsumerRebind
        {
            private readonly ResidentObjectDirectory directory;
            private readonly ResidentMutationScope scope;
            private readonly Mesh mesh;
            private readonly IntPtr meshPointer;
            private readonly Action admission;
            private readonly Row[] rows;
            private readonly Consumer[] consumers;
            private readonly Dictionary<string, ConsumerOwner> owners = new Dictionary<string, ConsumerOwner>(StringComparer.Ordinal);
            private readonly Dictionary<MeshFilter, int> filters = new Dictionary<MeshFilter, int>();
            private bool applied;
            private sealed class ConsumerOwner
            {
                internal Component Owner = null!;
                internal IntPtr Pointer;
                internal string Kind = "";
                internal byte[] Bindings = Array.Empty<byte>(), StaticBatch = Array.Empty<byte>();
            }
            internal MeshConsumerRebind(ResidentObjectDirectory directory, Mesh mesh, ResidentMutationScope scope, Action admitted)
            {
                this.directory = directory ?? throw new ArgumentNullException(nameof(directory)); this.mesh = mesh ?? throw new ArgumentNullException(nameof(mesh));
                this.scope = scope ?? throw new ArgumentNullException(nameof(scope)); admission = admitted ?? throw new ArgumentNullException(nameof(admitted));
                scope.Require(); admission(); meshPointer = M.Owner(mesh);
                rows = directory.NativeBindings.Where(pair => pair.Value is Renderer).Select(pair => new Row { Owner = pair.Key, Kind = Kind((Component)pair.Value) }).ToArray();
                consumers = ReadConsumers(mesh, "Mesh", directory, rows);
                foreach (var key in consumers.Select(value => value.Owner).Distinct()) {
                    var owner = (Component)directory.Resolve(key); var kind = Kind(owner);
                    if (kind != "MeshRenderer" && kind != "SkinnedMeshRenderer") throw Error("Mesh consumer has no native refresh adapter: " + owner.GetType().FullName);
                    if (!scope.Owns(owner) || directory.Ownership.IsExecution(owner)) throw Error("Mesh Canon would change an execution or unowned renderer");
                    var pointer = M.Owner(owner);
                    owners.Add(key, new ConsumerOwner { Owner = owner, Pointer = pointer, Kind = kind, Bindings = MeshBindings(pointer, kind),
                        StaticBatch = M.Read(IntPtr.Add(pointer, 248), 4) });
                    if (kind == "SkinnedMeshRenderer") RequireSkinHandle(pointer, false);
                }
                foreach (var filter in directory.NativeBindings.Values.OfType<MeshFilter>()) {
                    var id = M.I32(M.Read(IntPtr.Add(M.Owner(filter), 56), 4), 0);
                    if (id == mesh.GetInstanceID()) filters.Add(filter, id);
                }
                RequireAssetUsersEmpty(); RequireIdentity();
            }
            internal void Apply()
            {
                if (applied) throw Error("Mesh consumer refresh is single-use"); scope.Require(); admission(); RequireIdentity();
                try {
                    var updateMesh = Function<NativeUnary>(0x6d6a90, "40534883ec204c8b81c0010000488bd94881c1d80000004d");
                    var updateSkin = Function<NativePair>(0x6f1180, "4c8bc98b8a0403000083f9ff7439498b8180000000448bc1");
                    foreach (var item in owners.Values) {
                        scope.Require(); admission();
                        // These are the actual kDidModifyMesh receivers. Direct
                        // dispatch works even when Mesh's notification-suppression
                        // bit is set, and preserves PPtrs/static-batch state that
                        // null/restore setter cycles would otherwise overwrite.
                        if (item.Kind == "MeshRenderer") updateMesh(item.Pointer);
                        else updateSkin(SkinManager(), item.Pointer);
                    }
                    applied = true; Verify();
                } catch (Exception error) { scope.Fault(error); throw; }
            }
            internal void Verify()
            {
                if (!applied) throw Error("Mesh consumer verification precedes refresh"); scope.Require(); admission(); RequireIdentity();
                var calculate = Function<CalculateBounds>(0x6d2cd0, "4055488d6c24a94881eca00000006683bafa000000004c8b");
                var storage = Marshal.AllocHGlobal(24);
                try {
                    foreach (var item in owners.Values) {
                        scope.Require(); admission();
                        if (item.Kind == "MeshRenderer") {
                            var cached = M.Ptr(IntPtr.Add(item.Pointer, 448));
                            if (cached != IntPtr.Zero) {
                                calculate(storage, item.Pointer, cached);
                                if (!M.Read(storage, 24).SequenceEqual(M.Read(IntPtr.Add(item.Pointer, 216), 24)))
                                    throw Error("Mesh renderer bounds did not refresh from its actual bound geometry");
                            }
                        } else RequireSkinHandle(item.Pointer, true);
                    }
                } finally { Marshal.FreeHGlobal(storage); }
                admission();
            }
            private void RequireIdentity()
            {
                if (M.Owner(mesh) != meshPointer || Json(ReadConsumers(mesh, "Mesh", directory, rows)) != Json(consumers))
                    throw Error("Mesh consumer identities or list order changed during Canon");
                foreach (var item in owners.Values)
                    if (M.Owner(item.Owner) != item.Pointer || !MeshBindings(item.Pointer, item.Kind).SequenceEqual(item.Bindings)
                        || !M.Read(IntPtr.Add(item.Pointer, 248), 4).SequenceEqual(item.StaticBatch)) throw Error("Mesh refresh changed consumer bindings or retained static batch state");
                foreach (var pair in filters)
                    if (M.I32(M.Read(IntPtr.Add(M.Owner(pair.Key), 56), 4), 0) != pair.Value) throw Error("MeshFilter binding changed during Mesh Canon");
                RequireAssetUsersEmpty();
            }
            private void RequireAssetUsersEmpty()
            {
                var sentinel = IntPtr.Add(meshPointer, 384); var bytes = M.Read(sentinel, 16);
                if (M.Ptr(bytes, 0) != sentinel || M.Ptr(bytes, 8) != sentinel)
                    throw Error("Mesh has native asset users requiring a separate refresh adapter");
            }
            private static byte[] MeshBindings(IntPtr owner, string kind) => kind == "MeshRenderer"
                ? M.Read(IntPtr.Add(owner, 448), 24).Concat(M.Read(IntPtr.Add(owner, 552), 12)).ToArray()
                : M.Read(IntPtr.Add(owner, 468), 12).Concat(M.Read(IntPtr.Add(owner, 772), 4)).ToArray();
            private static IntPtr SkinManager()
            {
                var pointer = M.Ptr(IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, 0x1a17458));
                return pointer == IntPtr.Zero ? throw Error("SkinnedMeshRenderer manager is absent") : pointer;
            }
            private static void RequireSkinHandle(IntPtr owner, bool refreshed)
            {
                var handle = M.I32(M.Read(IntPtr.Add(owner, 772), 4), 0); if (handle == -1) return;
                var manager = SkinManager(); var bytes = M.Read(manager, 320); var capacity = M.I32(bytes, 0); var end = M.I32(bytes, 4);
                if (handle < 0 || capacity < 0 || capacity > MaximumSlots || end < 0 || end > capacity || handle >= end
                    || M.Ptr(bytes, 120) != manager || M.Ptr(bytes, 256) != manager || M.Ptr(bytes, 288) != manager
                    || M.Ptr(IntPtr.Add(M.Ptr(bytes, 264), handle * 8)) != owner) throw Error("skinned renderer handle is not owned by its actual manager");
                if (refreshed && (M.Ptr(IntPtr.Add(M.Ptr(bytes, 296), handle * 8)) != IntPtr.Zero
                    || (M.U32(M.Read(IntPtr.Add(M.Ptr(bytes, 128), handle / 32 * 4), 4), 0) & (1U << (handle & 31))) == 0))
                    throw Error("skinned renderer retained prepared geometry after Mesh refresh");
            }
        }
        internal static MeshConsumerRebind PrepareMeshConsumerRebind(ResidentObjectDirectory directory, Mesh mesh, ResidentMutationScope scope, Action admitted)
            => new MeshConsumerRebind(directory, mesh, scope, admitted);
        private static T Function<T>(int rva, string prefix) where T : Delegate
        {
            var pointer = IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, rva);
            if (BitConverter.ToString(M.Read(pointer, prefix.Length / 2)).Replace("-", "").ToLowerInvariant() != prefix) throw Error("renderer native receiver ABI differs");
            return (T)Marshal.GetDelegateForFunctionPointer(pointer, typeof(T));
        }
        internal static string[] RequiredCoverageGaps(Data data)
        {
            var gaps = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var row in data.Rows) {
                if (row.Kind == "MeshFilter") continue;
                if ((M.U32(row.Scalars[0],232) & ~NamedDrawingBits) != 0)
                    gaps.Add(row.Execution ? "execution-renderer-drawing-padding" : "renderer-drawing-padding-requires-common-canon");
                if (M.I32(row.Scalars[1], 0) != -1) gaps.Add("renderer-texture-streaming-registration");
                if (row.LodGroup.Length != 0) gaps.Add("renderer-lod-group-registration");
                if (row.Kind == "SpriteRenderer") {
                    if (row.SpriteDeformed) gaps.Add("sprite-deformed-buffer");
                    if (row.SpriteTilingFence) gaps.Add("sprite-tiling-job-fence");
                    if (!row.SpriteTilingJobDone) gaps.Add("sprite-tiling-job-active");
                    if (row.SecondaryProperties != null && row.SecondaryReferenceCount != 1) gaps.Add("sprite-secondary-sheet-sharing-requires-common-canon");
                    if (row.SecondaryProperties != null) foreach (var gap in row.SecondaryProperties.CoverageGaps) gaps.Add("sprite-secondary-sheet:" + gap);
                    if (row.SpriteCache != null) foreach (var gap in SpriteCacheGaps(row.SpriteCache)) gaps.Add(gap);
                }
                if (row.Kind == "SkinnedMeshRenderer") gaps.Add("skinned-renderer-manager-and-cache-restoration");
                if (row.Kind == "UnsupportedRenderer") gaps.Add("unsupported-native-renderer-type:" + row.Type.Name);
                if (row.Scalars.Length != 0 && row.Scalars[0].Length >= 196 && (M.U32(row.Scalars[0], 192) >> 12) != 0)
                    gaps.Add("renderer-sorting-group-registration");
                foreach (var value in row.OpaqueStates) gaps.Add(value);
            }
            if (data.Scene.HasGateState || data.Scene.HasUmbraTome) gaps.Add("renderer-umbra-and-gate-state");
            if (data.Scene.Intermediate.Length != 0) gaps.Add("intermediate-renderer-ownership");
            if (data.Sprites.ExternalOwners.Length != 0) gaps.Add("sprite-registry-nonresident-native-owner-state");
            return gaps.ToArray();
        }
        internal static void RequireCompleteCoverage(Data data)
        {
            Validate(data); var gaps = RequiredCoverageGaps(data).Concat(data.CoverageGaps).Distinct().ToArray();
            if (gaps.Length != 0) throw Error("native renderer coverage is incomplete: " + string.Join(",", gaps));
            foreach (var row in data.Rows) if (row.SecondaryProperties != null) ResidentShaderPropertySheet.RequireCompleteCoverage(row.SecondaryProperties);
        }
        internal static void RequireCanonicalizableCoverage(Data data)
        {
            Validate(data); var removable = new HashSet<string>(StringComparer.Ordinal) { "sprite-owned-geometry-gpu-buffers",
                "sprite-secondary-sheet-sharing-requires-common-canon", "sprite-render-cache-requires-common-canon", "renderer-drawing-padding-requires-common-canon" };
            if (data.Rows.Where(row => row.Kind == "SpriteRenderer").All(row => row.SpriteTilingJobDone)) removable.Add("sprite-tiling-job-fence");
            var gaps = RequiredCoverageGaps(data).Concat(data.CoverageGaps).Where(gap => !removable.Contains(gap)).Distinct(StringComparer.Ordinal).ToArray();
            if (gaps.Length != 0) throw Error("native renderer cannot safely enter common Canon: " + string.Join(",", gaps));
            foreach (var row in data.Rows) if (row.SecondaryProperties != null) ResidentShaderPropertySheet.RequireCompleteCoverage(row.SecondaryProperties);
        }
        internal static void RequireCanonicalState(Data data)
        {
            RequireCompleteCoverage(data);
            foreach (var row in data.Rows.Where(row => row.Kind == "SpriteRenderer" && !row.Execution)) {
                if (row.SpriteTilingFence || !row.SpriteTilingJobDone || row.SecondaryProperties != null && row.SecondaryReferenceCount != 1)
                    throw Error("Sprite S0 tiling or unique property-sheet state differs");
                if (row.SpriteCache != null && (!row.SpriteCache.PreparePending || !row.SpriteCache.VertexFormatCleared
                    || row.SpriteCache.VertexFormat.Length != 0 || row.SpriteCache.GeometryBuffers.Any(value => value)))
                    throw Error("Sprite S0 cache storage is not cold");
            }
        }
        internal static void Validate(Data data)
        {
            if (data == null || data.Version != 2 || data.Rows == null || data.Rows.Length > MaximumSlots || data.Scene == null || data.Updates == null || data.Consumers == null || data.Sprites == null || data.CoverageGaps == null)
                throw Error("renderer snapshot shape differs");
            var seen = new HashSet<string>(StringComparer.Ordinal); string? previous = null;
            foreach (var row in data.Rows) {
                if (row == null || !Text(row.Owner, false) || !seen.Add(row.Owner) || previous != null && StringComparer.Ordinal.Compare(previous, row.Owner) >= 0
                    || row.Type == null || !new[] { "MeshFilter", "MeshRenderer", "SpriteRenderer", "SkinnedMeshRenderer", "UnsupportedRenderer" }.Contains(row.Kind))
                    throw Error("renderer owner/type directory differs");
                previous = row.Owner; row.Type.Validate();
                if ((row.SecondaryProperties == null) != (row.SecondaryReferenceCount == 0) || row.SecondaryReferenceCount < 0 || row.SecondaryReferenceCount > MaximumSlots
                    || row.Kind != "SpriteRenderer" && row.SecondaryProperties != null) throw Error("Sprite secondary property-sheet retain shape differs");
                if (row.Kind != "UnsupportedRenderer" && row.Type.Name != "UnityEngine." + row.Kind) throw Error("native renderer type and layout differ");
                var ranges = Ranges(row.Kind);
                if (row.Scalars == null || row.Scalars.Length != ranges.Length || row.Scalars.Where((bytes, index) => bytes == null || bytes.Length != ranges[index].Count).Any())
                    throw Error("renderer scalar layout differs");
                foreach (var values in new[] { row.Materials, row.Meshes, row.CachedMeshes, row.Bones, row.OpaqueStates })
                    if (values == null || values.Length > MaximumSlots || values.Any(value => !Text(value))) throw Error("renderer binding array differs");
                foreach (var value in new[] { row.StaticBatchRoot, row.ProbeAnchor, row.ProbeAnchorCached, row.LightProbeVolume, row.LodGroup, row.Sprite, row.CachedSprite,
                    row.RootBone, row.Animator, row.Cloth }) if (!Text(value)) throw Error("renderer binding text differs");
                if (row.ConsumerLinked == null || row.MaterialCapacity < row.Materials.Length || row.MaterialCapacity > MaximumSlots
                    || row.BoneCapacity < row.Bones.Length || row.BoneCapacity > MaximumSlots) throw Error("renderer capacity differs");
                var meshCount = row.Kind == "MeshRenderer" ? 3 : row.Kind == "MeshFilter" || row.Kind == "SkinnedMeshRenderer" ? 1 : 0;
                if (row.Meshes.Length != meshCount || row.CachedMeshes.Length != (row.Kind == "MeshFilter" ? 0 : meshCount)
                    || row.ConsumerLinked.Length != (row.Kind == "MeshRenderer" ? 3 : row.Kind == "SpriteRenderer" || row.Kind == "SkinnedMeshRenderer" ? 1 : 0))
                    throw Error("renderer mesh/consumer layout differs");
                Bytes(row.Weights, row.WeightCapacity, 4); Bytes(row.OptimizedBones, row.OptimizedBoneCapacity, 2); Bytes(row.SkinVertices, row.SkinVertexCapacity, 1);
                if (row.SecondaryProperties != null) ResidentShaderPropertySheet.Validate(row.SecondaryProperties);
                if (row.SpriteNineSlice != (row.SpriteCache != null)) throw Error("Sprite renderer cache presence differs");
                if (row.SpriteCache != null) ValidateSpriteCache(row.SpriteCache);
                if (row.Kind != "SpriteRenderer" && (row.SpriteNineSlice || row.SpriteDeformed || row.SpriteTilingFence || row.SecondaryProperties != null
                    || row.Sprite.Length != 0 || row.CachedSprite.Length != 0)) throw Error("non-Sprite renderer contains a Sprite cache domain");
                if (row.Kind == "MeshFilter" && (row.Materials.Length != 0 || row.Scalars.Length != 0 || row.SceneHandle != -1)) throw Error("MeshFilter contains a renderer domain");
                if (row.Kind != "MeshFilter") {
                    for (var index = 4; index < 7; index++) Bool(row.Scalars[1][index]);
                }
            }
            ValidateScene(data.Scene, seen);
            ValidateUpdates(data.Updates); ValidateConsumers(data);
            ValidateSpriteRegistry(data);
            if (data.CoverageGaps.Any(value => !Text(value, false))) throw Error("renderer gap descriptor differs");
        }
        private static void ValidateScene(SceneState scene, HashSet<string> owners)
        {
            if (scene.Nodes == null || scene.Intermediate == null || scene.Pending == null || scene.Removal == null || scene.Capacities == null || scene.Capacities.Length != 7
                || scene.Nodes.Length > MaximumSlots || scene.Intermediate.Length > MaximumSlots || scene.Pending.Length > MaximumSlots || scene.Removal.Length > MaximumSlots
                || scene.Capacities.Any(value => value < 0 || value > MaximumSlots) || scene.PreventChanges < 0 || scene.ForbidIntermediate < 0) throw Error("renderer scene shape differs");
            foreach (var node in scene.Nodes.Concat(scene.Intermediate))
                if (node == null || !Text(node.Owner) || node.Owner.Length != 0 && !owners.Contains(node.Owner) || node.State == null || node.State.Length != 16)
                    throw Error("renderer scene node differs");
            foreach (var node in scene.Pending) if (node == null || !owners.Contains(node.Owner)) throw Error("pending renderer is absent from directory");
            Bytes(scene.IntermediateBounds, scene.Capacities[1], 24); Bytes(scene.Bounds, scene.Capacities[5], 24); Bytes(scene.Visibility, scene.Capacities[6], 1);
            if (scene.Intermediate.Length > scene.Capacities[0] || scene.Pending.Length > scene.Capacities[2] || scene.Removal.Length > scene.Capacities[3]
                || scene.Nodes.Length > scene.Capacities[4] || scene.Bounds.Length != scene.Nodes.Length * 24 || scene.IntermediateBounds.Length != scene.Intermediate.Length * 24
                || scene.Visibility.Length != scene.Nodes.Length) throw Error("renderer scene parallel arrays differ");
            foreach (var index in scene.Removal) if (index < 0 || index >= scene.Nodes.Length) throw Error("pending renderer removal index differs");
        }
        private static void ValidateUpdates(UpdateRegistry registry)
        {
            if (registry.Systems == null || registry.Systems.Length != 17) throw Error("renderer update system count differs from pinned ABI");
            foreach (var system in registry.Systems)
                if (system == null || system.ChangeSystem < -1 || system.ChangeSystem >= 64 || system.FunctionRvas == null || system.FunctionRvas.Length != 5
                    || system.FunctionRvas.Any(value => value < 0 || value >= 32 * 1024 * 1024)) throw Error("renderer update callback schema differs");
        }
        private static void ValidateConsumers(Data data)
        {
            if (data.Consumers.Length > MaximumSlots) throw Error("asset consumer directory exceeds bounds");
            var rows = data.Rows.ToDictionary(row => row.Owner, StringComparer.Ordinal); var linked = new HashSet<string>(StringComparer.Ordinal);
            var expectedAssets = new HashSet<string>(data.Rows.SelectMany(row => row.CachedMeshes.Where(key => key.Length != 0).Select(key => "Mesh:" + key))
                .Concat(data.Rows.Where(row => row.CachedSprite.Length != 0).Select(row => "Sprite:" + row.CachedSprite)), StringComparer.Ordinal);
            string? previous = null;
            foreach (var list in data.Consumers) {
                if (list == null || !Text(list.Asset, false) || list.Kind != "Mesh" && list.Kind != "Sprite" || list.Entries == null || list.Entries.Length > MaximumSlots)
                    throw Error("asset consumer list shape differs");
                var key = ListKey(list);
                if (!expectedAssets.Remove(key) || previous != null && StringComparer.Ordinal.Compare(previous, key) >= 0) throw Error("asset consumer lists are duplicated or unsorted");
                previous = key;
                foreach (var entry in list.Entries) {
                    if (entry != null && !string.IsNullOrEmpty(entry.OtherRole)) {
                        var mesh = list.Kind == "Mesh";
                        var role = entry.OtherRole;
                        if (!Text(entry.Owner,false) || entry.Slot < 0 || entry.Slot >= (role == "MeshRenderer" ? 3 : 1)
                            || !(mesh ? role == "MeshRenderer" || role == "SkinnedMeshRenderer" || role == "MeshCollider" || role == "ParticleSystem.Shape"
                                : role == "SpriteRenderer" || role == "SpriteMask")
                            || rows.TryGetValue(entry.Owner,out var externalRow) && externalRow.Kind != "UnsupportedRenderer"
                            || !linked.Add("other/" + role + "/" + entry.Owner + ":" + entry.Slot)) throw Error("external asset consumer role, slot or ownership differs");
                        continue;
                    }
                    if (entry == null || entry.OtherRole == null || !rows.TryGetValue(entry.Owner, out var row) || entry.Slot < 0 || entry.Slot >= row.ConsumerLinked.Length
                        || !row.ConsumerLinked[entry.Slot] || !linked.Add(entry.Owner + ":" + entry.Slot)) throw Error("asset consumer owner/slot is absent or duplicated");
                    if (list.Kind == "Sprite" ? row.Kind != "SpriteRenderer" || row.CachedSprite != list.Asset
                        : row.Kind == "SpriteRenderer" || row.CachedMeshes[entry.Slot] != list.Asset) throw Error("asset consumer cached binding differs");
                }
            }
            if (expectedAssets.Count != 0) throw Error("an asset consumer list is missing");
            foreach (var row in data.Rows)
                for (var slot = 0; slot < row.ConsumerLinked.Length; slot++)
                    if (row.ConsumerLinked[slot] != linked.Contains(row.Owner + ":" + slot)) throw Error("component consumer registration disagrees with asset list");
            foreach (var row in data.Rows.Where(row => row.Kind != "MeshFilter"))
                if (row.SceneHandle < -1 || row.SceneHandle >= data.Scene.Nodes.Length || row.SceneHandle >= 0 && data.Scene.Nodes[row.SceneHandle].Owner != row.Owner)
                    throw Error("component scene handle disagrees with renderer scene registration");
        }
        private static string ListKey(ConsumerList list) => list.Kind + ":" + list.Asset;
        private static UpdateRegistry CopyRegistry(UpdateRegistry value) => Newtonsoft.Json.JsonConvert.DeserializeObject<UpdateRegistry>(Json(value))
            ?? throw Error("renderer update snapshot copy is absent");
        private static void Bytes(byte[] bytes, int capacity, int stride)
        { if (bytes == null || capacity < 0 || capacity > MaximumSlots || bytes.Length % stride != 0 || bytes.Length / stride > capacity) throw Error("renderer native array shape differs"); }
        private static bool Text(string value, bool empty = true) => value != null && value.Length <= 262144 && (empty || value.Length != 0);
        private static bool Bool(byte value) => value <= 1 ? value != 0 : throw Error("native bool is not binary");
        internal static void RequireEqual(Data expected, Data actual)
        { Validate(expected); Validate(actual); if (Json(expected) != Json(actual)) throw Error("renderer native readback differs"); }
        private static string Json(object value) => Newtonsoft.Json.JsonConvert.SerializeObject(value);
        private static Data Copy(Data value) => Newtonsoft.Json.JsonConvert.DeserializeObject<Data>(Json(value)) ?? throw Error("renderer snapshot copy is absent");
        private static InvalidDataException Error(string reason) => new InvalidDataException("Resident renderer state: " + reason);
    }
}
