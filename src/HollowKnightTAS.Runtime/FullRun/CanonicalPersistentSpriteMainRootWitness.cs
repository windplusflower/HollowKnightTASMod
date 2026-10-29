using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;

namespace HollowKnightTAS.Runtime.FullRun
{
    // A source MainNode reference alone is insufficient. Only an existing
    // explicit main root can witness that the target object will retain its
    // identity when the complete main graph is applied.
    internal sealed class CanonicalPersistentSpriteMainRootWitness
    {
        private readonly Dictionary<object, Tuple<string, int>> bindings = new Dictionary<object, Tuple<string, int>>(new References());
        private readonly object[] sourceDescriptors;
        internal CanonicalPersistentSpriteMainRootWitness(CanonicalCarryGraphData main, CanonicalCarryGraphData sprites,
            IReadOnlyDictionary<string, object> originalSoulGlobalRoots)
        {
            const string prefix = "PersistentSprites/MainNode/";
            var sourceNodes = new HashSet<int>();
            foreach (var external in sprites.Externals.Where(item => item.Binding.StartsWith(prefix, StringComparison.Ordinal))) {
                if (!int.TryParse(external.Binding.Substring(prefix.Length), out var id) || id < 1 || id > main.Nodes.Length
                    || main.Nodes[id - 1].Type != external.Type) throw new InvalidDataException("Persistent sprite source main-node witness is malformed.");
                sourceNodes.Add(id);
            }
            if (sourceNodes.Count > 128) throw new InvalidDataException("Persistent sprite main-root witness directory exceeds its finite bound.");
            sourceDescriptors = sourceNodes.OrderBy(id => id).Select(id => (object)new { sourceMainNode = id,
                type = main.Nodes[id - 1].Type, explicitRoots = main.Roots.Where(root => root.Node == id).Select(root => root.Binding).ToArray() }).ToArray();
            foreach (var root in main.Roots.Where(root => sourceNodes.Contains(root.Node))) {
                if (!originalSoulGlobalRoots.TryGetValue(root.Binding, out var owner)) continue;
                if (root.Node < 1 || root.Node > main.Nodes.Length || main.Nodes[root.Node - 1].Type != CanonicalCarryGraphCodec.Identity(typeof(FsmGameObject))
                    || owner.GetType() != typeof(FsmGameObject)) throw new InvalidDataException("Persistent sprite source explicit global root type differs.");
                if (bindings.TryGetValue(owner, out var prior) && (prior.Item1 != root.Binding || prior.Item2 != root.Node))
                    throw new InvalidDataException("Persistent sprite source global root witness is ambiguous.");
                bindings[owner] = Tuple.Create(root.Binding, root.Node);
            }
        }
        internal bool Contains(object owner) => bindings.ContainsKey(owner);
        internal object Audit => new { sourceMainNodes = sourceDescriptors,
            retainedBindings = bindings.Values.Select(value => new { binding = value.Item1, sourceMainNode = value.Item2 }).ToArray() };
        internal void RequirePrepared(CanonicalCarryGraphCodec.PreparedView main)
        {
            foreach (var pair in bindings)
                if (!ReferenceEquals(main.Root(pair.Value.Item1), pair.Key) || !ReferenceEquals(main.Node(pair.Value.Item2), pair.Key))
                    throw new InvalidOperationException("Persistent sprite explicit main root lost its original global identity.");
        }
        private sealed class References : IEqualityComparer<object>
        {
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
