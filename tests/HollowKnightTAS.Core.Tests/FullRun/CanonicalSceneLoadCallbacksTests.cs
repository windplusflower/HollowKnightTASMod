using System;
using System.Collections.Generic;
using System.IO;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalSceneLoadCallbacksTests
{
    [TestMethod]
    public void Only_exact_factory_registered_diagnostic_suffixes_are_retained_without_invoking_callbacks()
    {
        Action original = () => throw new InvalidOperationException("Must not run game callback.");
        Action diagnostic = () => throw new InvalidOperationException("Must not run diagnostic callback.");
        Action other = () => throw new InvalidOperationException("Unknown callback.");
        CanonicalSceneLoadCallbacks.RequireDiagnosticSuffix(new Delegate[] { original, diagnostic }, new Delegate[] { diagnostic }, 1);
        CanonicalSceneLoadCallbacks.RequireDiagnosticSuffix(new Delegate[] { diagnostic }, new Delegate[] { diagnostic }, 0);
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalSceneLoadCallbacks.RequireDiagnosticSuffix(
            new Delegate[] { diagnostic, original }, new Delegate[] { diagnostic }, 1));
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalSceneLoadCallbacks.RequireDiagnosticSuffix(
            new Delegate[] { original, other }, new Delegate[] { diagnostic }, 1));
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalSceneLoadCallbacks.RequireDiagnosticSuffix(
            new Delegate[] { original, diagnostic, other }, new Delegate[] { diagnostic }, 1));
    }
    private static CanonicalSceneLoadCallbacks.Slot Method(string field, int token, string name) => new() {
        Field = field, Token = token, Method = name, SharedClosure = 0,
        ModuleMvid = "2b54cdaf-7a93-4f0e-b297-d50c4090ea9f", DeclaringType = "GameManager+<>c__DisplayClass174_0" };
    private static CanonicalSceneLoadCallbacks.Data Source() => new() { SourceScene = "Town", Slots = new[] {
        Method("ActivationComplete", 100667494, "<BeginSceneTransitionRoutine>b__2"),
        new CanonicalSceneLoadCallbacks.Slot { Field = "BossLoaded" },
        Method("Complete", 100667495, "<BeginSceneTransitionRoutine>b__3"),
        Method("FetchComplete", 100667492, "<BeginSceneTransitionRoutine>b__0"),
        Method("Finish", 100667496, "<BeginSceneTransitionRoutine>b__4"),
        new CanonicalSceneLoadCallbacks.Slot { Field = "StartCalled" },
        Method("WillActivate", 100667493, "<BeginSceneTransitionRoutine>b__1") } };
    [TestMethod]
    public void Prepared_source_callbacks_keep_the_original_shared_closure_contract_without_writing_the_target()
    {
        var codec = new CanonicalCarryGraphCodec(CanonicalSceneLoadCallbacks.DataTypes, "loader-callbacks-v1");
        var source = Source(); var graph = codec.Capture(new Dictionary<string, object> { [CanonicalSceneLoadCallbacks.Root] = source }, _ => null);
        var target = new CanonicalSceneLoadCallbacks.Data(); CanonicalSceneLoadCallbacks.Data? saved = null;
        codec.PrepareApply(graph, new Dictionary<string, object> { [CanonicalSceneLoadCallbacks.Root] = target },
            _ => throw new InvalidDataException(), view => saved = CanonicalSceneLoadCallbacks.FromPrepared(view));
        Assert.AreEqual(0, target.Slots.Length); Assert.AreEqual("", target.SourceScene);
        Assert.AreEqual("Town", saved!.SourceScene); Assert.AreEqual(100667494, saved.Slots[0].Token);
        Assert.AreEqual(0, saved.Slots[6].SharedClosure); Assert.AreEqual(-1, saved.Slots[1].SharedClosure);
        source.Slots[0].Token = 999; Assert.AreEqual(100667494, saved.Slots[0].Token);
    }
    [TestMethod]
    [DataRow("method")]
    [DataRow("token")]
    [DataRow("closure-type")]
    [DataRow("mvid")]
    [DataRow("target-alias")]
    [DataRow("extra-callback")]
    [DataRow("slot-order")]
    [DataRow("source")]
    public void Unknown_or_reordered_scene_load_callbacks_remain_rejected(string fault)
    {
        var source = Source();
        if (fault == "method") source.Slots[0].Method = "<Other>b__0";
        if (fault == "token") source.Slots[0].Token++;
        if (fault == "closure-type") source.Slots[0].DeclaringType = "GameManager";
        if (fault == "mvid") source.Slots[0].ModuleMvid = Guid.Empty.ToString("D");
        if (fault == "target-alias") source.Slots[0].SharedClosure = 1;
        if (fault == "extra-callback") source.Slots[1].Token = 123;
        if (fault == "slot-order") (source.Slots[0], source.Slots[2]) = (source.Slots[2], source.Slots[0]);
        if (fault == "source") source.SourceScene = "";
        if (fault == "token")
        {
            var copy = CanonicalSceneLoadCallbacks.Copy(source);
            Assert.AreEqual(source.Slots[0].Token, copy.Slots[0].Token);
            return;
        }
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalSceneLoadCallbacks.Copy(source));
    }
}
