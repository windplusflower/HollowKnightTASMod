using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Observe real Text -> Key -> Keys -> Map Key -> Inventory -> HudCamera
    // ancestors. No activation/layout/property writes or inferred PD shape.
    internal sealed class CanonicalMapKeyTextExternalGuard : IDisposable
    {
        private readonly Transform keys;
        private readonly Transform[] ancestors;
        private readonly MonoBehaviour[] owners;
        private readonly LoadingFieldDigest fields;
        private readonly string[] managed;
        private readonly Dictionary<object, string> members = new Dictionary<object, string>(new References());
        private readonly List<KeyValuePair<object, string>> memberRoots = new List<KeyValuePair<object, string>>();
        private readonly Dictionary<object, string> natives = new Dictionary<object, string>(new References());
        private readonly string[] native;
        private readonly Transform[] subtreeNodes;
        private readonly Component[] subtreeComponents;
        private readonly string[] subtreeNative;
        private readonly IReadOnlyDictionary<string, object> originalOwned;
        private readonly Dictionary<object, string> logicalOwned = new Dictionary<object, string>(new References());
        private bool incomingRebindPrepared, incomingRebindVerified;
        private readonly Transform[] sourceParentPoseOwners;
        private readonly MonoBehaviour[] sourceManagedOwners;
        private readonly KeyValuePair<string, object?>[]? explicitManagedRoots;
        internal int ManagedRootCount => managed.Length;
        // Reuse the exact original FSM/detached/native/fake-null/shared-alias
        // reader for a finite set of non-Map owners. No second approximation
        // of that partition or larger per-root budget is introduced.
        internal CanonicalMapKeyTextExternalGuard(IReadOnlyList<KeyValuePair<string, object?>> roots,
            IReadOnlyDictionary<string, object> originalOwned)
        {
            if (roots.Count > 16000) throw new InvalidOperationException("Explicit preservation roots exceed the existing owner bound.");
            keys = null!; ancestors = Array.Empty<Transform>(); owners = Array.Empty<MonoBehaviour>();
            subtreeNodes = Array.Empty<Transform>(); subtreeComponents = Array.Empty<Component>();
            sourceParentPoseOwners = Array.Empty<Transform>(); sourceManagedOwners = Array.Empty<MonoBehaviour>();
            native = Array.Empty<string>(); subtreeNative = Array.Empty<string>();
            this.originalOwned = originalOwned; explicitManagedRoots = roots.ToArray();
            foreach (var pair in originalOwned.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                if (!logicalOwned.ContainsKey(pair.Value)) logicalOwned.Add(pair.Value, "owned:" + pair.Key);
            fields = new LoadingFieldDigest(Reference,
                type => type == typeof(MonoBehaviour) || type == typeof(UnityEngine.Object), sharedFieldReaders: true);
            for (var index = 0; index < explicitManagedRoots.Length; index++)
                if (explicitManagedRoots[index].Value is PlayMakerFSM owner && fields.Read(owner, "fsm") is Fsm fsm)
                    RegisterFsm(fsm, "explicit-root[" + index + "]");
            managed = ReadAll();
        }
        internal void VerifyManagedRoots()
        {
            if (explicitManagedRoots == null || !managed.SequenceEqual(ReadAll()))
                throw new InvalidOperationException("Complete explicit preservation graph or cross-root aliases changed.");
        }
        internal CanonicalMapKeyTextExternalGuard(Transform keys, IReadOnlyDictionary<string, object>? sourceParentOwned = null,
            MonoBehaviour[]? sourceManagedOwners = null, Transform[]? sourcePoseOwners = null)
        {
            this.keys = keys;
            this.sourceManagedOwners = sourceManagedOwners ?? Array.Empty<MonoBehaviour>();
            originalOwned = sourceParentOwned ?? LoadingGeoIncomingReferences.BuildMapKeyDirectory(keys);
            sourceParentPoseOwners = sourcePoseOwners ?? Array.Empty<Transform>();
            foreach (var pair in originalOwned.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                if (!logicalOwned.ContainsKey(pair.Value)) logicalOwned.Add(pair.Value, "owned:" + pair.Key);
            var nodes = new List<Transform>();
            foreach (var key in CanonicalMapKeyTextDirectory.Keys) {
                var node = keys.Find(key) ?? throw new InvalidOperationException("Map Key real parent is absent.");
                while (node != null) { if (!nodes.Contains(node)) nodes.Add(node); node = node.parent; }
            }
            subtreeNodes = sourceManagedOwners == null ? Array.Empty<Transform>() : keys.parent.GetComponentsInChildren<Transform>(true)
                .Where(node => !CanonicalMapKeyTextDirectory.Keys.Any(key => {
                    var leaf = keys.Find(key + "/Text"); return ReferenceEquals(node, leaf) || node.IsChildOf(leaf);
                })).ToArray();
            foreach (var node in subtreeNodes) if (!nodes.Contains(node)) nodes.Add(node);
            ancestors = nodes.ToArray();
            subtreeComponents = subtreeNodes.SelectMany(node => node.GetComponents<Component>())
                .Where(component => !(component is Transform) && !(component is MonoBehaviour))
                .Concat(ancestors.SelectMany(node => node.GetComponents<Camera>())).Distinct().ToArray();
            if (keys.parent == null || keys.parent.name != "Map Key" || keys.parent.parent == null || keys.parent.parent.name != "Inventory"
                || keys.parent.parent.parent == null || !ReferenceEquals(keys.parent.parent.parent, GameCameras.instance.hudCamera.transform))
                throw new InvalidOperationException("Map Key actual Inventory/HudCamera ancestry differs.");
            owners = ancestors.SelectMany(node => node.GetComponents<MonoBehaviour>())
                .Where(owner => sourceManagedOwners == null || !sourceManagedOwners.Any(permitted => ReferenceEquals(owner, permitted))).ToArray();
            if (owners.Any(owner => ReferenceEquals(owner, null) || owner == null))
                throw new InvalidOperationException("Map Key ancestor contains a missing native script.");
            fields = new LoadingFieldDigest(Reference,
                type => type == typeof(MonoBehaviour) || type == typeof(UnityEngine.Object), sharedFieldReaders: true);
            for (var index = 0; index < owners.Length; index++)
                if (owners[index] is PlayMakerFSM && fields.Read(owners[index], "fsm") is Fsm fsm)
                    RegisterFsm(fsm, "ancestor[" + index + "]");
            managed = ReadAll(); native = ancestors.Select(Pose).ToArray(); subtreeNative = ReadSubtreeNative();
        }
        internal static IReadOnlyDictionary<object, string> PrepareLogicalIncomingBindings(
            IReadOnlyDictionary<string, object> original, IReadOnlyDictionary<string, object> replacement)
        {
            if (!original.Keys.OrderBy(key => key, StringComparer.Ordinal).SequenceEqual(replacement.Keys.OrderBy(key => key, StringComparer.Ordinal)))
                throw new InvalidOperationException("Map Key exact logical incoming directory keys differ.");
            var result = new Dictionary<object, string>(new References());
            var oldToNew = new Dictionary<object, object>(new References());
            var newToOld = new Dictionary<object, object>(new References());
            foreach (var pair in original.OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
                var next = replacement[pair.Key];
                if (ReferenceEquals(pair.Value, null) || ReferenceEquals(next, null) || pair.Value.GetType() != next.GetType()
                    || oldToNew.TryGetValue(pair.Value, out var previousNew) && !ReferenceEquals(previousNew, next)
                    || newToOld.TryGetValue(next, out var previousOld) && !ReferenceEquals(previousOld, pair.Value))
                    throw new InvalidOperationException("Map Key logical incoming alias shape, original owner or replacement type differs.");
                oldToNew[pair.Value] = next; newToOld[next] = pair.Value;
                if (!result.ContainsKey(pair.Value)) result.Add(pair.Value, "owned:" + pair.Key);
            }
            foreach (var pair in oldToNew) {
                var identity = result[pair.Key];
                if (result.TryGetValue(pair.Value, out var existing) && existing != identity)
                    throw new InvalidOperationException("Map Key incoming new owner overlaps another original identity.");
                result[pair.Value] = identity;
            }
            return result;
        }
        internal void PrepareExactIncomingRebind(IReadOnlyDictionary<string, object> replacement)
        {
            if (incomingRebindPrepared) throw new InvalidOperationException("Map Key logical incoming rebinding is single-use.");
            var map = PrepareLogicalIncomingBindings(originalOwned, replacement);
            incomingRebindPrepared = true;
            foreach (var pair in map) logicalOwned[pair.Key] = pair.Value;
        }
        internal void PrepareIncomingRebind(IReadOnlyList<GameObject> created)
        {
            // Build proves actual Key parent/component identity before mapping.
            PrepareExactIncomingRebind(LoadingGeoIncomingReferences.BuildMapKeyDirectory(keys, created));
        }
        private void AddMember(object value, string name)
        {
            if (members.ContainsKey(value)) return;
            if (members.Count >= 100000) throw new InvalidOperationException("Map Key ancestor raw member directory exceeded the original reader bound.");
            members.Add(value, name); memberRoots.Add(new KeyValuePair<object, string>(value, name));
        }
        // Same raw Fsm/state/action partition as the existing original incoming
        // reader. All roots share one alias graph; no field or edge is omitted.
        private void RegisterFsm(Fsm fsm, string id)
        {
            if (members.ContainsKey(fsm)) return;
            AddMember(fsm, id + "/fsm");
            var states = fields.Read(fsm, "states") as Array;
            if (states == null) return;
            for (var stateIndex = 0; stateIndex < states.Length; stateIndex++) {
                var state = states.GetValue(stateIndex); if (state == null) continue;
                var stateId = id + "/state[" + stateIndex + "]"; AddMember(state, stateId);
                var actions = fields.Read(state, "actions") as Array; if (actions == null) continue;
                for (var actionIndex = 0; actionIndex < actions.Length; actionIndex++) {
                    var action = actions.GetValue(actionIndex);
                    if (action != null) AddMember(action, stateId + "/action[" + actionIndex + "]");
                }
            }
        }
        private string? Reference(object value)
        {
            if (logicalOwned.TryGetValue(value, out var owned))
                return value is UnityEngine.Object ownNative ? owned + (ownNative == null ? ":destroyed" : ":live") : owned;
            if (members.TryGetValue(value, out var member)) return member;
            if (value is FsmState || value is FsmStateAction) {
                if (fields.Read(value, "fsm") is Fsm owner) RegisterFsm(owner, "detached-fsm[" + members.Count + "]");
                if (!members.ContainsKey(value)) AddMember(value, "detached-fsm-member[" + members.Count + "]");
                return members[value];
            }
            if (value is Fsm detached) { RegisterFsm(detached, "detached-fsm[" + members.Count + "]"); return members[value]; }
            if (LoadingRuntimeReferencePolicy.IsOpaqueController(value.GetType(), typeof(LoadingGameplayOracle).Assembly))
                return "TAS-controller:" + value.GetType().FullName;
            if (value is UnityEngine.Object obj) {
                if (!natives.TryGetValue(value, out var identity)) {
                    // Never evaluate a destroyed wrapper's native name/owner.
                    identity = obj.GetType().FullName + ":" + obj.GetInstanceID() + ":wrapper[" + natives.Count + "]";
                    natives.Add(value, identity);
                }
                return (obj == null ? "destroyed-native:" : "live-native:") + identity;
            }
            return null;
        }
        private string[] ReadAll()
        {
            var hashes = new List<string>();
            fields.BeginSharedGraph();
            try {
                void Read(object? owner, string root) {
                    var result = fields.Capture(owner, root, expandRoot: true);
                    if (result.Unsupported.Count != 0) throw new InvalidOperationException("Map Key ancestor has unread fields: " + string.Join(";", result.Unsupported));
                    hashes.Add(result.Sha256);
                }
                if (explicitManagedRoots != null)
                    foreach (var root in explicitManagedRoots) Read(root.Value, root.Key);
                else for (var index = 0; index < owners.Length; index++) Read(owners[index], "ancestor[" + index + "]");
                for (var index = 0; index < memberRoots.Count; index++) Read(memberRoots[index].Key, memberRoots[index].Value);
            } finally { fields.EndSharedGraph(); }
            return hashes.ToArray();
        }
        private string[] ReadSubtreeNative()
        {
            var result = new List<string>();
            for (var index = 0; index < subtreeComponents.Length; index++) {
                var component = subtreeComponents[index];
                if (component == null) throw new InvalidOperationException("Map Key unowned native subtree component disappeared.");
                var observation = LoadingNativeComponentObservation.Capture(component,
                    reason => throw new InvalidOperationException("Map Key unowned native callback field is unread: " + reason));
                if (observation == null) throw new InvalidOperationException("Map Key unowned native callback component has no reader: " + component.GetType().FullName);
                var digest = fields.Capture(observation, "map-key-unowned-native[" + index + "]", expandRoot: true);
                if (digest.Unsupported.Count != 0) throw new InvalidOperationException("Map Key unowned native callback graph has unread fields.");
                result.Add(digest.Sha256);
            }
            return result.ToArray();
        }
        private static int Bits(float value) => BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
        private string Pose(Transform node)
        {
            if (sourceParentPoseOwners.Any(owner => ReferenceEquals(owner, node)))
                return Newtonsoft.Json.JsonConvert.SerializeObject(new { id = node.GetInstanceID(), parent = node.parent == null ? 0 : node.parent.GetInstanceID(),
                    sibling = node.GetSiblingIndex(), node.gameObject.layer, node.gameObject.tag, node.gameObject.isStatic, hideFlags = (int)node.gameObject.hideFlags });
            var position = node.localPosition; var rotation = node.localRotation; var scale = node.localScale;
            var shape = new List<int> { Bits(position.x), Bits(position.y), Bits(position.z), Bits(rotation.x), Bits(rotation.y), Bits(rotation.z), Bits(rotation.w),
                Bits(scale.x), Bits(scale.y), Bits(scale.z) };
            if (node is RectTransform rect) {
                foreach (var value in new[] { rect.anchoredPosition, rect.sizeDelta, rect.pivot, rect.anchorMin, rect.anchorMax, rect.offsetMin, rect.offsetMax }) {
                    shape.Add(Bits(value.x)); shape.Add(Bits(value.y));
                }
            }
            var inheritedSourceActivation = sourceParentPoseOwners.Any(owner => ReferenceEquals(node.parent, owner));
            return Newtonsoft.Json.JsonConvert.SerializeObject(new { id = node.GetInstanceID(), parent = node.parent == null ? 0 : node.parent.GetInstanceID(),
                active = node.gameObject.activeSelf, hierarchy = inheritedSourceActivation ? (bool?)null : node.gameObject.activeInHierarchy,
                poseBits = shape, sibling = node.GetSiblingIndex(), node.gameObject.layer, node.gameObject.tag, node.gameObject.isStatic,
                hideFlags = (int)node.gameObject.hideFlags });
        }
        internal void Verify()
        {
            if (!ReferenceEquals(keys, CanonicalMapKeyTextDirectory.Current()) || !native.SequenceEqual(ancestors.Select(Pose))
                || !managed.SequenceEqual(ReadAll()) || !subtreeNative.SequenceEqual(ReadSubtreeNative())
                || !subtreeComponents.SequenceEqual(subtreeNodes.SelectMany(node => node.GetComponents<Component>())
                    .Where(component => !(component is Transform) && !(component is MonoBehaviour))
                    .Concat(ancestors.SelectMany(node => node.GetComponents<Camera>())).Distinct()) || !owners.SequenceEqual(ancestors.SelectMany(node => node.GetComponents<MonoBehaviour>())
                    .Where(owner => !sourceManagedOwners.Any(permitted => ReferenceEquals(owner, permitted)))))
                throw new InvalidOperationException("Map Key canonical callback changed its real parent activation, pose, Control FSM or other ancestor fields.");
            if (incomingRebindPrepared && !incomingRebindVerified) {
                incomingRebindVerified = true;
                Modding.Logger.Log("[HKTAS] Map Key full ancestor post-incoming preservation: completeManagedRoots=" + managed.Length
                    + ";originalAncestorCount=" + ancestors.Length + ";logicalGraphAndCrossRootAliasesEqual=true;onlyExplicitOneToOneOwnedDirectoryMapped=true;parentNativePoseActivationExact=true");
            }
        }
        public void Dispose() => fields.Dispose();
        private sealed class References : IEqualityComparer<object>
        {
            public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
