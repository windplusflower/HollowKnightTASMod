using System;
using System.Linq;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalHeroFallTrailPolicy
    {
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
                originalFsmStateUnchanged = true, originalEnableAndAliasUnchanged = true,
                nativeAndUnityRandomUnchanged = true, simulationQualified = false };
        }
        private static object State(ParticleSystem system) => new {
            system.isPlaying, system.isPaused, system.isStopped, system.isEmitting,
            system.particleCount, system.time, system.randomSeed, system.useAutoRandomSeed };
    }
}
