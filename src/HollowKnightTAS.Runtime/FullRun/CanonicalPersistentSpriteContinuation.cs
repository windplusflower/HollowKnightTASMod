using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Original managed continuations only. No Play/Enter/SendEvent or delegate
    // construction is allowed here: target callbacks must already exist.
    internal static class CanonicalPersistentSpriteContinuation
    {
        internal const string Prefix = "R2/PersistentSprites/health/";
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo Raw = typeof(PlayMakerFSM).GetField("fsm", Flags)!;
        private static readonly FieldInfo States = typeof(Fsm).GetField("states", Flags)!;
        private static readonly FieldInfo Actions = typeof(FsmState).GetField("actions", Flags)!;
        internal static Type[] OriginalActionTypes()
        {
            var assembly = typeof(GameManager).Assembly;
            var respawn = assembly.GetType("GetRespawningHero", true)!;
            var bound = assembly.GetType("GGCheckBoundHeart", true)!;
            var source = bound.GetNestedType("CheckSource", BindingFlags.Public | BindingFlags.NonPublic)!;
            var baseLayout = CanonicalSoulOrbCarry.BoundSoulLayout.Substring(CanonicalSoulOrbCarry.BoundSoulLayout.IndexOf("HutongGames.PlayMaker.FsmStateAction/", StringComparison.Ordinal));
            void Require(Type type, string original) {
                var actual = string.Join(";", CanonicalCarryGraphCodec.Fields(type).Select(field => field.DeclaringType!.FullName + "/" + field.Name + "=" + field.FieldType.FullName));
                if (type.Module.ModuleVersionId.ToString("D") != "2b54cdaf-7a93-4f0e-b297-d50c4090ea9f" || actual != original + ";" + baseLayout)
                    throw new InvalidDataException("Original health concrete action field layout differs: " + type.FullName);
            }
            Require(respawn, "GetRespawningHero/variable=HutongGames.PlayMaker.FsmBool");
            Require(bound, "FSMUtility+CheckFsmStateAction/falseEvent=HutongGames.PlayMaker.FsmEvent;FSMUtility+CheckFsmStateAction/trueEvent=HutongGames.PlayMaker.FsmEvent;GGCheckBoundHeart/checkSource=GGCheckBoundHeart+CheckSource;GGCheckBoundHeart/healthNumber=HutongGames.PlayMaker.FsmInt");
            if (!source.IsEnum || Enum.GetUnderlyingType(source) != typeof(int) || !Enum.GetNames(source).SequenceEqual(new[] { "Regular", "Joni" })
                || !Enum.GetValues(source).Cast<object>().Select(Convert.ToInt32).SequenceEqual(new[] { 0, 1 }))
                throw new InvalidDataException("Original bound-heart enum layout differs.");
            return new[] { respawn, bound, source };
        }
        internal static readonly string[] Keys = { "HudCamera/Hud Canvas/Health/Health 1", "HudCamera/Hud Canvas/Health/Health 2",
            "HudCamera/Hud Canvas/Health/Joni Health", "HudCamera/Hud Canvas/Soul Orb/SoulOrb_fill/Liquid" };
        internal sealed class Data
        {
            public string[] Fields = Array.Empty<string>(), Owners = Array.Empty<string>();
            public object?[][] Values = Array.Empty<object?[]>();
            public object?[][] CallbackArguments = Array.Empty<object?[]>();
            public byte[][] Lifecycle = Array.Empty<byte[]>();
            public int GlobalState;
        }
        internal static FieldInfo[] AnimatorFields()
        {
            var type = typeof(tk2dSpriteAnimator);
            var fields = type.GetFields(Flags | BindingFlags.DeclaredOnly);
            var names = new[] { "library", "defaultClipId", "playAutomatically", "currentClip", "clipTime", "clipFps", "previousFrame",
                "AnimationCompleted", "AnimationEventTriggered", "state", "_sprite" };
            var types = new[] { typeof(tk2dSpriteAnimation), typeof(int), typeof(bool), typeof(tk2dSpriteAnimationClip), typeof(float), typeof(float), typeof(int),
                typeof(Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip>), typeof(Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip, int>),
                type.GetNestedType("State", BindingFlags.Public | BindingFlags.NonPublic)!, typeof(tk2dBaseSprite) };
            if (type.Module.ModuleVersionId.ToString("D") != "2b54cdaf-7a93-4f0e-b297-d50c4090ea9f" || fields.Length != 11
                || fields.Where((field, index) => field.Name != names[index] || field.FieldType != types[index]).Any())
                throw new InvalidDataException("Persistent animator original eleven-field ABI differs.");
            return fields;
        }
        internal static SortedDictionary<string, tk2dSpriteAnimator> Animators()
        {
            var sprites = CanonicalPersistentSpriteDirectory.Resolve();
            var result = new SortedDictionary<string, tk2dSpriteAnimator>(StringComparer.Ordinal);
            foreach (var key in Keys) {
                var values = sprites[key].GetComponents<tk2dSpriteAnimator>();
                if (values.Length != 1 || values[0].GetType() != typeof(tk2dSpriteAnimator)) throw new InvalidDataException("Persistent original animator owner differs.");
                result.Add(key, values[0]);
            }
            return result;
        }
        internal static PlayMakerFSM HealthOwner(int index)
        {
            if (index < 0 || index > 1) throw new InvalidDataException("Health owner index differs.");
            var sprite = CanonicalPersistentSpriteDirectory.Resolve()[Keys[index]];
            var owners = sprite.GetComponents<PlayMakerFSM>().Where(owner => ((Fsm)Raw.GetValue(owner)).Name == "health_display").ToArray();
            if (owners.Length != 1) throw new InvalidDataException("Original health_display identity differs.");
            return owners[0];
        }
        internal static SortedDictionary<string, object> HealthRoots()
        {
            var result = new SortedDictionary<string, object>(StringComparer.Ordinal);
            for (var owner = 0; owner < 2; owner++) {
                var raw = (Fsm)Raw.GetValue(HealthOwner(owner)); var states = (FsmState[])States.GetValue(raw);
                if (states == null || states.Length != 27) throw new InvalidDataException("Original health state count differs.");
                var layout = string.Join("\n", states.Select(state => state.Name + "|" + string.Join(",", ((FsmStateAction[])Actions.GetValue(state)).Select(action => action.GetType().FullName))));
                if (HollowKnightTAS.Core.Cryptography.Sha256Utility.ComputeHex(System.Text.Encoding.UTF8.GetBytes(layout)) != "c5e96b56c14773f4bbafdd1917221a07628eb5ddebecbc6ff1ea8b8e8cbe697f")
                    throw new InvalidDataException("Original health 27-state/99-action directory differs.");
                var root = Prefix + owner; result.Add(root, raw);
                for (var s = 0; s < states.Length; s++) {
                    var state = states[s]; var actions = (FsmStateAction[])Actions.GetValue(state);
                    if (actions == null || actions.Length > 128) throw new InvalidDataException("Original health action count differs.");
                    var key = root + "/state[" + s + "]"; result.Add(key, state);
                    for (var a = 0; a < actions.Length; a++) {
                        if (actions[a] == null || actions[a].GetType().Module != typeof(GameManager).Module
                            && actions[a].GetType().Module != typeof(Fsm).Module) throw new InvalidDataException("Foreign health action.");
                        result.Add(key + "/action[" + a + "]", actions[a]);
                    }
                }
            }
            if (result.Values.Distinct(new References()).Count() != result.Count) throw new InvalidDataException("Health original root alias differs.");
            return result;
        }
        internal static SortedDictionary<string, object> OwnerDefaultVariableSlots()
        {
            var result = new SortedDictionary<string, object>(StringComparer.Ordinal);
            var ownerVariable = typeof(FsmOwnerDefault).GetField("gameObject", Flags)!;
            foreach (var pair in HealthRoots()) {
                if (pair.Value is FsmState state) {
                    var data = typeof(FsmState).GetField("actionData", Flags)!.GetValue(state);
                    if (data == null) continue;
                    var parameters = data.GetType().GetField("fsmOwnerDefaultParams", Flags)!.GetValue(data) as System.Collections.IList;
                    if (parameters == null || parameters.Count > 256) throw new InvalidDataException("Health original serialized owner-default parameter directory differs.");
                    for (var i = 0; i < parameters.Count; i++) if (parameters[i] is FsmOwnerDefault parameter) {
                        var value = ownerVariable.GetValue(parameter); if (value != null) result.Add(pair.Key + "/actionData/fsmOwnerDefaultParams[" + i + "]/gameObject", value);
                    }
                } else if (pair.Value is FsmStateAction action) foreach (var field in CanonicalCarryGraphCodec.Fields(action.GetType()).Where(field => field.FieldType == typeof(FsmOwnerDefault))) {
                    var parameter = field.GetValue(action); if (parameter == null) continue;
                    var value = ownerVariable.GetValue(parameter); if (value != null) result.Add(pair.Key + "/" + field.DeclaringType!.FullName + "/" + field.Name + "/gameObject", value);
                }
            }
            return result;
        }
        internal static Data Capture()
        {
            var owners = Animators(); var fields = AnimatorFields();
            var lifecycle = new List<byte[]>();
            using (new LoadingEngineClockProbe.CheckpointValidation()) {
                foreach (var owner in owners.Values) lifecycle.Add(BoundaryMonoLifecycle.Read(owner));
                for (var i = 0; i < 2; i++) lifecycle.Add(BoundaryMonoLifecycle.Read(HealthOwner(i)));
            }
            return new Data { Owners = owners.Keys.ToArray(), Fields = fields.Select(field => field.Name + "=" + field.FieldType.FullName).ToArray(),
                Values = owners.Values.Select(owner => fields.Select(field => field.GetValue(owner)).ToArray()).ToArray(), Lifecycle = lifecycle.ToArray(),
                GlobalState = Convert.ToInt32(typeof(tk2dSpriteAnimator).GetField("globalState", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)),
                CallbackArguments = owners.Values.SelectMany(owner => AnimatorFields().Where(field => typeof(Delegate).IsAssignableFrom(field.FieldType))
                    .SelectMany(field => ((Delegate?)field.GetValue(owner))?.GetInvocationList() ?? Array.Empty<Delegate>()))
                    .Select(call => call.Target is Tk2dPlayAnimationWithEvents action
                        ? new object?[] { call, action, action.clipName, action.animationTriggerEvent, action.animationCompleteEvent }
                        : throw new InvalidDataException("Unknown persistent callback argument owner.")).ToArray() };
        }
        internal static SortedDictionary<string, object> ExternalDirectory()
        {
            var result = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var pair in Animators()) {
                var library = (tk2dSpriteAnimation)AnimatorFields()[0].GetValue(pair.Value);
                if (library == null || library.clips == null || library.clips.Length > 4096) throw new InvalidDataException("Original animator library differs.");
                for (var index = 0; index < library.clips.Length; index++) {
                    var clip = library.clips[index];
                    if (clip == null || result.Values.Any(value => ReferenceEquals(value, clip))) continue;
                    result.Add("PersistentAnimator/clip/" + pair.Key + "/" + index + "/" + CanonicalPersistentSpriteAnimationDiagnostic.ClipHash(clip), clip);
                }
                foreach (var field in AnimatorFields().Where(field => typeof(Delegate).IsAssignableFrom(field.FieldType))) {
                    var callback = (Delegate?)field.GetValue(pair.Value); if (callback == null) continue;
                    var calls = callback.GetInvocationList();
                    if (calls.Length != 1) throw new InvalidDataException("Persistent animator callback count differs.");
                    var descriptions = new List<string>();
                    foreach (var call in calls) {
                        if (!(call.Target is Tk2dPlayAnimationWithEvents action) || call.Method.DeclaringType != typeof(Tk2dPlayAnimationWithEvents)
                            || call.Method.Module != typeof(GameManager).Module || call.Method.Name != (field.Name == "AnimationCompleted" ? "AnimationCompleteDelegate" : "AnimationEventDelegate"))
                            throw new InvalidDataException("Unknown persistent animator callback.");
                        var actionState = (FsmState)typeof(FsmStateAction).GetField("fsmState", Flags)!.GetValue(action);
                        var fsmOwner = action.Fsm == null ? null : typeof(Fsm).GetField("owner", Flags)!.GetValue(action.Fsm) as PlayMakerFSM;
                        if (fsmOwner == null || !ReferenceEquals(Raw.GetValue(fsmOwner), action.Fsm) || !ReferenceEquals(typeof(FsmState).GetField("fsm", Flags)!.GetValue(actionState), action.Fsm)
                            || !ReferenceEquals(typeof(Tk2dPlayAnimationWithEvents).GetField("_sprite", Flags)!.GetValue(action), pair.Value))
                            throw new InvalidDataException("Persistent animator callback original owner alias differs.");
                        // Only FSMs on this exact persistent body can subscribe.
                        var fsms = pair.Value.GetComponents<PlayMakerFSM>();
                        var matches = Enumerable.Range(0, fsms.Length).Where(index => ReferenceEquals(fsms[index], fsmOwner)).ToArray();
                        var states = (FsmState[])States.GetValue(action.Fsm);
                        var stateMatches = Enumerable.Range(0, states.Length).Where(index => ReferenceEquals(states[index], actionState)).ToArray();
                        if (matches.Length != 1 || stateMatches.Length != 1) throw new InvalidDataException("Persistent animator callback FSM directory differs.");
                        var actions = (FsmStateAction[])Actions.GetValue(actionState);
                        var actionMatches = Enumerable.Range(0, actions.Length).Where(index => ReferenceEquals(actions[index], action)).ToArray();
                        if (actionMatches.Length != 1) throw new InvalidDataException("Persistent animator callback action identity differs.");
                        var actionKey = pair.Key + "/" + matches[0] + "/" + action.Fsm.Name + "/" + stateMatches[0] + "/" + actionState.Name + "/" + actionMatches[0];
                        descriptions.Add(actionKey + "/" + call.Method.MetadataToken + "/" + action.clipName.Value + "/" + action.animationTriggerEvent?.Name + "/" + action.animationCompleteEvent?.Name);
                        void AddAlias(string key, object value) {
                            if (result.TryGetValue(key, out var previous) ? !ReferenceEquals(previous, value) : result.Values.Any(other => ReferenceEquals(other, value)))
                                throw new InvalidDataException("Persistent callback argument binding is ambiguous.");
                            result[key] = value;
                        }
                        AddAlias("PersistentAnimator/action/" + actionKey, action);
                        AddAlias("PersistentAnimator/clip-name/" + actionKey + "/" + action.clipName.Value, action.clipName);
                    }
                    var descriptor = "PersistentAnimator/callback/" + pair.Key + "/" + field.Name + "/" + string.Join(";", descriptions);
                    if (result.Values.Any(value => ReferenceEquals(value, callback))) throw new InvalidDataException("Persistent animator callback cross-slot alias is unsupported.");
                    result.Add(descriptor, callback);
                }
            }
            return result;
        }
        internal static Action Prepare(Data source, Data current, CanonicalCarryGraphCodec codec, out Action verify)
        {
            var owners = Animators(); var fields = AnimatorFields();
            if (!source.Owners.SequenceEqual(current.Owners) || !source.Fields.SequenceEqual(current.Fields) || source.Values.Length != 4
                || source.Values.Any(row => row == null || row.Length != 11) || source.Lifecycle.Length != 6
                || source.Lifecycle.Where((bytes, index) => bytes == null || !bytes.SequenceEqual(current.Lifecycle[index])).Any()
                || source.GlobalState != current.GlobalState)
                throw new InvalidDataException("Persistent animator source owner/ABI/global state/native lifecycle differs.");
            var instances = owners.Values.ToArray();
            foreach (var row in source.CallbackArguments)
                if (row == null || row.Length != 5 || !(row[0] is Delegate call) || !(row[1] is Tk2dPlayAnimationWithEvents action)
                    || !ReferenceEquals(call.Target, action) || !ReferenceEquals(row[2], action.clipName)
                    || !ReferenceEquals(row[3], action.animationTriggerEvent) || !ReferenceEquals(row[4], action.animationCompleteEvent))
                    throw new InvalidDataException("Persistent callback closed action/event/parameter aliases differ.");
            for (var owner = 0; owner < instances.Length; owner++) for (var field = 0; field < fields.Length; field++) {
                var value = source.Values[owner][field];
                if (value == null ? fields[field].FieldType.IsValueType : !fields[field].FieldType.IsInstanceOfType(value))
                    throw new InvalidDataException("Persistent animator source field type differs.");
                if ((field == 0 || field == 10) && !ReferenceEquals(value, fields[field].GetValue(instances[owner])))
                    throw new InvalidDataException("Persistent animator original library/sprite alias differs.");
            }
            void Verify() {
                var now = Capture();
                if (now.GlobalState != source.GlobalState || !now.Owners.SequenceEqual(source.Owners)
                    || now.Lifecycle.Where((bytes, index) => !bytes.SequenceEqual(source.Lifecycle[index])).Any()
                    || owners.Where((pair, index) => !ReferenceEquals(Animators()[pair.Key], instances[index])).Any())
                    throw new InvalidOperationException("Persistent animator global state/native lifecycle/identity readback differs.");
                for (var owner = 0; owner < instances.Length; owner++) for (var field = 0; field < fields.Length; field++)
                    if (!codec.SameRawSlot(source.Values[owner][field], fields[field].GetValue(instances[owner]), fields[field].FieldType, 0))
                        throw new InvalidOperationException("Persistent animator complete managed readback differs.");
            }
            verify = Verify;
            return () => {
                for (var owner = 0; owner < instances.Length; owner++) for (var field = 1; field < fields.Length - 1; field++)
                    fields[field].SetValue(instances[owner], source.Values[owner][field]);
                Verify();
            };
        }
        internal static IReadOnlyDictionary<object, string> CopiedMembers()
        {
            var roots = HealthRoots(); var copied = new Dictionary<object, string>(new References());
            var codec = CanonicalCarryFieldRegistry.Create(persistentSpriteDomain: true);
            var externals = ExternalDirectory();
            var metadata = new CanonicalCarryReflectionBindings(new[] { typeof(object).Assembly, typeof(GameManager).Assembly,
                typeof(Fsm).Assembly, typeof(GameObject).Assembly, typeof(AudioClip).Assembly, typeof(Rigidbody2D).Assembly,
                typeof(RaycastHit).Assembly }, Array.Empty<FieldInfo>());
            string? Bind(object value) {
                if (value is UnityEngine.Object native) return "PersistentAnimator/native/" + native.GetInstanceID();
                if (value is FsmEvent shared) return "PersistentAnimator/event/" + shared.Name;
                if (value is Fsm other && !roots.Values.Any(root => ReferenceEquals(root, other))) {
                    var owner = typeof(Fsm).GetField("owner", Flags)!.GetValue(other) as PlayMakerFSM;
                    if (owner == null || !ReferenceEquals(Raw.GetValue(owner), other)) throw new InvalidDataException("Health closure reaches an unbound foreign FSM.");
                    return "PersistentAnimator/fsm/" + owner.GetInstanceID();
                }
                foreach (var pair in externals) if (ReferenceEquals(pair.Value, value)) return pair.Key;
                var binding = metadata.Bind(value);
                if (binding != null && value is Array && !copied.ContainsKey(value)) copied.Add(value, "reflection-parameter-array");
                return binding;
            }
            var graph = codec.Capture(roots, Bind, null, (value, id, path) => { if (!copied.ContainsKey(value)) copied.Add(value, "node[" + id + "]/" + path); });
            if (graph.Unsupported.Length != 0 || copied.Count > 8192) throw new InvalidDataException("Health copied closure is unsupported: " + string.Join(";", graph.Unsupported.Take(8)));
            foreach (var pair in externals.Where(pair => pair.Value is Delegate)) if (!copied.ContainsKey(pair.Value)) copied.Add(pair.Value, pair.Key);
            return copied;
        }
        private sealed class References : IEqualityComparer<object>
        {
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
