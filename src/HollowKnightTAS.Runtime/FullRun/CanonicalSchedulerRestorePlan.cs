using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Plans epoch/deadline writes only when the actual prepared queue has the
    // same ordered owner/function/family shape. Changed order requires an
    // original queue operation; this plan never guesses a tree comparator.
    internal sealed class CanonicalSchedulerRestorePlan
    {
        internal uint Timestamp { get; private set; }
        internal Change[] Changes { get; private set; } = Array.Empty<Change>();
        internal sealed class Change
        {
            internal int TargetOrder;
            internal long WakeTimeBits, Frame;
            internal uint Timestamp;
        }
        internal static CanonicalSchedulerRestorePlan Prepare(CanonicalSchedulerCarry source, CanonicalSchedulerCarry prepared)
        {
            source.Validate(); prepared.Validate();
            if (source.Callbacks.Length != prepared.Callbacks.Length || source.Coroutines.Length != prepared.Coroutines.Length)
                throw Invalid("registered queue membership differs");
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            var reverse = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < source.Callbacks.Length; index++) {
                var first = source.Callbacks[index]; var second = prepared.Callbacks[index];
                if (first.Owner != second.Owner || first.Call != second.Call || first.Cleanup != second.Cleanup
                    || first.Rate != second.Rate || first.Mode != second.Mode || first.Repeat != second.Repeat
                    || first.UserDataPresent != second.UserDataPresent || (first.Coroutine.Length == 0) != (second.Coroutine.Length == 0))
                    throw Invalid("ordered callback shape differs at " + index);
                if (first.Coroutine.Length != 0) {
                    if (aliases.TryGetValue(first.Coroutine, out var known) && known != second.Coroutine
                        || reverse.TryGetValue(second.Coroutine, out var original) && original != first.Coroutine)
                        throw Invalid("coroutine aliases differ");
                    aliases[first.Coroutine] = second.Coroutine; reverse[second.Coroutine] = first.Coroutine;
                }
            }
            // A related coroutine without its own callback needs a separately
            // registered native binding. Do not infer it from a type or name.
            if (aliases.Count != source.Coroutines.Length) throw Invalid("coroutine has no explicit queued binding");
            foreach (var first in source.Coroutines) {
                var second = prepared.Coroutines.Single(item => item.Identity == aliases[first.Identity]);
                string Relation(string id) => id == "none" ? id : aliases.TryGetValue(id, out var target) ? target : throw Invalid("unbound relation");
                if (first.Family != second.Family || first.Done != second.Done || first.EnumeratorBased != second.EnumeratorBased
                    || first.HasAsyncOperation != second.HasAsyncOperation || Relation(first.WaitingFor) != second.WaitingFor
                    || Relation(first.ContinueWhenFinished) != second.ContinueWhenFinished)
                    throw Invalid("coroutine family, flags or relations differ");
            }
            return new CanonicalSchedulerRestorePlan { Timestamp = source.Timestamp, Changes = source.Callbacks.Select(item =>
                new Change { TargetOrder = item.Order, WakeTimeBits = item.WakeTimeBits, Frame = item.Frame,
                    Timestamp = unchecked(source.Timestamp - item.TimestampAge) }).ToArray() };
        }
        private static InvalidDataException Invalid(string reason) => new InvalidDataException("Canonical scheduler restore binding differs: " + reason);
    }
}
