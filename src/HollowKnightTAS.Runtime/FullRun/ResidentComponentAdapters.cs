using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Selection is by the actual native component type. Object paths and counts
    // are measured directory data; they never select an implementation here.
    internal static class ResidentComponentAdapters
    {
        internal const int MaximumBytes = 32 * 1024 * 1024;
        internal sealed class Data
        {
            [JsonProperty(Required = Required.Always)] public int Version = 2;
            public TypeCoverage[] Types = Array.Empty<TypeCoverage>();
            public ResidentTransformState.Data Transforms = new ResidentTransformState.Data();
            public ResidentPhysicsState.Data Physics = new ResidentPhysicsState.Data();
            public ResidentRendererPropertyBlocks.Data Properties = new ResidentRendererPropertyBlocks.Data();
            public ResidentRendererState.Data Renderers = new ResidentRendererState.Data();
            public ResidentCameraCanvasState.Data CameraCanvas = new ResidentCameraCanvasState.Data();
            public ResidentAudioState.Data Audio = new ResidentAudioState.Data();
            [JsonProperty(Required = Required.Always)] public ResidentAnimatorState.Data Animation = new ResidentAnimatorState.Data();
            [JsonProperty(Required = Required.Always)] public ResidentParticleSystemState.Data Particles = new ResidentParticleSystemState.Data();
            [JsonProperty(Required = Required.Always)] public ResidentPresentationTypes.Data Presentation = new ResidentPresentationTypes.Data();
            public ResidentMonoLifecycle.Data[] Mono = Array.Empty<ResidentMonoLifecycle.Data>();
            public ResidentMonoNativeState.Data[] MonoNative = Array.Empty<ResidentMonoNativeState.Data>();
            public string[] CoverageGaps = Array.Empty<string>();
        }
        internal sealed class TypeCoverage
        {
            public ResidentObjectDirectory.TypeRecord Type = new ResidentObjectDirectory.TypeRecord();
            public string[] Owners = Array.Empty<string>();
            public string[] Domains = Array.Empty<string>();
            public string[] Gaps = Array.Empty<string>();
        }

        internal sealed class Observation
        {
            internal readonly Data Data;
            internal readonly ResidentPhysicsState.CaptureResult Physics;
            internal readonly IReadOnlyDictionary<IntPtr,ResidentCameraCanvasState.TransientOwner> TransientAssets;
            internal IReadOnlyDictionary<string, object> ManagedRoots => Physics.ManagedRoots;
            internal Observation(Data data, ResidentPhysicsState.CaptureResult physics, IReadOnlyDictionary<IntPtr,ResidentCameraCanvasState.TransientOwner> transientAssets)
            { Data = data; Physics = physics; TransientAssets = transientAssets; }
        }
        internal static Data Capture(ResidentObjectDirectory directory, Func<object, string?> bind)
            => CaptureWithManagedRoots(directory, bind).Data;
        internal static Observation CaptureWithManagedRoots(ResidentObjectDirectory directory, Func<object, string?> bind)
        {
            if (directory == null || bind == null) throw new ArgumentNullException();
            string Binding(UnityEngine.Object value) => bind(value) ?? throw Error("native reference is unbound: " + value.GetType().FullName);
            var snapshot = directory.CopySnapshot();
            ResidentFailureEvidence.Mark("component physics"); var physics = ResidentPhysicsState.CaptureWithManagedRoots(directory, Binding);
            var transientAssets = new Dictionary<IntPtr,ResidentCameraCanvasState.TransientOwner>();
            var result = new Data {
                Types = DescribeTypes(snapshot),
                Transforms = ResidentFailureEvidence.Measure("component Transforms",() => ResidentTransformState.Capture(directory)),
                Physics = physics.Data,
                Properties = ResidentFailureEvidence.Measure("component Properties",() => ResidentRendererPropertyBlocks.Capture(directory, bind)),
                Renderers = ResidentFailureEvidence.Measure("component Renderers",() => ResidentRendererState.Capture(directory, bind)),
                CameraCanvas = ResidentFailureEvidence.Measure("component CameraCanvas",() => ResidentCameraCanvasState.Capture(directory, bind, value => transientAssets.Add(value.Pointer,value))),
                Audio = ResidentFailureEvidence.Measure("component Audio",() => ResidentAudioState.Capture(directory, bind)),
                Animation = ResidentFailureEvidence.Measure("component Animation",() => ResidentAnimatorState.Capture(directory, bind)),
                Particles = ResidentFailureEvidence.Measure("component Particles",() => ResidentParticleSystemState.Capture(directory, bind)),
                Presentation = ResidentFailureEvidence.Measure("component Presentation",() => ResidentPresentationTypes.Capture(directory, bind)),
                Mono = ResidentFailureEvidence.Measure("component Mono",() => ResidentMonoLifecycle.Capture(GameplayMono(directory), Binding)),
                MonoNative = ResidentFailureEvidence.Measure("component MonoNative",() => ResidentMonoNativeState.CaptureMany(GameplayMono(directory), bind))
            };
            ResidentFailureEvidence.Mark("component coverage");
            result.CoverageGaps = RequiredGaps(result);
            ResidentFailureEvidence.Mark("component validation");
            Validate(result, snapshot);
            return new Observation(result, physics, transientAssets);
        }

        internal static TypeCoverage[] DescribeTypes(ResidentObjectDirectory.Snapshot directory)
        {
            ResidentObjectDirectory.Validate(directory);
            return directory.Objects.SelectMany(owner => owner.Components)
                .Where(component => component.Ownership == ResidentComponentOwnership.Gameplay)
                .GroupBy(component => component.Type.Key, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => {
                    var type = group.First().Type.Resolve();
                    var domains = new List<string> { "directory/object-component-metadata:D", "managed/complete-instance-fields:D", "native/owner-binding:Dependency" };
                    var gaps = new List<string>();
                    if (type == typeof(Transform) || type == typeof(RectTransform)) domains.Add("transform/local-cache-hierarchy-dispatch:D");
                    else if (typeof(MonoBehaviour).IsAssignableFrom(type)) {
                        domains.Add("mono/lifecycle-enabled-registration:D");
                        domains.Add("mono/name-script-binding:D");
                        domains.Add("mono/name-storage-rebuild:S0");
                        domains.Add("mono/script-class-method-cache:Dependency");
                    } else if (type == typeof(Rigidbody2D) || type == typeof(BoxCollider2D) || type == typeof(CircleCollider2D)
                        || type == typeof(CapsuleCollider2D) || type == typeof(PolygonCollider2D) || type == typeof(EdgeCollider2D))
                        domains.Add("physics/component-fixture-world:D");
                    else if (typeof(Renderer).IsAssignableFrom(type)) {
                        domains.Add("renderer/property-semantic-values:D");
                        domains.Add("renderer/property-storage-rebuild:S0");
                        domains.Add("renderer/property-ids-packing-hash:Dependency");
                        domains.Add("renderer/drawing-bounds-registration-subtype:D");
                    } else if (type == typeof(MeshFilter)) domains.Add("mesh-filter/mesh-binding:D");
                    else if (type == typeof(Camera) || type == typeof(Canvas) || type == typeof(CanvasRenderer)) {
                        domains.Add("camera-canvas/settings-cpu-geometry-registries:D");
                        domains.Add("camera-canvas/render-batch-caches:S0");
                        domains.Add("camera-canvas/native-resource-binding:Dependency");
                    }
                    else if (type == typeof(AudioSource) || type == typeof(AudioListener)) {
                        domains.Add("audio/source-listener-settings-curves-registries:D");
                        domains.Add("audio/curve-storage:S0");
                        domains.Add("audio/native-channel-resource-binding:Dependency");
                    }
                    else if (type == typeof(Animator)) {
                        domains.Add("animator/controller-playables-and-director:D");
                        domains.Add("animator/scalar-padding:S0");
                        domains.Add("animator/resource-and-native-owner-bindings:Dependency");
                    }
                    else if (type == typeof(ParticleSystem)) domains.Add("particles/emitter-simulation-and-manager:D");
                    else if (ResidentPresentationTypes.Supports(type)) domains.Add("presentation/" + ResidentPresentationTypes.Kind(type) + ":D");
                    else gaps.Add("component/native-type-unsupported");
                    return new TypeCoverage { Type = group.First().Type.Copy(), Owners = group.Select(value => value.Key).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                        Domains = domains.OrderBy(value => value, StringComparer.Ordinal).ToArray(), Gaps = gaps.ToArray() };
                }).ToArray();
        }

        // Recompute mandatory gaps from the actual types and subdomain DTOs.
        // Erasing the serialized CoverageGaps array cannot grant admission.
        internal static string[] RequiredGaps(Data value)
        {
            var gaps = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var type in value.Types) foreach (var gap in type.Gaps) gaps.Add(type.Type.Key + "/" + gap);
            foreach (var gap in value.Transforms.CoverageGaps) gaps.Add("transform/" + gap);
            foreach (var gap in value.Physics.CoverageGaps) gaps.Add("physics/" + gap);
            foreach (var gap in ResidentRendererState.RequiredCoverageGaps(value.Renderers)) gaps.Add("renderer/" + gap);
            foreach (var gap in ResidentCameraCanvasState.RequiredCoverageGaps(value.CameraCanvas)) gaps.Add("camera-canvas/" + gap);
            foreach (var gap in ResidentAudioState.RequiredCoverageGaps(value.Audio)) gaps.Add("audio/" + gap);
            foreach (var gap in ResidentAnimatorState.RequiredCoverageGaps(value.Animation)) gaps.Add("animator/" + gap);
            foreach (var gap in ResidentParticleSystemState.RequiredCoverageGaps(value.Particles)) gaps.Add("particles/" + gap);
            foreach (var gap in ResidentPresentationTypes.RequiredCoverageGaps(value.Presentation)) gaps.Add("presentation/" + gap);
            foreach (var row in value.MonoNative) foreach (var gap in ResidentMonoNativeState.RequiredGaps(row)) gaps.Add(row.Owner + "/" + gap);
            if (value.Physics.Globals == null) gaps.Add("physics/global-state-absent");
            foreach (var row in value.Properties.Rows) {
                if (row.Main != null) foreach (var gap in row.Main.CoverageGaps) gaps.Add(row.Owner + "/properties/" + gap);
                foreach (var sheet in row.PerMaterial) if (sheet != null)
                    foreach (var gap in sheet.CoverageGaps) gaps.Add(row.Owner + "/properties/" + gap);
            }
            return gaps.ToArray();
        }

        internal static void Validate(Data value, ResidentObjectDirectory.Snapshot directory)
        {
            if (value == null || value.Version != 2 || value.Types == null || value.Transforms == null || value.Physics == null
                || value.Animation == null || value.Particles == null || value.Presentation == null
                || value.Properties == null || value.Renderers == null || value.CameraCanvas == null || value.Audio == null || value.Mono == null || value.MonoNative == null || value.CoverageGaps == null) throw Error("component snapshot shape differs");
            var expected = DescribeTypes(directory);
            if (JsonConvert.SerializeObject(value.Types) != JsonConvert.SerializeObject(expected)) throw Error("complete actual component type/owner coverage differs");
            ResidentTransformState.Validate(value.Transforms);
            ResidentPhysicsState.Validate(value.Physics);
            ResidentRendererPropertyBlocks.Validate(value.Properties);
            ResidentRendererState.Validate(value.Renderers);
            ResidentCameraCanvasState.Validate(value.CameraCanvas);
            ResidentAudioState.Validate(value.Audio);
            ResidentAnimatorState.ValidateOwners(value.Animation, directory);
            ResidentParticleSystemState.Validate(value.Particles);
            ResidentPresentationTypes.Validate(value.Presentation);
            var particleType = ResidentObjectDirectory.TypeRecord.Capture(typeof(ParticleSystem)).Key;
            var presentationTypes = new[] { typeof(CanvasGroup), typeof(UnityEngine.Rendering.SortingGroup), typeof(Light), typeof(TextMesh) }
                .ToDictionary(t => ResidentObjectDirectory.TypeRecord.Capture(t).Key, ResidentPresentationTypes.Kind, StringComparer.Ordinal);
            var actual = directory.Objects.SelectMany(row => row.Components).OrderBy(row => row.Key, StringComparer.Ordinal).ToArray();
            if (!actual.Where(row => row.Type.Key == particleType).Select(row => (row.Key, row.Ownership == ResidentComponentOwnership.Execution))
                .SequenceEqual(value.Particles.Rows.Select(row => (row.Owner, row.Execution))))
                throw Error("particle owner directory differs");
            if (!actual.Where(row => presentationTypes.ContainsKey(row.Type.Key)).Select(row => (row.Key, presentationTypes[row.Type.Key], row.Ownership == ResidentComponentOwnership.Execution))
                .SequenceEqual(value.Presentation.Rows.Select(row => (row.Owner, row.Kind, row.Execution))))
                throw Error("presentation component owner directory differs");
            var monos = expected.Where(row => typeof(MonoBehaviour).IsAssignableFrom(row.Type.Resolve()))
                .SelectMany(row => row.Owners).OrderBy(key => key, StringComparer.Ordinal).ToArray();
            if (!value.Mono.Select(row => row.Owner).SequenceEqual(monos)) throw Error("complete gameplay Mono directory differs");
            if (!value.MonoNative.Select(row => row.Owner).SequenceEqual(monos)) throw Error("complete native Mono directory differs");
            ResidentMonoNativeState.ValidateMany(value.MonoNative);
            if (value.Mono.Length != 0 && value.Mono.Any(row => !ReferenceEquals(row.Registry, value.Mono[0].Registry)))
                throw Error("complete Mono registry alias differs");
            if (!value.CoverageGaps.SequenceEqual(RequiredGaps(value))) throw Error("serialized component coverage differs from required coverage");
        }
        internal static void RequireComplete(Data value, ResidentObjectDirectory.Snapshot directory)
        {
            Validate(value, directory);
            if (value.CoverageGaps.Length != 0) throw Error("coverage gaps: " + string.Join(";", value.CoverageGaps.Take(16)));
            ResidentTransformState.RequireCompleteCoverage(value.Transforms);
            ResidentPhysicsState.RequireCompleteCoverage(value.Physics);
            ResidentRendererState.RequireCompleteCoverage(value.Renderers);
            ResidentCameraCanvasState.RequireCompleteCoverage(value.CameraCanvas);
            ResidentAudioState.RequireCompleteCoverage(value.Audio);
            ResidentAnimatorState.RequireCompleteCoverage(value.Animation);
            ResidentParticleSystemState.RequireCompleteCoverage(value.Particles);
            ResidentPresentationTypes.RequireCompleteCoverage(value.Presentation);
            ResidentMonoNativeState.ValidateMany(value.MonoNative, requireComplete: true);
            foreach (var row in value.Properties.Rows) {
                if (row.Main != null) ResidentShaderPropertySheet.RequireCompleteCoverage(row.Main);
                foreach (var sheet in row.PerMaterial) if (sheet != null) ResidentShaderPropertySheet.RequireCompleteCoverage(sheet);
            }
        }
        internal static void RequireCanonicalizableCoverage(Data value, ResidentObjectDirectory.Snapshot directory)
        {
            Validate(value, directory);
            if (value.Types.Any(type => type.Gaps.Length != 0)) throw Error("actual native component type coverage is incomplete");
            ResidentTransformState.RequireCompleteCoverage(value.Transforms);
            ResidentPhysicsState.RequireCanonicalizableCoverage(value.Physics);
            ResidentRendererState.RequireCanonicalizableCoverage(value.Renderers);
            ResidentCameraCanvasState.RequireCanonicalizableCoverage(value.CameraCanvas);
            ResidentAudioState.RequireCanonicalizableCoverage(value.Audio);
            ResidentAnimatorState.RequireCanonicalizableCoverage(value.Animation);
            ResidentParticleSystemState.RequireCanonicalizableCoverage(value.Particles);
            ResidentPresentationTypes.RequireCanonicalizableCoverage(value.Presentation);
            ResidentMonoNativeState.ValidateMany(value.MonoNative, requireComplete: true);
            foreach (var row in value.Properties.Rows) {
                if (row.Main != null) ResidentShaderPropertySheet.RequireCompleteCoverage(row.Main);
                foreach (var sheet in row.PerMaterial) if (sheet != null) ResidentShaderPropertySheet.RequireCompleteCoverage(sheet);
            }
        }
        internal static Action CanonBeforeAssets(ResidentObjectDirectory directory, ResidentMutationScope scope,
            Func<object, string?> bind, Func<string, UnityEngine.Object> resolve, Action admitted, Action<ResidentCameraCanvasState.TransientOwner> requireTransientExclusive)
            => ResidentCameraCanvasState.Canon(directory, scope, resolve, bind, admitted, requireTransientExclusive);

        // Shared S0 operation, called with the same inputs and order for every
        // entrance. D remains in the native property values while only their
        // history-dependent COW storage is rebuilt by the engine.
        internal static Action Canon(ResidentObjectDirectory directory, ResidentMutationScope scope,
            Func<object, string?> bind, Func<string, UnityEngine.Object> resolve, Action admitted)
        {
            var blocks = ResidentRendererPropertyBlocks.PrepareCanon(directory, scope, resolve, bind, admitted);
            blocks.Apply();
            var renderers = ResidentRendererState.Canon(directory, scope, resolve, bind, admitted);
            var audio = ResidentAudioState.Canon(directory, scope, bind, admitted);
            var animation = ResidentAnimatorState.Canon(directory, scope, bind, admitted);
            var particles = ResidentParticleSystemState.Canon(directory, scope, bind, admitted);
            var presentation = ResidentPresentationTypes.Canon(directory, scope, bind, admitted);
            foreach (var mono in GameplayMono(directory)) ResidentMonoNativeState.Canon(mono, scope, admitted);
            return () => { blocks.Verify(); renderers(); audio(); animation(); particles(); presentation(); };
        }

        internal sealed class Plan
        {
            private readonly Data source;
            private readonly ResidentObjectDirectory directory;
            private readonly ResidentMutationScope scope;
            private readonly Func<object, string?> bind;
            private readonly Func<string, UnityEngine.Object> resolve;
            private readonly Action admitted;
            private readonly Func<string, object?> resolveManaged;
            private ResidentTransformState.Plan? transforms;
            private ResidentPhysicsState.Plan? physics;
            private ResidentRendererPropertyBlocks.Plan? properties;
            private ResidentRendererState.Plan? renderers;
            private ResidentCameraCanvasState.Plan? cameraCanvas;
            private ResidentAudioState.Plan? audio;
            private ResidentAnimatorState.Plan? animation;
            private ResidentParticleSystemState.Plan? particles;
            private ResidentPresentationTypes.Plan? presentation;
            private ResidentMonoLifecycle.Plan? lifecycle;
            private ResidentMonoNativeState.Plan[]? monoNative;
            private bool nativeApplied, lifecycleApplied;
            internal Plan(Data saved, ResidentObjectDirectory directory, ResidentMutationScope scope,
                Func<object, string?> bind, Func<string, UnityEngine.Object> resolve, Action admitted, Func<string, object?> resolveManaged)
            {
                this.directory = directory; this.scope = scope; this.bind = bind; this.resolve = resolve; this.admitted = admitted;
                this.resolveManaged = resolveManaged ?? throw new ArgumentNullException(nameof(resolveManaged));
                source = Decode(Encode(saved, directory.CopySnapshot()), directory.CopySnapshot());
                RequireComplete(source, directory.CopySnapshot());
            }
            internal void ApplyNative()
            {
                if (nativeApplied) throw Error("native component plan is single use");
                scope.Require(); admitted();
                // Prepare each plan immediately before its domain write: earlier
                // native setters may legitimately invalidate later witnesses.
                physics = ResidentPhysicsState.Prepare(source.Physics, directory, Binding, resolve, scope, admitted, resolveManaged);
                physics.ApplyStructure();
                transforms = ResidentTransformState.Prepare(source.Transforms, directory, scope, admitted);
                transforms.Apply(); physics.Apply();
                renderers = ResidentRendererState.Prepare(source.Renderers, directory, scope, resolve, bind, admitted);
                renderers.Apply();
                properties = ResidentRendererPropertyBlocks.Prepare(source.Properties, directory, scope, resolve, bind, admitted);
                properties.Apply();
                cameraCanvas = ResidentCameraCanvasState.Prepare(source.CameraCanvas, directory, scope, resolve, bind, admitted); cameraCanvas.Apply();
                audio = ResidentAudioState.Prepare(source.Audio, directory, scope, resolve, bind, admitted); audio.Apply();
                animation = ResidentAnimatorState.Prepare(source.Animation, directory, scope, resolve, bind, admitted); animation.Apply();
                particles = ResidentParticleSystemState.Prepare(source.Particles, directory, scope, resolve, bind, admitted); particles.Apply();
                presentation = new ResidentPresentationTypes.Plan(source.Presentation, directory, scope, resolve, bind, admitted); presentation.Apply();
                monoNative = source.MonoNative.Select(row => ResidentMonoNativeState.Prepare(row, resolve(row.Owner), scope, resolve, bind, admitted)).ToArray();
                foreach (var mono in monoNative) mono.Apply();
                nativeApplied = true;
            }
            internal void ApplyLifecycle(Action reconcileScheduler)
            {
                if (!nativeApplied || lifecycleApplied) throw Error("lifecycle order or single-use state differs");
                scope.Require(); admitted();
                lifecycle = ResidentMonoLifecycle.Prepare(source.Mono, GameplayMono(directory), scope, admitted, Binding, resolve);
                lifecycle.Apply(reconcileScheduler); lifecycleApplied = true;
            }
            internal void Verify()
            {
                if (!nativeApplied || !lifecycleApplied) throw Error("component verification precedes complete application");
                admitted(); transforms!.Verify(); physics!.Verify(); renderers!.Verify(); properties!.Verify(); cameraCanvas!.Verify(); audio!.Verify(); lifecycle!.Verify();
                animation!.Verify(); particles!.Verify(); presentation!.Verify();
                foreach (var mono in monoNative!) mono.Verify(); admitted();
            }
            private string Binding(UnityEngine.Object value) => bind(value) ?? throw Error("native reference is unbound");
        }
        internal static Plan Prepare(Data source, ResidentObjectDirectory directory, ResidentMutationScope scope,
            Func<object, string?> bind, Func<string, UnityEngine.Object> resolve, Action admitted, Func<string, object?> resolveManaged)
            => new Plan(source, directory, scope, bind, resolve, admitted, resolveManaged);
        private static MonoBehaviour[] GameplayMono(ResidentObjectDirectory directory) => directory.ManagedRoots
            .Where(pair => pair.Value is MonoBehaviour).OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (MonoBehaviour)pair.Value).ToArray();

        private static readonly JsonSerializerSettings Wire = new JsonSerializerSettings {
            PreserveReferencesHandling = PreserveReferencesHandling.Objects, TypeNameHandling = TypeNameHandling.None,
            MissingMemberHandling = MissingMemberHandling.Error, MaxDepth = 128
        };
        internal static byte[] Encode(Data value, ResidentObjectDirectory.Snapshot directory)
        {
            Validate(value, directory);
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, Formatting.None, Wire));
            if (bytes.Length > MaximumBytes) throw Error("component payload exceeds package bound");
            return bytes;
        }
        internal static Data Decode(byte[] bytes, ResidentObjectDirectory.Snapshot directory)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumBytes) throw Error("component payload size differs");
            Data value;
            try { value = JsonConvert.DeserializeObject<Data>(new UTF8Encoding(false, true).GetString(bytes), Wire) ?? throw Error("component payload absent"); }
            catch (Exception error) when (error is JsonException || error is DecoderFallbackException) { throw new InvalidDataException("Resident component payload is malformed.", error); }
            Validate(value, directory); return value;
        }
        private static InvalidDataException Error(string reason) => new InvalidDataException("Resident component adapters: " + reason);
    }
}
