using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using Newtonsoft.Json;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Native-only identities are not Unity wrappers and never enter the managed
    // asset factory. Serialized provenance and live-body coverage are separate.
    // Object48 is delegated explicitly through NativeOwners, never inferred from
    // an image hash. No type/name/path/count is an instance-specific whitelist.
    internal static partial class ResidentNativeAssetDirectory
    {
        private static readonly Dictionary<string, string> pathHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly object pathHashGate = new object();
        private const int Maximum = ResidentObjectDirectory.MaximumComponents;
        internal sealed class Data
        {
            public int Version = 2;
            [JsonProperty(Required = Required.Always)] public string[] BuiltinSlotOwners = null!;
            public Row[] Rows = Array.Empty<Row>();
            public string[] CoverageGaps = Array.Empty<string>();
            internal Data Copy() => JsonConvert.DeserializeObject<Data>(JsonConvert.SerializeObject(this))!;
        }
        internal sealed class Row
        {
            public string Binding = "", TypeKey = "", Kind = "unsupported", Owner = "", ManagedType = "";
            public int TypeRva, Size, Vtable;
            public bool ManagedPresent, Persistent, NamePresent;
            public ResidentSerializedAssetImage.Data Source = new ResidentSerializedAssetImage.Data();
            public byte[] Name = Array.Empty<byte>(), Values = Array.Empty<byte>(), Padding = Array.Empty<byte>();
            public TextStorage? Text;
            public TextureBody? Texture;
        }
        internal sealed class TextStorage
        {
            public byte[] Bytes = Array.Empty<byte>(), Unused = Array.Empty<byte>();
            public int Capacity, Label;
            public bool Inline;
        }
        internal sealed class Live
        {
            internal Data Snapshot = null!;
            internal Dictionary<IntPtr,TextureAliasGroup>? TextureAliasOwners;
            internal readonly Dictionary<IntPtr,TextureImage> TextureImages = new Dictionary<IntPtr,TextureImage>();
            internal readonly Dictionary<string,TextureLocal> TextureLocals = new Dictionary<string,TextureLocal>(StringComparer.Ordinal);
            internal readonly Dictionary<string, Tuple<int, ResidentNativeGlobalObjects.Row>> OriginalRows = new Dictionary<string, Tuple<int, ResidentNativeGlobalObjects.Row>>(StringComparer.Ordinal);
            internal readonly Dictionary<string, IntPtr> Pointers = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            internal readonly List<ResidentObjectNativeState.GlobalOwner> NativeOwners = new List<ResidentObjectNativeState.GlobalOwner>();
            internal IReadOnlyDictionary<string, IntPtr> Bindings => new ReadOnlyDictionary<string, IntPtr>(Pointers);
            internal readonly Dictionary<string, object> managedRoots = new Dictionary<string, object>(StringComparer.Ordinal);
            internal IReadOnlyDictionary<string, object> ManagedRoots => new ReadOnlyDictionary<string, object>(managedRoots);
            internal object? ResolveExternal(string key) => managedRoots.TryGetValue(key, out var value) ? value : null;
            internal readonly Dictionary<NativeObject, string> ManagedBindings = new Dictionary<NativeObject, string>(ResidentObjectDirectory.ReferenceComparer.Instance);
            internal string? BindExternal(object value) => value is NativeObject owner && ManagedBindings.TryGetValue(owner, out var key) ? key : null;
            internal readonly List<Tuple<IntPtr, byte[]>> Witness = new List<Tuple<IntPtr, byte[]>>();
            internal Action Admitted = null!;
            internal ResidentNativeGlobalObjects.CaptureResult Globals = null!;
            internal ResidentBuiltinTextureRoots.Live Builtins = null!;
            private long observed;
            internal long WitnessBytes => observed;
            internal byte[] Read(IntPtr pointer, int size)
            {
                if (size == 0) return Array.Empty<byte>();
                Admitted(); observed = checked(observed + size); if (observed > 32 * 1024 * 1024) throw Error("native asset observation exceeds bounds");
                var bytes = M.Read(pointer, size); Witness.Add(Tuple.Create(pointer, bytes)); return bytes;
            }
            // Identity projection only. Caller MUST also retain RequiredGaps
            // and independent Object48/type-body proofs in process coverage.
            internal ResidentNativeGlobalObjects.Data ProjectCapturedDirectory()
            {
                // Capture has independently read back both directories. This
                // projection uses only their captured rows; the outer world
                // observation checks the live witnesses again before use.
                Admitted(); var result = Globals.Snapshot.Copy(); var rows = result.Rows;
                var indices = IndexRegisteredRows(Globals.Snapshot.Rows);
                foreach (var asset in Snapshot.Rows.Where(row => row.Binding.Length != 0)) {
                    var exact = OriginalRows[asset.Binding];
                    if (!Globals.RegisteredRows.TryGetValue(exact.Item1, out var registered) || !ReferenceEquals(registered, exact.Item2)
                        || !Globals.RegisteredPointers.TryGetValue(exact.Item1, out var pointer) || pointer != Pointers[asset.Binding]) throw Error("native asset exact registered identity changed");
                    if (!indices.TryGetValue(registered, out var index) || registered.Kind != "unbound" || registered.Binding.Length != 0 || registered.TypeRva != asset.TypeRva
                        || registered.Vtable != asset.Vtable || registered.ManagedPresent != asset.ManagedPresent || registered.ManagedType != asset.ManagedType || registered.Persistent != asset.Persistent)
                        throw Error("native asset exact registration row differs");
                    rows[index] = new ResidentNativeGlobalObjects.Row { TypeRva = asset.TypeRva, Vtable = asset.Vtable, Persistent = asset.Persistent,
                        ManagedPresent = asset.ManagedPresent, ManagedType = asset.ManagedType, Kind = "native-asset", Binding = asset.Binding };
                }
                var classes = result.Classes.ToDictionary(type => type.Rva);
                var unresolved = new HashSet<string>(rows.Where(row => row.Kind == "unbound").Select(row => "global-object/unbound-native-owner:" + classes[row.TypeRva].Key), StringComparer.Ordinal);
                result.Rows = rows.OrderBy(row => row.TypeRva).ThenBy(row => row.Binding, StringComparer.Ordinal).ToArray();
                result.CoverageGaps = result.CoverageGaps.Where(gap => !gap.StartsWith("global-object/unbound-native-owner:", StringComparison.Ordinal) || unresolved.Contains(gap)).ToArray();
                ResidentNativeGlobalObjects.Validate(result); Admitted(); return result;
            }
            internal void RequireUnchanged()
            {
                Globals.RequireUnchanged(); Builtins.RequireUnchanged();
                var verify = new M.Readback();
                foreach (var item in Witness) { Admitted(); if (!verify.Matches(item.Item1,item.Item2)) throw Error("native asset changed during observation"); }
            }
        }
        private static Dictionary<ResidentNativeGlobalObjects.Row,int> IndexRegisteredRows(ResidentNativeGlobalObjects.Row[] rows)
        {
            var indices = new Dictionary<ResidentNativeGlobalObjects.Row,int>(rows.Length);
            for (var i = 0; i < rows.Length; i++) {
                var row = rows[i]; // Row has reference identity.
                if (row == null || indices.ContainsKey(row)) throw Error("exact native registration row is absent or repeated");
                indices.Add(row, i);
            }
            return indices;
        }
        internal static int ExactRegisteredRowIndex(ResidentNativeGlobalObjects.Row[] rows, ResidentNativeGlobalObjects.Row exact)
            => rows != null && exact != null && IndexRegisteredRows(rows).TryGetValue(exact, out var i) ? i : throw Error("exact native registration row is absent");
        internal static string SourceKey(ResidentSerializedAssetImage.Data source)
        {
            ResidentSerializedAssetImage.RequireComplete(source);
            return ProvenanceKey(source.NativeTypeRva,source.SourcePath,source.LocalIdentifier);
        }
        // References may discover a key before the full object body is read.
        // Only Live.Pointers (which has proved the source image) can resolve it.
        internal static string ProvenanceKey(int typeRva,string sourcePath,long localIdentifier)
        {
            if (typeRva <= 0 || string.IsNullOrEmpty(sourcePath) || localIdentifier == 0) throw Error("incomplete native resource identity");
            string path;
            // Pure UTF8/SHA of an immutable string, never a file/provenance
            // cache. Bounded independently of the number of observations.
            lock (pathHashGate) {
                if (!pathHashes.TryGetValue(sourcePath, out path!)) {
                    using (var sha = SHA256.Create()) path = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sourcePath))).Replace("-", "").ToLowerInvariant();
                    if (pathHashes.Count < 1024) pathHashes.Add(sourcePath, path);
                }
            }
            return "resident-native-asset/" + typeRva.ToString("x", CultureInfo.InvariantCulture) + "/" + path + "/" + localIdentifier.ToString(CultureInfo.InvariantCulture);
        }
        internal static Live Capture(ResidentNativeGlobalObjects.CaptureResult globals, IReadOnlyDictionary<IntPtr, string> known, Action admitted)
        {
            if (globals == null || known == null || admitted == null) throw new ArgumentNullException();
            globals.RequireUnchanged();
            using var images = ResidentSerializedAssetImage.BeginObservation();
            var live = new Live { Globals = globals, Admitted = admitted }; var classes = globals.Snapshot.Classes.ToDictionary(type => type.Rva);
            live.Builtins = ResidentNativeAssetReference.CompleteBuiltins(globals, admitted);
            var engine = LoadingEngineClockProbe.RequirePinnedEngine(); var rows = new List<Row>();
            var managedTypes = new Dictionary<Type, string>();
            var byId = new Dictionary<int, string>(); var local = new Dictionary<Row, Tuple<int, IntPtr>>();
            images.PrimeRegisteredRemappings(globals.RegisteredRows.Where(pair => pair.Value.Kind == "unbound"
                && !classes[pair.Value.TypeRva].GlobalManager).Select(pair => pair.Key), admitted);
            if (ResidentFailureEvidence.Collecting) {
                var builtinUnmapped = live.Builtins.Aliases.Keys.Count(pointer =>
                    images.ObservedUnmapped(M.I32(M.Read(IntPtr.Add(pointer,8),4),0)));
                ResidentFailureEvidence.Mark("builtin texture roots=" + live.Builtins.Origins.Count + ", nonnull slots="
                    + live.Builtins.Origins.Count(pair => pair.Value != IntPtr.Zero) + ", distinct owners=" + live.Builtins.Aliases.Count
                    + ", observed unmapped=" + builtinUnmapped);
                var counts = globals.RegisteredRows.Where(pair => pair.Value.Kind == "unbound")
                    .GroupBy(pair => pair.Value.TypeRva).OrderBy(group => group.Key)
                    .Select(group => classes[group.Key].Name + "=" + group.Count() + " (" + group.Count(pair => images.ObservedUnmapped(pair.Key)) + " unmapped)");
                ResidentFailureEvidence.Mark("global owners requiring resource bindings: " + string.Join(", ", counts));
            }
            var examined = 0; var nextReport = M.WallMilliseconds() + 300;
            foreach (var pair in globals.RegisteredPointers) {
                ++examined;
                if ((examined & 127) == 0) admitted();
                if (ResidentFailureEvidence.Collecting && (examined & 63) == 0 && M.WallMilliseconds() >= nextReport) {
                    ResidentFailureEvidence.Mark("native asset registrations=" + examined + "/" + globals.RegisteredPointers.Count
                        + ", source images=" + rows.Count + ", " + images.DescribeDiskReads());
                    nextReport = M.WallMilliseconds() + 300;
                }
                if (!globals.RegisteredRows.TryGetValue(pair.Key, out var registration)) throw Error("native ID lacks its exact registered row");
                if (known.TryGetValue(pair.Value, out var knownKey) && (registration.Kind == "unbound" || registration.Binding != knownKey))
                    throw Error("supplied known identity disagrees with exact native registration row");
                // Already-bound rows retain their ID, header, type and virtual
                // RTTI witnesses in Globals. Only unresolved rows need a
                // second local owner while we discover their asset provenance.
                if (registration.Binding.Length != 0) { byId.Add(pair.Key, registration.Binding); continue; }
                if (registration.Kind != "unbound") continue;
                if (!classes.TryGetValue(registration.TypeRva, out var type)) throw Error("registered native resource type differs");
                if (type.GlobalManager) continue;
                var source = ResidentSerializedAssetImage.CaptureRegistered(pair.Key, pair.Value, type, globals, engine.BaseAddress);
                ResidentSerializedAssetImage.Validate(source);
                var row = new Row { TypeKey = type.Key, TypeRva = type.Rva, Size = type.Size, Persistent = registration.Persistent,
                    Source = source, Vtable = registration.Vtable };
                // Partial provenance remains recorded but never becomes a binding.
                if (source.CoverageGaps.Length == 0) row.Binding = SourceKey(source);
                else if (source.SourcePath.Length == 0 && live.Builtins.Aliases.ContainsKey(pair.Value))
                    row.Binding = live.Builtins.Key(pair.Value);
                if (row.Binding.Length != 0) {
                    if (source.NativeTypeRva != 0 && source.NativeTypeRva != type.Rva || live.Pointers.ContainsKey(row.Binding))
                        throw Error("native asset identity is ambiguous or has another actual type");
                    live.Pointers.Add(row.Binding, pair.Value); byId.Add(pair.Key, row.Binding); live.OriginalRows.Add(row.Binding, Tuple.Create(pair.Key, registration));
                }
                rows.Add(row); local.Add(row, Tuple.Create(pair.Key, pair.Value));
            }
            ResidentFailureEvidence.Mark("native asset wrapper bindings");
            var bound = rows.Where(row => row.Binding.Length != 0).ToArray();
            var wrappers = globals.BorrowRegisteredAssets(bound.Select(row => local[row].Item1).ToArray());
            for (var i = 0; i < bound.Length; ++i) {
                var row = bound[i]; var wrapper = wrappers[i];
                row.ManagedPresent = !ReferenceEquals(wrapper, null);
                live.NativeOwners.Add(new ResidentObjectNativeState.GlobalOwner(row.Binding, row.TypeKey, local[row].Item2, wrapper));
                if (ReferenceEquals(wrapper, null)) continue;
                var actual = wrapper!.GetType();
                if (!managedTypes.TryGetValue(actual, out var key)) managedTypes.Add(actual, key = ResidentObjectDirectory.TypeRecord.Capture(actual).Key);
                row.ManagedType = key;
                live.ManagedBindings.Add(wrapper, row.Binding); live.managedRoots.Add(row.Binding, wrapper);
            }
            if (ResidentFailureEvidence.Collecting)
                ResidentFailureEvidence.Mark("native resource bodies=" + rows.Count + ", " + images.DescribeDiskReads());
            var ordered = rows.OrderBy(row => row.TypeRva).ThenBy(row => row.Binding, StringComparer.Ordinal).ToArray();
            int lastType = 0;
            foreach (var row in ordered) {
                if (row.TypeRva != lastType) {
                    if (ResidentFailureEvidence.Collecting)
                        ResidentFailureEvidence.Mark("native resource body type=" + row.TypeKey + ", retained bytes=" + live.WitnessBytes);
                    lastType = row.TypeRva;
                }
                ReadBody(live, row, local[row].Item2, engine.BaseAddress, byId);
            }
            foreach (var type in managedTypes) if (ResidentObjectDirectory.TypeRecord.Capture(type.Key).Key != type.Value) throw Error("native asset managed type metadata changed");
            var slots = ResidentBuiltinTextureRoots.DeclaredOrigins().Select(origin => {
                var pointer = live.Builtins.Origins[origin];
                if (pointer == IntPtr.Zero) return "";
                var id = M.I32(live.Read(IntPtr.Add(pointer,8),4),0);
                if (!globals.RegisteredPointers.TryGetValue(id,out var registered) || registered != pointer || !byId.TryGetValue(id,out var key))
                    throw Error("builtin texture static slot has no verified asset binding");
                return key;
            }).ToArray();
            live.Snapshot = new Data { BuiltinSlotOwners = slots, Rows = ordered };
            ResidentFailureEvidence.Mark("native resource validation and independent readback");
            Validate(live.Snapshot); live.Snapshot.CoverageGaps = RequiredGaps(live.Snapshot); live.RequireUnchanged(); return live;
        }
        private static void ReadBody(Live live, Row row, IntPtr pointer, IntPtr engine, IReadOnlyDictionary<int, string> bindings)
        {
            if (ReadTexture(live,row,pointer,engine,bindings)) return;
            if (row.TypeRva != 0x1986550 && row.TypeRva != 0x1968310) return;
            var physics = row.TypeRva == 0x1986550;
            if (row.Size != (physics ? 72 : 96)) throw Error("typed native asset size differs");
            var bytes = live.Read(pointer, row.Size);
            if (M.Ptr(bytes, 0) != IntPtr.Add(engine, physics ? 0x1701768 : 0x1696fa8)) throw Error("typed native asset vtable differs");
            row.Kind = physics ? "physics-material-2d" : "text-asset";
            ReadName(live, row, M.Ptr(bytes, 48));
            if (physics) {
                row.Values = M.Slice(bytes, 56, 8); row.Padding = M.Slice(bytes, 68, 4); var owner = M.I32(bytes, 64);
                row.Owner = owner == 0 ? "" : bindings.TryGetValue(owner, out var key) ? key : "@unresolved-native-owner";
            } else {
                var text = M.Slice(bytes, 56, 40); var storage = M.Ptr(text, 0); var size = M.U64(text, 24); var capacity = storage == IntPtr.Zero ? 15UL : M.U64(text, 8);
                if (size > 32 * 1024 * 1024 - 1 || capacity > 32 * 1024 * 1024 || storage == IntPtr.Zero && size > 15 || storage != IntPtr.Zero && capacity != 0 && capacity < size)
                    throw Error("native TextAsset storage shape differs");
                var actual = live.Read(storage == IntPtr.Zero ? IntPtr.Add(pointer, 64) : storage, checked((int)size + 1));
                if (actual[actual.Length - 1] != 0) throw Error("native TextAsset storage terminator differs");
                row.Text = new TextStorage { Bytes = M.Slice(actual, 0, actual.Length - 1), Inline = storage == IntPtr.Zero, Capacity = (int)capacity,
                    Label = M.I32(text, 32), Unused = (storage == IntPtr.Zero ? M.Slice(text, 9 + (int)size, 15 - (int)size) : M.Slice(text, 16, 8)).Concat(M.Slice(text, 36, 4)).ToArray() };
            }
        }
        internal static void Validate(Data data)
        {
            var originNames = ResidentBuiltinTextureRoots.DeclaredOrigins();
            if (data == null || data.Version != 2 || data.Rows == null || data.Rows.Length > Maximum || data.CoverageGaps == null || data.CoverageGaps.Length > Maximum || data.CoverageGaps.Any(string.IsNullOrEmpty)
                || data.BuiltinSlotOwners == null || data.BuiltinSlotOwners.Length != originNames.Length
                || data.BuiltinSlotOwners.Any(key => key == null || key.Length > 16384)) throw Error("native asset directory schema differs");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in data.Rows) {
                if (row == null || row.Source == null || row.TypeKey == null || row.Binding == null || row.Owner == null || row.Name == null || row.Name.Length > 16384
                    || row.Name.Contains((byte)0) || !row.NamePresent && row.Name.Length != 0 || row.Values == null || row.Padding == null || row.Size < 48 || row.TypeRva <= 0 || row.Vtable <= 0 || row.ManagedType == null || row.ManagedPresent != (row.ManagedType.Length != 0)
                    || !row.TypeKey.StartsWith(row.TypeRva.ToString("x", CultureInfo.InvariantCulture) + "/", StringComparison.Ordinal)) throw Error("native asset row schema differs");
                ResidentSerializedAssetImage.Validate(row.Source);
                if (row.Source.NativeTypeRva != 0 && row.Source.NativeTypeRva != row.TypeRva) throw Error("serialized type cannot substitute another native body");
                if (row.Binding.Length != 0) {
                    if (!keys.Add(row.Binding)) throw Error("duplicate native source binding");
                    if (row.Binding.StartsWith(ResidentBuiltinTextureRoots.Prefix,StringComparison.Ordinal)) {
                        var aliases = originNames.Where((name,i) => data.BuiltinSlotOwners[i] == row.Binding);
                        if (row.Source.SourcePath.Length != 0 || TextureType(row.TypeRva) == null
                            || !ResidentBuiltinTextureRoots.Decode(row.Binding).SequenceEqual(aliases)) throw Error("builtin source binding differs");
                    } else if (row.Binding != SourceKey(row.Source) || row.TypeRva != row.Source.NativeTypeRva) throw Error("serialized source binding differs");
                }
                if (row.Kind.StartsWith("texture/",StringComparison.Ordinal)) { ValidateTexture(row); }
                else if (row.Kind == "physics-material-2d") {
                    if (row.TypeRva != 0x1986550 || row.Size != 72 || row.Values.Length != 8 || row.Padding.Length != 4 || row.Text != null || row.Texture != null) throw Error("native physics material payload differs");
                } else if (row.Kind == "text-asset") {
                    var text = row.Text;
                    if (row.TypeRva != 0x1968310 || row.Size != 96 || text == null || text.Bytes == null || text.Unused == null || text.Capacity < 0 || text.Capacity > 32 * 1024 * 1024
                        || text.Label < 0 || text.Bytes.Length > 32 * 1024 * 1024 - 1 || text.Inline && (text.Bytes.Length > 15 || text.Capacity != 15)
                        || !text.Inline && text.Capacity != 0 && text.Capacity < text.Bytes.Length || text.Unused.Length != (text.Inline ? 19 - text.Bytes.Length : 12)
                        || row.Values.Length != 0 || row.Padding.Length != 0 || row.Owner.Length != 0 || row.Texture != null) throw Error("native text asset payload differs");
                } else if (row.Kind != "unsupported" || row.Values.Length != 0 || row.Padding.Length != 0 || row.Text != null || row.Texture != null) throw Error("native asset payload union differs");
            }
            if (data.BuiltinSlotOwners.Any(key => key.StartsWith(ResidentBuiltinTextureRoots.Prefix,StringComparison.Ordinal) && !keys.Contains(key)))
                throw Error("builtin slot has no matching native resource row");
        }
        internal static string[] RequiredGaps(Data data)
        {
            Validate(data); var gaps = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var row in data.Rows) {
                foreach (var gap in row.Source.CoverageGaps)
                    if (gap != "serialized-image/no-source-identifier" || !row.Binding.StartsWith(ResidentBuiltinTextureRoots.Prefix,StringComparison.Ordinal))
                        gaps.Add("native-asset/" + row.TypeKey + "/" + gap);
                if (row.Binding.Length == 0) gaps.Add("native-asset/unbound-source:" + row.TypeKey);
                if (row.Texture != null) foreach(var gap in TextureGaps(row)) gaps.Add(gap);
                if (row.Kind == "unsupported") gaps.Add("native-asset/missing-native-body:" + row.TypeKey);
                if (row.Owner == "@unresolved-native-owner") gaps.Add("native-asset/unbound-pptr:" + row.TypeKey);
                if (row.Kind == "physics-material-2d" && row.Padding.Any(value => value != 0)) gaps.Add("native-asset/physics-material-padding-needs-canon:" + row.Binding);
                // Full Text D capture is not permission to free/reassign storage
                // that may have non-Object borrowers. Keep this restoration gap.
                if (row.Kind == "text-asset") gaps.Add("native-asset/text-storage-restoration-unproven");
            }
            return gaps.ToArray();
        }
        internal static void RequireCanonicalizableBodies(Data data)
        {
            var hard = RequiredGaps(data).Concat(data.CoverageGaps).Where(gap => !gap.StartsWith("native-asset/physics-material-padding-needs-canon:", StringComparison.Ordinal) && !gap.StartsWith("native-texture/padding-needs-canon:",StringComparison.Ordinal)).Distinct().ToArray();
            if (hard.Length != 0) throw Error(string.Join(";", hard));
        }
        internal static Live Canon(Live before, ResidentMutationScope scope)
        {
            RequireCanonicalizableBodies(before.Snapshot); before.RequireUnchanged(); scope.Require();
            var expected = before.Snapshot.Copy(); foreach (var row in expected.Rows) { if (row.Kind == "physics-material-2d") row.Padding = new byte[4]; if(row.Texture!=null)CanonTextureSnapshot(row.Texture); }
            expected.CoverageGaps = RequiredGaps(expected);
            scope.Mutate(() => {
                foreach(var row in before.Snapshot.Rows.Where(row=>row.Texture!=null&&TextureHasPadding(row.Texture)))CanonTexturePadding(row,before,before.Admitted);
                foreach (var row in before.Snapshot.Rows.Where(row => row.Kind == "physics-material-2d" && row.Padding.Any(value => value != 0))) {
                    before.Admitted(); M.Write(IntPtr.Add(before.Pointers[row.Binding], 68), new byte[4]);
                }
            });
            var after = Capture(before.Globals, new Dictionary<IntPtr, string>(), before.Admitted);
            RequireEqual(expected, after.Snapshot); return after;
        }
        internal static byte[] EncodeS0(Data data)
        {
            RequireCompleteBodies(data);
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8);
            foreach (var row in data.Rows) {
                // Source/type/name/owner are checked readonly Dependency. D1
                // excludes only the exact live friction/bounce float bits.
                writer.Write(row.Binding); writer.Write(row.TypeKey); writer.Write(row.Kind); writer.Write(row.Size);
                writer.Write(JsonConvert.SerializeObject(row.Source)); writer.Write(row.Persistent); writer.Write(row.NamePresent);
                writer.Write(row.Name.Length); writer.Write(row.Name); writer.Write(row.Owner); writer.Write(row.Padding);
                if(row.Texture!=null)writer.Write(JsonConvert.SerializeObject(TextureDependency(row.Texture)));
            }
            writer.Flush(); return stream.ToArray();
        }
        internal static void RequireDerivedPhysicsCoverage(Data data, ResidentPhysicsState.Data physics, ResidentPhysicsGlobals.Data globals)
        {
            RequireCompleteBodies(data); if (!data.Rows.Any(row => row.Kind == "physics-material-2d")) return;
            // SetFriction/SetBounciness in the pinned PE only clamp/store the
            // scalar, with no reverse-cache update. Fixture material values and
            // existing contact friction/restitution are independent native D.
            // The transaction must restore and independently verify BOTH full
            // physics domains after asset scalar writes, never recompute them.
            ResidentPhysicsState.RequireCompleteCoverage(physics); ResidentPhysicsGlobals.Validate(globals);
            if (globals.CoverageGaps.Length != 0) throw Error("native material derived physics/contact coverage is incomplete");
        }
        internal static void RequireCompleteBodies(Data data)
        { var gaps = RequiredGaps(data); if (gaps.Length != 0 || data.CoverageGaps.Length != 0) throw Error(string.Join(";", gaps.Concat(data.CoverageGaps))); }
        internal static void RequireBaseCoverage(Data data, ResidentObjectNativeState.Data objects)
        {
            RequireCompleteBodies(data); ResidentObjectNativeState.RequireComplete(objects);
            var owners = objects.Rows.Where(owner => owner.Kind == 0).ToDictionary(owner => owner.Owner, owner => owner.Type, StringComparer.Ordinal);
            foreach (var row in data.Rows) if (!owners.TryGetValue(row.Binding, out var type) || type != "native/" + row.TypeKey)
                throw Error("native asset Object48 is absent from independent base adapter");
        }
        internal static void RequireEqual(Data expected, Data actual)
        {
            RequireCompleteBodies(expected); RequireCompleteBodies(actual);
            if (JsonConvert.SerializeObject(expected) != JsonConvert.SerializeObject(actual)) throw Error("independent native asset directory/body readback differs");
        }
        private static InvalidDataException Error(string reason) => new InvalidDataException("ResidentNativeAssetDirectory: " + reason);
    }
}
