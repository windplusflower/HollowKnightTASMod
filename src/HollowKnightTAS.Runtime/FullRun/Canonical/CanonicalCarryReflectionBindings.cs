using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Metadata is rebound to the same already-loaded module, never copied as
    // Mono reflection implementation fields. No imported name loads an assembly.
    internal sealed class CanonicalCarryReflectionBindings
    {
        private readonly Assembly[] assemblies;
        private readonly Dictionary<string, int> arrays = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly FieldInfo[] originalCallbacks;
        internal CanonicalCarryReflectionBindings(Assembly[] assemblies, FieldInfo[] originalCallbacks)
        { this.assemblies = assemblies.Distinct().ToArray(); this.originalCallbacks = originalCallbacks; }

        private string TypeKey(Type type)
        {
            if (!assemblies.Contains(type.Assembly) || type.FullName == null || type.ContainsGenericParameters
                || type.Assembly.IsDynamic || type.FullName.Contains("|"))
                throw new InvalidDataException("unregistered-metadata-type");
            return type.Assembly.GetName().Name + "|" + type.Module.ModuleVersionId.ToString("D") + "|" + type.FullName;
        }
        private string MethodKey(MethodInfo method)
        {
            if (method is DynamicMethod || method.DeclaringType == null || method.IsGenericMethod)
                throw new InvalidDataException("dynamic-or-generic-method-needs-binding");
            return TypeKey(method.DeclaringType) + "|" + method.MetadataToken.ToString("x8");
        }
        internal string? Bind(object value)
        {
            if (value is Type type) return "metadata-type|" + TypeKey(type);
            if (value is MethodInfo method) return "metadata-method|" + MethodKey(method);
            if (value is Array array && typeof(ParameterInfo).IsAssignableFrom(array.GetType().GetElementType()))
            {
                if (array.Rank != 1 || array.GetLowerBound(0) != 0 || array.Length > 128)
                    throw new InvalidDataException("parameter-metadata-array-shape");
                var parameters = array.Cast<ParameterInfo>().ToArray();
                var key = "metadata-parameters|" + TypeKey(array.GetType()) + "|";
                if (parameters.Length == 0) key += "empty";
                else
                {
                    if (!(parameters[0].Member is MethodInfo owner) || !parameters.Select(p => p.Position).SequenceEqual(Enumerable.Range(0, parameters.Length))
                        || parameters.Any(p => !Equals(p.Member, owner))) throw new InvalidDataException("parameter-metadata-owner-binding");
                    key += MethodKey(owner);
                }
                arrays.TryGetValue(key, out var ordinal); arrays[key] = ordinal + 1;
                return key + "|array=" + ordinal;
            }
            if (ReferenceEquals(value, EqualityComparer<string>.Default)) return "runtime-comparer|string-default";
            if (ReferenceEquals(value, EqualityComparer<int>.Default)) return "runtime-comparer|int32-default";
            if (value is Delegate callback)
            {
                // Only the exact original dream-warp checks are registered.
                // Static slots have already been populated by original action
                // constructors before this reader runs. Do not create callbacks.
                foreach (var field in originalCallbacks)
                {
                    var existing = field.GetValue(null);
                    if (ReferenceEquals(existing, callback))
                    {
                        if (callback.GetInvocationList().Length != 1) throw new InvalidDataException("multicast-gameplay-callback-needs-binding");
                        return "original-callback|" + TypeKey(field.DeclaringType!) + "|" + field.Name + "|" + MethodKey(callback.Method);
                    }
                }
                throw new InvalidDataException("unregistered-gameplay-callback");
            }
            if (value is MemberInfo) throw new InvalidDataException("unregistered-reflection-member");
            return null;
        }
        internal object Resolve(string binding)
        {
            var parts = binding.Split('|');
            Type TypeAt(int offset)
            {
                if (parts.Length < offset + 3) throw new InvalidDataException("Incomplete metadata binding.");
                var assembly = assemblies.SingleOrDefault(value => value.GetName().Name == parts[offset])
                    ?? throw new InvalidDataException("Metadata assembly is not registered.");
                var type = assembly.GetType(parts[offset + 2], false) ?? throw new InvalidDataException("Metadata type is absent.");
                if (!Guid.TryParseExact(parts[offset + 1], "D", out var savedMvid) || savedMvid == Guid.Empty)
                    throw new InvalidDataException("Metadata module identity differs.");
                var expectedKey = TypeKey(type);
                var actualPrefix = string.Join("|", parts.Skip(offset).Take(3));
                if (expectedKey != actualPrefix) {
                    if (savedMvid != type.Module.ModuleVersionId && parts[offset] == assembly.GetName().Name && parts[offset + 2] == type.FullName)
                        CanonicalSceneMappingCarry.WarnOnce("CarryReflection:TypeMvid:" + type.FullName + ":" + savedMvid,
                            "[HKTAS] Metadata module MVID differs for " + type.FullName + " (continuing): saved=" + savedMvid + ";actual=" + type.Module.ModuleVersionId);
                    else
                        throw new InvalidDataException("Metadata module identity differs.");
                }
                return type;
            }
            MethodInfo MethodAt(int offset)
            {
                var type = TypeAt(offset);
                if (parts.Length <= offset + 3 || !int.TryParse(parts[offset + 3], System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var token)) throw new InvalidDataException("Metadata method token is invalid.");
                var method = type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static
                    | BindingFlags.NonPublic | BindingFlags.Public).SingleOrDefault(value => value.MetadataToken == token)
                    ?? throw new InvalidDataException("Metadata method is absent.");
                if (method is DynamicMethod || method.DeclaringType != type || method.IsGenericMethod)
                    throw new InvalidDataException("Metadata method identity differs.");
                return method;
            }
            if (binding == "runtime-comparer|string-default") return EqualityComparer<string>.Default;
            if (binding == "runtime-comparer|int32-default") return EqualityComparer<int>.Default;
            if (parts[0] == "metadata-type" && parts.Length == 4) return TypeAt(1);
            if (parts[0] == "metadata-method" && parts.Length == 5) return MethodAt(1);
            if (parts[0] == "original-callback" && parts.Length == 9)
            {
                var type = TypeAt(1); var method = MethodAt(5);
                var field = originalCallbacks.SingleOrDefault(value => value.DeclaringType == type && value.Name == parts[4])
                    ?? throw new InvalidDataException("Callback slot is not registered.");
                var callback = field.GetValue(null) as Delegate ?? throw new InvalidDataException("Original callback slot is not initialized.");
                if (callback.GetInvocationList().Length != 1 || !Equals(callback.Method, method))
                    throw new InvalidDataException("Original callback method differs.");
                return callback;
            }
            if (parts[0] == "metadata-parameters" && (parts.Length == 6 || parts.Length == 9))
            {
                var arrayType = TypeAt(1); var ordinal = parts[parts.Length - 1];
                if (!arrayType.IsArray || arrayType.GetArrayRank() != 1 || !typeof(ParameterInfo).IsAssignableFrom(arrayType.GetElementType())
                    || !ordinal.StartsWith("array=", StringComparison.Ordinal)
                    || !int.TryParse(ordinal.Substring(6), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
                    || number < 0 || ordinal != "array=" + number)
                    throw new InvalidDataException("Parameter metadata array identity differs.");
                var parameters = parts.Length == 6 && parts[4] == "empty" ? Array.Empty<ParameterInfo>()
                    : parts.Length == 9 ? MethodAt(4).GetParameters() : throw new InvalidDataException("Parameter metadata owner differs.");
                if (parameters.Length > 128) throw new InvalidDataException("Parameter metadata count exceeds its bound.");
                var array = Array.CreateInstance(arrayType.GetElementType()!, parameters.Length);
                for (var i = 0; i < parameters.Length; i++) array.SetValue(parameters[i], i);
                return array;
            }
            throw new InvalidDataException("Unknown metadata binding.");
        }
    }
}
