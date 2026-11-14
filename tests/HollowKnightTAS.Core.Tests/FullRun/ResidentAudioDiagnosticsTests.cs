using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentAudioDiagnosticsTests
{
    private const BindingFlags Flags = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    private static Type Load()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName,"HollowKnightTAS.sln"))) root = root.Parent;
        Assert.IsNotNull(root);
        return Assembly.LoadFrom(Environment.GetEnvironmentVariable("HKTAS_TEST_RUNTIME_ASSEMBLY") ?? Path.Combine(root.FullName,"src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
            .GetType("HollowKnightTAS.Runtime.FullRun.ResidentAudioState",true)!;
    }
    private static object Call(Type type,string name,params object[] args) => type.GetMethod(name,Flags)!.Invoke(null,args)!;
    private static object Get(object value,string field) => value.GetType().GetField(field,Flags)!.GetValue(value)!;
    [TestMethod]
    public void SoundHandleDiagnosticPreservesTypedScalarsAndLeavesStreamingUnknown()
    {
        var type = Load();
        var body = new byte[208];
        void Put(int offset, byte[] bytes) => Array.Copy(bytes,0,body,offset,bytes.Length);
        Put(128,BitConverter.GetBytes(3));
        Put(132,BitConverter.GetBytes(0xf1234567u));
        Put(144,BitConverter.GetBytes(0x89abcdefu));
        Put(120,BitConverter.GetBytes(123L));
        Put(192,BitConverter.GetBytes(0x123456789abcdef0UL));
        Put(200,BitConverter.GetBytes(9876543210UL));
        var result = Call(type,"DecodeSoundHandleDiagnostic",body);
        Assert.AreEqual("ready",Get(result,"LoadStateMeaning"));
        Assert.AreEqual(0xf1234567u,Get(result,"Mode"));
        Assert.AreEqual(0x89abcdefu,Get(result,"Flags"));
        Assert.AreEqual(true,Get(result,"HasFMODSound"));
        Assert.AreEqual(false,Get(result,"HasFMODFSB"));
        Assert.AreEqual(0x123456789abcdef0UL,Get(result,"ResourceOffset"));
        Assert.AreEqual(9876543210UL,Get(result,"ResourceSize"));
        Assert.AreEqual("unknown",Get(result,"Streaming"));
        Put(128,BitConverter.GetBytes(71));
        Assert.AreEqual("unknown",Get(Call(type,"DecodeSoundHandleDiagnostic",body),"LoadStateMeaning"));
        var error = Assert.ThrowsExactly<TargetInvocationException>(()=>Call(type,"DecodeSoundHandleDiagnostic",new byte[207]));
        Assert.IsInstanceOfType<InvalidDataException>(error.InnerException);
    }
    [TestMethod]
    public void BufferedAudioDiagnosticProjectionExcludesNativePointersAndPaddingWithoutChangingBits()
    {
        var type = Load(); var body = new byte[360];
        for (int i=0;i<body.Length;i++) body[i]=(byte)(i*19);
        var before = Call(type,"DecodeChannelDiagnostic",body,"alias");
        var fields = (byte[][])Get(before,"ParameterScalars");
        CollectionAssert.AreEqual(new[] {60,40,2,4},fields.Select(value=>value.Length).ToArray());
        CollectionAssert.AreEqual(body.Skip(8).Take(60).ToArray(),fields[0]);
        CollectionAssert.AreEqual(body.Skip(88).Take(40).ToArray(),fields[1]);
        CollectionAssert.AreEqual(body.Skip(152).Take(2).ToArray(),fields[2]);
        Assert.AreEqual((byte)(body[160]&63),fields[3][3]);
        // Callback/group pointer slots, four-byte alignment hole and dirty-field padding.
        foreach (int offset in Enumerable.Range(68,20).Concat(Enumerable.Range(140,12)).Concat(new[] {157,158,159,161,162,163,164,165,166,167})) body[offset] ^= 0xff;
        body[160] ^= 0xc0;
        Assert.AreEqual(JsonConvert.SerializeObject(before),JsonConvert.SerializeObject(Call(type,"DecodeChannelDiagnostic",body,"alias")));
        body[120] ^= 0xff; // Buffered PCM request is retained, never labeled live position.
        Assert.AreNotEqual(JsonConvert.SerializeObject(before),JsonConvert.SerializeObject(Call(type,"DecodeChannelDiagnostic",body,"alias")));
        Assert.IsFalse(type.GetNestedType("Data",Flags)!.GetFields(Flags).Any(field=>field.FieldType.Name.Contains("Diagnostic")));
    }
    [TestMethod]
    public void AudioDiagnosticClassifiesProcessLocalCallbacksAndSourceGroupsWithoutBackendCalls()
    {
        var type=Load(); var engine=new IntPtr(0x100000);
        Assert.AreEqual("null",Call(type,"DiagnosticCallback",IntPtr.Zero,engine,100));
        Assert.AreEqual("UnityPlayer+0x1",Call(type,"DiagnosticCallback",IntPtr.Add(engine,1),engine,100));
        Assert.AreEqual("unknown-present",Call(type,"DiagnosticCallback",IntPtr.Add(engine,100),engine,100));
        Assert.AreEqual("source-dry-and-wet",Call(type,"DiagnosticGroup",new IntPtr(3),new IntPtr(3),new IntPtr(3)));
        Assert.AreEqual("source-dry",Call(type,"DiagnosticGroup",new IntPtr(3),new IntPtr(3),new IntPtr(4)));
        Assert.AreEqual("source-wet",Call(type,"DiagnosticGroup",new IntPtr(4),new IntPtr(3),new IntPtr(4)));
        Assert.AreEqual("other-present",Call(type,"DiagnosticGroup",new IntPtr(5),new IntPtr(3),new IntPtr(4)));
        Assert.AreEqual("null",Call(type,"DiagnosticGroup",IntPtr.Zero,IntPtr.Zero,IntPtr.Zero));
    }
    [TestMethod]
    public void AudioDiagnosticReadCacheReusesFirstObservationAndRejectsBudgetBeforeReading()
    {
        var type=Load().GetNestedType("DiagnosticMemory",Flags)!;
        var reader=Activator.CreateInstance(type,true)!; var read=type.GetMethod("Read",Flags)!;
        var memory=Marshal.AllocHGlobal(24);
        try {
            Marshal.Copy(new byte[24],0,memory,24);
            var initial=(byte[])read.Invoke(reader,new object[] {memory,24})!;
            Marshal.WriteByte(memory,0,99);
            CollectionAssert.AreEqual(initial,(byte[])read.Invoke(reader,new object[] {memory,24})!);
            Assert.AreEqual((byte)0,((byte[])read.Invoke(reader,new object[] {memory,8})!)[0]);
            var error=Assert.ThrowsExactly<TargetInvocationException>(()=>read.Invoke(reader,new object[] {new IntPtr(1),4*1024*1024+1}));
            Assert.IsInstanceOfType<InvalidDataException>(error.InnerException);
        } finally {Marshal.FreeHGlobal(memory);}
    }
}
