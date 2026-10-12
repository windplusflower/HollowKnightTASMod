using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using InControl;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalInputStaticSlots
    {
        private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        // Existing containers and providers retain their setup identity.
        // Native/backend internals are not claimed by this slot guard.
        internal static Action PrepareGuard(IReadOnlyDictionary<string, object> roots, IReadOnlyDictionary<string, object> fixedBindings)
        {
            var definitions = new[] {
                new { Type = typeof(InputManager), Field = "playerActionSets", Binding = "R6/InputManager-playerActionSets" },
                new { Type = typeof(InputManager), Field = "devices", Binding = "R6/InputManager-devices" },
                new { Type = typeof(InputManager), Field = "activeDevices", Binding = "R6/InputManager-activeDevices" },
                new { Type = typeof(InputManager), Field = "activeDevice", Binding = "R6/InputManager-activeDevice" },
                new { Type = typeof(InputManager), Field = "Devices", Binding = "R6/InputManager-Devices-view" },
                new { Type = typeof(InputManager), Field = "ActiveDevices", Binding = "R6/InputManager-ActiveDevices-view" },
                new { Type = typeof(InputDevice), Field = "Null", Binding = "R6/InputDevice-Null" },
                new { Type = typeof(InputManager), Field = "<KeyboardProvider>k__BackingField", Binding = "R6/InputProvider/Keyboard" },
                new { Type = typeof(InputManager), Field = "<MouseProvider>k__BackingField", Binding = "R6/InputProvider/Mouse" } };
            var all = CanonicalCarryRootBindings.Merge(fixedBindings, roots);
            var saved = new List<Tuple<FieldInfo, object?>>();
            foreach (var definition in definitions) {
                var field = definition.Type.GetField(definition.Field, Flags);
                if (field == null || field.IsLiteral || field.FieldType.IsValueType || !all.TryGetValue(definition.Binding, out var owner)
                    || !ReferenceEquals(field.GetValue(null), owner)) throw Invalid(definition.Binding);
                saved.Add(Tuple.Create<FieldInfo, object?>(field, owner));
            }
            foreach (var name in CanonicalInputCallbackContract.Slots) {
                var field = typeof(InputManager).GetField(name, Flags);
                if (field == null || !typeof(Delegate).IsAssignableFrom(field.FieldType)) throw Invalid(name);
                saved.Add(Tuple.Create<FieldInfo, object?>(field, field.GetValue(null)));
            }
            void Guard()
            {
                foreach (var item in saved)
                    if (!ReferenceEquals(item.Item1.GetValue(null), item.Item2)) throw Invalid(item.Item1.Name);
            }
            Guard(); return Guard;
        }
        private static InvalidDataException Invalid(string name) => new InvalidDataException("Canonical input setup slot changed: " + name);
    }
}
