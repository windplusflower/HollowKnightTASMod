using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Component/fixture identities come from this capture's complete directory.
    // Native layouts below are type ABI, never instance paths or expected counts.
    internal static partial class ResidentPhysicsState
    {
        private const int MaximumOwners = 100000, MaximumElements = 262144, WorldStateOffset = 103072;
        private static readonly Type[] ColliderTypes = { typeof(BoxCollider2D), typeof(PolygonCollider2D), typeof(CircleCollider2D), typeof(EdgeCollider2D), typeof(CapsuleCollider2D) };
        private static readonly FieldInfo PhysicsHandle = typeof(PhysicsScene2D).GetField("m_Handle", BindingFlags.Instance | BindingFlags.NonPublic)!;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AllocateTreeNode(IntPtr tree);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NativeSetFloat(IntPtr owner, float value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NativeSetByte(IntPtr field, byte value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NativeSetInt(IntPtr field, int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NativeVoid(IntPtr owner);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NativeSetPath(IntPtr owner, int index, IntPtr dynamicArray);

        internal sealed class Data
        {
            public int Version = 1;
            public bool ContactFactoryInitialized;
            public RigidbodyData[] Rigidbodies = Array.Empty<RigidbodyData>();
            public ColliderData[] Colliders = Array.Empty<ColliderData>();
            public BodyData[] Bodies = Array.Empty<BodyData>();
            public WorldData[] Worlds = Array.Empty<WorldData>();
            public ResidentPhysicsGlobals.Data? Globals;
            public string[] CoverageGaps = Array.Empty<string>();
        }
        internal sealed class CaptureResult
        {
            internal readonly Data Data;
            internal readonly Dictionary<string, object> ManagedRoots = new Dictionary<string, object>(StringComparer.Ordinal);
            private readonly ResidentStaticRoots.Slot<Collision2D?> collision = new ResidentStaticRoots.Slot<Collision2D?>();
            private readonly ResidentStaticRoots.Slot<ContactPoint2D[]?> contacts = new ResidentStaticRoots.Slot<ContactPoint2D[]?>();
            private readonly Action requireUnchanged;
            internal CaptureResult(Data data, object? collision, object? contacts, Action requireUnchanged)
            {
                Data = data; this.collision.Value = (Collision2D?)collision; this.contacts.Value = (ContactPoint2D[]?)contacts;
                this.requireUnchanged = requireUnchanged;
                ManagedRoots.Add(ResidentPhysicsGlobals.CollisionRoot, this.collision); ManagedRoots.Add(ResidentPhysicsGlobals.ContactsRoot, this.contacts);
            }
            internal object? ResolveManagedRoot(string key) => key == ResidentPhysicsGlobals.CollisionRoot ? collision.Value
                : key == ResidentPhysicsGlobals.ContactsRoot ? contacts.Value : throw Error("shared managed root key is absent");
            internal void RequireUnchanged() => requireUnchanged();
        }
        internal sealed class RigidbodyData
        {
            public string Owner = "", Material = "", Parent = "", World = "", Body = "", HierarchyPrevious = "", HierarchyNext = "";
            public bool Active;
            // Config: +56:19,+76:20,+100:2,+104:12. Movement: +152:4,+168:111.
            public byte[] Config = Array.Empty<byte>(), Movement = Array.Empty<byte>();
        }
        internal sealed class ColliderData
        {
            public string Owner = "", Type = "", Material = "", AttachedBody = "", World = "";
            public bool Active;
            public byte Enabled, Added;
            public int Vtable, TilingVtable;
            public ulong FixtureCapacity, PathCapacity;
            public byte[] Config = Array.Empty<byte>(), Cache = Array.Empty<byte>(), Geometry = Array.Empty<byte>(), Tiling = Array.Empty<byte>();
            public PointData[] Paths = Array.Empty<PointData>();
            public FixtureData[] Fixtures = Array.Empty<FixtureData>();
        }
        internal sealed class PointData
        {
            public int Count;
            public ulong Capacity;
            public byte[] Values = Array.Empty<byte>();
        }
        internal sealed class BodyData
        {
            public string Key = "", Owner = "", World = "";
            public byte[] Values = Array.Empty<byte>();
            public string[] FixtureOrder = Array.Empty<string>();
            public string[] ContactOrder = Array.Empty<string>();
        }
        internal sealed class FixtureData
        {
            public string Key = "", Body = "";
            public byte[] Values = Array.Empty<byte>();
            public ShapeData Shape = new ShapeData();
            public ProxyData[] Proxies = Array.Empty<ProxyData>();
        }
        internal sealed class ShapeData
        {
            public int Type, Vtable, VertexCount;
            public byte[] Values = Array.Empty<byte>(), Vertices = Array.Empty<byte>();
        }
        internal sealed class ProxyData
        {
            public int Child, TreeNode;
            public byte[] Bounds = Array.Empty<byte>();
        }
        internal sealed class WorldData
        {
            public string Key = "";
            public string[] BodyOrder = Array.Empty<string>(), MovementOrder = Array.Empty<string>();
            public string[][] Hierarchy = Array.Empty<string[]>();
            public byte[] SceneValues = Array.Empty<byte>(), WorldValues = Array.Empty<byte>(), Broadphase = Array.Empty<byte>();
            public string[] NonStaticBodies = Array.Empty<string>(), StaticBodies = Array.Empty<string>();
            public ulong MovementCapacity, NonStaticCapacity, StaticCapacity, PairCapacity, MoveCapacity;
            public int ContactCount, JointCount, IgnorePairCount, CallbackPairCount, CallbackCollisionCount;
            public ContactData[] Contacts = Array.Empty<ContactData>();
            public string[] NonToiContacts = Array.Empty<string>(), ToiContacts = Array.Empty<string>();
            public ulong NonToiContactCapacity, ToiContactCapacity;
            public CallbackData Callbacks = new CallbackData();
        }

        private sealed class Local
        {
            internal readonly Data Data = new Data();
            internal readonly Dictionary<string, IntPtr> Owners = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            internal readonly Dictionary<string, IntPtr> Bodies = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            internal readonly Dictionary<string, IntPtr> Fixtures = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            internal readonly Dictionary<string, IntPtr> Shapes = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            internal readonly Dictionary<string, IntPtr> Proxies = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            internal readonly Dictionary<string, IntPtr> Scenes = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            internal readonly Dictionary<string, IntPtr> Worlds = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            internal readonly Dictionary<string, IntPtr> Contacts = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            internal readonly Dictionary<IntPtr, string> ContactNames = new Dictionary<IntPtr, string>();
            internal readonly Dictionary<string, IntPtr[]> CollisionRows = new Dictionary<string, IntPtr[]>(StringComparer.Ordinal);
            internal readonly List<Tuple<IntPtr, int>> CallbackUnused = new List<Tuple<IntPtr, int>>();
            internal readonly List<Tuple<long, long>> ContactAllocations = new List<Tuple<long, long>>();
            internal readonly Dictionary<string, ResidentPhysicsCallbackAllocator.Live> CallbackAllocators = new Dictionary<string, ResidentPhysicsCallbackAllocator.Live>(StringComparer.Ordinal);
            internal readonly Dictionary<string, IntPtr> PointHeaders = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            internal readonly Dictionary<string, IntPtr> HierarchyLinks = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            internal readonly Dictionary<IntPtr, string> SceneNames = new Dictionary<IntPtr, string>();
            internal readonly Dictionary<IntPtr, string> BodyNames = new Dictionary<IntPtr, string>();
            internal readonly Dictionary<IntPtr, string> FixtureNames = new Dictionary<IntPtr, string>();
            internal readonly List<Tuple<IntPtr, byte[]>> Witness = new List<Tuple<IntPtr, byte[]>>();
            internal readonly SortedSet<string> Gaps = new SortedSet<string>(StringComparer.Ordinal);
            internal ResidentPhysicsGlobals.Live? Globals;
            internal byte[] Read(IntPtr pointer, int count) { var value = M.Read(pointer, count); Witness.Add(Tuple.Create(pointer, value)); return value; }
            internal void Frozen() {
                foreach (var row in Witness) if (!M.Read(row.Item1, row.Item2.Length).SequenceEqual(row.Item2)) throw Error("native observation changed");
                foreach (var allocator in CallbackAllocators.Values) allocator.RequireUnchanged();
                Globals?.Frozen();
            }
        }

        internal static Data Capture(ResidentObjectDirectory directory, Func<UnityEngine.Object, string> bindAsset)
        { var value = Read(directory, bindAsset); value.Frozen(); return value.Data; }

        internal static CaptureResult CaptureWithManagedRoots(ResidentObjectDirectory directory, Func<UnityEngine.Object, string> bindAsset)
        {
            var value = Read(directory, bindAsset); value.Frozen(); var roots = value.Globals!.ManagedRoots;
            CaptureResult? result = null;
            result = new CaptureResult(value.Data, roots[ResidentPhysicsGlobals.CollisionRoot], roots[ResidentPhysicsGlobals.ContactsRoot], () => {
                value.Frozen(); foreach (var root in roots) if (!ReferenceEquals(root.Value, result!.ResolveManagedRoot(root.Key)))
                    throw Error("captured shared callback slot changed");
            });
            return result;
        }

        internal static void CanonicalizeIdleStorage(ResidentObjectDirectory directory, Func<UnityEngine.Object, string> bindAsset, Action admitted)
        {
            var value = Read(directory, bindAsset); value.Frozen(); ResidentPhysicsGlobals.CanonicalizeIdleStorage(value.Globals!, admitted);
            var colliderNames = value.Data.Colliders.ToDictionary(c => value.Owners[c.Owner], c => c.Owner);
            foreach (var world in value.Data.Worlds) {
                admitted();
                value.CallbackAllocators[world.Key].CanonicalizeIdle(admitted);
                var owner = M.Ptr(IntPtr.Add(value.Scenes[world.Key], 40));
                ResidentPhysicsCallbackMap.CanonicalizeIdle(IntPtr.Add(owner, 8), ptr => Name(colliderNames, ptr, "callback hash collider"), admitted);
            }
            foreach (var block in value.CallbackUnused) { admitted(); M.Write(block.Item1, new byte[block.Item2]); }
        }

        internal static Plan Prepare(Data source, ResidentObjectDirectory directory, Func<UnityEngine.Object, string> bindAsset,
            Func<string, UnityEngine.Object> resolveAsset, ResidentMutationScope scope, Action admitted, Func<string, object?>? resolveManaged = null)
            => new Plan(source, directory, bindAsset, resolveAsset, scope, admitted, resolveManaged);

        internal sealed class Plan
        {
            private readonly Data source;
            private readonly ResidentObjectDirectory directory;
            private readonly Func<UnityEngine.Object, string> bindAsset;
            private readonly Func<string, UnityEngine.Object> resolveAsset;
            private readonly ResidentMutationScope scope;
            private readonly Action admitted;
            private readonly Func<string, object?> resolveManaged;
            private Local target;
            private bool structured, consumed, applied;
            internal Plan(Data source, ResidentObjectDirectory directory, Func<UnityEngine.Object, string> bindAsset,
                Func<string, UnityEngine.Object> resolveAsset, ResidentMutationScope scope, Action admitted, Func<string, object?>? resolveManaged)
            {
                this.source = Decode(Encode(source)); this.directory = directory; this.bindAsset = bindAsset; this.resolveAsset = resolveAsset;
                this.scope = scope; this.admitted = admitted;
                this.resolveManaged = resolveManaged ?? (_ => null);
                if (directory == null || bindAsset == null || resolveAsset == null || scope == null || admitted == null) throw new ArgumentNullException();
                if (this.source.Globals == null) throw Error("a restore requires the complete global physics owner inventory");
                RequireCompleteCoverage(); admitted(); target = Read(directory, bindAsset);
                if (resolveManaged == null && source.Globals != null && (source.Globals.Collision.Present || source.Globals.Contacts.Present))
                    throw Error("shared callback roots require the unified managed graph resolver");
                RequireOwners(this.source, target.Data);
                foreach (var owner in source.Rigidbodies.Select(value => value.Owner).Concat(source.Colliders.Select(value => value.Owner)))
                    if (!(directory.Resolve(owner) is Component component) || !scope.Owns(component)) throw Error("owner is outside mutation scope");
            }
            internal void RequireCompleteCoverage()
                => ResidentPhysicsState.RequireCompleteCoverage(source);

            // Run after Transform/topology synchronization, before the final
            // managed graph/native value pass. All native effects stay in scope.
            internal void ApplyStructure()
            {
                if (structured || consumed) throw Error("structure preparation is single-use"); scope.Require(); admitted();
                try {
                    foreach (var row in source.Rigidbodies) {
                        var owner = (Rigidbody2D)directory.Resolve(row.Owner);
                        if (owner.gameObject.activeInHierarchy != row.Active) throw Error("Rigidbody active hierarchy is not synchronized");
                        scope.Mutate(() => { owner.bodyType = (RigidbodyType2D)Int(row.Config, 35); owner.simulated = row.Config[16] != 0;
                            owner.sharedMaterial = Material(row.Material); });
                    }
                    var engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
                    var setCircleRadius = NativeEntry<NativeSetFloat>(engine, 0xc08530, "f30f1091e0000000");
                    var setAutoTiling = NativeEntry<NativeSetByte>(engine, 0xbf0330, "385108740a488b01");
                    var resizePaths = NativeEntry<NativeSetInt>(engine, 0x62c240, "4883ec488b410845");
                    var onPathCount = NativeEntry<NativeVoid>(engine, 0xc07230, "40534883ec20488b");
                    var setPolygonPath = NativeEntry<NativeSetPath>(engine, 0xc08400, "40574883ec204983");
                    foreach (var row in source.Colliders) {
                        var owner = (Collider2D)directory.Resolve(row.Owner);
                        if (owner.gameObject.activeInHierarchy != row.Active) throw Error("Collider active hierarchy is not synchronized");
                        scope.Mutate(() => {
                            owner.sharedMaterial = Material(row.Material); owner.offset = Vector(row.Config, 0); owner.density = Float(row.Config, 8);
                            owner.isTrigger = row.Config[12] != 0; owner.usedByEffector = row.Config[13] != 0; owner.usedByComposite = row.Config[14] != 0;
                            if (owner is BoxCollider2D box) { box.size = Vector(row.Geometry, 0); box.edgeRadius = Float(row.Geometry, 8); box.autoTiling = row.Tiling[0] != 0; }
                            else if (row.Type == typeof(CircleCollider2D).FullName) setCircleRadius(M.Owner(owner), Float(row.Geometry, 0));
                            else if (owner is CapsuleCollider2D capsule) { capsule.size = Vector(row.Geometry, 0); capsule.direction = (CapsuleDirection2D)Int(row.Geometry, 8); }
                            else if (row.Type == typeof(PolygonCollider2D).FullName) {
                                var poly = M.Owner(owner);
                                setAutoTiling(IntPtr.Add(poly, 0xe0), row.Tiling[0] != 0 ? (byte)1 : (byte)0);
                                resizePaths(IntPtr.Add(poly, 0x120), row.Paths.Length);
                                onPathCount(poly);
                                for (var index = 0; index < row.Paths.Length; index++) {
                                    var path = row.Paths[index];
                                    if (path.Count == 0) continue;
                                    var dyn = Marshal.AllocHGlobal(32);
                                    var buf = Marshal.AllocHGlobal(path.Values.Length);
                                    try {
                                        Marshal.Copy(path.Values, 0, buf, path.Values.Length);
                                        Marshal.WriteIntPtr(dyn, 0, buf);
                                        Marshal.WriteInt64(dyn, 8, 1L);
                                        Marshal.WriteInt64(dyn, 16, path.Count);
                                        Marshal.WriteInt64(dyn, 24, ((long)path.Count << 1) | 1L);
                                        setPolygonPath(poly, index, dyn);
                                    } finally {
                                        Marshal.FreeHGlobal(buf);
                                        Marshal.FreeHGlobal(dyn);
                                    }
                                }
                            }
                            else if (owner is EdgeCollider2D edge) { edge.edgeRadius = Float(row.Geometry, 0); edge.points = Points(row.Paths[0]);
                                edge.adjacentStartPoint = Vector(row.Geometry, 4); edge.adjacentEndPoint = Vector(row.Geometry, 12);
                                edge.useAdjacentStartPoint = row.Geometry[20] != 0; edge.useAdjacentEndPoint = row.Geometry[21] != 0; }
                            if (row.Enabled == 0 && row.World.Length != 0 && row.Active && M.Ptr(IntPtr.Add(M.Owner(owner), 216)) == IntPtr.Zero) {
                                owner.enabled = true;
                            }
                            owner.enabled = row.Enabled != 0;
                        });
                    }
                    var current = Read(directory, bindAsset); RequireOwners(source, current.Data); current.Frozen();
                    foreach (var row in source.Colliders) {
                        var pointer = current.Owners[row.Owner]; var header = M.Read(pointer, NativeSize(row.Type));
                        if (Long(header, 104) != row.Fixtures.Length) throw Error("fixture producers did not create source topology: " + row.Owner);
                        ReserveArray(IntPtr.Add(pointer, 88), row.FixtureCapacity, 8, 8, admitted);
                        if (row.Type == typeof(PolygonCollider2D).FullName) ReserveArray(IntPtr.Add(pointer, 288), row.PathCapacity, 32, 8, admitted);
                        var pathBase = row.Type == typeof(PolygonCollider2D).FullName ? M.Ptr(M.Read(pointer, 320), 288) : IntPtr.Zero;
                        for (var index = 0; index < row.Paths.Length; index++) ReserveArray(row.Type == typeof(EdgeCollider2D).FullName
                            ? IntPtr.Add(pointer, 232) : IntPtr.Add(pathBase, checked(index * 32)), row.Paths[index].Capacity, 8, 4, admitted);
                    }
                    target = Read(directory, bindAsset); RequireCompatible(source, target.Data); target.Frozen();
                    scope.Mutate(() => ReconstructBlockAllocators(source, target, admitted));
                    target = Read(directory, bindAsset); RequireCompatible(source, target.Data); target.Frozen();
                    scope.Mutate(() => PrepareContactNodes(source, target, admitted));
                    target = Read(directory, bindAsset); RequireCompatible(source, target.Data); target.Frozen();
                    if (ResidentPhysicsGlobals.NeedsInterestMapSync(source.Globals!, target.Globals!.Data)) {
                        scope.Mutate(() => ResidentPhysicsGlobals.SynchronizeInterestMap(source.Globals!, target.Globals!, admitted));
                        target = Read(directory, bindAsset); RequireCompatible(source, target.Data); target.Frozen();
                    }
                    structured = true;
                    ResidentPhysicsGlobals.RequireCompatible(source.Globals!, target.Globals!);
                } catch (Exception error) { scope.Fault(error); throw; }
            }
            private PhysicsMaterial2D? Material(string key)
            { if (key.Length == 0) return null; return resolveAsset(key) as PhysicsMaterial2D ?? throw Error("physics material binding has a different type"); }
            internal void Apply()
            {
                if (!structured || consumed) throw Error("native application requires one completed structure preparation"); consumed = true; scope.Require(); admitted();
                try {
                    var current = Read(directory, bindAsset); RequireEqual(target.Data, current.Data); target.Frozen();
                    ApplyComponentValues(source, target, admitted);
                    ResidentPhysicsGlobals.Apply(source.Globals!, target.Globals!, resolveManaged, admitted);
                    applied = true; Verify();
                } catch (Exception error) { scope.Fault(error); throw; }
            }
            internal void Verify()
            {
                if (!applied) throw Error("readback precedes application"); scope.Require(); admitted();
                var current = Read(directory, bindAsset); RequireEqual(source, current.Data); current.Frozen();
                foreach (var pair in current.Globals!.ManagedRoots) if (!ReferenceEquals(pair.Value, resolveManaged(pair.Key)))
                    throw Error("native shared callback root is not the unified graph owner");
            }
        }

        private static Local Read(ResidentObjectDirectory directory, Func<UnityEngine.Object, string> bindAsset)
        {
            if (directory == null || bindAsset == null) throw new ArgumentNullException();
            var engine = LoadingEngineClockProbe.RequirePinnedEngine(); var live = new Local();
            live.Data.ContactFactoryInitialized = ReadContactFactory(live, engine.BaseAddress);
            var rigidbodies = directory.NativeBindings.Where(pair => pair.Value is Rigidbody2D).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
            var colliders = directory.NativeBindings.Where(pair => pair.Value is Collider2D).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
            if (rigidbodies.Length + colliders.Length > MaximumOwners) throw Error("component inventory exceeds its bound");
            if (Physics2D.jobOptions.useMultithreading) live.Gaps.Add("physics-multithreaded-world-state");
            BindWorlds(live, directory, engine.BaseAddress);
            var rbRows = new List<RigidbodyData>(); var colliderRows = new List<ColliderData>();
            foreach (var pair in rigidbodies) {
                var owner = (Rigidbody2D)pair.Value;
                if (owner.GetType() != typeof(Rigidbody2D)) { live.Gaps.Add("unknown-rigidbody-native-type:" + owner.GetType().FullName); continue; }
                var material = owner.sharedMaterial; var pointer = M.Owner(owner); var bytes = live.Read(pointer, 296); live.Owners.Add(pair.Key, pointer);
                CheckOwner(bytes, owner); var body = M.Ptr(bytes, 120); var world = SceneName(live, M.Ptr(bytes, 288));
                if (body != IntPtr.Zero) Bind(live.Bodies, live.BodyNames, pair.Key + "/body", body);
                rbRows.Add(new RigidbodyData { Owner = pair.Key, Material = material == null ? "" : bindAsset(material), Active = owner.gameObject.activeInHierarchy,
                    Parent = NativeOwner(directory, M.Ptr(bytes, 280), typeof(Rigidbody2D)), World = world, Body = body == IntPtr.Zero ? "" : pair.Key + "/body",
                    Config = Join(Slice(bytes, 56, 19), Slice(bytes, 76, 20), Slice(bytes, 100, 2), Slice(bytes, 104, 12)),
                    Movement = Join(Slice(bytes, 152, 4), Slice(bytes, 168, 111)) });
                if (M.Ptr(bytes, 160) != pointer) live.Gaps.Add("rigidbody-movement-owner-alias:" + pair.Key);
            }
            foreach (var pair in colliders) {
                var owner = (Collider2D)pair.Value; var type = owner.GetType();
                if (!ColliderTypes.Contains(type)) { live.Gaps.Add("unknown-collider-native-type:" + type.FullName); continue; }
                var material = owner.sharedMaterial; var attached = owner.attachedRigidbody; var pointer = M.Owner(owner);
                var bytes = live.Read(pointer, NativeSize(type.FullName!)); live.Owners.Add(pair.Key, pointer); CheckOwner(bytes, owner);
                var row = new ColliderData { Owner = pair.Key, Type = type.FullName!, Material = material == null ? "" : bindAsset(material),
                    AttachedBody = attached == null ? "" : directory.Bind(attached) ?? throw Error("attached body is outside the directory"),
                    World = SceneName(live, M.Ptr(bytes, 216)), Active = owner.gameObject.activeInHierarchy, Enabled = bytes[56], Added = bytes[57],
                    Vtable = Rva(M.Ptr(bytes, 0), engine.BaseAddress, engine.ModuleMemorySize), FixtureCapacity = M.U64(bytes, 112),
                    Config = Join(Slice(bytes, 68, 15), Slice(bytes, 84, 4)), Cache = Slice(bytes, 120, 76) };
                if (M.Ptr(bytes, 200) != IntPtr.Zero) live.Gaps.Add("collider-effector-state:" + pair.Key);
                if (M.Ptr(bytes, 208) != IntPtr.Zero) live.Gaps.Add("collider-composite-state:" + pair.Key);
                if (type == typeof(BoxCollider2D) || type == typeof(PolygonCollider2D)) {
                    row.TilingVtable = Rva(M.Ptr(bytes, 224), engine.BaseAddress, engine.ModuleMemorySize);
                    row.Tiling = Join(Slice(bytes, 232, 1), Slice(bytes, 236, 45), Slice(bytes, 284, 4));
                }
                if (type == typeof(BoxCollider2D)) row.Geometry = Slice(bytes, 288, 12);
                else if (type == typeof(CircleCollider2D)) row.Geometry = Slice(bytes, 224, 4);
                else if (type == typeof(CapsuleCollider2D)) row.Geometry = Slice(bytes, 224, 12);
                else if (type == typeof(EdgeCollider2D)) {
                    row.Geometry = Join(Slice(bytes, 224, 4), Slice(bytes, 264, 18));
                    row.Paths = new[] { ReadPoints(live, pair.Key + "/path/0", IntPtr.Add(pointer, 232)) };
                } else {
                    var header = Slice(bytes, 288, 32); var count = DynamicCount(header, 32, out row.PathCapacity);
                    var paths = new PointData[count];
                    for (var index = 0; index < count; index++) paths[index] = ReadPoints(live, pair.Key + "/path/" + index, IntPtr.Add(M.Ptr(header, 0), checked(index * 32)));
                    row.Paths = paths;
                }
                var fixtureCount = DynamicCount(Slice(bytes, 88, 32), 8, out var fixtureCapacity);
                if (fixtureCapacity != row.FixtureCapacity) throw Error("fixture capacity observation changed");
                var fixturePointers = live.Read(M.Ptr(bytes, 88), checked(fixtureCount * 8));
                var fixtures = new FixtureData[fixtureCount];
                for (var index = 0; index < fixtureCount; index++) {
                    var key = pair.Key + "/fixture[" + index + "]"; var fixture = M.Ptr(fixturePointers, index * 8);
                    Bind(live.Fixtures, live.FixtureNames, key, fixture);
                    fixtures[index] = ReadFixture(live, key, fixture, pointer, engine.BaseAddress, engine.ModuleMemorySize);
                }
                row.Fixtures = fixtures; colliderRows.Add(row);
            }
            live.Data.Rigidbodies = rbRows.ToArray(); live.Data.Colliders = colliderRows.ToArray();
            ReadWorlds(live, directory);
            var allocations = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            foreach (var pair in live.Bodies) allocations.Add(pair.Key, pair.Value);
            foreach (var pair in live.Contacts) allocations.Add(pair.Key, pair.Value);
            foreach (var pair in live.Fixtures) {
                allocations.Add(pair.Key, pair.Value); allocations.Add(pair.Key + "/shape", live.Shapes[pair.Key]);
                var fixture = live.Read(pair.Value, 88); var proxies = M.Ptr(fixture, 56);
                if (proxies != IntPtr.Zero) allocations.Add(pair.Key + "/proxy-buffer", proxies);
                var shape = live.Read(live.Shapes[pair.Key], 16);
                if (Int(shape, 8) == 3) {
                    var vertices = M.Ptr(live.Read(live.Shapes[pair.Key], 24), 16);
                    if (vertices != IntPtr.Zero) allocations.Add(pair.Key + "/chain-vertices", vertices);
                }
            }
            live.Globals = ResidentPhysicsGlobals.Capture(directory, live.Scenes, live.Worlds, allocations);
            live.Data.Globals = live.Globals.Data;
            RequireContactStorage(live);
            foreach (var gap in live.Globals.Data.CoverageGaps) live.Gaps.Add(gap);
            live.Data.CoverageGaps = live.Gaps.ToArray(); Validate(live.Data); live.Frozen(); return live;
        }

        internal static int ReadPhysicsSceneHandle(PhysicsScene2D scene)
        {
            if (PhysicsHandle == null || PhysicsHandle.FieldType != typeof(int)) throw Error("physics scene handle field ABI differs");
            return (int)ResidentValueAccess.ReadField(ResidentValueAccess.FromValue(scene), PhysicsHandle)!;
        }

        private static void BindWorlds(Local live, ResidentObjectDirectory directory, IntPtr engine)
        {
            if (!M.Read(IntPtr.Add(engine, 0x721ee0), 8).SequenceEqual(new byte[] { 0x48, 0x8b, 0x05, 0x01, 0x60, 0x2f, 0x01, 0xc3 }))
                throw Error("physics manager getter ABI differs");
            var manager = M.Ptr(live.Read(IntPtr.Add(engine, 0x1a17ee8), 8), 0);
            var state = live.Read(manager, 184);
            var handles = new Dictionary<int, List<string>>();
            foreach (var scene in directory.CopySnapshot().Scenes) {
                var owner = directory.SceneFor(scene.Key);
                var physical = PhysicsSceneExtensions2D.GetPhysicsScene2D(owner);
                var handle = ReadPhysicsSceneHandle(physical);
                if (!handles.TryGetValue(handle, out var keys)) handles.Add(handle, keys = new List<string>());
                keys.Add(scene.Key);
            }
            var defaultHandle = ReadPhysicsSceneHandle(Physics2D.defaultPhysicsScene);
            if (Int(state, 104) != defaultHandle) throw Error("default physics scene identity differs");
            var scaled = M.U32(state, 80); var count = M.U32(state, 84);
            if ((scaled & 7) != 0 || scaled > 8 * 1024) throw Error("physics scene map capacity exceeds its bound");
            var capacity = checked((int)(scaled + 8) / 8); var map = live.Read(M.Ptr(state, 72), checked(capacity * 24)); var found = 0;
            for (var index = 0; index < capacity; index++) {
                var at = index * 24; if (M.U32(map, at) >= uint.MaxValue - 1) continue;
                var handle = Int(map, at + 8); var pointer = M.Ptr(map, at + 16); var scene = live.Read(pointer, 48);
                if (Int(scene, 4) != handle) throw Error("physics scene map binding differs");
                string key;
                if (handle == defaultHandle) key = "physics/default";
                else if (handles.TryGetValue(handle, out var names)) key = "physics/scenes/" + string.Join("|", names.OrderBy(value => value, StringComparer.Ordinal));
                else { key = "physics/unbound-scene/" + found; live.Gaps.Add("physics-scene-without-directory-owner:" + key); }
                if (live.Scenes.ContainsKey(key) || live.SceneNames.ContainsKey(pointer)) throw Error("physics scene identity is ambiguous");
                live.Scenes.Add(key, pointer); live.SceneNames.Add(pointer, key); live.Worlds.Add(key, M.Ptr(scene, 8));
                Bind(live.Bodies, live.BodyNames, key + "/ground", M.Ptr(scene, 16)); found++;
            }
            if (found != count) throw Error("physics scene map count differs");
        }

        private static PointData ReadPoints(Local live, string key, IntPtr headerAddress)
        {
            var header = live.Read(headerAddress, 32); var count = DynamicCount(header, 8, out var encoded);
            var capacity = checked((int)(encoded >> 1));
            if ((encoded & 1) != 0 && capacity != 0) live.Gaps.Add("borrowed-geometry-storage:" + key);
            live.PointHeaders.Add(key, headerAddress);
            return new PointData { Count = count, Capacity = encoded, Values = live.Read(M.Ptr(header, 0), checked(capacity * 8)) };
        }

        private static FixtureData ReadFixture(Local live, string key, IntPtr pointer, IntPtr collider, IntPtr engine, int engineLength)
        {
            var bytes = live.Read(pointer, 88);
            if (M.Ptr(bytes, 80) != collider) throw Error("fixture user data is not its directory collider");
            var body = Name(live.BodyNames, M.Ptr(bytes, 32), "fixture body");
            var shapeAddress = M.Ptr(bytes, 40); var shapeHeader = live.Read(shapeAddress, 16); var shapeType = Int(shapeHeader, 8);
            var shape = new ShapeData { Type = shapeType, Vtable = Rva(M.Ptr(shapeHeader, 0), engine, engineLength) };
            live.Shapes.Add(key, shapeAddress);
            switch (shapeType) {
                case 0: shape.Values = Slice(live.Read(shapeAddress, 24), 12, 12); break;
                case 1: shape.Values = Slice(live.Read(shapeAddress, 56), 12, 38); break;
                case 2: shape.Values = Slice(live.Read(shapeAddress, 160), 12, 144); shape.VertexCount = Int(shape.Values, 140); break;
                case 3:
                    var chain = live.Read(shapeAddress, 48); shape.VertexCount = Bound(Int(chain, 24), MaximumElements, "chain vertex count");
                    shape.Values = Join(Slice(chain, 12, 4), Slice(chain, 24, 22));
                    shape.Vertices = live.Read(M.Ptr(chain, 16), checked(shape.VertexCount * 8)); break;
                case 4: shape.Values = Slice(live.Read(shapeAddress, 32), 12, 20); break;
                default: throw Error("unknown Box2D shape type: " + shapeType);
            }
            var count = Bound(Int(bytes, 64), MaximumElements, "fixture proxy count"); var proxies = new ProxyData[count];
            var proxyBytes = live.Read(M.Ptr(bytes, 56), checked(count * 32));
            for (var index = 0; index < count; index++) {
                var at = index * 32;
                if (M.Ptr(proxyBytes, at + 16) != pointer || Int(proxyBytes, at + 24) != index) throw Error("fixture proxy ownership differs");
                proxies[index] = new ProxyData { Bounds = Slice(proxyBytes, at, 16), Child = Int(proxyBytes, at + 24), TreeNode = Int(proxyBytes, at + 28) };
                live.Proxies.Add(key + "/proxy[" + index + "]", IntPtr.Add(M.Ptr(bytes, 56), at));
            }
            return new FixtureData { Key = key, Body = body, Values = Join(Slice(bytes, 0, 24), Slice(bytes, 48, 8), Slice(bytes, 64, 11)), Shape = shape, Proxies = proxies };
        }

        private static void ReadWorlds(Local live, ResidentObjectDirectory directory)
        {
            var bodyRows = new Dictionary<string, BodyData>(StringComparer.Ordinal); var worlds = new List<WorldData>();
            var rbNames = live.Data.Rigidbodies.ToDictionary(value => live.Owners[value.Owner], value => value.Owner);
            var movementNames = rbNames.ToDictionary(value => IntPtr.Add(value.Key, 152), value => value.Value);
            foreach (var pair in live.Scenes) for (var index = 0; index < 64; index++) live.HierarchyLinks.Add(pair.Key + "/hierarchy[" + index + "]", IntPtr.Add(pair.Value, 144 + index * 16));
            foreach (var pair in rbNames) live.HierarchyLinks.Add(pair.Value + "/hierarchy", IntPtr.Add(pair.Key, 128));
            var hierarchyNames = live.HierarchyLinks.ToDictionary(value => value.Value, value => value.Key);
            foreach (var row in live.Data.Rigidbodies) {
                var bytes = live.Read(live.Owners[row.Owner], 152);
                row.HierarchyPrevious = Name(hierarchyNames, M.Ptr(bytes, 128), "rigidbody previous hierarchy link", true);
                row.HierarchyNext = Name(hierarchyNames, M.Ptr(bytes, 136), "rigidbody next hierarchy link", true);
                if (M.Ptr(bytes, 144) != live.Owners[row.Owner]) throw Error("rigidbody hierarchy embedded owner differs");
            }
            foreach (var pair in live.Scenes.OrderBy(value => value.Key, StringComparer.Ordinal)) {
                var scene = live.Read(pair.Value, 1184); var world = live.Worlds[pair.Key]; var state = live.Read(IntPtr.Add(world, WorldStateOffset), 360);
                var row = new WorldData { Key = pair.Key, SceneValues = Slice(scene, 1168, 14),
                    WorldValues = Join(Slice(state, 0, 4), Slice(state, 312, 9), Slice(state, 344, 16)), JointCount = Int(state, 244), IgnorePairCount = checked((int)M.U32(scene, 92)) };
                if (scene[1180] != 0 || (Int(state, 0) & 2) != 0) live.Gaps.Add("physics-world-is-running:" + pair.Key);
                if (row.JointCount != 0 || M.Ptr(state, 232) != IntPtr.Zero) live.Gaps.Add("physics-world-joints:" + pair.Key);
                if (row.IgnorePairCount != 0) live.Gaps.Add("physics-scene-ignore-map:" + pair.Key);
                var cache = live.Read(M.Ptr(scene, 40), 88); row.CallbackPairCount = checked((int)M.U32(cache, 20));
                row.CallbackCollisionCount = DynamicCount(Slice(cache, 40, 32), 8, out _);
                var bodies = Chain(live, M.Ptr(state, 224), 200, 104, Bound(Int(state, 240), MaximumOwners, "world bodies"));
                var bodyOrder = new List<string>();
                foreach (var body in bodies) {
                    if (!live.BodyNames.TryGetValue(body.Item1, out var key)) {
                        key = pair.Key + "/unbound-body[" + bodyOrder.Count + "]"; Bind(live.Bodies, live.BodyNames, key, body.Item1);
                        live.Gaps.Add("physics-body-without-resident-owner:" + key);
                    }
                    bodyOrder.Add(key); var bytes = body.Item2;
                    if (M.Ptr(bytes, 88) != world) throw Error("Box2D body world membership differs");
                    var fixtureCount = Bound(Int(bytes, 120), MaximumElements, "body fixtures");
                    var fixtures = Chain(live, M.Ptr(bytes, 112), 88, 24, fixtureCount);
                    var order = fixtures.Select(value => Name(live.FixtureNames, value.Item1, "body fixture list")).ToArray();
                    if (fixtures.Any(value => M.Ptr(value.Item2, 32) != body.Item1)) throw Error("body fixture membership differs");
                    var owner = M.Ptr(bytes, 192) == IntPtr.Zero ? "" : Name(rbNames, M.Ptr(bytes, 192), "body native user data");
                    if (M.Ptr(bytes, 128) != IntPtr.Zero) live.Gaps.Add("physics-body-joint-edges:" + key);
                    bodyRows.Add(key, new BodyData { Key = key, Owner = owner, World = pair.Key, FixtureOrder = order,
                        Values = Join(Slice(bytes, 0, 6), Slice(bytes, 8, 80), Slice(bytes, 144, 44)) });
                }
                row.BodyOrder = bodyOrder.ToArray();
                ReadContacts(live, row, state, cache, bodies, bodyRows);
                row.Callbacks = ReadCallbacks(live, row, cache);
                var movement = ReadPointerArray(live, Slice(scene, 112, 32), movementNames, "movement state", out row.MovementCapacity, true);
                row.MovementOrder = movement;
                row.Hierarchy = new string[64][];
                for (var bucket = 0; bucket < 64; bucket++) {
                    var sentinel = IntPtr.Add(pair.Value, 144 + bucket * 16); var sentinelBytes = live.Read(sentinel, 16);
                    var current = M.Ptr(sentinelBytes, 8); var previous = sentinel; var seen = new HashSet<IntPtr>(); var order = new List<string>();
                    while (current != sentinel) {
                        if (current == IntPtr.Zero || !seen.Add(current) || seen.Count > MaximumOwners) throw Error("rigidbody hierarchy list is invalid");
                        var link = live.Read(current, 24);
                        if (M.Ptr(link, 0) != previous) throw Error("rigidbody hierarchy previous link differs");
                        var name = Name(rbNames, M.Ptr(link, 16), "hierarchy rigidbody");
                        if (current != IntPtr.Add(live.Owners[name], 128)) throw Error("rigidbody hierarchy node is not embedded in its owner");
                        order.Add(name); previous = current; current = M.Ptr(link, 8);
                    }
                    if (M.Ptr(sentinelBytes, 0) != previous) throw Error("rigidbody hierarchy tail differs");
                    row.Hierarchy[bucket] = order.ToArray();
                }
                row.NonStaticBodies = ReadPointerArray(live, Slice(state, 248, 32), live.BodyNames, "nonstatic body", out row.NonStaticCapacity);
                row.StaticBodies = ReadPointerArray(live, Slice(state, 280, 32), live.BodyNames, "static body", out row.StaticCapacity);
                row.Broadphase = ReadBroadphase(live, state, cache, out row.PairCapacity, out row.MoveCapacity);
                worlds.Add(row);
            }
            live.Data.Bodies = bodyRows.Values.OrderBy(value => value.Key, StringComparer.Ordinal).ToArray(); live.Data.Worlds = worlds.ToArray();
        }

        private static byte[] ReadBroadphase(Local live, byte[] state, byte[] cache, out ulong pairCapacity, out ulong moveCapacity)
        {
            var capacity = Bound(Int(state, 28), 32768, "tree capacity"); var bytes = live.Read(M.Ptr(state, 16), checked(capacity * 40));
            var aliases = live.Proxies.ToDictionary(value => value.Value, value => value.Key);
            var nodes = new CheckpointPhysicsBroadphaseState.Node[capacity];
            for (var index = 0; index < capacity; index++) {
                var at = index * 40; var row = new CheckpointPhysicsBroadphaseState.Node { Parent = Int(bytes, at + 24), Height = Int(bytes, at + 36) }; nodes[index] = row;
                if (row.Height == -1) continue;
                row.Bounds = Slice(bytes, at, 16); row.Child1 = Int(bytes, at + 28); row.Child2 = Int(bytes, at + 32);
                var user = M.Ptr(bytes, at + 16); row.UserData = user == IntPtr.Zero ? null : Name(aliases, user, "tree user data");
                if (row.Child1 != -1) continue;
                row.Proxy = Name(aliases, user, "tree leaf proxy"); var proxy = live.Read(user, 32);
                row.ProxyBounds = Slice(proxy, 0, 16); row.ProxyChild = Int(proxy, 24);
                if (Int(proxy, 28) != index) throw Error("tree node and fixture proxy differ");
            }
            var pairs = ReadIntArray(live, Slice(state, 56, 32), 8, out pairCapacity);
            var moves = ReadIntArray(live, Slice(state, 88, 32), 4, out moveCapacity);
            var queryProxy = Int(state, 120);
            if (queryProxy < -1 || queryProxy >= nodes.Length || (queryProxy != -1 && (nodes[queryProxy] == null || nodes[queryProxy].Height == -1)))
                queryProxy = -1;
            return new CheckpointPhysicsBroadphaseState { Root = Int(state, 8), Count = Int(state, 24), FreeHead = Int(state, 32), Path = M.U32(state, 36),
                InsertionCount = Int(state, 40), ProxyCount = Int(state, 48), QueryProxy = queryProxy, WorldFlags = Int(state, 0),
                Step = Slice(state, 344, 8), SimulationId = Int(cache, 80), Nodes = nodes, Pairs = pairs, Moves = moves }.EncodeCompact();
        }

        private static void ApplyComponentValues(Data source, Local target, Action admitted)
        {
            foreach (var row in source.Rigidbodies) {
                admitted(); var pointer = target.Owners[row.Owner];
                WriteParts(pointer, row.Config, new[] { 56, 19, 76, 20, 100, 2, 104, 12 });
                WriteParts(pointer, row.Movement, new[] { 152, 4, 168, 111 });
                WritePointer(IntPtr.Add(pointer, 280), row.Parent.Length == 0 ? IntPtr.Zero : target.Owners[row.Parent]);
                WritePointer(IntPtr.Add(pointer, 128), Link(target.HierarchyLinks, row.HierarchyPrevious));
                WritePointer(IntPtr.Add(pointer, 136), Link(target.HierarchyLinks, row.HierarchyNext));
            }
            foreach (var row in source.Colliders) {
                admitted(); var pointer = target.Owners[row.Owner];
                WriteParts(pointer, row.Config, new[] { 68, 15, 84, 4 }); M.Write(IntPtr.Add(pointer, 120), row.Cache);
                if (row.Tiling.Length != 0) WriteParts(pointer, row.Tiling, new[] { 232, 1, 236, 45, 284, 4 });
                if (row.Type == typeof(BoxCollider2D).FullName) M.Write(IntPtr.Add(pointer, 288), row.Geometry);
                else if (row.Type == typeof(CircleCollider2D).FullName || row.Type == typeof(CapsuleCollider2D).FullName) M.Write(IntPtr.Add(pointer, 224), row.Geometry);
                else if (row.Type == typeof(EdgeCollider2D).FullName) WriteParts(pointer, row.Geometry, new[] { 224, 4, 264, 18 });
                for (var index = 0; index < row.Paths.Length; index++) {
                    var header = target.PointHeaders[row.Owner + "/path/" + index]; var bytes = M.Read(header, 32);
                    M.Write(M.Ptr(bytes, 0), row.Paths[index].Values);
                }
                foreach (var fixture in row.Fixtures) {
                    admitted(); var fixturePointer = target.Fixtures[fixture.Key]; var shape = target.Shapes[fixture.Key];
                    WriteParts(fixturePointer, fixture.Values, new[] { 0, 24, 48, 8, 64, 11 });
                    if (fixture.Shape.Type == 3) {
                        WriteParts(shape, fixture.Shape.Values, new[] { 12, 4, 24, 22 });
                        M.Write(M.Ptr(M.Read(shape, 24), 16), fixture.Shape.Vertices);
                    } else M.Write(IntPtr.Add(shape, 12), fixture.Shape.Values);
                    for (var index = 0; index < fixture.Proxies.Length; index++) {
                        var proxy = target.Proxies[fixture.Key + "/proxy[" + index + "]"]; var saved = fixture.Proxies[index];
                        M.Write(proxy, saved.Bounds); M.Write(IntPtr.Add(proxy, 24), Join(BitConverter.GetBytes(saved.Child), BitConverter.GetBytes(saved.TreeNode)));
                    }
                }
            }
            foreach (var row in source.Bodies) {
                admitted(); var pointer = target.Bodies[row.Key]; WriteParts(pointer, row.Values, new[] { 0, 6, 8, 80, 144, 44 });
                WritePointer(IntPtr.Add(pointer, 112), row.FixtureOrder.Length == 0 ? IntPtr.Zero : target.Fixtures[row.FixtureOrder[0]]);
                for (var index = 0; index < row.FixtureOrder.Length; index++) WritePointer(IntPtr.Add(target.Fixtures[row.FixtureOrder[index]], 24),
                    index + 1 == row.FixtureOrder.Length ? IntPtr.Zero : target.Fixtures[row.FixtureOrder[index + 1]]);
            }
            var sourceBodies = source.Bodies.ToDictionary(body => body.Key, StringComparer.Ordinal);
            foreach (var row in source.Worlds) ApplyWorld(row, sourceBodies, target, admitted);
        }

        private static void ApplyWorld(WorldData source, Dictionary<string, BodyData> bodies, Local target, Action admitted)
        {
            admitted(); var scene = target.Scenes[source.Key]; var world = target.Worlds[source.Key]; var state = IntPtr.Add(world, WorldStateOffset);
            M.Write(IntPtr.Add(scene, 1168), source.SceneValues); WriteParts(state, source.WorldValues, new[] { 0, 4, 312, 9, 344, 16 });
            WritePointer(IntPtr.Add(state, 224), source.BodyOrder.Length == 0 ? IntPtr.Zero : target.Bodies[source.BodyOrder[0]]);
            for (var index = 0; index < source.BodyOrder.Length; index++) {
                var pointer = target.Bodies[source.BodyOrder[index]];
                WritePointer(IntPtr.Add(pointer, 96), index == 0 ? IntPtr.Zero : target.Bodies[source.BodyOrder[index - 1]]);
                WritePointer(IntPtr.Add(pointer, 104), index + 1 == source.BodyOrder.Length ? IntPtr.Zero : target.Bodies[source.BodyOrder[index + 1]]);
            }
            WritePointerArray(IntPtr.Add(scene, 112), source.MovementOrder.Select(value => value.Length == 0 ? IntPtr.Zero : IntPtr.Add(target.Owners[value], 152)).ToArray(), source.MovementCapacity, admitted);
            WritePointerArray(IntPtr.Add(state, 248), source.NonStaticBodies.Select(value => target.Bodies[value]).ToArray(), source.NonStaticCapacity, admitted);
            WritePointerArray(IntPtr.Add(state, 280), source.StaticBodies.Select(value => target.Bodies[value]).ToArray(), source.StaticCapacity, admitted);
            for (var bucket = 0; bucket < source.Hierarchy.Length; bucket++) {
                var sentinel = IntPtr.Add(scene, 144 + bucket * 16); var names = source.Hierarchy[bucket];
                WritePointer(sentinel, names.Length == 0 ? sentinel : IntPtr.Add(target.Owners[names[names.Length - 1]], 128));
                WritePointer(IntPtr.Add(sentinel, 8), names.Length == 0 ? sentinel : IntPtr.Add(target.Owners[names[0]], 128));
            }
            var saved = CheckpointPhysicsBroadphaseState.DecodeCompact(source.Broadphase); var tree = IntPtr.Add(state, 8);
            var header = M.Read(tree, 40); var capacity = Bound(Int(header, 20), 32768, "target tree capacity");
            if (capacity > saved.Nodes.Length) throw Error("broadphase target capacity exceeds source capacity");
            if (capacity < saved.Nodes.Length) {
                var engine = LoadingEngineClockProbe.RequirePinnedEngine(); var entry = IntPtr.Add(engine.BaseAddress, 0xb9d5a0);
                if (!M.Read(entry, 17).SequenceEqual(new byte[] { 0x40, 0x53, 0x48, 0x83, 0xec, 0x20, 0x83, 0x79, 0x18, 0xff, 0x48, 0x8b, 0xd9, 0x0f, 0x85, 0xbe, 0x00 }))
                    throw Error("broadphase allocator ABI differs");
                var allocate = (AllocateTreeNode)Marshal.GetDelegateForFunctionPointer(entry, typeof(AllocateTreeNode));
                for (var attempts = 0; capacity < saved.Nodes.Length; attempts++) {
                    if (attempts > 32768) throw Error("broadphase capacity allocation exceeds its bound"); admitted(); allocate(tree);
                    header = M.Read(tree, 40); capacity = Bound(Int(header, 20), 32768, "target tree capacity");
                }
                if (capacity != saved.Nodes.Length) throw Error("broadphase target capacity cannot represent the source");
            }
            var nodes = M.Ptr(header, 8);
            for (var index = 0; index < saved.Nodes.Length; index++) {
                var node = saved.Nodes[index]; var pointer = IntPtr.Add(nodes, checked(index * 40));
                M.Write(IntPtr.Add(pointer, 24), BitConverter.GetBytes(node.Parent)); M.Write(IntPtr.Add(pointer, 36), BitConverter.GetBytes(node.Height));
                if (node.Height == -1) continue;
                M.Write(pointer, node.Bounds); WritePointer(IntPtr.Add(pointer, 16), node.UserData == null ? IntPtr.Zero : target.Proxies[node.UserData]);
                M.Write(IntPtr.Add(pointer, 28), Join(BitConverter.GetBytes(node.Child1), BitConverter.GetBytes(node.Child2)));
            }
            M.Write(tree, BitConverter.GetBytes(saved.Root));
            M.Write(IntPtr.Add(tree, 16), new[] { saved.Count, saved.Nodes.Length, saved.FreeHead, unchecked((int)saved.Path), saved.InsertionCount }.SelectMany(BitConverter.GetBytes).ToArray());
            M.Write(IntPtr.Add(tree, 40), BitConverter.GetBytes(saved.ProxyCount)); M.Write(IntPtr.Add(tree, 112), BitConverter.GetBytes(saved.QueryProxy));
            WriteIntArray(IntPtr.Add(state, 56), saved.Pairs, source.PairCapacity, 8, admitted);
            WriteIntArray(IntPtr.Add(state, 88), saved.Moves, source.MoveCapacity, 4, admitted);
            ApplyContacts(source, bodies, target, state, admitted);
            M.Write(IntPtr.Add(M.Ptr(M.Read(scene, 48), 40), 80), BitConverter.GetBytes(saved.SimulationId));
        }

        private static void WritePointerArray(IntPtr header, IntPtr[] values, ulong capacity, Action admitted)
        {
            PrepareArraySize(header, values.Length, capacity, 8, 8, admitted);
            M.Write(M.Ptr(M.Read(header, 8), 0), values.SelectMany(value => BitConverter.GetBytes(value.ToInt64())).ToArray());
        }
        private static void WriteIntArray(IntPtr header, int[] values, ulong capacity, int stride, Action admitted)
        {
            PrepareArraySize(header, checked(values.Length * 4 / stride), capacity, stride, 4, admitted);
            M.Write(M.Ptr(M.Read(header, 8), 0), values.SelectMany(BitConverter.GetBytes).ToArray());
        }
        private static void PrepareArraySize(IntPtr header, int count, ulong capacity, int stride, int alignment, Action admitted)
        {
            var old = M.Read(header, 32); var oldCount = DynamicCount(old, stride, out _); Capacity(capacity, count, stride);
            // Truncating these POD arrays requires no destructor. Never increase
            // size until allocation succeeds: borrowed storage copies old size.
            if (oldCount > count) M.Write(IntPtr.Add(header, 16), BitConverter.GetBytes((long)count));
            ReserveArray(header, capacity, stride, alignment, admitted);
            M.Write(IntPtr.Add(header, 16), BitConverter.GetBytes((long)count));
        }
        private static void ReserveArray(IntPtr header, ulong encodedCapacity, int stride, int alignment, Action admitted)
        {
            var current = M.Read(header, 32); var count = Bound(Long(current, 16), MaximumElements, "array size");
            var capacity = Capacity(encodedCapacity, count, stride);
            if ((encodedCapacity & 1) != 0 && capacity != 0) throw Error("borrowed nonempty storage needs an explicit backing-owner binding");
            if (M.U64(current, 24) == encodedCapacity) return;
            M.Reserve(header, capacity, stride, alignment, admitted);
            if (capacity == 0 && encodedCapacity == 1) {
                var empty = M.Read(header, 32); if (M.Ptr(empty, 0) != IntPtr.Zero || Long(empty, 16) != 0) throw Error("empty borrowed array is not empty");
                M.Write(IntPtr.Add(header, 24), BitConverter.GetBytes(1UL));
            }
            var after = M.Read(header, 32);
            if (M.U64(after, 24) != encodedCapacity || Long(after, 16) != count) throw Error("native array capacity did not read back");
        }

        internal static void RequireCompleteCoverage(Data source)
        { Validate(source); if (source.CoverageGaps.Length != 0) throw Error("coverage gaps: " + string.Join(";", source.CoverageGaps.Take(16))); }
        internal static void RequireCanonicalizableCoverage(Data source)
        {
            Validate(source);
            // These are the three storage domains CanonicalizeIdleStorage
            // actually rewrites. Active allocations and all gameplay/native
            // gaps remain fatal before the first mutation.
            var hard = source.CoverageGaps.Where(gap => !gap.StartsWith("physics-stack-idle-storage-needs-canon:", StringComparison.Ordinal)
                && !gap.StartsWith("physics-block-free-storage-needs-canon:", StringComparison.Ordinal)
                && !gap.StartsWith("physics-unused-callback-buffer-needs-canon:", StringComparison.Ordinal)
                && !(gap.StartsWith("physics-callback-allocator:", StringComparison.Ordinal) && gap.EndsWith(":retired-slot payload needs typed Canon", StringComparison.Ordinal))
                && !(gap.StartsWith("physics-callback-map:", StringComparison.Ordinal) && gap.EndsWith(":" + ResidentPhysicsCallbackMap.IdleStorageGap, StringComparison.Ordinal))
                && !gap.StartsWith("physics-empty-handle-stale-cache:", StringComparison.Ordinal)).ToArray();
            if (hard.Length != 0) throw Error("common Canon coverage gaps: " + string.Join(";", hard.Take(16)));
        }

        internal static byte[] Encode(Data source)
        {
            Validate(source); var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(source, Formatting.None));
            if (bytes.Length > 24 * 1024 * 1024) throw Error("physics payload exceeds its byte bound"); return bytes;
        }
        internal static Data Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 24 * 1024 * 1024) throw Error("physics payload size differs");
            try {
                var result = JsonConvert.DeserializeObject<Data>(new UTF8Encoding(false, true).GetString(bytes)) ?? throw Error("physics payload is absent");
                if (!Encode(result).SequenceEqual(bytes)) throw Error("physics payload is noncanonical"); return result;
            } catch (Exception error) when (error is JsonException || error is DecoderFallbackException) { throw new InvalidDataException("Resident physics payload is malformed.", error); }
        }

        internal static void Validate(Data value)
        {
            if (value == null || value.Version != 1 || value.Rigidbodies == null || value.Colliders == null || value.Bodies == null || value.Worlds == null || value.CoverageGaps == null
                || value.Rigidbodies.Length + value.Colliders.Length > MaximumOwners || value.Bodies.Length > MaximumOwners || value.Worlds.Length > 1024
                || value.CoverageGaps.Length > MaximumOwners || value.CoverageGaps.Any(string.IsNullOrEmpty)) throw Error("payload schema differs");
            var keys = new HashSet<string>(StringComparer.Ordinal); var rbKeys = new HashSet<string>(StringComparer.Ordinal);
            var worlds = new HashSet<string>(StringComparer.Ordinal); var bodyKeys = new HashSet<string>(StringComparer.Ordinal); var fixtureKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in value.Worlds) { if (row == null || !worlds.Add(Text(row.Key))) throw Error("world identities differ"); }
            foreach (var row in value.Bodies) { if (row == null || !bodyKeys.Add(Text(row.Key))) throw Error("body identities differ"); }
            foreach (var row in value.Rigidbodies) {
                if (row == null || !keys.Add(Text(row.Owner)) || !rbKeys.Add(row.Owner)) throw Error("rigidbody identities differ");
                Text(row.Material, true); Text(row.Parent, true); Text(row.World, true); Text(row.Body, true); Text(row.HierarchyPrevious, true); Text(row.HierarchyNext, true);
                Exact(row.Config, 53); Exact(row.Movement, 115); Flags(row.Config[16], row.Config[17], row.Config[18], row.Config[39], row.Config[40]);
                if (row.World.Length != 0 && !worlds.Contains(row.World) || row.Body.Length != 0 && !bodyKeys.Contains(row.Body)) throw Error("rigidbody world/body binding differs");
            }
            foreach (var row in value.Rigidbodies) if (row.Parent.Length != 0 && !rbKeys.Contains(row.Parent)) throw Error("parent rigidbody is absent");
            var fixtures = new Dictionary<string, FixtureData>(StringComparer.Ordinal);
            foreach (var row in value.Colliders) {
                if (row == null || !keys.Add(Text(row.Owner)) || !ColliderTypes.Any(type => type.FullName == row.Type) || row.Paths == null || row.Fixtures == null
                    || row.Paths.Length > MaximumElements || row.Fixtures.Length > MaximumElements) throw Error("collider schema or type differs");
                Text(row.Material, true); Text(row.AttachedBody, true); Text(row.World, true); Flags(row.Enabled, row.Added);
                if (row.AttachedBody.Length != 0 && !rbKeys.Contains(row.AttachedBody) || row.World.Length != 0 && !worlds.Contains(row.World)) throw Error("collider body/world binding differs");
                if (row.Vtable <= 0 || row.Vtable >= 32 * 1024 * 1024) throw Error("collider vtable is outside the pinned module");
                Exact(row.Config, 19); Exact(row.Cache, 76); Flags(row.Config[12], row.Config[13], row.Config[14]);
                Capacity(row.FixtureCapacity, row.Fixtures.Length, 8);
                var tiled = row.Type == typeof(BoxCollider2D).FullName || row.Type == typeof(PolygonCollider2D).FullName;
                Exact(row.Tiling, tiled ? 50 : 0);
                if (tiled) { Flags(row.Tiling[0], row.Tiling[45]); if (row.TilingVtable <= 0 || row.TilingVtable >= 32 * 1024 * 1024) throw Error("tiling vtable differs"); }
                else if (row.TilingVtable != 0) throw Error("nontiled collider has a tiling vtable");
                var polygon = row.Type == typeof(PolygonCollider2D).FullName; var edge = row.Type == typeof(EdgeCollider2D).FullName;
                Exact(row.Geometry, polygon ? 0 : edge ? 22 : row.Type == typeof(CircleCollider2D).FullName ? 4 : 12);
                if (polygon) Capacity(row.PathCapacity, row.Paths.Length, 32);
                else if (row.PathCapacity != 0 || edge && row.Paths.Length != 1 || !edge && row.Paths.Length != 0) throw Error("collider path container differs");
                if (edge) Flags(row.Geometry[20], row.Geometry[21]);
                foreach (var path in row.Paths) {
                    if (path == null) throw Error("point path is absent"); var capacity = Capacity(path.Capacity, path.Count, 8);
                    Exact(path.Values, checked(capacity * 8)); Finite(path.Values, path.Count * 2);
                }
                for (var index = 0; index < row.Fixtures.Length; index++) {
                    var fixture = row.Fixtures[index];
                    if (fixture == null || fixture.Key != row.Owner + "/fixture[" + index + "]" || !fixtureKeys.Add(fixture.Key)
                        || !bodyKeys.Contains(fixture.Body) || fixture.Proxies == null || fixture.Proxies.Length > MaximumElements) throw Error("fixture identity/body differs");
                    fixtures.Add(fixture.Key, fixture); Exact(fixture.Values, 43); Flags(fixture.Values[42]);
                    if (Int(fixture.Values, 32) != fixture.Proxies.Length) throw Error("fixture proxy count differs");
                    ValidateShape(fixture.Shape);
                    for (var proxyIndex = 0; proxyIndex < fixture.Proxies.Length; proxyIndex++) {
                        var proxy = fixture.Proxies[proxyIndex];
                        if (proxy == null || proxy.Child != proxyIndex || proxy.TreeNode < -1 || proxy.TreeNode >= 32768) throw Error("fixture proxy index differs");
                        Exact(proxy.Bounds, 16); Finite(proxy.Bounds, 4);
                    }
                }
            }
            var coveredFixtures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in value.Bodies) {
                if (!worlds.Contains(row.World) || row.Owner.Length != 0 && !rbKeys.Contains(row.Owner) || row.FixtureOrder == null || row.FixtureOrder.Length > MaximumElements) throw Error("body owner/world differs");
                Exact(row.Values, 130);
                foreach (var fixture in row.FixtureOrder)
                    if (!fixtures.TryGetValue(fixture, out var entry) || entry.Body != row.Key || !coveredFixtures.Add(fixture)) throw Error("fixture list membership differs");
            }
            if (coveredFixtures.Count != fixtureKeys.Count) throw Error("body fixture lists omit native fixtures");
            var coveredBodies = new HashSet<string>(StringComparer.Ordinal); var linkedRigidbodies = new HashSet<string>(StringComparer.Ordinal);
            var bodyBindings = value.Bodies.ToDictionary(body => body.Key, StringComparer.Ordinal);
            var fixtureOwners = value.Colliders.SelectMany(c => c.Fixtures.Select(f => new { f.Key, c.Owner })).ToDictionary(x => x.Key, x => x.Owner, StringComparer.Ordinal);
            var colliderKeys = new HashSet<string>(value.Colliders.Select(c => c.Owner), StringComparer.Ordinal);
            foreach (var row in value.Worlds) {
                Exact(row.SceneValues, 14); Exact(row.WorldValues, 29); Flags(row.SceneValues[12], row.SceneValues[13], row.WorldValues[12]);
                if (row.BodyOrder == null || row.NonStaticBodies == null || row.StaticBodies == null || row.MovementOrder == null || row.Hierarchy == null || row.Hierarchy.Length != 64
                    || row.ContactCount < 0 || row.JointCount < 0 || row.IgnorePairCount < 0 || row.CallbackPairCount < 0 || row.CallbackCollisionCount < 0) throw Error("world collections differ");
                if ((row.JointCount != 0 && !value.CoverageGaps.Contains("physics-world-joints:" + row.Key))
                    || (row.IgnorePairCount != 0 && !value.CoverageGaps.Contains("physics-scene-ignore-map:" + row.Key))
                    || ((row.SceneValues[12] != 0 || (Int(row.WorldValues, 0) & 2) != 0) && !value.CoverageGaps.Contains("physics-world-is-running:" + row.Key)))
                    throw Error("unsupported world, joint or ignore-pair state lost its coverage gap");
                foreach (var body in row.BodyOrder) if (!bodyKeys.Contains(body) || !coveredBodies.Add(body) || value.Bodies.Single(entry => entry.Key == body).World != row.Key) throw Error("world body list differs");
                if (row.NonStaticBodies.Concat(row.StaticBodies).Any(body => !row.BodyOrder.Contains(body)) || row.MovementOrder.Any(owner => owner.Length != 0 && !rbKeys.Contains(owner))) throw Error("world body/movement array binding differs");
                Capacity(row.NonStaticCapacity, row.NonStaticBodies.Length, 8); Capacity(row.StaticCapacity, row.StaticBodies.Length, 8); Capacity(row.MovementCapacity, row.MovementOrder.Length, 8);
                foreach (var bucket in row.Hierarchy) {
                    if (bucket == null || bucket.Length > MaximumOwners) throw Error("hierarchy bucket differs");
                    foreach (var owner in bucket) if (!rbKeys.Contains(owner) || !linkedRigidbodies.Add(owner)) throw Error("rigidbody hierarchy repeats an owner");
                }
                var broadphase = CheckpointPhysicsBroadphaseState.DecodeCompact(row.Broadphase);
                Capacity(row.PairCapacity, broadphase.Pairs.Length / 2, 8); Capacity(row.MoveCapacity, broadphase.Moves.Length, 4);
                var sourceProxies = value.Colliders.SelectMany(collider => collider.Fixtures).Where(fixture => value.Bodies.Single(body => body.Key == fixture.Body).World == row.Key)
                    .SelectMany(fixture => fixture.Proxies.Select((proxy, index) => fixture.Key + "/proxy[" + index + "]")).OrderBy(key => key, StringComparer.Ordinal).ToArray();
                var treeProxies = broadphase.Nodes.Where(node => node.Proxy != null).Select(node => node.Proxy!).OrderBy(key => key, StringComparer.Ordinal).ToArray();
                if (!sourceProxies.SequenceEqual(treeProxies)) throw Error("world broadphase omits fixture proxies");
                ValidateContacts(row, fixtures, bodyBindings, value.CoverageGaps);
                ValidateCallbacks(row, fixtures, fixtureOwners, colliderKeys, rbKeys);
                if (row.ContactCount != 0 && !value.ContactFactoryInitialized) throw Error("active contact without its initialized factory");
            }
            if (coveredBodies.Count != bodyKeys.Count) throw Error("world body lists omit bodies");
            if (value.Worlds.Length != 0 && value.Globals == null) throw Error("global physics owners were omitted");
            if (value.Globals != null) {
                ResidentPhysicsGlobals.Validate(value.Globals);
                if (!value.Worlds.Select(row => row.Key).SequenceEqual(value.Globals.Scenes.Select(row => row.Key))) throw Error("global scene inventory differs");
                if (value.Globals.CoverageGaps.Except(value.CoverageGaps, StringComparer.Ordinal).Any()) throw Error("global coverage gaps were omitted");
                foreach (var world in value.Worlds) {
                    var global = value.Globals.Scenes.Single(scene => scene.Key == world.Key);
                    ValidateContactCells(world, global); ValidateCallbackOwners(world, global, value.CoverageGaps);
                }
            }
        }

        internal static void ValidateShape(ShapeData shape)
        {
            if (shape == null || shape.Type < 0 || shape.Type > 4 || shape.Vtable <= 0 || shape.Vtable >= 32 * 1024 * 1024 || shape.VertexCount < 0 || shape.VertexCount > MaximumElements)
                throw Error("Box2D shape schema differs");
            Exact(shape.Values, shape.Type == 0 ? 12 : shape.Type == 1 ? 38 : shape.Type == 2 ? 144 : shape.Type == 3 ? 26 : 20);
            Exact(shape.Vertices, shape.Type == 3 ? checked(shape.VertexCount * 8) : 0);
            if ((shape.Type < 2 || shape.Type == 4) && shape.VertexCount != 0 || shape.Type == 2 && (shape.VertexCount < 3 || shape.VertexCount > 8 || Int(shape.Values, 140) != shape.VertexCount)
                || shape.Type == 3 && Int(shape.Values, 4) != shape.VertexCount) throw Error("Box2D shape vertex count differs");
            if (shape.Type == 0) Finite(shape.Values, 3);
            else if (shape.Type == 1) { Finite(shape.Values, 9); Flags(shape.Values[36], shape.Values[37]); }
            else if (shape.Type == 2) { Finite(Slice(shape.Values, 0, 12), 3); Finite(Slice(shape.Values, 12, shape.VertexCount * 8), shape.VertexCount * 2); Finite(Slice(shape.Values, 76, shape.VertexCount * 8), shape.VertexCount * 2); }
            else if (shape.Type == 3) { Finite(Slice(shape.Values, 0, 4), 1); Finite(Slice(shape.Values, 8, 16), 4); Flags(shape.Values[24], shape.Values[25]); Finite(shape.Vertices, shape.VertexCount * 2); }
            else Finite(shape.Values, 5);
        }

        private static void RequireOwners(Data source, Data target)
        {
            if (!source.Rigidbodies.Select(value => value.Owner).SequenceEqual(target.Rigidbodies.Select(value => value.Owner))
                || !source.Colliders.Select(value => value.Owner + ":" + value.Type).SequenceEqual(target.Colliders.Select(value => value.Owner + ":" + value.Type)))
                throw Error("source/target actual component directories differ");
        }
        private static void RequireCompatible(Data source, Data target)
        {
            RequireOwners(source, target);
            for (var index = 0; index < source.Rigidbodies.Length; index++) {
                var a = source.Rigidbodies[index]; var b = target.Rigidbodies[index];
                if (a.Active != b.Active || a.Body != b.Body || a.World != b.World || a.Parent != b.Parent || a.Material != b.Material)
                    throw Error("Rigidbody native ownership is not synchronized: " + a.Owner);
            }
            for (var index = 0; index < source.Colliders.Length; index++) {
                var a = source.Colliders[index]; var b = target.Colliders[index];
                if (a.Material != b.Material || a.AttachedBody != b.AttachedBody || a.World != b.World || a.Active != b.Active || a.Enabled != b.Enabled || a.Added != b.Added
                    || a.Vtable != b.Vtable || a.TilingVtable != b.TilingVtable || a.FixtureCapacity != b.FixtureCapacity || a.PathCapacity != b.PathCapacity
                    || a.Paths.Length != b.Paths.Length || a.Fixtures.Length != b.Fixtures.Length)
                    throw Error($"collider native allocation/registration is not synchronized: {a.Owner} "
                        + $"mat=({a.Material},{b.Material}) body=({a.AttachedBody},{b.AttachedBody}) world=({a.World},{b.World}) "
                        + $"active=({a.Active},{b.Active}) enabled=({a.Enabled},{b.Enabled}) added=({a.Added},{b.Added}) "
                        + $"vt=({a.Vtable},{b.Vtable}) tvt=({a.TilingVtable},{b.TilingVtable}) "
                        + $"fcap=({a.FixtureCapacity},{b.FixtureCapacity}) pcap=({a.PathCapacity},{b.PathCapacity}) "
                        + $"paths=({a.Paths.Length},{b.Paths.Length}) fix=({a.Fixtures.Length},{b.Fixtures.Length})");
                for (var path = 0; path < a.Paths.Length; path++)
                    if (a.Paths[path].Count != b.Paths[path].Count || a.Paths[path].Capacity != b.Paths[path].Capacity) throw Error("collider point allocation is not synchronized: " + a.Owner);
                for (var fixture = 0; fixture < a.Fixtures.Length; fixture++) {
                    var first = a.Fixtures[fixture]; var second = b.Fixtures[fixture];
                    if (first.Key != second.Key || first.Body != second.Body || first.Proxies.Length != second.Proxies.Length || first.Shape.Type != second.Shape.Type
                        || first.Shape.Vtable != second.Shape.Vtable || first.Shape.VertexCount != second.Shape.VertexCount)
                        throw Error("fixture shape allocation differs: " + first.Key);
                }
            }
            if (!source.Bodies.Select(value => value.Key).SequenceEqual(target.Bodies.Select(value => value.Key))
                || !source.Worlds.Select(value => value.Key).SequenceEqual(target.Worlds.Select(value => value.Key))) throw Error("native body/world ownership differs");
            foreach (var world in source.Worlds) {
                var other = target.Worlds.Single(value => value.Key == world.Key);
                if (CheckpointPhysicsBroadphaseState.DecodeCompact(other.Broadphase).Nodes.Length > CheckpointPhysicsBroadphaseState.DecodeCompact(world.Broadphase).Nodes.Length)
                    throw Error("target broadphase capacity needs a supported shrink transaction");
            }
        }
        private static void RequireEqual(Data source, Data actual)
        { if (!Encode(source).SequenceEqual(Encode(actual))) throw Error("complete captured native field/alias readback differs"); }

        private static Tuple<IntPtr, byte[]>[] Chain(Local live, IntPtr first, int size, int nextOffset, int? count)
        {
            var seen = new HashSet<IntPtr>(); var rows = new List<Tuple<IntPtr, byte[]>>();
            for (var pointer = first; pointer != IntPtr.Zero;) {
                if (!seen.Add(pointer) || seen.Count > MaximumElements) throw Error("native linked list is cyclic or exceeds its bound");
                var bytes = live.Read(pointer, size); rows.Add(Tuple.Create(pointer, bytes)); pointer = M.Ptr(bytes, nextOffset);
            }
            if (count != null && count != rows.Count) throw Error("native linked-list count differs"); return rows.ToArray();
        }
        private static string[] ReadPointerArray(Local live, byte[] header, Dictionary<IntPtr, string> bindings, string label, out ulong capacity, bool allowNull = false)
        {
            var count = DynamicCount(header, 8, out capacity); var bytes = live.Read(M.Ptr(header, 0), checked(count * 8));
            if ((capacity & 1) != 0 && (capacity >> 1) != 0) live.Gaps.Add("borrowed-physics-pointer-buffer:" + label);
            return Enumerable.Range(0, count).Select(index => Name(bindings, M.Ptr(bytes, index * 8), label, allowNull)).ToArray();
        }
        private static int[] ReadIntArray(Local live, byte[] header, int stride, out ulong capacity)
        {
            var count = DynamicCount(header, stride, out capacity); var bytes = live.Read(M.Ptr(header, 0), checked(count * stride));
            if ((capacity & 1) != 0 && (capacity >> 1) != 0) live.Gaps.Add("borrowed-broadphase-value-buffer");
            return Enumerable.Range(0, bytes.Length / 4).Select(index => Int(bytes, index * 4)).ToArray();
        }
        private static int DynamicCount(byte[] header, int stride, out ulong capacity)
        {
            Exact(header, 32); var count = Bound(Long(header, 16), MaximumElements, "dynamic array count"); capacity = M.U64(header, 24);
            var allocated = Capacity(capacity, count, stride);
            if (allocated != 0 && M.Ptr(header, 0) == IntPtr.Zero || allocated == 0 && M.Ptr(header, 0) != IntPtr.Zero) throw Error("dynamic array pointer/capacity differs");
            return count;
        }
        internal static int Capacity(ulong encoded, int count, int stride)
        {
            if (count < 0 || count > MaximumElements || stride <= 0 || stride > 1024 || (encoded >> 1) > MaximumElements || (ulong)count > (encoded >> 1)
                || (encoded >> 1) * (ulong)stride > 16 * 1024 * 1024) throw Error("dynamic array size/capacity exceeds its bound");
            return checked((int)(encoded >> 1));
        }
        private static string NativeOwner(ResidentObjectDirectory directory, IntPtr pointer, Type type)
        {
            if (pointer == IntPtr.Zero) return "";
            var matches = directory.NativeBindings.Where(pair => pair.Value.GetType() == type && M.Owner(pair.Value) == pointer).ToArray();
            if (matches.Length != 1) throw Error("native owner has no unique directory binding"); return matches[0].Key;
        }
        private static void CheckOwner(byte[] bytes, Component owner)
        {
            if (Int(bytes, 8) != owner.GetInstanceID() || M.Ptr(bytes, 48) != M.Owner(owner.gameObject)) throw Error("native component owner/instance differs");
        }
        private static string SceneName(Local live, IntPtr pointer) => pointer == IntPtr.Zero ? "" : Name(live.SceneNames, pointer, "physics scene");
        private static string Name(Dictionary<IntPtr, string> values, IntPtr pointer, string label, bool allowNull = false)
        {
            if (allowNull && pointer == IntPtr.Zero) return "";
            return values.TryGetValue(pointer, out var key) ? key : throw Error(label + " has no unique binding");
        }
        private static void Bind(Dictionary<string, IntPtr> values, Dictionary<IntPtr, string> reverse, string key, IntPtr pointer)
        {
            if (pointer == IntPtr.Zero || values.ContainsKey(key) || reverse.ContainsKey(pointer)) throw Error("native owner is absent or multiply bound: " + key);
            values.Add(key, pointer); reverse.Add(pointer, key);
        }
        private static IntPtr Link(Dictionary<string, IntPtr> values, string key) => key.Length == 0 ? IntPtr.Zero
            : values.TryGetValue(key, out var pointer) ? pointer : throw Error("native link binding is absent: " + key);
        private static int NativeSize(string type)
        {
            if (type == typeof(BoxCollider2D).FullName) return 304;
            if (type == typeof(PolygonCollider2D).FullName) return 320;
            if (type == typeof(CircleCollider2D).FullName) return 232;
            if (type == typeof(CapsuleCollider2D).FullName) return 240;
            if (type == typeof(EdgeCollider2D).FullName) return 288;
            throw Error("unsupported actual collider type: " + type);
        }
        private static int Rva(IntPtr pointer, IntPtr engine, int length)
        { var value = pointer.ToInt64() - engine.ToInt64(); return value > 0 && value < length ? checked((int)value) : throw Error("native vtable is outside pinned engine"); }
        private static void WriteParts(IntPtr owner, byte[] values, int[] spans)
        {
            var at = 0; for (var index = 0; index < spans.Length; index += 2) {
                M.Write(IntPtr.Add(owner, spans[index]), Slice(values, at, spans[index + 1])); at += spans[index + 1];
            }
            if (at != values.Length) throw Error("native value span schema differs");
        }
        private static void WritePointer(IntPtr slot, IntPtr value) => M.Write(slot, BitConverter.GetBytes(value.ToInt64()));
        private static Vector2 Vector(byte[] bytes, int offset) => new Vector2(Float(bytes, offset), Float(bytes, offset + 4));
        private static Vector2[] Points(PointData path) => Enumerable.Range(0, path.Count).Select(index => Vector(path.Values, index * 8)).ToArray();
        private static float Float(byte[] bytes, int offset) => BitConverter.ToSingle(bytes, offset);
        private static int Int(byte[] bytes, int offset) => BitConverter.ToInt32(bytes, offset);
        private static long Long(byte[] bytes, int offset) => BitConverter.ToInt64(bytes, offset);
        private static byte[] Slice(byte[] bytes, int offset, int count) => M.Slice(bytes, offset, count);
        private static byte[] Join(params byte[][] values)
        { var result = new byte[values.Sum(value => value.Length)]; var at = 0; foreach (var value in values) { Buffer.BlockCopy(value, 0, result, at, value.Length); at += value.Length; } return result; }
        private static int Bound(long value, int maximum, string label) => value >= 0 && value <= maximum ? (int)value : throw Error(label + " exceeds its bound");
        private static string Text(string value, bool empty = false)
        { if (value == null || !empty && value.Length == 0 || value.Length > 16384 || value.IndexOf('\0') >= 0) throw Error("binding text differs"); return value; }
        private static void Exact(byte[] value, int length) { if (value == null || value.Length != length) throw Error("native scalar byte count differs"); }
        private static void Flags(params byte[] values) { if (values.Any(value => value > 1)) throw Error("native boolean byte differs"); }
        private static void Finite(byte[] value, int floats)
        { for (var index = 0; index < floats; index++) if (float.IsNaN(Float(value, index * 4)) || float.IsInfinity(Float(value, index * 4))) throw Error("active native geometry is not finite"); }
        private static T NativeEntry<T>(IntPtr engine, int rva, string prefix) where T : class
        {
            var pointer = IntPtr.Add(engine, rva);
            if (BitConverter.ToString(M.Read(pointer, prefix.Length / 2)).Replace("-", "").ToLowerInvariant() != prefix)
                throw Error("native physics ABI differs at 0x" + rva.ToString("x"));
            return (T)(object)Marshal.GetDelegateForFunctionPointer(pointer, typeof(T));
        }
        private static InvalidDataException Error(string detail) => new InvalidDataException("Resident physics: " + detail + ".");
    }
}
