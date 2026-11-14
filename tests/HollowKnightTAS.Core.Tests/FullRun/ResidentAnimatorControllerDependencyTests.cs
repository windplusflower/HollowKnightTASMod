using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentAnimatorControllerDependencyTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static Type Load()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !File.Exists(Path.Combine(repo.FullName,"HollowKnightTAS.sln"))) repo = repo.Parent;
        Assert.IsNotNull(repo);
        return Assembly.LoadFrom(Environment.GetEnvironmentVariable("HKTAS_TEST_RUNTIME_ASSEMBLY")
            ?? Path.Combine(repo.FullName,"src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
            .GetType("HollowKnightTAS.Runtime.FullRun.ResidentAnimatorState",true)!;
    }
    private static object New(Type type, string name) => Activator.CreateInstance(type.GetNestedType(name,Flags)!,true)!;
    private static object Get(object value, string name) => value.GetType().GetField(name,Flags)!.GetValue(value)!;
    private static void Set(object value, string name, object? field) => value.GetType().GetField(name,Flags)!.SetValue(value,field);
    private static object? Call(Type type, string name, params object[] args) => type.GetMethod(name,Flags)!.Invoke(null,args);
    private static void Invalid(Action action)
        => Assert.IsInstanceOfType<InvalidDataException>(Assert.ThrowsExactly<TargetInvocationException>(action).InnerException);
    private static string[] Gaps(Type type, object player)
        => ((IEnumerable)Call(type,"ControllerDependencyGaps",player)!).Cast<string>().ToArray();
    private static void ArrayField(object owner, string name, params object[] values)
    {
        var field = owner.GetType().GetField(name,Flags)!;
        var array = Array.CreateInstance(field.FieldType.GetElementType()!,values.Length);
        for (int index = 0; index < values.Length; index++) array.SetValue(values[index],index);
        field.SetValue(owner,array);
    }
    private static object Player(Type type)
    {
        var player = New(type,"Playable");
        var dependency = New(type,"ControllerDependency");
        Set(player,"Dependency",dependency);
        Set(dependency,"Sender",0);
        Set(dependency,"SenderVtable",0x15d9030);
        Set(dependency,"ConstantAssetAlias",true);
        Set(dependency,"ObserverSelf",true);
        Set(dependency,"HeapVtable",0x15d5b60);
        Set(dependency,"BlockVtable",0x15d6cf8);
        Set(dependency,"MemoryVtable",0x15d8f00);
        Set(dependency,"HeapLabel",30);
        Set(dependency,"BlockLabel",30);
        return player;
    }

    [TestMethod]
    public void CompleteEmptyOwnerMatchesAndLegacySenderCloneAndAllocatorShapesRemainGated()
    {
        var type = Load();
        var source = Player(type);
        var target = Player(type);
        Call(type,"ValidateControllerDependency",source);
        Assert.AreEqual(0,Gaps(type,source).Length);
        Call(type,"RequireControllerDependenciesMatch",source,target);
        Set(target,"Dependency",null);
        CollectionAssert.Contains(Gaps(type,target),"controller-playable-mixer-and-behaviours");
        Invalid(() => Call(type,"RequireControllerDependenciesMatch",source,target));
        target = Player(type);
        var dependency = Get(target,"Dependency");
        Set(dependency,"SenderVtable",123);
        Invalid(() => Call(type,"RequireControllerDependenciesMatch",source,target));
        Set(dependency,"SenderVtable",0x15d9030);
        Set(dependency,"BehaviourObjects",new[] {"behaviour"});
        var storage = Get(dependency,"Behaviours");
        Set(storage,"Capacity",1);
        Set(storage,"Allocated",true);
        CollectionAssert.Contains(Gaps(type,target),"controller-nonempty-behaviour-clone-lifecycle");
        Set(dependency,"BehaviourObjects",Array.Empty<string>());
        Set(dependency,"BlockStorage",true);
        CollectionAssert.Contains(Gaps(type,target),"controller-native-allocator-dependency");
    }

    [TestMethod]
    public void MappingUsesParameterHashAndActualFloatBindingOrdinalAndCannotInventLifetime()
    {
        var type = Load();
        var player = Player(type);
        var dependency = Get(player,"Dependency");
        var runtime = Get(player,"Runtime");
        var schema = new byte[12];
        BitConverter.GetBytes(73u).CopyTo(schema,0);
        BitConverter.GetBytes(1u).CopyTo(schema,4);
        Set(runtime,"Parameters",schema);
        Set(dependency,"BindingsAlive",true);
        Set(dependency,"AdditionalPresent",true);
        Set(dependency,"AdditionalIndices",new[] {0});
        var binding = New(type,"Generic");
        Set(binding,"Type",95);
        Set(binding,"Attribute",73u);
        ArrayField(dependency,"AdditionalBindings",binding);
        Call(type,"ValidateControllerDependency",player);
        Set(dependency,"AdditionalIndices",new[] {1});
        Invalid(() => Call(type,"ValidateControllerDependency",player));
        Set(dependency,"AdditionalIndices",new[] {0});
        Set(binding,"Attribute",74u);
        Invalid(() => Call(type,"ValidateControllerDependency",player));
        Set(binding,"Attribute",73u);
        Set(binding,"Custom",(byte)1);
        Invalid(() => Call(type,"ValidateControllerDependency",player));
        Set(binding,"Custom",(byte)0);
        Set(dependency,"BindingsAlive",false);
        Invalid(() => Call(type,"ValidateControllerDependency",player));
    }

    [TestMethod]
    public void ObserverAndLogicalPlayerChangesRejectAndUnknownRegistryPeersRemainGated()
    {
        var type = Load();
        var source = Player(type);
        var target = Player(type);
        Set(Get(target,"Dependency"),"PlayerObject","different-object");
        Invalid(() => Call(type,"RequireControllerDependenciesMatch",source,target));
        target = Player(type);
        var dependency = Get(target,"Dependency");
        Set(dependency,"ObservedAsset","controller-key");
        Set(dependency,"ObserverCallback",0x1138e0);
        Set(dependency,"ObserverSlot",0);
        Set(dependency,"ObserverPeers",new[] {"#unresolved-native-object"});
        var storage = Get(dependency,"ObserverStorage");
        Set(storage,"Capacity",1);
        Set(storage,"Allocated",true);
        Call(type,"ValidateControllerDependency",target);
        CollectionAssert.Contains(Gaps(type,target),"controller-asset-observer-registration");
        Set(dependency,"ObserverPeers",new[] {"0/playable/0/observer/416"});
        Assert.AreEqual(0,Gaps(type,target).Length);
        Set(dependency,"ObserverCallback",1);
        CollectionAssert.Contains(Gaps(type,target),"controller-asset-observer-registration");
    }

    [TestMethod]
    public void InstanceDirectoryRequiresActualAllocationAndPreservesGraphNodeAliases()
    {
        var type = Load();
        var source = Player(type);
        var target = Player(type);
        foreach (var player in new[] {source,target}) {
            var runtime = Get(player,"Runtime");
            Set(runtime,"PlayableInstances",1);
            var dependency = Get(player,"Dependency");
            var instance = New(type,"ControllerInstanceDependency");
            Set(instance,"Present",true);
            Set(instance,"Nodes",new[] {2,2,-1});
            ArrayField(dependency,"Instances",instance);
            Set(dependency,"InstancesPresent",true);
        }
        Call(type,"RequireControllerDependenciesMatch",source,target);
        var targetDependency = Get(target,"Dependency");
        var row = ((Array)Get(targetDependency,"Instances")).GetValue(0)!;
        Set(row,"Nodes",new[] {2,3,-1});
        Invalid(() => Call(type,"RequireControllerDependenciesMatch",source,target));
        Set(row,"Present",false);
        Invalid(() => Call(type,"ValidateControllerDependency",target));
        Set(row,"Present",true);
        Set(row,"Nodes",new[] {-2});
        CollectionAssert.Contains(Gaps(type,target),"controller-unbound-owned-graph-node");
    }

    [TestMethod]
    public void NativeCaptureAcrossAddressesNormalizesSelfSenderAndRepeatedInstanceAliases()
    {
        var type = Load();
        var addresses = new[] {Marshal.AllocHGlobal(640),Marshal.AllocHGlobal(640)};
        try {
            var captured = addresses.Select(owner => {
                Marshal.Copy(new byte[640],0,owner,640);
                void Pointer(int at, IntPtr pointer) => Marshal.WriteIntPtr(owner,at,pointer);
                Pointer(192,new IntPtr(0x15d5b60));
                Pointer(208,new IntPtr(0x15d6cf8));
                Pointer(272,new IntPtr(0x15d9030));
                Pointer(288,new IntPtr(0x15d8f00));
                Marshal.WriteInt32(owner,200,30);
                Marshal.WriteInt32(owner,248,30);
                Marshal.WriteInt32(owner,344,74);
                Pointer(384,IntPtr.Add(owner,272));
                Pointer(440,IntPtr.Add(owner,512));
                Marshal.WriteInt32(owner,448,1);
                Pointer(512,IntPtr.Add(owner,528));
                Marshal.WriteInt32(owner,520,2);
                Pointer(528,owner);
                Pointer(536,owner);
                var readerType = type.GetNestedType("Reader",Flags)!;
                var reader = RuntimeHelpers.GetUninitializedObject(readerType);
                Set(reader,"Memory",New(type,"MemoryReader"));
                Set(reader,"Live",New(type,"Live"));
                Set(reader,"Engine",IntPtr.Zero);
                Set(reader,"moduleSize",int.MaxValue);
                var graphType = type.GetNestedType("GraphView",Flags)!;
                var graph = RuntimeHelpers.GetUninitializedObject(graphType);
                Set(graph,"Nodes",new List<IntPtr> {owner});
                Set(graph,"Indices",new Dictionary<IntPtr,int> {{owner,0}});
                Set(graph,"Data",New(type,"Topology"));
                var player = Player(type);
                Set(Get(player,"Runtime"),"PlayableInstances",1);
                return Call(type,"CaptureControllerDependency",reader,graph,0,player)!;
            }).ToArray();
            Assert.AreEqual(Call(type,"Json",captured[0]),Call(type,"Json",captured[1]));
            Assert.AreEqual(0,Get(captured[0],"Sender"));
            var row = ((Array)Get(captured[0],"Instances")).GetValue(0)!;
            CollectionAssert.AreEqual(new[] {0,0},(int[])Get(row,"Nodes"));
        } finally {
            foreach (var address in addresses) Marshal.FreeHGlobal(address);
        }
    }

    [TestMethod]
    public void OwnerValidationRejectsWrongMixerDirectoryAndObserverReciprocalSlots()
    {
        var type = Load();
        var data = New(type,"Data");
        var player = Player(type);
        ArrayField(data,"Playables",player);
        var graph = New(type,"Topology");
        var controller = New(type,"Node");
        Set(controller,"Controller",0);
        Set(controller,"AnimationFlags",new byte[5]);
        var mixer = New(type,"Node");
        Set(mixer,"Vtable",0x15d9380);
        ArrayField(graph,"Nodes",controller,mixer);
        ArrayField(data,"Graphs",graph);
        var dependency = Get(player,"Dependency");
        Set(dependency,"LayerMixer",1);
        Call(type,"ValidateControllerDependencyOwners",data);
        Set(dependency,"LayerMixer",0);
        Invalid(() => Call(type,"ValidateControllerDependencyOwners",data));
        Set(dependency,"LayerMixer",1);
        Set(dependency,"ObservedAsset","asset");
        Set(dependency,"ObserverPeers",new[] {"0/playable/1/observer/416"});
        Set(dependency,"ObserverSlot",0);
        var storage = Get(dependency,"ObserverStorage");
        Set(storage,"Capacity",1);
        Set(storage,"Allocated",true);
        Invalid(() => Call(type,"ValidateControllerDependencyOwners",data));
        Set(dependency,"ObserverPeers",new[] {"0/playable/0/observer/416"});
        var users = new[] {56,96}.Select(offset => {
            var slot = New(type,"UserSlot");
            Set(slot,"Owner","asset");
            Set(slot,"Offset",offset);
            return slot;
        }).ToArray();
        ArrayField(data,"Users",users);
        Call(type,"ValidateControllerDependencyOwners",data);
        Set(Get(player,"Runtime"),"PlayableInstances",1);
        Set(Get(player,"Runtime"),"SynchronizedLayers",new[] {1});
        var instance = New(type,"ControllerInstanceDependency");
        Set(instance,"Present",true);
        Set(instance,"Nodes",new[] {2});
        Set(dependency,"InstancesPresent",true);
        ArrayField(dependency,"Instances",instance);
        Invalid(() => Call(type,"ValidateControllerDependencyOwners",data));
        Set(instance,"Nodes",new[] {1});
        Call(type,"ValidateControllerDependencyOwners",data);
    }
}
