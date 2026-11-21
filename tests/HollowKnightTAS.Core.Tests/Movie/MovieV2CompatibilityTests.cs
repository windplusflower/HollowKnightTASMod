using System;
using System.IO;
using System.Linq;
using HollowKnightTAS.Core.Movie;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.Movie
{
    [TestClass]
    public sealed class MovieV2CompatibilityTests
    {
        private static readonly string EnvironmentHash = new string('a', 64);

        [TestMethod]
        public void MatchingCapabilities_AllowReplay()
        {
            var report = new MovieV2Compatibility().Evaluate(Header(), Runtime());
            Assert.IsTrue(report.Allowed);
            Assert.AreEqual(0, report.Errors.Count);
            Assert.AreEqual(0, report.Warnings.Count);
        }

        [TestMethod]
        public void CanonicalFirstLevelPolicy_AllowsLegacyInputsWithWarning()
        {
            var oldHeader = new MovieV2Header("game1", "api1", "mod1",
                "hktas-unity-input-playerloop-load-elision-scene-rng-2026-v3",
                MovieProtocolV2.ActionSchemaId, true, EnvironmentHash, 800, 450);
            var current = Runtime(profile: MovieProtocolV2.NativeProfileId);
            var legacyReport = new MovieV2Compatibility().Evaluate(oldHeader, current);
            Assert.IsTrue(legacyReport.Allowed);
            Assert.AreEqual(0, legacyReport.Errors.Count);
            Assert.IsTrue(legacyReport.Warnings.Any(w => w.Message.Contains("Execution rules differ")));
            var currentHeader = new MovieV2Header("game1", "api1", "mod1",
                MovieProtocolV2.NativeProfileId, MovieProtocolV2.ActionSchemaId,
                true, EnvironmentHash, 800, 450);
            Assert.IsTrue(new MovieV2Compatibility().Evaluate(currentHeader, current).Allowed);
        }

        [TestMethod]
        public void MissingNativeGateSaveGuardOrMouseBridge_BlockReplay()
        {
            var runtime = Runtime(nativeGate: false, saveGuard: false, mouseBridge: false,
                profile: "other-profile");
            var report = new MovieV2Compatibility().Evaluate(Header(mouse: true), runtime);
            Assert.IsFalse(report.Allowed);
            Assert.IsTrue(report.Errors.Count >= 3);
            Assert.IsTrue(report.Warnings.Any(w => w.Message.Contains("Execution rules differ")));
            Assert.IsTrue(report.Errors.Any(value => value.Message.Contains("save write guard")));
            Assert.IsTrue(report.Errors.Any(value => value.Message.Contains("frame gate")));
        }

        [TestMethod]
        public void VersionAndEnvironmentDifferences_AreDistinctWarnings()
        {
            var runtime = new MovieV2RuntimeCapabilities("profile", MovieProtocolV2.ActionSchemaId,
                true, true, true, 800, 450, new string('b', 64), "game2", "api2", "mod2");
            var report = new MovieV2Compatibility().Evaluate(Header(), runtime);
            Assert.IsTrue(report.Allowed);
            Assert.AreEqual(4, report.Warnings.Count);
            Assert.IsTrue(report.Warnings.Any(value => value.Message.Contains("Game version")));
            Assert.IsTrue(report.Warnings.Any(value => value.Message.Contains("Modding API version")));
            Assert.IsTrue(report.Warnings.Any(value => value.Message.Contains("HollowKnightTAS Mod version")));
            Assert.IsTrue(report.Warnings.Any(value => value.Message.Contains("Environment identity")));
        }

        [TestMethod]
        public void DraftMetadataAndMouseViewportMismatch_BlockReplay()
        {
            var draft = new MovieV2Header("unknown", "unknown", "unknown", "profile",
                MovieProtocolV2.ActionSchemaId, true, "none", 0, 0);
            var report = new MovieV2Compatibility().Evaluate(draft, Runtime());
            Assert.IsTrue(report.Allowed);
            Assert.IsTrue(report.Warnings.Count > 0);
            Assert.IsTrue(report.Warnings.Any(value => value.Message.Contains("Draft or missing metadata")));
            Assert.IsTrue(report.Warnings.Any(value => value.Message.Contains("Viewport dimensions")));
            var keyboardOnly = new MovieV2Compatibility().Evaluate(Header(mouse: false),
                Runtime(width: 1280, height: 720));
            Assert.IsTrue(keyboardOnly.Allowed);
            Assert.IsTrue(keyboardOnly.Warnings.Any(value => value.Message.Contains("Viewport")));
        }

        [TestMethod]
        public void Validator_RejectsDisabledMouseAndBadRunsWithoutBindingToSaveSlot()
        {
            var mouseSample = new GameInputSample(GameInputChannel.MouseInControl, Array.Empty<short>(),
                new MouseFrameState(10, 20, 0, 0, 0, 0));
            var movie = new MovieV2Document("validation.hktas", Header(mouse: false), new[]
            {
                new NativeFrameRun(1, new[] { mouseSample }, new MovieSourceSpan("validation.hktas", 2, 1, 1))
            });
            var report = new MovieV2Validator().Validate(movie, MovieV2ValidationContext.CreateDefault());
            Assert.IsFalse(report.Success);
            Assert.AreEqual(1L, report.ExpandedFrames);
            Assert.IsTrue(report.Diagnostics.Any(value => value.Message.Contains("mouse input is disabled")));

            var emptyMovie = new MovieV2Document("empty.hktas", Header(mouse: false), new[]
            {
                new NativeFrameRun(2, Array.Empty<GameInputSample>(), new MovieSourceSpan("empty.hktas", 2, 1, 1))
            });
            var valid = new MovieV2Validator().Validate(emptyMovie, MovieV2ValidationContext.CreateDefault());
            Assert.IsTrue(valid.Success);
            Assert.AreEqual(2L, valid.ExpandedFrames);
        }

        [TestMethod]
        public void AnyCodec_RoutesV1AndV2AndNeverFallsBackFromBadV2()
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "movie", "valid", "minimal-v1.hktas");
            using var v1Reader = new StreamReader(fixture);
            var v1 = new MovieAnyCodec().Parse(v1Reader, fixture);
            Assert.IsTrue(v1.Success);
            Assert.AreEqual(1, v1.Version);
            Assert.IsNotNull(v1.V1Document);
            Assert.IsNull(v1.V2Document);

            var v2Document = new MovieV2Document("v2.hktas", Header(mouse: false), new[]
            {
                new NativeFrameRun(1, Array.Empty<GameInputSample>(), new MovieSourceSpan("v2.hktas", 2, 1, 1))
            });
            var source = new MovieV2Codec().WriteCanonical(v2Document);
            var v2 = new MovieAnyCodec().Parse(new StringReader(source), "v2.hktas");
            Assert.IsTrue(v2.Success);
            Assert.AreEqual(2, v2.Version);
            Assert.IsNull(v2.V1Document);
            Assert.IsNotNull(v2.V2Document);

            var invalid = new MovieAnyCodec().Parse(new StringReader("{\"format\":\"hktas\",\"version\":2}\n"),
                "bad-v2.hktas");
            Assert.IsFalse(invalid.Success);
            Assert.AreEqual(2, invalid.Version);
            Assert.IsNull(invalid.V1Document);
            Assert.IsNull(invalid.V2Document);
        }

        private static MovieV2Header Header(bool mouse = true)
            => new MovieV2Header("game1", "api1", "mod1", "profile",
                MovieProtocolV2.ActionSchemaId, mouse, EnvironmentHash, 800, 450);

        private static MovieV2RuntimeCapabilities Runtime(bool nativeGate = true,
            bool saveGuard = true, bool mouseBridge = true, string profile = "profile",
            int width = 800, int height = 450)
            => new MovieV2RuntimeCapabilities(profile, MovieProtocolV2.ActionSchemaId,
                nativeGate, saveGuard, mouseBridge, width, height, EnvironmentHash,
                "game1", "api1", "mod1");
    }
}
