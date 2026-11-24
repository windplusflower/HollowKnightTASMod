using System;
using System.Collections.Generic;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class SerialSceneEventFilterTests
{
    private const string StagingName = "__HKTAS_SerialBoundary_v6";

    private readonly record struct FakeScene(int Handle, string Name, bool IsValid = true);

    private static (SerialSceneEventFilter<FakeScene> Filter, List<string> Events, Func<FakeScene> GetActive, Action<FakeScene> SetActive)
        CreateHarness(int stagingHandle = 999)
    {
        var active = new FakeScene(1, "GG_Workshop");
        var events = new List<string>();
        var filter = new SerialSceneEventFilter<FakeScene>(
            scene => (stagingHandle > 0 && scene.IsValid && scene.Handle == stagingHandle)
                     || string.Equals(scene.Name, StagingName, StringComparison.Ordinal),
            scene => scene.Name);

        void SetActive(FakeScene next)
        {
            var prev = active;
            active = next;
            filter.HandleActiveSceneChanged(
                prev,
                next,
                (p, n) => events.Add($"activeSceneChanged:{p.Name}->{n.Name}(active={active.Name})"));
        }

        return (filter, events, () => active, SetActive);
    }

    [TestMethod]
    public void SourceToStagingIsSuppressedAndStagingToTargetRewritesPrevious()
    {
        var (filter, events, _, setActive) = CreateHarness();
        var staging = new FakeScene(999, StagingName);
        var target = new FakeScene(2, "GG_Radiance");

        setActive(staging);
        Assert.AreEqual(0, events.Count);
        Assert.IsTrue(filter.HasPendingPreviousActiveScene);

        setActive(target);
        Assert.IsFalse(filter.HasPendingPreviousActiveScene);
        CollectionAssert.AreEqual(
            new[] { "activeSceneChanged:GG_Workshop->GG_Radiance(active=GG_Radiance)" },
            events);
    }

    [TestMethod]
    public void StagingToStagingIsSuppressedWithoutOverwritingPendingPrevious()
    {
        var (filter, events, _, setActive) = CreateHarness();
        var stagingByHandle = new FakeScene(999, StagingName);
        var stagingByName = new FakeScene(1000, StagingName);
        var target = new FakeScene(2, "GG_Radiance");

        setActive(stagingByHandle);
        setActive(stagingByName);
        Assert.AreEqual(0, events.Count);
        Assert.IsTrue(filter.HasPendingPreviousActiveScene);

        setActive(target);
        CollectionAssert.AreEqual(
            new[] { "activeSceneChanged:GG_Workshop->GG_Radiance(active=GG_Radiance)" },
            events);
    }

    [TestMethod]
    public void SerialTargetLoadSelectsTargetBeforeSceneLoadedAndDefersActiveSceneChangedUntilAfter()
    {
        var (filter, events, getActive, setActive) = CreateHarness();
        var source = new FakeScene(1, "GG_Workshop");
        var staging = new FakeScene(999, StagingName);
        var target = new FakeScene(2, "GG_Radiance");

        setActive(staging);
        filter.HandleSceneUnloaded(source, s => events.Add($"sceneUnloaded:{s.Name}"));
        // Awake/OnEnable run before Internal_SceneLoaded: active scene must still be staging.
        Assert.AreEqual(StagingName, getActive().Name);

        filter.HandleSceneLoaded(
            target,
            isSingleMode: false,
            serialTargetSceneName: "GG_Radiance",
            activateAndRefreshTarget: scene =>
            {
                events.Add($"before-select(active={getActive().Name})");
                setActive(scene);
                events.Add($"refresh-tilemap:{scene.Name}(active={getActive().Name})");
            },
            invokeOriginal: () => events.Add($"sceneLoaded:{target.Name}(active={getActive().Name})"),
            invokeOriginalActiveSceneChanged: (p, n) =>
                events.Add($"activeSceneChanged:{p.Name}->{n.Name}(active={getActive().Name})"));

        CollectionAssert.AreEqual(
            new[]
            {
                "sceneUnloaded:GG_Workshop",
                "before-select(active=__HKTAS_SerialBoundary_v6)",
                "refresh-tilemap:GG_Radiance(active=GG_Radiance)",
                "sceneLoaded:GG_Radiance(active=GG_Radiance)",
                "activeSceneChanged:GG_Workshop->GG_Radiance(active=GG_Radiance)"
            },
            events);
        Assert.IsFalse(filter.HasPendingPreviousActiveScene);
        Assert.IsFalse(filter.IsDeferringTargetActivationChange);
        Assert.IsFalse(filter.HasDeferredActiveSceneChange);
    }

    [TestMethod]
    public void StagingLoadedAndUnloadedAreAlwaysSuppressed()
    {
        var (filter, events, _, _) = CreateHarness();
        var stagingByHandle = new FakeScene(999, "RenamedStaging");
        var stagingByName = new FakeScene(555, StagingName);

        filter.HandleSceneLoaded(
            stagingByHandle,
            isSingleMode: false,
            serialTargetSceneName: null,
            _ => Assert.Fail("Staging must not trigger target activation."),
            () => events.Add("sceneLoaded-should-not-run"),
            (_, _) => Assert.Fail("Staging must not emit activeSceneChanged."));

        filter.HandleSceneUnloaded(stagingByName, _ => events.Add("sceneUnloaded-should-not-run"));

        Assert.AreEqual(0, events.Count);
    }

    [TestMethod]
    public void SingleLoadModeClearsPendingPreviousActiveScene()
    {
        var (filter, events, getActive, setActive) = CreateHarness();
        var staging = new FakeScene(999, StagingName);
        var menu = new FakeScene(3, "Menu_Title");

        setActive(staging);
        Assert.IsTrue(filter.HasPendingPreviousActiveScene);

        filter.HandleSceneLoaded(
            menu,
            isSingleMode: true,
            serialTargetSceneName: null,
            _ => Assert.Fail("Non-serial single load must not invoke serial activation."),
            () => events.Add($"sceneLoaded:{menu.Name}(active={getActive().Name})"),
            (_, _) => Assert.Fail("Unexpected deferred callback."));

        Assert.IsFalse(filter.HasPendingPreviousActiveScene);
        CollectionAssert.AreEqual(new[] { "sceneLoaded:Menu_Title(active=__HKTAS_SerialBoundary_v6)" }, events);

        // Subsequent staging -> target fallback has no stale GG_Workshop pending.
        setActive(menu);
        CollectionAssert.AreEqual(
            new[]
            {
                "sceneLoaded:Menu_Title(active=__HKTAS_SerialBoundary_v6)",
                "activeSceneChanged:__HKTAS_SerialBoundary_v6->Menu_Title(active=Menu_Title)"
            },
            events);
    }

    [TestMethod]
    public void ExceptionInOrigSceneLoadedStillExitsDeferralAndDispatchesDeferredActiveSceneChanged()
    {
        var (filter, events, getActive, setActive) = CreateHarness();
        var staging = new FakeScene(999, StagingName);
        var target = new FakeScene(2, "GG_Radiance");

        setActive(staging);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            filter.HandleSceneLoaded(
                target,
                isSingleMode: false,
                serialTargetSceneName: "GG_Radiance",
                activateAndRefreshTarget: setActive,
                invokeOriginal: () => throw new InvalidOperationException("listener boom"),
                invokeOriginalActiveSceneChanged: (p, n) =>
                    events.Add($"activeSceneChanged:{p.Name}->{n.Name}(active={getActive().Name})")));

        Assert.IsFalse(filter.IsDeferringTargetActivationChange);
        Assert.IsFalse(filter.HasDeferredActiveSceneChange);
        Assert.IsFalse(filter.HasPendingPreviousActiveScene);
        CollectionAssert.AreEqual(
            new[] { "activeSceneChanged:GG_Workshop->GG_Radiance(active=GG_Radiance)" },
            events);
    }

    [TestMethod]
    public void ResetClearsPendingAndNonMatchingAdditiveScenePassesThrough()
    {
        var (filter, events, _, setActive) = CreateHarness();
        var staging = new FakeScene(999, StagingName);
        var extra = new FakeScene(7, "AdditiveExtra");

        setActive(staging);
        Assert.IsTrue(filter.HasPendingPreviousActiveScene);

        filter.Reset();
        Assert.IsFalse(filter.HasPendingPreviousActiveScene);
        Assert.IsFalse(filter.IsDeferringTargetActivationChange);
        Assert.IsFalse(filter.HasDeferredActiveSceneChange);

        filter.HandleSceneLoaded(
            extra,
            isSingleMode: false,
            serialTargetSceneName: "GG_Radiance",
            _ => Assert.Fail("Unrelated additive scene must not trigger serial activation."),
            () => events.Add("sceneLoaded:AdditiveExtra"),
            (_, _) => Assert.Fail("Unrelated additive scene must not defer activeSceneChanged."));

        CollectionAssert.AreEqual(new[] { "sceneLoaded:AdditiveExtra" }, events);
    }
}
