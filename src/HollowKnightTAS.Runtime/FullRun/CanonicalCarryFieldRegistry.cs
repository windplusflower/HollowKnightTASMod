using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HutongGames.PlayMaker;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalCarryFieldRegistry
    {
        private const string Resource = "HollowKnightTAS.Runtime.FullRun.CanonicalCarryFieldRegistry.json";
        private static readonly string[] NativeValues = { "UnityEngine.Vector2", "UnityEngine.Vector3", "UnityEngine.Vector4",
            "UnityEngine.Quaternion", "UnityEngine.Color", "UnityEngine.Color32", "UnityEngine.Rect", "UnityEngine.Bounds",
            "UnityEngine.LayerMask", "UnityEngine.Matrix4x4", "UnityEngine.RaycastHit" };
        private static readonly FieldInfo RaycastCollider = typeof(RaycastHit).GetField("m_Collider", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidDataException("Pinned RaycastHit collider binding is absent.");
        private static readonly FieldInfo[] LocalEventFields = new[] { "name", "isGlobal", "isSystemEvent", "<Path>k__BackingField" }
            .Select(name => typeof(FsmEvent).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidDataException("Pinned FsmEvent field binding is absent: " + name)).ToArray();

        internal static CanonicalCarryGraphCodec Create()
        {
            using var stream = typeof(CanonicalCarryFieldRegistry).Assembly.GetManifestResourceStream(Resource)
                ?? throw new InvalidDataException("Frozen carry field registry is absent.");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
            var text = reader.ReadToEnd();
            if (Encoding.UTF8.GetByteCount(text) > 2 * 1024 * 1024) throw new InvalidDataException("Carry field registry exceeds its bound.");
            var registry = JObject.Parse(text);
            if ((int?)registry["version"] != 1 || (bool?)registry["diagnosticOnly"] != true || !(registry["types"] is JArray entries))
                throw new InvalidDataException("Unsupported frozen carry field registry.");
            var assemblies = new[] { typeof(object).Assembly, typeof(Fsm).Assembly, typeof(HeroController).Assembly,
                typeof(Vector3).Assembly, typeof(UnityEngine.UI.Image).Assembly, typeof(AudioClip).Assembly,
                typeof(RaycastHit).Assembly, typeof(Rigidbody2D).Assembly }.Distinct().ToArray();
            var types = new List<Type>(); var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var name = (string?)entry["name"] ?? throw new InvalidDataException("Frozen carry type name is absent.");
                if (!names.Add(name)) throw new InvalidDataException("Frozen carry type name is duplicated.");
                var type = assemblies.Select(assembly => assembly.GetType(name, false)).FirstOrDefault(value => value != null);
                // Unknown/abstract/native types are never instantiated. If an
                // actual field reaches one, Capture retains an explicit gap.
                if (type == null || !Allocatable(type)) continue;
                if (!type.IsArray && !type.IsPrimitive && !type.IsEnum && type != typeof(string) && type != typeof(decimal))
                {
                    if (!(entry["fields"] is JArray fields)) continue;
                    var actual = CanonicalCarryGraphCodec.Fields(type).Select(field => new {
                        owner = field.DeclaringType!.FullName, name = field.Name, type = field.FieldType.FullName }).ToArray();
                    var expected = fields.Select(field => new { owner = (string?)field["owner"] ?? throw new InvalidDataException("Carry field owner is absent."),
                        name = (string?)field["name"] ?? throw new InvalidDataException("Carry field name is absent."),
                        type = (string?)field["type"] ?? throw new InvalidDataException("Carry field type is absent.") }).ToArray();
                    if (!actual.SequenceEqual(expected))
                        throw new InvalidDataException("Frozen carry field layout differs: " + name);
                }
                types.Add(type);
            }
            // The placeholder enum is a concrete boxed value reached through
            // FsmEnum.value (declared System.Object), so it is not a field type
            // in the original schema. It carries only its underlying bits.
            types.Add(typeof(HutongGames.PlayMaker.None));
            using var hash = SHA256.Create();
            return new CanonicalCarryGraphCodec(types, "canonical-carry-fields-v1:" + BitConverter.ToString(
                hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant(), PortableValue, AllocatedValue);
        }
        // Only the original empty placeholder can be allocated as local data.
        // Named/global/system events must retain their shared external identity.
        internal static bool IsLocalEmptyEvent(FsmEvent value) => Equals(LocalEventFields[0].GetValue(value), "")
            && Equals(LocalEventFields[1].GetValue(value), false) && Equals(LocalEventFields[2].GetValue(value), false)
            && LocalEventFields[3].GetValue(value) == null;
        private static string? AllocatedValue(object value) => value is FsmEvent fsmEvent && !IsLocalEmptyEvent(fsmEvent)
            ? "allocated-FsmEvent-needs-shared-global-binding" : null;
        private static string? PortableValue(object value) => value is RaycastHit && (int)RaycastCollider.GetValue(value) != 0
            ? "RaycastHit-native-collider-id-needs-portable-rebinding" : null;
        private static bool Allocatable(Type type)
        {
            if (type.IsArray) return type.GetArrayRank() == 1;
            if (type.IsEnum) return true;
            if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters || type.IsPointer || type.IsByRef
                || type == typeof(IntPtr) || type == typeof(UIntPtr) || typeof(UnityEngine.Object).IsAssignableFrom(type)
                || typeof(MemberInfo).IsAssignableFrom(type) || typeof(Delegate).IsAssignableFrom(type)
                || typeof(MarshalByRefObject).IsAssignableFrom(type) || type.Namespace?.StartsWith("System.Threading", StringComparison.Ordinal) == true)
                return false;
            // Opaque Unity values (collisions, native handles, instance-id
            // structs) require their own portable binding adapter.
            if (type.Namespace?.StartsWith("UnityEngine", StringComparison.Ordinal) == true) return NativeValues.Contains(type.FullName);
            return true;
        }
    }
}
