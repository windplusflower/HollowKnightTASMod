using System;
using System.IO;
using System.Linq;
using System.Text;
using HollowKnightTAS.Cli;
using HollowKnightTAS.Core.Movie;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.Cli
{
    [TestClass]
    public sealed class MovieCommandTests
    {
        [TestMethod]
        public void ValidateAndInspect_ValidFixture_ReturnStableMetadata()
        {
            var path = FixturePath("valid", "actions-v1.hktas");
            using var validateOutput = new StringWriter();
            using var validateError = new StringWriter();

            var validateExit = Program.Run(
                new[] { "movie", "validate", path },
                validateOutput,
                validateError);

            Assert.AreEqual(0, validateExit);
            StringAssert.StartsWith(
                validateOutput.ToString(),
                "VALID 453889fc8b9ee0b53d9b9a849cf9dd03ae2571e17a8afcebd1652b353aa45114");
            StringAssert.Contains(validateOutput.ToString(), "ticks=25 commands=7");
            Assert.AreEqual(string.Empty, validateError.ToString());

            using var inspectOutput = new StringWriter();
            using var inspectError = new StringWriter();
            var inspectExit = Program.Run(
                new[] { "movie", "inspect", path },
                inspectOutput,
                inspectError);

            Assert.AreEqual(0, inspectExit);
            StringAssert.Contains(inspectOutput.ToString(), "input-ticks=25");
            StringAssert.Contains(inspectOutput.ToString(), "markers=1");
            StringAssert.Contains(inspectOutput.ToString(), "checkpoints=1");
            StringAssert.Contains(inspectOutput.ToString(), "asserts=2");
            Assert.AreEqual(string.Empty, inspectError.ToString());
        }

        [TestMethod]
        public void Validate_WithExpectedHashMismatch_FailsBeforeRuntime()
        {
            using var output = new StringWriter();
            using var error = new StringWriter();

            var path = FixturePath("valid", "minimal-v1.hktas");
            var exitCode = Program.Run(
                new[]
                {
                    "movie",
                    "validate",
                    path,
                    "--manifest-sha256",
                    new string('b', 64)
                },
                output,
                error);

            Assert.AreEqual(0, exitCode);
            StringAssert.StartsWith(output.ToString(), "VALID ");
            Assert.AreEqual(string.Empty, error.ToString());

            var parsed = new MovieParser().Parse(new StringReader(File.ReadAllText(path, new UTF8Encoding(false, true))), path);
            Assert.IsTrue(parsed.Success);
            var report = new MovieValidator().Validate(
                parsed.Document!,
                new MovieValidationContext(
                    MovieProtocolV1.DefaultMaxExpandedTicks,
                    MovieProtocolV1.DefaultSemanticPaths,
                    new string('b', 64),
                    new string('c', 64)));
            Assert.IsTrue(report.Success);
            Assert.IsTrue(report.Warnings.Any(w => w.Code == MovieDiagnosticCodes.ManifestMismatch));
            Assert.IsTrue(report.Warnings.Any(w => w.Code == MovieDiagnosticCodes.BaselineMismatch));
        }

        [TestMethod]
        public void FormatCheck_CanonicalFixturePasses_NoncanonicalFails()
        {
            using var canonicalOutput = new StringWriter();
            using var canonicalError = new StringWriter();
            var canonicalExit = Program.Run(
                new[]
                {
                    "movie",
                    "format",
                    FixturePath("valid", "actions-v1.hktas"),
                    "--check"
                },
                canonicalOutput,
                canonicalError);

            Assert.AreEqual(0, canonicalExit);
            StringAssert.StartsWith(canonicalOutput.ToString(), "FORMATTED ");
            Assert.AreEqual(string.Empty, canonicalError.ToString());

            var directory = CreateTemporaryDirectory();
            try
            {
                var path = Path.Combine(directory, "noncanonical.hktas");
                var canonical = File.ReadAllText(
                    FixturePath("valid", "minimal-v1.hktas"),
                    new UTF8Encoding(false, true));
                File.WriteAllText(
                    path,
                    "# comment\r\n" + canonical.Replace("\n", "\r\n"),
                    new UTF8Encoding(false));
                using var output = new StringWriter();
                using var error = new StringWriter();

                var exitCode = Program.Run(
                    new[] { "movie", "format", path, "--check" },
                    output,
                    error);

                Assert.AreEqual(3, exitCode);
                Assert.AreEqual(string.Empty, output.ToString());
                StringAssert.StartsWith(error.ToString(), "NOT_FORMATTED ");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [TestMethod]
        public void Format_WithoutCheck_WritesOnlyCanonicalMovie()
        {
            var path = FixturePath("valid", "minimal-v1.hktas");
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = Program.Run(
                new[] { "movie", "format", path },
                output,
                error);

            Assert.AreEqual(0, exitCode);
            Assert.AreEqual(
                File.ReadAllText(path, new UTF8Encoding(false, true)),
                output.ToString());
            Assert.AreEqual(string.Empty, error.ToString());
        }

        [TestMethod]
        public void Validate_BomAndUnknownCommand_ReturnSingleLineErrors()
        {
            var directory = CreateTemporaryDirectory();
            try
            {
                var path = Path.Combine(directory, "bom.hktas");
                var source = File.ReadAllBytes(
                    FixturePath("valid", "minimal-v1.hktas"));
                var withBom = new byte[source.Length + 3];
                withBom[0] = 0xEF;
                withBom[1] = 0xBB;
                withBom[2] = 0xBF;
                Buffer.BlockCopy(source, 0, withBom, 3, source.Length);
                File.WriteAllBytes(path, withBom);

                AssertSingleLineValidationFailure(path, "Movie must be UTF-8 without BOM");
                AssertSingleLineValidationFailure(
                    FixturePath("invalid", "unknown-command.hktas"),
                    MovieDiagnosticCodes.UnknownCommand);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static void AssertSingleLineValidationFailure(
            string path,
            string expectedText)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = Program.Run(
                new[] { "movie", "validate", path },
                output,
                error);

            Assert.AreEqual(3, exitCode);
            Assert.AreEqual(string.Empty, output.ToString());
            StringAssert.Contains(error.ToString(), expectedText);
            Assert.AreEqual(
                1,
                error.ToString().Split(
                    new[] { Environment.NewLine },
                    StringSplitOptions.RemoveEmptyEntries).Length);
        }

        private static string FixturePath(string category, string name)
        {
            return Path.Combine(
                AppContext.BaseDirectory,
                "fixtures",
                "movie",
                category,
                name);
        }

        private static string CreateTemporaryDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "HollowKnightTAS.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
