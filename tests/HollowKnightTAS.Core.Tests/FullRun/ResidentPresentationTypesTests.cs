using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentPresentationTypesTests
{
    private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    private static object Get(object owner,string name)=>owner.GetType().GetField(name,Flags)!.GetValue(owner)!;
    private static void Set(object owner,string name,object value)=>owner.GetType().GetField(name,Flags)!.SetValue(owner,value);
    private sealed class Api
    {
        private readonly Type type;
        internal Api()
        {
            var repo=new DirectoryInfo(AppContext.BaseDirectory);
            while(repo!=null && !File.Exists(Path.Combine(repo.FullName,"HollowKnightTAS.sln"))) repo=repo.Parent;
            Assert.IsNotNull(repo);
            type=Assembly.LoadFrom(Path.Combine(repo.FullName,"src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
                .GetType("HollowKnightTAS.Runtime.FullRun.ResidentPresentationTypes",true)!;
        }
        internal object New(string name)=>Activator.CreateInstance(type.GetNestedType(name,Flags)!,true)!;
        internal object Fixture(string kind="canvas-group")
        {
            var data=New("Data"); var row=New("Row"); Set(row,"Owner","owner"); Set(row,"Kind",kind);
            var sizes=kind=="canvas-group"?new[]{2,7}:kind=="sorting-group"?new[]{2,9,8}:kind=="light"?new[]{2,12}:new[]{33};
            Set(row,"Scalars",sizes.Select(size=>new byte[size]).ToArray());
            var rows=Array.CreateInstance(row.GetType(),1); rows.SetValue(row,0); Set(data,"Rows",rows);
            if(kind=="sorting-group") {
                var registry=Get(data,"Sorting"); Set(registry,"Present",true); Set(registry,"Owners",new[]{"owner"}); Set(registry,"OwnerCapacity",1);
                Set(registry,"Values",new byte[28]); Set(registry,"ValueCapacity",1);
            }
            return data;
        }
        internal object LightFixture()
        {
            var data=Fixture("light"); var row=Row(data); Set(row,"SharedLight",true); Set(row,"LightReferences",1);
            var sizes=new[]{2,12,102,44,5,96,64,20,156}; var scalars=sizes.Select(size=>new byte[size]).ToArray();
            Buffer.BlockCopy(BitConverter.GetBytes(-1),0,scalars[1],8,4); Set(row,"Scalars",scalars);
            Set(Get(data,"Lights"),"Present",true); return data;
        }
        internal object Row(object data)=>((Array)Get(data,"Rows")).GetValue(0)!;
        internal void Call(string method,params object[] values)=>type.GetMethod(method,Flags)!.Invoke(null,values);
        internal void Reject(string method,params object[] values)
        { var error=Assert.ThrowsExactly<TargetInvocationException>(()=>Call(method,values)); Assert.IsInstanceOfType<InvalidDataException>(error.InnerException); }
    }
    [TestMethod]
    public void CanvasGroupReadbackDetectsEveryNativeSemanticByte()
    {
        var api=new Api(); var expected=api.Fixture(); api.Call("RequireCompleteCoverage",expected);
        foreach(var slot in new[]{Tuple.Create(0,0),Tuple.Create(0,1),Tuple.Create(1,0),Tuple.Create(1,3),Tuple.Create(1,4),Tuple.Create(1,5),Tuple.Create(1,6)}) {
            var actual=api.Fixture(); ((byte[][])Get(api.Row(actual),"Scalars"))[slot.Item1][slot.Item2]=1;
            api.Reject("RequireEqual",expected,actual);
        }
    }
    [TestMethod]
    public void LightCoverageCannotBeErasedByDeletingSerializedGaps()
    { var api=new Api(); var data=api.Fixture("light"); Set(data,"CoverageGaps",Array.Empty<string>()); api.Reject("RequireCompleteCoverage",data); api.Reject("RequireCanonicalizableCoverage",data); }
    [TestMethod]
    public void TextFontAndRetainedMeshNeedIndependentOwnershipClosure()
    {
        var api=new Api(); var data=api.Fixture("text-mesh"); api.Call("RequireCompleteCoverage",data);
        Set(api.Row(data),"Font","font"); api.Reject("RequireCompleteCoverage",data);
        Set(api.Row(data),"Font",""); Set(api.Row(data),"Mesh","mesh"); api.Reject("RequireCompleteCoverage",data);
    }
    [TestMethod]
    public void SortingRegistryOrderIndexAndDataAreIndependentReadbackState()
    {
        var api=new Api(); var data=api.Fixture("sorting-group"); api.Call("RequireCompleteCoverage",data);
        var actual=api.Fixture("sorting-group"); ((byte[])Get(Get(actual,"Sorting"),"Values"))[24]=1; api.Reject("RequireEqual",data,actual);
        actual=api.Fixture("sorting-group"); Set(Get(actual,"Sorting"),"Dirty",new[]{"owner"}); api.Reject("RequireEqual",data,actual);
        actual=api.Fixture("sorting-group"); ((byte[][])Get(api.Row(actual),"Scalars"))[2][4]=1; api.Reject("Validate",actual);
    }
    [TestMethod]
    public void TargetAllocatorCapacityIsValidatedButDoesNotCarryGameplayHistory()
    {
        var api=new Api(); var expected=api.Fixture("sorting-group"); var actual=api.Fixture("sorting-group");
        Set(Get(actual,"Sorting"),"OwnerCapacity",8); Set(Get(actual,"Sorting"),"ValueCapacity",8); api.Call("RequireEqual",expected,actual);
        Set(Get(actual,"Sorting"),"OwnerCapacity",0); api.Reject("Validate",actual);
    }
    [TestMethod]
    public void UnknownRegistryOwnersAndExecutionOwnershipRemainHardGaps()
    {
        var api=new Api(); var data=api.Fixture("canvas-group"); Set(api.Row(data),"Execution",true); api.Reject("RequireCompleteCoverage",data);
        data=api.Fixture("sorting-group"); Set(Get(data,"Sorting"),"Dirty",new[]{"@unsupported"}); api.Reject("RequireCompleteCoverage",data);
    }
    [TestMethod]
    public void LightCompleteBodyIncludesShadowMatrixAndCachedLightingValues()
    {
        var api=new Api(); var expected=api.LightFixture(); api.Call("RequireCompleteCoverage",expected);
        foreach(var slot in new[]{Tuple.Create(2,60),Tuple.Create(3,43),Tuple.Create(4,4),Tuple.Create(5,95),Tuple.Create(6,63),Tuple.Create(7,19),Tuple.Create(8,155)}) {
            var actual=api.LightFixture(); ((byte[][])Get(api.Row(actual),"Scalars"))[slot.Item1][slot.Item2]=1;
            api.Reject("RequireEqual",expected,actual);
        }
        var distances=api.LightFixture(); Set(api.Row(distances),"ShadowDistances",new byte[128]); api.Reject("RequireEqual",expected,distances);
    }
    [TestMethod]
    public void LightCowHasOneExplicitCanonicalizableGap()
    {
        var api=new Api(); var data=api.LightFixture(); Set(api.Row(data),"LightReferences",2);
        api.Call("RequireCanonicalizableCoverage",data); api.Reject("RequireCompleteCoverage",data);
        Set(Get(data,"Lights"),"ListenerVtables",new[]{0x100000}); api.Reject("RequireCanonicalizableCoverage",data);
    }
    [TestMethod]
    public void LightNativeRegistriesAndResourcesCannotEraseTheirGaps()
    {
        var api=new Api(); var data=api.LightFixture(); var row=api.Row(data);
        Set(row,"LightEventCounts",new[]{1}); Set(row,"RenderEvents",true); api.Reject("RequireCompleteCoverage",data);
        data=api.LightFixture(); row=api.Row(data); Set(row,"CookieNativeTexture","@unsupported"); Set(row,"CookieResource",true); api.Reject("RequireCompleteCoverage",data);
        data=api.LightFixture(); Set(Get(data,"Lights"),"Active",new[]{"@unsupported"}); api.Reject("RequireCanonicalizableCoverage",data);
        data=api.LightFixture(); Set(Get(data,"Lights"),"Realtime",new[]{"owner"}); api.Call("RequireCompleteCoverage",data);
        var other=api.LightFixture(); api.Reject("RequireEqual",data,other);
    }
    [TestMethod]
    public void TextMeshOwnedSlotIsExplicitAndCannotExistWithoutAMesh()
    {
        var api=new Api(); var data=api.Fixture("text-mesh"); var row=api.Row(data);
        Set(row,"MeshOwned",true); api.Reject("Validate",data);
        Set(row,"Mesh","dynamic-mesh"); api.Call("RequireCompleteCoverage",data);
        var other=api.Fixture("text-mesh"); api.Reject("RequireEqual",data,other);
    }

}
