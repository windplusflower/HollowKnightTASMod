using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Read only the original fifteen Wide Map labels and Marker. No TMP
    // property, force-update, FSM Actions getter or lifecycle call is allowed.
    internal static class LoadingMapLifecycleObservation
    {
        private static readonly FieldInfo CachedPointer = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly string[] Areas = { "Ancient Basin", "City", "Cliffs", "Crossroads", "Deepnest", "Fog Canyon",
            "Fungal Wastes", "Greenpath", "Hive", "Mines", "Outskirts", "Resting Grounds", "Royal Gardens", "Town", "Waterways" };
        private static object? Read(object owner, string name)
        {
            for (var type = owner.GetType(); type != null; type = type.BaseType) {
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(owner);
            }
            throw new InvalidOperationException("Map observation original field absent: " + owner.GetType().FullName + "/" + name);
        }
        private static object? Asset(object? value)
        {
            if (value == null) return null;
            if (!(value is UnityEngine.Object asset)) throw new InvalidOperationException("Map original asset slot has an unexpected type.");
            var destroyed = (IntPtr)CachedPointer.GetValue(asset)! == IntPtr.Zero;
            return new { type = asset.GetType().FullName, assembly = asset.GetType().Assembly.GetName().Name,
                destroyed, name = destroyed ? null : asset.name };
        }
        private static int[] Native(MonoBehaviour owner) => BoundaryMonoLifecycle.ObserveUnstartedUi(owner).Select(value => (int)value).ToArray();
        internal static object Capture()
        {
            var cameras = typeof(GameCameras).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) as GameCameras;
            var map = cameras == null ? null : cameras.hudCamera.transform.Find("Inventory/Map/World Map");
            if (map == null) return new { available = false, diagnosticOnly = true };
            var wide = map.Find("Wide Map") ?? throw new InvalidOperationException("Original Wide Map is absent.");
            using var validation = new LoadingEngineClockProbe.CheckpointValidation();
            var labels = Areas.Select(area => {
                try {
                var parent = wide.Find(area) ?? throw new InvalidOperationException("Original Map area is absent: " + area);
                var owner = parent.Find("Area Name") ?? throw new InvalidOperationException("Original Map area label is absent: " + area);
                var text = owner.GetComponent<TextMeshPro>() ?? throw new InvalidOperationException("Original Map TMP label is absent: " + area);
                var rawText = Read(text, "m_text") as string;
                var children = (Array?)Read(text, "m_subTextObjects");
                if (rawText?.Length > 2048 || children?.Length > 16) throw new InvalidOperationException("Map diagnostic exceeds its original fixed bounds.");
                var language = owner.GetComponent<ChangeFontByLanguage>() ?? throw new InvalidOperationException("Original Map font owner is absent.");
                var localized = owner.GetComponent<SetTextMeshProGameText>() ?? throw new InvalidOperationException("Original Map text owner is absent.");
                var info = Read(text, "m_textInfo");
                object? SubMesh(object? child, int index) {
                    if (child == null) return null;
                    if (!(child is TMP_SubMesh component)) throw new InvalidOperationException("Map raw submesh has an unknown concrete owner.");
                    if ((IntPtr)CachedPointer.GetValue(component)! == IntPtr.Zero) return new { index, destroyed = true };
                    return new { index, destroyed = false, name = component.name, nativeLifecycle = Native(component),
                        activeSelf = component.gameObject.activeSelf, textAlias = ReferenceEquals(Read(child, "m_TextComponent"), text),
                        mesh = Asset(Read(child, "m_mesh")), sharedMaterial = Asset(Read(child, "m_sharedMaterial")),
                        fallbackMaterial = Asset(Read(child, "m_fallbackMaterial")) };
                }
                return new { area, activeSelf = owner.gameObject.activeSelf, activeInHierarchy = owner.gameObject.activeInHierarchy,
                    parentActive = parent.gameObject.activeSelf, nativeLifecycle = Native(text), rawText, isAwake = Read(text, "m_isAwake"),
                    inputParsingRequired = Read(text, "m_isInputParsingRequired"), propertiesChanged = Read(text, "m_havePropertiesChanged"),
                    ignoreActiveState = Read(text, "m_ignoreActiveState"), font = Asset(Read(text, "m_fontAsset")),
                    sharedMaterial = Asset(Read(text, "m_sharedMaterial")), mesh = Asset(Read(text, "m_mesh")),
                    materialCount = info == null ? null : Read(info, "materialCount"),
                    fontOwner = new { nativeLifecycle = Native(language), onlyOnStart = Read(language, "onlyOnStart"),
                        currentTextAlias = ReferenceEquals(Read(language, "tmpro"), text), defaultFont = Asset(Read(language, "defaultFont")),
                        fontZH = Asset(Read(language, "fontZH")), fontRU = Asset(Read(language, "fontRU")) },
                    textOwner = new { nativeLifecycle = Native(localized), currentTextAlias = ReferenceEquals(Read(localized, "textMesh"), text),
                        sheetName = Read(localized, "sheetName"), convName = Read(localized, "convName") },
                    subMeshes = children?.Cast<object?>().Select(SubMesh).ToArray() };
                } catch (Exception error) {
                    throw new InvalidOperationException("Original Map read-only area " + area + " failed: " + error, error);
                }
            }).ToArray();
            var marker = map.Find("Map Marker Action") ?? throw new InvalidOperationException("Original Map marker is absent.");
            var deactivate = marker.GetComponent<DeactivateIfPlayerdataFalse>() ?? throw new InvalidOperationException("Original Map marker gate is absent.");
            return new { available = true, diagnosticOnly = true, wideActiveSelf = wide.gameObject.activeSelf,
                currentHasMarker = PlayerData.instance.hasMarker, labels,
                marker = new { activeSelf = marker.gameObject.activeSelf, nativeLifecycle = Native(deactivate), boolName = Read(deactivate, "boolName"),
                    playerPresent = Read(deactivate, "pd") != null, playerIsCurrent = ReferenceEquals(Read(deactivate, "pd"), PlayerData.instance) } };
        }
    }
}
