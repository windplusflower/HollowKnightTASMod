using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Lossless diagnostic encoding. Keep every record in its original order,
    // including repeated IDs. Names are UTF-16 prefix lengths, not bindings.
    internal sealed class LoadingInventoryRootEncoding
    {
        internal const string Format = "sorted-utf16-prefix-root-names-v1";
        internal object[] Names { get; }
        internal string[] Types { get; }
        internal JArray Roots { get; }

        internal LoadingInventoryRootEncoding(object[] roots)
        {
            if (roots.Length > 100000) throw new InvalidOperationException("P0 diagnostic root encoding budget exceeded.");
            Roots = JArray.FromObject(roots, JsonSerializer.Create(new JsonSerializerSettings
                { NullValueHandling = NullValueHandling.Ignore }));
            var names = new SortedSet<string>(StringComparer.Ordinal);
            var types = new SortedSet<string>(StringComparer.Ordinal);
            foreach (JObject root in Roots)
            {
                if (root["id"]?.Type != JTokenType.String)
                    throw new InvalidOperationException("P0 diagnostic root has no string identity.");
                names.Add((string)root["id"]!);
                if (root["type"]?.Type == JTokenType.String) types.Add((string)root["type"]!);
                else if (root["type"] != null && root["type"]!.Type != JTokenType.Null)
                    throw new InvalidOperationException("P0 diagnostic root type is invalid.");
            }
            var indices = new Dictionary<string, int>(StringComparer.Ordinal);
            var encoded = new List<object>();
            var previous = "";
            foreach (var name in names)
            {
                var prefix = 0;
                while (prefix < previous.Length && prefix < name.Length && previous[prefix] == name[prefix]) prefix++;
                // Do not split a surrogate pair in the readable suffix.
                if (prefix > 0 && prefix < name.Length && char.IsLowSurrogate(name[prefix])) prefix--;
                indices.Add(name, encoded.Count);
                encoded.Add(new { prefix, suffix = name.Substring(prefix) });
                previous = name;
            }
            Names = encoded.ToArray();
            Types = types.ToArray();
            var typeIndices = Types.Select((name, index) => new { name, index })
                .ToDictionary(item => item.name, item => item.index, StringComparer.Ordinal);
            foreach (JObject root in Roots)
            {
                root["id"] = indices[(string)root["id"]!];
                if (root["type"]?.Type == JTokenType.String) root["type"] = typeIndices[(string)root["type"]!];
            }
        }
    }
}
