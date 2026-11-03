using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentPhysicsState
    {
        internal sealed class CallbackData
        {
            public int VectorLabel;
            public byte[] VectorPadding = Array.Empty<byte>();
            public ulong Capacity;
            public CollisionValue[] Rows = Array.Empty<CollisionValue>();
            public ResidentPhysicsCallbackMap.Data Map = new ResidentPhysicsCallbackMap.Data();
            public ResidentPhysicsCallbackAllocator.Data Allocator = new ResidentPhysicsCallbackAllocator.Data();
        }
        internal sealed class CollisionValue
        {
            // The pair in the lookup map and the pair delivered to callbacks
            // are independent actual fields. Keep both with their orientation.
            public string KeyA = "", KeyB = "", ColliderA = "", ColliderB = "", BodyA = "", BodyB = "", Receiving = "";
            public int ContactState, ContactCount, ManifoldLabel;
            public ulong ManifoldCapacity;
            public byte[] ManifoldHeaderPadding = Array.Empty<byte>(), FlagsAndPadding = Array.Empty<byte>();
            public CallbackManifold[] Manifolds = Array.Empty<CallbackManifold>();
        }
        internal sealed class CallbackManifold
        {
            public string Contact = "", FixtureA = "", FixtureB = "";
            public int ChildA, ChildB;
            // +0..7: enabled byte/pad. +40..111: simulation ID, point count,
            // world normal, both points, relative velocities, separations and
            // both contact impulse arrays. This is not the solver's manifold.
            public byte[] EnabledAndPadding = Array.Empty<byte>(), Values = Array.Empty<byte>();
        }
        private static CallbackData ReadCallbacks(Local live, WorldData world, byte[] cache)
        {
            var colliders = live.Data.Colliders.ToDictionary(c => live.Owners[c.Owner], c => c.Owner);
            var bodies = live.Data.Rigidbodies.ToDictionary(b => live.Owners[b.Owner], b => b.Owner);
            var result = new CallbackData { Capacity = M.U64(cache, 64), VectorLabel = Int(cache, 48), VectorPadding = Slice(cache, 52, 4) };
            var rows = live.CollisionRows[world.Key]; result.Rows = new CollisionValue[rows.Length];
            var scene = live.Scenes[world.Key]; var owner = M.Ptr(live.Read(IntPtr.Add(scene, 40), 8), 0);
            AddStorage(live, owner, 88); AddStorage(live, M.Ptr(cache, 72), 16);
            RecordPadding(live, world.Key, owner, cache, 36, 4);
            RecordPadding(live, world.Key, owner, cache, 84, 4);
            RecordPadding(live, world.Key, owner, cache, 52, 4); Array.Clear(result.VectorPadding, 0, 4);
            void RequireOwner() {
                if (M.Ptr(IntPtr.Add(scene, 40)) != owner || M.Ptr(IntPtr.Add(owner, 72)) != M.Ptr(cache, 72))
                    throw Error("callback manager/allocator changed during observation");
            }
            result.Map = ResidentPhysicsCallbackMap.Capture(IntPtr.Add(owner, 8), value => Name(colliders, value, "collision key"), RequireOwner);
            if (!result.Map.StaticEmpty) AddStorage(live, M.Ptr(cache, 8), checked(result.Map.Mask + 32));
            foreach (var gap in result.Map.CoverageGaps) live.Gaps.Add(MapGap(world.Key, gap));
            var allocator = ResidentPhysicsCallbackAllocator.Capture(M.Ptr(cache, 72), rows, RequireOwner);
            RecordPadding(live, world.Key, allocator.Owner, allocator.OwnerWitness, 4, 4);
            if (allocator.Data.Present) AddStorage(live, M.Ptr(IntPtr.Add(allocator.Owner, 8)), 32);
            if (allocator.Data.Present) RecordPadding(live, world.Key, M.Ptr(allocator.OwnerWitness, 8), allocator.HeaderWitness, 28, 4);
            for (var c = 0; c < allocator.Chunks.Length; c++) {
                AddStorage(live, allocator.Chunks[c], 26536); RecordPadding(live, world.Key, allocator.Chunks[c], allocator.ChunkWitness[c], 26530, 6);
            }
            live.CallbackAllocators.Add(world.Key, allocator); result.Allocator = allocator.Data;
            foreach (var gap in result.Allocator.CoverageGaps) live.Gaps.Add(CallbackGap(world.Key, gap));
            ObserveUnused(live, world.Key, M.Ptr(cache, 40), rows.Length, result.Capacity, 8);
            for (var index = 0; index < rows.Length; index++) {
                var bytes = live.Read(rows[index], 104);
                var count = DynamicCount(Slice(bytes, 0, 32), 112, out var capacity);
                var row = new CollisionValue {
                    ManifoldLabel = Int(bytes, 8), ManifoldHeaderPadding = Slice(bytes, 12, 4), ManifoldCapacity = capacity,
                    ContactState = Int(bytes, 32), ContactCount = Int(bytes, 36),
                    ColliderA = Name(colliders, M.Ptr(bytes, 40), "collision component A"), ColliderB = Name(colliders, M.Ptr(bytes, 48), "collision component B"),
                    BodyA = Name(bodies, M.Ptr(bytes, 56), "collision Rigidbody A", true), BodyB = Name(bodies, M.Ptr(bytes, 64), "collision Rigidbody B", true),
                    Receiving = Name(colliders, M.Ptr(bytes, 72), "receiving collision component", true),
                    KeyA = Name(colliders, M.Ptr(bytes, 80), "collision index key A"), KeyB = Name(colliders, M.Ptr(bytes, 88), "collision index key B"),
                    FlagsAndPadding = Slice(bytes, 96, 8), Manifolds = new CallbackManifold[count]
                };
                var buffer = M.Ptr(bytes, 0);
                RecordPadding(live, world.Key, rows[index], bytes, 12, 4); Array.Clear(row.ManifoldHeaderPadding, 0, 4);
                RecordPadding(live, world.Key, rows[index], bytes, 100, 4); Array.Clear(row.FlagsAndPadding, 4, 4);
                var active = live.Read(buffer, checked(count * 112));
                ObserveUnused(live, world.Key, buffer, count, capacity, 112);
                for (var j = 0; j < count; j++) {
                    var at = j * 112;
                    row.Manifolds[j] = new CallbackManifold {
                        EnabledAndPadding = Slice(active, at, 8),
                        Contact = Name(live.ContactNames, M.Ptr(active, at + 8), "callback contact", true),
                        FixtureA = Name(live.FixtureNames, M.Ptr(active, at + 16), "callback fixture A"),
                        FixtureB = Name(live.FixtureNames, M.Ptr(active, at + 24), "callback fixture B"),
                        ChildA = Int(active, at + 32), ChildB = Int(active, at + 36), Values = Slice(active, at + 40, 72)
                    };
                    RecordPadding(live, world.Key, buffer, active, at + 1, 7); Array.Clear(row.Manifolds[j].EnabledAndPadding, 1, 7);
                }
                result.Rows[index] = row;
            }
            return result;
        }
        private static void AddStorage(Local live, IntPtr pointer, int bytes)
        {
            if (bytes == 0) return;
            var start = pointer.ToInt64();
            if (start <= 0 || bytes < 0) throw Error("callback storage address is invalid");
            live.ContactAllocations.Add(Tuple.Create(start, checked(start + bytes)));
        }
        private static void RequireContactStorage(Local live)
        {
            foreach (var world in live.Data.Worlds) {
                var global = live.Globals!.Data.Scenes.Single(scene => scene.Key == world.Key); ValidateContactCells(world, global);
                AddStorage(live, live.Worlds[world.Key], 103432);
                for (var i = 0; i < global.Blocks.Chunks.Length; i++) AddStorage(live, live.Globals.Cells[world.Key + "/chunk[" + i + "]/cell[0]"], 16384);
                foreach (var c in world.Contacts) {
                    var tail = IntPtr.Add(live.Contacts[c.Key], 312); var bytes = live.Read(tail, 8);
                    RecordPadding(live, world.Key, tail, bytes, 0, 8);
                }
            }
            long last = 0;
            foreach (var block in live.ContactAllocations.OrderBy(b => b.Item1)) {
                if (block.Item1 < last) throw Error("callback buffer, allocator or Box2D world storage overlaps another owner");
                last = block.Item2;
            }
        }
        private static void RecordPadding(Local live, string world, IntPtr owner, byte[] bytes, int offset, int count)
        {
            for (var i = 0; i < count; i++) if (bytes[offset + i] != 0) {
                live.Gaps.Add("physics-unused-callback-buffer-needs-canon:" + world);
                live.CallbackUnused.Add(Tuple.Create(IntPtr.Add(owner, offset), count)); break;
            }
        }
        private static string CallbackGap(string world, string detail) => "physics-callback-allocator:" + world + ":" + detail;
        private static string MapGap(string world, string detail) => "physics-callback-map:" + world + ":" + detail;
        private static void ValidateCallbackOwners(WorldData world, ResidentPhysicsGlobals.Scene global, string[] gaps)
        {
            var callback = world.Callbacks;
            if (callback.Map == null || callback.Map.Count != world.CallbackCollisionCount || (uint)callback.Map.Growth != global.CallbackIndexUnused
                || global.CallbackIndexCapacity != ResidentPhysicsGlobals.MapCapacity((uint)callback.Map.Mask, 32)
                || callback.Capacity != global.CallbackCapacity) throw Error("callback vector/map/global coverage differs");
            if ((IsBorrowed(callback.Capacity) || callback.Rows.Any(row => IsBorrowed(row.ManifoldCapacity)))
                && !gaps.Contains("physics-borrowed-callback-storage:" + world.Key)) throw Error("borrowed callback storage lost its coverage gap");
            ResidentPhysicsCallbackMap.ValidateRows(callback.Map, index => {
                if (index < 0 || index >= callback.Rows.Length) throw Error("collision hash index exceeds its actual vector");
                return Tuple.Create(callback.Rows[index].KeyA, callback.Rows[index].KeyB);
            });
            ResidentPhysicsCallbackAllocator.Validate(callback.Allocator, callback.Rows.Length);
            if (callback.Allocator.CoverageGaps.Select(gap => CallbackGap(world.Key, gap)).Except(gaps, StringComparer.Ordinal).Any())
                throw Error("callback allocator coverage gap was omitted");
            if (callback.Map.CoverageGaps.Select(gap => MapGap(world.Key, gap)).Except(gaps, StringComparer.Ordinal).Any())
                throw Error("callback map coverage gap was omitted");
        }
        private static bool IsBorrowed(ulong capacity) => capacity > 1 && (capacity & 1) != 0;
        private static void ObserveUnused(Local live, string world, IntPtr buffer, int count, ulong encodedCapacity, int stride)
        {
            var capacity = Capacity(encodedCapacity, count, stride);
            AddStorage(live, buffer, checked(capacity * stride));
            if (capacity != 0 && (encodedCapacity & 1) != 0) {
                live.Gaps.Add("physics-borrowed-callback-storage:" + world); return;
            }
            if (capacity <= count) return;
            var address = IntPtr.Add(buffer, checked(count * stride)); var length = checked((capacity - count) * stride);
            if (buffer == IntPtr.Zero) throw Error("callback array has no backing buffer");
            if (live.Read(address, length).Any(value => value != 0)) {
                live.Gaps.Add("physics-unused-callback-buffer-needs-canon:" + world);
                live.CallbackUnused.Add(Tuple.Create(address, length));
            }
        }
        internal static void ValidateCallbacks(WorldData world, Dictionary<string, FixtureData> fixtures, Dictionary<string, string> fixtureOwners,
            ISet<string> colliderKeys, ISet<string> rigidbodies)
        {
            var cache = world.Callbacks;
            if (cache == null || cache.Rows == null || cache.Rows.Length != world.CallbackCollisionCount || cache.Rows.Length != world.CallbackPairCount)
                throw Error("callback vector/map row count or ownership differs");
            Exact(cache.VectorPadding, 4); Capacity(cache.Capacity, cache.Rows.Length, 8);
            if (cache.VectorPadding.Any(b => b != 0)) throw Error("callback vector padding is not portable");
            var contacts = world.Contacts.ToDictionary(c => c.Key, StringComparer.Ordinal);
            var activeCounts = new int[cache.Rows.Length];
            foreach (var c in world.Contacts) if (c.CollisionIndex >= 0) activeCounts[c.CollisionIndex]++;
            for (var i = 0; i < cache.Rows.Length; i++) {
                var row = cache.Rows[i];
                if (row == null || !colliderKeys.Contains(row.KeyA) || !colliderKeys.Contains(row.KeyB) || row.KeyA == row.KeyB
                    || row.ColliderA != row.KeyA || row.ColliderB != row.KeyB || row.Receiving == null ||
                    row.Receiving.Length != 0 && row.Receiving != row.KeyA && row.Receiving != row.KeyB ||
                    row.BodyA == null || row.BodyA.Length != 0 && !rigidbodies.Contains(row.BodyA) || row.BodyB == null || row.BodyB.Length != 0 && !rigidbodies.Contains(row.BodyB)
                    || row.ContactState < 0 || row.ContactState > 4 || row.ContactCount != activeCounts[i] || row.Manifolds == null || row.Manifolds.Length > MaximumElements)
                    throw Error("callback collider pair, receiving owner or contact count differs");
                Exact(row.FlagsAndPadding, 8); Flags(row.FlagsAndPadding[0], row.FlagsAndPadding[1], row.FlagsAndPadding[2], row.FlagsAndPadding[3]);
                Exact(row.ManifoldHeaderPadding, 4); Capacity(row.ManifoldCapacity, row.Manifolds.Length, 112);
                if (row.FlagsAndPadding.Skip(4).Any(b => b != 0) || row.ManifoldHeaderPadding.Any(b => b != 0))
                    throw Error("callback row padding is not portable");
                var isTrigger = row.FlagsAndPadding[1] != 0;
                if (isTrigger && row.Manifolds.Length != 0) throw Error("trigger callback contains solid contact points");
                for (var j = 0; j < row.Manifolds.Length; j++) {
                    var m = row.Manifolds[j];
                    if (m == null || m.Contact == null || !fixtures.TryGetValue(m.FixtureA, out var a) || !fixtures.TryGetValue(m.FixtureB, out var b)
                        || fixtureOwners[m.FixtureA] != row.KeyA || fixtureOwners[m.FixtureB] != row.KeyB ||
                        m.ChildA < 0 || m.ChildA >= a.Proxies.Length || m.ChildB < 0 || m.ChildB >= b.Proxies.Length)
                        throw Error("callback manifold fixture or collider binding differs");
                    if (m.Contact.Length != 0 && (!contacts.TryGetValue(m.Contact, out var c) || c.CollisionIndex != i || Int(c.Values, 176) != j ||
                        c.Key != ContactKey(m.FixtureA, m.ChildA, m.FixtureB, m.ChildB)))
                        throw Error("callback manifold has no matching contact userData/index");
                    Exact(m.EnabledAndPadding, 8); Flags(m.EnabledAndPadding[0]); Exact(m.Values, 72);
                    if (m.EnabledAndPadding.Skip(1).Any(b => b != 0)) throw Error("callback manifold padding is not portable");
                    var n = Int(m.Values, 4); if (n < 0 || n > 2) throw Error("callback point count differs");
                    if (n > 0) {
                        Finite(Slice(m.Values, 8, 8), 2); Finite(Slice(m.Values, 16, n * 8), n * 2); Finite(Slice(m.Values, 32, n * 8), n * 2);
                        foreach (var off in new[] { 48, 56, 64 }) Finite(Slice(m.Values, off, n * 4), n);
                    }
                }
            }
            foreach (var c in world.Contacts.Where(contact => contact.CollisionIndex >= 0)) {
                var row = cache.Rows[c.CollisionIndex];
                var userIndex = Int(c.Values, 176);
                if (row.FlagsAndPadding[1] != 0 ? userIndex != -1 : userIndex < 0 || userIndex >= row.Manifolds.Length || row.Manifolds[userIndex].Contact != c.Key)
                    throw Error("contact omits its callback manifold backlink");
            }
        }
        private static void PrepareCallbacks(WorldData world, Local target, Action admitted)
        {
            admitted();
            var owner = M.Ptr(IntPtr.Add(target.Scenes[world.Key], 40));
            var header = IntPtr.Add(owner, 40);
            var source = world.Callbacks;
            if (M.I32(M.Read(header, 12), 8) != source.VectorLabel) throw Error("callback vector allocator label differs");
            foreach (var row in source.Rows) {
                // BeginContact constructs the ordered key with signed, local
                // Unity instance IDs. An inversion would change future lookup.
                var a = M.I32(M.Read(IntPtr.Add(target.Owners[row.KeyA], 8), 4), 0);
                var b = M.I32(M.Read(IntPtr.Add(target.Owners[row.KeyB], 8), 4), 0);
                if (a >= b) throw Error("callback key ordering cannot be represented by the target collider identities");
            }
            var map = IntPtr.Add(owner, 8);
            var staged = Marshal.AllocHGlobal(32);
            try {
                var original = M.Read(map, 32);
                Marshal.Copy(original, 0, staged, original.Length);
                // Temporarily detach the table as an owning value. Native resize
                // reads/writes only this header and its own allocation. The live
                // PhysicsContacts2D publishes no lookup into retiring rows.
                var empty = (byte[])original.Clone();
                Array.Clear(empty, 0, 24);
                var sentinel = IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, 0x1723b3c);
                Buffer.BlockCopy(BitConverter.GetBytes(sentinel.ToInt64()), 0, empty, 0, 8);
                M.Write(map, empty);
                ResidentPhysicsCallbackMap.Restore(source.Map, staged, key => target.Owners[key], admitted);
                ReplaceCallbackDirectory(world, target, header, admitted);
                var replacement = M.Read(staged, 32);
                Array.Clear(replacement, 28, 4);
                M.Write(map, replacement);
            } finally {
                // Free only the temporary header. On failure the boundary faults
                // and the process retires; an old table is never republished.
                Marshal.FreeHGlobal(staged);
            }
        }
        private static void ReplaceCallbackDirectory(WorldData world, Local target, IntPtr header, Action admitted)
        {
            var source = world.Callbacks;
            var old = target.Data.Worlds.Single(w => w.Key == world.Key);
            foreach (var c in old.Contacts.Where(contact => contact.CollisionIndex >= 0)) {
                var ptr = target.Contacts[c.Key]; WritePointer(IntPtr.Add(ptr, 304), IntPtr.Zero);
                M.Write(IntPtr.Add(ptr, 296), BitConverter.GetBytes(-1));
            }
            // Nobody can dispatch a collision inside this native boundary.
            // Retire the old published vector before creating/reusing rows.
            M.Write(IntPtr.Add(header, 16), BitConverter.GetBytes(0L));
            var rows = ResidentPhysicsCallbackAllocator.PrepareRows(source.Allocator, target.CallbackAllocators[world.Key], admitted);
            var fresh = ResidentPhysicsCallbackAllocator.Capture(target.CallbackAllocators[world.Key].Owner, rows, admitted);
            fresh.CanonicalizeIdle(admitted);
            M.Write(IntPtr.Add(fresh.Owner, 4), new byte[4]);
            if (fresh.Data.Present) M.Write(IntPtr.Add(M.Ptr(fresh.OwnerWitness, 8), 28), new byte[4]);
            foreach (var chunk in fresh.Chunks) { admitted(); M.Write(IntPtr.Add(chunk, 26530), new byte[6]); }
            WritePointerArray(header, rows, source.Capacity, admitted);
            M.Write(IntPtr.Add(header, 12), source.VectorPadding);
            ClearUnused(M.Ptr(header), rows.Length, source.Capacity, 8);
            target.CollisionRows[world.Key] = rows;
        }
        private static void WriteCallbackRows(CallbackData source, Local target, IntPtr[] collisionRows, Action admitted)
        {
            if (source.Rows.Length != collisionRows.Length) throw Error("callback allocator did not return the requested row directory");
            for (var i = 0; i < source.Rows.Length; i++) {
                admitted();
                var row = source.Rows[i]; var ptr = collisionRows[i]; var header = M.Read(ptr, 32);
                if (Int(header, 8) != row.ManifoldLabel) throw Error("callback manifold owner allocator label differs");
                PrepareArraySize(ptr, row.Manifolds.Length, row.ManifoldCapacity, 112, 8, admitted);
                var buffer = M.Ptr(M.Read(ptr, 8), 0);
                M.Write(IntPtr.Add(ptr, 12), row.ManifoldHeaderPadding);
                M.Write(IntPtr.Add(ptr, 32), Join(BitConverter.GetBytes(row.ContactState), BitConverter.GetBytes(row.ContactCount)));
                WritePointer(IntPtr.Add(ptr, 40), target.Owners[row.ColliderA]); WritePointer(IntPtr.Add(ptr, 48), target.Owners[row.ColliderB]);
                WritePointer(IntPtr.Add(ptr, 56), Link(target.Owners, row.BodyA)); WritePointer(IntPtr.Add(ptr, 64), Link(target.Owners, row.BodyB));
                WritePointer(IntPtr.Add(ptr, 72), Link(target.Owners, row.Receiving));
                WritePointer(IntPtr.Add(ptr, 80), target.Owners[row.KeyA]); WritePointer(IntPtr.Add(ptr, 88), target.Owners[row.KeyB]); M.Write(IntPtr.Add(ptr, 96), row.FlagsAndPadding);
                for (var j = 0; j < row.Manifolds.Length; j++) {
                    if ((j & 31) == 0) admitted();
                    var m = row.Manifolds[j]; var dest = IntPtr.Add(buffer, j * 112);
                    M.Write(dest, m.EnabledAndPadding);
                    WritePointer(IntPtr.Add(dest, 8), Link(target.Contacts, m.Contact)); WritePointer(IntPtr.Add(dest, 16), target.Fixtures[m.FixtureA]);
                    WritePointer(IntPtr.Add(dest, 24), target.Fixtures[m.FixtureB]);
                    M.Write(IntPtr.Add(dest, 32), Join(BitConverter.GetBytes(m.ChildA), BitConverter.GetBytes(m.ChildB), m.Values));
                }
                ClearUnused(buffer, row.Manifolds.Length, row.ManifoldCapacity, 112);
            }
        }
        private static void ClearUnused(IntPtr buffer, int count, ulong capacity, int stride)
        {
            var elements = Capacity(capacity, count, stride) - count;
            if (elements > 0) {
                if ((capacity & 1) != 0 || buffer == IntPtr.Zero) throw Error("cannot clear borrowed callback storage");
                M.Write(IntPtr.Add(buffer, checked(count * stride)), new byte[checked(elements * stride)]);
            }
        }
    }
}
