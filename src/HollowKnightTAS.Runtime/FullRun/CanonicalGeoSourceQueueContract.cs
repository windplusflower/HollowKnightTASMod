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
            string prefix, IReadOnlyDictionary<string, OwnerState> owners)
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
            CanonicalSchedulerRestorePlan.Prepare(source, remaining);
            if (source.Timestamp != remaining.Timestamp || source.Callbacks.Where((item, index) =>
                item.WakeTimeBits != retained[index].WakeTimeBits || item.Frame != retained[index].Frame
                || item.TimestampAge != retained[index].TimestampAge).Any())
                throw Invalid("non-Geo source scheduler scalars differ");
            return selected.Select(item => item.Order).ToArray();
        }
        private static bool Within(string owner, string prefix) => owner.StartsWith(prefix + ":", StringComparison.Ordinal)
            || owner.StartsWith(prefix + "/", StringComparison.Ordinal);
        private static InvalidDataException Invalid(string message) => new InvalidDataException("Source Geo pending Start contract differs: " + message);
    }
}
