using System;
using System.IO;
using System.Linq;
using UnityEngine;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentNativeAssetState
    {
        // Mesh480 ownership after Object/NamedObject: semantic geometry is in
        // MeshState. Import/usage/streaming metadata, bone hashes and owner are
        // retained D here. ObjectUsers+368 belongs to Renderer.Consumers; its
        // whole-directory preflight is mandatory before common Canon. AssetUsers
        // and collision/async/compute resources remain explicitly unsupported.
        // InternalMeshID, allocator labels, array capacities and pointers are
        // local dependencies. Geometry/skin/GPU caches are the declared cold S0.
        internal sealed class MeshNativeState
        {
            public byte Compression, RetainedFlags;
            public bool KeepVertices, KeepIndices, PhysicsDirty, VertexBatchingCompatible;
            public uint RootBoneHash, UsageFlags, StreamSize;
            public ulong StreamOffset;
            public string OwnerBinding = "", StreamPath = "";
            public uint[] BonePathHashes = Array.Empty<uint>();
            public byte[] Metrics = new byte[8];
            public bool PhysicsResourcesEmpty, AsyncEmpty, ComputeBindingsEmpty, AssetUsersEmpty;
            public bool VerticesDirty, IndicesDirty, GpuBuffersEmpty, DerivedArraysEmpty, VertexFormatMatches;
            public int MaxBoneIndex;
            internal MeshNativeState Copy() {
                var result = (MeshNativeState)MemberwiseClone(); result.BonePathHashes = (uint[])BonePathHashes.Clone(); result.Metrics = (byte[])Metrics.Clone(); return result;
            }
        }
        private static MeshNativeState CaptureMeshNative(Mesh mesh, Func<object, string?> bind)
        {
            var pointer = MeshPointer(mesh); var bytes = ReadMeshOwner(mesh); var sharedPointer = Pointer(bytes, 56); var shared = Read(sharedPointer, 400);
            var hashCount = BitConverter.ToUInt64(bytes, 296); var hashCapacity = BitConverter.ToUInt64(bytes, 304) >> 1;
            if (hashCount > hashCapacity || hashCount > MaximumVertices || hashCapacity > MaximumVertices) throw Error("mesh bone-path directory exceeds bounds");
            var hashBytes = Read(Pointer(bytes, 280), checked((int)hashCount * 4));
            var ownerId = BitConverter.ToInt32(bytes, 400); var ownerBinding = "";
            if (ownerId != 0) {
                ownerBinding = ResidentNativeAssetReference.BindId(ownerId,typeof(NativeObject),bind);
            }
            var format = Pointer(bytes, 64); var assetHead = IntPtr.Add(pointer, 384);
            var result = new MeshNativeState { Compression = bytes[152], KeepVertices = bytes[154] != 0, KeepIndices = bytes[155] != 0,
                RetainedFlags = (byte)(bytes[156] & 0x1c), VerticesDirty = (bytes[156] & 1) != 0, IndicesDirty = (bytes[156] & 2) != 0,
                RootBoneHash = BitConverter.ToUInt32(bytes, 312), PhysicsDirty = bytes[352] != 0, UsageFlags = BitConverter.ToUInt32(bytes, 360),
                OwnerBinding = ownerBinding, StreamOffset = BitConverter.ToUInt64(bytes, 416), StreamSize = BitConverter.ToUInt32(bytes, 424),
                StreamPath = ResidentNativeMemory.String(IntPtr.Add(pointer, 432)), Metrics = bytes.Skip(472).Take(8).ToArray(),
                BonePathHashes = Enumerable.Range(0, (int)hashCount).Select(index => BitConverter.ToUInt32(hashBytes, index * 4)).ToArray(),
                PhysicsResourcesEmpty = new[] { 320, 328, 336, 344 }.All(offset => Pointer(bytes, offset) == IntPtr.Zero),
                AsyncEmpty = Pointer(bytes, 408) == IntPtr.Zero, ComputeBindingsEmpty = shared[136] == 0 && shared[137] == 0,
                VertexBatchingCompatible = shared[138] != 0, AssetUsersEmpty = Pointer(bytes, 384) == assetHead && Pointer(bytes, 392) == assetHead,
                GpuBuffersEmpty = Enumerable.Range(0, 10).All(index => Pointer(bytes, 72 + index * 8) == IntPtr.Zero),
                DerivedArraysEmpty = new[] { 184, 216, 248 }.All(offset => IsEmptyNativeArray(bytes, offset))
                    && new[] { 264, 296, 328 }.All(offset => IsEmptyNativeArray(shared, offset)),
                MaxBoneIndex = BitConverter.ToInt32(bytes, 316), VertexFormatMatches = format != IntPtr.Zero && Read(format, 56).SequenceEqual(shared.Skip(8).Take(56)) };
            if (bytes[153] > 1 || bytes[154] > 1 || bytes[155] > 1 || bytes[352] > 1 || shared[136] > 1 || shared[137] > 1 || shared[138] > 1
                || !bytes.SequenceEqual(ReadMeshOwner(mesh)) || !shared.SequenceEqual(Read(sharedPointer, 400))
                || !hashBytes.SequenceEqual(Read(Pointer(bytes, 280), hashBytes.Length))) throw Error("mesh native metadata changed or has invalid booleans");
            ValidateMeshNative(result); return result;
        }
        internal static void ValidateMeshNative(MeshNativeState value)
        {
            if (value == null || value.Compression > 3 || (value.RetainedFlags & ~0x1c) != 0 || value.OwnerBinding == null || value.OwnerBinding.Length > 32768
                || value.StreamPath == null || value.StreamPath.IndexOf('\0') >= 0 || new System.Text.UTF8Encoding(false, true).GetByteCount(value.StreamPath) > 16384
                || value.BonePathHashes == null || value.BonePathHashes.Length > MaximumVertices || value.Metrics == null || value.Metrics.Length != 8)
                throw Error("mesh native metadata schema differs");
        }
        private static string[] MeshCoverageGaps(MeshState state)
        {
            var gaps = new System.Collections.Generic.List<string>();
            if (!state.Readable || !state.BuffersCaptured) gaps.Add("mesh/cpu-geometry-unreadable");
            var value = state.Native;
            if (value == null) gaps.Add("mesh/native-metadata-not-captured");
            else {
                ValidateMeshNative(value);
                if (!value.PhysicsResourcesEmpty) gaps.Add("mesh/collision-resource-state");
                if (!value.AsyncEmpty) gaps.Add("mesh/async-upload-state");
                if (!value.ComputeBindingsEmpty) gaps.Add("mesh/external-compute-buffer-bindings");
                if (!value.AssetUsersEmpty) gaps.Add("mesh/asset-user-registry");

            }
            return gaps.ToArray();
        }
        internal static void RequireMeshCanonicalizable(MeshState state)
        {
            ValidateMesh(state); var gaps = MeshCoverageGaps(state);
            if (gaps.Length != 0) throw Error("Mesh cannot enter common Canon: " + string.Join(";", gaps));
        }
        private static void RequireMeshComplete(MeshState state, string name = "", string binding = "")
        {
            RequireMeshCanonicalizable(state);
            var value = state.Native!;
            if (!value.VerticesDirty || !value.IndicesDirty || !value.GpuBuffersEmpty || !value.DerivedArraysEmpty
                || value.MaxBoneIndex != -1 || !value.VertexFormatMatches)
            {
                var detail = $"name='{name}', binding='{binding}', VerticesDirty={value.VerticesDirty}, IndicesDirty={value.IndicesDirty}, GpuBuffersEmpty={value.GpuBuffersEmpty}, DerivedArraysEmpty={value.DerivedArraysEmpty}, MaxBoneIndex={value.MaxBoneIndex}, VertexFormatMatches={value.VertexFormatMatches}";
                ResidentFailureEvidence.Mark("Mesh cache normalization failed: " + detail);
                throw Error("Mesh cache normalization did not read back: " + detail);
            }
            var gaps = MeshCoverageGaps(state);
            if (gaps.Length != 0) throw Error("Mesh complete native readback is not in S0: " + string.Join(";", gaps));
        }
        internal static byte[] EncodeMeshNativeValues(MeshNativeState value)
        {
            ValidateMeshNative(value);
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream)) {
                writer.Write(value.Compression); writer.Write(value.KeepVertices); writer.Write(value.KeepIndices); writer.Write(value.RetainedFlags);
                writer.Write(value.PhysicsDirty); writer.Write(value.VertexBatchingCompatible); writer.Write(value.RootBoneHash); writer.Write(value.UsageFlags);
                writer.Write(value.OwnerBinding); writer.Write(value.StreamOffset); writer.Write(value.StreamSize); writer.Write(value.StreamPath); writer.Write(value.Metrics);
                writer.Write(value.BonePathHashes.Length); foreach (var hash in value.BonePathHashes) writer.Write(hash); writer.Flush(); return stream.ToArray();
            }
        }
        internal static void ApplyMeshNativeValues(MeshState source, Mesh owner, Func<string, NativeObject> resolve, Func<object, string?> bind, Action admitted)
        {
            RequireMeshCanonicalizable(source); admitted(); var data = source.Native!; var pointer = MeshPointer(owner); var bytes = ReadMeshOwner(owner);
            var before = CaptureMeshNative(owner, bind);
            if (!before.PhysicsResourcesEmpty || !before.AsyncEmpty || !before.ComputeBindingsEmpty || !before.AssetUsersEmpty)
                throw Error("mesh target metadata transfer requires closed physics, async, compute and asset-user domains");
            var dependency = ResidentNativeAssetReference.ResolveId(data.OwnerBinding,typeof(NativeObject),resolve);
            ResidentNativeMemory.Write(IntPtr.Add(pointer, 152), new[] { data.Compression, (byte)1, data.KeepVertices ? (byte)1 : (byte)0, data.KeepIndices ? (byte)1 : (byte)0 });
            ResidentNativeMemory.Write(IntPtr.Add(pointer, 156), new[] { (byte)((bytes[156] & ~0x1c) | data.RetainedFlags) });
            var hashes = IntPtr.Add(pointer, 280); ResidentNativeMemory.Write(IntPtr.Add(hashes, 16), BitConverter.GetBytes(0UL));
            ResidentNativeMemory.Reserve(hashes, data.BonePathHashes.Length, 4, 4, admitted);
            ResidentNativeMemory.Write(ResidentNativeMemory.Ptr(hashes), data.BonePathHashes.SelectMany(BitConverter.GetBytes).ToArray());
            ResidentNativeMemory.Write(IntPtr.Add(hashes, 16), BitConverter.GetBytes((ulong)data.BonePathHashes.Length));
            ResidentNativeMemory.Write(IntPtr.Add(pointer, 312), BitConverter.GetBytes(data.RootBoneHash));
            ResidentNativeMemory.Write(IntPtr.Add(pointer, 352), new[] { data.PhysicsDirty ? (byte)1 : (byte)0 });
            ResidentNativeMemory.Write(IntPtr.Add(pointer, 360), BitConverter.GetBytes(data.UsageFlags));
            ResidentNativeMemory.Write(IntPtr.Add(pointer, 400), BitConverter.GetBytes(dependency));
            ResidentNativeMemory.Write(IntPtr.Add(pointer, 416), BitConverter.GetBytes(data.StreamOffset)); ResidentNativeMemory.Write(IntPtr.Add(pointer, 424), BitConverter.GetBytes(data.StreamSize));
            ResidentNativeMemory.WriteString(IntPtr.Add(pointer, 432), data.StreamPath, admitted); ResidentNativeMemory.Write(IntPtr.Add(pointer, 472), data.Metrics);
            ResidentNativeMemory.Write(IntPtr.Add(ResidentNativeMemory.Ptr(IntPtr.Add(pointer, 56)), 138), new[] { data.VertexBatchingCompatible ? (byte)1 : (byte)0 });
            admitted();
            if (!EncodeMeshNativeValues(data).SequenceEqual(EncodeMeshNativeValues(CaptureMeshNative(owner, bind)))) throw Error("mesh native metadata independent readback differs");
        }
    }
}
