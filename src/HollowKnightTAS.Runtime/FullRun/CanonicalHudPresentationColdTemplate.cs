using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Retain the actual serialized owners before their first native Awake.
    // No initialized live component or inferred language/PD value is a template.
    internal sealed class CanonicalHudPresentationColdTemplate : IDisposable
    {
        internal const string AssetBindingPrefix = "HudPresentationOriginalAsset/v1/";
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private GameObject? holder;
        private Transform? inventory;
        private Transform[] parents = Array.Empty<Transform>();
        private int[] siblings = Array.Empty<int>();
        private GameObject[] templates = Array.Empty<GameObject>();
        private readonly CanonicalAssetOriginRegistry assets = new CanonicalAssetOriginRegistry();
        private readonly Dictionary<string, Tuple<object, FieldInfo?, int, UnityEngine.Object>> assetSlots
            = new Dictionary<string, Tuple<object, FieldInfo?, int, UnityEngine.Object>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Tuple<SpriteRenderer, Sprite?, Material[]>> rendererSlots
            = new Dictionary<string, Tuple<SpriteRenderer, Sprite?, Material[]>>(StringComparer.Ordinal);
        private readonly Dictionary<TMP_FontAsset, Material?> fontMaterials = new Dictionary<TMP_FontAsset, Material?>();
        internal static Transform? FrozenInventory { get; private set; }
        internal string AssetManifest { get; private set; } = "";
        internal bool Captured => holder != null;

        internal static void RequireCold(IReadOnlyDictionary<string, UnityEngine.Object> directory)
        {
            if (directory.Count != 110 || directory.Values.OfType<MonoBehaviour>().Count() != 40)
                throw new InvalidDataException("HUD presentation cold owner directory is incomplete.");
            using var validation = new LoadingEngineClockProbe.CheckpointValidation();
            foreach (var owner in directory.Values.OfType<MonoBehaviour>()) {
                var flags = BoundaryMonoLifecycle.ObserveUnstartedUi(owner);
                if (owner.gameObject.activeInHierarchy || flags[0] != 0 || flags[1] != 0 || flags[3] != 0 || flags[4] != 0)
                    throw new InvalidOperationException("HUD presentation original definition has already awakened: " + owner.GetType().FullName);
            }
            foreach (var fsm in directory.Values.OfType<PlayMakerFSM>())
                if (((HutongGames.PlayMaker.Fsm)typeof(PlayMakerFSM).GetField("fsm", Flags)!.GetValue(fsm)).Started)
                    throw new InvalidOperationException("HUD presentation original managed FSM has already started.");
            if (directory.Values.OfType<MeshFilter>().Any(filter => !ReferenceEquals(filter.sharedMesh, null))
                || directory.Values.OfType<TextMeshPro>().Any(text => !ReferenceEquals(typeof(TextMeshPro).GetField("m_mesh", Flags)!.GetValue(text), null)))
                throw new InvalidOperationException("HUD original cold definition unexpectedly owns a generated native mesh.");
            CanonicalMarkerEventCarry.RequireEmpty(directory);
            CanonicalMarkerReadonlyCarry.CaptureMapKey(directory);
            var icon = directory.Values.OfType<MenuButtonIcon>().Single();
            if (typeof(ActionButtonIconBase).GetField("OnIconUpdate", Flags)!.GetValue(icon) != null)
                throw new InvalidOperationException("HUD presentation icon has an unqualified subscriber.");
        }

        private static Dictionary<string, object> NativeBindings(IReadOnlyDictionary<string, UnityEngine.Object> directory)
            => directory.ToDictionary(pair => pair.Key, pair => (object)pair.Value, StringComparer.Ordinal);
        private void CaptureOriginalAssets(IReadOnlyDictionary<string, UnityEngine.Object> directory)
        {
            var own = NativeBindings(directory);
            var members = LoadingGeoIncomingReferences.BuildHudPresentationDirectory(inventory!);
            var byReference = new Dictionary<object, string>(new References());
            foreach (var pair in members) if (!byReference.ContainsKey(pair.Value)) byReference.Add(pair.Value, pair.Key);
            LoadingFieldDigest? reader = null;
            void Register(string origin, UnityEngine.Object native)
            {
                if (native == null) throw new InvalidDataException("Original HUD presentation asset is dead.");
                assets.Add(origin, native, CanonicalCarryGraphCodec.Identity(native.GetType()), native.name);
                if (native is TMP_FontAsset font && !fontMaterials.ContainsKey(font)) {
                    var material = (Material?)typeof(TMP_Asset).GetField("material", Flags)!.GetValue(font);
                    fontMaterials.Add(font, material);
                    if (!ReferenceEquals(material, null)) assets.Add(origin + "/TMP_Asset.material", material!, CanonicalCarryGraphCodec.Identity(typeof(Material)), material!.name);
                }
            }
            string? Reference(object value)
            {
                if (byReference.TryGetValue(value, out var path)) return "original-hud-owner:" + path;
                if (value is UnityEngine.Object native) {
                    if (native == null) throw new InvalidDataException("Original HUD definition contains a dead native reference.");
                    return "original-native:" + native.GetType().FullName + ":" + native.GetInstanceID();
                }
                return null;
            }
            using (reader = new LoadingFieldDigest(Reference, type => type == typeof(MonoBehaviour) || type == typeof(UnityEngine.Object),
                slotObservation: (owner, field, index, value, path) => {
                    if (!(value is UnityEngine.Object native) || own.Values.Any(candidate => ReferenceEquals(candidate, native))) return;
                    if (native is Component || native is GameObject)
                        throw new InvalidDataException("Original HUD definition has an unregistered external scene-object field: " + path);
                    var origin = "level1/" + reader!.DiagnosticRoot + "/" + path;
                    Register(origin, native);
                    assetSlots.Add(origin, Tuple.Create(owner, field, index, native));
                }, observeSlotValue: value => value is UnityEngine.Object, sharedFieldReaders: true)) {
                reader.BeginSharedGraph();
                try {
                    var roots = directory.Where(pair => pair.Value is MonoBehaviour).Select(pair => new KeyValuePair<string, object>(pair.Key, pair.Value))
                        .Concat(members.Where(pair => !(pair.Value is UnityEngine.Object) && (pair.Value is HutongGames.PlayMaker.Fsm
                            || pair.Value is HutongGames.PlayMaker.FsmState || pair.Value is HutongGames.PlayMaker.FsmStateAction)));
                    foreach (var root in roots) {
                        reader.CaptureReferences(root.Value, root.Key, expandRoot: true);
                    }
                } finally { reader.EndSharedGraph(); }
            }
            foreach (var pair in directory.Where(pair => pair.Value is SpriteRenderer)) {
                var renderer = (SpriteRenderer)pair.Value; var sprite = renderer.sprite; var materials = renderer.sharedMaterials;
                rendererSlots.Add(pair.Key, Tuple.Create<SpriteRenderer, Sprite?, Material[]>(renderer, sprite, materials));
                if (!ReferenceEquals(sprite, null)) Register("level1/" + pair.Key + "/native-sprite", sprite!);
                for (var index = 0; index < materials.Length; index++)
                    if (!ReferenceEquals(materials[index], null)) Register("level1/" + pair.Key + "/native-shared-material[" + index + "]", materials[index]);
            }
            assets.Seal(); AssetManifest = assets.ManifestHash();
        }
        internal void RequireFrozenAssets()
        {
            if (holder == null || FrozenInventory == null || AssetManifest.Length != 64)
                throw new InvalidOperationException("Original HUD presentation asset definition is absent.");
            RequireCold(CanonicalHudPresentationDirectory.Build(inventory!, templates));
            foreach (var pair in assetSlots) {
                var slot = pair.Value;
                var current = slot.Item2 == null ? ((Array)slot.Item1).GetValue(slot.Item3) : slot.Item2.GetValue(slot.Item1);
                assets.RequireOrigin(pair.Key, current);
            }
            foreach (var pair in fontMaterials)
                if (!ReferenceEquals(typeof(TMP_Asset).GetField("material", Flags)!.GetValue(pair.Key), pair.Value))
                    throw new InvalidOperationException("Original HUD presentation font material was replaced.");
            foreach (var pair in rendererSlots) {
                var slot = pair.Value;
                var actual = slot.Item1.sharedMaterials;
                if (!ReferenceEquals(slot.Item1.sprite, slot.Item2) || actual.Length != slot.Item3.Length
                    || actual.Where((material, index) => !ReferenceEquals(material, slot.Item3[index])).Any())
                    throw new InvalidOperationException("Original HUD presentation SpriteRenderer asset configuration was replaced.");
            }
        }
        internal string? BindOriginalAsset(object value)
        {
            if (!(value is UnityEngine.Object native) || native == null || native is Component || native is GameObject) return null;
            return assets.Bind(value, CanonicalCarryGraphCodec.Identity(value.GetType()), native.name);
        }
        internal object ResolveOriginalAsset(string binding)
        {
            var value = assets.Resolve(binding);
            if (!(value is UnityEngine.Object native) || native == null) throw new InvalidDataException("Original HUD presentation asset died.");
            return value;
        }

        internal void Capture(Transform actualInventory, LoadingGeoIncomingReferences incoming)
        {
            if (holder != null || FrozenInventory != null) throw new InvalidOperationException("HUD presentation cold definition was captured twice.");
            inventory = actualInventory;
            var directory = CanonicalHudPresentationDirectory.Build(inventory); RequireCold(directory);
            var paths = CanonicalHudPresentationDirectory.RootPaths;
            templates = paths.Select(path => inventory.Find(path).gameObject).ToArray();
            parents = templates.Select(root => root.transform.parent).ToArray();
            siblings = templates.Select(root => root.transform.GetSiblingIndex()).ToArray();
            if (parents.Any(parent => parent == null || parent.gameObject.activeInHierarchy))
                throw new InvalidOperationException("HUD presentation freeze is outside the original inactive startup.");
            CaptureOriginalAssets(directory);
            var native = NativeBindings(directory);
            var parentNodes = parents.SelectMany(Ancestors).Distinct().ToArray();
            var parentRoots = parentNodes.SelectMany(node => node.GetComponents<MonoBehaviour>()).Distinct()
                .Select((owner, index) => new KeyValuePair<string, object?>("hud-parent[" + index + "]", owner)).ToArray();
            using var parentGuard = new CanonicalMapKeyTextExternalGuard(parentRoots, native, parentNodes);
            var nonText = directory.Where(pair => pair.Value is MonoBehaviour && !(pair.Value is TextMeshPro))
                .Select(pair => new KeyValuePair<string, object?>(pair.Key, pair.Value)).ToArray();
            var player = PlayerData.instance; var manager = GameManager.instance;
            using var sourceGuard = new CanonicalMapKeyTextExternalGuard(nonText.Concat(new[] {
                new KeyValuePair<string, object?>("actual-player", player), new KeyValuePair<string, object?>("actual-scene-data", manager.sceneData) }).ToArray(), native);
            var random = UnityEngine.Random.state; var particles = LoadingGameplayOracle.CaptureParticleRandomState();
            var texts = directory.Where(pair => pair.Value is TextMeshPro).ToArray();
            var raw = texts.Select(pair => CanonicalHudPresentationDirectory.ManagedFields(pair.Value.GetType()).Select(field => field.GetValue(pair.Value)).ToArray()).ToArray();
            var coldFlags = directory.Values.OfType<MonoBehaviour>().SelectMany(BoundaryMonoLifecycle.ObserveUnstartedUi).ToArray();
            var self = directory.Values.OfType<GameObject>().Select(owner => owner.activeSelf).ToArray();
            var codec = CanonicalCarryFieldRegistry.Create(hudPresentationDomain: true);
            bool freeze = true;
            void Verify()
            {
                RequireCold(directory); sourceGuard.VerifyManagedRoots();
                if (freeze) parentGuard.VerifyManagedRootsAfterOriginalHudDetach(parents, siblings);
                else parentGuard.VerifyManagedRoots();
                if (!ReferenceEquals(player, PlayerData.instance) || !ReferenceEquals(manager, GameManager.instance)
                    || !random.Equals(UnityEngine.Random.state) || !particles.SequenceEqual(LoadingGameplayOracle.CaptureParticleRandomState())
                    || !coldFlags.SequenceEqual(directory.Values.OfType<MonoBehaviour>().SelectMany(BoundaryMonoLifecycle.ObserveUnstartedUi))
                    || !self.SequenceEqual(directory.Values.OfType<GameObject>().Select(owner => owner.activeSelf)))
                    throw new InvalidOperationException("HUD original cold producer changed gameplay, lifecycle or RNG.");
                int transitions = 0;
                for (var owner = 0; owner < texts.Length; owner++) {
                    var fields = CanonicalHudPresentationDirectory.ManagedFields(texts[owner].Value.GetType());
                    for (var index = 0; index < fields.Length; index++) {
                        var field = fields[index]; var actual = field.GetValue(texts[owner].Value);
                        // Original Transform::SetParent broadcasts OnBeforeTransformParentChanged recursively
                        // (pinned 0x7ea27e -> 0x22a540 -> 0x7e3c90), including button text descendants.
                        var parentCache = freeze && field.DeclaringType == typeof(UnityEngine.UI.Graphic) && field.Name == "m_RectTransform"
                            && ReferenceEquals(raw[owner][index], null) && ReferenceEquals(actual, ((Component)texts[owner].Value).GetComponent<RectTransform>());
                        if (parentCache) { transitions++; raw[owner][index] = actual; }
                        else if (!codec.SameRawSlot(raw[owner][index], actual, field.FieldType, 0))
                            throw new InvalidOperationException("HUD original cold parent callback changed a raw TMP field: " + texts[owner].Key + "/" + field.Name);
                    }
                }
                if (freeze && transitions != texts.Length) throw new InvalidOperationException("HUD original cold freeze did not take its complete original subtree parent-cache transitions.");
                freeze = false;
            }
            holder = new GameObject("__HKTAS_OriginalHudPresentationColdTemplate"); holder.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(holder);
            var frozen = new GameObject("Inventory"); frozen.transform.SetParent(holder.transform, false); FrozenInventory = frozen.transform;
            for (var index = 0; index < paths.Length; index++) {
                var slash = paths[index].LastIndexOf('/'); var path = paths[index].Substring(0, slash);
                var parent = FrozenInventory;
                foreach (var name in path.Split('/')) {
                    var child = parent.Find(name);
                    if (child == null) { var item = new GameObject(name); item.transform.SetParent(parent, false); child = item.transform; }
                    parent = child;
                }
                templates[index].transform.SetParent(parent, false);
            }
            Verify();
            var created = ProduceInactive(); Verify();
            var replacement = LoadingGeoIncomingReferences.BuildHudPresentationDirectory(inventory, created);
            parentGuard.PrepareExplicitIncomingRebind(NativeBindings(CanonicalHudPresentationDirectory.Build(inventory, created)));
            var plan = incoming.Prepare(replacement); plan.Apply(); plan.VerifyReadback(); Verify();
            Modding.Logger.Log("[HKTAS] Original HUD presentation cold factory installed: roots=13;objects=18;components=92;Mono=40;TMP=9;FSM=7;fullIncomingReadback=true;originalAssets=" + AssetManifest);
        }
        private static IEnumerable<Transform> Ancestors(Transform node)
        { for (int depth = 0; node != null; node = node.parent) { if (++depth > 32) throw new InvalidDataException("HUD presentation ancestor bound exceeded."); yield return node; } }
        internal GameObject[] ProduceInactive()
        {
            RequireFrozenAssets();
            if (parents.Length != 13 || parents.Any(parent => parent == null || parent.gameObject.activeInHierarchy))
                throw new InvalidOperationException("Active HUD presentation needs its original natural-close continuation.");
            var result = new GameObject[13];
            for (var index = 0; index < result.Length; index++) {
                var clone = UnityEngine.Object.Instantiate(templates[index], holder!.transform, false); clone.name = templates[index].name;
                clone.transform.SetParent(parents[index], false); clone.transform.SetSiblingIndex(siblings[index]); result[index] = clone;
            }
            RequireCold(CanonicalHudPresentationDirectory.Build(inventory!, result));
            return result;
        }
        public void Dispose() { if (holder != null) UnityEngine.Object.DestroyImmediate(holder); holder = null; FrozenInventory = null; inventory = null; }
        private sealed class References : IEqualityComparer<object> {
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
        }
    }
}
