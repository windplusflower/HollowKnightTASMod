using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using InControl;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed class CanonicalDeviceManagerBindings
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static readonly FieldInfo ListSlot = Field(typeof(InputManager), "deviceManagers", Static);
        private static readonly FieldInfo TableSlot = Field(typeof(InputManager), "deviceManagerTable", Static);
        private readonly List<InputDeviceManager> list;
        private readonly Dictionary<Type, InputDeviceManager> table;
        private readonly InputDeviceManager[] managers;
        private readonly List<Action> ownerChecks = new List<Action>();
        private readonly List<Tuple<object, FieldInfo, uint[]>> eventBuffers = new List<Tuple<object, FieldInfo, uint[]>>();
        internal CanonicalDeviceManagerCarry.Data Data { get; }
        private readonly CanonicalDeviceManagerCarry.Data preparation;
        private static FieldInfo Field(Type type, string name, BindingFlags flags) => type.GetField(name, flags)
            ?? throw new InvalidDataException("SDK manager raw field is absent: " + type.FullName + "/" + name);
        internal CanonicalDeviceManagerBindings()
        {
            list = ListSlot.GetValue(null) as List<InputDeviceManager> ?? throw new InvalidDataException("Original SDK manager list is absent.");
            table = TableSlot.GetValue(null) as Dictionary<Type, InputDeviceManager> ?? throw new InvalidDataException("Original SDK manager type table is absent.");
            if (list.Count > 1 || table.Count != list.Count) throw new InvalidDataException("SDK manager list/table membership is not registered.");
            managers = list.ToArray(); var records = new List<CanonicalDeviceManagerCarry.Manager>();
            for (var index = 0; index < managers.Length; index++) {
                var manager = managers[index] ?? throw new InvalidDataException("SDK manager list contains a null owner.");
                var type = manager.GetType();
                if (type != typeof(NativeInputDeviceManager) || !table.TryGetValue(type, out var indexed) || !ReferenceEquals(indexed, manager))
                    throw new InvalidDataException("Original SDK manager type/list/table aliases differ.");
                var customFind = Field(type, "CustomFindDetachedDevice", Static);
                if (customFind.GetValue(null) != null)
                    throw new InvalidDataException("SDK manager custom detached-device callback is not registered.");
                ownerChecks.Add(() => { if (customFind.GetValue(null) != null)
                    throw new InvalidOperationException("SDK manager custom detached-device callback changed."); });
                int Count<T>(Type ownerType, string name, out List<T> collection)
                {
                    var field = Field(ownerType, name, Instance);
                    collection = field.GetValue(manager) as List<T> ?? throw new InvalidDataException("SDK manager collection type differs: " + name);
                    var expected = collection; var count = expected.Count;
                    ownerChecks.Add(() => { if (!ReferenceEquals(field.GetValue(manager), expected) || expected.Count != count)
                        throw new InvalidOperationException("SDK manager collection owner or membership changed: " + name); });
                    return count;
                }
                var registered = Count<InputDevice>(typeof(InputDeviceManager), "devices", out _);
                var attached = Count<NativeInputDevice>(type, "attachedDevices", out _);
                var detached = Count<NativeInputDevice>(type, "detachedDevices", out _);
                var systemProfiles = Count<InputDeviceProfile>(type, "systemDeviceProfiles", out var profiles);
                var customProfiles = Count<InputDeviceProfile>(type, "customDeviceProfiles", out _);
                var profileOwners = profiles.ToArray();
                var profileValues = CanonicalInputProfileContract.Capture(profiles, out var configurationOwners);
                ownerChecks.Add(() => { if (profiles.Count != profileOwners.Length || profiles.Where((item, ordinal) => !ReferenceEquals(item, profileOwners[ordinal])).Any())
                    throw new InvalidOperationException("SDK manager system profile order or identity changed.");
                    if (!CanonicalInputProfileContract.Capture(profiles, out var currentOwners).SequenceEqual(profileValues)
                        || currentOwners.Length != configurationOwners.Length
                        || currentOwners.Where((owner, ordinal) => !ReferenceEquals(owner, configurationOwners[ordinal])).Any())
                        throw new InvalidOperationException("SDK manager system profile raw configuration or nested owner changed."); });
                var eventsField = Field(type, "deviceEvents", Instance);
                var events = eventsField.GetValue(manager) as uint[] ?? throw new InvalidDataException("SDK manager managed event buffer is absent.");
                eventBuffers.Add(Tuple.Create<object, FieldInfo, uint[]>(manager, eventsField, events));
                records.Add(new CanonicalDeviceManagerCarry.Manager { Order = index, Type = type.FullName!, ModuleMvid = type.Module.ModuleVersionId.ToString("D"),
                    RegisteredDevices = registered, AttachedDevices = attached, DetachedDevices = detached, SystemProfiles = systemProfiles,
                    CustomProfiles = customProfiles, NativeEvents = (uint[])events.Clone(), SystemProfileContract = Convert.ToBase64String(profileValues) });
            }
            Data = CanonicalDeviceManagerCarry.Copy(new CanonicalDeviceManagerCarry.Data { Managers = records.ToArray() });
            preparation = CanonicalDeviceManagerCarry.Copy(Data);
            Guard();
        }
        internal void Guard()
        {
            if (!ReferenceEquals(ListSlot.GetValue(null), list) || !ReferenceEquals(TableSlot.GetValue(null), table) || list.Count != managers.Length
                || table.Count != managers.Length || list.Where((item, index) => !ReferenceEquals(item, managers[index])).Any())
                throw new InvalidOperationException("SDK manager original registration containers or order changed.");
            foreach (var manager in managers)
                if (!table.TryGetValue(manager.GetType(), out var indexed) || !ReferenceEquals(indexed, manager))
                    throw new InvalidOperationException("SDK manager original type-table aliases changed.");
            foreach (var check in ownerChecks) check();
            for (var index = 0; index < eventBuffers.Count; index++) {
                var item = eventBuffers[index];
                if (!ReferenceEquals(item.Item2.GetValue(item.Item1), item.Item3) || !item.Item3.SequenceEqual(preparation.Managers[index].NativeEvents))
                    throw new InvalidOperationException("SDK manager managed event buffer changed.");
            }
        }
        internal Action PrepareApply(CanonicalDeviceManagerCarry.Data source)
        {
            var saved = CanonicalDeviceManagerCarry.Copy(source);
            CanonicalDeviceManagerCarry.RequireSameConfiguration(saved, preparation);
            Guard(); var consumed = false;
            return () => {
                if (consumed) throw new InvalidOperationException("SDK manager managed buffer preparation was already consumed.");
                Guard(); consumed = true;
                for (var index = 0; index < eventBuffers.Count; index++) {
                    Array.Copy(saved.Managers[index].NativeEvents, eventBuffers[index].Item3, saved.Managers[index].NativeEvents.Length);
                    if (!eventBuffers[index].Item3.SequenceEqual(saved.Managers[index].NativeEvents))
                        throw new InvalidOperationException("SDK manager managed buffer readback differs.");
                }
            };
        }
    }
}
