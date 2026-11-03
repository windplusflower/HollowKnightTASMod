using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using Newtonsoft.Json;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Execution owners stay in this process. Their explicitly declared D fields
    // enter the SAME graph as components/statics through typed slots, preserving
    // aliases back into gameplay without serializing IPC, hooks or threads.
    internal sealed class ResidentProcessSlots
    {
        internal sealed class Data
        {
            public string Key = "";
            public bool Static;
            public ResidentManagedTypeCatalog.TypeReference Type = null!;
            public Field[] Fields = Array.Empty<Field>();
            public string[] CoverageGaps = Array.Empty<string>();
        }
        internal sealed class Field
        {
            public string Name = "", Storage = "", Root = "";
            public int Token, Attributes;
            public ResidentManagedTypeCatalog.TypeReference DeclaringType = null!, ValueType = null!;
        }
        private sealed class Live
        {
            internal FieldInfo Field = null!;
            internal Field Descriptor = null!;
            internal object? Original, Slot;
            internal FieldInfo? Value;
        }
        internal readonly Data Snapshot;
        internal IReadOnlyDictionary<string, object> ManagedRoots => roots;
        private readonly object? owner;
        private readonly Live[] fields;
        private readonly Action admitted;
        private readonly Dictionary<string, object> roots = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly Dictionary<object, string> external = new Dictionary<object, string>(References.Instance);
        private readonly Dictionary<string, object> resolved = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly string frozen;
        private bool applied;

        internal ResidentProcessSlots(string key, object owner, IReadOnlyDictionary<string, string> ownership, Action admitted)
            : this(key, owner, ownership, admitted, false) { }
        // Callers must supply an existing instance whose implemented constructor
        // has already accessed these statics. This path never accepts an
        // arbitrary Type and never invokes a dormant type's constructor.
        internal ResidentProcessSlots(string key, object owner, IReadOnlyDictionary<string, string> ownership, Action admitted, bool initializedStaticFields)
            : this(key, owner, owner?.GetType() ?? throw new ArgumentNullException(nameof(owner)), ownership, admitted, initializedStaticFields) { }
        internal static ResidentProcessSlots CaptureInitializedStatic(string key, Type type, IReadOnlyDictionary<string, string> ownership,
            Action admitted, Action requireAlreadyInitialized)
        {
            if (type == null || requireAlreadyInitialized == null) throw new ArgumentNullException();
            requireAlreadyInitialized();
            var result = new ResidentProcessSlots(key, null, type, ownership, admitted, true);
            requireAlreadyInitialized(); return result;
        }
        private ResidentProcessSlots(string key, object? owner, Type ownerType, IReadOnlyDictionary<string, string> ownership, Action admitted, bool initializedStaticFields)
        {
            if (string.IsNullOrEmpty(key) || owner == null && !initializedStaticFields || ownership == null || admitted == null) throw new ArgumentNullException();
            this.owner = owner; this.admitted = admitted; admitted();
            var actual = initializedStaticFields
                ? ownerType.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).OrderBy(field => field.MetadataToken).ToArray()
                : CanonicalCarryGraphCodec.Fields(ownerType);
            var gaps = new SortedSet<string>(StringComparer.Ordinal);
            fields = actual.Select(field => {
                var storage = ownership.TryGetValue(field.Name, out var value) ? value : "Unsupported";
                if (storage != "D" && storage != "Dependency" && storage != "Execution" && storage != "Unsupported") throw Error("invalid field ownership");
                if (storage == "Unsupported") gaps.Add("process-field-unclassified:" + key + "/" + field.Name);
                var descriptor = new Field { Name = field.Name, Storage = storage, Token = field.MetadataToken,
                    Attributes = (int)field.Attributes, DeclaringType = ResidentManagedTypeCatalog.DescribeType(field.DeclaringType!),
                    ValueType = ResidentManagedTypeCatalog.DescribeType(field.FieldType),
                    Root = storage == "D" ? "resident-process/D/" + key + "/" + field.MetadataToken.ToString("x8") : "" };
                var live = new Live { Field = field, Descriptor = descriptor, Original = ResidentValueAccess.ReadOwner(owner, field) };
                if (storage == "D") {
                    var type = typeof(ResidentStaticRoots.Slot<>).MakeGenericType(field.FieldType);
                    live.Slot = FormatterServices.GetUninitializedObject(type); live.Value = type.GetField("Value")!;
                    ResidentValueAccess.WriteField(live.Slot,live.Value,live.Original); roots.Add(descriptor.Root, live.Slot);
                }
                return live;
            }).ToArray();
            foreach (var missing in ownership.Keys.Where(name => !actual.Any(field => field.Name == name))) gaps.Add("process-field-absent:" + key + "/" + missing);
            Snapshot = new Data { Key = key, Static = initializedStaticFields, Type = ResidentManagedTypeCatalog.DescribeType(ownerType),
                Fields = fields.Select(field => field.Descriptor).ToArray(), CoverageGaps = gaps.ToArray() };
            Bind(owner, "resident-process/execution/" + key);
            foreach (var field in fields.Where(field => field.Descriptor.Storage == "Execution"))
                if (!field.Field.FieldType.IsValueType) Bind(field.Original, "resident-process/execution/" + key + "/" + field.Field.Name);
            frozen = JsonConvert.SerializeObject(Snapshot); RequireUnchanged();
        }
        private void Bind(object? value, string key)
        {
            if (value == null || value.GetType().IsValueType || value is string || value is Delegate
                || value is UnityEngine.Object || ResidentGraphPolicy.IsMetadata(value)) return;
            if (external.ContainsKey(value)) return;
            external.Add(value, key); resolved.Add(key, value);
        }
        internal string? BindExternal(object value) => external.TryGetValue(value, out var key) ? key : null;
        internal object? ResolveExternal(string key) => resolved.TryGetValue(key, out var value) ? value : null;
        internal void RequireComplete()
        { RequireManifest(); if (Snapshot.CoverageGaps.Length != 0) throw Error(string.Join(";", Snapshot.CoverageGaps)); }
        internal void RequireLayout(Data source)
        {
            RequireComplete();
            // Includes field type/MVID/token and every actual field, so a packet
            // cannot remove a required gap or downgrade D into Execution.
            if (source == null || JsonConvert.SerializeObject(source) != frozen) throw Error("source process owner layout/ownership differs");
        }
        internal void RequireUnchanged()
        {
            admitted(); RequireManifest();
            foreach (var field in fields)
                if (!Same(field.Original, ResidentValueAccess.ReadOwner(owner,field.Field), field.Field.FieldType)) throw Error("process field changed before application: " + field.Field.Name);
        }
        internal void RequireControlUnchanged()
        {
            admitted(); RequireManifest();
            foreach (var field in fields.Where(field => field.Descriptor.Storage != "D"))
                if (!Same(field.Original, ResidentValueAccess.ReadOwner(owner,field.Field), field.Field.FieldType)) throw Error("process execution/dependency field changed: " + field.Field.Name);
        }
        private void RequireManifest()
        { if (frozen != JsonConvert.SerializeObject(Snapshot)) throw Error("process ownership descriptor was mutated"); }
        internal void Apply(ResidentMutationScope scope)
        {
            if (applied) throw Error("process slots already applied"); RequireUnchanged(); RequireComplete(); scope.Require();
            scope.Mutate(() => {
                foreach (var field in fields.Where(field => field.Slot != null)) {
                    admitted(); scope.Require(); ResidentValueAccess.WriteOwner(owner,field.Field,ResidentValueAccess.ReadField(field.Slot!,field.Value!));
                }
            });
            applied = true; Verify();
        }
        internal void Verify()
        {
            if (!applied) throw Error("process slots were not applied"); RequireControlUnchanged();
            foreach (var field in fields.Where(field => field.Slot != null))
                if (!Same(ResidentValueAccess.ReadField(field.Slot!,field.Value!), ResidentValueAccess.ReadOwner(owner,field.Field), field.Field.FieldType)) throw Error("process D readback differs: " + field.Field.Name);
        }
        internal static IReadOnlyDictionary<string, string> Ownership(params string[] groups)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var group in groups) {
                var split = group.IndexOf(':'); if (split <= 0) throw Error("ownership group is malformed");
                foreach (var name in group.Substring(split + 1).Split(',')) result.Add(name, group.Substring(0, split));
            }
            return result;
        }
        private static bool Same(object? left, object? right, Type type)
            => ResidentValueAccess.SameControlValue(left,right,type,true);
        private static InvalidDataException Error(string message) => new InvalidDataException("Resident process slots: " + message);
        private sealed class References : IEqualityComparer<object>
        {
            internal static readonly References Instance = new References();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
