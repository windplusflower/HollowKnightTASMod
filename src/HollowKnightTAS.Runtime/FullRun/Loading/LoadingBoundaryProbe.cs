using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;
using USceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowKnightTAS.Runtime.FullRun
{
    // P0 diagnostics only: hashes are deliberately not a restorable checkpoint.
    // This probe neither changes RNG nor enumerates the live Unity object graph.
    internal sealed class LoadingBoundaryProbe : IDisposable
    {
        private const int MaximumRows = 50000;
        private const long MaximumBytes = 32L * 1024 * 1024;
        private readonly string path;
        private long first;
        private readonly long end;
        private Tuple<long, long>[] windows;
        private int headerBytes;
        private long? checkpointWindow;
        private readonly Func<long> movieFrame;
        private readonly Func<long> nativeFrame;
        private readonly Func<string> rngHash;
        private readonly MemoryStream buffer = new MemoryStream();
        private readonly List<Action> detach = new List<Action>();
        private int rows;
        private int transition;
        private bool sealedTrace;
        private bool disposed;
        private readonly bool light = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_LIGHT") == "1";
        private readonly LoadingRandomCallTrace? randomTrace;
        private readonly long randomFirst;
        private readonly long randomEnd;
        private readonly bool continuousRandomCalls = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_CALL_CONTINUOUS") == "1";
        private readonly LoadingRigidbodyObservation rigidbodies;
        private readonly HashSet<long> tweenCallerFrames = new HashSet<long>();
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private LoadingBoundaryProbe(string directory, long first, long end,
            Func<long> movieFrame, Func<long> nativeFrame, Func<string> rngHash)
        {
            path = Path.Combine(directory, "loading-boundary-probe.jsonl");
            this.first = first;
            this.end = end;
            windows = ReadWindows(first, end);
            this.movieFrame = movieFrame;
            this.nativeFrame = nativeFrame;
            this.rngHash = rngHash;
            rigidbodies = new LoadingRigidbodyObservation();
            LoadingEngineClockProbe.Capture(directory);
            randomFirst = long.TryParse(Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_CALL_FIRST"), out var rf) ? Math.Max(first, rf) : first;
            randomEnd = long.TryParse(Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_CALL_END"), out var re) ? Math.Min(end, re) : end;
            if (continuousRandomCalls && (randomEnd <= randomFirst || randomEnd - randomFirst > 256))
                throw new InvalidOperationException("Continuous random-call diagnostics require an explicit interval of at most 256 Movie frames.");
            WriteHeader();
            if (Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_CALLS") == "1")
                randomTrace = new LoadingRandomCallTrace(SampleRandom);
            GameManager.SceneTransitionBegan += TransitionBegan;
            USceneManager.sceneLoaded += SceneLoaded;
            USceneManager.sceneUnloaded += SceneUnloaded;
            USceneManager.activeSceneChanged += ActiveSceneChanged;
        }

        private void WriteHeader()
        {
            Write(new { kind = "header", schema = "hktas-loading-probe-v1", first, end,
                windows = windows.Select(window => new { first = window.Item1, end = window.Item2 }).ToArray(),
                observerModuleId = typeof(LoadingBoundaryProbe).Module.ModuleVersionId.ToString("D"),
                rigidbodyObservation = LoadingRigidbodyValueBits.Policy,
                nativeProfile = HollowKnightTAS.Core.Movie.MovieProtocolV2.SelectedNativeProfileId,
                experimentalExecution = new {
                    disableLegacyCalibration = Environment.GetEnvironmentVariable("HKTAS_LOADING_DISABLE_LEGACY_CALIBRATION") == "1",
                    canonicalStartupClock = Environment.GetEnvironmentVariable("HKTAS_LOADING_CANONICAL_STARTUP_CLOCK") == "1",
                    synchronousFetch = Environment.GetEnvironmentVariable("HKTAS_LOADING_SYNCHRONOUS_FETCH") == "1",
                    modWallClock = Environment.GetEnvironmentVariable("HKTAS_LOADING_MOD_WALL_CLOCK") == "1" },
                coverage = light ? "clock-hero-boss-sequence-only" : "clock-hero-boss-playerdata-scenedata-sequence-only",
                randomCalls = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_CALLS") == "1" ? "Assembly-CSharp-nongeneric" : "off",
                randomFirst, randomEnd, continuousRandomCalls,
                checkpointWindow,
                completeStateCoverage = false });
            headerBytes = checked((int)buffer.Length);
        }

        internal void IncludeCheckpointK(long k)
        {
            if (Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_CHECKPOINT_K") != "1") return;
            if (sealedTrace || checkpointWindow.HasValue || k < 0 || k > end - 3)
                throw new InvalidOperationException("Checkpoint probe window cannot be bound at this boundary.");
            var merged = new List<Tuple<long, long>>();
            foreach (var window in windows.Concat(new[] { Tuple.Create(k, k + 3) }).OrderBy(w => w.Item1))
            {
                if (merged.Count == 0 || merged[merged.Count - 1].Item2 < window.Item1) merged.Add(window);
                else
                {
                    var previous = merged[merged.Count - 1];
                    merged[merged.Count - 1] = Tuple.Create(previous.Item1, Math.Max(previous.Item2, window.Item2));
                }
            }
            if (merged.Count > 17 || merged.Sum(w => w.Item2 - w.Item1) > 128)
                throw new InvalidOperationException("Checkpoint probe exceeds its short-window budget.");
            // Evidence is still an in-memory bounded stream. Preserve any
            // earlier samples and replace only its declared header before seal.
            var samples = buffer.ToArray().Skip(headerBytes).ToArray();
            windows = merged.ToArray(); first = windows[0].Item1; checkpointWindow = k;
            buffer.SetLength(0); buffer.Position = 0; WriteHeader();
            buffer.Write(samples, 0, samples.Length);
        }

        public static LoadingBoundaryProbe? Create(string directory, Func<long> movieFrame,
            Func<long> nativeFrame, Func<string> rngHash)
        {
            if (Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE") != "1") return null;
            if (!long.TryParse(Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_FIRST"), out var first)
                || !long.TryParse(Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_END"), out var end)
                || first < 0 || end <= first || end - first > 10000)
                throw new InvalidOperationException("Loading probe requires a bounded [first,end) window of 1..10000 Movie frames.");
            return new LoadingBoundaryProbe(directory, first, end, movieFrame, nativeFrame, rngHash);
        }

        private static readonly string[] NailSlashBindings = { "normalSlash", "alternateSlash", "upSlash", "downSlash", "wallSlash" };
        private static readonly FieldInfo[] NailSlashScalarFields = CheckpointScalarFields.Declared(typeof(NailSlash))
            .Where(f => f.FieldType.IsPrimitive || f.FieldType.IsEnum || f.FieldType == typeof(string) || f.FieldType == typeof(Vector3)).ToArray();
        private static string NailSlashScalarDigest(HeroController hero)
        {
            var values = NailSlashBindings.Select(binding =>
            {
                var slash = (NailSlash?)typeof(HeroController).GetField(binding)?.GetValue(hero);
                return slash == null ? binding + ":missing" : binding + ":" + string.Join(";",
                    CheckpointScalarFields.Capture(slash, NailSlashScalarFields).Select(pair => pair.Key + "=" + pair.Value));
            });
            return HollowKnightTAS.Core.Cryptography.Sha256Utility.ComputeHex(System.Text.Encoding.UTF8.GetBytes(string.Join("|", values)));
        }

        public void Sample(string phase, string boundary)
        {
            if (sealedTrace || !InWindow(movieFrame())) return;
            try
            {
                var state = new SortedDictionary<string, string>(StringComparer.Ordinal);
                void Add(string key, object? value) => state.Add(key, Scalar(value));
                Add("time", Time.time);
                Add("timeDouble", Time.timeAsDouble);
                Add("fixedTime", Time.fixedTime);
                Add("fixedTimeDouble", Time.fixedTimeAsDouble);
                Add("unscaledTime", Time.unscaledTime);
                Add("realtime", Time.realtimeSinceStartup);
                Add("deltaTime", Time.deltaTime);
                Add("fixedDeltaTime", Time.fixedDeltaTime);
                Add("frameCount", Time.frameCount);
                Add("timeScale", Time.timeScale);
                Add("rng", rngHash());
                // Never let an observation lazily find/register a singleton during teardown.
                var manager = GameManager.UnsafeInstance;
                Add("gameState", manager == null ? "missing" : manager.gameState.ToString());
                Add("inTransition", manager != null && manager.IsInSceneTransition);
                var hero = HeroController.SilentInstance;
                Add("heroPresent", hero != null);
                if (hero != null)
                {
                    var body = hero.GetComponent<Rigidbody2D>();
                    var position = hero.transform.position;
                    var values = body == null ? new LoadingRigidbodyValueBits.Primary(float.NaN, float.NaN, float.NaN, float.NaN)
                        : rigidbodies.CapturePrimary(body, position);
                    Add("heroX", position.x);
                    Add("heroY", position.y);
                    var scale = hero.transform.localScale;
                    Add("heroScaleX", scale.x);
                    Add("heroScaleY", scale.y);
                    Add("heroScaleZ", scale.z);
                    Add("heroVx", values.Vx);
                    Add("heroVy", values.Vy);
                    Add("heroBodyX", values.X);
                    Add("heroBodyY", values.Y);
                    Add("heroState", hero.hero_state);
                    Add("nailSlashes", NailSlashScalarDigest(hero));
                    if (!light) Add("heroFlags", HashFields(hero.cState));
                    if (Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_FIELDS") == "hero")
                    {
                        var cameras = GameCameras.instance;
                        if (cameras != null && cameras.mainCamera != null && cameras.cameraTarget != null)
                        {
                            var cameraPosition = cameras.mainCamera.transform.position;
                            var targetPosition = cameras.cameraTarget.transform.position;
                            Add("cameraX", cameraPosition.x); Add("cameraY", cameraPosition.y); Add("cameraZ", cameraPosition.z);
                            Add("cameraTargetX", targetPosition.x); Add("cameraTargetY", targetPosition.y);
                            Add("cameraMode", cameras.cameraController.mode); Add("cameraTargetMode", cameras.cameraTarget.mode);
                            Add("cameraFadeState", cameras.cameraFadeFSM.ActiveStateName); Add("cameraShakeState", cameras.cameraShakeFSM.ActiveStateName);
                            Add("cameraParentX", cameras.cameraParent.position.x); Add("cameraParentY", cameras.cameraParent.position.y);
                        }
                        Add("heroLayer", hero.gameObject.layer);
                        if (body != null)
                        {
                            Add("heroBodyType", body.bodyType);
                            Add("heroGravityScale", body.gravityScale);
                            Add("heroSimulated", body.simulated);
                        }
                        var collider = hero.GetComponent<Collider2D>();
                        if (collider != null)
                        {
                            Add("heroColliderEnabled", collider.enabled);
                            Add("heroColliderTrigger", collider.isTrigger);
                        }
                        foreach (var field in CheckpointScalarFields.Declared(typeof(HeroControllerStates)))
                            Add("heroFlag." + field.Name, field.GetValue(hero.cState));
                        var fsms = hero.GetComponents<PlayMakerFSM>().OrderBy(fsm => fsm.FsmName, StringComparer.Ordinal).ToArray();
                        for (var i = 0; i < fsms.Length; i++) Add("heroFsm." + i + "." + fsms[i].FsmName, fsms[i].ActiveStateName);
                        foreach (var value in CheckpointHeroLightState.Observe(hero)) Add(value.Key, value.Value);
                        var buzzer = GameObject.Find("Angry Buzzer");
                        if (buzzer != null)
                        {
                            var control = buzzer.GetComponents<PlayMakerFSM>().SingleOrDefault(f => f.FsmName == "Control");
                            if (control != null)
                            {
                                var fsm = (HutongGames.PlayMaker.Fsm)typeof(PlayMakerFSM).GetField("fsm", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(control);
                                var active = (HutongGames.PlayMaker.FsmState?)typeof(HutongGames.PlayMaker.Fsm).GetField("activeState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fsm);
                                Add("buzzerState", active?.Name);
                                var actions = active == null ? null : (HutongGames.PlayMaker.FsmStateAction[]?)typeof(HutongGames.PlayMaker.FsmState)
                                    .GetField("actions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(active);
                                var buzz = actions?.SingleOrDefault(a => a.GetType().FullName == "HutongGames.PlayMaker.Actions.IdleBuzz");
                                if (buzz != null)
                                    foreach (var field in buzz.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Where(f => f.FieldType == typeof(float)))
                                        Add("buzzer." + field.Name, field.GetValue(buzz));
                            }
                        }
                        foreach (var name in new[] { "Bursting Bouncer (3)", "Bursting Bouncer (4)", "Bursting Bouncer (5)" })
                        {
                            var enemy = GameObject.Find(name);
                            var prefix = "bouncer." + name + ".";
                            Add(prefix + "present", enemy != null);
                            if (enemy == null) continue;
                            var enemyPosition = enemy.transform.position;
                            Add(prefix + "x", enemyPosition.x); Add(prefix + "y", enemyPosition.y);
                            var enemyBody = enemy.GetComponent<Rigidbody2D>();
                            if (enemyBody != null)
                            {
                                var enemyValues = rigidbodies.CapturePrimary(enemyBody, enemyPosition);
                                Add(prefix + "vx", enemyValues.Vx); Add(prefix + "vy", enemyValues.Vy);
                            }
                            foreach (var fsm in enemy.GetComponents<PlayMakerFSM>().Where(f => f.FsmName == "Bouncer Control" || f.FsmName == "Drip"))
                                Add(prefix + fsm.FsmName, HollowKnightTAS.Core.Cryptography.Sha256Utility.ComputeHex(
                                    Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(CheckpointHeroSpellVariables.Capture(fsm)))));
                        }
                        foreach (var geo in CheckpointGeoState.Observe(rigidbodies)) Add(geo.Key, geo.Value);
                        foreach (var spatter in CheckpointSpatterState.Observe(rigidbodies)) Add(spatter.Key, spatter.Value);
                        foreach (var projectile in CheckpointProjectileState.Observe(rigidbodies)) Add(projectile.Key, projectile.Value);
                    }
                }
                // Field-only recursive hashing: avoids getters and does not publish save data.
                if (!light)
                {
                    Add("playerData", manager == null ? "missing" : HashFields(manager.playerData));
                    Add("sceneData", manager == null ? "missing" : HashFields(manager.sceneData));
                    if (manager != null && Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_FIELDS") == "playerData")
                        foreach (var field in CheckpointScalarFields.Declared(typeof(PlayerData)))
                        {
                            var value = field.GetValue(manager.playerData);
                            Add("pd." + field.Name, field.FieldType.IsPrimitive || field.FieldType.IsEnum
                                ? value : HashFields(value));
                        }
                }
                Add("bossSequenceIndex", BossSequenceController.BossIndex);
                Add("bossSequenceActive", BossSequenceController.IsInSequence);
                Add("bossSequenceTimer", BossSequenceController.Timer);
                var bossController = BossSceneController.Instance;
                var boss = bossController != null && bossController.bosses != null && bossController.bosses.Length > 0
                    ? bossController.bosses[0] : null;
                Add("bossPresent", boss != null);
                if (boss != null)
                {
                    Add("bossHealth", boss.hp); Add("bossDead", boss.isDead);
                    var body = boss.GetComponent<Rigidbody2D>();
                    var position = boss.transform.position;
                    Add("bossX", position.x); Add("bossY", position.y);
                    var values = body == null ? new LoadingRigidbodyValueBits.Primary(float.NaN, float.NaN, float.NaN, float.NaN)
                        : rigidbodies.CapturePrimary(body, position);
                    Add("bossVx", values.Vx); Add("bossVy", values.Vy);
                }
                if (!light) Add("heroHealth", manager == null ? -1 : manager.playerData.health);
                Write(new { kind = "sample", sequence = rows, phase, boundary,
                    movieFrame = movieFrame(), nativeFrame = nativeFrame(), transition,
                    scene = USceneManager.GetActiveScene().name, state });
                rows++;
            }
            catch (Exception exception) { Seal(false, exception.GetType().Name + ": " + exception.Message); }
        }

        public void Completed()
        {
            if (!sealedTrace && movieFrame() == end) Seal(true, "window-complete");
        }

        public void Failed(string detail)
        {
            // The native fault gate stops engine callbacks. Preserve the bounded
            // evidence already collected before entering that terminal state.
            if (!sealedTrace) Seal(false, "runtime-fault: " + detail);
        }

        private void SampleRandom(string phase, string site, object? self)
        {
            if (sealedTrace || !continuousRandomCalls && !InWindow(movieFrame()) || movieFrame() < randomFirst || movieFrame() >= randomEnd) return;
            try
            {
                if (phase == "rng-before" && site.StartsWith("System.String iTween::GenerateID()", StringComparison.Ordinal)
                    && tweenCallerFrames.Add(nativeFrame()))
                {
                    var caller = new System.Diagnostics.StackTrace(false).ToString();
                    Write(new { kind = "timer-call", sequence = rows, phase = "tween-create-caller",
                        boundary = caller.Substring(0, Math.Min(8192, caller.Length)), movieFrame = movieFrame(), nativeFrame = nativeFrame(),
                        scene = USceneManager.GetActiveScene().name, owner = "iTween.GenerateID", state = new { rng = rngHash() } });
                    rows++;
                }
                Write(new { kind = phase.StartsWith("rng-", StringComparison.Ordinal) ? "rng-call" : "timer-call", sequence = rows, phase, boundary = site,
                    movieFrame = movieFrame(), nativeFrame = nativeFrame(),
                    scene = USceneManager.GetActiveScene().name,
                    owner = LoadingRandomCallTrace.Describe(self),
                    state = new { rng = rngHash(), frameCount = Time.frameCount,
                        time = Scalar(Time.time), fixedTime = Scalar(Time.fixedTime), deltaTime = Scalar(Time.deltaTime) } });
                rows++;
            }
            catch (Exception exception) { Seal(false, exception.GetType().Name + ": " + exception.Message); }
        }

        private void TransitionBegan(SceneLoad load)
        {
            transition++;
            Sample("event", "transition-began:" + load.TargetSceneName);
            SceneLoad.FetchCompleteDelegate fetch = () => Sample("event", "fetch-complete");
            SceneLoad.WillActivateDelegate activate = () => Sample("event", "will-activate");
            SceneLoad.ActivationCompleteDelegate activated = () => Sample("event", "activation-complete");
            SceneLoad.CompleteDelegate complete = () => Sample("event", "load-complete");
            SceneLoad.StartCalledDelegate started = () => Sample("event", "start-called");
            SceneLoad.FinishDelegate finish = null!;
            Action unregister = () => { };
            Action remove = () =>
            {
                load.FetchComplete -= fetch;
                load.WillActivate -= activate;
                load.ActivationComplete -= activated;
                load.Complete -= complete;
                load.StartCalled -= started;
                load.Finish -= finish;
                unregister();
            };
            finish = () => { Sample("event", "load-finish"); remove(); detach.Remove(remove); };
            load.FetchComplete += fetch;
            load.WillActivate += activate;
            load.ActivationComplete += activated;
            load.Complete += complete;
            load.StartCalled += started;
            load.Finish += finish;
            detach.Add(remove);
            unregister = CanonicalSceneLoadDiagnosticRegistry.Register(load, this, new Dictionary<string, Delegate> {
                ["FetchComplete"] = fetch, ["WillActivate"] = activate, ["ActivationComplete"] = activated,
                ["Complete"] = complete, ["StartCalled"] = started, ["Finish"] = finish });
        }

        private void SceneLoaded(Scene scene, LoadSceneMode mode) => Sample("event", "scene-loaded:" + scene.name + ":" + mode);
        private void SceneUnloaded(Scene scene) => Sample("event", "scene-unloaded:" + scene.name);
        private void ActiveSceneChanged(Scene previous, Scene current) => Sample("event", "active-scene:" + previous.name + "->" + current.name);

        private bool InWindow(long frame) => windows.Any(window => frame >= window.Item1 && frame < window.Item2);
        private static Tuple<long, long>[] ReadWindows(long first, long end)
        {
            var text = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_WINDOWS");
            if (string.IsNullOrEmpty(text)) return new[] { Tuple.Create(first, end) };
            var result = new List<Tuple<long, long>>(); var previousEnd = first; long sampledFrames = 0;
            foreach (var interval in text!.Split(','))
            {
                var bounds = interval.Split(':');
                if (bounds.Length != 2 || !long.TryParse(bounds[0], out var start) || !long.TryParse(bounds[1], out var stop)
                    || start < previousEnd || stop > end || stop <= start || stop - start > 16)
                    throw new InvalidOperationException("Loading probe windows must be ordered short intervals inside [first,end).");
                result.Add(Tuple.Create(start, stop)); previousEnd = stop; sampledFrames += stop - start;
            }
            if (result.Count > 16 || sampledFrames > 128 || result[0].Item1 != first || previousEnd != end)
                throw new InvalidOperationException("Loading probe short-window budget or endpoints differ.");
            return result.ToArray();
        }

        private void Write(object row)
        {
            var bytes = Utf8.GetBytes(JsonConvert.SerializeObject(row, Formatting.None) + "\n");
            if (rows >= MaximumRows || buffer.Length + bytes.Length > MaximumBytes)
                throw new InvalidOperationException("Loading probe budget exceeded; trace is incomplete.");
            buffer.Write(bytes, 0, bytes.Length);
        }

        private void Seal(bool success, string detail)
        {
            if (sealedTrace) return;
            sealedTrace = true;
            try
            {
                using (var hash = SHA256.Create())
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    buffer.Position = 0;
                    var digest = Hex(hash.ComputeHash(buffer));
                    buffer.Position = 0;
                    buffer.CopyTo(file);
                    var footer = Utf8.GetBytes(JsonConvert.SerializeObject(new { kind = "footer", success,
                        detail, rows, first, end, sha256 = digest }) + "\n");
                    file.Write(footer, 0, footer.Length);
                }
            }
            catch (Exception exception)
            {
                Modding.Logger.LogError("[HKTAS] Loading probe evidence failed: " + exception);
            }
            finally { buffer.Dispose(); }
        }

        private static string Scalar(object? value)
        {
            if (value == null) return "null";
            if (value is float f) return "f32:" + BitConverter.ToInt32(BitConverter.GetBytes(f), 0).ToString("x8");
            if (value is double d) return "f64:" + BitConverter.DoubleToInt64Bits(d).ToString("x16");
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }

        private static string HashFields(object? value)
        {
            var builder = new StringBuilder();
            var count = 0;
            Append(value, builder, 0, ref count);
            using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(Utf8.GetBytes(builder.ToString())));
        }

        private static void Append(object? value, StringBuilder builder, int depth, ref int count)
        {
            if (++count > 16000 || depth > 12 || builder.Length > 1024 * 1024)
                throw new InvalidOperationException("Diagnostic data graph exceeds bounded field coverage.");
            if (value == null) { builder.Append("null;"); return; }
            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is string || value is decimal)
            {
                var text = Scalar(value);
                builder.Append(type.FullName).Append(':').Append(text.Length).Append(':').Append(text).Append(';');
                return;
            }
            if (value is UnityEngine.Object || value is Delegate)
                throw new InvalidOperationException("Diagnostic data contains an unsupported reference: " + type.FullName);
            if (value is System.Collections.IEnumerable collection)
            {
                builder.Append(type.FullName).Append('[');
                foreach (var item in collection) Append(item, builder, depth + 1, ref count);
                builder.Append(']');
                return;
            }
            builder.Append(type.FullName).Append('{');
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         .OrderBy(field => field.Name, StringComparer.Ordinal))
            {
                builder.Append(field.Name).Append('=');
                Append(field.GetValue(value), builder, depth + 1, ref count);
            }
            builder.Append('}');
        }

        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            randomTrace?.Dispose();
            GameManager.SceneTransitionBegan -= TransitionBegan;
            USceneManager.sceneLoaded -= SceneLoaded;
            USceneManager.sceneUnloaded -= SceneUnloaded;
            USceneManager.activeSceneChanged -= ActiveSceneChanged;
            foreach (var remove in detach) remove();
            detach.Clear();
            if (!sealedTrace) Seal(false, "disposed-before-window-complete");
        }
    }
}
