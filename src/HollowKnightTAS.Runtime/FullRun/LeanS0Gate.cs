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
        public bool NormalizedAltAttack { get; set; }
        public bool NormalizedInvulnerable { get; set; }
        public bool NormalizedNailCharging { get; set; }
        public bool NormalizedDreamNailQueueTimer { get; set; }
        public bool NormalizedSoulOrbFsm { get; set; }
        public bool NormalizedCameraShakeFsm { get; set; }
    }

    /// <summary>
    /// Unified S0 gate shared across recording, normal replay, and snapshot restoration paths.
    /// </summary>
    internal static class LeanS0Gate
    {
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

        internal static LeanS0GateReceipt Execute(
            int boundaryOrdinal,
            string targetSceneName,
            string? entryGateName,
            bool applyUnityRuntime = true,
            Action<int>? overrideRngInit = null,
            Func<string>? rngDigestReader = null)
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

            if (applyUnityRuntime)
            {
                ExecuteUnityRuntimePhase(receipt);
            }

            return receipt;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void InitUnityRandomSeed(int seed)
            => UnityEngine.Random.InitState(seed);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ExecuteUnityRuntimePhase(LeanS0GateReceipt receipt)
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

            if (cameraShake.transform.localPosition != Vector3.zero)
            {
                cameraShake.transform.localPosition = Vector3.zero;
                normalized = true;
            }

            if (!string.Equals(cameraShake.ActiveStateName, "Normal", StringComparison.Ordinal)
                && cameraShake.Fsm.GetState("Normal") != null)
            {
                cameraShake.SetState("Normal");
                normalized = true;
            }

            if (normalized)
            {
                receipt.NormalizedCameraShakeFsm = true;
            }
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
