using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Companion.Automation;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.ViewModels;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Core.SceneCheckpoints;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Companion.Tests;

[TestClass]
public sealed class StudioTimelineCheckpointIntegrationTests
{
    private const BindingFlags NonPublicFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    private static void SetField(object owner, string name, object? value)
        => owner.GetType().GetField(name, NonPublicFlags)!.SetValue(owner, value);

    private static T GetField<T>(object owner, string name)
        => (T)owner.GetType().GetField(name, NonPublicFlags)!.GetValue(owner)!;

    private static string Id() => Guid.NewGuid().ToString("N");

    private static MovieV2Document CreateMovie(int totalFrames = 400) => new(
        "test",
        new MovieV2Header(
            "game",
            "api",
            "mod",
            NativeExecutionProfile.CanonicalBoundaryProfileId,
            MovieProtocolV2.ActionSchemaId,
            false,
            new string('a', 64),
            800,
            450),
        new[]
        {
            new NativeFrameRun(
                totalFrames,
                Array.Empty<GameInputSample>(),
                new MovieSourceSpan("test", 1, 1, 1),
                50,
                true)
        });

    private static string WriteCanonical(MovieV2Document doc) => new MovieV2Codec().WriteCanonical(doc);

    private static SceneCheckpointPacket CreateLeanS0Packet(
        string studioSessionId,
        string treeId,
        string branchId,
        long generation,
        string checkpointId,
        long transitionId,
        long resumeMovieFrame,
        SceneCheckpointBinding binding)
    {
        var scope = new SceneCheckpointScope(studioSessionId, treeId, branchId, generation);
        var payload = SceneCheckpointPacket.EncodeSections(
            new Dictionary<string, byte[]>
            {
                ["leanS0"] = Enumerable.Range(0, 1024).Select(n => (byte)(n ^ (int)transitionId)).ToArray(),
                ["executionFiles"] = new byte[] { 1, 2, 3, 4 }
            },
            SceneCheckpointHeader.LeanS0SchemaVersion);
        var header = SceneCheckpointHeader.CreateLeanS0(
            checkpointId,
            scope,
            binding,
            SceneCheckpointTrigger.AutoOnLoadBoundary,
            transitionId,
            resumeMovieFrame,
            resumeMovieFrame + 10,
            resumeMovieFrame,
            resumeMovieFrame + 12,
            payload.Length,
            Sha256Utility.ComputeHex(payload));
        return new SceneCheckpointPacket(header, payload);
    }

    [TestMethod]
    public void TimelineTree_FindsNearestCheckpoint_AcrossBranchesByInputPrefix_AndPrunesOnDelete()
    {
        var baseline = new Dictionary<string, string> { ["user1.dat"] = new string('1', 64) };
        var baseDoc = CreateMovie(400);
        var baseText = WriteCanonical(baseDoc);
        var movie1Doc = MovieV2RangeEditor.Paint(baseDoc, 200, 5, "Right", true);
        var movie1Text = WriteCanonical(movie1Doc);

        var tree = new TimelineTree { InitialSavesId = new string('b', 64) };
        var saved150 = tree.Add(150, WriteCanonical(MovieV2Prefix.Take(baseDoc, 150)), baseline);
        var branch1 = tree.UpdateTip(350, movie1Text, baseline, null);

        // Branch 2 diverges at Frame 200: [0..100) still matches Branch 1, [0..250) diverges.
        var movie2Doc = MovieV2RangeEditor.Paint(baseDoc, 200, 5, "Left", true);
        var movie2Text = WriteCanonical(movie2Doc);
        var branch2 = tree.UpdateTip(300, movie2Text, baseline, null);

        Assert.AreEqual(saved150.Id, branch2.ParentId);
        Assert.AreNotEqual(branch1.Id, branch2.Id);

        var studioSessionId = Id();
        var binding = new SceneCheckpointBinding(
            new string('a', 64),
            SceneCheckpointBinding.HashInitialSaves(new Dictionary<string, string> { ["user1.dat"] = new string('1', 64) }),
            NativeExecutionProfile.CanonicalBoundaryProfileId);

        var cp100Id = Id();
        var cp250Id = Id();
        var pkt100 = CreateLeanS0Packet(studioSessionId, tree.Id, MainViewModel.FormatBranchId(branch1.Id), 1, cp100Id, 1, 100, binding);
        var pkt250 = CreateLeanS0Packet(studioSessionId, tree.Id, MainViewModel.FormatBranchId(branch1.Id), 1, cp250Id, 2, 250, binding);

        tree.RecordCheckpoint(cp100Id, 100, 1, movie1Text);
        tree.RecordCheckpoint(cp250Id, 250, 2, movie1Text);

        var headers = new[] { pkt100.Header, pkt250.Header };

        var bestForBranch1 = tree.FindNearestCheckpoint(300, movie1Doc, headers, MainViewModel.FormatBranchId(branch1.Id));
        Assert.IsNotNull(bestForBranch1);
        Assert.AreEqual(cp250Id, bestForBranch1.CheckpointId);

        var bestForBranch2 = tree.FindNearestCheckpoint(300, movie2Doc, headers, MainViewModel.FormatBranchId(branch2.Id));
        Assert.IsNotNull(bestForBranch2);
        Assert.AreEqual(cp100Id, bestForBranch2.CheckpointId);

        var pruned = tree.Delete(branch1.Id);
        CollectionAssert.AreEqual(new[] { cp250Id }, pruned.ToArray());
        Assert.AreEqual(1, tree.Checkpoints.Count);
        Assert.AreEqual(cp100Id, tree.Checkpoints[0].CheckpointId);
    }

    [TestMethod]
    public async Task SequencePackage_PersistsTimelineAndCheckpoints_AndRestoresInNewStudioSession()
    {
        var root = Path.Combine(Path.GetTempPath(), "hktas-timeline-cp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var snapshot = new InitialSaveSnapshot(new Dictionary<string, byte[]> { ["user1.dat"] = new byte[] { 10, 20, 30 } });
            var movie1Doc = CreateMovie(400);
            var movie1Text = WriteCanonical(movie1Doc);
            var movie2Doc = MovieV2RangeEditor.Paint(movie1Doc, 220, 3, "Jump", true);
            var movie2Text = WriteCanonical(movie2Doc);

            var packagePath = Path.Combine(root, "session-with-checkpoints.hktaspack");
            string originalTreeId;
            string cp120Id = Id();

            using (var session1 = new StudioFixture(Path.Combine(root, "s1")))
            {
                SetField(session1.Vm, "sequenceInitialSaves", snapshot);
                session1.Vm.InitializeWorldlines();
                var store = GetField<StudioTimelineStore>(session1.Vm, "worldlines");
                store.Update(lib =>
                {
                    var tree = lib.Trees[0];
                    tree.InitialSavesId = snapshot.Id;
                    tree.OriginalHashes = new Dictionary<string, string>(snapshot.Hashes);
                    tree.Nodes[0].Movie = movie1Text;
                    var node100 = tree.Add(100, movie1Text, snapshot.Hashes);
                    var branch1 = tree.UpdateTip(320, movie1Text, snapshot.Hashes, null);
                    var branch2 = tree.UpdateTip(260, movie2Text, snapshot.Hashes, null);
                    Assert.AreEqual(node100.Id, branch2.ParentId);
                    Assert.AreNotEqual(branch1.Id, branch2.Id);
                    lib.ActiveTreeId = tree.Id;
                });

                originalTreeId = store.Library.ActiveTreeId;
                SetField(session1.Vm, "activeDraftTree", originalTreeId);
                SetField(session1.Vm, "movieText", movie2Text);

                var branchId = MainViewModel.FormatBranchId(0);
                var scope = session1.Vm.SceneCheckpoints.ActivateBranch(originalTreeId, branchId);
                var binding = new SceneCheckpointBinding(
                    new string('a', 64),
                    SceneCheckpointBinding.HashInitialSaves(snapshot.Hashes),
                    NativeExecutionProfile.CanonicalBoundaryProfileId);
                var pkt = CreateLeanS0Packet(session1.Vm.SceneCheckpoints.StudioSessionId, originalTreeId, branchId, scope.Generation, cp120Id, 1, 120, binding);

                var lease = session1.Vm.SceneCheckpoints.BeginCapture(scope, binding, SceneCheckpointTrigger.AutoOnLoadBoundary);
                // Match lease CaptureId by creating packet with lease.CaptureId
                cp120Id = lease.CaptureId;
                pkt = CreateLeanS0Packet(session1.Vm.SceneCheckpoints.StudioSessionId, originalTreeId, branchId, scope.Generation, cp120Id, 1, 120, binding);
                session1.Vm.SceneCheckpoints.Publish(lease, pkt);
                session1.Vm.RegisterCapturedCheckpointOnTimeline(originalTreeId, pkt);

                await session1.Vm.SaveSequenceToPathAsync(packagePath);
            }

            using (var session2 = new StudioFixture(Path.Combine(root, "s2")))
            {
                await session2.Vm.OpenMovieFileAsync(packagePath);

                var restoredTree = session2.Vm.SelectedTimelineTree;
                Assert.IsNotNull(restoredTree);
                Assert.AreEqual(originalTreeId, restoredTree.Id);
                Assert.AreEqual(4, restoredTree.Nodes.Count);
                Assert.AreEqual(2, restoredTree.Leaves.Count());
                Assert.AreEqual(1, restoredTree.Checkpoints.Count);
                Assert.AreEqual(cp120Id, restoredTree.Checkpoints[0].CheckpointId);

                var restoredPackets = session2.Vm.SceneCheckpoints.GetTreePackets(originalTreeId);
                Assert.AreEqual(1, restoredPackets.Count);
                Assert.AreEqual(session2.Vm.SceneCheckpoints.StudioSessionId, restoredPackets[0].Header.Scope.StudioSessionId);

                var best = session2.Vm.FindBestSceneCheckpoint(originalTreeId, 250, movie2Doc);
                Assert.IsNotNull(best);
                Assert.AreEqual(cp120Id, best.CheckpointId);
                Assert.AreEqual(session2.Vm.SceneCheckpoints.StudioSessionId, best.Scope.StudioSessionId);

                // Verify the imported packet is immediately stageable after activating its branch.
                var stageScope = session2.Vm.SceneCheckpoints.ActivateBranch(best.Scope.TreeId, best.Scope.BranchId);
                var staged = session2.Vm.SceneCheckpoints.Get(best.CheckpointId, stageScope, best.Binding);
                Assert.AreEqual(cp120Id, staged.Header.CheckpointId);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ForwardSeekSceneCheckpoint_SelectsNearestQualifiedCheckpointAheadOfCurrentFrame()
    {
        var root = Path.Combine(Path.GetTempPath(), "hktas-fwd-seek-cp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var baseDoc = CreateMovie(1000);
            var baseText = WriteCanonical(baseDoc);
            var baseline = new Dictionary<string, string> { ["user1.dat"] = new string('1', 64) };

            using var fixture = new StudioFixture(root);
            fixture.Vm.InitializeWorldlines();
            var store = GetField<StudioTimelineStore>(fixture.Vm, "worldlines");

            string treeId = string.Empty;
            int branchId = 0;
            store.Update(lib =>
            {
                var tree = lib.Trees[0];
                tree.InitialSavesId = new string('b', 64);
                tree.OriginalHashes = new Dictionary<string, string>(baseline);
                tree.Nodes[0].Movie = baseText;
                var tip = tree.UpdateTip(1000, baseText, baseline, null);
                treeId = lib.ActiveTreeId = tree.Id;
                branchId = tip.Id;
            });

            var branchHex = MainViewModel.FormatBranchId(branchId);
            var scope = fixture.Vm.SceneCheckpoints.ActivateBranch(treeId, branchHex);
            var binding = new SceneCheckpointBinding(
                new string('a', 64),
                SceneCheckpointBinding.HashInitialSaves(baseline),
                NativeExecutionProfile.CanonicalBoundaryProfileId);

            var cp100Id = Id();
            var cp300Id = Id();
            var cp800Id = Id();
            var packets = new[]
            {
                CreateLeanS0Packet(fixture.Vm.SceneCheckpoints.StudioSessionId, treeId, branchHex, scope.Generation, cp100Id, 1, 100, binding),
                CreateLeanS0Packet(fixture.Vm.SceneCheckpoints.StudioSessionId, treeId, branchHex, scope.Generation, cp300Id, 2, 300, binding),
                CreateLeanS0Packet(fixture.Vm.SceneCheckpoints.StudioSessionId, treeId, branchHex, scope.Generation, cp800Id, 3, 800, binding)
            };

            SetField(fixture.Vm, "activeDraftTree", treeId);
            SetField(fixture.Vm, "activeDraftTip", branchId);
            SetField(fixture.Vm, "movieText", baseText);

            foreach (var packet in packets)
            {
                fixture.Vm.SceneCheckpoints.ImportPacket(packet);
                fixture.Vm.RegisterCapturedCheckpointOnTimeline(treeId, packet);
            }

            // 1. From frame 50 to 950 -> selects K=800 (750 frames skipped >= 150).
            var hit800 = fixture.Vm.FindForwardSeekSceneCheckpoint(50, 950, baseDoc);
            Assert.IsNotNull(hit800);
            Assert.AreEqual(cp800Id, hit800.CheckpointId);

            // 2. From frame 50 to 450 -> selects K=300 (250 frames skipped >= 150).
            var hit300 = fixture.Vm.FindForwardSeekSceneCheckpoint(50, 450, baseDoc);
            Assert.IsNotNull(hit300);
            Assert.AreEqual(cp300Id, hit300.CheckpointId);

            // 3. From frame 200 to 450 -> K=300 only skips 100 frames (< 150 threshold) -> null (step forward directly).
            Assert.IsNull(fixture.Vm.FindForwardSeekSceneCheckpoint(200, 450, baseDoc));

            // 4. From frame 350 to 450 -> nearest K=300 is behind current frame -> null (never jump backward).
            Assert.IsNull(fixture.Vm.FindForwardSeekSceneCheckpoint(350, 450, baseDoc));

            // 5. Editing future input at F250 invalidates K=300 and K=800; K=100 skips only 50 frames -> null.
            var editedBefore300 = MovieV2RangeEditor.Paint(baseDoc, 250, 3, "Jump", true);
            Assert.IsNull(fixture.Vm.FindForwardSeekSceneCheckpoint(50, 950, editedBefore300));

            // 6. Editing future input at F850 (after K=800) preserves K=800 prefix -> selects K=800.
            var editedAfter800 = MovieV2RangeEditor.Paint(baseDoc, 850, 3, "Jump", true);
            var hitAfterEdit = fixture.Vm.FindForwardSeekSceneCheckpoint(50, 950, editedAfter800);
            Assert.IsNotNull(hitAfterEdit);
            Assert.AreEqual(cp800Id, hitAfterEdit.CheckpointId);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void FindBestSceneCheckpoint_FiltersOutMismatchedEnvironmentAndInitialSaves()
    {
        var root = Path.Combine(Path.GetTempPath(), "hktas-cp-filter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var baseDoc = CreateMovie(1000);
            var baseText = WriteCanonical(baseDoc);
            var snapshot = new InitialSaveSnapshot(new Dictionary<string, byte[]> { ["user1.dat"] = new byte[] { 10, 20, 30 } });
            var otherSnapshot = new InitialSaveSnapshot(new Dictionary<string, byte[]> { ["user1.dat"] = new byte[] { 40, 50, 60 } });
            var envNew = new string('1', 64);
            var envOld = new string('2', 64);
            var envAnother = new string('3', 64);

            using var fixture = new StudioFixture(root);
            SetField(fixture.Vm, "sequenceInitialSaves", snapshot);
            fixture.Vm.InitializeWorldlines();
            var store = GetField<StudioTimelineStore>(fixture.Vm, "worldlines");

            string treeId = string.Empty;
            int branchId = 0;
            store.Update(lib =>
            {
                var tree = lib.Trees[0];
                tree.InitialSavesId = snapshot.Id;
                tree.OriginalHashes = new Dictionary<string, string>(snapshot.Hashes);
                tree.Nodes[0].Movie = baseText;
                var tip = tree.UpdateTip(1000, baseText, snapshot.Hashes, null);
                treeId = lib.ActiveTreeId = tree.Id;
                branchId = tip.Id;
            });

            var branchHex = MainViewModel.FormatBranchId(branchId);
            var scope = fixture.Vm.SceneCheckpoints.ActivateBranch(treeId, branchHex);

            var cp300Id = Id();
            var cp800Id = Id();
            var cp900Id = Id();

            var pkt300 = CreateLeanS0Packet(
                fixture.Vm.SceneCheckpoints.StudioSessionId, treeId, branchHex, scope.Generation, cp300Id, 1, 300,
                new SceneCheckpointBinding(envNew, SceneCheckpointBinding.HashInitialSaves(snapshot.Hashes), NativeExecutionProfile.CanonicalBoundaryProfileId));
            var pkt800 = CreateLeanS0Packet(
                fixture.Vm.SceneCheckpoints.StudioSessionId, treeId, branchHex, scope.Generation, cp800Id, 2, 800,
                new SceneCheckpointBinding(envOld, SceneCheckpointBinding.HashInitialSaves(snapshot.Hashes), NativeExecutionProfile.CanonicalBoundaryProfileId));
            var pkt900 = CreateLeanS0Packet(
                fixture.Vm.SceneCheckpoints.StudioSessionId, treeId, branchHex, scope.Generation, cp900Id, 3, 900,
                new SceneCheckpointBinding(envNew, SceneCheckpointBinding.HashInitialSaves(otherSnapshot.Hashes), NativeExecutionProfile.CanonicalBoundaryProfileId));

            SetField(fixture.Vm, "activeDraftTree", treeId);
            SetField(fixture.Vm, "activeDraftTip", branchId);
            SetField(fixture.Vm, "movieText", baseText);

            foreach (var packet in new[] { pkt300, pkt800, pkt900 })
            {
                fixture.Vm.SceneCheckpoints.ImportPacket(packet);
                fixture.Vm.RegisterCapturedCheckpointOnTimeline(treeId, packet);
            }

            // Without known runtime environment: excludes K=900 (initial-save mismatch) and selects K=800.
            var withoutKnownEnv = fixture.Vm.FindBestSceneCheckpoint(treeId, 950, baseDoc);
            Assert.IsNotNull(withoutKnownEnv);
            Assert.AreEqual(cp800Id, withoutKnownEnv.CheckpointId);

            // With runtime environment set to envNew: EnvironmentSha256 mismatch is allowed with warning;
            // K=900 is still excluded by initial-save mismatch, so K=800 remains the latest compatible checkpoint.
            fixture.Vm.RememberRuntimeEnvironmentManifestSha256(envNew);
            var withEnvNew = fixture.Vm.FindBestSceneCheckpoint(treeId, 950, baseDoc);
            Assert.IsNotNull(withEnvNew);
            Assert.AreEqual(cp800Id, withEnvNew.CheckpointId);

            // With runtime environment set to envAnother: EnvironmentSha256 mismatch still allows K=800 (and K=300).
            fixture.Vm.RememberRuntimeEnvironmentManifestSha256(envAnother);
            var withEnvAnother = fixture.Vm.FindBestSceneCheckpoint(treeId, 950, baseDoc);
            Assert.IsNotNull(withEnvAnother);
            Assert.AreEqual(cp800Id, withEnvAnother.CheckpointId);

            // With an unrelated initial save snapshot: all checkpoints are still filtered out.
            var unrelatedSnapshot = new InitialSaveSnapshot(new Dictionary<string, byte[]> { ["user1.dat"] = new byte[] { 70, 80, 90 } });
            SetField(fixture.Vm, "sequenceInitialSaves", unrelatedSnapshot);
            Assert.IsNull(fixture.Vm.FindBestSceneCheckpoint(treeId, 950, baseDoc));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void RegisterCapturedCheckpointOnTimeline_RemovesSupersededStaleCheckpointPacket()
    {
        var root = Path.Combine(Path.GetTempPath(), "hktas-cp-supersede-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var baseDoc = CreateMovie(500);
            var baseText = WriteCanonical(baseDoc);
            var baseline = new Dictionary<string, string> { ["user1.dat"] = new string('1', 64) };
            var envOld = new string('a', 64);
            var envNew = new string('b', 64);

            using var fixture = new StudioFixture(root);
            fixture.Vm.InitializeWorldlines();
            var store = GetField<StudioTimelineStore>(fixture.Vm, "worldlines");

            string treeId = string.Empty;
            int branchId = 0;
            store.Update(lib =>
            {
                var tree = lib.Trees[0];
                tree.InitialSavesId = new string('c', 64);
                tree.OriginalHashes = new Dictionary<string, string>(baseline);
                tree.Nodes[0].Movie = baseText;
                var tip = tree.UpdateTip(500, baseText, baseline, null);
                treeId = lib.ActiveTreeId = tree.Id;
                branchId = tip.Id;
            });

            var branchHex = MainViewModel.FormatBranchId(branchId);
            var scope = fixture.Vm.SceneCheckpoints.ActivateBranch(treeId, branchHex);
            var savesSha = SceneCheckpointBinding.HashInitialSaves(baseline);

            var cpOldId = Id();
            var cpNewId = Id();
            var oldPacket = CreateLeanS0Packet(
                fixture.Vm.SceneCheckpoints.StudioSessionId, treeId, branchHex, scope.Generation, cpOldId, 2, 300,
                new SceneCheckpointBinding(envOld, savesSha, NativeExecutionProfile.CanonicalBoundaryProfileId));
            var newPacket = CreateLeanS0Packet(
                fixture.Vm.SceneCheckpoints.StudioSessionId, treeId, branchHex, scope.Generation, cpNewId, 2, 300,
                new SceneCheckpointBinding(envNew, savesSha, NativeExecutionProfile.CanonicalBoundaryProfileId));

            SetField(fixture.Vm, "activeDraftTree", treeId);
            SetField(fixture.Vm, "activeDraftTip", branchId);
            SetField(fixture.Vm, "movieText", baseText);

            fixture.Vm.SceneCheckpoints.ImportPacket(oldPacket);
            fixture.Vm.RegisterCapturedCheckpointOnTimeline(treeId, oldPacket);

            fixture.Vm.SceneCheckpoints.ImportPacket(newPacket);
            fixture.Vm.RegisterCapturedCheckpointOnTimeline(treeId, newPacket);

            var tree = store.Library.Trees.Single(t => t.Id == treeId);
            Assert.AreEqual(1, tree.Checkpoints.Count);
            Assert.AreEqual(cpNewId, tree.Checkpoints[0].CheckpointId);

            var remainingPackets = fixture.Vm.SceneCheckpoints.GetTreePackets(treeId);
            Assert.AreEqual(1, remainingPackets.Count);
            Assert.AreEqual(cpNewId, remainingPackets[0].Header.CheckpointId);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
        }
    }

    private sealed class StudioFixture : IDisposable
    {
        private readonly SessionRegistry sessions = new("timeline-cp-test");
        private readonly StartupBootController boot = new();
        private readonly AutomationBroker broker;
        public MainViewModel Vm { get; }

        public StudioFixture(string root)
        {
            Directory.CreateDirectory(root);
            var coordinator = new FullRunMovieCoordinator(boot);
            broker = new AutomationBroker(sessions, fullRunMovies: coordinator, automationDirectory: Path.Combine(root, "automation"));
            Vm = new MainViewModel(
                sessions,
                new MovieEditorService(),
                new CapabilityBroker(),
                new NativeHostLauncher(AppContext.BaseDirectory),
                broker,
                startupBoot: boot,
                fullRunMovies: coordinator,
                restartProtectedGame: () => throw new InvalidOperationException("Tests must not launch a game."));
            SetField(Vm, "initialSaveCacheRoot", Path.Combine(root, "baselines"));
            SetField(Vm, "worldlines", new StudioTimelineStore(Path.Combine(root, "timelines.json")));
        }

        public void Dispose()
        {
            Vm.SceneCheckpoints.Dispose();
            broker.Dispose();
            boot.Dispose();
            sessions.Dispose();
        }
    }
}
