using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Object48 is owned by ResidentObjectNativeState. The manager owns its axis
    // definitions and transient values, key blocks, mouse vectors, joystick rows,
    // strings, IME, pending quit/unload and exit code. Managed Movie input leases
    // only intercept selected consumers and do not own this native storage.
    internal static partial class ResidentNativeInputState
    {
        internal const int NativeTypeRva = 0x195d330, NativeSize = 352;
        private const int VtableRva = 0x1675260, ContextSlotRva = 0x19d7a38;
        private const int MaxItems = 65536, MaxBytes = 32 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        // Scalars are packed as manager[176..199] followed by manager[312..351].
        private static readonly int[] ScalarPadding = { 34, 35, 41, 42, 43, 54, 55, 61, 62, 63 };
        private static readonly int[] ScalarBools = { 32, 33, 40, 52, 53, 60 };
        private static readonly int[] TextOffsets = { 0, 48, 88 };
        private static readonly string[] ExternalDomains = {
            "native-input/platform-keyboard-mouse-joystick-and-IME-event-queues"
        };

        internal sealed class Storage
        {
            public int Label, Capacity;
            public bool Owned, Present;
            public byte[] Padding = new byte[4];
        }
        internal sealed class TextValue
        {
            public string Value = "";
            public bool Inline;
            public int Label, Capacity;
            public byte[] Padding = Array.Empty<byte>();
        }
        internal sealed class Axis
        {
            public TextValue[] Names = Array.Empty<TextValue>();
            // m_NameHash then positiveButton through invert, including the
            // exact float bits of value/rawValue/gravity/dead/sensitivity.
            public byte[] Fields = new byte[54], Padding = new byte[10];
        }
        internal sealed class Bitset
        {
            public int Label, Bits;
            public byte[] Blocks = Array.Empty<byte>(), Padding = new byte[4];
        }
        internal sealed class Joystick
        {
            public Storage Array = new Storage();
            public byte[] Positions = System.Array.Empty<byte>();
        }
        internal sealed class Data
        {
            public int Version = 4;
            public string Manager = "";
            public Storage AxisStorage = new Storage(), JoystickStorage = new Storage();
            public Axis[] Axes = Array.Empty<Axis>();
            public Joystick[] Joysticks = Array.Empty<Joystick>();
            public Bitset[] Keys = Array.Empty<Bitset>();
            public TextValue Input = new TextValue(), Composition = new TextValue();
            public KeyTable KeyNames = new KeyTable();
            public LegacyQueues Legacy = new LegacyQueues();
            public EventQueues Events = new EventQueues();
            public byte[] Scalars = new byte[64], Padding = new byte[10];
            public string[] CoverageGaps = Array.Empty<string>();
            internal Data Copy() => JsonConvert.DeserializeObject<Data>(JsonConvert.SerializeObject(this))!;
        }
        internal sealed class Live
        {
            internal Data Snapshot = null!;
            internal IntPtr Owner, Engine;
            internal LegacyLocal LegacyIngress = null!;
            internal Action Admitted = null!;
            internal Func<IntPtr> ReadEventOwner = null!;
            internal readonly List<Tuple<IntPtr, byte[]>> Reads = new List<Tuple<IntPtr, byte[]>>();
            internal readonly List<Tuple<long, long, bool>> Allocations = new List<Tuple<long, long, bool>>();
            internal string Frozen = "";
            private long total;
            internal byte[] Read(IntPtr address, int count)
            {
                if ((Reads.Count & 127) == 0) Admitted();
                total = checked(total + count);
                if (total > MaxBytes) throw Error("native input observation exceeds its budget");
                var bytes = M.Read(address, count);
                Reads.Add(Tuple.Create(address, bytes));
                return bytes;
            }
            internal void Allocation(IntPtr address, int size, bool owned)
            {
                if (size == 0 && address == IntPtr.Zero) return;
                if (address.ToInt64() <= 0 || size <= 0) throw Error("invalid native allocation");
                var start = address.ToInt64();
                Allocations.Add(Tuple.Create(start, checked(start + size), owned));
            }
            internal void CheckOwnership()
            {
                // Shared borrowed literal text is valid. No mutable allocation
                // may overlap another allocation or the manager itself.
                long lastEnd = 0, lastOwnedEnd = 0;
                foreach (var block in Allocations.OrderBy(row => row.Item1)) {
                    if (block.Item1 < lastOwnedEnd || block.Item3 && block.Item1 < lastEnd)
                        throw Error("overlapping native input allocations");
                    lastEnd = Math.Max(lastEnd, block.Item2);
                    if (block.Item3) lastOwnedEnd = block.Item2;
                }
            }
            internal void RequireUnchanged()
            {
                Admitted();
                if (Frozen != JsonConvert.SerializeObject(Snapshot)) throw Error("native input snapshot changed after capture");
                for (var i = 0; i < Reads.Count; i++) {
                    if ((i & 127) == 0) Admitted();
                    var row = Reads[i];
                    if (!row.Item2.SequenceEqual(M.Read(row.Item1, row.Item2.Length))) throw Error("native input changed during observation");
                }
                Admitted();
            }
        }
        internal static bool OwnsManager(ResidentNativeGlobalObjects.NativeClass type)
            => type != null && type.Rva == NativeTypeRva && type.Name == "InputManager" && type.Namespace == ""
                && type.Size == NativeSize && type.PersistentTypeId == 13 && type.ObjectDerived && type.GlobalManager && !type.Abstract && !type.Stripped;

        internal static Live Capture(ResidentNativeGlobalObjects.CaptureResult globals, Action admitted, Func<IntPtr> readEventOwner)
        {
            if (globals == null || admitted == null) throw new ArgumentNullException();
            admitted();
            var types = globals.Snapshot.Classes.Where(type => type.Rva == NativeTypeRva).ToArray();
            if (types.Length != 1 || !OwnsManager(types[0])) throw Error("native InputManager type differs");
            var key = "resident-native-global/" + types[0].Key;
            if (!globals.ManagerPointers.TryGetValue(key, out var owner) || owner == IntPtr.Zero) throw Error("native InputManager is absent");
            var live = new Live { Admitted = admitted, Engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, Owner = owner,
                ReadEventOwner = readEventOwner ?? throw new ArgumentNullException(nameof(readEventOwner)) };
            if (M.Ptr(live.Read(IntPtr.Add(live.Engine, ContextSlotRva), 8), 0) != owner
                || M.Ptr(live.Read(owner, 8), 0) != IntPtr.Add(live.Engine, VtableRva)
                || M.Ptr(live.Read(IntPtr.Add(live.Engine, VtableRva + 64), 8), 0) != IntPtr.Add(live.Engine, 0x531550))
                throw Error("native InputManager context or actual class differs");
            RequireFunction(live.Engine, 0x531550, 8, "9e865237ed03ffe620f4a513244f800d8de97c4b057733351db3b08cee58f7f6");
            live.Allocation(owner, NativeSize, true);
            var body = live.Read(IntPtr.Add(owner, 48), NativeSize - 48);
            var result = new Data { Manager = key };
            result.AxisStorage = ReadArray(live, body, 0, 184, out var axes, out var axisCount);
            result.Axes = new Axis[axisCount];
            for (var i = 0; i < axisCount; i++) {
                var address = IntPtr.Add(axes, i * 184);
                var row = live.Read(address, 184);
                result.Axes[i] = new Axis {
                    Names = TextOffsets.Select(offset => ReadText(live, IntPtr.Add(address, offset), row, offset)).ToArray(),
                    Fields = M.Slice(row, 40, 4).Concat(M.Slice(row, 128, 50)).ToArray(),
                    Padding = M.Slice(row, 44, 4).Concat(M.Slice(row, 178, 6)).ToArray()
                };
            }
            result.Keys = new[] { 80, 112, 144 }.Select(offset => ReadBitset(live, body, offset - 48)).ToArray();
            result.JoystickStorage = ReadArray(live, body, 200 - 48, 32, out var sticks, out var stickCount);
            result.Joysticks = new Joystick[stickCount];
            for (var i = 0; i < stickCount; i++) {
                var row = live.Read(IntPtr.Add(sticks, i * 32), 32);
                var storage = ReadArray(live, row, 0, 4, out var floats, out var count);
                result.Joysticks[i] = new Joystick { Array = storage, Positions = live.Read(floats, checked(count * 4)) };
            }
            result.Input = ReadText(live, IntPtr.Add(owner, 232), body, 232 - 48);
            result.Composition = ReadText(live, IntPtr.Add(owner, 272), body, 272 - 48);
            result.Scalars = M.Slice(body, 176 - 48, 24).Concat(M.Slice(body, 312 - 48, 40)).ToArray();
            result.Padding = ScalarPadding.Select(offset => result.Scalars[offset]).ToArray();
            foreach (var offset in ScalarPadding) result.Scalars[offset] = 0;
            result.KeyNames = CaptureKeyNames(live);
            live.LegacyIngress = CaptureLegacy(live);
            result.Legacy = live.LegacyIngress.Snapshot;
            result.Events = CaptureEvents(live);
            live.CheckOwnership();
            result.CoverageGaps = RequiredGaps(result);
            live.Snapshot = result;
            live.Frozen = JsonConvert.SerializeObject(result);
            live.RequireUnchanged();
            return live;
        }
        private static Storage ReadArray(Live live, byte[] header, int offset, int stride, out IntPtr storage, out int count)
        {
            storage = M.Ptr(header, offset);
            var size = M.U64(header, offset + 16);
            var tagged = M.U64(header, offset + 24);
            var capacity = tagged >> 1;
            if (size > capacity || capacity > MaxItems || capacity * (ulong)stride > MaxBytes || capacity > 0 && storage == IntPtr.Zero)
                throw Error("native array size/capacity differs");
            count = (int)size;
            var result = new Storage { Label = M.I32(header, offset + 8), Capacity = (int)capacity,
                Owned = (tagged & 1) == 0, Present = storage != IntPtr.Zero, Padding = M.Slice(header, offset + 12, 4) };
            // An allocator can return a non-null address for zero bytes.
            if (storage != IntPtr.Zero) live.Allocation(storage, Math.Max(1, checked((int)capacity * stride)), result.Owned);
            return result;
        }
        private static Bitset ReadBitset(Live live, byte[] body, int offset)
        {
            var bits = M.U64(body, offset + 16);
            var blocks = M.U64(body, offset + 24);
            var ptr = M.Ptr(body, offset + 8);
            if (bits > MaxItems * 32 || blocks != (bits + 31) / 32 || (blocks == 0) != (ptr == IntPtr.Zero))
                throw Error("invalid key bitset storage");
            live.Allocation(ptr, (int)blocks * 4, true);
            return new Bitset { Label = M.I32(body, offset), Bits = (int)bits, Blocks = live.Read(ptr, (int)blocks * 4),
                Padding = M.Slice(body, offset + 4, 4) };
        }
        private static TextValue ReadText(Live live, IntPtr address, byte[] body, int offset)
        {
            var pointer = M.Ptr(body, offset);
            var size = M.U64(body, offset + 24);
            var inline = pointer == IntPtr.Zero;
            var capacity = inline ? 15UL : M.U64(body, offset + 8);
            if (size > 16384 || capacity > MaxBytes || inline && size > 15 || !inline && capacity != 0 && capacity < size)
                throw Error("invalid input string storage");
            var bytes = live.Read(inline ? IntPtr.Add(address, 8) : pointer, (int)size + 1);
            if (bytes[bytes.Length - 1] != 0 || Array.IndexOf(bytes, (byte)0) != bytes.Length - 1) throw Error("input string terminator differs");
            if (!inline) live.Allocation(pointer, checked((int)(capacity == 0 ? size : capacity) + 1), capacity != 0);
            return new TextValue { Value = Utf8.GetString(bytes, 0, (int)size), Inline = inline, Capacity = (int)capacity, Label = M.I32(body, offset + 32),
                Padding = (inline ? M.Slice(body, offset + 9 + (int)size, 15 - (int)size) : M.Slice(body, offset + 16, 8))
                    .Concat(M.Slice(body, offset + 36, 4)).ToArray() };
        }
        private static IEnumerable<TextValue> Texts(Data data) => data.Axes.SelectMany(axis => axis.Names).Concat(new[] { data.Input, data.Composition });
        private static IEnumerable<Tuple<Storage, int>> Arrays(Data data) => new[] { Tuple.Create(data.AxisStorage, data.Axes.Length),
            Tuple.Create(data.JoystickStorage, data.Joysticks.Length) }.Concat(data.Joysticks.Select(row => Tuple.Create(row.Array, row.Positions.Length / 4)));

        internal static void Validate(Data data)
        {
            if (data == null || data.Version != 4 || data.Manager == null || data.Manager.Length > 4096
                || !data.Manager.StartsWith("resident-native-global/195d330/", StringComparison.Ordinal)
                || !data.Manager.EndsWith("//InputManager", StringComparison.Ordinal) || data.Manager.Split('/').Length != 5 || data.Manager.IndexOf('\0') >= 0
                || data.Axes == null || data.Axes.Length > MaxItems || data.Keys == null || data.Keys.Length != 3
                || data.Joysticks == null || data.Joysticks.Length > MaxItems || data.Scalars == null || data.Scalars.Length != 64
                || data.Padding == null || data.Padding.Length != ScalarPadding.Length || ScalarPadding.Any(offset => data.Scalars[offset] != 0)
                || ScalarBools.Any(offset => data.Scalars[offset] > 1) || data.CoverageGaps == null || data.CoverageGaps.Length > 32
                || data.CoverageGaps.Any(gap => string.IsNullOrEmpty(gap) || gap.Length > 1024)) throw Error("native input schema differs");
            long size = NativeSize + data.Axes.Length * 184L + data.Joysticks.Length * 32L;
            size += ValidateKeyNames(data.KeyNames);
            size += ValidateLegacy(data.Legacy);
            size += ValidateEvents(data.Events);
            foreach (var axis in data.Axes)
                if (axis == null || axis.Fields == null || axis.Fields.Length != 54 || axis.Fields[52] > 1 || axis.Fields[53] > 1
                    || axis.Padding == null || axis.Padding.Length != 10 || axis.Names == null || axis.Names.Length != 3) throw Error("axis schema differs");
            foreach (var key in data.Keys) {
                if (key == null || key.Label < 0 || key.Label > 4095 || key.Bits < 0 || key.Bits > MaxItems * 32 || key.Blocks == null
                    || key.Blocks.Length != (key.Bits + 31) / 32 * 4 || key.Padding == null || key.Padding.Length != 4) throw Error("key block schema differs");
                size += key.Blocks.Length;
            }
            foreach (var stick in data.Joysticks) {
                if (stick == null || stick.Positions == null || stick.Positions.Length % 4 != 0 || stick.Positions.Length / 4 > MaxItems)
                    throw Error("joystick row schema differs");
                size += stick.Positions.Length;
            }
            foreach (var array in Arrays(data))
                if (array.Item1 == null || array.Item1.Label < 0 || array.Item1.Label > 4095 || array.Item1.Capacity < array.Item2
                    || array.Item1.Capacity > MaxItems || array.Item1.Capacity > 0 && !array.Item1.Present
                    || array.Item1.Padding == null || array.Item1.Padding.Length != 4) throw Error("array schema differs");
            foreach (var text in Texts(data)) {
                if (text == null || text.Value == null || text.Value.Length > 16384 || text.Value.IndexOf('\0') >= 0 || text.Label < 0
                    || text.Label > 4095 || text.Padding == null || text.Capacity < 0 || text.Capacity > MaxBytes) throw Error("text schema differs");
                var length = Utf8.GetByteCount(text.Value);
                if (length > 16384 || text.Inline && (length > 15 || text.Capacity != 15 || text.Padding.Length != 19 - length)
                    || !text.Inline && (text.Capacity != 0 && text.Capacity < length || text.Padding.Length != 12)) throw Error("text storage differs");
                size += length;
                if (size > MaxBytes) throw Error("input snapshot exceeds its budget");
            }
        }
        private static string[] BodyGaps(Data data, bool canonical)
        {
            Validate(data);
            var gaps = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var array in Arrays(data)) {
                if (array.Item1.Label != 74) gaps.Add("native-input/array-allocator-label");
                if (!array.Item1.Owned && array.Item1.Present) gaps.Add("native-input/borrowed-array-alias");
                if (canonical && (!array.Item1.Owned || array.Item1.Capacity != array.Item2 || array.Item1.Present != (array.Item2 > 0) || array.Item1.Padding.Any(x => x != 0)))
                    gaps.Add("native-input/array-storage-canon");
            }
            if (data.Keys.Any(value => value.Label != 9)) gaps.Add("native-input/bitset-allocator-label");
            // The platform SetKeyState and button consumers index blocks
            // directly. The constructor reserves the ABI's entire key domain.
            var keyLimit = data.Keys.Min(value => value.Bits);
            gaps.UnionWith(KeyNameGaps(data.KeyNames, keyLimit, canonical));
            gaps.UnionWith(LegacyGaps(data.Legacy, keyLimit, canonical));
            gaps.UnionWith(EventGaps(data.Events, canonical).Where(gap => gap != EventAdmissionGap));
            if (keyLimit < 0x29e) gaps.Add("native-input/unchecked-engine-key-domain");
            foreach (var axis in data.Axes) {
                var type = M.I32(axis.Fields, 24);
                if (type == 0 && new[] { 4, 8, 12, 16 }.Any(i => M.I32(axis.Fields, i) < 0 || M.I32(axis.Fields, i) >= keyLimit))
                    gaps.Add("native-input/unchecked-axis-key-index");
                if (type == 2 && (M.I32(axis.Fields, 20) < 0 || M.I32(axis.Fields, 36) < 0))
                    gaps.Add("native-input/unchecked-negative-joystick-index");
            }
            if (canonical) {
                if (data.Padding.Concat(data.Axes.SelectMany(axis => axis.Padding)).Concat(data.Keys.SelectMany(key => key.Padding)).Any(value => value != 0))
                    gaps.Add("native-input/body-padding-canon");
                if (Texts(data).Any(value => {
                    var length = Utf8.GetByteCount(value.Value);
                    return value.Label != 72 || value.Inline != (length < 16) || value.Capacity != (value.Inline ? 15 : length) || value.Padding.Any(x => x != 0);
                })) gaps.Add("native-input/text-storage-canon");
            }
            return gaps.ToArray();
        }
        internal static string[] RequiredGaps(Data data) => ExternalDomains.Concat(new[] { EventAdmissionGap }).Concat(BodyGaps(data, false)).ToArray();
        internal static void RequireBodyCanonicalizable(Data data) => CheckGaps(BodyGaps(data, false));
        // The common transaction must reject missing producer admission before
        // it frees an old array. A stable second read only witnesses the past.
        internal static void RequireCanonicalizable(Data data) => CheckGaps(RequiredGaps(data).Concat(data.CoverageGaps).Distinct());
        internal static void RequireBodyComplete(Data data) => CheckGaps(BodyGaps(data, true));
        internal static void RequireComplete(Data data)
        {
            RequireBodyComplete(data);
            CheckGaps(RequiredGaps(data).Concat(data.CoverageGaps).Distinct());
        }
        private static void CheckGaps(IEnumerable<string> gaps)
        {
            var list = gaps.ToArray();
            if (list.Length != 0) throw Error(string.Join(";", list));
        }
        // QueueInputEvent and SendInputEventsToScript have a common epoch.
        // Platform/legacy producers and direct event-state writes must also
        // be enrolled before any FIFO or allocator can publish a new buffer.
        internal static void RequireInputProducerAdmission() => CheckGaps(ExternalDomains.Concat(new[] { EventAdmissionGap }));
        internal static byte[] EncodeS0(Data data)
        {
            RequireBodyComplete(data);
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Utf8);
            writer.Write("native-input-body-and-accepted-queues-v4/" + data.Manager);
            EncodeLegacyDependencies(writer, data.Legacy);
            writer.Write(data.Events.Present);
            writer.Flush();
            return stream.ToArray();
        }
        internal static byte[] EncodeValues(Data data)
        {
            Validate(data);
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Utf8);
            writer.Write(data.Manager); writer.Write(data.Scalars);
            writer.Write(data.Input.Value); writer.Write(data.Composition.Value);
            foreach (var key in data.Keys) { writer.Write(key.Bits); writer.Write(key.Blocks); }
            writer.Write(data.Axes.Length);
            foreach (var axis in data.Axes) {
                foreach (var text in axis.Names) writer.Write(text.Value);
                writer.Write(axis.Fields);
            }
            writer.Write(data.Joysticks.Length);
            foreach (var stick in data.Joysticks) { writer.Write(stick.Positions.Length); writer.Write(stick.Positions); }
            EncodeKeyNames(writer, data.KeyNames);
            EncodeLegacy(writer, data.Legacy);
            EncodeEvents(writer, data.Events);
            writer.Flush(); return stream.ToArray();
        }
        internal static void RequireEqual(Data source, Data actual)
        {
            if (!EncodeS0(source).SequenceEqual(EncodeS0(actual)) || !EncodeValues(source).SequenceEqual(EncodeValues(actual)))
                throw Error("independent native InputManager body readback differs");
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Destroy(IntPtr storage);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Resize(IntPtr storage, UIntPtr bits, byte value);
        private sealed class Functions
        {
            internal readonly Destroy Axes, Joysticks, Text;
            internal readonly Resize Keys;
            internal Functions(IntPtr engine)
            {
                T Bind<T>(int rva, int length, string hash) where T : Delegate {
                    RequireFunction(engine, rva, length, hash);
                    return (T)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, rva), typeof(T));
                }
                Axes = Bind<Destroy>(0x5302a0, 135, "5ef397d89dce141456453bb66c987f92b09ab040ef7818ef6044913287d29213");
                Joysticks = Bind<Destroy>(0x257b10, 113, "9e1c833335e59c5e5e00ab332cc3aa478074d8e8757f631a6ba133874917d277");
                Text = Bind<Destroy>(0x8edc0, 40, "e0f14cbde4f4b8e90c6ae11619489f3285ec24f1721db49c52becad89b0f9f18");
                Keys = Bind<Resize>(0x1c79a0, 459, "cb4330ce035b5fd876ed0d34c3a072bb0c23a38f0bd17ca62ddf1415f07ce62b");
            }
        }
        private static IntPtr NewArray(IntPtr header, int count, int stride, int alignment, Action admitted)
        {
            var empty = new byte[32];
            Buffer.BlockCopy(BitConverter.GetBytes(74), 0, empty, 8, 4);
            // New empty storage starts borrowed/null per the native constructor.
            // reserve converts it to an owned array of exactly the requested size.
            if (count != 0) empty[24] = 1;
            M.Write(header, empty);
            if (count == 0) return IntPtr.Zero;
            M.Reserve(header, count, stride, alignment, admitted);
            var pointer = M.Ptr(header);
            M.Write(pointer, new byte[checked(stride * count)]);
            M.Write(IntPtr.Add(header, 16), BitConverter.GetBytes((ulong)count));
            return pointer;
        }
        private static void WriteText(IntPtr address, string value, Action admitted)
        {
            var empty = new byte[40];
            Buffer.BlockCopy(BitConverter.GetBytes(72), 0, empty, 32, 4);
            M.Write(address, empty);
            M.WriteString(address, value, admitted);
            var header = M.Read(address, 40);
            var length = Utf8.GetByteCount(value);
            if (M.Ptr(header, 0) == IntPtr.Zero) M.Write(IntPtr.Add(address, 9 + length), new byte[15 - length]);
            else M.Write(IntPtr.Add(address, 16), new byte[8]);
            M.Write(IntPtr.Add(address, 36), new byte[4]);
        }
        private static void Rebuild(IntPtr owner, Data source, Functions functions, NamePlan names, LegacyPlan legacy, EventPlan events, Action admitted)
        {
            var clockAdmission = admitted;
            admitted = () => { clockAdmission(); RequireInputProducerAdmission(); };
            admitted();
            var header = IntPtr.Add(owner, 48);
            functions.Axes(header);
            var storage = NewArray(header, source.Axes.Length, 184, 8, admitted);
            for (var i = 0; i < source.Axes.Length; i++) {
                admitted();
                var address = IntPtr.Add(storage, i * 184);
                var axis = source.Axes[i];
                for (var j = 0; j < TextOffsets.Length; j++) WriteText(IntPtr.Add(address, TextOffsets[j]), axis.Names[j].Value, admitted);
                M.Write(IntPtr.Add(address, 40), M.Slice(axis.Fields, 0, 4));
                M.Write(IntPtr.Add(address, 128), M.Slice(axis.Fields, 4, 50));
            }
            for (var i = 0; i < source.Keys.Length; i++) {
                admitted();
                var key = source.Keys[i];
                var address = IntPtr.Add(owner, 80 + 32 * i);
                functions.Keys(address, new UIntPtr((uint)key.Bits), 0);
                var result = M.Read(address, 32);
                if (M.U64(result, 16) != (ulong)key.Bits || M.U64(result, 24) * 4 != (ulong)key.Blocks.Length)
                    throw Error("native bitset resize failed");
                // GetAnyKey tests whole blocks, including high bits of the last
                // block. Write all bits after the native resize masks its tail.
                M.Write(M.Ptr(result, 8), key.Blocks);
                M.Write(IntPtr.Add(address, 4), new byte[4]);
            }
            header = IntPtr.Add(owner, 200);
            functions.Joysticks(header);
            storage = NewArray(header, source.Joysticks.Length, 32, 8, admitted);
            for (var i = 0; i < source.Joysticks.Length; i++) {
                admitted();
                var row = source.Joysticks[i].Positions;
                var positions = NewArray(IntPtr.Add(storage, i * 32), row.Length / 4, 4, 4, admitted);
                M.Write(positions, row);
            }
            foreach (var slot in new[] { Tuple.Create(232, source.Input), Tuple.Create(272, source.Composition) }) {
                var address = IntPtr.Add(owner, slot.Item1);
                functions.Text(address);
                WriteText(address, slot.Item2.Value, admitted);
            }
            M.Write(IntPtr.Add(owner, 176), M.Slice(source.Scalars, 0, 24));
            M.Write(IntPtr.Add(owner, 312), M.Slice(source.Scalars, 24, 40));
            names.Apply(admitted);
            legacy.Apply(admitted);
            events.Apply(admitted);
            admitted();
        }
        internal static Live Canon(Live before, ResidentNativeGlobalObjects.CaptureResult globals, ResidentMutationScope scope, Action admitted)
        {
            RequireCanonicalizable(before.Snapshot);
            before.RequireUnchanged();
            scope.Require();
            admitted();
            if (BodyGaps(before.Snapshot, true).Length == 0) return before;
            var source = before.Snapshot.Copy();
            var functions = new Functions(before.Engine);
            var names = new NamePlan(before.Engine, source.KeyNames, admitted);
            var legacy = new LegacyPlan(before.LegacyIngress, source.Legacy);
            var events = new EventPlan(before, source.Events, admitted);
            scope.Mutate(() => Rebuild(before.Owner, source, functions, names, legacy, events, admitted));
            var after = Capture(globals, admitted, before.ReadEventOwner);
            RequireBodyComplete(after.Snapshot);
            if (!EncodeValues(source).SequenceEqual(EncodeValues(after.Snapshot))) throw Error("common storage Canon changed input D");
            VerifyNativeQueries(after);
            return after;
        }
        internal static Plan Prepare(Data source, ResidentNativeGlobalObjects.CaptureResult globals, ResidentMutationScope scope, Action admitted, Func<IntPtr> readEventOwner)
            => new Plan(source, globals, scope, admitted, readEventOwner);
        internal sealed class Plan
        {
            private readonly Data source;
            private readonly ResidentNativeGlobalObjects.CaptureResult globals;
            private readonly ResidentMutationScope scope;
            private readonly Action admitted;
            private readonly Live target;
            private readonly Functions functions;
            private readonly NamePlan names;
            private readonly LegacyPlan legacy;
            private readonly EventPlan events;
            private bool attempted, applied;
            internal Plan(Data source, ResidentNativeGlobalObjects.CaptureResult globals, ResidentMutationScope scope, Action admitted, Func<IntPtr> readEventOwner)
            {
                RequireBodyComplete(source);
                this.globals = globals;
                this.scope = scope ?? throw new ArgumentNullException(nameof(scope));
                this.admitted = admitted ?? throw new ArgumentNullException(nameof(admitted));
                this.source = source.Copy();
                target = Capture(globals, admitted, readEventOwner);
                RequireBodyComplete(target.Snapshot);
                if (target.Snapshot.Manager != source.Manager) throw Error("source and target native singleton descriptors differ");
                functions = new Functions(target.Engine);
                names = new NamePlan(target.Engine, this.source.KeyNames, admitted);
                legacy = new LegacyPlan(target.LegacyIngress, this.source.Legacy);
                events = new EventPlan(target, this.source.Events, admitted);
            }
            internal void RequireComplete()
            {
                ResidentNativeInputState.RequireComplete(source);
                ResidentNativeInputState.RequireComplete(target.Snapshot);
            }
            internal void Apply()
            {
                if (attempted) throw Error("native input plan was reused");
                attempted = true;
                RequireComplete();
                target.RequireUnchanged();
                scope.Require(); admitted();
                scope.Mutate(() => Rebuild(target.Owner, source, functions, names, legacy, events, admitted));
                applied = true;
                Verify();
            }
            internal void Verify()
            {
                if (!applied) throw Error("native input plan was not applied");
                scope.Require(); admitted(); RequireInputProducerAdmission();
                var actual = Capture(globals, admitted, target.ReadEventOwner);
                RequireEqual(source, actual.Snapshot);
                VerifyNativeQueries(actual);
            }
        }
        private static InvalidDataException Error(string reason) => new InvalidDataException("Resident InputManager: " + reason);
        private static void RequireFunction(IntPtr engine, int rva, int length, string expected, byte[]? originalPrefix = null)
        {
            var bytes = M.Read(IntPtr.Add(engine, rva), length);
            if (originalPrefix != null) {
                // Only after the native receipt checked the five-byte jump,
                // its exact relay, relocated original and typed wrapper.
                if (originalPrefix.Length != 5 || bytes[0] != 0xe9) throw Error("input entry hook differs");
                Buffer.BlockCopy(originalPrefix, 0, bytes, 0, originalPrefix.Length);
            }
            using var sha = SHA256.Create();
            if (BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() != expected)
                throw Error("pinned helper differs at " + rva.ToString("x"));
        }
    }
}
