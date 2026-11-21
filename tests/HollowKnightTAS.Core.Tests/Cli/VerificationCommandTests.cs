using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using HollowKnightTAS.Cli;
using HollowKnightTAS.Core.Input;
using HollowKnightTAS.Core.Ledger;
using HollowKnightTAS.Core.Verification;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.Cli
{
    [TestClass]
    public sealed class VerificationCommandTests
    {
        private static readonly string Manifest = new string('1', 64);
        private static readonly string Baseline = new string('2', 64);
        private static readonly string Movie = new string('3', 64);

        [TestMethod]
        public void VerificationValidateAndCompare_MatchingRunsPass()
        {
            using var temporary = new TemporaryDirectory();
            var expected = WriteRun(
                Path.Combine(temporary.Path, "expected"),
                "session-a",
                "process-a",
                changedSnapshot: false,
                Manifest);
            var actual = WriteRun(
                Path.Combine(temporary.Path, "actual"),
                "session-b",
                "process-b",
                changedSnapshot: false,
                Manifest);
            var output = new StringWriter();
            var error = new StringWriter();

            Assert.AreEqual(
                0,
                Program.Run(
                    new[] { "verification", "validate", expected },
                    output,
                    error));
            StringAssert.StartsWith(output.ToString(), "VALID ");
            output.GetStringBuilder().Clear();

            Assert.AreEqual(
                0,
                Program.Run(
                    new[]
                    {
                        "verification",
                        "compare",
                        expected,
                        actual
                    },
                    output,
                    error));
            StringAssert.StartsWith(output.ToString(), "MATCH ");
            Assert.AreEqual(string.Empty, error.ToString());
        }

        [TestMethod]
        public void VerificationCompare_SemanticDiffWritesActionableReport()
        {
            using var temporary = new TemporaryDirectory();
            var expected = WriteRun(
                Path.Combine(temporary.Path, "expected"),
                "session-a",
                "process-a",
                changedSnapshot: false,
                Manifest);
            var actual = WriteRun(
                Path.Combine(temporary.Path, "actual"),
                "session-b",
                "process-b",
                changedSnapshot: true,
                Manifest);
            var report = Path.Combine(temporary.Path, "report.json");
            var output = new StringWriter();
            var error = new StringWriter();

            var exitCode = Program.Run(
                new[]
                {
                    "verification",
                    "compare",
                    expected,
                    actual,
                    "--out",
                    report
                },
                output,
                error);

            Assert.AreEqual(4, exitCode);
            StringAssert.Contains(output.ToString(), "DESYNC");
            StringAssert.Contains(output.ToString(), "hero.position.x");
            var reportText = File.ReadAllText(report);
            StringAssert.Contains(reportText, "\"movieTick\":55");
            StringAssert.Contains(reportText, "\"reason\":\"semantic-state\"");
            StringAssert.Contains(reportText, "\"expectedRngStateSha256\":\"not-captured\"");
            StringAssert.Contains(reportText, "\"expectedContext\":");
            StringAssert.Contains(
                reportText,
                "\"timeMinusFixedTimeBits\":0");
            StringAssert.Contains(reportText, "\"eventName\":\"input\"");
        }

        [TestMethod]
        public void VerificationCompare_ManifestMismatchIsIncomparable()
        {
            using var temporary = new TemporaryDirectory();
            var expected = WriteRun(
                Path.Combine(temporary.Path, "expected"),
                "session-a",
                "process-a",
                changedSnapshot: false,
                Manifest);
            var actual = WriteRun(
                Path.Combine(temporary.Path, "actual"),
                "session-b",
                "process-b",
                changedSnapshot: false,
                new string('f', 64));
            var output = new StringWriter();

            var exitCode = Program.Run(
                new[] { "verification", "compare", expected, actual },
                output,
                new StringWriter());

            Assert.AreEqual(4, exitCode);
            StringAssert.Contains(output.ToString(), "DESYNC");
            StringAssert.Contains(output.ToString(), "run signatures differ");

            var baselineActual = WriteRun(
                Path.Combine(temporary.Path, "baseline-actual"),
                "session-c",
                "process-c",
                changedSnapshot: false,
                Manifest,
                new string('e', 64));
            var baselineOutput = new StringWriter();
            var baselineExitCode = Program.Run(
                new[] { "verification", "compare", expected, baselineActual },
                baselineOutput,
                new StringWriter());

            Assert.AreEqual(5, baselineExitCode);
            StringAssert.Contains(baselineOutput.ToString(), "INCOMPARABLE");
            StringAssert.Contains(baselineOutput.ToString(), "Baseline");
        }

        [TestMethod]
        public void VerificationCampaign_RequiresIndependentTenRunEvidence()
        {
            using var temporary = new TemporaryDirectory();
            for (var index = 1; index <= 10; index++)
            {
                WriteRun(
                    Path.Combine(temporary.Path, "run-" + index.ToString("D2")),
                    "session-" + index,
                    "process-" + index,
                    changedSnapshot: false,
                    Manifest);
            }

            var output = new StringWriter();
            var exitCode = Program.Run(
                new[]
                {
                    "verification",
                    "campaign",
                    temporary.Path
                },
                output,
                new StringWriter());

            Assert.AreEqual(0, exitCode);
            StringAssert.Contains(output.ToString(), "LOCAL_VERIFIED runs=10");
        }

        [TestMethod]
        public void VerificationValidate_RejectsSnapshotTraversal()
        {
            using var temporary = new TemporaryDirectory();
            var path = WriteRun(
                Path.Combine(temporary.Path, "run"),
                "session-a",
                "process-a",
                changedSnapshot: false,
                Manifest);
            var text = File.ReadAllText(path, Encoding.UTF8)
                .Replace(
                    "\"snapshotFile\":\"milestone.snapshot\"",
                    "\"snapshotFile\":\"../milestone.snapshot\"",
                    StringComparison.Ordinal);
            File.WriteAllText(path, text, new UTF8Encoding(false, true));

            var exitCode = Program.Run(
                new[] { "verification", "validate", path },
                new StringWriter(),
                new StringWriter());

            Assert.AreEqual(3, exitCode);
        }

        [TestMethod]
        public void VerificationStaticFixtures_CoverMatchDesyncAndIncomparable()
        {
            var root = Path.Combine(
                AppContext.BaseDirectory,
                "fixtures",
                "verification");

            AssertFixtureComparison(
                Path.Combine(root, "matching"),
                0,
                "MATCH");
            AssertFixtureComparison(
                Path.Combine(root, "first-diff"),
                4,
                "DESYNC");
            AssertFixtureComparison(
                Path.Combine(root, "incomparable"),
                4,
                "DESYNC");
        }

        private static string WriteRun(
            string directory,
            string sessionId,
            string processId,
            bool changedSnapshot,
            string manifest,
            string? baseline = null)
        {
            var effectiveBaseline = baseline ?? Baseline;
            Directory.CreateDirectory(directory);
            var snapshot = ReadSnapshot(
                changedSnapshot
                    ? "changed-hero-x.snapshot.hex"
                    : "golden-v1.snapshot.hex");
            var snapshotPath = Path.Combine(directory, "milestone.snapshot");
            File.WriteAllBytes(snapshotPath, snapshot);
            var stamp = new TickStamp(
                1000,
                2000,
                3000,
                0,
                TickPhase.LateUpdateEnd);
            var input = InputSample.FromHeld(
                55,
                TasAction.Left,
                TasAction.Left);
            var ledger = new[]
            {
                new VerificationLedgerEntry(
                    55,
                    stamp,
                    input,
                    1,
                    0,
                    "GG_Vengefly",
                    "input")
            };
            var milestone = new MilestoneRecord(
                "end",
                55,
                stamp,
                "GG_Vengefly",
                snapshot,
                input,
                ledger);
            var evidence = new RunEvidence(
                sessionId,
                processId,
                manifest,
                effectiveBaseline,
                Movie,
                1,
                1,
                new[] { milestone });
            var inputObject = SerializeInput(input);
            var stampObject = SerializeStamp(stamp);
            var root = new Dictionary<string, object?>
            {
                ["schemaVersion"] = 1,
                ["sessionId"] = sessionId,
                ["processInstanceId"] = processId,
                ["manifestSha256"] = manifest,
                ["baselineSha256"] = effectiveBaseline,
                ["movieId"] = Movie,
                ["snapshotSchemaVersion"] = 1,
                ["ledgerSchemaVersion"] = 1,
                ["semanticProjection"] =
                    VerificationSnapshotNormalizer.ProjectionId,
                ["runSignature"] = evidence.RunSignature,
                ["milestones"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["milestoneId"] = milestone.MilestoneId,
                        ["movieTick"] = milestone.MovieTick,
                        ["tickStamp"] = stampObject,
                        ["sceneName"] = milestone.SceneName,
                        ["semanticSha256"] = milestone.SemanticSha256,
                        ["snapshotFile"] = "milestone.snapshot",
                        ["input"] = inputObject,
                        ["ledgerWindowSha256"] =
                            milestone.LedgerWindowSha256,
                        ["rngStateSha256"] = milestone.RngStateSha256,
                        ["ledgerWindow"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["movieTick"] = 55,
                                ["tickStamp"] = stampObject,
                                ["input"] = inputObject,
                                ["fixedStepsSincePreviousVisual"] = 1,
                                ["timeMinusFixedTimeBits"] = 0,
                                ["sceneName"] = "GG_Vengefly",
                                ["eventName"] = "input",
                                ["rngStateSha256"] = "not-captured"
                            }
                        }
                    }
                }
            };
            var path = Path.Combine(directory, "run.json");
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(root),
                new UTF8Encoding(false, true));
            return path;
        }

        private static Dictionary<string, object> SerializeStamp(
            TickStamp stamp)
        {
            return new Dictionary<string, object>
            {
                ["inputTick"] = stamp.InputTick,
                ["visualTick"] = stamp.VisualTick,
                ["fixedTick"] = stamp.FixedTick,
                ["sceneEpoch"] = stamp.SceneEpoch,
                ["phase"] = stamp.Phase.ToString()
            };
        }

        private static Dictionary<string, object> SerializeInput(
            InputSample input)
        {
            return new Dictionary<string, object>
            {
                ["inputTick"] = input.InputTick,
                ["held"] = (int)input.Held,
                ["pressed"] = (int)input.Pressed,
                ["released"] = (int)input.Released,
                ["axisX"] = input.AxisX,
                ["axisY"] = input.AxisY
            };
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

        private static void AssertFixtureComparison(
            string directory,
            int expectedExitCode,
            string expectedOutput)
        {
            var expected = Path.Combine(directory, "expected.run.json");
            var actual = Path.Combine(directory, "actual.run.json");
            var validationOutput = new StringWriter();
            Assert.AreEqual(
                0,
                Program.Run(
                    new[] { "verification", "validate", expected },
                    validationOutput,
                    new StringWriter()));
            validationOutput.GetStringBuilder().Clear();
            Assert.AreEqual(
                0,
                Program.Run(
                    new[] { "verification", "validate", actual },
                    validationOutput,
                    new StringWriter()));

            var comparisonOutput = new StringWriter();
            Assert.AreEqual(
                expectedExitCode,
                Program.Run(
                    new[]
                    {
                        "verification",
                        "compare",
                        expected,
                        actual
                    },
                    comparisonOutput,
                    new StringWriter()));
            StringAssert.Contains(
                comparisonOutput.ToString(),
                expectedOutput);
        }

        private sealed class TemporaryDirectory : IDisposable
        {
            public TemporaryDirectory()
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "HollowKnightTAS-tests-"
                    + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public void Dispose()
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, true);
                }
            }
        }
    }
}
