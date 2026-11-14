using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace HollowKnightTAS.Core.Tests.FullRun;
[TestClass, DoNotParallelize]
public sealed class ResidentDirectorLocalServiceTests
{
    private const BindingFlags F = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    private static Type Load() {
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root!=null&&!File.Exists(Path.Combine(root.FullName,"HollowKnightTAS.sln")))root=root.Parent;
        return Assembly.LoadFrom(Environment.GetEnvironmentVariable("HKTAS_TEST_RUNTIME_ASSEMBLY")??Path.Combine(root!.FullName,"src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll")).GetType("HollowKnightTAS.Runtime.FullRun.ResidentAnimatorState",true)!;
    }
    private static object New(Type t,string name)=>Activator.CreateInstance(t.GetNestedType(name,F)!,true)!;
    private static object Call(Type t,string name,params object[] a)=>t.GetMethod(name,F)!.Invoke(null,a)!;
    private static object Get(object o,string n)=>o.GetType().GetField(n,F)!.GetValue(o)!;
    private static void Set(object o,string n,object v)=>o.GetType().GetField(n,F)!.SetValue(o,v);
    private static void Invalid(Action a)=>Assert.IsInstanceOfType<InvalidDataException>(Assert.ThrowsExactly<TargetInvocationException>(a).InnerException);
    private static object Services(Type t,IntPtr manager)=>Call(t,"CaptureLocalServices",manager,New(t,"MemoryReader"));
    [TestMethod] public void FreePoolPointersCountsAndTrackerAreNotReadOrExported() {
        var t=Load();var p=Marshal.AllocHGlobal(1120);
        try {
            Marshal.Copy(new byte[1120],0,p,1120);var a=Services(t,p);
            for(int i=0;i<9;i++)Marshal.WriteIntPtr(p,1040+i*8,new IntPtr(1+i));
            Marshal.WriteIntPtr(p,760,new IntPtr(1));Marshal.WriteInt32(p,992,1234);Marshal.WriteInt32(p,1000,int.MaxValue);Marshal.WriteIntPtr(p,1112,new IntPtr(1));
            var b=Services(t,p);Call(t,"ValidateProducers",a);Call(t,"ValidateProducers",b);
            Assert.AreEqual(Call(t,"Json",a),Call(t,"Json",b));Assert.AreEqual(true,Get(b,"LocalServices"));
        }finally{Marshal.FreeHGlobal(p);}
    }
    [TestMethod] public void MissingModeAndNonDefaultLegacyPayloadCannotMasqueradeAsLocalServices() {
        var t=Load();var p=New(t,"Producers");Set(p,"NullMethods",Enumerable.Repeat("",10).ToArray());
        Invalid(()=>Call(t,"ValidateProducers",p));Set(p,"LocalServices",true);Call(t,"ValidateProducers",p);
        Set(p,"Tracker",true);Invalid(()=>Call(t,"ValidateProducers",p));
    }
    [TestMethod] public void LiveMethodDependenciesAndQueuedCommandsRemainRejected() {
        var t=Load();var d=New(t,"Data");var registry=Get(d,"Registry");Set(registry,"Present",true);
        var p=New(t,"Producers");Set(p,"LocalServices",true);Set(p,"NullMethods",Enumerable.Repeat("",10).ToArray());Set(registry,"Producers",p);
        Assert.AreEqual(0,((IEnumerable<string>)Call(t,"ProducerGaps",d)).Count());
        ((string[])Get(p,"NullMethods"))[0]="unexpected/method";
        Assert.IsTrue(((IEnumerable<string>)Call(t,"ProducerGaps",d)).Contains("director-native-null-method-dependency"));
        ((string[])Get(p,"NullMethods"))[0]="";
        var graph=New(t,"Topology");var node=New(t,"Node");Set(node,"MethodTable",(short)1);var nodes=Array.CreateInstance(node.GetType(),1);nodes.SetValue(node,0);Set(graph,"Nodes",nodes);var graphs=Array.CreateInstance(graph.GetType(),1);graphs.SetValue(graph,0);Set(d,"Graphs",graphs);
        Assert.IsTrue(((IEnumerable<string>)Call(t,"ProducerGaps",d)).Contains("director-live-managed-method-cache-dependency"));
        Set(d,"Graphs",Array.CreateInstance(graph.GetType(),0));var q=Get(registry,"Queues");var h=New(t,"GraphHandle");var commands=Array.CreateInstance(h.GetType(),1);commands.SetValue(h,0);Set(q,"Commands",commands);Set(q,"Opcodes",new[]{0});Set(q,"CommandStack",true);
        var row=New(t,"Row");Set(row,"Owner","owner");Set(row,"Fields",new[]{2,1,2,12,157,4,16,14,4}.Select(n=>new byte[n]).ToArray());Set(row,"Padding",new byte[7]);var rows=Array.CreateInstance(row.GetType(),1);rows.SetValue(row,0);Set(d,"Rows",rows);Set(q,"OutputCounts",new[]{-1});
        Assert.IsTrue(((string[])Call(t,"RequiredCoverageGaps",d)).Contains("director-pending-atomic-command-stack"));
    }
}
