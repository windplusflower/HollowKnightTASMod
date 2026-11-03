using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentSerializedFileIndexTests
{
    private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    private static readonly Type Api=Load();
    private static Type Load()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root!=null && !File.Exists(Path.Combine(root.FullName,"HollowKnightTAS.sln"))) root=root.Parent;
        Assert.IsNotNull(root);
        return Assembly.LoadFrom(Environment.GetEnvironmentVariable("HKTAS_TEST_RUNTIME_ASSEMBLY") ?? Path.Combine(root.FullName,"src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
            .GetType("HollowKnightTAS.Runtime.FullRun.ResidentSerializedFileIndex",true)!;
    }
    private static object Read(Stream stream)=>Api.GetMethod("Read",Flags)!.Invoke(null,new object[]{stream})!;
    private static T Get<T>(object row,string name)=>(T)row.GetType().GetProperty(name,Flags)!.GetValue(row)!;
    private static object[] Rows(object row,string name)=>((IEnumerable)Get<object>(row,name)).Cast<object>().ToArray();
    private static object Lookup(object index,long id)=>index.GetType().GetMethod("Lookup",Flags)!.Invoke(index,new object[]{id})!;
    private static void Rejected(Fixture fixture)
    {
        using var stream=new MemoryStream(fixture.Bytes); var error=Assert.ThrowsExactly<TargetInvocationException>(()=>Read(stream));
        Assert.IsInstanceOfType<InvalidDataException>(error.InnerException); Assert.AreEqual(0L,stream.Position); Assert.IsTrue(stream.CanRead);
    }
    private sealed class Writer : IDisposable
    {
        internal readonly MemoryStream Stream=new(); internal readonly bool Little;
        internal Writer(bool little){Little=little;}
        internal void Number(ulong value,int size){for(int i=0;i<size;i++) Stream.WriteByte((byte)(value>>(8*(Little ? i : size-1-i))));}
        internal void I32(int value)=>Number(unchecked((uint)value),4);
        internal void I64(long value)=>Number(unchecked((ulong)value),8);
        internal void Raw(byte[] value)=>Stream.Write(value,0,value.Length);
        internal void Text(string value){Raw(Encoding.UTF8.GetBytes(value));Number(0,1);}
        internal void Align(){while((Stream.Position&3)!=0) Number(0xa5,1);}
        public void Dispose()=>Stream.Dispose();
    }
    private sealed class Fixture
    {
        internal readonly byte[] Bytes; internal readonly bool Little; internal readonly Dictionary<string,int> At=new();
        internal readonly long DataOffset;
        internal Fixture(bool little=true,bool tree=false,bool duplicateTypes=false,bool duplicateScripts=false,bool duplicateExternals=false,bool duplicateRefs=false,bool duplicateDependencies=false,bool expandStrings=false)
        {
            Little=little; using var m=new Writer(little);
            void Mark(string key)=>At.Add(key,checked(48+(int)m.Stream.Position));
            void Tree(string key)
            {
                if(expandStrings) {
                    m.I32(1024);m.I32(65537);
                    for(int i=0;i<1024;i++){m.Number(1,2);m.Number(i==0 ? 0UL : 1UL,1);m.Number(0,1);m.Number(0x80000000U|76U,4);m.Number((ulong)i,4);m.I32(1);m.I32(i);m.I32(0);m.Number(0,8);}
                    m.Raw(Enumerable.Repeat((byte)'A',65536).Concat(new byte[1]).ToArray());return;
                }
                Mark(key+".NodeCount");m.I32(2);m.I32(11);
                m.Number(1,2);m.Number(0,1);m.Number(0,1);Mark(key+".RootType");m.Number(0x80000000U|76U,4);m.Number(0,4);m.I32(-1);m.I32(0);m.I32(0);m.Number(0x1122334455667788,8);
                m.Number(1,2);Mark(key+".ChildLevel");m.Number(1,1);m.Number(0,1);m.Number(0x80000000U|222U,4);m.Number(5,4);m.I32(4);m.I32(1);m.I32(0x4000);m.Number(7,8);
                m.Raw(Encoding.UTF8.GetBytes("root\0value\0"));
            }
            void Type(int classId,bool reference,string key)
            {
                m.I32(classId);Mark(key+".Stripped");m.Number(0,1);Mark(key+".ScriptIndex");m.Number(classId==114 ? 0UL : 65535UL,2);
                if(classId==114)m.Raw(Enumerable.Repeat((byte)0x91,16).ToArray());
                Mark(key+".Hash");m.Raw(Enumerable.Repeat((byte)classId,16).ToArray());
                if(tree){Tree(key);if(reference){m.Text("Example");m.Text("Tests");m.Text("Game.dll");}else{m.I32(duplicateDependencies ? 2 : 0);if(duplicateDependencies){m.I32(1);m.I32(1);}}}
            }
            m.Text("2020.2.2f1");m.I32(19);Mark("TreeControl");m.Number(tree ? 1UL : 0UL,1);Mark("TypeCount");m.I32(duplicateTypes ? 4 : 3);
            Type(28,false,"Type0");Type(114,false,"Type1");Type(115,false,"Type2");if(duplicateTypes)Type(28,false,"Type3");
            Mark("ObjectCount");m.I32(3);
            for(int i=0;i<3;i++){m.Align();Mark("Object"+i+".Id");m.I64(i+1);Mark("Object"+i+".Offset");m.I64(i*8);Mark("Object"+i+".Length");m.Number(4,4);Mark("Object"+i+".Type");m.I32(i);}
            Mark("ScriptCount");m.I32(duplicateScripts ? 2 : 1);
            for(int i=0;i<(duplicateScripts ? 2 : 1);i++){Mark("Script"+i+".File");m.I32(0);m.Align();Mark("Script"+i+".Id");m.I64(3);}
            Mark("ExternalCount");m.I32(duplicateExternals ? 2 : 1);
            for(int i=0;i<(duplicateExternals ? 2 : 1);i++){m.Text("");m.Raw(new byte[16]);Mark("External"+i+".Type");m.I32(2);m.Text("archive:/actual/serialized");}
            Mark("RefCount");m.I32(duplicateRefs ? 2 : 1);Type(114,true,"Ref0");if(duplicateRefs)Type(114,true,"Ref1");m.Text("fixture");
            var metadata=m.Stream.ToArray();DataOffset=(48+metadata.Length+15)&~15L;
            using var h=new Writer(false);h.I32(0);h.I32(0);h.I32(22);h.I32(0);h.Number(little ? 0UL : 1UL,1);h.Raw(new byte[3]);h.I32(metadata.Length);h.I64(DataOffset+24);h.I64(DataOffset);h.I64(0);h.Raw(metadata);
            while(h.Stream.Position<DataOffset)h.Number(0,1);h.Raw(Enumerable.Range(0,24).Select(i=>(byte)i).ToArray());Bytes=h.Stream.ToArray();
        }
        internal void Put(int offset,ulong value,int size,bool? little=null){var order=little??Little;for(int i=0;i<size;i++)Bytes[offset+i]=(byte)(value>>(8*(order ? i : size-1-i)));}
        internal void Set(string key,long value,int size=4)=>Put(At[key],unchecked((ulong)value),size);
    }
    [TestMethod]
    [DataRow(true,false)] [DataRow(false,false)] [DataRow(true,true)] [DataRow(false,true)]
    public void Format22ParsesBothMetadataOrdersScriptHashesAndOptionalTree(bool little,bool tree)
    {
        var fixture=new Fixture(little,tree);using var stream=new MemoryStream(fixture.Bytes);var index=Read(stream);
        Assert.AreEqual(little ? (byte)0 : (byte)1,Get<byte>(index,"Endianness"));Assert.AreEqual((long)fixture.Bytes.Length,Get<long>(index,"FileLength"));Assert.AreEqual(fixture.DataOffset,Get<long>(index,"DataOffset"));
        Assert.AreEqual(3,Rows(index,"Types").Length);Assert.AreEqual(3,Rows(index,"Objects").Length);Assert.AreEqual(1,Rows(index,"Scripts").Length);Assert.AreEqual(1,Rows(index,"Externals").Length);Assert.AreEqual(1,Rows(index,"ReferencedTypes").Length);
        var row=Lookup(index,2);Assert.AreEqual(fixture.DataOffset+8,Get<long>(row,"Offset"));Assert.AreEqual(4,Get<int>(row,"Length"));Assert.AreEqual(114,Get<int>(row,"ClassId"));
        CollectionAssert.AreEqual(Enumerable.Repeat((byte)114,16).ToArray(),Get<byte[]>(row,"TypeHash"));
        Get<byte[]>(row,"TypeHash")[0]=0;Assert.AreEqual((byte)114,Get<byte[]>(row,"TypeHash")[0]);
        var type=Rows(index,"Types")[1];Assert.AreEqual(16,Get<byte[]>(type,"ScriptId").Length);Assert.AreEqual((short)0,Get<short>(type,"ScriptTypeIndex"));
        Assert.AreEqual(tree ? 2 : 0,Rows(type,"Tree").Length);if(tree){var node=Rows(type,"Tree")[0];Assert.AreEqual("bool",Get<string>(node,"TypeName"));Assert.AreEqual("root",Get<string>(node,"Name"));Assert.AreEqual(0x1122334455667788UL,Get<ulong>(node,"RefTypeHash"));Assert.AreEqual("Example",Get<string>(Rows(index,"ReferencedTypes")[0],"ClassName"));}
        Assert.AreEqual(0L,stream.Position);Assert.IsTrue(stream.CanRead);
        var missing=Assert.ThrowsExactly<TargetInvocationException>(()=>Lookup(index,99));Assert.IsInstanceOfType<InvalidDataException>(missing.GetBaseException());
    }
    [TestMethod]
    public void HeaderVariantsUnsupportedVersionsCountsAndTruncationAreRejected()
    {
        foreach(var mutation in new Action<Fixture>[] {f=>f.Put(8,21,4,false),f=>f.Put(8,23,4,false),f=>f.Bytes[16]=2,f=>f.Bytes[17]=1,f=>f.Put(0,1,4,false),f=>f.Put(20,uint.MaxValue,4,false),f=>f.Put(24,ulong.MaxValue,8,false),f=>f.Put(32,47,8,false),f=>f.Put(40,1,8,false),f=>f.Set("TypeCount",int.MaxValue),f=>f.Set("ObjectCount",-1),f=>f.Set("TreeControl",2,1),f=>f.Set("Type0.Stripped",2,1),f=>f.Set("Type0.ScriptIndex",-2,2),f=>f.Bytes[48]=0xff}) {var f=new Fixture();mutation(f);Rejected(f);}
        var valid=new Fixture();foreach(var n in new[]{0,19,47,48,valid.Bytes.Length-1}){using var s=new MemoryStream(valid.Bytes.Take(n).ToArray());var e=Assert.ThrowsExactly<TargetInvocationException>(()=>Read(s));Assert.IsInstanceOfType<InvalidDataException>(e.InnerException);}
    }
    [TestMethod]
    public void ObjectIdentityOffsetsLengthsAndTypeReferencesCannotEscapeTheFile()
    {
        foreach(var mutation in new Action<Fixture>[] {f=>f.Set("Object0.Id",0,8),f=>f.Set("Object1.Id",1,8),f=>f.Set("Object0.Offset",-1,8),f=>f.Set("Object0.Offset",long.MaxValue,8),f=>f.Set("Object0.Length",uint.MaxValue),f=>f.Set("Object0.Length",25),f=>f.Set("Object1.Offset",0,8),f=>f.Set("Object0.Type",-1),f=>f.Set("Object0.Type",3),f=>f.Set("Type1.ScriptIndex",1,2),f=>f.Set("Script0.File",2),f=>f.Set("Script0.Id",99,8),f=>f.Set("External0.Type",4)}){var f=new Fixture();mutation(f);Rejected(f);}
        var negative=new Fixture();negative.Set("Object0.Id",long.MinValue,8);using var stream=new MemoryStream(negative.Bytes);Assert.AreEqual(long.MinValue,Get<long>(Lookup(Read(stream),long.MinValue),"LocalId"));
    }
    [TestMethod]
    public void DuplicateDirectoriesAndMalformedTreeRangesAreRejected()
    {
        foreach(var f in new[]{new Fixture(duplicateTypes:true),new Fixture(duplicateScripts:true),new Fixture(duplicateExternals:true),new Fixture(duplicateRefs:true),new Fixture(tree:true,duplicateDependencies:true)})Rejected(f);
        foreach(var mutation in new Action<Fixture>[] {f=>f.Set("Type0.NodeCount",0),f=>f.Set("Type0.NodeCount",int.MaxValue),f=>f.Set("Type0.RootType",0x8000ffffU),f=>f.Set("Type0.RootType",uint.MaxValue),f=>f.Set("Type0.ChildLevel",2,1)}){var f=new Fixture(tree:true);mutation(f);Rejected(f);}
    }
    [TestMethod]
    public void MaliciousTreeSuffixAliasesCannotExpandSmallMetadataIntoUnboundedStrings()
    {
        var fixture=new Fixture(tree:true,expandStrings:true);Assert.IsLessThan(1024*1024,fixture.Bytes.Length);
        using var stream=new MemoryStream(fixture.Bytes);var error=Assert.ThrowsExactly<TargetInvocationException>(()=>Read(stream));
        Assert.IsInstanceOfType<InvalidDataException>(error.InnerException);StringAssert.Contains(error.InnerException!.Message,"expanded metadata strings");Assert.AreEqual(0L,stream.Position);
    }
    private sealed class MetadataOnlyStream : MemoryStream
    {
        private readonly long limit;
        internal MetadataOnlyStream(byte[] bytes,long end):base(bytes){limit=end;}
        public override int Read(byte[] buffer,int offset,int count){if(Position+count>limit)throw new InvalidOperationException("parser read an object body");return base.Read(buffer,offset,count);}
    }
    [TestMethod]
    public void ParserReadsOnlyProvidedMetadataAndRestoresNonzeroOrigin()
    {
        var f=new Fixture();var bytes=new byte[3].Concat(f.Bytes).ToArray();using var stream=new MetadataOnlyStream(bytes,3+f.DataOffset);stream.Position=3;
        var index=Read(stream);Assert.AreEqual(f.DataOffset,Get<long>(Lookup(index,1),"Offset"));Assert.AreEqual(3L,stream.Position);Assert.IsTrue(stream.CanRead);
    }
    private sealed class ShortReadStream : MemoryStream
    {
        private readonly bool seek;
        internal ShortReadStream(byte[] bytes,bool canSeek=true):base(bytes){seek=canSeek;}
        public override bool CanSeek=>seek;
        public override int Read(byte[] buffer,int offset,int count)=>base.Read(buffer,offset,Math.Min(7,count));
    }
    [TestMethod]
    public void IndependentMetadataInputMustMatchEveryByteAndRestoreBothCursors()
    {
        var fixture = new Fixture();
        var bytes = (byte[])fixture.Bytes.Clone();
        using var first = new ShortReadStream(new byte[] { 0 }.Concat(fixture.Bytes).ToArray());
        using var other = new ShortReadStream(bytes);
        first.Position = 1;
        var method = Api.GetMethod("ReadVerified", Flags)!;
        var index = method.Invoke(null, new object[] { first, other })!;
        Assert.AreEqual(3, Rows(index, "Objects").Length);
        Assert.AreEqual(1L, first.Position); Assert.AreEqual(0L, other.Position);
        bytes[48] ^= 1;
        var error = Assert.ThrowsExactly<TargetInvocationException>(() => method.Invoke(null, new object[] { first, other }));
        Assert.IsInstanceOfType<InvalidDataException>(error.InnerException);
        Assert.AreEqual(1L, first.Position); Assert.AreEqual(0L, other.Position);
    }
    [TestMethod]
    public void ShortReadsImmutabilityAndFailurePositionArePartOfTheStreamContract()
    {
        var fixture=new Fixture();using(var stream=new ShortReadStream(fixture.Bytes)) {
            var index=Read(stream);Assert.AreEqual(3,Rows(index,"Objects").Length);Assert.AreEqual(0L,stream.Position);
            var list=(IList)Get<object>(index,"Types");Assert.ThrowsExactly<NotSupportedException>(()=>list.Add(list[0]));
            var type=Rows(index,"Types")[0];Get<byte[]>(type,"OldTypeHash")[0]=0;Assert.AreEqual((byte)28,Get<byte[]>(type,"OldTypeHash")[0]);
        }
        using(var stream=new ShortReadStream(fixture.Bytes,false)) {var error=Assert.ThrowsExactly<TargetInvocationException>(()=>Read(stream));Assert.IsInstanceOfType<InvalidDataException>(error.InnerException);}
        fixture.Bytes[16]=2;using(var stream=new MemoryStream(new byte[3].Concat(fixture.Bytes).ToArray())) {
            stream.Position=3;var error=Assert.ThrowsExactly<TargetInvocationException>(()=>Read(stream));Assert.IsInstanceOfType<InvalidDataException>(error.InnerException);Assert.AreEqual(3L,stream.Position);Assert.IsTrue(stream.CanRead);
        }
    }
    // Independent expected digests from installed UnityPy1.25.0's SerializedFile/ObjectReader,
    // over EVERY type/object/script/external row in these four actual format22 files.
    [TestMethod]
    [DataRow("globalgamemanagers",18,18,"e28e408f9316fd4a3880a9cb3ba7b501adfdc8aa6089128770e7f7cc89a603d6")]
    [DataRow("resources.assets",315,26579,"093ef31a7a25ed081b6f79fd92c04b52446bfb2f9db8c155241edc3862126516")]
    [DataRow("sharedassets0.assets",24,194,"08b6efd42e5025bc49b6478d3b3d0c49efa77715e3e91fab0acfa9da74a39ca0")]
    [DataRow("level0",47,475,"1262ec02fd4c288c8f11c667c6de4a59c97f1174ad4c621067a54468699d4758")]
    public void InstalledSamplesMatchIndependentUnityPyMetadata(string name,int typeCount,int objectCount,string expected)
    {
        var path=Path.Combine(@"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight_Data",name);if(!File.Exists(path))Assert.Inconclusive("Local fixed-version sample is not installed.");
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);var index=Read(stream);var types=Rows(index,"Types");var objects=Rows(index,"Objects");Assert.AreEqual(typeCount,types.Length);Assert.AreEqual(objectCount,objects.Length);Assert.AreEqual("2020.2.2f1",Get<string>(index,"UnityVersion"));
        static string Hex(byte[] bytes)=>Convert.ToHexString(bytes).ToLowerInvariant();var lines=new List<string>();
        for(int i=0;i<types.Length;i++){var t=types[i];lines.Add($"T|{i}|{Get<int>(t,"ClassId")}|{(Get<bool>(t,"IsStripped") ? 1 : 0)}|{Get<short>(t,"ScriptTypeIndex")}|{Hex(Get<byte[]>(t,"OldTypeHash"))}|{Hex(Get<byte[]>(t,"ScriptId"))}");}
        foreach(var o in objects)lines.Add($"O|{Get<long>(o,"LocalId")}|{Get<long>(o,"Offset")}|{Get<int>(o,"Length")}|{Get<int>(o,"ClassId")}|{Get<int>(o,"TypeIndex")}");
        foreach(var s in Rows(index,"Scripts"))lines.Add($"S|{Get<int>(s,"FileIndex")}|{Get<long>(s,"LocalId")}");
        foreach(var e in Rows(index,"Externals"))lines.Add($"E|{Get<int>(e,"Type")}|{Hex(Get<byte[]>(e,"Guid"))}|{Get<string>(e,"EmptyName")}|{Get<string>(e,"Path")}");
        Assert.AreEqual(expected,Hex(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",lines)+"\n"))));Assert.AreEqual(0L,stream.Position);
    }
}
