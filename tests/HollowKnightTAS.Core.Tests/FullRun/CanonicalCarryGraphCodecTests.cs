using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalCarryGraphCodecTests
{
    private static CanonicalCarryGraphCodec Codec() => new(new[] { typeof(Owner), typeof(Value), typeof(Pair),
        typeof(Owner[]), typeof(Value[]), typeof(Pair[]), typeof(List<Value>), typeof(Hidden), typeof(ScalarBits) }, "test-fields-v1");
    private static Dictionary<string, object> Roots(params (string key, object value)[] entries)
        => entries.ToDictionary(e => e.key, e => e.value, StringComparer.Ordinal);
    private static string? NoExternal(object value) => null;
    private static object NoBinding(string key) => throw new InvalidDataException("Unexpected external: " + key);
    private enum SignedBits : long { Min = long.MinValue, Max = long.MaxValue }
    private enum UnsignedBits : ulong { Zero = 0, Max = ulong.MaxValue }
    [TestMethod]
    public void Scalar_readback_matches_original_encoding_including_every_bit_and_decimal_representation()
    {
        var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        var same = (Func<object, object, Type, bool>)typeof(CanonicalCarryGraphCodec).GetMethod("SameScalarBits", flags)!
            .CreateDelegate(typeof(Func<object, object, Type, bool>));
        var encode = (Func<object, Type, string>)typeof(CanonicalCarryGraphCodec).GetMethod("EncodeScalar", flags)!
            .CreateDelegate(typeof(Func<object, Type, string>));
        object[] edges = { false, true, (byte)0, byte.MaxValue, sbyte.MinValue, sbyte.MaxValue, short.MinValue, short.MaxValue,
            (ushort)0, ushort.MaxValue, int.MinValue, int.MaxValue, (uint)0, uint.MaxValue, long.MinValue, long.MaxValue,
            (ulong)0, ulong.MaxValue, '\0', '\uffff', SignedBits.Min, SignedBits.Max, UnsignedBits.Zero, UnsignedBits.Max,
            0f, -0f, float.PositiveInfinity, float.NegativeInfinity, float.NaN,
            0d, -0d, double.PositiveInfinity, double.NegativeInfinity, double.NaN,
            decimal.Zero, decimal.MinValue, decimal.MaxValue, new decimal(0, 0, 0, true, 0),
            new decimal(100, 0, 0, false, 2), new decimal(1, 0, 0, false, 0), new decimal(0, 0, 0, false, 28) };
        void Check(object a, object b) {
            var type = a.GetType(); Assert.AreEqual(encode(a, type) == encode(b, type), same(a, b, type));
        }
        foreach (var a in edges) foreach (var b in edges) if (a.GetType() == b.GetType()) Check(a, b);
        var random = new Random(73191); var bytes = new byte[8];
        for (var i = 0; i < 10000; i++) {
            random.NextBytes(bytes); var bits = BitConverter.ToInt64(bytes);
            object single = BitConverter.Int32BitsToSingle((int)bits), dual = BitConverter.Int64BitsToDouble(bits);
            Check(single, single); Check(single, BitConverter.Int32BitsToSingle((int)bits ^ 1));
            Check(dual, dual); Check(dual, BitConverter.Int64BitsToDouble(bits ^ 1));
            object number = new decimal(random.Next(), random.Next(), random.Next(), i % 2 == 0, (byte)(i % 29));
            Check(number, number); Check(number, decimal.Zero);
        }
    }

    private static (CanonicalPreparedWritePlan plan, CanonicalCarryGraphCodec.PreparedLifecycleWrites phase,
        Owner target, Owner other, Dictionary<string, object> roots, CanonicalCarryGraphCodec.PreparedView view) LifecyclePlan()
    {
        var codec = Codec(); var target = new Owner { Counter = 1 }; var other = new Owner { Counter = 2 };
        var roots = Roots(("owner", target), ("other", other));
        var graph = codec.Capture(Roots(("owner", new Owner { Counter = 70, Current = new Value { Number = 8 } }),
            ("other", new Owner { Counter = 80 })), NoExternal);
        CanonicalCarryGraphCodec.PreparedLifecycleWrites? phase = null; CanonicalCarryGraphCodec.PreparedView? view = null;
        var plan = codec.PrepareOwnedGraph(graph, roots, NoBinding, value => view = value,
            lifecycleWriteRoots: new[] { "owner" }, exposeLifecycleWrites: value => phase = value);
        return (plan, phase!, target, other, roots, view!);
    }

    [TestMethod]
    public void Read_only_final_window_checks_main_once_before_first_freeze_and_source_write()
    {
        var x = LifecyclePlan(); var trace = new List<string>(); var eventSlot = 0; var mainReads = 0;
        x.phase.BeginOriginalLifecycle(() => trace.Add("begin"));
        x.target.Counter = 5; // Original selected-owner lifecycle, before final read-only window.
        eventSlot = 17; // Explicit original EventData source replay ends before this window.
        trace.Add("incoming-read"); Assert.AreEqual(5, x.target.Counter);
        trace.Add("native-read"); Assert.AreEqual(2, x.other.Counter);
        x.phase.PrepareWritesAfterOriginalLifecycle(() => {
            mainReads++; trace.Add("main-sdk-read");
            Assert.AreEqual(17, eventSlot); Assert.AreEqual(5, x.target.Counter); Assert.AreEqual(2, x.other.Counter);
        });
        trace.Add("first-freeze-complete"); Assert.AreEqual(5, x.target.Counter); Assert.IsNull(x.target.Current);
        x.plan.Apply(); trace.Add("source-write-readback"); Assert.AreEqual(70, x.target.Counter);
        x.plan.VerifyReadback(); trace.Add("independent-after");
        Assert.AreEqual(1, mainReads);
        CollectionAssert.AreEqual(new[] { "begin", "incoming-read", "native-read", "main-sdk-read", "first-freeze-complete",
            "source-write-readback", "independent-after" }, trace);
        var failed = LifecyclePlan(); failed.phase.BeginOriginalLifecycle(() => { });
        Assert.ThrowsExactly<InvalidDataException>(() => failed.phase.PrepareWritesAfterOriginalLifecycle(() => throw new InvalidDataException("main source differs")));
        Assert.AreEqual(1, failed.target.Counter); Assert.AreEqual(2, failed.other.Counter); Assert.IsNull(failed.target.Current);
        Assert.ThrowsExactly<InvalidOperationException>(failed.plan.Apply);
    }

    [TestMethod]
    public void Lifecycle_completed_plan_readback_rejects_later_failed_phase_reentry()
    {
        var x = LifecyclePlan(); x.phase.BeginOriginalLifecycle(() => { });
        x.phase.PrepareWritesAfterOriginalLifecycle(() => { }); x.plan.Apply(); x.plan.VerifyReadback();
        Assert.ThrowsExactly<InvalidOperationException>(() => x.phase.BeginOriginalLifecycle(() => { }));
        Assert.ThrowsExactly<InvalidOperationException>(x.plan.VerifyReadback);
        var y = LifecyclePlan(); y.phase.BeginOriginalLifecycle(() => { });
        y.phase.PrepareWritesAfterOriginalLifecycle(() => { }); y.plan.Apply();
        Assert.ThrowsExactly<InvalidOperationException>(() => y.phase.PrepareWritesAfterOriginalLifecycle(() => { }));
        Assert.ThrowsExactly<InvalidOperationException>(y.plan.VerifyReadback);
    }

    [TestMethod]
    public void Lifecycle_preserves_source_members_and_freezes_all_actual_slots_only_after_original_window()
    {
        var x = LifecyclePlan(); var sourceMember = x.view.Field(x.target, typeof(Owner), nameof(Owner.Current));
        x.phase.BeginOriginalLifecycle(() => { }); x.target.Counter = 5; x.target.Current = new Value();
        x.phase.PrepareWritesAfterOriginalLifecycle(() => { }); x.plan.Apply(); x.plan.VerifyReadback();
        Assert.AreEqual(70, x.target.Counter); Assert.AreEqual(80, x.other.Counter);
        Assert.AreSame(sourceMember, x.target.Current);
        Assert.ThrowsExactly<InvalidOperationException>(x.plan.Apply);
        Assert.ThrowsExactly<InvalidOperationException>(() => x.phase.PrepareWritesAfterOriginalLifecycle(() => { }));
    }

    [TestMethod]
    public void Lifecycle_rejects_pre_window_and_post_prepare_mutation_without_rearming()
    {
        var x = LifecyclePlan(); x.target.Counter++;
        Assert.ThrowsExactly<InvalidOperationException>(() => x.phase.BeginOriginalLifecycle(() => { }));
        x.target.Counter--;
        Assert.ThrowsExactly<InvalidOperationException>(() => x.phase.BeginOriginalLifecycle(() => { }));
        var y = LifecyclePlan(); y.phase.BeginOriginalLifecycle(() => { });
        y.phase.PrepareWritesAfterOriginalLifecycle(() => { }); y.target.Counter++;
        Assert.ThrowsExactly<InvalidOperationException>(y.plan.Apply);
        y.target.Counter--; Assert.ThrowsExactly<InvalidOperationException>(y.plan.Apply);
    }

    [TestMethod]
    public void Lifecycle_rejects_early_use_other_roots_owner_changes_and_failed_admission_permanently()
    {
        var early = LifecyclePlan(); Assert.ThrowsExactly<InvalidOperationException>(early.plan.Guard);
        Assert.ThrowsExactly<InvalidOperationException>(() => early.phase.BeginOriginalLifecycle(() => { }));
        var x = LifecyclePlan(); x.phase.BeginOriginalLifecycle(() => { }); x.other.Counter++;
        Assert.ThrowsExactly<InvalidOperationException>(() => x.phase.PrepareWritesAfterOriginalLifecycle(() => { }));
        x.other.Counter--; Assert.ThrowsExactly<InvalidOperationException>(() => x.phase.PrepareWritesAfterOriginalLifecycle(() => { }));
        var y = LifecyclePlan(); y.phase.BeginOriginalLifecycle(() => { }); y.roots["owner"] = new Owner();
        Assert.ThrowsExactly<InvalidOperationException>(() => y.phase.PrepareWritesAfterOriginalLifecycle(() => { }));
        var z = LifecyclePlan();
        Assert.ThrowsExactly<InvalidDataException>(() => z.phase.BeginOriginalLifecycle(() => throw new InvalidDataException()));
        Assert.ThrowsExactly<InvalidOperationException>(() => z.phase.BeginOriginalLifecycle(() => { }));
        var q = LifecyclePlan(); q.phase.BeginOriginalLifecycle(() => { });
        Assert.ThrowsExactly<InvalidDataException>(() => q.phase.PrepareWritesAfterOriginalLifecycle(() => throw new InvalidDataException()));
        Assert.ThrowsExactly<InvalidOperationException>(() => q.phase.PrepareWritesAfterOriginalLifecycle(() => { }));
    }

    [TestMethod]
    public void Lifecycle_rejects_cross_thread_use_and_incomplete_root_alias_selection()
    {
        var x = LifecyclePlan(); x.phase.BeginOriginalLifecycle(() => { });
        Exception? error = null;
        var thread = new System.Threading.Thread(() => { try { x.phase.PrepareWritesAfterOriginalLifecycle(() => { }); } catch (Exception e) { error = e; } });
        thread.Start(); thread.Join(); Assert.IsInstanceOfType<InvalidOperationException>(error);
        Assert.ThrowsExactly<InvalidOperationException>(() => x.phase.PrepareWritesAfterOriginalLifecycle(() => { }));
        var codec = Codec(); var source = new Owner(); var target = new Owner();
        var graph = codec.Capture(Roots(("a", source), ("b", source)), NoExternal);
        foreach (var names in new[] { Array.Empty<string>(), new[] { "a" }, new[] { "missing" }, new[] { "a", "a" } })
            Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareOwnedGraph(graph, Roots(("a", target), ("b", target)), NoBinding,
                lifecycleWriteRoots: names, exposeLifecycleWrites: _ => { }));
    }

    [TestMethod]
    public void Prepared_source_slots_survive_live_imported_root_scalar_sender_and_struct_mutation()
    {
        var codec = Codec(); var target = new Owner();
        var graph = codec.Capture(Roots(("owner", new Owner { Counter = 73, Current = new Value { Number = 9 },
            Vector = new Pair { X = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)), Y = 17 } })), NoExternal);
        CanonicalCarryGraphCodec.PreparedView? view = null;
        var plan = codec.PrepareOwnedGraph(graph, Roots(("owner", target)), NoBinding, value => view = value);
        plan.Apply(); Assert.AreSame(target, view!.Root("owner")); var importedSender = target.Current;
        target.Counter = -3; target.Current = new Value(); target.Vector = new Pair { X = 0, Y = -8 };
        Assert.AreEqual(73, view.Field(target, typeof(Owner), nameof(Owner.Counter)));
        Assert.AreSame(importedSender, view.Field(target, typeof(Owner), nameof(Owner.Current)));
        var frozen = (Pair)view.Field(target, typeof(Owner), nameof(Owner.Vector))!;
        Assert.AreEqual(unchecked((int)0x80000000), BitConverter.SingleToInt32Bits(frozen.X)); Assert.AreEqual(17f, frozen.Y);
        Assert.ThrowsExactly<InvalidOperationException>(plan.VerifyReadback);
    }

    [TestMethod]
    [TestCategory("OfflineCodecTiming")]
    public void Production_sized_graph_retains_shared_aliases_and_reports_transaction_phase_costs()
    {
        var codec = Codec();
        var source = new Dictionary<string, object>(StringComparer.Ordinal);
        var target = new Dictionary<string, object>(StringComparer.Ordinal);
        var shared = new Value { Number = 137 };
        for (var i = 0; i < 650; i++) {
            var owner = new Owner { Counter = i, Current = shared,
                Values = Enumerable.Range(0, 150).Select(n => new Value { Number = n }).ToArray() };
            owner.Next = owner;
            source.Add("root/" + i, owner); target.Add("root/" + i, new Owner());
        }
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var graph = codec.Capture(source, NoExternal);
        Console.WriteLine("codec-sized capture nodes=" + graph.Nodes.Length + ";ms=" + timer.Elapsed.TotalMilliseconds);
        timer.Restart(); var bytes = codec.Encode(graph);
        Console.WriteLine("codec-sized encode bytes=" + bytes.Length + ";ms=" + timer.Elapsed.TotalMilliseconds);
        timer.Restart(); var decoded = codec.Decode(bytes);
        Console.WriteLine("codec-sized decode ms=" + timer.Elapsed.TotalMilliseconds);
        timer.Restart(); var plan = codec.PrepareOwnedGraph(decoded, target, NoBinding);
        Console.WriteLine("codec-sized prepare ms=" + timer.Elapsed.TotalMilliseconds);
        timer.Restart(); plan.Guard();
        Console.WriteLine("codec-sized guard ms=" + timer.Elapsed.TotalMilliseconds);
        timer.Restart(); plan.Apply();
        Console.WriteLine("codec-sized apply ms=" + timer.Elapsed.TotalMilliseconds);
        timer.Restart(); for (var i = 0; i < 12; i++) plan.VerifyReadback();
        Console.WriteLine("codec-sized twelve-independent-readbacks ms=" + timer.Elapsed.TotalMilliseconds);
        timer.Restart(); for (var i = 0; i < 32; i++) { plan.VerifyReadback(); plan.VerifyReadback(); plan.VerifyReadback(); }
        var threeReads = timer.Elapsed.TotalMilliseconds;
        timer.Restart(); for (var i = 0; i < 32; i++) plan.VerifyReadback();
        Console.WriteLine("codec-sized readonly final window (.NET8 only): iterations=32;oldMainReads=96;newMainReads=32;oldMs="
            + threeReads + ";newMs=" + timer.Elapsed.TotalMilliseconds);
        var first = (Owner)target["root/0"];
        foreach (Owner owner in target.Values) { Assert.AreSame(first.Current, owner.Current); Assert.AreSame(owner, owner.Next); }
        first.Counter++;
        Assert.ThrowsExactly<InvalidOperationException>(() => plan.VerifyReadback());
    }

    [TestMethod]
    public void Later_domain_retains_actual_imported_node_alias_including_root_and_cycles()
    {
        var codec = Codec(); var source = new Owner { Current = new Value { Number = 29 } };
        source.Next = source;
        var ids = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        var first = codec.Capture(Roots(("main", source)), NoExternal, (value, id) => ids.Add(value, id));
        var later = codec.Capture(Roots(("later", new Owner { Current = source.Current, Next = source })),
            value => ids.TryGetValue(value, out var id) ? "main-node/" + id : null);
        var target = new Owner(); CanonicalCarryGraphCodec.PreparedView? imported = null;
        var main = codec.PrepareGraph(first, Roots(("main", target)), NoBinding, view => imported = view);
        Assert.IsNull(target.Current); main.Apply(); main.VerifyReadback();
        var child = target.Current; var next = new Owner();
        var tail = codec.PrepareGraph(later, Roots(("later", next)), key => imported!.Node(int.Parse(key.Substring("main-node/".Length))));
        tail.Apply(); tail.VerifyReadback(); main.VerifyReadback();
        Assert.AreSame(target, next.Next); Assert.AreSame(target, target.Next);
        Assert.AreSame(child, next.Current); Assert.AreEqual(29, next.Current!.Number);
        Assert.ThrowsExactly<InvalidDataException>(() => imported!.Node(0));
        Assert.ThrowsExactly<InvalidDataException>(() => imported!.Node(first.Nodes.Length + 1));
    }

    [TestMethod]
    public void Deferred_producer_rebinds_every_original_field_and_array_slot_without_replacing_managed_nodes()
    {
        var codec = new CanonicalCarryGraphCodec(new[] { typeof(ExternalOwner), typeof(object[]) }, "late-owner-v1");
        var old = new Value(); var born = new Value(); var sourceNative = new Value();
        var graph = codec.Capture(Roots(("owner", new ExternalOwner { Item = sourceNative, Items = new object[] { sourceNative, sourceNative } })),
            value => ReferenceEquals(value, sourceNative) ? "geo-owner" : null);
        var target = new ExternalOwner(); CanonicalCarryGraphCodec.ExternalRebinding? deferred = null;
        CanonicalCarryGraphCodec.PreparedView? prepared = null;
        var plan = codec.PrepareOwnedGraph(graph, Roots(("owner", target)), _ => old,
            view => prepared = view, exposeExternalRebinding: value => deferred = value);
        Assert.ThrowsExactly<InvalidOperationException>(() => deferred!.Prepare(Roots(("geo-owner", born))));
        plan.Apply(); var array = target.Items;
        var late = deferred!.Prepare(Roots(("geo-owner", born)));
        Assert.AreSame(old, target.Item); late.Apply(); plan.VerifyReadback(); late.VerifyReadback();
        Assert.AreSame(array, target.Items); Assert.AreSame(born, target.Item);
        Assert.IsTrue(target.Items.All(item => ReferenceEquals(item, born)));
        Assert.ThrowsExactly<InvalidOperationException>(() => deferred.Prepare(Roots(("geo-owner", new Value()))));
        target.Item = old;
        Assert.AreSame(born, prepared!.Field(target, typeof(ExternalOwner), nameof(ExternalOwner.Item)));
        Assert.ThrowsExactly<InvalidOperationException>(plan.VerifyReadback);
    }

    [TestMethod]
    public void Deferred_producer_rejects_unknown_binding_alias_collapse_and_changed_detached_array_slot()
    {
        foreach (var fault in new[] { "unknown", "collapse", "changed" }) {
            var codec = new CanonicalCarryGraphCodec(new[] { typeof(ExternalOwner), typeof(object[]) }, "late-owner-v1");
            var one = new Value(); var two = new Value(); var oldOne = new Value(); var oldTwo = new Value();
            var graph = codec.Capture(Roots(("owner", new ExternalOwner { Item = one, Items = new object[] { one, two } })),
                value => ReferenceEquals(value, one) ? "one" : ReferenceEquals(value, two) ? "two" : null);
            var target = new ExternalOwner(); CanonicalCarryGraphCodec.ExternalRebinding? deferred = null;
            var plan = codec.PrepareOwnedGraph(graph, Roots(("owner", target)), key => key == "one" ? oldOne : oldTwo,
                exposeExternalRebinding: value => deferred = value);
            plan.Apply();
            if (fault == "unknown") Assert.ThrowsExactly<InvalidDataException>(() => deferred!.Prepare(Roots(("unknown", new Value()))));
            else if (fault == "collapse") Assert.ThrowsExactly<InvalidDataException>(() => deferred!.Prepare(Roots(("one", oldTwo))));
            else {
                var late = deferred!.Prepare(Roots(("one", new Value()))); target.Items[0] = new Value();
                Assert.ThrowsExactly<InvalidOperationException>(() => late.Apply()); Assert.AreSame(oldOne, target.Item);
                Assert.ThrowsExactly<InvalidOperationException>(() => late.Apply());
            }
        }
    }
    private sealed class ExternalOwner { public object? Item; public object[] Items = Array.Empty<object>(); }

    [TestMethod]
    public void Finite_marker_phase_preserves_disjoint_geo_aliases_and_all_parent_slots()
    {
        var codec = new CanonicalCarryGraphCodec(new[] { typeof(ExternalOwner), typeof(object[]) }, "two-producers");
        var sourceGeo = new Value(); var sourceMarker = new Value(); var oldGeo = new Value(); var oldMarker = new Value();
        var bornGeo = new Value(); var bornMarker = new Value();
        var graph = codec.Capture(Roots(("owner", new ExternalOwner { Item = sourceGeo, Items = new object[] { sourceGeo, sourceMarker, sourceMarker } })),
            value => ReferenceEquals(value, sourceGeo) ? "geo" : ReferenceEquals(value, sourceMarker) ? "marker" : null);
        CanonicalCarryGraphCodec.ExternalRebinding? phases = null;
        var target = new ExternalOwner();
        var parent = codec.PrepareOwnedGraph(graph, Roots(("owner", target)), key => key == "geo" ? oldGeo : oldMarker,
            exposeExternalRebinding: value => phases = value);
        parent.Apply(); var array = target.Items;
        phases!.Prepare(Roots(("geo", bornGeo))).Apply(); phases.CompleteGeoPhase();
        var marker = phases.PrepareMarker(Roots(("marker", bornMarker))); marker.Apply();
        parent.VerifyReadback(); marker.VerifyReadback();
        Assert.AreSame(array, target.Items); Assert.AreSame(bornGeo, target.Item); Assert.AreSame(bornGeo, target.Items[0]);
        Assert.AreSame(bornMarker, target.Items[1]); Assert.AreSame(bornMarker, target.Items[2]);
        Assert.ThrowsExactly<InvalidOperationException>(() => phases.PrepareMarker(Roots(("marker", new Value()))));
    }

    [TestMethod]
    public void Finite_marker_phase_rejects_order_overlap_shared_target_and_failure_retry()
    {
        foreach (var fault in new[] { "order", "unfinished", "overlap", "shared", "changed" }) {
            var codec = new CanonicalCarryGraphCodec(new[] { typeof(ExternalOwner), typeof(object[]) }, "two-producers");
            var sourceGeo = new Value(); var sourceMarker = new Value(); var oldGeo = new Value(); var oldMarker = new Value(); var bornGeo = new Value();
            var graph = codec.Capture(Roots(("owner", new ExternalOwner { Item = sourceGeo, Items = new object[] { sourceMarker } })),
                value => ReferenceEquals(value, sourceGeo) ? "geo" : ReferenceEquals(value, sourceMarker) ? "marker" : null);
            CanonicalCarryGraphCodec.ExternalRebinding? phases = null; var target = new ExternalOwner();
            var parent = codec.PrepareOwnedGraph(graph, Roots(("owner", target)), key => key == "geo" ? oldGeo : oldMarker,
                exposeExternalRebinding: value => phases = value); parent.Apply();
            if (fault == "order") Assert.ThrowsExactly<InvalidOperationException>(() => phases!.PrepareMarker(Roots(("marker", new Value()))));
            else {
                var geo = phases!.Prepare(Roots(("geo", bornGeo)));
                if (fault == "unfinished") Assert.ThrowsExactly<InvalidOperationException>(phases.CompleteGeoPhase);
                else {
                    geo.Apply(); phases.CompleteGeoPhase();
                    if (fault == "overlap") Assert.ThrowsExactly<InvalidDataException>(() => phases.PrepareMarker(Roots(("geo", new Value()))));
                    else if (fault == "shared") Assert.ThrowsExactly<InvalidDataException>(() => phases.PrepareMarker(Roots(("marker", bornGeo))));
                    else {
                        var marker = phases.PrepareMarker(Roots(("marker", new Value()))); target.Items[0] = new Value();
                        Assert.ThrowsExactly<InvalidOperationException>(marker.Apply);
                    }
                }
            }
            Assert.ThrowsExactly<InvalidOperationException>(() => phases!.PrepareMarker(Roots(("marker", new Value()))));
        }
    }

    [TestMethod]
    public void Array_roots_are_rejected_symmetrically_by_capture_and_import_before_any_live_write()
    {
        var codec = new CanonicalCarryGraphCodec(new[] { typeof(ArrayOwner), typeof(int[]) }, "bound-array-v1");
        var source = new[] { 17, 29 }; var target = new[] { -1, -2 };
        Assert.ThrowsExactly<InvalidDataException>(() => codec.Capture(Roots(("array", source)), NoExternal));
        var graph = codec.Capture(Roots(("array", new ArrayOwner { Items = source })), NoExternal);
        graph.Roots[0].Node = graph.Nodes.Single(node => node.Kind == "array").Id;
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareApply(graph, Roots(("array", target)), NoBinding));
        CollectionAssert.AreEqual(new[] { -1, -2 }, target);
    }

    [TestMethod]
    public void Graph_application_is_single_use_and_retains_existing_array_identity_until_apply()
    {
        var codec = new CanonicalCarryGraphCodec(new[] { typeof(ArrayOwner), typeof(int[]) }, "array-owner-v1");
        var graph = codec.Capture(Roots(("owner", new ArrayOwner { Items = new[] { 17, 29 } })), NoExternal);
        var old = new[] { -1, -2 }; var target = new ArrayOwner { Items = old };
        var apply = codec.PrepareApply(graph, Roots(("owner", target)), NoBinding);
        Assert.AreSame(old, target.Items); CollectionAssert.AreEqual(new[] { -1, -2 }, old);
        apply(); CollectionAssert.AreEqual(new[] { 17, 29 }, target.Items);
        target.Items[0] = 77; Assert.ThrowsExactly<InvalidOperationException>(() => apply()); Assert.AreEqual(77, target.Items[0]);
    }
    private sealed class ArrayOwner { public int[] Items = Array.Empty<int>(); }

    [TestMethod]
    public void Changed_scalar_bits_reference_aliases_or_root_membership_reject_the_whole_prepared_graph()
    {
        foreach (var fault in new[] { "bits", "owner", "alias" }) {
            var codec = Codec(); var source = new Owner { Counter = 17 }; var first = new Owner(); var second = new Owner();
            var graph = codec.Capture(Roots(("first", source), ("second", new Owner())), NoExternal);
            var roots = Roots(("first", first), ("second", second)); var apply = codec.PrepareApply(graph, roots, NoBinding);
            if (fault == "bits") second.Vector.X = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
            if (fault == "owner") roots["second"] = new Owner();
            if (fault == "alias") second.Current = new Value();
            Assert.ThrowsExactly<InvalidOperationException>(() => apply()); Assert.AreEqual(0, first.Counter);
            second.Vector.X = 0; roots["second"] = second; second.Current = null;
            Assert.ThrowsExactly<InvalidOperationException>(() => apply()); Assert.AreEqual(0, first.Counter);
        }
    }

    [TestMethod]
    public void Consumer_preflight_reads_source_fields_and_target_aliases_before_any_live_write()
    {
        var source = new Owner { Counter = 17, Current = new Value { Number = 29 } };
        source.Next = source;
        var target = new Owner { Counter = -4 };
        var codec = Codec(); var graph = codec.Capture(Roots(("root", source)), NoExternal);
        var checkedSource = false;
        var apply = codec.PrepareApply(graph, Roots(("root", target)), NoBinding, view => {
            Assert.AreSame(target, view.Root("root"));
            Assert.AreEqual(-4, target.Counter); Assert.IsNull(target.Next); Assert.IsNull(target.Current);
            Assert.AreEqual(17, view.Field(target, typeof(Owner), "Counter"));
            Assert.AreSame(target, view.Field(target, typeof(Owner), "Next"));
            var current = view.Field(target, typeof(Owner), "Current")!;
            Assert.AreEqual(29, view.Field(current, typeof(Value), "Number"));
            Assert.ThrowsExactly<InvalidDataException>(() => view.Field(new Value(), typeof(Value), "Number"));
            checkedSource = true;
        });
        Assert.IsTrue(checkedSource); Assert.AreEqual(-4, target.Counter);
        source.Counter = 999; source.Current.Number = 999;
        apply(); Assert.AreEqual(17, target.Counter); Assert.AreEqual(29, target.Current!.Number);
        Assert.AreSame(target, target.Next);
    }

    [TestMethod]
    public void Rejected_consumer_alias_preflight_preserves_every_live_root()
    {
        var first = new Owner { Counter = 17 }; var second = new Owner { Counter = 31 };
        first.Next = second; second.Next = first;
        var targetFirst = new Owner { Counter = -1 }; var targetSecond = new Owner { Counter = -2 };
        var codec = Codec(); var graph = codec.Capture(Roots(("a", first), ("b", second)), NoExternal);
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareApply(graph,
            Roots(("a", targetFirst), ("b", targetSecond)), NoBinding, view => {
                Assert.AreSame(targetSecond, view.Field(targetFirst, typeof(Owner), "Next"));
                throw new InvalidDataException("consumer binding rejects the prepared source");
            }));
        Assert.AreEqual(-1, targetFirst.Counter); Assert.AreEqual(-2, targetSecond.Counter);
        Assert.IsNull(targetFirst.Next); Assert.IsNull(targetSecond.Next);
    }

    [TestMethod]
    public void Malformed_graph_never_reaches_consumer_preflight()
    {
        var codec = Codec(); var graph = codec.Capture(Roots(("root", new Owner { Counter = 17 })), NoExternal);
        graph.Nodes[0].Fields![0].Value.Kind = "unsupported";
        var entered = false; var target = new Owner { Counter = -1 };
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareApply(graph, Roots(("root", target)), NoBinding,
            _ => entered = true));
        Assert.IsFalse(entered); Assert.AreEqual(-1, target.Counter);
    }

    [TestMethod]
    public void Import_domain_validation_reads_the_frozen_graph_before_live_writes()
    {
        CanonicalCarryGraphData? input = null;
        var validated = false;
        var codec = new CanonicalCarryGraphCodec(new[] { typeof(Owner), typeof(Pair) }, "domain-test", importValidation: frozen => {
            Assert.AreNotSame(input, frozen);
            var counter = Array.FindIndex(input!.Nodes[0].Fields!, field => field.Name.EndsWith("/Counter", StringComparison.Ordinal));
            input.Nodes[0].Fields![counter].Value.Text = "88";
            validated = true;
        });
        var source = new Owner { Counter = 17 }; var target = new Owner { Counter = 5 };
        input = codec.Capture(Roots(("owner", source)), NoExternal);
        var apply = codec.PrepareApply(input, Roots(("owner", target)), NoBinding);
        Assert.IsTrue(validated); Assert.AreEqual(5, target.Counter);
        apply(); Assert.AreEqual(17, target.Counter);
    }

    [TestMethod]
    public void Rejected_import_domain_does_not_resolve_external_objects_or_write_roots()
    {
        var codec = new CanonicalCarryGraphCodec(new[] { typeof(Owner), typeof(Pair) }, "domain-test",
            importValidation: graph => throw new InvalidDataException("Rejected domain alias."));
        var source = new Owner { Counter = 17, Other = new Unregistered() }; var target = new Owner { Counter = 5 };
        var graph = codec.Capture(Roots(("owner", source)), value => "external/one");
        var resolutions = 0;
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareApply(graph, Roots(("owner", target)), key => {
            resolutions++; return source.Other!;
        }));
        Assert.AreEqual(0, resolutions); Assert.AreEqual(5, target.Counter); Assert.IsNull(target.Other);
    }

    [TestMethod]
    public void Restores_cross_root_cycles_variable_cast_aliases_action_lists_and_structs()
    {
        var first = new Owner(); var second = new Owner(); var shared = new Value { Number = 71 };
        first.Next = second; second.Next = first; first.Current = shared; second.Current = shared;
        first.Values = new[] { shared, shared, new Value { Number = 9, Cast = shared } };
        first.Active = new List<Value> { first.Values[2], shared };
        first.Vector = new Pair { X = -3.5f, Y = 4.75f };
        first.Vectors = new[] { first.Vector, new Pair { X = 6, Y = -8 } };
        var codec = Codec(); var payload = codec.Capture(Roots(("first", first), ("second", second)), NoExternal);
        Assert.AreEqual(0, payload.Unsupported.Length);
        var restoredFirst = new Owner(); var restoredSecond = new Owner();
        var apply = codec.PrepareApply(codec.Decode(codec.Encode(payload)), Roots(("first", restoredFirst), ("second", restoredSecond)), NoBinding);
        Assert.IsNull(restoredFirst.Next);
        apply();
        Assert.AreSame(restoredSecond, restoredFirst.Next); Assert.AreSame(restoredFirst, restoredSecond.Next);
        Assert.AreSame(restoredFirst.Current, restoredSecond.Current);
        Assert.AreSame(restoredFirst.Current, restoredFirst.Values![0]); Assert.AreSame(restoredFirst.Values[0], restoredFirst.Values[1]);
        Assert.AreSame(restoredFirst.Current, restoredFirst.Values[2].Cast);
        Assert.AreSame(restoredFirst.Values[2], restoredFirst.Active![0]); Assert.AreSame(restoredFirst.Current, restoredFirst.Active[1]);
        Assert.AreEqual(71, restoredFirst.Current!.Number); Assert.AreEqual(-3.5f, restoredFirst.Vector.X);
        Assert.AreEqual(-8f, restoredFirst.Vectors![1].Y);
        CollectionAssert.AreEqual(codec.Encode(payload), codec.Encode(codec.Capture(Roots(("first", restoredFirst), ("second", restoredSecond)), NoExternal)));
    }

    [TestMethod]
    public void Detached_allocation_never_calls_game_constructors_getters_or_serialization_callbacks()
    {
        var source = new Owner { Current = new Value { Number = 12 } }; var target = new Owner();
        var codec = Codec(); var payload = codec.Capture(Roots(("owner", source)), NoExternal);
        Value.Constructors = 0; Value.Properties = 0;
        var apply = codec.PrepareApply(payload, Roots(("owner", target)), NoBinding);
        Assert.AreEqual(0, Value.Constructors); Assert.AreEqual(0, Value.Properties); Assert.IsNull(target.Current);
        apply();
        Assert.AreEqual(12, target.Current!.Number); Assert.AreEqual(0, Value.Constructors); Assert.AreEqual(0, Value.Properties);
    }

    [TestMethod]
    public void Keeps_exact_signed_zero_nan_payload_and_hidden_field_identities()
    {
        var source = new ScalarBits { First = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)),
            Second = BitConverter.Int64BitsToDouble(unchecked((long)0x7ff8000000000137)), Decimal = new decimal(100, 0, 0, true, 2) };
        var target = new ScalarBits(); var hidden = new Hidden(); hidden.Set(7, 29); var restored = new Hidden();
        var codec = Codec(); var payload = codec.Capture(Roots(("bits", source), ("hidden", hidden)), NoExternal);
        codec.PrepareApply(payload, Roots(("bits", target), ("hidden", restored)), NoBinding)();
        Assert.AreEqual(unchecked((int)0x80000000), BitConverter.SingleToInt32Bits(target.First));
        Assert.AreEqual(unchecked((long)0x7ff8000000000137), BitConverter.DoubleToInt64Bits(target.Second));
        CollectionAssert.AreEqual(decimal.GetBits(source.Decimal), decimal.GetBits(target.Decimal));
        Assert.AreEqual((7, 29), restored.Read());
    }

    [TestMethod]
    public void Freezes_prepared_input_and_validates_all_fields_before_any_live_write()
    {
        var codec = Codec(); var source = new Owner { Current = new Value { Number = 42 }, Counter = 99 };
        var payload = codec.Capture(Roots(("root", source)), NoExternal); var target = new Owner { Counter = 5 };
        var damaged = codec.Decode(codec.Encode(payload));
        var ownerNode = damaged.Nodes.First(n => n.Type == CanonicalCarryGraphCodec.Identity(typeof(Owner)));
        var field = Array.FindIndex(ownerNode.Fields!, f => f.Name.EndsWith("/Counter", StringComparison.Ordinal));
        ownerNode.Fields![field].Value.Text = "099";
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareApply(damaged, Roots(("root", target)), NoBinding));
        Assert.AreEqual(5, target.Counter); Assert.IsNull(target.Current);
        var apply = codec.PrepareApply(payload, Roots(("root", target)), NoBinding);
        payload.Nodes[0].Fields![field].Value.Text = "123";
        apply(); Assert.AreEqual(99, target.Counter); Assert.AreEqual(42, target.Current!.Number);
    }

    [TestMethod]
    public void Unknown_managed_and_native_types_are_recorded_and_block_apply()
    {
        var codec = Codec(); var source = new Owner { Other = new Unregistered() }; var target = new Owner();
        var unknown = codec.Capture(Roots(("root", source)), NoExternal);
        Assert.IsTrue(unknown.Unsupported.Single().Contains("unregistered-managed-type", StringComparison.Ordinal));
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareApply(unknown, Roots(("root", target)), NoBinding));
        source.Other = new IntPtr(123);
        var native = codec.Capture(Roots(("root", source)), NoExternal);
        Assert.IsTrue(native.Unsupported.Single().Contains("System.IntPtr", StringComparison.Ordinal));
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareApply(native, Roots(("root", target)), NoBinding));
    }

    [TestMethod]
    public void External_owner_rebinding_preserves_aliases_and_rejects_ambiguous_or_collapsed_bindings()
    {
        var external = new Unregistered(); var source = new Owner { Other = external, Other2 = external };
        var codec = Codec(); var payload = codec.Capture(Roots(("root", source)), o => o is Unregistered ? "native-owner" : null);
        Assert.AreEqual(1, payload.Externals.Length); var target = new Owner(); var targetExternal = new Unregistered();
        codec.PrepareApply(payload, Roots(("root", target)), _ => targetExternal)();
        Assert.AreSame(targetExternal, target.Other); Assert.AreSame(target.Other, target.Other2);
        source.Other2 = new Unregistered();
        Assert.ThrowsExactly<InvalidDataException>(() => codec.Capture(Roots(("root", source)), o => o is Unregistered ? "duplicate-owner" : null));
        var separate = codec.Capture(Roots(("root", source)), o => o is Unregistered ? ReferenceEquals(o, external) ? "one" : "two" : null);
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareApply(separate, Roots(("root", target)), _ => targetExternal));
    }

    [TestMethod]
    public void Rejects_schema_field_type_reference_root_alias_and_unreachable_node_tampering()
    {
        var codec = Codec(); var source = new Owner { Current = new Value { Number = 9 } }; var target = new Owner();
        var original = codec.Capture(Roots(("a", source), ("b", source)), NoExternal);
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareApply(original, Roots(("a", target), ("b", new Owner())), NoBinding));
        var p = codec.Decode(codec.Encode(original)); p.Schema = "changed";
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareApply(p, Roots(("a", target), ("b", target)), NoBinding));
        p = codec.Decode(codec.Encode(original)); p.Nodes[0].Type = "payload-controlled:type";
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareApply(p, Roots(("a", target), ("b", target)), NoBinding));
        p = codec.Decode(codec.Encode(original)); p.Nodes[0].Fields![0].Name = "unknown-field";
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareApply(p, Roots(("a", target), ("b", target)), NoBinding));
        p = codec.Decode(codec.Encode(original));
        var current = Array.FindIndex(p.Nodes[0].Fields!, f => f.Name.EndsWith("/Current", StringComparison.Ordinal));
        p.Nodes[0].Fields![current].Value.Reference = int.MaxValue;
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareApply(p, Roots(("a", target), ("b", target)), NoBinding));
        p = codec.Decode(codec.Encode(original)); p.Nodes[0].Fields![current].Value = new CanonicalCarryGraphData.Value { Kind = "null" };
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareApply(p, Roots(("a", target), ("b", target)), NoBinding));
        Assert.IsNull(target.Current);
    }

    [TestMethod]
    public void Rejects_allocating_delegate_pointer_native_object_and_unbounded_array_schemas()
    {
        foreach (var type in new[] { typeof(Action), typeof(IntPtr), typeof(UnityEngine.GameObject), typeof(Value).MakeArrayType(2) })
        {
            if (type.IsArray)
            {
                var codec = new CanonicalCarryGraphCodec(new[] { typeof(Owner), type }, "array-v1");
                Assert.ThrowsExactly<InvalidDataException>(() => codec.Capture(Roots(("root", new Owner { Other = new Value[2, 2] })), NoExternal));
            }
            else Assert.ThrowsExactly<InvalidDataException>(() => new CanonicalCarryGraphCodec(new[] { type }, "blocked-v1"));
        }
    }

    [TestMethod]
    public void Reports_exact_depth_budget_and_never_loosens_graph_or_array_limits()
    {
        var first = new Owner(); var next = first;
        for (var i = 0; i < 130; i++) { next.Next = new Owner(); next = next.Next; }
        var error = Assert.ThrowsExactly<InvalidDataException>(() => Codec().Capture(Roots(("root", first)), NoExternal));
        StringAssert.Contains(error.Message, "depth=129"); StringAssert.Contains(error.Message, "node[129]");
        var array = new Owner[CanonicalCarryGraphCodec.MaximumNodes + 1];
        Assert.ThrowsExactly<InvalidDataException>(() => Codec().Capture(Roots(("root", new Owner { Other = array })), NoExternal));
    }

    [TestMethod]
    public void Plain_structs_are_inline_but_boxed_scalars_and_structs_keep_reference_aliases()
    {
        var codec = Codec(); var target = new Owner(); var source = new Owner();
        foreach (var boxed in new object[] { 17, true, new Pair { X = 2, Y = 4 } })
        {
            source.Other = boxed; source.Other2 = boxed;
            var payload = codec.Capture(Roots(("root", source)), NoExternal);
            codec.PrepareApply(payload, Roots(("root", target)), NoBinding)();
            Assert.AreSame(target.Other, target.Other2); Assert.AreEqual(boxed, target.Other);
        }
        source.Other = 17; source.Other2 = 17;
        var separate = codec.Capture(Roots(("root", source)), NoExternal);
        codec.PrepareApply(separate, Roots(("root", target)), NoBinding)();
        Assert.AreNotSame(target.Other, target.Other2);
        source.Other = null; source.Other2 = null; source.Vectors = new Pair[60000];
        var large = codec.Capture(Roots(("root", source)), NoExternal);
        Assert.AreEqual(2, large.Nodes.Length);
        codec.PrepareApply(large, Roots(("root", target)), NoBinding)();
        Assert.AreEqual(60000, target.Vectors!.Length);
    }

    [TestMethod]
    public void Wire_rejects_truncated_trailing_invalid_counts_and_encoding_before_any_live_write()
    {
        var codec = Codec(); var payload = codec.Encode(codec.Capture(Roots(("root", new Owner { Counter = 19 })), NoExternal));
        foreach (var length in new[] { 0, 3, 11, payload.Length - 1 })
            Assert.ThrowsExactly<InvalidDataException>(() => codec.Decode(payload.Take(length).ToArray()));
        Assert.ThrowsExactly<InvalidDataException>(() => codec.Decode(payload.Concat(new byte[] { 0 }).ToArray()));
        var count = (byte[])payload.Clone(); Array.Copy(BitConverter.GetBytes(int.MaxValue), 0, count, 8, 4);
        Assert.ThrowsExactly<InvalidDataException>(() => codec.Decode(count));
        var invalid = (byte[])payload.Clone(); invalid[16] = 0xff;
        Assert.ThrowsExactly<InvalidDataException>(() => codec.Decode(invalid));
    }

    [TestMethod]
    public void Conditional_native_value_rules_validate_capture_and_every_imported_leaf_before_writing()
    {
        var codec = new CanonicalCarryGraphCodec(new[] { typeof(Owner), typeof(Pair) }, "conditional-native-v1",
            value => value is Pair pair && pair.X != 0 ? "native-id-needs-binding" : null);
        var source = new Owner { Vector = new Pair { X = 9 } }; var target = new Owner { Counter = 7 };
        var invalid = codec.Capture(Roots(("root", source)), NoExternal);
        Assert.AreEqual(1, invalid.Unsupported.Length);
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareApply(invalid, Roots(("root", target)), NoBinding));
        source.Vector.X = 0;
        var payload = codec.Capture(Roots(("root", source)), NoExternal);
        var vector = Array.FindIndex(payload.Nodes[0].Fields!, field => field.Name.EndsWith("/Vector", StringComparison.Ordinal));
        payload.Nodes[0].Fields![vector].Value.Text = "3f800000|00000000";
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareApply(payload, Roots(("root", target)), NoBinding));
        Assert.AreEqual(7, target.Counter);
    }

    [TestMethod]
    public void Local_allocation_rules_preserve_external_singletons_and_reject_tampered_local_fields_before_write()
    {
        var codec = new CanonicalCarryGraphCodec(new[] { typeof(Owner), typeof(Value), typeof(Pair) }, "local-allocation-v1",
            allocationPortability: value => value is Value data && data.Number != 0 ? "must-bind-shared-singleton" : null);
        var shared = new Value { Number = 19 }; var source = new Owner { Other = shared, Current = new Value(), Counter = 9 };
        var payload = codec.Capture(Roots(("root", source)), value => ReferenceEquals(value, shared) ? "singleton" : null);
        Assert.AreEqual(0, payload.Unsupported.Length); Assert.AreEqual(1, payload.Externals.Length);
        var target = new Owner { Counter = 7 };
        codec.PrepareApply(payload, Roots(("root", target)), _ => shared)();
        Assert.AreSame(shared, target.Other); Assert.AreEqual(0, target.Current!.Number);
        var local = payload.Nodes.Single(node => node.Type == CanonicalCarryGraphCodec.Identity(typeof(Value)));
        var number = Array.FindIndex(local.Fields!, field => field.Name.EndsWith("/Number", StringComparison.Ordinal));
        local.Fields![number].Value.Text = "1";
        target.Counter = 7;
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareApply(payload, Roots(("root", target)), _ => shared));
        Assert.AreEqual(7, target.Counter);
        source.Current!.Number = 1;
        Assert.AreEqual(1, codec.Capture(Roots(("root", source)), value => ReferenceEquals(value, shared) ? "singleton" : null).Unsupported.Length);
    }

    private sealed class Owner
    {
        public int Counter;
        public Owner? Next;
        public Value? Current;
        public Value[]? Values;
        public List<Value>? Active;
        public Pair Vector;
        public Pair[]? Vectors;
        public object? Other, Other2;
    }
    private sealed class Value
    {
        public static int Constructors, Properties;
        public int Number;
        public Value? Cast;
        public Value() { Constructors++; }
        public int Dangerous { get { Properties++; throw new InvalidOperationException("Getter must not run."); } }
    }
    private struct Pair { public float X, Y; }
    private sealed class ScalarBits { public float First; public double Second; public decimal Decimal; }
    private class BaseHidden { protected int number; }
    private sealed class Hidden : BaseHidden
    {
        private new int number;
        public void Set(int first, int second) { base.number = first; number = second; }
        public (int, int) Read() => (base.number, number);
    }
    private sealed class Unregistered { }
}
