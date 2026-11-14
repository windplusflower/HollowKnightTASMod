using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    // PDB 0334be90-63dd-4644-bee1-fc8d56c840c2 age 1. ParticleSystem::SyncJobs
    // (1c1310) clears needsSync, completes the job and can run a time-dependent
    // update. Observation must never call it, Play, Simulate or a managed getter.
    internal static partial class ResidentParticleSystemState
    {
        internal sealed class Data
        {
            public int Version = 4;
            public ValueNode[] Values = Array.Empty<ValueNode>();
            public Row[] Rows = Array.Empty<Row>();
            public Manager Registry = new Manager();
            public string[] CoverageGaps = Array.Empty<string>();
        }
        internal sealed class Row
        {
            public string Owner = "";
            public ResidentObjectDirectory.TypeRecord Type = new ResidentObjectDirectory.TypeRecord();
            public bool Execution, NeedsSync, HasJob, ManagedBufferIsOwner;
            public uint JobVersion;
            public int ActiveIndex = -1, JobGroup;
            public byte[] RayBudget = Array.Empty<byte>(), ManagedJobRange = Array.Empty<byte>();
            public Body? Simulation;
        }
        internal sealed class Body
        {
            public byte[] ReadOnly = Array.Empty<byte>(), State = Array.Empty<byte>(), ParticleFlags = Array.Empty<byte>(), Module = Array.Empty<byte>();
            public int ParticleRetains, ReadOnlyVTable, DelayLabel;
            public CacheBindings Caches = new CacheBindings();
            public int[] RootAllocations = Array.Empty<int>();
            public int[] ModuleVTables = Array.Empty<int>(), Labels = Array.Empty<int>(), ModuleLinkKinds = Array.Empty<int>();
            public int[] Curves = Array.Empty<int>(), Gradients = Array.Empty<int>();
            public string[] ObjectRefs = Array.Empty<string>();
            public bool[] MissingRefs = Array.Empty<bool>();
            public ArrayState[] Arrays = Array.Empty<ArrayState>();
            public Bits[] BitSets = Array.Empty<Bits>();
        }
        internal sealed class Bits
        {
            public int Label, Count, Blocks, Allocation;
            public byte[] Words = Array.Empty<byte>();
        }
        internal sealed class ArrayState
        {
            public int Label, Count, Capacity, Allocation;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public byte[]? SpareNumeric;
            public bool External, Allocated;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public ElementBinding[]? Refs;
            public byte[] Bytes = Array.Empty<byte>();
        }
        internal sealed class Manager
        {
            public bool Present, PendingJobs;
            public Producers Producers = new Producers();
            public Fence[] Fences = Array.Empty<Fence>();
            public uint CpuFence;
            public ulong StackVersion;
            public ArrayState ActiveStorage = new ArrayState(), StoppingStorage = new ArrayState();
            public string[] Active = Array.Empty<string>(), Stopping = Array.Empty<string>();
        }
        private sealed class ArrayField
        {
            internal readonly int Part, Offset, Stride, Alignment;
            internal readonly ElementRole Role;
            internal ArrayField(int part, int offset, int stride, int alignment = 4, ElementRole role = ElementRole.Plain)
            { Part = part; Offset = offset; Stride = stride; Alignment = alignment; Role = role; }
        }
        private const int MaximumBytes = 32 * 1024 * 1024;
        private static readonly int[] ReadOnlyRanges = { 12, 12, 40, 18, 60, 16, 80, 12 };
        private static readonly int[] StateRanges = { 0, 14, 16, 337, 356, 36, 400, 40, 472, 24 };
        private static readonly int[] ParticleRanges = { 1664, 16, 1968, 17, 1988, 4 };
        private static readonly int[] BitOffsets = { 1312, 1504 };
        private static class StorageLayout { internal static readonly ArrayField[] Fields = ArrayFields(); }
        // Corresponding source offsets: readOnly+76, state+392/396, then ModulePPtrs.
        private static class References { internal static readonly Type[] Types = { typeof(Transform), typeof(Rigidbody2D), typeof(Rigidbody), typeof(Mesh), typeof(MeshRenderer),
            typeof(SkinnedMeshRenderer), typeof(Sprite), typeof(SpriteRenderer), typeof(Texture2D), typeof(Light), typeof(Light) }; }
        private static ArrayField[] ArrayFields()
        {
            var result = new List<ArrayField> { new ArrayField(1, 440, 32, 8), new ArrayField(1, 496, 32, 8, ElementRole.Force) };
            for (var offset = 0; offset < 1312; offset += 32) result.Add(new ArrayField(2, offset, 4, 16));
            result.Add(new ArrayField(2, 1344, 44, 4, ElementRole.Collision));
            // TriggerEvents::GetEvents@1b7dee walks set bits with bsf. These
            // values are indices/bitmasks into TriggerModule's collider order.
            for (var offset = 1376; offset < 1504; offset += 32) result.Add(new ArrayField(2, offset, 8));
            result.Add(new ArrayField(2, 1536, 16, 16));
            for (var offset = 1568; offset < 1664; offset += 32) result.Add(new ArrayField(2, offset, 8, 8));
            for (var offset = 1680; offset <= 1936; offset += 32) result.Add(new ArrayField(2, offset, 4, 16));
            result.AddRange(new[] { new ArrayField(3, 920, 20), new ArrayField(3, 952, 8), new ArrayField(3, 984, 4), new ArrayField(3, 1016, 4),
                new ArrayField(3, 2256, 4, 4, ElementRole.Sprite), new ArrayField(3, 2288, 32), new ArrayField(3, 3000, 4, 4, ElementRole.Field),
                new ArrayField(3, 4168, 4, 4, ElementRole.Plane), new ArrayField(3, 4560, 4, 4, ElementRole.Collider), new ArrayField(3, 4608, 16, 4, ElementRole.Emitter) });
            if (!result.Where(field => field.Part == 3).Select(field => field.Offset).SequenceEqual(ModuleArrays)) throw Error("module array directory differs from pinned layout");
            return result.ToArray();
        }
        private sealed partial class Reader
        {
            private readonly Func<object, string?> bind;
            private readonly List<KeyValuePair<IntPtr, byte[]>> checks = new List<KeyValuePair<IntPtr, byte[]>>();
            private long bytes;
            internal Reader(Func<object, string?> bind) { this.bind = bind; }
            internal byte[] Read(IntPtr at, int size)
            {
                if (size == 0) return Array.Empty<byte>();
                bytes += size;
                if (bytes > MaximumBytes) throw Error("aggregate native observation exceeds the packet bound");
                var value = M.Read(at, size);
                checks.Add(new KeyValuePair<IntPtr, byte[]>(at, value));
                return value;
            }
            internal void Verify() {
                foreach (var check in idChecks) check();
                foreach (var pair in checks) if (!M.Read(pair.Key, pair.Value.Length).SequenceEqual(pair.Value)) throw Error("native state changed during capture");
            }
            internal string Reference(Type type, int id, out bool missing)
            {
                missing = false;
                if (id == 0) return "";
                var value = Registered(id);
                if (value == IntPtr.Zero || !ResidentNativeAssetReference.IsFamily(value,type)) { missing = true; return ""; }
                return ResidentNativeAssetReference.BindPointer(value,type,bind);
            }
        }
        internal static Data Capture(ResidentObjectDirectory directory, Func<object, string?> bind)
        {
            if (directory == null || bind == null) throw new ArgumentNullException();
            using var engineValidation = LoadingEngineClockProbe.ReadOnlyCheckpointScope();
            var engine = LoadingEngineClockProbe.RequirePinnedEngine();
            var reader = new Reader(bind);
            var owners = directory.NativeBindings.Where(pair => pair.Value.GetType() == typeof(ParticleSystem))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
            var byPointer = owners.ToDictionary(pair => M.Owner(pair.Value), pair => pair.Key);
            var managerSlot = IntPtr.Add(engine.BaseAddress, 0x19b71f0);
            var manager = M.Ptr(reader.Read(managerSlot, 8), 0);
            var result = new Data();
            if (manager != IntPtr.Zero) {
                // The manager Rand at 192 is separately transferred by ClockPayload.
                var bytes = reader.Read(manager, 144);
                var registry = result.Registry;
                registry.Present = true; registry.CpuFence = M.U32(bytes, 64);
                registry.PendingJobs = M.Ptr(bytes, 128) != IntPtr.Zero;
                registry.Producers = CaptureProducers();
                registry.Fences = CaptureFences(reader, M.Ptr(bytes, 128));
                registry.StackVersion = M.U64(bytes, 136);
                if (!Busy(registry)) {
                    registry.Active = RegistryEntries(reader, bytes, 0, byPointer, out registry.ActiveStorage);
                    registry.Stopping = RegistryEntries(reader, bytes, 32, byPointer, out registry.StoppingStorage);
                }
            }
            result.Rows = owners.Select(pair => {
                var pointer = M.Owner(pair.Value);
                var bytes = reader.Read(pointer, 152);
                var row = new Row { Owner = pair.Key, Type = ResidentObjectDirectory.TypeRecord.Capture(pair.Value.GetType()), Execution = directory.Ownership.IsExecution((ParticleSystem)pair.Value),
                    ActiveIndex = M.I32(bytes, 104), RayBudget = M.Slice(bytes, 88, 16), NeedsSync = Boolean(bytes, 108), HasJob = M.Ptr(bytes, 112) != IntPtr.Zero,
                    JobGroup = reader.Group(M.Ptr(bytes, 112)), JobVersion = M.U32(bytes, 120), ManagedBufferIsOwner = M.Ptr(bytes, 128) == pointer, ManagedJobRange = M.Slice(bytes, 136, 12) };
                if (result.Registry.Present && !Busy(result.Registry) && !Busy(row))
                    row.Simulation = CaptureBody(reader, engine, pointer, bytes);
                return row;
            }).ToArray();
            result.Values = reader.Values.ToArray();
            result.CoverageGaps = RequiredCoverageGaps(result);
            Validate(result); reader.Verify(); return result;
        }
        private static bool Busy(Manager row) => row.PendingJobs || row.CpuFence != 0;
        private static bool Busy(Row row) => row.NeedsSync || row.HasJob || !row.ManagedBufferIsOwner;
        private static bool Boolean(byte[] bytes, int offset) => bytes[offset] <= 1 ? bytes[offset] == 1 : throw Error("native boolean differs");
        private static string[] RegistryEntries(Reader reader, byte[] bytes, int offset, Dictionary<IntPtr, string> bindings, out ArrayState state)
        {
            state = CaptureArray(reader, bytes, offset);
            var values = reader.Read(M.Ptr(bytes, offset), checked(state.Count * 8));
            var order = new string[state.Count];
            for (var index = 0; index < order.Length; index++)
                order[index] = bindings.TryGetValue(M.Ptr(values, index * 8), out var key) ? key : "";
            return order;
        }
        private static Body CaptureBody(Reader reader, ProcessModule engine, IntPtr emitter, byte[] header)
        {
            var ro = reader.Read(M.Ptr(header, 64), 96);
            var state = reader.Read(M.Ptr(header, 72), 528);
            var particles = reader.Read(M.Ptr(header, 56), 2000);
            var modulesAt = M.Ptr(header, 80);
            var modules = reader.Read(modulesAt, 5248);
            var parts = new[] { ro, state, particles, modules };
            int Rva(IntPtr pointer) {
                long value = pointer.ToInt64() - engine.BaseAddress.ToInt64();
                return value >= 0 && value < engine.ModuleMemorySize ? (int)value : throw Error("vtable is not in the pinned engine");
            }
            foreach (var offset in ModuleBooleans) Boolean(modules, offset);
            var refs = new[] { M.I32(ro, 76), M.I32(state, 392), M.I32(state, 396) }.Concat(ModulePPtrs.Select(offset => M.I32(modules, offset))).ToArray();
            var objectRefs = new string[refs.Length]; var missing = new bool[refs.Length];
            for (var i = 0; i < refs.Length; i++) objectRefs[i] = reader.Reference(References.Types[i], refs[i], out missing[i]);
            var cacheBindings = CaptureCacheBindings(reader, emitter, modulesAt, modules, out var links);
            return new Body {
                RootAllocations = new[] { 64, 72, 56, 80 }.Select(offset => reader.Allocation(M.Ptr(header, offset))).ToArray(),
                ReadOnly = Pack(ro, ReadOnlyRanges), State = Pack(state, StateRanges), ParticleFlags = Pack(particles, ParticleRanges), Module = Pack(modules, ModuleScalarRanges),
                ParticleRetains = M.I32(particles, 1992), ReadOnlyVTable = Rva(M.Ptr(ro, 0)), DelayLabel = M.I32(ro, 8),
                ModuleVTables = ModuleVTables.Select(offset => Rva(M.Ptr(modules, offset))).ToArray(),
                Labels = ModuleLabels.Select(offset => M.I32(modules, offset)).ToArray(),
                Curves = new[] { reader.Value(ro, 24, false), reader.Value(ro, 32, false) }.Concat(ModuleCurves.Select(offset => reader.Value(modules, offset, false))).ToArray(),
                Gradients = ModuleGradients.Select(offset => reader.Value(modules, offset, true)).ToArray(),
                ModuleLinkKinds = links, Caches = cacheBindings,
                ObjectRefs = objectRefs, MissingRefs = missing,
                Arrays = StorageLayout.Fields.Select(field => CaptureField(reader, parts[field.Part], field)).ToArray(),
                BitSets = BitOffsets.Select(offset => CaptureBits(reader, particles, offset)).ToArray()
            };
        }
        private static ArrayState CaptureArray(Reader reader, byte[] bytes, int offset)
        {
            var capacity = M.U64(bytes, offset + 24);
            var value = new ArrayState { Allocation = reader.Allocation(M.Ptr(bytes, offset)), Label = M.I32(bytes, offset + 8), Count = Bounded(M.U64(bytes, offset + 16)), Capacity = Bounded(capacity >> 1),
                External = (capacity & 1) != 0, Allocated = M.Ptr(bytes, offset) != IntPtr.Zero };
            if (value.Capacity < value.Count || value.Capacity != 0 && !value.Allocated) throw Error("array header exceeds allocation");
            return value;
        }
        private static Bits CaptureBits(Reader reader, byte[] body, int offset)
        {
            var value = new Bits { Allocation = reader.Allocation(M.Ptr(body, offset + 8)), Label = M.I32(body, offset), Count = Bounded(M.U64(body, offset + 16)), Blocks = Bounded(M.U64(body, offset + 24)) };
            // CodeView m_bits is unsigned long* (0x675), i.e. 4-byte Windows unsigned int.
            value.Words = reader.Read(M.Ptr(body, offset + 8), checked(value.Blocks * 4)); return value;
        }
        private static int Bounded(ulong value) => value <= MaximumBytes ? (int)value : throw Error("native allocation exceeds the packet bound");
        private static byte[] Pack(byte[] source, int[] ranges)
        {
            var bytes = new byte[PackedLength(ranges)];
            for (int index = 0, pos = 0; index < ranges.Length; index += 2) {
                Buffer.BlockCopy(source, ranges[index], bytes, pos, ranges[index + 1]); pos += ranges[index + 1];
            }
            return bytes;
        }
        private static int PackedLength(int[] ranges) { var result = 0; for (var i = 1; i < ranges.Length; i += 2) result += ranges[i]; return result; }
        private static InvalidDataException Error(string message) => new InvalidDataException("Resident particle system: " + message);
    }
}
