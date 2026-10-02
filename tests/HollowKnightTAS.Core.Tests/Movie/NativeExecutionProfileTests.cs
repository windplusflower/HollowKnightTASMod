using System;
using HollowKnightTAS.Core.Movie;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.Movie
{
    [TestClass]
    public sealed class NativeExecutionProfileTests
    {
        [TestMethod]
        public void PublishedProfileRemainsV8AndExperimentalClockRequiresCompletePolicy()
        {
            Assert.AreEqual("hktas-unity-input-playerloop-canonical-cinematics-2026-v8",
                MovieProtocolV2.SelectNativeProfile(false, false, false));
            Assert.AreEqual(MovieProtocolV2.CanonicalStartupNativeProfileId,
                MovieProtocolV2.SelectNativeProfile(true, true, true));
            Assert.ThrowsExactly<InvalidOperationException>(() => MovieProtocolV2.SelectNativeProfile(true, false, true));
            Assert.ThrowsExactly<InvalidOperationException>(() => MovieProtocolV2.SelectNativeProfile(true, true, false));
        }

        [TestMethod]
        public void ModWallClockProfileRequiresAllClockPoliciesAndKeepsV4Identity()
        {
            Assert.AreEqual(MovieProtocolV2.CanonicalModClockNativeProfileId,
                MovieProtocolV2.SelectNativeProfile(true, true, true, true));
            Assert.ThrowsExactly<InvalidOperationException>(() => MovieProtocolV2.SelectNativeProfile(false, true, true, true));
            Assert.ThrowsExactly<InvalidOperationException>(() => MovieProtocolV2.SelectNativeProfile(true, false, true, true));
            Assert.AreEqual(MovieProtocolV2.CanonicalStartupNativeProfileId,
                MovieProtocolV2.SelectNativeProfile(true, true, true, false));
        }

        [TestMethod]
        public void SerialScenesRequireFullClockPolicyAndHaveIndependentIdentity()
        {
            Assert.AreEqual(NativeExecutionProfile.SerialSceneProfileId, NativeExecutionProfile.Select(true, true, true, true, true));
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeExecutionProfile.Select(true, true, true, false, true));
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeExecutionProfile.Select(false, true, true, true, true));
            Assert.IsTrue(NativeExecutionProfile.UsesModWallClock(NativeExecutionProfile.SerialSceneProfileId));
        }

        [TestMethod]
        public void ReplayRejectsBothDirectionsOfProfileMismatch()
        {
            foreach (var recordingProfile in new[] { MovieProtocolV2.NativeProfileId, MovieProtocolV2.CanonicalStartupNativeProfileId,
                         MovieProtocolV2.CanonicalModClockNativeProfileId, NativeExecutionProfile.SerialSceneProfileId })
            {
                var header = new MovieV2Header("game", "api", "mod", recordingProfile,
                    MovieProtocolV2.ActionSchemaId, false, new string('a', 64), 800, 450);
                foreach (var runtimeProfile in new[] { MovieProtocolV2.NativeProfileId, MovieProtocolV2.CanonicalStartupNativeProfileId,
                             MovieProtocolV2.CanonicalModClockNativeProfileId, NativeExecutionProfile.SerialSceneProfileId })
                {
                    var runtime = new MovieV2RuntimeCapabilities(runtimeProfile, MovieProtocolV2.ActionSchemaId,
                        true, true, true, 800, 450, new string('a', 64), "game", "api", "mod");
                    Assert.AreEqual(recordingProfile == runtimeProfile,
                        new MovieV2Compatibility().Evaluate(header, runtime).Allowed);
                }
            }
        }
    }
}
