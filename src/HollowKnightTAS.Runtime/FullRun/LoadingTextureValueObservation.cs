using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // The same pinned SharedTextureData layout already used by the font reader.
    // Do not call GetRawTextureData<T>: this Unity build calls GetWritableImageData.
    internal sealed class LoadingTextureValueObservation
    {
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(
            IntPtr process, IntPtr address, [Out] byte[] buffer, UIntPtr size, out UIntPtr read);
        private static readonly FieldInfo CachedPointer = typeof(UnityEngine.Object)
            .GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("UnityEngine.Object.m_CachedPtr");
        private bool validated;

        internal object Capture(Texture2D texture, Action<string> omit)
        {
            if (!validated) { LoadingEngineClockProbe.RequirePinnedEngine(); validated = true; }
            var native = (IntPtr)CachedPointer.GetValue(texture)!;
            var owner = Read(IntPtr.Add(native, 88), 8);
            var shared = new IntPtr(BitConverter.ToInt64(owner, 0));
            if (shared == IntPtr.Zero)
            {
                omit("asset-texture-cpu-data-unreadable");
                return new { resident = false };
            }
            var header = Read(shared, 80);
            var length = LoadingTextureDataBounds.Length(header);
            var pointer = new IntPtr(BitConverter.ToInt64(header, 16));
            var bytes = length == 0 ? Array.Empty<byte>() : Read(pointer, length);
            if (!owner.SequenceEqual(Read(IntPtr.Add(native, 88), 8)) || !header.SequenceEqual(Read(shared, 80)))
                throw new InvalidOperationException("Texture CPU data changed during boundary observation.");
            if (length == 0) omit("asset-texture-cpu-data-unreadable");
            // Resident CPU bytes do not establish GPU equality or a restore codec.
            omit("asset-texture-native-copy-contract");
            using (var sha = SHA256.Create())
                return new { resident = length != 0, byteLength = length,
                    width = BitConverter.ToInt32(header, 48), height = BitConverter.ToInt32(header, 52),
                    format = BitConverter.ToInt32(header, 56),
                    sha256 = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() };
        }

        private static byte[] Read(IntPtr pointer, int size)
        {
            var bytes = new byte[size];
            if (pointer == IntPtr.Zero || !ReadProcessMemory(GetCurrentProcess(), pointer, bytes, (UIntPtr)size, out var read)
                || read.ToUInt64() != (ulong)size) throw new InvalidOperationException("Texture CPU memory read failed.");
            return bytes;
        }
    }
}
