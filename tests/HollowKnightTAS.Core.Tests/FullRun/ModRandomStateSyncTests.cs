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
        public void CaptureState_ReturnsEmptySnapshotWhenEmpty_AndPrunesCollectedInstances()
        {
            using var tracker = new ModRandomTracker(Thread.CurrentThread.ManagedThreadId);
            ModRandomStateSnapshot emptySnapshot = tracker.CaptureState();
            Assert.IsNotNull(emptySnapshot);
            Assert.AreEqual(0, emptySnapshot.Ordinals.Count);
            Assert.AreEqual(0, emptySnapshot.Instances.Length);

            ModRandomStateSnapshot decodedEmpty = ModRandomStateSnapshot.Decode(emptySnapshot.Encode());
            Assert.AreEqual(0, decodedEmpty.Ordinals.Count);
            Assert.AreEqual(0, decodedEmpty.Instances.Length);

            const string callsite = "TestMod:Transient:Create:8";
            CreateAndDiscardTrackedRandom(tracker, callsite);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            ModRandomStateSnapshot snapshot = tracker.CaptureState();
            Assert.IsNotNull(snapshot);
            Assert.AreEqual(1u, snapshot.Ordinals[callsite]);
            Assert.AreEqual(0, snapshot.Instances.Length);
        }

        [TestMethod]
        public void RestoreState_EmptySnapshot_ClearsAdvancedOrdinals()
        {
            using var tracker = new ModRandomTracker(Thread.CurrentThread.ManagedThreadId);
            const string callsite = "TestMod:Encounter:Start:24";

            ModRandomStateSnapshot emptySnapshot = ModRandomStateSnapshot.Decode(tracker.CaptureState().Encode());
            Assert.AreEqual(0, emptySnapshot.Ordinals.Count);
            Assert.AreEqual(0, emptySnapshot.Instances.Length);

            int seed0 = tracker.PrepareMainThreadSeed(callsite);
            var rng0 = new Random(seed0);
            tracker.RegisterConstructedInstance(rng0);

            int seed1 = tracker.PrepareMainThreadSeed(callsite);
            var rng1 = new Random(seed1);
            tracker.RegisterConstructedInstance(rng1);
            Assert.AreNotEqual(seed0, seed1);
            Assert.AreEqual(2u, tracker.SnapshotOrdinals()[callsite]);

            ModRandomRestoreResult result = tracker.RestoreState(emptySnapshot);
            Assert.AreEqual(0, result.OrdinalCount);
            Assert.AreEqual(0, result.SnapshotInstanceCount);
            Assert.AreEqual(0, result.RestoredInstanceCount);
            Assert.AreEqual(0, result.MissingInstanceCount);
            Assert.AreEqual(0, tracker.SnapshotOrdinals().Count);

            int replayedSeed0 = tracker.PrepareMainThreadSeed(callsite);
            Assert.AreEqual(seed0, replayedSeed0);
            Assert.AreEqual(ModRandomTracker.ComputeDeterministicSeed(callsite, 0), replayedSeed0);
        }

        [TestMethod]
        [DataRow("TestMod:TestMod.LazyBoss:Void .cctor():12")]
        [DataRow("TestMod:TestMod.LazyBoss:.cctor():12")]
        public void RestoreState_PostSnapshotStaticCctorInstance_ResetsToInitialSeedAndRemainsTracked(string cctorCallsite)
        {
            using var tracker = new ModRandomTracker(Thread.CurrentThread.ManagedThreadId);
            const string nonCctorCallsite = "TestMod:TestMod.RegularActor:Void Awake():4";

            // Capture S0 before the .cctor() executes.
            ModRandomStateSnapshot emptyS0 = ModRandomStateSnapshot.Decode(tracker.CaptureState().Encode());

            // Later in the same process, the type's .cctor() constructs its static System.Random.
            int cctorSeed = tracker.PrepareMainThreadSeed(cctorCallsite);
            var staticRng = new Random(cctorSeed);
            tracker.RegisterConstructedInstance(staticRng);

            // Also construct a non-.cctor instance post-S0 to ensure only .cctor instances auto-reset.
            int nonCctorSeed = tracker.PrepareMainThreadSeed(nonCctorCallsite);
            var regularRng = new Random(nonCctorSeed);
            tracker.RegisterConstructedInstance(regularRng);
            _ = regularRng.Next();

            var initialInts = new int[32];
            var initialDoubles = new double[8];
            var stateProbe = new Random(cctorSeed);
            for (int i = 0; i < initialInts.Length; i++)
            {
                initialInts[i] = stateProbe.Next();
            }
            for (int i = 0; i < initialDoubles.Length; i++)
            {
                initialDoubles[i] = stateProbe.NextDouble();
            }

            // Consume random numbers from the live .cctor instance before rolling back to S0.
            for (int i = 0; i < 20; i++)
            {
                _ = staticRng.Next();
            }

            var diagnostics = new List<string>();
            ModRandomRestoreResult result = tracker.RestoreState(emptyS0, diagnostics.Add);
            Assert.AreEqual(0, result.OrdinalCount);
            Assert.AreEqual(0, diagnostics.Count);
            Assert.AreEqual(0, tracker.SnapshotOrdinals().Count);

            // The live static .cctor instance must be reset back to its deterministic initial sequence.
            for (int i = 0; i < initialInts.Length; i++)
            {
                Assert.AreEqual(initialInts[i], staticRng.Next(), $"Static .cctor Next() mismatch at step {i}.");
            }
            for (int i = 0; i < initialDoubles.Length; i++)
            {
                Assert.AreEqual(initialDoubles[i], staticRng.NextDouble(), $"Static .cctor NextDouble() mismatch at step {i}.");
            }

            // Non-.cctor instance was absent from S0 and must not be reset to its initial seed.
            var freshRegular = new Random(nonCctorSeed);
            Assert.AreNotEqual(freshRegular.Next(), regularRng.Next());

            // The .cctor instance remains tracked across rollbacks so a later S0 snapshot captures its mid-run state.
            ModRandomStateSnapshot midS0 = ModRandomStateSnapshot.Decode(tracker.CaptureState().Encode());
            Assert.AreEqual(2, midS0.Instances.Length);
            var expectedMidInts = new int[16];
            for (int i = 0; i < expectedMidInts.Length; i++)
            {
                expectedMidInts[i] = staticRng.Next();
            }

            // Advance further, then restore to midS0 (which DOES contain the .cctor instance).
            for (int i = 0; i < 10; i++)
            {
                _ = staticRng.Next();
            }
            ModRandomRestoreResult midResult = tracker.RestoreState(midS0, diagnostics.Add);
            Assert.AreEqual(0, diagnostics.Count);
            Assert.AreEqual(2, midResult.RestoredInstanceCount);
            for (int i = 0; i < expectedMidInts.Length; i++)
            {
                Assert.AreEqual(expectedMidInts[i], staticRng.Next(), $"Mid-S0 restore mismatch at step {i}.");
            }
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
