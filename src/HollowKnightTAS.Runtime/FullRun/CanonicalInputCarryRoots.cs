using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using InControl;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Only objects already created by the original input setup are registered.
    // This provides candidate data and existing-object identities; providers,
    // native devices, callback consumers and logical epochs remain open gates.
    internal static class CanonicalInputCarryRoots
    {
        private const int MaximumSets = 64;
        private const int MaximumDevices = 256;
        private const int MaximumItems = 10000;
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;
        private static readonly Type[] SetTypes = { typeof(HeroActions), typeof(InputModuleBinder.MyActionSet),
            typeof(PreMenuInputModuleActionAdaptor.PreMenuInputModuleActions) };
        private static readonly string[] DeviceTypes = { "InControl.InputDevice", "InControl.NativeInputDevice",
            "InControl.UnityInputDevice", "InControl.XInputDevice" };
        private static readonly string[] CallbackFields = { "OnSetup", "OnUpdate", "OnReset", "OnDeviceAttached",
            "OnDeviceDetached", "OnActiveDeviceChanged", "OnUpdateDevices", "OnCommitDevices" };

        internal static Audit Add(IDictionary<string, object> roots)
        {
            if (roots == null) throw new ArgumentNullException(nameof(roots));
            var setup = Field(typeof(InputManager), "<IsSetup>k__BackingField", typeof(bool), Static).GetValue(null);
            if (!Equals(setup, true)) throw new InvalidDataException("Original InputManager has not completed setup.");
            var sets = ReadList<PlayerActionSet>(RawStatic("playerActionSets"), MaximumSets, "playerActionSets");
            var devices = ReadList<InputDevice>(RawStatic("devices"), MaximumDevices, "devices");
            var active = ReadList<InputDevice>(RawStatic("activeDevices"), MaximumDevices, "activeDevices");
            if (ReferenceEquals(devices.Owner, active.Owner)) throw new InvalidDataException("Original device and active-device lists share an unexpected owner.");
            var nullDevice = Field(typeof(InputDevice), "Null", typeof(InputDevice), Static).GetValue(null) as InputDevice;
            if (ReferenceEquals(nullDevice, null) || nullDevice!.GetType() != typeof(InputDevice))
                throw new InvalidDataException("Original InputDevice.Null slot is absent or has a different type.");
            var deviceReferences = new HashSet<object>(new References());
            var deviceBindings = new Dictionary<object, string>(new References()) { [nullDevice!] = "R6/InputDevice-Null" };
            var deviceAudits = new List<DeviceAudit>();
            for (var index = 0; index < devices.Count; index++)
            {
                var device = devices.Items[index];
                if (ReferenceEquals(device, null) || ReferenceEquals(device, nullDevice) || !deviceReferences.Add(device))
                    throw new InvalidDataException("Original device registration is absent, duplicated or aliases InputDevice.Null.");
                var type = device.GetType();
                if (type.Assembly != typeof(InputDevice).Assembly || !DeviceTypes.Contains(type.FullName))
                    throw new InvalidDataException("Original device registration has an unknown type: " + type.FullName);
                var binding = "R6/InputDevice/" + index.ToString(CultureInfo.InvariantCulture) + ":" + CanonicalCarryGraphCodec.Identity(type);
                deviceBindings.Add(device, binding);
                deviceAudits.Add(new DeviceAudit { Ordinal = index, Binding = binding, RuntimeType = CanonicalCarryGraphCodec.Identity(type) });
            }
            var activeReferences = new HashSet<object>(new References());
            var activeBindings = new string[active.Count];
            for (var index = 0; index < active.Count; index++)
            {
                var device = active.Items[index];
                if (ReferenceEquals(device, null) || !deviceReferences.Contains(device) || !activeReferences.Add(device))
                    throw new InvalidDataException("Original active-device registration is absent, duplicated or outside the device list.");
                activeBindings[index] = deviceBindings[device];
            }
            var activeDevice = RawStatic("activeDevice");
            if (ReferenceEquals(activeDevice, null) || !deviceBindings.TryGetValue(activeDevice!, out var activeDeviceBinding))
                throw new InvalidDataException("Original activeDevice slot is outside its registered original devices.");
            var deviceWrapper = Wrapper<InputDevice>(RawStatic("Devices"), devices.Owner, "Devices");
            var activeWrapper = Wrapper<InputDevice>(RawStatic("ActiveDevices"), active.Owner, "ActiveDevices");
            if (ReferenceEquals(deviceWrapper, activeWrapper))
                throw new InvalidDataException("Original readonly device wrappers share an unexpected owner.");

            var proposed = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                ["R6/InputManager-playerActionSets"] = sets.Owner, ["R6/InputManager-devices"] = devices.Owner,
                ["R6/InputManager-activeDevices"] = active.Owner, ["R6/InputManager-Devices-view"] = deviceWrapper,
                ["R6/InputManager-ActiveDevices-view"] = activeWrapper, ["R6/InputDevice-Null"] = nullDevice!,
                ["R6/InputManager-activeDevice"] = activeDevice! };
            foreach (var device in deviceAudits) proposed.Add(device.Binding, devices.Items[device.Ordinal]);
            var setReferences = new HashSet<object>(new References());
            var actionReferences = new HashSet<object>(new References());
            var setAudits = new List<SetAudit>();
            var retainedLists = new List<Action> { sets.Validate, devices.Validate, active.Validate };
            var totalActions = 0; var heroSets = 0;
            for (var setIndex = 0; setIndex < sets.Count; setIndex++)
            {
                var set = sets.Items[setIndex];
                if (ReferenceEquals(set, null) || !setReferences.Add(set) || !SetTypes.Contains(set.GetType()))
                    throw new InvalidDataException("Original action set is absent, duplicated or has an unknown type.");
                if (set.GetType() == typeof(HeroActions)) heroSets++;
                var typeIdentity = CanonicalCarryGraphCodec.Identity(set.GetType());
                var key = "R6/InputActionSet/" + setIndex.ToString(CultureInfo.InvariantCulture) + ":" + typeIdentity;
                proposed.Add(key, set);
                var actions = ReadList<PlayerAction>(Field(typeof(PlayerActionSet), "actions", typeof(List<PlayerAction>), Instance).GetValue(set),
                    MaximumItems, "action-set actions");
                var localActions = new HashSet<object>(new References());
                totalActions = checked(totalActions + actions.Count);
                if (totalActions > MaximumItems) throw new InvalidDataException("Original registered action count exceeds its bound.");
                retainedLists.Add(actions.Validate);
                Wrapper<PlayerAction>(Field(typeof(PlayerActionSet), "<Actions>k__BackingField", typeof(ReadOnlyCollection<PlayerAction>), Instance).GetValue(set),
                    actions.Owner, "action-set readonly view");
                var names = Field(typeof(PlayerActionSet), "actionsByName", typeof(Dictionary<string, PlayerAction>), Instance).GetValue(set)
                    as Dictionary<string, PlayerAction> ?? throw new InvalidDataException("Original action name lookup is absent.");
                if (names.Count != actions.Count) throw new InvalidDataException("Original action name lookup count differs from its action list.");
                var actionNames = new string[actions.Count];
                for (var actionIndex = 0; actionIndex < actions.Count; actionIndex++)
                {
                    var action = actions.Items[actionIndex];
                    if (ReferenceEquals(action, null) || action.GetType() != typeof(PlayerAction) || !actionReferences.Add(action)
                        || !ReferenceEquals(Field(typeof(PlayerAction), "<Owner>k__BackingField", typeof(PlayerActionSet), Instance).GetValue(action), set))
                        throw new InvalidDataException("Original action list has an absent, duplicated, unknown or foreign action.");
                    var name = Field(typeof(PlayerAction), "<Name>k__BackingField", typeof(string), Instance).GetValue(action) as string;
                    if (name == null || name.Length == 0 || name.Length > 1024 || name.IndexOf('\0') >= 0
                        || !names.TryGetValue(name, out var named) || !ReferenceEquals(named, action))
                        throw new InvalidDataException("Original action name and dictionary/list alias differ.");
                    actionNames[actionIndex] = name;
                    localActions.Add(action);
                    proposed.Add(key + "/action/" + actionIndex.ToString(CultureInfo.InvariantCulture) + ":" + name, action);
                }
                var listening = Field(typeof(PlayerActionSet), "listenWithAction", typeof(PlayerAction), Instance).GetValue(set);
                if (!ReferenceEquals(listening, null) && !localActions.Contains(listening!))
                    throw new InvalidDataException("Original binding listener refers outside its registered action set.");
                setAudits.Add(new SetAudit { Ordinal = setIndex, Binding = key, RuntimeType = typeIdentity,
                    ActionCount = actions.Count, ActionNames = actionNames, ActionListCapacity = actions.Items.Length,
                    BindingListenerPresent = !ReferenceEquals(listening, null) });
            }
            // Original InputHandler/UI setup can register multiple distinct
            // HeroActions. Their ordered instances are separate data roots;
            // neither a type match nor the channel identifies one singleton.
            if (heroSets == 0) throw new InvalidDataException("Original Hero action-set registration is absent.");

            var keyboard = RawStatic("<KeyboardProvider>k__BackingField");
            var mouse = RawStatic("<MouseProvider>k__BackingField");
            if (!(keyboard is IKeyboardProvider) || !(mouse is IMouseProvider) || ReferenceEquals(keyboard, mouse))
                throw new InvalidDataException("Original input provider slots are absent, mistyped or unexpectedly aliased.");
            if (keyboard!.GetType() != typeof(UnityKeyboardProvider))
                throw new InvalidDataException("Original keyboard provider has an unknown implementation.");
            var mouseType = mouse!.GetType();
            var filteredName = "HollowKnightTAS.Runtime.Input.FullRunMouseBridge+FilteredProvider";
            if (mouseType != typeof(UnityMouseProvider) && (mouseType.Assembly != typeof(CanonicalInputCarryRoots).Assembly || mouseType.FullName != filteredName))
                throw new InvalidDataException("Original mouse provider has an unknown implementation.");
            proposed.Add("R6/InputProvider/Keyboard", keyboard);
            var fixedBindings = new Dictionary<string, object>(StringComparer.Ordinal) {
                ["R6/InputProvider/Keyboard"] = keyboard, ["R6/InputProvider/Mouse"] = mouse };
            object? originalMouse = null;
            if (mouseType.FullName == filteredName)
            {
                originalMouse = Field(mouseType, "original", typeof(IMouseProvider), Instance).GetValue(mouse);
                if (ReferenceEquals(originalMouse, null) || originalMouse!.GetType() != typeof(UnityMouseProvider))
                    throw new InvalidDataException("Original filtered mouse provider does not retain its known original provider.");
                fixedBindings.Add("R6/InputProvider/Mouse-original", originalMouse!);
                proposed.Add("R6/InputProvider/Mouse-original", originalMouse!);
            }
            else proposed.Add("R6/InputProvider/Mouse", mouse);
            foreach (var binding in CanonicalInputDeadzoneBindings.Create(proposed)) fixedBindings.Add(binding.Key, binding.Value);
            var callbackTargets = new Dictionary<object, string>(new References());
            var callbackAudits = CallbackFields.Select(name => Callback(name, fixedBindings, callbackTargets)).ToArray();
            var unsupported = new List<string> {
                "R6/InputManager-static-clock-focus-configuration-not-in-collection-payload",
                "R6/InputManager-device-manager-native-state-and-update-order-unqualified",
                "R6/InputProvider-fixed-slot-and-native-state-rebinding-unqualified",
                "R6/UnityMouseProvider-existing-slot-and-consumer-rebinding-unqualified",
                "R6/InputManager-callback-and-list-consumer-rebinding-unqualified",
                "R6/InputDevice-GUID-and-control-slot-rebinding-unqualified",
                "R6/InputAction-binding-axis-and-consumer-aliases-unqualified" };
            if (setAudits.Any(set => set.BindingListenerPresent)) unsupported.Add("R6/InputAction-live-binding-listener-continuation-unqualified");
            foreach (var device in deviceAudits.Where(device => device.RuntimeType != CanonicalCarryGraphCodec.Identity(typeof(InputDevice))))
                unsupported.Add("R6/InputDevice/" + device.Ordinal.ToString(CultureInfo.InvariantCulture) + "-original-type-layout-not-observed");
            foreach (var set in setAudits.Where(set => set.RuntimeType != CanonicalCarryGraphCodec.Identity(typeof(HeroActions))))
                unsupported.Add("R6/InputActionSet/" + set.Ordinal.ToString(CultureInfo.InvariantCulture) + "-original-type-layout-not-observed");

            foreach (var validate in retainedLists) validate();
            if (!ReferenceEquals(RawStatic("playerActionSets"), sets.Owner) || !ReferenceEquals(RawStatic("devices"), devices.Owner)
                || !ReferenceEquals(RawStatic("activeDevices"), active.Owner) || !ReferenceEquals(RawStatic("activeDevice"), activeDevice)
                || !ReferenceEquals(RawStatic("Devices"), deviceWrapper) || !ReferenceEquals(RawStatic("ActiveDevices"), activeWrapper)
                || !ReferenceEquals(RawStatic("<KeyboardProvider>k__BackingField"), keyboard)
                || !ReferenceEquals(RawStatic("<MouseProvider>k__BackingField"), mouse)
                || !ReferenceEquals(Field(typeof(InputDevice), "Null", typeof(InputDevice), Static).GetValue(null), nullDevice))
                throw new InvalidDataException("Original input root slots changed during their readonly carry audit.");
            if (roots.Count + proposed.Count > MaximumItems || proposed.Keys.Any(roots.ContainsKey))
                throw new InvalidDataException("Input carry roots exceed their bound or collide with a registered root.");
            foreach (var pair in proposed) roots.Add(pair.Key, pair.Value);
            return new Audit { ActionSets = setAudits.ToArray(), Devices = deviceAudits.ToArray(), ActiveDevices = activeBindings,
                ActiveDevice = activeDeviceBinding!, PlayerActionSetList = sets.Snapshot(), DeviceList = devices.Snapshot(),
                ActiveDeviceList = active.Snapshot(), Providers = new[] {
                    new ProviderAudit { Binding = "R6/InputProvider/Keyboard", RuntimeType = CanonicalCarryGraphCodec.Identity(keyboard.GetType()) },
                    new ProviderAudit { Binding = "R6/InputProvider/Mouse", RuntimeType = CanonicalCarryGraphCodec.Identity(mouseType),
                        OriginalBinding = originalMouse == null ? "" : "R6/InputProvider/Mouse-original" } },
                Callbacks = callbackAudits, RootCount = proposed.Count, ActionCount = totalActions,
                FixedBindings = new ReadOnlyDictionary<string, object>(fixedBindings), FixedBindingCount = fixedBindings.Count, HeroActionSetCount = heroSets,
                Unsupported = unsupported.OrderBy(value => value, StringComparer.Ordinal).ToArray() };
        }

        private static object? RawStatic(string name) => (typeof(InputManager).GetField(name, Static)
            ?? throw new InvalidDataException("Original InputManager static slot is absent: " + name)).GetValue(null);

        private static FieldInfo Field(Type owner, string name, Type type, BindingFlags flags)
        {
            var field = owner.GetField(name, flags);
            if (field == null || field.DeclaringType != owner || field.FieldType != type || field.IsLiteral)
                throw new InvalidDataException("Original input field layout differs: " + owner.FullName + "/" + name);
            return field;
        }

        private static ListView<T> ReadList<T>(object? value, int maximum, string label)
        {
            if (!(value is List<T> list) || value.GetType() != typeof(List<T>))
                throw new InvalidDataException("Original input list is absent or has a different type: " + label);
            var itemsField = Field(typeof(List<T>), "_items", typeof(T[]), Instance);
            var countField = Field(typeof(List<T>), "_size", typeof(int), Instance);
            var versionField = Field(typeof(List<T>), "_version", typeof(int), Instance);
            var items = itemsField.GetValue(list) as T[] ?? throw new InvalidDataException("Original input list backing array is absent: " + label);
            var count = (int)countField.GetValue(list)!; var version = (int)versionField.GetValue(list)!;
            if (count < 0 || count > maximum || items.Length < count || items.Length > MaximumItems)
                throw new InvalidDataException("Original input list exceeds its item/capacity bound: " + label);
            return new ListView<T>(list, items, count, version, () => {
                if (!ReferenceEquals(itemsField.GetValue(list), items) || !Equals(countField.GetValue(list), count)
                    || !Equals(versionField.GetValue(list), version))
                    throw new InvalidDataException("Original input list changed during its carry audit: " + label);
            });
        }

        private static object Wrapper<T>(object? value, object list, string label)
        {
            if (!(value is ReadOnlyCollection<T>) || value.GetType() != typeof(ReadOnlyCollection<T>)
                || !ReferenceEquals(Field(typeof(ReadOnlyCollection<T>), "list", typeof(IList<T>), Instance).GetValue(value), list))
                throw new InvalidDataException("Original readonly input view does not alias its raw list: " + label);
            return value;
        }

        private static CallbackAudit Callback(string name, IDictionary<string, object> fixedBindings, IDictionary<object, string> targets)
        {
            var value = RawStatic(name);
            if (ReferenceEquals(value, null)) return new CallbackAudit { Field = name };
            if (!(value is Delegate callback)) throw new InvalidDataException("Original InputManager callback slot has a different type: " + name);
            var invocation = callback.GetInvocationList();
            if (invocation.Length > 256) throw new InvalidDataException("Original InputManager callback invocation list exceeds its bound.");
            var methods = new MethodAudit[invocation.Length];
            for (var index = 0; index < invocation.Length; index++)
            {
                var item = invocation[index]; var method = item.Method; var target = item.Target;
                if (method.Module.Assembly.IsDynamic || method.MetadataToken <= 0 || method.DeclaringType == null)
                    throw new InvalidDataException("Original InputManager callback method has no bounded module/token identity.");
                var binding = "";
                if (!ReferenceEquals(target, null))
                {
                    if (!targets.TryGetValue(target!, out binding!))
                    {
                        if (targets.Count >= 2048) throw new InvalidDataException("Original InputManager callback target inventory exceeds its bound.");
                        binding = "R6/InputCallbackTarget/" + targets.Count.ToString(CultureInfo.InvariantCulture) + ":"
                            + CanonicalCarryGraphCodec.Identity(target!.GetType());
                        targets.Add(target!, binding); fixedBindings.Add(binding, target!);
                    }
                }
                methods[index] = new MethodAudit { Ordinal = index, Assembly = method.Module.Assembly.GetName().Name ?? "",
                    ModuleMvid = method.Module.ModuleVersionId.ToString("D"), MetadataToken = method.MetadataToken,
                    DeclaringType = method.DeclaringType.FullName ?? "", MethodName = method.Name, MethodIsStatic = method.IsStatic,
                    TargetBinding = binding, TargetRuntimeType = ReferenceEquals(target, null) ? "" : CanonicalCarryGraphCodec.Identity(target!.GetType()) };
            }
            if (!ReferenceEquals(RawStatic(name), callback))
                throw new InvalidDataException("Original InputManager callback slot changed during its readonly audit: " + name);
            return new CallbackAudit { Field = name, Present = true, InvocationCount = invocation.Length, Invocations = methods };
        }

        private sealed class ListView<T>
        {
            internal ListView(List<T> owner, T[] items, int count, int version, Action validate)
            { Owner = owner; Items = items; Count = count; Version = version; Validate = validate; }
            internal List<T> Owner { get; }
            internal T[] Items { get; }
            internal int Count { get; }
            internal int Version { get; }
            internal Action Validate { get; }
            internal ListAudit Snapshot() => new ListAudit { Count = Count, Capacity = Items.Length, Version = Version };
        }
        private sealed class References : IEqualityComparer<object>
        {
            public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
        internal sealed class Audit
        {
            public int RootCount { get; set; }
            public int ActionCount { get; set; }
            public int FixedBindingCount { get; set; }
            public int HeroActionSetCount { get; set; }
            // Live fixed-slot identities stay out of JSON and out of codec
            // allocation roots. The caller must supply an explicit resolver.
            [Newtonsoft.Json.JsonIgnore]
            internal IReadOnlyDictionary<string, object> FixedBindings { get; set; } = new ReadOnlyDictionary<string, object>(new Dictionary<string, object>());
            public SetAudit[] ActionSets { get; set; } = Array.Empty<SetAudit>();
            public DeviceAudit[] Devices { get; set; } = Array.Empty<DeviceAudit>();
            public string[] ActiveDevices { get; set; } = Array.Empty<string>();
            public string ActiveDevice { get; set; } = "";
            public ListAudit PlayerActionSetList { get; set; } = new ListAudit();
            public ListAudit DeviceList { get; set; } = new ListAudit();
            public ListAudit ActiveDeviceList { get; set; } = new ListAudit();
            public ProviderAudit[] Providers { get; set; } = Array.Empty<ProviderAudit>();
            public CallbackAudit[] Callbacks { get; set; } = Array.Empty<CallbackAudit>();
            public string[] Unsupported { get; set; } = Array.Empty<string>();
            public bool RestoreBindingQualified => false;
        }
        internal sealed class ListAudit
        {
            public int Count { get; set; }
            public int Capacity { get; set; }
            public int Version { get; set; }
        }
        internal sealed class SetAudit
        {
            public int Ordinal { get; set; }
            public string Binding { get; set; } = "";
            public string RuntimeType { get; set; } = "";
            public int ActionCount { get; set; }
            public int ActionListCapacity { get; set; }
            public string[] ActionNames { get; set; } = Array.Empty<string>();
            public bool BindingListenerPresent { get; set; }
        }
        internal sealed class DeviceAudit
        {
            public int Ordinal { get; set; }
            public string Binding { get; set; } = "";
            public string RuntimeType { get; set; } = "";
        }
        internal sealed class ProviderAudit
        {
            public string Binding { get; set; } = "";
            public string RuntimeType { get; set; } = "";
            public string OriginalBinding { get; set; } = "";
        }
        internal sealed class CallbackAudit
        {
            public string Field { get; set; } = "";
            public bool Present { get; set; }
            public int InvocationCount { get; set; }
            public MethodAudit[] Invocations { get; set; } = Array.Empty<MethodAudit>();
        }
        internal sealed class MethodAudit
        {
            public int Ordinal { get; set; }
            public string Assembly { get; set; } = "";
            public string ModuleMvid { get; set; } = "";
            public int MetadataToken { get; set; }
            public string DeclaringType { get; set; } = "";
            public string MethodName { get; set; } = "";
            public bool MethodIsStatic { get; set; }
            public string TargetBinding { get; set; } = "";
            public string TargetRuntimeType { get; set; } = "";
        }
    }
}
