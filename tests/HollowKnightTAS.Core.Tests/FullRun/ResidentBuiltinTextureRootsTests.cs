using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;
[TestClass]
public sealed class ResidentBuiltinTextureRootsTests
{
    private const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    [TestMethod] public void EmptySlotsAreObservedWithoutCreatingResourcesOrRequiringInstanceCounts() { var f=new Fixture();var o=f.Observe();Assert.AreEqual(0,f.Aliases(f.Complete(o)).Count);f.PointerSlot(0x1a16298,f.Object(7));f.Api.Reject(()=>f.Api.Call(o,"RequireUnchanged")); }
    [TestMethod] public void EarlyReferencesCannotBecomeStableKeysBeforeExactRegistration() { var f=new Fixture();var p=f.Object(7);f.PointerSlot(0x1a16298,p);f.Registered.Clear();var o=f.Observe();Assert.IsTrue(((IReadOnlyDictionary<string,IntPtr>)f.Api.Field(o,"Origins")).Values.Contains(p));f.Api.Reject(()=>f.Complete(o)); }
    [TestMethod] public void SortedAliasesProduceReversibleKeysAndRelocateAcrossProcesses() { var a=new Fixture();var p=a.Object(7);a.PointerSlot(0x1a16298,p);a.PointerSlot(0x1a162a0,p);var source=a.Live();var b=new Fixture(0x20000000);var q=b.Object(81);b.PointerSlot(0x1a16298,q);b.PointerSlot(0x1a162a0,q);var target=b.Live();var key=(string)a.Api.Call(source,"Key",p)!;var aliases=a.Aliases(source)[p];CollectionAssert.AreEqual(aliases,(string[])a.Api.Static("Decode",key)!);Assert.AreEqual(q,b.Api.Call(target,"Resolve",key));var pairs=(IReadOnlyDictionary<IntPtr,IntPtr>)a.Api.Static("RequireSameTopology",source,target)!;Assert.AreEqual(q,pairs[p]);Assert.AreEqual(key,b.Api.Call(target,"Key",q)); }
    [TestMethod] public void NewlyFormedAliasCannotBeHiddenByResolvingOnlyOneSlot() { var a=new Fixture();var p=a.Object(7);a.PointerSlot(0x1a16298,p);var source=a.Live();var b=new Fixture();var q=b.Object(8);b.PointerSlot(0x1a16298,q);b.PointerSlot(0x1a162a0,q);var target=b.Live();a.Api.Reject(()=>b.Api.Call(target,"Resolve",a.Api.Call(source,"Key",p)!)); }
    [TestMethod] public void SplitAliasesAndNullSlotTopologyAreRejected() { var a=new Fixture();var p=a.Object(7);a.PointerSlot(0x1a16298,p);a.PointerSlot(0x1a162a0,p);var source=a.Live();var b=new Fixture();b.PointerSlot(0x1a16298,b.Object(8));b.PointerSlot(0x1a162a0,b.Object(9));var target=b.Live();a.Api.Reject(()=>a.Api.Static("RequireSameTopology",source,target));var empty=new Fixture().Live();a.Api.Reject(()=>a.Api.Static("RequireSameTopology",empty,source)); }
    [TestMethod] public void NativeIDBackReferenceAndCachedTypeMustMatchDirectory() { var f=new Fixture();var p=f.Object(7);f.PointerSlot(0x1a16298,p);f.Registered[7]=new IntPtr(123);f.Api.Reject(()=>f.Live());f=new Fixture();p=f.Object(7);f.PointerSlot(0x1a16298,p);f.Write32(p.ToInt64()+12,77U<<21);f.Api.Reject(()=>f.Live()); }
    [TestMethod] public void ActualVirtualRttiAndDeclaredInheritanceAreIndependentChecks() { var f=new Fixture();var p=f.Object(7);f.PointerSlot(0x1a16298,p);f.Getter(1,0x1960fb0);f.Api.Reject(()=>f.Live());f=new Fixture();p=f.Object(7);f.PointerSlot(0x1a162a8,p);f.Api.Reject(()=>f.Live()); }
    [TestMethod] public void AbstractOrUnregisteredPendingRootsFailClosed() { var f=new Fixture();var p=f.Object(7);f.PointerSlot(0x1a16298,p);Set(f.Classes.GetValue(1)!,"Abstract",true);f.Api.Reject(()=>f.Live()); }
    [TestMethod] public void DefaultArrayAliasesAreCapturedFromCurrentPointers() { var f=new Fixture();var p=f.Object(7);f.PointerSlot(0x1a16408+8,p);f.PointerSlot(0x1a16408+16,p);var live=f.Live();Assert.AreEqual(2,f.Aliases(live)[p].Length); }
    [TestMethod] public void ScreenManagerOwnerChainRequiresItsActualVtableAndRegisteredTexture() { var f=new Fixture();var p=f.Object(7,3);f.Screen(p);var live=f.Live();Assert.IsTrue(f.Aliases(live)[p].Single().StartsWith("P/",StringComparison.Ordinal));f=new Fixture();f.Screen(f.Object(7,3),false);f.Api.Reject(()=>f.Observe()); }
    [TestMethod] public void ExposedAliasArrayCannotRewriteCanonicalIdentity() { var f=new Fixture();var p=f.Object(7);f.PointerSlot(0x1a16298,p);f.PointerSlot(0x1a162a0,p);var live=f.Live();var key=(string)f.Api.Call(live,"Key",p)!;f.Aliases(live)[p][0]="bad";Assert.AreEqual(key,f.Api.Call(live,"Key",p));Assert.AreEqual(p,f.Api.Call(live,"Resolve",key)); }
    [TestMethod] public void AlteredEngineAndUnsortedDuplicateAliasKeysAreRejected() { var f=new Fixture();var p=f.Object(7);f.PointerSlot(0x1a16298,p);var live=f.Live();var key=(string)f.Api.Call(live,"Key",p)!;f.Api.Reject(()=>f.Api.Static("Decode",key.Replace("D97E","0000")));f.Api.Reject(()=>f.Api.Static("Encode",new object[]{new[]{"S/1a16298/0","S/1a16298/0"}})); }
    private static void Set(object value,string name,object field)=>value.GetType().GetField(name,All)!.SetValue(value,field);
    private sealed class Api
    {
        internal readonly Type Type,EntryType,ClassType;
        internal Api() {var location=Environment.GetEnvironmentVariable("HKTAS_BUILTIN_ROOT_TEST_ASSEMBLY");if(string.IsNullOrEmpty(location)){var root=new DirectoryInfo(AppContext.BaseDirectory);while(root!=null&&!File.Exists(Path.Combine(root.FullName,"HollowKnightTAS.sln")))root=root.Parent;Assert.IsNotNull(root);location=Path.Combine(root.FullName,"src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll");}Type=Assembly.LoadFrom(location).GetType("HollowKnightTAS.Runtime.FullRun.ResidentBuiltinTextureRoots",true)!;EntryType=Type.GetNestedType("Entry",All)!;ClassType=Type.GetNestedType("TypeInfo",All)!;}
        internal object? Static(string name,params object[] args)=>Type.GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(null,args);
        internal object? Call(object value,string name,params object[] args)=>value.GetType().GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(value,args);
        internal object Field(object value,string name)=>value.GetType().GetField(name,All)?.GetValue(value)??value.GetType().GetProperty(name,All)!.GetValue(value)!;
        internal void Reject(Action action){var error=Assert.ThrowsExactly<TargetInvocationException>(action);Assert.IsInstanceOfType<InvalidDataException>(error.GetBaseException());}
    }
    private sealed class Fixture
    {
        internal readonly Api Api=new Api();internal readonly long Engine;internal readonly Dictionary<int,IntPtr> Registered=new();internal readonly Array Classes;
        private readonly Dictionary<long,byte> memory=new();private readonly Dictionary<int,object> entries=new();
        internal Fixture(long engine=0x10000000){Engine=engine;foreach(var root in (Array)Api.Type.GetField("Layout",All)!.GetValue(null)!){int rva=(int)Api.Field(root!,"Rva"),count=(int)Api.Field(root!,"Count");Write(engine+rva,new byte[count*8]);}PointerSlot(0x1a15970,IntPtr.Zero);Classes=Array.CreateInstance(Api.ClassType,4);int[] rvas={0x194ce60,0x194ceb0,0x1960fb0,0x19645a0};for(int i=0;i<4;i++){var type=Activator.CreateInstance(Api.ClassType,true)!;Set(type,"Rva",rvas[i]);Set(type,"BaseRva",i==0?0:rvas[0]);Set(type,"Index",i);Set(type,"Size",i==3?288:312);Set(type,"ObjectDerived",true);Classes.SetValue(type,i);Getter(i,rvas[i]);}}
        internal void Getter(int index,int rtti){long table=Engine+0x1000+index*128,getter=Engine+0x2000+index*16;Write64(table+64,getter);var code=new byte[]{0x48,0x8d,0x05,0,0,0,0,0xc3};Array.Copy(BitConverter.GetBytes((int)(Engine+rtti-getter-7)),0,code,3,4);Write(getter,code);}
        internal IntPtr Object(int id,int index=1){long pointer=0x70000000+Engine+id*64;Write(pointer,new byte[16]);Write64(pointer,Engine+0x1000+index*128);Write32(pointer+8,(uint)id);Write32(pointer+12,(uint)index<<21);var p=new IntPtr(pointer);Registered.Add(id,p);var e=Activator.CreateInstance(Api.EntryType,true)!;Set(e,"TypeRva",Api.Field(Classes.GetValue(index)!,"Rva"));Set(e,"Vtable",0x1000+index*128);entries.Add(id,e);return p;}
        internal void Screen(IntPtr leaf,bool valid=true){long owner=Engine+0x900000;PointerSlot(0x1a15970,new IntPtr(owner));Write64(owner,Engine+(valid?0x1671bf8:0x1671bf0));Write64(owner+144,leaf.ToInt64());}
        internal void PointerSlot(int rva,IntPtr value)=>Write64(Engine+rva,value.ToInt64());
        internal void Write(long address,byte[] bytes){for(int i=0;i<bytes.Length;i++)memory[address+i]=bytes[i];}
        internal void Write64(long address,long value)=>Write(address,BitConverter.GetBytes(value));internal void Write32(long address,uint value)=>Write(address,BitConverter.GetBytes(value));
        private byte[] Read(IntPtr address,int length){var result=new byte[length];for(int i=0;i<length;i++)if(!memory.TryGetValue(address.ToInt64()+i,out result[i]))throw new InvalidDataException("fixture unreadable address");return result;}
        public object EntryFor(int id)=>entries[id];
        internal object Observe()=>Api.Static("Observe",new IntPtr(Engine),0x2000000,(Func<IntPtr,int,byte[]>)Read,(Action)(()=>{}))!;
        internal object Complete(object observation){var argument=Expression.Parameter(typeof(int));var body=Expression.Convert(Expression.Call(Expression.Constant(this),typeof(Fixture).GetMethod(nameof(EntryFor))!,argument),Api.EntryType);var factory=Expression.Lambda(typeof(Func<,>).MakeGenericType(typeof(int),Api.EntryType),body,argument).Compile();return Api.Call(observation,"Complete",Registered,factory,Classes)!;}
        internal object Live()=>Complete(Observe());internal IReadOnlyDictionary<IntPtr,string[]> Aliases(object live)=>(IReadOnlyDictionary<IntPtr,string[]>)Api.Field(live,"Aliases");
    }
}
