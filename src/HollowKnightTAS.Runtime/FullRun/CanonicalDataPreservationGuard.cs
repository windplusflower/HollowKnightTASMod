using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Read-only before/after audit using the existing main codec. This is not
    // another payload domain and never provides a restore/application action.
    internal sealed class CanonicalDataPreservationGuard
    {
        private readonly CanonicalCarryGraphCodec codec;
        private readonly Func<IReadOnlyDictionary<string, object>> roots;
        private readonly Func<IReadOnlyDictionary<string, string>> scalars;
        private readonly Func<object[]> owners;
        private readonly Dictionary<string, object> beforeRoots;
        private readonly Dictionary<string, string> beforeScalars;
        private readonly object[] beforeOwners;
        private readonly byte[] beforeData;
        internal CanonicalDataPreservationGuard(CanonicalCarryGraphCodec codec,
            Func<IReadOnlyDictionary<string, object>> roots, Func<IReadOnlyDictionary<string, string>> scalars, Func<object[]> owners)
        {
            this.codec = codec; this.roots = roots; this.scalars = scalars; this.owners = owners;
            beforeRoots = roots().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            beforeScalars = scalars().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            beforeOwners = (object[])owners().Clone(); beforeData = Read(beforeRoots);
        }
        private byte[] Read(IReadOnlyDictionary<string, object> values)
        {
            var graph = codec.Capture(values, _ => null);
            if (graph.Unsupported.Length != 0) throw new InvalidDataException("Canonical preservation audit has unresolved data: " + string.Join(", ", graph.Unsupported.Take(4)));
            return codec.Encode(graph);
        }
        internal void Verify()
        {
            var currentRoots = roots(); var currentOwners = owners(); var currentScalars = scalars();
            if (currentRoots.Count != beforeRoots.Count || beforeRoots.Any(pair => !currentRoots.TryGetValue(pair.Key, out var current)
                || !ReferenceEquals(pair.Value, current)) || currentOwners.Length != beforeOwners.Length
                || currentOwners.Where((owner, index) => !ReferenceEquals(owner, beforeOwners[index])).Any())
                throw new InvalidOperationException("Canon changed a protected data or Hero owner.");
            if (currentScalars.Count != beforeScalars.Count || beforeScalars.Any(pair => !currentScalars.TryGetValue(pair.Key, out var current) || current != pair.Value))
                throw new InvalidOperationException("Canon changed a protected Hero gameplay field, pose or body scalar.");
            if (!Read(currentRoots).SequenceEqual(beforeData)) throw new InvalidOperationException("Canon changed PlayerData, SceneData or Hero state data.");
        }
    }
}
