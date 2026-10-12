using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Cached events are live roots, never freshly allocated local copies.
    // Every system static alias is another root of that same object graph.
    // Import planning and all non-D consumers remain an explicit open gate.
    internal static class CanonicalEventCarryRoots
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static FieldInfo Field(Type type, string name, BindingFlags flags) => type.GetField(name, flags)
            ?? throw new InvalidDataException("Event carry field binding is absent: " + type.FullName + "/" + name);

        internal static Audit Add(IDictionary<string, object> roots, object globals)
        {
            var table = Field(typeof(FsmEvent), "_eventLookup", Static).GetValue(null) as Dictionary<string, FsmEvent>
                ?? throw new InvalidDataException("Original shared event cache is absent.");
            var globalEvents = Field(typeof(PlayMakerGlobals), "events", Instance).GetValue(globals) as List<string>
                ?? throw new InvalidDataException("Original global event list is absent.");
            if (table.Count == 0 || table.Count > 2000 || globalEvents.Count > 2000)
                throw new InvalidDataException("Original event carry table exceeds its bound.");
            var nameField = Field(typeof(FsmEvent), "name", Instance);
            // Enumerate entries, not Keys/Values: those getters create caches.
            var entries = table.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
            foreach (var pair in entries)
            {
                if (pair.Key == null || pair.Key.Length > 1024 || pair.Value == null || !Equals(nameField.GetValue(pair.Value), pair.Key))
                    throw new InvalidDataException("Original event name/cache key identity differs.");
                roots.Add("R6/FsmEvent-cache/" + pair.Key, pair.Value);
            }
            roots.Add("R6/FsmEvent-lookup", table);
            roots.Add("R6/PlayMakerGlobals-events", globalEvents);
            var aliases = new List<Alias>();
            foreach (var field in typeof(FsmEvent).GetFields(Static | BindingFlags.DeclaredOnly)
                .Where(field => !field.IsLiteral && field.FieldType == typeof(FsmEvent)).OrderBy(field => field.Name, StringComparer.Ordinal))
            {
                var value = field.GetValue(null) as FsmEvent;
                var name = value == null ? null : nameField.GetValue(value) as string;
                if (name == null || !table.TryGetValue(name, out var cached) || !ReferenceEquals(value, cached))
                    throw new InvalidDataException("Original system event static/cache alias differs: " + field.Name);
                roots.Add("R6/FsmEvent-system-slot/" + field.Name, value!);
                aliases.Add(new Alias { Slot = field.Name, Event = name });
            }
            if (aliases.Count == 0) throw new InvalidDataException("Original system event statics are absent.");
            var systemBindings = CanonicalSystemEventBindings.Snapshot();
            if (aliases.Count != systemBindings.Count || aliases.Any(alias => !systemBindings.TryGetValue(alias.Slot, out var expected) || expected != alias.Event))
                throw new InvalidDataException("Original system event slot/name registry differs.");
            return new Audit { CachedEvents = entries.Select(pair => pair.Key).ToArray(), SystemAliases = aliases.ToArray(),
                GlobalEventCount = globalEvents.Count };
        }
        internal static CanonicalEventGraphContract.Audit ValidateGraph(CanonicalCarryGraphData graph)
        {
            var tableType = typeof(Dictionary<string, FsmEvent>);
            var entryType = Field(tableType, "entries", Instance).FieldType.GetElementType()
                ?? throw new InvalidDataException("Pinned original event entry array is absent.");
            var layout = new CanonicalEventGraphContract.Layout {
                EventType = CanonicalCarryGraphCodec.Identity(typeof(FsmEvent)),
                DictionaryType = CanonicalCarryGraphCodec.Identity(tableType),
                EntryType = CanonicalCarryGraphCodec.Identity(entryType),
                ListType = CanonicalCarryGraphCodec.Identity(typeof(List<string>)),
                IntType = CanonicalCarryGraphCodec.Identity(typeof(int)), BoolType = CanonicalCarryGraphCodec.Identity(typeof(bool)),
                StringType = CanonicalCarryGraphCodec.Identity(typeof(string)),
                ComparerType = CanonicalCarryGraphCodec.Identity(EqualityComparer<string>.Default.GetType()) };
            return CanonicalEventGraphContract.Validate(graph, layout, CanonicalSystemEventBindings.Snapshot(), EqualityComparer<string>.Default.GetHashCode);
        }
        internal sealed class Audit
        {
            public string[] CachedEvents { get; set; } = Array.Empty<string>();
            public Alias[] SystemAliases { get; set; } = Array.Empty<Alias>();
            public int GlobalEventCount { get; set; }
            public bool RestoreBindingQualified => false;
        }
        internal sealed class Alias
        {
            public string Slot { get; set; } = "";
            public string Event { get; set; } = "";
        }
    }
}
