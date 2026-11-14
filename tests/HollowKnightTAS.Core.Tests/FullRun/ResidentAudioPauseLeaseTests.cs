using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;
[TestClass, DoNotParallelize]
public sealed class ResidentAudioPauseLeaseTests
{
    private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    private static Type Lease()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root!=null&&!File.Exists(Path.Combine(root.FullName,"HollowKnightTAS.sln")))root=root.Parent;
        return Assembly.LoadFrom(Environment.GetEnvironmentVariable("HKTAS_TEST_RUNTIME_ASSEMBLY")??Path.Combine(root!.FullName,"src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
            .GetType("HollowKnightTAS.Runtime.FullRun.ResidentAudioPauseLease",true)!;
    }
    private static object? Current()=>Lease().GetProperty("Current",Flags)!.GetValue(null);
    private static void Invoke(object lease,string name)=>lease.GetType().GetMethod(name,Flags)!.Invoke(lease,null);
    private static void Invalid(Action action)=>Assert.IsInstanceOfType<InvalidDataException>(Assert.ThrowsExactly<TargetInvocationException>(action).InnerException);
    private sealed class Platform
    {
        internal long[] Identity={11,22,33};
        internal bool Master,Child=true,Source;
        internal bool FailFirstSet,IgnoreFirstSet;
        internal int Sets,Gets;
        internal readonly List<string> Events=new();
        internal object Begin()
        {
            var backendType=Lease().GetNestedType("Backend",Flags)!;
            var backend=Activator.CreateInstance(backendType,Flags,null,new object[]{
                (Func<long[]>)(()=>{Events.Add("identity");return (long[])Identity.Clone();}),
                (Func<long,bool>)(master=>{Assert.AreEqual(33L,master);Gets++;Events.Add("get:"+Master);return Master;}),
                (Action<long,bool>)((master,value)=>{Assert.AreEqual(33L,master);Sets++;Events.Add("set:"+value);if(!(IgnoreFirstSet&&Sets==1))Master=value;if(FailFirstSet&&Sets==1)throw new InvalidOperationException("set failed after mutation");})},null)!;
            return Lease().GetMethod("Begin",Flags,null,new[]{backendType},null)!.Invoke(null,new[]{backend})!;
        }
    }
    [TestMethod]
    public void SystemOpaqueIsReadOnlyAndRequiresCurrentLiveUnchangedLease()
    {
        var platform=new Platform();var lease=platform.Begin();
        object? System()=>Lease().GetMethod("RequireSystemOpaque",Flags)!.Invoke(lease,null);
        try {
            Assert.AreEqual(new IntPtr(22),System());Assert.AreEqual(1,platform.Sets);
            platform.Identity[1]=23;Invalid(()=>System());platform.Identity[1]=22;
            platform.Master=false;Invalid(()=>System());platform.Master=true;
        } finally {((IDisposable)lease).Dispose();}
        Invalid(()=>System());
        var absent=new Platform {Identity=new long[]{11,0,0}};var noSystem=absent.Begin();
        try {Invalid(()=>Lease().GetMethod("RequireSystemOpaque",Flags)!.Invoke(noSystem,null));Assert.AreEqual(0,absent.Sets);}
        finally {((IDisposable)noSystem).Dispose();}
    }
    [TestMethod]
    public void OriginalFalseAndTrueRestoreWithoutChangingChildOrSourceFlags()
    {
        foreach(bool original in new[]{false,true}) {
            var platform=new Platform{Master=original};var lease=platform.Begin();
            try {Assert.IsTrue(platform.Master);Assert.AreSame(lease,Current());Invoke(lease,"Require");Invoke(lease,"Complete");Assert.AreEqual(original,platform.Master);Assert.IsNull(Current());Assert.IsTrue(platform.Child);Assert.IsFalse(platform.Source);}
            finally{((IDisposable)lease).Dispose();}
            Assert.AreEqual(2,platform.Sets);
        }
    }
    [TestMethod]
    public void BeginFailureCompensatesAttemptedMutationAndNeverPublishesCurrent()
    {
        foreach(bool throwSetter in new[]{false,true}) {
            var platform=new Platform{FailFirstSet=throwSetter,IgnoreFirstSet=!throwSetter};
            Assert.ThrowsExactly<TargetInvocationException>(()=>platform.Begin());
            Assert.IsFalse(platform.Master);Assert.AreEqual(2,platform.Sets);Assert.IsNull(Current());
        }
    }
    [TestMethod]
    public void NoSystemModesAndIdentityChangesCannotPretendToBeSilentSuccess()
    {
        foreach(long manager in new long[]{0,11}) {
            var platform=new Platform{Identity=new[]{manager,0L,0L}};var lease=platform.Begin();
            try{Invoke(lease,"Require");Assert.AreEqual(0,platform.Gets);Assert.AreEqual(0,platform.Sets);platform.Identity=new[]{manager,22L,33L};Invalid(()=>Invoke(lease,"Require"));}
            finally{((IDisposable)lease).Dispose();}
            Assert.IsNull(Current());Assert.AreEqual(0,platform.Sets);
        }
        var live=new Platform();var active=live.Begin();
        try{live.Identity[2]=44;Invalid(()=>Invoke(active,"Require"));}
        finally{((IDisposable)active).Dispose();}
        Assert.IsNull(Current());Assert.AreEqual(1,live.Sets,"Never restore to a substituted master identity.");
    }
    [TestMethod]
    public void ActionFailureCleanupPrecedesFaultAndCannotMaskTheOriginalException()
    {
        var platform=new Platform();var original=new InvalidOperationException("original action");Exception? observed=null;
        try{using var lease=(IDisposable)platform.Begin();platform.Events.Add("action");throw original;}
        catch(Exception error){platform.Events.Add("fault");observed=error;}
        Assert.AreSame(original,observed);Assert.IsFalse(platform.Master);Assert.IsNull(Current());
        Assert.IsTrue(platform.Events.IndexOf("set:False")<platform.Events.IndexOf("fault"));
        var failed=new Platform();observed=null;
        try{using var lease=(IDisposable)failed.Begin();failed.Identity[2]=99;throw original;}
        catch(Exception error){observed=error;}
        Assert.AreSame(original,observed);Assert.IsNull(Current());
    }
    [TestMethod]
    public void RestoreReadbackFailureCannotCompleteAndNestedOrCrossThreadCallsAreRejected()
    {
        var platform=new Platform();var lease=platform.Begin();
        try {
            Invalid(()=>platform.Begin());Exception? require=null,dispose=null;
            var worker=new Thread(()=>{try{Invoke(lease,"Require");}catch(Exception error){require=error;}try{((IDisposable)lease).Dispose();}catch(Exception error){dispose=error;}});
            worker.Start();worker.Join();Assert.IsNotNull(require);Assert.IsInstanceOfType<InvalidDataException>(dispose);Assert.AreSame(lease,Current());
            platform.IgnoreFirstSet=true;platform.Sets=0;
            Invalid(()=>Invoke(lease,"Complete"));Assert.AreSame(lease,Current());
        }finally{((IDisposable)lease).Dispose();}
        Assert.IsFalse(platform.Master);Assert.IsNull(Current());
    }
}
