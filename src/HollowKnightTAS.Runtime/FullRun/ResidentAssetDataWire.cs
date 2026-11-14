using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Unity value structs expose computed properties such as normalized/center.
    // Serialize their pinned value layout, including private descriptor fields,
    // rather than invoking those properties or rounding floating-point values.
    internal static class ResidentAssetDataWire
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings {
            TypeNameHandling = TypeNameHandling.None, MissingMemberHandling = MissingMemberHandling.Error, MaxDepth = 128,
            Converters = { new UnityValues() }
        };
        internal static byte[] Encode(ResidentAssetPipeline.Data value) => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, Settings));
        internal static ResidentAssetPipeline.Data Decode(byte[] bytes)
        {
            var document = Newtonsoft.Json.Linq.JObject.Parse(new UTF8Encoding(false,true).GetString(bytes));
            var version = document["Version"];
            if (version == null || version.Type != Newtonsoft.Json.Linq.JTokenType.Integer || (int)version < 2 || (int)version > 4)
                throw new InvalidDataException("Resident asset domain has no supported explicit version.");
            return document.ToObject<ResidentAssetPipeline.Data>(JsonSerializer.Create(Settings))
                ?? throw new InvalidDataException("Resident asset domain is absent.");
        }
        internal static string Values(object value) => JsonConvert.SerializeObject(value, Settings);
        internal static string PortableValues(ResidentNativeAssetState.Snapshot[] snapshots)
        {
            var values = snapshots.Select(snapshot => {
                if (snapshot.Clip == null) return snapshot;
                var copy = snapshot.Copy();
                copy.Clip = ResidentClipDependencyState.PortableValues(snapshot.Clip);
                return copy;
            }).ToArray();
            return Values(values);
        }
        private sealed class UnityValues : JsonConverter
        {
            public override bool CanConvert(Type type)
            {
                var element = type.IsArray && type.GetArrayRank() == 1 ? type.GetElementType()! : type;
                return (element == typeof(float) || element == typeof(double)
                    || element.IsValueType && !element.IsEnum && element.Namespace != null && element.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal))
                    && CanonicalCarryGraphCodec.PackedWidth(element.MakeArrayType()) != 0;
            }
            public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
            {
                if (value == null) { writer.WriteNull(); return; }
                var array = value as Array;
                if (array == null) { array = Array.CreateInstance(value.GetType(), 1); array.SetValue(value, 0); }
                writer.WriteValue(CanonicalCarryGraphCodec.PackedBytes(array));
            }
            public override object? ReadJson(JsonReader reader, Type type, object? existing, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null && type.IsArray) return null;
                if ((type == typeof(float) || type == typeof(double)) && (reader.TokenType == JsonToken.Float || reader.TokenType == JsonToken.Integer
                    || reader.TokenType == JsonToken.String && ((string)reader.Value! == "NaN" || (string)reader.Value! == "Infinity" || (string)reader.Value! == "-Infinity")))
                    return Convert.ChangeType(reader.Value, type, System.Globalization.CultureInfo.InvariantCulture);
                // Earlier resident packets used public-property JSON for value structs.
                if (reader.TokenType == JsonToken.StartObject && !type.IsArray)
                    return Newtonsoft.Json.Linq.JObject.Load(reader).ToObject(type, new JsonSerializer());
                if (reader.TokenType == JsonToken.StartArray && type.IsArray)
                    return Newtonsoft.Json.Linq.JArray.Load(reader).ToObject(type, new JsonSerializer());
                if (reader.TokenType != JsonToken.String) throw new InvalidDataException("Unity asset value must use packed storage.");
                var bytes = Convert.FromBase64String((string)reader.Value!);
                var element = type.IsArray ? type.GetElementType()! : type;
                var width = CanonicalCarryGraphCodec.PackedWidth(element.MakeArrayType());
                if (bytes.Length > ResidentNativeAssetState.MaximumBytes || bytes.Length % width != 0 || !type.IsArray && bytes.Length != width)
                    throw new InvalidDataException("Unity asset value layout or byte count differs.");
                var array = Array.CreateInstance(element, bytes.Length / width);
                CanonicalCarryGraphCodec.WritePacked(bytes, array);
                return type.IsArray ? array : array.GetValue(0);
            }
        }
    }
}
