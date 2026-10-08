using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using InControl;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Process bindings are identities, not portable object bytes. These rules
    // do not change live state, Unity time, action values or repeat timers.
    internal sealed class LoadingObservationIdentity
    {
        private readonly string shadowRoot;
        private readonly Dictionary<InputDevice, string> devices = new Dictionary<InputDevice, string>(DeviceComparer.Instance);
        private enum FieldKind { None, ActiveListEpoch, ProtectedRoot, DeviceGuid, InputTick }
        private readonly Dictionary<FieldInfo, FieldKind> fieldKinds = new Dictionary<FieldInfo, FieldKind>();
        private ulong inputTick;
        private LoadingListEpochIdentity<InputDevice>? activeDeviceEpoch;
        internal LoadingObservationIdentity(string sessionDirectory)
        {
            var sessions = Path.GetDirectoryName(sessionDirectory)!;
            var runtimeRoot = Path.GetDirectoryName(sessions)!;
            if (Path.GetFileName(sessions) != "sessions" || Path.GetFileName(runtimeRoot) != "HollowKnightTAS")
                throw new InvalidOperationException("P0 oracle session is outside the protected runtime directory.");
            shadowRoot = Path.GetDirectoryName(runtimeRoot)!.TrimEnd('\\', '/');
        }
        internal void Refresh()
        {
            // InputManager and InputDevice have already initialized and run
            // the native input handshake before this observer can be created.
            inputTick = InputManager.CurrentTick;
            var active = typeof(InputManager).GetField("activeDevices", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetValue(null) as List<InputDevice>
                ?? throw new InvalidOperationException("P0 active input list is unavailable.");
            activeDeviceEpoch = new LoadingListEpochIdentity<InputDevice>(active);
            devices.Clear(); devices.Add(InputDevice.Null, "device:null");
            var field = typeof(InputManager).GetField("devices", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("P0 input device registry is unavailable.");
            var registered = field.GetValue(null) as List<InputDevice>
                ?? throw new InvalidOperationException("P0 input device registry has an unknown layout.");
            for (var index = 0; index < registered.Count; index++)
                devices[registered[index]] = "device:registered[" + index + "]:" + registered[index].GetType().FullName;
        }
        internal string? Field(object owner, FieldInfo field, object? value)
        {
            if (!fieldKinds.TryGetValue(field, out var kind))
            {
                kind = Classify(field);
                fieldKinds.Add(field, kind);
            }
            if (kind == FieldKind.ActiveListEpoch) return activeDeviceEpoch?.Field(owner, field, value);
            if (kind == FieldKind.ProtectedRoot && value is string path)
            {
                if (!Path.GetFullPath(path).TrimEnd('\\', '/').Equals(shadowRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("P0 platform save root does not match the protected session.");
                return "binding:protected-save-root";
            }
            if (kind == FieldKind.DeviceGuid && owner is InputDevice device)
            {
                if (!devices.TryGetValue(device, out var id))
                    throw new InvalidOperationException("P0 input device has no registered logical identity.");
                return "binding:" + id;
            }
            if (kind == FieldKind.InputTick && value is ulong tick)
                return tick == 0 ? "input-epoch:never" : "input-epoch:age="
                    + unchecked(inputTick - tick).ToString(CultureInfo.InvariantCulture);
            return null;
        }

        private static FieldKind Classify(FieldInfo field)
        {
            if ((field.DeclaringType == typeof(List<InputDevice>) || field.DeclaringType == typeof(List<InputDevice>.Enumerator))
                && (field.Name == "version" || field.Name == "_version")) return FieldKind.ActiveListEpoch;
            var type = field.DeclaringType!.FullName;
            if (type == "DesktopPlatform" && field.Name == "saveDirPath") return FieldKind.ProtectedRoot;
            if (type == "InControl.InputDevice" && field.Name == "<GUID>k__BackingField") return FieldKind.DeviceGuid;
            if (
                (type == "InControl.OneAxisInputControl" && (field.Name == "<UpdateTick>k__BackingField" || field.Name == "pendingTick"))
                || (type == "InControl.TwoAxisInputControl" && field.Name == "<UpdateTick>k__BackingField")
                || (type == "InControl.InputControl" && field.Name == "zeroTick")
                || (type == "InControl.PlayerActionSet" && field.Name == "<UpdateTick>k__BackingField")
                || (type == "InControl.PlayerAction" && field.Name == "LastInputTypeChangedTick")
                || (type == "InControl.InputDevice" && field.Name == "<LastInputTick>k__BackingField")) return FieldKind.InputTick;
            return FieldKind.None;
        }
        private sealed class DeviceComparer : IEqualityComparer<InputDevice>
        {
            internal static readonly DeviceComparer Instance = new DeviceComparer();
            public bool Equals(InputDevice? left, InputDevice? right) => ReferenceEquals(left, right);
            public int GetHashCode(InputDevice value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
