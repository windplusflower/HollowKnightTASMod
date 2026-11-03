using System;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentCameraCanvasState
    {
        internal sealed class CameraRow
        {
            public string Owner = "";
            public ResidentObjectDirectory.TypeRecord Type = new ResidentObjectDirectory.TypeRecord();
            public bool Execution;
            public byte[][] Scalars = Array.Empty<byte[]>();
            public string TargetTexture = "", ReplacementShader = "", ReplacementTag = "", Scene = "", OdsShader = "";
            public string[] OriginTextures = new string[8], TemporaryTextures = new string[4];
            public TransientTexture[] TransientTextures = Enumerable.Range(0, 3).Select(_ => new TransientTexture()).ToArray();
            public SurfaceBinding[] ColorSurfaces = Array.Empty<SurfaceBinding>();
            public SurfaceBinding DepthSurface = new SurfaceBinding();
            public bool RenderLoop, RenderContext, ShadowCache;
            public int ShadowEntries;
            public ShadowEntry[] Shadows = Array.Empty<ShadowEntry>();
            public ImageList[] Images = Array.Empty<ImageList>();
            public int CommandBufferFilters, EventBufferCapacity, EventMaskCapacity;
            public int[] EventCounts = Array.Empty<int>(), EventCapacities = Array.Empty<int>();
            public NativeBuffer[] EventMasks = Array.Empty<NativeBuffer>();
        }
        internal sealed class ShadowEntry
        {
            public int Key;
            public TransientTexture Texture = new TransientTexture();
            public byte[] Scalars = Array.Empty<byte>();
        }
        internal sealed class ImageList
        {
            public int Capacity;
            public ImageEntry[] Entries = Array.Empty<ImageEntry>();
        }
        internal sealed class ImageEntry
        {
            public string Owner = "";
            public int CallbackRva, Stage;
            public bool TransformsToLdr, CommandBuffers;
        }
        internal sealed class CameraRegistry
        {
            public string Current = "";
            public BindingList All = new BindingList(), Onscreen = new BindingList(), Offscreen = new BindingList(), Add = new BindingList(), Remove = new BindingList();
            public bool OnscreenSorted, OffscreenSorted, InsideRenderOrCull, Stack;
            public int Projectors;
            public string[] ExternalOwners = Array.Empty<string>();
        }
        private static readonly Range[] CameraRanges = {
            new Range(56, 2), new Range(64, 740), new Range(872, 4), new Range(952, 8),
            new Range(1008, 241), new Range(1252, 7), new Range(1260, 5), new Range(1268, 32),
            new Range(1304, 8), new Range(1320, 9), new Range(1456, 577),
            new Range(2036, 193), new Range(2232, 5), new Range(2240, 20), new Range(2272, 12)
        };
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetScene(IntPtr camera, int handle);
        private static IntPtr RenderManager()
        {
            Function<Unary>(0x5d66f0, "488b0521234001c3");
            var pointer = M.Ptr(IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, 0x19d8a18));
            return pointer != IntPtr.Zero ? pointer : throw Error("native RenderManager is absent");
        }
        private static IntPtr AllCameraStorage()
        {
            var pointer = M.Ptr(IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, 0x1960780));
            return pointer != IntPtr.Zero ? pointer : throw Error("native all-camera directory is absent");
        }
        private static CameraRow CaptureCamera(Reader reader, string key, Camera owner)
        {
            var native = M.Owner(owner); var bytes = reader.Read(native, 2288);
            var row = new CameraRow { Owner = key, Type = ResidentObjectDirectory.TypeRecord.Capture(owner.GetType()), Execution = reader.Directory.Ownership.IsExecution(owner),
                Scalars = CaptureRanges(bytes, CameraRanges), TargetTexture = reader.Id<RenderTexture>(M.I32(bytes, 804)),
                ColorSurfaces = Enumerable.Range(0, 8).Select(index => reader.Surface(BitConverter.ToInt64(bytes, 808 + index * 8))).ToArray(),
                DepthSurface = reader.Surface(BitConverter.ToInt64(bytes, 880)),
                OriginTextures = Enumerable.Range(0, 8).Select(index => reader.Pointer<RenderTexture>(M.Ptr(bytes, 888 + index * 8))).ToArray(),
                ReplacementShader = reader.Id<Shader>(M.I32(bytes, 960)), ReplacementTag = M.String(IntPtr.Add(native, 968)), Scene = reader.Scene(M.Ptr(bytes, 1312)),
                // Slot zero is the retained current target. The other three
                // slots are S0 cache retains and must not seed portable D roots.
                TemporaryTextures = new[] { reader.Pointer<RenderTexture>(M.Ptr(bytes, 1424)), "", "", "" },
                TransientTextures = Enumerable.Range(0, 3).Select(index => reader.Transient(M.Ptr(bytes, 1432 + index * 8), key + "/" + index)).ToArray(),
                OdsShader = reader.Pointer<Shader>(M.Ptr(bytes, 2264)), RenderLoop = M.Ptr(bytes, 1336) != IntPtr.Zero,
                ShadowCache = M.Ptr(bytes, 1344) != IntPtr.Zero };
            var buffers = Dynamic(reader, IntPtr.Add(native, 1352), 32); row.EventBufferCapacity = buffers.Capacity;
            var bufferPointer = M.Ptr(bytes, 1352); row.EventCounts = new int[buffers.Bytes.Length / 32]; row.EventCapacities = new int[row.EventCounts.Length];
            for (var index = 0; index < row.EventCounts.Length; index++) {
                var entries = Dynamic(reader, IntPtr.Add(bufferPointer, index * 32), 8);
                row.EventCounts[index] = entries.Bytes.Length / 8; row.EventCapacities[index] = entries.Capacity;
            }
            var masks = Dynamic(reader, IntPtr.Add(native, 1384), 32); row.EventMaskCapacity = masks.Capacity;
            row.EventMasks = Enumerable.Range(0, masks.Bytes.Length / 32).Select(index => Dynamic(reader, IntPtr.Add(M.Ptr(bytes, 1384), index * 32), 4)).ToArray();
            if (row.RenderLoop) {
                var loop = M.Ptr(bytes, 1336); var loopBytes = reader.Read(loop, 64); row.RenderContext = M.Ptr(loopBytes, 0) != IntPtr.Zero;
                row.CommandBufferFilters = M.I32(loopBytes, 56);
                row.Images = new[] { 8, 32 }.Select(offset => {
                    var vector = Vector(reader, IntPtr.Add(loop, offset), 24);
                    return new ImageList { Capacity = vector.Capacity, Entries = Enumerable.Range(0, vector.Bytes.Length / 24).Select(index => {
                        var at = index * 24;
                        return new ImageEntry { Owner = reader.Pointer<Component>(M.Ptr(vector.Bytes, at)), CallbackRva = Rva(M.Ptr(vector.Bytes, at + 8)),
                            TransformsToLdr = Bool(vector.Bytes[at + 16]), CommandBuffers = Bool(vector.Bytes[at + 17]), Stage = M.I32(vector.Bytes, at + 20) };
                    }).ToArray() };
                }).ToArray();
            }
            if (row.ShadowCache) {
                row.Shadows = TreeNodes(reader, M.Ptr(bytes, 1344), 392, true).Select(value => new ShadowEntry {
                    Key = M.I32(value, 32), Texture = reader.Transient(M.Ptr(value, 40), key + "/shadow/" + M.I32(value, 32)), Scalars = M.Slice(value, 48, 344)
                }).ToArray(); row.ShadowEntries = row.Shadows.Length;
            }
            return row;
        }
        private static CameraRegistry CaptureCameraRegistry(Reader reader)
        {
            var native = RenderManager(); var bytes = reader.Read(native, 200);
            BindingList Ids(int offset) => BindList(Dynamic(reader, IntPtr.Add(native, offset), 4), 4, (data, at) => reader.Id<Camera>(M.I32(data, at)));
            var all = BindList(Dynamic(reader, AllCameraStorage(), 8), 8, (data, at) => reader.Pointer<Camera>(M.Ptr(data, at)));
            var projectors = M.U64(bytes, 176); if (projectors > Maximum) throw Error("projector registry exceeds the native bound");
            return new CameraRegistry { Current = reader.Id<Camera>(M.I32(bytes, 0)), All = all, Onscreen = Ids(16), Offscreen = Ids(56), Add = Ids(96), Remove = Ids(128),
                OnscreenSorted = Bool(bytes[48]), OffscreenSorted = Bool(bytes[88]), InsideRenderOrCull = Bool(bytes[160]),
                Stack = M.Ptr(bytes, 8) != IntPtr.Zero, Projectors = (int)projectors,
                ExternalOwners = all.Owners.Where(key => !reader.Directory.NativeBindings.ContainsKey(key)).OrderBy(key => key, StringComparer.Ordinal).ToArray() };
        }
        private static void ValidateCamera(CameraRow row)
        {
            if (row == null || !Text(row.Owner, false) || row.Type == null || row.Type.Name != "UnityEngine.Camera") throw Error("Camera owner/type differs");
            row.Type.Validate(); ValidateRanges(row.Scalars, CameraRanges);
            foreach (var value in new[] { row.TargetTexture, row.ReplacementShader, row.ReplacementTag, row.Scene, row.OdsShader }) if (!Text(value)) throw Error("Camera binding differs");
            if (row.OriginTextures == null || row.OriginTextures.Length != 8 || row.TemporaryTextures == null || row.TemporaryTextures.Length != 4
                || row.OriginTextures.Concat(row.TemporaryTextures).Any(value => !Text(value)) || row.ColorSurfaces == null || row.ColorSurfaces.Length != 8
                || row.Images == null || row.Images.Length != (row.RenderLoop ? 2 : 0) || row.CommandBufferFilters < 0 || row.CommandBufferFilters > Maximum
                || row.Shadows == null || row.ShadowEntries < 0 || row.ShadowEntries > Maximum || row.ShadowEntries != row.Shadows.Length
                || !row.ShadowCache && row.ShadowEntries != 0 || !row.RenderLoop && (row.RenderContext || row.CommandBufferFilters != 0)
                || row.EventCounts == null || row.EventCapacities == null || row.EventMasks == null || row.EventCounts.Length != row.EventCapacities.Length
                || row.EventBufferCapacity < row.EventCounts.Length || row.EventBufferCapacity > Maximum || row.EventMaskCapacity < row.EventMasks.Length || row.EventMaskCapacity > Maximum)
                throw Error("Camera native domain shape differs");
            foreach (var surface in row.ColorSurfaces.Concat(new[] { row.DepthSurface })) ValidateSurface(surface);
            if (row.TransientTextures == null || row.TransientTextures.Length != 3 || row.TemporaryTextures.Skip(1).Any(value => value.Length != 0)) throw Error("Camera transient cache shape differs");
            foreach (var value in row.TransientTextures) ValidateTransient(value);
            for (var index = 0; index < row.Shadows.Length; index++) {
                var value = row.Shadows[index];
                if (value == null || value.Scalars == null || value.Scalars.Length != 344 || index != 0 && row.Shadows[index - 1].Key >= value.Key)
                    throw Error("Camera shadow cache shape/order differs");
                ValidateTransient(value.Texture);
            }
            for (var index = 0; index < row.EventCounts.Length; index++)
                if (row.EventCounts[index] < 0 || row.EventCapacities[index] < row.EventCounts[index] || row.EventCapacities[index] > Maximum) throw Error("Camera event buffer shape differs");
            foreach (var buffer in row.EventMasks) ValidateBuffer(buffer, 4);
            foreach (var images in row.Images) {
                if (images == null || images.Entries == null || images.Capacity < images.Entries.Length || images.Capacity > Maximum) throw Error("image-filter vector shape differs");
                foreach (var image in images.Entries)
                    if (image == null || !Text(image.Owner, false) || image.CallbackRva <= 0 || image.CallbackRva >= 0x4000000) throw Error("image-filter owner/callback binding differs");
            }
            if (row.Scalars[0].Any(value => value > 1) || M.I32(row.Scalars[2], 0) < 0 || M.I32(row.Scalars[2], 0) > 8 || row.Scalars[9].Any(value => value > 1))
                throw Error("Camera native boolean or target count differs");
        }
        private static void ValidateTransient(TransientTexture value)
        {
            if (value == null || !Text(value.Alias) || (value.Alias.Length == 0) != (value.Type == null) || value.Type != null && value.Type.Name != "UnityEngine.RenderTexture")
                throw Error("Camera transient cache type differs");
            value.Type?.Validate();
        }
        private static void ValidateCameraRegistry(Data data)
        {
            var value = data.CameraManager; if (value == null || !Text(value.Current) || value.Projectors < 0 || value.Projectors > Maximum || value.ExternalOwners == null) throw Error("Camera registry shape differs");
            foreach (var list in new[] { value.All, value.Onscreen, value.Offscreen, value.Add, value.Remove }) {
                ValidateList(list, false); if (list.Owners.Distinct(StringComparer.Ordinal).Count() != list.Owners.Length) throw Error("Camera registry repeats an owner");
            }
            if (value.ExternalOwners.Any(key => !Text(key, false)) || value.ExternalOwners.Distinct(StringComparer.Ordinal).Count() != value.ExternalOwners.Length)
                throw Error("Camera external registry owner differs");
            var owned = new System.Collections.Generic.HashSet<string>(data.Cameras.Select(row => row.Owner), StringComparer.Ordinal);
            foreach (var key in value.ExternalOwners) if (!owned.Add(key)) throw Error("Camera external owner overlaps the directory");
            if (!owned.SetEquals(value.All.Owners) || value.Onscreen.Owners.Concat(value.Offscreen.Owners).Concat(value.Add.Owners).Concat(value.Remove.Owners).Any(key => !owned.Contains(key))
                || value.Current.Length != 0 && !owned.Contains(value.Current)) throw Error("Camera registry has an omitted or unknown owner");
            if (value.Onscreen.Owners.Intersect(value.Offscreen.Owners, StringComparer.Ordinal).Any()) throw Error("Camera appears in both sorted render lists");
        }
        private static void ApplyCamera(CameraRow row, Camera owner, ResidentObjectDirectory directory, Func<string, NativeObject> resolve, Action admitted)
        {
            admitted(); var native = M.Owner(owner); var bytes = M.Read(native, 2288);
            if (row.RenderLoop != (M.Ptr(bytes, 1336) != IntPtr.Zero) || row.ShadowCache != (M.Ptr(bytes, 1344) != IntPtr.Zero)) throw Error("target Camera storage domain is absent");
            owner.RemoveAllCommandBuffers();
            // The real teardown releases target event-buffer retains; arrays are
            // then rebuilt with target allocator labels and source capacities.
            void Events(int offset, int length, int capacity, Func<int, NativeBuffer> values, int stride) {
                var top = IntPtr.Add(native, offset); var header = M.Read(top, 32); var oldCount = checked((int)M.U64(header, 16));
                var old = M.Ptr(header, 0);
                for (var index = 0; index < oldCount; index++) WriteDynamic(IntPtr.Add(old, index * 32), Array.Empty<byte>(), 0, stride, stride, admitted);
                WriteDynamic(top, new byte[length * 32], capacity, 32, 8, admitted);
                var target = M.Ptr(top); var label = M.Read(IntPtr.Add(top, 8), 4);
                for (var index = 0; index < length; index++) {
                    var inner = IntPtr.Add(target, index * 32); M.Write(IntPtr.Add(inner, 8), label); M.Write(IntPtr.Add(inner, 24), BitConverter.GetBytes(1UL));
                    var value = values(index); WriteDynamic(inner, value.Bytes, value.Capacity, stride, stride, admitted);
                }
            }
            Events(1352, row.EventCounts.Length, row.EventBufferCapacity, index => new NativeBuffer { Capacity = row.EventCapacities[index] }, 8);
            Events(1384, row.EventMasks.Length, row.EventMaskCapacity, index => row.EventMasks[index], 4);
            if (row.RenderLoop) {
                var loop = M.Ptr(bytes, 1336);
                for (var lane = 0; lane < 2; lane++) {
                    var source = row.Images[lane]; var values = new byte[source.Entries.Length * 24];
                    for (var index = 0; index < source.Entries.Length; index++) {
                        var image = source.Entries[index]; var at = index * 24;
                        System.Buffer.BlockCopy(BitConverter.GetBytes(Pointer(image.Owner, resolve).ToInt64()), 0, values, at, 8);
                        System.Buffer.BlockCopy(BitConverter.GetBytes(EnginePointer(image.CallbackRva).ToInt64()), 0, values, at + 8, 8);
                        values[at + 16] = image.TransformsToLdr ? (byte)1 : (byte)0; values[at + 17] = image.CommandBuffers ? (byte)1 : (byte)0;
                        System.Buffer.BlockCopy(BitConverter.GetBytes(image.Stage), 0, values, at + 20, 4);
                    }
                    WriteVector(IntPtr.Add(loop, 8 + lane * 24), values, source.Capacity, 24, admitted);
                }
                M.Write(IntPtr.Add(loop, 56), BitConverter.GetBytes(row.CommandBufferFilters));
            }
            M.Write(IntPtr.Add(native, 804), BitConverter.GetBytes(Id(row.TargetTexture, resolve)));
            M.Write(IntPtr.Add(native, 808), row.ColorSurfaces.SelectMany(value => BitConverter.GetBytes(SurfaceHandle(value, resolve))).ToArray());
            M.Write(IntPtr.Add(native, 880), BitConverter.GetBytes(SurfaceHandle(row.DepthSurface, resolve)));
            M.Write(IntPtr.Add(native, 888), row.OriginTextures.SelectMany(value => BitConverter.GetBytes(Pointer(value, resolve).ToInt64())).ToArray());
            M.Write(IntPtr.Add(native, 960), BitConverter.GetBytes(Id(row.ReplacementShader, resolve))); WriteString(IntPtr.Add(native, 968), row.ReplacementTag, admitted);
            if (row.Scene.Length == 0) M.Write(IntPtr.Add(native, 1312), BitConverter.GetBytes(0L));
            else Function<SetScene>(0x5e15a0, "48895c2408574883ec208bda488bf9e86cb5")(native, directory.SceneFor(row.Scene).handle);
            M.Write(IntPtr.Add(native, 1424), row.TemporaryTextures.SelectMany(value => BitConverter.GetBytes(Pointer(value, resolve).ToInt64())).ToArray());
            M.Write(IntPtr.Add(native, 2264), BitConverter.GetBytes(Pointer(row.OdsShader, resolve).ToInt64())); ApplyRanges(native, CameraRanges, row.Scalars); admitted();
        }
        private static void ApplyCameraRegistry(CameraRegistry source, Func<string, NativeObject> resolve, Action admitted)
        {
            var manager = RenderManager();
            void Ids(int offset, BindingList list) => WriteDynamic(IntPtr.Add(manager, offset), list.Owners.SelectMany(key => BitConverter.GetBytes(Id(key, resolve))).ToArray(), list.Capacity, 4, 4, admitted);
            Ids(16, source.Onscreen); Ids(56, source.Offscreen); Ids(96, source.Add); Ids(128, source.Remove);
            WriteDynamic(AllCameraStorage(), source.All.Owners.SelectMany(key => BitConverter.GetBytes(Pointer(key, resolve).ToInt64())).ToArray(), source.All.Capacity, 8, 8, admitted);
            M.Write(manager, BitConverter.GetBytes(Id(source.Current, resolve))); M.Write(IntPtr.Add(manager, 48), new[] { source.OnscreenSorted ? (byte)1 : (byte)0 });
            M.Write(IntPtr.Add(manager, 88), new[] { source.OffscreenSorted ? (byte)1 : (byte)0 });
        }
        private static void CanonCamera(Camera owner, Action admitted)
        {
            admitted(); var native = M.Owner(owner); var flags = M.Read(IntPtr.Add(native, 1320), 4);
            if (flags.Any(value => value != 0)) throw Error("Camera Canon would interrupt active rendering/culling");
            var loop = M.Ptr(IntPtr.Add(native, 1336));
            if (loop != IntPtr.Zero) Function<Unary>(0x603cb0, "40534883ec20488bd9e8120c1d00488b0b4885c9740c")(loop);
            for (var slot = 0; slot < 3; slot++) {
                var storage = IntPtr.Add(native, 1432 + slot * 8); var texture = M.Ptr(storage);
                if (texture == IntPtr.Zero) continue;
                var pool = RenderPoolPointer(); if (pool == IntPtr.Zero) throw Error("Camera transient texture has no native pool");
                Function<Binary>(0x664430, "4885d20f84a20100004c8bdc49897320574881ec90000000")(pool, texture);
                M.Write(storage, BitConverter.GetBytes(0L)); admitted();
            }
            var shadow = M.Ptr(IntPtr.Add(native, 1344));
            if (shadow != IntPtr.Zero) Function<Unary>(0x59f7f0, "48895c24084889742410574883ec20488b39488bf1488b1f")(shadow);
            admitted();
        }
    }
}
