using System;
using System.Linq;
using System.Reflection;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed class CanonicalHudParticlePolicy : IDisposable
    {
        private delegate void OriginalSceneInit(GameCameras cameras);
        private readonly Hook hook;
        private ParticleSystem[]? armed;
        private GameCameras? owner;
        private static readonly string[] Paths = {
            "Hud Canvas/Health/Low Health Particles",
            "Hud Canvas/Soul Orb/Vessels/Particle 1", "Hud Canvas/Soul Orb/Vessels/Particle 2",
            "Hud Canvas/Soul Orb/Vessels/Particle 3", "Hud Canvas/Soul Orb/Vessels/Particle 4",
            "Hud Canvas/Soul Orb/Vessels/Particle Fill" };

        internal CanonicalHudParticlePolicy()
        {
            hook = new Hook(typeof(GameCameras).GetMethod("SceneInit", BindingFlags.Instance | BindingFlags.Public)!,
                (Action<OriginalSceneInit, GameCameras>)SceneInit);
        }
        internal int Reset()
        {
            if (armed != null) throw new InvalidOperationException("Owned HUD particles were rearmed before original SceneInit consumed them.");
            owner = typeof(GameCameras).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) as GameCameras
                ?? throw new InvalidOperationException("Owned HUD particles have no original camera owner.");
            var systems = Paths.Select(path => {
                var node = owner.hudCamera.transform.Find(path) ?? throw new InvalidOperationException("Owned HUD particle path is absent: " + path);
                if (node.childCount != 0 || node.GetComponents<Component>().Any(component => !(component is Transform) && !(component is ParticleSystem) && !(component is ParticleSystemRenderer)
                    && !(path == Paths[0] && component is SetParticleScale)))
                    throw new InvalidOperationException("Owned HUD particle subtree contains another lifecycle owner: " + path);
                var system = node.GetComponent<ParticleSystem>() ?? throw new InvalidOperationException("Owned HUD particle component is absent: " + path);
                if (!system.useAutoRandomSeed || !system.main.playOnAwake || !node.gameObject.activeSelf)
                    throw new InvalidOperationException("Owned HUD particle original auto-seed/activation contract differs: " + path);
                return system;
            }).ToArray();
            if (systems.Distinct().Count() != Paths.Length) throw new InvalidOperationException("Owned HUD particle aliases overlap.");
            // Original native Stop/Clear retires live particles. A real later
            // activation owns Play/autoSeed, rather than copying RNG outputs.
            foreach (var system in systems) {
                system.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                system.gameObject.SetActive(false);
                system.randomSeed = 0;
                system.useAutoRandomSeed = true;
                if (system.isPlaying || system.particleCount != 0 || system.randomSeed != 0 || system.gameObject.activeSelf)
                    throw new InvalidOperationException("Owned HUD particle reset native readback differs.");
            }
            armed = systems;
            return systems.Length;
        }
        private void SceneInit(OriginalSceneInit original, GameCameras cameras)
        {
            var systems = armed;
            if (systems != null) {
                if (!ReferenceEquals(cameras, owner) || systems.Any(system => system == null || system.gameObject.activeSelf))
                    throw new InvalidOperationException("Owned HUD particle original activation identity differs.");
                armed = null; // Once only, including failure. Never replay it.
                foreach (var system in systems) system.gameObject.SetActive(true);
            }
            original(cameras);
        }
        public void Dispose() => hook.Dispose();
    }
}
