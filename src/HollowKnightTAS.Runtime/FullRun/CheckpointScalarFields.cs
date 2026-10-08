using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Explicit field selections only. Never evaluates game properties, invokes
    // callbacks, or interprets an omitted reference as a restored field.
    internal static class CheckpointScalarFields
    {
        internal static FieldInfo[] Declared(Type type, bool isStatic = false) => type.GetFields(
            BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic
            | (isStatic ? BindingFlags.Static : BindingFlags.Instance)).Where(f => !f.IsLiteral).OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();
        internal static Dictionary<string, string> Capture(object? target, IEnumerable<FieldInfo> fields)
        {
            var result = new Dictionary<string, string>();
            foreach (var field in fields) CaptureValue(field.GetValue(target), field.FieldType, Key(field), result);
            return result;
        }
        internal static Action PrepareApply(object? target, IEnumerable<FieldInfo> fields, IReadOnlyDictionary<string, string> state)
        {
            var used = new HashSet<string>(StringComparer.Ordinal); var writes = new List<Tuple<FieldInfo, object?>>();
            foreach (var field in fields) writes.Add(Tuple.Create(field, DecodeValue(field.FieldType, Key(field), state, used)));
            if (state.Count != used.Count || state.Keys.Any(key => !used.Contains(key))) throw new InvalidDataException("Checkpoint scalar field layout differs.");
            return () => { foreach (var write in writes) write.Item1.SetValue(target, write.Item2); };
        }
        private static string Key(FieldInfo field) => field.DeclaringType!.FullName + "/" + field.Name;
        private static void CaptureValue(object? value, Type type, string key, IDictionary<string, string> state)
        {
            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable != null)
            { if (value == null) state.Add(key, "nullable:null"); else CaptureValue(value, nullable, key, state); return; }
            if (type == typeof(string)) { state.Add(key, value == null ? "null" : "string:" + (string)value); return; }
            if (type == typeof(float)) { state.Add(key, "f32:" + BitConverter.ToUInt32(BitConverter.GetBytes((float)value!), 0).ToString("x8")); return; }
            if (type == typeof(double)) { state.Add(key, "f64:" + BitConverter.DoubleToInt64Bits((double)value!).ToString("x16")); return; }
            if (type.IsEnum) { CaptureValue(Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture), Enum.GetUnderlyingType(type), key, state); return; }
            if (type.IsPrimitive && type != typeof(IntPtr) && type != typeof(UIntPtr))
            { state.Add(key, type.FullName + ":" + Convert.ToString(value, CultureInfo.InvariantCulture)); return; }
            if (!type.IsValueType || Nullable.GetUnderlyingType(type) != null || Declared(type).Length == 0)
                throw new InvalidDataException("Checkpoint scalar adapter cannot capture " + type.FullName);
            foreach (var field in Declared(type)) CaptureValue(field.GetValue(value), field.FieldType, key + "/" + field.Name, state);
        }
        private static object? DecodeValue(Type type, string key, IReadOnlyDictionary<string, string> state, ISet<string> used)
        {
            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable != null)
            {
                if (state.TryGetValue(key, out var value) && value == "nullable:null")
                { if (!used.Add(key)) throw new InvalidDataException("Duplicate nullable field."); return null; }
                return DecodeValue(nullable, key, state, used);
            }
            if (type.IsPrimitive || type.IsEnum || type == typeof(string))
            {
                if (!state.TryGetValue(key, out var text) || !used.Add(key)) throw new InvalidDataException("Missing checkpoint scalar field: " + key);
                object? value;
                try
                {
                    var scalar = type.IsEnum ? Enum.GetUnderlyingType(type) : type;
                    if (scalar == typeof(string)) value = text == "null" ? null : text.StartsWith("string:", StringComparison.Ordinal)
                        ? text.Substring(7) : throw new InvalidDataException("Invalid checkpoint string.");
                    else if (scalar == typeof(float)) value = BitConverter.ToSingle(BitConverter.GetBytes(uint.Parse(text.Substring(4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)), 0);
                    else if (scalar == typeof(double)) value = BitConverter.Int64BitsToDouble(long.Parse(text.Substring(4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    else if (scalar == typeof(IntPtr) || scalar == typeof(UIntPtr)) throw new InvalidDataException("Checkpoint addresses are forbidden.");
                    else value = Convert.ChangeType(text.Substring(scalar.FullName!.Length + 1), scalar, CultureInfo.InvariantCulture);
                    if (type.IsEnum) value = Enum.ToObject(type, value!);
                }
                catch (Exception error) when (error is FormatException || error is OverflowException || error is ArgumentException)
                { throw new InvalidDataException("Invalid checkpoint scalar: " + key, error); }
                var canonical = new Dictionary<string, string>(); CaptureValue(value, type, key, canonical);
                if (canonical[key] != text) throw new InvalidDataException("Noncanonical checkpoint scalar: " + key);
                return value;
            }
            if (!type.IsValueType || Nullable.GetUnderlyingType(type) != null || Declared(type).Length == 0)
                throw new InvalidDataException("Checkpoint scalar adapter cannot restore " + type.FullName);
            var result = Activator.CreateInstance(type)!;
            foreach (var field in Declared(type)) field.SetValue(result, DecodeValue(field.FieldType, key + "/" + field.Name, state, used));
            return result;
        }
    }
}
