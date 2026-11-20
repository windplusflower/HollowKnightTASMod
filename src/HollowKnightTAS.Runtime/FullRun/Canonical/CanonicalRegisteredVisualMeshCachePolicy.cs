using System;
using System.Reflection;
using HollowKnightTAS.Core.Movie;
using UnityEngine;
using UnityEngine.Rendering;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalRegisteredVisualMeshCachePolicy
    {
        internal const string Id = "v15-registered-Hero-tk2dSprite-submesh-derived-bounds:full-logic-and-physics-retained:v1";
        private static readonly FieldInfo Mesh = typeof(tk2dSprite).GetField("mesh", BindingFlags.Instance | BindingFlags.NonPublic)!;
        internal static bool Owns(UnityEngine.Mesh mesh)
        {
            if (NativeExecutionProfile.SelectedProfileId != NativeExecutionProfile.CanonicalBoundaryProfileId) return false;
            var hero = HeroController.SilentInstance;
            if (hero == null) return false;
            var sprite = hero.GetComponent<tk2dSprite>(); var filter = hero.GetComponent<MeshFilter>();
            return sprite != null && filter != null && ReferenceEquals(Mesh.GetValue(sprite), mesh) && ReferenceEquals(filter.sharedMesh, mesh);
        }
        internal static SubMeshDescriptor Project(SubMeshDescriptor value, Bounds derivedBounds)
        { value.bounds = derivedBounds; return value; }
    }
}
