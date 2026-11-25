using System;
using System.Reflection;
using System.Runtime.Serialization;
using HollowKnightTAS.Core.SceneCheckpoints;
using HollowKnightTAS.Runtime.FullRun;
using HutongGames.PlayMaker;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UnityEngine;

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

        [TestMethod]
        public void NormalizeHeroController_ClearsResidualCombatAndEnvironmentCStateFlags()
        {
#pragma warning disable SYSLIB0050
            var hero = (HeroController)FormatterServices.GetUninitializedObject(typeof(HeroController));
#pragma warning restore SYSLIB0050
            hero.cState = new HeroControllerStates
            {
                facingRight = true,
                transitioning = true,
                falling = true,
                recoiling = true,
                recoilFrozen = true,
                casting = true,
                castRecoiling = true,
                wallJumping = true,
                superDashing = true,
                superDashOnWall = true,
                spellQuake = true,
                focusing = true,
                lookingUp = true,
                lookingDown = true,
                lookingUpAnim = true,
                lookingDownAnim = true,
                dead = true,
                hazardDeath = true,
                hazardRespawning = true,
                preventDash = true,
                preventBackDash = true,
                dashCooldown = true,
                backDashCooldown = true,
                freezeCharge = true,
                inAcid = true,
                swimming = true,
                onConveyor = true,
                onConveyorV = true,
                inConveyorZone = true,
                slidingLeft = true,
                slidingRight = true,
                touchingNonSlider = true,
                nearBench = true,
                inWalkZone = true
            };

            var receipt = new LeanS0GateReceipt();
            LeanS0Gate.NormalizeHeroController(hero, receipt);

            Assert.IsTrue(hero.cState.facingRight);
            Assert.IsTrue(hero.cState.transitioning);
            Assert.IsFalse(hero.cState.falling);
            Assert.IsFalse(hero.cState.recoiling);
            Assert.IsFalse(hero.cState.recoilFrozen);
            Assert.IsFalse(hero.cState.casting);
            Assert.IsFalse(hero.cState.castRecoiling);
            Assert.IsFalse(hero.cState.wallJumping);
            Assert.IsFalse(hero.cState.superDashing);
            Assert.IsFalse(hero.cState.superDashOnWall);
            Assert.IsFalse(hero.cState.spellQuake);
            Assert.IsFalse(hero.cState.focusing);
            Assert.IsFalse(hero.cState.lookingUp);
            Assert.IsFalse(hero.cState.lookingDown);
            Assert.IsFalse(hero.cState.lookingUpAnim);
            Assert.IsFalse(hero.cState.lookingDownAnim);
            Assert.IsFalse(hero.cState.dead);
            Assert.IsFalse(hero.cState.hazardDeath);
            Assert.IsFalse(hero.cState.hazardRespawning);
            Assert.IsFalse(hero.cState.preventDash);
            Assert.IsFalse(hero.cState.preventBackDash);
            Assert.IsFalse(hero.cState.dashCooldown);
            Assert.IsFalse(hero.cState.backDashCooldown);
            Assert.IsFalse(hero.cState.freezeCharge);
            Assert.IsFalse(hero.cState.inAcid);
            Assert.IsFalse(hero.cState.swimming);
            Assert.IsFalse(hero.cState.onConveyor);
            Assert.IsFalse(hero.cState.onConveyorV);
            Assert.IsFalse(hero.cState.inConveyorZone);
            Assert.IsFalse(hero.cState.slidingLeft);
            Assert.IsFalse(hero.cState.slidingRight);
            Assert.IsFalse(hero.cState.touchingNonSlider);
            Assert.IsFalse(hero.cState.nearBench);
            Assert.IsFalse(hero.cState.inWalkZone);
        }

        [TestMethod]
        public void NormalizeHeroController_ClearsTransientFocusAttackRecoilAndEffectReferences()
        {
#pragma warning disable SYSLIB0050
            var hero = (HeroController)FormatterServices.GetUninitializedObject(typeof(HeroController));
#pragma warning restore SYSLIB0050
            hero.cState = new HeroControllerStates { attacking = false };

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            Type hc = typeof(HeroController);
            hc.GetField("drainMP", flags)!.SetValue(hero, true);
            hc.GetField("drainMP_timer", flags)!.SetValue(hero, 0.12f);
            hc.GetField("drainMP_time", flags)!.SetValue(hero, 0.025f);
            hc.GetField("MP_drained", flags)!.SetValue(hero, 18f);
            hc.GetField("drainMP_seconds", flags)!.SetValue(hero, 1.45f);
            hc.GetField("focusMP_amount", flags)!.SetValue(hero, 33f);
            hc.GetField("attackDuration", flags)!.SetValue(hero, 0.35f);
            hc.GetField("recoilVector", flags)!.SetValue(hero, new UnityEngine.Vector2(-15f, 7.5f));

            var receipt = new LeanS0GateReceipt();
            LeanS0Gate.NormalizeHeroController(hero, receipt);

            Assert.IsFalse((bool)hc.GetField("drainMP", flags)!.GetValue(hero)!);
            Assert.AreEqual(0f, (float)hc.GetField("drainMP_timer", flags)!.GetValue(hero)!);
            Assert.AreEqual(0f, (float)hc.GetField("drainMP_time", flags)!.GetValue(hero)!);
            Assert.AreEqual(0f, (float)hc.GetField("MP_drained", flags)!.GetValue(hero)!);
            Assert.AreEqual(0f, (float)hc.GetField("drainMP_seconds", flags)!.GetValue(hero)!);
            Assert.AreEqual(0f, (float)hc.GetField("focusMP_amount", flags)!.GetValue(hero)!);
            Assert.AreEqual(0f, (float)hc.GetField("attackDuration", flags)!.GetValue(hero)!);
            Assert.AreEqual(UnityEngine.Vector2.zero, (UnityEngine.Vector2)hc.GetField("recoilVector", flags)!.GetValue(hero)!);
            Assert.IsNull(hc.GetField("dashEffect", flags)!.GetValue(hero));
            Assert.IsNull(hc.GetField("takeDamageCoroutine", flags)!.GetValue(hero));
        }

        [TestMethod]
        public void RandomSeedSync_PreservesS0SeedAcrossBootstrapAndLoadingUntilTargetSceneReady()
        {
            // Target S0 restore starts with bootstrap handle 101 (e.g. GG_Workshop) and leanS0RandomSeededForTargetScene=true
            // while loading (!ready).
            int trackedHandle = 101;
            string trackedName = "GG_Vengefly";
            bool handshakePending = false;
            bool s0Seeded = true;

            // Bootstrap loading frames (!ready) must NOT consume s0Seeded or lock trackedHandle to GG_Workshop.
            Assert.IsFalse(LeanS0RandomSeedSync.ShouldRequestExternalHandshake(
                leanS0ModeEnabled: false, ready: false, sceneHandle: 101, sceneName: "GG_Workshop",
                seededTargetSceneName: "GG_Gruz_Mother",
                ref trackedHandle, ref trackedName, ref handshakePending, ref s0Seeded));
            Assert.IsTrue(s0Seeded);
            Assert.AreEqual(101, trackedHandle);

            // Target scene becomes active while hero is still loading (!ready): consumes s0Seeded and syncs target handle 202.
            Assert.IsFalse(LeanS0RandomSeedSync.ShouldRequestExternalHandshake(
                leanS0ModeEnabled: false, ready: false, sceneHandle: 202, sceneName: "GG_Gruz_Mother",
                seededTargetSceneName: "GG_Gruz_Mother",
                ref trackedHandle, ref trackedName, ref handshakePending, ref s0Seeded));
            Assert.IsFalse(s0Seeded);
            Assert.AreEqual(202, trackedHandle);
            Assert.AreEqual("GG_Gruz_Mother", trackedName);

            // Subsequent ready frames in GG_Gruz_Mother must NOT request RandomSeedHandshake.
            Assert.IsFalse(LeanS0RandomSeedSync.ShouldRequestExternalHandshake(
                leanS0ModeEnabled: false, ready: true, sceneHandle: 202, sceneName: "GG_Gruz_Mother",
                seededTargetSceneName: "GG_Gruz_Mother",
                ref trackedHandle, ref trackedName, ref handshakePending, ref s0Seeded));
        }

        [TestMethod]
        public void RandomSeedSync_ConsumesCutsceneS0SeedSoNextNonSeededLevelStillRequestsHandshake()
        {
            // Menu_Title (10) -> Opening_Sequence (20, never ready) -> Tutorial_01 (30, skips S0Gate because IsFirstLevelForPlayer).
            int trackedHandle = 10;
            string trackedName = "Menu_Title";
            bool handshakePending = false;
            bool s0Seeded = true;

            Assert.IsFalse(LeanS0RandomSeedSync.ShouldRequestExternalHandshake(
                leanS0ModeEnabled: false, ready: false, sceneHandle: 20, sceneName: "Opening_Sequence",
                seededTargetSceneName: "Opening_Sequence",
                ref trackedHandle, ref trackedName, ref handshakePending, ref s0Seeded));
            Assert.IsFalse(s0Seeded);
            Assert.AreEqual(20, trackedHandle);

            Assert.IsTrue(LeanS0RandomSeedSync.ShouldRequestExternalHandshake(
                leanS0ModeEnabled: false, ready: true, sceneHandle: 30, sceneName: "Tutorial_01",
                seededTargetSceneName: "Opening_Sequence",
                ref trackedHandle, ref trackedName, ref handshakePending, ref s0Seeded));
        }

        [TestMethod]
        public void RandomSeedSync_StillRequestsHandshakeAtTitleStartupWithoutS0Seed()
        {
            int trackedHandle = -1;
            string trackedName = string.Empty;
            bool handshakePending = false;
            bool s0Seeded = false;

            Assert.IsFalse(LeanS0RandomSeedSync.ShouldRequestExternalHandshake(
                leanS0ModeEnabled: false, ready: false, sceneHandle: 10, sceneName: "Menu_Title",
                seededTargetSceneName: string.Empty,
                ref trackedHandle, ref trackedName, ref handshakePending, ref s0Seeded));

            Assert.IsTrue(LeanS0RandomSeedSync.ShouldRequestExternalHandshake(
                leanS0ModeEnabled: false, ready: true, sceneHandle: 10, sceneName: "Menu_Title",
                seededTargetSceneName: string.Empty,
                ref trackedHandle, ref trackedName, ref handshakePending, ref s0Seeded));
        }

        [TestMethod]
        public void NormalizeSpellControlVariables_ClearsTransientVarsWithoutTouchingCharmConfig()
        {
            var vars = new FsmVariables
            {
                BoolVariables = new[]
                {
                    new FsmBool("Return Bool") { Value = true },
                    new FsmBool("Refocusing") { Value = true },
                    new FsmBool("Spore Cooldown") { Value = true }
                },
                FloatVariables = new[]
                {
                    new FsmFloat("Grace Timer") { Value = 0.74f },
                    new FsmFloat("Fireball Recoil Current") { Value = 2f },
                    new FsmFloat("Y Speed") { Value = -50f },
                    new FsmFloat("Self X Scale") { Value = 1f },
                    new FsmFloat("Camera Start Z") { Value = -38.1f },
                    new FsmFloat("Time Per MP Drain") { Value = 0.027f }
                },
                IntVariables = new[]
                {
                    new FsmInt("Start MP") { Value = 57 },
                    new FsmInt("MP") { Value = 31 },
                    new FsmInt("HP") { Value = 3 },
                    new FsmInt("Max HP") { Value = 9 },
                    new FsmInt("Health Increase") { Value = 1 },
                    new FsmInt("Spell Level") { Value = 2 },
                    new FsmInt("Focus MP amount") { Value = 33 },
                    new FsmInt("MP Cost") { Value = 24 }
                },
                Vector3Variables = new[]
                {
                    new FsmVector3("Fireball Recoil Vector") { Value = new Vector3(1f, 0f, 0f) },
                    new FsmVector3("Focus Camera Zoom Current") { Value = new Vector3(0f, 0f, 3f) },
                    new FsmVector3("Camera Pos") { Value = new Vector3(94.2f, 19.9f, -38.1f) },
                    new FsmVector3("Focus Camera Zoom") { Value = new Vector3(0f, 0f, 3f) }
                }
            };

            bool changed = LeanS0Gate.NormalizeSpellControlVariables(vars, isInactive: true, checkUnityLifetime: false);
            Assert.IsTrue(changed);

            Assert.IsFalse(vars.FindFsmBool("Return Bool").Value);
            Assert.IsFalse(vars.FindFsmBool("Refocusing").Value);
            Assert.AreEqual(0f, vars.FindFsmFloat("Grace Timer").Value);
            Assert.AreEqual(0f, vars.FindFsmFloat("Fireball Recoil Current").Value);
            Assert.AreEqual(0f, vars.FindFsmFloat("Y Speed").Value);
            Assert.AreEqual(0f, vars.FindFsmFloat("Self X Scale").Value);
            Assert.AreEqual(0f, vars.FindFsmFloat("Camera Start Z").Value);
            Assert.AreEqual(0, vars.FindFsmInt("Start MP").Value);
            Assert.AreEqual(0, vars.FindFsmInt("MP").Value);
            Assert.AreEqual(0, vars.FindFsmInt("HP").Value);
            Assert.AreEqual(0, vars.FindFsmInt("Max HP").Value);
            Assert.AreEqual(0, vars.FindFsmInt("Health Increase").Value);
            Assert.AreEqual(Vector3.zero, vars.FindFsmVector3("Fireball Recoil Vector").Value);
            Assert.AreEqual(Vector3.zero, vars.FindFsmVector3("Focus Camera Zoom Current").Value);
            Assert.AreEqual(Vector3.zero, vars.FindFsmVector3("Camera Pos").Value);

            // Charm-derived config variables must remain untouched.
            Assert.IsTrue(vars.FindFsmBool("Spore Cooldown").Value);
            Assert.AreEqual(0.027f, vars.FindFsmFloat("Time Per MP Drain").Value);
            Assert.AreEqual(2, vars.FindFsmInt("Spell Level").Value);
            Assert.AreEqual(33, vars.FindFsmInt("Focus MP amount").Value);
            Assert.AreEqual(24, vars.FindFsmInt("MP Cost").Value);
            Assert.AreEqual(new Vector3(0f, 0f, 3f), vars.FindFsmVector3("Focus Camera Zoom").Value);
        }
    }
}
