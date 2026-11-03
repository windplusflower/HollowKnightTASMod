using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentNativeAssetState
    {
        // Exact CPU image transfer. This stage leaves GPU upload/resource and
        // streaming-manager registration to the full Texture2D adapter, whose
        // coverage gate stays closed until those dependencies are implemented.
        // It never calls LoadRawTextureData: that API can reinitialize crunched
        // or special formats, changing the captured native CPU representation.
        internal static PreparedTextureImageBuffers PrepareTextureImageBuffers(Snapshot source, Texture2D target, Func<bool> admitted)
            => new PreparedTextureImageBuffers(source, target, admitted);
        internal sealed class PreparedTextureImageBuffers
        {
            private readonly Snapshot source;
            private readonly Texture2D target;
            private readonly Func<bool> admitted;
            private readonly TextureImageFunctions functions;
            private bool attempted, applied;
            internal PreparedTextureImageBuffers(Snapshot source, Texture2D target, Func<bool> admitted)
            {
                if (source == null || source.Kind != "Texture2D" || source.Texture == null || source.Type.Resolve() != typeof(Texture2D) || admitted == null)
                    throw Error("texture CPU transfer lacks its exact type or admitted owner");
                ResidentAssetPipeline.ValidateTexture(source.Texture);
                if (!source.Texture.CpuDataPresent || !source.Texture.Readable) throw Error("texture CPU transfer has no readable source image");
                this.source = source.Copy(); this.target = target; this.admitted = admitted; Require(); functions = new TextureImageFunctions();
                var current = CaptureTexture(target);
                if (!current.Readable || current.Width != source.Texture.Width || current.Height != source.Texture.Height
                    || current.Dimension != source.Texture.Dimension || current.Format != source.Texture.Format || current.GraphicsFormat != source.Texture.GraphicsFormat
                    || current.MipmapCount != source.Texture.MipmapCount) throw Error("texture CPU transfer target needs native image reconstruction before copying");
                RequireQuiescentTexture(target);
            }
            internal void Apply()
            {
                Require(); if (attempted) throw Error("texture CPU application is single use"); attempted = true; RequireQuiescentTexture(target);
                var data = source.Texture!; var pointer = ResidentNativeMemory.Owner(target); functions.Unshare(pointer);
                var shared = ResidentNativeMemory.Ptr(IntPtr.Add(pointer, 88));
                if (shared == IntPtr.Zero || BitConverter.ToInt32(Read(IntPtr.Add(shared, 12), 4), 0) != 1)
                    throw Error("texture native COW did not return uniquely owned CPU data");
                var array = IntPtr.Add(shared, 16); var header = Read(array, 32);
                if (BitConverter.ToUInt64(header, 16) > (BitConverter.ToUInt64(header, 24) >> 1)) throw Error("target texture CPU allocation bounds differ");
                // Bytes are trivial values. Shortening before Reserve avoids
                // pretending the target's old length must fit the new image.
                ResidentNativeMemory.Write(IntPtr.Add(array, 16), BitConverter.GetBytes(0UL));
                ResidentNativeMemory.Reserve(array, data.CpuBytes.Length, 1, 1, Require);
                ResidentNativeMemory.Write(ResidentNativeMemory.Ptr(array), data.CpuBytes);
                ResidentNativeMemory.Write(IntPtr.Add(array, 16), BitConverter.GetBytes((ulong)data.CpuBytes.Length));
                ResidentNativeMemory.Write(IntPtr.Add(shared, 48), new[] { data.CpuWidth, data.CpuHeight, data.CpuFormat }.SelectMany(BitConverter.GetBytes).ToArray());
                ResidentNativeMemory.Write(IntPtr.Add(shared, 64), BitConverter.GetBytes(data.CpuImageSize));
                ResidentNativeMemory.Write(IntPtr.Add(shared, 72), new[] { data.CpuImageCount, data.CpuMipCount }.SelectMany(BitConverter.GetBytes).ToArray());
                var settings = new byte[24];
                Buffer.BlockCopy(BitConverter.GetBytes(data.FilterMode), 0, settings, 0, 4); Buffer.BlockCopy(BitConverter.GetBytes(data.AnisoLevel), 0, settings, 4, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(data.MipMapBias), 0, settings, 8, 4); Buffer.BlockCopy(BitConverter.GetBytes(data.WrapU), 0, settings, 12, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(data.WrapV), 0, settings, 16, 4); Buffer.BlockCopy(BitConverter.GetBytes(data.WrapW), 0, settings, 20, 4);
                ResidentNativeMemory.Write(IntPtr.Add(pointer, 56), settings); functions.ApplySettings(pointer);
                ResidentNativeMemory.Write(IntPtr.Add(pointer, 240), BitConverter.GetBytes(data.UpdateCount));
                target.name = source.Name; target.hideFlags = (HideFlags)source.HideFlags;
                applied = true; Verify();
            }
            internal void Verify()
            {
                Require(); if (!applied) throw Error("texture CPU verification precedes application");
                if (target.name != source.Name || (int)target.hideFlags != source.HideFlags
                    || !EncodeTextureImage(source.Texture!).SequenceEqual(EncodeTextureImage(CaptureTexture(target))))
                    throw Error("texture CPU image, sampler or metadata failed independent readback");
            }
            private void Require()
            {
                if (!admitted() || !ResidentObjectDirectory.IsLive(target) || target.GetType() != typeof(Texture2D))
                    throw Error("texture CPU transfer lost its admitted exact-type owner");
            }
        }
        internal static byte[] EncodeTextureImage(TextureState state)
        {
            ResidentAssetPipeline.ValidateTexture(state);
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream)) {
                writer.Write(state.Width); writer.Write(state.Height); writer.Write(state.Dimension); writer.Write(state.Format); writer.Write(state.GraphicsFormat); writer.Write(state.MipmapCount);
                writer.Write(state.Readable); writer.Write(state.FilterMode); writer.Write(state.AnisoLevel); writer.Write(state.MipMapBias);
                writer.Write(state.WrapU); writer.Write(state.WrapV); writer.Write(state.WrapW); writer.Write(state.UpdateCount);
                writer.Write(state.CpuDataPresent); writer.Write(state.CpuWidth); writer.Write(state.CpuHeight); writer.Write(state.CpuFormat);
                writer.Write(state.CpuImageSize); writer.Write(state.CpuImageCount); writer.Write(state.CpuMipCount); WriteBytes(writer, state.CpuBytes);
                return stream.ToArray();
            }
        }
        private static void RequireQuiescentTexture(Texture2D texture)
        {
            var pointer = ResidentNativeMemory.Owner(texture); var owner = Read(pointer, 352);
            // The two JobGroupID handles and AsyncFence carry actual pointer
            // owners at 184/200/216. Completed-but-retained handles also need
            // the scheduler adapter; this helper only admits their empty form.
            if (Pointer(owner, 184) != IntPtr.Zero || Pointer(owner, 200) != IntPtr.Zero || Pointer(owner, 216) != IntPtr.Zero
                || BitConverter.ToInt32(owner, 232) != 0 || BitConverter.ToInt32(owner, 236) != 0 || owner[323] != 0)
                throw Error("texture CPU transfer requires quiescent nonstreaming resource ownership");
            var shared = Pointer(owner, 88);
            if (shared == IntPtr.Zero) throw Error("texture CPU transfer cannot construct absent SharedTextureData");
            var header = Read(shared, 80); var count = LoadingTextureDataBounds.Length(header); RequireBytes(count);
            if (BitConverter.ToInt32(header, 12) < 1) throw Error("texture CPU owner has an invalid native reference count");
        }
        private sealed class TextureImageFunctions
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Unary(IntPtr owner);
            internal readonly Unary Unshare, ApplySettings;
            internal TextureImageFunctions()
            {
                var engine = LoadingEngineClockProbe.RequirePinnedEngine();
                Unshare = Bind(engine.BaseAddress, 0x6800e0, 335, "5d487d128d79ea2b27707930ba4d5fc2d61e71b243ff8d1cf03391fa8250ac56");
                ApplySettings = Bind(engine.BaseAddress, 0x6761b0, 320, "11f0c5e52a1b4efe49aadea3f5710933827245d555a20ff6b87c64a8fcede3ff");
            }
            private static Unary Bind(IntPtr module, int rva, int extent, string expected)
            {
                var address = IntPtr.Add(module, rva);
                using (var sha = SHA256.Create())
                    if (BitConverter.ToString(sha.ComputeHash(Read(address, extent))).Replace("-", "").ToLowerInvariant() != expected)
                        throw Error("pinned Texture2D native entry differs: " + rva.ToString("x"));
                return (Unary)Marshal.GetDelegateForFunctionPointer(address, typeof(Unary));
            }
        }
    }
}
