using System;
using System.Collections.Generic;

namespace HollowKnightTAS.Core.Movie
{
    public sealed class MovieV2RuntimeCapabilities
    {
        public MovieV2RuntimeCapabilities(string nativeProfileId, string actionSchemaId,
            bool nativeGateActive, bool saveGuardArmed, bool mouseBridgeReady,
            int viewportWidth, int viewportHeight, string environmentSha256,
            string gameVersion, string apiVersion, string modVersion)
        {
            NativeProfileId = nativeProfileId ?? throw new ArgumentNullException(nameof(nativeProfileId));
            ActionSchemaId = actionSchemaId ?? throw new ArgumentNullException(nameof(actionSchemaId));
            NativeGateActive = nativeGateActive;
            SaveGuardArmed = saveGuardArmed;
            MouseBridgeReady = mouseBridgeReady;
            ViewportWidth = viewportWidth;
            ViewportHeight = viewportHeight;
            EnvironmentSha256 = environmentSha256 ?? throw new ArgumentNullException(nameof(environmentSha256));
            GameVersion = gameVersion ?? throw new ArgumentNullException(nameof(gameVersion));
            ApiVersion = apiVersion ?? throw new ArgumentNullException(nameof(apiVersion));
            ModVersion = modVersion ?? throw new ArgumentNullException(nameof(modVersion));
        }
        public string NativeProfileId { get; }
        public string ActionSchemaId { get; }
        public bool NativeGateActive { get; }
        public bool SaveGuardArmed { get; }
        public bool MouseBridgeReady { get; }
        public int ViewportWidth { get; }
        public int ViewportHeight { get; }
        public string EnvironmentSha256 { get; }
        public string GameVersion { get; }
        public string ApiVersion { get; }
        public string ModVersion { get; }
    }

    public sealed class MovieV2CompatibilityReport
    {
        private readonly IReadOnlyList<MovieDiagnostic> errors;
        private readonly IReadOnlyList<MovieDiagnostic> warnings;
        internal MovieV2CompatibilityReport(IEnumerable<MovieDiagnostic> errors,
            IEnumerable<MovieDiagnostic> warnings)
        {
            this.errors = Array.AsReadOnly(new List<MovieDiagnostic>(errors).ToArray());
            this.warnings = Array.AsReadOnly(new List<MovieDiagnostic>(warnings).ToArray());
        }
        public IReadOnlyList<MovieDiagnostic> Errors => errors;
        public IReadOnlyList<MovieDiagnostic> Warnings => warnings;
        public bool Allowed => errors.Count == 0;
    }

    public sealed class MovieV2Compatibility
    {
        public MovieV2CompatibilityReport Evaluate(MovieV2Header header, MovieV2RuntimeCapabilities runtime)
        {
            if (header == null) throw new ArgumentNullException(nameof(header));
            if (runtime == null) throw new ArgumentNullException(nameof(runtime));
            var errors = new List<MovieDiagnostic>();
            var warnings = new List<MovieDiagnostic>();
            if (header.Version != MovieProtocolV2.Version || header.TickUnit != MovieProtocolV2.TickUnit)
                Add(errors, MovieDiagnosticCodes.UnsupportedVersion, "Unsupported v2 protocol or tick unit.");
            if (header.ActionSchemaId != MovieProtocolV2.ActionSchemaId
                || runtime.ActionSchemaId != MovieProtocolV2.ActionSchemaId)
                Add(errors, MovieDiagnosticCodes.UnknownAction, "V2 action schema is unavailable or differs.");
            if (Unknown(header.NativeProfileId) || Unknown(runtime.NativeProfileId))
                Add(errors, MovieDiagnosticCodes.InvalidIdentifier, "Native profile metadata is unavailable.");
            else if (!string.Equals(header.NativeProfileId, runtime.NativeProfileId, StringComparison.Ordinal))
            {
                if (NativeExecutionProfile.IsExperimentalProfile(header.NativeProfileId)
                    || NativeExecutionProfile.IsExperimentalProfile(runtime.NativeProfileId))
                    Add(errors, MovieDiagnosticCodes.InvalidIdentifier,
                        "Experimental execution profiles must match the recording exactly.");
                else Add(warnings, MovieDiagnosticCodes.InvalidIdentifier,
                    "Execution rules differ from the recording. Playback is allowed; some input frames may need adjustment.");
            }
            if (!runtime.NativeGateActive)
                Add(errors, MovieDiagnosticCodes.InvalidCommand, "Native full-run frame gate is inactive.");
            if (!runtime.SaveGuardArmed)
                Add(errors, MovieDiagnosticCodes.InvalidCommand, "Original save write guard is not armed.");
            if (header.MouseEnabled && !runtime.MouseBridgeReady)
                Add(errors, MovieDiagnosticCodes.InvalidCommand, "Game mouse input bridge is unavailable.");
            if (header.MouseEnabled && (header.ViewportWidth != runtime.ViewportWidth
                                        || header.ViewportHeight != runtime.ViewportHeight))
                Add(errors, MovieDiagnosticCodes.InvalidHeaderValue, "Mouse-enabled replay requires the recorded viewport dimensions.");
            else if (!header.MouseEnabled && (header.ViewportWidth != runtime.ViewportWidth
                                              || header.ViewportHeight != runtime.ViewportHeight))
                Add(warnings, MovieDiagnosticCodes.InvalidHeaderValue, "Viewport dimensions differ from the recording.");

            if (Unknown(header.GameVersion) || Unknown(header.ApiVersion) || Unknown(header.ModVersion)
                || header.EnvironmentSha256 == "none")
                Add(errors, MovieDiagnosticCodes.InvalidHeaderValue, "Unresolved draft metadata cannot be replayed.");
            WarnVersion(warnings, "Game", header.GameVersion, runtime.GameVersion);
            WarnVersion(warnings, "Modding API", header.ApiVersion, runtime.ApiVersion);
            WarnVersion(warnings, "HollowKnightTAS Mod", header.ModVersion, runtime.ModVersion);
            if (Unknown(runtime.EnvironmentSha256) || runtime.EnvironmentSha256 == "none")
                Add(warnings, MovieDiagnosticCodes.ManifestMismatch, "Current environment identity is unavailable.");
            else if (header.EnvironmentSha256 != "none"
                     && !string.Equals(header.EnvironmentSha256, runtime.EnvironmentSha256, StringComparison.Ordinal))
                Add(warnings, MovieDiagnosticCodes.ManifestMismatch, "Environment identity differs from the recording.");
            return new MovieV2CompatibilityReport(errors, warnings);
        }

        private static bool Unknown(string value)
            => string.IsNullOrWhiteSpace(value) || value == "unknown";

        private static void WarnVersion(ICollection<MovieDiagnostic> warnings, string name,
            string recorded, string current)
        {
            if (Unknown(current)) Add(warnings, MovieDiagnosticCodes.InvalidHeaderValue,
                name + " version comparison is unavailable.");
            else if (!Unknown(recorded) && !string.Equals(recorded, current, StringComparison.Ordinal))
                Add(warnings, MovieDiagnosticCodes.InvalidHeaderValue,
                    name + " version differs from the recording.");
        }

        private static void Add(ICollection<MovieDiagnostic> diagnostics, string code, string message)
            => diagnostics.Add(new MovieDiagnostic(code,
                new MovieSourceSpan("<v2-compatibility>", 1, 1, 1), message,
                "Inspect the recorded and current full-run capabilities."));
    }
}
