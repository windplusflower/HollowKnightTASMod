using System;
using System.Collections.Generic;
using System.IO;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalCarryRootBindings
    {
        internal static SortedDictionary<string, object> Merge(params IReadOnlyDictionary<string, object>[] maps)
        {
            var result = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var map in maps)
                foreach (var pair in map) {
                    if (string.IsNullOrEmpty(pair.Key) || pair.Key.Length > 16384 || pair.Key.IndexOf('\0') >= 0 || pair.Value == null)
                        throw new InvalidDataException("Canonical cross-domain root binding is absent or invalid.");
                    if (result.TryGetValue(pair.Key, out var prior)) {
                        if (!ReferenceEquals(prior, pair.Value)) throw new InvalidDataException("Canonical cross-domain root identity is ambiguous: " + pair.Key);
                    }
                    else {
                        if (result.Count == CanonicalCarryGraphCodec.MaximumNodes) throw new InvalidDataException("Canonical cross-domain root count exceeds its bound.");
                        result.Add(pair.Key, pair.Value);
                    }
                }
            return result;
        }
    }
}
