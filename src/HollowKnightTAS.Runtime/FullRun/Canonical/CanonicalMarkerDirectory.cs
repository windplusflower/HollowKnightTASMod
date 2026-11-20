using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // The original level1/2077 owner has a grandchild. Never pass it through
    // the Geo directory's four-direct-children admission.
    internal static class CanonicalMarkerDirectory
    {
        private static readonly string[] Paths = { "$", "$/ActionButtonIcon", "$/ActionButtonIcon/Label", "$/Backboard", "$/Text" };
        private static readonly Type[][] Components = {
            new[] { typeof(Transform), typeof(PlayMakerFSM), typeof(DeactivateIfPlayerdataFalse) },
            new[] { typeof(Transform), typeof(SpriteRenderer), typeof(MenuButtonIcon) },
            new[] { typeof(RectTransform), typeof(TextContainer), typeof(MeshRenderer), typeof(MeshFilter), typeof(TextMeshPro), typeof(CanvasRenderer) },
            new[] { typeof(Transform), typeof(SpriteRenderer) },
            new[] { typeof(RectTransform), typeof(TextContainer), typeof(MeshFilter), typeof(MeshRenderer), typeof(CanvasRenderer), typeof(TextMeshPro), typeof(ChangeFontByLanguage) }
        };
        internal static GameObject Current()
        {
            var cameras = GameCameras.instance;
            if (cameras == null || cameras.hudCamera == null) throw new InvalidDataException("Original Marker HUD owner is absent.");
            var marker = cameras.hudCamera.transform.Find("Inventory/Map/World Map/Map Marker Action");
            return marker == null ? throw new InvalidDataException("Original Marker owner is absent.") : marker.gameObject;
        }
        internal static SortedDictionary<string, UnityEngine.Object> Build(GameObject root)
        {
            if (ReferenceEquals(root, null) || root == null || root.name != "Map Marker Action")
                throw new InvalidDataException("Original Marker root identity differs.");
            var nodes = root.GetComponentsInChildren<Transform>(true);
            if (nodes.Length != Paths.Length) throw new InvalidDataException("Original Marker five-owner directory differs.");
            var result = new SortedDictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
            for (var index = 0; index < Paths.Length; index++) {
                var path = Paths[index];
                var node = index == 0 ? root.transform : root.transform.Find(path.Substring(2));
                if (node == null || !nodes.Any(item => ReferenceEquals(item, node))
                    || index != 0 && !ReferenceEquals(node.parent, path == "$/ActionButtonIcon/Label"
                        ? root.transform.Find("ActionButtonIcon") : root.transform))
                    throw new InvalidDataException("Original Marker hierarchy differs: " + path);
                var components = node.GetComponents<Component>();
                if (components.Any(component => ReferenceEquals(component, null) || component == null)
                    || !components.Select(component => component.GetType()).SequenceEqual(Components[index]))
                    throw new InvalidDataException("Original Marker complete component order differs: " + path);
                result.Add(path, node.gameObject);
                foreach (var component in components) result.Add(path + ":" + component.GetType().FullName + "[0]", component);
            }
            if (result.Values.OfType<MonoBehaviour>().Count() != 8)
                throw new InvalidDataException("Original Marker eight-Mono directory differs.");
            return result;
        }
    }
}
