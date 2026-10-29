using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalAssetBindingKey
    {
        internal static T UniqueOriginal<T>(T[] matchingCandidates, string binding, Func<T, string> identity)
        {
            if (matchingCandidates.Length != 1 || !string.Equals(identity(matchingCandidates[0]), binding, StringComparison.Ordinal))
                throw new InvalidDataException("Original asset candidate is absent, ambiguous or a different scene/resource identity: " + binding);
            return matchingCandidates[0];
        }
        internal static T UniqueLiveOriginal<T>(T[] inventory, string name, string binding, Func<T, bool> alive,
            Func<T, string> readName, Func<T, string> identity, T? required = null) where T : class
        {
            if (required != null && !alive(required)) throw new InvalidDataException("Required original asset was retired: " + binding);
            // Never read a destroyed wrapper's native name. Every live match,
            // including a newly produced duplicate, still participates.
            var value = UniqueOriginal(inventory.Where(alive).Where(owner => readName(owner) == name).ToArray(), binding, identity);
            if (required != null && !ReferenceEquals(required, value)) throw new InvalidDataException("Required original asset identity was replaced: " + binding);
            return value;
        }
        internal static Tuple<Type, string> Resolve(string binding, Assembly[] loaded, Func<Type, bool> admitType)
        {
            const string prefix = "asset:";
            if (!binding.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Original asset binding prefix differs.");
            var split = binding.IndexOf(':', prefix.Length);
            if (split <= prefix.Length || loaded.Length > 2000)
                throw new InvalidDataException("Original asset binding syntax or assembly budget differs.");
            var fullName = binding.Substring(prefix.Length, split - prefix.Length);
            var matches = loaded.Select(assembly => assembly.GetType(fullName, false, false)).Where(type => type != null)
                .Cast<Type>().Distinct().ToArray();
            if (matches.Length != 1 || matches[0].FullName != fullName || matches[0].IsAbstract || matches[0].IsInterface
                || matches[0].ContainsGenericParameters || !admitType(matches[0]))
                throw new InvalidDataException("Original asset concrete type is absent, ambiguous or unregistered: " + fullName);
            return Tuple.Create(matches[0], binding.Substring(split + 1));
        }
    }
}
