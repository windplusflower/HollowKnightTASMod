using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // The transitive original level1 closure: thirteen replaceable roots,
    // eighteen GameObjects, ninety-two components, forty MonoBehaviours.
    // Gameplay Inventory/Charms parents are deliberately outside this domain.
    internal static class CanonicalHudPresentationDirectory
    {
        internal const string OriginalLevel1Sha256 = "f29d7ae503508d81552208ab4efcb1a8d4477820eb31ab6d7724e759cc47776a";
        internal const int RootCount = 13, ObjectCount = 18, ComponentCount = 92, MonoCount = 40;
        internal sealed class Definition
        {
            internal readonly string Path;
            internal readonly long SerializedGameObject;
            private readonly Type[] components;
            private readonly string[] children;
            internal Type[] Components => (Type[])components.Clone();
            internal string[] Children => (string[])children.Clone();
            internal Definition(string path, long gameObject, Type[] components, params string[] children)
            { Path = path; SerializedGameObject = gameObject; this.components = components; this.children = children; }
        }
        private static Type[] Text(params Type[] suffix) => new[] { typeof(RectTransform), typeof(MeshRenderer), typeof(MeshFilter),
            typeof(TextContainer), typeof(TextMeshPro), typeof(CanvasRenderer) }.Concat(suffix).ToArray();
        private static Type[] Corner => new[] { typeof(Transform), typeof(SpriteRenderer), typeof(PlayMakerFSM) };
        private static readonly Definition[] Nodes = {
            new Definition("Border/Pane Name",1357,Text(typeof(UnboldIfCJK),typeof(ChangeFontByLanguage))),
            new Definition("Border/Pane Name L",788,Text(typeof(ChangeFontByLanguage))),
            new Definition("Border/Pane Name R",1186,Text(typeof(ChangeFontByLanguage))),
            new Definition("Charms/Confirm Action",921,new[]{typeof(Transform),typeof(PlayMakerFSM)},"ActionButtonIcon","Text"),
            new Definition("Charms/Confirm Action/ActionButtonIcon",797,new[]{typeof(Transform),typeof(SpriteRenderer),typeof(MenuButtonIcon)},"Label"),
            new Definition("Charms/Confirm Action/ActionButtonIcon/Label",805,new[]{typeof(RectTransform),typeof(TextContainer),typeof(MeshRenderer),typeof(MeshFilter),typeof(TextMeshPro),typeof(CanvasRenderer)}),
            new Definition("Charms/Confirm Action/Text",962,new[]{typeof(RectTransform),typeof(TextContainer),typeof(MeshFilter),typeof(MeshRenderer),typeof(CanvasRenderer),typeof(TextMeshPro),typeof(SetTextMeshProGameText),typeof(ChangeFontByLanguage)}),
            new Definition("Charms/Cursor/BL/Sprite",1407,Corner),
            new Definition("Charms/Cursor/BR/Sprite",1074,Corner),
            new Definition("Charms/Cursor/TL/Sprite",1016,Corner),
            new Definition("Charms/Cursor/TR/Sprite",780,Corner),
            new Definition("Charms/Details/Cost/Text Cost",846,Text(typeof(SetTextMeshProGameText),typeof(ChangeFontByLanguage))),
            new Definition("Charms/Equipped Charms/Text Equipped",928,new[]{typeof(RectTransform),typeof(MeshRenderer),typeof(MeshFilter),typeof(TextContainer),typeof(TextMeshPro),typeof(PlayMakerFSM),typeof(CanvasRenderer),typeof(ChangeFontByLanguage),typeof(SetTextMeshProGameText)}),
            new Definition("Charms/Overcharm/Overcharm Set",803,new[]{typeof(Transform),typeof(PlayMakerFSM)},"break_glow","Wave"),
            new Definition("Charms/Overcharm/Overcharm Set/Wave",2227,new[]{typeof(Transform),typeof(SpriteRenderer),typeof(WaveEffectControl)}),
            new Definition("Charms/Overcharm/Overcharm Set/break_glow",1163,new[]{typeof(Transform),typeof(SpriteRenderer),typeof(SimpleFadeOut)}),
            new Definition("Charms/Text Desc",1038,Text(typeof(ChangeFontByLanguage))),
            new Definition("Charms/Text Name",1048,Text(typeof(ChangeFontByLanguage)))
        };
        internal static bool IsBorderMember(string key) => Nodes.Take(3).Any(node => key == "$/" + node.Path || key.StartsWith("$/" + node.Path + ":", StringComparison.Ordinal));
        internal static Definition[] Definitions => (Definition[])Nodes.Clone();
        private static readonly Type[] ManagedTypes = { typeof(TextContainer), typeof(TextMeshPro), typeof(UnboldIfCJK), typeof(ChangeFontByLanguage),
            typeof(PlayMakerFSM), typeof(MenuButtonIcon), typeof(SetTextMeshProGameText), typeof(WaveEffectControl), typeof(SimpleFadeOut) };
        private static readonly int[] ManagedCounts = { 12, 223, 1, 14, 4, 20, 3, 10, 6 };
        private static readonly object metadataGate = new object();
        private static MetadataSession? metadataSession;
        internal sealed class MetadataSession : IDisposable
        {
            private readonly string profile = HollowKnightTAS.Core.Movie.NativeExecutionProfile.SelectedProfileId;
            private readonly Dictionary<Tuple<Module, Type, string>, FieldInfo[]> fields = new Dictionary<Tuple<Module, Type, string>, FieldInfo[]>();
            private readonly Dictionary<string, string> schemas = new Dictionary<string, string>(StringComparer.Ordinal);
            internal FieldInfo[] Fields(Type type, string kind, Func<FieldInfo[]> build)
            {
                lock (metadataGate) {
                    Require();
                    var key = Tuple.Create(type.Module, type, kind + ":" + OriginalLevel1Sha256 + ":" + HollowKnightTAS.Core.Movie.NativeExecutionProfile.CanonicalHudPresentationPolicyId);
                    if (!fields.TryGetValue(key, out var value)) {
                        if (fields.Count >= 13) throw new InvalidDataException("HUD session metadata exceeded its fixed nine-plus-four type bound.");
                        fields.Add(key, value = build());
                    }
                    return (FieldInfo[])value.Clone();
                }
            }
            internal string Schema(string key, Func<string> build)
            {
                lock (metadataGate) { Require(); if (!schemas.TryGetValue(key, out var value)) { if (schemas.Count >= 2) throw new InvalidDataException("HUD session schema bound exceeded."); schemas.Add(key, value = build()); } return value; }
            }
            private void Require()
            { if (!ReferenceEquals(metadataSession, this) || profile != HollowKnightTAS.Core.Movie.NativeExecutionProfile.SelectedProfileId) throw new InvalidOperationException("HUD metadata lost its sealed template session/profile."); }
            public void Dispose()
            { lock (metadataGate) { if (ReferenceEquals(metadataSession, this)) metadataSession = null; fields.Clear(); schemas.Clear(); } }
        }
        internal static MetadataSession BeginMetadataSession()
        {
            lock (metadataGate) {
                if (metadataSession != null) throw new InvalidOperationException("HUD metadata template session is already active.");
                return metadataSession = new MetadataSession();
            }
        }
        internal static FieldInfo[] MetadataFields(Type type, string kind, Func<FieldInfo[]> build) => metadataSession?.Fields(type, kind, build) ?? build();
        internal static string MetadataSchema(string key, Func<string> build) => metadataSession?.Schema(key, build) ?? build();
        internal static FieldInfo[] ManagedFields(Type type)
        {
            var index = Array.IndexOf(ManagedTypes, type);
            if (index < 0)
                throw new InvalidDataException("Original HUD presentation managed type/module differs.");
            var module = type == typeof(PlayMakerFSM) ? "29eb59fa-5c99-43cd-8f33-6db074533d31" : CanonicalSceneMappingCarry.ModuleMvid;
            var actualModule = type.Module.ModuleVersionId.ToString("D");
            if (actualModule != module)
                CanonicalSceneMappingCarry.WarnOnce("HudPresentation:ManagedFields:" + type.FullName + ":" + actualModule,
                    "[HKTAS] HUD presentation managed module MVID differs (continuing): type=" + type.FullName + ";expected=" + module + ";actual=" + actualModule);
            return MetadataFields(type, "original-managed", () => {
            var fields = CanonicalCarryGraphCodec.Fields(type).Where(field => field.DeclaringType != typeof(UnityEngine.Object)
                && field.DeclaringType != typeof(Component) && field.DeclaringType != typeof(Behaviour) && field.DeclaringType != typeof(MonoBehaviour)).ToArray();
            if (fields.Length != ManagedCounts[index] || fields.Any(field => field.FieldType.IsPointer || field.FieldType.IsByRef
                || field.FieldType == typeof(IntPtr) || field.FieldType == typeof(UIntPtr)))
                throw new InvalidDataException("Original HUD presentation complete managed ABI differs: " + type.FullName);
            return fields;
            });
        }
        internal static string SchemaIdentity => MetadataSchema("original-managed", () => "original-hud-presentation:13roots18objects92components40mono9TMP7FSM:level1:" + OriginalLevel1Sha256
            + ":" + string.Join(";", ManagedTypes.SelectMany(type => ManagedFields(type)).Select(field => field.DeclaringType!.FullName + "/" + field.Name + "=" + field.FieldType.FullName)));
        internal static string[] RootPaths => Nodes.Select(node => node.Path).Where(path => !Nodes.Any(parent =>
            path.StartsWith(parent.Path + "/", StringComparison.Ordinal))).ToArray();
        internal static void ValidateAbi()
        {
            var actualTmpModule = typeof(TextMeshPro).Module.ModuleVersionId.ToString("D");
            if (actualTmpModule != CanonicalSceneMappingCarry.ModuleMvid)
                CanonicalSceneMappingCarry.WarnOnce("HudPresentation:ValidateAbi:" + actualTmpModule,
                    "[HKTAS] TextMeshPro ModuleVersionId differs from pinned original (continuing): actual=" + actualTmpModule);
            if (Nodes.Length != ObjectCount || Nodes.Select(node => node.Path).Distinct(StringComparer.Ordinal).Count() != ObjectCount
                || Nodes.Select(node => node.SerializedGameObject).Distinct().Count() != ObjectCount
                || RootPaths.Length != RootCount || Nodes.Sum(node => node.Components.Length) != ComponentCount
                || Nodes.Sum(node => node.Components.Count(type => typeof(MonoBehaviour).IsAssignableFrom(type))) != MonoCount
                || Nodes.Sum(node => node.Components.Count(type => type == typeof(TextMeshPro))) != 9
                || Nodes.Sum(node => node.Components.Count(type => type == typeof(PlayMakerFSM))) != 7)
                throw new InvalidDataException("Original HUD presentation complete serialized ABI differs.");
            foreach (var node in Nodes) {
                foreach (var child in node.Children)
                    if (!Nodes.Any(candidate => candidate.Path == node.Path + "/" + child))
                        throw new InvalidDataException("Original HUD presentation recursive child closure is incomplete.");
                if (Nodes.Count(candidate => candidate.Path.StartsWith(node.Path + "/", StringComparison.Ordinal)
                    && candidate.Path.Substring(node.Path.Length + 1).IndexOf('/') < 0) != node.Children.Length)
                    throw new InvalidDataException("Original HUD presentation child closure contains an unregistered node.");
            }
        }
        internal static void RequireShape(string path, IReadOnlyList<Type> components, IReadOnlyList<string> children, bool allowGeneratedSubmeshes)
        {
            var definition = Nodes.SingleOrDefault(node => node.Path == path)
                ?? throw new InvalidDataException("Unregistered HUD presentation owner path.");
            if (!components.SequenceEqual(definition.Components) || children.Count > 36
                || children.Distinct(StringComparer.Ordinal).Count() != children.Count)
                throw new InvalidDataException("Original HUD presentation component/child directory differs: " + path);
            var original = definition.Children;
            var extra = children.Where(child => !original.Contains(child, StringComparer.Ordinal)).ToArray();
            if (!children.Where(child => original.Contains(child, StringComparer.Ordinal)).SequenceEqual(original)
                || extra.Length != 0 && (!allowGeneratedSubmeshes || !definition.Components.Contains(typeof(TextMeshPro))
                    || extra.Any(child => !child.StartsWith("TMP SubMesh [", StringComparison.Ordinal) || !child.EndsWith("]", StringComparison.Ordinal))))
                throw new InvalidDataException("Original HUD presentation has an unregistered child/ordering: " + path);
        }
        internal static Transform Current()
        {
            var cameras = GameCameras.instance;
            if (cameras == null || cameras.hudCamera == null) throw new InvalidDataException("Original HUD presentation camera is absent.");
            return cameras.hudCamera.transform.Find("Inventory") ?? throw new InvalidDataException("Original Inventory parent is absent.");
        }
        private static Transform Resolve(Transform parent, string relative)
        {
            foreach (var name in relative.Split('/')) {
                var matches = parent.Cast<Transform>().Where(child => child.name == name).ToArray();
                if (matches.Length != 1) throw new InvalidDataException("Original HUD presentation source path is absent/ambiguous: " + relative);
                parent = matches[0];
            }
            return parent;
        }
        internal static SortedDictionary<string, UnityEngine.Object> Build(Transform inventory, IReadOnlyList<GameObject>? roots = null,
            bool allowGeneratedSubmeshes = false)
        {
            ValidateAbi();
            if (inventory == null || inventory.name != "Inventory" || roots != null && roots.Count != RootCount)
                throw new InvalidDataException("Original HUD presentation root directory differs.");
            var paths = RootPaths;
            var top = paths.Select((path, index) => roots == null ? Resolve(inventory, path).gameObject : roots[index]).ToArray();
            var result = new SortedDictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
            foreach (var node in Nodes) {
                var index = Array.FindIndex(paths, root => node.Path == root || node.Path.StartsWith(root + "/", StringComparison.Ordinal));
                var transform = node.Path == paths[index] ? top[index].transform
                    : Resolve(top[index].transform, node.Path.Substring(paths[index].Length + 1));
                var parts = transform.GetComponents<Component>();
                if (parts.Any(part => part == null)) throw new InvalidDataException("Original HUD presentation has a missing component.");
                RequireShape(node.Path, parts.Select(part => part.GetType()).ToArray(), transform.Cast<Transform>().Select(child => child.name).ToArray(), allowGeneratedSubmeshes);
                var key = "$/" + node.Path;
                result.Add(key, transform.gameObject);
                var ordinals = new Dictionary<Type, int>();
                foreach (var part in parts) {
                    var type = part.GetType(); ordinals.TryGetValue(type, out var ordinal); ordinals[type] = ordinal + 1;
                    result.Add(key + ":" + type.FullName + "[" + ordinal + "]", part);
                }
            }
            if (result.Count != ObjectCount + ComponentCount) throw new InvalidDataException("Original HUD presentation complete directory differs.");
            RequireOriginalInternalAliases(result);
            return result;
        }
        internal static void RequireOriginalInternalAliases(IReadOnlyDictionary<string, UnityEngine.Object> directory)
        {
            const string icon = "$/Charms/Confirm Action/ActionButtonIcon";
            var button = (MenuButtonIcon)directory[icon + ":MenuButtonIcon[0]"];
            if (!ReferenceEquals(button.label, directory[icon + "/Label:TMPro.TextMeshPro[0]"])
                || !ReferenceEquals(button.textContainer, directory[icon + "/Label:TMPro.TextContainer[0]"]))
                throw new InvalidDataException("Original confirm-button/label serialized aliases were lost.");
            const string wave = "$/Charms/Overcharm/Overcharm Set/Wave";
            var owner = (WaveEffectControl)directory[wave + ":WaveEffectControl[0]"];
            if (!ReferenceEquals(owner.spriteRenderer, directory[wave + ":UnityEngine.SpriteRenderer[0]"]))
                throw new InvalidDataException("Original overcharm Wave sprite-renderer serialized alias was lost.");
        }
        internal static SortedDictionary<string, UnityEngine.Object> GeneratedSubmeshes(IReadOnlyDictionary<string, UnityEngine.Object> directory)
        {
            var result = new SortedDictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var componentField = typeof(TMP_SubMesh).GetField("m_TextComponent", flags)!;
            var filterField = typeof(TMP_SubMesh).GetField("m_meshFilter", flags)!;
            var expected = new[] { typeof(Transform), typeof(MeshRenderer), typeof(MeshFilter), typeof(TMP_SubMesh), typeof(LinkRendererState) }
                .OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
            foreach (var pair in directory.Where(pair => pair.Value is TextMeshPro)) {
                var text = (TextMeshPro)pair.Value;
                var candidates = text.transform.Cast<Transform>().ToArray();
                if (candidates.Length > 32) throw new InvalidDataException("HUD TMP generated-child budget exceeded.");
                for (var index = 0; index < candidates.Length; index++) {
                    var child = candidates[index];
                    var parts = child.GetComponents<Component>();
                    if (!child.name.StartsWith("TMP SubMesh [", StringComparison.Ordinal) || !child.name.EndsWith("]", StringComparison.Ordinal)
                        || child.childCount != 0 || parts.Any(part => part == null)
                        || !parts.Select(part => part.GetType()).OrderBy(type => type.FullName, StringComparer.Ordinal).SequenceEqual(expected))
                        throw new InvalidDataException("HUD generated child does not match the complete original TMP factory.");
                    var sub = child.GetComponent<TMP_SubMesh>(); var filter = child.GetComponent<MeshFilter>();
                    var links = child.GetComponent<LinkRendererState>();
                    if (!ReferenceEquals(componentField.GetValue(sub), text) || !ReferenceEquals(filterField.GetValue(sub), filter)
                        || !ReferenceEquals(links.parent, text.GetComponent<MeshRenderer>())
                        || !ReferenceEquals(links.child, child.GetComponent<MeshRenderer>()))
                        throw new InvalidDataException("HUD original TMP generated child has a foreign parent/filter/renderer backlink.");
                    var key = pair.Key + "/original-generated-submesh[" + index + "]";
                    result.Add(key, child.gameObject);
                    foreach (var part in parts) result.Add(key + ":" + part.GetType().FullName + "[0]", part);
                }
            }
            return result;
        }
    }
}
