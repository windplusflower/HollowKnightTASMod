using System;
using System.Collections.Generic;
using System.Reflection;
using HollowKnightTAS.Core.Movie;
using InControl;
using UnityEngine;
using UnityEngine.EventSystems;

namespace HollowKnightTAS.Runtime.Input
{
    /// <summary>
    /// Records the two game mouse consumers at their own Update boundaries. The
    /// UI process is separate and never receives this provider or BaseInput.
    /// </summary>
    public sealed class FullRunMouseBridge : IDisposable
    {
        private static readonly PropertyInfo MouseProviderProperty = typeof(InputManager).GetProperty(
            "MouseProvider", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException("InputManager.MouseProvider");
        private static readonly FieldInfo HkLastPosition = typeof(HollowKnightInputModule).GetField(
            "lastMousePosition", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException("HollowKnightInputModule.lastMousePosition");
        private static readonly FieldInfo HkThisPosition = typeof(HollowKnightInputModule).GetField(
            "thisMousePosition", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException("HollowKnightInputModule.thisMousePosition");
        private readonly List<(HollowKnightInputModule Module, bool Prior, BaseInput? Override,
            FullRunBaseInput? Installed)> hkModules =
            new List<(HollowKnightInputModule, bool, BaseInput?, FullRunBaseInput?)>();
        private readonly List<(InControlInputModule Module, bool Prior)> inControlModules =
            new List<(InControlInputModule, bool)>();
        private FullRunActionSetAdapter? input;
        private IMouseProvider? originalProvider;
        private FilteredProvider? filteredProvider;
        private MouseFrameState inControlState = Zero;
        private MouseFrameState hkState = Zero;
        private uint previousInControlButtons;
        private uint previousHkButtons;
        private int viewportWidth;
        private int viewportHeight;
        private bool replaying;
        private bool installed;
        private bool suspended = true;
        private bool disposed;
        private bool canonicalMouseCaches;
        private FieldInfo? standaloneLastPosition;
        private FieldInfo? standaloneThisPosition;

        private static MouseFrameState Zero => new MouseFrameState(0, 0, 0, 0, 0, 0);

        public bool Enabled { get; private set; }
        internal bool Replaying => replaying;
        internal bool UsesCapturedInput => replaying || suspended || canonicalMouseCaches;
        internal MouseFrameState HkState => hkState;
        internal uint PreviousHkButtons => previousHkButtons;

        public void Configure(FullRunActionSetAdapter adapter, bool isReplay)
        {
            if (installed || disposed || input != null)
                throw new InvalidOperationException("Game mouse bridge is already configured.");
            input = adapter ?? throw new ArgumentNullException(nameof(adapter));
            replaying = isReplay;
            canonicalMouseCaches = NativeExecutionProfile.UsesModWallClock(MovieProtocolV2.SelectedNativeProfileId);
        }

        public void UseReplayInputs()
        {
            if (disposed || input == null) throw new InvalidOperationException("Mouse bridge is not configured.");
            replaying = true;
        }

        public bool TryInstall(bool enabled, out string error)
        {
            error = string.Empty;
            if (disposed || installed || input == null)
            {
                error = "Game mouse bridge is already active or not configured.";
                return false;
            }
            try
            {
                viewportWidth = Screen.width;
                viewportHeight = Screen.height;
                if (viewportWidth <= 0 || viewportHeight <= 0)
                    throw new InvalidOperationException("Game viewport is unavailable.");
                originalProvider = InputManager.MouseProvider
                    ?? throw new InvalidOperationException("InControl mouse provider is unavailable.");
                filteredProvider = new FilteredProvider(this, originalProvider);
                MouseProviderProperty.SetValue(null, filteredProvider, null);
                On.InControl.HollowKnightInputModule.UpdateModule += OnHkUpdateModule;
                On.InControl.HollowKnightInputModule.ShouldActivateModule += OnHkShouldActivate;
                On.InControl.HollowKnightInputModule.ActivateModule += OnHkActivateModule;
                On.InControl.HollowKnightInputModule.Process += OnHkProcess;
                On.InControl.InControlInputModule.ShouldActivateModule += OnInControlShouldActivate;
                On.InControl.InControlInputModule.Process += OnInControlProcess;
                Enabled = enabled;
                if (canonicalMouseCaches)
                {
                    standaloneLastPosition = typeof(StandaloneInputModule).GetField("m_LastMousePosition", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new MissingMemberException("StandaloneInputModule.m_LastMousePosition");
                    standaloneThisPosition = typeof(StandaloneInputModule).GetField("m_MousePosition", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new MissingMemberException("StandaloneInputModule.m_MousePosition");
                    if (standaloneLastPosition.FieldType != typeof(Vector2) || standaloneThisPosition.FieldType != typeof(Vector2)
                        || HkLastPosition.FieldType != typeof(Vector3) || HkThisPosition.FieldType != typeof(Vector3))
                        throw new InvalidOperationException("V5 mouse cache layout is unavailable.");
                    foreach (var module in Resources.FindObjectsOfTypeAll<HollowKnightInputModule>())
                        if (module != null && module.gameObject.scene.IsValid() && module.gameObject.scene.isLoaded)
                        {
                            EnsureHkModule(module);
                            SetCanonicalPositions(module, Vector2.zero, Vector2.zero);
                        }
                }
                installed = true;
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                Dispose();
                return false;
            }
        }

        public void SetFrameInputEnabled(bool enabled)
        {
            if (!installed || disposed)
                throw new InvalidOperationException("Game mouse bridge is unavailable.");
            suspended = !enabled;
            if (suspended)
            {
                inControlState = Zero;
                hkState = Zero;
                previousInControlButtons = 0;
                previousHkButtons = 0;
            }
            foreach (var item in hkModules)
                if (item.Module != null)
                {
                    item.Module.allowMouseInput = Enabled && enabled && item.Prior;
                    if (canonicalMouseCaches && (!Enabled || suspended))
                        SetCanonicalPositions(item.Module, Vector2.zero, Vector2.zero);
                }
            foreach (var item in inControlModules)
                if (item.Module != null) item.Module.allowMouseInput = Enabled && enabled && item.Prior;
        }

        public MouseFrameState Capture(long frameIndex)
        {
            if (!installed || frameIndex < 0)
                throw new InvalidOperationException("Game mouse bridge is unavailable.");
            return inControlState;
        }

        public void Prepare(long frameIndex, MouseFrameState state)
        {
            if (!installed || frameIndex < 0 || state == null)
                throw new InvalidOperationException("Game mouse bridge is unavailable.");
            if (!Enabled && (state.Buttons != 0 || state.DeltaXQ15 != 0
                || state.DeltaYQ15 != 0 || state.WheelQ15 != 0))
                throw new InvalidOperationException("Disabled game mouse cannot receive input.");
            if (Enabled) inControlState = state;
        }

        private void OnProviderUpdate(IMouseProvider original)
        {
            if (!Enabled || suspended || input?.IsNativeFrameActive != true) return;
            try
            {
                var desired = replaying
                    ? input!.PeekMouse(GameInputChannel.MouseInControl)?.Mouse
                        ?? throw new InvalidOperationException("InControl mouse sample is missing.")
                    : null;
                if (!replaying) original.Update();
                previousInControlButtons = inControlState.Buttons;
                inControlState = desired ?? CaptureProvider(original);
                input!.CompleteMouse(GameInputChannel.MouseInControl, inControlState,
                    InputManager.CurrentTick);
            }
            catch (Exception exception)
            {
                input!.ReportExternalFault("InControl mouse: " + exception.Message);
            }
        }

        private void OnHkUpdateModule(
            On.InControl.HollowKnightInputModule.orig_UpdateModule original,
            HollowKnightInputModule module)
        {
            if (input?.IsNativeFrameActive != true) return;
            var originalCalled = false;
            try
            {
                EnsureHkModule(module);
                if (!Enabled || suspended) Disable(module);
                if (Enabled && !suspended)
                {
                    var desired = replaying
                        ? input!.PeekMouse(GameInputChannel.MouseHollowKnight)?.Mouse
                            ?? throw new InvalidOperationException("HollowKnight mouse sample is missing.")
                        : null;
                    previousHkPosition = DecodePosition(hkState);
                    previousHkButtons = hkState.Buttons;
                    hkState = desired ?? CaptureUnity();
                    input!.CompleteMouse(GameInputChannel.MouseHollowKnight, hkState,
                        InputManager.CurrentTick);
                }
                originalCalled = true;
                original(module);
                if (canonicalMouseCaches)
                    SetCanonicalPositions(module,
                        Enabled && !suspended ? previousHkPosition : Vector2.zero,
                        Enabled && !suspended ? DecodePosition(hkState) : Vector2.zero);
                else if (Enabled && replaying && !suspended)
                {
                    HkLastPosition.SetValue(module, ToVector3(previousHkPosition));
                    HkThisPosition.SetValue(module, ToVector3(DecodePosition(hkState)));
                }
            }
            catch (Exception exception)
            {
                input?.ReportExternalFault("HollowKnight mouse update: " + exception.Message);
                if (!originalCalled) original(module);
            }
        }

        private bool OnHkShouldActivate(
            On.InControl.HollowKnightInputModule.orig_ShouldActivateModule original,
            HollowKnightInputModule module)
        {
            if (!Enabled || suspended)
            {
                Disable(module);
                return original(module);
            }
            EnsureHkModule(module);
            if (!replaying) return original(module);
            // The original getter reads Input.GetMouseButtonDown directly.
            // Evaluate its keyboard path with mouse disabled, then add the
            // recorded mouse activation from the same native frame.
            var prior = module.allowMouseInput;
            module.allowMouseInput = false;
            try
            {
                var keyboard = original(module);
                return keyboard || hkState.Buttons != previousHkButtons
                    || hkState.DeltaXQ15 != 0 || hkState.DeltaYQ15 != 0;
            }
            finally { module.allowMouseInput = prior; }
        }

        private void OnHkActivateModule(
            On.InControl.HollowKnightInputModule.orig_ActivateModule original,
            HollowKnightInputModule module)
        {
            EnsureHkModule(module);
            original(module);
            if (canonicalMouseCaches)
            {
                var position = Enabled && !suspended ? DecodePosition(hkState) : Vector2.zero;
                SetCanonicalPositions(module, position, position);
            }
            else if (Enabled && replaying && !suspended)
            {
                var position = DecodePosition(hkState);
                HkLastPosition.SetValue(module, ToVector3(position));
                HkThisPosition.SetValue(module, ToVector3(position));
            }
        }

        private void OnHkProcess(On.InControl.HollowKnightInputModule.orig_Process original,
            HollowKnightInputModule module)
        {
            EnsureHkModule(module);
            if (!Enabled || suspended) Disable(module);
            original(module);
        }

        private bool OnInControlShouldActivate(
            On.InControl.InControlInputModule.orig_ShouldActivateModule original,
            InControlInputModule module)
        {
            if (!Enabled || suspended) Disable(module);
            return original(module);
        }

        private void OnInControlProcess(On.InControl.InControlInputModule.orig_Process original,
            InControlInputModule module)
        {
            if (!Enabled || suspended) Disable(module);
            original(module);
        }

        private void EnsureHkModule(HollowKnightInputModule module)
        {
            if (hkModules.Exists(item => ReferenceEquals(item.Module, module))) return;
            var prior = module.inputOverride;
            FullRunBaseInput? overrideInput = null;
            if (Enabled)
            {
                overrideInput = module.gameObject.AddComponent<FullRunBaseInput>();
                overrideInput.Configure(this);
                module.inputOverride = overrideInput;
            }
            hkModules.Add((module, module.allowMouseInput, prior, overrideInput));
            if (!Enabled) module.allowMouseInput = false;
        }

        private void SetCanonicalPositions(HollowKnightInputModule module, Vector2 previous, Vector2 current)
        {
            // Run the original hook chain, then bind both inherited and HK
            // caches to the Movie mouse channel. Disabled input stays zero.
            HkLastPosition.SetValue(module, ToVector3(previous));
            HkThisPosition.SetValue(module, ToVector3(current));
            standaloneLastPosition!.SetValue(module, previous);
            standaloneThisPosition!.SetValue(module, current);
        }

        private void Disable(HollowKnightInputModule module)
        {
            EnsureHkModule(module);
            module.allowMouseInput = false;
        }

        private void Disable(InControlInputModule module)
        {
            if (!inControlModules.Exists(item => ReferenceEquals(item.Module, module)))
                inControlModules.Add((module, module.allowMouseInput));
            module.allowMouseInput = false;
        }

        private MouseFrameState CaptureProvider(IMouseProvider provider)
        {
            var position = provider.GetPosition();
            uint buttons = 0;
            for (var index = 0; index < 9; index++)
            {
                var control = (Mouse)(index < 3 ? index + 1 : index + 7);
                if (provider.GetButtonIsPressed(control)) buttons |= 1u << index;
            }
            return Encode(position, provider.GetDeltaX(), provider.GetDeltaY(),
                buttons, provider.GetDeltaScroll());
        }

        private MouseFrameState CaptureUnity()
        {
            uint buttons = 0;
            for (var index = 0; index < 9; index++)
            {
                try { if (UnityEngine.Input.GetMouseButton(index)) buttons |= 1u << index; }
                catch (ArgumentException) { }
            }
            return Encode(UnityEngine.Input.mousePosition,
                UnityEngine.Input.GetAxisRaw("mouse x"),
                UnityEngine.Input.GetAxisRaw("mouse y"), buttons,
                UnityEngine.Input.mouseScrollDelta.y);
        }

        private MouseFrameState Encode(Vector2 position, float dx, float dy,
            uint buttons, float wheel)
            => new MouseFrameState(
                QuantizePosition(position.x, viewportWidth),
                QuantizePosition(position.y, viewportHeight),
                QuantizeAxis(dx), QuantizeAxis(dy), buttons, QuantizeAxis(wheel));

        private static int QuantizePosition(float value, int extent)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new InvalidOperationException("Mouse position is not finite.");
            return Mathf.Clamp(Mathf.RoundToInt(value * ushort.MaxValue / extent),
                0, ushort.MaxValue);
        }

        private static short QuantizeAxis(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new InvalidOperationException("Mouse delta is not finite.");
            return checked((short)Math.Round(Mathf.Clamp(value, -1f, 1f) * short.MaxValue));
        }

        private Vector2 DecodePosition(MouseFrameState state)
            => new Vector2(state.XQ16 * viewportWidth / (float)ushort.MaxValue,
                state.YQ16 * viewportHeight / (float)ushort.MaxValue);

        private static Vector3 ToVector3(Vector2 position)
            => new Vector3(position.x, position.y, 0f);

        private Vector2 previousHkPosition;

        internal Vector2 HkPosition => DecodePosition(hkState);
        internal Vector2 HkScroll => new Vector2(0f, hkState.WheelQ15 / (float)short.MaxValue);
        internal bool HkButton(int button)
            => button >= 0 && button < 9 && (hkState.Buttons & (1u << button)) != 0;
        internal bool HkButtonDown(int button)
            => button >= 0 && button < 9 && (hkState.Buttons & (1u << button)) != 0
                && (previousHkButtons & (1u << button)) == 0;
        internal bool HkButtonUp(int button)
            => button >= 0 && button < 9 && (hkState.Buttons & (1u << button)) == 0
                && (previousHkButtons & (1u << button)) != 0;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            On.InControl.HollowKnightInputModule.UpdateModule -= OnHkUpdateModule;
            On.InControl.HollowKnightInputModule.ShouldActivateModule -= OnHkShouldActivate;
            On.InControl.HollowKnightInputModule.ActivateModule -= OnHkActivateModule;
            On.InControl.HollowKnightInputModule.Process -= OnHkProcess;
            On.InControl.InControlInputModule.ShouldActivateModule -= OnInControlShouldActivate;
            On.InControl.InControlInputModule.Process -= OnInControlProcess;
            foreach (var item in hkModules)
            {
                if (item.Module == null) continue;
                item.Module.allowMouseInput = item.Prior;
                item.Module.inputOverride = item.Override;
                if (item.Installed != null) UnityEngine.Object.Destroy(item.Installed);
            }
            foreach (var item in inControlModules)
                if (item.Module != null) item.Module.allowMouseInput = item.Prior;
            if (filteredProvider != null
                && ReferenceEquals(InputManager.MouseProvider, filteredProvider))
                MouseProviderProperty.SetValue(null, originalProvider, null);
            hkModules.Clear();
            inControlModules.Clear();
        }

        private sealed class FilteredProvider : IMouseProvider
        {
            private readonly FullRunMouseBridge owner;
            private readonly IMouseProvider original;
            public FilteredProvider(FullRunMouseBridge owner, IMouseProvider original)
            {
                this.owner = owner;
                this.original = original;
            }
            public void Setup() { if (owner.Enabled && !owner.replaying) original.Setup(); }
            public void Reset() { if (owner.Enabled && !owner.replaying) original.Reset(); }
            public void Update() => owner.OnProviderUpdate(original);
            public Vector2 GetPosition() => !owner.Enabled || owner.suspended ? Vector2.zero
                : owner.replaying ? owner.DecodePosition(owner.inControlState) : original.GetPosition();
            public float GetDeltaX() => !owner.Enabled || owner.suspended ? 0f
                : owner.replaying ? owner.inControlState.DeltaXQ15 / (float)short.MaxValue
                    : original.GetDeltaX();
            public float GetDeltaY() => !owner.Enabled || owner.suspended ? 0f
                : owner.replaying ? owner.inControlState.DeltaYQ15 / (float)short.MaxValue
                    : original.GetDeltaY();
            public float GetDeltaScroll() => !owner.Enabled || owner.suspended ? 0f
                : owner.replaying ? owner.inControlState.WheelQ15 / (float)short.MaxValue
                    : original.GetDeltaScroll();
            public bool GetButtonIsPressed(Mouse control) => !owner.Enabled || owner.suspended ? false
                : owner.replaying ? Button(owner.inControlState.Buttons, control)
                    : original.GetButtonIsPressed(control);
            public bool GetButtonWasPressed(Mouse control) => !owner.Enabled || owner.suspended ? false
                : owner.replaying ? Button(owner.inControlState.Buttons
                    & ~owner.previousInControlButtons, control)
                    : original.GetButtonWasPressed(control);
            public bool GetButtonWasReleased(Mouse control) => !owner.Enabled || owner.suspended ? false
                : owner.replaying ? Button(owner.previousInControlButtons
                    & ~owner.inControlState.Buttons, control)
                    : original.GetButtonWasReleased(control);
            public bool HasMousePresent() => owner.Enabled && !owner.suspended
                && (owner.replaying || original.HasMousePresent());
            private static bool Button(uint buttons, Mouse control)
            {
                var index = (int)control;
                var bit = index >= 1 && index <= 3 ? index - 1
                    : index >= 10 && index <= 15 ? index - 7 : -1;
                return bit >= 0 && (buttons & (1u << bit)) != 0;
            }
        }
    }

    public sealed class FullRunBaseInput : BaseInput
    {
        private FullRunMouseBridge? owner;
        internal void Configure(FullRunMouseBridge bridge) => owner = bridge;
        public override bool mousePresent => owner?.UsesCapturedInput == true ? true : base.mousePresent;
        public override Vector2 mousePosition => owner?.UsesCapturedInput == true
            ? owner.HkPosition : base.mousePosition;
        public override Vector2 mouseScrollDelta => owner?.UsesCapturedInput == true
            ? owner.HkScroll : base.mouseScrollDelta;
        public override bool GetMouseButton(int button) => owner?.UsesCapturedInput == true
            ? owner.HkButton(button) : base.GetMouseButton(button);
        public override bool GetMouseButtonDown(int button) => owner?.UsesCapturedInput == true
            ? owner.HkButtonDown(button) : base.GetMouseButtonDown(button);
        public override bool GetMouseButtonUp(int button) => owner?.UsesCapturedInput == true
            ? owner.HkButtonUp(button) : base.GetMouseButtonUp(button);
    }
}
