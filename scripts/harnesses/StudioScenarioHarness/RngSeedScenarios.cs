using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Companion.Automation;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.ViewModels;
using HollowKnightTAS.Core.Automation;
using HollowKnightTAS.Core.Movie;

internal static partial class StudioScenarioHarness
{
    // Opt-in real-game scenario. Only neutral title-screen inputs; original saves
    // are protected by the production full-run launcher and verified at exit.
    static async Task RunRngSeedAsync(string? previousOutput = null)
    {
        Environment.SetEnvironmentVariable("HKTAS_FULL_RUN_RNG_TRACE", "1");
        var vm = (MainViewModel)app.MainWindow.DataContext;
        var boot = Field<StartupBootController>(app, "startupBoot");
        var movies = Field<FullRunMovieCoordinator>(app, "fullRunMovies");
        await Field<Func<string, Task>>(vm, "launchGame")(@"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe");
        Execute(vm.NewFullRunMovieCommand);
        await Command(vm.StepCommand);
        await Until(() => vm.SelectedSession?.Client.IsConnected == true, "RNG Runtime connected", 120);
        await vm.PollInputGridProgressAsync();
        var initial = await VideoRuntimeMovie(vm, movies);
        var movie = new MovieV2Document("rng-title", TimelineTree.Parse(initial.Movie).Header,
            new[] { new NativeFrameRun(40, Array.Empty<GameInputSample>(), new MovieSourceSpan("rng", 1, 1, 1), authored: true) });
        var path = Path.Combine(output, "rng-title.hktas");
        await File.WriteAllTextAsync(path, new MovieV2Codec().WriteCanonical(movie));
        await vm.OpenMovieFileAsync(path);
        Require(vm.TrySetGridRngSeed(0, "0") && vm.TrySetGridRngSeed(7, "12345")
            && vm.TrySetGridRngSeed(8, "0") && vm.TrySetGridRngSeed(20, "12345"), "seed edits accepted");
        var seededMovie = vm.MovieText;
        await File.WriteAllTextAsync(Path.Combine(output, "seeded-title.hktas"), seededMovie);

        async Task<string[]> Trace(string name)
        {
            var result = await Field<AutomationBroker>(vm, "automationBroker").ExecuteHumanAsync(
                AutomationCommandIds.FullRunSnapshot, AutomationScope.MovieRead, null, "Paused", null, CancellationToken.None);
            Require(result.Success, "Runtime snapshot available for RNG trace");
            var snapshot = Path.GetFullPath(result.Data["path"]);
            Require(snapshot.StartsWith(Path.GetFullPath(movies.ShadowRoot) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase), "trace is in protected shadow storage");
            var trace = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(snapshot)!)!, "movie-rng-trace.csv");
            var text = await File.ReadAllTextAsync(trace);
            await File.WriteAllTextAsync(Path.Combine(output, name + ".csv"), text);
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        }
        string State(string[] rows, int frame) => rows.Single(r => r.StartsWith(frame + ",before,", StringComparison.Ordinal)).Split(',')[3];

        string[] first;
        if (previousOutput == null)
        {
            await vm.FrameMenuAsync("rebuild", 24);
            AtFrame(vm, boot, 24, "seeded replay reaches Movie frame 24");
            first = await Trace("first");
            Require(first.Length == 48, "one before/after RNG observation per Movie frame, excluding loading frames");
            Require(first.Count(r => r.Split(',')[2].Length != 0) == 4, "each seeded Movie frame applies once");
            Require(State(first, 0) == State(first, 8) && State(first, 7) == State(first, 20),
                "scene-first and later equal seeds produce identical initial RNG states");
            await Task.Delay(300);
            Require(first.SequenceEqual(await Trace("paused")), "paused observation does not consume or reset RNG");
            await vm.FrameMenuAsync("rebuild", 24);
            AtFrame(vm, boot, 24, "cold replay reaches same frame");
            Require(first.SequenceEqual(await Trace("repeat")), "independent cold replay reproduces every before/after RNG state");
            var snapshotMovie = await VideoRuntimeMovie(vm, movies);
            Require(snapshotMovie.Movie == seededMovie, "Runtime snapshot preserves all seed commands");
            await vm.SaveCurrentBranchAsync();
            var oldLeaf = vm.SelectedWorldline!.Id;
            var oldMovie = vm.SelectedWorldline.Movie;
            Require(vm.TrySetGridRngSeed(7, "54321"), "past seed edit accepted");
            await vm.SaveCurrentBranchAsync();
            Require(vm.SelectedTimelineTree!.Leaves.Count() >= 2
                && vm.SelectedTimelineTree.Nodes.Single(n => n.Id == oldLeaf).Movie == oldMovie,
                "past RNG edit creates a sibling and preserves original worldline");
        }
        else
        {
            first = File.ReadAllLines(Path.Combine(previousOutput, "first.csv")).Skip(1).ToArray();
            var repeated = File.ReadAllLines(Path.Combine(previousOutput, "repeat.csv")).Skip(1).ToArray();
            Require(first.Length == 48 && first.SequenceEqual(repeated), "prior independent RNG replay evidence verified");
            Require(vm.TrySetGridRngSeed(7, "54321"), "remaining branch replay seed prepared");
        }
        await vm.FrameMenuAsync("rebuild", 24);
        AtFrame(vm, boot, 24, "edited worldline replayed");
        var branch = await Trace("branch");
        Require(first.Take(14).SequenceEqual(branch.Take(14)) && State(first, 7) != State(branch, 7),
            "past prefix remains identical and RNG diverges on edited frame");
        Require(State(first, 20) == State(branch, 20), "later explicit seed restores specified RNG state");
        Require(vm.TrySetGridRngSeed(30, "-2147483648") && vm.TrySetGridRngSeed(31, "2147483647")
            && vm.TrySetGridRngSeed(31, ""), "future seed edit and clear accepted");
        await vm.FrameMenuAsync("seek", 32);
        AtFrame(vm, boot, 32, "future RNG edit continues without cold restore");
        var continued = await Trace("continued");
        Require(continued.Any(r => r.StartsWith("30,before,-2147483648,", StringComparison.Ordinal))
            && continued.Any(r => r.StartsWith("31,before,,", StringComparison.Ordinal)), "future seed applies and cleared seed does not");
        movies.VerifyOriginalSavesUnchanged();
        Require(true, "original save hashes unchanged");
        Environment.SetEnvironmentVariable("HKTAS_FULL_RUN_RNG_TRACE", null);
    }
}
