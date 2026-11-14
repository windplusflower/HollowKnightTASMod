using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentCameraCanvasState
    {
        internal static string[] RequiredCoverageGaps(Data data)
        {
            var gaps = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var row in data.Cameras) {
                if (row.Execution) gaps.Add("camera-execution-control-native-domain");
                if (row.RenderContext) gaps.Add("camera-active-render-loop-context");
                if (row.TransientTextures.Any(value => value.Alias.Length != 0)) gaps.Add("camera-transient-render-texture-cache");
                if (row.ShadowEntries != 0) gaps.Add("camera-retained-shadow-cache");
                if (row.EventCounts.Any(count => count != 0)) gaps.Add("camera-rendering-command-buffer-state");
                if (row.ColorSurfaces.Concat(new[] { row.DepthSurface }).Any(value => value.Unknown)) gaps.Add("camera-unbound-render-surface");
                if (row.Scalars[9].Take(4).Any(value => value != 0)) gaps.Add("camera-active-render-or-cull");
            }
            foreach (var row in data.Renderers) {
                if (row.Execution) gaps.Add("canvas-renderer-execution-control-native-domain");
                if (row.Texture.Unknown || row.AlphaTexture.Unknown) gaps.Add("canvas-renderer-unbound-texture-id");
                if (row.Push.Canvas == "@unsupported" || row.Pop.Canvas == "@unsupported") gaps.Add("canvas-renderer-unbound-canvas-data");
            }
            foreach (var row in data.Canvases) {
                if (row.Execution) gaps.Add("canvas-execution-control-native-domain");
                if (row.AssetUsers != 0) gaps.Add("canvas-native-asset-user-list");
                if (row.BatchFence || row.GeometryFence) gaps.Add("canvas-active-batch-or-geometry-fence");
                if (!row.BatchJobDone) gaps.Add("canvas-running-batch-job");
                if (row.BatchBuffers || row.Batches.Length != 0 || row.SubBatches.Length != 0 || row.BatchCapacity != 0 || row.SubBatchCapacity != 0
                    || row.BatchScalars.Any(bytes => bytes.Any(value => value != 0))) gaps.Add("canvas-gpu-batches-require-common-canon");
                if ((row.Scalars[5][8] & 6) != 6) gaps.Add("canvas-native-rebuild-flags-not-canonical");
                if (row.Handles.Owners.Contains("@unsupported")) gaps.Add("canvas-unbound-handle-owner");
                if (row.Instructions.Any(value => value.Texture.Unknown || value.AlphaTexture.Unknown)) gaps.Add("canvas-instruction-unbound-texture-id");
            }
            foreach (var group in data.Shared) {
                foreach (var gap in ResidentSharedMeshState.RequiredCoverageGaps(group.State)) gaps.Add(gap);
                var retained = data.Renderers.Count(row => row.Shared == group.Key) + data.Canvases.Sum(row => row.Instructions.Count(value => value.Shared == group.Key));
                if (retained + group.Meshes.Length + group.Sprites.Length != group.State.References) gaps.Add("canvas-shared-mesh-unbound-retains");
                if (group.Sprites.Length != 0) gaps.Add("canvas-shared-mesh-sprite-cache-aliases");
                if (group.Meshes.Length > 1) gaps.Add("canvas-shared-mesh-multiple-asset-wrappers");
            }
            if (data.CameraManager.Stack || data.CameraManager.InsideRenderOrCull) gaps.Add("camera-render-manager-active-stack");
            if (data.CameraManager.Projectors != 0) gaps.Add("camera-render-manager-projector-map");
            if (data.CanvasManager.EmptyDirtyBuckets != 0) gaps.Add("canvas-empty-dirty-buckets-require-common-canon");
            if (data.CanvasManager.CallbackDispatch) gaps.Add("canvas-geometry-callback-active-dispatch");
            if (data.CanvasManager.Callbacks.Any(value => value.UserKind == "unsupported" || value.UserKind == "engine")) gaps.Add("canvas-geometry-callback-native-context");
            var pool = data.RenderTextures;
            if (pool.Free.Length != 0 || pool.Taken.Length != 0 || pool.FreeCapacity != 0 || pool.FreeDeleted != 0 || pool.TemporaryCounter != 0
                || pool.BufferCapacity != 0 || pool.BufferDeleted != 0) gaps.Add("render-texture-pool-requires-common-canon");
            if (pool.BufferEntries != 0) gaps.Add("render-texture-pool-retained-gfx-buffers");
            var cacheAliases = new HashSet<string>(data.Cameras.SelectMany(row => row.TransientTextures.Concat(row.Shadows.Select(value => value.Texture)))
                .Select(value => value.Alias).Where(value => value.Length != 0), StringComparer.Ordinal);
            var takenAliases = new HashSet<string>(pool.Taken.Select(value => value.Texture.Alias), StringComparer.Ordinal);
            if (!cacheAliases.IsSubsetOf(takenAliases)) gaps.Add("camera-native-render-texture-pool-state");
            if (!takenAliases.IsSubsetOf(cacheAliases)) gaps.Add("render-texture-pool-unbound-taken-texture");
            return gaps.ToArray();
        }
        internal static void RequireCompleteCoverage(Data data)
        {
            Validate(data); var gaps = RequiredCoverageGaps(data).Concat(data.CoverageGaps).Distinct(StringComparer.Ordinal).ToArray();
            if (gaps.Length != 0) throw Error("native coverage is incomplete: " + string.Join(",", gaps));
        }
        internal static void RequireCanonicalizableCoverage(Data data)
        {
            Validate(data);
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "canvas-gpu-batches-require-common-canon", "canvas-native-rebuild-flags-not-canonical",
                "camera-transient-render-texture-cache", "camera-retained-shadow-cache", "render-texture-pool-requires-common-canon", "canvas-empty-dirty-buckets-require-common-canon" };
            if (data.Canvases.All(row => row.BatchJobDone && !row.GeometryFence)) allowed.Add("canvas-active-batch-or-geometry-fence");
            // A live RenderLoopContext or unknown pool owner still needs its
            // retained native resource closure before teardown may be admitted.
            var gaps = RequiredCoverageGaps(data).Concat(data.CoverageGaps).Where(value => !allowed.Contains(value)).Distinct(StringComparer.Ordinal).ToArray();
            if (gaps.Length != 0) throw Error("native state cannot safely enter common Canon: " + string.Join(",", gaps));
        }
        internal static void RequireCanonicalState(Data data)
        {
            RequireCompleteCoverage(data);
            foreach (var row in data.Cameras.Where(row => !row.Execution))
                if (row.RenderContext || row.TransientTextures.Any(value => value.Alias.Length != 0) || row.Shadows.Length != 0
                    || row.ShadowEntries != 0 || row.Scalars[9].Take(4).Any(value => value != 0)) throw Error("Camera S0 rendering caches are not empty");
            foreach (var row in data.Canvases.Where(row => !row.Execution))
                if (row.BatchBuffers || row.BatchFence || row.GeometryFence || !row.BatchJobDone || row.Batches.Length != 0 || row.SubBatches.Length != 0
                    || row.BatchCapacity != 0 || row.SubBatchCapacity != 0 || row.BatchScalars.Any(bytes => bytes.Any(value => value != 0)) || (row.Scalars[5][8] & 6) != 6)
                    throw Error("Canvas S0 native batch storage or rebuild flags differ");
            if (data.CanvasManager.CallbackChanged) throw Error("Canvas S0 callback-change flag remains set");
            var pool = data.RenderTextures;
            if (pool.Free.Length != 0 || pool.Taken.Length != 0 || pool.FreeCapacity != 0 || pool.FreeDeleted != 0 || pool.TemporaryCounter != 0
                || pool.BufferCapacity != 0 || pool.BufferEntries != 0 || pool.BufferDeleted != 0) throw Error("temporary render-pool S0 storage is not empty");
        }
        internal static void Validate(Data data)
        {
            if (data == null || data.Version != 2 || data.Cameras == null || data.Canvases == null || data.Renderers == null || data.Shared == null
                || data.CameraManager == null || data.CanvasManager == null || data.RenderTextures == null || data.CoverageGaps == null || data.Cameras.Length + data.Canvases.Length + data.Renderers.Length > Maximum
                || data.Shared.Length > Maximum || data.CoverageGaps.Length > Maximum || data.CoverageGaps.Any(value => !Text(value, false))) throw Error("camera/UI snapshot shape differs");
            foreach (var row in data.Cameras) ValidateCamera(row);
            foreach (var row in data.Canvases) ValidateCanvas(row);
            foreach (var row in data.Renderers) ValidateCanvasRenderer(row);
            foreach (var keys in new[] { data.Cameras.Select(row => row.Owner), data.Canvases.Select(row => row.Owner), data.Renderers.Select(row => row.Owner) })
                if (!keys.SequenceEqual(keys.OrderBy(key => key, StringComparer.Ordinal)) || keys.Distinct(StringComparer.Ordinal).Count() != keys.Count()) throw Error("native owner directory is not ordered and unique");
            var all = data.Cameras.Select(row => row.Owner).Concat(data.Canvases.Select(row => row.Owner)).Concat(data.Renderers.Select(row => row.Owner)).ToArray();
            if (all.Distinct(StringComparer.Ordinal).Count() != all.Length) throw Error("native component type directories overlap");
            var groups = new HashSet<string>(StringComparer.Ordinal); var meshOwners = new HashSet<string>(StringComparer.Ordinal); var spriteOwners = new HashSet<string>(StringComparer.Ordinal); string? previous = null;
            foreach (var group in data.Shared) {
                if (group == null || !Text(group.Key, false) || !groups.Add(group.Key) || previous != null && StringComparer.Ordinal.Compare(previous, group.Key) >= 0
                    || group.Meshes == null || group.Sprites == null || group.Meshes.Length + group.Sprites.Length > Maximum
                    || group.Meshes.Any(key => !Text(key, false) || !meshOwners.Add(key)) || group.Sprites.Any(key => !Text(key, false) || !spriteOwners.Add(key)))
                    throw Error("shared CPU geometry identity graph differs");
                previous = group.Key; ResidentSharedMeshState.Validate(group.State);
            }
            var uses = data.Renderers.Select(row => row.Shared).Concat(data.Canvases.SelectMany(row => row.Instructions).Select(value => value.Shared)).Where(key => key.Length != 0).ToArray();
            if (uses.Any(key => !groups.Contains(key)) || groups.Any(key => !uses.Contains(key))) throw Error("shared CPU geometry is missing or unreferenced");
            ValidateCameraRegistry(data); ValidateCanvasRegistry(data); ValidateRenderPool(data);
        }
        internal static Action Canon(ResidentObjectDirectory directory, ResidentMutationScope scope, Func<string, NativeObject> resolve,
            Func<object, string?> bind, Action admitted, Action<TransientOwner>? requireTransientExclusive = null)
        {
            if (directory == null || scope == null || resolve == null || bind == null || admitted == null) throw new ArgumentNullException();
            scope.Require(); admitted(); var transients = new List<TransientOwner>(); var before = Capture(directory, bind, transients.Add); RequireCanonicalizableCoverage(before);
            foreach (var owner in transients) {
                if (requireTransientExclusive == null) throw Error("transient RenderTexture lacks complete world alias exclusion");
                requireTransientExclusive(owner);
            }
            if (before.CameraManager.Stack || before.CameraManager.InsideRenderOrCull || before.CanvasManager.CallbackDispatch)
                throw Error("common Camera/UI Canon is outside the quiescent native boundary");
            foreach (var row in before.Cameras.Where(row => !row.Execution)) if (!scope.Owns((Component)directory.Resolve(row.Owner))) throw Error("Camera Canon owner is outside the mutation scope");
            foreach (var row in before.Canvases.Where(row => !row.Execution)) if (!scope.Owns((Component)directory.Resolve(row.Owner))) throw Error("Canvas Canon owner is outside the mutation scope");
            try {
                foreach (var row in before.Cameras.Where(row => !row.Execution)) { scope.Require(); CanonCamera((Camera)directory.Resolve(row.Owner), admitted); }
                foreach (var row in before.Canvases.Where(row => !row.Execution)) { scope.Require(); CanonCanvas((Canvas)directory.Resolve(row.Owner), admitted); }
                var poolReader = new Reader(directory, bind, owner => {
                    if (requireTransientExclusive == null) throw Error("temporary texture purge lacks complete world alias exclusion");
                    requireTransientExclusive(owner);
                });
                var purge = CaptureRenderPool(poolReader); poolReader.RequireFrozen();
                if (purge.Taken.Length != 0 || purge.BufferEntries != 0) throw Error("native rendering cleanup retained an unknown pool owner");
                scope.Require(); CanonRenderPool(admitted);
                M.Write(IntPtr.Add(CanvasManager(), 3136), new byte[1]);
                RemoveEmptyDirtyBuckets(directory,bind,admitted);
                var after = Capture(directory, bind); var retained = Copy(before);
                retained.CanvasManager.EmptyDirtyBuckets = 0;
                for (var index = 0; index < retained.Cameras.Length; index++) {
                    var expected = retained.Cameras[index]; var actual = after.Cameras[index]; if (expected.Execution) continue;
                    if (actual.RenderContext || actual.TransientTextures.Any(value => value.Alias.Length != 0)) throw Error("Camera teardown retained an active rendering resource");
                    if (actual.ShadowEntries != 0) throw Error("Camera native shadow teardown did not reach its canonical empty state");
                    expected.RenderContext = false; expected.ShadowEntries = 0; expected.Shadows = Array.Empty<ShadowEntry>();
                    for (var slot = 1; slot < 4; slot++) expected.TemporaryTextures[slot] = "";
                    expected.TransientTextures = Enumerable.Range(0, 3).Select(_ => new TransientTexture()).ToArray();
                }
                retained.RenderTextures.Free = Array.Empty<PoolBucket>(); retained.RenderTextures.Taken = Array.Empty<PoolTexture>();
                retained.RenderTextures.FreeCapacity = retained.RenderTextures.FreeDeleted = retained.RenderTextures.TemporaryCounter = 0;
                retained.RenderTextures.BufferCapacity = retained.RenderTextures.BufferDeleted = 0;
                for (var index = 0; index < retained.Canvases.Length; index++) {
                    var expected = retained.Canvases[index]; var actual = after.Canvases[index]; if (expected.Execution) continue;
                    if (actual.BatchBuffers || actual.BatchFence || actual.GeometryFence || actual.Batches.Length != 0 || actual.SubBatches.Length != 0 || actual.BatchCapacity != 0 || actual.SubBatchCapacity != 0
                        || actual.BatchScalars.Any(bytes => bytes.Any(value => value != 0))) throw Error("Canvas native GPU teardown did not reach its canonical empty state");
                    expected.Batches = Array.Empty<Batch>(); expected.SubBatches = Array.Empty<SubBatch>(); expected.BatchCapacity = expected.SubBatchCapacity = 0;
                    expected.BatchBuffers = expected.BatchFence = expected.GeometryFence = false;
                    expected.BatchScalars = BatchRanges.Select(range => new byte[range.Count]).ToArray(); expected.Scalars[5][8] |= 6;
                }
                retained.CanvasManager.CallbackChanged = false; retained.CoverageGaps = RequiredCoverageGaps(retained); RequireEqual(retained, after);
                Action verify = () => { scope.Require(); admitted(); var actual = Capture(directory, bind); RequireCanonicalState(actual); RequireEqual(after, actual); admitted(); }; verify(); return verify;
            } catch (Exception error) { scope.Fault(error); throw; }
        }
        internal static Plan Prepare(Data source, ResidentObjectDirectory directory, ResidentMutationScope scope,
            Func<string, NativeObject> resolve, Func<object, string?> bind, Action admitted) => new Plan(source, directory, scope, resolve, bind, admitted);
        internal sealed class Plan
        {
            private readonly Data source, before;
            private readonly ResidentObjectDirectory directory;
            private readonly ResidentMutationScope scope;
            private readonly Func<string, NativeObject> resolve;
            private readonly Func<object, string?> bind;
            private readonly Action admitted;
            private bool applied;
            internal Plan(Data source, ResidentObjectDirectory directory, ResidentMutationScope scope, Func<string, NativeObject> resolve, Func<object, string?> bind, Action admitted)
            {
                RequireCompleteCoverage(source); this.source = Copy(source); this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
                this.scope = scope ?? throw new ArgumentNullException(nameof(scope)); this.resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
                this.bind = bind ?? throw new ArgumentNullException(nameof(bind)); this.admitted = admitted ?? throw new ArgumentNullException(nameof(admitted));
                scope.Require(); admitted(); before = Capture(directory, bind);
                if (RequiredCoverageGaps(before).Concat(before.CoverageGaps).Any()) {
                    Canon(directory, scope, resolve, bind, admitted);
                    before = Capture(directory, bind);
                }
                RequireCompleteCoverage(before);
                void Owner(string key, ResidentObjectDirectory.TypeRecord type) {
                    var owner = directory.Resolve(key);
                    if (!(owner is Component component) || !scope.Owns(component) || ResidentObjectDirectory.TypeRecord.Capture(owner.GetType()).Key != type.Key)
                        throw Error("native target owner/type is outside the complete mutation directory");
                }
                foreach (var row in source.Cameras) Owner(row.Owner, row.Type);
                foreach (var row in source.Canvases) Owner(row.Owner, row.Type);
                foreach (var row in source.Renderers) Owner(row.Owner, row.Type);
                if (!source.Cameras.Select(row => row.Owner).SequenceEqual(before.Cameras.Select(row => row.Owner))
                    || !source.Canvases.Select(row => row.Owner).SequenceEqual(before.Canvases.Select(row => row.Owner))
                    || !source.Renderers.Select(row => row.Owner).SequenceEqual(before.Renderers.Select(row => row.Owner))) throw Error("native component type directories differ after topology preparation");
                if (before.CameraManager.Stack || before.CameraManager.InsideRenderOrCull || before.CameraManager.Projectors != 0 || before.CanvasManager.CallbackDispatch || before.Cameras.Any(row => row.TransientTextures.Any(value => value.Alias.Length != 0))
                    || before.Canvases.Any(row => row.AssetUsers != 0) || before.Renderers.Any(row => row.Push.Canvas == "@unsupported" || row.Pop.Canvas == "@unsupported")
                    || before.Canvases.Any(row => row.Handles.Owners.Contains("@unsupported"))) throw Error("target native execution/foreign UI state cannot be replaced at this boundary");
                if (!new HashSet<string>(source.CameraManager.All.Owners, StringComparer.Ordinal).SetEquals(before.CameraManager.All.Owners)
                    || !source.CanvasManager.TransformSystems.SequenceEqual(before.CanvasManager.TransformSystems)) throw Error("target native type/global registration differs");
                PreflightBindings(); admitted();
            }
            private void PreflightBindings()
            {
                // The native set comparator uses target addresses. Reject before
                // writing if it cannot reproduce the source callback sequence.
                foreach (var group in source.CanvasManager.Dirty) {
                    ulong previous = 0;
                    foreach (var key in group.Renderers) {
                        var pointer = unchecked((ulong)M.Owner(directory.Resolve(key)).ToInt64());
                        if (pointer <= previous) throw Error("target CanvasRenderer address order would change dirty callback dispatch");
                        previous = pointer;
                    }
                }
                void Typed(string key, Type type) { if (key.Length != 0) _ = ResidentNativeAssetReference.ResolveTyped(key,type,resolve); }
                foreach (var row in source.Cameras) {
                    Typed(row.TargetTexture, typeof(RenderTexture)); Typed(row.ReplacementShader, typeof(Shader)); Typed(row.OdsShader, typeof(Shader));
                    foreach (var key in row.OriginTextures.Concat(row.TemporaryTextures)) Typed(key, typeof(RenderTexture));
                    if (row.Scene.Length != 0) directory.SceneFor(row.Scene);
                    foreach (var value in row.ColorSurfaces.Concat(new[] { row.DepthSurface }))
                        if (value.Owner.Length != 0 && SurfaceHandle(value, resolve) == 0) throw Error("target render surface has not been reconstructed");
                    foreach (var image in row.Images.SelectMany(value => value.Entries)) { Typed(image.Owner, typeof(Component)); if (image.CallbackRva >= LoadingEngineClockProbe.RequirePinnedEngine().ModuleMemorySize) throw Error("image callback is outside the target engine"); }
                }
                void Texture(TextureBinding value) { if (value.Owner.Length != 0 && TextureId(value, resolve) == 0) throw Error("target texture ID has not been reconstructed"); }
                foreach (var row in source.Renderers) {
                    Typed(row.Parent, typeof(Canvas)); foreach (var key in row.PushMaterials.Owners.Concat(row.PopMaterials.Owners)) Typed(key, typeof(Material)); Texture(row.Texture); Texture(row.AlphaTexture);
                }
                foreach (var row in source.Canvases) {
                    Typed(row.Parent, typeof(Canvas)); Typed(row.Camera, typeof(Camera)); foreach (var key in row.Nested.Owners) Typed(key, typeof(Canvas));
                    foreach (var instruction in row.Instructions) { foreach (var key in instruction.Materials) Typed(key, typeof(Material)); Texture(instruction.Texture); Texture(instruction.AlphaTexture); }
                }
                foreach (var group in source.Shared) foreach (var key in group.Meshes) {
                    Typed(key, typeof(Mesh)); var wanted = ResidentSharedMeshState.Copy(group.State); var target = ResidentSharedMeshState.Capture(M.Ptr(IntPtr.Add(ResidentNativeAssetReference.ResolveTyped(key,typeof(Mesh),resolve),56)));
                    wanted.References = target.References; ResidentSharedMeshState.RequireEqual(wanted, target);
                }
                foreach (var callback in source.CanvasManager.Callbacks) {
                    if (callback.UserKind == "owner") resolve(callback.User);
                    if (callback.Rva >= LoadingEngineClockProbe.RequirePinnedEngine().ModuleMemorySize || callback.UserRva >= LoadingEngineClockProbe.RequirePinnedEngine().ModuleMemorySize)
                        throw Error("Canvas callback is outside the target engine");
                }
            }
            internal void Apply()
            {
                if (applied) throw Error("native Camera/UI plan was already applied"); scope.Require(); admitted(); RequireEqual(before, Capture(directory, bind));
                var meshes = new Dictionary<string, IntPtr>(StringComparer.Ordinal); var temporary = new List<Mesh>();
                try {
                    foreach (var row in source.Cameras) { scope.Require(); CanonCamera((Camera)directory.Resolve(row.Owner), admitted); }
                    foreach (var row in source.Canvases) { scope.Require(); CanonCanvas((Canvas)directory.Resolve(row.Owner), admitted); ClearCanvasData((Canvas)directory.Resolve(row.Owner), admitted); }
                    foreach (var group in source.Shared) {
                        scope.Require(); admitted(); IntPtr mesh;
                        if (group.Meshes.Length != 0) mesh = ResidentNativeAssetReference.ResolveTyped(group.Meshes[0],typeof(Mesh),resolve);
                        else {
                            Mesh? created = null; scope.Mutate(() => { created = new Mesh(); created.vertices = new Vector3[1]; }); if (created == null) throw Error("temporary shared Mesh owner allocation failed"); temporary.Add(created); mesh = M.Owner(created);
                            ResidentSharedMeshState.ApplyToUniqueStorage(group.State, M.Ptr(IntPtr.Add(mesh, 56)), admitted);
                        }
                        meshes.Add(group.Key, mesh);
                    }
                    foreach (var row in source.Renderers) { scope.Require(); ApplyCanvasRenderer(row, (CanvasRenderer)directory.Resolve(row.Owner), row.Shared.Length == 0 ? IntPtr.Zero : meshes[row.Shared], resolve, admitted); }
                    foreach (var row in source.Canvases) { scope.Require(); ApplyCanvas(row, (Canvas)directory.Resolve(row.Owner), meshes, resolve, admitted); }
                    foreach (var mesh in temporary) { scope.Require(); admitted(); scope.Mutate(() => NativeObject.DestroyImmediate(mesh)); } temporary.Clear();
                    foreach (var row in source.Cameras) { scope.Require(); ApplyCamera(row, (Camera)directory.Resolve(row.Owner), directory, resolve, admitted); }
                    ApplyCameraRegistry(source.CameraManager, resolve, admitted); ApplyCanvasRegistry(source.CanvasManager, directory, bind, resolve, admitted);
                    ApplyRenderPool(source.RenderTextures, admitted);
                    applied = true; Verify();
                } catch (Exception error) { scope.Fault(error); throw; }
                // On failure the process is faulted. Do not continue native
                // cleanup in a partially restored graph or retry this plan.
            }
            internal void Verify()
            {
                if (!applied) throw Error("native Camera/UI verification precedes application"); scope.Require(); admitted(); RequireEqual(source, Capture(directory, bind)); admitted();
            }
        }
    }
}
