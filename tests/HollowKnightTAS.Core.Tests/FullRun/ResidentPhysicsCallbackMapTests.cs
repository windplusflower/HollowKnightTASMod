using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace HollowKnightTAS.Core.Tests.FullRun;
[TestClass]
public sealed class ResidentPhysicsCallbackMapTests
{
    private const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    private static Type Load()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root!=null && !File.Exists(Path.Combine(root.FullName,"HollowKnightTAS.sln"))) root=root.Parent;
        Assert.IsNotNull(root);
        return Assembly.LoadFrom(Path.Combine(root.FullName,"src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
            .GetType("HollowKnightTAS.Runtime.FullRun.ResidentPhysicsCallbackMap",true)!;
    }
    private static void Set(object o,string n,object v)=>o.GetType().GetField(n,All)!.SetValue(o,v);
    private static object Call(Type t,string n,params object[] args)=>t.GetMethod(n,All)!.Invoke(null,args)!;
    private static object Data(Type t,int mask,int index=0)
    {
        var d=Activator.CreateInstance(t.GetNestedType("Data",All)!,true)!;
        Set(d,"Mask",mask);Set(d,"Count",1);Set(d,"Growth",(2*(mask/32+1))/3-1);
        var st=t.GetNestedType("Slot",All)!;
        var slots=Array.CreateInstance(st,mask/32+1);
        for(int i=0;i<slots.Length;i++) {var s=Activator.CreateInstance(st,true)!;Set(s,"Kind",i==index?0:1);if(i==index){Set(s,"KeyA","a");Set(s,"KeyB","b");}slots.SetValue(s,i);}
        Set(d,"Slots",slots);return d;
    }
    private static string Wire(object o)=>JsonSerializer.Serialize(o,o.GetType(),new JsonSerializerOptions{IncludeFields=true});
    [TestMethod]
    public void ActualHashAndTargetRebindingKeepPortableWireIndependentOfIds()
    {
        var t=Load();var d=Data(t,32);var a=Marshal.AllocHGlobal(16);var b=Marshal.AllocHGlobal(16);
        try {
            int sourceId=1; while(((uint)Call(t,"Hash",sourceId,29)&32)!=0)sourceId++;
            Marshal.WriteInt32(a,8,sourceId);Marshal.WriteInt32(b,8,29);
            Func<string,IntPtr> resolve=k=>k=="a"?a:b;
            var first=Call(t,"RebindHashes",d,resolve);
            int targetId=sourceId+1; while(((uint)Call(t,"Hash",targetId,899)&32)!=0)targetId++;
            Marshal.WriteInt32(a,8,targetId);Marshal.WriteInt32(b,8,899);
            var second=Call(t,"RebindHashes",d,resolve);
            Assert.AreEqual(Wire(first),Wire(second));
            StringAssert.Contains(Wire(first),"KeyA");Assert.IsFalse(Wire(first).Contains("Hash"));
            Assert.AreNotEqual(Call(t,"Hash",17,29),Call(t,"Hash",29,17));
        } finally {Marshal.FreeHGlobal(a);Marshal.FreeHGlobal(b);}
    }
    [TestMethod]
    public void ActualValidationRejectsBadKindExtentAndDuplicateCollisionIndex()
    {
        var t=Load();var d=Data(t,32);Call(t,"ValidateWire",d);
        Set(d,"Count",2);Assert.ThrowsExactly<TargetInvocationException>(()=>Call(t,"ValidateWire",d));
        d=Data(t,32);Set(d,"Header16",1);Assert.ThrowsExactly<TargetInvocationException>(()=>Call(t,"ValidateWire",d));
        d=Data(t,32);var slots=(Array)d.GetType().GetField("Slots",All)!.GetValue(d)!;
        Set(slots.GetValue(1)!,"Kind",7);Assert.ThrowsExactly<TargetInvocationException>(()=>Call(t,"ValidateWire",d));
                d=Data(t,32);slots=(Array)d.GetType().GetField("Slots",All)!.GetValue(d)!;
        Set(slots.GetValue(1)!,"Kind",0);Set(slots.GetValue(1)!,"KeyA","c");Set(slots.GetValue(1)!,"KeyB","d");Set(d,"Count",2);
        Func<int,Tuple<string,string>> rows=i=>Tuple.Create("a","b");
        Assert.ThrowsExactly<TargetInvocationException>(()=>Call(t,"ValidateRows",d,rows));
        d=Data(t,32);Func<int,Tuple<string,string>> row=i=>Tuple.Create("wrong","b");
        Assert.ThrowsExactly<TargetInvocationException>(()=>Call(t,"ValidateRows",d,row));
    }
    [TestMethod]
    public void TargetSignedInstanceIdOrderReversalIsRejectedBeforeMapMutation()
    {
        var t=Load();var a=Marshal.AllocHGlobal(16);var b=Marshal.AllocHGlobal(16);
        try {
            Marshal.WriteInt32(a,8,20);Marshal.WriteInt32(b,8,-10);
            Assert.ThrowsExactly<TargetInvocationException>(()=>Call(t,"ColliderHash",a,b));
            Marshal.WriteInt32(a,8,-20);Call(t,"ColliderHash",a,b);
        } finally {Marshal.FreeHGlobal(a);Marshal.FreeHGlobal(b);}
    }
    [TestMethod]
    public void IdleGapIsObservableForRowsButBlocksRestoreRebinding()
    {
        var t=Load();var d=Data(t,32);
        Set(d,"CoverageGaps",new[]{"unused callback map bytes need Canon"});
        Func<int,Tuple<string,string>> row=i=>Tuple.Create("a","b");
        Call(t,"ValidateRows",d,row);
        Assert.ThrowsExactly<TargetInvocationException>(()=>Call(t,"ValidateWire",d));
        Set(d,"CoverageGaps",new[]{"unknown gap"});
        Assert.ThrowsExactly<TargetInvocationException>(()=>Call(t,"ValidateRows",d,row));
    }
    [TestMethod]
    public void ActualTargetProbeRejectsVirginEmptyBeforeSavedLiveSlot()
    {
        var t=Load();var d=Data(t,96,1);var slots=(Array)d.GetType().GetField("Slots",All)!.GetValue(d)!;
        Set(slots.GetValue(0)!,"Kind",1);Set(slots.GetValue(1)!,"Hash",0u);
        Assert.ThrowsExactly<TargetInvocationException>(()=>Call(t,"ValidateLocal",d));
        Set(slots.GetValue(0)!,"Kind",2);Set(d,"Growth",0);Call(t,"ValidateLocal",d);
    }
}
