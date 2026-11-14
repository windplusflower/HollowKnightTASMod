using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentParticleSystemState
    {
        internal static string[] RequiredCoverageGaps(Data data)
        {
            var gaps = new SortedSet<string>(StringComparer.Ordinal);
            if (data.Registry.Present) {
                if (data.Registry.CpuFence != 0) gaps.Add("manager-cpu-fence");
                if (data.Registry.PendingJobs) gaps.Add("manager-job-fence-stack");
                if (data.Registry.Active.Contains("") || data.Registry.Stopping.Contains("")) gaps.Add("manager-emitter-outside-directory");
                if (Foreign(data.Registry.ActiveStorage) || Foreign(data.Registry.StoppingStorage)) gaps.Add("manager-external-emitter-storage");
            }
            if (!data.Registry.Present && data.Rows.Length != 0) gaps.Add("manager-absent-with-live-emitters");
            foreach (var row in data.Rows) {
                if (row.Execution) gaps.Add("execution-owned-emitter");
                if (row.NeedsSync || row.HasJob) gaps.Add("pending-emitter-update-job");
                if (!row.ManagedBufferIsOwner) gaps.Add("managed-job-buffer-owner");
                var body = row.Simulation;
                if (body == null) continue;
                if (body.MissingRefs.Any(value => value)) gaps.Add("native-reference-outside-binding-closure");
                if (body.ParticleRetains != 1) gaps.Add("shared-particle-stream-retains");
                CacheGaps(body, gaps);
                if (body.BitSets.Any(bits => bits.Blocks != ((long)bits.Count + 31) / 32 || (bits.Allocation != 0) != (bits.Blocks != 0)))
                    gaps.Add("bitset-allocation-extent");
                for (var i = 0; i < body.Arrays.Length; i++) {
                    var array = body.Arrays[i];
                    if (Foreign(array)) gaps.Add("externally-owned-or-zero-capacity-array-storage");
                    if (array.Refs != null && array.Refs.Any(binding => binding.Missing)) gaps.Add("array-element-outside-binding-closure");

                }
            }
            var allocations = new HashSet<int>();
            if (OwnedAllocations(data).Any(id => id != 0 && !allocations.Add(id))) gaps.Add("multiply-owned-root-array-or-bitset-allocation");
            ValueGaps(data, gaps); ProducerGaps(data, gaps); return gaps.ToArray();
        }
        private static bool Foreign(ArrayState value) => value.Allocated && (value.External || value.Capacity == 0);
        internal static void Validate(Data data)
        {
            if (data == null || data.Version != 4 || data.Registry == null || data.Rows == null || data.Rows.Length > 100000 || data.CoverageGaps == null || data.Values == null || data.Values.Length > 100000) throw Error("snapshot shape differs");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in data.Rows) {
                if (row == null || !Key(row.Owner) || !keys.Add(row.Owner) || row.Type == null || row.Type.Name != "UnityEngine.ParticleSystem"
                    || row.Type.Assembly != "UnityEngine.ParticleSystemModule" || row.RayBudget == null || row.RayBudget.Length != 16
                    || row.ManagedJobRange == null || row.ManagedJobRange.Length != 12 || row.ActiveIndex < -1) throw Error("owner metadata differs");
                row.Type.Validate();
                if ((row.Simulation != null) != (data.Registry.Present && !Busy(data.Registry) && !Busy(row))) throw Error("simulation witness is missing or acquired without job admission");
                if (row.Simulation != null) ValidateBody(row.Simulation);
            }
            if (!data.Rows.Select(row => row.Owner).SequenceEqual(keys.OrderBy(value => value, StringComparer.Ordinal))) throw Error("owner order differs");
            var registry = data.Registry;
            ValidateArray(registry.ActiveStorage, -1); ValidateArray(registry.StoppingStorage, -1);
            if (registry.Active == null || registry.Stopping == null || registry.Active.Length != registry.ActiveStorage.Count
                || registry.Stopping.Length != registry.StoppingStorage.Count) throw Error("manager emitter storage shape differs");
            if (!registry.Present && (Busy(registry) || registry.StackVersion != 0)
                || (!registry.Present || Busy(registry)) && (registry.ActiveStorage.Allocated || registry.StoppingStorage.Allocated
                    || registry.Active.Length + registry.Stopping.Length != 0)) throw Error("manager lifetime or scheduling witness differs");
            if (registry.Present && !Busy(registry)) {
                var position = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var i = 0; i < registry.Active.Length; i++) {
                    var key = registry.Active[i]; if (key == "") continue;
                    if (!Key(key) || !keys.Contains(key) || position.ContainsKey(key)) throw Error("active manager registry has an unknown or duplicate emitter");
                    position.Add(key, i);
                }
                var stopped = new HashSet<string>(StringComparer.Ordinal);
                foreach (var key in registry.Stopping)
                    if (key != "" && (!Key(key) || !keys.Contains(key) || position.ContainsKey(key) || !stopped.Add(key))) throw Error("inactive stop-action registry overlaps or repeats");
                foreach (var row in data.Rows) if (row.ActiveIndex != (position.TryGetValue(row.Owner, out var index) ? index : -1))
                    throw Error("active emitter index is not reciprocal");
            }
            var allocations = new HashSet<int>();
            foreach (var id in OwnedAllocations(data)) if (id < 0 || id != 0 && allocations.Add(id) && id != allocations.Count)
                throw Error("allocation aliases are not in first-use order");
            ValidateProducers(data); ValidateValues(data);
            if (!data.CoverageGaps.SequenceEqual(RequiredCoverageGaps(data))) throw Error("coverage declaration differs from actual retained domains");
        }
        private static bool Key(string value) => !string.IsNullOrEmpty(value) && value.Length <= 262144;
        private static void ValidateArray(ArrayState value, int stride)
        {
            if (value == null || value.Count < 0 || value.Capacity < value.Count || value.Capacity > MaximumBytes
                || value.Allocated != (value.Allocation > 0) || value.Capacity != 0 && !value.Allocated || value.Bytes == null
                || stride > 0 && (long)value.Capacity * stride > MaximumBytes || stride < 0 && (value.Refs != null || value.SpareNumeric != null)
                || value.Bytes.LongLength != (stride > 0 ? (long)value.Count * stride : 0L)) throw Error("array extent differs");
        }
        private static void ValidateBody(Body body)
        {
            void Size(Array value, int count) { if (value == null || value.Length != count) throw Error("simulation field width differs"); }
            Size(body.RootAllocations, 4); if (body.RootAllocations.Any(id => id <= 0)) throw Error("live emitter lacks one of its native subobjects");
            Size(body.ReadOnly, PackedLength(ReadOnlyRanges)); Size(body.State, PackedLength(StateRanges)); Size(body.ParticleFlags, PackedLength(ParticleRanges));
            Size(body.Module, PackedLength(ModuleScalarRanges)); Size(body.Labels, ModuleLabels.Length); Size(body.ModuleVTables, ModuleVTables.Length);
            Size(body.Curves, ModuleCurves.Length + 2); Size(body.Gradients, ModuleGradients.Length); Size(body.ModuleLinkKinds, ModuleLinks.Length);
            Size(body.ObjectRefs, ModulePPtrs.Length + 3); Size(body.MissingRefs, body.ObjectRefs.Length); Size(body.Arrays, StorageLayout.Fields.Length); Size(body.BitSets, BitOffsets.Length);
            if (body.ObjectRefs.Where((key, i) => key == null || key.Length > 262144 || body.MissingRefs[i] && key.Length != 0).Any() || body.ReadOnlyVTable <= 0 || body.ModuleVTables.Any(rva => rva <= 0)
                || body.ParticleRetains < 1) throw Error("simulation dependencies differ");
            ValidateCacheBindings(body);
            if (FlagLayout.Booleans.Any(index => body.Module[index] > 1)
                || Enumerable.Range(52, 6).Any(offset => body.ReadOnly[PackedOffset(offset, ReadOnlyRanges)] > 1)
                || new[] { 12, 13, 32, 33, 34, 35, 36, 37, 38, 39, 352 }.Any(offset => body.State[PackedOffset(offset, StateRanges)] > 1)
                || Enumerable.Range(16, 17).Any(index => body.ParticleFlags[index] > 1)) throw Error("typed particle boolean differs");
            for (var i = 0; i < body.Arrays.Length; i++) ValidateField(StorageLayout.Fields[i], body.Arrays[i]);
            foreach (var bits in body.BitSets) if (bits == null || bits.Count < 0 || bits.Blocks < 0 || bits.Blocks > MaximumBytes / 4
                || bits.Count > (long)bits.Blocks * 32 || bits.Blocks != 0 && bits.Allocation <= 0 || bits.Words == null || bits.Words.Length != bits.Blocks * 4) throw Error("bitset extent differs");
        }
        internal static void RequireCompleteCoverage(Data data)
        {
            Validate(data); RequireLivePublication(data);
            var gaps = RequiredCoverageGaps(data);
            if (gaps.Length != 0) throw Error("native coverage is incomplete: " + string.Join(", ", gaps));
        }
        internal static void RequireEqual(Data expected, Data actual)
        {
            Validate(expected); Validate(actual);
            if (JsonConvert.SerializeObject(expected) != JsonConvert.SerializeObject(actual)) throw Error("independent native readback differs");
        }
        internal static Action Canon(ResidentObjectDirectory directory, ResidentMutationScope scope, Func<object, string?> bind, Action admitted)
        {
            scope.Require(); admitted(); var before = Capture(directory, bind); RequireCanonicalizableCoverage(before);
            try {
                scope.Mutate(() => CanonValues(before, directory, admitted));
                var expected = NormalizeValues(before);
                Action verify = () => { scope.Require(); admitted(); RequireEqual(expected, Capture(directory, bind)); admitted(); };
                verify(); return verify;
            } catch (Exception error) { scope.Fault(error); throw; }
        }
        internal static Plan Prepare(Data source, ResidentObjectDirectory directory, ResidentMutationScope scope,
            Func<string, NativeObject> resolve, Func<object, string?> bind, Action admitted) => new Plan(source, directory, scope, resolve, bind, admitted);
        private const int BurstVTableRva = 0x15dfe70, BurstBaseVTableRva = 0x15d5ec8;
        internal sealed class Plan
        {
            private readonly Data source, before;
            private readonly ResidentObjectDirectory directory;
            private readonly ResidentMutationScope scope;
            private readonly Func<string, NativeObject> resolve;
            private readonly Func<object, string?> bind;
            private readonly Action admitted;
            private bool applied;
            internal Plan(Data source, ResidentObjectDirectory directory, ResidentMutationScope scope, Func<string, NativeObject> resolve, Func<object, string?> bind, Action admitted)
            {
                RequireCompleteCoverage(source);
                this.source = JsonConvert.DeserializeObject<Data>(JsonConvert.SerializeObject(source)) ?? throw Error("source is missing");
                this.directory = directory; this.scope = scope; this.resolve = resolve; this.bind = bind; this.admitted = admitted;
                scope.Require(); admitted(); before = Capture(directory, bind);
                if (RequiredCoverageGaps(before).Length != 0) {
                    Canon(directory, scope, bind, admitted);
                    before = Capture(directory, bind);
                }
                RequireCompleteCoverage(before);
                if (source.Registry.Present != before.Registry.Present || source.Rows.Length != before.Rows.Length)
                    throw Error("target manager lifetime or emitter count differs");
                Compatible(source.Registry.ActiveStorage, before.Registry.ActiveStorage); Compatible(source.Registry.StoppingStorage, before.Registry.StoppingStorage);
                for (var i = 0; i < source.Rows.Length; i++) {
                    var saved = source.Rows[i]; var target = before.Rows[i]; var owner = directory.Resolve(saved.Owner);
                    if (saved.Owner != target.Owner || saved.Type.Resolve() != owner.GetType() || !(owner is ParticleSystem system) || !scope.Owns(system)
                        || saved.Execution || target.Execution) throw Error("owner or mutation scope differs");
                    var body = saved.Simulation ?? throw Error("source simulation is absent");
                    var local = target.Simulation ?? throw Error("target simulation is absent");
                    if (body.ReadOnlyVTable != local.ReadOnlyVTable || !CompatibleModuleVTables(body, local) || body.DelayLabel != local.DelayLabel
                        || !body.Labels.SequenceEqual(local.Labels)) {
                        var mv = Enumerable.Range(0, body.ModuleVTables.Length).Where(k => body.ModuleVTables[k] != local.ModuleVTables[k]).Select(k => $"{k}:0x{body.ModuleVTables[k]:x}/0x{local.ModuleVTables[k]:x}").FirstOrDefault() ?? "ok";
                        var lb = Enumerable.Range(0, body.Labels.Length).Where(k => body.Labels[k] != local.Labels[k]).Select(k => $"{k}@{ModuleLabels[k]}:{body.Labels[k]}/{local.Labels[k]}").FirstOrDefault() ?? "ok";
                        throw Error($"module dependencies differ: owner={saved.Owner} roVt=0x{body.ReadOnlyVTable:x}/0x{local.ReadOnlyVTable:x} delayLabel={body.DelayLabel}/{local.DelayLabel} modVt={mv} label={lb}");
                    }
                    CheckCaches(body, resolve);
                    for (var j = 0; j < body.ObjectRefs.Length; j++) if (body.ObjectRefs[j].Length != 0) _ = ResidentNativeAssetReference.ResolveTyped(body.ObjectRefs[j],References.Types[j],resolve);
                    for (var j = 0; j < body.Arrays.Length; j++) {
                        Compatible(body.Arrays[j], local.Arrays[j]); CheckFieldBindings(body.Arrays[j], StorageLayout.Fields[j], resolve);
                    }
                    for (var j = 0; j < body.BitSets.Length; j++) if (body.BitSets[j].Label != local.BitSets[j].Label)
                        throw Error("target native bit allocation differs");
                }
            }
            private static void Compatible(ArrayState saved, ArrayState local)
            {
                if (saved.Label != local.Label || Foreign(saved) || Foreign(local)) throw Error("target native array allocator or ownership differs");
            }
            internal void Apply()
            {
                if (applied) throw Error("apply is single-use"); scope.Require(); admitted(); RequireEqual(before, Capture(directory, bind));
                try {
                    scope.Mutate(() => {
                        for (var i = 0; i < source.Rows.Length; i++) {
                            scope.Require(); admitted(); var row = source.Rows[i];
                            var pointer = M.Owner(directory.Resolve(row.Owner));
                            WriteOwner(row, before.Rows[i], pointer, resolve, admitted);
                            ApplyValues(source, row, before, before.Rows[i], pointer, admitted);
                        }
                        if (source.Registry.Present) {
                            var manager = M.Ptr(IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, 0x19b71f0));
                            WriteList(manager, source.Registry.Active, source.Registry.ActiveStorage); WriteList(IntPtr.Add(manager, 32), source.Registry.Stopping, source.Registry.StoppingStorage);
                            if (source.Registry.StackVersion != before.Registry.StackVersion)
                                M.Write(IntPtr.Add(manager, 136), BitConverter.GetBytes(source.Registry.StackVersion));
                        }
                    });
                    applied = true; Verify();
                } catch (Exception error) { scope.Fault(error); throw; }
            }
            private void WriteList(IntPtr address, string[] keys, ArrayState storage)
            {
                admitted(); PrepareStorage(address, storage, 8, 8, admitted); var bytes = new byte[checked(keys.Length * 8)];
                for (var i = 0; i < keys.Length; i++) Buffer.BlockCopy(BitConverter.GetBytes(M.Owner(directory.Resolve(keys[i])).ToInt64()), 0, bytes, i * 8, 8);
                M.Write(M.Ptr(address), bytes); M.Write(IntPtr.Add(address, 16), BitConverter.GetBytes((ulong)keys.Length));
            }
            internal void Verify()
            {
                if (!applied) throw Error("verification precedes apply");
                scope.Require(); admitted(); RequireEqual(source, Capture(directory, bind)); admitted();
            }
        }
        private static void WriteOwner(Row row, Row before, IntPtr owner, Func<string, NativeObject> resolve, Action admitted)
        {
            var body = row.Simulation ?? throw Error("simulation is absent");
            var local = before.Simulation ?? throw Error("target simulation is absent");
            var header = M.Read(owner, 152);
            var parts = new[] { M.Ptr(header, 64), M.Ptr(header, 72), M.Ptr(header, 56), M.Ptr(header, 80) };
            WritePacked(parts[0], ReadOnlyRanges, body.ReadOnly, local.ReadOnly); WritePacked(parts[1], StateRanges, body.State, local.State);
            WritePacked(parts[2], ParticleRanges, body.ParticleFlags, local.ParticleFlags); WritePacked(parts[3], ModuleScalarRanges, body.Module, local.Module);
            var engineBase = IntPtr.Zero;
            for (var i = 4; i <= 11; i++) {
                if (body.ModuleVTables[i] == local.ModuleVTables[i]) continue;
                if (engineBase == IntPtr.Zero) engineBase = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
                M.Write(IntPtr.Add(parts[3], ModuleVTables[i]), BitConverter.GetBytes(IntPtr.Add(engineBase, body.ModuleVTables[i]).ToInt64()));
            }
            if (!row.RayBudget.SequenceEqual(before.RayBudget)) M.Write(IntPtr.Add(owner, 88), row.RayBudget);
            if (row.ActiveIndex != before.ActiveIndex) M.Write(IntPtr.Add(owner, 104), BitConverter.GetBytes(row.ActiveIndex));
            if (row.JobVersion != before.JobVersion) M.Write(IntPtr.Add(owner, 120), BitConverter.GetBytes(row.JobVersion));
            if (!row.ManagedJobRange.SequenceEqual(before.ManagedJobRange)) M.Write(IntPtr.Add(owner, 136), row.ManagedJobRange);
            for (var i = 0; i < body.Arrays.Length; i++) {
                var field = StorageLayout.Fields[i]; var value = body.Arrays[i];
                if (SameArray(value, local.Arrays[i])) continue;
                var address = IntPtr.Add(parts[field.Part], field.Offset);
                PrepareStorage(address, value, field.Stride, field.Alignment, admitted);
                var pointer = M.Ptr(address);
                M.Write(pointer, BindField(field, value, resolve));
                if (value.SpareNumeric != null) M.Write(IntPtr.Add(pointer, value.Bytes.Length), value.SpareNumeric);
                M.Write(IntPtr.Add(address, 16), BitConverter.GetBytes((ulong)value.Count));
            }
            for (var i = 0; i < BitOffsets.Length; i++) {
                var address = IntPtr.Add(parts[2], BitOffsets[i]); var value = body.BitSets[i];
                if (value.Count != local.BitSets[i].Count || !value.Words.SequenceEqual(local.BitSets[i].Words)) ApplyBits(address, value, admitted);
            }
            for (var i = 0; i < body.ObjectRefs.Length; i++) {
                var key = body.ObjectRefs[i]; if (key == local.ObjectRefs[i]) continue;
                var address = i == 0 ? IntPtr.Add(parts[0], 76) : i <= 2 ? IntPtr.Add(parts[1], 392 + (i - 1) * 4) : IntPtr.Add(parts[3], ModulePPtrs[i - 3]);
                M.Write(address, BitConverter.GetBytes(ResidentNativeAssetReference.ResolveId(key,References.Types[i],resolve)));
            }
            ApplyCaches(body, local, owner, parts[3], resolve, admitted);
        }
        private static bool CompatibleModuleVTables(Body body, Body local)
        {
            if (body.ModuleVTables.Length != local.ModuleVTables.Length || body.ModuleVTables.Length != ModuleVTables.Length) return false;
            var sourceCount = BurstCount(body); var localCount = BurstCount(local);
            for (var i = 0; i < body.ModuleVTables.Length; i++) {
                if (body.ModuleVTables[i] == local.ModuleVTables[i]) continue;
                if (i < 4 || i > 11) return false;
                var slot = i - 4;
                if (!ValidBurstVTable(body.ModuleVTables[i], slot, sourceCount) || !ValidBurstVTable(local.ModuleVTables[i], slot, localCount))
                    return false;
            }
            return true;
        }
        private static bool ValidBurstVTable(int rva, int slot, int burstCount) =>
            rva == BurstVTableRva || (rva == BurstBaseVTableRva && slot >= burstCount);
        private static int BurstCount(Body body)
        {
            for (int i = 0, pos = 0; i < ModuleScalarRanges.Length; i += 2) {
                if (ModuleScalarRanges[i] == 1272) return pos + 4 <= body.Module.Length ? BitConverter.ToInt32(body.Module, pos) : 0;
                pos += ModuleScalarRanges[i + 1];
            }
            return 0;
        }
        private static void WritePacked(IntPtr pointer, int[] ranges, byte[] values, byte[] before)
        {
            for (int i = 0, position = 0; i < ranges.Length; i += 2) {
                var length = ranges[i + 1];
                for (var j = 0; j < length; j++) if (before[position + j] != values[position + j]) {
                    M.Write(IntPtr.Add(pointer, ranges[i]), M.Slice(values, position, length)); break;
                }
                position += length;
            }
        }
    }
}
