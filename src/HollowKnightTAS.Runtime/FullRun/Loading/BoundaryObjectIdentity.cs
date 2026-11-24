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
        private readonly Dictionary<int, string> rootPaths = new Dictionary<int, string>();
        private readonly Dictionary<int, List<GameObject>> childrenByParent = new Dictionary<int, List<GameObject>>();
        private readonly HashSet<int> inventoryIds = new HashSet<int>();
        internal readonly GameObject[] Objects;

        internal BoundaryObjectIdentity()
        {
            Objects = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(go => go != null && go.GetInstanceID() != 0 && go.scene.IsValid() && go.scene.isLoaded).ToArray();
            if (Objects.Length > 100000) throw new InvalidOperationException("Boundary object budget exceeded.");
            foreach (var go in Objects)
            {
                inventoryIds.Add(go.GetInstanceID());
                var parent = go.transform.parent;
                if (parent != null && parent.gameObject != null && parent.gameObject.GetInstanceID() != 0)
                {
                    var parentId = parent.gameObject.GetInstanceID();
                    if (!childrenByParent.TryGetValue(parentId, out var list))
                    {
                        list = new List<GameObject>();
                        childrenByParent[parentId] = list;
                    }
                    list.Add(go);
                }
            }
            foreach (var scene in Objects.Select(go => go.scene).Distinct())
            {
                var names = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root == null || root.GetInstanceID() == 0) continue;
                    names.TryGetValue(root.name, out var ordinal);
                    names[root.name] = ordinal + 1;
                    rootPaths[root.GetInstanceID()] = scene.name + "/" + root.name + "[" + ordinal + "]";
                }
            }
        }

        private string ResolvePath(Transform node, int depth)
        {
            if (depth > 128) throw new InvalidOperationException("Boundary hierarchy budget exceeded.");
            var go = node.gameObject;
            var id = go.GetInstanceID();
            if (paths.TryGetValue(id, out var cached)) return cached;

            var parent = node.parent;
            if (parent == null)
            {
                if (!rootPaths.TryGetValue(id, out var rootPath))
                    throw new InvalidOperationException("Boundary root missing from scene roots: " + node.name);
                paths[id] = rootPath;
                return rootPath;
            }

            var parentPath = ResolvePath(parent, depth + 1);
            var siblingIndex = node.GetSiblingIndex();
            var ordinal = 0;
            if (childrenByParent.TryGetValue(parent.gameObject.GetInstanceID(), out var siblings))
            {
                foreach (var sibling in siblings)
                {
                    if (!ReferenceEquals(sibling, go)
                        && sibling != null
                        && sibling.GetInstanceID() != 0
                        && sibling.transform.GetSiblingIndex() < siblingIndex
                        && string.Equals(sibling.name, node.name, StringComparison.Ordinal))
                    {
                        ordinal++;
                    }
                }
            }

            var resolved = parentPath + "/" + node.name + "[" + ordinal + "]";
            paths[id] = resolved;
            return resolved;
        }

        internal string Get(UnityEngine.Object value)
        {
            var component = value as Component;
            var go = value as GameObject ?? component?.gameObject;
            if (go == null || !go.scene.IsValid() || !go.scene.isLoaded)
                return "asset:" + value.GetType().FullName + ":" + value.name;
            if (!inventoryIds.Contains(go.GetInstanceID()))
                throw new InvalidOperationException("Boundary owner was created after its identity inventory: " + go.name);
            var path = ResolvePath(go.transform, 0);
            if (component == null) return path;
            var ordinal = Array.IndexOf(go.GetComponents(component.GetType()), component);
            if (ordinal < 0) throw new InvalidOperationException("Boundary component owner binding differs.");
            return path + ":" + component.GetType().FullName + "[" + ordinal + "]";
        }
    }
}
