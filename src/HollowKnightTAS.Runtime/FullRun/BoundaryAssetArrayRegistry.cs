using System;
using System.Collections.Generic;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Split large managed resource arrays into exact ordered element roots.
    // Array aliases remain explicit links; no values, tails or capacities are
    // sampled away to satisfy a per-root digest budget.
    internal sealed class BoundaryAssetArrayRegistry
    {
        internal sealed class Entry
        {
            internal readonly Array Value;
            internal readonly string Id;
            internal Entry(Array value, string id) { Value = value; Id = id; }
        }
        private readonly Dictionary<Array, string> identities = new Dictionary<Array, string>();
        private readonly Dictionary<string, Array> owners = new Dictionary<string, Array>(StringComparer.Ordinal);
        private readonly List<Entry> entries = new List<Entry>();
        private int elements;
        internal IReadOnlyList<Entry> Entries => entries;
        internal string? Reference(object value) => value is Array array && identities.TryGetValue(array, out var id) ? id : null;
        internal void Register(Array array, string id)
        {
            if (identities.ContainsKey(array)) return;
            if (array.Rank != 1 || array.GetLowerBound(0) != 0 || entries.Count >= 16384
                || array.Length > 200000 - elements || owners.ContainsKey(id))
                throw new InvalidOperationException("Boundary asset array binding or budget differs: " + id);
            identities.Add(array, id); owners.Add(id, array); entries.Add(new Entry(array, id));
            elements += array.Length;
        }
        internal void Clear() { identities.Clear(); owners.Clear(); entries.Clear(); elements = 0; }
    }
}
