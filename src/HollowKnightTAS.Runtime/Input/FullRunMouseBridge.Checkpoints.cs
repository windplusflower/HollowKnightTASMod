using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Runtime.FullRun;
using InControl;
using UnityEngine;

namespace HollowKnightTAS.Runtime.Input
{
    [Serializable]
    internal sealed class CheckpointMouseValue
    {
        public int X, Y;
        public short DeltaX, DeltaY, Wheel;
        public uint Buttons;
        internal static CheckpointMouseValue Capture(MouseFrameState value) => new CheckpointMouseValue
        { X = value.XQ16, Y = value.YQ16, DeltaX = value.DeltaXQ15, DeltaY = value.DeltaYQ15, Wheel = value.WheelQ15, Buttons = value.Buttons };
        internal MouseFrameState Create()
        {
            if ((Buttons & ~511u) != 0) throw new InvalidDataException("Invalid checkpoint mouse buttons.");
            return new MouseFrameState(X, Y, DeltaX, DeltaY, Buttons, Wheel);
        }
    }
    [Serializable]
    internal sealed class CheckpointMouseModule
    {
        public string Identity = "";
        public Dictionary<string, string> Fields = null!;
    }
    [Serializable]
    internal sealed class CheckpointMouseState
    {
        public bool Enabled, Suspended;
        public int Width, Height;
        public uint PreviousInControlButtons, PreviousHkButtons;
        public float PreviousHkX, PreviousHkY;
        public CheckpointMouseValue InControl = null!, Hk = null!;
        public CheckpointMouseModule[] Modules = null!;
    }
    public sealed partial class FullRunMouseBridge
    {
        private bool checkpointMouseImported;
        private FieldInfo[] CheckpointMouseFields => canonicalMouseCaches
            ? new[] { HkLastPosition, HkThisPosition, standaloneLastPosition!, standaloneThisPosition! }
            : new[] { HkLastPosition, HkThisPosition };
        private static string ModuleIdentity(HollowKnightInputModule module)
        {
            var path = new List<string>();
            for (var item = module.transform; item != null; item = item.parent) path.Add(item.name);
            path.Reverse();
            return module.gameObject.scene.name + "/" + string.Join("/", path);
        }
        internal CheckpointMouseState CaptureCheckpoint()
        {
            if (!installed || disposed || input!.IsNativeFrameActive) throw new InvalidOperationException("Mouse capture requires a completed boundary.");
            var modules = hkModules.Where(m => m.Module != null).OrderBy(m => ModuleIdentity(m.Module), StringComparer.Ordinal).ToArray();
            if (modules.Select(m => ModuleIdentity(m.Module)).Distinct().Count() != modules.Length)
                throw new InvalidDataException("Mouse module identities are ambiguous.");
            return new CheckpointMouseState
            {
                Enabled = Enabled, Suspended = suspended, Width = viewportWidth, Height = viewportHeight,
                InControl = CheckpointMouseValue.Capture(inControlState), Hk = CheckpointMouseValue.Capture(hkState),
                PreviousInControlButtons = previousInControlButtons, PreviousHkButtons = previousHkButtons,
                PreviousHkX = previousHkPosition.x, PreviousHkY = previousHkPosition.y,
                Modules = modules.Select(m => new CheckpointMouseModule { Identity = ModuleIdentity(m.Module),
                    Fields = CheckpointScalarFields.Capture(m.Module, CheckpointMouseFields) }).ToArray()
            };
        }
        internal void ImportCheckpoint(CheckpointMouseState state)
        {
            if (!installed || disposed || !replaying || checkpointMouseImported || input!.IsNativeFrameActive
                || state.Enabled != Enabled || state.Width != viewportWidth || state.Height != viewportHeight)
                throw new InvalidOperationException("Mouse continuation target differs or is outside a fresh paused replay.");
            if (((state.PreviousInControlButtons | state.PreviousHkButtons) & ~511u) != 0
                || state.Modules.Select(m => m.Identity).Distinct().Count() != state.Modules.Length)
                throw new InvalidDataException("Invalid mouse continuation layout.");
            var nextInControl = state.InControl.Create(); var nextHk = state.Hk.Create();
            var modules = Resources.FindObjectsOfTypeAll<HollowKnightInputModule>().Where(m => m != null
                && m.gameObject.scene.IsValid() && m.gameObject.scene.isLoaded).ToArray();
            var apply = new List<Action>();
            foreach (var saved in state.Modules)
            {
                var candidates = modules.Where(m => ModuleIdentity(m) == saved.Identity).ToArray();
                if (candidates.Length != 1) throw new InvalidDataException("Mouse continuation module is absent or ambiguous.");
                apply.Add(CheckpointScalarFields.PrepareApply(candidates[0], CheckpointMouseFields, saved.Fields));
            }
            SetFrameInputEnabled(!state.Suspended);
            inControlState = nextInControl; hkState = nextHk;
            previousInControlButtons = state.PreviousInControlButtons; previousHkButtons = state.PreviousHkButtons;
            previousHkPosition = new Vector2(state.PreviousHkX, state.PreviousHkY);
            foreach (var application in apply) application();
            checkpointMouseImported = true;
        }
    }
}
