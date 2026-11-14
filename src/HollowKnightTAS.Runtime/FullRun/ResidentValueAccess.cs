using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Only accessor metadata/delegates are cached. Every value slot is created
    // per read/parse and belongs to that observation, never a gameplay-field cache.
    internal static partial class ResidentValueAccess
    {
        private sealed class Slot<T> { public T Value = default!; }
        internal sealed class Value
        {
            internal readonly Type Type;
            internal readonly object Storage;
            internal Value(Type type, object storage) { Type = type; Storage = storage; }
        }
        internal sealed class EnumValue
        {
            internal readonly Type Type;
            internal readonly object Bits;
            internal EnumValue(Type type, object bits) { Type = type; Bits = bits; }
        }
        private sealed class Template
        {
            internal readonly Type Type;
            internal readonly FieldInfo Field;
            internal Template(Type value) { Type = typeof(Slot<>).MakeGenericType(value); Field = Type.GetField("Value")!; }
            internal object Create() => FormatterServices.GetUninitializedObject(Type);
        }
        private static readonly object Gate = new object();
        private static readonly Dictionary<Type, Template> Templates = new Dictionary<Type, Template>();
        private static readonly Dictionary<Tuple<FieldInfo, Type?>, Func<object, object?>> Readers = new Dictionary<Tuple<FieldInfo, Type?>, Func<object, object?>>();
        private static readonly Dictionary<Tuple<FieldInfo, Type?, bool>, Action<object, object?>> Writers = new Dictionary<Tuple<FieldInfo, Type?, bool>, Action<object, object?>>();
        private static readonly Dictionary<Type, Func<Array, int, object?>> ArrayReaders = new Dictionary<Type, Func<Array, int, object?>>();
        private static readonly Dictionary<Tuple<Type, bool>, Action<Array, int, object?>> ArrayWriters = new Dictionary<Tuple<Type, bool>, Action<Array, int, object?>>();
        private static Template Get(Type type) { lock (Gate) { if (!Templates.TryGetValue(type, out var template)) { template = new Template(type); Templates.Add(type, template); } return template; } }
        internal static bool IsView(object value) => value is Value || value is EnumValue;
        internal static Type TypeOf(object value) => value is Value slot ? slot.Type : value is EnumValue scalar ? scalar.Type : value.GetType();
        internal static object Scalar(object value) => value is EnumValue scalar ? scalar.Bits : value;
        internal static object Enum(Type type, object bits)
        {
            if (!type.IsEnum || bits.GetType() != System.Enum.GetUnderlyingType(type)) throw Error("enum underlying storage differs");
            return new EnumValue(type, bits);
        }
        private static readonly Dictionary<Type, Delegate> TypedReturns = new Dictionary<Type, Delegate>();
        internal static T ToValue<T>(object? value)
        {
            var target = typeof(T);
            if (value is Value exact && exact.Type == target) return ((Slot<T>)exact.Storage).Value;
            if (Nullable.GetUnderlyingType(target) != null) { var nullable = (Value)Prepared(target, value)!; return ((Slot<T>)nullable.Storage).Value; }
            if (value == null) return default!;
            if (value is EnumValue scalar) {
                if (scalar.Type != target) throw Error("typed enum conversion has another type");
                Func<object, T> read;
                lock (Gate) {
                    if (!TypedReturns.TryGetValue(target, out var cached)) {
                        var method = new DynamicMethod("resident_return_typed_enum", target, new[] { typeof(object) }, typeof(ResidentValueAccess).Module, true);
                        var il = method.GetILGenerator(); Source(il, target, false, 0); il.Emit(OpCodes.Ret);
                        cached = method.CreateDelegate(typeof(Func<object, T>)); TypedReturns.Add(target, cached);
                    }
                    read = (Func<object, T>)cached;
                }
                return read(scalar.Bits);
            }
            if (IsView(value)) throw Error("a typed value view cannot become a reference/box implicitly");
            return (T)value;
        }
        internal static object FromValue<T>(T value) where T : struct
        {
            var type = typeof(T);
            if (type.IsPrimitive || type.IsEnum || type == typeof(decimal)) throw Error("FromValue requires composite struct storage");
            var storage = (Slot<T>)FormatterServices.GetUninitializedObject(typeof(Slot<T>)); storage.Value = value;
            return new Value(type, storage);
        }
        internal static object Create(Type type)
        {
            if (!type.IsValueType || type.IsPrimitive || type.IsEnum || type == typeof(decimal)) throw Error("typed slot requires a composite value type");
            return new Value(type, Get(type).Create());
        }
        internal static Func<object, object?> PrepareRead(FieldInfo field)
        {
            if (field.IsStatic || field.FieldType.IsPointer || field.FieldType.IsByRef) return owner => ReadField(owner, field);
            if (field.DeclaringType!.IsValueType) return owner => ReadField(owner, field);
            var reader = ReaderFor(field, null);
            return owner => Normalize(reader(owner));
        }
        internal delegate int FirstReference(object owner, out object? value);
        internal static FirstReference? PrepareFirstReference(Type ownerType, FieldInfo[] fields)
        {
            if (ownerType.IsValueType || fields.Any(field => field.FieldType.IsValueType)) return null;
            var method = new DynamicMethod("resident_first_reference", typeof(int), new[] { typeof(object), typeof(object).MakeByRefType() },
                typeof(ResidentValueAccess).Module, true);
            var il = method.GetILGenerator(); var value = il.DeclareLocal(typeof(object));
            for (var index = 0; index < fields.Length; index++) {
                var field = fields[index]; var next = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, field.DeclaringType!);
                if (field.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsVolatile))) il.Emit(OpCodes.Volatile);
                il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Stloc, value); il.Emit(OpCodes.Ldloc, value); il.Emit(OpCodes.Brfalse, next);
                il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldloc, value); il.Emit(OpCodes.Stind_Ref);
                il.Emit(OpCodes.Ldc_I4, index); il.Emit(OpCodes.Ret); il.MarkLabel(next);
            }
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Stind_Ref); il.Emit(OpCodes.Ldc_I4_M1); il.Emit(OpCodes.Ret);
            return (FirstReference)method.CreateDelegate(typeof(FirstReference));
        }
        internal static object? ReadField(object owner, FieldInfo field)
        {
            if (owner == null || field == null || field.IsStatic || field.FieldType.IsPointer || field.FieldType.IsByRef) throw Error("instance field access differs");
            var slot = owner as Value;
            return Normalize(ReaderFor(field, slot?.Type)(slot?.Storage ?? owner));
        }
        private static Func<object, object?> ReaderFor(FieldInfo field, Type? slotType)
        {
            var key = Tuple.Create(field, slotType);
            lock (Gate) {
                if (!Readers.TryGetValue(key, out var reader)) {
                    reader = MakeReader(field.FieldType, il => {
                        Owner(il, field.DeclaringType!, slotType);
                        if (field.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsVolatile))) il.Emit(OpCodes.Volatile);
                        il.Emit(OpCodes.Ldfld, field);
                    });
                    Readers.Add(key, reader);
                }
                return reader;
            }
        }
        private static Func<object, object?> MakeReader(Type type, Action<ILGenerator> load)
        {
            if (!type.IsValueType || type.IsPrimitive || type.IsEnum || type == typeof(decimal)) {
                var method = new DynamicMethod("resident_read_unboxed_scalar", typeof(object), new[] { typeof(object) }, typeof(ResidentValueAccess).Module, true);
                var il = method.GetILGenerator(); load(il);
                if (type.IsValueType) il.Emit(OpCodes.Box, type.IsEnum ? System.Enum.GetUnderlyingType(type) : type);
                il.Emit(OpCodes.Ret); var read = (Func<object, object?>)method.CreateDelegate(typeof(Func<object, object?>));
                return type.IsEnum ? owner => new EnumValue(type, read(owner)!) : read;
            }
            var template = Get(type);
            var copy = new DynamicMethod("resident_read_unboxed_value", typeof(void), new[] { typeof(object), typeof(object) }, typeof(ResidentValueAccess).Module, true);
            var generator = copy.GetILGenerator(); generator.Emit(OpCodes.Ldarg_1); generator.Emit(OpCodes.Castclass, template.Type); load(generator); generator.Emit(OpCodes.Stfld, template.Field); generator.Emit(OpCodes.Ret);
            var assign = (Action<object, object>)copy.CreateDelegate(typeof(Action<object, object>));
            return owner => { var storage = template.Create(); assign(owner, storage); return new Value(type, storage); };
        }
        private static object? Normalize(object? value)
        {
            if (!(value is Value slot) || Nullable.GetUnderlyingType(slot.Type) == null) return value;
            var fields = NullableFields(slot.Type); var present = (bool)ReadField(slot, fields.Item1)!;
            var contents = ReadField(slot, fields.Item2);
            if (present) return contents;
            var underlying = Nullable.GetUnderlyingType(slot.Type)!;
            if (!SameControlValue(contents, DefaultControlValue(underlying), underlying, false)) throw Error("nullable null has a nondefault hidden value not representable by the existing wire");
            return null;
        }
        private static Tuple<FieldInfo, FieldInfo> NullableFields(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? throw Error("nullable storage type differs");
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var has = fields.SingleOrDefault(field => field.Name.Replace("_", "").Equals("hasvalue", StringComparison.OrdinalIgnoreCase) && field.FieldType == typeof(bool));
            var value = fields.SingleOrDefault(field => field.Name.Equals("value", StringComparison.OrdinalIgnoreCase) && field.FieldType == underlying);
            if (fields.Length != 2 || has == null || value == null) throw Error("nullable actual field ABI differs");
            return Tuple.Create(has, value);
        }
        private static void Owner(ILGenerator il, Type declaring, Type? slot)
        {
            il.Emit(OpCodes.Ldarg_0);
            if (slot != null) { var template = Get(slot); il.Emit(OpCodes.Castclass, template.Type); il.Emit(OpCodes.Ldflda, template.Field); }
            else il.Emit(declaring.IsValueType ? OpCodes.Unbox : OpCodes.Castclass, declaring);
        }
        private static object? Prepared(Type target, object? value)
        {
            var underlying = Nullable.GetUnderlyingType(target);
            if (underlying == null) return value;
            var result = (Value)Create(target); if (value == null) return result;
            if (TypeOf(value) != underlying) throw Error("nullable value has another actual type");
            var fields = NullableFields(target); WriteField(result, fields.Item2, value); WriteField(result, fields.Item1, true); return result;
        }
        internal static void WriteField(object owner, FieldInfo field, object? value)
        {
            if (owner == null || field == null || field.IsStatic) throw Error("instance field write differs");
            var slot = owner as Value; value = Prepared(field.FieldType, value); var typed = value is Value;
            var key = Tuple.Create(field, slot?.Type, typed); Action<object, object?> writer;
            lock (Gate) {
                if (!Writers.TryGetValue(key, out writer!)) {
                    var method = new DynamicMethod("resident_write_unboxed_value", typeof(void), new[] { typeof(object), typeof(object) }, typeof(ResidentValueAccess).Module, true);
                    var il = method.GetILGenerator(); Owner(il, field.DeclaringType!, slot?.Type); Source(il, field.FieldType, typed, 1); if (field.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsVolatile))) il.Emit(OpCodes.Volatile); il.Emit(OpCodes.Stfld, field); il.Emit(OpCodes.Ret);
                    writer = (Action<object, object?>)method.CreateDelegate(typeof(Action<object, object?>)); Writers.Add(key, writer);
                }
            }
            writer(slot?.Storage ?? owner, value is Value actual ? actual.Storage : value is EnumValue scalar ? scalar.Bits : value);
        }
        private static void Source(ILGenerator il, Type type, bool typed, int argument)
        {
            il.Emit(OpCodes.Ldarg, (short)argument);
            if (typed) { var template = Get(type); il.Emit(OpCodes.Castclass, template.Type); il.Emit(OpCodes.Ldfld, template.Field); }
            else if (type.IsValueType) il.Emit(OpCodes.Unbox_Any, type.IsEnum ? System.Enum.GetUnderlyingType(type) : type);
            else if (type != typeof(object)) il.Emit(OpCodes.Castclass, type);
        }
        private static void ArrayAddress(ILGenerator il, Type array)
        {
            var element = array.GetElementType()!; il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, array); il.Emit(OpCodes.Ldarg_1);
            if (array == element.MakeArrayType()) il.Emit(OpCodes.Ldelema, element);
            else {
                var address = array.GetMethod("Address", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int) }, null);
                if (address == null || address.ReturnType != element.MakeByRefType()) throw Error("non-vector array address ABI differs");
                il.Emit(OpCodes.Call, address);
            }
        }
        internal static object? ReadArray(Array array, int index)
        {
            if (array.Rank != 1 || array.GetLowerBound(0) != 0 || index < 0 || index >= array.Length) throw Error("array element index/shape differs");
            var type = array.GetType(); var element = type.GetElementType()!; Func<Array, int, object?> reader;
            lock (Gate) {
                if (!ArrayReaders.TryGetValue(type, out reader!)) {
                    var composite = element.IsValueType && !element.IsPrimitive && !element.IsEnum && element != typeof(decimal); var template = composite ? Get(element) : null;
                    var method = new DynamicMethod("resident_array_read_unboxed", composite ? typeof(void) : typeof(object), composite ? new[] { typeof(Array), typeof(int), typeof(object) } : new[] { typeof(Array), typeof(int) }, typeof(ResidentValueAccess).Module, true);
                    var il = method.GetILGenerator();
                    if (composite) { il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Castclass, template!.Type); }
                    ArrayAddress(il, type); il.Emit(OpCodes.Ldobj, element);
                    if (composite) il.Emit(OpCodes.Stfld, template!.Field); else if (element.IsValueType) il.Emit(OpCodes.Box, element.IsEnum ? System.Enum.GetUnderlyingType(element) : element);
                    il.Emit(OpCodes.Ret);
                    if (composite) { var copy = (Action<Array, int, object>)method.CreateDelegate(typeof(Action<Array, int, object>)); reader = (owner, at) => { var storage = template!.Create(); copy(owner, at, storage); return new Value(element, storage); }; }
                    else { var read = (Func<Array, int, object?>)method.CreateDelegate(typeof(Func<Array, int, object?>)); reader = element.IsEnum ? (owner, at) => new EnumValue(element, read(owner, at)!) : read; }
                    ArrayReaders.Add(type, reader);
                }
            }
            return Normalize(reader(array, index));
        }
        internal static void WriteArray(Array array, int index, object? value)
        {
            if (array.Rank != 1 || array.GetLowerBound(0) != 0 || index < 0 || index >= array.Length) throw Error("array element write shape differs");
            var type = array.GetType(); var element = type.GetElementType()!; value = Prepared(element, value); var typed = value is Value;
            var key = Tuple.Create(type, typed); Action<Array, int, object?> writer;
            lock (Gate) {
                if (!ArrayWriters.TryGetValue(key, out writer!)) {
                    var method = new DynamicMethod("resident_array_write_unboxed", typeof(void), new[] { typeof(Array), typeof(int), typeof(object) }, typeof(ResidentValueAccess).Module, true);
                    var il = method.GetILGenerator(); ArrayAddress(il, type); Source(il, element, typed, 2); il.Emit(OpCodes.Stobj, element); il.Emit(OpCodes.Ret);
                    writer = (Action<Array, int, object?>)method.CreateDelegate(typeof(Action<Array, int, object?>)); ArrayWriters.Add(key, writer);
                }
            }
            writer(array, index, value is Value actual ? actual.Storage : value is EnumValue scalar ? scalar.Bits : value);
        }
        // Explicitly reserved for nodes whose incoming object/interface edge
        // proves that the source already contained a real box. Never a field reader.
        internal static object BoxValue(object value)
        {
            var type = TypeOf(value); var typed = value is Value;
            var method = new DynamicMethod("resident_materialize_existing_box_kind", typeof(object), new[] { typeof(object) }, typeof(ResidentValueAccess).Module, true);
            var il = method.GetILGenerator(); Source(il, type, typed, 0); il.Emit(OpCodes.Box, type); il.Emit(OpCodes.Ret);
            return ((Func<object, object>)method.CreateDelegate(typeof(Func<object, object>)))(value is Value slot ? slot.Storage : Scalar(value));
        }
        internal static void WriteBox(object owner, object value, Type type)
        {
            var typed = value is Value;
            var method = new DynamicMethod("resident_write_existing_box", typeof(void), new[] { typeof(object), typeof(object) }, typeof(ResidentValueAccess).Module, true);
            var il = method.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Unbox, type); Source(il, type, typed, 1); il.Emit(OpCodes.Stobj, type); il.Emit(OpCodes.Ret);
            ((Action<object, object>)method.CreateDelegate(typeof(Action<object, object>)))(owner, value is Value slot ? slot.Storage : Scalar(value));
        }
        private static InvalidDataException Error(string reason) => new InvalidDataException("ResidentValueAccess: " + reason);
    }
}
