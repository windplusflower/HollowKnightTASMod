using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace HollowKnightTAS.Core.Tests.FullRun;
[TestClass]
public sealed class ResidentFontCacheDiagnosticTests
{
    private const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    private static Type Load(){var root=new DirectoryInfo(AppContext.BaseDirectory);while(root!=null&&!File.Exists(Path.Combine(root.FullName,"HollowKnightTAS.sln")))root=root.Parent;return Assembly.LoadFrom(Environment.GetEnvironmentVariable("HKTAS_TEST_RUNTIME_ASSEMBLY")??Path.Combine(root!.FullName,"src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll")).GetType("HollowKnightTAS.Runtime.FullRun.ResidentPresentationTypes",true)!;}
    private static object Get(object o,string n)=>o.GetType().GetField(n,F)!.GetValue(o)!;
    private sealed class Image
    {
        internal readonly byte[] Bytes=new byte[9000];internal int ReadBytes,Calls;
        internal void I(int a,int v)=>BitConverter.GetBytes(v).CopyTo(Bytes,a);
        internal void Q(int a,long v)=>BitConverter.GetBytes(v).CopyTo(Bytes,a);
        internal void Array(int a,int data,int count,int stride){Q(a,data);Q(a+16,count);Q(a+24,count*2);}
        internal void Sentinel(int a,int node){Q(a,node);Q(a+8,node);Q(a+16,node);Bytes[a+25]=1;}
        internal void Node(int a,int parent,int head){Q(a,head);Q(a+8,parent);Q(a+16,head);}
        internal void Text(int a,string s){Q(a,0);Encoding.UTF8.GetBytes(s).CopyTo(Bytes,a+8);Q(a+24,s.Length);}
        internal Image(){
            I(1008,17);I(1064,19);I(1068,23);Q(1072,1200);I(1252,-2);Q(1320,1000);
            Array(1264,2000,1,60);I(2000,65);Bytes[2056]=1;Q(1296,2100);Q(1304,2160);Q(1312,2160);I(2100,0x4e2d);
            Q(1424,2200);Q(1432,2216);Q(1440,2216);I(2200,9);
            Q(1448,3000);Q(1456,1);Q(1464,3100);Sentinel(3000,3100);Node(3100,3000,3000);I(3128,7);I(3132,8);
            I(1472,256);I(1476,128);Array(1328,4000,3,1);Bytes[4000]=1;Bytes[4001]=2;Bytes[4002]=3;
            Array(1360,4100,1,40);Text(4100,"FontA");Array(1392,4200,1,4);I(4200,99);
            Q(1512,5000);Q(5000,5100);Q(5008,1);Sentinel(5100,5200);Node(5200,5100,5100);Text(5232,"Family");I(5272,2);Q(5280,6000);I(6000,1);I(6004,3);I(6016,128);Q(6024,6300);Encoding.UTF8.GetBytes("NativeFamily").CopyTo(Bytes,6300);
        }
        internal byte[] Read(IntPtr p,int n){Calls++;ReadBytes+=n;long at=p.ToInt64();if(at<0||at+n>Bytes.Length)throw new IOException("outside fixture");var b=new byte[n];System.Array.Copy(Bytes,(int)at,b,0,n);return b;}
        internal object Capture()=>Load().GetMethod("CaptureFontCacheDiagnostic",F)!.Invoke(null,new object[]{new IntPtr(1000),"asset/font",(Func<IntPtr,int,byte[]>)Read})!;
    }
    [TestMethod] public void ActualGlyphsPackingIteratorAndFaceKeysAreReadWithoutNativeCalls(){var image=new Image();var d=image.Capture();Assert.AreEqual("",Get(d,"Reason"));Assert.AreEqual(19,Get(d,"MaterialId"));Assert.AreEqual(23,Get(d,"TextureId"));Assert.AreEqual(57,((byte[][])Get(d,"Ascii"))[0].Length);CollectionAssert.AreEqual(new[]{7,8},(int[])Get(d,"IteratorKey"));CollectionAssert.AreEqual(new[]{"FontA"},(string[])Get(d,"FontNames"));CollectionAssert.AreEqual(new[]{99},(int[])Get(d,"FallbackIds"));var face=((IList)Get(d,"Faces"))[0]!;Assert.AreEqual("Family",Get(face,"Family"));Assert.AreEqual("NativeFamily",Get(face,"NativeFamily"));Assert.AreEqual(3,Get(face,"FaceIndex"));Assert.IsTrue(image.ReadBytes<2*1024*1024);}
    [TestMethod] public void UnknownContainerShapeAndForeignIteratorHaveDiagnosticReasonsOnly(){var image=new Image();image.Q(1288,100000);var d=image.Capture();StringAssert.Contains((string)Get(d,"Reason"),"array extent");Assert.AreEqual(2,image.Calls);image=new Image();image.Q(1464,9999);d=image.Capture();StringAssert.Contains((string)Get(d,"Reason"),"iterator outside set");image=new Image();image.Q(3100,3100);d=image.Capture();StringAssert.Contains((string)Get(d,"Reason"),"cyclic tree");}
}
