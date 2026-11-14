using System;
using System.Collections.Generic;
using System.Linq;
using Group = HollowKnightTAS.Runtime.FullRun.ResidentContinuationTrace.Group;
using GroupMeta = HollowKnightTAS.Runtime.FullRun.ResidentContinuationTrace.GroupMeta;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed partial class ResidentBoundaryCoordinator
    {
        [ThreadStatic] internal static Dictionary<string, (string fingerprint, GroupMeta meta)>? s_FrameGroupMeta;
        [ThreadStatic] internal static (string fingerprint, byte[] bytes)? s_CachedEncDir;
        [ThreadStatic] internal static (string scene, byte[] bytes)? s_CachedAssetValuesBytes;
        [ThreadStatic] internal static (string scene, byte[] bytes)? s_CachedStaticRootsBytes;

        private static Group GetOrCaptureGroup(string name, byte[] payload, string fingerprint, Func<IEnumerable<string>> expectedKeys, Func<IEnumerable<string>> observedKeys, string[] gaps)
        {
            if (s_FrameGroupMeta == null) s_FrameGroupMeta = new Dictionary<string, (string fingerprint, GroupMeta meta)>(StringComparer.Ordinal);
            if (s_FrameGroupMeta.TryGetValue(name, out var entry) && entry.fingerprint == fingerprint) {
                return Group.CaptureTrusted(name, payload, entry.meta.ExpectedHash, entry.meta.ExpectedCount, entry.meta.ObservedHash, entry.meta.ObservedCount, gaps);
            }
            var g = Group.CaptureWithMeta(name, payload, expectedKeys(), observedKeys(), gaps, out var meta);
            s_FrameGroupMeta[name] = (fingerprint, meta);
            return g;
        }

        // Single read-only pass using the same actual component, asset, static
        // and graph observers as B. No call to EnterS0/Prepare/Apply; active
        // caches and scheduler state are evidence at this precise native phase.
        internal Group[] ObserveContinuationFrame(int phase)
        {
            if (phase != 1 && phase != 3 || process is not ResidentBoundaryProcessState frameProcess) throw Error("frame observation requires an admitted native callback");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long t0 = 0;
            void Lap(string tag) {
                var now = sw.ElapsedMilliseconds;
                Modding.Logger.Log($"[HKTAS-OBS-PROF] {tag}: {now - t0}ms (total {now}ms)");
                t0 = now;
            }
            RequireIdle(); process.RequireAdmission();
            using var audio = ResidentAudioPauseLease.Begin();
            using var engine = new LoadingEngineClockProbe.ActualDValidation();
            var lastPeriodicCheck = Environment.TickCount;
            void Admitted() {
                var now = Environment.TickCount;
                if (now - lastPeriodicCheck >= 50) {
                    lastPeriodicCheck = now;
                    process.RequireAdmission();
                }
            }
            void ForceCheck() {
                process.RequireAdmission();
            }
            ForceCheck();
            Lap("init");
            var dir = ResidentObjectDirectory.Capture(ownership);
            Lap("directory_capture");
            var v = ObserveWorld(dir, Admitted, prepareResourceWrappers: true, captureManagedGraph: false);
            Lap("observe_world");
            var p = frameProcess.RequireFrameObservation(v.Process, v.Directory);
            Lap("require_frame_obs");
            ForceCheck();
            var directory = v.Directory.CurrentSnapshot;
            var dirFp = dir.StructureFingerprint;
            var sceneFp = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? "";
            var noGaps = Array.Empty<string>();
            IEnumerable<string> Tagged(string prefix, IEnumerable<string> keys) => keys.Select(key => prefix + key);
            var normalizedNativeClock = (byte[])p.Clock.Native.Clone();
            if (normalizedNativeClock.Length == 1192) {
                Array.Clear(normalizedNativeClock, 1136, 1184 - 1136);
            }
            Lap("keys_prepared");

            var g_admission = GetOrCaptureGroup("admission", Json(new { phase, readOnly = true }), "admission",
                () => new[] { "clock-epoch", "rng-producer-fence" }, () => new[] { "clock-epoch", "rng-producer-fence" }, noGaps);
            Lap("g_admission");

            var g_assetManifest = GetOrCaptureGroup("assetManifest", Json(v.AssetData.Manifest), sceneFp,
                () => v.Assets.SourceAssets.Keys, () => v.AssetData.Manifest.Select(x => x.Binding).ToArray(), noGaps);
            Lap("g_assetManifest");

            byte[] assetValuesBytes;
            if (s_CachedAssetValuesBytes != null && s_CachedAssetValuesBytes.Value.scene == sceneFp) {
                assetValuesBytes = s_CachedAssetValuesBytes.Value.bytes;
            } else {
                assetValuesBytes = System.Text.Encoding.UTF8.GetBytes(ResidentAssetDataWire.PortableValues(v.AssetData.Snapshots));
                s_CachedAssetValuesBytes = (sceneFp, assetValuesBytes);
            }
            var g_assetValues = GetOrCaptureGroup("assetValues", assetValuesBytes, sceneFp,
                () => v.AssetData.Manifest.Where(x => x.Kind == "dynamic").Select(x => x.Binding),
                () => v.AssetData.Snapshots.Select(x => x.Binding),
                v.AssetData.Snapshots.SelectMany(state => Tagged(state.Binding + "/", state.CoverageGaps)).Distinct().ToArray());
            Lap("g_assetValues");

            var actualClock = new List<string>(); if (p.Clock.Native.Length != 0) actualClock.Add("native-clock"); if (p.Clock.Observer.Length != 0) actualClock.Add("clock-observer");
            var g_clock = GetOrCaptureGroup("clock", Json(new { p.Clock.Backend, p.Clock.NativeFrame, Native = normalizedNativeClock, p.Clock.Observer }), "clock",
                () => new[] { "native-clock", "clock-observer" }, () => actualClock, p.Clock.CoverageGaps);
            Lap("g_clock");

            var encComp = ResidentComponentAdapters.EncodeObservation(v.Components.Data);
            Lap("encode_componentNative");
            var g_componentNative = GetOrCaptureGroup("componentNative", encComp, dirFp,
                () => directory.Objects.SelectMany(x => x.Components).Where(x => x.Ownership == ResidentComponentOwnership.Gameplay).Select(x => x.Key),
                () => v.Components.Data.Types.SelectMany(x => x.Owners),
                ResidentComponentAdapters.RequiredGaps(v.Components.Data));
            Lap("g_componentNative");

            byte[] encDir;
            if (s_CachedEncDir != null && s_CachedEncDir.Value.fingerprint == dirFp) {
                encDir = s_CachedEncDir.Value.bytes;
            } else {
                encDir = ResidentDirectoryWire.Encode(directory);
                s_CachedEncDir = (dirFp, encDir);
            }
            Lap("encode_directory");
            var g_directory = GetOrCaptureGroup("directory", encDir, dirFp,
                () => v.Directory.NativeBindings.Keys,
                () => directory.Objects.SelectMany(x => new[] { x.Key }.Concat(x.Components.Select(c => c.Key))),
                noGaps);
            Lap("g_directory");

            var inputKeys = p.Clock.Owners.SelectMany(owner => owner.Fields).Where(field => field.Root.Length != 0).Select(field => field.Root).ToArray();
            var graphKeys = v.Graph.Roots.Select(x => x.Binding).ToArray();
            var g_input = GetOrCaptureGroup("input", Json(new { p.Clock.Owners, p.Clock.Dependencies, p.Clock.MovieFrame, p.Clock.ConfiguredKeys, p.Clock.ViewportWidth,
                p.Clock.ViewportHeight, p.Clock.CanonicalMouseCaches, p.Clock.MouseEnabled, p.Clock.KeyboardPresent }), sceneFp,
                () => inputKeys,
                () => graphKeys.Where(key => inputKeys.Contains(key, StringComparer.Ordinal)),
                p.Clock.CoverageGaps);
            Lap("g_input");

            var encGraph = CanonicalCarryGraphWire.Encode(v.Graph);
            Lap("encode_managedGraph");
            var g_managedGraph = GetOrCaptureGroup("managedGraph", encGraph, sceneFp,
                () => v.Roots.Keys, () => graphKeys, v.Graph.Unsupported);
            Lap("g_managedGraph");

            var encObj = ResidentObjectNativeState.EncodeObservation(v.NativeObjects);
            Lap("encode_objectNative");
            var g_objectNative = GetOrCaptureGroup("objectNative", encObj, dirFp,
                () => v.Directory.NativeBindings.Keys.Concat(v.Assets.SourceAssets.Keys),
                () => v.NativeObjects.Rows.Select(row => row.Owner),
                v.NativeObjects.CoverageGaps);
            Lap("g_objectNative");

            var processKeys = new[] { "control-plane", "loader", "session", "native-managed-wrappers", "gui-native-values" };
            var g_process = GetOrCaptureGroup("process", Json(new { p.Session, p.ControlPlane, p.Loader, p.Wrappers, p.Gui }), sceneFp,
                () => processKeys,
                () => new[] { p.ControlPlane == null ? "" : "control-plane", p.Loader == null ? "" : "loader", p.Session == null ? "" : "session",
                    p.Wrappers == null ? "" : "native-managed-wrappers", p.Gui == null ? "" : "gui-native-values" }.Where(x => x.Length != 0),
                v.Process.CoverageGaps);
            Lap("g_process");

            var actualRng = new List<string>(); if (p.Clock.Random.Length != 0) actualRng.Add("portable-random"); if (p.Clock.Particles.Length == 4) actualRng.Add("particle-words");
            var g_rng = GetOrCaptureGroup("rng", Json(new { p.Clock.Random, p.Clock.Particles }), "rng",
                () => new[] { "portable-random", "particle-words" }, () => actualRng, p.Clock.CoverageGaps);
            Lap("g_rng");

            var encSched = Json(p.Scheduler);
            Lap("encode_scheduler");
            var g_scheduler = GetOrCaptureGroup("scheduler", encSched, dirFp,
                () => v.Directory.NativeBindings.Concat(v.Assets.SourceAssets.Where(x => !ResidentAssetBindings.IsSharedBinding(x.Key))).Where(x => x.Value is UnityEngine.MonoBehaviour || x.Value is UnityEngine.ScriptableObject).Select(x => x.Key),
                () => p.Scheduler.Owners.Select(x => x.Binding),
                p.Scheduler.CoverageGaps);
            Lap("g_scheduler");

            var staticSlots = v.Statics.Snapshot.Types.Where(type => type.Initialization == "Initialized").SelectMany(type => type.Fields)
                .Where(field => field.Storage == "static").Select(field => field.Key);
            byte[] encStat;
            if (s_CachedStaticRootsBytes != null && s_CachedStaticRootsBytes.Value.scene == sceneFp) {
                encStat = s_CachedStaticRootsBytes.Value.bytes;
            } else {
                encStat = ResidentStaticRoots.ManifestBytes(v.Statics.Snapshot);
                s_CachedStaticRootsBytes = (sceneFp, encStat);
            }
            Lap("encode_staticRoots");
            var g_staticRoots = GetOrCaptureGroup("staticRoots", encStat, sceneFp,
                () => staticSlots, () => v.Statics.ManagedRoots.Keys, v.Statics.Unsupported);
            Lap("g_staticRoots");

            var encType = ResidentManagedTypeCatalog.ManifestBytes(v.Catalog.Manifest);
            Lap("encode_typeManifest");
            var g_typeManifest = GetOrCaptureGroup("typeManifest", encType, sceneFp,
                () => v.Catalog.Types.Select(CanonicalCarryGraphCodec.Identity),
                () => v.Catalog.Manifest.Types.Select(x => x.Identity),
                noGaps);
            Lap("g_typeManifest");

            var groups = new[] {
                g_admission, g_assetManifest, g_assetValues, g_clock, g_componentNative, g_directory,
                g_input, g_managedGraph, g_objectNative, g_process, g_rng, g_scheduler, g_staticRoots, g_typeManifest
            };
            ForceCheck();
            audio.Complete();
            ForceCheck();
            Modding.Logger.Log($"[HKTAS-OBS] phase={phase} complete in {sw.ElapsedMilliseconds}ms");
            return groups;
        }
    }
}
