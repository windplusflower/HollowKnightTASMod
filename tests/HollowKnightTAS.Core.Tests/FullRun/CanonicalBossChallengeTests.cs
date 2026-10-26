using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class CanonicalBossChallengeTests
{
    private readonly Dictionary<string, string?> saved = new();
    [TestInitialize] public void SelectNewProfile() => NativeExecutionProfile.ConfigureReplayEnvironment(NativeExecutionProfile.CanonicalBoundaryProfileId, Write);
    private void Write(string key, string? value)
    { if (!saved.ContainsKey(key)) saved.Add(key, Environment.GetEnvironmentVariable(key)); Environment.SetEnvironmentVariable(key, value); }
    [TestCleanup] public void RestoreEnvironment() { foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
    private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static CanonicalCarryGraphCodec Codec(params Type[] more) => new(CanonicalBossChallengeCarry.DataTypes.Concat(more), "challenge-contract");
    private static CanonicalBossChallengeCarry.Data Source(bool cached)
    {
        var context = new CanonicalBossChallengeContext { Level = 2, Statue = new CanonicalBossStatueContext { StatueStatePD = "normal", DreamStatueStatePD = "dream" } };
        if (cached) context.CachedCompletion = context.Complete;
        return new CanonicalBossChallengeCarry.Data { Pending = context.Setup, AssetManifest = "fixture" };
    }
    private static SortedDictionary<string, object> Roots(object data) => new(StringComparer.Ordinal) { [CanonicalBossChallengeCarry.Root] = data };

    [TestMethod] public void CompleteGraphRoundTripRetainsContextCacheAndStatueAliases()
    {
        foreach (bool cached in new[] { false, true }) {
            var codec = Codec(); var source = Source(cached);
            source.LastSelectedButton = int.MinValue;
            var graph = codec.Capture(Roots(source), _ => null);
            Assert.AreEqual(0, graph.Unsupported.Length);
            var target = new CanonicalBossChallengeCarry.Data();
            CanonicalBossChallengeContext? prepared = null;
            var plan = codec.PrepareGraph(codec.Decode(codec.Encode(graph)), Roots(target), _ => throw new Exception("Unexpected external"), view => {
                prepared = (CanonicalBossChallengeContext)CanonicalBossChallengeCarry.FromPrepared(view).Pending!.Target!;
                Assert.AreEqual(2, prepared.Level);
                Assert.AreEqual("normal", prepared.Statue.StatueStatePD);
                Assert.AreEqual(cached, prepared.CachedCompletion != null);
                if (cached) Assert.AreSame(prepared, prepared.CachedCompletion!.Target);
            });
            Assert.IsNull(target.Pending);
            plan.Apply(); plan.VerifyReadback();
            Assert.AreSame(prepared, target.Pending!.Target);
            Assert.AreEqual(int.MinValue, target.LastSelectedButton);
            Assert.AreNotSame(source.Pending!.Target, target.Pending.Target);
        }
    }
    private sealed class Foreign { public Action? Callback = null; public Action[] Items = Array.Empty<Action>(); }
    [TestMethod] public void ForeignFieldArrayMulticastAndMethodAreNotAdmittedByKnownAlias()
    {
        var source = Source(true); var callback = ((CanonicalBossChallengeContext)source.Pending!.Target!).CachedCompletion!;
        foreach (var foreign in new[] { new Foreign { Callback = callback }, new Foreign { Items = new[] { callback } } }) {
            var roots = Roots(source); roots.Add("foreign", foreign);
            Assert.IsTrue(Codec(typeof(Foreign), typeof(Action[])).Capture(roots, _ => null).Unsupported.Length > 0);
        }
        var context = (CanonicalBossChallengeContext)source.Pending.Target!;
        context.CachedCompletion = GC.Collect;
        Assert.IsTrue(Codec().Capture(Roots(source), _ => null).Unsupported.Length > 0);
        context.CachedCompletion = callback + callback;
        Assert.IsTrue(Codec().Capture(Roots(source), _ => null).Unsupported.Length > 0);
    }
    [TestMethod] public void ForgedCallbackIdentityRejectsBeforeAnyExternalResolution()
    {
        var codec = Codec(); var graph = codec.Capture(Roots(Source(true)), _ => null);
        graph.Nodes.Single(node => node.Kind == "boss-challenge-callback" && node.Text == "complete").Text = "foreign";
        int resolutions = 0;
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareGraph(graph, Roots(new CanonicalBossChallengeCarry.Data()), _ => { resolutions++; throw new Exception(); }));
        Assert.AreEqual(0, resolutions);
    }
    [TestMethod] public void HistoricalV14HasOldPolicyAndNoNewContextOrSnapshotAdmission()
    {
        var codec = Codec(); var graph = codec.Capture(Roots(Source(false)), _ => null);
        NativeExecutionProfile.ConfigureReplayEnvironment(NativeExecutionProfile.RetiredUiCanonicalBoundaryProfileId, Write);
        Assert.AreEqual(NativeExecutionProfile.RetiredUiCanonicalBoundaryProfileId, NativeExecutionProfile.SelectedProfileId);
        Assert.IsTrue(NativeExecutionProfile.UsesCanonicalRetiredUiCallbacks(NativeExecutionProfile.SelectedProfileId));
        Assert.IsFalse(NativeExecutionProfile.UsesCanonicalBossChallengeContext(NativeExecutionProfile.SelectedProfileId));
        Assert.IsFalse(NativeExecutionProfile.SupportsSceneCheckpoints(NativeExecutionProfile.SelectedProfileId));
        Assert.ThrowsExactly<InvalidDataException>(() => Codec().PrepareGraph(graph, Roots(new CanonicalBossChallengeCarry.Data()), _ => throw new Exception()));
    }
    [TestMethod] public void ServiceTypesRemainObservableAndOriginalMetadataIsPinned()
    {
        CanonicalBossChallengeOriginal.Validate();
        foreach (var type in CanonicalBossChallengeCarry.DataTypes)
            Assert.IsFalse(LoadingRuntimeReferencePolicy.IsOpaqueController(type, type.Assembly));
    }
    [TestMethod] public void IncomingWhitelistRejectsForeignOwnersTargetsAndArrayAliases()
    {
#pragma warning disable SYSLIB0050
        object Empty(Type type) => FormatterServices.GetUninitializedObject(type);
#pragma warning restore SYSLIB0050
        var primary = Empty(CanonicalBossChallengeOriginal.Primary);
        var ui = (BossChallengeUI)Empty(typeof(BossChallengeUI)); var statue = (BossStatue)Empty(typeof(BossStatue));
        var pending = (BossSceneController.SetupEventDelegate)Delegate.CreateDelegate(typeof(BossSceneController.SetupEventDelegate), primary,
            primary.GetType().GetMethod("<LoadBoss>b__0", Flags)!);
        var cached = (Action)Delegate.CreateDelegate(typeof(Action), primary, primary.GetType().GetMethod("<LoadBoss>b__1", Flags)!);
        var incoming = (CanonicalBossChallengePolicy)Empty(typeof(CanonicalBossChallengePolicy));
        foreach (var item in new[] { ("original", (object)pending), ("primary", primary), ("ui", (object)ui), ("statue", (object)statue), ("cached", (object)cached) })
            typeof(CanonicalBossChallengePolicy).GetField(item.Item1, Flags)!.SetValue(incoming, item.Item2);
        incoming.BeginWalk(); incoming.ObserveReference(pending, CanonicalBossChallengePolicy.SetupRoot);
        incoming.ObserveSlot(primary, primary.GetType().GetField("<>4__this", Flags), ui);
        incoming.ObserveSlot(ui, typeof(BossChallengeUI).GetField("bossStatue", Flags), statue);
        incoming.ObserveSlot(primary, primary.GetType().GetField("<>9__1", Flags), cached);
        incoming.CompleteWalk();
        foreach (var value in new object[] { pending, primary, ui, statue, cached }) {
            Assert.ThrowsExactly<InvalidDataException>(() => incoming.ObserveReference(value, "unknown-root"));
            Assert.ThrowsExactly<InvalidDataException>(() => incoming.ObserveSlot(new object[] { value }, null, value));
        }
        incoming.BeginWalk(); Assert.ThrowsExactly<InvalidDataException>(() => incoming.CompleteWalk());
    }
}
