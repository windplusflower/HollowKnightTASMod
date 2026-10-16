using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // The post-main domain carries source values, including pending roller
    // amounts/timers and complete FSM graphs. A producer is never their source.
    // Native active-source continuation remains a separate explicit admission.
    internal static class CanonicalGeoCarry
    {
        internal const string Root = "Geo/Source";
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo RawFsm = typeof(PlayMakerFSM).GetField("fsm", Flags)!;
        private static readonly string[] CounterNames = { "addCounter", "addRollerStartTimer", "addRollerState", "addTextFsm", "addTextMesh",
            "changePerTick", "counterCurrent", "digitChangeTimer", "geoChange", "geoSprite", "geoSpriteFsm", "geoTextMesh", "playerData",
            "subTextFsm", "subTextMesh", "takeCounter", "takeRollerStartTimer", "takeRollerState", "toZero" };
        private static readonly FieldInfo[] CounterFields = typeof(GeoCounter).GetFields(Flags | BindingFlags.DeclaredOnly)
            .OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();
        internal static readonly Type[] DataTypes = { typeof(Data), typeof(object[]), typeof(string[]), typeof(bool[]), typeof(Fsm[]) };
        internal static string SchemaIdentity => "original-geo-post-main-v2:" + typeof(GeoCounter).Module.ModuleVersionId
            + ":" + string.Join(";", CounterFields.Select(field => field.Name + "=" + field.FieldType.FullName));
        internal sealed class Data
        {
            public string CounterSchema = "";
            public object?[] CounterValues = Array.Empty<object?>();
            public string[] FsmOwners = Array.Empty<string>();
            public Fsm[] Fsms = Array.Empty<Fsm>();
            public string[] NativeOwners = Array.Empty<string>();
            public string[] FsmRegistrations = Array.Empty<string>();
            public byte[] NativeLifecycle = Array.Empty<byte>();
            public string[] GameObjectOwners = Array.Empty<string>();
            public bool[] ActiveSelf = Array.Empty<bool>();
            public bool CurrentPlayerFirstGeo;
            public int CurrentPlayerGeo;
        }
        internal static Data Capture(GeoCounter owner, IReadOnlyDictionary<string, UnityEngine.Object> directory)
        {
            RequireSchema();
            var fsms = directory.Where(pair => pair.Value is PlayMakerFSM).ToArray();
            var native = directory.Where(pair => pair.Value is MonoBehaviour).ToArray();
            var gameObjects = directory.Where(pair => pair.Value is GameObject).ToArray();
            var registrations = typeof(PlayMakerFSM).GetField("fsmList", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as List<PlayMakerFSM>
                ?? throw new InvalidDataException("Original Geo lifecycle registration list is absent.");
            if (fsms.Length != 6 || gameObjects.Length != 5)
                throw new InvalidDataException("Source Geo is outside the registered original five-owner/six-FSM directory.");
            return new Data { CounterSchema = SchemaIdentity, CounterValues = CounterFields.Select(field => field.GetValue(owner)).ToArray(),
                FsmOwners = fsms.Select(pair => pair.Key).ToArray(), Fsms = fsms.Select(pair => (Fsm)RawFsm.GetValue(pair.Value)).ToArray(),
                NativeOwners = native.Select(pair => pair.Key).ToArray(),
                FsmRegistrations = fsms.Where(pair => registrations.Any(item => ReferenceEquals(item, pair.Value))).Select(pair => pair.Key).ToArray(),
                NativeLifecycle = CaptureLifecycle(native),
                GameObjectOwners = gameObjects.Select(pair => pair.Key).ToArray(), ActiveSelf = gameObjects.Select(pair => ((GameObject)pair.Value).activeSelf).ToArray(),
                CurrentPlayerFirstGeo = PlayerData.instance.firstGeo, CurrentPlayerGeo = PlayerData.instance.geo };
        }
        private static byte[] CaptureLifecycle(KeyValuePair<string, UnityEngine.Object>[] native)
        {
            using var validation = new LoadingEngineClockProbe.CheckpointValidation();
            return native.SelectMany(pair => BoundaryMonoLifecycle.ObserveUnstartedUi((MonoBehaviour)pair.Value)).ToArray();
        }
        internal static SortedDictionary<string, object> Roots(Data data)
        {
            if (data.Fsms.Length != 6 || data.FsmOwners.Length != 6 || data.FsmOwners.Distinct().Count() != 6)
                throw new InvalidDataException("Source Geo FSM root directory differs.");
            var result = new SortedDictionary<string, object>(StringComparer.Ordinal) { [Root] = data };
            for (var index = 0; index < data.Fsms.Length; index++) result.Add("Geo/Fsm/" + data.FsmOwners[index], data.Fsms[index]);
            return result;
        }
        internal static Action PrepareCounter(Data source, GeoCounter target, Func<bool> admitted)
        {
            RequireSchema();
            if (source.CounterSchema != SchemaIdentity || source.CounterValues.Length != CounterFields.Length)
                throw new InvalidDataException("Source Geo counter field ABI differs.");
            for (var index = 0; index < CounterFields.Length; index++) {
                var value = source.CounterValues[index]; var type = CounterFields[index].FieldType;
                if (value == null ? type.IsValueType : !type.IsInstanceOfType(value))
                    throw new InvalidDataException("Source Geo counter field type differs: " + CounterFields[index].Name);
            }
            var consumed = false;
            return () => {
                if (consumed || target == null || !admitted()) throw new InvalidOperationException("Source Geo counter lost its single-use actual owner admission.");
                consumed = true;
                if (PlayerData.instance.firstGeo != source.CurrentPlayerFirstGeo || PlayerData.instance.geo != source.CurrentPlayerGeo)
                    throw new InvalidOperationException("Source Geo current PlayerData changed before its post-main application.");
                for (var index = 0; index < CounterFields.Length; index++) CounterFields[index].SetValue(target, source.CounterValues[index]);
                for (var index = 0; index < CounterFields.Length; index++) {
                    var value = CounterFields[index].GetValue(target); var expected = source.CounterValues[index];
                    if (CounterFields[index].FieldType == typeof(float)
                        ? BitConverter.ToInt32(BitConverter.GetBytes((float)value!), 0) != BitConverter.ToInt32(BitConverter.GetBytes((float)expected!), 0)
                        : CounterFields[index].FieldType.IsValueType ? !Equals(value, expected) : !ReferenceEquals(value, expected))
                        throw new InvalidOperationException("Source Geo counter complete field readback differs: " + CounterFields[index].Name);
                }
                if (!admitted()) throw new InvalidOperationException("Source Geo counter lost post-main admission.");
            };
        }
        private static void RequireSchema()
        {
            if (!CounterFields.Select(field => field.Name).SequenceEqual(CounterNames))
                throw new InvalidDataException("Pinned original GeoCounter field directory differs.");
        }
    }
}
