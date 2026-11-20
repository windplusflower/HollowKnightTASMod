using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
namespace HollowKnightTAS.Runtime.FullRun
{
    // Two integers per original ID; no whole-world paths or values.
    // This finite relation diagnostic never changes the original walker/SHA.
    internal sealed class LoadingSharedGraphEvidence : IDisposable
    {
        private struct Definition { internal int Root, Ordinal; }
        private readonly Dictionary<string, int> roots = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<string> rootNames = new List<string>();
        private readonly List<int> rootCounts = new List<int>();
        private readonly List<Definition> definitions = new List<Definition>();
        private readonly List<object> events = new List<object>();
        private readonly List<string> selectedRoots = new List<string>();
        private readonly List<object> digests = new List<object>();
        private readonly StringBuilder canonical = new StringBuilder();
        private SHA256? canonicalHash;
        private string? active;
        private int rootCharacters, eventCharacters, tokenCount, pendingId = -1;
        private string? incomplete;
        internal static bool WantsTokens(string root)
        {
            var component = root.IndexOf('|');
            return root.IndexOf("/Map Marker Action[", StringComparison.Ordinal) >= 0 && component >= 0;
        }
        internal void BeginRoot(string root)
        {
            if (active != null) incomplete = "unfinished-selected-root";
            canonicalHash?.Dispose(); canonicalHash = null;
            active = WantsTokens(root) ? root : null; canonical.Clear(); tokenCount = 0; pendingId = -1;
            if (active != null) {
                if (selectedRoots.Contains(root) || selectedRoots.Count >= 21) { incomplete = "selected-root-count-or-duplicate"; active = null; }
                else if (Admit(root)) { selectedRoots.Add(root); canonicalHash = SHA256.Create(); }
                else active = null;
            }
        }
        internal void EndRoot(string rawSha)
        {
            if (active == null) return;
            if (canonicalHash != null) {
                Flush(); canonicalHash.TransformFinalBlock(new byte[0], 0, 0);
                var sha = BitConverter.ToString(canonicalHash.Hash!).Replace("-", "").ToLowerInvariant();
                var entry = new { root = active, originalSha256 = rawSha, canonicalSha256 = sha, tokens = tokenCount };
                if (Admit(entry)) digests.Add(entry);
            }
            canonicalHash?.Dispose(); canonicalHash = null;
            active = null; canonical.Clear(); pendingId = -1;
        }
        private void Part(string value)
        {
            canonical.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
            if (canonical.Length >= 65536) Flush();
        }
        private void Flush()
        {
            if (canonical.Length == 0) return;
            var bytes = Encoding.UTF8.GetBytes(canonical.ToString());
            canonicalHash!.TransformBlock(bytes, 0, bytes.Length, bytes, 0); canonical.Clear();
        }
        public void Dispose() { canonicalHash?.Dispose(); canonicalHash = null; canonical.Clear(); }
        private void Target(int id)
        {
            var d = definitions[id]; Part(rootNames[d.Root]); Part(d.Ordinal.ToString(CultureInfo.InvariantCulture));
        }
        internal void Reference(int id, string root, string? selectedPath, bool first)
        {
            if (incomplete != null) return;
            if (first) {
                if (id != definitions.Count || id >= 1048576) { incomplete = "definition-budget-or-order"; return; }
                if (!roots.TryGetValue(root, out var index)) {
                    if (rootCharacters + root.Length > 8*1024*1024 || roots.Count >= 131072) { incomplete = "first-root-budget"; return; }
                    index = rootNames.Count; roots.Add(root, index); rootNames.Add(root); rootCounts.Add(0); rootCharacters += root.Length;
                }
                definitions.Add(new Definition { Root = index, Ordinal = rootCounts[index] }); rootCounts[index]++;
            }
            if (active == null || selectedPath == null) return;
            if (id < 0 || id >= definitions.Count) { incomplete = "definition-unavailable"; return; }
            var origin = definitions[id];
            var edge = new { kind = first ? "define" : "reference", root, path = selectedPath, originalId = id,
                firstRoot = rootNames[origin.Root], definitionOrdinal = origin.Ordinal };
            if (events.Count >= 32768 || !Admit(edge)) { incomplete = "selected-event-budget"; return; }
            events.Add(edge);
            Part(first ? "define" : "reference"); Part(selectedPath); Target(id); pendingId = id;
        }
        private bool Admit(object value)
        {
            if (incomplete != null) return false;
            var bytes = Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(value)) + 1;
            // Reserve the fixed snapshot keys, counters, rule and failure reason.
            if (eventCharacters + bytes > 2*1024*1024 - 2048) { incomplete = "selected-event-budget"; return false; }
            eventCharacters += bytes; return true;
        }
        internal void Token(string root, string path, string token)
        {
            if (incomplete != null || active != root) return;
            // Only an immediately observed reference may substitute an ID.
            // A string value spelling ref:123 remains a literal token.
            string? prefix = null;
            if (pendingId >= 0) {
                var digits = pendingId.ToString(CultureInfo.InvariantCulture);
                if (token == "ref:" + digits) prefix = "ref:";
                else if (token.EndsWith(":shared:" + digits, StringComparison.Ordinal)) prefix = token.Substring(0, token.Length - digits.Length);
            }
            Part(prefix == null ? "token" : "reference-token");
            Part(path);
            if (prefix == null) Part(token); else { Part(prefix); Target(pendingId); }
            tokenCount++; pendingId = -1;
        }
        private string? Incomplete => incomplete ?? (active != null ? "unfinished-selected-root" : selectedRoots.Count == 0 ? "selected-root-absent" : null);
        internal object Summary() => new { complete = Incomplete == null, incomplete = Incomplete,
            definitionCount = definitions.Count, firstRootCount = rootNames.Count, selectedEvents = events.Count, selectedRootCount = selectedRoots.Count };
        internal object Snapshot() => new { schema = "marker-shared-relations-canonical-v4", diagnosticOnly = true,
            complete = Incomplete == null, incomplete = Incomplete, definitionCount = definitions.Count, firstRootCount = rootNames.Count,
            selectedRoots = selectedRoots.ToArray(), digests = digests.ToArray(), events = events.ToArray(),
            identityRule = "first definition root and definition ordinal within that root; finite selected relations only; external content remains unknown", completeStateCoverage = false };
    }
}
