using System;
using System.IO;
using System.Linq;
using HollowKnightTAS.Core.Input;
using HollowKnightTAS.Core.Ledger;
using HollowKnightTAS.Core.State;
using HollowKnightTAS.Core.Verification;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.Verification
{
    [TestClass]
    public sealed class VerificationCoreTests
    {
        private static readonly string Manifest = new string('1', 64);
        private static readonly string Baseline = new string('2', 64);
        private static readonly string Movie = new string('3', 64);

        [TestMethod]
        public void RunSignature_NormalizesProcessAndAbsoluteTickOrigins()
        {
            var expected = CreateRun(
                "session-a",
                "process-a",
                rawTickOffset: 100,
                visualTickOffset: 200,
                changedSecondSnapshot: false);
            var actual = CreateRun(
                "session-b",
                "process-b",
                rawTickOffset: 90000,
                visualTickOffset: 70000,
                changedSecondSnapshot: false);

            Assert.AreEqual(expected.RunSignature, actual.RunSignature);
            var comparison = RunComparator.Compare(expected, actual);
            Assert.AreEqual(RunComparisonStatus.Match, comparison.Status);
        }

        [TestMethod]
        public void LedgerSignature_NormalizesRenderTimingRemainder()
        {
            var input = InputSample.FromHeld(
                7,
                TasAction.Left,
                TasAction.None);
            var stamp = new TickStamp(
                100,
                200,
                300,
                0,
                TickPhase.InControlCommitted);
            var first = new VerificationLedgerEntry(
                7,
                stamp,
                input,
                -1,
                123,
                "GG_Vengefly",
                "input");
            var second = new VerificationLedgerEntry(
                7,
                stamp,
                input,
                -1,
                987654321,
                "GG_Vengefly",
                "input");

            Assert.AreEqual(
                RunSignature.ComputeLedgerWindowSha256(new[] { first }),
                RunSignature.ComputeLedgerWindowSha256(new[] { second }));
        }

        [TestMethod]
        public void VerificationSnapshotProjection_NormalizesSubPrecisionDrift()
        {
            var first = VerificationSnapshotNormalizer.Normalize(
                CreateSnapshot(45.189995f));
            var second = VerificationSnapshotNormalizer.Normalize(
                CreateSnapshot(45.19f));
            var meaningfulDifference =
                VerificationSnapshotNormalizer.Normalize(
                    CreateSnapshot(45.191f));

            CollectionAssert.AreEqual(first, second);
            CollectionAssert.AreNotEqual(first, meaningfulDifference);
        }

        [TestMethod]
        public void RunComparator_ReportsFirstSemanticKeyDifference()
        {
            var expected = CreateRun(
                "session-a",
                "process-a",
                100,
                200,
                changedSecondSnapshot: false);
            var actual = CreateRun(
                "session-b",
                "process-b",
                500,
                800,
                changedSecondSnapshot: true);

            var comparison = RunComparator.Compare(expected, actual);

            Assert.AreEqual(RunComparisonStatus.Desync, comparison.Status);
            Assert.IsNotNull(comparison.Desync);
            Assert.AreEqual("end", comparison.Desync!.FirstMilestoneId);
            Assert.AreEqual(55L, comparison.Desync.FirstMovieTick);
            Assert.AreEqual("semantic-state", comparison.Desync.Reason);
            Assert.IsFalse(comparison.Desync.SemanticDiff.AreEqual);
            Assert.AreEqual(
                "hero.position.x",
                comparison.Desync.SemanticDiff.FirstDifference!.Key);
        }

        [TestMethod]
        public void RunComparator_ReportsLedgerOnlyDifferenceWithContext()
        {
            var expected = CreateRun(
                "session-a",
                "process-a",
                100,
                200,
                changedSecondSnapshot: false);
            var actual = CreateRun(
                "session-b",
                "process-b",
                500,
                800,
                changedSecondSnapshot: false,
                changedSecondLedger: true);

            var comparison = RunComparator.Compare(expected, actual);

            Assert.AreEqual(RunComparisonStatus.Desync, comparison.Status);
            Assert.IsNotNull(comparison.Desync);
            Assert.AreEqual("ledger-window", comparison.Desync!.Reason);
            Assert.IsTrue(comparison.Desync.SemanticDiff.AreEqual);
            Assert.HasCount(1, comparison.Desync.ExpectedLedgerWindow);
            Assert.AreEqual(
                TasAction.Left,
                comparison.Desync.ExpectedLedgerWindow[0].Input.Held);
            Assert.AreEqual(
                TasAction.None,
                comparison.Desync.ActualLedgerWindow[0].Input.Held);
        }

        [TestMethod]
        public void RunComparator_ReportsRngDifferenceBeforeSemanticGuess()
        {
            var snapshot = ReadSnapshot("golden-v1.snapshot.hex");
            var expected = new RunEvidence(
                "session-a",
                "process-a",
                Manifest,
                Baseline,
                Movie,
                1,
                1,
                new[]
                {
                    CreateMilestone(
                        "rng-check",
                        10,
                        100,
                        snapshot,
                        TasAction.None,
                        rngStateSha256: new string('a', 64))
                });
            var actual = new RunEvidence(
                "session-b",
                "process-b",
                Manifest,
                Baseline,
                Movie,
                1,
                1,
                new[]
                {
                    CreateMilestone(
                        "rng-check",
                        10,
                        900,
                        snapshot,
                        TasAction.None,
                        rngStateSha256: new string('b', 64))
                });

            var comparison = RunComparator.Compare(expected, actual);

            Assert.AreEqual(RunComparisonStatus.Desync, comparison.Status);
            Assert.AreEqual("rng-state", comparison.Desync!.Reason);
            Assert.IsTrue(comparison.Desync.SemanticDiff.AreEqual);
        }

        [TestMethod]
        public void RunComparator_MetadataMismatchIsIncomparable()
        {
            var expected = CreateRun(
                "session-a",
                "process-a",
                100,
                200,
                changedSecondSnapshot: false);
            var actual = new RunEvidence(
                "session-b",
                "process-b",
                new string('f', 64),
                expected.BaselineSha256,
                expected.MovieId,
                expected.SnapshotSchemaVersion,
                expected.LedgerSchemaVersion,
                expected.Milestones);

            var comparison = RunComparator.Compare(expected, actual);

            Assert.AreEqual(RunComparisonStatus.Desync, comparison.Status);
            Assert.IsNull(comparison.Desync);
            StringAssert.Contains(comparison.Message, "run signatures differ");

            var baselineMismatch = new RunEvidence(
                "session-b",
                "process-b",
                expected.ManifestSha256,
                new string('e', 64),
                expected.MovieId,
                expected.SnapshotSchemaVersion,
                expected.LedgerSchemaVersion,
                expected.Milestones);
            var baselineComparison = RunComparator.Compare(expected, baselineMismatch);
            Assert.AreEqual(RunComparisonStatus.Incomparable, baselineComparison.Status);
            Assert.IsNull(baselineComparison.Desync);
            StringAssert.Contains(baselineComparison.Message, "Baseline");

            var movieMismatch = new RunEvidence(
                "session-b",
                "process-b",
                expected.ManifestSha256,
                expected.BaselineSha256,
                new string('d', 64),
                expected.SnapshotSchemaVersion,
                expected.LedgerSchemaVersion,
                expected.Milestones);
            var movieComparison = RunComparator.Compare(expected, movieMismatch);
            Assert.AreEqual(RunComparisonStatus.Incomparable, movieComparison.Status);
            Assert.IsNull(movieComparison.Desync);
            StringAssert.Contains(movieComparison.Message, "Movie ID");

            var schemaMismatch = new RunEvidence(
                "session-b",
                "process-b",
                expected.ManifestSha256,
                expected.BaselineSha256,
                expected.MovieId,
                expected.SnapshotSchemaVersion + 1,
                expected.LedgerSchemaVersion,
                expected.Milestones);
            var schemaComparison = RunComparator.Compare(expected, schemaMismatch);
            Assert.AreEqual(RunComparisonStatus.Incomparable, schemaComparison.Status);
            Assert.IsNull(schemaComparison.Desync);
            StringAssert.Contains(schemaComparison.Message, "schema");
        }

        [TestMethod]
        public void RunEvidence_RejectsDuplicateOrReorderedMilestones()
        {
            var first = CreateMilestone(
                "same",
                10,
                0,
                ReadSnapshot("golden-v1.snapshot.hex"),
                TasAction.None);
            var duplicate = CreateMilestone(
                "same",
                20,
                1,
                ReadSnapshot("golden-v1.snapshot.hex"),
                TasAction.Left);
            Assert.ThrowsExactly<ArgumentException>(
                () => new RunEvidence(
                    "session",
                    "process",
                    Manifest,
                    Baseline,
                    Movie,
                    1,
                    1,
                    new[] { first, duplicate }));

            var earlier = CreateMilestone(
                "earlier",
                5,
                1,
                ReadSnapshot("golden-v1.snapshot.hex"),
                TasAction.None);
            Assert.ThrowsExactly<ArgumentException>(
                () => new RunEvidence(
                    "session",
                    "process",
                    Manifest,
                    Baseline,
                    Movie,
                    1,
                    1,
                    new[] { first, earlier }));
        }

        private static RunEvidence CreateRun(
            string sessionId,
            string processId,
            ulong rawTickOffset,
            long visualTickOffset,
            bool changedSecondSnapshot,
            bool changedSecondLedger = false)
        {
            var first = CreateMilestone(
                "start",
                0,
                rawTickOffset,
                ReadSnapshot("golden-v1.snapshot.hex"),
                TasAction.None,
                visualTickOffset);
            var second = CreateMilestone(
                "end",
                55,
                rawTickOffset + 55,
                ReadSnapshot(
                    changedSecondSnapshot
                        ? "changed-hero-x.snapshot.hex"
                        : "golden-v1.snapshot.hex"),
                changedSecondLedger ? TasAction.None : TasAction.Left,
                visualTickOffset + 55);
            return new RunEvidence(
                sessionId,
                processId,
                Manifest,
                Baseline,
                Movie,
                1,
                1,
                new[] { first, second });
        }

        private static MilestoneRecord CreateMilestone(
            string id,
            long movieTick,
            ulong rawTick,
            byte[] snapshot,
            TasAction held,
            long visualTick = 0,
            string rngStateSha256 =
                VerificationLedgerEntry.RngNotCaptured)
        {
            var input = InputSample.FromHeld(
                checked((ulong)movieTick),
                held,
                TasAction.None);
            var stamp = new TickStamp(
                rawTick,
                visualTick,
                visualTick,
                0,
                TickPhase.LateUpdateEnd);
            var ledger = new[]
            {
                new VerificationLedgerEntry(
                    movieTick,
                    stamp,
                    input,
                    1,
                    0,
                    "GG_Vengefly",
                    "input")
            };
            return new MilestoneRecord(
                id,
                movieTick,
                stamp,
                "GG_Vengefly",
                snapshot,
                input,
                ledger,
                rngStateSha256);
        }

        private static byte[] ReadSnapshot(string name)
        {
            var text = File.ReadAllText(
                    Path.Combine(
                        AppContext.BaseDirectory,
                        "fixtures",
                        "state",
                        name))
                .Trim();
            return Enumerable.Range(0, text.Length / 2)
                .Select(index => Convert.ToByte(text.Substring(index * 2, 2), 16))
                .ToArray();
        }

        private static byte[] CreateSnapshot(float heroX)
        {
            var builder = new SemanticSnapshotBuilder();
            builder.AddString("scene.name", "GG_Vengefly");
            builder.AddString("game.state", "PLAYING");
            builder.AddFloat32("hero.position.x", heroX);
            builder.AddFloat32("hero.position.y", 13.400625f);
            builder.AddFloat32("hero.velocity.x", 0f);
            builder.AddFloat32("hero.velocity.y", 0f);
            builder.AddString("hero.actorState", "idle");
            builder.AddBoolean("hero.cState.onGround", true);
            builder.AddBoolean("hero.cState.jumping", false);
            builder.AddBoolean("hero.cState.falling", false);
            builder.AddBoolean("hero.cState.dashing", false);
            builder.AddBoolean("hero.cState.attacking", false);
            builder.AddBoolean("hero.cState.wallSliding", false);
            builder.AddInt32("player.health", 9);
            builder.AddInt32("player.maxHealth", 9);
            builder.AddInt32("player.mp", 99);
            return SemanticSnapshotCanonicalizer.Serialize(builder.Build());
        }
    }
}
