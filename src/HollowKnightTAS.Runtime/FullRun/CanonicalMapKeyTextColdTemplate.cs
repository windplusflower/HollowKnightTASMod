using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Retain only the nine original never-awakened Text leaves. Their actual
    // Key parents and original Control FSM remain outside this owner domain.
    internal sealed class CanonicalMapKeyTextColdTemplate : IDisposable
    {
        private GameObject? holder;
        private Transform? keys;
        private Transform[] parents = Array.Empty<Transform>();
        private GameObject[] templates = Array.Empty<GameObject>();
        private int[] siblings = Array.Empty<int>();
        internal static Transform? FrozenKeys { get; private set; }
        internal static bool IsOriginalParentCacheTransition(string owner, System.Reflection.FieldInfo field,
            object? before, object? after, object expectedRect, bool firstFreeze)
            => firstFreeze && CanonicalMapKeyTextDirectory.Keys.Any(key => owner == "$/" + key + "/Text:TMPro.TextMeshPro[0]")
                && field.DeclaringType == typeof(UnityEngine.UI.Graphic) && field.Name == "m_RectTransform"
                && field.FieldType == typeof(RectTransform) && ReferenceEquals(before, null)
                && expectedRect is RectTransform && ReferenceEquals(after, expectedRect);
        internal void Capture(Transform source, LoadingGeoIncomingReferences incoming)
        {
            if (holder != null || source == null) throw new InvalidOperationException("Map Key Text original cold capture is absent or repeated.");
            var directory = CanonicalMapKeyTextDirectory.Build(source); RequireCold(directory);
            keys = source;
            using var externalGuard = new CanonicalMapKeyTextExternalGuard(source);
            templates = CanonicalMapKeyTextDirectory.Keys.Select(key => source.Find(key + "/Text").gameObject).ToArray();
            parents = templates.Select(text => text.transform.parent).ToArray();
            siblings = templates.Select(text => text.transform.GetSiblingIndex()).ToArray();
            if (parents.Any(parent => parent == null || parent.gameObject.activeInHierarchy) || FrozenKeys != null)
                throw new InvalidOperationException("Map Key Text original cold capture is outside first inactive HUD activation.");
            var player = PlayerData.instance; var random = UnityEngine.Random.state;
            var particles = LoadingGameplayOracle.CaptureParticleRandomState();
            using var preservation = new LoadingFieldDigest(value => value is UnityEngine.Object native ? "original-native:" + native.GetInstanceID() : null,
                type => type == typeof(MonoBehaviour) || type == typeof(UnityEngine.Object));
            string Preserve(object owner) {
                var data = preservation.Capture(owner, "original-map-key-preservation", expandRoot: true);
                if (data.Unsupported.Count != 0) throw new InvalidOperationException("Map Key Text original preservation has unread fields: " + string.Join(";", data.Unsupported));
                return data.Sha256;
            }
            var playerBefore = Preserve(player);
            var owners = directory.Where(pair => pair.Value is MonoBehaviour).ToArray();
            var fieldBefore = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var rawBefore = new Dictionary<string, object?>(StringComparer.Ordinal);
            var slotCodec = CanonicalCarryFieldRegistry.Create(mapKeyTextDomain: true);
            string FieldDigest(object owner, System.Reflection.FieldInfo field) {
                var data = preservation.Capture(field.GetValue(owner), "original-map-key-field-preservation", expandRoot: false);
                if (data.Unsupported.Count != 0) throw new InvalidOperationException("Map Key Text field preservation has unread fields: " + field.Name);
                return data.Sha256;
            }
            foreach (var owner in owners)
                foreach (var field in CanonicalMapKeyTextCarry.ManagedFields(owner.Value.GetType())) {
                    var key = owner.Key + "/" + field.DeclaringType!.FullName + "/" + field.Name;
                    fieldBefore.Add(key, FieldDigest(owner.Value, field)); rawBefore.Add(key, field.GetValue(owner.Value));
                    if (owner.Value is TMPro.TextMeshPro && field.DeclaringType == typeof(UnityEngine.UI.Graphic) && field.Name == "m_RectTransform"
                        && !ReferenceEquals(rawBefore[key], null))
                        throw new InvalidOperationException("Map Key Text cold original parent cache is already populated; this source shape is unsupported.");
                }
            var before = owners.Select(owner => Preserve(owner.Value)).ToArray();
            var nativeBefore = CanonicalMapKeyTextCarry.Capture(directory);
            var freezeConsumed = false;
            void Verify(string stage) {
                externalGuard.Verify();
                if (stage == "freeze-original-parent" && freezeConsumed)
                    throw new InvalidOperationException("Map Key Text original parent callback phase is single-use.");
                var playerEqual = ReferenceEquals(player, PlayerData.instance) && playerBefore == Preserve(player);
                var randomEqual = random.Equals(UnityEngine.Random.state);
                var particlesEqual = particles.SequenceEqual(LoadingGameplayOracle.CaptureParticleRandomState());
                var after = owners.Select(owner => Preserve(owner.Value)).ToArray();
                var differences = new List<object>();
                var originalFreeze = stage == "freeze-original-parent" && !freezeConsumed;
                var acceptedTransitions = 0;
                var exact = true;
                for (var index = 0; index < owners.Length; index++) {
                    foreach (var field in CanonicalMapKeyTextCarry.ManagedFields(owners[index].Value.GetType())) {
                        var key = owners[index].Key + "/" + field.DeclaringType!.FullName + "/" + field.Name;
                        var actual = FieldDigest(owners[index].Value, field);
                        var raw = field.GetValue(owners[index].Value);
                        var isCache = owners[index].Value is TMPro.TextMeshPro && field.DeclaringType == typeof(UnityEngine.UI.Graphic) && field.Name == "m_RectTransform";
                        var transition = originalFreeze && isCache && IsOriginalParentCacheTransition(owners[index].Key, field,
                            rawBefore[key], raw, ((Component)owners[index].Value).GetComponent<RectTransform>(), true);
                        if (transition) acceptedTransitions++;
                        if (fieldBefore[key] != actual || !slotCodec.SameRawSlot(rawBefore[key], raw, field.FieldType, 0)) {
                            differences.Add(new { field = key, before = fieldBefore[key], after = actual, exactOriginalParentCallback = transition });
                            if (!transition) exact = false;
                        }
                        if (originalFreeze && isCache && !transition) exact = false;
                    }
                }
                var ownerEqual = before.SequenceEqual(after);
                var nativeAfter = CanonicalMapKeyTextCarry.Capture(directory);
                var nativeEqual = nativeBefore.Lifecycle.SequenceEqual(nativeAfter.Lifecycle)
                    && nativeBefore.ActiveSelf.SequenceEqual(nativeAfter.ActiveSelf) && nativeBefore.Readonly.Aliases.SequenceEqual(nativeAfter.Readonly.Aliases);
                Modding.Logger.Log("[HKTAS] Map Key Text original cold preservation: " + Newtonsoft.Json.JsonConvert.SerializeObject(new {
                    stage, playerEqual, randomEqual, particlesEqual, ownerEqual, nativeEqual, acceptedTransitions, exact, changedFields = differences }));
                RequireCold(directory);
                if (!playerEqual || !randomEqual || !particlesEqual || !nativeEqual || !exact || (originalFreeze ? acceptedTransitions != 9 : !ownerEqual))
                    throw new InvalidOperationException("Map Key Text original inactive producer preservation differs at " + stage + "; see exact field-name/hash witness.");
                if (originalFreeze) {
                    freezeConsumed = true;
                    before = after;
                    foreach (var owner in owners)
                        foreach (var field in CanonicalMapKeyTextCarry.ManagedFields(owner.Value.GetType())) {
                            var key = owner.Key + "/" + field.DeclaringType!.FullName + "/" + field.Name;
                            fieldBefore[key] = FieldDigest(owner.Value, field); rawBefore[key] = field.GetValue(owner.Value);
                        }
                }
            }
            var originalSelf = templates.Select(text => text.activeSelf).ToArray();
            holder = new GameObject("__HKTAS_OriginalMapKeyTextColdTemplate"); holder.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(holder);
            var frozenMapKey = new GameObject("Map Key"); frozenMapKey.transform.SetParent(holder.transform, false);
            var frozenKeys = new GameObject("Keys"); frozenKeys.transform.SetParent(frozenMapKey.transform, false);
            FrozenKeys = frozenKeys.transform;
            for (var index = 0; index < templates.Length; index++) {
                var frozenParent = new GameObject(CanonicalMapKeyTextDirectory.Keys[index]);
                frozenParent.transform.SetParent(FrozenKeys, false);
                templates[index].transform.SetParent(frozenParent.transform, false);
            }
            Verify("freeze-original-parent");
            var created = ProduceInactive();
            Verify("clone-original-inactive-producer");
            if (!created.Select(text => text.activeSelf).SequenceEqual(originalSelf))
                throw new InvalidOperationException("Map Key Text producer changed serialized activation.");
            incoming.Prepare(LoadingGeoIncomingReferences.BuildMapKeyDirectory(source, created)).Apply();
            Verify("rebind-original-incoming");
            RequireCold(directory);
            Modding.Logger.Log("[HKTAS] Original Map Key complete cold nine-leaf/thirty-six-Mono producer installed with full incoming alias readback.");
        }
        internal GameObject[] ProduceInactive()
        {
            if (keys == null || holder == null || holder.activeSelf || FrozenKeys == null || templates.Length != 9
                || parents.Any(parent => parent == null || parent.gameObject.activeInHierarchy))
                throw new InvalidOperationException("Map Key original inactive producer has no retained nine-leaf template.");
            RequireCold(CanonicalMapKeyTextDirectory.Build(FrozenKeys));
            var clones = new GameObject[9];
            for (var index = 0; index < clones.Length; index++) {
                var clone = UnityEngine.Object.Instantiate(templates[index], holder.transform, false); clone.name = templates[index].name;
                clone.transform.SetParent(parents[index], false); clone.transform.SetSiblingIndex(siblings[index]);
                clones[index] = clone;
            }
            RequireCold(CanonicalMapKeyTextDirectory.Build(keys, clones));
            return clones;
        }
        private static void RequireCold(IReadOnlyDictionary<string, UnityEngine.Object> directory)
        {
            using var validation = new LoadingEngineClockProbe.CheckpointValidation();
            foreach (var owner in directory.Values.OfType<MonoBehaviour>()) {
                var flags = BoundaryMonoLifecycle.ObserveUnstartedUi(owner);
                if (flags[0] != 0 || flags[1] != 0 || owner.gameObject.activeInHierarchy)
                    throw new InvalidOperationException("Map Key Text original complete cold native directory has awakened: " + owner.GetType().FullName);
            }
        }
        public void Dispose()
        {
            if (holder != null) UnityEngine.Object.DestroyImmediate(holder);
            FrozenKeys = null; holder = null; keys = null; templates = Array.Empty<GameObject>(); parents = Array.Empty<Transform>();
        }
    }
}
