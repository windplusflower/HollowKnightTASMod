using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.IO.Compression;
using System.Text;
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

        // This is the production suffix payload serializer. Keep every root
        // member, including bounded independent field diagnostics, in the
        // actual compressed artifact rather than a separate selector audit.
        internal object EncodeSuffix(object metadata)
        {
            // All tokens belong to this synchronous sample, already detached
            // from live game owners by the constructor/FromObject. Write them
            // without reparenting, mutating or cloning the complete inventory.
            var extra = JObject.FromObject(metadata);
            foreach (var property in extra.Properties())
                if (property.Name == "format" || property.Name == "names" || property.Name == "types" || property.Name == "roots")
                    throw new InvalidOperationException("Suffix diagnostic metadata overlaps the original inventory.");
            byte[] content;
            using (var raw = new MemoryStream()) {
                using (var text = new StreamWriter(raw, new UTF8Encoding(false), 4096, true))
                using (var writer = new JsonTextWriter(text) { Formatting = Formatting.None }) {
                    writer.WriteStartObject();
                    writer.WritePropertyName("format"); writer.WriteValue(Format);
                    writer.WritePropertyName("names"); JArray.FromObject(Names).WriteTo(writer);
                    writer.WritePropertyName("types"); JArray.FromObject(Types).WriteTo(writer);
                    writer.WritePropertyName("roots"); Roots.WriteTo(writer);
                    foreach (var property in extra.Properties()) property.WriteTo(writer);
                    writer.WriteEndObject(); writer.Flush();
                }
                content = raw.ToArray();
            }
            if (content.Length > 16 * 1024 * 1024)
                throw new InvalidOperationException("Actual D suffix inventory exceeds the original 16 MiB diagnostic bound.");
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionMode.Compress, true)) gzip.Write(content, 0, content.Length);
            return new { encoding = "gzip-base64-utf8-json-v1", bytes = content.Length,
                sha256 = HollowKnightTAS.Core.Cryptography.Sha256Utility.ComputeHex(content), gzipBase64 = Convert.ToBase64String(compressed.ToArray()) };
        }

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
