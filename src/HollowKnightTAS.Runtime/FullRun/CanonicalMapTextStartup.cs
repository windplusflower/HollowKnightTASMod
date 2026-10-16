using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using MonoMod.RuntimeDetour;
using TMPro;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Fixed original Wide Map producers only. The original Start runs once;
    // new SubMesh/Link owners receive their lifecycle from Unity's PlayerLoop.
    internal sealed class CanonicalMapTextStartup : IDisposable
    {
        private delegate void OriginalStart(SetTextMeshProGameText owner);
        private static readonly string[] Areas = { "Ancient Basin", "City", "Cliffs", "Crossroads", "Deepnest", "Fog Canyon",
            "Fungal Wastes", "Greenpath", "Hive", "Mines", "Outskirts", "Resting Grounds", "Royal Gardens", "Town", "Waterways" };
        private readonly List<SetTextMeshProGameText> owners = new List<SetTextMeshProGameText>();
        private readonly List<TextMeshPro> texts = new List<TextMeshPro>();
        private readonly HashSet<int> arrived = new HashSet<int>();
        private readonly int thread = Thread.CurrentThread.ManagedThreadId;
        private Hook? hook;
        private Exception? failure;
        internal bool Complete => failure == null && arrived.Count == Areas.Length;
        private static readonly BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        internal static object? Raw(object owner, string name)
            => Field(owner.GetType(), name).GetValue(owner);
        private static FieldInfo Field(Type owner, string name)
        {
            for (var type = owner; type != null; type = type.BaseType) {
                var field = type.GetField(name, Instance | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            throw new MissingFieldException(owner.FullName, name);
        }
        internal static object Static(Type type, string name) => type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)
            ?? throw new InvalidOperationException("Map TMP static slot is null: " + name);
        internal CanonicalMapTextStartup(Transform hud)
        {
            var start = typeof(SetTextMeshProGameText).GetMethod("Start", Instance)!;
            var force = typeof(TextMeshPro).GetMethod("ForceMeshUpdate", new[] { typeof(bool) })!;
            if (start.Module.ModuleVersionId != new Guid("2b54cdaf-7a93-4f0e-b297-d50c4090ea9f")
                || start.MetadataToken != 100670153 || force.MetadataToken != 100673150
                || !Pinned(start, "c4db4afda08106a9317e161d056d5b29e1c0dc0f6cc63bbaa1e59dfb48b06bdf")
                || !Pinned(force, "b37f2044dc0811a40c5fb37ab38db7b33c976870aa879407bc0fe6105af477f7"))
                throw new InvalidOperationException("Original Map TMP producer assembly/IL differs.");
            var wide = hud.Find("Inventory/Map/World Map/Wide Map") ?? throw new InvalidOperationException("Original Wide Map missing.");
            using (var validation = new LoadingEngineClockProbe.CheckpointValidation()) {
            foreach (var area in Areas) {
                var node = wide.Find(area + "/Area Name") ?? throw new InvalidOperationException("Original Map label missing: " + area);
                var owner = node.GetComponent<SetTextMeshProGameText>() ?? throw new InvalidOperationException("Original Map producer missing.");
                var text = node.GetComponent<TextMeshPro>() ?? throw new InvalidOperationException("Original Map TMP missing.");
                if (BoundaryMonoLifecycle.ObserveUnstartedUi(owner).Take(2).Any(value => value != 0)
                    || BoundaryMonoLifecycle.ObserveUnstartedUi(text).Take(2).Any(value => value != 0)
                    || node.GetComponents<SetTextMeshProGameText>().Length != 1 || node.GetComponents<TextMeshPro>().Length != 1)
                    throw new InvalidOperationException("Map producer hook installed after original lifecycle: " + area);
                owners.Add(owner); texts.Add(text);
            }
            }
            hook = new Hook(start, (Action<OriginalStart, SetTextMeshProGameText>)Start);
            Modding.Logger.Log("[HKTAS] Installed fixed fifteen original Map text Start producers.");
        }
        private static bool Pinned(MethodInfo method, string hash) => string.Equals(
            HollowKnightTAS.Core.Cryptography.Sha256Utility.ComputeHex(method.GetMethodBody()!.GetILAsByteArray()), hash, StringComparison.OrdinalIgnoreCase);
        private void Start(OriginalStart original, SetTextMeshProGameText owner)
        {
            var index = owners.FindIndex(value => ReferenceEquals(value, owner));
            if (index < 0) { original(owner); return; }
            try {
                ThrowIfFailed();
                if (Thread.CurrentThread.ManagedThreadId != thread || !arrived.Add(index))
                    throw new InvalidOperationException("Map producer repeated or changed thread.");
                original(owner);
                var totalTimer = System.Diagnostics.Stopwatch.StartNew();
                var text = texts[index];
                if (!owner.gameObject.activeInHierarchy || !text.enabled || !ReferenceEquals(Raw(owner, "textMesh"), text)
                    || !ReferenceEquals(text.gameObject, owner.gameObject) || Native(owner)[0] != 1)
                    throw new InvalidOperationException("Original Map Start owner/text/active alias differs.");
                var parent = text.transform.parent; var position = text.transform.localPosition;
                var rotation = text.transform.localRotation; var scale = text.transform.localScale;
                var active = text.gameObject.activeSelf; var rawText = Raw(text, "m_text"); var font = Raw(text, "m_fontAsset");
                var material = Raw(text, "m_sharedMaterial");
                var configuration = new[] { "m_fontSizeBase", "m_fontSizeMin", "m_fontSizeMax", "m_enableAutoSizing", "m_fontStyle", "m_fontWeight", "m_isRightToLeft",
                    "m_textAlignment", "m_characterSpacing", "m_lineSpacing", "m_lineSpacingMax", "m_paragraphSpacing",
                    "m_charWidthMaxAdj", "m_enableWordWrapping", "m_wordWrappingRatios", "m_overflowMode", "m_enableKerning",
                    "m_enableExtraPadding", "m_isRichText", "m_parseCtrlCharacters", "m_isOrthographic", "m_isCullingEnabled",
                    "m_horizontalMapping", "m_verticalMapping", "m_maxVisibleCharacters", "m_maxVisibleWords", "m_maxVisibleLines",
                    "m_pageToDisplay", "m_margin", "m_isVolumetricText", "m_inputSource" }
                    .ToDictionary(name => name, name => Raw(text, name));
                var nativeBefore = Native(text);
                if (!(font is TMP_FontAsset fontAsset) || !(material is Material mainMaterial)
                    || (IntPtr)Raw(fontAsset, "m_CachedPtr")! == IntPtr.Zero || (IntPtr)Raw(mainMaterial, "m_CachedPtr")! == IntPtr.Zero)
                    throw new InvalidOperationException("Map TMP original font/main material native owner is absent.");
                Modding.Logger.Log("[HKTAS] Original Map producer post-Start input: " + Newtonsoft.Json.JsonConvert.SerializeObject(new {
                    area = Areas[index], rawText, inputSource = Raw(text, "m_inputSource")!.ToString(), font = fontAsset.name, material = mainMaterial.name,
                    activeSelf = active, activeInHierarchy = text.gameObject.activeInHierarchy, nativeLifecycle = nativeBefore.Select(value => (int)value).ToArray(),
                    configuration = CheckpointScalarFields.Capture(text, configuration.Keys.Select(name => Field(text.GetType(), name))) }));
                var events = new MapTextEventGuard();
                var updates = new MapTextUpdateGuard();
                var fonts = new MapFontReadGuard(text);
                var caches = new MapFallbackGuard(text);
                var data = DataGuard(); var random = ClockRandomFields(); var unityRandom = UnityEngine.Random.state;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var captureMilliseconds = totalTimer.ElapsedMilliseconds;
                text.ForceMeshUpdate(true);
                events.Verify(); updates.Verify(); fonts.Verify(); caches.Verify(text); data.Verify();
                var randomAfter = ClockRandomFields();
                if (random.Count != randomAfter.Count || random.Any(pair => !randomAfter.TryGetValue(pair.Key, out var value) || value != pair.Value)
                    || !MapFontReadGuard.Same(unityRandom, UnityEngine.Random.state)
                    || !nativeBefore.SequenceEqual(Native(text))
                    || configuration.Any(pair => !MapFontReadGuard.Same(pair.Value, Raw(text, pair.Key)))
                    || !ReferenceEquals(parent, text.transform.parent) || !MapFontReadGuard.Same(position, text.transform.localPosition)
                    || !MapFontReadGuard.Same(rotation, text.transform.localRotation) || !MapFontReadGuard.Same(scale, text.transform.localScale) || active != text.gameObject.activeSelf
                    || !text.gameObject.activeInHierarchy || !Equals(rawText, Raw(text, "m_text"))
                    || !ReferenceEquals(font, Raw(text, "m_fontAsset")) || !ReferenceEquals(material, Raw(text, "m_sharedMaterial")))
                    throw new InvalidOperationException("Map TMP force changed protected input, owner, pose or RNG.");
                Modding.Logger.Log("[HKTAS] Original Map text producer completed " + Areas[index] + " captureMs=" + captureMilliseconds
                    + ";forceAndVerifyMs=" + timer.ElapsedMilliseconds + ";totalMs=" + totalTimer.ElapsedMilliseconds);
                if (Complete) Dispose();
            } catch (Exception error) {
                failure = new InvalidOperationException("Original Map producer failed permanently: " + Areas[index], error);
                try { Dispose(); }
                catch (Exception cleanup) { failure = new AggregateException("Map producer failed and unhook cleanup also failed.", failure, cleanup); }
                try { Modding.Logger.LogError("[HKTAS] " + failure); }
                catch { /* Preserve the original failure for the mandatory Advance channel. */ }
                throw failure;
            }
        }
        private static byte[] Native(MonoBehaviour owner)
        {
            using var validation = new LoadingEngineClockProbe.CheckpointValidation();
            return BoundaryMonoLifecycle.ObserveUnstartedUi(owner);
        }
        private static Dictionary<string, string> ClockRandomFields() => CaptureClockRandomFields(AppDomain.CurrentDomain.GetAssemblies()
            .Single(value => value.GetName().Name == "HollowKnightTAS.ClockPayload").GetType("HollowKnightTAS.ClockPayload.ClockController", true)!);
        internal static Dictionary<string, string> CaptureClockRandomFields(Type clock) => CheckpointScalarFields.Capture(null,
            new[] { "fullRunRenderInitialized", "fullRunRenderEntered", "fullRunRenderIsolationEnabled", "fullRunRenderState",
                "randomSynchronizationStateS0", "randomSynchronizationStateS1", "randomSynchronizationStateS2", "randomSynchronizationStateS3",
                "randomSynchronizationSnapshotAvailable", "randomSynchronizationResetCount", "randomSynchronizationSceneEpoch",
                "randomSynchronizationLastAppliedSeed", "deterministicClockAdvanceSequence", "randomSynchronizationLastBoundary", "randomSynchronizationLastScene" }
            .Select(name => clock.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                ?? throw new MissingFieldException("Map RNG preservation field missing: " + name)));
        private static CanonicalDataPreservationGuard DataGuard()
        {
            IReadOnlyDictionary<string, object> Roots() {
                var result = new Dictionary<string, object> { ["PlayerData"] = PlayerData.instance,
                    ["SceneData"] = GameManager.instance.sceneData };
                var hero = HeroController.SilentInstance;
                if (hero != null) result.Add("D/HeroControllerStates", hero.cState);
                return result;
            }
            IReadOnlyDictionary<string, string> Scalars() => HeroController.SilentInstance == null
                ? new Dictionary<string, string>() : CanonicalGameplayCarry.CaptureHeroPreservationScalars();
            object[] Owners() => new object[] { GameManager.instance, GameManager.instance.playerData,
                    HeroController.SilentInstance ?? (object)DBNull.Value,
                    HeroController.SilentInstance == null ? (object)DBNull.Value : HeroController.SilentInstance.playerData }
                .Concat(HeroController.SilentInstance == null ? Array.Empty<object>() : CanonicalGameplayCarry.HeroPreservationOwners()).ToArray();
            return new CanonicalDataPreservationGuard(CanonicalCarryFieldRegistry.Create(), Roots, Scalars, Owners);
        }
        internal void ThrowIfFailed() { if (failure != null) throw failure; }
        internal void RejectIncomplete()
        {
            failure = failure ?? new InvalidOperationException("Original Map producer exceeded bounded HUD startup before all fifteen owners completed.");
            Dispose(); ThrowIfFailed();
        }
        public void Dispose() { var installed = hook; hook = null; installed?.Dispose(); }
    }

    internal sealed class MapTextEventGuard
    {
        private readonly object action = CanonicalMapTextStartup.Static(typeof(TMPro_EventManager), "TEXT_CHANGED_EVENT");
        private readonly object delegates, lookup;
        internal MapTextEventGuard() {
            delegates = CanonicalMapTextStartup.Raw(action, "delegates")!; lookup = CanonicalMapTextStartup.Raw(action, "lookup")!; Verify();
            if (action.GetType() != typeof(FastAction<UnityEngine.Object>)
                || delegates.GetType() != typeof(LinkedList<Action<UnityEngine.Object>>)
                || lookup.GetType() != typeof(Dictionary<Action<UnityEngine.Object>, LinkedListNode<Action<UnityEngine.Object>>>))
                throw new InvalidOperationException("Map TMP event collection ABI differs.");
        }
        internal void Verify() {
            if (!ReferenceEquals(action, CanonicalMapTextStartup.Static(typeof(TMPro_EventManager), "TEXT_CHANGED_EVENT"))
                || !ReferenceEquals(delegates, CanonicalMapTextStartup.Raw(action, "delegates"))
                || !ReferenceEquals(lookup, CanonicalMapTextStartup.Raw(action, "lookup"))
                || ((ICollection)delegates).Count != 0 || ((IDictionary)lookup).Count != 0
                || !(delegates is LinkedList<Action<UnityEngine.Object>> chain) || chain.First != null || chain.Last != null)
                throw new InvalidOperationException("Map TMP text event is not an unchanged empty original FastAction.");
        }
    }

    internal sealed class MapTextUpdateGuard
    {
        private readonly object manager = CanonicalMapTextStartup.Static(typeof(TMP_UpdateManager), "s_Instance");
        private readonly object? callback = typeof(Camera).GetField("onPreRender", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null);
        private readonly List<Action> checks = new List<Action>();
        internal MapTextUpdateGuard()
        {
            foreach (var name in new[] { "m_LayoutRebuildQueue", "m_GraphicRebuildQueue" }) {
                var list = CanonicalMapTextStartup.Raw(manager, name) as List<TMP_Text>
                    ?? throw new InvalidOperationException("Map TMP original update queue ABI differs.");
                if (list.GetType() != typeof(List<TMP_Text>) || list.Count > 4096) throw new InvalidOperationException("Map TMP update queue concrete ABI/bound differs.");
                var before = list.Cast<object>().ToArray();
                checks.Add(() => {
                    if (!ReferenceEquals(list, CanonicalMapTextStartup.Raw(manager, name)) || before.Length != list.Count
                        || before.Where((value, index) => !ReferenceEquals(value, list[index])).Any())
                        throw new InvalidOperationException("Map TMP original update queue members/order/alias changed.");
                });
            }
            foreach (var name in new[] { "m_LayoutQueueLookup", "m_GraphicQueueLookup" }) {
                var dictionary = CanonicalMapTextStartup.Raw(manager, name) as Dictionary<int, int>
                    ?? throw new InvalidOperationException("Map TMP original update lookup ABI differs.");
                if (dictionary.GetType() != typeof(Dictionary<int, int>) || dictionary.Count > 4096) throw new InvalidOperationException("Map TMP update lookup concrete ABI/bound differs.");
                var before = dictionary.ToArray();
                checks.Add(() => {
                    if (!ReferenceEquals(dictionary, CanonicalMapTextStartup.Raw(manager, name)) || before.Length != dictionary.Count
                        || before.Any(pair => !dictionary.TryGetValue(pair.Key, out var value) || value != pair.Value))
                        throw new InvalidOperationException("Map TMP original update lookup changed.");
                });
            }
        }
        internal void Verify()
        {
            if (!ReferenceEquals(manager, CanonicalMapTextStartup.Static(typeof(TMP_UpdateManager), "s_Instance"))
                || !ReferenceEquals(callback, typeof(Camera).GetField("onPreRender", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)))
                throw new InvalidOperationException("Map TMP original update manager/camera callback changed.");
            foreach (var check in checks) check();
        }
    }
}
