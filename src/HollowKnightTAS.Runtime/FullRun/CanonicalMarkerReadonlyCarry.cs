using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // The readonly references are real constructor-owned objects. Preserve
    // their identities; only the four-corner runtime cache has source writes.
    internal static class CanonicalMarkerReadonlyCarry
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo Power = typeof(TMPro.TMP_Text).GetField("k_Power", Flags)!;
        private static readonly FieldInfo Corners = typeof(UnityEngine.UI.MaskableGraphic).GetField("m_Corners", Flags)!;
        private static readonly FieldInfo Runner = typeof(UnityEngine.UI.Graphic).GetField("m_ColorTweenRunner", Flags)!;
        private static readonly float[] OriginalPowers = { .5f, .05f, .005f, .0005f, .00005f, .000005f, .0000005f, .00000005f, .000000005f, .0000000005f };
        internal static readonly Type[] DataTypes = { typeof(Data), typeof(float[][]), typeof(Vector3[][]), typeof(Vector3[]) };
        internal static string SchemaIdentity => "marker-readonly-v1:b4d135af-a1fd-407b-bcc7-280e400336cf:"
            + string.Join(";", CanonicalCarryGraphCodec.Fields(typeof(Data)).Select(field => field.Name + "=" + field.FieldType.FullName));
        internal sealed class Data
        {
            public string[] Owners = Array.Empty<string>();
            public float[][] Powers = Array.Empty<float[]>();
            public Vector3[][] Corners = Array.Empty<Vector3[]>();
            public bool[] ContainerIsSelf = Array.Empty<bool>();
            public int[] Aliases = Array.Empty<int>();
        }
        internal static IReadOnlyDictionary<string, object> Directory(IReadOnlyDictionary<string, UnityEngine.Object> directory)
            => DirectoryCore(directory, 2);
        internal static IReadOnlyDictionary<string, object> MapKeyDirectory(IReadOnlyDictionary<string, UnityEngine.Object> directory)
            => DirectoryCore(directory, 9);
        internal static IReadOnlyDictionary<string, object> FinalVisualDirectory(IReadOnlyDictionary<string, UnityEngine.Object> directory)
            => DirectoryCore(directory, 3);
        private static IReadOnlyDictionary<string, object> DirectoryCore(IReadOnlyDictionary<string, UnityEngine.Object> directory, int texts)
        {
            if (typeof(UnityEngine.UI.Graphic).Module.ModuleVersionId != new Guid("b4d135af-a1fd-407b-bcc7-280e400336cf"))
                throw new InvalidDataException("Marker readonly UI module ABI differs.");
            var result = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var pair in directory.Where(pair => pair.Value is TMPro.TextMeshPro)) {
                foreach (var field in new[] { Power, Corners, Runner }) {
                    if (!field.IsInitOnly) throw new InvalidDataException("Marker original readonly field ABI differs.");
                    var value = field.GetValue(pair.Value) ?? throw new InvalidDataException("Marker constructor-owned readonly reference is absent.");
                    result.Add(pair.Key + "/readonly/" + field.Name, value);
                }
            }
            if (result.Count != texts * 3) throw new InvalidDataException("Fixed UI readonly reference count differs.");
            return result;
        }
        private static FieldInfo[] RunnerFields(object runner)
        {
            var type = runner.GetType();
            var expected = typeof(UnityEngine.UI.Graphic).Assembly.GetType("UnityEngine.UI.CoroutineTween.TweenRunner`1", true)!
                .MakeGenericType(typeof(UnityEngine.UI.Graphic).Assembly.GetType("UnityEngine.UI.CoroutineTween.ColorTween", true)!);
            var fields = CanonicalCarryGraphCodec.Fields(type);
            if (type != expected || fields.Length != 2 || fields[0].Name != "m_CoroutineContainer" || fields[1].Name != "m_Tween"
                || fields.Any(field => field.IsInitOnly)) throw new InvalidDataException("Marker UI tween runner field ABI differs.");
            return fields;
        }
        internal static Data Capture(IReadOnlyDictionary<string, UnityEngine.Object> directory)
            => CaptureCore(directory, 2);
        internal static Data CaptureMapKey(IReadOnlyDictionary<string, UnityEngine.Object> directory)
            => CaptureCore(directory, 9);
        internal static Data CaptureFinalVisual(IReadOnlyDictionary<string, UnityEngine.Object> directory)
            => CaptureCore(directory, 3);
        private static Data CaptureCore(IReadOnlyDictionary<string, UnityEngine.Object> directory, int texts)
        {
            var owners = directory.Where(pair => pair.Value is TMPro.TextMeshPro).ToArray();
            var references = DirectoryCore(directory, texts).Values.ToArray();
            var aliases = new int[references.Length];
            for (var i = 0; i < references.Length; i++) aliases[i] = Array.FindIndex(references, value => ReferenceEquals(value, references[i]));
            var powers = new float[texts][]; var corners = new Vector3[texts][]; var containers = new bool[texts];
            for (var i = 0; i < owners.Length; i++) {
                var power = Power.GetValue(owners[i].Value) as float[];
                var corner = Corners.GetValue(owners[i].Value) as Vector3[];
                if (power == null || power.Length != 10 || corner == null || corner.Length != 4)
                    throw new InvalidDataException("Marker readonly native producer array lengths differ.");
                for (var index = 0; index < 10; index++)
                    if (BitConverter.ToInt32(BitConverter.GetBytes(power[index]), 0) != BitConverter.ToInt32(BitConverter.GetBytes(OriginalPowers[index]), 0))
                        throw new InvalidDataException("Marker readonly original power configuration differs.");
                powers[i] = (float[])power.Clone(); corners[i] = (Vector3[])corner.Clone();
                var runner = Runner.GetValue(owners[i].Value)!; var fields = RunnerFields(runner);
                containers[i] = ReferenceEquals(fields[0].GetValue(runner), owners[i].Value);
                if (fields[0].GetValue(runner) != null && !containers[i] || fields[1].GetValue(runner) != null)
                    throw new InvalidDataException("Marker active/foreign original UI tween continuation is unsupported.");
            }
            return new Data { Owners = owners.Select(pair => pair.Key).ToArray(), Powers = powers, Corners = corners,
                ContainerIsSelf = containers, Aliases = aliases };
        }
        internal static Action Prepare(Data source, IReadOnlyDictionary<string, UnityEngine.Object> directory,
            CanonicalCarryGraphCodec codec, Func<bool> admitted)
            => PrepareCore(source, directory, codec, admitted, 2);
        internal static Action PrepareMapKey(Data source, IReadOnlyDictionary<string, UnityEngine.Object> directory,
            CanonicalCarryGraphCodec codec, Func<bool> admitted)
            => PrepareCore(source, directory, codec, admitted, 9);
        internal static Action PrepareFinalVisual(Data source, IReadOnlyDictionary<string, UnityEngine.Object> directory,
            CanonicalCarryGraphCodec codec, Func<bool> admitted)
            => PrepareCore(source, directory, codec, admitted, 3);
        private static Action PrepareCore(Data source, IReadOnlyDictionary<string, UnityEngine.Object> directory,
            CanonicalCarryGraphCodec codec, Func<bool> admitted, int texts)
        {
            var actual = CaptureCore(directory, texts); var references = DirectoryCore(directory, texts);
            if (!source.Owners.SequenceEqual(actual.Owners) || source.Powers.Length != texts || source.Corners.Length != texts
                || source.ContainerIsSelf.Length != texts || !source.Aliases.SequenceEqual(actual.Aliases))
                throw new InvalidDataException("Marker source readonly complete directory/alias differs.");
            for (var i = 0; i < texts; i++) {
                if (source.Powers[i] == null || source.Powers[i].Length != 10 || source.Corners[i] == null || source.Corners[i].Length != 4
                    || source.ContainerIsSelf[i] != actual.ContainerIsSelf[i]
                    || Enumerable.Range(0, 10).Any(index => !codec.SameRawSlot(source.Powers[i][index], actual.Powers[i][index], typeof(float), 0)))
                    throw new InvalidDataException("Marker readonly constructor configuration/source container differs.");
            }
            var consumed = false;
            return () => {
                if (consumed || !admitted()) throw new InvalidOperationException("Marker readonly cache lost single-use source admission.");
                consumed = true;
                var current = DirectoryCore(directory, texts);
                if (references.Any(pair => !ReferenceEquals(pair.Value, current[pair.Key])))
                    throw new InvalidOperationException("Marker readonly constructor reference changed.");
                var now = CaptureCore(directory, texts);
                if (!now.ContainerIsSelf.SequenceEqual(source.ContainerIsSelf) || !now.Aliases.SequenceEqual(source.Aliases))
                    throw new InvalidOperationException("Marker readonly original runner/alias changed before source writes.");
                for (var i = 0; i < texts; i++) {
                    var owner = directory[source.Owners[i]];
                    var corners = (Vector3[])Corners.GetValue(owner);
                    Array.Copy(source.Corners[i], corners, 4);
                    for (var index = 0; index < 4; index++) if (!codec.SameRawSlot(source.Corners[i][index], corners[index], typeof(Vector3), 0))
                        throw new InvalidOperationException("Marker readonly corner cache source readback differs.");
                }
                var after = CaptureCore(directory, texts);
                if (!after.ContainerIsSelf.SequenceEqual(source.ContainerIsSelf) || !after.Aliases.SequenceEqual(source.Aliases)
                    || Enumerable.Range(0, texts).Any(i => Enumerable.Range(0, 10).Any(j => !codec.SameRawSlot(source.Powers[i][j], after.Powers[i][j], typeof(float), 0)))
                    || Enumerable.Range(0, texts).Any(i => Enumerable.Range(0, 4).Any(j => !codec.SameRawSlot(source.Corners[i][j], after.Corners[i][j], typeof(Vector3), 0))))
                    throw new InvalidOperationException("Marker readonly complete configuration/source state readback differs.");
            };
        }
    }
}
