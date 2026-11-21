using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.Core.SceneCheckpoints
{
    public static class SceneCheckpointEnvironmentPolicy
    {
        public static void ValidateMods(string nativeProfileId, IEnumerable<string> availableMods)
        {
            if (!NativeExecutionProfile.UsesCanonicalBoundary(nativeProfileId)) return;
            if (availableMods == null)
            {
                Trace.TraceWarning("[HKTAS] SceneCheckpointEnvironmentPolicy.ValidateMods received a null mod list; allowing execution.");
                return;
            }

            var mods = availableMods.ToArray();
            if (mods.Length > 256 || mods.Any(string.IsNullOrWhiteSpace))
            {
                Trace.TraceWarning("[HKTAS] Canonical boundary Mod inventory exceeds 256 entries or contains blank names; allowing execution.");
            }

            var external = mods
                .Where(name => !string.IsNullOrWhiteSpace(name) && !string.Equals(name, "HollowKnightTAS", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            if (external.Length != 0)
            {
                Trace.TraceWarning(
                    "[HKTAS] Canonical boundary detected other active Mods; allowing execution: "
                    + string.Join(", ", external));
            }
        }
    }
}
