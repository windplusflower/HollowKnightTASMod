using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentNativeAssetState
    {
        // Common S0 ownership for Mesh:
        // D: actual geometry/layout, explicit bounds, bindposes, packed/fixed
        // skinning, blend-shape values, name/hideFlags and retained importer /
        // usage / streaming / owner metadata. The operation preserves these.
        // S0: newly allocated SharedMeshData; empty derived skin/AABB caches;
        // no owned geometry/blend/skin GPU buffers; both upload dirty bits set;
        // MaxBoneIndex=-1. Source and target use this same operation.
        // Dependency: current native vertex format and all registered / direct
        // consumers are rebound from the complete current directory. Execution
        // handles, native mesh ID, allocator labels and pointers stay local.
        // This stage does not authorize unknown physics/async/compute state or
        // qualify the entire asset adapter. The caller must reapply its unified
        // graph after native notifications and verify the complete transaction.
        internal static ColdMeshReceipt CanonColdMesh(Snapshot source, Mesh owner, ResidentMutationScope scope,
            Func<bool> admitted, Action<Mesh> rebindConsumers, Action<Mesh> verifyConsumers, Func<object, string?> bind)
        {
            if (source == null || source.Kind != "Mesh" || source.Mesh == null || source.Type.Resolve() != typeof(Mesh)
                || scope == null || admitted == null || rebindConsumers == null || verifyConsumers == null || bind == null)
                throw Error("cold Mesh Canon lacks its complete source, transaction or consumer dependency");
            RequireMeshCanonicalizable(source.Mesh); scope.Require();
            if (!admitted() || !ResidentObjectDirectory.IsLive(owner) || owner.GetType() != typeof(Mesh))
                throw Error("cold Mesh Canon is outside its admitted exact-type owner");
            var functions = new MeshCanonFunctions();
            var sourceGeometry = EncodeMesh(source.Mesh);
            if (owner.name != source.Name || (int)owner.hideFlags != source.HideFlags || !sourceGeometry.SequenceEqual(EncodeMesh(CaptureMesh(owner))))
                throw Error("cold Mesh Canon source changed before its transaction");
            var original = ReadMeshOwner(owner); RequireMeshCanBecomeCold(original);
            var preserved = CapturePreservedMeshState(original);
            var metadata = EncodeMeshNativeValues(source.Mesh.Native!);
            if (!metadata.SequenceEqual(EncodeMeshNativeValues(CaptureMeshNative(owner, bind)))) throw Error("cold Mesh retained metadata changed before Canon");
            Mesh? staging = null; var mutated = false;
            try {
                scope.Mutate(() => {
                    // A fresh engine-owned SharedMeshData removes historical
                    // empty layouts too. Clear(false) on the old Mesh cannot:
                    // its zero-vertex branch leaves old VertexData descriptors.
                    staging = new Mesh();
                    var transfer = new PreparedMeshBuffers(source.Name, source.HideFlags, source.Mesh, staging, admitted); transfer.Apply();
                    scope.Require();
                    var targetPointer = MeshPointer(owner); var stagingOwner = ReadMeshOwner(staging);
                    var oldShared = Pointer(original, 56); var replacement = Pointer(stagingOwner, 56);
                    if (replacement == IntPtr.Zero || replacement == oldShared || MeshPointer(owner) != targetPointer
                        || Pointer(ReadMeshOwner(owner), 56) != oldShared) throw Error("cold Mesh shared-data ownership changed before replacement");
                    // This is the player's real retain/release operation used
                    // by ApplyAndDisposeWritableMeshData; never copy a source
                    // process pointer or alter its reference count manually.
                    functions.SetNewVertexData(targetPointer, replacement);
                    functions.ClearSkinCache(targetPointer);
                    functions.FreeBuffer(IntPtr.Add(targetPointer, 112));
                    functions.FreeGeometry(IntPtr.Add(targetPointer, 72));
                    owner.bounds = source.Mesh.Bounds;
                    // Nonzero channels ensures the empty-mesh case is dirty as
                    // well. The real function interprets this parameter as a
                    // dirty mask; it does not invent a vertex attribute.
                    var channels = BitConverter.ToUInt32(Read(IntPtr.Add(replacement, 116), 4), 0);
                    functions.SetChannelsDirty(targetPointer, channels == 0 ? 1U : channels, 1, 0);
                    // Geometry is unchanged. Keep the two retained semantic
                    // flags instead of adopting the staging owner's defaults.
                    ResidentNativeMemory.Write(IntPtr.Add(targetPointer, 352), new[] { source.Mesh.Native!.PhysicsDirty ? (byte)1 : (byte)0 });
                    ResidentNativeMemory.Write(IntPtr.Add(replacement, 138), new[] { source.Mesh.Native.VertexBatchingCompatible ? (byte)1 : (byte)0 });
                    var manager = functions.GetFormatManager();
                    if (manager == IntPtr.Zero) throw Error("Mesh vertex format manager is not initialized");
                    var format = functions.GetFormat(manager, IntPtr.Add(replacement, 8));
                    if (format == IntPtr.Zero || !Read(format, 56).SequenceEqual(Read(IntPtr.Add(replacement, 8), 56)))
                        throw Error("Mesh vertex format manager returned another channel key");
                    ResidentNativeMemory.Write(IntPtr.Add(targetPointer, 64), BitConverter.GetBytes(format.ToInt64()));
                    rebindConsumers(owner);
                    scope.Require();
                    if (!admitted()) throw Error("cold Mesh Canon lost admission during consumer rebinding");
                });
                mutated = true;
            } finally {
                // The replacement Mesh retained its own share before this
                // local staging wrapper releases its initial share.
                if (mutated && ResidentObjectDirectory.IsLive(staging)) scope.Mutate(() => UnityEngine.Object.DestroyImmediate(staging));
            }
            var result = new ColdMeshReceipt(owner, source.Name, source.HideFlags, sourceGeometry, preserved, metadata, admitted, verifyConsumers, bind);
            scope.Mutate(result.Verify); return result;
        }

        internal sealed class ColdMeshReceipt
        {
            private readonly Mesh owner;
            private readonly string name;
            private readonly int hideFlags;
            private readonly byte[] geometry, preserved, metadata;
            private readonly Func<bool> admitted;
            private readonly Action<Mesh> verifyConsumers;
            private readonly Func<object, string?> bind;
            internal ColdMeshReceipt(Mesh owner, string name, int hideFlags, byte[] geometry, byte[] preserved, byte[] metadata, Func<bool> admitted, Action<Mesh> verifyConsumers, Func<object, string?> bind)
            { this.owner = owner; this.name = name; this.hideFlags = hideFlags; this.geometry = geometry; this.preserved = preserved; this.metadata = metadata; this.admitted = admitted; this.verifyConsumers = verifyConsumers; this.bind = bind; }
            internal void Verify()
            {
                if (!admitted() || !ResidentObjectDirectory.IsLive(owner)) throw Error("cold Mesh readback lost its admitted native owner");
                var native = ReadMeshOwner(owner); RequireMeshCanBecomeCold(native);
                if (owner.name != name || (int)owner.hideFlags != hideFlags) throw Error("cold Mesh Canon changed identity metadata");
                if ((native[156] & 3) != 3 || BitConverter.ToInt32(native, 316) != -1)
                    throw Error("cold Mesh did not retain both dirty bits and an invalidated derived bone index");
                for (var offset = 72; offset < 152; offset += 8)
                    if (Pointer(native, offset) != IntPtr.Zero) throw Error("cold Mesh retained an owned native GPU buffer");
                foreach (var offset in new[] { 184, 216, 248 }) RequireEmptyNativeArray(native, offset);
                var shared = Read(Pointer(native, 56), 400);
                foreach (var offset in new[] { 264, 296, 328 }) RequireEmptyNativeArray(shared, offset);
                var format = Pointer(native, 64);
                if (format == IntPtr.Zero || !Read(format, 56).SequenceEqual(shared.Skip(8).Take(56)))
                    throw Error("cold Mesh current-channel native format binding differs");
                if (!preserved.SequenceEqual(CapturePreservedMeshState(native))) throw Error("cold Mesh Canon changed retained metadata/import/streaming state");
                if (!geometry.SequenceEqual(EncodeMesh(CaptureMesh(owner)))) throw Error("cold Mesh Canon changed source geometry or skinning");
                if (!metadata.SequenceEqual(EncodeMeshNativeValues(CaptureMeshNative(owner, bind)))) throw Error("cold Mesh Canon changed retained portable metadata");
                verifyConsumers(owner);
            }
        }
        private static byte[] ReadMeshOwner(Mesh mesh)
        {
            var native = Read(MeshPointer(mesh), 480);
            if (BitConverter.ToInt32(native, 8) != mesh.GetInstanceID() || Pointer(native, 56) == IntPtr.Zero)
                throw Error("native Mesh owner identity or SharedMeshData is absent");
            return native;
        }
        private static IntPtr MeshPointer(Mesh mesh)
        {
            LoadingEngineClockProbe.RequirePinnedEngine();
            if (!ResidentObjectDirectory.IsLive(mesh) || mesh.GetType() != typeof(Mesh)) throw Error("Mesh native access has another type or a dead wrapper");
            return (IntPtr)CachedPointer.GetValue(mesh)!;
        }
        private static void RequireMeshCanBecomeCold(byte[] owner)
        {
            if (owner.Length != 480 || owner[153] != 1 || owner[154] > 1 || owner[155] > 1 || Pointer(owner, 408) != IntPtr.Zero)
                throw Error("Mesh requires unreadable/async resource handling before common cold Canon");
            for (var offset = 320; offset < 352; offset += 8)
                if (Pointer(owner, offset) != IntPtr.Zero) throw Error("Mesh collision resource needs the native physics adapter before cold Canon");
            var vertex = Read(IntPtr.Add(Pointer(owner, 56), 8), 152);
            if (vertex[128] != 0 || vertex[129] != 0) throw Error("Mesh has external compute-buffer bindings before cold Canon");
        }
        private static byte[] CapturePreservedMeshState(byte[] owner)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream)) {
                // Only compared within one live owner epoch; pointer-valued
                // import/streaming fields are receipts, never cross-process D.
                writer.Write(owner, 152, 4); writer.Write((byte)(owner[156] & 0x1c));
                writer.Write(owner, 280, 36); writer.Write(owner, 360, 8); writer.Write(owner, 400, 8);
                writer.Write(owner, 416, 64);
                var count = BitConverter.ToUInt64(owner, 296); var capacity = BitConverter.ToUInt64(owner, 304) >> 1;
                if (count > capacity || count > MaximumBytes / 4) throw Error("Mesh bone-path hash array exceeds its native bound");
                WriteBytes(writer, Read(Pointer(owner, 280), checked((int)count * 4)));
                return stream.ToArray();
            }
        }
        private static void RequireEmptyNativeArray(byte[] owner, int offset)
        {
            if (Pointer(owner, offset) != IntPtr.Zero || BitConverter.ToUInt64(owner, offset + 16) != 0 || BitConverter.ToUInt64(owner, offset + 24) != 1)
                throw Error("derived Mesh cache was not reset to the native empty allocation state");
        }
        private static IntPtr Pointer(byte[] bytes, int offset) => new IntPtr(BitConverter.ToInt64(bytes, offset));

        private sealed class MeshCanonFunctions
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Unary(IntPtr owner);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Replace(IntPtr owner, IntPtr replacement);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Dirty(IntPtr owner, uint channels, byte indices, uint flags);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr Manager();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr Format(IntPtr manager, IntPtr channels);
            internal readonly Unary FreeGeometry, FreeBuffer, ClearSkinCache;
            internal readonly Replace SetNewVertexData;
            internal readonly Dirty SetChannelsDirty;
            internal readonly Manager GetFormatManager;
            internal readonly Format GetFormat;
            internal MeshCanonFunctions()
            {
                var module = LoadingEngineClockProbe.RequirePinnedEngine();
                FreeGeometry = Bind<Unary>(module.BaseAddress, 0x6f0770, 189, "75a6e350be004d2b45ca39bade1f578d6be31b801be7f350478952e88bd773d2");
                FreeBuffer = Bind<Unary>(module.BaseAddress, 0x6f0330, 90, "1da8978902db81f6c7a518bbe06e1e7b89e21fb00518e6c0c134b2cf38db7e2d");
                ClearSkinCache = Bind<Unary>(module.BaseAddress, 0xea9540, 275, "9d1c26c6a7c8835a7b0b4d6787b76434e539380000b192fc58065c38de15cd3b");
                SetNewVertexData = Bind<Replace>(module.BaseAddress, 0xeafd70, 107, "d996b284c3dfa908bb95c7a889bd59007c20c22ab89f32d188e005693cb35142");
                SetChannelsDirty = Bind<Dirty>(module.BaseAddress, 0xeaecf0, 268, "8b7b7e79e8fbec6e68bdef30ce8806c8932387ab4470f18bd0020bdb93bb5503");
                GetFormatManager = Bind<Manager>(module.BaseAddress, 0x6d8d00, 8, "2c8c20e5213595bfec2fdf353bb71538eebbab96e42cc449da4e616e5af68dc0");
                GetFormat = Bind<Format>(module.BaseAddress, 0x6d8a00, 759, "f4915232111e7b1bce3c32d7cd26f35c81abc3de9146ee47f0e9bab4e10c8a35");
            }
            private static T Bind<T>(IntPtr module, int rva, int extent, string expected) where T : Delegate
            {
                var address = IntPtr.Add(module, rva); var bytes = Read(address, extent);
                using (var sha = SHA256.Create())
                    if (BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() != expected)
                        throw Error("pinned Mesh native entry differs: " + rva.ToString("x"));
                return (T)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
            }
        }
    }
}
