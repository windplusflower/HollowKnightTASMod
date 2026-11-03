using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentNativeInputState
    {
        // Pinned InputSystemState::eventState. The read-only observation does not
        // dequeue, pump Win32 messages, invoke a user callback, or grant admission.
        private const int EventRootRva = 0x194e800, RingBytes = 0x8000;
        internal const string EventAdmissionGap = "native-input/event-state-writers-outside-QueueInputEvent-and-SendInputEventsToScript";
        private const int StateType = 0x53544154, DeltaType = 0x444c5441, TextType = 0x54455854;
        private const int ImeType = 0x494d4543, ConfigType = 0x44434647, RemovedType = 0x4452454d;
        private const int KeysFormat = 0x4b455953, MouseFormat = 0x4d4f5553, PenFormat = 0x50454e20;
        private const int TouchFormat = 0x544f5543, XInputFormat = 0x58494e50;
        // Source/target portable roles: keyboard, mouse, pen, touchscreen,
        // XInput user 0..3. Zero denotes an unresolved native device ID.
        private static readonly int[] DeviceFormats = { 0, KeysFormat, MouseFormat, PenFormat, TouchFormat,
            XInputFormat, XInputFormat, XInputFormat, XInputFormat };

        internal sealed class EventQueues
        {
            public bool Present, MainExternal, MainAllocated;
            public int Capacity, ReadHead, ReadTail, WriteHead;
            public int MainLabel, MainSize, MainCapacity, Used, UpdateMask;
            public ulong Block, RunningId, TimeOffsetBits;
            public byte[] Padding = new byte[8]; // dynamic_array +12 and InputEventState +100
            // SendInputEventsToScript exposes the available byte count. Even
            // storage above main.size can become visible after a resize within
            // capacity. An untyped nonzero tail cannot cross process boundaries.
            public byte[] NonzeroMainTailDigest = Array.Empty<byte>();
            public bool RingUnusedZero = true;
            public AcceptedEvent[] Main = Array.Empty<AcceptedEvent>(), Background = Array.Empty<AcceptedEvent>();
        }

        internal sealed class AcceptedEvent
        {
            public int Type, Size, Id, Role, Format, StateOffset;
            public ushort UnresolvedDevice;
            public ulong TimeBits;
            // One little-endian scalar per field (or intersecting portion of
            // that field for DLTA). IMEC is UTF-16 code units, including NUL.
            public uint[] Fields = Array.Empty<uint>();
            // Unrecognized payloads are diagnostic hashes, never transferable
            // bytes that might contain a native pointer or foreign handle.
            public byte[] UnsupportedDigest = Array.Empty<byte>();
            public byte[] Alignment = Array.Empty<byte>();
            public bool RingPaddingZero = true;
        }

        private static int Aligned4(int size) => checked(size + 3) & ~3;
        private static void Bounds(bool condition, string name)
        {
            if (!condition) throw Error("accepted native input " + name + " differs");
        }

        private static readonly int[] KeyLanes = Enumerable.Repeat(1, 16).ToArray();
        // Mouse ends with two bytes of packed tail padding, preserved separately
        // from clickCount. Touch phase is a uint16 in this engine build.
        private static readonly int[] MouseLanes = { 4, 4, 4, 4, 4, 4, 2, 2, 2, 2 };
        private static readonly int[] PenLanes = { 4, 4, 4, 4, 4, 4, 4, 4, 2, 2 };
        private static readonly int[] TouchLanes = { 4, 4, 4, 4, 4, 4, 4, 4, 2, 1, 1 };
        private static readonly int[] XInputLanes = { 2, 1, 1, 2, 2, 2, 2 };
        private static int[]? StateLanes(int format)
        {
            switch (format) {
            case KeysFormat: return KeyLanes;
            case MouseFormat: return MouseLanes;
            case PenFormat: return PenLanes;
            case TouchFormat: return TouchLanes;
            case XInputFormat: return XInputLanes;
            default: return null;
            }
        }

        private static int[]? PayloadLanes(AcceptedEvent row)
        {
            switch (row.Type) {
            case ConfigType:
            case RemovedType:
                return row.Size == 20 ? Array.Empty<int>() : null;
            case TextType:
                return row.Size == 24 ? new[] { 4 } : null;
            case ImeType:
                // charCount occupies the first lane and is validated separately.
                if (row.Size < 24 || (row.Size & 1) != 0) return null;
                return new[] { 4 }.Concat(Enumerable.Repeat(2, (row.Size - 24) / 2)).ToArray();
            case StateType:
            case DeltaType:
                var lanes = StateLanes(row.Format);
                if (lanes == null || row.Role == 0 || DeviceFormats[row.Role] != row.Format) return null;
                var stateLength = lanes.Sum();
                if (row.Type == StateType) return row.Size == 24 + stateLength ? lanes : null;
                int bytes = row.Size - 28, begin = row.StateOffset;
                if (bytes <= 0 || begin < 0 || begin > stateLength - bytes) return null;
                var result = new List<int>();
                int cursor = 0;
                foreach (var width in lanes) {
                    var part = Math.Min(cursor + width, begin + bytes) - Math.Max(cursor, begin);
                    if (part > 0) result.Add(part);
                    cursor += width;
                }
                return result.ToArray();
            default: return null;
            }
        }

        private static AcceptedEvent DecodeEvent(byte[] bytes, int at, int n, int role)
        {
            Bounds(n >= 20 && n <= 1024 && at >= 0 && at <= bytes.Length - n
                && BitConverter.ToUInt16(bytes, at + 4) == n && role >= 0 && role < DeviceFormats.Length, "record");
            var row = new AcceptedEvent { Type = M.I32(bytes, at), Size = n, Role = role, TimeBits = M.U64(bytes, at + 8),
                Id = M.I32(bytes, at + 16), UnresolvedDevice = role == 0 ? BitConverter.ToUInt16(bytes, at + 6) : (ushort)0 };
            var start = 20;
            if ((row.Type == StateType || row.Type == DeltaType) && n >= 24) {
                row.Format = M.I32(bytes, at + 20); start = 24;
                if (row.Type == DeltaType && n >= 28) { row.StateOffset = M.I32(bytes, at + 24); start = 28; }
            }
            var lanes = PayloadLanes(row);
            if (lanes == null || row.Type == ImeType && M.I32(bytes, at + 20) != (n - 24) / 2) {
                using var hash = SHA256.Create();
                row.UnsupportedDigest = hash.ComputeHash(bytes, at + 20, n - 20);
                return row;
            }
            row.Fields = new uint[lanes.Length];
            for (int i = 0; i < lanes.Length; i++)
                for (int b = 0; b < lanes[i]; b++) row.Fields[i] |= (uint)bytes[at + start++] << (8 * b);
            Bounds(start == n, "payload extent");
            return row;
        }

        private static int[] ReadDeviceIds(Live live, IntPtr owner)
        {
            var ids = new int[DeviceFormats.Length];
            var header = live.Read(IntPtr.Add(owner, 304), 32); // InputDeviceRegistrationState::callbacks
            var count = M.U64(header, 16); var capacity = M.U64(header, 24) >> 1;
            var ptr = M.Ptr(header, 0);
            Bounds(count <= capacity && capacity <= MaxItems && (capacity == 0 || ptr != IntPtr.Zero), "device callbacks");
            var callbacks = live.Read(ptr, checked((int)count * 24));
            var registered = new Dictionary<int, IntPtr>();
            for (int i = 0; i < callbacks.Length; i += 24) {
                int id = M.I32(callbacks, i);
                Bounds(id > 0 && id <= ushort.MaxValue && !registered.ContainsKey(id), "registered device ID");
                registered.Add(id, M.Ptr(callbacks, i + 8));
            }
            var total = M.I32(live.Read(IntPtr.Add(owner, 264), 4), 0);
            void Bind(int role, int id, IntPtr instance)
            {
                if (id <= 0 || id > total || id > ushort.MaxValue) return;
                var hasCallback = registered.TryGetValue(id, out var callback);
                if (instance == IntPtr.Zero ? !hasCallback : hasCallback && callback == instance) ids[role] = id;
            }
            var win = M.Ptr(live.Read(IntPtr.Add(live.Engine, 0x19bd998), 8), 0);
            if (win != IntPtr.Zero) {
                var body = live.Read(win, 216);
                Bounds(M.Ptr(body, 0) == IntPtr.Add(live.Engine, 0x166ec28), "NewInput actual class");
                var offsets = new[] { 8, 56, 120 }; var vtables = new[] { 0x166ebe0, 0x166ebf8, 0x166ec10 };
                for (int i = 0; i < offsets.Length; i++) {
                    var member = offsets[i]; var at = member + 8;
                    Bounds(M.Ptr(body, member) == IntPtr.Add(live.Engine, vtables[i]), "device callback class");
                    int id = BitConverter.ToUInt16(body, at + 6);
                    if (id != 0 && M.I32(body, at) == StateType && M.I32(body, at + 20) == DeviceFormats[i + 1]
                        && BitConverter.ToUInt16(body, at + 4) == 24 + StateLanes(DeviceFormats[i + 1])!.Sum())
                        Bind(i + 1, id, IntPtr.Add(win, member));
                }
                Bind(4, M.I32(body, 204), IntPtr.Zero);
            }
            var slots = IntPtr.Add(live.Engine, 0x19bd930);
            var pads = live.Read(slots, 96);
            for (int i = 0; i < 4; i++) {
                int at = i * 24;
                if (M.Ptr(pads, at) == IntPtr.Add(live.Engine, 0x166ee30) && M.I32(pads, at + 8) == i)
                    Bind(i + 5, M.I32(pads, at + 16), IntPtr.Add(slots, at));
            }
            Bounds(ids.Skip(1).Where(id => id != 0).Distinct().Count() == ids.Count(id => id != 0), "ambiguous device binding");
            return ids;
        }

        private static AcceptedEvent[] ParseAccepted(byte[] occupied, int count, bool ring, int[] ids)
        {
            Bounds(count >= 0 && count <= MaxItems && count <= occupied.Length / 20, "count");
            var result = new AcceptedEvent[count];
            int cursor = 0;
            for (int i = 0; i < count; i++) {
                int at = ring ? cursor + 4 : Aligned4(cursor);
                Bounds(at <= occupied.Length - 20, "record header");
                int n = BitConverter.ToUInt16(occupied, at + 4);
                Bounds(n >= 20 && n <= 1024 && n <= occupied.Length - at && (!ring || M.I32(occupied, cursor) == n), "nested event length");
                var id = BitConverter.ToUInt16(occupied, at + 6);
                var role = id == 0 ? 0 : Math.Max(0, Array.IndexOf(ids, (int)id));
                var row = DecodeEvent(occupied, at, n, role);
                if (ring) {
                    cursor = at + Aligned4(n);
                    Bounds(cursor <= occupied.Length, "ring alignment");
                    for (int b = at + n; b < cursor; b++) row.RingPaddingZero &= occupied[b] == 0;
                } else {
                    row.Alignment = M.Slice(occupied, cursor, at - cursor);
                    cursor = at + n;
                }
                result[i] = row;
            }
            Bounds(cursor == occupied.Length, "final occupied extent");
            return result;
        }

        private static void CheckCursor(int cursor) => Bounds(cursor >= 0 && cursor < RingBytes && (cursor & 3) == 0, "ring cursor");

        internal static EventQueues CaptureEvents(Live live)
        {
            live.Admitted();
            // Receipt checks both installed jump destinations, relocated
            // prefixes, full original bodies, wrapper ownership and counters.
            var heldOwner = live.ReadEventOwner();
            RequireFunction(live.Engine, 0x292a30, 574, "dfbe7db182391a405c7935619b17dab0342815addeb28bfe2c8a3aeedbc39d06", new byte[] { 0x40,0x55,0x57,0x41,0x54 });
            RequireFunction(live.Engine, 0x293690, 518, "5e1df5e5c953ee952ffd6c5c81fa3b569604a15eb173b525b54302fa3415b3a6", new byte[] { 0x89,0x4c,0x24,0x08,0x53 });
            var owner = M.Ptr(live.Read(IntPtr.Add(live.Engine, EventRootRva), 8), 0);
            Bounds(owner == heldOwner, "event state owner does not match the input fence");
            if (owner == IntPtr.Zero) return new EventQueues();
            var body = live.Read(owner, 112);
            var ring = M.Ptr(body, 0); var main = M.Ptr(body, 56);
            ulong tagged = M.U64(body, 80), length = M.U64(body, 72), capacity = tagged >> 1;
            int count = M.I32(body, 92), used = M.I32(body, 88);
            Bounds(length <= capacity && capacity <= MaxBytes && (capacity == 0 || main != IntPtr.Zero)
                && used >= 0 && (ulong)used <= length && count >= 0 && count <= MaxItems && count <= used / 20, "main extent");
            var q = new EventQueues { Present = true, Capacity = M.I32(body, 16), ReadHead = M.I32(body, 20),
                ReadTail = M.I32(body, 24), WriteHead = M.I32(body, 28), Block = M.U64(body, 40), RunningId = M.U64(body, 48),
                MainLabel = M.I32(body, 64), MainSize = (int)length, MainCapacity = (int)capacity, MainAllocated = main != IntPtr.Zero,
                MainExternal = (tagged & 1) != 0, Used = used, UpdateMask = M.I32(body, 96), TimeOffsetBits = M.U64(body, 104),
                Padding = M.Slice(body, 68, 4).Concat(M.Slice(body, 100, 4)).ToArray() };
            CheckCursor(q.ReadHead); CheckCursor(q.ReadTail); CheckCursor(q.WriteHead);
            Bounds(q.Capacity == RingBytes && ring != IntPtr.Zero && (ring.ToInt64() & 15) == 0
                && M.Ptr(body, 8).ToInt64() == checked(ring.ToInt64() + RingBytes), "ring allocation");
            Bounds(q.WriteHead == q.ReadTail, "in-flight reservation");
            int bytes = (q.ReadTail - q.ReadHead) & (RingBytes - 1);
            var bgCount = M.U64(body, 32);
            Bounds(bgCount <= (ulong)(bytes / 24), "published background count");
            live.Allocation(owner, 424, true); live.Allocation(ring, RingBytes, true);
            if (main != IntPtr.Zero) live.Allocation(main, Math.Max(1, (int)capacity), !q.MainExternal);
            var ids = count != 0 || bgCount != 0 ? ReadDeviceIds(live, owner) : new int[DeviceFormats.Length];
            var first = Math.Min(RingBytes - q.ReadHead, bytes);
            var ringImage = live.Read(ring, RingBytes);
            var occupied = M.Slice(ringImage, q.ReadHead, first).Concat(M.Slice(ringImage, 0, bytes - first)).ToArray();
            q.Background = ParseAccepted(occupied, (int)bgCount, true, ids);
            for (int i = 0; i < RingBytes - bytes; i++) {
                if (ringImage[(q.ReadTail + i) & (RingBytes - 1)] == 0) continue;
                q.RingUnusedZero = false;
                break;
            }
            q.Main = ParseAccepted(live.Read(main, used), count, false, ids);
            var slack = live.Read(IntPtr.Add(main, used), q.MainCapacity - used);
            if (slack.Any(b => b != 0)) {
                using var hash = SHA256.Create();
                q.NonzeroMainTailDigest = hash.ComputeHash(slack);
            }
            ValidateEvents(q);
            live.Admitted();
            return q;
        }

        internal static long ValidateEvents(EventQueues q)
        {
            if (q == null || q.Main == null || q.Background == null || q.Padding == null || q.Padding.Length != 8
                || q.NonzeroMainTailDigest == null || (q.NonzeroMainTailDigest.Length != 0 && q.NonzeroMainTailDigest.Length != 32)
                || !q.Present && !q.RingUnusedZero
                || q.Used == q.MainCapacity && q.NonzeroMainTailDigest.Length != 0
                || q.Main.Length > MaxItems || q.Background.Length > RingBytes / 24) throw Error("accepted event schema differs");
            if (!q.Present) {
                Bounds(q.Capacity == 0 && q.ReadHead == 0 && q.ReadTail == 0 && q.WriteHead == 0 && q.MainLabel == 0
                    && q.MainSize == 0 && q.MainCapacity == 0 && q.Used == 0 && !q.MainExternal && !q.MainAllocated
                    && q.Block == 0 && q.RunningId == 0 && q.TimeOffsetBits == 0 && q.UpdateMask == 0 && q.Padding.All(b => b == 0)
                    && q.Main.Length == 0 && q.Background.Length == 0, "absent event owner");
                return 1;
            }
            CheckCursor(q.ReadHead); CheckCursor(q.ReadTail); CheckCursor(q.WriteHead);
            Bounds(q.Capacity == RingBytes && q.ReadTail == q.WriteHead && q.Block <= 1 && q.MainLabel >= 0 && q.MainLabel <= 4095
                && q.Used >= 0 && q.Used <= q.MainSize && q.MainSize <= q.MainCapacity && q.MainCapacity <= MaxBytes
                && (q.MainCapacity == 0 || q.MainAllocated), "storage schema");
            int mainExtent = ValidateRecords(q.Main, false);
            int ringExtent = ValidateRecords(q.Background, true);
            Bounds(mainExtent == q.Used && ringExtent == ((q.ReadTail - q.ReadHead) & (RingBytes - 1)), "queue extents");
            return 112L + RingBytes + q.MainCapacity;
        }

        private static int ValidateRecords(AcceptedEvent[] rows, bool ring)
        {
            int extent = 0;
            foreach (var row in rows) {
                if (row == null || row.Fields == null || row.Alignment == null || row.UnsupportedDigest == null
                    || row.Role < 0 || row.Role >= DeviceFormats.Length || row.Role != 0 && row.UnresolvedDevice != 0
                    || row.Size < 20 || row.Size > 1024 || row.Fields.Length > 501
                    || (row.Size < 24 || row.Type != StateType && row.Type != DeltaType) && row.Format != 0
                    || (row.Size < 28 || row.Type != DeltaType) && row.StateOffset != 0)
                    throw Error("accepted event record schema differs");
                var lanes = PayloadLanes(row);
                if (row.UnsupportedDigest.Length != 0) {
                    Bounds(row.UnsupportedDigest.Length == 32 && row.Fields.Length == 0, "unresolved payload digest");
                } else {
                    if (lanes == null || row.Fields.Length != lanes.Length) throw Error("unsupported accepted event payload schema");
                    for (int i = 0; i < lanes.Length; i++)
                        Bounds(lanes[i] == 4 || row.Fields[i] < (1u << (8 * lanes[i])), "scalar width");
                    Bounds(row.Type != ImeType || row.Fields[0] == (row.Size - 24) / 2, "IME character count");
                }
                int padding = ring ? Aligned4(row.Size) - row.Size : Aligned4(extent) - extent;
                Bounds(row.Alignment.Length == (ring ? 0 : padding) && (ring && padding > 0 || row.RingPaddingZero), "record alignment");
                extent = checked(extent + (ring ? 4 : 0) + row.Size + padding);
                Bounds(extent <= (ring ? RingBytes - 4 : MaxBytes), "occupied size");
            }
            return extent;
        }

        internal static IEnumerable<string> EventGaps(EventQueues q, bool canonical)
        {
            ValidateEvents(q);
            yield return EventAdmissionGap;
            if (!q.Present) yield break;
            if (q.MainLabel != 43) yield return "native-input/accepted-event-main-allocator-label";
            if (q.MainExternal || q.MainCapacity == 0) yield return "native-input/accepted-event-main-callback-storage";
            if (q.NonzeroMainTailDigest.Length != 0) yield return "native-input/accepted-event-main-untyped-capacity-tail";
            if (canonical && (!q.RingUnusedZero || q.Background.Any(row => !row.RingPaddingZero)))
                yield return "native-input/accepted-event-ring-storage-canon";
            if (q.Main.Concat(q.Background).Any(row => row.Role == 0)) yield return "native-input/accepted-event-device-binding";
            if (q.Main.Concat(q.Background).Any(row => row.UnsupportedDigest.Length != 0)) yield return "native-input/accepted-event-unresolved-payload";
        }

        internal static void EncodeEvents(BinaryWriter writer, EventQueues q)
        {
            ValidateEvents(q);
            writer.Write(q.Present);
            if (!q.Present) return;
            writer.Write(q.Capacity); writer.Write(q.ReadHead); writer.Write(q.ReadTail); writer.Write(q.WriteHead);
            writer.Write(q.Block); writer.Write(q.RunningId); writer.Write(q.MainLabel); writer.Write(q.MainExternal); writer.Write(q.MainAllocated);
            writer.Write(q.MainSize); writer.Write(q.MainCapacity); writer.Write(q.Used); writer.Write(q.UpdateMask);
            writer.Write(q.TimeOffsetBits); writer.Write(q.Padding);
            writer.Write(q.NonzeroMainTailDigest.Length); writer.Write(q.NonzeroMainTailDigest);
            void Records(AcceptedEvent[] rows)
            {
                writer.Write(rows.Length);
                foreach (var row in rows) {
                    writer.Write(row.Type); writer.Write(row.Size); writer.Write(row.TimeBits); writer.Write(row.Id);
                    writer.Write(row.Role); writer.Write(row.UnresolvedDevice); writer.Write(row.Format); writer.Write(row.StateOffset);
                    writer.Write(row.Fields.Length);
                    foreach (var field in row.Fields) writer.Write(field);
                    writer.Write(row.UnsupportedDigest.Length); writer.Write(row.UnsupportedDigest);
                    writer.Write(row.Alignment.Length); writer.Write(row.Alignment);
                }
            }
            Records(q.Main); Records(q.Background);
        }

        private static byte[] EventImage(EventQueues q)
        {
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output);
            EncodeEvents(writer, q);
            return output.ToArray();
        }

        private static byte[] Repack(AcceptedEvent[] rows, bool ring, int[] ids)
        {
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output);
            foreach (var row in rows) {
                var lanes = PayloadLanes(row);
                if (row.Role == 0 || ids[row.Role] <= 0 || lanes == null || row.UnsupportedDigest.Length != 0)
                    throw Error("accepted event cannot bind its target device and payload");
                if (ring) writer.Write(row.Size);
                else writer.Write(row.Alignment);
                long start = output.Position;
                writer.Write(row.Type); writer.Write((ushort)row.Size); writer.Write(checked((ushort)ids[row.Role]));
                writer.Write(row.TimeBits); writer.Write(row.Id);
                if (row.Type == StateType || row.Type == DeltaType) writer.Write(row.Format);
                if (row.Type == DeltaType) writer.Write(row.StateOffset);
                for (int i = 0; i < lanes.Length; i++)
                    for (int b = 0; b < lanes[i]; b++) writer.Write((byte)(row.Fields[i] >> (8 * b)));
                Bounds(output.Position - start == row.Size, "repacked record length");
                // SendInputEventsToScript passes only the inner byte count to
                // memcpy. The reader's aligned stack tail never reaches script.
                if (ring) writer.Write(new byte[Aligned4(row.Size) - row.Size]);
            }
            return output.ToArray();
        }

        internal sealed class EventPlan
        {
            private readonly Live observed;
            private readonly EventQueues source, before;
            private readonly byte[] desired, main, background;
            private readonly IntPtr owner, ring;
            private bool attempted;

            internal EventPlan(Live target, EventQueues source, Action admitted)
            {
                admitted();
                // Seal source and the target-local device bindings before the
                // surrounding transaction releases or modifies any allocation.
                ValidateEvents(source);
                this.source = Newtonsoft.Json.JsonConvert.DeserializeObject<EventQueues>(
                    Newtonsoft.Json.JsonConvert.SerializeObject(source))!;
                desired = EventImage(this.source);
                observed = new Live { Engine = target.Engine, Owner = target.Owner, Admitted = admitted,
                    ReadEventOwner = target.ReadEventOwner, Snapshot = target.Snapshot, Frozen = target.Frozen };
                before = CaptureEvents(observed);
                Bounds(before.Present == source.Present, "target owner presence");
                CheckGaps(EventGaps(source, false).Concat(EventGaps(before, false)).Where(gap => gap != EventAdmissionGap));
                main = Array.Empty<byte>(); background = Array.Empty<byte>();
                if (!before.Present) return;
                owner = M.Ptr(observed.Read(IntPtr.Add(target.Engine, EventRootRva), 8), 0);
                ring = M.Ptr(observed.Read(owner, 8), 0);
                var ids = this.source.Main.Length + this.source.Background.Length == 0 ? new int[DeviceFormats.Length] : ReadDeviceIds(observed, owner);
                main = Repack(this.source.Main, false, ids);
                background = Repack(this.source.Background, true, ids);
                observed.CheckOwnership();
            }

            internal void Apply(Action admitted)
            {
                if (attempted) throw Error("accepted event plan was reused");
                attempted = true;
                // The exclusive receipt must distinguish the target's previous
                // game epoch from new physical input held for later delivery.
                // Queue contents or matching source/target IDs cannot prove it.
                RequireInputProducerAdmission();
                admitted();
                observed.RequireUnchanged();
                if (!source.Present) return;
                var header = IntPtr.Add(owner, 56);
                if (before.MainCapacity != source.MainCapacity) {
                    // No C++ objects in dynamic_array<char>. Size zero permits
                    // exact shrink or growth by the target allocator.
                    M.Write(IntPtr.Add(header, 16), BitConverter.GetBytes(0UL));
                    M.Reserve(header, source.MainCapacity, 1, 1, admitted);
                }
                var actualHeader = M.Read(header, 32);
                Bounds(M.U64(actualHeader, 24) == (ulong)source.MainCapacity * 2 && M.I32(actualHeader, 8) == source.MainLabel,
                    "target allocation after reserve");
                // No source pointer is published. Ring reservation helpers
                // spin on inter-writer publication, so rebuild only under the
                // exclusive native gate, preserving the target buffer/end.
                var mainPtr = M.Ptr(actualHeader, 0);
                Bounds(mainPtr != IntPtr.Zero, "target main allocation");
                M.Write(mainPtr, main);
                M.Write(IntPtr.Add(mainPtr, main.Length), new byte[source.MainCapacity - main.Length]);
                int first = Math.Min(RingBytes - source.ReadHead, background.Length);
                var ringImage = new byte[RingBytes];
                Buffer.BlockCopy(background, 0, ringImage, source.ReadHead, first);
                Buffer.BlockCopy(background, first, ringImage, 0, background.Length - first);
                M.Write(ring, ringImage);
                M.Write(IntPtr.Add(header, 12), M.Slice(source.Padding, 0, 4));
                M.Write(IntPtr.Add(header, 16), BitConverter.GetBytes((ulong)source.MainSize));
                using var scalars = new MemoryStream();
                using var w = new BinaryWriter(scalars);
                w.Write(source.ReadHead); w.Write(source.ReadTail); w.Write(source.WriteHead);
                w.Write((ulong)source.Background.Length); w.Write(source.Block); w.Write(source.RunningId);
                M.Write(IntPtr.Add(owner, 20), scalars.ToArray());
                scalars.SetLength(0);
                w.Write(source.Used); w.Write(source.Main.Length); w.Write(source.UpdateMask); w.Write(source.Padding, 4, 4); w.Write(source.TimeOffsetBits);
                M.Write(IntPtr.Add(owner, 88), scalars.ToArray());
                RequireInputProducerAdmission();
                var actual = CaptureEvents(new Live { Engine = observed.Engine, Owner = observed.Owner, Admitted = admitted,
                    ReadEventOwner = observed.ReadEventOwner });
                CheckGaps(EventGaps(actual, true).Where(gap => gap != EventAdmissionGap));
                Bounds(EventImage(actual).SequenceEqual(desired), "independent readback");
                admitted();
            }
        }
    }
}
