using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    public sealed class StartupObjectPoolRegistryTests
    {
        [TestMethod]
        public void TryRegister_RecordsFirstCapacityOnly_AndPreservesOrder()
        {
            var catalog = new StartupObjectPoolCatalog<int, string>();

            Assert.IsTrue(catalog.TryRegister(101, "PrefabA", 6, isPersistentPrefab: true));
            Assert.IsFalse(catalog.TryRegister(101, "PrefabA", 24, isPersistentPrefab: true));
            Assert.IsTrue(catalog.TryRegister(102, "PrefabB", 0, isPersistentPrefab: true));

            StartupObjectPoolCatalogEntry<string>[] entries = catalog.GetRegisteredEntries();
            Assert.AreEqual(2, entries.Length);
            Assert.AreEqual("PrefabA", entries[0].Prefab);
            Assert.AreEqual(6, entries[0].InitialPoolSize);
            Assert.AreEqual("PrefabB", entries[1].Prefab);
            Assert.AreEqual(0, entries[1].InitialPoolSize);
        }

        [TestMethod]
        public void TryRegister_RejectsInvalidOrNonPersistentPrefabs()
        {
            var catalog = new StartupObjectPoolCatalog<int, string>();

            Assert.IsFalse(catalog.TryRegister(1, null, 4, isPersistentPrefab: true));
            Assert.IsFalse(catalog.TryRegister(2, "NegPool", -1, isPersistentPrefab: true));

            bool regularScenePersistent = StartupObjectPoolCatalog<int, string>.IsPersistentPrefabScene(true, "Town");
            Assert.IsFalse(regularScenePersistent);
            Assert.IsFalse(catalog.TryRegister(3, "SceneObj", 4, regularScenePersistent));

            bool assetPersistent = StartupObjectPoolCatalog<int, string>.IsPersistentPrefabScene(false, string.Empty);
            bool ddolPersistent = StartupObjectPoolCatalog<int, string>.IsPersistentPrefabScene(true, "DontDestroyOnLoad");
            Assert.IsTrue(assetPersistent);
            Assert.IsTrue(ddolPersistent);

            Assert.IsTrue(catalog.TryRegister(4, "AssetPrefab", 3, assetPersistent));
            Assert.IsTrue(catalog.TryRegister(5, "DdolPrefab", 2, ddolPersistent));
            Assert.AreEqual(2, catalog.Count);
        }

        [TestMethod]
        public void TryRegister_ExcludesNestedSpawnLazyPoolCreation()
        {
            var catalog = new StartupObjectPoolCatalog<int, string>();

            catalog.EnterSpawn();
            catalog.EnterSpawn();
            Assert.IsTrue(catalog.IsInSpawn);
            Assert.IsFalse(catalog.TryRegister(10, "LazyFromInnerSpawn", 5, isPersistentPrefab: true));

            catalog.ExitSpawn();
            Assert.IsTrue(catalog.IsInSpawn);
            Assert.IsFalse(catalog.TryRegister(11, "LazyFromOuterSpawn", 5, isPersistentPrefab: true));

            catalog.ExitSpawn();
            Assert.IsFalse(catalog.IsInSpawn);
            Assert.IsTrue(catalog.TryRegister(12, "DirectStartupPool", 5, isPersistentPrefab: true));
            Assert.AreEqual(1, catalog.Count);
        }

        [TestMethod]
        public void TryRegister_ExcludesRebuildReentrancy()
        {
            var catalog = new StartupObjectPoolCatalog<int, string>();

            catalog.BeginRebuild();
            catalog.BeginRebuild();
            Assert.IsTrue(catalog.IsRebuilding);
            Assert.IsFalse(catalog.TryRegister(20, "DuringRebuildInner", 4, isPersistentPrefab: true));

            catalog.EndRebuild();
            Assert.IsTrue(catalog.IsRebuilding);
            Assert.IsFalse(catalog.TryRegister(21, "DuringRebuildOuter", 4, isPersistentPrefab: true));

            catalog.EndRebuild();
            Assert.IsFalse(catalog.IsRebuilding);
            Assert.IsTrue(catalog.TryRegister(22, "NormalStartupPool", 4, isPersistentPrefab: true));
            Assert.AreEqual(1, catalog.Count);
        }

        [TestMethod]
        public void TryRegister_RejectsAfterRegistrationWindowClosed()
        {
            var catalog = new StartupObjectPoolCatalog<int, string>();

            Assert.IsTrue(catalog.IsRegistrationOpen);
            Assert.IsTrue(catalog.TryRegister(30, "BeforeClose", 8, isPersistentPrefab: true));

            catalog.CloseRegistrationWindow();
            Assert.IsFalse(catalog.IsRegistrationOpen);
            Assert.IsFalse(catalog.TryRegister(31, "AfterClose", 8, isPersistentPrefab: true));
            Assert.AreEqual(1, catalog.Count);
        }
    }
}
