using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Exact-type asset state. A buffer transfer is deliberately separate from
    // complete native coverage: public geometry and equal resource provenance
    // alone do not establish native cache, GPU or resource-registration parity.
    internal static partial class ResidentNativeAssetState
    {
        internal const int MaximumBytes = 32 * 1024 * 1024;
        internal const int MaximumVertices = 1048576, MaximumSubMeshes = 4096, MaximumBlendShapes = 1024;
        private static readonly FieldInfo CachedPointer = typeof(NativeObject).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)!;
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(
            IntPtr process, IntPtr address, [Out] byte[] buffer, UIntPtr size, out UIntPtr read);

        internal sealed class Snapshot
        {
            public string Binding = "", Name = "", Kind = "";
            public ResidentObjectDirectory.TypeRecord Type = new ResidentObjectDirectory.TypeRecord();
            public int HideFlags;
            public MeshState? Mesh;
            public MaterialState? Material;
            public TextureState? Texture;
            public PhysicsMaterialState? PhysicsMaterial;
            public TextAssetState? Text;
            public ResidentMonoNativeState.Data? Scriptable;
            public MonoScriptState? Script;
            public ShaderState? Shader;
            public string[] CoverageGaps = Array.Empty<string>();
            internal Snapshot Copy() => new Snapshot { Binding = Binding, Name = Name, Kind = Kind, Type = Type.Copy(), HideFlags = HideFlags,
                Mesh = Mesh?.Copy(), Material = Material?.Copy(), Texture = Texture?.Copy(), PhysicsMaterial = PhysicsMaterial?.Copy(), Text = Text?.Copy(),
                Scriptable = Scriptable == null ? null : ResidentMonoNativeState.Copy(Scriptable), Script = Script?.Copy(), Shader = Shader?.Copy(), CoverageGaps = (string[])CoverageGaps.Clone() };
            internal void RequireCompleteCoverage()
            {
                var required = RequiredGaps(Kind);
                if (Kind == "Mesh") RequireMeshComplete(Mesh ?? throw Error("Mesh native state is absent"));
                if (Kind == "Material") RequireMaterialComplete(Material ?? throw Error("Material native state is absent"));
                if (Kind == "ScriptableObject") ResidentMonoNativeState.RequireComplete(Scriptable ?? throw Error("ScriptableObject native state is absent"));
                if (Kind == "MonoScript") RequireMonoScriptComplete(Script ?? throw Error("MonoScript native state is absent"));
                if (Kind == "Shader") RequireShaderComplete(Shader ?? throw Error("Shader native state is absent"));
                if (required.Length != 0 || CoverageGaps.Length != 0)
                    throw Error("native asset coverage is incomplete: " + Binding + ": " + string.Join(",", required.Concat(CoverageGaps).Distinct()));
            }
            internal void RequireCanonicalizableCoverage()
            {
                if (Kind == "Mesh") {
                    RequireMeshCanonicalizable(Mesh ?? throw Error("Mesh native state is absent"));
                    if (CoverageGaps.Any(value => value != "mesh/common-cold-cache-state"))
                        throw Error("Mesh has another noncanonicalizable coverage gap: " + string.Join(";", CoverageGaps));
                    return;
                }
                if (Kind == "Material") {
                    RequireMaterialCanonicalizable(Material ?? throw Error("Material native state is absent"));
                    if (CoverageGaps.Any(value => value != "material/common-cold-cache-state"))
                        throw Error("Material has another noncanonicalizable coverage gap: " + string.Join(";", CoverageGaps));
                    return;
                }
                // Texture GPU/streaming remains real missing state, not merely
                // cache markers. Its lower transfer helper cannot grant full
                // canonicalizable coverage yet.
                RequireCompleteCoverage();
            }
        }
        internal sealed class MeshState
        {
            public bool Readable, BuffersCaptured, FixedSkinningInVertexChannels;
            public int VertexCount, DeclaredSubMeshCount, DeclaredBlendShapeCount;
            public IndexFormat IndexFormat;
            public Bounds Bounds;
            public VertexAttributeDescriptor[] Attributes = Array.Empty<VertexAttributeDescriptor>();
            public byte[][] VertexBuffers = Array.Empty<byte[]>();
            public byte[] IndexBuffer = Array.Empty<byte>();
            public SubMeshDescriptor[] SubMeshes = Array.Empty<SubMeshDescriptor>();
            public Matrix4x4[] BindPoses = Array.Empty<Matrix4x4>();
            public byte[] BonesPerVertex = Array.Empty<byte>();
            public BoneWeight1[] BoneWeights = Array.Empty<BoneWeight1>();
            public uint[] PackedVariableWeights = Array.Empty<uint>();
            public BlendShape[] BlendShapes = Array.Empty<BlendShape>();
            public MeshNativeState? Native;
            internal MeshState Copy() => new MeshState {
                Readable = Readable, BuffersCaptured = BuffersCaptured, FixedSkinningInVertexChannels = FixedSkinningInVertexChannels, VertexCount = VertexCount, IndexFormat = IndexFormat, Bounds = Bounds,
                DeclaredSubMeshCount = DeclaredSubMeshCount, DeclaredBlendShapeCount = DeclaredBlendShapeCount,
                Attributes = (VertexAttributeDescriptor[])Attributes.Clone(), VertexBuffers = VertexBuffers.Select(value => (byte[])value.Clone()).ToArray(),
                IndexBuffer = (byte[])IndexBuffer.Clone(), SubMeshes = (SubMeshDescriptor[])SubMeshes.Clone(), BindPoses = (Matrix4x4[])BindPoses.Clone(),
                BonesPerVertex = (byte[])BonesPerVertex.Clone(), BoneWeights = (BoneWeight1[])BoneWeights.Clone(), PackedVariableWeights = (uint[])PackedVariableWeights.Clone(),
                BlendShapes = BlendShapes.Select(value => value.Copy()).ToArray(), Native = Native?.Copy()
            };
        }
        internal sealed class BlendShape
        {
            public string Name = "";
            public BlendFrame[] Frames = Array.Empty<BlendFrame>();
            internal BlendShape Copy() => new BlendShape { Name = Name, Frames = Frames.Select(value => value.Copy()).ToArray() };
        }
        internal sealed class BlendFrame
        {
            public float Weight;
            public Vector3[] Vertices = Array.Empty<Vector3>(), Normals = Array.Empty<Vector3>(), Tangents = Array.Empty<Vector3>();
            internal BlendFrame Copy() => new BlendFrame { Weight = Weight, Vertices = (Vector3[])Vertices.Clone(),
                Normals = (Vector3[])Normals.Clone(), Tangents = (Vector3[])Tangents.Clone() };
        }
        internal sealed class MaterialState
        {
            public string ShaderBinding = "", RuntimeShaderBinding = "", OwnerBinding = "", SerializedKeywords = "";
            public int RenderQueue, GlobalIlluminationFlags;
            public bool EnableInstancing, DoubleSidedGI;
            public string[] Keywords = Array.Empty<string>(), DisabledPasses = Array.Empty<string>();
            public ResidentShaderPropertySheet.Data? RuntimeProperties;
            public SavedMaterialProperty[] SavedProperties = Array.Empty<SavedMaterialProperty>();
            public MaterialTag[] OverrideTags = Array.Empty<MaterialTag>();
            public uint RuntimeFlags;
            public bool HasRuntimeSheet, TextureDirty, ConstantBufferDirty, RuntimeTextureDirty, RuntimeConstantBufferDirty, RuntimeBufferOverride, RuntimeStacksDirty;
            public MaterialCacheState? Caches;
            internal MaterialState Copy() => new MaterialState { ShaderBinding = ShaderBinding, RuntimeShaderBinding = RuntimeShaderBinding, OwnerBinding = OwnerBinding,
                SerializedKeywords = SerializedKeywords, RenderQueue = RenderQueue, GlobalIlluminationFlags = GlobalIlluminationFlags,
                EnableInstancing = EnableInstancing, DoubleSidedGI = DoubleSidedGI, Keywords = (string[])Keywords.Clone(), DisabledPasses = (string[])DisabledPasses.Clone(),
                RuntimeProperties = RuntimeProperties == null ? null : ResidentShaderPropertySheet.Copy(RuntimeProperties), SavedProperties = SavedProperties.Select(value => value.Copy()).ToArray(),
                OverrideTags = OverrideTags.Select(value => value.Copy()).ToArray(),
                RuntimeFlags = RuntimeFlags, HasRuntimeSheet = HasRuntimeSheet, TextureDirty = TextureDirty, ConstantBufferDirty = ConstantBufferDirty,
                RuntimeTextureDirty = RuntimeTextureDirty, RuntimeConstantBufferDirty = RuntimeConstantBufferDirty, RuntimeBufferOverride = RuntimeBufferOverride, RuntimeStacksDirty = RuntimeStacksDirty,
                Caches = Caches?.Copy() };
        }
        internal sealed class MaterialCacheState
        {
            public int SharedReferences, PropertySheetReferences;
            public bool BuildStacksEmpty, RuntimeStacksEmpty, MainTextureUncached, MainColorUncached, HashesEmpty, TextureUnionEmpty,
                BufferAbsent, DisplayListsEmpty, SmallIndexMatchesOwner;
            internal MaterialCacheState Copy() => (MaterialCacheState)MemberwiseClone();
        }
        internal sealed class MaterialTag
        {
            public string Name = "", Value = "";
            internal MaterialTag Copy() => new MaterialTag { Name = Name, Value = Value };
        }
        internal sealed class SavedMaterialProperty
        {
            public string Name = "", TextureBinding = "";
            public int Kind;
            public byte[] Value = Array.Empty<byte>();
            internal SavedMaterialProperty Copy() => new SavedMaterialProperty { Name = Name, TextureBinding = TextureBinding, Kind = Kind, Value = (byte[])Value.Clone() };
        }
        internal sealed class TextureState
        {
            public int Width, Height, Dimension, Format, GraphicsFormat, MipmapCount, FilterMode, AnisoLevel, WrapU, WrapV, WrapW;
            public int StreamingPriority, RequestedMip, MinimumMip, CalculatedMip, DesiredMip, LoadingMip, LoadedMip;
            public float MipMapBias;
            public uint UpdateCount;
            public bool Readable, Streaming, VirtualTextureOnly, CpuDataPresent;
            public int CpuWidth, CpuHeight, CpuFormat, CpuImageCount, CpuMipCount;
            public ulong CpuImageSize;
            public ulong CpuCapacity;
            public byte[] CpuBytes = Array.Empty<byte>();
            public TextureNativeState? Native;
            internal TextureState Copy() { var result = (TextureState)MemberwiseClone(); result.CpuBytes = (byte[])CpuBytes.Clone(); result.Native = Native?.Copy(); return result; }
        }

        internal static Snapshot Capture(string binding, NativeObject asset, Func<object, string?> bindReference)
        {
            if (string.IsNullOrEmpty(binding) || !ResidentObjectDirectory.IsLive(asset) || bindReference == null)
                throw Error("asset capture lacks a live identity or reference binder");
            var actual = asset.GetType();
            var result = new Snapshot { Binding = binding, Name = asset.name, HideFlags = (int)asset.hideFlags,
                Type = ResidentObjectDirectory.TypeRecord.Capture(actual) };
            var gaps = new List<string>();
            if (IsNativeMonoScript(asset)) {
                result.Kind = "MonoScript"; result.Script = CaptureMonoScript(asset); gaps.AddRange(MonoScriptGaps(result.Script));
            } else if (actual == typeof(Mesh)) {
                result.Kind = "Mesh"; result.Mesh = CaptureMesh((Mesh)asset);
                result.Mesh.Native = CaptureMeshNative((Mesh)asset, bindReference); gaps.AddRange(MeshCoverageGaps(result.Mesh, false));
                if (!result.Mesh.Readable) gaps.Add("mesh-cpu-buffers-unreadable");
            } else if (actual == typeof(Material)) {
                result.Kind = "Material"; var material = (Material)asset; var state = new MaterialState();
                CaptureMaterialNative(material, state, bindReference);
                if (state.RuntimeProperties != null) gaps.AddRange(state.RuntimeProperties.CoverageGaps);
                gaps.AddRange(MaterialCoverageGaps(state, false));
                result.Material = state;
            } else if (actual == typeof(Texture2D)) {
                result.Kind = "Texture2D"; result.Texture = CaptureTexture((Texture2D)asset);
                if (!result.Texture.CpuDataPresent) gaps.Add("texture-cpu-image-absent");
                gaps.AddRange(TextureNativeCoverageGaps(result.Texture));
            } else if (actual == typeof(Shader)) {
                result.Kind = "Shader"; result.Shader = CaptureShader((Shader)asset, bindReference); gaps.AddRange(ShaderCoverageGaps(result.Shader));
            } else if (actual == typeof(PhysicsMaterial2D)) {
                result.Kind = "PhysicsMaterial2D"; result.PhysicsMaterial = CapturePhysicsMaterial((PhysicsMaterial2D)asset, bindReference);
            } else if (actual == typeof(TextAsset)) {
                result.Kind = "TextAsset"; result.Text = CaptureTextAsset((TextAsset)asset);
            } else if (asset is ScriptableObject) {
                result.Kind = "ScriptableObject"; result.Scriptable = ResidentMonoNativeState.Capture(asset, bindReference); gaps.AddRange(result.Scriptable.CoverageGaps);
            } else { result.Kind = "Unsupported"; gaps.Add("native-asset-type:" + actual.FullName); }
            result.CoverageGaps = RequiredGaps(result.Kind).Concat(gaps).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            return result;
        }
        private static string[] RequiredGaps(string kind)
        {
            switch (kind) {
                case "PhysicsMaterial2D": return Array.Empty<string>();
                case "TextAsset": return Array.Empty<string>();
                case "ScriptableObject": return Array.Empty<string>();
                case "MonoScript": return Array.Empty<string>();
                case "Mesh": return Array.Empty<string>();
                case "Material": return Array.Empty<string>();
                case "Shader": return Array.Empty<string>();
                case "Texture2D": return new[] { "texture-cpu-image-layout-and-allocation-restore", "texture-gpu-image-and-upload-state",
                    "texture-native-streaming-resource-registration" };
                default: return new[] { "native-asset-type-unsupported" };
            }
        }

        private static MeshState CaptureMesh(Mesh mesh)
        {
            var state = new MeshState { Readable = mesh.isReadable, VertexCount = mesh.vertexCount, IndexFormat = mesh.indexFormat,
                Bounds = mesh.bounds, DeclaredSubMeshCount = mesh.subMeshCount, DeclaredBlendShapeCount = mesh.blendShapeCount };
            if (state.VertexCount < 0 || state.VertexCount > MaximumVertices || state.DeclaredSubMeshCount < 0 || state.DeclaredSubMeshCount > MaximumSubMeshes
                || state.DeclaredBlendShapeCount < 0 || state.DeclaredBlendShapeCount > MaximumBlendShapes) throw Error("mesh dimensions exceed their bound");
            if (!state.Readable) return state;
            state.Attributes = mesh.GetVertexAttributes(); state.BindPoses = mesh.bindposes;
            // These public getters are NOT read-only in the pinned player:
            // GetBonesPerVertex/GetAllBoneWeights call ExtractBoneWeights when
            // Mesh's derived arrays are empty. Decode the existing packed
            // SharedMeshData instead, without creating either source cache.
            state.PackedVariableWeights = ReadVariableSkinning(mesh);
            DecodeVariableSkinning(state.VertexCount, state.PackedVariableWeights, out state.BonesPerVertex, out state.BoneWeights);
            state.FixedSkinningInVertexChannels = state.PackedVariableWeights.Length == 0 && state.Attributes.Any(value => value.attribute == VertexAttribute.BlendIndices);
            long bytes = checked((long)state.BindPoses.Length * 64 + state.BonesPerVertex.Length + (long)state.BoneWeights.Length * 8 + (long)state.PackedVariableWeights.Length * 4);
            RequireBytes(bytes);
            using (var data = Mesh.AcquireReadOnlyMeshData(mesh)) {
                if (data.Length != 1) throw Error("mesh read-only data did not contain one owner");
                var native = data[0];
                if (native.vertexCount != state.VertexCount || native.subMeshCount != state.DeclaredSubMeshCount || native.indexFormat != state.IndexFormat
                    || native.vertexBufferCount < 0 || native.vertexBufferCount > 4) throw Error("mesh read-only directory differs");
                state.VertexBuffers = new byte[native.vertexBufferCount][];
                for (var index = 0; index < state.VertexBuffers.Length; index++) {
                    var buffer = native.GetVertexData<byte>(index); RequireBytes(bytes += buffer.Length);
                    state.VertexBuffers[index] = buffer.ToArray();
                }
                var indices = native.GetIndexData<byte>(); RequireBytes(bytes += indices.Length); state.IndexBuffer = indices.ToArray();
                state.SubMeshes = Enumerable.Range(0, state.DeclaredSubMeshCount).Select(native.GetSubMesh).ToArray();
            }
            state.BlendShapes = new BlendShape[state.DeclaredBlendShapeCount];
            for (var shape = 0; shape < state.BlendShapes.Length; shape++) {
                var count = mesh.GetBlendShapeFrameCount(shape);
                if (count < 1 || count > 4096) throw Error("mesh blend-shape frame count exceeds its bound");
                RequireBytes(bytes += checked((long)count * state.VertexCount * 36));
                var row = new BlendShape { Name = mesh.GetBlendShapeName(shape), Frames = new BlendFrame[count] };
                for (var frame = 0; frame < count; frame++) {
                    var value = new BlendFrame { Weight = mesh.GetBlendShapeFrameWeight(shape, frame), Vertices = new Vector3[state.VertexCount],
                        Normals = new Vector3[state.VertexCount], Tangents = new Vector3[state.VertexCount] };
                    mesh.GetBlendShapeFrameVertices(shape, frame, value.Vertices, value.Normals, value.Tangents); row.Frames[frame] = value;
                }
                state.BlendShapes[shape] = row;
            }
            state.BuffersCaptured = true; ValidateMesh(state); return state;
        }

        // This lower-level transfer is a named part of a larger native adapter.
        // Calling it does not clear Snapshot.CoverageGaps or grant qualification.
        internal static PreparedMeshBuffers PrepareMeshBuffers(Snapshot source, Mesh target, Func<bool> admitted)
        {
            if (source == null || source.Kind != "Mesh" || source.Mesh == null || source.Type.Resolve() != typeof(Mesh))
                throw Error("mesh transfer received another native asset type");
            return new PreparedMeshBuffers(source.Name, source.HideFlags, source.Mesh, target, admitted);
        }
        internal sealed class PreparedMeshBuffers
        {
            private readonly string name;
            private readonly int hideFlags;
            private readonly MeshState source;
            private readonly Mesh target;
            private readonly Func<bool> admitted;
            private bool attempted, applied;
            internal PreparedMeshBuffers(string name, int hideFlags, MeshState source, Mesh target, Func<bool> admitted)
            {
                ValidateMesh(source);
                this.name = name ?? throw new ArgumentNullException(nameof(name)); this.hideFlags = hideFlags;
                this.source = source.Copy(); this.target = target; this.admitted = admitted ?? throw new ArgumentNullException(nameof(admitted));
                Require();
                if (!target.isReadable) throw Error("mesh buffer target is not CPU-readable");
            }
            internal void Apply()
            {
                Require(); if (attempted) throw Error("mesh buffer application is single use"); attempted = true;
                const MeshUpdateFlags flags = MeshUpdateFlags.DontRecalculateBounds;
                target.Clear(false); target.ClearBlendShapes(); target.indexFormat = source.IndexFormat;
                if (source.Attributes.Length != 0) target.SetVertexBufferParams(source.VertexCount, source.Attributes);
                target.SetIndexBufferParams(source.IndexBuffer.Length / IndexSize(source.IndexFormat), source.IndexFormat);
                target.subMeshCount = source.SubMeshes.Length; target.bindposes = source.BindPoses;
                if (source.PackedVariableWeights.Length != 0) {
                    using (var counts = new NativeArray<byte>(source.BonesPerVertex, Allocator.Temp))
                    using (var weights = new NativeArray<BoneWeight1>(source.BoneWeights, Allocator.Temp)) target.SetBoneWeights(counts, weights);
                }
                foreach (var shape in source.BlendShapes)
                    foreach (var frame in shape.Frames) target.AddBlendShapeFrame(shape.Name, frame.Weight, frame.Vertices, frame.Normals, frame.Tangents);
                Require();
                for (var stream = 0; stream < source.VertexBuffers.Length; stream++)
                    if (source.VertexBuffers[stream].Length != 0) target.SetVertexBufferData(source.VertexBuffers[stream], 0, 0, source.VertexBuffers[stream].Length, stream, flags);
                if (source.IndexBuffer.Length != 0) target.SetIndexBufferData(source.IndexBuffer, 0, 0, source.IndexBuffer.Length, flags);
                for (var index = 0; index < source.SubMeshes.Length; index++) target.SetSubMesh(index, source.SubMeshes[index], flags);
                target.bounds = source.Bounds; target.name = name; target.hideFlags = (HideFlags)hideFlags;
                applied = true; Verify();
            }
            internal void Verify()
            {
                Require(); if (!applied) throw Error("mesh buffer verification precedes application");
                if (target.name != name || (int)target.hideFlags != hideFlags) throw Error("mesh metadata did not read back");
                var observed = CaptureMesh(target);
                if (!EncodeMesh(source).SequenceEqual(EncodeMesh(observed))) throw Error("mesh buffer/geometry independent readback differs");
            }
            private void Require()
            {
                if (!admitted() || !ResidentObjectDirectory.IsLive(target) || target.GetType() != typeof(Mesh))
                    throw Error("mesh buffer transfer lost its admitted live owner");
            }
        }

        internal static void ValidateMesh(MeshState state)
        {
            if (state == null || !state.Readable || !state.BuffersCaptured || state.VertexCount < 0 || state.VertexCount > MaximumVertices
                || state.Attributes == null || state.VertexBuffers == null || state.IndexBuffer == null || state.SubMeshes == null || state.BindPoses == null
                || state.BonesPerVertex == null || state.BoneWeights == null || state.PackedVariableWeights == null || state.BlendShapes == null || state.Attributes.Length > 16
                || state.VertexBuffers.Length > 4 || state.SubMeshes.Length > MaximumSubMeshes || state.BlendShapes.Length > MaximumBlendShapes
                || state.SubMeshes.Length != state.DeclaredSubMeshCount || state.BlendShapes.Length != state.DeclaredBlendShapeCount)
                throw Error("mesh buffer manifest is absent or exceeds its bound");
            var stride = new int[state.VertexBuffers.Length]; var seen = new HashSet<VertexAttribute>();
            foreach (var attribute in state.Attributes) {
                if (!Enum.IsDefined(typeof(VertexAttribute), attribute.attribute) || !seen.Add(attribute.attribute)
                    || attribute.dimension < 1 || attribute.dimension > 4 || attribute.stream < 0 || attribute.stream >= stride.Length)
                    throw Error("mesh vertex attribute identity or shape differs");
                var size = checked(FormatSize(attribute.format) * attribute.dimension);
                if (size % 4 != 0) throw Error("mesh vertex attribute violates the native four-byte alignment");
                stride[attribute.stream] += size;
            }
            if (state.Attributes.Length == 0 ? state.VertexCount != 0 || state.VertexBuffers.Length != 0
                : state.VertexBuffers.Length != state.Attributes.Max(value => value.stream) + 1) throw Error("mesh vertex stream directory is incomplete");
            long bytes = state.IndexBuffer.Length + (long)state.BindPoses.Length * 64 + state.BonesPerVertex.Length + (long)state.BoneWeights.Length * 8 + (long)state.PackedVariableWeights.Length * 4;
            for (var stream = 0; stream < stride.Length; stream++) {
                var buffer = state.VertexBuffers[stream];
                if (stride[stream] == 0 || buffer == null || (long)stride[stream] * state.VertexCount != buffer.Length)
                    throw Error("mesh raw stream byte count differs from its descriptors");
                bytes += buffer.Length;
            }
            var indexSize = IndexSize(state.IndexFormat);
            if (state.IndexBuffer.Length % indexSize != 0) throw Error("mesh index buffer is misaligned");
            var indexCount = state.IndexBuffer.Length / indexSize;
            foreach (var sub in state.SubMeshes) {
                if (!Enum.IsDefined(typeof(MeshTopology), sub.topology) || sub.indexStart < 0 || sub.indexCount < 0 || sub.indexStart > indexCount
                    || sub.indexCount > indexCount - sub.indexStart || sub.firstVertex < 0 || sub.vertexCount < 0 || sub.firstVertex > state.VertexCount
                    || sub.vertexCount > state.VertexCount - sub.firstVertex || sub.topology == MeshTopology.Triangles && sub.indexCount % 3 != 0
                    || sub.topology == MeshTopology.Quads && sub.indexCount % 4 != 0 || sub.topology == MeshTopology.Lines && sub.indexCount % 2 != 0)
                    throw Error("mesh submesh descriptor has an invalid range or primitive count");
                for (var index = sub.indexStart; index < sub.indexStart + sub.indexCount; index++) {
                    var offset = index * indexSize;
                    var vertex = (indexSize == 2 ? (long)BitConverter.ToUInt16(state.IndexBuffer, offset) : BitConverter.ToUInt32(state.IndexBuffer, offset)) + sub.baseVertex;
                    if (vertex < 0 || vertex >= state.VertexCount || vertex < sub.firstVertex || vertex >= (long)sub.firstVertex + sub.vertexCount)
                        throw Error("mesh index plus baseVertex escapes its captured vertex range");
                }
            }
            if (state.BonesPerVertex.Length != 0 && state.BonesPerVertex.Length != state.VertexCount
                || state.BonesPerVertex.Sum(value => (long)value) != state.BoneWeights.Length)
                throw Error("mesh variable bone influences do not match their per-vertex counts");
            if (state.PackedVariableWeights.Length != 0) {
                DecodeVariableSkinning(state.VertexCount, state.PackedVariableWeights, out var counts, out var weights);
                if (!counts.SequenceEqual(state.BonesPerVertex) || !Raw(weights, 8).SequenceEqual(Raw(state.BoneWeights, 8)))
                    throw Error("mesh decoded variable skinning differs from its packed source words");
            } else if (state.BonesPerVertex.Length != 0 || state.BoneWeights.Length != 0) throw Error("mesh variable skinning lacks its packed source words");
            foreach (var bone in state.BoneWeights)
                if (bone.boneIndex < 0 || bone.boneIndex >= state.BindPoses.Length || float.IsNaN(bone.weight) || float.IsInfinity(bone.weight) || bone.weight < 0)
                    throw Error("mesh bone influence has an invalid bind pose or weight");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var shape in state.BlendShapes) {
                if (shape == null || shape.Name == null || shape.Name.Length > 262144 || !names.Add(shape.Name) || shape.Frames == null
                    || shape.Frames.Length < 1 || shape.Frames.Length > 4096) throw Error("mesh blend shape identity or frame directory differs");
                var previous = float.NegativeInfinity;
                foreach (var frame in shape.Frames) {
                    if (frame == null || frame.Vertices == null || frame.Normals == null || frame.Tangents == null
                        || frame.Vertices.Length != state.VertexCount || frame.Normals.Length != state.VertexCount || frame.Tangents.Length != state.VertexCount
                        || float.IsNaN(frame.Weight) || float.IsInfinity(frame.Weight) || frame.Weight <= previous)
                        throw Error("mesh blend shape frame layout or weight order differs");
                    previous = frame.Weight; bytes += (long)state.VertexCount * 36;
                }
            }
            RequireBytes(bytes);
        }
        private static int FormatSize(VertexAttributeFormat format)
        {
            switch (format) {
                case VertexAttributeFormat.Float32: case VertexAttributeFormat.UInt32: case VertexAttributeFormat.SInt32: return 4;
                case VertexAttributeFormat.Float16: case VertexAttributeFormat.UNorm16: case VertexAttributeFormat.SNorm16:
                case VertexAttributeFormat.UInt16: case VertexAttributeFormat.SInt16: return 2;
                case VertexAttributeFormat.UNorm8: case VertexAttributeFormat.SNorm8: case VertexAttributeFormat.UInt8: case VertexAttributeFormat.SInt8: return 1;
                default: throw Error("mesh vertex attribute format is unsupported");
            }
        }
        private static int IndexSize(IndexFormat format) => format == IndexFormat.UInt16 ? 2 : format == IndexFormat.UInt32 ? 4 : throw Error("mesh index format differs");
        internal static byte[] EncodeMesh(MeshState state)
        {
            ValidateMesh(state);
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream)) {
                writer.Write(1); writer.Write(state.VertexCount); writer.Write((int)state.IndexFormat); WriteBounds(writer, state.Bounds);
                writer.Write(state.Attributes.Length);
                foreach (var attribute in state.Attributes) { writer.Write((int)attribute.attribute); writer.Write((int)attribute.format); writer.Write(attribute.dimension); writer.Write(attribute.stream); }
                writer.Write(state.VertexBuffers.Length); foreach (var buffer in state.VertexBuffers) WriteBytes(writer, buffer);
                WriteBytes(writer, state.IndexBuffer); writer.Write(state.SubMeshes.Length);
                foreach (var sub in state.SubMeshes) { WriteBounds(writer, sub.bounds); writer.Write((int)sub.topology); writer.Write(sub.indexStart); writer.Write(sub.indexCount);
                    writer.Write(sub.baseVertex); writer.Write(sub.firstVertex); writer.Write(sub.vertexCount); }
                WriteBytes(writer, Raw(state.BindPoses, 64)); WriteBytes(writer, state.BonesPerVertex); WriteBytes(writer, Raw(state.BoneWeights, 8));
                WriteBytes(writer, Raw(state.PackedVariableWeights, 4)); writer.Write(state.FixedSkinningInVertexChannels);
                writer.Write(state.BlendShapes.Length);
                foreach (var shape in state.BlendShapes) { writer.Write(shape.Name); writer.Write(shape.Frames.Length);
                    foreach (var frame in shape.Frames) { writer.Write(frame.Weight); WriteBytes(writer, Raw(frame.Vertices, 12)); WriteBytes(writer, Raw(frame.Normals, 12)); WriteBytes(writer, Raw(frame.Tangents, 12)); } }
                if (stream.Length > MaximumBytes) throw Error("mesh wire payload exceeds its bound");
                return stream.ToArray();
            }
        }
        private static byte[] Raw<T>(T[] values, int stride) where T : struct
        {
            if (Marshal.SizeOf(typeof(T)) != stride) throw Error("native value struct layout differs: " + typeof(T).FullName);
            var bytes = new byte[checked(values.Length * stride)];
            if (bytes.Length != 0) { var pinned = GCHandle.Alloc(values, GCHandleType.Pinned);
                try { Marshal.Copy(pinned.AddrOfPinnedObject(), bytes, 0, bytes.Length); } finally { pinned.Free(); } }
            return bytes;
        }
        private static void WriteBytes(BinaryWriter writer, byte[] bytes) { writer.Write(bytes.Length); writer.Write(bytes); }
        private static void WriteBounds(BinaryWriter writer, Bounds bounds)
        {
            var center = bounds.center; var extents = bounds.extents;
            writer.Write(center.x); writer.Write(center.y); writer.Write(center.z); writer.Write(extents.x); writer.Write(extents.y); writer.Write(extents.z);
        }

        private static uint[] ReadVariableSkinning(Mesh mesh)
        {
            LoadingEngineClockProbe.RequirePinnedEngine();
            var owner = (IntPtr)CachedPointer.GetValue(mesh)!; var sharedBytes = Read(IntPtr.Add(owner, 56), 8);
            var shared = new IntPtr(BitConverter.ToInt64(sharedBytes, 0));
            if (shared == IntPtr.Zero) throw Error("mesh SharedMeshData is absent");
            var header = Read(IntPtr.Add(shared, 360), 32); var count = BitConverter.ToUInt64(header, 16); var capacity = BitConverter.ToUInt64(header, 24) >> 1;
            if (count > capacity || count > MaximumBytes / 4) throw Error("mesh variable skinning array exceeds its native bounds");
            var bytes = Read(new IntPtr(BitConverter.ToInt64(header, 0)), checked((int)count * 4));
            if (!header.SequenceEqual(Read(IntPtr.Add(shared, 360), 32)) || !sharedBytes.SequenceEqual(Read(IntPtr.Add(owner, 56), 8)))
                throw Error("mesh variable skinning changed during copy-only capture");
            var values = new uint[(int)count]; if (bytes.Length != 0) Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length); return values;
        }
        // Pinned ConvertToBoneWeights RVA 6d81c0 and SetBoneWeights RVA 6d9780.
        // Offsets count absolute uint words; weight uses the original float
        // reciprocal bits, not a double division or a normalized replacement.
        internal static void DecodeVariableSkinning(int vertexCount, uint[] words, out byte[] counts, out BoneWeight1[] weights)
        {
            if (vertexCount < 0 || vertexCount > MaximumVertices || words == null || words.Length > MaximumBytes / 4)
                throw Error("packed mesh skinning dimensions exceed their bound");
            if (words.Length == 0) { counts = Array.Empty<byte>(); weights = Array.Empty<BoneWeight1>(); return; }
            if (words.Length < vertexCount + 1 || words[0] != vertexCount + 1 || words[vertexCount] != words.Length)
                throw Error("packed mesh skinning offset directory is incomplete");
            counts = new byte[vertexCount]; weights = new BoneWeight1[words.Length - vertexCount - 1];
            var scale = BitConverter.ToSingle(BitConverter.GetBytes(0x37800080), 0); var cursor = 0;
            for (var vertex = 0; vertex < vertexCount; vertex++) {
                var first = words[vertex]; var end = words[vertex + 1];
                if (first > end || end > words.Length || end - first > byte.MaxValue) throw Error("packed mesh skinning offsets or influence count differ");
                counts[vertex] = (byte)(end - first);
                for (var index = first; index < end; index++) weights[cursor++] = new BoneWeight1 { boneIndex = (int)(words[index] & 0xffff), weight = (words[index] >> 16) * scale };
            }
            if (cursor != weights.Length) throw Error("packed mesh skinning weights are incomplete");
        }

        private static TextureState CaptureTexture(Texture2D texture)
        {
            RequireAssetVtable(texture, 0x1685618); var native = ResidentNativeMemory.Owner(texture); var fullOwner = Read(native, 352);
            var state = new TextureState { Width = texture.width, Height = texture.height, Dimension = (int)texture.dimension, Format = (int)texture.format,
                GraphicsFormat = (int)texture.graphicsFormat, MipmapCount = texture.mipmapCount, FilterMode = (int)texture.filterMode, AnisoLevel = texture.anisoLevel,
                WrapU = (int)texture.wrapModeU, WrapV = (int)texture.wrapModeV, WrapW = (int)texture.wrapModeW, MipMapBias = texture.mipMapBias,
                Readable = texture.isReadable, UpdateCount = texture.updateCount, Streaming = texture.streamingMipmaps, StreamingPriority = texture.streamingMipmapsPriority,
                RequestedMip = texture.requestedMipmapLevel, MinimumMip = texture.minimumMipmapLevel, CalculatedMip = texture.calculatedMipmapLevel,
                DesiredMip = texture.desiredMipmapLevel, LoadingMip = texture.loadingMipmapLevel, LoadedMip = texture.loadedMipmapLevel, VirtualTextureOnly = texture.vtOnly,
                Native = CaptureTextureNative(texture) };
            var owner = Read(IntPtr.Add(native, 88), 8);
            var shared = new IntPtr(BitConverter.ToInt64(owner, 0));
            if (shared == IntPtr.Zero) {
                if (!fullOwner.SequenceEqual(Read(native, 352))) throw Error("texture native owner changed during capture");
                return state;
            }
            var header = Read(shared, 80); var count = LoadingTextureDataBounds.Length(header); RequireBytes(count);
            state.CpuBytes = count == 0 ? Array.Empty<byte>() : Read(new IntPtr(BitConverter.ToInt64(header, 16)), count);
            state.CpuWidth = BitConverter.ToInt32(header, 48); state.CpuHeight = BitConverter.ToInt32(header, 52);
            state.CpuFormat = BitConverter.ToInt32(header, 56); state.CpuImageSize = BitConverter.ToUInt64(header, 64);
            state.CpuImageCount = BitConverter.ToInt32(header, 72); state.CpuMipCount = BitConverter.ToInt32(header, 76);
            state.CpuCapacity = BitConverter.ToUInt64(header, 40) >> 1; state.CpuDataPresent = count != 0;
            if (!fullOwner.SequenceEqual(Read(native, 352)) || !owner.SequenceEqual(Read(IntPtr.Add(native, 88), 8)) || !header.SequenceEqual(Read(shared, 80))
                || !state.CpuBytes.SequenceEqual(Read(new IntPtr(BitConverter.ToInt64(header, 16)), count))) throw Error("texture native CPU directory or bytes changed during capture");
            return state;
        }
        private static void CaptureMaterialNative(Material material, MaterialState state, Func<object, string?> bind)
        {
            LoadingEngineClockProbe.RequirePinnedEngine();
            var pointer = (IntPtr)CachedPointer.GetValue(material)!; var owner = Read(pointer, 224); var sharedPointer = new IntPtr(BitConverter.ToInt64(owner, 184));
            // PPtr getters can resolve unloaded assets and renderQueue returns
            // an effective value instead of the actual -1/custom-queue state.
            // Resolve already-loaded identities first and copy the owner data.
            string ShaderBinding(int instance) {
                if (instance == 0) return "";
                return ResidentNativeAssetReference.BindId(instance,typeof(Shader),bind);
            }
            state.ShaderBinding = ShaderBinding(BitConverter.ToInt32(owner, 56));
            var ownerId = BitConverter.ToInt32(owner, 64);
            if (ownerId != 0) {
                state.OwnerBinding = ResidentNativeAssetReference.BindId(ownerId,typeof(NativeObject),bind);
            }
            state.RenderQueue = BitConverter.ToInt32(owner, 60); state.EnableInstancing = owner[168] != 0;
            state.DoubleSidedGI = owner[169] != 0; state.GlobalIlluminationFlags = BitConverter.ToInt32(owner, 172);
            state.SerializedKeywords = ResidentNativeMemory.String(IntPtr.Add(pointer, 128));
            // GetShaderKeywordNames reads the existing ShaderKeywordSet. The
            // shader PPtr was proved loaded above, so this cannot load it.
            state.Keywords = state.ShaderBinding.Length == 0 || sharedPointer == IntPtr.Zero ? Array.Empty<string>() : material.shaderKeywords;
            state.TextureDirty = owner[176] != 0; state.ConstantBufferDirty = owner[177] != 0; state.HasRuntimeSheet = sharedPointer != IntPtr.Zero;
            state.Caches = new MaterialCacheState { BuildStacksEmpty = IsEmptyNativeArray(owner, 192),
                MainTextureUncached = BitConverter.ToUInt32(owner, 120) == 0xdeadbeefU, MainColorUncached = BitConverter.ToUInt32(owner, 124) == 0xdeadbeefU };
            if (state.HasRuntimeSheet) {
                var shared = Read(sharedPointer, 688);
                state.Caches.SharedReferences = BitConverter.ToInt32(shared, 12); state.Caches.PropertySheetReferences = BitConverter.ToInt32(shared, 36);
                state.Caches.RuntimeStacksEmpty = IsEmptyNativeArray(shared, 648); state.Caches.BufferAbsent = Pointer(shared, 568) == IntPtr.Zero;
                state.Caches.DisplayListsEmpty = IsEmptyNativeArray(shared, 584) && IsEmptyNativeArray(shared, 616);
                state.Caches.HashesEmpty = !shared.Skip(224).Take(12).Any(value => value != 0);
                state.Caches.TextureUnionEmpty = !shared.Skip(312).Take(256).Any(value => value != 0);
                state.Caches.SmallIndexMatchesOwner = BitConverter.ToInt32(shared, 240) == material.GetInstanceID();
                var runtimeShader = new IntPtr(BitConverter.ToInt64(shared, 16));
                if (runtimeShader != IntPtr.Zero) {
                    state.RuntimeShaderBinding = ResidentNativeAssetReference.BindPointer(runtimeShader,typeof(Shader),bind);
                }
                state.RuntimeProperties = ResidentShaderPropertySheet.Capture(IntPtr.Add(sharedPointer, 24), bind);
                // PDB declares exactly three bitfields here; the remaining
                // bits are C++ padding, not additional hidden flags.
                state.RuntimeFlags = BitConverter.ToUInt32(shared, 236) & 7U; state.RuntimeTextureDirty = shared[576] != 0;
                state.RuntimeConstantBufferDirty = shared[577] != 0; state.RuntimeBufferOverride = shared[578] != 0; state.RuntimeStacksDirty = shared[680] != 0;
                // GetShaderPassEnabled(string) interns a missing tag in the
                // global registry. Read the existing disabled IDs instead and
                // use Shader.IDToTag -> GetShaderTagName's copy-only lookup.
                var disabledCount = BitConverter.ToUInt64(shared, 264); var disabledCapacity = BitConverter.ToUInt64(shared, 272) >> 1;
                if (disabledCount > disabledCapacity || disabledCount > 4096) throw Error("material disabled-pass directory exceeds its bound");
                var disabledPointer = new IntPtr(BitConverter.ToInt64(shared, 248)); var disabled = Read(disabledPointer, checked((int)disabledCount * 4));
                var idToTag = (Func<int, string>)Delegate.CreateDelegate(typeof(Func<int, string>), typeof(Shader).GetMethod("IDToTag", BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw Error("copy-only shader tag reverse lookup is absent"));
                state.DisabledPasses = Enumerable.Range(0, (int)disabledCount).Select(index => idToTag(BitConverter.ToInt32(disabled, index * 4))).ToArray();
                if (state.DisabledPasses.Any(value => string.IsNullOrEmpty(value) || value == "<unknown tag>")
                    || state.DisabledPasses.Distinct(StringComparer.Ordinal).Count() != state.DisabledPasses.Length
                    || !disabled.SequenceEqual(Read(disabledPointer, disabled.Length))) throw Error("material disabled-pass identity is unknown, duplicate or unstable");
                var tagBegin = new IntPtr(BitConverter.ToInt64(shared, 288)); var tagEnd = new IntPtr(BitConverter.ToInt64(shared, 296));
                var tagCapacityEnd = new IntPtr(BitConverter.ToInt64(shared, 304)); var tagBytes = tagEnd.ToInt64() - tagBegin.ToInt64();
                if (tagBytes < 0 || tagBytes % 8 != 0 || tagBytes > 4096 * 8 || tagCapacityEnd.ToInt64() < tagEnd.ToInt64()
                    || (tagBegin == IntPtr.Zero && (tagEnd != IntPtr.Zero || tagCapacityEnd != IntPtr.Zero))) throw Error("material override-tag vector exceeds its native bounds");
                var tags = Read(tagBegin, (int)tagBytes); var tagRows = new List<MaterialTag>();
                for (var index = 0; index < tags.Length; index += 8) {
                    var key = idToTag(BitConverter.ToInt32(tags, index)); var value = idToTag(BitConverter.ToInt32(tags, index + 4));
                    if (string.IsNullOrEmpty(key) || key == "<unknown tag>" || string.IsNullOrEmpty(value) || value == "<unknown tag>")
                        throw Error("material override tag has an unknown or absent registered identity");
                    tagRows.Add(new MaterialTag { Name = key, Value = value });
                }
                state.OverrideTags = tagRows.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray();
                if (state.OverrideTags.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != state.OverrideTags.Length
                    || !tags.SequenceEqual(Read(tagBegin, tags.Length))) throw Error("material override tag identities changed or repeat");
                if (!shared.SequenceEqual(Read(sharedPointer, 688))) throw Error("material runtime state changed during copy-only capture");
            }
            var names = new LoadingMaterialValueObservation(); var values = new List<SavedMaterialProperty>();
            var textures = new Dictionary<int,string>();
            string TextureBinding(int instance) {
                if (instance == 0) return "";
                if (!textures.TryGetValue(instance, out var actual)) textures.Add(instance,actual=ResidentNativeAssetReference.BindId(instance,typeof(Texture),bind));
                return actual;
            }
            void Map(int offset, int kind, int size) {
                var header = Read(IntPtr.Add(pointer, offset), 16); var count = BitConverter.ToUInt64(header, 8);
                var sentinel = new IntPtr(BitConverter.ToInt64(header, 0));
                if (count > 4096 || sentinel == IntPtr.Zero) throw Error("material saved-map header exceeds its bound");
                var head = Read(sentinel, 32); if (head[25] != 1) throw Error("material saved-map sentinel differs");
                var visited = new HashSet<IntPtr>();
                void Visit(IntPtr node, int depth) {
                    if (node == sentinel) return;
                    if (node == IntPtr.Zero || depth > 128 || (ulong)visited.Count >= count || !visited.Add(node)) throw Error("material saved-map graph is invalid");
                    var data = Read(node, 32 + size); if (data[25] != 0) throw Error("material saved-map node differs");
                    Visit(new IntPtr(BitConverter.ToInt64(data, 0)), depth + 1);
                    var value = new byte[kind == 2 ? 16 : size]; Buffer.BlockCopy(data, 32, value, 0, value.Length);
                    values.Add(new SavedMaterialProperty { Name = names.ReadPropertyName(BitConverter.ToInt32(data, 28)), Kind = kind, Value = value,
                        TextureBinding = kind == 2 ? TextureBinding(BitConverter.ToInt32(data, 48)) : "" });
                    Visit(new IntPtr(BitConverter.ToInt64(data, 16)), depth + 1);
                    if (!data.SequenceEqual(Read(node, data.Length))) throw Error("material saved-map node changed during capture");
                }
                Visit(new IntPtr(BitConverter.ToInt64(head, 8)), 0);
                if ((ulong)visited.Count != count || !header.SequenceEqual(Read(IntPtr.Add(pointer, offset), 16)) || !head.SequenceEqual(Read(sentinel, 32)))
                    throw Error("material saved-map directory changed during capture");
            }
            Map(88, 0, 4); Map(104, 1, 16); Map(72, 2, 20);
            state.SavedProperties = values.OrderBy(value => value.Kind).ThenBy(value => value.Name, StringComparer.Ordinal).ToArray();
            if (state.SavedProperties.GroupBy(value => value.Kind + ":" + value.Name, StringComparer.Ordinal).Any(group => group.Count() != 1)
                || !owner.SequenceEqual(Read(pointer, 224)) || state.SerializedKeywords != ResidentNativeMemory.String(IntPtr.Add(pointer, 128)))
                throw Error("material saved property identity or native owner changed during capture");
        }
        private static byte[] Read(IntPtr address, int size)
        {
            RequireBytes(size); var data = new byte[size];
            if (size != 0 && (address == IntPtr.Zero || !ReadProcessMemory(GetCurrentProcess(), address, data, (UIntPtr)size, out var read) || read.ToUInt64() != (ulong)size))
                throw Error("native asset read failed");
            return data;
        }
        private static void RequireBytes(long bytes) { if (bytes < 0 || bytes > MaximumBytes) throw Error("native asset payload exceeds its byte bound"); }
        private static InvalidDataException Error(string message) => new InvalidDataException("Resident native assets: " + message);
    }
}
