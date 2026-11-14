using System;
using System.Linq;
using System.Runtime.InteropServices;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentMonoNativeState
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong CityHashCall(IntPtr bytes, ulong length);

        // Pinned SerializationCache::FetchData (0x8ca080) indexes by ~klass.
        // BuildSerializationCacheFor (0x8c8b60) receives only ScriptingClassPtr
        // and constructs transfer instruction queues. This is lazy class
        // metadata, retained by the target process, with no instance payload.
        // Probe the existing hash entry directly so capture cannot build a
        // cache, invoke registered builders, or scan unrelated engine objects.
        private static Action BindSerializationCache(IntPtr klass, IntPtr cached)
        {
            if (cached == IntPtr.Zero) return () => { };
            if (klass == IntPtr.Zero) throw Error("serialization cache lacks a script class");
            var engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
            var hashFunction = IntPtr.Add(engine, 0x527860);
            var prefix = new byte[] { 0x40, 0x53, 0x55, 0x57, 0x41, 0x57, 0x48, 0x83, 0xec, 0x38, 0x48, 0x8b, 0xda, 0x48, 0x8b, 0xf9, 0x48, 0x83, 0xfa, 0x20 };
            if (!M.Read(hashFunction, prefix.Length).SequenceEqual(prefix)) throw Error("serialization cache hash ABI differs");
            var key = ~unchecked((ulong)klass.ToInt64());
            var storage = Marshal.AllocHGlobal(8);
            ulong hash;
            try {
                Marshal.WriteInt64(storage, unchecked((long)key));
                hash = ((CityHashCall)Marshal.GetDelegateForFunctionPointer(hashFunction, typeof(CityHashCall)))(storage, 8);
            } finally { Marshal.FreeHGlobal(storage); }
            var tableSlot = IntPtr.Add(engine, 0x1a23820);
            var table = M.Ptr(tableSlot);
            if (table == IntPtr.Zero) throw Error("serialization cache table is absent");
            var header = M.Read(table, 80);
            var entry = FindSerializationCacheEntry(header, key, hash, M.Read);
            var entryBytes = M.Read(entry, 16);
            if (M.Ptr(entryBytes, 8) != cached) throw Error("serialization cache is not the actual class entry");
            var body = M.Read(cached, 416);
            var used = M.I32(body, 8);
            if (used < 0 || used > 10) throw Error("serialization instruction cache shape differs");
            return () => {
                if (M.Ptr(tableSlot) != table || !M.Read(table, 80).SequenceEqual(header)
                    || !M.Read(entry, 16).SequenceEqual(entryBytes) || !M.Read(cached, 416).SequenceEqual(body))
                    throw Error("serialization class dependency changed during capture");
            };
        }

        private static IntPtr FindSerializationCacheEntry(byte[] header, ulong key, ulong hash, Func<IntPtr, int, byte[]> read)
        {
            var buckets = M.U64(header, 64);
            // dense_hashtable stores deleted entries at +8 and occupied
            // entries (including tombstones) at +72.
            var count = M.U64(header, 72);
            var deleted = M.U64(header, 8);
            var storage = M.Ptr(header, 56);
            if (buckets == 0 || buckets > 1048576 || (buckets & (buckets - 1)) != 0
                || count > buckets || deleted > count || storage == IntPtr.Zero)
                throw Error("serialization class table shape differs");
            var empty = M.U64(header, 40);
            var tombstone = M.U64(header, 24);
            var mask = buckets - 1;
            var bucket = hash & mask;
            for (ulong probe = 0; probe < buckets; probe++) {
                var entry = IntPtr.Add(storage, checked((int)(bucket * 16)));
                var candidate = M.U64(read(entry, 16), 0);
                if (candidate == empty) break;
                if (!(header[16] != 0 && deleted != 0 && candidate == tombstone) && candidate == key) return entry;
                bucket = (bucket + probe + 1) & mask;
            }
            throw Error("serialization class cache is not registered");
        }
    }
}
