using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    // D0-only diagnostic coordinate projection, anchored at actual StartInputAt.
    // Preserve the raw digest/epoch separately; never change a clock or a queue,
    // and never use this projection for write/readback or D2 acceptance.
    internal static class CanonicalBoundaryEpochObservation
    {
        internal static BoundaryDigestSnapshot Project(BoundaryDigestSnapshot raw, CanonicalInputEpoch epoch)
        {
            epoch.RequireControlOrigin();
            if (!raw.Groups.TryGetValue("nativeFrame", out var frame) || frame != epoch.SourceNative.ToString(CultureInfo.InvariantCulture)
                || !raw.Groups.TryGetValue("controlledNativeFrame", out var controlled)
                || controlled != epoch.NativeFramesFromOrigin.ToString(CultureInfo.InvariantCulture))
                throw new InvalidDataException("Raw boundary cursor does not match its actual input epoch.");
            var scheduler = JArray.FromObject(raw.NativeScheduler);
            if (scheduler.Count == 0 || !(scheduler[0] is JObject manager) || (string?)manager["kind"] != "manager"
                || manager["timestamp"]?.Type != JTokenType.Integer || manager["timestamp"]!.Value<uint>() != epoch.SchedulerTimestamp)
                throw new InvalidDataException("Raw boundary scheduler does not match its actual input epoch.");
            manager["timestamp"] = epoch.SchedulerAge(epoch.OriginSchedulerTimestamp);
            var groups = new SortedDictionary<string,string>(raw.Groups, StringComparer.Ordinal) { ["nativeFrame"] = controlled };
            return new BoundaryDigestSnapshot(groups, raw.Roots, raw.Unsupported, scheduler.Select(item => (object)item).ToArray(),
                raw.NativePhysics, raw.CanonicalWaitIssues);
        }
    }
}
