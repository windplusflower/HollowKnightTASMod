using System;
using System.Globalization;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed class CanonicalParkedBoundaryGuard
    {
        private readonly long native;
        private readonly int ordinal;
        private readonly object owner;
        private readonly string source, target, fields;
        internal CanonicalParkedBoundaryGuard(long native, int ordinal, object owner, string source, string target, string fields)
        {
            if (native < 0 || ordinal < 1 || owner == null || string.IsNullOrEmpty(source)
                || string.IsNullOrEmpty(target) || string.IsNullOrEmpty(fields)) throw new ArgumentException("Incomplete parked B identity.");
            this.native = native; this.ordinal = ordinal; this.owner = owner; this.source = source; this.target = target; this.fields = fields;
        }
        internal void Require(long actualNative, bool paused, int actualOrdinal, object actualOwner,
            string actualSource, string actualTarget, string actualFields)
        {
            if (!paused || actualNative != native || actualOrdinal != ordinal || !ReferenceEquals(owner, actualOwner)
                || source != actualSource || target != actualTarget || fields != actualFields)
                throw new InvalidOperationException("Original parked B cursor, owner or load fields changed.");
        }
        internal static void RejectPlayerLoopAdvance() => throw new InvalidOperationException("Parked S0 B cannot execute another PlayerLoop; retire this process.");
        internal CanonicalParkedBoundaryGuard WithVerifiedImportedClock(long importedNative)
            => new CanonicalParkedBoundaryGuard(importedNative, ordinal, owner, source, target, fields);
        internal static string EncodeScalarValue(object? value, Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null) {
                if (!underlying.IsEnum) throw new ArgumentException("Only original nullable enum load fields are admitted.");
                return value == null ? "nullable:null" : "nullable:" + EncodeScalarValue(value, underlying);
            }
            if (type == typeof(string)) {
                if (value == null) return "string:null";
                var text = (string)value; var bytes = new byte[checked(text.Length * 2)];
                for (var index = 0; index < text.Length; index++) { bytes[index * 2] = (byte)text[index]; bytes[index * 2 + 1] = (byte)(text[index] >> 8); }
                return "utf16:" + Convert.ToBase64String(bytes);
            }
            if (value == null || value.GetType() != type) throw new ArgumentException("Load field type or value differs.");
            if (type == typeof(float)) return "f32:" + BitConverter.ToString(BitConverter.GetBytes((float)value));
            if (type == typeof(bool)) return (bool)value ? "bool:1" : "bool:0";
            if (type.IsEnum) return "enum:" + Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            throw new ArgumentException("Unregistered load scalar type.");
        }
    }
}
