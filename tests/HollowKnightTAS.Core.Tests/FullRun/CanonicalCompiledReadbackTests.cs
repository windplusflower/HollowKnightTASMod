using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalCompiledReadbackTests
{
    private class Base { private readonly int hidden = 17; public int Hidden => hidden; }
    private sealed class Fields : Base {
        public volatile int Version = 3;
        public readonly double Readonly = BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000));
        public int? Nullable;
        public object? Alias;
        public Value Data;
    }
    private struct Value { public float A, B, C, D; public double E, F; public int G, H; }
    private sealed class Owner { public Value A, B, C, D; public object? Left, Right; public int Count; }
    private sealed class Generic<T> { public T? Item; }
    private static Value Bits(int n) => new() { A = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)),
        B = BitConverter.Int32BitsToSingle(unchecked((int)0x7fc01234)), C = n, D = -n, E = -0d, F = n, G = n, H = ~n };

    [TestMethod]
    public void MetadataGettersPreservePrivateReadonlyVolatileNullableAndReflectionFailures()
    {
        var reader = new LoadingFieldReader(true, preserveReflectionFailures: true);
        var owner = new Fields { Data = Bits(9), Alias = new object() };
        foreach (var field in CanonicalCarryGraphCodec.Fields(typeof(Fields))) {
            var read = reader.Prepare(field);
            Assert.AreEqual(field.GetValue(owner), read(owner));
            foreach (var invalid in new object?[] { null, new object(), 1 }) {
                Exception? Expected() { try { field.GetValue(invalid); return null; } catch (Exception e) { return e; } }
                Exception? Actual() { try { read(invalid!); return null; } catch (Exception e) { return e; } }
                Assert.AreEqual(Expected()?.GetType(), Actual()?.GetType(), field.Name);
            }
        }
        var nullable = reader.Prepare(typeof(Fields).GetField(nameof(Fields.Nullable))!);
        Assert.IsNull(nullable(owner)); owner.Nullable = 7; Assert.AreEqual(7, nullable(owner));
        var version = reader.Prepare(typeof(Fields).GetField(nameof(Fields.Version))!);
        owner.Version = 99; Assert.AreEqual(99, version(owner));
        object boxed = Bits(10);
        foreach (var field in typeof(Value).GetFields()) Assert.AreEqual(field.GetValue(boxed), reader.Prepare(field)(boxed));
        Assert.AreEqual(unchecked((long)0x8000000000000000), BitConverter.DoubleToInt64Bits((double)reader.Prepare(typeof(Fields).GetField(nameof(Fields.Readonly))!)(owner)!));
    }

    [TestMethod]
    public void SharedMetadataUsesExactClosedFieldsAndRetainsFreshReflectionSemantics()
    {
        var number = typeof(Generic<int>).GetField(nameof(Generic<int>.Item))!;
        var text = typeof(Generic<string>).GetField(nameof(Generic<string>.Item))!;
        var readNumber = LoadingFieldReader.PrepareSharedReadback(number);
        Assert.AreSame(readNumber, LoadingFieldReader.PrepareSharedReadback(typeof(Generic<int>).GetField(nameof(Generic<int>.Item))!));
        var readText = LoadingFieldReader.PrepareSharedReadback(text);
        Assert.AreNotSame(readNumber, readText);
        var a = new Generic<int> { Item = 4 }; var b = new Generic<string>();
        Assert.AreEqual(4, readNumber(a)); Assert.IsNull(readText(b));
        a.Item = 5; b.Item = "next"; Assert.AreEqual(5, readNumber(a)); Assert.AreEqual("next", readText(b));
        Assert.ThrowsExactly<ArgumentException>(() => readNumber(b));
        Assert.IsTrue(LoadingFieldReader.SharedReadbackAccessorCount <= 16384);
    }

    [TestMethod]
    public void FullPlansHaveIdenticalEncodingAliasesAndFreshBitStaleRejection()
    {
        byte[]? baseline = null;
        foreach (var compiled in new[] { false, true }) {
            var codec = new CanonicalCarryGraphCodec(new[] { typeof(Owner), typeof(Value) }, "compiled-readback-contract", compiledReadbackFields: compiled);
            var alias = new object();
            var source = new Owner { A = Bits(7), B = Bits(8), Left = alias, Right = alias, Count = 3 };
            var graph = codec.Capture(new Dictionary<string, object> { ["root"] = source }, value => ReferenceEquals(value, alias) ? "alias" : null);
            var bytes = codec.Encode(graph);
            if (baseline == null) baseline = bytes; else CollectionAssert.AreEqual(baseline, bytes);
            var target = new Owner();
            var plan = codec.PrepareOwnedGraph(graph, new Dictionary<string, object> { ["root"] = target }, _ => alias);
            plan.Apply(); plan.VerifyReadback(); Assert.AreSame(target.Left, target.Right);
            target.A.A = 0f;
            Assert.ThrowsExactly<InvalidOperationException>(() => plan.VerifyReadback());
            target.A.A = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
            plan.VerifyReadback();
            target.Right = new object();
            Assert.ThrowsExactly<InvalidOperationException>(() => plan.VerifyReadback());
        }
    }

    [TestMethod]
    public void RepresentativeCoreClrReadbackTimingRetainsAllFreshChecks()
    {
        const int roots = 654, passes = 256;
        var times = new List<object>();
        foreach (var compiled in new[] { false, true }) {
            var codec = new CanonicalCarryGraphCodec(new[] { typeof(Owner), typeof(Value) }, "readback-timing", compiledReadbackFields: compiled);
            var source = Enumerable.Range(0, roots).ToDictionary(n => "root-" + n, n => (object)new Owner { A = Bits(n), B = Bits(n+1), C = Bits(n+2), D = Bits(n+3), Count = n });
            var target = Enumerable.Range(0, roots).ToDictionary(n => "root-" + n, _ => (object)new Owner());
            var graph = codec.Capture(source, _ => null);
            var plan = codec.PrepareOwnedGraph(graph, target, _ => throw new InvalidDataException());
            plan.Apply(); for (var warm = 0; warm < 4; warm++) plan.VerifyReadback();
            var watch = Stopwatch.StartNew(); for (var pass = 0; pass < passes; pass++) plan.VerifyReadback(); watch.Stop();
            times.Add(new { compiled, roots, passes, graphNodes = graph.Nodes.Length, elapsedMilliseconds = watch.Elapsed.TotalMilliseconds });
        }
        var output = Environment.GetEnvironmentVariable("HKTAS_READBACK_BENCH_OUTPUT");
        if (output != null) File.WriteAllText(output, JsonSerializer.Serialize(new { runtime = "CoreCLR", actualMonoMeasured = false,
            fullFreshReadbacks = true, times }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
