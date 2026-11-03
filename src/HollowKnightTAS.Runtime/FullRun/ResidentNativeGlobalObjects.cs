using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Complete registered Object membership, independently cross-checked using
    // IDToPointer and every actual native type's object set. GameManager-derived
    // global objects are identified by real RTTI ancestry, never object names.
    // PersistentManager, GameObjectManager, TypeManager, etc. do NOT derive from
    // Object and are deliberately outside this layout; typed adapters own them.
    // This directory proves identity/coverage, not the managers' fields beyond
    // Object48. A body adapter must still cover each reported GlobalManager type.
    internal static class ResidentNativeGlobalObjects
    {
        private const int Maximum = ResidentObjectDirectory.MaximumComponents, MaximumTypes = 1024;
        private const int ObjectRtti = 0x194b230, ManagerRtti = 0x194d180;
        private const string GlobalPrefix = "resident-native-global/";
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool IsMainThread();

        internal sealed class Data
        {
            public int Version = 1, RegisteredCount;
            public NativeClass[] Classes = Array.Empty<NativeClass>();
            public Row[] Rows = Array.Empty<Row>();
            public string[] CoverageGaps = Array.Empty<string>();
            internal Data Copy() => new Data { Version = Version, RegisteredCount = RegisteredCount,
                Classes = Classes.Select(value => value.Copy()).ToArray(), Rows = Rows.Select(value => value.Copy()).ToArray(), CoverageGaps = (string[])CoverageGaps.Clone() };
        }
        internal sealed class NativeClass
        {
            public int Rva, BaseRva, FactoryRva, PersistentTypeId, Size, Index, DescendantCount;
            public string Name = "", Namespace = "", Module = "";
            public bool Abstract, Sealed, EditorOnly, Stripped, ObjectDerived, GlobalManager;
            internal string Key => Rva.ToString("x", CultureInfo.InvariantCulture) + "/" + Module + "/" + Namespace + "/" + Name;
            internal NativeClass Copy() => (NativeClass)MemberwiseClone();
        }
        internal sealed class Row
        {
            public string Binding = "", ManagedType = "", Kind = "";
            public int TypeRva, Vtable;
            public bool Persistent, ManagedPresent;
            internal Row Copy() => (Row)MemberwiseClone();
        }
        internal sealed class CaptureResult
        {
            private readonly Observation observation;
            private readonly Dictionary<int,ResidentScriptingHandles.Capture> handles;
            private readonly Dictionary<int, IntPtr> registeredPointers;
            internal readonly IReadOnlyDictionary<int, IntPtr> RegisteredPointers;
            // Local join keys only. The instance IDs and pointers are never
            // serialized; the values are the exact rows in Snapshot.Rows.
            internal readonly IReadOnlyDictionary<int, Row> RegisteredRows;
            private readonly Dictionary<NativeObject, string> globalBindings;
            private readonly Dictionary<string, object> globalManaged;
            internal readonly Data Snapshot;
            internal readonly IReadOnlyCollection<ResidentObjectNativeState.GlobalOwner> GlobalOwners;
            internal readonly IReadOnlyDictionary<string, object> ManagedRoots;
            // Includes every actual singleton manager, even if a caller had
            // already supplied its native pointer through another binding.
            internal readonly IReadOnlyDictionary<string, IntPtr> ManagerPointers;
            internal CaptureResult(Observation observation, Data snapshot, ResidentObjectNativeState.GlobalOwner[] globals,
                Dictionary<NativeObject, string> bindings, Dictionary<string, object> managed, Dictionary<string, IntPtr> managers,
                Dictionary<int,ResidentScriptingHandles.Capture> handles, Dictionary<int, IntPtr> registeredPointers, Dictionary<int, Row> registeredRows)
            {
                this.observation = observation; this.handles = handles; this.registeredPointers = registeredPointers;
                RegisteredPointers = new ReadOnlyDictionary<int, IntPtr>(registeredPointers);
                RegisteredRows = new ReadOnlyDictionary<int, Row>(registeredRows);
                Snapshot = snapshot; GlobalOwners = Array.AsReadOnly(globals);
                globalBindings = bindings; globalManaged = managed; ManagedRoots = new ReadOnlyDictionary<string, object>(managed);
                ManagerPointers = new ReadOnlyDictionary<string, IntPtr>(managers);
            }
            internal string? BindExternal(object value) => value is NativeObject native && globalBindings.TryGetValue(native, out var key) ? key : null;
            internal object? ResolveExternal(string key) => globalManaged.TryGetValue(key, out var value) ? value : null;
            // Resolve a PPtr only through membership already witnessed above.
            // A missing wrapper remains absent; this never loads an asset or
            // asks Unity to manufacture a managed wrapper during observation.
            internal NativeObject? BorrowRegisteredObject(int instanceId, Type expected)
            {
                if (instanceId == 0) return null;
                observation.Admitted();
                var result = ReadRegisteredWrapper(instanceId, expected);
                observation.Admitted(); return result;
            }
            internal NativeObject?[] BorrowRegisteredAssets(int[] ids)
            {
                observation.Admitted();
                var result = new NativeObject?[ids.Length];
                for (var i = 0; i < ids.Length; ++i) {
                    if ((i & 63) == 0) observation.Admitted();
                    result[i] = ReadRegisteredWrapper(ids[i], typeof(NativeObject));
                }
                observation.Admitted(); return result;
            }
            private NativeObject? ReadRegisteredWrapper(int instanceId, Type expected)
            {
                if (expected == null || !typeof(NativeObject).IsAssignableFrom(expected)
                    || !registeredPointers.TryGetValue(instanceId, out var pointer)) throw Error("PPtr is outside the observed native Object directory");
                if (!handles.TryGetValue(instanceId,out var handle)) {
                    handle = ResidentScriptingHandles.Read(IntPtr.Add(pointer,24)); handles.Add(instanceId,handle);
                }
                handle.RequireUnchanged();
                if (ReferenceEquals(handle.Target,null)) return null;
                if (!(handle.Target is NativeObject wrapper) || !expected.IsInstanceOfType(wrapper)
                    || !ResidentObjectDirectory.IsLive(wrapper) || M.Owner(wrapper) != pointer || wrapper.GetInstanceID() != instanceId)
                    throw Error("registered PPtr's existing managed wrapper has another owner or type");
                handle.RequireUnchanged(); return wrapper;
            }
            internal void RequireUnchanged() { observation.Frozen(); foreach (var handle in handles.Values) handle.RequireUnchanged(); observation.Admitted(); }
            internal void RequireDirectoryComplete() { RequireCompleteDirectory(Snapshot); RequireUnchanged(); }
        }
        internal sealed class HashRow
        {
            internal int InstanceId;
            internal IntPtr Pointer;
        }
        internal sealed class Observation
        {
            private readonly List<Tuple<IntPtr, byte[]>> reads = new List<Tuple<IntPtr, byte[]>>();
            internal readonly Action Admitted;
            internal readonly IntPtr Engine;
            internal readonly int EngineLength;
            private long totalBytes;
            internal Observation(Action admitted, IntPtr engine, int length) { Admitted = admitted; Engine = engine; EngineLength = length; }
            internal byte[] Read(IntPtr pointer, int length) {
                if ((reads.Count & 255) == 0) Admitted(); totalBytes = checked(totalBytes + length);
                if (totalBytes > 32 * 1024 * 1024) throw Error("native object observation exceeds its bounded memory directory");
                var value = M.Read(pointer, length); reads.Add(Tuple.Create(pointer, value)); return value;
            }
            internal int Rva(IntPtr pointer, int length = 1) {
                var offset = pointer.ToInt64() - Engine.ToInt64();
                if (offset <= 0 || offset > EngineLength - length) throw Error("native type or function lies outside the pinned engine");
                return (int)offset;
            }
            internal string CString(IntPtr pointer) {
                if (pointer == IntPtr.Zero) return "";
                var rva = Rva(pointer); var bytes = new List<byte>();
                while (bytes.Count < 1024) {
                    var take = Math.Min(128, Math.Min(1024 - bytes.Count, EngineLength - rva - bytes.Count));
                    if (take <= 0) throw Error("native class name has no bounded terminator");
                    var part = Read(IntPtr.Add(pointer, bytes.Count), take); var end = Array.IndexOf(part, (byte)0);
                    if (end >= 0) { bytes.AddRange(part.Take(end)); return new UTF8Encoding(false, true).GetString(bytes.ToArray()); }
                    bytes.AddRange(part);
                }
                throw Error("native class name exceeds bounds");
            }
            internal void Frozen() {
                Admitted(); var index = 0;
                var verify = new M.Readback();
                foreach (var pair in reads) {
                    if ((index++ & 255) == 0) Admitted();
                    if (!verify.Matches(pair.Item1, pair.Item2)) throw Error("native object/type registry changed during observation");
                }
                Admitted();
            }
        }
        private sealed class LocalRow
        {
            internal IntPtr Pointer;
            internal int Id, Vtable;
            internal uint Flags;
            internal NativeClass Class = null!;
        }

        internal static NativeClass[] ReadClasses(Observation observation)
        {
            var runtime = observation.Read(IntPtr.Add(observation.Engine, 0x19d59f0), 8200); var count = M.U32(runtime, 0);
            if (count == 0 || count > MaximumTypes) throw Error("native runtime type array is absent or oversized");
            var classes = new NativeClass[(int)count];
            for (var index = 0; index < classes.Length; index++) {
                var pointer = M.Ptr(runtime, 8 + index * 8); var rva = observation.Rva(pointer, 80); var bytes = observation.Read(pointer, 80);
                var parent = M.Ptr(bytes, 0); var factory = M.Ptr(bytes, 8);
                classes[index] = new NativeClass { Rva = rva, BaseRva = parent == IntPtr.Zero ? 0 : observation.Rva(parent, 80),
                    FactoryRva = factory == IntPtr.Zero ? 0 : observation.Rva(factory), Name = observation.CString(M.Ptr(bytes, 16)),
                    Namespace = observation.CString(M.Ptr(bytes, 24)), Module = observation.CString(M.Ptr(bytes, 32)),
                    PersistentTypeId = M.I32(bytes, 40), Size = M.I32(bytes, 44), Index = M.I32(bytes, 48), DescendantCount = M.I32(bytes, 52),
                    Abstract = Bool(bytes[56]), Sealed = Bool(bytes[57]), EditorOnly = Bool(bytes[58]), Stripped = Bool(bytes[59]) };
            }
            Classify(classes); ValidateClasses(classes);
            return classes;
        }
        // Research evidence only: collect the complete native type histogram
        // even if another domain subsequently rejects this boundary. Reading
        // the two table headers creates no Unity wrappers. Capture() below
        // still independently verifies every member, binding and virtual RTTI.
        internal static void RecordProbeTypeCounts(Action admitted, bool rejectedBootstrap = false)
        {
            var output=Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT");
            if (string.IsNullOrEmpty(output) || !Path.IsPathRooted(output) || !Directory.Exists(output)) return;
            using var host=System.Diagnostics.Process.GetCurrentProcess();
            var stage=rejectedBootstrap ? "rejected-before-movie-zero" : "source-unloaded";
            var path=Path.Combine(output,"resident-native-type-counts-"+stage+"-"+host.Id+".json");
            if (File.Exists(path)) return;
            object result;
            try {
                admitted(); var engine=LoadingEngineClockProbe.RequirePinnedEngine(); RequireMainThread(engine.BaseAddress);
                var view=new Observation(admitted,engine.BaseAddress,engine.ModuleMemorySize);
                var types=ReadClasses(view);
                var ids=M.Ptr(view.Read(IntPtr.Add(view.Engine,0x19d7be8),8),0);
                var sets=M.Ptr(view.Read(IntPtr.Add(view.Engine,0x19d7bf0),8),0);
                var idHeader=view.Read(ids,32); _=HashByteLength(idHeader,true);
                var headers=view.Read(sets,types.Length*32);
                var rows=types.Select(t => {
                    var header=M.Slice(headers,t.Index*32,32); _=HashByteLength(header,false);
                    return new { t.Name,t.Namespace,t.Module,t.PersistentTypeId,t.Size,t.GlobalManager,t.Rva, live=M.U32(header,12) };
                }).ToArray();
                var total=M.U32(idHeader,12); if (rows.Sum(x=>(long)x.live)!=total) throw Error("diagnostic registry totals differ");
                view.Frozen();
                result=new { stage, verifiedTableCounts=true, completeStateCoverage=false, total, types=rows };
            } catch (Exception error) { result=new { stage, verifiedTableCounts=false, completeStateCoverage=false, error=error.ToString() }; }
            try {
                using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
                using var writer=new StreamWriter(file,new UTF8Encoding(false)); writer.Write(Newtonsoft.Json.JsonConvert.SerializeObject(result));
            } catch (IOException) { /* Evidence is optional; the actual directory never uses this file. */ }
            admitted();
        }
        internal static CaptureResult Capture(ResidentObjectDirectory directory, IReadOnlyDictionary<string, NativeObject> assets,
            Func<object, string?> bindAsset, Action admitted)
        {
            if (directory == null || assets == null || bindAsset == null || admitted == null) throw new ArgumentNullException();
            admitted(); var engine = LoadingEngineClockProbe.RequirePinnedEngine();
            RequireMainThread(engine.BaseAddress);
            var observation = new Observation(admitted, engine.BaseAddress, engine.ModuleMemorySize);
            var classes = ReadClasses(observation);
            var idMapPointer = M.Ptr(observation.Read(IntPtr.Add(engine.BaseAddress, 0x19d7be8), 8), 0);
            var typeSetsPointer = M.Ptr(observation.Read(IntPtr.Add(engine.BaseAddress, 0x19d7bf0), 8), 0);
            if (idMapPointer == IntPtr.Zero || typeSetsPointer == IntPtr.Zero) throw Error("native Object registration tables are absent");
            var idHeader = observation.Read(idMapPointer, 32); var idBytes = observation.Read(M.Ptr(idHeader, 0), HashByteLength(idHeader, true));
            var ids = DecodeHashRows(idHeader, idBytes, true);
            var typeHeaders = observation.Read(typeSetsPointer, classes.Length * 32);
            var pointers = new Dictionary<IntPtr, LocalRow>(); var getters = new Dictionary<int, int>();
            foreach (var item in ids) {
                var header = observation.Read(item.Pointer, 16); var index = (int)(M.U32(header, 12) >> 21);
                if (M.I32(header, 8) != item.InstanceId || index < 0 || index >= classes.Length || !classes[index].ObjectDerived)
                    throw Error("registered native Object header/type does not match its ID table");
                var vtablePointer = M.Ptr(header, 0); var vtable = observation.Rva(vtablePointer, 72);
                if (!getters.TryGetValue(vtable, out var actualRtti)) {
                    var getter = M.Ptr(observation.Read(IntPtr.Add(vtablePointer, 64), 8), 0); observation.Rva(getter, 8);
                    var code = observation.Read(getter, 8);
                    // All 206 GetTypeVirtualInternal procedures in this pinned
                    // PDB are exactly this lea-static-RTTI/ret body. Decode it
                    // instead of invoking arbitrary object virtual functions.
                    if (code[0] != 0x48 || code[1] != 0x8d || code[2] != 0x05 || code[7] != 0xc3)
                        throw Error("native Object actual-class getter differs from the pinned ABI");
                    actualRtti = observation.Rva(new IntPtr(checked(getter.ToInt64() + 7 + M.I32(code, 3))), 80); getters.Add(vtable, actualRtti);
                }
                if (actualRtti != classes[index].Rva || classes[index].Abstract || classes[index].Stripped || classes[index].Size < 48)
                    throw Error("native Object cached type differs from its actual virtual RTTI");
                pointers.Add(item.Pointer, new LocalRow { Id = item.InstanceId, Pointer = item.Pointer, Class = classes[index], Vtable = vtable, Flags = M.U32(header, 12) });
            }
            var typed = new HashSet<IntPtr>();
            for (var index = 0; index < classes.Length; index++) {
                var header = M.Slice(typeHeaders, index * 32, 32); var bytes = observation.Read(M.Ptr(header, 0), HashByteLength(header, false));
                foreach (var item in DecodeHashRows(header, bytes, false)) {
                    if (!typed.Add(item.Pointer) || !pointers.TryGetValue(item.Pointer, out var owner) || owner.Class.Index != index)
                        throw Error("native type sets and ID table disagree on Object membership");
                }
            }
            if (typed.Count != pointers.Count) throw Error("native type sets omit a registered Object");

            var known = new Dictionary<IntPtr, Tuple<string, NativeObject, string>>();
            foreach (var pair in directory.NativeBindings) AddKnown(pair.Key, pair.Value, "directory");
            foreach (var pair in assets) AddKnown(pair.Key, pair.Value, "asset");
            void AddKnown(string key, NativeObject value, string kind) {
                var pointer = M.Owner(value);
                if (!pointers.ContainsKey(pointer) || known.ContainsKey(pointer)) throw Error("a supplied resident/asset binding is absent or repeated in native registration");
                known.Add(pointer, Tuple.Create(key, value, kind));
            }
            var gaps = new SortedSet<string>(StringComparer.Ordinal); var rows = new List<Row>();
            var globals = new List<ResidentObjectNativeState.GlobalOwner>(); var handles = new Dictionary<int,ResidentScriptingHandles.Capture>(pointers.Count);
            var managedTypes = new Dictionary<Type,string>();
            string TypeKey(NativeObject owner) {
                var type = owner.GetType(); if (!managedTypes.TryGetValue(type,out var key)) managedTypes.Add(type,key = ResidentObjectDirectory.TypeRecord.Capture(type).Key);
                return key;
            }
            var globalBindings = new Dictionary<NativeObject, string>(ResidentObjectDirectory.ReferenceComparer.Instance);
            var managed = new Dictionary<string, object>(StringComparer.Ordinal); var managerPointers = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            var managerCounts = pointers.Values.Where(value => value.Class.GlobalManager).GroupBy(value => value.Class.Rva).ToDictionary(group => group.Key, group => group.Count());
            var registeredRows = new Dictionary<int, Row>();
            foreach (var local in pointers.Values.OrderBy(value => value.Class.Rva).ThenBy(value => value.Id)) {
                var row = new Row { TypeRva = local.Class.Rva, Vtable = local.Vtable, Persistent = (local.Flags & (1U << 20)) != 0 };
                if (local.Class.GlobalManager && managerCounts[local.Class.Rva] == 1)
                    managerPointers.Add(GlobalPrefix + local.Class.Key, local.Pointer);
                if (known.TryGetValue(local.Pointer, out var binding)) {
                    row.Binding = binding.Item1; row.Kind = binding.Item3; row.ManagedPresent = true; row.ManagedType = TypeKey(binding.Item2);
                    if (local.Class.GlobalManager) {
                        gaps.Add("global-object/manager-already-bound-as-" + row.Kind + ":" + local.Class.Key);
                    }
                } else if (local.Class.GlobalManager && managerCounts[local.Class.Rva] == 1) {
                    row.Kind = "global"; row.Binding = GlobalPrefix + local.Class.Key;
                    NativeObject? managedOwner = null;
                    var handle = ResidentScriptingHandles.Read(IntPtr.Add(local.Pointer, 24)); handles.Add(local.Id,handle);
                    if (handle.Target is NativeObject wrapper && ResidentObjectDirectory.IsLive(wrapper) && M.Owner(wrapper) == local.Pointer) {
                        row.ManagedPresent = true; row.ManagedType = TypeKey(wrapper);
                        globalBindings.Add(wrapper, row.Binding); managed.Add(row.Binding, wrapper); managedOwner = wrapper;
                    } else if (handle.Target != null || handle.Allocated) gaps.Add("global-object/unresolved-global-managed-handle:" + local.Class.Key);
                    globals.Add(new ResidentObjectNativeState.GlobalOwner(row.Binding, local.Class.Key, local.Pointer, managedOwner));
                } else if (local.Class.GlobalManager) {
                    row.Kind = "unbound"; gaps.Add("global-object/multiple-manager-identities:" + local.Class.Key);
                } else {
                    var handle = ResidentScriptingHandles.Read(IntPtr.Add(local.Pointer, 24)); handles.Add(local.Id,handle);
                    // Borrow only a wrapper that already exists. Neither
                    // Resources.InstanceIDToObject nor FindObjectsOfTypeAll is
                    // used: those can allocate wrappers or lazy-load objects.
                    if (handle.Target is NativeObject wrapper && ResidentObjectDirectory.IsLive(wrapper) && M.Owner(wrapper) == local.Pointer) {
                        row.Binding = bindAsset(wrapper) ?? ""; row.ManagedPresent = true; row.ManagedType = TypeKey(wrapper);
                    }
                    row.Kind = row.Binding.Length == 0 ? "unbound" : "asset";
                    if (row.Binding.Length == 0) gaps.Add("global-object/unbound-native-owner:" + local.Class.Key);
                }
                rows.Add(row);
                registeredRows.Add(local.Id, row);
            }
            var data = new Data { RegisteredCount = pointers.Count, Classes = classes,
                Rows = rows.OrderBy(value => value.TypeRva).ThenBy(value => value.Binding, StringComparer.Ordinal).ToArray(), CoverageGaps = gaps.ToArray() };
            Validate(data); observation.Frozen();
            return new CaptureResult(observation, data, globals.ToArray(), globalBindings, managed, managerPointers, handles,
                pointers.Values.ToDictionary(value => value.Id, value => value.Pointer), registeredRows);
        }

        private static bool Bool(byte value) { if (value > 1) throw Error("native RTTI boolean differs"); return value != 0; }
        private static void RequireMainThread(IntPtr engine)
        {
            var address = IntPtr.Add(engine, 0x81c590);
            using (var sha = SHA256.Create()) if (BitConverter.ToString(sha.ComputeHash(M.Read(address, 30))).Replace("-", "").ToLowerInvariant()
                != "730b0ebf0c4d9a6bec6ff34df66477641fc299b8a008a553b195d5283b05556d") throw Error("native main-thread predicate ABI differs");
            if (!((IsMainThread)Marshal.GetDelegateForFunctionPointer(address, typeof(IsMainThread)))()) throw Error("Object registry capture requires the admitted native main thread");
        }
        internal static int HashByteLength(byte[] header, bool idMap)
        {
            if (header == null || header.Length != 32) throw Error("native hash header differs");
            var scale = idMap ? 8U : 16U; var stride = idMap ? 24U : 16U; var mask = M.U32(header, 8); var size = M.U32(header, 12);
            var units = (ulong)mask + scale; var capacity = units / scale;
            if (units % scale != 0 || capacity == 0 || (capacity & (capacity - 1)) != 0 || capacity > 524288 || size > Maximum || size > capacity)
                throw Error("native Object hash capacity/count differs");
            return checked((int)(capacity * stride));
        }
        internal static HashRow[] DecodeHashRows(byte[] header, byte[] nodes, bool idMap)
        {
            var length = HashByteLength(header, idMap); var stride = idMap ? 24 : 16;
            if (nodes == null || nodes.Length != length) throw Error("native Object hash storage length differs");
            var rows = new List<HashRow>(); var pointers = new HashSet<IntPtr>(); var ids = new HashSet<int>();
            for (var offset = 0; offset < nodes.Length; offset += stride) {
                if (M.U32(nodes, offset) >= 0xfffffffeU) continue;
                var pointer = M.Ptr(nodes, offset + (idMap ? 16 : 8)); var id = idMap ? M.I32(nodes, offset + 8) : 0;
                if (pointer == IntPtr.Zero || !pointers.Add(pointer) || idMap && (id == 0 || !ids.Add(id))) throw Error("native Object hash has repeated or absent live identities");
                rows.Add(new HashRow { InstanceId = id, Pointer = pointer });
            }
            if (rows.Count != M.U32(header, 12)) throw Error("native Object hash live count differs");
            return rows.ToArray();
        }
        private static void Classify(NativeClass[] classes)
        {
            if (classes == null || classes.Any(value => value == null) || classes.Select(value => value.Rva).Distinct().Count() != classes.Length)
                throw Error("native RTTI directory is absent or repeated");
            var byRva = classes.ToDictionary(value => value.Rva);
            foreach (var type in classes) {
                var visited = new HashSet<int>(); var cursor = type;
                while (true) {
                    if (!visited.Add(cursor.Rva) || visited.Count > 128) throw Error("native RTTI ancestry is cyclic or oversized");
                    if (cursor.Rva == ObjectRtti) type.ObjectDerived = true;
                    if (cursor.Rva == ManagerRtti) type.GlobalManager = true;
                    if (cursor.BaseRva == 0) break;
                    if (!byRva.TryGetValue(cursor.BaseRva, out cursor)) throw Error("native RTTI ancestry is outside its runtime directory");
                }
            }
        }
        private static void ValidateClasses(NativeClass[] classes)
        {
            if (classes == null || classes.Length == 0 || classes.Length > MaximumTypes || classes.Any(value => value == null)
                || classes.Select(value => value.Rva).Distinct().Count() != classes.Length) throw Error("native class directory differs");
            var expected = classes.Select(value => value.Copy()).ToArray();
            foreach (var type in expected) { type.ObjectDerived = false; type.GlobalManager = false; }
            Classify(expected);
            var byRva = classes.ToDictionary(value => value.Rva);
            for (var index = 0; index < classes.Length; index++) {
                var value = classes[index];
                if (value.Index != index || value.Rva <= 0 || value.BaseRva < 0 || value.FactoryRva < 0 || value.Size < 0 || value.Size > 32 * 1024 * 1024
                    || value.DescendantCount < 1 || value.DescendantCount > classes.Length - index
                    || value.Name == null || value.Name.Length == 0 || value.Name.Length > 1024 || value.Name.IndexOf('\0') >= 0
                    || value.Namespace == null || value.Namespace.Length > 1024 || value.Namespace.IndexOf('\0') >= 0
                    || value.Module == null || value.Module.Length > 1024 || value.Module.IndexOf('\0') >= 0
                    || value.ObjectDerived != expected[index].ObjectDerived || value.GlobalManager != expected[index].GlobalManager)
                    throw Error("native class type/index/ancestry schema differs");
                if (value.BaseRva != 0) {
                    var parent = byRva[value.BaseRva];
                    if (parent.Index >= index || index >= parent.Index + parent.DescendantCount) throw Error("native RTTI descendant interval differs from ancestry");
                }
            }
            var root = classes.SingleOrDefault(value => value.Rva == ObjectRtti); var manager = classes.SingleOrDefault(value => value.Rva == ManagerRtti);
            if (root == null || root.Name != "Object" || root.Size != 48 || root.BaseRva != 0 || manager == null || manager.Name != "GameManager" || manager.Size != 48 || !manager.ObjectDerived)
                throw Error("pinned native Object/GameManager roots differ");
        }
        internal static void Validate(Data data)
        {
            if (data == null || data.Version != 1 || data.Rows == null || data.Rows.Length > Maximum || data.RegisteredCount != data.Rows.Length
                || data.CoverageGaps == null || data.CoverageGaps.Length > MaximumTypes * 4 || data.CoverageGaps.Any(value => string.IsNullOrEmpty(value) || value.Length > 8192))
                throw Error("native Object directory schema differs");
            ValidateClasses(data.Classes); var types = data.Classes.ToDictionary(value => value.Rva); var bindings = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in data.Rows) {
                if (row == null || !types.TryGetValue(row.TypeRva, out var type) || !type.ObjectDerived || type.Abstract || type.Stripped || type.Size < 48 || row.Vtable <= 0 || row.Binding == null || row.ManagedType == null
                    || row.Binding.Length > 32768 || row.ManagedType.Length > 32768 || row.ManagedPresent != (row.ManagedType.Length != 0)
                    || row.Kind != "directory" && row.Kind != "asset" && row.Kind != "native-asset" && row.Kind != "global" && row.Kind != "unbound"
                    || (row.Kind == "unbound") != (row.Binding.Length == 0) || row.Binding.Length != 0 && !bindings.Add(row.Binding)
                    || row.Kind == "native-asset" && type.GlobalManager
                    || row.Kind == "global" && (!type.GlobalManager || row.Binding != GlobalPrefix + type.Key)) throw Error("native Object directory identity/type ownership differs");
                if (row.Kind == "native-asset") {
                    if (row.Binding.StartsWith(ResidentBuiltinTextureRoots.Prefix,StringComparison.Ordinal))
                        ResidentBuiltinTextureRoots.RequireKeyType(row.Binding,type,types);
                    else if (!row.Binding.StartsWith("resident-native-asset/" + type.Rva.ToString("x", CultureInfo.InvariantCulture) + "/", StringComparison.Ordinal))
                        throw Error("serialized native asset key does not match its registered type");
                }
            }
        }
        internal static void RequireCompleteDirectory(Data data)
        {
            Validate(data);
            var classes = data.Classes.ToDictionary(value => value.Rva);
            if (data.CoverageGaps.Length != 0 || data.Rows.Any(value => value.Kind == "unbound" || classes[value.TypeRva].GlobalManager && value.Kind != "global")
                || data.Rows.Where(value => classes[value.TypeRva].GlobalManager).GroupBy(value => value.TypeRva).Any(group => group.Count() != 1))
                throw Error("native Object identity directory is incomplete: " + string.Join(";", data.CoverageGaps));
        }
        private static InvalidDataException Error(string message) => new InvalidDataException("Resident native globals: " + message);
    }
}
