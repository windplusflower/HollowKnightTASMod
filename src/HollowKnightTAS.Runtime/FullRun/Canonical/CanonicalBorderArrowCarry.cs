using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using HutongGames.PlayMaker;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalBorderArrowCarry
    {
        internal const string OwnerKey = "HudCamera/Inventory/Border/Arrow Left/Arrow Left";
        internal const string FsmRoot = "R2/PersistentSprites/arrow-left", RightFsmRoot = "R2/PersistentSprites/arrow-right";
        internal const int OwnerCount = 8;
        internal static readonly string[] OwnerKeys = { OwnerKey, "HudCamera/Inventory/Border/Arrow Right/Arrow Right", "HudCamera/Inventory/Border/Inv_Border_Bottom",
            "HudCamera/Inventory/Border/Inv_Border_Corner (1)", "HudCamera/Inventory/Border/Inv_Border_Corner (2)", "HudCamera/Inventory/Border/Inv_Border_Corner TL",
            "HudCamera/Inventory/Border/Inv_Border_Corner TR", "HudCamera/Inventory/Border/Inv_Border_Top" };
        internal const string MaterialBinding = "PersistentSprites/ArrowLeft/original-material/resources.assets/20966/materials[0]/38/v1";
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal sealed class Data
        {
            public string[] Fields = Array.Empty<string>();
            public object?[] Values = Array.Empty<object?>(), Materials = Array.Empty<object?>();
            public string Owner = "", NativeSignature = "";
        }
        internal static tk2dSprite Owner(string key = OwnerKey) {
            if (OwnerKeys.Length != OwnerCount || !OwnerKeys.Contains(key, StringComparer.Ordinal)) throw new InvalidDataException("Unknown original Border sprite owner.");
            var owner = CanonicalPersistentSpriteDirectory.Resolve()[key];
            var expected = new[] { typeof(Transform), typeof(MeshRenderer), typeof(MeshFilter), typeof(tk2dSprite), typeof(tk2dSpriteAnimator), typeof(InvAnimateUpAndDown) }
                .Concat(OwnerKeys.Take(2).Contains(key) ? new[] { typeof(PlayMakerFSM) } : Array.Empty<Type>()).ToArray();
            var parts = owner.GetComponents<Component>();
            if (expected.Any(type => parts.Count(part => part != null && part.GetType() == type) != 1)
                || parts.Any(part => part == null || !expected.Contains(part.GetType()) && part.GetType() != typeof(iTween) && part.GetType() != typeof(iTweenFSMEvents)))
                throw new InvalidDataException("Original fixed Border component directory differs: " + key);
            return owner;
        }
        internal static InvAnimateUpAndDown Animation(string key = OwnerKey)
        {
            var values = Owner(key).GetComponents<InvAnimateUpAndDown>();
            if (values.Length != 1 || values[0].GetType() != typeof(InvAnimateUpAndDown)) throw new InvalidDataException("Original Arrow Left animation owner differs.");
            return values[0];
        }
        internal static PlayMakerFSM FsmOwner(string key = OwnerKey)
        {
            var values = Owner(key).GetComponents<PlayMakerFSM>();
            if (values.Length != 1 || ((Fsm)typeof(PlayMakerFSM).GetField("fsm", Flags)!.GetValue(values[0])).Name != "Arrow Anim") throw new InvalidDataException("Original Arrow Left FSM owner differs.");
            return values[0];
        }
        internal static FieldInfo[] Fields()
        {
            var type = typeof(InvAnimateUpAndDown); var fields = type.GetFields(Flags | BindingFlags.DeclaredOnly);
            var names = new[] { "upAnimation", "downAnimation", "upDelay", "randomStartFrameSpriteMax", "spriteAnimator", "meshRenderer", "timer", "animatingDown", "readyingAnimUp" };
            var types = new[] { typeof(string), typeof(string), typeof(float), typeof(int), typeof(tk2dSpriteAnimator), typeof(MeshRenderer), typeof(float), typeof(bool), typeof(bool) };
            if (type.Module.ModuleVersionId.ToString("D") != "2b54cdaf-7a93-4f0e-b297-d50c4090ea9f" || fields.Length != 9
                || fields.Where((field, index) => field.Name != names[index] || field.FieldType != types[index]).Any()) throw new InvalidDataException("Original Arrow Left complete animation ABI differs.");
            return fields;
        }
        internal static SortedDictionary<string, object> Roots()
        {
            var roots = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var ownerKey in OwnerKeys.Take(2)) {
            var rootKey = ownerKey == OwnerKey ? FsmRoot : RightFsmRoot;
            var raw = (Fsm)typeof(PlayMakerFSM).GetField("fsm", Flags)!.GetValue(FsmOwner(ownerKey));
            var states = (FsmState[])typeof(Fsm).GetField("states", Flags)!.GetValue(raw);
            var expected = new[] { "Init|HutongGames.PlayMaker.Actions.SetPosition", "Anim Up|HutongGames.PlayMaker.Actions.iTweenMoveBy", "Anim Back|HutongGames.PlayMaker.Actions.iTweenMoveBy" };
            roots.Add(rootKey, raw);
            if (states.Length != 3) throw new InvalidDataException("Original Arrow Left state directory differs.");
            for (var i = 0; i < states.Length; i++) {
                var actions = (FsmStateAction[])typeof(FsmState).GetField("actions", Flags)!.GetValue(states[i]);
                if (states[i].Name + "|" + string.Join(",", actions.Select(action => action.GetType().FullName)) != expected[i]) throw new InvalidDataException("Original Arrow Left complete action directory differs.");
                var key = rootKey + "/state[" + i + "]"; roots.Add(key, states[i]); roots.Add(key + "/action[0]", actions[0]);
            }
            }
            return roots;
        }
        private static string Bits(float value) => BitConverter.ToUInt32(BitConverter.GetBytes(value), 0).ToString("x8");
        internal static string NativeSignature(string key = OwnerKey)
        {
            var owner = Owner(key); var inventory = GameCameras.instance.hudCamera.transform.Find("Inventory");
            if (inventory == null) throw new InvalidDataException("Original Arrow Left Inventory owner is absent.");
            if (owner.GetComponents<iTween>().Length != 0) throw new InvalidDataException("Arrow Left has an unsupported live original Tween.");
            if (owner.GetComponents<iTweenFSMEvents>().Length != 0) throw new InvalidDataException("Arrow Left has an unsupported original Tween callback.");
            var t = owner.transform; var p = t.localPosition; var q = t.localRotation; var s = t.localScale;
            var parentColliders = t.parent.GetComponents<BoxCollider2D>();
            if (parentColliders.Length != (OwnerKeys.Take(2).Contains(key) ? 1 : 0) || owner.GetComponents<Collider2D>().Length != 0 || owner.GetComponents<Collider>().Length != 0)
                throw new InvalidDataException("Original Arrow Left parent/body collider ownership differs.");
            return owner.gameObject.activeSelf + ":" + owner.gameObject.activeInHierarchy + ":" + inventory.gameObject.activeSelf + ":" + inventory.gameObject.activeInHierarchy
                + ":" + owner.GetComponent<MeshRenderer>().enabled + ":" + string.Join("|", new[] { p.x,p.y,p.z,q.x,q.y,q.z,q.w,s.x,s.y,s.z }.Select(Bits));
        }
        internal static Data Capture(string key = OwnerKey)
        {
            OriginalMaterial(key);
            var owner = Animation(key); var fields = Fields(); var values = fields.Select(field => field.GetValue(owner)).ToArray();
            if (!ReferenceEquals(values[4], Owner(key).GetComponent<tk2dSpriteAnimator>()) || !ReferenceEquals(values[5], Owner(key).GetComponent<MeshRenderer>()))
                throw new InvalidDataException("Original Arrow Left Awake reference aliases differ.");
            return new Data { Owner = key, Fields = fields.Select(field => field.Name + "=" + field.FieldType.FullName).ToArray(), Values = values,
                Materials = Owner(key).GetComponent<MeshRenderer>().sharedMaterials.Cast<object?>().ToArray(), NativeSignature = NativeSignature(key) };
        }
        internal static Material OriginalMaterial(string key = OwnerKey)
        {
            var sprite = Owner(key); var type = typeof(tk2dBaseSprite);
            var original = type.GetField("collection", Flags)!.GetValue(sprite) as tk2dSpriteCollectionData;
            var collection = type.GetField("collectionInst", Flags)!.GetValue(sprite) as tk2dSpriteCollectionData;
            if (original == null || collection == null || collection.materials == null || collection.materials.Length != 1 || collection.materials[0] == null
                || collection.spriteDefinitions == null || collection.spriteDefinitions.Length != 51)
                throw new InvalidDataException("Arrow Left original resources20966 collection/material layout differs.");
            if (key != OwnerKey && !ReferenceEquals(original, type.GetField("collection", Flags)!.GetValue(Owner())))
                throw new InvalidDataException("Original Border owners no longer share their one original resources20966 collection identity.");
            var material = collection.materials[0]; var definitions = collection.spriteDefinitions;
            var directory = string.Join("\n", definitions.Select(definition => definition == null ? "null" : definition.name + "|" + definition.materialId));
            var directoryHash = HollowKnightTAS.Core.Cryptography.Sha256Utility.ComputeHex(System.Text.Encoding.UTF8.GetBytes(directory));
            var textures = typeof(tk2dSpriteCollectionData).GetField("textures", Flags)!.GetValue(collection) as Texture[];
            var current = sprite.GetComponent<MeshRenderer>().sharedMaterials;
            if (current.Length != 1 || material.shader == null || textures == null || textures.Length != 1 || textures[0] == null)
                throw new InvalidDataException("Arrow Left original renderer/material38/shader472/texture223 alias chain differs.");
            RequireMaterialProvenance(original, collection, collection.spriteCollectionGUID, collection.dataGuid, collection.spriteCollectionName,
                collection.needMaterialInstance, directoryHash, material, current[0], definitions.Select(definition => (object?)definition?.material).ToArray(),
                definitions.Select(definition => definition?.materialId ?? -1).ToArray(), material.shader.name, textures[0], material.GetTexture("_MainTex"));
            return material;
        }
        internal static void RequireMaterialProvenance(object original, object collection, string guid, string dataGuid, string name,
            bool needsInstance, string directoryHash, object material, object rendererMaterial, object?[] definitionMaterials, int[] materialIds,
            string shader, object texture, object mainTexture)
        {
            if (!ReferenceEquals(original, collection) || guid != "0da62180aa569654fbb53b30e4cd76a3" || dataGuid != "abe5a82bf1d4fee43ad073dd01f384af"
                || name != "UI_inventory_animated" || needsInstance || directoryHash != "f4d4b2f35d0d68016003bd358c85f2ddd5e367662dc8b721596dcc95ee2ad352"
                || material == null || !ReferenceEquals(material, rendererMaterial) || definitionMaterials.Length != 51 || materialIds.Length != 51
                || definitionMaterials.Any(value => !ReferenceEquals(value, material)) || materialIds.Any(value => value != 0)
                || shader != "tk2d/BlendVertexColor" || texture == null || !ReferenceEquals(texture, mainTexture))
                throw new InvalidDataException("Arrow Left original collection/material/shader/texture provenance differs.");
        }
        private static CanonicalCarryGraphData.Node[] SourceData(CanonicalCarryGraphData graph)
        {
            CanonicalCarryGraphData.Node Node(CanonicalCarryGraphData.Value value) => value.Kind == "node" && value.Reference > 0 && value.Reference <= graph.Nodes.Length
                ? graph.Nodes[value.Reference - 1] : throw new InvalidDataException("Arrow Left source data reference differs.");
            CanonicalCarryGraphData.Value Field(CanonicalCarryGraphData.Node node, Type type, string name) => node.Fields!.Single(field => field.Name == CanonicalCarryGraphCodec.Identity(type) + "/" + name).Value;
            var root = graph.Nodes[graph.Roots.Single(item => item.Binding == CanonicalPersistentSpriteCarry.Root).Node - 1];
            var continuation = Node(Field(root, typeof(CanonicalPersistentSpriteCarry.Data), "Continuation"));
            var array = Node(Field(continuation, typeof(CanonicalPersistentSpriteContinuation.Data), "Arrows"));
            if (array.Type != CanonicalCarryGraphCodec.Identity(typeof(Data[])) || array.Items == null || array.Items.Length != OwnerCount) throw new InvalidDataException("Original eight-Border source array differs.");
            var rows = array.Items.Select(Node).ToArray();
            for (var i = 0; i < rows.Length; i++) {
                var identity = Field(rows[i], typeof(Data), "Owner");
                if (rows[i].Type != CanonicalCarryGraphCodec.Identity(typeof(Data)) || identity.Kind != "scalar" || identity.Type != CanonicalCarryGraphCodec.Identity(typeof(string)) || identity.Text != OwnerKeys[i]) throw new InvalidDataException("Original fixed Border owner source directory differs.");
            }
            return rows;
        }
        internal static void PreflightNative(CanonicalCarryGraphData graph)
        {
            var rows = SourceData(graph);
            for (var i = 0; i < rows.Length; i++) { var data = rows[i];
            var signature = data.Fields!.Single(field => field.Name == CanonicalCarryGraphCodec.Identity(typeof(Data)) + "/NativeSignature").Value;
            if (signature.Kind != "scalar" || signature.Type != CanonicalCarryGraphCodec.Identity(typeof(string)) || signature.Text != NativeSignature(OwnerKeys[i]))
                throw new InvalidDataException("Border source renderer/pose differs before producer: " + OwnerKeys[i]);
            }
        }
        internal static void PreflightMaterials(CanonicalCarryGraphData graph, Func<string, object> resolveOriginalAsset)
        {
            var rows = SourceData(graph);
            for (var owner = 0; owner < rows.Length; owner++) { var key = OwnerKeys[owner]; var data = rows[owner]; var reference = data.Fields!.Single(field => field.Name == CanonicalCarryGraphCodec.Identity(typeof(Data)) + "/Materials").Value;
            if (reference.Kind != "node" || reference.Reference < 1 || reference.Reference > graph.Nodes.Length) throw new InvalidDataException("Arrow Left source material array reference differs.");
            var materials = graph.Nodes[reference.Reference - 1]; var current = Owner(key).GetComponent<MeshRenderer>().sharedMaterials;
            if (materials.Type != CanonicalCarryGraphCodec.Identity(typeof(object[])) || materials.Items == null || materials.Items.Length != current.Length || current.Length != 1)
                throw new InvalidDataException("Arrow Left original one-material directory differs.");
            for (var i = 0; i < current.Length; i++) {
                var value = materials.Items[i];
                if (value.Kind != "external" || value.Reference < 1 || value.Reference > graph.Externals.Length
                    || !ReferenceEquals(graph.Externals[value.Reference - 1].Binding == MaterialBinding ? OriginalMaterial(key)
                        : resolveOriginalAsset(graph.Externals[value.Reference - 1].Binding), current[i]))
                    throw new InvalidDataException("Border original material alias differs before producer: " + key);
            }
            }
        }
        internal static Action Prepare(Data source, Data current, CanonicalCarryGraphCodec codec, out Action verify)
        {
            if (source.Owner != current.Owner || !OwnerKeys.Contains(source.Owner, StringComparer.Ordinal) || !source.Fields.SequenceEqual(current.Fields) || source.Values.Length != 9 || source.NativeSignature != current.NativeSignature
                || source.Materials.Length != current.Materials.Length || source.Materials.Where((value, index) => !ReferenceEquals(value, current.Materials[index])).Any())
                throw new InvalidDataException("Arrow Left source native pose/renderer/material identity or ABI differs.");
            var owner = Animation(source.Owner); var fields = Fields();
            for (var i = 0; i < fields.Length; i++) if (source.Values[i] == null ? fields[i].FieldType.IsValueType : !fields[i].FieldType.IsInstanceOfType(source.Values[i])) throw new InvalidDataException("Arrow Left source field type differs.");
            if (!ReferenceEquals(source.Values[4], current.Values[4]) || !ReferenceEquals(source.Values[5], current.Values[5])) throw new InvalidDataException("Arrow Left original producer reference alias differs.");
            void Verify() {
                var now = Capture(source.Owner);
                OriginalMaterial(source.Owner);
                if (now.NativeSignature != source.NativeSignature || now.Materials.Length != source.Materials.Length || now.Materials.Where((value,index) => !ReferenceEquals(value,source.Materials[index])).Any()
                    || fields.Where((field,index) => !codec.SameRawSlot(source.Values[index], field.GetValue(owner), field.FieldType, 0)).Any()) throw new InvalidOperationException("Arrow Left complete original source readback differs.");
            }
            verify = Verify;
            return () => { for (var i = 0; i < fields.Length; i++) fields[i].SetValue(owner, source.Values[i]); Verify(); };
        }
    }
}
