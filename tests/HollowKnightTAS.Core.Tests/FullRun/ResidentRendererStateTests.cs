using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentRendererStateTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static object Get(object owner, string name) => owner.GetType().GetField(name, Flags)!.GetValue(owner)!;
    private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Flags)!.SetValue(owner, value);
    private sealed class Api
    {
        private readonly Type api;
        internal Api()
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo != null && !File.Exists(Path.Combine(repo.FullName, "HollowKnightTAS.sln"))) repo = repo.Parent;
            Assert.IsNotNull(repo);
            api = Assembly.LoadFrom(Path.Combine(repo.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
                .GetType("HollowKnightTAS.Runtime.FullRun.ResidentRendererState", true)!;
        }
        internal object New(string type) => Activator.CreateInstance(api.GetNestedType(type, Flags)!, true)!;
        internal object EmptyProperties() => Activator.CreateInstance(api.Assembly.GetType("HollowKnightTAS.Runtime.FullRun.ResidentShaderPropertySheet", true)!.GetNestedType("Data", Flags)!, true)!;
        internal Array Array(string type, params object[] values)
        {
            var array = System.Array.CreateInstance(api.GetNestedType(type, Flags)!, values.Length);
            for (var index = 0; index < values.Length; index++) array.SetValue(values[index], index);
            return array;
        }
        internal object Fixture(string kind = "MeshRenderer")
        {
            var row = New("Row"); Set(row, "Owner", "renderer"); Set(row, "Kind", kind);
            var type = Get(row, "Type"); Set(type, "Assembly", "UnityEngine.CoreModule"); Set(type, "Module", "9b972b67-126e-4d2b-893b-e07ab2c34011");
            Set(type, "Name", "UnityEngine." + kind); Set(type, "Token", 0x02000001);
            var stream = new byte[7]; Buffer.BlockCopy(BitConverter.GetBytes(-1), 0, stream, 0, 4);
            var scalarSizes = kind == "ParticleSystemRenderer" ? new[] { 244, 7, 12, 208, 4 } : kind == "MeshRenderer" ? new[] { 244, 7, 12, 1 } : new[] { 244, 7, 12, 18, 8, 25, 26 };
            var scalars = scalarSizes.Select(size => new byte[size]).ToArray(); scalars[1] = stream; Buffer.BlockCopy(BitConverter.GetBytes(0xfffff000U), 0, scalars[0], 192, 4); Set(row, "Scalars", scalars);
            Set(row, "Meshes", kind == "ParticleSystemRenderer" ? new[] { "", "", "", "" } : kind == "MeshRenderer" ? new[] { "", "", "" } : System.Array.Empty<string>());
            Set(row, "CachedMeshes", kind == "ParticleSystemRenderer" ? new[] { "", "", "", "" } : kind == "MeshRenderer" ? new[] { "", "", "" } : System.Array.Empty<string>());
            Set(row, "ConsumerLinked", new bool[kind == "ParticleSystemRenderer" ? 4 : kind == "MeshRenderer" ? 3 : 1]);
            if (kind == "ParticleSystemRenderer") {
                var particle = New("ParticleRendererData"); Set(particle, "StripIndices", Array("NativeBuffer", Enumerable.Range(0, 4).Select(_ => New("NativeBuffer")).ToArray()));
                Set(row, "Particle", particle);
            }
            var data = New("Data"); Set(data, "Rows", Array("Row", row));
            var registry = Get(data, "Updates"); Set(registry, "Systems", Array("UpdateSystem", Enumerable.Range(0, 17).Select(_ => New("UpdateSystem")).ToArray()));
            return data;
        }
        internal object Owner(object data) => ((Array)Get(data, "Rows")).GetValue(0)!;
        internal void Call(string name, params object[] values) => api.GetMethod(name, Flags)!.Invoke(null, values);
        internal void Reject(string name, params object[] values)
        {
            var error = Assert.ThrowsExactly<TargetInvocationException>(() => Call(name, values));
            Assert.IsInstanceOfType<InvalidDataException>(error.InnerException);
        }
    }
    [TestMethod]
    public void ShapeOnlyMeshListsRequireMatchingOwnerCacheAndLinkedNode()
    {
        var api = new Api(); var data = api.Fixture(); var shape = api.New("ShapeConsumer");
        Set(shape, "Owner", "particle"); Set(shape, "Mesh", "mesh/shape"); Set(shape, "SharedMesh", "mesh/shape"); Set(shape, "Linked", true);
        Set(data, "Shapes", api.Array("ShapeConsumer", shape));
        var entry = api.New("Consumer"); Set(entry, "Owner", "particle"); Set(entry, "OtherRole", "ParticleSystem.Shape");
        var list = api.New("ConsumerList"); Set(list, "Asset", "mesh/shape"); Set(list, "Kind", "Mesh"); Set(list, "Entries", api.Array("Consumer", entry));
        Set(data, "Consumers", api.Array("ConsumerList", list)); api.Call("Validate", data);
        Set(list, "Entries", api.Array("Consumer")); api.Reject("Validate", data);
        Set(list, "Entries", api.Array("Consumer", entry)); Set(shape, "Linked", false); api.Reject("Validate", data);
        Set(shape, "Linked", true); Set(shape, "SharedMesh", "mesh/other"); api.Reject("Validate", data);
        Set(shape, "SharedMesh", "mesh/shape"); Set(entry, "Slot", 1); api.Reject("Validate", data);
    }
    [TestMethod]
    public void SortingGroupUsesTheNativeInvalidSentinelAndRejectsActualGroupZero()
    {
        var api = new Api(); var data = api.Fixture(); api.Call("RequireCompleteCoverage", data);
        Buffer.BlockCopy(BitConverter.GetBytes(0U), 0, ((byte[][])Get(api.Owner(data), "Scalars"))[0], 192, 4);
        api.Reject("RequireCanonicalizableCoverage", data);
    }
    [TestMethod]
    public void ParticleRendererKeepsSettingsAndOwnedIndicesWhileCanonicalizingBorrowedDrawViews()
    {
        var api = new Api(); var data = api.Fixture("ParticleSystemRenderer"); api.Call("RequireCompleteCoverage", data);
        var particle = Get(api.Owner(data), "Particle"); Set(particle, "DrawCacheDirty", true);
        api.Call("RequireCanonicalizableCoverage", data); api.Reject("RequireCompleteCoverage", data);
        Set(particle, "DrawCacheDirty", false); Set(particle, "InstanceBufferPresent", true); api.Reject("RequireCanonicalizableCoverage", data);
        Set(particle, "InstanceBufferPresent", false);
        ((byte[][])Get(api.Owner(data), "Scalars"))[3][12] = 1;
        api.Reject("RequireEqual", data, api.Fixture("ParticleSystemRenderer"));
    }
    [TestMethod]
    public void DrawingPaddingIsCanonicalizedWithoutDiscountingNamedFieldsOrExecutionOwnership()
    {
        var api = new Api(); var before = api.Fixture(); var row = api.Owner(before);
        var fields = ((byte[][])Get(row,"Scalars"))[0];
        Buffer.BlockCopy(BitConverter.GetBytes(0xffe00001u),0,fields,232,4);
        api.Call("RequireCanonicalizableCoverage",before); api.Reject("RequireCanonicalState",before);
        Set(row,"Execution",true); api.Reject("RequireCanonicalizableCoverage",before);
        Set(row,"Execution",false); Buffer.BlockCopy(BitConverter.GetBytes(0x00200001u),0,fields,232,4);
        api.Call("RequireCanonicalState",before); api.Reject("RequireEqual",before,api.Fixture());
    }
    [TestMethod]
    public void NativeRendererReadbackIncludesPreviousMatrixAndRegistryFrame()
    {
        var api = new Api(); var expected = api.Fixture(); var actual = api.Fixture(); api.Call("RequireCompleteCoverage", expected);
        api.Call("RequireEqual", expected, actual);
        ((byte[][])Get(api.Owner(actual), "Scalars"))[0][64] = 0x80; api.Reject("RequireEqual", expected, actual);
        ((byte[][])Get(api.Owner(actual), "Scalars"))[0][64] = 0;
        Set(Get(actual, "Updates"), "MotionVectorFrame", 1); api.Reject("RequireEqual", expected, actual);
    }
    [TestMethod]
    public void ClearingDiagnosticGapsCannotAdmitStreamingOrSpriteNativeResources()
    {
        var api = new Api(); var mesh = api.Fixture();
        ((byte[][])Get(api.Owner(mesh), "Scalars"))[1][0] = 0; api.Reject("RequireCompleteCoverage", mesh);
        var sprite = api.Fixture("SpriteRenderer"); Set(api.Owner(sprite), "SpriteDeformed", true);
        Set(sprite, "CoverageGaps", System.Array.Empty<string>()); Set(api.Owner(sprite), "OpaqueStates", System.Array.Empty<string>());
        api.Call("Validate", sprite); api.Reject("RequireCompleteCoverage", sprite);
    }
    [TestMethod]
    public void SpriteCpuCacheAndRegistryAreRequiredEvenForAnEmptyNineSliceBuffer()
    {
        var api = new Api(); var data = api.Fixture("SpriteRenderer"); var row = api.Owner(data);
        var cache = api.New("SpriteCacheState"); Set(cache, "Scalars", new byte[56]); Set(cache, "PreparePending", true); Set(cache, "VertexFormatCleared", true);
        var shared = api.New("SpriteSharedMesh"); Set(shared, "References", 1); Set(shared, "VertexState", new byte[136]);
        Set(shared, "Buffers", api.Array("NativeBuffer", Enumerable.Range(0, 7).Select(_ => api.New("NativeBuffer")).ToArray()));
        Set(cache, "Shared", shared); Set(row, "SpriteCache", cache); Set(row, "SpriteNineSlice", true);
        var registry = Get(data, "Sprites"); Set(registry, "Capacity", 1); Set(registry, "Owners", new[] { "renderer" });
        api.Call("RequireCompleteCoverage", data);
        ((bool[])Get(cache, "GeometryBuffers"))[2] = true; api.Reject("RequireCompleteCoverage", data);
        ((bool[])Get(cache, "GeometryBuffers"))[2] = false; Set(registry, "Owners", System.Array.Empty<string>()); api.Reject("Validate", data);
    }
    [TestMethod]
    public void AssetConsumerListsPreserveRepeatedAssetSlotsAndRejectAliasCollapse()
    {
        var api = new Api(); var data = api.Fixture(); var row = api.Owner(data);
        Set(row, "CachedMeshes", new[] { "mesh", "mesh", "" }); Set(row, "ConsumerLinked", new[] { true, true, false });
        var first = api.New("Consumer"); Set(first, "Owner", "renderer");
        var second = api.New("Consumer"); Set(second, "Owner", "renderer"); Set(second, "Slot", 1);
        var list = api.New("ConsumerList"); Set(list, "Kind", "Mesh"); Set(list, "Asset", "mesh"); Set(list, "Entries", api.Array("Consumer", second, first));
        Set(data, "Consumers", api.Array("ConsumerList", list)); api.Call("Validate", data);
        Set(second, "Slot", 0); api.Reject("Validate", data);
        Set(second, "Slot", 1); Set(list, "Entries", api.Array("Consumer", first)); api.Reject("Validate", data);
    }
    [TestMethod]
    public void SharedAssetMayHaveAnOrderedExternalConsumerButCannotAliasASceneSlot()
    {
        var api = new Api(); var data = api.Fixture(); var row = api.Owner(data);
        Set(row, "CachedMeshes", new[] { "mesh", "", "" }); Set(row, "ConsumerLinked", new[] { true, false, false });
        var scene = api.New("Consumer"); Set(scene, "Owner", "renderer");
        var external = api.New("Consumer"); Set(external, "Owner", "prefab"); Set(external, "OtherRole", "MeshRenderer");
        var list = api.New("ConsumerList"); Set(list, "Kind", "Mesh"); Set(list, "Asset", "mesh"); Set(list, "Entries", api.Array("Consumer", external, scene));
        Set(data, "Consumers", api.Array("ConsumerList", list)); api.Call("Validate", data);
        Set(external, "Slot", 3); api.Reject("Validate", data);
        Set(external, "Slot", 0); Set(external, "Owner", "renderer"); api.Reject("Validate", data);
        Set(external, "Owner", "prefab"); Set(external, "OtherRole", "SpriteMask"); api.Reject("Validate", data);
    }
    [TestMethod]
    public void RendererSceneHandlesMustPointBackToTheActualRegisteredOwner()
    {
        var api = new Api(); var data = api.Fixture(); var row = api.Owner(data); var scene = Get(data, "Scene");
        var node = api.New("SceneNode"); Set(node, "Owner", "renderer"); Set(node, "State", new byte[16]);
        Set(scene, "Nodes", api.Array("SceneNode", node)); Set(scene, "Bounds", new byte[24]); Set(scene, "Visibility", new byte[1]);
        Set(scene, "Capacities", new[] { 0, 0, 0, 0, 1, 1, 1 }); Set(row, "SceneHandle", 0); api.Call("Validate", data);
        Set(node, "Owner", ""); api.Reject("Validate", data);
        Set(node, "Owner", "renderer"); Set(scene, "Removal", new[] { 1 }); Set(scene, "Capacities", new[] { 0, 0, 0, 1, 1, 1, 1 }); api.Reject("Validate", data);
    }
    [TestMethod]
    public void ActualTypeCannotChooseAnUnrelatedNativeLayout()
    {
        var api = new Api(); var data = api.Fixture(); Set(Get(api.Owner(data), "Type"), "Name", "UnityEngine.SpriteRenderer"); api.Reject("Validate", data);
        data = api.Fixture(); ((byte[][])Get(api.Owner(data), "Scalars"))[3] = new byte[8]; api.Reject("Validate", data);
    }
    [TestMethod]
    public void SpriteS0ReadsUniqueSecondarySheetRetainsRatherThanOnlyPropertyValues()
    {
        var api = new Api(); var data = api.Fixture("SpriteRenderer"); var row = api.Owner(data);
        Set(row, "SecondaryProperties", api.EmptyProperties()); Set(row, "SecondaryReferenceCount", 2);
        api.Call("RequireCanonicalizableCoverage", data); api.Reject("RequireCanonicalState", data);
        Set(row, "SecondaryReferenceCount", 1); api.Call("RequireCanonicalState", data);
        Set(row, "SecondaryReferenceCount", 0); api.Reject("Validate", data);
    }
    [TestMethod]
    public void SpriteS0RequiresThePendingVertexFormatPointerToBeActuallyCleared()
    {
        var api = new Api(); var data = api.Fixture("SpriteRenderer"); var row = api.Owner(data);
        var cache = api.New("SpriteCacheState"); Set(cache, "Scalars", new byte[56]); Set(cache, "PreparePending", true);
        Set(row, "SpriteCache", cache); Set(row, "SpriteNineSlice", true); var registry = Get(data, "Sprites"); Set(registry, "Capacity", 1); Set(registry, "Owners", new[] { "renderer" });
        api.Call("RequireCanonicalizableCoverage", data); api.Reject("RequireCanonicalState", data);
        Set(cache, "VertexFormatCleared", true); api.Call("RequireCanonicalState", data);
        Set(cache, "PreparePending", false); Set(cache, "VertexFormatCleared", false); Set(cache, "VertexFormat", new byte[56]);
        api.Call("RequireCanonicalizableCoverage", data); api.Reject("RequireCanonicalState", data);
    }
}
