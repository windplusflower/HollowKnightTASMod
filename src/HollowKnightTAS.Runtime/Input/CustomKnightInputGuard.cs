using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InControl;

namespace HollowKnightTAS.Runtime.Input
{
    // Custom Knight 3.5 registers this action set even with both hotkeys unbound.
    // Retain normal InControl updates (including release edges), but supply neutral
    // bindings for the lifetime of the TAS session. Do not persist any CK settings.
    internal sealed class CustomKnightInputGuard : IDisposable
    {
        private readonly Dictionary<PlayerActionSet, BindingSource[][]> originals
            = new Dictionary<PlayerActionSet, BindingSource[][]>();

        internal static bool IsCustomKnightSet(PlayerActionSet set)
        {
            var type = set.GetType();
            return type.FullName == "CustomKnight.KeyBinds"
                && type.Assembly.GetName().Name == "CustomKnight";
        }

        public bool TryPrepare(PlayerActionSet set)
        {
            if (!IsCustomKnightSet(set)) return false;
            if (originals.ContainsKey(set)) return true;

            var actions = set.Actions.ToArray();
            if (actions.Length == 0)
            {
                Modding.Logger.LogWarn("[HollowKnightTAS] CustomKnight.KeyBinds has no actions; skipping hotkey neutralization.");
                originals.Add(set, Array.Empty<BindingSource[]>());
                return true;
            }

            if (actions.Length != 2 || actions[0].Name != "OpenSkinList"
                || actions[1].Name != "ReloadSkins")
            {
                Modding.Logger.LogWarn("[HollowKnightTAS] CustomKnight hotkey layout differs from expected (count="
                    + actions.Length + ", names=" + string.Join(", ", actions.Select(a => a.Name))
                    + "); neutralizing existing actions.");
            }

            var saved = actions.Select(action => action.UnfilteredBindings.ToArray()).ToArray();
            originals.Add(set, saved);
            try
            {
                foreach (var action in actions)
                {
                    action.ClearBindings();
                    if (!action.AddBinding(new NeutralBinding()))
                    {
                        Modding.Logger.LogWarn("[HollowKnightTAS] CustomKnight neutral binding was rejected for action '" + action.Name + "'; restoring original bindings.");
                        Restore(set, saved);
                        return true;
                    }
                }
                Modding.Logger.Log("[HollowKnightTAS] CustomKnight hotkeys neutralized for TAS session.");
            }
            catch (Exception exception)
            {
                Modding.Logger.LogWarn("[HollowKnightTAS] Failed to neutralize CustomKnight hotkeys (" + exception.GetType().Name + ": " + exception.Message + "); restoring original bindings.");
                try { Restore(set, saved); }
                catch (Exception restoreError)
                {
                    Modding.Logger.LogWarn("[HollowKnightTAS] Failed to restore CustomKnight bindings after error: " + restoreError.Message);
                }
            }
            return true;
        }

        public void Dispose()
        {
            foreach (var item in originals)
            {
                try { Restore(item.Key, item.Value); }
                catch (Exception exception)
                {
                    Modding.Logger.LogWarn("[HollowKnightTAS] CustomKnight bindings were not fully restored: " + exception.Message);
                }
            }
            originals.Clear();
        }

        private static void Restore(PlayerActionSet set, BindingSource[][] saved)
        {
            var count = Math.Min(saved.Length, set.Actions.Count);
            for (var index = 0; index < count; index++)
            {
                var action = set.Actions[index];
                action.ClearBindings();
                foreach (var binding in saved[index])
                {
                    if (!action.AddBinding(binding))
                        Modding.Logger.LogWarn("[HollowKnightTAS] CustomKnight original binding was rejected for action '" + action.Name + "'.");
                }
            }
        }

        private sealed class NeutralBinding : BindingSource
        {
            public override string Name => "HKTAS CustomKnight neutral";
            public override string DeviceName => "HollowKnightTAS";
            public override InputDeviceClass DeviceClass => InputDeviceClass.Unknown;
            public override InputDeviceStyle DeviceStyle => InputDeviceStyle.Unknown;
            public override BindingSourceType BindingSourceType => BindingSourceType.UnknownDeviceBindingSource;
            public override float GetValue(InputDevice device) => 0f;
            public override bool GetState(InputDevice device) => false;
            public override bool Equals(BindingSource other) => ReferenceEquals(this, other);
            public override int GetHashCode() => base.GetHashCode();
            public override void Save(BinaryWriter writer) { }
            public override void Load(BinaryReader reader, ushort version) { }
        }
    }
}
