using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;
[TestClass, DoNotParallelize]
public sealed class ResidentTransactionCleanupTests
{
    private const BindingFlags Flags = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    private static Type RuntimeType(string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName,"HollowKnightTAS.sln"))) root = root.Parent;
        return Assembly.LoadFrom(Environment.GetEnvironmentVariable("HKTAS_TEST_RUNTIME_ASSEMBLY") ?? Path.Combine(root!.FullName,"src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
            .GetType("HollowKnightTAS.Runtime.FullRun."+name,true)!;
    }
    private static void Set(object value,string name,object item) => value.GetType().GetField(name,Flags)!.SetValue(value,item);
    private static string Get(object value,string name) => (string)value.GetType().GetField(name,Flags)!.GetValue(value)!;
    private static Delegate ActionFor(Type element,Action<object> action)
    {
        var parameter = Expression.Parameter(element,"snapshot");
        return Expression.Lambda(typeof(Action<>).MakeGenericType(element),Expression.Invoke(Expression.Constant(action),Expression.Convert(parameter,typeof(object))),parameter).Compile();
    }
    private static void ApplyAssets(Action<object> preflight,Action<object> apply)
    {
        var snapshot = RuntimeType("ResidentNativeAssetState").GetNestedType("Snapshot",Flags)!;
        var rows = Array.CreateInstance(snapshot,4);
        for (int index=0;index<4;index++) {
            var row = Activator.CreateInstance(snapshot,true)!;
            Set(row,"Binding",new[]{"fontA","material","fontB","texture"}[index]);
            Set(row,"Kind",index%2 == 0 ? "Font" : index == 1 ? "Material" : "Texture2D");
            rows.SetValue(row,index);
        }
        RuntimeType("ResidentAssetPipeline").GetMethod("ApplyWithFontPreflight",Flags)!
            .Invoke(null,new object[]{rows,ActionFor(snapshot,preflight),ActionFor(snapshot,apply)});
    }
    [TestMethod]
    public void EveryFontPreflightPrecedesAnyAssetWriteAndEachRealFontPlanStaysLast()
    {
        var events = new List<string>();
        ApplyAssets(row=>events.Add("pre:"+Get(row,"Binding")),row=>events.Add("apply:"+Get(row,"Binding")));
        CollectionAssert.AreEqual(new[]{"pre:fontA","pre:fontB","apply:material","apply:texture","apply:fontA","apply:fontB"},events);
    }
    [TestMethod]
    public void LaterFontPreflightFailurePreventsAllMaterialAndAtlasMutations()
    {
        var original = new InvalidOperationException("second Font readonly dependency");
        var events = new List<string>();
        var error = Assert.ThrowsExactly<TargetInvocationException>(()=>ApplyAssets(row=> {
            events.Add("pre:"+Get(row,"Binding"));
            if (Get(row,"Binding") == "fontB") throw original;
        },row=>events.Add("mutation")));
        Assert.AreSame(original,error.InnerException);
        CollectionAssert.AreEqual(new[]{"pre:fontA","pre:fontB"},events);
    }
    private sealed class Engine : IDisposable
    {
        private readonly List<string> events;
        private readonly Exception? failure;
        internal Engine(List<string> events,Exception? failure) { this.events=events; this.failure=failure; }
        public void Dispose() { events.Add("engine"); if(failure != null) throw failure; }
    }
    private static IDisposable Audio(List<string> events,bool failRestore)
    {
        bool paused = false;
        var lease = RuntimeType("ResidentAudioPauseLease");
        var backendType = lease.GetNestedType("Backend",Flags)!;
        var backend = Activator.CreateInstance(backendType,Flags,null,new object[]{
            (Func<long[]>)(()=>new long[]{1,2,3}), (Func<long,bool>)(_=>paused),
            (Action<long,bool>)((_,value)=> { paused=value; events.Add(value ? "audio begin" : "audio cleanup"); if(!value && failRestore) throw new InvalidOperationException("secondary audio cleanup"); })},null)!;
        return (IDisposable)lease.GetMethod("Begin",Flags,null,new[]{backendType},null)!.Invoke(null,new[]{backend})!;
    }
    private static void Cleanup(IDisposable engine,Exception? primary)
    {
        RuntimeType("ResidentBoundaryCoordinator").GetMethod("DisposePreservingPrimary",Flags)!.Invoke(null,new object?[]{engine,primary});
    }
    [TestMethod]
    public void ActionAndBothCleanupFailuresKeepPrimaryAndCleanAudioBeforeEngineBeforeFault()
    {
        var events = new List<string>(); var original = new InvalidOperationException("action primary"); Exception? observed = null;
        try {
            var engine = new Engine(events,new InvalidOperationException("engine secondary")); Exception? primary = null;
            try { using var audio=Audio(events,true); events.Add("action"); throw original; }
            catch(Exception error) { primary=error; throw; }
            finally { Cleanup(engine,primary); }
        } catch(Exception error) { events.Add("fault"); observed=error; }
        Assert.AreSame(original,observed);
        CollectionAssert.AreEqual(new[]{"audio begin","action","audio cleanup","engine","fault"},events);
        Assert.IsNull(RuntimeType("ResidentAudioPauseLease").GetProperty("Current",Flags)!.GetValue(null));
    }
    [TestMethod]
    public void SuccessfulActionCannotHideEngineValidationCleanupFailure()
    {
        var events = new List<string>(); var failure = new InvalidOperationException("engine validation");
        var error = Assert.ThrowsExactly<TargetInvocationException>(()=> {
            var engine = new Engine(events,failure); Exception? primary = null;
            try { using var audio=Audio(events,false); events.Add("action success"); }
            catch(Exception cause) { primary=cause; throw; }
            finally { Cleanup(engine,primary); }
        });
        Assert.AreSame(failure,error.InnerException);
        CollectionAssert.AreEqual(new[]{"audio begin","action success","audio cleanup","engine"},events);
    }
}
