using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using HollowKnightTAS.Core.SceneCheckpoints;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    public sealed class ModRandomStateSyncTests
    {
        [TestMethod]
        public void CaptureAndRestore_ReproducesExactNextSequence()
        {
            using var tracker = new ModRandomTracker(Thread.CurrentThread.ManagedThreadId);
            const string callsite = "TestMod:BossController:Awake:16";

            int seed = tracker.PrepareMainThreadSeed(callsite);
            var rng = new Random(seed);
            tracker.RegisterConstructedInstance(rng);

            for (int i = 0; i < 25; i++)
            {
                _ = rng.Next();
            }

            ModRandomStateSnapshot? snapshot = tracker.CaptureState();
            Assert.IsNotNull(snapshot);
            Assert.AreEqual(1, snapshot!.Ordinals.Count);
            Assert.AreEqual(1u, snapshot.Ordinals[callsite]);
            Assert.AreEqual(1, snapshot.Instances.Length);

            var expectedInts = new int[64];
            var expectedDoubles = new double[16];
            for (int i = 0; i < expectedInts.Length; i++)
            {
                expectedInts[i] = rng.Next();
            }
            for (int i = 0; i < expectedDoubles.Length; i++)
            {
                expectedDoubles[i] = rng.NextDouble();
            }

            // Advance further before restoring to S0 snapshot.
            for (int i = 0; i < 40; i++)
            {
                _ = rng.Next(1, 1000);
            }

            ModRandomStateSnapshot roundTripped = ModRandomStateSnapshot.Decode(snapshot.Encode());
            ModRandomRestoreResult result = tracker.RestoreState(roundTripped);
            Assert.AreEqual(1, result.RestoredInstanceCount);
            Assert.AreEqual(0, result.MissingInstanceCount);

            for (int i = 0; i < expectedInts.Length; i++)
            {
                Assert.AreEqual(expectedInts[i], rng.Next(), $"Mismatch at Next() step {i}.");
            }
            for (int i = 0; i < expectedDoubles.Length; i++)
            {
                Assert.AreEqual(expectedDoubles[i], rng.NextDouble(), $"Mismatch at NextDouble() step {i}.");
            }
        }

        [TestMethod]
        public void RestoreState_RollsBackOrdinalsAndReproducesSameDeterministicSeed()
        {
            using var tracker = new ModRandomTracker(Thread.CurrentThread.ManagedThreadId);
            const string callsite = "TestMod:PhaseTwo:Enter:42";

            int seed0 = tracker.PrepareMainThreadSeed(callsite);
            var rng0 = new Random(seed0);
            tracker.RegisterConstructedInstance(rng0);

            ModRandomStateSnapshot? snapshot = tracker.CaptureState();
            Assert.IsNotNull(snapshot);
            Assert.AreEqual(1u, snapshot!.Ordinals[callsite]);

            int originalSeed1 = tracker.PrepareMainThreadSeed(callsite);
            var rng1 = new Random(originalSeed1);
            tracker.RegisterConstructedInstance(rng1);
            Assert.AreNotEqual(seed0, originalSeed1);

            ModRandomRestoreResult result = tracker.RestoreState(snapshot);
            Assert.AreEqual(1, result.OrdinalCount);
            Assert.AreEqual(1, result.RestoredInstanceCount);

            int replayedSeed1 = tracker.PrepareMainThreadSeed(callsite);
            Assert.AreEqual(originalSeed1, replayedSeed1);
        }

        [TestMethod]
        public void CaptureState_ReturnsNullWhenEmpty_AndPrunesCollectedInstances()
        {
            using var tracker = new ModRandomTracker(Thread.CurrentThread.ManagedThreadId);
            Assert.IsNull(tracker.CaptureState());

            const string callsite = "TestMod:Transient:Create:8";
            CreateAndDiscardTrackedRandom(tracker, callsite);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            ModRandomStateSnapshot? snapshot = tracker.CaptureState();
            Assert.IsNotNull(snapshot);
            Assert.AreEqual(1u, snapshot!.Ordinals[callsite]);
            Assert.AreEqual(0, snapshot.Instances.Length);
        }

        [TestMethod]
        public void RestoreState_MissingTargetInstance_LogsDiagnosticWithoutThrowing()
        {
            using var tracker = new ModRandomTracker(Thread.CurrentThread.ManagedThreadId);
            var diagnostics = new List<string>();

            var snapshot = new ModRandomStateSnapshot(
                new Dictionary<string, uint>(StringComparer.Ordinal)
                {
                    ["MissingMod:Type:Method:0"] = 1
                },
                new[]
                {
                    new ModRandomInstanceSnapshot(
                        "MissingMod:Type:Method:0",
                        0,
                        3,
                        24,
                        new int[56])
                });

            ModRandomRestoreResult result = tracker.RestoreState(snapshot, diagnostics.Add);
            Assert.AreEqual(1, result.OrdinalCount);
            Assert.AreEqual(1, result.SnapshotInstanceCount);
            Assert.AreEqual(0, result.RestoredInstanceCount);
            Assert.AreEqual(1, result.MissingInstanceCount);
            Assert.AreEqual(1, diagnostics.Count);
            StringAssert.Contains(diagnostics[0], "MissingMod:Type:Method:0");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CreateAndDiscardTrackedRandom(ModRandomTracker tracker, string callsite)
        {
            int seed = tracker.PrepareMainThreadSeed(callsite);
            var transient = new Random(seed);
            tracker.RegisterConstructedInstance(transient);
            _ = transient.Next();
        }
    }
}
