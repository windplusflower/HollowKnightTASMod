using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using InControl;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Stateful controls are existing roots, not detached copies. A game or
    // adapter consumer retaining an axis/control keeps its original identity.
    internal static class CanonicalInputOwnerRoots
    {
        internal static int Add(IDictionary<string, object> roots)
        {
            var seen = new HashSet<object>(new References());
            foreach (var owner in roots.Values.Where(value => value is OneAxisInputControl)) seen.Add(owner);
            var additions = new SortedDictionary<string, object>(StringComparer.Ordinal);
            void AddOwner(string key, object? owner)
            {
                if (owner == null || !(owner is OneAxisInputControl || owner is TwoAxisInputControl) || !seen.Add(owner)) return;
                if (additions.Count >= 10000) throw new InvalidDataException("Canonical input control roots exceed their bound.");
                additions.Add(key, owner);
                if (owner is TwoAxisInputControl)
                    foreach (var field in typeof(TwoAxisInputControl).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
                        .Where(field => field.FieldType == typeof(OneAxisInputControl)).OrderBy(field => field.Name, StringComparer.Ordinal))
                        AddOwner(key + "/" + field.Name, field.GetValue(owner));
            }
            foreach (var pair in roots.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray()) {
                if (pair.Value is HeroActions || pair.Value is InputDevice)
                    foreach (var field in CanonicalCarryGraphCodec.Fields(pair.Value.GetType()).Where(field =>
                        typeof(OneAxisInputControl).IsAssignableFrom(field.FieldType) || typeof(TwoAxisInputControl).IsAssignableFrom(field.FieldType)))
                        AddOwner("R6/InputControl/" + pair.Key + "/" + field.DeclaringType!.FullName + "/" + field.Name, field.GetValue(pair.Value));
            }
            foreach (var pair in additions) roots.Add(pair.Key, pair.Value);
            return additions.Count;
        }
        private sealed class References : IEqualityComparer<object>
        {
            public new bool Equals(object? first, object? second) => ReferenceEquals(first, second);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
