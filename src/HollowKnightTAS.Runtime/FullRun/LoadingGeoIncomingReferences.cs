using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Observe references already traversed by the full reader. This neither
    // performs another world walk nor claims coverage of unobserved native slots.
    internal sealed class LoadingGeoIncomingReferences
    {
        private readonly Dictionary<object, string> owned = new Dictionary<object, string>(new References());
        private readonly SortedSet<string> references = new SortedSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Slot> slots = new Dictionary<string, Slot>(StringComparer.Ordinal);
        private readonly HashSet<string> externalReferences = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, object> soulExternalOwners = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly HashSet<object> soulMembers = new HashSet<object>(new References());
        private readonly HashSet<object> soulGlobalCandidates = new HashSet<object>(new References());
        private readonly Dictionary<string, object> soulCandidatePaths = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly Dictionary<string, Slot> soulCandidateSlots = new Dictionary<string, Slot>(StringComparer.Ordinal);
        private bool soulSample;
        private int soulReferenceBytes;
        private CanonicalHeroAnimationCallbacks.DetachedBenchProof? soulBench;
        private string? soulBenchId;
        private readonly List<Tuple<object, string, string>> soulBenchReferences = new List<Tuple<object, string, string>>();
        private string[] ownedRoots = Array.Empty<string>();
        private GeoCounter? counter;
        private GameObject? marker;
        private Transform? mapKey;
        private bool menuFlash;
        private bool hudPresentation;
        private bool persistentSprites;
        private readonly HashSet<object> persistentInternalContainers = new HashSet<object>(new References());
        private readonly Dictionary<string, Tuple<FieldInfo, object>> persistentStructSlots = new Dictionary<string, Tuple<FieldInfo, object>>(StringComparer.Ordinal);
        private string? persistentInternalSlotPath;
        private object? persistentInternalSlotValue;
        private long persistentInternalSlotCount;
        internal bool Owns(object? value) => value != null && owned.ContainsKey(value);
        internal void BeginPersistentSpriteSample(Func<UnityEngine.Object, string> identity)
        {
            owned.Clear(); references.Clear(); slots.Clear(); externalReferences.Clear(); ownedRoots = Array.Empty<string>();
            counter = null; marker = null; mapKey = null; menuFlash = false; hudPresentation = false; soulSample = false;
            persistentSprites = true; persistentInternalContainers.Clear(); persistentInternalSlotPath = null; persistentInternalSlotValue = null; persistentInternalSlotCount = 0;
            var directory = CanonicalPersistentSpriteDirectory.Resolve(); var fields = CanonicalPersistentSpriteCarry.Fields();
            foreach (var owner in directory.Values.Cast<object>().Concat(CanonicalPersistentSpriteContinuation.NativeOwners())) persistentInternalContainers.Add(owner);
            ownedRoots = directory.Values.Select(identity).Concat(CanonicalPersistentSpriteContinuation.NativeOwners().Select(identity)).ToArray();
            foreach (var pair in CanonicalPersistentSpriteContinuation.CopiedMembers()) owned.Add(pair.Key, "$/PersistentAnimator/" + pair.Value);
            foreach (var pair in CanonicalFinalVisualCarry.CopiedMembers()) if (!owned.ContainsKey(pair.Key)) owned.Add(pair.Key, "$/FinalVisual/" + pair.Value);
            foreach (var owner in CanonicalFinalVisualCarry.Directory().Values) persistentInternalContainers.Add(owner);
            ownedRoots = ownedRoots.Concat(CanonicalFinalVisualCarry.Directory().Values.Select(identity)).ToArray();
            persistentStructSlots.Clear();
            foreach (var pair in CanonicalFinalVisualCarry.StructSlots(identity)) persistentStructSlots.Add(pair.Key, pair.Value);
            foreach (var pair in directory) foreach (var field in fields) {
                var value = field.GetValue(pair.Value);
                if (value == null || (!(value is Array) && !(value is System.Collections.IList) && !(value is Mesh))) continue;
                if (!owned.ContainsKey(value)) owned.Add(value, "$/PersistentSprite/" + pair.Key + "/" + field.Name);
                if (value is System.Collections.IList && !(value is Array)) {
                    var items = value.GetType().GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value);
                    if (!(items is Array)) throw new InvalidOperationException("Persistent sprite list backing ABI differs.");
                    if (!owned.ContainsKey(items)) owned.Add(items, "$/PersistentSprite/" + pair.Key + "/" + field.Name + "/_items");
                }
            }
        }
        internal void RequirePersistentSpriteNoExternalManagedHolders(Func<object, bool>? mainNode = null)
        {
            var rejected = externalReferences.Where(path => !slots.TryGetValue(path, out var slot)
                || mainNode == null || !mainNode(slot.Original) || !ReferenceEquals(slot.Read(), slot.Original)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (ownedRoots.Length != CanonicalPersistentSpriteDirectory.OwnerCount + CanonicalPersistentSpriteContinuation.NativeOwnerCount + 5 || rejected.Length != 0)
                throw new InvalidOperationException("Persistent sprite managed incoming proof has external holders: " + string.Join(";", rejected.Take(32)));
        }
        internal object PersistentSpriteSharedIdentityAudit(Func<object, int?> mainNode)
        {
            var anchors = CanonicalPersistentSpriteContinuation.OwnerDefaultVariableSlots();
            var members = new Dictionary<object, List<string>>(new References());
            foreach (var path in externalReferences.OrderBy(path => path, StringComparer.Ordinal)) {
                if (!slots.TryGetValue(path, out var slot) || !ReferenceEquals(slot.Read(), slot.Original)) throw new InvalidOperationException("Persistent sprite identity audit has an unreadable or changed outside slot.");
                if (!members.TryGetValue(slot.Original, out var paths)) { paths = new List<string>(); members.Add(slot.Original, paths); }
                paths.Add(path);
            }
            if (members.Count > 32 || externalReferences.Count > 4096) throw new InvalidOperationException("Persistent sprite identity audit exceeds its finite bound.");
            return members.Select(pair => new { type = pair.Key.GetType().FullName, mainNode = mainNode(pair.Key),
                localOwnedNodePath = owned[pair.Key], exactSameObjectHealthSlots = anchors.Where(anchor => ReferenceEquals(anchor.Value, pair.Key)).Select(anchor => anchor.Key).ToArray(),
                outsideSlotCount = pair.Value.Count, outsideSlotsSample = pair.Value.Take(8).ToArray(),
                allOutsidePathsSha256 = HollowKnightTAS.Core.Cryptography.Sha256Utility.ComputeHex(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", pair.Value))) }).ToArray();
        }
        // Soul's Fsm/state/action objects retain their actual identities as D
        // roots. Unknown outside holders of roots or copied descendants are
        // still refused: preserving identity alone does not restore their slots.
        internal void BeginSoulCopiedSample(Func<UnityEngine.Object, string> identity, IReadOnlyDictionary<object, string> copied)
        {
            owned.Clear(); references.Clear(); slots.Clear(); externalReferences.Clear();
            soulExternalOwners.Clear(); soulMembers.Clear(); soulGlobalCandidates.Clear(); soulCandidatePaths.Clear(); soulCandidateSlots.Clear(); soulSample = true; soulReferenceBytes = 0;
            soulBench = CanonicalHeroAnimationCallbacks.ProbeDetachedBench(); soulBenchId = null; soulBenchReferences.Clear();
            counter = null; marker = null; mapKey = null; menuFlash = false; hudPresentation = false;
            var owner = CanonicalSoulOrbCarry.Owner();
            var roots = new List<string> { identity(owner) };
            foreach (var callback in owner.transform.Find("SoulOrb_fill/Liquid").GetComponents<iTweenFSMEvents>()) roots.Add(identity(callback));
            ownedRoots = roots.ToArray();
            foreach (var pair in copied) { owned.Add(pair.Key, "$/Soul/" + pair.Value + " (" + pair.Key.GetType().FullName + ")"); soulMembers.Add(pair.Key); }
            var globals = CanonicalSoulOrbCarry.GlobalGameObjects();
            if (globals.Length > 4096) throw new InvalidOperationException("Soul global candidate count exceeds its bound.");
            Modding.Logger.Log("[HKTAS] Soul global incoming original array: count=" + globals.Length + ";candidateLimit=4096;indexLimit=32768;allReferenceBytesLimit=4194304.");
            for (var i = 0; i < globals.Length; i++) if (globals[i] != null) {
                soulGlobalCandidates.Add(globals[i]);
                if (!owned.ContainsKey(globals[i])) owned.Add(globals[i], "$/Soul/global-candidate[" + i + "]");
            }
        }
        internal bool IsSoulMember(object value) => soulMembers.Contains(value);
        internal void ObserveSoulBench(object value, string path, string root)
        {
            if (soulBench == null || !soulBench.Members.Contains(value)) return;
            if (soulBenchReferences.Count >= 4096) throw new InvalidOperationException("Detached Bench incoming proof exceeds its bound.");
            soulBenchReferences.Add(Tuple.Create(value, path, root));
        }
        internal void BindSoulBenchFsm(Fsm value, string id)
        {
            if (soulBench == null || !ReferenceEquals(value, soulBench.Fsm)) return;
            if (soulBenchId != null && soulBenchId != id) throw new InvalidOperationException("Detached Bench FSM identity changed.");
            soulBenchId = id;
        }
        internal string SoulIndexStatistics() => "candidates=" + soulGlobalCandidates.Count + ";paths=" + soulCandidatePaths.Count
            + ";slots=" + soulCandidateSlots.Count + ";bytes=" + soulReferenceBytes;
        internal void BeginMenuFlashSample(Func<UnityEngine.Object, string> identity, GameObject root)
        {
            owned.Clear(); references.Clear(); slots.Clear(); externalReferences.Clear(); ownedRoots = Array.Empty<string>();
            counter = null; marker = null; mapKey = null; menuFlash = true; hudPresentation = false;
            foreach (var pair in CanonicalMenuFlashColdPolicy.IncomingDirectory(root)) owned.Add(pair.Value, pair.Key);
            ownedRoots = owned.Keys.OfType<MonoBehaviour>().Select(identity).ToArray();
            var mesh = typeof(UnityEngine.UI.Graphic).GetField("m_CachedMesh", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(root.GetComponent<UnityEngine.UI.Image>());
            if (!ReferenceEquals(mesh, null)) {
                if (!(mesh is Mesh actual) || actual == null || owned.ContainsKey(actual)) throw new InvalidOperationException("Menu Flash retired mesh is dead/shared.");
                owned.Add(actual, "$/retired-owned-mesh");
            }
        }
        internal void BeginSample(Func<UnityEngine.Object, string> identity, GeoCounter? explicitCounter = null)
        {
            owned.Clear(); references.Clear(); slots.Clear(); externalReferences.Clear(); ownedRoots = Array.Empty<string>();
            marker = null; mapKey = null; menuFlash = false; hudPresentation = false;
            var cameras = typeof(GameCameras).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) as GameCameras;
            counter = explicitCounter ?? (cameras == null ? null : cameras.geoCounter);
            if (counter == null) return;
            PopulateOwnedDirectory(counter);
            ownedRoots = owned.Keys.OfType<MonoBehaviour>().Select(identity).ToArray();
        }
        // A finite second owner domain shares the existing slot/alias reader.
        // This entry accepts only the exact original Marker directory above;
        // it does not broaden the existing Geo shape or its admission.
        internal void BeginMarkerSample(Func<UnityEngine.Object, string> identity, GameObject? explicitMarker = null)
        {
            owned.Clear(); references.Clear(); slots.Clear(); externalReferences.Clear(); ownedRoots = Array.Empty<string>();
            counter = null; mapKey = null; menuFlash = false; hudPresentation = false; marker = explicitMarker ?? CanonicalMarkerDirectory.Current();
            foreach (var pair in CanonicalMarkerDirectory.Build(marker)) {
                owned.Add(pair.Value, pair.Key);
                if (pair.Value is PlayMakerFSM fsm) {
                    var raw = typeof(PlayMakerFSM).GetField("fsm", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fsm);
                    AddManaged(raw, pair.Key + "/fsm");
                }
            }
            foreach (var pair in CanonicalMarkerReadonlyCarry.Directory(CanonicalMarkerDirectory.Build(marker)))
                if (!owned.ContainsKey(pair.Value)) owned.Add(pair.Value, pair.Key);
            foreach (var pair in CanonicalMarkerEventCarry.Directory(CanonicalMarkerDirectory.Build(marker)))
                if (!owned.ContainsKey(pair.Value)) owned.Add(pair.Value, pair.Key);
            ownedRoots = owned.Keys.OfType<MonoBehaviour>().Select(identity).ToArray();
        }
        internal static IReadOnlyDictionary<string, object> BuildMarkerDirectory(GameObject marker)
        {
            var directory = new LoadingGeoIncomingReferences();
            directory.BeginMarkerSample(_ => "unused-marker-local-root", marker);
            return directory.OwnedDirectory();
        }
        internal void BeginMapKeySample(Func<UnityEngine.Object, string> identity, Transform keys, IReadOnlyList<GameObject>? leaves = null)
        {
            owned.Clear(); references.Clear(); slots.Clear(); externalReferences.Clear(); ownedRoots = Array.Empty<string>();
            counter = null; marker = null; mapKey = keys; menuFlash = false; hudPresentation = false;
            var directory = CanonicalMapKeyTextDirectory.Build(keys, leaves);
            foreach (var pair in directory) owned.Add(pair.Value, pair.Key);
            foreach (var pair in CanonicalMarkerReadonlyCarry.MapKeyDirectory(directory))
                if (!owned.ContainsKey(pair.Value)) owned.Add(pair.Value, pair.Key);
            foreach (var pair in CanonicalMarkerEventCarry.Directory(directory))
                if (!owned.ContainsKey(pair.Value)) owned.Add(pair.Value, pair.Key);
            ownedRoots = owned.Keys.OfType<MonoBehaviour>().Select(identity).ToArray();
        }
        internal static IReadOnlyDictionary<string, object> BuildMapKeyDirectory(Transform keys, IReadOnlyList<GameObject>? leaves = null)
        {
            var directory = new LoadingGeoIncomingReferences();
            directory.BeginMapKeySample(_ => "unused-map-key-local-root", keys, leaves);
            return directory.OwnedDirectory();
        }
        internal void BeginHudPresentationSample(Func<UnityEngine.Object, string> identity, Transform inventory, IReadOnlyList<GameObject>? roots = null,
            bool allowGenerated = false, bool includeBorderMeshes = false)
        {
            owned.Clear(); references.Clear(); slots.Clear(); externalReferences.Clear(); ownedRoots = Array.Empty<string>();
            counter = null; marker = null; mapKey = null; menuFlash = false; hudPresentation = true;
            var directory = CanonicalHudPresentationDirectory.Build(inventory, roots, allowGenerated);
            foreach (var pair in directory) {
                owned.Add(pair.Value, pair.Key);
                if (pair.Value is PlayMakerFSM fsm) AddManaged(typeof(PlayMakerFSM).GetField("fsm", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fsm), pair.Key + "/fsm");
            }
            foreach (var pair in CanonicalMarkerReadonlyCarry.MapKeyDirectory(directory)) if (!owned.ContainsKey(pair.Value)) owned.Add(pair.Value, pair.Key);
            foreach (var pair in CanonicalMarkerEventCarry.Directory(directory)) if (!owned.ContainsKey(pair.Value)) owned.Add(pair.Value, pair.Key);
            if (includeBorderMeshes) foreach (var pair in CanonicalHudPresentationColdTemplate.FreshBorderMeshes(directory, requireFresh: false)) owned.Add(pair.Value, pair.Key);
            ownedRoots = directory.Values.OfType<MonoBehaviour>().Select(identity).ToArray();
        }
        internal static IReadOnlyDictionary<string, object> BuildHudPresentationDirectory(Transform inventory, IReadOnlyList<GameObject>? roots = null, bool includeBorderMeshes = false)
        {
            var directory = new LoadingGeoIncomingReferences();
            directory.BeginHudPresentationSample(_ => "unused-presentation-local-root", inventory, roots, includeBorderMeshes: includeBorderMeshes);
            return directory.OwnedDirectory();
        }
        internal void ObserveRetiringHudPresentationMeshes(Transform inventory, Func<UnityEngine.Object, string> identity)
        {
            if (!hudPresentation) throw new InvalidOperationException("HUD retiring mesh observation has no exact owner domain.");
            var directory = CanonicalHudPresentationDirectory.Build(inventory, allowGeneratedSubmeshes: true);
            var generated = CanonicalHudPresentationDirectory.GeneratedSubmeshes(directory);
            // These exact mutable render caches disappear with the old TMP owners.
            // Shared font glyph classes/assets are not owned and are never retired.
            var cacheNodes = 0;
            void OwnCache(object? value, string path, int depth)
            {
                if (value == null || value is UnityEngine.Object || value is string || value.GetType().IsPrimitive || value.GetType().IsEnum) return;
                if (depth > 8 || ++cacheNodes > 20000) throw new InvalidOperationException("HUD old render-cache graph exceeds its finite bound.");
                if (value is Array array) {
                    if (array.Length == 0 || owned.ContainsKey(array)) return;
                    owned.Add(array, path);
                    for (var index = 0; index < array.Length; index++) OwnCache(array.GetValue(index), path + "[" + index + "]", depth + 1);
                } else if (value.GetType().IsValueType) {
                    foreach (var field in CanonicalCarryGraphCodec.Fields(value.GetType())) OwnCache(field.GetValue(value), path + "/" + field.Name, depth + 1);
                } else if (value.GetType() == typeof(TMPro.TMP_TextInfo)) {
                    if (owned.ContainsKey(value)) return;
                    owned.Add(value, path);
                    foreach (var field in CanonicalCarryGraphCodec.Fields(value.GetType())) OwnCache(field.GetValue(value), path + "/" + field.Name, depth + 1);
                }
            }
            foreach (var pair in directory.Where(pair => pair.Value is TMPro.TextMeshPro))
                foreach (var field in CanonicalHudPresentationDirectory.ManagedFields(pair.Value.GetType()).Where(field => !field.IsInitOnly))
                    OwnCache(field.GetValue(pair.Value), pair.Key + "/retired-render-cache/" + field.DeclaringType!.FullName + "/" + field.Name, 0);
            foreach (var pair in generated) owned.Add(pair.Value, pair.Key);
            ownedRoots = ownedRoots.Concat(generated.Values.OfType<MonoBehaviour>().Select(identity)).ToArray();
            foreach (var pair in directory.Concat(generated).Where(pair => pair.Value is TMPro.TextMeshPro || pair.Value is TMPro.TMP_SubMesh)) {
                var mesh = pair.Value.GetType().GetField("m_mesh", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pair.Value);
                if (ReferenceEquals(mesh, null)) continue;
                if (!(mesh is Mesh native) || native == null) throw new InvalidOperationException("HUD retiring mesh is dead or unknown.");
                if (owned.TryGetValue(native, out var existing)) {
                    if (existing == pair.Key + "/original-Awake-mesh") continue;
                    throw new InvalidOperationException("HUD retiring mesh has a shared/foreign original owner.");
                }
                owned.Add(native, pair.Key + "/retired-owned-mesh");
            }
        }
        internal void BeginMapKeyParentSample(Func<UnityEngine.Object, string> identity, Transform keys)
        {
            owned.Clear(); references.Clear(); slots.Clear(); externalReferences.Clear(); ownedRoots = Array.Empty<string>();
            counter = null; marker = null; mapKey = keys; menuFlash = false; hudPresentation = false;
            var directory = new CanonicalMapKeyParentDirectory(keys);
            foreach (var pair in directory.Native) owned.Add(pair.Value, pair.Key);
            AddManaged(directory.Fsm, "$:PlayMakerFSM[0]/fsm");
            for (var index = 0; index < 3; index++) owned.Add(directory.Containers[index], CanonicalMapKeyParentDirectory.ContainerPrefix + CanonicalMapKeyParentDirectory.ContainerNames[index]);
            ownedRoots = new MonoBehaviour[] { directory.Control, directory.Fade }.Select(identity).ToArray();
        }
        internal string[] PathsForIdentity(string identity)
            => references.Where(path => path.StartsWith(identity + " <- ", StringComparison.Ordinal))
                .Select(path => path.Substring(identity.Length + 4)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        internal void RequireMapKeyTextArraySlots(Array actualCurrentContainer)
        {
            if (actualCurrentContainer.GetType() != typeof(TMPro.TextMeshPro[]) || actualCurrentContainer.Length != 11 || slots.Count != 9)
                throw new InvalidOperationException("Map Key old/new source slots have an unknown array owner shape.");
            for (var index = 0; index < 9; index++) {
                var identity = "$/" + CanonicalMapKeyTextDirectory.Keys[index] + "/Text:TMPro.TextMeshPro[0]";
                var matches = slots.Values.Where(slot => slot.Identity == identity).ToArray();
                if (matches.Length != 1 || !matches[0].IsArraySlot(actualCurrentContainer, index + 1))
                    throw new InvalidOperationException("Map Key frozen incoming slot does not bind the real current FadeGroup array/source index.");
            }
        }
        internal void ObserveRetiringMapKeyMeshes()
        {
            if (mapKey == null || marker != null || counter != null) throw new InvalidOperationException("Retiring Map Key mesh observation has no exact directory.");
            var field = typeof(TMPro.TextMeshPro).GetField("m_mesh", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var pair in CanonicalMapKeyTextDirectory.Build(mapKey).Where(pair => pair.Value is TMPro.TextMeshPro)) {
                var value = field.GetValue(pair.Value);
                if (ReferenceEquals(value, null)) continue;
                if (!(value is Mesh mesh) || mesh == null || owned.ContainsKey(mesh))
                    throw new InvalidOperationException("Retiring Map Key TMP mesh is dead, shared or has an unknown type.");
                owned.Add(mesh, pair.Key + "/retired-owned-mesh");
            }
        }
        // A cold Canon retires these exact TMP-owned meshes through the
        // original OnDestroy. They have no replacement: any external consumer
        // therefore fails Prepare rather than silently retaining a dead mesh.
        internal void ObserveRetiringMarkerMeshes()
        {
            if (marker == null || counter != null) throw new InvalidOperationException("Retiring Marker mesh observation has no exact directory.");
            var field = typeof(TMPro.TextMeshPro).GetField("m_mesh", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var pair in CanonicalMarkerDirectory.Build(marker).Where(pair => pair.Value is TMPro.TextMeshPro)) {
                var value = field.GetValue(pair.Value);
                if (ReferenceEquals(value, null)) continue;
                if (!(value is Mesh mesh) || mesh == null || owned.ContainsKey(mesh))
                    throw new InvalidOperationException("Retiring Marker TMP mesh is dead, shared or has an unknown type.");
                owned.Add(mesh, pair.Key + "/retired-owned-mesh");
            }
        }
        // Directory consumers need exact local aliases, not world-root paths.
        // Keep the same native/managed admission below without constructing a
        // whole-scene identity inventory whose paths would never be consumed.
        internal static IReadOnlyDictionary<string, object> BuildOwnedDirectory(GeoCounter counter)
        {
            var directory = new LoadingGeoIncomingReferences();
            directory.PopulateOwnedDirectory(counter);
            return directory.OwnedDirectory();
        }
        private void PopulateOwnedDirectory(GeoCounter counter)
        {
            var nodes = counter.GetComponentsInChildren<Transform>(true);
            if (nodes.Length != 5) throw new InvalidOperationException("Geo incoming reference owner directory differs.");
            foreach (var node in nodes) {
                var path = ReferenceEquals(node, counter.transform) ? "$" : "$/" + node.name;
                owned.Add(node.gameObject, path);
                var ordinals = new Dictionary<Type, int>();
                foreach (var component in node.GetComponents<Component>()) {
                    if (component == null) throw new InvalidOperationException("Geo incoming reference component is missing.");
                    var type = component.GetType(); ordinals.TryGetValue(type, out var ordinal); ordinals[type] = ordinal + 1;
                    owned.Add(component, path + ":" + type.FullName + "[" + ordinal + "]");
                    if (component is PlayMakerFSM fsm) {
                        var raw = typeof(PlayMakerFSM).GetField("fsm", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fsm);
                        AddManaged(raw, path + ":" + type.FullName + "[" + ordinal + "]/fsm");
                    }
                }
            }
        }
        internal IReadOnlyDictionary<string, object> OwnedDirectory() => owned.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
        internal LoadingGeoIncomingReferences Freeze()
        {
            var copy = new LoadingGeoIncomingReferences { counter = counter, marker = marker, mapKey = mapKey, menuFlash = menuFlash, hudPresentation = hudPresentation, soulSample = soulSample, soulReferenceBytes = soulReferenceBytes, ownedRoots = (string[])ownedRoots.Clone(), persistentSprites = persistentSprites, persistentInternalSlotCount = persistentInternalSlotCount };
            foreach (var owner in persistentInternalContainers) copy.persistentInternalContainers.Add(owner);
            foreach (var pair in persistentStructSlots) copy.persistentStructSlots.Add(pair.Key, pair.Value);
            foreach (var pair in owned) copy.owned.Add(pair.Key, pair.Value);
            foreach (var item in references) copy.references.Add(item);
            foreach (var pair in slots) copy.slots.Add(pair.Key, pair.Value);
            foreach (var item in externalReferences) copy.externalReferences.Add(item);
            foreach (var item in soulMembers) copy.soulMembers.Add(item);
            foreach (var pair in soulExternalOwners) copy.soulExternalOwners.Add(pair.Key, pair.Value);
            foreach (var item in soulGlobalCandidates) copy.soulGlobalCandidates.Add(item);
            foreach (var pair in soulCandidatePaths) copy.soulCandidatePaths.Add(pair.Key, pair.Value);
            foreach (var pair in soulCandidateSlots) copy.soulCandidateSlots.Add(pair.Key, pair.Value);
            copy.soulBench = soulBench; copy.soulBenchId = soulBenchId; copy.soulBenchReferences.AddRange(soulBenchReferences);
            return copy;
        }
        internal void RequireNoExternalConsumers()
        {
            if (externalReferences.Count != 0)
                throw new InvalidOperationException("Frozen original Geo owner still has an external consumer: " + string.Join(";", externalReferences.OrderBy(value => value, StringComparer.Ordinal))
                    + (owned.Values.Any(value => value.StartsWith("$/Soul/", StringComparison.Ordinal)) ? ";soulMatches=" + string.Join(";", references.Where(value => value.StartsWith("$/Soul/", StringComparison.Ordinal) && externalReferences.Contains(value.Substring(value.IndexOf(" <- ", StringComparison.Ordinal) + 4))).Take(32)) : ""));
        }
        // Existing full-walk observations only: no new world traversal. Every
        // admitted external path must have a readable slot to guard twice.
        internal string[] SoulSharedPaths(IReadOnlyDictionary<object, string> globals)
        {
            var result = new List<string>();
            foreach (var path in externalReferences.OrderBy(value => value, StringComparer.Ordinal))
                if (!soulExternalOwners.TryGetValue(path, out var observed) || !globals.ContainsKey(observed))
                    throw new InvalidOperationException("Soul external holder is not a registered global variable: " + path);
            foreach (var pair in soulCandidatePaths.OrderBy(value => value.Key, StringComparer.Ordinal)) {
                var path = pair.Key; var observed = pair.Value;
                if (!globals.TryGetValue(observed, out var key)) continue;
                if (!soulCandidateSlots.TryGetValue(path, out var slot) || !ReferenceEquals(slot.Original, observed)
                    || !ReferenceEquals(slot.Read(), slot.Original))
                    throw new InvalidOperationException("Soul external holder is not an exact writable global variable slot: " + path);
                result.Add(key + "\n" + path);
            }
            if (result.Count > 4096 || System.Text.Encoding.UTF8.GetByteCount(string.Join("\n", result)) > 1024 * 1024)
                throw new InvalidOperationException("Soul global incoming directory exceeds its bounds.");
            return result.ToArray();
        }
        internal void RequireSoulSharedPaths(IReadOnlyDictionary<object, string> globals, string[] expected)
        {
            if (!SoulSharedPaths(globals).SequenceEqual(expected))
                throw new InvalidOperationException("Soul global shared incoming paths or current identities differ.");
        }
        internal Action ValidateSoulSharedTransition(IReadOnlyDictionary<object, string> globals, string[] sourcePaths)
        {
            var current = SoulSharedPaths(globals);
            var missing = sourcePaths.Except(current, StringComparer.Ordinal).ToArray();
            var extra = current.Except(sourcePaths, StringComparer.Ordinal).ToArray();
            if (missing.Length != 0) throw new InvalidDataException("Soul source incoming paths are missing on target: count=" + missing.Length + ";first=" + missing[0]);
            if (extra.Length == 0) return () => RequireSoulSharedPaths(globals, current);
            var proof = soulBench ?? throw new InvalidDataException("Soul target has extra incoming paths without the original detached Bench callback.");
            var id = soulBenchId ?? throw new InvalidDataException("Original detached Bench FSM was not observed by the full walker.");
            proof.VerifyBefore();
            foreach (var reference in soulBenchReferences) {
                var internalRoot = reference.Item3 == id || reference.Item3.StartsWith(id + "/", StringComparison.Ordinal);
                var callbackRoot = ReferenceEquals(reference.Item1, proof.Target)
                    && reference.Item2 == "DontDestroyOnLoad/Knight[0]:tk2dSpriteAnimator[0].AnimationCompleted.target[0]";
                if (!internalRoot && !callbackRoot) throw new InvalidDataException("Detached Bench FSM/action has an unknown external holder: " + reference.Item2);
            }
            if (!soulBenchReferences.Any(reference => ReferenceEquals(reference.Item1, proof.Target)
                && reference.Item2 == "DontDestroyOnLoad/Knight[0]:tk2dSpriteAnimator[0].AnimationCompleted.target[0]"))
                throw new InvalidDataException("Detached Bench original animator edge was not observed.");
            var field = typeof(FsmOwnerDefault).GetField("gameObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            foreach (var item in extra) {
                var split = item.IndexOf('\n'); if (split < 0) throw new InvalidDataException("Soul extra path is malformed.");
                var path = item.Substring(split + 1); var prefix = id + "/";
                if (!path.StartsWith(prefix, StringComparison.Ordinal) || !proof.Slots.TryGetValue(path.Substring(prefix.Length), out var owner)
                    || !soulCandidateSlots.TryGetValue(path, out var slot) || !slot.SameFieldOwner(owner, field))
                    throw new InvalidDataException("Soul target extra is not an exact original detached Bench slot: " + path);
            }
            return () => { RequireSoulSharedPaths(globals, current); proof.VerifyRemoved(); };
        }
        internal static IReadOnlyDictionary<string, object> HudFsmMembers(object raw, string path)
        {
            var tracker = new LoadingGeoIncomingReferences(); tracker.AddManaged(raw, path); return tracker.OwnedDirectory();
        }
        private void AddManaged(object value, string path)
        {
            if (!owned.ContainsKey(value)) owned.Add(value, path);
            var states = typeof(Fsm).GetField("states", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(value) as FsmState[];
            if (states == null || states.Length > 256) throw new InvalidOperationException("Geo incoming original state directory differs.");
            for (var i = 0; i < states.Length; i++) {
                if (!owned.ContainsKey(states[i])) owned.Add(states[i], path + "/state[" + i + "]");
                var actions = typeof(FsmState).GetField("actions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(states[i]) as FsmStateAction[];
                if (actions == null) continue;
                if (actions.Length > 128) throw new InvalidOperationException("Geo incoming original action directory differs.");
                for (var a = 0; a < actions.Length; a++) if (actions[a] != null && !owned.ContainsKey(actions[a]))
                    owned.Add(actions[a], path + "/state[" + i + "]/action[" + a + "]");
            }
        }
        private bool InternalRoot(string root) => ownedRoots.Any(value => root == value || root.StartsWith(value + "/", StringComparison.Ordinal));
        // This is the original OnEnable/OnDisable/OnDestroy registration list,
        // not a consumer alias. Replacing its array entry before OnDestroy
        // would prevent the original Remove(this) from retiring the old FSM.
        // Source membership is carried and checked after the real lifecycle.
        private bool LifecycleRoot(string root) => root == "static:PlayMaker:PlayMakerFSM.fsmList"
            || hudPresentation && root == "hud-Border3/original-TMP-manager";
        internal void Observe(object value, string path, string root)
        {
            if (!owned.TryGetValue(value, out var identity)) return;
            // Slot observation precedes Reference(value) in the original full
            // reader. Omit only this exact already-owned internal slot from the
            // diagnostic path set; its full value/alias remains in the graph.
            // A root-name match alone is never sufficient for this omission.
            if (persistentSprites && path == persistentInternalSlotPath && ReferenceEquals(value, persistentInternalSlotValue)) {
                persistentInternalSlotPath = null; persistentInternalSlotValue = null; return;
            }
            if (soulSample && soulGlobalCandidates.Contains(value) && !InternalRoot(root) && !LifecycleRoot(root)) {
                if (soulCandidatePaths.TryGetValue(path, out var existing)) {
                    if (!ReferenceEquals(existing, value)) throw new InvalidOperationException("Soul candidate path maps to multiple identities.");
                } else {
                    soulReferenceBytes = checked(soulReferenceBytes + System.Text.Encoding.UTF8.GetByteCount(path) + System.Text.Encoding.UTF8.GetByteCount(identity));
                    if (soulCandidatePaths.Count >= 32768 || soulReferenceBytes > 4 * 1024 * 1024)
                        throw new InvalidOperationException("Soul global candidate index exceeds bound: paths=" + soulCandidatePaths.Count + ";bytes=" + soulReferenceBytes + ";candidates=" + soulGlobalCandidates.Count);
                    soulCandidatePaths.Add(path, value);
                }
                if (!soulMembers.Contains(value)) return;
            }
            if (references.Count >= 4096) throw new InvalidOperationException("Geo incoming reference diagnostic bound exceeded.");
            var reference = identity + " <- " + path;
            references.Add(reference);
            if (!InternalRoot(root) && !LifecycleRoot(root)) {
                externalReferences.Add(path);
                if (soulSample) {
                    if (soulExternalOwners.TryGetValue(path, out var prior) && !ReferenceEquals(prior, value))
                        throw new InvalidOperationException("Soul incoming path maps to multiple identities.");
                    soulExternalOwners[path] = value;
                }
            }
        }
        internal void ObserveSlot(object owner, FieldInfo? field, int index, object? value, string path, string root)
        {
            if (value == null || !owned.TryGetValue(value, out var identity)) return;
            if (persistentSprites) {
                persistentInternalSlotPath = null; persistentInternalSlotValue = null;
                if ((InternalRoot(root) || LifecycleRoot(root)) && (owned.ContainsKey(owner) || persistentInternalContainers.Contains(owner)
                    || owner.GetType().IsValueType && field != null && persistentStructSlots.TryGetValue(path, out var proof)
                        && proof.Item1 == field && ReferenceEquals(proof.Item2, value))) {
                    persistentInternalSlotCount = checked(persistentInternalSlotCount + 1);
                    persistentInternalSlotPath = path; persistentInternalSlotValue = value; return;
                }
                // Even a forged/internal root cannot hide an outside containing
                // owner. Keep every outside slot under the original 4096 cap.
                if (externalReferences.Count >= 4096 && !externalReferences.Contains(path)) throw new InvalidOperationException("Persistent sprite external reference bound exceeded.");
                externalReferences.Add(path);
            } else if (InternalRoot(root) || LifecycleRoot(root)) return;
            if (soulSample && soulGlobalCandidates.Contains(value)) {
                if (owner.GetType().IsValueType || field?.IsInitOnly == true) throw new InvalidOperationException("Soul candidate incoming alias is not writable: " + path);
                if (soulCandidateSlots.TryGetValue(path, out var prior)) {
                    if (!prior.SameSlot(owner, field, index, value)) throw new InvalidOperationException("Soul candidate path maps to different slots: " + path);
                } else {
                    soulReferenceBytes = checked(soulReferenceBytes + System.Text.Encoding.UTF8.GetByteCount(path) + System.Text.Encoding.UTF8.GetByteCount(identity));
                    if (soulCandidateSlots.Count >= 32768 || soulReferenceBytes > 4 * 1024 * 1024)
                        throw new InvalidOperationException("Soul candidate slot index exceeds bound: slots=" + soulCandidateSlots.Count + ";bytes=" + soulReferenceBytes + ";candidates=" + soulGlobalCandidates.Count);
                    soulCandidateSlots.Add(path, new Slot(owner, field, index, value, identity));
                }
                if (!soulMembers.Contains(value)) return;
            }
            if (slots.Count >= 4096) throw new InvalidOperationException("Geo incoming source slot bound exceeded.");
            if (owner.GetType().IsValueType || field?.IsInitOnly == true)
                throw new InvalidOperationException("Geo incoming alias needs an explicit writable containing owner: " + path);
            slots[path] = new Slot(owner, field, index, value, identity);
        }
        internal CanonicalPreparedWritePlan Prepare(IReadOnlyDictionary<string, object> replacements, Action<object>? appliedWitness = null)
        {
            if ((counter == null && marker == null && mapKey == null && !menuFlash && !hudPresentation) || externalReferences.Any(path => !slots.ContainsKey(path)))
                throw new InvalidOperationException("Geo incoming directory contains an unbound delegate/native/collection slot.");
            var selected = slots.Values.ToArray();
            foreach (var slot in selected)
                if (!replacements.TryGetValue(slot.Identity, out var replacement) || replacement.GetType() != slot.Original.GetType())
                    throw new InvalidOperationException("Geo incoming replacement directory is incomplete: " + slot.Identity);
            void Guard() {
                foreach (var slot in selected) if (!ReferenceEquals(slot.Read(), slot.Original))
                    throw new InvalidOperationException("Geo incoming source slot changed after the original full traversal.");
            }
            Guard();
            var witnessProduced = false;
            return new CanonicalPreparedWritePlan(Guard, () => {
                foreach (var slot in selected) slot.Write(replacements[slot.Identity]);
            }, () => {
                foreach (var slot in selected) if (!ReferenceEquals(slot.Read(), replacements[slot.Identity]))
                    throw new InvalidOperationException("Geo incoming complete source alias readback differs.");
                if (appliedWitness != null && !witnessProduced) {
                    witnessProduced = true;
                    object Identity(object value) => value is UnityEngine.Object native
                        ? new { type = value.GetType().FullName, nativeInstance = native.GetInstanceID(), alive = native != null }
                        : (object)new { type = value.GetType().FullName, managedReference = RuntimeHelpers.GetHashCode(value) };
                    appliedWitness(slots.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new {
                        path = pair.Key, logicalOwner = pair.Value.Identity, before = Identity(pair.Value.Original),
                        after = Identity(replacements[pair.Value.Identity]), exactReferenceReadback = ReferenceEquals(pair.Value.Read(), replacements[pair.Value.Identity]) }).ToArray());
                }
            });
        }
        private sealed class Slot
        {
            private readonly object owner; private readonly FieldInfo? field; private readonly int index;
            internal readonly object Original; internal readonly string Identity;
            internal Slot(object owner, FieldInfo? field, int index, object original, string identity)
            { this.owner = owner; this.field = field; this.index = index; Original = original; Identity = identity; }
            internal bool SameSlot(object candidateOwner, FieldInfo? candidateField, int candidateIndex, object value)
                => ReferenceEquals(owner, candidateOwner) && field == candidateField && index == candidateIndex && ReferenceEquals(Original, value);
            internal bool SameFieldOwner(object candidateOwner, FieldInfo candidateField) => ReferenceEquals(owner, candidateOwner) && field == candidateField;
            internal bool IsArraySlot(Array array, int arrayIndex) => field == null && ReferenceEquals(owner, array) && index == arrayIndex && ReferenceEquals(Read(), Original);
            internal object? Read() => field == null ? ((Array)owner).GetValue(index) : field.GetValue(owner);
            internal void Write(object value) { if (field == null) ((Array)owner).SetValue(value, index); else field.SetValue(owner, value); }
        }
        private sealed class References : IEqualityComparer<object>
        {
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
        internal object Snapshot() => new { diagnosticOnly = true, managedPaths = references.ToArray(),
            writableExternalPaths = slots.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            unboundExternalPaths = externalReferences.Where(path => !slots.ContainsKey(path)).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            includesInternalAliases = !persistentSprites, countedInternalManagedSlots = persistentInternalSlotCount,
            completeInternalAliasesRemainInOwnedGraph = persistentSprites, unobservedNativeSlotsQualified = false };
    }
}
