using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Only the new original Geo producer's redundant pending Starts may be
    // removed to install an explicitly empty source Geo queue. Reactivation
    // uses the pinned original AddToManager, which registers Start before Awake.
    internal static class CanonicalGeoSourceQueueContract
    {
        internal sealed class OwnerState
        {
            internal byte[] Lifecycle = Array.Empty<byte>();
            internal bool Active;
        }
        internal static string ResolveFrozenOwner(object frozenOwner, IntPtr frozenPointer, string frozenIdentity,
            object actualOwner, IntPtr actualPointer, bool loaded)
        {
            if (frozenOwner == null || actualOwner == null || !loaded || frozenPointer == IntPtr.Zero || actualPointer != frozenPointer || !ReferenceEquals(frozenOwner, actualOwner)
                || string.IsNullOrEmpty(frozenIdentity)) throw Invalid("frozen native owner identity changed");
            return frozenIdentity;
        }
        internal static int[] Select(CanonicalSchedulerCarry source, CanonicalSchedulerCarry actual,
            string prefix, IReadOnlyDictionary<string, OwnerState> owners, IReadOnlyCollection<string>? pendingBorderOwners = null)
        {
            source.Validate(); actual.Validate();
            if (string.IsNullOrEmpty(prefix) || owners.Count == 0 || owners.Count > 64
                || owners.Any(pair => !Within(pair.Key, prefix))) throw Invalid("produced owner directory differs");
            if (source.Callbacks.Any(item => Within(item.Owner, prefix)))
                throw Invalid("source Geo queue is not empty");
            var selected = actual.Callbacks.Where(item => Within(item.Owner, prefix)).ToArray();
            if (selected.Length > owners.Count || selected.Select(item => item.Owner).Distinct(StringComparer.Ordinal).Count() != selected.Length)
                throw Invalid("produced Start owner is duplicated");
            foreach (var callback in selected) {
                if (!owners.TryGetValue(callback.Owner, out var owner) || owner.Active || owner.Lifecycle.Length != 5
                    || owner.Lifecycle.Any(value => value > 1) || owner.Lifecycle[1] != 0 || owner.Lifecycle[4] != 0
                    || callback.Call != "UnityPlayer:8ac680" || callback.Cleanup != "none" || callback.UserDataPresent
                    || callback.Coroutine.Length != 0 || callback.Mode != 7 || callback.Frame != -1 || callback.Rate != 0
                    || callback.Repeat != 0 || callback.TimestampAge != 0)
                    throw Invalid("pending node is not an inactive unstarted unregistered produced owner: " + callback.Owner);
            }
            var retained = actual.Callbacks.Where(item => !Within(item.Owner, prefix)).Select((item, index) =>
                new CanonicalSchedulerCarry.Callback { Order = index, Owner = item.Owner, Call = item.Call, Cleanup = item.Cleanup,
                    Coroutine = item.Coroutine, WakeTimeBits = item.WakeTimeBits, Frame = item.Frame, Rate = item.Rate,
                    Mode = item.Mode, Repeat = item.Repeat, TimestampAge = item.TimestampAge, UserDataPresent = item.UserDataPresent }).ToArray();
            var remaining = new CanonicalSchedulerCarry { SourceNative = actual.SourceNative, Timestamp = actual.Timestamp,
                Callbacks = retained, Coroutines = actual.Coroutines };
            var sourceComparison = source;
            var actualComparison = remaining;
            if (pendingBorderOwners != null) {
                sourceComparison = WithoutPendingBorderStarts(source, remaining, pendingBorderOwners);
                actualComparison = WithoutPendingBorderStarts(remaining, source, pendingBorderOwners);
            }
            CanonicalSchedulerRestorePlan.Prepare(sourceComparison, actualComparison);
            if (sourceComparison.Timestamp != actualComparison.Timestamp || sourceComparison.Callbacks.Where((item, index) =>
                item.WakeTimeBits != actualComparison.Callbacks[index].WakeTimeBits || item.Frame != actualComparison.Callbacks[index].Frame
                || item.TimestampAge != actualComparison.Callbacks[index].TimestampAge).Any())
                throw Invalid("non-Geo source scheduler scalars differ");
            return selected.Select(item => item.Order).ToArray();
        }
        private static CanonicalSchedulerCarry WithoutPendingBorderStarts(CanonicalSchedulerCarry current, CanonicalSchedulerCarry other, IReadOnlyCollection<string> names)
        {
            if (names.Count != 9 || names.Distinct(StringComparer.Ordinal).Count() != 9)
                throw Invalid("pending Border requires nine exact frozen original Start identities");
            var selected = current.Callbacks.Where(item => names.Contains(item.Owner)).ToArray();
            var peers = other.Callbacks.Where(item => names.Contains(item.Owner)).ToArray();
            if (selected.Length != 9 || peers.Length != 9 || selected.Select(item => item.Owner).Distinct(StringComparer.Ordinal).Count() != 9
                || peers.Select(item => item.Owner).Distinct(StringComparer.Ordinal).Count() != 9
                || !selected.Select(item => item.Owner).SequenceEqual(peers.Select(item => item.Owner)))
                throw Invalid("pending Border original Start membership differs");
            foreach (var item in selected) {
                var peer = peers.Single(value => value.Owner == item.Owner);
                if (item.Call != "UnityPlayer:8ac680" || item.Cleanup != "none" || item.Coroutine.Length != 0 || item.UserDataPresent
                    || item.Mode != 7 || item.Frame != -1 || item.Rate != 0 || item.Repeat != 0 || item.TimestampAge != 0
                    || peer.Call != item.Call || peer.Cleanup != item.Cleanup || peer.Coroutine != item.Coroutine || peer.UserDataPresent != item.UserDataPresent
                    || peer.Mode != item.Mode || peer.Frame != item.Frame || peer.Rate != item.Rate || peer.Repeat != item.Repeat
                    || peer.TimestampAge != item.TimestampAge || peer.WakeTimeBits != item.WakeTimeBits)
                    throw Invalid("pending Border original callback payload differs: " + item.Owner);
            }
            return new CanonicalSchedulerCarry { SourceNative = current.SourceNative, Timestamp = current.Timestamp, Coroutines = current.Coroutines,
                Callbacks = current.Callbacks.Where(item => !names.Contains(item.Owner)).Select((item, index) =>
                    new CanonicalSchedulerCarry.Callback { Order = index, Owner = item.Owner, Call = item.Call, Cleanup = item.Cleanup,
                        Coroutine = item.Coroutine, WakeTimeBits = item.WakeTimeBits, Frame = item.Frame, Rate = item.Rate, Mode = item.Mode,
                        Repeat = item.Repeat, TimestampAge = item.TimestampAge, UserDataPresent = item.UserDataPresent }).ToArray() };
        }
        private static bool Within(string owner, string prefix) => owner.StartsWith(prefix + ":", StringComparison.Ordinal)
            || owner.StartsWith(prefix + "/", StringComparison.Ordinal);
        private static InvalidDataException Invalid(string message) => new InvalidDataException("Source Geo pending Start contract differs: " + message);
    }
}
