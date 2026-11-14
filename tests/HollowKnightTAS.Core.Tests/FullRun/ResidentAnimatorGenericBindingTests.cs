using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentAnimatorGenericBindingTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static Type Load()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !File.Exists(Path.Combine(repo.FullName, "HollowKnightTAS.sln"))) repo = repo.Parent;
        Assert.IsNotNull(repo);
        return Assembly.LoadFrom(Environment.GetEnvironmentVariable("HKTAS_TEST_RUNTIME_ASSEMBLY")
            ?? Path.Combine(repo.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
            .GetType("HollowKnightTAS.Runtime.FullRun.ResidentAnimatorState", true)!;
    }
    private static object New(Type type, string name) => Activator.CreateInstance(type.GetNestedType(name, Flags)!, true)!;
    private static object Get(object value, string field) => value.GetType().GetField(field, Flags)!.GetValue(value)!;
    private static void Set(object value, string field, object? data) => value.GetType().GetField(field, Flags)!.SetValue(value, data);
    private static object? Call(Type type, string method, params object[] args) => type.GetMethod(method, Flags)!.Invoke(null, args);
    private static void Invalid(Action action)
        => Assert.IsInstanceOfType<InvalidDataException>(Assert.ThrowsExactly<TargetInvocationException>(action).InnerException);
    private static void ArrayField(object owner, string field, params object[] values)
    {
        var info = owner.GetType().GetField(field, Flags)!;
        var array = Array.CreateInstance(info.FieldType.GetElementType()!, values.Length);
        for (int index = 0; index < values.Length; index++) array.SetValue(values[index], index);
        info.SetValue(owner, array);
    }
    private static byte[] ScaleSchema()
    {
        var bytes = new byte[12];
        BitConverter.GetBytes(8u).CopyTo(bytes, 4);
        return bytes;
    }
    private static object Backing(Type type, object? dependency = null)
    {
        var value = New(type, "Backing");
        foreach (string field in new[] { "GenericPresent", "SetPresent", "ControllerPresent", "ClipSetPresent" }) Set(value, field, true);
        Set(value, "ParameterSchema", ScaleSchema());
        var set = New(type, "SetBindingDependency");
        Set(set, "FullSchemaPresent", true);
        Set(set, "FullSchema", ScaleSchema());
        Set(set, "IntegerStride", 4);
        Set(set, "TransformCount", 1);
        Set(set, "NonConstantTransforms", 1);
        Set(set, "TransformsPresent", true);
        Set(set, "Transforms", new byte[8]);
        Set(value, "SetDependency", set);
        dependency ??= New(type, "GenericOwnerDependency");
        Set(dependency, "SetAlias", true);
        Set(dependency, "SkeletonCount", 1);
        Set(dependency, "TqsPresent", true);
        Set(dependency, "TqsIndices", new[] { -1, -1, 0 });
        var table = ((Array)Get(dependency, "Tables")).GetValue(0)!;
        if (((Array)Get(table, "Rows")).Length == 0)
        {
            var row = New(type, "BoundCurveDependency");
            Set(row, "Type", 3u);
            ArrayField(table, "Rows", row);
            Set(table, "OwnerOffset", 96);
        }
        Set(value, "BoundCurveTargets", new[] { true, false, false, false });
        Set(value, "GenericDependency", dependency);
        return value;
    }
    private static object Dependency(object backing) => Get(backing, "GenericDependency");
    private static object Curve(object backing) => ((Array)Get(((Array)Get(Dependency(backing), "Tables")).GetValue(0)!, "Rows")).GetValue(0)!;
    private static void Validate(Type type, object backing)
    {
        var evaluation = New(type, "Evaluation");
        Set(evaluation, "Backing", backing);
        Call(type, "ValidateEvaluation", evaluation);
    }

    [TestMethod]
    public void CompleteTransformDependencyPreservesDestroyedTargetTagAndLegacyDtosRemainGated()
    {
        var type = Load();
        var source = Backing(type);
        var target = Backing(type);
        Validate(type, source);
        Assert.AreEqual(3u, Get(Curve(source), "Type"));
        Assert.AreEqual("", Get(Curve(source), "TargetObject"));
        Assert.AreEqual(0, ((IEnumerable<string>)Call(type, "GenericDependencyGaps", source)!).Count());
        Call(type, "RequireGenericDependenciesMatch", source, target);
        Set(source, "GenericDependency", null);
        var gaps = ((IEnumerable<string>)Call(type, "GenericDependencyGaps", source)!).ToArray();
        CollectionAssert.Contains(gaps, "generic-and-asset-binding-owner-allocation");
        CollectionAssert.Contains(gaps, "generic-native-bound-curves");
        Invalid(() => Call(type, "RequireGenericDependenciesMatch", source, target));
    }

    [TestMethod]
    public void DifferentLogicalBindingUnknownTagAndCustomInteriorPointersCannotPrepare()
    {
        var type = Load();
        var source = Backing(type);
        var target = Backing(type);
        Set(Curve(source), "TargetObject", "transform/source");
        Set(Curve(target), "TargetObject", "transform/target");
        Invalid(() => Call(type, "RequireGenericDependenciesMatch", source, target));
        foreach (var change in new Action<object>[] {
            row => Set(row, "Type", 99u), row => Set(row, "CustomBinding", true), row => Set(row, "TargetPointer", true)
        })
        {
            source = Backing(type);
            target = Backing(type);
            change(Curve(source));
            change(Curve(target));
            Invalid(() => Call(type, "RequireGenericDependenciesMatch", source, target));
        }
    }

    [TestMethod]
    public void ScalarScaleArrayTqsAndSetAliasesAreIndependentlyChecked()
    {
        var type = Load();
        var source = Backing(type);
        var target = Backing(type);
        Set(Dependency(target), "RootIndices", new[] { -1, -1, 0 });
        Invalid(() => Call(type, "RequireGenericDependenciesMatch", source, target));
        target = Backing(type);
        Set(Dependency(target), "TqsIndices", new[] { -1, -1, -1 });
        Invalid(() => Call(type, "RequireGenericDependenciesMatch", source, target));
        Set(Dependency(target), "TqsIndices", new[] { -1, -1, 1 });
        Invalid(() => Validate(type, target));
        target = Backing(type);
        Set(Dependency(target), "SetAlias", false);
        Invalid(() => Call(type, "RequireGenericDependenciesMatch", source, target));
        Set(Dependency(target), "SetAlias", true);
        Set(Dependency(target), "AllowConstantOptimization", (byte)2);
        Invalid(() => Call(type, "RequireGenericDependenciesMatch", source, target));
        foreach (var backing in new[] { source, target })
        {
            var dependency = Dependency(backing);
            Set(dependency, "AllowConstantOptimization", (byte)0);
            Set(dependency, "Optimized", true);
            Set(dependency, "ExposedCount", 2);
            Set(dependency, "ScaleOffset", 96);
            Set(dependency, "ScaleChanges", new byte[] { 0, 1 });
            var table = ((Array)Get(dependency, "Tables")).GetValue(0)!;
            Set(table, "OwnerOffset", -1);
            ArrayField(table, "Rows");
            Set(backing, "BoundCurveTargets", new bool[4]);
        }
        Call(type, "RequireGenericDependenciesMatch", source, target);
        Set(Dependency(target), "ScaleChanges", new byte[] { 1, 1 });
        Invalid(() => Call(type, "RequireGenericDependenciesMatch", source, target));
        var owners = new Dictionary<IntPtr, IntPtr>();
        Call(type, "RegisterTqsOwner", owners, new IntPtr(10), new IntPtr(20));
        Call(type, "RegisterTqsOwner", owners, new IntPtr(10), new IntPtr(20));
        Invalid(() => Call(type, "RegisterTqsOwner", owners, new IntPtr(10), new IntPtr(30)));
    }

    [TestMethod]
    public void NonemptyFloatTableAndWrongOwnerOffsetsKeepSpecificCoverageGaps()
    {
        var type = Load();
        var backing = Backing(type);
        var dependency = Dependency(backing);
        var table = ((Array)Get(dependency, "Tables")).GetValue(1)!;
        var row = New(type, "BoundCurveDependency");
        Set(row, "Type", 5u);
        Set(row, "TargetPointer", true);
        ArrayField(table, "Rows", row);
        Set(table, "OwnerOffset", 128);
        var gaps = ((IEnumerable<string>)Call(type, "GenericDependencyGaps", backing)!).ToArray();
        CollectionAssert.Contains(gaps, "generic-native-bound-curves:unsupported-float-target-storage");
        Set(table, "OwnerOffset", -2);
        gaps = ((IEnumerable<string>)Call(type, "GenericDependencyGaps", backing)!).ToArray();
        CollectionAssert.Contains(gaps, "generic-and-asset-binding-owner-allocation");
    }

    [TestMethod]
    public void NativeCaptureNormalizesOwnerRelativeTablesAcrossAddressesAndRetainsNullTransform()
    {
        var type = Load();
        object Capture(IntPtr memory)
        {
            Marshal.Copy(new byte[1408], 0, memory, 1408);
            Marshal.WriteInt64(memory, 0, 1);
            Marshal.WriteIntPtr(memory, 8, IntPtr.Add(memory, 96));
            Marshal.WriteInt32(memory, 104, 3);
            Marshal.WriteIntPtr(memory, 72, IntPtr.Add(memory, 512));
            foreach (int offset in new[] { 84, 88, 92 }) Marshal.WriteInt32(memory, offset, -1);
            Marshal.WriteIntPtr(memory, 528, IntPtr.Add(memory, 1264));
            Marshal.WriteIntPtr(memory, 536, IntPtr.Add(memory, 800));
            Marshal.WriteIntPtr(memory, 696, IntPtr.Add(memory, 800));
            Marshal.WriteInt64(memory, 896, 208);
            Marshal.WriteInt32(memory, 1104, 1);
            Marshal.WriteInt64(memory, 1208, 1);
            foreach (int offset in new[] { 1264, 1268, 1272 }) Marshal.WriteInt32(memory, offset, -1);
            var reader = RuntimeHelpers.GetUninitializedObject(type.GetNestedType("Reader", Flags)!);
            Set(reader, "Memory", New(type, "MemoryReader"));
            return Call(type, "CaptureGenericDependency", reader, memory, IntPtr.Add(memory, 608),
                IntPtr.Add(memory, 896), IntPtr.Add(memory, 1184), false)!;
        }
        var first = Marshal.AllocHGlobal(1408);
        var second = Marshal.AllocHGlobal(1408);
        try
        {
            var source = Capture(first);
            var target = Capture(second);
            Assert.AreNotEqual(first, second);
            Assert.AreEqual(Call(type, "Json", source), Call(type, "Json", target));
            var table = ((Array)Get(source, "Tables")).GetValue(0)!;
            Assert.AreEqual(96, Get(table, "OwnerOffset"));
            Assert.AreEqual(3u, Get(((Array)Get(table, "Rows")).GetValue(0)!, "Type"));
            Assert.AreEqual("", Get(((Array)Get(table, "Rows")).GetValue(0)!, "TargetObject"));
        }
        finally
        {
            Marshal.FreeHGlobal(first);
            Marshal.FreeHGlobal(second);
        }
    }
}
