using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // All addresses are process-local. Only typed values and directory bindings
    // may enter a resident snapshot. Callers own boundary admission and ordering.
    internal static class ResidentNativeMemory
    {
        private static readonly FieldInfo PointerField = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw Error("Unity object pointer field is absent");
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr count, out UIntPtr read);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr count, out UIntPtr written);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReserveArray(IntPtr header, UIntPtr capacity, UIntPtr stride, UIntPtr alignment);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AssignString(IntPtr storage, IntPtr first, IntPtr last);
        internal static IntPtr Owner(UnityEngine.Object value)
        {
            if (value == null || IntPtr.Size != 8 || !BitConverter.IsLittleEndian) throw Error("native owner or pinned architecture differs");
            var result = (IntPtr)PointerField.GetValue(value)!;
            if (result == IntPtr.Zero || I32(Read(IntPtr.Add(result, 8), 4), 0) != value.GetInstanceID())
                throw Error("native instance and managed owner differ");
            return result;
        }
        internal static ulong WallMilliseconds() => GetTickCount64();
        internal static byte[] Read(IntPtr address, int count)
        {
            if (count < 0 || count > 32 * 1024 * 1024 || count != 0 && address == IntPtr.Zero) throw Error("native read exceeds bounds");
            var result = new byte[count];
            if (count != 0 && (!ReadProcessMemory(GetCurrentProcess(), address, result, new UIntPtr((uint)count), out var read)
                || read.ToUInt64() != (ulong)count)) throw Error("native state is unreadable");
            return result;
        }
        internal static void Write(IntPtr address, byte[] bytes)
        {
            if (bytes == null || bytes.Length > 32 * 1024 * 1024 || bytes.Length != 0 && address == IntPtr.Zero) throw Error("native write exceeds bounds");
            if (bytes.Length != 0 && (!WriteProcessMemory(GetCurrentProcess(), address, bytes, new UIntPtr((uint)bytes.Length), out var written)
                || written.ToUInt64() != (ulong)bytes.Length)) throw Error("native state write failed");
            if (!Read(address, bytes.Length).SequenceEqual(bytes)) throw Error("native state write did not read back");
        }
        internal static IntPtr Ptr(byte[] bytes, int offset) => new IntPtr(BitConverter.ToInt64(bytes, offset));
        internal static IntPtr Ptr(IntPtr address) => Ptr(Read(address, 8), 0);
        internal static int I32(byte[] bytes, int offset) => BitConverter.ToInt32(bytes, offset);
        internal static uint U32(byte[] bytes, int offset) => BitConverter.ToUInt32(bytes, offset);
        internal static ulong U64(byte[] bytes, int offset) => BitConverter.ToUInt64(bytes, offset);
        internal static byte[] Slice(byte[] bytes, int offset, int count)
        { var result = new byte[count]; Buffer.BlockCopy(bytes, offset, result, 0, count); return result; }
        internal static void Reserve(IntPtr header, int capacity, int stride, int alignment, Action admitted)
        {
            if (admitted == null || capacity < 0 || stride < 1 || alignment < 1 || alignment > 64 || (alignment & (alignment - 1)) != 0
                || (long)capacity * stride > 32 * 1024 * 1024) throw Error("native reserve exceeds bounds");
            admitted(); var before = Read(header, 32); var size = U64(before, 16);
            if (size > (ulong)capacity || size > U64(before, 24) >> 1) throw Error("native reserve would discard owned values");
            var engine = LoadingEngineClockProbe.RequirePinnedEngine(); var address = IntPtr.Add(engine.BaseAddress, 0x7feeb0);
            const string expected = "48895c240848896c24104889742418574883ec400fb641184d8bd1f6d0498be8488bfa488bd9";
            if (BitConverter.ToString(Read(address, expected.Length / 2)).Replace("-", "").ToLowerInvariant() != expected)
                throw Error("native dynamic-array allocator entry differs");
            ((ReserveArray)Marshal.GetDelegateForFunctionPointer(address, typeof(ReserveArray)))(header, new UIntPtr((uint)capacity),
                new UIntPtr((uint)stride), new UIntPtr((uint)alignment));
            var after = Read(header, 32);
            if (U64(after, 16) != size || U64(after, 24) >> 1 < (ulong)capacity || I32(after, 8) != I32(before, 8)
                || capacity != 0 && (Ptr(after, 0) == IntPtr.Zero || (U64(after, 24) & 1) != 0)) throw Error("native array reservation did not preserve its target owner");
            admitted();
        }
        internal static string String(IntPtr storage)
        {
            // core::StringStorageDefault<char>, pinned engine ABI. Inline mode
            // uses the 16-byte union and a null external-data pointer.
            var header = Read(storage, 40); var size = U64(header, 24);
            if (size > 16384) throw Error("native string exceeds bounds");
            var pointer = Ptr(header, 0);
            if (pointer == IntPtr.Zero) {
                if (size >= 16) throw Error("inline native string exceeds storage");
                pointer = IntPtr.Add(storage, 8);
            } else if (U64(header, 8) < size) throw Error("native string capacity differs");
            var bytes = Read(pointer, checked((int)size + 1));
            if (bytes[bytes.Length - 1] != 0 || bytes.Take(bytes.Length - 1).Contains((byte)0)) throw Error("native string terminator differs");
            return new UTF8Encoding(false, true).GetString(bytes, 0, bytes.Length - 1);
        }
        internal static void WriteString(IntPtr storage, string value, Action admitted)
        {
            if (value == null || value.IndexOf('\0') >= 0 || admitted == null) throw Error("native string value or admission differs");
            var bytes = new UTF8Encoding(false, true).GetBytes(value);
            if (bytes.Length > 16384) throw Error("native string exceeds bounds");
            admitted(); _ = String(storage);
            var address = IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, 0xbe6d0);
            const string prefix = "40535641574883ec204d8bf8488bf2488bd9";
            if (BitConverter.ToString(Read(address, prefix.Length / 2)).Replace("-", "").ToLowerInvariant() != prefix)
                throw Error("native string assignment ABI differs");
            // The engine destroys the old storage and constructs a new string
            // using its original allocator label. No source allocation crosses
            // the process boundary; equal values get the same storage policy.
            var buffer = Marshal.AllocHGlobal(bytes.Length + 1);
            try {
                Marshal.Copy(bytes, 0, buffer, bytes.Length); Marshal.WriteByte(buffer, bytes.Length, 0);
                ((AssignString)Marshal.GetDelegateForFunctionPointer(address, typeof(AssignString)))(storage, buffer, IntPtr.Add(buffer, bytes.Length));
                if (String(storage) != value) throw Error("native string did not read back");
                admitted();
            } finally { Marshal.FreeHGlobal(buffer); }
        }
        internal static InvalidDataException Error(string reason) => new InvalidDataException("Resident native state: " + reason);
    }
}
