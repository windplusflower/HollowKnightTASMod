using System;
using System.Collections.Generic;
namespace HollowKnightTAS.Runtime.FullRun
{
    // Only integer first-root provenance for the existing graph IDs. No whole-
    // world field paths/tokens; selected Marker tokens retain their exact IDs.
    internal sealed class LoadingSharedGraphEvidence
    {
        private readonly Dictionary<string, int> roots = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<string> rootNames = new List<string>();
        private readonly List<int> definitionRoots = new List<int>();
        private readonly List<object> events = new List<object>();
        private int rootCharacters, eventCharacters;
        private string? incomplete;
        internal static bool WantsTokens(string root) => root.IndexOf("/Map Marker Action[", StringComparison.Ordinal) >= 0;
        internal void Reference(int id, string root, string selectedPath, bool first)
        {
            if (incomplete != null) return;
            if (first) {
                if (id != definitionRoots.Count || id >= 1048576) { incomplete = "definition-budget-or-order"; return; }
                if (!roots.TryGetValue(root, out var index)) {
                    if (rootCharacters + root.Length > 8*1024*1024 || roots.Count >= 131072) { incomplete = "first-root-budget"; return; }
                    index = rootNames.Count; roots.Add(root, index); rootNames.Add(root); rootCharacters += root.Length;
                }
                definitionRoots.Add(index);
            }
            if (selectedPath.Length == 0) return;
            if (id < 0 || id >= definitionRoots.Count) { incomplete = "definition-unavailable"; return; }
            var origin = rootNames[definitionRoots[id]];
            if (!Admit(selectedPath.Length + origin.Length + 64)) return;
            events.Add(new { kind = first ? "define" : "reference", path = selectedPath, originalId = id, firstRoot = origin });
        }
        internal void Token(string path, string token)
        {
            if (incomplete != null || !Admit(path.Length + token.Length + 32)) return;
            events.Add(new { kind = "token", path, token });
        }
        private bool Admit(int characters)
        {
            if (eventCharacters + characters > 2*1024*1024 || events.Count >= 32768) { incomplete = "selected-event-budget"; return false; }
            eventCharacters += characters; return true;
        }
        internal object Summary() => new { complete = incomplete == null, incomplete,
            definitionCount = definitionRoots.Count, firstRootCount = rootNames.Count, selectedEvents = events.Count };
        internal object Snapshot() => new { schema = "marker-shared-traversal-first-root-v2", diagnosticOnly = true,
            complete = incomplete == null, incomplete, definitionCount = definitionRoots.Count, firstRootCount = rootNames.Count, events = events.ToArray(),
            identityRule = "exact original ID and first traversal root; field path only at selected roots; same firstRoot never proves same object", completeStateCoverage = false };
    }
}
