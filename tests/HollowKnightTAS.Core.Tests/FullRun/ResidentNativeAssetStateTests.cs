using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UnityEngine;
using UnityEngine.Rendering;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentNativeAssetStateTests
{
    [TestMethod]
    public void MeshManifestPreservesRawStreamsUnusedIndicesAndIndependentCopies()
    {
        var api = new Api(); var state = api.Mesh(); api.Validate(state);
        var expected = api.Encode(state); var copy = api.Copy(state);
        CollectionAssert.AreEqual(expected, api.Encode(copy));
        ((byte[][])Get(state, "VertexBuffers"))[1][0] ^= 0x5a;
        var index = (byte[])Get(state, "IndexBuffer"); index[0] = 0x77;
        CollectionAssert.AreEqual(expected, api.Encode(copy));
        Assert.IsFalse(expected.SequenceEqual(api.Encode(state)), "Unused index bytes and non-Float32 vertex streams must remain in the payload.");
        Assert.AreEqual(2, ((SubMeshDescriptor[])Get(copy, "SubMeshes")).Length);
        Assert.AreEqual(-1, ((SubMeshDescriptor[])Get(copy, "SubMeshes"))[1].baseVertex);
    }

    [TestMethod]
    public void MeshManifestRejectsInvalidRangesStrideSkinningAndBlendFrameOrder()
    {
        var api = new Api();
        void Reject(Action<object> change) { var value = api.Mesh(); change(value); api.Reject(() => api.Validate(value)); }
        Reject(value => ((byte[][])Get(value, "VertexBuffers"))[1] = new byte[12]);
        Reject(value => { var rows = (SubMeshDescriptor[])Get(value, "SubMeshes"); rows[0].indexCount = 7; });
        Reject(value => { var rows = (SubMeshDescriptor[])Get(value, "SubMeshes"); rows[1].baseVertex = -2; });
        Reject(value => { var rows = (SubMeshDescriptor[])Get(value, "SubMeshes"); rows[0].topology = MeshTopology.Triangles; });
        Reject(value => Set(value, "BonesPerVertex", new byte[] { 1, 1, 1, 0 }));
        Reject(value => ((uint[])Get(value, "PackedVariableWeights"))[0] = 4);
        Reject(value => ((uint[])Get(value, "PackedVariableWeights"))[2] = 5);
        Reject(value => { var weights = (BoneWeight1[])Get(value, "BoneWeights"); weights[0].boneIndex = 1; });
        Reject(value => { var shapes = (Array)Get(value, "BlendShapes"); var frames = (Array)Get(shapes.GetValue(0)!, "Frames"); Set(frames.GetValue(1)!, "Weight", 10f); });
    }

    [TestMethod]
    public void MeshManifestAcceptsUInt32IndicesAndEncodesFloatBitsWithoutVectorEquality()
    {
        var api = new Api(); var state = api.Mesh();
        Set(state, "IndexFormat", IndexFormat.UInt32);
        Set(state, "IndexBuffer", new uint[] { uint.MaxValue, 0, 1, 2, 3, 0 }.SelectMany(BitConverter.GetBytes).ToArray());
        api.Validate(state); var first = api.Encode(state);
        var bounds = (Bounds)Get(state, "Bounds"); var center = bounds.center;
        center.x = BitConverter.ToSingle(new byte[] { 0, 0, 0, 128 }, 0); bounds.center = center; Set(state, "Bounds", bounds);
        Assert.IsFalse(first.SequenceEqual(api.Encode(state)), "Signed zero must be compared by captured bits, not Unity's approximate vector equality.");
    }

    [TestMethod]
    public void NativeAssetCoverageCannotBeGrantedByErasingCallerSuppliedGapArray()
    {
        var api = new Api();
        foreach (var kind in new[] { "Mesh", "Material", "Texture2D", "Unsupported", "invented adapter" }) {
            var source = api.Row("Snapshot"); Set(source, "Kind", kind); Set(source, "Binding", "test/asset");
            Set(source, "CoverageGaps", Array.Empty<string>());
            api.Reject(() => source.GetType().GetMethod("RequireCompleteCoverage", All)!.Invoke(source, null));
        }
    }

    [TestMethod]
    public void PackedSkinningDecodesNativeOffsetTableWithoutCreatingSourceCaches()
    {
        var api = new Api(); var words = new uint[] { 4, 4, 6, 7, 0xffff0003, 0x80000005, 0x00000007 }; var original = (uint[])words.Clone();
        var decoded = api.Decode(3, words);
        CollectionAssert.AreEqual(new byte[] { 0, 2, 1 }, decoded.Counts);
        CollectionAssert.AreEqual(new[] { 3, 5, 7 }, decoded.Weights.Select(value => value.boneIndex).ToArray());
        CollectionAssert.AreEqual(new[] { 0x3f800000, 0x3f000080, 0 }, decoded.Weights.Select(value => BitConverter.ToInt32(BitConverter.GetBytes(value.weight), 0)).ToArray());
        CollectionAssert.AreEqual(original, words);
        api.Reject(() => api.Decode(3, new uint[] { 4, 4, 8, 7, 0xffff0003, 0x80000005, 7 }));
        api.Reject(() => api.Decode(3, new uint[] { 4, 4, 6, 6, 0xffff0003, 0x80000005, 7 }));
    }

    [TestMethod]
    public void SavedMaterialInputsPreserveEveryValueAndNativeRedBlackTreeInvariants()
    {
        var api = new Api();
        foreach (var count in Enumerable.Range(0, 129).Concat(new[] { 255, 256, 257, 4095, 4096 })) {
            var kinds = Enumerable.Range(0, count * 3).Select(index => index / count).ToArray();
            var keys = Enumerable.Range(0, count * 3).Select(index => checked((index % count) * -7 + 19)).ToArray();
            var values = kinds.Select((kind, index) => Enumerable.Range(0, kind == 0 ? 4 : kind == 1 ? 16 : 20).Select(offset => (byte)(index + offset)).ToArray()).ToArray();
            using (var saved = api.Saved(kinds, keys, values)) {
                var pointer = api.SavedPointer(saved);
                foreach (var map in Enumerable.Range(0, 3)) {
                    var sentinel = Marshal.ReadIntPtr(pointer, map * 16); var root = Marshal.ReadIntPtr(sentinel, 8);
                    Assert.AreEqual(count, Marshal.ReadInt64(pointer, map * 16 + 8));
                    Assert.AreEqual(1, Marshal.ReadByte(sentinel, 24)); Assert.AreEqual(1, Marshal.ReadByte(sentinel, 25));
                    if (count == 0) {
                        Assert.AreEqual(sentinel, root); Assert.AreEqual(sentinel, Marshal.ReadIntPtr(sentinel)); Assert.AreEqual(sentinel, Marshal.ReadIntPtr(sentinel, 16)); continue;
                    }
                    var kind = new[] { 2, 0, 1 }[map]; var observed = new List<int>(); var visited = new HashSet<IntPtr>();
                    int Visit(IntPtr node, IntPtr parent, bool parentRed, long min, long max) {
                        if (node == sentinel) return 1;
                        Assert.IsTrue(visited.Add(node), "Native input must be a tree, not an alias or cycle.");
                        Assert.AreEqual(parent, Marshal.ReadIntPtr(node, 8)); Assert.AreEqual(0, Marshal.ReadByte(node, 25));
                        var key = Marshal.ReadInt32(node, 28); Assert.IsTrue(key > min && key < max);
                        var red = Marshal.ReadByte(node, 24) == 0; Assert.IsFalse(red && parentRed);
                        var left = Visit(Marshal.ReadIntPtr(node), node, red, min, key); observed.Add(key);
                        var right = Visit(Marshal.ReadIntPtr(node, 16), node, red, key, max); Assert.AreEqual(left, right, "Every native nil path needs the same black height.");
                        var expectedIndex = kind * count + (19 - key) / 7; var bytes = new byte[values[expectedIndex].Length]; Marshal.Copy(IntPtr.Add(node, 32), bytes, 0, bytes.Length);
                        CollectionAssert.AreEqual(values[expectedIndex], bytes);
                        return left + (red ? 0 : 1);
                    }
                    Assert.AreEqual(1, Marshal.ReadByte(root, 24)); Visit(root, sentinel, false, long.MinValue, long.MaxValue);
                    Assert.AreEqual(count, visited.Count); CollectionAssert.AreEqual(keys.Skip(kind * count).Take(count).OrderBy(value => value).ToArray(), observed.ToArray());
                    Assert.AreEqual(observed[0], Marshal.ReadInt32(Marshal.ReadIntPtr(sentinel), 28));
                    Assert.AreEqual(observed[observed.Count - 1], Marshal.ReadInt32(Marshal.ReadIntPtr(sentinel, 16), 28));
                }
            }
        }
    }

    [TestMethod]
    public void SavedMaterialInputsRejectDuplicateNativeKeysAndWrongPayloadWidths()
    {
        var api = new Api();
        api.Reject(() => api.Saved(new[] { 0, 0 }, new[] { 7, 7 }, new[] { new byte[4], new byte[4] }));
        api.Reject(() => api.Saved(new[] { 2 }, new[] { 7 }, new[] { new byte[16] }));
        api.Reject(() => api.Saved(new[] { 3 }, new[] { 7 }, new[] { new byte[20] }));
    }

    [TestMethod]
    public void TextureNativeGuardPixelIsPreservedAndPartialPaddingIsRejected()
    {
        var api = new Api(); var state = api.Row("TextureState");
        foreach (var name in new[] { "Width", "Height", "CpuWidth", "CpuHeight" }) Set(state, name, 4);
        foreach (var name in new[] { "MipmapCount", "CpuMipCount", "CpuImageCount" }) Set(state, name, 1);
        Set(state, "CpuFormat", 4); Set(state, "Readable", true); Set(state, "CpuDataPresent", true);
        Set(state, "CpuImageSize", 64UL); Set(state, "CpuCapacity", 68UL);
        Set(state, "CpuBytes", Enumerable.Range(0, 68).Select(value => (byte)value).ToArray());
        var saved = api.EncodeTexture(state);
        ((byte[])Get(state, "CpuBytes"))[67] ^= 0x40;
        Assert.IsFalse(saved.SequenceEqual(api.EncodeTexture(state)), "Native guard bytes are retained as captured data.");
        Set(state, "CpuBytes", new byte[67]); api.Reject(() => api.EncodeTexture(state));
        Set(state, "CpuBytes", new byte[69]); Set(state, "CpuCapacity", 69UL); api.Reject(() => api.EncodeTexture(state));
        Set(state, "CpuBytes", new byte[64]); api.EncodeTexture(state);
    }

    [TestMethod]
    public void TextureImageKeepsRawMipPayloadAndRejectsIncompleteNativeImageDirectory()
    {
        var api = new Api();
        object Texture() {
            var state = api.Row("TextureState");
            foreach (var name in new[] { "Width", "Height", "CpuWidth", "CpuHeight" }) Set(state, name, 2);
            Set(state, "MipmapCount", 2); Set(state, "CpuMipCount", 2); Set(state, "CpuImageCount", 1);
            Set(state, "Readable", true); Set(state, "CpuDataPresent", true); Set(state, "CpuImageSize", 20UL); Set(state, "CpuCapacity", 32UL);
            Set(state, "CpuBytes", Enumerable.Range(0, 20).Select(value => (byte)value).ToArray()); return state;
        }
        var source = Texture(); var expected = api.EncodeTexture(source); var copy = api.Copy(source);
        ((byte[])Get(source, "CpuBytes"))[19] ^= 0x40;
        CollectionAssert.AreEqual(expected, api.EncodeTexture(copy)); Assert.IsFalse(expected.SequenceEqual(api.EncodeTexture(source)));
        Set(copy, "MipMapBias", BitConverter.ToSingle(new byte[] { 0, 0, 0, 128 }, 0));
        Assert.IsFalse(expected.SequenceEqual(api.EncodeTexture(copy)), "Sampler signed zero must retain its exact bits.");
        foreach (var mutation in new Action<object>[] {
            value => Set(value, "CpuImageSize", 19UL), value => Set(value, "CpuImageCount", 2),
            value => Set(value, "CpuMipCount", 0), value => Set(value, "CpuCapacity", 19UL), value => Set(value, "CpuDataPresent", false) }) {
            var invalid = Texture(); mutation(invalid); api.Reject(() => api.EncodeTexture(invalid));
        }
    }

    [TestMethod]
    public void PhysicsMaterialPreservesExactFloatBytesAndOwnerIdentityIndependently()
    {
        var api = new Api(); var source = api.Row("PhysicsMaterialState");
        Set(source, "Values", new byte[] { 0, 0, 0, 128, 0x91, 0x22, 0xc0, 0x7f });
        Set(source, "OwnerBinding", "resident-owner/source-instance");
        var original = api.EncodePhysicsMaterial(source); var copy = api.Copy(source);
        ((byte[])Get(source, "Values"))[3] = 0;
        CollectionAssert.AreEqual(original, api.EncodePhysicsMaterial(copy));
        Assert.IsFalse(original.SequenceEqual(api.EncodePhysicsMaterial(source)), "Native signed zero and NaN payloads are raw state, not approximate float values.");
        Set(copy, "OwnerBinding", "resident-owner/another-instance");
        Assert.IsFalse(original.SequenceEqual(api.EncodePhysicsMaterial(copy)), "An owner PPtr is not interchangeable with a matching display name.");
        Set(copy, "Values", new byte[7]); api.Reject(() => api.EncodePhysicsMaterial(copy));
    }

    [TestMethod]
    public void RawNativeAssetStringPreservesBinaryBytesAndChecksStorageBounds()
    {
        var api = new Api(); var header = Marshal.AllocHGlobal(40); var external = Marshal.AllocHGlobal(33);
        try {
            foreach (var count in new[] { 0, 1, 15, 16, 32 }) {
                Marshal.Copy(new byte[40], 0, header, 40); Marshal.Copy(new byte[33], 0, external, 33);
                var expected = Enumerable.Range(0, count).Select(value => value % 3 == 0 ? (byte)0 : (byte)(0xff - value)).ToArray();
                var storage = count < 16 ? IntPtr.Add(header, 8) : external;
                if (count >= 16) { Marshal.WriteIntPtr(header, external); Marshal.WriteInt64(header, 8, 32); }
                Marshal.WriteInt64(header, 24, count); Marshal.Copy(expected, 0, storage, count);
                CollectionAssert.AreEqual(expected, api.ReadRawString(header));
                Marshal.WriteByte(storage, count, 1); api.Reject(() => api.ReadRawString(header)); Marshal.WriteByte(storage, count, 0);
            }
            Marshal.Copy(new byte[40], 0, header, 40); Marshal.WriteInt64(header, 24, 16);
            api.Reject(() => api.ReadRawString(header));
            Marshal.WriteIntPtr(header, external); Marshal.WriteInt64(header, 8, 15);
            api.Reject(() => api.ReadRawString(header));
        } finally { Marshal.FreeHGlobal(external); Marshal.FreeHGlobal(header); }
    }

    [TestMethod]
    public void MonoScriptKeepsBinarySourcePropertiesHashAndSharedCacheWitness()
    {
        var api = new Api(); var source = api.Row("MonoScriptState"); var cache = api.ScriptCache();
        Set(cache, "ReferenceCount", 3); Set(cache, "ScriptType", -7); Set(cache, "IdentifierMatches", true);
        Set(source, "Script", new byte[] { 0xff, 0, 0xc0, 0x20, 0 }); Set(source, "PropertiesHash", Enumerable.Range(0, 16).Select(value => (byte)value).ToArray());
        Set(source, "ClassName", "ArbitraryType"); Set(source, "Namespace", "Example.Module"); Set(source, "AssemblyName", "Example.dll"); Set(source, "Cache", cache);
        var copy = api.Copy(source); var expected = api.EncodeMonoScript(source); api.MonoScriptEqual(source, copy);
        ((byte[])Get(source, "Script"))[1] = 1; ((byte[])Get(source, "PropertiesHash"))[15] ^= 0x40;
        CollectionAssert.AreEqual(expected, api.EncodeMonoScript(copy)); Assert.IsFalse(expected.SequenceEqual(api.EncodeMonoScript(source)));
        var another = api.Copy(copy); Set(Get(another, "Cache"), "ReferenceCount", 4);
        api.Reject(() => api.MonoScriptEqual(copy, another));
        Set(Get(copy, "Cache"), "SerializationPresent", true);
        var snapshot = api.Row("Snapshot"); Set(snapshot, "Kind", "MonoScript"); Set(snapshot, "Script", copy); Set(snapshot, "CoverageGaps", Array.Empty<string>());
        api.Reject(() => snapshot.GetType().GetMethod("RequireCompleteCoverage", All)!.Invoke(snapshot, null));
    }

    [TestMethod]
    public void MaterialCanonicalizableGateAllowsOnlyTypedRepairableCacheState()
    {
        var api = new Api(); var material = api.Row("MaterialState"); var cache = api.Row("MaterialCacheState");
        Set(material, "HasRuntimeSheet", true); Set(material, "ShaderBinding", "source/shader"); Set(material, "RuntimeShaderBinding", "source/shader");
        Set(material, "RuntimeProperties", api.PropertySheet()); Set(material, "Caches", cache);
        Set(cache, "SharedReferences", 2); Set(cache, "PropertySheetReferences", 1); Set(cache, "BuildStacksEmpty", true); Set(cache, "RuntimeStacksEmpty", true);
        var row = api.Row("Snapshot"); Set(row, "Kind", "Material"); Set(row, "Material", material); Set(row, "Binding", "source/material");
        Set(row, "CoverageGaps", Array.Empty<string>());
        api.Gate(row, "RequireCanonicalizableCoverage");
        Set(row, "CoverageGaps", Array.Empty<string>()); api.Reject(() => api.Gate(row, "RequireCompleteCoverage"));
        Set(cache, "BuildStacksEmpty", false); api.Reject(() => api.Gate(row, "RequireCanonicalizableCoverage")); Set(cache, "BuildStacksEmpty", true);
        Set(material, "RuntimeBufferOverride", true); api.Reject(() => api.Gate(row, "RequireCanonicalizableCoverage")); Set(material, "RuntimeBufferOverride", false);
        foreach (var name in new[] { "MainTextureUncached", "MainColorUncached", "HashesEmpty", "TextureUnionEmpty", "BufferAbsent", "DisplayListsEmpty", "SmallIndexMatchesOwner" }) Set(cache, name, true);
        foreach (var name in new[] { "TextureDirty", "ConstantBufferDirty", "RuntimeTextureDirty", "RuntimeConstantBufferDirty", "RuntimeStacksDirty" }) Set(material, name, true);
        Set(cache, "SharedReferences", 1); Set(material, "RuntimeFlags", 4U); api.Gate(row, "RequireCompleteCoverage");
        Set(cache, "SmallIndexMatchesOwner", false); api.Reject(() => api.Gate(row, "RequireCompleteCoverage"));
    }

    [TestMethod]
    public void MeshNativeMetadataSeparatesRetainedValuesFromColdCachesAndHardResourceGaps()
    {
        var api = new Api(); var mesh = api.Mesh(); var native = api.Row("MeshNativeState"); Set(mesh, "Native", native);
        foreach (var name in new[] { "PhysicsResourcesEmpty", "AsyncEmpty", "ComputeBindingsEmpty", "AssetUsersEmpty" }) Set(native, name, true);
        Set(native, "RetainedFlags", (byte)0x14); Set(native, "OwnerBinding", "owner/arbitrary-asset"); Set(native, "StreamPath", "captured/source/path");
        Set(native, "StreamOffset", 0x1020304050607080UL); Set(native, "BonePathHashes", new[] { 0U, uint.MaxValue, 17U });
        var row = api.Row("Snapshot"); Set(row, "Kind", "Mesh"); Set(row, "Mesh", mesh); Set(row, "CoverageGaps", Array.Empty<string>());
        api.Gate(row, "RequireCanonicalizableCoverage");
        Set(row, "CoverageGaps", Array.Empty<string>()); api.Reject(() => api.Gate(row, "RequireCompleteCoverage"));
        Set(native, "PhysicsResourcesEmpty", false); api.Reject(() => api.Gate(row, "RequireCanonicalizableCoverage")); Set(native, "PhysicsResourcesEmpty", true);
        var before = api.EncodeMeshNative(native);
        foreach (var name in new[] { "VerticesDirty", "IndicesDirty", "GpuBuffersEmpty", "DerivedArraysEmpty", "VertexFormatMatches" }) Set(native, name, true);
        Set(native, "MaxBoneIndex", -1); api.Gate(row, "RequireCompleteCoverage");
        CollectionAssert.AreEqual(before, api.EncodeMeshNative(native), "Cold cache markers must not become gameplay/retained D values.");
        var copy = api.Copy(native); ((uint[])Get(native, "BonePathHashes"))[2] ^= 1; ((byte[])Get(native, "Metrics"))[3] = 128;
        CollectionAssert.AreEqual(before, api.EncodeMeshNative(copy)); Assert.IsFalse(before.SequenceEqual(api.EncodeMeshNative(native)));
    }

    [TestMethod]
    public void SerializedAssetDependencyRequiresActualImageHashBeyondMatchingProvenance()
    {
        var api = new Api(); var source = api.SerializedImage();
        Set(source, "SourcePath", "actual/loaded/asset-file"); Set(source, "LocalIdentifier", 901L); Set(source, "NativeTypeRva", 0x1700000);
        Set(source, "Length", 17); Set(source, "TypeHash", Enumerable.Range(0, 16).Select(value => (byte)value).ToArray()); Set(source, "Hash", new string('a', 64));
        api.ImageComplete(source); var copy = api.Copy(source); api.ImagesEqual(source, copy);
        ((byte[])Get(source, "TypeHash"))[0] ^= 1; api.Reject(() => api.ImagesEqual(source, copy));
        var changed = api.Copy(copy); Set(changed, "Hash", new string('b', 64)); api.Reject(() => api.ImagesEqual(copy, changed));
        Set(changed, "Hash", ""); Set(changed, "CoverageGaps", Array.Empty<string>()); api.Reject(() => api.ImageComplete(changed));
        Set(changed, "Hash", new string('z', 64)); api.Reject(() => api.ImageComplete(changed));
    }

    [TestMethod]
    public void ShaderTypedCaptureKeepsDefinitionsAliasesAndMutableValuesWithoutGrantingCoverage()
    {
        var api = new Api(); var state = api.Row("ShaderState");
        Set(state, "Name", "actual shader / changed name"); Set(state, "HasImplementation", true); Set(state, "MaximumLod", 173);
        Set(state, "ImplementationName", "original implementation"); Set(state, "Dependencies", new[] { "asset/shader/a", "", "asset/shader/a" });
        var property = api.Row("ShaderProperty"); Set(property, "Name", "_ActualValue"); Set(property, "Type", 1);
        Set(property, "DefaultValue", new byte[] { 0, 0, 0, 128, 0x45, 0x23, 0xc1, 0x7f, 1, 0, 0, 0, 0, 0, 0, 0 });
        Set(property, "Attributes", new[] { "first", "second" }); Set(state, "Properties", api.Rows("ShaderProperty", property));
        var captured = api.EncodeShaderObserved(state); var d = api.EncodeShaderValues(state); var copy = api.Copy(state);
        Set(state, "ActiveSubShader", 3); CollectionAssert.AreEqual(d, api.EncodeShaderValues(state)); Assert.IsFalse(captured.SequenceEqual(api.EncodeShaderObserved(state)));
        ((byte[])Get(property, "DefaultValue"))[0] = 4; ((string[])Get(state, "Dependencies"))[0] = "asset/shader/b";
        CollectionAssert.AreEqual(captured, api.EncodeShaderObserved(copy), "Shader definitions and repeated bindings must be independently copied.");
        var snapshot = api.Row("Snapshot"); Set(snapshot, "Kind", "Shader"); Set(snapshot, "Shader", copy); Set(snapshot, "CoverageGaps", Array.Empty<string>());
        api.Reject(() => api.Gate(snapshot, "RequireCompleteCoverage")); api.Reject(() => api.Gate(snapshot, "RequireCanonicalizableCoverage"));
        Set(copy, "Properties", api.Rows("ShaderProperty", api.Copy(property), api.Copy(property))); api.Reject(() => api.EncodeShaderObserved(copy));
    }

    [TestMethod]
    public void TextureNativeMetadataKeepsExactBytesAndDoesNotConfuseGpuHandlesWithPixelCoverage()
    {
        var api = new Api(); var state = api.Row("TextureNativeState");
        Set(state, "TexelSize", new byte[] { 0, 0, 0, 128, 0x25, 0x11, 0xc1, 0x7f }); Set(state, "PlatformBlob", new byte[] { 4, 0, 0xff, 7 });
        Set(state, "StreamOffset", 0x100000001UL); Set(state, "StreamSize", 37U); Set(state, "StreamPath", "actual/resource/texture.resS");
        Set(state, "DataWidth", 64); Set(state, "DataHeight", 32); Set(state, "GpuWidth", 32); Set(state, "GpuHeight", 16);
        var data = api.EncodeTextureNative(state); var copy = api.Copy(state);
        Set(state, "TextureIdPresent", true); Set(state, "Uploaded", true); Set(state, "AsyncVersions", new uint[] { 8, 10, 12 });
        CollectionAssert.AreEqual(data, api.EncodeTextureNative(state), "Process-local GPU handles and fence versions must not become retained texture metadata.");
        ((byte[])Get(state, "PlatformBlob"))[0] = 8; ((byte[])Get(state, "TexelSize"))[0] = 1;
        CollectionAssert.AreEqual(data, api.EncodeTextureNative(copy)); Assert.IsFalse(data.SequenceEqual(api.EncodeTextureNative(state)));
        var texture = api.Row("TextureState"); Set(texture, "Native", copy); var snapshot = api.Row("Snapshot"); Set(snapshot, "Kind", "Texture2D"); Set(snapshot, "Texture", texture);
        Set(snapshot, "CoverageGaps", Array.Empty<string>()); api.Reject(() => api.Gate(snapshot, "RequireCompleteCoverage"));
        Set(copy, "SharedPresent", true); api.Reject(() => api.EncodeTextureNative(copy));
    }

    [TestMethod]
    public void AssetWirePreservesUnityStructPrivateFieldsAndFloatBitsWithoutComputedProperties()
    {
        var api = new Api(); var sprite = api.Row("SpriteState");
        var negativeZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
        Set(sprite, "PixelsPerUnit", negativeZero);
        Set(sprite, "Rect", new Rect(2, 3, 32, 64)); Set(sprite, "Pivot", new Vector2(negativeZero, 17));
        Set(sprite, "Vertices", new[] { new Vector2(negativeZero, BitConverter.Int32BitsToSingle(0x7fc01234)) });
        var state = api.Row("Snapshot"); Set(state, "Kind", "Sprite"); Set(state, "Sprite", sprite);
        var copy = api.AssetWireRoundtrip(state); var copied = Get(copy, "Sprite");
        Assert.AreEqual(new Rect(2, 3, 32, 64), (Rect)Get(copied, "Rect"));
        Assert.AreEqual(unchecked((int)0x80000000), BitConverter.SingleToInt32Bits((float)Get(copied, "PixelsPerUnit")));
        Assert.AreEqual(unchecked((int)0x80000000), BitConverter.SingleToInt32Bits(((Vector2)Get(copied, "Pivot")).x));
        var vertices = (Vector2[])Get(copied, "Vertices");
        Assert.AreEqual(0x7fc01234, BitConverter.SingleToInt32Bits(vertices[0].y));
        var mesh = api.Row("Snapshot"); Set(mesh, "Kind", "Mesh"); Set(mesh, "Mesh", api.Mesh());
        var meshCopy = api.AssetWireRoundtrip(mesh);
        Assert.AreEqual(Get(Get(mesh, "Mesh"), "Bounds"), Get(Get(meshCopy, "Mesh"), "Bounds"));
    }

    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static object Get(object owner, string name) => owner.GetType().GetField(name, All)!.GetValue(owner)!;
    private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, All)!.SetValue(owner, value);
    private sealed class Api
    {
        private readonly Type adapter;
        private readonly Type monoNative;
        private readonly Type propertySheet;
        private readonly Type serializedImage;
        internal Api()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
            Assert.IsNotNull(root);
            var runtime = Assembly.LoadFrom(Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
            adapter = runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentNativeAssetState", true)!;
            monoNative = runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentMonoNativeState", true)!;
            propertySheet = runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentShaderPropertySheet", true)!;
            serializedImage = runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentSerializedAssetImage", true)!;
        }
        internal object AssetWireRoundtrip(object snapshot)
        {
            var pipeline = adapter.Assembly.GetType("HollowKnightTAS.Runtime.FullRun.ResidentAssetPipeline", true)!;
            var data = Activator.CreateInstance(pipeline.GetNestedType("Data", All)!, true)!;
            Set(data, "Snapshots", Rows("Snapshot", snapshot));
            var wire = adapter.Assembly.GetType("HollowKnightTAS.Runtime.FullRun.ResidentAssetDataWire", true)!;
            var bytes = (byte[])wire.GetMethod("Encode", All)!.Invoke(null, new[] { data })!;
            var result = wire.GetMethod("Decode", All)!.Invoke(null, new object[] { bytes })!;
            CollectionAssert.AreEqual(bytes, (byte[])wire.GetMethod("Encode", All)!.Invoke(null, new[] { result })!);
            return ((Array)Get(result, "Snapshots")).GetValue(0)!;
        }
        internal object Row(string name) => Activator.CreateInstance(adapter.GetNestedType(name, All)!, true)!;
        internal Array Rows(string name, params object[] values)
        {
            var result = Array.CreateInstance(adapter.GetNestedType(name, All)!, values.Length);
            for (var index = 0; index < values.Length; index++) result.SetValue(values[index], index);
            return result;
        }
        internal object Mesh()
        {
            var state = Row("MeshState");
            Set(state, "Readable", true); Set(state, "BuffersCaptured", true); Set(state, "VertexCount", 4);
            Set(state, "DeclaredSubMeshCount", 2); Set(state, "DeclaredBlendShapeCount", 1); Set(state, "IndexFormat", IndexFormat.UInt16);
            Set(state, "Bounds", new Bounds(Vector3.zero, new Vector3(2, 4, 6)));
            Set(state, "Attributes", new[] {
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float16, 2, 1) });
            Set(state, "VertexBuffers", new[] { Enumerable.Range(0, 48).Select(value => (byte)value).ToArray(), Enumerable.Range(0, 16).Select(value => (byte)(value + 64)).ToArray() });
            Set(state, "IndexBuffer", new ushort[] { ushort.MaxValue, 0, 1, 2, 3, 0 }.SelectMany(BitConverter.GetBytes).ToArray());
            Set(state, "SubMeshes", new[] {
                new SubMeshDescriptor(1, 4, MeshTopology.Points) { firstVertex = 0, vertexCount = 4 },
                new SubMeshDescriptor(2, 2, MeshTopology.Lines) { baseVertex = -1, firstVertex = 0, vertexCount = 2 } });
            Set(state, "BindPoses", new[] { Matrix4x4.identity }); Set(state, "BonesPerVertex", new byte[] { 1, 1, 1, 1 });
            Set(state, "BoneWeights", Enumerable.Range(0, 4).Select(_ => new BoneWeight1 { boneIndex = 0, weight = 1 }).ToArray());
            Set(state, "PackedVariableWeights", new uint[] { 5, 6, 7, 8, 9, 0xffff0000, 0xffff0000, 0xffff0000, 0xffff0000 });
            object Frame(float weight) {
                var frame = Row("BlendFrame"); Set(frame, "Weight", weight); Set(frame, "Vertices", new Vector3[4]);
                Set(frame, "Normals", new Vector3[4]); Set(frame, "Tangents", new Vector3[4]); return frame;
            }
            var shape = Row("BlendShape"); Set(shape, "Name", "arbitrary source shape / 变化"); Set(shape, "Frames", Rows("BlendFrame", Frame(10), Frame(100)));
            Set(state, "BlendShapes", Rows("BlendShape", shape)); return state;
        }
        internal void Validate(object value) => adapter.GetMethod("ValidateMesh", All)!.Invoke(null, new[] { value });
        internal byte[] Encode(object value) => (byte[])adapter.GetMethod("EncodeMesh", All)!.Invoke(null, new[] { value })!;
        internal byte[] EncodeTexture(object value) => (byte[])adapter.GetMethod("EncodeTextureImage", All)!.Invoke(null, new[] { value })!;
        internal byte[] EncodePhysicsMaterial(object value) => (byte[])adapter.GetMethod("EncodePhysicsMaterial", All)!.Invoke(null, new[] { value })!;
        internal byte[] ReadRawString(IntPtr value) => (byte[])adapter.GetMethod("ReadRawString", All)!.Invoke(null, new object[] { value })!;
        internal byte[] EncodeMonoScript(object value) => (byte[])adapter.GetMethod("EncodeMonoScriptValues", All)!.Invoke(null, new[] { value })!;
        internal byte[] EncodeMeshNative(object value) => (byte[])adapter.GetMethod("EncodeMeshNativeValues", All)!.Invoke(null, new[] { value })!;
        internal byte[] EncodeShaderValues(object value) => (byte[])adapter.GetMethod("EncodeShaderValues", All)!.Invoke(null, new[] { value })!;
        internal byte[] EncodeShaderObserved(object value) => (byte[])adapter.GetMethod("EncodeShaderObservedState", All)!.Invoke(null, new[] { value })!;
        internal byte[] EncodeTextureNative(object value) => (byte[])adapter.GetMethod("EncodeTextureNativeValues", All)!.Invoke(null, new[] { value })!;
        internal void MonoScriptEqual(object left, object right) => adapter.GetMethod("RequireMonoScriptEqual", All)!.Invoke(null, new[] { left, right });
        internal object ScriptCache() => Activator.CreateInstance(monoNative.GetNestedType("CacheData", All)!, true)!;
        internal object PropertySheet() => Activator.CreateInstance(propertySheet.GetNestedType("Data", All)!, true)!;
        internal void Gate(object row, string method) => row.GetType().GetMethod(method, All)!.Invoke(row, null);
        internal object SerializedImage() => Activator.CreateInstance(serializedImage.GetNestedType("Data", All)!, true)!;
        internal void ImageComplete(object value) => serializedImage.GetMethod("RequireComplete", All)!.Invoke(null, new[] { value });
        internal void ImagesEqual(object left, object right) => serializedImage.GetMethod("RequireEqual", All)!.Invoke(null, new[] { left, right });
        internal object Copy(object value) => value.GetType().GetMethod("Copy", All)!.Invoke(value, null)!;
        internal IDisposable Saved(int[] kinds, int[] keys, byte[][] values)
            => (IDisposable)Activator.CreateInstance(adapter.GetNestedType("BorrowedSavedMaterialSheet", All)!, All, null, new object[] { kinds, keys, values }, null)!;
        internal IntPtr SavedPointer(IDisposable value) => (IntPtr)value.GetType().GetProperty("Pointer", All)!.GetValue(value)!;
        internal (byte[] Counts, BoneWeight1[] Weights) Decode(int vertices, uint[] words)
        {
            var arguments = new object[] { vertices, words, null!, null! };
            adapter.GetMethod("DecodeVariableSkinning", All)!.Invoke(null, arguments);
            return ((byte[])arguments[2], (BoneWeight1[])arguments[3]);
        }
        internal void Reject(Action action)
        {
            var error = Assert.ThrowsExactly<TargetInvocationException>(action);
            Assert.IsInstanceOfType<InvalidDataException>(error.GetBaseException());
        }
    }
}
