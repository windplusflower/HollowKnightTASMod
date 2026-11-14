using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentAudioPositionProbeTests
{
    private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    private static Type Load()
    {
        var repo=new DirectoryInfo(AppContext.BaseDirectory);
        while(repo!=null&&!File.Exists(Path.Combine(repo.FullName,"HollowKnightTAS.sln")))repo=repo.Parent;
        Assert.IsNotNull(repo);
        return Assembly.LoadFrom(Environment.GetEnvironmentVariable("HKTAS_TEST_RUNTIME_ASSEMBLY")??Path.Combine(repo.FullName,"src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
            .GetType("HollowKnightTAS.Runtime.FullRun.ResidentAudioPositionProbe",true)!;
    }
    private static object Get(object value,string field)=>value.GetType().GetField(field,Flags)!.GetValue(value)!;
    private static void Set(object value,string field,object data)=>value.GetType().GetField(field,Flags)!.SetValue(value,data);
    private static object Call(Type type,string name,params object[] args)=>type.GetMethod(name,Flags)!.Invoke(null,args)!;
    private sealed class Fixture
    {
        internal readonly Type Api=Load();
        internal readonly object Backend;
        internal readonly Array Candidates;
        internal readonly List<long> Queries=new();
        internal ulong Wall;
        internal int Reads, Requires, Sleeps;
        internal Fixture(int count=2)
        {
            Backend=Activator.CreateInstance(Api.GetNestedType("Backend",Flags)!,true)!;
            Set(Backend,"Available",true);Set(Backend,"WeakVtable",11L);Set(Backend,"InstanceVtable",22L);
            Set(Backend,"Require",(Action)(()=>Requires++));
            Set(Backend,"Wall",(Func<ulong>)(()=>Wall));
            Set(Backend,"Sleep",(Action<int>)(milliseconds=>{Sleeps++;Wall+=(ulong)milliseconds;}));
            Set(Backend,"ReadState",(Func<long,long,long[]>)((weak,instance)=>{Reads++;return new[]{11L,instance,22L,weak,instance+1000};}));
            Set(Backend,"Query",(Func<long,(int,uint)>)(instance=>{Queries.Add(instance);return (0,(uint)instance+99);}));
            var candidate=Api.GetNestedType("Candidate",Flags)!;Candidates=Array.CreateInstance(candidate,count);
            for(int i=0;i<count;i++) {
                var value=Activator.CreateInstance(candidate,true)!;
                Set(value,"Alias","voice/"+i);Set(value,"Weak",100L+i);Set(value,"Instance",10L+i);Candidates.SetValue(value,i);
            }
        }
        internal object Run()=>Call(Api,"Run",Candidates,Backend);
        internal static object Voice(object data,int at=0)=>((Array)Get(data,"Voices")).GetValue(at)!;
    }
    [TestMethod]
    public void MissingLeaseNoSystemAndBackendNeverQueryOrWaitAndCannotClaimSamePosition()
    {
        foreach(int mode in new[]{0,1,2}) {
            var fixture=new Fixture(1);
            if(mode==0)Set(fixture.Backend,"Available",false);
            if(mode==1)Set(fixture.Backend,"NoSystem",true);
            if(mode==2)Set(fixture.Backend,"ReadState",(Func<long,long,long[]>)((weak,instance)=>new[]{11L,instance,22L,weak,0L}));
            var data=fixture.Run();Assert.AreEqual(0,fixture.Queries.Count);Assert.AreEqual(0,fixture.Sleeps);
            Assert.IsNull(Get(Fixture.Voice(data),"SamePosition"));
            Assert.AreEqual(false,Get(Get(Fixture.Voice(data),"First"),"Attempted"));
        }
    }
    [TestMethod]
    public void TwoRoundsSampleTheEntireBatchInOrderSeparatedByRealWallInterval()
    {
        var fixture=new Fixture();var data=fixture.Run();
        CollectionAssert.AreEqual(new long[]{10,11,10,11},fixture.Queries);
        Assert.AreEqual(100UL,Get(data,"WaitElapsedMilliseconds"));Assert.AreEqual(100UL,Get(data,"ActualElapsedMilliseconds"));
        for(int i=0;i<2;i++) {
            var voice=Fixture.Voice(data,i);Assert.AreEqual(true,Get(voice,"SamePosition"));
            Assert.AreEqual(0UL,Get(Get(voice,"First"),"WallMilliseconds"));Assert.AreEqual(100UL,Get(Get(voice,"Second"),"WallMilliseconds"));
        }
        Assert.IsTrue(fixture.Requires>=16);
    }
    [TestMethod]
    public void GetterErrorAndPostQueryWeakExpiryPreventAnySecondQuery()
    {
        var error=new Fixture(1);
        Set(error.Backend,"Query",(Func<long,(int,uint)>)(instance=>{error.Queries.Add(instance);return (37,123u);}));
        var failed=Fixture.Voice(error.Run());Assert.AreEqual(1,error.Queries.Count);Assert.IsNull(Get(failed,"SamePosition"));
        Assert.AreEqual(37,Get(Get(failed,"First"),"ReturnCode"));
        StringAssert.Contains((string)Get(failed,"Reason"),"GetPositionPCM-error-37");
        var expired=new Fixture(1);bool live=true;
        Set(expired.Backend,"ReadState",(Func<long,long,long[]>)((weak,instance)=>live?new[]{11L,instance,22L,weak,instance+1000}:new[]{11L,0L,0L,0L,0L}));
        Set(expired.Backend,"Query",(Func<long,(int,uint)>)(instance=>{expired.Queries.Add(instance);live=false;return (0,123u);}));
        var changed=Fixture.Voice(expired.Run());Assert.AreEqual(1,expired.Queries.Count);Assert.AreEqual("weak-expired",Get(changed,"Reason"));Assert.IsNull(Get(changed,"SamePosition"));
    }
    [TestMethod]
    public void NativeWeakValidationDoesNotDereferenceExpiredReplacedOrUntypedTargets()
    {
        var type=Load();var weak=Marshal.AllocHGlobal(24);
        try {
            Marshal.Copy(new byte[24],0,weak,24);Marshal.WriteIntPtr(weak,new IntPtr(11));
            var expired=(long[])Call(type,"ReadNativeState",weak.ToInt64(),2L,11L);Assert.AreEqual(0L,expired[1]);
            Marshal.WriteIntPtr(weak,16,new IntPtr(1));
            var replaced=(long[])Call(type,"ReadNativeState",weak.ToInt64(),2L,11L);Assert.AreEqual(1L,replaced[1]);Assert.AreEqual(0L,replaced[2]);
            Marshal.WriteIntPtr(weak,new IntPtr(12));
            var untyped=(long[])Call(type,"ReadNativeState",weak.ToInt64(),1L,11L);Assert.AreEqual(0L,untyped[2]);
        } finally {Marshal.FreeHGlobal(weak);}
    }
    [TestMethod]
    public void StalledWallClockAndLostMasterLeaseAbortBeforeSecondRound()
    {
        var stalled=new Fixture(1);Set(stalled.Backend,"Sleep",(Action<int>)(_=>stalled.Sleeps++));
        var data=stalled.Run();Assert.AreEqual(100,stalled.Sleeps);Assert.AreEqual(1,stalled.Queries.Count);
        StringAssert.Contains((string)Get(data,"Reason"),"wall-clock interval");Assert.IsNull(Get(Fixture.Voice(data),"SamePosition"));
        var lost=new Fixture(1);bool valid=true;
        Set(lost.Backend,"Require",(Action)(()=>{if(!valid)throw new InvalidDataException("master identity changed");}));
        Set(lost.Backend,"Query",(Func<long,(int,uint)>)(instance=>{lost.Queries.Add(instance);valid=false;return (0,9u);}));
        var voice=Fixture.Voice(lost.Run());Assert.AreEqual(1,lost.Queries.Count);Assert.AreEqual(1,lost.Reads);
        StringAssert.Contains((string)Get(voice,"Reason"),"master identity changed");Assert.IsNull(Get(voice,"SamePosition"));
    }
    [TestMethod]
    public void CandidateCapIsExplicitDiagnosticTruncationWithoutChangingCoverage()
    {
        var fixture=new Fixture(33);var data=fixture.Run();Assert.AreEqual(true,Get(data,"Truncated"));
        Assert.AreEqual(33,Get(data,"CandidateCount"));Assert.AreEqual(32,((Array)Get(data,"Voices")).Length);
        Assert.AreEqual(64,fixture.Queries.Count);
        Assert.IsFalse(fixture.Api.GetMethods(Flags).Any(method=>method.Name.Contains("Stop")||method.Name.Contains("Seek")||method.Name.Contains("isPlaying")));
    }
}
