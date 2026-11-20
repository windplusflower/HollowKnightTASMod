using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    /// <summary>Immutable captures; all access is serialized by the Runtime command worker.</summary>
    public sealed class WorldObservationCache
    {
        private const int PageCharacters = 160000;
        private const int CacheCharacters = 32000000;
        private readonly List<Snapshot> snapshots = new List<Snapshot>();
        private readonly List<Details> details = new List<Details>();

        public string AddSnapshot(long nativeFrame, long movieFrame, string metadataJson,
            IEnumerable<Tuple<string, string, string>> objects, string view = "world")
        {
            // Metadata repeats on every page and cannot be paginated as an object.
            // Refuse an oversized header explicitly instead of caching unreadable pages.
            if (Encoding.UTF8.GetByteCount(metadataJson) > 100000)
                throw new InvalidOperationException("World metadata exceeds the 100 KB transport budget; scene names or collection errors are too large for this view.");
            var snapshot = new Snapshot { Id = Guid.NewGuid().ToString("N"), NativeFrame = nativeFrame,
                MovieFrame = movieFrame, Metadata = metadataJson, Objects = objects.ToArray(), View = view };
            snapshot.Characters = metadataJson.Length + snapshot.Objects.Sum(x => (long)x.Item3.Length);
            if (snapshot.Characters > CacheCharacters)
                throw new InvalidOperationException("World capture exceeds the 32-million-character cache budget; narrow the view.");
            snapshots.Add(snapshot);
            while (snapshots.Count > 4 || snapshots.Sum(x => x.Characters) > CacheCharacters)
            {
                // Frequent display refreshes must not evict a world's paginated AI query.
                // Reclaim superseded display captures before other cached snapshots.
                var display = snapshots.Where(x => x.View == "colliders").ToArray();
                snapshots.Remove(display.Length > 1 ? display[0] : snapshots[0]);
            }
            return snapshot.Id;
        }

        public Dictionary<string, string> ReadSnapshot(string id, int offset, int limit)
        {
            var snapshot = snapshots.FirstOrDefault(x => x.Id == id)
                ?? throw new InvalidOperationException("Snapshot expired or belongs to another session; capture a new snapshot.");
            if (offset < 0 || offset > snapshot.Objects.Length || limit < 1 || limit > 128)
                throw new ArgumentOutOfRangeException(nameof(offset));
            var root = new JObject { ["schemaVersion"] = 1, ["snapshotId"] = id,
                ["nativeFrame"] = snapshot.NativeFrame, ["movieFrame"] = snapshot.MovieFrame,
                ["metadata"] = JObject.Parse(snapshot.Metadata), ["total"] = snapshot.Objects.Length,
                ["offset"] = offset };
            var array = new JArray();
            int used = root.ToString(Formatting.None).Length;
            int index = offset;
            for (; index < snapshot.Objects.Length && index - offset < limit; index++)
            {
                var item = snapshot.Objects[index];
                var json = item.Item3;
                if (json.Length > PageCharacters / 2)
                    json = ReduceOverview(item.Item1, item.Item2, json);
                if (array.Count > 0 && used + json.Length > PageCharacters) break;
                array.Add(JObject.Parse(json));
                used += json.Length;
            }
            root["objects"] = array;
            root["nextOffset"] = index < snapshot.Objects.Length ? index : -1;
            var result = Frames(snapshot.NativeFrame, snapshot.MovieFrame);
            result["snapshotId"] = id;
            result["snapshotJson"] = root.ToString(Formatting.None);
            return result;
        }

        private static string ReduceOverview(string id, string kind, string json)
        {
            // Project only the collector's known schema. Do not recursively prune arbitrary
            // component/FSM data: its complete representation remains in object details.
            var source = JObject.Parse(json);
            var candidates = new JObject { ["id"] = id, ["kind"] = kind };
            string[] fields = { "id", "kind", "name", "path", "scene", "activeSelf", "activeInHierarchy",
                "transform", "hero", "rigidbodies", "health", "damageHero", "layer", "layerName", "tag",
                "parentId", "siblingIndex", "fsms", "colliders", "components", "renderers", "particles", "errors" };
            foreach (var field in fields.Skip(2))
                if (source[field] != null) candidates[field] = source[field]!.DeepClone();
            if (source["hero"] is JObject hero)
                candidates["hero"] = ProjectFields(hero,
                    new[] { "resources", "actor", "charms", "cState", "cooldowns", "abilities" }, 32000);
            var reductions = new JObject
            {
                ["originalCharacters"] = json.Length, ["maximumCharacters"] = PageCharacters / 2,
                ["policy"] = "Known core fields retained within budget; omitted values are marked in place. Query getObjectDetails for the complete object.",
                ["otherTopLevelFieldsOmitted"] = source.Properties().Count(x => !fields.Contains(x.Name))
            };
            if (source["fsms"] is JArray fsms)
            {
                candidates["fsms"] = ProjectComponents(fsms, false, out var reduction);
                reductions["fsms"] = reduction;
            }
            if (source["colliders"] is JArray colliders)
            {
                candidates["colliders"] = ProjectComponents(colliders, true, out var reduction);
                reductions["colliders"] = reduction;
            }
            var result = ProjectFields(candidates, fields, PageCharacters / 2 - 2048,
                field => field == "hero" ? 32000 : field == "fsms" || field == "colliders" ? 12000
                    : field == "rigidbodies" || field == "health" ? 8192 : 4096);
            result["detailsRequired"] = true;
            result["omission"] = "Overview exceeds transport budget; this core summary is reduced. Query getObjectDetails.";
            result["overviewReduction"] = reductions;
            var reduced = result.ToString(Formatting.None);
            if (reduced.Length > PageCharacters / 2)
                throw new InvalidOperationException("Reduced world overview exceeds its transport budget.");
            return reduced;
        }

        private static JArray ProjectComponents(JArray source, bool colliders, out JObject reduction)
        {
            var result = new JArray();
            int used = 2;
            string[] fields = colliders
                ? new[] { "componentIndex", "instanceId", "type", "enabled", "activeInHierarchy", "isTrigger",
                    "usedByComposite", "layer", "layerName", "classification", "offset", "bounds", "definition",
                    "geometry", "worldPaths", "screenPaths" }
                : new[] { "componentIndex", "name", "activeState", "enabled", "activeInHierarchy", "initialized", "variableCount" };
            foreach (var token in source.Take(64))
            {
                JObject projected;
                if (token is JObject component)
                {
                    var candidate = new JObject();
                    foreach (var field in fields)
                        if (component[field] != null) candidate[field] = component[field]!.DeepClone();
                    if (colliders && component["definition"] is JObject definition)
                        candidate["definition"] = ProjectFields(definition,
                            new[] { "size", "radius", "direction", "edgeRadius", "geometryType", "pathCount", "pointCount",
                                "worldRadius", "worldRadiusSource", "worldAxis", "worldHalfLine", "worldFit", "localPaths", "points" }, 4096);
                    projected = ProjectFields(candidate, fields, colliders ? 8192 : 4096);
                }
                else projected = OverviewOmission(token, "unexpectedComponentShape");
                int length = projected.ToString(Formatting.None).Length + 1;
                if (used + length > 12000) break;
                result.Add(projected); used += length;
            }
            reduction = new JObject { ["originalCount"] = source.Count, ["returnedCount"] = result.Count,
                ["omittedCount"] = source.Count - result.Count,
                ["scope"] = colliders ? "Collider identity, bounds and definitions; large definition/path fields are marked omitted."
                    : "FSM identity, enabled flag and active state only; variables and graph require object details.",
                ["omittedReason"] = source.Count > result.Count ? "overviewCollectionBudget; query getObjectDetails" : null };
            return result;
        }

        private static JObject ProjectFields(JObject source, string[] fields, int budget, Func<string, int>? fieldLimit = null)
        {
            var result = new JObject();
            int used = 2;
            for (int index = 0; index < fields.Length; index++)
            {
                var field = fields[index];
                var value = source[field];
                if (value == null) continue;
                int length = value.ToString(Formatting.None).Length;
                int propertyOverhead = field.Length + 4;
                // Reserve a bounded omission marker for every remaining known property.
                int available = Math.Max(0, budget - used - propertyOverhead - (fields.Length - index - 1) * 160);
                if (fieldLimit != null) available = Math.Min(available, fieldLimit(field));
                var retained = length <= available ? value.DeepClone() : OverviewOmission(value, "overviewFieldBudget");
                result[field] = retained;
                used += propertyOverhead + retained.ToString(Formatting.None).Length;
            }
            return result;
        }

        private static JObject OverviewOmission(JToken value, string reason) => new JObject
        {
            ["omitted"] = true, ["reason"] = reason, ["detailsRequired"] = true,
            ["originalCharacters"] = value.ToString(Formatting.None).Length,
            ["count"] = value is JArray array ? (int?)array.Count : null
        };

        public string AddDetails(string objectId, long nativeFrame, long movieFrame, string json)
        {
            if (json.Length > CacheCharacters) throw new InvalidOperationException("Object details exceed the cache budget.");
            string hash;
            using (var sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(json))).Replace("-", "").ToLowerInvariant();
            var item = new Details { Id = Guid.NewGuid().ToString("N"), ObjectId = objectId,
                NativeFrame = nativeFrame, MovieFrame = movieFrame, Json = json, Hash = hash };
            details.Add(item);
            while (details.Count > 4 || details.Sum(x => (long)x.Json.Length) > CacheCharacters) details.RemoveAt(0);
            return item.Id;
        }

        public Dictionary<string, string> ReadDetails(string id, string objectId, long? expectedFrame, int cursor, int maxCharacters)
        {
            var item = details.FirstOrDefault(x => x.Id == id)
                ?? throw new InvalidOperationException("Object details expired or belong to another session; capture new details.");
            if (item.ObjectId != objectId) throw new InvalidOperationException("detailsId belongs to a different object.");
            if (expectedFrame.HasValue && item.NativeFrame != expectedFrame.Value)
                throw new InvalidOperationException("Expected native frame differs from the captured details.");
            if (cursor < 0 || cursor > item.Json.Length || maxCharacters < 1024 || maxCharacters > 200000
                || (cursor > 0 && cursor < item.Json.Length && char.IsLowSurrogate(item.Json[cursor])))
                throw new ArgumentOutOfRangeException(nameof(cursor));
            // Worst-case JSON escaping uses six bytes per UTF-16 code unit. Leave room
            // for the IPC envelope even if a Mod exposes a string of control characters.
            var length = Math.Min(Math.Min(maxCharacters, 150000), item.Json.Length - cursor);
            if (length > 0 && cursor + length < item.Json.Length && char.IsHighSurrogate(item.Json[cursor + length - 1])) length--;
            int next = cursor + length < item.Json.Length ? cursor + length : -1;
            var result = Frames(item.NativeFrame, item.MovieFrame);
            result["detailsId"] = id; result["objectId"] = objectId;
            result["detailsJson"] = item.Json.Substring(cursor, length);
            result["cursor"] = cursor.ToString(CultureInfo.InvariantCulture);
            result["nextCursor"] = next.ToString(CultureInfo.InvariantCulture);
            result["totalCharacters"] = item.Json.Length.ToString(CultureInfo.InvariantCulture);
            result["complete"] = next == -1 ? "true" : "false";
            result["sha256"] = item.Hash;
            return result;
        }

        private static Dictionary<string, string> Frames(long nativeFrame, long movieFrame) => new Dictionary<string, string>
        { ["nativeFrame"] = nativeFrame.ToString(CultureInfo.InvariantCulture), ["movieFrame"] = movieFrame.ToString(CultureInfo.InvariantCulture) };
        private sealed class Snapshot
        {
            public string Id = "", Metadata = "", View = "";
            public long NativeFrame, MovieFrame, Characters;
            public Tuple<string, string, string>[] Objects = Array.Empty<Tuple<string, string, string>>();
        }
        private sealed class Details
        {
            public string Id = "", ObjectId = "", Json = "", Hash = "";
            public long NativeFrame, MovieFrame;
        }
    }
}
