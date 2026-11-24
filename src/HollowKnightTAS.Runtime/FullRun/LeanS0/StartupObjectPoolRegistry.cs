using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HollowKnightTAS.Runtime.FullRun
{
    /// <summary>
    /// Captures persistent <see cref="ObjectPool.CreatePool"/> definitions registered during startup
    /// (excluding lazy creation inside <see cref="ObjectPool.Spawn"/>) so S0 boundary resets can rebuild
    /// them uniformly alongside vanilla startup pools without persisting pooled instances.
    /// </summary>
    internal static class StartupObjectPoolRegistry
    {
        private static readonly object Sync = new object();
        private static readonly StartupObjectPoolCatalog<int, GameObject> Catalog =
            new StartupObjectPoolCatalog<int, GameObject>();

        private static bool hooksInstalled;

        internal static bool IsRegistrationOpen => Catalog.IsRegistrationOpen;

        internal static int RegisteredCount
        {
            get
            {
                lock (Sync)
                {
                    return Catalog.Count;
                }
            }
        }

        internal static void InstallHooks()
        {
            lock (Sync)
            {
                if (hooksInstalled)
                {
                    return;
                }

                Catalog.Reset();
                On.ObjectPool.CreatePool += OnObjectPoolCreatePool;
                On.ObjectPool.Spawn_GameObject_Transform_Vector3_Quaternion += OnObjectPoolSpawn;
                hooksInstalled = true;
            }
        }

        internal static void UninstallHooks()
        {
            lock (Sync)
            {
                if (!hooksInstalled)
                {
                    return;
                }

                On.ObjectPool.CreatePool -= OnObjectPoolCreatePool;
                On.ObjectPool.Spawn_GameObject_Transform_Vector3_Quaternion -= OnObjectPoolSpawn;
                hooksInstalled = false;
                Catalog.Reset();
            }
        }

        internal static void CloseRegistrationWindow()
        {
            int count;
            lock (Sync)
            {
                Catalog.CloseRegistrationWindow();
                count = Catalog.Count;
            }

            if (count > 0)
            {
                Modding.Logger.Log($"[HKTAS] Startup ObjectPool registration window closed; captured={count}");
            }
        }

        internal static void BeginRebuild()
        {
            lock (Sync)
            {
                Catalog.BeginRebuild();
            }
        }

        internal static void EndRebuild()
        {
            lock (Sync)
            {
                Catalog.EndRebuild();
            }
        }

        internal static IDisposable EnterRebuildScope()
        {
            BeginRebuild();
            return new RebuildScope();
        }

        internal static StartupObjectPoolCatalogEntry<GameObject>[] GetRegisteredEntries()
        {
            lock (Sync)
            {
                return Catalog.GetRegisteredEntries();
            }
        }

        private static void OnObjectPoolCreatePool(
            On.ObjectPool.orig_CreatePool orig,
            GameObject prefab,
            int initialPoolSize)
        {
            if (Catalog.IsRegistrationOpen)
            {
                TryRecord(prefab, initialPoolSize);
            }

            orig(prefab, initialPoolSize);
        }

        private static GameObject OnObjectPoolSpawn(
            On.ObjectPool.orig_Spawn_GameObject_Transform_Vector3_Quaternion orig,
            GameObject prefab,
            Transform parent,
            Vector3 position,
            Quaternion rotation)
        {
            bool trackSpawn = Catalog.IsRegistrationOpen;
            if (trackSpawn)
            {
                lock (Sync)
                {
                    Catalog.EnterSpawn();
                }
            }

            try
            {
                return orig(prefab, parent, position, rotation);
            }
            finally
            {
                if (trackSpawn)
                {
                    lock (Sync)
                    {
                        Catalog.ExitSpawn();
                    }
                }
            }
        }

        private static void TryRecord(GameObject? prefab, int initialPoolSize)
        {
            if (prefab == null || initialPoolSize < 0)
            {
                return;
            }

            Scene scene = prefab.scene;
            bool isPersistent = StartupObjectPoolCatalog<int, GameObject>.IsPersistentPrefabScene(
                scene.IsValid(),
                scene.name);
            if (!isPersistent)
            {
                return;
            }

            int instanceId = prefab.GetInstanceID();
            lock (Sync)
            {
                Catalog.TryRegister(instanceId, prefab, initialPoolSize, isPersistent);
            }
        }

        private sealed class RebuildScope : IDisposable
        {
            private bool disposed;

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                EndRebuild();
            }
        }
    }
}
