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
        internal CanonicalMapKeyTextExternalGuard(Transform keys)
        {
            this.keys = keys;
            var nodes = new List<Transform>();
            foreach (var key in CanonicalMapKeyTextDirectory.Keys) {
                var node = keys.Find(key) ?? throw new InvalidOperationException("Map Key real parent is absent.");
                while (node != null) { if (!nodes.Contains(node)) nodes.Add(node); node = node.parent; }
            }
            ancestors = nodes.ToArray();
            if (keys.parent == null || keys.parent.name != "Map Key" || keys.parent.parent == null || keys.parent.parent.name != "Inventory"
                || keys.parent.parent.parent == null || !ReferenceEquals(keys.parent.parent.parent, GameCameras.instance.hudCamera.transform))
                throw new InvalidOperationException("Map Key actual Inventory/HudCamera ancestry differs.");
            owners = ancestors.SelectMany(node => node.GetComponents<MonoBehaviour>()).ToArray();
            if (owners.Any(owner => ReferenceEquals(owner, null) || owner == null))
                throw new InvalidOperationException("Map Key ancestor contains a missing native script.");
            fields = new LoadingFieldDigest(Reference,
                type => type == typeof(MonoBehaviour) || type == typeof(UnityEngine.Object));
            for (var index = 0; index < owners.Length; index++)
                if (owners[index] is PlayMakerFSM && fields.Read(owners[index], "fsm") is Fsm fsm)
                    RegisterFsm(fsm, "ancestor[" + index + "]");
            managed = ReadAll(); native = ancestors.Select(Pose).ToArray();
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
                void Read(object owner, string root) {
                    var result = fields.Capture(owner, root, expandRoot: true);
                    if (result.Unsupported.Count != 0) throw new InvalidOperationException("Map Key ancestor has unread fields: " + string.Join(";", result.Unsupported));
                    hashes.Add(result.Sha256);
                }
                for (var index = 0; index < owners.Length; index++) Read(owners[index], "ancestor[" + index + "]");
                for (var index = 0; index < memberRoots.Count; index++) Read(memberRoots[index].Key, memberRoots[index].Value);
            } finally { fields.EndSharedGraph(); }
            return hashes.ToArray();
        }
        private static int Bits(float value) => BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
        private static string Pose(Transform node)
        {
            var position = node.localPosition; var rotation = node.localRotation; var scale = node.localScale;
            var shape = new List<int> { Bits(position.x), Bits(position.y), Bits(position.z), Bits(rotation.x), Bits(rotation.y), Bits(rotation.z), Bits(rotation.w),
                Bits(scale.x), Bits(scale.y), Bits(scale.z) };
            if (node is RectTransform rect) {
                foreach (var value in new[] { rect.anchoredPosition, rect.sizeDelta, rect.pivot, rect.anchorMin, rect.anchorMax, rect.offsetMin, rect.offsetMax }) {
                    shape.Add(Bits(value.x)); shape.Add(Bits(value.y));
                }
            }
            return Newtonsoft.Json.JsonConvert.SerializeObject(new { id = node.GetInstanceID(), parent = node.parent == null ? 0 : node.parent.GetInstanceID(),
                active = node.gameObject.activeSelf, hierarchy = node.gameObject.activeInHierarchy, poseBits = shape, sibling = node.GetSiblingIndex() });
        }
        internal void Verify()
        {
            if (!ReferenceEquals(keys, CanonicalMapKeyTextDirectory.Current()) || !native.SequenceEqual(ancestors.Select(Pose))
                || !managed.SequenceEqual(ReadAll()) || !owners.SequenceEqual(ancestors.SelectMany(node => node.GetComponents<MonoBehaviour>())))
                throw new InvalidOperationException("Map Key canonical callback changed its real parent activation, pose, Control FSM or other ancestor fields.");
        }
        public void Dispose() => fields.Dispose();
        private sealed class References : IEqualityComparer<object>
        {
            public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
