using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalSceneLoadCarryTests
{
    private static CheckpointSceneLoadState State() => new() {
        TransitionIteratorFinished = true, IteratorType = "GameManager+<BeginSceneTransitionRoutine>d__174", ClosureType = "GameManager+<>c__DisplayClass174_0",
        LoadFields = new() { ["SceneLoad/targetSceneName"] = "string:Town", ["SceneLoad/<IsFinished>k__BackingField"] = "System.Boolean:False" },
        IteratorFields = new() { ["GameManager+<BeginSceneTransitionRoutine>d__174/<>1__state"] = "System.Int32:-1" },
        ClosureFields = new() { ["GameManager+<>c__DisplayClass174_0/lastSceneName"] = "string:Menu_Title" },
        Phases = Enumerable.Range(0, 8).Select(_ => new Dictionary<string, string> {
            ["SceneLoad+PhaseInfo/BeginTime"] = "nullable:null", ["SceneLoad+PhaseInfo/EndTime"] = "f32:80000000" }).ToArray() };
    [TestMethod]
    public void Finished_original_iterator_and_all_eight_phase_values_are_carried_without_running_a_loader()
    {
        var state = State(); var data = CanonicalSceneLoadCarry.Capture("Menu_Title", "Town", state);
        state.Phases[7]["SceneLoad+PhaseInfo/EndTime"] = "f32:00000000";
        var decoded = CanonicalSceneLoadCarry.Decode(data);
        Assert.IsTrue(decoded.TransitionIteratorFinished); Assert.AreEqual(8, decoded.Phases.Length);
        Assert.AreEqual("f32:80000000", decoded.Phases[7]["SceneLoad+PhaseInfo/EndTime"]);
        Assert.AreEqual("string:Town", decoded.LoadFields["SceneLoad/targetSceneName"]);
        decoded.ClosureFields.Clear(); Assert.AreEqual(1, data.Packets[2].Fields.Length);
    }
    [TestMethod]
    public void Source_info_and_continuation_agree_in_the_prepared_view_before_target_route_changes()
    {
        var data = CanonicalSceneLoadCarry.Capture("Menu_Title", "Town", State());
        var sourceInfo = new GameManager.SceneLoadInfo { SceneName = "Town" };
        var targetInfo = new GameManager.SceneLoadInfo { SceneName = "old-target" };
        var targetData = new CanonicalSceneLoadCarry.Data();
        var types = CanonicalSceneLoadCarry.DataTypes.Concat(new[] { typeof(string[]), typeof(GameManager.SceneLoadInfo),
            typeof(GameManager.SceneLoadVisualizations), typeof(GlobalEnums.GatePosition) });
        var codec = new CanonicalCarryGraphCodec(types, "loading-source-v1");
        var graph = codec.Capture(new Dictionary<string, object> { [CanonicalSceneLoadCarry.Root] = data, ["D/SceneLoadInfo"] = sourceInfo }, _ => null);
        CanonicalSceneLoadCarry.Data? frozen = null;
        codec.PrepareApply(graph, new Dictionary<string, object> { [CanonicalSceneLoadCarry.Root] = targetData, ["D/SceneLoadInfo"] = targetInfo },
            _ => throw new InvalidDataException(), view => frozen = CanonicalSceneLoadCarry.FromPrepared(view));
        Assert.AreEqual("Town", frozen!.TargetScene); Assert.AreEqual("old-target", targetInfo.SceneName);
        sourceInfo.SceneName = "wrong-target";
        graph = codec.Capture(new Dictionary<string, object> { [CanonicalSceneLoadCarry.Root] = data, ["D/SceneLoadInfo"] = sourceInfo }, _ => null);
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareApply(graph,
            new Dictionary<string, object> { [CanonicalSceneLoadCarry.Root] = targetData, ["D/SceneLoadInfo"] = targetInfo },
            _ => throw new InvalidDataException(), view => CanonicalSceneLoadCarry.FromPrepared(view)));
        Assert.AreEqual("old-target", targetInfo.SceneName);
    }
    [TestMethod]
    [DataRow("target")]
    [DataRow("source")]
    [DataRow("phase")]
    [DataRow("unfinished")]
    [DataRow("duplicate")]
    [DataRow("type")]
    public void Inconsistent_route_or_unclosed_original_loading_state_is_rejected(string fault)
    {
        var data = CanonicalSceneLoadCarry.Capture("Menu_Title", "Town", State());
        if (fault == "target") data.TargetScene = "Crossroads_01";
        if (fault == "source") data.SourceScene = "Crossroads_02";
        if (fault == "phase") data.Packets[10].Group = "phase/0";
        if (fault == "unfinished") data.TransitionIteratorFinished = false;
        if (fault == "duplicate") data.Packets[0].Fields[1] = data.Packets[0].Fields[0];
        if (fault == "type") data.IteratorType = "GameManager+otherIterator";
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalSceneLoadCarry.Decode(data));
    }
}
