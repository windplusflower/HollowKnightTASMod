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
            public int Version = 4;
            public string[] SharedMutable = Array.Empty<string>();
            public ResidentAssetBindings.AssetRecord[] Manifest = Array.Empty<ResidentAssetBindings.AssetRecord>();
            public ResidentNativeAssetState.Snapshot[] Snapshots = Array.Empty<ResidentNativeAssetState.Snapshot>();
            internal Data Copy() => new Data { Version = Version, SharedMutable = (string[])SharedMutable.Clone(), Manifest = Manifest.Select(value => value.Copy()).ToArray(), Snapshots = Snapshots.Select(value => value.Copy()).ToArray() };
        }
        internal static Data CaptureClosure(ResidentAssetBindings assets, Func<object, string?> bind,
            Func<IReadOnlyDictionary<string, NativeObject>, IReadOnlyCollection<string>> discoverManaged)
        {
            if (assets == null || bind == null || discoverManaged == null) throw new ArgumentNullException("asset closure requires native and managed discovery");
            var states = new Dictionary<string, ResidentNativeAssetState.Snapshot>(StringComparer.Ordinal);
            var mutable = new HashSet<string>(StringComparer.Ordinal);
            for (var pass = 0; pass <= ResidentAssetBindings.MaximumAssets; pass++) {
                assets.RequireSources();
                var before = assets.SourceAssets.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                // Snapshot the current view: discovery is expressly allowed to
                // add bindings while examining these actual managed owners.
                var covered = new HashSet<string>(discoverManaged(new ReadOnlyDictionary<string, NativeObject>(before)), StringComparer.Ordinal);
                foreach (var pair in assets.SourceAssets.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray()) {
                    bool fontCache = pair.Value is Font font && font.dynamic;
                    bool clipDependency = pair.Value.GetType() == typeof(AnimationClip);
                    if ((assets.IsDynamic(pair.Value) || fontCache || clipDependency || mutable.Contains(pair.Key)) && !states.ContainsKey(pair.Key))
                        states.Add(pair.Key,ResidentNativeAssetState.Capture(pair.Key,pair.Value,bind));
                    if (clipDependency && ResidentAssetBindings.IsSharedBinding(pair.Key)) mutable.Add(pair.Key);
                    if (fontCache) {
                        var cache = states[pair.Key].Font ?? throw Error("dynamic Font lacks its typed native cache");
                        if (ResidentAssetBindings.IsSharedBinding(pair.Key)) mutable.Add(pair.Key);
                        foreach (var reference in new[] {(cache.Material,typeof(Material)),(cache.Texture,typeof(Texture2D))}) {
                            if (reference.Item1.Length == 0) continue;
                            if (!assets.SourceAssets.TryGetValue(reference.Item1,out var owner) || owner.GetType() != reference.Item2)
                                throw Error("Font cache mutable dependency has no actual referenced wrapper");
                            if (ResidentAssetBindings.IsSharedBinding(reference.Item1)) mutable.Add(reference.Item1);
                        }
                    }
                }
                assets.RequireSources();
                // Shared assets are identity dependencies. Their appearance cannot add
                // managed instance fields or coroutine owners to this closure.
                // Dynamic owners discovered by the graph already have explicit roots.
                var pending = assets.SourceAssets.Where(pair => !ResidentAssetBindings.IsSharedBinding(pair.Key)
                    && (!covered.Contains(pair.Key) || !states.ContainsKey(pair.Key))).ToArray();
                bool mutablePending = mutable.Any(key => !states.ContainsKey(key));
                bool fontPending = assets.SourceAssets.Any(pair => pair.Value is Font font && font.dynamic && !states.ContainsKey(pair.Key));
                if (pending.Length != 0 || mutablePending || fontPending || assets.SourceAssets.Any(pair=>pair.Value.GetType()==typeof(AnimationClip) && !states.ContainsKey(pair.Key)) || assets.SourceAssets.Count != before.Count) {
                    ResidentFailureEvidence.Mark("asset closure extension: " + string.Join(",", pending.GroupBy(pair => pair.Value.GetType().FullName)
                        .Select(group => group.Key + "=" + group.Count())));
                    continue;
                }
                var result = new Data { SharedMutable = mutable.OrderBy(key=>key,StringComparer.Ordinal).ToArray(), Manifest = assets.SealManifest(), Snapshots = states.Values.OrderBy(value => value.Binding, StringComparer.Ordinal).Select(value => value.Copy()).ToArray() };
                ResidentFailureEvidence.Mark("dynamic asset closure: " + string.Join(",", result.Snapshots.GroupBy(value => value.Kind)
                    .Select(group => group.Key + "=" + group.Count())) + ";gaps=" + string.Join(",", result.Snapshots.SelectMany(value => value.CoverageGaps).Distinct().Take(20)));
                Validate(result); return result;
            }
            throw Error("asset discovery did not converge within its bounded identity directory");
        }
        internal static void Validate(Data data)
        {
            if (data == null || (data.Version != 2 && data.Version != 3 && data.Version != 4) || data.Manifest == null || data.Snapshots == null)
                throw Error("asset pipeline snapshot header or state coverage differs");
            ResidentAssetBindings.ValidateManifest(data.Manifest);
            ValidateSharedMutable(data);
            foreach (var record in data.Manifest.Where(record=>record.Type.Resolve()==typeof(AnimationClip))) {
                var clips = data.Snapshots.Where(state=>state != null && state.Binding==record.Binding).ToArray();
                if (data.Version != 4 || clips.Length != 1 || clips[0].Kind != "AnimationClip" || clips[0].Clip == null)
                    throw Error("referenced Clip lacks its version4 typed conditional dependency payload");
            }
            var mutable = new HashSet<string>(data.SharedMutable,StringComparer.Ordinal);
            var dynamicAssets = data.Manifest.Where(record => record.Kind == "dynamic" || mutable.Contains(record.Binding)).ToArray();
            if (dynamicAssets.Length != data.Snapshots.Length) throw Error("dynamic asset state coverage differs");
            for (var index = 0; index < dynamicAssets.Length; index++) {
                var record = dynamicAssets[index]; var state = data.Snapshots[index];
                if (state == null || state.Type == null || state.CoverageGaps == null || state.CoverageGaps.Length > 4096
                    || state.CoverageGaps.Any(value => string.IsNullOrEmpty(value) || value.Length > 32768)) throw Error("asset native state or coverage directory is absent");
                state.Type.Validate();
                if (record.Binding != state.Binding || record.Type.Key != state.Type.Key || record.Name != state.Name || record.HideFlags != state.HideFlags)
                    throw Error("asset native state does not belong to its exact source identity");
                var actual = record.Type.Resolve();
                var stateCount = (state.Mesh == null ? 0 : 1) + (state.Material == null ? 0 : 1) + (state.Texture == null ? 0 : 1)
                    + (state.RenderTarget == null ? 0 : 1) + (state.Sprite == null ? 0 : 1) + (state.PhysicsMaterial == null ? 0 : 1) + (state.Text == null ? 0 : 1) + (state.Scriptable == null ? 0 : 1) + (state.Script == null ? 0 : 1) + (state.Shader == null ? 0 : 1) + (state.Font == null ? 0 : 1) + (state.Clip == null ? 0 : 1);
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
                } else if (state.Kind == "RenderTexture") {
                    if (actual != typeof(RenderTexture)) throw Error("render target actual type differs");
                    ResidentNativeAssetState.ValidateRenderTarget(state.RenderTarget!);
                } else if (state.Kind == "Sprite") {
                    if (actual != typeof(Sprite)) throw Error("sprite actual type differs");
                    ResidentNativeAssetState.ValidateSprite(state.Sprite!);
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
                } else if (state.Kind == "Font") {
                    if (data.Version != 4 || actual != typeof(Font) || state.Font == null) throw Error("Font cache actual type or asset version differs");
                    ResidentFontCacheState.Validate(state.Font);
                } else if (state.Kind == "AnimationClip") {
                    if (data.Version != 4 || actual != typeof(AnimationClip) || state.Clip == null) throw Error("Clip actual type or asset version differs");
                    ResidentClipDependencyState.Validate(state.Clip);
                } else if (state.Kind == "MonoScript") {
                    if (state.Script == null || !typeof(NativeObject).IsAssignableFrom(actual) || typeof(Component).IsAssignableFrom(actual) || actual == typeof(GameObject))
                        throw Error("MonoScript managed wrapper type or payload differs");
                    ResidentNativeAssetState.ValidateMonoScript(state.Script);
                } else if (state.Kind != "Unsupported" || state.Mesh != null || state.Material != null || state.Texture != null)
                    throw Error("asset state kind is not implemented by its actual type");
            }
        }
        private static void ValidateSharedMutable(Data data)
        {
            if (data.SharedMutable == null || data.SharedMutable.Length > ResidentAssetBindings.MaximumAssets
                || data.Version != 4 && data.SharedMutable.Length != 0
                || data.SharedMutable.Any(string.IsNullOrEmpty)
                || !data.SharedMutable.SequenceEqual(data.SharedMutable.Distinct(StringComparer.Ordinal).OrderBy(key=>key,StringComparer.Ordinal)))
                throw Error("shared mutable cache directory differs");
            var records = data.Manifest.ToDictionary(record=>record.Binding,StringComparer.Ordinal);
            var declared = new HashSet<string>(data.SharedMutable,StringComparer.Ordinal);
            var allowed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var font in data.Snapshots.Where(state=>state != null && state.Kind == "Font")) {
                if (data.Version != 4 || font.Font == null || !records.TryGetValue(font.Binding,out var root)
                    || root.Type.Resolve() != typeof(Font)) throw Error("shared mutable root is not an actual Font codec");
                ResidentFontCacheState.Validate(font.Font);
                if (root.Kind == "serialized") allowed.Add(root.Binding);
                foreach (var reference in new[] {(font.Font.Material,typeof(Material)),(font.Font.Texture,typeof(Texture2D))}) {
                    if (reference.Item1.Length == 0) continue;
                    if (!records.TryGetValue(reference.Item1,out var record) || record.Type.Resolve() != reference.Item2)
                        throw Error("Font cache dependency has no exact asset manifest type");
                    if (record.Kind == "serialized") allowed.Add(record.Binding);
                    var candidates = data.Snapshots.Where(state=>state != null && state.Binding == record.Binding).Take(2).ToArray();
                    var dependency = candidates.Length == 1 ? candidates[0] : null;
                    if (dependency == null || reference.Item2 == typeof(Material) && (dependency.Kind != "Material" || dependency.Material == null)
                        || reference.Item2 == typeof(Texture2D) && (dependency.Kind != "Texture2D" || dependency.Texture == null))
                        throw Error("Font cache mutable dependency lacks its exact typed native snapshot");
                }
                foreach (var fallback in font.Font.Fallbacks.Where(key=>key.Length != 0))
                    if (!records.TryGetValue(fallback,out var record) || record.Type.Resolve() != typeof(Font))
                        throw Error("Font fallback has no exact referenced Font manifest entry");
            }
            foreach (var clip in data.Snapshots.Where(state=>state != null && state.Kind == "AnimationClip")) {
                if (data.Version != 4 || clip.Clip == null || !records.TryGetValue(clip.Binding,out var root)
                    || root.Type.Resolve() != typeof(AnimationClip)) throw Error("Clip dependency root type differs");
                ResidentClipDependencyState.Validate(clip.Clip);
                if (root.Kind == "serialized") allowed.Add(root.Binding);
                if (clip.Clip.AdditiveClip.Length != 0 && (!records.TryGetValue(clip.Clip.AdditiveClip,out var additive)
                    || additive.Type.Resolve() != typeof(AnimationClip))) throw Error("Clip additive reference type differs");
            }
            if (!allowed.SetEquals(declared)) throw Error("shared mutable assets contain missing or orphaned Font cache declarations");
            foreach (var key in declared)
                if (!records.TryGetValue(key,out var record) || record.Kind != "serialized")
                    throw Error("shared mutable entry is not an explicit serialized dependency");
        }
        internal static void ValidateAnimatorAssetDependencies(ResidentAnimatorState.Data animation,Data assets)
        {
            Validate(assets);
            var records = assets.Manifest.ToDictionary(record=>record.Binding,StringComparer.Ordinal);
            var clipStates = assets.Snapshots.Where(state=>state.Kind=="AnimationClip").ToDictionary(state=>state.Binding,StringComparer.Ordinal);
            foreach (var key in ResidentAnimatorState.ClipAssetReferences(animation).Where(key=>key.Length!=0).Distinct(StringComparer.Ordinal)) {
                if (!records.TryGetValue(key,out var record) || record.Type.Resolve()!=typeof(AnimationClip)
                    || !clipStates.TryGetValue(key,out var state) || state.Clip == null)
                    throw Error("actual Animator Clip reference lacks its exact typed asset payload");
            }
            ResidentAnimatorState.ValidateControllerAssetObservers(animation);
            var clipObservers = animation.Graphs.SelectMany(graph=>graph.Nodes.Select((node,index)=>new {graph.Owner,Index=index,Node=node}))
                .Where(row=>row.Node.Clip != null && row.Node.Clip.Observed.Length != 0).ToArray();
            var observerDirectory = clipObservers.ToLookup(row=>row.Node.Clip!.Observed,StringComparer.Ordinal);
            foreach (var snapshot in assets.Snapshots.Where(state=>state.Kind=="AnimationClip")) {
                var actual = observerDirectory[snapshot.Binding].ToArray();
                var keys = actual.Select(row=>row.Owner+"/playable/"+row.Index+"/observer/472").ToArray();
                if (snapshot.Clip!.ObserverCount != actual.Length) throw Error("Clip observer directory has external or missing typed owners");
                foreach (var row in actual) {
                    var peers = row.Node.Clip!.ObserverPeers;
                    if (peers == null || peers.Length != actual.Length || peers.Distinct(StringComparer.Ordinal).Count()!=peers.Length
                        || !new HashSet<string>(peers,StringComparer.Ordinal).SetEquals(keys))
                        throw Error("Clip observer directory lacks reciprocal graph ownership");
                }
            }
            foreach (var controller in animation.Assets) {
                if (!records.TryGetValue(controller.Binding,out var record) || record.Kind != "serialized"
                    || !typeof(RuntimeAnimatorController).IsAssignableFrom(record.Type.Resolve()) || controller.Kind == "unknown")
                    throw Error("controller program lacks its typed serialized asset identity");
                foreach (var key in controller.Clips.Concat(controller.OriginalClips).Concat(controller.OverridePairs).Where(key=>key.Length!=0)) {
                    if (!records.TryGetValue(key,out var clip) || clip.Type.Resolve() != typeof(AnimationClip))
                        throw Error("controller clip directory lacks its exact AnimationClip dependency");
                }
            }
        }
        internal static void ValidatePresentationDependencies(ResidentPresentationTypes.Data presentation,Data assets)
        {
            ResidentPresentationTypes.Validate(presentation);
            Validate(assets);
            var records = assets.Manifest.ToDictionary(record=>record.Binding,StringComparer.Ordinal);
            var states = assets.Snapshots.ToDictionary(state=>state.Binding,StringComparer.Ordinal);
            foreach (var row in presentation.Rows.Where(row=>row.Kind == "text-mesh" && row.Font.Length != 0)) {
                if (!records.TryGetValue(row.Font,out var record) || record.Type.Resolve() != typeof(Font))
                    throw Error("TextMesh Font dependency has no exact Font asset identity");
                if (row.StaticFontDependency) {
                    if (record.Kind != "serialized" || row.FontCacheDependency || states.TryGetValue(row.Font,out var unexpected) && unexpected.Kind == "Font")
                        throw Error("static TextMesh Font is not an identity-only serialized dependency");
                    continue;
                }
                // The component marker only delegates ownership. This unified
                // cross-domain check, never that marker alone, requires Font D.
                if (!row.FontCacheDependency || assets.Version != 4 || !states.TryGetValue(row.Font,out var state)
                    || state.Kind != "Font" || state.Font == null || state.Type.Resolve() != typeof(Font))
                    throw Error("TextMesh dynamic Font lacks its version-four typed asset cache snapshot");
                ResidentFontCacheState.Validate(state.Font);
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
            IReadOnlyDictionary<string, NativeObject>? adoptedDynamic = null, IReadOnlyDictionary<string, NativeObject>? observedAssets = null)
            => new Prepared(source, directory, scope, admitted, adoptedDynamic, observedAssets);
        private static void ApplyWithFontPreflight(IEnumerable<ResidentNativeAssetState.Snapshot> snapshots,
            Action<ResidentNativeAssetState.Snapshot> preflight,Action<ResidentNativeAssetState.Snapshot> apply)
        {
            var ordered = snapshots.ToArray();
            foreach (var font in ordered.Where(state=>state.Kind == "Font")) preflight(font);
            // Build each real Font plan only at its apply point: an earlier
            // Font Flush invalidates global generator receipts for later Fonts.
            foreach (var state in ordered.OrderBy(state=>state.Kind == "Font" ? 1 : 0)) apply(state);
        }
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
                IReadOnlyDictionary<string, NativeObject>? adoptedDynamic = null, IReadOnlyDictionary<string, NativeObject>? observedAssets = null)
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
                    if (state.Kind == "Font" && data.Manifest.Any(record=>record.Binding == state.Binding && record.Kind == "dynamic"))
                        throw Error("dynamic Font has no proven native construction adapter");
                    // Dynamic Material with native Shader reference is resolved dynamically via native lookup in Create
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
                        else if (state.Kind == "Material") {
                            var shaderBinding = state.Material!.ShaderBinding;
                            Shader? shader = null;
                            if (!shaderBinding.StartsWith("resident-native-asset/", StringComparison.Ordinal)) {
                                shader = resolve(shaderBinding) as Shader;
                            } else {
                                var pointer = ResidentNativeAssetReference.ResolveTyped(shaderBinding, typeof(Shader), resolve);
                                if (pointer != IntPtr.Zero) {
                                    var id = ResidentNativeMemory.I32(ResidentNativeMemory.Read(IntPtr.Add(pointer, 8), 4), 0);
                                    shader = ResidentTargetAssetLookup.BindRegistered(id, pointer, typeof(Shader), scope) as Shader;
                                }
                            }
                            if (shader == null) {
                                shader = Shader.Find("Sprites/Default") ?? throw Error("dynamic material shader binding differs: " + shaderBinding);
                            }
                            result = new Material(shader);
                        }
                        else if (state.Kind == "Texture2D") {
                            var texture = state.Texture!;
                            result = new Texture2D(texture.Width, texture.Height, (TextureFormat)texture.Format, texture.MipmapCount,
                                !GraphicsFormatUtility.IsSRGBFormat((GraphicsFormat)texture.GraphicsFormat));
                        } else if (state.Kind == "PhysicsMaterial2D") result = new PhysicsMaterial2D();
                        else if (state.Kind == "RenderTexture") result = new RenderTexture(state.RenderTarget!.Descriptor);
                        else if (state.Kind == "Sprite") result = ResidentNativeAssetState.CreateSprite(state.Sprite!, resolve);
                        else if (state.Kind == "TextAsset") result = new TextAsset("");
                        else if (state.Kind == "ScriptableObject") result = ResidentNativeAssetState.CreateScriptableAsset(record.Type.Resolve(), scope);
                        else if (state.Kind == "MonoScript") result = ResidentNativeAssetState.BindOrCreateRuntimeMonoScript(state, Require, scope);
                        else throw Error("dynamic native asset has no exact-type construction adapter");
                        result.name = state.Name; result.hideFlags = (HideFlags)state.HideFlags;
                    });
                    return result ?? throw Error("dynamic asset construction returned no owner");
                }
                bindings = ResidentAssetBindings.PrepareTarget(data.Manifest, directory, admitted, scope, Create, observedAssets);
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
                Require();
                foreach (var clip in data.Snapshots.Where(state=>state.Kind == "AnimationClip")) {
                    if (!data.Manifest.Any(record=>record.Binding==clip.Binding && record.Kind=="serialized"))
                        throw Error("dynamic Clip dependency construction is not supported");
                    ResidentClipDependencyState.Preflight(clip.Clip!,(AnimationClip)Resolve(clip.Binding),Resolve,Bind,Require);
                }
                if (applied) throw Error("native asset application is single use"); applied = true;
                ApplyWithFontPreflight(data.Snapshots,
                    state => ResidentFontCacheState.Preflight(state.Font!,(Font)Resolve(state.Binding),Resolve,Bind,Require),
                    state => scope.Mutate(() => {
                    var owner = Resolve(state.Binding);
                    if (state.Kind == "Mesh") {
                        ResidentNativeAssetState.PrepareMeshBuffers(state, (Mesh)owner, admitted).Apply();
                        ResidentNativeAssetState.ApplyMeshNativeValues(state.Mesh!, (Mesh)owner, Resolve, Bind, Require);
                    }
                    else if (state.Kind == "Material") {
                        using (var transfer = ResidentNativeAssetState.PrepareMaterialValues(state, (Material)owner, Resolve, Bind, admitted)) transfer.Apply();
                    }
                    else if (state.Kind == "Texture2D") ResidentNativeAssetState.ApplyTexture(state, (Texture2D)owner, admitted);
                    else if (state.Kind == "PhysicsMaterial2D") ResidentNativeAssetState.ApplyPhysicsMaterial(state, (PhysicsMaterial2D)owner, Resolve, Bind, Require);
                    else if (state.Kind == "RenderTexture") ResidentNativeAssetState.ApplyRenderTarget(state, (RenderTexture)owner);
                    else if (state.Kind == "Sprite") ResidentNativeAssetState.ApplySprite(state.Sprite!, (Sprite)owner);
                    else if (state.Kind == "TextAsset") ResidentNativeAssetState.ApplyTextAsset(state, (TextAsset)owner, Require);
                    else if (state.Kind == "ScriptableObject") ResidentMonoNativeState.Prepare(state.Scriptable!, owner, scope, Resolve, Bind, Require).Apply();
                    else if (state.Kind == "MonoScript") ResidentNativeAssetState.ApplyMonoScript(state, owner, Require);
                    else if (state.Kind == "AnimationClip") ResidentClipDependencyState.Preflight(state.Clip!,(AnimationClip)owner,Resolve,Bind,Require);
                    else if (state.Kind == "Font") {
                        var plan = ResidentFontCacheState.Prepare(state.Font!,(Font)owner,Resolve,Bind,Require);
                        plan.Apply(); plan.Verify();
                    }
                    else throw Error("the requested asset native restore stage is not complete: " + state.Kind);
                }));
                Verify();
            }
            internal void Canon(Action<Mesh> rebindConsumers, Action<Mesh> verifyConsumers)
            { Require(); ResidentAssetPipeline.Canon(data, Assets, scope, admitted, rebindConsumers, verifyConsumers, Resolve, Bind,
                mesh => ResidentRendererState.RequireMeshConsumersCovered(directory, mesh)); }
            internal void Verify()
            {
                Require(); bindings.RequireIdentity(); var tPrev = ResidentTransformState.DebugSummary(); Modding.Logger.Log($"[HKTAS-ASSET-T] Verify start: {tPrev}");
                foreach (var state in data.Snapshots) {
                    var observed = ResidentNativeAssetState.Capture(state.Binding, Resolve(state.Binding), Bind); var tNow = ResidentTransformState.DebugSummary(); if (tNow != tPrev) { Modding.Logger.Log($"[HKTAS-ASSET-T] Verify CHANGED on {state.Kind} ({state.Name} / {state.Binding}): {tPrev} -> {tNow}"); tPrev = tNow; }
                    if (state.Name != observed.Name || state.HideFlags != observed.HideFlags || state.Type.Key != observed.Type.Key || state.Kind != observed.Kind)
                        throw Error("native asset identity readback differs");
                    if (state.Kind == "AnimationClip") ResidentClipDependencyState.RequireEquivalent(state.Clip!,observed.Clip!);
                    if (state.Kind == "Font" && ResidentAssetDataWire.Values(state.Font!) != ResidentAssetDataWire.Values(observed.Font!))
                        throw Error("native Font cache independent readback differs");
                    if (state.Kind == "Mesh" && !ResidentNativeAssetState.EncodeMesh(state.Mesh!).SequenceEqual(ResidentNativeAssetState.EncodeMesh(observed.Mesh!)))
                        throw Error("native asset geometry readback differs");
                    if (state.Kind == "Mesh" && !ResidentNativeAssetState.EncodeMeshNativeValues(state.Mesh!.Native!).SequenceEqual(ResidentNativeAssetState.EncodeMeshNativeValues(observed.Mesh!.Native!)))
                        throw Error("native asset retained mesh metadata readback differs");
                    if (state.Kind == "Material" && !ResidentNativeAssetState.EncodeMaterialValues(state.Material!).SequenceEqual(ResidentNativeAssetState.EncodeMaterialValues(observed.Material!)))
                        throw Error("native asset material values readback differs");
                    if (state.Kind == "Texture2D" && !state.Texture!.GpuMips.SelectMany(x => x).SequenceEqual(observed.Texture!.GpuMips.SelectMany(x => x)))
                        throw Error("native asset GPU texture image readback differs");
                    if (state.Kind == "Texture2D" && !ResidentNativeAssetState.EncodeTextureImage(state.Texture!).SequenceEqual(ResidentNativeAssetState.EncodeTextureImage(observed.Texture!)))
                        throw Error("native asset texture image readback differs");
                    if (state.Kind == "PhysicsMaterial2D" && !ResidentNativeAssetState.EncodePhysicsMaterial(state.PhysicsMaterial!).SequenceEqual(ResidentNativeAssetState.EncodePhysicsMaterial(observed.PhysicsMaterial!)))
                        throw Error("native asset physics material readback differs");
                    if (state.Kind == "TextAsset" && !state.Text!.Bytes.SequenceEqual(observed.Text!.Bytes)) throw Error("native TextAsset bytes readback differs");
                    if (state.Kind == "ScriptableObject") ResidentMonoNativeState.RequirePlanEqual(state.Scriptable!, observed.Scriptable!);
                    if (state.Kind == "MonoScript") ResidentNativeAssetState.RequireMonoScriptPlanEqual(state.Script!, observed.Script!);
                    if ((state.Kind == "RenderTexture" || state.Kind == "Sprite")
                        && ResidentAssetDataWire.Values(state) != ResidentAssetDataWire.Values(observed))
                        throw Error("render asset independent readback differs");
                    if (state.Kind != "AnimationClip" && state.Kind != "Font" && state.Kind != "RenderTexture" && state.Kind != "Sprite" && state.Kind != "Mesh" && state.Kind != "Material" && state.Kind != "Texture2D" && state.Kind != "PhysicsMaterial2D" && state.Kind != "TextAsset" && state.Kind != "ScriptableObject" && state.Kind != "MonoScript") throw Error("the requested asset native verification stage is not complete: " + state.Kind);
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
                if (state.Kind == "Font" || state.Kind == "AnimationClip") { state.RequireCompleteCoverage(); continue; }
                if (state.Kind == "Texture2D" || state.Kind == "RenderTexture" || state.Kind == "Sprite") { state.RequireCompleteCoverage(); continue; }
                if (state.Kind == "MonoScript" && state.Script != null) { ResidentNativeAssetState.RequireMonoScriptComplete(state.Script); continue; }
                throw Error("asset common Canon stage or reference dependencies are not complete: " + state.Kind);
            }


            foreach (var state in source.Snapshots) {
                if (state.Kind == "Mesh") ResidentNativeAssetState.CanonColdMesh(state, (Mesh)owners[state.Binding], scope, admitted, rebindConsumers, verifyConsumers, bind!);
                else if (state.Kind == "Material") ResidentNativeAssetState.CanonColdMaterial(state, (Material)owners[state.Binding], scope, admitted, resolve!, bind!);
                else if (state.Kind == "TextAsset") scope.Mutate(() => ResidentNativeAssetState.ApplyTextAsset(state, (TextAsset)owners[state.Binding], scope.Require));
                else if (state.Kind == "ScriptableObject") scope.Mutate(() => ResidentMonoNativeState.Canon(owners[state.Binding], scope, scope.Require));
                else if (state.Kind == "MonoScript") scope.Mutate(() => ResidentNativeAssetState.ApplyMonoScript(state, owners[state.Binding], scope.Require));
            }


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
                || !TextureCpuStorageLengthMatches(state)))
                throw Error("texture CPU image stride, count or mip directory differs;public=" + state.Width + "x" + state.Height
                    + ";cpu=" + state.CpuWidth + "x" + state.CpuHeight + ";format=" + state.CpuFormat + ";stride=" + state.CpuImageSize
                    + ";images=" + state.CpuImageCount + ";mips=" + state.CpuMipCount + ";bytes=" + state.CpuBytes.Length);
            if (state.Native != null) ResidentNativeAssetState.ValidateTextureNative(state.Native);
        }
        internal static bool TextureCpuStorageLengthMatches(ResidentNativeAssetState.TextureState state)
        {
            if (state.CpuBytes == null || state.CpuImageCount < 1 || state.CpuImageCount > 4096
                || state.CpuImageSize < 1 || state.CpuImageSize > ResidentNativeAssetState.MaximumBytes) return false;
            var logical = state.CpuImageSize * (ulong)state.CpuImageCount;
            // Pinned SharedTextureData constructor at 0x69bd60 allocates image
            // stride * image count + GetBytesForOnePixel(format). Compressed
            // formats contribute zero. Preserve both the raw image and guard pixel.
            var pixel = TextureCpuGuardPixelBytes(state.CpuFormat);
            return (ulong)state.CpuBytes.Length == logical || (ulong)state.CpuBytes.Length == logical + (uint)pixel;
        }
        private static int TextureCpuGuardPixelBytes(int format)
        {
            // Native TextureFormat values from GetBytesForOnePixel (0x679740)
            // and its byte-width table (0x1687ce0), including internal formats.
            switch (format) {
                case 1: case 63: return 1; // Alpha8, R8
                case 2: case 7: case 9: case 13: case 15: case 21: case 62: return 2;
                case 3: case 8: return 3; // RGB24, BGR24
                case 4: case 5: case 14: case 16: case 18: case 22: case 72: return 4;
                case 73: return 6; // RGB48
                case 17: case 19: case 74: return 8;
                case 23: return 12; // RGBFloat
                case 6: case 20: return 16; // ARGBFloat, RGBAFloat
                default: return 0;
            }
        }
        private static InvalidDataException Error(string reason) => new InvalidDataException("Resident asset pipeline: " + reason);
    }
}
