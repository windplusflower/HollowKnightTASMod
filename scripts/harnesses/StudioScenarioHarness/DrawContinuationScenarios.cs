using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.ViewModels;

internal static partial class StudioScenarioHarness
{
    static async Task VerifyDrawContinuationAsync(MainViewModel vm, StartupBootController boot,
        FullRunMovieCoordinator movies, long target)
    {
        var request = typeof(MainViewModel).GetMethod("RequestRuntimeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        async Task Capture(long frame)
        {
            boot.Refresh(); var native = boot.NativeCompletedFrames;
            var objects = new List<JsonElement>();
            var id = ""; int offset = 0;
            do
            {
                var fields = new Dictionary<string, string> { ["requestId"] = "draw-world-" + Guid.NewGuid().ToString("N"),
                    ["view"] = "all", ["includeInactive"] = "false", ["offset"] = offset.ToString(), ["limit"] = "128" };
                if (id.Length > 0) fields["snapshotId"] = id;
                var result = await (Task<IReadOnlyDictionary<string, string>>)request.Invoke(vm,
                    new object[] { vm.SelectedSession!.Client, fields, CancellationToken.None })!;
                using var page = JsonDocument.Parse(result["snapshotJson"]);
                var root = page.RootElement;
                Require(root.GetProperty("movieFrame").GetInt64() == frame, "world snapshot target " + frame);
                if (offset == 0)
                    File.WriteAllText(Path.Combine(output, "world-" + frame + "-metadata.json"), root.GetProperty("metadata").GetRawText());
                objects.AddRange(root.GetProperty("objects").EnumerateArray().Select(o => o.Clone()));
                id = root.GetProperty("snapshotId").GetString()!;
                offset = root.GetProperty("nextOffset").GetInt32();
            } while (offset >= 0);
            File.WriteAllText(Path.Combine(output, "world-" + frame + ".json"), JsonSerializer.Serialize(objects));
            File.WriteAllText(Path.Combine(output, "state-" + frame + ".json"), JsonSerializer.Serialize(await VideoRuntimeState(vm)));
            boot.Refresh(); Require(boot.IsWaiting && native == boot.NativeCompletedFrames, "world inspection did not advance " + frame);
            Log("WORLD frame=" + frame + " objects=" + objects.Count);
        }
        await Capture(target);
        foreach (var frame in new[] { target + 100, target + 400, MovieFrames(vm.MovieText) - 1 }.Distinct().OrderBy(x => x))
        {
            if (frame <= target || frame >= MovieFrames(vm.MovieText)) continue;
            await vm.FrameMenuAsync("seek", frame);
            AtFrame(vm, boot, frame, "visible continuation reached " + frame);
            await Capture(frame);
        }
        foreach (var trace in Directory.GetFiles(movies.ShadowRoot, "boss-trace.csv", SearchOption.AllDirectories))
            File.Copy(trace, Path.Combine(output, "continuation-boss-trace.csv"), true);
        movies.VerifyOriginalSavesUnchanged();
    }
}
