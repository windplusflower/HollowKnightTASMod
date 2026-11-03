using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    // The actual Object-derived Physics2DSettings is 296 bytes. It is NOT the
    // 184-byte PhysicsManager2D behind gIPhysics2D. Object48 belongs to the base
    // adapter. Every remaining byte has the following ownership:
    // D: default-material PPtr48; scalar/vector/options/colour fields52..259;
    //    complete layer-collision values in the dynamic array at264.
    // S0: C++ padding117..119,126..127,260..263,276..279 is zero;
    //    matrix storage is owned, with capacity equal to its actual value count.
    // Dependency: target-local matrix pointer/allocator label and resolved PPtr;
    //    the 11 Box2D float globals, 2 squared limits and 64-byte job options
    //    written by the pinned UpdateBox2D implementation. Its padding is S0.
    // Gravity setters wake bodies and IgnoreLayerCollision refilters fixtures.
    // Those side effects are intentionally not replayed: the physics adapter
    // restores actual world/body/contact state independently in this transaction.
    internal static class ResidentPhysicsSettingsState
    {
        internal const int NativeTypeRva = 0x1986420, NativeSize = 296;
        private const int VtableRva = 0x1701828, ContextSlotRva = 0x19d7aa0, MaximumLayers = 4096;
        private static readonly int[] PaddingOffsets = { 117, 118, 119, 126, 127, 260, 261, 262, 263, 276, 277, 278, 279 };
        private static readonly int[] BooleanOffsets = { 112, 113, 114, 115, 116, 188, 189, 190, 191 };
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void UpdateBox2D(IntPtr owner);

        internal sealed class Data
        {
            public int Version = 1;
            public string Manager = "", DefaultMaterial = "";
            public bool DefaultMaterialPresent, MatrixOwnsStorage;
            public int MatrixCapacity;
            // Raw field bits preserve signed zero and NaN payloads. Padding
            // inside Values is zero; its actual bytes are recorded separately.
            public byte[] Values = new byte[208], Padding = new byte[13], Box2D = new byte[116];
            public uint[] LayerCollisionMatrix = Array.Empty<uint>();
            public string[] CoverageGaps = Array.Empty<string>();
            internal Data Copy() => new Data { Version = Version, Manager = Manager, DefaultMaterial = DefaultMaterial,
                DefaultMaterialPresent = DefaultMaterialPresent, MatrixOwnsStorage = MatrixOwnsStorage, MatrixCapacity = MatrixCapacity,
                Values = (byte[])Values.Clone(), Padding = (byte[])Padding.Clone(), Box2D = (byte[])Box2D.Clone(),
                LayerCollisionMatrix = (uint[])LayerCollisionMatrix.Clone(), CoverageGaps = (string[])CoverageGaps.Clone() };
        }
        internal sealed class Live
        {
            internal Data Snapshot = new Data();
            internal IntPtr Owner, Engine;
            internal readonly List<Tuple<IntPtr, byte[]>> Witness = new List<Tuple<IntPtr, byte[]>>();
            internal Action Admitted = null!;
            internal byte[] Read(IntPtr pointer, int count) { var bytes = M.Read(pointer, count); Witness.Add(Tuple.Create(pointer, bytes)); return bytes; }
            internal void RequireUnchanged() {
                Admitted(); foreach (var row in Witness) if (!row.Item2.SequenceEqual(M.Read(row.Item1, row.Item2.Length)))
                    throw Error("native settings or Box2D dependency changed during observation"); Admitted();
            }
        }

        internal static bool OwnsManager(ResidentNativeGlobalObjects.NativeClass type)
            => type != null && type.Rva == NativeTypeRva && type.Name == "Physics2DSettings" && type.Module == "Physics2D"
                && type.Size == NativeSize && type.ObjectDerived && type.GlobalManager && !type.Abstract && !type.Stripped;

        private static Tuple<string, IntPtr> FindManager(ResidentNativeGlobalObjects.CaptureResult globals)
        {
            if (globals == null) throw new ArgumentNullException(nameof(globals));
            var types = globals.Snapshot.Classes.Where(value => value.Rva == NativeTypeRva).ToArray();
            if (types.Length != 1 || !OwnsManager(types[0])) throw Error("registered Physics2DSettings native class differs");
            var key = "resident-native-global/" + types[0].Key;
            if (!globals.ManagerPointers.TryGetValue(key, out var owner) || owner == IntPtr.Zero)
                throw Error("registered Physics2DSettings singleton is absent or ambiguous");
            return Tuple.Create(key, owner);
        }

        internal static Live Capture(ResidentNativeGlobalObjects.CaptureResult globals, Func<object, string?> bind, Action admitted)
            => CaptureInternal(globals, bind, admitted, null);
        private static Live CaptureInternal(ResidentNativeGlobalObjects.CaptureResult globals, Func<object, string?> bind, Action admitted, NativeObject? knownMaterial)
        {
            if (bind == null || admitted == null) throw new ArgumentNullException();
            admitted(); var manager = FindManager(globals); var engine = LoadingEngineClockProbe.RequirePinnedEngine();
            var live = new Live { Owner = manager.Item2, Engine = engine.BaseAddress, Admitted = admitted };
            RequireOwner(live.Owner, live.Engine);
            var native = new byte[NativeSize];
            // The shared Object48 adapter may change flags/handles between
            // Prepare and Apply. Its fields are not a settings-body witness.
            Buffer.BlockCopy(live.Read(IntPtr.Add(live.Owner, 48), NativeSize - 48), 0, native, 48, NativeSize - 48);
            var pointer = M.Ptr(native, 264);
            var count = M.U64(native, 280); var taggedCapacity = M.U64(native, 288); var capacity = taggedCapacity >> 1;
            if (count > capacity || capacity > MaximumLayers || count != 0 && pointer == IntPtr.Zero)
                throw Error("native layer-collision matrix allocation differs");
            var matrixBytes = live.Read(pointer, checked((int)count * 4));
            var materialId = M.I32(native, 48); var binding = "";
            if (materialId != 0) {
                // A target asset may have been created after the directory
                // used to prepare this plan. Use only that exact resolved
                // wrapper here; the final full registry readback is separate.
                var material = !ReferenceEquals(knownMaterial, null) && ResidentObjectDirectory.IsLive(knownMaterial)
                    && knownMaterial!.GetInstanceID() == materialId ? knownMaterial : globals.BorrowRegisteredObject(materialId, typeof(PhysicsMaterial2D));
                if (material != null) binding = bind(material) ?? throw Error("default PhysicsMaterial2D lacks a portable binding");
            }
            var dependency = new byte[116];
            Buffer.BlockCopy(live.Read(IntPtr.Add(live.Engine, 0x1985910), 44), 0, dependency, 0, 44);
            Buffer.BlockCopy(live.Read(IntPtr.Add(live.Engine, 0x1a2a824), 8), 0, dependency, 44, 8);
            Buffer.BlockCopy(live.Read(IntPtr.Add(live.Engine, 0x1985980), 64), 0, dependency, 52, 64);
            var result = new Data { Manager = manager.Item1, DefaultMaterial = binding, DefaultMaterialPresent = materialId != 0,
                MatrixCapacity = (int)capacity, MatrixOwnsStorage = (taggedCapacity & 1) == 0,
                Values = ExtractValues(native), Padding = PaddingOffsets.Select(offset => native[offset]).ToArray(), Box2D = dependency,
                LayerCollisionMatrix = Enumerable.Range(0, (int)count).Select(index => M.U32(matrixBytes, index * 4)).ToArray() };
            result.CoverageGaps = RequiredGaps(result, false); Validate(result); live.Snapshot = result; live.RequireUnchanged(); return live;
        }

        private static void RequireOwner(IntPtr owner, IntPtr engine)
        {
            if (owner == IntPtr.Zero || M.Ptr(IntPtr.Add(engine, ContextSlotRva)) != owner)
                throw Error("Physics2DSettings does not own actual gContext slot 14");
            var header = M.Read(owner, 16);
            if (M.Ptr(header, 0) != IntPtr.Add(engine, VtableRva)) throw Error("Physics2DSettings native vtable differs");
            var getter = M.Ptr(IntPtr.Add(IntPtr.Add(engine, VtableRva), 64));
            if (getter != IntPtr.Add(engine, 0xc04290)) throw Error("Physics2DSettings actual-type getter differs");
            RequireFunction(engine, 0xc04290, 8, "8e89e6066ef43776555f82e0065b48602924c5290d6d4c7bca1855f228d7cda0");
            RequireFunction(engine, 0xc04110, 10, "4e4aa8f406361ca69dc33e874a15ca6bf7fa2f27c231ecd1ee26ed8b19b5e20f");
            RequireFunction(engine, 0xc08b80, 241, "97c75014cf0b268121b45bc50acc4fe969aeda20dee5ac680f6f753c5988797a");
        }
        private static void RequireFunction(IntPtr engine, int rva, int length, string expected)
        {
            using (var sha = SHA256.Create()) if (BitConverter.ToString(sha.ComputeHash(M.Read(IntPtr.Add(engine, rva), length))).Replace("-", "").ToLowerInvariant() != expected)
                throw Error("pinned Physics2DSettings function ABI differs");
        }

        internal static byte[] ExtractValues(byte[] native)
        {
            if (native == null || native.Length != NativeSize) throw Error("native settings body extent differs");
            var result = M.Slice(native, 52, 208);
            foreach (var offset in PaddingOffsets.Where(offset => offset >= 52 && offset < 260)) result[offset - 52] = 0;
            return result;
        }
        internal static void Validate(Data data)
        {
            if (data == null || data.Version != 1 || data.Manager == null || !data.Manager.StartsWith("resident-native-global/1986420/Physics2D/", StringComparison.Ordinal)
                || data.Manager.Length > 4096 || data.Manager.IndexOf('\0') >= 0 || data.DefaultMaterial == null || data.DefaultMaterial.Length > 32768 || data.DefaultMaterial.IndexOf('\0') >= 0
                || !data.DefaultMaterialPresent && data.DefaultMaterial.Length != 0 || data.Values == null || data.Values.Length != 208 || data.Padding == null || data.Padding.Length != 13
                || data.Box2D == null || data.Box2D.Length != 116 || data.LayerCollisionMatrix == null || data.LayerCollisionMatrix.Length > MaximumLayers
                || data.MatrixCapacity < data.LayerCollisionMatrix.Length || data.MatrixCapacity > MaximumLayers || data.CoverageGaps == null
                || data.CoverageGaps.Length > 16 || data.CoverageGaps.Any(value => string.IsNullOrEmpty(value) || value.Length > 4096)) throw Error("native settings schema differs");
            foreach (var offset in BooleanOffsets) if (data.Values[offset - 52] > 1) throw Error("native settings boolean differs");
            foreach (var offset in PaddingOffsets.Where(offset => offset >= 52 && offset < 260))
                if (data.Values[offset - 52] != 0) throw Error("native settings value image contains padding");
            var mode = M.I32(data.Values, 120 - 52);
            if (mode < 0 || mode > 2) throw Error("native 2D simulation mode differs");
        }
        private static string[] RequiredGaps(Data data, bool cold)
        {
            Validate(data); var result = new List<string>();
            if (data.DefaultMaterialPresent && data.DefaultMaterial.Length == 0) result.Add("physics-settings/default-material-existing-wrapper-missing");
            // Engine query entrypoints index all 32 layers without a size check.
            // Additional serialized rows remain D and are not silently omitted.
            if (data.LayerCollisionMatrix.Length < 32) result.Add("physics-settings/layer-matrix-has-fewer-than-engine-layers");
            if (!data.MatrixOwnsStorage) result.Add("physics-settings/borrowed-layer-matrix-alias-ownership");
            if (!BuildBox2D(data.Values).SequenceEqual(DependencyValues(data.Box2D))) result.Add("physics-settings/Box2D-global-dependency-does-not-match-settings");
            if (cold && (data.Padding.Any(value => value != 0) || data.Box2D[54] != 0 || data.Box2D[55] != 0
                || data.MatrixCapacity != data.LayerCollisionMatrix.Length)) result.Add("physics-settings/common-native-storage-canon");
            return result.ToArray();
        }
        internal static void RequireCanonicalizable(Data data)
        {
            var gaps = RequiredGaps(data, false);
            if (gaps.Length != 0) throw Error("native settings are not canonicalizable: " + string.Join(";", gaps));
        }
        internal static void RequireComplete(Data data)
        {
            var gaps = RequiredGaps(data, true);
            if (gaps.Length != 0 || data.CoverageGaps.Length != 0) throw Error("native settings coverage is incomplete: " + string.Join(";", gaps.Concat(data.CoverageGaps)));
        }
        private static byte[] DependencyValues(byte[] actual)
        { var values = (byte[])actual.Clone(); values[54] = values[55] = 0; return values; }

        // Matches the precise SSE movss/mulss order at UpdateBox2D c08b80.
        // Source capture must independently agree with these bits before the
        // dependency is admitted; a modified/stale global is an explicit gap.
        internal static byte[] BuildBox2D(byte[] values)
        {
            if (values == null || values.Length != 208) throw Error("native settings value image extent differs");
            var result = new byte[116]; var offsets = new[] { 88, 68, 72, 76, 80, 84, 92, 96, 100, 104, 108 };
            for (var index = 0; index < offsets.Length; index++) Buffer.BlockCopy(values, offsets[index] - 52, result, index * 4, 4);
            var radians = BitConverter.ToSingle(BitConverter.GetBytes(0x3c8efa35), 0);
            foreach (var index in new[] { 3, 5, 10 }) WriteSingle(result, index * 4, RoundedMultiply(BitConverter.ToSingle(result, index * 4), radians));
            WriteSingle(result, 44, RoundedMultiply(BitConverter.ToSingle(result, 16), BitConverter.ToSingle(result, 16)));
            WriteSingle(result, 48, RoundedMultiply(BitConverter.ToSingle(result, 20), BitConverter.ToSingle(result, 20)));
            Buffer.BlockCopy(values, 124 - 52, result, 52, 64); result[54] = result[55] = 0; return result;
        }
        [MethodImpl(MethodImplOptions.NoInlining)] private static float RoundedMultiply(float left, float right) => left * right;
        private static void WriteSingle(byte[] bytes, int offset, float value) => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, 4);

        internal static byte[] EncodeValues(Data data)
        {
            Validate(data); using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream)) {
                writer.Write(data.Manager); writer.Write(data.DefaultMaterialPresent); writer.Write(data.DefaultMaterial); writer.Write(data.Values);
                writer.Write(data.LayerCollisionMatrix.Length); foreach (var value in data.LayerCollisionMatrix) writer.Write(value);
                writer.Flush(); return stream.ToArray();
            }
        }
        internal static byte[] EncodeDependency(Data data) { Validate(data); return DependencyValues(data.Box2D); }
        internal static byte[] EncodeS0(Data data)
        {
            Validate(data); using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream)) {
                writer.Write(data.Padding); writer.Write(data.Box2D[54]); writer.Write(data.Box2D[55]); writer.Write(data.MatrixOwnsStorage);
                writer.Write(data.MatrixCapacity - data.LayerCollisionMatrix.Length); writer.Flush(); return stream.ToArray();
            }
        }
        internal static void RequireEqual(Data expected, Data actual)
        {
            RequireComplete(expected); RequireComplete(actual);
            if (!EncodeValues(expected).SequenceEqual(EncodeValues(actual)) || !EncodeDependency(expected).SequenceEqual(EncodeDependency(actual))
                || !EncodeS0(expected).SequenceEqual(EncodeS0(actual))) throw Error("independent native settings readback differs");
        }

        internal static Live Canon(Live before, ResidentNativeGlobalObjects.CaptureResult globals, ResidentMutationScope scope,
            Func<object, string?> bind, Action admitted)
        {
            if (before == null || scope == null || bind == null || admitted == null) throw new ArgumentNullException();
            RequireCanonicalizable(before.Snapshot); before.RequireUnchanged(); scope.Require(); admitted();
            scope.Mutate(() => CanonStorage(before.Owner, before.Engine, before.Snapshot.LayerCollisionMatrix.Length, admitted));
            var after = Capture(globals, bind, admitted); RequireComplete(after.Snapshot);
            if (!EncodeValues(before.Snapshot).SequenceEqual(EncodeValues(after.Snapshot))
                || !EncodeDependency(before.Snapshot).SequenceEqual(EncodeDependency(after.Snapshot))) throw Error("common settings Canon changed retained D or Box2D values");
            return after;
        }
        private static void CanonStorage(IntPtr owner, IntPtr engine, int count, Action admitted)
        {
            admitted(); RequireOwner(owner, engine);
            M.Reserve(IntPtr.Add(owner, 264), count, 4, 4, admitted);
            foreach (var offset in PaddingOffsets) M.Write(IntPtr.Add(owner, offset), new byte[1]);
            M.Write(IntPtr.Add(engine, 0x1985982), new byte[2]); admitted();
        }

        internal static Plan Prepare(Data source, ResidentNativeGlobalObjects.CaptureResult globals, ResidentMutationScope scope,
            Func<string, NativeObject> resolve, Func<object, string?> bind, Action admitted)
        {
            if (scope == null || resolve == null || bind == null || admitted == null) throw new ArgumentNullException();
            // Target history can refer to a material absent from source D.
            // Preflight witnesses it without adding it to the source binding
            // manifest or demanding a source key for the value being replaced.
            RequireComplete(source); admitted(); var before = Capture(globals, _ => "", admitted);
            if (before.Snapshot.Manager != source.Manager) throw Error("target settings singleton identity differs");
            if (!before.Snapshot.MatrixOwnsStorage) throw Error("target layer matrix has unclassified borrowed storage aliases");
            NativeObject? material = null;
            if (source.DefaultMaterialPresent) {
                material = resolve(source.DefaultMaterial);
                if (!(material is PhysicsMaterial2D) || !ResidentObjectDirectory.IsLive(material) || bind(material) != source.DefaultMaterial)
                    throw Error("default material did not resolve to its actual target PhysicsMaterial2D");
                _ = M.Owner(material);
            }
            return new Plan(source.Copy(), globals, before, scope, material, bind, admitted);
        }
        internal sealed class Plan
        {
            private readonly Data source;
            private readonly ResidentNativeGlobalObjects.CaptureResult globals;
            private readonly Live before;
            private readonly ResidentMutationScope scope;
            private readonly NativeObject? material;
            private readonly Func<object, string?> bind;
            private readonly Action admitted;
            private bool applied;
            internal Plan(Data source, ResidentNativeGlobalObjects.CaptureResult globals, Live before, ResidentMutationScope scope,
                NativeObject? material, Func<object, string?> bind, Action admitted)
            { this.source = source; this.globals = globals; this.before = before; this.scope = scope; this.material = material; this.bind = bind; this.admitted = admitted; }
            internal void Apply()
            {
                if (applied) throw Error("settings plan was already applied");
                scope.Require(); admitted(); before.RequireUnchanged(); RequireOwner(before.Owner, before.Engine);
                scope.Mutate(() => {
                    var owner = before.Owner;
                    M.Write(IntPtr.Add(owner, 48), BitConverter.GetBytes(ReferenceEquals(material, null) ? 0 : material!.GetInstanceID()));
                    M.Write(IntPtr.Add(owner, 52), source.Values);
                    // uint elements are trivially destructible; clearing count
                    // before Reserve permits shrinking without copying old D.
                    M.Write(IntPtr.Add(owner, 280), new byte[8]);
                    M.Reserve(IntPtr.Add(owner, 264), source.LayerCollisionMatrix.Length, 4, 4, admitted);
                    var bytes = new byte[source.LayerCollisionMatrix.Length * 4];
                    Buffer.BlockCopy(source.LayerCollisionMatrix, 0, bytes, 0, bytes.Length);
                    M.Write(M.Ptr(IntPtr.Add(owner, 264)), bytes);
                    M.Write(IntPtr.Add(owner, 280), BitConverter.GetBytes((ulong)source.LayerCollisionMatrix.Length));
                    CanonStorage(owner, before.Engine, source.LayerCollisionMatrix.Length, admitted);
                    ((UpdateBox2D)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(before.Engine, 0xc08b80), typeof(UpdateBox2D)))(owner);
                    admitted();
                });
                applied = true; Verify();
            }
            internal void Verify()
            {
                if (!applied) throw Error("settings plan has not been applied");
                admitted(); RequireEqual(source, CaptureInternal(globals, bind, admitted, material).Snapshot); admitted();
            }
        }
        private static InvalidDataException Error(string message) => new InvalidDataException("Resident Physics2DSettings: " + message);
    }
}
