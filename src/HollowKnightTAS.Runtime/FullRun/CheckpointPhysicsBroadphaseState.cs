using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Pinned Box2D broadphase continuation at the unloaded C. Fixture/proxy
    // addresses are rebound locally; only names, indices and value bytes travel.
    // Contacts, joints, body solver and movement caches remain separate domains.
    [Serializable]
    internal sealed partial class CheckpointPhysicsBroadphaseState
    {
        public int Version = 1, Root, Count, FreeHead, InsertionCount, ProxyCount, QueryProxy, WorldFlags, SimulationId;
        public uint Path;
        public byte[] Step = null!;
        public Node[] Nodes = null!;
        public int[] Moves = null!, Pairs = null!;
        [Serializable] internal sealed class Node
        {
            public int Parent, Child1 = -1, Child2 = -1, Height;
            public string? Proxy;
            public byte[] Bounds = Array.Empty<byte>(), ProxyBounds = Array.Empty<byte>();
            public int ProxyChild;
        }
        private const int WorldState = 103072, MaximumNodes = 32768;
        private static readonly FieldInfo CachedPointer = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)!;
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr read);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr written);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AllocateNode(IntPtr tree);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void InsertLeaf(IntPtr tree, int node);
        private static byte[] Read(IntPtr address, int length)
        {
            if (address == IntPtr.Zero || length < 0 || length > MaximumNodes * 40) throw new InvalidOperationException("Broadphase memory range differs.");
            var bytes = new byte[length];
            if (length != 0 && (!ReadProcessMemory(GetCurrentProcess(), address, bytes, new UIntPtr((uint)length), out var read) || read.ToUInt64() != (ulong)length))
                throw new InvalidOperationException("Broadphase memory read failed.");
            return bytes;
        }
        private static void Write(IntPtr address, byte[] bytes)
        {
            if (address == IntPtr.Zero || bytes.Length > MaximumNodes * 40
                || !WriteProcessMemory(GetCurrentProcess(), address, bytes, new UIntPtr((uint)bytes.Length), out var written) || written.ToUInt64() != (ulong)bytes.Length)
                throw new InvalidOperationException("Broadphase memory write failed.");
        }
        private static IntPtr Pointer(byte[] b, int offset) => new IntPtr(BitConverter.ToInt64(b, offset));
        private static int Int(byte[] b, int offset) => BitConverter.ToInt32(b, offset);
        private static byte[] Slice(byte[] b, int offset, int size) => b.Skip(offset).Take(size).ToArray();
        private static IntPtr Native(UnityEngine.Object value) => (IntPtr)CachedPointer.GetValue(value)!;
        private static int Bound(long value, int maximum, string name)
        { if (value < 0 || value > maximum) throw new InvalidDataException("Broadphase " + name + " exceeds its bound."); return (int)value; }
        private static string OwnerKey(Transform transform, GameObject[] spatters, GameObject[] geos, GameObject[] projectiles)
        {
            var segments = new List<string>(); var current = transform;
            while (current.parent != null)
            {
                var ordinal = 0;
                for (var i = 0; i < current.GetSiblingIndex(); i++) if (current.parent.GetChild(i).name == current.name) ordinal++;
                segments.Add(current.name + "[" + ordinal + "]"); current = current.parent;
            }
            var spatter = Array.FindIndex(spatters, go => ReferenceEquals(go.transform, current));
            var geo = Array.FindIndex(geos, go => ReferenceEquals(go.transform, current));
            var projectile = Array.FindIndex(projectiles, go => ReferenceEquals(go.transform, current));
            var root = spatter >= 0 ? "pool-spatter[" + spatter + "]" : geo >= 0 ? "pool-geo[" + geo + "]"
                : projectile >= 0 ? "pool-projectile[" + projectile + "]" : current.name;
            if (spatter < 0 && geo < 0 && projectile < 0 && current.gameObject.scene.GetRootGameObjects().Count(go => go.name == current.name) != 1)
                throw new InvalidOperationException("Broadphase root identity is ambiguous: " + current.name);
            segments.Reverse(); return root + "/" + string.Join("/", segments);
        }
        private sealed class Live
        {
            internal IntPtr Scene, World, State, Tree, Cache;
            internal byte[] Header = null!;
            internal Dictionary<string, IntPtr> Proxies = new Dictionary<string, IntPtr>();
        }
        private static Live Bind(GameObject[]? restoredSpatters = null, GameObject[]? restoredGeos = null, GameObject[]? restoredProjectiles = null,
            BoundaryObjectIdentity? canonicalIdentities = null)
        {
            LoadingEngineClockProbe.RequirePinnedEngine();
            var hero = HeroController.SilentInstance ?? throw new InvalidOperationException("Broadphase Hero is absent.");
            var body = Read(Native(hero.GetComponent<Rigidbody2D>()), 296); var scene = Pointer(body, 288);
            var sceneBytes = Read(scene, 1184); var world = Pointer(sceneBytes, 8); var state = IntPtr.Add(world, WorldState);
            var header = Read(state, 360); var cache = Pointer(sceneBytes, 40);
            // No native call is running while the checkpoint callback is parked.
            if (sceneBytes[1180] != 0 || (Int(header, 0) & 2) != 0 || Int(header, 244) != 0 || Pointer(header, 128) != IntPtr.Zero)
                throw new InvalidOperationException("Broadphase continuation requires a quiescent contact/joint-free C.");
            if (cache == IntPtr.Zero) throw new InvalidOperationException("Broadphase callback cache is absent.");
            var cached = Read(cache, 88);
            if (BitConverter.ToUInt32(cached, 20) != 0 || BitConverter.ToUInt64(cached, 56) != 0)
                throw new InvalidOperationException("Broadphase callback cache is not empty at C.");
            var result = new Live { Scene = scene, World = world, State = state, Tree = IntPtr.Add(state, 8), Cache = cache, Header = header };
            // Capture assigns ordinals from the source pool. Restore rebinds
            // those ordinals to its explicit prepared actor array; a fresh
            // Dictionary can enumerate different free slots after recycling.
            var spatters = restoredSpatters ?? CheckpointSpatterState.LiveActors();
            var geos = restoredGeos ?? CheckpointGeoState.LiveActors();
            var projectiles = restoredProjectiles ?? CheckpointProjectileState.LiveActors();
            foreach (var collider in Resources.FindObjectsOfTypeAll<Collider2D>())
            {
                if (collider == null || !collider.gameObject.scene.IsValid() || !collider.gameObject.scene.isLoaded) continue;
                var pointer = Native(collider); if (pointer == IntPtr.Zero) continue;
                var owner = Read(pointer, 224); var count = Bound(BitConverter.ToInt64(owner, 104), 4096, "collider fixture count");
                // dynamic_array stores size at +16 relative to its +88 header.
                if (count == 0) continue;
                var fixtures = Read(Pointer(owner, 88), count * 8);
                var components = collider.GetComponents<Collider2D>(); var component = Array.FindIndex(components, c => ReferenceEquals(c, collider));
                for (var i = 0; i < count; i++)
                {
                    var fixturePointer = Pointer(fixtures, i * 8); var fixture = Read(fixturePointer, 88);
                    var proxies = Bound(Int(fixture, 64), MaximumNodes, "fixture proxies");
                    if (proxies == 0) continue;
                    if (Pointer(owner, 200) != IntPtr.Zero || Pointer(owner, 208) != IntPtr.Zero)
                        throw new InvalidOperationException("Broadphase effector/composite fixture binding is unsupported.");
                    var id = (canonicalIdentities == null ? OwnerKey(collider.transform, spatters, geos, projectiles)
                        : canonicalIdentities.Get(collider.gameObject)) + "/collider[" + component + "]";
                    var nativeBody = Read(Pointer(fixture, 32), 200);
                    if (Pointer(nativeBody, 88) != world || Pointer(fixture, 80) != pointer)
                        throw new InvalidOperationException("Broadphase fixture world/owner binding differs.");
                    var proxyBase = Pointer(fixture, 56);
                    for (var j = 0; j < proxies; j++)
                    {
                        var proxy = IntPtr.Add(proxyBase, j * 32); var bytes = Read(proxy, 32);
                        if (Pointer(bytes, 16) != fixturePointer || Int(bytes, 24) != j)
                            throw new InvalidOperationException("Broadphase fixture proxy binding differs.");
                        result.Proxies.Add(id + "/fixture[" + i + "]/proxy[" + j + "]", proxy);
                    }
                }
            }
            return result;
        }
        private static int[] Buffer(byte[] header, int offset, int stride)
        {
            var count = Bound(BitConverter.ToInt64(header, offset + 16), MaximumNodes * 2, "buffer size");
            if (count > BitConverter.ToInt64(header, offset + 24)) throw new InvalidOperationException("Broadphase buffer size exceeds capacity.");
            if (count == 0) return Array.Empty<int>();
            var b = Read(Pointer(header, offset), count * stride); return Enumerable.Range(0, b.Length / 4).Select(i => Int(b, i * 4)).ToArray();
        }
        internal static CheckpointPhysicsBroadphaseState Capture(GameObject[]? restoredSpatters = null, GameObject[]? restoredGeos = null, GameObject[]? restoredProjectiles = null)
        {
            return Capture(Bind(restoredSpatters, restoredGeos, restoredProjectiles));
        }
        private static CheckpointPhysicsBroadphaseState Capture(Live live)
        {
            var b = live.Header; var capacity = Bound(Int(b, 28), MaximumNodes, "node capacity");
            var count = Bound(Int(b, 24), capacity, "node count"); var bytes = Read(Pointer(b, 16), capacity * 40);
            var aliases = live.Proxies.ToDictionary(p => p.Value, p => p.Key);
            var nodes = new Node[capacity];
            for (var i = 0; i < capacity; i++)
            {
                var offset = i * 40; var height = Int(bytes, offset + 36);
                var node = new Node { Height = height, Parent = Int(bytes, offset + 24) }; nodes[i] = node;
                if (height == -1) continue;
                node.Bounds = Slice(bytes, offset, 16); node.Child1 = Int(bytes, offset + 28); node.Child2 = Int(bytes, offset + 32);
                if (node.Child1 != -1) continue;
                var proxy = Pointer(bytes, offset + 16);
                if (!aliases.TryGetValue(proxy, out node.Proxy)) throw new InvalidOperationException("Broadphase leaf is unbound.");
                var proxyBytes = Read(proxy, 32); node.ProxyBounds = Slice(proxyBytes, 0, 16); node.ProxyChild = Int(proxyBytes, 24);
                if (Int(proxyBytes, 28) != i) throw new InvalidOperationException("Broadphase proxy/node identity differs.");
            }
            var result = new CheckpointPhysicsBroadphaseState { Root = Int(b, 8), Count = count, FreeHead = Int(b, 32),
                Path = BitConverter.ToUInt32(b, 36), InsertionCount = Int(b, 40), ProxyCount = Int(b, 48), QueryProxy = Int(b, 120),
                WorldFlags = Int(b, 0), Step = Slice(b, 344, 8), SimulationId = Int(Read(live.Cache, 88), 80), Nodes = nodes,
                Pairs = Buffer(b, 56, 8), Moves = Buffer(b, 88, 4) };
            result.Validate();
            if (live.Proxies.Count != result.ProxyCount) throw new InvalidOperationException("Broadphase live proxy coverage differs.");
            Modding.Logger.Log("[HKTAS] Captured broadphase: nodes=" + count + " capacity=" + capacity + " proxies=" + result.ProxyCount + " free=" + result.FreeHead);
            return result;
        }

        // Pinned native InsertLeaf preserves the engine's tree algorithm. The
        // allocator array has one fixed capacity and contains no dead bytes or
        // history-dependent free links. Existing fixture addresses stay owned
        // by Unity; only the broadphase topology is rebuilt.
        internal static CheckpointPhysicsBroadphaseState Canonicalize(BoundaryObjectIdentity identities)
        {
            using var validation = new LoadingEngineClockProbe.CheckpointValidation();
            var live = Bind(Array.Empty<GameObject>(), Array.Empty<GameObject>(), Array.Empty<GameObject>(), identities);
            if (live.Proxies.Count * 2 > MaximumNodes) throw new InvalidOperationException("Canonical broadphase proxy budget exceeded.");
            var engine = LoadingEngineClockProbe.RequirePinnedEngine();
            var allocateAddress = IntPtr.Add(engine.BaseAddress, 0xb9d5a0);
            var insertAddress = IntPtr.Add(engine.BaseAddress, 0xba0b10);
            if (!Read(allocateAddress, 17).SequenceEqual(new byte[] { 0x40,0x53,0x48,0x83,0xec,0x20,0x83,0x79,0x18,0xff,0x48,0x8b,0xd9,0x0f,0x85,0xbe,0x00 })
                || !Read(insertAddress, 24).SequenceEqual(new byte[] { 0x48,0x89,0x5c,0x24,0x18,0x56,0x57,0x41,0x57,0x48,0x81,0xec,0x80,0x00,0x00,0x00,0xff,0x41,0x20,0x48,0x8b,0xd9,0x48,0x63 }))
                throw new InvalidOperationException("Canonical broadphase allocator/insertion code differs.");
            var allocate = (AllocateNode)Marshal.GetDelegateForFunctionPointer(allocateAddress, typeof(AllocateNode));
            var insert = (InsertLeaf)Marshal.GetDelegateForFunctionPointer(insertAddress, typeof(InsertLeaf));
            var proxies = live.Proxies.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new { pair.Key, Address = pair.Value, Bytes = Read(pair.Value, 32) }).ToArray();
            if (proxies.Length > BitConverter.ToInt64(live.Header, 112)
                || proxies.Any(proxy => Enumerable.Range(0, 4).Any(axis => float.IsNaN(BitConverter.ToSingle(proxy.Bytes, axis * 4))
                    || float.IsInfinity(BitConverter.ToSingle(proxy.Bytes, axis * 4)))))
                throw new InvalidOperationException("Canonical broadphase bounds or move buffer differ.");
            var tree = Read(live.Tree, 40);
            if (Int(tree, 20) > MaximumNodes) throw new InvalidOperationException("Canonical broadphase capacity exceeds its fixed contract.");
            for (var attempts = 0; Int(tree, 20) < MaximumNodes; attempts++)
            {
                if (attempts >= MaximumNodes) throw new InvalidOperationException("Canonical broadphase capacity preparation exceeded its bound.");
                allocate(live.Tree);
                tree = Read(live.Tree, 40);
            }
            if (Int(tree, 20) != MaximumNodes) throw new InvalidOperationException("Canonical broadphase allocation differs from its fixed capacity.");
            var nodes = new byte[MaximumNodes * 40];
            for (var i = 0; i < MaximumNodes; i++)
            {
                Array.Copy(BitConverter.GetBytes(i + 1 == MaximumNodes ? -1 : i + 1), 0, nodes, i * 40 + 24, 4);
                Array.Copy(BitConverter.GetBytes(-1), 0, nodes, i * 40 + 28, 4);
                Array.Copy(BitConverter.GetBytes(-1), 0, nodes, i * 40 + 32, 4);
                Array.Copy(BitConverter.GetBytes(-1), 0, nodes, i * 40 + 36, 4);
            }
            Write(Pointer(tree, 8), nodes);
            Write(live.Tree, BitConverter.GetBytes(-1));
            Write(IntPtr.Add(live.Tree, 16), new[] { 0, MaximumNodes, 0, 0, 0 }.SelectMany(BitConverter.GetBytes).ToArray());
            Write(IntPtr.Add(live.Tree, 40), BitConverter.GetBytes(0));
            // The source unload already removed all contacts. Re-query every
            // live proxy in stable order when the target is activated.
            if (proxies.Length > BitConverter.ToInt64(live.Header, 112))
                throw new InvalidOperationException("Canonical broadphase move buffer is too small.");
            Write(IntPtr.Add(live.State, 72), BitConverter.GetBytes(0L));
            var moves = new List<int>();
            foreach (var proxy in proxies)
            {
                var node = allocate(live.Tree);
                var leaf = new byte[40];
                for (var axis = 0; axis < 4; axis++)
                {
                    var value = BitConverter.ToSingle(proxy.Bytes, axis * 4);
                    if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidOperationException("Canonical broadphase bounds are not finite.");
                    // The policy uses fixed 0.1 loose-bound padding. Clear the
                    // movement-history expansion; retain the live fixture AABB.
                    Array.Copy(BitConverter.GetBytes(value + (axis < 2 ? -0.1f : 0.1f)), 0, leaf, axis * 4, 4);
                }
                Array.Copy(BitConverter.GetBytes(proxy.Address.ToInt64()), 0, leaf, 16, 8);
                Array.Copy(BitConverter.GetBytes(-1), 0, leaf, 24, 4);
                Array.Copy(BitConverter.GetBytes(-1), 0, leaf, 28, 4);
                Array.Copy(BitConverter.GetBytes(-1), 0, leaf, 32, 4);
                tree = Read(live.Tree, 40);
                Write(IntPtr.Add(Pointer(tree, 8), node * 40), leaf);
                Write(IntPtr.Add(proxy.Address, 28), BitConverter.GetBytes(node));
                insert(live.Tree, node);
                moves.Add(node);
            }
            Write(IntPtr.Add(live.Tree, 40), BitConverter.GetBytes(proxies.Length));
            Write(IntPtr.Add(live.Tree, 112), BitConverter.GetBytes(-1));
            if (moves.Count != 0) Write(Pointer(live.Header, 88), moves.SelectMany(BitConverter.GetBytes).ToArray());
            Write(IntPtr.Add(live.State, 104), BitConverter.GetBytes((long)moves.Count));
            var after = Capture(Bind(Array.Empty<GameObject>(), Array.Empty<GameObject>(), Array.Empty<GameObject>(), identities));
            if (after.Nodes.Length != MaximumNodes || after.ProxyCount != proxies.Length || after.InsertionCount != proxies.Length
                || after.Path != 0 || after.Pairs.Length != 0 || !after.Moves.SequenceEqual(moves))
                throw new InvalidOperationException("Canonical broadphase topology readback differs.");
            return after;
        }
        private void Validate()
        {
            if (Version != 1 || Nodes == null || Nodes.Length == 0 || Nodes.Length > MaximumNodes || Step == null || Step.Length != 8
                || Pairs == null || Pairs.Length > MaximumNodes * 4 || (Pairs.Length & 1) != 0 || Moves == null || Moves.Length > MaximumNodes * 2)
                throw new InvalidDataException("Broadphase checkpoint schema differs.");
            if (Nodes.Any(n => n == null || n.Height < -1 || n.Height > 64) || (WorldFlags & 2) != 0)
                throw new InvalidDataException("Broadphase checkpoint nodes/lock state differ.");
            var free = new HashSet<int>(); var active = new HashSet<int>(); var proxies = new HashSet<string>();
            for (var n = FreeHead; n != -1; n = Nodes[n].Parent)
                if (n < 0 || n >= Nodes.Length || !free.Add(n) || Nodes[n].Height != -1) throw new InvalidDataException("Broadphase free chain differs.");
            void Visit(int i, int parent)
            {
                if (i < 0 || i >= Nodes.Length || !active.Add(i) || Nodes[i].Parent != parent || Nodes[i].Height < 0)
                    throw new InvalidDataException("Broadphase active tree differs.");
                var node = Nodes[i];
                if (node.Bounds == null || node.Bounds.Length != 16) throw new InvalidDataException("Broadphase bounds differ.");
                for (var j = 0; j < 4; j++) if (float.IsNaN(BitConverter.ToSingle(node.Bounds, j * 4)) || float.IsInfinity(BitConverter.ToSingle(node.Bounds, j * 4)))
                    throw new InvalidDataException("Broadphase bounds are not finite.");
                if (node.Child1 == -1)
                {
                    if (node.Child2 != -1 || node.Height != 0 || node.Proxy == null || node.Proxy.Length > 4096 || !proxies.Add(node.Proxy) || node.ProxyBounds == null || node.ProxyBounds.Length != 16)
                        throw new InvalidDataException("Broadphase leaf differs.");
                    for (var j = 0; j < 4; j++) if (float.IsNaN(BitConverter.ToSingle(node.ProxyBounds, j * 4)) || float.IsInfinity(BitConverter.ToSingle(node.ProxyBounds, j * 4)))
                        throw new InvalidDataException("Broadphase proxy bounds are not finite.");
                }
                else
                {
                    if (node.Proxy != null || node.Child1 < 0 || node.Child1 >= Nodes.Length || node.Child2 < 0 || node.Child2 >= Nodes.Length
                        || node.Height != 1 + Math.Max(Nodes[node.Child1].Height, Nodes[node.Child2].Height))
                        throw new InvalidDataException("Broadphase internal node/height differs.");
                    Visit(node.Child1, i); Visit(node.Child2, i);
                }
            }
            if (Root != -1) Visit(Root, -1);
            if (active.Count != Count || proxies.Count != ProxyCount || active.Overlaps(free) || active.Count + free.Count != Nodes.Length
                || Pairs.Concat(Moves).Any(i => i < -1 || i >= Nodes.Length)) throw new InvalidDataException("Broadphase node coverage differs.");
        }
        internal Action PrepareApply(GameObject[] spatters, GameObject[] geos, GameObject[] projectiles)
        {
            Validate();
            if (spatters == null || geos == null || projectiles == null || spatters.Concat(geos).Concat(projectiles).Any(go => go == null)
                || spatters.Concat(geos).Concat(projectiles).Distinct().Count() != spatters.Length + geos.Length + projectiles.Length)
                throw new InvalidDataException("Broadphase prepared actor aliases differ.");
            return () =>
            {
                var live = Bind(spatters, geos, projectiles); var aliases = Nodes.Where(n => n.Proxy != null).Select(n => n.Proxy!).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                if (!aliases.SequenceEqual(live.Proxies.Keys.OrderBy(x => x, StringComparer.Ordinal)))
                    throw new InvalidOperationException("Broadphase target fixture proxy identities differ: missing=" + string.Join(",", aliases.Except(live.Proxies.Keys))
                        + " extra=" + string.Join(",", live.Proxies.Keys.Except(aliases)));
                if (Pairs.Length / 2 > BitConverter.ToInt64(live.Header, 80) || Moves.Length > BitConverter.ToInt64(live.Header, 112))
                    throw new InvalidOperationException("Broadphase target buffer capacity differs.");
                var engine = LoadingEngineClockProbe.RequirePinnedEngine(); var allocateAddress = IntPtr.Add(engine.BaseAddress, 0xb9d5a0);
                if (!Read(allocateAddress, 17).SequenceEqual(new byte[] { 0x40,0x53,0x48,0x83,0xec,0x20,0x83,0x79,0x18,0xff,0x48,0x8b,0xd9,0x0f,0x85,0xbe,0x00 }))
                    throw new InvalidOperationException("Broadphase node allocator code differs.");
                var allocate = (AllocateNode)Marshal.GetDelegateForFunctionPointer(allocateAddress, typeof(AllocateNode));
                var reserved = new List<int>(); var tree = Read(live.Tree, 40);
                while (Int(tree, 20) < Nodes.Length)
                {
                    if (reserved.Count > MaximumNodes) throw new InvalidOperationException("Broadphase capacity preparation exceeded its bound.");
                    reserved.Add(allocate(live.Tree)); tree = Read(live.Tree, 40);
                }
                // The engine allocated any larger array. Its owned pointer and
                // allocator remain intact; all reserved slots are replaced below.
                var nodeBytes = Read(Pointer(tree, 8), Nodes.Length * 40);
                for (var i = 0; i < Nodes.Length; i++)
                {
                    var n = Nodes[i]; var offset = i * 40;
                    Array.Copy(BitConverter.GetBytes(n.Parent), 0, nodeBytes, offset + 24, 4);
                    Array.Copy(BitConverter.GetBytes(n.Height), 0, nodeBytes, offset + 36, 4);
                    if (n.Height == -1) continue;
                    Array.Copy(n.Bounds, 0, nodeBytes, offset, 16);
                    Array.Copy(BitConverter.GetBytes(n.Child1), 0, nodeBytes, offset + 28, 4);
                    Array.Copy(BitConverter.GetBytes(n.Child2), 0, nodeBytes, offset + 32, 4);
                    var proxy = n.Proxy == null ? IntPtr.Zero : live.Proxies[n.Proxy];
                    Array.Copy(BitConverter.GetBytes(proxy.ToInt64()), 0, nodeBytes, offset + 16, 8);
                    if (proxy != IntPtr.Zero)
                    {
                        Write(proxy, n.ProxyBounds); Write(IntPtr.Add(proxy, 24), BitConverter.GetBytes(n.ProxyChild));
                        Write(IntPtr.Add(proxy, 28), BitConverter.GetBytes(i));
                    }
                }
                void ApplyBuffer(int offset, int[] values, int stride)
                {
                    var header = Read(IntPtr.Add(live.State, offset), 32); var count = values.Length * 4 / stride;
                    if (count > BitConverter.ToInt64(header, 24)) throw new InvalidOperationException("Broadphase target buffer capacity differs.");
                    if (values.Length != 0) Write(Pointer(header, 0), values.SelectMany(BitConverter.GetBytes).ToArray());
                    Write(IntPtr.Add(live.State, offset + 16), BitConverter.GetBytes((long)count));
                }
                ApplyBuffer(56, Pairs, 8); ApplyBuffer(88, Moves, 4);
                Write(Pointer(tree, 8), nodeBytes);
                Write(live.Tree, BitConverter.GetBytes(Root));
                Write(IntPtr.Add(live.Tree, 16), new[] { Count, Nodes.Length, FreeHead, unchecked((int)Path), InsertionCount }.SelectMany(BitConverter.GetBytes).ToArray());
                Write(IntPtr.Add(live.Tree, 40), BitConverter.GetBytes(ProxyCount)); Write(IntPtr.Add(live.Tree, 112), BitConverter.GetBytes(QueryProxy));
                Write(live.State, BitConverter.GetBytes(WorldFlags)); Write(IntPtr.Add(live.State, 344), Step);
                Write(IntPtr.Add(live.Cache, 80), BitConverter.GetBytes(SimulationId));
                var actual = Capture(spatters, geos, projectiles);
                if (Newtonsoft.Json.JsonConvert.SerializeObject(actual) != Newtonsoft.Json.JsonConvert.SerializeObject(this))
                    throw new InvalidOperationException("Restored broadphase values did not match the captured C.");
                Modding.Logger.Log("[HKTAS] Restored broadphase values match captured C; fixture addresses rebound locally.");
            };
        }
    }
}
