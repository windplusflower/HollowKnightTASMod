using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentNativeInputState
    {
        private const int LegacySlot = 0x19bd990, RawVtable = 0x166eb78, JoystickVtable = 0x166eb40;
        private const int HidVtable = 0x166eb68, TouchVtable = 0x1672928, TouchCount = 32, TouchStride = 112;
        private static readonly int[] TouchPadding = { 68, 69, 70, 71, 84, 85, 86, 87, 108, 109, 110, 111 };
        internal sealed class ButtonQueue
        {
            public int Capacity;
            public int[] Keys = Array.Empty<int>();
            public byte[] Pressed = Array.Empty<byte>();
            public bool PaddingZero;
        }
        internal sealed class HidIdentity
        {
            // Hash of the actual native UTF-16 raw device path. The physical
            // path (including any device serial) stays in its home process.
            public string Binding = "";
            public int NativeId;
            public uint ResourceReferences;
            public bool HasJoystick;
            public int JoystickMapKey;
        }
        internal sealed class LegacyStick
        {
            public int MapKey, DeviceId, DeviceType;
            public uint TypeData, ResourceReferences;
            public int[] KeyMap = Array.Empty<int>();
            public byte[] Axes = Array.Empty<byte>();
            public ButtonQueue Buttons = new ButtonQueue();
        }
        internal sealed class RawQueues
        {
            public byte[] MouseDelta = Array.Empty<byte>(), TouchOwner = Array.Empty<byte>(), Touches = Array.Empty<byte>();
            public bool Active, WindowPresent, WindowOnOwnerThread, PaddingZero;
            public string WindowClass = "";
            public int DeviceCapacity, NextDevice, TreeRoot = -1;
            public int[] KeyMap = Array.Empty<int>(), TreeShape = Array.Empty<int>();
            public HidIdentity[] Devices = Array.Empty<HidIdentity>();
            public LegacyStick[] Sticks = Array.Empty<LegacyStick>();
            public ButtonQueue Keyboard = new ButtonQueue(), Mouse = new ButtonQueue();
        }
        internal sealed class LegacyQueues
        {
            public bool Present;
            public int Vtable;
            public RawQueues? Raw;
        }
        internal sealed class LegacyLocal
        {
            internal readonly LegacyQueues Snapshot = new LegacyQueues();
            internal IntPtr Owner, Sentinel, Touches;
            internal readonly List<IntPtr> Devices = new List<IntPtr>(), Nodes = new List<IntPtr>(), Sticks = new List<IntPtr>();
            internal Live Live = null!;
        }
        private sealed class Vector
        {
            internal IntPtr Start, Allocation;
            internal int Count, Capacity, AllocatedBytes;
        }
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, StringBuilder text, int maximum);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        private static void ReadWindow(Live live, RawQueues raw, IntPtr window)
        {
            if (!raw.WindowPresent) return;
            live.Admitted();
            var thread = GetWindowThreadProcessId(window, out var process);
            var name = new StringBuilder(512);
            var length = GetClassNameW(window, name, name.Capacity);
            raw.WindowClass = length > 0 && length < name.Capacity - 1 ? name.ToString() : "";
            raw.WindowOnOwnerThread = thread != 0 && thread == GetCurrentThreadId() && process == GetCurrentProcessId();
        }
        private static Vector LegacyVector(byte[] raw, int offset, Live? live)
        {
            var start = M.Ptr(raw, offset);
            var end = M.Ptr(raw, offset + 8);
            var limit = M.Ptr(raw, offset + 16);
            var count = checked(end.ToInt64() - start.ToInt64());
            var capacity = checked(limit.ToInt64() - start.ToInt64());
            if (start.ToInt64() < 0 || count < 0 || capacity < count || capacity > MaxItems * 8 || (count & 7) != 0 || (capacity & 7) != 0
                || (capacity == 0) != (start == IntPtr.Zero)) throw Error("invalid MSVC input vector bounds");
            var allocation = start;
            var bytes = (int)capacity;
            if (bytes >= 4096) {
                var slot = IntPtr.Subtract(start, 8);
                allocation = M.Ptr(live == null ? M.Read(slot, 8) : live.Read(slot, 8), 0);
                var alignment = checked(start.ToInt64() - allocation.ToInt64());
                if (allocation.ToInt64() <= 0 || (start.ToInt64() & 31) != 0 || alignment < 8 || alignment > 39)
                    throw Error("invalid target MSVC vector alignment header");
                bytes += 39;
            }
            live?.Allocation(allocation, bytes, true);
            return new Vector { Start = start, Allocation = allocation, Count = (int)count / 8, Capacity = (int)capacity / 8, AllocatedBytes = bytes };
        }
        private static ButtonQueue ReadButtons(Live live, byte[] header, int offset)
        {
            var vector = LegacyVector(header, offset, live);
            var bytes = live.Read(vector.Start, vector.Capacity * 8);
            var result = new ButtonQueue { Capacity = vector.Capacity, Keys = new int[vector.Count], Pressed = new byte[vector.Count], PaddingZero = true };
            for (var i = 0; i < vector.Count; i++) {
                result.Keys[i] = M.I32(bytes, i * 8);
                result.Pressed[i] = bytes[i * 8 + 4];
                if (result.Pressed[i] > 1) throw Error("invalid native button state");
                result.PaddingZero &= (bytes[i * 8 + 5] | bytes[i * 8 + 6] | bytes[i * 8 + 7]) == 0;
            }
            for (var i = vector.Count * 8; i < bytes.Length; i++) result.PaddingZero &= bytes[i] == 0;
            return result;
        }
        private static int[] Ints(byte[] raw, int offset, int count)
            => Enumerable.Range(0, count).Select(i => M.I32(raw, offset + 4 * i)).ToArray();
        private static string HidPathIdentity(Live live, IntPtr owner, byte[] raw)
        {
            var length = M.U64(raw, 64);
            var pointer = M.Ptr(raw, 40);
            if (length > 16384 || pointer == IntPtr.Zero && length > 7
                || pointer != IntPtr.Zero && M.U64(raw, 48) != 0 && M.U64(raw, 48) < length) throw Error("invalid UTF-16 raw HID path");
            var bytes = live.Read(pointer == IntPtr.Zero ? IntPtr.Add(owner, 48) : pointer, (int)length * 2 + 2);
            if (bytes[bytes.Length - 1] != 0 || bytes[bytes.Length - 2] != 0) throw Error("unterminated HID path");
            if (length == 0) return "";
            using var sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(bytes, 0, bytes.Length - 2));
        }
        internal static LegacyLocal CaptureLegacy(Live live)
        {
            var local = new LegacyLocal { Live = live };
            var data = local.Snapshot;
            local.Owner = M.Ptr(live.Read(IntPtr.Add(live.Engine, LegacySlot), 8), 0);
            data.Present = local.Owner != IntPtr.Zero;
            if (!data.Present) return local;
            var vtable = M.Ptr(live.Read(local.Owner, 8), 0).ToInt64() - live.Engine.ToInt64();
            data.Vtable = vtable > 0 && vtable <= int.MaxValue ? (int)vtable : -1;
            if (data.Vtable != RawVtable) return local;
            var body = live.Read(local.Owner, 256);
            live.Allocation(local.Owner, 256, true);
            if (body[216] > 1 || M.Ptr(body, 96) != IntPtr.Add(live.Engine, TouchVtable)) throw Error("legacy backend or touch owner layout differs");
            var raw = new RawQueues { Active = body[216] != 0, WindowPresent = M.Ptr(body, 88) != IntPtr.Zero,
                MouseDelta = M.Slice(body, 8, 16), Mouse = ReadButtons(live, body, 24), Keyboard = ReadButtons(live, body, 48),
                KeyMap = Ints(body, 136, 20), NextDevice = M.I32(body, 248), PaddingZero = true };
            ReadWindow(live, raw, M.Ptr(body, 88));
            raw.PaddingZero &= M.Slice(body, 116, 4).Concat(M.Slice(body, 217, 7)).Concat(M.Slice(body, 252, 4)).All(x => x == 0);
            // TouchPhaseEmulation owns 32 slots allocated by its constructor
            // (0xe00 bytes). frameToReport/frameBegan/endPhaseInQueue are D.
            raw.TouchOwner = M.Slice(body, 112, 4).Concat(M.Slice(body, 120, 16)).ToArray();
            local.Touches = M.Ptr(body, 104);
            live.Allocation(local.Touches, TouchCount * TouchStride, true);
            raw.Touches = (byte[])live.Read(local.Touches, TouchCount * TouchStride).Clone();
            for (var i = 0; i < TouchCount; i++) foreach (var pad in TouchPadding) {
                var offset = i * TouchStride + pad;
                raw.PaddingZero &= raw.Touches[offset] == 0;
                raw.Touches[offset] = 0;
            }
            var nodeMap = new Dictionary<IntPtr, int>();
            var capturedSticks = new List<LegacyStick>();
            var capturedNodes = new List<byte[]>();
            local.Sentinel = M.Ptr(body, 72);
            var count = M.U64(body, 80);
            if (count > MaxItems) throw Error("oversized HID joystick map");
            live.Allocation(local.Sentinel, 48, true);
            var head = live.Read(local.Sentinel, 48);
            if (head[24] != 1 || head[25] != 1) throw Error("invalid joystick tree sentinel");
            raw.PaddingZero &= head.Skip(26).All(x => x == 0);
            var seen = new HashSet<IntPtr>();
            void Walk(IntPtr node, int depth) {
                if (node == local.Sentinel) return;
                if (depth > 64 || !seen.Add(node) || seen.Count > (int)count) throw Error("invalid joystick map cycle or extent");
                live.Allocation(node, 48, true);
                var entry = live.Read(node, 48);
                if (entry[24] > 1 || entry[25] != 0) throw Error("invalid joystick node flags");
                Walk(M.Ptr(entry, 0), depth + 1);
                var stick = M.Ptr(entry, 40);
                live.Allocation(stick, 248, true);
                var item = live.Read(stick, 248);
                if (M.Ptr(item, 0) != IntPtr.Add(live.Engine, JoystickVtable)) throw Error("actual joystick resource type differs");
                var key = M.I32(entry, 32);
                if (nodeMap.ContainsKey(stick)) throw Error("multiple joystick map keys alias one device");
                nodeMap.Add(stick, key);
                local.Sticks.Add(stick); local.Nodes.Add(node); capturedNodes.Add(entry);
                capturedSticks.Add(new LegacyStick { MapKey = key, ResourceReferences = M.U32(item, 8), DeviceId = M.I32(item, 16),
                    DeviceType = M.I32(item, 20), TypeData = M.U32(item, 24), KeyMap = Ints(item, 28, 20), Axes = M.Slice(item, 136, 112),
                    Buttons = ReadButtons(live, item, 112) });
                raw.PaddingZero &= M.Slice(entry, 26, 6).Concat(M.Slice(entry, 36, 4)).Concat(M.Slice(item, 12, 4)).Concat(M.Slice(item, 108, 4)).All(x => x == 0);
                Walk(M.Ptr(entry, 16), depth + 1);
            }
            Walk(M.Ptr(head, 8), 0);
            if (local.Nodes.Count != (int)count || M.Ptr(head, 0) != (count == 0 ? local.Sentinel : local.Nodes[0])
                || M.Ptr(head, 16) != (count == 0 ? local.Sentinel : local.Nodes[(int)count - 1])) throw Error("invalid joystick map membership");
            var indexes = local.Nodes.Select((node, index) => new { node, index }).ToDictionary(row => row.node, row => row.index);
            int Index(IntPtr ptr) => ptr == local.Sentinel ? -1 : indexes.TryGetValue(ptr, out var i) ? i : throw Error("foreign joystick map edge");
            raw.TreeRoot = Index(M.Ptr(head, 8));
            raw.TreeShape = capturedNodes.SelectMany(entry => new[] { Index(M.Ptr(entry, 0)), Index(M.Ptr(entry, 8)), Index(M.Ptr(entry, 16)), (int)entry[24] }).ToArray();
            raw.Sticks = capturedSticks.ToArray();
            var devices = LegacyVector(body, 224, live);
            raw.DeviceCapacity = devices.Capacity;
            var directory = live.Read(devices.Start, devices.Capacity * 8);
            raw.PaddingZero &= directory.Skip(devices.Count * 8).All(x => x == 0);
            raw.Devices = new HidIdentity[devices.Count];
            for (var i = 0; i < devices.Count; i++) {
                var device = M.Ptr(directory, i * 8);
                // The report, OVERLAPPED and HID driver's handles remain on the
                // actual device. This adapter borrows only the identity fields
                // and the existing shared joystick ResourcePtr.
                var identity = live.Read(device, 80);
                if (M.Ptr(identity, 0) != IntPtr.Add(live.Engine, HidVtable)) throw Error("actual raw HID resource type differs");
                var state = M.Ptr(live.Read(IntPtr.Add(device, 320), 8), 0);
                var item = new HidIdentity { Binding = HidPathIdentity(live, device, identity), NativeId = M.I32(identity, 16),
                    ResourceReferences = M.U32(identity, 8), HasJoystick = state != IntPtr.Zero };
                if (item.HasJoystick && !nodeMap.TryGetValue(state, out item.JoystickMapKey)) throw Error("HID state belongs to an unobserved joystick");
                raw.Devices[i] = item; local.Devices.Add(device);
            }
            data.Raw = raw;
            ValidateLegacy(data);
            return local;
        }

        internal static long ValidateLegacy(LegacyQueues data)
        {
            if (data == null || !data.Present && data.Vtable != 0 || data.Present && data.Vtable == 0
                || (data.Vtable == RawVtable) != (data.Raw != null)) throw Error("legacy backend identity differs");
            var raw = data.Raw;
            if (raw == null) return 0;
            if (raw.MouseDelta == null || raw.MouseDelta.Length != 16 || raw.KeyMap == null || raw.KeyMap.Length != 20
                || raw.TouchOwner == null || raw.TouchOwner.Length != 20 || raw.TouchOwner.Skip(16).Any(x => x > 1)
                || raw.Touches == null || raw.Touches.Length != TouchCount * TouchStride || raw.Devices == null || raw.Devices.Length > MaxItems
                || raw.Sticks == null || raw.Sticks.Length > MaxItems || raw.DeviceCapacity < raw.Devices.Length || raw.DeviceCapacity > MaxItems
                || raw.TreeShape == null || raw.TreeShape.Length != raw.Sticks.Length * 4 || raw.WindowClass == null || raw.WindowClass.Length > 511
                || raw.WindowClass.IndexOf('\0') >= 0 || !raw.WindowPresent && (raw.WindowClass.Length != 0 || raw.WindowOnOwnerThread))
                throw Error("legacy native queue schema differs");
            ResidentTagManagerState.ValidateTreeShape(raw.TreeShape, raw.TreeRoot);
            for (var i = 0; i < TouchCount; i++) if (TouchPadding.Any(p => raw.Touches[i * TouchStride + p] != 0)) throw Error("touch payload includes padding");
            long total = raw.DeviceCapacity * 8L + raw.Sticks.Length * (48L + 248) + raw.Touches.Length + 256;
            void Queue(ButtonQueue buttons) {
                if (buttons == null || buttons.Keys == null || buttons.Pressed == null || buttons.Pressed.Length != buttons.Keys.Length
                    || buttons.Capacity < buttons.Keys.Length || buttons.Capacity > MaxItems || buttons.Pressed.Any(x => x > 1)) throw Error("input button FIFO schema differs");
                total += (long)buttons.Capacity * 8;
                if (total > MaxBytes) throw Error("legacy input allocation budget exceeded");
            }
            Queue(raw.Mouse); Queue(raw.Keyboard);
            var keys = new HashSet<int>();
            foreach (var stick in raw.Sticks) {
                if (stick == null || stick.KeyMap == null || stick.KeyMap.Length != 20 || stick.Axes == null || stick.Axes.Length != 112
                    || !keys.Add(stick.MapKey)) throw Error("legacy joystick schema differs");
                Queue(stick.Buttons);
            }
            if (!raw.Sticks.Select(row => row.MapKey).SequenceEqual(keys.OrderBy(i => i))) throw Error("joystick tree keys are not in native order");
            foreach (var device in raw.Devices) {
                if (device == null || device.Binding == null || device.Binding.Length != 0 && (device.Binding.Length != 44
                    || !TryDigest(device.Binding)) || device.HasJoystick && !keys.Contains(device.JoystickMapKey)
                    || !device.HasJoystick && device.JoystickMapKey != 0) throw Error("invalid raw device binding");
            }
            return total;
        }
        private static bool TryDigest(string value)
        {
            try { var bytes = Convert.FromBase64String(value); return bytes.Length == 32 && Convert.ToBase64String(bytes) == value; }
            catch (FormatException) { return false; }
        }
        private static Dictionary<int, string> StickIdentities(RawQueues data)
        {
            var devices = data.Devices.Where(d => d.HasJoystick).ToLookup(d => d.JoystickMapKey, d => d.Binding);
            return data.Sticks.ToDictionary(stick => stick.MapKey, stick => {
                var hid = devices[stick.MapKey].OrderBy(s => s, StringComparer.Ordinal).ToArray();
                if (hid.Length != 0 && hid.All(value => value.Length != 0)) return "hid/" + string.Join(".", hid);
                // For an XInput-only joystick the constructor's typeData is
                // the OS user slot queried by RawInput::Process.
                return stick.DeviceType == 1 && stick.TypeData < 4 ? "xinput/" + stick.TypeData.ToString(CultureInfo.InvariantCulture) : "";
            });
        }
        private static IEnumerable<string> LegacyGaps(LegacyQueues data, int keyLimit, bool canonical)
        {
            var raw = data.Raw;
            if (raw == null) {
                if (data.Present) yield return "native-input/unimplemented-win-input-vtable/" + data.Vtable.ToString("x");
                yield break;
            }
            if (raw.Devices.Any(device => device.Binding.Length == 0 || device.ResourceReferences == 0)
                || raw.Devices.Select(device => device.Binding).Distinct(StringComparer.Ordinal).Count() != raw.Devices.Length)
                yield return "native-input/raw-hid-identity-or-ownership";
            if (raw.WindowPresent && (!raw.WindowOnOwnerThread || raw.WindowClass.Length == 0))
                yield return "native-input/legacy-window-binding";
            if (raw.NextDevice < 0 || raw.Devices.Any(device => device.NativeId < 0 || device.NativeId >= raw.NextDevice))
                yield return "native-input/raw-device-counter-domain";
            var identities = StickIdentities(raw).Values;
            if (identities.Any(key => key.Length == 0) || identities.Distinct(StringComparer.Ordinal).Count() != identities.Count
                || raw.Sticks.Any(stick => stick.DeviceId < 0 || stick.DeviceId >= int.MaxValue || stick.ResourceReferences == 0 || stick.DeviceType < 0 || stick.DeviceType > 1))
                yield return "native-input/joystick-binding-or-ownership";
            if (raw.Keyboard.Keys.Any(key => key < 0 || key >= keyLimit) || raw.Mouse.Keys.Any(key => key < 0 || (long)key + 323 >= keyLimit)
                || raw.KeyMap.Concat(raw.Sticks.SelectMany(stick => stick.KeyMap)).Any(key => key < -1 || key >= keyLimit))
                yield return "native-input/accepted-button-setter-index";
            if (canonical) {
                var shape = ResidentTagManagerState.CanonicalTreeShape(raw.Sticks.Length, out var root);
                if (!raw.PaddingZero || !raw.Mouse.PaddingZero || !raw.Keyboard.PaddingZero || raw.Sticks.Any(stick => !stick.Buttons.PaddingZero)
                    || root != raw.TreeRoot || !shape.SequenceEqual(raw.TreeShape)) yield return "native-input/legacy-queue-padding-and-shape";
            }
        }
        private static void EncodeLegacy(BinaryWriter writer, LegacyQueues data)
        {
            writer.Write(data.Present); writer.Write(data.Vtable);
            var raw = data.Raw;
            if (raw == null) return;
            void Queue(ButtonQueue buttons) {
                writer.Write(buttons.Capacity); writer.Write(buttons.Keys.Length);
                for (var i = 0; i < buttons.Keys.Length; i++) { writer.Write(buttons.Keys[i]); writer.Write(buttons.Pressed[i]); }
            }
            writer.Write(raw.Active); writer.Write(raw.WindowPresent); writer.Write(raw.WindowOnOwnerThread); writer.Write(raw.WindowClass);
            writer.Write(raw.NextDevice); writer.Write(raw.MouseDelta);
            writer.Write(raw.TouchOwner); writer.Write(raw.Touches); foreach (var key in raw.KeyMap) writer.Write(key);
            Queue(raw.Mouse); Queue(raw.Keyboard);
            writer.Write(raw.DeviceCapacity); writer.Write(raw.Devices.Length);
            foreach (var device in raw.Devices) {
                writer.Write(device.Binding); writer.Write(device.NativeId); writer.Write(device.ResourceReferences);
                writer.Write(device.HasJoystick); writer.Write(device.JoystickMapKey);
            }
            writer.Write(raw.Sticks.Length);
            foreach (var stick in raw.Sticks) {
                writer.Write(stick.MapKey); writer.Write(stick.DeviceId); writer.Write(stick.DeviceType); writer.Write(stick.TypeData); writer.Write(stick.ResourceReferences);
                foreach (var key in stick.KeyMap) writer.Write(key); writer.Write(stick.Axes); Queue(stick.Buttons);
            }
        }
        private static void EncodeLegacyDependencies(BinaryWriter writer, LegacyQueues state)
        {
            writer.Write(state.Present); writer.Write(state.Vtable);
            var raw = state.Raw;
            if (raw == null) return;
            writer.Write(raw.WindowPresent); writer.Write(raw.WindowOnOwnerThread); writer.Write(raw.WindowClass);
            // Actual physical device membership and the target-local resource
            // references are dependencies. The order of the registered-device
            // vector, its allocation, and each pending input are D.
            writer.Write(raw.Devices.Length);
            foreach (var device in raw.Devices.OrderBy(d => d.Binding, StringComparer.Ordinal)) {
                writer.Write(device.Binding); writer.Write(device.NativeId); writer.Write(device.ResourceReferences);
                writer.Write(device.HasJoystick); writer.Write(device.JoystickMapKey);
            }
            writer.Write(raw.Sticks.Length);
            var identities = StickIdentities(raw);
            foreach (var stick in raw.Sticks.OrderBy(s => identities[s.MapKey], StringComparer.Ordinal)) {
                writer.Write(identities[stick.MapKey]); writer.Write(stick.DeviceId); writer.Write(stick.MapKey);
                writer.Write(stick.DeviceType); writer.Write(stick.TypeData); writer.Write(stick.ResourceReferences);
            }
        }

        internal static void RequireLegacyBindings(LegacyQueues source, LegacyQueues target)
        {
            ValidateLegacy(source); ValidateLegacy(target);
            if (source.Present != target.Present || source.Vtable != target.Vtable) throw Error("native Windows input backend dependency differs");
            var from = source.Raw;
            var to = target.Raw;
            if (from == null || to == null) {
                if (source.Present) throw Error("unbound actual Windows input backend");
                return;
            }
            if (from.WindowPresent != to.WindowPresent || from.WindowOnOwnerThread != to.WindowOnOwnerThread || from.WindowClass != to.WindowClass
                || from.Devices.Length != to.Devices.Length || from.Sticks.Length != to.Sticks.Length)
                throw Error("native input window/device dependency differs");
            Dictionary<string, T> Unique<T>(IEnumerable<T> rows, Func<T, string> keyOf) {
                var result = new Dictionary<string, T>(StringComparer.Ordinal);
                foreach (var item in rows) {
                    var key = keyOf(item);
                    if (key.Length == 0 || result.ContainsKey(key)) throw Error("missing or ambiguous physical input binding");
                    result.Add(key, item);
                }
                return result;
            }
            var targetDevices = Unique(to.Devices, d => d.Binding);
            foreach (var device in Unique(from.Devices, d => d.Binding).Values) {
                if (!targetDevices.TryGetValue(device.Binding, out var bound) || device.NativeId != bound.NativeId
                    || device.HasJoystick != bound.HasJoystick || device.JoystickMapKey != bound.JoystickMapKey
                    || device.ResourceReferences != bound.ResourceReferences) throw Error("target HID registration or alias differs: " + device.Binding);
            }
            var sourceIds = StickIdentities(from); var targetIds = StickIdentities(to);
            var targetSticks = Unique(to.Sticks, stick => targetIds[stick.MapKey]);
            foreach (var sourceBinding in Unique(from.Sticks, stick => sourceIds[stick.MapKey])) {
                var stick = sourceBinding.Value;
                if (!targetSticks.TryGetValue(sourceBinding.Key, out var bound) || stick.DeviceId != bound.DeviceId
                    || stick.DeviceType != bound.DeviceType || stick.TypeData != bound.TypeData || stick.MapKey != bound.MapKey
                    || stick.ResourceReferences != bound.ResourceReferences) throw Error("target joystick registration or alias differs: " + sourceBinding.Key);
            }
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AllocateEight(IntPtr allocator, UIntPtr count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SizedDelete(IntPtr allocation, UIntPtr bytes);
        private sealed class LegacyPlan
        {
            private readonly LegacyLocal target;
            private readonly RawQueues? source;
            private readonly AllocateEight? allocate;
            private readonly SizedDelete? release;
            private readonly IntPtr[] orderedDevices = Array.Empty<IntPtr>();
            private bool attempted;
            internal LegacyPlan(LegacyLocal target, LegacyQueues source)
            {
                this.target = target;
                RequireLegacyBindings(source, target.Snapshot);
                this.source = source.Raw;
                if (this.source == null) return;
                var actual = target.Snapshot.Raw!;
                var byName = actual.Devices.Select((device, i) => new { device.Binding, Address = target.Devices[i] })
                    .ToDictionary(row => row.Binding, row => row.Address, StringComparer.Ordinal);
                orderedDevices = this.source.Devices.Select(device => byName[device.Binding]).ToArray();
                RequireFunction(target.Live.Engine, 0x279cf0, 111, "941395e2224f496df8d675f0ce7c80f44b211e6edb215b4eebbf55ae29f27b7f");
                RequireFunction(target.Live.Engine, 0x1551d80, 5, "fb603ff8e68402a868288adb3e710f2c316916f2af637e2f845d69ad485ae597");
                allocate = (AllocateEight)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(target.Live.Engine, 0x279cf0), typeof(AllocateEight));
                release = (SizedDelete)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(target.Live.Engine, 0x1551d80), typeof(SizedDelete));
            }
            private void WriteVector(IntPtr header, byte[] entries, int capacity, Action admitted)
            {
                admitted();
                if (entries.Length % 8 != 0 || entries.Length / 8 > capacity || capacity > MaxItems) throw Error("invalid prepared input vector");
                var old = LegacyVector(M.Read(header, 24), 0, null);
                var payload = new byte[capacity * 8];
                Buffer.BlockCopy(entries, 0, payload, 0, entries.Length);
                if (old.Capacity == capacity) {
                    M.Write(old.Start, payload);
                    M.Write(IntPtr.Add(header, 8), BitConverter.GetBytes(IntPtr.Add(old.Start, entries.Length).ToInt64()));
                    return;
                }
                var replacement = capacity == 0 ? IntPtr.Zero : allocate!(header, new UIntPtr((uint)capacity));
                if (capacity > 0 && replacement == IntPtr.Zero) throw Error("native input vector allocation failed");
                var next = new byte[24];
                Buffer.BlockCopy(BitConverter.GetBytes(replacement.ToInt64()), 0, next, 0, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(IntPtr.Add(replacement, entries.Length).ToInt64()), 0, next, 8, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(IntPtr.Add(replacement, capacity * 8).ToInt64()), 0, next, 16, 8);
                var owned = LegacyVector(next, 0, null);
                var publishing = false;
                try {
                    M.Write(replacement, payload);
                    publishing = true;
                    M.Write(header, next);
                    if (old.Allocation != IntPtr.Zero) release!(old.Allocation, new UIntPtr((uint)old.AllocatedBytes));
                } finally {
                    if (!publishing && owned.Allocation != IntPtr.Zero) release!(owned.Allocation, new UIntPtr((uint)owned.AllocatedBytes));
                }
            }
            private void WriteQueue(IntPtr header, ButtonQueue queue, Action admitted)
            {
                var values = new byte[queue.Keys.Length * 8];
                for (var i = 0; i < queue.Keys.Length; i++) {
                    Buffer.BlockCopy(BitConverter.GetBytes(queue.Keys[i]), 0, values, i * 8, 4);
                    values[i * 8 + 4] = queue.Pressed[i];
                }
                WriteVector(header, values, queue.Capacity, admitted);
            }
            internal void Apply(Action admitted)
            {
                if (attempted) throw Error("legacy accepted queue plan was reused");
                attempted = true;
                RequireInputProducerAdmission();
                admitted();
                if (source == null) return;
                var owner = target.Owner;
                if (M.Ptr(IntPtr.Add(target.Live.Engine, LegacySlot)) != owner || M.Ptr(owner) != IntPtr.Add(target.Live.Engine, RawVtable))
                    throw Error("legacy backend binding changed");
                M.Write(IntPtr.Add(owner, 8), source.MouseDelta);
                WriteQueue(IntPtr.Add(owner, 24), source.Mouse, admitted);
                WriteQueue(IntPtr.Add(owner, 48), source.Keyboard, admitted);
                M.Write(IntPtr.Add(owner, 112), M.Slice(source.TouchOwner, 0, 4));
                M.Write(IntPtr.Add(owner, 116), new byte[4]);
                M.Write(IntPtr.Add(owner, 120), M.Slice(source.TouchOwner, 4, 16));
                M.Write(target.Touches, source.Touches);
                M.Write(IntPtr.Add(owner, 136), source.KeyMap.SelectMany(BitConverter.GetBytes).ToArray());
                M.Write(IntPtr.Add(owner, 216), new[] { source.Active ? (byte)1 : (byte)0 });
                M.Write(IntPtr.Add(owner, 217), new byte[7]);
                M.Write(IntPtr.Add(owner, 248), BitConverter.GetBytes(source.NextDevice));
                M.Write(IntPtr.Add(owner, 252), new byte[4]);
                // Same physical devices and reference multiset. Only the
                // ResourcePtr vector container moves, retaining all refcounts.
                WriteVector(IntPtr.Add(owner, 224), orderedDevices.SelectMany(pointer => BitConverter.GetBytes(pointer.ToInt64())).ToArray(),
                    source.DeviceCapacity, admitted);
                var shape = ResidentTagManagerState.CanonicalTreeShape(source.Sticks.Length, out var root);
                IntPtr Node(int index) => index < 0 ? target.Sentinel : target.Nodes[index];
                for (var i = 0; i < source.Sticks.Length; i++) {
                    admitted();
                    var stick = source.Sticks[i];
                    var address = target.Sticks[i];
                    M.Write(IntPtr.Add(address, 12), new byte[4]);
                    M.Write(IntPtr.Add(address, 28), stick.KeyMap.SelectMany(BitConverter.GetBytes).ToArray());
                    M.Write(IntPtr.Add(address, 108), new byte[4]);
                    WriteQueue(IntPtr.Add(address, 112), stick.Buttons, admitted);
                    M.Write(IntPtr.Add(address, 136), stick.Axes);
                    var links = new byte[32];
                    for (var edge = 0; edge < 3; edge++) Buffer.BlockCopy(BitConverter.GetBytes(Node(shape[i * 4 + edge]).ToInt64()), 0, links, edge * 8, 8);
                    links[24] = (byte)shape[i * 4 + 3];
                    M.Write(target.Nodes[i], links);
                    M.Write(IntPtr.Add(target.Nodes[i], 36), new byte[4]);
                }
                var head = new byte[48];
                Buffer.BlockCopy(BitConverter.GetBytes(Node(source.Sticks.Length == 0 ? -1 : 0).ToInt64()), 0, head, 0, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(Node(root).ToInt64()), 0, head, 8, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(Node(source.Sticks.Length - 1).ToInt64()), 0, head, 16, 8);
                head[24] = head[25] = 1;
                M.Write(target.Sentinel, head);
                admitted();
            }
        }
    }
}
