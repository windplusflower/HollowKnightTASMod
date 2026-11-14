using System;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace HollowKnightTAS.Core.Tests.FullRun;
[TestClass]
public sealed class ResidentReferencedWrapperTests
{
    private static MethodInfo Method()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName,"HollowKnightTAS.sln"))) root = root.Parent;
        return Assembly.LoadFrom(Environment.GetEnvironmentVariable("HKTAS_TEST_RUNTIME_ASSEMBLY") ?? Path.Combine(root!.FullName,"src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
            .GetType("HollowKnightTAS.Runtime.FullRun.ResidentTargetAssetLookup",true)!
            .GetMethod("SelectReferencedWrapper",BindingFlags.Static|BindingFlags.NonPublic)!;
    }
    private static object Select(object created,object? authoritative,int actualId=17,bool pointer=true,bool cache=true,bool family=true)
        => Method().Invoke(null,new object?[] { created,authoritative,typeof(object),17,(Func<object,int>)(_=>actualId),(Func<object,bool>)(_=>pointer),(Func<object,bool>)(_=>cache),family })!;
    [TestMethod]
    public void NativeConnectMaySelectAnExistingCorrectWrapperInsteadOfTheNewAllocation()
    {
        var created = new object(); var authoritative = new object();
        Assert.AreNotSame(created,authoritative);
        Assert.AreSame(authoritative,Select(created,authoritative));
        Assert.AreSame(created,Select(created,created));
    }
    [TestMethod]
    public void MissingTypeIdPointerCacheAndFamilyMismatchesRemainRejectedWithBoundedEvidence()
    {
        var created = new object(); var authoritative = new object();
        foreach (var attempt in new Action[] {
            ()=>Select(created,null), ()=>Select(created,"wrong type"), ()=>Select(created,authoritative,18),
            ()=>Select(created,authoritative,pointer:false), ()=>Select(created,authoritative,cache:false), ()=>Select(created,authoritative,family:false) }) {
            var exception = Assert.ThrowsExactly<TargetInvocationException>(attempt).InnerException;
            Assert.IsInstanceOfType<InvalidDataException>(exception);
            StringAssert.Contains(exception.Message,"handleTargetType=");
            StringAssert.Contains(exception.Message,"actualId=");
            Assert.IsTrue(exception.Message.Length < 1024);
        }
    }
    [TestMethod]
    public void RegisteredParentWrapperUsesExactNativeClassMetadataRatherThanObjectAssignability()
    {
        var lookup = Method().DeclaringType!;
        var check = lookup.GetMethod("RequireRegisteredWrapperType",BindingFlags.Static|BindingFlags.NonPublic)!;
        var pointer = new IntPtr(0x1234);
        var resolved = check.Invoke(null,new object[]{typeof(UnityEngine.TextAsset),pointer,(Func<Type,IntPtr>)(_=>pointer)});
        Assert.AreEqual(typeof(UnityEngine.TextAsset),resolved);
        foreach (var args in new object[][] {
            new object[]{typeof(UnityEngine.TextAsset),pointer,(Func<Type,IntPtr>)(_=>new IntPtr(0x5678))},
            new object[]{typeof(UnityEngine.TextAsset),IntPtr.Zero,(Func<Type,IntPtr>)(_=>pointer)},
            new object[]{typeof(string),pointer,(Func<Type,IntPtr>)(_=>pointer)},
            new object[]{typeof(UnityEngine.GameObject),pointer,(Func<Type,IntPtr>)(_=>pointer)} })
            Assert.IsInstanceOfType<InvalidDataException>(Assert.ThrowsExactly<TargetInvocationException>(()=>check.Invoke(null,args)).InnerException);
    }
    [TestMethod]
    public void TextAssetWrapperStillRequiresNativeFamilyOwnerIdAndAuthoritativeCache()
    {
        var created = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(UnityEngine.TextAsset));
        var owner = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(UnityEngine.TextAsset));
        object SelectExact(object authoritative,int id,bool pointer,bool cache,bool family)
            => Method().Invoke(null,new object[]{created,authoritative,typeof(UnityEngine.TextAsset),17,
                (Func<object,int>)(_=>id),(Func<object,bool>)(_=>pointer),(Func<object,bool>)(_=>cache),family})!;
        Assert.AreSame(owner,SelectExact(owner,17,true,true,true));
        foreach (var attempt in new Action[] {
            ()=>SelectExact(owner,18,true,true,true), ()=>SelectExact(owner,17,false,true,true),
            ()=>SelectExact(owner,17,true,false,true), ()=>SelectExact(owner,17,true,true,false) })
            Assert.IsInstanceOfType<InvalidDataException>(Assert.ThrowsExactly<TargetInvocationException>(attempt).InnerException);
        var wrong = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(UnityEngine.Object));
        var error = Assert.ThrowsExactly<TargetInvocationException>(()=>SelectExact(wrong,17,true,true,true)).InnerException!;
        StringAssert.Contains(error.Message,"actualId=notChecked");
        StringAssert.Contains(error.Message,"pointerMatches=notChecked");
        StringAssert.Contains(error.Message,"cacheMatches=notChecked");
    }
    [TestMethod]
    public void NativeOnlySerializedProgramIssuesOnlyInDiscoveryAndUsesManifestIdentityForEveryAlias()
    {
        var reference = Method().DeclaringType!.Assembly.GetType("HollowKnightTAS.Runtime.FullRun.ResidentNativeAssetReference",true)!;
        var policy = reference.GetMethod("RequireProgramWrapper",BindingFlags.Static|BindingFlags.NonPublic)!;
        Assert.AreEqual(true,policy.Invoke(null,new object[]{true,"serialized",true}));
        Assert.AreEqual(false,policy.Invoke(null,new object[]{false,"serialized",true}));
        Assert.AreEqual(false,policy.Invoke(null,new object[]{true,"unmapped",true}));
        Assert.ThrowsExactly<TargetInvocationException>(()=>policy.Invoke(null,new object[]{true,"serialized",false}));
        var binding = reference.GetMethod("BindIssuedProgram",BindingFlags.Static|BindingFlags.NonPublic)!;
        var owner = new object();
        string key="resident-asset/serialized/exact-program";
        Func<object,string?> bind=value=>ReferenceEquals(value,owner)?key:null;
        Assert.AreEqual(key,binding.Invoke(null,new object[]{owner,typeof(object),bind}));
        Assert.AreEqual(key,binding.Invoke(null,new object[]{owner,typeof(object),bind}));
        foreach(string invalid in new[]{"resident-native-asset/old-native-alias","resident-asset/dynamic/0",""})
            Assert.ThrowsExactly<TargetInvocationException>(()=>binding.Invoke(null,new object[]{owner,typeof(object),(Func<object,string?>)(_=>invalid)}));
        Assert.ThrowsExactly<TargetInvocationException>(()=>binding.Invoke(null,new object[]{owner,typeof(string),bind}));
    }

}
