using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Newtonsoft.Json.Linq;
using HollowKnightTAS.Core.SceneCheckpoints;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Data-only adapter. It never reads game properties, constructs live game objects,
    // invokes save hooks, or carries a Unity reference/delegate/process pointer.
    public sealed class CheckpointDataCodec
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly Type[] roots;
        private readonly FieldsResolver resolver;
        public CheckpointDataCodec(IEnumerable<Type> roots, IEnumerable<Type>? valueTypes = null)
        {
            this.roots = roots.ToArray();
            if (this.roots.Length == 0) throw new ArgumentException("Checkpoint data roots are required.", nameof(roots));
            resolver = new FieldsResolver(this.roots, (valueTypes ?? Array.Empty<Type>()).ToArray());
        }
        public byte[] Encode<T>(T value) where T : class
        {
            if (value == null || !roots.Contains(typeof(T)) || value.GetType() != typeof(T))
                throw new InvalidDataException("Unregistered checkpoint root.");
            ValidateGraph(value);
            using (var text = new BoundedTextWriter())
            using (var writer = new JsonTextWriter(text) { Formatting = Formatting.None, Culture = CultureInfo.InvariantCulture })
            {
                Serializer().Serialize(writer, value, typeof(T)); writer.Flush(); return Utf8.GetBytes(text.ToString());
            }
        }
        public T Decode<T>(byte[] bytes) where T : class
        {
            if (!roots.Contains(typeof(T)) || bytes == null || bytes.Length == 0 || bytes.Length > SceneCheckpointPacket.MaximumPayloadBytes)
                throw new InvalidDataException("Invalid checkpoint data root or size.");
            var canonical = Utf8.GetString(bytes);
            using (var text = new StringReader(canonical))
            using (var reader = new JsonTextReader(text) { MaxDepth = 128, Culture = CultureInfo.InvariantCulture })
            {
                var result = Serializer().Deserialize<T>(reader) ?? throw new InvalidDataException("Checkpoint data is null.");
                if (reader.Read() || Utf8.GetString(Encode(result)) != canonical)
                    throw new InvalidDataException("Checkpoint data is incomplete or noncanonical.");
                return result;
            }
        }
        private JsonSerializer Serializer() => JsonSerializer.Create(new JsonSerializerSettings
        {
            ContractResolver = resolver, TypeNameHandling = TypeNameHandling.None,
            PreserveReferencesHandling = PreserveReferencesHandling.All, ReferenceLoopHandling = ReferenceLoopHandling.Serialize,
            MissingMemberHandling = MissingMemberHandling.Error, ObjectCreationHandling = ObjectCreationHandling.Replace,
            NullValueHandling = NullValueHandling.Include, Culture = CultureInfo.InvariantCulture, MaxDepth = 128,
            ReferenceResolverProvider = () => new DataReferences(),
            Converters = new List<JsonConverter> { new PortableArrayConverter(), new FloatBitsConverter() }
        });
        private void ValidateGraph(object root)
        {
            var seen = new HashSet<object>(new ReferenceComparer());
            void Visit(object? value, int depth)
            {
                if (value == null) return;
                if (depth > 128 || seen.Count > 100000) throw new InvalidDataException("Checkpoint data graph exceeds its bound.");
                var type = value.GetType(); resolver.RequireType(type);
                if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)) return;
                if (!type.IsValueType && !seen.Add(value)) return;
                if (value is IDictionary dictionary)
                {
                    var key = type.GetGenericArguments()[0];
                    var comparer = type.GetProperty("Comparer")!.GetValue(value);
                    var expected = typeof(EqualityComparer<>).MakeGenericType(key).GetProperty("Default")!.GetValue(null);
                    if (!Equals(comparer, expected)) throw new InvalidDataException("Custom dictionary comparers need a checkpoint adapter.");
                    foreach (DictionaryEntry entry in dictionary) { Visit(entry.Key, depth + 1); Visit(entry.Value, depth + 1); }
                }
                else if (value is IList list) { if (list.Count > 100000) throw new InvalidDataException("Checkpoint data list is too large."); foreach (var item in list) Visit(item, depth + 1); }
                else foreach (var field in FieldsResolver.Fields(type)) Visit(field.GetValue(value), depth + 1);
            }
            Visit(root, 0);
        }
        private sealed class FieldsResolver : DefaultContractResolver
        {
            private readonly Type[] roots;
            private readonly Type[] values;
            public FieldsResolver(Type[] roots, Type[] values) { this.roots = roots; this.values = values; }
            internal void RequireType(Type type)
            {
                if (type == typeof(IntPtr) || type == typeof(UIntPtr) || type.IsPointer || type.IsByRef
                    || typeof(Delegate).IsAssignableFrom(type))
                    throw new InvalidDataException("Nonportable checkpoint type: " + type.FullName);
                if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)) return;
                if (type.IsArray && type.GetArrayRank() == 1) { RequireType(type.GetElementType()!); return; }
                if (type.IsGenericType)
                {
                    var kind = type.GetGenericTypeDefinition();
                    if (kind == typeof(List<>) || kind == typeof(Nullable<>)) { RequireType(type.GetGenericArguments()[0]); return; }
                    if (kind == typeof(Dictionary<,>) && type.GetGenericArguments()[0] == typeof(string))
                    { RequireType(type.GetGenericArguments()[1]); return; }
                }
                if (typeof(ISerializable).IsAssignableFrom(type)) throw new InvalidDataException("Custom serializers need a checkpoint adapter.");
                for (var parent = type; parent != null; parent = parent.BaseType)
                    if (parent.FullName?.StartsWith("UnityEngine.", StringComparison.Ordinal) == true && !values.Contains(type))
                        throw new InvalidDataException("Unity objects need a stable-binding checkpoint adapter.");
                if (roots.Contains(type) || values.Contains(type)
                    || (roots.Any(r => r.Assembly == type.Assembly) && type.IsDefined(typeof(SerializableAttribute), false))) return;
                throw new InvalidDataException("Unregistered checkpoint data type: " + type.FullName);
            }
            protected override JsonContract CreateContract(Type objectType)
            { RequireType(objectType); var contract = base.CreateContract(objectType); contract.Converter = null; return contract; }
            protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization memberSerialization)
            {
                var result = new List<JsonProperty>();
                foreach (var field in Fields(type))
                {
                    RequireType(field.FieldType);
                    var property = base.CreateProperty(field, MemberSerialization.Fields);
                    property.PropertyName = field.Name; property.Readable = true; property.Writable = true; property.Ignored = false;
                    property.Converter = null; property.ItemConverter = null; property.ShouldSerialize = null;
                    property.GetIsSpecified = null; property.SetIsSpecified = null;
                    property.Required = Required.AllowNull; result.Add(property);
                }
                if (result.Select(p => p.PropertyName).Distinct(StringComparer.Ordinal).Count() != result.Count)
                    throw new InvalidDataException("Ambiguous checkpoint field identity.");
                return result;
            }
            protected override JsonObjectContract CreateObjectContract(Type type)
            {
                var contract = base.CreateObjectContract(type);
                // Detached decoded data must not execute PlayerData constructors or lifecycle callbacks.
#pragma warning disable SYSLIB0050
                if (!type.IsValueType) contract.DefaultCreator = () => FormatterServices.GetUninitializedObject(type);
#pragma warning restore SYSLIB0050
                contract.OnSerializingCallbacks.Clear(); contract.OnSerializedCallbacks.Clear();
                contract.OnDeserializingCallbacks.Clear(); contract.OnDeserializedCallbacks.Clear();
                contract.OnErrorCallbacks.Clear();
                return contract;
            }
            internal static IEnumerable<FieldInfo> Fields(Type type)
            {
                var fields = new List<FieldInfo>();
                for (var current = type; current != null && current != typeof(object); current = current.BaseType)
                    fields.AddRange(current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                return fields.OrderBy(f => f.Name, StringComparer.Ordinal);
            }
        }
        // Json.NET's default preserve-references array reader cannot allocate
        // arrays before resolving aliases. Allocate detached arrays first and
        // register them before reading elements, including cycles through roots.
        private sealed class PortableArrayConverter : JsonConverter
        {
            public override bool CanConvert(Type type) => type.IsArray && type.GetArrayRank() == 1;
            public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
            {
                if (value == null) { writer.WriteNull(); return; }
                var array = (Array)value;
                var references = serializer.ReferenceResolver!;
                writer.WriteStartObject();
                if (references.IsReferenced(serializer, array))
                { writer.WritePropertyName("$ref"); writer.WriteValue(references.GetReference(serializer, array)); writer.WriteEndObject(); return; }
                writer.WritePropertyName("$id"); writer.WriteValue(references.GetReference(serializer, array));
                writer.WritePropertyName("$values"); writer.WriteStartArray();
                foreach (var item in array) serializer.Serialize(writer, item, array.GetType().GetElementType());
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            public override object? ReadJson(JsonReader reader, Type type, object? existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null) return null;
                var token = JToken.Load(reader);
                if (!(token is JObject data)) throw new InvalidDataException("Checkpoint arrays need their portable reference encoding.");
                if (data.Count == 1 && data["$ref"]?.Type == JTokenType.String)
                {
                    var reference = serializer.ReferenceResolver!.ResolveReference(serializer, (string)data["$ref"]!);
                    if (reference.GetType() != type) throw new InvalidDataException("Checkpoint array reference type differs.");
                    return reference;
                }
                if (data.Count != 2 || data["$id"]?.Type != JTokenType.String || !(data["$values"] is JArray values) || values.Count > 100000)
                    throw new InvalidDataException("Invalid checkpoint array shape or bound.");
                var element = type.GetElementType()!; var result = Array.CreateInstance(element, values.Count);
                serializer.ReferenceResolver!.AddReference(serializer, (string)data["$id"]!, result);
                for (var i = 0; i < values.Count; i++) result.SetValue(values[i].ToObject(element, serializer), i);
                return result;
            }
        }
        private sealed class DataReferences : IReferenceResolver
        {
            private readonly Dictionary<string, object> byId = new Dictionary<string, object>(StringComparer.Ordinal);
            private readonly Dictionary<object, string> byObject = new Dictionary<object, string>(new ReferenceComparer());
            private int sequence;
            public bool IsReferenced(object context, object value) => byObject.ContainsKey(value);
            public string GetReference(object context, object value)
            {
                if (byObject.TryGetValue(value, out var id)) return id;
                id = (++sequence).ToString(CultureInfo.InvariantCulture); AddReference(context, id, value); return id;
            }
            public object ResolveReference(object context, string reference) => byId.TryGetValue(reference, out var value)
                ? value : throw new InvalidDataException("Checkpoint reference is absent.");
            public void AddReference(object context, string reference, object value)
            {
                if (byId.Count >= 100000 || !int.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    || id < 1 || id.ToString(CultureInfo.InvariantCulture) != reference || byId.ContainsKey(reference) || byObject.ContainsKey(value))
                    throw new InvalidDataException("Invalid or duplicate checkpoint reference.");
                byId.Add(reference, value); byObject.Add(value, reference);
            }
        }
        private sealed class FloatBitsConverter : JsonConverter
        {
            public override bool CanConvert(Type type) => type == typeof(float) || type == typeof(double);
            public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
            {
                writer.WriteValue(value is float f ? "f32:" + BitConverter.ToUInt32(BitConverter.GetBytes(f), 0).ToString("x8", CultureInfo.InvariantCulture)
                    : "f64:" + BitConverter.ToUInt64(BitConverter.GetBytes((double)value!), 0).ToString("x16", CultureInfo.InvariantCulture));
            }
            public override object ReadJson(JsonReader reader, Type type, object? existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType != JsonToken.String || !(reader.Value is string value)) throw new InvalidDataException("Floating point checkpoint data must use raw bits.");
                if (type == typeof(float) && value.Length == 12 && value.StartsWith("f32:", StringComparison.Ordinal)
                    && uint.TryParse(value.Substring(4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var f))
                    return BitConverter.ToSingle(BitConverter.GetBytes(f), 0);
                if (type == typeof(double) && value.Length == 20 && value.StartsWith("f64:", StringComparison.Ordinal)
                    && ulong.TryParse(value.Substring(4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var d))
                    return BitConverter.ToDouble(BitConverter.GetBytes(d), 0);
                throw new InvalidDataException("Invalid floating point checkpoint bits.");
            }
        }
        private sealed class ReferenceComparer : IEqualityComparer<object>
        { public new bool Equals(object? x, object? y) => ReferenceEquals(x, y); public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj); }
        private sealed class BoundedTextWriter : StringWriter
        {
            public override void Write(char value) { Require(1); base.Write(value); }
            public override void Write(string? value) { Require(value?.Length ?? 0); base.Write(value); }
            public override void Write(char[] buffer, int index, int count) { Require(count); base.Write(buffer, index, count); }
            private void Require(int count) { if (GetStringBuilder().Length > SceneCheckpointPacket.MaximumPayloadBytes / 4 - count) throw new InvalidDataException("Checkpoint data exceeds its encoding bound."); }
        }
    }
}
