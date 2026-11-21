using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using InControl;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalInputManagerScalars
    {
        // Original Assembly-CSharp MVID 2b54cdaf-7a93-4f0e-b297-d50c4090ea9f,
        // metadata-only extraction. No property access initializes a backend.
        private const string Layout = "<CommandWasPressed>k__BackingField:System.Boolean\n<EnableICade>k__BackingField:System.Boolean\n<EnableNativeInput>k__BackingField:System.Boolean\n<EnableXInput>k__BackingField:System.Boolean\n<InvertYAxis>k__BackingField:System.Boolean\n<IsSetup>k__BackingField:System.Boolean\n<NativeInputEnableMFi>k__BackingField:System.Boolean\n<NativeInputEnableXInput>k__BackingField:System.Boolean\n<NativeInputPreventSleep>k__BackingField:System.Boolean\n<NativeInputUpdateRate>k__BackingField:System.UInt32\n<Platform>k__BackingField:System.String\n<SuspendInBackground>k__BackingField:System.Boolean\n<XInputBufferSize>k__BackingField:System.UInt32\n<XInputUpdateRate>k__BackingField:System.UInt32\napplicationIsFocused:System.Boolean\ncurrentTick:System.UInt64\ncurrentTime:System.Single\nenabled:System.Boolean\ninitialTime:System.Single\nlastUpdateTime:System.Single";
        private static readonly string[] Mutable = { "<CommandWasPressed>k__BackingField", "applicationIsFocused", "currentTick",
            "currentTime", "initialTime", "lastUpdateTime" };
        private static FieldInfo[] Fields()
        {
            var fields = typeof(InputManager).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(field => !field.IsLiteral && (field.FieldType.IsPrimitive && field.FieldType != typeof(IntPtr) && field.FieldType != typeof(UIntPtr)
                    || field.FieldType.IsEnum || field.FieldType == typeof(string))).OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();
            var actualMvid = typeof(InputManager).Module.ModuleVersionId.ToString("D");
            if (actualMvid != CanonicalSceneMappingCarry.ModuleMvid)
                CanonicalSceneMappingCarry.WarnOnce("InputManagerScalars:Mvid:" + actualMvid,
                    "[HKTAS] InControl.InputManager ModuleVersionId differs from pinned original (continuing): actual=" + actualMvid);
            if (string.Join("\n", fields.Select(field => field.Name + ":" + field.FieldType.FullName)) != Layout)
                throw new InvalidDataException("Pinned InputManager scalar static layout differs.");
            return fields;
        }
        internal static string SchemaIdentity => "CanonicalInputManagerScalars/v1:" + Layout;
        internal static Data FromPrepared(CanonicalCarryGraphCodec.PreparedView view)
        {
            var owner = view.Root("R6/InputManager-scalars");
            var names = view.Field(owner, typeof(Data), nameof(Data.Fields)) as string[];
            var values = view.Field(owner, typeof(Data), nameof(Data.Values)) as string[];
            if (names == null || values == null) throw new InvalidDataException("Prepared InputManager scalar arrays are absent.");
            return new Data { Fields = (string[])names.Clone(), Values = (string[])values.Clone() };
        }
        internal static void RequireEpoch(Data saved, CanonicalInputEpoch epoch)
        {
            epoch.Validate(epoch.SourceNative);
            if (saved == null || saved.Fields == null || saved.Values == null || saved.Fields.Length != 20 || saved.Values.Length != 20
                || !saved.Fields.SequenceEqual(saved.Fields.Distinct().OrderBy(name => name, StringComparer.Ordinal)))
                throw new InvalidDataException("InputManager scalar/epoch binding has an invalid field set.");
            var expected = new Dictionary<string, string>(StringComparer.Ordinal) {
                ["InControl.InputManager/currentTick"] = "System.UInt64:" + epoch.CurrentTick.ToString(CultureInfo.InvariantCulture),
                ["InControl.InputManager/initialTime"] = "f32:" + unchecked((uint)epoch.InitialTimeBits).ToString("x8", CultureInfo.InvariantCulture),
                ["InControl.InputManager/currentTime"] = "f32:" + unchecked((uint)epoch.CurrentTimeBits).ToString("x8", CultureInfo.InvariantCulture),
                ["InControl.InputManager/lastUpdateTime"] = "f32:" + unchecked((uint)epoch.LastUpdateTimeBits).ToString("x8", CultureInfo.InvariantCulture) };
            foreach (var pair in expected) {
                var index = Array.IndexOf(saved.Fields, pair.Key);
                if (index < 0 || saved.Values[index] != pair.Value)
                    throw new InvalidDataException("InputManager scalar and input epoch disagree: " + pair.Key);
            }
        }
        internal static Data Capture()
        {
            var fields = Fields();
            if (!Equals(fields.Single(field => field.Name == "<IsSetup>k__BackingField").GetValue(null), true))
                throw new InvalidDataException("InputManager scalar capture preceded original setup.");
            var values = CheckpointScalarFields.Capture(null, fields).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
            return new Data { Fields = values.Select(pair => pair.Key).ToArray(), Values = values.Select(pair => pair.Value).ToArray() };
        }
        internal static Action PrepareApply(Data saved)
        {
            if (saved == null || saved.Fields == null || saved.Values == null || saved.Fields.Length != 20 || saved.Values.Length != 20)
                throw new InvalidDataException("InputManager scalar data layout differs.");
            var fields = Fields();
            var current = CheckpointScalarFields.Capture(null, fields);
            if (!saved.Fields.SequenceEqual(current.Keys.OrderBy(key => key, StringComparer.Ordinal)))
                throw new InvalidDataException("InputManager scalar field identity differs.");
            var source = saved.Fields.Select((key, index) => new { key, value = saved.Values[index] })
                .ToDictionary(pair => pair.key, pair => pair.value, StringComparer.Ordinal);
            var fixedFields = fields.Where(field => !Mutable.Contains(field.Name)).ToArray();
            foreach (var field in fixedFields) {
                var key = field.DeclaringType!.FullName + "/" + field.Name;
                if (source[key] != current[key]) throw new InvalidDataException("Input backend/configuration binding differs: " + key);
            }
            var mutableFields = fields.Where(field => Mutable.Contains(field.Name)).ToArray();
            var mutableValues = source.Where(pair => mutableFields.Any(field => pair.Key == field.DeclaringType!.FullName + "/" + field.Name))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var write = CheckpointScalarFields.PrepareApply(null, mutableFields, mutableValues);
            return () => {
                // Do not call Enabled/setup setters: those invoke original
                // updates, device discovery or native backend configuration.
                write();
                var observed = CheckpointScalarFields.Capture(null, fields);
                if (source.Any(pair => !observed.TryGetValue(pair.Key, out var value) || value != pair.Value))
                    throw new InvalidOperationException("Restored InputManager raw scalar readback differs.");
            };
        }
        internal sealed class Data
        {
            public string[] Fields = Array.Empty<string>(), Values = Array.Empty<string>();
        }
    }
}
