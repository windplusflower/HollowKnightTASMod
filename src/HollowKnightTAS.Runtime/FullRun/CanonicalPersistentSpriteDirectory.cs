using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // This is deliberately a fixed persistent-owner directory, not a scene scan.
    // Resolving it does not establish exclusive ownership of the native Mesh;
    // the eventual carry domain must independently prove its incoming aliases.
    internal static class CanonicalPersistentSpriteDirectory
    {
        internal const int OwnerCount = 8 + CanonicalBorderArrowCarry.OwnerCount;
        private static readonly FieldInfo MeshField = typeof(tk2dSprite).GetField("mesh", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidDataException("Original tk2dSprite mesh field is absent.");
        private static readonly string[] HudPaths = {
            "Hud Canvas/Extras/Blocker HUD Icon",
            "Hud Canvas/Health/Health 1",
            "Hud Canvas/Health/Health 2",
            "Hud Canvas/Health/Joni Health",
            "Hud Canvas/Soul Orb/HUD_frame",
            "Hud Canvas/Soul Orb/SoulOrb_fill/Liquid"
        };

        internal static SortedDictionary<string, tk2dSprite> Resolve()
        {
            var hero = HeroController.SilentInstance ?? throw new InvalidDataException("Persistent sprite Hero is absent.");
            var cameras = GameCameras.instance ?? throw new InvalidDataException("Persistent sprite camera is absent.");
            var hud = FindUnique(cameras.transform, "HudCamera");
            var result = new SortedDictionary<string, tk2dSprite>(StringComparer.Ordinal);
            Add(result, "Hero/Effects/Dash Burst", FindPath(hero.transform, "Effects/Dash Burst"));
            Add(result, CanonicalFinalVisualCarry.Shadow, FindPath(hero.transform, "Effects/Shadow Recharge"));
            foreach (var path in HudPaths) Add(result, "HudCamera/" + path, FindPath(hud, path));
            foreach (var key in CanonicalBorderArrowCarry.OwnerKeys) Add(result, key, FindPath(hud, key.Substring("HudCamera/".Length)));
            if (result.Count != OwnerCount) throw new InvalidDataException("Persistent sprite owner count differs.");
            var meshes = new HashSet<Mesh>();
            foreach (var pair in result) {
                var mesh = MeshField.GetValue(pair.Value) as Mesh;
                if (mesh == null || !ReferenceEquals(mesh, pair.Value.GetComponent<MeshFilter>().sharedMesh)
                    || !meshes.Add(mesh))
                    throw new InvalidDataException("Persistent sprite original mesh alias differs: " + pair.Key);
            }
            return result;
        }

        private static Transform FindPath(Transform root, string path)
        {
            var current = root;
            foreach (var name in path.Split('/')) current = FindUnique(current, name);
            return current;
        }

        private static Transform FindUnique(Transform parent, string name)
        {
            Transform? found = null;
            for (var i = 0; i < parent.childCount; i++) {
                var child = parent.GetChild(i);
                if (!string.Equals(child.name, name, StringComparison.Ordinal)) continue;
                if (found != null) throw new InvalidDataException("Persistent sprite owner path is ambiguous: " + name);
                found = child;
            }
            return found ?? throw new InvalidDataException("Persistent sprite owner path is absent: " + name);
        }

        private static void Add(IDictionary<string, tk2dSprite> directory, string key, Transform owner)
        {
            var sprites = owner.GetComponents<tk2dSprite>();
            var filters = owner.GetComponents<MeshFilter>();
            var renderers = owner.GetComponents<MeshRenderer>();
            if (sprites.Length != 1 || sprites[0].GetType() != typeof(tk2dSprite) || filters.Length != 1 || renderers.Length != 1)
                throw new InvalidDataException("Persistent sprite component directory differs: " + key);
            directory.Add(key, sprites[0]);
        }
    }
}
