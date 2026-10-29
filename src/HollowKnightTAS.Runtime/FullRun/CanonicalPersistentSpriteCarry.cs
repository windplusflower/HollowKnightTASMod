using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalPersistentSpriteCarry
    {
        internal const string Root = "R2/PersistentSprites/context";
        internal static readonly Type[] DataTypes = { typeof(Data), typeof(object[]), typeof(object[][]), typeof(string[]), typeof(byte[][]),
            typeof(CanonicalPersistentSpriteContinuation.Data), typeof(CanonicalBorderArrowCarry.Data), typeof(CanonicalBorderArrowCarry.Data[]), typeof(tk2dSpriteAnimator).GetNestedType("State", BindingFlags.Public | BindingFlags.NonPublic)! };
        internal sealed class Data
        {
            public string[] Owners = Array.Empty<string>(), Fields = Array.Empty<string>();
            public object?[][] Values = Array.Empty<object?[]>();
            public byte[][] Meshes = Array.Empty<byte[]>();
            public CanonicalPersistentSpriteContinuation.Data Continuation = new CanonicalPersistentSpriteContinuation.Data();
        }
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static readonly string[] OriginalNames = { "mesh", "meshVertices", "meshNormals", "meshTangents", "meshColors",
            "collection", "collectionInst", "_color", "_scale", "_spriteId", "boxCollider2D", "polygonCollider2D", "edgeCollider2D",
            "boxCollider", "meshCollider", "meshColliderPositions", "meshColliderMesh", "SpriteChanged", "_cachedRenderer", "renderLayer" };
        private static readonly Type[] OriginalTypes = { typeof(Mesh), typeof(Vector3[]), typeof(Vector3[]), typeof(Vector4[]), typeof(Color32[]),
            typeof(tk2dSpriteCollectionData), typeof(tk2dSpriteCollectionData), typeof(Color), typeof(Vector3), typeof(int), typeof(BoxCollider2D),
            typeof(List<PolygonCollider2D>), typeof(List<EdgeCollider2D>), typeof(BoxCollider), typeof(MeshCollider), typeof(Vector3[]), typeof(Mesh),
            typeof(Action<tk2dBaseSprite>), typeof(Renderer), typeof(int) };
        internal static FieldInfo[] Fields()
        {
            if (typeof(tk2dSprite).Module.ModuleVersionId.ToString("D") != "2b54cdaf-7a93-4f0e-b297-d50c4090ea9f")
                throw new InvalidDataException("Persistent sprite original assembly differs.");
            var fields = typeof(tk2dSprite).GetFields(Flags).Concat(typeof(tk2dBaseSprite).GetFields(Flags)).ToArray();
            if (fields.Length != OriginalNames.Length || fields.Where((field, index) => field.Name != OriginalNames[index]
                || field.FieldType != OriginalTypes[index] || field.DeclaringType != (index < 5 ? typeof(tk2dSprite) : typeof(tk2dBaseSprite))).Any())
                throw new InvalidDataException("Persistent sprite original managed field ABI differs.");
            return fields;
        }
        internal static Data Capture()
        {
            var owners = CanonicalPersistentSpriteDirectory.Resolve(); var fields = Fields();
            return new Data {
                Owners = owners.Keys.ToArray(), Fields = fields.Select(field => field.DeclaringType!.FullName + "/" + field.Name + "=" + field.FieldType.FullName).ToArray(),
                Values = owners.Values.Select(owner => fields.Select(field => field.GetValue(owner)).ToArray()).ToArray(),
                Meshes = owners.Values.Select(owner => CanonicalPersistentSpriteMeshSnapshot.Capture((Mesh)fields[0].GetValue(owner)).Encode()).ToArray(),
                Continuation = CanonicalPersistentSpriteContinuation.Capture()
            };
        }
        internal static SortedDictionary<string, object> Roots(Data data) {
            var roots = CanonicalPersistentSpriteContinuation.HealthRoots(); roots.Add(Root, data); return roots;
        }
    }
}
