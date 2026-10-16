using System;
using System.Collections.Generic;
using System.IO;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace HollowKnightTAS.Core.Tests.FullRun;
[TestClass]
public sealed class CanonicalCaptureOwnerDirectoryTests
{
    [TestMethod]
    public void Same_capture_shares_one_sealed_inventory_and_preserves_strong_owner_identity()
    {
        var identity = new object(); var epoch = new object(); var owner = new object(); var calls = 0;
        var raw = new Dictionary<string, object> { ["owner"] = owner };
        var directory = new CanonicalCaptureOwnerDirectory(identity, epoch, () => { calls++; return raw; });
        var main = directory.Require(identity, epoch); var input = directory.Require(identity, epoch);
        Assert.AreEqual(1, calls); Assert.AreSame(main, input); Assert.AreSame(owner, main["owner"]);
        raw["owner"] = new object(); raw.Add("late", new object());
        Assert.AreSame(owner, input["owner"]); Assert.AreEqual(1, input.Count);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IDictionary<string, object>)main).Add("mutated", new object()));
        Assert.ThrowsExactly<InvalidDataException>(() => directory.Require(new object(), epoch));
        Assert.ThrowsExactly<InvalidDataException>(() => directory.Require(identity, new object()));
    }
}
