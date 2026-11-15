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
