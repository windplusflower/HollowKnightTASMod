using System;
using System.Linq;
using System.IO;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using HollowKnightTAS.Core.Movie;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalHeroFallTrailPolicy
    {
        internal const string RetiredTweenPolicy = NativeExecutionProfile.CanonicalRetiredFallTrailTweenPolicyId;
        private static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidDataException("Original Fall Trail field is absent: " + name);
        // The original action remains in D, including its ID/event/configuration.
        // Only its dead Unity callback cache is S2; live caches remain unsupported.
        internal static iTweenScaleTo RequireRetiredTweenAction(Fsm raw, PlayMakerFSM component, GameObject owner)
        {
            if (typeof(iTweenScaleTo).Module.ModuleVersionId.ToString("D") != "2b54cdaf-7a93-4f0e-b297-d50c4090ea9f"
                || !ReferenceEquals(Field(typeof(PlayMakerFSM), "fsm").GetValue(component), raw)
                || !ReferenceEquals(Field(typeof(Fsm), "owner").GetValue(raw), component)
                || (string)Field(typeof(Fsm), "name").GetValue(raw) != "Trail Control")
                throw new InvalidDataException("Original Fall Trail raw FSM owner differs.");
            var states = Field(typeof(Fsm), "states").GetValue(raw) as FsmState[];
            if (states == null || states.Length != 3 || !states.Select(s => s.Name).SequenceEqual(new[] { "Playing", "End", "Idle" })
                || !ReferenceEquals(Field(typeof(Fsm), "activeState").GetValue(raw), states[2]))
                throw new InvalidDataException("Original Fall Trail retired Tween requires its exact Idle state.");
            var actions = Field(typeof(FsmState), "actions").GetValue(states[0]) as FsmStateAction[];
            var idle = Field(typeof(FsmState), "actions").GetValue(states[2]) as FsmStateAction[] ?? Array.Empty<FsmStateAction>();
            if (actions == null || actions.Length != 6 || !actions.Select(a => a?.GetType()).SequenceEqual(new[] {
                typeof(PlayParticleEmitter), typeof(SetScale), typeof(iTweenScaleTo), typeof(EaseFloat), typeof(GetVelocity2d), typeof(FloatCompare) })
                || idle.Length != 0)
                throw new InvalidDataException("Original Fall Trail Playing action layout differs.");
            var action = (iTweenScaleTo)actions[2];
            if (!ReferenceEquals(Field(typeof(FsmStateAction), "fsm").GetValue(action), raw)
                || !ReferenceEquals(Field(typeof(FsmStateAction), "fsmState").GetValue(action), states[0])
                || !ReferenceEquals(Field(typeof(FsmStateAction), "fsmComponent").GetValue(action), component)
                || !ReferenceEquals(Field(typeof(FsmStateAction), "owner").GetValue(action), owner)
                || action.gameObject == null || action.gameObject.OwnerOption != OwnerDefaultOption.UseOwner
                || action.stopOnExit == null || (!action.stopOnExit.IsNone && !action.stopOnExit.Value)
                || action.loopType != iTween.LoopType.none)
                throw new InvalidDataException("Original Fall Trail Tween action provenance or exit policy differs.");
            return action;
        }
        private static int ClearRetiredTweenCache(PlayMakerFSM component, GameObject owner)
        {
            if (!NativeExecutionProfile.UsesCanonicalRetiredUiCallbacks(NativeExecutionProfile.SelectedProfileId)) return 0;
            var action = RequireRetiredTweenAction((Fsm)Field(typeof(PlayMakerFSM), "fsm").GetValue(component), component, owner);
            var slot = Field(typeof(iTweenFsmAction), "itweenEvents");
            var callback = slot.GetValue(action) as iTweenFSMEvents;
            if (ReferenceEquals(callback, null)) return 0;
            if (callback != null || !ReferenceEquals(callback!.itweenFSMAction, action)
                || callback.itweenID != (int)Field(typeof(iTweenFsmAction), "itweenID").GetValue(action))
                throw new InvalidDataException("Original Fall Trail Tween cache is live or has foreign provenance.");
            slot.SetValue(action, null);
            if (!ReferenceEquals(slot.GetValue(action), null)) throw new InvalidDataException("Original Fall Trail retired Tween cache readback differs.");
            return 1;
        }
        // The original Trail Control Playing action calls Play only when this
        // exact PS is not playing. Carrying old simulation changes future Rand
        // consumption even with identical original FSM/Hero fields.
        internal static object Reset()
        {
            var hero = HeroController.SilentInstance ?? throw new InvalidOperationException("Fall Trail Canon has no original Hero.");
            var node = hero.transform.Find("Effects/Fall Trail") ?? throw new InvalidOperationException("Original Fall Trail owner is absent.");
            var system = node.GetComponent<ParticleSystem>() ?? throw new InvalidOperationException("Original Fall Trail PS is absent.");
            var fsm = hero.fsm_fallTrail;
            Modding.Logger.Log("[HKTAS] Fall Trail original contract: " + Newtonsoft.Json.JsonConvert.SerializeObject(new {
                current = State(system), name = fsm?.FsmName, sameOwner = fsm != null && fsm.transform == node,
                node.childCount, active = node.gameObject.activeInHierarchy, system.collision.enabled,
                trigger = system.trigger.enabled, subEmitters = system.subEmitters.subEmittersCount,
                components = node.GetComponents<Component>().Select(component => component == null ? "missing" : component.GetType().FullName).ToArray() }));
            if (fsm == null || fsm.transform != node || fsm.FsmName != "Trail Control" || node.childCount != 0
                || !node.gameObject.activeInHierarchy || !node.gameObject.activeSelf || !system.useAutoRandomSeed
                || system.collision.enabled || system.trigger.enabled || system.subEmitters.subEmittersCount != 0)
                throw new InvalidOperationException("Original Fall Trail pure visual ownership/configuration contract differs.");
            var allowed = new[] { typeof(Transform), typeof(ParticleSystem), typeof(ParticleSystemRenderer), typeof(PlayMakerFSM), typeof(iTweenFSMEvents) };
            if (node.GetComponents<Component>().Any(component => component == null || !allowed.Contains(component.GetType())))
                throw new InvalidOperationException("Fall Trail owner contains an unregistered lifecycle component.");
            var before = State(system);
            var state = fsm.ActiveStateName;
            var seedBefore = LoadingGameplayOracle.CaptureParticleRandomState();
            var unityBefore = UnityEngine.Random.state;
            system.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            // This is the local visual simulation cursor, not any engine clock.
            // Stop/Clear alone may retain elapsed system time on this Unity.
            system.time = 0;
            system.randomSeed = 0;
            system.useAutoRandomSeed = true;
            var retiredTweenCaches = ClearRetiredTweenCache(fsm, node.gameObject);
            Modding.Logger.Log("[HKTAS] Fall Trail stopped readback: " + Newtonsoft.Json.JsonConvert.SerializeObject(new {
                current = State(system), fsmStateEqual = fsm.ActiveStateName == state,
                particleRandomEqual = seedBefore.SequenceEqual(LoadingGameplayOracle.CaptureParticleRandomState()),
                unityRandomEqual = unityBefore.Equals(UnityEngine.Random.state) }));
            if (system.isPlaying || system.isPaused || !system.isStopped || system.particleCount != 0 || system.time != 0
                || system.randomSeed != 0 || !system.useAutoRandomSeed || fsm.ActiveStateName != state
                || !node.gameObject.activeInHierarchy || !seedBefore.SequenceEqual(LoadingGameplayOracle.CaptureParticleRandomState())
                || !unityBefore.Equals(UnityEngine.Random.state))
                throw new InvalidOperationException("Fall Trail native stopped/empty readback or preserved original FSM/RNG differs.");
            return new { originalOwner = "Knight/Effects/Fall Trail", before, after = State(system),
                retiredTweenCaches, retiredTweenPolicy = RetiredTweenPolicy,
                originalFsmStateUnchanged = true, originalEnableAndAliasUnchanged = true,
                nativeAndUnityRandomUnchanged = true, simulationQualified = false };
        }
        private static object State(ParticleSystem system) => new {
            system.isPlaying, system.isPaused, system.isStopped, system.isEmitting,
            system.particleCount, system.time, system.randomSeed, system.useAutoRandomSeed };
    }
}
