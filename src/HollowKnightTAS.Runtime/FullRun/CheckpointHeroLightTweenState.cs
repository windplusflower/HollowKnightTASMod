using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // One already-started scale-to tween on the bound persistent HeroLight.
    // Original delegates, arguments and callback action are rebound locally;
    // no OnEnter/Start callback or generated random ID is replayed at C.
    [Serializable]
    internal sealed class CheckpointHeroLightTweenState
    {
        public Dictionary<string, string> Fields = null!, EventFields = null!;
        public Vector3[] Vectors = null!;
        public Argument[] Arguments = null!;
        public byte[] Lifecycle = null!, EventLifecycle = null!;
        public string ActionState = "";
        public int ActionIndex;
        [Serializable] internal sealed class Argument
        { public string Key = "", Tag = ""; public Dictionary<string, string> Value = null!; }
        private sealed class ValueBox
        {
            public string? Text;
            public float Number;
            public bool Boolean;
            public Vector3 Vector;
            public iTween.EaseType Ease;
            public iTween.LoopType Loop;
        }
        private static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidDataException("HeroLight tween field binding is absent: " + name);
        private static readonly FieldInfo[] Scalars = CheckpointScalarFields.Declared(typeof(iTween)).Where(f => f.FieldType.IsPrimitive && f.FieldType != typeof(IntPtr)
            && f.FieldType != typeof(UIntPtr) || f.FieldType.IsEnum || f.FieldType == typeof(string) || f.FieldType == typeof(Vector3)).ToArray();
        private static readonly FieldInfo[] EventScalars = CheckpointScalarFields.Declared(typeof(iTweenFSMEvents)).Where(f => f.FieldType.IsPrimitive).ToArray();
        private static readonly FieldInfo Args = Field(typeof(iTween), "tweenArguments"), VectorArray = Field(typeof(iTween), "vector3s");
        private static readonly FieldInfo Ease = Field(typeof(iTween), "ease"), Apply = Field(typeof(iTween), "apply");
        private static readonly FieldInfo Events = Field(typeof(iTweenFsmAction), "itweenEvents");
        private static readonly FieldInfo CachedPointer = Field(typeof(UnityEngine.Object), "m_CachedPtr");
        private static readonly string[] ArgumentKeys = { "delay", "easetype", "id", "ignoretimescale", "looptype", "method", "name",
            "oncomplete", "oncompleteparams", "onstart", "onstartparams", "scale", "speed", "target", "time", "type" };
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr read);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr written);
        private static IntPtr LifecycleAddress(MonoBehaviour value) => IntPtr.Add((IntPtr)CachedPointer.GetValue(value)!, 292);
        private static byte[] ReadLifecycle(MonoBehaviour value)
        {
            var pointer = (IntPtr)CachedPointer.GetValue(value)!; var b = new byte[5];
            if (pointer == IntPtr.Zero || !ReadProcessMemory(GetCurrentProcess(), IntPtr.Add(pointer, 292), b, new UIntPtr(5), out var read)
                || read.ToUInt64() != 5 || b.Any(v => v > 1) || b[1] > b[0] || b[3] != 0)
                throw new InvalidDataException("HeroLight tween native lifecycle differs.");
            return b.Take(2).ToArray();
        }
        private static void WriteLifecycle(MonoBehaviour value, byte[] b)
        {
            if (b == null || b.Length != 2 || b.Any(v => v > 1) || b[1] > b[0] || value.isActiveAndEnabled
                || !WriteProcessMemory(GetCurrentProcess(), LifecycleAddress(value), b, new UIntPtr(2), out var written)
                || written.ToUInt64() != 2 || !ReadLifecycle(value).SequenceEqual(b))
                throw new InvalidDataException("HeroLight tween lifecycle write verification differs.");
        }
        private static FieldInfo ValueField(string tag) => Field(typeof(ValueBox), tag);
        private static Argument CaptureArgument(DictionaryEntry pair, GameObject owner)
        {
            var key = pair.Key as string;
            if (key == null || !ArgumentKeys.Contains(key)) throw new InvalidDataException("HeroLight tween argument needs an adapter.");
            if (pair.Value is GameObject go)
            {
                if (key != "target" || !ReferenceEquals(go, owner)) throw new InvalidDataException("HeroLight tween argument owner differs.");
                return new Argument { Key = key, Tag = "owner", Value = new Dictionary<string, string>() };
            }
            var box = new ValueBox(); string tag;
            if (pair.Value is string text) { tag = nameof(ValueBox.Text); box.Text = text; }
            else if (pair.Value is float number) { tag = nameof(ValueBox.Number); box.Number = number; }
            else if (pair.Value is bool boolean) { tag = nameof(ValueBox.Boolean); box.Boolean = boolean; }
            else if (pair.Value is Vector3 vector) { tag = nameof(ValueBox.Vector); box.Vector = vector; }
            else if (pair.Value is iTween.EaseType ease) { tag = nameof(ValueBox.Ease); box.Ease = ease; }
            else if (pair.Value is iTween.LoopType loop) { tag = nameof(ValueBox.Loop); box.Loop = loop; }
            else throw new InvalidDataException("HeroLight tween argument value needs an adapter: " + key);
            return new Argument { Key = key, Tag = tag, Value = CheckpointScalarFields.Capture(box, new[] { ValueField(tag) }) };
        }
        private Hashtable DecodeArguments(GameObject owner)
        {
            if (Arguments == null || Arguments.Length > 16 || !Arguments.Select(a => a.Key).SequenceEqual(Arguments.Select(a => a.Key).OrderBy(k => k, StringComparer.Ordinal)))
                throw new InvalidDataException("HeroLight tween argument layout differs.");
            var result = new Hashtable();
            foreach (var argument in Arguments)
            {
                if (!ArgumentKeys.Contains(argument.Key) || result.ContainsKey(argument.Key)) throw new InvalidDataException("HeroLight tween argument key differs.");
                if (argument.Tag == "owner")
                {
                    if (argument.Key != "target" || argument.Value.Count != 0) throw new InvalidDataException("HeroLight tween owner alias differs.");
                    result.Add(argument.Key, owner); continue;
                }
                var box = new ValueBox(); var field = ValueField(argument.Tag);
                CheckpointScalarFields.PrepareApply(box, new[] { field }, argument.Value)();
                result.Add(argument.Key, field.GetValue(box));
            }
            if (!(result["id"] is string id) || id.Length != 15 || !ReferenceEquals(result["target"], owner)
                || !Equals(result["type"], "scale") || !Equals(result["method"], "to")
                || !Equals(result["onstart"], "iTweenOnStart") || !Equals(result["oncomplete"], "iTweenOnComplete")
                || !Equals(result["easetype"], iTween.EaseType.linear) || !Equals(result["looptype"], iTween.LoopType.none)
                || !Equals(result["ignoretimescale"], false) || !Equals(result["delay"], 0f))
                throw new InvalidDataException("HeroLight tween arguments are outside the started linear scale-to family.");
            return result;
        }
        private static void RequireRunning(iTween tween)
        {
            if (tween.type != "scale" || tween.method != "to" || tween.easeType != iTween.EaseType.linear || tween.loopType != iTween.LoopType.none
                || !tween.isRunning || tween.isPaused || tween.delay != 0f || !(tween.time > 0f) || float.IsInfinity(tween.time)
                || new[] { "physics", "loop", "reverse", "wasPaused", "useRealTime" }.Any(n => (bool)Field(typeof(iTween), n).GetValue(tween))
                || new[] { "audioSource", "vector2s", "colors", "floats", "rects", "path" }.Any(n => !ReferenceEquals(Field(typeof(iTween), n).GetValue(tween), null)))
                throw new InvalidDataException("HeroLight tween requires a running, undelayed linear scale-to continuation.");
            foreach (var pair in new[] { Tuple.Create(Ease, "linear"), Tuple.Create(Apply, "ApplyScaleToTargets") })
            {
                var value = (Delegate?)pair.Item1.GetValue(tween);
                if (value == null || value.GetInvocationList().Length != 1 || !ReferenceEquals(value.Target, tween)
                    || value.Method.DeclaringType != typeof(iTween) || value.Method.Name != pair.Item2)
                    throw new InvalidDataException("HeroLight tween delegate binding differs.");
            }
        }
        private static iTweenScaleTo Action(PlayMakerFSM fsm, string state, int index)
        {
            var actions = fsm.Fsm.GetState(state).Actions;
            if (index < 0 || index >= actions.Length || !(actions[index] is iTweenScaleTo action))
                throw new InvalidDataException("HeroLight tween callback action ordinal differs.");
            return action;
        }
        internal static CheckpointHeroLightTweenState? Capture(PlayMakerFSM fsm)
        {
            var tweens = fsm.GetComponents<iTween>(); var events = fsm.GetComponents<iTweenFSMEvents>();
            if (tweens.Length == 0 && events.Length == 0) return null;
            if (tweens.Length != 1 || events.Length != 1) throw new InvalidDataException("HeroLight tween multiplicity needs an adapter.");
            LoadingEngineClockProbe.RequirePinnedEngine();
            var tween = tweens[0]; var evt = events[0]; RequireRunning(tween);
            var args = (Hashtable)Args.GetValue(tween);
            if (!tween.enabled || !evt.enabled || iTween.tweens.Count != 1 || !ReferenceEquals(iTween.tweens[0], args)
                || !(evt.itweenFSMAction is iTweenScaleTo action) || !ReferenceEquals(action.Fsm, fsm.Fsm)
                || !ReferenceEquals(Events.GetValue(action), evt) || action.State.Name != fsm.ActiveStateName)
                throw new InvalidDataException("HeroLight tween registry/callback binding differs: count=" + iTween.tweens.Count);
            var result = new CheckpointHeroLightTweenState { Fields = CheckpointScalarFields.Capture(tween, Scalars),
                EventFields = CheckpointScalarFields.Capture(evt, EventScalars), Vectors = (Vector3[])((Vector3[])VectorArray.GetValue(tween)).Clone(),
                Arguments = args.Cast<DictionaryEntry>().OrderBy(p => (string)p.Key, StringComparer.Ordinal).Select(p => CaptureArgument(p, fsm.gameObject)).ToArray(),
                Lifecycle = ReadLifecycle(tween), EventLifecycle = ReadLifecycle(evt), ActionState = action.State.Name,
                ActionIndex = Array.FindIndex(action.State.Actions, a => ReferenceEquals(a, action)) };
            result.RequireData(); result.DecodeArguments(fsm.gameObject);
            Modding.Logger.Log("[HKTAS] Captured HeroLight running tween: state=" + result.ActionState + " time=" + tween.time);
            return result;
        }
        private void RequireData()
        {
            if (Vectors == null || Vectors.Length != 3 || Vectors.SelectMany(v => new[] { v.x, v.y, v.z }).Any(v => float.IsNaN(v) || float.IsInfinity(v))
                || Lifecycle == null || !Lifecycle.SequenceEqual(new byte[] { 1, 1 }) || EventLifecycle == null || EventLifecycle.Length != 2
                || EventLifecycle.Any(v => v > 1) || EventLifecycle[1] > EventLifecycle[0])
                throw new InvalidDataException("HeroLight tween vectors or completed native Start differ.");
        }
        internal void PrepareObject(PlayMakerFSM fsm)
        {
            RequireData(); var args = DecodeArguments(fsm.gameObject);
            if (fsm.GetComponents<iTween>().Length != 0 || fsm.GetComponents<iTweenFSMEvents>().Length != 0)
                throw new InvalidDataException("Prepared HeroLight already has a tween.");
            var action = Action(fsm, ActionState, ActionIndex);
            var evt = fsm.gameObject.AddComponent<iTweenFSMEvents>(); evt.enabled = false; evt.itweenFSMAction = action;
            CheckpointScalarFields.PrepareApply(evt, EventScalars, EventFields)(); Events.SetValue(action, evt);
            // The captured ID bypasses GenerateID. Awake follows the original
            // factory path; park before the engine's first Start/Update.
            iTween.ScaleTo(fsm.gameObject, args);
            fsm.GetComponents<iTween>().Single().enabled = false;
        }
        internal Action PrepareApply(PlayMakerFSM fsm)
        {
            LoadingEngineClockProbe.RequirePinnedEngine(); RequireData();
            var tween = fsm.GetComponents<iTween>().Single(); var evt = fsm.GetComponents<iTweenFSMEvents>().Single();
            var action = Action(fsm, ActionState, ActionIndex); var args = DecodeArguments(fsm.gameObject);
            var currentArgs = (Hashtable)Args.GetValue(tween);
            if (tween.enabled || evt.enabled || !ReferenceEquals(evt.itweenFSMAction, action) || !ReferenceEquals(Events.GetValue(action), evt)
                || iTween.tweens.Count != 1 || !ReferenceEquals(iTween.tweens[0], currentArgs) || tween.id != (string)args["id"])
                throw new InvalidDataException("Prepared HeroLight tween is not its parked original factory instance.");
            ReadLifecycle(tween); ReadLifecycle(evt);
            var applyFields = CheckpointScalarFields.PrepareApply(tween, Scalars, Fields);
            var applyEvents = CheckpointScalarFields.PrepareApply(evt, EventScalars, EventFields);
            var vectors = (Vector3[])Vectors.Clone();
            var ease = Delegate.CreateDelegate(Ease.FieldType, tween, "linear");
            var apply = Delegate.CreateDelegate(Apply.FieldType, tween, "ApplyScaleToTargets");
            return () => { applyFields(); applyEvents(); VectorArray.SetValue(tween, vectors);
                Ease.SetValue(tween, ease); Apply.SetValue(tween, apply); currentArgs.Clear();
                foreach (DictionaryEntry pair in args) currentArgs.Add(pair.Key, pair.Value);
                WriteLifecycle(tween, Lifecycle); WriteLifecycle(evt, EventLifecycle); RequireRunning(tween);
                evt.enabled = true; tween.enabled = true; };
        }
    }
}
