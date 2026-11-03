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
        var body = Get<object>(row, "Texture"); var present = Get<bool[]>(body, "ResourcePresent"); var aliases = Get<int[]>(body, "ResourceAliases"); present[0] = true; aliases[0] = 0; Set(body, "MainResourceMapped", true);
        api.Reject(() => api.Call("RequireCompleteBodies", data)); // ID membership/presence is not a GPU image or sampler capture.
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
            Assert.IsNotNull(root); runtime = Assembly.LoadFrom(Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll")); type = runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentNativeAssetDirectory", true)!;
        }
        internal object Row(string name) => Activator.CreateInstance(type.GetNestedType(name, All)!, true)!;
        internal Array Array(string name, params object[] values) { var result = System.Array.CreateInstance(type.GetNestedType(name, All)!, values.Length); for (var i = 0; i < values.Length; i++) result.SetValue(values[i], i); return result; }
        internal object First(object data) => Get<Array>(data, "Rows").GetValue(0)!;
        internal object? Call(string name, params object[] values) => type.GetMethod(name, All)!.Invoke(null, values);
        internal object Data(int rva) {
            var profile = Call("TextureType", rva)!; var size = Get<int>(profile, "Size"); var kind = Get<string>(profile, "Kind"); var resources = Get<int[]>(profile, "Resources");
            var row = Row("Row"); Set(row, "TypeRva", rva); Set(row, "Vtable", Get<int>(profile, "Vtable")); Set(row, "Size", size); Set(row, "TypeKey", rva.ToString("x") + "/Core//" + kind); Set(row, "Kind", "texture/" + kind); Set(row, "Persistent", true);
            var source = Activator.CreateInstance(runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentSerializedAssetImage+Data", true)!, true)!;
            Set(source, "SourcePath", "actual/unit.texture"); Set(source, "LocalIdentifier", (long)rva); Set(source, "NativeTypeRva", rva); Set(source, "Hash", new string('a', 64)); Set(source, "TypeHash", new byte[16]); Set(source, "Length", 64);
            Set(row, "Source", source); Set(row, "Binding", Call("SourceKey", source)!);
            var body = Row("TextureBody"); var fields = new byte[size - 56];
            if (kind is "texture-2d" or "cubemap") Put(fields, 336 - 56, -1);
            Set(body, "Fields", fields); Set(body, "Padding", new byte[Get<int[]>(profile, "Padding").Length]); Set(body, "UnusedFlags", new byte[kind is "texture-2d" or "cubemap" ? 2 : 1]);
            Set(body, "ResourcePresent", new bool[resources.Length]); Set(body, "ResourceAliases", Enumerable.Repeat(-1, resources.Length).ToArray());
            Set(body, "SurfacePresent", new bool[kind == "render-texture" ? 3 : 0]); Set(body, "SurfaceAliases", Enumerable.Repeat(-1, kind == "render-texture" ? 3 : 0).ToArray());
            var path = Row("TextStorage"); Set(path, "Inline", true); Set(path, "Capacity", 15); Set(path, "Label", 72); Set(path, "Unused", new byte[19]); Set(body, "StreamPath", path);
            if (kind is "texture-3d" or "texture-2d-array" or "cubemap-array") { var raw = Get<object>(body, "Standalone"); Set(raw, "RawAllocation", true); Set(raw, "Owned", true); }
            Set(row, "Texture", body); var data = Row("Data"); Set(data, "Rows", Array("Row", row)); return data;
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
