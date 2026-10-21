using System;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace HollowKnightTAS.Core.Tests.FullRun;
[TestClass] public sealed class LoadingSharedGraphEvidenceTests
{
    private sealed class Node { public Node? left; public Node? right; public int value; }
    private const string Selected = "scene/root/Map Marker Action[0]|Node[0]";
    private static LoadingFieldDigest New() => new(_ => null, _ => false);
    [TestMethod] public void EvidenceRetainsOriginalDigestSharedAliasesAndCycle()
    {
        var child = new Node { value = 17 }; child.left = child;
        var root = new Node { left = child, right = child };
        using var original = New(); using var observed = New();
        original.BeginSharedGraph(); observed.BeginSharedGraph(true);
        Assert.AreEqual(original.Capture(root, Selected, true).Sha256, observed.Capture(root, Selected, true).Sha256);
        var json = JsonSerializer.Serialize(observed.SharedGraphEvidence);
        StringAssert.Contains(json, "reference"); StringAssert.Contains(json, Selected + ".left"); StringAssert.Contains(json, "ref:1");
        var before = json; observed.CaptureFields(root, "local", "left");
        Assert.AreEqual(before, JsonSerializer.Serialize(observed.SharedGraphEvidence));
        observed.EndSharedGraph(); Assert.IsNull(observed.SharedGraphEvidence);
    }
    [TestMethod] public void DifferentObjectsAreNotMergedEvenWithEqualFields()
    {
        using var digest = New(); digest.BeginSharedGraph(true);
        var a = new Node { value = 17 }; var b = new Node { value = 17 };
        digest.Capture(new Node { left = a, right = b }, Selected, true);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(digest.SharedGraphEvidence));
        var definitions = doc.RootElement.GetProperty("events").EnumerateArray().Where(e => e.GetProperty("kind").GetString() == "define").ToArray();
        Assert.AreEqual(3, definitions.Length); Assert.AreEqual(3, definitions.Select(e => e.GetProperty("originalId").GetInt32()).Distinct().Count());
    }
    [TestMethod] public void EarlierUnrelatedDefinitionChangesNumberButNotTargetProvenance()
    {
        string Capture(bool extra) {
            using var d = New(); d.BeginSharedGraph(true); if(extra) d.Capture(new Node(), "earlier", true);
            var child = new Node(); d.Capture(child, "shared-owner", true); d.Capture(new Node { left = child }, Selected, true);
            return JsonSerializer.Serialize(d.SharedGraphEvidence);
        }
        var a = Capture(false); var b = Capture(true);
        Assert.AreNotEqual(a, b); StringAssert.Contains(a, "shared-owner"); StringAssert.Contains(b, "shared-owner");
    }
    [TestMethod] public void ActualRuntimeSuffixManagedEntryPublishesCompleteTargetEvidence()
    {
        // Load the built production Runtime, not another linked helper copy.
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !File.Exists(Path.Combine(repo.FullName, "CURRENT.md"))) repo = repo.Parent;
        Assert.IsNotNull(repo);
        var assembly = Assembly.LoadFrom(Path.Combine(repo.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
        var oracleType = assembly.GetType("HollowKnightTAS.Runtime.FullRun.LoadingGameplayOracle", true)!;
        var digestType = assembly.GetType("HollowKnightTAS.Runtime.FullRun.LoadingFieldDigest", true)!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var oracle = RuntimeHelpers.GetUninitializedObject(oracleType);
        var digest = Activator.CreateInstance(digestType, flags, null,
            new object?[] { (Func<object,string?>)(_ => null), (Func<Type,bool>)(_ => false), null, null, false, null, null, null, null }, null)!;
        oracleType.GetField("fields", flags)!.SetValue(oracle, digest);
        oracleType.GetField("boundaryCapture", flags)!.SetValue(oracle, false);
        oracleType.GetField("suffixCapture", flags)!.SetValue(oracle, true);
        var capture = digestType.GetMethod("Capture", flags)!;
        var node = new Node { value = 7 }; node.left = node;
        var entry = oracleType.GetMethod("CaptureManagedSampleGraph", flags)!;
        entry.Invoke(oracle, new object[] { (Action)(() => capture.Invoke(digest, new object[] { node, Selected, true, false })) });
        var snapshot = oracleType.GetField("markerSharedGraphEvidence", flags)!.GetValue(oracle);
        Assert.IsNotNull(snapshot);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(snapshot));
        Assert.IsTrue(json.RootElement.GetProperty("complete").GetBoolean());
        Assert.IsTrue(json.RootElement.GetProperty("events").EnumerateArray().Any(e => e.GetProperty("path").GetString()!.StartsWith(Selected, StringComparison.Ordinal)));
        Assert.IsNull(digestType.GetProperty("SharedGraphEvidence", flags)!.GetValue(digest));
        ((IDisposable)digest).Dispose();
    }
    [TestMethod] public void ProductionTransparentModalGuardRejectsVisibleInteractiveAndInvalidStates()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !File.Exists(Path.Combine(repo.FullName, "CURRENT.md"))) repo = repo.Parent;
        Assert.IsNotNull(repo);
        var assembly = Assembly.LoadFrom(Path.Combine(repo.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
        var guard = assembly.GetType("HollowKnightTAS.Runtime.FullRun.CanonicalMenuFlashColdPolicy", true)!
            .GetMethod("RequireTransparentModalState", BindingFlags.Static | BindingFlags.NonPublic)!;
        guard.Invoke(null, new object[] { true, 0f, false, false });
        foreach (var values in new[] { new object[] { false, 0f, false, false }, new object[] { true, 0.01f, false, false },
            new object[] { true, float.NaN, false, false }, new object[] { true, float.PositiveInfinity, false, false },
            new object[] { true, 0f, true, false }, new object[] { true, 0f, false, true } }) {
            var error = Assert.ThrowsExactly<TargetInvocationException>(() => guard.Invoke(null, values));
            Assert.IsInstanceOfType<InvalidOperationException>(error.InnerException);
        }
    }
    [TestMethod] public void ProductionModalStartupAdmissionRejectsPauseConcurrencyAndOutOfScope()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !File.Exists(Path.Combine(repo.FullName, "CURRENT.md"))) repo = repo.Parent;
        Assert.IsNotNull(repo);
        var assembly = Assembly.LoadFrom(Path.Combine(repo.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var policy = assembly.GetType("HollowKnightTAS.Runtime.FullRun.CanonicalMenuFlashColdPolicy", true)!;
        var guard = policy.GetMethod("RequireTransparentModalAdmission", flags)!;
        // Legitimate source/target startup UI/game states differ. Scene and old
        // menu selection are deliberately not inputs of this local original fade.
        foreach (var ui in new[] { 0, 1, 2, 3, 4 }) foreach (var game in new[] { 0, 1, 2, 3, 4, 6, 7, 8 })
            guard.Invoke(null, new object[] { true, false, false, ui, game });
        foreach (var values in new[] { new object[] { false, false, false, 1, 2 }, new object[] { true, true, false, 1, 2 },
            new object[] { true, false, true, 1, 2 }, new object[] { true, false, false, 5, 2 }, new object[] { true, false, false, 6, 2 },
            new object[] { true, false, false, 4, 5 }, new object[] { true, false, false, 99, 2 } })
            Assert.ThrowsExactly<TargetInvocationException>(() => guard.Invoke(null, values));
        var loader = assembly.GetType("HollowKnightTAS.Runtime.FullRun.LoadingSerialSceneProbe", true)!;
        var admission = loader.GetMethod("RequireCanonicalStartupAdmission", flags)!;
        var admitted = loader.GetField("canonicalStartupAdmission", flags)!;
        Assert.ThrowsExactly<TargetInvocationException>(() => admission.Invoke(null, null));
        admitted.SetValue(null, true);
        try { admission.Invoke(null, null); }
        finally { admitted.SetValue(null, false); }
        Assert.ThrowsExactly<TargetInvocationException>(() => admission.Invoke(null, null));
    }
    [TestMethod] public void ExceededBudgetIsExplicitIncomplete()
    {
        var evidence = new LoadingSharedGraphEvidence(); evidence.Reference(0, new string('a', 8*1024*1024+1), "", true);
        var json = JsonSerializer.Serialize(evidence.Snapshot()); StringAssert.Contains(json, "\"complete\":false"); StringAssert.Contains(json, "first-root-budget");
    }
}


