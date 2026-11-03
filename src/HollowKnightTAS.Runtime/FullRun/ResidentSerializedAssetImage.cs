using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    // A dependency on the actual serialized object image, not merely its path
    // and local ID. Reads the already-loaded PersistentManager namespace and
    // its exact ObjectInfo slice. Source capture never calls a cache-reader API:
    // memory readers are copied, while a real absolute file is opened read-only.
    // Archive/unknown reader layouts remain explicit gaps.
    internal static partial class ResidentSerializedAssetImage
    {
        private const int MaximumBytes = 32 * 1024 * 1024, MaximumEntries = 1000000;
        internal sealed class Data
        {
            public int Version = 1, NativeTypeRva;
            public string SourcePath = "", Hash = "";
            public long LocalIdentifier;
            public int Length;
            public byte[] TypeHash = Array.Empty<byte>();
            public byte Endianness;
            public string[] CoverageGaps = Array.Empty<string>();
            internal Data Copy() => new Data { Version = Version, NativeTypeRva = NativeTypeRva, SourcePath = SourcePath, Hash = Hash,
                LocalIdentifier = LocalIdentifier, Length = Length, TypeHash = (byte[])TypeHash.Clone(), Endianness = Endianness,
                CoverageGaps = (string[])CoverageGaps.Clone() };
        }
        [StructLayout(LayoutKind.Explicit, Size = 16)]
        internal struct Identifier
        {
            [FieldOffset(0)] internal int File;
            [FieldOffset(8)] internal long Object;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal delegate bool TryLock(IntPtr mutex);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Unlock(IntPtr mutex);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal delegate bool Lookup(IntPtr remapper, int instance, out Identifier identifier);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool Reload(IntPtr manager, IntPtr owner);
        internal sealed class ManagerApi
        {
            internal readonly IntPtr Engine;
            internal readonly TryLock Enter;
            internal readonly Unlock Leave;
            internal readonly Lookup LookupId;
            internal ManagerApi(IntPtr engine)
            {
                Engine = engine; Enter = Entry<TryLock>(engine, 0xedc160);
                Leave = Entry<Unlock>(engine, 0xedc170); LookupId = Entry<Lookup>(engine, 0x909290);
            }
        }
        internal sealed class Observation
        {
            internal readonly List<Tuple<IntPtr, byte[]>> Reads = new List<Tuple<IntPtr, byte[]>>();
            private readonly Dictionary<Tuple<IntPtr,int>, byte[]> metadata = new Dictionary<Tuple<IntPtr,int>, byte[]>();
            private int cachedBytes;
            internal byte[] Read(IntPtr pointer, int length) {
                var key = Tuple.Create(pointer, length);
                if (metadata.TryGetValue(key, out var cached)) return cached;
                var result = M.Read(pointer, length); Reads.Add(Tuple.Create(pointer, result));
                // Only this observation owns these bytes; Frozen independently
                // reads every original address. Large image bodies are not cached.
                if (length <= 8192 && cachedBytes <= MaximumBytes - length) { metadata.Add(key, result); cachedBytes += length; }
                return result;
            }
            private readonly Dictionary<Tuple<IntPtr,int>, byte[]> maps = new Dictionary<Tuple<IntPtr,int>, byte[]>();
            internal byte[]? ObjectMap(IntPtr pointer, int length) {
                var key = Tuple.Create(pointer, length);
                if (maps.TryGetValue(key, out var cached)) return cached;
                if (length > MaximumBytes - cachedBytes) return null;
                var bytes = M.Read(pointer, length); Reads.Add(Tuple.Create(pointer, bytes)); maps.Add(key, bytes); cachedBytes += length;
                return bytes;
            }
            internal void Frozen() { foreach (var item in Reads) if (!item.Item2.SequenceEqual(M.Read(item.Item1, item.Item2.Length))) throw Error("serialized source directory or memory image changed"); }
        }
        internal static Data Capture(NativeObject owner)
        {
            if (!ResidentObjectDirectory.IsLive(owner)) throw Error("serialized image has no live native owner");
            var id = owner.GetInstanceID(); var pointer = M.Owner(owner);
            var result = CaptureIdentity(id, null);
            if (!ResidentObjectDirectory.IsLive(owner) || M.Owner(owner) != pointer || owner.GetInstanceID() != id)
                throw Error("serialized image lost its original owner");
            return result;
        }
        // The global directory independently proved this type against both the
        // Object registry and virtual RTTI. Recheck the actual ID, pointer,
        // object header and serialized type on this particular image read.
        internal static Data CaptureRegistered(int instanceId, IntPtr pointer, int expectedTypeRva)
        {
            if (expectedTypeRva <= 0 || pointer == IntPtr.Zero ||
                ResidentLoadedAssetIndex.RegisteredPointer(instanceId, out var recheck) != pointer)
                throw Error("serialized image owner is no longer the registered native object");
            var header = M.Read(pointer, 16);
            var data = CaptureIdentity(instanceId, expectedTypeRva);
            recheck();
            if (!header.SequenceEqual(M.Read(pointer, 16))) throw Error("registered native header changed while reading source image");
            return data;
        }
        // The caller owns a complete current registry observation and must
        // independently re-read it at the end of this same batch. Do not use
        // a historical map or an arbitrary ID/pointer dictionary here.
        internal static Data CaptureRegistered(int instanceId, IntPtr pointer, ResidentNativeGlobalObjects.NativeClass type,
            ResidentNativeGlobalObjects.CaptureResult registry, IntPtr engine)
        {
            if (activeBatch == null || registry == null || pointer == IntPtr.Zero || type == null || type.Rva <= 0
                || !registry.RegisteredPointers.TryGetValue(instanceId, out var registered) || registered != pointer
                || !registry.RegisteredRows.TryGetValue(instanceId, out var row) || row.TypeRva != type.Rva)
                throw Error("batch image lacks its exact current registry proof");
            var header = M.Read(pointer, 16);
            var flags = M.U32(header, 12);
            if (M.I32(header, 8) != instanceId || (flags >> 21) != type.Index || ((flags & (1U << 20)) != 0) != row.Persistent
                || M.Ptr(header, 0) != IntPtr.Add(engine, row.Vtable)) throw Error("batch image registered header differs");
            var data = activeBatch.ObservedUnmapped(instanceId)
                ? new Data { CoverageGaps = new[] { "serialized-image/no-source-identifier" } }
                : CaptureIdentity(instanceId, type.Rva);
            if (!header.SequenceEqual(M.Read(pointer, 16))) throw Error("batch image native header changed");
            return data;
        }
        // PPtr identity lookup for already-mapped but currently unloaded assets.
        // This does not prove a live native body, invoke a loader, or create a wrapper.
        internal static Data CaptureMappedId(int instanceId, int? expectedTypeRva = null)
        {
            if (instanceId == 0 || expectedTypeRva.HasValue && expectedTypeRva.Value <= 0) throw Error("mapped source identity differs");
            return CaptureIdentity(instanceId, expectedTypeRva);
        }
        private static Data CaptureIdentity(int instanceId, int? expectedTypeRva)
        {
            using var standalone = activeBatch == null ? new Batch(multiple: false) : null;
            var batch = activeBatch ?? throw Error("serialized observation is absent");
            var engine = LoadingEngineClockProbe.RequirePinnedEngine(); var manager = M.Ptr(IntPtr.Add(engine.BaseAddress, 0x1a23bc8));
            if (manager == IntPtr.Zero || M.Ptr(manager) != IntPtr.Add(engine.BaseAddress, 0x16aad40)) throw Error("serialized image manager ABI differs");
            var mutex = IntPtr.Add(manager, 184);
            var api = batch.ApiFor(engine.BaseAddress);
            if (!api.Enter(mutex)) throw Error("serialized image manager is busy");
            try {
                var observation = batch.Memory; var remapper = M.Ptr(observation.Read(IntPtr.Add(manager, 88), 8), 0);
                if (!batch.TryOriginalMapping(manager, remapper, instanceId, out var mapped, out var identifier)) {
                    mapped = remapper != IntPtr.Zero && api.LookupId(remapper, instanceId, out identifier);
                    batch.RecordRemapping(manager, remapper, instanceId, mapped, identifier);
                }
                if (!mapped) return new Data { CoverageGaps = new[] { "serialized-image/no-source-identifier" } };
                var streamArray = observation.Read(IntPtr.Add(manager, 8), 32); var count = ArrayCount(streamArray, 32);
                var pathArray = observation.Read(IntPtr.Add(manager, 352), 32); var paths = ArrayCount(pathArray, 40);
                if (identifier.File < 0 || identifier.File >= count || identifier.File >= paths || identifier.Object == 0) throw Error("serialized source namespace index differs");
                var streamRow = observation.Read(IntPtr.Add(M.Ptr(streamArray, 0), identifier.File * 32), 32); var file = M.Ptr(streamRow, 0);
                var pathStorage = IntPtr.Add(M.Ptr(pathArray, 0), identifier.File * 40); var sourcePath = batch.ReadString(pathStorage);
                observation.Read(pathStorage, 40);
                var loadError = M.I32(streamRow, 24);
                if (loadError != 0)
                    throw Error("engine reported serialized loadError=" + loadError + " for " + sourcePath
                        + " (namespace " + identifier.File + ", local ID " + identifier.Object + ")");
                if (file == IntPtr.Zero) {
                    // UnloadNonDirtyStreams releases the reader while keeping
                    // the path and remapping. Inspect the file Unity would
                    // reopen without repopulating its namespace on the source.
                    try {
                        var path = batch.ResolveSourcePath(engine.BaseAddress, manager, pathStorage, sourcePath);
                        return batch.ReadClosed(path, sourcePath, identifier.Object, expectedTypeRva);
                    } catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException) {
                        throw new InvalidDataException("Resident serialized image: closed stream at " + sourcePath
                            + " (namespace " + identifier.File + ", local ID " + identifier.Object + ", loadError=0)", error);
                    }
                }
                var header = observation.Read(file, 312);
                if (header[131] != 0) throw Error("serialized file has an error flag");
                var row = FindObject(observation, header, identifier.Object);
                var length = M.U32(row, 16); if (length > MaximumBytes) throw Error("serialized object image exceeds the package bound");
                var offset = checked(M.U64(header, 80) + M.U64(row, 8));
                if (offset > long.MaxValue || offset + length > M.U64(header, 88)) throw Error("serialized object image exceeds the source file range");
                var types = M.Slice(header, 0, 32); var typeCount = ArrayCount(types, 216); var typeIndex = M.U32(row, 20);
                if (typeIndex >= typeCount) throw Error("serialized object type index is outside its directory");
                var type = observation.Read(IntPtr.Add(M.Ptr(types, 0), checked((int)typeIndex * 216)), 216);
                var typeRva = M.Ptr(type, 0).ToInt64() - engine.BaseAddress.ToInt64();
                if (typeRva <= 0 || typeRva >= engine.ModuleMemorySize) throw Error("serialized native type is outside the pinned engine");
                if (expectedTypeRva.HasValue && typeRva != expectedTypeRva.Value)
                    throw Error("serialized native type differs from registered runtime RTTI");
                var data = new Data { SourcePath = sourcePath, LocalIdentifier = identifier.Object, Length = (int)length,
                    NativeTypeRva = (int)typeRva, TypeHash = M.Slice(type, 24, 16), Endianness = header[128] };
                var reader = M.Ptr(header, 272);
                if (reader == IntPtr.Zero) throw Error("serialized source cache reader is absent");
                // Body witnesses are independently consumed now, rather than
                // retaining every scene image until the end of the batch.
                var imageObservation = new Observation();
                var body = ReadImage(imageObservation, reader, offset, (int)length, engine.BaseAddress, out var gap);
                imageObservation.Frozen();
                if (ResidentFailureEvidence.Collecting) batch.RecordOpenImage(body);
                data.CoverageGaps = gap == null ? Array.Empty<string>() : new[] { gap };
                if (body != null) data.Hash = batch.Hash(body);
                if (!api.LookupId(remapper, instanceId, out var current)
                    || current.File != identifier.File || current.Object != identifier.Object)
                    throw Error("serialized namespace or original remapping changed");
                Validate(data); return data;
            } finally { api.Leave(mutex); }
        }
        private static int ArrayCount(byte[] header, int stride)
        {
            var count = M.U64(header, 16); var capacity = M.U64(header, 24) >> 1;
            if (count > capacity || count > MaximumEntries || capacity > MaximumEntries || count != 0 && M.Ptr(header, 0) == IntPtr.Zero
                || count * (ulong)stride > int.MaxValue) throw Error("serialized directory array exceeds bounds");
            return (int)count;
        }
        private static byte[] FindObject(Observation observation, byte[] file, long identifier)
        {
            var first = M.Ptr(file, 104); var last = M.Ptr(file, 112); var end = M.Ptr(file, 120); var length = last.ToInt64() - first.ToInt64();
            if (length < 0 || length % 24 != 0 || length / 24 > MaximumEntries || end.ToInt64() < last.ToInt64()
                || first == IntPtr.Zero && (last != IntPtr.Zero || end != IntPtr.Zero)) throw Error("serialized object map exceeds bounds");
            var map = activeBatch?.CacheObjectMaps == true ? observation.ObjectMap(first, checked((int)length)) : null;
            var low = 0; var high = checked((int)(length / 24));
            while (low < high) {
                var middle = low + (high - low) / 2;
                var row = map == null ? observation.Read(IntPtr.Add(first, middle * 24), 24) : null;
                var key = row != null ? BitConverter.ToInt64(row, 0) : BitConverter.ToInt64(map!, middle * 24);
                if (key < identifier) low = middle + 1;
                else if (key > identifier) high = middle;
                else return row ?? M.Slice(map!, middle * 24, 24);
            }
            throw Error("actual local identifier is absent from its serialized object map");
        }
        private static byte[]? ReadImage(Observation observation, IntPtr reader, ulong offset, int length, IntPtr engine, out string? gap)
        {
            var metadata = activeBatch?.Memory ?? observation;
            gap = null; var vtable = M.Ptr(metadata.Read(reader, 8), 0).ToInt64() - engine.ToInt64();
            if (vtable == 0x15dc490) {
                var header = metadata.Read(reader, 24); var array = metadata.Read(M.Ptr(header, 8), 32);
                var size = M.U64(array, 16); var capacity = M.U64(array, 24) >> 1;
                if (size > capacity || offset > size || (ulong)length > size - offset || offset > long.MaxValue) throw Error("serialized memory image exceeds its storage");
                return observation.Read(new IntPtr(checked(M.Ptr(array, 0).ToInt64() + (long)offset)), length);
            }
            if (vtable == 0x1692d10) {
                var header = metadata.Read(reader, 32); var fileSize = M.U64(header, 16); var blockSize = M.U64(header, 24);
                if (blockSize == 0 || blockSize > MaximumBytes || fileSize / blockSize > MaximumEntries || offset > fileSize || (ulong)length > fileSize - offset)
                    throw Error("serialized block image exceeds bounds");
                var bytes = new byte[length]; var written = 0; var cursor = offset;
                while (written < length) {
                    var block = cursor / blockSize; var inside = cursor % blockSize;
                    var pointer = M.Ptr(observation.Read(IntPtr.Add(M.Ptr(header, 8), checked((int)block * 8)), 8), 0);
                    var take = (int)Math.Min((ulong)(length - written), Math.Min(blockSize - inside, fileSize - cursor));
                    var part = observation.Read(IntPtr.Add(pointer, (int)inside), take); Buffer.BlockCopy(part, 0, bytes, written, take);
                    written += take; cursor += (uint)take;
                }
                return bytes;
            }
            if (vtable == 0x1692c18) {
                var header = metadata.Read(reader, 600); var fileSize = M.U64(header, 24); var path = activeBatch?.ReadString(IntPtr.Add(reader, 32)) ?? M.String(IntPtr.Add(reader, 32));
                if (offset > fileSize || (ulong)length > fileSize - offset || offset > long.MaxValue || fileSize > long.MaxValue) throw Error("serialized file image exceeds bounds");
                // VFS archive identifiers are not filesystem paths. Never
                // reinterpret them or guess a data directory from their name.
                if (!AbsoluteFilesystemPath(path)) { gap = "serialized-image/nonfilesystem-file-reader"; return null; }
                var bytes = ReadFilesystemSlice(path, (long)fileSize, offset, length);
                if (activeBatch == null && M.String(IntPtr.Add(reader, 32)) != path) throw Error("serialized file reader path changed during independent source read");
                return bytes;
            }
            gap = "serialized-image/cache-reader-native-type"; return null;
        }
        internal static void Validate(Data value)
        {
            if (value == null || value.Version != 1 || value.SourcePath == null || value.SourcePath.Length > 16384 || value.Hash == null || value.TypeHash == null
                || value.CoverageGaps == null || value.CoverageGaps.Length > 16 || value.CoverageGaps.Any(string.IsNullOrEmpty)
                || value.Length < 0 || value.Length > MaximumBytes || value.Endianness > 1 || value.NativeTypeRva < 0)
                throw Error("serialized image manifest differs");
            if (value.SourcePath.Length == 0) {
                if (value.LocalIdentifier != 0 || value.NativeTypeRva != 0 || value.Length != 0 || value.TypeHash.Length != 0 || value.Hash.Length != 0
                    || !value.CoverageGaps.SequenceEqual(new[] { "serialized-image/no-source-identifier" })) throw Error("unmapped serialized image manifest differs");
            } else if (value.LocalIdentifier == 0 || value.NativeTypeRva <= 0 || value.TypeHash.Length != 16 || value.Hash.Length != 0 &&
                (value.Hash.Length != 64 || value.Hash.Any(character => character < '0' || character > '9' && character < 'a' || character > 'f')))
                throw Error("serialized image identity/hash schema differs");
        }
        internal static void RequireComplete(Data value)
        { Validate(value); if (value.SourcePath.Length == 0 || value.Hash.Length != 64 || value.CoverageGaps.Length != 0) throw Error("serialized image dependency is incomplete"); }
        internal static void RequireEqual(Data source, Data actual)
        {
            RequireComplete(source); RequireComplete(actual);
            if (source.SourcePath != actual.SourcePath || source.LocalIdentifier != actual.LocalIdentifier || source.NativeTypeRva != actual.NativeTypeRva
                || source.Length != actual.Length || source.Hash != actual.Hash || source.Endianness != actual.Endianness || !source.TypeHash.SequenceEqual(actual.TypeHash))
                throw Error("serialized image dependency changed");
        }
        // This lower primitive is intentionally restricted to Shader. A full
        // Shader adapter must preserve its D values and reconcile dependent
        // materials/render caches before admitting the boundary afterwards.
        internal static void ReloadShader(Data source, Shader owner, ResidentMutationScope scope, Action admitted)
        {
            scope.Require(); admitted(); RequireEqual(source, Capture(owner));
            var engine = LoadingEngineClockProbe.RequirePinnedEngine(); var pointer = M.Owner(owner);
            if (M.Ptr(pointer) != IntPtr.Add(engine.BaseAddress, 0x17270d8)) throw Error("serialized reload has another native asset type");
            var address = IntPtr.Add(engine.BaseAddress, 0x8fa810);
            using (var sha = SHA256.Create()) if (BitConverter.ToString(sha.ComputeHash(M.Read(address, 334))).Replace("-", "").ToLowerInvariant()
                != "928e0921f55f9886514429f1fc63d8fbb6b3a83534735b5ee3741514ad89beba") throw Error("serialized in-place reload ABI differs");
            scope.Mutate(() => {
                if (!((Reload)Marshal.GetDelegateForFunctionPointer(address, typeof(Reload)))(M.Ptr(IntPtr.Add(engine.BaseAddress, 0x1a23bc8)), pointer))
                    throw Error("serialized in-place shader reload failed");
                admitted(); if (M.Owner(owner) != pointer) throw Error("serialized reload replaced its native wrapper identity");
                RequireEqual(source, Capture(owner));
            });
        }
        private static T Entry<T>(IntPtr engine, int offset) where T : Delegate => (T)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, offset), typeof(T));
        private static InvalidDataException Error(string reason) => new InvalidDataException("Resident serialized image: " + reason);
    }
}
