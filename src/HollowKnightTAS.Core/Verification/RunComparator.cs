using System;
using System.Diagnostics;
using HollowKnightTAS.Core.State;

namespace HollowKnightTAS.Core.Verification
{
    public enum RunComparisonStatus
    {
        Match = 0,
        Desync = 1,
        Incomparable = 2
    }

    public sealed class RunComparison
    {
        internal RunComparison(
            RunComparisonStatus status,
            string message,
            DesyncReport? desync)
        {
            Status = status;
            Message = message ?? throw new ArgumentNullException(nameof(message));
            Desync = desync;
        }

        public RunComparisonStatus Status { get; }
        public string Message { get; }
        public DesyncReport? Desync { get; }
        public bool IsMatch => Status == RunComparisonStatus.Match;
    }

    public static class RunComparator
    {
        public static RunComparison Compare(
            RunEvidence expected,
            RunEvidence actual)
        {
            if (expected == null)
            {
                throw new ArgumentNullException(nameof(expected));
            }

            if (actual == null)
            {
                throw new ArgumentNullException(nameof(actual));
            }

            var incompatible = FindIncompatibleMetadata(expected, actual);
            if (incompatible != null)
            {
                return new RunComparison(
                    RunComparisonStatus.Incomparable,
                    incompatible,
                    null);
            }

            var commonCount = Math.Min(
                expected.Milestones.Count,
                actual.Milestones.Count);
            for (var index = 0; index < commonCount; index++)
            {
                var left = expected.Milestones[index];
                var right = actual.Milestones[index];
                if (!string.Equals(
                        left.MilestoneId,
                        right.MilestoneId,
                        StringComparison.Ordinal)
                    || left.MovieTick != right.MovieTick)
                {
                    return new RunComparison(
                        RunComparisonStatus.Incomparable,
                        "Milestone declarations differ at index " + index + ".",
                        null);
                }

                if (!string.Equals(
                        left.SemanticSha256,
                        right.SemanticSha256,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        left.LedgerWindowSha256,
                        right.LedgerWindowSha256,
                        StringComparison.Ordinal)
                    || left.TickStamp.SceneEpoch != right.TickStamp.SceneEpoch
                    || left.TickStamp.Phase != right.TickStamp.Phase
                    || !string.Equals(
                        left.SceneName,
                        right.SceneName,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        left.RngStateSha256,
                        right.RngStateSha256,
                        StringComparison.Ordinal))
                {
                    var semanticDiff = SemanticSnapshotDiffer.Compare(
                        left.ReadSemanticSnapshot(),
                        right.ReadSemanticSnapshot());
                    var reason = !semanticDiff.AreEqual
                        ? "semantic-state"
                        : !string.Equals(
                            left.RngStateSha256,
                            right.RngStateSha256,
                            StringComparison.Ordinal)
                            ? "rng-state"
                        : !string.Equals(
                            left.LedgerWindowSha256,
                            right.LedgerWindowSha256,
                            StringComparison.Ordinal)
                            ? "ledger-window"
                            : "milestone-metadata";
                    return new RunComparison(
                        RunComparisonStatus.Desync,
                        "First difference at milestone "
                        + left.MilestoneId
                        + " (movie tick "
                        + left.MovieTick
                        + ").",
                        new DesyncReport(
                            index,
                            left,
                            right,
                            semanticDiff,
                            reason));
                }
            }

            if (expected.Milestones.Count != actual.Milestones.Count)
            {
                return new RunComparison(
                    RunComparisonStatus.Desync,
                    "Run ended with a different milestone count.",
                    null);
            }

            if (!string.Equals(
                    expected.RunSignature,
                    actual.RunSignature,
                    StringComparison.Ordinal))
            {
                return new RunComparison(
                    RunComparisonStatus.Desync,
                    "Canonical run signatures differ after matching milestones.",
                    null);
            }

            return new RunComparison(
                RunComparisonStatus.Match,
                "Run signatures and every milestone match.",
                null);
        }

        private static string? FindIncompatibleMetadata(
            RunEvidence expected,
            RunEvidence actual)
        {
            if (!string.Equals(
                    expected.ManifestSha256,
                    actual.ManifestSha256,
                    StringComparison.Ordinal))
            {
                Trace.TraceWarning(
                    $"RunComparator: Manifest SHA-256 differs ({expected.ManifestSha256} != {actual.ManifestSha256}); continuing comparison.");
            }

            if (!string.Equals(
                    expected.BaselineSha256,
                    actual.BaselineSha256,
                    StringComparison.Ordinal))
            {
                return "Baseline SHA-256 differs.";
            }

            if (!string.Equals(
                    expected.MovieId,
                    actual.MovieId,
                    StringComparison.Ordinal))
            {
                return "Movie ID differs.";
            }

            if (expected.SnapshotSchemaVersion != actual.SnapshotSchemaVersion
                || expected.LedgerSchemaVersion != actual.LedgerSchemaVersion)
            {
                return "Evidence schema versions differ.";
            }

            return null;
        }
    }
}
