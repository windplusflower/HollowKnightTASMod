using System;
using System.IO;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Raw source data remains authoritative. Ages are diagnostics only: they
    // never reconstruct clock floats, replace an input tick, or move a queue.
    [Serializable]
    internal sealed class CanonicalInputEpoch
    {
        public int Version = 1;
        public long? OriginNative;
        public long SourceNative;
        public ulong CurrentTick;
        public uint SchedulerTimestamp;
        public bool ControlOriginBound;
        public ulong OriginTick;
        public uint OriginSchedulerTimestamp;
        public int InitialTimeBits, CurrentTimeBits, LastUpdateTimeBits;

        internal static CanonicalInputEpoch Capture(long? originNative, long sourceNative, ulong currentTick,
            float initialTime, float currentTime, float lastUpdateTime, uint schedulerTimestamp)
        {
            var result = new CanonicalInputEpoch
            {
                OriginNative = originNative, SourceNative = sourceNative, CurrentTick = currentTick,
                SchedulerTimestamp = schedulerTimestamp, InitialTimeBits = Bits(initialTime),
                CurrentTimeBits = Bits(currentTime), LastUpdateTimeBits = Bits(lastUpdateTime)
            };
            result.Validate(sourceNative);
            return result;
        }

        internal void Validate(long importedNativeFrame)
        {
            if (Version != 1 || !OriginNative.HasValue || OriginNative.Value < 0 || SourceNative < OriginNative.Value
                || importedNativeFrame != SourceNative || !Finite(InitialTimeBits) || !Finite(CurrentTimeBits)
                || !Finite(LastUpdateTimeBits))
                throw new InvalidDataException("Canonical input epoch origin, native continuation or raw time differs.");
        }

        // Bind the saved source cursor to the exact existing HTC1 schema. This
        // validates metadata only and does not claim that its native import ran.
        internal void RequireNativeContinuation(byte[] bytes)
        {
            if (bytes == null || bytes.Length != 1128 || BitConverter.ToUInt32(bytes, 0) != 0x31435448
                || BitConverter.ToInt32(bytes, 4) != 1 || BitConverter.ToInt32(bytes, 8) != bytes.Length
                || BitConverter.ToInt32(bytes, 12) != 0 || BitConverter.ToUInt64(bytes, 80) > long.MaxValue)
                throw new InvalidDataException("Canonical input epoch requires the pinned native continuation.");
            Validate((long)BitConverter.ToUInt64(bytes, 80));
        }

        internal long NativeFramesFromOrigin
        {
            get { Validate(SourceNative); return checked(SourceNative - OriginNative!.Value); }
        }
        internal ulong TickAge(ulong rawEarlierTick) => unchecked(CurrentTick - rawEarlierTick);
        internal uint SchedulerAge(uint rawEarlierTimestamp) => unchecked(SchedulerTimestamp - rawEarlierTimestamp);
        internal void BindControlOrigin(CanonicalInputEpoch origin)
        {
            if (origin == null) throw new InvalidDataException("Canonical control origin is absent.");
            origin.Validate(origin.SourceNative); Validate(SourceNative);
            if (ControlOriginBound || !origin.ControlOriginBound && origin.SourceNative != origin.OriginNative || OriginNative != origin.OriginNative)
                throw new InvalidDataException("Canonical control origin is not the actual input start boundary.");
            OriginTick = origin.ControlOriginBound ? origin.OriginTick : origin.CurrentTick;
            OriginSchedulerTimestamp = origin.ControlOriginBound ? origin.OriginSchedulerTimestamp : origin.SchedulerTimestamp;
            ControlOriginBound = true;
        }
        internal void RequireControlOrigin()
        {
            Validate(SourceNative);
            if (!ControlOriginBound) throw new InvalidDataException("Canonical input tick/scheduler origin is absent.");
        }

        internal void RequireReadback(CanonicalInputEpoch observed, long importedNativeFrame)
        {
            Validate(importedNativeFrame);
            if (observed == null) throw new InvalidDataException("Canonical input epoch readback is absent.");
            observed.Validate(importedNativeFrame);
            if (OriginNative != observed.OriginNative || SourceNative != observed.SourceNative
                || CurrentTick != observed.CurrentTick || SchedulerTimestamp != observed.SchedulerTimestamp
                || InitialTimeBits != observed.InitialTimeBits || CurrentTimeBits != observed.CurrentTimeBits
                || LastUpdateTimeBits != observed.LastUpdateTimeBits || ControlOriginBound != observed.ControlOriginBound
                || OriginTick != observed.OriginTick || OriginSchedulerTimestamp != observed.OriginSchedulerTimestamp)
                throw new InvalidDataException("Canonical input epoch raw readback differs.");
        }

        internal CanonicalInputEpoch Copy() => new CanonicalInputEpoch
        {
            Version = Version, OriginNative = OriginNative, SourceNative = SourceNative, CurrentTick = CurrentTick,
            SchedulerTimestamp = SchedulerTimestamp, InitialTimeBits = InitialTimeBits,
            CurrentTimeBits = CurrentTimeBits, LastUpdateTimeBits = LastUpdateTimeBits,
            ControlOriginBound = ControlOriginBound, OriginTick = OriginTick, OriginSchedulerTimestamp = OriginSchedulerTimestamp
        };
        private static int Bits(float value) => BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
        private static bool Finite(int bits) => (unchecked((uint)bits) & 0x7f800000u) != 0x7f800000u;

        // The caller supplies an actual already-imported native frame and raw
        // readers. Only the source origin is rebound here. A failure after the
        // first write consumes the transaction, so partial writes cannot retry.
        internal sealed class RestoreBinding
        {
            private bool consumed;
            internal bool Consumed => consumed;
            internal void ApplyOnce(CanonicalInputEpoch source, long importedNativeFrame,
                Action<long> restoreSourceOrigin, Func<CanonicalInputEpoch> readback)
            {
                if (consumed) throw new InvalidOperationException("Canonical input epoch restore was already consumed.");
                if (source == null || restoreSourceOrigin == null || readback == null)
                    throw new ArgumentNullException("Canonical input epoch restore binding is absent.");
                var frozen = source.Copy(); frozen.Validate(importedNativeFrame);
                consumed = true;
                restoreSourceOrigin(frozen.OriginNative!.Value);
                frozen.RequireReadback(readback(), importedNativeFrame);
            }
        }
    }
}
