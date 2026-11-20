using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.ViewModels;

internal static partial class StudioScenarioHarness
{
    static async Task RunSequenceBindingAsync()
    {
        var vm = (MainViewModel)app.MainWindow.DataContext;
        var boot = Field<StartupBootController>(app, "startupBoot");
        var coordinator = Field<FullRunMovieCoordinator>(app, "fullRunMovies");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(MainViewModel).GetField(name, flags)!.SetValue(vm, value);
        Set("worldlines", new StudioTimelineStore(Path.Combine(output, "timelines.json")));
        Set("initialSaveCacheRoot", Path.Combine(output, "initial-saves"));
        Set("activeSequenceDirectory", output);
        async Task Save(string path) => await (Task)typeof(MainViewModel).GetMethod("SaveSequenceToPathAsync", flags)!.Invoke(vm, new object[] { path })!;
        var launch = Field<Func<string, Task>>(vm, "launchGame");
        await launch(@"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe");
        vm.NewFullRunMovieCommand.Execute(null);
        Require(coordinator.SequenceInitialSaves != null, "new sequence binds startup save set");
        var initialId = coordinator.SequenceInitialSaves!.Id;
        await Command(vm.StepCommand);
        await Until(() => vm.SelectedSession != null, "Runtime connected");
        await vm.PollInputGridProgressAsync();
        await vm.FrameMenuAsync("seek", 120);
        AtFrame(vm, boot, 120, "initial recording reaches frame 120");
        await vm.SaveCurrentBranchAsync();
        var package = Path.Combine(output, "recording.hktaspack");
        var nativeBefore = boot.NativeCompletedFrames;
        await Save(package);
        Require(boot.NativeCompletedFrames == nativeBefore, "saving package does not advance or restore game");
        var original = SequencePackage.Read(package);
        Require(original.InitialSaves!.Id == initialId, "first save contains frame-zero baseline");

        // Deliberately mutate only this disposable shadow while paused, then discard
        // the process on replay. This is an isolation test, not a gameplay fixture.
        File.WriteAllBytes(Path.Combine(coordinator.ShadowRoot, "user4.dat"), new byte[] { 8, 7, 6 });
        await Save(package);
        Require(SequencePackage.Read(package).InitialSaves!.Id == initialId, "resave ignores mutated shadow");
        Set("activeAutoSaveSeconds", 1); Set("nextSequenceAutoSave", DateTime.MinValue); Set("autoSaveName", "binding-smoke");
        await vm.AutoSaveSequenceAsync();
        Require(SequencePackage.Read(Path.Combine(output, "Autosave", "sequence-binding-smoke.hktaspack")).InitialSaves!.Id == initialId,
            "autosave retains initial snapshot");
        coordinator.VerifyOriginalSavesUnchanged();

        var empty = new InitialSaveSnapshot(new Dictionary<string, byte[]>());
        var emptyPath = Path.Combine(output, "empty-slots.hktaspack");
        await SequencePackage.WriteAsync(emptyPath, original.Movie, empty);
        await vm.OpenMovieFileAsync(emptyPath);
        await vm.FrameMenuAsync("rebuild", 20);
        AtFrame(vm, boot, 20, "package with four empty slots replays to frame 20");
        Require(coordinator.SessionInitialSaves!.Id == empty.Id && coordinator.SessionInitialSaves.Hashes.Count == 0,
            "package emptiness overrides local saves");
        Require(!Directory.EnumerateFiles(coordinator.ShadowRoot, "user*", SearchOption.TopDirectoryOnly).Any(),
            "empty replay shadow has no local slot files");
        coordinator.VerifyOriginalSavesUnchanged();

        await vm.OpenMovieFileAsync(package);
        await vm.FrameMenuAsync("rebuild", 20);
        AtFrame(vm, boot, 20, "original package replays independently after empty package");
        Require(coordinator.SessionInitialSaves!.Id == initialId, "replay restored original bound snapshot");
        foreach (var pair in original.InitialSaves.Hashes)
            Require(HollowKnightTAS.Core.Cryptography.Sha256Utility.ComputeFileHex(Path.Combine(coordinator.ShadowRoot, pair.Key)) == pair.Value,
                "replay shadow matches initial file " + pair.Key);
        coordinator.VerifyOriginalSavesUnchanged();
        Require(boot.FullRunFaultCode == 0, "native fault remains zero");
        Log("SEQUENCE BINDING PASSED baseline=" + initialId);
    }
}
