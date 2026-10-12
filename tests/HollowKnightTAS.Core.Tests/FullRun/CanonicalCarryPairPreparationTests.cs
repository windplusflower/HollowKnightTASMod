using System;
using System.Collections.Generic;
using System.IO;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalCarryPairPreparationTests
{
    private sealed class Owner { public int Value; public Owner? Other; }
    private static CanonicalCarryGraphCodec Codec() => new(new[] { typeof(Owner) }, "pair-v1");
    private static Dictionary<string, object> Roots(string name, Owner owner) => new() { [name] = owner };
    private static object NoExternal(string _) => throw new InvalidDataException();

    [TestMethod]
    public void Both_source_graphs_are_prepared_before_any_write_and_preserve_cross_graph_external_aliases()
    {
        var codec = Codec(); var sourceMain = new Owner { Value = 11 }; var sourceInput = new Owner { Value = 29 };
        sourceMain.Other = sourceInput; sourceInput.Other = sourceMain;
        var main = new Owner(); var input = new Owner();
        var mainData = codec.Capture(Roots("main", sourceMain), value => ReferenceEquals(value, sourceInput) ? "input" : null);
        var inputData = codec.Capture(Roots("input", sourceInput), value => ReferenceEquals(value, sourceMain) ? "main" : null);
        var mainPlan = codec.PrepareGraph(mainData, Roots("main", main), _ => input);
        var inputPlan = codec.PrepareGraph(inputData, Roots("input", input), _ => main);
        var admitted = false; var pair = new CanonicalCarryPairPreparation(mainPlan, inputPlan, () => { }, () => admitted);
        pair.Guard(); Assert.AreEqual(0, main.Value); Assert.AreEqual(0, input.Value); Assert.IsNull(main.Other);
        admitted = true; pair.Apply(); Assert.AreEqual(11, main.Value); Assert.AreEqual(29, input.Value);
        Assert.AreSame(input, main.Other); Assert.AreSame(main, input.Other);
        main.Value = 77; Assert.ThrowsExactly<InvalidOperationException>(() => pair.Apply()); Assert.AreEqual(77, main.Value);
    }
    [TestMethod]
    public void A_changed_second_graph_blocks_the_first_graph_before_any_write()
    {
        var codec = Codec(); var main = new Owner(); var input = new Owner();
        var first = codec.PrepareGraph(codec.Capture(Roots("main", new Owner { Value = 11 }), _ => null), Roots("main", main), NoExternal);
        var second = codec.PrepareGraph(codec.Capture(Roots("input", new Owner { Value = 29 }), _ => null), Roots("input", input), NoExternal);
        var pair = new CanonicalCarryPairPreparation(first, second, () => { }, () => true);
        input.Value = 99; Assert.ThrowsExactly<InvalidOperationException>(() => pair.Apply());
        Assert.AreEqual(0, main.Value); Assert.AreEqual(99, input.Value);
        input.Value = 0; Assert.ThrowsExactly<InvalidOperationException>(() => pair.Apply()); Assert.AreEqual(0, main.Value);
    }
    [TestMethod]
    public void Two_graphs_cannot_plan_conflicting_writes_to_the_same_root_owner()
    {
        var codec = Codec(); var owner = new Owner();
        var first = codec.PrepareGraph(codec.Capture(Roots("main", new Owner { Value = 11 }), _ => null), Roots("main", owner), NoExternal);
        var second = codec.PrepareGraph(codec.Capture(Roots("input", new Owner { Value = 29 }), _ => null), Roots("input", owner), NoExternal);
        Assert.ThrowsExactly<ArgumentException>(() => new CanonicalCarryPairPreparation(first, second, () => { }, () => true));
        Assert.AreEqual(0, owner.Value);
    }
    [TestMethod]
    public void Rejected_admission_never_runs_a_writer_and_is_terminal_for_that_pair()
    {
        var writes = 0; var admitted = false;
        var first = new CanonicalPreparedWritePlan(() => { }, () => writes++, () => { });
        var second = new CanonicalPreparedWritePlan(() => { }, () => writes++, () => { });
        var pair = new CanonicalCarryPairPreparation(first, second, () => { }, () => admitted);
        Assert.ThrowsExactly<InvalidOperationException>(() => pair.Apply()); Assert.AreEqual(0, writes);
        admitted = true; Assert.ThrowsExactly<InvalidOperationException>(() => pair.Apply()); Assert.AreEqual(0, writes);
    }
    [TestMethod]
    public void Partial_write_failure_is_terminal_and_does_not_reenter_a_completed_graph()
    {
        var writes = 0;
        var first = new CanonicalPreparedWritePlan(() => { }, () => writes++, () => { });
        var second = new CanonicalPreparedWritePlan(() => { }, () => { writes++; throw new IOException("Injected write failure."); }, () => { });
        var pair = new CanonicalCarryPairPreparation(first, second, () => { }, () => true);
        Assert.ThrowsExactly<IOException>(() => pair.Apply()); Assert.AreEqual(2, writes);
        Assert.ThrowsExactly<InvalidOperationException>(() => pair.Apply()); Assert.AreEqual(2, writes);
    }
    [TestMethod]
    public void Full_readback_detects_a_second_writer_corrupting_the_completed_first_domain()
    {
        var state = 0;
        var first = new CanonicalPreparedWritePlan(() => { }, () => state = 11,
            () => { if (state != 11) throw new InvalidOperationException("Main domain readback differs."); });
        var second = new CanonicalPreparedWritePlan(() => { }, () => state = 29, () => { });
        var pair = new CanonicalCarryPairPreparation(first, second, () => { }, () => true);
        Assert.ThrowsExactly<InvalidOperationException>(() => pair.Apply()); Assert.AreEqual(29, state);
        Assert.ThrowsExactly<InvalidOperationException>(() => pair.Apply());
    }
    [TestMethod]
    public void Losing_the_paused_boundary_after_the_first_write_prevents_the_second_write()
    {
        var admitted = true; var secondWrites = 0;
        var first = new CanonicalPreparedWritePlan(() => { }, () => admitted = false, () => { });
        var second = new CanonicalPreparedWritePlan(() => { }, () => secondWrites++, () => { });
        var pair = new CanonicalCarryPairPreparation(first, second, () => { }, () => admitted);
        Assert.ThrowsExactly<InvalidOperationException>(() => pair.Apply()); Assert.AreEqual(0, secondWrites);
    }
    [TestMethod]
    public void A_reentrant_writer_guard_is_rejected_before_the_first_write()
    {
        CanonicalPreparedWritePlan? plan = null; var writes = 0;
        plan = new CanonicalPreparedWritePlan(() => plan!.Apply(), () => writes++, () => { });
        Assert.ThrowsExactly<InvalidOperationException>(() => plan.Apply()); Assert.AreEqual(0, writes);
        Assert.ThrowsExactly<InvalidOperationException>(() => plan.Apply());
        Assert.ThrowsExactly<InvalidOperationException>(() => plan.VerifyReadback());
    }
}
