using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentCameraCanvasState
    {
        internal sealed class CanvasRow
        {
            public string Owner = "", Camera = "", Parent = "";
            public ResidentObjectDirectory.TypeRecord Type = new ResidentObjectDirectory.TypeRecord();
            public bool Execution;
            public byte[][] Scalars = Array.Empty<byte[]>();
            public BindingList Handles = new BindingList(), Nested = new BindingList();
            public Instruction[] Instructions = Array.Empty<Instruction>();
            public int InstructionCapacity, BatchCapacity, SubBatchCapacity, AssetUsers;
            public Batch[] Batches = Array.Empty<Batch>();
            public SubBatch[] SubBatches = Array.Empty<SubBatch>();
            public byte[][] BatchScalars = Array.Empty<byte[]>();
            public bool BatchBuffers, BatchFence, GeometryFence;
            public bool BatchJobDone = true;
        }
        internal sealed class CanvasRendererRow
        {
            public string Owner = "", Parent = "", Shared = "";
            public ResidentObjectDirectory.TypeRecord Type = new ResidentObjectDirectory.TypeRecord();
            public bool Execution;
            public byte[][] Scalars = Array.Empty<byte[]>();
            public BindingList PushMaterials = new BindingList(), PopMaterials = new BindingList();
            public TextureBinding Texture = new TextureBinding(), AlphaTexture = new TextureBinding();
            public Handle Push = new Handle(), Pop = new Handle();
        }
        internal sealed class Handle
        {
            public string Canvas = "";
            public int InstructionIndex = -1, HandleIndex = -1, AbsoluteIndex = -1;
        }
        internal sealed class Instruction
        {
            public string[] Materials = Array.Empty<string>();
            public string Shared = "";
            public TextureBinding Texture = new TextureBinding(), AlphaTexture = new TextureBinding();
            public byte[][] Scalars = Array.Empty<byte[]>();
        }
        internal sealed class Batch
        {
            public bool Injection;
            public string Material = "";
            public int SubBatchStart, SubBatchCount;
        }
        internal sealed class SubBatch
        {
            public ResidentShaderPropertySheet.Data? Properties;
            public byte[] Range = Array.Empty<byte>();
        }
        internal sealed class DirtyGroup
        {
            public string Canvas = "";
            public string[] Renderers = Array.Empty<string>();
        }
        internal sealed class CanvasRegistry
        {
            public BindingList Roots = new BindingList();
            public int EmptyDirtyBuckets;
            public DirtyGroup[] Dirty = Array.Empty<DirtyGroup>();
            public Callback[] Callbacks = Array.Empty<Callback>();
            public bool CallbackDispatch, CallbackChanged, GuiZTestInitialized, VrOverlayWarning;
            public int[] TransformSystems = new int[3];
        }
        private static readonly Range[] CanvasRanges = { new Range(56, 2), new Range(64, 4), new Range(72, 1), new Range(76, 64),
            new Range(144, 64), new Range(272, 10), new Range(456, 15), new Range(472, 37), new Range(512, 5), new Range(520, 5), new Range(528, 6), new Range(536, 6) };
        private static readonly Range[] CanvasRendererRanges = { new Range(56, 16), new Range(80, 24), new Range(176, 17), new Range(194, 5), new Range(200, 25), new Range(240, 9) };
        private static readonly Range[] InstructionRanges = { new Range(32, 452), new Range(492, 16), new Range(520, 116), new Range(640, 79) };
        private static readonly Range[] BatchRanges = { new Range(352, 28), new Range(392, 32) };
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SyncVertex(IntPtr fence, IntPtr instruction, IntPtr mesh, IntPtr materials);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr MapSet(IntPtr map, IntPtr key);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void InsertNode(IntPtr set, IntPtr location, IntPtr node);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DirtyRemove(IntPtr manager, IntPtr canvas, IntPtr renderer);
        private static IntPtr CanvasManager()
        {
            Function<Unary>(0x225410, "488b0501287901c3");
            var pointer = M.Ptr(IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, 0x19b7c18));
            return pointer != IntPtr.Zero ? pointer : throw Error("native CanvasManager is absent");
        }
        private static CanvasRendererRow CaptureCanvasRenderer(Reader reader, string key, CanvasRenderer owner)
        {
            var native = M.Owner(owner); var bytes = reader.Read(native, 304);
            BindingList Materials(int offset) => BindList(Dynamic(reader, IntPtr.Add(native, offset), 4), 4, (value, at) => reader.Id<Material>(M.I32(value, at)));
            return new CanvasRendererRow { Owner = key, Type = ResidentObjectDirectory.TypeRecord.Capture(owner.GetType()), Execution = reader.Directory.Ownership.IsExecution(owner),
                Scalars = CaptureRanges(bytes, CanvasRendererRanges), Parent = reader.Pointer<Canvas>(M.Ptr(bytes, 232)),
                Shared = reader.Shared(M.Ptr(bytes, 72), key), PushMaterials = Materials(104), PopMaterials = Materials(136),
                Texture = reader.Texture(M.U32(bytes, 168)), AlphaTexture = reader.Texture(M.U32(bytes, 172)),
                Push = CaptureHandle(reader, bytes, 256), Pop = CaptureHandle(reader, bytes, 280) };
        }
        private static Handle CaptureHandle(Reader reader, byte[] bytes, int offset)
        {
            var canvas = M.Ptr(bytes, offset);
            return new Handle { Canvas = canvas == IntPtr.Zero ? "" : reader.CanvasData.TryGetValue(canvas, out var owner) ? owner : "@unsupported",
                InstructionIndex = M.I32(bytes, offset + 8), HandleIndex = M.I32(bytes, offset + 12), AbsoluteIndex = M.I32(bytes, offset + 16) };
        }
        private static CanvasRow CaptureCanvas(Reader reader, string key, Canvas owner)
        {
            var native = M.Owner(owner); var bytes = reader.Read(native, 584);
            var row = new CanvasRow { Owner = key, Type = ResidentObjectDirectory.TypeRecord.Capture(owner.GetType()), Execution = reader.Directory.Ownership.IsExecution(owner),
                Scalars = CaptureRanges(bytes, CanvasRanges), Camera = reader.Id<Camera>(M.I32(bytes, 68)), Parent = reader.Pointer<Canvas>(M.Ptr(bytes, 448)),
                Handles = BindList(Dynamic(reader, IntPtr.Add(native, 208), 8), 8, (values, at) => {
                    var pointer = M.Ptr(values, at); return pointer == IntPtr.Zero ? "" : reader.Handles.TryGetValue(pointer, out var handle) ? handle : "@unsupported";
                }), Nested = BindList(Vector(reader, IntPtr.Add(native, 544), 8), 8, (values, at) => reader.Pointer<Canvas>(M.Ptr(values, at))),
                BatchScalars = CaptureRanges(bytes, BatchRanges), BatchBuffers = M.Ptr(bytes, 384) != IntPtr.Zero,
                BatchFence = M.Slice(bytes, 424, 12).Any(value => value != 0), GeometryFence = (M.U32(bytes, 440) & 0x7fffffffU) != 0x7fffffffU,
                BatchJobDone = Function<FenceDone>(0x725ac0, "40534883ec30488bd9e86217000084c0742648833b007420")(IntPtr.Add(native, 424)),
                AssetUsers = CountList(reader, IntPtr.Add(native, 568)) };
            var instructions = Dynamic(reader, IntPtr.Add(native, 240), 720); row.InstructionCapacity = instructions.Capacity;
            row.Instructions = Enumerable.Range(0, instructions.Bytes.Length / 720).Select(index => {
                var values = M.Slice(instructions.Bytes, index * 720, 720);
                return new Instruction { Materials = Enumerable.Range(0, 8).Select(slot => reader.Id<Material>(M.I32(values, slot * 4))).ToArray(),
                    Shared = reader.Shared(M.Ptr(values, 512), key + "/instruction/" + index), Scalars = CaptureRanges(values, InstructionRanges),
                    Texture = reader.Texture(M.U32(values, 484)), AlphaTexture = reader.Texture(M.U32(values, 488)) };
            }).ToArray();
            var batches = Dynamic(reader, IntPtr.Add(native, 288), 16); row.BatchCapacity = batches.Capacity;
            row.Batches = Enumerable.Range(0, batches.Bytes.Length / 16).Select(index => {
                var at = index * 16; return new Batch { Injection = Bool(batches.Bytes[at]), Material = reader.Id<Material>(M.I32(batches.Bytes, at + 4)),
                    SubBatchStart = M.I32(batches.Bytes, at + 8), SubBatchCount = M.I32(batches.Bytes, at + 12) };
            }).ToArray();
            var subs = Dynamic(reader, IntPtr.Add(native, 320), 40); row.SubBatchCapacity = subs.Capacity;
            row.SubBatches = Enumerable.Range(0, subs.Bytes.Length / 40).Select(index => {
                var pointer = M.Ptr(subs.Bytes, index * 40);
                return new SubBatch { Properties = pointer == IntPtr.Zero ? null : ResidentShaderPropertySheet.Capture(pointer, reader.Bind), Range = M.Slice(subs.Bytes, index * 40 + 8, 32) };
            }).ToArray();
            return row;
        }
        private static int CountList(Reader reader, IntPtr head)
        {
            var header = reader.Read(head, 16); var current = M.Ptr(header, 8); var previous = head; var seen = new HashSet<IntPtr>();
            while (current != head) {
                if (current == IntPtr.Zero || !seen.Add(current) || seen.Count > Maximum) throw Error("Canvas asset-user list is invalid");
                var bytes = reader.Read(current, 16); if (M.Ptr(bytes, 0) != previous) throw Error("Canvas asset-user predecessor differs");
                previous = current; current = M.Ptr(bytes, 8);
            }
            if (M.Ptr(header, 0) != previous) throw Error("Canvas asset-user tail differs"); return seen.Count;
        }
        private static List<byte[]> TreeNodes(Reader reader, IntPtr tree, int size, bool signedIntegerKey = false)
            => TreeNodes(reader, tree, size, out _, signedIntegerKey);

        private static List<byte[]> TreeNodes(Reader reader, IntPtr tree, int size, out List<IntPtr> addresses, bool signedIntegerKey = false)
        {
            var header = reader.Read(tree, 16); var head = M.Ptr(header, 0); var count = M.U64(header, 8);
            if (count > Maximum) throw Error("UI native tree exceeds bounds");
            var sentinel = reader.Read(head, 32); if (sentinel[25] != 1) throw Error("UI native tree sentinel differs");
            var seen = new HashSet<IntPtr>(); var result = new List<byte[]>(); var nodes = new List<IntPtr>();
            void Walk(IntPtr node, IntPtr parent, int depth) {
                if (node == head) return;
                if (node == IntPtr.Zero || depth > 128 || !seen.Add(node) || seen.Count > (int)count) throw Error("UI native tree links differ");
                var bytes = reader.Read(node, size);
                if (bytes[25] != 0 || bytes[24] > 1 || M.Ptr(bytes, 8) != parent) throw Error("UI native tree parent/color differs");
                Walk(M.Ptr(bytes, 0), node, depth + 1); result.Add(bytes); nodes.Add(node); Walk(M.Ptr(bytes, 16), node, depth + 1);
            }
            Walk(M.Ptr(sentinel, 8), head, 0);
            if (seen.Count != (int)count || M.Ptr(sentinel, 0) != (nodes.Count == 0 ? head : nodes[0]) || M.Ptr(sentinel, 16) != (nodes.Count == 0 ? head : nodes[nodes.Count - 1]))
                throw Error("UI native tree count/extrema differ");
            for (var index = 1; index < result.Count; index++)
                if (signedIntegerKey ? M.I32(result[index - 1], 32) >= M.I32(result[index], 32)
                    : unchecked((ulong)M.Ptr(result[index - 1], 32).ToInt64()) >= unchecked((ulong)M.Ptr(result[index], 32).ToInt64())) throw Error("native tree key order differs");
            addresses = nodes;
            return result;
        }
        private static DirtyGroup[] CaptureDirty(Reader reader, out List<IntPtr> emptyKeys)
        {
            // operator[] creates map entries even on lookup. Inspect the
            // mapped set before treating the opaque comparison key as an Object.
            var groups = new List<DirtyGroup>();
            emptyKeys = new List<IntPtr>();
            var entries = TreeNodes(reader, IntPtr.Add(CanvasManager(), 32), 56, out var nodes);
            for (var index = 0; index < nodes.Count; index++) {
                var canvas = M.Ptr(entries[index], 32);
                if (canvas == IntPtr.Zero) throw Error("Canvas dirty map contains a null key");
                var queued = TreeNodes(reader, IntPtr.Add(nodes[index], 40), 40);
                if (queued.Count == 0) {
                    emptyKeys.Add(canvas);
                    continue;
                }
                // UpdateDirtyRenderers dispatches this inner set in native
                // traversal order. The order of callbacks is portable D.
                var owner = reader.DirtyOwner(canvas, ResidentLoadedAssetIndex.CanvasClass, queued.Count);
                var renderers = queued.Select(entry => reader.DirtyOwner(M.Ptr(entry, 32),
                    ResidentLoadedAssetIndex.CanvasRendererClass, queued.Count)).ToArray();
                groups.Add(new DirtyGroup { Canvas = owner, Renderers = renderers });
            }
            ResidentFailureEvidence.Mark("canvas dirty map: pending groups=" + groups.Count + ", empty buckets=" + emptyKeys.Count);
            return groups.OrderBy(group => group.Canvas, StringComparer.Ordinal).ToArray();
        }

        private static void RemoveEmptyDirtyBuckets(ResidentObjectDirectory directory, Func<object,string?> bind, Action admitted)
        {
            var reader = new Reader(directory,bind);
            var pending = CaptureDirty(reader,out var empty);
            reader.RequireFrozen();
            if (empty.Count == 0) return;
            // RemoveAllDirtyRenderers compares the raw key and erases the map
            // node and empty set sentinel. It never dereferences the Canvas.
            var remove = Function<Binary>(0x226100, "4885d2741b48895424104883ec284883c120488d542438e8");
            var manager = CanvasManager();
            foreach (var key in empty) {
                admitted();
                remove(manager,key);
            }
            var check = new Reader(directory,bind);
            var after = CaptureDirty(check,out var remaining);
            check.RequireFrozen();
            if (remaining.Count != 0 || Json(pending) != Json(after)) throw Error("normalizing empty Canvas buckets changed pending callbacks");
        }
        private static CanvasRegistry CaptureCanvasRegistry(Reader reader)
        {
            var manager = CanvasManager(); var bytes = reader.Read(manager, 3152); var count = M.U32(bytes, 3120);
            if (count > 128) throw Error("Canvas geometry callback array exceeds its native storage");
            var systems = reader.Read(IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, 0x194dccc), 12);
            return new CanvasRegistry { Roots = BindList(Vector(reader, IntPtr.Add(manager, 8), 8), 8, (values, at) => reader.Pointer<Canvas>(M.Ptr(values, at))),
                Dirty = CaptureDirty(reader,out var emptyDirty), EmptyDirtyBuckets = emptyDirty.Count, Callbacks = Enumerable.Range(0, (int)count).Select(index => reader.Callback(bytes, 48 + index * 24)).ToArray(),
                CallbackDispatch = M.Ptr(bytes, 3128) != IntPtr.Zero, CallbackChanged = Bool(bytes[3136]), GuiZTestInitialized = Bool(bytes[3144]), VrOverlayWarning = Bool(bytes[3145]),
                TransformSystems = Enumerable.Range(0, 3).Select(index => M.I32(systems, index * 4)).ToArray() };
        }
        private static void ValidateCanvas(CanvasRow row)
        {
            if (row == null || !Text(row.Owner, false) || !Text(row.Camera) || !Text(row.Parent) || row.Type == null || row.Type.Name != "UnityEngine.Canvas") throw Error("Canvas owner/type differs");
            row.Type.Validate(); ValidateRanges(row.Scalars, CanvasRanges); ValidateRanges(row.BatchScalars, BatchRanges); ValidateList(row.Handles); ValidateList(row.Nested, false);
            if (row.Instructions == null || row.Batches == null || row.SubBatches == null || row.InstructionCapacity < row.Instructions.Length || row.InstructionCapacity > Maximum
                || row.BatchCapacity < row.Batches.Length || row.BatchCapacity > Maximum || row.SubBatchCapacity < row.SubBatches.Length || row.SubBatchCapacity > Maximum
                || row.AssetUsers < 0 || row.AssetUsers > Maximum || row.Scalars[0].Any(value => value > 1) || row.Scalars[2][0] > 1) throw Error("Canvas native storage shape differs");
            foreach (var value in row.Instructions) {
                if (value == null || value.Materials == null || value.Materials.Length != 8 || value.Materials.Any(item => !Text(item)) || !Text(value.Shared)) throw Error("Canvas instruction bindings differ");
                ValidateTexture(value.Texture); ValidateTexture(value.AlphaTexture); ValidateRanges(value.Scalars, InstructionRanges);
                var count = M.I32(value.Scalars[0], 448); if (count < 0 || count > 8) throw Error("Canvas instruction subset count differs");
            }
            foreach (var batch in row.Batches) if (batch == null || !Text(batch.Material) || batch.SubBatchStart < 0 || batch.SubBatchCount < 0
                || (long)batch.SubBatchStart + batch.SubBatchCount > row.SubBatches.Length) throw Error("Canvas batch range differs");
            foreach (var batch in row.SubBatches) {
                if (batch == null || batch.Range == null || batch.Range.Length != 32) throw Error("Canvas sub-batch range differs");
                if (batch.Properties != null) ResidentShaderPropertySheet.Validate(batch.Properties);
            }
        }
        private static void ValidateCanvasRenderer(CanvasRendererRow row)
        {
            if (row == null || !Text(row.Owner, false) || !Text(row.Parent) || !Text(row.Shared) || row.Type == null || row.Type.Name != "UnityEngine.CanvasRenderer") throw Error("CanvasRenderer owner/type differs");
            row.Type.Validate(); ValidateRanges(row.Scalars, CanvasRendererRanges); ValidateList(row.PushMaterials); ValidateList(row.PopMaterials);
            ValidateTexture(row.Texture); ValidateTexture(row.AlphaTexture);
            foreach (var handle in new[] { row.Push, row.Pop }) {
                if (handle == null || !Text(handle.Canvas) || handle.InstructionIndex < -1 || handle.InstructionIndex >= Maximum || handle.HandleIndex < -1 || handle.HandleIndex >= Maximum
                    || handle.AbsoluteIndex < -1 || handle.AbsoluteIndex >= Maximum || handle.Canvas.Length == 0 && (handle.InstructionIndex != -1 || handle.HandleIndex != -1 || handle.AbsoluteIndex != -1))
                    throw Error("CanvasRenderer handle schema differs");
            }
            if (row.Scalars[2][16] > 1 || row.Scalars[3].Skip(2).Any(value => value > 1) || row.Scalars[4][24] > 1 || row.Scalars[5][8] > 1)
                throw Error("CanvasRenderer native boolean differs");
        }
        private static void ValidateCanvasRegistry(Data data)
        {
            var value = data.CanvasManager;
            if (value == null || value.Dirty == null || value.EmptyDirtyBuckets < 0 || (long)value.Dirty.Length + value.EmptyDirtyBuckets > Maximum || value.Callbacks == null || value.Callbacks.Length > 128
                || value.TransformSystems == null || value.TransformSystems.Length != 3 || value.TransformSystems.Any(index => index < 0)) throw Error("Canvas registry schema differs");
            ValidateList(value.Roots, false); foreach (var callback in value.Callbacks) ValidateCallback(callback);
            var canvases = data.Canvases.ToDictionary(row => row.Owner, StringComparer.Ordinal); var renderers = data.Renderers.ToDictionary(row => row.Owner, StringComparer.Ordinal);
            if (value.Roots.Owners.Distinct(StringComparer.Ordinal).Count() != value.Roots.Owners.Length || value.Roots.Owners.Any(key => !canvases.ContainsKey(key))) throw Error("Canvas root registry is outside the directory");
            var dirty = new HashSet<string>(StringComparer.Ordinal); string? last = null;
            foreach (var group in value.Dirty) {
                if (group == null || !canvases.ContainsKey(group.Canvas) || !dirty.Add(group.Canvas) || group.Renderers == null || group.Renderers.Length == 0 || group.Renderers.Length > Maximum
                    || group.Renderers.Any(key => !renderers.ContainsKey(key)) || group.Renderers.Distinct(StringComparer.Ordinal).Count() != group.Renderers.Length
                    || last != null && StringComparer.Ordinal.Compare(last, group.Canvas) >= 0)
                    throw Error("Canvas dirty renderer map differs");
                last = group.Canvas;
            }
            foreach (var row in data.Renderers) foreach (var item in new[] { Tuple.Create(row.Push, row.Owner + "/push"), Tuple.Create(row.Pop, row.Owner + "/pop") }) {
                var handle = item.Item1; if (handle.Canvas.Length == 0 || handle.Canvas == "@unsupported") continue;
                if (!canvases.TryGetValue(handle.Canvas, out var canvas) || handle.HandleIndex < 0 || handle.HandleIndex >= canvas.Handles.Owners.Length
                    || canvas.Handles.Owners[handle.HandleIndex] != item.Item2 || handle.InstructionIndex < 0 || handle.InstructionIndex >= canvas.Instructions.Length)
                    throw Error("Canvas handle does not point back to its actual instruction owner");
            }
            foreach (var canvas in data.Canvases) {
                if (canvas.Parent.Length != 0 && !canvases.ContainsKey(canvas.Parent) || canvas.Nested.Owners.Any(key => !canvases.ContainsKey(key))) throw Error("Canvas parent/nested binding is absent");
                for (var index = 0; index < canvas.Handles.Owners.Length; index++) {
                    var key = canvas.Handles.Owners[index]; if (key.Length == 0 || key == "@unsupported") continue;
                    var pop = key.EndsWith("/pop", StringComparison.Ordinal); var push = key.EndsWith("/push", StringComparison.Ordinal);
                    var owner = key.Substring(0, Math.Max(0, key.Length - (pop ? 4 : 5)));
                    if ((!pop && !push) || !renderers.TryGetValue(owner, out var renderer)) throw Error("Canvas handle has no native renderer owner");
                    var handle = pop ? renderer.Pop : renderer.Push;
                    if (handle.Canvas != canvas.Owner || handle.HandleIndex != index) throw Error("Canvas handle directory is not reciprocal");
                }
            }
        }
        private static void ApplyHandle(IntPtr pointer, Handle source, Func<string, NativeObject> resolve)
        {
            if (source.Canvas == "@unsupported") throw Error("unbound Canvas handle cannot be restored");
            var canvas = source.Canvas.Length == 0 ? IntPtr.Zero : IntPtr.Add(Pointer(source.Canvas, resolve), 144);
            M.Write(pointer, BitConverter.GetBytes(canvas.ToInt64()).Concat(BitConverter.GetBytes(source.InstructionIndex))
                .Concat(BitConverter.GetBytes(source.HandleIndex)).Concat(BitConverter.GetBytes(source.AbsoluteIndex)).ToArray());
        }
        private static IntPtr HandlePointer(string key, Func<string, NativeObject> resolve)
        {
            if (key.Length == 0) return IntPtr.Zero;
            var pop = key.EndsWith("/pop", StringComparison.Ordinal); var push = key.EndsWith("/push", StringComparison.Ordinal);
            if (!pop && !push) throw Error("unbound Canvas handle cannot be restored");
            var owner = resolve(key.Substring(0, key.Length - (pop ? 4 : 5))); if (owner.GetType() != typeof(CanvasRenderer)) throw Error("Canvas handle target type differs");
            return IntPtr.Add(M.Owner(owner), pop ? 280 : 256);
        }
        private static void ApplyCanvasRenderer(CanvasRendererRow row, CanvasRenderer owner, IntPtr mesh, Func<string, NativeObject> resolve, Action admitted)
        {
            admitted(); var native = M.Owner(owner);
            Function<Binary>(0x233510, "48895c2408488974241048897c24185541564157488d6c24")(native, mesh);
            void Materials(int offset, BindingList list) => WriteDynamic(IntPtr.Add(native, offset), list.Owners.SelectMany(key => BitConverter.GetBytes(Id(key, resolve))).ToArray(), list.Capacity, 4, 4, admitted);
            Materials(104, row.PushMaterials); Materials(136, row.PopMaterials);
            M.Write(IntPtr.Add(native, 168), BitConverter.GetBytes(TextureId(row.Texture, resolve))); M.Write(IntPtr.Add(native, 172), BitConverter.GetBytes(TextureId(row.AlphaTexture, resolve)));
            M.Write(IntPtr.Add(native, 232), BitConverter.GetBytes(Pointer(row.Parent, resolve).ToInt64())); ApplyRanges(native, CanvasRendererRanges, row.Scalars);
            ApplyHandle(IntPtr.Add(native, 256), row.Push, resolve); ApplyHandle(IntPtr.Add(native, 280), row.Pop, resolve); admitted();
        }
        private static void ApplyCanvas(CanvasRow row, Canvas owner, IReadOnlyDictionary<string, IntPtr> meshes, Func<string, NativeObject> resolve, Action admitted)
        {
            admitted(); var native = M.Owner(owner);
            WriteDynamic(IntPtr.Add(native, 208), row.Handles.Owners.SelectMany(key => BitConverter.GetBytes(HandlePointer(key, resolve).ToInt64())).ToArray(), row.Handles.Capacity, 8, 8, admitted);
            WriteDynamic(IntPtr.Add(native, 240), new byte[row.Instructions.Length * 720], row.InstructionCapacity, 720, 16, admitted);
            var instructions = M.Ptr(IntPtr.Add(native, 240)); var materialHeader = Marshal.AllocHGlobal(32);
            try {
                var header = new byte[32]; System.Buffer.BlockCopy(BitConverter.GetBytes(8UL), 0, header, 16, 8); M.Write(materialHeader, header);
                for (var index = 0; index < row.Instructions.Length; index++) {
                    admitted(); var value = row.Instructions[index]; var target = IntPtr.Add(instructions, index * 720);
                    var shared = value.Shared.Length == 0 ? IntPtr.Zero : M.Ptr(IntPtr.Add(meshes[value.Shared],56));
                    Function<SyncVertex>(0x231270, "48896c2420565741574883ec204c8974245033ff4c8bb200")(IntPtr.Add(native, 424), target, shared, materialHeader);
                    M.Write(target, value.Materials.SelectMany(key => BitConverter.GetBytes(Id(key, resolve))).ToArray());
                    M.Write(IntPtr.Add(target, 484), BitConverter.GetBytes(TextureId(value.Texture, resolve))); M.Write(IntPtr.Add(target, 488), BitConverter.GetBytes(TextureId(value.AlphaTexture, resolve)));
                    ApplyRanges(target, InstructionRanges, value.Scalars);
                }
            } finally { Marshal.FreeHGlobal(materialHeader); }
            WriteVector(IntPtr.Add(native, 544), row.Nested.Owners.SelectMany(key => BitConverter.GetBytes(Pointer(key, resolve).ToInt64())).ToArray(), row.Nested.Capacity, 8, admitted);
            M.Write(IntPtr.Add(native, 68), BitConverter.GetBytes(Id(row.Camera, resolve))); M.Write(IntPtr.Add(native, 448), BitConverter.GetBytes(Pointer(row.Parent, resolve).ToInt64()));
            ApplyRanges(native, CanvasRanges, row.Scalars); ApplyRanges(native, BatchRanges, row.BatchScalars); admitted();
        }
        private static void ClearCanvasData(Canvas owner, Action admitted)
        {
            admitted(); var native = M.Owner(owner);
            if (!Function<FenceDone>(0x725ac0, "40534883ec30488bd9e86217000084c0742648833b007420")(IntPtr.Add(native, 424))) throw Error("Canvas batch job is not complete at the admitted boundary");
            Function<Binary>(0x22a7e0, "405741544883ec4848833900488bfa48895c2460488bd948")(IntPtr.Add(native, 424), IntPtr.Add(native, 144)); admitted();
        }
        private static void CanonCanvas(Canvas owner, Action admitted)
        {
            admitted(); var native = M.Owner(owner);
            if (!Function<FenceDone>(0x725ac0, "40534883ec30488bd9e86217000084c0742648833b007420")(IntPtr.Add(native, 424))) throw Error("Canvas Canon would interrupt an active batch job");
            Function<Unary>(0xe5430, "40534883ec2048833900488bd9741433d2e8ca006400488b")(IntPtr.Add(native, 424));
            // The player's own device-loss operation tears down retained GPU
            // buffers/property sheets and sets both rebuild flags. CPU renderer
            // geometry and CanvasData instructions retain their original D.
            Function<Unary>(0x235710, "40534883ec20488bd94881c120010000e87bf0feff66838b")(native);
            M.Write(IntPtr.Add(native, 356), new byte[24]); M.Write(IntPtr.Add(native, 396), new byte[28]); admitted();
        }
        private static void RestoreDirty(DirtyGroup[] source, ResidentObjectDirectory directory, Func<object, string?> bind, Func<string, NativeObject> resolve, Action admitted)
        {
            var manager = CanvasManager(); var reader = new Reader(directory, bind); var before = CaptureDirty(reader, out var empty); reader.RequireFrozen();
            var remove = Function<Binary>(0x226100, "4885d2741b48895424104883ec284883c120488d542438e8");
            foreach (var keyToRemove in empty) { admitted(); remove(manager, keyToRemove); }
            foreach (var group in before) { admitted(); remove(manager, Pointer(group.Canvas, resolve)); }
            var key = Marshal.AllocHGlobal(16);
            try {
                foreach (var group in source) {
                    admitted(); var canvas = Pointer(group.Canvas, resolve); M.Write(key, BitConverter.GetBytes(canvas.ToInt64()));
                    var set = Function<MapSet>(0x2230d0, "48896c2410488974241848897c242041564883ec40488b39")(IntPtr.Add(manager, 32), key);
                    foreach (var name in group.Renderers) InsertDirtySet(set, Pointer(name, resolve), key, admitted);
                }
            } finally { Marshal.FreeHGlobal(key); }
            var after = new Reader(directory, bind); if (Json(source) != Json(CaptureDirty(after,out var remaining)) || remaining.Count != 0) throw Error("Canvas dirty map readback differs"); after.RequireFrozen(); admitted();
        }
        private static void InsertDirtySet(IntPtr set, IntPtr owner, IntPtr temporary, Action admitted)
        {
            admitted(); var header = M.Read(set, 16); var head = M.Ptr(header, 0); var parent = head; var node = M.Ptr(IntPtr.Add(head, 8)); var left = true; var seen = new HashSet<IntPtr>();
            while (node != head) {
                if (node == IntPtr.Zero || !seen.Add(node) || seen.Count > 128) throw Error("target Canvas dirty tree is invalid");
                var bytes = M.Read(node, 40); parent = node; var actual = M.Ptr(bytes, 32);
                if (actual == owner) throw Error("Canvas dirty owner is duplicated"); left = unchecked((ulong)owner.ToInt64()) < unchecked((ulong)actual.ToInt64()); node = M.Ptr(bytes, left ? 0 : 16);
            }
            var storage = Function<Allocate>(0x567ff0, "40534883ec40488bd9488b0d18a94501")(new UIntPtr(40)); if (storage == IntPtr.Zero) throw Error("Canvas dirty tree allocation failed");
            var value = new byte[40]; for (var index = 0; index < 3; index++) System.Buffer.BlockCopy(BitConverter.GetBytes(head.ToInt64()), 0, value, index * 8, 8);
            System.Buffer.BlockCopy(BitConverter.GetBytes(owner.ToInt64()), 0, value, 32, 8); M.Write(storage, value);
            M.Write(temporary, BitConverter.GetBytes(parent.ToInt64()).Concat(BitConverter.GetBytes(left ? 1L : 0L)).ToArray());
            Function<InsertNode>(0xc6200, "48ff41084d8bc84c8b194c8bd1488b0249894008493bc375")(set, temporary, storage); admitted();
        }
        private static void ApplyCanvasRegistry(CanvasRegistry source, ResidentObjectDirectory directory, Func<object, string?> bind, Func<string, NativeObject> resolve, Action admitted)
        {
            var manager = CanvasManager(); WriteVector(IntPtr.Add(manager, 8), source.Roots.Owners.SelectMany(key => BitConverter.GetBytes(Pointer(key, resolve).ToInt64())).ToArray(), source.Roots.Capacity, 8, admitted);
            RestoreDirty(source.Dirty, directory, bind, resolve, admitted);
            M.Write(IntPtr.Add(manager, 48), source.Callbacks.SelectMany(value => CallbackBytes(value, resolve)).ToArray());
            M.Write(IntPtr.Add(manager, 3120), BitConverter.GetBytes((uint)source.Callbacks.Length)); M.Write(IntPtr.Add(manager, 3136), new[] { source.CallbackChanged ? (byte)1 : (byte)0 });
            M.Write(IntPtr.Add(manager, 3144), new[] { source.GuiZTestInitialized ? (byte)1 : (byte)0, source.VrOverlayWarning ? (byte)1 : (byte)0 });
        }
    }
}
