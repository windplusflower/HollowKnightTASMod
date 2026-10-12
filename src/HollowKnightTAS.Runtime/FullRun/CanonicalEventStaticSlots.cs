using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HutongGames.PlayMaker;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Preserve the actual shared owners during graph preparation/application.
    // This does not qualify references held by unregistered outside consumers.
    internal static class CanonicalEventStaticSlots
    {
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        internal static Action PrepareGuard(IReadOnlyDictionary<string, object> roots)
        {
            var checks = new List<Action>();
            object Root(string name) => roots.TryGetValue(name, out var value) ? value
                : throw new InvalidDataException("Event setup root is absent: " + name);
            void Slot(Type type, object? owner, string name, object expected)
            {
                var field = type.GetField(name, owner == null ? Static : Instance);
                if (field == null || field.IsLiteral || field.FieldType.IsValueType)
                    throw new InvalidDataException("Event setup reference slot layout differs: " + type.FullName + "/" + name);
                checks.Add(() => {
                    if (!ReferenceEquals(field.GetValue(owner), expected))
                        throw new InvalidDataException("Event setup reference slot changed: " + type.FullName + "/" + name);
                });
            }
            var globalsSlot = typeof(PlayMakerGlobals).GetField("instance", Static)
                ?? throw new InvalidDataException("Original PlayMaker globals slot is absent.");
            var globals = globalsSlot.GetValue(null)
                ?? throw new InvalidDataException("Original PlayMaker globals owner is absent.");
            if (globals.GetType() != typeof(PlayMakerGlobals)) throw new InvalidDataException("Original PlayMaker globals type differs.");
            Slot(typeof(PlayMakerGlobals), null, "instance", globals);
            Slot(typeof(PlayMakerGlobals), globals, "variables", Root("PlayMakerGlobals/variables"));
            Slot(typeof(PlayMakerGlobals), globals, "events", Root("R6/PlayMakerGlobals-events"));
            Slot(typeof(Fsm), null, "EventData", Root("R6/Fsm-EventData"));
            Slot(typeof(FsmEvent), null, "_eventLookup", Root("R6/FsmEvent-lookup"));
            foreach (var pair in CanonicalSystemEventBindings.Snapshot())
                Slot(typeof(FsmEvent), null, pair.Key, Root("R6/FsmEvent-system-slot/" + pair.Key));
            var initialized = typeof(PlayMakerGlobals).GetField("<Initialized>k__BackingField", Static);
            if (initialized == null || initialized.FieldType != typeof(bool))
                throw new InvalidDataException("Original PlayMaker globals initialized flag layout differs.");
            void Guard()
            {
                if (!(bool)initialized.GetValue(null)) throw new InvalidDataException("Original PlayMaker globals became uninitialized.");
                foreach (var check in checks) check();
            }
            Guard(); return Guard;
        }
    }
}
