using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Keep the actual, never-awakened serialized owner. Cloning it is the
    // original producer: OnAfterDeserialize is allowed to initialize FSM data
    // against the current real globals. No old initialized clone is a template.
    internal sealed class CanonicalGeoColdTemplate : IDisposable
    {
        private GameObject? holder;
        private GeoCounter? template;
        internal static GeoCounter? FrozenCounter { get; private set; }
        private Transform? parent;
        private int sibling;
        internal void Capture(GeoCounter source)
        {
            var stage = "original-owner-admission";
            try {
            if (holder != null || source == null) throw new InvalidOperationException("Original Geo template capture is absent or repeated.");
            var directory = Directory(source.gameObject);
            RequireColdDirectory(directory);
            parent = source.transform.parent;
            if (parent == null || parent.gameObject.activeInHierarchy)
                throw new InvalidOperationException("Original Geo must be frozen before its original HUD parent activation.");
            sibling = source.transform.GetSiblingIndex();
            stage = "full-original-incoming-reader";
            var incoming = ObserveIncoming(source);
            stage = "original-PD-counter-random-preservation";
            var originalSelf = source.gameObject.activeSelf;
            var random = UnityEngine.Random.state;
            var particles = LoadingGameplayOracle.CaptureParticleRandomState();
            var player = PlayerData.instance; var firstGeo = player.firstGeo; var geo = player.geo;
            using var preservation = new LoadingFieldDigest(value => value is UnityEngine.Object native ? "original-native:" + native.GetInstanceID() : null,
                type => type == typeof(MonoBehaviour) || type == typeof(UnityEngine.Object));
            string Preserve(object owner, string name) {
                var digest = preservation.Capture(owner, name, expandRoot: true);
                if (digest.Unsupported.Count != 0) throw new InvalidOperationException("Original Geo producer preservation has unread fields: " + string.Join(";", digest.Unsupported));
                return digest.Sha256;
            }
            var playerBefore = Preserve(player, "actual-PlayerData");
            var counterBefore = Preserve(source, "frozen-GeoCounter");
            holder = new GameObject("__HKTAS_OriginalGeoColdTemplate");
            holder.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(holder);
            template = source;
            if (FrozenCounter != null) throw new InvalidOperationException("Another original Geo frozen owner is still retained.");
            FrozenCounter = source;
            stage = "freeze-original-owner";
            source.transform.SetParent(holder.transform, false);
            stage = "original-inactive-producer";
            var created = ProduceInactive();
            if (created.gameObject.activeSelf != originalSelf) throw new InvalidOperationException("Original Geo producer changed its serialized activation.");
            stage = "produced-managed-directory";
            var createdDirectory = ManagedDirectory(created);
            stage = "original-incoming-application";
            incoming.Prepare(createdDirectory).Apply();
            stage = "entry-PD-counter-random-readback";
            if (!ReferenceEquals(GameCameras.instance.geoCounter, created)
                || !ReferenceEquals(HeroController.SilentInstance.geoCounter, created)
                || !ReferenceEquals(PlayerData.instance, player) || player.firstGeo != firstGeo || player.geo != geo
                || playerBefore != Preserve(player, "actual-PlayerData") || counterBefore != Preserve(source, "frozen-GeoCounter")
                || !random.Equals(UnityEngine.Random.state) || !particles.SequenceEqual(LoadingGameplayOracle.CaptureParticleRandomState()))
                throw new InvalidOperationException("Original Geo producer changed its real entry aliases, PlayerData or random state.");
            RequireColdDirectory(directory);
            Modding.Logger.Log("[HKTAS] Original Geo frozen owner retained; original inactive producer installed with complete observed incoming alias readback; native configuration remains unqualified.");
            } catch (Exception error) {
                throw new InvalidOperationException("Original Geo producer stage " + stage + " rejected: " + error.GetType().Name + ": " + error.Message, error);
            }
        }
        internal GeoCounter ProduceInactive()
        {
            if (template == null || holder == null || holder.activeSelf || parent == null)
                throw new InvalidOperationException("Original Geo producer has no frozen original template.");
            RequireColdDirectory(Directory(template.gameObject));
            var clone = UnityEngine.Object.Instantiate(template.gameObject, holder.transform, false);
            clone.name = template.gameObject.name;
            var produced = Directory(clone);
            var original = Directory(template.gameObject);
            if (!original.Keys.SequenceEqual(produced.Keys)
                || original.Any(pair => pair.Value.GetType() != produced[pair.Key].GetType()))
                throw new InvalidOperationException("Original Geo producer changed its exact owner/component directory.");
            RequireColdDirectory(produced);
            // With an already active HUD, keep the new owner inactive until
            // every external entry has been rebound. Its subsequent original
            // OnEnable/Awake path sees the installed source PD and globals.
            if (parent.gameObject.activeInHierarchy) clone.SetActive(false);
            clone.transform.SetParent(parent, false); clone.transform.SetSiblingIndex(sibling);
            return clone.GetComponent<GeoCounter>();
        }
        internal void ActivateProduced(GeoCounter created)
        {
            if (template == null || created == null || created.transform.parent != parent)
                throw new InvalidOperationException("Original Geo activation has no registered produced owner.");
            created.gameObject.SetActive(template.gameObject.activeSelf);
        }
        private static LoadingGeoIncomingReferences ObserveIncoming(GeoCounter source)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            long observedNodes = 0, logicalCharacters = 0; var observedRoots = 0;
            var identities = new BoundaryObjectIdentity();
            var incoming = new LoadingGeoIncomingReferences(); incoming.BeginSample(identities.Get, source);
            var members = new Dictionary<object, string>(new ManagedReferences());
            var destroyed = new Dictionary<UnityEngine.Object, string>(new ObjectReferences());
            var memberRoots = new List<KeyValuePair<object, string>>();
            LoadingFieldDigest? reader = null;
            void AddMember(object value, string name) {
                if (members.ContainsKey(value)) return;
                if (members.Count >= 100000) throw new InvalidOperationException("Original Geo incoming raw member directory exceeded its existing bound.");
                members.Add(value, name); memberRoots.Add(new KeyValuePair<object, string>(value, name));
            }
            void RegisterFsm(HutongGames.PlayMaker.Fsm fsm, string id) {
                if (members.ContainsKey(fsm)) return;
                AddMember(fsm, id + "/fsm");
                var states = reader!.Read(fsm, "states") as Array;
                if (states == null) return;
                for (var stateIndex = 0; stateIndex < states.Length; stateIndex++) {
                    var state = states.GetValue(stateIndex); if (state == null) continue;
                    var stateId = id + "/state[" + stateIndex + "]"; AddMember(state, stateId);
                    var actions = reader.Read(state, "actions") as Array; if (actions == null) continue;
                    for (var actionIndex = 0; actionIndex < actions.Length; actionIndex++) {
                        var action = actions.GetValue(actionIndex);
                        if (action != null) AddMember(action, stateId + "/action[" + actionIndex + "]");
                    }
                }
            }
            string? Reference(object value) {
                if (reader!.Capturing && incoming.Owns(value)) incoming.Observe(value, reader.DiagnosticPath, reader.DiagnosticRoot);
                if (members.TryGetValue(value, out var member)) return member;
                if (value is HutongGames.PlayMaker.FsmState || value is HutongGames.PlayMaker.FsmStateAction) {
                    if (reader.Read(value, "fsm") is HutongGames.PlayMaker.Fsm owner)
                        RegisterFsm(owner, "detached-fsm[" + members.Count + "]");
                    if (!members.ContainsKey(value)) AddMember(value, "detached-fsm-member[" + members.Count + "]");
                    return members[value];
                }
                if (value is HutongGames.PlayMaker.Fsm detached) {
                    RegisterFsm(detached, "detached-fsm[" + members.Count + "]"); return members[value];
                }
                if (LoadingRuntimeReferencePolicy.IsOpaqueController(value.GetType(), typeof(LoadingGameplayOracle).Assembly))
                    return "TAS-controller:" + value.GetType().FullName;
                if (value is UnityEngine.Object native) {
                    if (native == null) {
                        // Preserve distinct real managed wrappers even when
                        // Unity's overloaded null reports the native owner dead.
                        // Never ask a destroyed Component for gameObject/name.
                        var wrapper = native!;
                        if (!destroyed.TryGetValue(wrapper, out var identity)) {
                            identity = "destroyed-native-wrapper[" + destroyed.Count + "]:" + wrapper.GetType().FullName + ":" + wrapper.GetInstanceID();
                            destroyed.Add(wrapper, identity);
                        }
                        return identity;
                    }
                    try { return identities.Get(native); }
                    catch (Exception error) { throw new InvalidOperationException("Original Geo incoming native reference " + native.GetType().FullName
                        + " at " + reader.DiagnosticPath + " rejected: " + error.GetType().Name + ": " + error.Message, error); }
                }
                return null;
            }
            using (reader = new LoadingFieldDigest(Reference, type => type == typeof(MonoBehaviour) || type == typeof(UnityEngine.Object),
                slotObservation: (owner, field, index, value, path) => incoming.ObserveSlot(owner, field, index, value, path, reader!.DiagnosticRoot),
                observeSlotValue: incoming.Owns)) {
                var owners = Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(owner => owner != null && owner.gameObject.scene.IsValid()
                    && owner.gameObject.scene.isLoaded && owner.GetType().Assembly != typeof(CanonicalGeoColdTemplate).Assembly).ToArray();
                if (owners.Length > 16000) throw new InvalidOperationException("Original Geo incoming owner inventory exceeded its existing reader bound.");
                // Match the existing oracle's raw FSM/state/action partition:
                // every actual member is read once as a full root, while all
                // edges into it retain its real identity instead of recursively
                // expanding the same transition cycle to the depth limit.
                foreach (var owner in owners.OfType<PlayMakerFSM>()) {
                    var id = identities.Get(owner);
                    if (!(reader.Read(owner, "fsm") is HutongGames.PlayMaker.Fsm fsm)) continue;
                    RegisterFsm(fsm, id);
                }
                reader.BeginSharedGraph();
                try {
                    void CaptureRoot(object? value, string name, bool expand) {
                        try {
                            var result = reader.CaptureReferences(value, name, expandRoot: expand);
                            observedNodes += result.Nodes; logicalCharacters += result.Characters; observedRoots++;
                        }
                        catch (Exception error) { throw new InvalidOperationException("Original Geo incoming root " + name + " rejected: " + error.GetType().Name + ": " + error.Message, error); }
                    }
                    foreach (var owner in owners) CaptureRoot(owner, identities.Get(owner), true);
                    foreach (var root in LoadingRegisteredStaticRoots.ReadCanonical()) CaptureRoot(root.Item2, root.Item1, false);
                    for (var member = 0; member < memberRoots.Count; member++) CaptureRoot(memberRoots[member].Key, memberRoots[member].Value, true);
                } finally { reader.EndSharedGraph(); }
            }
            Modding.Logger.Log("[HKTAS] Original Geo incoming full field walk completed: roots=" + observedRoots + ";nodes=" + observedNodes
                + ";logicalTokenCharacters=" + logicalCharacters + ";elapsedMilliseconds=" + elapsed.ElapsedMilliseconds + ";unusedRootHashProduced=false");
            return incoming;
        }
        internal static IReadOnlyDictionary<string, object> ManagedDirectory(GeoCounter counter)
            => LoadingGeoIncomingReferences.BuildOwnedDirectory(counter);
        private static void RequireCold(MonoBehaviour component)
        {
            var bytes = BoundaryMonoLifecycle.ObserveUnstartedUi(component);
            if (bytes[0] != 0 || bytes[1] != 0 || component.gameObject.activeInHierarchy)
                throw new InvalidOperationException("Original Geo frozen/produced owner has already awakened: " + component.GetType().FullName);
        }
        private static void RequireColdDirectory(IReadOnlyDictionary<string, UnityEngine.Object> directory)
        {
            // One synchronous native-read batch: retain the existing pinned
            // file hash and per-read loaded-module checks, then discard scope.
            using var validation = new LoadingEngineClockProbe.CheckpointValidation();
            foreach (var mono in directory.Values.OfType<MonoBehaviour>()) RequireCold(mono);
        }
        internal static SortedDictionary<string, UnityEngine.Object> Directory(GameObject root)
        {
            var result = new SortedDictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
            var nodes = root.GetComponentsInChildren<Transform>(true);
            if (nodes.Length != 5 || nodes.Skip(1).Any(node => node.parent != root.transform))
                throw new InvalidOperationException("Original Geo is not its exact five-owner subtree.");
            foreach (var node in nodes) {
                var path = node == root.transform ? "$" : "$/" + node.name; result.Add(path, node.gameObject);
                var components = node.GetComponents<Component>();
                if (components.Length > 12 || components.Any(component => component == null)) throw new InvalidOperationException("Original Geo component directory differs.");
                var ordinals = new Dictionary<Type, int>();
                foreach (var component in components) {
                    var type = component.GetType(); ordinals.TryGetValue(type, out var ordinal); ordinals[type] = ordinal + 1;
                    result.Add(path + ":" + type.FullName + "[" + ordinal + "]", component);
                }
            }
            return result;
        }
        internal sealed class ObjectReferences : IEqualityComparer<UnityEngine.Object>
        {
            public bool Equals(UnityEngine.Object? x, UnityEngine.Object? y) => ReferenceEquals(x, y);
            public int GetHashCode(UnityEngine.Object value) => RuntimeHelpers.GetHashCode(value);
        }
        private sealed class ManagedReferences : IEqualityComparer<object>
        {
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
        public void Dispose() { if (ReferenceEquals(FrozenCounter, template)) FrozenCounter = null;
            if (holder != null) UnityEngine.Object.DestroyImmediate(holder); holder = null; template = null; }
    }
}
