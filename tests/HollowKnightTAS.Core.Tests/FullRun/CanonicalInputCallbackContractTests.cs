using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalInputCallbackContractTests
{
    private static CanonicalInputCallbackContract.Data Data() => new() { Callbacks = CanonicalInputCallbackContract.Slots.Select(name =>
        new CanonicalInputCallbackContract.Slot { Field = name, Methods = name == "OnDeviceAttached" ? new[] { Method(1), Method(2) }
            : Array.Empty<CanonicalInputCallbackContract.Method>() }).ToArray() };
    private static CanonicalInputCallbackContract.Method Method(int ordinal) => new() {
        Assembly = "Assembly-CSharp", ModuleMvid = "2b54cdaf-7a93-4f0e-b297-d50c4090ea9f", Token = 0x06000001 + ordinal,
        DeclaringType = "InputHandler", Name = "OnAttach", TargetType = "Assembly-CSharp:InputHandler",
        TargetBinding = "R6/InputCallbackTarget/" + ordinal, IsStatic = false };
    [TestMethod]
    public void Proposed_source_descriptors_are_checked_before_target_callback_data_is_written()
    {
        var source = Data(); var target = Data(); target.Callbacks[3].Methods[0].Token++;
        var codec = new CanonicalCarryGraphCodec(CanonicalInputCallbackContract.DataTypes, "callback-v1");
        var graph = codec.Capture(new Dictionary<string, object> { [CanonicalInputCallbackContract.Root] = source }, _ => null);
        var accepted = false;
        codec.PrepareApply(graph, new Dictionary<string, object> { [CanonicalInputCallbackContract.Root] = target },
            _ => throw new InvalidDataException(), view => {
                var prepared = CanonicalInputCallbackContract.FromPrepared(view);
                CanonicalInputCallbackContract.RequireSame(source, prepared);
                Assert.ThrowsExactly<InvalidDataException>(() => CanonicalInputCallbackContract.RequireSame(target, prepared));
                accepted = true;
            });
        Assert.IsTrue(accepted); Assert.AreEqual(0x06000003, target.Callbacks[3].Methods[0].Token);
    }
    [TestMethod]
    [DataRow("order")]
    [DataRow("target")]
    [DataRow("module")]
    [DataRow("method")]
    [DataRow("static")]
    [DataRow("slot")]
    public void Same_delegate_type_does_not_hide_different_invocation_or_target_identity(string fault)
    {
        var source = Data(); var target = CanonicalInputCallbackContract.Copy(source.Callbacks);
        if (fault == "order") Array.Reverse(target.Callbacks[3].Methods);
        if (fault == "target") target.Callbacks[3].Methods[0].TargetBinding = target.Callbacks[3].Methods[1].TargetBinding;
        if (fault == "module") target.Callbacks[3].Methods[0].ModuleMvid = "00000000-0000-0000-0000-000000000001";
        if (fault == "method") target.Callbacks[3].Methods[0].Token++;
        if (fault == "static") target.Callbacks[3].Methods[0].IsStatic = true;
        if (fault == "slot") target.Callbacks[3].Field = "OnUpdate";
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalInputCallbackContract.RequireSame(source, target));
    }
}
