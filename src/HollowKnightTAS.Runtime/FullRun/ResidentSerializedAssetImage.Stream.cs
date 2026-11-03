using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentSerializedAssetImage
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileIdentity
        {
            public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileIdentity information);
        private static FileIdentity Identity(FileStream stream)
        {
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out var identity)) throw Error("source file handle identity is unavailable");
            if ((identity.Attributes & 0x10) != 0 || identity.Links == 0) throw Error("source handle is not a stable regular file");
            return identity;
        }
        internal static FileIdentity ReadFileIdentity(string path)
        { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Identity(stream); }
        internal static void RequireFileIdentity(FileIdentity source, FileIdentity actual)
        {
            // Last-access can be updated by our own read; it is not file data.
            if (source.Attributes != actual.Attributes || source.CreationLow != actual.CreationLow || source.CreationHigh != actual.CreationHigh
                || source.WriteLow != actual.WriteLow || source.WriteHigh != actual.WriteHigh || source.Volume != actual.Volume
                || source.SizeHigh != actual.SizeHigh || source.SizeLow != actual.SizeLow || source.Links != actual.Links
                || source.IndexHigh != actual.IndexHigh || source.IndexLow != actual.IndexLow) throw Error("source file identity or metadata changed");
        }
        [ThreadStatic] private static Batch? activeBatch;
        internal sealed class Batch : IDisposable
        {
            internal readonly Observation Memory;
            internal readonly bool CacheObjectMaps;
            private readonly Batch? previous;
            private readonly Lazy<CanonicalEngineFileSha256.BufferHasher> hasher;
            private readonly Dictionary<string, FilePair> files;
            private ResidentNativeGlobalObjects.Observation? classObservation;
            private ILookup<int,ResidentNativeGlobalObjects.NativeClass>? serializedClasses;
            private bool disposed;
            private readonly Dictionary<IntPtr, string> strings;
            private readonly Dictionary<(IntPtr manager, IntPtr path), string> resolvedPaths;
            private ulong indexMs, bodyMs, hashMs;
            private int diskIndexes, closedImages;
            private long closedBytes;
            private int openImages, unsupportedReaders;
            private long openBytes;
            internal string DescribeDiskReads() => "disk indexes=" + diskIndexes + ", closed images=" + closedImages
                + ", closed bytes=" + closedBytes + ", index/read/hash ms=" + indexMs + "/" + bodyMs + "/" + hashMs
                + ", open images=" + openImages + ", open bytes=" + openBytes + ", unsupported readers=" + unsupportedReaders;
            internal void RecordOpenImage(byte[]? bytes) {
                ++openImages;
                if (bytes == null) ++unsupportedReaders; else openBytes += bytes.Length;
            }
            private readonly Dictionary<int, Tuple<IntPtr,IntPtr,bool,Identifier>> remappings;
            private ManagerApi? managerApi;
            internal ManagerApi ApiFor(IntPtr engine)
            {
                if (managerApi == null) managerApi = new ManagerApi(engine);
                if (managerApi.Engine != engine) throw Error("serialized manager engine changed in the observation");
                return managerApi;
            }
            internal bool TryOriginalMapping(IntPtr manager, IntPtr remapper, int id, out bool mapped, out Identifier identifier)
            {
                if (!remappings.TryGetValue(id, out var record)) {
                    identifier = default; mapped = false; return false;
                }
                if (record.Item1 != manager || record.Item2 != remapper) throw Error("serialized mapping owner changed");
                identifier = record.Item4; mapped = record.Item3; return true;
            }
            internal void PrimeRegisteredRemappings(IEnumerable<int> ids, Action admitted)
            {
                admitted();
                var engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
                var manager = M.Ptr(Memory.Read(IntPtr.Add(engine, 0x1a23bc8), 8), 0);
                if (manager == IntPtr.Zero || M.Ptr(Memory.Read(manager, 8), 0) != IntPtr.Add(engine, 0x16aad40))
                    throw Error("serialized image manager ABI differs");
                var mutex = IntPtr.Add(manager, 184);
                var api = ApiFor(engine);
                if (!api.Enter(mutex)) throw Error("serialized image manager is busy during registry enumeration");
                try {
                    var remapper = M.Ptr(Memory.Read(IntPtr.Add(manager, 88), 8), 0);
                    var mappedCount = 0; var count = 0;
                    foreach (var id in ids) {
                        var identifier = default(Identifier);
                        var mapped = remapper != IntPtr.Zero && api.LookupId(remapper, id, out identifier);
                        RecordRemapping(manager, remapper, id, mapped, identifier);
                        ++count; if (mapped) ++mappedCount;
                    }
                    if (ResidentFailureEvidence.Collecting)
                        ResidentFailureEvidence.Mark("native asset remappings=" + mappedCount + "/" + count);
                } finally { api.Leave(mutex); }
                admitted();
            }
            internal bool ObservedUnmapped(int id) => remappings.TryGetValue(id, out var result) && !result.Item3;
            internal void RecordRemapping(IntPtr manager, IntPtr remapper, int id, bool mapped, Identifier value) {
                if (remappings.TryGetValue(id, out var previous)) {
                    if (previous.Item1 != manager || previous.Item2 != remapper || previous.Item3 != mapped
                        || mapped && (previous.Item4.File != value.File || previous.Item4.Object != value.Object))
                        throw Error("serialized original remapping changed within observation");
                } else remappings.Add(id, Tuple.Create(manager, remapper, mapped, value));
            }
            private void RequireRemappings() {
                if (remappings.Count == 0) return;
                var engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
                var api = ApiFor(engine);
                foreach (var group in remappings.GroupBy(pair => pair.Value.Item1)) {
                    if (ResidentNativeMemory.Ptr(IntPtr.Add(engine, 0x1a23bc8)) != group.Key) throw Error("serialized source manager changed");
                    var mutex = IntPtr.Add(group.Key, 184); if (!api.Enter(mutex)) throw Error("serialized source manager is busy at final proof");
                    try {
                        var remapper = M.Ptr(IntPtr.Add(group.Key,88));
                        foreach (var pair in group) {
                            var proof = pair.Value;
                            if (remapper != proof.Item2) throw Error("serialized remapper changed");
                            var current = default(Identifier); var mapped = remapper != IntPtr.Zero && api.LookupId(remapper,pair.Key,out current);
                            if (mapped != proof.Item3 || mapped && (current.File != proof.Item4.File || current.Object != proof.Item4.Object))
                                throw Error("serialized original remapping changed at final proof");
                        }
                    } finally { api.Leave(mutex); }
                }
            }
            internal string ReadString(IntPtr pointer) {
                if (!strings.TryGetValue(pointer, out var value)) strings.Add(pointer, value = ResidentNativeMemory.String(pointer));
                return value;
            }
            internal Batch(bool multiple = true) {
                previous = activeBatch;
                CacheObjectMaps = multiple;
                hasher = previous?.hasher ?? new Lazy<CanonicalEngineFileSha256.BufferHasher>(() => new CanonicalEngineFileSha256.BufferHasher());
                Memory = previous?.Memory ?? new Observation();
                files = previous?.files ?? new Dictionary<string, FilePair>(StringComparer.Ordinal);
                strings = previous?.strings ?? new Dictionary<IntPtr, string>();
                resolvedPaths = previous?.resolvedPaths ?? new Dictionary<(IntPtr,IntPtr),string>();
                remappings = previous?.remappings ?? new Dictionary<int, Tuple<IntPtr,IntPtr,bool,Identifier>>();
                activeBatch = this;
            }
            internal string Hash(byte[] bytes) => hasher.Value.Hex(bytes);
            internal string ResolveSourcePath(IntPtr engine, IntPtr manager, IntPtr storage, string namespacePath) {
                var key = (manager, storage);
                if (!resolvedPaths.TryGetValue(key, out var path)) {
                    path = ResidentSerializedSourcePath.Resolve(engine, manager, storage, namespacePath, Memory.Read, ReadString);
                    resolvedPaths.Add(key, path);
                }
                return path;
            }
            private FilePair FileAt(string path) {
                if (disposed || !ReferenceEquals(activeBatch, this)) throw Error("serialized observation scope is not current");
                if (!AbsoluteFilesystemPath(path)) throw Error("serialized source is not an absolute filesystem path");
                if (!files.TryGetValue(path, out var pair)) {
                    pair = new FilePair(path); files.Add(path, pair);
                }
                return pair;
            }
            internal byte[] ReadFile(string path, long expectedLength, ulong offset, int length) {
                var pair = FileAt(path);
                return ReadVerifiedSlice(pair.First, pair.Second, expectedLength, offset, length, () => { });
            }
            internal Data ReadClosed(string path, string namespacePath, long localId, int? expectedRva) {
                var image = FileAt(path).ReadObject(localId, namespacePath, this, out var classId);
                if (serializedClasses == null) {
                    var engine = LoadingEngineClockProbe.RequirePinnedEngine();
                    classObservation = new ResidentNativeGlobalObjects.Observation(() => { }, engine.BaseAddress, engine.ModuleMemorySize);
                    serializedClasses = ResidentNativeGlobalObjects.ReadClasses(classObservation)
                        .Where(type => type.ObjectDerived && !type.Stripped).ToLookup(type => type.PersistentTypeId);
                }
                var types = serializedClasses[classId].ToArray();
                if (types.Length != 1 || expectedRva.HasValue && types[0].Rva != expectedRva.Value)
                    throw Error("detached serialized object class is ambiguous or differs from its registered runtime RTTI");
                image.NativeTypeRva = types[0].Rva;
                Validate(image); return image;
            }
            internal void RequireUnchanged() {
                if (disposed || !ReferenceEquals(activeBatch, this)) throw Error("serialized observation scope is not current");
                classObservation?.Frozen();
                Memory.Frozen(); RequireRemappings(); foreach (var text in strings) if (ResidentNativeMemory.String(text.Key) != text.Value) throw Error("serialized source pathname changed");
                foreach (var file in files.Values) file.Frozen();
            }
            internal void RecordIndex(ulong elapsed) { ++diskIndexes; indexMs += elapsed; }
            internal void RecordBody(int bytes, ulong readTime, ulong hashTime) {
                ++closedImages; closedBytes += bytes; bodyMs += readTime; hashMs += hashTime;
            }
            public void Dispose() {
                if (disposed) return;
                try { RequireUnchanged(); }
                finally {
                    disposed = true; activeBatch = previous;
                    if (previous == null) {
                        try { foreach (var file in files.Values) file.Dispose(); }
                        finally { if (hasher.IsValueCreated) hasher.Value.Dispose(); }
                    }
                }
            }
        }
        private sealed class FilePair : IDisposable
        {
            internal readonly FileStream First, Second;
            private readonly FileIdentity identity;
            private ResidentSerializedFileIndex.Index? index;
            internal FilePair(string path) {
                First = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess);
                try {
                    identity = Identity(First);
                    Second = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess);
                    try { Frozen(); } catch { Second.Dispose(); throw; }
                } catch { First.Dispose(); throw; }
            }
            internal void Frozen() { RequireFileIdentity(identity, Identity(First)); RequireFileIdentity(identity, Identity(Second)); }
            internal Data ReadObject(long id, string namespacePath, Batch batch, out int classId)
            {
                var timed = ResidentFailureEvidence.Collecting;
                ulong Now() => timed ? ResidentNativeMemory.WallMilliseconds() : 0;
                if (index == null) {
                    var begin = Now();
                    First.Position = Second.Position = 0;
                    index = ResidentSerializedFileIndex.ReadVerified(First, Second);
                    Frozen();
                    if (timed) batch.RecordIndex(Now() - begin);
                }
                var a = index.Lookup(id);
                var start = Now();
                var bytes = ReadVerifiedSlice(First, Second, index.FileLength, checked((ulong)a.Offset), a.Length, Frozen);
                var copied = Now();
                classId = a.ClassId;
                var hash = batch.Hash(bytes);
                if (timed) batch.RecordBody(bytes.Length, copied-start, Now()-copied);
                return new Data { SourcePath = namespacePath, LocalIdentifier = id, Length = a.Length,
                    TypeHash = a.TypeHash, Endianness = index.Endianness,
                    Hash = hash };
            }
            public void Dispose() { Second.Dispose(); First.Dispose(); }
        }
        internal static Batch BeginObservation() => new Batch();
        internal static byte[] ReadFilesystemSlice(string path, long expectedLength, ulong offset, int length)
        {
            if (!AbsoluteFilesystemPath(path)) throw Error("source is not an absolute filesystem path");
            if (activeBatch != null) return activeBatch.ReadFile(path, expectedLength, offset, length);
            // Both handles deny write/delete sharing throughout both reads. The
            // second stream has an independent managed buffer and cursor; its
            // identity must be the exact same Windows file, not a same-size copy.
            using var first = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess);
            var identity = Identity(first);
            using var second = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess);
            void Frozen() { RequireFileIdentity(identity, Identity(first)); RequireFileIdentity(identity, Identity(second)); }
            Frozen(); var bytes = ReadVerifiedSlice(first, second, expectedLength, offset, length, Frozen); Frozen(); return bytes;
        }
        internal static bool AbsoluteFilesystemPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.IndexOf('\0') >= 0 || path.StartsWith("archive:", StringComparison.OrdinalIgnoreCase)) return false;
            var drive = path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
            var unc = path.StartsWith("\\\\", StringComparison.Ordinal) && !path.StartsWith("\\\\.\\", StringComparison.Ordinal)
                && !path.StartsWith("\\\\?\\", StringComparison.Ordinal);
            return Path.IsPathRooted(path) && (drive || unc);
        }
        internal static byte[] ReadVerifiedSlice(Stream first, Stream second, long expectedLength, ulong offset, int length, Action identity)
        {
            if (first == null || second == null || ReferenceEquals(first, second) || !first.CanRead || !second.CanRead || !first.CanSeek || !second.CanSeek
                || identity == null || expectedLength < 0 || offset > (ulong)expectedLength || length < 0 || length > MaximumBytes
                || (ulong)length > (ulong)expectedLength - offset || offset > long.MaxValue) throw Error("source slice extent or independent streams differ");
            identity(); if (first.Length != expectedLength || second.Length != expectedLength) throw Error("source slice file length differs");
            first.Position = (long)offset; second.Position = (long)offset;
            var result = new byte[length]; var written = 0;
            while (written < length) { var read = first.Read(result, written, Math.Min(65536, length - written)); if (read <= 0) throw Error("source slice ended early"); written += read; }
            var buffer = new byte[Math.Min(65536, Math.Max(length, 1))]; var verified = 0;
            while (verified < length) {
                var read = second.Read(buffer, 0, Math.Min(buffer.Length, length - verified));
                if (read <= 0) throw Error("independent source slice ended early");
                // Exact independent bytes are stronger than a second digest,
                // and leave the one payload SHA to CaptureIdentity.
                for (var index = 0; index < read; index++) if (buffer[index] != result[verified + index])
                    throw Error("independent source slice bytes differ");
                verified += read;
            }
            if (first.Length != expectedLength || second.Length != expectedLength) throw Error("source slice length changed"); identity(); return result;
        }
    }
}
