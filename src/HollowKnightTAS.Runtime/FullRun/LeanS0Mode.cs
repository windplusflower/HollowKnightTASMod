using System;
using System.Globalization;

namespace HollowKnightTAS.Runtime.FullRun
{
    /// <summary>
    /// Reads runtime configuration flags for the Lean S0 scene-boundary snapshot pipeline.
    /// </summary>
    internal static class LeanS0Mode
    {
        internal const string EnabledEnvVar = "HKTAS_LEAN_S0";
        internal const string CaptureOrdinalEnvVar = "HKTAS_LEAN_S0_CAPTURE_ORDINAL";
        internal const string RoleEnvVar = "HKTAS_LEAN_S0_ROLE";
        internal const string SuffixToEndEnvVar = "HKTAS_LEAN_S0_SUFFIX_TO_END";
        internal const int MaxPacketBytes = 32 * 1024 * 1024;
        internal const int DefaultSuffixFrames = 32;

        internal static bool Enabled
            => string.Equals(Environment.GetEnvironmentVariable(EnabledEnvVar), "1", StringComparison.Ordinal);

        internal static string Role
            => (Environment.GetEnvironmentVariable(RoleEnvVar) ?? string.Empty).Trim().ToLowerInvariant();

        internal static bool IsSourceRole => Enabled && Role == "source";

        internal static bool IsTargetRole => Enabled && Role == "target";

        internal static bool SuffixToEnd
            => string.Equals(Environment.GetEnvironmentVariable(SuffixToEndEnvVar), "1", StringComparison.Ordinal);

        internal static int SelectedCaptureOrdinal
        {
            get
            {
                string? raw = Environment.GetEnvironmentVariable(CaptureOrdinalEnvVar);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return 0;
                }
                if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 1 || value > 10000)
                {
                    throw new InvalidOperationException("Lean S0 capture ordinal is invalid.");
                }
                return value;
            }
        }
    }
}
