using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Equality of observed data is separate from closed state coverage. A
    // matching diagnostic digest with omissions never qualifies a checkpoint.
    internal sealed class BoundaryDigestComparison
    {
        public readonly bool ObservedEqual, CompleteStateCoverage;
        public readonly string? FirstDifference;
        public readonly string[] OpenDomains;

        private BoundaryDigestComparison(bool equal, string? firstDifference, string[] openDomains)
        {
            ObservedEqual = equal; FirstDifference = firstDifference; OpenDomains = openDomains;
            CompleteStateCoverage = false;
        }

        internal static BoundaryDigestComparison Compare(BoundaryDigestSnapshot expected, BoundaryDigestSnapshot actual)
        {
            if (expected.Schema != 2 || actual.Schema != 2)
                throw new InvalidOperationException("Unsupported boundary digest schema.");
            if (expected.RecomputeSha256() != expected.FullSha256 || actual.RecomputeSha256() != actual.FullSha256)
                throw new InvalidOperationException("Boundary digest data changed after capture.");
            var left = expected.ProofBody(); var right = actual.ProofBody();
            string? difference = null;
            // Prefer an owning root/native queue path over an aggregate hash.
            foreach (var name in new[] { "roots", "nativeScheduler", "nativePhysics", "groups", "unsupported", "canonicalWaitIssues" })
            {
                difference = First(left[name], right[name], name);
                if (difference != null) break;
            }
            var gaps = expected.Unsupported.Concat(actual.Unsupported)
                .Concat(expected.CanonicalWaitIssues).Concat(actual.CanonicalWaitIssues)
                .Concat(new[] { "boundary-state-coverage-open", "closed-D-field-registry" })
                .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            return new BoundaryDigestComparison(difference == null, difference, gaps);
        }

        private static string? First(JToken? left, JToken? right, string path)
        {
            if (left == null || right == null) return left == right ? null : path;
            if (left.Type != right.Type) return path;
            if (left is JObject leftObject && right is JObject rightObject)
            {
                foreach (var name in leftObject.Properties().Select(property => property.Name)
                    .Concat(rightObject.Properties().Select(property => property.Name)).Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal))
                {
                    var difference = First(leftObject[name], rightObject[name], path + "." + name);
                    if (difference != null) return difference;
                }
                return null;
            }
            if (left is JArray leftArray && right is JArray rightArray)
            {
                var repeated = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var index = 0; index < Math.Min(leftArray.Count, rightArray.Count); index++)
                {
                    var itemPath = path + "[" + index + "]";
                    if (path == "roots" && leftArray[index] is JObject root && root["id"]?.Type == JTokenType.String)
                    {
                        var id = (string)root["id"]!;
                        repeated.TryGetValue(id, out var occurrence); repeated[id] = occurrence + 1;
                        itemPath = "root:" + id + "[record=" + occurrence + "]";
                    }
                    var difference = First(leftArray[index], rightArray[index], itemPath);
                    if (difference != null) return difference;
                }
                return leftArray.Count == rightArray.Count ? null : path + ".count";
            }
            return JToken.DeepEquals(left, right) ? null : path;
        }
    }
}
