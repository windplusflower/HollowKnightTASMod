using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using MonoMod.RuntimeDetour;
using Newtonsoft.Json;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Opt-in causal observation only. Buffered records contain scalars and
    // metadata strings, never scene owners, addresses or saved D packets.
    internal sealed class CanonicalPoolFlukeDiagnostic : IDisposable
    {
        internal const string EnvironmentFlag = "HKTAS_LOADING_POOL_FLUKE_CAUSALITY";
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly bool Enabled = Environment.GetEnvironmentVariable(EnvironmentFlag) == "1";
        [ThreadStatic] private static string? phase;
        internal readonly struct Phase : IDisposable
        {
            private readonly string? previous;
            internal Phase(string value) { previous = phase; if (Enabled) phase = value; }
            public void Dispose() { if (Enabled) phase = previous; }
        }
        internal static Phase EnterPhase(string value) => new Phase(value);
        private readonly Hook? hook;
        private readonly List<string> records = new List<string>();
        private readonly List<string> failures = new List<string>();
        private int observed;
        private bool disposed;
        private static FieldInfo Field(Type type, string name)
            => type.GetField(name, All) ?? throw new MissingFieldException(type.FullName, name);
        internal CanonicalPoolFlukeDiagnostic()
        {
            if (!Enabled) return;
            CanonicalPhaseOneTrace.RequireContext(Environment.GetEnvironmentVariable);
            if (!CanonicalActualDExperiment.Enabled || !CanonicalPhaseOneTrace.Enabled)
                throw new InvalidOperationException("Pool Fluke causality requires the protected actual-D diagnostic.");
            var method = typeof(CreateGameObjectPool).GetMethod("OnEnter", All, null, Type.EmptyTypes, null);
            if (typeof(CreateGameObjectPool).Module.ModuleVersionId != new Guid("2b54cdaf-7a93-4f0e-b297-d50c4090ea9f")
                || method == null || method.MetadataToken != 100663546 || method.IsStatic || method.ReturnType != typeof(void))
                throw new InvalidOperationException("Original CreateGameObjectPool.OnEnter diagnostic ABI differs.");
            hook = new Hook(method, (Action<Action<CreateGameObjectPool>, CreateGameObjectPool>)Observe);
            Modding.Logger.Log("[HKTAS] Pool Fluke diagnostic enabled=true installed=true output="
                + Path.Combine(Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT")!, "pool-flukes-producer-diagnostic.json"));
        }
        private static int BlackFreeCount()
        {
            var pool = Field(typeof(ObjectPool), "_instance").GetValue(null) as ObjectPool;
            if (pool == null) return -1;
            var table = Field(typeof(ObjectPool), "pooledObjects").GetValue(pool) as Dictionary<GameObject, List<GameObject>>;
            if (table == null || table.Count > 4096) throw new InvalidOperationException("Diagnostic pool directory bound differs.");
            var matches = table.Where(pair => pair.Key != null && pair.Key.name == "Spell Fluke Black").ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Diagnostic black pool prefab name is ambiguous.");
            return matches.Length == 0 ? 0 : matches[0].Value.Count;
        }
        private void Observe(Action<CreateGameObjectPool> original, CreateGameObjectPool action)
        {
            string? before = null;
            try {
                var fsm = Field(typeof(FsmStateAction), "fsm").GetValue(action) as Fsm;
                var state = Field(typeof(FsmStateAction), "fsmState").GetValue(action) as FsmState;
                if (fsm != null && state != null && Equals(Field(typeof(Fsm), "name").GetValue(fsm), "Pool Flukes")
                    && Equals(Field(typeof(FsmState), "name").GetValue(state), "Pool Black")) {
                    var owner = Field(typeof(Fsm), "owner").GetValue(fsm) as PlayMakerFSM;
                    var hero = HeroController.SilentInstance;
                    if (owner == null || hero == null || owner.gameObject.name != "Charm Effects"
                        || !ReferenceEquals(owner.transform.parent, hero.transform)
                        || !ReferenceEquals(Field(typeof(PlayMakerFSM), "fsm").GetValue(owner), fsm))
                        throw new InvalidOperationException("Diagnostic Pool Flukes is not the actual Hero child.");
                    var states = Field(typeof(Fsm), "states").GetValue(fsm) as FsmState[];
                    var actions = Field(typeof(FsmState), "actions").GetValue(state) as FsmStateAction[];
                    if (states == null || states.Length != 4 || !states.Any(value => ReferenceEquals(value, state))
                        || actions == null || actions.Length != 1 || !ReferenceEquals(actions[0], action))
                        throw new InvalidOperationException("Diagnostic Pool Black exact action layout differs.");
                    observed++;
                    if (observed <= 8) {
                        var components = owner.GetComponents<PlayMakerFSM>();
                        var index = Array.FindIndex(components, component => ReferenceEquals(component, owner));
                        var player = typeof(PlayerData).GetField("_instance", All)!.GetValue(null);
                        var level = player == null ? -1 : (int)Field(typeof(PlayerData), "fireballLevel").GetValue(player);
                        var stack = (new StackTrace(1, false).GetFrames() ?? Array.Empty<StackFrame>()).Take(16)
                            .Select(frame => { var method = frame.GetMethod(); return method == null ? "unknown" : method.DeclaringType?.FullName + "/" + method.Name; }).ToArray();
                        before = JsonConvert.SerializeObject(new { role = CanonicalActualDExperiment.CaptureSource ? "source" : "target",
                            phase = phase ?? "startup-or-loading-outside-Canon-and-D", targetStage = LoadingSerialSceneProbe.ExperimentalTargetStage,
                            engineFrame = Time.frameCount, owner = "actual-Hero/Charm Effects/PlayMakerFSM[" + index + "]",
                            state = "Pool Black", actionIndex = 0, fireballLevel = level,
                            amount = Field(typeof(FsmInt), "value").GetValue(action.amount),
                            useExisting = Field(typeof(FsmBool), "value").GetValue(action.useExisting),
                            blackFreeBefore = BlackFreeCount(), stack, originalCalledOnce = true, diagnosticOnly = true });
                    }
                }
            } catch (Exception error) { if (failures.Count < 8) failures.Add(error.GetType().Name + ":" + error.Message); }
            // Observation failures never suppress or replay the original action.
            try { original(action); }
            finally {
                if (before != null) {
                    try { records.Add(JsonConvert.SerializeObject(new { before = JsonConvert.DeserializeObject(before), blackFreeAfter = BlackFreeCount() })); }
                    catch (Exception error) { if (failures.Count < 8) failures.Add(error.GetType().Name + ":" + error.Message); }
                }
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; hook?.Dispose();
            if (!Enabled) return;
            var directory = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT");
            if (string.IsNullOrEmpty(directory)) return;
            try {
                File.WriteAllText(Path.Combine(directory, "pool-flukes-producer-diagnostic.json"), JsonConvert.SerializeObject(new {
                    diagnosticOnly = true, observed, recordLimit = 8, overflow = observed > 8, failures,
                    records = records.Select(JsonConvert.DeserializeObject).ToArray(), rngReadOrAdvanced = false,
                    sceneObjectsRetained = false, nativeAddressesEmitted = false, completeStateCoverage = false, restoreQualified = false }));
            } catch (Exception error) {
                // The artifact's absence is a failed diagnostic. Do not let an
                // evidence-write error prevent the original session cleanup.
                Modding.Logger.LogError("[HKTAS] Pool Fluke diagnostic flush failed: " + error.GetType().Name + ":" + error.Message);
            }
        }
    }
}
