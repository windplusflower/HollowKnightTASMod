using System;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed class LoadingRigidbodyObservation
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr process, IntPtr address,
            byte[] buffer, UIntPtr size, out UIntPtr read);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        private readonly FieldInfo cachedPointer;

        internal LoadingRigidbodyObservation()
        {
            LoadingEngineClockProbe.RequirePinnedEngine();
            cachedPointer = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("P0 passive rigidbody pointer field is unavailable.");
        }

        internal LoadingRigidbodyValueBits.Primary CapturePrimary(Rigidbody2D body, Vector3 transformPosition)
        {
            ReadBody(body, out var owner, out var nativeBody);
            return LoadingRigidbodyValueBits.Decode(owner, nativeBody, transformPosition.x, transformPosition.y);
        }

        internal object CaptureValues(Rigidbody2D body)
        {
            ReadBody(body, out var owner, out var nativeBody);
            // The native physics oracle separately binds scene/parent/world,
            // fixture and contact pointers. Never export recoverable bytes.
            return new {
                ownerValues = LoadingRigidbodyValueBits.CopyOwnerValues(owner),
                nativeBodyPresent = nativeBody != null,
                bodyValues = nativeBody == null ? Array.Empty<byte>() : LoadingRigidbodyValueBits.CopyBodyValues(nativeBody),
                parentPresent = BitConverter.ToInt64(owner, 280) != 0,
                scenePresent = BitConverter.ToInt64(owner, 288) != 0,
                transformPosition = body.transform.position,
                transformRotation = body.transform.rotation,
                transformScale = body.transform.localScale
            };
        }

        private void ReadBody(Rigidbody2D body, out byte[] owner, out byte[]? nativeBody)
        {
            var pointer = (IntPtr)cachedPointer.GetValue(body)!;
            owner = Read(pointer, 296);
            var bodyPointer = new IntPtr(BitConverter.ToInt64(owner, 120));
            nativeBody = bodyPointer == IntPtr.Zero ? null : Read(bodyPointer, 200);
        }

        private static byte[] Read(IntPtr address, int length)
        {
            if (address == IntPtr.Zero || (length != 296 && length != 200))
                throw new InvalidOperationException("P0 passive rigidbody memory range is invalid.");
            var bytes = new byte[length];
            if (!ReadProcessMemory(GetCurrentProcess(), address, bytes, new UIntPtr((uint)length), out var read)
                || read.ToUInt64() != (ulong)length)
                throw new InvalidOperationException("P0 passive rigidbody memory read failed.");
            return bytes;
        }
    }
}
