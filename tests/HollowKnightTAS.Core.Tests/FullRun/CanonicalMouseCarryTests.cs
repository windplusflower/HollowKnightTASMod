using System;
using System.Collections.Generic;
using System.IO;
using HollowKnightTAS.Runtime.FullRun;
using HollowKnightTAS.Runtime.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalMouseCarryTests
{
    private static CheckpointMouseState Source() => new() { Enabled = true, Suspended = false, Width = 1280, Height = 720,
        PreviousInControlButtons = 1, PreviousHkButtons = 2, PreviousHkX = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)), PreviousHkY = 0.25f,
        InControl = new() { X = 100, Y = 200, DeltaX = 3, DeltaY = 4, Wheel = 5, Buttons = 1 },
        Hk = new() { X = 300, Y = 400, DeltaX = 6, DeltaY = 7, Wheel = 8, Buttons = 2 }, Modules = Array.Empty<CheckpointMouseModule>() };
    [TestMethod]
    public void Both_consumers_and_previous_edges_come_from_saved_fields_without_clearing_target_caches()
    {
        var codec = new CanonicalCarryGraphCodec(CanonicalMouseCarry.DataTypes, "mouse-v1"); var source = Source();
        var graph = codec.Capture(new Dictionary<string, object> { [CanonicalMouseCarry.Root] = source }, _ => null);
        var target = new CheckpointMouseState { Width = 1 }; CheckpointMouseState? saved = null;
        codec.PrepareApply(graph, new Dictionary<string, object> { [CanonicalMouseCarry.Root] = target },
            _ => throw new InvalidDataException(), view => saved = CanonicalMouseCarry.FromPrepared(view));
        Assert.AreEqual(1, target.Width); Assert.AreEqual(1280, saved!.Width);
        Assert.AreEqual(1u, saved.PreviousInControlButtons); Assert.AreEqual(2u, saved.PreviousHkButtons);
        Assert.AreEqual(100, saved.InControl.X); Assert.AreEqual(300, saved.Hk.X);
        Assert.AreEqual(unchecked((int)0x80000000), BitConverter.SingleToInt32Bits(saved.PreviousHkX));
        source.Hk.X = 999; Assert.AreEqual(300, saved.Hk.X);
    }
    [TestMethod]
    public void Module_field_packets_are_frozen_before_a_caller_can_mutate_them()
    {
        var source = Source(); source.Modules = new[] { new CheckpointMouseModule { Identity = "DDOL/UI", Fields = new() { ["allow"] = "System.Boolean:False" } } };
        var saved = CanonicalMouseCarry.Copy(source); source.Modules[0].Fields["allow"] = "System.Boolean:True";
        Assert.AreEqual("System.Boolean:False", saved.Modules[0].Fields["allow"]);
    }
    [TestMethod]
    [DataRow("viewport")]
    [DataRow("old-buttons")]
    [DataRow("buttons")]
    [DataRow("position")]
    [DataRow("module-alias")]
    [DataRow("module-null")]
    public void Invalid_mouse_or_consumer_state_is_rejected_before_a_bridge_application(string fault)
    {
        var source = Source();
        if (fault == "viewport") source.Width = 0;
        if (fault == "old-buttons") source.PreviousHkButtons = 512;
        if (fault == "buttons") source.InControl.Buttons = 512;
        if (fault == "position") source.PreviousHkX = float.NaN;
        if (fault == "module-alias") source.Modules = new[] { new CheckpointMouseModule { Identity = "same", Fields = new() }, new CheckpointMouseModule { Identity = "same", Fields = new() } };
        if (fault == "module-null") source.Modules = new CheckpointMouseModule[] { null! };
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalMouseCarry.Copy(source));
    }
}
