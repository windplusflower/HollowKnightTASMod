using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Native asset identities are captured from the engine's actual resource
    // remapper. Names are descriptive state, never a fallback lookup key.
    // Unmapped assets are explicit dynamic nodes supplied to type adapters.
    internal sealed class ResidentAssetBindings
    {
        internal const int MaximumAssets = 100000;
        private readonly ResidentObjectDirectory directory;
        private readonly LoadingAssetProvenanceObservation provenance = new LoadingAssetProvenanceObservation();
        private readonly Dictionary<NativeObject, AssetRecord> records = new Dictionary<NativeObject, AssetRecord>(ResidentObjectDirectory.ReferenceComparer.Instance);
        private readonly Dictionary<string, NativeObject> sources = new Dictionary<string, NativeObject>(StringComparer.Ordinal);
        private readonly Dictionary<int, NativeObject> instances = new Dictionary<int, NativeObject>();
        private readonly Dictionary<Type, ResidentObjectDirectory.TypeRecord> sourceTypes = new Dictionary<Type, ResidentObjectDirectory.TypeRecord>();
        private readonly Dictionary<NativeObject, Type> sourceOwnerTypes = new Dictionary<NativeObject, Type>(ResidentObjectDirectory.ReferenceComparer.Instance);
        private readonly HashSet<int> residentGameObjectIds;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr QueryComponent(IntPtr owner, IntPtr type);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr NativeScene(IntPtr transform);
        private static QueryComponent? queryComponent;
        private static NativeScene? getNativeScene;
        private int dynamicCount;
        private bool sealedManifest;
        private readonly IReadOnlyDictionary<string, NativeObject> sourceView;
        internal IReadOnlyDictionary<string, NativeObject> SourceAssets => sourceView;

        internal sealed class AssetRecord
        {
            public string Binding = "", Kind = "", SourcePath = "", Name = "";
            public ResidentObjectDirectory.TypeRecord Type = new ResidentObjectDirectory.TypeRecord();
            public long LocalIdentifier;
            public int DynamicOrdinal = -1, HideFlags;
            internal AssetRecord Copy() => new AssetRecord { Binding = Binding, Kind = Kind, SourcePath = SourcePath, Name = Name,
                Type = Type.Copy(), LocalIdentifier = LocalIdentifier, DynamicOrdinal = DynamicOrdinal, HideFlags = HideFlags };
        }
        private sealed class Origin
        {
            internal string Kind = "", Path = "";
            internal long Identifier;
        }
        internal ResidentAssetBindings(ResidentObjectDirectory directory)
        {
            this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
            sourceView = new ReadOnlyDictionary<string, NativeObject>(sources);
            residentGameObjectIds = new HashSet<int>(directory.NativeBindings.Values.OfType<GameObject>().Select(owner => owner.GetInstanceID()));
        }

        internal static bool IsSharedBinding(string? key)
            => key != null && key.StartsWith("resident-asset/serialized/", StringComparison.Ordinal);
        internal bool IsDynamic(NativeObject owner) => records[owner].Kind == "dynamic";

        internal string? Bind(object value)
        {
            if (!(value is NativeObject owner) || !ResidentObjectDirectory.IsLive(owner)) return null;
            if (owner is RenderTexture rt && ResidentNativeAssetReference.IsTemporaryRenderTexture(ResidentNativeMemory.Owner(rt))) return null;
            var resident = directory.Bind(owner);
            if (resident != null) return resident;
            if (records.TryGetValue(owner, out var recorded)) {
                RequireSource(owner, recorded); return recorded.Binding;
            }
            if (sealedManifest) throw Error("an asset was discovered after its manifest was sealed");
            RequireAssetSceneOwnership(owner);
            if (records.Count >= MaximumAssets) throw Error("asset count exceeds its bound");
            var instance = owner.GetInstanceID();
            if (instances.TryGetValue(instance, out var alias) && !ReferenceEquals(alias, owner)) throw Error("an asset native instance has multiple managed wrappers");
            var origin = ReadOrigin(provenance, instance);
            var ownerType = owner.GetType();
            if (!sourceTypes.TryGetValue(ownerType, out var typeRecord)) sourceTypes.Add(ownerType, typeRecord = ResidentObjectDirectory.TypeRecord.Capture(ownerType));
            sourceOwnerTypes.Add(owner, ownerType);
            var record = new AssetRecord { Type = typeRecord.Copy(), Name = owner.name,
                HideFlags = (int)owner.hideFlags, Kind = origin.Kind, SourcePath = origin.Path, LocalIdentifier = origin.Identifier };
            if (origin.Kind == "serialized") record.Binding = SerializedKey(record.Type, origin.Path, origin.Identifier);
            else if (origin.Kind == "unmapped") {
                record.Kind = "dynamic"; record.DynamicOrdinal = dynamicCount++;
                record.Binding = DynamicKey(record.Type, record.DynamicOrdinal);
            } else throw Error("unrecognized native asset provenance kind");
            if (sources.ContainsKey(record.Binding)) throw Error("serialized asset provenance resolves to multiple live owners: " + record.Binding);
            records.Add(owner, record); sources.Add(record.Binding, owner); instances[instance] = owner;
            return record.Binding;
        }
        private void RequireAssetSceneOwnership(NativeObject owner)
        {
            if (!(owner is Component) && !(owner is GameObject)) return;
            var native = ResidentNativeMemory.Owner(owner);
            var gameObject = owner is GameObject ? native : ResidentNativeMemory.Ptr(IntPtr.Add(native, 48));
            if (gameObject == IntPtr.Zero) throw Error("Component has no native GameObject owner");
            var header = ResidentNativeMemory.Read(gameObject, 16); var id = ResidentNativeMemory.I32(header, 8);
            if (residentGameObjectIds.Contains(id)) throw Error("a resident scene owner appeared as an asset");
            if (ResidentLoadedAssetIndex.RegisteredPointer(id, out var registered) != gameObject) throw Error("asset GameObject has another native registration");
            var engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
            ResidentLoadedAssetIndex.RequireCode(engine, 0x580130, 58, "88c12a257d8d4affce118c5121493a7530ce07391be806aa8d6170a0156c2853");
            ResidentLoadedAssetIndex.RequireCode(engine, 0x7e6a60, 87, "35de8f205df5c58bd435dd56b9b0fb2e66de43e4440984ef9f60bffae6f949e1");
            var query = queryComponent ?? (queryComponent = (QueryComponent)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0x580130), typeof(QueryComponent)));
            var getScene = getNativeScene ?? (getNativeScene = (NativeScene)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0x7e6a60), typeof(NativeScene)));
            var transform = query(gameObject, IntPtr.Add(engine, 0x19491a0));
            if (transform == IntPtr.Zero || ResidentNativeMemory.Ptr(IntPtr.Add(transform, 48)) != gameObject) throw Error("asset native Transform/GameObject backlink differs");
            var scene = getScene(transform);
            var sceneHandle = scene == IntPtr.Zero ? 0 : ResidentNativeMemory.I32(ResidentNativeMemory.Read(IntPtr.Add(scene, 8), 4), 0);
            if (sceneHandle != 0)
                throw Error("a native scene owner outside the complete directory appeared as an asset");
            if (query(gameObject, IntPtr.Add(engine, 0x19491a0)) != transform || getScene(transform) != scene
                || scene != IntPtr.Zero && ResidentNativeMemory.I32(ResidentNativeMemory.Read(IntPtr.Add(scene, 8), 4), 0) != sceneHandle
                || !header.SequenceEqual(ResidentNativeMemory.Read(gameObject, 16))
                || owner is Component && ResidentNativeMemory.Ptr(IntPtr.Add(native, 48)) != gameObject)
                throw Error("asset native scene ownership changed");
            registered();
        }
        internal AssetRecord[] SealManifest()
        {
            RequireSources(); sealedManifest = true;
            return CopyManifest();
        }
        internal AssetRecord[] CopyManifest()
        {
            var result = records.Values.OrderBy(record => record.Binding, StringComparer.Ordinal).Select(record => record.Copy()).ToArray();
            ValidateManifest(result); return result;
        }
        internal void RequireSources()
        {
            // Metadata is immutable per actual Type; independently revalidate
            // once per distinct Type at each inventory guard, never per alias.
            foreach (var pair in sourceTypes) if (ResidentObjectDirectory.TypeRecord.Capture(pair.Key).Key != pair.Value.Key)
                throw Error("source asset type metadata changed within capture");
            foreach (var pair in records) RequireSource(pair.Key, pair.Value);
        }
        private void RequireSource(NativeObject owner, AssetRecord record)
        {
            if (!ResidentObjectDirectory.IsLive(owner) || !sourceOwnerTypes.TryGetValue(owner, out var type) || owner.GetType() != type
                || owner.name != record.Name || (int)owner.hideFlags != record.HideFlags)
                throw Error("source asset identity or metadata changed within capture: " + record.Binding);
        }

        internal static Prepared PrepareTarget(AssetRecord[] manifest, ResidentObjectDirectory directory, Func<bool> admitted, ResidentMutationScope scope,
            Func<AssetRecord, Func<string, NativeObject>, NativeObject>? createDynamic, IReadOnlyDictionary<string, NativeObject>? observedAssets = null)
            => new Prepared(manifest, directory, admitted, scope, createDynamic, observedAssets);

        internal sealed class Prepared
        {
            private readonly AssetRecord[] manifest;
            private readonly ResidentObjectDirectory directory;
            private readonly Func<bool> admitted;
            private readonly Dictionary<string, NativeObject> owners = new Dictionary<string, NativeObject>(StringComparer.Ordinal);
            private readonly Dictionary<string, Type> types = new Dictionary<string, Type>(StringComparer.Ordinal);
            private readonly Dictionary<string, AssetRecord> records;
            private readonly Dictionary<int, NativeObject> instances = new Dictionary<int, NativeObject>();
            private readonly HashSet<string> creating = new HashSet<string>(StringComparer.Ordinal);
            private readonly Func<AssetRecord, Func<string, NativeObject>, NativeObject>? createDynamic;
            private bool allocationComplete;
            internal IReadOnlyDictionary<string, NativeObject> Assets { get; }

            internal Prepared(AssetRecord[] source, ResidentObjectDirectory directory, Func<bool> admitted, ResidentMutationScope scope,
                Func<AssetRecord, Func<string, NativeObject>, NativeObject>? createDynamic, IReadOnlyDictionary<string, NativeObject>? observedAssets = null)
            {
                ValidateManifest(source);
                manifest = source.Select(record => record.Copy()).ToArray();
                this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
                this.admitted = admitted ?? throw new ArgumentNullException(nameof(admitted)); this.createDynamic = createDynamic;
                records = manifest.ToDictionary(record => record.Binding, StringComparer.Ordinal);
                Assets = new ReadOnlyDictionary<string, NativeObject>(owners);
                Require();
                foreach (var record in manifest) {
                    if (!types.ContainsKey(record.Type.Key)) {
                        var type = record.Type.Resolve();
                        if (!typeof(NativeObject).IsAssignableFrom(type)) throw Error("asset type is not a Unity object: " + type.FullName);
                        types.Add(record.Type.Key, type);
                    }
                }
                foreach (var record in manifest.Where(record => record.Kind == "serialized")) {
                    Require();
                    var owner = observedAssets != null && observedAssets.TryGetValue(record.Binding, out var observed)
                        ? observed : ResidentTargetAssetLookup.Serialized(record, scope);
                    if (directory.Bind(owner) != null) throw Error("serialized asset aliases a resident owner: " + record.Binding);
                    Add(record.Binding, owner);
                }
                foreach (var record in manifest.Where(record => record.Kind == "dynamic")) Resolve(record.Binding);
                allocationComplete = true; RequireIdentity();
            }
            internal NativeObject Resolve(string binding)
            {
                Require();
                if (directory.NativeBindings.ContainsKey(binding)) return directory.Resolve(binding);
                if (ResidentExecutionOwnership.IsExecutionBinding(binding)) {
                    foreach (var pair in directory.NativeBindings)
                        if (ResidentExecutionOwnership.SameExecutionKey(pair.Key, binding)) return pair.Value;
                }
                if (owners.TryGetValue(binding, out var owner)) {
                    if (!ResidentObjectDirectory.IsLive(owner)) throw Error("target asset was destroyed after binding: " + binding);
                    return owner;
                }
                if (allocationComplete || !records.TryGetValue(binding, out var record) || record.Kind != "dynamic" || createDynamic == null)
                    throw Error("asset has no resolved source identity or dynamic type adapter: " + binding);
                if (!creating.Add(binding)) throw Error("dynamic asset construction dependencies are cyclic: " + binding);
                try {
                    var created = createDynamic(record.Copy(), Resolve);
                    Require();
                    if (!ResidentObjectDirectory.IsLive(created) || created.GetType() != types[record.Type.Key] || directory.Bind(created) != null)
                        throw Error("dynamic asset adapter returned an absent, wrong-type or resident owner: " + binding);
                    Add(binding, created); return created;
                } finally { creating.Remove(binding); }
            }
            private void Add(string binding, NativeObject owner)
            {
                if (owners.ContainsKey(binding)) throw Error("asset provenance is ambiguous in the loaded target: " + binding);
                var instance = owner.GetInstanceID();
                if (instances.TryGetValue(instance, out var previous))
                    throw Error(ReferenceEquals(previous, owner) ? "two source asset identities collapsed to one target owner" : "target native asset has multiple managed wrappers");
                owners.Add(binding, owner); instances.Add(instance, owner);
            }
            internal void RequireIdentity()
            {
                Require();
                if (!allocationComplete || creating.Count != 0 || owners.Count != manifest.Length) throw Error("target asset allocation directory is incomplete");
                var provenance = new LoadingAssetProvenanceObservation();
                foreach (var record in manifest) {
                    if (!owners.TryGetValue(record.Binding, out var owner) || !ResidentObjectDirectory.IsLive(owner)
                        || owner.GetType() != types[record.Type.Key] || !instances.TryGetValue(owner.GetInstanceID(), out var original) || !ReferenceEquals(owner, original))
                        throw Error("target asset binding identity changed: " + record.Binding);
                    var origin = ReadOrigin(provenance, owner.GetInstanceID());
                    if (record.Kind == "serialized" && (origin.Kind != "serialized" || origin.Path != record.SourcePath || origin.Identifier != record.LocalIdentifier)
                        || record.Kind == "dynamic" && origin.Kind != "unmapped")
                        throw Error("target asset provenance changed: " + record.Binding);
                }
            }
            private void Require() { if (!admitted()) throw Error("asset preparation lost its paused restore transaction"); }
        }

        internal static void ValidateManifest(AssetRecord[] manifest)
        {
            if (manifest == null || manifest.Length > MaximumAssets) throw Error("asset manifest is absent or exceeds its bound");
            var bindings = new HashSet<string>(StringComparer.Ordinal); var dynamicOrdinals = new List<int>();
            var validatedTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in manifest) {
                if (record == null || record.Type == null || record.Binding == null || record.Name == null || record.SourcePath == null
                    || record.Binding.Length == 0 || record.Binding.Length > 262144 || record.Name.Length > 262144 || record.Name.IndexOf('\0') >= 0
                    || record.SourcePath.Length > 8192 || record.SourcePath.IndexOf('\0') >= 0 || !bindings.Add(record.Binding))
                    throw Error("asset manifest row is absent, duplicate or oversized");
                // Resolve performs only loaded-module metadata lookup; it does
                // not load an assembly, invoke a constructor or touch Unity.
                // Validate the record before reading its recursively composed
                // key, then resolve each complete type identity only once.
                record.Type.Validate();
                if (validatedTypes.Add(record.Type.Key) && !typeof(NativeObject).IsAssignableFrom(record.Type.Resolve())) throw Error("asset manifest type is not native");
                if (record.Kind == "serialized") {
                    if (record.SourcePath.Length == 0 || record.LocalIdentifier == 0 || record.DynamicOrdinal != -1
                        || record.Binding != SerializedKey(record.Type, record.SourcePath, record.LocalIdentifier)) throw Error("serialized asset identity row differs");
                } else if (record.Kind == "dynamic") {
                    if (record.SourcePath.Length != 0 || record.LocalIdentifier != 0 || record.DynamicOrdinal < 0
                        || record.Binding != DynamicKey(record.Type, record.DynamicOrdinal)) throw Error("dynamic asset identity row differs");
                    dynamicOrdinals.Add(record.DynamicOrdinal);
                } else throw Error("asset manifest kind differs");
            }
            if (!manifest.Select(record => record.Binding).SequenceEqual(manifest.Select(record => record.Binding).OrderBy(binding => binding, StringComparer.Ordinal))
                || !dynamicOrdinals.OrderBy(value => value).SequenceEqual(Enumerable.Range(0, dynamicOrdinals.Count)))
                throw Error("asset manifest order or dynamic identity sequence differs");
        }
        internal static string SerializedKey(ResidentObjectDirectory.TypeRecord type, string path, long identifier)
            => "resident-asset/serialized/" + ResidentObjectDirectory.TextKey(type.Key) + ResidentObjectDirectory.TextKey(path) + identifier.ToString(CultureInfo.InvariantCulture);
        internal static string DynamicKey(ResidentObjectDirectory.TypeRecord type, int ordinal)
            => "resident-asset/dynamic/" + ResidentObjectDirectory.TextKey(type.Key) + ordinal.ToString(CultureInfo.InvariantCulture);
        internal static bool IsUnmapped(NativeObject owner, LoadingAssetProvenanceObservation provenance)
            => ResidentObjectDirectory.IsLive(owner) && ReadOrigin(provenance, owner.GetInstanceID()).Kind == "unmapped";
        private static Origin ReadOrigin(LoadingAssetProvenanceObservation reader, int instance)
        {
            var value = reader.Capture(instance); var type = value.GetType();
            var kind = type.GetProperty("kind")?.GetValue(value, null) as string;
            var path = type.GetProperty("sourcePath")?.GetValue(value, null) as string;
            var identifier = type.GetProperty("localIdentifier")?.GetValue(value, null);
            if (kind == null || path == null || !(identifier is long localIdentifier) || kind != "serialized" && kind != "unmapped"
                || kind == "serialized" && (path.Length == 0 || localIdentifier == 0) || kind == "unmapped" && (path.Length != 0 || localIdentifier != 0))
                throw Error("native resource remapper returned an invalid provenance record");
            return new Origin { Kind = kind, Path = path, Identifier = localIdentifier };
        }
        private static InvalidDataException Error(string message) => new InvalidDataException("Resident assets: " + message);
    }
}
