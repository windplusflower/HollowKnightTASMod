using System;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class LoadingSceneRootIdentityTests
{
    [TestMethod]
    public void OrdinalsSurviveNewProcessHandlesAndDistinguishSameNameRoots()
    {
        var first = new LoadingSceneRootIdentity(); var second = new LoadingSceneRootIdentity();
        first.Register(41, "DontDestroyOnLoad", new[] { 100, 200, 300, 400 });
        second.Register(97, "DontDestroyOnLoad", new[] { -8, -99, -7, -3 });
        for (var i = 0; i < 4; i++) Assert.AreEqual(first.Ordinal(41, (i + 1) * 100),
            second.Ordinal(97, new[] { -8, -99, -7, -3 }[i]));
        Assert.AreNotEqual(first.Ordinal(41, 100), first.Ordinal(41, 200));
    }

    [TestMethod]
    public void NativeRootOrderIsRetainedRatherThanSortedByInstance()
    {
        var roots = new LoadingSceneRootIdentity();
        roots.Register(1, "scene", new[] { 300, 100, 200 });
        Assert.AreEqual(0, roots.Ordinal(1, 300)); Assert.AreEqual(1, roots.Ordinal(1, 100));
        roots.Clear(); roots.Register(1, "scene", new[] { 100, 300, 200 });
        Assert.AreEqual(1, roots.Ordinal(1, 300)); Assert.AreEqual(0, roots.Ordinal(1, 100));
    }

    [TestMethod]
    public void MissingRepeatedAndAmbiguousRootsFailClosed()
    {
        var roots = new LoadingSceneRootIdentity();
        Assert.ThrowsExactly<InvalidOperationException>(() => roots.Register(1, "scene", new[] { 1, 1 }));
        Assert.ThrowsExactly<InvalidOperationException>(() => roots.Register(1, "scene", new[] { 0 }));
        roots.Register(1, "scene", new[] { 8 });
        Assert.ThrowsExactly<InvalidOperationException>(() => roots.Register(2, "scene", new[] { 9 }));
        Assert.ThrowsExactly<InvalidOperationException>(() => roots.Ordinal(1, 9));
        Assert.ThrowsExactly<InvalidOperationException>(() => roots.Ordinal(2, 8));
    }

    [TestMethod]
    public void SceneRebuildDropsOldProcessBindings()
    {
        var roots = new LoadingSceneRootIdentity(); roots.Register(1, "scene", new[] { 8 });
        roots.Clear(); roots.Register(2, "scene", new[] { 9 });
        Assert.IsFalse(roots.Contains(1)); Assert.AreEqual(0, roots.Ordinal(2, 9));
        Assert.ThrowsExactly<InvalidOperationException>(() => roots.Ordinal(1, 8));
    }

    [TestMethod]
    public void AdditionalRootsRetainExistingOrdinalsAndAppendNewOnesDeterministically()
    {
        var roots = new LoadingSceneRootIdentity();
        roots.Register(1, "scene", new[] { 100, 200 });
        Assert.AreEqual(0, roots.Ordinal(1, 100));
        Assert.AreEqual(1, roots.Ordinal(1, 200));
        Assert.IsFalse(roots.TryGetOrdinal(1, 300, out _));

        // Dynamically add roots: 100 and 200 still exist, 300 and 400 are added
        roots.RegisterAdditionalRoots(1, new[] { 100, 200, 300, 400 });
        Assert.IsTrue(roots.TryGetOrdinal(1, 300, out var ord300));
        Assert.AreEqual(2, ord300);
        Assert.AreEqual(0, roots.Ordinal(1, 100));
        Assert.AreEqual(1, roots.Ordinal(1, 200));
        Assert.AreEqual(2, roots.Ordinal(1, 300));
        Assert.AreEqual(3, roots.Ordinal(1, 400));

        // Even if 200 is later destroyed and absent from the new query, its ordinal remains valid
        roots.RegisterAdditionalRoots(1, new[] { 100, 300, 400, 500 });
        Assert.AreEqual(4, roots.Ordinal(1, 500));
        Assert.AreEqual(1, roots.Ordinal(1, 200));
    }

    [TestMethod]
    public void AdditionalRootsFailClosedOnUnknownSceneOrZeroRoot()
    {
        var roots = new LoadingSceneRootIdentity();
        roots.Register(1, "scene", new[] { 10 });
        Assert.ThrowsExactly<InvalidOperationException>(() => roots.RegisterAdditionalRoots(99, new[] { 20 }));
        Assert.ThrowsExactly<InvalidOperationException>(() => roots.RegisterAdditionalRoots(1, new[] { 10, 0 }));
    }
}
