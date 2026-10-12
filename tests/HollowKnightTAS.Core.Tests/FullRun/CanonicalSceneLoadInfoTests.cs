using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using GlobalEnums;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalSceneLoadInfoTests
{
    private static GameManager.SceneLoadInfo Empty()
    {
#pragma warning disable SYSLIB0050
        return (GameManager.SceneLoadInfo)FormatterServices.GetUninitializedObject(typeof(GameManager.SceneLoadInfo));
#pragma warning restore SYSLIB0050
    }
    private static CanonicalCarryGraphCodec Codec() => new(new[] { typeof(GameManager.SceneLoadInfo), typeof(GatePosition), typeof(GameManager.SceneLoadVisualizations) }, "load-info-test-v1");
    [TestMethod]
    public void Production_frozen_info_registry_matches_original_metadata_in_ordinal_field_order()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("canonical-scene-loading-fields.json")!;
        using var reader = new StreamReader(stream); var data = JObject.Parse(reader.ReadToEnd());
        Assert.AreEqual(typeof(GameManager).Module.ModuleVersionId.ToString("D"), (string)data["moduleMvid"]!);
        var entry = data["types"]!.Single(type => (string)type["name"]! == typeof(GameManager.SceneLoadInfo).FullName);
        var expected = entry["fields"]!.Select(field => (string)field["owner"]! + "/" + (string)field["name"]! + ":" + (string)field["type"]!).ToArray();
        var actual = CanonicalCarryGraphCodec.Fields(typeof(GameManager.SceneLoadInfo)).Select(field =>
            field.DeclaringType!.FullName + "/" + field.Name + ":" + field.FieldType.FullName).ToArray();
        CollectionAssert.AreEqual(actual, expected);
    }
    [TestMethod]
    public void Nullable_door_direction_and_all_entry_fields_restore_into_the_existing_info_object()
    {
        var source = Empty(); source.SceneName = "Town"; source.EntryGateName = "left1"; source.HeroLeaveDirection = GatePosition.right;
        source.EntryDelay = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
        source.AlwaysUnloadUnusedAssets = true; source.forceWaitFetch = true; source.PreventCameraFadeOut = true;
        source.WaitForSceneTransitionCameraFade = true; source.IsFirstLevelForPlayer = false;
        var target = Empty(); target.SceneName = "old-target"; var retainedClosureReference = target;
        var codec = Codec(); var graph = codec.Capture(new Dictionary<string, object> { ["D/SceneLoadInfo"] = source }, _ => null);
        Assert.AreEqual(0, graph.Unsupported.Length);
        var apply = codec.PrepareApply(graph, new Dictionary<string, object> { ["D/SceneLoadInfo"] = target }, _ => throw new InvalidDataException());
        source.SceneName = "changed-source-after-freeze"; Assert.AreEqual("old-target", target.SceneName);
        apply(); Assert.AreSame(target, retainedClosureReference); Assert.AreEqual("Town", retainedClosureReference.SceneName);
        Assert.AreEqual("left1", target.EntryGateName); Assert.AreEqual(GatePosition.right, target.HeroLeaveDirection);
        Assert.AreEqual(unchecked((int)0x80000000), BitConverter.SingleToInt32Bits(target.EntryDelay));
        Assert.IsTrue(target.AlwaysUnloadUnusedAssets && target.forceWaitFetch && target.PreventCameraFadeOut && target.WaitForSceneTransitionCameraFade);
        Assert.IsFalse(target.IsFirstLevelForPlayer);
    }
    [TestMethod]
    public void First_level_null_direction_is_preserved_without_a_default_door_being_invented()
    {
        var source = Empty(); source.IsFirstLevelForPlayer = true; source.SceneName = "Town";
        var target = Empty(); target.HeroLeaveDirection = GatePosition.left;
        var codec = Codec(); var graph = codec.Capture(new Dictionary<string, object> { ["D/SceneLoadInfo"] = source }, _ => null);
        codec.PrepareApply(graph, new Dictionary<string, object> { ["D/SceneLoadInfo"] = target }, _ => throw new InvalidDataException())();
        Assert.IsNull(target.HeroLeaveDirection); Assert.IsTrue(target.IsFirstLevelForPlayer); Assert.IsNull(target.EntryGateName);
    }
}
