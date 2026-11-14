using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentRendererState
    {
        private const int MaximumBufferBytes = 32 * 1024 * 1024;
        internal sealed class SpriteRegistryState
        {
            public int Capacity;
            public string[] Owners = Array.Empty<string>(), ExternalOwners = Array.Empty<string>();
        }
        internal sealed class SpriteCacheState
        {
            public string Texture = "", AlphaTexture = "";
            public SpriteSecondaryTexture[] Secondary = Array.Empty<SpriteSecondaryTexture>();
            public int SecondaryCapacity;
            public byte[] Scalars = Array.Empty<byte>();
            public bool UvPending, PreparePending;
            public bool VertexFormatCleared;
            public bool[] GeometryBuffers = new bool[5];
            public byte[] VertexFormat = Array.Empty<byte>();
            public SpriteSharedMesh? Shared;
        }
        internal sealed class SpriteSecondaryTexture
        {
            public string Name = "", Texture = "";
        }
        internal sealed class SpriteSharedMesh
        {
            public int References, IndexFormat;
            public byte[] VertexState = Array.Empty<byte>(), Vertices = Array.Empty<byte>();
            public NativeBuffer[] Buffers = Array.Empty<NativeBuffer>();
            public bool BlendShapes;
        }
        internal sealed class NativeBuffer
        {
            public int Capacity;
            public byte[] Bytes = Array.Empty<byte>();
        }
        private static readonly int[] SpriteSharedOffsets = { 160, 200, 232, 264, 296, 328, 360 };
        private static readonly int[] SpriteSharedStrides = { 1, 56, 64, 4, 16, 32, 4 };
        private static readonly int[] SpriteSharedAlignments = { 1, 4, 16, 4, 16, 16, 4 };
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr NativeFactory();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr NativeFormat(IntPtr manager, IntPtr channels);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AssignString(IntPtr storage, IntPtr bytes, UIntPtr length);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool FenceDone(IntPtr fence);

        private static SpriteCacheState CaptureSpriteCache(IntPtr pointer, SourceBindings references)
        {
            var bytes = M.Read(pointer, 160); var result = new SpriteCacheState {
                Texture = references.Id<Texture2D>(M.I32(bytes, 0)), AlphaTexture = references.Id<Texture2D>(M.I32(bytes, 4)),
                Scalars = M.Slice(bytes, 40, 56), UvPending = Bool(bytes[152]), PreparePending = Bool(bytes[153]),
                VertexFormatCleared = M.Ptr(bytes, 104) == IntPtr.Zero,
                GeometryBuffers = Enumerable.Range(0, 5).Select(index => M.Ptr(bytes, 112 + index * 8) != IntPtr.Zero).ToArray(),
                // The constructor leaves m_MeshVertexFormat uninitialized.
                // Native PrepareRenderingDataIfNeeded writes it before clearing
                // PreparePending. Pending cache bytes are never dereferenced.
                VertexFormat = bytes[153] != 0 ? Array.Empty<byte>() : M.Read(M.Ptr(bytes, 104), 56) };
            var secondary = ReadArray(pointer, 8, 48, out result.SecondaryCapacity); var secondaryPointer = M.Ptr(bytes, 8);
            result.Secondary = Enumerable.Range(0, secondary.Length / 48).Select(index => new SpriteSecondaryTexture {
                Name = M.String(IntPtr.Add(secondaryPointer, index * 48)), Texture = references.Id<Texture2D>(M.I32(secondary, index * 48 + 40)) }).ToArray();
            var sharedPointer = M.Ptr(bytes, 96);
            if (sharedPointer != IntPtr.Zero) {
                var shared = M.Read(sharedPointer, 400); var length = M.U64(shared, 128);
                if (length > MaximumBufferBytes) throw Error("Sprite cached vertex buffer exceeds bounds");
                result.Shared = new SpriteSharedMesh { References = M.I32(shared, 4), IndexFormat = M.I32(shared, 192),
                    VertexState = VertexState(shared), Vertices = M.Read(M.Ptr(shared, 152), (int)length), BlendShapes = M.Ptr(shared, 392) != IntPtr.Zero,
                    Buffers = Enumerable.Range(0, SpriteSharedOffsets.Length).Select(index => {
                        var data = ReadArray(sharedPointer, SpriteSharedOffsets[index], SpriteSharedStrides[index], out var capacity);
                        return new NativeBuffer { Capacity = capacity, Bytes = data };
                    }).ToArray() };
                if (!M.Read(sharedPointer, 400).SequenceEqual(shared)) throw Error("Sprite shared mesh changed during capture");
            }
            if (!M.Read(pointer, 160).SequenceEqual(bytes)) throw Error("Sprite render data changed during capture"); ValidateSpriteCache(result); return result;
        }
        private static byte[] VertexState(byte[] shared)
        {
            var state = M.Slice(shared, 8, 136);
            // These two ranges are ABI alignment padding, not fields or live
            // cache values. They can contain uninitialized allocator bytes.
            Array.Clear(state, 116, 4); Array.Clear(state, 131, 5); return state;
        }
        private static SpriteRegistryState CaptureSpriteRegistry(ResidentObjectDirectory directory, Func<object, string?> bind)
        {
            var root = IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, 0x1965470);
            var header = M.Read(root, 32); var contents = ReadArray(root, 0, 8, out var capacity);
            var owners = new Dictionary<IntPtr, string>();
            foreach (var pair in directory.NativeBindings.Where(pair => pair.Value is SpriteRenderer)) {
                var pointer = M.Ptr(IntPtr.Add(M.Owner(pair.Value), 584)); if (pointer == IntPtr.Zero) continue;
                if (owners.ContainsKey(pointer)) throw Error("Sprite cache has multiple resident owners");
                owners.Add(pointer, pair.Key);
            }
            var seen = new HashSet<IntPtr>(); var list = new List<string>();
            for (var index = 0; index < contents.Length / 8; index++) {
                var pointer = M.Ptr(contents, index * 8);
                if (pointer == IntPtr.Zero || !seen.Add(pointer)) throw Error("Sprite cache registry contains an absent or duplicate cache");
                if (owners.TryGetValue(pointer, out var key)) list.Add(key);
            }
            if (list.Count != owners.Count) throw Error("Sprite cache registry omits a resident owner");
            if (!M.Read(root, 32).SequenceEqual(header) || !M.Read(M.Ptr(header, 0), contents.Length).SequenceEqual(contents)) throw Error("Sprite cache registry changed during capture");
            // Foreign prefab cache allocations are process-local dependencies.
            return new SpriteRegistryState { Capacity = list.Count, Owners = list.ToArray() };
        }
        private static void ValidateSpriteRegistry(Data data)
        {
            var registry = data.Sprites;
            if (registry.Owners == null || registry.ExternalOwners == null || registry.Capacity < registry.Owners.Length || registry.Capacity > MaximumSlots
                || registry.Owners.Any(value => !Text(value, false)) || registry.ExternalOwners.Any(value => !Text(value, false))
                || registry.Owners.Distinct(StringComparer.Ordinal).Count() != registry.Owners.Length
                || registry.ExternalOwners.Distinct(StringComparer.Ordinal).Count() != registry.ExternalOwners.Length) throw Error("Sprite global registry shape differs");
            var expected = new HashSet<string>(data.Rows.Where(row => row.Kind == "SpriteRenderer" && row.SpriteCache != null).Select(row => row.Owner), StringComparer.Ordinal);
            foreach (var key in registry.ExternalOwners) if (!expected.Add(key)) throw Error("Sprite asset owner overlaps the resident directory");
            if (!expected.SetEquals(registry.Owners)) throw Error("Sprite global registry omits a render-data owner");
        }
        private static void ValidateSpriteCache(SpriteCacheState cache)
        {
            if (!Text(cache.Texture) || !Text(cache.AlphaTexture) || cache.Secondary == null || cache.SecondaryCapacity < cache.Secondary.Length
                || cache.SecondaryCapacity > MaximumSlots || cache.Scalars == null || cache.Scalars.Length != 56 || cache.GeometryBuffers == null || cache.GeometryBuffers.Length != 5
                || cache.VertexFormat == null || cache.VertexFormat.Length != 0 && cache.VertexFormat.Length != 56) throw Error("Sprite CPU cache shape differs");
            if (cache.PreparePending ? cache.VertexFormat.Length != 0 : cache.VertexFormat.Length != 56) throw Error("Sprite vertex-format cache validity differs");
            if (!cache.PreparePending && cache.VertexFormatCleared) throw Error("prepared Sprite cache has no native vertex-format binding");
            foreach (var texture in cache.Secondary)
                if (texture == null || !Text(texture.Name) || texture.Name.IndexOf('\0') >= 0 || Encoding.UTF8.GetByteCount(texture.Name) > 16384 || !Text(texture.Texture)) throw Error("Sprite secondary texture differs");
            var shared = cache.Shared; if (shared == null) return;
            if (shared.References <= 0 || shared.References > MaximumSlots || shared.IndexFormat < 0 || shared.IndexFormat > 1 || shared.VertexState == null || shared.VertexState.Length != 136
                || shared.Vertices == null || shared.Vertices.Length > MaximumBufferBytes || shared.Buffers == null || shared.Buffers.Length != SpriteSharedOffsets.Length
                || M.U64(shared.VertexState, 120) != (ulong)shared.Vertices.Length) throw Error("Sprite shared geometry shape differs");
            if (shared.VertexState.Skip(116).Take(4).Concat(shared.VertexState.Skip(131).Take(5)).Any(value => value != 0)) throw Error("Sprite vertex state contains ABI padding");
            for (var index = 0; index < shared.Buffers.Length; index++) {
                var buffer = shared.Buffers[index]; if (buffer == null) throw Error("Sprite shared geometry buffer is absent");
                Bytes(buffer.Bytes, buffer.Capacity, SpriteSharedStrides[index]);
            }
            Bool(shared.VertexState[128]); Bool(shared.VertexState[129]); Bool(shared.VertexState[130]);
            var vertices = M.U32(shared.VertexState, 112);
            for (var stream = 0; stream < 4; stream++) {
                var offset = M.U32(shared.VertexState, 60 + stream * 12); var stride = M.U32(shared.VertexState, 64 + stream * 12);
                if ((ulong)offset + (ulong)stride * vertices > (ulong)shared.Vertices.Length) throw Error("Sprite vertex stream exceeds its raw buffer");
            }
        }
        private static IEnumerable<string> SpriteCacheGaps(SpriteCacheState cache)
        {
            if (cache.GeometryBuffers.Any(value => value)) yield return "sprite-owned-geometry-gpu-buffers";
            if (!cache.PreparePending || !cache.VertexFormatCleared) yield return "sprite-render-cache-requires-common-canon";
            if (cache.Shared != null) {
                if (cache.Shared.References != 1) yield return "sprite-shared-geometry-aliases";
                if (cache.Shared.BlendShapes) yield return "sprite-blend-shape-cache";
                if (cache.Shared.VertexState[128] != 0 || cache.Shared.VertexState[129] != 0) yield return "sprite-external-compute-buffer-binding";
            }
        }
        private static void PreflightSpriteCache(SpriteCacheState source, SpriteCacheState? target, Func<string, NativeObject> resolve)
        {
            ValidateSpriteCache(source);
            if (target == null || (source.Shared == null) != (target.Shared == null)) throw Error("Sprite render-data storage presence differs");
            foreach (var binding in source.Secondary.Select(value => value.Texture).Concat(new[] { source.Texture, source.AlphaTexture }))
                if (binding.Length != 0) _ = ResidentNativeAssetReference.ResolveTyped(binding,typeof(Texture2D),resolve);
        }
        private static void PreflightSpriteRegistry(SpriteRegistryState source, SpriteRegistryState target, ResidentObjectDirectory directory, Func<string, NativeObject> resolve)
        {
            foreach (var key in source.Owners)
                if (M.Ptr(IntPtr.Add(ResidentNativeAssetReference.Resolve(key,ResidentLoadedAssetIndex.SpriteRendererClass,typeof(SpriteRenderer),resolve),584)) == IntPtr.Zero) throw Error("Sprite registry target cache is absent");
            if (!new HashSet<string>(source.Owners, StringComparer.Ordinal).SetEquals(target.Owners)) throw Error("source/target complete Sprite registry owner sets differ");
        }
        private static void WriteArray(IntPtr owner, int offset, byte[] bytes, int capacity, int stride, int alignment, Action admitted)
        {
            admitted(); var header = IntPtr.Add(owner, offset);
            M.Write(IntPtr.Add(header, 16), BitConverter.GetBytes(0UL)); M.Reserve(header, capacity, stride, alignment, admitted);
            if (M.U64(M.Read(header, 32), 24) >> 1 != (ulong)capacity) throw Error("Sprite native array capacity did not read back");
            M.Write(M.Ptr(header), bytes); M.Write(IntPtr.Add(header, 16), BitConverter.GetBytes((ulong)(bytes.Length / stride)));
        }
        private static void ApplySpriteRegistry(SpriteRegistryState source, Func<string, NativeObject> resolve, Action admitted)
        {
            var owned = source.Owners.Select(key => M.Ptr(IntPtr.Add(ResidentNativeAssetReference.Resolve(key,ResidentLoadedAssetIndex.SpriteRendererClass,typeof(SpriteRenderer),resolve),584))).ToArray();
            if (owned.Any(pointer => pointer == IntPtr.Zero)) throw Error("restored Sprite cache is absent");
            var resident = new HashSet<IntPtr>(owned);
            var root = IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, 0x1965470);
            var current = ReadArray(root, 0, 8, out var capacity);
            var foreign = Enumerable.Range(0, current.Length / 8).Select(index => M.Ptr(current, index * 8)).Where(pointer => !resident.Contains(pointer));
            var pointers = foreign.Concat(owned).SelectMany(pointer => BitConverter.GetBytes(pointer.ToInt64())).ToArray();
            WriteArray(root, 0, pointers, Math.Max(capacity, pointers.Length / 8), 8, 8, admitted);
        }
        private static void ApplySecondarySheet(IntPtr owner, ResidentShaderPropertySheet.Data? source, Func<string, NativeObject> resolve, Func<object, string?> bind, Action admitted)
        {
            admitted(); var create = Function<NativeFactory>(0x661700, "4883ec388b15723102014c8d0df348f60041b808000000c7");
            var destroy = Function<NativeUnary>(0x661910, "4883ec284885c97447b8fffffffff00fc1410c83f8017538");
            var old = M.Ptr(IntPtr.Add(owner, 520)); var replacement = IntPtr.Zero;
            if (source != null) {
                using var prepared = ResidentShaderPropertySheet.Prepare(source, resolve, bind, admitted);
                replacement = create(); if (replacement == IntPtr.Zero) throw Error("Sprite secondary sheet allocation failed");
                try { prepared.CopyIntoUniqueSheet(replacement); }
                catch { destroy(replacement); throw; }
            }
            M.Write(IntPtr.Add(owner, 520), BitConverter.GetBytes(replacement.ToInt64())); if (old != IntPtr.Zero) destroy(old); admitted();
        }
        private static void ApplySpriteCache(IntPtr pointer, SpriteCacheState source, Func<string, NativeObject> resolve, Action admitted)
        {
            admitted(); ValidateSpriteCache(source); if (SpriteCacheGaps(source).Any()) throw Error("Sprite cache requires unsupported native resources");
            var before = M.Read(pointer, 160);
            int Id(string key) => ResidentNativeAssetReference.ResolveId(key,typeof(Texture2D),resolve);
            M.Write(pointer, BitConverter.GetBytes(Id(source.Texture))); M.Write(IntPtr.Add(pointer, 4), BitConverter.GetBytes(Id(source.AlphaTexture)));
            var oldSecondary = ReadArray(pointer, 8, 48, out _); var oldPointer = M.Ptr(before, 8);
            var deallocate = Function<NativeUnary>(0x8edc0, "488b014885c0741f488379080076188b51204c8d052b7253");
            for (var index = 0; index < oldSecondary.Length / 48; index++) deallocate(IntPtr.Add(oldPointer, index * 48));
            WriteArray(pointer, 8, new byte[source.Secondary.Length * 48], source.SecondaryCapacity, 48, 8, admitted);
            var secondary = M.Ptr(IntPtr.Add(pointer, 8)); var label = M.Read(IntPtr.Add(pointer, 16), 4);
            var assign = Function<AssignString>(0x8ece0, "48895c241048896c24184889742420574883ec20488bf149");
            for (var index = 0; index < source.Secondary.Length; index++) {
                admitted(); var storage = IntPtr.Add(secondary, index * 48); M.Write(IntPtr.Add(storage, 32), label);
                var bytes = new UTF8Encoding(false, true).GetBytes(source.Secondary[index].Name); var text = Marshal.AllocHGlobal(bytes.Length + 1);
                try { M.Write(text, bytes.Concat(new byte[1]).ToArray()); assign(storage, text, new UIntPtr((uint)bytes.Length)); }
                finally { Marshal.FreeHGlobal(text); }
                M.Write(IntPtr.Add(storage, 40), BitConverter.GetBytes(Id(source.Secondary[index].Texture)));
            }
            M.Write(IntPtr.Add(pointer, 40), source.Scalars);
            M.Write(IntPtr.Add(pointer, 152), new[] { source.UvPending ? (byte)1 : (byte)0, source.PreparePending ? (byte)1 : (byte)0 });
            if (source.Shared != null) {
                var shared = M.Ptr(before, 96); var sharedBefore = M.Read(shared, 400);
                if (M.I32(sharedBefore, 4) != 1) throw Error("Sprite target geometry is shared");
                var temporary = Marshal.AllocHGlobal(32);
                try {
                    var header = new byte[32]; Buffer.BlockCopy(sharedBefore, 152, header, 0, 8); Buffer.BlockCopy(sharedBefore, 144, header, 8, 4);
                    M.Write(temporary, header); M.Reserve(temporary, source.Shared.Vertices.Length, 1, 16, admitted);
                    var vertices = M.Ptr(temporary); M.Write(vertices, source.Shared.Vertices);
                    M.Write(IntPtr.Add(shared, 152), BitConverter.GetBytes(vertices.ToInt64()));
                } finally { Marshal.FreeHGlobal(temporary); }
                M.Write(IntPtr.Add(shared, 8), source.Shared.VertexState); M.Write(IntPtr.Add(shared, 192), BitConverter.GetBytes(source.Shared.IndexFormat));
                for (var index = 0; index < source.Shared.Buffers.Length; index++)
                    WriteArray(shared, SpriteSharedOffsets[index], source.Shared.Buffers[index].Bytes, source.Shared.Buffers[index].Capacity,
                        SpriteSharedStrides[index], SpriteSharedAlignments[index], admitted);
            }
            var format = IntPtr.Zero;
            if (source.VertexFormat.Length != 0) {
                var manager = Function<NativeFactory>(0x6d8d00, "488b05f9c52801c3")();
                if (manager == IntPtr.Zero) throw Error("Sprite vertex-format manager is absent");
                var data = Marshal.AllocHGlobal(56);
                try { M.Write(data, source.VertexFormat); format = Function<NativeFormat>(0x6d8a00, "488bc44889581857415641574881ec300200004c8b31488b")(manager, data); }
                finally { Marshal.FreeHGlobal(data); }
                if (format == IntPtr.Zero || !M.Read(format, 56).SequenceEqual(source.VertexFormat)) throw Error("Sprite target vertex format differs");
            }
            M.Write(IntPtr.Add(pointer, 104), BitConverter.GetBytes(format.ToInt64())); admitted();
        }

        // All modes run this same cache Canon before final D capture. Semantic
        // CPU geometry, texture names/bindings, colors/bounds and drawing state
        // are retained. Secondary-sheet sharing and GPU allocation are S0;
        // renderer/asset registration and target format bindings are Dependency.
        internal static Action Canon(ResidentObjectDirectory directory, ResidentMutationScope scope,
            Func<string, NativeObject> resolve, Func<object, string?> bind, Action admitted)
        {
            if (scope == null || admitted == null) throw new ArgumentNullException(); scope.Require(); admitted();
            var source = Capture(directory, bind); RequireCanonicalizableCoverage(source); var expected = Copy(source);
            var done = Function<FenceDone>(0x725ac0, "40534883ec30488bd9e86217000084c0742648833b007420");
            var sync = Function<NativeUnary>(0xe5430, "40534883ec2048833900488bd9741433d2e8ca006400488b");
            var free = Function<NativeUnary>(0x6f0770, "48895c240848896c24104889742418574883ec20488b1933");
            foreach (var row in source.Rows.Where(row => row.Kind == "SpriteRenderer" && !row.Execution)) {
                var native = M.Owner(directory.Resolve(row.Owner));
                if (!scope.Owns((Component)directory.Resolve(row.Owner))) throw Error("Sprite Canon owner is outside mutation scope");
                if (!done(IntPtr.Add(native, 568))) throw Error("Sprite tiling job is still running at the admitted boundary");
                if (row.SecondaryProperties != null) ResidentShaderPropertySheet.RequireCompleteCoverage(row.SecondaryProperties);
            }
            try {
                CanonParticleDrawCaches(expected, directory, scope, admitted);
                foreach (var row in expected.Rows.Where(row => !row.Execution && row.Kind != "MeshFilter")) {
                    var old = M.U32(row.Scalars[0],232); if ((old & ~NamedDrawingBits) == 0) continue;
                    scope.Require(); admitted(); var owner = (Component)directory.Resolve(row.Owner);
                    if (!scope.Owns(owner)) throw Error("renderer bitfield Canon is outside mutation scope");
                    var field = IntPtr.Add(M.Owner(owner),64+232);
                    if (M.U32(M.Read(field,4),0) != old) throw Error("drawing state changed before common Canon");
                    var normalized = BitConverter.GetBytes(old & NamedDrawingBits);
                    M.Write(field,normalized); Buffer.BlockCopy(normalized,0,row.Scalars[0],232,4);
                }
                foreach (var row in expected.Rows.Where(row => row.Kind == "SpriteRenderer" && !row.Execution)) {
                    scope.Require(); admitted(); var native = M.Owner(directory.Resolve(row.Owner));
                    sync(IntPtr.Add(native, 568)); row.SpriteTilingFence = false;
                    ApplySecondarySheet(native, row.SecondaryProperties, resolve, bind, admitted);
                    row.SecondaryReferenceCount = row.SecondaryProperties == null ? 0 : 1;
                    if (row.SpriteCache != null) {
                        var cache = M.Ptr(IntPtr.Add(native, 584)); free(IntPtr.Add(cache, 112));
                        M.Write(IntPtr.Add(cache, 153), new byte[] { 1 }); M.Write(IntPtr.Add(cache, 104), BitConverter.GetBytes(0L));
                        row.SpriteCache.PreparePending = true; row.SpriteCache.VertexFormatCleared = true; row.SpriteCache.VertexFormat = Array.Empty<byte>();
                        row.SpriteCache.GeometryBuffers = new bool[5];
                    }
                }
                expected.CoverageGaps = RequiredCoverageGaps(expected);
                Action verify = () => { scope.Require(); admitted(); var actual = Capture(directory, bind); RequireCanonicalState(actual); RequireEqual(expected, actual); admitted(); };
                verify(); return verify;
            } catch (Exception error) { scope.Fault(error); throw; }
        }
    }
}
