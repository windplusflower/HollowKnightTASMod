using System;

namespace HollowKnightTAS.Core.Movie
{
    // Also compiled into the standalone injector so launch receipts and
    // managed replay/recording use the same versioned execution contract.
    public static class NativeExecutionProfile
    {
        public const string ProfileId = "hktas-unity-input-playerloop-canonical-cinematics-2026-v8";
        public const string LegacyProfileId = "hktas-unity-input-playerloop-load-elision-scene-rng-2026-v3";
        public const string CanonicalStartupProfileId =
            "hktas-unity-input-playerloop-canonical-startup-clock-2026-v4-experimental";
        public const string CanonicalModClockProfileId =
            "hktas-unity-input-playerloop-canonical-startup-mod-wall-clock-2026-v5-experimental";
        public const string SerialSceneProfileId =
            "hktas-unity-input-playerloop-serial-scene-load-2026-v6-experimental";

        public static string SelectedProfileId => Select(
            Environment.GetEnvironmentVariable("HKTAS_LOADING_CANONICAL_STARTUP_CLOCK") == "1",
            Environment.GetEnvironmentVariable("HKTAS_LOADING_SYNCHRONOUS_FETCH") == "1",
            Environment.GetEnvironmentVariable("HKTAS_LOADING_DISABLE_LEGACY_CALIBRATION") == "1",
            Environment.GetEnvironmentVariable("HKTAS_LOADING_MOD_WALL_CLOCK") == "1",
            Environment.GetEnvironmentVariable("HKTAS_LOADING_SERIAL_SCENES") == "1");

        public static string Select(bool canonicalStartupClock,
            bool synchronousFetch, bool disableLegacyCalibration, bool modWallClock = false, bool serialScenes = false)
        {
            if (serialScenes && !modWallClock)
                throw new InvalidOperationException("Serial scene loading requires the complete Mod clock policy.");
            if (modWallClock && !canonicalStartupClock)
                throw new InvalidOperationException("Mod wall clocks require canonical startup clocks.");
            if (!canonicalStartupClock) return ProfileId;
            if (!synchronousFetch || !disableLegacyCalibration)
                throw new InvalidOperationException(
                    "Canonical startup clocks require synchronous scene fetch and disabled legacy calibration.");
            return serialScenes ? SerialSceneProfileId : modWallClock ? CanonicalModClockProfileId : CanonicalStartupProfileId;
        }

        public static bool IsCanonicalStartupProfile(string profile)
            => profile == CanonicalStartupProfileId || UsesModWallClock(profile);

        public static bool UsesModWallClock(string profile)
            => profile == CanonicalModClockProfileId || profile == SerialSceneProfileId;
    }
}
