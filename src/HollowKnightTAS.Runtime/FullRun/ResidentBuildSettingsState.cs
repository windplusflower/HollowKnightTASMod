using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    // PDB 0334be90-63dd-4644-bee1-fc8d56c840c2/1: BuildSettings352.
    // Object48 remains owned by the shared Object adapter. This complete body
    // is a SAME-BUILD READ-ONLY DEPENDENCY, never mutable gameplay D. Neither
    // ownership bits nor native registry membership prove exclusive storage:
    // GetScenePathName can return borrowed character storage. No Canon/Apply
    // here destroys, assigns, reallocates, zeroes, or writes any native byte.
    // All nonpointer metadata, unused bytes and values must match independently;
    // target-local addresses are frozen privately, never copied across processes.
    internal static class ResidentBuildSettingsState
    {
        internal const int NativeTypeRva = 0x198ceb0, NativeSize = 352;
        private const int VtableRva = 0x1725bc8, MaximumEntries = 65536;
        private const string Policy = "same-build-read-only-dependency-v1";
        private static readonly int[] Arrays = { 48, 80, 112, 144, 176 };
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        internal sealed class Text
        {
            public string Value = "";
            public int Capacity, Label;
            public bool Inline;
            public byte[] Unused = Array.Empty<byte>();
        }
        internal sealed class Secret
        {
            // Credential bytes and potentially stale credential padding never
            // enter exported DTOs, encoded snapshots or messages.
            public byte[] Digest = new byte[32], UnusedDigest = new byte[32];
            public int Length, Capacity, Label, UnusedLength;
            public bool Inline;
        }
        internal sealed class ArrayStorage
        {
            public int Capacity, Label;
            public bool Owned, Present;
            public byte[] Padding = new byte[4];
        }
        internal sealed class StringArray
        {
            public ArrayStorage Storage = new ArrayStorage();
            public Text[] Values = Array.Empty<Text>();
        }
        internal sealed class Data
        {
            public int Version = 2;
            public string Ownership = Policy;
            public string Manager = "";
            public StringArray[] Lists = Array.Empty<StringArray>();
            public byte[] Guid = new byte[16], Flags = new byte[15];
            public byte Padding;
            public Secret AuthToken = new Secret();
            public Text EngineVersion = new Text();
            public ArrayStorage GraphicsStorage = new ArrayStorage();
            public int[] GraphicsApis = Array.Empty<int>();
            public string[] CoverageGaps = Array.Empty<string>();
            internal Data Copy() => JsonConvert.DeserializeObject<Data>(JsonConvert.SerializeObject(this))!;
        }
        internal sealed class Live
        {
            internal Data Snapshot = null!;
            internal IntPtr Owner, Engine;
            internal Action Admitted = null!;
            private long bytes;
            private readonly HashSet<IntPtr> ownedAllocations = new HashSet<IntPtr>();
            internal void OwnAllocation(IntPtr pointer)
            { if (pointer != IntPtr.Zero && !ownedAllocations.Add(pointer)) throw Error("native owned storage is aliased"); }
            private readonly List<Tuple<IntPtr, byte[]>> witness = new List<Tuple<IntPtr, byte[]>>();
            internal byte[] Read(IntPtr address, int size)
            {
                Admitted(); bytes = checked(bytes + size); if (bytes > 32 * 1024 * 1024) throw Error("observation exceeds memory bound");
                var raw = M.Read(address, size); witness.Add(Tuple.Create(address, raw)); return raw;
            }
            internal void RequireUnchanged()
            {
                Admitted(); foreach (var item in witness) {
                    Admitted(); if (!M.Read(item.Item1, item.Item2.Length).SequenceEqual(item.Item2)) throw Error("native build settings changed during observation");
                }
            }
        }
        internal static bool OwnsManager(ResidentNativeGlobalObjects.NativeClass type)
            => type != null && type.Rva == NativeTypeRva && type.Size == NativeSize && type.Name == "BuildSettings"
                && type.Namespace == "" && type.ObjectDerived && type.GlobalManager && !type.Abstract && !type.Stripped;
        internal static Live Capture(ResidentNativeGlobalObjects.CaptureResult globals, Action admitted)
        {
            if (globals == null || admitted == null) throw new ArgumentNullException(); admitted();
            var types = globals.Snapshot.Classes.Where(type => type.Rva == NativeTypeRva).ToArray();
            if (types.Length != 1 || !OwnsManager(types[0])) throw Error("actual native manager RTTI differs");
            var key = "resident-native-global/" + types[0].Key;
            if (!globals.ManagerPointers.TryGetValue(key, out var owner) || owner == IntPtr.Zero) throw Error("registered manager singleton is absent or ambiguous");
            var live = new Live { Owner = owner, Engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, Admitted = admitted };
            RequireOwner(live.Owner, live.Engine);
            var body = live.Read(IntPtr.Add(owner, 48), NativeSize - 48);
            var lists = new List<StringArray>();
            foreach (var offset in Arrays) {
                var header = M.Slice(body, offset - 48, 32); var count = Count(header);
                var storage = Storage(header); if (storage.Owned) live.OwnAllocation(M.Ptr(header, 0)); var values = new Text[count];
                for (var index = 0; index < count; index++) values[index] = ReadText(live, IntPtr.Add(M.Ptr(header, 0), index * 40));
                lists.Add(new StringArray { Storage = storage, Values = values });
            }
            var graphics = M.Slice(body, 320 - 48, 32); var graphicsCount = Count(graphics);
            if ((M.U64(graphics, 24) & 1) == 0) live.OwnAllocation(M.Ptr(graphics, 0));
            var bytes = live.Read(M.Ptr(graphics, 0), graphicsCount * 4);
            var data = new Data { Manager = key, Lists = lists.ToArray(), Guid = M.Slice(body, 208 - 48, 16), Flags = M.Slice(body, 224 - 48, 15),
                Padding = body[239 - 48], AuthToken = ReadSecret(live, IntPtr.Add(owner, 240)), EngineVersion = ReadText(live, IntPtr.Add(owner, 280)),
                GraphicsStorage = Storage(graphics), GraphicsApis = Enumerable.Range(0, graphicsCount).Select(index => M.I32(bytes, index * 4)).ToArray() };
            Validate(data); live.Snapshot = data; data.CoverageGaps = RequiredGaps(data); live.RequireUnchanged(); return live;
        }
        private static int Count(byte[] header)
        {
            var count = M.U64(header, 16); var capacity = M.U64(header, 24) >> 1;
            if (count > capacity || capacity > MaximumEntries || capacity != 0 && M.Ptr(header, 0) == IntPtr.Zero) throw Error("native array shape differs");
            return (int)count;
        }
        private static ArrayStorage Storage(byte[] header) => new ArrayStorage { Capacity = (int)(M.U64(header, 24) >> 1),
            Owned = (M.U64(header, 24) & 1) == 0, Present = M.Ptr(header, 0) != IntPtr.Zero, Label = M.I32(header, 8), Padding = M.Slice(header, 12, 4) };
        private static Text ReadText(Live live, IntPtr address)
        {
            var header = live.Read(address, 40); var pointer = M.Ptr(header, 0); var size = M.U64(header, 24);
            var capacity = pointer == IntPtr.Zero ? 15UL : M.U64(header, 8);
            if (size > 16384 || capacity > 32 * 1024 * 1024 || pointer == IntPtr.Zero && size > 15 || pointer != IntPtr.Zero && capacity != 0 && capacity < size)
                throw Error("native string shape differs");
            if (pointer != IntPtr.Zero && capacity != 0) live.OwnAllocation(pointer);
            var raw = live.Read(pointer == IntPtr.Zero ? IntPtr.Add(address, 8) : pointer, checked((int)size + 1));
            if (raw[raw.Length - 1] != 0 || raw.Take(raw.Length - 1).Contains((byte)0)) throw Error("native string terminator differs");
            var unused = pointer == IntPtr.Zero ? M.Slice(header, 9 + (int)size, 15 - (int)size) : M.Slice(header, 16, 8);
            return new Text { Value = Utf8.GetString(raw, 0, raw.Length - 1), Capacity = (int)capacity, Inline = pointer == IntPtr.Zero,
                Label = M.I32(header, 32), Unused = unused.Concat(M.Slice(header, 36, 4)).ToArray() };
        }
        internal static void Validate(Data data)
        {
            if (data == null || data.Version != 2 || string.IsNullOrEmpty(data.Manager) || !data.Manager.StartsWith("resident-native-global/198ceb0/", StringComparison.Ordinal)
                || data.Lists == null || data.Lists.Length != 5 || data.Lists.Any(list => list == null || list.Values == null || list.Values.Length > MaximumEntries)
                || data.Guid == null || data.Guid.Length != 16 || data.Flags == null || data.Flags.Length != 15 || data.Flags.Any(flag => flag > 1)
                || data.GraphicsApis == null || data.GraphicsApis.Length > MaximumEntries || data.CoverageGaps == null || data.CoverageGaps.Any(value => value == null))
                throw Error("typed manager snapshot differs");
            foreach (var list in data.Lists) { ValidateStorage(list.Storage, list.Values.Length); foreach (var text in list.Values) ValidateText(text); }
            ValidateStorage(data.GraphicsStorage, data.GraphicsApis.Length); ValidateSecret(data.AuthToken); ValidateText(data.EngineVersion);
        }
        private static void ValidateStorage(ArrayStorage storage, int count)
        {
            if (storage == null || storage.Capacity < count || storage.Capacity > MaximumEntries || storage.Capacity != 0 && !storage.Present || storage.Label < 0 || storage.Padding == null || storage.Padding.Length != 4)
                throw Error("typed array storage differs");
        }
        private static void ValidateText(Text text)
        {
            if (text == null || text.Value == null || text.Value.IndexOf('\0') >= 0 || text.Label < 0 || text.Unused == null) throw Error("typed string differs");
            var size = Utf8.GetByteCount(text.Value);
            if (size > 16384 || text.Capacity < 0 || text.Capacity > 32 * 1024 * 1024 || text.Inline && (text.Capacity != 15 || size > 15)
                || !text.Inline && text.Capacity != 0 && text.Capacity < size || text.Unused.Length != (text.Inline ? 19 - size : 12)) throw Error("typed string storage differs");
        }
        private static Secret ReadSecret(Live live, IntPtr address)
        {
            // ReadText's complete local memory witnesses stay private in Live;
            // only its digest/length and nonsensitive storage metadata escape.
            var text = ReadText(live, address); byte[] digest, unusedDigest;
            using (var sha = SHA256.Create()) { digest = sha.ComputeHash(Utf8.GetBytes(text.Value)); unusedDigest = sha.ComputeHash(text.Unused); }
            return new Secret { Digest = digest, Length = Utf8.GetByteCount(text.Value), Capacity = text.Capacity,
                Label = text.Label, Inline = text.Inline, UnusedLength = text.Unused.Length, UnusedDigest = unusedDigest };
        }
        private static void ValidateSecret(Secret secret)
        {
            if (secret == null || secret.Digest == null || secret.Digest.Length != 32 || secret.Length < 0 || secret.Length > 16384
                || secret.Capacity < 0 || secret.Capacity > 32 * 1024 * 1024 || secret.Label < 0 || secret.UnusedDigest == null || secret.UnusedDigest.Length != 32
                || secret.Inline && (secret.Capacity != 15 || secret.Length > 15) || !secret.Inline && secret.Capacity != 0 && secret.Capacity < secret.Length
                || secret.UnusedLength != (secret.Inline ? 19 - secret.Length : 12)) throw Error("typed credential dependency differs");
        }
        internal static string[] RequiredGaps(Data data)
        {
            Validate(data);
            return data.Ownership == Policy ? Array.Empty<string>() : new[] { "build-settings/unproven-native-storage-mutation-policy" };
        }
        internal static void RequireCanonicalizable(Data data)
        {
            var gaps = RequiredGaps(data);
            if (gaps.Length != 0 || data.CoverageGaps.Length != 0) throw Error(string.Join(";", gaps.Concat(data.CoverageGaps)));
        }
        internal static void RequireComplete(Data data) => RequireCanonicalizable(data);
        internal static byte[] EncodeDependencies(Data data)
        {
            RequireComplete(data); using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Utf8);
            void Storage(ArrayStorage storage) { writer.Write(storage.Capacity); writer.Write(storage.Label); writer.Write(storage.Owned); writer.Write(storage.Present); writer.Write(storage.Padding); }
            void TextValue(Text text) {
                writer.Write(text.Value); writer.Write(text.Capacity); writer.Write(text.Label); writer.Write(text.Inline); writer.Write(text.Unused.Length); writer.Write(text.Unused);
            }
            writer.Write(data.Version); writer.Write(data.Ownership); writer.Write(data.Manager);
            foreach (var list in data.Lists) { Storage(list.Storage); writer.Write(list.Values.Length); foreach (var text in list.Values) TextValue(text); }
            writer.Write(data.Guid); writer.Write(data.Flags); writer.Write(data.Padding);
            var secret = data.AuthToken; writer.Write(secret.Length); writer.Write(secret.Digest); writer.Write(secret.Capacity); writer.Write(secret.Label);
            writer.Write(secret.Inline); writer.Write(secret.UnusedLength); writer.Write(secret.UnusedDigest); TextValue(data.EngineVersion);
            Storage(data.GraphicsStorage); writer.Write(data.GraphicsApis.Length); foreach (var api in data.GraphicsApis) writer.Write(api);
            writer.Flush(); return stream.ToArray();
        }
        // Historical interface names retained for coordinator integration. Both
        // encodings contain the complete typed Dependency, not a constant PASS.
        internal static byte[] EncodeValues(Data data) => EncodeDependencies(data);
        internal static byte[] EncodeS0(Data data) => EncodeDependencies(data);
        internal static void RequireEqual(Data source, Data actual)
        {
            if (!EncodeDependencies(source).SequenceEqual(EncodeDependencies(actual))) throw Error("independent complete typed build dependency differs");
        }
        internal static Live Canon(Live before, ResidentNativeGlobalObjects.CaptureResult globals, ResidentMutationScope scope, Action admitted)
        {
            RequireCanonicalizable(before.Snapshot); before.RequireUnchanged(); scope.Require(); admitted();
            var after = Capture(globals, admitted); RequireEqual(before.Snapshot, after.Snapshot); return after;
        }
        internal static Plan Prepare(Data source, ResidentNativeGlobalObjects.CaptureResult globals, ResidentMutationScope scope, Action admitted)
            => new Plan(source, globals, scope, admitted);
        internal sealed class Plan
        {
            private readonly Data source;
            private readonly ResidentNativeGlobalObjects.CaptureResult globals;
            private readonly ResidentMutationScope scope;
            private readonly Action admitted;
            private readonly Live before;
            private bool attempted, applied;
            internal Plan(Data source, ResidentNativeGlobalObjects.CaptureResult globals, ResidentMutationScope scope, Action admitted)
            {
                if (scope == null || admitted == null) throw new ArgumentNullException(); RequireCanonicalizable(source); this.source = source.Copy();
                this.globals = globals; this.scope = scope; this.admitted = admitted; before = Capture(globals, admitted);
                // A mismatch is an unsupported dependency, never permission to
                // overwrite deployment settings or borrowed native storage.
                RequireEqual(this.source, before.Snapshot);
            }
            internal void Apply()
            {
                if (attempted) throw Error("manager plan was reused"); attempted = true; admitted(); scope.Require(); before.RequireUnchanged();
                RequireEqual(source, Capture(globals, admitted).Snapshot); applied = true; Verify();
            }
            internal void Verify() { if (!applied) throw Error("manager plan has not applied"); scope.Require(); admitted(); RequireEqual(source, Capture(globals, admitted).Snapshot); }
        }
        private static void RequireOwner(IntPtr owner, IntPtr engine)
        {
            if (M.Ptr(owner) != IntPtr.Add(engine, VtableRva)) throw Error("actual manager vtable differs");
        }
        private static InvalidDataException Error(string reason) => new InvalidDataException("ResidentBuildSettingsState: " + reason);
    }
}
