using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Object's native header is shared by components, assets and engine global
    // managers. Instance IDs, allocator labels, GC handles and addresses remain
    // target-local; event payloads contain logical owner bindings only.
    internal static class ResidentObjectNativeState
    {
        private const int Maximum = ResidentObjectDirectory.MaximumComponents;
        internal sealed class GlobalOwner
        {
            internal readonly string Key, NativeClass;
            internal readonly IntPtr Pointer;
            internal readonly NativeObject? Managed;
            internal GlobalOwner(string key, string nativeClass, IntPtr pointer, NativeObject? managed = null)
            { Key = key; NativeClass = nativeClass; Pointer = pointer; Managed = managed; }
        }
        internal sealed class Data
        {
            public int Version = 1;
            public Row[] Rows = Array.Empty<Row>();
            public string[][] ActiveLists = new[] { Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>() };
            public string[] CoverageGaps = Array.Empty<string>();
        }
        internal sealed class Row
        {
            public string Owner = "", Type = "", GameObject = "", HandleTarget = "";
            public int Vtable, NativeTypeIndex, Weakness, Kind;
            public uint Flags;
            public bool Execution, HandleAllocated, CachePresent, CacheMatches;
            public Event[] Events = Array.Empty<Event>();
            public GameObjectData? Object;
        }
        internal sealed class Event
        {
            public int Callback;
            public string UserData = "";
        }
        internal sealed class GameObjectData
        {
            public int ComponentCapacity;
            public string[] Components = Array.Empty<string>();
            public uint[] Types = Array.Empty<uint>();
            public byte[] State = Array.Empty<byte>(); // layer/tag/active/cache/activation/messages: native +80..95
        }
        private sealed class LocalOwner
        {
            internal string Key = "", Type = "";
            internal IntPtr Pointer;
            internal NativeObject? Managed;
            internal bool Execution;
            internal int Kind; // 0 asset/global Object, 1 component, 2 GameObject
        }
        private sealed class Live
        {
            internal readonly Dictionary<string, LocalOwner> Owners = new Dictionary<string, LocalOwner>(StringComparer.Ordinal);
            internal readonly Dictionary<IntPtr, string> Reverse = new Dictionary<IntPtr, string>();
            internal readonly List<Tuple<IntPtr, byte[]>> Witness = new List<Tuple<IntPtr, byte[]>>();
            internal readonly List<ResidentScriptingHandles.Capture> Handles = new List<ResidentScriptingHandles.Capture>();
            internal IntPtr Engine, Manager;
            internal int EngineLength;
            internal Data Data = new Data();
            internal byte[] Read(IntPtr pointer, int length) { var value = M.Read(pointer, length); Witness.Add(Tuple.Create(pointer, value)); return value; }
            internal IntPtr ResolveAnimator(string key)
            {
                if (!Owners.TryGetValue(key,out var owner) || owner.Managed?.GetType() != typeof(Animator)) return IntPtr.Zero;
                var vtable = M.Ptr(Read(owner.Pointer,8),0).ToInt64() - Engine.ToInt64();
                return vtable > 0 && vtable < EngineLength && ResidentAnimatorState.IsBinderOwnerVtable((int)vtable) ? owner.Pointer : IntPtr.Zero;
            }
            internal string? IdentifyAnimator(IntPtr pointer) => Reverse.TryGetValue(pointer,out var key) && ResolveAnimator(key) == pointer ? key : null;
            internal void Frozen() {
                foreach (var pair in Witness) if (!M.Read(pair.Item1, pair.Item2.Length).SequenceEqual(pair.Item2)) throw Error("native object observation changed");
                foreach (var handle in Handles) handle.RequireUnchanged();
            }
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ObjectEvent(IntPtr owner, IntPtr callback, IntPtr userData);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void InsertBefore(IntPtr head, IntPtr node);

        internal static Data Capture(ResidentObjectDirectory directory, IReadOnlyDictionary<string, NativeObject> assets, IEnumerable<GlobalOwner> globals)
        { var live = Read(directory, assets, globals); live.Frozen(); return live.Data; }
        private static Live Read(ResidentObjectDirectory directory, IReadOnlyDictionary<string, NativeObject> assets, IEnumerable<GlobalOwner> globals)
        {
            if (directory == null || assets == null || globals == null) throw new ArgumentNullException();
            var engine = LoadingEngineClockProbe.RequirePinnedEngine(); var live = new Live { Engine = engine.BaseAddress, EngineLength = engine.ModuleMemorySize };
            void Add(LocalOwner owner) {
                if (string.IsNullOrEmpty(owner.Key) || string.IsNullOrEmpty(owner.Type) || owner.Pointer == IntPtr.Zero || live.Owners.Count >= Maximum
                    || live.Owners.ContainsKey(owner.Key) || live.Reverse.ContainsKey(owner.Pointer)) throw Error("native owner directory is absent, oversized or aliased");
                live.Owners.Add(owner.Key, owner); live.Reverse.Add(owner.Pointer, owner.Key);
            }
            foreach (var pair in directory.NativeBindings.Concat(assets)) Add(new LocalOwner { Key = pair.Key,
                Type = ResidentObjectDirectory.TypeRecord.Capture(pair.Value.GetType()).Key, Pointer = M.Owner(pair.Value), Managed = pair.Value,
                Execution = directory.Ownership.Owns(pair.Value), Kind = pair.Value is GameObject ? 2 : pair.Value is Component ? 1 : 0 });
            foreach (var global in globals) {
                if (!ReferenceEquals(global.Managed, null) && M.Owner(global.Managed!) != global.Pointer) throw Error("global wrapper does not own its registered native object");
                Add(new LocalOwner { Key = global.Key, Type = "native/" + global.NativeClass, Pointer = global.Pointer, Managed = global.Managed });
            }
            var gaps = new SortedSet<string>(StringComparer.Ordinal); var rows = new List<Row>();
            var eventNodes = new HashSet<IntPtr>();
            foreach (var owner in live.Owners.Values.OrderBy(value => value.Key, StringComparer.Ordinal)) {
                var bytes = live.Read(owner.Pointer, owner.Kind == 2 ? 128 : owner.Kind == 1 ? 56 : 48);
                var flags = M.U32(bytes, 12); var vtable = M.Ptr(bytes, 0).ToInt64() - live.Engine.ToInt64();
                if (vtable <= 0 || vtable >= live.EngineLength) throw Error("native owner vtable is outside the pinned engine");
                var handle = ResidentScriptingHandles.Read(IntPtr.Add(owner.Pointer, 24)); live.Handles.Add(handle);
                string handleTarget = "";
                if (handle.Target != null) {
                    if (owner.Managed != null && ReferenceEquals(handle.Target, owner.Managed)) handleTarget = owner.Key;
                    else gaps.Add(owner.Key + "/native-managed-reference-not-owned-wrapper");
                }
                if (handle.Allocated && !handle.Present) gaps.Add(owner.Key + "/collected-native-weak-handle");
                if (handle.Allocated && !handle.CacheMatches) gaps.Add(owner.Key + "/live-native-handle-cache-alias");
                var events = new List<Event>(); var cursor = M.Ptr(bytes, 16);
                while (cursor != IntPtr.Zero) {
                    if (events.Count >= 4096 || !eventNodes.Add(cursor)) throw Error("native event graph is cyclic, shared or oversized");
                    var entry = live.Read(cursor, 32); var callback = M.Ptr(entry, 16).ToInt64() - live.Engine.ToInt64();
                    if (M.I32(entry, 24) != 1) throw Error("native event capture overlaps an active dispatch");
                    if (callback <= 0 || callback >= live.EngineLength) { gaps.Add(owner.Key + "/event-callback-outside-pinned-engine"); callback = 0; }
                    var userData = M.Ptr(entry, 0); var binding = "";
                    if (ResidentAnimatorState.IsBinderCallback((int)callback)) {
                        if (!ResidentAnimatorState.TryBindBinderUserData((int)callback, owner.Pointer, userData, live.IdentifyAnimator, live.Read, out binding)) {
                            gaps.Add(owner.Key + "/animator-binder-event-receiver-unresolved"); binding = "unresolved";
                        }
                    } else if (userData != IntPtr.Zero && !live.Reverse.TryGetValue(userData, out binding)) {
                        gaps.Add(owner.Key + "/event-user-data-not-native-owner"); binding = "unresolved";
                    }
                    events.Add(new Event { Callback = (int)callback, UserData = binding }); cursor = M.Ptr(entry, 8);
                }
                var row = new Row { Owner = owner.Key, Type = owner.Type, Kind = owner.Kind, Execution = owner.Execution,
                    Vtable = (int)vtable, NativeTypeIndex = (int)(flags >> 21), Flags = flags & 0x1ff000,
                    HandleAllocated = handle.Allocated, Weakness = handle.Weakness, HandleTarget = handleTarget,
                    CachePresent = handle.CachePresent, CacheMatches = handle.CacheMatches, Events = events.ToArray() };
                if (owner.Kind == 1) {
                    var gameObject = M.Ptr(bytes, 48);
                    if (!live.Reverse.TryGetValue(gameObject, out row.GameObject) || live.Owners[row.GameObject].Kind != 2)
                        throw Error("Component native GameObject is outside the complete directory");
                }
                if (owner.Kind == 2) {
                    var count64 = M.U64(bytes, 64); var cap64 = M.U64(bytes, 72) >> 1;
                    if (count64 == 0 || count64 > Maximum || count64 > cap64 || cap64 > Maximum) throw Error("GameObject component array exceeds bounds");
                    var count = (int)count64; var pairs = live.Read(M.Ptr(bytes, 48), checked(count * 16));
                    var componentKeys = new string[count]; var types = new uint[count];
                    for (var index = 0; index < count; index++) {
                        if (!live.Reverse.TryGetValue(M.Ptr(pairs, index * 16 + 8), out componentKeys[index])
                            || live.Owners[componentKeys[index]].Kind != 1) throw Error("GameObject component slot is unbound");
                        types[index] = M.U32(pairs, index * 16);
                    }
                    row.Object = new GameObjectData { ComponentCapacity = (int)cap64, Components = componentKeys, Types = types, State = M.Slice(bytes, 80, 16) };
                    if (M.Ptr(bytes, 120) != owner.Pointer) throw Error("active GameObject node has another owner");
                }
                rows.Add(row);
            }
            live.Manager = M.Ptr(live.Read(IntPtr.Add(live.Engine, 0x19d7cf0), 8), 0);
            if (live.Manager == IntPtr.Zero) throw Error("GameObjectManager is absent");
            var lists = new string[3][]; var listed = new HashSet<string>(StringComparer.Ordinal);
            for (var lane = 0; lane < 3; lane++) {
                var head = IntPtr.Add(live.Manager, lane * 16); var header = live.Read(head, 16);
                var previous = head; var cursor = M.Ptr(header, 8); var members = new List<string>();
                while (cursor != head) {
                    if (cursor == IntPtr.Zero || members.Count >= Maximum) throw Error("GameObject active list is unbounded or open");
                    var node = live.Read(cursor, 24);
                    if (M.Ptr(node, 0) != previous || !live.Reverse.TryGetValue(M.Ptr(node, 16), out var key)
                        || live.Owners[key].Kind != 2 || IntPtr.Add(live.Owners[key].Pointer, 104) != cursor || !listed.Add(key))
                        throw Error("GameObject active-list linkage/ownership differs");
                    members.Add(key); previous = cursor; cursor = M.Ptr(node, 8);
                }
                if (M.Ptr(header, 0) != previous) throw Error("GameObject active-list tail differs"); lists[lane] = members.ToArray();
            }
            foreach (var owner in live.Owners.Values.Where(value => value.Kind == 2)) {
                var node = live.Read(IntPtr.Add(owner.Pointer, 104), 16);
                if (listed.Contains(owner.Key) != (M.Ptr(node, 0) != IntPtr.Zero)
                    || (M.Ptr(node, 0) == IntPtr.Zero) != (M.Ptr(node, 8) == IntPtr.Zero)) throw Error("GameObject active membership is incomplete");
            }
            live.Data = new Data { Rows = rows.ToArray(), ActiveLists = lists, CoverageGaps = gaps.ToArray() };
            Validate(live.Data); live.Frozen(); return live;
        }

        internal static void Canon(ResidentObjectDirectory directory, IReadOnlyDictionary<string, NativeObject> assets,
            IEnumerable<GlobalOwner> globals, ResidentMutationScope scope, Action admitted)
        {
            var live = Read(directory, assets, globals); scope.Require(); admitted();
            foreach (var row in live.Data.Rows.Where(value => !value.Execution)) {
                var owner = live.Owners[row.Owner];
                // Only inactive cached GC roots and unused component storage
                // are S0. Actual flags, event order and activity remain D.
                if (!row.HandleAllocated && row.CachePresent) ResidentScriptingHandles.ClearInactiveCache(IntPtr.Add(owner.Pointer, 24), admitted);
                if (row.Object != null) M.Reserve(IntPtr.Add(owner.Pointer, 48), row.Object.Components.Length, 16, 8, admitted);
            }
            var after = Read(directory, assets, globals);
            RequireCanonPreserved(live.Data, after.Data); admitted();
        }
        private static void RequireCanonPreserved(Data before, Data after)
        {
            var expected = Copy(before);
            foreach (var row in expected.Rows.Where(value => !value.Execution)) {
                if (!row.HandleAllocated) { row.CachePresent = false; row.CacheMatches = true; }
                if (row.Object != null) row.Object.ComponentCapacity = row.Object.Components.Length;
            }
            RequireEqual(expected, after);
        }

        internal sealed class Plan
        {
            private readonly Data source;
            private readonly ResidentObjectDirectory directory;
            private readonly IReadOnlyDictionary<string, NativeObject> assets;
            private readonly GlobalOwner[] globals;
            private readonly ResidentMutationScope scope;
            private readonly Action admitted;
            private readonly Live before;
            private bool applied;
            internal Plan(Data source, ResidentObjectDirectory directory, IReadOnlyDictionary<string, NativeObject> assets,
                IEnumerable<GlobalOwner> globals, ResidentMutationScope scope, Action admitted)
            {
                Validate(source); RequireComplete(source); this.source = Copy(source); this.directory = directory; this.assets = assets;
                this.globals = globals.ToArray(); this.scope = scope; this.admitted = admitted;
                admitted(); before = Read(directory, assets, this.globals); RequireComplete(before.Data);
                if (!this.source.Rows.Select(row => row.Owner).SequenceEqual(before.Data.Rows.Select(row => row.Owner))) throw Error("native base owner directory differs");
                for (var index = 0; index < this.source.Rows.Length; index++) {
                    var wanted = this.source.Rows[index]; var actual = before.Data.Rows[index];
                    if (wanted.Events.Any(entry => entry.Callback >= before.EngineLength)) throw Error("source event callback exceeds the pinned engine");
                    foreach (var entry in wanted.Events) _ = UserData(before.Owners[actual.Owner].Pointer,entry);
                    if (wanted.Type != actual.Type || wanted.Kind != actual.Kind || wanted.Execution != actual.Execution
                        || wanted.Vtable != actual.Vtable || wanted.NativeTypeIndex != actual.NativeTypeIndex || wanted.GameObject != actual.GameObject)
                        throw Error("native base owner type, vtable or structural dependency differs");
                    if (wanted.Execution && Json(wanted) != Json(actual)) throw Error("execution native owner must already match exactly");
                    if (wanted.Object != null && (!wanted.Object.Components.SequenceEqual(actual.Object!.Components)
                        || !wanted.Object.Types.SequenceEqual(actual.Object.Types))) throw Error("native component directory differs after topology");
                }
                var execution = new HashSet<string>(this.source.Rows.Where(row => row.Execution).Select(row => row.Owner), StringComparer.Ordinal);
                for (var lane = 0; lane < 3; lane++)
                    if (!this.source.ActiveLists[lane].Where(execution.Contains).SequenceEqual(before.Data.ActiveLists[lane].Where(execution.Contains)))
                        throw Error("execution active-GameObject order differs");
            }
            internal void Apply()
            {
                if (applied) throw Error("native base plan is single use"); applied = true; scope.Require(); admitted(); before.Frozen();
                var add = Entry<ObjectEvent>(before.Engine, 0x5790e0, "48895c24084889742410574883ec20");
                var remove = Entry<ObjectEvent>(before.Engine, 0x57a6b0, "48895c24084889742410574883ec20");
                foreach (var row in source.Rows.Where(value => !value.Execution)) {
                    scope.Require(); admitted(); var local = before.Owners[row.Owner];
                    var old = before.Data.Rows.Single(value => value.Owner == row.Owner);
                    foreach (var entry in old.Events) remove(local.Pointer, IntPtr.Add(before.Engine, entry.Callback), UserData(local.Pointer,entry));
                    if (M.Ptr(IntPtr.Add(local.Pointer, 16)) != IntPtr.Zero) throw Error("native event removal did not empty the owned chain");
                    for (var index = row.Events.Length - 1; index >= 0; index--) {
                        var entry = row.Events[index]; add(local.Pointer, IntPtr.Add(before.Engine, entry.Callback), UserData(local.Pointer,entry));
                    }
                    var flags = M.U32(M.Read(IntPtr.Add(local.Pointer, 12), 4), 0);
                    M.Write(IntPtr.Add(local.Pointer, 12), BitConverter.GetBytes((flags & ~0x1ff000U) | row.Flags));
                    ResidentScriptingHandles.Apply(IntPtr.Add(local.Pointer, 24), row.HandleTarget.Length == 0 ? null : local.Managed,
                        row.Weakness, admitted);
                    if (row.Object != null) {
                        M.Reserve(IntPtr.Add(local.Pointer, 48), row.Object.ComponentCapacity, 16, 8, admitted);
                        M.Write(IntPtr.Add(local.Pointer, 80), row.Object.State);
                    }
                }
                // Rebuild only target-local intrusive links. Foreign/execution
                // relative order was independently checked above.
                foreach (var owner in before.Owners.Values.Where(value => value.Kind == 2)) {
                    var node = IntPtr.Add(owner.Pointer, 104); var bytes = M.Read(node, 16); var previous = M.Ptr(bytes, 0); var next = M.Ptr(bytes, 8);
                    if (previous == IntPtr.Zero) continue;
                    M.Write(IntPtr.Add(previous, 8), BitConverter.GetBytes(next.ToInt64())); M.Write(next, BitConverter.GetBytes(previous.ToInt64()));
                    M.Write(node, new byte[16]);
                }
                var insert = Entry<InsertBefore>(before.Engine, 0x289bf0, "483bd174384c8b024d85c0");
                for (var lane = 0; lane < 3; lane++) foreach (var key in source.ActiveLists[lane]) {
                    admitted(); insert(IntPtr.Add(before.Manager, lane * 16), IntPtr.Add(before.Owners[key].Pointer, 104));
                }
                Verify();
            }
            private IntPtr UserData(IntPtr receiver, Event entry)
            {
                if (ResidentAnimatorState.IsBinderCallback(entry.Callback)) {
                    if (!ResidentAnimatorState.TryResolveBinderUserData(entry.Callback,receiver,entry.UserData,before.ResolveAnimator,M.Read,out var binder))
                        throw Error("target Animator binder does not contain this native receiver");
                    return binder;
                }
                return entry.UserData.Length == 0 ? IntPtr.Zero : before.Owners[entry.UserData].Pointer;
            }
            internal void Verify()
            { if (!applied) throw Error("native base verification precedes application"); scope.Require(); admitted(); RequireEqual(source, Read(directory, assets, globals).Data); }
        }
        internal static Plan Prepare(Data source, ResidentObjectDirectory directory, IReadOnlyDictionary<string, NativeObject> assets,
            IEnumerable<GlobalOwner> globals, ResidentMutationScope scope, Action admitted) => new Plan(source, directory, assets, globals, scope, admitted);

        internal static void Validate(Data value)
        {
            if (value == null || value.Version != 1 || value.Rows == null || value.Rows.Length > Maximum || value.ActiveLists == null
                || value.ActiveLists.Length != 3 || value.CoverageGaps == null) throw Error("native base snapshot shape differs");
            var keys = new Dictionary<string, Row>(StringComparer.Ordinal); string? previous = null;
            foreach (var row in value.Rows) {
                if (row == null || string.IsNullOrEmpty(row.Owner) || row.Owner.Length > 16384 || string.IsNullOrEmpty(row.Type) || row.Type.Length > 16384
                    || previous != null && StringComparer.Ordinal.Compare(previous, row.Owner) >= 0 || row.Vtable <= 0 || row.NativeTypeIndex < 0 || row.NativeTypeIndex > 2047
                    || (row.Flags & ~0x1ff000U) != 0 || row.Kind < 0 || row.Kind > 2 || row.Weakness < 0 || row.Weakness > 2
                    || row.HandleTarget == null || row.HandleTarget.Length != 0 && (row.HandleTarget != row.Owner || !row.HandleAllocated || row.Weakness == 0)
                    || row.GameObject == null || row.Kind == 1 != (row.GameObject.Length != 0) || row.Kind == 2 != (row.Object != null)
                    || row.Events == null || row.Events.Length > 4096) throw Error("native base owner fields differ");
                previous = row.Owner; keys.Add(row.Owner, row);
                if (row.Object != null && (row.Object.Components == null || row.Object.Components.Length == 0 || row.Object.Components.Length > Maximum
                    || row.Object.ComponentCapacity < row.Object.Components.Length || row.Object.ComponentCapacity > Maximum || row.Object.Types == null
                    || row.Object.Types.Length != row.Object.Components.Length || row.Object.Components.Distinct(StringComparer.Ordinal).Count() != row.Object.Components.Length
                    || row.Object.State == null || row.Object.State.Length != 16 || row.Object.State[6] > 1 || row.Object.State[7] != 0xff && row.Object.State[7] > 1))
                    throw Error("native GameObject component/state shape differs");
            }
            foreach (var row in value.Rows) {
                if (row.Kind == 1 && (!keys.TryGetValue(row.GameObject, out var parent) || parent.Kind != 2 || !parent.Object!.Components.Contains(row.Owner)))
                    throw Error("native Component/GameObject reverse binding differs");
                if (row.Object != null && row.Object.Components.Any(key => !keys.TryGetValue(key, out var component) || component.Kind != 1 || component.GameObject != row.Owner))
                    throw Error("native GameObject/Component forward binding differs");
                foreach (var entry in row.Events) {
                    if (entry == null || entry.Callback < 0 || entry.UserData == null
                        || entry.UserData.Length != 0 && entry.UserData != "unresolved" && !keys.ContainsKey(entry.UserData)) throw Error("native event binding differs");
                    if (entry.UserData != "unresolved" && ResidentAnimatorState.IsBinderCallback(entry.Callback)
                        && (!keys.TryGetValue(entry.UserData,out var binderOwner) || binderOwner.Kind != 1 || !ResidentAnimatorState.IsBinderOwnerVtable(binderOwner.Vtable)))
                        throw Error("native Animator binder event has the wrong typed owner");
                }
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var list in value.ActiveLists) {
                if (list == null || list.Length > Maximum) throw Error("native active list is absent or oversized");
                foreach (var key in list) if (!keys.TryGetValue(key, out var owner) || owner.Kind != 2 || !seen.Add(key)) throw Error("native active list owner differs");
            }
        }
        internal static void RequireComplete(Data value)
        {
            Validate(value);
            if (value.CoverageGaps.Length != 0 || value.Rows.Any(row => row.HandleAllocated && (row.HandleTarget.Length == 0 || !row.CacheMatches)
                || row.Events.Any(entry => entry.Callback == 0 || entry.UserData == "unresolved"))) throw Error("native object base contains unsupported references");
        }
        internal static void RequireEqual(Data expected, Data actual)
        { Validate(expected); Validate(actual); if (Json(expected) != Json(actual)) throw Error("complete native Object/GameObject readback differs"); }
        private static string Json(object value) => Newtonsoft.Json.JsonConvert.SerializeObject(value);
        private static Data Copy(Data value) => Newtonsoft.Json.JsonConvert.DeserializeObject<Data>(Json(value)) ?? throw Error("native base snapshot copy failed");
        private static T Entry<T>(IntPtr engine, int rva, string prefix) where T : class
        {
            var pointer = IntPtr.Add(engine, rva);
            if (BitConverter.ToString(M.Read(pointer, prefix.Length / 2)).Replace("-", "").ToLowerInvariant() != prefix) throw Error("native Object entry ABI differs");
            return (T)(object)Marshal.GetDelegateForFunctionPointer(pointer, typeof(T));
        }
        private static InvalidDataException Error(string reason) => new InvalidDataException("Resident native Object: " + reason);
    }
}
