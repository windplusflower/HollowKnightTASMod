using System;
using System.Linq;
using HollowKnightTAS.Core.Movie;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.Movie
{
    [TestClass]
    public sealed class NativeExecutionProfileTests
    {
        [TestMethod]
        public void PublishedProfileUsesV9AndExperimentalClockRequiresCompletePolicy()
        {
            Assert.AreEqual("hktas-unity-input-playerloop-canonical-cinematics-2026-v9",
                MovieProtocolV2.SelectNativeProfile(false, false, false));
            Assert.AreEqual(MovieProtocolV2.CanonicalStartupNativeProfileId,
                MovieProtocolV2.SelectNativeProfile(true, true, true));
            Assert.ThrowsExactly<InvalidOperationException>(() => MovieProtocolV2.SelectNativeProfile(true, false, true));
            Assert.ThrowsExactly<InvalidOperationException>(() => MovieProtocolV2.SelectNativeProfile(true, true, false));
        }

        [TestMethod]
        public void ModWallClockProfileRequiresAllClockPoliciesAndKeepsIndependentIdentity()
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
            Assert.AreEqual(NativeExecutionProfile.VirtualAudioSerialProfileId,
                NativeExecutionProfile.Select(true, true, true, true, true, true));
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeExecutionProfile.Select(true, true, true, true, false, true));
        }

        [TestMethod]
        public void IsolatedOnlineRequiresAllPoliciesAndRetainsOrdinaryDefault()
        {
            Assert.AreEqual(NativeExecutionProfile.IsolatedOnlineProfileId,
                NativeExecutionProfile.Select(true, true, true, true, true, true, true));
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeExecutionProfile.Select(true, true, true, true, true, false, true));
            Assert.IsTrue(NativeExecutionProfile.UsesVirtualAudio(NativeExecutionProfile.IsolatedOnlineProfileId));
            Assert.IsTrue(NativeExecutionProfile.UsesSerialScenes(NativeExecutionProfile.IsolatedOnlineProfileId));
            Assert.AreEqual(NativeExecutionProfile.ProfileId, NativeExecutionProfile.Select(false, false, false));
        }

        [TestMethod]
        public void ParticleInitializationRequiresIsolationPoliciesAndHasItsOwnReplayIdentity()
        {
            Assert.AreEqual(NativeExecutionProfile.CanonicalParticleProfileId,
                NativeExecutionProfile.Select(true, true, true, true, true, true, true, true));
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                NativeExecutionProfile.Select(true, true, true, true, true, true, false, true));
            Assert.IsTrue(NativeExecutionProfile.UsesIsolatedOnline(NativeExecutionProfile.CanonicalParticleProfileId));
            Assert.IsTrue(NativeExecutionProfile.UsesModWallClock(NativeExecutionProfile.CanonicalParticleProfileId));
            Assert.IsFalse(NativeExecutionProfile.UsesIsolatedOnline(NativeExecutionProfile.ProfileId));
        }

        [TestMethod]
        public void CanonicalBoundaryRequiresEveryPrecedingPolicyAndKeepsLegacyReplaySeparate()
        {
            Assert.AreEqual(NativeExecutionProfile.CanonicalBoundaryProfileId,
                NativeExecutionProfile.Select(true, true, true, true, true, true, true, true, true));
            for (var missing = 0; missing < 8; missing++)
            {
                var policies = Enumerable.Repeat(true, 8).ToArray(); policies[missing] = false;
                Assert.ThrowsExactly<InvalidOperationException>(() => NativeExecutionProfile.Select(
                    policies[0], policies[1], policies[2], policies[3], policies[4], policies[5], policies[6], policies[7], true));
            }
            var profile = NativeExecutionProfile.CanonicalBoundaryProfileId;
            Assert.IsTrue(NativeExecutionProfile.UsesCanonicalParticles(profile));
            Assert.IsTrue(NativeExecutionProfile.UsesIsolatedOnline(profile));
            Assert.IsTrue(NativeExecutionProfile.UsesVirtualAudio(profile));
            Assert.IsTrue(NativeExecutionProfile.UsesSerialScenes(profile));
            Assert.IsTrue(NativeExecutionProfile.UsesModWallClock(profile));
            Assert.IsTrue(NativeExecutionProfile.IsCanonicalStartupProfile(profile));
            Assert.IsTrue(NativeExecutionProfile.SupportsSceneCheckpoints(profile));
            foreach (var legacy in new[] { NativeExecutionProfile.ProfileId, NativeExecutionProfile.SerialSceneProfileId,
                NativeExecutionProfile.CanonicalParticleProfileId,
                "hktas-unity-input-playerloop-serial-scene-canonical-particles-2026-v11-experimental" })
                Assert.IsFalse(NativeExecutionProfile.SupportsSceneCheckpoints(legacy));
            Assert.AreEqual(NativeExecutionProfile.CanonicalParticleProfileId,
                NativeExecutionProfile.Select(true, true, true, true, true, true, true, true));
        }

        [TestMethod]
        public void ExperimentalReplayRejectsProfileMismatchIncludingOlderRevisions()
        {
            foreach (var recordingProfile in new[] { MovieProtocolV2.NativeProfileId, MovieProtocolV2.CanonicalStartupNativeProfileId,
                         MovieProtocolV2.CanonicalModClockNativeProfileId, NativeExecutionProfile.SerialSceneProfileId,
                         NativeExecutionProfile.VirtualAudioSerialProfileId, NativeExecutionProfile.IsolatedOnlineProfileId,
                         NativeExecutionProfile.CanonicalParticleProfileId,
                         NativeExecutionProfile.CanonicalBoundaryProfileId,
                         "hktas-unity-input-playerloop-canonical-cinematics-2026-v8",
                         "hktas-unity-input-playerloop-serial-scene-canonical-particles-2026-v11-experimental" })
            {
                var header = new MovieV2Header("game", "api", "mod", recordingProfile,
                    MovieProtocolV2.ActionSchemaId, false, new string('a', 64), 800, 450);
                foreach (var runtimeProfile in new[] { MovieProtocolV2.NativeProfileId, MovieProtocolV2.CanonicalStartupNativeProfileId,
                             MovieProtocolV2.CanonicalModClockNativeProfileId, NativeExecutionProfile.SerialSceneProfileId,
                             NativeExecutionProfile.VirtualAudioSerialProfileId, NativeExecutionProfile.IsolatedOnlineProfileId,
                             NativeExecutionProfile.CanonicalParticleProfileId, NativeExecutionProfile.CanonicalBoundaryProfileId })
                {
                    var runtime = new MovieV2RuntimeCapabilities(runtimeProfile, MovieProtocolV2.ActionSchemaId,
                        true, true, true, 800, 450, new string('a', 64), "game", "api", "mod");
                    var allowed = recordingProfile == runtimeProfile ||
                        (!NativeExecutionProfile.IsExperimentalProfile(recordingProfile)
                         && !NativeExecutionProfile.IsExperimentalProfile(runtimeProfile));
                    Assert.AreEqual(allowed,
                        new MovieV2Compatibility().Evaluate(header, runtime).Allowed);
                }
            }
        }
    }
}
