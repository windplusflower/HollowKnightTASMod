using System;
using System.Collections.Generic;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalBoundaryEpochObservationTests
{
    private static CanonicalInputEpoch Epoch(long origin, uint stamp)
    {
        var start = CanonicalInputEpoch.Capture(origin,origin,1,0,0,0,stamp);
        var result = CanonicalInputEpoch.Capture(origin,origin+20,21,0,0,0,unchecked(stamp+80));
        result.BindControlOrigin(start); return result;
    }
    private static BoundaryDigestSnapshot Digest(CanonicalInputEpoch epoch, string field="unchanged") => new(
        new SortedDictionary<string,string> { ["nativeFrame"] = epoch.SourceNative.ToString(), ["controlledNativeFrame"] = "20", ["gameplayRng"] = "original" },
        new object[] { new { id="Hero", digest=field } }, new[] { "native-coverage-open" },
        new object[] { new { kind="manager", timestamp=epoch.SchedulerTimestamp, callbackCount=0 } },
        Array.Empty<object>(), Array.Empty<string>());
    [TestMethod]
    public void EpochCoordinatesChangeWithoutDroppingStateOrRawProof()
    {
        var a = Epoch(3,20); var b = Epoch(100,unchecked(uint.MaxValue-30));
        var rawA = Digest(a); var rawB = Digest(b); var hash = rawA.FullSha256;
        Assert.IsFalse(BoundaryDigestComparison.Compare(rawA,rawB).ObservedEqual);
        var projectedA = CanonicalBoundaryEpochObservation.Project(rawA,a);
        var projectedB = CanonicalBoundaryEpochObservation.Project(rawB,b);
        Assert.IsTrue(BoundaryDigestComparison.Compare(projectedA,projectedB).ObservedEqual);
        Assert.AreEqual(hash, rawA.RecomputeSha256());
        Assert.IsFalse(projectedA.CompleteStateCoverage);
        Assert.IsFalse(BoundaryDigestComparison.Compare(projectedA,
            CanonicalBoundaryEpochObservation.Project(Digest(b,"changed"),b)).ObservedEqual);
    }
    [TestMethod]
    public void MissingOrIncorrectStartOriginRejects()
    {
        var epoch = Epoch(3,20); var raw = Digest(epoch); var wrong = epoch.Copy();
        wrong.ControlOriginBound = false;
        Assert.ThrowsExactly<System.IO.InvalidDataException>(() => CanonicalBoundaryEpochObservation.Project(raw,wrong));
        wrong = epoch.Copy(); wrong.OriginNative++;
        Assert.ThrowsExactly<System.IO.InvalidDataException>(() => CanonicalBoundaryEpochObservation.Project(raw,wrong));
        wrong = epoch.Copy(); wrong.SchedulerTimestamp++;
        Assert.ThrowsExactly<System.IO.InvalidDataException>(() => CanonicalBoundaryEpochObservation.Project(raw,wrong));
    }
}
