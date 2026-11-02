using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // UnityPlayer d97e...: SetMesh icall table entry 34 -> RVA 373150 ->
    // 233510; that producer stores the result of 6b95b0 in CanvasRenderer+48h.
    // 6b95b0 returns Mesh+38h with a refcount increment. This is a read-only
    // identity comparison, not a native write or a cross-process identity.
    // SHA d97e92a7640b10580b4e60139eacf01828f74baaef53f75e08b9fdd6193fbe5e:
    // SetMesh 2335dc tests Mesh+99h; zero logs an error and retains null data.
    // SharedMeshData ctor 6d22f9 sets refcount=1. UnshareMeshData eb0b66-73
    // returns unchanged exactly at count=1; otherwise eb0bd8-eb0c2e copies,
    // releases old data and replaces Mesh+38h. SetVertexData eb07d3 calls it.
    // Reject shared data BEFORE writes; post-write identity is only readback.
    internal sealed class CanonicalPersistentSpriteNativeGuard
    {
        private static readonly FieldInfo Pointer = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static void RequireOriginalProducers()
        {
            // Audited original callers: only DepthOfFieldDeprecated.AddBokeh
            // calls DrawMeshNow, and it draws Quads.GetMesh's independent new
            // meshes. No original CommandBuffer.DrawMesh or BatchRendererGroup
            // caller exists. This finite argument does not admit other mods.
            var expected = new Dictionary<string, string>(StringComparer.Ordinal) {
                ["Assembly-CSharp"] = "2b54cdaf-7a93-4f0e-b297-d50c4090ea9f",
                ["Assembly-CSharp-firstpass"] = "b2727848-be8b-4f4d-9c64-5f08b403527a",
                ["UnityEngine.UI"] = "b4d135af-a1fd-407b-bcc7-280e400336cf",
                ["PlayMaker"] = "29eb59fa-5c99-43cd-8f33-6db074533d31"
            };
            var loaded = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var pair in expected) {
                var matches = loaded.Where(assembly => assembly.GetName().Name == pair.Key).ToArray();
                if (matches.Length != 1 || matches[0].ManifestModule.ModuleVersionId.ToString("D") != pair.Value)
                    throw new InvalidOperationException("Persistent sprite original mesh producer module differs: " + pair.Key);
            }
        }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr read);
        private readonly SortedDictionary<string, tk2dSprite> owners;
        private readonly SortedDictionary<string, UnityEngine.Object> finalOwners;
        private long[]? originalData;
        private CanonicalPersistentSpriteNativeGuard(SortedDictionary<string, tk2dSprite> owners)
        { this.owners = owners; finalOwners = CanonicalFinalVisualCarry.Directory(); }
        internal static CanonicalPersistentSpriteNativeGuard Capture(IEnumerable<Component> source)
        {
            var components = source.ToArray();
            if (components.Length > 100000 || components.Any(component => component == null))
                throw new InvalidDataException("Persistent sprite native component inventory differs.");
            var guard = new CanonicalPersistentSpriteNativeGuard(CanonicalPersistentSpriteDirectory.Resolve());
            guard.RequireNoOutsideConsumers(components.Where(IsConsumer).ToArray()); return guard;
        }
        private static bool IsConsumer(Component component) => component is MeshFilter || component is MeshCollider
            || component is SkinnedMeshRenderer || component is CanvasRenderer || component is MeshRenderer
            || component is ParticleSystemRenderer || component is ParticleSystem;
        internal static Action ProtectFinalVisual(Mesh[] meshes, MeshFilter[]? allowedFilters = null)
        {
            allowedFilters ??= Array.Empty<MeshFilter>();
            if (meshes.Length == 0 || meshes.Length > 8 || meshes.Any(mesh => mesh == null) || meshes.Distinct().Count() != meshes.Length)
                throw new InvalidOperationException("Registered visual mesh identity directory differs.");
            long[] original;
            using (LoadingEngineClockProbe.ReadOnlyCheckpointScope())
                original = meshes.Select(mesh => BitConverter.ToInt64(Native(mesh, 64), 56)).ToArray();
            void Verify() {
                using var validation = LoadingEngineClockProbe.ReadOnlyCheckpointScope();
                RequireOriginalProducers();
                var data = meshes.Select(mesh => BitConverter.ToInt64(Native(mesh, 64), 56)).ToArray();
                if (!original.SequenceEqual(data) || data.Any(value => value == 0 || RefCount(value) != 1) || data.Distinct().Count() != data.Length)
                    throw new InvalidOperationException("Registered visual mesh shared data changed or has retained consumers.");
                var count = 0; var particleSlots = 0;
                void All<T>(Action<T> action) where T : Component {
                    var items = Resources.FindObjectsOfTypeAll<T>(); count = checked(count + items.Length);
                    if (count > 100000 || items.Any(item => item == null)) throw new InvalidOperationException("Registered visual consumer inventory differs.");
                    foreach (var item in items) action(item);
                }
                void Reject(Mesh mesh) { if (mesh != null && meshes.Contains(mesh)) throw new InvalidOperationException("Registered visual mesh has an outside/physical consumer."); }
                All<MeshFilter>(owner => { if (!allowedFilters.Contains(owner)) Reject(owner.sharedMesh); });
                All<MeshCollider>(owner => Reject(owner.sharedMesh)); All<SkinnedMeshRenderer>(owner => Reject(owner.sharedMesh));
                All<CanvasRenderer>(owner => { if (data.Contains(BitConverter.ToInt64(Native(owner, 80), 72))) throw new InvalidOperationException("Registered visual Canvas mesh consumer is unsupported."); });
                All<MeshRenderer>(owner => { Reject(owner.additionalVertexStreams); Reject(owner.enlightenVertexStream); });
                All<ParticleSystemRenderer>(owner => {
                    var size = owner.meshCount;
                    if (size < 0 || size > 100000 - particleSlots) throw new InvalidOperationException("Registered visual particle slots exceed bound.");
                    particleSlots += size; var values = new Mesh[size];
                    if (owner.GetMeshes(values) != size || owner.meshCount != size) throw new InvalidOperationException("Registered visual particle inventory changed.");
                    Reject(owner.mesh); foreach (var mesh in values) Reject(mesh);
                });
                All<ParticleSystem>(owner => { var shape = owner.shape; Reject(shape.mesh);
                    if (shape.meshRenderer != null) Reject(shape.meshRenderer.GetComponent<MeshFilter>()?.sharedMesh!);
                    if (shape.skinnedMeshRenderer != null) Reject(shape.skinnedMeshRenderer.sharedMesh); });
            }
            Verify(); return Verify;
        }
        private static byte[] Native(UnityEngine.Object owner, int size)
        {
            LoadingEngineClockProbe.RequirePinnedEngine();
            var pointer = (IntPtr)Pointer.GetValue(owner)!; var bytes = new byte[size];
            if (pointer == IntPtr.Zero || !ReadProcessMemory(GetCurrentProcess(), pointer, bytes, new UIntPtr((uint)size), out var read)
                || read.ToUInt64() != (ulong)size || BitConverter.ToInt32(bytes, 8) != owner.GetInstanceID())
                throw new InvalidOperationException("Persistent sprite native owner identity cannot be read.");
            return bytes;
        }
        private static int RefCount(long address)
        {
            var bytes = new byte[8];
            if (!ReadProcessMemory(GetCurrentProcess(), new IntPtr(address), bytes, new UIntPtr(8), out var read) || read.ToUInt64() != 8)
                throw new InvalidOperationException("Persistent sprite shared data cannot be read.");
            return BitConverter.ToInt32(bytes, 4);
        }
        internal void RequireNoOutsideConsumers()
        {
            // Original producers retire and create native UI components. A
            // cached S0 world array is no longer a live consumer inventory.
            // These typed native indices include inactive/unloaded resources;
            // no managed/world graph is walked, and no result is retained.
            var current = Resources.FindObjectsOfTypeAll<MeshFilter>().Cast<Component>()
                .Concat(Resources.FindObjectsOfTypeAll<MeshCollider>())
                .Concat(Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>())
                .Concat(Resources.FindObjectsOfTypeAll<CanvasRenderer>())
                .Concat(Resources.FindObjectsOfTypeAll<MeshRenderer>())
                .Concat(Resources.FindObjectsOfTypeAll<ParticleSystemRenderer>())
                .Concat(Resources.FindObjectsOfTypeAll<ParticleSystem>()).ToArray();
            RequireNoOutsideConsumers(current);
        }
        private void RequireNoOutsideConsumers(Component[] components)
        {
            using (new LoadingEngineClockProbe.CheckpointValidation()) {
                RequireOriginalProducers();
                if (components.Length > 100000 || components.Any(component => component == null || !IsConsumer(component))
                    || components.Distinct().Count() != components.Length)
                    throw new InvalidOperationException("Persistent sprite current native consumer inventory differs.");
                var current = CanonicalPersistentSpriteDirectory.Resolve();
                if (!current.Keys.SequenceEqual(owners.Keys) || owners.Any(pair => !ReferenceEquals(pair.Value, current[pair.Key])))
                    throw new InvalidOperationException("Persistent sprite native owners changed.");
                var finalCurrent = CanonicalFinalVisualCarry.Directory();
                if (!finalCurrent.Keys.SequenceEqual(finalOwners.Keys) || finalOwners.Any(pair => !ReferenceEquals(pair.Value, finalCurrent[pair.Key])))
                    throw new InvalidOperationException("Final visual native owners changed.");
                var expected = owners.Values.Select(owner => owner.GetComponent<MeshFilter>()).ToArray();
                if (expected.Any(filter => !components.Contains(filter)))
                    throw new InvalidOperationException("Persistent sprite original native consumer is absent.");
                var meshes = expected.Select(filter => filter.sharedMesh).Concat(finalOwners.Values.Select(CanonicalFinalVisualCarry.Mesh)).ToArray();
                var meshCount = CanonicalPersistentSpriteDirectory.OwnerCount + finalOwners.Count;
                if (meshes.Any(mesh => mesh == null) || meshes.Distinct().Count() != meshCount)
                    throw new InvalidOperationException("Persistent sprite native mesh identity differs.");
                var data = meshes.Select(mesh => BitConverter.ToInt64(Native(mesh, 64), 56)).ToArray();
                if (data.Any(value => value == 0) || data.Distinct().Count() != meshCount)
                    throw new InvalidOperationException("Persistent sprite native shared mesh data is absent or shared.");
                if (data.Any(value => RefCount(value) != 1))
                    throw new InvalidOperationException("Persistent sprite native mesh data has an outside retained reference.");
                if (originalData != null && !originalData.SequenceEqual(data))
                    throw new InvalidOperationException("Persistent sprite shared data identity changed during its transaction.");
                originalData ??= data;
                var particleMeshSlots = 0;
                var expectedRenderers = owners.Values.Select(owner => owner.GetComponent<MeshRenderer>()).ToArray();
                foreach (var component in components) {
                    if (component == null) throw new InvalidOperationException("Persistent sprite native inventory changed.");
                    if (component is MeshFilter filter && meshes.Contains(filter.sharedMesh) && !expected.Contains(filter)
                        || component is MeshCollider collider && meshes.Contains(collider.sharedMesh)
                        || component is SkinnedMeshRenderer skinned && meshes.Contains(skinned.sharedMesh))
                        throw new InvalidOperationException("Persistent sprite mesh has an outside native consumer.");
                    if (component is CanvasRenderer canvas && data.Contains(BitConverter.ToInt64(Native(canvas, 80), 72)))
                        throw new InvalidOperationException("Persistent sprite mesh has an outside native CanvasRenderer consumer.");
                    if (component is MeshRenderer renderer && (meshes.Contains(renderer.additionalVertexStreams) || meshes.Contains(renderer.enlightenVertexStream)))
                        throw new InvalidOperationException("Persistent sprite mesh has an outside native renderer vertex stream consumer.");
                    if (component is ParticleSystemRenderer particle) {
                        var count = particle.meshCount;
                        if (count < 0 || count > 100000 - particleMeshSlots)
                            throw new InvalidOperationException("Persistent sprite particle mesh inventory exceeds its bound.");
                        particleMeshSlots += count;
                        var particleMeshes = new Mesh[count];
                        if (particle.GetMeshes(particleMeshes) != count || particle.meshCount != count)
                            throw new InvalidOperationException("Persistent sprite particle mesh inventory changed.");
                        if (meshes.Contains(particle.mesh) || particleMeshes.Any(mesh => meshes.Contains(mesh)))
                            throw new InvalidOperationException("Persistent sprite mesh has an outside native particle renderer consumer.");
                    }
                    if (component is ParticleSystem system) {
                        var shape = system.shape;
                        if (meshes.Contains(shape.mesh) || expectedRenderers.Contains(shape.meshRenderer)
                            || shape.skinnedMeshRenderer != null && meshes.Contains(shape.skinnedMeshRenderer.sharedMesh))
                            throw new InvalidOperationException("Persistent sprite mesh has an outside native particle shape consumer.");
                    }
                }
            }
        }
    }
}
