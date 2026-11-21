using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // This data is the actual source, not state inferred from hasMarker. The
    // producer must independently establish all native flags before any writes.
    internal static class CanonicalMarkerCarry
    {
        internal const string Root = "Marker/Source";
        private static readonly Guid Module = new Guid("2b54cdaf-7a93-4f0e-b297-d50c4090ea9f");
        private static readonly Guid PlayMakerModule = new Guid("29eb59fa-5c99-43cd-8f33-6db074533d31");
        private static readonly Type[] OwnerTypes = { typeof(PlayMakerFSM), typeof(DeactivateIfPlayerdataFalse), typeof(MenuButtonIcon),
            typeof(TMPro.TextContainer), typeof(TMPro.TextMeshPro), typeof(ChangeFontByLanguage) };
        private static readonly int[] FieldCounts = { 4, 3, 20, 12, 223, 14 };
        private static readonly FieldInfo RawFsm = typeof(PlayMakerFSM).GetField("fsm", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo Registrations = typeof(PlayMakerFSM).GetField("fsmList", BindingFlags.Static | BindingFlags.NonPublic)!;
        internal static readonly Type[] DataTypes = { typeof(Data), typeof(object[]), typeof(object[][]), typeof(string[]), typeof(bool[]), typeof(Fsm[]) };
        internal static Type[] OriginalDataTypes()
        {
            var scale = typeof(ChangeFontByLanguage).GetNestedType("FontScaleLang", BindingFlags.NonPublic)
                ?? throw new InvalidDataException("Marker original font scale data is absent.");
            var fields = CanonicalCarryGraphCodec.Fields(scale);
            if (scale.Module.ModuleVersionId != Module)
                CanonicalSceneMappingCarry.WarnOnce("Marker:ScaleMvid:" + scale.Module.ModuleVersionId,
                    "[HKTAS] FontScaleLang ModuleVersionId differs from pinned original (continuing): actual=" + scale.Module.ModuleVersionId.ToString("D"));
            if (fields.Length != 4
                || !fields.Select(field => field.Name).SequenceEqual(new[] { "fontSizeJA", "fontSizeKO", "fontSizeRU", "fontSizeZH" })
                || fields.Any(field => field.FieldType != typeof(float?) || field.IsInitOnly))
                throw new InvalidDataException("Marker original complete font scale ABI differs.");
            return new[] { scale, typeof(Platform.MenuActions), typeof(ChangeFontByLanguage.FontScaleLangTypes) };
        }
        internal sealed class Data
        {
            public string Schema = "";
            public string[] NativeOwners = Array.Empty<string>();
            public string[] FieldOwners = Array.Empty<string>();
            public object?[][] FieldValues = Array.Empty<object?[]>();
            public string[] GameObjectOwners = Array.Empty<string>();
            public bool[] ActiveSelf = Array.Empty<bool>();
            public byte[] Lifecycle = Array.Empty<byte>();
            public string[] Registered = Array.Empty<string>();
            public string[] FsmOwners = Array.Empty<string>();
            public Fsm[] Fsms = Array.Empty<Fsm>();
            public CanonicalMarkerReadonlyCarry.Data Readonly = new CanonicalMarkerReadonlyCarry.Data();
        }
        // UnityEngine.Object's native pointer is not transferable managed data.
        // Every field above MonoBehaviour, including inherited icon/TMP fields,
        // is retained. Native flags are observed separately and never written.
        internal static FieldInfo[] ManagedFields(Type type)
        {
            var index = Array.IndexOf(OwnerTypes, type);
            if (index < 0)
                throw new InvalidDataException("Original Marker managed owner ABI differs: " + type.FullName);
            var expectedMvid = type == typeof(PlayMakerFSM) ? PlayMakerModule : Module;
            if (type.Module.ModuleVersionId != expectedMvid)
                CanonicalSceneMappingCarry.WarnOnce("Marker:OwnerMvid:" + type.FullName + ":" + type.Module.ModuleVersionId,
                    "[HKTAS] Marker owner ModuleVersionId differs from pinned original (continuing): type=" + type.FullName + ";actual=" + type.Module.ModuleVersionId.ToString("D"));
            var fields = CanonicalCarryGraphCodec.Fields(type).Where(field => field.DeclaringType != typeof(UnityEngine.Object)
                && field.DeclaringType != typeof(Component) && field.DeclaringType != typeof(Behaviour)
                && field.DeclaringType != typeof(MonoBehaviour)).ToArray();
            if (fields.Length != FieldCounts[index] || fields.Any(field => field.FieldType == typeof(IntPtr)
                || field.FieldType == typeof(UIntPtr) || field.FieldType.IsPointer || field.FieldType.IsByRef))
                throw new InvalidDataException("Original Marker complete managed field directory differs: " + type.FullName);
            return fields;
        }
        internal static string SchemaIdentity => "original-marker-all-managed-v2-common-cold-Canon:" + Module + ":" + PlayMakerModule + ":" + CanonicalMarkerReadonlyCarry.SchemaIdentity + ":" + string.Join(";", OwnerTypes.SelectMany(type =>
            ManagedFields(type).Select(field => field.DeclaringType!.FullName + "/" + field.Name + "=" + field.FieldType.FullName)));
        internal static Data Capture(GameObject root)
        {
            var directory = CanonicalMarkerDirectory.Build(root);
            var native = directory.Where(pair => pair.Value is MonoBehaviour).ToArray();
            var objects = directory.Where(pair => pair.Value is GameObject).ToArray();
            var fsms = directory.Where(pair => pair.Value is PlayMakerFSM).ToArray();
            var registrations = Registrations.GetValue(null) as List<PlayMakerFSM>
                ?? throw new InvalidDataException("Original Marker FSM registration directory is absent.");
            byte[] lifecycle;
            using (new LoadingEngineClockProbe.CheckpointValidation())
                lifecycle = native.SelectMany(pair => BoundaryMonoLifecycle.ObserveUnstartedUi((MonoBehaviour)pair.Value)).ToArray();
            return new Data { Schema = SchemaIdentity, NativeOwners = native.Select(pair => pair.Key).ToArray(),
                FieldOwners = native.SelectMany(pair => ManagedFields(pair.Value.GetType()).Select(field => pair.Key + "/" + field.DeclaringType!.FullName + "/" + field.Name)).ToArray(),
                FieldValues = native.Select(pair => ManagedFields(pair.Value.GetType()).Select(field => field.GetValue(pair.Value)).ToArray()).ToArray(),
                GameObjectOwners = objects.Select(pair => pair.Key).ToArray(), ActiveSelf = objects.Select(pair => ((GameObject)pair.Value).activeSelf).ToArray(),
                Lifecycle = lifecycle, Registered = fsms.Where(pair => registrations.Any(owner => ReferenceEquals(owner, pair.Value))).Select(pair => pair.Key).ToArray(),
                FsmOwners = fsms.Select(pair => pair.Key).ToArray(), Fsms = fsms.Select(pair => (Fsm)RawFsm.GetValue(pair.Value)).ToArray(),
                Readonly = CanonicalMarkerReadonlyCarry.Capture(directory) };
        }
        internal static SortedDictionary<string, object> Roots(Data data)
        {
            if (data.Schema != SchemaIdentity || data.NativeOwners.Length != 8 || data.NativeOwners.Distinct(StringComparer.Ordinal).Count() != 8
                || data.FieldValues.Length != 8 || data.Lifecycle.Length != 40 || data.Lifecycle.Any(value => value > 1)
                || data.GameObjectOwners.Length != 5 || data.GameObjectOwners.Distinct(StringComparer.Ordinal).Count() != 5
                || data.ActiveSelf.Length != 5 || data.FsmOwners.Length != 1 || data.Fsms.Length != 1 || data.Fsms[0] == null
                || data.Fsms[0].GetType() != typeof(Fsm) || data.Registered.Any(value => value != data.FsmOwners[0]) || data.Registered.Length > 1
                || data.FieldValues.Any(value => value == null) || data.FieldValues.Sum(value => value.Length) != data.FieldOwners.Length)
                throw new InvalidDataException("Original Marker complete source data directory differs.");
            return new SortedDictionary<string, object>(StringComparer.Ordinal) { [Root] = data, ["Marker/Fsm/" + data.FsmOwners[0]] = data.Fsms[0] };
        }
        internal static object Observe(GameObject root)
        {
            var data = Capture(root);
            var parents = new List<object>();
            for (var parent = root.transform.parent; parent != null; parent = parent.parent) {
                if (parents.Count >= 32) throw new InvalidDataException("Marker original parent chain exceeds its finite bound.");
                parents.Add(new { name = parent.name, self = parent.gameObject.activeSelf, hierarchy = parent.gameObject.activeInHierarchy });
            }
            return new { diagnosticOnly = true, data.NativeOwners, data.Lifecycle, data.GameObjectOwners, data.ActiveSelf,
                data.Registered, readonlyAliases = data.Readonly.Aliases, parents };
        }
        internal static Action PrepareManaged(Data source, GameObject target, CanonicalCarryGraphCodec codec, Func<bool> admitted)
        {
            var directory = CanonicalMarkerDirectory.Build(target);
            var owners = directory.Where(pair => pair.Value is MonoBehaviour).ToArray();
            var fields = owners.Select(pair => ManagedFields(pair.Value.GetType())).ToArray();
            if (source.Schema != SchemaIdentity || !source.NativeOwners.SequenceEqual(owners.Select(pair => pair.Key))
                || source.FieldValues.Length != owners.Length
                || !source.FieldOwners.SequenceEqual(owners.SelectMany(pair => ManagedFields(pair.Value.GetType())
                    .Select(field => pair.Key + "/" + field.DeclaringType!.FullName + "/" + field.Name))))
                throw new InvalidDataException("Marker complete source field directory differs.");
            for (var owner = 0; owner < owners.Length; owner++) {
                if (source.FieldValues[owner].Length != fields[owner].Length)
                    throw new InvalidDataException("Marker source field row length differs.");
                for (var field = 0; field < fields[owner].Length; field++) {
                    var value = source.FieldValues[owner][field]; var type = fields[owner][field].FieldType;
                    if (value == null ? type.IsValueType : !type.IsInstanceOfType(value))
                        throw new InvalidDataException("Marker source field type differs: " + source.FieldOwners[field]);
                }
            }
            var consumed = false;
            return () => {
                if (consumed || !admitted() || target == null)
                    throw new InvalidOperationException("Marker source managed write lost its single-use original owner admission.");
                consumed = true;
                var current = CanonicalMarkerDirectory.Build(target);
                if (!directory.Keys.SequenceEqual(current.Keys) || directory.Any(pair => !ReferenceEquals(pair.Value, current[pair.Key])))
                    throw new InvalidOperationException("Marker actual owner directory changed before source writes.");
                var applyReadonly = CanonicalMarkerReadonlyCarry.Prepare(source.Readonly, directory, codec, admitted);
                for (var owner = 0; owner < owners.Length; owner++)
                    for (var field = 0; field < fields[owner].Length; field++)
                        if (fields[owner][field].IsInitOnly) {
                            if (!ReferenceEquals(fields[owner][field].GetValue(owners[owner].Value), source.FieldValues[owner][field]))
                                throw new InvalidOperationException("Marker readonly source reference does not bind the actual constructor owner.");
                        }
                for (var owner = 0; owner < owners.Length; owner++)
                    for (var field = 0; field < fields[owner].Length; field++)
                        if (!fields[owner][field].IsInitOnly) fields[owner][field].SetValue(owners[owner].Value, source.FieldValues[owner][field]);
                applyReadonly();
                for (var owner = 0; owner < owners.Length; owner++)
                    for (var field = 0; field < fields[owner].Length; field++)
                        if (!codec.SameRawSlot(source.FieldValues[owner][field], fields[owner][field].GetValue(owners[owner].Value), fields[owner][field].FieldType, 0))
                            throw new InvalidOperationException("Marker complete raw field readback differs: " + owners[owner].Key + "/" + fields[owner][field].Name);
                if (!admitted()) throw new InvalidOperationException("Marker source managed readback lost paused admission.");
            };
        }
    }
}
