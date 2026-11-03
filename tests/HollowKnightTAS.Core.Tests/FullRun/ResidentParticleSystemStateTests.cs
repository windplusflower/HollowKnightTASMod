using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentParticleSystemStateTests
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
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
                .GetType("HollowKnightTAS.Runtime.FullRun.ResidentParticleSystemState", true)!;
        }
        internal object New(string name) => Activator.CreateInstance(api.GetNestedType(name, Flags)!, true)!;
        internal Array Items(string name, params object[] items)
        {
            var result = Array.CreateInstance(api.GetNestedType(name, Flags)!, items.Length);
            for (var i = 0; i < items.Length; i++) result.SetValue(items[i], i);
            return result;
        }
        internal object Field(int part, int offset)
        {
            var fields = (Array)Call("ArrayFields")!;
            return fields.Cast<object>().Single(field => (int)Get(field, "Part") == part && (int)Get(field, "Offset") == offset);
        }
        internal object Curve()
        {
            var value = New("ValueNode");
            var curve = Activator.CreateInstance(value.GetType().GetField("Curve", Flags)!.FieldType, true)!;
            Set(curve, "Caches", new byte[56]); Set(value, "Curve", curve);
            Set(value, "Optimized", new byte[40]); Set(value, "Padding", new byte[16]);
            return value;
        }
        internal int[] Offsets(string name) => (int[])api.GetField(name, Flags)!.GetValue(null)!;
        internal object? Call(string name, params object[] args) => api.GetMethod(name, Flags)!.Invoke(null, args);
        internal string[] Refresh(object data)
        {
            var gaps = (string[])Call("RequiredCoverageGaps", data)!;
            Set(data, "CoverageGaps", gaps); return gaps;
        }
        internal void Reject(string method, params object[] args)
        {
            var error = Assert.ThrowsExactly<TargetInvocationException>(() => Call(method, args));
            Assert.IsInstanceOfType<InvalidDataException>(error.InnerException);
        }
        internal object BusyRowData()
        {
            var data = New("Data"); var row = New("Row"); var registry = Get(data, "Registry");
            Set(registry, "Present", true); Set(row, "Owner", "test/actual-emitter"); Set(row, "ManagedBufferIsOwner", true);
            Set(row, "NeedsSync", true); Set(row, "RayBudget", new byte[16]); Set(row, "ManagedJobRange", new byte[12]);
            var type = Get(row, "Type"); Set(type, "Name", "UnityEngine.ParticleSystem"); Set(type, "Assembly", "UnityEngine.ParticleSystemModule");
            Set(type, "Module", "23d61384-e7eb-4114-b030-20dda7fcb77b"); Set(type, "Token", 0x02000001);
            var rows = Array.CreateInstance(row.GetType(), 1); rows.SetValue(row, 0); Set(data, "Rows", rows);
            Refresh(data); return data;
        }
    }
    [TestMethod]
    public void FixedModuleScalarWritesExcludeAllPointerAllocatorAndInstanceIdSlots()
    {
        var api = new Api();
        var ranges = api.Offsets("ModuleScalarRanges"); var writable = new HashSet<int>();
        for (var i = 0; i < ranges.Length; i += 2)
            for (var j = ranges[i]; j < ranges[i] + ranges[i + 1]; j++) Assert.IsTrue(j >= 0 && j < 5248 && writable.Add(j));
        foreach (var category in new[] { ("ModuleVTables", 8), ("ModuleLinks", 8), ("ModuleCurves", 8), ("ModuleGradients", 8),
            ("ModulePPtrs", 4), ("ModuleLabels", 4), ("ModuleArrays", 32) })
            foreach (var offset in api.Offsets(category.Item1))
                Assert.IsFalse(Enumerable.Range(offset, category.Item2).Any(writable.Contains), category.Item1 + " @ " + offset);
        Assert.IsTrue(api.Offsets("ModulePPtrs").Contains(4816));    // Cached light instance ID is process-local.
        Assert.IsTrue(Enumerable.Range(384, 64).All(writable.Contains)); // InitialModule's four SIMD random lanes are D.
        Assert.IsTrue(api.Offsets("ModuleVTables").Contains(1280)); // Embedded emission burst polymorphic header.
    }
    [TestMethod]
    public void AQuietManagerFenceDoesNotProveProducerAdmission()
    {
        var api = new Api(); var data = api.New("Data"); api.Call("RequireCompleteCoverage", data);
        Set(Get(data, "Registry"), "Present", true);
        CollectionAssert.Contains(api.Refresh(data), "manager-worker-and-render-producer-admission");
        api.Call("Validate", data); api.Reject("RequireCompleteCoverage", data);
        Set(data, "CoverageGaps", Array.Empty<string>()); api.Reject("Validate", data);
    }
    [TestMethod]
    public void BusyJobCannotCarryOrPretendToHaveReadAParticleBody()
    {
        var api = new Api(); var data = api.BusyRowData(); var row = ((Array)Get(data, "Rows")).GetValue(0)!;
        api.Call("Validate", data); CollectionAssert.Contains((string[])Get(data, "CoverageGaps"), "pending-emitter-update-job");
        Set(row, "Simulation", api.New("Body")); api.Reject("Validate", data);
        Set(row, "Simulation", null!); Set(row, "NeedsSync", false); api.Refresh(data); api.Reject("Validate", data);
    }
    [TestMethod]
    public void EmitterIndexAndBothManagerArraysMustAgree()
    {
        var api = new Api(); var data = api.BusyRowData(); var row = ((Array)Get(data, "Rows")).GetValue(0)!; var registry = Get(data, "Registry");
        var storage = Get(registry, "ActiveStorage"); Set(storage, "Count", 1); Set(storage, "Capacity", 2); Set(storage, "Allocated", true); Set(storage, "Allocation", 1);
        Set(registry, "Active", new[] { (string)Get(row, "Owner") }); api.Reject("Validate", data);
        Set(row, "ActiveIndex", 0); api.Call("Validate", data);
        Set(registry, "StoppingStorage", storage); Set(registry, "Stopping", new[] { (string)Get(row, "Owner") }); api.Reject("Validate", data);
    }
    [TestMethod]
    public void IndependentReadbackComparesTheManagerStackEpochAndRayBudget()
    {
        var api = new Api(); var expected = api.BusyRowData(); var actual = api.BusyRowData();
        api.Call("RequireEqual", expected, actual);
        Set(Get(actual, "Registry"), "StackVersion", 1UL); api.Reject("RequireEqual", expected, actual);
        Set(Get(actual, "Registry"), "StackVersion", 0UL); ((byte[])Get(((Array)Get(actual, "Rows")).GetValue(0)!, "RayBudget"))[8] = 1;
        api.Reject("RequireEqual", expected, actual);
    }
    [TestMethod]
    public void ValueGraphRetainsFirstUseOrderAndRejectsAliasedOwnersBeforeMutation()
    {
        var api = new Api(); var data = api.New("Data"); var row = api.New("Row"); var body = api.New("Body");
        Set(body, "ReadOnly", new byte[80]); Set(body, "Module", new byte[5248]);
        Set(data, "Rows", api.Items("Row", row)); Set(row, "Simulation", body);
        var min = api.New("ValueNode"); var max = api.New("ValueNode");
        Set(min, "Gradient", new byte[168]); Set(max, "Gradient", new byte[168]);
        Set(data, "Values", api.Items("ValueNode", min, max));
        Set(body, "Gradients", new[] { 1, 2, 1 });
        api.Call("ValidateValues", data);
        CollectionAssert.Contains(api.Refresh(data), "multiply-owned-curve-or-gradient-allocation");
        Set(body, "Gradients", new[] { 2, 1 }); api.Reject("ValidateValues", data);
        Set(body, "Gradients", new[] { 1 }); api.Reject("ValidateValues", data);
        Set(body, "Curves", new[] { 1 }); Set(body, "Gradients", new[] { 2 }); api.Reject("ValidateValues", data);
    }
    [TestMethod]
    public void ValueGraphRequiresAllOptimizedCurveCoefficientsAndAllGradientSlots()
    {
        var api = new Api(); var data = api.New("Data"); var row = api.New("Row"); var body = api.New("Body");
        Set(body, "ReadOnly", new byte[80]); Set(body, "Module", new byte[5248]);
        var curve = api.Curve(); var gradient = api.New("ValueNode"); Set(gradient, "Gradient", new byte[168]);
        Set(data, "Values", api.Items("ValueNode", curve, gradient)); Set(data, "Rows", api.Items("Row", row));
        Set(row, "Simulation", body); Set(body, "Curves", new[] { 1 }); Set(body, "Gradients", new[] { 2 });
        api.Call("ValidateValues", data);
        Set(curve, "Optimized", new byte[32]); api.Reject("ValidateValues", data);
        Set(curve, "Optimized", new byte[40]); ((byte[])Get(gradient, "Gradient"))[161] = 9; api.Reject("ValidateValues", data);
        ((byte[])Get(gradient, "Gradient"))[161] = 8; ((byte[])Get(gradient, "Gradient"))[162] = 0x7e;
        CollectionAssert.Contains(api.Refresh(data), "curve-gradient-key-storage-and-padding-requires-common-canon");
    }
    [TestMethod]
    public void ManagerFenceAndEmitterHandleKeepOneSharedGroupOrdinalAndTheirVersions()
    {
        var api = new Api(); var data = api.BusyRowData(); var registry = Get(data, "Registry");
        var row = ((Array)Get(data, "Rows")).GetValue(0)!; var fence = api.New("Fence");
        Set(registry, "PendingJobs", true); Set(registry, "Fences", api.Items("Fence", fence));
        Set(fence, "Group", 1); Set(fence, "Version", 7U); Set(row, "HasJob", true); Set(row, "JobGroup", 1); Set(row, "JobVersion", 7U);
        api.Refresh(data); api.Call("Validate", data);
        Set(fence, "Group", 2); api.Reject("Validate", data);
        Set(fence, "Group", 1); Set(row, "JobGroup", 0); api.Reject("Validate", data);
    }
    [TestMethod]
    public void ProducerReceiptRequiresTheActualClientAndWorkerLayouts()
    {
        var api = new Api(); var data = api.BusyRowData(); var producer = Get(Get(data, "Registry"), "Producers");
        Set(producer, "HasWorker", true); api.Refresh(data); api.Reject("Validate", data);
        Set(producer, "MainVTable", 0x166bad8); Set(producer, "WorkerOwnsDevice", true); api.Refresh(data); api.Reject("Validate", data);
        Set(producer, "WorkerIsKnown", true); Set(producer, "Threaded", true); Set(producer, "Serialize", true);
        var gaps = api.Refresh(data); api.Call("Validate", data);
        CollectionAssert.Contains(gaps, "graphics-command-producer-or-pending-fence"); CollectionAssert.Contains(gaps, "managed-job-schedule-to-handle-publication");
        Set(producer, "Serialize", false); Set(producer, "WorkerOwnsDevice", false); Set(producer, "WorkerReachedClientFence", true);
        api.Refresh(data); api.Reject("RequireCompleteCoverage", data);
    }
    [TestMethod]
    public void JointManagedPublicationReceiptRemovesOnlyItsOwnCoverageGap()
    {
        var api = new Api(); var data = api.BusyRowData(); var p = Get(Get(data, "Registry"), "Producers");
        Set(p, "NativeMainThread", true); Set(p, "ManagedPublicationHeld", true);
        var gaps = api.Refresh(data); api.Call("Validate", data);
        CollectionAssert.DoesNotContain(gaps, "managed-job-schedule-to-handle-publication");
        api.Reject("RequireLivePublication", data); // Wire bit alone cannot admit this process.
        CollectionAssert.Contains(gaps, "manager-worker-and-render-producer-admission");
        CollectionAssert.Contains(gaps, "pending-emitter-update-job");
        Set(p, "NativeMainThread", false); api.Reject("Validate", data);
    }
    [TestMethod]
    public void NumericLanesKeepEveryAllocationSlotIncludingInactiveSimdTailBits()
    {
        var api = new Api(); var field = api.Field(2, 0); var array = api.New("ArrayState");
        Set(array, "Count", 1); Set(array, "Capacity", 4); Set(array, "Allocated", true); Set(array, "Allocation", 1); Set(array, "Bytes", new byte[4]);
        api.Reject("ValidateField", field, array);
        Set(array, "SpareNumeric", new byte[12]); api.Call("ValidateField", field, array);
        Set(array, "SpareNumeric", new byte[8]); api.Reject("ValidateField", field, array);
    }
    [TestMethod]
    public void BatchedForceAndCollisionEventNeverSerializeProcessPointersOrInstanceIds()
    {
        var api = new Api(); var array = api.New("ArrayState");
        Set(array, "Count", 1); Set(array, "Capacity", 1); Set(array, "Allocated", true); Set(array, "Allocation", 1);
        var first = api.New("ElementBinding"); Set(first, "Key", "body/actual-owner");
        Set(array, "Bytes", new byte[32]); Set(array, "Refs", api.Items("ElementBinding", first));
        var field = api.Field(1, 496); api.Call("ValidateField", field, array);
        ((byte[])Get(array, "Bytes"))[29] = 0xab; api.Reject("ValidateField", field, array);
        field = api.Field(2, 1344); Set(array, "Bytes", new byte[44]); api.Reject("ValidateField", field, array);
        Set(array, "Refs", api.Items("ElementBinding", first, api.New("ElementBinding"))); api.Call("ValidateField", field, array);
        ((byte[])Get(array, "Bytes"))[40] = 0x12; api.Reject("ValidateField", field, array);
    }
    [TestMethod]
    public void TriggerEventColliderMemberIsAnOrdinalOrBitmaskRatherThanAnInstanceId()
    {
        var api = new Api(); var field = api.Field(2, 1376); var array = api.New("ArrayState");
        Set(array, "Count", 1); Set(array, "Capacity", 1); Set(array, "Allocated", true); Set(array, "Allocation", 1);
        Set(array, "Bytes", new byte[] { 9, 0, 0, 0, 0x80, 0, 0, 0 }); api.Call("ValidateField", field, array);
        Set(array, "Refs", api.Items("ElementBinding", api.New("ElementBinding"))); api.Reject("ValidateField", field, array);
    }
    [TestMethod]
    public void EmitterSelfOnlyBindsTheNativeMeshNotificationDataField()
    {
        var api = new Api(); var body = api.New("Body"); var offsets = api.Offsets("ModuleLinks"); var links = offsets.Select(_ => -1).ToArray();
        Set(body, "ModuleLinkKinds", links); links[Array.IndexOf(offsets, 1064)] = -3; api.Call("ValidateCacheBindings", body);
        links[Array.IndexOf(offsets, 912)] = -3; api.Reject("ValidateCacheBindings", body);
    }
    [TestMethod]
    public void SharedMeshPinMustNameTheCurrentBoundMeshAndLightSentinelsMustBeReciprocal()
    {
        var api = new Api(); var body = api.New("Body"); var offsets = api.Offsets("ModuleLinks"); var links = offsets.Select(_ => -1).ToArray();
        Set(body, "ModuleLinkKinds", links); var cache = Get(body, "Caches");
        ((string[])Get(cache, "Objects"))[0] = "mesh/1"; Set(cache, "SharedMesh", "mesh/1"); Set(cache, "MeshRetains", 2);
        links[Array.IndexOf(offsets,856)] = -4; links[Array.IndexOf(offsets,912)] = -5; api.Call("ValidateCacheBindings", body);
        Set(cache, "SharedMesh", "mesh/2"); api.Reject("ValidateCacheBindings", body);
        Set(cache, "SharedMesh", "mesh/1"); links[Array.IndexOf(offsets,4760)] = 4760; api.Reject("ValidateCacheBindings", body);
        links[Array.IndexOf(offsets,4768)] = 4760; api.Call("ValidateCacheBindings", body);
    }
}
