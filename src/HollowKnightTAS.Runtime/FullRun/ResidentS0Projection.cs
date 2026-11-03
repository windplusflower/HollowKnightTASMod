using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    // The serialized proof is produced ONLY after independently captured
    // native state satisfies every registered canonical predicate. Counts,
    // gameplay values and object/asset identity are D; process addresses and
    // metadata identities are dependencies. A matching schema is insufficient.
    internal static class ResidentS0Projection
    {
        // Directory coverage and manager-body coverage are separate. Derive
        // missing bodies from actual RTTI, never trust a caller-cleared gap list
        // or mistake the captured 48-byte Object base for the complete manager.
        internal static string[] RequiredProcessCoverageGaps(ResidentNativeGlobalObjects.Data directory)
        {
            ResidentNativeGlobalObjects.Validate(directory);
            var classes = directory.Classes.ToDictionary(value => value.Rva);
            return directory.CoverageGaps.Concat(directory.Rows.Where(row => classes[row.TypeRva].GlobalManager
                    && !IsTimeManager(classes[row.TypeRva]) && !ResidentPhysicsSettingsState.OwnsManager(classes[row.TypeRva])
                    && !ResidentSchedulerState.OwnsManager(classes[row.TypeRva]) && !ResidentTagManagerState.OwnsManager(classes[row.TypeRva])
                    && !ResidentNativeInputState.OwnsManager(classes[row.TypeRva]) && !ResidentBuildSettingsState.OwnsManager(classes[row.TypeRva]) && !ResidentSceneServiceSettings.OwnsManager(classes[row.TypeRva]) && !ResidentResourceManagerState.OwnsManager(classes[row.TypeRva]))
                .Select(row => "process/native-manager-body-not-covered:" + classes[row.TypeRva].Key))
                // The registered settings bodies do not contain the independent
                // navigation backend or streaming worker queues. Keep those gaps
                // visible even when the settings' PPtr/list happens to be empty.
                .Concat(directory.Rows.Where(row => ResidentSceneServiceSettings.OwnsManager(classes[row.TypeRva]))
                    .Select(row => "process/native-service-backend-not-covered:" + classes[row.TypeRva].Key))
                .Concat(directory.Rows.Count(row => ResidentResourceManagerState.OwnsManager(classes[row.TypeRva])) == 1 ? Array.Empty<string>() : new[] { "process/native-resource-manager-identity-absent-or-ambiguous" })
                .Concat(directory.Rows.Count(row => IsTimeManager(classes[row.TypeRva])) == 1 ? Array.Empty<string>() : new[] { "process/native-time-manager-identity-absent-or-ambiguous" })
                .Concat(directory.Rows.Count(row => ResidentPhysicsSettingsState.OwnsManager(classes[row.TypeRva])) == 1 ? Array.Empty<string>() : new[] { "process/native-physics-settings-identity-absent-or-ambiguous" })
                .Concat(directory.Rows.Count(row => ResidentTagManagerState.OwnsManager(classes[row.TypeRva])) == 1 ? Array.Empty<string>() : new[] { "process/native-tag-manager-identity-absent-or-ambiguous" })
                .Concat(directory.Rows.Count(row => ResidentSchedulerState.OwnsManager(classes[row.TypeRva])) == 1 ? Array.Empty<string>() : new[] { "process/native-delayed-call-manager-identity-absent-or-ambiguous" })
                .Concat(directory.Rows.Count(row => ResidentNativeInputState.OwnsManager(classes[row.TypeRva])) == 1 ? Array.Empty<string>() : new[] { "process/native-input-manager-identity-absent-or-ambiguous" })
                .Distinct().OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }
        private static bool IsTimeManager(ResidentNativeGlobalObjects.NativeClass type)
            => type.GlobalManager && type.Rva == 0x195d060 && type.Size == 0x430 && type.Name == "TimeManager" && type.Module == "Core" && type.Namespace == "";
        internal static void RequireClockBodyBinding(ResidentNativeGlobalObjects.CaptureResult globals, ResidentClockInputState.Data clock, Action admitted)
        {
            admitted(); var types = globals.Snapshot.Classes.Where(IsTimeManager).ToArray();
            if (types.Length != 1) throw Error("pinned TimeManager native class differs");
            var row = globals.Snapshot.Rows.SingleOrDefault(value => value.TypeRva == types[0].Rva)
                ?? throw Error("actual TimeManager owner is absent");
            if (!globals.ManagerPointers.TryGetValue(row.Binding, out var pointer)
                || pointer != M.Ptr(IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, 0x19d7a68)))
                throw Error("clock transport does not own the registered TimeManager");
            if (clock.FrameOnly) {
                if (clock.Native.Length != 1192 || BitConverter.ToUInt32(clock.Native, 0) != 0x31434652U ||
                    !M.Read(IntPtr.Add(pointer, 0x30), 0x400).SequenceEqual(clock.Native.Skip(104).Take(0x400)))
                    throw Error("complete active TimeManager body differs from the frame observation");
                admitted(); return;
            }
            if (clock.Native.Length != 1128 || BitConverter.ToUInt32(clock.Native, 0) != 0x31435448U)
                throw Error("native clock packet shape differs");
            var actual = M.Read(IntPtr.Add(pointer, 0x30), 0x400); var expected = new byte[0x400];
            var spans = new[] { new[] { 0x30, 44 }, new[] { 0x60, 44 }, new[] { 0x90, 44 }, new[] { 0xc0, 3 },
                new[] { 0xc8, 20 }, new[] { 0xe0, 24 }, new[] { 0xf8, 2 }, new[] { 0xfc, 12 }, new[] { 0x108, 808 } };
            foreach (var span in spans) Array.Copy(actual, span[0] - 0x30, expected, span[0] - 0x30, span[1]);
            if (!expected.SequenceEqual(clock.Native.Skip(104))) throw Error("independent complete TimeManager body differs from the clock transport");
            admitted();
        }
        internal static void RequireProcessCoverage(ResidentNativeGlobalObjects.Data directory)
        {
            var gaps = RequiredProcessCoverageGaps(directory);
            if (gaps.Length != 0) throw Error(string.Join(";", gaps));
        }
        internal static byte[] Read(ResidentObjectDirectory directory, ResidentStaticRoots.Data statics,
            ResidentComponentAdapters.Data components, ResidentAssetPipeline.Data assets,
            ResidentObjectNativeState.Data objects, ResidentBoundaryProcessState.Data process)
        {
            RequireProcessCoverage(process.GlobalDirectory); directory.RequireCurrent();
            ResidentComponentAdapters.RequireComplete(components, directory.CopySnapshot());
            ResidentAssetPipeline.RequireCompleteCoverage(assets); ResidentObjectNativeState.RequireComplete(objects);
            _ = ResidentStaticRoots.ManifestBytes(statics);
            ResidentControlPlaneState.Validate(process.ControlPlane);
            ResidentBoundaryProcessState.ValidateNativeAssetDirectory(process.GlobalDirectory, process.NativeAssets);
            ResidentNativeAssetDirectory.RequireBaseCoverage(process.NativeAssets, objects);
            ResidentNativeAssetDirectory.RequireDerivedPhysicsCoverage(process.NativeAssets, components.Physics, components.Physics.Globals!);
            if (statics.Unsupported.Length != 0 || process.Session.CoverageGaps.Length != 0 || process.ControlPlane.CoverageGaps.Length != 0 || process.LoaderCoverageGaps.Length != 0
                || process.Clock.CoverageGaps.Length != 0 || process.Scheduler.CoverageGaps.Length != 0 || process.Wrappers.CoverageGaps.Length != 0)
                throw Error("complete managed/process ownership is absent");
            RequireObjectState(objects);
            ResidentRendererState.RequireCanonicalState(components.Renderers);
            ResidentCameraCanvasState.RequireCanonicalState(components.CameraCanvas);
            ResidentAudioState.RequireCanonicalState(components.Audio);
            ResidentAnimatorState.RequireCompleteCoverage(components.Animation);
            ResidentParticleSystemState.RequireCompleteCoverage(components.Particles);
            ResidentPresentationTypes.RequireCanonicalState(components.Presentation);
            ResidentManagedNativeWrappers.RequireCanonicalState(process.Wrappers);
            ResidentPhysicsSettingsState.RequireComplete(process.PhysicsSettings);
            ResidentTagManagerState.RequireComplete(process.Tags);
            ResidentBoundaryProcessState.ValidateBuildSettings(process.GlobalDirectory, process.BuildSettings);
            foreach (var settings in process.BuildSettings) ResidentBuildSettingsState.RequireComplete(settings);
            ResidentNativeAudioFenceState.RequireCompleteCoverage(process.AudioFence);
            ResidentDirectorFence.RequireComplete(process.DirectorFence);
            ResidentSchedulerState.RequireCanonicalState(process.Scheduler);
            ResidentNativeInputState.RequireComplete(process.NativeInput);
            if (JsonConvert.SerializeObject(process.Wrappers) != JsonConvert.SerializeObject(ResidentManagedNativeWrappers.NormalizeStorage(process.Wrappers)))
                throw Error("native wrappers retain history-dependent capacity or sharing");
            // Full physics coverage includes the actual idle allocator bytes
            // and empty handle-cache predicates, not just public geometry.
            ResidentPhysicsState.RequireCompleteCoverage(components.Physics);
            foreach (var owner in directory.ManagedRoots.Values.OfType<MonoBehaviour>())
                RequireStringStorage(IntPtr.Add(M.Owner(owner), 72));
            // Asset completeness above verifies the current Mesh/Material cold
            // predicates and their D-preserving consumer/alias requirements.
            // Transform/registration/managed fields are retained complete D;
            // the D1 projection must not make them zero to force equality.
            directory.RequireCurrent();
            return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new {
                schema = "resident-complete-s0-v1",
                nativeObjectInactiveHandlesCleared = true,
                nativeObjectComponentStorageExact = true,
                monoNameStorageExact = true,
                rendererCachesColdAndUnique = true,
                cameraCanvasAndTransientPoolsCold = true,
                meshMaterialCachesCold = true,
                physicsIdleStorageCanonical = true,
                schedulerInactiveHandlesCleared = true,
                schedulerManagerAlignmentBytes = process.Scheduler.ManagerPadding,
                nativeInputStorage = Convert.ToBase64String(ResidentNativeInputState.EncodeS0(process.NativeInput)),
                managedNativeWrapperStorageExact = true,
                physicsSettingsStorage = Convert.ToBase64String(ResidentPhysicsSettingsState.EncodeS0(process.PhysicsSettings)),
                nativeAssetStorage = Convert.ToBase64String(ResidentNativeAssetDirectory.EncodeS0(process.NativeAssets)),
                resourceManagerStorage = Convert.ToBase64String(ResidentResourceManagerState.EncodeS0(process.ResourceManager)),
                sceneServiceStorage = Convert.ToBase64String(ResidentSceneServiceSettings.EncodeS0(process.SceneServices)),
                buildSettingsStorage = process.BuildSettings.Select(row => Convert.ToBase64String(ResidentBuildSettingsState.EncodeS0(row))).ToArray(),
                tagManagerStorage = Convert.ToBase64String(ResidentTagManagerState.EncodeS0(process.Tags)),
                nativeAudioFence = Convert.ToBase64String(ResidentNativeAudioFenceState.EncodeS0(process.AudioFence)),
                directorAdmission = process.DirectorFence
            }));
        }
        internal static void RequireObjectState(ResidentObjectNativeState.Data value)
        {
            ResidentObjectNativeState.RequireComplete(value);
            foreach (var row in value.Rows.Where(row => !row.Execution)) {
                if (!row.HandleAllocated && (row.CachePresent || !row.CacheMatches)) throw Error("inactive Object scripting handle cache is not canonical");
                if (row.Object != null && row.Object.ComponentCapacity != row.Object.Components.Length) throw Error("GameObject component capacity is not canonical");
            }
        }
        private static void RequireStringStorage(IntPtr address)
        {
            var value = M.String(address); var bytes = new UTF8Encoding(false, true).GetByteCount(value); var header = M.Read(address, 40);
            var inline = M.Ptr(header, 0) == IntPtr.Zero;
            if (inline != (bytes < 16) || !inline && M.U64(header, 8) != (ulong)bytes) throw Error("native name string storage is not canonical");
        }
        private static InvalidDataException Error(string message) => new InvalidDataException("Resident S0 projection: " + message);
    }
}
