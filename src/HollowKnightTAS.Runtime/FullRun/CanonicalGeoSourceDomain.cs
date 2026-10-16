using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using HutongGames.PlayMaker;
using System.Reflection;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Source packets remain in memory. The caller controls producer/retirement
    // and must finish main application before preparing this separate domain.
    internal sealed class CanonicalGeoSourceDomain
    {
        private const string MainNode = "Geo/MainNode/", Owner = "Geo/Owner/";
        private readonly CanonicalCarryGraphCodec codec = CanonicalCarryFieldRegistry.Create(geoDomain: true);
        internal byte[] Capture(GeoCounter counter, IReadOnlyDictionary<string, UnityEngine.Object> directory,
            IReadOnlyDictionary<object, int> mainNodes, Func<object, string?> bindOther, Action<CanonicalCarryGraphData> auditAssets)
        {
            var data = CanonicalGeoCarry.Capture(counter, directory);
            string? Bind(object value) {
                if (mainNodes.TryGetValue(value, out var id)) return MainNode + id;
                foreach (var pair in directory) if (ReferenceEquals(pair.Value, value)) return Owner + pair.Key;
                return bindOther(value);
            }
            var graph = codec.Capture(CanonicalGeoCarry.Roots(data), Bind);
            if (graph.Unsupported.Length != 0) throw new InvalidDataException("Source Geo graph has an explicit unsupported field: " + string.Join(";", graph.Unsupported));
            auditAssets(graph);
            return codec.Encode(graph);
        }
        internal Action Prepare(byte[] source, GeoCounter counter, IReadOnlyDictionary<string, UnityEngine.Object> directory,
            CanonicalCarryGraphCodec.PreparedView main, Action requireMainApplied,
            Func<string, object> resolveOther, Func<bool> admitted,
            Action<IReadOnlyDictionary<string, object>> exposePreparedDirectory, out Action beginOriginalLifecycle, out Action requireSourceNative)
        {
            requireMainApplied();
            if (!admitted()) throw new InvalidOperationException("Post-main Geo graph has no paused admission.");
            var graph = codec.Decode(source);
            var actual = CanonicalGeoCarry.Capture(counter, directory);
            object Resolve(string binding) {
                if (binding.StartsWith(MainNode, StringComparison.Ordinal)) {
                    if (!int.TryParse(binding.Substring(MainNode.Length), out var id)) throw new InvalidDataException("Source Geo main-node binding is malformed.");
                    return main.Node(id);
                }
                if (binding.StartsWith(Owner, StringComparison.Ordinal))
                    return directory.TryGetValue(binding.Substring(Owner.Length), out var value) ? value
                        : throw new InvalidDataException("Source Geo native owner binding is absent.");
                return resolveOther(binding);
            }
            CanonicalCarryGraphCodec.PreparedLifecycleWrites? lifecycle = null;
            CanonicalGeoCarry.Data? saved = null;
            var lifecycleRoots = CanonicalGeoCarry.Roots(actual).Where(pair => pair.Key != CanonicalGeoCarry.Root).Select(pair => pair.Key).ToArray();
            if (lifecycleRoots.Length != 6 || actual.Fsms.Any(fsm => fsm.GetType() != typeof(Fsm)))
                throw new InvalidDataException("Geo lifecycle requires six exact original Fsm roots.");
            var plan = codec.PrepareOwnedGraph(graph, CanonicalGeoCarry.Roots(actual), Resolve, view => {
                saved = new CanonicalGeoCarry.Data();
                var sourceRoot = view.Root(CanonicalGeoCarry.Root);
                foreach (var field in CanonicalCarryGraphCodec.Fields(typeof(CanonicalGeoCarry.Data)))
                    field.SetValue(saved, view.Field(sourceRoot, field.DeclaringType!, field.Name));
                var final = directory.ToDictionary(pair => pair.Key, pair => (object)pair.Value, StringComparer.Ordinal);
                foreach (var pair in directory.Where(pair => pair.Value is PlayMakerFSM)) {
                    var fsm = (Fsm)view.Root("Geo/Fsm/" + pair.Key);
                    var path = pair.Key + "/fsm"; final.Add(path, fsm);
                    var states = (FsmState[]?)view.Field(fsm, typeof(Fsm), "states")
                        ?? throw new InvalidDataException("Source Geo has no complete prepared state directory.");
                    for (var state = 0; state < states.Length; state++) {
                        var statePath = path + "/state[" + state + "]"; final.Add(statePath, states[state]);
                        // State/action nodes are detached graph nodes, already
                        // fully filled by the codec; only the six real Fsm roots
                        // await writes. A state shared with main is already live
                        // and main's full readback is required above.
                        var actions = typeof(FsmState).GetField("actions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(states[state]) as FsmStateAction[];
                        if (actions == null) continue;
                        for (var action = 0; action < actions.Length; action++) if (actions[action] != null)
                            final.Add(statePath + "/action[" + action + "]", actions[action]);
                    }
                }
                exposePreparedDirectory(final);
            }, lifecycleWriteRoots: lifecycleRoots, exposeLifecycleWrites: controller => lifecycle = controller);
            beginOriginalLifecycle = () => lifecycle!.BeginOriginalLifecycle(() => {
                if (!admitted()) throw new InvalidOperationException("Geo lifecycle lost paused admission.");
                requireMainApplied();
            });
            void RequireNative() {
                if (!admitted() || saved == null) throw new InvalidOperationException("Geo native source admission was lost.");
                var native = CanonicalGeoCarry.Capture(counter, directory);
                // Native state is produced by the original lifecycle, never
                // imitated by writing Awake/Start bytes or invoking Start.
                if (!saved!.NativeOwners.SequenceEqual(native.NativeOwners) || !saved!.NativeLifecycle.SequenceEqual(native.NativeLifecycle)
                    || !saved!.FsmRegistrations.SequenceEqual(native.FsmRegistrations)
                    || !saved!.GameObjectOwners.SequenceEqual(native.GameObjectOwners) || !saved!.ActiveSelf.SequenceEqual(native.ActiveSelf)
                    || !actual.Fsms.Zip(native.Fsms, (left, right) => ReferenceEquals(left, right)).All(equal => equal))
                    throw new InvalidOperationException("Source Geo native lifecycle/activation needs an unsupported original producer continuation.");
            }
            requireSourceNative = RequireNative;
            var consumed = false;
            return () => {
                if (consumed || !admitted()) throw new InvalidOperationException("Post-main Geo graph was consumed or lost paused admission.");
                consumed = true; requireMainApplied();
                var sourceData = saved ?? throw new InvalidOperationException("Geo source snapshot is absent.");
                RequireNative();
                lifecycle!.PrepareWritesAfterOriginalLifecycle(() => {
                    if (!admitted()) throw new InvalidOperationException("Geo final lifecycle admission lost pause.");
                    requireMainApplied();
                });
                plan.Apply(); plan.VerifyReadback();
                CanonicalGeoCarry.PrepareCounter(sourceData, counter, admitted)();
                plan.VerifyReadback(); requireMainApplied();
                Modding.Logger.Log("[HKTAS] Geo complete source graph/counter readback passed: fsmRoots=" + actual.Fsms.Length
                    + ";counterFields=" + actual.CounterValues.Length + ";nativeOwners=" + actual.NativeOwners.Length
                    + ";originalNativeLifecycleActivationRegistrationEqual=true;currentPlayerGeoFirstGeoEqual=true;mainInputReadbackEqual=true;completeStateCoverage=false");
            };
        }
    }
}
