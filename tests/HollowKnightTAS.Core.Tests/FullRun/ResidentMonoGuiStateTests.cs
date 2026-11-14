using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;
[TestClass]
public sealed class ResidentMonoGuiStateTests
{
    private const BindingFlags Flags=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private static Type Owner()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root!=null && !File.Exists(Path.Combine(root.FullName,"HollowKnightTAS.sln"))) root=root.Parent;
        return Assembly.LoadFrom(Environment.GetEnvironmentVariable("HKTAS_TEST_RUNTIME_ASSEMBLY") ?? Path.Combine(root!.FullName,"src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
            .GetType("HollowKnightTAS.Runtime.FullRun.ResidentMonoNativeState",true)!;
    }
    private static object New(string name) => Activator.CreateInstance(Owner().GetNestedType(name,Flags)!,true)!;
    private static object? Call(string method,params object?[] args) => Owner().GetMethod(method,Flags)!.Invoke(null,args);
    private static object Get(object value,string name) => value.GetType().GetField(name,Flags)!.GetValue(value)!;
    private static void Set(object value,string name,object data) => value.GetType().GetField(name,Flags)!.SetValue(value,data);
    private static void I32(byte[] bytes,int offset,int value) => BitConverter.GetBytes(value).CopyTo(bytes,offset);
    private static void U64(byte[] bytes,int offset,ulong value) => BitConverter.GetBytes(value).CopyTo(bytes,offset);
    private static byte[] Empty(bool afterBegin=false)
    {
        var bytes=new byte[72]; I32(bytes,20,-1);I32(bytes,24,-1);I32(bytes,40,74);U64(bytes,56,1);
        if(afterBegin) {I32(bytes,0,1);for(int offset=4;offset<=16;offset+=4) I32(bytes,offset,-1);}
        return bytes;
    }
    private static object Project(byte[]? bytes,object? idle=null) => Call("ProjectGuiState",bytes,idle??New("GuiIdle"))!;
    private static string Values(object value) => (string)Owner().Assembly.GetType("HollowKnightTAS.Runtime.FullRun.ResidentAssetDataWire",true)!
        .GetMethod("Values",Flags)!.Invoke(null,new[]{value})!;
    private static object Row(object gui)
    {
        var row=New("Data");Set(row,"Owner","actual-instance");Set(row,"Type",Call("Meta",typeof(UnityEngine.MonoBehaviour))!);
        Set(row,"ReferenceVtable",123);Set(row,"CacheMethodsMatch",true);Set(row,"Gui",gui);return row;
    }
    [TestMethod]
    public void AbsentConstructorAndPostGuiEmptyHaveIdenticalFullPortableValuesAndComparison()
    {
        var absent=Project(null);var initial=Project(Empty());var bytes=Empty(true);
        U64(bytes,32,0x12345678);U64(bytes,56,128);bytes[29]=0xa5;bytes[44]=0x77;bytes[68]=0xcc;
        var idle=New("GuiIdle");Set(idle,"Present",true);Set(idle,"EternalPresent",true);Set(idle,"UniqueId",5913);
        var retained=Project(bytes,idle);
        Assert.AreEqual(Values(absent),Values(initial));Assert.AreEqual(Values(absent),Values(retained));
        var a=Row(absent);var b=Row(retained);Call("RequireEqual",a,b);Call("RequireComplete",a);Call("RequireComplete",b);
        // Full packet encoding uses the same DTO, not an ad-hoc K3 filter.
        Assert.AreEqual(Values(a),Values(b));
        Assert.AreEqual(Newtonsoft.Json.JsonConvert.SerializeObject(a),Newtonsoft.Json.JsonConvert.SerializeObject(b));
        Assert.IsFalse(Values(b).Contains("5913"));Assert.IsFalse(Values(b).Contains("305419896"));
        Set(b,"Name","changed");Assert.AreNotEqual(Values(a),Values(b));
    }
    [TestMethod]
    public void NonemptyIdsCursorAndFocusReferencesRemainSpecificGaps()
    {
        var bytes=Empty();U64(bytes,32,4096);U64(bytes,48,1);U64(bytes,56,2);
        var data=Project(bytes);CollectionAssert.Contains((string[])Get(data,"RequiredGaps"),"mono/gui-control-id-aliases-uncovered");
        bytes=Empty();I32(bytes,64,1);data=Project(bytes);CollectionAssert.Contains((string[])Get(data,"RequiredGaps"),"mono/gui-control-id-cursor-active");
        bytes=Empty();I32(bytes,20,17);data=Project(bytes);CollectionAssert.Contains((string[])Get(data,"RequiredGaps"),"mono/gui-control-id-focus-reference-uncovered");
        bytes=Empty();I32(bytes,0,3);data=Project(bytes);Assert.AreEqual("uncovered",Get(data,"State"));
    }
    [TestMethod]
    public void GlobalDispatchAndFocusAreIndependentlyRequiredEvenForAbsentLocalState()
    {
        foreach(string name in new[]{"Depth","Keyboard","HotControl"}) {
            var idle=New("GuiIdle");Set(idle,name,1);Assert.AreEqual("uncovered",Get(Project(null,idle),"State"));
        }
        foreach(string name in new[]{"CurrentObject","DomainReloading","Present"}) {
            var idle=New("GuiIdle");Set(idle,name,true);Assert.AreEqual("uncovered",Get(Project(null,idle),"State"));
        }
    }
    [TestMethod]
    public void LegacyPresenceWithoutTypedProofAndMixedRepresentationsCannotQualify()
    {
        var row=Row(Project(null));Set(row,"Gui",null!);Set(row,"GuiState",true);
        Set(row,"CoverageGaps",Call("RequiredGaps",row)!);
        Assert.ThrowsExactly<TargetInvocationException>(()=>Call("RequireComplete",row));
        Set(row,"GuiState",false);Set(row,"CoverageGaps",Call("RequiredGaps",row)!);
        Assert.ThrowsExactly<TargetInvocationException>(()=>Call("RequireComplete",row));
        Set(row,"Gui",Project(null));Set(row,"GuiState",true);Set(row,"CoverageGaps",Array.Empty<string>());
        Assert.ThrowsExactly<TargetInvocationException>(()=>Call("RequireComplete",row));
    }
    [TestMethod]
    public void LayoutAndSemanticProofCannotBeInventedOrPromotedFromUncovered()
    {
        foreach(Action<byte[]> corrupt in new Action<byte[]>[]{bytes=>bytes[28]=2,bytes=>I32(bytes,0,4),bytes=>I32(bytes,40,73),bytes=>U64(bytes,48,1)}) {
            var bytes=Empty();corrupt(bytes);Assert.ThrowsExactly<TargetInvocationException>(()=>Project(bytes));
        }
        var gui=Project(null);Set(gui,"Version",0);Assert.ThrowsExactly<TargetInvocationException>(()=>Call("ValidateGui",gui));
        gui=Project(null);Set(gui,"State","uncovered");Assert.ThrowsExactly<TargetInvocationException>(()=>Call("ValidateGui",gui));
    }
}
