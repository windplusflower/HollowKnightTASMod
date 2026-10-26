using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Original thirteen-root presentation closure; gameplay parents remain external.
    internal static class CanonicalHudPresentationBoundaryPolicy
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static object Reset(CanonicalHudPresentationColdTemplate template, LoadingGameplayOracle observer)
        {
            void Require() => LoadingSerialSceneProbe.RequireCanonicalMutationBoundary();
            Require(); template.RequireFrozenAssets();
            var keys = CanonicalHudPresentationDirectory.Current();
            var directory = CanonicalHudPresentationDirectory.Build(keys, allowGeneratedSubmeshes: true);
            var generated = CanonicalHudPresentationDirectory.GeneratedSubmeshes(directory);
            var old = CanonicalHudPresentationDirectory.RootPaths.Select(path => keys.Find(path).gameObject).ToArray();
            var parents = old.Select(root => root.transform.parent).ToArray();
            var parentActive = parents.Select(parent => parent != null && parent.gameObject.activeInHierarchy).ToArray();
            CanonicalHudBorderInputs.Data inputs;
            CanonicalHudBorderQueuePartition queues;
            try {
                var paths = CanonicalHudPresentationDirectory.RootPaths;
                if (Enumerable.Range(0, 13).Any(index => parents[index] == null
                    || parents[index].gameObject.activeInHierarchy != CanonicalHudBorderInputs.IsBorderPath(paths[index])
                    || old[index].activeInHierarchy != CanonicalHudBorderInputs.IsBorderPath(paths[index])))
                    throw new InvalidOperationException("Original HUD mixed active Border/cold Charms parent phase is not ready.");
                inputs = CanonicalHudBorderInputs.Capture(keys, directory, template.BindOriginalAsset);
                queues = new CanonicalHudBorderQueuePartition(CanonicalHudBorderInputs.Paths.Select(path => keys.Find(path).GetComponent<TMPro.TextMeshPro>()).ToArray());
            } catch (Exception error) {
                // Snapshot admission only: preserve an ordinary in-progress menu without injecting a close event or time.
                template.MarkUnsupported(error.GetType().Name + ": " + error.Message);
                return new { snapshotSupported = false, commonHudMutationExecuted = false, sourceInputsPreserved = true,
                    reason = template.SnapshotUnsupportedReason, completeStateCoverage = false };
            }
            using var queueScope = queues;
            var native = directory.ToDictionary(pair => pair.Key, pair => (object)pair.Value, StringComparer.Ordinal);
            var poseOwners = parents.SelectMany(Ancestors).Distinct().ToArray();
            var parentRoots = poseOwners.SelectMany(parent => parent.GetComponents<MonoBehaviour>()).Distinct()
                .Select((owner, index) => new KeyValuePair<string, object?>("hud-parent[" + index + "]", owner)).ToArray();
            using var externalGuard = new CanonicalMapKeyTextExternalGuard(parentRoots, native, poseOwners);
            CanonicalMarkerEventCarry.RequireEmpty(directory);
            CanonicalMarkerReadonlyCarry.CaptureMapKey(directory);
            var icon = directory.Values.OfType<MenuButtonIcon>().Single();
            if (typeof(ActionButtonIconBase).GetField("OnIconUpdate", Instance)!.GetValue(icon) != null)
                throw new InvalidOperationException("HUD presentation icon retains an unsupported listener.");
            var fsmListField = typeof(PlayMakerFSM).GetField("fsmList", BindingFlags.Static | BindingFlags.NonPublic)!;
            var fsmList = fsmListField.GetValue(null) as IList ?? throw new InvalidOperationException("Original FSM list is absent.");
            var ownedFsms = directory.Values.OfType<PlayMakerFSM>().ToArray();
            var retainedFsms = fsmList.Cast<object>().Where(item => !ownedFsms.Any(owner => ReferenceEquals(owner, item))).ToArray();
            var rawType = typeof(HutongGames.PlayMaker.Fsm);
            var rawField = typeof(PlayMakerFSM).GetField("fsm", Instance)!;
            var selectedField = typeof(PlayMakerFSM).Assembly.GetType("PlayMakerGUI", true)!
                .GetField("SelectedFSM", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
            var selectedBefore = selectedField.GetValue(null);
            var raycastField = rawType.GetField("lastRaycastHit2DInfoLUT", BindingFlags.Static | BindingFlags.NonPublic)!;
            var raycasts = raycastField.GetValue(null) as IDictionary;
            foreach (var owner in ownedFsms) {
                var raw = rawField.GetValue(owner)!;
                if (rawType.GetField("subFsmList", Instance)!.GetValue(raw) is IList sub && sub.Count != 0
                    || rawType.GetField("myLog", Instance)!.GetValue(raw) != null
                    || (bool)rawType.GetField("handleLevelLoaded", Instance)!.GetValue(raw)
                    || ReferenceEquals(selectedBefore, raw) || raycasts != null && raycasts.Contains(raw))
                    throw new InvalidOperationException("HUD presentation FSM has an external editor, log, sub-FSM or scene continuation.");
            }
            var input = GameManager.instance.inputHandler;
            var controllerEvent = typeof(InputHandler).GetField("RefreshActiveControllerEvent", Instance)!;
            var controllerBefore = controllerEvent.GetValue(input);
            if (controllerBefore is Delegate callbacks && callbacks.GetInvocationList().Any(item => ReferenceEquals(item.Target, icon)))
                throw new InvalidOperationException("Inactive HUD presentation icon retains a controller subscription.");
            var texts = directory.Values.OfType<TMPro.TextMeshPro>().ToArray();
            var meshField = typeof(TMPro.TextMeshPro).GetField("m_mesh", Instance)!;
            var meshes = texts.Select(owner => meshField.GetValue(owner)).Concat(generated.Values.OfType<TMPro.TMP_SubMesh>().Select(owner => typeof(TMPro.TMP_SubMesh).GetField("m_mesh", Instance)!.GetValue(owner))).Where(value => !ReferenceEquals(value, null)).Cast<Mesh>().ToArray();
            if (meshes.Any(mesh => mesh == null) || meshes.Distinct().Count() != meshes.Length)
                throw new InvalidOperationException("HUD presentation retired native meshes are already dead or shared.");
            // The existing managed incoming walk cannot read native MeshFilter /
            // collider slots. Check those real consumers before native retirement.
            var filters = directory.Values.Concat(generated.Values).OfType<MeshFilter>().ToArray();
            foreach (var filter in Resources.FindObjectsOfTypeAll<MeshFilter>())
                if (filter != null && meshes.Any(mesh => mesh == filter.sharedMesh) && !filters.Any(owner => ReferenceEquals(owner, filter)))
                    throw new InvalidOperationException("HUD presentation owned mesh has an external native MeshFilter consumer.");
            foreach (var collider in Resources.FindObjectsOfTypeAll<MeshCollider>())
                if (collider != null && meshes.Any(mesh => mesh == collider.sharedMesh))
                    throw new InvalidOperationException("HUD presentation owned mesh has an external native MeshCollider consumer.");
            foreach (var renderer in Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>())
                if (renderer != null && meshes.Any(mesh => mesh == renderer.sharedMesh))
                    throw new InvalidOperationException("HUD presentation owned mesh has an external native skinned-renderer consumer.");
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
            var coldTexts = directory.Where(pair => pair.Value is TMPro.TextMeshPro && !CanonicalHudPresentationDirectory.IsBorderMember(pair.Key)).Select(pair => (TMPro.TextMeshPro)pair.Value).ToArray();
            var textIds = new HashSet<int>(coldTexts.Select(text => text.GetInstanceID()));
            foreach (var name in queueNames) {
                var value = tmpType.GetField(name, Instance)!.GetValue(tmp);
                if (value is IDictionary lookup && lookup.Keys.Cast<object>().Any(key => key is int id && textIds.Contains(id))
                    || value is IList queue && queue.Cast<object>().Any(item => coldTexts.Any(owner => ReferenceEquals(owner, item))))
                    throw new InvalidOperationException("Inactive HUD presentation retains a TMP rebuild entry; natural queue retirement is required.");
            }
            using var fields = new LoadingFieldDigest(value => value is UnityEngine.Object native ? "marker-canon-native:" + native.GetInstanceID() : null,
                type => type == typeof(MonoBehaviour) || type == typeof(UnityEngine.Object), sharedFieldReaders: true);
            string Tmp() {
                var result = fields.Capture(tmp, "original-TMP-queues", expandRoot: true);
                if (result.Unsupported.Count != 0) throw new InvalidOperationException("HUD presentation TMP queue preservation has unread fields.");
                return result.Sha256;
            }
            var tmpBefore = Tmp();
            var rng = UnityEngine.Random.state; var particles = LoadingGameplayOracle.CaptureParticleRandomState();
            var incoming = new LoadingGeoIncomingReferences();
            CanonicalGeoColdTemplate.ObserveIncoming(GameCameras.instance.geoCounter, presentationIncoming: incoming,
                presentationRoot: keys, retiringPresentationMeshes: true, borderQueues: queues);
            try { incoming.Prepare(LoadingGeoIncomingReferences.BuildHudPresentationDirectory(CanonicalHudPresentationColdTemplate.FrozenInventory!)); }
            catch (Exception error) {
                template.MarkUnsupported("Original HUD incoming replacement preflight: " + error.Message);
                return new { snapshotSupported = false, commonHudMutationExecuted = false, sourceInputsPreserved = true, reason = template.SnapshotUnsupportedReason };
            }
            var owners = directory.Values.Concat(generated.Values).OfType<MonoBehaviour>().ToDictionary(owner => owner.GetInstanceID());
            observer.RecaptureOwnedHudScheduler(Require, () => { });
            var born = new HashSet<int>();
            var transition = observer.PrepareOwnedHudLifecycle(owners, id => born.Contains(id), out var retireStarts);
            queues.RequireBeforeMutation();
            var created = template.ProduceMixed(inputs);
            foreach (var owner in CanonicalHudPresentationDirectory.Build(keys, created).Values.OfType<MonoBehaviour>()) born.Add(owner.GetInstanceID());
            var borderCreated = CanonicalHudBorderInputs.Paths.Select(path => (TMPro.TextMeshPro)CanonicalHudPresentationDirectory.Build(keys, created)["$/" + path + ":TMPro.TextMeshPro[0]"]).ToArray();
            var verifyQueues = queues.ExpectOriginalEnableThenRetirement(borderCreated);
            void RequireCreated() => CanonicalHudPresentationColdTemplate.RequireProduced(CanonicalHudPresentationDirectory.Build(keys, created));
            RequireCreated();
            externalGuard.PrepareExplicitIncomingRebind(CanonicalHudPresentationDirectory.Build(keys, created)
                .ToDictionary(pair => pair.Key, pair => (object)pair.Value, StringComparer.Ordinal));
            var plan = incoming.Prepare(LoadingGeoIncomingReferences.BuildHudPresentationDirectory(keys, created));
            plan.Apply();
            foreach (var text in old) UnityEngine.Object.DestroyImmediate(text);
            retireStarts();
            observer.RecaptureOwnedHudScheduler(Require, transition);
            plan.VerifyReadback(); RequireCreated(); Require(); externalGuard.VerifyManagedRoots(); template.RequireFrozenAssets(); verifyQueues();
            void Preserve(bool valid, string contract) {
                if (!valid) throw new InvalidOperationException("HUD presentation mixed Canon preservation failed: " + contract);
            }
            Preserve(!created.Where((text, index) => !ReferenceEquals(text.transform.parent, parents[index])).Any(), "original parent identities");
            Preserve(!parents.Where((parent, index) => parent.gameObject.activeInHierarchy != parentActive[index]).Any(), "original parent activation");
            Preserve(!old.Any(text => text != null), "retired original roots");
            Preserve(CanonicalHudPresentationDirectory.Build(keys).Keys.SequenceEqual(directory.Keys), "complete replacement directory");
            Preserve(!meshes.Any(mesh => mesh != null), "retired owned native meshes");
            Preserve(ReferenceEquals(fsmList, fsmListField.GetValue(null)), "original FSM registry identity");
            Preserve(fsmList.Count == retainedFsms.Length, "retained FSM registry count");
            Preserve(!fsmList.Cast<object>().Where((item, index) => !ReferenceEquals(item, retainedFsms[index])).Any(), "retained FSM registry order and aliases");
            Preserve(ReferenceEquals(selectedBefore, selectedField.GetValue(null)), "selected FSM identity");
            Preserve(ReferenceEquals(raycasts, raycastField.GetValue(null)), "raycast registry identity");
            Preserve(ReferenceEquals(controllerBefore, controllerEvent.GetValue(input)), "controller callback identity");
            Preserve(ReferenceEquals(tmp, tmpField.GetValue(null)), "TMP manager identity");
            Preserve(rng.Equals(UnityEngine.Random.state), "Unity RNG");
            Preserve(particles.SequenceEqual(LoadingGameplayOracle.CaptureParticleRandomState()), "particle RNG");
            return new { originalRoots = 13, originalObjects = 18, originalMonoOwners = 40, coldCharmsRoots = 10, activeBorderRoots = 3, originalAwakeAndQueuedStart = true,
                fullIncomingReadback = true, retiredOwnedMeshes = meshes.Length, controllerAndUnrelatedTmpStoragePreserved = true,
                gameplayParentDataTouched = false, sourceHistoryReplayed = false, completeStateCoverage = false };
        }
        private static IEnumerable<Transform> Ancestors(Transform node)
        { for (int depth = 0; node != null; node = node.parent) { if (++depth > 32) throw new InvalidOperationException("HUD parent bound exceeded."); yield return node; } }
    }
}
