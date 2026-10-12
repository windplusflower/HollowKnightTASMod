using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HollowKnightTAS.Core.SceneCheckpoints;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalCarryContinuationContractTests
{
    private static Dictionary<string, object> Source()
    {
        var epoch = CanonicalInputEpoch.Capture(0, 10, 50, 0, 0.25f, 0.125f, 40);
        epoch.BindControlOrigin(CanonicalInputEpoch.Capture(0, 0, 0, 0, 0, 0, 10));
        var native = new byte[1128];
        void Put(int offset, byte[] bytes) => Buffer.BlockCopy(bytes, 0, native, offset, bytes.Length);
        Put(0, BitConverter.GetBytes(0x31435448u)); Put(4, BitConverter.GetBytes(1)); Put(8, BitConverter.GetBytes(1128));
        Put(80, BitConverter.GetBytes(10UL)); Put(88, BitConverter.GetBytes(4)); Put(256, BitConverter.GetBytes(11L));
        var clock = new CanonicalClockCarry { Native = native, Observer = new CheckpointClockObserverState(true, false, 10, 4, 11).Encode(),
            Random = new CheckpointRandomState(new int[12], true, true, 0, 0, 1, 4, "serial-boundary", "Town").Encode() };
        var state = new CheckpointSceneLoadState { TransitionIteratorFinished = true,
            IteratorType = "GameManager+<BeginSceneTransitionRoutine>d__174", ClosureType = "GameManager+<>c__DisplayClass174_0",
            LoadFields = new() { ["SceneLoad/targetSceneName"] = "string:Town" },
            IteratorFields = new() { ["GameManager+<BeginSceneTransitionRoutine>d__174/<>1__state"] = "System.Int32:-1" },
            ClosureFields = new() { ["GameManager+<>c__DisplayClass174_0/lastSceneName"] = "string:Menu_Title" },
            Phases = Enumerable.Range(0, 8).Select(_ => new Dictionary<string, string> { ["SceneLoad+PhaseInfo/BeginTime"] = "nullable:null" }).ToArray() };
        return new() { ["D/InputEpoch"] = epoch, ["D/Clock"] = clock,
            [CanonicalSchedulerCarry.Root] = new CanonicalSchedulerCarry { SourceNative = 10, Timestamp = 40 },
            [CanonicalSceneLoadCarry.Root] = CanonicalSceneLoadCarry.Capture("Menu_Title", "Town", state),
            ["D/SceneLoadInfo"] = new GameManager.SceneLoadInfo { SceneName = "Town" } };
    }
    private static Dictionary<string, object> Target() => new() {
        ["D/InputEpoch"] = new CanonicalInputEpoch { SourceNative = 200 }, ["D/Clock"] = new CanonicalClockCarry(),
        [CanonicalSchedulerCarry.Root] = new CanonicalSchedulerCarry(), [CanonicalSceneLoadCarry.Root] = new CanonicalSceneLoadCarry.Data(),
        ["D/SceneLoadInfo"] = new GameManager.SceneLoadInfo { SceneName = "preparation-target" } };
    private static CanonicalCarryGraphCodec Codec() => new(CanonicalSchedulerCarry.DataTypes.Concat(CanonicalSceneLoadCarry.DataTypes)
        .Concat(new[] { typeof(CanonicalInputEpoch), typeof(CanonicalClockCarry), typeof(byte[]), typeof(string[]),
            typeof(GameManager.SceneLoadInfo), typeof(GameManager.SceneLoadVisualizations), typeof(GlobalEnums.GatePosition) }), "continuation-contract-v1");

    [TestMethod]
    public void Prepared_continuation_reads_saved_fields_while_live_preparation_roots_remain_untouched()
    {
        var codec = Codec(); var source = Source(); var target = Target();
        var graph = codec.Capture(source, _ => null); CanonicalCarryContinuationContract.Prepared? saved = null;
        codec.PrepareApply(graph, target, _ => throw new InvalidDataException(), view => saved = CanonicalCarryContinuationContract.Read(view));
        Assert.AreEqual(10L, saved!.Epoch.SourceNative); Assert.AreEqual(40u, saved.Scheduler.Timestamp);
        Assert.AreEqual("Town", saved.SceneLoad.TargetScene); Assert.AreEqual(1128, saved.Clock.Native.Length);
        Assert.AreEqual(200L, ((CanonicalInputEpoch)target["D/InputEpoch"]).SourceNative);
        Assert.AreEqual("preparation-target", ((GameManager.SceneLoadInfo)target["D/SceneLoadInfo"]).SceneName);
        ((CanonicalClockCarry)source["D/Clock"]).Native[80] ^= 1;
        Assert.AreEqual(10UL, BitConverter.ToUInt64(saved.Clock.Native, 80));
    }
    [TestMethod]
    [DataRow("queue-stamp")]
    [DataRow("queue-native")]
    [DataRow("native-clock")]
    [DataRow("observer-sequence")]
    [DataRow("rng-sequence")]
    [DataRow("input-origin")]
    [DataRow("scene-target")]
    public void Individually_decodable_but_inconsistent_continuation_domains_reject_before_any_live_write(string fault)
    {
        var codec = Codec(); var source = Source(); var target = Target(); var clock = (CanonicalClockCarry)source["D/Clock"];
        if (fault == "queue-stamp") ((CanonicalSchedulerCarry)source[CanonicalSchedulerCarry.Root]).Timestamp++;
        if (fault == "queue-native") ((CanonicalSchedulerCarry)source[CanonicalSchedulerCarry.Root]).SourceNative++;
        if (fault == "native-clock") clock.Native[80]++;
        if (fault == "observer-sequence") clock.Observer = new CheckpointClockObserverState(true, false, 10, 5, 11).Encode();
        if (fault == "rng-sequence") clock.Random = new CheckpointRandomState(new int[12], true, true, 0, 0, 1, 5, "serial-boundary", "Town").Encode();
        if (fault == "input-origin") ((CanonicalInputEpoch)source["D/InputEpoch"]).ControlOriginBound = false;
        if (fault == "scene-target") ((GameManager.SceneLoadInfo)source["D/SceneLoadInfo"]).SceneName = "different-scene";
        var graph = codec.Capture(source, _ => null);
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareApply(graph, target,
            _ => throw new InvalidDataException(), view => CanonicalCarryContinuationContract.Read(view)));
        Assert.AreEqual(200L, ((CanonicalInputEpoch)target["D/InputEpoch"]).SourceNative);
        Assert.AreEqual("preparation-target", ((GameManager.SceneLoadInfo)target["D/SceneLoadInfo"]).SceneName);
    }
}
