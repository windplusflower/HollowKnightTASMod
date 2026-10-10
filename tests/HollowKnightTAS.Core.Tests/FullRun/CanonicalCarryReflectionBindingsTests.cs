using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalCarryReflectionBindingsTests
{
    private static readonly Func<bool> Original = () => true;
    private static readonly Func<bool> Unregistered = () => false;
    private static CanonicalCarryReflectionBindings Bindings() => new(new[] { typeof(object).Assembly, typeof(CanonicalCarryReflectionBindingsTests).Assembly },
        new[] { typeof(CanonicalCarryReflectionBindingsTests).GetField(nameof(Original), BindingFlags.Static | BindingFlags.NonPublic)! });

    [TestMethod]
    public void Resolves_current_metadata_and_original_callback_without_invoking_it()
    {
        var bindings = Bindings();
        var method = typeof(Example).GetMethod(nameof(Example.Sample))!;
        Assert.AreSame(typeof(Example), bindings.Resolve(bindings.Bind(typeof(Example))!));
        Assert.AreEqual(method, bindings.Resolve(bindings.Bind(method)!));
        Assert.AreSame(Original, bindings.Resolve(bindings.Bind(Original)!));
        Assert.AreSame(EqualityComparer<string>.Default, bindings.Resolve(bindings.Bind(EqualityComparer<string>.Default)!));
    }

    [TestMethod]
    public void Parameter_arrays_keep_metadata_shape_and_distinct_array_identities()
    {
        var bindings = Bindings(); var method = typeof(Example).GetMethod(nameof(Example.Sample))!;
        var first = method.GetParameters(); var second = method.GetParameters();
        var key = bindings.Bind(first)!; var nextKey = bindings.Bind(second)!;
        Assert.AreNotEqual(key, nextKey);
        var result = (ParameterInfo[])bindings.Resolve(key);
        Assert.AreEqual(first.Length, result.Length); Assert.AreEqual(method, result[0].Member); Assert.AreEqual(0, result[0].Position);
        Assert.AreNotSame(result, bindings.Resolve(nextKey));
        Assert.AreEqual(0, ((ParameterInfo[])bindings.Resolve(bindings.Bind(Array.Empty<ParameterInfo>())!)).Length);
    }

    [TestMethod]
    public void Rejects_unknown_assembly_module_method_and_callback_bindings()
    {
        var bindings = Bindings(); var key = bindings.Bind(typeof(Example))!;
        Assert.ThrowsExactly<InvalidDataException>(() => bindings.Resolve(key.Replace(typeof(Example).Module.ModuleVersionId.ToString("D"), Guid.Empty.ToString("D"))));
        Assert.ThrowsExactly<InvalidDataException>(() => bindings.Resolve("metadata-type|unloaded|" + Guid.Empty + "|Payload.Type"));
        Assert.ThrowsExactly<InvalidDataException>(() => bindings.Bind(Unregistered));
        Assert.ThrowsExactly<InvalidDataException>(() => bindings.Bind(new DynamicMethod("unowned", typeof(void), Type.EmptyTypes)));
        var method = bindings.Bind(typeof(Example).GetMethod(nameof(Example.Sample))!)!;
        Assert.ThrowsExactly<InvalidDataException>(() => bindings.Resolve(method.Substring(0, method.LastIndexOf('|') + 1) + "00000001"));
    }
    private sealed class Example { public bool Sample(int ignored) => throw new InvalidOperationException("Metadata method must not execute."); }
}
