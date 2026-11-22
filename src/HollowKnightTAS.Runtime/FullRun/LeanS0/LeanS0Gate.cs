using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using GlobalEnums;
using HollowKnightTAS.Core.SceneCheckpoints;
using HutongGames.PlayMaker;
using UnityEngine;

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
        public bool NormalizedAltAttack { get; set; }
        public bool NormalizedInvulnerable { get; set; }
        public bool NormalizedNailCharging { get; set; }
        public bool NormalizedDreamNailQueueTimer { get; set; }
        public bool NormalizedSoulOrbFsm { get; set; }
        public bool NormalizedLowHealthFsm { get; set; }
        public bool NormalizedCameraShakeFsm { get; set; }
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

        private static readonly (FieldInfo? Field, object ZeroValue)[] TransientHeroResetFields =
            BuildTransientHeroResetFields();

        private static (FieldInfo? Field, object ZeroValue)[] BuildTransientHeroResetFields()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            Type heroType = typeof(HeroController);
            var specs = new (string Name, object ZeroValue)[]
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
                ("headBumpSteps", 0)
            };
            var result = new (FieldInfo? Field, object ZeroValue)[specs.Length];
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
            }

            NormalizeHudFsms(receipt);
            NormalizeCameraShakeFsm(receipt);
            receipt.RecycledLevelUnloadObjects = RecycleLevelUnloadPooledObjects();
            Action<LeanS0GateReceipt, HeroController?> resetHandler = boundaryEnvironmentReset
                ?? throw new InvalidOperationException("LeanS0Gate boundary environment reset handler is required when applyUnityRuntime is true.");
            resetHandler(receipt, hero);
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

            if (AltAttackTimeField != null)
            {
                float current = (float)(AltAttackTimeField.GetValue(hero) ?? 0f);
                if (current != 0f)
                {
                    AltAttackTimeField.SetValue(hero, 0f);
                    receipt.NormalizedAltAttack = true;
                }
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

        internal static void NormalizeHeroFsms(HeroController hero, LeanS0GateReceipt receipt)
        {
            if (hero == null || receipt == null)
            {
                return;
            }

            PlayMakerFSM[] fsms = hero.GetComponents<PlayMakerFSM>();
            foreach (PlayMakerFSM fsm in fsms)
            {
                if (fsm == null || !string.Equals(fsm.FsmName, "Dream Nail", StringComparison.Ordinal))
                {
                    continue;
                }

                FsmFloat? queueTimer = fsm.FsmVariables?.FindFsmFloat("Queue Timer");
                if (queueTimer != null && queueTimer.Value != 0f)
                {
                    queueTimer.Value = 0f;
                    receipt.NormalizedDreamNailQueueTimer = true;
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

                GameObject tweenGo = tween.gameObject;
                for (int i = iTween.tweens.Count - 1; i >= 0; i--)
                {
                    if (iTween.tweens[i] is Hashtable args && (args["target"] == null || ReferenceEquals(args["target"], tweenGo)
                        || (args["target"] is UnityEngine.Object obj && obj == null)))
                    {
                        iTween.tweens.RemoveAt(i);
                    }
                }

                tween.StopAllCoroutines();
                tween.enabled = false;
                UnityEngine.Object.DestroyImmediate(tween);
                removedAny = true;
            }

            return removedAny;
        }

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
