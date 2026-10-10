using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class BoundaryMonoLifecycle
    {
        private static readonly FieldInfo Pointer = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)!;
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address,
            byte[] bytes, UIntPtr size, out UIntPtr read);
        internal static byte[] Read(MonoBehaviour component)
        {
            LoadingEngineClockProbe.RequirePinnedEngine();
            var pointer = (IntPtr)Pointer.GetValue(component)!;
            var bytes = new byte[5];
            if (pointer == IntPtr.Zero || !ReadProcessMemory(GetCurrentProcess(), IntPtr.Add(pointer, 292), bytes,
                new UIntPtr(5), out var read) || read.ToUInt64() != 5 || bytes.Any(value => value > 1) || bytes[3] != 0 || bytes[1] > bytes[0])
                throw new InvalidOperationException("Boundary MonoBehaviour lifecycle layout differs.");
            return bytes;
        }
    }
}
