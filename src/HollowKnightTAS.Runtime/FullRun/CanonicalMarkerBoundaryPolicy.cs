using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Fixed level1/2077 is the World Map button hint, not MapMarkerMenu or
    // PlayerData's placed markers. Every B uses this same cold Canon before
    // source capture or target import. No source startup history is replayed.
    internal static class CanonicalMarkerBoundaryPolicy
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static void RequireCold(GameObject root)
        {
            LoadingSerialSceneProbe.RequireCanonicalMutationBoundary();
            if (root == null || root.transform.parent == null || root.transform.parent.gameObject.activeInHierarchy
                || root.activeInHierarchy) throw new InvalidOperationException("Marker cold Canon requires its actual inactive World Map parent.");
            using var validation = new LoadingEngineClockProbe.CheckpointValidation();
            foreach (var owner in CanonicalMarkerDirectory.Build(root).Values.OfType<MonoBehaviour>()) {
                var flags = BoundaryMonoLifecycle.ObserveUnstartedUi(owner);
                if (flags[0] != 0 || flags[1] != 0 || flags[3] != 0 || flags[4] != 0)
                    throw new InvalidOperationException("Marker canonical owner is not a real never-awakened inactive instance.");
            }
        }
        internal static object Reset(CanonicalMarkerColdTemplate template, LoadingGameplayOracle observer)
        {
            void Require() => LoadingSerialSceneProbe.RequireCanonicalMutationBoundary();
            Require();
            var old = CanonicalMarkerDirectory.Current();
            var parent = old.transform.parent;
            if (parent == null || parent.gameObject.activeInHierarchy || old.activeInHierarchy)
                throw new InvalidOperationException("Active Marker/World Map needs an explicit future natural-close continuation.");
            var directory = CanonicalMarkerDirectory.Build(old);
            CanonicalMarkerEventCarry.RequireEmpty(directory);
            // Active color-tween continuations cannot be discarded by Canon.
            CanonicalMarkerReadonlyCarry.Capture(directory);
            var icon = directory.Values.OfType<MenuButtonIcon>().Single();
            var iconEvent = typeof(ActionButtonIconBase).GetField("OnIconUpdate", Instance)!;
            if (iconEvent.GetValue(icon) != null) throw new InvalidOperationException("Marker hint has an unsupported OnIconUpdate listener.");
            var input = GameManager.instance.inputHandler;
            var controllerEvent = typeof(InputHandler).GetField("RefreshActiveControllerEvent", Instance)!;
            var controllerBefore = controllerEvent.GetValue(input);
            if (controllerBefore is Delegate callbacks && callbacks.GetInvocationList().Any(item => ReferenceEquals(item.Target, icon)))
                throw new InvalidOperationException("Inactive Marker still has an original controller subscription.");
            var fsms = typeof(PlayMakerFSM).GetField("fsmList", BindingFlags.Static | BindingFlags.NonPublic)!;
            var list = fsms.GetValue(null) as IList ?? throw new InvalidOperationException("Original FSM registration list is absent.");
            var oldFsms = directory.Values.OfType<PlayMakerFSM>().ToArray();
            var raw = typeof(PlayMakerFSM).GetField("fsm", Instance)!.GetValue(oldFsms.Single())!;
            var rawType = typeof(HutongGames.PlayMaker.Fsm);
            if (rawType.GetField("subFsmList", Instance)!.GetValue(raw) is IList sub && sub.Count != 0
                || rawType.GetField("myLog", Instance)!.GetValue(raw) != null
                || (bool)rawType.GetField("handleLevelLoaded", Instance)!.GetValue(raw))
                throw new InvalidOperationException("Marker retirement has an unregistered sub-FSM, log or scene event continuation.");
            var gui = typeof(PlayMakerFSM).Assembly.GetType("PlayMakerGUI", true)!;
            var selectedField = gui.GetField("SelectedFSM", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
            var selectedBefore = selectedField.GetValue(null);
            var raycastField = rawType.GetField("lastRaycastHit2DInfoLUT", BindingFlags.Static | BindingFlags.NonPublic)!;
            var raycasts = raycastField.GetValue(null) as IDictionary;
            if (ReferenceEquals(selectedBefore, raw) || raycasts != null && raycasts.Contains(raw))
                throw new InvalidOperationException("Marker retirement has an external editor or native raycast continuation.");
            var retainedFsms = list.Cast<object>().Where(item => !oldFsms.Any(owner => ReferenceEquals(owner, item))).ToArray();
            var texts = directory.Values.OfType<TMPro.TextMeshPro>().ToArray();
            var meshField = typeof(TMPro.TextMeshPro).GetField("m_mesh", Instance)!;
            var meshes = texts.Select(owner => meshField.GetValue(owner)).Where(value => !ReferenceEquals(value, null)).Cast<Mesh>().ToArray();
            if (meshes.Any(mesh => mesh == null) || meshes.Distinct().Count() != meshes.Length)
                throw new InvalidOperationException("Marker retired native meshes are already dead or shared.");
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
            foreach (var name in queueNames) {
                var value = tmpType.GetField(name, Instance)!.GetValue(tmp);
                if (value is IDictionary lookup && lookup.Keys.Cast<object>().Any(key => key is int id && textIds.Contains(id))
                    || value is IList queue && queue.Cast<object>().Any(item => texts.Any(owner => ReferenceEquals(owner, item))))
                    throw new InvalidOperationException("Inactive Marker retains a TMP rebuild entry; natural queue retirement is required.");
            }
            using var fields = new LoadingFieldDigest(value => value is UnityEngine.Object native ? "marker-canon-native:" + native.GetInstanceID() : null,
                type => type == typeof(MonoBehaviour) || type == typeof(UnityEngine.Object));
            string Tmp() {
                var result = fields.Capture(tmp, "original-TMP-queues", expandRoot: true);
                if (result.Unsupported.Count != 0) throw new InvalidOperationException("Marker TMP queue preservation has unread fields.");
                return result.Sha256;
            }
            var tmpBefore = Tmp();
            var rng = UnityEngine.Random.state; var particles = LoadingGameplayOracle.CaptureParticleRandomState();
            var incoming = new LoadingGeoIncomingReferences();
            CanonicalGeoColdTemplate.ObserveIncoming(GameCameras.instance.geoCounter, incoming, old, retiringMarkerMeshes: true);
            var owners = directory.Values.OfType<MonoBehaviour>().ToDictionary(owner => owner.GetInstanceID());
            observer.RecaptureOwnedHudScheduler(Require, () => { });
            // A true cold Instantiate must add no Start or coroutine callback.
            var transition = observer.PrepareOwnedHudLifecycle(owners, _ => false, out var retireStarts);
            var created = template.ProduceInactive();
            RequireCold(created);
            var plan = incoming.Prepare(LoadingGeoIncomingReferences.BuildMarkerDirectory(created));
            plan.Apply();
            UnityEngine.Object.DestroyImmediate(old);
            retireStarts();
            observer.RecaptureOwnedHudScheduler(Require, transition);
            plan.VerifyReadback(); RequireCold(created); Require();
            var afterList = fsms.GetValue(null) as IList;
            if (!ReferenceEquals(parent, created.transform.parent) || parent.gameObject.activeInHierarchy || old != null
                || !ReferenceEquals(CanonicalMarkerDirectory.Current(), created) || meshes.Any(mesh => mesh != null)
                || !ReferenceEquals(controllerBefore, controllerEvent.GetValue(input)) || !ReferenceEquals(tmp, tmpField.GetValue(null)) || tmpBefore != Tmp()
                || !ReferenceEquals(selectedBefore, selectedField.GetValue(null)) || !ReferenceEquals(raycasts, raycastField.GetValue(null))
                || !ReferenceEquals(list, afterList) || afterList.Count != retainedFsms.Length
                || afterList.Cast<object>().Where((item, index) => !ReferenceEquals(item, retainedFsms[index])).Any()
                || !rng.Equals(UnityEngine.Random.state) || !particles.SequenceEqual(LoadingGameplayOracle.CaptureParticleRandomState()))
                throw new InvalidOperationException("Marker cold Canon changed an external callback, queue, RNG, registration or retired native mesh contract.");
            return new { originalFiveObjects = 5, originalMonoOwners = 8, realColdNative = true,
                fullIncomingReadback = true, retiredOwnedMeshes = meshes.Length, controllerAndTmpQueuesPreserved = true,
                placedMarkerDataTouched = false, sourceHistoryReplayed = false, completeStateCoverage = false };
        }
    }
}
