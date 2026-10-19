using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalMapKeyTextCarry
    {
        internal const string Root = "MapKeyText/Source";
        private static readonly Guid Module = new Guid("2b54cdaf-7a93-4f0e-b297-d50c4090ea9f");
        private static readonly Type[] Types = { typeof(SetTextMeshProGameText), typeof(TMPro.TextContainer), typeof(TMPro.TextMeshPro), typeof(ChangeFontByLanguage) };
        private static readonly int[] Counts = { 3, 12, 223, 14 };
        internal static readonly Type[] DataTypes = { typeof(Data), typeof(object[]), typeof(object[][]), typeof(string[]), typeof(bool[]), typeof(byte[]) };
        internal sealed class Data
        {
            public string Schema = "";
            public string[] NativeOwners = Array.Empty<string>();
            public string[] FieldOwners = Array.Empty<string>();
            public object?[][] FieldValues = Array.Empty<object?[]>();
            public string[] GameObjectOwners = Array.Empty<string>();
            public bool[] ActiveSelf = Array.Empty<bool>();
            public byte[] Lifecycle = Array.Empty<byte>();
            public CanonicalMarkerReadonlyCarry.Data Readonly = new CanonicalMarkerReadonlyCarry.Data();
        }
        internal static FieldInfo[] ManagedFields(Type type)
        {
            var index = Array.IndexOf(Types, type);
            if (index < 0 || type.Module.ModuleVersionId != Module)
                throw new InvalidDataException("Original Map Key Text managed owner ABI differs: " + type.FullName);
            var fields = CanonicalCarryGraphCodec.Fields(type).Where(field => field.DeclaringType != typeof(UnityEngine.Object)
                && field.DeclaringType != typeof(Component) && field.DeclaringType != typeof(Behaviour)
                && field.DeclaringType != typeof(MonoBehaviour)).ToArray();
            if (fields.Length != Counts[index] || fields.Any(field => field.FieldType == typeof(IntPtr)
                || field.FieldType == typeof(UIntPtr) || field.FieldType.IsPointer || field.FieldType.IsByRef))
                throw new InvalidDataException("Original Map Key Text complete field directory differs: " + type.FullName);
            return fields;
        }
        internal static string SchemaIdentity => "original-map-key-nine-text-all-managed-v1-common-cold:" + Module + ":"
            + CanonicalMarkerReadonlyCarry.SchemaIdentity + ":" + string.Join(";", Types.SelectMany(type => ManagedFields(type)
                .Select(field => field.DeclaringType!.FullName + "/" + field.Name + "=" + field.FieldType.FullName)));
        internal static Data Capture(IReadOnlyDictionary<string, UnityEngine.Object> directory)
        {
            var owners = directory.Where(pair => pair.Value is MonoBehaviour).ToArray();
            var objects = directory.Where(pair => pair.Value is GameObject).ToArray();
            if (owners.Length != 36 || objects.Length != 9) throw new InvalidDataException("Map Key complete nine Text directory differs.");
            byte[] lifecycle;
            using (new LoadingEngineClockProbe.CheckpointValidation())
                lifecycle = owners.SelectMany(pair => BoundaryMonoLifecycle.ObserveUnstartedUi((MonoBehaviour)pair.Value)).ToArray();
            return new Data { Schema = SchemaIdentity, NativeOwners = owners.Select(pair => pair.Key).ToArray(),
                FieldOwners = owners.SelectMany(pair => ManagedFields(pair.Value.GetType())
                    .Select(field => pair.Key + "/" + field.DeclaringType!.FullName + "/" + field.Name)).ToArray(),
                FieldValues = owners.Select(pair => ManagedFields(pair.Value.GetType()).Select(field => field.GetValue(pair.Value)).ToArray()).ToArray(),
                GameObjectOwners = objects.Select(pair => pair.Key).ToArray(), ActiveSelf = objects.Select(pair => ((GameObject)pair.Value).activeSelf).ToArray(),
                Lifecycle = lifecycle, Readonly = CanonicalMarkerReadonlyCarry.CaptureMapKey(directory) };
        }
        internal static SortedDictionary<string, object> Roots(Data data)
        {
            if (data.Schema != SchemaIdentity || data.NativeOwners.Length != 36 || data.NativeOwners.Distinct(StringComparer.Ordinal).Count() != 36
                || data.FieldValues.Length != 36 || data.FieldValues.Any(row => row == null) || data.FieldValues.Sum(row => row.Length) != 2268
                || data.FieldOwners.Length != 2268 || data.FieldOwners.Distinct(StringComparer.Ordinal).Count() != 2268
                || data.Lifecycle.Length != 180 || data.Lifecycle.Any(value => value > 1)
                || data.GameObjectOwners.Length != 9 || data.GameObjectOwners.Distinct(StringComparer.Ordinal).Count() != 9 || data.ActiveSelf.Length != 9)
                throw new InvalidDataException("Map Key Text complete source data directory differs.");
            return new SortedDictionary<string, object>(StringComparer.Ordinal) { [Root] = data };
        }
        internal static Action PrepareManaged(Data source, Transform keys, CanonicalCarryGraphCodec codec, Func<bool> admitted)
        {
            Roots(source);
            var directory = CanonicalMapKeyTextDirectory.Build(keys);
            var owners = directory.Where(pair => pair.Value is MonoBehaviour).ToArray();
            var fields = owners.Select(pair => ManagedFields(pair.Value.GetType())).ToArray();
            var before = owners.Select((owner, index) => fields[index].Select(field => field.GetValue(owner.Value)).ToArray()).ToArray();
            if (!source.NativeOwners.SequenceEqual(owners.Select(pair => pair.Key))
                || !source.FieldOwners.SequenceEqual(owners.SelectMany(pair => ManagedFields(pair.Value.GetType())
                    .Select(field => pair.Key + "/" + field.DeclaringType!.FullName + "/" + field.Name))))
                throw new InvalidDataException("Map Key Text complete prepared source field directory differs.");
            for (var owner = 0; owner < owners.Length; owner++) {
                if (source.FieldValues[owner].Length != fields[owner].Length) throw new InvalidDataException("Map Key source field row length differs.");
                for (var field = 0; field < fields[owner].Length; field++) {
                    var value = source.FieldValues[owner][field]; var type = fields[owner][field].FieldType;
                    if (value == null ? type.IsValueType : !type.IsInstanceOfType(value))
                        throw new InvalidDataException("Map Key source field type differs: " + owners[owner].Key + "/" + fields[owner][field].Name);
                }
            }
            var consumed = false;
            return () => {
                if (consumed || !admitted() || keys == null) throw new InvalidOperationException("Map Key source writes lost single-use admission.");
                consumed = true;
                var current = CanonicalMapKeyTextDirectory.Build(keys);
                if (!directory.Keys.SequenceEqual(current.Keys) || directory.Any(pair => !ReferenceEquals(pair.Value, current[pair.Key])))
                    throw new InvalidOperationException("Map Key actual owner directory changed before source writes.");
                var applyReadonly = CanonicalMarkerReadonlyCarry.PrepareMapKey(source.Readonly, directory, codec, admitted);
                for (var owner = 0; owner < owners.Length; owner++)
                    for (var field = 0; field < fields[owner].Length; field++) {
                        if (!codec.SameRawSlot(before[owner][field], fields[owner][field].GetValue(owners[owner].Value), fields[owner][field].FieldType, 0))
                            throw new InvalidOperationException("Map Key managed field changed after preparation.");
                        if (fields[owner][field].IsInitOnly && !ReferenceEquals(before[owner][field], source.FieldValues[owner][field]))
                            throw new InvalidOperationException("Map Key source readonly reference does not bind its actual constructor owner.");
                    }
                for (var owner = 0; owner < owners.Length; owner++)
                    for (var field = 0; field < fields[owner].Length; field++)
                        if (!fields[owner][field].IsInitOnly) fields[owner][field].SetValue(owners[owner].Value, source.FieldValues[owner][field]);
                applyReadonly();
                for (var owner = 0; owner < owners.Length; owner++)
                    for (var field = 0; field < fields[owner].Length; field++)
                        if (!codec.SameRawSlot(source.FieldValues[owner][field], fields[owner][field].GetValue(owners[owner].Value), fields[owner][field].FieldType, 0))
                            throw new InvalidOperationException("Map Key complete raw field readback differs: " + owners[owner].Key + "/" + fields[owner][field].Name);
                if (!admitted()) throw new InvalidOperationException("Map Key source readback lost paused admission.");
            };
        }
    }
}
