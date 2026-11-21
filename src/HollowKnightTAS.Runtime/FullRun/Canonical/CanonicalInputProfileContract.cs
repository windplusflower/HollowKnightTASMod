using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using InControl;
using Newtonsoft.Json.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalInputProfileContract
    {
        internal const string Resource = "HollowKnightTAS.Runtime.FullRun.CanonicalInputProfileFieldRegistry.json";
        internal static string SchemaIdentity { get; } = Read();
        private static readonly Dictionary<string, JToken> layouts = Load();
        private static readonly Dictionary<string, string> enums = ((JArray?)JObject.Parse(SchemaIdentity)["enums"]
            ?? throw new InvalidDataException("Input profile enum layouts are absent."))
            .ToDictionary(entry => (string?)entry["name"] ?? throw new InvalidDataException("Input profile enum name is absent."),
                entry => (string?)entry["underlying"] ?? throw new InvalidDataException("Input profile enum storage is absent."), StringComparer.Ordinal);
        private static string Read()
        {
            using var stream = typeof(CanonicalInputProfileContract).Assembly.GetManifestResourceStream(Resource)
                ?? throw new InvalidDataException("Original input profile layouts are absent.");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
            var text = reader.ReadToEnd();
            if (text.Length > 65536) throw new InvalidDataException("Input profile layout bound exceeded.");
            return text;
        }
        private static Dictionary<string, JToken> Load()
        {
            var data = JObject.Parse(SchemaIdentity);
            if ((int?)data["version"] != 1)
                throw new InvalidDataException("Original input profile module differs.");
            var actualMvid = typeof(InputDeviceProfile).Module.ModuleVersionId.ToString("D");
            if ((string?)data["moduleMvid"] != actualMvid)
                CanonicalSceneMappingCarry.WarnOnce("InputProfileContract:Mvid:" + actualMvid,
                    "[HKTAS] CanonicalInputProfileFieldRegistry moduleMvid differs from loaded Assembly-CSharp (continuing): actual=" + actualMvid);
            return ((JArray?)data["types"] ?? throw new InvalidDataException("Input profile types are absent."))
                .ToDictionary(entry => (string?)entry["name"] ?? throw new InvalidDataException("Input profile type name is absent."), entry => entry, StringComparer.Ordinal);
        }
        private static void ValidateType(Type type)
        {
            if (type == typeof(string) || type == typeof(bool) || type == typeof(byte) || type == typeof(short)
                || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) || type == typeof(float)) return;
            if (type.IsArray) { ValidateType(type.GetElementType()!); return; }
            if (type.Module.ModuleVersionId != typeof(InputDeviceProfile).Module.ModuleVersionId)
                throw new InvalidDataException("Input profile contains a foreign type: " + type.FullName);
            if (type.IsEnum && enums.TryGetValue(type.FullName!, out var underlying) && Enum.GetUnderlyingType(type).FullName == underlying) return;
            if (layouts.ContainsKey(type.FullName!) || typeof(InputDeviceProfile).IsAssignableFrom(type) && !type.IsAbstract
                && type.Namespace == "InControl.NativeDeviceProfiles") return;
            throw new InvalidDataException("Input profile contains an unregistered type: " + type.FullName);
        }
        private static FieldInfo[] Fields(Type type)
        {
            ValidateType(type);
            var layoutType = typeof(InputDeviceProfile).IsAssignableFrom(type) ? typeof(InputDeviceProfile) : type;
            if (!layouts.TryGetValue(layoutType.FullName!, out var entry)) throw new InvalidDataException("Input profile field layout is unregistered.");
            var fields = CanonicalCarryGraphCodec.Fields(type);
            var actual = fields.Select(field => field.DeclaringType!.FullName + "/" + field.Name + ":" + field.FieldType.FullName);
            var expected = ((JArray?)entry["fields"] ?? throw new InvalidDataException("Input profile fields are absent."))
                .Select(field => (string?)field["owner"] + "/" + (string?)field["name"] + ":" + (string?)field["type"]);
            if (!actual.SequenceEqual(expected)) throw new InvalidDataException("Original input profile field layout differs: " + type.FullName);
            return fields;
        }
        internal static byte[] Capture(IReadOnlyList<InputDeviceProfile> profiles, out object[] owners)
        {
            if (profiles.Any(profile => ReferenceEquals(profile, null))) throw new InvalidDataException("Input profile contains a null owner.");
            var references = new List<object>();
            var packet = CanonicalFrozenValuePacket.Capture(profiles.Cast<object>().ToArray(), Fields, ValidateType, references.Add);
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            object Static(string name) => typeof(InputDeviceProfile).GetField(name, flags)?.GetValue(null)
                ?? throw new InvalidDataException("Original input profile static is absent: " + name);
            var hidden = Static("hiddenProfiles") as HashSet<Type> ?? throw new InvalidDataException("Input profile hidden-type set differs.");
            if (hidden.Count > 4096) throw new InvalidDataException("Input profile hidden-type bound exceeded.");
            if (hidden.Contains(null!)) throw new InvalidDataException("Input profile hidden-type set contains null.");
            references.Add(hidden);
            var hiddenTypes = hidden.OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
            var staticRoots = new List<object> { Static("MenuKey"), Static("EscapeKey") };
            foreach (var type in hiddenTypes) {
                if (type == null || type.Module.ModuleVersionId != typeof(InputDeviceProfile).Module.ModuleVersionId
                    || !typeof(InputDeviceProfile).IsAssignableFrom(type) || type.IsAbstract)
                    throw new InvalidDataException("Input profile hidden set contains a foreign type.");
                references.Add(type); staticRoots.Add(type.Module.ModuleVersionId.ToString("D") + "/" + type.FullName);
            }
            var statics = CanonicalFrozenValuePacket.Capture(staticRoots.ToArray(), Fields, ValidateType);
            if (packet.Length + statics.Length + 8 > CanonicalFrozenValuePacket.MaximumBytes)
                throw new InvalidDataException("Combined input profile configuration bound exceeded.");
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(packet.Length); writer.Write(packet); writer.Write(statics.Length); writer.Write(statics);
            owners = references.ToArray(); return stream.ToArray();
        }
    }
}
