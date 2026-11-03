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
        private readonly Dictionary<FieldInfo, Type?> instanceIdFields = new Dictionary<FieldInfo, Type?>();

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
        internal FieldInfo[] ManagedFields(Type type)
        {
            var pointer = PointerField(type);
            // m_CachedPtr is represented by the node's native/dead kind and is
            // always checked separately. Every other managed field is retained.
            return CanonicalCarryGraphCodec.Fields(type).Where(field => !SameField(field, pointer)).ToArray();
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
            var descriptor = new MetadataDescriptor();
            if (value is Type type) { descriptor.Kind = "type"; descriptor.Type = ResidentManagedTypeCatalog.DescribeType(type); }
            else if (value is MemberInfo member) {
                descriptor.Kind = "member"; descriptor.Type = ResidentManagedTypeCatalog.DescribeType(member.DeclaringType
                    ?? throw new InvalidDataException("Resident metadata member has no declaring type."));
                descriptor.Token = member.MetadataToken; descriptor.MemberKind = (int)member.MemberType;
                if (member is MethodInfo method && method.IsGenericMethod && !method.IsGenericMethodDefinition)
                    descriptor.Arguments = method.GetGenericArguments().Select(ResidentManagedTypeCatalog.DescribeType).ToArray();
            } else {
                var module = value is Module actual ? actual : ((Assembly)value).ManifestModule;
                descriptor.Kind = value is Module ? "module" : "assembly";
                descriptor.Assembly = module.Assembly.GetName().Name!; descriptor.Module = module.ModuleVersionId.ToString("D");
            }
            return MetadataPrefix + Encode(descriptor);
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
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                var matches = type.GetMembers(flags).Where(member => member.MetadataToken == descriptor.Token
                    && (int)member.MemberType == descriptor.MemberKind).ToArray();
                if (matches.Length != 1) throw new InvalidDataException("Resident metadata token is absent or ambiguous.");
                value = matches[0];
                if (descriptor.Arguments.Length != 0) {
                    if (value is not MethodInfo method || !method.IsGenericMethodDefinition) throw new InvalidDataException("Resident metadata generic method differs.");
                    value = method.MakeGenericMethod(descriptor.Arguments.Select(ResidentManagedTypeCatalog.ResolveType).ToArray());
                }
            } else throw new InvalidDataException("Resident metadata descriptor differs.");
            if (BindMetadata(value) != binding) throw new InvalidDataException("Resident metadata descriptor is noncanonical.");
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
                || Encode(descriptor) != text) throw new InvalidDataException("Resident delegate descriptor shape differs.");
            return descriptor.Calls.Select(call => {
                var owner = ResidentManagedTypeCatalog.ResolveType(call.DeclaringType);
                var matches = owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(method => method.MetadataToken == call.Token).ToArray();
                if (matches.Length != 1) throw new InvalidDataException("Resident delegate method token differs.");
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
                { throw new InvalidDataException("Resident delegate method/target binding differs.", error); }
                result = result == null ? next : Delegate.Combine(result, next);
            }
            if (result == null || result.GetType() != type || DescribeDelegate(result) != text)
                throw new InvalidDataException("Resident delegate binding did not read back.");
            RequireDelegate(result, text, targets); return result;
        }
        internal static void RequireDelegate(Delegate value, string text, object?[] targets)
        {
            var calls = value.GetInvocationList();
            if (DescribeDelegate(value) != text || calls.Length != targets.Length
                || calls.Where((call, index) => !ReferenceEquals(call.Target, targets[index])).Any())
                throw new InvalidDataException("Resident delegate invocation list or target alias differs.");
        }
        private static bool IsClosedBinding(Type delegateType, MethodInfo method)
        {
            var invoke = delegateType.GetMethod("Invoke") ?? throw new InvalidDataException("Resident delegate has no invocation signature.");
            var difference = method.GetParameters().Length - invoke.GetParameters().Length;
            if (method.IsStatic && (difference == 0 || difference == 1)) return difference == 1;
            if (!method.IsStatic && (difference == 0 || difference == -1)) return difference == 0;
            throw new InvalidDataException("Resident delegate method parameter arity differs.");
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
        private sealed class MetadataDescriptor
        {
            public string Kind = "", Assembly = "", Module = "";
            public ResidentManagedTypeCatalog.TypeReference? Type;
            public int Token, MemberKind;
            public ResidentManagedTypeCatalog.TypeReference[] Arguments = Array.Empty<ResidentManagedTypeCatalog.TypeReference>();
        }
        private sealed class DelegateDescriptor { public MethodDescriptor[] Calls = Array.Empty<MethodDescriptor>(); }
        private sealed class MethodDescriptor
        {
            public ResidentManagedTypeCatalog.TypeReference DeclaringType = null!;
            public int Token;
            public ResidentManagedTypeCatalog.TypeReference[] Arguments = Array.Empty<ResidentManagedTypeCatalog.TypeReference>();
            public bool HasTarget, Closed;
        }
    }
}
