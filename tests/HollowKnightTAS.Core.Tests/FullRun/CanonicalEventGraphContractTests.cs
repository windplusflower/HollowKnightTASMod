using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalEventGraphContractTests
{
    private const string EventType = "fake:Event", DictionaryType = "fake:Dictionary", EntryType = "fake:Entry",
        ListType = "fake:List", IntType = "fake:Int32", BoolType = "fake:Boolean", StringType = "fake:String",
        ComparerType = "fake:StringComparer";
    private const int Table = 1, GlobalList = 2, Alpha = 3, Beta = 4, Gamma = 5, Buckets = 6,
        Entries = 7, GlobalItems = 8, AlphaEntry = 9, BetaEntry = 10, GammaEntry = 11, FreeEntry = 12, UnusedEntry = 13;
    private static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
    { ["Finished"] = "ALPHA", ["Cancelled"] = "BETA" };
    private static CanonicalEventGraphContract.Layout Layout() => new()
    {
        EventType = EventType, DictionaryType = DictionaryType, EntryType = EntryType, ListType = ListType,
        IntType = IntType, BoolType = BoolType, StringType = StringType, ComparerType = ComparerType
    };

    [TestMethod]
    public void Accepts_collision_free_and_unused_slots_without_mutating_the_saved_graph()
    {
        var graph = ValidGraph(); var bytes = CanonicalCarryGraphWire.Encode(graph);
        var audit = Validate(graph);
        Assert.AreEqual(3, audit.CachedEvents); Assert.AreEqual(2, audit.SystemAliases); Assert.AreEqual(2, audit.GlobalEvents);
        Assert.IsFalse(audit.RestoreBindingQualified);
        CollectionAssert.AreEqual(bytes, CanonicalCarryGraphWire.Encode(graph));
        Assert.AreEqual("GAMMA", graph.Nodes[GlobalItems - 1].Items![0].Text);
        Assert.AreEqual("ALPHA", graph.Nodes[GlobalItems - 1].Items![1].Text);
        Assert.AreEqual("unused-original-string", graph.Nodes[GlobalItems - 1].Items![2].Text);
        Assert.AreEqual("null", graph.Nodes[GlobalItems - 1].Items![3].Kind);
    }

    [TestMethod]
    public void Preserves_global_list_order_duplicates_and_inactive_original_storage()
    {
        var graph = ValidGraph(); var items = graph.Nodes[GlobalItems - 1].Items!;
        items[0] = Text("ALPHA"); items[1] = Text("GAMMA"); items[2] = Text("ALPHA");
        Set(graph, GlobalList, "_size", Integer(3));
        var bytes = CanonicalCarryGraphWire.Encode(graph);
        Assert.AreEqual(3, Validate(graph).GlobalEvents);
        CollectionAssert.AreEqual(bytes, CanonicalCarryGraphWire.Encode(graph));
        CollectionAssert.AreEqual(new[] { "ALPHA", "GAMMA", "ALPHA" }, items.Take(3).Select(value => value.Text).ToArray());
    }

    [TestMethod]
    public void Rejects_cache_root_name_and_event_field_disagreement_or_collapsed_events()
    {
        Reject(graph => Set(graph, Alpha, "name", Text("BETA")));
        Reject(graph => ChangeRoot(graph, "R6/FsmEvent-cache/BETA", Alpha));
        Reject(graph => graph.Roots = graph.Roots.Where(root => root.Binding != "R6/FsmEvent-cache/GAMMA").ToArray());
    }

    [TestMethod]
    public void Rejects_system_slot_registry_alias_or_system_flag_changes()
    {
        Reject(graph => ChangeRoot(graph, "R6/FsmEvent-system-slot/Finished", Beta));
        Reject(graph => Set(graph, Alpha, "isSystemEvent", Boolean(false)));
        Reject(graph => graph.Roots = graph.Roots.Where(root => root.Binding != "R6/FsmEvent-system-slot/Finished").ToArray());
        Reject(graph => graph.Roots = graph.Roots.Append(new CanonicalCarryGraphData.Root
        { Binding = "R6/FsmEvent-system-slot/Injected", Node = Alpha }).ToArray());
    }

    [TestMethod]
    public void Rejects_entry_key_value_alias_mismatch_and_duplicate_cached_names()
    {
        Reject(graph => Set(graph, AlphaEntry, "value", Reference(Beta, EventType)));
        Reject(graph => Set(graph, AlphaEntry, "key", Text("UNKNOWN")));
        Reject(graph =>
        {
            Set(graph, BetaEntry, "key", Text("ALPHA")); Set(graph, BetaEntry, "value", Reference(Alpha, EventType));
            Set(graph, BetaEntry, "hashCode", Integer(Hash("ALPHA")));
        });
    }

    [TestMethod]
    public void Rejects_wrong_hashes_and_noncanonical_hash_encoding()
    {
        Reject(graph => Set(graph, AlphaEntry, "hashCode", Integer(Hash("ALPHA") + 5)));
        Reject(graph => Set(graph, AlphaEntry, "hashCode", Integer(-2)));
        Reject(graph => Set(graph, AlphaEntry, "hashCode", new CanonicalCarryGraphData.Value
        { Kind = "scalar", Type = IntType, Text = "017" }));
    }

    [TestMethod]
    public void Rejects_bucket_cycles_out_of_range_heads_and_omitted_active_entries()
    {
        Reject(graph => Set(graph, AlphaEntry, "next", Integer(2)));
        Reject(graph => graph.Nodes[Buckets - 1].Items![2] = Integer(99));
        Reject(graph => graph.Nodes[Buckets - 1].Items![2] = Integer(-1));
        Reject(graph => Set(graph, BetaEntry, "next", Integer(-1)));
    }

    [TestMethod]
    public void Rejects_wrong_bucket_ownership_duplicate_chains_and_deleted_slot_heads()
    {
        Reject(graph =>
        {
            graph.Nodes[Buckets - 1].Items![2] = Integer(-1); graph.Nodes[Buckets - 1].Items![1] = Integer(2);
        });
        Reject(graph => graph.Nodes[Buckets - 1].Items![3] = Integer(2));
        Reject(graph => graph.Nodes[Buckets - 1].Items![2] = Integer(3));
    }

    [TestMethod]
    public void Rejects_invalid_free_chains_and_references_retained_by_deleted_slots()
    {
        Reject(graph => Set(graph, FreeEntry, "next", Integer(3)));
        Reject(graph => Set(graph, Table, "freeList", Integer(-1)));
        Reject(graph => Set(graph, Table, "freeList", Integer(2)));
        Reject(graph => Set(graph, FreeEntry, "key", Text("ALPHA")));
        Reject(graph => Set(graph, FreeEntry, "value", Reference(Alpha, EventType)));
    }

    [TestMethod]
    public void Rejects_dirty_unused_entries_and_inconsistent_dictionary_counts()
    {
        Reject(graph => Set(graph, UnusedEntry, "hashCode", Integer(17)));
        Reject(graph => Set(graph, UnusedEntry, "next", Integer(-1)));
        Reject(graph => Set(graph, UnusedEntry, "key", Text("ALPHA")));
        Reject(graph => Set(graph, UnusedEntry, "value", Reference(Alpha, EventType)));
        Reject(graph => Set(graph, Table, "count", Integer(5)));
        Reject(graph => Set(graph, Table, "freeCount", Integer(0)));
    }

    [TestMethod]
    public void Rejects_invalid_global_list_sizes_names_and_unused_value_types()
    {
        Reject(graph => Set(graph, GlobalList, "_size", Integer(-1)));
        Reject(graph => Set(graph, GlobalList, "_size", Integer(5)));
        Reject(graph => graph.Nodes[GlobalItems - 1].Items![0] = Text("UNKNOWN"));
        Reject(graph => graph.Nodes[GlobalItems - 1].Items![2] = Integer(1));
    }

    [TestMethod]
    public void Preserves_original_global_names_whose_cached_event_flag_is_false()
    {
        var graph = ValidGraph();
        graph.Nodes[GlobalItems - 1].Items![0] = Text("BETA");
        Set(graph, Alpha, "isGlobal", Boolean(false));
        var original = CanonicalCarryGraphWire.Encode(graph);
        var audit = Validate(graph);
        Assert.AreEqual(2, audit.GlobalListWithoutGlobalFlag);
        CollectionAssert.AreEqual(original, CanonicalCarryGraphWire.Encode(graph));
    }

    [TestMethod]
    public void Rejects_unbound_collection_consumers_and_changed_comparer_bindings()
    {
        foreach (var field in new[] { "_syncRoot", "keys", "values" })
            Reject(graph => Set(graph, Table, field, Reference(Table, DictionaryType)));
        Reject(graph => Set(graph, GlobalList, "_syncRoot", Reference(GlobalList, ListType)));
        Reject(graph => graph.Externals[0].Binding = "runtime-comparer|changed");
        Reject(graph => graph.Externals[0].Type = "fake:DifferentComparer");
        Reject(graph => Set(graph, Table, "comparer", Null()));
    }

    [TestMethod]
    public void Rejects_event_field_array_entry_and_scalar_layout_tampering()
    {
        Reject(graph => graph.Nodes[Alpha - 1].Fields = graph.Nodes[Alpha - 1].Fields![1..]);
        Reject(graph => graph.Nodes[Entries - 1].Items![0] = Reference(Alpha, EventType));
        Reject(graph => graph.Nodes[Buckets - 1].Type = StringType + "[]");
        Reject(graph => Set(graph, Alpha, "isGlobal", new CanonicalCarryGraphData.Value
        { Kind = "scalar", Type = BoolType, Text = "true" }));
        Reject(graph => Set(graph, Alpha, "name", new CanonicalCarryGraphData.Value
        { Kind = "scalar", Type = StringType, Text = "ALPHA", Reference = 1 }));
    }

    private static CanonicalEventGraphContract.Audit Validate(CanonicalCarryGraphData graph)
        => CanonicalEventGraphContract.Validate(graph, Layout(), Aliases, Hash);
    private static void Reject(Action<CanonicalCarryGraphData> mutate)
    {
        var graph = ValidGraph(); mutate(graph);
        Assert.ThrowsExactly<InvalidDataException>(() => Validate(graph));
    }
    private static int Hash(string name) => name switch { "ALPHA" => 17, "BETA" => 22, "GAMMA" => 27, _ => 37 };
    private static CanonicalCarryGraphData.Value Null() => new() { Kind = "null" };
    private static CanonicalCarryGraphData.Value Integer(int value) => new()
    { Kind = "scalar", Type = IntType, Text = value.ToString(CultureInfo.InvariantCulture) };
    private static CanonicalCarryGraphData.Value Text(string value) => new() { Kind = "scalar", Type = StringType, Text = value };
    private static CanonicalCarryGraphData.Value Boolean(bool value) => new() { Kind = "scalar", Type = BoolType, Text = value ? "True" : "False" };
    private static CanonicalCarryGraphData.Value Reference(int node, string type) => new() { Kind = "node", Type = type, Reference = node };
    private static CanonicalCarryGraphData.Node Object(int id, string type, params (string name, CanonicalCarryGraphData.Value value)[] fields)
        => new()
        {
            Id = id, Type = type, Kind = "object", Fields = fields.OrderBy(field => field.name, StringComparer.Ordinal)
                .Select(field => new CanonicalCarryGraphData.Field { Name = type + "/" + field.name, Value = field.value }).ToArray()
        };
    private static CanonicalCarryGraphData.Node ArrayNode(int id, string element, params CanonicalCarryGraphData.Value[] items)
        => new() { Id = id, Type = element + "[]", Kind = "array", Items = items };
    private static CanonicalCarryGraphData.Node Event(int id, string name, bool global, bool system) => Object(id, EventType,
        ("<Path>k__BackingField", Null()), ("isGlobal", Boolean(global)), ("isSystemEvent", Boolean(system)), ("name", Text(name)));
    private static CanonicalCarryGraphData.Node Entry(int id, int hash, int next, string? name, int? node) => Object(id, EntryType,
        ("hashCode", Integer(hash)), ("key", name == null ? Null() : Text(name)), ("next", Integer(next)),
        ("value", node == null ? Null() : Reference(node.Value, EventType)));
    private static void Set(CanonicalCarryGraphData graph, int node, string field, CanonicalCarryGraphData.Value value)
    {
        var owner = graph.Nodes[node - 1]; var index = System.Array.FindIndex(owner.Fields!, item => item.Name == owner.Type + "/" + field);
        if (index < 0) throw new InvalidOperationException("Missing fixture field: " + field);
        owner.Fields![index].Value = value;
    }
    private static void ChangeRoot(CanonicalCarryGraphData graph, string binding, int node)
    {
        var index = System.Array.FindIndex(graph.Roots, root => root.Binding == binding);
        if (index < 0) throw new InvalidOperationException("Missing fixture root: " + binding);
        graph.Roots[index].Node = node;
    }
    private static CanonicalCarryGraphData ValidGraph()
    {
        // Old Mono buckets are zero based, with -1 for empty. All three active
        // hashes share bucket 2; slot 3 is deleted and slot 4 is unused storage.
        var roots = new (string binding, int node)[]
        {
            ("R6/FsmEvent-cache/ALPHA", Alpha), ("R6/FsmEvent-cache/BETA", Beta), ("R6/FsmEvent-cache/GAMMA", Gamma),
            ("R6/FsmEvent-lookup", Table), ("R6/FsmEvent-system-slot/Cancelled", Beta),
            ("R6/FsmEvent-system-slot/Finished", Alpha), ("R6/PlayMakerGlobals-events", GlobalList)
        };
        return new CanonicalCarryGraphData
        {
            Schema = "event-contract-fixture-v1", Unsupported = System.Array.Empty<string>(),
            Roots = roots.OrderBy(root => root.binding, StringComparer.Ordinal)
                .Select(root => new CanonicalCarryGraphData.Root { Binding = root.binding, Node = root.node }).ToArray(),
            Externals = new[] { new CanonicalCarryGraphData.External { Binding = "runtime-comparer|string-default", Type = ComparerType } },
            Nodes = new[]
            {
                Object(Table, DictionaryType, ("_syncRoot", Null()), ("buckets", Reference(Buckets, IntType + "[]")),
                    ("comparer", new CanonicalCarryGraphData.Value { Kind = "external", Type = ComparerType, Reference = 1 }),
                    ("count", Integer(4)), ("entries", Reference(Entries, EntryType + "[]")), ("freeCount", Integer(1)),
                    ("freeList", Integer(3)), ("keys", Null()), ("values", Null()), ("version", Integer(7))),
                Object(GlobalList, ListType, ("_items", Reference(GlobalItems, StringType + "[]")), ("_size", Integer(2)),
                    ("_syncRoot", Null()), ("_version", Integer(11))),
                Event(Alpha, "ALPHA", true, true), Event(Beta, "BETA", false, true), Event(Gamma, "GAMMA", true, false),
                ArrayNode(Buckets, IntType, Integer(-1), Integer(-1), Integer(2), Integer(-1), Integer(-1)),
                ArrayNode(Entries, EntryType, Reference(AlphaEntry, EntryType), Reference(BetaEntry, EntryType),
                    Reference(GammaEntry, EntryType), Reference(FreeEntry, EntryType), Reference(UnusedEntry, EntryType)),
                ArrayNode(GlobalItems, StringType, Text("GAMMA"), Text("ALPHA"), Text("unused-original-string"), Null()),
                Entry(AlphaEntry, 17, -1, "ALPHA", Alpha), Entry(BetaEntry, 22, 0, "BETA", Beta),
                Entry(GammaEntry, 27, 1, "GAMMA", Gamma), Entry(FreeEntry, -1, -1, null, null), Entry(UnusedEntry, 0, 0, null, null)
            }
        };
    }
}
