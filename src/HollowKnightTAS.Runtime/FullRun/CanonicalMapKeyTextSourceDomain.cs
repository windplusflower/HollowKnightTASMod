using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // The source graph contains every managed field of the nine Text leaves.
    // Key parents and their Control FSM are external, never canonical inputs
    // inferred from hasPin or mapKeyPref.
    internal sealed class CanonicalMapKeyTextSourceDomain
    {
        private const string MainNode = "MapKeyText/MainNode/", Owner = "MapKeyText/Owner/";
        private readonly CanonicalCarryGraphCodec codec = CanonicalCarryFieldRegistry.Create(mapKeyTextDomain: true);
        internal static void RequireCold(Transform keys)
        {
            LoadingSerialSceneProbe.RequireCanonicalMutationBoundary();
            ObserveCold(keys);
        }
        private static void ObserveCold(Transform keys)
        {
            var directory = CanonicalMapKeyTextDirectory.Build(keys);
            using var validation = new LoadingEngineClockProbe.CheckpointValidation();
            foreach (var owner in directory.Values.OfType<MonoBehaviour>()) {
                var parent = owner.transform.parent;
                if (parent == null || parent.gameObject.activeInHierarchy || owner.gameObject.activeInHierarchy)
                    throw new InvalidOperationException("Active Map Key Text needs its original natural-close continuation.");
                var flags = BoundaryMonoLifecycle.ObserveUnstartedUi(owner);
                if (flags[0] != 0 || flags[1] != 0 || flags[3] != 0 || flags[4] != 0)
                    throw new InvalidOperationException("Map Key Text canonical owner is not a real never-awakened inactive instance.");
            }
        }
        internal byte[] Capture(Transform keys, IReadOnlyDictionary<object, int> mainNodes,
            Func<object, string?> bindOther, Action<CanonicalCarryGraphData> auditAssets)
        {
            RequireCold(keys);
            var directory = CanonicalMapKeyTextDirectory.Build(keys);
            CanonicalMarkerEventCarry.RequireEmpty(directory);
            var readOnly = CanonicalMarkerReadonlyCarry.MapKeyDirectory(directory);
            string? Bind(object value) {
                if (mainNodes.TryGetValue(value, out var id)) return MainNode + id;
                foreach (var pair in directory) if (ReferenceEquals(pair.Value, value)) return Owner + pair.Key;
                foreach (var pair in readOnly) if (ReferenceEquals(pair.Value, value)) return Owner + pair.Key;
                return bindOther(value);
            }
            var graph = codec.Capture(CanonicalMapKeyTextCarry.Roots(CanonicalMapKeyTextCarry.Capture(directory)), Bind);
            if (graph.Unsupported.Length != 0)
                throw new InvalidDataException("Map Key complete source graph has unsupported fields: " + string.Join(";", graph.Unsupported));
            auditAssets(graph);
            return codec.Encode(graph);
        }
        internal Action Prepare(byte[] source, Transform keys, CanonicalCarryGraphCodec.PreparedView main,
            Action requireMainApplied, Func<string, object> resolveOther, Func<bool> admitted, Action<IReadOnlyDictionary<string, object>> exposeDirectory, out Action verifySource)
        {
            requireMainApplied();
            if (!admitted()) throw new InvalidOperationException("Map Key preparation lost post-main paused admission.");
            RequireCold(keys);
            var directory = CanonicalMapKeyTextDirectory.Build(keys);
            var readOnly = CanonicalMarkerReadonlyCarry.MapKeyDirectory(directory);
            var actual = CanonicalMapKeyTextCarry.Capture(directory);
            object Resolve(string binding) {
                if (binding.StartsWith(MainNode, StringComparison.Ordinal)) {
                    if (!int.TryParse(binding.Substring(MainNode.Length), out var id)) throw new InvalidDataException("Map Key source main-node binding differs.");
                    return main.Node(id);
                }
                if (binding.StartsWith(Owner, StringComparison.Ordinal))
                    return directory.TryGetValue(binding.Substring(Owner.Length), out var value) ? value
                        : readOnly.TryGetValue(binding.Substring(Owner.Length), out var preserved) ? preserved
                        : throw new InvalidDataException("Map Key source original owner binding is absent.");
                return resolveOther(binding);
            }
            CanonicalMapKeyTextCarry.Data? saved = null;
            var graph = codec.Decode(source);
            var plan = codec.PrepareOwnedGraph(graph, CanonicalMapKeyTextCarry.Roots(actual), Resolve, view => {
                saved = new CanonicalMapKeyTextCarry.Data();
                var sourceRoot = view.Root(CanonicalMapKeyTextCarry.Root);
                foreach (var field in CanonicalCarryGraphCodec.Fields(typeof(CanonicalMapKeyTextCarry.Data)))
                    field.SetValue(saved, view.Field(sourceRoot, field.DeclaringType!, field.Name));
                CanonicalMapKeyTextCarry.Roots(saved);
                for (var owner = 0; owner < saved.NativeOwners.Length; owner++)
                    if (saved.Lifecycle[owner * 5] != 0 || saved.Lifecycle[owner * 5 + 1] != 0
                        || saved.Lifecycle[owner * 5 + 3] != 0 || saved.Lifecycle[owner * 5 + 4] != 0)
                        throw new InvalidDataException("Map Key source packet predates the common real cold Canon.");
                var final = directory.ToDictionary(pair => pair.Key, pair => (object)pair.Value, StringComparer.Ordinal);
                foreach (var pair in readOnly) final.Add(pair.Key, pair.Value);
                for (var owner = 0; owner < saved.NativeOwners.Length; owner++) {
                    var native = directory[saved.NativeOwners[owner]];
                    if (!(native is TMPro.TextMeshPro)) continue;
                    var fields = CanonicalMapKeyTextCarry.ManagedFields(native.GetType());
                    var index = Array.FindIndex(fields, field => field == CanonicalMarkerEventCarry.EventField);
                    if (index < 0 || saved.FieldValues[owner][index] == null) throw new InvalidDataException("Map Key prepared empty event is absent.");
                    final.Add(saved.NativeOwners[owner] + "/event/m_OnCullStateChanged", saved.FieldValues[owner][index]!);
                }
                exposeDirectory(final);
            });
            void Native() {
                if (!admitted() || saved == null) throw new InvalidOperationException("Map Key native source snapshot admission is absent.");
                ObserveCold(keys);
                var current = CanonicalMapKeyTextCarry.Capture(directory);
                if (!saved.NativeOwners.SequenceEqual(current.NativeOwners) || !saved.Lifecycle.SequenceEqual(current.Lifecycle)
                    || !saved.GameObjectOwners.SequenceEqual(current.GameObjectOwners) || !saved.ActiveSelf.SequenceEqual(current.ActiveSelf))
                    throw new InvalidOperationException("Map Key native lifecycle requires unsupported original source continuation.");
            }
            void VerifySource() {
                Native(); plan.VerifyReadback(); requireMainApplied();
                var currentOwners = directory.Where(pair => pair.Value is MonoBehaviour).ToArray();
                for (var owner = 0; owner < currentOwners.Length; owner++) {
                    var managedFields = CanonicalMapKeyTextCarry.ManagedFields(currentOwners[owner].Value.GetType());
                    for (var field = 0; field < managedFields.Length; field++)
                        if (!codec.SameRawSlot(saved!.FieldValues[owner][field], managedFields[field].GetValue(currentOwners[owner].Value), managedFields[field].FieldType, 0))
                            throw new InvalidOperationException("Map Key final source field readback differs.");
                }
            }
            verifySource = VerifySource;
            var consumed = false;
            return () => {
                if (consumed || !admitted() || saved == null) throw new InvalidOperationException("Map Key source application lost single-use admission.");
                consumed = true;
                requireMainApplied(); Native(); plan.Apply(); plan.VerifyReadback();
                CanonicalMapKeyTextCarry.PrepareManaged(saved, keys, codec, admitted)();
                VerifySource();
            };
        }
    }
}
