using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using TMPro;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Shares the existing PersistentSprites graph/header and its main-node
    // bindings. Text meshes are derived from the complete source TMP buffers;
    // no additional raw Mesh stream or per-owner wire segment is introduced.
    internal static class CanonicalFinalVisualCarry
    {
        internal const string Binding = "PersistentSprites/FinalVisual/";
        internal const string Shadow = "Hero/Effects/Shadow Recharge";
        internal const string Schema = "three-original-TMP-223-and-two-submesh13:full-fields-aliases-readonly:original-font-glyph-slots:source-buffer-refresh:shadow-source-renderer:v2";
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static readonly Type[] DataTypes = { typeof(Data), typeof(int[]), typeof(string[]), typeof(object[][]), typeof(object[]) };
        internal static Type[] OriginalDataTypes()
        {
            var type = typeof(TMP_LinkInfo);
            var layout = string.Join(";", CanonicalCarryGraphCodec.Fields(type).Select(field => field.Name + "=" + field.FieldType.FullName));
            if (type.Module.ModuleVersionId.ToString("D") != CanonicalSceneMappingCarry.ModuleMvid || layout != "hashCode=System.Int32;linkID=System.Char[];linkIdFirstCharacterIndex=System.Int32;linkIdLength=System.Int32;linkTextLength=System.Int32;linkTextfirstCharacterIndex=System.Int32;textComponent=TMPro.TMP_Text")
                throw new InvalidDataException("Final visual original link-info seven-field ABI differs.");
            var entry = typeof(Dictionary<int, int>).GetField("entries", Flags)?.FieldType.GetElementType()
                ?? throw new InvalidDataException("Final visual original integer dictionary storage absent.");
            if (string.Join(";", CanonicalCarryGraphCodec.Fields(entry).Select(field => field.Name + "=" + field.FieldType.FullName))
                != "hashCode=System.Int32;key=System.Int32;next=System.Int32;value=System.Int32")
                throw new InvalidDataException("Final visual original dictionary entry ABI differs.");
            if (EqualityComparer<int>.Default.GetType().FullName != "System.Collections.Generic.GenericEqualityComparer`1[[System.Int32, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]"
                || CanonicalCarryGraphCodec.Fields(EqualityComparer<int>.Default.GetType()).Length != 0)
                throw new InvalidDataException("Final visual original integer comparer ABI differs.");
            return new[] { type, entry, entry.MakeArrayType() };
        }
        internal sealed class Data
        {
            public string Schema = CanonicalFinalVisualCarry.Schema;
            public string[] Owners = Array.Empty<string>(), Fields = Array.Empty<string>(), Native = Array.Empty<string>();
            public object?[][] Values = Array.Empty<object?[]>();
            public CanonicalMarkerReadonlyCarry.Data Readonly = new CanonicalMarkerReadonlyCarry.Data();
            public bool ShadowRenderer;
        }
        private static Transform Find(Transform parent, string path)
        {
            foreach (var name in path.Split('/')) {
                var matches = parent.Cast<Transform>().Where(child => child.name == name).ToArray();
                if (matches.Length != 1) throw new InvalidDataException("Final visual original owner missing/ambiguous: " + path);
                parent = matches[0];
            }
            return parent;
        }
        internal static tk2dSprite ShadowSprite()
        {
            var hero = HeroController.SilentInstance ?? throw new InvalidDataException("Final visual Hero absent.");
            var node = Find(hero.transform, "Effects/Shadow Recharge");
            var parts = node.GetComponents<Component>();
            var expected = new[] { typeof(Transform), typeof(MeshRenderer), typeof(MeshFilter), typeof(tk2dSprite), typeof(tk2dSpriteAnimator), typeof(PlayMakerFSM) };
            if (!parts.Select(part => part.GetType()).SequenceEqual(expected)) throw new InvalidDataException("Shadow Recharge original six-component layout differs.");
            return node.GetComponent<tk2dSprite>();
        }
        internal static SortedDictionary<string, UnityEngine.Object> Directory()
        {
            if (typeof(TextMeshPro).Module.ModuleVersionId.ToString("D") != CanonicalSceneMappingCarry.ModuleMvid)
                throw new InvalidDataException("Final visual original TMP module differs.");
            var hud = GameCameras.instance.hudCamera.transform;
            var paths = new[] { "Inventory/Charms/Equipped Charms/Notches/Text Notches", "Inventory/Charms/Equipped Charms/Text Overcharmed", "Prompts/First Map/Button/Label" };
            var result = new SortedDictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
            foreach (var path in paths) {
                var node = Find(hud, path); var texts = node.GetComponents<TextMeshPro>();
                if (texts.Length != 1 || texts[0].GetType() != typeof(TextMeshPro) || node.gameObject.activeInHierarchy)
                    throw new InvalidDataException("Final visual TMP owner is absent, foreign or active.");
                result.Add(path, texts[0]);
                var children = node.Cast<Transform>().SelectMany(child => child.GetComponents<TMP_SubMesh>()).ToArray();
                if (children.Length != (path == paths[2] ? 0 : 1)) throw new InvalidDataException("Final visual original TMP submesh directory differs.");
                foreach (var child in children) result.Add(path + "/submesh", child);
            }
            if (result.Count != 5) throw new InvalidDataException("Final visual directory size differs.");
            return result;
        }
        internal static FieldInfo[] Fields(Type type)
        {
            var fields = CanonicalCarryGraphCodec.Fields(type).Where(field => field.DeclaringType != typeof(UnityEngine.Object)
                && field.DeclaringType != typeof(MonoBehaviour)).ToArray();
            if (type != typeof(TextMeshPro) && type != typeof(TMP_SubMesh) || fields.Length != (type == typeof(TextMeshPro) ? 223 : 13)
                || fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length)
                throw new InvalidDataException("Final visual complete original managed ABI differs.");
            return fields;
        }
        private static string FieldName(FieldInfo field) => field.DeclaringType!.FullName + "/" + field.Name + "=" + field.FieldType.FullName;
        internal static Mesh Mesh(UnityEngine.Object owner) => (Mesh)(typeof(TMP_Text).IsInstanceOfType(owner)
            ? typeof(TMP_Text).GetField("m_mesh", Flags)!.GetValue(owner) : typeof(TMP_SubMesh).GetField("m_mesh", Flags)!.GetValue(owner))!;
        internal static IReadOnlyDictionary<string, object> Externals()
        {
            var directory = Directory();
            var result = new SortedDictionary<string, object>(StringComparer.Ordinal);
            result.Add(Binding + "original-default-int-comparer", EqualityComparer<int>.Default);
            // The two original inactive TMP_SubMesh owners retain their exact
            // destroyed fallback-material wrappers after the common HUD Canon.
            // These are existing S0 slot aliases, never newly made Unity ghosts.
            // Bind only that finite anchored alias set; unknown dead materials
            // still reach the ordinary destroyed-wrapper rejection.
            var retired = new Dictionary<object, List<string>>(new References());
            var pointer = typeof(UnityEngine.Object).GetField("m_CachedPtr", Flags)!;
            foreach (var pair in directory.Where(pair => pair.Value is TMP_SubMesh)) {
                foreach (var name in new[] { "m_sharedMaterial", "m_fallbackMaterial", "m_material" }) {
                    var value = typeof(TMP_SubMesh).GetField(name, Flags)!.GetValue(pair.Value) as Material;
                    if (ReferenceEquals(value, null) || value != null) continue;
                    if ((IntPtr)pointer.GetValue(value)! != IntPtr.Zero) throw new InvalidDataException("Final visual retired material has a live native pointer.");
                    if (!retired.TryGetValue(value!, out var sites)) retired.Add(value!, sites = new List<string>());
                    sites.Add(pair.Key + "/" + name);
                }
            }
            foreach (var pair in retired) {
                var sites = string.Join("|", pair.Value.OrderBy(value => value, StringComparer.Ordinal));
                result.Add(Binding + "original-retired-submesh-material/" + Uri.EscapeDataString(sites), pair.Key);
            }
            foreach (var pair in CanonicalMarkerReadonlyCarry.FinalVisualDirectory(directory)) result.Add(Binding + pair.Key, pair.Value);
            foreach (var pair in directory) {
                var mesh = Mesh(pair.Value);
                if (mesh == null) throw new InvalidDataException("Final visual original managed mesh absent.");
                result.Add(Binding + pair.Key + "/mesh", mesh);
            }
            return result;
        }
        private static string Native(UnityEngine.Object owner)
        {
            var component = (MonoBehaviour)owner; var t = component.transform;
            var renderer = component.GetComponent<MeshRenderer>(); var filter = component.GetComponent<MeshFilter>();
            if (renderer == null || filter == null || filter.sharedMesh != null || component.GetComponents<Collider>().Length != 0 || component.GetComponents<Collider2D>().Length != 0)
                throw new InvalidDataException("Final visual inactive original filter/collider ownership differs.");
            return component.enabled + ":" + component.gameObject.activeSelf + ":" + component.gameObject.activeInHierarchy + ":" + renderer.enabled + ":"
                + string.Join(",", BoundaryMonoLifecycle.ObserveUnstartedUi(component)) + ":"
                + string.Join(",", LoadingVisualMeshBatchAudit.PoseBits(t.localPosition, t.localRotation, t.localScale));
        }
        internal static Data Capture()
        {
            using var validation = LoadingEngineClockProbe.ReadOnlyCheckpointScope();
            var directory = Directory(); CanonicalMarkerEventCarry.RequireEmpty(directory);
            return new Data { Owners = directory.Keys.ToArray(), Fields = directory.SelectMany(pair => Fields(pair.Value.GetType()).Select(field => pair.Key + "/" + FieldName(field))).ToArray(),
                Values = directory.Values.Select(owner => Fields(owner.GetType()).Select(field => field.GetValue(owner)).ToArray()).ToArray(),
                Native = directory.Values.Select(Native).ToArray(), Readonly = CanonicalMarkerReadonlyCarry.CaptureFinalVisual(directory),
                ShadowRenderer = ShadowSprite().GetComponent<MeshRenderer>().enabled };
        }
        internal static void Preflight(CanonicalCarryGraphData graph)
        {
            using var validation = LoadingEngineClockProbe.ReadOnlyCheckpointScope();
            CanonicalCarryGraphData.Node Node(CanonicalCarryGraphData.Value value) => value.Kind == "node" && value.Reference > 0 && value.Reference <= graph.Nodes.Length
                ? graph.Nodes[value.Reference - 1] : throw new InvalidDataException("Final visual source reference differs.");
            CanonicalCarryGraphData.Value Field(CanonicalCarryGraphData.Node node, Type type, string name) => node.Fields!.Single(field => field.Name == CanonicalCarryGraphCodec.Identity(type) + "/" + name).Value;
            var root = graph.Nodes[graph.Roots.Single(item => item.Binding == CanonicalPersistentSpriteCarry.Root).Node - 1];
            var data = Node(Field(root, typeof(CanonicalPersistentSpriteCarry.Data), "FinalVisual"));
            if (data.Type != CanonicalCarryGraphCodec.Identity(typeof(Data)) || Field(data, typeof(Data), "Schema").Text != Schema)
                throw new InvalidDataException("Final visual source schema differs before producers.");
            string[] Strings(string name) {
                var array = Node(Field(data, typeof(Data), name));
                if (array.Type != CanonicalCarryGraphCodec.Identity(typeof(string[])) || array.Items == null || array.Items.Any(item => item.Kind != "scalar" || item.Type != CanonicalCarryGraphCodec.Identity(typeof(string)) || item.Text == null))
                    throw new InvalidDataException("Final visual source string directory differs.");
                return array.Items.Select(item => item.Text!).ToArray();
            }
            var directory = Directory();
            if (!Strings("Owners").SequenceEqual(directory.Keys)
                || !Strings("Fields").SequenceEqual(directory.SelectMany(pair => Fields(pair.Value.GetType()).Select(field => pair.Key + "/" + FieldName(field))))
                || !Strings("Native").SequenceEqual(directory.Values.Select(Native)))
                throw new InvalidDataException("Final visual source ownership/ABI/native flags differ before producers.");
            var externals = Externals();
            var glyphs = new CanonicalFinalVisualGlyphs();
            foreach (var external in graph.Externals.Where(item => item.Binding.StartsWith(Binding, StringComparison.Ordinal)))
                if (CanonicalCarryGraphCodec.Identity(external.Binding.StartsWith(CanonicalFinalVisualGlyphs.Prefix, StringComparison.Ordinal)
                    ? glyphs.Resolve(external.Binding).GetType() : externals.TryGetValue(external.Binding, out var value) ? value.GetType()
                        : throw new InvalidDataException("Final visual source original external absent.")) != external.Type)
                    throw new InvalidDataException("Final visual source readonly/mesh binding differs before producers.");
            CanonicalPersistentSpriteNativeGuard.ProtectFinalVisual(directory.Values.Select(Mesh).ToArray());
        }
        internal static IReadOnlyDictionary<object, string> CopiedMembers()
        {
            var externals = Externals(); var copied = new Dictionary<object, string>(new References());
            var glyphs = new CanonicalFinalVisualGlyphs();
            var codec = CanonicalCarryFieldRegistry.Create(persistentSpriteDomain: true);
            string? Bind(object value) {
                var glyph = glyphs.Bind(value); if (glyph != null) return glyph;
                foreach (var pair in externals) if (ReferenceEquals(pair.Value, value)) {
                    if (value is Mesh) copied[value] = pair.Key;
                    return pair.Key;
                }
                if (value is UnityEngine.Object native) return "local-only-final-visual/" + native.GetInstanceID();
                return null;
            }
            var graph = codec.Capture(new SortedDictionary<string, object>(StringComparer.Ordinal) { [Binding + "data"] = Capture() }, Bind, null,
                (value, id, path) => { if (!copied.ContainsKey(value)) copied.Add(value, Binding + id + "/" + path); });
            if (graph.Unsupported.Length != 0) throw new InvalidDataException("Final visual complete closure unsupported: " + string.Join(";", graph.Unsupported));
            return copied;
        }
        internal static IReadOnlyDictionary<string, Tuple<FieldInfo, object>> StructSlots(Func<UnityEngine.Object, string> identity)
        {
            var result = new Dictionary<string, Tuple<FieldInfo, object>>(StringComparer.Ordinal);
            // Value-type boxes are freshly allocated by both readers. Anchor
            // their reference slots to the real original owner field walk,
            // not the identity of a temporary box or just a root-name match.
            using (var reader = new LoadingFieldDigest(value => value is UnityEngine.Object ? "native" : null,
                type => type == typeof(UnityEngine.Object) || type == typeof(MonoBehaviour),
                slotObservation: (owner, field, index, value, path) => {
                    if (!owner.GetType().IsValueType || field == null || value == null || value.GetType().IsValueType || value is string) return;
                    if (result.Count >= 8192) throw new InvalidOperationException("Final visual inline slot proof exceeds bound.");
                    result[path] = Tuple.Create(field, value);
                })) {
                foreach (var owner in Directory().Values) {
                    var observed = reader.CaptureReferences(owner, identity(owner), expandRoot: true);
                    if (observed.Unsupported.Count != 0) throw new InvalidOperationException("Final visual inline slot proof is incomplete.");
                }
            }
            return result;
        }
        internal static Action Prepare(Data source, Data current, CanonicalCarryGraphCodec codec, Func<bool> admitted, out Action verify)
        {
            if (source.Schema != Schema || !source.Owners.SequenceEqual(current.Owners) || !source.Fields.SequenceEqual(current.Fields)
                || !source.Native.SequenceEqual(current.Native) || source.Values.Length != 5)
                throw new InvalidDataException("Final visual source ABI/native lifecycle/pose differs.");
            var directory = Directory(); var owners = directory.Values.ToArray();
            var fields = owners.Select(owner => Fields(owner.GetType())).ToArray();
            var readonlyApply = CanonicalMarkerReadonlyCarry.PrepareFinalVisual(source.Readonly, directory, codec, admitted);
            for (var i = 0; i < owners.Length; i++) {
                if (source.Values[i].Length != fields[i].Length) throw new InvalidDataException("Final visual field row differs.");
                for (var f = 0; f < fields[i].Length; f++) {
                    var value = source.Values[i][f]; var field = fields[i][f];
                    if (value == null ? field.FieldType.IsValueType : !field.FieldType.IsInstanceOfType(value)) throw new InvalidDataException("Final visual source field type differs.");
                    if (field.IsInitOnly && !ReferenceEquals(value, field.GetValue(owners[i]))) throw new InvalidDataException("Final visual readonly owner identity differs.");
                }
            }
            // All five meshes also belong to the shared persistent native
            // consumer guard. SourceDomain checks that guard before writes,
            // after this refresh and on final source readback. Re-enumerating
            // every native consumer separately for these same five meshes on
            // each field readback would duplicate the same synchronous proof.
            void Verify() {
                using var validation = LoadingEngineClockProbe.ReadOnlyCheckpointScope();
                var now = Directory();
                if (!now.Keys.SequenceEqual(directory.Keys) || now.Any(pair => !ReferenceEquals(pair.Value, directory[pair.Key]))) throw new InvalidOperationException("Final visual owner changed.");
                for (var i = 0; i < owners.Length; i++) {
                    if (Native(owners[i]) != source.Native[i]) throw new InvalidOperationException("Final visual native state changed.");
                    for (var f = 0; f < fields[i].Length; f++) if (!codec.SameRawSlot(source.Values[i][f], fields[i][f].GetValue(owners[i]), fields[i][f].FieldType, 0))
                        throw new InvalidOperationException("Final visual full source field/alias readback differs: " + directory.Keys.ElementAt(i) + "/" + fields[i][f].Name);
                }
                if (ShadowSprite().GetComponent<MeshRenderer>().enabled != source.ShadowRenderer) throw new InvalidOperationException("Shadow source renderer readback differs.");
            }
            verify = Verify;
            return () => {
                if (!admitted()) throw new InvalidOperationException("Final visual source application not admitted.");
                for (var i = 0; i < owners.Length; i++) for (var f = 0; f < fields[i].Length; f++)
                    if (!fields[i][f].IsInitOnly) fields[i][f].SetValue(owners[i], source.Values[i][f]);
                readonlyApply();
                foreach (var text in owners.OfType<TextMeshPro>()) {
                    var info = (TMP_TextInfo)typeof(TMP_Text).GetField("m_textInfo", Flags)!.GetValue(text);
                    if (info == null || info.materialCount < 1 || info.materialCount > 2 || info.meshInfo == null || info.meshInfo.Length < info.materialCount)
                        throw new InvalidOperationException("Final visual source mesh-info layout differs.");
                    for (var i = 0; i < info.materialCount; i++) {
                        var item = info.meshInfo[i]; if (item.mesh == null) throw new InvalidOperationException("Final visual source mesh alias absent.");
                        item.mesh.Clear(); item.mesh.vertices = item.vertices; item.mesh.normals = item.normals;
                        item.mesh.tangents = item.tangents; item.mesh.triangles = item.triangles;
                    }
                    text.UpdateVertexData(); // pinned original: vertices, UV0/1, colors and bounds only; no callbacks/activation
                }
                ShadowSprite().GetComponent<MeshRenderer>().enabled = source.ShadowRenderer;
                Verify();
            };
        }
        private sealed class References : IEqualityComparer<object>
        {
            public new bool Equals(object? a, object? b) => ReferenceEquals(a, b);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
