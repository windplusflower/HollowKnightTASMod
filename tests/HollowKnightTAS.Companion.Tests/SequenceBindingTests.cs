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
using HollowKnightTAS.Core.Movie;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Companion.Tests;

[TestClass]
public sealed class SequenceBindingTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(object owner, string name, object? value) => owner.GetType().GetField(name, Flags)!.SetValue(owner, value);
    private static T Get<T>(object owner, string name) => (T)owner.GetType().GetField(name, Flags)!.GetValue(owner)!;
    private static Task Save(MainViewModel vm, string path) => (Task)typeof(MainViewModel).GetMethod("SaveSequenceToPathAsync", Flags)!.Invoke(vm, new object[] { path })!;
    private static string Movie() => new MovieV2Codec().WriteCanonical(new MovieV2Document("test",
        new MovieV2Header("game", "api", "mod", MovieProtocolV2.NativeProfileId, MovieProtocolV2.ActionSchemaId, false, new string('a', 64), 800, 450),
        new[] { new NativeFrameRun(500, Array.Empty<GameInputSample>(), new MovieSourceSpan("test", 1, 1, 1), 50, true) }));
    private static InitialSaveSnapshot Snapshot(byte value) => new(new Dictionary<string, byte[]> { ["user1.dat"] = new[] { value } });

    [TestMethod] public async Task OpenOfflineSaveAutosaveAndSaveAsPreserveBindingAndFutureDraft()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "portable.hktaspack");
        var snapshot = Snapshot(3);
        await SequencePackage.WriteAsync(path, Movie(), snapshot);
        await f.Vm.OpenMovieFileAsync(path);
        Assert.IsTrue(Get<bool>(f.Vm, "draftRequiresRestart"), "Opening before launch must not be replaced by automatic New.");
        Assert.AreEqual(snapshot.Id, f.Coordinator.SequenceInitialSaves!.Id);
        Assert.AreEqual(snapshot.Id, f.Vm.SelectedTimelineTree!.InitialSavesId);
        Assert.AreEqual(Movie(), f.Vm.MovieText);
        var saveAs = Path.Combine(f.Root, "copy.hktaspack");
        await Save(f.Vm, saveAs);
        Set(f.Vm, "activeSequenceDirectory", f.Root); Set(f.Vm, "activeAutoSaveSeconds", 1);
        Set(f.Vm, "nextSequenceAutoSave", DateTime.MinValue); Set(f.Vm, "autoSaveName", "bound");
        await f.Vm.AutoSaveSequenceAsync();
        foreach (var saved in new[] { path, saveAs, Path.Combine(f.Root, "Autosave", "sequence-bound.hktaspack") })
        {
            var result = SequencePackage.Read(saved);
            Assert.AreEqual(snapshot.Id, result.InitialSaves!.Id);
            Assert.AreEqual(Movie(), result.Movie);
        }
        var corrupt = Path.Combine(f.Root, "bad.hktaspack"); File.WriteAllText(corrupt, "bad");
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Vm.OpenMovieFileAsync(corrupt));
        Assert.AreEqual(snapshot.Id, Get<InitialSaveSnapshot>(f.Vm, "sequenceInitialSaves").Id);
        Assert.AreEqual(saveAs, Get<string>(f.Vm, "sequenceSavePath"));
        var legacy = Path.Combine(f.Root, "old.hktas"); File.WriteAllText(legacy, Movie());
        await f.Vm.OpenMovieFileAsync(legacy);
        Assert.IsNull(f.Coordinator.SequenceInitialSaves);
        StringAssert.Contains(f.Vm.SequenceBindingStatus, "未绑定");
    }

    [TestMethod] public async Task ManualSaveUpgradesLegacyProfileAndPreservesInputsAndInitialSaves()
    {
        using var f = new Fixture();
        var values = new short[26]; values[1] = 32767; values[10] = 32767;
        var expected = new MovieV2Codec().WriteCanonical(new MovieV2Document("test", TimelineTree.Parse(Movie()).Header.WithCustomKeys(new short[] { 282 }),
            new[] { new NativeFrameRun(1, new[] { new GameInputSample(GameInputChannel.Hero, values, null,
                    pressedMask: (1UL << 1) | (1UL << 10)), CustomKeyInput.Sample(282, true) }, new MovieSourceSpan("test", 2, 1, 1), 59.94m, true, 12345),
                new NativeFrameRun(499, Array.Empty<GameInputSample>(), new MovieSourceSpan("test", 3, 1, 1), 50, true) }));
        var legacy = expected.Replace(MovieProtocolV2.NativeProfileId,
            "hktas-unity-input-playerloop-load-elision-scene-rng-2026-v3");
        var original = Path.Combine(f.Root, "legacy.hktaspack");
        var snapshot = Snapshot(7);
        await SequencePackage.WriteAsync(original, legacy, snapshot);
        await f.Vm.OpenMovieFileAsync(original);
        Assert.AreEqual(legacy, f.Vm.MovieText, "Opening alone must not migrate the source.");
        Set(f.Vm, "activeSequenceDirectory", f.Root); Set(f.Vm, "activeAutoSaveSeconds", 1);
        Set(f.Vm, "nextSequenceAutoSave", DateTime.MinValue); Set(f.Vm, "autoSaveName", "legacy");
        await f.Vm.AutoSaveSequenceAsync();
        Assert.AreEqual(legacy, SequencePackage.Read(Path.Combine(f.Root, "Autosave", "sequence-legacy.hktaspack")).Movie);

        var copy = Path.Combine(f.Root, "new.hktaspack");
        await Save(f.Vm, copy);
        Assert.AreEqual(expected, SequencePackage.Read(copy).Movie, "Inputs, edges, fractional FPS and RNG seed must survive.");
        Assert.AreEqual(snapshot.Id, SequencePackage.Read(copy).InitialSaves!.Id);
        Assert.AreEqual(legacy, f.Vm.MovieText, "Saving must not change the live timeline identity.");
        Assert.AreEqual(legacy, SequencePackage.Read(original).Movie, "Save As preserves the source file.");

        await f.Vm.OpenMovieFileAsync(original);
        await Save(f.Vm, original);
        Assert.AreEqual(expected, SequencePackage.Read(original).Movie, "Ordinary save also upgrades the profile.");
        Assert.AreEqual(snapshot.Id, SequencePackage.Read(original).InitialSaves!.Id);
    }

    [TestMethod] public async Task OpeningBoundSequenceAtFrameZeroRequiresFreshShadowEvenWhenUnarmed()
    {
        using var f = new Fixture(); f.SetFrameZero();
        var path = Path.Combine(f.Root, "different.hktaspack");
        await SequencePackage.WriteAsync(path, Movie(), Snapshot(8));
        await f.Vm.OpenMovieFileAsync(path);
        Assert.AreEqual("Unarmed", f.Coordinator.Mode);
        Assert.IsTrue(Get<bool>(f.Vm, "draftRequiresRestart"));
        Assert.Throws<InvalidOperationException>(() => f.Coordinator.ArmReplay(TimelineTree.Parse(Movie())));
        Assert.AreEqual(0L, f.Boot.NativeCompletedFrames);
    }

    [TestMethod] public async Task NewSequenceSavesFrameZeroSnapshotAfterShadowHasChanged()
    {
        using var f = new Fixture(); f.SetFrameZero();
        f.Vm.NewFullRunMovieCommand.Execute(null);
        StringAssert.Contains(f.Vm.Status, "录制已预置");
        var initial = f.Coordinator.SessionInitialSaves!.Id;
        File.WriteAllBytes(Path.Combine(f.Coordinator.ShadowRoot, "user1.dat"), new byte[] { 99 });
        var path = Path.Combine(f.Root, "new.hktaspack");
        await Save(f.Vm, path);
        Assert.AreEqual(initial, SequencePackage.Read(path).InitialSaves!.Id);
        f.Coordinator.VerifyOriginalSavesUnchanged();
        Assert.AreEqual(0L, f.Boot.NativeCompletedFrames);
    }

    [TestMethod] public async Task TimelineSwitchRestoresItsOwnBaselineWithoutDependingOnPackagePath()
    {
        using var f = new Fixture();
        var first = Path.Combine(f.Root, "first.hktaspack"); var second = Path.Combine(f.Root, "second.hktaspack");
        await SequencePackage.WriteAsync(first, Movie(), Snapshot(1));
        await SequencePackage.WriteAsync(second, Movie(), Snapshot(2));
        await f.Vm.OpenMovieFileAsync(first);
        var treeId = f.Vm.SelectedTimelineTreeId;
        await f.Vm.OpenMovieFileAsync(second);
        File.Move(first, first + ".moved");
        var method = typeof(MainViewModel).GetMethod("SwitchWorldlineAsync", Flags)!;
        await (Task)method.Invoke(f.Vm, new object[] { treeId!, 0 })!;
        Assert.AreEqual(Snapshot(1).Id, f.Coordinator.SequenceInitialSaves!.Id);
        Assert.IsTrue(Get<bool>(f.Vm, "draftRequiresRestart"));
        Assert.IsNull(Get<string?>(f.Vm, "sequenceSavePath"));
    }

    [TestMethod]
    [DataRow(41)]
    [DataRow(0)]
    public async Task TerminalSessionCanSaveDraftAndOpenAnotherSequenceWithoutNativeCommand(int fault)
    {
        using var f = new Fixture();
        f.SetFrameZero();
        var first = Path.Combine(f.Root, "first.hktas");
        var second = Path.Combine(f.Root, "second.hktaspack");
        await File.WriteAllTextAsync(first, Movie());
        await SequencePackage.WriteAsync(second, Movie(), Snapshot(2));
        await f.Vm.OpenMovieFileAsync(first);
        var tree = f.Vm.SelectedTimelineTree!;
        Assert.IsTrue(f.Vm.TrySetGridRngSeed(4, "123"));
        var draft = f.Vm.MovieText;
        f.SetTerminal(fault);
        var commands = f.CommandSequence;
        await f.Vm.OpenMovieFileAsync(second);
        Assert.AreEqual(commands, f.CommandSequence, "Document switch must not command a terminal gate.");
        tree = Get<StudioTimelineStore>(f.Vm, "worldlines").Library.Trees.Single(t => t.Id == tree.Id);
        Assert.IsTrue(tree.Nodes.Any(n => n.Movie == draft), "Keep the old editable draft.");
        Assert.IsTrue(tree.Nodes.All(n => n.Frame == 0), "Do not certify the failed/completed native position without a snapshot.");
        Assert.AreEqual(Snapshot(2).Id, f.Coordinator.SequenceInitialSaves!.Id);
        Assert.IsTrue(Get<bool>(f.Vm, "draftRequiresRestart"));
        Assert.IsTrue(f.Vm.TogglePauseCommand.CanExecute(null));
        Assert.IsTrue(f.Vm.StepCommand.CanExecute(null));
        Assert.AreEqual("Play 从起点开始", f.Vm.PlayPauseLabel);
    }

    [TestMethod] public async Task EditedSaveStartsSeparateTimelineAndPreservesOriginalPackage()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "original.hktaspack");
        var original = Snapshot(1);
        await SequencePackage.WriteAsync(path, Movie(), original);
        var originalBytes = File.ReadAllBytes(path);
        await f.Vm.OpenMovieFileAsync(path);
        var oldTree = f.Vm.SelectedTimelineTree!;
        await f.Vm.ApplyEditedInitialSavesAsync(original, Snapshot(2));
        Assert.AreNotEqual(oldTree.Id, f.Vm.SelectedTimelineTree!.Id);
        Assert.AreEqual(original.Id, oldTree.InitialSavesId);
        Assert.AreEqual(Snapshot(2).Id, f.Vm.SelectedTimelineTree.InitialSavesId);
        Assert.IsTrue(Get<bool>(f.Vm, "draftRequiresRestart"));
        Assert.IsNull(Get<string?>(f.Vm, "sequenceSavePath"));
        Assert.AreEqual(Movie(), f.Vm.MovieText);
        var editedPath = Path.Combine(f.Root, "edited.hktaspack");
        await Save(f.Vm, editedPath);
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(path));
        Assert.AreEqual(Snapshot(2).Id, SequencePackage.Read(editedPath).InitialSaves!.Id);
        Assert.Throws<InvalidOperationException>(() => f.Vm.ApplyEditedInitialSavesAsync(original, Snapshot(3)));
    }

    [TestMethod] public void SaveJsonWindowRendersEditableTextAndRejectsInvalidJson()
    {
        WpfTestApplication.Run(() =>
        {
            SequenceSavesWindow? window = null;
            try
            {
                using var f = new Fixture();
                f.Vm.MovieText = Movie();
                Set(f.Vm, "sequenceInitialSaves", new InitialSaveSnapshot(new Dictionary<string, byte[]> {
                    ["user1.dat"] = System.Text.Encoding.UTF8.GetBytes("{\"playerData\":{\"geo\":123},\"sceneData\":{}}") }));
                window = new SequenceSavesWindow(f.Vm);
                var panel = (System.Windows.Controls.DockPanel)window.Content;
                var editor = panel.Children.OfType<System.Windows.Controls.TextBox>().Single();
                var status = panel.Children.OfType<System.Windows.Controls.TextBlock>().Last();
                var save = panel.Children.OfType<System.Windows.Controls.StackPanel>().Single().Children
                    .OfType<System.Windows.Controls.Button>().Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "HktasStudio.ApplySaveJson");
                window.Show(); window.UpdateLayout();
                Assert.IsFalse(editor.IsReadOnly);
                Assert.IsTrue(editor.ActualHeight > 400);
                StringAssert.Contains(editor.Text, "123");
                var originalText = editor.Text;
                editor.Text = "{";
                save.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                StringAssert.Contains(status.Text, "JSON 保存失败");
                Assert.IsTrue(window.IsVisible);
                editor.Text = originalText;
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight,
                    96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(window);
                var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (var output = File.Create(Path.Combine(Path.GetTempPath(), "hktas-save-json-window.png"))) png.Save(output);
                editor.Text = originalText.Replace("123", "456");
                save.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.IsFalse(window.IsVisible);
                var exported = SaveJsonDocument.Export(f.Vm.SequenceInitialSaves!, f.Root);
                StringAssert.Contains(new SaveJsonDocument(File.ReadAllBytes(Path.Combine(exported, "user1.dat"))).Json, "456");
            }
            finally { window?.Close(); }
        }, TimeSpan.FromSeconds(15), loadStudioTheme: true);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "hktas-binding-" + Guid.NewGuid().ToString("N"));
        private readonly SessionRegistry sessions = new("binding-test");
        public StartupBootController Boot { get; } = new();
        private readonly AutomationBroker broker;
        public FullRunMovieCoordinator Coordinator { get; }
        public MainViewModel Vm { get; }
        private MemoryMappedFile? mapping;
        private MemoryMappedViewAccessor? view;
        private EventWaitHandle? ready;
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Coordinator = new FullRunMovieCoordinator(Boot);
            broker = new AutomationBroker(sessions, fullRunMovies: Coordinator, automationDirectory: Path.Combine(Root, "automation"));
            Vm = new MainViewModel(sessions, new MovieEditorService(), new CapabilityBroker(), new NativeHostLauncher(AppContext.BaseDirectory), broker,
                startupBoot: Boot, fullRunMovies: Coordinator,
                restartProtectedGame: () => throw new InvalidOperationException("Tests must not launch a game."));
            Set(Vm, "initialSaveCacheRoot", Path.Combine(Root, "baselines"));
            Set(Vm, "worldlines", new StudioTimelineStore(Path.Combine(Root, "timelines.json")));
        }
        public void SetFrameZero()
        {
            var gate = Boot.BeginV2();
            var original = Path.Combine(Root, "original"); Directory.CreateDirectory(original);
            File.WriteAllBytes(Path.Combine(original, "user1.dat"), new byte[] { 1 });
            var saves = ProtectedSaveSession.Prepare("binding-test", original, Path.Combine(Root, "shadow"));
            gate.SetProtectedSaveSession(saves);
            Set(Coordinator, "gate", gate); Set(Coordinator, "saves", saves);
            Set(Coordinator, "bootstrap", new FullRunBootstrapStore(Path.Combine(Root, "boot")));
            mapping = MemoryMappedFile.OpenExisting("Local\\HKTAS.Boot." + gate.Token + ".V2State");
            view = mapping.CreateViewAccessor(); ready = EventWaitHandle.OpenExisting("Local\\HKTAS.Boot." + gate.Token + ".Ready");
            view.Write(92, 1); view.Write(76, 0); view.Write(40, 0L); ready.Set(); Boot.Refresh();
        }
        public long CommandSequence => view!.ReadInt64(48);
        public void SetTerminal(int fault)
        {
            view!.Write(40, 3563L);
            view.Write(88, fault);
            view.Write(76, fault == 0 ? 4 : 3);
            Boot.Refresh();
        }
        public void Dispose()
        {
            ready?.Dispose(); view?.Dispose(); mapping?.Dispose(); broker.Dispose(); Boot.Dispose(); sessions.Dispose();
            if (!Path.GetFullPath(Root).StartsWith(Path.Combine(Path.GetTempPath(), "hktas-binding-"), StringComparison.OrdinalIgnoreCase)) throw new Exception();
            foreach (var path in Directory.GetFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }
}
