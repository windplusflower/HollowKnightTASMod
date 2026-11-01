using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Only nine Text leaves are replaced; PD-controlled Key parents remain external.
    internal static class CanonicalMapKeyTextBoundaryPolicy
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static object Reset(CanonicalMapKeyTextColdTemplate template, LoadingGameplayOracle observer,
            Action requireParentDependency, LoadingGeoIncomingReferences? frozenIncoming = null, Tuple<Action, Action>? outerLifecycle = null, Action<Action>? exposeIncomingReadback = null)
        {
            void Require() => LoadingSerialSceneProbe.RequireCanonicalMutationBoundary();
            Require(); requireParentDependency();
            var keys = CanonicalMapKeyTextDirectory.Current();
            var directory = CanonicalMapKeyTextDirectory.Build(keys);
            using var externalGuard = new CanonicalMapKeyTextExternalGuard(keys);
            var old = CanonicalMapKeyTextDirectory.Keys.Select(key => keys.Find(key + "/Text").gameObject).ToArray();
            var parents = old.Select(text => text.transform.parent).ToArray();
            if (parents.Any(parent => parent == null || parent.gameObject.activeInHierarchy) || old.Any(text => text.activeInHierarchy))
                throw new InvalidOperationException("Active Map Key Text/World Map needs an explicit future natural-close continuation.");
            CanonicalMarkerEventCarry.RequireEmpty(directory);
            // Active color-tween continuations cannot be discarded by Canon.
            CanonicalMarkerReadonlyCarry.CaptureMapKey(directory);
            var input = GameManager.instance.inputHandler;
            var controllerEvent = typeof(InputHandler).GetField("RefreshActiveControllerEvent", Instance)!;
            var controllerBefore = controllerEvent.GetValue(input);
            var texts = directory.Values.OfType<TMPro.TextMeshPro>().ToArray();
            var meshField = typeof(TMPro.TextMeshPro).GetField("m_mesh", Instance)!;
            var meshes = texts.Select(owner => meshField.GetValue(owner)).Where(value => !ReferenceEquals(value, null)).Cast<Mesh>().ToArray();
            if (meshes.Any(mesh => mesh == null) || meshes.Distinct().Count() != meshes.Length)
                throw new InvalidOperationException("Map Key Text retired native meshes are already dead or shared.");
            // The existing managed incoming walk cannot read native MeshFilter /
            // collider slots. Check those real consumers before native retirement.
            var filters = directory.Values.OfType<MeshFilter>().ToArray();
            var meshSet = new HashSet<Mesh>(meshes);
            foreach (var filter in Resources.FindObjectsOfTypeAll<MeshFilter>())
                if (filter != null && meshSet.Contains(filter.sharedMesh) && !filters.Any(owner => ReferenceEquals(owner, filter)))
                    throw new InvalidOperationException("Map Key owned mesh has an external native MeshFilter consumer.");
            foreach (var collider in Resources.FindObjectsOfTypeAll<MeshCollider>())
                if (collider != null && meshSet.Contains(collider.sharedMesh))
                    throw new InvalidOperationException("Map Key owned mesh has an external native MeshCollider consumer.");
            foreach (var renderer in Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>())
                if (renderer != null && meshSet.Contains(renderer.sharedMesh))
                    throw new InvalidOperationException("Map Key owned mesh has an external native skinned-renderer consumer.");
            // TMP's original OnDestroy unregisters both queues. An inactive
            // hint must already be absent; all unrelated queue storage remains
            // byte/field-identical, including hidden capacities and aliases.
            var tmpType = typeof(TMPro.TMP_UpdateManager);
            var tmpField = tmpType.GetField("s_Instance", BindingFlags.Static | BindingFlags.NonPublic)!;
            var tmp = tmpField.GetValue(null);
            if (tmp == null) throw new InvalidOperationException("The original HUD has no initialized TMP update manager.");
            var queueNames = new[] { "m_GraphicQueueLookup", "m_GraphicRebuildQueue", "m_LayoutQueueLookup", "m_LayoutRebuildQueue" };
            if (!tmpType.GetFields(Instance).Select(field => field.Name).OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(queueNames))
                throw new InvalidOperationException("Original TMP queue field ABI differs.");
            var textIds = new HashSet<int>(texts.Select(owner => owner.GetInstanceID()));
            var textSet = new HashSet<object>(texts);
            foreach (var name in queueNames) {
                var value = tmpType.GetField(name, Instance)!.GetValue(tmp);
                if (value is IDictionary lookup && lookup.Keys.Cast<object>().Any(key => key is int id && textIds.Contains(id))
                    || value is IList queue && queue.Cast<object>().Any(item => textSet.Contains(item)))
                    throw new InvalidOperationException("Inactive Map Key Text retains a TMP rebuild entry; natural queue retirement is required.");
            }
            using var fields = new LoadingFieldDigest(value => value is UnityEngine.Object native ? "marker-canon-native:" + native.GetInstanceID() : null,
                type => type == typeof(MonoBehaviour) || type == typeof(UnityEngine.Object), sharedFieldReaders: true);
            string Tmp() {
                var result = fields.Capture(tmp, "original-TMP-queues", expandRoot: true);
                if (result.Unsupported.Count != 0) throw new InvalidOperationException("Map Key Text TMP queue preservation has unread fields.");
                return result.Sha256;
            }
            var tmpBefore = Tmp();
            var rng = UnityEngine.Random.state; var particles = LoadingGameplayOracle.CaptureParticleRandomState();
            var incoming = frozenIncoming ?? new LoadingGeoIncomingReferences();
            if (frozenIncoming == null)
                CanonicalGeoColdTemplate.ObserveIncoming(GameCameras.instance.geoCounter, mapKeyIncoming: incoming, mapKeyRoot: keys, retiringMapKeyMeshes: true);
            else incoming.RequireMapKeyTextArraySlots((Array)typeof(FadeGroup).GetField("texts")!.GetValue(keys.parent.GetComponent<FadeGroup>()));
            var owners = directory.Values.OfType<MonoBehaviour>().ToDictionary(owner => owner.GetInstanceID());
            Action transition, retireStarts;
            if (outerLifecycle == null) {
                observer.RecaptureOwnedHudScheduler(Require, () => { });
                transition = observer.PrepareOwnedHudLifecycle(owners, _ => false, out retireStarts);
            }
            else { transition = outerLifecycle.Item1; retireStarts = outerLifecycle.Item2; }
            var created = template.ProduceInactive();
            void RequireCreated() {
                var current = CanonicalMapKeyTextDirectory.Build(keys, created);
                using var validation = new LoadingEngineClockProbe.CheckpointValidation();
                foreach (var owner in current.Values.OfType<MonoBehaviour>()) {
                    var flags = BoundaryMonoLifecycle.ObserveUnstartedUi(owner);
                    if (flags[0] != 0 || flags[1] != 0 || flags[3] != 0 || flags[4] != 0 || owner.gameObject.activeInHierarchy)
                        throw new InvalidOperationException("Map Key Text producer has an active or initialized native owner.");
                }
            }
            RequireCreated();
            externalGuard.PrepareIncomingRebind(created);
            var plan = incoming.Prepare(LoadingGeoIncomingReferences.BuildMapKeyDirectory(keys, created),
                witness => Modding.Logger.Log("[HKTAS] Map Key B exact incoming old/new slots: " + Newtonsoft.Json.JsonConvert.SerializeObject(witness)));
            plan.Apply();
            foreach (var text in old) UnityEngine.Object.DestroyImmediate(text);
            retireStarts();
            observer.RecaptureOwnedHudScheduler(Require, transition);
            plan.VerifyReadback(); RequireCreated(); Require(); externalGuard.Verify(); requireParentDependency();
            if (created.Where((text, index) => !ReferenceEquals(text.transform.parent, parents[index])).Any()
                || parents.Any(parent => parent.gameObject.activeInHierarchy) || old.Any(text => text != null)
                || !CanonicalMapKeyTextDirectory.Build(keys).Values.OfType<GameObject>().OrderBy(text => text.GetInstanceID())
                    .SequenceEqual(created.OrderBy(text => text.GetInstanceID())) || meshes.Any(mesh => mesh != null)
                || !ReferenceEquals(controllerBefore, controllerEvent.GetValue(input)) || !ReferenceEquals(tmp, tmpField.GetValue(null)) || tmpBefore != Tmp()
                || !rng.Equals(UnityEngine.Random.state) || !particles.SequenceEqual(LoadingGameplayOracle.CaptureParticleRandomState()))
                throw new InvalidOperationException("Map Key Text cold Canon changed an external callback, queue, RNG, registration or retired native mesh contract.");
            exposeIncomingReadback?.Invoke(plan.VerifyReadback);
            return new { originalTextLeaves = 9, originalMonoOwners = 36, realColdNative = true,
                fullIncomingReadback = true, retiredOwnedMeshes = meshes.Length, controllerAndTmpQueuesPreserved = true,
                keyParentDataTouched = false, sourceHistoryReplayed = false, completeStateCoverage = false };
        }
    }
}
