using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class ResidentNativeTextureBodyTests
{
    private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    [DataTestMethod]
    [DataRow(0x194ce60)] [DataRow(0x194ceb0)] [DataRow(0x1960fb0)] [DataRow(0x1960470)]
    [DataRow(0x1961550)] [DataRow(0x1961750)] [DataRow(0x194c6f0)] [DataRow(0x19645a0)]
    public void ActualTypeProfilesCaptureKnownBodyButNeverInventDeviceImageProof(int rva)
    {
        var api = new Api(); var data = api.Data(rva); api.Validate(data);
        var row = api.First(data); var gaps = api.Gaps(data);
        Assert.IsTrue(gaps.Any(gap => gap.StartsWith("native-texture/device-resource-entry-image-and-sampler-unproven:", StringComparison.Ordinal)));
        Assert.IsFalse(gaps.Any(gap => gap.StartsWith("native-asset/missing-native-body:", StringComparison.Ordinal)));
        Set(data, "CoverageGaps", Array.Empty<string>()); api.Reject(() => api.Call("RequireCanonicalizableBodies", data));
        var body = Get<object>(row, "Texture"); var present = Get<bool[]>(body, "ResourcePresent"); var aliases = Get<int[]>(body, "ResourceAliases"); present[0] = true; aliases[0] = 0; Get<bool[]>(body,"ResourceMapped")[0]=true; Set(body, "MainResourceMapped", true);
        api.Reject(() => api.Call("RequireCompleteBodies", data)); // ID membership/presence is not a GPU image or sampler capture.
    }
    [TestMethod]
    public void StaticOriginRequiresExactSlotAliasesAndDoesNotGrantDeviceCoverage()
    {
        var api = new Api(); var data = api.Data(0x194ceb0); var row = api.First(data);
        var roots = data.GetType().Assembly.GetType("HollowKnightTAS.Runtime.FullRun.ResidentBuiltinTextureRoots",true)!;
        var names = (string[])roots.GetMethod("DeclaredOrigins",All)!.Invoke(null,null)!;
        var index = Array.IndexOf(names,"S/1a16298/0"); Assert.IsGreaterThanOrEqualTo(0,index);
        var key = (string)roots.GetMethod("Encode",All)!.Invoke(null,new object[] { new[] { names[index] } })!;
        var source = Activator.CreateInstance(Get<object>(row,"Source").GetType(),true)!;
        Set(source,"CoverageGaps",new[] { "serialized-image/no-source-identifier" });
        Set(row,"Source",source); Set(row,"Binding",key);
        var owners = Get<string[]>(data,"BuiltinSlotOwners"); owners[index] = key;
        api.Validate(data);
        Assert.IsFalse(api.Gaps(data).Any(g => g.Contains("no-source-identifier",StringComparison.Ordinal)));
        api.Reject(() => api.Call("RequireCompleteBodies",data));
        owners[0] = key; api.Reject(() => api.Validate(data)); // A second alias cannot be hidden in a single-slot key.
        owners[0] = ""; owners[index] = ""; api.Reject(() => api.Validate(data));
    }
    [TestMethod]
    public void ProcessPointersResourceIdsAndPaddingCannotHideInPortableFieldBits()
    {
        var api = new Api();
        foreach (var absolute in new[] { 80, 88, 144, 184, 200, 216, 248, 84, 115 }) {
            var data = api.Data(0x194ceb0); Get<byte[]>(Get<object>(api.First(data), "Texture"), "Fields")[absolute - 56] = 1;
            api.Reject(() => api.Validate(data));
        }
        var flags = api.Data(0x194ceb0); Get<byte[]>(Get<object>(api.First(flags), "Texture"), "Fields")[114 - 56] = 0x80; api.Reject(() => api.Validate(flags));
        var cube = api.Data(0x1960fb0); Get<byte[]>(Get<object>(api.First(cube), "Texture"), "Fields")[352 - 56] = 1; api.Reject(() => api.Validate(cube));
        var render = api.Data(0x194c6f0); Get<byte[]>(Get<object>(api.First(render), "Texture"), "Fields")[344 - 56] = 1; api.Reject(() => api.Validate(render));
    }
    [TestMethod]
    public void UnregisteredAdditionalSlotsRemainObservedAndCannotEraseDeviceQualificationGaps()
    {
        var api=new Api();var data=api.Data(0x194c6f0);var body=Get<object>(api.First(data),"Texture");
        Set(body,"ResourcePresent",new[]{true,true,true});Set(body,"ResourceAliases",new[]{0,1,1});Set(body,"ResourceMapped",new[]{true,false,false});Set(body,"MainResourceMapped",true);
        api.Validate(data);var gaps=api.Gaps(data);
        Assert.IsTrue(gaps.Any(x=>x.EndsWith("/332",StringComparison.Ordinal)));Assert.IsTrue(gaps.Any(x=>x.EndsWith("/336",StringComparison.Ordinal)));
        Set(data,"CoverageGaps",Array.Empty<string>());api.Reject(()=>api.Call("RequireCompleteBodies",data));api.Reject(()=>api.Call("RequireCanonicalizableBodies",data));
        Get<bool[]>(body,"ResourceMapped")[2]=true;api.Reject(()=>api.Validate(data)); // one ID cannot be simultaneously mapped and absent
        Set(body,"ResourceMapped",new[]{true,true,true});api.Validate(data);api.Reject(()=>api.Call("RequireCompleteBodies",data)); // registration never proves device image
        Set(body,"ResourcePresent",new[]{false,true,true});Set(body,"ResourceAliases",new[]{-1,0,0});Set(body,"ResourceMapped",new[]{false,true,true});Set(body,"MainResourceMapped",false);api.Validate(data);
        Set(body,"ResourceAliases",new[]{0,1,0});Set(body,"ResourcePresent",new[]{false,true,true});api.Reject(()=>api.Validate(data));
    }
    [TestMethod]
    public void ResourceRoleBindingsRetainMainAdditionalAndCompleteAliasPartitionsAcrossProcesses()
    {
        var api=new Api();const int rt=0x194c6f0;const int tex=0x194ceb0;
        foreach(var sample in new[]{(new[]{10,20,30},10,101),(new[]{10,20,30},20,202),(new[]{10,20,30},30,303),(new[]{10,20,20},20,202),(new[]{10,10,10},10,101)}) {
            var target=sample.Item1.Select(x=>x==10?101:x==20?202:303).ToArray();
            var key=(string)api.Ref("EncodeTextureBinding","owner/带/slash",rt,sample.Item1,sample.Item2)!;
            Assert.AreEqual("owner/带/slash",api.Ref("TextureBindingOwner",key));Assert.AreEqual(sample.Item3,api.Ref("ResolveTextureRole",key,rt,target));
        }
        var unscaled=(string)api.Ref("EncodeTextureBinding","owner",tex,new[]{1,2},2)!;Assert.AreEqual(9,api.Ref("ResolveTextureRole",unscaled,tex,new[]{8,9}));
        foreach(var pair in new[]{(new[]{0,2,2},new[]{0,9,9}),(new[]{0,0,2},new[]{0,0,9}),(new[]{1,0,2},new[]{8,0,9})}) {
            var key=(string)api.Ref("EncodeTextureBinding","owner",rt,pair.Item1,2)!;Assert.AreEqual(9,api.Ref("ResolveTextureRole",key,rt,pair.Item2));
        }
        var alias=(string)api.Ref("EncodeTextureBinding","owner",rt,new[]{1,2,2},2)!;
        api.RejectRole(()=>api.Ref("ResolveTextureRole",alias,rt,new[]{8,9,10}));api.RejectRole(()=>api.Ref("ResolveTextureRole",alias,tex,new[]{8,9}));
        var absent=(string)api.Ref("EncodeTextureBinding","owner",rt,new[]{1,2,0},2)!;api.RejectRole(()=>api.Ref("ResolveTextureRole",absent,rt,new[]{8,9,10}));
        Assert.AreEqual(7,api.Ref("ResolveTextureRole","legacy-owner",rt,new[]{7,0,0}));api.RejectRole(()=>api.Ref("ResolveTextureRole","legacy-owner",rt,new[]{7,8,0}));
        api.RejectRole(()=>api.Ref("EncodeTextureBinding","@unbound-native-asset-origin",rt,new[]{1,2,3},2));
        api.RejectRole(()=>api.Ref("EncodeTextureBinding","owner",rt,new[]{1,2,3},4));
        api.RejectRole(()=>api.Ref("TextureBindingOwner",alias.Replace("/6/","/2/"))); // partial alias mask cannot be decoded
        api.RejectRole(()=>api.Ref("TextureBindingOwner",alias.Replace("/0.1.1/","/0.2.2/")));
        api.RejectRole(()=>api.Ref("TextureBindingOwner","resident-texture-resource/2/owner"));
    }
    [TestMethod]
    public void NativeMapLookupDistinguishesProvenMissFromWrongOwnerAndDamagedTraversal()
    {
        var api=new Api();var head=new IntPtr(0x1000);var node=new IntPtr(0x2000);var owner=new IntPtr(0x9000);var header=new byte[16];Put64(header,0,(ulong)head.ToInt64());Put64(header,8,1);
        var sentinel=new byte[48];Put64(sentinel,0,(ulong)node.ToInt64());Put64(sentinel,8,(ulong)node.ToInt64());Put64(sentinel,16,(ulong)node.ToInt64());sentinel[24]=sentinel[25]=1;
        var row=new byte[48];Put64(row,0,(ulong)head.ToInt64());Put64(row,8,(ulong)head.ToInt64());Put64(row,16,(ulong)head.ToInt64());row[24]=1;Put(row,32,20);Put64(row,40,(ulong)owner.ToInt64());
        var memory=new System.Collections.Generic.Dictionary<IntPtr,byte[]>{{head,sentinel},{node,row}};var calls=0;
        Func<IntPtr,int,byte[]> read=(p,n)=>(byte[])memory[p].Clone();Action<IntPtr,int> validate=(p,id)=>{calls++;if(p!=owner)throw new InvalidDataException("wrong native owner");Assert.AreEqual(6,api.Call("TextureResourceMask",0x194c6f0,new[]{10,20,20},id));};
        object? Find(int id)=>api.Index("FindTextureResourceInTree",id,header,read,validate);
        Assert.AreEqual(owner,Find(20));Assert.AreEqual(1,calls);Assert.AreEqual(IntPtr.Zero,Find(30));Assert.AreEqual(1,calls);
        var high=unchecked((int)0x80000000u);Put(row,32,high);Assert.AreEqual(owner,api.Index("FindTextureResourceInTree",high,header,read,(Action<IntPtr,int>)((p,id)=>Assert.AreEqual(high,id))));Put(row,32,20);
        api.Reject(()=>Find(0));
        Put64(row,40,0x9999);api.Reject(()=>Find(20));Put64(row,40,(ulong)owner.ToInt64());
        Put64(row,8,0);api.Reject(()=>Find(20));Put64(row,8,(ulong)head.ToInt64());
        Put64(row,0,(ulong)node.ToInt64());api.Reject(()=>Find(10));Put64(row,0,(ulong)head.ToInt64());
        Put64(header,8,0);api.Reject(()=>Find(20));Put64(header,8,1);
        sentinel[25]=0;api.Reject(()=>Find(30));sentinel[25]=1;
        var child=new IntPtr(0x3000);var lower=(byte[])row.Clone();Put(lower,32,25);Put64(lower,8,(ulong)node.ToInt64());memory[child]=lower;Put64(row,0,(ulong)child.ToInt64());Put64(header,8,2);api.Reject(()=>Find(10));
    }
    [TestMethod]
    public void FixedPlayerResourceGetterAbiAgreesWithTheActualProfileSlots()
    {
        using var stream=File.OpenRead(@"D:\SteamLibrary\steamapps\common\Hollow Knight\UnityPlayer.dll");
        Assert.AreEqual("D97E92A7640B10580B4E60139EACF01828F74BAAEF53F75E08B9FDD6193FBE5E",Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)));stream.Position=0;
        using var pe=new System.Reflection.PortableExecutable.PEReader(stream);
        foreach(var sample in new[]{(0x69bfe0,364,"7F3D7ABDB710FC7DCB46DA44F0F6F29D061080C41CC409F8A8CF131BDCD62587"),(0x67b610,31,"245F2BE19EF72C4C8352DF518D1DA94393811A844F12A2BB1572BFF5FA0D2A6B"),(0x657120,161,"BDB282481DEB68F8A60026E6F988533424FCEF3BB8A4B1985A59FF5BC59F6148")})
            Assert.AreEqual(sample.Item3,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pe.GetSectionData(sample.Item1).GetContent(0,sample.Item2).ToArray())));
        var api=new Api();CollectionAssert.AreEqual(new[]{80,332},(int[])api.Call("TextureResourceOffsets",0x194ceb0)!);CollectionAssert.AreEqual(new[]{80,332,336},(int[])api.Call("TextureResourceOffsets",0x194c6f0)!);
        var getter=BitConverter.ToUInt64(pe.GetSectionData(0x1685618+0xb0).GetContent(0,8).ToArray());Assert.AreEqual((ulong)pe.PEHeaders.PEHeader!.ImageBase+0x67b610,getter);
    }
    [TestMethod]
    public void NativeBooleanDimensionAndRawCpuLengthAreValidatedBeforeAnyWrite()
    {
        var api = new Api(); var data = api.Data(0x194ceb0); Get<byte[]>(Get<object>(api.First(data), "Texture"), "Fields")[321 - 56] = 2; api.Reject(() => api.Validate(data));
        data = api.Data(0x194ceb0); Put(Get<byte[]>(Get<object>(api.First(data), "Texture"), "Fields"), 284 - 56, -1); api.Reject(() => api.Validate(data));
        data = api.Data(0x1960470); var body = Get<object>(api.First(data), "Texture"); var raw = Get<object>(body, "Standalone");
        Set(raw, "Present", true); Set(raw, "Count", 2); Set(raw, "Capacity", 2); Set(raw, "Bytes", new byte[2]); api.Reject(() => api.Validate(data));
        Put64(Get<byte[]>(body, "Fields"), 304 - 56, 2); api.Validate(data);
        Set(raw, "Capacity", 258); Set(raw, "Bytes", new byte[258]); api.Reject(() => api.Validate(data)); // Format guard is one pinned descriptor byte, not arbitrary growth.
    }
    [TestMethod]
    public void CpuImageAndUpdateBitsAreDWhileStorageAndAliasesRemainReadonlyDependencies()
    {
        var api = new Api(); var source = api.Data(0x194ceb0); var sourceBody = Get<object>(api.First(source), "Texture");
        Set(sourceBody, "Shared", api.Image(Get<string>(api.First(source), "Binding")));
        var target = api.Copy(source); var targetBody = Get<object>(api.First(target), "Texture");
        Get<byte[]>(Get<object>(Get<object>(targetBody, "Shared"), "Data"), "Bytes")[0] = 73;
        Put(Get<byte[]>(targetBody, "Fields"), 240 - 56, 91);
        api.Call("RequireTextureReadonlyDependencies", api.First(source), api.First(target));
        Set(Get<object>(Get<object>(targetBody, "Shared"), "Data"), "Label", 75);
        api.Reject(() => api.Call("RequireTextureReadonlyDependencies", api.First(source), api.First(target)));
    }
    [TestMethod]
    public void SharedCpuOwnersBorrowedStorageAndCubemapPPtrVectorsRetainExplicitGaps()
    {
        var api = new Api(); var data = api.Data(0x194ceb0); var body = Get<object>(api.First(data), "Texture"); var image = api.Image("actual/a");
        Set(image, "Owners", new[] { "actual/a", "actual/b" }); Put(Get<byte[]>(image, "Fields"), 4, 2); Set(body, "Shared", image);
        Assert.IsTrue(api.Gaps(data).Any(gap => gap.StartsWith("native-texture/shared-cpu-image-alias-transfer-unproven:", StringComparison.Ordinal)));
        Set(Get<object>(image, "Data"), "Owned", false); Assert.IsTrue(api.Gaps(data).Any(gap => gap.StartsWith("native-texture/borrowed-cpu-byte-storage:", StringComparison.Ordinal)));
        var cube = api.Data(0x1960fb0); body = Get<object>(api.First(cube), "Texture"); var face = api.Row("TextureReference");
        Set(body, "SourceFaces", api.Array("TextureReference", face)); Set(body, "SourceFaceCapacity", 2); Set(body, "SourceFaceStoragePresent", true); Set(body, "SourceFaceUnused", new byte[4]);
        Assert.IsTrue(api.Gaps(cube).Any(gap => gap.StartsWith("native-texture/cubemap-source-vector-capacity-canon-unproven:", StringComparison.Ordinal)));
        Set(face, "Null", false); Set(face, "Loaded", true); Set(face, "NativeTypeRva", 0x1960470); Set(face, "Binding", "actual/3d"); api.Reject(() => api.Validate(cube));
    }
    [TestMethod]
    public void TextureDeepCopyKeepsCpuBytesAndReadonlyMetadataIndependent()
    {
        var api = new Api(); var source = api.Data(0x194ceb0); Set(Get<object>(api.First(source), "Texture"), "Shared", api.Image("actual/a"));
        var copy = api.Copy(source); var body = Get<object>(api.First(copy), "Texture");
        Get<byte[]>(Get<object>(Get<object>(body, "Shared"), "Data"), "Bytes")[0] = 9;
        Get<byte[]>(Get<object>(body, "StreamPath"), "Unused")[0] = 8;
        var original = Get<object>(api.First(source), "Texture");
        Assert.AreEqual(0, Get<byte[]>(Get<object>(Get<object>(original, "Shared"), "Data"), "Bytes")[0]);
        Assert.AreEqual(0, Get<byte[]>(Get<object>(original, "StreamPath"), "Unused")[0]); api.Validate(source);
    }
    private static T Get<T>(object value, string field) => (T)value.GetType().GetField(field, All)!.GetValue(value)!;
    private static void Set(object value, string field, object data) => value.GetType().GetField(field, All)!.SetValue(value, data);
    private static void Put(byte[] bytes, int at, int value) => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, at, 4);
    private static void Put64(byte[] bytes, int at, ulong value) => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, at, 8);
    private sealed class Api
    {
        private readonly Type type; private readonly Assembly runtime;
        internal Api() {
            var root = new DirectoryInfo(AppContext.BaseDirectory); while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
            Assert.IsNotNull(root); runtime = Assembly.LoadFrom(Environment.GetEnvironmentVariable("HKTAS_TEST_RUNTIME_ASSEMBLY") ?? Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll")); type = runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentNativeAssetDirectory", true)!;
        }
        internal object Row(string name) => Activator.CreateInstance(type.GetNestedType(name, All)!, true)!;
        internal Array Array(string name, params object[] values) { var result = System.Array.CreateInstance(type.GetNestedType(name, All)!, values.Length); for (var i = 0; i < values.Length; i++) result.SetValue(values[i], i); return result; }
        internal object First(object data) => Get<Array>(data, "Rows").GetValue(0)!;
        internal object? Call(string name, params object[] values) => type.GetMethod(name, All)!.Invoke(null, values);
        internal object? Ref(string name, params object[] values) => runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentNativeAssetReference",true)!.GetMethod(name,All)!.Invoke(null,values);
        internal object? Index(string name, params object[] values) => runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentLoadedAssetIndex",true)!.GetMethod(name,All)!.Invoke(null,values);
        internal void RejectRole(Action action) { var error=Assert.ThrowsExactly<TargetInvocationException>(action).GetBaseException();Assert.IsTrue(error is InvalidOperationException or InvalidDataException,error.ToString()); }
        internal object Data(int rva) {
            var profile = Call("TextureType", rva)!; var size = Get<int>(profile, "Size"); var kind = Get<string>(profile, "Kind"); var resources = Get<int[]>(profile, "Resources");
            var row = Row("Row"); Set(row, "TypeRva", rva); Set(row, "Vtable", Get<int>(profile, "Vtable")); Set(row, "Size", size); Set(row, "TypeKey", rva.ToString("x") + "/Core//" + kind); Set(row, "Kind", "texture/" + kind); Set(row, "Persistent", true);
            var source = Activator.CreateInstance(runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentSerializedAssetImage+Data", true)!, true)!;
            Set(source, "SourcePath", "actual/unit.texture"); Set(source, "LocalIdentifier", (long)rva); Set(source, "NativeTypeRva", rva); Set(source, "Hash", new string('a', 64)); Set(source, "TypeHash", new byte[16]); Set(source, "Length", 64);
            Set(row, "Source", source); Set(row, "Binding", Call("SourceKey", source)!);
            var body = Row("TextureBody"); var fields = new byte[size - 56];
            if (kind is "texture-2d" or "cubemap") Put(fields, 336 - 56, -1);
            Set(body, "Fields", fields); Set(body, "Padding", new byte[Get<int[]>(profile, "Padding").Length]); Set(body, "UnusedFlags", new byte[kind is "texture-2d" or "cubemap" ? 2 : 1]);
            Set(body, "ResourcePresent", new bool[resources.Length]); Set(body,"ResourceMapped",new bool[resources.Length]); Set(body, "ResourceAliases", Enumerable.Repeat(-1, resources.Length).ToArray());
            Set(body, "SurfacePresent", new bool[kind == "render-texture" ? 3 : 0]); Set(body, "SurfaceAliases", Enumerable.Repeat(-1, kind == "render-texture" ? 3 : 0).ToArray());
            var path = Row("TextStorage"); Set(path, "Inline", true); Set(path, "Capacity", 15); Set(path, "Label", 72); Set(path, "Unused", new byte[19]); Set(body, "StreamPath", path);
            if (kind is "texture-3d" or "texture-2d-array" or "cubemap-array") { var raw = Get<object>(body, "Standalone"); Set(raw, "RawAllocation", true); Set(raw, "Owned", true); }
            Set(row, "Texture", body); var data = Row("Data"); Set(data, "Rows", Array("Row", row));
            var slots = (string[])runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentBuiltinTextureRoots",true)!.GetMethod("DeclaredOrigins", All)!.Invoke(null,null)!;
            Set(data,"BuiltinSlotOwners",slots.Select(_ => "").ToArray()); return data;
        }
        internal object Image(string owner) {
            var image = Row("TextureImage"); var fields = Get<byte[]>(image, "Fields"); Put(fields, 0, 74); Put(fields, 4, 1); Put(fields, 40, 1); Put(fields, 44, 1); Put(fields, 64, 1); Put(fields, 68, 1);
            Set(image, "Owners", new[] { owner }); Set(image, "OwnersComplete", true); var buffer = Get<object>(image, "Data");
            Set(buffer, "Count", 2); Set(buffer, "Capacity", 2); Set(buffer, "Present", true); Set(buffer, "Owned", true); Set(buffer, "Label", 74); Set(buffer, "Bytes", new byte[2]); return image;
        }
        internal void Validate(object data) => Call("Validate", data);
        internal string[] Gaps(object data) => (string[])Call("RequiredGaps", data)!;
        internal object Copy(object data) => data.GetType().GetMethod("Copy", All)!.Invoke(data, null)!;
        internal void Reject(Action action) { var error = Assert.ThrowsExactly<TargetInvocationException>(action); Assert.IsInstanceOfType<InvalidDataException>(error.GetBaseException()); }
    }
}
