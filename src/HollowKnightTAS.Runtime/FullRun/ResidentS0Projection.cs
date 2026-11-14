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
        internal static void RequireClockBodyBinding(ResidentClockInputState.Data clock, Action admitted)
        {
            admitted();
            var pointer = M.Ptr(IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, 0x19d7a68));
            if (pointer == IntPtr.Zero) throw Error("active TimeManager is absent");
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
        internal static byte[] Read(ResidentObjectDirectory directory, ResidentStaticRoots.Data statics,
            ResidentComponentAdapters.Data components, ResidentAssetPipeline.Data assets,
            ResidentObjectNativeState.Data objects, ResidentBoundaryProcessState.Data process)
        {
            directory.RequireCurrent();
            ResidentComponentAdapters.RequireComplete(components, directory.CopySnapshot());
            ResidentAssetPipeline.RequireCompleteCoverage(assets); ResidentObjectNativeState.RequireComplete(objects);
            _ = ResidentStaticRoots.ManifestBytes(statics);
            ResidentControlPlaneState.Validate(process.ControlPlane);
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
            ResidentSchedulerState.RequireCanonicalState(process.Scheduler);
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
                managedNativeWrapperStorageExact = true
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
