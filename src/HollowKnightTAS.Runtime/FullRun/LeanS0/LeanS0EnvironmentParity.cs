using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using InControl;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class LeanS0EnvironmentParity
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly HashSet<Type> HeroInactiveRandomTypes = new HashSet<Type>
        {
            typeof(RandomRotationRange),
            typeof(RandomRotation),
            typeof(RandomScale),
            typeof(AudioSourcePitchRandomizer),
            typeof(RandomAudioStart)
        };

        private static readonly FieldInfo? PitchRandomizerAudioField = typeof(AudioSourcePitchRandomizer).GetField("audioSource", AnyInstance);
        private static readonly FieldInfo PoolObjectsField = typeof(ObjectPool).GetField("pooledObjects", AnyInstance)
            ?? throw new MissingFieldException("ObjectPool.pooledObjects");
        private static readonly FieldInfo SpawnedObjectsField = typeof(ObjectPool).GetField("spawnedObjects", AnyInstance)
            ?? throw new MissingFieldException("ObjectPool.spawnedObjects");
        private static readonly FieldInfo CachedPointerField = typeof(UnityEngine.Object).GetField("m_CachedPtr", AnyInstance)
            ?? throw new MissingFieldException("UnityEngine.Object.m_CachedPtr");

        private static FieldInfo[] GetScalarFields(Type type)
        {
            return CheckpointScalarFields.Declared(type)
                .Where(f => (f.FieldType.IsPrimitive && f.FieldType != typeof(IntPtr) && f.FieldType != typeof(UIntPtr))
                    || f.FieldType.IsEnum || f.FieldType == typeof(string) || f.FieldType == typeof(Vector3))
                .ToArray();
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr read);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr written);

        private static bool nativeLifecycleUnsupportedWarned;

        private static void WarnNativeLifecycleUnsupportedOnce(string detail)
        {
            if (!nativeLifecycleUnsupportedWarned)
            {
                nativeLifecycleUnsupportedWarned = true;
                Modding.Logger.LogWarn("[HKTAS] " + detail);
            }
        }

        private static bool TryReadMonoLifecycle(MonoBehaviour component, out byte[] lifecycle)
        {
            lifecycle = new byte[] { 0, 0 };
            if (!LoadingEngineClockProbe.IsPinnedEngineMatched)
            {
                WarnNativeLifecycleUnsupportedOnce("Skipping MonoBehaviour native lifecycle read on unmatched UnityPlayer.dll.");
                return false;
            }
            IntPtr pointer = (IntPtr)CachedPointerField.GetValue(component)!;
            var bytes = new byte[5];
            if (pointer == IntPtr.Zero
                || !ReadProcessMemory(GetCurrentProcess(), IntPtr.Add(pointer, 292), bytes, new UIntPtr(5), out var read)
                || read.ToUInt64() != 5 || bytes.Any(v => v > 1) || bytes[1] > bytes[0] || bytes[3] != 0)
            {
                WarnNativeLifecycleUnsupportedOnce("LeanS0 MonoBehaviour native lifecycle layout differs for " + component.GetType().FullName + "; using fallback observation.");
                return false;
            }
            lifecycle = new[] { bytes[0], bytes[1] };
            return true;
        }

        private static void WriteMonoLifecycle(MonoBehaviour component, byte[] lifecycle)
        {
            if (!LoadingEngineClockProbe.IsPinnedEngineMatched)
            {
                WarnNativeLifecycleUnsupportedOnce("Skipping MonoBehaviour native lifecycle write on unmatched UnityPlayer.dll.");
                return;
            }
            if (lifecycle.Length != 2 || lifecycle.Any(v => v > 1) || lifecycle[1] > lifecycle[0] || component.isActiveAndEnabled)
            {
                WarnNativeLifecycleUnsupportedOnce("LeanS0 MonoBehaviour lifecycle write preconditions differ for " + component.GetType().FullName + "; skipping write.");
                return;
            }
            IntPtr pointer = (IntPtr)CachedPointerField.GetValue(component)!;
            if (pointer == IntPtr.Zero
                || !WriteProcessMemory(GetCurrentProcess(), IntPtr.Add(pointer, 292), lifecycle, new UIntPtr(2), out var written)
                || written.ToUInt64() != 2
                || !TryReadMonoLifecycle(component, out var verify) || !verify.SequenceEqual(lifecycle))
            {
                WarnNativeLifecycleUnsupportedOnce("LeanS0 MonoBehaviour lifecycle write verification differed for " + component.GetType().FullName + ".");
            }
        }

        /// <summary>
        /// Captures native Awake/Start lifecycle bits and post-initialization fields for inactive
        /// HeroController child randomizers (excluding Hit Crack, which D4 handles directly).
        /// </summary>
        internal static LeanS0HeroInactiveComponentState[] CaptureHeroInactiveLifecycles(HeroController hero)
        {
            if (hero == null)
            {
                return Array.Empty<LeanS0HeroInactiveComponentState>();
            }

            LoadingEngineClockProbe.RequirePinnedEngine();
            if (!LoadingEngineClockProbe.IsPinnedEngineMatched)
            {
                WarnNativeLifecycleUnsupportedOnce("Skipping CaptureHeroInactiveLifecycles on unmatched UnityPlayer.dll.");
                return Array.Empty<LeanS0HeroInactiveComponentState>();
            }
            GameObject? hitCrack = hero.damageEffectFSM?.FsmVariables.FindFsmGameObject("Hit Crack")?.Value;
            Transform root = hero.transform;
            MonoBehaviour[] behaviours = hero.GetComponentsInChildren<MonoBehaviour>(true);
            var matchCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var captured = new List<LeanS0HeroInactiveComponentState>();

            foreach (MonoBehaviour mb in behaviours)
            {
                if (mb == null || (IntPtr)CachedPointerField.GetValue(mb)! == IntPtr.Zero)
                {
                    continue;
                }

                Type compType = mb.GetType();
                if (!HeroInactiveRandomTypes.Contains(compType))
                {
                    continue;
                }

                GameObject go = mb.gameObject;
                if (go == null || go.activeInHierarchy || (hitCrack != null && ReferenceEquals(go, hitCrack)))
                {
                    continue;
                }

                string relPath = GetRelativePath(root, mb.transform);
                string typeName = compType.FullName ?? compType.Name;
                string key = relPath + "|" + typeName;
                matchCounts.TryGetValue(key, out int matchIndex);
                matchCounts[key] = matchIndex + 1;

                if (!TryReadMonoLifecycle(mb, out byte[] lifecycle) || (lifecycle[0] == 0 && lifecycle[1] == 0))
                {
                    continue;
                }

                AudioSource? audio = mb.GetComponent<AudioSource>();
                captured.Add(new LeanS0HeroInactiveComponentState
                {
                    RelativePath = relPath,
                    TypeFullName = typeName,
                    MatchIndex = matchIndex,
                    Enabled = mb.enabled,
                    Lifecycle = lifecycle,
                    Fields = CheckpointScalarFields.Capture(mb, GetScalarFields(compType)),
                    LocalRotation = new[] { mb.transform.localRotation.x, mb.transform.localRotation.y, mb.transform.localRotation.z, mb.transform.localRotation.w },
                    LocalScale = new[] { mb.transform.localScale.x, mb.transform.localScale.y, mb.transform.localScale.z },
                    HasAudioPitch = audio != null,
                    AudioPitch = audio != null ? audio.pitch : 1f
                });
            }

            return captured.ToArray();
        }

        /// <summary>
        /// Applies captured lifecycle bits and post-initialization fields to matching inactive
        /// HeroController child randomizers whose Awake/Start have not yet run.
        /// </summary>
        internal static void ApplyHeroInactiveLifecycles(HeroController hero, LeanS0HeroInactiveComponentState[]? states)
        {
            if (hero == null || states == null)
            {
                return;
            }

            LoadingEngineClockProbe.RequirePinnedEngine();
            bool nativeMatched = LoadingEngineClockProbe.IsPinnedEngineMatched;
            if (!nativeMatched)
            {
                WarnNativeLifecycleUnsupportedOnce("Skipping native lifecycle writes in ApplyHeroInactiveLifecycles on unmatched UnityPlayer.dll.");
            }
            GameObject? hitCrack = hero.damageEffectFSM?.FsmVariables.FindFsmGameObject("Hit Crack")?.Value;
            Transform root = hero.transform;
            var stateByKey = new Dictionary<string, LeanS0HeroInactiveComponentState>(states.Length, StringComparer.Ordinal);
            foreach (LeanS0HeroInactiveComponentState dto in states)
            {
                stateByKey[$"{dto.RelativePath}|{dto.TypeFullName}|{dto.MatchIndex}"] = dto;
            }

            MonoBehaviour[] behaviours = hero.GetComponentsInChildren<MonoBehaviour>(true);
            var matchCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            int appliedCount = 0;
            int resetCount = 0;

            foreach (MonoBehaviour mb in behaviours)
            {
                if (mb == null || (IntPtr)CachedPointerField.GetValue(mb)! == IntPtr.Zero)
                {
                    continue;
                }

                Type compType = mb.GetType();
                if (!HeroInactiveRandomTypes.Contains(compType))
                {
                    continue;
                }

                GameObject go = mb.gameObject;
                if (go == null || go.activeInHierarchy || (hitCrack != null && ReferenceEquals(go, hitCrack)))
                {
                    continue;
                }

                string relPath = GetRelativePath(root, mb.transform);
                string typeName = compType.FullName ?? compType.Name;
                string matchKey = relPath + "|" + typeName;
                matchCounts.TryGetValue(matchKey, out int matchIndex);
                matchCounts[matchKey] = matchIndex + 1;

                if (mb.isActiveAndEnabled)
                {
                    continue;
                }

                byte[] current = new byte[] { 0, 0 };
                bool hasCurrent = nativeMatched && TryReadMonoLifecycle(mb, out current);
                string fullKey = $"{matchKey}|{matchIndex}";
                if (stateByKey.TryGetValue(fullKey, out LeanS0HeroInactiveComponentState? dto))
                {
                    CheckpointScalarFields.PrepareApply(mb, GetScalarFields(compType), dto.Fields)();
                    mb.transform.localRotation = new Quaternion(dto.LocalRotation[0], dto.LocalRotation[1], dto.LocalRotation[2], dto.LocalRotation[3]);
                    mb.transform.localScale = new Vector3(dto.LocalScale[0], dto.LocalScale[1], dto.LocalScale[2]);
                    if (dto.HasAudioPitch)
                    {
                        AudioSource? audio = mb.GetComponent<AudioSource>();
                        if (audio != null)
                        {
                            audio.pitch = dto.AudioPitch;
                            if (mb is AudioSourcePitchRandomizer)
                            {
                                PitchRandomizerAudioField?.SetValue(mb, audio);
                            }
                            else if (mb is RandomAudioStart randomAudio && randomAudio.audioSource == null)
                            {
                                randomAudio.audioSource = audio;
                            }
                        }
                    }

                    if (hasCurrent && (current[0] != dto.Lifecycle[0] || current[1] != dto.Lifecycle[1]))
                    {
                        WriteMonoLifecycle(mb, dto.Lifecycle);
                    }
                    mb.enabled = dto.Enabled;
                    appliedCount++;
                }
                else if (hasCurrent && (current[0] != 0 || current[1] != 0))
                {
                    WriteMonoLifecycle(mb, new byte[] { 0, 0 });
                    resetCount++;
                }
            }

            if (appliedCount > 0 || resetCount > 0)
            {
                Modding.Logger.Log($"[HKTAS] Synced inactive Hero randomizer lifecycles for S0 parity: applied={appliedCount}, reset={resetCount}");
            }
        }

        internal static LeanS0ObjectPoolWarmupState CaptureObjectPoolWarmup(HeroController hero)
        {
            ObjectPool? pool = ObjectPool.instance;
            if (pool == null)
            {
                return new LeanS0ObjectPoolWarmupState();
            }

            var pooledObjects = PoolObjectsField.GetValue(pool) as Dictionary<GameObject, List<GameObject>>;
            if (pooledObjects == null || pooledObjects.Count == 0)
            {
                return new LeanS0ObjectPoolWarmupState();
            }

            LoadingEngineClockProbe.RequirePinnedEngine();
            BuildPrefabTables(pool, hero, out Dictionary<GameObject, string> prefabToKey, out _);
            var entries = new List<LeanS0PooledPrefabState>();

            foreach (KeyValuePair<GameObject, List<GameObject>> pair in pooledObjects)
            {
                if (pair.Key == null || pair.Value == null || pair.Value.Count == 0)
                {
                    continue;
                }
                if (!prefabToKey.TryGetValue(pair.Key, out string? key))
                {
                    continue;
                }

                bool[] awakened = pair.Value.Select(IsObjectAwakened).ToArray();
                if (!awakened.Any(b => b) && key.StartsWith("startup:", StringComparison.Ordinal))
                {
                    continue;
                }

                entries.Add(new LeanS0PooledPrefabState
                {
                    PrefabKey = key,
                    TotalCount = pair.Value.Count,
                    AwakenedSlots = awakened
                });
            }

            return new LeanS0ObjectPoolWarmupState
            {
                Entries = entries.OrderBy(e => e.PrefabKey, StringComparer.Ordinal).ToArray()
            };
        }

        internal static void ApplyObjectPoolWarmup(HeroController hero, LeanS0ObjectPoolWarmupState? state, bool inProcessRestore = false)
        {
            ObjectPool? pool = ObjectPool.instance;
            if (pool == null || state?.Entries == null)
            {
                return;
            }

            var pooledObjects = PoolObjectsField.GetValue(pool) as Dictionary<GameObject, List<GameObject>>;
            if (pooledObjects == null)
            {
                return;
            }

            LoadingEngineClockProbe.RequirePinnedEngine();
            BuildPrefabTables(pool, hero, out Dictionary<GameObject, string> prefabToKey, out Dictionary<string, GameObject> keyToPrefab);
            var entryByKey = new Dictionary<string, LeanS0PooledPrefabState>(state.Entries.Length, StringComparer.Ordinal);
            foreach (LeanS0PooledPrefabState entry in state.Entries)
            {
                entryByKey[entry.PrefabKey] = entry;
            }

            UnityEngine.Random.State savedRng = UnityEngine.Random.state;
            int warmedCount = 0;
            int resetCount = 0;

            try
            {
                foreach (LeanS0PooledPrefabState entry in state.Entries)
                {
                    if (!keyToPrefab.TryGetValue(entry.PrefabKey, out GameObject? prefab) || prefab == null)
                    {
                        continue;
                    }

                    if (!pooledObjects.TryGetValue(prefab, out List<GameObject>? list))
                    {
                        if (entry.TotalCount > 0)
                        {
                            ObjectPool.CreatePool(prefab, entry.TotalCount);
                        }
                        if (!pooledObjects.TryGetValue(prefab, out list))
                        {
                            continue;
                        }
                    }
                    else if (list.Count < entry.TotalCount)
                    {
                        ObjectPool.CreatePool(prefab, entry.TotalCount - list.Count);
                    }

                    bool isActiveRecycler = prefab.GetComponent<ActiveRecycler>() != null;
                    for (int i = 0; i < entry.AwakenedSlots.Length && i < list.Count; i++)
                    {
                        if (!entry.AwakenedSlots[i])
                        {
                            continue;
                        }

                        GameObject go = list[i];
                        if (go == null || isActiveRecycler || go.activeSelf || IsObjectAwakened(go))
                        {
                            continue;
                        }

                        go.SetActive(true);
                        SilenceAndClearEffects(go);
                        go.SetActive(false);
                        warmedCount++;
                    }
                }

                var startupSizes = new Dictionary<GameObject, int>();
                if (pool.startupPools != null)
                {
                    foreach (ObjectPool.StartupPool sp in pool.startupPools)
                    {
                        if (sp?.prefab != null)
                        {
                            startupSizes[sp.prefab] = startupSizes.TryGetValue(sp.prefab, out int prev)
                                ? Math.Max(prev, sp.size)
                                : sp.size;
                        }
                    }
                }

                // Reverse/cross-branch parity: reset or replace inactive slots that were awakened later
                // than the target S0 packet so pooled randomizers execute identically on respawn.
                foreach (KeyValuePair<GameObject, List<GameObject>> pair in pooledObjects.ToList())
                {
                    GameObject prefab = pair.Key;
                    List<GameObject> list = pair.Value;
                    if (prefab == null || list == null)
                    {
                        continue;
                    }

                    bool hasActiveRecycler = prefab.GetComponent<ActiveRecycler>() != null;
                    if (!prefabToKey.TryGetValue(prefab, out string? key))
                    {
                        if (inProcessRestore && !startupSizes.ContainsKey(prefab))
                        {
                            for (int i = list.Count - 1; i >= 0; i--)
                            {
                                GameObject extra = list[i];
                                if (extra != null)
                                {
                                    SilenceAndClearEffects(extra);
                                    UnityEngine.Object.DestroyImmediate(extra);
                                    resetCount++;
                                }
                            }
                            pooledObjects.Remove(prefab);
                        }
                        continue;
                    }

                    entryByKey.TryGetValue(key, out LeanS0PooledPrefabState? entry);
                    int targetCount = entry != null
                        ? entry.TotalCount
                        : (inProcessRestore && startupSizes.TryGetValue(prefab, out int startupSize) ? startupSize : -1);

                    if (targetCount >= 0 && list.Count > targetCount)
                    {
                        for (int i = list.Count - 1; i >= targetCount; i--)
                        {
                            GameObject extra = list[i];
                            if (extra != null && (hasActiveRecycler || (!extra.activeSelf && extra.transform.parent == pool.transform)))
                            {
                                SilenceAndClearEffects(extra);
                                UnityEngine.Object.DestroyImmediate(extra);
                                list.RemoveAt(i);
                                resetCount++;
                            }
                        }
                    }

                    if (hasActiveRecycler)
                    {
                        if (inProcessRestore && targetCount == 0 && !startupSizes.ContainsKey(prefab) && list.Count == 0)
                        {
                            pooledObjects.Remove(prefab);
                            continue;
                        }

                        if (inProcessRestore && targetCount > 0)
                        {
                            for (int i = 0; i < list.Count; i++)
                            {
                                GameObject old = list[i];
                                if (old != null)
                                {
                                    SilenceAndClearEffects(old);
                                    UnityEngine.Object.DestroyImmediate(old);
                                }
                                list[i] = InstantiateActiveRecyclerClone(prefab, pool.transform);
                                resetCount++;
                            }
                            while (list.Count < targetCount)
                            {
                                list.Add(InstantiateActiveRecyclerClone(prefab, pool.transform));
                                resetCount++;
                            }
                        }
                        continue;
                    }

                    int limit = entry != null ? Math.Min(entry.AwakenedSlots.Length, list.Count) : list.Count;
                    for (int i = 0; i < limit; i++)
                    {
                        bool shouldBeAwakened = entry != null && entry.AwakenedSlots[i];
                        if (!inProcessRestore && shouldBeAwakened)
                        {
                            continue;
                        }

                        GameObject go = list[i];
                        if (go == null || go.activeSelf)
                        {
                            continue;
                        }
                        if (!inProcessRestore && !IsObjectAwakened(go))
                        {
                            continue;
                        }

                        if (go.transform.parent == pool.transform)
                        {
                            bool prevPrefabActive = prefab.activeSelf;
                            prefab.SetActive(false);
                            GameObject fresh;
                            try
                            {
                                fresh = UnityEngine.Object.Instantiate(prefab, pool.transform);
                            }
                            finally
                            {
                                prefab.SetActive(prevPrefabActive);
                            }
                            if (shouldBeAwakened)
                            {
                                fresh.SetActive(true);
                                SilenceAndClearEffects(fresh);
                                fresh.SetActive(false);
                            }
                            list[i] = fresh;
                            UnityEngine.Object.DestroyImmediate(go);
                            resetCount++;
                        }
                        else if (!shouldBeAwakened)
                        {
                            foreach (MonoBehaviour mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                            {
                                if (mb == null || mb.isActiveAndEnabled || (IntPtr)CachedPointerField.GetValue(mb)! == IntPtr.Zero)
                                {
                                    continue;
                                }
                                if (HeroInactiveRandomTypes.Contains(mb.GetType()))
                                {
                                    if (TryReadMonoLifecycle(mb, out byte[] cur) && (cur[0] != 0 || cur[1] != 0))
                                    {
                                        WriteMonoLifecycle(mb, new byte[] { 0, 0 });
                                        resetCount++;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                UnityEngine.Random.state = savedRng;
            }

            if (warmedCount > 0 || resetCount > 0)
            {
                Modding.Logger.Log($"[HKTAS] Synced ObjectPool slots for S0 parity: warmed={warmedCount}, reset={resetCount}");
            }
        }

        /// <summary>
        /// Recycles active DontDestroyOnLoad pooled combat/effect clones (such as Strike Nail R)
        /// that survived a direct in-process scene transition before ApplyObjectPoolWarmup runs.
        /// </summary>
        internal static int RecycleActivePooledObjectsForInProcessRestore()
        {
            ObjectPool? pool = ObjectPool.instance;
            if (pool == null)
            {
                return 0;
            }

            if (SpawnedObjectsField.GetValue(pool) is not Dictionary<GameObject, GameObject> spawned || spawned.Count == 0)
            {
                return 0;
            }

            var pooledObjects = PoolObjectsField.GetValue(pool) as Dictionary<GameObject, List<GameObject>>;
            var startupPrefabs = new HashSet<GameObject>();
            if (pool.startupPools != null)
            {
                foreach (ObjectPool.StartupPool sp in pool.startupPools)
                {
                    if (sp?.prefab != null)
                    {
                        startupPrefabs.Add(sp.prefab);
                    }
                }
            }

            List<KeyValuePair<GameObject, GameObject>> active = spawned
                .Where(pair => pair.Key != null && !pair.Key.name.StartsWith("Grimmchild", StringComparison.Ordinal))
                .ToList();
            if (active.Count == 0)
            {
                return 0;
            }

            UnityEngine.Random.State savedRng = UnityEngine.Random.state;
            int recycled = 0;
            try
            {
                foreach (KeyValuePair<GameObject, GameObject> pair in active)
                {
                    GameObject go = pair.Key;
                    GameObject prefab = pair.Value;
                    if (go == null)
                    {
                        continue;
                    }
                    SilenceAndClearEffects(go);
                    if (go.GetComponent<ActiveRecycler>() != null)
                    {
                        // Do NOT call ObjectPool.Recycle on ActiveRecycler clones: it sends "A RECYCLE"
                        // to FSMs (e.g., Strike Nail R/Strike/Effect RandomFloat) and keeps them active.
                        spawned.Remove(go);
                        UnityEngine.Object.DestroyImmediate(go);
                        if (prefab != null && startupPrefabs.Contains(prefab) && pooledObjects != null
                            && pooledObjects.TryGetValue(prefab, out List<GameObject>? list))
                        {
                            list.Add(InstantiateActiveRecyclerClone(prefab, pool.transform));
                        }
                        recycled++;
                    }
                    else
                    {
                        ObjectPool.Recycle(go);
                        recycled++;
                    }
                }
            }
            finally
            {
                UnityEngine.Random.state = savedRng;
            }

            if (recycled > 0)
            {
                Modding.Logger.Log($"[HKTAS] Recycled {recycled} active ObjectPool instance(s) for in-process S0 restore");
            }
            return recycled;
        }

        private static GameObject InstantiateActiveRecyclerClone(GameObject prefab, Transform poolTransform)
        {
            bool prevActive = prefab.activeSelf;
            prefab.SetActive(true);
            GameObject fresh = UnityEngine.Object.Instantiate(prefab, poolTransform);
            fresh.transform.SetPosition2D(new Vector2(-20f, -20f));
            prefab.SetActive(prevActive);
            foreach (tk2dSprite sprite in fresh.GetComponentsInChildren<tk2dSprite>(true))
            {
                if (sprite != null)
                {
                    sprite.ForceBuild();
                }
            }
            // Start PlayMakerFSMs synchronously inside savedRng so they do not invoke Start()/Random on frame S0.
            foreach (PlayMakerFSM fsm in fresh.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (fsm != null && fsm.Fsm != null && !fsm.Fsm.Started)
                {
                    fsm.Fsm.Start();
                }
            }
            return fresh;
        }

        private static void SilenceAndClearEffects(GameObject go)
        {
            foreach (AudioSource audio in go.GetComponentsInChildren<AudioSource>(true))
            {
                if (audio != null) audio.Stop();
            }
            foreach (ParticleSystem ps in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (ps != null) ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        private static bool IsObjectAwakened(GameObject? go)
        {
            if (go == null || !LoadingEngineClockProbe.IsPinnedEngineMatched)
            {
                return false;
            }
            foreach (MonoBehaviour mb in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null || (IntPtr)CachedPointerField.GetValue(mb)! == IntPtr.Zero)
                {
                    continue;
                }
                if (TryReadMonoLifecycle(mb, out byte[] lifecycle) && lifecycle[0] == 1)
                {
                    return true;
                }
            }
            return false;
        }

        private static void BuildPrefabTables(
            ObjectPool pool,
            HeroController hero,
            out Dictionary<GameObject, string> prefabToKey,
            out Dictionary<string, GameObject> keyToPrefab)
        {
            var p2k = new Dictionary<GameObject, string>();
            var k2p = new Dictionary<string, GameObject>(StringComparer.Ordinal);

            void Register(string key, GameObject? go)
            {
                if (go == null || go.scene.IsValid() || k2p.ContainsKey(key) || p2k.ContainsKey(go))
                {
                    return;
                }
                k2p[key] = go;
                p2k[go] = key;
            }

            void RegisterFields(string prefix, object target)
            {
                foreach (FieldInfo field in target.GetType().GetFields(AnyInstance))
                {
                    if (field.FieldType == typeof(GameObject))
                    {
                        var go = field.GetValue(target) as GameObject;
                        if (go != null)
                        {
                            Register(prefix + field.Name + ":" + go.name, go);
                        }
                    }
                    else if (typeof(Component).IsAssignableFrom(field.FieldType))
                    {
                        var comp = field.GetValue(target) as Component;
                        if (comp != null)
                        {
                            Register(prefix + field.Name + ":" + comp.gameObject.name, comp.gameObject);
                        }
                    }
                }
            }

            if (pool.startupPools != null)
            {
                for (int i = 0; i < pool.startupPools.Length; i++)
                {
                    GameObject? prefab = pool.startupPools[i]?.prefab;
                    if (prefab != null)
                    {
                        Register($"startup:{i}:{prefab.name}", prefab);
                    }
                }
            }

            RegisterFields("hero:", hero);
            foreach (MonoBehaviour child in hero.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (child != null && child != hero)
                {
                    RegisterFields($"hero-child:{GetRelativePath(hero.transform, child.transform)}:{child.GetType().Name}:", child);
                }
            }
            if (GlobalPrefabDefaults.Instance != null)
            {
                RegisterFields("global:", GlobalPrefabDefaults.Instance);
            }
            if (GameManager.instance != null)
            {
                RegisterFields("gm:", GameManager.instance);
            }

            prefabToKey = p2k;
            keyToPrefab = k2p;
        }

        private static string GetRelativePath(Transform root, Transform target)
        {
            if (target == root)
            {
                return string.Empty;
            }
            var parts = new List<string>();
            Transform? cur = target;
            while (cur != null && cur != root)
            {
                parts.Add(cur.name);
                cur = cur.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static readonly FieldInfo? OneAxisUpdateTickField = typeof(OneAxisInputControl).GetField("<UpdateTick>k__BackingField", AnyInstance);
        private static readonly FieldInfo? OneAxisPendingTickField = typeof(OneAxisInputControl).GetField("pendingTick", AnyInstance);
        private static readonly FieldInfo? OneAxisPendingCommitField = typeof(OneAxisInputControl).GetField("pendingCommit", AnyInstance);
        private static readonly FieldInfo? OneAxisNextRepeatTimeField = typeof(OneAxisInputControl).GetField("nextRepeatTime", AnyInstance);
        private static readonly FieldInfo? OneAxisWasRepeatedField = typeof(OneAxisInputControl).GetField("wasRepeated", AnyInstance);
        private static readonly FieldInfo? TwoAxisUpdateTickField = typeof(TwoAxisInputControl).GetField("<UpdateTick>k__BackingField", AnyInstance);
        private static readonly FieldInfo? InputControlZeroTickField = typeof(InputControl).GetField("zeroTick", AnyInstance);
        private static readonly FieldInfo? InputDeviceLastTickField = typeof(InputDevice).GetField("<LastInputTick>k__BackingField", AnyInstance);
        private static readonly FieldInfo? PlayerActionSetUpdateTickField = typeof(PlayerActionSet).GetField("<UpdateTick>k__BackingField", AnyInstance);
        private static readonly FieldInfo? PlayerActionSetLastChangedField = typeof(PlayerActionSet).GetField("LastInputTypeChangedTick", AnyInstance);
        private static readonly FieldInfo? PlayerActionSetTwoAxisField = typeof(PlayerActionSet).GetField("twoAxisActions", AnyInstance);
        private static readonly FieldInfo? PlayerActionLastChangedField = typeof(PlayerAction).GetField("LastInputTypeChangedTick", AnyInstance);
        private static readonly FieldInfo? InputModuleDirectionField = typeof(HollowKnightInputModule).GetField("direction", AnyInstance);
        private static readonly FieldInfo? InputModuleNextMoveRepeatField = typeof(HollowKnightInputModule).GetField("nextMoveRepeatTime", AnyInstance);
        private static readonly FieldInfo? InputModuleLastVectorPressedField = typeof(HollowKnightInputModule).GetField("lastVectorPressedTime", AnyInstance);
        private static readonly FieldInfo? InputManagerDevicesField = typeof(InputManager).GetField("devices", AnyStatic);
        private static readonly FieldInfo? InputManagerActionSetsField = typeof(InputManager).GetField("playerActionSets", AnyStatic);

        /// <summary>
        /// Clamps/resets persistent InControl tick stamps that exceed <paramref name="targetTick"/>
        /// after rewinding InputManager.CurrentTick during an in-process reverse S0 restore.
        /// </summary>
        internal static void ResetInControlPersistentTicks(ulong targetTick)
        {
            foreach (HollowKnightInputModule mod in UnityEngine.Object.FindObjectsOfType<HollowKnightInputModule>())
            {
                if (mod == null) continue;
                ResetTwoAxisIfFuture(InputModuleDirectionField?.GetValue(mod) as TwoAxisInputControl, targetTick, forceReset: true);
                InputModuleNextMoveRepeatField?.SetValue(mod, 0f);
                InputModuleLastVectorPressedField?.SetValue(mod, 0f);
            }

            if (InputManagerDevicesField?.GetValue(null) is System.Collections.IList devices)
            {
                foreach (object? item in devices)
                {
                    if (item is InputDevice device) ResetDeviceTicks(device, targetTick);
                }
            }
            if (InputManager.ActiveDevice != null) ResetDeviceTicks(InputManager.ActiveDevice, targetTick);
            if (InputDevice.Null != null) ResetDeviceTicks(InputDevice.Null, targetTick);

            if (InputManagerActionSetsField?.GetValue(null) is System.Collections.IList sets)
            {
                foreach (object? item in sets)
                {
                    if (item is not PlayerActionSet set) continue;
                    ClampUlongField(PlayerActionSetUpdateTickField, set, targetTick, targetTick);
                    ClampUlongField(PlayerActionSetLastChangedField, set, targetTick, targetTick);
                    if (set.Actions != null)
                    {
                        foreach (PlayerAction action in set.Actions)
                        {
                            if (action == null) continue;
                            ResetOneAxisIfFuture(action, targetTick, forceReset: false);
                            ClampUlongField(PlayerActionLastChangedField, action, targetTick, targetTick);
                        }
                    }
                    if (PlayerActionSetTwoAxisField?.GetValue(set) is System.Collections.IList twoAxisList)
                    {
                        foreach (object? t in twoAxisList)
                        {
                            ResetTwoAxisIfFuture(t as TwoAxisInputControl, targetTick, forceReset: false);
                        }
                    }
                }
            }
        }

        private static void ResetDeviceTicks(InputDevice device, ulong targetTick)
        {
            ClampUlongField(InputDeviceLastTickField, device, targetTick, targetTick);
            ResetTwoAxisIfFuture(device.LeftStick, targetTick, forceReset: false);
            ResetTwoAxisIfFuture(device.RightStick, targetTick, forceReset: false);
            ResetTwoAxisIfFuture(device.DPad, targetTick, forceReset: false);
            if (device.Controls != null)
            {
                foreach (InputControl control in device.Controls)
                {
                    ResetOneAxisIfFuture(control, targetTick, forceReset: false);
                }
            }
        }

        private static void ResetTwoAxisIfFuture(TwoAxisInputControl? control, ulong targetTick, bool forceReset)
        {
            if (control == null) return;
            if (forceReset)
            {
                TwoAxisUpdateTickField?.SetValue(control, 0UL);
            }
            else
            {
                ClampUlongField(TwoAxisUpdateTickField, control, targetTick, 0UL);
            }
            ResetOneAxisIfFuture(control.Left, targetTick, forceReset);
            ResetOneAxisIfFuture(control.Right, targetTick, forceReset);
            ResetOneAxisIfFuture(control.Up, targetTick, forceReset);
            ResetOneAxisIfFuture(control.Down, targetTick, forceReset);
        }

        private static void ResetOneAxisIfFuture(OneAxisInputControl? control, ulong targetTick, bool forceReset)
        {
            if (control == null) return;
            bool reset = forceReset;
            if (OneAxisUpdateTickField != null && (ulong)OneAxisUpdateTickField.GetValue(control)! > targetTick)
            {
                OneAxisUpdateTickField.SetValue(control, 0UL);
                reset = true;
            }
            else if (forceReset)
            {
                OneAxisUpdateTickField?.SetValue(control, 0UL);
            }
            if (OneAxisPendingTickField != null && (forceReset || (ulong)OneAxisPendingTickField.GetValue(control)! > targetTick))
            {
                OneAxisPendingTickField.SetValue(control, 0UL);
                OneAxisPendingCommitField?.SetValue(control, false);
                reset = true;
            }
            if (reset)
            {
                OneAxisNextRepeatTimeField?.SetValue(control, 0f);
                OneAxisWasRepeatedField?.SetValue(control, false);
            }
            if (control is InputControl ic && InputControlZeroTickField != null
                && (forceReset || (ulong)InputControlZeroTickField.GetValue(ic)! > targetTick))
            {
                InputControlZeroTickField.SetValue(ic, 0UL);
            }
        }

        private static void ClampUlongField(FieldInfo? field, object target, ulong maxAllowed, ulong replacement)
        {
            if (field != null && (ulong)field.GetValue(target)! > maxAllowed)
            {
                field.SetValue(target, replacement);
            }
        }
    }
}
