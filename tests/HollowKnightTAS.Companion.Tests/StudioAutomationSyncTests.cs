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
            var request = Guid.NewGuid().ToString("N");
            LastRequestId = request;
            return broker.StudioFullRunCommand!(new AutomationCommandEnvelope(request, request, "test-ai", "sync-test", new string('a', 64),
                id, scope, "", id == AutomationCommandIds.FullRunPause ? "Running" : "Paused", null, IpcPayloadCodec.Serialize(args)), Execute);
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
