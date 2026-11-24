using System;
using System.Collections.Generic;

namespace HollowKnightTAS.Runtime.FullRun
{
    /// <summary>
    /// Immutable startup object pool definition captured during mod initialization.
    /// </summary>
    internal readonly struct StartupObjectPoolCatalogEntry<TPrefab>
    {
        public StartupObjectPoolCatalogEntry(TPrefab prefab, int initialPoolSize)
        {
            Prefab = prefab;
            InitialPoolSize = initialPoolSize;
        }

        public TPrefab Prefab { get; }
        public int InitialPoolSize { get; }
    }

    /// <summary>
    /// Pure state machine and deduplicated catalog tracking startup ObjectPool.CreatePool definitions
    /// while excluding post-startup calls, Spawn-induced lazy creation, rebuild reentrancy, and
    /// transient scene-bound prefabs.
    /// </summary>
    internal sealed class StartupObjectPoolCatalog<TKey, TPrefab>
        where TKey : notnull
        where TPrefab : class
    {
        private readonly Dictionary<TKey, StartupObjectPoolCatalogEntry<TPrefab>> entriesByKey;
        private readonly List<StartupObjectPoolCatalogEntry<TPrefab>> orderedEntries =
            new List<StartupObjectPoolCatalogEntry<TPrefab>>();

        private volatile bool registrationOpen = true;
        private int spawnDepth;
        private int rebuildDepth;

        public StartupObjectPoolCatalog(IEqualityComparer<TKey>? comparer = null)
        {
            entriesByKey = new Dictionary<TKey, StartupObjectPoolCatalogEntry<TPrefab>>(comparer);
        }

        public bool IsRegistrationOpen => registrationOpen;
        public bool IsInSpawn => spawnDepth > 0;
        public bool IsRebuilding => rebuildDepth > 0;
        public int Count => orderedEntries.Count;

        public static bool IsPersistentPrefabScene(bool sceneIsValid, string? sceneName)
            => !sceneIsValid || string.Equals(sceneName, "DontDestroyOnLoad", StringComparison.Ordinal);

        public void CloseRegistrationWindow()
        {
            registrationOpen = false;
        }

        public void Reset()
        {
            entriesByKey.Clear();
            orderedEntries.Clear();
            registrationOpen = true;
            spawnDepth = 0;
            rebuildDepth = 0;
        }

        public void EnterSpawn()
        {
            spawnDepth++;
        }

        public void ExitSpawn()
        {
            if (spawnDepth > 0)
            {
                spawnDepth--;
            }
        }

        public void BeginRebuild()
        {
            rebuildDepth++;
        }

        public void EndRebuild()
        {
            if (rebuildDepth > 0)
            {
                rebuildDepth--;
            }
        }

        public bool TryRegister(TKey key, TPrefab? prefab, int initialPoolSize, bool isPersistentPrefab)
        {
            if (!registrationOpen
                || rebuildDepth > 0
                || spawnDepth > 0
                || prefab == null
                || initialPoolSize < 0
                || !isPersistentPrefab
                || entriesByKey.ContainsKey(key))
            {
                return false;
            }

            var entry = new StartupObjectPoolCatalogEntry<TPrefab>(prefab, initialPoolSize);
            entriesByKey.Add(key, entry);
            orderedEntries.Add(entry);
            return true;
        }

        public StartupObjectPoolCatalogEntry<TPrefab>[] GetRegisteredEntries()
            => orderedEntries.Count == 0
                ? Array.Empty<StartupObjectPoolCatalogEntry<TPrefab>>()
                : orderedEntries.ToArray();
    }
}
