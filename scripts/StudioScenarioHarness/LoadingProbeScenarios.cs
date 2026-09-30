using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.ViewModels;

internal static partial class StudioScenarioHarness
{
    // Read-only discovery run. A completed trace is not a P0/full-state PASS.
    static async Task RunLoadingProbeAsync(string[] args)
    {
        string Arg(string name) => args.Single(a => a.StartsWith(name + "=", StringComparison.Ordinal)).Split('=', 2)[1];
        var first = long.Parse(Arg("--probe-first"));
        var end = long.Parse(Arg("--probe-end"));
        Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE", "1");
        Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE_FIRST", first.ToString());
        Environment.SetEnvironmentVariable("HKTAS_LOADING_PROBE_END", end.ToString());
        var vm = (MainViewModel)app.MainWindow.DataContext;
        var boot = Field<StartupBootController>(app, "startupBoot");
        var movies = Field<FullRunMovieCoordinator>(app, "fullRunMovies");
        void Set(string name, object value) => typeof(MainViewModel).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);
        Set("worldlines", new StudioTimelineStore());
        Set("initialSaveCacheRoot", Path.Combine(output, "initial-saves"));
        Set("activeSequenceDirectory", output);
        Set("activeAutoSaveSeconds", 0);
        await Field<Func<string, Task>>(vm, "launchGame")(@"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe");
        try
        {
            Execute(vm.NewFullRunMovieCommand);
            await Command(vm.StepCommand);
            await Until(() => vm.SelectedSession?.Client.IsConnected == true, "loading probe Runtime connected", 120);
            var source = Path.GetFullPath(Arg("--probe-movie"));
            var sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(source)));
            await vm.OpenMovieFileAsync(source);
            var oldPid = Field<Process>(app, "startupGame").Id;
            await vm.FrameMenuAsync("rebuild", end);
            AtFrame(vm, boot, end, "loading probe exact endpoint");
            Require(Field<Process>(app, "startupGame").Id != oldPid, "independent cold replacement process");
            var state = await VideoRuntimeState(vm);
            Require(state["mismatchCount"] == "0" && state["error"] == "", "probe execution has no input mismatch");
            await File.WriteAllTextAsync(Path.Combine(output, "target-state.json"), JsonSerializer.Serialize(state));
            var traces = Directory.GetFiles(movies.ShadowRoot, "loading-boundary-probe.jsonl", SearchOption.AllDirectories);
            Require(traces.Length == 1, "one trace in current protected shadow");
            File.Copy(traces[0], Path.Combine(output, "loading-boundary-probe.jsonl"), false);
            using var footer = JsonDocument.Parse(File.ReadLines(traces[0]).Last());
            Require(footer.RootElement.GetProperty("kind").GetString() == "footer"
                && footer.RootElement.GetProperty("success").GetBoolean(), "bounded trace completed without sampling failure");
            Require(sourceHash == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(source))),
                "input package bytes unchanged");
            await File.WriteAllTextAsync(Path.Combine(output, "source-identity.json"), JsonSerializer.Serialize(new
                { source, sourceHash, first, end, shadow = movies.ShadowRoot, phaseP0Passed = false }));
            Log("DISCOVERY TRACE COMPLETE; full-state P0 gate not asserted");
        }
        finally { movies.VerifyOriginalSavesUnchanged(); }
    }
}
