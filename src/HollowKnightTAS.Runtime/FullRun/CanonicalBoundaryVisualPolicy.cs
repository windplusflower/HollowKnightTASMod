using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalBoundaryVisualPolicy
    {
        internal static int Reset(CanonicalVisualFsmTemplates templates, out int removedTweenDestroyCallbacks)
        {
            using var validation = new LoadingEngineClockProbe.CheckpointValidation();
            // OnExit in the registered FSM resets can already retire a Tween.
            // Register its scalar ID before sending those original events.
            var retiredTweens = Resources.FindObjectsOfTypeAll<iTween>().Where(tween => tween != null
                && tween.gameObject.scene.IsValid() && tween.gameObject.scene.isLoaded).ToDictionary(tween => tween.GetInstanceID());
            if (retiredTweens.Count > 4096) throw new InvalidOperationException("Canonical initial Tween budget exceeded.");
            templates.BeginBoundaryReset();
            var cameras = GameCameras.instance ?? throw new InvalidOperationException("Canonical visual reset requires GameCameras.");
            var shake = cameras.cameraShakeFSM;
            if (shake == null || shake.FsmName != "CameraShake") throw new InvalidOperationException("Canonical CameraShake binding differs.");
            var rumbleFlags = new[] { "RumblingHuge", "RumblingBig", "RumblingMed", "RumblingSmall", "RumblingFocus", "RumblingFocus2", "RumblingFall" }
                .Select(name => shake.FsmVariables.FindFsmBool(name)
                    ?? throw new InvalidOperationException("Canonical CameraShake rumble binding differs: " + name)).ToArray();
            // Original global transition executes ShakePosition.OnExit and the
            // CancelAllShake actions, including its original position reset.
            shake.SendEvent("CANCEL SHAKE");
            if (shake.ActiveStateName != "CancelAllShake")
                throw new InvalidOperationException("Canonical CameraShake did not enter cancellation: " + shake.ActiveStateName);
            foreach (var flag in rumbleFlags) flag.Value = false;
            // CancelAllShake waits for this original event, not FINISHED.
            shake.SendEvent("RESUME SHAKE");
            if (shake.ActiveStateName != "Normal") throw new InvalidOperationException("Canonical CameraShake did not finish cancellation: " + shake.ActiveStateName);
            templates.ResetShake(shake);
            var hero = HeroController.SilentInstance;
            if (hero != null)
            {
                var fsmField = typeof(PlayMakerFSM).GetField("fsm", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException("PlayMakerFSM.fsm");
                var lights = hero.GetComponentsInChildren<PlayMakerFSM>(true).Where(component => component.gameObject.name == "HeroLight"
                    && fsmField.GetValue(component) is HutongGames.PlayMaker.Fsm fsm && fsm.Name == "HeroLight Control").ToArray();
                if (lights.Length != 1 || lights[0].gameObject.name != "HeroLight")
                    throw new InvalidOperationException("Canonical HeroLight binding differs.");
                var light = lights[0];
                if (light.FsmName != "HeroLight Control") throw new InvalidOperationException("Canonical HeroLight FSM name differs.");
                for (var step = 0; light.ActiveStateName != "Idle"; step++)
                {
                    if (step >= 4 || !new[] { "Init", "Damage", "Damage Pause", "Recover" }.Contains(light.ActiveStateName))
                        throw new InvalidOperationException("Canonical HeroLight has an unregistered visual state: " + light.ActiveStateName);
                    light.SendEvent("FINISHED");
                }
                templates.ResetLight(light);
                // Read the fresh original configuration, not the outgoing
                // runtime variable table. Otherwise the transform can retain
                // a history-dependent value after its FSM was canonicalized.
                var idle = light.FsmVariables.FindFsmVector3("Idle Scale")
                    ?? throw new InvalidOperationException("Canonical HeroLight fresh Idle Scale binding differs.");
                light.transform.localScale = idle.Value;
                var colors = hero.GetComponentsInChildren<PlayMakerFSM>(true).Where(component => component.gameObject == light.gameObject
                    && fsmField.GetValue(component) is HutongGames.PlayMaker.Fsm fsm && fsm.Name == "color_fader").ToArray();
                if (colors.Length != 1) throw new InvalidOperationException("Canonical HeroLight color_fader binding differs.");
                templates.ResetLight(colors[0]);
            }
            CanonicalAreaTitleBoundaryPolicy.Reset();
            var tweens = Resources.FindObjectsOfTypeAll<iTween>().Where(tween => tween != null
                && tween.gameObject.scene.IsValid() && tween.gameObject.scene.isLoaded).ToArray();
            if (tweens.Length > 4096) throw new InvalidOperationException("Canonical Tween budget exceeded.");
            foreach (var tween in tweens)
            {
                var instance = tween.GetInstanceID();
                if (retiredTweens.TryGetValue(instance, out var old) && !ReferenceEquals(old, tween))
                    throw new InvalidOperationException("Canonical Tween instance ID was reused during its reset.");
                retiredTweens[instance] = tween;
            }
            foreach (var owner in tweens.Select(tween => tween.gameObject).Distinct()) iTween.Stop(owner);
            // Stop/OnExit use deferred Destroy; finish removing those same
            // components at the parked boundary before the state is observed.
            foreach (var tween in tweens) if (tween != null) UnityEngine.Object.DestroyImmediate(tween);
            iTween.tweens = new ArrayList();
            if (Resources.FindObjectsOfTypeAll<iTween>().Any(tween => tween != null
                && tween.gameObject.scene.IsValid() && tween.gameObject.scene.isLoaded))
                throw new InvalidOperationException("Canonical Tween stop left a loaded component alive.");
            var reader = new LoadingNativeSchedulerDigest();
            try
            {
                removedTweenDestroyCallbacks = reader.CancelRetiredObjectDestroys(instance => {
                    if (!retiredTweens.TryGetValue(instance, out var tween)) return null;
                    return () => { if (tween != null) throw new InvalidOperationException("Canonical retired Tween is still alive."); };
                });
            }
            finally { reader.Release(); }
            return retiredTweens.Count;
        }

        internal static void ResetFsmOrder(BoundaryObjectIdentity identities)
        {
            var field = typeof(PlayMakerFSM).GetField("fsmList", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingFieldException("PlayMakerFSM.fsmList");
            var list = field.GetValue(null) as System.Collections.Generic.List<PlayMakerFSM>
                ?? throw new InvalidOperationException("Canonical PlayMaker list binding differs.");
            if (list.Count > 200000 || list.Any(fsm => fsm == null || !fsm.gameObject.scene.IsValid() || !fsm.gameObject.scene.isLoaded))
                throw new InvalidOperationException("Canonical PlayMaker list contains an unregistered owner.");
            list.Sort((left, right) => StringComparer.Ordinal.Compare(identities.Get(left), identities.Get(right)));
        }
    }
}
