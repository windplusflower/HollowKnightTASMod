using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HollowKnightTAS.Core.SceneCheckpoints;
using HutongGames.PlayMaker;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Unity resolves native references after some OnAfterDeserialize callbacks.
    // Observe the original configuration on the main thread, before the first
    // Fsm.Init/Awake/Start; disabled children are bound before Hero Start.
    // InitData may already have converted ActionData, but no actions have run.
    internal sealed class CanonicalHeroAssetTemplates : IDisposable
    {
        private delegate void OriginalInit(Fsm fsm, MonoBehaviour owner);
        private static readonly BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly FieldInfo Raw = Field(typeof(PlayMakerFSM), "fsm");
        private static readonly FieldInfo States = Field(typeof(Fsm), "states"), Variables = Field(typeof(Fsm), "variables");
        private static readonly FieldInfo ActionDataField = Field(typeof(FsmState), "actionData");
        private static readonly FieldInfo[] ParameterLists = CanonicalCarryGraphCodec.Fields(typeof(ActionData))
            .Where(field => field.IsDefined(typeof(SerializeField), false) && field.FieldType.IsGenericType
                && field.FieldType.GetGenericTypeDefinition() == typeof(List<>)
                && (typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType.GetGenericArguments()[0])
                    || ConfigurationValue(field.FieldType.GetGenericArguments()[0]))).ToArray();
        private readonly Hook initHook;
        private readonly IReadOnlyDictionary<string, string> expected = CanonicalHeroFsmBindingRegistry.Snapshot();
        private readonly Dictionary<string, PlayMakerFSM> owners = new Dictionary<string, PlayMakerFSM>(StringComparer.Ordinal);
        private CanonicalAssetOriginRegistry origins = new CanonicalAssetOriginRegistry();
        private HeroController? hero;
        private Exception? registrationFailure;
        private bool sealedRegistry;

        internal CanonicalHeroAssetTemplates()
        {
            var init = typeof(Fsm).GetMethod("Init", Instance, null, new[] { typeof(MonoBehaviour) }, null)
                ?? throw new MissingMethodException("Fsm.Init");
            initHook = new Hook(init, (Action<OriginalInit, Fsm, MonoBehaviour>)Init);
        }
        private static FieldInfo Field(Type type, string name) => type.GetField(name, Instance)
            ?? throw new InvalidDataException("Original asset configuration field is absent: " + type.FullName + "/" + name);

        private void Init(OriginalInit original, Fsm raw, MonoBehaviour owner)
        {
            if (owner is PlayMakerFSM component && component.transform.root.GetComponent<HeroController>() is HeroController value)
                try { Observe(value, component, raw); }
                catch (Exception error) { registrationFailure ??= error; }
            // Registration failure is reported at the controlled boundary;
            // preserve the original Awake/Init path even on a rejected layout.
            original(raw, owner);
        }

        internal void BeforeHeroStart(HeroController value)
        {
            try
            {
                foreach (var component in value.GetComponentsInChildren<PlayMakerFSM>(true))
                    Observe(value, component, (Fsm)Raw.GetValue(component));
                CanonicalHeroFsmBindingRegistry.Validate(owners.Select(pair => new KeyValuePair<string, string>(pair.Key,
                    ((Fsm)Raw.GetValue(pair.Value)).Name)));
                origins.Seal(); sealedRegistry = true;
                Modding.Logger.Log("[HKTAS] Registered original Hero asset origins before Start: FSMs=" + owners.Count
                    + " assets=" + origins.Snapshot().Length + " manifest=" + origins.ManifestHash());
            }
            catch (Exception error) { registrationFailure ??= error; }
        }

        private void Observe(HeroController value, PlayMakerFSM component, Fsm raw)
        {
            if (hero == null)
            {
                // A title return may destroy the previous Hero. A new actual
                // pickup must own a fresh catalogue, not the previous wrappers.
                if (owners.Count != 0) { owners.Clear(); origins = new CanonicalAssetOriginRegistry(); sealedRegistry = false; registrationFailure = null; }
                hero = value;
            }
            if (value != hero || value.gameObject.name != "Knight" || component.transform.root != value.transform)
                throw new InvalidDataException("Original Hero asset owner differs.");
            var path = Path(value.transform, component.transform) + ":PlayMakerFSM["
                + Array.IndexOf(component.gameObject.GetComponents<PlayMakerFSM>(), component) + "]";
            if (!expected.TryGetValue(path, out var name) || name != raw.Name)
                throw new InvalidDataException("Original Hero asset FSM binding differs: " + path);
            if (owners.TryGetValue(path, out var existing))
            {
                if (!ReferenceEquals(existing, component)) throw new InvalidDataException("Original Hero asset component was replaced.");
                return;
            }
            if (sealedRegistry || raw.Started)
                throw new InvalidDataException("Original Hero asset registration is too late: " + path);
            var configuration = CaptureSlots(raw);
            owners.Add(path, component);
            foreach (var slot in configuration) Add("HeroFSM/" + path + "/" + slot.Origin, slot.Value);
        }

        private static Slot[] CaptureSlots(Fsm raw)
        {
            var result = new List<Slot>();
            var fieldsRead = 0;
            var active = new HashSet<object>(new References());
            void Read(string path, object? value, int depth)
            {
                if (++fieldsRead > 100000 || depth > 32) throw new InvalidDataException("Original FSM asset configuration graph exceeds its bound.");
                if (value is UnityEngine.Object native)
                {
                    if (result.Count >= 10000) throw new InvalidDataException("Original FSM asset slot budget exceeded.");
                    result.Add(new Slot(path, native)); return;
                }
                if (value == null || value is string) return;
                var type = value.GetType();
                if (!type.IsArray && !(type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                    && !ConfigurationValue(type)) return;
                if (!active.Add(value)) return;
                try
                {
                    if (value is IList list)
                    {
                        if (list.Count > 10000) throw new InvalidDataException("Original FSM asset configuration list exceeds its bound.");
                        for (var slot = 0; slot < list.Count; slot++) Read(path + "[" + slot + "]", list[slot], depth + 1);
                    }
                    else foreach (var field in CanonicalCarryGraphCodec.Fields(type))
                        Read(path + "/" + field.Name, field.GetValue(value), depth + 1);
                }
                finally { active.Remove(value); }
            }
            var states = (FsmState[])States.GetValue(raw);
            if (states == null || states.Length > 4096) throw new InvalidDataException("Original FSM asset state table differs.");
            for (var index = 0; index < states.Length; index++)
            {
                var data = ActionDataField.GetValue(states[index]);
                if (data == null) continue;
                foreach (var field in ParameterLists)
                    Read("states[" + index + "]/actionData/" + field.Name, field.GetValue(data), 0);
            }
            var variables = Variables.GetValue(raw);
            if (variables == null) return result.ToArray();
            foreach (var group in CanonicalCarryGraphCodec.Fields(typeof(FsmVariables)).Where(field =>
                field.FieldType.IsArray && typeof(NamedVariable).IsAssignableFrom(field.FieldType.GetElementType())))
            {
                var array = group.GetValue(variables) as Array;
                if (array == null) continue;
                if (array.Length > 10000) throw new InvalidDataException("Original FSM asset variable table exceeds its bound.");
                for (var index = 0; index < array.Length; index++)
                {
                    Read("variables/" + group.Name + "[" + index + "]", array.GetValue(index), 0);
                }
            }
            return result.ToArray();
        }
        private static bool ConfigurationValue(Type type) => typeof(NamedVariable).IsAssignableFrom(type)
            || type == typeof(FsmVar) || type == typeof(FsmOwnerDefault) || type == typeof(FsmProperty)
            || type == typeof(FunctionCall) || type == typeof(FsmEventTarget) || type == typeof(FsmTemplateControl);

        private void Add(string path, object? value)
        {
            if (!(value is UnityEngine.Object native) || native == null) return;
            var go = native as GameObject ?? (native as Component)?.gameObject;
            // The not-yet-loaded Hero scene still owns actual scene objects.
            // Prefab assets can belong to separate unloaded Unity containers.
            if (go != null && go.scene.IsValid() && (go.scene.isLoaded || go.scene == hero!.gameObject.scene)) return;
            origins.Add(path, native, CanonicalCarryGraphCodec.Identity(native.GetType()), native.name);
        }
        private static string Path(Transform root, Transform current)
        {
            if (current == root) return "Knight[0]";
            var parent = current.parent ?? throw new InvalidDataException("Original Hero asset hierarchy is detached.");
            var ordinal = 0;
            for (var index = 0; index < current.GetSiblingIndex(); index++) if (parent.GetChild(index).name == current.name) ordinal++;
            return Path(root, parent) + "/" + current.name + "[" + ordinal + "]";
        }
        internal CanonicalAssetOriginRegistry.Entry[] Validate(BoundaryObjectIdentity identities)
        {
            if (registrationFailure != null) throw new InvalidDataException("Original Hero asset origin registration failed: "
                + registrationFailure.GetType().Name + ": " + registrationFailure.Message, registrationFailure);
            if (!sealedRegistry || hero == null || hero != HeroController.SilentInstance)
                throw new InvalidDataException("Original Hero asset origins are incomplete.");
            var current = CanonicalHeroFsmBindings.Snapshot(identities);
            if (current.Count != owners.Count || current.Any(pair => !owners.TryGetValue(pair.Key, out var owner)
                || !ReferenceEquals(owner, pair.Value))) throw new InvalidDataException("Original Hero asset component identities differ.");
            var snapshot = origins.Snapshot();
            foreach (var entry in snapshot)
            {
                var native = (UnityEngine.Object)origins.Resolve(entry.Binding);
                if (native == null || origins.Bind(native, CanonicalCarryGraphCodec.Identity(native.GetType()), native.name) != entry.Binding)
                    throw new InvalidDataException("Original Hero asset origin was destroyed or changed.");
            }
            return snapshot;
        }
        internal string? Bind(UnityEngine.Object value) => origins.Bind(value, CanonicalCarryGraphCodec.Identity(value.GetType()), value.name);
        internal UnityEngine.Object Resolve(string key) => (UnityEngine.Object)origins.Resolve(key);
        private sealed class Slot
        {
            internal readonly string Origin;
            internal readonly UnityEngine.Object Value;
            internal Slot(string origin, UnityEngine.Object value) { Origin = origin; Value = value; }
        }
        private sealed class References : IEqualityComparer<object>
        {
            public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
        public void Dispose() { initHook.Dispose(); owners.Clear(); hero = null; origins = new CanonicalAssetOriginRegistry(); }
    }
}
