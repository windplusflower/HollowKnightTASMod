using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UnityEngine;
namespace HollowKnightTAS.Core.Tests.FullRun;
[TestClass, DoNotParallelize]
public sealed class ResidentFontMutableClosureTests
{
    private const BindingFlags Flags = BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private static Assembly Runtime()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName,"HollowKnightTAS.sln"))) root = root.Parent;
        return Assembly.LoadFrom(Environment.GetEnvironmentVariable("HKTAS_TEST_RUNTIME_ASSEMBLY") ?? Path.Combine(root!.FullName,"src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
    }
    private static Type Type(string name) => Runtime().GetType("HollowKnightTAS.Runtime.FullRun."+name,true)!;
    private static object New(string owner,string nested) => Activator.CreateInstance(Type(owner).GetNestedType(nested,Flags)!,true)!;
    private static object Get(object value,string name) => value.GetType().GetField(name,Flags)!.GetValue(value)!;
    private static void Set(object value,string name,object data) => value.GetType().GetField(name,Flags)!.SetValue(value,data);
    private static object Call(string owner,string name,params object[] arguments) => Type(owner).GetMethod(name,Flags)!.Invoke(null,arguments)!;
    private static void Rows(object value,string field,IEnumerable<object> rows)
    {
        var values=rows.ToArray();var info=value.GetType().GetField(field,Flags)!;var array=Array.CreateInstance(info.FieldType.GetElementType()!,values.Length);
        for (int index=0;index<values.Length;index++) array.SetValue(values[index],index);
        info.SetValue(value,array);
    }
    private static object Record(System.Type actual,int id)
    {
        var record=New("ResidentAssetBindings","AssetRecord");
        var metadata=Type("ResidentObjectDirectory").GetNestedType("TypeRecord",Flags)!.GetMethod("Capture",Flags)!.Invoke(null,new object[]{actual})!;
        Set(record,"Type",metadata);Set(record,"Kind","serialized");Set(record,"SourcePath","archive:/font-cache");Set(record,"LocalIdentifier",(long)id);Set(record,"Name","asset"+id);
        Set(record,"Binding",Call("ResidentAssetBindings","SerializedKey",metadata,"archive:/font-cache",(long)id));
        return record;
    }
    private static object Snapshot(object record,string kind)
    {
        var state=New("ResidentNativeAssetState","Snapshot");
        foreach (string name in new[]{"Binding","Name","Type","HideFlags"}) Set(state,name,Get(record,name));
        Set(state,"Kind",kind);return state;
    }
    private static object FontData()
    {
        var data=New("ResidentFontCacheState","Data");Set(data,"Header",new byte[8]);Set(data,"Scalars",new[]{new byte[16],new byte[2],new byte[8],new byte[36]});Set(data,"IteratorAtEnd",true);return data;
    }
    private static object Fixture()
    {
        var records=new[]{Record(typeof(Font),1),Record(typeof(Material),2),Record(typeof(Texture2D),3)};
        var font=FontData();Set(font,"Material",Get(records[1],"Binding"));Set(font,"Texture",Get(records[2],"Binding"));
        var snapshots=new[]{Snapshot(records[0],"Font"),Snapshot(records[1],"Material"),Snapshot(records[2],"Texture2D")};Set(snapshots[0],"Font",font);
        Set(snapshots[1],"Material",New("ResidentNativeAssetState","MaterialState"));
        var texture=New("ResidentNativeAssetState","TextureState");Set(texture,"Width",1);Set(texture,"Height",1);Set(texture,"MipmapCount",1);Set(snapshots[2],"Texture",texture);
        var result=New("ResidentAssetPipeline","Data");Rows(result,"Manifest",records.OrderBy(record=>(string)Get(record,"Binding"),StringComparer.Ordinal));Rows(result,"Snapshots",snapshots.OrderBy(state=>(string)Get(state,"Binding"),StringComparer.Ordinal));Set(result,"SharedMutable",records.Select(record=>(string)Get(record,"Binding")).OrderBy(key=>key,StringComparer.Ordinal).ToArray());return result;
    }
    private static void Invalid(Action action) => Assert.IsInstanceOfType<InvalidDataException>(Assert.ThrowsExactly<TargetInvocationException>(action).InnerException);
    [TestMethod]
    public void VersionFourAllowsOnlyFontRootAndExactMutableAtlasReferences()
    {
        var data=Fixture();Call("ResidentAssetPipeline","Validate",data);
        var wire=(byte[])Call("ResidentAssetDataWire","Encode",data);var copy=Call("ResidentAssetDataWire","Decode",wire);Call("ResidentAssetPipeline","Validate",copy);
        Set(data,"SharedMutable",((string[])Get(data,"SharedMutable")).Concat(new[]{"unrelated/asset"}).OrderBy(key=>key,StringComparer.Ordinal).ToArray());Invalid(()=>Call("ResidentAssetPipeline","Validate",data));
    }
    [TestMethod]
    public void MissingDuplicateAndOrphanDeclarationsAndOldVersionPromotionAreRejected()
    {
        foreach (int mode in new[]{0,1,2,3}) {
            var data=Fixture();var shared=(string[])Get(data,"SharedMutable");
            if (mode==0) Set(data,"SharedMutable",shared.Skip(1).ToArray());
            if (mode==1) Set(data,"SharedMutable",shared.Concat(new[]{shared[0]}).OrderBy(key=>key,StringComparer.Ordinal).ToArray());
            if (mode==2) Set(data,"Version",3);
            if (mode==3) Rows(data,"Snapshots",((Array)Get(data,"Snapshots")).Cast<object>().Skip(1));
            Invalid(()=>Call("ResidentAssetPipeline","Validate",data));
        }
        foreach(int version in new[]{2,3}) {var data=New("ResidentAssetPipeline","Data");Set(data,"Version",version);Call("ResidentAssetPipeline","Validate",data);}
        Invalid(()=>Call("ResidentAssetDataWire","Decode",System.Text.Encoding.UTF8.GetBytes("{}")));
    }
    [TestMethod]
    public void BorrowLoadedNullIdIsTheOnlyNoOwnerResultAndDoesNotBindAnything()
    {
        var method=Type("ResidentNativeAssetReference").GetMethod("BorrowLoaded",Flags)!;
        object?[] arguments={0,typeof(Font),(Func<object,string?>)(_=>throw new AssertFailedException("null reference must not bind")),null};
        Assert.IsNull(method.Invoke(null,arguments));Assert.AreEqual("",arguments[3]);
    }
    private static object Presentation(object assets,bool cache,bool identity=false)
    {
        var result=New("ResidentPresentationTypes","Data");var row=New("ResidentPresentationTypes","Row");
        var font=((Array)Get(assets,"Manifest")).Cast<object>().Single(record=>(string)Get(record,"Name") == "asset1");
        Set(row,"Owner","resident/text");Set(row,"Kind","text-mesh");Set(row,"Font",Get(font,"Binding"));Set(row,"Scalars",new[]{new byte[33]});Set(row,"FontCacheDependency",cache);Set(row,"StaticFontDependency",identity);Rows(result,"Rows",new[]{row});return result;
    }
    [TestMethod]
    public void DynamicFontMarkerRequiresExactVersionFourCacheAndTypedAtlasDependencies()
    {
        var data=Fixture();var presentation=Presentation(data,true);Call("ResidentAssetPipeline","ValidatePresentationDependencies",presentation,data);
        foreach(int mode in new[]{0,1,2,3,4}) {
            data=Fixture();presentation=Presentation(data,true);
            var states=((Array)Get(data,"Snapshots")).Cast<object>().ToArray();var font=states.Single(state=>(string)Get(state,"Kind")=="Font");
            if(mode==0) {Set(data,"SharedMutable",Array.Empty<string>());Rows(data,"Snapshots",Array.Empty<object>());}
            if(mode==1) {Set(font,"Kind","Unsupported");Set(font,"Font",null!);}
            if(mode==2) Set(data,"Version",3);
            if(mode==3) Set(data,"SharedMutable",Array.Empty<string>());
            if(mode==4) {var material=states.Single(state=>(string)Get(state,"Kind")=="Material");Set(material,"Kind","Unsupported");Set(material,"Material",null!);}
            Invalid(()=>Call("ResidentAssetPipeline","ValidatePresentationDependencies",presentation,data));
        }
    }
    [TestMethod]
    public void OrdinaryStaticFontRemainsIdentityOnlyAndCannotMasqueradeAsAnotherType()
    {
        var data=Fixture();Set(data,"SharedMutable",Array.Empty<string>());Rows(data,"Snapshots",Array.Empty<object>());Set(data,"Version",3);
        var presentation=Presentation(data,false,true);Call("ResidentAssetPipeline","ValidatePresentationDependencies",presentation,data);
        var row=((Array)Get(presentation,"Rows")).GetValue(0)!;var texture=((Array)Get(data,"Manifest")).Cast<object>().Single(record=>(string)Get(record,"Name")=="asset3");Set(row,"Font",Get(texture,"Binding"));
        Invalid(()=>Call("ResidentAssetPipeline","ValidatePresentationDependencies",presentation,data));
    }
}
