using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using MonoMod.RuntimeDetour;
using Newtonsoft.Json;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Installed hooks keep their actual target identity. Only explicitly owned
    // semantic fields enter the shared world graph through typed D slots.
    internal static partial class ResidentControlPlaneState
    {
        private const BindingFlags Fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        internal sealed class Data
        {
            public int Version = 1;
            public FrameStatus? ObservedFrame;
            public ResidentProcessSlots.Data[] Owners = Array.Empty<ResidentProcessSlots.Data>();
            public Dependency[] Dependencies = Array.Empty<Dependency>();
            public PayloadData? Payload;
            public string[] CoverageGaps = Array.Empty<string>();
        }
        internal sealed class FrameStatus
        {
            public bool InputReady, FrameInputEnabled, RandomSeedHandshakePending, SuppressInput;
            public long SuppressedUpdates;
        }
        internal sealed class Dependency
        {
            public string Key = "", Value = "";
        }
        internal static readonly IReadOnlyDictionary<string, string> ModFields = ResidentProcessSlots.Ownership(
            "Execution:settings,runtimeHost,protectedSaves,fullRunSession,hooksRegistered,manualStartup,modStartup,startupMessage,startupLabel,_globalSettingsPath,onLoadGlobalSettings,onSaveGlobalSettings,onLoadSaveSettings,onSaveSaveSettings,ClassName",
            "Dependency:globalSettingsType,saveSettingsType,Name");
        internal static readonly IReadOnlyDictionary<string, string> StartupFields = ResidentProcessSlots.Ownership(
            "D:ordinals", "Execution:gameThread,randomConstructor,synchronousFetch,cinematics,experimentalFetch,serialScenes,audioLifetimes,suppressInput,suppressedUpdates");
        internal static readonly IReadOnlyDictionary<string, string> CinematicFields = ResidentProcessSlots.Ownership("Execution:factory,polling");
        internal static readonly IReadOnlyDictionary<string, string> CinematicStatics = ResidentProcessSlots.Ownership(
            "D:players,<FrameDuration>k__BackingField", "Dependency:PlayerVideo", "Execution:<Failure>k__BackingField");
        internal static readonly IReadOnlyDictionary<string, string> AudioFields = ResidentProcessSlots.Ownership(
            "D:states", "Execution:hooks,failure", "Dependency:inspectedAssemblies");
        internal static readonly IReadOnlyDictionary<string, string> AudioStatics = ResidentProcessSlots.Ownership("Execution:instance");
        internal static readonly IReadOnlyDictionary<string, string> FetchFields = ResidentProcessSlots.Ownership(
            "D:diagnosticRows", "Execution:hook,thread,update,pumping");
        internal static readonly IReadOnlyDictionary<string, string> ExperimentalFetchStatics = ResidentProcessSlots.Ownership("Execution:instance");
        internal static readonly IReadOnlyDictionary<string, string> CutsceneFields = ResidentProcessSlots.Ownership("Execution:hooks");

        // This phase deliberately remains unqualified until the payload's
        // complete semantic/static contract is composed by Capture below.
        internal static CaptureResult CaptureRuntime(HollowKnightTASMod mod, ModStartupCompatibility startup, Action admitted)
        {
            var builder = CaptureRuntimeOwners(mod, startup, admitted);
            builder.Gaps.Add("control-plane-clock-payload-contract-missing");
            return builder.Finish();
        }
        private static Builder CaptureRuntimeOwners(HollowKnightTASMod mod, ModStartupCompatibility startup, Action admitted, bool frameOnly = false)
        {
            if (mod == null || startup == null || admitted == null) throw new ArgumentNullException();
            var builder = new Builder(admitted);
            BindMasterGuiEvent(builder);
            if (ResidentStaticRoots.IsInitialized(typeof(UnityEngine.Display))) {
                var displays = UnityEngine.Display.displays.ToArray();
                builder.Dependency("unity/display-count", displays.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                for (var index = 0; index < displays.Length; index++) {
                    if (displays[index] == null) throw Error("Unity display slot has no device");
                    builder.Bind(displays[index], "resident-control/display/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                builder.Require(() => {
                    var current = UnityEngine.Display.displays;
                    if (current.Length != displays.Length || displays.Where((display, i) => !ReferenceEquals(display, current[i])).Any())
                        throw Error("Unity display device membership changed");
                });
            }
            builder.Require(() => {
                if (!ReferenceEquals(Read(mod, "modStartup"), startup) || Read(mod, "fullRunSession") == null
                    || !(bool)Read(mod, "hooksRegistered")! || !frameOnly && (bool)Read(startup, "suppressInput")!
                    || (int)Read(startup, "gameThread")! != Thread.CurrentThread.ManagedThreadId)
                    throw Error("actual Mod/startup hook ownership or released input boundary differs");
            });
            // The Modding API's file sink and generated reflection accessors are
            // process services. Keep graph aliases bound to the installed service
            // while the ordinary static graph still carries its configuration.
            void BindApiStatics(Type type, string role, IReadOnlyDictionary<string, string> ownership) {
                if (ResidentStaticRoots.IsInitialized(type))
                    builder.AddStatic(role, type, ownership, ResidentStaticRoots.CreateInitializationGuard(type));
            }
            BindApiStatics(typeof(Modding.Logger), "api-log", ResidentProcessSlots.Ownership(
                "Execution:Locker,Writer,APILogger", "Dependency:_logLevel,_shortLoggingLevel,_includeTimestamps,OldLogDir"));
            BindApiStatics(typeof(Modding.ReflectionHelper), "api-reflection", ResidentProcessSlots.Ownership(
                "Execution:Fields,FieldGetters,FieldSetters,Properties,PropertyGetters,PropertySetters,Methods,MethodsDelegates", "Dependency:_preloaded"));
            BindApiStatics(typeof(HutongGames.PlayMaker.FsmLog), "playmaker-log", ResidentProcessSlots.Ownership(
                "Execution:Logs,logEntryPool", "Dependency:MaxSize,nextLogEntryPoolIndex,<LoggingEnabled>k__BackingField,<MirrorDebugLog>k__BackingField,<EnableDebugFlow>k__BackingField"));
            builder.Add("mod", mod, ModFields);
            builder.Add("startup", startup, StartupFields);
            var session = Read(mod, "fullRunSession") as RuntimeFullRunSession ?? throw Error("actual full-run session is absent");
            if (frameOnly) {
                FrameStatus Observe() => new FrameStatus {
                    InputReady = (bool)Read(session, "inputReady")!, FrameInputEnabled = (bool)Read(session, "frameInputEnabled")!,
                    RandomSeedHandshakePending = (bool)Read(session, "randomSeedHandshakePending")!,
                    SuppressInput = (bool)Read(startup, "suppressInput")!, SuppressedUpdates = (long)Read(startup, "suppressedUpdates")!
                };
                var observed = Observe(); builder.ObservedFrame = observed;
                builder.Require(() => {
                    var now = Observe();
                    if (!ReferenceEquals(Read(mod, "fullRunSession"), session) || now.InputReady != observed.InputReady || now.FrameInputEnabled != observed.FrameInputEnabled
                        || now.RandomSeedHandshakePending != observed.RandomSeedHandshakePending || now.SuppressInput != observed.SuppressInput
                        || now.SuppressedUpdates != observed.SuppressedUpdates) throw Error("frame input readiness or RNG handshake changed during observation");
                });
            } else builder.Require(() => {
                if ((bool)Read(session, "randomSeedHandshakePending")! || !(bool)Read(session, "inputReady")!)
                    throw Error("restore control boundary requires ready input and an idle RNG handshake");
            });
            var cutscene = Read(session.ResidentInput, "cutsceneInput") as Input.FullRunCutsceneInput ?? throw Error("actual cutscene input hook owner is absent");
            builder.Add("cutscene-input", cutscene, CutsceneFields);
            builder.ObserveList("cutscene-input/hooks", (IList)Read(cutscene, "hooks")!);
            builder.Dependency("mod/name", (string)Read(mod, "Name")!);
            var cinematics = Read(startup, "cinematics") as DeterministicCinematics ?? throw Error("actual cinematic hook owner is absent");
            builder.Add("cinematics", cinematics, CinematicFields);
            // The actual constructor has assigned Failure and installed its
            // hooks; this is not a request to initialize a dormant Type.
            builder.AddStatic("cinematics/static", cinematics, CinematicStatics);
            builder.Require(() => {
                if (ReadStatic(typeof(DeterministicCinematics), "<Failure>k__BackingField") != null)
                    throw Error("cinematic hook owner is faulted");
            });
            var players = (IList)ReadStatic(typeof(DeterministicCinematics), "players")!;
            if (players.Count != 0) builder.Gaps.Add("control-plane-cinematic-native-video-continuation");
            builder.Dependency("cinematics/player-video-field", DescribeMetadata(ReadStatic(typeof(DeterministicCinematics), "PlayerVideo")!));
            AddFetch(builder, "fetch", Read(startup, "synchronousFetch"), false);
            AddFetch(builder, "experimental-fetch", Read(startup, "experimentalFetch"), true);
            if (Read(startup, "synchronousFetch") == null && Read(startup, "experimentalFetch") == null)
                builder.Gaps.Add("control-plane-scene-fetch-owner-absent");
            var audio = Read(startup, "audioLifetimes");
            if (audio != null) {
                if (audio.GetType() != typeof(LoadingAudioLifetimePolicy)) throw Error("actual audio hook owner type differs");
                builder.Add("audio", audio, AudioFields); builder.AddStatic("audio/static", audio, AudioStatics);
                builder.Require(() => {
                    if (!ReferenceEquals(ReadStatic(typeof(LoadingAudioLifetimePolicy), "instance"), audio) || Read(audio, "failure") != null)
                        throw Error("actual audio hook owner was replaced or faulted");
                });
                builder.ObserveList("audio/hooks", Read(audio, "hooks") as IList ?? throw Error("audio hook collection differs"));
                builder.ObserveAssemblies("audio/installed-assemblies", (IEnumerable)Read(audio, "inspectedAssemblies")!);
            }
            return builder;
        }
        // Unity keeps its master Event installed on both sides of the managed/native
        // boundary. Pair the engine-owned instance only when parked outside OnGUI.
        // A distinct gameplay-created Event still goes through ordinary discovery.
        private static void BindMasterGuiEvent(Builder builder)
        {
            var type = typeof(UnityEngine.Event);
            if (!ResidentStaticRoots.IsInitialized(type)) return;
            var master = (UnityEngine.Event?)ReadStatic(type, "s_MasterEvent");
            if (ReferenceEquals(master, null)) return;
            var current = (UnityEngine.Event?)ReadStatic(type, "s_Current");
            var depth = typeof(UnityEngine.GUIUtility).GetProperty("guiDepth", Fields) ?? throw Error("Unity GUI depth ABI differs");
            if ((int)depth.GetValue(null, null)! != 0 || !ReferenceEquals(current, master) && !ReferenceEquals(current, null))
                throw Error("Unity GUI dispatch has an active non-master event");
            var pointer = Read(master!, "m_Ptr");
            string Witness() {
                var position = master!.mousePosition; var delta = master.delta;
                return JsonConvert.SerializeObject(new object?[] { ReferenceEquals(master, current), master.type, master.rawType,
                    master.pointerType, master.button, master.modifiers, BitConverter.GetBytes(master.pressure), master.clickCount,
                    (int)master.character, master.keyCode, master.displayIndex, master.commandName, BitConverter.GetBytes(position.x),
                    BitConverter.GetBytes(position.y), BitConverter.GetBytes(delta.x), BitConverter.GetBytes(delta.y) });
            }
            var witness = Witness();
            builder.Dependency("unity/gui-master-event", witness);
            builder.Bind(master!, "resident-control/gui-master-event");
            builder.Require(() => {
                if ((int)depth.GetValue(null, null)! != 0 || !ReferenceEquals(ReadStatic(type, "s_MasterEvent"), master)
                    || !ReferenceEquals(ReadStatic(type, "s_Current"), current) || !Equals(Read(master!, "m_Ptr"), pointer)
                    || Witness() != witness) throw Error("Unity GUI master event changed during observation");
            });
        }
        private static void AddFetch(Builder builder, string key, object? fetch, bool experimental)
        {
            if (fetch == null) return;
            if (fetch.GetType() != (experimental ? typeof(LoadingSynchronousFetchProbe) : typeof(DeterministicSceneFetch)))
                throw Error("actual scene fetch owner type differs");
            builder.Add(key, fetch, FetchFields);
            builder.Require(() => {
                if ((bool)Read(fetch, "pumping")! || (int)Read(fetch, "thread")! != Thread.CurrentThread.ManagedThreadId)
                    throw Error("scene fetch is pumping or belongs to another thread");
            });
            if (experimental) {
                builder.AddStatic(key + "/static", fetch, ExperimentalFetchStatics);
                builder.Require(() => {
                    if (!ReferenceEquals(ReadStatic(typeof(LoadingSynchronousFetchProbe), "instance"), fetch))
                        throw Error("actual experimental fetch singleton changed");
                });
            }
        }

        internal sealed class CaptureResult
        {
            internal readonly Data Snapshot;
            internal IReadOnlyDictionary<string, object> ManagedRoots { get; }
            private readonly ResidentProcessSlots[] owners;
            private readonly Dictionary<object, string> external;
            private readonly Dictionary<string, object> resolved;
            private readonly Action admitted;
            private readonly Action[] checks;
            private readonly PayloadCapture? payload;
            private readonly int thread;
            private string frozen;
            internal CaptureResult(Data snapshot, ResidentProcessSlots[] owners, Dictionary<object, string> external,
                Dictionary<string, object> resolved, Action admitted, Action[] checks, PayloadCapture? payload)
            {
                Snapshot = snapshot; this.owners = owners; this.external = external; this.resolved = resolved;
                this.admitted = admitted; this.checks = checks; this.payload = payload; thread = Thread.CurrentThread.ManagedThreadId;
                ManagedRoots = owners.SelectMany(owner => owner.ManagedRoots).Concat(payload == null
                    ? Enumerable.Empty<KeyValuePair<string, object>>() : payload.ManagedRoots).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                // Graph traversal probes every reachable reference. Index installed owners
                // once, preserving the original priority of control, payload and owner slots.
                var installed = owners.SelectMany(owner => owner.ExternalBindings);
                if (payload != null) installed = payload.ExternalBindings.Concat(installed);
                foreach (var binding in installed) {
                    if (!external.ContainsKey(binding.Key)) external.Add(binding.Key, binding.Value);
                    if (!resolved.ContainsKey(binding.Value)) resolved.Add(binding.Value, binding.Key);
                }
                frozen = JsonConvert.SerializeObject(snapshot);
                if (snapshot.ObservedFrame == null) RequireUnchanged(); else RequireFrameUnchanged();
            }
            internal IReadOnlyDictionary<string, object> CurrentManagedRoots() => owners.SelectMany(owner => owner.ManagedRoots).Concat(payload == null
                ? Enumerable.Empty<KeyValuePair<string, object>>() : payload.ManagedRoots).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            internal string? BindExternal(object value) => external.TryGetValue(value, out var key) ? key : null;
            internal object? ResolveExternal(string key) => resolved.TryGetValue(key, out var value) ? value : null;
            internal void RequireUnchanged()
            { if (Snapshot.ObservedFrame != null) throw Error("frame control observation is read-only"); RequireFields(); }
            internal void RequireFrameUnchanged()
            { if (Snapshot.ObservedFrame == null) throw Error("control witness requires a read-only frame"); RequireFields(); }
            private void RequireFields()
            { RequireControlUnchanged(); foreach (var owner in owners) owner.RequireUnchanged(); payload?.RequireUnchanged(); RequireControlUnchanged(); }
            internal void RequireControlUnchanged()
            {
                if (thread != Thread.CurrentThread.ManagedThreadId) throw Error("control observation changed thread");
                admitted(); if (frozen != JsonConvert.SerializeObject(Snapshot)) throw Error("control-plane manifest was mutated");
                foreach (var check in checks) check(); foreach (var owner in owners) owner.RequireControlUnchanged(); payload?.RequireControlUnchanged(); admitted();
            }
            internal void RequireComplete()
            {
                if (Snapshot.ObservedFrame != null) throw Error("frame control observation cannot admit a restore");
                RequireControlUnchanged(); foreach (var owner in owners) owner.RequireComplete();
                payload?.RequireComplete();
                if (Snapshot.CoverageGaps.Length != 0) throw Error(string.Join(";", Snapshot.CoverageGaps));
            }
            internal void RequireLayout(Data source)
            {
                RequireComplete(); Validate(source);
                var srcId = LayoutIdentity(source);
                var tgtId = LayoutIdentity(Snapshot);
                if (source.CoverageGaps.Length != 0 || srcId != tgtId)
                {
                    var diff = "";
                    for (int i = 0; i < Math.Min(srcId.Length, tgtId.Length); i++) {
                        if (srcId[i] != tgtId[i]) {
                            var sSub = srcId.Substring(Math.Max(0, i - 20), Math.Min(40, srcId.Length - Math.Max(0, i - 20)));
                            var tSub = tgtId.Substring(Math.Max(0, i - 20), Math.Min(40, tgtId.Length - Math.Max(0, i - 20)));
                            diff += $" at {i}: src=[{sSub}] target=[{tSub}]";
                            break;
                        }
                    }
                    throw Error("source control-plane owners, dependencies or coverage differ: " + diff);
                }

                for (var index = 0; index < owners.Length; index++) owners[index].RequireLayout(source.Owners[index]);
                frozen = JsonConvert.SerializeObject(Snapshot);
            }
            internal void Apply(ResidentMutationScope scope)
            { RequireUnchanged(); RequireComplete(); foreach (var owner in owners) owner.Apply(scope); payload?.Apply(scope); Verify(); }
            internal void Verify()
            { RequireControlUnchanged(); foreach (var owner in owners) owner.Verify(); payload?.VerifySlots(); RequireControlUnchanged(); }
            internal void VerifyImported(PayloadData? source)
            { if (payload != null) payload.VerifyImported(source ?? throw Error("source payload transport is absent")); }
        }

        internal static Plan Prepare(Data source, CaptureResult target, ResidentMutationScope scope, Action admitted)
            => new Plan(source, target, scope, admitted);
        internal sealed class Plan
        {
            private readonly CaptureResult target;
            private readonly ResidentMutationScope scope;
            private readonly Action admitted;
            private readonly PayloadData? sourcePayload;
            private readonly Dictionary<string, object> managedRoots;
            private int state;
            internal IReadOnlyDictionary<string, object> ManagedRoots => managedRoots;
            internal string? BindExternal(object value) => target.BindExternal(value);
            internal object? ResolveExternal(string key) => target.ResolveExternal(key);
            internal Plan(Data source, CaptureResult target, ResidentMutationScope scope, Action admitted)
            {
                this.target = target ?? throw new ArgumentNullException(nameof(target)); this.scope = scope ?? throw new ArgumentNullException(nameof(scope));
                this.admitted = admitted ?? throw new ArgumentNullException(nameof(admitted)); admitted(); target.RequireUnchanged(); target.RequireLayout(source);
                sourcePayload = source.Payload == null ? null : JsonConvert.DeserializeObject<PayloadData>(JsonConvert.SerializeObject(source.Payload));
                managedRoots = target.CurrentManagedRoots().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            }
            internal void Apply()
            {
                if (state != 0) { state = -1; throw Error("control-plane plan was reused or faulted"); }
                try { admitted(); scope.Require(); state = 1; target.Apply(scope); state = 2; }
                catch { state = -1; throw; }
            }
            internal void Verify()
            {
                if (state != 2) throw Error("control-plane restore did not complete");
                try { admitted(); scope.Require(); target.Verify(); target.VerifyImported(sourcePayload); admitted(); }
                catch { state = -1; throw; }
            }
        }
        internal sealed class Builder
        {
            internal PayloadCapture? Payload;
            internal FrameStatus? ObservedFrame;
            internal readonly SortedSet<string> Gaps = new SortedSet<string>(StringComparer.Ordinal);
            private readonly List<ResidentProcessSlots> owners = new List<ResidentProcessSlots>();
            private readonly Dictionary<string, string> dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Dictionary<object, string> external = new Dictionary<object, string>(References.Instance);
            private readonly Dictionary<string, object> resolved = new Dictionary<string, object>(StringComparer.Ordinal);
            private readonly List<Action> checks = new List<Action>();
            private readonly Action admitted;
            internal Builder(Action admitted) { this.admitted = admitted; admitted(); }
            internal void Require(Action check) { check(); checks.Add(check); }
            internal void Add(string key, object value, IReadOnlyDictionary<string, string> ownership)
            {
                owners.Add(new ResidentProcessSlots("control/" + key, value, ownership, admitted));
                foreach (var field in CanonicalCarryGraphCodec.Fields(value.GetType()))
                    if (ownership.TryGetValue(field.Name, out var storage) && storage == "Execution") {
                        var member = field.GetValue(value);
                        if (member is Hook || member is ILHook) ObserveHook(key + "/" + field.Name, member);
                    }
            }
            internal void AddStatic(string key, object instance, IReadOnlyDictionary<string, string> ownership)
                => owners.Add(new ResidentProcessSlots("control/" + key, instance, ownership, admitted, true));
            internal void AddStatic(string key, Type type, IReadOnlyDictionary<string, string> ownership, Action initialized)
                => owners.Add(ResidentProcessSlots.CaptureInitializedStatic("control/" + key, type, ownership, admitted, initialized));
            internal void Dependency(string key, string value) => dependencies.Add(key, value);
            internal void Bind(object value, string key)
            {
                if (value == null || value.GetType().IsValueType || value is string || ResidentGraphPolicy.IsMetadata(value)) return;
                if (external.TryGetValue(value, out var old)) { if (old != key) throw Error("control identity has two external roles"); return; }
                external.Add(value, key); resolved.Add(key, value);
            }
            internal void ObserveList(string key, IList list)
            {
                if (list.Count > 65536) throw Error("hook collection exceeds its bound");
                var expected = list.Cast<object>().ToArray();
                for (var index = 0; index < expected.Length; index++) {
                    var role = key + "/" + index; Bind(expected[index], "resident-control/" + role);
                    if (expected[index] is Hook || expected[index] is ILHook) ObserveHook(role, expected[index]);
                    else Gaps.Add("control-plane-unclassified-hook:" + role);
                }
                Require(() => {
                    if (list.Count != expected.Length || expected.Where((value, index) => !ReferenceEquals(value, list[index])).Any())
                        throw Error("installed hook membership changed: " + key);
                });
            }
            private void ObserveHook(string key, object hook)
            {
                if (hook is Hook direct) {
                    var method = direct.Method; var replacement = direct.Target; var owner = direct.DelegateTarget;
                    Dependency("hook/" + key, DescribeMetadata(method) + "/" + DescribeMetadata(replacement));
                    Require(() => {
                        if (!direct.IsApplied || !direct.IsValid || !ReferenceEquals(method, direct.Method)
                            || !ReferenceEquals(replacement, direct.Target) || !ReferenceEquals(owner, direct.DelegateTarget))
                            throw Error("actual native hook target or installation changed: " + key);
                    });
                } else if (hook is ILHook il) {
                    var method = il.Method; var manipulator = il.Manipulator;
                    Dependency("hook/" + key, DescribeMetadata(method) + "/" + ResidentGraphPolicy.DescribeDelegate(manipulator));
                    Require(() => {
                        if (!il.IsApplied || !il.IsValid || !ReferenceEquals(method, il.Method) || !ReferenceEquals(manipulator, il.Manipulator))
                            throw Error("actual IL hook target or installation changed: " + key);
                    });
                } else throw Error("hook type is unclassified");
            }
            internal void ObserveAssemblies(string key, IEnumerable assemblies)
            {
                string Observe() => string.Join(";", assemblies.Cast<Assembly>().Select(assembly => assembly.GetName().FullName + "/"
                    + assembly.ManifestModule.ModuleVersionId.ToString("D")).OrderBy(value => value, StringComparer.Ordinal));
                var identity = Observe(); Dependency(key, identity); Require(() => { if (Observe() != identity) throw Error("installed assembly dependency changed: " + key); });
            }
            internal CaptureResult Finish()
            {
                var ordered = owners.OrderBy(owner => owner.Snapshot.Key, StringComparer.Ordinal).ToArray();
                if (ordered.Select(owner => owner.Snapshot.Key).Distinct(StringComparer.Ordinal).Count() != ordered.Length) throw Error("control owner role is repeated");
                foreach (var owner in ordered) foreach (var gap in owner.Snapshot.CoverageGaps) Gaps.Add(gap);
                if (Payload != null) foreach (var gap in Payload.Snapshot.CoverageGaps) Gaps.Add(gap);
                var data = new Data { ObservedFrame = ObservedFrame, Owners = ordered.Select(owner => owner.Snapshot).ToArray(),
                    Dependencies = dependencies.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new Dependency { Key = pair.Key, Value = pair.Value }).ToArray(),
                    CoverageGaps = Gaps.ToArray(), Payload = Payload?.Snapshot };
                ValidateCapture(data, ObservedFrame != null); return new CaptureResult(data, ordered, external, resolved, admitted, checks.ToArray(), Payload);
            }
        }
        internal static void Validate(Data data)
            => ValidateCapture(data, false);
        private static void ValidateCapture(Data data, bool frameOnly)
        {
            if (data == null || (data.ObservedFrame != null) != frameOnly || data.Version != 1 || data.Owners == null || data.Dependencies == null || data.CoverageGaps == null
                || data.Owners.Length == 0 || data.Owners.Length > 1024 || data.Dependencies.Length > 4096 || data.CoverageGaps.Length > 4096)
                throw Error("control-plane packet is absent or exceeds its bound");
            var previous = "";
            foreach (var owner in data.Owners) {
                if (owner == null || string.IsNullOrEmpty(owner.Key) || StringComparer.Ordinal.Compare(previous, owner.Key) >= 0)
                    throw Error("control owner roles are absent, duplicated or unordered");
                previous = owner.Key;
            }
            previous = "";
            foreach (var dependency in data.Dependencies) {
                if (dependency == null || string.IsNullOrEmpty(dependency.Key) || dependency.Value == null
                    || StringComparer.Ordinal.Compare(previous, dependency.Key) >= 0) throw Error("control dependency is absent, duplicated or unordered");
                previous = dependency.Key;
            }
            if (data.Payload != null) ValidateObservedPayload(data.Payload, frameOnly);
            else if (frameOnly) throw Error("frame clock payload is absent");
        }
        private static string NormalizeDependencyValue(string key, string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (key == "audio/installed-assemblies") {
                var entries = value.Split(';');
                for (int i = 0; i < entries.Length; i++) {
                    var slash = entries[i].LastIndexOf('/');
                    if (slash > 0) {
                        var fullName = entries[i].Substring(0, slash);
                        var comma = fullName.IndexOf(',');
                        var shortName = (comma >= 0 ? fullName.Substring(0, comma) : fullName).Trim();
                        if (ResidentManagedTypeCatalog.IsControlPlaneAssembly(shortName)) {
                            entries[i] = fullName + "/00000000-0000-0000-0000-000000000000";
                        }
                    }
                }
                Array.Sort(entries, StringComparer.Ordinal);
                return string.Join(";", entries);
            }
            if (key.StartsWith("hook/", StringComparison.Ordinal)) {
                var searchStart = value.StartsWith("resident-metadata/1/", StringComparison.Ordinal) ? "resident-metadata/1/".Length : 0;
                var secondMeta = value.IndexOf("/resident-metadata/1/", searchStart, StringComparison.Ordinal);
                if (secondMeta > searchStart) {
                    var left = value.Substring(0, secondMeta);
                    var right = value.Substring(secondMeta + 1);
                    return ResidentGraphPolicy.NormalizeMetadataOrDelegate(left) + "/" + ResidentGraphPolicy.NormalizeMetadataOrDelegate(right);
                }
                for (var pos = searchStart; ;) {
                    var delegateMarker = value.IndexOf("/eyJ", pos, StringComparison.Ordinal);
                    if (delegateMarker <= searchStart) break;
                    if (searchStart == 0 || (delegateMarker - searchStart) % 4 == 0) {
                        var left = value.Substring(0, delegateMarker);
                        var right = value.Substring(delegateMarker + 1);
                        return ResidentGraphPolicy.NormalizeMetadataOrDelegate(left) + "/" + ResidentGraphPolicy.NormalizeMetadataOrDelegate(right);
                    }
                    pos = delegateMarker + 1;
                }
            }
            if (value.StartsWith("resident-metadata/1/", StringComparison.Ordinal)) {
                return ResidentGraphPolicy.NormalizeMetadataOrDelegate(value);
            }
            if (key == "payload/random-producer/abi" && value.StartsWith("1/", StringComparison.Ordinal)) {
                return "1/" + ResidentGraphPolicy.NormalizeMetadataOrDelegate(value.Substring(2));
            }
            var segments = value.Split('/');
            for (int i = 0; i < segments.Length; i++) {
                if (segments[i] == "resident-metadata" && i + 2 < segments.Length && segments[i + 1] == "1") {
                    var fullMeta = "resident-metadata/1/" + segments[i + 2];
                    var normalized = ResidentGraphPolicy.NormalizeMetadataOrDelegate(fullMeta);
                    if (normalized.StartsWith("resident-metadata/1/", StringComparison.Ordinal)) {
                        segments[i + 2] = normalized.Substring("resident-metadata/1/".Length);
                    }
                    i += 2;
                } else if (segments[i].Length > 20) {
                    segments[i] = ResidentGraphPolicy.NormalizeMetadataOrDelegate(segments[i]);
                }
            }
            return string.Join("/", segments);
        }

        private static string LayoutIdentity(Data data) => JsonConvert.SerializeObject(new {
            data.Version, Owners = data.Owners.Select(ResidentProcessSlots.CanonicalLayoutJson).ToArray(), Dependencies = data.Dependencies.Select(d => new { d.Key, Value = NormalizeDependencyValue(d.Key, d.Value) }).ToArray(), data.CoverageGaps, Payload = PayloadLayout(data.Payload)
        });
        private static FieldInfo Field(Type type, string name)
        {
            for (var current = type; current != null; current = current.BaseType) {
                var field = current.GetField(name, Fields | BindingFlags.DeclaredOnly); if (field != null) return field;
            }
            throw Error("actual control field is absent: " + type.FullName + "/" + name);
        }
        private static object? Read(object owner, string name) => Field(owner.GetType(), name).GetValue(owner);
        private static object? ReadStatic(Type type, string name) => Field(type, name).GetValue(null);
        private static string DescribeMetadata(object value) => new ResidentGraphPolicy(_ => null).BindMetadata(value) ?? throw Error("control metadata identity is absent");
        private static InvalidDataException Error(string message) => new InvalidDataException("Resident control plane: " + message);
        private sealed class References : IEqualityComparer<object>
        {
            internal static readonly References Instance = new References();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
