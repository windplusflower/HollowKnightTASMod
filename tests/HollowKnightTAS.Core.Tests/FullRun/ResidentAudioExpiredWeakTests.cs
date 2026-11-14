using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentAudioExpiredWeakTests
{
    private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    private static object Get(object value,string field)=>value.GetType().GetField(field,Flags)!.GetValue(value)!;
    private static void Set(object value,string field,object data)=>value.GetType().GetField(field,Flags)!.SetValue(value,data);
    private sealed class Api
    {
        internal readonly Type Type;
        internal Api()
        {
            var repo=new DirectoryInfo(AppContext.BaseDirectory);
            while(repo!=null&&!File.Exists(Path.Combine(repo.FullName,"HollowKnightTAS.sln"))) repo=repo.Parent;
            Assert.IsNotNull(repo);
            Type=Assembly.LoadFrom(Environment.GetEnvironmentVariable("HKTAS_TEST_RUNTIME_ASSEMBLY")??Path.Combine(repo.FullName,"src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
                .GetType("HollowKnightTAS.Runtime.FullRun.ResidentAudioState",true)!;
        }
        internal object New(string name)=>Activator.CreateInstance(Type.GetNestedType(name,Flags)!,true)!;
        internal object Call(string name,params object[] values)=>Type.GetMethod(name,Flags)!.Invoke(null,values)!;
        internal Array Array(string name,params object[] values)
        {
            var result=System.Array.CreateInstance(Type.GetNestedType(name,Flags)!,values.Length);
            for(int i=0;i<values.Length;i++)result.SetValue(values[i],i);return result;
        }
        internal object Data(out object source)
        {
            var data=New("Data");source=New("Source");Set(source,"Owner","source");Set(data,"Sources",Array("Source",source));return data;
        }
        internal object Channel(string alias) {var value=New("Channel");Set(value,"WeakOwner",alias.Length!=0);Set(value,"Alias",alias);return value;}
        internal void Invalid(string method,params object[] values)
            =>Assert.IsInstanceOfType<InvalidDataException>(Assert.ThrowsExactly<TargetInvocationException>(()=>Call(method,values)).InnerException);
    }
    private sealed class Memory : IDisposable
    {
        internal readonly Api Api=new Api();
        internal readonly IntPtr Vtable=new IntPtr(0x123400);
        internal readonly HashSet<IntPtr> Blocks=new();
        private readonly List<IntPtr> slots=new();
        private readonly List<IntPtr> owners=new();
        internal readonly Dictionary<IntPtr,int> Storage=new();
        internal IntPtr FailWrite, FailRetire;
        internal bool WriteOnlyFirstPointer;
        internal bool Assigned, ThrowReadback;
        internal int Creates, FailCreate;
        internal IntPtr Slot(IntPtr block=default) {var slot=Marshal.AllocHGlobal(8);Marshal.WriteIntPtr(slot,block);slots.Add(slot);return slot;}
        internal IntPtr Block(int count=1)
        {
            var block=Marshal.AllocHGlobal(24);var bytes=(byte[])Api.Call("ExpiredWeakBody",Vtable);
            BitConverter.GetBytes(count).CopyTo(bytes,12);Marshal.Copy(bytes,0,block,24);Blocks.Add(block);return block;
        }
        internal byte[] Read(IntPtr pointer,int count)
        {
            if(ThrowReadback&&Assigned&&slots.Contains(pointer))throw new InvalidDataException("write succeeded; readback failed");
            var result=new byte[count];Marshal.Copy(pointer,result,0,count);return result;
        }
        private void Drop(IntPtr block)
        {
            if(block==IntPtr.Zero)return;
            int count=Marshal.ReadInt32(block,12)-1;Assert.IsTrue(count>=0);
            if(count==0) {Assert.IsTrue(Blocks.Remove(block));Marshal.FreeHGlobal(block);}
            else Marshal.WriteInt32(block,12,count);
        }
        internal void Release(IntPtr slot) {Drop(Marshal.ReadIntPtr(slot));Marshal.WriteIntPtr(slot,IntPtr.Zero);}
        internal IntPtr Owner()
        {
            var pointer=Marshal.AllocHGlobal(1016);Marshal.Copy(new byte[1016],0,pointer,1016);owners.Add(pointer);return pointer;
        }
        internal IntPtr AllocateStorage(int bytes,int label)
        {
            var pointer=Marshal.AllocHGlobal(bytes);Marshal.Copy(new byte[bytes],0,pointer,bytes);Storage.Add(pointer,label);return pointer;
        }
        private void FreeStorage(IntPtr pointer,int label)
        {
            if(pointer==IntPtr.Zero)return;
            Assert.AreEqual(label,Storage[pointer]);Storage.Remove(pointer);Marshal.FreeHGlobal(pointer);
        }
        internal object Backend()
        {
            var backend=Api.New("WeakBackend");Set(backend,"Vtable",Vtable);
            Set(backend,"Read",new Func<IntPtr,int,byte[]>(Read));
            Set(backend,"Create",new Func<IntPtr>(()=>{if(++Creates==FailCreate)throw new InvalidDataException("allocation failed");return Block();}));
            Set(backend,"Release",new Action<IntPtr>(Release));
            Set(backend,"Assign",new Action<IntPtr,IntPtr>((destination,source)=>{
                var block=Marshal.ReadIntPtr(source);
                if(block!=IntPtr.Zero)Marshal.WriteInt32(block,12,Marshal.ReadInt32(block,12)+1);
                Drop(Marshal.ReadIntPtr(destination));Marshal.WriteIntPtr(destination,block);Assigned=true;
            }));
            Set(backend,"Write",new Action<IntPtr,byte[]>((pointer,bytes)=>{
                Marshal.Copy(bytes,0,pointer,pointer==FailWrite&&WriteOnlyFirstPointer?8:bytes.Length);
                if(pointer==FailWrite)throw new InvalidDataException("publication write completed then readback failed");
            }));
            Set(backend,"AllocateStorage",new Func<int,int,int,IntPtr>((bytes,label,_)=>AllocateStorage(bytes,label)));
            Set(backend,"FreeStorage",new Action<IntPtr,int>(FreeStorage));
            Set(backend,"Reserve",new Action<IntPtr,int>((header,capacity)=>{
                Assert.AreEqual(0L,Marshal.ReadInt64(header,16));Assert.AreEqual(IntPtr.Zero,Marshal.ReadIntPtr(header));
                int label=Marshal.ReadInt32(header,8);var storage=capacity==0?IntPtr.Zero:AllocateStorage(capacity*8,label);
                Marshal.WriteIntPtr(header,storage);Marshal.WriteInt64(header,24,capacity==0?1L:(long)capacity*2);
            }));
            Set(backend,"ClearArray",new Action<IntPtr>(header=>{
                var pointer=Marshal.ReadIntPtr(header);int size=checked((int)Marshal.ReadInt64(header,16));long flags=Marshal.ReadInt64(header,24);
                if(pointer!=IntPtr.Zero&&(flags&1)==0) {
                    for(int index=0;index<size;index++)Release(IntPtr.Add(pointer,index*8));
                    FreeStorage(pointer,Marshal.ReadInt32(header,8));
                }
                Marshal.WriteIntPtr(header,IntPtr.Zero);Marshal.WriteInt64(header,16,0);Marshal.WriteInt64(header,24,1);
                if(pointer!=IntPtr.Zero&&pointer==FailRetire)throw new InvalidDataException("old clear completed then cleanup failed");
            }));
            return backend;
        }
        internal object Plan(params (IntPtr Slot,string Alias)[] values)
        {
            var backend=Backend();
            var slots=values.Select(value=>{var slot=Api.New("WeakSlot");Set(slot,"Address",value.Slot);Set(slot,"Alias",value.Alias);return slot;}).ToArray();
            return Activator.CreateInstance(Api.Type.GetNestedType("ExpiredWeakPlan",Flags)!,Flags,null,new[]{Api.Array("WeakSlot",slots),backend},null)!;
        }
        internal static object Invoke(object plan,string method,params object?[] args)=>plan.GetType().GetMethod(method,Flags)!.Invoke(plan,args)!;
        public void Dispose()
        {
            foreach(var slot in slots)Marshal.FreeHGlobal(slot);
            foreach(var owner in owners)Marshal.FreeHGlobal(owner);
            foreach(var pointer in Storage.Keys)Marshal.FreeHGlobal(pointer);
            foreach(var block in Blocks)Marshal.FreeHGlobal(block);
        }
    }
    private sealed class Topology
    {
        internal readonly Memory Store;
        internal readonly object Desired, Before, Source;
        internal readonly IntPtr Owner, OldArray, OldNode, OldBlock;
        internal object Plan;
        internal Topology(Memory memory)
        {
            Store=memory;var api=memory.Api;Desired=api.Data(out var desired);Source=desired;Before=api.Data(out var before);
            Set(desired,"Main",api.Channel("shared"));Set(desired,"Cached",api.Array("Channel",api.Channel("shared"),api.Channel(""),api.Channel("other")));Set(desired,"CachedCapacity",4);
            Set(desired,"Providers",api.Array("Channel",api.Channel("shared")));Set(desired,"ProviderCapacity",2);
            Set(desired,"OneShots",api.Array("Channel",api.Channel("shared"),api.Channel(""),api.Channel("other")));
            Set(before,"Cached",api.Array("Channel",api.Channel("old")));Set(before,"CachedCapacity",1);Set(before,"OneShots",api.Array("Channel",api.Channel("old")));
            Owner=memory.Owner();OldArray=memory.AllocateStorage(8,33);OldNode=memory.AllocateStorage(32,31);OldBlock=memory.Block(2);
            Marshal.WriteIntPtr(OldArray,OldBlock);
            Marshal.WriteIntPtr(Owner,760,OldArray);Marshal.WriteInt32(Owner,768,33);Marshal.WriteInt64(Owner,776,1);Marshal.WriteInt64(Owner,784,2);
            Marshal.WriteInt32(Owner,984,37);Marshal.WriteInt64(Owner,1000,1);
            var head=IntPtr.Add(Owner,792);Marshal.WriteIntPtr(Owner,792,OldNode);Marshal.WriteIntPtr(Owner,800,OldNode);
            Marshal.WriteIntPtr(OldNode,0,head);Marshal.WriteIntPtr(OldNode,8,head);Marshal.WriteIntPtr(OldNode,16,OldNode);Marshal.WriteIntPtr(OldNode,24,OldBlock);
            Plan=Prepare();
        }
        internal object Prepare()=>Store.Api.Call("PrepareExpiredTopology",Desired,Before,new Func<string,IntPtr>(_=>Owner),Store.Backend());
        internal void Run() {Memory.Invoke(Plan,"Allocate");Memory.Invoke(Plan,"Publish");Memory.Invoke(Plan,"ReleaseAnchors",new object?[]{null});}
        internal IntPtr[] Nodes()
        {
            var result=new List<IntPtr>();var head=IntPtr.Add(Owner,792);var current=Marshal.ReadIntPtr(head,8);var previous=head;
            while(current!=head) {
                Assert.IsTrue(result.Count<10);Assert.AreEqual(previous,Marshal.ReadIntPtr(current));Assert.AreEqual(current,Marshal.ReadIntPtr(current,16));
                result.Add(current);previous=current;current=Marshal.ReadIntPtr(current,8);
            }
            Assert.AreEqual(previous,Marshal.ReadIntPtr(head));return result.ToArray();
        }
    }
    [TestMethod]
    public void ExpiredTopologyGrowsArraysAndRebuildsOneShotOrderWithSharedAliasesAndLocalLabels()
    {
        using var memory=new Memory();var topology=new Topology(memory);topology.Run();
        var shared=Marshal.ReadIntPtr(topology.Owner,856);var cached=Marshal.ReadIntPtr(topology.Owner,760);var providers=Marshal.ReadIntPtr(topology.Owner,976);var nodes=topology.Nodes();
        Assert.AreEqual(3L,Marshal.ReadInt64(topology.Owner,776));Assert.AreEqual(8L,Marshal.ReadInt64(topology.Owner,784));
        Assert.AreEqual(1L,Marshal.ReadInt64(topology.Owner,992));Assert.AreEqual(4L,Marshal.ReadInt64(topology.Owner,1000));
        Assert.AreEqual(33,memory.Storage[cached]);Assert.AreEqual(37,memory.Storage[providers]);
        Assert.AreEqual(shared,Marshal.ReadIntPtr(cached));Assert.AreEqual(shared,Marshal.ReadIntPtr(providers));Assert.AreEqual(shared,Marshal.ReadIntPtr(nodes[0],24));
        Assert.AreEqual(IntPtr.Zero,Marshal.ReadIntPtr(cached,8));Assert.AreEqual(IntPtr.Zero,Marshal.ReadIntPtr(nodes[1],24));
        Assert.AreEqual(Marshal.ReadIntPtr(cached,16),Marshal.ReadIntPtr(nodes[2],24));Assert.AreEqual(4,Marshal.ReadInt32(shared,12));
        Assert.IsFalse(memory.Blocks.Contains(topology.OldBlock));Assert.IsFalse(memory.Storage.ContainsKey(topology.OldArray));Assert.IsFalse(memory.Storage.ContainsKey(topology.OldNode));
    }
    [TestMethod]
    public void ExpiredTopologyShrinksContainersToTrueNullWithoutLeakingRetains()
    {
        using var memory=new Memory();var topology=new Topology(memory);var api=memory.Api;
        Set(topology.Source,"Main",api.Channel(""));Set(topology.Source,"Cached",api.Array("Channel"));Set(topology.Source,"CachedCapacity",0);
        Set(topology.Source,"Providers",api.Array("Channel"));Set(topology.Source,"ProviderCapacity",0);Set(topology.Source,"OneShots",api.Array("Channel"));
        topology.Plan=topology.Prepare();topology.Run();
        Assert.AreEqual(IntPtr.Zero,Marshal.ReadIntPtr(topology.Owner,760));Assert.AreEqual(IntPtr.Zero,Marshal.ReadIntPtr(topology.Owner,976));Assert.AreEqual(IntPtr.Zero,Marshal.ReadIntPtr(topology.Owner,856));
        Assert.AreEqual(0,topology.Nodes().Length);Assert.AreEqual(0,memory.Blocks.Count);Assert.AreEqual(0,memory.Storage.Count);
    }
    [TestMethod]
    public void UnpublishedTopologyDisposalReleasesPreparedContainersAndKeepsOldOwnersIntact()
    {
        using var memory=new Memory();var topology=new Topology(memory);Memory.Invoke(topology.Plan,"Allocate");
        Memory.Invoke(topology.Plan,"ReleaseAnchors",new object?[]{null});
        Assert.AreEqual(topology.OldArray,Marshal.ReadIntPtr(topology.Owner,760));Assert.AreEqual(topology.OldNode,topology.Nodes()[0]);
        Assert.AreEqual(2,Marshal.ReadInt32(topology.OldBlock,12));Assert.AreEqual(1,memory.Blocks.Count);Assert.AreEqual(2,memory.Storage.Count);
    }
    [TestMethod]
    public void ArrayHeaderWrittenThenFailurePreservesAdoptedStorageAndWeakReferences()
    {
        using var memory=new Memory();var topology=new Topology(memory);Memory.Invoke(topology.Plan,"Allocate");
        memory.FailWrite=IntPtr.Add(topology.Owner,760);
        var error=Assert.ThrowsExactly<TargetInvocationException>(()=>Memory.Invoke(topology.Plan,"Publish"));
        var adopted=Marshal.ReadIntPtr(topology.Owner,760);Assert.AreNotEqual(topology.OldArray,adopted);
        var weak=Marshal.ReadIntPtr(adopted);Memory.Invoke(topology.Plan,"ReleaseAnchors",error.InnerException);
        Assert.IsTrue(memory.Storage.ContainsKey(adopted));Assert.IsTrue(memory.Blocks.Contains(weak));Assert.IsTrue(memory.Storage.ContainsKey(topology.OldArray));
    }
    [TestMethod]
    public void ListFirstPointerWrittenThenFailurePreservesPotentiallyPublishedNodes()
    {
        using var memory=new Memory();var topology=new Topology(memory);Memory.Invoke(topology.Plan,"Allocate");
        memory.FailWrite=IntPtr.Add(topology.Owner,792);memory.WriteOnlyFirstPointer=true;
        var error=Assert.ThrowsExactly<TargetInvocationException>(()=>Memory.Invoke(topology.Plan,"Publish"));
        var tail=Marshal.ReadIntPtr(topology.Owner,792);Assert.AreNotEqual(topology.OldNode,tail);Assert.AreEqual(topology.OldNode,Marshal.ReadIntPtr(topology.Owner,800));
        var weak=Marshal.ReadIntPtr(tail,24);Memory.Invoke(topology.Plan,"ReleaseAnchors",error.InnerException);
        Assert.IsTrue(memory.Storage.ContainsKey(tail));Assert.IsTrue(memory.Blocks.Contains(weak));Assert.IsTrue(memory.Storage.ContainsKey(topology.OldNode));
    }
    [TestMethod]
    public void OldArrayRetirementFailureCannotFreeNewPublishedContainerOrReplacePrimary()
    {
        using var memory=new Memory();var topology=new Topology(memory);Memory.Invoke(topology.Plan,"Allocate");memory.FailRetire=topology.OldArray;
        var error=Assert.ThrowsExactly<TargetInvocationException>(()=>Memory.Invoke(topology.Plan,"Publish"));
        var adopted=Marshal.ReadIntPtr(topology.Owner,760);var weak=Marshal.ReadIntPtr(adopted);
        Memory.Invoke(topology.Plan,"ReleaseAnchors",error.InnerException);
        Assert.IsFalse(memory.Storage.ContainsKey(topology.OldArray));Assert.IsTrue(memory.Storage.ContainsKey(adopted));Assert.IsTrue(memory.Blocks.Contains(weak));
        StringAssert.Contains(error.InnerException!.Message,"old clear completed");
    }
    [TestMethod]
    public void BorrowedArrayOrChangedOneShotClosureFailsReadonlyBeforeAllocating()
    {
        using var memory=new Memory();var topology=new Topology(memory);int creates=memory.Creates;
        Marshal.WriteInt64(topology.Owner,784,3);
        Assert.ThrowsExactly<TargetInvocationException>(()=>topology.Prepare());Assert.AreEqual(creates,memory.Creates);
        Marshal.WriteInt64(topology.Owner,784,2);Marshal.WriteIntPtr(topology.OldNode,8,topology.OldNode);
        Assert.ThrowsExactly<TargetInvocationException>(()=>topology.Prepare());Assert.AreEqual(creates,memory.Creates);
    }
    [TestMethod]
    public void ExpiredBlockContainsExactlyTypedVtableLabelFreshLocalCountAndNullInstance()
    {
        var api=new Api();var bytes=(byte[])api.Call("ExpiredWeakBody",new IntPtr(0x12345678));
        Assert.AreEqual(24,bytes.Length);Assert.AreEqual(0x12345678L,BitConverter.ToInt64(bytes,0));
        Assert.AreEqual(31,BitConverter.ToInt32(bytes,8));Assert.AreEqual(1,BitConverter.ToInt32(bytes,12));Assert.AreEqual(0L,BitConverter.ToInt64(bytes,16));
    }
    [TestMethod]
    public void AliasSharingAcrossSlotsRetainsIndependentReferencesAndRetiresOldExpiredBlocks()
    {
        using var memory=new Memory();var old=memory.Block(2);var first=memory.Slot(old);var second=memory.Slot(old);var clear=memory.Slot();
        var plan=memory.Plan((first,"shared"),(second,"shared"),(clear,""));
        Memory.Invoke(plan,"Allocate");Assert.AreEqual(1,memory.Creates);
        Memory.Invoke(plan,"Publish");var created=Marshal.ReadIntPtr(first);
        Assert.AreEqual(created,Marshal.ReadIntPtr(second));Assert.AreEqual(IntPtr.Zero,Marshal.ReadIntPtr(clear));
        Assert.IsFalse(memory.Blocks.Contains(old));Assert.AreEqual(3,Marshal.ReadInt32(created,12));
        Memory.Invoke(plan,"Dispose");Assert.IsTrue(memory.Blocks.Contains(created));Assert.AreEqual(2,Marshal.ReadInt32(created,12));
        memory.Release(first);memory.Release(second);Assert.AreEqual(0,memory.Blocks.Count);
    }
    [TestMethod]
    public void AssignmentAlreadyWrittenThenReadbackFailureNeverFreesPublishedReference()
    {
        using var memory=new Memory();var slot=memory.Slot();var plan=memory.Plan((slot,"expired"));
        Memory.Invoke(plan,"Allocate");memory.ThrowReadback=true;
        var error=Assert.ThrowsExactly<TargetInvocationException>(()=>Memory.Invoke(plan,"Publish"));Assert.IsInstanceOfType<InvalidDataException>(error.InnerException);
        var published=Marshal.ReadIntPtr(slot);Assert.AreNotEqual(IntPtr.Zero,published);
        Memory.Invoke(plan,"ReleaseAnchors",error.InnerException);
        Assert.IsTrue(memory.Blocks.Contains(published));Assert.AreEqual(1,Marshal.ReadInt32(published,12));
        memory.ThrowReadback=false;memory.Release(slot);Assert.AreEqual(0,memory.Blocks.Count);
    }
    [TestMethod]
    public void UnpublishedAndPartiallyAllocatedPlansReleaseOnlyTheirTemporaryOwners()
    {
        using var memory=new Memory();var first=memory.Slot();var second=memory.Slot();var plan=memory.Plan((first,"a"),(second,"b"));memory.FailCreate=2;
        Assert.ThrowsExactly<TargetInvocationException>(()=>Memory.Invoke(plan,"Allocate"));
        Memory.Invoke(plan,"Dispose");Assert.AreEqual(0,memory.Blocks.Count);Assert.IsFalse(memory.Assigned);
        Assert.AreEqual(IntPtr.Zero,Marshal.ReadIntPtr(first));Assert.AreEqual(IntPtr.Zero,Marshal.ReadIntPtr(second));
    }
    [TestMethod]
    public void MainCachedAndProviderSlotsUseActualStorageAndRejectShapeOrLiveTransitions()
    {
        using var memory=new Memory();var api=memory.Api;var data=api.Data(out var source);var before=api.Data(out var target);
        Set(source,"Main",api.Channel("shared"));Set(source,"Cached",api.Array("Channel",api.Channel("shared")));Set(source,"CachedCapacity",1);
        Set(source,"Providers",api.Array("Channel",api.Channel("")));Set(source,"ProviderCapacity",1);
        Set(target,"Cached",api.Array("Channel",api.Channel("")));Set(target,"CachedCapacity",1);Set(target,"Providers",api.Array("Channel",api.Channel("")));Set(target,"ProviderCapacity",1);
        api.Call("RequireExpiredWeakShapes",data,before);
        var owner=Marshal.AllocHGlobal(1016);var cached=memory.Slot();var provider=memory.Slot();
        try {
            Marshal.Copy(new byte[1016],0,owner,1016);
            Marshal.WriteIntPtr(owner,792,IntPtr.Add(owner,792));Marshal.WriteIntPtr(owner,800,IntPtr.Add(owner,792));
            foreach(var (offset,storage) in new[]{(760,cached),(976,provider)}) {Marshal.WriteIntPtr(owner,offset,storage);Marshal.WriteInt64(owner,offset+16,1);Marshal.WriteInt64(owner,offset+24,2);}
            var backend=api.New("WeakBackend");Set(backend,"Vtable",memory.Vtable);Set(backend,"Read",new Func<IntPtr,int,byte[]>(memory.Read));
            Func<string,IntPtr> resolve=_=>owner;
            var plan=api.Call("PrepareExpiredTopology",data,before,resolve,backend);var main=((Array)Get(plan,"main")).GetValue(0)!;var arrays=((Array)Get(plan,"arrays")).Cast<object>().ToArray();
            CollectionAssert.AreEqual(new[]{IntPtr.Add(owner,856),cached,provider},new[]{(IntPtr)Get(main,"Address"),Marshal.ReadIntPtr((IntPtr)Get(arrays[0],"Header")),Marshal.ReadIntPtr((IntPtr)Get(arrays[1],"Header"))});
            CollectionAssert.AreEqual(new[]{"shared","shared",""},new[]{(string)Get(main,"Alias"),((string[])Get(arrays[0],"Aliases"))[0],((string[])Get(arrays[1],"Aliases"))[0]});
            Set(target,"ProviderCapacity",2);api.Invalid("PrepareExpiredTopology",data,before,resolve,backend);Set(target,"ProviderCapacity",1);
            Set(Get(source,"Main"),"LiveInstance",true);api.Call("RequireExpiredWeakShapes",data,before);Set(data,"Version",3);api.Invalid("RequireExpiredWeakShapes",data,before);Set(data,"Version",5);Set(Get(source,"Main"),"LiveInstance",false);
            Set(source,"OneShots",api.Array("Channel",api.Channel("expired")));api.Call("RequireExpiredWeakShapes",data,before);
        } finally {Marshal.FreeHGlobal(owner);}
    }
    [TestMethod]
    public void ReadonlyPreflightRejectsLiveOldBlocksAndStaleSlotsBeforeAllocation()
    {
        using var memory=new Memory();var block=memory.Block();var slot=memory.Slot(block);
        Marshal.WriteIntPtr(block,0,new IntPtr(42));
        Assert.ThrowsExactly<TargetInvocationException>(()=>memory.Plan((slot,"expired")));Assert.AreEqual(0,memory.Creates);
        Marshal.WriteIntPtr(block,0,memory.Vtable);Marshal.WriteIntPtr(block,16,new IntPtr(42));Assert.IsNotNull(memory.Plan((slot,"expired")));Marshal.WriteIntPtr(block,16,IntPtr.Zero);var plan=memory.Plan((slot,"expired"));
        Marshal.WriteIntPtr(slot,IntPtr.Zero);
        Assert.ThrowsExactly<TargetInvocationException>(()=>Memory.Invoke(plan,"Allocate"));Assert.AreEqual(0,memory.Creates);
        Memory.Invoke(plan,"Dispose");
    }
    [TestMethod]
    public void ExpiredCoverageRemainsTypedAndDoesNotAdmitLiveOneShotOwners()
    {
        var api=new Api();var data=api.Data(out var source);var expired=api.Channel("expired");Set(source,"Main",expired);
        var gaps=(string[])api.Call("RequiredCoverageGaps",data);Assert.IsFalse(gaps.Contains("audio-native-channel-instance-graph"));
        Set(data,"Version",3);CollectionAssert.Contains((string[])api.Call("RequiredCoverageGaps",data),"audio-legacy-weak-without-typed-voice");Set(data,"Version",4);
        Set(expired,"LiveInstance",true);Assert.IsFalse(((string[])api.Call("RequiredCoverageGaps",data)).Contains("audio-native-channel-instance-graph"));Set(expired,"LiveInstance",false);
        var shot=api.Channel("expired");Set(source,"OneShots",api.Array("Channel",shot));Assert.IsFalse(((string[])api.Call("RequiredCoverageGaps",data)).Contains("audio-one-shot-native-owner-list"));
        Set(shot,"LiveInstance",true);Assert.IsFalse(((string[])api.Call("RequiredCoverageGaps",data)).Contains("audio-one-shot-native-owner-list"));
    }
}
