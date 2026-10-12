using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using InControl;
using HutongGames.PlayMaker;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalBoundaryInputTablePolicy
    {
        internal const string SchemaIdentity = "SDK-Type-table-S0-v1:Mono-original-10-fields:capacity1:version0:FNV1a-UTF16-MVID-FullName:RuntimeType-reference-equality";
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        internal static object Reset()
        {
            if (FsmExecutionStack.StackCount != 0 || PlayMakerFSM.NotMainThread
                || !Equals(typeof(InputManager).GetField("<IsSetup>k__BackingField", Static)?.GetValue(null), true))
                throw new InvalidOperationException("Canonical SDK registration reset requires original setup and an idle FSM stack.");
            var slot = typeof(InputManager).GetField("deviceManagerTable", Static)
                ?? throw new InvalidDataException("Original SDK type-table slot is absent.");
            var table = slot.GetValue(null) as Dictionary<Type, InputDeviceManager>
                ?? throw new InvalidDataException("Original SDK type-table owner differs.");
            var fields = CanonicalCarryGraphCodec.Fields(table.GetType());
            if (!fields.Select(field => field.Name).SequenceEqual(new[] {
                "_syncRoot", "buckets", "comparer", "count", "entries", "freeCount", "freeList", "keys", "values", "version" }))
                throw new InvalidDataException("Original Mono SDK dictionary field layout differs.");
            var comparerField = fields.Single(field => field.Name == "comparer");
            var oldComparer = comparerField.GetValue(table);
            if (oldComparer != null && !ReferenceEquals(oldComparer, EqualityComparer<Type>.Default)
                && !ReferenceEquals(oldComparer, CanonicalStableTypeComparer.Instance))
                throw new InvalidDataException("SDK type table contains an unregistered comparer.");
            // This bounded original-owner audit also rejects physical devices,
            // foreign managers and custom discovery callbacks before mutation.
            var original = new CanonicalDeviceManagerBindings();
            var members = table.ToArray();
            var canonical = new Dictionary<Type, InputDeviceManager>(1, CanonicalStableTypeComparer.Instance);
            foreach (var item in members) canonical.Add(item.Key, item.Value);
            var version = fields.Single(field => field.Name == "version");
            if (version.FieldType != typeof(int)) throw new InvalidDataException("Original Mono SDK dictionary version type differs.");
            version.SetValue(canonical, 0);
            // Keep the actual dictionary and any cached Keys/Values/syncRoot
            // owners. Their views still refer to this same dictionary.
            var writes = fields.Where(field => field.Name != "_syncRoot" && field.Name != "keys" && field.Name != "values").ToArray();
            var retained = fields.Except(writes).Select(field => new { field, value = field.GetValue(table) }).ToArray();
            original.Guard();
            using var transaction = CanonicalFieldWriteTransaction.Begin(writes.Select(field => new CanonicalFieldWriteTransaction.Target(table, field)));
            foreach (var field in writes) field.SetValue(table, field.GetValue(canonical));
            foreach (var field in writes)
                if ((!field.FieldType.IsValueType && !ReferenceEquals(field.GetValue(table), field.GetValue(canonical)))
                    || (field.FieldType.IsValueType && !Equals(field.GetValue(table), field.GetValue(canonical))))
                    throw new InvalidDataException("Canonical SDK registration field readback differs: " + field.Name);
            if (!ReferenceEquals(slot.GetValue(null), table) || table.Count != members.Length
                || members.Any(item => !table.TryGetValue(item.Key, out var value) || !ReferenceEquals(value, item.Value)))
                throw new InvalidDataException("Canonical SDK registration changed an original lookup alias.");
            if (retained.Any(item => !ReferenceEquals(item.field.GetValue(table), item.value)))
                throw new InvalidDataException("Canonical SDK registration changed a cached view or sync owner.");
            original.Guard(); transaction.Commit();
            return new { managers = members.Length, originalContainerRetained = true, stableTypeHash = true,
                bucketCapacity = ((int[])fields.Single(field => field.Name == "buckets").GetValue(table)).Length,
                entryCapacity = ((Array)fields.Single(field => field.Name == "entries").GetValue(table)).Length,
                keyHash = members.Length == 0 ? 0 : CanonicalStableTypeComparer.Instance.GetHashCode(members[0].Key) & int.MaxValue,
                version = (int)version.GetValue(table), cachedViewOwnersRetained = true, nativeBackendQualified = false };
        }
    }
}
