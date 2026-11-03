using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    // A shared per-type step for ordinary Canon and restored Canon. Native
    // sheets are COW implementation storage; each occupied renderer slot is
    // rebuilt through Unity's own clear/copy path. Semantic properties are D;
    // sheet sharing, allocator capacity and hashes are canonical dependencies.
    // Renderer registration, materials, bounds and drawing state belong to the
    // enclosing renderer adapter, not to this property-block substep.
    internal static class ResidentRendererPropertyBlocks
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void MainBlock(IntPtr renderer, IntPtr sheet);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void IndexedBlock(IntPtr renderer, IntPtr sheet, int index);
        internal sealed class Data
        {
            public int Version = 1;
            public Row[] Rows = Array.Empty<Row>();
        }
        internal sealed class Row
        {
            public string Owner = "";
            public ResidentObjectDirectory.TypeRecord Type = new ResidentObjectDirectory.TypeRecord();
            public int MaterialCount;
            public bool Execution;
            public ResidentShaderPropertySheet.Data? Main;
            public ResidentShaderPropertySheet.Data?[] PerMaterial = Array.Empty<ResidentShaderPropertySheet.Data?>();
        }
        internal sealed class Plan
        {
            private readonly Data source, before;
            private readonly ResidentObjectDirectory directory;
            private readonly ResidentMutationScope scope;
            private readonly Func<string, UnityEngine.Object> resolve;
            private readonly Func<object, string?> bind;
            private readonly Action admission;
            private readonly Dictionary<string, Renderer> owners = new Dictionary<string, Renderer>(StringComparer.Ordinal);
            private bool used, applied;
            internal Plan(Data source, ResidentObjectDirectory directory, ResidentMutationScope scope,
                Func<string, UnityEngine.Object> resolve, Func<object, string?> bind, Action admitted)
            {
                Validate(source); this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
                this.scope = scope ?? throw new ArgumentNullException(nameof(scope)); this.resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
                this.bind = bind ?? throw new ArgumentNullException(nameof(bind)); admission = admitted ?? throw new ArgumentNullException(nameof(admitted));
                this.source = Copy(source); admission(); before = Capture(directory, bind);
                if (this.source.Rows.Length != before.Rows.Length) throw Error("source/target complete renderer directory differs");
                for (var index = 0; index < source.Rows.Length; index++) {
                    var row = this.source.Rows[index]; var target = before.Rows[index];
                    if (row.Owner != target.Owner || row.Type.Key != target.Type.Key || row.MaterialCount != target.MaterialCount || row.Execution != target.Execution)
                        throw Error("source/target renderer binding, type or material count differs");
                    if (row.Execution && Newtonsoft.Json.JsonConvert.SerializeObject(row) != Newtonsoft.Json.JsonConvert.SerializeObject(target))
                        throw Error("execution renderer properties must already match; restoration cannot overwrite them");
                    var renderer = directory.Resolve(row.Owner) as Renderer ?? throw Error("renderer owner cannot be resolved");
                    if (!row.Execution && !scope.Owns(renderer)) throw Error("renderer is outside the mutation scope"); owners.Add(row.Owner, renderer);
                    if (row.Main != null) ResidentShaderPropertySheet.RequireCompleteCoverage(row.Main);
                    foreach (var sheet in row.PerMaterial) if (sheet != null) ResidentShaderPropertySheet.RequireCompleteCoverage(sheet);
                }
            }
            internal void Apply()
            {
                if (used) throw Error("renderer property plan is single-use"); used = true; scope.Require(); admission();
                try {
                    RequireEqual(before, Capture(directory, bind));
                    var engine = LoadingEngineClockProbe.RequirePinnedEngine();
                    var main = (MainBlock)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine.BaseAddress, 0x65b0d0), typeof(MainBlock));
                    var indexed = (IndexedBlock)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine.BaseAddress, 0x65b120), typeof(IndexedBlock));
                    foreach (var row in source.Rows) {
                        if (row.Execution) continue;
                        scope.Require(); admission(); var renderer = owners[row.Owner]; var native = M.Owner(renderer);
                        // Clear first: CopyCustomPropertiesFrom skips copying
                        // when a hash matches, which would retain history-specific
                        // packing/shared storage instead of the common Canon.
                        main(native, IntPtr.Zero);
                        for (var index = 0; index < row.MaterialCount; index++) indexed(native, IntPtr.Zero, index);
                        if (row.Main != null) using (var sheet = ResidentShaderPropertySheet.Prepare(row.Main, resolve, bind, admission))
                            main(native, sheet.Pointer);
                        for (var index = 0; index < row.PerMaterial.Length; index++) {
                            var value = row.PerMaterial[index]; if (value == null) continue;
                            using var sheet = ResidentShaderPropertySheet.Prepare(value, resolve, bind, admission);
                            indexed(native, sheet.Pointer, index);
                        }
                    }
                    applied = true; Verify();
                } catch (Exception error) { scope.Fault(error); throw; }
            }
            internal void Verify()
            { if (!applied) throw Error("property-block readback precedes application"); scope.Require(); admission(); RequireEqual(source, Capture(directory, bind)); admission(); }
        }
        internal static Data Capture(ResidentObjectDirectory directory, Func<object, string?> bind)
        {
            if (directory == null || bind == null) throw new ArgumentNullException();
            LoadingEngineClockProbe.RequirePinnedEngine(); var rows = new List<Row>();
            foreach (var pair in directory.NativeBindings.Where(pair => pair.Value is Renderer).OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
                var renderer = (Renderer)pair.Value; var native = M.Owner(renderer); var bytes = M.Read(native, 448);
                var materials = Count(bytes, 336); var count = Count(bytes, 376); var capacity = M.U64(bytes, 384) >> 1;
                if (count > materials || (ulong)count > capacity || capacity > 4096) throw Error("renderer override directory exceeds material slots");
                var pointers = M.Read(M.Ptr(bytes, 360), checked(count * 8));
                var row = new Row { Owner = pair.Key, Type = ResidentObjectDirectory.TypeRecord.Capture(renderer.GetType()), MaterialCount = materials,
                    Execution = directory.Ownership.IsExecution(renderer),
                    Main = M.Ptr(bytes, 312) == IntPtr.Zero ? null : ResidentShaderPropertySheet.Capture(M.Ptr(bytes, 312), bind),
                    PerMaterial = Enumerable.Range(0, count).Select(index => M.Ptr(pointers, index * 8) == IntPtr.Zero ? null
                        : ResidentShaderPropertySheet.Capture(M.Ptr(pointers, index * 8), bind)).ToArray() };
                // An allocated all-null override array has no property slots.
                // Native ClearPerMaterialCustomProperties makes this size zero;
                // normalize its portable representation at every boundary too.
                if (row.PerMaterial.All(value => value == null)) row.PerMaterial = Array.Empty<ResidentShaderPropertySheet.Data?>();
                if (!M.Read(IntPtr.Add(native, 312), 80).SequenceEqual(M.Slice(bytes, 312, 80))
                    || !M.Read(M.Ptr(bytes, 360), pointers.Length).SequenceEqual(pointers)) throw Error("renderer property bindings changed during capture");
                rows.Add(row);
            }
            var result = new Data { Rows = rows.ToArray() }; Validate(result); return result;
        }
        internal static Plan Prepare(Data source, ResidentObjectDirectory directory, ResidentMutationScope scope,
            Func<string, UnityEngine.Object> resolve, Func<object, string?> bind, Action admitted) => new Plan(source, directory, scope, resolve, bind, admitted);
        internal static Plan PrepareCanon(ResidentObjectDirectory directory, ResidentMutationScope scope,
            Func<string, UnityEngine.Object> resolve, Func<object, string?> bind, Action admitted)
            => Prepare(Capture(directory, bind), directory, scope, resolve, bind, admitted);
        internal static void Validate(Data data)
        {
            if (data == null || data.Version != 1 || data.Rows == null || data.Rows.Length > ResidentObjectDirectory.MaximumComponents) throw Error("property-block snapshot shape differs");
            string? previous = null;
            foreach (var row in data.Rows) {
                if (row == null || string.IsNullOrEmpty(row.Owner) || row.Owner.Length > 16384 || previous != null && StringComparer.Ordinal.Compare(previous, row.Owner) >= 0
                    || row.Type == null || row.MaterialCount < 0 || row.MaterialCount > 4096 || row.PerMaterial == null || row.PerMaterial.Length > row.MaterialCount
                    || row.PerMaterial.Length != 0 && (row.PerMaterial.Length != row.MaterialCount || row.PerMaterial.All(value => value == null)))
                    throw Error("renderer property slots differ");
                row.Type.Validate(); previous = row.Owner;
                if (row.Main != null) ResidentShaderPropertySheet.Validate(row.Main);
                foreach (var value in row.PerMaterial) if (value != null) ResidentShaderPropertySheet.Validate(value);
            }
        }
        internal static void RequireEqual(Data expected, Data actual)
        {
            Validate(expected); Validate(actual);
            if (Newtonsoft.Json.JsonConvert.SerializeObject(expected) != Newtonsoft.Json.JsonConvert.SerializeObject(actual)) throw Error("renderer property-block readback differs");
        }
        private static Data Copy(Data data) => Newtonsoft.Json.JsonConvert.DeserializeObject<Data>(Newtonsoft.Json.JsonConvert.SerializeObject(data))
            ?? throw Error("renderer property snapshot copy is absent");
        private static int Count(byte[] bytes, int offset)
        { var value = M.U64(bytes, offset); return value <= 4096 ? (int)value : throw Error("renderer property count exceeds bounds"); }
        private static InvalidDataException Error(string reason) => new InvalidDataException("Resident renderer properties: " + reason);
    }
}
