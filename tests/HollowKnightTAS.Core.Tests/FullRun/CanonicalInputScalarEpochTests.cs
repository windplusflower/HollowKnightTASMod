using System;
using System.IO;
using System.Linq;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalInputScalarEpochTests
{
    private static CanonicalInputEpoch Epoch() => CanonicalInputEpoch.Capture(0, 10, ulong.MaxValue,
        BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)), 1f, 0.5f, uint.MaxValue);
    private static CanonicalInputManagerScalars.Data Data()
    {
        var layout = CanonicalInputManagerScalars.SchemaIdentity.Substring("CanonicalInputManagerScalars/v1:".Length);
        var fields = layout.Split('\n').Select(line => "InControl.InputManager/" + line.Substring(0, line.IndexOf(':'))).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        var values = fields.Select(_ => "System.Boolean:False").ToArray();
        values[Array.IndexOf(fields, "InControl.InputManager/currentTick")] = "System.UInt64:18446744073709551615";
        values[Array.IndexOf(fields, "InControl.InputManager/initialTime")] = "f32:80000000";
        values[Array.IndexOf(fields, "InControl.InputManager/currentTime")] = "f32:3f800000";
        values[Array.IndexOf(fields, "InControl.InputManager/lastUpdateTime")] = "f32:3f000000";
        return new CanonicalInputManagerScalars.Data { Fields = fields, Values = values };
    }
    [TestMethod]
    public void Exact_uint64_and_float_bits_agree_across_domains_including_negative_zero()
    {
        var data = Data(); CanonicalInputManagerScalars.RequireEpoch(data, Epoch());
        data.Values[Array.IndexOf(data.Fields, "InControl.InputManager/initialTime")] = "f32:00000000";
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalInputManagerScalars.RequireEpoch(data, Epoch()));
    }
    [TestMethod]
    [DataRow("currentTick")]
    [DataRow("currentTime")]
    [DataRow("lastUpdateTime")]
    public void Different_static_clock_data_is_rejected_even_when_both_domains_are_individually_typed(string field)
    {
        var data = Data(); var index = Array.IndexOf(data.Fields, "InControl.InputManager/" + field);
        data.Values[index] = field == "currentTick" ? "System.UInt64:0" : "f32:3f800001";
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalInputManagerScalars.RequireEpoch(data, Epoch()));
    }
    [TestMethod]
    public void Duplicate_and_missing_fields_are_rejected_before_a_static_write_can_be_planned()
    {
        var data = Data(); data.Fields[1] = data.Fields[0];
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalInputManagerScalars.RequireEpoch(data, Epoch()));
        data = Data(); data.Fields = data.Fields.Take(19).ToArray();
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalInputManagerScalars.RequireEpoch(data, Epoch()));
    }
}
