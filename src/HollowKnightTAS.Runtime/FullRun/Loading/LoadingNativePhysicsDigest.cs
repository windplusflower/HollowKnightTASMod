using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Diagnostic only. Exact installed Unity/PDB layouts; read no padding,
    // never call native factories or export pointers as portable identities.
    internal sealed partial class LoadingNativePhysicsDigest
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr process, IntPtr address,
            byte[] bytes, UIntPtr size, out UIntPtr read);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        private const int WorldState = 103072;
        private readonly IntPtr engine;
        private readonly int engineLength;
        private readonly IntPtr managerAddress;
        private readonly FieldInfo cachedPointer;
        private readonly SortedSet<string> unsupported = new SortedSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<IntPtr, string> bodyIds = new Dictionary<IntPtr, string>();
        private readonly Dictionary<IntPtr, string> fixtureIds = new Dictionary<IntPtr, string>();
        private readonly Dictionary<IntPtr, string> contactIds = new Dictionary<IntPtr, string>();
        private readonly Dictionary<IntPtr, IntPtr> bodyOwners = new Dictionary<IntPtr, IntPtr>();
        private readonly Dictionary<IntPtr, IntPtr> fixtureOwners = new Dictionary<IntPtr, IntPtr>();
        private readonly Dictionary<IntPtr, string> rigidbodyIds = new Dictionary<IntPtr, string>();
        private readonly Dictionary<IntPtr, LoadingContactUserDataBindings.ColliderBinding> colliderBindings = new Dictionary<IntPtr, LoadingContactUserDataBindings.ColliderBinding>();
        private readonly List<object> inventory = new List<object>();
        private readonly StringBuilder text = new StringBuilder();
        private readonly SortedDictionary<string, StringBuilder> groups = new SortedDictionary<string, StringBuilder>(StringComparer.Ordinal);
        private readonly SortedDictionary<string, string> groupDigests = new SortedDictionary<string, string>(StringComparer.Ordinal);
        private readonly List<object> diagnosticEntries = new List<object>();
        private readonly List<object> contactRawEvidence = new List<object>();
        private readonly bool detailed = Environment.GetEnvironmentVariable("HKTAS_LOADING_NATIVE_PHYSICS_DETAILS") == "1";
        private static readonly HashSet<string> DiagnosticKinds = new HashSet<string>(new[] {
            "body", "collider", "fixture", "fixture-proxy", "shape", "polygon-active", "body-fixture-order",
            "body-contact-order", "world-body-order", "world-contact-order", "contact",
            "contact-control-initialized", "contact-toi-cache", "contact-current-manifold", "contact-active-points"
        }, StringComparer.Ordinal);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint CreateHandle(IntPtr target, int pinned);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FreeHandle(uint handle);
        private readonly CreateHandle createHandle;
        private readonly FreeHandle freeHandle;

        internal LoadingNativePhysicsDigest()
        {
            var module = LoadingEngineClockProbe.RequirePinnedEngine();
            engine = module.BaseAddress; engineLength = module.ModuleMemorySize;
            var mono = GetModuleHandle("mono-2.0-bdwgc.dll");
            var create = GetProcAddress(mono, "mono_gchandle_new");
            var free = GetProcAddress(mono, "mono_gchandle_free");
            if (mono == IntPtr.Zero || create == IntPtr.Zero || free == IntPtr.Zero)
                throw new InvalidOperationException("P0 physics scripting reference accessors unavailable.");
            createHandle = (CreateHandle)Marshal.GetDelegateForFunctionPointer(create, typeof(CreateHandle));
            freeHandle = (FreeHandle)Marshal.GetDelegateForFunctionPointer(free, typeof(FreeHandle));
            cachedPointer = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("P0 native physics object pointer field is unavailable.");
            var getter = Read(IntPtr.Add(engine, 0x721ee0), 8);
            if (Hex(getter) != "488b0501602f01c3")
                throw new InvalidOperationException("P0 physics manager getter code mismatch.");
            managerAddress = IntPtr.Add(engine, checked(0x721ee7 + BitConverter.ToInt32(getter, 3)));
        }

        internal object[] Inventory => inventory.ToArray();
        internal string[] Unsupported => unsupported.ToArray();
        internal IReadOnlyDictionary<string, string> Groups => groupDigests;
        internal object[] DiagnosticEntries => diagnosticEntries.ToArray();
        internal object[] ContactRawEvidence => contactRawEvidence.ToArray();
        internal string ContactProbeBinding(IntPtr contact, IntPtr fixtureA, IntPtr fixtureB, int childA, int childB)
        {
            if (!contactIds.TryGetValue(contact, out var binding)) return "not-present-at-after-sample";
            var current = Read(contact, 120);
            return Pointer(current, 96) == fixtureA && Pointer(current, 104) == fixtureB
                && I32(current, 112) == childA && I32(current, 116) == childB ? binding : "contact-storage-reused-before-after-sample";
        }

        internal string Capture(IEnumerable<Rigidbody2D> bodies, IEnumerable<Collider2D> colliders,
            Func<UnityEngine.Object, string> identity, LoadingFieldDigest fields)
        {
            unsupported.Clear(); bodyIds.Clear(); fixtureIds.Clear(); contactIds.Clear(); bodyOwners.Clear(); fixtureOwners.Clear(); inventory.Clear(); text.Clear(); groups.Clear(); groupDigests.Clear();
            diagnosticEntries.Clear();
            contactRawEvidence.Clear();
            var scenes = new Dictionary<IntPtr, int>();
            rigidbodyIds.Clear();
            colliderBindings.Clear();
            var orderedBodies = bodies.OrderBy(identity, StringComparer.Ordinal).ToArray();
            foreach (var body in orderedBodies) rigidbodyIds[Native(body)] = identity(body);
            foreach (var body in orderedBodies)
            {
                var pointer = Native(body);
                if (pointer == IntPtr.Zero) { unsupported.Add("rigidbody-native-pointer-unavailable"); continue; }
                var native = Read(pointer, 296);
                var boxBody = Pointer(native, 120);
                Line("rigidbody", identity(body), Hex(native, 56, 19), Hex(native, 76, 20), Hex(native, 100, 2), Hex(native, 104, 12),
                    Hex(native, 152, 4), Hex(native, 168, 111));
                if (Pointer(native, 288) != IntPtr.Zero) scenes[Pointer(native, 288)] = I32(Read(Pointer(native, 288), 8), 4);
                if (boxBody != IntPtr.Zero) { Bind(bodyIds, boxBody, identity(body), "body"); bodyOwners[boxBody] = pointer; }
                Line("rigidbody-parent", identity(body), Resolve(rigidbodyIds, Pointer(native, 280), "parent-rigidbody"));
            }
            foreach (var collider in colliders.OrderBy(identity, StringComparer.Ordinal))
            {
                var pointer = Native(collider);
                if (pointer == IntPtr.Zero) { unsupported.Add("collider-native-pointer-unavailable"); continue; }
                var native = Read(pointer, 224);
                var scene = Pointer(native, 216);
                if (scene != IntPtr.Zero) scenes[scene] = I32(Read(scene, 8), 4);
                var shapes = Array(native, 88, 8, "collider-fixtures");
                var id = identity(collider);
                colliderBindings[pointer] = new LoadingContactUserDataBindings.ColliderBinding { Id = id, InstanceId = I32(native, 8) };
                Line("collider", id, Hex(native, 120, 76), shapes.Length.ToString(CultureInfo.InvariantCulture));
                inventory.Add(new { kind = "collider-storage-evidence", binding = id, hex = Hex(native, 120, 76), shapes = shapes.Length });
                for (var index = 0; index < shapes.Length / 8; index++)
                { var fixture = Pointer(shapes, index * 8); Bind(fixtureIds, fixture, id + "/fixture[" + index + "]", "fixture"); fixtureOwners[fixture] = pointer; }
                if (Pointer(native, 200) != IntPtr.Zero) unsupported.Add("collider-effector-native-state");
                if (Pointer(native, 208) != IntPtr.Zero) unsupported.Add("collider-composite-native-state");
            }
            var manager = Pointer(Read(managerAddress, 8), 0);
            if (manager == IntPtr.Zero) throw new InvalidOperationException("P0 physics manager is unavailable.");
            var managerState = Read(manager, 184);
            var registeredScenes = BitConverter.ToUInt32(managerState, 84);
            Line("manager", I32(managerState, 104).ToString(), I32(managerState, 108).ToString(), registeredScenes.ToString());
            // Pinned GetPhysicsScenes (RVA be2320): end = nodes +
            // 3*(scaledMask+8), 24-byte nodes, occupied hash < uint.MaxValue-1.
            var scaledMask = BitConverter.ToUInt32(managerState, 80);
            if ((scaledMask & 7) != 0 || scaledMask > 2040)
                throw new InvalidOperationException("P0 physics scene map capacity is invalid.");
            var capacity = checked((int)(scaledMask + 8) / 8);
            if ((capacity & (capacity - 1)) != 0 || registeredScenes > capacity)
                throw new InvalidOperationException("P0 physics scene map count is invalid.");
            var map = Read(Pointer(managerState, 72), capacity * 24);
            var found = 0;
            for (var index = 0; index < capacity; index++)
            {
                if (BitConverter.ToUInt32(map, index * 24) >= uint.MaxValue - 1) continue;
                var scene = Pointer(map, index * 24 + 16); var handle = I32(map, index * 24 + 8);
                if (scene == IntPtr.Zero || I32(Read(scene, 8), 4) != handle)
                    throw new InvalidOperationException("P0 physics scene map identity mismatch.");
                if (scenes.TryGetValue(scene, out var existing) && existing != handle)
                    throw new InvalidOperationException("P0 physics scene map conflicts with loaded components.");
                scenes[scene] = handle; found++;
            }
            if (found != registeredScenes || scenes.Count != found)
                throw new InvalidOperationException("P0 physics scene map coverage mismatch.");
            Line("global-settings", Physics2D.gravity.ToString("R"), Physics2D.velocityIterations.ToString(),
                Physics2D.positionIterations.ToString(), Physics2D.simulationMode.ToString(),
                Physics2D.autoSyncTransforms.ToString(), Physics2D.queriesHitTriggers.ToString(),
                Physics2D.queriesStartInColliders.ToString(), Physics2D.callbacksOnDisable.ToString(),
                Physics2D.reuseCollisionCallbacks.ToString());
            var jobOptions = Physics2D.jobOptions;
            var jobDigest = fields.Capture(jobOptions, "physics/global-job-options", expandRoot: true);
            foreach (var missing in jobDigest.Unsupported) unsupported.Add("global-job-options:" + missing);
            Line("global-job-options", jobDigest.Sha256);
            inventory.Add(new { kind = "job-options", fields = fields.Fields(jobOptions.GetType())
                .Select(field => new { name = field.Name, value = field.GetValue(jobOptions) }).ToArray() });
            for (var layer = 0; layer < 32; layer++)
                Line("global-layer-mask", layer.ToString(), Physics2D.GetLayerCollisionMask(layer).ToString("x8"));
            CaptureSharedCallback(managerState, 120, 128, "collision", fields);
            CaptureSharedCallback(managerState, 152, 160, "contacts", fields);
            foreach (var scene in scenes.OrderBy(value => value.Value)) CaptureScene(scene.Key, scene.Value);
            Line("unsupported", string.Join(";", unsupported));
            // Publish successfully observed empty collections as empty digests.
            // Missing groups must still mean missing evidence to the K3 comparer.
            // Do this only after every registered physics scene was traversed;
            // do not add synthetic entries to the raw aggregate observation.
            foreach (var kind in new[] { "body", "contact-active-points", "contact-control-initialized", "polygon-active" })
                if (!groups.ContainsKey(kind)) groups.Add(kind, new StringBuilder());
            using (var sha = SHA256.Create())
            {
                foreach (var group in groups) groupDigests[group.Key] = Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(group.Value.ToString())));
                return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
            }
        }

        private void CaptureSharedCallback(byte[] manager, int objectOffset, int handleOffset,
            string name, LoadingFieldDigest fields)
        {
            var target = Pointer(manager, objectOffset);
            var handle = BitConverter.ToInt64(manager, handleOffset);
            var weakness = I32(manager, handleOffset + 8);
            var cachedObject = Pointer(manager, handleOffset + 16);
            var allocated = handle != 0 && handle != -1;
            Line("shared-callback-binding", name, allocated.ToString(), weakness.ToString(),
                (target == IntPtr.Zero).ToString(), (cachedObject == target).ToString());
            if (target == IntPtr.Zero) return;
            // Resolve the raw Mono pointer only through a temporary GC handle.
            var temporary = createHandle(target, 0);
            if (temporary == 0) throw new InvalidOperationException("P0 shared callback GC handle failed.");
            try
            {
                var value = GCHandle.FromIntPtr(new IntPtr(temporary)).Target;
                var digest = fields.Capture(value, "physics/shared-" + name, expandRoot: true);
                Line("shared-callback-state", name, digest.Sha256);
                foreach (var missing in digest.Unsupported) unsupported.Add("shared-" + name + ":" + missing);
                inventory.Add(new { kind = "shared-scripting-callback", name, allocated, weakness,
                    type = value?.GetType().FullName, nodes = digest.Nodes, digest = digest.Sha256 });
            }
            finally { freeHandle(temporary); }
        }

        private void CaptureScene(IntPtr pointer, int handle)
        {
            var scene = Read(pointer, 1184);
            var contactCachePointer = Pointer(scene, 40);
            var contactCache = contactCachePointer == IntPtr.Zero ? null : Read(contactCachePointer, 88);
            var movement = Array(scene, 112, 8, "movement-states");
            inventory.Add(new { kind = "scene-residuals", handle,
                ignorePairs = BitConverter.ToUInt32(scene, 92),
                callbackPairCount = contactCache == null ? 0 : BitConverter.ToUInt32(contactCache, 20),
                callbackCollisionCount = contactCache == null ? 0UL : BitConverter.ToUInt64(contactCache, 56),
                callbackSimulationId = contactCache == null ? -1 : I32(contactCache, 80),
                movementCount = movement.Length / 8 });
            var world = Pointer(scene, 8);
            if (world == IntPtr.Zero) throw new InvalidOperationException("P0 physics scene world is unavailable.");
            Bind(bodyIds, Pointer(scene, 16), "world[" + handle + "]/ground-body", "body");
            Line("scene", handle.ToString(), Hex(scene, 1168, 14));
            var ignoreCount = BitConverter.ToUInt32(scene, 92);
            Line("ignore-pair-count", handle.ToString(), ignoreCount.ToString());
            if (ignoreCount != 0) unsupported.Add("physics-scene-nonempty-ignore-map");
            if (contactCache != null)
            {
                var pairs = BitConverter.ToUInt32(contactCache, 20);
                var collisions = Array(contactCache, 40, 8, "callback-collisions");
                Line("callback-cache", handle.ToString(), pairs.ToString(), (collisions.Length / 8).ToString(),
                    I32(contactCache, 80).ToString());
                if (pairs != 0 || collisions.Length != 0) unsupported.Add("physics-scene-nonempty-collision-callback-cache");
            }
            for (var index = 0; index < movement.Length / 8; index++)
            {
                var movementPointer = Pointer(movement, index * 8);
                // This native pointer array can retain vacant slots. Preserve
                // their positions in the digest instead of dereferencing null.
                if (movementPointer == IntPtr.Zero)
                {
                    Line("movement-order", index.ToString(), "null-slot");
                    continue;
                }
                var value = Read(movementPointer, 128);
                Line("movement-order", index.ToString(), Resolve(rigidbodyIds, Pointer(value, 8), "movement-rigidbody"),
                    I32(value, 0).ToString(), Hex(value, 16, 111));
            }
            for (var bucket = 0; bucket < 64; bucket++)
            {
                var sentinel = IntPtr.Add(pointer, 144 + bucket * 16);
                var link = Pointer(Read(sentinel, 16), 8);
                var seen = new HashSet<IntPtr>();
                while (link != sentinel)
                {
                    if (link == IntPtr.Zero || !seen.Add(link) || seen.Count > 10000)
                        throw new InvalidOperationException("P0 invalid rigidbody hierarchy list.");
                    var node = Read(link, 24);
                    Line("rigidbody-hierarchy", bucket.ToString(), Resolve(rigidbodyIds, Pointer(node, 16), "hierarchy-rigidbody"));
                    link = Pointer(node, 8);
                }
            }
            var state = Read(IntPtr.Add(world, WorldState), 360);
            var bodyCount = Count(I32(state, 240), 10000, "bodies");
            var jointCount = Count(I32(state, 244), 10000, "joints");
            if (jointCount != 0) unsupported.Add("physics-world-joints:" + jointCount);
            Line("world", handle.ToString(), Hex(state, 0, 4), Hex(state, 312, 9), Hex(state, 344, 16));
            var bodies = Chain(Pointer(state, 224), 200, 104, bodyCount, "body-list");
            foreach (var body in bodies)
                if (!bodyIds.ContainsKey(body.Key))
                { bodyIds[body.Key] = "world[" + handle + "]/unbound-body[" + bodyIds.Count + "]"; unsupported.Add("physics-body-unbound"); }
            foreach (var body in bodies) Line("world-body-order", handle.ToString(), bodyIds[body.Key]);
            // Object values use their stable binding order. Intrusive native
            // lists are separate state and retain their original order below.
            var boundBodies = bodies.OrderBy(body => bodyIds[body.Key], StringComparer.Ordinal).ToArray();
            var allFixtures = new Dictionary<IntPtr, byte[]>();
            foreach (var body in boundBodies)
            {
                var bytes = body.Value;
                if (Pointer(bytes, 88) != world) throw new InvalidOperationException("P0 body world membership mismatch.");
                var fixtureCount = Count(I32(bytes, 120), 50000, "fixtures");
                var fixtures = Chain(Pointer(bytes, 112), 88, 24, fixtureCount, "fixture-list");
                Line("body", bodyIds[body.Key], Hex(bytes, 0, 6), Hex(bytes, 8, 80), Hex(bytes, 144, 44));
                var userData = Pointer(bytes, 192);
                if (userData != IntPtr.Zero && (!bodyOwners.TryGetValue(body.Key, out var owner) || owner != userData))
                    unsupported.Add("physics-body-user-data-unbound");
                foreach (var fixture in fixtures)
                {
                    if (Pointer(fixture.Value, 32) != body.Key) throw new InvalidOperationException("P0 fixture body membership mismatch.");
                    if (!fixtureIds.ContainsKey(fixture.Key))
                    { fixtureIds[fixture.Key] = bodyIds[body.Key] + "/unbound-fixture[" + allFixtures.Count + "]"; unsupported.Add("physics-fixture-unbound"); }
                    allFixtures.Add(fixture.Key, fixture.Value);
                    Line("body-fixture-order", bodyIds[body.Key], fixtureIds[fixture.Key]);
                }
            }
            foreach (var fixture in allFixtures.OrderBy(fixture => fixtureIds[fixture.Key], StringComparer.Ordinal))
                CaptureFixture(fixture.Key, fixture.Value);
            var contacts = Chain(Pointer(state, 128), 312, 24, null, "contact-list");
            foreach (var contact in contacts)
            {
                var bytes = contact.Value;
                var id = Fixture(Pointer(bytes, 96)) + ":" + I32(bytes, 112) + ">" + Fixture(Pointer(bytes, 104)) + ":" + I32(bytes, 116);
                if (contactIds.Values.Contains(id)) { id += "/duplicate[" + contactIds.Count + "]"; unsupported.Add("physics-contact-identity-duplicate"); }
                contactIds.Add(contact.Key, id);
            }
            foreach (var contact in contacts) Line("world-contact-order", handle.ToString(), contactIds[contact.Key]);
            if (contactCache != null) inventory.Add(new { kind = "contact-userdata-bindings", handle,
                evidence = CaptureContactUserDataBindings(contactCachePointer, contactCache, contacts) });
            foreach (var contact in contacts.OrderBy(contact => contactIds[contact.Key], StringComparer.Ordinal))
            {
                var bytes = contact.Value;
                Count(I32(bytes, 188), 2, "contact-manifold-points");
                if (!allFixtures.TryGetValue(Pointer(bytes, 96), out var fixtureA)
                    || !allFixtures.TryGetValue(Pointer(bytes, 104), out var fixtureB))
                    throw new InvalidOperationException("P0 contact evidence fixture is outside the bound world.");
                contactRawEvidence.Add(CaptureContactRaw(contactIds[contact.Key], bytes, fixtureA, fixtureB));
                // The pinned constructor initializes current pointCount only.
                // Retain old bytes in diagnostics until their read/write lifetime
                // is proved; do not treat an uninitialized count as memory damage.
                if (I32(bytes, 260) < 0 || I32(bytes, 260) > 2) unsupported.Add("physics-old-manifold-lifetime-unproven");
                Line("contact", contactIds[contact.Key], Function(Pointer(bytes, 0)), Hex(bytes, 8, 4), Hex(bytes, 112, 16), Hex(bytes, 272, 28));
                Line("contact-control-initialized", contactIds[contact.Key], Function(Pointer(bytes, 0)), Hex(bytes, 8, 4),
                    Hex(bytes, 112, 16), Hex(bytes, 272, 4), Hex(bytes, 280, 20));
                Line("contact-toi-cache", contactIds[contact.Key], Hex(bytes, 276, 4));
                Line("contact-current-manifold", contactIds[contact.Key], Hex(bytes, 128, 72));
                Line("contact-old-manifold", contactIds[contact.Key], Hex(bytes, 200, 72));
                var points = I32(bytes, 188);
                Line("contact-active-points", contactIds[contact.Key], points.ToString(), Hex(bytes, 128, points * 20),
                    points == 0 ? "" : Hex(bytes, 168, 20), Hex(bytes, 192, 8));
                Line("contact-inactive-points", contactIds[contact.Key], Hex(bytes, 128 + points * 20, (2 - points) * 20),
                    points == 0 ? Hex(bytes, 168, 20) : "");
                if (Pointer(bytes, 304) != IntPtr.Zero) unsupported.Add("physics-contact-user-data");
            }
            foreach (var body in boundBodies)
            {
                var edges = Chain(Pointer(body.Value, 136), 32, 24, null, "body-contact-edges");
                foreach (var edge in edges) Line("body-contact-order", bodyIds[body.Key], Body(Pointer(edge.Value, 0)), Contact(Pointer(edge.Value, 8)));
                if (Pointer(body.Value, 128) != IntPtr.Zero) unsupported.Add("physics-body-joint-edges");
            }
            CaptureContactArray(state, 160, "contacts-non-toi"); CaptureContactArray(state, 192, "contacts-toi");
            CaptureBodyArray(state, 248, "nonstatic-body-order"); CaptureBodyArray(state, 280, "static-body-order");
            CaptureBroadphase(state, 8);
            inventory.Add(new { kind = "world", handle, bodyCount, jointCount, fixtureCount = allFixtures.Count,
                contactCount = contacts.Count, broadphaseNodes = I32(state, 24), broadphaseProxies = I32(state, 48),
                runningStep = scene[1180] != 0, completeStateCoverage = false });
        }

        private void CaptureFixture(IntPtr pointer, byte[] bytes)
        {
            var id = Fixture(pointer);
            Line("fixture", id, Hex(bytes, 0, 24), Hex(bytes, 48, 8), Hex(bytes, 64, 11));
            var shape = Pointer(bytes, 40);
            if (shape == IntPtr.Zero) throw new InvalidOperationException("P0 fixture shape is unavailable.");
            var header = Read(shape, 16); var type = I32(header, 8);
            Line("shape", id, Function(Pointer(header, 0)), Hex(header, 8, 8));
            switch (type)
            {
                case 0: Line("circle", id, Hex(Read(shape, 24), 16, 8)); break;
                case 1: Line("edge", id, Hex(Read(shape, 56), 16, 34)); break;
                case 2:
                    var polygon = Read(shape, 160); var vertices = Count(I32(polygon, 152), 8, "polygon-vertices");
                    Line("polygon", id, Hex(polygon, 16, 140));
                    Line("polygon-active", id, vertices.ToString(), Hex(polygon, 16, 8), Hex(polygon, 24, vertices * 8), Hex(polygon, 88, vertices * 8));
                    Line("polygon-unused-capacity", id, Hex(polygon, 24 + vertices * 8, (8 - vertices) * 8), Hex(polygon, 88 + vertices * 8, (8 - vertices) * 8));
                    inventory.Add(new { kind = "polygon-storage-evidence", binding = id, vertexCount = vertices,
                        radiusHex = Hex(header, 12, 4), centroidHex = Hex(polygon, 16, 8),
                        activeVerticesHex = Hex(polygon, 24, vertices * 8), activeNormalsHex = Hex(polygon, 88, vertices * 8),
                        unusedVerticesHex = Hex(polygon, 24 + vertices * 8, (8 - vertices) * 8),
                        unusedNormalsHex = Hex(polygon, 88 + vertices * 8, (8 - vertices) * 8),
                        suffixReadBeforeWriteQualified = false, completeStateCoverage = false });
                    break;
                case 3:
                    var chain = Read(shape, 48); var count = Count(I32(chain, 24), 100000, "chain-vertices");
                    Line("chain", id, Hex(chain, 24, 22), count == 0 ? "" : Hex(Read(Pointer(chain, 16), count * 8))); break;
                default: throw new InvalidOperationException("P0 unknown Box2D shape type: " + type);
            }
            var proxyCount = Count(I32(bytes, 64), 100000, "fixture-proxies");
            var proxies = proxyCount == 0 ? System.Array.Empty<byte>() : Read(Pointer(bytes, 56), proxyCount * 32);
            for (var index = 0; index < proxyCount; index++)
            {
                if (Pointer(proxies, index * 32 + 16) != pointer) throw new InvalidOperationException("P0 fixture proxy membership mismatch.");
                Line("fixture-proxy", id, Hex(proxies, index * 32, 16), Hex(proxies, index * 32 + 24, 8));
            }
            if (Pointer(bytes, 80) == IntPtr.Zero || !fixtureOwners.TryGetValue(pointer, out var owner) || Pointer(bytes, 80) != owner)
                unsupported.Add("physics-fixture-user-data-unbound");
        }

        private void CaptureBroadphase(byte[] state, int offset)
        {
            var count = Count(I32(state, offset + 16), 262144, "tree-nodes");
            var capacity = Count(I32(state, offset + 20), 262144, "tree-capacity");
            if (count > capacity) throw new InvalidOperationException("P0 broadphase node count exceeds capacity.");
            Line("broadphase", Hex(state, offset, 4), Hex(state, offset + 16, 20), Hex(state, offset + 40, 4), Hex(state, offset + 112, 4));
            var nodes = capacity == 0 ? System.Array.Empty<byte>() : Read(Pointer(state, offset + 8), capacity * 40);
            var active = 0;
            var activeNodes = new List<object>();
            for (var index = 0; index < capacity; index++)
            {
                var position = index * 40; var height = I32(nodes, position + 36);
                if (height < -1) throw new InvalidOperationException("P0 invalid broadphase node height.");
                if (height == -1) { Line("tree-free", index.ToString(), I32(nodes, position + 24).ToString()); continue; }
                active++;
                Line("tree-node", index.ToString(), Hex(nodes, position, 16), Hex(nodes, position + 24, 16));
                activeNodes.Add(new { index, aabbBits = Hex(nodes, position, 16),
                    parent = I32(nodes, position + 24), child1 = I32(nodes, position + 28),
                    child2 = I32(nodes, position + 32), height });
                if (I32(nodes, position + 28) == -1)
                {
                    var proxy = Read(Pointer(nodes, position + 16), 32);
                    Line("tree-leaf", index.ToString(), Fixture(Pointer(proxy, 16)), Hex(proxy, 0, 16), Hex(proxy, 24, 8));
                }
                else if (Pointer(nodes, position + 16) != IntPtr.Zero) unsupported.Add("physics-internal-tree-user-data");
            }
            if (active != count) throw new InvalidOperationException("P0 active broadphase node count mismatch.");
            var freePrefix = new List<int>();
            var seenFree = new HashSet<int>();
            for (var index = I32(state, offset + 24); index != -1; index = I32(nodes, index * 40 + 24))
            {
                if (index < 0 || index >= capacity || !seenFree.Add(index) || I32(nodes, index * 40 + 36) != -1)
                    throw new InvalidOperationException("P0 broadphase free chain is invalid.");
                if (freePrefix.Count < 128) freePrefix.Add(index);
            }
            if (seenFree.Count != capacity - active) throw new InvalidOperationException("P0 broadphase free chain count mismatch.");
            inventory.Add(new { kind = "broadphase-diagnostic", root = I32(state, offset), count, capacity,
                freeHead = I32(state, offset + 24), path = I32(state, offset + 28),
                insertionCount = I32(state, offset + 32), queryProxy = I32(state, offset + 112),
                activeNodes = activeNodes.ToArray(), freePrefix = freePrefix.ToArray() });
            Line("broadphase-pairs", Hex(Array(state, offset + 48, 8, "broadphase-pairs")));
            Line("broadphase-moves", Hex(Array(state, offset + 80, 4, "broadphase-moves")));
        }

        private void CaptureContactArray(byte[] state, int offset, string kind)
        {
            var values = Array(state, offset, 8, kind);
            for (var index = 0; index < values.Length / 8; index++) Line(kind, Contact(Pointer(values, index * 8)));
        }
        private void CaptureBodyArray(byte[] state, int offset, string kind)
        {
            var values = Array(state, offset, 8, kind);
            for (var index = 0; index < values.Length / 8; index++) Line(kind, Body(Pointer(values, index * 8)));
        }
        private List<KeyValuePair<IntPtr, byte[]>> Chain(IntPtr pointer, int size, int nextOffset, int? expected, string kind)
        {
            var seen = new HashSet<IntPtr>(); var result = new List<KeyValuePair<IntPtr, byte[]>>();
            while (pointer != IntPtr.Zero)
            {
                if (result.Count >= 100000 || !seen.Add(pointer)) throw new InvalidOperationException("P0 invalid bounded physics " + kind);
                var value = Read(pointer, size); result.Add(new KeyValuePair<IntPtr, byte[]>(pointer, value)); pointer = Pointer(value, nextOffset);
            }
            if (expected != null && expected.Value != result.Count) throw new InvalidOperationException("P0 physics " + kind + " count mismatch.");
            return result;
        }
        private static byte[] Array(byte[] bytes, int offset, int elementSize, string kind)
        {
            var count = BitConverter.ToUInt64(bytes, offset + 16); var capacity = BitConverter.ToUInt64(bytes, offset + 24);
            if (count > capacity || capacity > 262144) throw new InvalidOperationException("P0 native physics array budget: " + kind);
            return count == 0 ? System.Array.Empty<byte>() : Read(Pointer(bytes, offset), checked((int)count * elementSize));
        }
        private static void Bind(Dictionary<IntPtr, string> values, IntPtr pointer, string id, string kind)
        {
            if (pointer == IntPtr.Zero) throw new InvalidOperationException("P0 missing native physics " + kind);
            if (values.TryGetValue(pointer, out var previous) && previous != id) throw new InvalidOperationException("P0 native physics identity alias: " + kind);
            values[pointer] = id;
        }
        private string Body(IntPtr pointer) => Resolve(bodyIds, pointer, "body");
        private string Fixture(IntPtr pointer) => Resolve(fixtureIds, pointer, "fixture");
        private string Contact(IntPtr pointer) => Resolve(contactIds, pointer, "contact");
        private string Resolve(Dictionary<IntPtr, string> values, IntPtr pointer, string kind)
        {
            if (pointer == IntPtr.Zero) return "none";
            if (values.TryGetValue(pointer, out var id)) return id;
            unsupported.Add("physics-reference-unbound:" + kind); return "unbound:" + kind;
        }
        private string Function(IntPtr pointer)
        {
            var rva = pointer.ToInt64() - engine.ToInt64();
            if (rva >= 0 && rva < engineLength) return "UnityPlayer:" + rva.ToString("x");
            unsupported.Add("physics-vtable-outside-engine"); return "unbound-vtable";
        }
        private IntPtr Native(UnityEngine.Object value) => (IntPtr)cachedPointer.GetValue(value)!;
        private void Line(params string[] values)
        {
            if (!groups.TryGetValue(values[0], out var group)) groups.Add(values[0], group = new StringBuilder());
            foreach (var value in values) { text.Append(value.Length).Append(':').Append(value); group.Append(value.Length).Append(':').Append(value); }
            text.Append('\n'); group.Append('\n');
            if (text.Length > 8 * 1024 * 1024) throw new InvalidOperationException("P0 native physics digest exceeds its 8 MiB budget.");
            if (detailed && DiagnosticKinds.Contains(values[0]))
            {
                if (diagnosticEntries.Count >= 65536)
                    throw new InvalidOperationException("P0 native physics entry diagnostic budget exceeded.");
                // Only per-field digests are written; preserve entry order and
                // stable binding, without exporting restorable native bytes.
                using (var sha = SHA256.Create())
                    diagnosticEntries.Add(new { kind = values[0], binding = values[1],
                        fields = values.Skip(2).Select(value => Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(value)))).ToArray() });
            }
        }
        private static int Count(int value, int maximum, string kind)
        { if (value < 0 || value > maximum) throw new InvalidOperationException("P0 invalid native physics " + kind); return value; }
        private static int I32(byte[] bytes, int offset) => BitConverter.ToInt32(bytes, offset);
        private static IntPtr Pointer(byte[] bytes, int offset) => new IntPtr(BitConverter.ToInt64(bytes, offset));
        private static string Hex(byte[] bytes) => Hex(bytes, 0, bytes.Length);
        private static string Hex(byte[] bytes, int offset, int count) => count == 0 ? "" : BitConverter.ToString(bytes, offset, count).Replace("-", "").ToLowerInvariant();
        private static byte[] Read(IntPtr address, int length)
        {
            if (address == IntPtr.Zero || length < 0 || length > 16 * 1024 * 1024)
                throw new InvalidOperationException("P0 native physics read range is invalid.");
            var bytes = new byte[length];
            if (!ReadProcessMemory(GetCurrentProcess(), address, bytes, new UIntPtr((uint)length), out var read)
                || read.ToUInt64() != (ulong)length) throw new InvalidOperationException("P0 native physics memory read failed.");
            return bytes;
        }
    }
}
