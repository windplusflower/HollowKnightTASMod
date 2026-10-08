using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Read existing FontImpl data without requesting characters, rebuilding its
    // texture or allocating a default material. Dynamic face/packer restoration
    // is still an explicit gap; this is a diagnostic inventory.
    internal sealed class LoadingFontValueObservation
    {
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(
            IntPtr process, IntPtr address, [Out] byte[] buffer, UIntPtr size, out UIntPtr read);
        private static readonly FieldInfo CachedPointer = typeof(UnityEngine.Object)
            .GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("UnityEngine.Object.m_CachedPtr");
        private Dictionary<int, UnityEngine.Object>? loaded;
        private readonly List<object> details = new List<object>();
        private bool validated;
        internal object[] Details => details.ToArray();
        internal void BeginSample() { loaded = null; details.Clear(); }

        internal object Capture(Font font, Action<string> omit)
        {
            if (!validated) { LoadingEngineClockProbe.RequirePinnedEngine(); validated = true; }
            var header = Read((IntPtr)CachedPointer.GetValue(font)!, 80);
            var impl = Pointer(header, 72);
            if (impl == IntPtr.Zero) return new { implementation = "null" };
            var data = Read(impl, 320);
            var ascii = DynamicArray(data, 64, 60);
            var unicode = Vector(data, 96, 60);
            var material = Resolve(BitConverter.ToInt32(header, 64));
            var texture = Resolve(BitConverter.ToInt32(header, 68));
            var atlas = texture is Texture2D image ? Atlas(image) : null;
            if (texture != null && atlas == null) omit("asset-font-atlas-cpu-data-unavailable");
            if (Pointer(data, 312) != IntPtr.Zero) omit("asset-font-dynamic-face-and-packer-codec");
            var asciiState = Characters(ascii);
            var unicodeState = Characters(unicode);
            if (font.name == "TrajanPro-Bold")
                details.Add(new { font = font.name, ascii = CharacterDetails(ascii), unicode = CharacterDetails(unicode),
                    atlas = atlas == null ? null : new { atlas.Width, atlas.Height, atlas.Format, sha256 = Hash(atlas.Bytes) } });
            return new { lineSpacingBits = Hex(header, 56, 4), fontSize = BitConverter.ToInt32(header, 60),
                material, texture,
                trackingBits = Hex(data, 32, 4), characterSpacing = BitConverter.ToInt32(data, 36),
                characterPadding = BitConverter.ToInt32(data, 40), asciiStart = BitConverter.ToInt32(data, 44),
                legacyBounds = data[48] != 0, roundAdvance = data[49] != 0,
                convertCase = BitConverter.ToInt32(data, 52), pixelScaleBits = Hex(data, 56, 4),
                asciiState, unicodeState, fontData = Hash(DynamicArray(data, 128, 1)),
                texWidth = BitConverter.ToUInt32(data, 272), texHeight = BitConverter.ToUInt32(data, 276),
                texMargin = BitConverter.ToUInt32(data, 280), subImageSize = BitConverter.ToUInt32(data, 284),
                subImageIndex = BitConverter.ToUInt32(data, 288), defaultStyle = BitConverter.ToUInt32(data, 292),
                ascentDescentBits = Hex(data, 296, 8), renderMode = BitConverter.ToInt32(data, 304),
                callbackDepth = BitConverter.ToInt32(data, 308),
                atlas = atlas == null ? null : new { atlas.Width, atlas.Height, atlas.Format, sha256 = Hash(atlas.Bytes) } };
        }

        private UnityEngine.Object? Resolve(int instance)
        {
            if (instance == 0) return null;
            loaded ??= Resources.FindObjectsOfTypeAll<Material>().Cast<UnityEngine.Object>()
                .Concat(Resources.FindObjectsOfTypeAll<Texture>()).Where(value => value != null)
                .GroupBy(value => value.GetInstanceID()).ToDictionary(group => group.Key, group => group.First());
            if (!loaded.TryGetValue(instance, out var value))
                throw new InvalidOperationException("P0 font material/texture reference is not already loaded.");
            return value;
        }

        private static object Characters(byte[] data)
        {
            using (var full = new MemoryStream())
            using (var metrics = new MemoryStream())
            using (var placement = new MemoryStream())
            using (var lastUse = new MemoryStream())
            {
                for (var i = 0; i < data.Length; i += 60)
                {
                    // Tail padding is not a C++ field. All actual fields,
                    // including lastUsedInFrame and flipped, are retained.
                    full.Write(data, i, 57);
                    metrics.Write(data, i, 4); metrics.Write(data, i + 20, 32);
                    placement.Write(data, i, 20); placement.Write(data, i + 56, 1);
                    lastUse.Write(data, i, 4); lastUse.Write(data, i + 52, 4);
                }
                return new { count = data.Length / 60, full = Hash(full.ToArray()), metrics = Hash(metrics.ToArray()),
                    placement = Hash(placement.ToArray()), lastUse = Hash(lastUse.ToArray()) };
            }
        }

        private static object[] CharacterDetails(byte[] data)
        {
            if (data.Length / 60 > 4096) throw new InvalidOperationException("P0 font glyph diagnostic budget exceeded.");
            return Enumerable.Range(0, data.Length / 60).Select(index => {
                var offset = index * 60;
                return (object)new { index = BitConverter.ToUInt32(data, offset),
                    size = BitConverter.ToInt32(data, offset + 40), style = BitConverter.ToUInt32(data, offset + 44),
                    uvBits = Hex(data, offset + 4, 16), metricsBits = Hex(data, offset + 20, 32),
                    lastUsed = BitConverter.ToInt32(data, offset + 52), flipped = data[offset + 56] != 0 };
            }).ToArray();
        }

        private sealed class AtlasData
        {
            internal int Width, Height, Format;
            internal byte[] Bytes = Array.Empty<byte>();
        }

        private static AtlasData? Atlas(Texture2D texture)
        {
            var native = (IntPtr)CachedPointer.GetValue(texture)!;
            var shared = Pointer(Read(IntPtr.Add(native, 88), 8), 0);
            if (shared == IntPtr.Zero) return null;
            // SharedTextureData::m_Data, copied directly. No virtual GetData call
            // or GPU readback; an absent resident buffer remains absent.
            var data = Read(shared, 80);
            var bytes = DynamicArray(data, 16, 1);
            if (bytes.Length == 0) return null;
            return new AtlasData { Width = BitConverter.ToInt32(data, 48), Height = BitConverter.ToInt32(data, 52),
                Format = BitConverter.ToInt32(data, 56), Bytes = bytes };
        }

        private static byte[] DynamicArray(byte[] header, int offset, int stride)
        {
            var count = BitConverter.ToInt64(header, offset + 16);
            var capacity = BitConverter.ToUInt64(header, offset + 24) >> 1;
            // Unity dynamic_array stores its allocation/ownership bit in bit 0
            // of capacity. The logical count must fit either owned or borrowed data.
            if (count < 0 || (ulong)count > capacity || count > 64 * 1024 * 1024 / stride)
                throw new InvalidOperationException("P0 font array bounds are invalid.");
            return count == 0 ? Array.Empty<byte>() : Read(Pointer(header, offset), checked((int)count * stride));
        }

        private static byte[] Vector(byte[] header, int offset, int stride)
        {
            var begin = Pointer(header, offset).ToInt64();
            var end = Pointer(header, offset + 8).ToInt64();
            var capacity = Pointer(header, offset + 16).ToInt64();
            var size = end - begin;
            if (size < 0 || end > capacity || size % stride != 0 || size > 64 * 1024 * 1024)
                throw new InvalidOperationException("P0 font vector bounds are invalid.");
            return size == 0 ? Array.Empty<byte>() : Read(new IntPtr(begin), (int)size);
        }

        private static IntPtr Pointer(byte[] data, int offset) => new IntPtr(BitConverter.ToInt64(data, offset));
        private static string Hex(byte[] data, int offset, int length) => BitConverter.ToString(data, offset, length).Replace("-", "").ToLowerInvariant();
        private static string Hash(byte[] data) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(data), 0, 32); }
        private static byte[] Read(IntPtr pointer, int size)
        {
            var data = new byte[size];
            if (pointer == IntPtr.Zero || !ReadProcessMemory(GetCurrentProcess(), pointer, data, (UIntPtr)size, out var read)
                || read.ToUInt64() != (ulong)size) throw new InvalidOperationException("P0 font memory read failed.");
            return data;
        }
    }
}
