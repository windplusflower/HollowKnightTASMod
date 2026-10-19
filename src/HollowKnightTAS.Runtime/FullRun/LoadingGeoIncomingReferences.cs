using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Observe references already traversed by the full reader. This neither
    // performs another world walk nor claims coverage of unobserved native slots.
    internal sealed class LoadingGeoIncomingReferences
    {
        private readonly Dictionary<object, string> owned = new Dictionary<object, string>(new References());
        private readonly SortedSet<string> references = new SortedSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Slot> slots = new Dictionary<string, Slot>(StringComparer.Ordinal);
        private readonly HashSet<string> externalReferences = new HashSet<string>(StringComparer.Ordinal);
        private string[] ownedRoots = Array.Empty<string>();
        private GeoCounter? counter;
        private GameObject? marker;
        internal bool Owns(object? value) => value != null && owned.ContainsKey(value);
        internal void BeginSample(Func<UnityEngine.Object, string> identity, GeoCounter? explicitCounter = null)
        {
            owned.Clear(); references.Clear(); slots.Clear(); externalReferences.Clear(); ownedRoots = Array.Empty<string>();
            marker = null;
            var cameras = typeof(GameCameras).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) as GameCameras;
            counter = explicitCounter ?? (cameras == null ? null : cameras.geoCounter);
            if (counter == null) return;
            PopulateOwnedDirectory(counter);
            ownedRoots = owned.Keys.OfType<MonoBehaviour>().Select(identity).ToArray();
        }
        // A finite second owner domain shares the existing slot/alias reader.
        // This entry accepts only the exact original Marker directory above;
        // it does not broaden the existing Geo shape or its admission.
        internal void BeginMarkerSample(Func<UnityEngine.Object, string> identity, GameObject? explicitMarker = null)
        {
            owned.Clear(); references.Clear(); slots.Clear(); externalReferences.Clear(); ownedRoots = Array.Empty<string>();
            counter = null; marker = explicitMarker ?? CanonicalMarkerDirectory.Current();
            foreach (var pair in CanonicalMarkerDirectory.Build(marker)) {
                owned.Add(pair.Value, pair.Key);
                if (pair.Value is PlayMakerFSM fsm) {
                    var raw = typeof(PlayMakerFSM).GetField("fsm", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fsm);
                    AddManaged(raw, pair.Key + "/fsm");
                }
            }
            foreach (var pair in CanonicalMarkerReadonlyCarry.Directory(CanonicalMarkerDirectory.Build(marker)))
                if (!owned.ContainsKey(pair.Value)) owned.Add(pair.Value, pair.Key);
            foreach (var pair in CanonicalMarkerEventCarry.Directory(CanonicalMarkerDirectory.Build(marker)))
                if (!owned.ContainsKey(pair.Value)) owned.Add(pair.Value, pair.Key);
            ownedRoots = owned.Keys.OfType<MonoBehaviour>().Select(identity).ToArray();
        }
        internal static IReadOnlyDictionary<string, object> BuildMarkerDirectory(GameObject marker)
        {
            var directory = new LoadingGeoIncomingReferences();
            directory.BeginMarkerSample(_ => "unused-marker-local-root", marker);
            return directory.OwnedDirectory();
        }
        // A cold Canon retires these exact TMP-owned meshes through the
        // original OnDestroy. They have no replacement: any external consumer
        // therefore fails Prepare rather than silently retaining a dead mesh.
        internal void ObserveRetiringMarkerMeshes()
        {
            if (marker == null || counter != null) throw new InvalidOperationException("Retiring Marker mesh observation has no exact directory.");
            var field = typeof(TMPro.TextMeshPro).GetField("m_mesh", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var pair in CanonicalMarkerDirectory.Build(marker).Where(pair => pair.Value is TMPro.TextMeshPro)) {
                var value = field.GetValue(pair.Value);
                if (ReferenceEquals(value, null)) continue;
                if (!(value is Mesh mesh) || mesh == null || owned.ContainsKey(mesh))
                    throw new InvalidOperationException("Retiring Marker TMP mesh is dead, shared or has an unknown type.");
                owned.Add(mesh, pair.Key + "/retired-owned-mesh");
            }
        }
        // Directory consumers need exact local aliases, not world-root paths.
        // Keep the same native/managed admission below without constructing a
        // whole-scene identity inventory whose paths would never be consumed.
        internal static IReadOnlyDictionary<string, object> BuildOwnedDirectory(GeoCounter counter)
        {
            var directory = new LoadingGeoIncomingReferences();
            directory.PopulateOwnedDirectory(counter);
            return directory.OwnedDirectory();
        }
        private void PopulateOwnedDirectory(GeoCounter counter)
        {
            var nodes = counter.GetComponentsInChildren<Transform>(true);
            if (nodes.Length != 5) throw new InvalidOperationException("Geo incoming reference owner directory differs.");
            foreach (var node in nodes) {
                var path = ReferenceEquals(node, counter.transform) ? "$" : "$/" + node.name;
                owned.Add(node.gameObject, path);
                var ordinals = new Dictionary<Type, int>();
                foreach (var component in node.GetComponents<Component>()) {
                    if (component == null) throw new InvalidOperationException("Geo incoming reference component is missing.");
                    var type = component.GetType(); ordinals.TryGetValue(type, out var ordinal); ordinals[type] = ordinal + 1;
                    owned.Add(component, path + ":" + type.FullName + "[" + ordinal + "]");
                    if (component is PlayMakerFSM fsm) {
                        var raw = typeof(PlayMakerFSM).GetField("fsm", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fsm);
                        AddManaged(raw, path + ":" + type.FullName + "[" + ordinal + "]/fsm");
                    }
                }
            }
        }
        internal IReadOnlyDictionary<string, object> OwnedDirectory() => owned.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
        internal LoadingGeoIncomingReferences Freeze()
        {
            var copy = new LoadingGeoIncomingReferences { counter = counter, marker = marker, ownedRoots = (string[])ownedRoots.Clone() };
            foreach (var pair in owned) copy.owned.Add(pair.Key, pair.Value);
            foreach (var item in references) copy.references.Add(item);
            foreach (var pair in slots) copy.slots.Add(pair.Key, pair.Value);
            foreach (var item in externalReferences) copy.externalReferences.Add(item);
            return copy;
        }
        internal void RequireNoExternalConsumers()
        {
            if (externalReferences.Count != 0)
                throw new InvalidOperationException("Frozen original Geo owner still has an external consumer: " + string.Join(";", externalReferences.OrderBy(value => value, StringComparer.Ordinal)));
        }
        private void AddManaged(object value, string path)
        {
            if (!owned.ContainsKey(value)) owned.Add(value, path);
            var states = typeof(Fsm).GetField("states", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(value) as FsmState[];
            if (states == null || states.Length > 256) throw new InvalidOperationException("Geo incoming original state directory differs.");
            for (var i = 0; i < states.Length; i++) {
                if (!owned.ContainsKey(states[i])) owned.Add(states[i], path + "/state[" + i + "]");
                var actions = typeof(FsmState).GetField("actions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(states[i]) as FsmStateAction[];
                if (actions == null) continue;
                if (actions.Length > 128) throw new InvalidOperationException("Geo incoming original action directory differs.");
                for (var a = 0; a < actions.Length; a++) if (actions[a] != null && !owned.ContainsKey(actions[a]))
                    owned.Add(actions[a], path + "/state[" + i + "]/action[" + a + "]");
            }
        }
        private bool InternalRoot(string root) => ownedRoots.Any(value => root == value || root.StartsWith(value + "/", StringComparison.Ordinal));
        // This is the original OnEnable/OnDisable/OnDestroy registration list,
        // not a consumer alias. Replacing its array entry before OnDestroy
        // would prevent the original Remove(this) from retiring the old FSM.
        // Source membership is carried and checked after the real lifecycle.
        private static bool LifecycleRoot(string root) => root == "static:PlayMaker:PlayMakerFSM.fsmList";
        internal void Observe(object value, string path, string root)
        {
            if (!owned.TryGetValue(value, out var identity)) return;
            if (references.Count >= 4096) throw new InvalidOperationException("Geo incoming reference diagnostic bound exceeded.");
            references.Add(identity + " <- " + path);
            if (!InternalRoot(root) && !LifecycleRoot(root)) externalReferences.Add(path);
        }
        internal void ObserveSlot(object owner, FieldInfo? field, int index, object? value, string path, string root)
        {
            if (value == null || !owned.TryGetValue(value, out var identity) || InternalRoot(root) || LifecycleRoot(root)) return;
            if (slots.Count >= 4096) throw new InvalidOperationException("Geo incoming source slot bound exceeded.");
            if (owner.GetType().IsValueType || field?.IsInitOnly == true)
                throw new InvalidOperationException("Geo incoming alias needs an explicit writable containing owner: " + path);
            slots[path] = new Slot(owner, field, index, value, identity);
        }
        internal CanonicalPreparedWritePlan Prepare(IReadOnlyDictionary<string, object> replacements)
        {
            if ((counter == null && marker == null) || externalReferences.Any(path => !slots.ContainsKey(path)))
                throw new InvalidOperationException("Geo incoming directory contains an unbound delegate/native/collection slot.");
            var selected = slots.Values.ToArray();
            foreach (var slot in selected)
                if (!replacements.TryGetValue(slot.Identity, out var replacement) || replacement.GetType() != slot.Original.GetType())
                    throw new InvalidOperationException("Geo incoming replacement directory is incomplete: " + slot.Identity);
            void Guard() {
                foreach (var slot in selected) if (!ReferenceEquals(slot.Read(), slot.Original))
                    throw new InvalidOperationException("Geo incoming source slot changed after the original full traversal.");
            }
            Guard();
            return new CanonicalPreparedWritePlan(Guard, () => {
                foreach (var slot in selected) slot.Write(replacements[slot.Identity]);
            }, () => {
                foreach (var slot in selected) if (!ReferenceEquals(slot.Read(), replacements[slot.Identity]))
                    throw new InvalidOperationException("Geo incoming complete source alias readback differs.");
            });
        }
        private sealed class Slot
        {
            private readonly object owner; private readonly FieldInfo? field; private readonly int index;
            internal readonly object Original; internal readonly string Identity;
            internal Slot(object owner, FieldInfo? field, int index, object original, string identity)
            { this.owner = owner; this.field = field; this.index = index; Original = original; Identity = identity; }
            internal object? Read() => field == null ? ((Array)owner).GetValue(index) : field.GetValue(owner);
            internal void Write(object value) { if (field == null) ((Array)owner).SetValue(value, index); else field.SetValue(owner, value); }
        }
        private sealed class References : IEqualityComparer<object>
        {
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
        internal object Snapshot() => new { diagnosticOnly = true, managedPaths = references.ToArray(),
            writableExternalPaths = slots.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            unboundExternalPaths = externalReferences.Where(path => !slots.ContainsKey(path)).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            includesInternalAliases = true, unobservedNativeSlotsQualified = false };
    }
}
