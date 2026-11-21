using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Cached events are live shared roots; missing restore identities are
    // interned by the original factory, never allocated as local graph copies.
    // Every system static alias is another root of that same object graph.
    // Import planning and all non-D consumers remain an explicit open gate.
    internal static class CanonicalEventCarryRoots
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static FieldInfo Field(Type type, string name, BindingFlags flags) => type.GetField(name, flags)
            ?? throw new InvalidDataException("Event carry field binding is absent: " + type.FullName + "/" + name);

        internal static int PrepareMissingOriginalOwners(CanonicalCarryGraphData source, Func<bool> admitted)
        {
            ValidateGraph(source); // Complete saved names/flags/lookup/list/alias contract, before any factory call.
            const string prefix = "R6/FsmEvent-cache/";
            return PrepareOriginalEventNames(source.Roots.Where(root => root.Binding.StartsWith(prefix, StringComparison.Ordinal))
                .Select(root => root.Binding.Substring(prefix.Length)).OrderBy(name => name, StringComparer.Ordinal).ToArray(), admitted);
        }

        // Allocate identities through the original interning factory. The later
        // complete D graph applies all four event fields and source dictionary /
        // global-list backing storage; factory order is not saved cache order.
        internal static int PrepareOriginalEventNames(string[] names, Func<bool> admitted)
        {
            void Require() {
                if (admitted == null || !admitted()) throw new InvalidOperationException("Original event preparation lost paused admission.");
            }
            Require();
            if (names == null || names.Length == 0 || names.Length > 2000 || names.Any(name => name == null || name.Length > 1024)
                || !names.SequenceEqual(names.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal)))
                throw new InvalidDataException("Source original event names are not bounded, unique and ordered.");
            var factory = typeof(FsmEvent).GetMethod("GetFsmEvent", Static, null, new[] { typeof(string) }, null);
            var actualMvid = typeof(FsmEvent).Module.ModuleVersionId;
            if (actualMvid != new Guid("29eb59fa-5c99-43cd-8f33-6db074533d31"))
                CanonicalSceneMappingCarry.WarnOnce("EventCarryRoots:Mvid:" + actualMvid,
                    "[HKTAS] PlayMaker FsmEvent ModuleVersionId differs from pinned original (continuing): actual=" + actualMvid.ToString("D"));
            if (factory == null || factory.ReturnType != typeof(FsmEvent)
                || !Equals(Field(typeof(PlayMakerGlobals), "<IsPlaying>k__BackingField", Static).GetValue(null), true))
                throw new InvalidDataException("Original event factory ABI or playing identity semantics differ.");
            if (factory.MetadataToken != 100664424)
                CanonicalSceneMappingCarry.WarnOnce("EventCarryRoots:FactoryToken:" + factory.MetadataToken,
                    "[HKTAS] FsmEvent.GetFsmEvent MetadataToken differs from pinned 100664424 (continuing): actual=" + factory.MetadataToken);
            var tableField = Field(typeof(FsmEvent), "_eventLookup", Static);
            var table = tableField.GetValue(null) as Dictionary<string, FsmEvent>
                ?? throw new InvalidDataException("Original initialized event cache is absent.");
            if (table.Count == 0 || table.Count > 2000) throw new InvalidDataException("Original event cache exceeds its bound.");
            var fields = CanonicalCarryGraphCodec.Fields(typeof(FsmEvent));
            if (!fields.Select(field => field.Name).SequenceEqual(new[] { "<Path>k__BackingField", "isGlobal", "isSystemEvent", "name" }))
                throw new InvalidDataException("Original event complete field layout differs.");
            var before = table.Select(pair => Tuple.Create(pair.Key, pair.Value, fields.Select(field => field.GetValue(pair.Value)).ToArray())).ToArray();
            if (before.Any(pair => pair.Item2 == null || !Equals(pair.Item3[3], pair.Item1)))
                throw new InvalidDataException("Original event key/owner identity differs before preparation.");
            var missing = names.Where(name => !table.ContainsKey(name)).ToArray();
            if (table.Count + missing.Length > 2000) throw new InvalidDataException("Prepared original event cache exceeds its bound.");
            var globals = Field(typeof(PlayMakerGlobals), "instance", Static).GetValue(null)
                ?? throw new InvalidDataException("Original event globals owner is absent.");
            var globalField = Field(typeof(PlayMakerGlobals), "events", Instance);
            var globalList = globalField.GetValue(globals) as List<string>
                ?? throw new InvalidDataException("Original global event list is absent.");
            Add(new SortedDictionary<string, object>(StringComparer.Ordinal), globals);
            var globalNames = globalList.ToArray();
            var systemSlots = typeof(FsmEvent).GetFields(Static | BindingFlags.DeclaredOnly).Where(field => !field.IsLiteral && field.FieldType == typeof(FsmEvent))
                .Select(field => Tuple.Create(field, field.GetValue(null))).ToArray();
            foreach (var name in missing) {
                Require();
                var value = FsmEvent.GetFsmEvent(name);
                if (!ReferenceEquals(tableField.GetValue(null), table) || !table.TryGetValue(name, out var cached) || !ReferenceEquals(value, cached)
                    || value.GetType() != typeof(FsmEvent) || !Equals(fields[3].GetValue(value), name)
                    || fields[0].GetValue(value) != null || !Equals(fields[1].GetValue(value), false) || !Equals(fields[2].GetValue(value), false))
                    throw new InvalidDataException("Original event factory did not produce its exact shared default owner.");
            }
            Require();
            if (!ReferenceEquals(tableField.GetValue(null), table) || table.Count != before.Length + missing.Length
                || before.Any(pair => !table.TryGetValue(pair.Item1, out var value) || !ReferenceEquals(value, pair.Item2)
                    || fields.Where((field, index) => !Equals(field.GetValue(value), pair.Item3[index])).Any())
                || !ReferenceEquals(Field(typeof(PlayMakerGlobals), "instance", Static).GetValue(null), globals)
                || !ReferenceEquals(globalField.GetValue(globals), globalList) || !globalNames.SequenceEqual(globalList)
                || systemSlots.Any(slot => !ReferenceEquals(slot.Item1.GetValue(null), slot.Item2)))
                throw new InvalidDataException("Original event preparation changed existing events, global list or system aliases.");
            return missing.Length;
        }

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
