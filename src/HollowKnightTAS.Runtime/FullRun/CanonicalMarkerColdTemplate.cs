using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Retain the actual never-awakened five-owner original, including its
    // grandchild, constructor-owned arrays, icon and font callbacks.
    internal sealed class CanonicalMarkerColdTemplate : IDisposable
    {
        private GameObject? holder, template;
        private Transform? parent;
        private int sibling;
        internal static GameObject? FrozenOwner { get; private set; }
        internal static bool IsOriginalParentCacheTransition(string owner, System.Reflection.FieldInfo field,
            object? before, object? after, object expectedRect, bool firstFreeze)
            => firstFreeze && (owner == "$/Text:TMPro.TextMeshPro[0]" || owner == "$/ActionButtonIcon/Label:TMPro.TextMeshPro[0]")
                && field.DeclaringType == typeof(UnityEngine.UI.Graphic) && field.Name == "m_RectTransform"
                && field.FieldType == typeof(RectTransform) && ReferenceEquals(before, null)
                && expectedRect is RectTransform && ReferenceEquals(after, expectedRect);
        internal void Capture(GameObject source, LoadingGeoIncomingReferences incoming)
        {
            if (holder != null || source == null) throw new InvalidOperationException("Marker original cold capture is absent or repeated.");
            var directory = CanonicalMarkerDirectory.Build(source); RequireCold(directory);
            parent = source.transform.parent; sibling = source.transform.GetSiblingIndex();
            if (parent == null || parent.gameObject.activeInHierarchy || FrozenOwner != null)
                throw new InvalidOperationException("Marker original cold capture is outside first inactive HUD activation.");
            var player = PlayerData.instance; var random = UnityEngine.Random.state;
            var particles = LoadingGameplayOracle.CaptureParticleRandomState();
            using var preservation = new LoadingFieldDigest(value => value is UnityEngine.Object native ? "original-native:" + native.GetInstanceID() : null,
                type => type == typeof(MonoBehaviour) || type == typeof(UnityEngine.Object));
            string Preserve(object owner) {
                var data = preservation.Capture(owner, "original-marker-preservation", expandRoot: true);
                if (data.Unsupported.Count != 0) throw new InvalidOperationException("Marker original preservation has unread fields: " + string.Join(";", data.Unsupported));
                return data.Sha256;
            }
            var playerBefore = Preserve(player);
            var owners = directory.Where(pair => pair.Value is MonoBehaviour).ToArray();
            var fieldBefore = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var rawBefore = new Dictionary<string, object?>(StringComparer.Ordinal);
            var slotCodec = CanonicalCarryFieldRegistry.Create(markerDomain: true);
            string FieldDigest(object owner, System.Reflection.FieldInfo field) {
                var data = preservation.Capture(field.GetValue(owner), "original-marker-field-preservation", expandRoot: false);
                if (data.Unsupported.Count != 0) throw new InvalidOperationException("Marker field preservation has unread fields: " + field.Name);
                return data.Sha256;
            }
            foreach (var owner in owners)
                foreach (var field in CanonicalMarkerCarry.ManagedFields(owner.Value.GetType())) {
                    var key = owner.Key + "/" + field.DeclaringType!.FullName + "/" + field.Name;
                    fieldBefore.Add(key, FieldDigest(owner.Value, field)); rawBefore.Add(key, field.GetValue(owner.Value));
                    if (owner.Value is TMPro.TextMeshPro && field.DeclaringType == typeof(UnityEngine.UI.Graphic) && field.Name == "m_RectTransform"
                        && !ReferenceEquals(rawBefore[key], null))
                        throw new InvalidOperationException("Marker cold original parent cache is already populated; this source shape is unsupported.");
                }
            var before = owners.Select(owner => Preserve(owner.Value)).ToArray();
            var nativeBefore = CanonicalMarkerCarry.Capture(source);
            var freezeConsumed = false;
            void Verify(string stage) {
                if (stage == "freeze-original-parent" && freezeConsumed)
                    throw new InvalidOperationException("Marker original parent callback phase is single-use.");
                var playerEqual = ReferenceEquals(player, PlayerData.instance) && playerBefore == Preserve(player);
                var randomEqual = random.Equals(UnityEngine.Random.state);
                var particlesEqual = particles.SequenceEqual(LoadingGameplayOracle.CaptureParticleRandomState());
                var after = owners.Select(owner => Preserve(owner.Value)).ToArray();
                var differences = new List<object>();
                var originalFreeze = stage == "freeze-original-parent" && !freezeConsumed;
                var acceptedTransitions = 0;
                var exact = true;
                for (var index = 0; index < owners.Length; index++) {
                    foreach (var field in CanonicalMarkerCarry.ManagedFields(owners[index].Value.GetType())) {
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
                var nativeAfter = CanonicalMarkerCarry.Capture(source);
                var nativeEqual = nativeBefore.Lifecycle.SequenceEqual(nativeAfter.Lifecycle) && nativeBefore.Registered.SequenceEqual(nativeAfter.Registered)
                    && nativeBefore.ActiveSelf.SequenceEqual(nativeAfter.ActiveSelf) && nativeBefore.Readonly.Aliases.SequenceEqual(nativeAfter.Readonly.Aliases);
                Modding.Logger.Log("[HKTAS] Marker original cold preservation: " + Newtonsoft.Json.JsonConvert.SerializeObject(new {
                    stage, playerEqual, randomEqual, particlesEqual, ownerEqual, nativeEqual, acceptedTransitions, exact, changedFields = differences }));
                RequireCold(directory);
                if (!playerEqual || !randomEqual || !particlesEqual || !nativeEqual || !exact || (originalFreeze ? acceptedTransitions != 2 : !ownerEqual))
                    throw new InvalidOperationException("Marker original inactive producer preservation differs at " + stage + "; see exact field-name/hash witness.");
                if (originalFreeze) {
                    freezeConsumed = true;
                    before = after;
                    foreach (var owner in owners)
                        foreach (var field in CanonicalMarkerCarry.ManagedFields(owner.Value.GetType())) {
                            var key = owner.Key + "/" + field.DeclaringType!.FullName + "/" + field.Name;
                            fieldBefore[key] = FieldDigest(owner.Value, field); rawBefore[key] = field.GetValue(owner.Value);
                        }
                }
            }
            var originalSelf = source.activeSelf;
            holder = new GameObject("__HKTAS_OriginalMarkerColdTemplate"); holder.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(holder);
            template = source; FrozenOwner = source;
            source.transform.SetParent(holder.transform, false);
            Verify("freeze-original-parent");
            var created = ProduceInactive();
            Verify("clone-original-inactive-producer");
            if (created.activeSelf != originalSelf) throw new InvalidOperationException("Marker original producer changed serialized activation.");
            incoming.Prepare(LoadingGeoIncomingReferences.BuildMarkerDirectory(created)).Apply();
            Verify("rebind-original-incoming");
            RequireCold(directory);
            Modding.Logger.Log("[HKTAS] Original Marker complete cold five-owner/eight-Mono producer installed with full incoming alias readback; native configuration remains unqualified.");
        }
        internal GameObject ProduceInactive()
        {
            if (template == null || holder == null || holder.activeSelf || parent == null || parent.gameObject.activeInHierarchy)
                throw new InvalidOperationException("Marker original inactive producer has no retained template.");
            RequireCold(CanonicalMarkerDirectory.Build(template));
            var clone = UnityEngine.Object.Instantiate(template, holder.transform, false); clone.name = template.name;
            var directory = CanonicalMarkerDirectory.Build(clone); RequireCold(directory);
            clone.transform.SetParent(parent, false); clone.transform.SetSiblingIndex(sibling);
            RequireCold(directory);
            return clone;
        }
        private static void RequireCold(IReadOnlyDictionary<string, UnityEngine.Object> directory)
        {
            using var validation = new LoadingEngineClockProbe.CheckpointValidation();
            foreach (var owner in directory.Values.OfType<MonoBehaviour>()) {
                var flags = BoundaryMonoLifecycle.ObserveUnstartedUi(owner);
                if (flags[0] != 0 || flags[1] != 0 || owner.gameObject.activeInHierarchy)
                    throw new InvalidOperationException("Marker original complete cold native directory has awakened: " + owner.GetType().FullName);
            }
        }
        public void Dispose()
        {
            if (ReferenceEquals(FrozenOwner, template)) FrozenOwner = null;
            if (holder != null) UnityEngine.Object.DestroyImmediate(holder);
            holder = null; template = null;
        }
    }
}
