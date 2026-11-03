using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentPhysicsStateTests
{
    [TestMethod] public void TypedPhysicsSceneHandleAndContactArraysUseActualUnityFieldLayout()
    {
        var api = new Api(); var sceneType = api.ParameterType("ReadPhysicsSceneHandle", 0);
        Assert.AreEqual(0, api.Call("ReadPhysicsSceneHandle", Activator.CreateInstance(sceneType)!));
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
        Assert.IsNotNull(root);
        var globals = Assembly.LoadFrom(Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
            .GetType("HollowKnightTAS.Runtime.FullRun.ResidentPhysicsGlobals", true)!;
        var method = globals.GetMethod("DescribeManagedReferences", BindingFlags.Static | BindingFlags.NonPublic)!;
        var contactType = sceneType.Assembly.GetType("UnityEngine.ContactPoint2D", true)!;
        var roots = new Dictionary<string, object?> { ["physics/shared-collision"] = null, ["physics/shared-contacts"] = Array.CreateInstance(contactType, 2) };
        var gaps = new List<string>();
        var rows = (Array)method.Invoke(null, new object[] { roots, (Func<int, Type, string?>)((_, _) => null), (Action<string>)gaps.Add })!;
        Assert.AreEqual(8, rows.Length); Assert.AreEqual(0, gaps.Count);
        foreach (var row in rows) Assert.AreEqual("", Get(row!, "Owner"));
    }
    [TestMethod] public void ActualTypesAndArbitraryPathsRoundTrip()
    {
        var a = new Api(); var polygon = a.Collider("arbitrary/owner[7]", "PolygonCollider2D");
        Set(polygon, "Paths", a.Rows("PointData", a.Points(3, 5), a.Points(7, 11), a.Points(4, 4))); Set(polygon, "PathCapacity", 10UL);
        var edge = a.Collider("edge[6]", "EdgeCollider2D"); Set(edge, "Paths", a.Rows("PointData", a.Points(5, 9)));
        var source = a.Data(polygon, edge, a.Collider("box", "BoxCollider2D"), a.Collider("circle", "CircleCollider2D"), a.Collider("capsule", "CapsuleCollider2D"));
        var bytes = a.Encode(source); var copy = a.Decode(bytes); CollectionAssert.AreEqual(bytes, a.Encode(copy));
        var paths = (Array)Get(((Array)Get(copy, "Colliders")).GetValue(0)!, "Paths"); Assert.AreEqual(3, paths.Length); Assert.AreEqual(22UL, Get(paths.GetValue(1)!, "Capacity"));
    }
    [TestMethod] public void DynamicCapacityUsesEncodedHalfCapacity()
    {
        var a = new Api(); Assert.AreEqual(4, a.Capacity(8, 4, 8)); Assert.AreEqual(4, a.Capacity(9, 4, 8)); Assert.AreEqual(0, a.Capacity(1, 0, 32));
        a.Reject(() => a.Capacity(8, 5, 8)); a.Reject(() => a.Capacity(ulong.MaxValue, 0, 8));
    }
    [TestMethod] public void PolygonUnusedStorageIsPreservedButActiveGeometryIsChecked()
    {
        var a = new Api(); var row = a.Collider("polygon", "PolygonCollider2D"); var path = a.Points(3, 7);
        Set(row, "PathCapacity", 2UL); Set(row, "Paths", a.Rows("PointData", path)); var values = (byte[])Get(path, "Values");
        Array.Copy(BitConverter.GetBytes(float.NaN), 0, values, 24, 4); var data = a.Data(row); var bytes = a.Encode(data); CollectionAssert.AreEqual(bytes, a.Encode(a.Decode(bytes)));
        Array.Copy(BitConverter.GetBytes(float.NaN), 0, values, 0, 4); a.Reject(() => a.Encode(data));
    }
    [TestMethod] public void AllBox2DShapeKindsUseExactPointerFreeLayouts()
    {
        var a = new Api(); var circle = a.Shape(0, 12, 0); var edge = a.Shape(1, 38, 0); var polygon = a.Shape(2, 144, 6); var chain = a.Shape(3, 26, 13);
        Array.Copy(BitConverter.GetBytes(6), 0, (byte[])Get(polygon, "Values"), 140, 4); Array.Copy(BitConverter.GetBytes(13), 0, (byte[])Get(chain, "Values"), 4, 4); Set(chain, "Vertices", new byte[104]);
        foreach (var shape in new[] { circle, edge, polygon, chain, a.Shape(4, 20, 0) }) a.Call("ValidateShape", shape);
        Set(polygon, "VertexCount", 9); a.Reject(() => a.Call("ValidateShape", polygon)); Set(chain, "Vertices", new byte[105]); a.Reject(() => a.Call("ValidateShape", chain));
    }
    [TestMethod] public void UnsupportedNativeDomainsCannotBeComplete()
    {
        var a = new Api(); var data = a.Data(); Set(data, "CoverageGaps", new[] { "physics-manager-shared-scripting-callback-state" });
        a.Reject(() => a.Call("RequireCompleteCoverage", a.Decode(a.Encode(data))));
    }
    [TestMethod] public void UnknownTypesDuplicateOwnersAndInvalidCapacityAreRejected()
    {
        var a = new Api(); var box = a.Collider("owner", "BoxCollider2D"); a.Reject(() => a.Encode(a.Data(box, box)));
        Set(box, "Type", "UnityEngine.FutureCollider2D"); a.Reject(() => a.Encode(a.Data(box)));
        var polygon = a.Collider("polygon", "PolygonCollider2D"); Set(polygon, "PathCapacity", 2UL); Set(polygon, "Paths", a.Rows("PointData", a.Points(4, 3))); a.Reject(() => a.Encode(a.Data(polygon)));
    }
    [TestMethod] public void NoncanonicalWireAndSchemaRevisionAreRejected()
    {
        var a = new Api(); var bytes = a.Encode(a.Data()); a.Reject(() => a.Decode(bytes.Concat(new byte[] { 32 }).ToArray())); var data = a.Data(); Set(data, "Version", 2); a.Reject(() => a.Encode(data));
    }
    [TestMethod] public void ActiveContactsRestoreBothManifoldsManagerOrderAndTargetLocalBodyEdges()
    {
        var api = new Api(); var local = api.Row("Local"); var world = api.Row("WorldData"); Set(world, "Key", "w");
        var fixtureRows = api.Map("FixtureData"); var bodyRows = api.Map("BodyData"); var allocations = new List<IntPtr>();
        IntPtr Alloc(int size) { var ptr = Marshal.AllocHGlobal(size); allocations.Add(ptr); Marshal.Copy(new byte[size], 0, ptr, size); return ptr; }
        try {
            var state = Alloc(360); var contactPointers = (IDictionary)Get(local, "Contacts");
            var fixturePointers = (IDictionary)Get(local, "Fixtures"); var bodyPointers = (IDictionary)Get(local, "Bodies");
            var collider = api.Row("ColliderData");
            foreach (var name in new[] { "a", "b", "c" }) {
                var f = api.Row("FixtureData"); Set(f, "Key", name); Set(f, "Body", name); Set(f, "Shape", api.Shape(0, 12, 0));
                Set(f, "Proxies", api.Rows("ProxyData", api.Row("ProxyData"))); fixtureRows.Add(name, f);
                var b = api.Row("BodyData"); Set(b, "Key", name); bodyRows.Add(name, b);
                bodyPointers.Add(name, Alloc(200)); fixturePointers.Add(name, Alloc(88));
            }
            Set(collider, "Fixtures", api.Rows("FixtureData", fixtureRows.Values.Cast<object>().ToArray()));
            Set(Get(local, "Data"), "Colliders", api.Rows("ColliderData", collider));
            ((IDictionary)Get(local, "CollisionRows")).Add("w", Array.Empty<IntPtr>());
            object Contact(string b, bool toi, float impulse) {
                var c = api.Row("ContactData"); var key = (string)api.Call("ContactKey", "a", 0, b, 0);
                Set(c, "Key", key); Set(c, "FixtureA", "a"); Set(c, "FixtureB", b); Set(c, "Vtable", 0x16fea80);
                Set(c, "Flags", toi ? 0x86U : 6U); Set(c, "Padding", new byte[8]); var data = new byte[180]; Set(c, "Values", data);
                foreach (var start in new[] { 8, 80 }) {
                    Array.Copy(BitConverter.GetBytes(1), 0, data, start + 60, 4);
                    Array.Copy(BitConverter.GetBytes(impulse + start), 0, data, start + 8, 4);
                    Array.Copy(BitConverter.GetBytes(-impulse), 0, data, start + 12, 4);
                }
                Array.Copy(BitConverter.GetBytes(-1), 0, data, 176, 4); contactPointers.Add(key, Alloc(320)); return c;
            }
            var first = Contact("b", false, .125f); var second = Contact("c", true, .625f);
            var one = (string)Get(first, "Key"); var two = (string)Get(second, "Key");
            Set(world, "Contacts", api.Rows("ContactData", first, second)); Set(world, "ContactCount", 2);
            Set(world, "BodyOrder", new[] { "a", "b", "c" }); Set(bodyRows["a"]!, "ContactOrder", new[] { two, one });
            Set(bodyRows["b"]!, "ContactOrder", new[] { one }); Set(bodyRows["c"]!, "ContactOrder", new[] { two });
            Set(world, "NonToiContacts", new[] { one }); Set(world, "ToiContacts", new[] { two });
            Set(world, "NonToiContactCapacity", 2UL); Set(world, "ToiContactCapacity", 2UL);
            foreach (var off in new[] { 160, 192 }) { Marshal.WriteIntPtr(state, off, Alloc(8)); Marshal.WriteInt64(state, off + 24, 2); }
            void Validate() => api.Call("ValidateContacts", world, fixtureRows, bodyRows, Array.Empty<string>());
            Validate(); api.Call("ApplyContacts", world, bodyRows, local, state, (Action)(() => { }));
            var p = (IntPtr)contactPointers[one]!; var q = (IntPtr)contactPointers[two]!;
            Assert.AreEqual(p, Marshal.ReadIntPtr(state, 128)); Assert.AreEqual(q, Marshal.ReadIntPtr(p, 24));
            Assert.AreEqual(p, Marshal.ReadIntPtr(q, 16)); Assert.AreEqual(IntPtr.Add(q, 32), Marshal.ReadIntPtr((IntPtr)bodyPointers["a"]!, 136));
            Assert.AreEqual(IntPtr.Add(p, 32), Marshal.ReadIntPtr(q, 32 + 24)); Assert.AreEqual(IntPtr.Add(q, 32), Marshal.ReadIntPtr(p, 32 + 16));
            Assert.AreEqual(bodyPointers["c"], Marshal.ReadIntPtr(q, 32)); Assert.AreEqual(q, Marshal.ReadIntPtr(q, 40));
            Assert.AreEqual(fixturePointers["b"], Marshal.ReadIntPtr(p, 104)); Assert.AreEqual(q, Marshal.ReadIntPtr(Marshal.ReadIntPtr(state, 192)));
            var restored = new byte[180]; Marshal.Copy(IntPtr.Add(p, 120), restored, 0, restored.Length);
            CollectionAssert.AreEqual((byte[])Get(first, "Values"), restored);
            Set(bodyRows["a"]!, "ContactOrder", new[] { one, one }); api.Reject(Validate); Set(bodyRows["a"]!, "ContactOrder", new[] { two, one });
            Set(world, "NonToiContacts", new[] { two }); api.Reject(Validate); Set(world, "NonToiContacts", new[] { one });
            Set(second, "ChildB", 1); api.Reject(Validate); Set(second, "ChildB", 0);
            Set(second, "Flags", 0x10086U); api.Reject(Validate);
        } finally { foreach (var ptr in allocations) Marshal.FreeHGlobal(ptr); }
    }
    [TestMethod] public void NativeContactChunksMayGrowOrReuseTheirCellsWithoutDisplacingAForeignOwner()
    {
        var a = new Api();
        var globals = a.Row("Data").GetType().Assembly.GetType("HollowKnightTAS.Runtime.FullRun.ResidentPhysicsGlobals", true)!;
        var chunkType = globals.GetNestedType("Chunk", All)!; var allocatorType = globals.GetNestedType("Allocator", All)!;
        object Chunk(int size, params string[] occupied) {
            var c = Activator.CreateInstance(chunkType, true)!; var owners = Enumerable.Repeat("", 16384 / size).ToArray(); Array.Copy(occupied, owners, occupied.Length);
            Set(c, "BlockSize", size); Set(c, "Owners", owners); return c;
        }
        object Allocator(params object[] chunks) {
            var result = Activator.CreateInstance(allocatorType, true)!; var array = Array.CreateInstance(chunkType, chunks.Length);
            for (var i = 0; i < chunks.Length; i++) array.SetValue(chunks[i], i);
            Set(result, "ChunkCapacity", 128); Set(result, "Chunks", array); return result;
        }
        const string one = "physics/contact/first", two = "physics/contact/second";
        var current = Allocator(Chunk(320, one, "foreign"));
        Assert.AreEqual(false, a.Call("RequireContactBlockLayout", Allocator(Chunk(320, one, "foreign")), current));
        Assert.AreEqual(true, a.Call("RequireContactBlockLayout", Allocator(Chunk(320, "", "foreign", two), Chunk(320, one)), current));
        a.Reject(() => a.Call("RequireContactBlockLayout", Allocator(Chunk(320, "", two, "foreign")), current));
        a.Reject(() => a.Call("RequireContactBlockLayout", Allocator(Chunk(320, one, "foreign"), Chunk(224, two)), current));
        a.Reject(() => a.Call("RequireContactBlockLayout", Allocator(Chunk(320, one, "foreign"), Chunk(320, "foreign2")), current));
        a.Reject(() => a.Call("RequireContactBlockLayout", Allocator(), current));
        Assert.AreEqual(true, a.Call("RequireContactBlockLayout", Allocator(), Allocator(Chunk(320, one, two))));
    }
    [TestMethod] public void SolidCallbackAndRetainedExitManifoldKeepWorldPointsAndBacklink()
    {
        var api = new Api(); var world = api.Row("WorldData"); Set(world, "Key", "physics/default");
        var fixtures = api.Map("FixtureData");
        foreach (var key in new[] { "first-fix", "second-fix" }) {
            var f = api.Row("FixtureData"); Set(f, "Proxies", api.Rows("ProxyData", api.Row("ProxyData"))); fixtures.Add(key, f);
        }
        var c = api.Row("ContactData"); var identity = (string)api.Call("ContactKey", "first-fix", 0, "second-fix", 0);
        Set(c, "Key", identity); Set(c, "CollisionIndex", 0); var values = new byte[180]; Set(c, "Values", values); // callback manifold 0
        Set(world, "Contacts", api.Rows("ContactData", c)); Set(world, "CallbackCollisionCount", 1); Set(world, "CallbackPairCount", 1);
        var cache = Get(world, "Callbacks"); Set(cache, "Capacity", 2UL); Set(cache, "VectorPadding", new byte[4]);
        var row = api.Row("CollisionValue"); Set(row, "KeyA", "a"); Set(row, "KeyB", "b"); Set(row, "ColliderA", "a"); Set(row, "ColliderB", "b");
        Set(row, "Receiving", "b"); Set(row, "ContactCount", 1); Set(row, "FlagsAndPadding", new byte[8]); Set(row, "ManifoldHeaderPadding", new byte[4]); Set(row, "ManifoldCapacity", 4UL);
        object Manifold(string key) {
            var m = api.Row("CallbackManifold"); Set(m, "Contact", key); Set(m, "FixtureA", "first-fix"); Set(m, "FixtureB", "second-fix");
            Set(m, "EnabledAndPadding", new byte[8]); var raw = new byte[72]; Array.Copy(BitConverter.GetBytes(2), 0, raw, 4, 4);
            Array.Copy(BitConverter.GetBytes(.875f), 0, raw, 60, 4); Array.Copy(BitConverter.GetBytes(-.5f), 0, raw, 68, 4); Set(m, "Values", raw); return m;
        }
        var current = Manifold(identity); var ended = Manifold(""); Set(row, "Manifolds", api.Rows("CallbackManifold", current, ended)); Set(cache, "Rows", api.Rows("CollisionValue", row));
        var owners = new Dictionary<string,string> { ["first-fix"]="a", ["second-fix"]="b" }; var ownerKeys = new HashSet<string> { "a","b" };
        void Validate() => api.Call("ValidateCallbacks", world, fixtures, owners, ownerKeys, new HashSet<string>());
        Validate();
        var local = api.Row("Local"); var allocations = new List<IntPtr>();
        IntPtr Alloc(int size) { var p = Marshal.AllocHGlobal(size); allocations.Add(p); Marshal.Copy(new byte[size], 0, p, size); return p; }
        try {
            var receiver = Alloc(16); var colliderA = Alloc(16); var contact = Alloc(312); var nativeRow = Alloc(104); var buffer = Alloc(224);
            var fixtureA = Alloc(88); var fixtureB = Alloc(88);
            ((IDictionary)Get(local, "Owners")).Add("a", colliderA); ((IDictionary)Get(local, "Owners")).Add("b", receiver);
            ((IDictionary)Get(local, "Contacts")).Add(identity, contact); ((IDictionary)Get(local, "Fixtures")).Add("first-fix", fixtureA);
            ((IDictionary)Get(local, "Fixtures")).Add("second-fix", fixtureB);
            Marshal.WriteIntPtr(nativeRow, 0, buffer); Marshal.WriteInt64(nativeRow, 24, 4);
            api.Call("WriteCallbackRows", cache, local, new[] { nativeRow }, (Action)(() => { }));
            Assert.AreEqual(receiver, Marshal.ReadIntPtr(nativeRow, 72)); Assert.AreEqual(colliderA, Marshal.ReadIntPtr(nativeRow, 80));
            Assert.AreEqual(receiver, Marshal.ReadIntPtr(nativeRow, 88)); Assert.AreEqual(contact, Marshal.ReadIntPtr(buffer, 8));
            Assert.AreEqual(IntPtr.Zero, Marshal.ReadIntPtr(buffer, 112 + 8)); Assert.AreEqual(fixtureB, Marshal.ReadIntPtr(buffer, 112 + 24));
            Assert.AreEqual(BitConverter.SingleToInt32Bits(.875f), Marshal.ReadInt32(buffer, 40 + 60));
            Assert.AreEqual(BitConverter.SingleToInt32Bits(-.5f), Marshal.ReadInt32(buffer, 112 + 40 + 68));
        } finally { foreach (var p in allocations) Marshal.FreeHGlobal(p); }
        Set(current, "Contact", ""); api.Reject(Validate); Set(current, "Contact", identity);
        Set(ended, "Contact", identity); api.Reject(Validate); Set(ended, "Contact", "");
        Set(row, "ContactCount", 2); api.Reject(Validate); Set(row, "ContactCount", 1);
        Set(row, "Receiving", "other"); api.Reject(Validate); Set(row, "Receiving", "b");
        var raw = (byte[])Get(ended, "Values"); Array.Copy(BitConverter.GetBytes(float.NaN), 0, raw, 68, 4); api.Reject(Validate);
    }
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static object Get(object row, string name) => row.GetType().GetField(name, All)!.GetValue(row)!;
    private static void Set(object row, string name, object value) => row.GetType().GetField(name, All)!.SetValue(row, value);
    private sealed class Api
    {
        private readonly Type type;
        internal Api()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory); while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
            Assert.IsNotNull(root); type = Assembly.LoadFrom(Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll")).GetType("HollowKnightTAS.Runtime.FullRun.ResidentPhysicsState", true)!;
        }
        internal object Call(string name, params object[] args) => type.GetMethod(name, All)!.Invoke(null, args)!;
        internal Type ParameterType(string name, int index) => type.GetMethod(name, All)!.GetParameters()[index].ParameterType;
        internal object Row(string name) => Activator.CreateInstance(type.GetNestedType(name, All)!, true)!;
        internal IDictionary Map(string name) => (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), type.GetNestedType(name, All)!))!;
        internal Array Rows(string name, params object[] rows) { var result = Array.CreateInstance(type.GetNestedType(name, All)!, rows.Length); for (var i = 0; i < rows.Length; i++) result.SetValue(rows[i], i); return result; }
        internal object Data(params object[] colliders) { var row = Row("Data"); Set(row, "Colliders", Rows("ColliderData", colliders)); return row; }
        internal object Collider(string owner, string kind)
        {
            var row = Row("ColliderData"); var tiled = kind == "BoxCollider2D" || kind == "PolygonCollider2D";
            Set(row, "Owner", owner); Set(row, "Type", "UnityEngine." + kind); Set(row, "Vtable", 16); Set(row, "TilingVtable", tiled ? 24 : 0); Set(row, "FixtureCapacity", 1UL);
            Set(row, "Config", new byte[19]); Set(row, "Cache", new byte[76]); Set(row, "Tiling", new byte[tiled ? 50 : 0]); Set(row, "Geometry", new byte[kind == "PolygonCollider2D" ? 0 : kind == "EdgeCollider2D" ? 22 : kind == "CircleCollider2D" ? 4 : 12]); return row;
        }
        internal object Points(int count, int capacity) { var row = Row("PointData"); Set(row, "Count", count); Set(row, "Capacity", (ulong)capacity * 2); Set(row, "Values", Enumerable.Range(0, capacity * 2).SelectMany(i => BitConverter.GetBytes(i * .25f)).ToArray()); return row; }
        internal object Shape(int kind, int size, int count) { var row = Row("ShapeData"); Set(row, "Type", kind); Set(row, "Vtable", 16); Set(row, "VertexCount", count); Set(row, "Values", new byte[size]); return row; }
        internal byte[] Encode(object data) => (byte[])Call("Encode", data);
        internal object Decode(byte[] bytes) => Call("Decode", bytes);
        internal int Capacity(ulong encoded, int count, int stride) => (int)Call("Capacity", encoded, count, stride);
        internal void Reject(Action action) { var error = Assert.ThrowsExactly<TargetInvocationException>(action); Assert.IsInstanceOfType<InvalidDataException>(error.GetBaseException()); }
    }
}
