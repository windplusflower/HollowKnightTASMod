using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;
[TestClass, DoNotParallelize]
public sealed class ResidentClipDependencyTests
{
    private const BindingFlags Flags = BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private static Assembly Runtime()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName,"HollowKnightTAS.sln"))) root = root.Parent;
        return Assembly.LoadFrom(Environment.GetEnvironmentVariable("HKTAS_TEST_RUNTIME_ASSEMBLY") ?? Path.Combine(root!.FullName,"src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
    }
    private static Type Type(string name) => Runtime().GetType("HollowKnightTAS.Runtime.FullRun."+name,true)!;
    private static object Data()
    {
        var value = Activator.CreateInstance(Type("ResidentClipDependencyState").GetNestedType("Data",Flags)!,true)!;
        var cache=value.GetType().GetField("Cache",Flags)!.GetValue(value)!; Set(cache,"CurveStorageBytes",new ulong[6]);
        Set(value,"Flags",new byte[16]); Set(value,"Settings",new uint[6]); Set(value,"Bounds",new uint[6]);
        return value;
    }
    private static void Set(object value,string field,object data) => value.GetType().GetField(field,Flags)!.SetValue(value,data);
    private static object? Call(string owner,string name,params object[] args) => Type(owner).GetMethod(name,Flags)!.Invoke(null,args);
    private static void Reject(Action action) => Assert.Throws<TargetInvocationException>(action);
    [TestMethod]
    public void PortableAssetValuesExcludeOnlyLocalClipWitnessesWithoutMutatingPackage()
    {
        var source = Data();
        var target = Data();
        Set(target, "ObserverCount", 2);
        var cache = target.GetType().GetField("Cache", Flags)!.GetValue(target)!;
        Set(cache, "ConstantPresent", true);
        Set(cache, "ConstantBytes", 2400u);
        Set(cache, "CurveStorageBytes", new ulong[] { 64, 0, 0, 0, 0, 0 });
        var snapshotType = Type("ResidentNativeAssetState").GetNestedType("Snapshot", Flags)!;
        Array Snapshots(object clip)
        {
            var snapshot = Activator.CreateInstance(snapshotType, true)!;
            Set(snapshot, "Kind", "AnimationClip");
            Set(snapshot, "Binding", "clip");
            Set(snapshot, "Clip", clip);
            var snapshots = Array.CreateInstance(snapshotType, 1);
            snapshots.SetValue(snapshot, 0);
            return snapshots;
        }
        var sourceRows = Snapshots(source);
        var targetRows = Snapshots(target);
        string Wire(Array rows, bool portable) => (string)Call("ResidentAssetDataWire", portable ? "PortableValues" : "Values", (object)rows)!;
        var packageBefore = Wire(targetRows, false);
        Call("ResidentClipDependencyState", "RequireEquivalent", source, target);
        Assert.AreEqual(Wire(sourceRows, true), Wire(targetRows, true));
        Assert.AreNotEqual(Wire(sourceRows, false), packageBefore);
        Assert.AreEqual(packageBefore, Wire(targetRows, false));
        Set(target, "SampleRate", 123u);
        Assert.AreNotEqual(Wire(sourceRows, true), Wire(targetRows, true));
        Set(target, "SampleRate", 0u);
        Set(cache, "ConstantBytes", 1u);
        Reject(() => Wire(targetRows, true));
    }
    [TestMethod]
    public void EqualDependencyAcceptsButScalarChangesReject()
    {
        var source=Data(); var target=Data();
        Call("ResidentClipDependencyState","RequireEquivalent",source,target);
        var cache=target.GetType().GetField("Cache",Flags)!.GetValue(target)!;
        Set(cache,"CurveStorageBytes",new ulong[]{64,0,0,0,0,0});
        Set(cache,"ConstantPresent",true); Set(cache,"ConstantBytes",2400u);
        Call("ResidentClipDependencyState","RequireEquivalent",source,target);
        Set(target,"SampleRate",123u);
        Reject(()=>Call("ResidentClipDependencyState","RequireEquivalent",source,target));
    }
    [TestMethod]
    public void ReferenceAliasAndMissingCoverageReject()
    {
        var source=Data(); var target=Data();
        Set(target,"AdditiveClip","other-owner");
        Reject(()=>Call("ResidentClipDependencyState","RequireEquivalent",source,target));
        Set(target,"AdditiveClip",""); Set(target,"RequiredGaps",new[]{"clip-animationasset-observer-directory-not-covered"});
        Reject(()=>Call("ResidentClipDependencyState","RequireEquivalent",source,target));
    }
    [TestMethod]
    public void UnknownFlagsAndWrapModesReject()
    {
        var data=Data(); Set(data,"WrapMode",3u);
        Reject(()=>Call("ResidentClipDependencyState","Validate",data));
        Set(data,"WrapMode",0u); var flags=new byte[16]; flags[15]=2; Set(data,"Flags",flags);
        Reject(()=>Call("ResidentClipDependencyState","Validate",data));
    }
    [TestMethod]
    public void CleanReceiptRequiresHeldGateAndEveryWitness()
    {
        var bytes=new byte[64];
        void U32(int at,uint value) => BitConverter.GetBytes(value).CopyTo(bytes,at);
        void U64(int at,ulong value) => BitConverter.GetBytes(value).CopyTo(bytes,at);
        U32(0,0x314c4343);U32(4,1);U32(8,64);U32(12,7);U64(16,1);U64(24,5);U32(56,1);U32(60,1);
        Assert.AreEqual(true,Call("ResidentClipEligibility","IsCleanReceipt",1,bytes));
        foreach (int field in new[]{0,4,8,12,16,24,56,60}) {
            var changed=(byte[])bytes.Clone(); changed[field]=0;
            Assert.AreEqual(false,Call("ResidentClipEligibility","IsCleanReceipt",1,changed),"field "+field);
        }
        U32(48,101);Assert.AreEqual(false,Call("ResidentClipEligibility","IsCleanReceipt",1,bytes));
        U32(48,0);Assert.AreEqual(false,Call("ResidentClipEligibility","IsCleanReceipt",2,bytes));
        U32(52,1);Assert.AreEqual(false,Call("ResidentClipEligibility","IsCleanReceipt",1,bytes));
    }
    [TestMethod]
    public void MissingBackendScopeCannotPretendClean()
    {
        Reject(()=>Call("ResidentClockBackend","RequireClipToken"));
        Exception? failure=null;
        var thread=new System.Threading.Thread(()=> {
            try { Call("ResidentClockBackend","RequireClipToken"); } catch(Exception error) { failure=error; }
        });
        thread.Start();thread.Join();Assert.IsInstanceOfType(failure,typeof(TargetInvocationException));
    }
    [TestMethod]
    public void VersionFourClipRequiresPayloadAndExactSharedDeclaration()
    {
        object New(string owner,string nested) => Activator.CreateInstance(Type(owner).GetNestedType(nested,Flags)!,true)!;
        object Get(object value,string name) => value.GetType().GetField(name,Flags)!.GetValue(value)!;
        void Rows(object owner,string field,params object[] values)
        {
            var info=owner.GetType().GetField(field,Flags)!;
            var array=Array.CreateInstance(info.FieldType.GetElementType()!,values.Length);
            for(int index=0;index<values.Length;index++) array.SetValue(values[index],index);
            info.SetValue(owner,array);
        }
        var record=New("ResidentAssetBindings","AssetRecord");
        var metadata=Type("ResidentObjectDirectory").GetNestedType("TypeRecord",Flags)!.GetMethod("Capture",Flags)!.Invoke(null,new object[]{Assembly.LoadFrom("D:/SteamLibrary/steamapps/common/Hollow Knight/hollow_knight_Data/Managed/UnityEngine.AnimationModule.dll").GetType("UnityEngine.AnimationClip",true)!})!;
        Set(record,"Type",metadata); Set(record,"Kind","serialized"); Set(record,"SourcePath","archive:/clip"); Set(record,"LocalIdentifier",1L);
        string key=(string)Call("ResidentAssetBindings","SerializedKey",metadata,"archive:/clip",1L)!;
        Set(record,"Binding",key);
        var state=New("ResidentNativeAssetState","Snapshot");
        foreach(string name in new[]{"Binding","Name","Type","HideFlags"}) Set(state,name,Get(record,name));
        Set(state,"Kind","AnimationClip"); Set(state,"Clip",Data());
        var pipeline=New("ResidentAssetPipeline","Data"); Rows(pipeline,"Manifest",record); Rows(pipeline,"Snapshots",state);
        Set(pipeline,"SharedMutable",new[]{key}); Call("ResidentAssetPipeline","Validate",pipeline);
        var animation=New("ResidentAnimatorState","Data");
        Call("ResidentAssetPipeline","ValidateAnimatorAssetDependencies",animation,pipeline);
        var clip=Get(state,"Clip"); Set(clip,"ObserverCount",1);
        Reject(()=>Call("ResidentAssetPipeline","ValidateAnimatorAssetDependencies",animation,pipeline));
        var graph=New("ResidentAnimatorState","Topology");
        var node=New("ResidentAnimatorState","Node");
        var observer=New("ResidentAnimatorState","ClipState");
        Set(observer,"Observed",key); Set(observer,"ObserverPeers",new[]{"0/playable/0/observer/472"});
        Set(node,"Clip",observer); Rows(graph,"Nodes",node); Rows(animation,"Graphs",graph);
        Call("ResidentAssetPipeline","ValidateAnimatorAssetDependencies",animation,pipeline);
        Set(observer,"ObserverPeers",new[]{"external-observer"});
        Reject(()=>Call("ResidentAssetPipeline","ValidateAnimatorAssetDependencies",animation,pipeline));
        Set(clip,"ObserverCount",0);
        Set(pipeline,"Version",3); Reject(()=>Call("ResidentAssetPipeline","Validate",pipeline));
        Set(pipeline,"Version",4); Set(pipeline,"SharedMutable",Array.Empty<string>());
        Reject(()=>Call("ResidentAssetPipeline","Validate",pipeline));
        Set(pipeline,"SharedMutable",new[]{key}); Rows(pipeline,"Snapshots");
        Reject(()=>Call("ResidentAssetPipeline","Validate",pipeline));
    }
    [TestMethod]
    public void BackendScopeRejectsNestedAndInactiveAndClearsAfterCleanupFailure()
    {
        var backendType=Type("ResidentClockBackend");
        var current=backendType.GetField("currentClipBackend",Flags)!;
        var backend=System.Runtime.Serialization.FormatterServices.GetUninitializedObject(backendType);
        backendType.GetField("ownerThread",Flags)!.SetValue(backend,System.Threading.Thread.CurrentThread.ManagedThreadId);
        backendType.GetField("closed",Flags)!.SetValue(backend,true);
        current.SetValue(null,backend);
        try {
            Reject(()=>Call("ResidentClockBackend","RequireClipToken"));
            var constructor=backendType.GetConstructors(Flags).Single();
            Reject(()=>constructor.Invoke(new object?[]{null,null,0,false}));
            // The synthetic boundary is absent; its cleanup throws, but the
            // finally must still remove the published scope.
            Reject(()=>backendType.GetMethod("Dispose",Flags)!.Invoke(backend,null));
            Assert.IsNull(current.GetValue(null));
        } finally { current.SetValue(null,null); }
    }

    [TestMethod]
    public void MoreThan4096SmallEventsRemainWithinTheExistingByteBudget()
    {
        var data=Data();
        var eventType=Type("ResidentClipDependencyState").GetNestedType("Event",Flags)!;
        var events=Array.CreateInstance(eventType,4097);
        for (int index=0; index<events.Length; index++) events.SetValue(Activator.CreateInstance(eventType,true),index);
        Set(data,"Events",events);
        Set(data,"ObserverCount",4097);
        Call("ResidentClipDependencyState","Validate",data);
    }

    [TestMethod]
    public void EveryForwardClipReferenceRequiresTypedAssetButLocalBindingDoesNot()
    {
        object New(string owner,string nested) => Activator.CreateInstance(Type(owner).GetNestedType(nested,Flags)!,true)!;
        void Rows(object owner,string field,params object[] values)
        {
            var info=owner.GetType().GetField(field,Flags)!;
            var array=Array.CreateInstance(info.FieldType.GetElementType()!,values.Length);
            for(int index=0;index<values.Length;index++) array.SetValue(values[index],index);
            info.SetValue(owner,array);
        }
        var pipeline=New("ResidentAssetPipeline","Data");
        var animation=New("ResidentAnimatorState","Data");
        var graph=New("ResidentAnimatorState","Topology");
        var node=New("ResidentAnimatorState","Node");
        var clip=New("ResidentAnimatorState","ClipState");
        Set(node,"Clip",clip); Rows(graph,"Nodes",node); Rows(animation,"Graphs",graph);
        Set(clip,"Binding","local-binding-table-key");
        Call("ResidentAssetPipeline","ValidateAnimatorAssetDependencies",animation,pipeline);
        foreach(string field in new[]{"Asset","Clip","Observed","Constant"}) {
            Set(clip,field,"missing-clip");
            Reject(()=>Call("ResidentAssetPipeline","ValidateAnimatorAssetDependencies",animation,pipeline));
            Set(clip,field,"");
        }
        var row=New("ResidentAnimatorState","Row"); Rows(animation,"Rows",row);
        Set(row,"BinderClip","missing-clip"); Reject(()=>Call("ResidentAssetPipeline","ValidateAnimatorAssetDependencies",animation,pipeline));
        Set(row,"BinderClip",""); Set(row,"CachedClips",new[]{"missing-clip"});
        Reject(()=>Call("ResidentAssetPipeline","ValidateAnimatorAssetDependencies",animation,pipeline));
        Set(row,"CachedClips",Array.Empty<string>());
        var evaluation=((Array)row.GetType().GetField("Evaluation",Flags)!.GetValue(row)!).GetValue(0)!;
        var backing=evaluation.GetType().GetField("Backing",Flags)!.GetValue(evaluation)!;
        Set(backing,"Clips",new[]{"missing-clip"});
        Reject(()=>Call("ResidentAssetPipeline","ValidateAnimatorAssetDependencies",animation,pipeline));
    }
    [TestMethod]
    public void EveryControllerRequiresCapturedEmptyOrCompleteReciprocalObserverDirectory()
    {
        object New(string nested) => Activator.CreateInstance(Type("ResidentAnimatorState").GetNestedType(nested,Flags)!,true)!;
        void Rows(object owner,string field,params object[] values)
        {
            var info=owner.GetType().GetField(field,Flags)!;
            var array=Array.CreateInstance(info.FieldType.GetElementType()!,values.Length);
            for(int index=0;index<values.Length;index++) array.SetValue(values[index],index);
            info.SetValue(owner,array);
        }
        var animation=New("Data"); var asset=New("ControllerAsset"); Set(asset,"Binding","controller"); Rows(animation,"Assets",asset);
        Reject(()=>Call("ResidentAnimatorState","ValidateControllerAssetObservers",animation));
        Set(asset,"ObserverDirectoryCaptured",true); Call("ResidentAnimatorState","ValidateControllerAssetObservers",animation);
        Set(asset,"ObserverPeers",new[]{"external"}); Reject(()=>Call("ResidentAnimatorState","ValidateControllerAssetObservers",animation));
        var graph=New("Topology"); var node=New("Node"); Set(node,"Controller",0); Rows(graph,"Nodes",node); Rows(animation,"Graphs",graph);
        var player=New("Playable"); var dependency=New("ControllerDependency");
        Set(dependency,"ObservedAsset","controller"); Set(dependency,"ObserverPeers",new[]{"0/playable/0/observer/416"});
        Set(player,"Dependency",dependency); Rows(animation,"Playables",player);
        Set(asset,"ObserverPeers",new[]{"0/playable/0/observer/416"});
        Call("ResidentAnimatorState","ValidateControllerAssetObservers",animation);
        Set(dependency,"ObserverPeers",Array.Empty<string>());
        Reject(()=>Call("ResidentAnimatorState","ValidateControllerAssetObservers",animation));
    }

}
