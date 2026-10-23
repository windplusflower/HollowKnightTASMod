using System;
using System.Collections.Generic;
using System.IO;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;
[TestClass]
public sealed class CanonicalActualDRouteTests
{
    private static CanonicalActualDRoute Route() => new() { Ordinal = 2, MovieFrame = 2200, SourceScene = "from", TargetScene = "to",
        ContinuationSha256 = new string('a', 64), LoadFields = new SortedDictionary<string, string>(StringComparer.Ordinal) {
            ["AlwaysUnloadUnusedAssets"]="bool:0", ["EntryDelay"]="f32:00-00-00-00", ["EntryGateName"]="utf16:gate",
            ["HeroLeaveDirection"]="nullable:null", ["IsFirstLevelForPlayer"]="bool:0", ["PreventCameraFadeOut"]="bool:0",
            ["SceneName"]="utf16:to", ["Visualization"]="enum:1", ["WaitForSceneTransitionCameraFade"]="bool:1", ["forceWaitFetch"]="bool:1" } };
    [TestMethod]
    public void DefaultFirstBoundaryAndExplicitSelectionRemainStrict()
    {
        Assert.AreEqual(1, CanonicalActualDRoute.SelectOrdinal(null, false));
        Assert.AreEqual(1, CanonicalActualDRoute.SelectOrdinal("1", true));
        Assert.AreEqual(2, CanonicalActualDRoute.SelectOrdinal("2", true));
        foreach (var text in new[] { "0", "65", "", "two" })
            Assert.Throws<InvalidDataException>(() => CanonicalActualDRoute.SelectOrdinal(text, true));
        Assert.Throws<InvalidDataException>(() => CanonicalActualDRoute.SelectOrdinal("2", false));
    }
    [TestMethod]
    public void EverySourceRouteFieldAndEpochIsBoundBeforeApplication()
    {
        Route().RequireSame(Route(), 2, 2200);
        foreach (var key in Route().LoadFields.Keys) {
            var changed = Route(); changed.LoadFields[key] += "changed";
            Assert.Throws<InvalidDataException>(() => Route().RequireSame(changed, 2, 2200));
        }
        foreach (var mutate in new Action<CanonicalActualDRoute>[] { x => x.Ordinal++, x => x.MovieFrame++, x => x.SourceScene += "x",
            x => x.TargetScene += "x", x => x.ContinuationSha256 = new string('b',64), x => x.LoadFields.Remove("EntryGateName") }) {
            var changed = Route(); mutate(changed);
            Assert.Throws<InvalidDataException>(() => Route().RequireSame(changed, 2, 2200));
        }
        Assert.Throws<InvalidDataException>(() => Route().Validate(1, 2200));
    }
}

