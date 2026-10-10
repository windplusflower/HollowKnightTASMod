using System;
using System.Collections.Generic;
using System.Linq;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.Core.SceneCheckpoints
{
    public static class SceneCheckpointEnvironmentPolicy
    {
        // The first implementation excludes external Mods. Historical replay
        // retains its existing environment contract.
        public static void ValidateMods(string nativeProfileId, IEnumerable<string> availableMods)
        {
            if (!NativeExecutionProfile.UsesCanonicalBoundary(nativeProfileId)) return;
            if (availableMods == null) throw new ArgumentNullException(nameof(availableMods));
            var mods = availableMods.Take(257).ToArray();
            if (mods.Length > 256 || mods.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException("Canonical boundary Mod inventory is invalid.");
            var external = mods.Where(name => name != "HollowKnightTAS").Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal).ToArray();
            if (external.Length != 0)
                throw new InvalidOperationException("Canonical boundary currently supports only the original game, Modding API and HollowKnightTAS. Remove other Mods from the active Mod directory: "
                    + string.Join(", ", external));
        }
    }
}
