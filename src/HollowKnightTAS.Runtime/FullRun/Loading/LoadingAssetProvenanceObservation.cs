using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Pinned-engine, read-only source binding inventory; not an asset restore codec.
    // In particular, a persistent origin does not prove the live value is unmodified.
    internal sealed class LoadingAssetProvenanceObservation
    {
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(
            IntPtr process, IntPtr address, [Out] byte[] buffer, UIntPtr size, out UIntPtr read);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool TryLock(IntPtr mutex);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Unlock(IntPtr mutex);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool Lookup(IntPtr remapper, int instance, out SerializedIdentifier identifier);
        [StructLayout(LayoutKind.Explicit, Size = 16)]
        private struct SerializedIdentifier
        {
            [FieldOffset(0)] internal int File;
            [FieldOffset(8)] internal long Object;
        }

        internal sealed class Origin
        {
            public string kind { get; }
            public string sourcePath { get; }
            public long localIdentifier { get; }
            internal Origin(string kind,string path,long id) { this.kind=kind; sourcePath=path; localIdentifier=id; }
        }
        private readonly Dictionary<int, Origin> cache = new Dictionary<int, Origin>();
        private IntPtr engine;
        private TryLock? tryLock;
        private Unlock? unlock;
        private Lookup? lookup;
        internal void Clear() => cache.Clear();

        internal Origin Capture(int instance)
        {
            if (instance == 0) throw new InvalidOperationException("P0 asset provenance requires a live instance.");
            if (cache.TryGetValue(instance, out var known)) return known;
            if (engine == IntPtr.Zero)
            {
                engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
                tryLock = (TryLock)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0xedc160), typeof(TryLock));
                unlock = (Unlock)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0xedc170), typeof(Unlock));
                lookup = (Lookup)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0x909290), typeof(Lookup));
            }
            // GetPersistentManager only reads this pointer. Never call the higher-level
            // InstanceIDToLocalSerializedObjectIdentifier: it creates namespace mappings.
            var manager = Pointer(Read(IntPtr.Add(engine, 0x1a23bc8), 8), 0);
            if (manager == IntPtr.Zero || Pointer(Read(manager, 8), 0) != IntPtr.Add(engine, 0x16aad40))
                throw new InvalidOperationException("P0 PathNamePersistentManager identity mismatch.");
            var mutex = IntPtr.Add(manager, 184);
            // Bounded observation: a busy resource manager is not a closed checkpoint.
            if (!tryLock!(mutex)) throw new InvalidOperationException("P0 persistent resource manager is busy.");
            Origin result;
            try
            {
                var remapper = Pointer(Read(IntPtr.Add(manager, 88), 8), 0);
                if (remapper == IntPtr.Zero) throw new InvalidOperationException("P0 resource remapper is unavailable.");
                // Both this routine and its hash-map lookup at 0x905a50 have no
                // writes except the caller-owned output/stack (matching PDB + disassembly).
                if (!lookup!(remapper, instance, out var source))
                    result = new Origin("unmapped","",0);
                else
                {
                    var array = Read(IntPtr.Add(manager, 352), 32);
                    var count = BitConverter.ToUInt64(array, 16);
                    var capacity = BitConverter.ToUInt64(array, 24);
                    var data = Pointer(array, 0);
                    if (count > capacity || capacity > 65536 || source.File < 0 || (ulong)source.File >= count || data == IntPtr.Zero)
                        throw new InvalidOperationException("P0 resource pathname index is outside the validated table.");
                    var text = IntPtr.Add(data, checked(source.File * 40));
                    var storage = Read(text, 40);
                    var length = BitConverter.ToUInt64(storage, 24);
                    var characters = Pointer(storage, 0);
                    if (length == 0 || length > 8192 || (characters == IntPtr.Zero && length >= 16))
                        throw new InvalidOperationException("P0 resource pathname string has an unsupported layout.");
                    if (characters == IntPtr.Zero) characters = IntPtr.Add(text, 8);
                    var bytes = Read(characters, checked((int)length + 1));
                    if (bytes[(int)length] != 0) throw new InvalidOperationException("P0 resource pathname is not terminated.");
                    var sourcePath = new UTF8Encoding(false, true).GetString(bytes, 0, (int)length);
                    result = new Origin("serialized",sourcePath,source.Object);
                }
            }
            finally { unlock!(mutex); }
            cache.Add(instance, result);
            return result;
        }

        private static IntPtr Pointer(byte[] bytes, int offset) => new IntPtr(BitConverter.ToInt64(bytes, offset));
        private static byte[] Read(IntPtr address, int length)
        {
            var bytes = new byte[length];
            if (address == IntPtr.Zero || !ReadProcessMemory(GetCurrentProcess(), address, bytes, (UIntPtr)length, out var read)
                || read.ToUInt64() != (ulong)length)
                throw new InvalidOperationException("P0 resource provenance native read failed.");
            return bytes;
        }
    }
}
