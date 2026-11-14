using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Pinned Transform/RectTransform ABI, independent of object names and
    // component counts. Raw capture deliberately avoids RectTransform getters:
    // those getters may resolve invalid anchors and mutate the observed state.
    internal static class ResidentTransformState
    {
        private const int MaximumOwners = 100000, DispatchGlobal = 0x1a1ddd8;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DispatchHierarchy(IntPtr dispatch, IntPtr hierarchy);
        internal sealed class Data
        {
            public int Version = 1;
            public Node[] Nodes = Array.Empty<Node>();
            public Hierarchy[] Hierarchies = Array.Empty<Hierarchy>();
            public string[] Queue = Array.Empty<string>();
            public ulong CombinedChanged;
            public Registry Systems = new Registry();
            public string[] CoverageGaps = Array.Empty<string>();
        }
        internal sealed class Node
        {
            public string Owner = "", Parent = "", Next = "", Previous = "", Hierarchy = "";
            public bool Rectangle, Execution;
            // Native local q/p/s, SIMD hierarchy t/q/s and the full Rect cache.
            public byte[] Local = Array.Empty<byte>(), Cached = Array.Empty<byte>(), Rect = Array.Empty<byte>();
            public ulong Changed, Interested;
            public uint HierarchyInterested, Descendants;
            public byte TransformType;
        }
        internal sealed class Hierarchy
        {
            public string Key = "";
            public string[] Members = Array.Empty<string>();
            public ulong CombinedChanged, CombinedInterested;
        }
        internal sealed class Registry
        {
            public ulong InUse, Permanent, Deprecated;
            public ulong[] InterestTypes = Array.Empty<ulong>();
            public string[] Names = Array.Empty<string>();
            public uint ImmediateInUse, ImmediatePermanent;
            public int[] ImmediateInterests = Array.Empty<int>(), ImmediateCallbacks = Array.Empty<int>();
            public string[] ImmediateNames = Array.Empty<string>();
            public TypeMask[] TypePermanent = Array.Empty<TypeMask>(), ImmediateTypePermanent = Array.Empty<TypeMask>();
        }
        internal sealed class TypeMask { public uint Type; public ulong Mask; }
        private sealed class Live
        {
            internal Data Data = new Data();
            internal IntPtr Dispatch;
            internal readonly Dictionary<string, LocalNode> Nodes = new Dictionary<string, LocalNode>(StringComparer.Ordinal);
            internal readonly Dictionary<string, IntPtr> Hierarchies = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            internal readonly List<Tuple<IntPtr, byte[]>> Witness = new List<Tuple<IntPtr, byte[]>>();
            internal readonly List<Tuple<IntPtr, string>> Strings = new List<Tuple<IntPtr, string>>();
            internal byte[] Read(IntPtr address, int count)
            { var bytes = M.Read(address, count); Witness.Add(Tuple.Create(address, bytes)); return bytes; }
            internal string String(IntPtr storage)
            { var value = M.String(storage); Strings.Add(Tuple.Create(storage, value)); return value; }
            internal void RequireFrozen()
            {
                foreach (var row in Witness) if (!M.Read(row.Item1, row.Item2.Length).SequenceEqual(row.Item2)) throw Error("native capture changed during observation");
                foreach (var row in Strings) if (M.String(row.Item1) != row.Item2) throw Error("native registry name changed during observation");
            }
        }
        private sealed class LocalNode
        {
            internal Transform Owner = null!;
            internal IntPtr Native, Hierarchy;
            internal int Index;
            internal byte[] Header = Array.Empty<byte>();
        }
        internal sealed class Plan
        {
            private readonly Data source;
            private readonly Live target;
            private readonly ResidentObjectDirectory directory;
            private readonly ResidentMutationScope scope;
            private readonly Action admission;
            private bool used, applied;
            internal Plan(Data saved, ResidentObjectDirectory directory, ResidentMutationScope scope, Action admitted)
            {
                Validate(saved); this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
                this.scope = scope ?? throw new ArgumentNullException(nameof(scope)); admission = admitted ?? throw new ArgumentNullException(nameof(admitted));
                // Freeze caller data so later graph writes cannot change the plan.
                source = Copy(saved); admission(); target = Read(directory); RequireCompatible(source, target.Data);
                foreach (var row in target.Nodes.Values)
                    if (!directory.Ownership.IsExecution(row.Owner) && !scope.Owns(row.Owner)) throw Error("Transform is outside the mutation scope");
                target.RequireFrozen();
            }
            internal void Apply()
            {
                if (used) throw Error("Transform plan is single-use"); used = true; scope.Require(); admission();
                try {
                    var before = Read(directory); RequireEqual(target.Data, before.Data); target.RequireFrozen();
                    var engine = LoadingEngineClockProbe.RequirePinnedEngine();
                    var remove = (DispatchHierarchy)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine.BaseAddress, 0x7e8b80), typeof(DispatchHierarchy));
                    var queue = (DispatchHierarchy)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine.BaseAddress, 0x7e8080), typeof(DispatchHierarchy));
                    // Let the native container own allocation/removal. No source
                    // address, capacity, free-list or allocator header is copied.
                    foreach (var key in before.Data.Queue) { admission(); remove(target.Dispatch, target.Hierarchies[key]); }
                    foreach (var row in source.Nodes) {
                        if (row.Execution) continue;
                        admission(); var local = target.Nodes[row.Owner]; var header = local.Header;
                        M.Write(IntPtr.Add(local.Native, 72), row.Local);
                        if (row.Rectangle) M.Write(IntPtr.Add(local.Native, 184), row.Rect);
                        M.Write(IntPtr.Add(M.Ptr(header, 24), checked(local.Index * 48)), row.Cached);
                        M.Write(IntPtr.Add(M.Ptr(header, 72), checked(local.Index * 8)), BitConverter.GetBytes(row.Changed));
                        M.Write(IntPtr.Add(M.Ptr(header, 80), checked(local.Index * 8)), BitConverter.GetBytes(row.Interested));
                        M.Write(IntPtr.Add(M.Ptr(header, 96), checked(local.Index * 4)), BitConverter.GetBytes(row.HierarchyInterested));
                        M.Write(IntPtr.Add(M.Ptr(header, 104), local.Index), new[] { row.TransformType });
                    }
                    foreach (var row in source.Hierarchies) {
                        var pointer = target.Hierarchies[row.Key];
                        M.Write(IntPtr.Add(pointer, 64), BitConverter.GetBytes(row.CombinedChanged));
                        M.Write(IntPtr.Add(pointer, 88), BitConverter.GetBytes(row.CombinedInterested));
                    }
                    M.Write(target.Dispatch, BitConverter.GetBytes(0UL));
                    foreach (var key in source.Queue) {
                        admission(); var pointer = target.Hierarchies[key];
                        var changed = source.Hierarchies.Single(row => row.Key == key).CombinedChanged;
                        // A queued hierarchy may be stale after its last bit was
                        // consumed. Queue insertion itself only owns this native
                        // container; temporarily supply a bit if needed, restore
                        // the captured value before any observer can run.
                        if ((changed & ~source.Systems.Deprecated) == 0) {
                            var candidates = source.Systems.InUse & ~source.Systems.Deprecated;
                            if (candidates == 0) throw Error("stale Transform queue has no registered insertion bit");
                            M.Write(IntPtr.Add(pointer, 64), BitConverter.GetBytes(changed | (candidates & unchecked(0UL - candidates))));
                        }
                        queue(target.Dispatch, pointer); M.Write(IntPtr.Add(pointer, 64), BitConverter.GetBytes(changed));
                    }
                    // The cached global mask may include a previously consumed
                    // hierarchy bit. Preserve the actual source value exactly.
                    M.Write(target.Dispatch, BitConverter.GetBytes(source.CombinedChanged));
                    applied = true; Verify();
                } catch (Exception error) { scope.Fault(error); throw; }
            }
            internal void Verify()
            {
                if (!applied) throw Error("Transform readback precedes application"); scope.Require(); admission();
                var actual = Read(directory); RequireEqual(source, actual.Data); actual.RequireFrozen(); admission();
            }
        }
        internal static Data Capture(ResidentObjectDirectory directory)
        { var live = Read(directory); live.RequireFrozen(); return live.Data; }
        internal static Plan Prepare(Data saved, ResidentObjectDirectory directory, ResidentMutationScope scope, Action admitted)
            => new Plan(saved, directory, scope, admitted);

        private static Live Read(ResidentObjectDirectory directory)
        {
            if (directory == null) throw new ArgumentNullException(nameof(directory));
            var engine = LoadingEngineClockProbe.RequirePinnedEngine(); var live = new Live();
            var owners = directory.NativeBindings.Where(pair => pair.Value is Transform).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
            if (owners.Length > MaximumOwners) throw Error("Transform directory exceeds bounds");
            var byNative = new Dictionary<IntPtr, string>();
            foreach (var pair in owners) {
                var owner = (Transform)pair.Value;
                if (owner.GetType() != typeof(Transform) && owner.GetType() != typeof(RectTransform)) throw Error("unknown native Transform subtype");
                var native = M.Owner(owner); var access = live.Read(IntPtr.Add(native, 56), 16);
                var local = new LocalNode { Owner = owner, Native = native, Hierarchy = M.Ptr(access, 0), Index = M.I32(access, 8) };
                if (byNative.ContainsKey(native)) throw Error("Transform directory aliases a native owner");
                byNative.Add(native, pair.Key); live.Nodes.Add(pair.Key, local);
            }
            var hierarchyGroups = live.Nodes.GroupBy(pair => pair.Value.Hierarchy).ToArray();
            var hierarchyKeys = new Dictionary<IntPtr, string>(); var rows = new List<Node>(); var groups = new List<Hierarchy>();
            var gaps = new List<string>();
            foreach (var group in hierarchyGroups) {
                if (group.Key == IntPtr.Zero) throw Error("Transform hierarchy is absent");
                var members = group.Select(pair => pair.Key).OrderBy(value => value, StringComparer.Ordinal).ToArray();
                var key = members[0]; hierarchyKeys.Add(group.Key, key); live.Hierarchies.Add(key, group.Key);
                var header = live.Read(group.Key, 136); var capacity = M.U32(header, 16);
                if (M.Ptr(header, 0) != IntPtr.Zero || capacity == 0 || capacity > MaximumOwners)
                    throw Error("Transform hierarchy has an outstanding job or invalid capacity");
                var pointers = live.Read(M.Ptr(header, 48), checked((int)capacity * 8));
                var nextIndices = live.Read(M.Ptr(header, 112), checked((int)capacity * 4));
                var previousIndices = live.Read(M.Ptr(header, 120), checked((int)capacity * 4));
                var free = new HashSet<int>(); var freeIndex = M.I32(header, 20); var freePrevious = -1;
                while (freeIndex != -1) {
                    if (freeIndex < 0 || freeIndex >= capacity || !free.Add(freeIndex) || M.I32(previousIndices, freeIndex * 4) != freePrevious)
                        throw Error("Transform native free-slot chain differs");
                    freePrevious = freeIndex; freeIndex = M.I32(nextIndices, freeIndex * 4);
                }
                var indexOwners = new Dictionary<int, string>();
                for (var index = 0; index < capacity; index++) {
                    if (free.Contains(index)) continue;
                    var pointer = M.Ptr(pointers, index * 8); if (pointer == IntPtr.Zero) throw Error("occupied Transform slot has no owner");
                    if (!byNative.TryGetValue(pointer, out var binding)) { gaps.Add("transform-hierarchy-owner-outside-directory:" + key + ":" + index); continue; }
                    if (live.Nodes[binding].Hierarchy != group.Key || live.Nodes[binding].Index != index) throw Error("Transform access and owner table disagree");
                    indexOwners.Add(index, binding);
                }
                string IndexOwner(int index) => index == -1 ? "" : indexOwners.TryGetValue(index, out var binding) ? binding
                    : throw Error("Transform topology index is outside the complete directory");
                foreach (var pair in group) {
                    var local = pair.Value; local.Header = header;
                    if (local.Index < 0 || local.Index >= capacity || !indexOwners.TryGetValue(local.Index, out var bound) || bound != pair.Key)
                        throw Error("Transform slot identity differs");
                    var index = local.Index; var parent = IndexOwner(M.I32(live.Read(IntPtr.Add(M.Ptr(header, 32), index * 4), 4), 0));
                    var nativeParent = M.Ptr(live.Read(IntPtr.Add(local.Native, 144), 8), 0);
                    if (parent.Length == 0 ? nativeParent != IntPtr.Zero : nativeParent != live.Nodes[parent].Native)
                        throw Error("Transform parent pointer and hierarchy table disagree");
                    rows.Add(new Node { Owner = pair.Key, Parent = parent, Hierarchy = key,
                        Next = IndexOwner(M.I32(nextIndices, index * 4)),
                        Previous = IndexOwner(M.I32(previousIndices, index * 4)),
                        Descendants = M.U32(live.Read(IntPtr.Add(M.Ptr(header, 40), index * 4), 4), 0),
                        Rectangle = local.Owner is RectTransform, Execution = directory.Ownership.IsExecution(local.Owner), Local = live.Read(IntPtr.Add(local.Native, 72), 40),
                        Cached = live.Read(IntPtr.Add(M.Ptr(header, 24), index * 48), 48),
                        Rect = local.Owner is RectTransform ? live.Read(IntPtr.Add(local.Native, 184), 58) : Array.Empty<byte>(),
                        Changed = M.U64(live.Read(IntPtr.Add(M.Ptr(header, 72), index * 8), 8), 0),
                        Interested = M.U64(live.Read(IntPtr.Add(M.Ptr(header, 80), index * 8), 8), 0),
                        HierarchyInterested = M.U32(live.Read(IntPtr.Add(M.Ptr(header, 96), index * 4), 4), 0),
                        TransformType = live.Read(IntPtr.Add(M.Ptr(header, 104), index), 1)[0] });
                }
                groups.Add(new Hierarchy { Key = key, Members = members, CombinedChanged = M.U64(header, 64), CombinedInterested = M.U64(header, 88) });
            }
            live.Dispatch = M.Ptr(live.Read(IntPtr.Add(engine.BaseAddress, DispatchGlobal), 8), 0);
            var remove = (DispatchHierarchy)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine.BaseAddress, 0x7e8b80), typeof(DispatchHierarchy));
            var rawDispatch = M.Read(live.Dispatch, 32); var initialCount = checked((int)M.U64(rawDispatch, 24));
            if (initialCount > 0 && initialCount <= MaximumOwners) {
                var qPtr = M.Ptr(rawDispatch, 8);
                var qBytes = M.Read(qPtr, initialCount * 8);
                for (var i = initialCount - 1; i >= 0; i--) {
                    var ptr = M.Ptr(qBytes, i * 8);
                    if (ptr != IntPtr.Zero && !hierarchyKeys.ContainsKey(ptr)) {
                        var qIdx = M.I32(M.Read(IntPtr.Add(ptr, 56), 4), 0);
                        Modding.Logger.Log($"[HKTAS-TRANSFORM] Purging orphan queued hierarchy at index {i}: pointer=0x{ptr.ToInt64():x}, qIdx={qIdx}");
                        if (qIdx != -1) remove(live.Dispatch, ptr);
                    }
                }
            }
            var dispatch = live.Read(live.Dispatch, 2744); var count = M.U64(dispatch, 24); var queueCapacity = M.U64(dispatch, 32) >> 1;
            if (count > queueCapacity || count > MaximumOwners || queueCapacity > MaximumOwners * 2) throw Error("Transform dispatch queue exceeds bounds");
            var queueBytes = live.Read(M.Ptr(dispatch, 8), checked((int)count * 8)); var queue = new string[count];
            for (var index = 0; index < queue.Length; index++) {
                var pointer = M.Ptr(queueBytes, index * 8);
                if (!hierarchyKeys.TryGetValue(pointer, out var key)) throw Error("queued hierarchy is outside the complete resident directory");
                if (M.I32(live.Read(IntPtr.Add(pointer, 56), 4), 0) != index) throw Error("Transform queue back-reference differs");
                queue[index] = key;
            }
            foreach (var group in groups) {
                var index = M.I32(live.Read(IntPtr.Add(live.Hierarchies[group.Key], 56), 4), 0);
                if (index != Array.IndexOf(queue, group.Key)) throw Error("Transform hierarchy dispatch membership differs");
            }
            var registry = new Registry { InUse = M.U64(dispatch, 128), Permanent = M.U64(dispatch, 2696), Deprecated = M.U64(dispatch, 2736),
                InterestTypes = Enumerable.Range(0, 11).Select(index => M.U64(dispatch, 40 + index * 8)).ToArray(),
                Names = Enumerable.Range(0, 64).Select(index => live.String(IntPtr.Add(live.Dispatch, 136 + index * 40))).ToArray(),
                TypePermanent = ReadTypeMasks(live, IntPtr.Add(live.Dispatch, 2704), true) };
            var immediate = M.Ptr(live.Read(IntPtr.Add(engine.BaseAddress, 0x1a1e0e0), 8), 0); var immediateBytes = live.Read(immediate, 1784);
            registry.ImmediateInUse = M.U32(immediateBytes, 0); registry.ImmediatePermanent = M.U32(immediateBytes, 1744);
            registry.ImmediateTypePermanent = ReadTypeMasks(live, IntPtr.Add(immediate, 1752), false);
            registry.ImmediateInterests = new int[31]; registry.ImmediateCallbacks = new int[31]; registry.ImmediateNames = new string[31];
            for (var index = 0; index < 31; index++) {
                registry.ImmediateInterests[index] = M.I32(immediateBytes, 8 + index * 56);
                var callback = M.Ptr(immediateBytes, 16 + index * 56); var relative = callback.ToInt64() - engine.BaseAddress.ToInt64();
                if (callback != IntPtr.Zero && (relative < 0 || relative >= engine.ModuleMemorySize)) throw Error("Transform callback is outside the pinned engine");
                registry.ImmediateCallbacks[index] = callback == IntPtr.Zero ? 0 : checked((int)relative);
                registry.ImmediateNames[index] = live.String(IntPtr.Add(immediate, 24 + index * 56));
            }
            live.Data = new Data { Nodes = rows.OrderBy(row => row.Owner, StringComparer.Ordinal).ToArray(),
                Hierarchies = groups.OrderBy(row => row.Key, StringComparer.Ordinal).ToArray(), Queue = queue,
                CombinedChanged = M.U64(dispatch, 0), Systems = registry, CoverageGaps = gaps.Distinct().OrderBy(value => value, StringComparer.Ordinal).ToArray() };
            Validate(live.Data); return live;
        }
        private static TypeMask[] ReadTypeMasks(Live live, IntPtr address, bool wide)
        {
            // Pinned core::hash_set node layout: hash/key/value, 12 or 24 bytes.
            // The scaled mask is (bucketCount - 1) * (4 or 8), not byte size.
            // Occupied hashes are below the deleted/empty sentinels FFFE/FFFF.
            var header = live.Read(address, 32); var scale = wide ? 8U : 4U;
            var scaledMask = M.U32(header, 8); var count = M.U32(header, 12); var pointer = M.Ptr(header, 0);
            if (pointer == IntPtr.Zero) {
                if (count != 0 || scaledMask != 0) throw Error("empty native type-interest map shape differs");
                return Array.Empty<TypeMask>();
            }
            if (scaledMask % scale != 0) throw Error("native type-interest map scale differs");
            var capacity = scaledMask / scale + 1;
            if (capacity > 65536 || (capacity & (capacity - 1)) != 0 || count > capacity) throw Error("native type-interest map exceeds bounds");
            var stride = checked((int)scale * 3); var bytes = live.Read(pointer, checked((int)capacity * stride));
            var entries = new Dictionary<uint, ulong>();
            for (var index = 0; index < capacity; index++) {
                var offset = checked((int)index * stride); var hash = M.U32(bytes, offset);
                if (hash >= 0xfffffffe) continue;
                if ((hash & 3) != 0) throw Error("native type-interest map hash differs");
                var key = M.U32(bytes, offset + (int)scale); var value = wide ? M.U64(bytes, offset + 16) : M.U32(bytes, offset + 8);
                if (entries.ContainsKey(key)) throw Error("native type-interest map repeats a type"); entries.Add(key, value);
            }
            if (entries.Count != count) throw Error("native type-interest map occupancy differs");
            return entries.OrderBy(pair => pair.Key).Select(pair => new TypeMask { Type = pair.Key, Mask = pair.Value }).ToArray();
        }
        internal static void RequireCompleteCoverage(Data data)
        { Validate(data); if (data.CoverageGaps.Length != 0) throw Error("Transform coverage is incomplete: " + string.Join(";", data.CoverageGaps)); }
        internal static void Validate(Data data)
        {
            if (data == null || data.Version != 1 || data.Nodes == null || data.Nodes.Length > MaximumOwners || data.Hierarchies == null
                || data.Hierarchies.Length > MaximumOwners || data.Queue == null || data.Queue.Length > MaximumOwners || data.Systems == null
                || data.CoverageGaps == null || data.CoverageGaps.Length > MaximumOwners
                || data.CoverageGaps.Any(value => string.IsNullOrEmpty(value) || value.Length > 32768)) throw Error("Transform snapshot shape differs");
            var nodes = new Dictionary<string, Node>(StringComparer.Ordinal); var groups = new Dictionary<string, Hierarchy>(StringComparer.Ordinal);
            foreach (var row in data.Nodes) {
                if (row == null || string.IsNullOrEmpty(row.Owner) || row.Owner.Length > 16384 || nodes.ContainsKey(row.Owner)
                    || row.Parent == null || row.Next == null || row.Previous == null || row.Hierarchy == null || row.Local == null || row.Local.Length != 40
                    || row.Cached == null || row.Cached.Length != 48 || row.Rect == null || row.Rect.Length != (row.Rectangle ? 58 : 0)
                    || row.Rectangle && (row.Rect[56] > 1 || row.Rect[57] > 1) || row.Descendants == 0 || row.Descendants > MaximumOwners)
                    throw Error("Transform node shape differs");
                nodes.Add(row.Owner, row);
            }
            foreach (var group in data.Hierarchies) {
                if (group == null || string.IsNullOrEmpty(group.Key) || groups.ContainsKey(group.Key) || group.Members == null || group.Members.Length == 0
                    || group.Members.Distinct(StringComparer.Ordinal).Count() != group.Members.Length || group.Members.Any(key => !nodes.ContainsKey(key))
                    || !group.Members.SequenceEqual(group.Members.OrderBy(value => value, StringComparer.Ordinal)) || group.Key != group.Members[0])
                    throw Error("Transform hierarchy shape differs");
                groups.Add(group.Key, group);
            }
            foreach (var row in nodes.Values) {
                if (!groups.TryGetValue(row.Hierarchy, out var group) || !group.Members.Contains(row.Owner)
                    || new[] { row.Parent, row.Next, row.Previous }.Any(key => key.Length != 0 && (!nodes.TryGetValue(key, out var other) || other.Hierarchy != row.Hierarchy)))
                    throw Error("Transform hierarchy links are not closed");
                var chain = new HashSet<string>(StringComparer.Ordinal) { row.Owner }; var cursor = row;
                while (cursor.Parent.Length != 0) { if (!chain.Add(cursor.Parent) || chain.Count > ResidentObjectDirectory.MaximumDepth) throw Error("Transform parent chain cycles or exceeds bounds"); cursor = nodes[cursor.Parent]; }
            }
            if (groups.Values.Sum(group => group.Members.Length) != nodes.Count || data.Queue.Distinct(StringComparer.Ordinal).Count() != data.Queue.Length
                || data.Queue.Any(key => !groups.ContainsKey(key))) throw Error("Transform hierarchy partition or queue differs");
            var systems = data.Systems;
            if (systems.InterestTypes == null || systems.InterestTypes.Length != 11 || systems.Names == null || systems.Names.Length != 64
                || systems.ImmediateInterests == null || systems.ImmediateInterests.Length != 31 || systems.ImmediateCallbacks == null || systems.ImmediateCallbacks.Length != 31
                || systems.ImmediateNames == null || systems.ImmediateNames.Length != 31 || systems.Names.Concat(systems.ImmediateNames).Any(value => value == null || value.Length > 16384))
                throw Error("Transform system registry shape differs");
            foreach (var map in new[] { systems.TypePermanent, systems.ImmediateTypePermanent })
                if (map == null || map.Length > 65536 || map.Any(row => row == null) || map.Select(row => row.Type).Distinct().Count() != map.Length
                    || !map.Select(row => row.Type).SequenceEqual(map.Select(row => row.Type).OrderBy(value => value))) throw Error("Transform permanent type map differs");
        }
        private static void RequireCompatible(Data source, Data target)
        {
            Validate(source); Validate(target);
            if (!Bytes(source.Systems).SequenceEqual(Bytes(target.Systems)) || source.Nodes.Length != target.Nodes.Length
                || source.Hierarchies.Length != target.Hierarchies.Length || !source.CoverageGaps.SequenceEqual(target.CoverageGaps))
                throw Error("Transform source and target registry or coverage differs");
            for (var index = 0; index < source.Nodes.Length; index++) {
                var a = source.Nodes[index]; var b = target.Nodes[index];
                if (a.Owner != b.Owner || a.Parent != b.Parent || a.Next != b.Next || a.Previous != b.Previous || a.Hierarchy != b.Hierarchy
                    || a.Descendants != b.Descendants || a.Rectangle != b.Rectangle || a.Execution != b.Execution) throw Error("Transform source and target topology differs: " + a.Owner);
                if (a.Execution && !Bytes(a).SequenceEqual(Bytes(b))) throw Error("execution Transform state must already match; restoration cannot overwrite it: " + a.Owner);
            }
            for (var index = 0; index < source.Hierarchies.Length; index++) if (source.Hierarchies[index].Key != target.Hierarchies[index].Key
                || !source.Hierarchies[index].Members.SequenceEqual(target.Hierarchies[index].Members)) throw Error("Transform native hierarchy grouping differs");
        }
        internal static string DebugSummary()
        {
            var dispatch = M.Ptr(M.Read(IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, DispatchGlobal), 8), 0);
            if (dispatch == IntPtr.Zero) return "none";
            var bytes = M.Read(dispatch, 32);
            return $"chg=0x{M.U64(bytes, 0):X},q={M.U64(bytes, 24)}";
        }
        internal static void RequireEqual(Data expected, Data actual)
        {
            RequireCompatible(expected, actual);
            if (!Bytes(expected).SequenceEqual(Bytes(actual))) {
                var diffs = new List<string> { $"sys10={expected.Systems.Names[10]}" };
                if (expected.CombinedChanged != actual.CombinedChanged) diffs.Add($"CombinedChanged exp=0x{expected.CombinedChanged:X} act=0x{actual.CombinedChanged:X}");
                if (!expected.Queue.SequenceEqual(actual.Queue)) diffs.Add($"Queue expLen={expected.Queue.Length} actLen={actual.Queue.Length} exp=[{string.Join(",", expected.Queue.Take(5))}] act=[{string.Join(",", actual.Queue.Take(5))}]");
                for (var i = 0; i < expected.Hierarchies.Length; i++) {
                    var eh = expected.Hierarchies[i]; var ah = actual.Hierarchies[i];
                    if (eh.CombinedChanged != ah.CombinedChanged || eh.CombinedInterested != ah.CombinedInterested)
                        diffs.Add($"Hier[{i}:{eh.Key}] chg={eh.CombinedChanged:X}->{ah.CombinedChanged:X} int={eh.CombinedInterested:X}->{ah.CombinedInterested:X}");
                }
                var nodeDiffs = 0;
                for (var i = 0; i < expected.Nodes.Length; i++) {
                    var e = expected.Nodes[i]; var a = actual.Nodes[i];
                    if (e.Changed != a.Changed || e.Interested != a.Interested || e.HierarchyInterested != a.HierarchyInterested || e.TransformType != a.TransformType
                        || !e.Local.SequenceEqual(a.Local) || !e.Cached.SequenceEqual(a.Cached) || !e.Rect.SequenceEqual(a.Rect)) {
                        if (++nodeDiffs <= 10)
                            diffs.Add($"Node[{e.Owner}] rect={e.Rectangle} chg={e.Changed:X}->{a.Changed:X} int={e.Interested:X}->{a.Interested:X} hi={e.HierarchyInterested:X}->{a.HierarchyInterested:X} tt={e.TransformType}->{a.TransformType} loc={!e.Local.SequenceEqual(a.Local)} cac={!e.Cached.SequenceEqual(a.Cached)} rec={!e.Rect.SequenceEqual(a.Rect)}");
                    }
                }
                if (nodeDiffs > 10) diffs.Add($"totalNodeDiffs={nodeDiffs}");
                throw Error("Transform source and independent target readback differ: " + string.Join(" | ", diffs));
            }
        }
        private static byte[] Bytes(object value) => Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(value));
        private static Data Copy(Data value) => Newtonsoft.Json.JsonConvert.DeserializeObject<Data>(Encoding.UTF8.GetString(Bytes(value)))
            ?? throw Error("Transform source copy is absent");
        private static InvalidDataException Error(string reason) => new InvalidDataException("Resident Transform state: " + reason);
    }
}
