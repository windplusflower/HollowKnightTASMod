using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Copy configuration before the original HeroLight Fsm.Init initializes its
    // states/actions. Runtime state is never used as the reset template.
    internal sealed class CanonicalVisualFsmTemplates : IDisposable
    {
        private delegate void OriginalInit(Fsm fsm, MonoBehaviour owner);
        private static readonly FieldInfo Raw = typeof(PlayMakerFSM).GetField("fsm", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly Hook initHook;
        private Exception? registrationFailure;
        private readonly Dictionary<PlayMakerFSM, Fsm> templates = new Dictionary<PlayMakerFSM, Fsm>();
        private readonly Dictionary<int, PlayMakerFSM> resetStartOwners = new Dictionary<int, PlayMakerFSM>();

        internal void BeginBoundaryReset() => resetStartOwners.Clear();

        internal bool OwnsResetStart(int instance)
        {
            if (!resetStartOwners.TryGetValue(instance, out var component) || component == null
                || !component.enabled || !component.gameObject.activeInHierarchy) return false;
            var lifecycle = BoundaryMonoLifecycle.Read(component);
            return lifecycle[0] == 1 && lifecycle[1] == 1 && lifecycle[4] == 1;
        }

        internal CanonicalVisualFsmTemplates()
        {
            var init = typeof(Fsm).GetMethod("Init", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(MonoBehaviour) }, null)
                ?? throw new MissingMethodException("Fsm.Init");
            initHook = new Hook(init, (Action<OriginalInit, Fsm, MonoBehaviour>)Init);
        }

        private void Init(OriginalInit original, Fsm raw, MonoBehaviour owner)
        {
            if (owner is PlayMakerFSM component && component.gameObject.name == "HeroLight"
                && (raw.Name == "HeroLight Control" || raw.Name == "color_fader")
                && component.transform.root.GetComponent<HeroController>() != null
                && !templates.ContainsKey(component))
            {
                try
                {
                    // Scene activation runs Awake/Init before scene.isLoaded
                    // becomes true. Require an actual Hero scene owner, but
                    // observe this configuration at Init's earlier lifecycle.
                    if (templates.Count >= 16 || raw.Started || !component.gameObject.scene.IsValid())
                        throw new InvalidOperationException("Canonical visual template registration differs.");
                    templates.Add(component, new Fsm(raw));
                    Modding.Logger.Log("[HKTAS] Registered original visual FSM configuration before Init: " + raw.Name
                        + " scene=" + component.gameObject.scene.name + " sceneLoaded=" + component.gameObject.scene.isLoaded
                        + " started=" + raw.Started);
                }
                catch (Exception error) { registrationFailure = error; }
            }
            // A guard failure must not leave the original Hero startup half
            // executed. Report it at the controlled native boundary instead.
            original(raw, owner);
        }

        internal void ResetLight(PlayMakerFSM component)
        {
            if (registrationFailure != null) throw new InvalidOperationException("Canonical visual configuration registration failed.", registrationFailure);
            if (!templates.TryGetValue(component, out var template))
                throw new InvalidOperationException("Canonical visual FSM did not register before Init: " + component.FsmName);
            Restart(component, template);
            if (component.FsmName == "HeroLight Control" && component.ActiveStateName != "Idle"
                || component.FsmName == "color_fader" && component.ActiveStateName != "Init")
                throw new InvalidOperationException("Canonical visual FSM did not return to its original idle: " + component.FsmName + "/" + component.ActiveStateName);
        }


        internal void ResetShake(PlayMakerFSM component)
        {
            // CameraParent is part of the title scene and can Awake before Mod
            // construction. Copy only its serialized state/action configuration
            // and reset its closed variable table to the original definition.
            if (!templates.TryGetValue(component, out var template))
            {
                template = new Fsm((Fsm)Raw.GetValue(component));
                var variables = template.Variables;
                if (!variables.FloatVariables.Select(value => value.Name).OrderBy(value => value, StringComparer.Ordinal)
                        .SequenceEqual(new[] { "Big Shake Time", "FPS Limit", "Priority" })
                    || !variables.BoolVariables.Select(value => value.Name).OrderBy(value => value, StringComparer.Ordinal)
                        .SequenceEqual(new[] { "RumblingBig", "RumblingFall", "RumblingFocus", "RumblingFocus2", "RumblingHuge", "RumblingMed", "RumblingSmall" })
                    || !variables.Vector3Variables.Select(value => value.Name).OrderBy(value => value, StringComparer.Ordinal)
                        .SequenceEqual(new[] { "AverageShake", "BigShake", "EnemyKillShake", "SmallShake" })
                    || variables.GameObjectVariables.Length != 1 || variables.GameObjectVariables[0].Name != "Self"
                    || variables.IntVariables.Length != 0 || variables.StringVariables.Length != 0 || variables.Vector2Variables.Length != 0
                    || variables.ColorVariables.Length != 0 || variables.QuaternionVariables.Length != 0 || variables.ObjectVariables.Length != 0
                    || variables.MaterialVariables.Length != 0 || variables.TextureVariables.Length != 0 || variables.ArrayVariables.Length != 0 || variables.EnumVariables.Length != 0)
                    throw new InvalidOperationException("Canonical CameraShake original variable table differs.");
                variables.FindFsmFloat("Big Shake Time").Value = 0;
                variables.FindFsmFloat("FPS Limit").Value = 60;
                variables.FindFsmFloat("Priority").Value = 0;
                foreach (var value in variables.BoolVariables) value.Value = false;
                variables.FindFsmVector3("AverageShake").Value = new Vector3(0.15f, 0.15f, 0);
                variables.FindFsmVector3("BigShake").Value = new Vector3(0.5f, 0.5f, 0);
                variables.FindFsmVector3("EnemyKillShake").Value = new Vector3(0.105f, 0.105f, 0);
                variables.FindFsmVector3("SmallShake").Value = new Vector3(0.08f, 0.08f, 0);
                variables.GameObjectVariables[0].Value = null;
                templates.Add(component, template);
            }
            Restart(component, template);
            if (component.ActiveStateName != "Normal")
                throw new InvalidOperationException("Canonical CameraShake original restart did not reach Normal.");
        }

        private void Restart(PlayMakerFSM component, Fsm template)
        {
            if (!component.enabled || !component.gameObject.activeInHierarchy)
                throw new InvalidOperationException("Canonical registered visual FSM is not active.");
            var lifecycle = BoundaryMonoLifecycle.Read(component);
            if (lifecycle[0] != 1 || lifecycle[1] != 1 || resetStartOwners.Count >= 3
                || resetStartOwners.ContainsKey(component.GetInstanceID()))
                throw new InvalidOperationException("Canonical visual FSM original native Start is incomplete or reset twice.");
            // Original OnDisable runs OnExit and removes the old scene listener.
            // New state/action objects discard inactive action caches as well.
            component.enabled = false;
            var fresh = new Fsm(template);
            Raw.SetValue(component, fresh);
            fresh.Init(component);
            component.enabled = true;
            // The native component has already started. Enter via the original
            // FSM Start, never SetState or a native lifecycle flag write.
            fresh.Start();
            // Unity's original enable path queues DelayedStartCall even when
            // this native component has already started. The pinned callback
            // checks m_DidStart and is a no-op for these three bound owners.
            // Preserve that real queue entry; never clear it or its flags.
            var after = BoundaryMonoLifecycle.Read(component);
            if (!after.SequenceEqual(lifecycle) || !fresh.Started)
                throw new InvalidOperationException("Canonical visual FSM changed its native lifecycle.");
            resetStartOwners.Add(component.GetInstanceID(), component);
        }

        public void Dispose() { initHook.Dispose(); templates.Clear(); resetStartOwners.Clear(); }
    }
}
