using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace HollowKnightTAS.Core.Tests.FullRun;
[TestClass, DoNotParallelize]
public sealed class ResidentAudioBackendControlTests
{
    private const BindingFlags Flags=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private static byte[] currentReceipt=Array.Empty<byte>();
    private static int queryResult=664;
    private static uint seenGroupCount;
    private static List<string> order=new();
    private static Type Owner()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root!=null && !File.Exists(Path.Combine(root.FullName,"HollowKnightTAS.sln"))) root=root.Parent;
        return Assembly.LoadFrom(Environment.GetEnvironmentVariable("HKTAS_TEST_RUNTIME_ASSEMBLY") ?? Path.Combine(root!.FullName,"src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
            .GetType("HollowKnightTAS.Runtime.FullRun.ResidentAudioBackendControl",true)!;
    }
    private static object? Call(string name,params object?[] args) => Owner().GetMethod(name,Flags)!.Invoke(null,args);
    private static object Get(object value,string field) => value.GetType().GetField(field,Flags)!.GetValue(value)!;
    private static void Set(object value,string field,object data) => value.GetType().GetField(field,Flags)!.SetValue(value,data);
    private static void Put(byte[] bytes,int offset,uint value) => BitConverter.GetBytes(value).CopyTo(bytes,offset);
    private static byte[] Receipt()
    {
        var bytes=new byte[664];
        Put(bytes,0,0x31435641);Put(bytes,4,2);Put(bytes,8,664);Put(bytes,12,1);Put(bytes,20,8);Put(bytes,24,1);Put(bytes,28,15);
        for(int index=0;index<15;index++) Put(bytes,64+index*40,index>=8 && index<=11?2u:1u);
        Put(bytes,72,8);Put(bytes,32,1);Put(bytes,36,15);
        BitConverter.GetBytes(100UL).CopyTo(bytes,40);BitConverter.GetBytes(101UL).CopyTo(bytes,48);
        Put(bytes,64+12*40+12,50);return bytes;
    }
    private static Array Groups(params (long Pointer,string Owner,string Role)[] values)
    {
        var type=Owner().GetNestedType("GroupBinding",Flags)!;
        var result=Array.CreateInstance(type,values.Length);
        for(int index=0;index<values.Length;index++) {
            var binding=Activator.CreateInstance(type,true)!;
            Set(binding,"Opaque",new IntPtr(values[index].Pointer));Set(binding,"Owner",values[index].Owner);Set(binding,"Role",values[index].Role);
            result.SetValue(binding,index);
        }
        return result;
    }
    private static object Decode(byte[]? bytes=null,Array? groups=null) => Call("Decode",bytes??Receipt(),groups??Groups((0x1234,"source-a","dry")))!;
    private static void Reject(Action action) => Assert.ThrowsExactly<TargetInvocationException>(action);
    private static int Query(IntPtr voice,IntPtr system,IntPtr groups,uint count,IntPtr output,uint bytes)
    {
        order.Add("query");Assert.AreEqual(new IntPtr(57),voice);Assert.AreEqual(new IntPtr(77),system);Assert.AreEqual(664u,bytes);
        seenGroupCount=count;
        Assert.AreEqual(new IntPtr(0x1234),Marshal.ReadIntPtr(groups));
        Marshal.Copy(currentReceipt,0,output,currentReceipt.Length);
        return queryResult;
    }
    private static object Capture(Array groups,Action guard)
    {
        var callback=Delegate.CreateDelegate(Owner().GetNestedType("NativeQuery",Flags)!,typeof(ResidentAudioBackendControlTests).GetMethod(nameof(Query),Flags)!);
        return Call("CaptureCore",new IntPtr(57),new IntPtr(77),groups,guard,callback,new Action<object>(_=>{}))!;
    }
    [TestMethod]
    public void InactiveCreationClockHistoryIsDiagnosticOnlyAndPortableDelayHasNoAbsoluteStamp()
    {
        var first=Receipt();var second=Receipt();
        BitConverter.GetBytes((1UL<<40)+100).CopyTo(second,40);BitConverter.GetBytes((1UL<<40)+101).CopyTo(second,48);
        Put(second,64+12*40+16,256);
        var data=Decode(first);var other=Decode(second);Call("RequireComplete",data);Call("RequireComplete",other);
        Assert.AreEqual(1,Get(data,"DelayKind"));Assert.AreEqual(JsonConvert.SerializeObject(data),JsonConvert.SerializeObject(other));
        Assert.IsTrue(((uint[])Get(((Array)Get(data,"Fields")).GetValue(12)!,"Values")).All(value=>value==0));
        var diagnostic=Call("DecodeDelayDiagnostic",second)!;
        Assert.AreEqual((1UL<<40)+100,Get(diagnostic,"FirstClock"));Assert.AreEqual(256u,((uint[])Get(diagnostic,"RawDelay"))[2]);
        CollectionAssert.AreEqual(first,Receipt(),"Projection never clears the native receipt or real engine timing fields.");
    }
    [TestMethod]
    public void ScheduledTimingRemainsUnsupportedAndEveryRawWordIsRetainedOnlyForDiagnostics()
    {
        var bytes=Receipt();Put(bytes,32,2);Put(bytes,64+12*40,3);Put(bytes,64+12*40+12,200);
        var data=Decode(bytes);Assert.AreEqual(2,Get(data,"DelayKind"));Reject(()=>Call("RequireComplete",data));
        CollectionAssert.Contains((string[])Call("RequiredGaps",data)!,"audio-backend-delay-scheduled-unsupported");
        var diagnostic=Call("DecodeDelayDiagnostic",bytes)!;Assert.AreEqual(200u,((uint[])Get(diagnostic,"RawDelay"))[1]);
        Assert.IsTrue(((uint[])Get(((Array)Get(data,"Fields")).GetValue(12)!,"Values")).All(value=>value==0));
        foreach(int word in new[]{0,3,4,5,6,7}) {
            var invalid=Receipt();Put(invalid,64+12*40+8+word*4,1);Reject(()=>Decode(invalid));
        }
    }
    [TestMethod]
    public void MissingProofBackwardClockAndNativeIdentityErrorsCannotQualifyAnInactiveKind()
    {
        foreach(uint mask in new[]{0u,1u,3u,7u}) {
            var bytes=Receipt();Put(bytes,36,mask);var data=Decode(bytes);
            Assert.AreEqual(0,Get(data,"DelayKind"));Reject(()=>Call("RequireComplete",data));
        }
        var backwards=Receipt();BitConverter.GetBytes(99UL).CopyTo(backwards,48);Reject(()=>Call("RequireComplete",Decode(backwards)));
        foreach(int result in new[]{-5,-6,-7,0x24}) {
            var bytes=Receipt();Put(bytes,12,0);Put(bytes,16,unchecked((uint)result));Put(bytes,36,0);
            var data=Decode(bytes);Assert.AreEqual(result,Get(data,"Result"));Reject(()=>Call("RequireComplete",data));
        }
        var legacy=Receipt();Put(legacy,4,1);Reject(()=>Decode(legacy));Reject(()=>Call("Decode",new byte[640],Groups()));
        var oldBody=Decode();Set(oldBody,"Version",1);Reject(()=>Call("RequireComplete",oldBody));
    }
    [TestMethod]
    public void DelayDiagnosticsSurviveDecodeOrPostQueryGuardFailureWithoutChangingPrimary()
    {
        currentReceipt=Receipt();queryResult=664;order=new();var observations=new List<object>();
        var callback=Delegate.CreateDelegate(Owner().GetNestedType("NativeQuery",Flags)!,typeof(ResidentAudioBackendControlTests).GetMethod(nameof(Query),Flags)!);
        int guards=0;
        Reject(()=>Call("CaptureCore",new IntPtr(57),new IntPtr(77),Groups((0x1234,"source-a","dry")),
            (Action)(()=>{if(++guards==2)throw new InvalidOperationException("primary identity error");}),callback,new Action<object>(observations.Add)));
        Assert.AreEqual(1,observations.Count);Assert.AreEqual(100UL,Get(observations[0],"FirstClock"));
        observations.Clear();Put(currentReceipt,64+12*40+12,200);
        Reject(()=>Call("CaptureCore",new IntPtr(57),new IntPtr(77),Groups((0x1234,"source-a","dry")),(Action)(()=>{}),callback,new Action<object>(observations.Add)));
        Assert.AreEqual(1,observations.Count);Assert.AreEqual(200u,((uint[])Get(observations[0],"RawDelay"))[1]);
        currentReceipt=Receipt();
        var data=Call("CaptureCore",new IntPtr(57),new IntPtr(77),Groups((0x1234,"source-a","dry")),(Action)(()=>{}),callback,new Action<object>(_=>throw new IOException("diagnostic failure")))!;
        Call("RequireComplete",data);
    }
    [TestMethod]
    public void PortableGroupAliasesReplaceTheLocalIndexAndDuplicateOpaqueInputsAreMerged()
    {
        var data=Decode(groups:Groups((0x1234,"source-b","wet"),(0x1234,"source-a","dry")));
        Call("RequireComplete",data);
        var uses=(Array)Get(data,"GroupUses");Assert.AreEqual(2,uses.Length);
        Assert.AreEqual("source-a",Get(uses.GetValue(0)!,"Owner"));
        var fields=(Array)Get(data,"Fields");Assert.IsTrue(((uint[])Get(fields.GetValue(14)!,"Values")).All(value=>value==0));
        var bad=Receipt();Put(bad,64+14*40+8,1);Reject(()=>Decode(bad));
        Put(bad,64+14*40+8,0);Put(bad,64+14*40+12,0x1234);Reject(()=>Decode(bad));
    }
    [TestMethod]
    public void CoherentDoesNotMeanAllFieldCallsSucceededAndOriginalErrorsRemainVisible()
    {
        var bytes=Receipt();Put(bytes,64+1*40,4);Put(bytes,64+1*40+4,0x24);
        var data=Decode(bytes);Assert.AreEqual(true,Get(data,"Coherent"));
        var field=((Array)Get(data,"Fields")).GetValue(1)!;Assert.AreEqual(0x24,Get(field,"Error"));
        Reject(()=>Call("RequireComplete",data));
        var gaps=(string[])Call("RequiredGaps",data)!;Assert.IsTrue(gaps.Any(gap=>gap.Contains("error-36")));
    }
    [TestMethod]
    public void ReceiptAndTypedBodyVersionsAndMissingBodiesFailClosed()
    {
        foreach(int offset in new[]{0,4,8,28}) {
            var bytes=Receipt();Put(bytes,offset,0);Reject(()=>Decode(bytes));
        }
        Reject(()=>Call("Decode",new byte[663],Groups()));
        Reject(()=>Call("RequireComplete",new object?[]{null}));
        var data=Decode();Set(data,"Version",0);Reject(()=>Call("RequireComplete",data));
    }
    [TestMethod]
    public void NARequiresActual2DAnd3DValuesCannotMasqueradeAs2D()
    {
        var bytes=Receipt();Put(bytes,20,0);Put(bytes,72,0);Reject(()=>Decode(bytes));
        bytes=Receipt();Put(bytes,64+8*40,1);Reject(()=>Decode(bytes));
        bytes=Receipt();Put(bytes,64+1*40,2);Reject(()=>Decode(bytes));
        bytes=Receipt();Put(bytes,20,0x10);Put(bytes,72,0x10);
        for(int index=8;index<=11;index++) Put(bytes,64+index*40,1);
        Call("RequireComplete",Decode(bytes));
    }
    [TestMethod]
    public void ReverbConnectionPresenceIsUnsupportedAndNeverAPointerPayload()
    {
        var bytes=Receipt();Put(bytes,64+4*40,3);Put(bytes,64+4*40+8+12,1);
        var data=Decode(bytes);Call("Validate",data);Reject(()=>Call("RequireComplete",data));
        Put(bytes,64+4*40,1);Reject(()=>Decode(bytes));
        bytes=Receipt();Put(bytes,64+4*40+8+16,0xdeadbeef);Reject(()=>Decode(bytes));
    }
    [TestMethod]
    public void CapturePinsOnlyDeduplicatedTemporaryInputsAndGuardsBothSidesOfNativeCall()
    {
        currentReceipt=Receipt();queryResult=664;order=new();
        var data=Capture(Groups((0x1234,"source-a","dry"),(0x1234,"source-b","wet")),()=>order.Add("guard"));
        Assert.AreEqual(1u,seenGroupCount);CollectionAssert.AreEqual(new[]{"guard","query","guard"},order);
        Call("RequireComplete",data);
        queryResult=0;order=new();Reject(()=>Capture(Groups((0x1234,"source-a","dry")),()=>order.Add("guard")));
        CollectionAssert.AreEqual(new[]{"guard","query","guard"},order);
        queryResult=664;
        int guards=0;Reject(()=>Capture(Groups((0x1234,"source-a","dry")),()=> {if(++guards==2) throw new InvalidOperationException("identity changed");}));
    }
    [TestMethod]
    public void SourceRoleCannotAliasDifferentGroupsAndProductionCaptureRequiresRealLease()
    {
        Reject(()=>Decode(groups:Groups((0x1234,"source-a","dry"),(0x5678,"source-a","dry"))));
        Reject(()=>Decode(groups:Groups((0,"source-a","dry"))));
        Reject(()=>Decode(groups:Groups((0x1234,"source-a","unknown"))));
        Reject(()=>Call("Capture",new IntPtr(57),new IntPtr(77),Groups((0x1234,"source-a","dry")),(Action)(()=>{}),new Action<object>(_=>{})));
    }
}
