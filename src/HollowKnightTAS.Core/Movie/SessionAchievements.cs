using System;
using System.Collections.Generic;
using System.Linq;

namespace HollowKnightTAS.Core.Movie
{
    // An empty set is the versioned initial state; account data is never imported.
    public sealed class SessionAchievements
    {
        private readonly SortedSet<string> unlocked = new SortedSet<string>(StringComparer.Ordinal);

        private static void Validate(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 256)
                throw new ArgumentException("Invalid session achievement identifier.", nameof(id));
        }

        public bool IsUnlocked(string id) { Validate(id); return unlocked.Contains(id); }
        public void Unlock(string id)
        {
            Validate(id);
            if (!unlocked.Contains(id) && unlocked.Count >= 4096)
                throw new InvalidOperationException("Session achievement budget exceeded.");
            unlocked.Add(id);
        }
        public void Clear(string id) { Validate(id); unlocked.Remove(id); }
        public void Reset() => unlocked.Clear();
        public string[] Capture() => unlocked.ToArray();
    }
}
