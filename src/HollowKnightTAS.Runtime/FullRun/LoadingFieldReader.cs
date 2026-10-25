using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Cache only a direct ldfld accessor. It cannot call a property, constructor,
    // enumerator or application method, and does not retain observed owners.
    internal sealed class LoadingFieldReader
    {
        private const int MaximumSharedReadbackAccessors = 16384;
        private static readonly object sharedReadbackGate = new object();
        // Runtime metadata belongs to this loading AppDomain. Closed declaring
        // and field types distinguish generic instantiations sharing a token.
        private static readonly Dictionary<Tuple<Module, Type, int, Type>, Func<object, object?>> sharedReadback
            = new Dictionary<Tuple<Module, Type, int, Type>, Func<object, object?>>();
        internal static int SharedReadbackAccessorCount { get { lock (sharedReadbackGate) return sharedReadback.Count; } }
        internal static Func<object, object?> PrepareSharedReadback(FieldInfo field)
        {
            Tuple<Module, Type, int, Type> key;
            try { key = Tuple.Create(field.Module, field.DeclaringType!, field.MetadataToken, field.FieldType); }
            catch (InvalidOperationException) { return field.GetValue; }
            catch (NotSupportedException) { return field.GetValue; }
            lock (sharedReadbackGate) {
                if (sharedReadback.TryGetValue(key, out var cached)) return cached;
                if (sharedReadback.Count >= MaximumSharedReadbackAccessors) return field.GetValue;
                Func<object, object?> reader;
                try { reader = new LoadingFieldReader(true, preserveReflectionFailures: true).Prepare(field); }
                catch (NotSupportedException) { reader = field.GetValue; }
                sharedReadback.Add(key, reader); return reader;
            }
        }
        private readonly bool compiled;
        private readonly bool preserveReflectionFailures;
        private readonly bool sharedAccessors;
        private readonly Dictionary<FieldInfo, Func<object, object?>> readers = new Dictionary<FieldInfo, Func<object, object?>>();
        private delegate void FloatingWriter(LoadingFieldDigest digest, object owner, int depth);
        private static readonly Dictionary<Tuple<Module, Type, int, Type>, FloatingWriter> sharedFloatingWriters = new Dictionary<Tuple<Module, Type, int, Type>, FloatingWriter>();
        private readonly Dictionary<FieldInfo, FloatingWriter> floatingWriters = new Dictionary<FieldInfo, FloatingWriter>();

        internal LoadingFieldReader(bool compiled, bool preserveReflectionFailures = false, bool sharedAccessors = false)
        { this.compiled = compiled; this.preserveReflectionFailures = preserveReflectionFailures; this.sharedAccessors = sharedAccessors; }

        internal bool AppendFloating(FieldInfo field, object owner, LoadingFieldDigest digest, int depth)
        {
            if (!compiled || (field.FieldType != typeof(float) && field.FieldType != typeof(double))) return false;
            if (!floatingWriters.TryGetValue(field, out var writer))
            {
                if (field.IsStatic || field.DeclaringType == null || field.DeclaringType.ContainsGenericParameters)
                    throw new InvalidOperationException("P0 direct floating reader requires a closed managed instance field.");
                if (sharedAccessors) {
                    var key = Tuple.Create(field.Module, field.DeclaringType, field.MetadataToken, field.FieldType);
                    lock (sharedReadbackGate) {
                        if (!sharedFloatingWriters.TryGetValue(key, out writer)) {
                            writer = new LoadingFieldReader(true).PrepareFloating(field);
                            if (sharedFloatingWriters.Count < MaximumSharedReadbackAccessors) sharedFloatingWriters.Add(key, writer);
                        }
                    }
                } else writer = PrepareFloating(field);
                floatingWriters.Add(field, writer);
            }
            writer(digest, owner, depth);
            return true;
        }
        private FloatingWriter PrepareFloating(FieldInfo field)
        {
            var method = new DynamicMethod("hktas_p0_append_float", typeof(void),
                new[] { typeof(LoadingFieldDigest), typeof(object), typeof(int) }, typeof(LoadingFieldReader).Module, true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(field.DeclaringType!.IsValueType ? OpCodes.Unbox : OpCodes.Castclass, field.DeclaringType);
            if (field.GetRequiredCustomModifiers().Contains(typeof(IsVolatile))) il.Emit(OpCodes.Volatile);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Call, typeof(LoadingFieldDigest).GetMethod(field.FieldType == typeof(float)
                ? nameof(LoadingFieldDigest.AppendSingleBits) : nameof(LoadingFieldDigest.AppendDoubleBits),
                BindingFlags.Instance | BindingFlags.NonPublic)!);
            il.Emit(OpCodes.Ret);
            return (FloatingWriter)method.CreateDelegate(typeof(FloatingWriter));
        }

        internal object? Read(FieldInfo field, object owner)
        { if (!compiled) return field.GetValue(owner); return Prepare(field)(owner); }

        // Metadata only: neither compilation nor the cached delegate retains
        // an observed owner or value. Each invocation executes a fresh ldfld.
        internal Func<object, object?> Prepare(FieldInfo field)
        {
            if (!compiled) return field.GetValue;
            if (!readers.TryGetValue(field, out var reader))
            {
                if (field.IsStatic || field.DeclaringType == null || field.DeclaringType.ContainsGenericParameters
                    || field.FieldType.IsByRef || field.FieldType.IsPointer) {
                    if (preserveReflectionFailures) return field.GetValue;
                    throw new InvalidOperationException("P0 direct field reader requires a closed managed instance field.");
                }
                if (sharedAccessors) {
                    reader = PrepareSharedReadback(field); readers.Add(field, reader); return reader;
                }
                var arguments = preserveReflectionFailures ? new[] { typeof(FieldInfo), typeof(object) } : new[] { typeof(object) };
                var method = new DynamicMethod("hktas_p0_read_field", typeof(object), arguments,
                    typeof(LoadingFieldReader).Module, true);
                var il = method.GetILGenerator();
                if (preserveReflectionFailures) {
                    var valid = il.DefineLabel();
                    il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Isinst, field.DeclaringType); il.Emit(OpCodes.Brtrue, valid);
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Callvirt, typeof(FieldInfo).GetMethod(nameof(FieldInfo.GetValue))!); il.Emit(OpCodes.Ret);
                    il.MarkLabel(valid);
                }
                il.Emit(preserveReflectionFailures ? OpCodes.Ldarg_1 : OpCodes.Ldarg_0);
                il.Emit(field.DeclaringType.IsValueType ? OpCodes.Unbox : OpCodes.Castclass, field.DeclaringType);
                if (field.GetRequiredCustomModifiers().Contains(typeof(IsVolatile))) il.Emit(OpCodes.Volatile);
                il.Emit(OpCodes.Ldfld, field);
                if (field.FieldType.IsValueType) il.Emit(OpCodes.Box, field.FieldType);
                il.Emit(OpCodes.Ret);
                try { reader = (Func<object, object?>)(preserveReflectionFailures
                    ? method.CreateDelegate(typeof(Func<object, object?>), field) : method.CreateDelegate(typeof(Func<object, object?>))); }
                catch (NotSupportedException) when (preserveReflectionFailures) { reader = field.GetValue; }
                readers.Add(field, reader);
            }
            return reader;
        }
    }
}
