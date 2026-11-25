using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using GlobalEnums;
using HollowKnightTAS.Core.SceneCheckpoints;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HollowKnightTAS.Runtime.FullRun
{
    /// <summary>
    /// Summary emitted by <see cref="LeanS0Gate"/> after normalizing transient boundary state and reseeding RNG.
    /// </summary>
    internal sealed class LeanS0GateReceipt
    {
        public int BoundaryOrdinal { get; set; }
        public string TargetSceneName { get; set; } = string.Empty;
        public string EntryGateName { get; set; } = string.Empty;
        public int Seed { get; set; }
        public string RngSha256 { get; set; } = string.Empty;
        public int RemovedStaleDamageDelegates { get; set; }
        public int RecycledLevelUnloadObjects { get; set; }
        public int ResetObjectPoolInstances { get; set; }
        public int ResetHeroInactiveLifecycles { get; set; }
        public int SweptStaleITweens { get; set; }
        public bool NormalizedAltAttack { get; set; }
        public bool NormalizedInvulnerable { get; set; }
        public bool NormalizedNailCharging { get; set; }
        public bool NormalizedDreamNailQueueTimer { get; set; }
        public bool NormalizedSpellControlFsm { get; set; }
        public bool NormalizedAudioManagerFsms { get; set; }
        public bool NormalizedSoulOrbFsm { get; set; }
        public bool NormalizedLowHealthFsm { get; set; }
        public bool NormalizedCameraShakeFsm { get; set; }
        public bool NormalizedHeroLightFsm { get; set; }
        public bool NormalizedCharmEffectFsms { get; set; }
    }

    /// <summary>
    /// Unified S0 gate shared across recording, normal replay, and snapshot restoration paths.
    /// </summary>
    internal static class LeanS0Gate
    {
        private static readonly FieldInfo? TweenVectorsField = typeof(iTween).GetField(
            "vector3s",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo? ShakeNextUpdateTimeField = typeof(HutongGames.PlayMaker.Actions.ShakePositionV2).GetField(
            "nextUpdateTime",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo? ShakeTimerField = typeof(HutongGames.PlayMaker.Actions.ShakePositionV2).GetField(
            "timer",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo? ShakeTargetField = typeof(HutongGames.PlayMaker.Actions.ShakePositionV2).GetField(
            "target",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo? ShakeStartingPosField = typeof(HutongGames.PlayMaker.Actions.ShakePositionV2).GetField(
            "startingWorldPosition",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo? FsmPreviousActiveStateField = typeof(Fsm).GetField(
            "previousActiveState",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo? FsmLastTransitionField = typeof(Fsm).GetField(
            "<LastTransition>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo? FsmStateActiveField = typeof(FsmState).GetField(
            "active",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo? FsmStateFinishedField = typeof(FsmState).GetField(
            "finished",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo? FsmStateFinishedActionsField = typeof(FsmState).GetField(
            "_finishedActions",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo? FsmStateActiveActionField = typeof(FsmState).GetField(
            "activeAction",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo? FsmStateActiveActionIndexField = typeof(FsmState).GetField(
            "activeActionIndex",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo? OnTakenDamageBackingField = typeof(HeroController).GetField(
            "OnTakenDamage",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo? AltAttackTimeField = typeof(HeroController).GetField(
            "altAttackTime",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo? FsmDelayedEventsField = typeof(Fsm).GetField(
            "delayedEvents",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo? FsmUpdateEventsField = typeof(Fsm).GetField(
            "updateEvents",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo? FsmRemoveEventsField = typeof(Fsm).GetField(
            "removeEvents",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo? TweenActionEventsField = typeof(HutongGames.PlayMaker.Actions.iTweenFsmAction).GetField(
            "itweenEvents",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo? WaitActionTimerField = typeof(HutongGames.PlayMaker.Actions.Wait).GetField(
            "timer",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo? AttackDurationField = typeof(HeroController).GetField(
            "attackDuration",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo? InvPulseNormalColorField = typeof(InvulnerablePulse).GetField("normalColor", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo? InvPulseSpriteField = typeof(InvulnerablePulse).GetField("sprite", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo? InvPulsePulsingField = typeof(InvulnerablePulse).GetField("pulsing", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo? InvPulseReverseField = typeof(InvulnerablePulse).GetField("reverse", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo? InvPulseCurrentLerpTimeField = typeof(InvulnerablePulse).GetField("currentLerpTime", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo? SpriteFlashRendField = typeof(SpriteFlash).GetField("rend", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo? SpriteFlashColourField = typeof(SpriteFlash).GetField("flashColour", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo? SpriteFlashAmountField = typeof(SpriteFlash).GetField("amount", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo? SpriteFlashTimeUpField = typeof(SpriteFlash).GetField("timeUp", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo? SpriteFlashStayTimeField = typeof(SpriteFlash).GetField("stayTime", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo? SpriteFlashTimeDownField = typeof(SpriteFlash).GetField("timeDown", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo? SpriteFlashStateField = typeof(SpriteFlash).GetField("flashingState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo? SpriteFlashTimerField = typeof(SpriteFlash).GetField("flashTimer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo? SpriteFlashAmountCurrentField = typeof(SpriteFlash).GetField("amountCurrent", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo? SpriteFlashTField = typeof(SpriteFlash).GetField("t", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo? SpriteFlashRepeatField = typeof(SpriteFlash).GetField("repeatFlash", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo? SpriteFlashCancelField = typeof(SpriteFlash).GetField("cancelFlash", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo? SpriteFlashGeoTimerField = typeof(SpriteFlash).GetField("geoTimer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo? SpriteFlashGeoFlashField = typeof(SpriteFlash).GetField("geoFlash", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo? SpriteFlashBlockField = typeof(SpriteFlash).GetField("block", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo? SpriteFlashSendToChildrenField = typeof(SpriteFlash).GetField("sendToChildren", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly (FieldInfo? Field, object? ZeroValue)[] TransientHeroResetFields =
            BuildTransientHeroResetFields();

        private static (FieldInfo? Field, object? ZeroValue)[] BuildTransientHeroResetFields()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            Type heroType = typeof(HeroController);
            var specs = new (string Name, object? ZeroValue)[]
            {
                ("wallUnstickSteps", 0),
                ("wallLockSteps", 0),
                ("wallJumpedL", false),
                ("wallJumpedR", false),
                ("currentWalljumpSpeed", 0f),
                ("walljumpSpeedDecel", 0f),
                ("wallSlashing", false),
                ("wallslideClipTimer", 0f),
                ("playingWallslideClip", false),
                ("playedMantisClawClip", false),
                ("jumpQueueSteps", 0),
                ("jumpQueuing", false),
                ("doubleJumpQueueSteps", 0),
                ("doubleJumpQueuing", false),
                ("jumpReleaseQueueSteps", 0),
                ("jumpReleaseQueuing", false),
                ("attackQueueSteps", 0),
                ("attackQueuing", false),
                ("dashQueueSteps", 0),
                ("dashQueuing", false),
                ("landingBufferSteps", 0),
                ("ledgeBufferSteps", 0),
                ("headBumpSteps", 0),
                ("drainMP", false),
                ("drainMP_timer", 0f),
                ("drainMP_time", 0f),
                ("MP_drained", 0f),
                ("drainMP_seconds", 0f),
                ("focusMP_amount", 0f),
                ("recoilVector", Vector2.zero),
                ("dashEffect", null),
                ("takeDamageCoroutine", null)
            };
            var result = new (FieldInfo? Field, object? ZeroValue)[specs.Length];
            for (int i = 0; i < specs.Length; i++)
            {
                result[i] = (heroType.GetField(specs[i].Name, flags), specs[i].ZeroValue);
            }
            return result;
        }

        internal static LeanS0GateReceipt Execute(
            int boundaryOrdinal,
            string targetSceneName,
            string? entryGateName,
            bool applyUnityRuntime = true,
            Action<int>? overrideRngInit = null,
            Func<string>? rngDigestReader = null,
            Action<LeanS0GateReceipt, HeroController?>? boundaryEnvironmentReset = null)
        {
            string normalizedGate = entryGateName ?? string.Empty;
            int seed = LeanS0Seed.ComputeBoundarySeed(boundaryOrdinal, targetSceneName, normalizedGate);

            var receipt = new LeanS0GateReceipt
            {
                BoundaryOrdinal = boundaryOrdinal,
                TargetSceneName = targetSceneName,
                EntryGateName = normalizedGate,
                Seed = seed
            };

            if (applyUnityRuntime)
            {
                ExecuteUnityRuntimePhase(receipt, boundaryEnvironmentReset);
            }

            if (overrideRngInit != null)
            {
                overrideRngInit(seed);
            }
            else if (applyUnityRuntime)
            {
                InitUnityRandomSeed(seed);
            }

            if (rngDigestReader != null)
            {
                receipt.RngSha256 = rngDigestReader() ?? string.Empty;
            }

            return receipt;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void InitUnityRandomSeed(int seed)
            => UnityEngine.Random.InitState(seed);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ExecuteUnityRuntimePhase(
            LeanS0GateReceipt receipt,
            Action<LeanS0GateReceipt, HeroController?>? boundaryEnvironmentReset)
        {
            HeroController? hero = HeroController.SilentInstance;
            if (hero != null)
            {
                NormalizeHeroController(hero, receipt, hasActiveBossScene: BossSceneController.Instance != null, checkUnityLifetime: true);
                NormalizeHeroFsms(hero, receipt);
                NormalizeHeroLightFsms(hero, receipt);
            }

            NormalizeHudFsms(receipt);
            NormalizeAudioManagerFsms(receipt);
            NormalizeCameraShakeFsm(receipt);
            receipt.RecycledLevelUnloadObjects = RecycleLevelUnloadPooledObjects();
            Action<LeanS0GateReceipt, HeroController?> resetHandler = boundaryEnvironmentReset
                ?? throw new InvalidOperationException("LeanS0Gate boundary environment reset handler is required when applyUnityRuntime is true.");
            resetHandler(receipt, hero);

            if (hero != null)
            {
                NormalizeCharmEffectFsms(hero, receipt);
            }

            receipt.SweptStaleITweens += SweepStaleITweens();
        }

        internal static void NormalizeHeroController(
            HeroController hero,
            LeanS0GateReceipt receipt,
            bool hasActiveBossScene = false,
            bool checkUnityLifetime = false)
        {
            if (ReferenceEquals(hero, null)) throw new ArgumentNullException(nameof(hero));
            if (ReferenceEquals(receipt, null)) throw new ArgumentNullException(nameof(receipt));

            receipt.RemovedStaleDamageDelegates += PruneStaleOnTakenDamageDelegates(hero, hasActiveBossScene, checkUnityLifetime);

            if (hero.cState != null)
            {
                if (hero.cState.altAttack)
                {
                    hero.cState.altAttack = false;
                    receipt.NormalizedAltAttack = true;
                }
                if (hero.cState.invulnerable)
                {
                    hero.cState.invulnerable = false;
                    receipt.NormalizedInvulnerable = true;
                }
                if (hero.cState.nailCharging)
                {
                    hero.cState.nailCharging = false;
                    receipt.NormalizedNailCharging = true;
                }

                // Reset transient combat/environment flags that LeaveScene/EnterScene omit,
                // so normal transitions and in-process/cold S0 restores share the same boundary state.
                hero.cState.falling = false;
                hero.cState.recoiling = false;
                hero.cState.recoilFrozen = false;
                hero.cState.casting = false;
                hero.cState.castRecoiling = false;
                hero.cState.wallJumping = false;
                hero.cState.superDashing = false;
                hero.cState.superDashOnWall = false;
                hero.cState.spellQuake = false;
                hero.cState.focusing = false;
                hero.cState.lookingUp = false;
                hero.cState.lookingDown = false;
                hero.cState.lookingUpAnim = false;
                hero.cState.lookingDownAnim = false;
                hero.cState.dead = false;
                hero.cState.hazardDeath = false;
                hero.cState.hazardRespawning = false;
                hero.cState.preventDash = false;
                hero.cState.preventBackDash = false;
                hero.cState.dashCooldown = false;
                hero.cState.backDashCooldown = false;
                hero.cState.freezeCharge = false;
                hero.cState.inAcid = false;
                hero.cState.swimming = false;
                hero.cState.onConveyor = false;
                hero.cState.onConveyorV = false;
                hero.cState.inConveyorZone = false;
                hero.cState.slidingLeft = false;
                hero.cState.slidingRight = false;
                hero.cState.touchingNonSlider = false;
                hero.cState.nearBench = false;
                hero.cState.inWalkZone = false;
            }

            hero.inAcid = false;
            hero.touchingWallL = false;
            hero.touchingWallR = false;
            hero.wallSlidingL = false;
            hero.wallSlidingR = false;
            hero.wallLocked = false;
            for (int i = 0; i < TransientHeroResetFields.Length; i++)
            {
                TransientHeroResetFields[i].Field?.SetValue(hero, TransientHeroResetFields[i].ZeroValue);
            }

            if (AttackDurationField != null && (hero.cState == null || !hero.cState.attacking))
            {
                AttackDurationField.SetValue(hero, 0f);
            }

            if (AltAttackTimeField != null)
            {
                float current = (float)(AltAttackTimeField.GetValue(hero) ?? 0f);
                if (current != 0f)
                {
                    AltAttackTimeField.SetValue(hero, 0f);
                    receipt.NormalizedAltAttack = true;
                }
            }

            if (checkUnityLifetime)
            {
                NormalizeHeroPulseAndFlash(hero);
            }
        }

        private static void NormalizeHeroPulseAndFlash(HeroController hero)
        {
            InvulnerablePulse? invPulse = hero.GetComponent<InvulnerablePulse>();
            if (invPulse != null)
            {
                bool wasPulsing = InvPulsePulsingField?.GetValue(invPulse) is bool p && p;
                if (wasPulsing
                    && InvPulseSpriteField?.GetValue(invPulse) is tk2dSprite sprite
                    && sprite != null
                    && InvPulseNormalColorField?.GetValue(invPulse) is Color normalColor)
                {
                    sprite.color = normalColor;
                }

                InvPulsePulsingField?.SetValue(invPulse, false);
                InvPulseReverseField?.SetValue(invPulse, false);
                InvPulseCurrentLerpTimeField?.SetValue(invPulse, 0f);
            }

            SpriteFlash? spriteFlash = hero.GetComponent<SpriteFlash>();
            if (spriteFlash != null)
            {
                Renderer? rend = SpriteFlashRendField?.GetValue(spriteFlash) as Renderer;
                if (rend == null)
                {
                    rend = hero.GetComponent<Renderer>();
                    if (rend != null)
                    {
                        SpriteFlashRendField?.SetValue(spriteFlash, rend);
                    }
                }

                if (SpriteFlashBlockField?.GetValue(spriteFlash) is MaterialPropertyBlock block && rend != null)
                {
                    block.SetFloat("_FlashAmount", 0f);
                    rend.SetPropertyBlock(block);
                }

                SpriteFlashStateField?.SetValue(spriteFlash, 0);
                SpriteFlashTimerField?.SetValue(spriteFlash, 0f);
                SpriteFlashAmountCurrentField?.SetValue(spriteFlash, 0f);
                SpriteFlashTField?.SetValue(spriteFlash, 0f);
                SpriteFlashRepeatField?.SetValue(spriteFlash, false);
                SpriteFlashCancelField?.SetValue(spriteFlash, false);
                SpriteFlashGeoTimerField?.SetValue(spriteFlash, 0f);
                SpriteFlashGeoFlashField?.SetValue(spriteFlash, false);
                SpriteFlashColourField?.SetValue(spriteFlash, default(Color));
                SpriteFlashAmountField?.SetValue(spriteFlash, 0f);
                SpriteFlashTimeUpField?.SetValue(spriteFlash, 0f);
                SpriteFlashStayTimeField?.SetValue(spriteFlash, 0f);
                SpriteFlashTimeDownField?.SetValue(spriteFlash, 0f);
                SpriteFlashSendToChildrenField?.SetValue(spriteFlash, true);
            }
        }

        internal static int PruneStaleOnTakenDamageDelegates(
            HeroController hero,
            bool hasActiveBossScene,
            bool checkUnityLifetime = false)
        {
            if (ReferenceEquals(hero, null) || OnTakenDamageBackingField == null)
            {
                return 0;
            }

            if (!(OnTakenDamageBackingField.GetValue(hero) is Delegate current))
            {
                return 0;
            }

            int removed = 0;
            var kept = new List<Delegate>();
            foreach (Delegate entry in current.GetInvocationList())
            {
                if (IsStaleOnTakenDamageDelegate(entry, hasActiveBossScene, checkUnityLifetime))
                {
                    removed++;
                }
                else
                {
                    kept.Add(entry);
                }
            }

            if (removed > 0)
            {
                Delegate? replacement = kept.Count == 0 ? null : Delegate.Combine(kept.ToArray());
                OnTakenDamageBackingField.SetValue(hero, replacement);
            }

            return removed;
        }

        internal static bool IsStaleOnTakenDamageDelegate(
            Delegate entry,
            bool hasActiveBossScene,
            bool checkUnityLifetime = false)
        {
            object? target = entry.Target;
            if (checkUnityLifetime && target is UnityEngine.Object unityTarget && IsUnityObjectDestroyed(unityTarget))
            {
                return true;
            }

            // BossSceneController registers SetKnightDamaged on OnTakenDamage without unsubscribing in OnDestroy.
            if (!hasActiveBossScene && target is BossSceneController)
            {
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsUnityObjectDestroyed(UnityEngine.Object unityTarget)
            => unityTarget == null;

        private static readonly string[] SpellControlTransientBools =
        {
            "Return Bool",
            "Focusing",
            "Refocusing",
            "Double",
            "Didnt Hit Full HP",
            "Dream End",
            "Dream Focus",
            "Is In Dream Focus"
        };

        private static readonly string[] SpellControlTransientFloats =
        {
            "Grace Timer",
            "Fireball Recoil Current",
            "Y Speed",
            "Self X Scale",
            "Camera Start Z",
            "Quake Antic Speed"
        };

        private static readonly string[] SpellControlTransientInts =
        {
            "Start MP",
            "MP",
            "HP",
            "Max HP",
            "Health Increase"
        };

        private static readonly string[] SpellControlTransientVector3s =
        {
            "Fireball Recoil Vector",
            "Focus Camera Zoom Current",
            "Quake Antic Vector",
            "Camera Pos"
        };

        internal static void NormalizeHeroFsms(HeroController hero, LeanS0GateReceipt receipt)
        {
            if (hero == null || receipt == null)
            {
                return;
            }

            PlayMakerFSM[] fsms = hero.GetComponents<PlayMakerFSM>();
            foreach (PlayMakerFSM fsm in fsms)
            {
                if (fsm == null)
                {
                    continue;
                }

                if (string.Equals(fsm.FsmName, "Dream Nail", StringComparison.Ordinal))
                {
                    FsmFloat? queueTimer = fsm.FsmVariables?.FindFsmFloat("Queue Timer");
                    if (queueTimer != null && queueTimer.Value != 0f)
                    {
                        queueTimer.Value = 0f;
                        receipt.NormalizedDreamNailQueueTimer = true;
                    }
                }
                else if (string.Equals(fsm.FsmName, "Spell Control", StringComparison.Ordinal))
                {
                    bool isInactive = string.Equals(fsm.ActiveStateName, "Inactive", StringComparison.Ordinal);
                    if (NormalizeSpellControlVariables(fsm.FsmVariables, isInactive, checkUnityLifetime: true))
                    {
                        receipt.NormalizedSpellControlFsm = true;
                    }
                }
            }
        }

        internal static bool NormalizeSpellControlVariables(
            FsmVariables? vars,
            bool isInactive,
            bool checkUnityLifetime = true)
        {
            if (vars == null)
            {
                return false;
            }

            bool normalized = false;
            if (vars.GameObjectVariables != null)
            {
                foreach (FsmGameObject? goVar in vars.GameObjectVariables)
                {
                    if (goVar == null || ReferenceEquals(goVar.Value, null))
                    {
                        continue;
                    }

                    bool isTransientSpellObj = string.Equals(goVar.Name, "Fireball Top", StringComparison.Ordinal)
                        || string.Equals(goVar.Name, "Roar Wave", StringComparison.Ordinal);
                    if (isTransientSpellObj || (checkUnityLifetime && IsUnityObjectDestroyed(goVar.Value)))
                    {
                        goVar.Value = null;
                        normalized = true;
                    }
                }
            }

            if (!isInactive)
            {
                return normalized;
            }

            // Reset single-cast / single-focus transient variables only; charm-derived config
            // variables (Spell Level, Time Per MP Drain, Focus MP amount, MP Cost, Spore Cooldown) stay untouched.
            for (int i = 0; i < SpellControlTransientBools.Length; i++)
            {
                FsmBool? b = vars.FindFsmBool(SpellControlTransientBools[i]);
                if (b != null && b.Value)
                {
                    b.Value = false;
                    normalized = true;
                }
            }

            for (int i = 0; i < SpellControlTransientFloats.Length; i++)
            {
                FsmFloat? f = vars.FindFsmFloat(SpellControlTransientFloats[i]);
                if (f != null && f.Value != 0f)
                {
                    f.Value = 0f;
                    normalized = true;
                }
            }

            for (int i = 0; i < SpellControlTransientInts.Length; i++)
            {
                FsmInt? iv = vars.FindFsmInt(SpellControlTransientInts[i]);
                if (iv != null && iv.Value != 0)
                {
                    iv.Value = 0;
                    normalized = true;
                }
            }

            for (int i = 0; i < SpellControlTransientVector3s.Length; i++)
            {
                FsmVector3? v = vars.FindFsmVector3(SpellControlTransientVector3s[i]);
                if (v != null && v.Value != Vector3.zero)
                {
                    v.Value = Vector3.zero;
                    normalized = true;
                }
            }

            return normalized;
        }

        internal static void NormalizeHeroLightFsms(HeroController hero, LeanS0GateReceipt receipt)
        {
            if (hero == null || receipt == null)
            {
                return;
            }

            SpriteRenderer? heroLight = hero.heroLight;
            if (heroLight == null || heroLight.gameObject == null)
            {
                return;
            }

            UnityEngine.Random.State savedRng = UnityEngine.Random.state;
            try
            {
                bool removedTweens = StopAndRemoveITweens(heroLight.gameObject, restorePositionAnchor: false);
                iTweenFSMEvents[] residualEvents = heroLight.gameObject.GetComponentsInChildren<iTweenFSMEvents>(true);
                foreach (iTweenFSMEvents ev in residualEvents)
                {
                    if (ev != null)
                    {
                        UnityEngine.Object.DestroyImmediate(ev);
                        removedTweens = true;
                    }
                }

                bool normalized = removedTweens;
                foreach (PlayMakerFSM fsm in heroLight.GetComponents<PlayMakerFSM>())
                {
                    if (fsm == null || fsm.Fsm == null || !fsm.Fsm.Initialized)
                    {
                        continue;
                    }

                    bool clearedEvents = ClearFsmEventList(FsmDelayedEventsField, fsm.Fsm);
                    if (ClearFsmEventList(FsmUpdateEventsField, fsm.Fsm)) clearedEvents = true;
                    if (ClearFsmEventList(FsmRemoveEventsField, fsm.Fsm)) clearedEvents = true;
                    if (clearedEvents) normalized = true;

                    if (string.Equals(fsm.FsmName, "HeroLight Control", StringComparison.Ordinal))
                    {
                        FsmVector3? idleScale = fsm.FsmVariables?.FindFsmVector3("Idle Scale");
                        bool hasIdleScale = idleScale != null && idleScale.Value != Vector3.zero;
                        bool scaleMismatch = hasIdleScale && heroLight.transform.localScale != idleScale!.Value;
                        bool notIdle = !string.Equals(fsm.ActiveStateName, "Idle", StringComparison.Ordinal);

                        if (removedTweens || clearedEvents || scaleMismatch || notIdle)
                        {
                            ResetFsmStatesAndActions(fsm.Fsm);
                            if (hasIdleScale)
                            {
                                heroLight.transform.localScale = idleScale!.Value;
                            }
                            if (fsm.Fsm.GetState("Idle") != null)
                            {
                                fsm.SetState("Idle");
                            }
                            FsmPreviousActiveStateField?.SetValue(fsm.Fsm, null);
                            FsmLastTransitionField?.SetValue(fsm.Fsm, null);
                            normalized = true;
                        }
                    }
                    else if (string.Equals(fsm.FsmName, "color_fader", StringComparison.Ordinal))
                    {
                        if (!string.Equals(fsm.ActiveStateName, "Init", StringComparison.Ordinal)
                            && fsm.Fsm.GetState("Init") != null)
                        {
                            ResetFsmStatesAndActions(fsm.Fsm);
                            fsm.SetState("Init");
                            FsmPreviousActiveStateField?.SetValue(fsm.Fsm, null);
                            FsmLastTransitionField?.SetValue(fsm.Fsm, null);
                            normalized = true;
                        }
                    }
                }

                if (normalized)
                {
                    receipt.NormalizedHeroLightFsm = true;
                }
            }
            finally
            {
                UnityEngine.Random.state = savedRng;
            }
        }

        private static void ResetFsmStatesAndActions(Fsm fsm)
        {
            if (fsm.States == null)
            {
                return;
            }

            foreach (FsmState state in fsm.States)
            {
                if (state == null) continue;
                state.ResetLoopCount();
                state.ActiveActions?.Clear();
                if (FsmStateFinishedActionsField?.GetValue(state) is IList finishedList)
                {
                    finishedList.Clear();
                }
                FsmStateActiveField?.SetValue(state, false);
                FsmStateFinishedField?.SetValue(state, false);
                FsmStateActiveActionField?.SetValue(state, null);
                FsmStateActiveActionIndexField?.SetValue(state, 0);
                if (state.Actions == null) continue;
                foreach (FsmStateAction action in state.Actions)
                {
                    if (action == null) continue;
                    action.Active = false;
                    action.Finished = false;
                    action.Entered = false;
                    if (action is HutongGames.PlayMaker.Actions.Wait && WaitActionTimerField != null)
                    {
                        WaitActionTimerField.SetValue(action, 0f);
                    }
                    if (action is HutongGames.PlayMaker.Actions.iTweenFsmAction && TweenActionEventsField != null)
                    {
                        TweenActionEventsField.SetValue(action, null);
                    }
                }
            }
        }

        internal static void NormalizeHudFsms(LeanS0GateReceipt receipt)
        {
            if (receipt == null)
            {
                return;
            }

            GameCameras? cameras = GameCameras.instance;
            PlayMakerFSM? soulOrb = cameras != null ? cameras.soulOrbFSM : null;
            if (soulOrb == null || soulOrb.Fsm == null || !soulOrb.Fsm.Initialized)
            {
                return;
            }

            bool normalized = false;
            if (ClearFsmEventList(FsmDelayedEventsField, soulOrb.Fsm)) normalized = true;
            if (ClearFsmEventList(FsmUpdateEventsField, soulOrb.Fsm)) normalized = true;
            if (ClearFsmEventList(FsmRemoveEventsField, soulOrb.Fsm)) normalized = true;

            if (soulOrb.gameObject != null)
            {
                iTween[] residualTweens = soulOrb.gameObject.GetComponentsInChildren<iTween>(true);
                if (residualTweens.Length > 0)
                {
                    iTween.Stop(soulOrb.gameObject, true);
                    normalized = true;
                }

                iTweenFSMEvents[] residualEvents = soulOrb.gameObject.GetComponentsInChildren<iTweenFSMEvents>(true);
                foreach (iTweenFSMEvents ev in residualEvents)
                {
                    if (ev != null)
                    {
                        UnityEngine.Object.Destroy(ev);
                        normalized = true;
                    }
                }
            }

            if (!string.Equals(soulOrb.ActiveStateName, "Idle", StringComparison.Ordinal)
                && soulOrb.Fsm.GetState("Idle") != null)
            {
                soulOrb.SetState("Idle");
                normalized = true;
            }

            if (normalized)
            {
                receipt.NormalizedSoulOrbFsm = true;
            }

            NormalizeLowHealthHudTweens(cameras, receipt);
        }

        private static void NormalizeLowHealthHudTweens(GameCameras? cameras, LeanS0GateReceipt receipt)
        {
            Transform? hudRoot = cameras != null && cameras.hudCamera != null ? cameras.hudCamera.transform : null;
            Transform? healthRoot = hudRoot != null ? hudRoot.Find("Hud Canvas/Health") : null;
            if (healthRoot == null)
            {
                return;
            }

            UnityEngine.Random.State savedRng = UnityEngine.Random.state;
            try
            {
                bool normalizedHealth = StopAndRemoveITweens(healthRoot.gameObject, restorePositionAnchor: true);

                PlayMakerFSM? lowHealthFx = null;
                foreach (PlayMakerFSM fsm in healthRoot.GetComponents<PlayMakerFSM>())
                {
                    if (fsm != null && string.Equals(fsm.FsmName, "Low Health FX", StringComparison.Ordinal))
                    {
                        lowHealthFx = fsm;
                        break;
                    }
                }

                if (lowHealthFx != null
                    && lowHealthFx.Fsm != null
                    && lowHealthFx.Fsm.Initialized
                    && string.Equals(lowHealthFx.ActiveStateName, "Low Health", StringComparison.Ordinal)
                    && lowHealthFx.Fsm.GetState("Low Health") != null)
                {
                    lowHealthFx.SetState("Low Health");
                    normalizedHealth = true;
                }

                if (normalizedHealth)
                {
                    receipt.NormalizedLowHealthFsm = true;
                }
            }
            finally
            {
                UnityEngine.Random.state = savedRng;
            }
        }

        internal static void NormalizeCameraShakeFsm(LeanS0GateReceipt receipt)
        {
            if (receipt == null)
            {
                return;
            }

            PlayMakerFSM? cameraShake = GameCameras.instance?.cameraShakeFSM;
            if (cameraShake == null || cameraShake.Fsm == null || !cameraShake.Fsm.Initialized)
            {
                return;
            }

            bool normalized = false;
            if (ClearFsmEventList(FsmDelayedEventsField, cameraShake.Fsm)) normalized = true;
            if (ClearFsmEventList(FsmUpdateEventsField, cameraShake.Fsm)) normalized = true;
            if (ClearFsmEventList(FsmRemoveEventsField, cameraShake.Fsm)) normalized = true;

            foreach (string rumbleVarName in new[]
            {
                "RumblingFall",
                "RumblingFocus",
                "RumblingFocus2",
                "RumblingMed",
                "RumblingSmall",
                "RumblingBig",
                "RumblingHuge"
            })
            {
                FsmBool? rumbleVar = cameraShake.FsmVariables?.FindFsmBool(rumbleVarName);
                if (rumbleVar != null && rumbleVar.Value)
                {
                    rumbleVar.Value = false;
                    normalized = true;
                }
            }

            if (StopAndRemoveITweens(cameraShake.gameObject, restorePositionAnchor: false))
            {
                normalized = true;
            }

            if (cameraShake.FsmStates != null)
            {
                foreach (FsmState state in cameraShake.FsmStates)
                {
                    if (state == null) continue;
                    state.ResetLoopCount();
                    state.ActiveActions?.Clear();
                    if (FsmStateFinishedActionsField?.GetValue(state) is System.Collections.IList finishedList)
                    {
                        finishedList.Clear();
                    }
                    FsmStateActiveField?.SetValue(state, false);
                    FsmStateFinishedField?.SetValue(state, false);
                    FsmStateActiveActionField?.SetValue(state, null);
                    FsmStateActiveActionIndexField?.SetValue(state, 0);
                    if (state.Actions == null) continue;
                    foreach (FsmStateAction action in state.Actions)
                    {
                        if (action == null) continue;
                        if (action.Active || action.Finished || action.Entered)
                        {
                            action.Active = false;
                            action.Finished = false;
                            action.Entered = false;
                            normalized = true;
                        }
                        if (action is HutongGames.PlayMaker.Actions.ShakePositionV2)
                        {
                            if (ShakeNextUpdateTimeField != null && (float)ShakeNextUpdateTimeField.GetValue(action)! != 0f)
                            {
                                ShakeNextUpdateTimeField.SetValue(action, 0f);
                                normalized = true;
                            }
                            if (ShakeTimerField != null && (float)ShakeTimerField.GetValue(action)! != 0f)
                            {
                                ShakeTimerField.SetValue(action, 0f);
                                normalized = true;
                            }
                            if (ShakeTargetField != null && ShakeTargetField.GetValue(action) != null)
                            {
                                ShakeTargetField.SetValue(action, null);
                                normalized = true;
                            }
                            if (ShakeStartingPosField != null && (Vector3)ShakeStartingPosField.GetValue(action)! != Vector3.zero)
                            {
                                ShakeStartingPosField.SetValue(action, Vector3.zero);
                                normalized = true;
                            }
                        }
                    }
                }
            }

            FsmFloat? priorityVar = cameraShake.FsmVariables?.FindFsmFloat("Priority");
            if (priorityVar != null && priorityVar.Value != 0f)
            {
                priorityVar.Value = 0f;
                normalized = true;
            }
            FsmFloat? bigShakeTimeVar = cameraShake.FsmVariables?.FindFsmFloat("Big Shake Time");
            if (bigShakeTimeVar != null && bigShakeTimeVar.Value != 0f)
            {
                bigShakeTimeVar.Value = 0f;
                normalized = true;
            }

            if (cameraShake.transform.localPosition != Vector3.zero)
            {
                cameraShake.transform.localPosition = Vector3.zero;
                normalized = true;
            }

            if (cameraShake.Fsm.GetState("Normal") != null)
            {
                if (!string.Equals(cameraShake.ActiveStateName, "Normal", StringComparison.Ordinal)
                    || FsmPreviousActiveStateField?.GetValue(cameraShake.Fsm) != null)
                {
                    cameraShake.SetState("Normal");
                    normalized = true;
                }
            }
            if (FsmPreviousActiveStateField?.GetValue(cameraShake.Fsm) != null)
            {
                FsmPreviousActiveStateField.SetValue(cameraShake.Fsm, null);
                normalized = true;
            }
            FsmLastTransitionField?.SetValue(cameraShake.Fsm, null);

            if (normalized)
            {
                receipt.NormalizedCameraShakeFsm = true;
            }
        }

        internal static bool StopAndRemoveITweens(GameObject targetRoot, bool restorePositionAnchor)
        {
            if (targetRoot == null)
            {
                return false;
            }

            bool removedAny = false;
            iTween[] tweens = targetRoot.GetComponentsInChildren<iTween>(true);
            foreach (iTween tween in tweens)
            {
                if (tween == null)
                {
                    continue;
                }

                if (restorePositionAnchor
                    && string.Equals(tween.type, "shake", StringComparison.Ordinal)
                    && string.Equals(tween.method, "position", StringComparison.Ordinal)
                    && TweenVectorsField?.GetValue(tween) is Vector3[] vecs
                    && vecs.Length > 0)
                {
                    tween.transform.position = vecs[0];
                }

                tween.StopAllCoroutines();
                tween.enabled = false;
                UnityEngine.Object.DestroyImmediate(tween);
                removedAny = true;
            }

            for (int i = iTween.tweens.Count - 1; i >= 0; i--)
            {
                if (iTween.tweens[i] is not Hashtable args)
                {
                    iTween.tweens.RemoveAt(i);
                    removedAny = true;
                    continue;
                }

                object? rawTarget = args["target"];
                if (rawTarget == null
                    || (rawTarget is UnityEngine.Object obj && IsUnityObjectDestroyed(obj))
                    || (rawTarget is GameObject go && (ReferenceEquals(go, targetRoot) || go.transform.IsChildOf(targetRoot.transform))))
                {
                    iTween.tweens.RemoveAt(i);
                    removedAny = true;
                }
            }

            return removedAny;
        }

        internal static int SweepStaleITweens()
        {
            if (iTween.tweens == null || iTween.tweens.Count == 0)
            {
                return 0;
            }

            UnityEngine.Random.State savedRng = UnityEngine.Random.state;
            int removed = 0;
            try
            {
                Scene ddolScene = default;
                GameManager? gm = GameManager.instance;
                if (gm != null && gm.gameObject != null)
                {
                    ddolScene = gm.gameObject.scene;
                }

                for (int i = iTween.tweens.Count - 1; i >= 0; i--)
                {
                    if (ShouldRemoveITweenEntry(iTween.tweens[i], ddolScene))
                    {
                        iTween.tweens.RemoveAt(i);
                        removed++;
                    }
                }
            }
            finally
            {
                UnityEngine.Random.state = savedRng;
            }

            return removed;
        }

        private static bool ShouldRemoveITweenEntry(object? entry, Scene ddolScene)
        {
            if (entry is not Hashtable args)
            {
                return true;
            }

            object? rawTarget = args["target"];
            if (rawTarget == null)
            {
                return true;
            }

            if (rawTarget is not UnityEngine.Object unityObj || IsUnityObjectDestroyed(unityObj))
            {
                return true;
            }

            if (rawTarget is not GameObject targetGo)
            {
                return true;
            }

            Scene targetScene = targetGo.scene;
            bool isDdol = (ddolScene.IsValid() && targetScene == ddolScene)
                || string.Equals(targetScene.name, "DontDestroyOnLoad", StringComparison.Ordinal);
            if (!isDdol)
            {
                return true;
            }

            string? entryId = args["id"] as string;
            iTween[] components = targetGo.GetComponents<iTween>();
            for (int i = 0; i < components.Length; i++)
            {
                iTween comp = components[i];
                if (comp == null)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(entryId) && string.Equals(comp.id, entryId, StringComparison.Ordinal))
                {
                    return false;
                }

                if (string.IsNullOrEmpty(comp.id) && !targetGo.activeInHierarchy)
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool ResetFsmToState(PlayMakerFSM? fsm, string targetStateName)
        {
            if (fsm == null || fsm.Fsm == null || !fsm.Fsm.Initialized)
            {
                return false;
            }

            if (fsm.Fsm.GetState(targetStateName) == null)
            {
                return false;
            }

            ClearFsmEventList(FsmDelayedEventsField, fsm.Fsm);
            ClearFsmEventList(FsmUpdateEventsField, fsm.Fsm);
            ClearFsmEventList(FsmRemoveEventsField, fsm.Fsm);
            ResetFsmStatesAndActions(fsm.Fsm);
            fsm.SetState(targetStateName);
            FsmPreviousActiveStateField?.SetValue(fsm.Fsm, null);
            FsmLastTransitionField?.SetValue(fsm.Fsm, null);
            return true;
        }

        internal static GameObject? FindGrimmchildInstance()
        {
            try
            {
                GameObject tagged = GameObject.FindGameObjectWithTag("Grimmchild");
                if (tagged != null) return tagged;
            }
            catch
            {
                // Ignore if tag lookup throws during teardown; fallback to DDOL scan.
            }

            GameManager? gm = GameManager.instance;
            Scene ddol = gm != null ? gm.gameObject.scene : default;
            if (ddol.IsValid() && ddol.isLoaded)
            {
                foreach (GameObject root in ddol.GetRootGameObjects())
                {
                    if (root != null && root.name.StartsWith("Grimmchild", StringComparison.Ordinal))
                    {
                        return root;
                    }
                }
            }

            return null;
        }

        internal static void NormalizeCharmEffectFsms(HeroController hero, LeanS0GateReceipt receipt)
        {
            if (hero == null || receipt == null)
            {
                return;
            }

            Transform? charmEffects = hero.transform.Find("Charm Effects");
            if (charmEffects == null)
            {
                return;
            }

            UnityEngine.Random.State savedRng = UnityEngine.Random.state;
            bool normalized = false;
            try
            {
                PlayMakerFSM[] fsms = charmEffects.GetComponents<PlayMakerFSM>();
                foreach (PlayMakerFSM fsm in fsms)
                {
                    if (fsm == null)
                    {
                        continue;
                    }

                    if (string.Equals(fsm.FsmName, "Weaverling Control", StringComparison.Ordinal))
                    {
                        if (ResetFsmToState(fsm, "Unequipped")) normalized = true;
                        FsmGameObject? weaverlingVar = fsm.FsmVariables?.FindFsmGameObject("Weaverling");
                        if (weaverlingVar != null && weaverlingVar.Value != null)
                        {
                            weaverlingVar.Value = null;
                            normalized = true;
                        }
                    }
                    else if (string.Equals(fsm.FsmName, "Spawn Orbit Shield", StringComparison.Ordinal))
                    {
                        if (ResetFsmToState(fsm, "Idle")) normalized = true;
                        FsmGameObject? shieldVar = fsm.FsmVariables?.FindFsmGameObject("Shield");
                        if (shieldVar != null && shieldVar.Value != null)
                        {
                            shieldVar.Value = null;
                            normalized = true;
                        }
                    }
                    else if (string.Equals(fsm.FsmName, "Thorn Counter", StringComparison.Ordinal))
                    {
                        if (ResetFsmToState(fsm, "Idle")) normalized = true;
                    }
                    else if (string.Equals(fsm.FsmName, "Spawn Grimmchild", StringComparison.Ordinal))
                    {
                        if (ResetFsmToState(fsm, "Idle")) normalized = true;
                        FsmGameObject? childVar = fsm.FsmVariables?.FindFsmGameObject("Child");
                        if (childVar != null)
                        {
                            GameObject? liveGrimmchild = FindGrimmchildInstance();
                            if (!ReferenceEquals(childVar.Value, liveGrimmchild))
                            {
                                childVar.Value = liveGrimmchild;
                                normalized = true;
                            }
                        }
                    }
                    else if (string.Equals(fsm.FsmName, "Hatchling Spawn", StringComparison.Ordinal))
                    {
                        if (ResetFsmToState(fsm, "Count Remaining")) normalized = true;
                    }
                }

                Transform? dung = charmEffects.Find("Dung");
                if (dung != null)
                {
                    foreach (PlayMakerFSM fsm in dung.GetComponents<PlayMakerFSM>())
                    {
                        if (fsm != null && string.Equals(fsm.FsmName, "Control", StringComparison.Ordinal))
                        {
                            if (ResetFsmToState(fsm, "Unequipped")) normalized = true;
                            break;
                        }
                    }

                    AudioSource? dungAudio = dung.GetComponent<AudioSource>();
                    if (dungAudio != null && dungAudio.isPlaying)
                    {
                        dungAudio.Stop();
                        normalized = true;
                    }

                    Transform? particle1 = dung.Find("Particle 1");
                    if (particle1 != null)
                    {
                        ParticleSystem? ps = particle1.GetComponent<ParticleSystem>();
                        if (ps != null)
                        {
                            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                            normalized = true;
                        }

                        AudioSource? p1Audio = particle1.GetComponent<AudioSource>();
                        if (p1Audio != null)
                        {
                            p1Audio.Stop();
                            normalized = true;
                        }
                    }
                }

                if (normalized)
                {
                    receipt.NormalizedCharmEffectFsms = true;
                }
            }
            finally
            {
                UnityEngine.Random.state = savedRng;
            }
        }

        internal static void NormalizeAudioManagerFsms(LeanS0GateReceipt receipt)
        {
            if (receipt == null)
            {
                return;
            }

            AudioManager? audioManager = GameManager.instance?.AudioManager;
            if (audioManager == null)
            {
                return;
            }

            UnityEngine.Random.State savedRng = UnityEngine.Random.state;
            try
            {
                bool normalized = false;
                foreach (PlayMakerFSM fsm in audioManager.GetComponents<PlayMakerFSM>())
                {
                    if (fsm == null || fsm.Fsm == null || !fsm.Fsm.Initialized)
                    {
                        continue;
                    }

                    if (!string.Equals(fsm.FsmName, "Hero Damaged Effects", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    bool hasDelayed = HasNonEmptyEventList(FsmDelayedEventsField, fsm.Fsm)
                        || HasNonEmptyEventList(FsmUpdateEventsField, fsm.Fsm)
                        || HasNonEmptyEventList(FsmRemoveEventsField, fsm.Fsm);
                    bool hasPrevious = FsmPreviousActiveStateField?.GetValue(fsm.Fsm) != null
                        || FsmLastTransitionField?.GetValue(fsm.Fsm) != null;
                    FsmInt? hpVar = fsm.FsmVariables?.FindFsmInt("HP");
                    bool dirtyHp = hpVar != null && hpVar.Value != 0;
                    bool notIdle = !string.Equals(fsm.ActiveStateName, "Idle", StringComparison.Ordinal);

                    if (notIdle || hasDelayed || hasPrevious || dirtyHp)
                    {
                        if (ResetFsmToState(fsm, "Idle"))
                        {
                            normalized = true;
                        }
                        if (hpVar != null && hpVar.Value != 0)
                        {
                            hpVar.Value = 0;
                            normalized = true;
                        }
                    }
                }

                if (normalized)
                {
                    receipt.NormalizedAudioManagerFsms = true;
                }
            }
            finally
            {
                UnityEngine.Random.state = savedRng;
            }
        }

        private static bool HasNonEmptyEventList(FieldInfo? field, Fsm fsm)
            => field?.GetValue(fsm) is IList list && list.Count > 0;

        private static bool ClearFsmEventList(FieldInfo? field, Fsm fsm)
        {
            if (field?.GetValue(fsm) is IList list && list.Count > 0)
            {
                list.Clear();
                return true;
            }
            return false;
        }

        internal static int RecycleLevelUnloadPooledObjects()
        {
            AutoRecycleSelf[] candidates = UnityEngine.Object.FindObjectsOfType<AutoRecycleSelf>();
            int recycled = 0;
            foreach (AutoRecycleSelf item in candidates)
            {
                if (item == null || item.afterEvent != AfterEvent.LEVEL_UNLOAD)
                {
                    continue;
                }

                GameObject go = item.gameObject;
                if (go == null || !go.activeInHierarchy)
                {
                    continue;
                }

                // Only recycle pooled runtime minions/effects that carry AutoRecycleSelf(LEVEL_UNLOAD).
                // Grimmchild(Clone) does not have AutoRecycleSelf and is preserved across scenes (D2 Option B).
                go.Recycle();
                recycled++;
            }

            return recycled;
        }
    }
}
