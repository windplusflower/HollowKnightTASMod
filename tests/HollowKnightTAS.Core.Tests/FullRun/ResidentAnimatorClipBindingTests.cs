using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentAnimatorClipBindingTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static Type Load()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !File.Exists(Path.Combine(repo.FullName,"HollowKnightTAS.sln"))) repo = repo.Parent;
        Assert.IsNotNull(repo);
        return Assembly.LoadFrom(Environment.GetEnvironmentVariable("HKTAS_TEST_RUNTIME_ASSEMBLY") ?? Path.Combine(repo.FullName,"src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
            .GetType("HollowKnightTAS.Runtime.FullRun.ResidentAnimatorState",true)!;
    }
    private static object New(Type type,string name) => Activator.CreateInstance(type.GetNestedType(name,Flags)!,true)!;
    private static object Call(Type type,string method,params object[] args) => type.GetMethod(method,Flags)!.Invoke(null,args)!;
    private static void Set(object value,string name,object data) => value.GetType().GetField(name,Flags)!.SetValue(value,data);
    private static object Get(object value,string name) => value.GetType().GetField(name,Flags)!.GetValue(value)!;
    private static void Invalid(Action action) => Assert.IsInstanceOfType<InvalidDataException>(Assert.ThrowsExactly<TargetInvocationException>(action).InnerException);
    private static byte[] Schema(params (uint Type,uint Index)[] rows)
    {
        var bytes = new byte[rows.Length*12];
        for (int i=0;i<rows.Length;i++) { BitConverter.GetBytes(rows[i].Type).CopyTo(bytes,i*12+4); BitConverter.GetBytes(rows[i].Index).CopyTo(bytes,i*12+8); }
        return bytes;
    }
    private static (object Evaluation,object Backing,object Dependency,object Clip) Fixture(Type type)
    {
        var evaluation = New(type,"Evaluation"); var backing = Get(evaluation,"Backing");
        var dependency = New(type,"SetBindingDependency"); var clip = New(type,"ClipCurveMapDependency");
        Set(backing,"SetPresent",true); Set(backing,"ClipSetPresent",true); Set(backing,"Clips",new[]{"asset/clip"});
        Set(backing,"OptimizedCurves",new[]{1}); Set(backing,"ClipMaps",new[]{511}); Set(backing,"SetDependency",dependency);
        Set(dependency,"FullSchemaPresent",true); Set(dependency,"FullSchema",Schema((1,0),(3,0),(6,0),(7,0),(8,0)));
        Set(dependency,"IntegerStride",4); Set(dependency,"RootMotion",(byte)1);
        Set(clip,"Present",Enumerable.Repeat(true,9).ToArray());
        Set(clip,"Indices",Enumerable.Range(0,8).Select(_=>new ushort[]{ushort.MaxValue}).ToArray());
        Set(clip,"Objects",new[]{"asset/object",""}); ((IList)Get(dependency,"ClipBindings")).Add(clip);
        return (evaluation,backing,dependency,clip);
    }
    [TestMethod]
    public void UnionSchemaCountsShareRotationAndIntegerCategoriesAndRejectHolesOrDuplicates()
    {
        var type=Load();
        CollectionAssert.AreEqual(new[]{1,2,2,2,1,1,2,2},(int[])Call(type,"ClipBindingCounts",Schema((7,1),(3,0),(6,0),(1,0),(8,0),(7,0),(3,1))));
        foreach (var schema in new[]{Schema((7,0),(7,0)),Schema((3,1)),Schema((2,0)),new byte[13]})
            Invalid(()=>Call(type,"ClipBindingCounts",schema));
    }
    [TestMethod]
    public void CompletePayloadCoversMapsButKeepsGenericOwnerGapAndPreservesUshortSentinel()
    {
        var type=Load(); var fixture=Fixture(type);
        Call(type,"ValidateEvaluation",fixture.Evaluation);
        var gaps=((IEnumerable<string>)Call(type,"EvaluationGaps",fixture.Evaluation)).ToArray();
        Assert.IsFalse(gaps.Contains("clip-curve-index-and-integer-remap"));
        Assert.IsTrue(gaps.Contains("generic-and-asset-binding-owner-allocation"));
        Assert.AreEqual(ushort.MaxValue,((ushort[][])Get(fixture.Clip,"Indices"))[0][0]);
        Set(fixture.Backing,"SetDependency",null!);
        Call(type,"ValidateEvaluation",fixture.Evaluation);
        Assert.IsTrue(((IEnumerable<string>)Call(type,"EvaluationGaps",fixture.Evaluation)).Contains("clip-curve-index-and-integer-remap"));
    }
    [TestMethod]
    public void DependencyRejectsMalformedLengthsPresenceAbsentOwnersAndUnboundObjects()
    {
        var type=Load();
        foreach (var mutation in new Action<object,object,object>[] {
            (b,d,c)=>Set(c,"Indices",new ushort[8][]),
            (b,d,c)=>Set(c,"Present",new bool[9]),
            (b,d,c)=>Set(b,"ClipMaps",new[]{0}),
            (b,d,c)=>Set(d,"FullSchema",Schema((7,2))),
            (b,d,c)=>Set(d,"RootMotion",(byte)2),
            (b,d,c)=>Set(d,"IntegerStride",8),
            (b,d,c)=>Set(d,"TransformCount",1),
            (b,d,c)=>Set(d,"ConstantCount",1),
            (b,d,c)=>Set(b,"SetPresent",false),
            (b,d,c)=>Set(b,"ClipSetPresent",false),
            (b,d,c)=>Set(b,"Clips",new[]{""})
        }) {
            var fixture=Fixture(type); mutation(fixture.Backing,fixture.Dependency,fixture.Clip);
            Invalid(()=>Call(type,"ValidateEvaluation",fixture.Evaluation));
        }
        var valid=Fixture(type); Set(valid.Clip,"Objects",new[]{"!unbound"});
        // Use the engine adapter's actual sentinel rather than presuming its spelling.
        var sentinel=(string)type.GetField("Unbound",Flags)!.GetValue(null)!; Set(valid.Clip,"Objects",new[]{sentinel});
        Assert.IsTrue(((IEnumerable<string>)Call(type,"EvaluationGaps",valid.Evaluation)).Contains("unbound-clip-integer-remap-object"));
    }
    [TestMethod]
    public void DependencyContentIncludesTransformAndFloatBitsAndLogicalObjectOrder()
    {
        var type=Load(); var a=Fixture(type); var b=Fixture(type);
        string Json(object value)=>(string)Call(type,"Json",value);
        Assert.AreEqual(Json(a.Dependency),Json(b.Dependency));
        Set(a.Dependency,"TransformCount",1); Set(a.Dependency,"TransformsPresent",true); Set(a.Dependency,"Transforms",new byte[]{1,2,3,4,1,0,0,0});
        Set(a.Dependency,"ConstantCount",1); Set(a.Dependency,"ConstantsPresent",true); Set(a.Dependency,"Constants",BitConverter.GetBytes(unchecked((int)0x80000000)));
        Call(type,"ValidateEvaluation",a.Evaluation);
        Assert.AreNotEqual(Json(a.Dependency),Json(b.Dependency));
        Set(b.Dependency,"TransformCount",1); Set(b.Dependency,"TransformsPresent",true); Set(b.Dependency,"Transforms",new byte[]{1,2,3,4,1,0,0,0});
        Set(b.Dependency,"ConstantCount",1); Set(b.Dependency,"ConstantsPresent",true); Set(b.Dependency,"Constants",BitConverter.GetBytes(unchecked((int)0x80000000)));
        Assert.AreEqual(Json(a.Dependency),Json(b.Dependency));
        Set(b.Clip,"Objects",new[]{"","asset/object"}); Assert.AreNotEqual(Json(a.Dependency),Json(b.Dependency));
    }
    [TestMethod]
    public void NativeIntegerRemapMustAliasItsClipMappingAndPreservesTargetStorage()
    {
        var type=Load(); var readerType=type.GetNestedType("Reader",Flags)!;
        var reader=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(readerType);
        Set(reader,"Memory",New(type,"MemoryReader"));
        var clip=System.Runtime.InteropServices.Marshal.AllocHGlobal(512);
        var ids=System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
        try {
            System.Runtime.InteropServices.Marshal.Copy(new byte[512],0,clip,512);
            System.Runtime.InteropServices.Marshal.WriteInt32(ids,0);
            System.Runtime.InteropServices.Marshal.WriteInt64(clip,480,ids.ToInt64());
            System.Runtime.InteropServices.Marshal.WriteInt64(clip,496,1);
            System.Runtime.InteropServices.Marshal.WriteInt64(clip,504,2);
            var entries=new byte[72]; BitConverter.GetBytes(ids.ToInt64()).CopyTo(entries,64);
            var result=Call(type,"CaptureClipCurveMapDependency",reader,clip,entries,0,new int[8]);
            CollectionAssert.AreEqual(new[]{""},(string[])Get(result,"Objects"));
            Assert.AreEqual(ids.ToInt64(),System.Runtime.InteropServices.Marshal.ReadInt64(clip,480));
            Assert.AreEqual(ids.ToInt64(),BitConverter.ToInt64(entries,64));
            var mismatched=(byte[])entries.Clone(); BitConverter.GetBytes(ids.ToInt64()+4).CopyTo(mismatched,64);
            Invalid(()=>Call(type,"CaptureClipCurveMapDependency",reader,clip,mismatched,0,new int[8]));
            Invalid(()=>Call(type,"CaptureClipCurveMapDependency",reader,IntPtr.Zero,entries,0,new int[8]));
            System.Runtime.InteropServices.Marshal.WriteInt64(clip,504,0);
            Invalid(()=>Call(type,"CaptureClipCurveMapDependency",reader,clip,entries,0,new int[8]));
            System.Runtime.InteropServices.Marshal.WriteInt64(clip,504,2);
            System.Runtime.InteropServices.Marshal.WriteInt64(clip,496,2);
            Invalid(()=>Call(type,"CaptureClipCurveMapDependency",reader,clip,entries,0,new int[8]));
        } finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(ids); System.Runtime.InteropServices.Marshal.FreeHGlobal(clip); }
    }

}
