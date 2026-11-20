using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.ViewModels;
using HollowKnightTAS.Core.Movie;

internal static partial class StudioScenarioHarness
{
    static async Task RunWorldlineFaultAsync(string[] args)
    {
        string Arg(string key) => args.Single(a => a.StartsWith(key + "=", StringComparison.Ordinal)).Split('=', 2)[1];
        var vm = (MainViewModel)app.MainWindow.DataContext;
        var boot = Field<StartupBootController>(app, "startupBoot");
        var movies = Field<FullRunMovieCoordinator>(app, "fullRunMovies");
        void Set(string name, object value) => typeof(MainViewModel).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);
        Set("worldlines", new StudioTimelineStore(Path.Combine(output, "timelines.json")));
        Set("initialSaveCacheRoot", Path.Combine(output, "initial-saves"));
        Set("activeSequenceDirectory", output);
        var raw = await File.ReadAllTextAsync(Arg("--fault-movie"));
        var original = TimelineTree.Parse(raw);
        var saves = SequencePackage.Read(Arg("--fault-baseline")).InitialSaves!;
        using (var descriptor = JsonDocument.Parse(await File.ReadAllTextAsync(Arg("--fault-descriptor"))))
        {
            var expected = descriptor.RootElement.GetProperty("files").EnumerateArray().ToArray();
            Require(expected.Length == saves.Hashes.Count && expected.All(f =>
                saves.Hashes.TryGetValue(f.GetProperty("name").GetString()!, out var hash)
                && hash == f.GetProperty("sha256").GetString()), "baseline matches every original fault-session save hash");
        }
        var source = Path.Combine(output, "old-seeded.hktaspack");
        await SequencePackage.WriteAsync(source, raw, saves);
        await vm.OpenMovieFileAsync(source);
        await Field<Func<string, Task>>(vm, "launchGame")(@"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe");
        await vm.FrameMenuAsync("rebuild", 3000);
        Require(boot.FullRunFaultCode == 41, "unchanged old seeded Movie reproduces native fault 41");
        var faults = Directory.GetFiles(Path.Combine(movies.ShadowRoot, "HollowKnightTAS"), "full-run-fault.txt", SearchOption.AllDirectories);
        Require(faults.Length == 1, "original diagnostic available");
        var faultText = await File.ReadAllTextAsync(faults[0]);
        await File.WriteAllTextAsync(Path.Combine(output, "reproduced-fault.txt"), faultText);
        Require(faultText.Contains("Input edge mismatch"), "failure is recorded input edge assertion");

        // Undo the old seed in a COPY without changing any other frame or header;
        // then use the real editor to perform that same seed edit under the fix.
        var codec = new MovieV2Codec();
        var unseeded = new MovieV2Document(original.SourceName, original.Header,
            original.Runs.Select(r => new NativeFrameRun(r.RepeatCount, r.Samples, r.Span, r.FramesPerSecond, r.Authored, null)));
        var candidate = Path.Combine(output, "candidate-base.hktaspack");
        await SequencePackage.WriteAsync(candidate, codec.WriteCanonical(unseeded), saves);
        var failedPid = Field<Process>(app, "startupGame").Id;
        await vm.OpenMovieFileAsync(candidate);
        Require(Field<Process>(app, "startupGame").Id == failedPid && vm.TogglePauseCommand.CanExecute(null),
            "faulted session opens another sequence without restarting and enables Play");
        Require(vm.TrySetGridRngSeed(2299, "0"), "same RNG edit authored through Studio");
        await File.WriteAllTextAsync(Path.Combine(output, "candidate-seeded.hktas"), vm.MovieText);
        Require(MovieV2Prefix.Matches(original, TimelineTree.Parse(vm.MovieText), 2299), "strict prefix remains identical before RNG edit");
        await vm.FrameMenuAsync("rebuild", 2700);
        AtFrame(vm, boot, 2700, "new worldline reaches before old failure");
        var pid = Field<Process>(app, "startupGame").Id;
        bool dead = false, marmu = false;
        for (long frame = 2720; frame <= 3220; frame += 20)
        {
            await vm.FrameMenuAsync("seek", frame);
            AtFrame(vm, boot, frame, "branch continues " + frame);
            var state = await VideoRuntimeState(vm);
            Log("WORLD " + JsonSerializer.Serialize(state));
            dead |= state.TryGetValue("heroHealth", out var health) && health == "0";
            marmu |= state.TryGetValue("sceneName", out var scene) && scene.Contains("Marmu");
        }
        Require(marmu && dead, "Marmu scene and native hero death observed without fault");
        Require(Field<Process>(app, "startupGame").Id == pid, "death and later frames stay in same process");
        Require(vm.TrySetGridRngSeed(3221, "17"), "new worldline remains editable after death");
        await vm.FrameMenuAsync("seek", 3240);
        AtFrame(vm, boot, 3240, "future seed edit executes after death");

        var neutral = new MovieV2Document("switch-target", original.Header,
            new[] { new NativeFrameRun(200, Array.Empty<GameInputSample>(), new MovieSourceSpan("switch", 1, 1, 1), authored: true) });
        var target = Path.Combine(output, "switch-target.hktaspack");
        await SequencePackage.WriteAsync(target, codec.WriteCanonical(neutral), saves);
        await Command(vm.TogglePauseCommand);
        await vm.OpenMovieFileAsync(target);
        Require(boot.IsWaiting && Field<Process>(app, "startupGame").Id == pid, "running sequence pauses and switches draft without restart");
        await Command(vm.StepCommand);
        Require(Field<Process>(app, "startupGame").Id != pid && boot.FullRunFaultCode == 0, "Step starts selected sequence in new protected process");
        await vm.FrameMenuAsync("seek", 8);
        AtFrame(vm, boot, 8, "switched sequence executes");
        movies.VerifyOriginalSavesUnchanged();
        Require(true, "original user saves unchanged");
    }
}
