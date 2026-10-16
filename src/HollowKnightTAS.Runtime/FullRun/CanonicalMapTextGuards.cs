using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Raw, bounded audit of the exact font/settings graph read by original TMP.
    // Unity asset handles remain strong aliases; no lazy property is evaluated.
    internal sealed class MapFontReadGuard
    {
        private readonly List<Action> checks = new List<Action>();
        private readonly HashSet<object> seen = new HashSet<object>(new MapFallbackGuard.MaterialReferences());
        internal MapFontReadGuard(TextMeshPro text)
        {
            var font = CanonicalMapTextStartup.Raw(text, "m_fontAsset") as TMP_FontAsset
                ?? throw new InvalidOperationException("Map TMP font is absent.");
            var settings = CanonicalMapTextStartup.Static(typeof(TMP_Settings), "s_Instance");
            if (font.GetType() != typeof(TMP_FontAsset) || settings.GetType() != typeof(TMP_Settings))
                throw new InvalidOperationException("Map TMP original font/settings owner ABI differs.");
            if (!(bool)CanonicalMapTextStartup.Static(typeof(ShaderUtilities), "isInitialized"))
                throw new InvalidOperationException("Map TMP shader IDs have an unqualified lazy initialization.");
            foreach (var field in typeof(ShaderUtilities).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where(field => !field.IsLiteral)) {
                var before = field.GetValue(null);
                checks.Add(() => { if (!Same(before, field.GetValue(null))) throw new InvalidOperationException("Map TMP shader ID/configuration changed."); });
            }
            RequireReadPath(text, font, settings);
            checks.Add(() => { if (!ReferenceEquals(settings, CanonicalMapTextStartup.Static(typeof(TMP_Settings), "s_Instance")))
                throw new InvalidOperationException("Map TMP settings singleton changed."); });
            Visit(font, 0); Visit(settings, 0);
        }
        // Corresponds to original SetArraySizes IL_220..IL_6ff and
        // TMP_Text.GetFontAssetForWeight. This only admits successful ordinary
        // character lookup; markup, escapes and missing-glyph branches reject.
        private static void RequireReadPath(TextMeshPro text, TMP_FontAsset font, object settings)
        {
            var input = CanonicalMapTextStartup.Raw(text, "m_text") as string;
            var inputSource = CanonicalMapTextStartup.Raw(text, "m_inputSource")?.ToString();
            if (string.IsNullOrEmpty(input) || input!.Length > 2048 || input.Any(value => value == '<' || value == '\\'
                || value == '\0' || char.IsSurrogate(value)) || inputSource != "Text" && inputSource != "String")
                throw new InvalidOperationException("Map TMP input requires an unqualified markup/escape/Unicode branch.");
            var style = (FontStyles)CanonicalMapTextStartup.Raw(text, "m_fontStyle")!;
            var overflow = (TextOverflowModes)CanonicalMapTextStartup.Raw(text, "m_overflowMode")!;
            if (((bool)CanonicalMapTextStartup.Raw(text, "m_enableWordWrapping")! || overflow == TextOverflowModes.Truncate || overflow == TextOverflowModes.Ellipsis)
                && input.Any(code => code > 4352 && code < 4607 || code > 11904 && code < 40959 || code > 43360 && code < 43391
                    || code > 44032 && code < 55295 || code > 63744 && code < 64255 || code > 65072 && code < 65103 || code > 65280 && code < 65519)) {
                var rules = CanonicalMapTextStartup.Raw(settings, "m_linebreakingRules");
                if (rules == null || CanonicalMapTextStartup.Raw(rules, "leadingCharacters") == null || CanonicalMapTextStartup.Raw(rules, "followingCharacters") == null)
                    throw new InvalidOperationException("Map TMP CJK wrapping requires unqualified lazy linebreaking rules.");
            }
            var weight = (style & FontStyles.Bold) == FontStyles.Bold ? 700 : (int)CanonicalMapTextStartup.Raw(text, "m_fontWeight")!;
            var weights = CanonicalMapTextStartup.Raw(font, "fontWeights") as Array;
            if (weights == null || weight < 100 || weight > 900 || weights.Length <= weight / 100)
                throw new InvalidOperationException("Map TMP original weight table differs.");
            var entry = weights.GetValue(weight / 100)!;
            var alternate = CanonicalMapTextStartup.Raw(entry, (style & FontStyles.Italic) == FontStyles.Italic ? "italicTypeface" : "regularTypeface");
            var current = alternate == null ? font : alternate as TMP_FontAsset
                ?? throw new InvalidOperationException("Map TMP alternate typeface has an unknown owner.");
            RequireInitialized(font);
            foreach (var character in input) {
                var code = character;
                if ((style & FontStyles.UpperCase) == FontStyles.UpperCase) { if (char.IsLower(code)) code = char.ToUpper(code); }
                else if ((style & FontStyles.LowerCase) == FontStyles.LowerCase) { if (char.IsUpper(code)) code = char.ToLower(code); }
                else if ((style & FontStyles.SmallCaps) == FontStyles.SmallCaps && char.IsLower(code)) code = char.ToUpper(code);
                if (RequireInitialized(current).Contains((int)code)) continue;
                if (Find(CanonicalMapTextStartup.Raw(current, "fallbackFontAssets"), code)) continue;
                if (Find(CanonicalMapTextStartup.Raw(settings, "m_fallbackFontAssets"), code)) continue;
                // The original next tries case replacement then missing-glyph
                // fallbacks. Those branches have no admitted real input here.
                throw new InvalidOperationException("Map TMP original input needs an unqualified missing-glyph/case branch: " + (int)code);
            }
        }
        private static IDictionary RequireInitialized(TMP_FontAsset font)
        {
            var dictionary = CanonicalMapTextStartup.Raw(font, "m_characterDictionary") as IDictionary;
            if (dictionary == null || CanonicalMapTextStartup.Raw(font, "m_kerningDictionary") == null)
                throw new InvalidOperationException("Map TMP actually read font has an uninitialized lazy dictionary: hash="
                    + CanonicalMapTextStartup.Raw(font, "hashCode") + ";charactersNull=" + (dictionary == null)
                    + ";kerningNull=" + (CanonicalMapTextStartup.Raw(font, "m_kerningDictionary") == null));
            return dictionary;
        }
        private static bool Find(object? candidates, char code)
        {
            if (candidates == null) return false;
            if (!(candidates is IList fonts) || fonts.Count > 64) throw new InvalidOperationException("Map fallback font list bound/type differs.");
            foreach (var value in fonts) {
                if (value == null) continue;
                if (!(value is TMP_FontAsset font)) throw new InvalidOperationException("Map fallback font owner type differs.");
                if (RequireInitialized(font).Contains((int)code)) return true;
            }
            return false;
        }
        private void Visit(object? value, int depth)
        {
            if (value == null || value is string || value.GetType().IsPrimitive || value.GetType().IsEnum) return;
            if (depth > 24 || seen.Count > 20000) throw new InvalidOperationException("Map font read graph exceeds fixed bound.");
            var type = value.GetType();
            if (!type.IsValueType && !seen.Add(value)) return;
            if (value is UnityEngine.Object && !(value is TMP_FontAsset) && !(value is TMP_Settings)) return;
            if (value is IDictionary dictionary) {
                if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Dictionary<,>))
                    throw new InvalidOperationException("Map TMP font dictionary concrete ABI differs.");
                var rows = MapFallbackGuard.Entries(dictionary);
                checks.Add(() => {
                    if (dictionary.Count != rows.Length || rows.Any(row => !dictionary.Contains(row.Key) || !Same(row.Value, dictionary[row.Key])))
                        throw new InvalidOperationException("Map TMP font dictionary mutated.");
                });
                foreach (var row in rows) { Visit(row.Key, depth + 1); Visit(row.Value, depth + 1); }
                return;
            }
            if (value is IList list) {
                if (!type.IsArray && (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(List<>)))
                    throw new InvalidOperationException("Map TMP font list concrete ABI differs.");
                var items = list.Cast<object?>().ToArray();
                checks.Add(() => {
                    if (list.Count != items.Length || items.Where((item, index) => !Same(item, list[index])).Any())
                        throw new InvalidOperationException("Map TMP font list mutated.");
                });
                foreach (var item in items) Visit(item, depth + 1); return;
            }
            if (type.Namespace != "TMPro" && type.Namespace != "UnityEngine")
                throw new InvalidOperationException("Unknown Map font read graph type: " + type.FullName);
            for (var current = type; current != null && current != typeof(UnityEngine.Object); current = current.BaseType) {
                foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
                    var before = field.GetValue(value);
                    checks.Add(() => { if (!Same(before, field.GetValue(value))) throw new InvalidOperationException("Map font raw field mutated: " + field.Name); });
                    Visit(before, depth + 1);
                }
            }
        }
        internal static bool Same(object? left, object? right)
        {
            if (left == null || right == null) return left == null && right == null;
            if (left.GetType() != right.GetType()) return false;
            if (left is float single) return BitConverter.GetBytes(single).SequenceEqual(BitConverter.GetBytes((float)right));
            if (left is double number) return BitConverter.DoubleToInt64Bits(number) == BitConverter.DoubleToInt64Bits((double)right);
            var type = left.GetType();
            if (type.IsPrimitive || type.IsEnum || left is string) return left.Equals(right);
            if (!type.IsValueType) return ReferenceEquals(left, right);
            return type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .All(field => Same(field.GetValue(left), field.GetValue(right)));
        }
        internal void Verify() { foreach (var check in checks) check(); }
    }

    internal sealed class MapFallbackGuard
    {
        internal sealed class MaterialReferences : IEqualityComparer<object>
        {
            public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
            public int GetHashCode(object value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
        }
        internal static DictionaryEntry[] Entries(IDictionary dictionary)
        {
            var result = new List<DictionaryEntry>();
            var iterator = dictionary.GetEnumerator();
            while (iterator.MoveNext()) result.Add(iterator.Entry);
            return result.ToArray();
        }
        private sealed class Row
        {
            internal object Owner = null!; internal object? Base, Material; internal int BaseId, Count;
            internal Row(object owner) {
                Owner = owner; Base = Raw(owner, "baseMaterial"); Material = Raw(owner, "fallbackMaterial");
                BaseId = (int)Raw(owner, "baseID")!; Count = (int)Raw(owner, "count")!;
            }
        }
        private static object? Raw(object owner, string name) => CanonicalMapTextStartup.Raw(owner, name);
        private static object Slot(string name)
        {
            var value = CanonicalMapTextStartup.Static(typeof(TMP_MaterialManager), name);
            var type = typeof(TMP_MaterialManager).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.FieldType;
            if (value.GetType() != type) throw new InvalidOperationException("Map TMP material cache concrete ABI differs.");
            return value;
        }
        private readonly IDictionary materials = (IDictionary)Slot("m_fallbackMaterials");
        private readonly IDictionary lookup = (IDictionary)Slot("m_fallbackMaterialLookup");
        private readonly IList cleanup = (IList)Slot("m_fallbackCleanupList");
        private readonly IList masking = (IList)Slot("m_materialList");
        private readonly Dictionary<object, Row> rows;
        private readonly Dictionary<object, object?> lookupRows;
        private readonly object[] cleanupRows, maskingRows;
        private readonly List<Action> maskingChecks = new List<Action>();
        private readonly List<Action> materialChecks = new List<Action>();
        private readonly HashSet<object> observedMaterials = new HashSet<object>(new MaterialReferences());
        private readonly Dictionary<object, int> subReferences;
        private readonly object baseMaterial;
        internal MapFallbackGuard(TextMeshPro text)
        {
            baseMaterial = Raw(text, "m_sharedMaterial") ?? throw new InvalidOperationException("Map base material absent.");
            rows = Entries(materials).ToDictionary(row => row.Key, row => new Row(row.Value!));
            lookupRows = Entries(lookup).ToDictionary(row => row.Key, row => (object?)row.Value);
            cleanupRows = cleanup.Cast<object>().ToArray(); maskingRows = masking.Cast<object>().ToArray();
            foreach (var item in maskingRows)
                foreach (var field in item.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
                    var before = field.GetValue(item);
                    maskingChecks.Add(() => {
                        var current = field.GetValue(item);
                        if (field.FieldType.IsValueType ? !Equals(before, current) : !ReferenceEquals(before, current))
                            throw new InvalidOperationException("Map TMP unrelated masking material row mutated.");
                    });
                }
            subReferences = References(text);
            ObserveMaterial(baseMaterial);
            foreach (var row in rows.Values) { ObserveMaterial(row.Base); ObserveMaterial(row.Material); }
            if (Raw(text, "m_subTextObjects") is Array children)
                foreach (var child in children) if (child != null) ObserveMaterial(Raw(child, "m_sharedMaterial"));
        }
        private void ObserveMaterial(object? value)
        {
            if (value == null || !observedMaterials.Add(value)) return;
            if (!(value is Material material)) throw new InvalidOperationException("Map TMP existing material concrete owner differs.");
            var pointer = Raw(material, "m_CachedPtr");
            materialChecks.Add(() => { if (!Equals(pointer, Raw(material, "m_CachedPtr")))
                throw new InvalidOperationException("Map TMP existing material native lifetime changed."); });
            if ((IntPtr)pointer! == IntPtr.Zero) return;
            // Exact existing-material reads/writes reachable through original
            // GetPadding/UpdateShaderRatios and UpdateMask. No renderer.material
            // getter, property creation, texture enumeration or setters here.
            var shader = material.shader; var keywords = material.shaderKeywords; var queue = material.renderQueue;
            var floats = new[] { "_ScaleRatioA", "_ScaleRatioB", "_ScaleRatioC", "_GradientScale", "_FaceDilate", "_OutlineWidth",
                "_OutlineSoftness", "_WeightNormal", "_WeightBold", "_GlowOffset", "_GlowOuter", "_UnderlayOffsetX",
                "_UnderlayOffsetY", "_UnderlayDilate", "_UnderlaySoftness", "_MaskSoftnessX", "_MaskSoftnessY", "_TextureWidth", "_TextureHeight" }
                .ToDictionary(name => name, name => Tuple.Create(material.HasProperty(name), material.HasProperty(name) ? material.GetFloat(name) : 0f));
            var clipPresent = material.HasProperty("_ClipRect"); var clip = clipPresent ? material.GetVector("_ClipRect") : default;
            materialChecks.Add(() => {
                if (!ReferenceEquals(shader, material.shader) || !keywords.SequenceEqual(material.shaderKeywords) || queue != material.renderQueue
                    || floats.Any(pair => material.HasProperty(pair.Key) != pair.Value.Item1
                        || pair.Value.Item1 && !MapFontReadGuard.Same(pair.Value.Item2, material.GetFloat(pair.Key)))
                    || material.HasProperty("_ClipRect") != clipPresent || clipPresent && !MapFontReadGuard.Same(clip, material.GetVector("_ClipRect")))
                    throw new InvalidOperationException("Map TMP modified an existing material shader/configuration.");
            });
        }
        private static Dictionary<object, int> References(TextMeshPro text)
        {
            var result = new Dictionary<object, int>(new MaterialReferences());
            var children = Raw(text, "m_subTextObjects") as Array;
            if (children == null) return result;
            if (children.Length > 16) throw new InvalidOperationException("Map TMP submesh bound exceeded.");
            foreach (var child in children) {
                if (child == null) continue;
                if (!(child is TMP_SubMesh sub) || !ReferenceEquals(Raw(sub, "m_TextComponent"), text))
                    throw new InvalidOperationException("Map TMP submesh alias differs.");
                var material = Raw(child, "m_fallbackMaterial");
                if (material == null) continue;
                // Destroyed wrappers remain references, never treated as null.
                result[material] = result.TryGetValue(material, out var count) ? count + 1 : 1;
            }
            return result;
        }
        internal void Verify(TextMeshPro text)
        {
            foreach (var check in maskingChecks) check();
            foreach (var check in materialChecks) check();
            if (!ReferenceEquals(materials, Slot("m_fallbackMaterials")) || !ReferenceEquals(lookup, Slot("m_fallbackMaterialLookup"))
                || !ReferenceEquals(cleanup, Slot("m_fallbackCleanupList")) || !ReferenceEquals(masking, Slot("m_materialList"))
                || !cleanup.Cast<object>().SequenceEqual(cleanupRows) || !masking.Cast<object>().SequenceEqual(maskingRows))
                throw new InvalidOperationException("Map TMP unrelated material cache/cleanup changed.");
            var after = References(text);
            foreach (var pair in rows) {
                if (!materials.Contains(pair.Key)) throw new InvalidOperationException("Map TMP removed existing fallback row.");
                var row = new Row(materials[pair.Key]!); var old = pair.Value;
                int Count(Dictionary<object, int> values) => old.Material != null && values.TryGetValue(old.Material, out var value) ? value : 0;
                var delta = Count(after) - Count(subReferences);
                if (!ReferenceEquals(old.Owner, row.Owner) || !ReferenceEquals(old.Base, row.Base) || !ReferenceEquals(old.Material, row.Material)
                    || old.BaseId != row.BaseId || row.Count != old.Count + delta || delta != 0 && !ReferenceEquals(old.Base, baseMaterial))
                    throw new InvalidOperationException("Map TMP existing fallback row/reference delta differs.");
            }
            foreach (DictionaryEntry pair in materials) {
                if (rows.ContainsKey(pair.Key)) continue;
                var row = new Row(pair.Value!);
                if (!ReferenceEquals(row.Base, baseMaterial) || !(row.Material is Material material)
                    || !after.TryGetValue(material, out var count) || count != row.Count || count < 1
                    || row.BaseId != ((Material)baseMaterial).GetInstanceID() || !lookup.Contains(material.GetInstanceID())
                    || !Equals(lookup[material.GetInstanceID()], pair.Key))
                    throw new InvalidOperationException("Map TMP new fallback row is not owned by this producer.");
            }
            foreach (var pair in lookupRows)
                if (!lookup.Contains(pair.Key) || !Equals(pair.Value, lookup[pair.Key])) throw new InvalidOperationException("Map TMP prior fallback lookup changed.");
            if (lookup.Count != lookupRows.Count + materials.Count - rows.Count)
                throw new InvalidOperationException("Map TMP fallback lookup coverage differs.");
        }
    }
}
