using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Managed source data is independent of the original native producer.
    // No lifecycle byte or activation is manufactured by this domain.
    internal sealed class CanonicalMarkerSourceDomain
    {
        private const string MainNode = "Marker/MainNode/", Owner = "Marker/Owner/";
        private readonly CanonicalCarryGraphCodec codec = CanonicalCarryFieldRegistry.Create(markerDomain: true);
        internal byte[] Capture(GameObject root, IReadOnlyDictionary<object, int> mainNodes,
            Func<object, string?> bindOther, Action<CanonicalCarryGraphData> auditAssets)
        {
            CanonicalMarkerBoundaryPolicy.RequireCold(root);
            var directory = CanonicalMarkerDirectory.Build(root);
            CanonicalMarkerEventCarry.RequireEmpty(directory);
            var readOnly = CanonicalMarkerReadonlyCarry.Directory(directory);
            string? Bind(object value) {
                if (mainNodes.TryGetValue(value, out var id)) return MainNode + id;
                foreach (var pair in directory) if (ReferenceEquals(pair.Value, value)) return Owner + pair.Key;
                foreach (var pair in readOnly) if (ReferenceEquals(pair.Value, value)) return Owner + pair.Key;
                return bindOther(value);
            }
            var graph = codec.Capture(CanonicalMarkerCarry.Roots(CanonicalMarkerCarry.Capture(root)), Bind);
            Modding.Logger.Log("[HKTAS] Original Marker source D native/parent directory: "
                + Newtonsoft.Json.JsonConvert.SerializeObject(CanonicalMarkerCarry.Observe(root)));
            if (graph.Unsupported.Length != 0)
                throw new InvalidDataException("Marker complete source graph has unsupported fields: " + string.Join(";", graph.Unsupported));
            auditAssets(graph);
            return codec.Encode(graph);
        }
        internal Action Prepare(byte[] source, GameObject target, CanonicalCarryGraphCodec.PreparedView main,
            Action requireMainApplied, Func<string, object> resolveOther, Func<bool> admitted,
            Action<IReadOnlyDictionary<string, object>> exposeDirectory,
            out Action beginOriginalLifecycle, out Action requireSourceNative)
        {
            requireMainApplied();
            if (!admitted()) throw new InvalidOperationException("Marker preparation lost post-main paused admission.");
            CanonicalMarkerBoundaryPolicy.RequireCold(target);
            var directory = CanonicalMarkerDirectory.Build(target);
            var readOnly = CanonicalMarkerReadonlyCarry.Directory(directory);
            var actual = CanonicalMarkerCarry.Capture(target);
            object Resolve(string binding) {
                if (binding.StartsWith(MainNode, StringComparison.Ordinal)) {
                    if (!int.TryParse(binding.Substring(MainNode.Length), out var id))
                        throw new InvalidDataException("Marker source main-node binding differs.");
                    return main.Node(id);
                }
                if (binding.StartsWith(Owner, StringComparison.Ordinal))
                    return directory.TryGetValue(binding.Substring(Owner.Length), out var value) ? value
                        : readOnly.TryGetValue(binding.Substring(Owner.Length), out var preserved) ? preserved
                        : throw new InvalidDataException("Marker source original owner binding is absent.");
                return resolveOther(binding);
            }
            CanonicalMarkerCarry.Data? saved = null;
            CanonicalCarryGraphCodec.PreparedLifecycleWrites? lifecycle = null;
            var graph = codec.Decode(source);
            var lifecycleRoots = CanonicalMarkerCarry.Roots(actual).Keys.Where(key => key != CanonicalMarkerCarry.Root).ToArray();
            var plan = codec.PrepareOwnedGraph(graph, CanonicalMarkerCarry.Roots(actual), Resolve, view => {
                saved = new CanonicalMarkerCarry.Data();
                var sourceRoot = view.Root(CanonicalMarkerCarry.Root);
                foreach (var field in CanonicalCarryGraphCodec.Fields(typeof(CanonicalMarkerCarry.Data)))
                    field.SetValue(saved, view.Field(sourceRoot, field.DeclaringType!, field.Name));
                CanonicalMarkerCarry.Roots(saved);
                for (var owner = 0; owner < saved.NativeOwners.Length; owner++)
                    if (saved.Lifecycle[owner * 5] != 0 || saved.Lifecycle[owner * 5 + 1] != 0
                        || saved.Lifecycle[owner * 5 + 3] != 0 || saved.Lifecycle[owner * 5 + 4] != 0)
                        throw new InvalidDataException("Marker source packet predates the common real cold Canon.");
                var final = directory.ToDictionary(pair => pair.Key, pair => (object)pair.Value, StringComparer.Ordinal);
                foreach (var pair in readOnly) final.Add(pair.Key, pair.Value);
                for (var owner = 0; owner < saved.NativeOwners.Length; owner++) {
                    var native = directory[saved.NativeOwners[owner]];
                    if (!(native is TMPro.TextMeshPro)) continue;
                    var fields = CanonicalMarkerCarry.ManagedFields(native.GetType());
                    var index = Array.FindIndex(fields, field => field == CanonicalMarkerEventCarry.EventField);
                    if (index < 0 || saved.FieldValues[owner][index] == null)
                        throw new InvalidDataException("Marker complete prepared empty event is absent.");
                    final.Add(saved.NativeOwners[owner] + "/event/m_OnCullStateChanged", saved.FieldValues[owner][index]!);
                }
                var path = actual.FsmOwners.Single() + "/fsm";
                var fsm = (Fsm)view.Root(lifecycleRoots.Single()); final.Add(path, fsm);
                var states = (FsmState[]?)view.Field(fsm, typeof(Fsm), "states")
                    ?? throw new InvalidDataException("Marker complete prepared states are absent.");
                for (var state = 0; state < states.Length; state++) {
                    var statePath = path + "/state[" + state + "]"; final.Add(statePath, states[state]);
                    var actions = typeof(FsmState).GetField("actions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(states[state]) as FsmStateAction[];
                    if (actions == null) continue;
                    for (var action = 0; action < actions.Length; action++) if (actions[action] != null)
                        final.Add(statePath + "/action[" + action + "]", actions[action]);
                }
                exposeDirectory(final);
            }, lifecycleWriteRoots: lifecycleRoots, exposeLifecycleWrites: controller => lifecycle = controller);
            void Require() { if (!admitted()) throw new InvalidOperationException("Marker lifecycle lost admission."); requireMainApplied(); }
            beginOriginalLifecycle = () => lifecycle!.BeginOriginalLifecycle(Require);
            void Native() {
                if (!admitted() || saved == null) throw new InvalidOperationException("Marker native source snapshot admission is absent.");
                var current = CanonicalMarkerCarry.Capture(target);
                if (!saved.NativeOwners.SequenceEqual(current.NativeOwners) || !saved.Lifecycle.SequenceEqual(current.Lifecycle)
                    || !saved.Registered.SequenceEqual(current.Registered) || !saved.GameObjectOwners.SequenceEqual(current.GameObjectOwners)
                    || !saved.ActiveSelf.SequenceEqual(current.ActiveSelf)
                    || !actual.Fsms.Zip(current.Fsms, (left, right) => ReferenceEquals(left, right)).All(equal => equal))
                    throw new InvalidOperationException("Marker native lifecycle requires unsupported original source continuation.");
            }
            requireSourceNative = Native;
            var consumed = false;
            return () => {
                if (consumed || !admitted() || saved == null) throw new InvalidOperationException("Marker source application lost single-use admission.");
                consumed = true;
                Native();
                lifecycle!.PrepareWritesAfterOriginalLifecycle(Require);
                plan.Apply(); plan.VerifyReadback();
                CanonicalMarkerCarry.PrepareManaged(saved, target, codec, admitted)();
                plan.VerifyReadback(); requireMainApplied(); Native();
            };
        }
    }
}
