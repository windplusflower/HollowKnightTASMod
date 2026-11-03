using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Object48 is owned by ResidentObjectNativeState. The remaining TagManager
    // body is fully typed here: three native maps, 32 engine layer strings,
    // ordered SortingLayerEntry values and the default sorting-layer index.
    // Names/IDs/counts are snapshot data. Native pointers and allocator labels
    // are rebuilt locally. Tree shape, unused bytes and allocation capacity
    // are common S0; native maps are rebuilt and balanced without PlayerLoop.
    internal static class ResidentTagManagerState
    {
        internal const int NativeTypeRva = 0x195f170, NativeSize = 1400;
        private const int VtableRva = 0x1677260, ContextSlotRva = 0x19d7a40;
        private const int MaximumEntries = 65536, MaximumTextBytes = 16384;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void EraseTree(IntPtr tree, IntPtr allocator, IntPtr root);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Register(IntPtr owner, uint id, IntPtr text);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyString(IntPtr text);

        internal sealed class TextValue
        {
            public string Value = "";
            public int Label, Capacity;
            public bool Inline;
            public byte[] Unused = Array.Empty<byte>();
        }
        internal sealed class MapRow { public TextValue Name = new TextValue(); public uint Id; }
        internal sealed class MapData
        {
            public bool NumericKey;
            public MapRow[] Rows = Array.Empty<MapRow>();
            // Per in-order row: left, parent, right, colour (0 red/1 black).
            public int Root = -1;
            public int[] Shape = Array.Empty<int>();
            public byte[] Padding = Array.Empty<byte>();
            public byte[] SentinelPadding = new byte[54];
        }
        internal sealed class SortingLayer
        {
            public TextValue Name = new TextValue();
            public uint Id;
            public bool Locked;
            public byte[] Padding = new byte[3];
        }
        internal sealed class Data
        {
            public int Version = 1;
            public string Manager = "";
            public int RootAllocatorLabel, SortingAllocatorLabel, SortingCapacity, DefaultSortingIndex;
            public bool SortingOwnsStorage;
            public MapData Tags = new MapData(), TagNames = new MapData { NumericKey = true }, Layers = new MapData();
            public TextValue[] LayerNames = Array.Empty<TextValue>();
            public SortingLayer[] SortingLayers = Array.Empty<SortingLayer>();
            public byte[] Padding = new byte[8]; // array header1372..1375, owner1396..1399
            public string[] CoverageGaps = Array.Empty<string>();
            internal Data Copy() => JsonConvert.DeserializeObject<Data>(JsonConvert.SerializeObject(this))!;
        }
        internal sealed class Live
        {
            internal Data Snapshot = null!;
            internal IntPtr Owner, Engine;
            internal readonly List<Tuple<IntPtr, byte[]>> Witness = new List<Tuple<IntPtr, byte[]>>();
            internal readonly List<Tuple<IntPtr, TextValue>> Strings = new List<Tuple<IntPtr, TextValue>>();
            internal readonly List<NativeMap> Maps = new List<NativeMap>();
            internal Action Admitted = null!;
            private int readCount;
            private long observedBytes;
            internal byte[] Read(IntPtr address, int length)
            {
                if ((readCount++ & 127) == 0) Admitted();
                observedBytes = checked(observedBytes + length);
                if (observedBytes > 32 * 1024 * 1024) throw Error("manager observation exceeds its bounded memory budget");
                var bytes = M.Read(address, length); Witness.Add(Tuple.Create(address, bytes)); return bytes;
            }
            internal void RequireUnchanged()
            {
                Admitted(); var count = 0;
                foreach (var read in Witness) {
                    if ((count++ & 127) == 0) Admitted();
                    if (!read.Item2.SequenceEqual(M.Read(read.Item1, read.Item2.Length))) throw Error("native tag/layer state changed during observation");
                }
                Admitted();
            }
        }
        internal sealed class NativeMap
        {
            internal IntPtr Header, Sentinel;
            internal IntPtr[] Nodes = Array.Empty<IntPtr>();
            internal MapData Data = null!;
        }
        internal static bool OwnsManager(ResidentNativeGlobalObjects.NativeClass type)
            => type != null && type.Rva == NativeTypeRva && type.Size == NativeSize && type.Name == "TagManager"
                && type.Module == "Core" && type.Namespace == "" && type.ObjectDerived && type.GlobalManager && !type.Abstract && !type.Stripped;

        internal static Live Capture(ResidentNativeGlobalObjects.CaptureResult globals, Action admitted)
        {
            if (globals == null || admitted == null) throw new ArgumentNullException();
            admitted(); var types = globals.Snapshot.Classes.Where(type => type.Rva == NativeTypeRva).ToArray();
            if (types.Length != 1 || !OwnsManager(types[0])) throw Error("actual registered native class differs");
            var key = "resident-native-global/" + types[0].Key;
            if (!globals.ManagerPointers.TryGetValue(key, out var owner)) throw Error("registered singleton is absent");
            var live = new Live { Owner = owner, Engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, Admitted = admitted };
            RequireOwner(live.Owner, live.Engine);
            var body = live.Read(IntPtr.Add(owner, 48), NativeSize - 48);
            IntPtr Pointer(int offset) => M.Ptr(body, offset - 48);
            var headers = new[] { Pointer(48), Pointer(56), Pointer(72) };
            if (headers.Any(value => value == IntPtr.Zero) || headers.Distinct().Count() != 3 || Pointer(64) == IntPtr.Zero)
                throw Error("native map owners are absent or aliased");
            var rootLabel = M.I32(live.Read(Pointer(64), 4), 0);
            var tags = CaptureMap(live, headers[0], false); var names = CaptureMap(live, headers[1], true); var layers = CaptureMap(live, headers[2], false);
            if (live.Maps.SelectMany(map => map.Nodes.Concat(new[] { map.Sentinel })).Distinct().Count() != live.Maps.Sum(map => map.Nodes.Length + 1))
                throw Error("native map node storage is shared across owners");
            var layerNames = Enumerable.Range(0, 32).Select(index => CaptureText(live, IntPtr.Add(owner, 80 + index * 40))).ToArray();
            var count = M.U64(body, 1376 - 48); var tagged = M.U64(body, 1384 - 48); var capacity = tagged >> 1;
            var storage = Pointer(1360);
            if (count > capacity || capacity > MaximumEntries || count != 0 && storage == IntPtr.Zero) throw Error("sorting layer allocation differs");
            var sorting = new SortingLayer[(int)count];
            for (var index = 0; index < sorting.Length; index++) {
                var address = IntPtr.Add(storage, index * 48); var raw = live.Read(address, 48);
                if (raw[44] > 1) throw Error("sorting layer locked flag differs");
                sorting[index] = new SortingLayer { Name = CaptureText(live, address), Id = M.U32(raw, 40), Locked = raw[44] == 1, Padding = M.Slice(raw, 45, 3) };
            }
            live.Snapshot = new Data { Manager = key, RootAllocatorLabel = rootLabel, SortingAllocatorLabel = M.I32(body, 1368 - 48),
                SortingCapacity = (int)capacity, SortingOwnsStorage = (tagged & 1) == 0, DefaultSortingIndex = M.I32(body, 1392 - 48),
                Tags = tags.Data, TagNames = names.Data, Layers = layers.Data, LayerNames = layerNames, SortingLayers = sorting,
                Padding = M.Slice(body, 1372 - 48, 4).Concat(M.Slice(body, 1396 - 48, 4)).ToArray() };
            Validate(live.Snapshot); live.Snapshot.CoverageGaps = RequiredGaps(live.Snapshot, false); live.RequireUnchanged(); return live;
        }
        private static TextValue CaptureText(Live live, IntPtr address)
        {
            var header = live.Read(address, 40); var pointer = M.Ptr(header, 0); var size = M.U64(header, 24);
            var capacity = pointer == IntPtr.Zero ? 15UL : M.U64(header, 8);
            if (size > MaximumTextBytes || capacity > 32 * 1024 * 1024 || pointer == IntPtr.Zero && size > 15
                || pointer != IntPtr.Zero && capacity != 0 && capacity < size) throw Error("native text size/capacity differs");
            var text = live.Read(pointer == IntPtr.Zero ? IntPtr.Add(address, 8) : pointer, checked((int)size + 1));
            if (text[text.Length - 1] != 0 || text.Take(text.Length - 1).Contains((byte)0)) throw Error("native name contains an invalid terminator");
            var unused = pointer == IntPtr.Zero ? M.Slice(header, 9 + (int)size, 15 - (int)size) : M.Slice(header, 16, 8);
            var value = new TextValue { Value = Utf8.GetString(text, 0, text.Length - 1), Capacity = (int)capacity, Inline = pointer == IntPtr.Zero,
                Label = M.I32(header, 32), Unused = unused.Concat(M.Slice(header, 36, 4)).ToArray() };
            ValidateText(value); live.Strings.Add(Tuple.Create(address, value)); return value;
        }
        private static NativeMap CaptureMap(Live live, IntPtr address, bool numeric)
        {
            var header = live.Read(address, 16); var sentinel = M.Ptr(header, 0); var count = M.U64(header, 8);
            if (sentinel == IntPtr.Zero || count > MaximumEntries) throw Error("native tree sentinel/count differs");
            var head = live.Read(sentinel, 80);
            if (head[24] != 1 || head[25] != 1) throw Error("native tree sentinel flags differ");
            var nodes = new List<IntPtr>(); var raws = new List<byte[]>(); var rows = new List<MapRow>(); var seen = new HashSet<IntPtr>();
            int Walk(IntPtr node, IntPtr parent, bool parentRed, int depth)
            {
                if (node == sentinel) return 1;
                if (node == IntPtr.Zero || depth > 64 || seen.Count >= (int)count || !seen.Add(node)) throw Error("native tree contains a cycle or invalid depth");
                var raw = live.Read(node, 80);
                if (M.Ptr(raw, 8) != parent || raw[24] > 1 || raw[25] != 0 || parentRed && raw[24] == 0) throw Error("native tree parent/colour differs");
                var leftHeight = Walk(M.Ptr(raw, 0), node, raw[24] == 0, depth + 1);
                nodes.Add(node); raws.Add(raw);
                rows.Add(new MapRow { Id = M.U32(raw, numeric ? 32 : 72), Name = CaptureText(live, IntPtr.Add(node, numeric ? 40 : 32)) });
                var rightHeight = Walk(M.Ptr(raw, 16), node, raw[24] == 0, depth + 1);
                if (leftHeight != rightHeight) throw Error("native tree black height differs");
                return leftHeight + raw[24];
            }
            var root = M.Ptr(head, 8); Walk(root, sentinel, true, 0);
            if (nodes.Count != (int)count || M.Ptr(head, 0) != (count == 0 ? sentinel : nodes[0]) || M.Ptr(head, 16) != (count == 0 ? sentinel : nodes[nodes.Count - 1]))
                throw Error("native tree ordered membership differs");
            var indexByPointer = nodes.Select((node, index) => new { node, index }).ToDictionary(value => value.node, value => value.index);
            int Index(IntPtr pointer) => pointer == sentinel ? -1 : indexByPointer.TryGetValue(pointer, out var index) ? index : throw Error("native tree edge leaves the observed owner");
            var data = new MapData { NumericKey = numeric, Rows = rows.ToArray(), Root = Index(root),
                Shape = raws.SelectMany(raw => new[] { Index(M.Ptr(raw, 0)), Index(M.Ptr(raw, 8)), Index(M.Ptr(raw, 16)), (int)raw[24] }).ToArray(),
                Padding = raws.SelectMany(raw => M.Slice(raw, 26, 6).Concat(M.Slice(raw, numeric ? 36 : 76, 4))).ToArray(),
                SentinelPadding = M.Slice(head, 26, 54) };
            ValidateMap(data); var result = new NativeMap { Header = address, Sentinel = sentinel, Nodes = nodes.ToArray(), Data = data }; live.Maps.Add(result); return result;
        }
        private static void RequireOwner(IntPtr owner, IntPtr engine)
        {
            if (owner == IntPtr.Zero || M.Ptr(IntPtr.Add(engine, ContextSlotRva)) != owner || M.Ptr(owner) != IntPtr.Add(engine, VtableRva)
                || M.Ptr(IntPtr.Add(engine, VtableRva + 64)) != IntPtr.Add(engine, 0x575a90)) throw Error("actual native owner/context/vtable differs");
            RequireFunction(engine, 0x575a90, 8, "6e22e48dd8a86d08ca555a57143eeab223be54882e748e405919117846672ef0");
        }
        private static void RequireFunction(IntPtr engine, int rva, int size, string expected)
        {
            using var sha = SHA256.Create();
            if (BitConverter.ToString(sha.ComputeHash(M.Read(IntPtr.Add(engine, rva), size))).Replace("-", "").ToLowerInvariant() != expected)
                throw Error("pinned native helper differs: " + rva.ToString("x"));
        }

        internal static void Validate(Data data)
        {
            if (data == null || data.Version != 1 || data.Manager != "resident-native-global/195f170/Core//TagManager"
                || data.LayerNames == null || data.LayerNames.Length != 32 || data.SortingLayers == null || data.SortingLayers.Length > MaximumEntries
                || data.SortingCapacity < data.SortingLayers.Length || data.SortingCapacity > MaximumEntries || data.Padding == null || data.Padding.Length != 8
                || data.CoverageGaps == null || data.CoverageGaps.Length > 16 || data.CoverageGaps.Any(value => string.IsNullOrEmpty(value) || value.Length > 4096)) throw Error("native manager schema differs");
            ValidateMap(data.Tags); ValidateMap(data.TagNames); ValidateMap(data.Layers);
            if (data.Tags.NumericKey || !data.TagNames.NumericKey || data.Layers.NumericKey) throw Error("native map key types differ");
            foreach (var name in data.LayerNames) ValidateText(name);
            foreach (var layer in data.SortingLayers) {
                if (layer == null || layer.Padding == null || layer.Padding.Length != 3) throw Error("sorting layer record differs"); ValidateText(layer.Name);
            }
        }
        private static void ValidateText(TextValue text)
        {
            if (text == null || text.Value == null || text.Value.IndexOf('\0') >= 0 || text.Unused == null || text.Label < 0 || text.Label > 4095) throw Error("native text schema differs");
            var length = Utf8.GetByteCount(text.Value);
            if (length > MaximumTextBytes || text.Capacity < 0 || text.Capacity > 32 * 1024 * 1024
                || text.Inline && (length > 15 || text.Capacity != 15 || text.Unused.Length != 19 - length)
                || !text.Inline && (text.Capacity != 0 && text.Capacity < length || text.Unused.Length != 12)) throw Error("native text storage schema differs");
        }
        private static void ValidateMap(MapData map)
        {
            if (map == null || map.Rows == null || map.Rows.Length > MaximumEntries || map.Shape == null || map.Shape.Length != map.Rows.Length * 4
                || map.Padding == null || map.Padding.Length != map.Rows.Length * 10 || map.SentinelPadding == null || map.SentinelPadding.Length != 54)
                throw Error("native tree schema differs");
            for (var index = 0; index < map.Rows.Length; index++) {
                var row = map.Rows[index]; if (row == null) throw Error("native map row is absent"); ValidateText(row.Name);
                if (index > 0 && (map.NumericKey ? map.Rows[index - 1].Id.CompareTo(row.Id) : CompareNames(map.Rows[index - 1].Name.Value, row.Name.Value)) >= 0)
                    throw Error("native map keys repeat or are not in native byte order");
            }
            ValidateTreeShape(map.Shape, map.Root);
        }
        internal static int CompareNames(string left, string right)
        {
            var a = Utf8.GetBytes(left); var b = Utf8.GetBytes(right);
            for (var index = 0; index < Math.Min(a.Length, b.Length); index++) if (a[index] != b[index]) return a[index].CompareTo(b[index]);
            return a.Length.CompareTo(b.Length);
        }
        internal static void ValidateTreeShape(int[] shape, int root)
        {
            if (shape == null || shape.Length % 4 != 0 || shape.Length / 4 > MaximumEntries) throw Error("native tree shape extent differs");
            var count = shape.Length / 4; var seen = new HashSet<int>(); var ordered = new List<int>();
            int Walk(int index, int parent, bool red, int depth)
            {
                if (index == -1) return 1;
                if (index < 0 || index >= count || depth > 64 || !seen.Add(index)) throw Error("native tree shape edge/cycle differs");
                var offset = index * 4; var color = shape[offset + 3];
                if (shape[offset + 1] != parent || color < 0 || color > 1 || red && color == 0) throw Error("native tree shape parent/colour differs");
                var left = Walk(shape[offset], index, color == 0, depth + 1); ordered.Add(index);
                var right = Walk(shape[offset + 2], index, color == 0, depth + 1);
                if (left != right) throw Error("native tree shape black height differs"); return left + color;
            }
            Walk(root, -1, true, 0);
            if (seen.Count != count || !ordered.SequenceEqual(Enumerable.Range(0, count))) throw Error("native tree shape omits or misorders nodes");
        }
        internal static int[] CanonicalTreeShape(int count, out int root)
        {
            if (count < 0 || count > MaximumEntries) throw Error("native tree size exceeds bounds");
            var shape = new int[count * 4]; var redLevel = 0;
            for (var remainder = count - 1; remainder >= 0; remainder = remainder / 2 - 1) redLevel++;
            int Build(int first, int last, int parent, int level) {
                if (last < first) return -1; var middle = first + (last - first) / 2; var offset = middle * 4;
                shape[offset] = Build(first, middle - 1, middle, level + 1); shape[offset + 1] = parent;
                shape[offset + 2] = Build(middle + 1, last, middle, level + 1); shape[offset + 3] = level == redLevel ? 0 : 1; return middle;
            }
            root = Build(0, count - 1, -1, 0); ValidateTreeShape(shape, root); return shape;
        }
        private static IEnumerable<TextValue> Texts(Data data) => data.Tags.Rows.Concat(data.TagNames.Rows).Concat(data.Layers.Rows).Select(row => row.Name)
            .Concat(data.LayerNames).Concat(data.SortingLayers.Select(layer => layer.Name));
        private static string[] RequiredGaps(Data data, bool canonical)
        {
            Validate(data); var gaps = new List<string>();
            if (data.RootAllocatorLabel != 56 || data.SortingAllocatorLabel != 74) gaps.Add("tag-manager/constructor-allocator-dependency-differs");
            if (!data.SortingOwnsStorage && data.SortingLayers.Length != 0) gaps.Add("tag-manager/borrowed-sorting-layer-aliases");
            if (data.SortingLayers.Length == 0 || data.DefaultSortingIndex < 0 || data.DefaultSortingIndex >= data.SortingLayers.Length)
                gaps.Add("tag-manager/default-sorting-layer-index-differs");
            var inverse = data.TagNames.Rows.ToDictionary(row => row.Id);
            if (data.Tags.Rows.Length != inverse.Count || data.Tags.Rows.Select(row => row.Id).Distinct().Count() != data.Tags.Rows.Length
                || data.Tags.Rows.Any(row => !inverse.TryGetValue(row.Id, out var value) || value.Name.Value != row.Name.Value)) gaps.Add("tag-manager/tag-maps-not-bijective");
            var layers = data.Layers.Rows.ToDictionary(row => row.Name.Value, StringComparer.Ordinal);
            if (data.Layers.Rows.Any(row => row.Id >= 32 || data.LayerNames[(int)row.Id].Value != row.Name.Value)
                || data.LayerNames.Where(name => name.Value.Length != 0).Any(name => !layers.TryGetValue(name.Value, out var row) || data.LayerNames[(int)row.Id].Value != name.Value))
                gaps.Add("tag-manager/layer-map-inverse-differs");
            if (canonical) {
                var cold = data.SortingOwnsStorage && data.SortingCapacity == data.SortingLayers.Length && data.Padding.All(value => value == 0)
                    && data.SortingLayers.All(layer => layer.Padding.All(value => value == 0));
                foreach (var map in new[] { data.Tags, data.TagNames, data.Layers }) {
                    var shape = CanonicalTreeShape(map.Rows.Length, out var root);
                    cold &= map.Root == root && map.Shape.SequenceEqual(shape) && map.Padding.All(value => value == 0) && map.SentinelPadding.All(value => value == 0);
                }
                cold &= Texts(data).All(text => text.Label == 72 && text.Inline == (Utf8.GetByteCount(text.Value) < 16)
                    && text.Capacity == (text.Inline ? 15 : Utf8.GetByteCount(text.Value)) && text.Unused.All(value => value == 0));
                if (!cold) gaps.Add("tag-manager/common-native-storage-canon");
            }
            return gaps.ToArray();
        }
        internal static void RequireCanonicalizable(Data data)
        { var gaps = RequiredGaps(data, false); if (gaps.Length != 0) throw Error(string.Join(";", gaps)); }
        internal static void RequireComplete(Data data)
        { var gaps = RequiredGaps(data, true); if (gaps.Length != 0 || data.CoverageGaps.Length != 0) throw Error(string.Join(";", gaps.Concat(data.CoverageGaps))); }
        internal static byte[] EncodeValues(Data data)
        {
            Validate(data); using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Utf8);
            writer.Write(data.Manager);
            foreach (var map in new[] { data.Tags, data.TagNames, data.Layers }) {
                writer.Write(map.NumericKey); writer.Write(map.Rows.Length); foreach (var row in map.Rows) { writer.Write(row.Name.Value); writer.Write(row.Id); }
            }
            foreach (var name in data.LayerNames) writer.Write(name.Value);
            writer.Write(data.SortingLayers.Length); foreach (var row in data.SortingLayers) { writer.Write(row.Name.Value); writer.Write(row.Id); writer.Write(row.Locked); }
            writer.Write(data.DefaultSortingIndex); writer.Flush(); return stream.ToArray();
        }
        internal static byte[] EncodeS0(Data data)
        {
            // All predicates were checked against actual tree edges and native
            // storage. The number of strings/nodes and their values belong to D.
            RequireComplete(data); return new byte[] { 1, 1, 1, 1, 1 };
        }
        internal static void RequireEqual(Data expected, Data actual)
        {
            RequireComplete(expected); RequireComplete(actual);
            if (!EncodeValues(expected).SequenceEqual(EncodeValues(actual)) || !EncodeS0(expected).SequenceEqual(EncodeS0(actual)))
                throw Error("independent complete manager readback differs");
        }
        internal static Live Canon(Live before, ResidentNativeGlobalObjects.CaptureResult globals, ResidentMutationScope scope, Action admitted)
        {
            RequireCanonicalizable(before.Snapshot); before.RequireUnchanged(); scope.Require(); admitted();
            var source = before.Snapshot.Copy(); var functions = new Functions(before.Engine);
            scope.Mutate(() => Rebuild(before.Owner, source, functions, admitted));
            var after = Capture(globals, admitted); RequireComplete(after.Snapshot);
            if (!EncodeValues(source).SequenceEqual(EncodeValues(after.Snapshot))) throw Error("common Canon changed tag/layer D values");
            return after;
        }
        internal static Plan Prepare(Data source, ResidentNativeGlobalObjects.CaptureResult globals, ResidentMutationScope scope, Action admitted)
            => new Plan(source, globals, scope, admitted);
        internal sealed class Plan
        {
            private readonly Data source;
            private readonly ResidentNativeGlobalObjects.CaptureResult globals;
            private readonly ResidentMutationScope scope;
            private readonly Action admitted;
            private readonly Live before;
            private readonly Functions functions;
            private bool attempted, applied;
            internal Plan(Data source, ResidentNativeGlobalObjects.CaptureResult globals, ResidentMutationScope scope, Action admitted)
            {
                if (scope == null || admitted == null) throw new ArgumentNullException(); RequireComplete(source);
                this.source = source.Copy(); this.globals = globals; this.scope = scope; this.admitted = admitted;
                before = Capture(globals, admitted); RequireCanonicalizable(before.Snapshot); functions = new Functions(before.Engine);
                if (source.Manager != before.Snapshot.Manager) throw Error("source and target singleton identity differ");
            }
            internal void Apply()
            {
                if (attempted) throw Error("native manager plan was reused"); attempted = true; admitted(); scope.Require(); before.RequireUnchanged();
                scope.Mutate(() => Rebuild(before.Owner, source, functions, admitted)); applied = true; Verify();
            }
            internal void Verify()
            { if (!applied) throw Error("native manager was not applied"); admitted(); scope.Require(); RequireEqual(source, Capture(globals, admitted).Snapshot); }
        }
        private sealed class Functions
        {
            internal readonly EraseTree EraseNames, EraseIds;
            internal readonly Register RegisterTag, RegisterLayer;
            internal readonly DestroyString Destroy;
            internal Functions(IntPtr engine)
            {
                T Bind<T>(int rva, int size, string hash) where T : Delegate {
                    RequireFunction(engine, rva, size, hash); return (T)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, rva), typeof(T));
                }
                EraseNames = Bind<EraseTree>(0x574ba0, 130, "2e6054d5e1b84913d00310c87e279906619c0c5b0503794e20b1a01eee9abbd3");
                EraseIds = Bind<EraseTree>(0x574b10, 130, "305398ef27775d77784ec9cf2d9fe10cb77ed31e5900ba837d2f118f34524457");
                RegisterTag = Bind<Register>(0x5771f0, 2170, "31fa7a29725d999635765e3cea992b6d494e88611732bd970a490b0e740a1751");
                RegisterLayer = Bind<Register>(0x576830, 2153, "781ece07b9f30803067ab8195e76e8c9a67459e0f1052a0e839f917dc89a364f");
                Destroy = Bind<DestroyString>(0x8edc0, 40, "e0f14cbde4f4b8e90c6ae11619489f3285ec24f1721db49c52becad89b0f9f18");
            }
        }
        private static void ResetText(IntPtr address, string value, Functions functions, Action admitted)
        {
            admitted(); functions.Destroy(address); var empty = new byte[40]; Buffer.BlockCopy(BitConverter.GetBytes(72), 0, empty, 32, 4); M.Write(address, empty);
            M.WriteString(address, value, admitted);
            var bytes = M.Read(address, 40); var length = Utf8.GetByteCount(value);
            if (M.Ptr(bytes, 0) == IntPtr.Zero) M.Write(IntPtr.Add(address, 9 + length), new byte[15 - length]);
            else M.Write(IntPtr.Add(address, 16), new byte[8]);
            M.Write(IntPtr.Add(address, 36), new byte[4]);
        }
        private static void Rebuild(IntPtr owner, Data data, Functions functions, Action admitted)
        {
            admitted();
            foreach (var offset in new[] { 48, 56, 72 }) {
                var header = M.Ptr(IntPtr.Add(owner, offset)); var sentinel = M.Ptr(header); var root = M.Ptr(IntPtr.Add(sentinel, 8));
                (offset == 56 ? functions.EraseIds : functions.EraseNames)(header, header, root);
                var pointers = new byte[24]; for (var index = 0; index < 3; index++) Buffer.BlockCopy(BitConverter.GetBytes(sentinel.ToInt64()), 0, pointers, index * 8, 8);
                M.Write(sentinel, pointers); M.Write(IntPtr.Add(header, 8), new byte[8]); M.Write(IntPtr.Add(sentinel, 26), new byte[54]);
            }
            // Clear names before RegisterLayer: its verified implementation
            // rejects assigning a second non-empty name to an occupied index.
            for (var index = 0; index < 32; index++) ResetText(IntPtr.Add(owner, 80 + index * 40), "", functions, admitted);
            void RegisterRows(MapRow[] rows, Register register) {
                foreach (var row in rows) {
                    admitted(); var bytes = Utf8.GetBytes(row.Name.Value); var input = Marshal.AllocHGlobal(41 + bytes.Length);
                    try {
                        var raw = new byte[41 + bytes.Length];
                        // A nonzero source capacity requests a deep copy. A
                        // capacity-zero external string is a borrowed alias in
                        // this ABI and would let this temporary pointer escape.
                        if (bytes.Length < 16) Buffer.BlockCopy(bytes, 0, raw, 8, bytes.Length);
                        else {
                            Buffer.BlockCopy(BitConverter.GetBytes(IntPtr.Add(input, 40).ToInt64()), 0, raw, 0, 8);
                            Buffer.BlockCopy(BitConverter.GetBytes((ulong)bytes.Length), 0, raw, 8, 8);
                        }
                        Buffer.BlockCopy(BitConverter.GetBytes((ulong)bytes.Length), 0, raw, 24, 8); Buffer.BlockCopy(BitConverter.GetBytes(72), 0, raw, 32, 4);
                        Buffer.BlockCopy(bytes, 0, raw, 40, bytes.Length); Marshal.Copy(raw, 0, input, raw.Length); register(owner, row.Id, input);
                    } finally { Marshal.FreeHGlobal(input); }
                }
            }
            RegisterRows(data.Tags.Rows, functions.RegisterTag); RegisterRows(data.Layers.Rows, functions.RegisterLayer);
            for (var index = 0; index < 32; index++) ResetText(IntPtr.Add(owner, 80 + index * 40), data.LayerNames[index].Value, functions, admitted);
            var array = IntPtr.Add(owner, 1360); var oldHeader = M.Read(array, 32); var oldCount = M.U64(oldHeader, 16);
            var oldStorage = M.Ptr(oldHeader, 0);
            for (var index = 0; index < (int)oldCount; index++) { admitted(); functions.Destroy(IntPtr.Add(oldStorage, index * 48)); }
            M.Write(IntPtr.Add(array, 16), new byte[8]); M.Reserve(array, data.SortingLayers.Length, 48, 8, admitted);
            var storage = M.Ptr(array); M.Write(storage, new byte[data.SortingLayers.Length * 48]);
            for (var index = 0; index < data.SortingLayers.Length; index++) {
                var address = IntPtr.Add(storage, index * 48); var row = data.SortingLayers[index];
                M.Write(IntPtr.Add(address, 32), BitConverter.GetBytes(72));
                ResetText(address, row.Name.Value, functions, admitted);
                M.Write(IntPtr.Add(address, 40), BitConverter.GetBytes(row.Id)); M.Write(IntPtr.Add(address, 44), new[] { row.Locked ? (byte)1 : (byte)0 });
            }
            M.Write(IntPtr.Add(array, 16), BitConverter.GetBytes((ulong)data.SortingLayers.Length));
            M.Write(IntPtr.Add(owner, 1392), BitConverter.GetBytes(data.DefaultSortingIndex));
            M.Write(IntPtr.Add(owner, 1372), new byte[4]); M.Write(IntPtr.Add(owner, 1396), new byte[4]);
            // Re-read the new nodes; canonicalize actual target-owned storage
            // and rebalance in-order without constructing native nodes in CLR.
            var temporary = new Live { Admitted = admitted };
            foreach (var offset in new[] { 48, 56, 72 }) {
                var map = CaptureMap(temporary, M.Ptr(IntPtr.Add(owner, offset)), offset == 56);
                foreach (var pair in temporary.Strings) ResetText(pair.Item1, pair.Item2.Value, functions, admitted);
                temporary.Strings.Clear();
                var shape = CanonicalTreeShape(map.Nodes.Length, out var root);
                IntPtr Pointer(int index) => index < 0 ? map.Sentinel : map.Nodes[index];
                for (var index = 0; index < map.Nodes.Length; index++) {
                    admitted(); var raw = new byte[32];
                    for (var edge = 0; edge < 3; edge++) Buffer.BlockCopy(BitConverter.GetBytes(Pointer(shape[index * 4 + edge]).ToInt64()), 0, raw, edge * 8, 8);
                    raw[24] = (byte)shape[index * 4 + 3]; M.Write(map.Nodes[index], raw);
                    M.Write(IntPtr.Add(map.Nodes[index], offset == 56 ? 36 : 76), new byte[4]);
                }
                var head = new byte[80];
                Buffer.BlockCopy(BitConverter.GetBytes(Pointer(map.Nodes.Length == 0 ? -1 : 0).ToInt64()), 0, head, 0, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(Pointer(root).ToInt64()), 0, head, 8, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(Pointer(map.Nodes.Length - 1).ToInt64()), 0, head, 16, 8); head[24] = head[25] = 1;
                M.Write(map.Sentinel, head);
            }
            admitted();
        }
        private static InvalidDataException Error(string message) => new InvalidDataException("Resident TagManager: " + message);
    }
}
