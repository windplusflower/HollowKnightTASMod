using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace HollowKnightTAS.Core.Tests.FullRun;
[TestClass]
public sealed class ResidentLightServiceDiagnosticTests
{
    private const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    private static Type Load() {
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root!=null&&!File.Exists(Path.Combine(root.FullName,"HollowKnightTAS.sln")))root=root.Parent;
        return Assembly.LoadFrom(Environment.GetEnvironmentVariable("HKTAS_TEST_RUNTIME_ASSEMBLY")??Path.Combine(root!.FullName,"src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll")).GetType("HollowKnightTAS.Runtime.FullRun.ResidentPresentationTypes",true)!;
    }
    private static object Get(object o,string n)=>o.GetType().GetField(n,F)!.GetValue(o)!;
    private static void I(byte[] b,int o,int v)=>BitConverter.GetBytes(v).CopyTo(b,o);
    private static void Q(byte[] b,int o,long v)=>BitConverter.GetBytes(v).CopyTo(b,o);
    private sealed class Image {
        internal readonly byte[] Header=new byte[248],Map=new byte[104],Data=new byte[96],Cookies=new byte[24];
        internal int Bytes,Calls;
        internal Image() {
            Header[208]=1;I(Header,8,19);Q(Header,16,2000);I(Header,24,0);I(Header,28,1);
            I(Map,0,4);I(Map,4,17);I(Map,8,17);I(Map,12,99);
            Q(Header,48,3000);Q(Header,64,1);Q(Header,72,2);I(Data,0,17);I(Data,4,99);
            Q(Header,112,4000);Q(Header,128,1);Q(Header,136,2);I(Cookies,16,99);
        }
        internal byte[] Read(IntPtr p,int n) {
            Calls++;Bytes+=n;
            var b=p.ToInt64() switch {1000=>Header,2000=>Map,3000=>Data,4000=>Cookies,_=>throw new IOException("unexpected read")};
            Assert.AreEqual(b.Length,n);return (byte[])b.Clone();
        }
        internal object Capture()=>Load().GetMethod("CaptureLightServiceDiagnostic",F)!.Invoke(null,new object[]{new IntPtr(1000),2,(Func<IntPtr,int,byte[]>)Read,(Func<int,string>)(id=>id==17?"resident/light":"")})!;
    }
    [TestMethod] public void ReadsActualMapAndResidualCachesAndIdentifiesResidentLightsWithoutCallbacks() {
        var image=new Image();var d=image.Capture();Assert.AreEqual("",Get(d,"Reason"));Assert.AreEqual(1,Get(d,"MapCount"));Assert.AreEqual(1,Get(d,"DataCount"));Assert.AreEqual(1,Get(d,"CookieCount"));
        Assert.AreEqual("resident/light",Get(((IList)Get(d,"Map"))[0]!,"ResidentLight"));Assert.AreEqual(99,Get(((IList)Get(d,"Cookies"))[0]!,"CookieId"));Assert.AreEqual(4,image.Calls);
        // Empty current map does not erase a cached GI record.
        I(image.Header,28,0);I(image.Map,0,-1);d=image.Capture();Assert.AreEqual(0,Get(d,"MapCount"));Assert.AreEqual(1,Get(d,"DataCount"));
    }
    [TestMethod] public void UnknownExtentsAndInconsistentIdsProduceBoundedReasonRatherThanThrowOrScan() {
        var image=new Image();I(image.Header,24,int.MaxValue);var d=image.Capture();StringAssert.Contains((string)Get(d,"Reason"),"hash extent");Assert.AreEqual(1,image.Calls);
        image=new Image();I(image.Map,8,18);d=image.Capture();StringAssert.Contains((string)Get(d,"Reason"),"key and value ID");Assert.AreEqual(2,image.Calls);
        image=new Image();Q(image.Header,72,20000);d=image.Capture();StringAssert.Contains((string)Get(d,"Reason"),"array extent");Assert.AreEqual(2,image.Calls);Assert.IsTrue(image.Bytes<2*1024*1024);
    }
}
