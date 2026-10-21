using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace HollowKnightTAS.Core.Tests.FullRun;
[TestClass] public sealed class CanonicalListEmptyArrayBindingsTests
{
    private sealed class Owner { public List<int> A = new(); public List<int> B = new(); public List<int> C = new(); public List<int> D = new(); public List<string> E = new(); }
    private static CanonicalCarryGraphCodec Codec() => new(new[] { typeof(Owner), typeof(List<int>), typeof(int[]), typeof(List<string>), typeof(string[]) }, "static-list-empty-test");
    private static FieldInfo Items(Type list) => list.GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static object Items(object list) => Items(list.GetType()).GetValue(list)!;
    private static Dictionary<string,object> Roots(Owner owner) => new() { ["owner"] = owner };
    private static string? None(object _) => null;
    private static object Missing(string _) => throw new InvalidDataException("Unexpected caller external");
    [TestMethod] public void OriginalStaticEmptyIsSharedAcrossListsAndUncarriedWorldWhileExplicitEmptyIsIndependent()
    {
        var outside = new List<int>(); var source = new Owner();
        var custom = new int[0]; Items(typeof(List<int>)).SetValue(source.C, custom); Items(typeof(List<int>)).SetValue(source.D, custom);
        var codec = Codec(); var graph = codec.Capture(Roots(source), None);
        Assert.AreEqual(0, graph.Unsupported.Length); Assert.AreEqual(2, graph.Externals.Length);
        var target = new Owner(); var apply = codec.PrepareApply(graph, Roots(target), Missing); apply();
        Assert.AreSame(Items(outside), Items(target.A)); Assert.AreSame(Items(target.A), Items(target.B));
        Assert.AreNotSame(Items(target.A), Items(target.C)); Assert.AreSame(Items(target.C), Items(target.D));
        Assert.AreNotSame(custom, Items(target.C)); Assert.AreEqual(0, ((Array)Items(target.C)).Length);
        Assert.AreSame(Items(new List<string>()), Items(target.E)); Assert.AreNotSame(Items(target.A), Items(target.E));
    }
    [TestMethod] public void IndependentNewEmptyArraysDoNotMergeAndTwoCarryDomainsKeepOriginalStaticAlias()
    {
        var first = new Owner(); var second = new Owner();
        Items(typeof(List<int>)).SetValue(first.C, new int[0]); Items(typeof(List<int>)).SetValue(first.D, new int[0]);
        var one = Codec(); var two = Codec(); var a = one.Capture(Roots(first), None); var b = two.Capture(Roots(second), None);
        var x = new Owner(); var y = new Owner(); one.PrepareApply(a, Roots(x), Missing)(); two.PrepareApply(b, Roots(y), Missing)();
        Assert.AreSame(Items(x.A), Items(y.A)); Assert.AreNotSame(Items(x.C), Items(x.D));
        Assert.AreNotSame(Items(x.C), Items(x.A)); Assert.AreNotSame(Items(x.D), Items(x.A));
    }
    [TestMethod] public void WrongGenericTypeUnregisteredIdentityAndStaleSchemaFailClosed()
    {
        var codec = Codec(); var graph = codec.Capture(Roots(new Owner()), None); var target = new Owner(); var before = target.A;
        var first = graph.Externals[0]; var binding = first.Binding; var type = first.Type;
        first.Type = "System.Private.CoreLib:System.String[]";
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareApply(graph, Roots(target), Missing));
        first.Type = type; first.Binding = binding + "-unknown";
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareApply(graph, Roots(target), Missing));
        first.Binding = binding; graph.Schema = "static-list-empty-test";
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareApply(graph, Roots(target), Missing));
        Assert.AreSame(before, target.A);
    }
}
