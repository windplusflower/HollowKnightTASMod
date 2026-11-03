using System;
using System.IO;
using System.Linq;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentNativeAssetState
    {
        // Texture352/SharedTextureData80, pinned player layout. The public CPU
        // image is only one domain: native import/stream metadata, the actual
        // GPU image, its two resource IDs, async fences and manager membership
        // are independent. Presence markers below report uncovered dependencies;
        // they never stand in for their contents or grant restoration coverage.
        internal sealed class TextureNativeState
        {
            public int UsageMode, ColorSpace, OwnerReferences, ForcedFallbackFormat;
            public bool DownscaleFallback, AlphaOptional, Uploaded, TextureIdPresent, UnscaledTextureIdPresent;
            public byte[] TexelSize = new byte[8], PlatformBlob = Array.Empty<byte>();
            public ulong StreamOffset;
            public uint StreamSize;
            public string StreamPath = "";
            public bool[] AsyncOwners = new bool[3];
            public uint[] AsyncVersions = new uint[3];
            public int CreateMainFence, UploadMainFence;
            public bool SharedPresent;
            public int SharedReferences;
            public int DataWidth, DataHeight, DataFormat, DataImageCount, GpuWidth, GpuHeight;
            public ulong DataImageSize;
            public uint InitFlags;
            public bool PowerOfTwo, UnscaledUploaded, PreProcessed, Unreloadable, StreamingRegistered, IgnoreMasterLimit;
            public int MipsStripped;
            internal TextureNativeState Copy() {
                var copy = (TextureNativeState)MemberwiseClone(); copy.TexelSize = (byte[])TexelSize.Clone(); copy.PlatformBlob = (byte[])PlatformBlob.Clone();
                copy.AsyncOwners = (bool[])AsyncOwners.Clone(); copy.AsyncVersions = (uint[])AsyncVersions.Clone(); return copy;
            }
        }
        private static TextureNativeState CaptureTextureNative(Texture2D texture)
        {
            RequireAssetVtable(texture, 0x1685618); var pointer = M.Owner(texture); var owner = M.Read(pointer, 352);
            var platformPointer = M.Ptr(owner, 248); var platformCount = M.U64(owner, 264); var platformCapacity = M.U64(owner, 272) >> 1;
            if (platformCount > platformCapacity || platformCapacity > MaximumBytes) throw Error("texture platform image exceeds its native allocation");
            var result = new TextureNativeState {
                UsageMode = M.I32(owner, 96), ColorSpace = M.I32(owner, 100), OwnerReferences = M.I32(owner, 104), ForcedFallbackFormat = M.I32(owner, 108),
                DownscaleFallback = TextureBool(owner[112]), AlphaOptional = TextureBool(owner[113]), Uploaded = (owner[114] & 1) != 0,
                TextureIdPresent = M.I32(owner, 80) != 0, UnscaledTextureIdPresent = M.I32(owner, 332) != 0, TexelSize = M.Slice(owner, 116, 8),
                StreamOffset = M.U64(owner, 128), StreamSize = M.U32(owner, 136), StreamPath = M.String(IntPtr.Add(pointer, 144)),
                AsyncOwners = new[] { M.Ptr(owner, 184) != IntPtr.Zero, M.Ptr(owner, 200) != IntPtr.Zero, M.Ptr(owner, 216) != IntPtr.Zero },
                AsyncVersions = new[] { M.U32(owner, 192), M.U32(owner, 208), M.U32(owner, 224) }, CreateMainFence = M.I32(owner, 232), UploadMainFence = M.I32(owner, 236),
                SharedPresent = M.Ptr(owner, 88) != IntPtr.Zero, PlatformBlob = M.Read(platformPointer, (int)platformCount),
                DataWidth = M.I32(owner, 284), DataHeight = M.I32(owner, 288), DataFormat = M.I32(owner, 292), DataImageSize = M.U64(owner, 296),
                DataImageCount = M.I32(owner, 304), GpuWidth = M.I32(owner, 308), GpuHeight = M.I32(owner, 312), InitFlags = M.U32(owner, 316),
                PowerOfTwo = (owner[320] & 1) != 0, UnscaledUploaded = (owner[320] & 2) != 0, PreProcessed = TextureBool(owner[322]),
                Unreloadable = TextureBool(owner[328]), StreamingRegistered = M.I32(owner, 336) >= 0, IgnoreMasterLimit = TextureBool(owner[340]), MipsStripped = M.I32(owner, 344)
            };
            byte[]? shared = null;
            if (result.SharedPresent) { shared = M.Read(M.Ptr(owner, 88), 80); result.SharedReferences = M.I32(shared, 12); }
            if (!owner.SequenceEqual(M.Read(pointer, 352)) || !result.PlatformBlob.SequenceEqual(M.Read(platformPointer, result.PlatformBlob.Length))
                || result.StreamPath != M.String(IntPtr.Add(pointer, 144)) || shared != null && !shared.SequenceEqual(M.Read(M.Ptr(owner, 88), 80)))
                throw Error("texture native metadata changed during capture");
            ValidateTextureNative(result); return result;
        }
        private static bool TextureBool(byte value) { if (value > 1) throw Error("texture native boolean differs"); return value != 0; }
        internal static void ValidateTextureNative(TextureNativeState state)
        {
            if (state == null || state.TexelSize == null || state.TexelSize.Length != 8 || state.PlatformBlob == null || state.PlatformBlob.Length > MaximumBytes
                || state.StreamPath == null || state.StreamPath.Length > 16384 || state.StreamPath.IndexOf('\0') >= 0
                || state.AsyncOwners == null || state.AsyncOwners.Length != 3 || state.AsyncVersions == null || state.AsyncVersions.Length != 3
                || state.OwnerReferences < 0 || state.SharedReferences < 0 || state.SharedPresent != (state.SharedReferences != 0)
                || state.DataWidth < 0 || state.DataHeight < 0 || state.DataWidth > 65536 || state.DataHeight > 65536
                || state.GpuWidth < 0 || state.GpuHeight < 0 || state.GpuWidth > 65536 || state.GpuHeight > 65536
                || state.DataImageCount < 0 || state.DataImageCount > 4096 || state.MipsStripped < 0 || state.MipsStripped > 32)
                throw Error("texture native metadata schema differs");
        }
        internal static byte[] EncodeTextureNativeValues(TextureNativeState state)
        {
            ValidateTextureNative(state);
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream)) {
                writer.Write(state.UsageMode); writer.Write(state.ColorSpace); writer.Write(state.ForcedFallbackFormat);
                writer.Write(state.DownscaleFallback); writer.Write(state.AlphaOptional); writer.Write(state.TexelSize);
                writer.Write(state.StreamOffset); writer.Write(state.StreamSize); writer.Write(state.StreamPath); WriteBytes(writer, state.PlatformBlob);
                writer.Write(state.DataWidth); writer.Write(state.DataHeight); writer.Write(state.DataFormat); writer.Write(state.DataImageSize); writer.Write(state.DataImageCount);
                writer.Write(state.GpuWidth); writer.Write(state.GpuHeight); writer.Write(state.InitFlags); writer.Write(state.PowerOfTwo); writer.Write(state.PreProcessed);
                writer.Write(state.Unreloadable); writer.Write(state.IgnoreMasterLimit); writer.Write(state.MipsStripped);
                writer.Flush(); return stream.ToArray();
            }
        }
        private static string[] TextureNativeCoverageGaps(TextureState state)
        {
            var native = state.Native;
            if (native == null) return new[] { "texture/native-owner-state-missing" };
            ValidateTextureNative(native);
            return new[] {
                native.AsyncOwners.Any(value => value) || native.CreateMainFence != 0 || native.UploadMainFence != 0 ? "texture/async-resource-fence-ownership" : "",
                native.StreamingRegistered || state.Streaming ? "texture/streaming-manager-membership-and-mip-state" : "",
                native.TextureIdPresent || native.UnscaledTextureIdPresent ? "texture/gpu-resource-identity-and-image" : "",
                native.SharedPresent && native.SharedReferences != 1 ? "texture/shared-cpu-image-alias-group" : "",
                native.PlatformBlob.Length != 0 ? "texture/platform-image-native-reconstruction" : ""
            }.Where(value => value.Length != 0).ToArray();
        }
    }
}
