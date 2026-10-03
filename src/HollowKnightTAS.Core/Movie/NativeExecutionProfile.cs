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
            "hktas-unity-input-playerloop-canonical-startup-cinematics-2026-v9-experimental";
        public const string CanonicalModClockProfileId =
            "hktas-unity-input-playerloop-canonical-cinematics-mod-wall-clock-2026-v9-experimental";
        public const string SerialSceneProfileId =
            "hktas-unity-input-playerloop-serial-scene-cinematics-2026-v9-experimental";
        public const string VirtualAudioSerialProfileId =
            "hktas-unity-input-playerloop-serial-scene-cinematics-audio-lifetime-2026-v9-experimental";
        public const string IsolatedOnlineProfileId =
            "hktas-unity-input-playerloop-serial-scene-isolated-online-2026-v10-experimental";

        public static string SelectedProfileId => Select(
            Environment.GetEnvironmentVariable("HKTAS_LOADING_CANONICAL_STARTUP_CLOCK") == "1",
            Environment.GetEnvironmentVariable("HKTAS_LOADING_SYNCHRONOUS_FETCH") == "1",
            Environment.GetEnvironmentVariable("HKTAS_LOADING_DISABLE_LEGACY_CALIBRATION") == "1",
            Environment.GetEnvironmentVariable("HKTAS_LOADING_MOD_WALL_CLOCK") == "1",
            Environment.GetEnvironmentVariable("HKTAS_LOADING_SERIAL_SCENES") == "1",
            Environment.GetEnvironmentVariable("HKTAS_LOADING_VIRTUAL_AUDIO") == "1",
            Environment.GetEnvironmentVariable("HKTAS_LOADING_ISOLATED_ONLINE") == "1");

        public static string Select(bool canonicalStartupClock,
            bool synchronousFetch, bool disableLegacyCalibration, bool modWallClock = false, bool serialScenes = false,
            bool virtualAudio = false, bool isolatedOnline = false)
        {
            if (isolatedOnline && !virtualAudio)
                throw new InvalidOperationException("Isolated online state requires the complete virtual audio profile.");
            if (virtualAudio && !serialScenes)
                throw new InvalidOperationException("Virtual pooled audio requires serial scene loading.");
            if (serialScenes && !modWallClock)
                throw new InvalidOperationException("Serial scene loading requires the complete Mod clock policy.");
            if (modWallClock && !canonicalStartupClock)
                throw new InvalidOperationException("Mod wall clocks require canonical startup clocks.");
            if (!canonicalStartupClock) return ProfileId;
            if (!synchronousFetch || !disableLegacyCalibration)
                throw new InvalidOperationException(
                    "Canonical startup clocks require synchronous scene fetch and disabled legacy calibration.");
            return isolatedOnline ? IsolatedOnlineProfileId : virtualAudio ? VirtualAudioSerialProfileId : serialScenes ? SerialSceneProfileId
                : modWallClock ? CanonicalModClockProfileId : CanonicalStartupProfileId;
        }

        public static bool IsCanonicalStartupProfile(string profile)
            => profile == CanonicalStartupProfileId || UsesModWallClock(profile);

        public static bool UsesModWallClock(string profile)
            => profile == CanonicalModClockProfileId || UsesSerialScenes(profile);

        public static bool UsesSerialScenes(string profile)
            => profile == SerialSceneProfileId || UsesVirtualAudio(profile);

        public static bool UsesVirtualAudio(string profile)
            => profile == VirtualAudioSerialProfileId || profile == IsolatedOnlineProfileId;
    }
}
