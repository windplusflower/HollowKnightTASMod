using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    public sealed class LeanS0HudParityTests
    {
        [TestMethod]
        [DataRow(false, 0, "HUD Frame", "HUD Frame Idle", "HUD Frame CrackAppear", "HUD Frame Cracked")]
        [DataRow(false, 1, "HUD Frame St", "HUD Frame Idle St", "HUD Frame CrackAppear St", "HUD Frame Cracked St")]
        [DataRow(false, 2, "HUD Frame St", "HUD Frame Idle St", "HUD Frame CrackAppear St", "HUD Frame Cracked St")]
        [DataRow(true, 0, "HUD Frame GG", "HUD Frame Idle GG", "HUD Frame CrackAppear", "HUD Frame Cracked")]
        [DataRow(true, 1, "HUD Frame GG", "HUD Frame Idle GG", "HUD Frame CrackAppear", "HUD Frame Cracked")]
        public void ResolveHudFrameClips_MatchesGameModes(
            bool bossRushMode,
            int permadeathMode,
            string expectedAppear,
            string expectedIdle,
            string expectedCrackAppear,
            string expectedCracked)
        {
            var clips = LeanS0HudParity.ResolveHudFrameClips(bossRushMode, permadeathMode);

            Assert.AreEqual(expectedAppear, clips.AnimAppear);
            Assert.AreEqual(expectedIdle, clips.AnimIdle);
            Assert.AreEqual(expectedCrackAppear, clips.AnimCrackAppear);
            Assert.AreEqual(expectedCracked, clips.AnimCracked);
        }

        [TestMethod]
        [DataRow(true, "Idle", "Out")]
        [DataRow(true, "Go Out", "Out")]
        [DataRow(true, "Out", "Out")]
        [DataRow(false, "Idle", "Idle")]
        [DataRow(false, "Out", "Idle")]
        [DataRow(false, "Go Out", "Idle")]
        [DataRow(false, "In", "In")]
        [DataRow(false, "Come In", "In")]
        [DataRow(false, null, "Idle")]
        public void ResolveSlideOutSteadyState_ConvergesTransitionalStates(
            bool isHudOut,
            string? capturedState,
            string expectedState)
        {
            Assert.AreEqual(expectedState, LeanS0HudParity.ResolveSlideOutSteadyState(isHudOut, capturedState));
        }

        [TestMethod]
        [DataRow(1, 1, 9, false, 4, "Load Animation?", "Idle")]
        [DataRow(2, 1, 9, false, 4, "Load Animation?", "Empty")]
        [DataRow(9, 1, 9, false, 4, "Waiting", "Empty")]
        [DataRow(10, 1, 9, false, 4, "Idle", "Inactive")]
        [DataRow(7, 7, 9, false, 4, "Empty", "Idle")]
        [DataRow(8, 7, 9, false, 4, "Idle", "Empty")]
        [DataRow(3, 3, 5, false, 0, "Waiting", "Idle")]
        [DataRow(4, 3, 5, false, 0, "Idle", "Empty")]
        [DataRow(6, 3, 5, false, 0, "Idle", "Inactive")]
        [DataRow(5, 4, 9, true, 4, "Empty", "Bound")]
        [DataRow(4, 4, 9, true, 4, "Load Animation?", "Idle")]
        [DataRow(3, 2, 9, true, 4, "Charm Pause", "Empty")]
        public void ResolveHealthDisplaySteadyState_RejectsIntermediateAndStaleStates(
            int healthNumber,
            int currentHp,
            int maxHp,
            bool boundShell,
            int boundMaxHp,
            string? capturedState,
            string expectedState)
        {
            string resolved = LeanS0HudParity.ResolveHealthDisplaySteadyState(
                healthNumber,
                currentHp,
                maxHp,
                boundShell,
                boundMaxHp,
                capturedState);

            Assert.AreEqual(expectedState, resolved);
        }

        [TestMethod]
        [DataRow(1, 0, false, false, "Init Wait", "Low Health")]
        [DataRow(1, 0, false, false, "Pre Init", "Low Health")]
        [DataRow(1, 0, false, false, "Wait", "Wait")]
        [DataRow(1, 0, false, true, "Low Health", "Idle")]
        [DataRow(1, 2, false, false, "Low Health", "Idle")]
        [DataRow(1, 0, true, false, "Low Health", "Idle")]
        [DataRow(5, 0, false, false, "Low Health", "Idle")]
        public void ResolveLowHealthFxSteadyState_EnforcesHealthyAndLowHealthRules(
            int health,
            int healthBlue,
            bool equippedJoni,
            bool isHudOut,
            string? capturedState,
            string expectedState)
        {
            string resolved = LeanS0HudParity.ResolveLowHealthFxSteadyState(
                health,
                healthBlue,
                equippedJoni,
                isHudOut,
                capturedState);

            Assert.AreEqual(expectedState, resolved);
        }

        [TestMethod]
        [DataRow(0, -10f)]
        [DataRow(1, -10f)]
        [DataRow(2, -0.589999974f + 2 * 0.0171000008f)]
        [DataRow(33, -0.589999974f + 33 * 0.0171000008f)]
        [DataRow(99, -0.589999974f + 99 * 0.0171000008f)]
        public void ComputeLiquidLocalY_MatchesSoulOrbThresholds(int mp, float expectedY)
        {
            Assert.AreEqual(expectedY, LeanS0HudParity.ComputeLiquidLocalY(mp), 1e-5f);
        }

        [TestMethod]
        [DataRow(1, 0, 99, false, true, true, false, "Empty", "Start Large", "V_Empty", 1f)]
        [DataRow(1, 8, 99, false, true, true, false, "Up To Quarter", "Start Large", "V_UpToQuarter", 1f)]
        [DataRow(1, 16, 99, false, true, true, false, "Up To Half", "Start Large", "V_UpToHalf", 1f)]
        [DataRow(1, 24, 99, false, true, true, false, "Up To 3Quarter", "Start Large", "V_UpTo3Quarter", 1f)]
        [DataRow(1, 33, 99, false, true, true, false, "Up To Full", "Start Small", "V_Full", 0.75f)]
        [DataRow(1, 33, 33, false, true, true, false, "Up To Full", "Start Large", "V_Full", 1f)]
        [DataRow(2, 66, 99, false, true, true, false, "Up To Full", "Start Small", "V_Full", 0.75f)]
        [DataRow(3, 66, 99, false, true, true, false, "Empty", "Start Large", "V_Empty", 1f)]
        [DataRow(3, 82, 99, false, true, true, false, "Up To Half", "Start Large", "V_UpToHalf", 1f)]
        [DataRow(3, 99, 99, false, true, true, false, "Up To Full", "Start Large", "V_Full", 1f)]
        [DataRow(4, 99, 99, false, false, false, false, "Not Obtained", "Start Small", null, 0.75f)]
        [DataRow(1, 33, 99, true, true, false, true, "Obtained", "Bound", null, 0.75f)]
        [DataRow(4, 0, 99, true, false, false, false, "Not obtained", "Bound", null, 0.75f)]
        public void ResolveVesselSteadyState_MatchesVesselOrbAndVesselSize(
            int vesselIndex,
            int reserveMp,
            int reserveMax,
            bool boundSoul,
            bool expectedObtained,
            bool expectedShowRenderer,
            bool expectedShowBindingCap,
            string expectedOrbState,
            string expectedSizeState,
            string? expectedClipName,
            float expectedScaleXY)
        {
            var resolved = LeanS0HudParity.ResolveVesselSteadyState(vesselIndex, reserveMp, reserveMax, boundSoul);

            Assert.AreEqual(expectedObtained, resolved.Obtained);
            Assert.AreEqual(expectedShowRenderer, resolved.ShowRenderer);
            Assert.AreEqual(expectedShowBindingCap, resolved.ShowBindingCap);
            Assert.AreEqual(expectedOrbState, resolved.OrbState);
            Assert.AreEqual(expectedSizeState, resolved.SizeState);
            Assert.AreEqual(expectedClipName, resolved.ClipName);
            Assert.AreEqual(expectedScaleXY, resolved.ScaleXY, 1e-5f);
        }

        [TestMethod]
        [DataRow(true, 1, false, 0, true)]
        [DataRow(true, 1, false, 2, true)]
        [DataRow(true, 1, true, 1, false)]
        [DataRow(true, 1, true, 0, true)]
        [DataRow(true, 2, false, 0, false)]
        [DataRow(false, 1, false, 0, false)]
        public void ShouldActivateFury_MatchesFuryAndJoniInteraction(
            bool equippedFury,
            int health,
            bool equippedJoni,
            int healthBlue,
            bool expected)
        {
            Assert.AreEqual(expected, LeanS0HudParity.ShouldActivateFury(equippedFury, health, equippedJoni, healthBlue));
        }

        [TestMethod]
        public void ReadBoundValueOrDefault_SkipsReaderWhenBindingInactive()
        {
            bool readerCalled = false;
            int inactiveValue = LeanS0HudParity.ReadBoundValueOrDefault(false, () =>
            {
                readerCalled = true;
                throw new System.NullReferenceException("Should not read BossSequenceController outside active binding");
            });

            Assert.IsFalse(readerCalled);
            Assert.AreEqual(0, inactiveValue);

            int activeValue = LeanS0HudParity.ReadBoundValueOrDefault(true, () => 13);
            Assert.AreEqual(13, activeValue);
        }

        [TestMethod]
        [DataRow(5, false, 0, false, false, 5, 2)]
        [DataRow(21, false, 0, true, false, 32, 16)]
        [DataRow(21, false, 13, false, false, 21, 10)]
        [DataRow(21, false, 13, true, false, 32, 16)]
        [DataRow(21, false, 13, true, true, 21, 10)]
        [DataRow(21, true, 13, true, false, 20, 10)]
        [DataRow(9, true, 13, false, false, 9, 4)]
        public void ComputeSlashAndBeamDamage_AppliesStrengthAndNailBinding(
            int rawNailDamage,
            bool boundNail,
            int boundNailDamage,
            bool equippedStrength,
            bool brokenStrength,
            int expectedFinalNail,
            int expectedBeam)
        {
            var result = LeanS0HudParity.ComputeSlashAndBeamDamage(
                rawNailDamage,
                boundNail,
                boundNailDamage,
                equippedStrength,
                brokenStrength);

            Assert.AreEqual(expectedFinalNail, result.FinalNailDamage);
            Assert.AreEqual(expectedBeam, result.BeamDamage);
        }

        [TestMethod]
        [DataRow(21, false, 21)]
        [DataRow(21, true, 32)]
        [DataRow(5, true, 8)]
        public void ComputeSharpShadowDamage_AppliesDashmasterMultiplier(
            int rawNailDamage,
            bool equippedDashmaster,
            int expectedDamage)
        {
            Assert.AreEqual(expectedDamage, LeanS0HudParity.ComputeSharpShadowDamage(rawNailDamage, equippedDashmaster));
        }

        [TestMethod]
        [DataRow(false, 33)]
        [DataRow(true, 24)]
        public void ComputeSpellCost_AppliesSpellTwister(bool equippedSpellTwister, int expectedCost)
        {
            Assert.AreEqual(expectedCost, LeanS0HudParity.ComputeSpellCost(equippedSpellTwister));
        }

        [TestMethod]
        [DataRow(5, "UI Appear")]
        [DataRow(4, "UI Appear")]
        [DataRow(3, "UI Break 1")]
        [DataRow(2, "UI Break 2")]
        [DataRow(1, "UI Break 3")]
        [DataRow(0, "UI Break 4")]
        [DataRow(-1, "UI Break 4")]
        public void ResolveBlockerHudClip_MapsHitsToClipName(int blockerHits, string expectedClip)
        {
            Assert.AreEqual(expectedClip, LeanS0HudParity.ResolveBlockerHudClip(blockerHits));
        }
    }
}
