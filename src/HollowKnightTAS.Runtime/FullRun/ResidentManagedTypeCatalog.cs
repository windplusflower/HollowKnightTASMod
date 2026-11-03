using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace HollowKnightTAS.Runtime.FullRun
{
    // The type manifest is measured from this snapshot, then checked against
    // modules already loaded in the target process. No payload assembly name
    // is passed to Assembly.Load/Type.GetType and no game constructor runs.
    internal sealed class ResidentManagedTypeCatalog
    {
        internal readonly ManifestData Manifest;
        internal readonly Type[] Types;
        internal readonly string SchemaIdentity;
        private ResidentManagedTypeCatalog(ManifestData manifest, Type[] types)
        {
            Manifest = manifest; Types = types;
            using var hash = SHA256.Create();
            SchemaIdentity = ResidentGraphPolicy.Identity + "/" + BitConverter.ToString(hash.ComputeHash(ManifestBytes(manifest))).Replace("-", "").ToLowerInvariant();
        }

        internal static ResidentManagedTypeCatalog Capture(IReadOnlyDictionary<string, object> roots,
            ResidentGraphPolicy policy, Func<object, string?> externalBinding)
        {
            if (roots == null || roots.Count == 0 || roots.Count > CanonicalCarryGraphCodec.MaximumNodes || policy == null || externalBinding == null)
                throw new InvalidDataException("Resident type discovery roots or policy are absent.");
            var types = new HashSet<Type>();
            var seen = new HashSet<object>(References.Instance);
            var pending = new Queue<Tuple<object, Type, int, string>>();
            var values = 0;
            void AddType(Type type)
            {
                if (types.Contains(type)) return;
                RequireDataType(type, policy);
                if (types.Count >= 16384) throw new InvalidDataException("Resident type manifest exceeds its bound.");
                types.Add(type);
            }
            void Visit(object? value, Type declared, int depth, string path, bool root = false)
            {
                if (++values > CanonicalCarryGraphCodec.MaximumValues || depth > 128)
                    throw new InvalidDataException("Resident type discovery exceeds its value/depth bound at " + path);
                if (value == null) return;
                var type = ResidentValueAccess.TypeOf(value);
                if (IsScalar(type)) { AddType(type); return; }
                if (policy.IsUnityType(type)) {
                    if (!policy.IsDestroyed(value) && policy.BindNativeOwner(value) == null) {
                        if (root || externalBinding(value) == null) throw new InvalidDataException("Resident live native owner lacks a directory or asset binding at " + path);
                        return;
                    }
                } else if (ResidentGraphPolicy.IsMetadata(value)) {
                    if (root || policy.BindMetadata(value) == null) throw new InvalidDataException("Resident metadata cannot be an allocating root at " + path);
                    return;
                } else if (value is not Delegate && !root && externalBinding(value) != null) return;
                AddType(type);
                if ((!type.IsValueType || !declared.IsValueType) && !seen.Add(value)) return;
                if (seen.Count > CanonicalCarryGraphCodec.MaximumNodes) throw new InvalidDataException("Resident type discovery exceeds its object bound at " + path);
                pending.Enqueue(Tuple.Create(value, declared, depth, path));
            }
            foreach (var root in roots.OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
                if (string.IsNullOrEmpty(root.Key) || root.Value == null || root.Value.GetType().IsValueType || root.Value is Array || root.Value is Delegate)
                    throw new InvalidDataException("Resident graph roots must be bound reference owners.");
                Visit(root.Value, root.Value.GetType(), 0, root.Key, true);
            }
            while (pending.Count != 0) {
                var item = pending.Dequeue(); var owner = item.Item1; var type = ResidentValueAccess.TypeOf(owner);
                if (owner is Delegate callback) {
                    _ = ResidentGraphPolicy.DescribeDelegate(callback);
                    foreach (var call in callback.GetInvocationList()) Visit(call.Target, typeof(object), item.Item3 + 1, item.Item4 + "/delegate-target");
                } else if (owner is Array array) {
                    if (array.Rank != 1 || array.GetLowerBound(0) != 0 || array.Length > CanonicalCarryGraphCodec.MaximumNodes)
                        throw new InvalidDataException("Resident array shape differs at " + item.Item4);
                    var element = type.GetElementType()!;
                    for (var index = 0; index < array.Length; index++) Visit(ResidentValueAccess.ReadArray(array, index), element, item.Item3 + 1, item.Item4 + "[" + index + "]");
                } else {
                    foreach (var field in policy.ManagedFields(type)) {
                        // Register complete value layouts even when their value
                        // is null (Nullable<T>) or is represented inline later.
                        if (field.FieldType.IsValueType && !field.FieldType.IsPointer && !field.FieldType.IsByRef) AddValueLayout(field.FieldType, new HashSet<Type>());
                        Visit(ResidentValueAccess.ReadField(owner, field), field.FieldType, item.Item3 + 1, item.Item4 + "/" + field.DeclaringType!.FullName + "/" + field.Name);
                    }
                }
            }
            void AddValueLayout(Type type, HashSet<Type> active)
            {
                AddType(type);
                if (IsScalar(type) || !active.Add(type)) return;
                foreach (var field in policy.ManagedFields(type)) if (field.FieldType.IsValueType) AddValueLayout(field.FieldType, active);
                active.Remove(type);
            }
            var ordered = types.OrderBy(CanonicalCarryGraphCodec.Identity, StringComparer.Ordinal).ToArray();
            var manifest = new ManifestData { Types = ordered.Select(type => DescribeEntry(type, policy)).ToArray() };
            _ = ManifestBytes(manifest);
            return new ResidentManagedTypeCatalog(manifest, ordered);
        }

        internal static ResidentManagedTypeCatalog ResolveManifest(ManifestData manifest, ResidentGraphPolicy policy)
        {
            _ = ManifestBytes(manifest);
            if (policy == null || manifest.Version != 1 || manifest.Types == null || manifest.Types.Length == 0 || manifest.Types.Length > 16384)
                throw new InvalidDataException("Resident type manifest shape differs.");
            var types = new Type[manifest.Types.Length]; string? previous = null;
            for (var index = 0; index < types.Length; index++) {
                var entry = manifest.Types[index] ?? throw new InvalidDataException("Resident manifest has an empty type.");
                var type = ResolveType(entry.Type);
                RequireDataType(type, policy);
                var identity = CanonicalCarryGraphCodec.Identity(type);
                if (identity != entry.Identity || previous != null && StringComparer.Ordinal.Compare(previous, identity) >= 0
                    || JsonConvert.SerializeObject(DescribeEntry(type, policy)) != JsonConvert.SerializeObject(entry))
                    throw new InvalidDataException("Resident loaded type or complete field ABI differs: " + entry.Identity);
                types[index] = type; previous = identity;
            }
            return new ResidentManagedTypeCatalog(manifest, types);
        }
        internal static byte[] ManifestBytes(ManifestData manifest)
        {
            if (manifest == null) throw new InvalidDataException("Resident type manifest is absent.");
            var bytes = new UTF8Encoding(false, true).GetBytes(JsonConvert.SerializeObject(manifest, Formatting.None));
            if (bytes.Length == 0 || bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("Resident type manifest exceeds its byte bound.");
            return bytes;
        }
        internal static ManifestData DecodeManifest(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("Resident type manifest bytes differ.");
            try {
                var value = JsonConvert.DeserializeObject<ManifestData>(new UTF8Encoding(false, true).GetString(bytes))
                    ?? throw new InvalidDataException("Resident type manifest is absent.");
                if (!bytes.SequenceEqual(ManifestBytes(value))) throw new InvalidDataException("Resident type manifest is noncanonical.");
                return value;
            } catch (Exception error) when (error is JsonException || error is DecoderFallbackException)
            { throw new InvalidDataException("Resident type manifest is malformed.", error); }
        }
        private static void RequireDataType(Type type, ResidentGraphPolicy policy)
        {
            if (type.ContainsGenericParameters || type.IsPointer || type.IsByRef || type == typeof(IntPtr) || type == typeof(UIntPtr)
                || type.IsInterface || type.IsAbstract || typeof(MemberInfo).IsAssignableFrom(type)
                || typeof(MarshalByRefObject).IsAssignableFrom(type) || type.Assembly.IsDynamic)
                throw new InvalidDataException("Resident graph type requires a native/metadata adapter: " + type.FullName);
            if (policy.IsUnityType(type)) _ = policy.ManagedFields(type);
        }
        private static bool IsScalar(Type type) => type == typeof(string) || type == typeof(decimal) || type.IsEnum
            || type.IsPrimitive && type != typeof(IntPtr) && type != typeof(UIntPtr);
        private static TypeEntry DescribeEntry(Type type, ResidentGraphPolicy policy)
        {
            var kind = ResidentGraphPolicy.IsDelegate(type) ? "delegate" : policy.IsUnityType(type) ? "unity" : type.IsArray ? "array" : IsScalar(type) ? "scalar" : "managed";
            var fields = kind == "delegate" || kind == "array" || kind == "scalar" ? Array.Empty<FieldInfo>() : CanonicalCarryGraphCodec.Fields(type);
            return new TypeEntry { Identity = CanonicalCarryGraphCodec.Identity(type), Type = DescribeType(type), Kind = kind,
                Fields = fields.Select(field => new FieldEntry { DeclaringType = DescribeType(field.DeclaringType!), Token = field.MetadataToken,
                    Name = field.Name, Type = DescribeType(field.FieldType), Attributes = (int)field.Attributes,
                    Storage = field.DeclaringType!.FullName == "UnityEngine.Object" && field.Name == "m_CachedPtr" && field.FieldType == typeof(IntPtr)
                        ? "native-pointer" : policy.InstanceIdTarget(field) is Type target ? "native-instance-id:" + CanonicalCarryGraphCodec.Identity(target) : "managed" }).ToArray() };
        }

        internal static TypeReference DescribeType(Type type)
        {
            if (type == null || type.IsPointer || type.IsByRef || type.Assembly.IsDynamic)
                throw new InvalidDataException("Resident type reference is not stable loaded metadata.");
            // Mono enumerates partially instantiated classes as well as closed
            // classes. A parameter is scoped to its metadata owner; two T's
            // with the same printed name/position need not be the same type.
            if (type.IsGenericParameter) return ResidentMonoGenericMetadata.DescribeParameter(type);
            if (type.IsArray) return new TypeReference { Kind = "array", Name = ReferenceName(type), Rank = type.GetArrayRank(),
                Element = DescribeType(type.GetElementType()!), SzArray = type == type.GetElementType()!.MakeArrayType() };
            var definition = type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericTypeDefinition() : type;
            return new TypeReference { Kind = "type", Name = ReferenceName(type),
                Assembly = definition.Assembly.GetName().Name!, Module = definition.Module.ModuleVersionId.ToString("D"), Token = definition.MetadataToken,
                Arguments = type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericArguments().Select(DescribeType).ToArray() : Array.Empty<TypeReference>() };
        }
        private static string ReferenceName(Type type) => type.IsGenericParameter ? type.Name
            : type.FullName ?? (type.ContainsGenericParameters && type.IsArray ? "open-array"
            : type.IsGenericType && type.ContainsGenericParameters ? type.GetGenericTypeDefinition().FullName : null)
            ?? throw new InvalidDataException("Resident type has no stable metadata name: " + type + " in " + type.Module.Name);
        internal static Type ResolveType(TypeReference source) => ResolveType(source, 0);
        private static Type ResolveType(TypeReference source, int depth)
        {
            if (source == null || depth > 32 || source.Arguments == null || source.Arguments.Length > 64)
                throw new InvalidDataException("Resident type reference shape exceeds its bound.");
            Type type;
            if ((source.Kind == "var" || source.Kind == "mvar") && source.Element == null && source.Rank == 0 && !source.SzArray
                && source.Arguments.Length == 0 && source.Position >= 0) {
                var module = ResolveModule(source.Assembly, source.Module);
                var table = source.Token & unchecked((int)0xff000000);
                if (table != (source.Kind == "mvar" ? 0x06000000 : 0x02000000))
                    throw new InvalidDataException("Resident generic parameter owner token differs.");
                var parameters = source.Kind == "mvar" ? module.ResolveMethod(source.Token).GetGenericArguments()
                    : module.ResolveType(source.Token).GetGenericArguments();
                if (source.Position >= parameters.Length || !parameters[source.Position].IsGenericParameter)
                    throw new InvalidDataException("Resident generic parameter position differs.");
                type = parameters[source.Position];
            } else if (source.Kind == "array" && source.Position == 0 && source.Element != null && source.Rank >= 1 && source.Rank <= 32
                && source.Arguments.Length == 0 && source.Token == 0 && source.Assembly.Length == 0 && source.Module.Length == 0) {
                var element = ResolveType(source.Element, depth + 1);
                if (source.SzArray && source.Rank != 1) throw new InvalidDataException("Resident vector-array rank differs.");
                type = source.SzArray ? element.MakeArrayType() : element.MakeArrayType(source.Rank);
            } else if (source.Kind == "type" && source.Position == 0 && source.Element == null && source.Rank == 0 && !source.SzArray) {
                var module = ResolveModule(source.Assembly, source.Module);
                try { type = module.ResolveType(source.Token); }
                catch (Exception error) when (error is ArgumentException || error is BadImageFormatException)
                { throw new InvalidDataException("Resident type metadata token differs.", error); }
                if (source.Arguments.Length != 0) {
                    if (!type.IsGenericTypeDefinition) throw new InvalidDataException("Resident generic type definition differs.");
                    type = type.MakeGenericType(source.Arguments.Select(argument => ResolveType(argument, depth + 1)).ToArray());
                }
            } else throw new InvalidDataException("Resident type reference kind differs.");
            if (ReferenceName(type) != source.Name) throw new InvalidDataException("Resident type name/token binding differs.");
            return type;
        }
        internal static Module ResolveModule(string assembly, string mvid)
        {
            if (string.IsNullOrEmpty(assembly) || !Guid.TryParseExact(mvid, "D", out var identity))
                throw new InvalidDataException("Resident loaded-module identity differs.");
            var modules = AppDomain.CurrentDomain.GetAssemblies().Where(value => !value.IsDynamic && value.GetName().Name == assembly)
                .SelectMany(value => value.GetLoadedModules()).Where(value => value.ModuleVersionId == identity).ToArray();
            if (modules.Length != 1) throw new InvalidDataException("Resident module is absent, changed or ambiguous: " + assembly);
            return modules[0];
        }

        [Serializable] internal sealed class ManifestData { public int Version = 1; public TypeEntry[] Types = Array.Empty<TypeEntry>(); }
        [Serializable] internal sealed class TypeEntry
        {
            public string Identity = "", Kind = "";
            public TypeReference Type = null!;
            public FieldEntry[] Fields = Array.Empty<FieldEntry>();
        }
        [Serializable] internal sealed class FieldEntry
        {
            public TypeReference DeclaringType = null!, Type = null!;
            public int Token, Attributes;
            public string Name = "", Storage = "";
        }
        [Serializable] internal sealed class TypeReference
        {
            public string Kind = "", Name = "", Assembly = "", Module = "";
            public int Token, Rank;
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public int Position;
            public bool SzArray;
            public TypeReference? Element;
            public TypeReference[] Arguments = Array.Empty<TypeReference>();
        }
        private sealed class References : IEqualityComparer<object>
        {
            internal static readonly References Instance = new References();
            public new bool Equals(object? first, object? second) => ReferenceEquals(first, second);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
