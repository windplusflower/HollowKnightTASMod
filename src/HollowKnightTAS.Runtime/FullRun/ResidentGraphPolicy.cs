using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Instance selection belongs to the captured directory. This policy only
    // distinguishes portable managed objects, bound native owners and dead
    // wrappers; it contains no scene, object-path or action-content catalogue.
    internal sealed class ResidentGraphPolicy
    {
        internal const string Identity = "resident-managed-graph-v2-native-instance-id";
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private const string MetadataPrefix = "resident-metadata/1/";
        private readonly Func<object, string?> nativeOwnerBinding;
        private readonly Func<int, Type, string?>? instanceIdBinding;
        private readonly Func<string, Type, int>? instanceIdResolver;
        private readonly Dictionary<Type, FieldInfo?> pointers = new Dictionary<Type, FieldInfo?>();
        private readonly Dictionary<Type, FieldInfo[]> fieldLayouts = new Dictionary<Type, FieldInfo[]>();
        private readonly Dictionary<FieldInfo, Type?> instanceIdFields = new Dictionary<FieldInfo, Type?>();
        private readonly Dictionary<object, string> metadataBindings = new Dictionary<object, string>(MetadataIdentity.Instance);

        internal ResidentGraphPolicy(Func<object, string?> nativeOwnerBinding, Func<int, Type, string?>? bindInstanceId = null,
            Func<string, Type, int>? resolveInstanceId = null)
        {
            this.nativeOwnerBinding = nativeOwnerBinding ?? throw new ArgumentNullException(nameof(nativeOwnerBinding));
            instanceIdBinding = bindInstanceId; instanceIdResolver = resolveInstanceId;
        }

        // These are native API field semantics, not instance/path selection.
        // MVID, token, complete layout and this storage kind are also included
        // in the actual-type manifest, and target resolution uses loaded types.
        internal Type? InstanceIdTarget(FieldInfo field)
        {
            if (instanceIdFields.TryGetValue(field, out var cached)) return cached;
            var owner = field.DeclaringType; string? target = null;
            if (owner != null && owner.Assembly.GetName().Name == "UnityEngine.Physics2DModule" && owner.Module.Name == "UnityEngine.Physics2DModule.dll") {
                if (owner.FullName == "UnityEngine.Collision2D" || owner.FullName == "UnityEngine.ContactPoint2D") {
                    if (field.Name == "m_Collider" || field.Name == "m_OtherCollider") target = "UnityEngine.Collider2D";
                    else if (field.Name == "m_Rigidbody" || field.Name == "m_OtherRigidbody") target = "UnityEngine.Rigidbody2D";
                } else if (owner.FullName == "UnityEngine.RaycastHit2D" && field.Name == "m_Collider") target = "UnityEngine.Collider2D";
            } else if (owner != null && owner.Assembly.GetName().Name == "UnityEngine.PhysicsModule" && owner.Module.Name == "UnityEngine.PhysicsModule.dll") {
                if (owner.FullName == "UnityEngine.RaycastHit" && field.Name == "m_Collider"
                    || owner.FullName == "UnityEngine.ContactPoint" && (field.Name == "m_ThisColliderInstanceID" || field.Name == "m_OtherColliderInstanceID"))
                    target = "UnityEngine.Collider";
            }
            Type? result = null;
            if (target != null) {
                if (field.IsStatic || field.FieldType != typeof(int)) throw new InvalidDataException("Resident instance-ID field ABI differs.");
                result = owner!.Assembly.GetType(target, false, false) ?? throw new InvalidDataException("Resident instance-ID target type is not loaded.");
                if (!IsUnityType(result)) throw new InvalidDataException("Resident instance-ID target is not a native Unity type.");
            }
            instanceIdFields.Add(field, result); return result;
        }

        internal string CaptureInstanceId(FieldInfo field, int value)
        {
            var target = InstanceIdTarget(field) ?? throw new InvalidDataException("Resident instance-ID tag is outside its declared field.");
            if (value == 0) return "";
            var binding = instanceIdBinding?.Invoke(value, target);
            if (binding == "") return "";
            if (string.IsNullOrEmpty(binding) || binding!.Length > 16384 || binding.IndexOf('\0') >= 0)
                throw new InvalidDataException("Resident nonzero instance ID has no unique native directory binding: " + field.DeclaringType!.FullName + "/" + field.Name);
            return binding;
        }

        internal int ResolveInstanceId(FieldInfo field, string binding)
        {
            var target = InstanceIdTarget(field) ?? throw new InvalidDataException("Resident instance-ID tag is outside its declared field.");
            if (binding == null || binding.Length > 16384 || binding.IndexOf('\0') >= 0) throw new InvalidDataException("Resident instance-ID binding text differs.");
            if (binding.Length == 0) return 0;
            if (instanceIdResolver == null) throw new InvalidDataException("Resident instance-ID binding requires a target directory resolver.");
            var result = instanceIdResolver(binding, target);
            if (result == 0 || CaptureInstanceId(field, result) != binding) throw new InvalidDataException("Resident instance-ID target does not round-trip its native directory identity.");
            return result;
        }

        internal bool IsUnityType(Type type) => PointerField(type) != null;
        internal IntPtr NativePointer(object owner)
        {
            var field = PointerField(owner.GetType()) ?? throw new InvalidDataException("Resident native owner is not a Unity object.");
            return (IntPtr)(field.GetValue(owner) ?? throw new InvalidDataException("Resident native pointer is absent."));
        }
        internal bool IsDestroyed(object owner) => IsUnityType(owner.GetType()) && NativePointer(owner) == IntPtr.Zero;
        internal string? BindNativeOwner(object owner)
        {
            if (!IsUnityType(owner.GetType()) || NativePointer(owner) == IntPtr.Zero) return null;
            var binding = nativeOwnerBinding(owner);
            if (binding != null && (binding.Length == 0 || binding.Length > 16384)) throw new InvalidDataException("Resident native binding is invalid.");
            return binding;
        }
        private static readonly Dictionary<Type, FieldInfo[]> ReferenceLayouts = new Dictionary<Type, FieldInfo[]>();
        internal static bool CanContainReferences(Type type)
        {
            if (type == typeof(string) || type.IsPointer || type.IsByRef) return false;
            if (!type.IsValueType) return true;
            return !type.IsPrimitive && !type.IsEnum && type != typeof(decimal) && HasManagedReferenceField(type);
        }
        private static readonly Dictionary<Type, bool> ReferenceValueTypes = new Dictionary<Type, bool>();
        private static bool HasManagedReferenceField(Type type)
        {
            lock (ReferenceValueTypes) {
                if (!ReferenceValueTypes.TryGetValue(type, out var found)) {
                    found = CanonicalCarryGraphCodec.Fields(type).Any(field => !field.FieldType.IsValueType
                        ? !field.FieldType.IsPointer && !field.FieldType.IsByRef
                        : CanContainReferences(field.FieldType));
                    ReferenceValueTypes.Add(type, found);
                }
                return found;
            }
        }
        internal static bool CanReachWrapper(Type type)
        {
            while (type.IsArray) type = type.GetElementType()!;
            return CanContainReferences(type);
        }
        internal static bool IsTerminalForWrapperSearch(object value, Type type)
        {
            if (value is Array array) return array.Length == 0 || !CanReachWrapper(type.GetElementType()!);
            return !(value is Delegate) && ReferenceFields(type).Length == 0;
        }
        internal static string DescribeNodeBudget(IEnumerable<object> seen)
            => string.Join(", ", seen.GroupBy(ResidentValueAccess.TypeOf).OrderByDescending(group => group.Count())
                .Take(8).Select(group => group.Key.FullName + "=" + group.Count()));

        internal static FieldInfo[] ReferenceFields(Type type)
        {
            lock (ReferenceLayouts) {
                if (!ReferenceLayouts.TryGetValue(type, out var fields)) {
                    fields = CanonicalCarryGraphCodec.Fields(type).Where(field => CanReachWrapper(field.FieldType)).ToArray();
                    ReferenceLayouts.Add(type, fields);
                }
                return fields;
            }
        }

        private static readonly Dictionary<Type, Func<object, object?>[]> ReferenceReaders = new Dictionary<Type, Func<object, object?>[]>();
        internal static Func<object, object?>[] ReadersForReferences(Type type)
        {
            lock (ReferenceReaders) {
                if (!ReferenceReaders.TryGetValue(type, out var readers)) {
                    readers = ReferenceFields(type).Select(ResidentValueAccess.PrepareRead).ToArray();
                    ReferenceReaders.Add(type, readers);
                }
                return readers;
            }
        }
        internal FieldInfo[] ManagedFields(Type type)
        {
            if (fieldLayouts.TryGetValue(type, out var cached)) return cached;
            var pointer = PointerField(type);
            // m_CachedPtr is represented by the node's native/dead kind and is
            // always checked separately. Every other managed field is retained.
            var fields = CanonicalCarryGraphCodec.Fields(type).Where(field => !SameField(field, pointer)).ToArray();
            fieldLayouts.Add(type, fields);
            return fields;
        }
        private FieldInfo? PointerField(Type type)
        {
            if (pointers.TryGetValue(type, out var cached)) return cached;
            FieldInfo? pointer = null;
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
                if (current.FullName == "UnityEngine.Object") {
                    pointer = current.GetField("m_CachedPtr", Fields);
                    if (pointer == null || pointer.FieldType != typeof(IntPtr) || pointer.IsStatic)
                        throw new InvalidDataException("Resident Unity base-pointer ABI differs.");
                    break;
                }
            pointers.Add(type, pointer); return pointer;
        }
        private static bool SameField(FieldInfo value, FieldInfo? other) => other != null
            && value.Module == other.Module && value.MetadataToken == other.MetadataToken;

        internal static bool IsDelegate(Type type) => typeof(Delegate).IsAssignableFrom(type);
        internal static bool IsMetadata(object value) => value is Type || value is MemberInfo || value is Assembly || value is Module;

        internal string? BindMetadata(object value)
        {
            if (!IsMetadata(value)) return null;
            if (metadataBindings.TryGetValue(value, out var binding)) return binding;
            var descriptor = new MetadataDescriptor();
            if (value is Type type) { descriptor.Kind = "type"; descriptor.Type = ResidentManagedTypeCatalog.DescribeType(type); }
            else if (value is MemberInfo member) {
                descriptor.Kind = "member"; descriptor.Type = ResidentManagedTypeCatalog.DescribeType(member.DeclaringType
                    ?? throw new InvalidDataException("Resident metadata member has no declaring type."));
                descriptor.Token = member.MetadataToken; descriptor.MemberKind = (int)member.MemberType;
                if (ResidentManagedTypeCatalog.IsControlPlaneAssembly(member.DeclaringType.Assembly.GetName().Name))
                    descriptor.Name = member.Name;
                if (member.ReflectedType != null && member.ReflectedType != member.DeclaringType)
                    descriptor.ReflectedType = ResidentManagedTypeCatalog.DescribeType(member.ReflectedType);
                if (member is MethodInfo method && method.IsGenericMethod && !method.IsGenericMethodDefinition)
                    descriptor.Arguments = method.GetGenericArguments().Select(ResidentManagedTypeCatalog.DescribeType).ToArray();
            } else {
                var module = value is Module actual ? actual : ((Assembly)value).ManifestModule;
                descriptor.Kind = value is Module ? "module" : "assembly";
                descriptor.Assembly = module.Assembly.GetName().Name!; descriptor.Module = module.ModuleVersionId.ToString("D");
            }
            binding = MetadataPrefix + Encode(descriptor);
            if (metadataBindings.Count < CanonicalCarryGraphCodec.MaximumNodes) metadataBindings.Add(value, binding);
            return binding;
        }
        internal bool TryResolveMetadata(string binding, out object value)
        {
            value = null!;
            if (!binding.StartsWith(MetadataPrefix, StringComparison.Ordinal)) return false;
            var descriptor = Decode<MetadataDescriptor>(binding.Substring(MetadataPrefix.Length));
            if (descriptor.Kind == "type" && descriptor.Type != null) value = ResidentManagedTypeCatalog.ResolveType(descriptor.Type);
            else if ((descriptor.Kind == "module" || descriptor.Kind == "assembly") && descriptor.Type == null) {
                var module = ResidentManagedTypeCatalog.ResolveModule(descriptor.Assembly, descriptor.Module);
                value = descriptor.Kind == "module" ? (object)module : module.Assembly;
            } else if (descriptor.Kind == "member" && descriptor.Type != null) {
                var type = ResidentManagedTypeCatalog.ResolveType(descriptor.Type);
                var reflected = descriptor.ReflectedType == null ? type : ResidentManagedTypeCatalog.ResolveType(descriptor.ReflectedType);
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                var matches = reflected.GetMembers(flags | (descriptor.ReflectedType == null ? BindingFlags.DeclaredOnly : BindingFlags.FlattenHierarchy))
                    .Where(member => member.MetadataToken == descriptor.Token && member.DeclaringType == type
                        && (int)member.MemberType == descriptor.MemberKind).ToArray();
                if (ResidentManagedTypeCatalog.IsControlPlaneAssembly(type.Assembly.GetName().Name)
                    && (matches.Length != 1 || descriptor.Type.Module != type.Module.ModuleVersionId.ToString("D"))) {
                    var candidates = reflected.GetMembers(flags | (descriptor.ReflectedType == null ? BindingFlags.DeclaredOnly : BindingFlags.FlattenHierarchy))
                        .Where(member => member.DeclaringType == type && (int)member.MemberType == descriptor.MemberKind).ToArray();
                    if (!string.IsNullOrEmpty(descriptor.Name)) {
                        var byName = candidates.Where(member => member.Name == descriptor.Name).ToArray();
                        if (byName.Length == 1) matches = byName;
                    } else if (descriptor.MemberKind == (int)MemberTypes.Constructor) {
                        var ctors = candidates.OfType<ConstructorInfo>().Where(c => !c.IsStatic).ToArray();
                        if (ctors.Length == 1) matches = ctors;
                    }
                }
                if (matches.Length != 1) throw new InvalidDataException("Resident metadata token is absent or ambiguous: type=" + type.FullName + ", asm=" + type.Assembly.GetName().Name + ", token=0x" + descriptor.Token.ToString("X8") + ", kind=" + descriptor.MemberKind + ", matches=" + matches.Length);
                value = matches[0];
                if (descriptor.Arguments.Length != 0) {
                    if (value is not MethodInfo method || !method.IsGenericMethodDefinition) throw new InvalidDataException("Resident metadata generic method differs.");
                    value = method.MakeGenericMethod(descriptor.Arguments.Select(ResidentManagedTypeCatalog.ResolveType).ToArray());
                }
            } else throw new InvalidDataException("Resident metadata descriptor differs.");
            var bound = BindMetadata(value);
            if (bound != binding && NormalizeMetadataOrDelegate(bound ?? "") != NormalizeMetadataOrDelegate(binding)) {
                var expJson = binding.StartsWith(MetadataPrefix, StringComparison.Ordinal)
                    ? new UTF8Encoding(false, true).GetString(Convert.FromBase64String(binding.Substring(MetadataPrefix.Length)))
                    : binding;
                var actJson = bound != null && bound.StartsWith(MetadataPrefix, StringComparison.Ordinal)
                    ? new UTF8Encoding(false, true).GetString(Convert.FromBase64String(bound.Substring(MetadataPrefix.Length)))
                    : (bound ?? "null");
                throw new InvalidDataException("Resident metadata descriptor is noncanonical: expJson=[" + expJson + "], actJson=[" + actJson + "]");
            }
            if (metadataBindings.Count < CanonicalCarryGraphCodec.MaximumNodes && !metadataBindings.ContainsKey(value))
                metadataBindings.Add(value, binding);
            return true;
        }

        internal static string DescribeDelegate(Delegate value)
        {
            var calls = value.GetInvocationList();
            if (calls.Length == 0 || calls.Length > 4096) throw new InvalidDataException("Resident delegate invocation count differs.");
            return Encode(new DelegateDescriptor { Calls = calls.Select(call => {
                var method = call.Method;
                if (method.DeclaringType == null || method.ContainsGenericParameters || method.Module.Assembly.IsDynamic)
                    throw new InvalidDataException("Resident delegate method has no stable loaded-module identity.");
                return new MethodDescriptor { DeclaringType = ResidentManagedTypeCatalog.DescribeType(method.DeclaringType),
                    Name = method.Name,
                    Token = method.MetadataToken, Arguments = method.IsGenericMethod
                        ? method.GetGenericArguments().Select(ResidentManagedTypeCatalog.DescribeType).ToArray()
                        : Array.Empty<ResidentManagedTypeCatalog.TypeReference>(),
                    HasTarget = call.Target != null, Closed = IsClosedBinding(call.GetType(), method) };
            }).ToArray() });
        }
        internal static MethodInfo[] ResolveDelegateMethods(string text, Type delegateType, int count)
        {
            if (!IsDelegate(delegateType) || delegateType.IsAbstract || delegateType.ContainsGenericParameters)
                throw new InvalidDataException("Resident delegate type is not closed and concrete.");
            var descriptor = Decode<DelegateDescriptor>(text);
            if (descriptor.Calls == null || descriptor.Calls.Length != count || count < 1 || count > 4096
                || !EquivalentDelegateDescriptors(Encode(descriptor), text)) throw new InvalidDataException("Resident delegate descriptor shape differs.");
            return descriptor.Calls.Select(call => {
                var owner = ResidentManagedTypeCatalog.ResolveType(call.DeclaringType);
                var methods = owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                var matches = methods.Where(method => method.MetadataToken == call.Token && (string.IsNullOrEmpty(call.Name) || method.Name == call.Name)).ToArray();
                if (matches.Length != 1 && !string.IsNullOrEmpty(call.Name)) {
                    matches = methods.Where(method => method.Name == call.Name && TryIsClosedBinding(delegateType, method, out var closed) && closed == call.Closed).ToArray();
                }
                if (matches.Length > 1) {
                    var invoke = delegateType.GetMethod("Invoke");
                    if (invoke != null) {
                        var invokeParams = invoke.GetParameters();
                        matches = matches.Where(m => {
                            var mParams = m.GetParameters();
                            int mOffset = (m.IsStatic && call.Closed) ? 1 : 0;
                            int invOffset = (!m.IsStatic && !call.Closed) ? 1 : 0;
                            int paramCount = mParams.Length - mOffset;
                            if (paramCount != invokeParams.Length - invOffset) return false;
                            for (int i = 0; i < paramCount; i++) {
                                if (mParams[i + mOffset].ParameterType != invokeParams[i + invOffset].ParameterType)
                                    return false;
                            }
                            return true;
                        }).ToArray();
                    }
                }
                if (matches.Length != 1) throw new InvalidDataException("Resident delegate method token differs: type=" + owner.FullName + ", name=" + call.Name + ", token=" + call.Token.ToString("x8") + ", matches=" + matches.Length);
                var method = matches[0];
                if (call.Arguments.Length != 0) {
                    if (!method.IsGenericMethodDefinition) throw new InvalidDataException("Resident delegate generic method differs.");
                    method = method.MakeGenericMethod(call.Arguments.Select(ResidentManagedTypeCatalog.ResolveType).ToArray());
                }
                if (method.ContainsGenericParameters) throw new InvalidDataException("Resident delegate method remains open.");
                if (IsClosedBinding(delegateType, method) != call.Closed || call.HasTarget && !call.Closed)
                    throw new InvalidDataException("Resident delegate open/closed binding differs.");
                return method;
            }).ToArray();
        }
        internal static Delegate BindDelegate(string text, Type type, object?[] targets, MethodInfo[] methods)
        {
            var descriptor = Decode<DelegateDescriptor>(text);
            if (targets.Length != methods.Length || descriptor.Calls.Length != methods.Length) throw new InvalidDataException("Resident delegate targets differ.");
            Delegate? result = null;
            for (var index = 0; index < targets.Length; index++) {
                if ((targets[index] != null) != descriptor.Calls[index].HasTarget) throw new InvalidDataException("Resident delegate target-null shape differs.");
                Delegate next;
                try { next = descriptor.Calls[index].Closed
                    ? Delegate.CreateDelegate(type, targets[index], methods[index], true)
                    : Delegate.CreateDelegate(type, methods[index], true); }
                catch (Exception error) when (error is ArgumentException || error is MethodAccessException)
                { throw new InvalidDataException($"Resident delegate method/target binding differs: delType={type.FullName}, target={targets[index]?.GetType().FullName}, method={methods[index].DeclaringType?.FullName}.{methods[index].Name}", error); }
                result = result == null ? next : Delegate.Combine(result, next);
            }
            if (result == null || result.GetType() != type)
                throw new InvalidDataException("Resident delegate binding did not read back: type null or mismatch.");
            var act = DescribeDelegate(result);
            if (!EquivalentDelegateDescriptors(act, text)) {
                var expJson = DecodeJsonText(text);
                var actJson = DecodeJsonText(act);
                throw new InvalidDataException("Resident delegate binding did not read back: expJson=[" + expJson + "], actJson=[" + actJson + "]");
            }
            RequireDelegate(result, text, targets); return result;
        }
        internal static void RequireDelegate(Delegate value, string text, object?[] targets)
        {
            var calls = value.GetInvocationList();
            var act = DescribeDelegate(value);
            if (!EquivalentDelegateDescriptors(act, text) || calls.Length != targets.Length
                || calls.Where((call, index) => !ReferenceEquals(call.Target, targets[index])).Any())
                throw new InvalidDataException("Resident delegate invocation list or target alias differs.");
        }
        private static bool TryIsClosedBinding(Type delegateType, MethodInfo method, out bool closed)
        {
            closed = false;
            var invoke = delegateType.GetMethod("Invoke");
            if (invoke == null) return false;
            var difference = method.GetParameters().Length - invoke.GetParameters().Length;
            if (method.IsStatic && (difference == 0 || difference == 1)) { closed = difference == 1; return true; }
            if (!method.IsStatic && (difference == 0 || difference == -1)) { closed = difference == 0; return true; }
            return false;
        }

        private static bool IsClosedBinding(Type delegateType, MethodInfo method)
        {
            if (TryIsClosedBinding(delegateType, method, out var closed)) return closed;
            throw new InvalidDataException("Resident delegate method parameter arity differs: delegate=" + delegateType.FullName + ", method=" + method.DeclaringType?.FullName + "." + method.Name);
        }
        private static string Encode<T>(T value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, Formatting.None)));
        private static T Decode<T>(string text) where T : class
        {
            if (text == null || text.Length == 0 || text.Length > 1024 * 1024) throw new InvalidDataException("Resident metadata size differs.");
            try { return JsonConvert.DeserializeObject<T>(new UTF8Encoding(false, true).GetString(Convert.FromBase64String(text)))
                ?? throw new InvalidDataException("Resident metadata is absent."); }
            catch (Exception error) when (error is JsonException || error is FormatException || error is DecoderFallbackException)
            { throw new InvalidDataException("Resident metadata is malformed.", error); }
        }
        internal static bool EquivalentDelegateDescriptors(string act, string exp)
        {
            if (act == exp) return true;
            if (NormalizeMetadataOrDelegate(act) == NormalizeMetadataOrDelegate(exp)) return true;
            return false;
        }
        private static string DecodeJsonText(string text)
        {
            try {
                var b64 = text.StartsWith(MetadataPrefix, StringComparison.Ordinal) ? text.Substring(MetadataPrefix.Length) : text;
                return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(b64));
            }
            catch { return text; }
        }
        internal static string NormalizeMetadataOrDelegate(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.StartsWith(MetadataPrefix, StringComparison.Ordinal)) {
                try {
                    var b64 = text.Substring(MetadataPrefix.Length);
                    var desc = Decode<MetadataDescriptor>(b64);
                    if (ResidentManagedTypeCatalog.IsControlPlaneAssembly(desc.Assembly) || (desc.Type != null && ResidentManagedTypeCatalog.IsControlPlaneAssembly(desc.Type.Assembly))) { desc.Module = ""; desc.Token = 0; }
                    desc.Type = ResidentManagedTypeCatalog.NormalizeType(desc.Type);
                    desc.ReflectedType = ResidentManagedTypeCatalog.NormalizeType(desc.ReflectedType);
                    if (desc.Arguments != null && desc.Arguments.Length != 0)
                        desc.Arguments = desc.Arguments.Select(ResidentManagedTypeCatalog.NormalizeType).ToArray();
                    return MetadataPrefix + Encode(desc);
                } catch { return text; }
            }
            try {
                var desc = Decode<DelegateDescriptor>(text);
                if (desc.Calls != null && desc.Calls.Length != 0) {
                    foreach (var call in desc.Calls) {
                        if (call.DeclaringType != null && ResidentManagedTypeCatalog.IsControlPlaneAssembly(call.DeclaringType.Assembly)) call.Token = 0;
                        call.Name = null;
                        call.DeclaringType = ResidentManagedTypeCatalog.NormalizeType(call.DeclaringType);
                        if (call.Arguments != null && call.Arguments.Length != 0)
                            call.Arguments = call.Arguments.Select(ResidentManagedTypeCatalog.NormalizeType).ToArray();
                    }
                    return Encode(desc);
                }
            } catch { }
            return text;
        }

        internal sealed class MetadataDescriptor
        {
            public string Kind = "", Assembly = "", Module = "";
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string? Name;
            public ResidentManagedTypeCatalog.TypeReference? Type;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public ResidentManagedTypeCatalog.TypeReference? ReflectedType;
            public int Token, MemberKind;
            public ResidentManagedTypeCatalog.TypeReference[] Arguments = Array.Empty<ResidentManagedTypeCatalog.TypeReference>();
        }
        private sealed class MetadataIdentity : IEqualityComparer<object>
        {
            internal static readonly MetadataIdentity Instance = new MetadataIdentity();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
        internal sealed class DelegateDescriptor { public MethodDescriptor[] Calls = Array.Empty<MethodDescriptor>(); }
        internal sealed class MethodDescriptor
        {
            public ResidentManagedTypeCatalog.TypeReference DeclaringType = null!;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string? Name;
            public int Token;
            public ResidentManagedTypeCatalog.TypeReference[] Arguments = Array.Empty<ResidentManagedTypeCatalog.TypeReference>();
            public bool HasTarget, Closed;
        }
    }
}
