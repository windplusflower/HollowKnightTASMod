using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalHudPresentationCarry
    {
        internal const string Root = "HudPresentation/Source";
        private static readonly string Schema = "original-hud-presentation-18-all-managed-v1:" + CanonicalHudPresentationDirectory.SchemaIdentity
            + ":" + CanonicalMarkerReadonlyCarry.SchemaIdentity;
        internal static readonly Type[] DataTypes = { typeof(Data), typeof(object[]), typeof(object[][]), typeof(string[]), typeof(bool[]), typeof(byte[]) };
        internal sealed class Data
        {
            public int TweenIdCount;
            public string[] Registered = Array.Empty<string>();
            public string Schema = "";
            public string OriginalAssetOrigins = "";
            public string[] NativeOwners = Array.Empty<string>();
            public string[] FieldOwners = Array.Empty<string>();
            public object?[][] FieldValues = Array.Empty<object?[]>();
            public string[] GameObjectOwners = Array.Empty<string>();
            public bool[] ActiveSelf = Array.Empty<bool>();
            public byte[] Lifecycle = Array.Empty<byte>();
            public CanonicalMarkerReadonlyCarry.Data Readonly = new CanonicalMarkerReadonlyCarry.Data();
        }
        internal static FieldInfo[] ManagedFields(Type type) => CanonicalHudPresentationDirectory.ManagedFields(type);
        internal static string SchemaIdentity => Schema;
        internal static Data Capture(IReadOnlyDictionary<string, UnityEngine.Object> directory)
        {
            var owners = directory.Where(pair => pair.Value is MonoBehaviour).ToArray();
            var objects = directory.Where(pair => pair.Value is GameObject).ToArray();
            if (owners.Length != 40 || objects.Length != 18) throw new InvalidDataException("HUD presentation complete nine Text directory differs.");
            byte[] lifecycle;
            using (new LoadingEngineClockProbe.CheckpointValidation())
                lifecycle = owners.SelectMany(pair => BoundaryMonoLifecycle.ObserveUnstartedUi((MonoBehaviour)pair.Value)).ToArray();
            var registrations = typeof(PlayMakerFSM).GetField("fsmList", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) as System.Collections.IList
                ?? throw new InvalidDataException("HUD original FSM registration list is absent.");
            RequireTweenCounterAbi();
            return new Data { TweenIdCount = iTweenFSMEvents.itweenIDCount,
                Registered = directory.Where(pair => pair.Value is PlayMakerFSM && registrations.Cast<object>().Any(item => ReferenceEquals(item, pair.Value))).Select(pair => pair.Key).ToArray(), Schema = SchemaIdentity, NativeOwners = owners.Select(pair => pair.Key).ToArray(),
                FieldOwners = owners.SelectMany(pair => ManagedFields(pair.Value.GetType())
                    .Select(field => pair.Key + "/" + field.DeclaringType!.FullName + "/" + field.Name)).ToArray(),
                FieldValues = owners.Select(pair => ManagedFields(pair.Value.GetType()).Select(field => field.GetValue(pair.Value)).ToArray()).ToArray(),
                GameObjectOwners = objects.Select(pair => pair.Key).ToArray(), ActiveSelf = objects.Select(pair => ((GameObject)pair.Value).activeSelf).ToArray(),
                Lifecycle = lifecycle, Readonly = CanonicalMarkerReadonlyCarry.CaptureMapKey(directory) };
        }
        internal static SortedDictionary<string, object> Roots(Data data)
        {
            if (data.Registered == null || data.Registered.Length != 0 || data.Schema != SchemaIdentity || data.OriginalAssetOrigins == null || data.OriginalAssetOrigins.Length != 64
                || data.OriginalAssetOrigins.Any(value => !Uri.IsHexDigit(value))
                || data.NativeOwners.Length != 40 || data.NativeOwners.Distinct(StringComparer.Ordinal).Count() != 40
                || data.FieldValues.Length != 40 || data.FieldValues.Any(row => row == null) || data.FieldValues.Sum(row => row.Length) != 2301
                || data.FieldOwners.Length != 2301 || data.FieldOwners.Distinct(StringComparer.Ordinal).Count() != 2301
                || data.Lifecycle.Length != 200 || data.Lifecycle.Any(value => value > 1)
                || data.GameObjectOwners.Length != 18 || data.GameObjectOwners.Distinct(StringComparer.Ordinal).Count() != 18 || data.ActiveSelf.Length != 18)
                throw new InvalidDataException("HUD presentation complete source data directory differs: registered=" + data.Registered?.Length
                    + ";schema=" + (data.Schema == SchemaIdentity) + ";assetManifestLength=" + data.OriginalAssetOrigins?.Length
                    + ";nativeOwners=" + data.NativeOwners.Length + ";fieldRows=" + data.FieldValues.Length
                    + ";fieldSlots=" + data.FieldValues.Sum(row => row?.Length ?? 0) + ";fieldOwners=" + data.FieldOwners.Length
                    + ";lifecycleBytes=" + data.Lifecycle.Length + ";objects=" + data.GameObjectOwners.Length + ";activeSelf=" + data.ActiveSelf.Length);
            return new SortedDictionary<string, object>(StringComparer.Ordinal) { [Root] = data };
        }
        internal static Action PrepareManaged(Data source, Transform keys, CanonicalCarryGraphCodec codec, Func<bool> admitted)
        {
            RequireTweenCounterAbi();
            Roots(source);
            var directory = CanonicalHudPresentationDirectory.Build(keys);
            var owners = directory.Where(pair => pair.Value is MonoBehaviour).ToArray();
            var fields = owners.Select(pair => ManagedFields(pair.Value.GetType())).ToArray();
            var before = owners.Select((owner, index) => fields[index].Select(field => field.GetValue(owner.Value)).ToArray()).ToArray();
            if (!source.NativeOwners.SequenceEqual(owners.Select(pair => pair.Key))
                || !source.FieldOwners.SequenceEqual(owners.SelectMany(pair => ManagedFields(pair.Value.GetType())
                    .Select(field => pair.Key + "/" + field.DeclaringType!.FullName + "/" + field.Name))))
                throw new InvalidDataException("HUD presentation complete prepared source field directory differs.");
            for (var owner = 0; owner < owners.Length; owner++) {
                if (source.FieldValues[owner].Length != fields[owner].Length) throw new InvalidDataException("HUD presentation source field row length differs.");
                for (var field = 0; field < fields[owner].Length; field++) {
                    var value = source.FieldValues[owner][field]; var type = fields[owner][field].FieldType;
                    if (value == null ? type.IsValueType : !type.IsInstanceOfType(value))
                        throw new InvalidDataException("HUD presentation source field type differs: " + owners[owner].Key + "/" + fields[owner][field].Name);
                }
            }
            var consumed = false;
            return () => {
                if (consumed || !admitted() || keys == null) throw new InvalidOperationException("HUD presentation source writes lost single-use admission.");
                consumed = true;
                iTweenFSMEvents.itweenIDCount = source.TweenIdCount;
                var current = CanonicalHudPresentationDirectory.Build(keys);
                if (!directory.Keys.SequenceEqual(current.Keys) || directory.Any(pair => !ReferenceEquals(pair.Value, current[pair.Key])))
                    throw new InvalidOperationException("HUD presentation actual owner directory changed before source writes.");
                var applyReadonly = CanonicalMarkerReadonlyCarry.PrepareMapKey(source.Readonly, directory, codec, admitted);
                for (var owner = 0; owner < owners.Length; owner++)
                    for (var field = 0; field < fields[owner].Length; field++) {
                        if (!codec.SameRawSlot(before[owner][field], fields[owner][field].GetValue(owners[owner].Value), fields[owner][field].FieldType, 0))
                            throw new InvalidOperationException("HUD presentation managed field changed after preparation.");
                        if (fields[owner][field].IsInitOnly && !ReferenceEquals(before[owner][field], source.FieldValues[owner][field]))
                            throw new InvalidOperationException("HUD presentation source readonly reference does not bind its actual constructor owner.");
                    }
                for (var owner = 0; owner < owners.Length; owner++)
                    for (var field = 0; field < fields[owner].Length; field++)
                        if (!fields[owner][field].IsInitOnly) fields[owner][field].SetValue(owners[owner].Value, source.FieldValues[owner][field]);
                applyReadonly();
                for (var owner = 0; owner < owners.Length; owner++)
                    for (var field = 0; field < fields[owner].Length; field++)
                        if (!codec.SameRawSlot(source.FieldValues[owner][field], fields[owner][field].GetValue(owners[owner].Value), fields[owner][field].FieldType, 0))
                            throw new InvalidOperationException("HUD presentation complete raw field readback differs: " + owners[owner].Key + "/" + fields[owner][field].Name);
                if (!admitted()) throw new InvalidOperationException("HUD presentation source readback lost paused admission.");
            };
        }
        private static void RequireTweenCounterAbi()
        {
            var type = typeof(iTweenFSMEvents);
            var initializer = type.TypeInitializer;
            var counter = type.GetField("itweenIDCount", BindingFlags.Static | BindingFlags.Public);
            if (type.Module.ModuleVersionId.ToString("D") != CanonicalSceneMappingCarry.ModuleMvid
                || initializer == null || initializer.MetadataToken != 0x060001a4
                || initializer.GetMethodBody()?.GetILAsByteArray() is not byte[] body
                || body.Length != 1 || body[0] != 0x2a
                || counter == null || counter.FieldType != typeof(int) || !counter.IsStatic || counter.IsInitOnly)
                throw new InvalidDataException("HUD original tween counter ABI differs.");
        }
    }
}
