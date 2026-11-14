using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentNativeAssetState
    {
        internal sealed class RenderTargetState
        {
            public RenderTextureDescriptor Descriptor;
            public bool Created;
            public int Filter, Aniso, WrapU, WrapV, WrapW;
            public float Bias;
            public byte[][] Mips = Array.Empty<byte[]>();
            public byte[] Depth = Array.Empty<byte>();
            internal RenderTargetState Copy() { var copy = (RenderTargetState)MemberwiseClone(); copy.Mips = Mips.Select(x => (byte[])x.Clone()).ToArray(); copy.Depth = (byte[])Depth.Clone(); return copy; }
        }
        internal sealed class SpriteState
        {
            public string Texture = "";
            public Rect Rect;
            public Vector2 Pivot;
            public Vector4 Border;
            public float PixelsPerUnit;
            public Vector2[] Vertices = Array.Empty<Vector2>();
            public ushort[] Triangles = Array.Empty<ushort>();
            public Vector2[][] Physics = Array.Empty<Vector2[]>();
            internal SpriteState Copy() => new SpriteState { Texture = Texture, Rect = Rect, Pivot = Pivot, Border = Border,
                PixelsPerUnit = PixelsPerUnit, Vertices = (Vector2[])Vertices.Clone(), Triangles = (ushort[])Triangles.Clone(),
                Physics = Physics.Select(x => (Vector2[])x.Clone()).ToArray() };
        }
        private static RenderTargetState CaptureRenderTarget(RenderTexture target)
        {
            Modding.Logger.Log($"[HKTAS-RT-T] begin ({target.name}): {ResidentTransformState.DebugSummary()}");
            var state = new RenderTargetState { Descriptor = target.descriptor, Created = target.IsCreated(), Filter = (int)target.filterMode,
                Aniso = target.anisoLevel, WrapU = (int)target.wrapModeU, WrapV = (int)target.wrapModeV, WrapW = (int)target.wrapModeW, Bias = target.mipMapBias };
            Modding.Logger.Log($"[HKTAS-RT-T] after props ({target.name}): {ResidentTransformState.DebugSummary()}");
            ValidateRenderTarget(state, false);
            if (state.Created) state.Mips = ReadGpuMips(target, target.mipmapCount);
            Modding.Logger.Log($"[HKTAS-RT-T] after ReadGpuMips ({target.name}): {ResidentTransformState.DebugSummary()}");
            if (state.Created && state.Descriptor.depthBufferBits != 0) state.Depth = ResidentDepthImage.Transfer(target);
            Modding.Logger.Log($"[HKTAS-RT-T] after Depth ({target.name}): {ResidentTransformState.DebugSummary()}");
            ValidateRenderTarget(state); return state;
        }
        internal static void ValidateRenderTarget(RenderTargetState state, bool requirePixels = true)
        {
            if (state == null || state.Descriptor.width < 1 || state.Descriptor.height < 1 || state.Descriptor.width > 32768 || state.Descriptor.height > 32768
                || state.Mips == null || state.Depth == null || state.Mips.Length > 32 || state.Mips.Any(x => x == null)
                || state.Mips.Sum(x => (long)x.Length) + state.Depth.Length > MaximumBytes)
                throw Error("render target descriptor or image bounds differ");
            if (state.Created && (state.Descriptor.dimension != TextureDimension.Tex2D || state.Descriptor.volumeDepth != 1
                || state.Descriptor.msaaSamples != 1 || state.Descriptor.memoryless != RenderTextureMemoryless.None))
                throw Error("live render target requires a multisample or layered image transfer adapter");
            if (requirePixels && state.Created != (state.Mips.Length != 0)) throw Error("render target allocation and image coverage differ");
            if (requirePixels && state.Created && state.Descriptor.depthBufferBits != 0)
                ResidentDepthImage.Validate(state.Depth, state.Descriptor.width, state.Descriptor.height);
            else if (state.Depth.Length != 0) throw Error("render target has an unexpected depth image");
        }
        private static byte[][] ReadGpuMips(Texture texture, int count)
        {
            if (count < 1 || count > 32 || !SystemInfo.supportsAsyncGPUReadback) throw Error("GPU image readback is unavailable");
            var result = new byte[count][]; long bytes = 0;
            for (var mip = 0; mip < count; mip++) {
                var request = AsyncGPUReadback.Request(texture, mip);
                request.WaitForCompletion();
                if (request.hasError) throw Error("GPU image readback failed at mip " + mip);
                var data = request.GetData<byte>(); RequireBytes(bytes += data.Length); result[mip] = data.ToArray();
            }
            return result;
        }
        internal static void ApplyRenderTarget(Snapshot source, RenderTexture target)
        {
            var state = source.RenderTarget!; ValidateRenderTarget(state);
            if (target.IsCreated()) target.Release();
            target.descriptor = state.Descriptor;
            target.filterMode = (FilterMode)state.Filter; target.anisoLevel = state.Aniso; target.mipMapBias = state.Bias;
            target.wrapModeU = (TextureWrapMode)state.WrapU; target.wrapModeV = (TextureWrapMode)state.WrapV; target.wrapModeW = (TextureWrapMode)state.WrapW;
            if (state.Created) {
                if (!target.Create()) throw Error("render target allocation failed");
                var image = new Texture2D(state.Descriptor.width, state.Descriptor.height, state.Descriptor.graphicsFormat,
                    state.Mips.Length, state.Mips.Length > 1 ? TextureCreationFlags.MipChain : TextureCreationFlags.None);
                try {
                    for (var mip = 0; mip < state.Mips.Length; mip++) image.SetPixelData(state.Mips[mip], mip);
                    image.Apply(false, false);
                    for (var mip = 0; mip < state.Mips.Length; mip++) Graphics.CopyTexture(image, 0, mip, target, 0, mip);
                } finally { NativeObject.DestroyImmediate(image); }
                if (state.Depth.Length != 0) ResidentDepthImage.Transfer(target, state.Depth);
            }
            target.name = source.Name; target.hideFlags = (HideFlags)source.HideFlags;
        }
        internal static void RequireTextureComplete(TextureState state)
        {
            ResidentAssetPipeline.ValidateTexture(state);
            if (state.Native == null || TextureNativeCoverageGaps(state).Length != 0
                || state.GpuMips == null || state.GpuMips.Length > 32 || state.GpuMips.Any(x => x == null || x.Length == 0)
                || state.Native.TextureIdPresent != (state.GpuMips.Length != 0)
                || state.GpuMips.Length != 0 && state.GpuMips.Length != state.MipmapCount
                || !state.CpuDataPresent && state.GpuMips.Length == 0
                || state.CpuDataPresent && !state.Readable || state.Streaming || state.VirtualTextureOnly)
                throw Error("texture image coverage or active streaming ownership differs");
            RequireBytes(state.GpuMips.Sum(x => (long)x.Length) + state.CpuBytes.Length);
        }
        internal static void ApplyTexture(Snapshot source, Texture2D target, Func<bool> admitted)
        {
            var state = source.Texture!; RequireTextureComplete(state);
            if (!admitted()) throw Error("texture restore lost admission");
            if (state.CpuDataPresent) PrepareTextureImageBuffers(source, target, admitted).Apply();
            else if (target.isReadable) target.Apply(false, true);
            if (target.width != state.Width || target.height != state.Height || (int)target.format != state.Format || target.mipmapCount != state.MipmapCount) {
                Modding.Logger.Log($"[HKTAS-TEX] resizing '{source.Name}' {target.width}x{target.height} fmt={target.format} mips={target.mipmapCount} -> {state.Width}x{state.Height} fmt={(TextureFormat)state.Format} mips={state.MipmapCount} readable={target.isReadable}");
                target.Resize(state.Width, state.Height, (TextureFormat)state.Format, state.MipmapCount > 1);
            }
            if (state.GpuMips.Length != 0) {
                var graphicsFormat = (GraphicsFormat)state.GraphicsFormat;
                var image = SystemInfo.IsFormatSupported(graphicsFormat, FormatUsage.Sample)
                    ? new Texture2D(state.Width, state.Height, graphicsFormat, state.MipmapCount,
                        state.MipmapCount > 1 ? TextureCreationFlags.MipChain : TextureCreationFlags.None)
                    : new Texture2D(state.Width, state.Height, (TextureFormat)state.Format, state.MipmapCount,
                        !GraphicsFormatUtility.IsSRGBFormat(graphicsFormat));
                try {
                    for (var mip = 0; mip < state.GpuMips.Length; mip++) image.SetPixelData(state.GpuMips[mip], mip);
                    image.Apply(false, true);
                    for (var mip = 0; mip < state.GpuMips.Length; mip++) Graphics.CopyTexture(image, 0, mip, target, 0, mip);
                } finally { NativeObject.DestroyImmediate(image); }
            }
            target.filterMode = (FilterMode)state.FilterMode; target.anisoLevel = state.AnisoLevel; target.mipMapBias = state.MipMapBias;
            target.wrapModeU = (TextureWrapMode)state.WrapU; target.wrapModeV = (TextureWrapMode)state.WrapV; target.wrapModeW = (TextureWrapMode)state.WrapW;
            target.name = source.Name; target.hideFlags = (HideFlags)source.HideFlags;
            RestoreUnscaledReservation(state.Native!, target);
            ResidentNativeMemory.Write(IntPtr.Add(ResidentNativeMemory.Owner(target), 240), BitConverter.GetBytes(state.UpdateCount));
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GfxDevice();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AllocateTextureId(IntPtr device, out int id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReleaseTextureId(IntPtr device, int id);
        private static void RestoreUnscaledReservation(TextureNativeState state, Texture2D target)
        {
            var owner = ResidentNativeMemory.Owner(target); var bytes = Read(owner, 352);
            var main = BitConverter.ToInt32(bytes, 80); var old = BitConverter.ToInt32(bytes, 332);
            if ((bytes[320] & 2) != 0 && old != main) throw Error("target has an independently uploaded unscaled image");
            var engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
            ResidentLoadedAssetIndex.RequireCode(engine, 0x886d80, 32, "30b51e35ca93e56efda9fc8b950d056c61de617badb9704dd9a4b51f95d648ad");
            var device = ((GfxDevice)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0x886d80), typeof(GfxDevice)))();
            if (device == IntPtr.Zero) throw Error("texture ID allocator has no graphics device");
            var table = ResidentNativeMemory.Ptr(device);
            var wanted = state.UnscaledAliasesMain ? main : state.UnscaledTextureIdPresent ? old : 0;
            if (state.UnscaledTextureIdPresent && !state.UnscaledAliasesMain && (wanted == 0 || wanted == main)) {
                var allocate = (AllocateTextureId)Marshal.GetDelegateForFunctionPointer(ResidentNativeMemory.Ptr(IntPtr.Add(table, 0x500)), typeof(AllocateTextureId));
                allocate(device, out wanted);
                if (wanted == 0 || wanted == main) throw Error("graphics device failed to allocate a distinct local texture ID");
            }
            if (old != 0 && old != main && old != wanted) {
                var release = (ReleaseTextureId)Marshal.GetDelegateForFunctionPointer(ResidentNativeMemory.Ptr(IntPtr.Add(table, 0x510)), typeof(ReleaseTextureId));
                release(device, old);
            }
            ResidentNativeMemory.Write(IntPtr.Add(owner, 332), BitConverter.GetBytes(wanted));
            ResidentNativeMemory.Write(IntPtr.Add(owner, 320), new[] { (byte)((bytes[320] & ~2) | (state.UnscaledUploaded ? 2 : 0)) });
        }
        private static SpriteState CaptureSprite(Sprite sprite, Func<object, string?> bind)
        {
            if (sprite.packed || sprite.associatedAlphaSplitTexture != null) throw Error("runtime sprite retains an atlas or split-alpha dependency");
            var state = new SpriteState { Texture = bind(sprite.texture) ?? throw Error("sprite texture has no binding"), Rect = sprite.rect,
                Pivot = sprite.pivot, Border = sprite.border, PixelsPerUnit = sprite.pixelsPerUnit, Vertices = sprite.vertices, Triangles = sprite.triangles };
            var count = sprite.GetPhysicsShapeCount();
            if (count < 0 || count > 4096) throw Error("sprite physics shape count exceeds bounds");
            state.Physics = new Vector2[count][];
            for (var i = 0; i < count; i++) { var points = new List<Vector2>(); sprite.GetPhysicsShape(i, points); state.Physics[i] = points.ToArray(); }
            ValidateSprite(state); return state;
        }
        internal static void ValidateSprite(SpriteState state)
        {
            if (state == null || string.IsNullOrEmpty(state.Texture) || state.Rect.width <= 0 || state.Rect.height <= 0
                || !(state.PixelsPerUnit > 0) || float.IsInfinity(state.PixelsPerUnit) || state.Vertices == null || state.Triangles == null || state.Physics == null
                || state.Vertices.Length > 65535 || state.Triangles.Length % 3 != 0 || state.Triangles.Any(x => x >= state.Vertices.Length)
                || state.Physics.Length > 4096 || state.Physics.Any(x => x == null || x.Length < 3 || x.Length > 65535))
                throw Error("sprite geometry or dependency is invalid");
            RequireBytes(checked((long)state.Vertices.Length * 8 + (long)state.Triangles.Length * 2 + state.Physics.Sum(x => (long)x.Length * 8)));
        }
        internal static Sprite CreateSprite(SpriteState state, Func<string, NativeObject> resolve)
        {
            ValidateSprite(state);
            var texture = resolve(state.Texture) as Texture2D ?? throw Error("sprite texture dependency has another type");
            return Sprite.Create(texture, state.Rect, new Vector2(state.Pivot.x / state.Rect.width, state.Pivot.y / state.Rect.height),
                state.PixelsPerUnit, 0, SpriteMeshType.FullRect, state.Border, false);
        }
        internal static void ApplySprite(SpriteState state, Sprite target)
        {
            ValidateSprite(state); target.OverrideGeometry(state.Vertices, state.Triangles);
            target.OverridePhysicsShape(state.Physics);
        }
    }
}
