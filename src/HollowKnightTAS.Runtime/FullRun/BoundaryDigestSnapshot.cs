using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Bounded diagnostic proof data. Coverage cannot be inferred from a hash:
    // all known omissions travel alongside the captured groups and root paths.
    internal sealed class BoundaryDigestSnapshot
    {
        public readonly int Schema = 2;
        public readonly bool CompleteStateCoverage = false;
        public readonly SortedDictionary<string, string> Groups;
        public readonly object[] Roots;
        public readonly string[] Unsupported;
        public readonly object[] NativeScheduler, NativePhysics;
        public readonly string[] CanonicalWaitIssues;
        public readonly string FullSha256;
        internal BoundaryDigestSnapshot(SortedDictionary<string, string> groups, object[] roots, string[] unsupported,
            object[] nativeScheduler, object[] nativePhysics, string[] canonicalWaitIssues)
        {
            if (roots.Length > 200000) throw new InvalidOperationException("Boundary digest root budget exceeded.");
            Groups = new SortedDictionary<string, string>(groups, StringComparer.Ordinal);
            Roots = CopyRecords(roots); Unsupported = unsupported.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            NativeScheduler = CopyRecords(nativeScheduler); NativePhysics = CopyRecords(nativePhysics);
            CanonicalWaitIssues = canonicalWaitIssues.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            FullSha256 = RecomputeSha256();
        }

        private static object[] CopyRecords(object[] values) => JArray.FromObject(values).Select(value => (object)value).ToArray();

        internal JObject ProofBody() => JObject.FromObject(new { groups = Groups, roots = Roots, unsupported = Unsupported,
            nativeScheduler = NativeScheduler, nativePhysics = NativePhysics, canonicalWaitIssues = CanonicalWaitIssues });

        internal string RecomputeSha256()
        {
            var bytes = Encoding.UTF8.GetBytes(ProofBody().ToString(Formatting.None));
            if (bytes.Length > 16 * 1024 * 1024) throw new InvalidOperationException("Boundary digest exceeds 16 MiB.");
            using var hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
