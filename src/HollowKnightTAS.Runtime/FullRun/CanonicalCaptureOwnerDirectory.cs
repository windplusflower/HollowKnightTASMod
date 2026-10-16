using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Share only the completed native inventory within one synchronous Capture.
    // Neither callers nor either external-binding cache can mutate its entries.
    internal sealed class CanonicalCaptureOwnerDirectory
    {
        private readonly object identity, epoch;
        private readonly IReadOnlyDictionary<string, object> entries;
        internal CanonicalCaptureOwnerDirectory(object identity, object epoch, Func<IReadOnlyDictionary<string, object>> capture)
        {
            this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
            this.epoch = epoch ?? throw new ArgumentNullException(nameof(epoch));
            var frozen = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var pair in capture()) {
                if (frozen.Count >= CanonicalCarryGraphCodec.MaximumNodes || string.IsNullOrEmpty(pair.Key) || pair.Value == null)
                    throw new InvalidDataException("Capture native owner directory is invalid or oversized.");
                frozen.Add(pair.Key, pair.Value);
            }
            entries = new ReadOnlyDictionary<string, object>(frozen);
        }
        internal IReadOnlyDictionary<string, object> Require(object expectedIdentity, object expectedEpoch)
        {
            if (!ReferenceEquals(identity, expectedIdentity) || !ReferenceEquals(epoch, expectedEpoch))
                throw new InvalidDataException("Native owner directory belongs to another capture identity or epoch.");
            return entries;
        }
    }
}
