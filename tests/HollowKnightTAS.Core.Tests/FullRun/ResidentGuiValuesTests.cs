using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class ResidentGuiValuesTests
{
    [TestMethod]
    public void DisposedTextGeneratorKeepsManagedIdentityWithoutNativeReads()
    {
        var generator = (UnityEngine.TextGenerator)RuntimeHelpers.GetUninitializedObject(typeof(UnityEngine.TextGenerator));
        GC.SuppressFinalize(generator);
        var values = new ResidentGuiValues();
        var roots = new Dictionary<string, object>();
        var binding = values.Discover(generator, roots.Add);
        Assert.AreEqual("resident-gui/0", binding);
        Assert.AreEqual(binding, values.Discover(generator, (_, _) => Assert.Fail("Alias must not add another root.")));
        Assert.AreSame(generator, values.Resolve(binding!));
        Assert.AreEqual(1, roots.Count);
        Assert.IsTrue(values.Rows[0].Disposed);
        values.Verify();
        ResidentGuiValues.Validate(values.Rows);
    }

    [TestMethod]
    public void MalformedBorrowRelationshipsAreRejectedBeforeNativeConstruction()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => ResidentGuiValues.Prepare(new[] {
            new ResidentGuiValues.Row { Kind = 1, Parent = 0, Slot = 0 } }));
        Assert.ThrowsExactly<InvalidDataException>(() => ResidentGuiValues.Prepare(new[] {
            new ResidentGuiValues.Row { Kind = 0 },
            new ResidentGuiValues.Row { Kind = 3, Parent = 0, Slot = 0 } }));
        Assert.ThrowsExactly<InvalidDataException>(() => ResidentGuiValues.Prepare(new[] {
            new ResidentGuiValues.Row { Kind = 0, Disposed = true } }));
        Assert.ThrowsExactly<InvalidDataException>(() => ResidentGuiValues.Prepare(new[] {
            new ResidentGuiValues.Row { Kind = 0 },
            new ResidentGuiValues.Row { Kind = 2, Parent = 0, Slot = 4 } }));
    }
}
