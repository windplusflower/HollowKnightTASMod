using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HollowKnightTAS.Runtime.Input;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalMouseCarry
    {
        internal const string Root = "R6/InputMouse-adapter";
        internal static readonly Type[] DataTypes = { typeof(CheckpointMouseState), typeof(CheckpointMouseModule), typeof(CheckpointMouseModule[]), typeof(CheckpointMouseValue) };
        internal static CheckpointMouseState Copy(CheckpointMouseState state)
        {
            if (state == null || state.Width <= 0 || state.Height <= 0 || ((state.PreviousInControlButtons | state.PreviousHkButtons) & ~511u) != 0
                || float.IsNaN(state.PreviousHkX) || float.IsInfinity(state.PreviousHkX) || float.IsNaN(state.PreviousHkY) || float.IsInfinity(state.PreviousHkY)
                || state.InControl == null || state.Hk == null || state.Modules == null || state.Modules.Length > 16)
                throw new InvalidDataException("Canonical mouse state has invalid viewport, cache or module bounds.");
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var module in state.Modules)
                if (module == null || module.Identity == null || module.Identity.Length == 0 || module.Identity.Length > 16384 || module.Identity.IndexOf('\0') >= 0
                    || !identities.Add(module.Identity) || module.Fields == null || module.Fields.Count > 16
                    || module.Fields.Any(pair => pair.Key == null || pair.Value == null || pair.Key.Length > 16384 || pair.Value.Length > 16384
                        || pair.Key.IndexOf('\0') >= 0 || pair.Value.IndexOf('\0') >= 0))
                    throw new InvalidDataException("Canonical mouse module identity or scalar field layout differs.");
            return new CheckpointMouseState { Enabled = state.Enabled, Suspended = state.Suspended, Width = state.Width, Height = state.Height,
                PreviousInControlButtons = state.PreviousInControlButtons, PreviousHkButtons = state.PreviousHkButtons,
                PreviousHkX = state.PreviousHkX, PreviousHkY = state.PreviousHkY,
                InControl = CheckpointMouseValue.Capture(state.InControl.Create()), Hk = CheckpointMouseValue.Capture(state.Hk.Create()),
                Modules = state.Modules.Select(module => new CheckpointMouseModule { Identity = module.Identity,
                    Fields = new Dictionary<string, string>(module.Fields) }).ToArray() };
        }
        internal static CheckpointMouseState FromPrepared(CanonicalCarryGraphCodec.PreparedView view)
        {
            var root = view.Root(Root); var type = typeof(CheckpointMouseState);
            object? Field(string name) => view.Field(root, type, name);
            return Copy(new CheckpointMouseState { Enabled = (bool)Field(nameof(CheckpointMouseState.Enabled))!, Suspended = (bool)Field(nameof(CheckpointMouseState.Suspended))!,
                Width = (int)Field(nameof(CheckpointMouseState.Width))!, Height = (int)Field(nameof(CheckpointMouseState.Height))!,
                PreviousInControlButtons = (uint)Field(nameof(CheckpointMouseState.PreviousInControlButtons))!, PreviousHkButtons = (uint)Field(nameof(CheckpointMouseState.PreviousHkButtons))!,
                PreviousHkX = (float)Field(nameof(CheckpointMouseState.PreviousHkX))!, PreviousHkY = (float)Field(nameof(CheckpointMouseState.PreviousHkY))!,
                InControl = (CheckpointMouseValue)Field(nameof(CheckpointMouseState.InControl))!, Hk = (CheckpointMouseValue)Field(nameof(CheckpointMouseState.Hk))!,
                Modules = (CheckpointMouseModule[])Field(nameof(CheckpointMouseState.Modules))! });
        }
    }
}
