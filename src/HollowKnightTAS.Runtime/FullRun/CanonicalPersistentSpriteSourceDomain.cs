using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed class CanonicalPersistentSpriteSourceDomain
    {
        private const string MainNode = "PersistentSprites/MainNode/", MeshOwner = "PersistentSprites/Mesh/";
        private readonly CanonicalCarryGraphCodec codec = CanonicalCarryFieldRegistry.Create(persistentSpriteDomain: true);
        internal CanonicalCarryGraphData DecodeForRootWitness(byte[] payload) => codec.Decode(payload);
        internal string ValidatePayload(byte[] payload)
        {
            var graph = codec.Decode(payload);
            if (graph.Version != 1 || graph.Schema != codec.SchemaIdentity || graph.Unsupported.Length != 0
                || !graph.Roots.Select(root => root.Binding).SequenceEqual(CanonicalPersistentSpriteCarry.Roots(new CanonicalPersistentSpriteCarry.Data()).Keys))
                throw new InvalidDataException("Persistent sprite source graph schema, root or unsupported values differ.");
            var root = graph.Nodes[graph.Roots.Single(item => item.Binding == CanonicalPersistentSpriteCarry.Root).Node - 1];
            var continuationExternals = CanonicalPersistentSpriteContinuation.ExternalDirectory();
            CanonicalBorderArrowCarry.PreflightNative(graph);
            CanonicalBorderArrowCarry.OriginalMaterial();
            CanonicalFinalVisualCarry.Preflight(graph);
            foreach (var external in graph.Externals.Where(item => item.Binding.StartsWith("PersistentAnimator/", StringComparison.Ordinal)))
            {
                if (!continuationExternals.TryGetValue(external.Binding, out var target) || CanonicalCarryGraphCodec.Identity(target.GetType()) != external.Type)
                    throw new InvalidDataException("Persistent animator original clip/callback preflight differs: " + external.Binding);
            }
            CanonicalCarryGraphData.Node ArrayNode(CanonicalCarryGraphData.Value value, Type type, int count) {
                if (value.Kind != "node" || value.Reference < 1 || value.Reference > graph.Nodes.Length)
                    throw new InvalidDataException("Persistent sprite source preflight reference differs.");
                var node = graph.Nodes[value.Reference - 1];
                if (node.Kind != "array" || node.Type != CanonicalCarryGraphCodec.Identity(type) || node.Items == null || node.Items.Length != count)
                    throw new InvalidDataException("Persistent sprite source preflight array differs.");
                return node;
            }
            CanonicalCarryGraphData.Value Field(string name) => root.Fields!.Single(field => field.Name == CanonicalCarryGraphCodec.Identity(typeof(CanonicalPersistentSpriteCarry.Data)) + "/" + name).Value;
            string[] Strings(string name, int count) => ArrayNode(Field(name), typeof(string[]), count).Items!.Select(item =>
                item.Kind == "scalar" && item.Type == CanonicalCarryGraphCodec.Identity(typeof(string)) && item.Text != null
                    ? item.Text : throw new InvalidDataException("Persistent sprite source preflight string differs.")).ToArray();
            var owners = CanonicalPersistentSpriteDirectory.Resolve(); var fields = CanonicalPersistentSpriteCarry.Fields();
            var currentRoots = CanonicalPersistentSpriteCarry.Roots(new CanonicalPersistentSpriteCarry.Data());
            foreach (var entry in graph.Roots)
                if (entry.Node < 1 || entry.Node > graph.Nodes.Length || graph.Nodes[entry.Node - 1].Type != CanonicalCarryGraphCodec.Identity(currentRoots[entry.Binding].GetType()))
                    throw new InvalidDataException("Persistent health original root type differs before producer.");
            var continuationReference = Field("Continuation");
            if (continuationReference.Kind != "node" || continuationReference.Reference < 1 || continuationReference.Reference > graph.Nodes.Length)
                throw new InvalidDataException("Persistent animator continuation reference differs.");
            var continuation = graph.Nodes[continuationReference.Reference - 1];
            if (continuation.Type != CanonicalCarryGraphCodec.Identity(typeof(CanonicalPersistentSpriteContinuation.Data)))
                throw new InvalidDataException("Persistent animator continuation type differs.");
            CanonicalCarryGraphData.Value ContinuationField(string name) => continuation.Fields!.Single(field => field.Name == CanonicalCarryGraphCodec.Identity(typeof(CanonicalPersistentSpriteContinuation.Data)) + "/" + name).Value;
            var currentContinuation = CanonicalPersistentSpriteContinuation.Capture();
            var global = ContinuationField("GlobalState");
            if (global.Kind != "scalar" || global.Type != CanonicalCarryGraphCodec.Identity(typeof(int)) || global.Text != currentContinuation.GlobalState.ToString(CultureInfo.InvariantCulture))
                throw new InvalidDataException("Persistent animator global control differs before producer.");
            var nativeFlags = ArrayNode(ContinuationField("Lifecycle"), typeof(byte[][]), CanonicalPersistentSpriteContinuation.NativeOwnerCount).Items!;
            for (var index = 0; index < CanonicalPersistentSpriteContinuation.NativeOwnerCount; index++) {
                var flags = ArrayNode(nativeFlags[index], typeof(byte[]), 5).Items!;
                if (flags.Where((value, slot) => value.Kind != "scalar" || value.Type != CanonicalCarryGraphCodec.Identity(typeof(byte))
                    || value.Text != currentContinuation.Lifecycle[index][slot].ToString(CultureInfo.InvariantCulture)).Any())
                    throw new InvalidDataException("Persistent animator/health native lifecycle differs before producer.");
            }
            if (!Strings("Owners", CanonicalPersistentSpriteDirectory.OwnerCount).SequenceEqual(owners.Keys) || !Strings("Fields", fields.Length).SequenceEqual(fields.Select(field => field.DeclaringType!.FullName + "/" + field.Name + "=" + field.FieldType.FullName)))
                throw new InvalidDataException("Persistent sprite source preflight owner or original field ABI differs.");
            var meshes = ArrayNode(Field("Meshes"), typeof(byte[][]), CanonicalPersistentSpriteDirectory.OwnerCount).Items!;
            var current = owners.Values.ToArray();
            for (var i = 0; i < meshes.Length; i++) {
                var item = meshes[i];
                if (item.Kind != "node" || item.Reference < 1 || item.Reference > graph.Nodes.Length)
                    throw new InvalidDataException("Persistent sprite source mesh preflight reference differs.");
                var bytes = graph.Nodes[item.Reference - 1];
                if (bytes.Kind != "array" || bytes.Type != CanonicalCarryGraphCodec.Identity(typeof(byte[])) || bytes.Items == null || bytes.Items.Length > 16 * 1024 * 1024)
                    throw new InvalidDataException("Persistent sprite source mesh preflight byte array differs.");
                var raw = bytes.Items.Select(value => value.Kind == "scalar" && value.Type == CanonicalCarryGraphCodec.Identity(typeof(byte))
                    && byte.TryParse(value.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed.ToString(CultureInfo.InvariantCulture) == value.Text
                    ? parsed : throw new InvalidDataException("Persistent sprite source mesh preflight byte differs.")).ToArray();
                CanonicalPersistentSpriteMeshSnapshot.Decode(raw).RequireTargetLayout(current[i].GetComponent<MeshFilter>().sharedMesh);
            }
            return HollowKnightTAS.Core.Cryptography.Sha256Utility.ComputeHex(payload);
        }
        internal byte[] Capture(IReadOnlyDictionary<object, int> mainNodes, Func<object, string?> bindOther, Action<CanonicalCarryGraphData> auditAssets)
        {
            var data = CanonicalPersistentSpriteCarry.Capture();
            var owners = CanonicalPersistentSpriteDirectory.Resolve();
            if (CanonicalPersistentSpriteContinuation.HealthRoots().Values.Any(mainNodes.ContainsKey))
                throw new InvalidDataException("Health original root is already owned by the main graph.");
            var continuationExternals = CanonicalPersistentSpriteContinuation.ExternalDirectory();
            var arrowMaterial = CanonicalBorderArrowCarry.OriginalMaterial();
            var finalVisualExternals = CanonicalFinalVisualCarry.Externals();
            var glyphs = new CanonicalFinalVisualGlyphs();
            string? Bind(object value) {
                var glyph = glyphs.Bind(value); if (glyph != null) return glyph;
                foreach (var pair in finalVisualExternals) if (ReferenceEquals(pair.Value, value)) return pair.Key;
                if (ReferenceEquals(value, arrowMaterial)) return CanonicalBorderArrowCarry.MaterialBinding;
                foreach (var pair in continuationExternals) if (ReferenceEquals(pair.Value, value)) return pair.Key;
                if (mainNodes.TryGetValue(value, out var id)) return MainNode + id;
                foreach (var pair in owners) if (ReferenceEquals(pair.Value.GetComponent<MeshFilter>().sharedMesh, value)) return MeshOwner + pair.Key;
                return bindOther(value);
            }
            var graph = codec.Capture(CanonicalPersistentSpriteCarry.Roots(data), Bind);
            if (graph.Unsupported.Length != 0) throw new InvalidDataException("Persistent sprite source graph has unsupported fields: " + string.Join(";", graph.Unsupported));
            auditAssets(graph); var encoded = codec.Encode(graph);
            Modding.Logger.Log("[HKTAS] Actual D persistent sprite domain: nodes=" + graph.Nodes.Length + ";bytes=" + encoded.Length
                + ";mainBindings=" + graph.Externals.Count(item => item.Binding.StartsWith(MainNode, StringComparison.Ordinal)));
            return encoded;
        }
        // Incoming proof must be built from the existing full managed walk and
        // native-consumer inventory, then checked both before and after writes.
        internal Action Prepare(byte[] source, CanonicalCarryGraphCodec.PreparedView main, Func<string, object> resolveOther,
            Func<bool> admitted, Action requireMain, Action requireIncoming, out Action verify)
        {
            requireMain(); requireIncoming();
            if (!admitted()) throw new InvalidOperationException("Persistent sprite target is not admitted.");
            var owners = CanonicalPersistentSpriteDirectory.Resolve(); var actual = CanonicalPersistentSpriteCarry.Capture();
            var fields = CanonicalPersistentSpriteCarry.Fields();
            var continuationExternals = CanonicalPersistentSpriteContinuation.ExternalDirectory();
            var finalVisualExternals = CanonicalFinalVisualCarry.Externals();
            var glyphs = new CanonicalFinalVisualGlyphs();
            object Resolve(string binding) {
                if (binding.StartsWith(CanonicalFinalVisualGlyphs.Prefix, StringComparison.Ordinal)) return glyphs.Resolve(binding);
                if (binding.StartsWith(CanonicalFinalVisualCarry.Binding, StringComparison.Ordinal)) return finalVisualExternals.TryGetValue(binding, out var final)
                    ? final : throw new InvalidDataException("Final visual original external alias absent.");
                if (binding == CanonicalBorderArrowCarry.MaterialBinding) return CanonicalBorderArrowCarry.OriginalMaterial();
                if (binding.StartsWith("PersistentAnimator/", StringComparison.Ordinal)) return continuationExternals.TryGetValue(binding, out var value)
                    ? value : throw new InvalidDataException("Persistent animator original external identity differs.");
                if (binding.StartsWith(MainNode, StringComparison.Ordinal)) {
                    if (!int.TryParse(binding.Substring(MainNode.Length), out var id)) throw new InvalidDataException("Persistent sprite main node binding differs.");
                    return main.Node(id);
                }
                if (binding.StartsWith(MeshOwner, StringComparison.Ordinal))
                    return owners.TryGetValue(binding.Substring(MeshOwner.Length), out var owner) ? owner.GetComponent<MeshFilter>().sharedMesh
                        : throw new InvalidDataException("Persistent sprite mesh binding differs.");
                return resolveOther(binding);
            }
            CanonicalPersistentSpriteCarry.Data? saved = null;
            var graph = codec.Decode(source);
            var plan = codec.PrepareOwnedGraph(graph, CanonicalPersistentSpriteCarry.Roots(actual), Resolve, view => {
                var root = view.Root(CanonicalPersistentSpriteCarry.Root); saved = new CanonicalPersistentSpriteCarry.Data();
                foreach (var field in CanonicalCarryGraphCodec.Fields(typeof(CanonicalPersistentSpriteCarry.Data)))
                    field.SetValue(saved, view.Field(root, field.DeclaringType!, field.Name));
            });
            if (saved == null || !saved.Owners.SequenceEqual(actual.Owners) || !saved.Fields.SequenceEqual(actual.Fields)
                || saved.Values.Length != CanonicalPersistentSpriteDirectory.OwnerCount || saved.Meshes.Length != CanonicalPersistentSpriteDirectory.OwnerCount || saved.Values.Any(row => row == null || row.Length != fields.Length))
                throw new InvalidDataException("Persistent sprite source owner or field directory differs.");
            var applyContinuation = CanonicalPersistentSpriteContinuation.Prepare(saved.Continuation, actual.Continuation, codec, out var verifyContinuation);
            var applyFinalVisual = CanonicalFinalVisualCarry.Prepare(saved.FinalVisual, actual.FinalVisual, codec, admitted, out var verifyFinalVisual);
            var snapshots = saved.Meshes.Select(CanonicalPersistentSpriteMeshSnapshot.Decode).ToArray();
            var currentOwners = owners.Values.ToArray();
            for (var i = 0; i < CanonicalPersistentSpriteDirectory.OwnerCount; i++) {
                if (!ReferenceEquals(saved.Values[i][0], fields[0].GetValue(currentOwners[i])))
                    throw new InvalidDataException("Persistent sprite source mesh alias differs.");
                snapshots[i].RequireTargetLayout((Mesh)fields[0].GetValue(currentOwners[i]));
                for (var f = 0; f < fields.Length; f++) {
                    var value = saved.Values[i][f];
                    if (value != null && !fields[f].FieldType.IsInstanceOfType(value)) throw new InvalidDataException("Persistent sprite source field type differs.");
                    if (fields[f].IsInitOnly && !ReferenceEquals(value, fields[f].GetValue(currentOwners[i]))) throw new InvalidDataException("Persistent sprite readonly field differs.");
                }
            }
            void Verify() {
                requireMain(); requireIncoming(); plan.VerifyReadback(); verifyContinuation(); verifyFinalVisual();
                var current = CanonicalPersistentSpriteDirectory.Resolve();
                if (!current.Keys.SequenceEqual(owners.Keys) || current.Where((pair, index) => !ReferenceEquals(pair.Value, currentOwners[index])).Any())
                    throw new InvalidOperationException("Persistent sprite owner identity changed.");
                for (var i = 0; i < CanonicalPersistentSpriteDirectory.OwnerCount; i++) {
                    for (var f = 0; f < fields.Length; f++)
                        if (!codec.SameRawSlot(saved.Values[i][f], fields[f].GetValue(currentOwners[i]), fields[f].FieldType, 0))
                            throw new InvalidOperationException("Persistent sprite managed source readback differs.");
                    snapshots[i].RequireReadback((Mesh)fields[0].GetValue(currentOwners[i]));
                }
            }
            verify = Verify; var used = false;
            return () => {
                if (used || !admitted()) throw new InvalidOperationException("Persistent sprite application lost single-use admission.");
                used = true; requireMain(); requireIncoming(); plan.Apply(); plan.VerifyReadback();
                applyContinuation();
                applyFinalVisual();
                for (var i = 0; i < CanonicalPersistentSpriteDirectory.OwnerCount; i++) {
                    for (var f = 1; f < fields.Length; f++) if (!fields[f].IsInitOnly) fields[f].SetValue(currentOwners[i], saved.Values[i][f]);
                    snapshots[i].Apply((Mesh)fields[0].GetValue(currentOwners[i]), admitted);
                }
                CanonicalEventDataCarry.PrepareSourceReapply(main, admitted)();
                Verify();
            };
        }
    }
}
