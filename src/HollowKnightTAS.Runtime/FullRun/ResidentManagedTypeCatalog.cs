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

        internal sealed class DiscoveryShape
        {
            internal bool Scalar, Value, Unity, Metadata, Delegate, ElementReferences, List;
            internal Type? Element;
        }

        // One asset-closure observation only. Independent readback starts with a
        // fresh cache, so every live source/target reference is traversed again.
        internal sealed class DiscoveryCache
        {
            internal readonly HashSet<Type> Types = new HashSet<Type>(), ValueLayouts = new HashSet<Type>();
            internal readonly HashSet<object> Seen = new HashSet<object>(References.Instance);
            internal readonly Dictionary<Type, Array> LastTerminalArray = new Dictionary<Type, Array>();
            internal readonly Dictionary<Type, (FieldInfo Field, Type Declared, Func<object, object?> Read)[]> Fields = new Dictionary<Type, (FieldInfo, Type, Func<object, object?>)[]>();
            internal readonly Dictionary<Type, ResidentValueAccess.FirstReference?> FirstReaders = new Dictionary<Type, ResidentValueAccess.FirstReference?>();
            internal readonly Dictionary<Type, DiscoveryShape> Shapes = new Dictionary<Type, DiscoveryShape>();
        }

        internal static ResidentManagedTypeCatalog Capture(IReadOnlyDictionary<string, object> roots,
            ResidentGraphPolicy policy, Func<object, string?> externalBinding, Queue<KeyValuePair<string, object>>? extraRoots = null, Action<string>? recordStage = null, DiscoveryCache? cache = null)
        {
            if (roots == null || roots.Count == 0 || roots.Count > CanonicalCarryGraphCodec.MaximumNodes || policy == null || externalBinding == null)
                throw new InvalidDataException("Resident type discovery roots or policy are absent.");
            cache ??= new DiscoveryCache();
            var types = cache.Types;
            var valueLayouts = cache.ValueLayouts;
            var seen = cache.Seen;
            var lastTerminalArray = cache.LastTerminalArray;
            var pending = new Queue<(object Owner, Type Type, int Depth, string Root, int FirstIndex, object? FirstValue)>();
            var dynamicFields = cache.Fields;
            var firstReaders = cache.FirstReaders;
            var shapes = cache.Shapes;
            DiscoveryShape Shape(Type type)
            {
                if (shapes.TryGetValue(type, out var shape)) return shape;
                var element = type.IsArray ? type.GetElementType() : null;
                shape = new DiscoveryShape {
                    Scalar = IsScalar(type), Value = type.IsValueType, Unity = policy.IsUnityType(type),
                    Metadata = typeof(MemberInfo).IsAssignableFrom(type) || typeof(Assembly).IsAssignableFrom(type) || typeof(Module).IsAssignableFrom(type),
                    Delegate = typeof(Delegate).IsAssignableFrom(type), Element = element,
                    ElementReferences = element != null && ResidentGraphPolicy.CanContainReferences(element),
                    List = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)
                };
                shapes.Add(type, shape); return shape;
            }
            var values = 0;
            var unavailable = new Dictionary<Type, string>();
            FieldInfo? incoming = null;
            Type? arrayOwner = null;
            var arrayIndex = -1;
            void Unsupported(Type type, string reason, string path)
            {
                if (unavailable.ContainsKey(type)) return;
                var detail = reason + " at " + path;
                if (incoming != null) detail += " via " + incoming.DeclaringType!.FullName + "." + incoming.Name;
                else if (arrayOwner != null) detail += " via " + arrayOwner.FullName + "[" + arrayIndex + "]";
                unavailable.Add(type, detail);
                recordStage?.Invoke(detail);
                if (unavailable.Count >= 16) throw new InvalidDataException(string.Join("; ", unavailable.Values));
            }
            void AddType(Type type)
            {
                if (types.Contains(type)) return;
                RequireDataType(type, policy);
                if (types.Count >= 16384) throw new InvalidDataException("Resident type manifest exceeds its bound.");
                types.Add(type);
            }
            (FieldInfo Field, Type Declared, Func<object, object?> Read)[] DynamicFields(Type type, string path)
            {
                if (dynamicFields.TryGetValue(type, out var known)) return known;
                var fields = new List<(FieldInfo, Type, Func<object, object?>)>();
                foreach (var field in policy.ManagedFields(type)) {
                    var declared = field.FieldType;
                    if (declared.IsValueType || declared == typeof(string)) {
                        try { AddValueLayout(declared); }
                        catch (InvalidDataException ex) { Unsupported(declared, ex.Message, path + "/" + field.Name); continue; }
                        if (!ResidentGraphPolicy.CanContainReferences(declared)) continue;
                    }
                    fields.Add((field, declared, ResidentValueAccess.PrepareRead(field)));
                }
                var result = fields.ToArray();
                firstReaders.Add(type, ResidentValueAccess.PrepareFirstReference(type, result.Select(field => field.Item1).ToArray()));
                dynamicFields.Add(type, result); return result;
            }
            void Visit(object? value, Type declared, int depth, string path, bool root = false)
            {
                if (depth > 128) throw new InvalidDataException("Resident type discovery exceeds its depth bound at " + path);
                if (value == null) return;
                var type = ResidentValueAccess.TypeOf(value);
                // Framework List<T> instances commonly share their backing array.
                // Cache one fully inspected leaf per array type without retaining a
                // second directory proportional to the number of array instances.
                if (value is Array candidate && lastTerminalArray.TryGetValue(type, out var previous)
                    && ReferenceEquals(candidate, previous)) return;
                var shape = Shape(type);
                if (shape.Scalar) { AddType(type); return; }
                // Queued reference owners have already passed native binding and layout checks.
                if (!shape.Value && seen.Contains(value)) return;
                if (shape.Unity) {
                    if (!policy.IsDestroyed(value) && policy.BindNativeOwner(value) == null) {
                        if (externalBinding(value) == null) {
                            if (path.StartsWith("resident-static/", StringComparison.Ordinal)) {
                                try { AddType(type); } catch (InvalidDataException ex) { Unsupported(type, ex.Message, path); }
                                return;
                            }
                            if (root) throw new InvalidDataException("Resident live native owner lacks a directory or asset binding at " + path);
                            return;
                        }
                        return;
                    }
                } else if (shape.Metadata) {
                    if (root || policy.BindMetadata(value) == null) throw new InvalidDataException("Resident metadata cannot be an allocating root at " + path);
                    return;
                } else if (!shape.Delegate && !root && !ResidentValueAccess.IsView(value) && externalBinding(value) != null) return;
                try { AddType(type); }
                catch (InvalidDataException ex) { Unsupported(type, ex.Message, path); return; }
                var firstIndex = -1; object? firstValue = null;
                if (value is Array array) {
                    if (array.Rank != 1 || array.GetLowerBound(0) != 0 || array.Length > CanonicalCarryGraphCodec.ResidentArrayLimit(type))
                        throw new InvalidDataException("Resident array shape differs at " + path);
                    if (array.Length == 0) { lastTerminalArray[type] = array; return; }
                    var element = shape.Element!;
                    if (!shape.ElementReferences) { AddValueLayout(element); lastTerminalArray[type] = array; return; }
                } else if (!shape.Delegate) {
                    var fields = DynamicFields(type, path);
                    if (fields.Length == 0) return;
                    // Discover a leaf list's backing array directly. An initialized sync
                    // root or any other non-array edge still needs the ordinary cycle walk.
                    // Graph capture independently assigns identity to every list and array.
                    if (shape.List) {
                        Array? storage = null;
                        var terminal = true;
                        foreach (var field in fields) {
                            var member = field.Read(value);
                            if (member == null) continue;
                            if (member is Array backing && field.Declared.IsArray && storage == null) storage = backing;
                            else { terminal = false; break; }
                        }
                        if (terminal && (storage == null || storage.Length == 0 || !ResidentGraphPolicy.CanContainReferences(storage.GetType().GetElementType()!))) {
                            if (storage != null) Visit(storage, storage.GetType(), depth + 1, path);
                            return;
                        }
                    }
                    // A class with only null outgoing references needs its type manifest,
                    // but no queue entry. Retain the first actual edge to avoid rereading
                    // it and the null prefix when expanding a nonterminal instance.
                    var firstReader = firstReaders[type];
                    if (firstReader != null) {
                        firstIndex = firstReader(value, out firstValue);
                        if (firstIndex < 0) return;
                    } else {
                        while (++firstIndex < fields.Length) {
                            firstValue = fields[firstIndex].Read(value);
                            if (firstValue != null) break;
                        }
                        if (firstIndex == fields.Length) return;
                    }
                }
                if ((!shape.Value || !declared.IsValueType) && !seen.Add(value)) return;
                if (seen.Count > CanonicalCarryGraphCodec.MaximumPackedNodes) throw new InvalidDataException("Resident type discovery exceeds its object bound at " + path + ": " + ResidentGraphPolicy.DescribeNodeBudget(seen));
                // Charge actual layout expansions. Null slots, scalars, external owners and
                // previously visited references contribute no work to the discovery queue.
                if (++values > CanonicalCarryGraphCodec.MaximumPackedValues)
                    throw new InvalidDataException("Resident type discovery exceeds its expansion bound at " + path);
                pending.Enqueue((value, type, depth, path, firstIndex, firstValue));
            }
            foreach (var root in roots.OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
                if (string.IsNullOrEmpty(root.Key) || root.Value == null || root.Value.GetType().IsValueType || root.Value is Array || root.Value is Delegate)
                    throw new InvalidDataException("Resident graph roots must be bound reference owners.");
                Visit(root.Value, root.Value.GetType(), 0, root.Key, true);
            }
            while (pending.Count != 0 || extraRoots?.Count > 0) {
                incoming = null; arrayOwner = null;
                while (extraRoots?.Count > 0) {
                    var root = extraRoots.Dequeue();
                    Visit(root.Value, root.Value.GetType(), 0, root.Key, true);
                }
                if (pending.Count == 0) break;
                var item = pending.Dequeue();
                if (item.Owner is Delegate callback) {
                    try { _ = ResidentGraphPolicy.DescribeDelegate(callback); }
                    catch (InvalidDataException ex) { Unsupported(item.Type, ex.Message, item.Root); continue; }
                    foreach (var call in callback.GetInvocationList()) Visit(call.Target, typeof(object), item.Depth + 1, item.Root);
                } else if (item.Owner is Array array) {
                    var element = item.Type.GetElementType()!;
                    arrayOwner = item.Type;
                    if (array is object[] references) {
                        for (arrayIndex = 0; arrayIndex < references.Length; arrayIndex++) {
                            var reference = references[arrayIndex];
                            if (reference != null) Visit(reference, element, item.Depth + 1, item.Root);
                        }
                    } else {
                        for (arrayIndex = 0; arrayIndex < array.Length; arrayIndex++)
                            Visit(ResidentValueAccess.ReadArray(array, arrayIndex), element, item.Depth + 1, item.Root);
                    }
                } else {
                    var fields = DynamicFields(item.Type, item.Root);
                    for (var index = item.FirstIndex; index < fields.Length; index++) {
                        var entry = fields[index]; incoming = entry.Field;
                        var member = index == item.FirstIndex ? item.FirstValue : entry.Read(item.Owner);
                        if (member != null) Visit(member, entry.Declared, item.Depth + 1, item.Root);
                    }
                }
            }
            void AddValueLayout(Type type)
            {
                if (!valueLayouts.Add(type)) return;
                AddType(type);
                if (IsScalar(type)) return;
                foreach (var field in policy.ManagedFields(type)) if (field.FieldType.IsValueType) AddValueLayout(field.FieldType);
            }
            if (unavailable.Count != 0) throw new InvalidDataException(string.Join("; ", unavailable.Values));
            recordStage?.Invoke("managed discovery complete; expanded=" + values + ";referenceOwners=" + seen.Count + ";types=" + types.Count);
            var ordered = types.OrderBy(CanonicalCarryGraphCodec.Identity, StringComparer.Ordinal).ToArray();
            var metadata = new Dictionary<Type, TypeReference>();
            var manifest = new ManifestData { Version = 2, Types = ordered.Select(type => DescribeEntry(type, policy, metadata)).ToArray() };
            recordStage?.Invoke("managed type manifest complete;references=" + metadata.Count);
            return new ResidentManagedTypeCatalog(manifest, ordered);
        }

        internal static ResidentManagedTypeCatalog ResolveManifest(ManifestData manifest, ResidentGraphPolicy policy)
        {
            _ = ManifestBytes(manifest);
            if (policy == null || (manifest.Version != 1 && manifest.Version != 2) || manifest.Types == null || manifest.Types.Length == 0 || manifest.Types.Length > 16384)
                throw new InvalidDataException("Resident type manifest shape differs.");
            var types = new Type[manifest.Types.Length]; string? previous = null;
            var metadata = new Dictionary<Type, TypeReference>();
            for (var index = 0; index < types.Length; index++) {
                var entry = manifest.Types[index] ?? throw new InvalidDataException("Resident manifest has an empty type.");
                var type = ResolveType(entry.Type);
                RequireDataType(type, policy);
                var identity = CanonicalCarryGraphCodec.Identity(type);
                if (identity != entry.Identity || previous != null && StringComparer.Ordinal.Compare(previous, identity) >= 0
                    || !SameEntry(DescribeEntry(type, policy, metadata), entry))
                    throw new InvalidDataException("Resident loaded type or complete field ABI differs: " + entry.Identity + " (" + DiffEntry(DescribeEntry(type, policy, metadata), entry) + ")");
                types[index] = type; previous = identity;
            }
            return new ResidentManagedTypeCatalog(manifest, types);
        }
        private static string DiffEntry(TypeEntry expected, TypeEntry actual)
        {
            if (expected.Identity != actual.Identity) return "Identity: exp=" + expected.Identity + ", act=" + actual.Identity;
            if (expected.Kind != actual.Kind) return "Kind: exp=" + expected.Kind + ", act=" + actual.Kind;
            if (!SameReference(expected.Type, actual.Type)) return "TypeRef mismatch";
            if (actual.Fields == null || expected.Fields.Length != actual.Fields.Length) return "FieldCount: exp=" + expected.Fields.Length + ", act=" + (actual.Fields == null ? -1 : actual.Fields.Length);
            var isControl = IsControlPlaneAssembly(expected.Type != null ? expected.Type.Assembly : null);
            for (var i = 0; i < expected.Fields.Length; i++) {
                var a = expected.Fields[i]; var b = actual.Fields[i];
                if (b == null) return "Field[" + i + "] is null";
                if (!isControl && a.Token != b.Token) return "Field[" + i + "].Token: exp=" + a.Token + ", act=" + b.Token;
                if (a.Attributes != b.Attributes) return "Field[" + i + "].Attributes: exp=" + a.Attributes + ", act=" + b.Attributes;
                if (a.Name != b.Name) return "Field[" + i + "].Name: exp=" + a.Name + ", act=" + b.Name;
                if (a.Storage != b.Storage) return "Field[" + i + "].Storage: exp=" + a.Storage + ", act=" + b.Storage;
                if (!SameReference(a.Type, b.Type)) return "Field[" + i + "].Type mismatch";
                if (!SameReference(a.DeclaringType, b.DeclaringType)) return "Field[" + i + "].DeclaringType mismatch";
            }
            return "none";
        }
        private static bool SameEntry(TypeEntry expected, TypeEntry actual)
        {
            if (expected.Identity != actual.Identity || expected.Kind != actual.Kind || !SameReference(expected.Type, actual.Type)
                || actual.Fields == null || expected.Fields.Length != actual.Fields.Length) return false;
            var isControl = IsControlPlaneAssembly(expected.Type != null ? expected.Type.Assembly : null);
            for (var i = 0; i < expected.Fields.Length; i++) {
                var a = expected.Fields[i]; var b = actual.Fields[i];
                if (b == null || (!isControl && a.Token != b.Token) || a.Attributes != b.Attributes || a.Name != b.Name || a.Storage != b.Storage
                    || !SameReference(a.Type, b.Type) || !SameReference(a.DeclaringType, b.DeclaringType)) return false;
            }
            return true;
        }
        private static bool SameReference(TypeReference? a, TypeReference? b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a.Kind != b.Kind || a.Name != b.Name || a.Assembly != b.Assembly) return false;
            var isControl = IsControlPlaneAssembly(a.Assembly);
            if (!isControl && (a.Module != b.Module || a.Token != b.Token)) return false;
            if (a.Rank != b.Rank || a.Position != b.Position || a.SzArray != b.SzArray
                || !SameReference(a.Element, b.Element) || b.Arguments == null || a.Arguments.Length != b.Arguments.Length) return false;
            for (var i = 0; i < a.Arguments.Length; i++) if (!SameReference(a.Arguments[i], b.Arguments[i])) return false;
            return true;
        }
        private const int ManifestMagic = 0x324d5452, ManifestLimit = 8 * 1024 * 1024;
        internal static byte[] ManifestBytes(ManifestData manifest)
        {
            if (manifest == null) throw new InvalidDataException("Resident type manifest is absent.");
            if (manifest.Version == 2) {
                using var writer = new ResidentMetadataWire.Writer(ManifestLimit);
                writer.Out.Write(ManifestMagic); writer.Out.Write(manifest.Types.Length);
                foreach (var entry in manifest.Types) {
                    writer.Text(entry.Identity); writer.Text(entry.Kind); writer.Type(entry.Type); writer.Out.Write(entry.Fields.Length);
                    foreach (var field in entry.Fields) {
                        writer.Type(field.DeclaringType); writer.Type(field.Type); writer.Out.Write(field.Token); writer.Out.Write(field.Attributes);
                        writer.Text(field.Name); writer.Text(field.Storage);
                    }
                }
                return writer.Finish();
            }
            var bytes = new UTF8Encoding(false, true).GetBytes(JsonConvert.SerializeObject(manifest, Formatting.None));
            if (bytes.Length == 0 || bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("Resident type manifest exceeds its byte bound.");
            return bytes;
        }
        internal static ManifestData DecodeManifest(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("Resident type manifest bytes differ.");
            if (bytes.Length >= 4 && BitConverter.ToInt32(bytes, 0) == ManifestMagic) {
                try {
                    using var reader = new ResidentMetadataWire.Reader(bytes, ManifestLimit);
                    _ = reader.In.ReadInt32(); var rows = new TypeEntry[reader.Count(16384, 46)];
                    var remainingFields = 100000;
                    for (var i = 0; i < rows.Length; i++) {
                        var row = rows[i] = new TypeEntry { Identity = reader.Text(), Kind = reader.Text(), Type = reader.Type() };
                        row.Fields = new FieldEntry[reader.Count(remainingFields, 84)]; remainingFields -= row.Fields.Length;
                        for (var j = 0; j < row.Fields.Length; j++) row.Fields[j] = new FieldEntry {
                            DeclaringType = reader.Type(), Type = reader.Type(), Token = reader.In.ReadInt32(), Attributes = reader.In.ReadInt32(),
                            Name = reader.Text(), Storage = reader.Text() };
                    }
                    reader.End(); return new ManifestData { Version = 2, Types = rows };
                } catch (Exception e) when (e is EndOfStreamException || e is DecoderFallbackException) {
                    throw new InvalidDataException("Malformed resident managed type manifest.", e);
                }
            }
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
        private static TypeEntry DescribeEntry(Type type, ResidentGraphPolicy policy, Dictionary<Type, TypeReference> metadata)
        {
            var kind = ResidentGraphPolicy.IsDelegate(type) ? "delegate" : policy.IsUnityType(type) ? "unity" : type.IsArray ? "array" : IsScalar(type) ? "scalar" : "managed";
            var fields = kind == "delegate" || kind == "array" || kind == "scalar" ? Array.Empty<FieldInfo>() : CanonicalCarryGraphCodec.Fields(type);
            return new TypeEntry { Identity = CanonicalCarryGraphCodec.Identity(type), Type = DescribeType(type, metadata), Kind = kind,
                Fields = fields.Select(field => new FieldEntry { DeclaringType = DescribeType(field.DeclaringType!, metadata), Token = field.MetadataToken,
                    Name = field.Name, Type = DescribeType(field.FieldType, metadata), Attributes = (int)field.Attributes,
                    Storage = field.DeclaringType!.FullName == "UnityEngine.Object" && field.Name == "m_CachedPtr" && field.FieldType == typeof(IntPtr)
                        ? "native-pointer" : policy.InstanceIdTarget(field) is Type target ? "native-instance-id:" + CanonicalCarryGraphCodec.Identity(target) : "managed" }).ToArray() };
        }

        internal static TypeReference DescribeType(Type type) => DescribeType(type, null);
        private static TypeReference DescribeType(Type type, Dictionary<Type, TypeReference>? metadata)
        {
            if (metadata != null && metadata.TryGetValue(type, out var found)) return found;
            var result = DescribeTypeBody(type, metadata);
            metadata?.Add(type, result);
            return result;
        }
        private static TypeReference DescribeTypeBody(Type type, Dictionary<Type, TypeReference>? metadata)
        {
            if (type == null || type.IsPointer || type.IsByRef || type.Assembly.IsDynamic)
                throw new InvalidDataException("Resident type reference is not stable loaded metadata.");
            // Mono enumerates partially instantiated classes as well as closed
            // classes. A parameter is scoped to its metadata owner; two T's
            // with the same printed name/position need not be the same type.
            if (type.IsGenericParameter) return ResidentMonoGenericMetadata.DescribeParameter(type);
            if (type.IsArray) return new TypeReference { Kind = "array", Name = ReferenceName(type), Rank = type.GetArrayRank(),
                Element = DescribeType(type.GetElementType()!, metadata), SzArray = type == type.GetElementType()!.MakeArrayType() };
            var definition = type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericTypeDefinition() : type;
            return new TypeReference { Kind = "type", Name = ReferenceName(type),
                Assembly = definition.Assembly.GetName().Name!, Module = definition.Module.ModuleVersionId.ToString("D"), Token = definition.MetadataToken,
                Arguments = type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericArguments().Select(argument => DescribeType(argument, metadata)).ToArray() : Array.Empty<TypeReference>() };
        }
        private static string ReferenceName(Type type) => type.IsGenericParameter ? type.Name
            : type.FullName ?? (type.ContainsGenericParameters && type.IsArray ? "open-array"
            : type.IsGenericType && type.ContainsGenericParameters ? type.GetGenericTypeDefinition().FullName : null)
            ?? throw new InvalidDataException("Resident type has no stable metadata name: " + type + " in " + type.Module.Name);
        internal static bool IsControlPlaneAssembly(string? assembly)
        {
            if (string.IsNullOrEmpty(assembly)) return false;
            return assembly == "HollowKnightTAS" || assembly == "HollowKnightTAS.Core"
                || assembly == "HollowKnightTAS.ClockPayload" || assembly == "HollowKnightTAS.ReferenceObserver"
                || assembly!.StartsWith("MonoMod", StringComparison.Ordinal) || assembly.StartsWith("Mono.Cecil", StringComparison.Ordinal)
                || assembly == "Newtonsoft.Json";
        }

        internal static TypeReference NormalizeType(TypeReference? tr)
        {
            if (tr == null) return null!;
            var isControl = IsControlPlaneAssembly(tr.Assembly);
            return new TypeReference {
                Kind = tr.Kind,
                Name = tr.Name,
                Assembly = tr.Assembly,
                Module = isControl ? "" : tr.Module,
                Token = isControl ? 0 : tr.Token,
                Rank = tr.Rank,
                SzArray = tr.SzArray,
                Position = tr.Position,
                Element = tr.Element != null ? NormalizeType(tr.Element) : null,
                Arguments = tr.Arguments != null ? tr.Arguments.Select(NormalizeType).ToArray() : Array.Empty<TypeReference>()
            };
        }

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
                type = null!;
                if (!IsControlPlaneAssembly(source.Assembly)) {
                    try { type = module.ResolveType(source.Token); }
                    catch (Exception error) when (error is ArgumentException || error is BadImageFormatException)
                    { throw new InvalidDataException("Resident type metadata token differs.", error); }
                } else {
                    try {
                        var candidate = module.ResolveType(source.Token);
                        if (ReferenceName(candidate) == source.Name) type = candidate;
                    } catch { }
                    if (type == null) {
                        type = module.Assembly.GetType(source.Name, false)
                            ?? module.GetTypes().FirstOrDefault(t => ReferenceName(t) == source.Name)
                            ?? throw new InvalidDataException("Resident control-plane type is absent: " + source.Name);
                    }
                }
                if (source.Arguments.Length != 0) {
                    if (type.IsGenericType && !type.IsGenericTypeDefinition) type = type.GetGenericTypeDefinition();
                    if (!type.IsGenericTypeDefinition) throw new InvalidDataException("Resident generic type definition differs: name=" + source.Name + ", resolved=" + type.FullName + ", isGenericDef=" + type.IsGenericTypeDefinition + ", token=0x" + source.Token.ToString("X8") + ", asm=" + source.Assembly);
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
            var candidates = AppDomain.CurrentDomain.GetAssemblies().Where(value => !value.IsDynamic && value.GetName().Name == assembly)
                .SelectMany(value => value.GetLoadedModules()).ToArray();
            var modules = candidates.Where(value => value.ModuleVersionId == identity).ToArray();
            if (modules.Length == 1) return modules[0];
            if (modules.Length == 0 && IsControlPlaneAssembly(assembly) && candidates.Length == 1) return candidates[0];
            throw new InvalidDataException("Resident module is absent, changed or ambiguous: " + assembly);
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
