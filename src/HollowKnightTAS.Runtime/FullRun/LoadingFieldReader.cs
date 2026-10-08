using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Cache only a direct ldfld accessor. It cannot call a property, constructor,
    // enumerator or application method, and does not retain observed owners.
    internal sealed class LoadingFieldReader
    {
        private readonly bool compiled;
        private readonly Dictionary<FieldInfo, Func<object, object?>> readers = new Dictionary<FieldInfo, Func<object, object?>>();
        private delegate void FloatingWriter(LoadingFieldDigest digest, object owner, int depth);
        private readonly Dictionary<FieldInfo, FloatingWriter> floatingWriters = new Dictionary<FieldInfo, FloatingWriter>();

        internal LoadingFieldReader(bool compiled) { this.compiled = compiled; }

        internal bool AppendFloating(FieldInfo field, object owner, LoadingFieldDigest digest, int depth)
        {
            if (!compiled || (field.FieldType != typeof(float) && field.FieldType != typeof(double))) return false;
            if (!floatingWriters.TryGetValue(field, out var writer))
            {
                if (field.IsStatic || field.DeclaringType == null || field.DeclaringType.ContainsGenericParameters)
                    throw new InvalidOperationException("P0 direct floating reader requires a closed managed instance field.");
                var method = new DynamicMethod("hktas_p0_append_float", typeof(void),
                    new[] { typeof(LoadingFieldDigest), typeof(object), typeof(int) }, typeof(LoadingFieldReader).Module, true);
                var il = method.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(field.DeclaringType.IsValueType ? OpCodes.Unbox : OpCodes.Castclass, field.DeclaringType);
                il.Emit(OpCodes.Ldfld, field);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Call, typeof(LoadingFieldDigest).GetMethod(field.FieldType == typeof(float)
                    ? nameof(LoadingFieldDigest.AppendSingleBits) : nameof(LoadingFieldDigest.AppendDoubleBits),
                    BindingFlags.Instance | BindingFlags.NonPublic)!);
                il.Emit(OpCodes.Ret);
                writer = (FloatingWriter)method.CreateDelegate(typeof(FloatingWriter));
                floatingWriters.Add(field, writer);
            }
            writer(digest, owner, depth);
            return true;
        }

        internal object? Read(FieldInfo field, object owner)
        {
            if (!compiled) return field.GetValue(owner);
            if (!readers.TryGetValue(field, out var reader))
            {
                if (field.IsStatic || field.DeclaringType == null || field.DeclaringType.ContainsGenericParameters
                    || field.FieldType.IsByRef || field.FieldType.IsPointer)
                    throw new InvalidOperationException("P0 direct field reader requires a closed managed instance field.");
                var method = new DynamicMethod("hktas_p0_read_field", typeof(object), new[] { typeof(object) },
                    typeof(LoadingFieldReader).Module, true);
                var il = method.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(field.DeclaringType.IsValueType ? OpCodes.Unbox : OpCodes.Castclass, field.DeclaringType);
                il.Emit(OpCodes.Ldfld, field);
                if (field.FieldType.IsValueType) il.Emit(OpCodes.Box, field.FieldType);
                il.Emit(OpCodes.Ret);
                reader = (Func<object, object?>)method.CreateDelegate(typeof(Func<object, object?>));
                readers.Add(field, reader);
            }
            return reader(owner);
        }
    }
}
