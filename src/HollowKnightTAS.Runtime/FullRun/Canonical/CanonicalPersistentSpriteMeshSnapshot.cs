using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Only the public CPU projection is observed here. A successful capture is
    // not an ownership proof and does not authorize a write to the mesh.
    internal sealed class CanonicalPersistentSpriteMeshSnapshot
    {
        private readonly VertexAttributeDescriptor[] attributes;
        private readonly int vertexCount, subMeshCount;
        private readonly IndexFormat indexFormat;
        private readonly HideFlags hideFlags;
        private readonly string name;
        private readonly byte[] projection;
        private readonly byte[][] vertexBuffers;
        private readonly byte[] indexBuffer;
        private readonly SubMeshDescriptor[] submeshes;
        private readonly Bounds bounds;

        private CanonicalPersistentSpriteMeshSnapshot(Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable || mesh.vertexCount > 65536 || mesh.subMeshCount > 32
                || mesh.blendShapeCount != 0 || mesh.bindposes.Length != 0)
                throw new InvalidDataException("Persistent sprite mesh has an unsupported native projection.");
            vertexCount = mesh.vertexCount; subMeshCount = mesh.subMeshCount;
            indexFormat = mesh.indexFormat; hideFlags = mesh.hideFlags; name = mesh.name;
            bounds = mesh.bounds;
            submeshes = Enumerable.Range(0, subMeshCount).Select(mesh.GetSubMesh).ToArray();
            attributes = mesh.GetVertexAttributes();
            if (attributes.Length == 0 || attributes.Length > 16 || attributes.Any(a => a.stream < 0 || a.stream > 3
                || a.dimension < 1 || a.dimension > 4
                || a.attribute == VertexAttribute.BlendIndices || a.attribute == VertexAttribute.BlendWeight)
                || attributes.Select(a => a.attribute).Distinct().Count() != attributes.Length)
                throw new InvalidDataException("Persistent sprite vertex attributes have an unsupported layout.");
            using (var data = Mesh.AcquireReadOnlyMeshData(mesh)) {
                var item = data[0];
                if (item.vertexBufferCount < 1 || item.vertexBufferCount > 4) throw new InvalidDataException("Persistent sprite vertex stream count differs.");
                vertexBuffers = new byte[item.vertexBufferCount][];
                for (var i = 0; i < vertexBuffers.Length; i++) {
                    var buffer = item.GetVertexData<byte>(i);
                    if (buffer.Length > 4 * 1024 * 1024) throw new InvalidDataException("Persistent sprite native vertex buffer exceeds its bound.");
                    vertexBuffers[i] = buffer.ToArray();
                }
                var indices = item.GetIndexData<byte>();
                if (indices.Length > 4 * 1024 * 1024) throw new InvalidDataException("Persistent sprite native index buffer exceeds its bound.");
                indexBuffer = indices.ToArray();
            }
            projection = ReadProjection(mesh);
        }

        internal static CanonicalPersistentSpriteMeshSnapshot Capture(Mesh mesh) => new CanonicalPersistentSpriteMeshSnapshot(mesh);

        private CanonicalPersistentSpriteMeshSnapshot(BinaryReader reader)
        {
            if (reader.ReadInt32() != 1) throw new InvalidDataException("Persistent sprite mesh payload version differs.");
            name = reader.ReadString(); if (name.Length > 1024) throw new InvalidDataException("Persistent sprite mesh name exceeds its bound.");
            vertexCount = reader.ReadInt32(); subMeshCount = reader.ReadInt32();
            indexFormat = (IndexFormat)reader.ReadInt32(); hideFlags = (HideFlags)reader.ReadInt32();
            if (vertexCount < 0 || vertexCount > 65536 || subMeshCount < 0 || subMeshCount > 32
                || (indexFormat != IndexFormat.UInt16 && indexFormat != IndexFormat.UInt32))
                throw new InvalidDataException("Persistent sprite mesh payload dimensions differ.");
            var count = reader.ReadInt32(); if (count < 1 || count > 16) throw new InvalidDataException("Persistent sprite attributes exceed bound.");
            attributes = new VertexAttributeDescriptor[count];
            for (var i = 0; i < count; i++) attributes[i] = new VertexAttributeDescriptor((VertexAttribute)reader.ReadInt32(),
                (VertexAttributeFormat)reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            bounds = ReadBounds(reader); submeshes = new SubMeshDescriptor[subMeshCount];
            for (var i = 0; i < subMeshCount; i++) {
                var sub = new SubMeshDescriptor { bounds = ReadBounds(reader), topology = (MeshTopology)reader.ReadInt32(),
                    indexStart = reader.ReadInt32(), indexCount = reader.ReadInt32(), baseVertex = reader.ReadInt32(),
                    firstVertex = reader.ReadInt32(), vertexCount = reader.ReadInt32() };
                submeshes[i] = sub;
            }
            count = reader.ReadInt32(); if (count < 1 || count > 4) throw new InvalidDataException("Persistent sprite stream count differs.");
            vertexBuffers = new byte[count][];
            for (var i = 0; i < count; i++) vertexBuffers[i] = ReadBytes(reader);
            indexBuffer = ReadBytes(reader); projection = ReadBytes(reader);
        }

        internal byte[] Encode()
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream)) {
                writer.Write(1); writer.Write(name); writer.Write(vertexCount); writer.Write(subMeshCount);
                writer.Write((int)indexFormat); writer.Write((int)hideFlags); writer.Write(attributes.Length);
                foreach (var attribute in attributes) { writer.Write((int)attribute.attribute); writer.Write((int)attribute.format); writer.Write(attribute.dimension); writer.Write(attribute.stream); }
                WriteBounds(writer, bounds);
                foreach (var sub in submeshes) {
                    WriteBounds(writer, sub.bounds); writer.Write((int)sub.topology); writer.Write(sub.indexStart); writer.Write(sub.indexCount);
                    writer.Write(sub.baseVertex); writer.Write(sub.firstVertex); writer.Write(sub.vertexCount);
                }
                writer.Write(vertexBuffers.Length); foreach (var buffer in vertexBuffers) WriteBytes(writer, buffer);
                WriteBytes(writer, indexBuffer); WriteBytes(writer, projection); return stream.ToArray();
            }
        }

        internal static CanonicalPersistentSpriteMeshSnapshot Decode(byte[] payload)
        {
            if (payload == null || payload.Length > 16 * 1024 * 1024) throw new InvalidDataException("Persistent sprite mesh payload exceeds bound.");
            using (var stream = new MemoryStream(payload, false)) using (var reader = new BinaryReader(stream)) {
                var value = new CanonicalPersistentSpriteMeshSnapshot(reader);
                if (stream.Position != stream.Length) throw new InvalidDataException("Persistent sprite mesh payload has trailing data.");
                return value;
            }
        }
        private static void WriteBytes(BinaryWriter writer, byte[] value) { writer.Write(value.Length); writer.Write(value); }
        private static byte[] ReadBytes(BinaryReader reader)
        {
            var count = reader.ReadInt32(); if (count < 0 || count > 4 * 1024 * 1024) throw new InvalidDataException("Persistent sprite buffer exceeds bound.");
            var result = reader.ReadBytes(count); if (result.Length != count) throw new EndOfStreamException(); return result;
        }
        private static void WriteBounds(BinaryWriter writer, Bounds value)
        {
            writer.Write(value.center.x); writer.Write(value.center.y); writer.Write(value.center.z);
            writer.Write(value.size.x); writer.Write(value.size.y); writer.Write(value.size.z);
        }
        private static Bounds ReadBounds(BinaryReader reader) => new Bounds(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));

        // The caller must establish the full incoming/native-consumer proof and
        // transaction admission before invoking this method.
        internal void Apply(Mesh mesh, Func<bool> admitted)
        {
            RequireTargetLayout(mesh);
            if (!admitted()) throw new InvalidOperationException("Persistent sprite write is not admitted.");
            const MeshUpdateFlags flags = MeshUpdateFlags.DontRecalculateBounds;
            for (var i = 0; i < vertexBuffers.Length; i++)
                mesh.SetVertexBufferData(vertexBuffers[i], 0, 0, vertexBuffers[i].Length, i, flags);
            mesh.SetIndexBufferData(indexBuffer, 0, 0, indexBuffer.Length, flags);
            for (var i = 0; i < submeshes.Length; i++) mesh.SetSubMesh(i, submeshes[i], flags);
            mesh.bounds = bounds;
            RequireReadback(mesh);
        }

        // Layout and identity metadata must match before any target mutation.
        internal void RequireTargetLayout(Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable || mesh.vertexCount != vertexCount || mesh.subMeshCount != subMeshCount
                || mesh.indexFormat != indexFormat || mesh.hideFlags != hideFlags || mesh.name != name
                || mesh.blendShapeCount != 0 || mesh.bindposes.Length != 0 || !SameAttributes(attributes, mesh.GetVertexAttributes()))
                throw new InvalidDataException("Persistent sprite target mesh identity or layout differs.");
            using (var data = Mesh.AcquireReadOnlyMeshData(mesh)) {
                var item = data[0];
                if (item.vertexBufferCount != vertexBuffers.Length || item.GetIndexData<byte>().Length != indexBuffer.Length)
                    throw new InvalidDataException("Persistent sprite target buffer sizes differ.");
                for (var i = 0; i < vertexBuffers.Length; i++)
                    if (item.GetVertexData<byte>(i).Length != vertexBuffers[i].Length)
                        throw new InvalidDataException("Persistent sprite target vertex buffer size differs.");
            }
        }

        internal void RequireReadback(Mesh mesh)
        {
            RequireTargetLayout(mesh);
            if (!projection.SequenceEqual(ReadProjection(mesh)))
                throw new InvalidDataException("Persistent sprite mesh complete public projection differs.");
            using (var data = Mesh.AcquireReadOnlyMeshData(mesh)) {
                var item = data[0];
                if (!indexBuffer.SequenceEqual(item.GetIndexData<byte>().ToArray())) throw new InvalidDataException("Persistent sprite index bytes differ.");
                for (var i = 0; i < vertexBuffers.Length; i++)
                    if (!vertexBuffers[i].SequenceEqual(item.GetVertexData<byte>(i).ToArray()))
                        throw new InvalidDataException("Persistent sprite vertex bytes differ.");
            }
        }

        private static bool SameAttributes(VertexAttributeDescriptor[] left, VertexAttributeDescriptor[] right)
        {
            if (left.Length != right.Length) return false;
            for (var i = 0; i < left.Length; i++)
                if (left[i].attribute != right[i].attribute || left[i].format != right[i].format
                    || left[i].dimension != right[i].dimension || left[i].stream != right[i].stream) return false;
            return true;
        }

        private static byte[] ReadProjection(Mesh mesh)
        {
            using (var output = new MemoryStream()) using (var writer = new BinaryWriter(output)) {
                void V3(Vector3 value) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }
                void V4(Vector4 value) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); writer.Write(value.w); }
                void Bounds(Bounds value) { V3(value.center); V3(value.size); }
                void V3s(Vector3[] values) { writer.Write(values.Length); foreach (var value in values) V3(value); }
                void V4s(Vector4[] values) { writer.Write(values.Length); foreach (var value in values) V4(value); }
                Bounds(mesh.bounds); V3s(mesh.vertices); V3s(mesh.normals); V4s(mesh.tangents);
                var colors = mesh.colors; writer.Write(colors.Length);
                foreach (var color in colors) { writer.Write(color.r); writer.Write(color.g); writer.Write(color.b); writer.Write(color.a); }
                for (var channel = 0; channel < 8; channel++) {
                    var values = new List<Vector4>(); mesh.GetUVs(channel, values); V4s(values.ToArray());
                }
                var ranges = new List<Tuple<int, int>>();
                for (var i = 0; i < mesh.subMeshCount; i++) {
                    var sub = mesh.GetSubMesh(i); var indices = mesh.GetIndices(i, false);
                    if (sub.indexStart < 0 || sub.indexCount != indices.Length || sub.indexCount > 262144)
                        throw new InvalidDataException("Persistent sprite submesh index range differs.");
                    ranges.Add(Tuple.Create(sub.indexStart, checked(sub.indexStart + sub.indexCount)));
                    Bounds(sub.bounds); writer.Write((int)sub.topology); writer.Write(sub.indexStart); writer.Write(sub.indexCount);
                    writer.Write(sub.baseVertex); writer.Write(sub.firstVertex); writer.Write(sub.vertexCount);
                    foreach (var index in indices) writer.Write(index);
                }
                var end = 0;
                foreach (var range in ranges.OrderBy(range => range.Item1)) {
                    if (range.Item1 != end) throw new InvalidDataException("Persistent sprite index buffer contains an overlap or unobserved gap.");
                    end = range.Item2;
                }
                using (var data = Mesh.AcquireReadOnlyMeshData(mesh)) {
                    var byteCount = data[0].GetIndexData<byte>().Length;
                    var size = mesh.indexFormat == IndexFormat.UInt16 ? 2 : 4;
                    if (byteCount != checked(end * size)) throw new InvalidDataException("Persistent sprite index buffer contains an unreferenced tail.");
                }
                return output.ToArray();
            }
        }
    }
}
