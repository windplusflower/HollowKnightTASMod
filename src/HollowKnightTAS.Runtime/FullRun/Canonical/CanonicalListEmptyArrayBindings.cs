using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
namespace HollowKnightTAS.Runtime.FullRun
{
    // Only List<T>'s exact framework-owned singleton, never all empty T[]. The
    // pinned initializer below allocates zero elements and writes this field only.
    internal sealed class CanonicalListEmptyArrayBindings
    {
        private const string Prefix = "framework-list-static-empty-v1/";
        private readonly Dictionary<Type, string> bindings = new Dictionary<Type, string>();
        private readonly Dictionary<Type, FieldInfo> listFields = new Dictionary<Type, FieldInfo>();
        private readonly Dictionary<Type, Array> observedValues = new Dictionary<Type, Array>();
        private readonly Dictionary<string, FieldInfo> keys = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
        internal CanonicalListEmptyArrayBindings(IEnumerable<Type> registered)
        {
            foreach (var type in registered) {
                if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(List<>)) continue;
                var candidates = type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                FieldInfo? field = null;
                foreach (var candidate in candidates) if (candidate.Name == "_emptyArray" || candidate.Name == "s_emptyArray") {
                    if (field != null) throw new InvalidDataException("Framework List empty-array identity is ambiguous.");
                    field = candidate;
                }
                var element = type.GetGenericArguments()[0];
                if (field == null || !field.IsStatic || !field.IsInitOnly || field.FieldType != element.MakeArrayType())
                    throw new InvalidDataException("Framework List empty-array field ABI differs.");
                var initializer = type.TypeInitializer;
                var body = initializer?.GetMethodBody(); var il = body?.GetILAsByteArray();
                if (il == null || il.Length != 12 || il[0] != 0x16 || il[1] != 0x8d || il[6] != 0x80 || il[11] != 0x2a
                    || body!.ExceptionHandlingClauses.Count != 0 || body.LocalVariables.Count != 0
                    || type.Module.ResolveType(BitConverter.ToInt32(il, 2), type.GetGenericArguments(), null) != element)
                    throw new InvalidDataException("Framework List empty-array initializer differs.");
                var written = type.Module.ResolveField(BitConverter.ToInt32(il, 7), type.GetGenericArguments(), null);
                if (written == null || written.Module != field.Module || written.MetadataToken != field.MetadataToken)
                    throw new InvalidDataException("Framework List empty-array initializer writes a different field.");
                var key = Key(field);
                bindings.Add(field.FieldType, key); listFields.Add(type, field); keys.Add(key, field);
            }
        }
        private static string Key(FieldInfo field) => Prefix + CanonicalCarryGraphCodec.Identity(field.DeclaringType!)
            + "/" + field.Module.ModuleVersionId.ToString("D") + "/" + field.Name;
        private static Array Read(FieldInfo field)
        {
            var value = field.GetValue(null) as Array;
            if (value == null || value.GetType() != field.FieldType || value.Rank != 1 || value.Length != 0 || value.GetLowerBound(0) != 0)
                throw new InvalidDataException("Framework List static empty-array value differs.");
            return value;
        }
        internal Array? ObserveList(object owner)
        {
            if (!listFields.TryGetValue(owner.GetType(), out var field)) return null;
            // Capture reaches an actual source List instance before its _items.
            // Reuse that framework type's initialized singleton. In the target,
            // resolving a required external may realize only the audited pure
            // zero-array initializer; it cannot run element/application code.
            if (!observedValues.TryGetValue(field.FieldType, out var value)) {
                value = Read(field); observedValues.Add(field.FieldType, value);
            }
            return value;
        }
        internal string? Bind(object value)
        {
            if (!(value is Array array) || array.Length != 0) return null;
            var type = array.GetType();
            return observedValues.TryGetValue(type, out var singleton) && ReferenceEquals(array, singleton) ? bindings[type] : null;
        }
        internal object Resolve(string binding, Func<string, object> other)
        {
            if (!binding.StartsWith(Prefix, StringComparison.Ordinal)) return other(binding);
            if (!keys.TryGetValue(binding, out var field)) throw new InvalidDataException("Framework List static empty-array binding is unregistered.");
            return Read(field);
        }
    }
}
