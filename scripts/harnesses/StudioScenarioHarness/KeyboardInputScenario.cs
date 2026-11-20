using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Input;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.ViewModels;
using HollowKnightTAS.Core.Movie;

internal static partial class StudioScenarioHarness
{
    static async Task RunKeyboardInputAsync()
    {
        var vm = (MainViewModel)app.MainWindow.DataContext;
        var boot = Field<StartupBootController>(app, "startupBoot");
        var movies = Field<FullRunMovieCoordinator>(app, "fullRunMovies");
        void Set(string name, object value) => typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);
        Set("worldlines", new StudioTimelineStore(Path.Combine(output, "timelines.json")));
        Set("initialSaveCacheRoot", Path.Combine(output, "initial-saves"));
        Set("activeSequenceDirectory", output);
        await Field<Func<string, Task>>(vm, "launchGame")(@"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe");
        vm.NewFullRunMovieCommand.Execute(null);
        await Command(vm.StepCommand);
        await Until(() => vm.SelectedSession != null, "runtime connected");
        await vm.PollInputGridProgressAsync();
        await vm.FrameMenuAsync("seek", 120);
        await Until(() => Field<IReadOnlyDictionary<string, string>>(vm, "keyboardBindings").Count == 13, "keyboard bindings received");
        var bindings = Field<IReadOnlyDictionary<string, string>>(vm, "keyboardBindings");
        await File.WriteAllTextAsync(Path.Combine(output, "bindings.json"), System.Text.Json.JsonSerializer.Serialize(bindings));
        var frame = 120L;
        foreach (var keys in new[] { new[] { 0x57, 0x4A }, new[] { 0x4B }, new[] { 0x4B }, new[] { 0x4B }, Array.Empty<int>() })
        {
            var input = KeyboardFrameInput.Capture(bindings, Key.V, keys.Contains);
            Set("pendingKeyboardFrame", input);
            await Command(vm.StepCommand);
            var read = (Task)typeof(MainViewModel).GetMethod("ReadReadyFrameSnapshotAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null)!;
            await read;
            var snapshot = read.GetType().GetProperty("Result")!.GetValue(read)!;
            var text = (string)snapshot.GetType().GetProperty("Movie")!.GetValue(snapshot)!;
            var actualFrame = (long)snapshot.GetType().GetProperty("Frame")!.GetValue(snapshot)!;
            Require(actualFrame == frame + 1, "single frame recorded " + actualFrame);
            var document = new MovieEditorService().ValidateAny(text).V2Document!;
            long position = 0;
            var run = document.Runs.First(r => { var end = position + r.RepeatCount; var match = frame >= position && frame < end; position = end; return match; });
            var sample = run.Samples.First(s => s.Channel == GameInputChannel.Hero);
            foreach (var action in KeyboardFrameInput.Actions)
                Require((sample.Values[MovieV2RangeEditor.ActionIndex(GameInputChannel.Hero, action)] != 0) == input[action], "recorded " + frame + " " + action + "=" + input[action]);
            await File.WriteAllTextAsync(Path.Combine(output, "recorded-" + actualFrame + ".hktas"), text);
            frame++;
        }
        Require(boot.FullRunFaultCode == 0 && boot.IsWaiting, "paused without native faults");
        movies.VerifyOriginalSavesUnchanged();
        Log("PASS original saves unchanged; injected key states use real mapping/VM/Runtime, physical hotkey not exercised");
    }
}
