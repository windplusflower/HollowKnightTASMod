using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Validate saved event data before it can be used as live root bindings.
    // This reads the graph; it never interns events, rehashes a dictionary,
    // normalizes list order or qualifies non-D consumers for restoration.
    internal static class CanonicalEventGraphContract
    {
        private const string CachePrefix = "R6/FsmEvent-cache/", SlotPrefix = "R6/FsmEvent-system-slot/";
        internal static Audit Validate(CanonicalCarryGraphData graph, Layout layout,
            IReadOnlyDictionary<string, string> systemAliases, Func<string, int> hash)
        {
            if (graph.Roots == null || graph.Nodes == null || graph.Externals == null
                || graph.Nodes.Length > CanonicalCarryGraphCodec.MaximumNodes || graph.Roots.Length > CanonicalCarryGraphCodec.MaximumNodes
                || systemAliases.Count == 0 || systemAliases.Count > 2000)
                throw Invalid("graph bounds");
            var roots = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var root in graph.Roots)
                if (root.Binding == null || roots.ContainsKey(root.Binding) || root.Node < 1 || root.Node > graph.Nodes.Length)
                    throw Invalid("root identity");
                else roots.Add(root.Binding, root.Node);
            CanonicalCarryGraphData.Node Node(int id, string type, string kind)
            {
                if (id < 1 || id > graph.Nodes.Length) throw Invalid("node reference");
                var node = graph.Nodes[id - 1];
                if (node == null || node.Id != id || node.Type != type || node.Kind != kind || node.Text != null
                    || (kind == "object" ? node.Fields == null || node.Items != null : node.Items == null || node.Fields != null))
                    throw Invalid("node layout");
                return node;
            }
            Dictionary<string, CanonicalCarryGraphData.Value> Fields(int id, string type, params string[] names)
            {
                var node = Node(id, type, "object");
                if (!node.Fields!.Select(field => field.Name).SequenceEqual(names.OrderBy(name => name, StringComparer.Ordinal).Select(name => type + "/" + name)))
                    throw Invalid("field registry");
                return node.Fields!.ToDictionary(field => field.Name.Substring(type.Length + 1), field => field.Value, StringComparer.Ordinal);
            }
            int Root(string binding) => roots.TryGetValue(binding, out var id) ? id : throw Invalid("missing root " + binding);
            bool IsNull(CanonicalCarryGraphData.Value value) => value.Kind == "null" && value.Type == null && value.Text == null && value.Reference == 0;
            string Scalar(CanonicalCarryGraphData.Value value, string type)
            {
                if (value.Kind != "scalar" || value.Type != type || value.Text == null || value.Reference != 0) throw Invalid("scalar layout");
                return value.Text;
            }
            int Integer(CanonicalCarryGraphData.Value value)
            {
                var text = Scalar(value, layout.IntType);
                if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result)
                    || result.ToString(CultureInfo.InvariantCulture) != text) throw Invalid("integer encoding");
                return result;
            }
            bool Boolean(CanonicalCarryGraphData.Value value)
            {
                var text = Scalar(value, layout.BoolType);
                if (text != "True" && text != "False") throw Invalid("boolean encoding");
                return text == "True";
            }
            int Reference(CanonicalCarryGraphData.Value value, string type)
            {
                if (value.Kind != "node" || value.Type != type || value.Text != null || value.Reference < 1 || value.Reference > graph.Nodes.Length)
                    throw Invalid("typed reference");
                return value.Reference;
            }
            CanonicalCarryGraphData.Value[] Array(CanonicalCarryGraphData.Value value, string element)
            {
                var items = Node(Reference(value, element + "[]"), element + "[]", "array").Items!;
                if (items.Length > 10000) throw Invalid("array bound");
                return items;
            }
            var names = roots.Where(pair => pair.Key.StartsWith(CachePrefix, StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key.Substring(CachePrefix.Length), pair => pair.Value, StringComparer.Ordinal);
            if (names.Count == 0 || names.Count > 2000 || names.Values.Distinct().Count() != names.Count) throw Invalid("cached root aliases");
            var globals = new Dictionary<string, bool>(StringComparer.Ordinal);
            var systems = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var pair in names)
            {
                var fields = Fields(pair.Value, layout.EventType, "<Path>k__BackingField", "isGlobal", "isSystemEvent", "name");
                if (pair.Key.Length > 1024 || Scalar(fields["name"], layout.StringType) != pair.Key) throw Invalid("cached event name");
                if (!IsNull(fields["<Path>k__BackingField"]) && Scalar(fields["<Path>k__BackingField"], layout.StringType).Length > 16384)
                    throw Invalid("event path bound");
                globals.Add(pair.Key, Boolean(fields["isGlobal"])); systems.Add(pair.Key, Boolean(fields["isSystemEvent"]));
            }
            if (!roots.Keys.Where(key => key.StartsWith(SlotPrefix, StringComparison.Ordinal)).OrderBy(key => key, StringComparer.Ordinal)
                .SequenceEqual(systemAliases.Keys.Select(key => SlotPrefix + key).OrderBy(key => key, StringComparer.Ordinal)))
                throw Invalid("system slot registry");
            foreach (var pair in systemAliases)
                if (!names.TryGetValue(pair.Value, out var id) || Root(SlotPrefix + pair.Key) != id || !systems[pair.Value])
                    throw Invalid("system slot alias");
            var table = Fields(Root("R6/FsmEvent-lookup"), layout.DictionaryType,
                "_syncRoot", "buckets", "comparer", "count", "entries", "freeCount", "freeList", "keys", "values", "version");
            if (!IsNull(table["_syncRoot"]) || !IsNull(table["keys"]) || !IsNull(table["values"]))
                throw Invalid("dictionary cache requires consumer binding");
            var comparer = table["comparer"];
            if (comparer.Kind != "external" || comparer.Type != layout.ComparerType || comparer.Text != null
                || comparer.Reference < 1 || comparer.Reference > graph.Externals.Length
                || graph.Externals[comparer.Reference - 1] == null
                || graph.Externals[comparer.Reference - 1].Type != layout.ComparerType
                || graph.Externals[comparer.Reference - 1].Binding != "runtime-comparer|string-default")
                throw Invalid("dictionary comparer binding");
            var buckets = Array(table["buckets"], layout.IntType).Select(Integer).ToArray();
            var entries = Array(table["entries"], layout.EntryType);
            var count = Integer(table["count"]); var freeCount = Integer(table["freeCount"]); var freeList = Integer(table["freeList"]);
            Integer(table["version"]);
            if (buckets.Length == 0 || buckets.Length != entries.Length || count < 1 || count > entries.Length
                || freeCount < 0 || freeCount > count || count - freeCount != names.Count)
                throw Invalid("dictionary counts");
            var hashes = new int[entries.Length]; var next = new int[entries.Length]; var active = new bool[entries.Length];
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < entries.Length; index++)
            {
                var fields = Fields(Reference(entries[index], layout.EntryType), layout.EntryType, "hashCode", "key", "next", "value");
                hashes[index] = Integer(fields["hashCode"]); next[index] = Integer(fields["next"]);
                if (index >= count)
                {
                    if (hashes[index] != 0 || next[index] != 0 || !IsNull(fields["key"]) || !IsNull(fields["value"])) throw Invalid("unused entry");
                }
                else if (hashes[index] == -1)
                {
                    if (!IsNull(fields["key"]) || !IsNull(fields["value"])) throw Invalid("free entry references");
                }
                else
                {
                    var name = Scalar(fields["key"], layout.StringType);
                    if (hashes[index] < 0 || hashes[index] != (hash(name) & int.MaxValue) || !usedNames.Add(name)
                        || !names.TryGetValue(name, out var id) || Reference(fields["value"], layout.EventType) != id)
                        throw Invalid("cache entry name/hash/alias");
                    active[index] = true;
                }
            }
            var visited = new HashSet<int>();
            for (var bucket = 0; bucket < buckets.Length; bucket++)
                for (var index = buckets[bucket]; index != -1; index = next[index])
                {
                    if (index < 0 || index >= count || !active[index] || !visited.Add(index) || hashes[index] % buckets.Length != bucket)
                        throw Invalid("bucket chain");
                }
            if (visited.Count != names.Count || usedNames.Count != names.Count) throw Invalid("unreachable cache entry");
            var freed = new HashSet<int>();
            for (var index = freeList; index != -1; index = next[index])
                if (index < 0 || index >= count || active[index] || !freed.Add(index)) throw Invalid("free chain");
            if (freed.Count != freeCount) throw Invalid("free chain count");
            var list = Fields(Root("R6/PlayMakerGlobals-events"), layout.ListType, "_items", "_size", "_syncRoot", "_version");
            if (!IsNull(list["_syncRoot"])) throw Invalid("global list synchronization consumer");
            var globalItems = Array(list["_items"], layout.StringType); var size = Integer(list["_size"]); Integer(list["_version"]);
            if (size < 0 || size > 2000 || size > globalItems.Length) throw Invalid("global list size");
            for (var index = 0; index < globalItems.Length; index++)
            {
                if (index >= size && IsNull(globalItems[index])) continue;
                var name = Scalar(globalItems[index], layout.StringType);
                // The original string constructor only registers an absent
                // name. AddGlobalEvents can set isGlobal on a new temporary
                // instance while an existing cached event keeps false. The
                // list and cached flag are independent saved state.
                if (index < size && !globals.ContainsKey(name)) throw Invalid("global list event");
            }
            return new Audit { CachedEvents = names.Count, SystemAliases = systemAliases.Count, GlobalEvents = size,
                GlobalListWithoutGlobalFlag = globalItems.Take(size).Count(item => !globals[item.Text!]) };
        }
        private static InvalidDataException Invalid(string reason) => new InvalidDataException("Canonical event graph differs: " + reason);
        internal sealed class Layout
        {
            public string EventType { get; set; } = "";
            public string DictionaryType { get; set; } = "";
            public string EntryType { get; set; } = "";
            public string ListType { get; set; } = "";
            public string IntType { get; set; } = "";
            public string BoolType { get; set; } = "";
            public string StringType { get; set; } = "";
            public string ComparerType { get; set; } = "";
        }
        internal sealed class Audit
        {
            public int CachedEvents { get; set; }
            public int SystemAliases { get; set; }
            public int GlobalEvents { get; set; }
            public int GlobalListWithoutGlobalFlag { get; set; }
            public bool RestoreBindingQualified => false;
        }
    }
}
