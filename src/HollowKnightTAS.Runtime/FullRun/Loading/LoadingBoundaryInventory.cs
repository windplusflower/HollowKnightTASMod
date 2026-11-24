using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HutongGames.PlayMaker;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Phase 0 only: observations of the existing V6 boundary, never a payload
    // or a normalization policy. Unknown domains remain explicit decisions.
    internal static class LoadingBoundaryInventory
    {
        private static readonly JArray boundaries = new JArray();
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        internal static void Capture(int ordinal, string source, string target, long movie, long native)
        {
            if (Environment.GetEnvironmentVariable("HKTAS_LOADING_BOUNDARY_INVENTORY") != "1") return;
            if (boundaries.Count >= 64) throw new InvalidOperationException("Boundary inventory exceeded 64 transitions.");
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var objects = LoadingSerialSceneProbe.LoadedGameObjects();
            if (objects.Length > 100000) throw new InvalidOperationException("Boundary object budget exceeded.");
            var identities = new Dictionary<int, string>();
            foreach (var scene in objects.Select(go => go.scene).Distinct())
            {
                var roots = scene.GetRootGameObjects();
                for (var index = 0; index < roots.Length; index++)
                    Bind(roots[index].transform, scene.name + "/root[" + index + "]:" + roots[index].name, identities);
            }
            string Id(UnityEngine.Object value)
            {
                var component = value as Component;
                var go = value as GameObject ?? component?.gameObject;
                if (go == null || !go.scene.IsValid() || !go.scene.isLoaded)
                    return "asset:" + value.GetType().FullName + ":" + value.name;
                if (!identities.TryGetValue(go.GetInstanceID(), out var id))
                    throw new InvalidOperationException("Boundary object has no registered root: " + go.name);
                return component == null ? id : id + ":" + component.GetType().FullName + "["
                    + Array.IndexOf(go.GetComponents(component.GetType()), component) + "]";
            }
            var components = objects.SelectMany(go => go.GetComponents<Component>()).Where(c => c != null).ToArray();
            if (components.Length > 200000) throw new InvalidOperationException("Boundary component budget exceeded.");
            var behaviourIds = new Dictionary<IntPtr, string>();
            var objectIds = new Dictionary<int, string>();
            var pointerField = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var go in objects) objectIds[go.GetInstanceID()] = Id(go);
            foreach (var component in components)
            {
                objectIds[component.GetInstanceID()] = Id(component);
                if (component is MonoBehaviour)
                    behaviourIds[(IntPtr)pointerField.GetValue(component)!] = Id(component);
            }
            using (var fields = new LoadingFieldDigest(value => value is UnityEngine.Object u && u != null ? Id(u)
                    : value.GetType().Assembly == typeof(LoadingBoundaryInventory).Assembly ? "observer:" + value.GetType().FullName : null,
                type => type == typeof(MonoBehaviour) || type == typeof(UnityEngine.Object),
                observerCallback: callback => callback.Method.DeclaringType?.Assembly == typeof(LoadingBoundaryInventory).Assembly
                    || callback.Target?.GetType().Assembly == typeof(LoadingBoundaryInventory).Assembly))
            {
                var poolMembers = new Dictionary<int, string>();
                var pools = components.OfType<ObjectPool>().Select(pool =>
                {
                    var free = fields.Read(pool, "pooledObjects") as IDictionary
                        ?? throw new InvalidOperationException("ObjectPool free table is unavailable.");
                    var live = fields.Read(pool, "spawnedObjects") as IDictionary
                        ?? throw new InvalidOperationException("ObjectPool live table is unavailable.");
                    var rows = new List<object>();
                    foreach (DictionaryEntry entry in free)
                    {
                        var prefab = (GameObject)entry.Key;
                        var instances = ((IList)entry.Value).Cast<GameObject>().Where(go => go != null).ToArray();
                        foreach (var go in instances) poolMembers[go.GetInstanceID()] = prefab.name;
                        rows.Add(new { prefab = Id(prefab), prefabName = prefab.name, idle = instances.Select(Id).ToArray(),
                            live = Entries(live).Where(pair => ReferenceEquals(pair.Value, prefab))
                                .Select(pair => (GameObject)pair.Key).Where(go => go != null).Select(go =>
                                { poolMembers[go.GetInstanceID()] = prefab.name; return Id(go); }).ToArray() });
                    }
                    var definitions = fields.Read(pool, "startupPools") as Array
                        ?? throw new InvalidOperationException("ObjectPool startup definitions are unavailable.");
                    return new { id = Id(pool), tables = rows,
                        startupPools = definitions.Cast<object>().Select(definition => new {
                            prefab = fields.Read(definition, "prefab") is GameObject prefab ? Id(prefab) : null,
                            size = fields.Read(definition, "size")
                        }).ToArray(), startupDefinition = fields.Capture(
                            definitions, Id(pool) + ".startupPools").Sha256 };
                }).ToArray();
                var scheduler = new LoadingNativeSchedulerDigest();
                string schedulerHash;
                object[] schedulerRows;
                string[] schedulerUnsupported;
                try
                {
                    schedulerHash = scheduler.Capture(fields,
                        id => objectIds.TryGetValue(id, out var value) ? value : null,
                        pointer => behaviourIds.TryGetValue(pointer, out var value) ? value : null);
                    schedulerRows = scheduler.Inventory; schedulerUnsupported = scheduler.Unsupported;
                }
                finally { scheduler.Release(); }
                var staticRows = new List<object>();
                // Only initialized component types. Arbitrary assembly-wide static
                // reads could execute constructors and change the observed game.
                foreach (var type in components.OfType<MonoBehaviour>().Select(c => c.GetType()).Distinct()
                    .Where(type => type == typeof(ObjectPool) || type == typeof(iTween) || type == typeof(FSMActivator)
                        || type == typeof(PlayMakerFSM)).OrderBy(type => type.FullName, StringComparer.Ordinal))
                    foreach (var field in type.GetFields(Static).Where(field => !field.IsLiteral))
                    {
                        var digest = fields.Capture(field.GetValue(null), "static:" + type.FullName + "." + field.Name);
                        staticRows.Add(new { owner = type.FullName, field = field.Name, type = field.FieldType.FullName,
                            digest = digest.Sha256, unsupported = digest.Unsupported });
                    }
                var fsms = components.OfType<PlayMakerFSM>().Select(component =>
                {
                    var fsm = fields.Read(component, "fsm");
                    var active = fsm == null ? null : fields.Read(fsm, "activeState");
                    var actions = active == null ? null : fields.Read(active, "actions") as Array;
                    return new { id = Id(component), name = fsm == null ? null : fields.Read(fsm, "name"), component.enabled,
                        component.gameObject.activeInHierarchy, state = active == null ? null : fields.Read(active, "name"),
                        actions = actions?.Cast<object>().Where(action => action != null).Select(action => new {
                            type = action.GetType().FullName, fields = fields.Fields(action.GetType()).Select(f => f.Name).ToArray()
                        }).ToArray(), classification = Role(component.gameObject, component, poolMembers) };
                }).ToArray();
                boundaries.Add(JObject.FromObject(new { ordinal, source, target, movie, native,
                    objects = objects.OrderBy(Id, StringComparer.Ordinal).Select(go => new {
                        id = Id(go), go.activeSelf, go.activeInHierarchy,
                        pool = poolMembers.TryGetValue(go.GetInstanceID(), out var prefab) ? prefab : null,
                        components = go.GetComponents<Component>().Select(c => c == null ? new {
                            type = "missing-script", classification = "Pending"
                        } : new { type = c.GetType().FullName!, classification = Role(go, c, poolMembers) }).ToArray()
                    }).ToArray(), fsms, pools,
                    tweens = components.OfType<iTween>().Select(c => new { id = Id(c), c.enabled,
                        c.gameObject.activeInHierarchy, fields = fields.Fields(c.GetType()).Select(f => f.Name).ToArray() }).ToArray(),
                    staticTables = staticRows, nativeScheduler = schedulerRows, schedulerHash, schedulerUnsupported,
                    elapsedMilliseconds = elapsed.ElapsedMilliseconds }));
                var json = JsonConvert.SerializeObject(new { schema = 1, diagnosticOnly = true, completeStateCoverage = false,
                    nativeProfile = HollowKnightTAS.Core.Movie.NativeExecutionProfile.SelectedProfileId,
                    identityPolicy = "scene-root-ordinal-child-sibling-component-index", boundaries,
                    pendingDomains = new[] { "unregistered-component-state", "unregistered-static-state", "scheduler-items-outside-carry-list" },
                    registeredObserverExclusion = "Runtime-owned diagnostic objects and subscriptions; not a gameplay digest" });
                if (Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024)
                    throw new InvalidOperationException("Boundary inventory exceeds 16 MiB.");
                File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT")
                    ?? throw new InvalidOperationException("Boundary inventory output is absent."), "boundary-inventory.json"), json, new UTF8Encoding(false));
                Modding.Logger.Log("[HKTAS] Boundary inventory " + ordinal + ": objects=" + objects.Length
                    + " components=" + components.Length + " schedulerRows=" + schedulerRows.Length);
            }
        }

        private static void Bind(Transform node, string path, Dictionary<int, string> identities)
        {
            if (node == null || node.gameObject.GetInstanceID() == 0) return;
            if (identities.Count >= 100000) throw new InvalidOperationException("Boundary hierarchy exceeds object budget.");
            identities.Add(node.gameObject.GetInstanceID(), path);
            var childCount = node.childCount;
            for (var index = 0; index < childCount; index++)
            {
                var child = node.GetChild(index);
                if (child == null || child.gameObject.GetInstanceID() == 0) continue;
                Bind(child, path + "/" + child.name + "[" + index + "]", identities);
            }
        }

        private static IEnumerable<DictionaryEntry> Entries(IDictionary table)
        {
            foreach (DictionaryEntry entry in table) yield return entry;
        }

        private static string Role(GameObject go, Component component, Dictionary<int, string> pools)
        {
            if (pools.ContainsKey(go.GetInstanceID()) || pools.ContainsKey(go.transform.root.gameObject.GetInstanceID())
                || component is ObjectPool) return "Canon:R1";
            if (component is iTween || component.GetType().Name == "CameraShake") return "Canon:R2";
            if (component is HeroController || component is GameManager) return "D";
            if (component is PlayMakerFSM && HeroController.SilentInstance != null
                && go.transform.IsChildOf(HeroController.SilentInstance.transform)) return "D:HeroFSM-review";
            if (component is AudioSource) return "Canon:R7-audio";
            if (component is ParticleSystem || component is ParticleSystemRenderer) return "Canon:R7-particles";
            return "Pending";
        }
    }
}
