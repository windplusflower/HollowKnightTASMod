using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Sibling ordinals are local to a named owner. Destroying an unrelated
    // pooled root must not rename every surviving DDOL object.
    internal sealed class BoundaryObjectIdentity
    {
        private readonly Dictionary<int, string> paths = new Dictionary<int, string>();
        internal readonly GameObject[] Objects;

        internal BoundaryObjectIdentity()
        {
            Objects = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(go => go != null && go.scene.IsValid() && go.scene.isLoaded).ToArray();
            if (Objects.Length > 100000) throw new InvalidOperationException("Boundary object budget exceeded.");
            foreach (var scene in Objects.Select(go => go.scene).Distinct())
            {
                var names = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var root in scene.GetRootGameObjects())
                {
                    names.TryGetValue(root.name, out var ordinal);
                    names[root.name] = ordinal + 1;
                    Bind(root.transform, scene.name + "/" + root.name + "[" + ordinal + "]", 0);
                }
            }
            if (paths.Count != Objects.Length) throw new InvalidOperationException("Boundary hierarchy coverage differs.");
        }

        private void Bind(Transform node, string path, int depth)
        {
            if (depth > 128 || paths.Count >= 100000) throw new InvalidOperationException("Boundary hierarchy budget exceeded.");
            paths.Add(node.gameObject.GetInstanceID(), path);
            var names = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < node.childCount; index++)
            {
                var child = node.GetChild(index);
                names.TryGetValue(child.name, out var ordinal);
                names[child.name] = ordinal + 1;
                Bind(child, path + "/" + child.name + "[" + ordinal + "]", depth + 1);
            }
        }

        internal string Get(UnityEngine.Object value)
        {
            var component = value as Component;
            var go = value as GameObject ?? component?.gameObject;
            if (go == null || !go.scene.IsValid() || !go.scene.isLoaded)
                return "asset:" + value.GetType().FullName + ":" + value.name;
            if (!paths.TryGetValue(go.GetInstanceID(), out var path))
                throw new InvalidOperationException("Boundary owner was created after its identity inventory: " + go.name);
            if (component == null) return path;
            var ordinal = Array.IndexOf(go.GetComponents(component.GetType()), component);
            if (ordinal < 0) throw new InvalidOperationException("Boundary component owner binding differs.");
            return path + ":" + component.GetType().FullName + "[" + ordinal + "]";
        }
    }
}
