using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Assets close in both directions: native properties can discover managed
    // owners, and managed fields can discover more native assets. Only the
    // coordinator's managed-discovery callback can finish that second side.
    internal static class ResidentAssetPipeline
    {
        internal sealed class Data
        {
            public int Version = 1;
            public ResidentAssetBindings.AssetRecord[] Manifest = Array.Empty<ResidentAssetBindings.AssetRecord>();
            public ResidentNativeAssetState.Snapshot[] Snapshots = Array.Empty<ResidentNativeAssetState.Snapshot>();
            internal Data Copy() => new Data { Version = Version, Manifest = Manifest.Select(value => value.Copy()).ToArray(), Snapshots = Snapshots.Select(value => value.Copy()).ToArray() };
        }
        internal static Data CaptureClosure(ResidentAssetBindings assets, Func<object, string?> bind,
            Action<IReadOnlyDictionary<string, NativeObject>> discoverManaged)
        {
            if (assets == null || bind == null || discoverManaged == null) throw new ArgumentNullException("asset closure requires native and managed discovery");
            using var images = ResidentSerializedAssetImage.BeginObservation();
            var states = new Dictionary<string, ResidentNativeAssetState.Snapshot>(StringComparer.Ordinal);
            for (var pass = 0; pass <= ResidentAssetBindings.MaximumAssets; pass++) {
                assets.RequireSources(); var before = assets.SourceAssets.Count;
                // Snapshot the current view: discovery is expressly allowed to
                // add bindings while examining these actual managed owners.
                discoverManaged(new ReadOnlyDictionary<string, NativeObject>(assets.SourceAssets.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)));
                foreach (var pair in assets.SourceAssets.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray())
                    if (!states.ContainsKey(pair.Key)) states.Add(pair.Key, ResidentNativeAssetState.Capture(pair.Key, pair.Value, bind));
                assets.RequireSources();
                if (before != assets.SourceAssets.Count || states.Count != assets.SourceAssets.Count) continue;
                var result = new Data { Manifest = assets.SealManifest(), Snapshots = states.Values.OrderBy(value => value.Binding, StringComparer.Ordinal).Select(value => value.Copy()).ToArray() };
                Validate(result); return result;
            }
            throw Error("asset discovery did not converge within its bounded identity directory");
        }
        internal static void Validate(Data data)
        {
            if (data == null || data.Version != 1 || data.Manifest == null || data.Snapshots == null || data.Manifest.Length != data.Snapshots.Length)
                throw Error("asset pipeline snapshot header or state coverage differs");
            ResidentAssetBindings.ValidateManifest(data.Manifest);
            for (var index = 0; index < data.Manifest.Length; index++) {
                var record = data.Manifest[index]; var state = data.Snapshots[index];
                if (state == null || state.Type == null || state.CoverageGaps == null || state.CoverageGaps.Length > 4096
                    || state.CoverageGaps.Any(value => string.IsNullOrEmpty(value) || value.Length > 32768)) throw Error("asset native state or coverage directory is absent");
                state.Type.Validate();
                if (record.Binding != state.Binding || record.Type.Key != state.Type.Key || record.Name != state.Name || record.HideFlags != state.HideFlags)
                    throw Error("asset native state does not belong to its exact source identity");
                var actual = record.Type.Resolve();
                var stateCount = (state.Mesh == null ? 0 : 1) + (state.Material == null ? 0 : 1) + (state.Texture == null ? 0 : 1)
                    + (state.PhysicsMaterial == null ? 0 : 1) + (state.Text == null ? 0 : 1) + (state.Scriptable == null ? 0 : 1) + (state.Script == null ? 0 : 1) + (state.Shader == null ? 0 : 1);
                if (stateCount != (state.Kind == "Unsupported" ? 0 : 1)) throw Error("asset state has another typed payload union");
                if (state.Kind == "Mesh") {
                    if (actual != typeof(Mesh) || state.Mesh == null || state.Material != null || state.Texture != null) throw Error("mesh state actual type differs");
                    if (state.Mesh.Readable) ResidentNativeAssetState.ValidateMesh(state.Mesh);
                    else if (state.Mesh.BuffersCaptured || state.Mesh.VertexCount < 0 || state.Mesh.VertexCount > ResidentNativeAssetState.MaximumVertices)
                        throw Error("unreadable mesh has invented buffers or invalid dimensions");
                    if (state.Mesh.Native != null) ResidentNativeAssetState.ValidateMeshNative(state.Mesh.Native);
                } else if (state.Kind == "Material") {
                    if (actual != typeof(Material) || state.Material == null || state.Mesh != null || state.Texture != null) throw Error("material state actual type differs");
                    ValidateMaterial(state.Material);
                } else if (state.Kind == "Texture2D") {
                    if (actual != typeof(Texture2D) || state.Texture == null || state.Mesh != null || state.Material != null) throw Error("texture state actual type differs");
                    ValidateTexture(state.Texture);
                } else if (state.Kind == "PhysicsMaterial2D") {
                    if (actual != typeof(PhysicsMaterial2D) || state.PhysicsMaterial == null) throw Error("physics material state actual type differs");
                    ResidentNativeAssetState.ValidatePhysicsMaterial(state.PhysicsMaterial);
                } else if (state.Kind == "Shader") {
                    if (actual != typeof(Shader) || state.Shader == null || state.Name != state.Shader.Name) throw Error("shader actual type or native name differs");
                    ResidentNativeAssetState.ValidateShader(state.Shader);
                } else if (state.Kind == "TextAsset") {
                    if (actual != typeof(TextAsset) || state.Text == null) throw Error("TextAsset state actual type differs");
                    ResidentNativeAssetState.ValidateTextAsset(state.Text);
                } else if (state.Kind == "ScriptableObject") {
                    ResidentNativeAssetState.ValidateScriptableAsset(state);
                } else if (state.Kind == "MonoScript") {
                    if (state.Script == null || !typeof(NativeObject).IsAssignableFrom(actual) || typeof(Component).IsAssignableFrom(actual) || actual == typeof(GameObject))
                        throw Error("MonoScript managed wrapper type or payload differs");
                    ResidentNativeAssetState.ValidateMonoScript(state.Script);
                } else if (state.Kind != "Unsupported" || state.Mesh != null || state.Material != null || state.Texture != null)
                    throw Error("asset state kind is not implemented by its actual type");
            }
        }
        internal static void RequireCompleteCoverage(Data source)
        {
            Validate(source);
            foreach (var state in source.Snapshots) state.RequireCompleteCoverage();
        }
        internal static void RequireCanonicalizableCoverage(Data source)
        {
            Validate(source);
            foreach (var state in source.Snapshots) state.RequireCanonicalizableCoverage();
        }
        internal static Prepared Prepare(Data source, ResidentObjectDirectory directory, ResidentMutationScope scope, Func<bool> admitted,
            IReadOnlyDictionary<string, NativeObject>? adoptedDynamic = null)
            => new Prepared(source, directory, scope, admitted, adoptedDynamic);
        internal sealed class Prepared
        {
            private readonly Data data;
            private readonly ResidentObjectDirectory directory;
            private readonly ResidentMutationScope scope;
            private readonly Func<bool> admitted;
            private readonly ResidentAssetBindings.Prepared bindings;
            private readonly Dictionary<NativeObject, string> reverse = new Dictionary<NativeObject, string>(ResidentObjectDirectory.ReferenceComparer.Instance);
            private readonly Dictionary<int, NativeObject> instances = new Dictionary<int, NativeObject>();
            private bool applied;
            internal IReadOnlyDictionary<string, NativeObject> Assets => bindings.Assets;
            internal Prepared(Data source, ResidentObjectDirectory directory, ResidentMutationScope scope, Func<bool> admitted,
                IReadOnlyDictionary<string, NativeObject>? adoptedDynamic = null)
            {
                // No dynamic native object is allocated before complete typed
                // preflight. An adapter pending implementation cannot be
                // enabled merely by deleting its serialized gap strings.
                RequireCompleteCoverage(source); data = source.Copy();
                this.directory = directory ?? throw new ArgumentNullException(nameof(directory)); this.scope = scope ?? throw new ArgumentNullException(nameof(scope));
                this.admitted = admitted ?? throw new ArgumentNullException(nameof(admitted)); Require();
                if (!ReferenceEquals(scope.ExecutionOwnership, directory.Ownership)) throw Error("asset transaction has another execution ownership directory");
                var states = data.Snapshots.ToDictionary(value => value.Binding, StringComparer.Ordinal);
                foreach (var state in data.Snapshots) {
                    if (state.Kind == "Material" && data.Manifest.Any(record => record.Binding == state.Binding && record.Kind == "dynamic")
                        && state.Material!.ShaderBinding.StartsWith("resident-native-asset/", StringComparison.Ordinal))
                        throw Error("dynamic Material construction requires a managed Shader binding; native-only Shader construction is unsupported");
                }
                var adopted = adoptedDynamic == null ? new Dictionary<string, NativeObject>(StringComparer.Ordinal)
                    : adoptedDynamic.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                ResidentPresentationTypes.ValidateOwnedAssetAdoptions(data.Manifest, adopted);
                foreach (var pair in adopted) {
                    if (!ResidentObjectDirectory.IsLive(pair.Value) || pair.Value.GetType() != typeof(Mesh)
                        || directory.Bind(pair.Value) != null) throw Error("owned dynamic asset adoption has no exact live asset owner");
                }
                NativeObject Create(ResidentAssetBindings.AssetRecord record, Func<string, NativeObject> resolve) {
                    Require(); NativeObject? result = null;
                    scope.Mutate(() => {
                        var state = states[record.Binding];
                        if (adopted.TryGetValue(record.Binding, out var retained)) result = retained;
                        else if (state.Kind == "Mesh") result = new Mesh();
                        else if (state.Kind == "Material") result = new Material(resolve(state.Material!.ShaderBinding) as Shader ?? throw Error("dynamic material shader binding differs"));
                        else if (state.Kind == "Texture2D") {
                            var texture = state.Texture!;
                            result = new Texture2D(texture.Width, texture.Height, (TextureFormat)texture.Format, texture.MipmapCount,
                                !GraphicsFormatUtility.IsSRGBFormat((GraphicsFormat)texture.GraphicsFormat));
                        } else if (state.Kind == "PhysicsMaterial2D") result = new PhysicsMaterial2D();
                        else if (state.Kind == "TextAsset") result = new TextAsset("");
                        else if (state.Kind == "ScriptableObject") result = ResidentNativeAssetState.CreateScriptableAsset(record.Type.Resolve(), scope);
                        else if (state.Kind == "MonoScript") result = ResidentNativeAssetState.BindOrCreateRuntimeMonoScript(state, Require, scope);
                        else throw Error("dynamic native asset has no exact-type construction adapter");
                        result.name = state.Name; result.hideFlags = (HideFlags)state.HideFlags;
                    });
                    return result ?? throw Error("dynamic asset construction returned no owner");
                }
                bindings = ResidentAssetBindings.PrepareTarget(data.Manifest, directory, admitted, scope, Create);
                foreach (var pair in bindings.Assets) {
                    reverse.Add(pair.Value, pair.Key); var instance = pair.Value.GetInstanceID();
                    if (instances.ContainsKey(instance)) throw Error("asset restore collapsed native owner identities"); instances.Add(instance, pair.Value);
                    if (pair.Value is ScriptableObject scriptable) scope.Track(scriptable);
                }
            }
            internal NativeObject Resolve(string key) { Require(); return bindings.Resolve(key); }
            internal string? Bind(object value)
            {
                Require(); var resident = directory.Bind(value); if (resident != null) return resident;
                if (!(value is NativeObject owner) || !ResidentObjectDirectory.IsLive(owner)) return null;
                if (reverse.TryGetValue(owner, out var key)) return key;
                if (instances.ContainsKey(owner.GetInstanceID())) throw Error("target asset has another managed wrapper for a bound native identity");
                return null;
            }
            internal void Apply()
            {
                Require(); if (applied) throw Error("native asset application is single use"); applied = true;
                foreach (var state in data.Snapshots) scope.Mutate(() => {
                    var owner = Resolve(state.Binding);
                    if (state.Kind == "Mesh") {
                        ResidentNativeAssetState.PrepareMeshBuffers(state, (Mesh)owner, admitted).Apply();
                        ResidentNativeAssetState.ApplyMeshNativeValues(state.Mesh!, (Mesh)owner, Resolve, Bind, Require);
                    }
                    else if (state.Kind == "Material") {
                        using (var transfer = ResidentNativeAssetState.PrepareMaterialValues(state, (Material)owner, Resolve, Bind, admitted)) transfer.Apply();
                    }
                    else if (state.Kind == "Texture2D") ResidentNativeAssetState.PrepareTextureImageBuffers(state, (Texture2D)owner, admitted).Apply();
                    else if (state.Kind == "PhysicsMaterial2D") ResidentNativeAssetState.ApplyPhysicsMaterial(state, (PhysicsMaterial2D)owner, Resolve, Bind, Require);
                    else if (state.Kind == "TextAsset") ResidentNativeAssetState.ApplyTextAsset(state, (TextAsset)owner, Require);
                    else if (state.Kind == "ScriptableObject") ResidentMonoNativeState.Prepare(state.Scriptable!, owner, scope, Resolve, Bind, Require).Apply();
                    else if (state.Kind == "MonoScript") ResidentNativeAssetState.ApplyMonoScript(state, owner, Require);
                    else throw Error("the requested asset native restore stage is not complete: " + state.Kind);
                });
                Verify();
            }
            internal void Canon(Action<Mesh> rebindConsumers, Action<Mesh> verifyConsumers)
            { Require(); ResidentAssetPipeline.Canon(data, Assets, scope, admitted, rebindConsumers, verifyConsumers, Resolve, Bind,
                mesh => ResidentRendererState.RequireMeshConsumersCovered(directory, mesh)); }
            internal void Verify()
            {
                Require(); bindings.RequireIdentity();
                foreach (var state in data.Snapshots) {
                    var observed = ResidentNativeAssetState.Capture(state.Binding, Resolve(state.Binding), Bind);
                    if (state.Name != observed.Name || state.HideFlags != observed.HideFlags || state.Type.Key != observed.Type.Key || state.Kind != observed.Kind)
                        throw Error("native asset identity readback differs");
                    if (state.Kind == "Mesh" && !ResidentNativeAssetState.EncodeMesh(state.Mesh!).SequenceEqual(ResidentNativeAssetState.EncodeMesh(observed.Mesh!)))
                        throw Error("native asset geometry readback differs");
                    if (state.Kind == "Mesh" && !ResidentNativeAssetState.EncodeMeshNativeValues(state.Mesh!.Native!).SequenceEqual(ResidentNativeAssetState.EncodeMeshNativeValues(observed.Mesh!.Native!)))
                        throw Error("native asset retained mesh metadata readback differs");
                    if (state.Kind == "Material" && !ResidentNativeAssetState.EncodeMaterialValues(state.Material!).SequenceEqual(ResidentNativeAssetState.EncodeMaterialValues(observed.Material!)))
                        throw Error("native asset material values readback differs");
                    if (state.Kind == "Texture2D" && !ResidentNativeAssetState.EncodeTextureImage(state.Texture!).SequenceEqual(ResidentNativeAssetState.EncodeTextureImage(observed.Texture!)))
                        throw Error("native asset texture image readback differs");
                    if (state.Kind == "PhysicsMaterial2D" && !ResidentNativeAssetState.EncodePhysicsMaterial(state.PhysicsMaterial!).SequenceEqual(ResidentNativeAssetState.EncodePhysicsMaterial(observed.PhysicsMaterial!)))
                        throw Error("native asset physics material readback differs");
                    if (state.Kind == "TextAsset" && !state.Text!.Bytes.SequenceEqual(observed.Text!.Bytes)) throw Error("native TextAsset bytes readback differs");
                    if (state.Kind == "ScriptableObject") ResidentMonoNativeState.RequireEqual(state.Scriptable!, observed.Scriptable!);
                    if (state.Kind == "MonoScript") ResidentNativeAssetState.RequireMonoScriptEqual(state.Script!, observed.Script!);
                    if (state.Kind != "Mesh" && state.Kind != "Material" && state.Kind != "Texture2D" && state.Kind != "PhysicsMaterial2D" && state.Kind != "TextAsset" && state.Kind != "ScriptableObject" && state.Kind != "MonoScript") throw Error("the requested asset native verification stage is not complete: " + state.Kind);
                }
            }
            private void Require() { scope.Require(); if (!admitted()) throw Error("native asset transaction lost admission"); }
        }
        internal static void Canon(Data source, IReadOnlyDictionary<string, NativeObject> owners, ResidentMutationScope scope, Func<bool> admitted,
            Action<Mesh> rebindConsumers, Action<Mesh> verifyConsumers, Func<string, NativeObject>? resolve = null, Func<object, string?>? bind = null, Action<Mesh>? requireConsumers = null)
        {
            RequireCanonicalizableCoverage(source);
            if (owners == null || scope == null || admitted == null || rebindConsumers == null || verifyConsumers == null) throw new ArgumentNullException("asset Canon requires its current owners and dependencies");
            // Type coverage is checked before the first native mutation.
            foreach (var state in source.Snapshots) {
                if (!owners.TryGetValue(state.Binding, out var owner) || !ResidentObjectDirectory.IsLive(owner) || owner.GetType() != state.Type.Resolve())
                    throw Error("asset common Canon owner identity differs: " + state.Binding);
                if (state.Kind == "Mesh" && state.Mesh != null && bind != null && requireConsumers != null) { requireConsumers((Mesh)owner); continue; }
                if (state.Kind == "Material" && state.Material != null && resolve != null && bind != null) {
                    ResidentNativeAssetState.RequireColdMaterialDomain(state, (Material)owner); continue;
                }
                if (state.Kind == "PhysicsMaterial2D" && state.PhysicsMaterial != null && bind != null) { ResidentNativeAssetState.ValidatePhysicsMaterial(state.PhysicsMaterial); continue; }
                if (state.Kind == "TextAsset" && state.Text != null) { ResidentNativeAssetState.ValidateTextAsset(state.Text); continue; }
                if (state.Kind == "ScriptableObject" && state.Scriptable != null && bind != null) {
                    ResidentNativeAssetState.ValidateScriptableAsset(state); ResidentMonoNativeState.RequireComplete(state.Scriptable); scope.Track((ScriptableObject)owner); continue;
                }
                if (state.Kind == "MonoScript" && state.Script != null) { ResidentNativeAssetState.RequireMonoScriptComplete(state.Script); continue; }
                throw Error("asset common Canon stage or reference dependencies are not complete: " + state.Kind);
            }
            var receipts = new List<ResidentNativeAssetState.ColdMeshReceipt>();
            var materialReceipts = new List<ResidentNativeAssetState.ColdMaterialReceipt>();
            foreach (var state in source.Snapshots) {
                if (state.Kind == "Mesh") receipts.Add(ResidentNativeAssetState.CanonColdMesh(state, (Mesh)owners[state.Binding], scope, admitted, rebindConsumers, verifyConsumers, bind!));
                else if (state.Kind == "Material") materialReceipts.Add(ResidentNativeAssetState.CanonColdMaterial(state, (Material)owners[state.Binding], scope, admitted, resolve!, bind!));
                else if (state.Kind == "TextAsset") scope.Mutate(() => ResidentNativeAssetState.ApplyTextAsset(state, (TextAsset)owners[state.Binding], scope.Require));
                else if (state.Kind == "ScriptableObject") scope.Mutate(() => ResidentMonoNativeState.Canon(owners[state.Binding], scope, scope.Require));
                else if (state.Kind == "MonoScript") scope.Mutate(() => ResidentNativeAssetState.ApplyMonoScript(state, owners[state.Binding], scope.Require));
            }
            foreach (var receipt in receipts) receipt.Verify();
            foreach (var receipt in materialReceipts) receipt.Verify();
            foreach (var state in source.Snapshots.Where(value => value.Kind == "ScriptableObject"))
                ResidentMonoNativeState.RequireEqual(state.Scriptable!, ResidentMonoNativeState.Capture(owners[state.Binding], bind!));
            foreach (var state in source.Snapshots.Where(value => value.Kind == "PhysicsMaterial2D")) {
                var observed = ResidentNativeAssetState.Capture(state.Binding, owners[state.Binding], bind!);
                if (state.Name != observed.Name || state.HideFlags != observed.HideFlags
                    || !ResidentNativeAssetState.EncodePhysicsMaterial(state.PhysicsMaterial!).SequenceEqual(ResidentNativeAssetState.EncodePhysicsMaterial(observed.PhysicsMaterial!)))
                    throw Error("common Canon changed retained physics material values");
            }
        }
        internal static void ValidateMaterial(ResidentNativeAssetState.MaterialState state)
        {
            if (state == null || state.ShaderBinding == null || state.RuntimeShaderBinding == null || state.OwnerBinding == null || state.SerializedKeywords == null
                || state.SerializedKeywords.Length > 16384 || state.SerializedKeywords.IndexOf('\0') >= 0
                || state.Keywords == null || state.DisabledPasses == null || state.SavedProperties == null || state.OverrideTags == null
                || state.Keywords.Length > 4096 || state.DisabledPasses.Length > 4096 || state.SavedProperties.Length > 12288
                || state.OverrideTags.Length > 4096
                || state.Keywords.Concat(state.DisabledPasses).Any(value => value == null || value.Length > 16384 || value.IndexOf('\0') >= 0)
                || state.DisabledPasses.Any(string.IsNullOrEmpty) || state.DisabledPasses.Distinct(StringComparer.Ordinal).Count() != state.DisabledPasses.Length
                || state.HasRuntimeSheet != (state.RuntimeProperties != null)) throw Error("material state directory differs");
            if (state.RuntimeProperties != null) ResidentShaderPropertySheet.Validate(state.RuntimeProperties);
            var previousTag = "";
            foreach (var value in state.OverrideTags) {
                if (value == null || string.IsNullOrEmpty(value.Name) || string.IsNullOrEmpty(value.Value) || value.Name.Length > 16384 || value.Value.Length > 16384
                    || value.Name.IndexOf('\0') >= 0 || value.Value.IndexOf('\0') >= 0 || StringComparer.Ordinal.Compare(previousTag, value.Name) >= 0)
                    throw Error("material override tag identity, value or order differs");
                previousTag = value.Name;
            }
            var previous = ""; var previousKind = -1;
            foreach (var value in state.SavedProperties) {
                if (value == null || string.IsNullOrEmpty(value.Name) || value.Name.Length > 16384 || value.Name.IndexOf('\0') >= 0 || value.TextureBinding == null
                    || value.Kind < 0 || value.Kind > 2 || value.Value == null || value.Value.Length != (value.Kind == 0 ? 4 : 16)
                    || value.Kind != 2 && value.TextureBinding.Length != 0 || value.Kind < previousKind || value.Kind == previousKind && StringComparer.Ordinal.Compare(previous, value.Name) >= 0)
                    throw Error("material saved property identity, order or value differs");
                previous = value.Name; previousKind = value.Kind;
            }
        }
        internal static void ValidateTexture(ResidentNativeAssetState.TextureState state)
        {
            if (state == null || state.Width < 1 || state.Height < 1 || state.Width > 65536 || state.Height > 65536 || state.MipmapCount < 1 || state.MipmapCount > 32
                || state.CpuBytes == null || state.CpuBytes.Length > ResidentNativeAssetState.MaximumBytes || state.CpuDataPresent != (state.CpuBytes.Length != 0)
                || (ulong)state.CpuBytes.Length > state.CpuCapacity) throw Error("texture image dimensions or captured CPU storage differ");
            if (state.CpuDataPresent && (state.CpuWidth < 1 || state.CpuHeight < 1 || state.CpuWidth > 65536 || state.CpuHeight > 65536
                || state.CpuImageCount < 1 || state.CpuImageCount > 4096 || state.CpuMipCount < 1 || state.CpuMipCount > 32
                || state.CpuImageSize < 1 || state.CpuImageSize > (ulong)state.CpuBytes.Length
                || state.CpuImageSize * (ulong)state.CpuImageCount != (ulong)state.CpuBytes.Length)) throw Error("texture CPU image stride, count or mip directory differs");
            if (state.Native != null) ResidentNativeAssetState.ValidateTextureNative(state.Native);
        }
        private static InvalidDataException Error(string reason) => new InvalidDataException("Resident asset pipeline: " + reason);
    }
}
