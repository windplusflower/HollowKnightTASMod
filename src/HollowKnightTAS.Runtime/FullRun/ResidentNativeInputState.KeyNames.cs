using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentNativeInputState
    {
        // KeyToString indexes the image's 670-pointer directory. StringToKey
        // queries an independent std::map<string_ref,int>. Preserve both views
        // including aliasing of image-backed strings and each actual map value.
        private const int KeyNameCount = 0x29e, KeyDirectoryRva = 0x195d390, NameTreeSlotRva = 0x19bde68;
        private const int ReadOnlyDataStart = 0x15be000, ReadOnlyDataEnd = ReadOnlyDataStart + 0x38a90e;
        internal sealed class NameReference
        {
            // -1 represents bytes observed outside pinned readonly image data.
            public int Rva;
            public byte[] Bytes = Array.Empty<byte>();
        }
        internal sealed class KeyRow
        {
            public NameReference Name = new NameReference();
            public int Key;
        }
        internal sealed class KeyTable
        {
            public NameReference[] Directory = Array.Empty<NameReference>();
            public KeyRow[] Rows = Array.Empty<KeyRow>();
            public int Root = -1;
            public int[] Shape = Array.Empty<int>();
            public bool PaddingZero, Initialized;
            public int FirstJoystick, ButtonsPerJoystick, MaximumJoysticks;
            // HeadingInfo32, previous touch count8, previous mouse position8,
            // previous simulated mouse buttons2. All are real static inputs.
            public byte[] InputHistory = new byte[50];
        }
        private static NameReference CaptureName(Live live, IntPtr pointer, int? length)
        {
            if (pointer == IntPtr.Zero) {
                if (length != 0) throw Error("null key name");
                return new NameReference();
            }
            var delta = pointer.ToInt64() - live.Engine.ToInt64();
            byte[] bytes;
            if (length.HasValue) bytes = live.Read(pointer, length.Value);
            else {
                var output = new List<byte>();
                for (;;) {
                    var address = IntPtr.Add(pointer, output.Count);
                    var remaining = 16385 - output.Count;
                    if (remaining <= 0) throw Error("unterminated key-directory name");
                    var take = Math.Min(64, Math.Min(remaining, 4096 - (int)(address.ToInt64() & 4095)));
                    var part = live.Read(address, take);
                    var end = Array.IndexOf(part, (byte)0);
                    if (end >= 0) { output.AddRange(part.Take(end)); break; }
                    output.AddRange(part);
                }
                bytes = output.ToArray();
            }
            var extent = Math.Max(1, bytes.Length + (length.HasValue ? 0 : 1));
            var rva = delta >= ReadOnlyDataStart && delta <= ReadOnlyDataEnd - extent ? (int)delta : -1;
            return new NameReference { Bytes = bytes, Rva = rva };
        }
        private static KeyTable CaptureKeyNames(Live live)
        {
            var table = new KeyTable();
            var pointers = live.Read(IntPtr.Add(live.Engine, KeyDirectoryRva), KeyNameCount * 8);
            var byPointer = new Dictionary<IntPtr, NameReference>();
            table.Directory = new NameReference[KeyNameCount];
            for (var i = 0; i < table.Directory.Length; i++) {
                var pointer = M.Ptr(pointers, i * 8);
                if (!byPointer.TryGetValue(pointer, out var entry)) {
                    entry = CaptureName(live, pointer, null);
                    byPointer.Add(pointer, entry);
                }
                table.Directory[i] = entry;
            }
            var headerAddress = M.Ptr(live.Read(IntPtr.Add(live.Engine, NameTreeSlotRva), 8), 0);
            var header = live.Read(headerAddress, 16);
            var sentinel = M.Ptr(header, 0);
            var count = M.U64(header, 8);
            if (count > MaxItems) throw Error("key-name tree exceeds bounds");
            live.Allocation(headerAddress, 16, true);
            live.Allocation(sentinel, 56, true);
            var head = live.Read(sentinel, 56);
            if (head[24] != 1 || head[25] != 1) throw Error("invalid key-name sentinel");
            table.PaddingZero = !head.Skip(26).Any(x => x != 0);
            var nodes = new List<IntPtr>();
            var rawNodes = new List<byte[]>();
            var rows = new List<KeyRow>();
            var seen = new HashSet<IntPtr>();
            void Walk(IntPtr node, int depth) {
                if (node == sentinel) return;
                if (depth > 64 || !seen.Add(node) || seen.Count > (int)count) throw Error("key-name tree cycle or depth differs");
                live.Allocation(node, 56, true);
                var raw = live.Read(node, 56);
                if (raw[25] != 0 || raw[24] > 1) throw Error("invalid key-name node flags");
                Walk(M.Ptr(raw, 0), depth + 1);
                nodes.Add(node);
                rawNodes.Add(raw);
                var length = M.U64(raw, 40);
                if (length > 16384) throw Error("key-name ref exceeds bounds");
                rows.Add(new KeyRow { Name = CaptureName(live, M.Ptr(raw, 32), (int)length), Key = M.I32(raw, 48) });
                table.PaddingZero &= !raw.Skip(26).Take(6).Concat(raw.Skip(52)).Any(x => x != 0);
                Walk(M.Ptr(raw, 16), depth + 1);
            }
            Walk(M.Ptr(head, 8), 0);
            if (nodes.Count != (int)count || M.Ptr(head, 0) != (count == 0 ? sentinel : nodes[0])
                || M.Ptr(head, 16) != (count == 0 ? sentinel : nodes[nodes.Count - 1])) throw Error("key-name native tree count or endpoint differs");
            var indexes = nodes.Select((node, index) => new { node, index }).ToDictionary(row => row.node, row => row.index);
            int Index(IntPtr node) => node == sentinel ? -1 : indexes.TryGetValue(node, out var value) ? value : throw Error("foreign key-name tree edge");
            table.Root = Index(M.Ptr(head, 8));
            table.Rows = rows.ToArray();
            table.Shape = rawNodes.SelectMany(raw => new[] { Index(M.Ptr(raw, 0)), Index(M.Ptr(raw, 8)), Index(M.Ptr(raw, 16)), (int)raw[24] }).ToArray();
            var flag = live.Read(IntPtr.Add(live.Engine, 0x19bde90), 1)[0];
            if (flag > 1) throw Error("invalid key table initialization flag");
            table.Initialized = flag != 0;
            table.FirstJoystick = M.I32(live.Read(IntPtr.Add(live.Engine, 0x19bde60), 4), 0);
            var settings = live.Read(IntPtr.Add(live.Engine, 0x195d380), 8);
            table.ButtonsPerJoystick = M.I32(settings, 0);
            table.MaximumJoysticks = M.I32(settings, 4);
            table.InputHistory = live.Read(IntPtr.Add(live.Engine, 0x19bde98), 32)
                .Concat(live.Read(IntPtr.Add(live.Engine, 0x19bdec0), 18)).ToArray();
            ValidateKeyNames(table);
            return table;
        }
        private static long ValidateKeyNames(KeyTable table)
        {
            if (table == null || table.Directory == null || table.Directory.Length != KeyNameCount
                || table.Rows == null || table.Rows.Length > MaxItems || table.Shape == null || table.Shape.Length != table.Rows.Length * 4
                || table.InputHistory == null || table.InputHistory.Length != 50 || table.InputHistory[48] > 1 || table.InputHistory[49] > 1)
                throw Error("key-name directory schema differs");
            ResidentTagManagerState.ValidateTreeShape(table.Shape, table.Root);
            long total = KeyNameCount * 8L + table.Rows.Length * 56L;
            void ValidateName(NameReference name, bool directory) {
                if (name == null || name.Bytes == null || name.Bytes.Length > 16384 || directory && name.Bytes.Contains((byte)0)
                    || name.Rva == 0 && (directory || name.Bytes.Length != 0)
                    || name.Rva != -1 && name.Rva != 0 && (name.Rva < ReadOnlyDataStart
                        || name.Rva > ReadOnlyDataEnd - Math.Max(1, name.Bytes.Length + (directory ? 1 : 0)))) throw Error("key name dependency schema differs");
                total += name.Bytes.Length;
                if (total > MaxBytes) throw Error("key-name payload exceeds its budget");
            }
            foreach (var entry in table.Directory) ValidateName(entry, true);
            for (var i = 0; i < table.Rows.Length; i++) {
                var row = table.Rows[i] ?? throw Error("missing key mapping");
                ValidateName(row.Name, false);
                if (i > 0 && CompareBytes(table.Rows[i - 1].Name.Bytes, row.Name.Bytes) >= 0) throw Error("unordered or duplicate key name");
            }
            return total;
        }
        private static int CompareBytes(byte[] left, byte[] right)
        {
            for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
                if (left[i] != right[i]) return left[i] - right[i];
            return left.Length.CompareTo(right.Length);
        }
        private static IEnumerable<string> KeyNameGaps(KeyTable table, int keyLimit, bool canonical)
        {
            if (table.Directory.Concat(table.Rows.Select(row => row.Name)).Any(name => name.Rva == -1))
                yield return "native-input/key-name-literal-lifetime-and-target-binding";
            if (table.Rows.Any(row => row.Key < 0 || row.Key >= keyLimit))
                yield return "native-input/key-name-native-index";
            if (table.MaximumJoysticks < 0 || table.ButtonsPerJoystick < 0 || table.FirstJoystick < 0
                || table.FirstJoystick + (long)table.MaximumJoysticks * table.ButtonsPerJoystick > keyLimit)
                yield return "native-input/joystick-key-domain";
            if (canonical) {
                var shape = ResidentTagManagerState.CanonicalTreeShape(table.Rows.Length, out var root);
                if (root != table.Root || !shape.SequenceEqual(table.Shape) || !table.PaddingZero)
                    yield return "native-input/key-name-tree-storage-canon";
            }
        }
        private static void EncodeKeyNames(BinaryWriter writer, KeyTable table)
        {
            void WriteName(NameReference name) { writer.Write(name.Rva); writer.Write(name.Bytes.Length); writer.Write(name.Bytes); }
            foreach (var name in table.Directory) WriteName(name);
            writer.Write(table.Rows.Length);
            foreach (var row in table.Rows) { WriteName(row.Name); writer.Write(row.Key); }
            writer.Write(table.Initialized);
            writer.Write(table.FirstJoystick); writer.Write(table.ButtonsPerJoystick); writer.Write(table.MaximumJoysticks); writer.Write(table.InputHistory);
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void EraseNames(IntPtr tree, IntPtr allocator, IntPtr root);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AllocateName(UIntPtr size, UIntPtr alignment, int label, int options, IntPtr file, int line);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeName(IntPtr node, int label, IntPtr file, int line);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Lookup(IntPtr name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte AnyKey(IntPtr manager);
        private static void VerifyNativeQueries(Live live)
        {
            RequireInputProducerAdmission();
            var data = live.Snapshot;
            RequireFunction(live.Engine, 0x5332a0, 167, "315bc1b03b55921341a4ea759598ca97a94c2534017255e7d26e0c777a79ef04");
            RequireFunction(live.Engine, 0x52fb80, 184, "14542e5a73150247bb10aafa51cecf7e0c967cb794dfb0bd48b0918cf57b3cd1");
            RequireFunction(live.Engine, 0x530af0, 76, "ab6728511349ff33678d6e27ae66b2f0515e8ce3fb5a2a0e7831a5aca93f1844");
            RequireFunction(live.Engine, 0x530b40, 41, "9081de365299a5bc1217ddbba0d7fc787fd6eeb694d631d89af20662dd80ed68");
            var held = data.Keys[0].Blocks.Any(x => x != 0);
            var down = data.Keys[1].Blocks.Any(x => x != 0);
            var any = (AnyKey)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(live.Engine, 0x530af0), typeof(AnyKey));
            var anyDown = (AnyKey)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(live.Engine, 0x530b40), typeof(AnyKey));
            if (any(live.Owner) != (held || down ? 1 : 0) || anyDown(live.Owner) != (down ? 1 : 0)) throw Error("native any-key query differs");
            var lookup = (Lookup)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(live.Engine, 0x5332a0), typeof(Lookup));
            var values = data.KeyNames.Rows.ToDictionary(row => Convert.ToBase64String(row.Name.Bytes), row => row.Key);
            var names = data.KeyNames.Directory.Concat(data.KeyNames.Rows.Select(row => row.Name))
                .GroupBy(name => Convert.ToBase64String(name.Bytes)).Select(group => group.First());
            var temporary = Marshal.AllocHGlobal(16 + 16385);
            try {
                var storage = IntPtr.Add(temporary, 16);
                Marshal.WriteIntPtr(temporary, storage);
                var counter = 0;
                foreach (var name in names) {
                    if ((counter++ & 31) == 0) live.Admitted();
                    Marshal.Copy(name.Bytes, 0, storage, name.Bytes.Length);
                    Marshal.WriteByte(storage, name.Bytes.Length, 0);
                    Marshal.WriteInt64(temporary, 8, name.Bytes.Length);
                    var expected = name.Bytes.Length != 0 && values.TryGetValue(Convert.ToBase64String(name.Bytes), out var key) ? key : 0;
                    if (lookup(temporary) != expected) throw Error("native StringToKey query differs");
                }
            } finally { Marshal.FreeHGlobal(temporary); }
            live.Admitted();
            RequireInputProducerAdmission();
        }
        private sealed class NamePlan
        {
            private readonly IntPtr engine, tree, sentinel;
            private readonly KeyTable source;
            private readonly EraseNames erase;
            private readonly AllocateName allocate;
            private readonly FreeName release;
            private readonly IntPtr[] directory, names;
            internal NamePlan(IntPtr engine, KeyTable source, Action admitted)
            {
                this.engine = engine;
                this.source = source;
                RequireFunction(engine, 0x52fb10, 97, "c4b1e3aec4faf5978f38609e0e55a2db3f76668025fe5e1e509f2a7a7c609807");
                RequireFunction(engine, 0x56b6a0, 113, "7033638fdc118c0f79fd44b82a529587626aae1fe71af8fa1152178d417dd560");
                RequireFunction(engine, 0x56b640, 94, "2890014de154460d79e3ec3357c76ec9af9f8198edd1243f07e27c9708f58447");
                erase = (EraseNames)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0x52fb10), typeof(EraseNames));
                allocate = (AllocateName)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0x56b6a0), typeof(AllocateName));
                release = (FreeName)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0x56b640), typeof(FreeName));
                tree = M.Ptr(IntPtr.Add(engine, NameTreeSlotRva));
                sentinel = M.Ptr(tree);
                var counter = 0;
                IntPtr Resolve(NameReference reference, bool terminated) {
                    if ((counter++ & 31) == 0) admitted();
                    if (reference.Rva == 0 && !terminated && reference.Bytes.Length == 0) return IntPtr.Zero;
                    if (reference.Rva < ReadOnlyDataStart) throw Error("unbound key name");
                    var pointer = IntPtr.Add(engine, reference.Rva);
                    var expected = terminated ? reference.Bytes.Concat(new byte[1]).ToArray() : reference.Bytes;
                    if (!expected.SequenceEqual(M.Read(pointer, expected.Length))) throw Error("target key name backing bytes differ");
                    return pointer;
                }
                directory = source.Directory.Select(name => Resolve(name, true)).ToArray();
                names = source.Rows.Select(row => Resolve(row.Name, false)).ToArray();
            }
            internal void Apply(Action admitted)
            {
                admitted();
                if (M.Ptr(IntPtr.Add(engine, NameTreeSlotRva)) != tree || M.Ptr(tree) != sentinel) throw Error("key map owner changed");
                var shape = ResidentTagManagerState.CanonicalTreeShape(source.Rows.Length, out var root);
                var nodes = new IntPtr[source.Rows.Length];
                var publishing = false;
                try {
                    // This matches SetupKeyNameMapping's native node allocator,
                    // label and alignment. No unmanaged CLR buffer escapes here.
                    for (var i = 0; i < nodes.Length; i++) {
                        if ((i & 31) == 0) admitted();
                        nodes[i] = allocate(new UIntPtr(56U), new UIntPtr(16U), 43, 0, IntPtr.Add(engine, 0x15c6004), 94);
                        if (nodes[i] == IntPtr.Zero) throw Error("target key-map node allocation failed");
                    }
                    IntPtr Node(int i) => i < 0 ? sentinel : nodes[i];
                    void Pointer(byte[] buffer, int at, IntPtr value) => Buffer.BlockCopy(BitConverter.GetBytes(value.ToInt64()), 0, buffer, at, 8);
                    for (var i = 0; i < nodes.Length; i++) {
                        if ((i & 31) == 0) admitted();
                        var row = source.Rows[i]; var raw = new byte[56];
                        for (var link = 0; link < 3; link++) Pointer(raw, link * 8, Node(shape[i * 4 + link]));
                        raw[24] = (byte)shape[i * 4 + 3];
                        Pointer(raw, 32, names[i]);
                        Buffer.BlockCopy(BitConverter.GetBytes((ulong)row.Name.Bytes.Length), 0, raw, 40, 8);
                        Buffer.BlockCopy(BitConverter.GetBytes(row.Key), 0, raw, 48, 4);
                        M.Write(nodes[i], raw);
                    }
                    publishing = true;
                    erase(tree, tree, M.Ptr(IntPtr.Add(sentinel, 8)));
                    var head = new byte[56];
                    Pointer(head, 0, Node(nodes.Length == 0 ? -1 : 0)); Pointer(head, 8, Node(root)); Pointer(head, 16, Node(nodes.Length - 1));
                    head[24] = head[25] = 1;
                    M.Write(sentinel, head); M.Write(IntPtr.Add(tree, 8), BitConverter.GetBytes((ulong)nodes.Length));
                    var pointers = new byte[KeyNameCount * 8];
                    for (var i = 0; i < directory.Length; i++) Pointer(pointers, i * 8, directory[i]);
                    M.Write(IntPtr.Add(engine, KeyDirectoryRva), pointers);
                    M.Write(IntPtr.Add(engine, 0x195d380), BitConverter.GetBytes(source.ButtonsPerJoystick).Concat(BitConverter.GetBytes(source.MaximumJoysticks)).ToArray());
                    M.Write(IntPtr.Add(engine, 0x19bde60), BitConverter.GetBytes(source.FirstJoystick));
                    M.Write(IntPtr.Add(engine, 0x19bde90), new[] { source.Initialized ? (byte)1 : (byte)0 });
                    M.Write(IntPtr.Add(engine, 0x19bde98), M.Slice(source.InputHistory, 0, 32));
                    M.Write(IntPtr.Add(engine, 0x19bdec0), M.Slice(source.InputHistory, 32, 18));
                    admitted();
                } finally {
                    // Prior to publication these nodes have no native owner.
                    // Once publication begins, a failed scope retires the
                    // process without freeing nodes the tree may already own.
                    if (!publishing) foreach (var node in nodes)
                        if (node != IntPtr.Zero) release(node, 43, IntPtr.Add(engine, 0x15c6004), 99);
                }
            }
        }
    }
}
