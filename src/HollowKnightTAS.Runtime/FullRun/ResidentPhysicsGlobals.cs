using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Process-local addresses never enter Data. Shared scripting objects are roots
    // of the SAME graph as component/static fields; their GC handles are local.
    internal static class ResidentPhysicsGlobals
    {
        internal const string CollisionRoot = "physics/shared-collision", ContactsRoot = "physics/shared-contacts";
        private const int WorldState = 103072, StackOffset = 136, ChunkBytes = 16384, Maximum = 262144;
        private static readonly int[] BlockSizes = { 16, 32, 64, 96, 128, 160, 192, 224, 256, 320, 384, 448, 512, 640 };

        internal sealed class Data
        {
            public int Version = 1, ManagerVtable, NextSceneHandle, MinimumContactArraySize, InterestCapacity, SceneCapacity;
            public uint InterestUnused, SceneUnused;
            public int[] TransformHandles = Array.Empty<int>();
            public Interest[] Interests = Array.Empty<Interest>();
            public Scene[] Scenes = Array.Empty<Scene>();
            public Shared Collision = new Shared(), Contacts = new Shared();
            public ManagedReference[] ManagedReferences = Array.Empty<ManagedReference>();
            public string[] CoverageGaps = Array.Empty<string>();
        }
        internal sealed class Interest { public string Owner = ""; public int Count; }
        internal sealed class Shared { public bool Present; public int Weakness; }
        internal sealed class ManagedReference
        {
            public string Location = "", ModuleIdentity = "", Owner = "";
            public int TypeToken, FieldToken;
        }
        internal sealed class Scene
        {
            public string Key = "";
            public int FilterVtable, ListenerVtable, ContactsVtable, SimulationId;
            public int IgnoreCapacity, CallbackIndexCapacity;
            public uint IgnoreUnused, CallbackIndexUnused;
            public ulong CallbackCapacity, NonToiCapacity, ToiCapacity;
            public Allocator Blocks = new Allocator();
            public int StackMaximum;
        }
        internal sealed class Allocator
        {
            public int ChunkCapacity;
            public Chunk[] Chunks = Array.Empty<Chunk>();
            public string[][] Free = Enumerable.Range(0, 14).Select(_ => Array.Empty<string>()).ToArray();
        }
        internal sealed class Chunk
        {
            public int BlockSize;
            // Empty slots are free. Nonempty strings bind actual native owners.
            public string[] Owners = Array.Empty<string>();
        }
        internal sealed class Live
        {
            internal Data Data = new Data();
            internal IntPtr Manager, Engine;
            internal readonly Dictionary<string, object?> ManagedRoots = new Dictionary<string, object?>(StringComparer.Ordinal);
            internal readonly Dictionary<string, IntPtr> InterestSlots = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            internal readonly Dictionary<string, IntPtr> Scenes = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            internal readonly Dictionary<string, IntPtr> Worlds = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            internal readonly Dictionary<string, int> SceneHandles = new Dictionary<string, int>(StringComparer.Ordinal);
            internal readonly Dictionary<string, IntPtr> Cells = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            internal readonly List<Tuple<IntPtr, byte[]>> Witness = new List<Tuple<IntPtr, byte[]>>();
            internal readonly List<ResidentScriptingHandles.Capture> Handles = new List<ResidentScriptingHandles.Capture>();
            internal readonly SortedSet<string> Gaps = new SortedSet<string>(StringComparer.Ordinal);
            internal byte[] Read(IntPtr pointer, int length) { var value = M.Read(pointer, length); Witness.Add(Tuple.Create(pointer, value)); return value; }
            internal void Frozen() { foreach (var row in Witness) if (!M.Read(row.Item1, row.Item2.Length).SequenceEqual(row.Item2)) throw Error("global native observation changed"); foreach (var handle in Handles) handle.RequireUnchanged(); }
        }

        internal static Live Capture(ResidentObjectDirectory directory, Dictionary<string, IntPtr> scenes,
            Dictionary<string, IntPtr> worlds, Dictionary<string, IntPtr> allocations)
        {
            var engine = LoadingEngineClockProbe.RequirePinnedEngine(); var live = new Live { Engine = engine.BaseAddress };
            RequireEntry(live.Engine, 0x721ee0, "488b0501602f01c3");
            live.Manager = M.Ptr(live.Read(IntPtr.Add(live.Engine, 0x1a17ee8), 8), 0); var state = live.Read(live.Manager, 184);
            foreach (var row in scenes) { live.Scenes.Add(row.Key, row.Value); live.SceneHandles.Add(row.Key, M.I32(live.Read(row.Value, 8), 4)); }
            foreach (var row in worlds) live.Worlds.Add(row.Key, row.Value);
            var objects = directory.NativeBindings.Where(row => row.Value is GameObject).ToDictionary(row => row.Value.GetInstanceID(), row => row.Key);
            var interests = new List<Interest>(); var map = ReadMap(live, Slice(state, 40, 32), 12, out var interestCapacity);
            for (var i = 0; i < interestCapacity; i++) {
                var at = i * 12; if (M.U32(map, at) >= 0xfffffffe) continue;
                if (!objects.TryGetValue(M.I32(map, at + 4), out var owner)) { live.Gaps.Add("physics-interest-without-directory-owner"); continue; }
                var count = M.I32(map, at + 8); if (count <= 0) throw Error("collider interest count is not positive");
                interests.Add(new Interest { Owner = owner, Count = count }); live.InterestSlots.Add(owner, IntPtr.Add(M.Ptr(state, 40), at + 8));
            }
            ReadMap(live, Slice(state, 72, 32), 24, out var sceneCapacity);
            var rows = scenes.OrderBy(row => row.Key, StringComparer.Ordinal).Select(row => ReadScene(live, row.Key, row.Value, worlds[row.Key], allocations)).ToArray();
            live.Data = new Data { ManagerVtable = Rva(M.Ptr(state, 0), live.Engine), NextSceneHandle = M.I32(state, 108), MinimumContactArraySize = M.I32(state, 112),
                TransformHandles = Enumerable.Range(0, 7).Select(i => M.I32(state, 12 + i * 4)).ToArray(), InterestCapacity = interestCapacity, SceneCapacity = sceneCapacity,
                InterestUnused = M.U32(state, 60), SceneUnused = M.U32(state, 92),
                Interests = interests.OrderBy(row => row.Owner, StringComparer.Ordinal).ToArray(), Scenes = rows,
                Collision = ReadShared(live, state, 120, 128, CollisionRoot, typeof(Collision2D)),
                Contacts = ReadShared(live, state, 152, 160, ContactsRoot, typeof(ContactPoint2D[])) };
            live.Data.ManagedReferences = ReadManagedReferences(live, directory);
            live.Data.CoverageGaps = live.Gaps.ToArray(); Validate(live.Data); live.Frozen(); return live;
        }

        private static Scene ReadScene(Live live, string key, IntPtr scene, IntPtr world, Dictionary<string, IntPtr> allocations)
        {
            var header = live.Read(scene, 1184); var state = live.Read(IntPtr.Add(world, WorldState), 360);
            var filter = M.Ptr(header, 24); var listener = M.Ptr(header, 32); var contacts = M.Ptr(header, 40);
            var filterBytes = live.Read(filter, 16); var listenerBytes = live.Read(listener, 16); var contactBytes = live.Read(contacts, 88);
            if (Rva(M.Ptr(filterBytes, 0), live.Engine) != 0x1702a48 || Rva(M.Ptr(listenerBytes, 0), live.Engine) != 0x1702a58)
                throw Error("actual contact filter/listener type differs from the pinned native class");
            if (M.Ptr(filterBytes, 8) != scene || M.Ptr(listenerBytes, 8) != contacts || M.Ptr(state, 136) != filter
                || M.Ptr(state, 144) != listener || M.Ptr(state, 152) != world) throw Error("world listener/filter/allocator ownership differs");
            if (M.Ptr(state, 328) != IntPtr.Zero || M.Ptr(state, 336) != IntPtr.Zero) live.Gaps.Add("physics-world-custom-destruction-or-draw-listener:" + key);
            foreach (var offset in new[] { 48, 64 }) if (M.Ptr(header, offset) != IntPtr.Add(scene, offset) || M.Ptr(header, offset + 8) != IntPtr.Add(scene, offset))
                live.Gaps.Add("physics-scene-effector-or-joint-update-list:" + key);
            ReadMap(live, Slice(header, 80, 32), 32, out var ignoreCapacity);
            ReadMap(live, Slice(contactBytes, 8, 32), 32, out var callbackIndexCapacity);
            var callbackCapacity = M.U64(contactBytes, 64);
            // Membership, manager indices, capacity and both queues' backing
            // storage now belong to the typed Box2D contact graph.
            var nonToi = M.U64(state, 184); var toi = M.U64(state, 216);
            if (M.Ptr(contactBytes, 72) == IntPtr.Zero || Rva(M.Ptr(contactBytes, 0), live.Engine) != 0x1701b68)
                throw Error("actual PhysicsContacts2D class or allocator is absent");
            var stack = live.Read(IntPtr.Add(world, StackOffset + 102400), 536);
            if (M.I32(stack, 0) != 0 || M.I32(stack, 4) != 0 || M.I32(stack, 528) != 0)
                live.Gaps.Add("physics-stack-active-allocations:" + key);
            else if (live.Read(IntPtr.Add(world, StackOffset), 102400).Any(value => value != 0) || M.I32(stack, 8) != 0 || Slice(stack, 16, 512).Any(value => value != 0))
                live.Gaps.Add("physics-stack-idle-storage-needs-canon:" + key);
            return new Scene { Key = key, FilterVtable = Rva(M.Ptr(filterBytes, 0), live.Engine), ListenerVtable = Rva(M.Ptr(listenerBytes, 0), live.Engine),
                ContactsVtable = Rva(M.Ptr(contactBytes, 0), live.Engine), SimulationId = M.I32(contactBytes, 80), IgnoreCapacity = ignoreCapacity,
                CallbackIndexCapacity = callbackIndexCapacity, IgnoreUnused = M.U32(header, 100), CallbackIndexUnused = M.U32(contactBytes, 28),
                CallbackCapacity = callbackCapacity, NonToiCapacity = nonToi, ToiCapacity = toi,
                Blocks = ReadAllocator(live, key, world, allocations), StackMaximum = M.I32(stack, 8) };
        }

        private static Allocator ReadAllocator(Live live, string key, IntPtr world, Dictionary<string, IntPtr> allocations)
        {
            var state = live.Read(world, 136); var count = Bounded(M.I32(state, 8), 4096); var capacity = Bounded(M.I32(state, 12), 4096);
            if (capacity < count || capacity % 128 != 0) throw Error("block allocator chunk capacity differs");
            var bytes = live.Read(M.Ptr(state, 0), checked(count * 16)); var chunks = new Chunk[count]; var cells = new Dictionary<IntPtr, string>();
            var occupied = allocations.GroupBy(row => row.Value).ToDictionary(group => group.Key, group => group.Count() == 1 ? group.First().Key : throw Error("native allocation is multiply bound"));
            for (var i = 0; i < count; i++) {
                var size = M.I32(bytes, i * 16); if (!BlockSizes.Contains(size)) throw Error("block allocator size class differs");
                var pointer = M.Ptr(bytes, i * 16 + 8); var owners = new string[ChunkBytes / size];
                if (cells.Count + owners.Length > Maximum) throw Error("block allocator cell inventory exceeds its bound");
                for (var slot = 0; slot < owners.Length; slot++) {
                    var cell = IntPtr.Add(pointer, slot * size); var identity = key + "/chunk[" + i + "]/cell[" + slot + "]";
                    cells.Add(cell, identity); live.Cells.Add(identity, cell); owners[slot] = occupied.TryGetValue(cell, out var owner) ? owner : "";
                }
                chunks[i] = new Chunk { BlockSize = size, Owners = owners };
            }
            var free = new string[14][]; var freePointers = new HashSet<IntPtr>();
            for (var bucket = 0; bucket < free.Length; bucket++) {
                var order = new List<string>();
                for (var pointer = M.Ptr(state, 24 + bucket * 8); pointer != IntPtr.Zero;) {
                    if (!freePointers.Add(pointer) || !cells.TryGetValue(pointer, out var identity) || order.Count >= Maximum) throw Error("block free list is cyclic or outside its chunks");
                    var position = FindCell(chunks, key, identity);
                    if (chunks[position.Item1].BlockSize != BlockSizes[bucket] || chunks[position.Item1].Owners[position.Item2].Length != 0)
                        throw Error("free cell aliases a live allocation or a different size class");
                    var contents = live.Read(pointer, BlockSizes[bucket]);
                    if (contents.Skip(8).Any(value => value != 0)) live.Gaps.Add("physics-block-free-storage-needs-canon:" + key);
                    order.Add(identity); pointer = M.Ptr(contents, 0);
                }
                free[bucket] = order.ToArray();
            }
            foreach (var pair in cells) if (!freePointers.Contains(pair.Key) && !occupied.ContainsKey(pair.Key)) live.Gaps.Add("physics-unbound-block-allocation:" + pair.Value);
            return new Allocator { ChunkCapacity = capacity, Chunks = chunks, Free = free };
        }

        private static Shared ReadShared(Live live, byte[] state, int objectOffset, int handleOffset, string root, Type expected)
        {
            var handle = ResidentScriptingHandles.Read(IntPtr.Add(live.Manager, handleOffset)); live.Handles.Add(handle);
            var value = handle.Target;
            if (handle.Allocated && (value == null || value.GetType() != expected || handle.Weakness == 0 || !handle.CacheMatches))
                throw Error("shared scripting object type or handle alias differs");
            ResidentScriptingHandles.VerifyObjectReference(IntPtr.Add(live.Manager, objectOffset), value);
            if (!handle.Allocated && handle.CachePresent) live.Gaps.Add("physics-empty-handle-stale-cache:" + root);
            live.ManagedRoots.Add(root, value); return new Shared { Present = value != null, Weakness = handle.Weakness };
        }

        private static ManagedReference[] ReadManagedReferences(Live live, ResidentObjectDirectory directory)
        {
            var owners = directory.NativeBindings.Where(row => row.Value is Collider2D || row.Value is Rigidbody2D).ToDictionary(row => row.Value.GetInstanceID(), row => row);
            return DescribeManagedReferences(live.ManagedRoots, (id, kind) => {
                if (!owners.TryGetValue(id, out var binding)) return null;
                if (!kind.IsInstanceOfType(binding.Value)) throw Error("callback ID resolves to a different component kind");
                return binding.Key;
            }, gap => live.Gaps.Add(gap));
        }

        internal static ManagedReference[] DescribeManagedReferences(IReadOnlyDictionary<string, object?> roots, Func<int, Type, string?> bindInstance, Action<string> reportGap)
        {
            if (roots == null || bindInstance == null || reportGap == null) throw new ArgumentNullException();
            var references = new List<ManagedReference>();
            Action<object, string> fields = (value, location) => {
                var type = ResidentValueAccess.TypeOf(value);
                if (type != typeof(Collision2D) && type != typeof(ContactPoint2D)) throw Error("callback reference metadata has an unknown actual type");
                foreach (var name in new[] { "m_Collider", "m_OtherCollider", "m_Rigidbody", "m_OtherRigidbody" }) {
                    var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw Error("callback instance-ID field is absent");
                    if (field.FieldType != typeof(int)) throw Error("callback instance-ID field type differs");
                    var id = (int)ResidentValueAccess.ReadField(value, field)!; var owner = "";
                    if (id != 0) {
                        var binding = bindInstance(id, name.IndexOf("Collider", StringComparison.Ordinal) >= 0 ? typeof(Collider2D) : typeof(Rigidbody2D));
                        if (string.IsNullOrEmpty(binding)) reportGap("physics-unresolved-callback-instance-id:" + location + ":" + field.MetadataToken);
                        else owner = binding!;
                    }
                    if (references.Count >= Maximum) throw Error("managed callback reference count exceeds its bound");
                    references.Add(new ManagedReference { Location = location, ModuleIdentity = field.Module.ModuleVersionId.ToString("D"),
                        TypeToken = type.MetadataToken, FieldToken = field.MetadataToken, Owner = owner });
                }
            };
            Action<Array, string> array = (values, location) => {
                if (values.GetType() != typeof(ContactPoint2D[]) || values.Length > Maximum / 4) throw Error("callback contact array schema differs");
                for (var i = 0; i < values.Length; i++) fields(ResidentValueAccess.ReadArray(values, i)!, location + "[" + i + "]");
            };
            if (roots[CollisionRoot] is Collision2D collision) {
                fields(collision, CollisionRoot);
                foreach (var name in new[] { "m_ReusedContacts", "m_LegacyContacts" }) {
                    var field = typeof(Collision2D).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw Error("callback contacts field is absent");
                    if (ResidentValueAccess.ReadField(collision, field) is ContactPoint2D[] values) array(values, CollisionRoot + "/field[" + field.MetadataToken + "]");
                }
            }
            if (roots[ContactsRoot] is ContactPoint2D[] contacts) array(contacts, ContactsRoot);
            return references.OrderBy(row => row.Location, StringComparer.Ordinal).ThenBy(row => row.FieldToken).ToArray();
        }

        internal static void RequireCompatible(Data source, Live target)
        {
            Validate(source); Validate(target.Data);
            if (source.CoverageGaps.Length != 0 || target.Data.CoverageGaps.Length != 0) throw Error("native global coverage is incomplete");
            var other = target.Data;
            if (source.ManagerVtable != other.ManagerVtable || source.InterestCapacity != other.InterestCapacity || source.SceneCapacity != other.SceneCapacity
                || source.InterestUnused != other.InterestUnused || source.SceneUnused != other.SceneUnused || target.SceneHandles.Values.Any(handle => handle >= source.NextSceneHandle)
                || !source.TransformHandles.SequenceEqual(other.TransformHandles) || !source.Interests.Select(row => row.Owner).SequenceEqual(other.Interests.Select(row => row.Owner))
                || !source.Scenes.Select(row => row.Key).SequenceEqual(other.Scenes.Select(row => row.Key))) {
                var diff = $"vtable={source.ManagerVtable}/{other.ManagerVtable}, intCap={source.InterestCapacity}/{other.InterestCapacity}, intUnused={source.InterestUnused}/{other.InterestUnused}, " +
                    $"sceneCap={source.SceneCapacity}/{other.SceneCapacity}, sceneUnused={source.SceneUnused}/{other.SceneUnused}, nextHandle={source.NextSceneHandle}/[{string.Join(",", target.SceneHandles.Values)}], " +
                    $"tx=[{string.Join(",", source.TransformHandles)}]/[{string.Join(",", other.TransformHandles)}], " +
                    $"intCnt={source.Interests.Length}/{other.Interests.Length}, intOnlySrc=[{string.Join(",", source.Interests.Select(r => r.Owner).Except(other.Interests.Select(r => r.Owner)).Take(5))}], " +
                    $"intOnlyTgt=[{string.Join(",", other.Interests.Select(r => r.Owner).Except(source.Interests.Select(r => r.Owner)).Take(5))}], " +
                    $"scenes=[{string.Join(",", source.Scenes.Select(r => r.Key))}]/[{string.Join(",", other.Scenes.Select(r => r.Key))}]";
                throw Error("manager registration/topology needs a common Canon: " + diff);
            }
            for (var i = 0; i < source.Scenes.Length; i++) {
                var a = source.Scenes[i]; var b = other.Scenes[i];
                if (a.FilterVtable != b.FilterVtable || a.ListenerVtable != b.ListenerVtable || a.ContactsVtable != b.ContactsVtable
                    || a.IgnoreCapacity != b.IgnoreCapacity || a.CallbackIndexCapacity != b.CallbackIndexCapacity || a.IgnoreUnused != b.IgnoreUnused || a.CallbackIndexUnused != b.CallbackIndexUnused
                    || a.Blocks.ChunkCapacity != b.Blocks.ChunkCapacity
                    || a.Blocks.Chunks.Length != b.Blocks.Chunks.Length)
                    throw Error($"world allocation owners require synchronized topology: key={a.Key}, igCap={a.IgnoreCapacity}/{b.IgnoreCapacity}, igUn={a.IgnoreUnused}/{b.IgnoreUnused}, cbCap={a.CallbackIndexCapacity}/{b.CallbackIndexCapacity}, cbUn={a.CallbackIndexUnused}/{b.CallbackIndexUnused}, blkChunkCap={a.Blocks.ChunkCapacity}/{b.Blocks.ChunkCapacity}, blkChunks={a.Blocks.Chunks.Length}/{b.Blocks.Chunks.Length}");
                for (var chunk = 0; chunk < a.Blocks.Chunks.Length; chunk++) if (a.Blocks.Chunks[chunk].BlockSize != b.Blocks.Chunks[chunk].BlockSize
                    || !a.Blocks.Chunks[chunk].Owners.SequenceEqual(b.Blocks.Chunks[chunk].Owners))
                    throw Error($"block allocation placement requires synchronized topology: key={a.Key}, chunk={chunk}, size={a.Blocks.Chunks[chunk].BlockSize}/{b.Blocks.Chunks[chunk].BlockSize}");
            }
        }

        internal static void ValidateTransformDependencies(Data source, ResidentTransformState.Data transforms)
        {
            Validate(source); if (transforms == null || transforms.Systems == null) throw Error("Transform dependency inventory is absent");
            for (var i = 0; i < source.TransformHandles.Length; i++) {
                var index = source.TransformHandles[i];
                var present = index >= 0 && (i < 5 ? index < 64 && (transforms.Systems.InUse & (1UL << index)) != 0
                    : index < 32 && (transforms.Systems.ImmediateInUse & (1U << index)) != 0);
                if (!present)
                    throw Error("physics transform-interest handle is not registered in the common Transform state");
            }
        }

        // The caller restores and verifies these roots through the unified graph.
        // Collision2D/ContactPoint2D instance-ID fields require that graph's
        // type-based directory remapper before this local GC-handle binding.
        internal static void Apply(Data source, Live target, Func<string, object?> resolveManaged, Action admitted)
        {
            RequireCompatible(source, target); if (resolveManaged == null || admitted == null) throw new ArgumentNullException(); admitted();
            foreach (var row in source.Interests) M.Write(target.InterestSlots[row.Owner], BitConverter.GetBytes(row.Count));
            M.Write(IntPtr.Add(target.Manager, 108), BitConverter.GetBytes(source.NextSceneHandle));
            M.Write(IntPtr.Add(target.Manager, 112), BitConverter.GetBytes(source.MinimumContactArraySize));
            BindShared(target, source.Collision, 120, 128, resolveManaged(CollisionRoot), typeof(Collision2D), admitted);
            BindShared(target, source.Contacts, 152, 160, resolveManaged(ContactsRoot), typeof(ContactPoint2D[]), admitted);
            foreach (var row in source.Scenes) {
                admitted(); var world = target.Worlds[row.Key]; var scene = target.Scenes[row.Key]; var header = M.Read(scene, 48);
                var contacts = M.Ptr(header, 40);
                M.Write(IntPtr.Add(contacts, 80), BitConverter.GetBytes(row.SimulationId));
                M.Write(IntPtr.Add(world, StackOffset + 102408), BitConverter.GetBytes(row.StackMaximum));
                for (var bucket = 0; bucket < row.Blocks.Free.Length; bucket++) {
                    var order = row.Blocks.Free[bucket];
                    Pointer(IntPtr.Add(world, 24 + bucket * 8), order.Length == 0 ? IntPtr.Zero : target.Cells[order[0]]);
                    for (var i = 0; i < order.Length; i++) Pointer(target.Cells[order[i]], i + 1 == order.Length ? IntPtr.Zero : target.Cells[order[i + 1]]);
                }
            }
            admitted();
        }

        private static void BindShared(Live target, Shared source, int objectOffset, int handleOffset, object? value, Type expected, Action admitted)
        {
            if (source.Present != (value != null) || value != null && value.GetType() != expected) throw Error("unified graph shared root type differs");
            var slot = IntPtr.Add(target.Manager, handleOffset); var objectSlot = IntPtr.Add(target.Manager, objectOffset);
            ResidentScriptingHandles.Apply(slot, value, source.Weakness, admitted);
            ResidentScriptingHandles.WriteObjectReference(objectSlot, value, admitted);
            ResidentScriptingHandles.VerifyObjectReference(objectSlot, value);
        }

        // Common S0 may canonicalize only *unallocated* native storage. This is
        // not a substitute for synchronizing live allocation ownership/layout.
        internal static void CanonicalizeIdleStorage(Live live, Action admitted)
        {
            if (admitted == null) throw new ArgumentNullException(); admitted(); live.Frozen();
            var manager = M.Read(live.Manager, 184);
            foreach (var pair in new[] { new[] { 120, 128 }, new[] { 152, 160 } })
                if (M.U64(manager, pair[1]) == ulong.MaxValue && M.Ptr(manager, pair[0]) == IntPtr.Zero)
                    ResidentScriptingHandles.ClearInactiveCache(IntPtr.Add(live.Manager, pair[1]), admitted);
            foreach (var row in live.Data.Scenes) {
                var world = live.Worlds[row.Key]; var stack = M.Read(IntPtr.Add(world, StackOffset + 102400), 536);
                if (M.I32(stack, 0) != 0 || M.I32(stack, 4) != 0 || M.I32(stack, 528) != 0) throw Error("cannot canonicalize an active stack allocation");
                M.Write(IntPtr.Add(world, StackOffset), new byte[102400]); M.Write(IntPtr.Add(world, StackOffset + 102408), new byte[4]);
                M.Write(IntPtr.Add(world, StackOffset + 102416), new byte[512]);
                for (var bucket = 0; bucket < row.Blocks.Free.Length; bucket++) foreach (var identity in row.Blocks.Free[bucket])
                    M.Write(IntPtr.Add(live.Cells[identity], 8), new byte[BlockSizes[bucket] - 8]);
                admitted();
            }
        }

        internal static void Validate(Data data)
        {
            if (data == null || data.Version != 1 || data.TransformHandles == null || data.TransformHandles.Length != 7 || data.Interests == null
                || data.Scenes == null || data.CoverageGaps == null || data.Collision == null || data.Contacts == null || data.ManagedReferences == null || data.Interests.Length > 100000
                || data.Scenes.Length > 1024 || data.ManagerVtable <= 0 || data.ManagerVtable >= 32 * 1024 * 1024 || data.NextSceneHandle < 0
                || data.MinimumContactArraySize < 0 || data.MinimumContactArraySize > Maximum || data.InterestCapacity < 1 || data.InterestCapacity > Maximum
                || data.SceneCapacity < 1 || data.SceneCapacity > 1024 || data.Interests.Select(row => row.Owner).Distinct().Count() != data.Interests.Length
                || data.Scenes.Select(row => row.Key).Distinct().Count() != data.Scenes.Length || data.InterestUnused > data.InterestCapacity || data.SceneUnused > data.SceneCapacity
                || data.ManagedReferences.Length > Maximum || data.CoverageGaps.Length > Maximum || data.CoverageGaps.Any(string.IsNullOrEmpty)) throw Error("global payload schema differs");
            for (var i = 0; i < data.TransformHandles.Length; i++) if (data.TransformHandles[i] < -1 || data.TransformHandles[i] >= (i < 5 ? 64 : 32)) throw Error("transform interest index differs");
            foreach (var shared in new[] { data.Collision, data.Contacts }) if (shared.Weakness < 0 || shared.Weakness > 2 || shared.Present && shared.Weakness == 0) throw Error("shared handle schema differs");
            foreach (var interest in data.Interests) if (string.IsNullOrEmpty(interest.Owner) || interest.Count <= 0) throw Error("interest payload differs");
            foreach (var reference in data.ManagedReferences) if (reference == null || string.IsNullOrEmpty(reference.Location) || reference.Owner == null
                || !Guid.TryParseExact(reference.ModuleIdentity, "D", out _) || (reference.TypeToken & unchecked((int)0xff000000)) != 0x02000000
                || (reference.FieldToken & unchecked((int)0xff000000)) != 0x04000000) throw Error("managed callback reference metadata differs");
            foreach (var scene in data.Scenes) {
                if (string.IsNullOrEmpty(scene.Key) || scene.FilterVtable != 0x1702a48 || scene.ListenerVtable != 0x1702a58 || scene.ContactsVtable <= 0
                    || scene.StackMaximum < 0 || scene.IgnoreCapacity < 1 || scene.CallbackIndexCapacity < 1 || scene.IgnoreUnused > scene.IgnoreCapacity
                    || scene.CallbackIndexUnused > scene.CallbackIndexCapacity || scene.Blocks == null || scene.Blocks.Chunks == null || scene.Blocks.Free == null || scene.Blocks.Free.Length != 14
                    || scene.Blocks.Chunks.Length > 4096 || scene.Blocks.ChunkCapacity < scene.Blocks.Chunks.Length || scene.Blocks.ChunkCapacity > 4096
                    || scene.Blocks.ChunkCapacity % 128 != 0) throw Error("world global payload differs");
                ResidentPhysicsState.Capacity(scene.CallbackCapacity, 0, 8); ResidentPhysicsState.Capacity(scene.NonToiCapacity, 0, 8); ResidentPhysicsState.Capacity(scene.ToiCapacity, 0, 8);
                var owned = new HashSet<string>(StringComparer.Ordinal); var free = new HashSet<string>(StringComparer.Ordinal);
                foreach (var chunk in scene.Blocks.Chunks) if (chunk == null || !BlockSizes.Contains(chunk.BlockSize) || chunk.Owners == null || chunk.Owners.Length != ChunkBytes / chunk.BlockSize
                    || chunk.Owners.Any(owner => owner == null || owner.Length != 0 && !owned.Add(owner))) throw Error("block allocation owner schema differs");
                if (scene.Blocks.Chunks.Sum(chunk => chunk.Owners.Length) > Maximum) throw Error("block allocation cell inventory exceeds its bound");
                for (var bucket = 0; bucket < 14; bucket++) {
                    if (scene.Blocks.Free[bucket] == null || scene.Blocks.Free[bucket].Length > Maximum) throw Error("free-list payload exceeds its bound");
                    foreach (var identity in scene.Blocks.Free[bucket]) {
                        var position = FindCell(scene.Blocks.Chunks, scene.Key, identity);
                        if (!free.Add(identity) || scene.Blocks.Chunks[position.Item1].BlockSize != BlockSizes[bucket]
                            || scene.Blocks.Chunks[position.Item1].Owners[position.Item2].Length != 0) throw Error("free-list owner alias differs");
                    }
                }
                for (var chunk = 0; chunk < scene.Blocks.Chunks.Length; chunk++) for (var cell = 0; cell < scene.Blocks.Chunks[chunk].Owners.Length; cell++) {
                    var identity = scene.Key + "/chunk[" + chunk + "]/cell[" + cell + "]";
                    if (scene.Blocks.Chunks[chunk].Owners[cell].Length == 0 && !free.Contains(identity)
                        && !data.CoverageGaps.Contains("physics-unbound-block-allocation:" + identity)) throw Error("unbound block allocation was marked covered");
                }
            }
        }

        private static Tuple<int, int> FindCell(Chunk[] chunks, string key, string identity)
        {
            var prefix = key + "/chunk[";
            if (identity == null || !identity.StartsWith(prefix, StringComparison.Ordinal)) throw Error("cell binding prefix differs");
            var split = identity.IndexOf("]/cell[", prefix.Length, StringComparison.Ordinal);
            if (split < 0 || !identity.EndsWith("]", StringComparison.Ordinal) || !int.TryParse(identity.Substring(prefix.Length, split - prefix.Length), out var chunk)
                || !int.TryParse(identity.Substring(split + 7, identity.Length - split - 8), out var cell) || chunk < 0 || chunk >= chunks.Length || cell < 0 || cell >= chunks[chunk].Owners.Length
                || identity != key + "/chunk[" + chunk + "]/cell[" + cell + "]") throw Error("cell binding is outside its allocator");
            return Tuple.Create(chunk, cell);
        }
        private static byte[] ReadMap(Live live, byte[] header, int stride, out int capacity)
        {
            var scaled = M.U32(header, 8); capacity = MapCapacity(scaled, stride);
            var nodes = M.Ptr(header, 0);
            if (capacity == 1 && nodes == IntPtr.Add(live.Engine, 0x1723b3c)) {
                var hash = live.Read(nodes, 4);
                if (M.U32(hash, 0) != uint.MaxValue || M.U32(header, 12) != 0) throw Error("shared empty hash sentinel differs");
                return hash;
            }
            var bytes = live.Read(nodes, checked(capacity * stride)); var found = 0;
            for (var i = 0; i < capacity; i++) if (M.U32(bytes, i * stride) < 0xfffffffe) found++;
            if (found != M.U32(header, 12)) throw Error("global map count differs"); return bytes;
        }
        internal static int MapCapacity(uint scaled, int stride)
        {
            // Unity scales indices by the lowest set bit of sizeof(node).
            // The last-node byte offset is then scaledMask * (stride / scale).
            if (stride != 12 && stride != 24 && stride != 32) throw Error("unsupported actual hash node layout");
            var scale = stride & -stride;
            if (scaled % scale != 0 || scaled > (uint)((Maximum - 1) * scale)) throw Error("global map capacity differs");
            var capacity = checked(1 + (int)scaled / scale);
            if ((capacity & (capacity - 1)) != 0) throw Error("global map capacity is not a power of two");
            return capacity;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ResizeInterestMap(IntPtr header, int mask);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeInterestNodes(IntPtr nodes, int label, IntPtr file, int line);

        internal static bool NeedsInterestMapSync(Data source, Data target)
        {
            return source.InterestCapacity != target.InterestCapacity || source.InterestUnused != target.InterestUnused;
        }

        internal static void SynchronizeInterestMap(Data source, Live target, Action admitted)
        {
            if (source == null || target == null || admitted == null) throw new ArgumentNullException();
            if (!NeedsInterestMapSync(source, target.Data)) return;
            if (!source.Interests.Select(row => row.Owner).SequenceEqual(target.Data.Interests.Select(row => row.Owner)))
                throw Error("interest map owner set differs before resize");
            admitted();
            target.Frozen();
            var header = IntPtr.Add(target.Manager, 40);
            var h = M.Read(header, 28);
            var p = M.Ptr(h, 0);
            var sentinel = IntPtr.Add(target.Engine, 0x1723b3c);
            if (source.InterestCapacity == 1) {
                if (source.Interests.Length != 0 || M.I32(h, 12) != 0) throw Error("static empty interest map cannot hold live keys");
                if (p != sentinel) {
                    var free = IntPtr.Add(target.Engine, 0x56b640);
                    RequireCodeSha256(free, 94, "2890014de154460d79e3ec3357c76ec9af9f8198edd1243f07e27c9708f58447", "interest free procedure differs");
                    if (M.U32(M.Read(sentinel, 4), 0) != uint.MaxValue) throw Error("shared empty hash sentinel differs");
                    ((FreeInterestNodes)Marshal.GetDelegateForFunctionPointer(free, typeof(FreeInterestNodes)))(
                        p, M.I32(h, 24), IntPtr.Add(target.Engine, 0x15c6004), 0x3c5);
                    var reset = (byte[])h.Clone();
                    Array.Copy(BitConverter.GetBytes(sentinel.ToInt64()), 0, reset, 0, 8);
                    Array.Clear(reset, 8, 12);
                    Array.Copy(BitConverter.GetBytes(source.InterestUnused), 0, reset, 20, 4);
                    M.Write(header, reset);
                }
                return;
            }
            var scaledMask = checked((source.InterestCapacity - 1) * 4);
            if (M.I32(h, 12) >= source.InterestCapacity) throw Error("resized interest map cannot hold live keys");
            var resize = IntPtr.Add(target.Engine, 0x26e900);
            RequireCodeSha256(resize, 322, "5c4255bd685ef505a11f634fbc34560301f7446c985d45cd8a10bb550600e7bd", "interest resize procedure differs");
            ((ResizeInterestMap)Marshal.GetDelegateForFunctionPointer(resize, typeof(ResizeInterestMap)))(header, scaledMask);
            h = M.Read(header, 28);
            p = M.Ptr(h, 0);
            if (p == IntPtr.Zero || p == sentinel || M.I32(h, 8) != scaledMask) throw Error("interest map resize readback differs");
            var freshGrowth = M.U32(h, 20);
            if (source.InterestUnused > freshGrowth) throw Error("source interest unused budget exceeds resized map");
            var tombstonesNeeded = checked((int)(freshGrowth - source.InterestUnused));
            if (tombstonesNeeded > 0) {
                var nodes = M.Read(p, checked(source.InterestCapacity * 12));
                var virginLeft = 0;
                for (var i = 0; i < source.InterestCapacity; i++) if (M.U32(nodes, i * 12) == uint.MaxValue) virginLeft++;
                if (virginLeft - tombstonesNeeded < 1) throw Error("interest map tombstone quota leaves no virgin empty sentinel");
                var placed = 0;
                for (var i = source.InterestCapacity - 1; i >= 0 && placed < tombstonesNeeded; i--) {
                    var at = i * 12;
                    if (M.U32(nodes, at) != uint.MaxValue) continue;
                    Array.Copy(BitConverter.GetBytes(0xfffffffeu), 0, nodes, at, 4);
                    Array.Clear(nodes, at + 4, 8);
                    placed++;
                }
                if (placed != tombstonesNeeded) throw Error("interest map tombstone placement incomplete");
                M.Write(p, nodes);
                M.Write(IntPtr.Add(header, 20), BitConverter.GetBytes(source.InterestUnused));
            }
        }

        private static void RequireCodeSha256(IntPtr address, int length, string expectedHex, string reason)
        {
            using (var sha = SHA256.Create()) {
                var actual = BitConverter.ToString(sha.ComputeHash(M.Read(address, length))).Replace("-", "").ToLowerInvariant();
                if (actual != expectedHex) throw Error(reason);
            }
        }

        private static void RequireEntry(IntPtr engine, int rva, string expected)
        { if (BitConverter.ToString(M.Read(IntPtr.Add(engine, rva), expected.Length / 2)).Replace("-", "").ToLowerInvariant() != expected) throw Error("native entry ABI differs"); }
        private static void Pointer(IntPtr slot, IntPtr value) => M.Write(slot, BitConverter.GetBytes(value.ToInt64()));
        private static byte[] Slice(byte[] bytes, int offset, int count) => M.Slice(bytes, offset, count);
        private static int Bounded(int value, int maximum) => value >= 0 && value <= maximum ? value : throw Error("native size exceeds its bound");
        private static int Rva(IntPtr pointer, IntPtr engine) { var rva = pointer.ToInt64() - engine.ToInt64(); return rva > 0 && rva < 32 * 1024 * 1024 ? (int)rva : throw Error("native type is outside pinned engine"); }
        private static InvalidDataException Error(string reason) => new InvalidDataException("Resident physics globals: " + reason + ".");
    }
}
