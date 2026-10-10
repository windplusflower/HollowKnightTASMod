using System;
using System.Linq;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Core.SceneCheckpoints;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.SceneCheckpoints
{
    [TestClass]
    public sealed class SceneCheckpointEnvironmentPolicyTests
    {
        [TestMethod]
        public void OnlyTasIsAdmittedByTheInitialCanonicalContract()
            => SceneCheckpointEnvironmentPolicy.ValidateMods(NativeExecutionProfile.CanonicalBoundaryProfileId,
                new[] { "HollowKnightTAS" });

        [TestMethod]
        [DataRow("Custom Knight")]
        [DataRow("Satchel")]
        [DataRow("DebugMod")]
        [DataRow("EnviousMarmu")]
        public void ExternalModsAreRejectedRegardlessOfWhetherTheirHooksAreActive(string mod)
        {
            var error = Assert.ThrowsExactly<InvalidOperationException>(() => SceneCheckpointEnvironmentPolicy.ValidateMods(
                NativeExecutionProfile.CanonicalBoundaryProfileId, new[] { "HollowKnightTAS", mod }));
            StringAssert.Contains(error.Message, mod);
        }

        [TestMethod]
        public void HistoricalFullReplayRetainsModAdmission()
        {
            foreach (var profile in new[] { NativeExecutionProfile.ProfileId, NativeExecutionProfile.CanonicalParticleProfileId })
                SceneCheckpointEnvironmentPolicy.ValidateMods(profile, new[] { "HollowKnightTAS", "Custom Knight", "Boss Mod" });
        }

        [TestMethod]
        public void UnboundedOrUnknownCanonicalInventoriesAreRejected()
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => SceneCheckpointEnvironmentPolicy.ValidateMods(
                NativeExecutionProfile.CanonicalBoundaryProfileId, Enumerable.Repeat("HollowKnightTAS", 257)));
            Assert.ThrowsExactly<InvalidOperationException>(() => SceneCheckpointEnvironmentPolicy.ValidateMods(
                NativeExecutionProfile.CanonicalBoundaryProfileId, new[] { "" }));
        }
    }
}
