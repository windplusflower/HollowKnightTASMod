using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Glyphs are shared ORIGINAL font-asset members, not owned text caches.
    // Rebind the actual list/dictionary slot, preserving all alias sites and
    // every glyph field. No font getter, cache creation, cloning or callback.
    internal sealed class CanonicalFinalVisualGlyphs
    {
        internal const string Prefix = CanonicalFinalVisualCarry.Binding + "glyph/";
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly Dictionary<string, TMP_Glyph> slots;
        private readonly Dictionary<object, string[]> aliases = new Dictionary<object, string[]>(new References());
        private readonly Dictionary<object, string> hashes = new Dictionary<object, string>(new References());
        internal CanonicalFinalVisualGlyphs() : this(OriginalSlots()) { }
        internal CanonicalFinalVisualGlyphs(IReadOnlyDictionary<string, TMP_Glyph> source)
        {
            var actualMvid = typeof(TMP_Glyph).Module.ModuleVersionId.ToString("D");
            if (actualMvid != CanonicalSceneMappingCarry.ModuleMvid)
                CanonicalSceneMappingCarry.WarnOnce("FinalVisualGlyphs:Mvid:" + actualMvid,
                    "[HKTAS] TMP_Glyph ModuleVersionId differs from pinned original (continuing): actual=" + actualMvid);
            if (source.Count > 100000)
                throw new InvalidDataException("Final visual original glyph module/slot bound differs.");
            slots = source.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            foreach (var group in slots.GroupBy(pair => (object)pair.Value, new References()))
                aliases.Add(group.Key, group.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray());
        }
        private static SortedDictionary<string, TMP_Glyph> OriginalSlots()
        {
            using var validation = LoadingEngineClockProbe.ReadOnlyCheckpointScope();
            var fonts = new HashSet<TMP_FontAsset>();
            using (var reader = new LoadingFieldDigest(value => {
                if (value is TMP_FontAsset font) fonts.Add(font);
                return value is UnityEngine.Object || value is TMP_Glyph ? "original-resource" : null;
            }, type => type == typeof(UnityEngine.Object) || type == typeof(MonoBehaviour))) {
                foreach (var pair in CanonicalFinalVisualCarry.Directory()) {
                    var observed = reader.CaptureReferences(pair.Value, pair.Key, expandRoot: true);
                    if (observed.Unsupported.Count != 0) throw new InvalidDataException("Final visual original font reference walk incomplete.");
                }
            }
            var pending = new Queue<TMP_FontAsset>(fonts);
            var fallback = typeof(TMP_FontAsset).GetField("fallbackFontAssets", Flags)!;
            while (pending.Count != 0) {
                if (fonts.Count > 128) throw new InvalidDataException("Final visual original font closure exceeds bound.");
                var font = pending.Dequeue(); var list = fallback.GetValue(font) as IList;
                if (list == null) continue;
                if (list.Count > 128) throw new InvalidDataException("Final visual fallback list exceeds bound.");
                foreach (TMP_FontAsset item in list) if (item != null && fonts.Add(item)) pending.Enqueue(item);
            }
            var origins = new LoadingAssetProvenanceObservation();
            var rows = fonts.Select(font => {
                var origin = JObject.FromObject(origins.Capture(font.GetInstanceID()));
                if ((string?)origin["kind"] != "serialized") throw new InvalidDataException("Final visual font has no original serialized provenance.");
                return new { font, key = (string)origin["sourcePath"]! + ":" + (long)origin["localIdentifier"]! };
            }).OrderBy(row => row.key, StringComparer.Ordinal).ToArray();
            var result = new SortedDictionary<string, TMP_Glyph>(StringComparer.Ordinal);
            void Add(string key, TMP_Glyph glyph) {
                if (glyph == null) return;
                if (result.Count >= 100000 || result.TryGetValue(key, out var previous) && !ReferenceEquals(previous, glyph))
                    throw new InvalidDataException("Final visual original glyph slot bound/identity collision.");
                result[key] = glyph;
            }
            var glyphList = typeof(TMP_FontAsset).GetField("m_glyphInfoList", Flags)!;
            var characters = typeof(TMP_FontAsset).GetField("m_characterDictionary", Flags)!;
            foreach (var row in rows) {
                if (glyphList.GetValue(row.font) is IList list) {
                    if (list.Count > 100000) throw new InvalidDataException("Final visual original glyph list exceeds bound.");
                    for (var i = 0; i < list.Count; i++) Add(row.key + "/list/" + i, (TMP_Glyph)list[i]);
                }
                if (characters.GetValue(row.font) is IDictionary dictionary) {
                    if (dictionary.Count > 100000) throw new InvalidDataException("Final visual original character dictionary exceeds bound.");
                    foreach (DictionaryEntry entry in dictionary) Add(row.key + "/character/" + (int)entry.Key, (TMP_Glyph)entry.Value);
                }
            }
            return result;
        }
        private string Hash(TMP_Glyph glyph)
        {
            if (hashes.TryGetValue(glyph, out var known)) return known;
            using (var reader = new LoadingFieldDigest(_ => null, _ => false)) {
                var observed = reader.Capture(glyph, "original-font-glyph", expandRoot: true);
                if (observed.Unsupported.Count != 0) throw new InvalidDataException("Original glyph full field fingerprint incomplete.");
                hashes.Add(glyph, observed.Sha256); return observed.Sha256;
            }
        }
        private string AliasHash(TMP_Glyph glyph) => HollowKnightTAS.Core.Cryptography.Sha256Utility.ComputeHex(Encoding.UTF8.GetBytes(string.Join("\n", aliases[glyph])));
        private static string Encode(string key) => Convert.ToBase64String(Encoding.UTF8.GetBytes(key)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        private static string Decode(string key) { var text = key.Replace('-', '+').Replace('_', '/'); return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(text.PadRight((text.Length + 3) / 4 * 4, '='))); }
        internal string? Bind(object value)
        {
            if (!(value is TMP_Glyph glyph)) return null;
            if (!aliases.TryGetValue(glyph, out var sites) || sites.Length == 0) throw new InvalidDataException("Referenced glyph has no exact original font slot.");
            return Prefix + Encode(sites[0]) + "/" + Hash(glyph) + "/" + AliasHash(glyph);
        }
        internal object Resolve(string binding)
        {
            if (!binding.StartsWith(Prefix, StringComparison.Ordinal) || binding.Length > 32768) throw new InvalidDataException("Original glyph binding prefix/bound differs.");
            var parts = binding.Substring(Prefix.Length).Split('/');
            if (parts.Length != 3 || !slots.TryGetValue(Decode(parts[0]), out var glyph) || Hash(glyph) != parts[1] || AliasHash(glyph) != parts[2])
                throw new InvalidDataException("Original glyph source value, provenance slot or complete aliases differ.");
            return glyph;
        }
        private sealed class References : IEqualityComparer<object>
        {
            public new bool Equals(object? a, object? b) => ReferenceEquals(a, b);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
