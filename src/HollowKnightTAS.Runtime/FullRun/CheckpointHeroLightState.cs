using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // The persistent damage-light FSM continues its original Wait after C.
    // A running scale-to tween uses its explicit native lifecycle adapter.
    [Serializable]
    internal sealed class CheckpointHeroLightState
    {
        public CheckpointPersistentFsmState Fsm = null!;
        public CheckpointPersistentFsmState ColorFader = null!;
        public CheckpointHeroLightTweenState? Tween;
        public Vector3 LocalPosition, LocalScale;
        public Quaternion LocalRotation;
        public float[] Color = null!;
        public bool RendererEnabled, FsmEnabled, ActiveSelf;
        public int TweenEventIdCount;
        private static readonly FieldInfo RawFsm = typeof(PlayMakerFSM).GetField("fsm", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo States = typeof(Fsm).GetField("states", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo Actions = typeof(FsmState).GetField("actions", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo TweenEvents = typeof(iTweenFsmAction).GetField("itweenEvents", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static PlayMakerFSM Bind(HeroController hero)
        {
            var light = hero.heroLight;
            if (light == null || light.gameObject.name != "HeroLight" || light.transform.parent != hero.transform)
                throw new InvalidDataException("Checkpoint HeroLight owner binding differs.");
            var fsm = light.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "HeroLight Control");
            if (!light.GetComponents<Component>().Where(c => !(c is iTween) && !(c is iTweenFSMEvents)).Select(c => c.GetType()).OrderBy(t => t.FullName, StringComparer.Ordinal)
                .SequenceEqual(new[] { typeof(Transform), typeof(SpriteRenderer), typeof(PlayMakerFSM), typeof(PlayMakerFSM) }.OrderBy(t => t.FullName, StringComparer.Ordinal))
                || !light.GetComponents<PlayMakerFSM>().Select(f => f.FsmName).OrderBy(n => n, StringComparer.Ordinal)
                    .SequenceEqual(new[] { "HeroLight Control", "color_fader" }))
                throw new InvalidDataException("Checkpoint HeroLight layout: state=" + fsm.ActiveStateName + " components="
                    + string.Join(",", light.GetComponents<Component>().Select(c => c.GetType().FullName)));
            return fsm;
        }
        private static PlayMakerFSM BindColorFader(HeroController hero)
        {
            var fsm = hero.heroLight.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "color_fader");
            if (fsm.ActiveStateName != "Init" || fsm.FsmVariables.FindFsmGameObject("Self").Value != hero.heroLight.gameObject)
                throw new InvalidDataException("Checkpoint HeroLight color_fader needs an active color/material continuation: " + fsm.ActiveStateName);
            return fsm;
        }
        private static iTweenFsmAction[] BoundTweenActions(PlayMakerFSM component)
        {
            var fsm = (Fsm)RawFsm.GetValue(component);
            var result = ((FsmState[])States.GetValue(fsm)).SelectMany(s => (FsmStateAction[]?)Actions.GetValue(s) ?? Array.Empty<FsmStateAction>())
                .OfType<iTweenFsmAction>().ToArray();
            foreach (var action in result)
            {
                if (!(action is iTweenScaleTo scale) || scale.gameObject.OwnerOption != OwnerDefaultOption.UseOwner
                    || !scale.transformScale.IsNone || scale.transformScale.Value != null
                    || (iTweenFSMEvents?)TweenEvents.GetValue(action) is iTweenFSMEvents evt && evt != null
                        && (!ReferenceEquals(evt.gameObject, component.gameObject) || !ReferenceEquals(evt.itweenFSMAction, action)))
                    throw new InvalidDataException("Checkpoint HeroLight tween action needs an explicit reference/continuation adapter.");
            }
            return result;
        }
        internal static CheckpointHeroLightState Capture(HeroController hero)
        {
            var fsm = Bind(hero); BoundTweenActions(fsm); var fader = BindColorFader(hero);
            var tween = CheckpointHeroLightTweenState.Capture(fsm);
            var light = hero.heroLight; var color = light.color;
            return new CheckpointHeroLightState { Fsm = CheckpointPersistentFsmState.Capture(fsm),
                ColorFader = CheckpointPersistentFsmState.Capture(fader),
                Tween = tween,
                LocalPosition = light.transform.localPosition, LocalRotation = light.transform.localRotation, LocalScale = light.transform.localScale,
                Color = new[] { color.r, color.g, color.b, color.a }, RendererEnabled = light.enabled,
                FsmEnabled = fsm.enabled, ActiveSelf = light.gameObject.activeSelf, TweenEventIdCount = iTweenFSMEvents.itweenIDCount };
        }
        internal void PrepareObject(HeroController hero)
        {
            var fsm = Bind(hero);
            if (Tween == null)
            {
                if (fsm.GetComponents<iTween>().Length != 0 || fsm.GetComponents<iTweenFSMEvents>().Length != 0)
                    throw new InvalidDataException("Prepared HeroLight has an unexpected native tween.");
            }
            else Tween.PrepareObject(fsm);
        }
        internal Action PrepareApply(HeroController hero)
        {
            var fsm = Bind(hero); var tweenActions = BoundTweenActions(fsm); var fader = BindColorFader(hero);
            var light = hero.heroLight;
            if (Fsm == null || ColorFader == null || ColorFader.Active != "Init" || Color == null || Color.Length != 4 || ActiveSelf != light.gameObject.activeSelf || FsmEnabled != fsm.enabled
                || new[] { LocalPosition.x, LocalPosition.y, LocalPosition.z, LocalRotation.x, LocalRotation.y, LocalRotation.z, LocalRotation.w,
                    LocalScale.x, LocalScale.y, LocalScale.z }.Concat(Color).Any(v => float.IsNaN(v) || float.IsInfinity(v)))
                throw new InvalidDataException("Checkpoint HeroLight native value or lifecycle binding differs.");
            var apply = Fsm.PrepareApply(fsm);
            var applyFader = ColorFader.PrepareApply(fader);
            // PrepareApply can load source-visited actions through the normal
            // action factory. Rebind their completed, destroyed event objects.
            tweenActions = BoundTweenActions(fsm);
            var applyTween = Tween?.PrepareApply(fsm);
            if (Tween == null && (fsm.GetComponents<iTween>().Length != 0 || fsm.GetComponents<iTweenFSMEvents>().Length != 0))
                throw new InvalidDataException("Restored HeroLight contains an unexpected native tween.");
            return () => { apply(); applyFader(); foreach (var action in tweenActions)
                    if ((iTweenFSMEvents?)TweenEvents.GetValue(action) == null) TweenEvents.SetValue(action, null);
                light.transform.localPosition = LocalPosition; light.transform.localRotation = LocalRotation; light.transform.localScale = LocalScale;
                light.color = new Color(Color[0], Color[1], Color[2], Color[3]); light.enabled = RendererEnabled;
                iTweenFSMEvents.itweenIDCount = TweenEventIdCount; applyTween?.Invoke(); };
        }
        internal static IEnumerable<KeyValuePair<string, object?>> Observe(HeroController hero)
        {
            var light = hero.heroLight;
            if (light == null) yield break;
            var fsm = light.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "HeroLight Control");
            yield return new KeyValuePair<string, object?>("heroLight.state", fsm.ActiveStateName);
            var scale = light.transform.localScale;
            yield return new KeyValuePair<string, object?>("heroLight.scaleX", scale.x);
            yield return new KeyValuePair<string, object?>("heroLight.scaleY", scale.y);
            yield return new KeyValuePair<string, object?>("heroLight.scaleZ", scale.z);
            yield return new KeyValuePair<string, object?>("heroLight.tweenEventIdCount", iTweenFSMEvents.itweenIDCount);
            var raw = (Fsm)RawFsm.GetValue(fsm);
            var active = (FsmState?)typeof(Fsm).GetField("activeState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(raw);
            var actions = active == null ? null : (FsmStateAction[]?)Actions.GetValue(active);
            foreach (var wait in actions?.OfType<Wait>() ?? Enumerable.Empty<Wait>())
                foreach (var field in CheckpointScalarFields.Declared(typeof(Wait)).Where(f => f.FieldType == typeof(float)))
                    yield return new KeyValuePair<string, object?>("heroLight.wait." + field.Name, field.GetValue(wait));
        }
    }
}
