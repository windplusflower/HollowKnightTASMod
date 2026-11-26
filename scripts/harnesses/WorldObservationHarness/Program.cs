using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Reflection;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Core.Ipc;
using HollowKnightTAS.Core.Movie;

internal static class Program
{
    private const string DefaultGame = @"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe";
    private const string DefaultBundle = @"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight_Data\Managed\Mods\HollowKnightTAS\Companion\win-x64\ClockStartup";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static Dictionary<string, object?>? currentReport;

    // Keep the observation wire names local so this harness can be built while
    // an installed Core bundle is one build behind the newly installed Runtime.
    // The values are the formal Core IpcMessageTypes names.
    private const string GetWorldSnapshot = "getWorldSnapshot";
    private const string GetObjectDetails = "getObjectDetails";
    private const string WorldSnapshot = "worldSnapshot";
    private const string ObjectDetails = "objectDetails";

    [STAThread]
    public static int Main(string[] args)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        int code = 3;
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try { code = await RunAsync(args); }
            finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        }));
        Dispatcher.Run();
        return code;
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var options = Options.Parse(args);
        Directory.CreateDirectory(options.Output);
        var started = Stopwatch.StartNew();
        var report = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["mode"] = options.Mode,
            ["fixture"] = options.Fixture,
            ["fixtureSha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(options.Fixture))).ToLowerInvariant(),
            ["output"] = options.Output,
            ["startedUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["queries"] = new List<object>(),
            ["status"] = new List<object>(),
            ["resources"] = new List<object>(),
            ["scenes"] = new List<object>(),
            ["checks"] = new List<string>()
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(options.Mode == "interactive" ? 30 : 4));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var resources = new ResourceMonitor(report, options.RelaxedResources, linked.Token);
        VerifiedGameLaunchHandle? handle = null;
        StartupBootController? boot = null;
        FullRunMovieCoordinator? movies = null;
        RuntimeSessionClient? runtime = null;
        SingleInstanceCoordinator? instance = null;
        SessionRegistry? sessions = null;
        ControlPipeServer? control = null;
        ColliderOverlayController? overlay = null;
        currentReport = report;
        try
        {
            // Bind all packaged initial-save files before PrepareLaunch creates
            // the protected shadow. Interactive route authoring must not silently
            // pick up the player's current live saves from a bare Movie file.
            var fixture = SequencePackage.Read(options.Fixture);
            if (options.Mode == "interactive" && fixture.InitialSaves == null)
                throw new InvalidDataException("Interactive authoring requires a .hktaspack with bound initial saves.");
            var movie = LoadMovie(fixture, options.Fixture);
            report["initialSavesSource"] = fixture.InitialSaves == null ? "protected-live-baseline" : "sequence-package";
            if (fixture.InitialSaves != null)
            {
                report["initialSavesId"] = fixture.InitialSaves.Id;
                report["initialSaveFileCount"] = fixture.InitialSaves.Hashes.Count;
            }

            // Own the formal current-user Companion mutex before the game starts.
            // The installed Companion then forwards Runtime registration to this
            // pipe, so this harness never handles or prints the registration token.
            instance = new SingleInstanceCoordinator();
            if (!instance.IsPrimary)
                throw new InvalidOperationException(
                    "A Companion instance already owns the current-user control pipe. "
                    + "Close it or temporarily disable AutoStartCompanion before running the harness.");
            sessions = new SessionRegistry(
                "world-observation-harness-" + Guid.NewGuid().ToString("N"));
            control = new ControlPipeServer(
                instance.ControlPipeName, sessions, canExitCompanion: () => false);
            control.Start();

            var profile = VerifiedStartupProfile.Load(options.Bundle, options.Game);
            boot = new StartupBootController();
            movies = new FullRunMovieCoordinator(boot) { SequenceInitialSaves = fixture.InitialSaves };
            var gate = movies.PrepareLaunch();
            if (fixture.InitialSaves != null)
                Require(movies.SessionInitialSaves?.Id == fixture.InitialSaves.Id,
                    "protected shadow binds the complete packaged initial-save snapshot");
            report["runId"] = movies.RunId;
            report["shadowRoot"] = movies.ShadowRoot;
            File.WriteAllText(Path.Combine(options.Output, "probe-shadows.json"),
                JsonSerializer.Serialize(new { shadows = new[] { movies.ShadowRoot } }));
            var launcher = new VerifiedGameLauncher(profile);
            handle = await launcher.LaunchInteractiveAsync(
                "world-observe-" + Guid.NewGuid().ToString("N"),
                TimeSpan.FromSeconds(60), linked.Token, gate);
            resources.Start(handle);
            await WaitForGateAsync(gate, handle, linked.Token);

            boot.Refresh();
            movies.ArmReplay(movie, 1);
            var first = await movies.StepAsync(0, linked.Token);
            Require(first.Mode != "Fault" && gate.IsWaiting, "startup frame 1 reached");
            AppendStatus(report, "frame-1", gate, movies);

            // A directly supplied bootstrap channel remains supported for a
            // formal launcher invocation. In the ordinary installed setup the
            // Companion forwards the same registration over ControlPipeServer.
            var bootstrap = await BootstrapRegistrationReader.TryReadAsync(args, linked.Token);
            if (bootstrap != null)
            {
                if (!await sessions.RegisterAsync(bootstrap, linked.Token))
                    throw new InvalidOperationException(
                        "Formal bootstrap registration failed: "
                        + sessions.LastRegistrationError);
            }
            runtime = await WaitForRuntimeSessionAsync(
                sessions, handle.ProcessId, linked.Token);
            report["sessionId"] = runtime.SessionId;
            report["gameProcessId"] = runtime.GameProcessId;
            report["runtimeAssemblySha256"] = runtime.RuntimeAssemblySha256;
            report["coreAssemblySha256"] = runtime.CoreAssemblySha256;
            report["environmentManifestSha256"] = runtime.EnvironmentManifestSha256;

            if (options.Overlay)
            {
                overlay = new ColliderOverlayController(() => runtime,
                    async (session, fields, token) => await RuntimeCommandAsync(session, GetWorldSnapshot, WorldSnapshot,
                        fields.ToDictionary(pair => pair.Key, pair => pair.Value), token),
                    status => report["overlayStatus"] = status);
                overlay.SetEnabled(true);
                report["overlayEnabled"] = true;
            }

            await RecordStatusAsync(report, runtime, "frame-1", linked.Token);
            if (options.Mode == "interactive")
            {
                await InteractiveAsync(report, runtime, movies, gate, options, linked.Token);
            }
            else if (options.Mode != "baseline")
            {
                await ObserveFrameAsync(report, runtime, movies, gate, 1, "title", true, linked.Token);
                if (options.VideoOutput != null)
                {
                    await MoveToFrameAsync(runtime, movies, gate, options.VideoStart, linked.Token);
                    await RuntimeCommandAsync(runtime, IpcMessageTypes.StartVideoExport, IpcMessageTypes.CommandAccepted,
                        new Dictionary<string, string> { ["requestId"] = RequestId(), ["ffmpegPath"] = options.Ffmpeg!,
                            ["outputPath"] = options.VideoOutput, ["maximumFrames"] = "20000", ["replayLoadedMovie"] = "true" }, linked.Token);
                    report["videoStartMovieFrame"] = options.VideoStart;
                }
                await MoveToFrameAsync(runtime, movies, gate, 1500, linked.Token);
                await RecordStatusAsync(report, runtime, "frame-1500", linked.Token);
                await ObserveFrameAsync(report, runtime, movies, gate, 1500, "sanctum", true, linked.Token);
                if (overlay != null) await VerifyOverlayAsync(overlay, gate, report, linked.Token);
                await MoveToFrameAsync(runtime, movies, gate, options.BossFrame, linked.Token);
                await RecordStatusAsync(report, runtime, "boss-frame", linked.Token);
                await ObserveFrameAsync(report, runtime, movies, gate, options.BossFrame, options.Mode == "probe" ? "probe" : "boss", true, linked.Token);
                if (overlay != null) await VerifyOverlayAsync(overlay, gate, report, linked.Token, "boss");
            }
            else
            {
                await MoveToFrameAsync(runtime, movies, gate, 1500, linked.Token);
                await RecordStatusAsync(report, runtime, "frame-1500", linked.Token);
                await MoveToFrameAsync(runtime, movies, gate, options.BossFrame, linked.Token);
                await RecordStatusAsync(report, runtime, "boss-frame", linked.Token);
            }

            if (options.Mode != "interactive")
            {
            string previousPopulation = "";
            foreach (long frame in options.SampleFrames)
            {
                await MoveToFrameAsync(runtime, movies, gate, frame, linked.Token);
                previousPopulation = await CaptureCombatSampleAsync(report, runtime, gate, frame,
                    previousPopulation, frame == options.SampleFrames.Last(), linked.Token);
            }
            await MoveToFrameAsync(runtime, movies, gate, movie.Runs.Sum(run => (long)run.RepeatCount), linked.Token);
            var completed = await RecordStatusAsync(report, runtime, "completed", linked.Token);
            Require(gate.IsFullRunFinished, "full-run completed");
            Require(gate.FullRunFaultCode == 0, "full-run fault=0");
            Require(string.Equals(ReadField(completed, "mode"), "Completed", StringComparison.OrdinalIgnoreCase),
                "final status Completed");
            Require(ReadInt64(completed, "mismatchCount", -1) == 0, "final mismatch=0");
            if (options.VideoOutput != null)
            {
                var encodingDeadline = DateTime.UtcNow.AddMinutes(2);
                while (true)
                {
                    var video = await RuntimeCommandAsync(runtime, IpcMessageTypes.FullRunStatus, IpcMessageTypes.FullRunState,
                        new Dictionary<string, string> { ["requestId"] = RequestId() }, linked.Token);
                    report["videoExport"] = video.Where(p => p.Key.StartsWith("videoExport.", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value);
                    string state = ReadField(video, "videoExport.state");
                    if (state == "Completed") break;
                    if (state == "Failed" || state == "Cancelled") throw new InvalidOperationException("Video export " + state);
                    if (DateTime.UtcNow > encodingDeadline) throw new TimeoutException("Video encoding did not complete.");
                    await Task.Delay(250, linked.Token);
                }
                Require(File.Exists(options.VideoOutput), "completed MP4 exists");
            }
            }
            movies.VerifyOriginalSavesUnchanged();
            AddCheck(report, "original saves unchanged before quit");
            await RuntimeCommandAsync(runtime, IpcMessageTypes.QuitGame,
                IpcMessageTypes.CommandAccepted,
                new Dictionary<string, string> { ["requestId"] = RequestId() }, linked.Token);
            await WaitForExitAsync(handle, linked.Token);
            movies.VerifyOriginalSavesUnchanged();
            AddCheck(report, "original saves unchanged after quit");
            string modLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "LocalLow", "Team Cherry", "Hollow Knight", "ModLog.txt");
            if (File.Exists(modLog)) File.Copy(modLog, Path.Combine(options.Output, "ModLog.txt"), true);
            report["success"] = true;
            report["elapsedSeconds"] = started.Elapsed.TotalSeconds;
            WriteReport(options.Output, report);
            return 0;
        }
        catch (Exception exception)
        {
            report["success"] = false;
            report["error"] = exception.GetType().Name + ": " + exception.Message;
            report["elapsedSeconds"] = started.Elapsed.TotalSeconds;
            WriteReport(options.Output, report);
            return 3;
        }
        finally
        {
            linked.Cancel();
            overlay?.Dispose();
            try { if (movies != null) movies.VerifyOriginalSavesUnchanged(); } catch { }
            runtime?.Dispose();
            control?.Dispose();
            sessions?.Dispose();
            instance?.Dispose();
            boot?.Dispose();
            // The launch handle is the only process handle this harness owns.
            // Keeping supervision until the normal quit lets Dispose terminate it on failure.
            handle?.Dispose();
            resources.Stop();
            currentReport = null;
        }
    }

    private static async Task InteractiveAsync(Dictionary<string, object?> report,
        RuntimeSessionClient runtime, FullRunMovieCoordinator movies, StartupBootGate gate,
        Options options, CancellationToken token)
    {
        File.WriteAllText(Path.Combine(options.Output, "ready.json"), JsonSerializer.Serialize(new { ready = true, frame = 1 }));
        for (int number = 1; ; number++)
        {
            string stem = "command-" + number.ToString("D4", CultureInfo.InvariantCulture);
            string path = Path.Combine(options.Output, stem + ".json");
            while (!File.Exists(path)) await Task.Delay(100, token);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var command = document.RootElement;
            if (command.TryGetProperty("quit", out var quit) && quit.GetBoolean()) return;
            var status = await RecordStatusAsync(report, runtime, stem + "-before", token);
            long frame = ReadInt64(status, "movieFrame", -1);
            if (command.TryGetProperty("moviePath", out var source))
            {
                string moviePath = Path.Combine(movies.ShadowRoot, "HollowKnightTAS", "interactive-movie.hktas");
                File.Copy(Path.GetFullPath(source.GetString()!), moviePath, true);
                await RuntimeCommandAsync(runtime, IpcMessageTypes.FullRunUpdateMovie, IpcMessageTypes.CommandAccepted,
                    new Dictionary<string, string> { ["requestId"] = RequestId(), ["moviePath"] = moviePath,
                        ["expectedNativeFrame"] = gate.NativeCompletedFrames.ToString(CultureInfo.InvariantCulture) }, token);
                movies.MarkLiveReplay();
            }
            if (command.TryGetProperty("frame", out var target) && target.GetInt64() != frame)
            {
                await MoveToFrameAsync(runtime, movies, gate, target.GetInt64(), token);
                frame = target.GetInt64();
            }
            long native = gate.NativeCompletedFrames;
            var world = await ReadSnapshotAsync(report, runtime, frame, stem + "-world",
                command.TryGetProperty("view", out var view) ? view.GetString()! : "world", token,
                command.TryGetProperty("includeInactive", out var inactive) && inactive.GetBoolean());
            using var snapshot = world.Snapshot;
            if (command.TryGetProperty("detailNames", out var names))
            {
                var wanted = names.EnumerateArray().Select(n => n.GetString()).ToHashSet();
                int index = 0;
                foreach (var obj in snapshot.RootElement.GetProperty("objects").EnumerateArray())
                    if (wanted.Contains(obj.GetProperty("name").GetString()))
                        await ObserveDetailsAsync(report, runtime, obj, frame, native, stem + "-detail-" + index++, token);
            }
            Require(gate.IsWaiting && gate.NativeCompletedFrames == native, stem + " paused queries preserve frame");
            var after = await RecordStatusAsync(report, runtime, stem + "-after", token);
            Require(gate.FullRunFaultCode == 0 && ReadInt64(after, "mismatchCount", -1) == 0, stem + " no fault or mismatch");
            movies.VerifyOriginalSavesUnchanged();
            WriteReport(options.Output, report);
            File.WriteAllText(Path.Combine(options.Output, stem + "-done.json"), JsonSerializer.Serialize(new { success = true, frame, nativeFrame = native }));
        }
    }

    private static async Task<RuntimeSessionClient> WaitForRuntimeSessionAsync(
        SessionRegistry sessions, int processId, CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow <= deadline)
        {
            token.ThrowIfCancellationRequested();
            var session = sessions.Sessions.FirstOrDefault(
                item => item.GameProcessId == processId && item.IsConnected);
            if (session != null) return session;
            await Task.Delay(100, token);
        }

        throw new TimeoutException(
            "No connected Runtime session was registered through the formal Companion control pipe.");
    }

    private static async Task VerifyOverlayAsync(ColliderOverlayController overlay, StartupBootGate gate,
        Dictionary<string, object?> report, CancellationToken token, string label = "sanctum")
    {
        Window? Window() => (Window?)typeof(ColliderOverlayController).GetField("window", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(overlay);
        long frame = gate.NativeCompletedFrames;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Window()?.IsVisible != true && DateTime.UtcNow < deadline) await Task.Delay(50, token);
        Require(Window()?.IsVisible == true, "overlay is visible with live collider data");
        var surface = (FrameworkElement)Window()!.Content;
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine((string)report["output"]!, label + "-overlay-live.png"))) png.Save(file);
        overlay.SetEnabled(false); await Task.Delay(200, token);
        Require(Window()?.IsVisible == false && gate.NativeCompletedFrames == frame, "hide overlay preserves paused native frame");
        overlay.SetEnabled(true);
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (Window()?.IsVisible != true && DateTime.UtcNow < deadline) await Task.Delay(50, token);
        Require(Window()?.IsVisible == true && gate.NativeCompletedFrames == frame, "show overlay preserves paused native frame");
        AddCheck(report, "live overlay visible, hide/show while paused, native frame unchanged, PNG rendered");
    }

    private static bool IsCustomMarmu(JsonElement item) => item.TryGetProperty("components", out var components)
        && components.ValueKind == JsonValueKind.Array
        && components.EnumerateArray().Any(c => c.TryGetProperty("assembly", out var assembly) && assembly.GetString() == "EnviousMarmu");

    private static async Task<string> CaptureCombatSampleAsync(Dictionary<string, object?> report,
        RuntimeSessionClient runtime, StartupBootGate gate, long frame, string previousPopulation,
        bool finalSample, CancellationToken token)
    {
        long native = gate.NativeCompletedFrames;
        Require(gate.IsWaiting, "combat sample starts paused");
        string label = "sample-" + frame.ToString(CultureInfo.InvariantCulture);
        var world = await ReadSnapshotAsync(report, runtime, frame, label + "-world", "world", token);
        using var snapshot = world.Snapshot;
        var objects = snapshot.RootElement.GetProperty("objects");
        var actors = objects.EnumerateArray().Where(IsCustomMarmu).ToArray();
        string population = string.Join("|", actors.Select(o => o.GetProperty("name").GetString()).OrderBy(n => n, StringComparer.Ordinal));
        if (population != previousPopulation || finalSample)
            for (int i = 0; i < actors.Length; i++)
                await ObserveDetailsAsync(report, runtime, actors[i], frame, native, label + "-custom-" + i, token);
        var hero = FindObject(objects, "hero");
        var summary = new
        {
            movieFrame = frame, nativeFrame = native,
            scene = snapshot.RootElement.GetProperty("metadata").GetProperty("activeScene").GetProperty("name").GetString(),
            hero = hero?.GetProperty("hero").GetProperty("resources").Clone(),
            enemies = actors.Where(o => o.TryGetProperty("health", out _)).Select(o => new
            {
                name = o.GetProperty("name").GetString(), health = o.GetProperty("health").Clone(),
                position = o.GetProperty("transform").GetProperty("position").Clone()
            }).ToArray()
        };
        File.WriteAllText(Path.Combine((string)report["output"]!, label + "-summary.json"), JsonSerializer.Serialize(summary, JsonOptions));
        Console.WriteLine("sample " + JsonSerializer.Serialize(summary));
        Require(gate.IsWaiting && gate.NativeCompletedFrames == native, label + " all observations preserve paused frame");
        return population;
    }

    private static MovieV2Document LoadMovie(SequenceFile fixture, string path)
    {
        using var reader = new StringReader(fixture.Movie);
        var parsed = new MovieV2Codec().Parse(reader, path);
        if (!parsed.Success || parsed.Document == null)
            throw new InvalidDataException("Fixture is not a canonical v2 movie.");
        return parsed.Document;
    }

    private static async Task WaitForGateAsync(StartupBootGate gate,
        VerifiedGameLaunchHandle handle, CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!gate.IsAcknowledged)
        {
            token.ThrowIfCancellationRequested();
            if (handle.HasExited) throw new InvalidOperationException("Owned game exited before gate acknowledgement.");
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Full-run startup gate acknowledgement timed out.");
            await Task.Delay(50, token);
        }
        Require(gate.NativeCompletedFrames == 0 && gate.SaveGuardArmed == 1,
            "protected native frame zero and save guard");
    }

    private static async Task MoveToFrameAsync(RuntimeSessionClient runtime,
        FullRunMovieCoordinator movies, StartupBootGate gate, long target,
        CancellationToken token)
    {
        if (gate.IsFullRunFinished) return;
        var status = await RuntimeCommandAsync(runtime, IpcMessageTypes.FullRunStatus,
            IpcMessageTypes.FullRunState,
            new Dictionary<string, string> { ["requestId"] = RequestId() }, token);
        var current = ReadInt64(status, "movieFrame", -1);
        if (current == target) return;
        if (current < 0 || target <= current)
            throw new InvalidOperationException($"Cannot advance from movie={current} to target={target}.");
        var expectedNative = ReadInt64(status, "nativeFrame", gate.NativeCompletedFrames);
        await RuntimeCommandAsync(runtime, IpcMessageTypes.FullRunSeek,
            IpcMessageTypes.CommandAccepted,
            new Dictionary<string, string>
            {
                ["requestId"] = RequestId(),
                ["targetFrame"] = target.ToString(CultureInfo.InvariantCulture),
                ["expectedNativeFrame"] = expectedNative.ToString(CultureInfo.InvariantCulture)
            }, token);
        var boundary = await movies.RunAsync(gate.NativeCompletedFrames, token);
        while (!gate.IsWaiting && !gate.IsFullRunFinished && gate.FullRunFaultCode == 0)
            await Task.Delay(20, token);
        Require(boundary.Mode != "Fault" && gate.FullRunFaultCode == 0,
            "target frame " + target + " reached without native fault");
        var completed = await RuntimeCommandAsync(runtime, IpcMessageTypes.FullRunStatus,
            IpcMessageTypes.FullRunState,
            new Dictionary<string, string> { ["requestId"] = RequestId() }, token);
        Require(ReadInt64(completed, "movieFrame", -1) == target,
            "movie frame " + target + " boundary");
    }

    private static async Task ObserveFrameAsync(Dictionary<string, object?> report,
        RuntimeSessionClient runtime, FullRunMovieCoordinator movies, StartupBootGate gate,
        long expectedMovieFrame, string label, bool includeDetails, CancellationToken token)
    {
        long beforeNative = gate.NativeCompletedFrames;
        Require(gate.IsWaiting, label + " query begins at paused boundary");
        var world = await ReadSnapshotAsync(report, runtime, expectedMovieFrame,
            label + "-world", "world", token);
        RecordScene(report, label, world.Snapshot.RootElement);
        var snapshot = world.Snapshot;
        using var snapshotLifetime = snapshot;
        Require(snapshot.RootElement.TryGetProperty("metadata", out _), label + " metadata");
        Require(snapshot.RootElement.TryGetProperty("objects", out var objects), label + " objects");
        Require(objects.GetArrayLength() > 0, label + " has objects");
        // The title/startup frame can legitimately expose only scene metadata
        // while Unity finishes loading the player object. Keep that observed
        // state in the report; later in-world milestones require the hero.
        if (!string.Equals(label, "title", StringComparison.OrdinalIgnoreCase))
        {
            var heroOverview = FindObject(objects, "hero");
            Require(heroOverview != null, label + " hero object");
            Require(heroOverview!.Value.TryGetProperty("transform", out var transform) && transform.TryGetProperty("position", out _),
                label + " hero position retained in overview");
        }
        Require(FindObject(objects, "enemy") != null || label != "boss",
            label + " enemy/boss object");
        if (label == "sanctum")
        {
            var colliders = await ReadSnapshotAsync(report, runtime, expectedMovieFrame,
                label + "-colliders", "colliders", token);
            RecordScene(report, label + "-colliders", colliders.Snapshot.RootElement);
            var colliderRoot = colliders.Snapshot;
            using var colliderLifetime = colliderRoot;
            Require(colliderRoot.RootElement.TryGetProperty("objects", out var colliderObjects)
                    && colliderObjects.GetArrayLength() > 0,
                label + " collider objects");
            Require(colliderObjects.EnumerateArray().Any(item =>
                item.TryGetProperty("colliders", out var values) && values.GetArrayLength() > 0),
                label + " collider fields");
        }
        if (!includeDetails) return;
        var nativeFrame = ReadInt64(world.Fields, "nativeFrame", -1);
        var hero = FindObject(objects, "hero");
        var enemy = FindObject(objects, "enemy");
        if (hero != null)
            await ObserveDetailsAsync(report, runtime, hero.Value, expectedMovieFrame,
                nativeFrame, label + "-hero", token);
        if (enemy != null)
            await ObserveDetailsAsync(report, runtime, enemy.Value, expectedMovieFrame,
                nativeFrame, label + "-enemy", token);
        int customIndex = 0;
        foreach (var item in objects.EnumerateArray())
            if (item.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Array
                && components.EnumerateArray().Any(c => c.TryGetProperty("assembly", out var assembly) && assembly.GetString() == "EnviousMarmu"))
                await ObserveDetailsAsync(report, runtime, item, expectedMovieFrame, nativeFrame,
                    label + "-custom-" + customIndex++, token);
        if (customIndex > 0)
        {
            var inactive = await ReadSnapshotAsync(report, runtime, expectedMovieFrame, label + "-all", "all", token, true);
            using var inactiveLifetime = inactive.Snapshot;
            var template = inactive.Snapshot.RootElement.GetProperty("objects").EnumerateArray()
                .FirstOrDefault(item => item.TryGetProperty("name", out var name) && name.GetString() == "Marmu Template");
            Require(template.ValueKind == JsonValueKind.Object, "inactive Marmu clone template discovered");
            await ObserveDetailsAsync(report, runtime, template, expectedMovieFrame, nativeFrame, label + "-template", token);
            var activeSceneName = inactive.Snapshot.RootElement.GetProperty("metadata")
                .GetProperty("activeScene").GetProperty("name").GetString();
            var controller = inactive.Snapshot.RootElement.GetProperty("objects").EnumerateArray()
                .FirstOrDefault(item => item.GetProperty("activeInHierarchy").GetBoolean()
                    && item.GetProperty("scene").GetProperty("name").GetString() == activeSceneName
                    && item.TryGetProperty("components", out var controllerComponents)
                    && controllerComponents.ValueKind == JsonValueKind.Array
                    && controllerComponents.EnumerateArray().Any(component =>
                        component.TryGetProperty("type", out var type) && type.GetString() == "BossSceneController"));
            Require(controller.ValueKind == JsonValueKind.Object, label + " active boss scene controller discovered");
            await ObserveDetailsAsync(report, runtime, controller, expectedMovieFrame, nativeFrame, label + "-controller", token);
        }
        Require(gate.IsWaiting && gate.NativeCompletedFrames == beforeNative,
            label + " all world, collider, and detail queries preserve paused native frame");
    }

    private static async Task<SnapshotRead> ReadSnapshotAsync(
        Dictionary<string, object?> report, RuntimeSessionClient runtime,
        long expectedMovieFrame, string label, string view, CancellationToken token, bool includeInactive = false)
    {
        var first = await QueryAsync(runtime, GetWorldSnapshot, WorldSnapshot,
            new Dictionary<string, string>
            {
                ["requestId"] = RequestId(), ["snapshotId"] = string.Empty,
                ["view"] = view, ["includeInactive"] = includeInactive ? "true" : "false",
                ["offset"] = "0", ["limit"] = "64"
            }, label + "-page-0", token);
        AssertFrame(first, expectedMovieFrame, label);
        SaveField(report, label + "-page-0", first, "snapshotJson");
        var snapshotId = ReadField(first, "snapshotId");
        var firstJson = ReadField(first, "snapshotJson");
        using var firstDocument = JsonDocument.Parse(firstJson);
        var root = firstDocument.RootElement;
        Require(root.TryGetProperty("metadata", out var metadata), label + " metadata");
        Require(root.TryGetProperty("objects", out var firstObjects), label + " objects");
        var total = ReadJsonInt64(root, "total", firstObjects.GetArrayLength());
        var allObjects = firstObjects.EnumerateArray().Select(value => value.Clone()).ToList();
        var next = ReadJsonInt64(root, "nextOffset", -1);
        var offset = 0;
        var page = 1;
        while (next >= 0)
        {
            if (next <= offset) throw new InvalidDataException(label + " snapshot pagination did not advance.");
            offset = checked((int)next);
            var fields = await QueryAsync(runtime, GetWorldSnapshot, WorldSnapshot,
                new Dictionary<string, string>
                {
                    ["requestId"] = RequestId(), ["snapshotId"] = snapshotId,
                    ["view"] = view, ["includeInactive"] = includeInactive ? "true" : "false",
                    ["offset"] = offset.ToString(CultureInfo.InvariantCulture), ["limit"] = "64"
                }, label + "-page-" + page, token);
            AssertFrame(fields, expectedMovieFrame, label + " page " + page);
            Require(ReadInt64(fields, "nativeFrame", -1) == ReadInt64(first, "nativeFrame", -2), label + " page native frame unchanged");
            Require(ReadField(fields, "snapshotId") == snapshotId, label + " snapshot id changed");
            var pageJson = ReadField(fields, "snapshotJson");
            SaveField(report, label + "-page-" + page, fields, "snapshotJson");
            using var pageDocument = JsonDocument.Parse(pageJson);
            var pageRoot = pageDocument.RootElement;
            Require(ReadJsonInt64(pageRoot, "total", total) == total, label + " total changed");
            if (pageRoot.TryGetProperty("objects", out var pageObjects))
                allObjects.AddRange(pageObjects.EnumerateArray().Select(value => value.Clone()));
            next = ReadJsonInt64(pageRoot, "nextOffset", -1);
            page++;
        }

        Require(allObjects.Count == total, label + " object total");
        var combined = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schemaVersion"] = root.TryGetProperty("schemaVersion", out var schema) ? schema.Clone() : 1,
            ["snapshotId"] = snapshotId,
            ["nativeFrame"] = ReadInt64(first, "nativeFrame", -1),
            ["movieFrame"] = ReadInt64(first, "movieFrame", -1),
            ["metadata"] = metadata.Clone(), ["total"] = total, ["offset"] = 0,
            ["objects"] = allObjects, ["nextOffset"] = -1
        });
        File.WriteAllText(Path.Combine((string)report["output"]!, label + ".json"), combined);
        return new SnapshotRead(first, JsonDocument.Parse(combined));
    }

    private static void RecordScene(Dictionary<string, object?> report,
        string label, JsonElement snapshotRoot)
    {
        string name = "";
        string path = "";
        if (snapshotRoot.TryGetProperty("metadata", out var metadata)
            && metadata.TryGetProperty("activeScene", out var scene))
        {
            if (scene.TryGetProperty("name", out var nameValue)) name = nameValue.GetString() ?? "";
            if (scene.TryGetProperty("path", out var pathValue)) path = pathValue.GetString() ?? "";
        }
        ((List<object>)report["scenes"]!).Add(new Dictionary<string, object?>
        {
            ["label"] = label, ["activeScene"] = name, ["scenePath"] = path,
            ["note"] = "Actual scene observed at this query; inspect this row if the milestone is still loading or differs from the expected label."
        });
    }

    private sealed class SnapshotRead
    {
        public SnapshotRead(Dictionary<string, string> fields, JsonDocument snapshot)
        {
            Fields = fields; Snapshot = snapshot;
        }
        public Dictionary<string, string> Fields { get; }
        public JsonDocument Snapshot { get; }
    }

    private static async Task ObserveDetailsAsync(Dictionary<string, object?> report,
        RuntimeSessionClient runtime, JsonElement objectValue, long expectedMovieFrame,
        long expectedNativeFrame, string label, CancellationToken token)
    {
        if (!objectValue.TryGetProperty("id", out var idValue)
            || idValue.ValueKind != JsonValueKind.String)
            throw new InvalidDataException(label + " lacks object id.");
        var objectId = idValue.GetString()!;
        var fragments = new List<string>();
        string detailsId = string.Empty;
        string expectedHash = string.Empty;
        var cursor = 0;
        while (true)
        {
            var fields = new Dictionary<string, string>
            {
                ["requestId"] = RequestId(), ["objectId"] = objectId,
                ["cursor"] = cursor.ToString(CultureInfo.InvariantCulture),
                ["maxCharacters"] = "100000"
            };
            fields["expectedNativeFrame"] = expectedNativeFrame.ToString(CultureInfo.InvariantCulture);
            if (detailsId.Length != 0) fields["detailsId"] = detailsId;
            var result = await QueryAsync(runtime, GetObjectDetails,
                ObjectDetails, fields, label + "-part-" + fragments.Count, token);
            AssertFrame(result, expectedMovieFrame, label);
            var pageDetailsId = ReadField(result, "detailsId");
            if (detailsId.Length != 0) Require(pageDetailsId == detailsId, label + " details id changed");
            detailsId = pageDetailsId;
            Require(ReadField(result, "objectId") == objectId, label + " object id changed");
            var pageHash = ReadField(result, "sha256");
            if (expectedHash.Length != 0) Require(pageHash == expectedHash, label + " sha256 changed");
            expectedHash = pageHash;
            fragments.Add(ReadField(result, "detailsJson"));
            SaveField(report, label + ".part-" + (fragments.Count - 1), result, "detailsJson");
            if (ReadField(result, "complete") == "true") break;
            var next = ReadInt64(result, "nextCursor", -1);
            if (next < 0 || next <= cursor) throw new InvalidDataException(label + " pagination did not advance.");
            cursor = checked((int)next);
        }
        var json = string.Concat(fragments);
        using var parsed = JsonDocument.Parse(json);
        Require(parsed.RootElement.TryGetProperty("components", out _)
                || parsed.RootElement.TryGetProperty("object", out _), label + " detail fields");
        using var sha = SHA256.Create();
        var actualHash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        Require(actualHash == expectedHash, label + " sha256");
        File.WriteAllText(Path.Combine((string)report["output"]!, label + ".json"), json);
    }

    private static async Task<Dictionary<string, string>> QueryAsync(RuntimeSessionClient runtime,
        string command, string response, IReadOnlyDictionary<string, string> fields,
        string label, CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await RuntimeCommandAsync(runtime, command, response, fields, token);
        stopwatch.Stop();
        SaveQueryTiming(label, stopwatch.Elapsed.TotalMilliseconds);
        return result;
    }

    private static async Task<Dictionary<string, string>> RuntimeCommandAsync(
        RuntimeSessionClient runtime, string command, string response,
        IReadOnlyDictionary<string, string> fields, CancellationToken token)
    {
        var requestId = fields.TryGetValue("requestId", out var value) ? value : RequestId();
        var completion = new TaskCompletionSource<Dictionary<string, string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnEnvelope(object? _, IpcEnvelope envelope)
        {
            if (envelope.MessageType != response && envelope.MessageType != IpcMessageTypes.CommandRejected) return;
            var payload = IpcPayloadCodec.TryDeserialize(envelope.PayloadUtf8);
            if (!payload.Success || payload.Fields == null
                || !payload.Fields.TryGetValue("requestId", out var received)
                || received != requestId) return;
            if (envelope.MessageType == IpcMessageTypes.CommandRejected)
                completion.TrySetException(new InvalidOperationException(
                    payload.Fields.TryGetValue("detail", out var detail) ? detail : "Runtime command rejected."));
            else completion.TrySetResult(new Dictionary<string, string>(payload.Fields, StringComparer.Ordinal));
        }
        runtime.EnvelopeReceived += OnEnvelope;
        try
        {
            await runtime.SendCommandAsync(command, fields, token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            return await completion.Task.WaitAsync(timeout.Token);
        }
        finally { runtime.EnvelopeReceived -= OnEnvelope; }
    }

    private static void AppendStatus(Dictionary<string, object?> report, string label,
        StartupBootGate gate, FullRunMovieCoordinator movies)
        => ((List<object>)report["status"]!).Add(new Dictionary<string, object?>
        {
            ["label"] = label, ["nativeFrame"] = gate.NativeCompletedFrames,
            ["movieFrame"] = -1, ["mode"] = movies.Mode,
            ["faultCode"] = gate.FullRunFaultCode, ["waiting"] = gate.IsWaiting
        });

    private static async Task<Dictionary<string, string>> RecordStatusAsync(Dictionary<string, object?> report,
        RuntimeSessionClient runtime, string label, CancellationToken token)
    {
        var fields = await RuntimeCommandAsync(runtime, IpcMessageTypes.FullRunStatus,
            IpcMessageTypes.FullRunState,
            new Dictionary<string, string> { ["requestId"] = RequestId() }, token);
        ((List<object>)report["status"]!).Add(fields.ToDictionary(
            pair => pair.Key, pair => (object?)pair.Value,
            StringComparer.Ordinal));
        Console.WriteLine(label + " movie=" + ReadField(fields, "movieFrame") + " scene=" + ReadField(fields, "sceneName") + " mode=" + ReadField(fields, "mode"));
        ((List<object>)report["status"]!).Add(new Dictionary<string, object?>
        {
            ["label"] = label, ["nativeFrame"] = ReadInt64(fields, "nativeFrame", -1),
            ["movieFrame"] = ReadInt64(fields, "movieFrame", -1),
            ["mode"] = ReadField(fields, "mode"), ["faultCode"] = ReadInt64(fields, "faultCode", 0),
            ["mismatchCount"] = ReadInt64(fields, "mismatchCount", 0)
        });
        return fields;
    }

    private static void SaveField(Dictionary<string, object?> report, string label,
        Dictionary<string, string> fields, string field)
    {
        if (!fields.TryGetValue(field, out var value)) throw new InvalidDataException(label + " omitted " + field);
        var output = (string)report["output"]!;
        File.WriteAllText(Path.Combine(output, label + ".json"), value);
    }

    private static JsonDocument ParseObject(Dictionary<string, string> fields, string name)
        => JsonDocument.Parse(ReadField(fields, name));

    private static JsonElement? FindObject(JsonElement objects, string kind)
    {
        foreach (var item in objects.EnumerateArray())
            if (item.TryGetProperty("kind", out var value)
                && value.GetString()?.Equals(kind, StringComparison.OrdinalIgnoreCase) == true)
                return item;
        return null;
    }

    private static void AssertFrame(Dictionary<string, string> fields, long movieFrame, string label)
    {
        Require(ReadInt64(fields, "movieFrame", -1) == movieFrame, label + " movie frame");
        Require(ReadInt64(fields, "nativeFrame", -1) >= 0, label + " native frame");
    }

    private static long ReadFrame(JsonElement value, string name, long fallback)
        => value.TryGetProperty(name, out var field) && field.TryGetInt64(out var parsed) ? parsed : fallback;

    private static long ReadJsonInt64(JsonElement value, string name, long fallback)
        => value.TryGetProperty(name, out var field) && field.TryGetInt64(out var parsed)
            ? parsed : fallback;

    private static string ReadField(Dictionary<string, string> fields, string name)
        => fields.TryGetValue(name, out var value) ? value : throw new InvalidDataException("Missing field " + name);

    private static long ReadInt64(Dictionary<string, string> fields, string name, long fallback)
        => fields.TryGetValue(name, out var value)
           && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : fallback;

    private static string RequestId() => "world-observe-" + Guid.NewGuid().ToString("N");

    private static void Require(bool value, string detail)
    {
        if (!value) throw new InvalidDataException(detail);
        if (currentReport != null) AddCheck(currentReport, detail);
    }

    private static void AddCheck(Dictionary<string, object?> report, string detail)
        => ((List<string>)report["checks"]!).Add(detail);

    private static void SaveQueryTiming(string label, double milliseconds)
    {
        if (currentReport == null) return;
        ((List<object>)currentReport["queries"]!).Add(
            new Dictionary<string, object?>
            {
                ["label"] = label,
                ["milliseconds"] = milliseconds
            });
    }

    private static void WriteReport(string output, Dictionary<string, object?> report)
    {
        lock (report) File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, JsonOptions));
    }

    private static async Task WaitForExitAsync(VerifiedGameLaunchHandle handle, CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!handle.HasExited)
        {
            token.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Owned game did not exit after quit.");
            await Task.Delay(100, token);
        }
    }

    private sealed class ResourceMonitor
    {
        private readonly Dictionary<string, object?> report;
        private readonly bool relaxedResources;
        private readonly CancellationToken token;
        private VerifiedGameLaunchHandle? handle;
        private Task? task;
        public ResourceMonitor(Dictionary<string, object?> report, bool relaxedResources, CancellationToken token) { this.report = report; this.relaxedResources = relaxedResources; this.token = token; }
        public void Start(VerifiedGameLaunchHandle value) { handle = value; task = Task.Run(RunAsync); }
        public void Stop() { try { task?.Wait(TimeSpan.FromSeconds(1)); } catch { } }
        private async Task RunAsync()
        {
            var watchdog = Stopwatch.StartNew();
            while (!token.IsCancellationRequested)
            {
                using var process = Process.GetCurrentProcess();
                process.Refresh();
                long gameBytes = 0;
                try { using var game = Process.GetProcessById(handle!.ProcessId); game.Refresh(); gameBytes = game.PrivateMemorySize64; } catch { }
                var row = new Dictionary<string, object?>
                {
                    ["utc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    ["hostPrivateBytes"] = process.PrivateMemorySize64,
                    ["gamePrivateBytes"] = gameBytes
                };
                lock (report) ((List<object>)report["resources"]!).Add(row);
                bool memoryExceeded = !relaxedResources && (process.PrivateMemorySize64 > 1024L * 1024 * 1024 || gameBytes > 3L * 1024 * 1024 * 1024);
                if (watchdog.Elapsed.TotalMinutes > ((string)report["mode"]! == "interactive" ? 30 : 4) || memoryExceeded)
                {
                    // Independent of the UI dispatcher: terminate only the test-owned game,
                    // then this harness, even if a managed await or native message pump is stuck.
                    try
                    {
                        File.WriteAllText(Path.Combine((string)report["output"]!, "watchdog-failure.json"),
                            JsonSerializer.Serialize(new { success = false, reason = "resourceOrTimeBudget", seconds = watchdog.Elapsed.TotalSeconds,
                                hostBytes = process.PrivateMemorySize64, gameBytes }));
                        using var game = Process.GetProcessById(handle!.ProcessId);
                        if (game.StartTime.ToUniversalTime().Ticks == handle.ProcessStartedAtUtc.UtcTicks) game.Kill();
                    }
                    catch { }
                    Environment.Exit(72);
                }
                try { await Task.Delay(1000, token); } catch (OperationCanceledException) { }
            }
        }
    }

    private sealed class Options
    {
        public string Output = Path.Combine("artifacts", "world-observation");
        public string Mode = "observe";
        public string Fixture = Path.Combine("fixtures", "full-run", "false-knight-startup-v2.hktas");
        public string Game = DefaultGame;
        public string Bundle = DefaultBundle;
        public bool Overlay;
        public bool RelaxedResources;
        public string? VideoOutput;
        public string? Ffmpeg;
        public long VideoStart = 1400;
        public long BossFrame = 8500;
        public long[] SampleFrames = Array.Empty<long>();
        public static Options Parse(string[] args)
        {
            var result = new Options();
            foreach (var arg in args)
            {
                if (arg.StartsWith("--output=", StringComparison.Ordinal)) result.Output = Path.GetFullPath(arg.Substring(9));
                else if (arg.StartsWith("--mode=", StringComparison.Ordinal)) result.Mode = arg.Substring(7);
                else if (arg.StartsWith("--fixture=", StringComparison.Ordinal)) result.Fixture = Path.GetFullPath(arg.Substring(10));
                else if (arg.StartsWith("--game=", StringComparison.Ordinal)) result.Game = Path.GetFullPath(arg.Substring(7));
                else if (arg.StartsWith("--bundle=", StringComparison.Ordinal)) result.Bundle = Path.GetFullPath(arg.Substring(9));
                else if (arg == "--overlay") result.Overlay = true;
                else if (arg == "--relaxed-resources") result.RelaxedResources = true;
                else if (arg.StartsWith("--video-output=", StringComparison.Ordinal)) result.VideoOutput = Path.GetFullPath(arg.Substring(15));
                else if (arg.StartsWith("--ffmpeg=", StringComparison.Ordinal)) result.Ffmpeg = Path.GetFullPath(arg.Substring(9));
                else if (arg.StartsWith("--video-start=", StringComparison.Ordinal)) result.VideoStart = long.Parse(arg.Substring(14), CultureInfo.InvariantCulture);
                else if (arg.StartsWith("--boss-frame=", StringComparison.Ordinal)) result.BossFrame = long.Parse(arg.Substring(13), CultureInfo.InvariantCulture);
                else if (arg.StartsWith("--sample-frames=", StringComparison.Ordinal)) result.SampleFrames = arg.Substring(16).Split(',').Select(s => long.Parse(s, CultureInfo.InvariantCulture)).Distinct().OrderBy(f => f).ToArray();
            }
            if (result.Mode != "baseline" && result.Mode != "observe" && result.Mode != "probe" && result.Mode != "interactive") throw new ArgumentException("--mode must be baseline, observe, probe, or interactive.");
            if (result.VideoOutput != null && (result.Ffmpeg == null || result.Mode != "observe" || result.VideoStart < 1 || result.VideoStart > 1500))
                throw new ArgumentException("Video requires --mode=observe, --ffmpeg and video start within frames 1..1500.");
            return result;
        }
    }
}
