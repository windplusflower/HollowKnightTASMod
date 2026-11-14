using System;
using System.Reflection;
using System.Runtime.Serialization;
using HollowKnightTAS.Core.SceneCheckpoints;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    public sealed class LeanS0GateTests
    {
        [TestMethod]
        public void ComputeBoundarySeed_IsDeterministicAndDistinguishesTupleFields()
        {
            int s1 = LeanS0Seed.ComputeBoundarySeed(8, "GG_Lost_Kin", "door_dreamEnter");
            int s2 = LeanS0Seed.ComputeBoundarySeed(8, "GG_Lost_Kin", "door_dreamEnter");
            Assert.AreEqual(s1, s2);
            Assert.AreNotEqual(0, s1);

            Assert.AreNotEqual(s1, LeanS0Seed.ComputeBoundarySeed(7, "GG_Lost_Kin", "door_dreamEnter"));
            Assert.AreNotEqual(s1, LeanS0Seed.ComputeBoundarySeed(8, "GG_Atrium", "door_dreamEnter"));
            Assert.AreNotEqual(s1, LeanS0Seed.ComputeBoundarySeed(8, "GG_Lost_Kin", "left1"));
        }

        [TestMethod]
        public void Execute_AppliesSeedToRngCallbackWhenUnityRuntimeDisabled()
        {
            int? observedSeed = null;
            var receipt = LeanS0Gate.Execute(
                8,
                "GG_Lost_Kin",
                "door_dreamEnter",
                applyUnityRuntime: false,
                overrideRngInit: seed => observedSeed = seed,
                rngDigestReader: () => "deadbeef");

            Assert.AreEqual(LeanS0Seed.ComputeBoundarySeed(8, "GG_Lost_Kin", "door_dreamEnter"), observedSeed);
            Assert.AreEqual(observedSeed, receipt.Seed);
            Assert.AreEqual("deadbeef", receipt.RngSha256);
        }

        [TestMethod]
        public void NormalizeHeroController_ClearsTransientStatesAndStaleBossDamageDelegates()
        {
#pragma warning disable SYSLIB0050
            var hero = (HeroController)FormatterServices.GetUninitializedObject(typeof(HeroController));
            var boss = (BossSceneController)FormatterServices.GetUninitializedObject(typeof(BossSceneController));
#pragma warning restore SYSLIB0050
            hero.cState = new HeroControllerStates
            {
                altAttack = true,
                invulnerable = true,
                nailCharging = true,
                facingRight = false
            };

            FieldInfo altAttackTimeField = typeof(HeroController).GetField("altAttackTime", BindingFlags.Instance | BindingFlags.NonPublic)!;
            altAttackTimeField.SetValue(hero, 92.836f);

            int liveCalls = 0;
            HeroController.TakeDamageEvent liveDelegate = () => liveCalls++;
            MethodInfo setDamagedMethod = typeof(BossSceneController).GetMethod("SetKnightDamaged", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var staleBossDelegate = (HeroController.TakeDamageEvent)Delegate.CreateDelegate(typeof(HeroController.TakeDamageEvent), boss, setDamagedMethod);
            hero.OnTakenDamage += liveDelegate;
            hero.OnTakenDamage += staleBossDelegate;

            var receipt = new LeanS0GateReceipt();
            LeanS0Gate.NormalizeHeroController(hero, receipt);

            Assert.IsFalse(hero.cState.altAttack);
            Assert.IsFalse(hero.cState.invulnerable);
            Assert.IsFalse(hero.cState.nailCharging);
            Assert.IsFalse(hero.cState.facingRight); // Must be preserved, not wiped by S0Gate
            Assert.AreEqual(0f, (float)altAttackTimeField.GetValue(hero)!);
            Assert.IsTrue(receipt.NormalizedAltAttack);
            Assert.IsTrue(receipt.NormalizedInvulnerable);
            Assert.IsTrue(receipt.NormalizedNailCharging);
            Assert.AreEqual(1, receipt.RemovedStaleDamageDelegates);

            FieldInfo backingField = typeof(HeroController).GetField("OnTakenDamage", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var remaining = (HeroController.TakeDamageEvent?)backingField.GetValue(hero);
            Assert.IsNotNull(remaining);
            Assert.AreEqual(1, remaining!.GetInvocationList().Length);
            remaining();
            Assert.AreEqual(1, liveCalls);
        }
    }
}
