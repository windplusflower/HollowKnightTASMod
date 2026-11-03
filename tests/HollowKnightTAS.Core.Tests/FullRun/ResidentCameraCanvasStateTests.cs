using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentCameraCanvasStateTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static object Get(object owner, string name) => owner.GetType().GetField(name, Flags)!.GetValue(owner)!;
    private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Flags)!.SetValue(owner, value);
    private sealed class Api
    {
        private readonly Type api, shared;
        internal Api()
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo != null && !File.Exists(Path.Combine(repo.FullName, "HollowKnightTAS.sln"))) repo = repo.Parent;
            Assert.IsNotNull(repo);
            var assembly = Assembly.LoadFrom(Path.Combine(repo.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
            api = assembly.GetType("HollowKnightTAS.Runtime.FullRun.ResidentCameraCanvasState", true)!;
            shared = assembly.GetType("HollowKnightTAS.Runtime.FullRun.ResidentSharedMeshState", true)!;
        }
        internal object New(string type) => Activator.CreateInstance(api.GetNestedType(type, Flags)!, true)!;
        internal Array Array(string type, params object[] values)
        {
            var array = System.Array.CreateInstance(api.GetNestedType(type, Flags)!, values.Length);
            for (var index = 0; index < values.Length; index++) array.SetValue(values[index], index);
            return array;
        }
        internal object Row(string type, string kind, string key, params int[] ranges)
        {
            var row = New(type); Set(row, "Owner", key); Set(row, "Scalars", ranges.Select(length => new byte[length]).ToArray());
            var identity = Get(row, "Type"); Set(identity, "Assembly", "UnityEngine.CoreModule"); Set(identity, "Module", "9b972b67-126e-4d2b-893b-e07ab2c34011");
            Set(identity, "Name", "UnityEngine." + kind); Set(identity, "Token", 0x02000001); return row;
        }
        internal object Camera()
        {
            var row = Row("CameraRow", "Camera", "camera", 2, 740, 4, 8, 241, 7, 5, 32, 8, 9, 577, 193, 5, 20, 12);
            Set(row, "OriginTextures", Enumerable.Repeat("", 8).ToArray()); Set(row, "TemporaryTextures", Enumerable.Repeat("", 4).ToArray());
            Set(row, "ColorSurfaces", Array("SurfaceBinding", Enumerable.Range(0, 8).Select(_ => New("SurfaceBinding")).ToArray()));
            var data = New("Data"); Set(data, "Cameras", Array("CameraRow", row));
            var all = Get(Get(data, "CameraManager"), "All"); Set(all, "Capacity", 1); Set(all, "Owners", new[] { "camera" }); return data;
        }
        internal object Canvas()
        {
            var canvas = Row("CanvasRow", "Canvas", "canvas", 2, 4, 1, 64, 64, 10, 15, 37, 5, 5, 6, 6);
            ((byte[][])Get(canvas, "Scalars"))[5][8] = 6;
            Set(canvas, "BatchScalars", new[] { new byte[28], new byte[32] });
            var renderer = Row("CanvasRendererRow", "CanvasRenderer", "renderer", 16, 24, 17, 5, 25, 9); Set(renderer, "Parent", "canvas");
            var instruction = New("Instruction"); Set(instruction, "Materials", Enumerable.Repeat("", 8).ToArray());
            Set(instruction, "Scalars", new[] { new byte[452], new byte[16], new byte[116], new byte[79] }); Set(instruction, "Shared", "ui-shared/renderer");
            Set(canvas, "Instructions", Array("Instruction", instruction)); Set(canvas, "InstructionCapacity", 1);
            var handles = Get(canvas, "Handles"); Set(handles, "Capacity", 1); Set(handles, "Owners", new[] { "renderer/push" });
            var push = Get(renderer, "Push"); Set(push, "Canvas", "canvas"); Set(push, "InstructionIndex", 0); Set(push, "HandleIndex", 0); Set(push, "AbsoluteIndex", 0);
            Set(renderer, "Shared", "ui-shared/renderer");
            var group = New("SharedGroup"); Set(group, "Key", "ui-shared/renderer"); var state = Get(group, "State");
            Set(state, "References", 2); Set(state, "VertexState", new byte[136]);
            var buffers = System.Array.CreateInstance(shared.GetNestedType("Buffer", Flags)!, 7);
            for (var index = 0; index < buffers.Length; index++) buffers.SetValue(Activator.CreateInstance(shared.GetNestedType("Buffer", Flags)!, true), index);
            Set(state, "Buffers", buffers);
            var data = New("Data"); Set(data, "Canvases", Array("CanvasRow", canvas)); Set(data, "Renderers", Array("CanvasRendererRow", renderer)); Set(data, "Shared", Array("SharedGroup", group)); return data;
        }
        internal object First(object data, string name) => ((Array)Get(data, name)).GetValue(0)!;
        internal object DirtyCanvas()
        {
            var data = Canvas(); var other = Row("CanvasRendererRow", "CanvasRenderer", "z-renderer", 16, 24, 17, 5, 25, 9);
            Set(other, "Parent", "canvas"); Set(data, "Renderers", Array("CanvasRendererRow", First(data, "Renderers"), other));
            var group = New("DirtyGroup"); Set(group, "Canvas", "canvas"); Set(group, "Renderers", new[] { "z-renderer", "renderer" });
            Set(Get(data, "CanvasManager"), "Dirty", Array("DirtyGroup", group)); return data;
        }
        internal object Transient(string alias)
        {
            var value = New("TransientTexture"); Set(value, "Alias", alias);
            var type = Get(Row("CameraRow", "RenderTexture", "unused"), "Type"); Set(value, "Type", type); return value;
        }
        internal object PoolTexture(string alias)
        {
            var value = New("PoolTexture"); Set(value, "Texture", Transient(alias)); Set(value, "Descriptor", new byte[52]); return value;
        }
        internal void Call(string name, params object[] values) => api.GetMethod(name, Flags)!.Invoke(null, values);
        internal void Reject(string name, params object[] values)
        {
            var error = Assert.ThrowsExactly<TargetInvocationException>(() => Call(name, values)); Assert.IsInstanceOfType<InvalidDataException>(error.InnerException);
        }
    }
    [TestMethod]
    public void EmptyCanvasDirtyBucketsCanEnterCommonCanonButCannotClaimFinalCoverage()
    {
        var api = new Api(); var data = api.Canvas(); var registry = Get(data, "CanvasManager");
        Set(registry, "EmptyDirtyBuckets", 1); Set(data, "CoverageGaps", System.Array.Empty<string>());
        api.Call("Validate", data); api.Call("RequireCanonicalizableCoverage", data);
        api.Reject("RequireCompleteCoverage", data); api.Reject("RequireCanonicalState", data);
        Set(registry, "EmptyDirtyBuckets", 0);
        api.Call("RequireCompleteCoverage", data); api.Call("RequireCanonicalState", data);
    }
    [TestMethod]
    public void EmptyCanvasDirtyBucketCountSharesTheBoundWithNonemptyGroups()
    {
        var api = new Api(); var data = api.Canvas(); var registry = Get(data, "CanvasManager");
        Set(registry, "EmptyDirtyBuckets", -1); api.Reject("Validate", data);
        Set(registry, "EmptyDirtyBuckets", 100000); api.Call("Validate", data); api.Call("RequireCanonicalizableCoverage", data);
        Set(registry, "EmptyDirtyBuckets", 100001); api.Reject("Validate", data);
        var group = api.New("DirtyGroup"); Set(group, "Canvas", "canvas"); Set(group, "Renderers", new[] { "renderer" });
        Set(registry, "Dirty", api.Array("DirtyGroup", group)); Set(registry, "EmptyDirtyBuckets", 99999); api.Call("Validate", data);
        Set(registry, "EmptyDirtyBuckets", 100000); api.Reject("Validate", data);
        Set(registry, "EmptyDirtyBuckets", int.MaxValue); api.Reject("Validate", data);
    }
    [TestMethod]
    public void DirtyCanvasGroupsRequireActualNonemptyUniqueRendererOwners()
    {
        var api = new Api(); var data = api.Canvas(); var registry = Get(data, "CanvasManager");
        var group = api.New("DirtyGroup"); Set(group, "Canvas", "canvas"); Set(group, "Renderers", new[] { "renderer" });
        Set(registry, "Dirty", api.Array("DirtyGroup", group)); api.Call("Validate", data);
        Set(group, "Renderers", System.Array.Empty<string>()); api.Reject("Validate", data);
        Set(group, "Renderers", new[] { "absent-renderer" }); api.Reject("Validate", data);
        Set(group, "Renderers", new[] { "renderer", "renderer" }); api.Reject("Validate", data);
        Set(group, "Renderers", new[] { "renderer" }); Set(group, "Canvas", "absent-canvas"); api.Reject("Validate", data);
        Set(group, "Canvas", "canvas"); Set(registry, "Dirty", api.Array("DirtyGroup", group, group)); api.Reject("Validate", data);
    }
    [TestMethod]
    public void DirtyRendererNativeTraversalOrderIsValidAndRemainsObservable()
    {
        var api = new Api(); var expected = api.DirtyCanvas(); var actual = api.DirtyCanvas();
        api.Call("Validate", expected); api.Call("RequireCanonicalizableCoverage", expected); api.Call("RequireEqual", expected, actual);
        var group = api.First(Get(actual, "CanvasManager"), "Dirty"); Set(group, "Renderers", new[] { "renderer", "z-renderer" });
        api.Call("Validate", actual); api.Reject("RequireEqual", expected, actual);
    }
    [TestMethod]
    public void CameraReadbackIncludesPreviousMatricesAndNativeSortRegistration()
    {
        var api = new Api(); var expected = api.Camera(); var actual = api.Camera(); api.Call("RequireCompleteCoverage", expected); api.Call("RequireEqual", expected, actual);
        ((byte[][])Get(api.First(actual, "Cameras"), "Scalars"))[10][64] = 1; api.Reject("RequireEqual", expected, actual);
        ((byte[][])Get(api.First(actual, "Cameras"), "Scalars"))[10][64] = 0; Set(Get(actual, "CameraManager"), "OnscreenSorted", true); api.Reject("RequireEqual", expected, actual);
    }
    [TestMethod]
    public void ClearingDiagnosticGapsCannotAdmitCameraCommandBuffersOrUnknownSurfaces()
    {
        var api = new Api(); var data = api.Camera(); var row = api.First(data, "Cameras");
        Set(row, "EventBufferCapacity", 1); Set(row, "EventCounts", new[] { 1 }); Set(row, "EventCapacities", new[] { 1 }); Set(data, "CoverageGaps", System.Array.Empty<string>());
        api.Call("Validate", data); api.Reject("RequireCompleteCoverage", data);
        Set(row, "EventCounts", new[] { 0 }); Set(Get(row, "DepthSurface"), "Unknown", true); api.Reject("RequireCompleteCoverage", data);
    }
    [TestMethod]
    public void CanvasHandleDirectoryMustBeReciprocalWithActualInstructionOwners()
    {
        var api = new Api(); var data = api.Canvas(); api.Call("RequireCompleteCoverage", data);
        var canvas = api.First(data, "Canvases"); Set(Get(canvas, "Handles"), "Owners", new[] { "" }); api.Reject("Validate", data);
        Set(Get(canvas, "Handles"), "Owners", new[] { "renderer/push" }); Set(Get(api.First(data, "Renderers"), "Push"), "InstructionIndex", 1); api.Reject("Validate", data);
    }
    [TestMethod]
    public void SharedGeometryRequiresEveryNativeRetainAndPreservesInstructionAlias()
    {
        var api = new Api(); var data = api.Canvas(); var group = api.First(data, "Shared"); api.Call("RequireCompleteCoverage", data);
        Set(Get(group, "State"), "References", 3); api.Reject("RequireCompleteCoverage", data);
        Set(Get(group, "State"), "References", 2); var instruction = api.First(api.First(data, "Canvases"), "Instructions"); Set(instruction, "Shared", ""); api.Reject("RequireCompleteCoverage", data);
    }
    [TestMethod]
    public void CanvasReadbackIncludesClippingSoftnessAndRetainedCpuGeometry()
    {
        var api = new Api(); var expected = api.Canvas(); var actual = api.Canvas(); api.Call("RequireEqual", expected, actual);
        var instruction = api.First(api.First(actual, "Canvases"), "Instructions"); ((byte[][])Get(instruction, "Scalars"))[3][68] = 1; api.Reject("RequireEqual", expected, actual);
        ((byte[][])Get(instruction, "Scalars"))[3][68] = 0;
        var state = Get(api.First(actual, "Shared"), "State"); ((byte[])Get(state, "VertexState"))[0] = 1; api.Reject("RequireEqual", expected, actual);
    }
    [TestMethod]
    public void CanvasGpuCachesAndCallbackContextsCannotBeHiddenByDiagnosticEdits()
    {
        var api = new Api(); var data = api.Canvas(); Set(api.First(data, "Canvases"), "BatchBuffers", true); api.Reject("RequireCompleteCoverage", data);
        Set(api.First(data, "Canvases"), "BatchBuffers", false); var callback = api.New("Callback"); Set(callback, "Rva", 0x1234); Set(callback, "WithUser", true); Set(callback, "UserKind", "unsupported");
        Set(Get(data, "CanvasManager"), "Callbacks", api.Array("Callback", callback)); Set(data, "CoverageGaps", System.Array.Empty<string>()); api.Call("Validate", data); api.Reject("RequireCompleteCoverage", data);
        Set(Get(data, "CanvasManager"), "Callbacks", api.Array("Callback")); ((byte[][])Get(api.First(data, "Canvases"), "Scalars"))[5][8] = 0;
        api.Reject("RequireCompleteCoverage", data);
    }
    [TestMethod]
    public void ActualTypeCannotSelectAnotherCameraOrCanvasNativeLayout()
    {
        var api = new Api(); var data = api.Camera(); Set(Get(api.First(data, "Cameras"), "Type"), "Name", "UnityEngine.Canvas"); api.Reject("Validate", data);
        data = api.Canvas(); ((byte[][])Get(api.First(data, "Renderers"), "Scalars"))[0] = new byte[24]; api.Reject("Validate", data);
    }
    [TestMethod]
    public void TemporaryCameraTextureRequiresItsActualTakenPoolOwnership()
    {
        var api = new Api(); var data = api.Camera(); var camera = api.First(data, "Cameras");
        Set(camera, "TransientTextures", api.Array("TransientTexture", api.Transient("camera-cache/a"), api.New("TransientTexture"), api.New("TransientTexture")));
        Set(data, "CoverageGaps", System.Array.Empty<string>()); api.Reject("RequireCanonicalizableCoverage", data);
        var pool = Get(data, "RenderTextures"); Set(pool, "Present", true); Set(pool, "Taken", api.Array("PoolTexture", api.PoolTexture("camera-cache/a")));
        api.Call("RequireCanonicalizableCoverage", data); api.Reject("RequireCompleteCoverage", data);
        Set(pool, "Taken", api.Array("PoolTexture", api.PoolTexture("camera-cache/a"), api.PoolTexture("camera-cache/unbound")));
        api.Reject("RequireCanonicalizableCoverage", data);
    }
    [TestMethod]
    public void ReturnedTexturePoolCannotHideBehindEmptyCameraSlots()
    {
        var api = new Api(); var data = api.Camera(); var pool = Get(data, "RenderTextures"); Set(pool, "Present", true); Set(pool, "FreeCapacity", 4); Set(pool, "TemporaryCounter", 7);
        var bucket = api.New("PoolBucket"); Set(bucket, "Hash", 1U); Set(bucket, "Descriptor", new byte[52]); Set(bucket, "Textures", api.Array("PoolTexture", api.PoolTexture("pool/free/a")));
        Set(pool, "Free", api.Array("PoolBucket", bucket)); api.Call("RequireCanonicalizableCoverage", data); api.Reject("RequireCompleteCoverage", data);
        Set(pool, "BufferCapacity", 1); Set(pool, "BufferEntries", 1); Set(data, "CoverageGaps", System.Array.Empty<string>()); api.Reject("RequireCanonicalizableCoverage", data);
        Set(pool, "BufferEntries", 0); api.Call("RequireCanonicalizableCoverage", data);
        Set(pool, "Taken", api.Array("PoolTexture", api.PoolTexture("pool/free/a"))); api.Reject("Validate", data);
    }
    [TestMethod]
    public void ShadowCacheCanonRequiresTypedTexturePoolClosure()
    {
        var api = new Api(); var data = api.Camera(); var camera = api.First(data, "Cameras");
        var shadow = api.New("ShadowEntry"); Set(shadow, "Key", -3); Set(shadow, "Texture", api.Transient("camera-cache/shadow")); Set(shadow, "Scalars", new byte[344]);
        Set(camera, "ShadowCache", true); Set(camera, "ShadowEntries", 1); Set(camera, "Shadows", api.Array("ShadowEntry", shadow));
        api.Reject("RequireCanonicalizableCoverage", data);
        var pool = Get(data, "RenderTextures"); Set(pool, "Present", true); Set(pool, "Taken", api.Array("PoolTexture", api.PoolTexture("camera-cache/shadow")));
        api.Call("RequireCanonicalizableCoverage", data); api.Reject("RequireCompleteCoverage", data);
        Set(camera, "ShadowEntries", 0); api.Reject("Validate", data);
    }
    [TestMethod]
    public void CanvasCommonCanonRejectsUnfinishedJobsAndGeometryFences()
    {
        var api = new Api(); var data = api.Canvas(); var canvas = api.First(data, "Canvases");
        Set(canvas, "BatchBuffers", true); Set(canvas, "BatchFence", true); api.Call("RequireCanonicalizableCoverage", data); api.Reject("RequireCompleteCoverage", data);
        Set(canvas, "BatchJobDone", false); api.Reject("RequireCanonicalizableCoverage", data);
        Set(canvas, "BatchJobDone", true); Set(canvas, "GeometryFence", true); api.Reject("RequireCanonicalizableCoverage", data);
    }
    [TestMethod]
    public void CameraCanvasS0ChecksCallbackAndPoolStateWhileRetainingDValues()
    {
        var api = new Api(); var data = api.Canvas(); api.Call("RequireCanonicalState", data);
        Set(Get(data, "CanvasManager"), "CallbackChanged", true); api.Call("RequireCompleteCoverage", data); api.Reject("RequireCanonicalState", data);
        Set(Get(data, "CanvasManager"), "CallbackChanged", false); var pool = Get(data, "RenderTextures"); Set(pool, "Present", true); Set(pool, "TextureFrame", 42); Set(pool, "BufferFrame", 17); Set(pool, "VrUsage", 2);
        ((byte[][])Get(api.First(data, "Canvases"), "Scalars"))[3][12] = 0x80; api.Call("RequireCanonicalState", data);
        Set(pool, "TemporaryCounter", 1); api.Reject("RequireCanonicalState", data);
    }
}
