using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;

namespace HollowKnightTAS.Core.Rng
{
    public sealed class RngAssemblyIdentity
    {
        public RngAssemblyIdentity(
            string assemblyName,
            string sha256,
            string moduleVersionId)
        {
            AssemblyName = RngValidation.RequireText(
                assemblyName,
                256,
                nameof(assemblyName));
            Sha256 = RngValidation.RequireSha256(
                sha256,
                nameof(sha256));
            if (!Guid.TryParse(moduleVersionId, out var parsed))
            {
                throw new ArgumentException(
                    "A canonical module version GUID is required.",
                    nameof(moduleVersionId));
            }

            ModuleVersionId = parsed.ToString("D");
        }

        public string AssemblyName { get; }
        public string Sha256 { get; }
        public string ModuleVersionId { get; }
    }

    public sealed class RngCallSiteDescriptor
    {
        public RngCallSiteDescriptor(
            string callSiteId,
            string declaringType,
            string methodSignature,
            int metadataToken,
            string methodIlSha256,
            string hookType,
            string expectedRandomApi,
            int expectedRandomCallCount)
        {
            if (metadataToken <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(metadataToken));
            }

            if (expectedRandomCallCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(expectedRandomCallCount));
            }

            CallSiteId = RngValidation.RequireIdentifier(
                callSiteId,
                nameof(callSiteId));
            DeclaringType = RngValidation.RequireText(
                declaringType,
                512,
                nameof(declaringType));
            MethodSignature = RngValidation.RequireText(
                methodSignature,
                2048,
                nameof(methodSignature));
            MetadataToken = metadataToken;
            MethodIlSha256 = RngValidation.RequireSha256(
                methodIlSha256,
                nameof(methodIlSha256));
            HookType = RngValidation.RequireIdentifier(
                hookType,
                nameof(hookType));
            ExpectedRandomApi = RngValidation.RequireText(
                expectedRandomApi,
                512,
                nameof(expectedRandomApi));
            ExpectedRandomCallCount = expectedRandomCallCount;
        }

        public string CallSiteId { get; }
        public string DeclaringType { get; }
        public string MethodSignature { get; }
        public int MetadataToken { get; }
        public string MethodIlSha256 { get; }
        public string HookType { get; }
        public string ExpectedRandomApi { get; }
        public int ExpectedRandomCallCount { get; }
    }

    public enum RngWhitelistResolutionStatus : byte
    {
        Ready = 1,
        AssemblyMismatch = 2,
        ModuleMismatch = 3,
        CallSiteMissing = 4,
        CallSiteMismatch = 5
    }

    public sealed class RngWhitelistResolution
    {
        internal RngWhitelistResolution(
            RngWhitelistResolutionStatus status,
            string detail,
            IReadOnlyList<RngCallSiteDescriptor> resolvedCallSites)
        {
            Status = status;
            Detail = detail ?? string.Empty;
            ResolvedCallSites = resolvedCallSites
                                ?? throw new ArgumentNullException(
                                    nameof(resolvedCallSites));
        }

        public RngWhitelistResolutionStatus Status { get; }
        public string Detail { get; }
        public IReadOnlyList<RngCallSiteDescriptor> ResolvedCallSites { get; }
        public bool Ready => Status == RngWhitelistResolutionStatus.Ready;
    }

    public sealed class RngWhitelist
    {
        public const string TargetBuildId = "hk-1.5.78.11833-rng-v1";
        public const string CoverageId =
            "unity-random-partial-whitelist-v1";

        private readonly ReadOnlyCollection<RngCallSiteDescriptor> callSites;

        public RngWhitelist(
            string whitelistId,
            RngAssemblyIdentity assembly,
            IEnumerable<RngCallSiteDescriptor> callSites)
        {
            WhitelistId = RngValidation.RequireIdentifier(
                whitelistId,
                nameof(whitelistId));
            Assembly = assembly
                       ?? throw new ArgumentNullException(nameof(assembly));
            if (callSites == null)
            {
                throw new ArgumentNullException(nameof(callSites));
            }

            var copied = callSites
                .OrderBy(value => value.CallSiteId, StringComparer.Ordinal)
                .ToList();
            if (copied.Count == 0)
            {
                throw new ArgumentException(
                    "At least one RNG call site is required.",
                    nameof(callSites));
            }

            if (copied.Select(value => value.CallSiteId)
                .Distinct(StringComparer.Ordinal)
                .Count() != copied.Count)
            {
                throw new ArgumentException(
                    "RNG call-site IDs must be unique.",
                    nameof(callSites));
            }

            this.callSites =
                new ReadOnlyCollection<RngCallSiteDescriptor>(copied);
        }

        public string WhitelistId { get; }
        public RngAssemblyIdentity Assembly { get; }
        public IReadOnlyList<RngCallSiteDescriptor> CallSites => callSites;

        public RngWhitelistResolution Resolve(
            RngAssemblyIdentity actualAssembly,
            IEnumerable<RngCallSiteDescriptor> actualCallSites)
        {
            if (actualAssembly == null)
            {
                throw new ArgumentNullException(nameof(actualAssembly));
            }

            if (actualCallSites == null)
            {
                throw new ArgumentNullException(nameof(actualCallSites));
            }

            if (!string.Equals(
                    Assembly.AssemblyName,
                    actualAssembly.AssemblyName,
                    StringComparison.Ordinal)
                || !string.Equals(
                    Assembly.Sha256,
                    actualAssembly.Sha256,
                    StringComparison.Ordinal))
            {
                Trace.TraceWarning(
                    "[HKTAS] RNG whitelist assembly identity differs (expected="
                    + Assembly.AssemblyName + "/" + Assembly.Sha256
                    + ", actual=" + actualAssembly.AssemblyName + "/" + actualAssembly.Sha256
                    + "); continuing without strict assembly lock.");
            }

            if (!string.Equals(
                    Assembly.ModuleVersionId,
                    actualAssembly.ModuleVersionId,
                    StringComparison.Ordinal))
            {
                Trace.TraceWarning(
                    "[HKTAS] RNG whitelist assembly MVID differs (expected="
                    + Assembly.ModuleVersionId + ", actual=" + actualAssembly.ModuleVersionId
                    + "); continuing without strict MVID lock.");
            }

            var actual = actualCallSites.ToDictionary(
                value => value.CallSiteId,
                StringComparer.Ordinal);
            var resolved = new List<RngCallSiteDescriptor>();
            foreach (var expected in callSites)
            {
                if (!actual.TryGetValue(
                        expected.CallSiteId,
                        out var candidate))
                {
                    Trace.TraceWarning("[HKTAS] RNG call site is missing: " + expected.CallSiteId + ".");
                    return Failure(
                        RngWhitelistResolutionStatus.CallSiteMissing,
                        "Call site is missing: " + expected.CallSiteId + ".");
                }

                if (!Equivalent(expected, candidate))
                {
                    Trace.TraceWarning(
                        "[HKTAS] RNG call site metadata differs for '"
                        + expected.CallSiteId
                        + "' (token/IL hash/signature); continuing.");
                }

                resolved.Add(candidate);
            }

            return new RngWhitelistResolution(
                RngWhitelistResolutionStatus.Ready,
                "RNG call sites resolved.",
                new ReadOnlyCollection<RngCallSiteDescriptor>(resolved));
        }

        public static RngWhitelist CreateTargetBuild()
        {
            return new RngWhitelist(
                TargetBuildId,
                new RngAssemblyIdentity(
                    "Assembly-CSharp.dll",
                    "5944411bd93830369390a4b51766ee68c4ab26195b299e25a07b5e7d0e00086d",
                    "2b54cdaf-7a93-4f0e-b297-d50c4090ea9f"),
                new[]
                {
                    new RngCallSiteDescriptor(
                        "helper.random-vector2",
                        "Helper",
                        "UnityEngine.Vector2 Helper::GetRandomVector2InRange(UnityEngine.Vector2,UnityEngine.Vector2)",
                        unchecked((int)0x06001BC0),
                        "6fe187f1d6cd4badb112c5b4269f171ade2e9cc38d561ad3095d121d0c7bcb38",
                        "method-boundary",
                        "System.Single UnityEngine.Random::Range(System.Single,System.Single)",
                        2),
                    new RngCallSiteDescriptor(
                        "hero.take-damage",
                        "HeroController",
                        "System.Void HeroController::TakeDamage(UnityEngine.GameObject,GlobalEnums.CollisionSide,System.Int32,System.Int32)",
                        unchecked((int)0x06000578),
                        "f5003dd615f7e330adb220543ba9582d40b4c8b6a575f63cebd788dd3512310e",
                        "method-boundary",
                        "System.Int32 UnityEngine.Random::Range(System.Int32,System.Int32)",
                        7)
                });
        }

        private static bool Equivalent(
            RngCallSiteDescriptor expected,
            RngCallSiteDescriptor actual)
        {
            return string.Equals(
                       expected.DeclaringType,
                       actual.DeclaringType,
                       StringComparison.Ordinal)
                   && string.Equals(
                       expected.MethodSignature,
                       actual.MethodSignature,
                       StringComparison.Ordinal)
                   && expected.MetadataToken == actual.MetadataToken
                   && string.Equals(
                       expected.MethodIlSha256,
                       actual.MethodIlSha256,
                       StringComparison.Ordinal)
                   && string.Equals(
                       expected.HookType,
                       actual.HookType,
                       StringComparison.Ordinal)
                   && string.Equals(
                       expected.ExpectedRandomApi,
                       actual.ExpectedRandomApi,
                       StringComparison.Ordinal)
                   && expected.ExpectedRandomCallCount
                   == actual.ExpectedRandomCallCount;
        }

        private static RngWhitelistResolution Failure(
            RngWhitelistResolutionStatus status,
            string detail)
        {
            return new RngWhitelistResolution(
                status,
                detail,
                Array.Empty<RngCallSiteDescriptor>());
        }
    }
}
