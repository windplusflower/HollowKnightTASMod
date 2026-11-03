using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Pinned PDB ResourceManager104: Object48 + dependency array32 +
    // multimap16 + reload bool1 + padding7. GetAll returns borrowed iterators;
    // exclusivity of nodes/arrays is unproven. Whole body is readonly Dependency.
    internal static class ResidentResourceManagerState
    {
        internal const int NativeTypeRva = 0x1967280, NativeSize = 104;
        private const int VtableRva = 0x1691520, MaximumEntries = 65536;
        private const string Policy = "same-build-read-only-resource-directory-v1";
        internal sealed class Storage
        {
            public int Capacity, Label;
            public bool Owned, Present;
            public byte[] Padding = new byte[4];
        }
        internal sealed class Reference
        {
            public string Key = "", NativeBinding = "";
            public bool Loaded;
            public ResidentSerializedAssetImage.Data Source = new ResidentSerializedAssetImage.Data();
        }
        internal sealed class Dependency
        {
            public int Object = -1;
            public int[] References = Array.Empty<int>();
            public byte[] Padding = new byte[4];
            public Storage Storage = new Storage();
        }
        internal sealed class Text
        {
            public byte[] Value = Array.Empty<byte>(), Unused = Array.Empty<byte>();
            public int Capacity, Label;
            public bool Inline;
        }
        internal sealed class Entry
        {
            public Text Name = new Text();
            public int Object = -1;
            public byte[] Padding = new byte[10];
        }
        internal sealed class Data
        {
            public int Version = 1, Root = -1;
            public string Manager = "", Ownership = Policy;
            public bool NeedsReload;
            public byte[] Padding = new byte[7], SentinelPadding = new byte[54];
            public Storage DependencyStorage = new Storage();
            public Dependency[] Dependencies = Array.Empty<Dependency>();
            public Reference[] References = Array.Empty<Reference>();
            public Entry[] Container = Array.Empty<Entry>();
            public int[] Shape = Array.Empty<int>();
            public string[] CoverageGaps = Array.Empty<string>();
            internal Data Copy() => JsonConvert.DeserializeObject<Data>(JsonConvert.SerializeObject(this))!;
        }
        internal sealed class Live
        {
            internal Data Snapshot = null!;
            internal IntPtr Owner;
            internal Action Admitted = null!;
            internal ResidentNativeGlobalObjects.CaptureResult Globals = null!;
            internal Func<int, string?> BindNative = null!;
            private long bytes;
            internal readonly List<Tuple<IntPtr, byte[]>> Witness = new List<Tuple<IntPtr, byte[]>>();
            internal byte[] Read(IntPtr address, int size) {
                if (size == 0) return Array.Empty<byte>();
                if ((Witness.Count & 255) == 0) Admitted();
                bytes = checked(bytes + size); if (bytes > 32 * 1024 * 1024) throw Error("resource directory observation exceeds bounds");
                var raw = M.Read(address, size); Witness.Add(Tuple.Create(address, raw)); return raw;
            }
            internal void RequireUnchanged() {
                Globals.RequireUnchanged(); var verify = new M.Readback();
                for (var index = 0; index < Witness.Count; index++) {
                    if ((index & 255) == 0) Admitted();
                    var item = Witness[index];
                    if (!verify.Matches(item.Item1,item.Item2)) throw Error("resource directory changed during observation");
                }
                Admitted();
            }
        }
        internal static bool OwnsManager(ResidentNativeGlobalObjects.NativeClass type)
            => type != null && type.Rva == NativeTypeRva && type.Name == "ResourceManager" && type.Module == "Core" && type.Namespace == ""
                && type.Size == NativeSize && type.ObjectDerived && type.GlobalManager && !type.Abstract && !type.Stripped;
        internal static Live Capture(ResidentNativeGlobalObjects.CaptureResult globals, Action admitted)
            => Capture(globals, id => globals.RegisteredRows.TryGetValue(id, out var row) && row.Binding.Length != 0 ? row.Binding : null, admitted);
        internal static Live Capture(ResidentNativeGlobalObjects.CaptureResult globals, Func<int, string?> bindNative, Action admitted)
        {
            using var images = ResidentSerializedAssetImage.BeginIfAbsent();
            if (globals == null || bindNative == null || admitted == null) throw new ArgumentNullException(); admitted();
            var types = globals.Snapshot.Classes.Where(type => type.Rva == NativeTypeRva).ToArray();
            if (types.Length != 1 || !OwnsManager(types[0])) throw Error("actual ResourceManager RTTI differs");
            var key = "resident-native-global/" + types[0].Key;
            if (!globals.ManagerPointers.TryGetValue(key, out var owner) || owner == IntPtr.Zero) throw Error("registered resource singleton absent or ambiguous");
            var live = new Live { Owner = owner, Globals = globals, Admitted = admitted, BindNative = bindNative };
            var engine = LoadingEngineClockProbe.RequirePinnedEngine();
            if (M.Ptr(owner) != IntPtr.Add(engine.BaseAddress, VtableRva)) throw Error("actual resource manager vtable differs");
            var body = live.Read(IntPtr.Add(owner, 48), 56); if (body[48] > 1) throw Error("native reload flag differs");
            var references = new List<Reference>(); var indices = new Dictionary<int, int>(); var unique = new Dictionary<string, int>(StringComparer.Ordinal);
            int Ref(int id) {
                if (id == 0) return -1; if (indices.TryGetValue(id, out var existing)) return existing;
                if (indices.Count == MaximumEntries) throw Error("resource reference bound exceeded");
                if ((indices.Count & 1023) == 0) {
                    admitted();
                    if (ResidentFailureEvidence.Collecting) ResidentFailureEvidence.Mark("ResourceManager distinct references=" + indices.Count);
                }
                var loaded = globals.RegisteredPointers.ContainsKey(id); int? nativeType = loaded ? globals.RegisteredRows[id].TypeRva : (int?)null;
                var source = ResidentSerializedAssetImage.CaptureMappedId(id, nativeType); var reference = new Reference { Source = source, Loaded = loaded };
                if (loaded) reference.NativeBinding = bindNative(id) ?? "";
                if (source.CoverageGaps.Length == 0) reference.Key = ResidentNativeAssetDirectory.SourceKey(source);
                else if (source.SourcePath.Length == 0 && reference.NativeBinding.StartsWith(ResidentBuiltinTextureRoots.Prefix,StringComparison.Ordinal))
                    reference.Key = reference.NativeBinding;
                if (reference.Key.Length != 0) {
                    if (unique.ContainsKey(reference.Key)) throw Error("different resource PPtrs map to one source identity");
                    unique.Add(reference.Key, id);
                }
                var index = references.Count; indices.Add(id, index); references.Add(reference); return index;
            }
            var array = M.Slice(body, 0, 32); var count = Count(array); var dependencies = new Dependency[count];
            var records = live.Read(M.Ptr(array, 0), checked(count * 40));
            ResidentFailureEvidence.Mark("ResourceManager dependency rows=" + count);
            for (var index = 0; index < count; index++) {
                var offset = index * 40; var header = M.Slice(records, offset + 8, 32); var nested = Count(header);
                var values = live.Read(M.Ptr(header, 0), nested * 4);
                dependencies[index] = new Dependency { Object = Ref(M.I32(records, offset)), Padding = M.Slice(records, offset + 4, 4), Storage = CaptureStorage(header),
                    References = Enumerable.Range(0, nested).Select(item => Ref(M.I32(values, item * 4))).ToArray() };
            }
            var tree = M.Slice(body, 32, 16); var sentinel = M.Ptr(tree, 0); var size = M.U64(tree, 8);
            if (sentinel == IntPtr.Zero || size > MaximumEntries) throw Error("resource tree shape exceeds bounds");
            ResidentFailureEvidence.Mark("ResourceManager container nodes=" + size + ", dependency refs=" + references.Count);
            var head = live.Read(sentinel, 80); if (head[24] != 1 || head[25] != 1) throw Error("resource tree sentinel differs");
            var nodes = new List<IntPtr>(); var raws = new List<byte[]>(); var entries = new List<Entry>(); var seen = new HashSet<IntPtr>();
            int Walk(IntPtr node, IntPtr parent, bool red, int depth) {
                if (node == sentinel) return 1;
                if (node == IntPtr.Zero || depth > 64 || (ulong)seen.Count >= size || !seen.Add(node)) throw Error("resource tree cycle/depth differs");
                var raw = live.Read(node, 80); if (M.Ptr(raw, 8) != parent || raw[24] > 1 || raw[25] != 0 || red && raw[24] == 0) throw Error("resource tree parent/colour differs");
                var left = Walk(M.Ptr(raw, 0), node, raw[24] == 0, depth + 1);
                nodes.Add(node); raws.Add(raw); entries.Add(new Entry { Name = ReadText(live, M.Slice(raw, 32, 40)), Object = Ref(M.I32(raw, 72)), Padding = M.Slice(raw, 26, 6).Concat(M.Slice(raw, 76, 4)).ToArray() });
                var right = Walk(M.Ptr(raw, 16), node, raw[24] == 0, depth + 1);
                if (left != right) throw Error("resource tree black height differs"); return left + raw[24];
            }
            var root = M.Ptr(head, 8); Walk(root, sentinel, true, 0);
            if ((ulong)nodes.Count != size || M.Ptr(head, 0) != (size == 0 ? sentinel : nodes[0]) || M.Ptr(head, 16) != (size == 0 ? sentinel : nodes[nodes.Count - 1])) throw Error("resource tree membership differs");
            var nodeIds = nodes.Select((pointer, index) => new { pointer, index }).ToDictionary(item => item.pointer, item => item.index);
            int Node(IntPtr pointer) => pointer == sentinel ? -1 : nodeIds.TryGetValue(pointer, out var index) ? index : throw Error("resource tree edge leaves owner");
            var sorted = references.Select((reference, index) => new { reference, index }).OrderBy(item => item.reference.Key, StringComparer.Ordinal).ToArray();
            var remap = sorted.Select((item, index) => new { item.index, current = index }).ToDictionary(item => item.index, item => item.current);
            int Remap(int index) => index == -1 ? -1 : remap[index];
            foreach (var dependency in dependencies) { dependency.Object = Remap(dependency.Object); dependency.References = dependency.References.Select(Remap).ToArray(); }
            foreach (var entry in entries) entry.Object = Remap(entry.Object);
            var data = new Data { Manager = key, NeedsReload = body[48] != 0, Padding = M.Slice(body, 49, 7), DependencyStorage = CaptureStorage(array), Dependencies = dependencies,
                References = sorted.Select(item => item.reference).ToArray(), Container = entries.ToArray(), Root = Node(root),
                Shape = raws.SelectMany(raw => new[] { Node(M.Ptr(raw, 0)), Node(M.Ptr(raw, 8)), Node(M.Ptr(raw, 16)), (int)raw[24] }).ToArray(), SentinelPadding = M.Slice(head, 26, 54) };
            ResidentFailureEvidence.Mark("ResourceManager validation, refs=" + references.Count);
            data.CoverageGaps = RequiredGaps(data); live.Snapshot = data; live.RequireUnchanged(); return live;
        }
        private static int Count(byte[] header) {
            var count = M.U64(header, 16); var capacity = M.U64(header, 24) >> 1;
            if (count > capacity || capacity > MaximumEntries || capacity != 0 && M.Ptr(header, 0) == IntPtr.Zero) throw Error("resource array shape differs"); return (int)count;
        }
        private static Storage CaptureStorage(byte[] header) => new Storage { Capacity = (int)(M.U64(header, 24) >> 1), Label = M.I32(header, 8), Owned = (M.U64(header, 24) & 1) == 0,
            Present = M.Ptr(header, 0) != IntPtr.Zero, Padding = M.Slice(header, 12, 4) };
        private static Text ReadText(Live live, byte[] raw) {
            var pointer = M.Ptr(raw, 0); var size = M.U64(raw, 24); var capacity = pointer == IntPtr.Zero ? 15UL : M.U64(raw, 8);
            if (size > 16384 || capacity > 32 * 1024 * 1024 || pointer == IntPtr.Zero && size > 15 || pointer != IntPtr.Zero && capacity != 0 && capacity < size) throw Error("resource key string shape differs");
            var length = checked((int)size + 1);
            var bytes = pointer == IntPtr.Zero ? M.Slice(raw, 8, length) : live.Read(pointer, length);
            if (bytes[bytes.Length - 1] != 0) throw Error("resource key terminator differs");
            return new Text { Value = M.Slice(bytes, 0, bytes.Length - 1), Inline = pointer == IntPtr.Zero, Capacity = (int)capacity, Label = M.I32(raw, 32),
                Unused = (pointer == IntPtr.Zero ? M.Slice(raw, 9 + (int)size, 15 - (int)size) : M.Slice(raw, 16, 8)).Concat(M.Slice(raw, 36, 4)).ToArray() };
        }
        internal static void Validate(Data data)
        {
            if (data == null || data.Version != 1 || data.Manager == null || !data.Manager.StartsWith("resident-native-global/1967280/", StringComparison.Ordinal)
                || data.Padding == null || data.Padding.Length != 7 || data.SentinelPadding == null || data.SentinelPadding.Length != 54 || data.References == null || data.References.Length > MaximumEntries
                || data.Dependencies == null || data.Dependencies.Length > MaximumEntries || data.Container == null || data.Container.Length > MaximumEntries || data.Shape == null || data.Shape.Length != data.Container.Length * 4
                || data.CoverageGaps == null || data.CoverageGaps.Any(string.IsNullOrEmpty)) throw Error("resource typed dependency schema differs");
            ResidentTagManagerState.ValidateTreeShape(data.Shape, data.Root); ValidateStorage(data.DependencyStorage, data.Dependencies.Length);
            bool Index(int index) => index >= -1 && index < data.References.Length;
            var keys = new HashSet<string>(StringComparer.Ordinal); string? previous = null;
            foreach (var reference in data.References) {
                if (reference == null || reference.Source == null || reference.Key == null || reference.NativeBinding == null || !reference.Loaded && reference.NativeBinding.Length != 0) throw Error("resource reference schema differs");
                ResidentSerializedAssetImage.Validate(reference.Source);
                if (reference.Key.Length != 0) {
                    if (!keys.Add(reference.Key) || previous != null && StringComparer.Ordinal.Compare(previous,reference.Key) > 0)
                        throw Error("resource source binding/order differs");
                    if (reference.Key.StartsWith(ResidentBuiltinTextureRoots.Prefix,StringComparison.Ordinal)) {
                        if (!reference.Loaded || reference.NativeBinding != reference.Key || reference.Source.SourcePath.Length != 0)
                            throw Error("builtin resource PPtr lacks matching native ownership");
                        _ = ResidentBuiltinTextureRoots.Decode(reference.Key);
                    } else if (reference.Key != ResidentNativeAssetDirectory.SourceKey(reference.Source)) throw Error("serialized resource PPtr identity differs");
                }
                previous = reference.Key;
            }
            foreach (var dependency in data.Dependencies) {
                if (dependency == null || !Index(dependency.Object) || dependency.References == null || dependency.References.Length > MaximumEntries || dependency.References.Any(index => !Index(index))
                    || dependency.Padding == null || dependency.Padding.Length != 4) throw Error("resource dependent asset schema differs"); ValidateStorage(dependency.Storage, dependency.References.Length);
            }
            byte[]? last = null;
            foreach (var entry in data.Container) {
                if (entry == null || !Index(entry.Object) || entry.Padding == null || entry.Padding.Length != 10) throw Error("resource multimap payload differs"); ValidateText(entry.Name);
                if (last != null && Compare(last, entry.Name.Value) > 0) throw Error("resource multimap key order differs"); last = entry.Name.Value;
            }
        }
        private static int Compare(byte[] first, byte[] second) { for (var index = 0; index < Math.Min(first.Length, second.Length); index++) if (first[index] != second[index]) return first[index].CompareTo(second[index]); return first.Length.CompareTo(second.Length); }
        private static void ValidateStorage(Storage storage, int count) {
            if (storage == null || storage.Capacity < count || storage.Capacity > MaximumEntries || storage.Capacity != 0 && !storage.Present || storage.Label < 0 || storage.Padding == null || storage.Padding.Length != 4) throw Error("typed resource array storage differs");
        }
        private static void ValidateText(Text text) {
            if (text == null || text.Value == null || text.Value.Length > 16384 || text.Label < 0 || text.Capacity < 0 || text.Capacity > 32 * 1024 * 1024 || text.Unused == null
                || text.Inline && (text.Value.Length > 15 || text.Capacity != 15) || !text.Inline && text.Capacity != 0 && text.Capacity < text.Value.Length
                || text.Unused.Length != (text.Inline ? 19 - text.Value.Length : 12)) throw Error("typed resource key storage differs");
        }
        internal static string[] RequiredGaps(Data data) {
            Validate(data); var gaps = new SortedSet<string>(StringComparer.Ordinal);
            if (data.Ownership != Policy) gaps.Add("resource-manager/unproven-storage-mutation-policy");
            if (data.NeedsReload) gaps.Add("resource-manager/pending-reload");
            foreach (var reference in data.References) {
                foreach (var gap in reference.Source.CoverageGaps)
                    if (gap != "serialized-image/no-source-identifier" || !reference.Key.StartsWith(ResidentBuiltinTextureRoots.Prefix,StringComparison.Ordinal))
                        gaps.Add("resource-manager/" + gap);
                if (reference.Key.Length == 0) gaps.Add("resource-manager/unresolved-source-reference");
                if (reference.Loaded && reference.NativeBinding.Length == 0) gaps.Add("resource-manager/loaded-native-reference-unbound");
            }
            return gaps.ToArray();
        }
        internal static void RequireCanonicalizable(Data data) { var gaps = RequiredGaps(data); if (gaps.Length != 0 || data.CoverageGaps.Length != 0) throw Error(string.Join(";", gaps.Concat(data.CoverageGaps))); }
        internal static void RequireComplete(Data data) => RequireCanonicalizable(data);
        internal static byte[] EncodeDependencies(Data data) { RequireComplete(data); return System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(data)); }
        internal static byte[] EncodeS0(Data data) => EncodeDependencies(data);
        internal static byte[] EncodeValues(Data data) => EncodeDependencies(data);
        internal static void RequireEqual(Data expected, Data actual) { if (!EncodeDependencies(expected).SequenceEqual(EncodeDependencies(actual))) throw Error("independent complete resource dependency differs"); }
        internal static void RequireReferenceCoverage(Data data, ResidentNativeGlobalObjects.Data globals) {
            RequireComplete(data); ResidentNativeGlobalObjects.Validate(globals);
            var owners = globals.Rows.Where(row => row.Binding.Length != 0).ToDictionary(row => row.Binding,StringComparer.Ordinal);
            foreach (var reference in data.References.Where(reference => reference.Loaded)) {
                if (!owners.TryGetValue(reference.NativeBinding,out var row)
                    || (reference.Key.StartsWith(ResidentBuiltinTextureRoots.Prefix,StringComparison.Ordinal)
                        ? row.Kind != "native-asset" : row.TypeRva != reference.Source.NativeTypeRva))
                    throw Error("resource reference lacks its independently covered native owner");
            }
        }
        internal static Live Canon(Live before, ResidentMutationScope scope) {
            RequireCanonicalizable(before.Snapshot); before.RequireUnchanged(); scope.Require(); var after = Capture(before.Globals, before.BindNative, before.Admitted); RequireEqual(before.Snapshot, after.Snapshot); return after;
        }
        internal static Plan Prepare(Data source, ResidentNativeGlobalObjects.CaptureResult globals, Func<int, string?> bindNative, ResidentMutationScope scope, Action admitted)
            => new Plan(source, globals, bindNative, scope, admitted);
        internal sealed class Plan {
            private readonly Data source; private readonly Live before; private readonly ResidentMutationScope scope; private bool attempted, applied;
            internal Plan(Data source, ResidentNativeGlobalObjects.CaptureResult globals, Func<int, string?> bindNative, ResidentMutationScope scope, Action admitted) {
                RequireComplete(source); this.source = source.Copy(); this.scope = scope; before = Capture(globals, bindNative, admitted); RequireEqual(this.source, before.Snapshot);
            }
            internal void Apply() { if (attempted) throw Error("resource dependency plan reused"); attempted = true; scope.Require(); before.RequireUnchanged(); RequireEqual(source, Capture(before.Globals, before.BindNative, before.Admitted).Snapshot); applied = true; }
            internal void Verify(ResidentNativeGlobalObjects.CaptureResult freshGlobals, Func<int, string?> bindNative) { if (!applied) throw Error("resource verify precedes Apply"); scope.Require(); RequireEqual(source, Capture(freshGlobals, bindNative, before.Admitted).Snapshot); }
        }
        private static InvalidDataException Error(string reason) => new InvalidDataException("ResidentResourceManagerState: " + reason);
    }
}
