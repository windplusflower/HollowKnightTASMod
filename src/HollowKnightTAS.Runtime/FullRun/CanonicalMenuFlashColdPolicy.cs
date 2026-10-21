using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using MonoMod.RuntimeDetour;
using UnityEngine;
using UnityEngine.UI;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Build the fixed serialized definition while the startup input gate is
    // still closed. Resource provenance, rather than Awake ordering or a late
    // live Sprite value, identifies the original blank frame and controller.
    internal sealed class CanonicalMenuFlashColdPolicy : IDisposable
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly Hook awakeHook;
        private GameObject? holder, template;
        private Sprite? blank;
        private RuntimeAnimatorController? controller;
        private Exception? failure;
        private bool observed;
        private UIManager? closingUi;
        private CanvasGroup? closingMenu;
        private Coroutine? menuCloseRoutine;
        private Coroutine? menuFadeRoutine;
        private bool menuCloseCompleted;
        private int menuCloseFirstFrame;
        private readonly HashSet<string> startupNativeStages = new HashSet<string>(StringComparer.Ordinal);
        private static readonly FieldInfo CachedMesh = Field(typeof(Graphic), "m_CachedMesh");
        private static readonly FieldInfo Runner = Field(typeof(Graphic), "m_ColorTweenRunner");
        private static readonly FieldInfo Corners = Field(typeof(MaskableGraphic), "m_Corners");
        internal static GameObject? FrozenRoot { get; private set; }
        private static FieldInfo Field(Type type, string name) => type.GetField(name, Instance)
            ?? throw new MissingFieldException(type.FullName, name);

        internal CanonicalMenuFlashColdPolicy()
        {
            if (InControl.InputManager.CurrentTick != 0)
                throw new InvalidOperationException("Original menu Flash definition installed after startup input tick zero.");
            awakeHook = new Hook(typeof(UIManager).GetMethod("Awake", Instance)!,
                (Action<Action<UIManager>, UIManager>)((original, owner) => {
                    if (!observed && failure == null)
                        try { CaptureDefinition(owner); } catch (Exception error) { failure ??= error; }
                    // Preserve the original lifecycle even when this experiment
                    // cannot qualify the definition. Report at controlled startup.
                    original(owner);
                }));
            // UIManager is already alive before ModLoader constructs mods in
            // the actual build (188). Do not replay or intercept its old Awake.
            var existing = typeof(UIManager).GetField("_instance", Static)!.GetValue(null) as UIManager;
            if (existing != null)
                try { CaptureDefinition(existing); } catch (Exception error) { failure ??= error; }
            Modding.Logger.Log("[HKTAS] Menu Flash definition startup: existingUIManager=" + (existing != null)
                + ";inputTick=" + InControl.InputManager.CurrentTick + ";definitionCaptured=" + observed);
        }
        internal static Animator Current() => UIManager.instance.mainMenuButtons.startButton.flashEffect
            ?? throw new InvalidOperationException("Original main-menu Start Flash entry is absent.");
        // Common startup, before source unloading, while the ordinary loader
        // still admits PlayerLoops. Unity owns and advances the original fade;
        // no iterator is pumped manually and no Movie input is synthesized.
        internal bool AdvanceNaturalMenuClose()
        {
            var ui = UIManager.instance;
            var screen = ui.mainMenuScreen;
            if (screen == null || !Current().transform.IsChildOf(screen.transform))
                throw new InvalidOperationException("Original Flash does not belong to the exact main-menu screen.");
            if (closingMenu != null) {
                if (!ReferenceEquals(ui, closingUi) || !ReferenceEquals(screen, closingMenu))
                    throw new InvalidOperationException("Original main-menu close owner changed.");
                if (Time.frameCount < menuCloseFirstFrame || Time.frameCount - menuCloseFirstFrame > 150)
                    throw new InvalidOperationException("Original main-menu fade exceeded its finite native-frame bound.");
                if (!menuCloseCompleted) return false;
                if (screen.gameObject.activeSelf || screen.gameObject.activeInHierarchy || screen.alpha != 0f || screen.interactable
                    || menuCloseRoutine != null)
                    throw new InvalidOperationException("Original main-menu fade did not complete its natural cold state.");
                closingMenu = null; closingUi = null;
                return true;
            }
            if (!screen.gameObject.activeSelf && !screen.gameObject.activeInHierarchy) return true;
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "Menu_Title"
                || ui.menuState != GlobalEnums.MainMenuState.MAIN_MENU || !screen.gameObject.activeInHierarchy
                || ui.IsFadingMenu || ui.IsAnimatingMenus
                || !ui.isActiveAndEnabled || float.IsNaN(screen.alpha) || float.IsInfinity(screen.alpha) || screen.alpha < 0f || screen.alpha > 1f
                || float.IsNaN(ui.MENU_FADE_SPEED) || float.IsInfinity(ui.MENU_FADE_SPEED) || ui.MENU_FADE_SPEED <= 0f)
                throw new InvalidOperationException("Active original main-menu close has an unsupported state.");
            closingUi = ui; closingMenu = screen; menuCloseFirstFrame = Time.frameCount; menuCloseCompleted = false;
            menuCloseRoutine = ui.StartCoroutine(CloseOriginalMenu(ui, screen));
            if (menuCloseRoutine == null) throw new InvalidOperationException("Original main-menu fade was not scheduled.");
            Modding.Logger.Log("[HKTAS] Common original main-menu close scheduled before source unload; originalFade=true;MovieInputSynthesized=false.");
            return false;
        }
        private IEnumerator CloseOriginalMenu(UIManager ui, CanvasGroup screen)
        {
            menuFadeRoutine = ui.StartCoroutine(ui.FadeOutCanvasGroup(screen));
            yield return menuFadeRoutine;
            menuFadeRoutine = null;
            menuCloseRoutine = null; menuCloseCompleted = true;
            Modding.Logger.Log("[HKTAS] Common original main-menu fade completed naturally; nativeFrames=" + (Time.frameCount - menuCloseFirstFrame));
        }
        internal bool RequireStartup(bool boundary = false)
        {
            if (failure != null) throw new InvalidOperationException("Original menu Flash cold definition failed.", failure);
            if (!observed || template == null) throw new InvalidOperationException("Original fixed menu Flash serialized resource definition is absent.");
            RequireCold(template, boundary ? "B-template-before-clone" : "startup-template-guard"); return true;
        }
        private void CaptureDefinition(UIManager ui)
        {
            if (observed || FrozenRoot != null || ui == null || ui.mainMenuButtons == null || ui.mainMenuButtons.startButton == null)
                throw new InvalidOperationException("Original menu Flash serialized origin is absent, repeated or ambiguous.");
            var animator = ui.mainMenuButtons.startButton.flashEffect;
            if (animator == null) throw new InvalidOperationException("Original serialized Start Flash animator is absent before UIManager Awake.");
            var source = animator.gameObject; var image = source.GetComponent<Image>();
            Directory(source); RequireOriginalSerialized(source);
            var provenance = new LoadingAssetProvenanceObservation();
            controller = animator.runtimeAnimatorController;
            if (controller == null || !IsOriginalOrigin(provenance.Capture(controller.GetInstanceID()), 63))
                throw new InvalidOperationException("Original Start Flash controller is not exact sharedassets0.assets/63.");
            var liveSprite = image.sprite;
            if (liveSprite != null && IsOriginalOrigin(provenance.Capture(liveSprite.GetInstanceID()), 101)) blank = liveSprite;
            else {
                var candidates = Resources.FindObjectsOfTypeAll<Sprite>();
                if (candidates.Length > 16384) throw new InvalidOperationException("Original Flash loaded Sprite inventory exceeds its existing asset bound.");
                var matches = candidates.Where(value => value != null && IsOriginalOrigin(provenance.Capture(value.GetInstanceID()), 101)).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("Exact original sharedassets0.assets/101 Sprite is not uniquely already loaded.");
                blank = matches[0];
            }
            if (blank.name != "blank_frame" || controller.name != "Menu Flash Effect")
                throw new InvalidOperationException("Exact original menu Flash asset metadata differs.");
            using var preservation = new Preservation(ui, source, preserveSource: true);
            holder = new GameObject("__HKTAS_OriginalMenuFlashColdTemplate");
            holder.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(holder);
            template = UnityEngine.Object.Instantiate(source, holder.transform, false);
            template.name = source.name;
            // This is the serialized input of the complete four-component cold
            // producer, not a write to the source/live target Image. A direct
            // original SerializeField assignment runs no Graphic dirty setter.
            var spriteField = Field(typeof(Image), "m_Sprite");
            if (spriteField.FieldType != typeof(Sprite) || spriteField.IsInitOnly || spriteField.IsNotSerialized
                || !spriteField.IsDefined(typeof(SerializeField), false)) throw new InvalidOperationException("Original Image serialized Sprite field ABI differs.");
            spriteField.SetValue(template.GetComponent<Image>(), blank);
            RequireOriginalSerialized(template); RequireCold(template, "startup-template-after-serialized-bind");
            if (!ReferenceEquals(template.GetComponent<Image>().sprite, blank)
                || !ReferenceEquals(template.GetComponent<Animator>().runtimeAnimatorController, controller))
                throw new InvalidOperationException("Original Flash clone did not preserve its actual serialized asset aliases.");
            preservation.Verify();
            FrozenRoot = template; observed = true;
            Modding.Logger.Log("[HKTAS] Original menu Flash complete four-component serialized definition: Sprite=sharedassets0.assets/101;controller=sharedassets0.assets/63;inputTick="
                + InControl.InputManager.CurrentTick + ";originalOwnerUnchanged=true;GraphicSetterInvoked=false;menuEventInvoked=false.");
        }
        internal static bool IsOriginalOrigin(object record, long identifier)
        {
            var type = record.GetType();
            if (type.Assembly != typeof(LoadingAssetProvenanceObservation).Assembly) throw new InvalidOperationException("Menu Flash provenance is not the original Runtime record.");
            var kind = type.GetProperty("kind")?.GetValue(record) as string;
            var path = type.GetProperty("sourcePath")?.GetValue(record) as string;
            var local = type.GetProperty("localIdentifier")?.GetValue(record);
            return kind == "serialized" && path == "sharedassets0.assets"
                && local is long actual && actual == identifier;
        }
        internal static IReadOnlyDictionary<string, UnityEngine.Object> Directory(GameObject root)
        {
            if (root == null || root.name != "FlashEffect" || root.transform.childCount != 0 || root.layer != 27
                || !root.GetComponents<Component>().Select(value => value.GetType()).SequenceEqual(new[] {
                    typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Animator) }))
                throw new InvalidOperationException("Original menu Flash exact four-component directory differs.");
            return new SortedDictionary<string, UnityEngine.Object>(StringComparer.Ordinal) {
                ["$"] = root, ["$:UnityEngine.RectTransform[0]"] = root.transform,
                ["$:UnityEngine.CanvasRenderer[0]"] = root.GetComponent<CanvasRenderer>(),
                ["$:UnityEngine.UI.Image[0]"] = root.GetComponent<Image>(), ["$:UnityEngine.Animator[0]"] = root.GetComponent<Animator>() };
        }
        internal static IReadOnlyDictionary<string, object> IncomingDirectory(GameObject root)
        {
            var directory = Directory(root).ToDictionary(pair => pair.Key, pair => (object)pair.Value, StringComparer.Ordinal);
            var image = root.GetComponent<Image>(); RequireIdleManaged(image);
            directory.Add("$:UnityEngine.UI.Image[0]/readonly/m_ColorTweenRunner", Runner.GetValue(image)!);
            directory.Add("$:UnityEngine.UI.Image[0]/readonly/m_Corners", Corners.GetValue(image)!);
            directory.Add("$:UnityEngine.UI.Image[0]/event/m_OnCullStateChanged", CanonicalMarkerEventCarry.EventField.GetValue(image)!);
            return directory;
        }
        private static void RequireIdleManaged(Image image)
        {
            if (typeof(Graphic).Module.ModuleVersionId != new Guid("b4d135af-a1fd-407b-bcc7-280e400336cf"))
                throw new InvalidOperationException("Original Flash UI module ABI differs.");
            var runner = Runner.GetValue(image) ?? throw new InvalidOperationException("Original Flash color runner is absent.");
            if (Field(runner.GetType(), "m_Tween").GetValue(runner) != null)
                throw new InvalidOperationException("Active original Flash color tween continuation is unsupported.");
            var container = Field(runner.GetType(), "m_CoroutineContainer").GetValue(runner);
            if (container != null && !ReferenceEquals(container, image) || !(Corners.GetValue(image) is Vector3[] corners) || corners.Length != 4)
                throw new InvalidOperationException("Original Flash readonly constructor alias differs.");
            foreach (var name in new[] { "m_OnDirtyLayoutCallback", "m_OnDirtyVertsCallback", "m_OnDirtyMaterialCallback" })
                if (Field(typeof(Graphic), name).GetValue(image) != null)
                    throw new InvalidOperationException("Original Flash has an unsupported dirty callback.");
            RequireEmptyEvent(image.onCullStateChanged);
        }
        private static void RequireEmptyEvent(object value)
        {
            CanonicalMarkerEventCarry.Types();
            var eventBase = value.GetType().BaseType!.BaseType!;
            var calls = Field(eventBase, "m_Calls").GetValue(value)!;
            foreach (var name in new[] { "m_PersistentCalls", "m_RuntimeCalls", "m_ExecutingCalls" })
                if (!(Field(calls.GetType(), name).GetValue(calls) is IList list) || list.Count != 0)
                    throw new InvalidOperationException("Original Flash nonempty cull listeners are unsupported.");
            var persistent = Field(eventBase, "m_PersistentCalls").GetValue(value)!;
            if (!(Field(persistent.GetType(), "m_Calls").GetValue(persistent) is IList entries) || entries.Count != 0)
                throw new InvalidOperationException("Original Flash persistent cull listeners are unsupported.");
        }
        private static int Bits(float value) => BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
        private static bool Same(Vector2 a, Vector2 b) => Bits(a.x) == Bits(b.x) && Bits(a.y) == Bits(b.y);
        private static bool Same(Vector3 a, Vector3 b) => Bits(a.x) == Bits(b.x) && Bits(a.y) == Bits(b.y) && Bits(a.z) == Bits(b.z);
        internal static void RequireOriginalImageSchema()
        {
            if (typeof(Image).Module.ModuleVersionId != new Guid("b4d135af-a1fd-407b-bcc7-280e400336cf"))
                throw new InvalidOperationException("Original Flash Image module ABI differs.");
            var actual = CanonicalCarryGraphCodec.Fields(typeof(Image)).Where(field => !field.IsNotSerialized
                && (field.IsPublic || field.IsDefined(typeof(SerializeField), false))).Select(field => field.Name).OrderBy(name => name, StringComparer.Ordinal);
            var expected = new[] { "m_Material", "m_Color", "m_RaycastTarget", "m_RaycastPadding", "m_Maskable", "m_OnCullStateChanged", "m_Sprite",
                "m_Type", "m_PreserveAspect", "m_FillCenter", "m_FillMethod", "m_FillAmount", "m_FillClockwise", "m_FillOrigin", "m_UseSpriteMesh", "m_PixelsPerUnitMultiplier" };
            if (!actual.SequenceEqual(expected.OrderBy(name => name, StringComparer.Ordinal)))
                throw new InvalidOperationException("Original Flash full Image serialized schema differs.");
        }
        private static void RequireOriginalSerialized(GameObject go)
        {
            Directory(go); var image = go.GetComponent<Image>(); var animator = go.GetComponent<Animator>();
            var rect = (RectTransform)go.transform;
            RequireOriginalImageSchema();
            var color = image.color; var padding = image.raycastPadding; var rotation = rect.localRotation;
            if (!go.activeSelf || !image.enabled || !animator.enabled
                || Field(typeof(Graphic), "m_Material").GetValue(image) != null
                || Bits(color.r) != Bits(1f) || Bits(color.g) != Bits(1f) || Bits(color.b) != Bits(1f) || Bits(color.a) != Bits(1f) || image.raycastTarget
                || Bits(padding.x) != 0 || Bits(padding.y) != 0 || Bits(padding.z) != 0 || Bits(padding.w) != 0
                || !image.maskable || image.type != Image.Type.Simple || image.preserveAspect || !image.fillCenter || image.fillMethod != Image.FillMethod.Radial360
                || Bits(image.fillAmount) != Bits(1f) || !image.fillClockwise || image.fillOrigin != 0 || image.useSpriteMesh || Bits(image.pixelsPerUnitMultiplier) != Bits(1f)
                || animator.avatar != null || animator.cullingMode != AnimatorCullingMode.AlwaysAnimate || animator.updateMode != AnimatorUpdateMode.UnscaledTime
                || animator.applyRootMotion || animator.keepAnimatorControllerStateOnDisable
                || !Same(rect.anchorMin, Vector2.zero) || !Same(rect.anchorMax, Vector2.one) || !Same(rect.anchoredPosition, Vector2.zero) || !Same(rect.sizeDelta, Vector2.zero)
                || !Same(rect.pivot, new Vector2(.5f, .5f)) || !Same(rect.localPosition, Vector3.zero)
                || Bits(rotation.x) != 0 || Bits(rotation.y) != 0 || Bits(rotation.z) != 0 || Bits(rotation.w) != Bits(1f)
                || Bits(rect.localScale.x) != Bits(1.2744134664535522f) || Bits(rect.localScale.y) != Bits(1.2744139432907104f) || Bits(rect.localScale.z) != Bits(1.274413824081421f)
                || !go.GetComponent<CanvasRenderer>().cullTransparentMesh)
                throw new InvalidOperationException("Original Flash complete serialized configuration differs from fixed level1/562.");
            var native = Native(animator, 1696);
            if (native[1146] != 0 || native[1147] != 0 || native[1484] != 1 || native[1485] != 1 || native[1155] != 0)
                throw new InvalidOperationException("Original Flash hidden serialized Animator bools differ.");
            RequireIdleManaged(image);
        }
        private void RequireCold(GameObject go, string stage, Canvas? expectedParentCanvas = null)
        {
            Directory(go); var image = go.GetComponent<Image>();
            if (go.activeInHierarchy || !ReferenceEquals(image.sprite, blank) || !ReferenceEquals(go.GetComponent<Animator>().runtimeAnimatorController, controller)
                || CachedMesh.GetValue(image) != null)
                throw new InvalidOperationException("Menu Flash producer is active or has a foreign asset/live mesh.");
            RequireIdleManaged(image);
            using var engine = new LoadingEngineClockProbe.CheckpointValidation();
            var flags = BoundaryMonoLifecycle.ObserveUnstartedUi(image);
            if (flags[0] != 0 || flags[1] != 0 || flags[3] != 0 || flags[4] != 0)
                throw new InvalidOperationException("Menu Flash Image is not a real never-awakened instance.");
            var animator = Native(go.GetComponent<Animator>(), 1696); var canvas = Native(go.GetComponent<CanvasRenderer>(), 304);
            var evidence = NativeEvidence(go, stage, animator, canvas, flags);
            if (!stage.StartsWith("startup-", StringComparison.Ordinal) || startupNativeStages.Add(stage))
                Modding.Logger.Log("[HKTAS] Menu Flash finite native stage: " + Newtonsoft.Json.JsonConvert.SerializeObject(evidence));
            try { RequireColdNativeBound(animator, canvas, expectedParentCanvas == null ? 0 : ((IntPtr)Pointer.GetValue(expectedParentCanvas)!).ToInt64()); }
            catch (Exception error) { throw new InvalidOperationException("Menu Flash native stage rejected: "
                + Newtonsoft.Json.JsonConvert.SerializeObject(evidence), error); }
        }
        private static object NativeEvidence(GameObject go, string stage, byte[] animator, byte[] canvas, byte[] imageLifecycle)
        {
            var pointers = new Dictionary<long, int>();
            object PointerValue(byte[] bytes, int offset) {
                var raw = BitConverter.ToInt64(bytes, offset);
                if (raw == 0) return new { present = false, localAlias = 0 };
                if (!pointers.TryGetValue(raw, out var alias)) { alias = pointers.Count + 1; pointers.Add(raw, alias); }
                return new { present = true, localAlias = alias };
            }
            var animatorOwner = go.GetComponent<Animator>(); var canvasOwner = go.GetComponent<CanvasRenderer>();
            var ownerPointer = (IntPtr)Pointer.GetValue(go)!;
            var nearest = go.GetComponentsInParent<Canvas>(true).FirstOrDefault();
            var parentPointer = BitConverter.ToInt64(canvas, 232);
            var parentBinding = parentPointer == 0 ? "null" : nearest != null && ((IntPtr)Pointer.GetValue(nearest)!).ToInt64() == parentPointer
                ? "exact-nearest-original-Canvas" : "unregistered";
            return new {
                stage, diagnosticOnly = true, completeNativeCoverage = false,
                objectInstance = go.GetInstanceID(), animatorInstance = animatorOwner.GetInstanceID(), canvasRendererInstance = canvasOwner.GetInstanceID(),
                imageLifecycle = imageLifecycle.Select(value => (int)value).ToArray(), go.activeSelf, go.activeInHierarchy,
                animatorGameObjectBound = BitConverter.ToInt64(animator, 48) == ownerPointer.ToInt64(),
                rendererGameObjectBound = BitConverter.ToInt64(canvas, 48) == ownerPointer.ToInt64(),
                animatorAvatar = new { constant = PointerValue(animator, 440), input = PointerValue(animator, 448), output = PointerValue(animator, 456),
                    memory = PointerValue(animator, 464), workspace = PointerValue(animator, 472), binding = PointerValue(animator, 480),
                    memorySize = BitConverter.ToUInt64(animator, 488), owns = animator[496], initialized = animator[497] },
                animatorBindings = new { generic = PointerValue(animator, 520), label = BitConverter.ToInt32(animator, 528),
                    destroyFunction = PointerValue(animator, 536), animationSet = PointerValue(animator, 544) },
                controllerCache = new { data = PointerValue(animator, 1160), label = BitConverter.ToInt32(animator, 1168),
                    size = BitConverter.ToUInt64(animator, 1176), capacity = BitConverter.ToUInt64(animator, 1184), layers = BitConverter.ToInt32(animator, 1192) },
                controllerPlayable = PointerValue(animator, 1520), animatorBindingsVersion = BitConverter.ToUInt32(animator, 1528),
                playableGraph = new { handle = PointerValue(animator, 1536), version = BitConverter.ToUInt32(animator, 1544) },
                playableOutput = new { handle = PointerValue(animator, 1552), version = BitConverter.ToUInt32(animator, 1560) },
                rendererMesh = PointerValue(canvas, 72),
                rendererPushMaterials = new { data = PointerValue(canvas, 104), size = BitConverter.ToUInt64(canvas, 120) },
                rendererPopMaterials = new { data = PointerValue(canvas, 136), size = BitConverter.ToUInt64(canvas, 152) },
                parentCanvas = PointerValue(canvas, 232), parentCanvasBinding = parentBinding, nearestCanvasInstance = nearest == null ? 0 : nearest.GetInstanceID(),
                rendererInterestInitialized = canvas[248],
                pushHandle = new { canvasData = PointerValue(canvas, 256), instructionsIndex = BitConverter.ToInt32(canvas, 264),
                    handleIndex = BitConverter.ToInt32(canvas, 268), absoluteIndex = BitConverter.ToInt32(canvas, 272) },
                popHandle = new { canvasData = PointerValue(canvas, 280), instructionsIndex = BitConverter.ToInt32(canvas, 288),
                    handleIndex = BitConverter.ToInt32(canvas, 292), absoluteIndex = BitConverter.ToInt32(canvas, 296) }
            };
        }
        internal static void RequireColdNative(byte[] animator, byte[] canvas)
            => RequireColdNativeBound(animator, canvas, 0);
        internal static void RequireColdNativeBound(byte[] animator, byte[] canvas, long expectedParentCanvas)
        {
            if (animator == null || animator.Length != 1696 || canvas == null || canvas.Length != 304)
                throw new InvalidOperationException("Menu Flash native constructor ABI differs.");
            bool Zero(byte[] bytes, int offset, int length) => Enumerable.Range(offset, length).All(index => bytes[index] == 0);
            // Exact fixed PDB ctor/streamed-transfer/Awake inactive path. These
            // are observations only; no native flag/handle is ever written.
            if (!Zero(animator, 440, 58) || !Zero(animator, 520, 8) || !Zero(animator, 536, 16)
                || BitConverter.ToInt32(animator, 528) != 30
                || !Zero(animator, 1160, 8) || !Zero(animator, 1176, 8) || !Zero(animator, 1192, 4)
                || BitConverter.ToInt32(animator, 1168) != 30 || BitConverter.ToUInt64(animator, 1184) != 1
                || !Zero(animator, 1520, 8) || BitConverter.ToUInt32(animator, 1528) != 2 || !Zero(animator, 1536, 12) || !Zero(animator, 1552, 12)
                || !Zero(canvas, 72, 8) || !Zero(canvas, 104, 8) || !Zero(canvas, 120, 8) || !Zero(canvas, 136, 8) || !Zero(canvas, 152, 8)
                || BitConverter.ToInt64(canvas, 232) != expectedParentCanvas || canvas[248] != 0 || !Zero(canvas, 256, 8) || !Zero(canvas, 280, 8)
                || BitConverter.ToInt32(canvas, 264) != -1 || BitConverter.ToInt32(canvas, 268) != -1 || BitConverter.ToInt32(canvas, 272) != -1
                || BitConverter.ToInt32(canvas, 288) != -1 || BitConverter.ToInt32(canvas, 292) != -1 || BitConverter.ToInt32(canvas, 296) != -1)
                throw new InvalidOperationException("Menu Flash clone retained live Animator/Canvas native runtime state.");
        }
        private static readonly FieldInfo Pointer = typeof(UnityEngine.Object).GetField("m_CachedPtr", Instance)!;
        internal static void RequireOriginalCanvasPreserved(byte[] before, byte[] after)
        {
            if (before == null || before.Length != 584 || after == null || after.Length != 584 || !before.SequenceEqual(after))
                throw new InvalidOperationException("Flash replacement changed original native Canvas state during the synchronous producer operation.");
        }
        internal static void RequireOriginalCanvasAfterParent(byte[] before, byte[] after, bool hierarchyDirty)
        {
            if (before == null || before.Length != 584) throw new InvalidOperationException("Original Canvas ABI differs.");
            var expected = (byte[])before.Clone();
            if (hierarchyDirty) expected[280] |= 4;
            RequireOriginalCanvasPreserved(expected, after);
        }
        internal static void RequireOriginalCanvasAfterRetirement(byte[] before, byte[] after, bool hierarchyDirty, bool ownedRendererRemoved, bool originalFenceCompleted)
        {
            if (before == null || before.Length != 584 || originalFenceCompleted && !ownedRendererRemoved)
                throw new InvalidOperationException("Original Canvas retirement phase/ABI differs.");
            var expected = (byte[])before.Clone();
            if (hierarchyDirty) expected[280] |= 4;
            if (ownedRendererRemoved) expected[280] |= 6;
            if (originalFenceCompleted) Array.Clear(expected, 424, 12);
            RequireOriginalCanvasPreserved(expected, after);
        }
        internal static void RequireRetiredInstruction(byte[] before, byte[] after, byte[] handleSlot)
        {
            if (before == null || before.Length != 720 || after == null || after.Length != 720
                || handleSlot == null || handleSlot.Length != 8 || handleSlot.Any(value => value != 0))
                throw new InvalidOperationException("Original Flash renderer retirement slot/ABI differs.");
            var expected = (byte[])before.Clone(); Array.Clear(expected, 512, 8);
            if (!expected.SequenceEqual(after)) throw new InvalidOperationException("Original Flash renderer retirement changed another instruction field.");
        }
        internal static void RequireRetirementHandle(long canvasData, long expectedCanvasData, long slotOwner, long expectedSlotOwner,
            int instructionIndex, int handleIndex, ulong instructionCount, ulong handleCount)
        {
            if (canvasData == 0 || canvasData != expectedCanvasData || slotOwner == 0 || slotOwner != expectedSlotOwner
                || instructionCount > 16000 || handleCount > 16000 || instructionIndex < 0 || handleIndex < 0
                || (ulong)instructionIndex >= instructionCount || (ulong)handleIndex >= handleCount)
                throw new InvalidOperationException("Original Flash renderer handle is foreign or out of bounds.");
        }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr read);
        private static byte[] Native(Component owner, int size)
        {
            LoadingEngineClockProbe.RequirePinnedEngine(); var pointer = (IntPtr)Pointer.GetValue(owner)!; var bytes = new byte[size];
            if (pointer == IntPtr.Zero || !ReadProcessMemory(GetCurrentProcess(), pointer, bytes, new UIntPtr((uint)size), out var read) || read.ToUInt64() != (ulong)size
                || BitConverter.ToInt32(bytes, 8) != owner.GetInstanceID()) throw new InvalidOperationException("Menu Flash native owner read differs.");
            return bytes;
        }
        private static byte[] NativeSlot(IntPtr pointer, int size)
        {
            if (pointer == IntPtr.Zero || size < 1 || size > 720) throw new InvalidOperationException("Original Flash native slot bounds differ.");
            var bytes = new byte[size];
            if (!ReadProcessMemory(GetCurrentProcess(), pointer, bytes, new UIntPtr((uint)size), out var read) || read.ToUInt64() != (ulong)size)
                throw new InvalidOperationException("Original Flash native slot cannot be read completely.");
            return bytes;
        }
        internal static object? ObserveCanvasDomain(Func<object, string?> reference)
        {
            if (FrozenRoot == null) return null;
            var ui = typeof(UIManager).GetField("_instance", Static)!.GetValue(null) as UIManager;
            if (ui == null || ui.mainMenuButtons == null || ui.mainMenuButtons.startButton == null
                || ui.mainMenuButtons.startButton.flashEffect == null) return new { unsupported = "original Flash entry unavailable" };
            var owners = ui.mainMenuButtons.startButton.flashEffect.GetComponentsInParent<Canvas>(true);
            if (owners.Length > 128) throw new InvalidOperationException("Original Flash Canvas observation exceeds the ancestor bound.");
            var identities = owners.ToDictionary(owner => ((IntPtr)Pointer.GetValue(owner)!).ToInt64(), owner => reference(owner));
            return owners.Select(owner => {
                var bytes = Native(owner, 584); var parent = BitConverter.ToInt64(bytes, 448);
                return new { owner = reference(owner), parentCanvas = parent == 0 ? "null" : identities.TryGetValue(parent, out var identity) ? identity : "unsupported-native-parent",
                    owner.gameObject.activeSelf, owner.gameObject.activeInHierarchy, dirtyFlag = BitConverter.ToUInt16(bytes, 280),
                    handleCount = BitConverter.ToUInt64(bytes, 224), instructionCount = BitConverter.ToUInt64(bytes, 256),
                    handleDataPresent = BitConverter.ToInt64(bytes, 208) != 0, instructionDataPresent = BitConverter.ToInt64(bytes, 240) != 0,
                    vertices = BitConverter.ToUInt32(bytes, 272), indices = BitConverter.ToUInt32(bytes, 276),
                    jobsFencePresent = BitConverter.ToInt64(bytes, 424) != 0, cachedBoundsDirty = bytes[508],
                    completeNativeCoverage = false };
            }).ToArray();
        }
        internal object Reset(LoadingGameplayOracle observer, Action<Image> beginRetirement, Action finishRetirement)
        {
            LoadingSerialSceneProbe.RequireCanonicalMutationBoundary(); RequireStartup(boundary: true);
            var old = Current().gameObject; var parent = old.transform.parent; var sibling = old.transform.GetSiblingIndex();
            if (parent == null || parent.gameObject.activeInHierarchy || old.activeInHierarchy)
                throw new InvalidOperationException("Active menu Flash needs explicit future natural-close support.");
            var parentCanvas = parent.GetComponentsInParent<Canvas>(true).FirstOrDefault();
            Directory(old); var image = old.GetComponent<Image>(); RequireIdleManaged(image);
            var mesh = CachedMesh.GetValue(image) as Mesh;
            if (!ReferenceEquals(CachedMesh.GetValue(image), null) && mesh == null)
                throw new InvalidOperationException("Original Flash cached mesh wrapper is already dead.");
            RequireNoExternalMeshConsumers(image, mesh);
            using var preservation = new Preservation(UIManager.instance, old, preserveSource: false);
            var incoming = new LoadingGeoIncomingReferences();
            var frozen = new LoadingGeoIncomingReferences();
            CanonicalGeoColdTemplate.ObserveIncoming(GameCameras.instance.geoCounter, menuFlashIncoming: incoming, menuFlashRoot: old,
                frozenMenuFlashIncoming: frozen, frozenMenuFlashRoot: template!);
            frozen.RequireNoExternalConsumers();
            var owners = new Dictionary<int, MonoBehaviour> { [image.GetInstanceID()] = image };
            observer.RecaptureOwnedHudScheduler(LoadingSerialSceneProbe.RequireCanonicalMutationBoundary, () => { });
            var transition = observer.PrepareOwnedHudLifecycle(owners, _ => false, out var retireStarts);
            var created = UnityEngine.Object.Instantiate(template!, holder!.transform, false); created.name = template!.name;
            RequireCold(created, "B-new-clone-before-parent"); RequireOriginalSerialized(created);
            RequireCold(created, "B-new-clone-immediately-before-parent");
            preservation.PrepareParentCanvasCallback(parentCanvas);
            created.transform.SetParent(parent, false); preservation.VerifyNativeCanvases("B-after-original-parent-callback");
            RequireCold(created, "B-new-clone-after-parent", parentCanvas);
            created.transform.SetSiblingIndex(sibling); RequireCold(created, "B-new-clone-after-sibling", parentCanvas);
            preservation.BindReplacement(created);
            var plan = incoming.Prepare(IncomingDirectory(created), witness => Modding.Logger.Log("[HKTAS] Flash original incoming exact slots: " + Newtonsoft.Json.JsonConvert.SerializeObject(witness)));
            beginRetirement(image); plan.Apply(); preservation.PrepareOriginalRendererRetirement(image);
            UnityEngine.Object.DestroyImmediate(old); preservation.CompleteOriginalRendererRetirement(); retireStarts(); finishRetirement();
            observer.RecaptureOwnedHudScheduler(LoadingSerialSceneProbe.RequireCanonicalMutationBoundary, transition);
            plan.VerifyReadback(); RequireCold(created, "B-final-after-original-retirement", parentCanvas); RequireOriginalSerialized(created); preservation.Verify();
            if (!ReferenceEquals(Current().gameObject, created) || old != null || mesh != null || !ReferenceEquals(created.transform.parent, parent)
                || created.transform.GetSiblingIndex() != sibling) throw new InvalidOperationException("Original Flash retirement/entry/parent readback differs.");
            return new { coldFourComponents = true, startupSerializedResourceDefinition = true, exactIncomingReadback = true,
                originalDestroy = true, exactSerializedProvenance = true, menuEventsInvoked = false, completeNativeCoverage = false };
        }
        private static void RequireNoExternalMeshConsumers(Image image, Mesh? mesh)
        {
            if (mesh == null) return;
            foreach (var filter in Resources.FindObjectsOfTypeAll<MeshFilter>())
                if (filter != null && filter.sharedMesh == mesh) throw new InvalidOperationException("Flash mesh has an external MeshFilter consumer.");
            foreach (var collider in Resources.FindObjectsOfTypeAll<MeshCollider>())
                if (collider != null && collider.sharedMesh == mesh) throw new InvalidOperationException("Flash mesh has an external MeshCollider consumer.");
            foreach (var renderer in Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>())
                if (renderer != null && renderer.sharedMesh == mesh) throw new InvalidOperationException("Flash mesh has an external skinned-renderer consumer.");
            using var validation = new LoadingEngineClockProbe.CheckpointValidation();
            var current = image.GetComponent<CanvasRenderer>(); var bytes = Native(current, 304); var shared = BitConverter.ToInt64(bytes, 72);
            if (shared == 0) return;
            var renderers = Resources.FindObjectsOfTypeAll<CanvasRenderer>();
            if (renderers.Length > 16000) throw new InvalidOperationException("Flash native Canvas consumer observation exceeds the existing owner bound.");
            foreach (var renderer in renderers)
                if (renderer != null && !ReferenceEquals(renderer, current) && BitConverter.ToInt64(Native(renderer, 304), 72) == shared)
                    throw new InvalidOperationException("Flash mesh data has an external native CanvasRenderer consumer.");
        }
        private sealed class Preservation : IDisposable
        {
            private readonly LoadingFieldDigest fields;
            private readonly CanonicalMapKeyTextExternalGuard managedGuard;
            private readonly List<Tuple<FieldInfo, object?>> singletonRoots = new List<Tuple<FieldInfo, object?>>();
            private readonly List<Tuple<Func<object?>, string, string>> nativeOuter = new List<Tuple<Func<object?>, string, string>>();
            private readonly List<Tuple<Canvas, byte[]>> nativeCanvases = new List<Tuple<Canvas, byte[]>>();
            private readonly HashSet<Canvas> parentDirtyCanvases = new HashSet<Canvas>();
            private readonly UnityEngine.Random.State random = UnityEngine.Random.state;
            private readonly uint[] particles = LoadingGameplayOracle.CaptureParticleRandomState();
            private static readonly FieldInfo PlayerInstance = typeof(PlayerData).GetField("_instance", Static)!;
            private static readonly FieldInfo SceneInstance = typeof(SceneData).GetField("_instance", Static)!;
            private readonly object? player = PlayerInstance.GetValue(null), scene = SceneInstance.GetValue(null);
            private readonly UIManager ui; private readonly GameObject original; private readonly bool preserveSource;
            private readonly byte[]? nativeAnimator, nativeCanvas;
            private readonly List<Tuple<Canvas, IntPtr, IntPtr, byte[]>> retiredInstructionSlots = new List<Tuple<Canvas, IntPtr, IntPtr, byte[]>>();
            private readonly HashSet<Canvas> retirementDirtyCanvases = new HashSet<Canvas>();
            private readonly HashSet<Canvas> retirementFenceCanvases = new HashSet<Canvas>();
            private bool retirementPrepared, retirementCompleted;
            internal Preservation(UIManager ui, GameObject original, bool preserveSource)
            {
                this.ui = ui; this.original = original; this.preserveSource = preserveSource;
                fields = new LoadingFieldDigest(value => value is UnityEngine.Object native ? "flash-preserve-native:" + native.GetInstanceID() : null,
                    type => type == typeof(MonoBehaviour) || type == typeof(UnityEngine.Object));
                var roots = new List<KeyValuePair<string, object?>>();
                void Add(object? value, string name) => roots.Add(new KeyValuePair<string, object?>(name, value));
                Add(player, "actual-PlayerData"); Add(scene, "actual-SceneData");
                if (HeroController.SilentInstance != null) Add(HeroController.SilentInstance, "actual-Hero");
                // B replacement intentionally changes only the observed Flash
                // incoming slots. Other menu owner fields are compared with a
                // logical Flash identity, preserving their complete graphs.
                Add(ui, "original-UIManager"); Add(ui.mainMenuButtons, "original-main-menu"); Add(ui.mainMenuButtons.startButton, "original-start-button");
                var depth = 0;
                for (var node = original.transform.parent; node != null; node = node.parent) {
                    if (++depth > 128) throw new InvalidOperationException("Flash original ancestor chain exceeds the existing depth bound.");
                    var actual = node; var name = "original-menu-ancestor[" + depth + "]";
                    object View() {
                        var rect = actual as RectTransform;
                        return new { owner = actual, parent = actual.parent, actual.gameObject.activeSelf, actual.gameObject.activeInHierarchy,
                            actual.localPosition, actual.localRotation, actual.localScale, actual.hasChanged,
                            anchorMin = rect == null ? (Vector2?)null : rect.anchorMin, anchorMax = rect == null ? (Vector2?)null : rect.anchorMax,
                            pivot = rect == null ? (Vector2?)null : rect.pivot, sizeDelta = rect == null ? (Vector2?)null : rect.sizeDelta,
                            anchoredPosition = rect == null ? (Vector2?)null : rect.anchoredPosition };
                    }
                    nativeOuter.Add(Tuple.Create<Func<object?>, string, string>(View, name, Digest(View(), name, true)));
                    foreach (var component in actual.GetComponents<Component>().Where(value => value is Canvas || value is CanvasGroup)) {
                        var owner = component; var id = name + "/" + owner.GetType().FullName;
                        if (owner is Canvas canvasOwner) {
                            // Pinned UI::Canvas embeds CanvasData at +144; its
                            // dirtyFlag ushort is +136. Original parent callbacks
                            // OR bit 4 into the exact native parent chain. Preserve
                            // all other bytes of these same synchronous owners;
                            // this explicit visual Canon is not an ignored bit.
                            var before = Native(canvasOwner, 584);
                            nativeCanvases.Add(Tuple.Create(canvasOwner, before));
                        }
                        object? Capture() => LoadingNativeComponentObservation.Capture(owner,
                            reason => throw new InvalidOperationException("Flash outer menu native value is unsupported: " + reason));
                        nativeOuter.Add(Tuple.Create<Func<object?>, string, string>(Capture, id, Digest(Capture(), id, true)));
                    }
                }
                foreach (var type in new[] { typeof(GraphicRegistry), typeof(CanvasUpdateRegistry), typeof(TMPro.TMP_UpdateManager) }) {
                    var singleton = type.GetField("s_Instance", Static) ?? throw new MissingFieldException(type.FullName, "s_Instance");
                    singletonRoots.Add(new Tuple<FieldInfo, object?>(singleton, singleton.GetValue(null)));
                    Add(singleton.GetValue(null), "original-UI-queues:" + type.FullName);
                }
                var manager = typeof(GameManager).GetField("_instance", Static)!.GetValue(null) as GameManager;
                var input = typeof(InputHandler).GetField("_instance", Static)?.GetValue(null) ?? manager?.inputHandler;
                if (input != null) foreach (var field in CanonicalCarryGraphCodec.Fields(input.GetType()).Where(field => typeof(Delegate).IsAssignableFrom(field.FieldType)))
                    Add(field.GetValue(input), "original-input-controller-event:" + field.Name);
                if (preserveSource) {
                    Add(original.GetComponent<Image>(), "original-source-Image");
                    nativeAnimator = Native(original.GetComponent<Animator>(), 1696); nativeCanvas = Native(original.GetComponent<CanvasRenderer>(), 304);
                }
                managedGuard = new CanonicalMapKeyTextExternalGuard(roots, preserveSource
                    ? new SortedDictionary<string, object>(StringComparer.Ordinal) : IncomingDirectory(original));
            }
            internal void BindReplacement(GameObject created)
            {
                if (preserveSource) throw new InvalidOperationException("Startup source preservation cannot rebind an owner.");
                managedGuard.PrepareExactIncomingRebind(IncomingDirectory(created));
            }
            internal void PrepareParentCanvasCallback(Canvas? nearest)
            {
                VerifyNativeCanvases("B-before-original-parent-callback");
                if (nearest == null) return;
                var byPointer = nativeCanvases.ToDictionary(value => ((IntPtr)Pointer.GetValue(value.Item1)!).ToInt64());
                var pointer = ((IntPtr)Pointer.GetValue(nearest)!).ToInt64();
                var first = true;
                while (pointer != 0) {
                    if (!byPointer.TryGetValue(pointer, out var owner) || !parentDirtyCanvases.Add(owner.Item1))
                        throw new InvalidOperationException("Original Canvas native parent chain is foreign or cyclic.");
                    // UpdateParentHierarchy marks nearest unconditionally, then
                    // its native parents through the first nonempty instruction
                    // owner (fixed procedure 2367a0, not Transform guesswork).
                    if (!first && BitConverter.ToUInt64(owner.Item2, 256) != 0) break;
                    first = false; pointer = BitConverter.ToInt64(owner.Item2, 448);
                }
            }
            internal void PrepareOriginalRendererRetirement(Image image)
            {
                if (preserveSource || retirementPrepared || retirementCompleted || !ReferenceEquals(image.gameObject, original))
                    throw new InvalidOperationException("Original Flash renderer retirement phase differs.");
                VerifyNativeCanvases("B-before-original-renderer-cleanup");
                var renderer = image.GetComponent<CanvasRenderer>(); var bytes = Native(renderer, 304);
                var rendererPointer = (IntPtr)Pointer.GetValue(renderer)!;
                var parent = BitConverter.ToInt64(bytes, 232);
                var seenHandleSlots = new HashSet<long>(); var seenInstructions = new HashSet<long>();
                foreach (var offset in new[] { 256, 280 }) {
                    var data = BitConverter.ToInt64(bytes, offset);
                    if (data == 0) {
                        if (BitConverter.ToInt32(bytes, offset + 8) != -1 || BitConverter.ToInt32(bytes, offset + 12) != -1
                            || BitConverter.ToInt32(bytes, offset + 16) != -1)
                            throw new InvalidOperationException("Original null Flash handle has non-invalid indices.");
                        continue;
                    }
                    var owners = nativeCanvases.Where(value => ((IntPtr)Pointer.GetValue(value.Item1)!).ToInt64() == parent
                        && checked(parent + 144) == data).ToArray();
                    if (owners.Length != 1) throw new InvalidOperationException("Original Flash handle does not belong to its exact parent Canvas.");
                    var owner = owners[0]; var before = owner.Item2;
                    var instructionIndex = BitConverter.ToInt32(bytes, offset + 8); var handleIndex = BitConverter.ToInt32(bytes, offset + 12);
                    var instructionCount = BitConverter.ToUInt64(before, 256); var handleCount = BitConverter.ToUInt64(before, 224);
                    if (BitConverter.ToInt64(before, 208) == 0 || BitConverter.ToInt64(before, 240) == 0)
                        throw new InvalidOperationException("Original Flash handle arrays are absent.");
                    // Validate indices before dereferencing either owned array.
                    RequireRetirementHandle(data, checked(parent + 144), 1, 1, instructionIndex, handleIndex, instructionCount, handleCount);
                    var handleSlot = new IntPtr(checked(BitConverter.ToInt64(before, 208) + handleIndex * 8L));
                    var instruction = new IntPtr(checked(BitConverter.ToInt64(before, 240) + instructionIndex * 720L));
                    var slotOwner = BitConverter.ToInt64(NativeSlot(handleSlot, 8), 0);
                    RequireRetirementHandle(data, checked(parent + 144), slotOwner, checked(rendererPointer.ToInt64() + offset),
                        instructionIndex, handleIndex, instructionCount, handleCount);
                    if (!seenHandleSlots.Add(handleSlot.ToInt64()) || !seenInstructions.Add(instruction.ToInt64()))
                        throw new InvalidOperationException("Original Flash push/pop handles unexpectedly share one owned slot.");
                    retiredInstructionSlots.Add(Tuple.Create(owner.Item1, handleSlot, instruction, NativeSlot(instruction, 720)));
                    retirementDirtyCanvases.Add(owner.Item1);
                    if (BitConverter.ToInt64(before, 424) != 0) retirementFenceCanvases.Add(owner.Item1);
                }
                retirementPrepared = true;
            }
            internal void CompleteOriginalRendererRetirement()
            {
                if (!retirementPrepared || retirementCompleted) throw new InvalidOperationException("Original Flash renderer retirement was repeated or not prepared.");
                retirementCompleted = true;
                VerifyNativeCanvases("B-after-original-renderer-cleanup");
                foreach (var slot in retiredInstructionSlots)
                    RequireRetiredInstruction(slot.Item4, NativeSlot(slot.Item3, 720), NativeSlot(slot.Item2, 8));
                Modding.Logger.Log("[HKTAS] Original Flash renderer exact native retirement readback: ownedPushPopSlots=" + retiredInstructionSlots.Count
                    + ";ownedInstructionSharedMeshCleared=true;ownedHandleSlotsNull=true;allOtherInstructionBytesEqual=true;nativeWritten=false.");
            }
            internal void VerifyNativeCanvases(string stage)
            {
                foreach (var owner in nativeCanvases) {
                    var after = Native(owner.Item1, 584); var marked = parentDirtyCanvases.Contains(owner.Item1);
                    var retired = retirementCompleted && retirementDirtyCanvases.Contains(owner.Item1);
                    var fence = retirementCompleted && retirementFenceCanvases.Contains(owner.Item1);
                    try { RequireOriginalCanvasAfterRetirement(owner.Item2, after, marked, retired, fence); }
                    catch {
                        Modding.Logger.Log("[HKTAS] Original Flash Canvas strict transition rejected: " + Newtonsoft.Json.JsonConvert.SerializeObject(new {
                            stage, parentDirtyExpected = marked, ownedRendererRetiredExpected = retired, originalFenceCompletedExpected = fence,
                            changedByteOffsets = Enumerable.Range(0, 584).Where(index => owner.Item2[index] != after[index]).ToArray(),
                            beforeDirty = BitConverter.ToUInt16(owner.Item2, 280), afterDirty = BitConverter.ToUInt16(after, 280),
                            beforeFencePresent = BitConverter.ToInt64(owner.Item2, 424) != 0, afterFencePresent = BitConverter.ToInt64(after, 424) != 0,
                            nativeAddressesEmitted = false }));
                        throw;
                    }
                    Modding.Logger.Log("[HKTAS] Menu Flash original Canvas callback: " + Newtonsoft.Json.JsonConvert.SerializeObject(new {
                        stage, originalCanvasInstance = owner.Item1.GetInstanceID(), hierarchyDirtyCallbackExpected = marked,
                        beforeDirty = BitConverter.ToUInt16(owner.Item2, 280), afterDirty = BitConverter.ToUInt16(after, 280),
                        ownedRendererRetiredExpected = retired, originalFenceCompletedExpected = fence,
                        owner.Item1.gameObject.activeSelf, owner.Item1.gameObject.activeInHierarchy,
                        handleCount = BitConverter.ToUInt64(after, 224), instructionCount = BitConverter.ToUInt64(after, 256),
                        vertices = BitConverter.ToUInt32(after, 272), indices = BitConverter.ToUInt32(after, 276),
                        allOther584BytesEqual = true, synchronousSameOwnerOnly = true, completeNativeCoverage = false }));
                }
            }
            private string Digest(object? root, string name, bool expand)
            {
                var result = fields.Capture(root, name, expandRoot: expand);
                if (result.Unsupported.Count != 0) throw new InvalidOperationException("Flash external preservation has unread fields: " + name + ":" + string.Join(";", result.Unsupported));
                return result.Sha256;
            }
            internal void Verify()
            {
                managedGuard.VerifyManagedRoots();
                VerifyNativeCanvases(preserveSource ? "startup-source-after-template" : "B-after-original-retirement");
                if (!ReferenceEquals(player, PlayerInstance.GetValue(null)) || !ReferenceEquals(scene, SceneInstance.GetValue(null))
                    || !random.Equals(UnityEngine.Random.state) || !particles.SequenceEqual(LoadingGameplayOracle.CaptureParticleRandomState())
                    || singletonRoots.Any(root => !ReferenceEquals(root.Item2, root.Item1.GetValue(null)))
                    || nativeOuter.Any(root => root.Item3 != Digest(root.Item1(), root.Item2, true))
                    || preserveSource && (!nativeAnimator!.SequenceEqual(Native(original.GetComponent<Animator>(), 1696))
                        || !nativeCanvas!.SequenceEqual(Native(original.GetComponent<CanvasRenderer>(), 304))))
                    throw new InvalidOperationException("Flash producer changed original gameplay/menu/queues/events/native source or double RNG.");
                Modding.Logger.Log("[HKTAS] Menu Flash original complete shared preservation passed: managedRoots=" + managedGuard.ManagedRootCount
                    + ";crossRootAliases=true;originalFsmDetachedPartition=true;allOriginalBudgets=true;sourceNativeReadback=" + preserveSource);
            }
            public void Dispose() { managedGuard.Dispose(); fields.Dispose(); }
        }
        public void Dispose()
        {
            if (closingUi != null) {
                if (menuCloseRoutine != null) closingUi.StopCoroutine(menuCloseRoutine);
                if (menuFadeRoutine != null) closingUi.StopCoroutine(menuFadeRoutine);
            }
            menuCloseRoutine = null; menuFadeRoutine = null; closingMenu = null; closingUi = null;
            awakeHook.Dispose(); FrozenRoot = null;
            if (holder != null) UnityEngine.Object.DestroyImmediate(holder);
            holder = null; template = null;
        }
    }
}
