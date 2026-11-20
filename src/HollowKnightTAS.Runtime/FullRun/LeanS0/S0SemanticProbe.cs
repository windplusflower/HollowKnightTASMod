using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;
using USceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowKnightTAS.Runtime.FullRun
{
    /// <summary>
    /// Managed reflection and Unity-API-only S0 semantic state dumper for T1 carry-manifest verification.
    /// Activated strictly by HKTAS_S0_SEMANTIC_PROBE=1 without altering Resident runtime behavior.
    /// </summary>
    internal static class S0SemanticProbe
    {
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private const int MaxDepth = 5;
        private const int MaxCollectionItems = 1500;

        private sealed class RefEqualityComparer : IEqualityComparer<object>
        {
            public static readonly RefEqualityComparer Instance = new RefEqualityComparer();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private sealed class DumpContext
        {
            public readonly Dictionary<int, string> ObjectPaths = new Dictionary<int, string>();
            public readonly SortedDictionary<string, object?> Flat = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            public readonly HashSet<object> Visiting = new HashSet<object>(RefEqualityComparer.Instance);
            public string StagingSceneName = "__HKTAS_SerialBoundary_v6";
        }

        internal static void CaptureIfEnabled(
            int ordinal,
            string sourceScene,
            string targetScene,
            string entryGate,
            string? leaveDirection,
            string visualization,
            long movieFrame,
            long nativeFrame)
        {
            if (Environment.GetEnvironmentVariable("HKTAS_S0_SEMANTIC_PROBE") != "1")
                return;
            var outputDir = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT");
            if (string.IsNullOrWhiteSpace(outputDir))
                return;

            try
            {
                Directory.CreateDirectory(outputDir);
                var payload = BuildSnapshot(ordinal, sourceScene, targetScene, entryGate, leaveDirection, visualization, movieFrame, nativeFrame);
                var filePath = Path.Combine(outputDir, $"s0-semantic-boundary-{ordinal:d2}.json");
                File.WriteAllText(filePath, JsonConvert.SerializeObject(payload, Formatting.Indented));
            }
            catch (Exception ex)
            {
                var errPath = Path.Combine(outputDir, $"s0-semantic-error-{ordinal:d2}.txt");
                File.WriteAllText(errPath, ex.ToString());
            }
        }

        private static object BuildSnapshot(
            int ordinal,
            string sourceScene,
            string targetScene,
            string entryGate,
            string? leaveDirection,
            string visualization,
            long movieFrame,
            long nativeFrame)
        {
            var ctx = new DumpContext();
            var ddolScene = ResolveDdolScene();
            var loadedScenes = new List<object>();

            for (var i = 0; i < USceneManager.sceneCount; i++)
            {
                var scene = USceneManager.GetSceneAt(i);
                loadedScenes.Add(new { name = scene.name, isValid = scene.IsValid(), isLoaded = scene.isLoaded, rootCount = scene.IsValid() && scene.isLoaded ? scene.rootCount : 0 });
                if (scene.IsValid() && scene.isLoaded)
                    RegisterScenePaths(ctx, scene, "scene(" + scene.name + ")");
            }

            if (ddolScene.HasValue && ddolScene.Value.IsValid() && ddolScene.Value.isLoaded)
            {
                RegisterScenePaths(ctx, ddolScene.Value, "ddol");
            }

            var ddolRootsSummary = new List<object>();
            var knightFsmsSummary = new List<object>();
            var charmEffectsFsmsSummary = new List<object>();
            var minionObjectsFound = new List<object>();
            var instantiatedComponentTypes = new HashSet<Type>();

            if (ddolScene.HasValue && ddolScene.Value.IsValid() && ddolScene.Value.isLoaded)
            {
                var roots = ddolScene.Value.GetRootGameObjects();
                for (var i = 0; i < roots.Length; i++)
                {
                    var root = roots[i];
                    var rootPrefix = "ddol/" + FormatIndexedName(roots, i, g => g.name);
                    var topComps = root.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().FullName ?? "").ToArray();
                    ddolRootsSummary.Add(new
                    {
                        path = rootPrefix,
                        name = root.name,
                        activeSelf = root.activeSelf,
                        childCount = root.transform.childCount,
                        components = topComps
                    });

                    DumpGameObjectTree(ctx, root, rootPrefix, instantiatedComponentTypes, knightFsmsSummary, charmEffectsFsmsSummary, minionObjectsFound);
                }
            }

            // Also record any root objects sitting in non-DDOL loaded scenes (should be empty staging scene at S0).
            var stagingRoots = new List<object>();
            for (var i = 0; i < USceneManager.sceneCount; i++)
            {
                var scene = USceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                var roots = scene.GetRootGameObjects();
                foreach (var root in roots)
                {
                    var topComps = root.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().FullName ?? "").ToArray();
                    stagingRoots.Add(new { scene = scene.name, name = root.name, activeSelf = root.activeSelf, components = topComps });
                    CheckMinionName(root, "scene(" + scene.name + ")/" + root.name, minionObjectsFound);
                    foreach (var tr in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (tr.gameObject != root)
                            CheckMinionName(tr.gameObject, "scene(" + scene.name + ")/" + tr.gameObject.name, minionObjectsFound);
                    }
                }
            }

            var objectPoolSummary = CaptureObjectPoolSummary(ctx);
            DumpPlayMakerGlobals(ctx);
            DumpStaticState(ctx, instantiatedComponentTypes);

            return new
            {
                schema = "lean-s0-semantic-probe-v1",
                meta = new
                {
                    ordinal,
                    sourceScene,
                    targetScene,
                    entryGate,
                    leaveDirection = leaveDirection ?? "",
                    visualization,
                    movieFrame,
                    nativeFrame,
                    unityFrameCount = Time.frameCount,
                    unityTime = Time.time,
                    unityTimeSinceLevelLoad = Time.timeSinceLevelLoad,
                    unityFixedTime = Time.fixedTime,
                    unityUnscaledTime = Time.unscaledTime,
                    unityRandomState = FormatRandomState(UnityEngine.Random.state),
                    loadedScenes
                },
                summary = new
                {
                    ddolRootCount = ddolRootsSummary.Count,
                    ddolRoots = ddolRootsSummary,
                    stagingRoots,
                    knightFsms = knightFsmsSummary,
                    charmEffectsFsms = charmEffectsFsmsSummary,
                    minionObjectsFound,
                    objectPoolSummary
                },
                flat = ctx.Flat
            };
        }

        private static Scene? ResolveDdolScene()
        {
            var gm = GameManager.UnsafeInstance;
            if (gm != null) return gm.gameObject.scene;
            var hc = HeroController.UnsafeInstance;
            if (hc != null) return hc.gameObject.scene;
            return null;
        }

        private static void RegisterScenePaths(DumpContext ctx, Scene scene, string prefix)
        {
            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                var root = roots[i];
                var path = prefix + "/" + FormatIndexedName(roots, i, g => g.name);
                RegisterTransformPaths(ctx, root.transform, path);
            }
        }

        private static void RegisterTransformPaths(DumpContext ctx, Transform tr, string path)
        {
            var go = tr.gameObject;
            ctx.ObjectPaths[go.GetInstanceID()] = path;
            var comps = go.GetComponents<Component>();
            var typeCounts = new Dictionary<Type, int>();
            var typeTotals = new Dictionary<Type, int>();
            foreach (var c in comps)
            {
                if (c == null) continue;
                var t = c.GetType();
                typeTotals[t] = typeTotals.TryGetValue(t, out var n) ? n + 1 : 1;
            }
            foreach (var c in comps)
            {
                if (c == null) continue;
                var t = c.GetType();
                var idx = typeCounts.TryGetValue(t, out var cur) ? cur : 0;
                typeCounts[t] = idx + 1;
                var compSuffix = typeTotals[t] > 1 ? $"{t.FullName}[{idx}]" : (t.FullName ?? t.Name);
                ctx.ObjectPaths[c.GetInstanceID()] = path + ":" + compSuffix;
            }

            var childCount = tr.childCount;
            var childNames = new string[childCount];
            for (var i = 0; i < childCount; i++) childNames[i] = tr.GetChild(i).name;
            for (var i = 0; i < childCount; i++)
            {
                var child = tr.GetChild(i);
                var childSeg = FormatIndexedName(childNames, i, s => s);
                RegisterTransformPaths(ctx, child, path + "/" + childSeg);
            }
        }

        private static string FormatIndexedName<T>(IList<T> items, int index, Func<T, string> getName)
        {
            var name = getName(items[index]);
            var dupCount = 0;
            var ordinal = 0;
            for (var i = 0; i < items.Count; i++)
            {
                if (string.Equals(getName(items[i]), name, StringComparison.Ordinal))
                {
                    if (i < index) ordinal++;
                    dupCount++;
                }
            }
            return dupCount > 1 ? $"{name}#{ordinal}" : name;
        }

        private static void CheckMinionName(GameObject go, string path, List<object> minionObjectsFound)
        {
            var n = go.name;
            if (n.IndexOf("grimmchild", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("weaverling", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("hatchling", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("orbit shield", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("dreamshield", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                minionObjectsFound.Add(new
                {
                    path,
                    name = n,
                    tag = go.tag,
                    scene = go.scene.IsValid() ? go.scene.name : "<invalid>",
                    activeSelf = go.activeSelf,
                    activeInHierarchy = go.activeInHierarchy
                });
            }
        }

        private static void DumpGameObjectTree(
            DumpContext ctx,
            GameObject go,
            string path,
            HashSet<Type> instantiatedComponentTypes,
            List<object> knightFsmsSummary,
            List<object> charmEffectsFsmsSummary,
            List<object> minionObjectsFound)
        {
            CheckMinionName(go, path, minionObjectsFound);

            ctx.Flat[path + ".@activeSelf"] = go.activeSelf;
            ctx.Flat[path + ".@activeInHierarchy"] = go.activeInHierarchy;
            ctx.Flat[path + ".@layer"] = go.layer;
            ctx.Flat[path + ".@tag"] = go.tag;
            ctx.Flat[path + ".@localPosition"] = FormatVector3(go.transform.localPosition);
            ctx.Flat[path + ".@localRotation"] = FormatQuaternion(go.transform.localRotation);
            ctx.Flat[path + ".@localScale"] = FormatVector3(go.transform.localScale);

            var isKnightRoot = path == "ddol/Knight";
            var isCharmEffects = path == "ddol/Knight/Charm Effects";

            var comps = go.GetComponents<Component>();
            var typeCounts = new Dictionary<Type, int>();
            var typeTotals = new Dictionary<Type, int>();
            foreach (var c in comps)
            {
                if (c == null) continue;
                var t = c.GetType();
                typeTotals[t] = typeTotals.TryGetValue(t, out var n) ? n + 1 : 1;
            }

            foreach (var comp in comps)
            {
                if (comp == null) continue;
                var t = comp.GetType();
                var idx = typeCounts.TryGetValue(t, out var cur) ? cur : 0;
                typeCounts[t] = idx + 1;

                // Exclude our own Mod observer/probe components to avoid noisy self-observation.
                var asmName = t.Assembly.GetName().Name ?? "";
                if (asmName.StartsWith("HollowKnightTAS", StringComparison.Ordinal))
                    continue;
                if (t == typeof(Transform) || t == typeof(RectTransform))
                    continue;

                instantiatedComponentTypes.Add(t);
                var compPrefix = path + ":" + (typeTotals[t] > 1 ? $"{t.FullName}[{idx}]" : (t.FullName ?? t.Name));
                DumpComponent(ctx, comp, compPrefix, isKnightRoot, isCharmEffects, knightFsmsSummary, charmEffectsFsmsSummary);
            }

            var tr = go.transform;
            var childCount = tr.childCount;
            var childNames = new string[childCount];
            for (var i = 0; i < childCount; i++) childNames[i] = tr.GetChild(i).name;
            for (var i = 0; i < childCount; i++)
            {
                var child = tr.GetChild(i);
                var childSeg = FormatIndexedName(childNames, i, s => s);
                DumpGameObjectTree(ctx, child.gameObject, path + "/" + childSeg, instantiatedComponentTypes, knightFsmsSummary, charmEffectsFsmsSummary, minionObjectsFound);
            }
        }

        private static void DumpComponent(
            DumpContext ctx,
            Component comp,
            string compPrefix,
            bool isKnightRoot,
            bool isCharmEffects,
            List<object> knightFsmsSummary,
            List<object> charmEffectsFsmsSummary)
        {
            try
            {
                DumpComponentCore(ctx, comp, compPrefix, isKnightRoot, isCharmEffects, knightFsmsSummary, charmEffectsFsmsSummary);
            }
            catch (Exception ex)
            {
                ctx.Flat[compPrefix + ".@dumpError"] = ex.GetType().Name + ":" + ex.Message;
            }
        }

        private static void DumpComponentCore(
            DumpContext ctx,
            Component comp,
            string compPrefix,
            bool isKnightRoot,
            bool isCharmEffects,
            List<object> knightFsmsSummary,
            List<object> charmEffectsFsmsSummary)
        {
            if (comp is Behaviour beh)
                ctx.Flat[compPrefix + ".enabled"] = beh.enabled;

            if (comp is PlayMakerFSM pmFsm)
            {
                DumpPlayMakerFsm(ctx, pmFsm, compPrefix, isKnightRoot, isCharmEffects, knightFsmsSummary, charmEffectsFsmsSummary);
                return;
            }
            if (comp is tk2dSpriteAnimator anim)
            {
                var clip = anim.CurrentClip;
                ctx.Flat[compPrefix + ".currentClip"] = clip != null ? clip.name : null;
                ctx.Flat[compPrefix + ".currentFrame"] = clip != null ? (object)anim.CurrentFrame : null;
                ctx.Flat[compPrefix + ".playing"] = anim.Playing;
                ctx.Flat[compPrefix + ".clipTimeSeconds"] = clip != null ? anim.ClipTimeSeconds : 0f;
                return;
            }
            if (comp is Rigidbody2D rb)
            {
                ctx.Flat[compPrefix + ".position"] = FormatVector2(rb.position);
                ctx.Flat[compPrefix + ".velocity"] = FormatVector2(rb.velocity);
                ctx.Flat[compPrefix + ".angularVelocity"] = rb.angularVelocity;
                ctx.Flat[compPrefix + ".gravityScale"] = rb.gravityScale;
                ctx.Flat[compPrefix + ".bodyType"] = rb.bodyType.ToString();
                ctx.Flat[compPrefix + ".simulated"] = rb.simulated;
                ctx.Flat[compPrefix + ".constraints"] = rb.constraints.ToString();
                return;
            }
            if (comp is AudioSource audio)
            {
                ctx.Flat[compPrefix + ".clip"] = audio.clip != null ? audio.clip.name : null;
                ctx.Flat[compPrefix + ".isPlaying"] = audio.isPlaying;
                ctx.Flat[compPrefix + ".time"] = audio.time;
                ctx.Flat[compPrefix + ".volume"] = audio.volume;
                ctx.Flat[compPrefix + ".pitch"] = audio.pitch;
                ctx.Flat[compPrefix + ".loop"] = audio.loop;
                ctx.Flat[compPrefix + ".mute"] = audio.mute;
                return;
            }
            if (comp is ParticleSystem ps)
            {
                ctx.Flat[compPrefix + ".isPlaying"] = ps.isPlaying;
                ctx.Flat[compPrefix + ".isPaused"] = ps.isPaused;
                ctx.Flat[compPrefix + ".isStopped"] = ps.isStopped;
                ctx.Flat[compPrefix + ".particleCount"] = ps.particleCount;
                ctx.Flat[compPrefix + ".time"] = ps.time;
                return;
            }
            if (comp is Collider2D col)
            {
                ctx.Flat[compPrefix + ".isTrigger"] = col.isTrigger;
                return;
            }
            if (comp is Renderer rend)
            {
                ctx.Flat[compPrefix + ".rendererEnabled"] = rend.enabled;
                return;
            }
            if (comp is ObjectPool pool)
            {
                DumpObjectPool(ctx, pool, compPrefix);
                return;
            }

            // Skip pure Unity/TMPro built-in UI/render components except game scripts.
            var ns = comp.GetType().Namespace ?? "";
            if (ns.StartsWith("UnityEngine", StringComparison.Ordinal) || ns.StartsWith("TMPro", StringComparison.Ordinal))
                return;

            // Reflect all instance fields on game/mod MonoBehaviours up to MonoBehaviour/Behaviour/Component.
            DumpReflectedInstanceFields(ctx, comp, compPrefix, 0);
        }

        private static void DumpPlayMakerFsm(
            DumpContext ctx,
            PlayMakerFSM pmFsm,
            string compPrefix,
            bool isKnightRoot,
            bool isCharmEffects,
            List<object> knightFsmsSummary,
            List<object> charmEffectsFsmsSummary)
        {
            var fsm = pmFsm.Fsm;
            var fsmName = pmFsm.FsmName ?? "";
            var activeState = fsm != null ? fsm.ActiveStateName : "";
            var prevState = fsm != null && fsm.PreviousActiveState != null ? fsm.PreviousActiveState.Name : "";
            var initialized = fsm != null && fsm.Initialized;
            var active = fsm != null && fsm.Active;

            var prefix = $"{compPrefix}({fsmName})";
            ctx.Flat[prefix + ".activeState"] = activeState;
            ctx.Flat[prefix + ".previousState"] = prevState;
            ctx.Flat[prefix + ".initialized"] = initialized;
            ctx.Flat[prefix + ".active"] = active;

            var varSummary = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            if (fsm != null && fsm.Variables != null)
                DumpFsmVariables(ctx, fsm.Variables, prefix + ".vars", varSummary);

            var actionTimers = new Dictionary<string, object?>();
            if (fsm != null && fsm.ActiveState != null && fsm.ActiveState.Actions != null)
            {
                for (var i = 0; i < fsm.ActiveState.Actions.Length; i++)
                {
                    var act = fsm.ActiveState.Actions[i];
                    if (act == null) continue;
                    var actType = act.GetType();
                    for (var t = actType; t != null && t != typeof(FsmStateAction) && t != typeof(object); t = t.BaseType!)
                    {
                        foreach (var f in t.GetFields(InstanceFlags))
                        {
                            if (f.Name == "timer" || f.Name == "timeLeft" || f.Name == "startTime" || f.Name == "elapsed" || f.Name == "delay")
                            {
                                var val = SerializeScalarOrSimple(ctx, f.GetValue(act));
                                ctx.Flat[$"{prefix}.activeAction[{i}]({actType.Name}).{f.Name}"] = val;
                                actionTimers[$"{actType.Name}.{f.Name}"] = val;
                            }
                        }
                    }
                }
            }

            if (isKnightRoot)
            {
                knightFsmsSummary.Add(new
                {
                    fsmName,
                    enabled = pmFsm.enabled,
                    initialized,
                    active,
                    activeState,
                    previousState = prevState,
                    actionTimers
                });
            }
            else if (isCharmEffects)
            {
                charmEffectsFsmsSummary.Add(new
                {
                    fsmName,
                    enabled = pmFsm.enabled,
                    initialized,
                    active,
                    activeState,
                    previousState = prevState,
                    vars = varSummary
                });
            }
        }

        private static void DumpFsmVariables(DumpContext ctx, FsmVariables vars, string prefix, SortedDictionary<string, object?>? summaryOut)
        {
            void AddVars<T>(string kind, T[]? arr, Func<T, string> getName, Func<T, object?> getVal) where T : class
            {
                if (arr == null) return;
                foreach (var item in arr)
                {
                    if (item == null) continue;
                    var name = getName(item);
                    var val = SerializeScalarOrSimple(ctx, getVal(item));
                    ctx.Flat[$"{prefix}.{kind}:{name}"] = val;
                    if (summaryOut != null)
                        summaryOut[$"{kind}:{name}"] = val;
                }
            }

            AddVars("bool", vars.BoolVariables, v => v.Name, v => v.Value);
            AddVars("int", vars.IntVariables, v => v.Name, v => v.Value);
            AddVars("float", vars.FloatVariables, v => v.Name, v => v.Value);
            AddVars("string", vars.StringVariables, v => v.Name, v => v.Value);
            AddVars("vec2", vars.Vector2Variables, v => v.Name, v => FormatVector2(v.Value));
            AddVars("vec3", vars.Vector3Variables, v => v.Name, v => FormatVector3(v.Value));
            AddVars("color", vars.ColorVariables, v => v.Name, v => FormatColor(v.Value));
            AddVars("rect", vars.RectVariables, v => v.Name, v => FormatRect(v.Value));
            AddVars("quat", vars.QuaternionVariables, v => v.Name, v => FormatQuaternion(v.Value));
            AddVars("go", vars.GameObjectVariables, v => v.Name, v => v.Value);
            AddVars("obj", vars.ObjectVariables, v => v.Name, v => v.Value);
            AddVars("mat", vars.MaterialVariables, v => v.Name, v => v.Value);
            AddVars("tex", vars.TextureVariables, v => v.Name, v => v.Value);
            AddVars("enum", vars.EnumVariables, v => v.Name, v => v.Value?.ToString());
            if (vars.ArrayVariables != null)
            {
                foreach (var arrVar in vars.ArrayVariables)
                {
                    if (arrVar == null) continue;
                    var vals = arrVar.Values;
                    ctx.Flat[$"{prefix}.array:{arrVar.Name}.length"] = vals != null ? vals.Length : 0;
                    if (summaryOut != null) summaryOut[$"array:{arrVar.Name}.length"] = vals != null ? vals.Length : 0;
                }
            }
        }

        private static void DumpObjectPool(DumpContext ctx, ObjectPool pool, string prefix)
        {
            var pooledDict = typeof(ObjectPool).GetField("pooledObjects", InstanceFlags)?.GetValue(pool) as IDictionary;
            var spawnedDict = typeof(ObjectPool).GetField("spawnedObjects", InstanceFlags)?.GetValue(pool) as IDictionary;

            if (pooledDict != null)
            {
                foreach (DictionaryEntry entry in pooledDict)
                {
                    var prefab = entry.Key as GameObject;
                    var pname = prefab != null ? prefab.name : "<null-prefab>";
                    var list = entry.Value as IList;
                    var total = list != null ? list.Count : 0;
                    var nullCount = 0;
                    var ddolCount = 0;
                    if (list != null)
                    {
                        foreach (var item in list)
                        {
                            var go = item as GameObject;
                            if (go == null) { nullCount++; continue; }
                            if (go.scene.IsValid() && go.scene.name == "DontDestroyOnLoad") ddolCount++;
                        }
                    }
                    ctx.Flat[$"{prefix}.pooled[{pname}].count"] = total;
                    ctx.Flat[$"{prefix}.pooled[{pname}].ddolCount"] = ddolCount;
                    ctx.Flat[$"{prefix}.pooled[{pname}].nullOrDestroyedCount"] = nullCount;
                }
            }

            if (spawnedDict != null)
            {
                var byPrefab = new SortedDictionary<string, int>(StringComparer.Ordinal);
                var destroyedCount = 0;
                foreach (DictionaryEntry entry in spawnedDict)
                {
                    var inst = entry.Key as GameObject;
                    var prefab = entry.Value as GameObject;
                    var pname = prefab != null ? prefab.name : "<null-prefab>";
                    if (inst == null) destroyedCount++;
                    byPrefab[pname] = byPrefab.TryGetValue(pname, out var c) ? c + 1 : 1;
                }
                ctx.Flat[$"{prefix}.spawned.totalCount"] = spawnedDict.Count;
                ctx.Flat[$"{prefix}.spawned.nullOrDestroyedCount"] = destroyedCount;
                foreach (var kv in byPrefab)
                    ctx.Flat[$"{prefix}.spawnedByPrefab[{kv.Key}]"] = kv.Value;
            }
        }

        private static object? CaptureObjectPoolSummary(DumpContext ctx)
        {
            var pool = ObjectPool.instance;
            if (pool == null) return null;
            var pooledDict = typeof(ObjectPool).GetField("pooledObjects", InstanceFlags)?.GetValue(pool) as IDictionary;
            var spawnedDict = typeof(ObjectPool).GetField("spawnedObjects", InstanceFlags)?.GetValue(pool) as IDictionary;
            var rows = new List<object>();

            var spawnedByPrefab = new Dictionary<string, (int total, int destroyed, int ddol)>(StringComparer.Ordinal);
            if (spawnedDict != null)
            {
                foreach (DictionaryEntry entry in spawnedDict)
                {
                    var inst = entry.Key as GameObject;
                    var prefab = entry.Value as GameObject;
                    var pname = prefab != null ? prefab.name : "<null-prefab>";
                    spawnedByPrefab.TryGetValue(pname, out var cur);
                    cur.total++;
                    if (inst == null) cur.destroyed++;
                    else if (inst.scene.IsValid() && inst.scene.name == "DontDestroyOnLoad") cur.ddol++;
                    spawnedByPrefab[pname] = cur;
                }
            }

            if (pooledDict != null)
            {
                foreach (DictionaryEntry entry in pooledDict)
                {
                    var prefab = entry.Key as GameObject;
                    var pname = prefab != null ? prefab.name : "<null-prefab>";
                    var list = entry.Value as IList;
                    var idleCount = list != null ? list.Count : 0;
                    spawnedByPrefab.TryGetValue(pname, out var sp);
                    rows.Add(new
                    {
                        prefabName = pname,
                        idleCount,
                        spawnedCount = sp.total,
                        spawnedDestroyedCount = sp.destroyed,
                        spawnedDdolCount = sp.ddol
                    });
                }
            }

            return new
            {
                pooledPrefabCount = pooledDict != null ? pooledDict.Count : 0,
                spawnedTotalCount = spawnedDict != null ? spawnedDict.Count : 0,
                entries = rows
            };
        }

        private static void DumpPlayMakerGlobals(DumpContext ctx)
        {
            var globals = PlayMakerGlobals.Instance;
            if (globals == null || globals.Variables == null)
            {
                ctx.Flat["globalVars.@status"] = "null";
                return;
            }
            DumpFsmVariables(ctx, globals.Variables, "globalVars", null);
            ctx.Flat["globalVars.@eventsCount"] = globals.Events != null ? globals.Events.Count : 0;
        }

        private static void DumpStaticState(DumpContext ctx, HashSet<Type> instantiatedComponentTypes)
        {
            var types = new HashSet<Type>
            {
                typeof(GameManager),
                typeof(HeroController),
                typeof(GameCameras),
                typeof(InputHandler),
                typeof(StaticVariableList),
                typeof(BossSequenceController),
                typeof(BossSceneController),
                typeof(BossChallengeUI),
                typeof(BossStatue),
                typeof(ObjectPool),
                typeof(PlayMakerFSM),
                typeof(PlayMakerGlobals),
                typeof(FsmVariables),
                typeof(Fsm),
                typeof(FsmEvent),
                typeof(FsmExecutionStack),
                typeof(PlayerData),
                typeof(SceneData),
                typeof(iTween)
            };

            foreach (var t in instantiatedComponentTypes)
            {
                var asmName = t.Assembly.GetName().Name ?? "";
                if (asmName == "Assembly-CSharp" || asmName == "Assembly-CSharp-firstpass")
                    types.Add(t);
            }

            foreach (var t in types.OrderBy(x => x.FullName, StringComparer.Ordinal))
            {
                if (t.ContainsGenericParameters) continue;
                foreach (var field in t.GetFields(StaticFlags).OrderBy(f => f.Name, StringComparer.Ordinal))
                {
                    if (field.IsLiteral) continue;
                    if (field.IsDefined(typeof(ThreadStaticAttribute), false))
                    {
                        ctx.Flat[$"static/{t.FullName}.{field.Name}"] = "<thread-static>";
                        continue;
                    }
                    object? val;
                    try { val = field.GetValue(null); }
                    catch (Exception ex)
                    {
                        ctx.Flat[$"static/{t.FullName}.{field.Name}"] = "<error:" + ex.GetType().Name + ">";
                        continue;
                    }

                    if (t == typeof(StaticVariableList) && field.Name == "variables" && val is IDictionary dict)
                    {
                        ctx.Flat["static/StaticVariableList.variables.@count"] = dict.Count;
                        foreach (DictionaryEntry entry in dict)
                        {
                            var k = entry.Key?.ToString() ?? "<null>";
                            DumpAnyValue(ctx, entry.Value, $"static/StaticVariableList.variables[{k}]", 1);
                        }
                        continue;
                    }

                    DumpAnyValue(ctx, val, $"static/{t.FullName}.{field.Name}", 1);
                }
            }
        }

        private static void DumpReflectedInstanceFields(DumpContext ctx, object instance, string prefix, int depth)
        {
            if (depth > MaxDepth)
            {
                ctx.Flat[prefix] = "<max-depth:" + instance.GetType().Name + ">";
                return;
            }
            if (!ctx.Visiting.Add(instance))
            {
                ctx.Flat[prefix] = "<cycle:" + instance.GetType().Name + ">";
                return;
            }
            try
            {
                var type = instance.GetType();
                // Trim bulky read-only achievement/input-device definition tables if present.
                var isAchievementHandler = type == typeof(AchievementHandler);
                var stack = new Stack<Type>();
                for (var cur = type; cur != null && cur != typeof(object) && cur != typeof(ValueType)
                    && cur != typeof(MonoBehaviour) && cur != typeof(Behaviour)
                    && cur != typeof(Component) && cur != typeof(UnityEngine.Object); cur = cur.BaseType!)
                {
                    stack.Push(cur);
                }

                while (stack.Count > 0)
                {
                    var layer = stack.Pop();
                    foreach (var field in layer.GetFields(InstanceFlags).OrderBy(f => f.Name, StringComparer.Ordinal))
                    {
                        if (field.IsLiteral || field.IsStatic) continue;
                        if (isAchievementHandler && (field.Name == "achievementWhiteLists" || field.Name == "<achievementsList>k__BackingField" || field.Name == "achievementsListPrefab"))
                        {
                            ctx.Flat[prefix + "." + field.Name] = "<omitted-static-table>";
                            continue;
                        }
                        if (field.FieldType == typeof(IntPtr) || field.FieldType == typeof(UIntPtr))
                            continue;

                        object? val;
                        try { val = field.GetValue(instance); }
                        catch (Exception ex)
                        {
                            ctx.Flat[prefix + "." + field.Name] = "<error:" + ex.GetType().Name + ">";
                            continue;
                        }
                        DumpAnyValue(ctx, val, prefix + "." + field.Name, depth + 1);
                    }
                }
            }
            finally
            {
                ctx.Visiting.Remove(instance);
            }
        }

        private static void DumpAnyValue(DumpContext ctx, object? val, string path, int depth)
        {
            var scalar = TryConvertSimple(ctx, val, out var handled);
            if (handled)
            {
                ctx.Flat[path] = scalar;
                return;
            }

            if (val is Delegate del)
            {
                var list = del.GetInvocationList();
                ctx.Flat[path] = "delegate:[" + string.Join(",", list.Select(d =>
                    (d.Target != null ? d.Target.GetType().FullName : "static") + "." + d.Method.Name)) + "]";
                return;
            }

            if (val is IEnumerator && !(val is UnityEngine.Object))
            {
                ctx.Flat[path + ".@enumeratorType"] = val.GetType().FullName;
                if (depth <= MaxDepth)
                    DumpReflectedInstanceFields(ctx, val, path, depth);
                return;
            }

            if (depth > MaxDepth)
            {
                ctx.Flat[path] = "<depth-limit:" + val!.GetType().Name + ">";
                return;
            }

            if (val is IDictionary dict)
            {
                ctx.Flat[path + ".@count"] = dict.Count;
                if (dict.Count == 0) return;
                if (!ctx.Visiting.Add(dict)) { ctx.Flat[path] = "<cycle:dict>"; return; }
                try
                {
                    var items = new List<(string key, object? val)>();
                    var n = 0;
                    foreach (DictionaryEntry entry in dict)
                    {
                        if (n++ >= MaxCollectionItems)
                        {
                            ctx.Flat[path + ".@truncated"] = true;
                            break;
                        }
                        items.Add((entry.Key?.ToString() ?? "<null>", entry.Value));
                    }
                    foreach (var pair in items.OrderBy(p => p.key, StringComparer.Ordinal))
                        DumpAnyValue(ctx, pair.val, $"{path}[{pair.key}]", depth + 1);
                }
                finally { ctx.Visiting.Remove(dict); }
                return;
            }

            if (val is IEnumerable seq)
            {
                if (!ctx.Visiting.Add(seq)) { ctx.Flat[path] = "<cycle:seq>"; return; }
                try
                {
                    var idx = 0;
                    foreach (var item in seq)
                    {
                        if (idx >= MaxCollectionItems)
                        {
                            ctx.Flat[path + ".@truncated"] = true;
                            break;
                        }
                        DumpAnyValue(ctx, item, $"{path}[{idx}]", depth + 1);
                        idx++;
                    }
                    ctx.Flat[path + ".@count"] = idx;
                }
                finally { ctx.Visiting.Remove(seq); }
                return;
            }

            var type = val!.GetType();
            var ns = type.Namespace ?? "";
            if (ns.StartsWith("System.", StringComparison.Ordinal) && !type.IsValueType)
            {
                ctx.Flat[path] = val.ToString();
                return;
            }
            if (ns.StartsWith("InControl", StringComparison.Ordinal))
            {
                // Keep top-level InControl state readable without diving deep into platform profiles.
                if (depth >= 2)
                {
                    ctx.Flat[path] = type.Name + ":" + val;
                    return;
                }
            }

            DumpReflectedInstanceFields(ctx, val, path, depth);
        }

        private static object? SerializeScalarOrSimple(DumpContext ctx, object? val)
        {
            var res = TryConvertSimple(ctx, val, out var handled);
            return handled ? res : val?.ToString();
        }

        private static object? TryConvertSimple(DumpContext ctx, object? val, out bool handled)
        {
            handled = true;
            if (ReferenceEquals(val, null)) return null;

            switch (val)
            {
                case bool b: return b;
                case byte by: return (long)by;
                case sbyte sb: return (long)sb;
                case short s: return (long)s;
                case ushort us: return (long)us;
                case int i: return (long)i;
                case uint ui: return (long)ui;
                case long l: return l;
                case ulong ul: return ul.ToString(CultureInfo.InvariantCulture);
                case float f:
                    if (float.IsNaN(f) || float.IsInfinity(f)) return f.ToString(CultureInfo.InvariantCulture);
                    return f.ToString("R", CultureInfo.InvariantCulture);
                case double d:
                    if (double.IsNaN(d) || double.IsInfinity(d)) return d.ToString(CultureInfo.InvariantCulture);
                    return d.ToString("R", CultureInfo.InvariantCulture);
                case decimal dec: return dec.ToString(CultureInfo.InvariantCulture);
                case char ch: return ch.ToString();
                case string str: return str;
                case Enum en: return en.ToString();
                case Type ty: return "typeof:" + ty.FullName;
                case Vector2 v2: return FormatVector2(v2);
                case Vector3 v3: return FormatVector3(v3);
                case Vector4 v4: return $"vec4({v4.x:R},{v4.y:R},{v4.z:R},{v4.w:R})";
                case Quaternion q: return FormatQuaternion(q);
                case Color c: return FormatColor(c);
                case Color32 c32: return $"rgba32({c32.r},{c32.g},{c32.b},{c32.a})";
                case Rect r: return FormatRect(r);
                case Bounds bd: return $"bounds(c={FormatVector3(bd.center)},s={FormatVector3(bd.size)})";
                case LayerMask lm: return "layerMask:" + lm.value;
                case Coroutine _: return "unity:Coroutine";
                case AsyncOperation ao: return $"unity:AsyncOperation(done={ao.isDone},progress={ao.progress:R})";
                case WaitForSeconds _: return "unity:WaitForSeconds";
                case WaitForSecondsRealtime _: return "unity:WaitForSecondsRealtime";
                case WaitForFixedUpdate _: return "unity:WaitForFixedUpdate";
            }

            if (val is UnityEngine.Object uo)
            {
                if (uo == null) return "destroyed:" + val.GetType().FullName;
                var id = uo.GetInstanceID();
                if (ctx.ObjectPaths.TryGetValue(id, out var knownPath)) return knownPath;
                if (uo is GameObject go)
                {
                    if (go.scene.IsValid()) return $"sceneObj({go.scene.name}):{go.name}";
                    return $"asset:GameObject:{go.name}";
                }
                if (uo is Component comp)
                {
                    var cgo = comp.gameObject;
                    if (cgo != null && cgo.scene.IsValid())
                        return $"sceneComp({cgo.scene.name}):{cgo.name}:{comp.GetType().FullName}";
                    return $"assetComp:{comp.GetType().FullName}:{(cgo != null ? cgo.name : uo.name)}";
                }
                return $"asset:{uo.GetType().FullName}:{uo.name}";
            }

            handled = false;
            return null;
        }

        private static object FormatRandomState(UnityEngine.Random.State state)
        {
            var dict = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            object boxed = state;
            foreach (var f in typeof(UnityEngine.Random.State).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                dict[f.Name] = f.GetValue(boxed);
            return dict;
        }

        private static string FormatVector2(Vector2 v) => $"({v.x.ToString("R", CultureInfo.InvariantCulture)},{v.y.ToString("R", CultureInfo.InvariantCulture)})";
        private static string FormatVector3(Vector3 v) => $"({v.x.ToString("R", CultureInfo.InvariantCulture)},{v.y.ToString("R", CultureInfo.InvariantCulture)},{v.z.ToString("R", CultureInfo.InvariantCulture)})";
        private static string FormatQuaternion(Quaternion q) => $"({q.x.ToString("R", CultureInfo.InvariantCulture)},{q.y.ToString("R", CultureInfo.InvariantCulture)},{q.z.ToString("R", CultureInfo.InvariantCulture)},{q.w.ToString("R", CultureInfo.InvariantCulture)})";
        private static string FormatColor(Color c) => $"rgba({c.r.ToString("R", CultureInfo.InvariantCulture)},{c.g.ToString("R", CultureInfo.InvariantCulture)},{c.b.ToString("R", CultureInfo.InvariantCulture)},{c.a.ToString("R", CultureInfo.InvariantCulture)})";
        private static string FormatRect(Rect r) => $"rect({r.x.ToString("R", CultureInfo.InvariantCulture)},{r.y.ToString("R", CultureInfo.InvariantCulture)},{r.width.ToString("R", CultureInfo.InvariantCulture)},{r.height.ToString("R", CultureInfo.InvariantCulture)})";
    }
}
