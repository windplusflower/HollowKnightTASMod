using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using InControl;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalInputDeadzoneBindings
    {
        internal static IReadOnlyDictionary<string, object> Create(IReadOnlyDictionary<string, object> roots)
        {
            var bindings = new SortedDictionary<string, object>(StringComparer.Ordinal);
            var axes = new HashSet<object>(new References()); var callbacks = new HashSet<object>(new References());
            var function = typeof(TwoAxisInputControl).GetField("DeadZoneFunc", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidDataException("Original two-axis deadzone slot is absent.");
            void Add(string binding, object? value)
            {
                if (!(value is TwoAxisInputControl axis) || !axes.Add(axis)) return;
                if (!(function.GetValue(axis) is Delegate callback) || callback.GetType() != function.FieldType
                    || callback.Target != null || callback.GetInvocationList().Length != 1
                    || callback.Method.DeclaringType != typeof(DeadZone) || !callback.Method.IsStatic
                    || callback.Method.Module != typeof(DeadZone).Module || callback.Method.IsGenericMethod
                    || !(callback.Method.Name == "Circular" || callback.Method.Name == "Separate"))
                    throw new InvalidDataException("Original two-axis deadzone callback differs: " + binding);
                // One delegate object can have several original consumers.
                // Keep that alias, never manufacture a method-group delegate.
                if (callbacks.Add(callback)) bindings.Add(binding + "/DeadZoneFunc", callback);
            }
            foreach (var pair in roots.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (pair.Value is HeroActions hero)
                    foreach (var field in typeof(HeroActions).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                        .Where(field => typeof(TwoAxisInputControl).IsAssignableFrom(field.FieldType)).OrderBy(field => field.Name, StringComparer.Ordinal))
                        Add("R6/InputDeadzone/" + pair.Key + "/" + field.Name, field.GetValue(hero));
                else if (pair.Value is InputDevice device)
                    foreach (var name in new[] { "<DPad>k__BackingField", "<LeftStick>k__BackingField", "<RightStick>k__BackingField" }) {
                        var field = typeof(InputDevice).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                            ?? throw new InvalidDataException("Original device axis slot is absent: " + name);
                        Add("R6/InputDeadzone/" + pair.Key + "/" + name, field.GetValue(device));
                    }
            }
            if (axes.Count > 1024) throw new InvalidDataException("Original two-axis callback catalog exceeds its bound.");
            return bindings;
        }
        private sealed class References : IEqualityComparer<object>
        {
            public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
