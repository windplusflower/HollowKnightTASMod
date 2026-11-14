using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentAudioVoiceStateTests
{
    private const BindingFlags Flags=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
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
            for(int i=0;i<values.Length;i++) result.SetValue(values[i],i);
            return result;
        }
        internal void Invalid(string name,params object[] values)
            =>Assert.IsInstanceOfType<InvalidDataException>(Assert.ThrowsExactly<TargetInvocationException>(()=>Call(name,values)).InnerException);
        internal object Source(string owner,string alias)
        {
            var source=New("Source");Set(source,"Owner",owner);
            var type=Get(source,"Type");Set(type,"Assembly","UnityEngine.AudioModule");Set(type,"Module","aebd3224-17b8-4b41-ad28-0fa7ff609f00");
            Set(type,"Name","UnityEngine.AudioSource");Set(type,"Token",0x02000001);
            Set(source,"Scalars",new[]{2,40,233,4,1,8,6,4,14,1}.Select(count=>new byte[count]).ToArray());
            var curve=Type.Assembly.GetType("HollowKnightTAS.Runtime.FullRun.ResidentManagedNativeWrappers",true)!.GetNestedType("CurveData",Flags)!;
            var curves=System.Array.CreateInstance(curve,4);
            for(int i=0;i<4;i++) {var value=Activator.CreateInstance(curve,true)!;Set(value,"Caches",new byte[56]);curves.SetValue(value,i);}
            Set(source,"Curves",curves);
            var channel=Get(source,"Main");Set(channel,"WeakOwner",true);Set(channel,"LiveInstance",true);Set(channel,"Alias",alias);
            return source;
        }
        internal object Voice(string alias)
        {
            var voice=Call("DecodeVoiceBody",new byte[360],alias);
            Set(voice,"Clip","clip");Set(voice,"HandleAlias","handle/"+alias);Set(voice,"LoadState",3);Set(voice,"SameSampleClipHandle",true);
            Set(voice,"Lists",Array("VoiceRegistration",New("VoiceRegistration"),New("VoiceRegistration"),New("VoiceRegistration")));
            return voice;
        }
        internal object Fixture(out object source,out object voice)
        {
            var data=New("Data");source=Source("source","a");voice=Voice("a");
            Set(data,"Sources",Array("Source",source));Set(data,"Voices",Array("VoiceState",voice));return data;
        }
        internal object Group(string owner,string role) {var group=New("VoiceGroupUse");Set(group,"Owner",owner);Set(group,"Role",role);return group;}
        internal object Backend(string? owner=null,string role="dry")
        {
            var type=Type.Assembly.GetType("HollowKnightTAS.Runtime.FullRun.ResidentAudioBackendControl",true)!;
            var body=Activator.CreateInstance(type.GetNestedType("Data",Flags)!,true)!;
            var fields=System.Array.CreateInstance(type.GetNestedType("Field",Flags)!,15);
            for(int i=0;i<15;i++)fields.SetValue(Activator.CreateInstance(type.GetNestedType("Field",Flags)!,true),i);
            Set(body,"Fields",fields);
            if(owner!=null) {
                var use=Activator.CreateInstance(type.GetNestedType("GroupUse",Flags)!,true)!;Set(use,"Owner",owner);Set(use,"Role",role);
                var uses=System.Array.CreateInstance(use.GetType(),1);uses.SetValue(use,0);Set(body,"GroupUses",uses);Set(fields.GetValue(14)!,"Status",1);
            }
            return body;
        }
        internal void Registered(object voice,int index,string previous,string next)
        {
            var list=((Array)Get(voice,"Lists")).GetValue(index)!;
            Set(list,"Registered",true);Set(list,"Owner",index==0?(string)Get(voice,"HandleAlias")+"/channels":index==1?(string)Get(voice,"HandleAlias")+"/pending":"sound-manager/update");
            Set(list,"Previous",previous);Set(list,"Next",next);
        }
    }
    [TestMethod]
    public void BackendControlsKeepActualGroupSeparateFromRequestedDirtyCacheAndValidateSourceRoles()
    {
        var api=new Api();var data=api.Fixture(out var source,out var voice);
        Set(voice,"HasFMOD",true);Set(voice,"PCMAvailable",true);
        CollectionAssert.Contains((string[])api.Call("RequiredCoverageGaps",data),"audio-live-voice-backend-control-body-missing");
        ((bool[])Get(source,"NativeResources"))[1]=true;((bool[])Get(source,"NativeResources"))[2]=true;
        Set(voice,"GroupAlias","requested-dry");Set(voice,"Groups",api.Array("VoiceGroupUse",api.Group("source","dry")));
        var backend=api.Backend("source","wet");Set(voice,"Backend",backend);api.Call("Validate",data);
        ((bool[])Get(source,"NativeResources"))[2]=false;api.Invalid("Validate",data);((bool[])Get(source,"NativeResources"))[2]=true;
        var use=((Array)Get(backend,"GroupUses")).GetValue(0)!;Set(use,"Owner","unknown-source");api.Invalid("Validate",data);
    }
    [TestMethod]
    public void BackendInvalidReceiptStatusesStayVisibleAndCopiedBodyParticipatesInReadback()
    {
        var api=new Api();var data=api.Fixture(out _,out var voice);Set(voice,"HasFMOD",true);Set(voice,"PCMAvailable",true);
        var backend=api.Backend();Set(backend,"Result",23);var fields=(Array)Get(backend,"Fields");
        Set(fields.GetValue(4)!,"Status",4);Set(fields.GetValue(4)!,"Error",23);Set(voice,"Backend",backend);api.Call("Validate",data);
        var gaps=(string[])api.Call("RequiredCoverageGaps",data);
        CollectionAssert.Contains(gaps,"audio-backend-control-receipt-not-coherent");CollectionAssert.Contains(gaps,"audio-backend-control-field-4-status-4-error-23");
        var copy=api.Call("Copy",data);api.Call("RequireEqual",data,copy);
        var otherBackend=Get(((Array)Get(copy,"Voices")).GetValue(0)!,"Backend");var field=((Array)Get(otherBackend,"Fields")).GetValue(1)!;
        Set(field,"Status",1);((uint[])Get(field,"Values"))[0]=0x80000000u;api.Call("RequireEqual",data,copy);((byte[][])Get(api.Source("source","a"),"Scalars"))[0][0]=7;Set(copy,"Sources",api.Array("Source",api.Source("source","a")));((byte[][])Get(((Array)Get(copy,"Sources")).GetValue(0)!,"Scalars"))[0][0]=7;api.Invalid("RequireEqual",data,copy);
        var readerType=api.Type.GetNestedType("Reader",Flags)!;
        var reader=Activator.CreateInstance(readerType,Flags,null,new object?[]{null,new Func<object,string?>(_=>null)},null)!;
        ((System.Collections.IDictionary)Get(reader,"BackendReceipts")).Add("a",backend);
        ((System.Collections.IDictionary)Get(reader,"BackendFailures")).Add("a","actual native control query error");
        var diagnostic=api.Call("DecodeChannelDiagnostic",new byte[360],"a");api.Call("AttachBackendDiagnostic",reader,diagnostic,"a");
        Assert.AreSame(backend,Get(diagnostic,"Backend"));Assert.AreEqual(23,Get(Get(diagnostic,"Backend"),"Result"));
        Assert.AreEqual("actual native control query error",Get(diagnostic,"BackendFailure"));
    }
    [TestMethod]
    public void ReverbIsThreeTypedWordsAndLocalDependencyPresenceNeverPointerOrPadding()
    {
        var api=new Api();var body=new byte[360];
        BitConverter.GetBytes(-1200).CopyTo(body,128);BitConverter.GetBytes(-500).CopyTo(body,132);
        BitConverter.GetBytes(0xfedcba98u).CopyTo(body,136);BitConverter.GetBytes(0x12345678L).CopyTo(body,144);
        var voice=api.Call("DecodeVoiceBody",body,"a");var reverb=Get(voice,"Reverb");
        Assert.AreEqual(-1200,Get(reverb,"Direct"));Assert.AreEqual(-500,Get(reverb,"Room"));Assert.AreEqual(0xfedcba98u,Get(reverb,"Flags"));Assert.AreEqual(true,Get(reverb,"HasConnectionPoint"));
        CollectionAssert.AreEqual(new[]{60,40,2,4},((byte[][])Get(voice,"Parameters")).Select(bytes=>bytes.Length).ToArray());
        BitConverter.GetBytes(0x76543210L).CopyTo(body,144);for(int i=140;i<144;i++)body[i]=0x99;
        Assert.AreEqual(JsonConvert.SerializeObject(voice),JsonConvert.SerializeObject(api.Call("DecodeVoiceBody",body,"a")));
        var data=api.Fixture(out _,out var actual);Set(actual,"Reverb",reverb);api.Call("Validate",data);
        CollectionAssert.Contains((string[])api.Call("RequiredCoverageGaps",data),"audio-live-voice-reverb-connection-point-unsupported");
        Set(data,"Version",4);api.Invalid("Validate",data);
        Set(data,"Version",5);Set(actual,"Parameters",new[]{new byte[60],new byte[66],new byte[4]});api.Invalid("Validate",data);
    }
    [TestMethod]
    public void TypedVoiceProjectionPreservesFloatBitsAndExactNoncontiguousDirtyFields()
    {
        var api=new Api();var body=new byte[360];
        BitConverter.GetBytes(unchecked((int)0x7fc01234)).CopyTo(body,8);
        BitConverter.GetBytes(unchecked((int)0x80000000)).CopyTo(body,280);
        body[154]=0xa5;body[155]=7;body[156]=0xc3;body[160]=0x3f;
        var row=api.Call("DecodeVoiceBody",body,"a");
        var parameters=(byte[][])Get(row,"Parameters");
        CollectionAssert.AreEqual(new byte[]{0xa5,7,0xc3,0x3f},parameters[3]);
        Assert.AreEqual(unchecked((int)0x7fc01234),BitConverter.ToInt32(parameters[0],0));
        Assert.AreEqual(unchecked((int)0x80000000),BitConverter.ToInt32((byte[])Get(row,"VolumePitchPan"),0));
        foreach(int offset in Enumerable.Range(68,20).Concat(Enumerable.Range(140,4)).Concat(new[]{157,158,159,161,162,163,164,165,166,167,331,332,333,334,335})) body[offset]=0x99;
        Assert.AreEqual(JsonConvert.SerializeObject(row),JsonConvert.SerializeObject(api.Call("DecodeVoiceBody",body,"a")));
        api.Invalid("DecodeVoiceBody",new byte[359],"a");
    }
    [TestMethod]
    public void DirtyReservedBitsAndNativeBooleansCannotEnterTypedData()
    {
        var api=new Api();var data=api.Fixture(out _,out var voice);api.Call("Validate",data);
        var dirty=((byte[][])Get(voice,"Parameters"))[3];
        dirty[1]=8;api.Invalid("Validate",data);dirty[1]=0;
        dirty[3]=0x40;api.Invalid("Validate",data);dirty[3]=0;
        ((byte[][])Get(voice,"Parameters"))[2][1]=2;api.Invalid("Validate",data);
    }
    [TestMethod]
    public void NullExpiredLiveAndLegacyVoicesRemainDistinctAndFailClosed()
    {
        var api=new Api();var data=api.Fixture(out var source,out var voice);
        api.Call("Validate",data);api.Invalid("RequireCompleteCoverage",data);
        Set(data,"Voices",api.Array("VoiceState"));api.Invalid("Validate",data);
        Set(data,"Version",3);api.Call("Validate",data);api.Invalid("RequireCompleteCoverage",data);
        CollectionAssert.Contains((string[])api.Call("RequiredCoverageGaps",data),"audio-legacy-weak-without-typed-voice");
        Set(data,"Version",4);Set(Get(source,"Main"),"LiveInstance",false);
        var expired=api.New("VoiceState");Set(expired,"Alias","a");Set(data,"Voices",api.Array("VoiceState",expired));api.Call("Validate",data);
        Set(expired,"PCM",1u);api.Invalid("Validate",data);Set(expired,"PCM",0u);
        Set(Get(source,"Main"),"WeakOwner",false);Set(Get(source,"Main"),"Alias","");api.Invalid("Validate",data);
        Set(data,"Voices",api.Array("VoiceState"));api.Call("RequireCompleteCoverage",data);
    }
    [TestMethod]
    public void WeakAliasesRequireExactlyOneConsistentVoiceBody()
    {
        var api=new Api();var data=api.Fixture(out var source,out var voice);
        var alias=api.New("Channel");Set(alias,"WeakOwner",true);Set(alias,"LiveInstance",true);Set(alias,"Alias","a");
        Set(source,"Cached",api.Array("Channel",alias));Set(source,"CachedCapacity",1);api.Call("Validate",data);
        Set(alias,"LiveInstance",false);api.Invalid("Validate",data);Set(alias,"LiveInstance",true);
        Set(data,"Voices",api.Array("VoiceState",voice,voice));api.Invalid("Validate",data);
        Set(data,"Voices",api.Array("VoiceState",voice));Set(voice,"Clip","@unbound-native-asset-origin");api.Invalid("Validate",data);
    }
    [TestMethod]
    public void GroupRolesPreserveCrossSourceAliasesAndRejectAmbiguousMapping()
    {
        var api=new Api();var data=api.Fixture(out var source,out var a);var second=api.Source("source2","b");var b=api.Voice("b");
        ((bool[])Get(source,"NativeResources"))[1]=true;((bool[])Get(second,"NativeResources"))[2]=true;
        Set(data,"Sources",api.Array("Source",source,second));Set(data,"Voices",api.Array("VoiceState",a,b));
        foreach(var voice in new[]{a,b}) {Set(voice,"GroupAlias","group");Set(voice,"Groups",api.Array("VoiceGroupUse",api.Group("source","dry"),api.Group("source2","wet")));}
        api.Call("Validate",data);
        ((bool[])Get(source,"NativeResources"))[1]=false;api.Invalid("Validate",data);((bool[])Get(source,"NativeResources"))[1]=true;
        Set(b,"GroupAlias","different-group");api.Invalid("Validate",data);Set(b,"GroupAlias","group");
        Set(b,"Groups",api.Array("VoiceGroupUse",api.Group("source","dry")));api.Invalid("Validate",data);
    }
    [TestMethod]
    public void OwnerListsValidateReciprocityHeadAndObservedMembershipWithoutBackendScan()
    {
        var api=new Api();var data=api.Fixture(out var source,out var a);var second=api.Source("source2","b");var b=api.Voice("b");
        Set(data,"Sources",api.Array("Source",source,second));Set(data,"Voices",api.Array("VoiceState",a,b));
        api.Registered(a,2,"@head","b");api.Registered(b,2,"a","@head");api.Call("Validate",data);
        api.Registered(b,2,"@head","@head");api.Invalid("Validate",data);
        api.Registered(a,2,"b","b");api.Registered(b,2,"a","a");api.Invalid("Validate",data);
        api.Registered(a,2,"@external","@head");api.Registered(b,2,"@head","@external");api.Call("Validate",data);
        CollectionAssert.Contains((string[])api.Call("RequiredCoverageGaps",data),"audio-voice-owner-list-has-unobserved-members");
    }
    [TestMethod]
    public void SinglePCMReadRequiresLeaseBeforeAPIAndRejectsErrorsOrIdentityChanges()
    {
        var api=new Api();var body=new byte[360];var calls=new List<string>();
        Action require=()=>calls.Add("lease");Func<byte[]> identity=()=>{calls.Add("identity");return body;};
        Func<(int,uint)> getter=()=>{calls.Add("get");return (0,123);};
        Assert.AreEqual(123u,api.Call("ReadVoicePCM",require,identity,getter,body));
        CollectionAssert.AreEqual(new[]{"lease","identity","lease","get","lease","identity"},calls);
        calls.Clear();Action noLease=()=>throw new InvalidDataException("no lease");
        api.Invalid("ReadVoicePCM",noLease,identity,getter,body);Assert.AreEqual(0,calls.Count);
        api.Invalid("ReadVoicePCM",require,identity,new Func<(int,uint)>(()=>(7,0)),body);
        calls.Clear();int reads=0;Func<byte[]> changed=()=>++reads==1?body:new byte[1];
        api.Invalid("ReadVoicePCM",require,changed,getter,body);
        reads=0;calls.Clear();Func<byte[]> beforeChanged=()=>new byte[1];
        api.Invalid("ReadVoicePCM",require,beforeChanged,getter,body);Assert.IsFalse(calls.Contains("get"));
    }
    [TestMethod]
    public void FinalWholeVoicePCMVerificationRejectsDriftWithoutWaitingOrInventingPosition()
    {
        var api=new Api();var order=new List<int>();
        var readers=new Func<uint>[]{()=>{order.Add(0);return 10;},()=>{order.Add(1);return 20;}};
        api.Call("VerifyVoicePCMPositions",readers,new uint[]{10,20});CollectionAssert.AreEqual(new[]{0,1},order);
        api.Invalid("VerifyVoicePCMPositions",readers,new uint[]{10,21});
        api.Invalid("VerifyVoicePCMPositions",readers,new uint[]{10});
        api.Call("VerifyVoicePCMPositions",Array.Empty<Func<uint>>(),Array.Empty<uint>());
    }
    [TestMethod]
    public void FailedCaptureDiagnosticsOnlyCollectInFailureContextAndPreservePrimary()
    {
        var api=new Api();var marks=new List<string>();int calls=0;
        Action collect=()=>{calls++;throw new InvalidOperationException("secondary");};
        Action<string> mark=marks.Add;
        api.Call("CollectFailedVoiceDiagnostics",false,collect,mark);Assert.AreEqual(0,calls);
        var primary=new InvalidDataException("primary");
        Exception? observed=null;
        try {try {throw primary;}catch {api.Call("CollectFailedVoiceDiagnostics",true,collect,mark);throw;}}
        catch(Exception error) {observed=error;}
        Assert.AreSame(primary,observed);Assert.AreEqual(1,calls);Assert.AreEqual(1,marks.Count);
        StringAssert.Contains(marks[0],"InvalidOperationException");
        api.Call("CollectFailedVoiceDiagnostics",true,collect,new Action<string>(_=>throw new IOException("evidence write failed")));
    }
    [TestMethod]
    public void ModeBitsRemainTypedDataWhileUnsupportedDependenciesAndRestoreKeepGaps()
    {
        var api=new Api();var data=api.Fixture(out _,out var voice);
        Set(voice,"Mode",0xfedcba98u);Set(voice,"HasDSP",true);Set(voice,"Callback","unity-rva");Set(voice,"CallbackRva",0x123);
        Set(voice,"GroupAlias","unknown");Set(voice,"LoadState",7);Set(voice,"HandleDisposed",true);api.Call("Validate",data);
        var gaps=(string[])api.Call("RequiredCoverageGaps",data);
        foreach(var gap in new[]{"audio-live-voice-DSP-unsupported","audio-live-voice-callback-unsupported","audio-live-voice-group-owner-unknown","audio-live-voice-handle-not-ready"}) CollectionAssert.Contains(gaps,gap);
        Assert.IsFalse(gaps.Contains("audio-typed-voice-restore-not-implemented")); Assert.IsFalse(gaps.Contains("audio-voice-streaming-mode-dependency-not-qualified"));
        api.Invalid("RequireCompleteCoverage",data);
        CollectionAssert.Contains(gaps,"audio-live-voice-handle-disposed");
        Set(voice,"CallbackRva",0x1bc7000);api.Invalid("Validate",data);Set(voice,"CallbackRva",0x123);
        Assert.IsFalse(voice.GetType().GetFields(Flags).Any(field=>field.FieldType==typeof(IntPtr)||field.Name.Contains("RefCount")||field.Name.Contains("Allocator")));
        Set(voice,"HasFMOD",true);api.Invalid("Validate",data);Set(voice,"PCMAvailable",true);api.Call("Validate",data);
    }
}
