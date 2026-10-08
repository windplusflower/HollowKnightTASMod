using System;
using System.Collections.Generic;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Process instance/scene handles are lookup keys only. Output identities
    // retain native scene-root order, including same-name and empty roots.
    internal sealed class LoadingSceneRootIdentity
    {
        internal const string Policy = "scene-root-list-ordinal-and-child-sibling-component-index-v2";
        private readonly Dictionary<int, Dictionary<int, int>> scenes = new Dictionary<int, Dictionary<int, int>>();
        private readonly Dictionary<string, int> names = new Dictionary<string, int>(StringComparer.Ordinal);
        private int rootCount;

        internal bool Contains(int scene) => scenes.ContainsKey(scene);
        internal void Clear() { scenes.Clear(); names.Clear(); rootCount = 0; }

        internal void Register(int scene, string name, int[] orderedRoots)
        {
            if (scenes.ContainsKey(scene) || orderedRoots.Length > 100000 - rootCount)
                throw new InvalidOperationException("P0 scene-root identity registration exceeds its budget or repeats a scene.");
            if (names.TryGetValue(name, out var existing) && existing != scene)
                throw new InvalidOperationException("P0 loaded scene names are ambiguous: " + name);
            var ordinals = new Dictionary<int, int>();
            for (var index = 0; index < orderedRoots.Length; index++)
            {
                var root = orderedRoots[index];
                if (root == 0 || ordinals.ContainsKey(root))
                    throw new InvalidOperationException("P0 scene-root list contains a missing or repeated native object.");
                ordinals.Add(root, index);
            }
            scenes.Add(scene, ordinals); names[name] = scene; rootCount += orderedRoots.Length;
        }

        internal int Ordinal(int scene, int root)
        {
            if (!scenes.TryGetValue(scene, out var roots) || !roots.TryGetValue(root, out var ordinal))
                throw new InvalidOperationException("P0 object is absent from its loaded scene-root list.");
            return ordinal;
        }
    }
}
