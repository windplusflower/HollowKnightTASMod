using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Source-required asset bindings are resolved before the original HUD
    // producers. Candidate inventories are fresh for each subsequent guard;
    // an inventory from before native retirement is never reused afterward.
    internal sealed class CanonicalPersistentSpriteRequiredAssets
    {
        private readonly SortedDictionary<string, Tuple<Type, string, UnityEngine.Object>> required = new SortedDictionary<string, Tuple<Type, string, UnityEngine.Object>>(StringComparer.Ordinal);
        private readonly BoundaryObjectIdentity identities;
        internal CanonicalPersistentSpriteRequiredAssets(CanonicalCarryGraphData source, BoundaryObjectIdentity identities)
        {
            this.identities = identities;
            var inventories = new Dictionary<Type, UnityEngine.Object[]>();
            foreach (var external in source.Externals.Where(item => item.Binding.StartsWith("asset:", StringComparison.Ordinal))) {
                if (required.Count >= 128) throw new InvalidDataException("Persistent sprite required asset directory exceeds its bound.");
                var key = CanonicalAssetBindingKey.Resolve(external.Binding, AppDomain.CurrentDomain.GetAssemblies(), type => typeof(UnityEngine.Object).IsAssignableFrom(type));
                if (external.Type != CanonicalCarryGraphCodec.Identity(key.Item1)) throw new InvalidDataException("Persistent sprite required asset concrete type differs.");
                if (!inventories.TryGetValue(key.Item1, out var candidates)) inventories.Add(key.Item1, candidates = Inventory(key.Item1));
                var owner = CanonicalAssetBindingKey.UniqueLiveOriginal(candidates, key.Item2, external.Binding, value => value != null, value => value.name, identities.Get);
                required.Add(external.Binding, Tuple.Create(key.Item1, key.Item2, owner));
            }
        }
        private static UnityEngine.Object[] Inventory(Type type)
        {
            var values = Resources.FindObjectsOfTypeAll(type);
            if (values.Length > 100000) throw new InvalidDataException("Persistent sprite original asset candidate inventory exceeds its bound.");
            return values.Where(value => value != null && value.GetType() == type).ToArray();
        }
        internal object Resolve(string binding) => required.TryGetValue(binding, out var value) && value.Item3 != null
            ? value.Item3 : throw new InvalidDataException("Persistent sprite required original asset is absent or retired: " + binding);
        internal void RequireCurrent()
        {
            var inventories = new Dictionary<Type, UnityEngine.Object[]>();
            foreach (var pair in required) {
                var value = pair.Value;
                if (!inventories.TryGetValue(value.Item1, out var candidates)) inventories.Add(value.Item1, candidates = Inventory(value.Item1));
                CanonicalAssetBindingKey.UniqueLiveOriginal(candidates, value.Item2, pair.Key, owner => owner != null, owner => owner.name, identities.Get, value.Item3);
            }
        }
    }
}
