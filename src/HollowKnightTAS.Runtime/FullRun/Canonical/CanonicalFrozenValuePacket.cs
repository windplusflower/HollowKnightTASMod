using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace HollowKnightTAS.Runtime.FullRun
{
    // A bounded, read-only configuration packet inside the input graph. It is
    // compared in full, never deserialized into game owners or used as a digest
    // substitute for mutable gameplay data. Only frozen raw fields are read.
    internal static class CanonicalFrozenValuePacket
    {
        internal const int MaximumBytes = 1024 * 1024;
        internal static byte[] Capture(object[] roots, Func<Type, FieldInfo[]> fields, Action<Type> validateType, Action<object>? referenceObserved = null)
        {
            if (roots == null || roots.Length > 4096) throw new InvalidDataException("Frozen configuration root bound exceeded.");
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            var nodes = new Dictionary<object, int>(new References());
            var types = new Dictionary<Type, int>(); var fieldCount = 0;
            var layouts = new Dictionary<Type, FieldInfo[]>();
            void Bound() { if (stream.Length > MaximumBytes) throw new InvalidDataException("Frozen configuration byte bound exceeded."); }
            void Text(string value)
            {
                if (value.Length > MaximumBytes / 2) throw new InvalidDataException("Frozen configuration string bound exceeded.");
                writer.Write(value.Length);
                // Preserve UTF-16 code units, including unpaired surrogates.
                foreach (var character in value) writer.Write((ushort)character);
                Bound();
            }
            void TypeId(Type type)
            {
                validateType(type);
                if (types.TryGetValue(type, out var known)) { writer.Write(known); return; }
                writer.Write(~types.Count); types.Add(type, types.Count);
                Text(type.Module.ModuleVersionId.ToString("D")); Text(type.FullName ?? throw new InvalidDataException("Unnamed configuration type."));
                if (!type.IsPrimitive && !type.IsEnum && !type.IsArray && type != typeof(string)) {
                    var layout = fields(type); layouts.Add(type, layout); writer.Write(layout.Length);
                    foreach (var field in layout) {
                        Text(field.DeclaringType!.FullName!); Text(field.Name); Text(field.FieldType.FullName!);
                    }
                }
            }
            void Value(object? value, int depth)
            {
                if (depth > 64) throw new InvalidDataException("Frozen configuration depth bound exceeded.");
                if (value == null) { writer.Write((byte)0); return; }
                var type = value.GetType();
                if (!type.IsValueType) {
                    if (nodes.TryGetValue(value, out var known)) { writer.Write((byte)1); writer.Write(known); return; }
                    if (nodes.Count >= 20000) throw new InvalidDataException("Frozen configuration node bound exceeded.");
                    writer.Write((byte)2); writer.Write(nodes.Count); nodes.Add(value, nodes.Count);
                    referenceObserved?.Invoke(value);
                } else writer.Write((byte)3);
                TypeId(type);
                if (type.IsEnum) Value(Convert.ChangeType(value, Enum.GetUnderlyingType(type)), depth + 1);
                else if (value is bool boolean) writer.Write(boolean);
                else if (value is byte unsignedByte) writer.Write(unsignedByte);
                else if (value is short signedShort) writer.Write(signedShort);
                else if (value is ushort unsignedShort) writer.Write(unsignedShort);
                else if (value is int signedInteger) writer.Write(signedInteger);
                else if (value is uint unsignedInteger) writer.Write(unsignedInteger);
                else if (value is float single) writer.Write(single);
                else if (value is string text) Text(text);
                else if (value is Array array) {
                    if (array.Rank != 1 || array.GetLowerBound(0) != 0 || array.Length > 20000)
                        throw new InvalidDataException("Frozen configuration array shape or bound differs.");
                    writer.Write(array.Length);
                    for (var index = 0; index < array.Length; index++) Value(array.GetValue(index), depth + 1);
                } else {
                    if (!layouts.TryGetValue(type, out var layout)) throw new InvalidDataException("Frozen configuration scalar type is unregistered.");
                    foreach (var field in layout) {
                        if (++fieldCount > 200000) throw new InvalidDataException("Frozen configuration field bound exceeded.");
                        Value(field.GetValue(value), depth + 1);
                    }
                }
                Bound();
            }
            writer.Write(0x31504346); writer.Write(roots.Length); // FCP1
            foreach (var root in roots) Value(root, 0);
            Bound(); return stream.ToArray();
        }
        private sealed class References : IEqualityComparer<object>
        {
            public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
