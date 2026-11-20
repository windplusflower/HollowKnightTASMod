using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Companion.Automation;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.ViewModels;
using HollowKnightTAS.Core.Movie;

internal static partial class StudioScenarioHarness
{
    // Real installed DebugMod, production VM/IPC/keyboard hook; no Debug function
    // is invoked by the harness. Gates permit independent game-window screenshots.
    static async Task RunCustomKeysAsync(string[] args)
    {
        var vm = (MainViewModel)app.MainWindow.DataContext;
        vm.InfoSettings.Enabled = false;
        var boot = Field<StartupBootController>(app, "startupBoot");
        var movies = Field<FullRunMovieCoordinator>(app, "fullRunMovies");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(MainViewModel).GetField(name, flags)!.SetValue(vm, value);
        Set("worldlines", new StudioTimelineStore(Path.Combine(output, "timelines.json")));
        Set("initialSaveCacheRoot", Path.Combine(output, "initial-saves"));
        Set("activeSequenceDirectory", output); Set("activeAutoSaveSeconds", 0);
        var request = typeof(MainViewModel).GetMethod("RequestRuntimeAsync", flags)!;
        async Task<IReadOnlyDictionary<string, JsonElement>> Info(string label)
        {
            boot.Refresh(); var before = boot.NativeCompletedFrames;
            var fields = new Dictionary<string,string> { ["requestId"] = "custom-keys-" + Guid.NewGuid().ToString("N"), ["view"] = "info" };
            var result = await (Task<IReadOnlyDictionary<string,string>>)request.Invoke(vm, new object[] { vm.SelectedSession!.Client, fields, CancellationToken.None })!;
            await File.WriteAllTextAsync(Path.Combine(output, label + ".json"), result["snapshotJson"]);
            var state = await VideoRuntimeState(vm);
            await File.WriteAllTextAsync(Path.Combine(output, label + "-status.json"), JsonSerializer.Serialize(state));
            boot.Refresh(); Require(boot.IsWaiting && boot.NativeCompletedFrames == before && boot.FullRunFaultCode == 0,
                label + " paused query; no native fault");
            Require(state["mismatchCount"] == "0", label + " no input mismatch");
            return InfoOverlayModel.Decode(result["snapshotJson"]);
        }
        async Task StepFrame()
        {
            var state = await VideoRuntimeState(vm);
            var target = long.Parse(state["movieFrame"], System.Globalization.CultureInfo.InvariantCulture) + 1;
            await Command(vm.StepCommand);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Field<long>(vm,"currentFullRunMovieFrame") != target)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Studio step progress " + target);
                await vm.PollInputGridProgressAsync(); await Task.Delay(100);
            }
            AtFrame(vm,boot,target,"single-step completed");
        }
        async Task Gate(string label)
        {
            var before = boot.NativeCompletedFrames;
            await File.WriteAllTextAsync(Path.Combine(output, "checkpoint.txt"), label);
            Log("SCREENSHOT READY " + label);
            await Until(() => File.Exists(Path.Combine(output, "continue-" + label)), "screenshot gate " + label, 150);
            boot.Refresh(); Require(boot.NativeCompletedFrames == before && boot.IsWaiting, label + " paused screenshot gate did not advance");
        }
        await Field<Func<string,Task>>(vm,"launchGame")(@"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe");
        Execute(vm.NewFullRunMovieCommand); await Command(vm.StepCommand);
        await Until(() => vm.SelectedSession?.Client.IsConnected == true,"custom-key Runtime connected",120);
        await vm.PollInputGridProgressAsync();
        var observed = await VideoRuntimeMovie(vm,movies);
        var baseline = SequencePackage.Read(args.Single(a=>a.StartsWith("--custom-key-baseline=",StringComparison.Ordinal)).Split('=',2)[1]);
        var source = MovieV2Prefix.Take(TimelineTree.Parse(baseline.Movie),1500);
        var header = TimelineTree.Parse(observed.Movie).Header.WithCustomKeys(new short[]{48,282,284});
        var candidate = new MovieV2Document("debug-custom-keys",header,source.Runs.Select(r=>new NativeFrameRun(r.RepeatCount,r.Samples,r.Span,r.FramesPerSecond,true,r.RngSeed))
            .Concat(new[]{new NativeFrameRun(600,Array.Empty<GameInputSample>(),new MovieSourceSpan("keys",1,1,1),authored:true)}));
        var path=Path.Combine(output,"candidate.hktaspack");
        await SequencePackage.WriteAsync(path,new MovieV2Codec().WriteCanonical(candidate),baseline.InitialSaves!);
        await vm.OpenMovieFileAsync(path);
        // These are the same painting operations as clicking/dragging Studio cells.
        vm.PaintGrid(1500,1501,"Key:284",true); vm.PaintGrid(1503,1503,"Key:284",true);
        vm.PaintGrid(1505,1505,"Key:48",true); vm.PaintGrid(1507,1507,"Key:48",true);
        vm.PaintGrid(1510,1510,"Cancel",true);
        vm.PaintGrid(1850,1851,"Key:282",true); vm.PaintGrid(1853,1853,"Key:282",true);
        var finalMovie=vm.MovieText;
        await File.WriteAllTextAsync(Path.Combine(output,"test.hktas"),finalMovie);
        await SequencePackage.WriteAsync(Path.Combine(output,"test.hktaspack"),finalMovie,baseline.InitialSaves!);
        await vm.FrameMenuAsync("rebuild",1500); AtFrame(vm,boot,1500,"Godhome baseline");
        await Info("before-panel"); await Gate("before-panel");
        await StepFrame(); AtFrame(vm,boot,1501,"panel toggle press");
        await Info("panel-pressed");
        await StepFrame(); AtFrame(vm,boot,1502,"held key second frame");
        await Info("panel-held"); await Gate("panel-open-held");
        await vm.FrameMenuAsync("seek",1505); AtFrame(vm,boot,1505,"panel second press closes after UI refresh");
        await Info("panel-closed"); await Gate("panel-closed");
        await vm.FrameMenuAsync("seek",1507); await Info("all-panels-open"); await Gate("all-panels-open");
        await vm.FrameMenuAsync("seek",1509); await Info("all-panels-closed"); await Gate("all-panels-closed");
        await vm.FrameMenuAsync("seek",1850); AtFrame(vm,boot,1850,"zero soul before refill");
        var empty = await Info("soul-before");
        var initialSoul = empty["soul"].GetInt32(); var initialReserve=empty["reserveSoul"].GetInt32();
        Require(initialSoul==0 && initialReserve==0,"test starts with zero soul and reserve");
        await Gate("soul-before");
        await StepFrame();
        var first=await Info("soul-first"); Require(first["soul"].GetInt32()==33,"F1 invokes Debug Add Soul (+33)"); await Gate("soul-first");
        await StepFrame();
        var held=await Info("soul-held"); Require(held["soul"].GetInt32()==33,"held F1 does not repeat GetKeyDown");
        await StepFrame();
        var released=await Info("soul-released"); Require(released["soul"].GetInt32()==33,"release does not trigger Add Soul");
        await StepFrame();
        var second=await Info("soul-second"); Require(second["soul"].GetInt32()==66,"second F1 press invokes another +33"); await Gate("soul-second");
        await vm.FrameMenuAsync("rebuild",1854); AtFrame(vm,boot,1854,"independent cold replay");
        var repeat=await Info("soul-cold-repeat"); Require(repeat["soul"].GetInt32()==66,"cold replay reproduces both shortcut presses");
        movies.VerifyOriginalSavesUnchanged();
        Log("PASS original user saves unchanged; test is Debug Mod shortcut behavior, not vanilla TAS qualification");
        await Gate("finished");
    }
}
