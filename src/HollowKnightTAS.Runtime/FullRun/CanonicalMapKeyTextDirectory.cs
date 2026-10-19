using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Own only the nine serialized Text leaves. Their PD-controlled Key
    // parents, Map Key Control FSM, preferences and pin layout stay external.
    internal static class CanonicalMapKeyTextDirectory
    {
        internal static readonly string[] Keys = { "Black Egg", "Cocoon", "Dream Plant", "Dreamer", "Ghost", "Grub", "Key Stag", "Spa", "Tram" };
        private static readonly Type[] Components = { typeof(RectTransform), typeof(TextContainer), typeof(MeshFilter),
            typeof(SetTextMeshProGameText), typeof(MeshRenderer), typeof(CanvasRenderer), typeof(TextMeshPro), typeof(ChangeFontByLanguage) };
        internal static Transform Current()
        {
            var cameras = GameCameras.instance;
            if (cameras == null || cameras.hudCamera == null) throw new InvalidDataException("Original Map Key HUD is absent.");
            var keys = cameras.hudCamera.transform.Find("Inventory/Map Key/Keys");
            return keys == null ? throw new InvalidDataException("Original Map Key directory is absent.") : keys;
        }
        internal static SortedDictionary<string, UnityEngine.Object> Build(Transform keys, IReadOnlyList<GameObject>? leaves = null)
        {
            if (ReferenceEquals(keys, null) || keys == null || keys.name != "Keys" || keys.parent == null || keys.parent.name != "Map Key")
                throw new InvalidDataException("Original Map Key parent identity differs.");
            var result = new SortedDictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
            for (var leafIndex = 0; leafIndex < Keys.Length; leafIndex++) {
                var key = Keys[leafIndex];
                var parents = Enumerable.Range(0, keys.childCount).Select(keys.GetChild).Where(node => node.name == key).ToArray();
                if (parents.Length != 1) throw new InvalidDataException("Original Map Key parent is missing or ambiguous: " + key);
                var parent = parents[0];
                var texts = leaves == null ? Enumerable.Range(0, parent.childCount).Select(parent.GetChild).Where(node => node.name == "Text").ToArray()
                    : leaves.Count == 9 && leaves[leafIndex] != null && ReferenceEquals(leaves[leafIndex].transform.parent, parent) && leaves[leafIndex].name == "Text"
                        ? new[] { leaves[leafIndex].transform } : Array.Empty<Transform>();
                if (texts.Length != 1 || texts[0].childCount != 0)
                    throw new InvalidDataException("Original Map Key Text is missing, ambiguous or has a generated child: " + key);
                var text = texts[0]; var components = text.GetComponents<Component>();
                if (components.Any(component => ReferenceEquals(component, null) || component == null)
                    || !components.Select(component => component.GetType()).SequenceEqual(Components))
                    throw new InvalidDataException("Original Map Key Text complete component order differs: " + key);
                var path = "$/" + key + "/Text";
                result.Add(path, text.gameObject);
                foreach (var component in components) result.Add(path + ":" + component.GetType().FullName + "[0]", component);
            }
            if (result.Values.OfType<MonoBehaviour>().Count() != 36)
                throw new InvalidDataException("Original Map Key thirty-six-Mono directory differs.");
            return result;
        }
    }
}
