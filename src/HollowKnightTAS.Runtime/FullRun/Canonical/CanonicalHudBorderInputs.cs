using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using TMPro;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Logical inputs to the three original active Border text factories.
    // Runtime mesh/submesh/glyph/dirty caches remain the original producer's responsibility.
    internal static class CanonicalHudBorderInputs
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static readonly string[] Paths = { "Border/Pane Name", "Border/Pane Name L", "Border/Pane Name R" };
        private static readonly string[] TextNames = { "m_Material","m_Color","m_RaycastTarget","m_RaycastPadding","m_Maskable","m_text","m_isRightToLeft","m_fontAsset","m_sharedMaterial","m_fontMaterial","m_fontColor32","m_fontColor","m_enableVertexGradient","m_fontColorGradient","m_fontColorGradientPreset","m_tintAllSprites","m_overrideHtmlColors","m_faceColor","m_outlineColor","m_fontSize","m_fontSizeBase","m_fontWeight","m_enableAutoSizing","m_fontSizeMin","m_fontSizeMax","m_fontStyle","m_textAlignment","m_characterSpacing","m_lineSpacing","m_lineSpacingMax","m_paragraphSpacing","m_charWidthMaxAdj","m_enableWordWrapping","m_wordWrappingRatios","m_enableAdaptiveJustification","m_overflowMode","m_enableKerning","m_enableExtraPadding","checkPaddingRequired","m_isRichText","m_parseCtrlCharacters","m_isOrthographic","m_isCullingEnabled","m_ignoreCulling","m_horizontalMapping","m_verticalMapping","m_useMaxVisibleDescender","m_pageToDisplay","m_margin","m_isUsingLegacyAnimationComponent","m_isVolumetricText","m_inputSource","m_uvOffset","m_uvLineOffset","m_maskType","m_lineLength","m_anchor" };
        private static readonly string[] ContainerNames = { "m_pivot","m_anchorPosition","m_rect","m_margins" };
        private static readonly string[] FontNames = { "defaultFont","fontJA","fontRU","fontZH","fontKO","onlyOnStart","fontScaleLangType" };
        private static readonly string[] PaneNames = { "Current Pane Str", "L Pane Str", "R Pane Str" };
        internal static string SchemaIdentity => CanonicalHudPresentationDirectory.MetadataSchema("border-input", () => "original-active-Border3-inputs-v1:" + CanonicalHudPresentationDirectory.OriginalLevel1Sha256
            + ":" + string.Join(";", new[] { typeof(TextMeshPro), typeof(TextContainer), typeof(ChangeFontByLanguage), typeof(UnboldIfCJK) }
                .SelectMany(Fields).Select(field => field.DeclaringType!.FullName + "/" + field.Name + "=" + field.FieldType.FullName)));
        internal static readonly Type[] DataTypes = { typeof(Data), typeof(RectData), typeof(RectData[]), typeof(object[]), typeof(object[][]), typeof(string[]), typeof(float[]), typeof(bool[]) };
        internal sealed class Data
        {
            public string Schema = "";
            public string[] Owners = Array.Empty<string>();
            public string[] FieldNames = Array.Empty<string>();
            public object?[][] Values = Array.Empty<object?[]>();
            public float[] BaseFontSizes = Array.Empty<float>();
            public bool[] RendererEnabled = Array.Empty<bool>();
            public RectData[] Rects = Array.Empty<RectData>();
            public string[] PaneStrings = Array.Empty<string>();
        }
        internal sealed class RectData
        {
            public Vector3 Position, Scale;
            public Quaternion Rotation;
            public Vector2 AnchoredPosition, SizeDelta, Pivot, AnchorMin, AnchorMax;
        }
        internal static bool IsBorderPath(string path) => Paths.Contains(path, StringComparer.Ordinal);
        internal static FieldInfo[] Fields(Type type)
        {
            return CanonicalHudPresentationDirectory.MetadataFields(type, "border-input", () => {
            var complete = CanonicalHudPresentationDirectory.ManagedFields(type);
            var names = type == typeof(TextMeshPro) ? TextNames : type == typeof(TextContainer) ? ContainerNames
                : type == typeof(ChangeFontByLanguage) ? FontNames : type == typeof(UnboldIfCJK) ? Array.Empty<string>()
                : throw new InvalidDataException("Foreign Border logical input owner.");
            return names.Select(name => {
                var matches = complete.Where(field => field.Name == name).ToArray();
                if (matches.Length != 1 || matches[0].IsInitOnly || matches[0].IsNotSerialized
                    || !(matches[0].IsPublic || matches[0].GetCustomAttributes(typeof(SerializeField), false).Length == 1))
                    throw new InvalidDataException("Original Border serialized input ABI differs: " + type.FullName + "/" + name);
                return matches[0];
            }).ToArray();
            });
        }
        private static KeyValuePair<string, UnityEngine.Object>[] Owners(IReadOnlyDictionary<string, UnityEngine.Object> directory)
        {
            var rows = directory.Where(pair => pair.Value is MonoBehaviour && Paths.Any(path => pair.Key.StartsWith("$/" + path + ":", StringComparison.Ordinal))).ToArray();
            if (rows.Length != 10 || rows.Count(pair => pair.Value is TextMeshPro) != 3
                || rows.Count(pair => pair.Value is TextContainer) != 3 || rows.Count(pair => pair.Value is ChangeFontByLanguage) != 3
                || rows.Count(pair => pair.Value is UnboldIfCJK) != 1)
                throw new InvalidDataException("Complete original Border input owner directory differs.");
            return rows;
        }
        private static FsmString[] PaneStrings(Transform inventory)
        {
            var border = inventory.Find("Border") ?? throw new InvalidDataException("Original Border owner is absent.");
            var matches = border.GetComponents<PlayMakerFSM>().Select(owner => (Fsm)typeof(PlayMakerFSM).GetField("fsm", Flags)!.GetValue(owner))
                .Where(fsm => fsm.Name == "Pane Names").ToArray();
            if (matches.Length != 1 || matches[0].ActiveStateName != "Idle")
                throw new InvalidOperationException("An in-progress original Border text change needs its unchanged continuation.");
            var variables = (FsmVariables)typeof(Fsm).GetField("variables", Flags)!.GetValue(matches[0]);
            var strings = variables.StringVariables;
            if (strings.Length != 3 || !strings.Select(value => value.Name).OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(PaneNames.OrderBy(value => value, StringComparer.Ordinal)))
                throw new InvalidDataException("Original complete Border pane string directory differs.");
            var objects = variables.GameObjectVariables;
            var objectNames = new[] { "Current Pane", "Pane L", "Pane R" };
            if (objects.Length != 3 || !objects.Select(value => value.Name).OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(objectNames.OrderBy(value => value, StringComparer.Ordinal))
                || Enumerable.Range(0, 3).Any(index => !ReferenceEquals(objects.Single(value => value.Name == objectNames[index]).Value, inventory.Find(Paths[index]).gameObject)))
                throw new InvalidDataException("Original Border pane GameObject aliases differ.");
            return PaneNames.Select(name => strings.Single(value => value.Name == name)).ToArray();
        }
        internal static Data Capture(Transform inventory, IReadOnlyDictionary<string, UnityEngine.Object> directory,
            Func<object, string?> bindOriginalAsset)
        {
            var owners = Owners(directory);
            if (owners.Any(pair => !((MonoBehaviour)pair.Value).enabled))
                throw new InvalidOperationException("Disabled Border Mono input needs its original enabled continuation.");
            CanonicalMarkerEventCarry.RequireEmpty(directory);
            var texts = Paths.Select(path => inventory.Find(path).GetComponent<TextMeshPro>()).ToArray();
            var fonts = Paths.Select(path => inventory.Find(path).GetComponent<ChangeFontByLanguage>()).ToArray();
            if (texts.Any(text => text == null || !text.enabled || !text.gameObject.activeInHierarchy)
                || fonts.Any(font => (bool)typeof(ChangeFontByLanguage).GetField("onlyOnStart", Flags)!.GetValue(font)))
                throw new InvalidOperationException("Border logical inputs require the exact original active/OnEnable-font configuration.");
            void RequireAsset(object? value, int depth)
            {
                if (value == null) return;
                if (depth > 8) throw new InvalidDataException("Border logical input asset nesting exceeds its finite bound.");
                if (value is UnityEngine.Object native) {
                    if (native == null || native is Component || native is GameObject || bindOriginalAsset(native) == null)
                        throw new InvalidDataException("Border logical setting contains a foreign/dead/unsealed native asset.");
                } else if (value is Array array) {
                    if (array.Length > 16) throw new InvalidDataException("Border logical input array exceeds the original material bound.");
                    foreach (var item in array) RequireAsset(item, depth + 1);
                }
            }
            var inputSource = typeof(TMP_Text).GetField("m_inputSource", Flags)!;
            var autoSize = typeof(TMP_Text).GetField("<autoSizeTextContainer>k__BackingField", Flags)!;
            var autoFit = typeof(TextContainer).GetField("m_isAutoFitting", Flags)!;
            foreach (var text in texts) {
                var kind = inputSource.GetValue(text)!.ToString();
                if (kind != "Text" && kind != "String" || (bool)autoSize.GetValue(text)
                    || (bool)autoFit.GetValue(text.GetComponent<TextContainer>()))
                    throw new InvalidOperationException("Original Border has an unsupported character-array, formatted or auto-fit input continuation.");
            }
            var values = owners.Select(pair => Fields(pair.Value.GetType()).Select(field => field.GetValue(pair.Value)).ToArray()).ToArray();
            for (var owner = 0; owner < owners.Length; owner++) for (var index = 0; index < values[owner].Length; index++) {
                try { RequireAsset(values[owner][index], 0); }
                catch (Exception error) { throw new InvalidDataException("Border input " + owners[owner].Key + "/" + Fields(owners[owner].Value.GetType())[index].Name + ": " + error.Message, error); }
            }
            var rects = Paths.Select(path => inventory.Find(path).GetComponent<RectTransform>()).ToArray();
            return new Data { Schema = SchemaIdentity, Owners = owners.Select(pair => pair.Key).ToArray(),
                FieldNames = owners.SelectMany(pair => Fields(pair.Value.GetType()).Select(field => pair.Key + "/" + field.DeclaringType!.FullName + "/" + field.Name)).ToArray(),
                Values = values, BaseFontSizes = fonts.Select(font => (float)typeof(ChangeFontByLanguage).GetField("startFontSize", Flags)!.GetValue(font)).ToArray(),
                RendererEnabled = texts.Select(text => text.GetComponent<MeshRenderer>().enabled).ToArray(),
                Rects = rects.Select(rect => new RectData { Position = rect.localPosition, Rotation = rect.localRotation, Scale = rect.localScale,
                    AnchoredPosition = rect.anchoredPosition, SizeDelta = rect.sizeDelta, Pivot = rect.pivot, AnchorMin = rect.anchorMin, AnchorMax = rect.anchorMax }).ToArray(),
                PaneStrings = PaneStrings(inventory).Select(value => value.Value).ToArray() };
        }
        internal static SortedDictionary<string, FieldInfo[]> InputLayout()
        {
            var result = new SortedDictionary<string, FieldInfo[]>(StringComparer.Ordinal);
            foreach (var definition in CanonicalHudPresentationDirectory.Definitions.Where(node => IsBorderPath(node.Path)))
                foreach (var type in definition.Components.Where(type => typeof(MonoBehaviour).IsAssignableFrom(type)))
                    result.Add("$/" + definition.Path + ":" + type.FullName + "[0]", Fields(type));
            if (result.Count != 10 || result.Values.Sum(fields => fields.Length) != 204)
                throw new InvalidDataException("Original complete Border logical input metadata differs.");
            return result;
        }
        internal static void Validate(Data source)
        {
            var layout = InputLayout();
            if (source == null || source.Schema != SchemaIdentity || source.Owners == null || !source.Owners.SequenceEqual(layout.Keys)
                || source.Values == null || source.Values.Length != 10 || source.Values.Any(row => row == null)
                || source.FieldNames == null || !source.FieldNames.SequenceEqual(layout.SelectMany(pair => pair.Value.Select(field => pair.Key + "/" + field.DeclaringType!.FullName + "/" + field.Name)))
                || source.BaseFontSizes == null || source.BaseFontSizes.Length != 3 || source.RendererEnabled == null || source.RendererEnabled.Length != 3
                || source.Rects == null || source.Rects.Length != 3 || source.Rects.Any(value => value == null) || source.PaneStrings == null || source.PaneStrings.Length != 3
                || source.BaseFontSizes.Any(value => float.IsNaN(value) || float.IsInfinity(value) || value <= 0))
                throw new InvalidDataException("Border complete logical source input directory differs.");
            var rows = layout.Values.ToArray();
            for (var owner = 0; owner < rows.Length; owner++) {
                if (source.Values[owner].Length != rows[owner].Length) throw new InvalidDataException("Border complete logical input row differs.");
                for (var index = 0; index < rows[owner].Length; index++) {
                    var value = source.Values[owner][index]; var type = rows[owner][index].FieldType;
                    if (value == null ? type.IsValueType : !type.IsInstanceOfType(value))
                        throw new InvalidDataException("Border logical input value type differs: " + source.Owners[owner] + "/" + rows[owner][index].Name);
                    if ((rows[owner][index].Name == "m_fontAsset" || rows[owner][index].Name == "m_sharedMaterial") && value == null)
                        throw new InvalidDataException("Border logical font/material source is absent.");
                }
            }
            foreach (var rect in source.Rects) foreach (var field in CanonicalCarryGraphCodec.Fields(typeof(RectData))) {
                var value = field.GetValue(rect)!;
                foreach (var scalar in CanonicalCarryGraphCodec.Fields(field.FieldType)) {
                    var number = (float)scalar.GetValue(value);
                    if (float.IsNaN(number) || float.IsInfinity(number)) throw new InvalidDataException("Border native layout contains a nonfinite input.");
                }
            }
        }
        internal static IReadOnlyDictionary<string, object> PaneStringBindings(Transform inventory)
            => PaneStrings(inventory).Select((value, index) => new KeyValuePair<string, object>("BorderInput/PaneString/" + PaneNames[index], value))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        internal static void VerifyPaneSource(Transform inventory, Data source)
        {
            Validate(source);
            if (!PaneStrings(inventory).Select(value => value.Value).SequenceEqual(source.PaneStrings, StringComparer.Ordinal))
                throw new InvalidOperationException("Original Border source pane scalar readback differs.");
        }
        internal static Action PreparePaneStringsAfterRebind(Transform inventory, Data source, Func<bool> admitted, CanonicalCarryGraphCodec codec)
        {
            Validate(source);
            var slots = PaneStrings(inventory);
            var valueField = typeof(FsmString).GetField("value", Flags)
                ?? throw new InvalidDataException("Original Border string value field is absent.");
            var actualFsmStringMvid = typeof(FsmString).Module.ModuleVersionId.ToString("D");
            if (actualFsmStringMvid != "29eb59fa-5c99-43cd-8f33-6db074533d31")
                CanonicalSceneMappingCarry.WarnOnce("HudBorderInputs:FsmStringMvid:" + actualFsmStringMvid,
                    "[HKTAS] PlayMaker FsmString ModuleVersionId differs from pinned original (continuing): actual=" + actualFsmStringMvid);
            if (valueField.FieldType != typeof(string) || valueField.IsInitOnly || slots.Any(slot => slot.GetType() != typeof(FsmString)))
                throw new InvalidDataException("Original Border FsmString complete ABI differs.");
            var fields = CanonicalCarryGraphCodec.Fields(typeof(FsmString));
            var before = slots.Select(slot => fields.Select(field => field.GetValue(slot)).ToArray()).ToArray();
            var used = false;
            return () => {
                if (used || !admitted()) throw new InvalidOperationException("Border source pane scalar application lost single-use parked admission.");
                used = true;
                var current = PaneStrings(inventory);
                if (!slots.Where((slot, index) => !ReferenceEquals(slot, current[index])).Any()) {
                    for (var owner = 0; owner < slots.Length; owner++)
                        for (var index = 0; index < fields.Length; index++)
                            if (!codec.SameRawSlot(before[owner][index], fields[index].GetValue(slots[owner]), fields[index].FieldType, 0))
                                throw new InvalidOperationException("Border pane input wrapper changed after admission.");
                } else throw new InvalidOperationException("Border pane input wrapper aliases changed before source values.");
                for (var owner = 0; owner < slots.Length; owner++) valueField.SetValue(slots[owner], source.PaneStrings[owner]);
                for (var owner = 0; owner < slots.Length; owner++)
                    for (var index = 0; index < fields.Length; index++) {
                        var expected = fields[index] == valueField ? source.PaneStrings[owner] : before[owner][index];
                        if (!codec.SameRawSlot(expected, fields[index].GetValue(slots[owner]), fields[index].FieldType, 0))
                            throw new InvalidOperationException("Border pane scalar/remaining wrapper field readback differs.");
                    }
                if (!admitted()) throw new InvalidOperationException("Border pane scalar source readback lost parked admission.");
            };
        }
        internal static void InstallOriginalFontBaseline(TextMeshPro text, float baseSize)
        {
            if (float.IsNaN(baseSize) || float.IsInfinity(baseSize) || baseSize <= 0) throw new InvalidDataException("Original Border pre-language font baseline differs.");
            typeof(TMP_Text).GetField("m_fontSize", Flags)!.SetValue(text, baseSize);
        }
        internal static void ApplyBeforeOriginalAwake(Data source, IReadOnlyDictionary<string, UnityEngine.Object> directory)
        {
            Validate(source);
            var owners = Owners(directory);
            if (!source.Owners.SequenceEqual(owners.Select(pair => pair.Key))
                || !source.FieldNames.SequenceEqual(owners.SelectMany(pair => Fields(pair.Value.GetType()).Select(field => pair.Key + "/" + field.DeclaringType!.FullName + "/" + field.Name))))
                throw new InvalidDataException("Border original inactive input bindings differ.");
            using var validation = new LoadingEngineClockProbe.CheckpointValidation();
            foreach (var owner in owners) {
                var native = (MonoBehaviour)owner.Value;
                var lifecycle = BoundaryMonoLifecycle.ObserveUnstartedUi(native);
                if (native.gameObject.activeInHierarchy || lifecycle[0] != 0 || lifecycle[1] != 0 || lifecycle[3] != 0 || lifecycle[4] != 0)
                    throw new InvalidOperationException("Border input application requires the real original never-awakened inactive producer.");
            }
            for (var owner = 0; owner < owners.Length; owner++) {
                var fields = Fields(owners[owner].Value.GetType());
                if (fields.Length != source.Values[owner].Length) throw new InvalidDataException("Border source input row length differs.");
                for (var index = 0; index < fields.Length; index++) {
                    var value = source.Values[owner][index]; var type = fields[index].FieldType;
                    if (value == null ? type.IsValueType : !type.IsInstanceOfType(value))
                        throw new InvalidDataException("Border source logical field type differs.");
                    fields[index].SetValue(owners[owner].Value, value);
                }
            }
            for (var index = 0; index < Paths.Length; index++) {
                var text = owners.Select(pair => pair.Value).OfType<TextMeshPro>().Single(owner => directory.Any(pair => pair.Key == "$/" + Paths[index] + ":TMPro.TextMeshPro[0]" && ReferenceEquals(pair.Value, owner)));
                // Original ChangeFont.Awake reads the pre-language base size. Its original OnEnable and Start remain scheduled normally.
                InstallOriginalFontBaseline(text, source.BaseFontSizes[index]);
                var rect = text.GetComponent<RectTransform>(); var saved = source.Rects[index];
                rect.localPosition = saved.Position; rect.localRotation = saved.Rotation; rect.localScale = saved.Scale;
                rect.anchorMin = saved.AnchorMin; rect.anchorMax = saved.AnchorMax; rect.pivot = saved.Pivot;
                rect.sizeDelta = saved.SizeDelta; rect.anchoredPosition = saved.AnchoredPosition;
                text.GetComponent<MeshRenderer>().enabled = source.RendererEnabled[index];
            }
        }
    }
}
