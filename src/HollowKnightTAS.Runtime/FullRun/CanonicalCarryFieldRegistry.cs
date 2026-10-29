using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HutongGames.PlayMaker;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalCarryFieldRegistry
    {
        private const string Resource = "HollowKnightTAS.Runtime.FullRun.CanonicalCarryFieldRegistry.json";
        private const string EventResource = "HollowKnightTAS.Runtime.FullRun.CanonicalCarryEventFieldRegistry.json";
        private const string InputResource = "HollowKnightTAS.Runtime.FullRun.CanonicalCarryInputFieldRegistry.json";
        private const string LoadingResource = "HollowKnightTAS.Runtime.FullRun.CanonicalCarryLoadingFieldRegistry.json";
        private const string GeoResource = "HollowKnightTAS.Runtime.FullRun.CanonicalCarryGeoFieldRegistry.json";
        private static readonly string[] NativeValues = { "UnityEngine.Vector2", "UnityEngine.Vector3", "UnityEngine.Vector4",
            "UnityEngine.Quaternion", "UnityEngine.Color", "UnityEngine.Color32", "UnityEngine.Rect", "UnityEngine.Bounds",
            "UnityEngine.LayerMask", "UnityEngine.Matrix4x4", "UnityEngine.RaycastHit" };
        private static readonly FieldInfo RaycastCollider = typeof(RaycastHit).GetField("m_Collider", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidDataException("Pinned RaycastHit collider binding is absent.");
        private static readonly FieldInfo[] LocalEventFields = new[] { "name", "isGlobal", "isSystemEvent", "<Path>k__BackingField" }
            .Select(name => typeof(FsmEvent).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidDataException("Pinned FsmEvent field binding is absent: " + name)).ToArray();

        internal static CanonicalCarryGraphCodec Create(bool inputDomain = false, bool geoDomain = false, bool mapDomain = false, bool markerDomain = false,
            bool mapKeyTextDomain = false, bool mapKeyParentDomain = false, bool hudPresentationDomain = false, bool hudBorderInputDomain = false, bool soulDomain = false, bool persistentSpriteDomain = false)
        {
            if ((inputDomain ? 1 : 0) + (geoDomain ? 1 : 0) + (mapDomain ? 1 : 0) + (markerDomain ? 1 : 0) + (mapKeyTextDomain ? 1 : 0) + (mapKeyParentDomain ? 1 : 0) + (hudPresentationDomain ? 1 : 0) + (hudBorderInputDomain ? 1 : 0) + (soulDomain ? 1 : 0) + (persistentSpriteDomain ? 1 : 0) > 1)
                throw new InvalidDataException("Carry domains must be distinct.");
            string ReadResource(string name)
            {
                using var stream = typeof(CanonicalCarryFieldRegistry).Assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidDataException("Frozen carry field registry is absent: " + name);
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
                var result = reader.ReadToEnd();
                if (Encoding.UTF8.GetByteCount(result) > 2 * 1024 * 1024) throw new InvalidDataException("Carry field registry exceeds its bound.");
                return result;
            }
            var text = ReadResource(Resource); var eventText = ReadResource(EventResource); var inputText = ReadResource(InputResource);
            var loadingText = ReadResource(LoadingResource); var loadingRegistry = JObject.Parse(loadingText);
            var geoText = ReadResource(GeoResource); var geoRegistry = JObject.Parse(geoText);
            var registry = JObject.Parse(text);
            var eventRegistry = JObject.Parse(eventText);
            var inputRegistry = JObject.Parse(inputText);
            if ((int?)registry["version"] != 1 || (bool?)registry["diagnosticOnly"] != true || !(registry["types"] is JArray entries)
                || (int?)eventRegistry["version"] != 1 || (bool?)eventRegistry["diagnosticOnly"] != true || !(eventRegistry["types"] is JArray eventEntries)
                || (int?)inputRegistry["version"] != 1 || (bool?)inputRegistry["diagnosticOnly"] != true || !(inputRegistry["types"] is JArray inputEntries)
                || (int?)loadingRegistry["version"] != 1 || (bool?)loadingRegistry["diagnosticOnly"] != true || !(loadingRegistry["types"] is JArray loadingEntries)
                || (string?)loadingRegistry["moduleMvid"] != typeof(GameManager).Module.ModuleVersionId.ToString("D"))
                throw new InvalidDataException("Unsupported frozen carry field registry.");
            if ((int?)geoRegistry["version"] != 1 || (bool?)geoRegistry["diagnosticOnly"] != true
                || !(geoRegistry["types"] is JArray geoEntries) || geoEntries.Count != 2
                || !geoEntries.Select(entry => (string?)entry["name"]).SequenceEqual(new[] {
                    "HutongGames.PlayMaker.Actions.ObjectJitter", "HutongGames.PlayMaker.Actions.ObjectJitterLocal" })
                || (string?)geoRegistry["moduleMvid"] != typeof(GameManager).Module.ModuleVersionId.ToString("D"))
                throw new InvalidDataException("Unsupported frozen original Geo action registry.");
            var assemblies = new[] { typeof(object).Assembly, typeof(Fsm).Assembly, typeof(HeroController).Assembly,
                typeof(Vector3).Assembly, typeof(UnityEngine.UI.Image).Assembly, typeof(AudioClip).Assembly,
                typeof(RaycastHit).Assembly, typeof(Rigidbody2D).Assembly }.Distinct().ToArray();
            var types = new List<Type>(); var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries.Concat(eventEntries).Concat(inputEntries).Concat(loadingEntries).Concat(geoEntries))
            {
                var name = (string?)entry["name"] ?? throw new InvalidDataException("Frozen carry type name is absent.");
                if (!names.Add(name)) throw new InvalidDataException("Frozen carry type name is duplicated.");
                var type = assemblies.Select(assembly => assembly.GetType(name, false)).FirstOrDefault(value => value != null);
                // Unknown/abstract/native types are never instantiated. If an
                // actual field reaches one, Capture retains an explicit gap.
                if (type == null || !Allocatable(type)) continue;
                if (!type.IsArray && !type.IsPrimitive && !type.IsEnum && type != typeof(string) && type != typeof(decimal))
                {
                    if (!(entry["fields"] is JArray fields)) continue;
                    var actual = CanonicalCarryGraphCodec.Fields(type).Select(field => new {
                        owner = field.DeclaringType!.FullName, name = field.Name, type = field.FieldType.FullName }).ToArray();
                    var expected = fields.Select(field => new { owner = (string?)field["owner"] ?? throw new InvalidDataException("Carry field owner is absent."),
                        name = (string?)field["name"] ?? throw new InvalidDataException("Carry field name is absent."),
                        type = (string?)field["type"] ?? throw new InvalidDataException("Carry field type is absent.") }).ToArray();
                    if (!actual.SequenceEqual(expected))
                        throw new InvalidDataException("Frozen carry field layout differs: " + name);
                }
                types.Add(type);
            }
            // The placeholder enum is a concrete boxed value reached through
            // FsmEnum.value (declared System.Object), so it is not a field type
            // in the original schema. It carries only its underlying bits.
            types.Add(typeof(HutongGames.PlayMaker.None));
            types.AddRange(CanonicalGameplayCarry.DataTypes);
            types.Add(typeof(CanonicalInputEpoch));
            types.Add(typeof(CanonicalClockCarry)); types.Add(typeof(byte[]));
            types.AddRange(HollowKnightTAS.Runtime.Input.FullRunActionSetAdapter.CanonicalSourceTypes);
            types.Add(typeof(CanonicalInputManagerScalars.Data));
            types.Add(typeof(CanonicalInputAdapterState)); types.Add(typeof(HollowKnightTAS.Core.Movie.GameInputChannel[])); types.Add(typeof(short[]));
            types.AddRange(CanonicalInputCallbackContract.DataTypes);
            types.AddRange(CanonicalSceneLoadCarry.DataTypes);
            types.AddRange(CanonicalSchedulerCarry.DataTypes);
            var invulnerabilityTypes = new[] { typeof(CheckpointHeroInvulnerability), typeof(CheckpointHeroInvulnerability.WaitData),
                typeof(CheckpointHeroInvulnerability.WaitData[]), typeof(float[]) };
            types.AddRange(invulnerabilityTypes);
            types.AddRange(CanonicalSceneMappingCarry.DataTypes);
            if (HollowKnightTAS.Core.Movie.NativeExecutionProfile.UsesCanonicalBossChallengeContext(
                HollowKnightTAS.Core.Movie.NativeExecutionProfile.SelectedProfileId)) {
                types.AddRange(CanonicalBossChallengeCarry.DataTypes);
                types.Add(typeof(CanonicalSoulOrbCarry.Data));
                types.Add(CanonicalSoulOrbCarry.BoundSoulType());
            }
            types.AddRange(CanonicalMouseCarry.DataTypes);
            types.AddRange(CanonicalDeviceManagerCarry.DataTypes);
            types.AddRange(CanonicalSceneLoadCallbacks.DataTypes);
            types.AddRange(CanonicalHeroAnimationCallbacks.DataTypes);
            types.AddRange(CanonicalHeroTriggerCallbacks.DataTypes);
            types.AddRange(CanonicalGeoCarry.DataTypes);
            types.AddRange(CanonicalMapSourceDomain.DataTypes);
            types.AddRange(CanonicalMarkerCarry.DataTypes);
            types.AddRange(CanonicalMarkerReadonlyCarry.DataTypes);
            if (hudBorderInputDomain) types.AddRange(CanonicalHudBorderInputs.DataTypes);
            if (hudPresentationDomain) types.AddRange(CanonicalHudPresentationCarry.DataTypes);
            if (persistentSpriteDomain) { types.AddRange(CanonicalPersistentSpriteCarry.DataTypes); types.AddRange(CanonicalPersistentSpriteContinuation.OriginalActionTypes()); }
            if (mapKeyTextDomain) types.AddRange(CanonicalMapKeyTextCarry.DataTypes);
            if (markerDomain || mapKeyTextDomain || hudPresentationDomain || hudBorderInputDomain) types.AddRange(CanonicalMarkerCarry.OriginalDataTypes());
            if (mapKeyParentDomain) { types.AddRange(CanonicalMapKeyParentSourceDomain.DataTypes); types.AddRange(CanonicalMapKeyParentSourceDomain.OriginalActionTypes()); }
            if (markerDomain || mapKeyTextDomain || hudPresentationDomain || hudBorderInputDomain) types.AddRange(CanonicalMarkerEventCarry.Types());
            using var hash = SHA256.Create();
            return new CanonicalCarryGraphCodec(types, (persistentSpriteDomain ? "canonical-persistent-sprite-fields-v4-health-eight-border:" : soulDomain ? "canonical-soul-fields-v1:" : hudBorderInputDomain ? "canonical-hud-border-input-v1:" : hudPresentationDomain ? "canonical-hud-presentation-v1:" : inputDomain ? "canonical-input-fields-v2:" : geoDomain ? "canonical-geo-fields-v1:" : mapDomain ? "canonical-map-fields-v1:" : markerDomain ? "canonical-marker-fields-v1:" : mapKeyTextDomain ? "canonical-map-key-text-fields-v1:" : mapKeyParentDomain ? "canonical-map-key-parent-fields-v1:" : "canonical-carry-fields-v6:") + BitConverter.ToString(
                hash.ComputeHash(Encoding.UTF8.GetBytes(text + "\n" + eventText + "\n" + inputText + "\n" + loadingText + "\n" + geoText + "\n" + CanonicalSystemEventBindings.SchemaIdentity
                    + (HollowKnightTAS.Core.Movie.NativeExecutionProfile.UsesCanonicalBossChallengeContext(HollowKnightTAS.Core.Movie.NativeExecutionProfile.SelectedProfileId) ? "\n" + CanonicalSoulOrbCarry.SchemaIdentity + "\n" + CanonicalSoulOrbCarry.BoundSoulLayout : "")
                    + "\n" + (markerDomain ? CanonicalMarkerCarry.SchemaIdentity : "")
                    + (mapKeyTextDomain ? "\n" + CanonicalMapKeyTextCarry.SchemaIdentity : "")
                    + (hudPresentationDomain ? "\n" + CanonicalHudPresentationCarry.SchemaIdentity : "")
                    + (hudBorderInputDomain ? "\n" + CanonicalHudBorderInputs.SchemaIdentity : "")
                    + (mapKeyParentDomain ? "\n" + CanonicalMapKeyParentSourceDomain.SchemaIdentity : "")
                    + "\n" + CanonicalGameplayCarry.SchemaIdentity + "\n" + CanonicalClockCarry.SchemaIdentity
                    + "\n" + CanonicalHeroAnimationCallbacks.SchemaIdentity
                    + "\n" + string.Join(";", CanonicalCarryGraphCodec.Fields(typeof(CanonicalMapSourceDomain.Data)).Select(field => field.Name + "=" + field.FieldType.FullName))
                    + "\n" + CanonicalHeroTriggerCallbacks.SchemaIdentity + "\n" + CanonicalGeoCarry.SchemaIdentity
                    + "\n" + string.Join(";", CanonicalGeoCarry.DataTypes.Where(type => !type.IsArray).SelectMany(type => CanonicalCarryGraphCodec.Fields(type))
                        .Select(field => field.Name + "=" + field.FieldType.FullName))
                    + "\n" + string.Join(";", CanonicalHeroTriggerCallbacks.DataTypes.Where(type => !type.IsArray)
                        .SelectMany(type => CanonicalCarryGraphCodec.Fields(type)).Select(field => field.DeclaringType!.FullName + "/" + field.Name + "=" + field.FieldType.FullName))
                    + "\n" + string.Join(";", CanonicalHeroAnimationCallbacks.DataTypes.Where(type => !type.IsArray)
                        .SelectMany(type => CanonicalCarryGraphCodec.Fields(type)).Select(field => field.DeclaringType!.FullName + "/" + field.Name + "=" + field.FieldType.FullName))
                    + "\n" + HollowKnightTAS.Runtime.Input.FullRunActionSetAdapter.CanonicalSourceSchema
                    + "\n" + string.Join("\n", CanonicalInputCallbackContract.DataTypes.Where(type => !type.IsArray).Select(type =>
                        CanonicalCarryGraphCodec.Identity(type) + ":" + string.Join(";", CanonicalCarryGraphCodec.Fields(type)
                            .Select(field => field.Name + "=" + CanonicalCarryGraphCodec.Identity(field.FieldType)))))
                    + "\n" + string.Join("\n", CanonicalSceneLoadCarry.DataTypes.Where(type => !type.IsArray).Select(type =>
                        CanonicalCarryGraphCodec.Identity(type) + ":" + string.Join(";", CanonicalCarryGraphCodec.Fields(type)
                            .Select(field => field.Name + "=" + CanonicalCarryGraphCodec.Identity(field.FieldType)))))
                    + "\n" + string.Join("\n", CanonicalSchedulerCarry.DataTypes.Where(type => !type.IsArray).Select(type =>
                        CanonicalCarryGraphCodec.Identity(type) + ":" + string.Join(";", CanonicalCarryGraphCodec.Fields(type)
                            .Select(field => field.Name + "=" + CanonicalCarryGraphCodec.Identity(field.FieldType)))))
                    + "\n" + string.Join("\n", invulnerabilityTypes.Where(type => !type.IsArray).Select(type =>
                        CanonicalCarryGraphCodec.Identity(type) + ":" + string.Join(";", CanonicalCarryGraphCodec.Fields(type)
                            .Select(field => field.Name + "=" + CanonicalCarryGraphCodec.Identity(field.FieldType)))))
                    + "\n" + string.Join("\n", CanonicalSceneMappingCarry.DataTypes.Where(type => !type.IsArray).Select(type =>
                        CanonicalCarryGraphCodec.Identity(type) + ":" + string.Join(";", CanonicalCarryGraphCodec.Fields(type)
                            .Select(field => field.Name + "=" + CanonicalCarryGraphCodec.Identity(field.FieldType)))))
                    + "\n" + CanonicalSceneMappingCarry.ModuleMvid + ":" + CanonicalSceneMappingCarry.ClosureType + ":"
                    + CanonicalSceneMappingCarry.HandlerName + ":" + CanonicalSceneMappingCarry.HandlerToken
                    + "\n" + string.Join("\n", CanonicalMouseCarry.DataTypes.Where(type => !type.IsArray).Select(type =>
                        CanonicalCarryGraphCodec.Identity(type) + ":" + string.Join(";", CanonicalCarryGraphCodec.Fields(type)
                            .Select(field => field.Name + "=" + CanonicalCarryGraphCodec.Identity(field.FieldType)))))
                    + "\n" + string.Join("\n", CanonicalDeviceManagerCarry.DataTypes.Where(type => !type.IsArray).Select(type =>
                        CanonicalCarryGraphCodec.Identity(type) + ":" + string.Join(";", CanonicalCarryGraphCodec.Fields(type)
                            .Select(field => field.Name + "=" + CanonicalCarryGraphCodec.Identity(field.FieldType)))))
                    + "\n" + CanonicalInputProfileContract.SchemaIdentity
                    + "\n" + CanonicalBoundaryInputTablePolicy.SchemaIdentity
                    + "\n" + CanonicalSceneLoadCallbacks.SchemaIdentity + "\n" + string.Join(";", CanonicalSceneLoadCallbacks.DataTypes.Where(type => !type.IsArray)
                        .SelectMany(type => CanonicalCarryGraphCodec.Fields(type)).Select(field => field.DeclaringType!.FullName + "/" + field.Name + "=" + field.FieldType.FullName))
                    + "\n" + CanonicalInputManagerScalars.SchemaIdentity + "\n" + string.Join(";", CanonicalCarryGraphCodec.Fields(typeof(CanonicalInputAdapterState))
                        .Select(field => field.Name + "=" + CanonicalCarryGraphCodec.Identity(field.FieldType))) + "\n" + string.Join(";", CanonicalCarryGraphCodec.Fields(typeof(CanonicalInputEpoch))
                        .Select(field => field.Name + "=" + CanonicalCarryGraphCodec.Identity(field.FieldType))))))
                .Replace("-", "").ToLowerInvariant(), PortableValue, AllocatedValue,
                graph => {
                    if (hudBorderInputDomain) {
                        if (graph.Roots == null || graph.Roots.Length != 1 || graph.Roots[0].Binding != CanonicalHudBorderInputDomain.Root)
                            throw new InvalidDataException("Border pre-Awake input domain has an unknown/missing root.");
                    }
                    else if (hudPresentationDomain) {
                        if (graph.Roots == null || graph.Roots.Length != 8 || graph.Roots.Count(root => root.Binding == CanonicalHudPresentationCarry.Root) != 1
                            || graph.Roots.Count(root => root.Binding.StartsWith(CanonicalHudPresentationCarry.FsmRootPrefix, StringComparison.Ordinal)) != 7
                            || graph.Roots.Any(root => root.Binding != CanonicalHudPresentationCarry.Root
                                && !root.Binding.StartsWith(CanonicalHudPresentationCarry.FsmRootPrefix, StringComparison.Ordinal)))
                            throw new InvalidDataException("HUD presentation carry domain has unknown/missing root.");
                    }
                    else if (mapKeyParentDomain) {
                        if (graph.Roots == null || graph.Roots.Length != 2 || graph.Roots.Count(root => root.Binding == CanonicalMapKeyParentSourceDomain.Root) != 1
                            || graph.Roots.Count(root => root.Binding == CanonicalMapKeyParentSourceDomain.FsmRoot) != 1)
                            throw new InvalidDataException("Map Key parent carry domain contains an unknown or missing root.");
                    }
                    else if (mapKeyTextDomain) {
                        if (graph.Roots == null || graph.Roots.Length != 1 || graph.Roots[0].Binding != CanonicalMapKeyTextCarry.Root)
                            throw new InvalidDataException("Map Key Text carry domain contains an unknown or missing original root.");
                    }
                    else if (markerDomain) {
                        if (graph.Roots == null || graph.Roots.Length != 2 || graph.Roots.Count(root => root.Binding == CanonicalMarkerCarry.Root) != 1
                            || graph.Roots.Count(root => root.Binding == "Marker/Fsm/$:PlayMakerFSM[0]") != 1)
                            throw new InvalidDataException("Marker carry domain contains an unknown or missing original root.");
                    }
                    else if (mapDomain) {
                        if (graph.Roots == null || graph.Roots.Length != 61 || graph.Roots.Count(root => root.Binding == CanonicalMapSourceDomain.Root) != 1
                            || graph.Roots.Any(root => root.Binding != CanonicalMapSourceDomain.Root && !root.Binding.StartsWith("Map/Fsm/", StringComparison.Ordinal)))
                            throw new InvalidDataException("Map carry domain contains an unknown or missing original root.");
                    }
                    else if (geoDomain) {
                        if (graph.Roots == null || graph.Roots.Length != 7 || graph.Roots.Count(root => root.Binding == CanonicalGeoCarry.Root) != 1
                            || graph.Roots.Any(root => root.Binding != CanonicalGeoCarry.Root && !root.Binding.StartsWith("Geo/Fsm/", StringComparison.Ordinal)))
                            throw new InvalidDataException("Geo carry domain contains an unknown or missing original root.");
                    }
                    else if (soulDomain) CanonicalSoulOrbCarry.ValidateDomainRoots(graph);
                    else if (persistentSpriteDomain) {
                        if (graph.Roots == null || graph.Roots.Length != CanonicalPersistentSpriteContinuation.RootCount || graph.Roots.Count(root => root.Binding == CanonicalPersistentSpriteCarry.Root) != 1
                            || graph.Roots.Any(root => root.Binding != CanonicalPersistentSpriteCarry.Root && !root.Binding.StartsWith(CanonicalPersistentSpriteContinuation.Prefix, StringComparison.Ordinal)
                                && root.Binding != CanonicalBorderArrowCarry.FsmRoot && !root.Binding.StartsWith(CanonicalBorderArrowCarry.FsmRoot + "/state[", StringComparison.Ordinal)
                                && root.Binding != CanonicalBorderArrowCarry.RightFsmRoot && !root.Binding.StartsWith(CanonicalBorderArrowCarry.RightFsmRoot + "/state[", StringComparison.Ordinal)))
                            throw new InvalidDataException("Persistent sprite carry domain contains unknown or missing roots.");
                    }
                    else if (!inputDomain) CanonicalEventCarryRoots.ValidateGraph(graph);
                    else if (graph.Roots == null || graph.Roots.Length == 0 || graph.Roots.Any(root =>
                        root.Binding == null || !root.Binding.StartsWith("R6/Input", StringComparison.Ordinal)))
                        throw new InvalidDataException("Input carry domain contains foreign roots.");
                }, fieldFailureDetails: (owner, field) => owner is FsmStateAction action
                    && field.DeclaringType?.FullName == "HutongGames.PlayMaker.Actions.iTweenFsmAction" && field.Name == "itweenEvents"
                    ? CanonicalHeroAnimationCallbacks.UnboundProvenance(action, new Dictionary<string, PlayMakerFSM>()) : null);
        }
        // Only the original empty placeholder can be allocated as local data.
        // Named/global/system events must retain their shared external identity.
        internal static bool IsLocalEmptyEvent(FsmEvent value) => Equals(LocalEventFields[0].GetValue(value), "")
            && Equals(LocalEventFields[1].GetValue(value), false) && Equals(LocalEventFields[2].GetValue(value), false)
            && LocalEventFields[3].GetValue(value) == null;
        private static string? AllocatedValue(object value) => value is FsmEvent fsmEvent && !IsLocalEmptyEvent(fsmEvent)
            ? "allocated-FsmEvent-needs-shared-global-binding"
            : value.GetType().DeclaringType == typeof(HollowKnightTAS.Runtime.Input.FullRunActionSetAdapter) ? "TAS-input-source-needs-existing-lease-root"
            : value is InControl.IKeyboardProvider || value is InControl.IMouseProvider ? "input-provider-needs-existing-slot-binding" : null;
        private static string? PortableValue(object value) => value is RaycastHit && (int)RaycastCollider.GetValue(value) != 0
            ? "RaycastHit-native-collider-id-needs-portable-rebinding" : null;
        private static bool Allocatable(Type type)
        {
            if (type.IsArray) return type.GetArrayRank() == 1;
            if (type.IsEnum) return true;
            if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters || type.IsPointer || type.IsByRef
                || type == typeof(IntPtr) || type == typeof(UIntPtr) || typeof(UnityEngine.Object).IsAssignableFrom(type)
                || typeof(MemberInfo).IsAssignableFrom(type) || typeof(Delegate).IsAssignableFrom(type)
                || typeof(MarshalByRefObject).IsAssignableFrom(type) || type.Namespace?.StartsWith("System.Threading", StringComparison.Ordinal) == true)
                return false;
            // Opaque Unity values (collisions, native handles, instance-id
            // structs) require their own portable binding adapter.
            if (type.Namespace?.StartsWith("UnityEngine", StringComparison.Ordinal) == true) return NativeValues.Contains(type.FullName);
            return true;
        }
    }
}
