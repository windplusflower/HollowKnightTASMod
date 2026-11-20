using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Copies exposed CPU values. GPU-only resources and hidden native metadata
    // remain omissions; these hashes are diagnostics, never serialized payloads.
    internal sealed class LoadingAssetValueObservation
    {
        private LoadingMaterialValueObservation? materials;
        private readonly LoadingFontValueObservation fonts = new LoadingFontValueObservation();
        private readonly LoadingTextureValueObservation textures = new LoadingTextureValueObservation();
        internal object[] FontDetails => fonts.Details;
        internal void BeginSample() { materials?.BeginSample(); fonts.BeginSample(); }
        internal object? Capture(UnityEngine.Object asset, Action<string> omit)
        {
            if (asset is Mesh mesh)
            {
                if (!mesh.isReadable)
                {
                    omit("asset-mesh-cpu-data-unreadable");
                    return new { mesh.isReadable, mesh.vertexCount, mesh.subMeshCount, mesh.indexFormat };
                }
                if (mesh.vertexCount > 262144 || mesh.subMeshCount > 256 || mesh.blendShapeCount > 128)
                    throw new InvalidOperationException("P0 mesh observation exceeds its bounded geometry budget.");
                var uv = new List<object>();
                for (var channel = 0; channel < 8; channel++)
                {
                    var values = new List<Vector4>();
                    mesh.GetUVs(channel, values);
                    uv.Add(CopyHash(values.ToArray(), 16));
                }
                var shapes = new List<object>();
                for (var shape = 0; shape < mesh.blendShapeCount; shape++)
                {
                    var count = mesh.GetBlendShapeFrameCount(shape);
                    if (count > 128) throw new InvalidOperationException("P0 blend shape frame budget exceeded.");
                    var frames = new List<object>();
                    for (var frame = 0; frame < count; frame++)
                    {
                        var vertices = new Vector3[mesh.vertexCount];
                        var normals = new Vector3[mesh.vertexCount];
                        var tangents = new Vector3[mesh.vertexCount];
                        mesh.GetBlendShapeFrameVertices(shape, frame, vertices, normals, tangents);
                        frames.Add(new { weight = mesh.GetBlendShapeFrameWeight(shape, frame),
                            vertices = CopyHash(vertices, 12), normals = CopyHash(normals, 12), tangents = CopyHash(tangents, 12) });
                    }
                    shapes.Add(new { name = mesh.GetBlendShapeName(shape), frames = frames.ToArray() });
                }
                // Native capacity/layout and variable-influence skinning still
                // need a restore contract; the legacy weights alone do not close it.
                if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.BlendWeight))
                    omit("asset-mesh-native-skinning-data");
                var registeredBounds = CanonicalRegisteredVisualMeshCachePolicy.Owns(mesh);
                if (registeredBounds) CanonicalPersistentSpriteNativeGuard.ProtectFinalVisual(new[] { mesh }, new[] { HeroController.SilentInstance!.GetComponent<MeshFilter>() });
                return new { mesh.isReadable, mesh.vertexCount, mesh.subMeshCount, mesh.indexFormat,
                    mesh.bounds, attributes = mesh.GetVertexAttributes(), vertices = CopyHash(mesh.vertices, 12),
                    normals = CopyHash(mesh.normals, 12), tangents = CopyHash(mesh.tangents, 16),
                    colors = CopyHash(mesh.colors, 16), uv = uv.ToArray(),
                    bindposes = CopyHash(mesh.bindposes, 64),
                    submeshes = Enumerable.Range(0, mesh.subMeshCount).Select(index => new {
                        description = registeredBounds ? CanonicalRegisteredVisualMeshCachePolicy.Project(mesh.GetSubMesh(index), mesh.bounds) : mesh.GetSubMesh(index), indices = CopyHash(mesh.GetIndices(index, false), 4) }).ToArray(),
                    blendShapes = shapes.ToArray() };
            }
            if (asset is Sprite sprite)
            {
                // GetSpriteUVs invokes PrepareRenderingDataIfNeeded first.
                // Do not materialize or refresh that cache from an observer.
                omit("asset-sprite-native-uv-physics-and-skinning-data");
                return new { sprite.rect, sprite.pivot, sprite.border, sprite.pixelsPerUnit,
                    sprite.packed, sprite.packingMode, sprite.packingRotation,
                    sprite.texture, sprite.associatedAlphaSplitTexture,
                    vertices = CopyHash(sprite.vertices, 8), triangles = CopyHash(sprite.triangles, 2) };
            }
            if (asset is Texture2D texture)
            {
                return new { texture.width, texture.height, texture.dimension, texture.format,
                    texture.mipmapCount, texture.isReadable, texture.filterMode, texture.anisoLevel,
                    texture.mipMapBias, texture.wrapModeU, texture.wrapModeV, texture.wrapModeW,
                    cpuData = textures.Capture(texture, omit) };
            }
            if (asset is RenderTexture render)
            {
                omit("asset-render-texture-gpu-contents");
                return new { descriptor = render.descriptor, render.filterMode, render.anisoLevel,
                    render.mipMapBias, render.wrapModeU, render.wrapModeV, render.wrapModeW,
                    created = render.IsCreated() };
            }
            if (asset is Material material)
            {
                materials ??= new LoadingMaterialValueObservation();
                return new { material.shader, material.renderQueue, material.shaderKeywords,
                    material.enableInstancing, material.doubleSidedGI, material.globalIlluminationFlags,
                    native = materials.CaptureMaterial(material, omit) };
            }
            if (asset is TextAsset text) return new { contents = CopyHash(text.bytes, 1) };
            if (asset is Font font) return fonts.Capture(font, omit);
            return null;
        }

        private static object CopyHash<T>(T[] values, int expectedStride) where T : struct
        {
            if (Marshal.SizeOf(typeof(T)) != expectedStride)
                throw new InvalidOperationException("P0 asset copy has an unexpected element layout.");
            var length = checked(values.Length * expectedStride);
            if (length > 64 * 1024 * 1024) throw new InvalidOperationException("P0 asset CPU copy budget exceeded.");
            var bytes = new byte[length];
            if (length != 0)
            {
                var pinned = GCHandle.Alloc(values, GCHandleType.Pinned);
                try { Marshal.Copy(pinned.AddrOfPinnedObject(), bytes, 0, length); }
                finally { pinned.Free(); }
            }
            using (var sha = SHA256.Create())
                return new { count = values.Length, stride = expectedStride,
                    sha256 = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() };
        }
    }
}
