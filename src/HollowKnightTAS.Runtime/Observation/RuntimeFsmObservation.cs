using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HollowKnightTAS.Runtime.Observation
{
    public sealed partial class RuntimeWorldObserver
    {
        private readonly Dictionary<string, PlayMakerFSM> fsmTargets = new Dictionary<string, PlayMakerFSM>();
        private long fsmSequence;

        private WorldCapture CaptureFsmCatalog(long nativeFrame, long movieFrame)
        {
            var errors = new List<object>();
            var scenes = Scenes(Existing<HeroController>(), Existing<GameManager>(), Existing<UIManager>(),
                Existing<GameCameras>(), Camera.allCameras, errors);
            UpdateEpoch(scenes);
            PruneObjects();
            foreach (var key in fsmTargets.Where(p => p.Value == null).Select(p => p.Key).ToArray()) fsmTargets.Remove(key);
            var items = new List<ObservedObject>();
            var validHandles = new HashSet<int>();
            foreach (var scene in scenes)
            {
                if (scene.IsValid() && scene.isLoaded) validHandles.Add(scene.handle);
            }
            var targetMap = new Dictionary<int, GameObject>();
            void CollectOwners<T>() where T : Component
            {
                foreach (var comp in Resources.FindObjectsOfTypeAll<T>())
                {
                    if (comp == null || comp.GetInstanceID() == 0) continue;
                    var owner = comp.gameObject;
                    if (owner == null || owner.GetInstanceID() == 0) continue;
                    var ownerScene = owner.scene;
                    if (!ownerScene.IsValid() || !ownerScene.isLoaded || !validHandles.Contains(ownerScene.handle)) continue;
                    targetMap[owner.GetInstanceID()] = owner;
                }
            }
            CollectOwners<PlayMakerFSM>();
            CollectOwners<HealthManager>();
            var orderedTargets = targetMap.Values
                .Select(go => (Go: go, Handle: go.scene.handle, Path: ObservationData.Path(go.transform), SiblingIndex: go.transform.GetSiblingIndex(), InstanceId: go.GetInstanceID()))
                .OrderBy(x => x.Handle)
                .ThenBy(x => x.Path, StringComparer.Ordinal)
                .ThenBy(x => x.SiblingIndex)
                .ThenBy(x => x.InstanceId)
                .Select(x => x.Go);

            foreach (var go in orderedTargets)
            {
                var transform = go.transform;
                var components = go.GetComponents<PlayMakerFSM>();
                bool enemy = go.GetComponent<HealthManager>() != null;
                if (components.Length == 0 && !enemy) continue;
                objects[Id(go)] = go;
                var record = BaseObject(go, enemy ? "enemy" : "fsm");
                var ancestors = new List<string>();
                for (var parent = transform.parent; parent != null; parent = parent.parent) ancestors.Add(Id(parent.gameObject));
                record["ancestors"] = ancestors;
                record["fsms"] = components.Select(component =>
                {
                    var key = fsmTargets.FirstOrDefault(p => ReferenceEquals(p.Value, component)).Key;
                    if (key == null) { key = sessionId + ":fsm:" + (++fsmSequence).ToString(CultureInfo.InvariantCulture); fsmTargets.Add(key, component); }
                    return ObservationData.Map("id", key, "name", values.Read(values.Read(component, "fsm"), "name"),
                        "instanceId", component.GetInstanceID());
                }).ToArray();
                items.Add(new ObservedObject(Id(go), enemy ? "enemy" : "fsm", ObservationData.Json(record)));
            }
            var metadata = Metadata(nativeFrame, movieFrame);
            metadata["errors"] = errors;
            metadata["view"] = "fsmCatalog";
            return new WorldCapture(ObservationData.Json(metadata), items);
        }

        public Dictionary<string, string> CaptureFsms(long nativeFrame, long movieFrame, string[] targets)
        {
            RequireMainThread();
            var result = Metadata(nativeFrame, movieFrame);
            result["fsms"] = targets.Select(target =>
            {
                var parts = target.Split('|');
                var item = ObservationData.Map("id", parts[0], "available", false);
                if (!fsmTargets.TryGetValue(parts[0], out var component) || component == null)
                { item["reason"] = "destroyedOrExpired"; return item; }
                try
                {
                    var fsm = values.Read(component, "fsm") as Fsm;
                    if (fsm == null) { item["reason"] = "notInitialized"; return item; }
                    item["available"] = true;
                    item["enabled"] = component.enabled;
                    item["activeInHierarchy"] = component.gameObject.activeInHierarchy;
                    item["initialized"] = values.Read(fsm, "initialized");
                    item["activeState"] = values.Read(fsm, "activeStateName");
                    var graph = Graph(fsm);
                    var text = ObservationData.Json(graph);
                    string version;
                    using (var sha = SHA256.Create()) version = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
                    item["version"] = version;
                    if (parts.Length < 2 || parts[1] != version) item["graph"] = graph;
                }
                catch (Exception error) { item["available"] = false; item["reason"] = error.Message; }
                return item;
            }).ToArray();
            var json = ObservationData.Json(result);
            if (Encoding.UTF8.GetByteCount(json) > 180000)
                throw new InvalidOperationException("FSM graphs exceed transport budget; select fewer FSMs.");
            return new Dictionary<string, string> { ["snapshotJson"] = json,
                ["nativeFrame"] = nativeFrame.ToString(CultureInfo.InvariantCulture), ["movieFrame"] = movieFrame.ToString(CultureInfo.InvariantCulture) };
        }

        private object Graph(Fsm fsm)
        {
            var states = values.Read(fsm, "states") as FsmState[] ?? Array.Empty<FsmState>();
            if (states.Length > 512) throw new InvalidOperationException("Graph exceeds 512 states.");
            int edgeCount = 0;
            object[] Edges(object? value) => (value as FsmTransition[] ?? Array.Empty<FsmTransition>()).Select(t =>
            {
                if (++edgeCount > 4096) throw new InvalidOperationException("Graph exceeds 4096 transitions.");
                return (object)ObservationData.Map("event", values.Read(values.Read(t, "fsmEvent"), "name"), "toState", values.Read(t, "toState"));
            }).ToArray();
            return ObservationData.Map("startState", values.Read(fsm, "startState"),
                "globalTransitions", Edges(values.Read(fsm, "globalTransitions")),
                "states", states.Select(s => ObservationData.Map("name", values.Read(s, "name"),
                    "transitions", Edges(values.Read(s, "transitions")),
                    "actionsLoaded", values.Read(s, "actions") != null,
                    "actionTypes", (values.Read(s, "actions") as FsmStateAction[])?.Select(a => a?.GetType().FullName).ToArray())).ToArray());
        }
    }
}
