using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using HutongGames.PlayMaker;
using Newtonsoft.Json;
using UnityEngine;
using USceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowKnightTAS.Runtime.FullRun
{
    // P0's independent observation path. Native schedulers/physics internals,
    // arbitrary Mod statics and asset aliasing remain explicit coverage gaps.
    internal sealed class LoadingGameplayOracle : IDisposable
    {
        private readonly string directory;
        private readonly long first, end;
        private readonly Func<long> movieFrame, nativeFrame;
        private readonly long controlledNativeOrigin;
        private readonly Func<string> gameplayRng;
        private readonly MemoryStream buffer = new MemoryStream();
        private readonly LoadingFieldDigest fields;
        private readonly LoadingObservationIdentity observationIdentity;
        private readonly Dictionary<Fsm, string> fsmIds = new Dictionary<Fsm, string>(ReferenceComparer<Fsm>.Instance);
        private readonly Dictionary<object, string> fsmMemberIds = new Dictionary<object, string>(ReferenceComparer<object>.Instance);
        private readonly Dictionary<object, string> unityIds = new Dictionary<object, string>(ReferenceComparer<object>.Instance);
        private readonly SortedSet<string> omissions = new SortedSet<string>(StringComparer.Ordinal);
        private int rows;
        private bool sealedTrace;
        private object[] lastInventory = Array.Empty<object>();
        private object[] rootSurvey = Array.Empty<object>();
        private object[] lastFieldDiagnostics = Array.Empty<object>();
        private readonly HashSet<string> detailedTypes = new HashSet<string>(new[] {
            "DesktopPlatform", "GlobalPrefabDefaults", "ObjectPool", "InputHandler", "InControl.HollowKnightInputModule"
        }, StringComparer.Ordinal);
        private readonly Type? payloadType;
        private readonly LoadingNativeSchedulerDigest? nativeScheduler;
        private readonly bool fieldDetails;
        private readonly List<Action> detach = new List<Action>();

        private LoadingGameplayOracle(string directory, long first, long end,
            Func<long> movieFrame, Func<long> nativeFrame, Func<string> gameplayRng)
        {
            this.directory = directory; this.first = first; this.end = end;
            this.movieFrame = movieFrame; this.nativeFrame = nativeFrame; this.gameplayRng = gameplayRng;
            controlledNativeOrigin = nativeFrame();
            fieldDetails = Environment.GetEnvironmentVariable("HKTAS_LOADING_ORACLE_FIELD_DETAILS") == "1";
            observationIdentity = new LoadingObservationIdentity(directory);
            fields = new LoadingFieldDigest(Reference, type => type == typeof(MonoBehaviour)
                || type == typeof(UnityEngine.Object), observationIdentity.Field, IsObservationCallback);
            if (Environment.GetEnvironmentVariable("HKTAS_LOADING_NATIVE_SCHEDULER") == "1")
                nativeScheduler = new LoadingNativeSchedulerDigest();
            payloadType = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => assembly.GetName().Name == "HollowKnightTAS.ClockPayload")
                ?.GetType("HollowKnightTAS.ClockPayload.ClockController", false);
            Write(new { kind = "header", schema = "hktas-loading-oracle-v1", first, end,
                nativeProfile = HollowKnightTAS.Core.Movie.MovieProtocolV2.SelectedNativeProfileId,
                coverage = "active-managed-fields-current-fsm-states-actions-and-all-roots-states-actions-at-window-endpoints-willactivate-loadfinish-v4",
                referencePolicy = "scene-hierarchy-sibling-component-index; assets-type-name; TAS-controller-references-by-type; readonly-probe-subscriptions-excluded-v1; protected-root-binding; registered-input-device-guid; input-tick-age; native-scheduler-timestamp-age-v1; dictionary-slots-v1; gradient-active-mode-v1; controlled-native-cursor-from-StartInputAt",
                nativeScheduler = nativeScheduler != null,
                fieldDiagnostics = fieldDetails,
                completeStateCoverage = false,
                knownMissing = new[] { "native-coroutine-running-callstacks-and-unregistered-schedulers", "native-physics-contact-caches",
                    "arbitrary-mod-statics", "ambiguous-asset-identities", "dormant-roots-between-window-endpoints-willactivate-loadfinish",
                    "TAS-controller-internals-require-separate-cursor-input-codecs" } });
            GameManager.SceneTransitionBegan += TransitionBegan;
            USceneManager.sceneLoaded += SceneLoaded;
            USceneManager.sceneUnloaded += SceneUnloaded;
            USceneManager.activeSceneChanged += ActiveSceneChanged;
        }

        internal static LoadingGameplayOracle? Create(string directory, Func<long> movieFrame,
            Func<long> nativeFrame, Func<string> gameplayRng)
        {
            if (Environment.GetEnvironmentVariable("HKTAS_LOADING_GAMEPLAY_ORACLE") != "1") return null;
            if (Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_CLOCK_ROUNDTRIP") == "1")
                throw new InvalidOperationException("The read-only gameplay oracle cannot run with clock-write diagnostics.");
            if (!long.TryParse(Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_FIRST"), out var first)
                || !long.TryParse(Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_END"), out var end)
                || first < 0 || end <= first || end - first > 10000)
                throw new InvalidOperationException("P0 gameplay oracle requires a bounded Movie window.");
            return new LoadingGameplayOracle(directory, first, end, movieFrame, nativeFrame, gameplayRng);
        }

        internal void Sample(string phase, string boundary)
        {
            if (sealedTrace || movieFrame() < first || movieFrame() >= end) return;
            try
            {
                var fullInventory = (phase == "event" && (boundary == "will-activate" || boundary == "load-finish"))
                    || (phase == "before" && movieFrame() == first)
                    || (phase == "after" && movieFrame() == end - 1);
                var state = CaptureState(fullInventory);
                Write(new { kind = "sample", sequence = rows++, phase, boundary,
                    movieFrame = movieFrame(), nativeFrame = nativeFrame(),
                    scene = USceneManager.GetActiveScene().name, state });
            }
            catch (Exception exception) { Seal(false, exception.GetType().Name + ": " + exception.Message); }
        }

        internal void Completed() { if (!sealedTrace && movieFrame() == end) Seal(true, "window-complete"); }

        private SortedDictionary<string, string> CaptureState(bool fullInventory)
        {
            var elapsed = Stopwatch.StartNew();
            observationIdentity.Refresh();
            unityIds.Clear(); fsmIds.Clear(); fsmMemberIds.Clear();
            var allBehaviours = Resources.FindObjectsOfTypeAll<MonoBehaviour>()
                .Where(component => component != null && Loaded(component.gameObject)
                    && component.GetType().Assembly != typeof(LoadingGameplayOracle).Assembly)
                .ToArray();
            if (allBehaviours.Length > 16000) throw new InvalidOperationException("P0 total inventory exceeds 16000 roots.");
            var behaviours = allBehaviours.Where(component => fullInventory || component.gameObject.activeInHierarchy)
                .Select(component => new { component, id = UnityId(component) })
                .OrderBy(item => item.id, StringComparer.Ordinal).ToArray();
            if (fullInventory) rootSurvey = behaviours.GroupBy(item => item.component.gameObject.scene.name + "|"
                + item.component.transform.root.name + "|" + item.component.GetType().FullName + "|"
                + item.component.gameObject.activeInHierarchy)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => (object)new { group = group.Key, count = group.Count(),
                    examples = group.Take(2).Select(item => item.id).ToArray() }).ToArray();
            if (!fullInventory && behaviours.Length > 6000) throw new InvalidOperationException("P0 active inventory exceeds 6000 roots: total="
                + behaviours.Length + ";active=" + behaviours.Count(item => item.component.gameObject.activeInHierarchy));
            foreach (var item in behaviours)
                if (item.component is PlayMakerFSM && fields.Read(item.component, "fsm") is Fsm fsm)
                {
                    fsmIds[fsm] = item.id;
                    var states = fields.Read(fsm, "states") as Array;
                    if (states == null) continue;
                    for (var stateIndex = 0; stateIndex < states.Length; stateIndex++)
                    {
                        var state = states.GetValue(stateIndex);
                        if (state == null) continue;
                        var stateId = item.id + "/state[" + stateIndex + "]:" + fields.Read(state, "name");
                        fsmMemberIds[state] = stateId;
                        var actions = fields.Read(state, "actions") as Array;
                        if (actions == null) continue;
                        for (var actionIndex = 0; actionIndex < actions.Length; actionIndex++)
                        {
                            var action = actions.GetValue(actionIndex);
                            if (action != null) fsmMemberIds[action] = stateId + "/action[" + actionIndex + "]";
                        }
                    }
                }
            string schedulerDigest = "disabled";
            if (nativeScheduler != null)
            {
                var objects = new Dictionary<int, string>();
                var nativeBehaviours = new Dictionary<IntPtr, string>();
                var nativePointer = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("P0 native behaviour pointer field is unavailable.");
                foreach (var component in allBehaviours)
                {
                    var id = UnityId(component);
                    objects[component.GetInstanceID()] = id;
                    objects[component.gameObject.GetInstanceID()] = UnityId(component.gameObject);
                    var pointer = (IntPtr)nativePointer.GetValue(component)!;
                    if (pointer != IntPtr.Zero) nativeBehaviours[pointer] = id;
                }
                schedulerDigest = nativeScheduler.Capture(fields,
                    id => objects.TryGetValue(id, out var value) ? value : null,
                    pointer => nativeBehaviours.TryGetValue(pointer, out var value) ? value : null,
                    includeFieldDiagnostics: fullInventory && fieldDetails);
            }
            var inventory = new List<object>();
            var fieldDiagnostics = new List<object>();
            var mono = new StringBuilder(); var fsms = new StringBuilder();
            foreach (var item in behaviours)
            {
                if (fullInventory && inventory.Count % 250 == 0)
                    File.WriteAllText(Path.Combine(directory, "loading-oracle-progress.json"),
                        JsonConvert.SerializeObject(new { diagnosticOnly = true, movieFrame = movieFrame(),
                            lastRoot = item.id, roots = inventory.Count, elapsedMs = elapsed.ElapsedMilliseconds,
                            completeStateCoverage = false }));
                var component = item.component;
                LoadingFieldDigestResult digest;
                try
                {
                if (component is PlayMakerFSM)
                {
                    var fsm = fields.Read(component, "fsm");
                    digest = fields.Capture(fsm, item.id, expandRoot: true);
                    fsms.Append(item.id).Append(':').Append(component.enabled).Append(':')
                        .Append(component.gameObject.activeSelf).Append(':').Append(digest.Sha256).Append('\n');
                    var states = fsm == null ? null : fullInventory ? fields.Read(fsm, "states") as Array
                        : new[] { fields.Read(fsm, "activeState") };
                    if (states != null)
                        for (var stateIndex = 0; stateIndex < states.Length; stateIndex++)
                        {
                            var state = states.GetValue(stateIndex);
                            if (state == null) continue;
                            AppendFsmMember(state, fsms, inventory);
                            if (fields.Read(state, "actions") is Array actions)
                                for (var actionIndex = 0; actionIndex < actions.Length; actionIndex++)
                                {
                                    var action = actions.GetValue(actionIndex);
                                    if (action != null) AppendFsmMember(action, fsms, inventory);
                                }
                        }
                }
                else
                {
                    digest = fields.Capture(component, item.id, expandRoot: true,
                        includeFieldDiagnostics: fullInventory && fieldDetails
                            && detailedTypes.Contains(component.GetType().FullName ?? ""));
                    mono.Append(item.id).Append(':').Append(component.enabled).Append(':')
                        .Append(component.gameObject.activeSelf).Append(':').Append(digest.Sha256).Append('\n');
                }
                }
                catch (Exception exception)
                {
                    lastInventory = inventory.ToArray();
                    throw new InvalidOperationException("P0 field root " + item.id + ": " + exception.Message, exception);
                }
                foreach (var omitted in digest.Unsupported) omissions.Add(omitted);
                if (digest.FieldDigests.Length != 0)
                    fieldDiagnostics.Add(new { root = item.id, fields = digest.FieldDigests });
                var fieldDigests = fullInventory && fieldDetails && detailedTypes.Contains(component.GetType().FullName ?? "")
                    ? fields.Fields(component.GetType()).Select(field => new
                        { field = field.Name, owner = field.DeclaringType!.FullName,
                            digest = fields.CaptureFields(component, item.id, field.Name).Sha256 }).ToArray() : null;
                inventory.Add(new { id = item.id, type = component.GetType().FullName,
                    digest = digest.Sha256, nodes = digest.Nodes, unsupported = digest.Unsupported, fieldDigests });
            }
            var bodies = new StringBuilder();
            foreach (var body in Resources.FindObjectsOfTypeAll<Rigidbody2D>().Where(body => body != null && Loaded(body.gameObject))
                         .Where(body => fullInventory || body.gameObject.activeInHierarchy)
                         .OrderBy(body => UnityId(body), StringComparer.Ordinal))
            {
                var id = UnityId(body);
                var digest = fields.Capture(new { body.position, body.velocity, body.rotation, body.angularVelocity,
                    body.mass, body.inertia, body.centerOfMass, body.worldCenterOfMass, body.gravityScale,
                    body.drag, body.angularDrag, body.constraints, body.bodyType, body.simulated, body.useAutoMass,
                    body.sleepMode, body.interpolation, body.collisionDetectionMode,
                    sleeping = body.IsSleeping(), transformPosition = body.transform.position,
                    transformRotation = body.transform.rotation, transformScale = body.transform.localScale }, id, expandRoot: true);
                bodies.Append(id).Append(':').Append(digest.Sha256).Append('\n');
                inventory.Add(new { id, type = typeof(Rigidbody2D).FullName, digest = digest.Sha256,
                    nodes = digest.Nodes, unsupported = digest.Unsupported });
            }
            lastInventory = inventory.ToArray();
            if (fullInventory) lastFieldDiagnostics = fieldDiagnostics.ToArray();
            if (nativeScheduler != null)
                foreach (var omitted in nativeScheduler.Unsupported) omissions.Add("native-scheduler:" + omitted);
            return new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["time"] = Bits(Time.time), ["timeDouble"] = Bits(Time.timeAsDouble),
                ["fixedTime"] = Bits(Time.fixedTime), ["fixedTimeDouble"] = Bits(Time.fixedTimeAsDouble),
                ["unscaledTime"] = Bits(Time.unscaledTime), ["realtime"] = Bits(Time.realtimeSinceStartup),
                ["frameCount"] = Time.frameCount.ToString(CultureInfo.InvariantCulture),
                ["controlledNativeFrame"] = (nativeFrame() - controlledNativeOrigin).ToString(CultureInfo.InvariantCulture),
                ["gameplayRng"] = gameplayRng(), ["monoFields"] = Hash(mono.ToString()),
                ["fsmFields"] = Hash(fsms.ToString()), ["physicsBodies"] = Hash(bodies.ToString()),
                ["rootCount"] = inventory.Count.ToString(CultureInfo.InvariantCulture),
                ["totalBehaviours"] = allBehaviours.Length.ToString(CultureInfo.InvariantCulture),
                ["inventoryScope"] = fullInventory ? "all-loaded" : "active-only",
                ["nativeScheduler"] = schedulerDigest,
                ["unsupported"] = Hash(string.Join("\n", omissions))
            };
        }

        private static bool Loaded(GameObject value) => value.scene.IsValid() && value.scene.isLoaded;

        private void AppendFsmMember(object member, StringBuilder digestText, List<object> inventory)
        {
            if (!fsmMemberIds.TryGetValue(member, out var id))
                throw new InvalidOperationException("P0 FSM member identity unavailable.");
            var digest = fields.Capture(member, id, expandRoot: true);
            digestText.Append(id).Append(':').Append(digest.Sha256).Append('\n');
            foreach (var omitted in digest.Unsupported) omissions.Add(omitted);
            inventory.Add(new { id, type = member.GetType().FullName, digest = digest.Sha256,
                nodes = digest.Nodes, unsupported = digest.Unsupported });
        }

        private string? Reference(object value)
        {
            if (value is ParticleSystem.MinMaxGradient gradient) return GradientIdentity(gradient);
            if (value is Coroutine coroutine && nativeScheduler != null) return nativeScheduler.Reference(coroutine, fields);
            if (value is UnityEngine.Object unity) return unity == null ? "destroyed:" + value.GetType().FullName : UnityId(unity);
            if (fsmMemberIds.TryGetValue(value, out var memberId)) return "fsm-member:" + memberId;
            if (value is FsmState || value is FsmStateAction)
            {
                omissions.Add("unbound-fsm-member:" + value.GetType().FullName);
                return "fsm-member-unbound:" + value.GetType().FullName;
            }
            if (value.GetType().Assembly == typeof(LoadingGameplayOracle).Assembly)
                return "TAS-controller:" + value.GetType().FullName;
            if (value is Fsm fsm)
            {
                if (fsmIds.TryGetValue(fsm, out var id)) return "fsm:" + id;
                var name = fields.Read(fsm, "name") as string ?? "unnamed";
                omissions.Add("unbound-fsm:" + name);
                return "fsm-unbound:" + name;
            }
            return null;
        }

        private static bool IsObservationCallback(Delegate callback)
        {
            // Only these two read-only recorders (including their compiler
            // closures) are instrumentation. Retain every game/Mod/controller
            // callback and its order. Clock-write diagnostics are rejected.
            for (var type = callback.Method.DeclaringType; type != null; type = type.DeclaringType)
                if (type == typeof(LoadingBoundaryProbe) || type == typeof(LoadingGameplayOracle)) return true;
            return false;
        }

        private static string GradientIdentity(ParticleSystem.MinMaxGradient gradient)
        {
            string ColorIdentity(Color color) => Bits(color.r) + ":" + Bits(color.g) + ":" + Bits(color.b) + ":" + Bits(color.a);
            string Keys(Gradient? value) => value == null ? "null" : value.mode + ":"
                + string.Join(";", value.colorKeys.Select(key => Bits(key.time) + ":" + ColorIdentity(key.color))) + ":"
                + string.Join(";", value.alphaKeys.Select(key => Bits(key.time) + ":" + Bits(key.alpha)));
            switch (gradient.mode)
            {
                case ParticleSystemGradientMode.Color: return "gradient:Color:" + ColorIdentity(gradient.color);
                case ParticleSystemGradientMode.TwoColors: return "gradient:TwoColors:" + ColorIdentity(gradient.colorMin) + ":" + ColorIdentity(gradient.colorMax);
                case ParticleSystemGradientMode.Gradient:
                case ParticleSystemGradientMode.RandomColor: return "gradient:" + gradient.mode + ":" + Keys(gradient.gradient);
                case ParticleSystemGradientMode.TwoGradients: return "gradient:TwoGradients:" + Keys(gradient.gradientMin) + ":" + Keys(gradient.gradientMax);
                default: throw new InvalidOperationException("P0 unsupported particle gradient mode.");
            }
        }

        private string UnityId(UnityEngine.Object value)
        {
            if (unityIds.TryGetValue(value, out var id)) return id;
            GameObject? go = value as GameObject;
            if (value is Component component) go = component.gameObject;
            if (go != null && Loaded(go))
            {
                var path = new Stack<string>();
                for (var current = go.transform; current != null; current = current.parent)
                    path.Push(current.name + "[" + current.GetSiblingIndex() + "]");
                id = go.scene.name + ":" + string.Join("/", path);
                if (value is Component part)
                {
                    var sameType = go.GetComponents(part.GetType());
                    var index = 0;
                    while (index < sameType.Length && !ReferenceEquals(sameType[index], part)) index++;
                    if (index == sameType.Length) throw new InvalidOperationException("P0 component identity unavailable.");
                    id += "|" + part.GetType().FullName + "[" + index + "]";
                }
            }
            else id = "asset:" + value.GetType().FullName + ":" + value.name;
            unityIds.Add(value, id);
            return id;
        }

        private object? RenderRngDiagnostic()
        {
            if (payloadType == null) return "payload-unavailable";
            var result = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var name in new[] { "fullRunRenderInitialized", "fullRunRenderEntered", "fullRunRenderState" })
            {
                // ClockController has already executed Bootstrap before Runtime
                // starts. Reading these existing static fields cannot initialize it.
                var field = payloadType.GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
                result[name] = field == null ? "field-unavailable" : fields.Capture(field.GetValue(null), name).Sha256;
            }
            return result;
        }

        private void Write(object value)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value) + "\n");
            if (rows > 50000 || buffer.Length + bytes.Length > 32L * 1024 * 1024)
                throw new InvalidOperationException("P0 gameplay oracle trace budget exceeded.");
            buffer.Write(bytes, 0, bytes.Length);
        }

        private void Seal(bool success, string detail)
        {
            if (sealedTrace) return;
            sealedTrace = true;
            try
            {
                try
                {
                    var inventory = JsonConvert.SerializeObject(new { schema = "hktas-p0-state-inventory-v1",
                        movieFrame = movieFrame(), completeStateCoverage = false,
                        roots = lastInventory, rootSurvey, fieldSchema = fields.Schema(), unsupported = omissions,
                        nativeScheduler = nativeScheduler?.Inventory,
                        renderRng = RenderRngDiagnostic() }, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                    if (Encoding.UTF8.GetByteCount(inventory) > 16 * 1024 * 1024)
                        throw new InvalidOperationException("P0 state inventory exceeds its explicit 16 MiB budget.");
                    File.WriteAllText(Path.Combine(directory, "loading-state-inventory.json"), inventory, new UTF8Encoding(false));
                    var diagnostics = JsonConvert.SerializeObject(new { diagnosticOnly = true,
                        movieFrame = movieFrame(), roots = lastFieldDiagnostics,
                        nativeCoroutines = nativeScheduler?.FieldDiagnostics });
                    if (Encoding.UTF8.GetByteCount(diagnostics) > 8 * 1024 * 1024)
                        throw new InvalidOperationException("P0 nested field diagnostics exceed the explicit 8 MiB budget.");
                    File.WriteAllText(Path.Combine(directory, "loading-field-digests.json"), diagnostics, new UTF8Encoding(false));
                }
                catch (Exception exception)
                {
                    success = false;
                    detail += "; inventory export failed: " + exception.GetType().Name + ": " + exception.Message;
                }
                var content = buffer.ToArray();
                using (var digest = SHA256.Create())
                {
                    var footer = new { kind = "footer", success, detail, rows, first, end,
                        sha256 = BitConverter.ToString(digest.ComputeHash(content)).Replace("-", "").ToLowerInvariant() };
                    using (var output = new FileStream(Path.Combine(directory, "loading-gameplay-oracle.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    {
                        output.Write(content, 0, content.Length);
                        var tail = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(footer) + "\n");
                        output.Write(tail, 0, tail.Length);
                    }
                }
            }
            catch (Exception exception) { Modding.Logger.LogError("P0 gameplay oracle export failed: " + exception.Message); }
            finally { nativeScheduler?.Release(); buffer.Dispose(); }
        }

        private static string Bits(float value) => "f32:" + BitConverter.ToInt32(BitConverter.GetBytes(value), 0).ToString("x8");
        private static string Bits(double value) => "f64:" + BitConverter.DoubleToInt64Bits(value).ToString("x16");
        private static string Hash(string value)
        { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant(); }

        private void TransitionBegan(SceneLoad load)
        {
            Sample("event", "transition-began:" + load.TargetSceneName);
            SceneLoad.FetchCompleteDelegate fetch = () => Sample("event", "fetch-complete");
            SceneLoad.WillActivateDelegate activate = () => Sample("event", "will-activate");
            SceneLoad.ActivationCompleteDelegate activated = () => Sample("event", "activation-complete");
            SceneLoad.CompleteDelegate complete = () => Sample("event", "load-complete");
            SceneLoad.StartCalledDelegate started = () => Sample("event", "start-called");
            SceneLoad.FinishDelegate finish = null!;
            Action remove = () => { load.FetchComplete -= fetch; load.WillActivate -= activate;
                load.ActivationComplete -= activated; load.Complete -= complete;
                load.StartCalled -= started; load.Finish -= finish; };
            finish = () => { Sample("event", "load-finish"); remove(); detach.Remove(remove); };
            load.FetchComplete += fetch; load.WillActivate += activate;
            load.ActivationComplete += activated; load.Complete += complete;
            load.StartCalled += started; load.Finish += finish; detach.Add(remove);
        }
        private void SceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
            => Sample("event", "scene-loaded:" + scene.name + ":" + mode);
        private void SceneUnloaded(UnityEngine.SceneManagement.Scene scene) => Sample("event", "scene-unloaded:" + scene.name);
        private void ActiveSceneChanged(UnityEngine.SceneManagement.Scene previous, UnityEngine.SceneManagement.Scene current)
            => Sample("event", "active-scene:" + previous.name + "->" + current.name);
        public void Dispose()
        {
            GameManager.SceneTransitionBegan -= TransitionBegan;
            USceneManager.sceneLoaded -= SceneLoaded;
            USceneManager.sceneUnloaded -= SceneUnloaded;
            USceneManager.activeSceneChanged -= ActiveSceneChanged;
            foreach (var remove in detach) remove(); detach.Clear();
            if (!sealedTrace) Seal(false, "disposed-before-window-complete");
        }

        private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
        {
            internal static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();
            public bool Equals(T? left, T? right) => ReferenceEquals(left, right);
            public int GetHashCode(T value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
