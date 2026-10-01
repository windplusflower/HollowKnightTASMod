using System;
using System.Collections.Generic;

namespace HollowKnightTAS.Core.Movie
{
    public enum GameInputChannel
    {
        Hero,
        PreMenu,
        Binder,
        MouseInControl,
        MouseHollowKnight,
        CustomKey
    }

    public static class MovieProtocolV2
    {
        public const int Version = 2;
        public const string Format = "hktas";
        public const string TickUnit = "input-playerloop";
        public const string ActionSchemaId = "hktas-full-run-actions-v2";
        public const string NativeProfileId = NativeExecutionProfile.ProfileId;
        public const string CanonicalStartupNativeProfileId = NativeExecutionProfile.CanonicalStartupProfileId;
        public const string CanonicalModClockNativeProfileId = NativeExecutionProfile.CanonicalModClockProfileId;

        // Keep the published default profile. The opt-in P0 execution
        // policy must be visible in recordings, capability receipts and replay.
        public static string SelectedNativeProfileId => NativeExecutionProfile.SelectedProfileId;

        public static string SelectNativeProfile(bool canonicalStartupClock,
            bool synchronousFetch, bool disableLegacyCalibration, bool modWallClock = false)
            => NativeExecutionProfile.Select(canonicalStartupClock, synchronousFetch, disableLegacyCalibration, modWallClock);
        public const string RandomSynchronizationPolicyId =
            "scene-input-boundary-seed-render-isolation-v1";
        public const int MaximumSourceUtf8Bytes = 16 * 1024 * 1024;
        public const int MaximumLineCharacters = 64 * 1024;
        public const long MaximumExpandedFrames = 10_000_000;
        public const int MaximumSamplesPerFrame = 256;
        public const int MaximumJsonNodesPerLine = 16384;
        public const int MaximumJsonDepth = 8;

        private static readonly IReadOnlyList<string> HeroActionNamesValue = Array.AsReadOnly(new[]
        {
            "left", "right", "up", "down",
            "rightStickLeft", "rightStickRight", "rightStickUp", "rightStickDown",
            "menuSubmit", "menuCancel", "jump", "evade", "dash", "superDash",
            "dreamNail", "attack", "cast", "focus", "quickMap", "quickCast",
            "textSpeedup", "skipCutscene", "openInventory", "paneRight", "paneLeft", "pause"
        });

        private static readonly IReadOnlyList<string> MenuActionNamesValue = Array.AsReadOnly(new[]
        {
            "submit", "cancel", "left", "right", "up", "down"
        });

        public static IReadOnlyList<string> HeroActionNames => HeroActionNamesValue;
        public static IReadOnlyList<string> PreMenuActionNames => MenuActionNamesValue;
        public static IReadOnlyList<string> BinderActionNames => MenuActionNamesValue;

        public static int ExpectedValueCount(GameInputChannel channel)
        {
            switch (channel)
            {
                case GameInputChannel.CustomKey: return 2;
                case GameInputChannel.Hero:
                    return HeroActionNamesValue.Count;
                case GameInputChannel.PreMenu:
                case GameInputChannel.Binder:
                    return MenuActionNamesValue.Count;
                case GameInputChannel.MouseInControl:
                case GameInputChannel.MouseHollowKnight:
                    return 0;
                default:
                    throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown v2 input channel.");
            }
        }

        public static string GetChannelName(GameInputChannel channel)
        {
            switch (channel)
            {
                case GameInputChannel.CustomKey: return "customKey";
                case GameInputChannel.Hero: return "hero";
                case GameInputChannel.PreMenu: return "preMenu";
                case GameInputChannel.Binder: return "binder";
                case GameInputChannel.MouseInControl: return "mouseInControl";
                case GameInputChannel.MouseHollowKnight: return "mouseHollowKnight";
                default:
                    throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown v2 input channel.");
            }
        }

        public static bool TryParseChannel(string value, out GameInputChannel channel)
        {
            foreach (GameInputChannel candidate in Enum.GetValues(typeof(GameInputChannel)))
            {
                if (string.Equals(value, GetChannelName(candidate), StringComparison.Ordinal))
                {
                    channel = candidate;
                    return true;
                }
            }

            channel = default;
            return false;
        }

        public static bool IsMouseChannel(GameInputChannel channel)
        {
            return channel == GameInputChannel.MouseInControl
                   || channel == GameInputChannel.MouseHollowKnight;
        }
    }
}
