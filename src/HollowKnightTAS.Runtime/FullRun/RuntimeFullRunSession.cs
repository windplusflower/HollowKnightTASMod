using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Reflection;
using System.Threading;
using System.Linq;
using System.Globalization;
using GlobalEnums;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Runtime.Input;
using HollowKnightTAS.Runtime.Timing;
using HollowKnightTAS.Runtime.Rng;
using HollowKnightTAS.Runtime.Observation;

namespace HollowKnightTAS.Runtime.FullRun
{
    public sealed class FullRunResult
    {
        public FullRunResult(bool success, string error, long frameIndex, string movieId)
        {
            if (frameIndex < 0) throw new ArgumentOutOfRangeException(nameof(frameIndex));
            Success = success;
            Error = error ?? throw new ArgumentNullException(nameof(error));
            FrameIndex = frameIndex;
            MovieId = movieId ?? throw new ArgumentNullException(nameof(movieId));
        }
        public bool Success { get; }
        public string Error { get; }
        public long FrameIndex { get; }
        public string MovieId { get; }
    }

    public sealed class FullRunStatus
    {
        public FullRunStatus(string mode, long nativeFrame, long movieFrame,
            long skippedLoadFrames,
            string frameBoundary, bool runtimeInputReady,
            long mismatchCount, string error, string sceneName, int saveSlot,
            string heroX, string heroY, string respawnScene, int heroHealth,
            bool bossSceneEntered, bool bossDeathObserved,
            bool bossesDeadObserved, bool bossSceneCompleteObserved,
            long bossDeathFrame, long bossSceneEntryMovieFrame)
        {
            if (nativeFrame < 0 || movieFrame < 0 || skippedLoadFrames < 0
                || mismatchCount < 0 || saveSlot < 0)
                throw new ArgumentOutOfRangeException(nameof(nativeFrame));
            Mode = mode ?? throw new ArgumentNullException(nameof(mode));
            NativeFrame = nativeFrame;
            MovieFrame = movieFrame;
            SkippedLoadFrames = skippedLoadFrames;
            FrameBoundary = frameBoundary ?? throw new ArgumentNullException(nameof(frameBoundary));
            RuntimeInputReady = runtimeInputReady;
            MismatchCount = mismatchCount;
            Error = error ?? throw new ArgumentNullException(nameof(error));
            SceneName = sceneName ?? throw new ArgumentNullException(nameof(sceneName));
            SaveSlot = saveSlot;
            HeroX = heroX ?? throw new ArgumentNullException(nameof(heroX));
            HeroY = heroY ?? throw new ArgumentNullException(nameof(heroY));
            RespawnScene = respawnScene ?? throw new ArgumentNullException(nameof(respawnScene));
            HeroHealth = heroHealth;
            BossSceneEntered = bossSceneEntered;
            BossDeathObserved = bossDeathObserved;
            BossesDeadObserved = bossesDeadObserved;
            BossSceneCompleteObserved = bossSceneCompleteObserved;
            BossDeathFrame = bossDeathFrame;
            BossSceneEntryMovieFrame = bossSceneEntryMovieFrame;
        }
        public string Mode { get; }
        public long NativeFrame { get; }
        public long MovieFrame { get; }
        public long SkippedLoadFrames { get; }
        public string FrameBoundary { get; }
        public bool RuntimeInputReady { get; }
        public long MismatchCount { get; }
        public string Error { get; }
        public string SceneName { get; }
        public int SaveSlot { get; }
        public string HeroX { get; }
        public string HeroY { get; }
        public string RespawnScene { get; }
        public int HeroHealth { get; }
        public bool BossSceneEntered { get; }
        public bool BossDeathObserved { get; }
        public bool BossesDeadObserved { get; }
        public bool BossSceneCompleteObserved { get; }
        public long BossDeathFrame { get; }
        public long BossSceneEntryMovieFrame { get; }
    }

    public sealed partial class RuntimeFullRunSession : IDisposable
    {
        private readonly NativeFullRunFrameClock clock;
        private readonly FullRunActionSetAdapter input;
        private readonly FullRunMouseBridge mouse;
        private readonly string sessionDirectory;
        private readonly FrameObservationQueue observationQueue;
        private readonly RuntimeWorldObserver worldObserver = new RuntimeWorldObserver();
        private readonly WorldObservationCache observationCache = new WorldObservationCache();
        private readonly RuntimeInfoTiming infoTiming = new RuntimeInfoTiming();
        private FullRunFrameJournal? journal;
        private MovieV2Document? replayMovie;
        private bool editableReplay;
        private MovieV2Header? recordingHeader;
        private MovieV2Document? recordedMovie;
        private string recordedCanonical = string.Empty;
        private long replayLength;
        private long bootstrapFrame = -1;
        private long movieFrame;
        private long skippedLoadFrames;
        private long expectedNativeStart = -1;
        private bool frameInputEnabled;
        private bool titleReadySeen;
        private bool returningToMainMenu;
        private bool returnToMainMenuHooked;
        private bool firstLevelActivationPending;
        private string frameBoundary = "Bootstrap";
        private UIManager? uiManager;
        private static readonly FieldInfo MainMenuScreenField = typeof(UIManager).GetField(
            "mainMenuScreen", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException("UIManager.mainMenuScreen");
        private long mismatchCount;
        private string mode = "Idle";
        private string error = string.Empty;
        private bool inputReady;
        private bool mouseEnabled;
        private bool disposed;
        private BossSceneController? boundBossController;
        private readonly System.Collections.Generic.List<HealthManager> boundBosses =
            new System.Collections.Generic.List<HealthManager>();
        private bool bossSceneEntered;
        private bool bossDeathObserved;
        private bool bossesDeadObserved;
        private bool bossSceneCompleteObserved;
        private long bossDeathFrame = -1;
        private long bossSceneEntryMovieFrame = -1;
        private readonly bool bossTraceEnabled = string.Equals(
            Environment.GetEnvironmentVariable("HKTAS_FULL_RUN_BOSS_TRACE"),
            "1", StringComparison.Ordinal);
        private readonly StringBuilder bossTrace = new StringBuilder("nativeFrame,movieFrame,scene,heroX,heroY,heroHealth,bossHp,bossX,bossY,bossDead,time,fixedTime,deltaTime,frameCount,rngSha256\n");
        private int bossTraceRows;
        private UnityRandomStateCodec_1_5_78_11833? bossTraceRngCodec;
        private EventWaitHandle? randomSeedRequest;
        private EventWaitHandle? randomSeedAcknowledged;
        private long randomSeedRequestNativeFrame = -1;
        private int randomSeedSceneHandle = -1;
        private bool randomSeedHandshakePending;
        private readonly ReplayRenderSuppression restoreDrawing = new ReplayRenderSuppression();

        public RuntimeFullRunSession(NativeFullRunFrameClock clock,
            FullRunActionSetAdapter input, FullRunMouseBridge mouse,
            string sessionDirectory)
        {
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.input = input ?? throw new ArgumentNullException(nameof(input));
            this.mouse = mouse ?? throw new ArgumentNullException(nameof(mouse));
            input.SetNativeFrameRunning(() => !clock.IsPaused);
            if (string.IsNullOrWhiteSpace(sessionDirectory))
                throw new ArgumentException("Full-run session directory is required.", nameof(sessionDirectory));
            this.sessionDirectory = Path.GetFullPath(sessionDirectory);
            Directory.CreateDirectory(this.sessionDirectory);
            observationQueue = new FrameObservationQueue(clock.RequestObservation);
            clock.RegisterObservation(observationQueue.Service);
            On.GameManager.Update += OnGameManagerTiming;
        }

        public Dictionary<string, string> ObserveWorld(IReadOnlyDictionary<string, string> fields)
        {
            ValidateObservationFields(fields, "snapshotId", "view", "includeInactive", "offset", "limit", "watches");
            string Value(string key, string fallback) => fields.TryGetValue(key, out var value) ? value : fallback;
            var id = Value("snapshotId", "");
            var view = Value("view", "world");
            if (view == "fsms")
            {
                if (fields.Keys.Any(key => key != "requestId" && key != "view" && key != "watches"))
                    throw new ArgumentException("FSM observations accept only view and watches.");
                var json = Value("watches", "[]");
                if (json.Length > 16000) throw new ArgumentException("FSM request is too large.");
                var targets = Newtonsoft.Json.JsonConvert.DeserializeObject<string[]>(json)
                    ?? throw new ArgumentException("watches must be an array.");
                if (targets.Length > 32 || targets.Any(t => t == null || t.Length > 256))
                    throw new ArgumentException("At most 32 FSM targets of 256 characters are supported.");
                return observationQueue.Invoke(frame => worldObserver.CaptureFsms(frame, movieFrame, targets));
            }
            if (view == "info")
            {
                if (Value("snapshotId", "").Length != 0 || Value("offset", "0") != "0")
                    throw new ArgumentException("Info observations are single frame captures without pagination.");
                var watchJson = Value("watches", "[]");
                if (watchJson.Length > 20000) throw new ArgumentException("Too many custom watch characters.");
                var watches = Newtonsoft.Json.JsonConvert.DeserializeObject<string[]>(watchJson)
                    ?? throw new ArgumentException("watches must be an array of field paths.");
                if (watches.Length > 32 || watches.Any(w => w == null || w.Length > 512))
                    throw new ArgumentException("At most 32 custom watches of up to 512 characters are supported.");
                return observationQueue.Invoke(frame => RuntimeInfoObservation.Capture(frame, movieFrame, watches,
                    infoTiming.RealSeconds, infoTiming.GameSeconds, infoTiming.Error));
            }
            if (fields.ContainsKey("watches")) throw new ArgumentException("watches requires the info view.");
            if (view != "world" && view != "all" && view != "colliders" && view != "fsmCatalog") throw new ArgumentException("Unknown observation view.");
            var inactive = Value("includeInactive", "false");
            if (inactive != "true" && inactive != "false") throw new ArgumentException("includeInactive must be true or false.");
            var offset = int.Parse(Value("offset", "0"), CultureInfo.InvariantCulture);
            var limit = int.Parse(Value("limit", "64"), CultureInfo.InvariantCulture);
            if (id.Length > 96 || offset < 0 || limit < 1 || limit > 128)
                throw new ArgumentException("Snapshot pagination arguments are invalid.");
            if (id.Length == 0)
            {
                if (offset != 0) throw new ArgumentException("First snapshot request must start at offset zero.");
                var capture = observationQueue.Invoke(frame => Tuple.Create(frame, movieFrame,
                    worldObserver.Capture(frame, movieFrame, view, inactive == "true")));
                id = observationCache.AddSnapshot(capture.Item1, capture.Item2, capture.Item3.MetadataJson,
                    capture.Item3.Objects.Select(x => Tuple.Create(x.Id, x.Kind, x.Json)), view);
            }
            return observationCache.ReadSnapshot(id, offset, limit);
        }

        public Dictionary<string, string> ObserveObject(IReadOnlyDictionary<string, string> fields)
        {
            ValidateObservationFields(fields, "objectId", "expectedNativeFrame", "detailsId", "cursor", "maxCharacters");
            string Value(string key, string fallback) => fields.TryGetValue(key, out var value) ? value : fallback;
            var objectId = Value("objectId", "");
            if (objectId.Length == 0 || objectId.Length > 128) throw new ArgumentException("objectId is required.");
            var id = Value("detailsId", "");
            long? expected = fields.TryGetValue("expectedNativeFrame", out var expectedText)
                ? long.Parse(expectedText, CultureInfo.InvariantCulture) : (long?)null;
            var cursor = int.Parse(Value("cursor", "0"), CultureInfo.InvariantCulture);
            var maximum = int.Parse(Value("maxCharacters", "100000"), CultureInfo.InvariantCulture);
            if (id.Length > 96 || expected < 0 || cursor < 0 || maximum < 1024 || maximum > 200000)
                throw new ArgumentException("Details pagination arguments are invalid.");
            if (id.Length == 0)
            {
                if (cursor != 0) throw new ArgumentException("First details request must start at cursor zero.");
                var capture = observationQueue.Invoke(frame =>
                {
                    if (expected.HasValue && expected.Value != frame)
                        throw new InvalidOperationException("Expected native frame is stale; pause and obtain a current snapshot.");
                    return Tuple.Create(frame, movieFrame, worldObserver.CaptureObjectDetails(objectId, frame, movieFrame));
                });
                id = observationCache.AddDetails(objectId, capture.Item1, capture.Item2, capture.Item3);
            }
            return observationCache.ReadDetails(id, objectId, expected, cursor, maximum);
        }

        private static void ValidateObservationFields(IReadOnlyDictionary<string, string> fields, params string[] optional)
        {
            if (!fields.TryGetValue("requestId", out var requestId) || string.IsNullOrWhiteSpace(requestId)
                || fields.Keys.Any(key => key != "requestId" && !optional.Contains(key)))
                throw new ArgumentException("Observation request fields are invalid.");
        }

        public MovieV2Document? RecordedMovie => recordedMovie;
        public string RecordedCanonicalText => recordedCanonical;
        public string RecordedMoviePath => recordedMovie == null ? string.Empty
            : Path.Combine(sessionDirectory, "full-run", "movie.hktas");
        public bool MouseEnabled => mouseEnabled;
        public string Mode => mode;
        public double ClockStepSeconds => clock.StepSeconds;
        public decimal ActiveFrameRate => activeFrameRate;

        private decimal recordingFrameRate = 50;
        private decimal activeFrameRate = 50;
        private long pauseAtMovieFrame = -1;
        private int timingRunIndex;
        private long timingRunStart;
        public void ConfigureTiming(decimal fps, long target)
        {
            if (!MovieFrameRate.IsValid(fps) || target < -1 || target > MovieProtocolV2.MaximumExpandedFrames)
                throw new ArgumentOutOfRangeException(nameof(fps));
            recordingFrameRate = fps;
            pauseAtMovieFrame = target;
        }
        public void SetPauseTarget(long target, long expectedNativeFrame)
        {
            if (IsVideoExportActive) throw new InvalidOperationException("Finish or cancel video export before seeking the Movie.");
            if (!clock.IsPaused || clock.CurrentFrameIndex != expectedNativeFrame || target <= movieFrame)
                throw new InvalidOperationException("Target requires a paused boundary and a future Movie frame.");
            restoreDrawing.Dispose();
            pauseAtMovieFrame = target;
        }

        public string SnapshotMovie()
        {
            if (!clock.IsPaused) throw new InvalidOperationException("Movie snapshot requires a paused boundary.");
            var movie = mode == "Recording" ? journal!.Freeze(recordingHeader!, movieFrame) : replayMovie ?? recordedMovie;
            if (movie == null) throw new InvalidOperationException("No Movie is available.");
            var path = Path.Combine(sessionDirectory, "full-run", "snapshot-" + new MovieV2Codec().ComputeMovieId(movie).Substring(0, 32) + ".hktas");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path)) File.WriteAllText(path, new MovieV2Codec().WriteCanonical(movie), new UTF8Encoding(false));
            return path;
        }

        public void UpdateFutureMovie(string path, long expectedNativeFrame)
        {
            if (IsVideoExportActive) throw new InvalidOperationException("Finish or cancel video export before editing the Movie.");
            if (!clock.IsPaused || clock.CurrentFrameIndex != expectedNativeFrame || !inputReady
                || (mode != "Recording" && mode != "Replay"))
                throw new InvalidOperationException("Pause at an input-ready boundary before updating the Movie.");
            var root = Path.GetFullPath(Path.Combine(sessionDirectory, "..", "..")) + Path.DirectorySeparatorChar;
            path = Path.GetFullPath(path);
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || new FileInfo(path).Length > MovieProtocolV2.MaximumSourceUtf8Bytes)
                throw new InvalidDataException("Movie update must be inside the protected session.");
            MovieV2Document candidate;
            using (var reader = File.OpenText(path))
                candidate = new MovieV2Codec().Parse(reader, path).Document
                    ?? throw new InvalidDataException("Invalid Movie update.");
            var validation = new MovieV2Validator().Validate(candidate, MovieV2ValidationContext.CreateDefault());
            if (!validation.Success || CountFrames(candidate) <= movieFrame)
                throw new InvalidDataException("Updated Movie must contain the next input frame.");
            var original = mode == "Recording" ? journal!.Freeze(recordingHeader!, movieFrame) : replayMovie!;
            if (!MovieV2Prefix.Matches(original, candidate, movieFrame))
                throw new InvalidOperationException("过去的输入已经改变，请使用应用并重放到 Frame。");
            input.ReplaceFutureMovie(candidate, movieFrame);
            input.Sampled -= OnSampled;
            mouse.UseReplayInputs();
            replayMovie = candidate;
            editableReplay = true;
            replayLength = CountFrames(candidate);
            timingRunIndex = 0;
            timingRunStart = 0;
            mode = "Replay";
        }

        public FullRunResult BeginRecording(bool mouseEnabled, long expectedFrame)
        {
            if (mode != "Idle" || expectedFrame < 0 || clock.CurrentFrameIndex != expectedFrame)
                return Reject("NativeFrameMismatch");
            try
            {
                this.mouseEnabled = mouseEnabled;
                journal = new FullRunFrameJournal(sessionDirectory);
                bootstrapFrame = expectedFrame;
                clock.RegisterCompleted(OnNativeCompleted);
                mode = "Recording";
                return new FullRunResult(true, string.Empty, expectedFrame, string.Empty);
            }
            catch (Exception exception)
            {
                Fail("BootstrapInputFault: " + exception.Message);
                return Reject(error);
            }
        }

        public FullRunResult BeginReplay(MovieV2Document movie,
            MovieV2CompatibilityReport compatibility, long expectedFrame)
        {
            if (movie == null || compatibility == null || !compatibility.Allowed
                || mode != "Idle" || expectedFrame < 0 || clock.CurrentFrameIndex != expectedFrame)
                return Reject("ReplayBootstrapRejected");
            try
            {
                replayLength = CountFrames(movie);
                if (replayLength < 1)
                    return Reject("EmptyFullRunMovie");
                mouseEnabled = movie.Header.MouseEnabled;
                replayMovie = movie;
                bootstrapFrame = expectedFrame;
                clock.RegisterCompleted(OnNativeCompleted);
                mode = "Replay";
                return new FullRunResult(true, string.Empty, expectedFrame,
                    new MovieV2Codec().ComputeMovieId(movie));
            }
            catch (Exception exception)
            {
                Fail("ReplayBootstrapFault: " + exception.Message);
                return Reject(error);
            }
        }

        public void SetRecordingHeader(MovieV2Header header)
        {
            if (header == null) throw new ArgumentNullException(nameof(header));
            if (mode != "Recording")
                throw new InvalidOperationException("Recording metadata requires an active full-run recording.");
            recordingHeader = header;
        }

        private FullRunStatus? observedWorld;
        private IReadOnlyDictionary<string, string> observedBindings = new Dictionary<string, string>();
        private IReadOnlyDictionary<string, string> observedFrameTiming = new Dictionary<string, string>();
        private int nextBindingsRefresh;
        private bool observationFaultLogged;
        public IReadOnlyDictionary<string, string> ReadBindingLabels() => Volatile.Read(ref observedBindings);
        public IReadOnlyDictionary<string, string> ReadFrameTiming() => Volatile.Read(ref observedFrameTiming);
        private long lastGameUpdateNativeFrame = -1;
        private double gameUpdateDeltaSeconds, gameUpdateTimeSeconds, executedMovieSeconds;

        private void OnGameManagerTiming(On.GameManager.orig_Update original, GameManager self)
        {
            var nativeFrame = clock.CurrentFrameIndex;
            if (inputReady && frameInputEnabled && nativeFrame != lastGameUpdateNativeFrame)
            {
                lastGameUpdateNativeFrame = nativeFrame;
                gameUpdateDeltaSeconds = UnityEngine.Time.deltaTime;
                gameUpdateTimeSeconds = UnityEngine.Time.timeAsDouble;
                executedMovieSeconds += gameUpdateDeltaSeconds;
            }
            original(self);
        }

        public FullRunStatus GetStatus()
        {
            // IPC runs on a worker. Unity object APIs must stay on the PlayerLoop.
            var world = Volatile.Read(ref observedWorld);
            return new FullRunStatus(mode, clock.CurrentFrameIndex, movieFrame,
                skippedLoadFrames, frameBoundary, inputReady, mismatchCount, error,
                world?.SceneName ?? string.Empty, world?.SaveSlot ?? 0,
                world?.HeroX ?? string.Empty, world?.HeroY ?? string.Empty,
                world?.RespawnScene ?? string.Empty, world?.HeroHealth ?? 0,
                bossSceneEntered, bossDeathObserved, bossesDeadObserved,
                bossSceneCompleteObserved, bossDeathFrame, bossSceneEntryMovieFrame);
        }

        private FullRunStatus CaptureWorldStatus()
        {
            var hero = HeroController.SilentInstance;
            var position = hero == null ? default(UnityEngine.Vector3) : hero.transform.position;
            return new FullRunStatus(mode, clock.CurrentFrameIndex, movieFrame,
                skippedLoadFrames, frameBoundary, inputReady,
                mismatchCount, error,
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? string.Empty,
                GameManager.instance?.profileID ?? 0,
                hero == null ? string.Empty : position.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                hero == null ? string.Empty : position.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                PlayerData.instance?.respawnScene ?? string.Empty,
                PlayerData.instance?.health ?? 0,
                bossSceneEntered, bossDeathObserved, bossesDeadObserved,
                bossSceneCompleteObserved, bossDeathFrame, bossSceneEntryMovieFrame);
        }

        public FullRunResult Stop(long expectedFrame)
        {
            if (IsVideoExportActive) throw new InvalidOperationException("Finish or cancel video export before stopping the Movie.");
            if (expectedFrame < 0 || expectedFrame != clock.CurrentFrameIndex || !clock.IsPaused)
                return Reject("NativeFrameMismatch");
            if (mode == "Recording")
            {
                try
                {
                    clock.RequestPause();
                    if (recordingHeader == null)
                        return Reject("RecordingEnvironmentUnresolved");
                    recordedMovie = journal?.Freeze(recordingHeader, movieFrame)
                        ?? throw new InvalidOperationException("Full-run recording journal is missing.");
                    recordedCanonical = new MovieV2Codec().WriteCanonical(recordedMovie);
                    var id = new MovieV2Codec().ComputeMovieId(recordedMovie);
                    var finalPath = Path.Combine(sessionDirectory, "full-run", "movie.hktas");
                    Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                    using (var stream = new FileStream(finalPath, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        var bytes = new UTF8Encoding(false, true).GetBytes(recordedCanonical);
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                    clock.Finish();
                    mode = "Stopped";
                    return new FullRunResult(true, string.Empty, expectedFrame, id);
                }
                catch (Exception exception)
                {
                    Fail("RecordingFreezeFault: " + exception.Message);
                    return Reject(error);
                }
            }
            if (mode == "Replay" || mode == "Completed")
            {
                if (!clock.IsFinished) clock.Finish();
                mode = "Stopped";
                return new FullRunResult(true, string.Empty, expectedFrame,
                    replayMovie == null ? string.Empty
                        : new MovieV2Codec().ComputeMovieId(replayMovie));
            }
            return Reject("FullRunNotActive");
        }

        private void OnSampled(long frame, GameInputSample sample, ulong inputTick)
        {
            if (mode == "Recording")
                journal?.Append(frame, sample, inputTick);
        }

        private void OnInputFault(string message)
        {
            Fail("InputMismatch: " + message);
        }

        private void OnNativeBeforeFrame(long completed)
        {
            restoreDrawing.Active = false;
            if (!inputReady || (mode != "Recording" && mode != "Replay")) return;
            try
            {
                if (mode == "Replay" && movieFrame >= replayLength)
                    throw new InvalidOperationException("Extend the editable Movie before continuing past its end.");
                if (completed != expectedNativeStart)
                    throw new InvalidDataException("Native frame start was not sequential.");
                var ready = IsMovieFrameReady(out var boundary);
                var sceneHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
                if (ready && sceneHandle != randomSeedSceneHandle)
                {
                    if (randomSeedRequest == null || randomSeedAcknowledged == null)
                    {
                        var runId = Environment.GetEnvironmentVariable("HKTAS_CLOCK_RUN_ID");
                        if (string.IsNullOrEmpty(runId))
                            throw new InvalidOperationException("External clock run ID is unavailable.");
                        var prefix = "HollowKnightTAS.V2.RngSync." + runId;
                        randomSeedRequest = new EventWaitHandle(false,
                            EventResetMode.AutoReset, prefix + ".request");
                        randomSeedAcknowledged = new EventWaitHandle(false,
                            EventResetMode.ManualReset, prefix + ".applied");
                    }
                    if (!randomSeedHandshakePending)
                    {
                        randomSeedAcknowledged.Reset();
                        randomSeedRequestNativeFrame = completed;
                        randomSeedHandshakePending = true;
                        randomSeedRequest.Set();
                    }
                    if (!randomSeedAcknowledged.WaitOne(0))
                    {
                        if (completed - randomSeedRequestNativeFrame > 120)
                            throw new InvalidOperationException("External random seed handshake timed out.");
                        ready = false;
                        boundary = "RandomSeedHandshake";
                    }
                    else
                    {
                        randomSeedSceneHandle = sceneHandle;
                        randomSeedHandshakePending = false;
                    }
                }
                activeFrameRate = recordingFrameRate;
                if (replayMovie != null)
                {
                    while (timingRunIndex + 1 < replayMovie.Runs.Count && movieFrame >= timingRunStart + replayMovie.Runs[timingRunIndex].RepeatCount)
                        timingRunStart += replayMovie.Runs[timingRunIndex++].RepeatCount;
                    activeFrameRate = replayMovie.Runs[timingRunIndex].FramesPerSecond;
                    // Loading/scene seed handshake frames do not consume Movie frames.
                    // This callback runs on the game thread before the native PlayerLoop.
                    // Apply after scene synchronization, once for the input-ready frame;
                    // never reuse the scene-sync helper (it advances the scene epoch).
                    if (ready && replayMovie.Runs[timingRunIndex].RngSeed is int seed)
                    {
                        UnityEngine.Random.InitState(seed);
                        Modding.Logger.LogDebug("[HKTAS] Movie RNG seed: frame=" + movieFrame
                            + " native=" + completed + " seed=" + seed);
                    }
                }
                clock.SetFrameRate(ready ? activeFrameRate : 50);
                DeterministicCinematics.FrameDuration = 1d / (double)(ready ? activeFrameRate : 50m);
                infoTiming.BeginFrame(movieFrame, completed, ready, boundary);
                if (ready) TraceMovieRng("before", replayMovie?.Runs[timingRunIndex].RngSeed);
                frameInputEnabled = ready;
                // Resume drawing before the target so temporal presentation can
                // settle using only frames already belonging to the replay.
                restoreDrawing.Active = ready && mode == "Replay" && !IsVideoExportActive
                    && pauseAtMovieFrame > movieFrame + 32;
                frameBoundary = boundary;
                input.SetFrameInputEnabled(ready);
                mouse.SetFrameInputEnabled(ready);
                // Loading and RNG synchronization still execute a native frame.
                // Keep the hook chain alive so the clock can acknowledge the seed;
                // SetFrameInputEnabled above separately suppresses Movie samples.
                input.SetNativeFrameActive(true);
            }
            catch (Exception exception)
            {
                Fail("FrameBoundaryFault: " + exception.Message);
            }
        }

        private bool IsMovieFrameReady(out string boundary)
        {
            if (DeterministicCinematics.Failure != null)
                throw new InvalidOperationException("Cinematic synchronization failed: " + DeterministicCinematics.Failure);
            var manager = GameManager.instance;
            if (manager == null)
            {
                boundary = "GameManagerUnavailable";
                return false;
            }
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            // NewGame sets PLAYING before awaiting the first world scene. The
            // active Knight_Pickup hero is not proof that that load has finished.
            if (firstLevelActivationPending)
            {
                boundary = "FirstLevelActivation";
                return false;
            }
            if (manager.IsInSceneTransition)
            {
                boundary = "SceneTransition";
                return false;
            }
            if (scene == "Menu_Title" && manager.gameState == GameState.MAIN_MENU)
            {
                uiManager = uiManager == null
                    ? UnityEngine.Object.FindObjectOfType<UIManager>() : uiManager;
                if (uiManager == null || uiManager.IsFadingMenu)
                {
                    boundary = "TitleLoading";
                    return false;
                }
                var screen = MainMenuScreenField.GetValue(uiManager) as UnityEngine.CanvasGroup;
                if (!titleReadySeen && (screen == null || !screen.interactable))
                {
                    boundary = "TitleLoading";
                    return false;
                }
                titleReadySeen = true;
                returningToMainMenu = false;
                boundary = "TitleInput";
                return true;
            }
            titleReadySeen = false;
            if (returningToMainMenu || scene == "Quit_To_Menu")
            {
                boundary = "QuitToMenuLoading";
                return false;
            }
            if (manager.gameState != GameState.PLAYING
                && manager.gameState != GameState.PAUSED
                && manager.gameState != GameState.CUTSCENE)
            {
                boundary = "GameState:" + manager.gameState;
                return false;
            }
            if (manager.gameState == GameState.CUTSCENE)
            {
                // CutsceneInput/Skip are valid without an active hero. Do not
                // consume these scenes as unrecorded loading PlayerLoops.
                boundary = "CutsceneInput";
                return true;
            }
            var hero = HeroController.SilentInstance;
            if (hero == null || !hero.gameObject.activeInHierarchy)
            {
                boundary = "HeroLoading";
                return false;
            }
            boundary = "GameplayInput";
            return true;
        }

        private void OnNativeCompleted(long completed)
        {
            if (mode != "Recording" && mode != "Replay") return;
            input.SetNativeFrameActive(false);
            try
            {
                if (!inputReady && completed - 1 == bootstrapFrame)
                {
                    StartInputAt(completed);
                    return;
                }
                if (!inputReady || completed != expectedNativeStart + 1)
                    throw new InvalidDataException("Native frame completion was not sequential.");
                expectedNativeStart = completed;
                infoTiming.CompleteFrame(clock.StepSeconds);
                if (!frameInputEnabled)
                {
                    skippedLoadFrames++;
                    return;
                }
                input.CompleteFrame(movieFrame);
                if (input.Fault.Length != 0)
                {
                    Fail("InputMismatch: " + input.Fault);
                    return;
                }
                ObserveBoss();
                TraceMovieRng("after", null);
                if (mode == "Recording") journal!.CompleteFrame(movieFrame, activeFrameRate);
                movieFrame++;
                clock.ReportMovieFrameCompleted();
                if (movieFrame == pauseAtMovieFrame)
                {
                    infoTiming.FlushTrace(sessionDirectory);
                    // The hidden-launch environment survives after the window is
                    // revealed. Retire these hooks so later visible seeks never
                    // inherit restore-only drawing suppression.
                    restoreDrawing.Dispose();
                    pauseAtMovieFrame = -1;
                    clock.RequestPause();
                    if (bossTraceEnabled)
                    {
                        try
                        {
                            File.WriteAllText(Path.Combine(sessionDirectory, "boss-trace.csv"),
                                bossTrace.ToString(), new UTF8Encoding(false));
                        }
                        catch (Exception diagnosticError)
                        {
                            Modding.Logger.LogWarn("[HKTAS] Restore trace unavailable: " + diagnosticError.Message);
                        }
                    }
                }
                if (mode == "Replay" && movieFrame == replayLength)
                {
                    if (editableReplay)
                    {
                        // Live edits must stay resumable in this process. Finished is
                        // terminal and used to force a cold restart on the next Play.
                        clock.RequestPause();
                        input.PrepareFrame(movieFrame, atReplayEnd: true);
                        return;
                    }
                    if (bossTraceEnabled)
                        File.WriteAllText(Path.Combine(sessionDirectory, "boss-trace.csv"),
                            bossTrace.ToString(), new UTF8Encoding(false));
                    mode = "Completed";
                    clock.Finish();
                    return;
                }
                if (mode == "Replay" && movieFrame > replayLength)
                    throw new InvalidDataException("Replay exceeded the movie frame count.");
                input.PrepareFrame(movieFrame);
            }
            catch (Exception exception)
            {
                Fail("FrameCompletionFault: " + exception.Message);
            }
            finally
            {
                restoreDrawing.EndFrame();
                CaptureVideoFrame(completed);
                TraceReplayState(completed);
                try
                {
                    Volatile.Write(ref observedWorld, CaptureWorldStatus());
                    // Sample Unity on its thread; IPC only reads this completed-frame snapshot.
                    Volatile.Write(ref observedFrameTiming, new Dictionary<string, string>
                    {
                        ["unityDeltaSeconds"] = UnityEngine.Time.deltaTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                        ["unityUpdateDeltaSeconds"] = gameUpdateDeltaSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                        ["unityUpdateTimeSeconds"] = gameUpdateTimeSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                        ["unityExecutedMovieSeconds"] = executedMovieSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                        ["unityUnscaledDeltaSeconds"] = UnityEngine.Time.unscaledDeltaTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                        ["unityCaptureDeltaSeconds"] = UnityEngine.Time.captureDeltaTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                        ["unityTimeSeconds"] = UnityEngine.Time.timeAsDouble.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                        ["unityTimeScale"] = UnityEngine.Time.timeScale.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                        ["unityTargetFrameRate"] = UnityEngine.Application.targetFrameRate.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    });
                    var now = Environment.TickCount;
                    if (unchecked(now - nextBindingsRefresh) >= 0)
                    {
                        nextBindingsRefresh = unchecked(now + 200);
                        Volatile.Write(ref observedBindings, input.ReadBindingLabels());
                    }
                    observationFaultLogged = false;
                }
                catch (Exception exception)
                {
                    if (!observationFaultLogged) Modding.Logger.LogWarn("TAS progress observation failed: " + exception.Message);
                    observationFaultLogged = true;
                }
            }
        }

        private void StartInputAt(long frame)
        {
            mouse.Configure(input, mode == "Replay");
            if (!mouse.TryInstall(mouseEnabled, out var mouseError))
                throw new InvalidOperationException("MouseBridgeUnavailable: " + mouseError);
            input.Faulted += OnInputFault;
            if (mode == "Recording")
            {
                input.Sampled += OnSampled;
                input.StartRecording();
            }
            else input.StartReplay(replayMovie
                ?? throw new InvalidOperationException("Replay movie is missing."));
            input.PrepareFrame(0);
            On.GameManager.ReturnToMainMenu += OnReturnToMainMenu;
            On.GameManager.OnWillActivateFirstLevel += OnWillActivateFirstLevel;
            On.GameManager.OnNextLevelReady += OnNextLevelReady;
            returnToMainMenuHooked = true;
            expectedNativeStart = frame;
            clock.RegisterBeforeFrame(OnNativeBeforeFrame);
            inputReady = true;
            // The bootstrap PlayerLoop installs input without consuming Movie frame 0.
            // Honour a zero target here so full-video export can include its first input.
            if (pauseAtMovieFrame == 0) { restoreDrawing.Dispose(); pauseAtMovieFrame = -1; clock.RequestPause(); }
        }

        private System.Collections.IEnumerator OnReturnToMainMenu(
            On.GameManager.orig_ReturnToMainMenu original, GameManager self,
            GameManager.ReturnToMainMenuSaveModes saveMode, Action<bool> callback)
        {
            returningToMainMenu = true;
            firstLevelActivationPending = false;
            return original(self, saveMode, callback);
        }

        private void OnWillActivateFirstLevel(On.GameManager.orig_OnWillActivateFirstLevel original,
            GameManager self)
        {
            firstLevelActivationPending = true;
            Modding.Logger.LogDebug("[HKTAS] First-level activation pending at Movie " + movieFrame);
            original(self);
        }

        private void OnNextLevelReady(On.GameManager.orig_OnNextLevelReady original, GameManager self)
        {
            // Keep the gate closed through vanilla setup and its callbacks. Hero
            // entry animation retains the existing gameplay readiness policy.
            original(self);
            if (!firstLevelActivationPending) return;
            firstLevelActivationPending = false;
            Modding.Logger.LogDebug("[HKTAS] First-level activation complete at Movie " + movieFrame);
        }

        private void ObserveBoss()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (bossTraceEnabled && bossSceneEntered && bossTraceRows < 4096)
            {
                bossTraceRows++;
                var hero = HeroController.instance;
                var bosses = BossSceneController.Instance?.bosses;
                var boss = bosses != null && bosses.Length > 0 ? bosses[0] : null;
                if (bossTraceRngCodec == null)
                    bossTraceRngCodec = UnityRandomStateCodec_1_5_78_11833.Resolve().Codec;
                bossTrace.Append(clock.CurrentFrameIndex).Append(',')
                    .Append(movieFrame).Append(',')
                    .Append(scene).Append(',')
                    .Append(hero == null ? string.Empty : hero.transform.position.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                    .Append(hero == null ? string.Empty : hero.transform.position.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                    .Append(PlayerData.instance?.health ?? 0).Append(',')
                    .Append(boss == null ? string.Empty : boss.hp.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                    .Append(boss == null ? string.Empty : boss.transform.position.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                    .Append(boss == null ? string.Empty : boss.transform.position.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                    .Append(boss != null && boss.isDead ? "true" : "false").Append(',')
                    .Append(UnityEngine.Time.time.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                    .Append(UnityEngine.Time.fixedTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                    .Append(UnityEngine.Time.deltaTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                    .Append(UnityEngine.Time.frameCount).Append(',')
                    .Append(bossTraceRngCodec == null ? string.Empty : bossTraceRngCodec.CaptureCurrent().Sha256).Append('\n');
            }
            if (scene != "GG_False_Knight") return;
            if (bossSceneEntryMovieFrame < 0) bossSceneEntryMovieFrame = movieFrame;
            bossSceneEntered = true;
            var controller = BossSceneController.Instance;
            if (controller == null || !controller.gameObject.activeInHierarchy) return;
            if (ReferenceEquals(controller, boundBossController))
            {
                BindBosses(controller);
                return;
            }
            UnbindBoss();
            boundBossController = controller;
            controller.OnBossesDead += OnBossesDead;
            controller.OnBossSceneComplete += OnBossSceneComplete;
            BindBosses(controller);
        }

        private void BindBosses(BossSceneController controller)
        {
            foreach (var boss in controller.bosses ?? Array.Empty<HealthManager>())
            {
                if (boss == null || boundBosses.Contains(boss)) continue;
                boss.OnDeath += OnBossDeath;
                boundBosses.Add(boss);
            }
        }

        private void OnBossDeath()
        {
            bossDeathObserved = true;
            if (bossDeathFrame < 0) bossDeathFrame = clock.CurrentFrameIndex;
        }

        private void OnBossesDead() => bossesDeadObserved = true;

        private void OnBossSceneComplete() => bossSceneCompleteObserved = true;

        private void UnbindBoss()
        {
            foreach (var boss in boundBosses)
                if (boss != null) boss.OnDeath -= OnBossDeath;
            boundBosses.Clear();
            if (boundBossController != null)
            {
                boundBossController.OnBossesDead -= OnBossesDead;
                boundBossController.OnBossSceneComplete -= OnBossSceneComplete;
            }
            boundBossController = null;
        }

        private void Fail(string detail)
        {
            if (mode == "Failed") return;
            mode = "Failed";
            error = detail;
            mismatchCount++;
            // An input or before-frame failure may prevent OnNativeCompleted from
            // running at all. Stop offline audio before faulting the native gate,
            // because that gate no longer services the observation queue in Fault.
            // Active exports only exist in Replay: their failures enter here from
            // Unity's input/native callbacks, never the worker's Recording freeze.
            FailVideoExport(detail);
            if (bossTraceEnabled && bossTraceRows > 0)
            {
                try
                {
                    File.WriteAllText(Path.Combine(sessionDirectory, "boss-trace-fault.csv"),
                        bossTrace.ToString(), new UTF8Encoding(false));
                }
                catch { }
            }
            try { clock.Fault(41); } catch { }
            try
            {
                File.WriteAllText(Path.Combine(sessionDirectory, "full-run-fault.txt"),
                    "frame=" + clock.CurrentFrameIndex + "\n" + detail + "\n",
                    new UTF8Encoding(false));
            }
            catch { }
        }

        private FullRunResult Reject(string reason)
            => new FullRunResult(false, reason, Math.Max(0, clock.CurrentFrameIndex),
                string.Empty);

        private static long CountFrames(MovieV2Document movie)
        {
            long total = 0;
            foreach (var run in movie.Runs) total = checked(total + run.RepeatCount);
            return total;
        }

        public void Dispose()
        {
            infoTiming.FlushTrace(sessionDirectory);
            if (disposed) return;
            disposed = true;
            On.GameManager.Update -= OnGameManagerTiming;
            replayStateTrace?.Dispose();
            restoreDrawing.Dispose();
            videoCapture?.Dispose();
            observationQueue.Dispose();
            if (returnToMainMenuHooked)
            {
                On.GameManager.ReturnToMainMenu -= OnReturnToMainMenu;
                On.GameManager.OnWillActivateFirstLevel -= OnWillActivateFirstLevel;
                On.GameManager.OnNextLevelReady -= OnNextLevelReady;
            }
            input.Sampled -= OnSampled;
            input.Faulted -= OnInputFault;
            UnbindBoss();
            input.Dispose();
            mouse.Dispose();
            randomSeedRequest?.Dispose();
            randomSeedAcknowledged?.Dispose();
        }
    }
}
