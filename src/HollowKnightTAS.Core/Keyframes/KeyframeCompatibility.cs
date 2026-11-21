using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.Core.Keyframes
{
    public enum KeyframeCompatibilityStatus : byte
    {
        Compatible = 1,
        ReplayOnly = 2,
        Incompatible = 3,
        UnsupportedAdapter = 4
    }

    public sealed class KeyframeCompatibilityContext
    {
        public KeyframeCompatibilityContext(
            KeyframeSupportTier maximumTier,
            long targetMovieTick,
            string gameBuildSha256,
            string manifestSha256,
            string baselineObjectSha256,
            IEnumerable<string> compatibleJournalHeadSha256s,
            string adapterManifestSha256,
            KeyframeAdapterManifest adapterManifest,
            bool hasUnknownRuntimeMods)
        {
            if (!Enum.IsDefined(
                    typeof(KeyframeSupportTier),
                    maximumTier))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumTier));
            }
            if (targetMovieTick < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(targetMovieTick));
            }

            RequireSha(gameBuildSha256, nameof(gameBuildSha256));
            RequireSha(manifestSha256, nameof(manifestSha256));
            RequireSha(
                baselineObjectSha256,
                nameof(baselineObjectSha256));
            RequireSha(
                adapterManifestSha256,
                nameof(adapterManifestSha256));
            var journalHeads = (
                    compatibleJournalHeadSha256s
                    ?? throw new ArgumentNullException(
                        nameof(compatibleJournalHeadSha256s)))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (journalHeads.Length == 0)
            {
                throw new ArgumentException(
                    "At least one compatible journal head is required.",
                    nameof(compatibleJournalHeadSha256s));
            }
            foreach (var journalHead in journalHeads)
            {
                RequireSha(
                    journalHead,
                    nameof(compatibleJournalHeadSha256s));
            }

            MaximumTier = maximumTier;
            TargetMovieTick = targetMovieTick;
            GameBuildSha256 = gameBuildSha256;
            ManifestSha256 = manifestSha256;
            BaselineObjectSha256 = baselineObjectSha256;
            CompatibleJournalHeadSha256s =
                new ReadOnlyCollection<string>(journalHeads);
            AdapterManifestSha256 = adapterManifestSha256;
            AdapterManifest = adapterManifest
                              ?? throw new ArgumentNullException(
                                  nameof(adapterManifest));
            HasUnknownRuntimeMods = hasUnknownRuntimeMods;
        }

        public KeyframeSupportTier MaximumTier { get; }
        public long TargetMovieTick { get; }
        public string GameBuildSha256 { get; }
        public string ManifestSha256 { get; }
        public string BaselineObjectSha256 { get; }
        public IReadOnlyList<string> CompatibleJournalHeadSha256s
        {
            get;
        }
        public string AdapterManifestSha256 { get; }
        public KeyframeAdapterManifest AdapterManifest { get; }
        public bool HasUnknownRuntimeMods { get; }

        private static void RequireSha(string value, string name)
        {
            if (!MovieProtocolV1.IsLowerSha256(value))
            {
                throw new ArgumentException(
                    "A lowercase SHA-256 is required.",
                    name);
            }
        }
    }

    public sealed class KeyframeCompatibilityResult
    {
        public KeyframeCompatibilityResult(
            KeyframeCompatibilityStatus status,
            string reasonCode,
            string detail,
            IEnumerable<string>? ignoredOptionalAdapters = null)
        {
            if (!Enum.IsDefined(
                    typeof(KeyframeCompatibilityStatus),
                    status))
            {
                throw new ArgumentOutOfRangeException(nameof(status));
            }
            ReasonCode = KeyframeAdapterEnvelope.RequireId(
                reasonCode,
                nameof(reasonCode));
            Status = status;
            Detail = detail ?? string.Empty;
            IgnoredOptionalAdapters =
                new ReadOnlyCollection<string>(
                    (ignoredOptionalAdapters
                     ?? Array.Empty<string>())
                    .Select(
                        value =>
                            KeyframeAdapterEnvelope.RequireId(
                                value,
                                nameof(ignoredOptionalAdapters)))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray());
        }

        public KeyframeCompatibilityStatus Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public IReadOnlyList<string> IgnoredOptionalAdapters { get; }
        public bool IsCompatible =>
            Status == KeyframeCompatibilityStatus.Compatible;
    }

    public static class KeyframeCompatibility
    {
        public static KeyframeCompatibilityResult Evaluate(
            SemanticKeyframeDescriptor descriptor,
            KeyframeCompatibilityContext context)
        {
            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (context.MaximumTier == KeyframeSupportTier.ReplayOnly)
            {
                return Result(
                    KeyframeCompatibilityStatus.ReplayOnly,
                    "runtime-replay-only",
                    "The current runtime has no verified keyframe gate.");
            }
            if (context.HasUnknownRuntimeMods)
            {
                Trace.TraceWarning("KeyframeCompatibility: unregistered runtime mod present; allowing evaluation.");
            }
            if (descriptor.Status != KeyframeStatus.Ready)
            {
                return Result(
                    KeyframeCompatibilityStatus.Incompatible,
                    "descriptor-not-ready",
                    "The keyframe descriptor is not Ready.");
            }
            if (descriptor.Tier > context.MaximumTier)
            {
                return Result(
                    KeyframeCompatibilityStatus.Incompatible,
                    "tier-not-supported",
                    "The keyframe tier exceeds the runtime tier.");
            }
            if (descriptor.CaptureMovieTick
                > context.TargetMovieTick)
            {
                return Result(
                    KeyframeCompatibilityStatus.Incompatible,
                    "keyframe-after-target",
                    "The keyframe tick is after the target tick.");
            }

            var environmentFields = new[]
            {
                Pair(
                    "game-build-mismatch",
                    descriptor.GameBuildSha256,
                    context.GameBuildSha256),
                Pair(
                    "manifest-mismatch",
                    descriptor.ManifestSha256,
                    context.ManifestSha256),
                Pair(
                    "adapter-manifest-mismatch",
                    descriptor.AdapterManifestSha256,
                    context.AdapterManifestSha256)
            };
            foreach (var field in environmentFields)
            {
                if (!string.Equals(
                        field.Actual,
                        field.Expected,
                        StringComparison.Ordinal))
                {
                    Trace.TraceWarning(
                        $"KeyframeCompatibility: {field.Reason} ({field.Actual} != {field.Expected}); continuing anyway.");
                }
            }
            if (!string.Equals(
                    descriptor.BaselineObjectSha256,
                    context.BaselineObjectSha256,
                    StringComparison.Ordinal))
            {
                return Result(
                    KeyframeCompatibilityStatus.Incompatible,
                    "baseline-mismatch",
                    "A compatibility-bound baseline hash differs.");
            }
            if (!context.CompatibleJournalHeadSha256s.Contains(
                    descriptor.JournalHeadSha256,
                    StringComparer.Ordinal))
            {
                return Result(
                    KeyframeCompatibilityStatus.Incompatible,
                    "journal-chain-mismatch",
                    "The keyframe journal head is not an ancestor of the target.");
            }

            var runtimeAdapters = context.AdapterManifest.Adapters
                .ToDictionary(
                    value => value.AdapterId,
                    StringComparer.Ordinal);
            var descriptorAdapters = descriptor.Adapters
                .ToDictionary(
                    value => value.AdapterId,
                    StringComparer.Ordinal);
            foreach (var required in runtimeAdapters.Values.Where(
                         value => value.IsRequired
                                  && value.MinimumTier
                                  <= descriptor.Tier))
            {
                if (!descriptorAdapters.ContainsKey(required.AdapterId))
                {
                    return Result(
                        KeyframeCompatibilityStatus.UnsupportedAdapter,
                        "required-adapter-missing",
                        "A required runtime adapter is absent.");
                }
            }

            var ignored = new List<string>();
            foreach (var envelope in descriptor.Adapters)
            {
                if (!runtimeAdapters.TryGetValue(
                        envelope.AdapterId,
                        out var runtime))
                {
                    if (envelope.IsRequired)
                    {
                        return Result(
                            KeyframeCompatibilityStatus.UnsupportedAdapter,
                            "unknown-required-adapter",
                            "The descriptor requires an unknown adapter.");
                    }
                    ignored.Add(envelope.AdapterId);
                    continue;
                }
                if (runtime.SchemaVersion != envelope.SchemaVersion
                    || runtime.IsRequired != envelope.IsRequired
                    || runtime.MinimumTier > descriptor.Tier)
                {
                    return Result(
                        KeyframeCompatibilityStatus.UnsupportedAdapter,
                        "adapter-contract-mismatch",
                        "An adapter schema, requirement, or tier differs.");
                }
            }

            return new KeyframeCompatibilityResult(
                KeyframeCompatibilityStatus.Compatible,
                "compatible",
                "All compatibility bindings match.",
                ignored);
        }

        private static KeyframeCompatibilityResult Result(
            KeyframeCompatibilityStatus status,
            string reason,
            string detail)
        {
            return new KeyframeCompatibilityResult(
                status,
                reason,
                detail);
        }

        private static CompatibilityPair Pair(
            string reason,
            string actual,
            string expected)
        {
            return new CompatibilityPair(reason, actual, expected);
        }

        private sealed class CompatibilityPair
        {
            public CompatibilityPair(
                string reason,
                string actual,
                string expected)
            {
                Reason = reason;
                Actual = actual;
                Expected = expected;
            }

            public string Reason { get; }
            public string Actual { get; }
            public string Expected { get; }
        }
    }
}
