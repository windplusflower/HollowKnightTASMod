using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace HollowKnightTAS.Runtime.FullRun
{
    /// <summary>
    /// Keeps raw field values until a bounded canonical write is committed.
    /// Owners and static declaring types must already be initialized. This
    /// restores field values and reference aliases, not mutations inside the
    /// referenced objects, so callers must replace backing stores when writing.
    /// </summary>
    internal sealed class CanonicalFieldWriteTransaction : IDisposable
    {
        internal const int MaximumFields = 4096;
        private const int MaximumValueDepth = 32;
        private const int MaximumValueFieldReads = 16384;
        private readonly Snapshot[] snapshots;
        private bool committed;
        private bool disposed;

        private CanonicalFieldWriteTransaction(Snapshot[] snapshots) => this.snapshots = snapshots;

        internal readonly struct Target
        {
            internal Target(object? owner, FieldInfo field)
            {
                Owner = owner;
                Field = field ?? throw new ArgumentNullException(nameof(field));
            }

            internal object? Owner { get; }
            internal FieldInfo Field { get; }
        }

        internal static CanonicalFieldWriteTransaction Begin(IEnumerable<Target> targets)
        {
            if (targets == null) throw new ArgumentNullException(nameof(targets));
            var saved = new List<Snapshot>();
            foreach (var target in targets)
            {
                if (saved.Count == MaximumFields)
                    throw new InvalidDataException("Canonical field transaction exceeds its field budget.");
                var field = target.Field ?? throw new ArgumentException("Canonical field transaction has no field.", nameof(targets));
                if (field.IsLiteral)
                    throw new ArgumentException("Canonical field transaction cannot write a literal field.", nameof(targets));
                if (field.IsStatic ? target.Owner != null : target.Owner == null
                    || field.DeclaringType == null || !field.DeclaringType.IsInstanceOfType(target.Owner))
                    throw new ArgumentException("Canonical field transaction owner does not match its field.", nameof(targets));
                // No property, setter, object constructor, or field write is
                // used during preparation. Keep exact old backing references.
                saved.Add(new Snapshot(target, field.GetValue(target.Owner)));
            }
            return new CanonicalFieldWriteTransaction(saved.ToArray());
        }

        internal void Commit()
        {
            if (disposed) throw new ObjectDisposedException(nameof(CanonicalFieldWriteTransaction));
            committed = true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (committed) return;
            var failures = new List<Exception>();
            var valueReadsLeft = MaximumValueFieldReads;
            for (var index = snapshots.Length - 1; index >= 0; index--)
            {
                var snapshot = snapshots[index];
                var field = snapshot.Target.Field;
                var label = (field.DeclaringType?.FullName ?? "<unknown>") + "." + field.Name;
                try { field.SetValue(snapshot.Target.Owner, snapshot.Value); }
                catch (Exception exception)
                {
                    failures.Add(new InvalidDataException("Canonical field rollback write failed: " + label, exception));
                }
                // A setter can fail after a partial write; still read back,
                // and always continue to the remaining fields on any failure.
                try
                {
                    if (!SameStoredValue(field.FieldType, snapshot.Value, field.GetValue(snapshot.Target.Owner), 0, ref valueReadsLeft))
                        throw new InvalidDataException("Canonical field rollback readback differs: " + label);
                }
                catch (Exception exception)
                {
                    failures.Add(new InvalidDataException("Canonical field rollback verification failed: " + label, exception));
                }
            }
            if (failures.Count != 0)
                throw new AggregateException("Canonical field rollback did not restore every field.", failures);
        }

        internal static bool SameRawValue(Type type, object? expected, object? actual)
        {
            var readsLeft = MaximumValueFieldReads;
            return SameStoredValue(type, expected, actual, 0, ref readsLeft);
        }
        private static bool SameStoredValue(Type type, object? expected, object? actual, int depth, ref int readsLeft)
        {
            if (ReferenceEquals(expected, actual)) return true;
            if (expected == null || actual == null) return false;
            if (!type.IsValueType) return false;
            if (expected.GetType() != actual.GetType()) return false;
            // Non-null Nullable<T> fields are boxed as T by reflection.
            type = expected.GetType();
            if (type == typeof(float))
                return BitConverter.ToInt32(BitConverter.GetBytes((float)expected), 0)
                    == BitConverter.ToInt32(BitConverter.GetBytes((float)actual), 0);
            if (type == typeof(double))
                return BitConverter.DoubleToInt64Bits((double)expected) == BitConverter.DoubleToInt64Bits((double)actual);
            if (type.IsPrimitive || type.IsEnum || type == typeof(IntPtr) || type == typeof(UIntPtr)) return expected.Equals(actual);
            if (depth == MaximumValueDepth)
                throw new InvalidDataException("Canonical field rollback value depth exceeds its bound.");
            // Compare raw struct fields instead of invoking a game-defined
            // Equals override. References nested in structs remain aliases.
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (readsLeft-- <= 0)
                    throw new InvalidDataException("Canonical field rollback value reads exceed their bound.");
                if (!SameStoredValue(field.FieldType, field.GetValue(expected), field.GetValue(actual), depth + 1, ref readsLeft))
                    return false;
            }
            return true;
        }

        private readonly struct Snapshot
        {
            internal Snapshot(Target target, object? value) { Target = target; Value = value; }
            internal Target Target { get; }
            internal object? Value { get; }
        }
    }
}
