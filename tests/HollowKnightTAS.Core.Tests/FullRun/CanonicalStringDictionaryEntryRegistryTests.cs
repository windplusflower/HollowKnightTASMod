using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalStringDictionaryEntryRegistryTests
{
    [TestMethod]
    public void Original_string_scalar_dictionary_entries_have_a_complete_layout_for_boxed_array_elements()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("canonical-main-fields.json")!;
        using var document = JsonDocument.Parse(stream);
        const string name = "System.Collections.Generic.Dictionary`2+Entry[[System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089],[System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]";
        var type = document.RootElement.GetProperty("types").EnumerateArray().Single(value => value.GetProperty("name").GetString() == name);
        var fields = type.GetProperty("fields").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(new[] { "hashCode", "key", "next", "value" }, fields.Select(field => field.GetProperty("name").GetString()).ToArray());
        CollectionAssert.AreEqual(new[] { "System.Int32", "System.String", "System.Int32", "System.String" }, fields.Select(field => field.GetProperty("type").GetString()).ToArray());
        Assert.IsTrue(fields.All(field => field.GetProperty("owner").GetString() == name));
    }
}
