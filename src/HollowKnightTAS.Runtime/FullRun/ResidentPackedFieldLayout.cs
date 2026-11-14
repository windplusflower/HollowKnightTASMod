using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HollowKnightTAS.Core.SceneCheckpoints;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Packs only pointer/reference-free value fields. Reference slots and semantic
    // instance IDs remain explicit graph edges. Layout comes from the exact type ABI.
    internal sealed class ResidentPackedFieldLayout
    {
        private static readonly ConcurrentDictionary<Type, ResidentPackedFieldLayout> Cache = new ConcurrentDictionary<Type, ResidentPackedFieldLayout>();
        internal readonly int Bytes;
        internal readonly int[] References;
        private readonly Action<object, byte[]>? read, write;

        internal static ResidentPackedFieldLayout For(Type type, FieldInfo[] fields, ResidentGraphPolicy policy)
            => Cache.GetOrAdd(type, _ => new ResidentPackedFieldLayout(type, fields, policy));

        private ResidentPackedFieldLayout(Type type, FieldInfo[] fields, ResidentGraphPolicy policy)
        {
            var values = new List<(FieldInfo Field, int Offset, int Width)>();
            var references = new List<int>();
            for (var i = 0; i < fields.Length; i++) {
                var field = fields[i];
                var width = type.IsValueType || !field.FieldType.IsValueType || policy.InstanceIdTarget(field) != null
                    ? 0 : CanonicalCarryGraphCodec.PackedWidth(field.FieldType.MakeArrayType());
                if (width == 0) { references.Add(i); continue; }
                values.Add((field, Bytes, width)); Bytes = checked(Bytes + width);
                if (Bytes > SceneCheckpointPacket.MaximumPayloadBytes) throw new InvalidDataException("Packed field layout exceeds its byte bound.");
            }
            References = references.ToArray();
            if (Bytes == 0) return;
            Action<object, byte[]> Copy(bool toOwner) {
                var method = new DynamicMethod(toOwner ? "resident_write_value_fields" : "resident_read_value_fields", typeof(void),
                    new[] { typeof(object), typeof(byte[]) }, typeof(ResidentPackedFieldLayout).Module, true);
                var il = method.GetILGenerator();
                foreach (var value in values) {
                    void Owner() { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, value.Field.DeclaringType!); il.Emit(OpCodes.Ldflda, value.Field); }
                    void Buffer() { il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldc_I4, value.Offset); il.Emit(OpCodes.Ldelema, typeof(byte)); }
                    if (toOwner) { Owner(); Buffer(); } else { Buffer(); Owner(); }
                    il.Emit(OpCodes.Ldc_I4, value.Width);
                    if (value.Field.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsVolatile))) il.Emit(OpCodes.Volatile);
                    il.Emit(OpCodes.Cpblk);
                }
                il.Emit(OpCodes.Ret);
                return (Action<object, byte[]>)method.CreateDelegate(typeof(Action<object, byte[]>));
            }
            read = Copy(false); write = Copy(true);
        }
        internal byte[] Read(object owner)
        {
            var value = new byte[Bytes];
            read?.Invoke(owner, value);
            return value;
        }
        internal void Write(object owner, byte[] value)
        {
            if (value == null || value.Length != Bytes) throw new InvalidDataException("Packed field payload length differs.");
            write?.Invoke(owner, value);
        }
        internal bool Matches(object owner, byte[] expected)
        {
            if (expected.Length != Bytes) return false;
            var actual = Read(owner);
            for (var i = 0; i < actual.Length; i++) if (actual[i] != expected[i]) return false;
            return true;
        }
    }
}
