using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalFinalVisualVisibilityTests
{
    [TestMethod]
    public void VisibilityCarryAllowsOnlyCanonicalNativeFieldsAndKeepsEveryOtherWitnessExact()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !File.Exists(Path.Combine(repo.FullName, "CURRENT.md"))) repo = repo.Parent;
        Assert.IsNotNull(repo);
        var assembly = Assembly.LoadFrom(Path.Combine(repo.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
        var type = assembly.GetType("HollowKnightTAS.Runtime.FullRun.CanonicalFinalVisualCarry", true)!;
        var validate = type.GetMethod("RequireCompatibleVisibility", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool[] Check(string source, string current, bool mainText = true)
            => (bool[])validate.Invoke(null, new object[] { source, current, mainText })!;
        void Reject(string source, string current, bool mainText = true) {
            var failure = Assert.ThrowsExactly<TargetInvocationException>(() => Check(source, current, mainText));
            Assert.IsInstanceOfType<InvalidDataException>(failure.InnerException);
        }
        // The final segment deliberately contains ':' inside its component
        // identity. It must remain one exact witness, never another bool mask.
        var fields = new[] { "True", "True", "False", "True", "1,1,1,0,0",
            "0,0,0,0,0,0,1065353216,1065353216,1065353216,1065353216", "live-mesh",
            "Assembly-CSharp:TMPro.TextMeshPro=True/1,1,1,0,0;Assembly-CSharp:LinkRendererState=True/1,1,1,0,0" };
        string Text(string[] parts) => string.Join(":", parts);
        var shown = Text(fields); var hiddenFields = (string[])fields.Clone();
        hiddenFields[1] = "False"; hiddenFields[3] = "False"; var hidden = Text(hiddenFields);
        CollectionAssert.AreEqual(new[] { false, false }, Check(hidden, shown));
        CollectionAssert.AreEqual(new[] { true, true }, Check(shown, hidden));
        Reject(hidden, shown, mainText: false);
        var rendererOnly = (string[])fields.Clone(); rendererOnly[3] = "False";
        CollectionAssert.AreEqual(new[] { true, false }, Check(Text(rendererOnly), shown, mainText: false));

        foreach (var index in new[] { 0, 2, 4, 5, 6, 7 }) {
            var altered = (string[])hiddenFields.Clone(); altered[index] += "!";
            Reject(Text(altered), shown);
        }
        foreach (var index in new[] { 1, 3 }) foreach (var invalid in new[] { "true", "0", "False ", "" }) {
            var altered = (string[])hiddenFields.Clone(); altered[index] = invalid;
            Reject(Text(altered), shown);
        }
        Reject(string.Join(":", hiddenFields.Take(7)), shown); // Old witness lacks full components.
        Reject(hidden + ":extra", shown);
        Reject(hidden, shown + ":extra");
        Reject(new string('x', 16385), shown);
    }
}
