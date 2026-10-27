using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Actual forty-owner source graph after the common ten-cold/three-active producer.
    // The three native meshes are bound only through their exact original TMP Awake owners.
    internal sealed class CanonicalHudPresentationSourceDomain
    {
        private const string MainNode = "HudPresentation/MainNode/", Owner = "HudPresentation/Owner/";
        private readonly CanonicalCarryGraphCodec codec = CanonicalCarryFieldRegistry.Create(hudPresentationDomain: true);
        private readonly CanonicalHudPresentationColdTemplate template;
        internal CanonicalHudPresentationSourceDomain(CanonicalHudPresentationColdTemplate template) { this.template = template; }
        private CanonicalHudPresentationCarry.Data CaptureOriginalAssets(IReadOnlyDictionary<string, UnityEngine.Object> directory)
        {
            template.RequireFrozenAssets();
            var data = CanonicalHudPresentationCarry.Capture(directory);
            data.OriginalAssetOrigins = template.AssetManifest;
            return data;
        }
        internal static void RequireCold(Transform keys)
        {
            LoadingSerialSceneProbe.RequireCanonicalMutationBoundary();
            ObserveCold(keys);
        }
        private static void ObserveCold(Transform keys, IReadOnlyList<GameObject>? roots = null)
        { CanonicalHudPresentationColdTemplate.RequireProduced(CanonicalHudPresentationDirectory.Build(keys, roots)); }
        internal byte[] Capture(Transform keys, IReadOnlyDictionary<object, int> mainNodes,
            Func<object, string?> bindOther, Action<CanonicalCarryGraphData> auditAssets)
        {
            RequireCold(keys);
            var directory = CanonicalHudPresentationDirectory.Build(keys);
            CanonicalMarkerEventCarry.RequireEmpty(directory);
            var meshes = CanonicalHudPresentationColdTemplate.FreshBorderMeshes(directory);
            var readOnly = CanonicalMarkerReadonlyCarry.MapKeyDirectory(directory);
            string? Bind(object value) {
                if (mainNodes.TryGetValue(value, out var id)) return MainNode + id;
                foreach (var pair in directory) if (ReferenceEquals(pair.Value, value)) return Owner + pair.Key;
                foreach (var pair in readOnly) if (ReferenceEquals(pair.Value, value)) return Owner + pair.Key;
                foreach (var pair in meshes) if (ReferenceEquals(pair.Value, value)) return Owner + pair.Key;
                return bindOther(value);
            }
            var graph = codec.Capture(CanonicalHudPresentationCarry.Roots(CaptureOriginalAssets(directory)), Bind);
            if (graph.Unsupported.Length != 0)
                throw new InvalidDataException("HUD presentation complete source graph has unsupported fields: " + string.Join(";", graph.Unsupported));
            auditAssets(graph);
            return codec.Encode(graph);
        }
        internal Action Prepare(byte[] source, Transform keys, CanonicalCarryGraphCodec.PreparedView main,
            Action requireMainApplied, Func<string, object> resolveOther, Func<bool> admitted, Action<IReadOnlyDictionary<string, object>> exposeDirectory, out Action verifySource, IReadOnlyList<GameObject>? producedRoots = null, int? expectedTweenCounter = null)
        {
            requireMainApplied();
            if (!admitted()) throw new InvalidOperationException("HUD presentation preparation lost post-main paused admission.");
            ObserveCold(keys, producedRoots);
            var directory = CanonicalHudPresentationDirectory.Build(keys, producedRoots);
            var meshes = CanonicalHudPresentationColdTemplate.FreshBorderMeshes(directory);
            var readOnly = CanonicalMarkerReadonlyCarry.MapKeyDirectory(directory);
            var actual = CaptureOriginalAssets(directory);
            object Resolve(string binding) {
                if (binding.StartsWith(MainNode, StringComparison.Ordinal)) {
                    if (!int.TryParse(binding.Substring(MainNode.Length), out var id)) throw new InvalidDataException("HUD presentation source main-node binding differs.");
                    return main.Node(id);
                }
                if (binding.StartsWith(Owner, StringComparison.Ordinal))
                    return directory.TryGetValue(binding.Substring(Owner.Length), out var value) ? value
                        : readOnly.TryGetValue(binding.Substring(Owner.Length), out var preserved) ? preserved
                        : meshes.TryGetValue(binding.Substring(Owner.Length), out var mesh) ? mesh
                        : throw new InvalidDataException("HUD presentation source original owner binding is absent.");
                return resolveOther(binding);
            }
            CanonicalHudPresentationCarry.Data? saved = null;
            var graph = codec.Decode(source);
            var plan = codec.PrepareOwnedGraph(graph, CanonicalHudPresentationCarry.Roots(actual), Resolve, view => {
                saved = new CanonicalHudPresentationCarry.Data();
                var sourceRoot = view.Root(CanonicalHudPresentationCarry.Root);
                foreach (var field in CanonicalCarryGraphCodec.Fields(typeof(CanonicalHudPresentationCarry.Data)))
                    field.SetValue(saved, view.Field(sourceRoot, field.DeclaringType!, field.Name));
                if (saved.OriginalAssetOrigins != template.AssetManifest)
                    throw new InvalidDataException("HUD presentation source original asset provenance or shared aliases differ.");
                CanonicalHudPresentationCarry.Roots(saved);
                if (expectedTweenCounter.HasValue && saved.TweenIdCount != expectedTweenCounter.Value)
                    throw new InvalidDataException("HUD and Soul original global Tween counter source values differ.");
                for (var owner = 0; owner < saved.NativeOwners.Length; owner++)
                    if (CanonicalHudPresentationDirectory.IsBorderMember(saved.NativeOwners[owner])
                        ? saved.Lifecycle[owner * 5] != 1 || directory[saved.NativeOwners[owner]] is ChangeFontByLanguage && saved.Lifecycle[owner * 5 + 1] != 0 || saved.Lifecycle[owner * 5 + 2] != 1 || saved.Lifecycle[owner * 5 + 3] != 0
                        : saved.Lifecycle[owner * 5] != 0 || saved.Lifecycle[owner * 5 + 1] != 0 || saved.Lifecycle[owner * 5 + 3] != 0 || saved.Lifecycle[owner * 5 + 4] != 0)
                        throw new InvalidDataException("HUD presentation source packet predates the common real cold Canon.");
                var final = directory.ToDictionary(pair => pair.Key, pair => (object)pair.Value, StringComparer.Ordinal);
                foreach (var pair in readOnly) final.Add(pair.Key, pair.Value);
                foreach (var pair in meshes) final.Add(pair.Key, pair.Value);
                for (var owner = 0; owner < saved.NativeOwners.Length; owner++) {
                    var native = directory[saved.NativeOwners[owner]];
                    if (!(native is TMPro.TextMeshPro)) continue;
                    var fields = CanonicalHudPresentationCarry.ManagedFields(native.GetType());
                    var index = Array.FindIndex(fields, field => field == CanonicalMarkerEventCarry.EventField);
                    if (index < 0 || saved.FieldValues[owner][index] == null) throw new InvalidDataException("HUD presentation prepared empty event is absent.");
                    final.Add(saved.NativeOwners[owner] + "/event/m_OnCullStateChanged", saved.FieldValues[owner][index]!);
                }
                for (var owner = 0; owner < saved.NativeOwners.Length; owner++) {
                    if (!(directory[saved.NativeOwners[owner]] is PlayMakerFSM)) continue;
                    var fields = CanonicalHudPresentationCarry.ManagedFields(typeof(PlayMakerFSM));
                    var index = Array.FindIndex(fields, field => field.Name == "fsm");
                    if (index < 0 || !(saved.FieldValues[owner][index] is HutongGames.PlayMaker.Fsm raw))
                        throw new InvalidDataException("HUD source FSM field is absent.");
                    foreach (var pair in LoadingGeoIncomingReferences.HudFsmMembers(raw, saved.NativeOwners[owner] + "/fsm"))
                        final.Add(pair.Key, pair.Value);
                }
                exposeDirectory(final);
            });
            void Native() {
                if (!admitted() || saved == null) throw new InvalidOperationException("HUD presentation native source snapshot admission is absent.");
                ObserveCold(keys);
                var actualDirectory = CanonicalHudPresentationDirectory.Build(keys);
                if (!directory.Keys.SequenceEqual(actualDirectory.Keys) || directory.Any(pair => !ReferenceEquals(pair.Value, actualDirectory[pair.Key])))
                    throw new InvalidOperationException("HUD presentation actual source owners changed during import.");
                var current = CanonicalHudPresentationCarry.Capture(directory);
                if (!saved.Registered.SequenceEqual(current.Registered)) throw new InvalidOperationException("HUD native FSM registration differs.");
                if (!saved.NativeOwners.SequenceEqual(current.NativeOwners) || !saved.Lifecycle.SequenceEqual(current.Lifecycle)
                    || !saved.GameObjectOwners.SequenceEqual(current.GameObjectOwners) || !saved.ActiveSelf.SequenceEqual(current.ActiveSelf))
                    throw new InvalidOperationException("HUD presentation native lifecycle requires unsupported original source continuation.");
            }
            void VerifySource() {
                Native(); plan.VerifyReadback(); requireMainApplied();
                if (iTweenFSMEvents.itweenIDCount != saved!.TweenIdCount) throw new InvalidOperationException("HUD source original tween counter readback differs.");
                var readOnlyNow = CanonicalMarkerReadonlyCarry.CaptureMapKey(directory);
                var expectedReadOnly = saved!.Readonly;
                if (!expectedReadOnly.Owners.SequenceEqual(readOnlyNow.Owners) || !expectedReadOnly.Aliases.SequenceEqual(readOnlyNow.Aliases)
                    || !expectedReadOnly.ContainerIsSelf.SequenceEqual(readOnlyNow.ContainerIsSelf)
                    || Enumerable.Range(0, 9).Any(owner => Enumerable.Range(0, 10).Any(index => !codec.SameRawSlot(expectedReadOnly.Powers[owner][index], readOnlyNow.Powers[owner][index], typeof(float), 0)))
                    || Enumerable.Range(0, 9).Any(owner => Enumerable.Range(0, 4).Any(index => !codec.SameRawSlot(expectedReadOnly.Corners[owner][index], readOnlyNow.Corners[owner][index], typeof(Vector3), 0))))
                    throw new InvalidOperationException("HUD presentation final readonly configuration, corner bits, runner or alias readback differs.");
                var currentOwners = directory.Where(pair => pair.Value is MonoBehaviour).ToArray();
                for (var owner = 0; owner < currentOwners.Length; owner++) {
                    var managedFields = CanonicalHudPresentationCarry.ManagedFields(currentOwners[owner].Value.GetType());
                    for (var field = 0; field < managedFields.Length; field++)
                        if (!codec.SameRawSlot(saved!.FieldValues[owner][field], managedFields[field].GetValue(currentOwners[owner].Value), managedFields[field].FieldType, 0))
                            throw new InvalidOperationException("HUD presentation final source field readback differs.");
                }
            }
            verifySource = VerifySource;
            var consumed = false;
            return () => {
                if (consumed || !admitted() || saved == null) throw new InvalidOperationException("HUD presentation source application lost single-use admission.");
                consumed = true;
                requireMainApplied(); Native(); plan.Apply(); plan.VerifyReadback();
                CanonicalHudPresentationCarry.PrepareManaged(saved, keys, codec, admitted)();
                VerifySource();
            };
        }
    }
}
