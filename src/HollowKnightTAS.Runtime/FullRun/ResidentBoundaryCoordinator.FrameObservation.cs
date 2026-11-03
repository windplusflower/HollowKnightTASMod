using System;
using System.Collections.Generic;
using System.Linq;
using Group = HollowKnightTAS.Runtime.FullRun.ResidentContinuationTrace.Group;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed partial class ResidentBoundaryCoordinator
    {
        // Single read-only pass using the same actual component, asset, static
        // and graph observers as B. No call to EnterS0/Prepare/Apply; active
        // caches and scheduler state are evidence at this precise native phase.
        internal Group[] ObserveContinuationFrame(int phase)
        {
            if (phase != 1 && phase != 3 || process is not ResidentBoundaryProcessState frameProcess) throw Error("frame observation requires an admitted native callback");
            RequireIdle(); process.RequireAdmission();
            using var engine = new LoadingEngineClockProbe.ActualDValidation();
            var v = ObserveWorld(ResidentObjectDirectory.Capture(ownership), process.RequireAdmission);
            var p = frameProcess.RequireFrameObservation(v.Process, v.Directory);
            v.RequireUnchanged();
            var directory = v.Directory.CopySnapshot(); var native = p.GlobalDirectory.Rows;
            var assetKeys = v.AssetData.Manifest.Select(x => x.Binding).ToArray();
            var noGaps = Array.Empty<string>();
            IEnumerable<string> Nonempty(IEnumerable<string> keys) => keys.Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.Ordinal);
            IEnumerable<string> Tagged(string prefix, IEnumerable<string> keys) => keys.Select(key => prefix + key);
            var staticSlots = v.Statics.Snapshot.Types.Where(type => type.Initialization == "Initialized").SelectMany(type => type.Fields)
                .Where(field => field.Storage == "static").Select(field => field.Key);
            var inputKeys = p.Clock.Owners.SelectMany(owner => owner.Fields).Where(field => field.Root.Length != 0).Select(field => field.Root).ToArray();
            var actualRng = new List<string>(); if (p.Clock.Random.Length != 0) actualRng.Add("portable-random"); if (p.Clock.Particles.Length == 4) actualRng.Add("particle-words");
            var actualClock = new List<string>(); if (p.Clock.Native.Length != 0) actualClock.Add("native-clock"); if (p.Clock.Observer.Length != 0) actualClock.Add("clock-observer");
            // Registration includes native-only objects, so a missing portable
            // binding remains a coverage gap even if other keys happen to match.
            var globalGaps = ResidentS0Projection.RequiredProcessCoverageGaps(p.GlobalDirectory)
                .Concat(ResidentNativeAssetDirectory.RequiredGaps(p.NativeAssets)).Concat(p.ResourceManager.CoverageGaps).Concat(p.PhysicsSettings.CoverageGaps).Concat(p.Tags.CoverageGaps).Concat(p.BuildSettings.SelectMany(x => x.CoverageGaps))
                .Concat(ResidentNativeAudioFenceState.RequiredCoverageGaps(p.AudioFence)).Distinct(StringComparer.Ordinal);
            var graphKeys = v.Graph.Roots.Select(x => x.Binding).ToArray();
            var engineKeys = v.NativeObjects.Rows.Select(row => row.Owner).ToArray();
            var processKeys = new[] { "control-plane", "loader", "session", "native-managed-wrappers" };
            var groups = new[] {
                Group.Capture("admission", Json(new { phase, readOnly = true, audioFence = Convert.ToBase64String(ResidentNativeAudioFenceState.EncodeS0(p.AudioFence)), p.DirectorFence }),
                    new[] { "clock-epoch", "rng-producer-fence", "audio-worker-fence", "director-producer-fence" },
                    new[] { "clock-epoch", "rng-producer-fence", "audio-worker-fence", "director-producer-fence" },
                    ResidentNativeAudioFenceState.RequiredCoverageGaps(p.AudioFence).Concat(ResidentDirectorFence.Gaps(p.DirectorFence))),
                Group.Capture("assetManifest", Json(v.AssetData.Manifest), Nonempty(native.Where(row => row.Kind == "asset").Select(row => row.Binding)), assetKeys, p.GlobalDirectory.CoverageGaps),
                Group.Capture("assetValues", Json(v.AssetData.Snapshots), assetKeys, v.AssetData.Snapshots.Select(x => x.Binding),
                    v.AssetData.Snapshots.SelectMany(state => Tagged(state.Binding + "/", state.CoverageGaps)).Distinct()),
                Group.Capture("clock", Json(new { p.Clock.Backend, p.Clock.NativeFrame, p.Clock.Native, p.Clock.Observer }),
                    new[] { "native-clock", "clock-observer" }, actualClock, p.Clock.CoverageGaps),
                Group.Capture("componentNative", ResidentComponentAdapters.Encode(v.Components.Data, directory),
                    directory.Objects.SelectMany(x => x.Components).Where(x => x.Ownership == ResidentComponentOwnership.Gameplay).Select(x => x.Key),
                    v.Components.Data.Types.SelectMany(x => x.Owners), ResidentComponentAdapters.RequiredGaps(v.Components.Data)),
                Group.Capture("directory", ResidentDirectoryWire.Encode(directory), Nonempty(native.Where(row => row.Kind == "directory").Select(row => row.Binding)),
                    directory.Objects.SelectMany(x => new[] { x.Key }.Concat(x.Components.Select(c => c.Key))), p.GlobalDirectory.CoverageGaps),
                Group.Capture("globalNative", Json(new { p.GlobalDirectory, p.NativeAssets, p.SceneServices, p.ResourceManager, p.PhysicsSettings, p.Tags, p.BuildSettings, p.AudioFence, p.AudioOutputs }), Nonempty(native.Select(x => x.Binding)), engineKeys, globalGaps),
                Group.Capture("input", Json(new { p.Clock.Owners, p.Clock.Dependencies, p.Clock.MovieFrame, p.Clock.ConfiguredKeys, p.Clock.ViewportWidth,
                    p.Clock.ViewportHeight, p.Clock.CanonicalMouseCaches, p.Clock.MouseEnabled, p.Clock.KeyboardPresent, p.NativeInput }), inputKeys,
                    graphKeys.Where(key => inputKeys.Contains(key, StringComparer.Ordinal)), ResidentNativeInputState.RequiredGaps(p.NativeInput).Concat(p.Clock.CoverageGaps).Distinct()),
                Group.Capture("managedGraph", CanonicalCarryGraphWire.Encode(v.Graph), v.Roots.Keys, graphKeys, v.Graph.Unsupported),
                Group.Capture("objectNative", Json(v.NativeObjects), Nonempty(native.Select(x => x.Binding)), engineKeys, v.NativeObjects.CoverageGaps),
                // This includes full control-plane/frame-only event and field
                // values. The four named subdomains are required regardless of
                // whether their managed graph currently contains any objects.
                Group.Capture("process", Json(new { p.Session, p.ControlPlane, p.Loader, p.Wrappers }), processKeys,
                    new[] { p.ControlPlane == null ? "" : "control-plane", p.Loader == null ? "" : "loader", p.Session == null ? "" : "session",
                        p.Wrappers == null ? "" : "native-managed-wrappers" }.Where(x => x.Length != 0), v.Process.CoverageGaps),
                Group.Capture("rng", Json(new { p.Clock.Random, p.Clock.Particles }), new[] { "portable-random", "particle-words" }, actualRng, p.Clock.CoverageGaps),
                Group.Capture("scheduler", Json(p.Scheduler), v.Directory.NativeBindings.Concat(v.Assets.SourceAssets).Where(x => x.Value is UnityEngine.MonoBehaviour || x.Value is UnityEngine.ScriptableObject).Select(x => x.Key),
                    p.Scheduler.Owners.Select(x => x.Binding), p.Scheduler.CoverageGaps),
                Group.Capture("staticRoots", ResidentStaticRoots.ManifestBytes(v.Statics.Snapshot), staticSlots, v.Statics.ManagedRoots.Keys, v.Statics.Unsupported),
                Group.Capture("typeManifest", ResidentManagedTypeCatalog.ManifestBytes(v.Catalog.Manifest), v.Catalog.Types.Select(CanonicalCarryGraphCodec.Identity), v.Catalog.Manifest.Types.Select(x => x.Identity), noGaps)
            };
            process.RequireAdmission(); return groups;
        }
    }
}
