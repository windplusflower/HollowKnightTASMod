using System.Collections.Generic;
using System.IO;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalCarryRootBindingsTests
{
    [TestMethod]
    public void Shared_named_slot_and_multiple_aliases_keep_the_original_object()
    {
        var owner = new object();
        var result = CanonicalCarryRootBindings.Merge(new Dictionary<string, object> { ["keyboard"] = owner },
            new Dictionary<string, object> { ["keyboard"] = owner, ["other-consumer"] = owner });
        Assert.AreEqual(2, result.Count); Assert.AreSame(owner, result["keyboard"]); Assert.AreSame(owner, result["other-consumer"]);
    }
    [TestMethod]
    public void Equal_looking_values_cannot_alias_the_same_slot()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalCarryRootBindings.Merge(
            new Dictionary<string, object> { ["slot"] = new object() }, new Dictionary<string, object> { ["slot"] = new object() }));
    }
    [TestMethod]
    public void Absent_identity_and_object_are_rejected()
    {
        foreach (var key in new[] { "", "bad\0key" })
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalCarryRootBindings.Merge(new Dictionary<string, object> { [key] = new object() }));
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalCarryRootBindings.Merge(new Dictionary<string, object> { ["slot"] = null! }));
    }
}
