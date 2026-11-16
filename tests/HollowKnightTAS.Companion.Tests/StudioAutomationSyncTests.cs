using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Companion.Automation;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.ViewModels;
using HollowKnightTAS.Core.Automation;
using HollowKnightTAS.Core.Ipc;
using HollowKnightTAS.Core.Movie;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Companion.Tests;

[TestClass]
public sealed class StudioAutomationSyncTests
{
    [TestMethod]
    public async Task SnapshotIncludesUnappliedHumanEditsAndAIUpdateSupportsUndoAndFurtherEditing()
    {
        using var h = new Harness();
        h.Vm.PaintGrid(5, 5, "Attack", true);
        var human = h.Vm.MovieText;
        var snapshot = await h.Run(AutomationCommandIds.FullRunSnapshot);
        Assert.IsTrue(snapshot.Success, snapshot.Detail);
        Assert.AreEqual(human, File.ReadAllText(snapshot.Data["path"]));
        var changed = MovieV2RangeEditor.Paint(h.Parse(human), 7, 1, "Jump", true);
        var aiText = new MovieV2Codec().WriteCanonical(changed);
        var update = await h.Update(aiText);
        Assert.IsTrue(update.Success, update.Detail);
        Assert.AreEqual(aiText, h.Vm.MovieText);
        Assert.AreEqual(10, h.Vm.InputRows.Count);
        h.Vm.UndoGridCommand.Execute(null);
        Assert.AreEqual(human, h.Vm.MovieText);
        h.Vm.RedoGridCommand.Execute(null);
        Assert.AreEqual(aiText, h.Vm.MovieText);
        h.Vm.PaintGrid(8, 8, "Dash", true);
        Assert.AreNotEqual(aiText, h.Vm.MovieText);
        Assert.IsTrue(h.Vm.IsInputGridInteractive);
    }

    [TestMethod]
    public async Task HumanEditAfterSnapshotRejectsAIOverwriteBeforeRuntimeMutation()
    {
        using var h = new Harness();
        await h.Run(AutomationCommandIds.FullRunSnapshot);
        h.Vm.PaintGrid(5, 5, "Attack", true);
        var human = h.Vm.MovieText;
        var result = await h.Update(h.Original);
        Assert.AreEqual("StudioDraftChanged", result.ResultCode);
        Assert.AreEqual(0, h.Updates);
        Assert.AreEqual(human, h.Vm.MovieText);
    }

    [TestMethod]
    public async Task FailedRuntimeUpdateLeavesStudioAndUndoHistoryUnchanged()
    {
        using var h = new Harness();
        h.RejectUpdate = true;
        var ai = new MovieV2Codec().WriteCanonical(MovieV2RangeEditor.Paint(h.Parse(h.Original), 6, 1, "Jump", true));
        var result = await h.Update(ai);
        Assert.IsFalse(result.Success);
        Assert.AreEqual(h.Original, h.Vm.MovieText);
        Assert.IsTrue(h.Vm.IsInputGridInteractive);
    }

    [TestMethod]
    public async Task ExternalStepAppliesHumanFutureDraftBeforeSteppingAndRefreshesPosition()
    {
        using var h = new Harness();
        h.Vm.PaintGrid(6, 6, "Jump", true);
        h.Vm.UndoGridCommand.Execute(null);
        h.Vm.RedoGridCommand.Execute(null);
        var draft = h.Vm.MovieText;
        var result = await h.Run(AutomationCommandIds.FullRunStep);
        Assert.IsTrue(result.Success, result.Detail);
        Assert.AreEqual(draft, h.RuntimeText);
        Assert.IsTrue(h.Calls.IndexOf(AutomationCommandIds.FullRunUpdateMovie) < h.Calls.IndexOf(AutomationCommandIds.FullRunStep));
        Assert.AreEqual(3L, h.Field<long>("currentFullRunMovieFrame"));
        Assert.IsFalse(h.Field<bool>("gridHasUserEdits"));
    }

    [TestMethod]
    public async Task PauseInRecordingPreservesPendingHumanEdits()
    {
        using var h = new Harness("Recording");
        h.Vm.PaintGrid(6, 6, "Jump", true);
        var draft = h.Vm.MovieText;
        var result = await h.Run(AutomationCommandIds.FullRunPause);
        Assert.IsTrue(result.Success, result.Detail);
        Assert.AreEqual(draft, h.Vm.MovieText);
        Assert.IsTrue(h.Field<bool>("gridHasUserEdits"));
        Assert.AreEqual(6L, h.Field<long>("earliestGridEdit"));
    }

    [TestMethod]
    public async Task ExternalUpdateDrainsBackgroundSaveWhileHoldingEditor()
    {
        using var h = new Harness();
        h.Set("savingBranch", true);
        var pending = h.Update(h.Original);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            await dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        Assert.IsFalse(pending.IsCompleted);
        Assert.IsFalse(h.Vm.IsInputGridInteractive);
        h.Set("savingBranch", false);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(result.Success, result.Detail);
        Assert.IsTrue(h.Vm.IsInputGridInteractive);
    }

    [TestMethod]
    public async Task FailedAutomaticDraftApplyKeepsOriginalRequestIdentityAndPendingEdits()
    {
        using var h = new Harness();
        h.Vm.PaintGrid(6, 6, "Jump", true);
        h.RejectUpdate = true;
        var result = await h.Run(AutomationCommandIds.FullRunStep);
        Assert.IsFalse(result.Success);
        Assert.AreEqual(h.LastRequestId, result.RequestId, "SDK must receive the result under the caller's request ID.");
        Assert.IsTrue(h.Field<bool>("gridHasUserEdits"));
        Assert.IsFalse(h.Calls.Contains(AutomationCommandIds.FullRunStep));
    }

    [TestMethod]
    public async Task BusyStudioRejectsExternalMutationAndPastEditsRequireReplay()
    {
        using var h = new Harness();
        h.Set("gridApplying", true);
        Assert.AreEqual("StudioBusy", (await h.Run(AutomationCommandIds.FullRunStep)).ResultCode);
        Assert.AreEqual(0, h.Calls.Count);
        h.Set("gridApplying", false);
        h.Vm.PaintGrid(0, 0, "Jump", true);
        Assert.AreEqual("StudioReplayRequired", (await h.Run(AutomationCommandIds.FullRunPlay)).ResultCode);
        Assert.AreEqual(0, h.Updates);
    }

    [TestMethod]
    public async Task StudioEditInputsGetMovieTimelineSaveNodeAndBeginReplayInitialSavesWork()
    {
        using var h = new Harness();
        var paint = await h.RunCustom(AutomationCommandIds.StudioEditInputs, AutomationScope.ControlInput, new Dictionary<string, string>
        {
            ["operation"] = "paint",
            ["startFrame"] = "3",
            ["endFrame"] = "5",
            ["action"] = "Jump",
            ["held"] = "true"
        });
        Assert.IsTrue(paint.Success, paint.Detail);

        var setRuns = await h.RunCustom(AutomationCommandIds.StudioEditInputs, AutomationScope.ControlInput, new Dictionary<string, string>
        {
            ["operation"] = "set_runs",
            ["startFrame"] = "6",
            ["runs"] = "[{\"frames\":3,\"actions\":[\"Right\",\"Attack\"]}]"
        });
        Assert.IsTrue(setRuns.Success, setRuns.Detail);

        var insert = await h.RunCustom(AutomationCommandIds.StudioEditInputs, AutomationScope.ControlInput, new Dictionary<string, string>
        {
            ["operation"] = "insert_frames",
            ["startFrame"] = "9",
            ["count"] = "2"
        });
        Assert.IsTrue(insert.Success, insert.Detail);
        Assert.AreEqual("12", insert.Data["totalFrames"]);

        var del = await h.RunCustom(AutomationCommandIds.StudioEditInputs, AutomationScope.ControlInput, new Dictionary<string, string>
        {
            ["operation"] = "delete_frames",
            ["startFrame"] = "9",
            ["count"] = "2"
        });
        Assert.IsTrue(del.Success, del.Detail);
        Assert.AreEqual("10", del.Data["totalFrames"]);

        var movie = await h.RunCustom(AutomationCommandIds.StudioGetMovie, AutomationScope.MovieRead, new Dictionary<string, string>
        {
            ["startFrame"] = "0",
            ["frameCount"] = "10",
            ["includeFullText"] = "true"
        });
        Assert.IsTrue(movie.Success, movie.Detail);
        Assert.AreEqual("10", movie.Data["totalFrames"]);
        Assert.IsTrue(h.Vm.InputRows[3].HasAction("Jump"));
        Assert.IsTrue(h.Vm.InputRows[5].HasAction("Jump"));
        Assert.IsTrue(h.Vm.InputRows[6].HasAction("Right"));
        Assert.IsTrue(h.Vm.InputRows[6].HasAction("Attack"));
        Assert.IsTrue(h.Vm.InputRows[8].HasAction("Right"));
        Assert.IsTrue(h.Vm.InputRows[8].HasAction("Attack"));

        // Verify BeginFullRunReplay binds SessionInitialSaves before calling ArmReplay even if a stale SequenceInitialSaves was set,
        // and verify StudioSaveTimelineNode + StudioGetTimeline at frame 0.
        h.ResetToFrameZeroWithStaleSequenceSaves();
        var saveNode = await h.RunCustom(AutomationCommandIds.StudioSaveTimelineNode, AutomationScope.ControlReplaySave, new Dictionary<string, string>
        {
            ["frame"] = "0"
        });
        Assert.IsTrue(saveNode.Success, saveNode.Detail);
        Assert.IsFalse(string.IsNullOrEmpty(saveNode.Data["nodeId"]));

        var timeline = await h.RunCustom(AutomationCommandIds.StudioGetTimeline, AutomationScope.ObserveStatus);
        Assert.IsTrue(timeline.Success, timeline.Detail);
        Assert.IsTrue(int.Parse(timeline.Data["nodeCount"]) >= 2);
        Assert.IsTrue(timeline.Data["timelineJson"].Contains("Frame 0", StringComparison.Ordinal));

        var beginReplay = await h.RunCustom(AutomationCommandIds.BeginFullRunReplay, AutomationScope.ControlPlayback, new Dictionary<string, string>
        {
            ["expectedNativeFrame"] = "0"
        });
        Assert.IsTrue(beginReplay.Success, beginReplay.Detail);
    }

    [TestMethod]
    public async Task SetRunsBulkFramesAndCompletedGuardWorkCleanly()
    {
        using var h = new Harness();
        var bulk = await h.RunCustom(AutomationCommandIds.StudioEditInputs, AutomationScope.ControlInput, new Dictionary<string, string>
        {
            ["operation"] = "set_runs",
            ["startFrame"] = "4",
            ["runs"] = "[{\"frames\":60,\"actions\":[\"Right\"]}]"
        });
        Assert.IsTrue(bulk.Success, bulk.Detail);
        Assert.AreEqual(64L, long.Parse(bulk.Data["totalFrames"]));
        Assert.IsFalse(h.Vm.SuppressBulkGridSelection);

        h.MarkCompletedAt(10);
        h.Vm.MovieText = h.RuntimeText;
        h.Vm.RefreshGridCommand.Execute(null);
        var noFutureStep = await h.RunCustom(AutomationCommandIds.FullRunStep, AutomationScope.ControlStep, new Dictionary<string, string>
        {
            ["expectedNativeFrame"] = "10"
        });
        Assert.IsFalse(noFutureStep.Success);
        Assert.AreEqual("PreconditionFailed", noFutureStep.ResultCode);
    }

    private sealed class Harness : IDisposable
    {
        private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly string root = Path.Combine(Path.GetTempPath(), "hktas-studio-sync-" + Guid.NewGuid().ToString("N"));
        private readonly SessionRegistry sessions = new("studio-sync-test");
        private readonly StartupBootController boot = new();
        private readonly FullRunMovieCoordinator coordinator;
        private readonly AutomationBroker broker;
        private readonly MemoryMappedFile mapping;
        private readonly MemoryMappedViewAccessor view;
        private readonly EventWaitHandle ready;
        public MainViewModel Vm { get; }
        public string Original { get; }
        public string RuntimeText { get; private set; }
        public bool RejectUpdate { get; set; }
        public int Updates { get; private set; }
        public string LastRequestId { get; private set; } = "";
        public List<string> Calls { get; } = new();
        private long frame = 2;

        public Harness(string mode = "Replay")
        {
            Directory.CreateDirectory(root);
            coordinator = new FullRunMovieCoordinator(boot);
            broker = new AutomationBroker(sessions, automationDirectory: Path.Combine(root, "automation"), fullRunMovies: coordinator);
            Vm = new MainViewModel(sessions, new MovieEditorService(), new CapabilityBroker(), new NativeHostLauncher(AppContext.BaseDirectory),
                broker, startupBoot: boot, fullRunMovies: coordinator);
            var gate = boot.BeginV2();
            typeof(FullRunMovieCoordinator).GetField("gate", Flags)!.SetValue(coordinator, gate);
            typeof(FullRunMovieCoordinator).GetField("mode", Flags)!.SetValue(coordinator, mode);
            typeof(FullRunMovieCoordinator).GetField("saves", Flags)!.SetValue(coordinator,
                ProtectedSaveSession.Prepare("sync-test", Path.Combine(root, "original"), Path.Combine(root, "shadow")));
            mapping = MemoryMappedFile.OpenExisting("Local\\HKTAS.Boot." + gate.Token + ".V2State");
            view = mapping.CreateViewAccessor();
            ready = EventWaitHandle.OpenExisting("Local\\HKTAS.Boot." + gate.Token + ".Ready");
            view.Write(92, 1); view.Write(76, 0); view.Write(40, 2L); ready.Set(); boot.Refresh();
            var header = new MovieV2Header("game", "api", "mod", MovieProtocolV2.NativeProfileId, MovieProtocolV2.ActionSchemaId, false, "none", 0, 0);
            Original = RuntimeText = new MovieV2Codec().WriteCanonical(new MovieV2Document("test", header,
                new[] { new NativeFrameRun(10, Array.Empty<GameInputSample>(), new MovieSourceSpan("test", 1, 1, 1), 50, true) }));
            Vm.MovieText = Original;
            Vm.RefreshGridCommand.Execute(null);
            Set("currentFullRunMovieFrame", 2L);
        }

        public MovieV2Document Parse(string text) => new MovieV2Codec().Parse(new StringReader(text), "test").Document!;
        public T Field<T>(string name) => (T)typeof(MainViewModel).GetField(name, Flags)!.GetValue(Vm)!;
        public void Set(string name, object value) => typeof(MainViewModel).GetField(name, Flags)!.SetValue(Vm, value);
        public void MarkCompletedAt(long completedFrame)
        {
            frame = completedFrame;
            view.Write(92, 1); view.Write(76, 4); view.Write(40, completedFrame); ready.Set(); boot.Refresh();
        }
        public Task<AutomationResultEnvelope> Update(string text)
        {
            var path = Path.Combine(coordinator.ShadowRoot, "candidate.hktas");
            File.WriteAllText(path, text);
            return Run(AutomationCommandIds.FullRunUpdateMovie, path);
        }
        public Task<AutomationResultEnvelope> Run(string id, string? path = null)
        {
            var args = new Dictionary<string, string>();
            if (id != AutomationCommandIds.FullRunSnapshot) args["expectedNativeFrame"] = "2";
            if (path != null) args["moviePath"] = path;
            var scope = id == AutomationCommandIds.FullRunSnapshot ? AutomationScope.MovieRead
                : id == AutomationCommandIds.FullRunStep ? AutomationScope.ControlStep : AutomationScope.ControlPlayback;
            return RunCustom(id, scope, args, id == AutomationCommandIds.FullRunPause ? "Running" : "Paused");
        }
        public Task<AutomationResultEnvelope> RunCustom(string id, string scope, Dictionary<string, string>? args = null, string mode = "Paused")
        {
            var request = Guid.NewGuid().ToString("N");
            LastRequestId = request;
            return broker.StudioFullRunCommand!(new AutomationCommandEnvelope(request, request, "test-ai", "sync-test", new string('a', 64),
                id, scope, "", mode, null, IpcPayloadCodec.Serialize(args ?? new Dictionary<string, string>())), Execute);
        }
        public void ResetToFrameZeroWithStaleSequenceSaves()
        {
            var stale = new InitialSaveSnapshot(new Dictionary<string, byte[]> { ["user1.dat"] = new byte[] { 1, 2, 3 } });
            coordinator.SequenceInitialSaves = stale;
            typeof(FullRunMovieCoordinator).GetField("mode", Flags)!.SetValue(coordinator, "Unarmed");
            view.Write(40, 0L);
            ready.Set();
            boot.Refresh();
            frame = 0;
        }
        private Task<AutomationResultEnvelope> Execute(AutomationCommandEnvelope command)
        {
            Calls.Add(command.CommandId);
            var data = new Dictionary<string, string>();
            var success = true;
            if (command.CommandId == AutomationCommandIds.FullRunSnapshot)
            {
                var path = Path.Combine(coordinator.ShadowRoot, "runtime.hktas");
                File.WriteAllText(path, RuntimeText);
                data["path"] = path; data["movieFrame"] = frame.ToString();
            }
            if (command.CommandId == AutomationCommandIds.FullRunUpdateMovie)
            {
                Assert.IsTrue(Path.GetFullPath(command.Arguments["moviePath"]).StartsWith(
                    Path.Combine(coordinator.ShadowRoot, "HollowKnightTAS") + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase), "Staged input must satisfy Runtime's session-root boundary.");
                Updates++; success = !RejectUpdate;
                if (success) RuntimeText = File.ReadAllText(command.Arguments["moviePath"]);
            }
            if (command.CommandId == AutomationCommandIds.BeginFullRunReplay)
            {
                Assert.AreEqual(coordinator.SessionInitialSaves?.Id, coordinator.SequenceInitialSaves?.Id);
            }
            if (command.CommandId == AutomationCommandIds.FullRunStep) frame++;
            if (command.CommandId == AutomationCommandIds.FullRunStatus)
            {
                data["movieFrame"] = frame.ToString(); data["mode"] = coordinator.Mode;
            }
            return Task.FromResult(new AutomationResultEnvelope(command.RequestId, success, success ? "Ok" : "PreconditionFailed", "test", command.SessionId,
                command.ManifestSha256, -1, IpcPayloadCodec.Serialize(data)));
        }
        public void Dispose()
        {
            ready.Dispose(); view.Dispose(); mapping.Dispose(); broker.Dispose(); boot.Dispose(); sessions.Dispose();
            var full = Path.GetFullPath(root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(full, true);
        }
    }
}
