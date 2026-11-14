using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentRendererState
    {
        private static ShapeConsumer[] CaptureShapes(ResidentObjectDirectory directory, SourceBindings references)
        {
            return directory.NativeBindings.Where(pair => pair.Value is ParticleSystem)
                .OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => {
                    var owner = M.Owner(pair.Value);
                    var modules = M.Ptr(IntPtr.Add(owner, 80));
                    if (modules == IntPtr.Zero) throw Error("particle shape modules are absent");
                    var mesh = M.Ptr(IntPtr.Add(modules, 856));
                    var shared = M.Ptr(IntPtr.Add(modules, 912));
                    var node = IntPtr.Add(modules, 1048);
                    var links = M.Read(node, 24);
                    var previous = M.Ptr(links, 0); var next = M.Ptr(links, 8);
                    if ((previous == IntPtr.Zero) != (next == IntPtr.Zero) || previous != IntPtr.Zero && M.Ptr(links, 16) != owner)
                        throw Error("particle shape consumer owner or link pair differs");
                    if (previous != IntPtr.Zero && (mesh == IntPtr.Zero || M.Ptr(IntPtr.Add(previous, 8)) != node || M.Ptr(next) != node))
                        throw Error("particle shape consumer links are not reciprocal");
                    if (shared != IntPtr.Zero && (mesh == IntPtr.Zero || M.Ptr(IntPtr.Add(mesh, 56)) != shared))
                        throw Error("particle shape retains historical shared Mesh data");
                    var key = references.Pointer<Mesh>(mesh);
                    return new ShapeConsumer { Owner = pair.Key, Mesh = key, SharedMesh = shared == IntPtr.Zero ? "" : key,
                        Linked = previous != IntPtr.Zero, Execution = directory.Ownership.IsExecution((Component)pair.Value) };
                }).ToArray();
        }

        internal static void ValidateParticleShapes(Data renderers, ResidentParticleSystemState.Data particles)
        {
            if (!renderers.Shapes.Select(row => (row.Owner, row.Execution)).SequenceEqual(particles.Rows.Select(row => (row.Owner, row.Execution))))
                throw Error("shape and particle owner directories differ");
            for (var index = 0; index < renderers.Shapes.Length; index++) {
                var body = particles.Rows[index].Simulation;
                if (body == null) continue;
                var shape = renderers.Shapes[index];
                if (shape.Mesh != body.Caches.Objects[0] || shape.SharedMesh != body.Caches.SharedMesh
                    || shape.Linked != ResidentParticleSystemState.HasShapeMeshLink(body))
                    throw Error("shape and particle cache domains disagree");
            }
        }

        private static void ValidateShapes(ShapeConsumer[] shapes)
        {
            if (shapes.Length > MaximumSlots) throw Error("particle shape directory exceeds bounds");
            string? previous = null;
            foreach (var shape in shapes) {
                if (shape == null || !Text(shape.Owner, false) || !Text(shape.Mesh) || !Text(shape.SharedMesh)
                    || shape.SharedMesh.Length != 0 && shape.SharedMesh != shape.Mesh || shape.Linked && shape.Mesh.Length == 0
                    || previous != null && StringComparer.Ordinal.Compare(previous, shape.Owner) >= 0)
                    throw Error("particle shape binding or directory order differs");
                previous = shape.Owner;
            }
        }
    }
}
