using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentValueAccess
    {
        private static readonly Dictionary<FieldInfo, Func<object,object?>> StaticReads = new Dictionary<FieldInfo, Func<object,object?>>();
        private static readonly Dictionary<Tuple<FieldInfo,bool>, Action<object?>> StaticWrites = new Dictionary<Tuple<FieldInfo,bool>, Action<object?>>();

        // These control owners prove initialization before enumerating statics.
        // Literals have no storage and must not go through ldsfld.
        internal static object? ReadOwner(object? owner, FieldInfo field)
        {
            if (!field.IsStatic) return ReadField(owner!,field);
            if (field.IsLiteral) {
                var raw = field.GetRawConstantValue();
                return field.FieldType.IsEnum ? Enum(field.FieldType, raw!) : raw;
            }
            if (field.FieldType.IsPointer || field.FieldType.IsByRef) throw Error("unclassified control pointer");
            Func<object,object?> read;
            lock (Gate) {
                if (!StaticReads.TryGetValue(field,out read!)) {
                    read = MakeReader(field.FieldType,il => {
                        if (field.GetRequiredCustomModifiers().Contains(typeof(IsVolatile))) il.Emit(OpCodes.Volatile);
                        il.Emit(OpCodes.Ldsfld,field);
                    });
                    StaticReads.Add(field,read);
                }
            }
            return Normalize(read(null!));
        }
        internal static void WriteOwner(object? owner, FieldInfo field, object? value)
        {
            if (!field.IsStatic) { WriteField(owner!,field,value); return; }
            if (field.IsLiteral || field.FieldType.IsPointer || field.FieldType.IsByRef) throw Error("unwritable control field");
            value = Prepared(field.FieldType,value);
            var typed = value is Value;
            var key = Tuple.Create(field,typed); Action<object?> write;
            lock (Gate) {
                if (!StaticWrites.TryGetValue(key,out write!)) {
                    var m = new DynamicMethod("resident_write_initialized_control_static",typeof(void),new[]{typeof(object)},typeof(ResidentValueAccess).Module,true);
                    var il = m.GetILGenerator(); Source(il,field.FieldType,typed,0);
                    if (field.GetRequiredCustomModifiers().Contains(typeof(IsVolatile))) il.Emit(OpCodes.Volatile);
                    il.Emit(OpCodes.Stsfld,field); il.Emit(OpCodes.Ret);
                    write = (Action<object?>)m.CreateDelegate(typeof(Action<object?>)); StaticWrites.Add(key,write);
                }
            }
            write(value is Value slot ? slot.Storage : value is EnumValue scalar ? scalar.Bits : value);
        }
        internal static bool SameControlValue(object? a, object? b, Type declared, bool stringByValue)
        {
            if (a == null || b == null) return a == null && b == null;
            var type = Nullable.GetUnderlyingType(declared) ?? declared;
            if (!type.IsValueType) return type == typeof(string) && stringByValue ? string.Equals((string)a,(string)b,StringComparison.Ordinal) : ReferenceEquals(a,b);
            if (TypeOf(a) != type || TypeOf(b) != type) throw Error("control value type differs");
            if (type.IsEnum) return Scalar(a).Equals(Scalar(b));
            if (type == typeof(float)) return BitConverter.ToInt32(BitConverter.GetBytes((float)a),0) == BitConverter.ToInt32(BitConverter.GetBytes((float)b),0);
            if (type == typeof(double)) return BitConverter.DoubleToInt64Bits((double)a) == BitConverter.DoubleToInt64Bits((double)b);
            if (type == typeof(decimal)) return decimal.GetBits((decimal)a).SequenceEqual(decimal.GetBits((decimal)b));
            if (type.IsPrimitive) return a.Equals(b);
            foreach (var f in CanonicalCarryGraphCodec.Fields(type))
                if (!SameControlValue(ReadField(a,f),ReadField(b,f),f.FieldType,stringByValue)) return false;
            return true;
        }
        internal static object? DefaultControlValue(Type type)
        {
            if (!type.IsValueType || Nullable.GetUnderlyingType(type) != null) return null;
            if (type.IsEnum) return Enum(type,DefaultControlValue(System.Enum.GetUnderlyingType(type))!);
            if (type.IsPrimitive || type == typeof(decimal)) return Activator.CreateInstance(type);
            return Create(type);
        }
    }
}
