using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass] public sealed class LoadingContactUserDataBindingsTests
{
    private sealed class Fixture
    {
        internal byte[] Cache = new byte[88], Hash = Enumerable.Repeat((byte)255,128).ToArray(), Value = new byte[104], Contact = new byte[312];
        internal int Slot;
        internal Fixture()
        {
            Put(Cache,8,3000L);Put(Cache,16,96u);Put(Cache,20,1u);Put(Cache,40,2000L);Put(Cache,56,1L);Put(Cache,64,3L);
            Put(Value,24,1L);Put(Value,32,1);Put(Value,36,1);
            Put(Value,40,100L);Put(Value,48,200L);Put(Value,80,100L);Put(Value,88,200L);Value[96]=Value[97]=1;
            Put(Contact,296,-1);Put(Contact,304,1000L);
            var hash=LoadingContactUserDataBindings.PairHash(10,20);Slot=(int)(hash&96);
            Put(Hash,Slot,hash&0xfffffffcu);Put(Hash,Slot+8,100L);Put(Hash,Slot+16,200L);Put(Hash,Slot+24,0);
        }
        internal object Capture(int aliases=1,bool unknown=false)
        {
            return LoadingContactUserDataBindings.Capture(Cache,Hash,new[]{1000L},new[]{Value},
                Enumerable.Range(0,aliases).Select(i=>new LoadingContactUserDataBindings.Contact {
                    Binding="fixture-pair-"+i, Bytes=Contact,OwnerA=100,OwnerB=200 }).ToArray(),
                p=>unknown?null:p==100?new(){Id="collider-a",InstanceId=10}:p==200?new(){Id="collider-b",InstanceId=20}:null,
                _=>null);
        }
    }
    private static void Put(byte[] b,int at,int value)=>BitConverter.GetBytes(value).CopyTo(b,at);
    private static void Put(byte[] b,int at,uint value)=>BitConverter.GetBytes(value).CopyTo(b,at);
    private static void Put(byte[] b,int at,long value)=>BitConverter.GetBytes(value).CopyTo(b,at);
    [TestMethod] public void SharedMultiFixtureProducerIdentityRetainsAliasesAndUnknownCoverage()
    {
        var f=new Fixture();Put(f.Value,36,2);
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(f.Capture(2)));
        Assert.IsTrue(json.RootElement.GetProperty("identityComplete").GetBoolean());
        Assert.IsFalse(json.RootElement.GetProperty("completeUserDataCoverage").GetBoolean());
        var contacts=json.RootElement.GetProperty("contacts").EnumerateArray().ToArray();
        Assert.AreEqual(2,contacts.Length);Assert.IsTrue(contacts.All(x=>x.GetProperty("collisionIndex").GetInt32()==0));
        Assert.IsFalse(json.RootElement.GetRawText().Contains("1000"));
    }
    [TestMethod] public void ForeignPointerPairAndReferenceCountReject()
    {
        var f=new Fixture();Put(f.Contact,304,999L);Assert.ThrowsExactly<InvalidDataException>(()=>f.Capture());
        f=new Fixture();Put(f.Value,36,2);Assert.ThrowsExactly<InvalidDataException>(()=>f.Capture());
        f=new Fixture();Put(f.Value,48,300L);Assert.ThrowsExactly<InvalidDataException>(()=>f.Capture());
    }
    [TestMethod] public void OriginalHashCodeIndexKeyAndTerminatedProbeReject()
    {
        foreach(var offset in new[]{0,8,16,24}) {
            var f=new Fixture();f.Hash[f.Slot+offset]^=1;
            Assert.ThrowsExactly<InvalidDataException>(()=>f.Capture());
        }
        var moved=new Fixture();var row=moved.Hash.AsSpan(moved.Slot,32).ToArray();
        moved.Hash.AsSpan(moved.Slot,32).Fill(255);row.CopyTo(moved.Hash,((moved.Slot+32)&96));
        Assert.ThrowsExactly<InvalidDataException>(()=>moved.Capture());
    }
    [TestMethod] public void UnknownColliderBindingFailsClosedWithoutReadingForeignPointers()
    {
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(new Fixture().Capture(unknown:true)));
        Assert.IsFalse(json.RootElement.GetProperty("identityComplete").GetBoolean());
        Assert.IsTrue(json.RootElement.GetProperty("unsupported").GetArrayLength()>0);
    }
    [TestMethod] public void ActiveManifoldAndBadBoolDoNotGainCoverage()
    {
        var f=new Fixture();Put(f.Value,0,3000L);Put(f.Value,16,1L);Put(f.Value,24,3L);
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(f.Capture()));
        Assert.IsTrue(json.RootElement.GetProperty("identityComplete").GetBoolean());
        Assert.IsTrue(json.RootElement.GetProperty("pointeeUnsupported").GetArrayLength()>0);
        Assert.IsFalse(json.RootElement.GetProperty("completeUserDataCoverage").GetBoolean());
        f=new Fixture();f.Value[98]=2;Assert.ThrowsExactly<InvalidDataException>(()=>f.Capture());
    }
    [TestMethod] public void MaskAndTableBoundsRejectBeforeTraversal()
    {
        var f=new Fixture();Put(f.Cache,16,64u);Assert.ThrowsExactly<InvalidDataException>(()=>f.Capture());
        f=new Fixture();f.Hash=new byte[32];Assert.ThrowsExactly<InvalidDataException>(()=>f.Capture());
        f=new Fixture();Put(f.Cache,16,uint.MaxValue);Assert.ThrowsExactly<InvalidDataException>(()=>f.Capture());
    }
    [TestMethod] public void ActiveManifoldValidatesBothDirectionsAndOnlyCountBoundedPointFields()
    {
        var f = new Fixture(); f.Value[97] = 0;
        Put(f.Value, 0, 3000L); Put(f.Value, 16, 1L); Put(f.Value, 24, 3L);
        Put(f.Contact, 296, 0); Put(f.Contact, 96, 4000L); Put(f.Contact, 104, 5000L);
        Put(f.Contact, 112, 7); Put(f.Contact, 116, 8);
        var m = Enumerable.Repeat((byte)0xab, 112).ToArray(); m[0] = 1;
        Put(m, 8, 6000L); Put(m, 16, 4000L); Put(m, 24, 5000L);
        Put(m, 32, 7); Put(m, 36, 8); Put(m, 40, 123); Put(m, 44, 1);
        object Capture() => LoadingContactUserDataBindings.Capture(f.Cache, f.Hash, new[]{1000L}, new[]{f.Value},
            new[]{new LoadingContactUserDataBindings.Contact { Binding="contact", Bytes=f.Contact, Pointer=6000, OwnerA=100, OwnerB=200 }},
            p=>p==100?new(){Id="a",InstanceId=10}:p==200?new(){Id="b",InstanceId=20}:null,
            _=>null, new[]{new[]{m}}, p=>p==4000?"fixture-a":p==5000?"fixture-b":null);
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(Capture()));
        var record=json.RootElement.GetProperty("manifolds")[0];
        Assert.AreEqual(224, record.GetProperty("pointerReboundRaw112Hex").GetString()!.Length);
        Assert.AreEqual(16, record.GetProperty("pointsHex").GetString()!.Length);
        Assert.AreEqual(8, record.GetProperty("normalImpulsesHex").GetString()!.Length);
        Assert.IsFalse(json.RootElement.GetProperty("completeUserDataCoverage").GetBoolean());
        foreach(var offset in new[]{8,16,24,32,36}) {
            m[offset]^=1; Assert.ThrowsExactly<InvalidDataException>(()=>Capture()); m[offset]^=1;
        }
        Put(f.Contact,296,1); Assert.ThrowsExactly<InvalidDataException>(()=>Capture()); Put(f.Contact,296,0);
        Put(m,44,3); Assert.ThrowsExactly<InvalidDataException>(()=>Capture());
        Put(m,44,0);
        using var empty=JsonDocument.Parse(JsonSerializer.Serialize(Capture()));
        Assert.AreEqual("",empty.RootElement.GetProperty("manifolds")[0].GetProperty("normalHex").GetString());
    }
    [TestMethod] public void BuiltRuntimeNativeReaderEntryValidatesStableAllocatedFixture()
    {
        var repo=new DirectoryInfo(AppContext.BaseDirectory);
        while(repo!=null&&!File.Exists(Path.Combine(repo.FullName,"CURRENT.md")))repo=repo.Parent;
        Assert.IsNotNull(repo);
        var assembly=Assembly.LoadFrom(Path.Combine(repo.FullName,"src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
        var type=assembly.GetType("HollowKnightTAS.Runtime.FullRun.LoadingNativePhysicsDigest",true)!;
        var binding=assembly.GetType("HollowKnightTAS.Runtime.FullRun.LoadingContactUserDataBindings+ColliderBinding",true)!;
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var reader=RuntimeHelpers.GetUninitializedObject(type);
        var allocations=new List<IntPtr>();
        IntPtr Allocate(byte[] bytes) {var p=Marshal.AllocHGlobal(bytes.Length);allocations.Add(p);Marshal.Copy(bytes,0,p,bytes.Length);return p;}
        void Set(string name,object value)=>type.GetField(name,flags)!.SetValue(reader,value);
        try {
            var fixture=new Fixture();var objectPointer=Allocate(fixture.Value);
            var hashPointer=Allocate(fixture.Hash);var vectorPointer=Allocate(BitConverter.GetBytes(objectPointer.ToInt64()));
            Put(fixture.Cache,8,hashPointer.ToInt64());Put(fixture.Cache,40,vectorPointer.ToInt64());
            Put(fixture.Contact,304,objectPointer.ToInt64());Put(fixture.Contact,96,10000L);Put(fixture.Contact,104,20000L);
            var contactPointer=Allocate(fixture.Contact);var cachePointer=Allocate(fixture.Cache);
            Set("fixtureOwners",new Dictionary<IntPtr,IntPtr>{{new(10000),new(100)},{new(20000),new(200)}});
            Set("contactIds",new Dictionary<IntPtr,string>{{contactPointer,"fixture-pair"}});
            Set("rigidbodyIds",new Dictionary<IntPtr,string>());
            var map=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(IntPtr),binding))!;
            foreach(var entry in new[]{(100,"collider-a",10),(200,"collider-b",20)}) {
                var value=Activator.CreateInstance(binding,true)!;
                binding.GetField("Id",flags)!.SetValue(value,entry.Item2);binding.GetField("InstanceId",flags)!.SetValue(value,entry.Item3);
                map.Add(new IntPtr(entry.Item1),value);
            }
            Set("colliderBindings",map);
            var contacts=new List<KeyValuePair<IntPtr,byte[]>>{new(contactPointer,fixture.Contact)};
            var method=type.GetMethod("CaptureContactUserDataBindings",flags)!;
            var result=method.Invoke(reader,new object[]{cachePointer,fixture.Cache,contacts});
            using var json=JsonDocument.Parse(JsonSerializer.Serialize(result));
            Assert.IsTrue(json.RootElement.GetProperty("identityComplete").GetBoolean());
            Assert.IsFalse(json.RootElement.GetProperty("completeUserDataCoverage").GetBoolean());
            Assert.IsFalse(json.RootElement.GetRawText().Contains(objectPointer.ToInt64().ToString()));
            // Exercise active dynamic storage through the actual built native reader.
            var manifold=new byte[112]; manifold[0]=1;
            Put(manifold,8,contactPointer.ToInt64());Put(manifold,16,10000L);Put(manifold,24,20000L);
            Put(manifold,44,2);
            var manifoldPointer=Allocate(manifold);
            fixture.Value[97]=0;Put(fixture.Value,0,manifoldPointer.ToInt64());Put(fixture.Value,16,1L);Put(fixture.Value,24,3L);
            Put(fixture.Contact,296,0);
            Marshal.Copy(fixture.Value,0,objectPointer,104);Marshal.Copy(fixture.Contact,0,contactPointer,312);
            Set("fixtureIds",new Dictionary<IntPtr,string>{{new(10000),"fixture-a"},{new(20000),"fixture-b"}});
            result=method.Invoke(reader,new object[]{cachePointer,fixture.Cache,contacts});
            using var active=JsonDocument.Parse(JsonSerializer.Serialize(result));
            Assert.AreEqual(1,active.RootElement.GetProperty("manifolds").GetArrayLength());
            Assert.AreEqual(2,active.RootElement.GetProperty("manifolds")[0].GetProperty("pointCount").GetInt32());
            // The actual native wrapper must reject a target outside its vector.
            Put(fixture.Contact,304,999L);Marshal.Copy(fixture.Contact,0,contactPointer,fixture.Contact.Length);
            var error=Assert.ThrowsExactly<TargetInvocationException>(()=>method.Invoke(reader,new object[]{cachePointer,fixture.Cache,contacts}));
            Assert.IsInstanceOfType<InvalidDataException>(error.InnerException);
        } finally {foreach(var p in allocations)Marshal.FreeHGlobal(p);}
    }
}
