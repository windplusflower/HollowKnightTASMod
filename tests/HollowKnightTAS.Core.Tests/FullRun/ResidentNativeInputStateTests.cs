using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class ResidentNativeInputStateTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    [TestMethod]
    public void LastBlockHighBitsAndEachHeldDownUpBankAreObservable()
    {
        var api = new Api();
        var source = api.Data();
        var seen = new System.Collections.Generic.HashSet<string>();
        for (var bank = 0; bank < 3; bank++) {
            var changed = api.Copy(source);
            var key = Get<Array>(changed, "Keys").GetValue(bank)!;
            Get<byte[]>(key, "Blocks")[83] = 0x80; // bit 671 exceeds logical count 670; GetAnyKey tests the complete dword.
            Assert.IsTrue(seen.Add(Convert.ToBase64String(api.Encode(changed))));
            api.Reject(() => api.Call("RequireEqual", source, changed));
            CollectionAssert.AreEqual(api.S0(source), api.S0(changed));
        }
    }

    [TestMethod]
    public void AxesAndJoysticksKeepOrderIdentityAndFloatBits()
    {
        var api = new Api();
        var source = api.Data();
        foreach (var field in new[] { 0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52, 53 }) {
            var changed = api.Copy(source);
            Get<byte[]>(Get<Array>(changed, "Axes").GetValue(0)!, "Fields")[field] = 1;
            api.Reject(() => api.Call("RequireEqual", source, changed));
        }
        var changedStick = api.Copy(source);
        // Same two axes as numbers, different signed-zero bits in row #1.
        Get<byte[]>(Get<Array>(changedStick, "Joysticks").GetValue(1)!, "Positions")[3] = 0x80;
        api.Reject(() => api.Call("RequireEqual", source, changedStick));
        var reordered = api.Copy(source);
        var axes = Get<Array>(reordered, "Axes");
        var first = axes.GetValue(0); axes.SetValue(axes.GetValue(1), 0); axes.SetValue(first, 1);
        api.Reject(() => api.Call("RequireEqual", source, reordered));
    }

    [TestMethod]
    public void MouseImeCompositionPendingUnloadQuitAndExitCodeAreD()
    {
        var api = new Api();
        var source = api.Data();
        foreach (var offset in new[] { 0, 4, 8, 12, 16, 20, 24, 28, 32, 33, 36, 40, 44, 48, 52, 53, 56, 60 }) {
            var changed = api.Copy(source);
            Get<byte[]>(changed, "Scalars")[offset] = 1;
            api.Reject(() => api.Call("RequireEqual", source, changed));
            CollectionAssert.AreEqual(api.S0(source), api.S0(changed));
        }
        foreach (var field in new[] { "Input", "Composition" }) {
            var changed = api.Copy(source);
            Set(changed, field, api.Text("候補\U00010000"));
            api.Reject(() => api.Call("RequireEqual", source, changed));
        }
    }

    [TestMethod]
    public void NoncanonicalStoragePreservesValuesButCannotBeComparedAsS0()
    {
        var api = new Api();
        var source = api.Data();
        var changed = api.Copy(source);
        Set(Get<object>(changed, "AxisStorage"), "Capacity", 6);
        Get<byte[]>(changed, "Padding")[5] = 1;
        var first = Get<Array>(changed, "Axes").GetValue(0)!;
        Get<byte[]>(first, "Padding")[0] = 255;
        Get<byte[]>(Get<Array>(first, "Names").GetValue(0)!, "Padding")[0] = 0x23;
        api.Call("RequireBodyCanonicalizable", changed);
        api.Reject(() => api.Call("RequireBodyComplete", changed));
        CollectionAssert.AreEqual(api.Encode(source), api.Encode(changed));
        Set(Get<object>(changed, "AxisStorage"), "Owned", false);
        api.Reject(() => api.Call("RequireBodyCanonicalizable", changed));
    }

    [TestMethod]
    public void NativeUncheckedArrayConsumersRejectUnsafeDomainsBeforeMutation()
    {
        var api = new Api();
        var source = api.Data();
        var key = Get<Array>(source, "Keys").GetValue(1)!;
        Set(key, "Bits", 669); // still 21 dwords, but native SetKeyState can index all 670 keys
        api.Reject(() => api.Call("RequireBodyCanonicalizable", source));
        Set(key, "Bits", 670);
        var fields = Get<byte[]>(Get<Array>(source, "Axes").GetValue(0)!, "Fields");
        Buffer.BlockCopy(BitConverter.GetBytes(670), 0, fields, 4, 4);
        api.Reject(() => api.Call("RequireBodyCanonicalizable", source));
        fields[24] = 2;
        Buffer.BlockCopy(BitConverter.GetBytes(-1), 0, fields, 20, 4);
        api.Reject(() => api.Call("RequireBodyCanonicalizable", source));
        Buffer.BlockCopy(BitConverter.GetBytes(0), 0, fields, 20, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(-1), 0, fields, 36, 4);
        api.Reject(() => api.Call("RequireBodyCanonicalizable", source));
    }

    [TestMethod]
    public void WireCannotEraseExternalDeviceAndImeQueueGap()
    {
        var api = new Api();
        var source = api.Data();
        api.Call("RequireBodyComplete", source);
        Set(source, "CoverageGaps", Array.Empty<string>());
        var gaps = (string[])api.Call("RequiredGaps", source)!;
        Assert.IsTrue(gaps.Any(gap => gap.Contains("IME")));
        api.Reject(() => api.Call("RequireCanonicalizable", source));
        api.Reject(() => api.Call("RequireComplete", source));
    }

    [TestMethod]
    public void KeyNameDirectoryTreeIntegerBindingsAndInputHistoryEachSurviveAsD()
    {
        var api = new Api(); var source = api.Data(); var table = Get<object>(source, "KeyNames");
        api.OneKey(table, api.Ref(0x15c6008, 65), 33);
        api.Call("RequireBodyComplete", source);
        var changed = api.Copy(source);
        Get<Array>(Get<object>(changed, "KeyNames"), "Directory").SetValue(api.Ref(0x15c6008, 65), 14);
        api.Reject(() => api.Call("RequireEqual", source, changed));
        changed = api.Copy(source);
        Set(Get<Array>(Get<object>(changed, "KeyNames"), "Rows").GetValue(0)!, "Key", 34);
        api.Reject(() => api.Call("RequireEqual", source, changed));
        changed = api.Copy(source);
        // Two equal byte strings can have distinct pinned image backing.
        Set(Get<Array>(Get<object>(changed, "KeyNames"), "Rows").GetValue(0)!, "Name", api.Ref(0x15c6018, 65));
        api.Reject(() => api.Call("RequireEqual", source, changed));
        changed = api.Copy(source);
        Get<byte[]>(Get<object>(changed, "KeyNames"), "InputHistory")[48] = 1;
        api.Reject(() => api.Call("RequireEqual", source, changed));
        changed = api.Copy(source);
        Set(Get<object>(changed, "KeyNames"), "Initialized", true);
        api.Reject(() => api.Call("RequireEqual", source, changed));
        CollectionAssert.AreEqual(api.S0(source), api.S0(changed));
    }

    [TestMethod]
    public void KeyNameTreeAndPlatformKeyDomainAreValidatedBeforeAdmission()
    {
        var api = new Api(); var source = api.Data(); var table = Get<object>(source, "KeyNames");
        api.OneKey(table, api.Ref(0x15c6008, 65), 33);
        Set(table, "PaddingZero", false);
        api.Call("RequireBodyCanonicalizable", source); api.Reject(() => api.Call("RequireBodyComplete", source));
        Set(table, "PaddingZero", true); Set(table, "Shape", new[] { 0, -1, -1, 1 });
        api.Reject(() => api.Call("Validate", source));
        Set(table, "Shape", new[] { -1, -1, -1, 1 });
        var row = Get<Array>(table, "Rows").GetValue(0)!;
        Set(row, "Name", api.Ref(-1, 65));
        api.Reject(() => api.Call("RequireBodyCanonicalizable", source));
        Set(row, "Name", api.Ref(0x15c6008, 65)); Set(row, "Key", 670);
        api.Reject(() => api.Call("RequireBodyCanonicalizable", source));
        Set(row, "Key", 1); Set(table, "FirstJoystick", 669); Set(table, "ButtonsPerJoystick", 2); Set(table, "MaximumJoysticks", 1);
        api.Reject(() => api.Call("RequireBodyCanonicalizable", source));
    }

    [TestMethod]
    public void TruncatedBankMalformedArrayAndNonBooleanControlFailValidation()
    {
        var api = new Api();
        var source = api.Data();
        Set(Get<Array>(source, "Keys").GetValue(2)!, "Blocks", new byte[80]);
        api.Reject(() => api.Call("Validate", source));
        source = api.Data(); Set(Get<object>(source, "JoystickStorage"), "Present", false);
        api.Reject(() => api.Call("Validate", source));
        source = api.Data(); Get<byte[]>(source, "Scalars")[53] = 2;
        api.Reject(() => api.Call("Validate", source));
        source = api.Data(); Set(Get<Array>(source, "Joysticks").GetValue(0)!, "Positions", new byte[5]);
        api.Reject(() => api.Call("Validate", source));
    }

    [TestMethod]
    public void AcceptedKeyboardEdgesRetainDuplicatesOrderAndCapacity()
    {
        var api = new Api(); var source = api.Data(); var raw = api.AddLegacy(source);
        var keys = new[] { 97, 97, 97, 120 };
        Set(raw, "Keyboard", api.Buttons(keys, new byte[] { 1, 0, 1, 0 }, 256));
        api.Call("RequireBodyComplete", source);
        var reordered = api.Copy(source);
        var queue = Get<object>(Get<object>(Get<object>(reordered, "Legacy"), "Raw"), "Keyboard");
        Get<byte[]>(queue, "Pressed")[0] = 0; Get<byte[]>(queue, "Pressed")[1] = 1;
        api.Reject(() => api.Call("RequireEqual", source, reordered));
        var resized = api.Copy(source);
        Set(Get<object>(Get<object>(Get<object>(resized, "Legacy"), "Raw"), "Keyboard"), "Capacity", 400);
        api.Reject(() => api.Call("RequireEqual", source, resized));
        CollectionAssert.AreEqual(api.S0(source), api.S0(resized));
        Set(raw, "Keyboard", api.Buttons(new[] { 670 }, new byte[] { 1 }, 4));
        api.Reject(() => api.Call("RequireBodyCanonicalizable", source));
    }

    [TestMethod]
    public void PhysicalJoystickBindingRequiresTheRealHidAliasAndRegistration()
    {
        var api = new Api(); var source = api.Data(); var raw = api.AddLegacy(source);
        api.AddStick(raw); var target = api.Copy(source);
        api.Call("RequireBodyComplete", source);
        var s = Get<object>(source, "Legacy"); var t = Get<object>(target, "Legacy");
        api.Call("RequireLegacyBindings", s, t);
        var stick = Get<Array>(Get<object>(t, "Raw"), "Sticks").GetValue(0)!;
        Set(stick, "DeviceId", 1);
        api.Reject(() => api.Call("RequireLegacyBindings", s, t));
        Set(stick, "DeviceId", 0);
        var device = Get<Array>(Get<object>(t, "Raw"), "Devices").GetValue(0)!;
        Set(device, "HasJoystick", false);
        api.Reject(() => api.Call("RequireLegacyBindings", s, t));
        Set(device, "HasJoystick", true);
        Set(device, "Binding", Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()));
        api.Reject(() => api.Call("RequireLegacyBindings", s, t));
    }

    [TestMethod]
    public void DelayedTouchFramesAndJoystickAxesRemainPartOfAcceptedInput()
    {
        var api = new Api(); var source = api.Data(); var raw = api.AddLegacy(source); api.AddStick(raw);
        foreach (var offset in new[] { 0, 72, 80, 88, 96, 104 }) {
            var changed = api.Copy(source); var target = Get<object>(Get<object>(changed, "Legacy"), "Raw");
            Get<byte[]>(target, "Touches")[31 * 112 + offset] = 1;
            api.Reject(() => api.Call("RequireEqual", source, changed));
        }
        var axis = api.Copy(source);
        var joystick = Get<Array>(Get<object>(Get<object>(axis, "Legacy"), "Raw"), "Sticks").GetValue(0)!;
        Get<byte[]>(joystick, "Axes")[111] = 0x80;
        api.Reject(() => api.Call("RequireEqual", source, axis));
        Get<byte[]>(raw, "Touches")[84] = 77; api.Reject(() => api.Call("Validate", source));
    }

    private static T Get<T>(object owner, string field) => (T)owner.GetType().GetField(field, All)!.GetValue(owner)!;
    private static void Set(object owner, string field, object value) => owner.GetType().GetField(field, All)!.SetValue(owner, value);
    private sealed class Api
    {
        private readonly Type type;
        internal Api()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
            Assert.IsNotNull(root);
            type = Assembly.LoadFrom(Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
                .GetType("HollowKnightTAS.Runtime.FullRun.ResidentNativeInputState", true)!;
        }
        private Type TypeOf(string name) => type.GetNestedType(name, All)!;
        private object New(string name) => Activator.CreateInstance(TypeOf(name), true)!;
        private Array Rows(string name, params object[] values)
        {
            var rows = Array.CreateInstance(TypeOf(name), values.Length);
            for (var i = 0; i < values.Length; i++) rows.SetValue(values[i], i);
            return rows;
        }
        internal object Text(string value)
        {
            var text = New("TextValue"); var length = Encoding.UTF8.GetByteCount(value);
            Set(text, "Label", 72); Set(text, "Value", value); Set(text, "Inline", length < 16);
            Set(text, "Capacity", length < 16 ? 15 : length); Set(text, "Padding", new byte[length < 16 ? 19 - length : 12]);
            return text;
        }
        private object Storage(int count)
        {
            var value = New("Storage"); Set(value, "Owned", true); Set(value, "Label", 74);
            Set(value, "Present", count != 0); Set(value, "Capacity", count); return value;
        }
        private object Axis(string name)
        {
            var axis = New("Axis"); Set(axis, "Names", Rows("TextValue", Text(name), Text("Description"), Text(""))); return axis;
        }
        private object Key()
        {
            var key = New("Bitset"); Set(key, "Bits", 670); Set(key, "Label", 9); Set(key, "Blocks", new byte[84]); return key;
        }
        private object Stick(int count)
        {
            var row = New("Joystick"); Set(row, "Array", Storage(count)); Set(row, "Positions", new byte[count * 4]); return row;
        }
        internal object Ref(int rva, params byte[] bytes)
        {
            var value = New("NameReference"); Set(value, "Rva", rva); Set(value, "Bytes", bytes); return value;
        }
        internal object Table()
        {
            var value = New("KeyTable"); Set(value, "PaddingZero", true);
            var entries = Enumerable.Repeat(Ref(0x15c6004), 670).ToArray();
            Set(value, "Directory", Rows("NameReference", entries));
            return value;
        }
        internal void OneKey(object table, object name, int key)
        {
            var row = New("KeyRow"); Set(row, "Name", name); Set(row, "Key", key);
            Set(table, "Rows", Rows("KeyRow", row)); Set(table, "Root", 0); Set(table, "Shape", new[] { -1, -1, -1, 1 });
        }
        internal object Buttons(int[] keys, byte[] states, int capacity)
        {
            var b = New("ButtonQueue"); Set(b, "Keys", keys); Set(b, "Pressed", states); Set(b, "Capacity", capacity);
            Set(b, "PaddingZero", true); return b;
        }
        internal object AddLegacy(object source)
        {
            var legacy = Get<object>(source, "Legacy"); var raw = New("RawQueues");
            Set(legacy, "Present", true); Set(legacy, "Vtable", 0x166eb78); Set(legacy, "Raw", raw);
            Set(raw, "KeyMap", new int[20]); Set(raw, "MouseDelta", new byte[16]); Set(raw, "Touches", new byte[32 * 112]);
            Set(raw, "TouchOwner", new byte[20]); Set(raw, "PaddingZero", true); Set(raw, "Active", true);
            Set(raw, "Mouse", Buttons(Array.Empty<int>(), Array.Empty<byte>(), 256));
            Set(raw, "Keyboard", Buttons(Array.Empty<int>(), Array.Empty<byte>(), 256));
            return raw;
        }
        internal void AddStick(object raw)
        {
            var device = New("HidIdentity"); Set(device, "Binding", Convert.ToBase64String(new byte[32]));
            Set(device, "ResourceReferences", 1U); Set(device, "HasJoystick", true);
            var stick = New("LegacyStick"); Set(stick, "KeyMap", new int[20]); Set(stick, "Axes", new byte[112]);
            Set(stick, "ResourceReferences", 2U); Set(stick, "Buttons", Buttons(new[] { 19, 19 }, new byte[] { 1, 0 }, 256));
            Set(raw, "Sticks", Rows("LegacyStick", stick)); Set(raw, "Devices", Rows("HidIdentity", device));
            Set(raw, "DeviceCapacity", 4); Set(raw, "NextDevice", 1); Set(raw, "TreeRoot", 0); Set(raw, "TreeShape", new[] { -1, -1, -1, 1 });
        }
        internal object Data()
        {
            var data = New("Data"); Set(data, "Manager", "resident-native-global/195d330/Core//InputManager");
            Set(data, "Axes", Rows("Axis", Axis("Horizontal"), Axis("Horizontal Alternate"))); Set(data, "AxisStorage", Storage(2));
            Set(data, "Keys", Rows("Bitset", Key(), Key(), Key())); Set(data, "Input", Text("")); Set(data, "Composition", Text(""));
            Set(data, "Joysticks", Rows("Joystick", Stick(3), Stick(2))); Set(data, "JoystickStorage", Storage(2));
            Set(data, "KeyNames", Table());
            return data;
        }
        internal object? Call(string name, params object[] arguments) => type.GetMethod(name, All)!.Invoke(null, arguments);
        internal byte[] Encode(object data) => (byte[])Call("EncodeValues", data)!;
        internal byte[] S0(object data) => (byte[])Call("EncodeS0", data)!;
        internal object Copy(object data) => data.GetType().GetMethod("Copy", All)!.Invoke(data, null)!;
        internal void Reject(Action action)
        {
            var failure = Assert.ThrowsExactly<TargetInvocationException>(action);
            Assert.IsInstanceOfType<InvalidDataException>(failure.GetBaseException());
        }
    }
}
