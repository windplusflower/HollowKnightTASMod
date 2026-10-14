using System;
using System.IO;
using System.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalSchedulerOwnedStartPartition
    {
        internal static CanonicalSchedulerCarry WithoutOwnedStarts(CanonicalSchedulerCarry source, string ownedPrefix)
        {
            source.Validate();
            if (string.IsNullOrEmpty(ownedPrefix) || !ownedPrefix.EndsWith("/", StringComparison.Ordinal))
                throw new InvalidDataException("Original owned Start directory is absent.");
            var owned = source.Callbacks.Where(value => value.Owner.StartsWith(ownedPrefix, StringComparison.Ordinal)).ToArray();
            if (owned.Any(value => value.Call != "UnityPlayer:8ac680" || value.Cleanup != "none" || value.Coroutine.Length != 0
                || value.UserDataPresent || value.Mode != 7 || value.Frame != -1 || value.Rate != 0 || value.Repeat != 0 || value.TimestampAge != 0))
                throw new InvalidDataException("Owned source directory contains a non-Start callback.");
            var callbacks = source.Callbacks.Where(value => !value.Owner.StartsWith(ownedPrefix, StringComparison.Ordinal))
                .Select((value, index) => new CanonicalSchedulerCarry.Callback { Order = index, Owner = value.Owner, Call = value.Call,
                    Cleanup = value.Cleanup, Coroutine = value.Coroutine, WakeTimeBits = value.WakeTimeBits, Frame = value.Frame,
                    Rate = value.Rate, Mode = value.Mode, Repeat = value.Repeat, TimestampAge = value.TimestampAge,
                    UserDataPresent = value.UserDataPresent }).ToArray();
            if (callbacks.Length + owned.Length != source.Callbacks.Length)
                throw new InvalidDataException("Owned Start partition is incomplete or overlapping.");
            var result = new CanonicalSchedulerCarry { SourceNative = source.SourceNative, Timestamp = source.Timestamp,
                Callbacks = callbacks, Coroutines = source.Coroutines };
            result.Validate(); return result;
        }
    }
}
