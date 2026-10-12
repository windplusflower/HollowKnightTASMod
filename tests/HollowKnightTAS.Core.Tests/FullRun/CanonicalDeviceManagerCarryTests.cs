using System;
using System.Collections.Generic;
using System.IO;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalDeviceManagerCarryTests
{
    private static CanonicalDeviceManagerCarry.Data Source() => new() { Managers = new[] { new CanonicalDeviceManagerCarry.Manager {
        Order = 0, ModuleMvid = CanonicalSceneMappingCarry.ModuleMvid, Type = "InControl.NativeInputDeviceManager",
        RegisteredDevices = 0, AttachedDevices = 0, DetachedDevices = 0, SystemProfiles = 100, CustomProfiles = 0,
        NativeEvents = new uint[] { 0, 0, uint.MaxValue, 123 } } } };
    [TestMethod]
    public void Zero_devices_preserve_the_actual_native_manager_and_its_full_managed_event_buffer()
    {
        var codec = new CanonicalCarryGraphCodec(CanonicalDeviceManagerCarry.DataTypes, "sdk-manager-v1"); var source = Source();
        var graph = codec.Capture(new Dictionary<string, object> { [CanonicalDeviceManagerCarry.Root] = source }, _ => null);
        var target = new CanonicalDeviceManagerCarry.Data(); CanonicalDeviceManagerCarry.Data? saved = null;
        codec.PrepareApply(graph, new Dictionary<string, object> { [CanonicalDeviceManagerCarry.Root] = target },
            _ => throw new InvalidDataException(), view => saved = CanonicalDeviceManagerCarry.FromPrepared(view));
        Assert.AreEqual(0, target.Managers.Length); Assert.AreEqual(1, saved!.Managers.Length); Assert.AreEqual(0, saved.Managers[0].RegisteredDevices);
        CollectionAssert.AreEqual(new uint[] { 0, 0, uint.MaxValue, 123 }, saved.Managers[0].NativeEvents);
        source.Managers[0].NativeEvents[3] = 999; Assert.AreEqual(123u, saved.Managers[0].NativeEvents[3]);
    }
    [TestMethod]
    [DataRow("type")]
    [DataRow("mvid")]
    [DataRow("order")]
    [DataRow("device")]
    [DataRow("detached")]
    [DataRow("custom-profile")]
    [DataRow("buffer-bound")]
    public void Unregistered_hardware_owner_or_layout_remains_fail_closed(string fault)
    {
        var source = Source(); var item = source.Managers[0];
        if (fault == "type") item.Type = "InControl.UnityInputDeviceManager";
        if (fault == "mvid") item.ModuleMvid = Guid.Empty.ToString("D");
        if (fault == "order") item.Order = 1;
        if (fault == "device") item.RegisteredDevices = 1;
        if (fault == "detached") item.DetachedDevices = 1;
        if (fault == "custom-profile") item.CustomProfiles = 1;
        if (fault == "buffer-bound") item.NativeEvents = new uint[4097];
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalDeviceManagerCarry.Copy(source));
    }
}
