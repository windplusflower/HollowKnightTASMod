using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentPhysicsState
    {
        // Pinned UnityPlayer PDB: b2Contact 312, embedded edges at 32 and 64.
        // StoreImpulses writes both per-point impulse fields in m_manifold.
        // Update copies it to m_oldManifold; neither manifold is recomputed.
        internal sealed class ContactData
        {
            public string Key = "", FixtureA = "", FixtureB = "";
            public int ChildA, ChildB, Vtable, CollisionIndex = -1;
            public uint Flags;
            // +120..299: both island indices, two complete 72-byte manifolds,
            // TOI count/value, friction, restitution, tangent speed, manager
            // index, and callback manifold index. No address fields.
            public byte[] Values = Array.Empty<byte>(), Padding = Array.Empty<byte>();
        }
        private static string ContactKey(string a, int childA, string b, int childB)
        {
            // Symmetric identity; source fixture orientation remains explicit.
            if (StringComparer.Ordinal.Compare(a, b) > 0) return ContactKey(b, childB, a, childA);
            return "physics/contact/" + a.Length.ToString(CultureInfo.InvariantCulture) + ":" + a + "/" + childA.ToString(CultureInfo.InvariantCulture)
                + "/" + b.Length.ToString(CultureInfo.InvariantCulture) + ":" + b + "/" + childB.ToString(CultureInfo.InvariantCulture);
        }
        private static void ReadContacts(Local live, WorldData world, byte[] state, byte[] cache,
            Tuple<IntPtr, byte[]>[] bodies, Dictionary<string, BodyData> bodyRows)
        {
            var engine = LoadingEngineClockProbe.RequirePinnedEngine();
            var callbackPointers = live.Read(M.Ptr(cache, 40), checked(world.CallbackCollisionCount * 8));
            var callbacks = Enumerable.Range(0, world.CallbackCollisionCount).Select(i => M.Ptr(callbackPointers, i * 8)).ToArray();
            var indexes = new Dictionary<IntPtr, int>();
            for (var i = 0; i < callbacks.Length; i++) if (callbacks[i] != IntPtr.Zero) indexes.Add(callbacks[i], i);
            live.CollisionRows.Add(world.Key, callbacks);
            var chain = Chain(live, M.Ptr(state, 128), 312, 24, null);
            world.ContactCount = chain.Length; world.Contacts = new ContactData[chain.Length];
            var endpoints = new Dictionary<IntPtr, Tuple<IntPtr, IntPtr>>();
            var fixtureBodies = live.Data.Colliders.SelectMany(c => c.Fixtures).ToDictionary(f => f.Key, f => live.Bodies[f.Body], StringComparer.Ordinal);
            for (var i = 0; i < chain.Length; i++) {
                var ptr = chain[i].Item1; var bytes = chain[i].Item2;
                if (M.Ptr(bytes, 16) != (i == 0 ? IntPtr.Zero : chain[i - 1].Item1)) throw Error("world contact backlink differs");
                var a = Name(live.FixtureNames, M.Ptr(bytes, 96), "contact fixture A"); var b = Name(live.FixtureNames, M.Ptr(bytes, 104), "contact fixture B");
                endpoints.Add(ptr, Tuple.Create(fixtureBodies[a], fixtureBodies[b]));
                var saved = new ContactData { FixtureA = a, FixtureB = b, ChildA = Int(bytes, 112), ChildB = Int(bytes, 116), Flags = M.U32(bytes, 8),
                    Vtable = Rva(M.Ptr(bytes, 0), engine.BaseAddress, engine.ModuleMemorySize), Values = Slice(bytes, 120, 180),
                    Padding = new byte[8] };
                RecordPadding(live, world.Key, ptr, bytes, 12, 4); RecordPadding(live, world.Key, ptr, bytes, 300, 4);
                saved.Key = ContactKey(a, saved.ChildA, b, saved.ChildB); Bind(live.Contacts, live.ContactNames, saved.Key, ptr);
                foreach (var gap in ContactGaps(saved)) live.Gaps.Add(gap);
                var user = M.Ptr(bytes, 304); var manifoldIndex = Int(bytes, 296);
                if (user != IntPtr.Zero) {
                    if (!indexes.TryGetValue(user, out saved.CollisionIndex)) throw Error("contact userData does not alias the scene's actual callback directory");
                    var collision = live.Read(user, 104);
                    if (collision[97] > 1) throw Error("collision trigger byte is invalid");
                    if (collision[97] == 0) {
                        var manifoldCount = DynamicCount(Slice(collision, 0, 32), 112, out _);
                        if (manifoldIndex < 0 || manifoldIndex >= manifoldCount ||
                            M.Ptr(live.Read(IntPtr.Add(M.Ptr(collision, 0), checked(manifoldIndex * 112 + 8)), 8), 0) != ptr)
                            throw Error("contact callback manifold index/backlink differs");
                    } else if (manifoldIndex != -1) throw Error("trigger contact has a solid callback manifold");
                } else if (manifoldIndex != -1) throw Error("unbound contact retains a callback manifold index");
                world.Contacts[i] = saved;
            }
            foreach (var body in bodies) {
                var row = bodyRows[live.BodyNames[body.Item1]];
                var edges = Chain(live, M.Ptr(body.Item2, 136), 32, 24, null);
                row.ContactOrder = new string[edges.Length];
                for (var i = 0; i < edges.Length; i++) {
                    var edge = edges[i].Item2; var ptr = M.Ptr(edge, 8); row.ContactOrder[i] = Name(live.ContactNames, ptr, "body contact edge");
                    if (!endpoints.TryGetValue(ptr, out var pair)) throw Error("body edge crosses a PhysicsScene2D");
                    var a = pair.Item1; var b = pair.Item2;
                    if (a == b || a != body.Item1 && b != body.Item1 || M.Ptr(edge, 0) != (a == body.Item1 ? b : a)
                        || edges[i].Item1 != IntPtr.Add(ptr, a == body.Item1 ? 32 : 64) || M.Ptr(edge, 16) != (i == 0 ? IntPtr.Zero : edges[i - 1].Item1))
                        throw Error("contact body membership or edge predecessor differs");
                }
            }
            world.NonToiContacts = ReadPointerArray(live, Slice(state, 160, 32), live.ContactNames, "non-TOI contact", out world.NonToiContactCapacity);
            world.ToiContacts = ReadPointerArray(live, Slice(state, 192, 32), live.ContactNames, "TOI contact", out world.ToiContactCapacity);
        }
        private static IEnumerable<string> ContactGaps(ContactData contact)
        {
            if ((contact.Flags & ~0xffU) != 0) yield return "physics-contact-deferred-simulation-or-unknown-flags:" + contact.Key;
            // New-contact construction initializes only current.pointCount.
            // An untouched old manifold has no proved portable invalid count.
            var oldCount = Int(contact.Values, 140);
            if (oldCount < 0 || oldCount > 2) yield return "physics-contact-old-manifold-uninitialized:" + contact.Key;
        }
        private static void ValidateManifold(byte[] values, int at, int count)
        {
            if (count <= 0 || count > 2) return;
            if (Int(values, at + 56) < 0 || Int(values, at + 56) > 2) throw Error("active contact manifold type differs");
            for (var p = 0; p < count; p++) Finite(Slice(values, at + p * 20, 16), 4);
            Finite(Slice(values, at + 40, 16), 4); Finite(Slice(values, at + 64, 8), 2);
        }
        private static int ContactVtable(int a, int b)
        {
            // RVAs written by each concrete Create from b2Contact's registry.
            // The input keys are in constructor order (A is the primary shape).
            switch (a * 5 + b) {
                case 0: return 0x16fea80;   // circle/circle
                case 5: return 0x16feab0;   // edge/circle
                case 7: return 0x16feac0;   // edge/polygon
                case 9: return 0x16fead0;   // edge/capsule (folded with capsule/capsule)
                case 10: return 0x16feaf0;  // polygon/circle
                case 12: return 0x16feaa0;  // polygon/polygon
                case 14: return 0x16fea90;  // polygon/capsule
                case 15: return 0x16fea60;  // chain/circle
                case 17: return 0x16fea70;  // chain/polygon
                case 19: return 0x16feae0;  // chain/capsule
                case 20: return 0x16fea50;  // capsule/circle
                case 24: return 0x16fead0;  // capsule/capsule
                default: throw Error("Box2D contact shape pair has no supported typed constructor");
            }
        }
        internal static void ValidateContacts(WorldData world, Dictionary<string, FixtureData> fixtures, Dictionary<string, BodyData> bodies, string[] gaps)
        {
            if (world.Contacts == null || world.ContactCount != world.Contacts.Length || world.Contacts.Length > MaximumElements
                || world.NonToiContacts == null || world.ToiContacts == null || world.NonToiContacts.Length + (long)world.ToiContacts.Length != world.ContactCount)
                throw Error("contact directory or manager queue counts differ");
            Capacity(world.NonToiContactCapacity, world.NonToiContacts.Length, 8); Capacity(world.ToiContactCapacity, world.ToiContacts.Length, 8);
            if (IsBorrowed(world.NonToiContactCapacity) && !gaps.Contains("borrowed-physics-pointer-buffer:non-TOI contact")
                || IsBorrowed(world.ToiContactCapacity) && !gaps.Contains("borrowed-physics-pointer-buffer:TOI contact"))
                throw Error("borrowed contact queue lost its coverage gap");
            var rows = new Dictionary<string, ContactData>(StringComparer.Ordinal);
            var incidence = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var body in world.BodyOrder) incidence.Add(body, new HashSet<string>(StringComparer.Ordinal));
            foreach (var c in world.Contacts) {
                if (c == null || c.FixtureA == null || c.FixtureB == null || !fixtures.TryGetValue(c.FixtureA, out var a) || !fixtures.TryGetValue(c.FixtureB, out var b)
                    || a.Body == b.Body || !incidence.ContainsKey(a.Body) || !incidence.ContainsKey(b.Body) || c.ChildA < 0 || c.ChildA >= a.Proxies.Length
                    || c.ChildB < 0 || c.ChildB >= b.Proxies.Length || c.Key != ContactKey(c.FixtureA, c.ChildA, c.FixtureB, c.ChildB)
                    || c.Vtable != ContactVtable(a.Shape.Type, b.Shape.Type) || rows.ContainsKey(c.Key)) throw Error("contact type or fixture/child identity differs");
                Exact(c.Values, 180); Exact(c.Padding, 8);
                if (c.Padding.Any(b => b != 0)) throw Error("contact padding is not portable");
                if (c.CollisionIndex < -1 || c.CollisionIndex >= world.CallbackCollisionCount || Int(c.Values, 176) < -1 || c.CollisionIndex == -1 && Int(c.Values, 176) != -1)
                    throw Error("contact callback binding differs");
                if (Int(c.Values, 68) < 0 || Int(c.Values, 68) > 2) throw Error("current contact manifold point count differs");
                ValidateManifold(c.Values, 8, Int(c.Values, 68)); ValidateManifold(c.Values, 80, Int(c.Values, 140));
                if (ContactGaps(c).Except(gaps, StringComparer.Ordinal).Any()) throw Error("an unresolved contact state lost its coverage gap");
                rows.Add(c.Key, c); incidence[a.Body].Add(c.Key); incidence[b.Body].Add(c.Key);
            }
            var queued = new HashSet<string>(StringComparer.Ordinal);
            void Queue(string[] order, bool toi) {
                for (var i = 0; i < order.Length; i++) {
                    if (order[i] == null || !rows.TryGetValue(order[i], out var c) || !queued.Add(c.Key) || Int(c.Values, 172) != i || ((c.Flags & 0x80) != 0) != toi)
                        throw Error("contact manager index, TOI flag or queue membership differs");
                }
            }
            Queue(world.NonToiContacts, false); Queue(world.ToiContacts, true);
            foreach (var pair in incidence) {
                var order = bodies[pair.Key].ContactOrder;
                if (order == null || order.Length != pair.Value.Count || !pair.Value.SetEquals(order)) throw Error("contact omitted or duplicated a body's embedded edge");
            }
        }
        private static void ValidateContactCells(WorldData world, ResidentPhysicsGlobals.Scene global)
        {
            if (world.NonToiContactCapacity != global.NonToiCapacity || world.ToiContactCapacity != global.ToiCapacity)
                throw Error("global/contact queue ownership differs");
            var owned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var chunk in global.Blocks.Chunks) foreach (var name in chunk.Owners)
                if (name.StartsWith("physics/contact/", StringComparison.Ordinal) && (chunk.BlockSize != 320 || !owned.Add(name)))
                    throw Error("contact allocation has a wrong or repeated block owner");
            if (!owned.SetEquals(world.Contacts.Select(c => c.Key))) throw Error("contact allocator omits or invents a live contact");
        }
        private static void ApplyContacts(WorldData world, Dictionary<string, BodyData> sourceBodies, Local target, IntPtr state, Action admitted)
        {
            var rows = world.Contacts.ToDictionary(c => c.Key, StringComparer.Ordinal);
            var fixtureBodies = target.Data.Colliders.SelectMany(c => c.Fixtures).ToDictionary(f => f.Key, f => f.Body, StringComparer.Ordinal);
            IntPtr Edge(string body, string key) => IntPtr.Add(target.Contacts[key], fixtureBodies[rows[key].FixtureA] == body ? 32 : 64);
            var callbacks = target.CollisionRows[world.Key];
            WritePointer(IntPtr.Add(state, 128), world.Contacts.Length == 0 ? IntPtr.Zero : target.Contacts[world.Contacts[0].Key]);
            for (var i = 0; i < world.Contacts.Length; i++) {
                var c = world.Contacts[i]; admitted(); var ptr = target.Contacts[c.Key];
                WritePointer(IntPtr.Add(ptr, 16), i == 0 ? IntPtr.Zero : target.Contacts[world.Contacts[i - 1].Key]);
                WritePointer(IntPtr.Add(ptr, 24), i + 1 == world.Contacts.Length ? IntPtr.Zero : target.Contacts[world.Contacts[i + 1].Key]);
                M.Write(IntPtr.Add(ptr, 8), Join(BitConverter.GetBytes(c.Flags), Slice(c.Padding, 0, 4)));
                WritePointer(IntPtr.Add(ptr, 96), target.Fixtures[c.FixtureA]); WritePointer(IntPtr.Add(ptr, 104), target.Fixtures[c.FixtureB]);
                M.Write(IntPtr.Add(ptr, 112), Join(BitConverter.GetBytes(c.ChildA), BitConverter.GetBytes(c.ChildB), c.Values, Slice(c.Padding, 4, 4)));
                M.Write(IntPtr.Add(ptr, 312), new byte[8]); // proven 320-byte owned allocator cell
                WritePointer(IntPtr.Add(ptr, 304), c.CollisionIndex == -1 ? IntPtr.Zero : callbacks[c.CollisionIndex]);
                WritePointer(IntPtr.Add(ptr, 32), target.Bodies[fixtureBodies[c.FixtureB]]); WritePointer(IntPtr.Add(ptr, 40), ptr);
                WritePointer(IntPtr.Add(ptr, 64), target.Bodies[fixtureBodies[c.FixtureA]]); WritePointer(IntPtr.Add(ptr, 72), ptr);
            }
            foreach (var key in world.BodyOrder) {
                admitted();
                var order = sourceBodies[key].ContactOrder;
                WritePointer(IntPtr.Add(target.Bodies[key], 136), order.Length == 0 ? IntPtr.Zero : Edge(key, order[0]));
                for (var i = 0; i < order.Length; i++) {
                    if ((i & 31) == 0) admitted();
                    var ptr = Edge(key, order[i]);
                    WritePointer(IntPtr.Add(ptr, 16), i == 0 ? IntPtr.Zero : Edge(key, order[i-1]));
                    WritePointer(IntPtr.Add(ptr, 24), i+1 == order.Length ? IntPtr.Zero : Edge(key, order[i+1]));
                }
            }
            WritePointerArray(IntPtr.Add(state, 160), world.NonToiContacts.Select(key => target.Contacts[key]).ToArray(), world.NonToiContactCapacity, admitted);
            WritePointerArray(IntPtr.Add(state, 192), world.ToiContacts.Select(key => target.Contacts[key]).ToArray(), world.ToiContactCapacity, admitted);
        }
    }
}
