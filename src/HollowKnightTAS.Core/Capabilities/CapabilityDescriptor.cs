using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;

namespace HollowKnightTAS.Core.Capabilities
{
    public enum CapabilityStatus : byte
    {
        Unavailable = 0,
        ExperimentalDisabled = 1,
        ExperimentalEnabled = 2,
        Verified = 3,
        RejectedBuild = 4,
        Faulted = 5,
        Unsupported = 6
    }

    [Flags]
    public enum CapabilityPermission : ushort
    {
        None = 0,
        ProcessQuery = 1,
        ProcessRead = 2,
        ProcessWrite = 4,
        SuspendThreads = 8,
        GraphicsApi = 16,
        AudioApi = 32,
        InProcessBridge = 64
    }

    public sealed class CapabilityDescriptor
    {
        public CapabilityDescriptor(
            string capabilityId,
            int semanticVersion,
            CapabilityStatus status,
            CapabilityPermission permissions,
            bool requiresNativeHost,
            bool requiresBridge,
            bool requiresSafeBarrier,
            IEnumerable<string> supportedOperatingSystems,
            IEnumerable<string> supportedArchitectures,
            IEnumerable<string> buildWhitelistIds,
            int maximumDurationMilliseconds,
            long maximumMemoryBytes,
            long maximumDiskBytes,
            IEnumerable<string> mutuallyExclusiveWith,
            string fallback,
            string evidenceSha256)
        {
            if (!IsValidId(capabilityId))
            {
                throw new ArgumentException(
                    "Capability ID is invalid.",
                    nameof(capabilityId));
            }
            if (semanticVersion <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(semanticVersion));
            }
            if (maximumDurationMilliseconds <= 0
                || maximumDurationMilliseconds > 600000)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumDurationMilliseconds));
            }
            if (maximumMemoryBytes < 0
                || maximumMemoryBytes
                > 1024L * 1024L * 1024L * 1024L)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumMemoryBytes));
            }
            if (maximumDiskBytes < 0
                || maximumDiskBytes
                > 1024L * 1024L * 1024L * 1024L)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumDiskBytes));
            }
            if (string.IsNullOrWhiteSpace(fallback)
                || fallback.Length > 256)
            {
                throw new ArgumentException(
                    "Fallback description is invalid.",
                    nameof(fallback));
            }

            CapabilityId = capabilityId;
            SemanticVersion = semanticVersion;
            Status = status;
            Permissions = permissions;
            RequiresNativeHost = requiresNativeHost;
            RequiresBridge = requiresBridge;
            RequiresSafeBarrier = requiresSafeBarrier;
            SupportedOperatingSystems =
                NormalizeIds(
                    supportedOperatingSystems,
                    nameof(supportedOperatingSystems));
            SupportedArchitectures =
                NormalizeIds(
                    supportedArchitectures,
                    nameof(supportedArchitectures));
            BuildWhitelistIds =
                NormalizeIds(
                    buildWhitelistIds,
                    nameof(buildWhitelistIds));
            MaximumDurationMilliseconds =
                maximumDurationMilliseconds;
            MaximumMemoryBytes = maximumMemoryBytes;
            MaximumDiskBytes = maximumDiskBytes;
            MutuallyExclusiveWith =
                new ReadOnlyCollection<string>(
                    (mutuallyExclusiveWith
                     ?? Array.Empty<string>())
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray());
            if (MutuallyExclusiveWith.Any(
                    value => !IsValidId(value)))
            {
                throw new ArgumentException(
                    "Mutual exclusion contains an invalid ID.",
                    nameof(mutuallyExclusiveWith));
            }
            Fallback = fallback;
            var normalizedEvidence = string.IsNullOrWhiteSpace(evidenceSha256)
                ? "pending"
                : evidenceSha256;
            if (!IsLowerSha256(normalizedEvidence)
                && !string.Equals(
                    normalizedEvidence,
                    "pending",
                    StringComparison.Ordinal)
                && !string.Equals(
                    normalizedEvidence,
                    "not-applicable",
                    StringComparison.Ordinal))
            {
                Trace.TraceWarning(
                    "CapabilityDescriptor({0}): non-standard evidenceSha256 '{1}'; continuing.",
                    capabilityId,
                    normalizedEvidence);
            }
            if (status == CapabilityStatus.Verified
                && !IsLowerSha256(normalizedEvidence))
            {
                Trace.TraceWarning(
                    "CapabilityDescriptor({0}): Verified capability has non-SHA256 evidence '{1}'; continuing.",
                    capabilityId,
                    normalizedEvidence);
            }
            EvidenceSha256 = normalizedEvidence;
        }

        public string CapabilityId { get; }
        public int SemanticVersion { get; }
        public CapabilityStatus Status { get; }
        public CapabilityPermission Permissions { get; }
        public bool RequiresNativeHost { get; }
        public bool RequiresBridge { get; }
        public bool RequiresSafeBarrier { get; }
        public IReadOnlyList<string> SupportedOperatingSystems { get; }
        public IReadOnlyList<string> SupportedArchitectures { get; }
        public IReadOnlyList<string> BuildWhitelistIds { get; }
        public int MaximumDurationMilliseconds { get; }
        public long MaximumMemoryBytes { get; }
        public long MaximumDiskBytes { get; }
        public IReadOnlyList<string> MutuallyExclusiveWith { get; }
        public string Fallback { get; }
        public string EvidenceSha256 { get; }

        public static bool IsValidId(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                   && value.Length <= 128
                   && value[0] >= 'a'
                   && value[0] <= 'z'
                   && value.All(
                       character =>
                           character >= 'a' && character <= 'z'
                           || character >= '0'
                           && character <= '9'
                           || character == '.'
                           || character == '-');
        }

        private static IReadOnlyList<string> NormalizeIds(
            IEnumerable<string> values,
            string parameterName)
        {
            if (values == null)
            {
                throw new ArgumentNullException(parameterName);
            }
            var result = values
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (result.Any(value => !IsValidId(value)))
            {
                throw new ArgumentException(
                    "Capability metadata contains an invalid ID.",
                    parameterName);
            }
            return new ReadOnlyCollection<string>(result);
        }

        private static bool IsLowerSha256(string value)
        {
            return value != null
                   && value.Length == 64
                   && value.All(
                       character =>
                           character >= '0'
                           && character <= '9'
                           || character >= 'a'
                           && character <= 'f');
        }
    }

    public static class NativeCapabilityCatalog
    {
        public const string ProcessObserve =
            "native.process.observe.v1";
        public const string InputOverride =
            "native.input.override.experimental.v1";
        public const string ClockTrace =
            "native.clock.trace.experimental.v1";
        public const string Capture =
            "native.capture.experimental.v1";
        public const string Checkpoint =
            "native.checkpoint.experimental.v1";
        public const string SupportedBuildId =
            "hk-1.5.78.11833-win64-v1";
        public const string SupportedImageSha256 =
            "4d3d0f55afbb249170241d0dc6faf9a5dcf3e1ed427a282ae990bb6e4e3a6913";
        public const string SupportedAssemblyCSharpSha256 =
            "5944411bd93830369390a4b51766ee68c4ab26195b299e25a07b5e7d0e00086d";

        public static IReadOnlyList<CapabilityDescriptor> Create(
            bool requestObserve)
        {
            return new[]
            {
                new CapabilityDescriptor(
                    ProcessObserve,
                    1,
                    requestObserve
                        ? CapabilityStatus.ExperimentalEnabled
                        : CapabilityStatus.ExperimentalDisabled,
                    CapabilityPermission.ProcessQuery,
                    true,
                    false,
                    false,
                    new[] { "windows-10", "windows-11" },
                    new[] { "x64" },
                    new[] { SupportedBuildId },
                    10000,
                    64L * 1024L * 1024L,
                    1024L * 1024L,
                    Array.Empty<string>(),
                    "Runtime/T09 continue without native observation.",
                    "pending"),
                Unsupported(InputOverride),
                Unsupported(ClockTrace),
                Unsupported(Capture),
                Unsupported(Checkpoint)
            };
        }

        private static CapabilityDescriptor Unsupported(string id)
        {
            return new CapabilityDescriptor(
                id,
                1,
                CapabilityStatus.Unsupported,
                CapabilityPermission.None,
                true,
                false,
                id == Checkpoint,
                new[] { "windows-10", "windows-11" },
                new[] { "x64" },
                Array.Empty<string>(),
                10000,
                0,
                0,
                Array.Empty<string>(),
                "Use pure Runtime, T09 replay saves, and T14 ReplayOnly.",
                "not-applicable");
        }
    }
}
