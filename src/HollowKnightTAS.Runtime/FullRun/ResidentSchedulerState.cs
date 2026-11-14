using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    // The native ABI selects callback DATA TYPES, never an object path, method
    // name or iterator type. IEnumerator state belongs to the unified graph.
    // No Current, MoveNext, StartCoroutine, Invoke or gameplay callback runs.
    internal static class ResidentSchedulerState
    {
        private const int Maximum = 65536, CoroutineBytes = 136;
        private const string Prefix = "resident-scheduler/";
        private static readonly FieldInfo WrapperPointer = typeof(Coroutine).GetField("m_Ptr", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw Error("Coroutine pointer field is absent");
        private static readonly ResidentGraphPolicy Metadata = new ResidentGraphPolicy(_ => null);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Allocate(UIntPtr size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Delete(IntPtr pointer, UIntPtr size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Construct(IntPtr pointer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Insert(IntPtr tree, IntPtr result, IntPtr callback);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Remove(IntPtr manager, IntPtr callback, IntPtr node);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr DuplicateString(IntPtr text);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeString(IntPtr text);

        internal sealed class Data
        {
            public int Version = 1, TreeRoot = -1, Next = -1;
            public uint Timestamp, ManagerPadding;
            public Owner[] Owners = Array.Empty<Owner>();
            public Routine[] Routines = Array.Empty<Routine>();
            public Callback[] Callbacks = Array.Empty<Callback>();
            public string[] ReleasedWrappers = Array.Empty<string>(), CoverageGaps = Array.Empty<string>();
        }
        internal sealed class Owner { public string Binding = ""; public string[] Order = Array.Empty<string>(); }
        internal sealed class Routine
        {
            public string Key = "", Owner = "", Execution = "", Continue = "", Waiting = "";
            public string Method = "", MoveNext = "", Current = "";
            public int References, EnumeratorWeakness, WrapperWeakness;
            public bool Done, EnumeratorBased, EnumeratorPresent, WrapperPresent, WrapperAllocated;
        }
        internal sealed class Callback
        {
            public int Left = -1, Parent = -1, Right = -1, Color, Call, Cleanup, Rate, Repeat, Mode;
            public long Time, Frame;
            public uint Timestamp;
            public string Owner = "", Kind = "", Routine = "", MethodName = "", Execution = "";
        }
        internal sealed class RoutineLive
        {
            internal IntPtr Pointer;
            internal byte[] Bytes = Array.Empty<byte>();
            internal Routine Row = new Routine();
            internal ResidentScriptingHandles.Capture Enumerator = null!, WrapperHandle = null!;
            internal Coroutine? Wrapper;
            internal readonly ResidentStaticRoots.Slot<object?> Slot = new ResidentStaticRoots.Slot<object?>();
        }
        internal sealed class CallbackLive { internal IntPtr Pointer; internal byte[] Bytes = Array.Empty<byte>(); internal Callback Row = new Callback(); }

        internal sealed class CaptureResult
        {
            internal Data Snapshot = new Data();
            internal readonly Dictionary<string, object> ManagedRoots = new Dictionary<string, object>(StringComparer.Ordinal);
            internal readonly Dictionary<object, string> externals = new Dictionary<object, string>(References.Instance);
            internal readonly Dictionary<string, object> externalValues = new Dictionary<string, object>(StringComparer.Ordinal);
            internal readonly Dictionary<IntPtr, RoutineLive> routines = new Dictionary<IntPtr, RoutineLive>();
            internal readonly List<CallbackLive> callbacks = new List<CallbackLive>();
            internal readonly Dictionary<string, IntPtr> owners = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            internal readonly List<Owner> ownerRows = new List<Owner>();
            internal readonly Dictionary<string, UnityEngine.Object> nativeAssets = new Dictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
            internal readonly Dictionary<string, IntPtr> assetPointers = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            internal readonly List<Tuple<IntPtr, byte[]>> witness = new List<Tuple<IntPtr, byte[]>>();
            internal readonly Dictionary<Coroutine, IntPtr> wrappers = new Dictionary<Coroutine, IntPtr>(References<Coroutine>.Instance);
            internal readonly SortedSet<string> gaps = new SortedSet<string>(StringComparer.Ordinal);
            internal readonly List<string> released = new List<string>();
            internal ResidentObjectDirectory directory = null!;
            internal IReadOnlyDictionary<string, object> graphRoots = null!;
            internal Func<object, string?> executionBinding = null!;
            internal IntPtr engine, manager, head;
            internal int routineOrdinal;
            internal IntPtr Manager => manager;
            internal string? BindExternal(object value) => externals.TryGetValue(value, out var key) ? key : null;
            internal object ResolveExternal(string key) => externalValues.TryGetValue(key, out var value) ? value : throw Error("scheduler external binding is absent");
            internal object? ResolveManagedRoot(string key)
            {
                if (!ManagedRoots.TryGetValue(key, out var value) || value is not ResidentStaticRoots.Slot<object?> slot) throw Error("scheduler graph root is absent");
                return slot.Value;
            }
            internal void RequireUnchanged()
            {
                directory.RequireCurrent();
                RequireAssets();
                foreach (var row in witness) if (!M.Read(row.Item1, row.Item2.Length).SequenceEqual(row.Item2)) throw Error("native scheduler changed during observation");
                foreach (var row in routines.Values) {
                    row.Enumerator.RequireUnchanged(); row.WrapperHandle.RequireUnchanged();
                    if (!ReferenceEquals(row.Slot.Value, row.Enumerator.Target)) throw Error("scheduler managed root changed before graph application");
                }
                foreach (var wrapper in wrappers) if ((IntPtr)WrapperPointer.GetValue(wrapper.Key)! != wrapper.Value) throw Error("stored Coroutine pointer changed");
            }
            internal void RequireAssets()
            { foreach (var asset in nativeAssets) if (M.Owner(asset.Value) != assetPointers[asset.Key]) throw Error("scheduler native asset identity changed"); }
            internal void RequireComplete() => RequireCanonicalState(Snapshot);
            internal byte[] Read(IntPtr pointer, int length)
            { var value = M.Read(pointer, length); witness.Add(Tuple.Create(pointer, value)); return value; }
            internal void Bind(object value, string key)
            {
                if (externals.TryGetValue(value, out var previous)) { if (previous != key) throw Error("scheduler object has multiple external bindings"); return; }
                if (externalValues.ContainsKey(key)) throw Error("scheduler external binding has multiple objects");
                externals.Add(value, key); externalValues.Add(key, value);
            }
            internal RoutineLive RoutineAt(IntPtr pointer, int depth = 0)
            {
                if (routines.TryGetValue(pointer, out var found)) return found;
                if (pointer == IntPtr.Zero || routines.Count >= Maximum || depth > 128) throw Error("native coroutine closure exceeds its bound");
                var bytes = Read(pointer, CoroutineBytes); var iterator = ResidentScriptingHandles.Read(IntPtr.Add(pointer, 16));
                var wrapper = ResidentScriptingHandles.Read(IntPtr.Add(pointer, 40)); var execution = iterator.Target == null ? null : executionBinding(iterator.Target);
                var key = execution == null ? Prefix + "coroutine/" + (routineOrdinal++).ToString("D6", CultureInfo.InvariantCulture) : Prefix + "execution/" + execution;
                if (routines.Values.Any(row => row.Row.Key == key)) throw Error("execution coroutine binding is not unique");
                var nativeOwner = M.Ptr(bytes, 88); var owner = owners.FirstOrDefault(row => row.Value == nativeOwner).Key ?? "";
                if (nativeOwner != IntPtr.Zero && owner.Length == 0) gaps.Add("scheduler-coroutine-owner-without-directory-binding:" + key);
                var row = new Routine { Key = key, Owner = owner, Execution = execution ?? "", References = M.I32(bytes, 96), Done = Flag(bytes, 100),
                    EnumeratorBased = Flag(bytes, 128), EnumeratorPresent = iterator.Present, EnumeratorWeakness = iterator.Weakness,
                    WrapperAllocated = wrapper.Allocated, WrapperWeakness = wrapper.Weakness,
                    Method = Method(M.Ptr(bytes, 64)), MoveNext = Method(M.Ptr(bytes, 72)), Current = Method(M.Ptr(bytes, 80)) };
                var live = new RoutineLive { Pointer = pointer, Bytes = bytes, Row = row, Enumerator = iterator, WrapperHandle = wrapper };
                routines.Add(pointer, live); live.Slot.Value = iterator.Target;
                if (execution == null) ManagedRoots.Add(Root(key), live.Slot);
                else if (iterator.Target != null) Bind(iterator.Target, execution);
                if (iterator.Allocated && !iterator.Present || iterator.Present && iterator.Target is not IEnumerator || iterator.Present && iterator.Weakness == 0)
                    gaps.Add("scheduler-coroutine-enumerator-handle:" + key);
                if (wrapper.Allocated && !wrapper.Present || wrapper.Present && wrapper.Target?.GetType() != typeof(Coroutine)) gaps.Add("scheduler-coroutine-wrapper-handle:" + key);
                if (!iterator.CacheMatches || !wrapper.CacheMatches) gaps.Add("scheduler-handle-cache-needs-canon:" + key);
                if (M.Ptr(bytes, 120) != IntPtr.Zero && execution == null) gaps.Add("scheduler-coroutine-native-async-operation:" + key);
                if (wrapper.Target is Coroutine managed) RegisterWrapper(managed, live);
                var continuation = M.Ptr(bytes, 104); var waiting = M.Ptr(bytes, 112);
                if (continuation != IntPtr.Zero) row.Continue = RoutineAt(continuation, depth + 1).Row.Key;
                if (waiting != IntPtr.Zero) row.Waiting = RoutineAt(waiting, depth + 1).Row.Key;
                return live;
            }
            internal void RegisterWrapper(Coroutine wrapper, RoutineLive live)
            {
                if (wrapper.GetType() != typeof(Coroutine) || (IntPtr)WrapperPointer.GetValue(wrapper)! != live.Pointer
                    || live.Wrapper != null && !ReferenceEquals(live.Wrapper, wrapper)) throw Error("native coroutine wrapper identity differs");
                live.Wrapper = wrapper; live.Row.WrapperPresent = true; wrappers[wrapper] = live.Pointer;
                Bind(wrapper, Wrapper(live.Row.Key));
            }
            internal string? DiscoverStoredWrapper(object value, Action<string, object> addRoot)
            {
                if (value is not Coroutine wrapper) return BindExternal(value);
                if (externals.TryGetValue(wrapper, out var known)) return known;
                var pointer = (IntPtr)WrapperPointer.GetValue(wrapper)!;
                if (pointer != IntPtr.Zero) {
                    var count = ManagedRoots.Count;
                    RegisterWrapper(wrapper, RoutineAt(pointer));
                    if (ManagedRoots.Count != count)
                        foreach (var pair in ManagedRoots) addRoot(pair.Key, pair.Value);
                } else {
                    var key = Prefix + "released/" + released.Count.ToString("D6", CultureInfo.InvariantCulture);
                    released.Add(key); Bind(wrapper, key); wrappers.Add(wrapper, pointer);
                }
                return externals[wrapper];
            }
            internal void SealStoredWrappers()
            {
                Snapshot.Owners = ownerRows.OrderBy(row => row.Binding, StringComparer.Ordinal).ToArray();
                var linked = new HashSet<string>(Snapshot.Owners.SelectMany(owner => owner.Order), StringComparer.Ordinal);
                foreach (var routine in routines.Values)
                    if (linked.Contains(routine.Row.Key) != (M.Ptr(routine.Bytes, 0) != IntPtr.Zero || M.Ptr(routine.Bytes, 8) != IntPtr.Zero))
                        throw Error("discovered coroutine native list is outside the owner inventory");
                Snapshot.Routines = routines.Values.Select(row => row.Row).OrderBy(row => row.Key, StringComparer.Ordinal).ToArray();
                Snapshot.ReleasedWrappers = released.ToArray();
                Snapshot.CoverageGaps = gaps.ToArray();
                Validate(Snapshot);
                RequireUnchanged();
            }

            internal void IncludeAsset(string binding, UnityEngine.Object asset, Action<string, object> addRoot)
            {
                if (ResidentAssetBindings.IsSharedBinding(binding) || nativeAssets.ContainsKey(binding)) return;
                var pointer = M.Owner(asset);
                nativeAssets.Add(binding, asset); assetPointers.Add(binding, pointer);
                if (asset is not MonoBehaviour && asset is not ScriptableObject) return;
                if (owners.ContainsValue(pointer)) throw Error("new scheduler asset aliases an existing owner");
                owners.Add(binding, pointer);
                foreach (var routine in routines.Values.Where(row => M.Ptr(row.Bytes, 88) == pointer && row.Row.Owner.Length == 0)) {
                    routine.Row.Owner = binding;
                    gaps.Remove("scheduler-coroutine-owner-without-directory-binding:" + routine.Row.Key);
                }
                ownerRows.Add(ReadOwner(binding, pointer));
                foreach (var root in ManagedRoots) addRoot(root.Key, root.Value);
            }
            internal Owner ReadOwner(string binding, IntPtr pointer)
            {
                var head = IntPtr.Add(pointer, 112); var links = Read(head, 16); var order = new List<string>(); var previous = head;
                var visited = new HashSet<IntPtr>();
                for (var node = M.Ptr(links, 8); node != head;) {
                    if (!visited.Add(node) || visited.Count > Maximum) throw Error("MonoBehaviour coroutine list is cyclic");
                    var routine = RoutineAt(node);
                    if (routine.Row.Owner != binding || M.Ptr(routine.Bytes, 0) != previous) throw Error("MonoBehaviour coroutine list ownership differs");
                    order.Add(routine.Row.Key); previous = node; node = M.Ptr(routine.Bytes, 8);
                }
                if (M.Ptr(links, 0) != previous) throw Error("MonoBehaviour coroutine list tail differs");
                return new Owner { Binding = binding, Order = order.ToArray() };
            }
        }

        internal static CaptureResult Capture(ResidentObjectDirectory directory, IReadOnlyDictionary<string, object> graphRoots, Func<object, string?> executionBinding,
            IReadOnlyDictionary<string, UnityEngine.Object>? nativeAssets = null)

        {
            if (directory == null || graphRoots == null || executionBinding == null) throw new ArgumentNullException();
            var engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress; RequireEntries(engine);
            var live = new CaptureResult { directory = directory, graphRoots = graphRoots, executionBinding = executionBinding, engine = engine };
            if (nativeAssets != null) foreach (var asset in nativeAssets.Where(asset => !ResidentAssetBindings.IsSharedBinding(asset.Key))) {
                live.nativeAssets.Add(asset.Key, asset.Value); live.assetPointers.Add(asset.Key, M.Owner(asset.Value));
            }
            var allNative = directory.NativeBindings.Concat(live.nativeAssets).ToArray();
            if (allNative.Select(row => row.Key).Distinct(StringComparer.Ordinal).Count() != allNative.Length
                || allNative.Select(row => row.Value.GetInstanceID()).Distinct().Count() != allNative.Length) throw Error("scheduler native owner/asset bindings are aliased");
            foreach (var owner in allNative.Where(row => row.Value is MonoBehaviour || row.Value is ScriptableObject).OrderBy(row => row.Key, StringComparer.Ordinal)) live.owners.Add(owner.Key, M.Owner(owner.Value));
            live.manager = M.Ptr(live.Read(IntPtr.Add(engine, 0x19d7a70), 8), 0);
            if (live.manager == IntPtr.Zero || M.Ptr(live.Read(live.manager, 8), 0) != IntPtr.Add(engine, 0x167d668))
                throw Error("pinned DelayedCallManager vtable differs");
            // Object48 belongs to the shared Object adapter and may acquire a
            // new handle during common Canon. Only this manager's body is our
            // frozen witness: callback tree/count, current iterator, timestamp
            // and trailing C++ alignment padding (76..79).
            var manager = new byte[80]; Buffer.BlockCopy(live.Read(IntPtr.Add(live.manager, 48), 32), 0, manager, 48, 32);
            live.head = M.Ptr(manager, 48); var sentinel = live.Read(live.head, 32);
            if (sentinel[25] != 1 || sentinel[24] != 1 || M.U64(manager, 56) > Maximum) throw Error("native callback sentinel/count differs");
            var ownerRows = live.ownerRows;
            foreach (var owner in live.owners) ownerRows.Add(live.ReadOwner(owner.Key, owner.Value));
            var visitedNodes = new HashSet<IntPtr>();
            void Walk(IntPtr pointer, IntPtr parent, int depth) {
                if (pointer == live.head) return;
                if (pointer == IntPtr.Zero || depth > 128 || !visitedNodes.Add(pointer) || visitedNodes.Count > Maximum) throw Error("native callback tree closure differs");
                var bytes = live.Read(pointer, 96);
                if (bytes[25] != 0 || bytes[24] > 1 || M.Ptr(bytes, 8) != parent) throw Error("native callback tree parent/color differs");
                Walk(M.Ptr(bytes, 0), pointer, depth + 1);
                live.callbacks.Add(new CallbackLive { Pointer = pointer, Bytes = bytes });
                Walk(M.Ptr(bytes, 16), pointer, depth + 1);
            }
            Walk(M.Ptr(sentinel, 8), live.head, 0);
            if (live.callbacks.Count != (int)M.U64(manager, 56) || M.Ptr(sentinel, 0) != (live.callbacks.Count == 0 ? live.head : live.callbacks[0].Pointer)
                || M.Ptr(sentinel, 16) != (live.callbacks.Count == 0 ? live.head : live.callbacks[live.callbacks.Count - 1].Pointer)) throw Error("native callback tree extremum/count differs");
            var index = live.callbacks.Select((row, i) => new { row.Pointer, Index = i }).ToDictionary(row => row.Pointer, row => row.Index);
            int Position(IntPtr pointer) => pointer == live.head ? -1 : index.TryGetValue(pointer, out var at) ? at : throw Error("native callback iterator is outside tree");
            var ownerIds = allNative.ToDictionary(row => row.Value.GetInstanceID(), row => row.Key);
            var invokePointers = new HashSet<IntPtr>(); var executionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var callback in live.callbacks) {
                var bytes = callback.Bytes; var call = Rva(M.Ptr(bytes, 64), engine); var cleanup = Rva(M.Ptr(bytes, 72), engine); var id = M.I32(bytes, 80);
                var owner = id == 0 ? "" : ownerIds.TryGetValue(id, out var binding) ? binding : "";
                if (id != 0 && owner.Length == 0) live.gaps.Add("scheduler-callback-owner-without-directory-binding:" + live.callbacks.IndexOf(callback));
                var row = new Callback { Left = Position(M.Ptr(bytes, 0)), Parent = Position(M.Ptr(bytes, 8)), Right = Position(M.Ptr(bytes, 16)), Color = bytes[24],
                    Time = BitConverter.ToInt64(bytes, 32), Frame = BitConverter.ToInt64(bytes, 40), Rate = M.I32(bytes, 48), Repeat = bytes[52],
                    Call = call, Cleanup = cleanup, Owner = owner, Mode = M.I32(bytes, 84), Timestamp = M.U32(bytes, 88) };
                var user = M.Ptr(bytes, 56);
                if (call == 0x8a6c70 && cleanup == 0x8a6950 && user != IntPtr.Zero) {
                    var routine = live.RoutineAt(user); row.Kind = "coroutine"; row.Routine = routine.Row.Key;
                    if (row.Owner != routine.Row.Owner) throw Error("callback and coroutine have different native owners");
                    if (routine.Row.Execution.Length != 0) {
                        var key = routine.Row.Execution; var ordinal = executionCounts.TryGetValue(key, out var count) ? count : 0; executionCounts[key] = ordinal + 1;
                        row.Execution = key + "/callback/" + ordinal.ToString(CultureInfo.InvariantCulture);
                    }
                } else if (call == 0x8ac680 && cleanup == 0 && user == IntPtr.Zero && live.owners.ContainsKey(owner)) row.Kind = "start";
                else if (call == 0x8c0ea0 && cleanup == 0x8c0f00 && user != IntPtr.Zero && live.owners.ContainsKey(owner)) {
                    row.Kind = "invoke"; row.MethodName = ReadCString(live, user);
                    if (!invokePointers.Add(user)) live.gaps.Add("scheduler-invoke-data-has-shared-allocation");
                } else { row.Kind = "unsupported"; live.gaps.Add("scheduler-callback-native-data-type:" + call.ToString("x") + "/" + cleanup.ToString("x")); }
                callback.Row = row;
            }
            var linkedRoutines = new HashSet<string>(ownerRows.SelectMany(owner => owner.Order), StringComparer.Ordinal);
            foreach (var routine in live.routines.Values) {
                var linked = M.Ptr(routine.Bytes, 0) != IntPtr.Zero || M.Ptr(routine.Bytes, 8) != IntPtr.Zero;
                if (linked != linkedRoutines.Contains(routine.Row.Key)) throw Error("coroutine native list is outside actual owner inventory");
            }
            live.Snapshot = new Data { TreeRoot = Position(M.Ptr(sentinel, 8)), Next = Position(M.Ptr(manager, 64)), Timestamp = M.U32(manager, 72), ManagerPadding = M.U32(manager, 76),
                Owners = ownerRows.ToArray(), Routines = live.routines.Values.Select(row => row.Row).OrderBy(row => row.Key, StringComparer.Ordinal).ToArray(),
                Callbacks = live.callbacks.Select(row => row.Row).ToArray(), ReleasedWrappers = live.released.ToArray(), CoverageGaps = live.gaps.ToArray() };
            Validate(live.Snapshot); live.RequireUnchanged(); return live;
        }

        internal sealed class Plan
        {
            internal readonly Dictionary<string, object> ManagedRoots = new Dictionary<string, object>(StringComparer.Ordinal);
            private readonly Data source;
            private readonly CaptureResult target;
            private readonly ResidentMutationScope scope;
            private readonly Action admission;
            private readonly Dictionary<string, IntPtr> pointers = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            private readonly Dictionary<string, Coroutine> wrappers = new Dictionary<string, Coroutine>(StringComparer.Ordinal);
            private readonly Dictionary<object, string> externalBindings = new Dictionary<object, string>(References.Instance);
            private readonly Dictionary<string, object> externalValues = new Dictionary<string, object>(StringComparer.Ordinal);
            private readonly Dictionary<string, ResidentStaticRoots.Slot<object?>> slots = new Dictionary<string, ResidentStaticRoots.Slot<object?>>(StringComparer.Ordinal);
            private readonly IntPtr[] nodes;
            private bool applied, faulted, windowEntered;
            internal Plan(Data source, CaptureResult target, ResidentMutationScope scope, Action admission)
            {
                this.source = JsonConvert.DeserializeObject<Data>(JsonConvert.SerializeObject(source)) ?? throw Error("scheduler source is absent");
                source = this.source;
                this.target = target; this.scope = scope; this.admission = admission; nodes = new IntPtr[source.Callbacks.Length];
                RequireCanonicalState(this.source); target.RequireComplete(); Require(); target.RequireUnchanged();

                // Map control plane owners across compilation differences
                var targetNormalizedOwners = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var o in target.Snapshot.Owners)
                {
                    var norm = NormalizeOwnerKey(o.Binding);
                    if (!targetNormalizedOwners.ContainsKey(norm))
                        targetNormalizedOwners.Add(norm, o.Binding);
                }
                string MapBinding(string b)
                {
                    if (string.IsNullOrEmpty(b)) return b;
                    if (target.owners.ContainsKey(b)) return b;
                    var norm = NormalizeOwnerKey(b);
                    return targetNormalizedOwners.TryGetValue(norm, out var targetKey) ? targetKey : b;
                }
                foreach (var o in this.source.Owners) {
                    o.Binding = MapBinding(o.Binding);
                }
                this.source.Owners = this.source.Owners.OrderBy(row => row.Binding, StringComparer.Ordinal).ToArray();
                foreach (var r in this.source.Routines) {
                    if (r.Owner.Length != 0) r.Owner = MapBinding(r.Owner);
                }
                foreach (var c in this.source.Callbacks) {
                    if (c.Owner.Length != 0) c.Owner = MapBinding(c.Owner);
                }

                if (source.CoverageGaps.Length != 0)
                    throw Error("scheduler source coverage differs: " + string.Join(";", source.CoverageGaps));
                var srcBindings = source.Owners.Select(row => row.Binding).ToArray();
                var tgtBindings = target.Snapshot.Owners.Select(row => row.Binding).ToArray();
                if (!srcBindings.SequenceEqual(tgtBindings))
                {
                    var missing = string.Join(",", srcBindings.Except(tgtBindings).Take(10));
                    var extra = string.Join(",", tgtBindings.Except(srcBindings).Take(10));
                    throw Error("scheduler native owner topology differs: missing=[" + missing + "], extra=[" + extra + "], srcCount=" + srcBindings.Length + ", tgtCount=" + tgtBindings.Length);
                }
                var targetExecution = target.routines.Values.Where(row => row.Row.Execution.Length != 0).ToDictionary(row => row.Row.Execution, StringComparer.Ordinal);
                if (!source.Routines.Where(row => row.Execution.Length != 0).Select(row => row.Execution).OrderBy(x => x, StringComparer.Ordinal)
                    .SequenceEqual(targetExecution.Keys.OrderBy(x => x, StringComparer.Ordinal))) throw Error("executing coroutine identities differ");
                var targetCallbacks = target.callbacks.Where(row => row.Row.Execution.Length != 0).ToDictionary(row => row.Row.Execution, StringComparer.Ordinal);
                if (!source.Callbacks.Where(row => row.Execution.Length != 0).Select(row => row.Execution).OrderBy(x => x, StringComparer.Ordinal)
                    .SequenceEqual(targetCallbacks.Keys.OrderBy(x => x, StringComparer.Ordinal))) throw Error("executing callback identities differ");

                // Align executing control plane routine method metadata to target instance
                foreach (var row in source.Routines.Where(row => row.Execution.Length != 0)) {
                    if (targetExecution.TryGetValue(row.Execution, out var curExec)) {
                        row.Method = curExec.Row.Method;
                        row.MoveNext = curExec.Row.MoveNext;
                        row.Current = curExec.Row.Current;
                    }
                }

                // Resolve all loaded method metadata before native allocation.
                foreach (var row in source.Routines.Where(row => row.Execution.Length == 0)) { ResolveMethod(row.Method); ResolveMethod(row.MoveNext); ResolveMethod(row.Current); }
                foreach (var row in source.Routines) {
                    Require();
                    if (row.Execution.Length != 0) {
                        var current = targetExecution[row.Execution];
                        if (JsonConvert.SerializeObject(row) != JsonConvert.SerializeObject(current.Row))
                            throw Error("executing coroutine local state differs: row=" + JsonConvert.SerializeObject(row) + ", cur=" + JsonConvert.SerializeObject(current.Row));
                        pointers.Add(row.Key, current.Pointer);
                        if (current.Wrapper != null) Bind(current.Wrapper, Wrapper(row.Key));
                        if (current.Enumerator.Target != null) Bind(current.Enumerator.Target, row.Execution);
                        continue;
                    }
                    var pointer = Entry<Allocate>(target.engine, 0x567ff0)(new UIntPtr(CoroutineBytes));
                    if (pointer == IntPtr.Zero) throw Error("native coroutine allocation failed");
                    M.Write(pointer, new byte[CoroutineBytes]); Entry<Construct>(target.engine, 0x8a6800)(pointer); pointers.Add(row.Key, pointer);
                    var slot = new ResidentStaticRoots.Slot<object?>(); slots.Add(Root(row.Key), slot); ManagedRoots.Add(Root(row.Key), slot);
                    if (row.WrapperPresent) {
                        var wrapper = NewWrapper(pointer);
                        wrappers.Add(row.Key, wrapper); Bind(wrapper, Wrapper(row.Key));
                    }
                }
                foreach (var key in source.ReleasedWrappers) Bind(NewWrapper(IntPtr.Zero), key);
                for (var i = 0; i < source.Callbacks.Length; i++) if (source.Callbacks[i].Execution.Length != 0) {
                    var before = targetCallbacks[source.Callbacks[i].Execution]; var row = source.Callbacks[i];
                    if (row.Kind != before.Row.Kind || row.Call != before.Row.Call || row.Cleanup != before.Row.Cleanup || row.Owner != before.Row.Owner
                        || row.Routine != before.Row.Routine || row.Mode != before.Row.Mode || row.Repeat != before.Row.Repeat || row.Rate != before.Row.Rate)
                        throw Error("executing callback type/ownership differs");
                    nodes[i] = before.Pointer;
                }
                Require();
            }
            internal string? BindExternal(object value) => externalBindings.TryGetValue(value, out var binding) ? binding : null;
            internal object ResolveExternal(string key) => externalValues.TryGetValue(key, out var value) ? value : throw Error("prepared scheduler external is absent");
            internal object? ResolveManagedRoot(string key) => slots.TryGetValue(key, out var slot) ? slot.Value : throw Error("prepared scheduler root is absent");
            private void Bind(object value, string key) { externalValues.Add(key, value); externalBindings.Add(value, key); }
            private void Require() { if (faulted) throw Error("failed scheduler transaction requires process retirement"); scope.Require(); admission(); }
            internal void Apply() => ApplyAround(reconcile => reconcile());

            // The only allowed interim change is native Start registration for
            // actual transaction-owned MonoBehaviours. Non-Start payloads,
            // native coroutine state, owner lists and the running callback
            // retain their original identities. The caller invokes reconcile
            // at ResidentMonoLifecycle's reconciliation point before it checks
            // callback counts. Swallowed/reentrant errors poison the plan.
            internal void ApplyAround(Action<Action> controlledNativeOperations)
            {
                if (windowEntered || applied || faulted) { faulted = true; throw Error("scheduler native window is already consumed"); }
                if (controlledNativeOperations == null) throw new ArgumentNullException(nameof(controlledNativeOperations));
                windowEntered = true;
                try {
                    Require(); target.RequireUnchanged(); var reconciled = false; CaptureResult? restored = null;
                    controlledNativeOperations(() => {
                        if (reconciled || applied || faulted) { faulted = true; throw Error("scheduler reconciliation was reused"); }
                        reconciled = true;
                        try {
                            Require(); var actual = Capture(CurrentDirectory(), new Dictionary<string, object>(), target.executionBinding, target.nativeAssets);
                            RequireControlledChanges(actual); ApplyCore(actual);
                            restored = Capture(CurrentDirectory(), new Dictionary<string, object>(), target.executionBinding, target.nativeAssets); restored.RequireComplete();
                        } catch { faulted = true; throw; }
                    });
                    Require();
                    if (!reconciled || restored == null) throw Error("native operation window did not reconcile the scheduler");
                    restored.RequireUnchanged(); Require();
                } catch { faulted = true; throw; }
            }
            private ResidentObjectDirectory CurrentDirectory()
            {
                target.RequireAssets();
                var current = ResidentObjectDirectory.Capture(target.directory.Ownership);
                ResidentObjectDirectory.RequireSame(target.directory.CopySnapshot(), current.CopySnapshot(), false);
                if (target.directory.NativeBindings.Count != current.NativeBindings.Count || target.directory.NativeBindings.Any(row => !current.NativeBindings.TryGetValue(row.Key, out var value)
                    || !ReferenceEquals(value, row.Value))) throw Error("controlled scheduler operation replaced a native directory identity");
                return current;
            }
            private void RequireControlledChanges(CaptureResult actual)
            {
                actual.RequireComplete();
                if (actual.manager != target.manager || actual.head != target.head || actual.Snapshot.Timestamp != target.Snapshot.Timestamp
                    || actual.Snapshot.ManagerPadding != target.Snapshot.ManagerPadding
                    || !M.Read(target.manager, 8).SequenceEqual(target.witness.First(row => row.Item1 == target.manager && row.Item2.Length == 8).Item2))
                    throw Error("controlled operation changed scheduler manager ownership/epoch");
                foreach (var routine in target.routines.Values) {
                    if (!M.Read(routine.Pointer, CoroutineBytes).SequenceEqual(routine.Bytes)) throw Error("controlled operation changed an existing native coroutine");
                    routine.Enumerator.RequireUnchanged(); routine.WrapperHandle.RequireUnchanged();
                }
                if (actual.routines.Keys.Any(pointer => !target.routines.ContainsKey(pointer))) throw Error("controlled operation created a gameplay coroutine");
                foreach (var owner in target.owners) {
                    var head = IntPtr.Add(owner.Value, 112); var before = target.witness.First(row => row.Item1 == head && row.Item2.Length == 16).Item2;
                    if (!M.Read(head, 16).SequenceEqual(before)) throw Error("controlled operation changed a native coroutine owner list");
                }
                foreach (var wrapper in target.wrappers) if ((IntPtr)WrapperPointer.GetValue(wrapper.Key)! != wrapper.Value) throw Error("controlled operation rebound an existing Coroutine wrapper");
                bool OwnedStart(Callback row) => row.Kind == "start" && target.directory.NativeBindings.TryGetValue(row.Owner, out var owner)
                    && owner is MonoBehaviour mono && scope.Owns(mono);
                var beforeCallbacks = target.callbacks.Where(row => !OwnedStart(row.Row)).ToDictionary(row => row.Pointer);
                var afterCallbacks = actual.callbacks.Where(row => !OwnedStart(row.Row)).ToDictionary(row => row.Pointer);
                if (beforeCallbacks.Count != afterCallbacks.Count || beforeCallbacks.Any(row => !afterCallbacks.TryGetValue(row.Key, out var after)
                    || !row.Value.Bytes.Skip(32).SequenceEqual(after.Bytes.Skip(32)))) throw Error("controlled operation changed a non-Start callback");
                var beforeNext = target.Snapshot.Next < 0 ? target.head : target.callbacks[target.Snapshot.Next].Pointer;
                var afterNext = actual.Snapshot.Next < 0 ? actual.head : actual.callbacks[actual.Snapshot.Next].Pointer;
                if (beforeNext != afterNext) {
                    if (beforeNext == target.head || !OwnedStart(target.callbacks[target.Snapshot.Next].Row))
                        throw Error("controlled operation advanced a protected callback iterator");
                    // Removing the pending owned Start may move the iterator
                    // through other owned Starts, but never past an existing
                    // non-Start (or foreign Start) callback.
                    var protectedNext = target.callbacks.Skip(target.Snapshot.Next).FirstOrDefault(row => !OwnedStart(row.Row));
                    var expected = protectedNext?.Pointer ?? target.head;
                    if (afterNext != expected && (afterNext == actual.head || !OwnedStart(actual.callbacks[actual.Snapshot.Next].Row)
                        || protectedNext != null && actual.Snapshot.Next > actual.callbacks.FindIndex(row => row.Pointer == protectedNext.Pointer)))
                        throw Error("controlled operation moved the iterator past a protected callback");
                }
            }
            private void ApplyCore(CaptureResult actual)
            {
                try {
                    Require(); actual.RequireUnchanged(); applied = true;
                    // The callback executing this boundary remains the SAME
                    // node: DelayedCallManager::Update has it on its stack.
                    var remove = Entry<Remove>(target.engine, 0x62ab10);
                    foreach (var row in actual.callbacks.Where(row => row.Row.Execution.Length == 0)) {
                        remove(target.manager, IntPtr.Add(row.Pointer, 32), row.Pointer);
                        if (row.Row.Kind == "invoke") Entry<FreeString>(target.engine, 0x1587ac0)(M.Ptr(row.Bytes, 56));
                    }
                    foreach (var row in target.routines.Values.Where(row => row.Row.Execution.Length == 0)) {
                        if (row.Wrapper != null) { GC.SuppressFinalize(row.Wrapper); WrapperPointer.SetValue(row.Wrapper, IntPtr.Zero); }
                        ResidentScriptingHandles.Apply(IntPtr.Add(row.Pointer, 16), null, 0, Require);
                        ResidentScriptingHandles.Apply(IntPtr.Add(row.Pointer, 40), null, 0, Require);
                        // No native cleanup traversal or managed callback runs.
                        Entry<Delete>(target.engine, 0x1551d80)(row.Pointer, new UIntPtr(CoroutineBytes));
                    }
                    foreach (var row in source.Routines.Where(row => row.Execution.Length == 0)) {
                        Require(); var pointer = pointers[row.Key]; var iterator = slots[Root(row.Key)].Value;
                        if (row.EnumeratorPresent != (iterator != null) || iterator != null && iterator is not IEnumerator) throw Error("unified graph iterator type differs");
                        ResidentScriptingHandles.Apply(IntPtr.Add(pointer, 16), iterator, row.EnumeratorWeakness, Require);
                        var wrapper = row.WrapperAllocated ? wrappers[row.Key] : null;
                        ResidentScriptingHandles.Apply(IntPtr.Add(pointer, 40), wrapper, row.WrapperWeakness, Require);
                        Pointer(IntPtr.Add(pointer, 64), ResidentScriptingHandles.MethodPointer(ResolveMethod(row.Method)));
                        Pointer(IntPtr.Add(pointer, 72), ResidentScriptingHandles.MethodPointer(ResolveMethod(row.MoveNext)));
                        Pointer(IntPtr.Add(pointer, 80), ResidentScriptingHandles.MethodPointer(ResolveMethod(row.Current)));
                        Pointer(IntPtr.Add(pointer, 88), row.Owner.Length == 0 ? IntPtr.Zero : target.owners[row.Owner]);
                        M.Write(IntPtr.Add(pointer, 96), BitConverter.GetBytes(row.References)); M.Write(IntPtr.Add(pointer, 100), new[] { row.Done ? (byte)1 : (byte)0 });
                        Pointer(IntPtr.Add(pointer, 104), Relation(row.Continue)); Pointer(IntPtr.Add(pointer, 112), Relation(row.Waiting));
                        M.Write(IntPtr.Add(pointer, 128), new[] { row.EnumeratorBased ? (byte)1 : (byte)0 });
                    }
                    foreach (var row in source.Routines.Where(row => row.Execution.Length != 0)) {
                        Pointer(IntPtr.Add(pointers[row.Key], 104), Relation(row.Continue)); Pointer(IntPtr.Add(pointers[row.Key], 112), Relation(row.Waiting));
                    }
                    foreach (var owner in source.Owners) {
                        var sentinel = IntPtr.Add(target.owners[owner.Binding], 112); var order = owner.Order.Select(key => pointers[key]).ToArray();
                        Pointer(sentinel, order.Length == 0 ? sentinel : order[order.Length - 1]); Pointer(IntPtr.Add(sentinel, 8), order.Length == 0 ? sentinel : order[0]);
                        for (var i = 0; i < order.Length; i++) { Pointer(order[i], i == 0 ? sentinel : order[i - 1]); Pointer(IntPtr.Add(order[i], 8), i + 1 == order.Length ? sentinel : order[i + 1]); }
                    }
                    var insert = Entry<Insert>(target.engine, 0x627ca0); var temporary = Marshal.AllocHGlobal(72);
                    try {
                        for (var i = 0; i < source.Callbacks.Length; i++) {
                            Require(); var row = source.Callbacks[i]; var bytes = CallbackBytes(row);
                            if (nodes[i] != IntPtr.Zero) { M.Write(IntPtr.Add(nodes[i], 32), bytes); continue; }
                            Marshal.Copy(bytes, 0, temporary, bytes.Length); insert(IntPtr.Add(target.manager, 48), IntPtr.Add(temporary, 64), temporary);
                            nodes[i] = M.Ptr(IntPtr.Add(temporary, 64));
                            if (nodes[i] == IntPtr.Zero || nodes[i] == target.head) throw Error("callback insertion did not return its native node");
                        }
                    } finally { Marshal.FreeHGlobal(temporary); }
                    for (var i = 0; i < nodes.Length; i++) {
                        var row = source.Callbacks[i]; Pointer(nodes[i], Node(row.Left)); Pointer(IntPtr.Add(nodes[i], 8), Node(row.Parent)); Pointer(IntPtr.Add(nodes[i], 16), Node(row.Right));
                        M.Write(IntPtr.Add(nodes[i], 24), new[] { (byte)row.Color, (byte)0 });
                    }
                    Pointer(target.head, nodes.Length == 0 ? target.head : nodes[0]); Pointer(IntPtr.Add(target.head, 8), Node(source.TreeRoot));
                    Pointer(IntPtr.Add(target.head, 16), nodes.Length == 0 ? target.head : nodes[nodes.Length - 1]);
                    M.Write(IntPtr.Add(target.manager, 56), BitConverter.GetBytes((ulong)nodes.Length)); Pointer(IntPtr.Add(target.manager, 64), Node(source.Next));
                    M.Write(IntPtr.Add(target.manager, 72), BitConverter.GetBytes(source.Timestamp)); Require();
                } catch { faulted = true; throw; }
            }
            private IntPtr Relation(string key) => key.Length == 0 ? IntPtr.Zero : pointers[key];
            private IntPtr Node(int index) => index < 0 ? target.head : nodes[index];
            private byte[] CallbackBytes(Callback row)
            {
                var bytes = new byte[64]; Copy(bytes, 0, BitConverter.GetBytes(row.Time)); Copy(bytes, 8, BitConverter.GetBytes(row.Frame));
                Copy(bytes, 16, BitConverter.GetBytes(row.Rate)); bytes[20] = (byte)row.Repeat; IntPtr user = IntPtr.Zero;
                if (row.Kind == "coroutine") user = pointers[row.Routine];
                else if (row.Kind == "invoke") {
                    var utf8 = new UTF8Encoding(false, true).GetBytes(row.MethodName + "\0"); var text = Marshal.AllocHGlobal(utf8.Length);
                    try { Marshal.Copy(utf8, 0, text, utf8.Length); user = Entry<DuplicateString>(target.engine, 0x1587a48)(text); }
                    finally { Marshal.FreeHGlobal(text); }
                    if (user == IntPtr.Zero) throw Error("invoke method-name allocation failed");
                }
                Copy(bytes, 24, BitConverter.GetBytes(user.ToInt64())); Copy(bytes, 32, BitConverter.GetBytes(IntPtr.Add(target.engine, row.Call).ToInt64()));
                Copy(bytes, 40, BitConverter.GetBytes(row.Cleanup == 0 ? 0L : IntPtr.Add(target.engine, row.Cleanup).ToInt64()));
                var owner = row.Owner.Length == 0 ? null : target.directory.NativeBindings.TryGetValue(row.Owner, out var component) ? component : target.nativeAssets[row.Owner];
                Copy(bytes, 48, BitConverter.GetBytes(owner == null ? 0 : owner.GetInstanceID()));
                Copy(bytes, 52, BitConverter.GetBytes(row.Mode)); Copy(bytes, 56, BitConverter.GetBytes(row.Timestamp)); return bytes;
            }
            internal void Verify(IReadOnlyDictionary<string, object> currentGraphRoots)
            {
                if (!applied || faulted) throw Error("scheduler verification precedes application");
                try {
                    Require(); var actual = Capture(CurrentDirectory(), currentGraphRoots, target.executionBinding, target.nativeAssets); actual.RequireComplete();
                    if (JsonConvert.SerializeObject(source) != JsonConvert.SerializeObject(actual.Snapshot)) {
                        var s1 = JsonConvert.SerializeObject(source);
                        var s2 = JsonConvert.SerializeObject(actual.Snapshot);
                        var diffPos = -1;
                        for (int i = 0; i < Math.Min(s1.Length, s2.Length); i++) {
                            if (s1[i] != s2[i]) { diffPos = i; break; }
                        }
                        var preview1 = diffPos >= 0 ? s1.Substring(Math.Max(0, diffPos - 30), Math.Min(60, s1.Length - Math.Max(0, diffPos - 30))) : "len:" + s1.Length;
                        var preview2 = diffPos >= 0 ? s2.Substring(Math.Max(0, diffPos - 30), Math.Min(60, s2.Length - Math.Max(0, diffPos - 30))) : "len:" + s2.Length;
                        throw Error("complete scheduler native readback differs at " + diffPos + ": src=[" + preview1 + "] act=[" + preview2 + "]");
                    }
                    foreach (var row in source.Routines.Where(row => row.Execution.Length == 0))
                        if (!ReferenceEquals(slots[Root(row.Key)].Value, actual.ResolveManagedRoot(Root(row.Key)))) throw Error("native scheduler and unified graph iterator identity differ");
                    foreach (var pair in externalValues) if (!ReferenceEquals(pair.Value, actual.ResolveExternal(pair.Key))) throw Error("scheduler external identity did not read back");
                    Require();
                } catch { faulted = true; throw; }
            }
        }

        internal static Plan Prepare(Data source, CaptureResult target, ResidentMutationScope scope, Action admitted)
        { Validate(source); if (target == null || scope == null || admitted == null) throw new ArgumentNullException(); return new Plan(source, target, scope, admitted); }

        internal static void CanonicalizeIdleStorage(CaptureResult observed, Action admitted)
        {
            if (observed == null || admitted == null) throw new ArgumentNullException(); admitted(); observed.RequireUnchanged();
            var eligible = CanonicalizableIdleHandles(observed);
            foreach (var candidate in eligible) {
                admitted(); var pointer = IntPtr.Add(candidate.Item1.Pointer, candidate.Item2);
                var handle = ResidentScriptingHandles.Read(pointer);
                if (handle.Allocated || handle.Present || !handle.CachePresent || handle.CacheMatches
                    || handle.Weakness != (candidate.Item2 == 16 ? candidate.Item1.Enumerator.Weakness : candidate.Item1.WrapperHandle.Weakness))
                    throw Error("idle handle changed after its exact Canon eligibility check");
                ResidentScriptingHandles.ClearInactiveCache(pointer, admitted);
            }
            if (observed.Snapshot.ManagerPadding != 0) {
                admitted(); var pad = IntPtr.Add(observed.manager, 76);
                if (M.U32(M.Read(pad, 4), 0) != observed.Snapshot.ManagerPadding) throw Error("manager alignment bytes changed before Canon");
                M.Write(pad, new byte[4]);
                if (M.U32(M.Read(pad, 4), 0) != 0) throw Error("manager alignment bytes did not normalize");
            }
            admitted(); // The caller must recapture all roots after common Canon.
        }

        internal static bool OwnsManager(ResidentNativeGlobalObjects.NativeClass type)
            => type != null && type.Rva == 0x19613a0 && type.Name == "DelayedCallManager" && type.Namespace == ""
                && type.PersistentTypeId == 98 && type.Size == 80 && type.GlobalManager && type.ObjectDerived && !type.Abstract && !type.Stripped;

        internal static void RequireManagerBinding(ResidentNativeGlobalObjects.CaptureResult globals, CaptureResult actual)
        {
            var key = globals.Snapshot.Classes.SingleOrDefault(OwnsManager)?.Key ?? throw Error("DelayedCallManager RTTI is absent or ambiguous");
            if (!globals.ManagerPointers.TryGetValue("resident-native-global/" + key, out var pointer) || pointer != actual.manager)
                throw Error("scheduler body and native Object registration identify different managers");
        }

        internal static void RequireCanonicalState(Data source)
        {
            Validate(source);
            if (source.ManagerPadding != 0 || source.CoverageGaps.Length != 0)
                throw Error("scheduler padding or coverage differs: " + string.Join(",", source.CoverageGaps));
        }

        // This classifies the actual immutable handle captures and their raw
        // coroutine witnesses. A gap-name prefix alone never grants eligibility.
        // CanonicalizeIdleStorage additionally revalidates live memory before
        // any write and reads each exact eligible handle immediately before it.
        internal static void RequireCanonicalizableCoverage(CaptureResult observed)
        { _ = CanonicalizableIdleHandles(observed); }

        private static List<Tuple<RoutineLive, int>> CanonicalizableIdleHandles(CaptureResult observed)
        {
            if (observed == null) throw new ArgumentNullException(nameof(observed));
            Validate(observed.Snapshot);
            if (observed.executionBinding == null || observed.routines.Count != observed.Snapshot.Routines.Length
                || !observed.Snapshot.CoverageGaps.SequenceEqual(observed.gaps.ToArray())) throw Error("scheduler captured coverage inventory was changed");
            var rows = observed.routines.Values.OrderBy(live => live.Row.Key, StringComparer.Ordinal).ToArray();
            if (rows.Where((live, index) => !ReferenceEquals(live.Row, observed.Snapshot.Routines[index])).Any()) throw Error("scheduler captured coroutine rows lost their actual native owners");
            var eligible = new List<Tuple<RoutineLive, int>>(); var allowedGaps = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var live in rows) {
                var row = live.Row; var bytes = live.Bytes;
                if (live.Pointer == IntPtr.Zero || bytes == null || bytes.Length != CoroutineBytes || !observed.routines.TryGetValue(live.Pointer, out var original)
                    || !ReferenceEquals(original, live) || live.Enumerator == null || live.WrapperHandle == null
                    || row.References != M.I32(bytes, 96) || row.Done != Flag(bytes, 100) || row.EnumeratorBased != Flag(bytes, 128)
                    || row.EnumeratorPresent != live.Enumerator.Present || row.EnumeratorWeakness != live.Enumerator.Weakness
                    || row.WrapperAllocated != live.WrapperHandle.Allocated || row.WrapperWeakness != live.WrapperHandle.Weakness
                    || row.WrapperPresent != (live.Wrapper != null) || !ReferenceEquals(live.Slot.Value, live.Enumerator.Target))
                    throw Error("scheduler coroutine handle facts differ from their native witness");
                var execution = live.Enumerator.Target == null ? null : observed.executionBinding(live.Enumerator.Target);
                if (row.Execution != (execution ?? "") || execution != null && row.Key != Prefix + "execution/" + execution)
                    throw Error("scheduler execution ownership differs from the captured iterator");
                if (live.Enumerator.Present && live.Enumerator.Target is not IEnumerator
                    || live.WrapperHandle.Present && (live.WrapperHandle.Target?.GetType() != typeof(Coroutine) || !ReferenceEquals(live.WrapperHandle.Target, live.Wrapper)))
                    throw Error("scheduler native handle target type or identity is unsupported");
                if (live.Wrapper != null && ((IntPtr)WrapperPointer.GetValue(live.Wrapper)! != live.Pointer
                    || !observed.wrappers.TryGetValue(live.Wrapper, out var nativeWrapper) || nativeWrapper != live.Pointer)) throw Error("scheduler stored wrapper is not its actual native owner");
                foreach (var offset in new[] { 16, 40 }) {
                    var handle = offset == 16 ? live.Enumerator : live.WrapperHandle;
                    var token = M.U64(bytes, offset); var allocated = token != ulong.MaxValue; var cache = M.Ptr(bytes, offset + 16) != IntPtr.Zero;
                    if (allocated && (token == 0 || token > uint.MaxValue) || handle.Allocated != allocated
                        || handle.Weakness != M.I32(bytes, offset + 8) || handle.Weakness < 0 || handle.Weakness > 2 || handle.CachePresent != cache
                        || allocated && (!handle.Present || handle.Weakness == 0) || !allocated && handle.Present
                        || !allocated && handle.CacheMatches == cache || allocated && handle.Weakness == 1 && handle.CacheMatches == cache
                        || allocated && handle.Weakness == 2 && handle.CacheMatches && !cache)
                        throw Error("scheduler scripting handle capture does not describe its actual native allocation/cache state");
                    if (handle.CacheMatches) continue;
                    if (execution != null || allocated || handle.Present || !cache) throw Error("scheduler active, executing or mismatched handle cannot be canonicalized as idle storage");
                    eligible.Add(Tuple.Create(live, offset)); allowedGaps.Add("scheduler-handle-cache-needs-canon:" + row.Key);
                }
            }
            if (!observed.Snapshot.CoverageGaps.SequenceEqual(allowedGaps.ToArray()))
                throw Error("scheduler has coverage gaps outside exact inactive handle-cache Canon: " + string.Join(";", observed.Snapshot.CoverageGaps.Except(allowedGaps).Take(16)));
            return eligible;
        }

        internal static void Validate(Data data)
        {
            if (data == null || data.Version != 1 || data.Owners == null || data.Routines == null || data.Callbacks == null || data.ReleasedWrappers == null
                || data.CoverageGaps == null || data.Owners.Length > Maximum || data.Routines.Length > Maximum || data.Callbacks.Length > Maximum
                || data.ReleasedWrappers.Length > Maximum || data.CoverageGaps.Length > Maximum || data.CoverageGaps.Any(string.IsNullOrEmpty)) throw Error("scheduler payload schema differs");
            var owners = new HashSet<string>(StringComparer.Ordinal); var routines = new Dictionary<string, Routine>(StringComparer.Ordinal); var wrappers = new HashSet<string>(StringComparer.Ordinal);
            string? last = null;
            foreach (var row in data.Owners) {
                if (row == null || !Text(row.Binding) || row.Order == null || row.Order.Length > Maximum || !owners.Add(row.Binding)
                    || last != null && StringComparer.Ordinal.Compare(last, row.Binding) >= 0) throw Error("scheduler owner inventory is not canonical");
                last = row.Binding;
            }
            last = null;
            foreach (var row in data.Routines) {
                if (row == null || !Text(row.Key) || !row.Key.StartsWith(Prefix, StringComparison.Ordinal) || row.Owner == null || row.Execution == null || row.Continue == null
                    || row.Waiting == null || row.Method == null || row.MoveNext == null || row.Current == null || row.References < 0 || row.References > Maximum
                    || row.EnumeratorWeakness < 0 || row.EnumeratorWeakness > 2 || row.WrapperWeakness < 0 || row.WrapperWeakness > 2
                    || row.EnumeratorPresent && row.EnumeratorWeakness == 0 || row.WrapperAllocated && (!row.WrapperPresent || row.WrapperWeakness == 0)
                    || row.Owner.Length != 0 && !owners.Contains(row.Owner) || last != null && StringComparer.Ordinal.Compare(last, row.Key) >= 0 || routines.ContainsKey(row.Key)) throw Error("scheduler coroutine schema differs");
                routines.Add(row.Key, row); last = row.Key;
                if (row.WrapperPresent && !wrappers.Add(Wrapper(row.Key))) throw Error("scheduler wrapper is multiply bound");
            }
            var linked = new HashSet<string>(StringComparer.Ordinal);
            foreach (var owner in data.Owners) foreach (var key in owner.Order)
                if (key == null || !routines.TryGetValue(key, out var routine) || routine.Owner != owner.Binding || !linked.Add(key)) throw Error("scheduler owner list closure differs");
            foreach (var row in data.Routines) {
                if (row.Continue.Length != 0 && (!routines.TryGetValue(row.Continue, out var continued) || continued.Waiting != row.Key)
                    || row.Waiting.Length != 0 && (!routines.TryGetValue(row.Waiting, out var waiting) || waiting.Continue != row.Key)) throw Error("scheduler wait/continue graph differs");
                if (row.References == 0 && (!row.Done || linked.Contains(row.Key) || row.EnumeratorPresent || row.Continue.Length != 0 || row.Waiting.Length != 0)) throw Error("terminal coroutine still owns live execution state");
            }
            foreach (var key in data.ReleasedWrappers) if (!Text(key) || !key.StartsWith(Prefix + "released/", StringComparison.Ordinal) || !wrappers.Add(key)) throw Error("released wrapper binding differs");
            var nodes = data.Callbacks; var executionCallbacks = new HashSet<string>(StringComparer.Ordinal);
            if (data.TreeRoot < -1 || data.TreeRoot >= nodes.Length || data.Next < -1 || data.Next >= nodes.Length || (nodes.Length == 0) != (data.TreeRoot == -1)) throw Error("scheduler tree root/iterator is outside its nodes");
            for (var i = 0; i < nodes.Length; i++) {
                var row = nodes[i];
                if (row == null || row.Owner == null || row.Kind == null || row.Routine == null || row.MethodName == null || row.Execution == null || row.Repeat < 0 || row.Repeat > 1 || row.Color < 0 || row.Color > 1
                    || row.Left < -1 || row.Left >= nodes.Length || row.Parent < -1 || row.Parent >= nodes.Length || row.Right < -1 || row.Right >= nodes.Length
                    || row.Mode < 0 || (row.Mode & ~31) != 0 || double.IsNaN(BitConverter.Int64BitsToDouble(row.Time)) || double.IsInfinity(BitConverter.Int64BitsToDouble(row.Time))) throw Error("scheduler callback schema differs");
                if (i > 0 && BitConverter.Int64BitsToDouble(nodes[i - 1].Time) > BitConverter.Int64BitsToDouble(row.Time)) throw Error("scheduler callback order differs");
                if (row.Kind == "coroutine") {
                    if (row.Call != 0x8a6c70 || row.Cleanup != 0x8a6950 || !routines.TryGetValue(row.Routine, out var routine) || row.Owner != routine.Owner
                        || row.MethodName.Length != 0 || (row.Execution.Length != 0) != (routine.Execution.Length != 0)) throw Error("coroutine callback type/ownership differs");
                } else if (row.Kind == "start") {
                    if (row.Call != 0x8ac680 || row.Cleanup != 0 || !owners.Contains(row.Owner) || row.Routine.Length != 0 || row.MethodName.Length != 0 || row.Execution.Length != 0) throw Error("Start callback type differs");
                } else if (row.Kind == "invoke") {
                    if (row.Call != 0x8c0ea0 || row.Cleanup != 0x8c0f00 || !owners.Contains(row.Owner) || row.Routine.Length != 0 || !Text(row.MethodName) || row.Execution.Length != 0) throw Error("Invoke callback type differs");
                } else if (row.Kind != "unsupported" || !data.CoverageGaps.Contains("scheduler-callback-native-data-type:" + row.Call.ToString("x") + "/" + row.Cleanup.ToString("x"))) throw Error("unknown callback type was marked covered");
                if (row.Execution.Length != 0 && !executionCallbacks.Add(row.Execution)) throw Error("executing callback binding is not unique");
            }
            var visited = new HashSet<int>(); var order = new List<int>();
            int Walk(int index, int parent, int depth) {
                if (index == -1) return 1;
                if (depth > 128 || !visited.Add(index) || nodes[index].Parent != parent) throw Error("scheduler callback tree is cyclic or has a different parent");
                var row = nodes[index]; var left = Walk(row.Left, index, depth + 1); order.Add(index); var right = Walk(row.Right, index, depth + 1);
                if (left != right || row.Color == 0 && (row.Left >= 0 && nodes[row.Left].Color == 0 || row.Right >= 0 && nodes[row.Right].Color == 0)) throw Error("scheduler red/black balance differs");
                return left + row.Color;
            }
            Walk(data.TreeRoot, -1, 0);
            if (data.TreeRoot >= 0 && nodes[data.TreeRoot].Color != 1 || !order.SequenceEqual(Enumerable.Range(0, nodes.Length))) throw Error("scheduler tree topology/order differs");
        }

        private static string NormalizeOwnerKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            var s = System.Text.RegularExpressions.Regex.Replace(key, @"runtime-driver/[0-9a-fA-F\-]+(?:/[0-9a-fA-F]+)?", "runtime-driver/*");
            return System.Text.RegularExpressions.Regex.Replace(s, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}(?:[0-9a-fA-F]{8})?", "*");
        }

        private static string ReadCString(CaptureResult live, IntPtr pointer)
        {
            var bytes = new List<byte>();
            for (var i = 0; i <= 16384; i++) { var value = live.Read(IntPtr.Add(pointer, i), 1)[0]; if (value == 0) return new UTF8Encoding(false, true).GetString(bytes.ToArray()); bytes.Add(value); }
            throw Error("native callback string exceeds its bound");
        }
        private static string Method(IntPtr pointer) => pointer == IntPtr.Zero ? "" : Metadata.BindMetadata(ResidentScriptingHandles.ReadMethod(pointer)!)!;
        private static MethodInfo? ResolveMethod(string value)
        { if (value.Length == 0) return null; return Metadata.TryResolveMetadata(value, out var method) && method is MethodInfo actual ? actual : throw Error("native method metadata differs"); }
        private static string Root(string key) => key + "/enumerator";
        private static string Wrapper(string key) => key + "/wrapper";
        private static Coroutine NewWrapper(IntPtr pointer)
        {
            var wrapper = (Coroutine)FormatterServices.GetUninitializedObject(typeof(Coroutine));
            // ReleaseCoroutine jumps to CleanupCoroutineGC with no null guard.
            // An empty or detached old wrapper must never finalize that pointer.
            GC.SuppressFinalize(wrapper); WrapperPointer.SetValue(wrapper, pointer);
            if (pointer != IntPtr.Zero) GC.ReRegisterForFinalize(wrapper);
            return wrapper;
        }
        private static bool Flag(byte[] bytes, int offset) => bytes[offset] <= 1 ? bytes[offset] == 1 : throw Error("native coroutine boolean differs");
        private static int Rva(IntPtr pointer, IntPtr engine)
        { if (pointer == IntPtr.Zero) return 0; var offset = pointer.ToInt64() - engine.ToInt64(); return offset > 0 && offset < 32 * 1024 * 1024 ? (int)offset : throw Error("callback is outside pinned engine"); }
        private static bool Text(string value) => value != null && value.Length > 0 && value.Length <= 16384 && value.IndexOf('\0') < 0;
        private static void Copy(byte[] target, int at, byte[] bytes) => Buffer.BlockCopy(bytes, 0, target, at, bytes.Length);
        private static void Pointer(IntPtr at, IntPtr value) => M.Write(at, BitConverter.GetBytes(value.ToInt64()));
        private static T Entry<T>(IntPtr engine, int rva) where T : class => (T)(object)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, rva), typeof(T));
        private static void RequireEntries(IntPtr engine)
        {
            foreach (var row in new[] {
                Tuple.Create(0x629ef0, "b908000000e9068ef4ff"), Tuple.Create(0x572d00, "4863c1488d0d264d4601488b04c1c3"),
                Tuple.Create(0x8a6800, "48895c2408574883ec2033db488bf9"), Tuple.Create(0x567ff0, "40534883ec40488bd9488b0d18a94501"),
                Tuple.Create(0x1551d80, "e98b6301ff"), Tuple.Create(0x627ca0, "48895c2408488974241048897c241841564883ec40"),
                Tuple.Create(0x62ab10, "4883ec28488b4140488bd1493bc0"), Tuple.Create(0x1587a48, "48895c24084889742410574883ec30488bf9"),
                Tuple.Create(0x1587ac0, "c7442410000000008b442410e9c3c60100") })
                if (BitConverter.ToString(M.Read(IntPtr.Add(engine, row.Item1), row.Item2.Length / 2)).Replace("-", "").ToLowerInvariant() != row.Item2) throw Error("scheduler native entry ABI differs: " + row.Item1.ToString("x"));
        }
        private sealed class References : IEqualityComparer<object>
        { internal static readonly References Instance = new References(); public new bool Equals(object? x, object? y) => ReferenceEquals(x, y); public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj); }
        private sealed class References<T> : IEqualityComparer<T> where T : class
        { internal static readonly References<T> Instance = new References<T>(); public bool Equals(T? x, T? y) => ReferenceEquals(x, y); public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj); }
        private static InvalidDataException Error(string reason) => new InvalidDataException("Resident scheduler: " + reason + ".");
    }
}
