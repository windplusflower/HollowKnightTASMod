using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Diagnostic projection only. The caller has already obtained these CPU
    // value hashes through the ordinary asset observer; no mesh data getter is repeated.
    internal sealed class LoadingMeshValueAudit : IDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly LoadingFieldDigest reader = new LoadingFieldDigest(_ => null, _ => false);
        private readonly Dictionary<Type, FieldInfo[]> layouts = new Dictionary<Type, FieldInfo[]>();
        private sealed class Identity : IEqualityComparer<object>
        {
            public new bool Equals(object? a, object? b) => ReferenceEquals(a, b);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
        private static string Name(string name) => name.StartsWith("<", StringComparison.Ordinal) && name.EndsWith(">i__Field", StringComparison.Ordinal)
            ? name.Substring(1, name.Length - 10) : name;
        private FieldInfo[] Fields(Type type)
        {
            if (!layouts.TryGetValue(type, out var result))
                layouts.Add(type, result = type.GetFields(Flags).OrderBy(field => field.Name, StringComparer.Ordinal).ToArray());
            return result;
        }
        private bool CopiedHash(object? value, out string digest, out int count, out int stride)
        {
            digest = ""; count = stride = 0;
            if (value == null) return false;
            var fields = Fields(value.GetType());
            var countField = fields.SingleOrDefault(field => Name(field.Name) == "count" && field.FieldType == typeof(int));
            var strideField = fields.SingleOrDefault(field => Name(field.Name) == "stride" && field.FieldType == typeof(int));
            var hashField = fields.SingleOrDefault(field => Name(field.Name) == "sha256" && field.FieldType == typeof(string));
            if (fields.Length != 3 || countField == null || strideField == null || hashField == null) return false;
            count = (int)countField.GetValue(value); stride = (int)strideField.GetValue(value); digest = (string)hashField.GetValue(value);
            return digest != null && digest.Length == 64;
        }

        internal object Capture(Mesh mesh, object snapshot) => CaptureProjection(snapshot, (int)mesh.hideFlags);
        internal object CaptureProjection(object snapshot, int hideFlags)
        {
            var fields = Fields(snapshot.GetType());
            if (fields.Length > 64) throw new InvalidOperationException("Mesh value projection field bound exceeded.");
            var values = new List<object>(); var channels = new List<object>();
            foreach (var field in fields) {
                var name = Name(field.Name); var raw = field.GetValue(snapshot);
                object? scalar = raw != null && (raw.GetType().IsPrimitive || raw.GetType().IsEnum) ? raw : null;
                var copied = CopiedHash(raw, out var direct, out var copiedCount, out var stride);
                values.Add(new { name, type = field.FieldType.FullName, digest = copied ? direct : reader.CaptureFields(snapshot, "projection", field.Name).Sha256,
                    scalar, copiedArrayCount = copied ? (int?)copiedCount : null, copiedArrayStride = copied ? (int?)stride : null });
                if ((name == "uv" || name == "submeshes" || name == "blendShapes") && raw is Array array) {
                    var limit = name == "uv" ? 8 : name == "submeshes" ? 256 : 128;
                    if (array.Length > limit) throw new InvalidOperationException("Mesh value projection channel bound exceeded.");
                    for (var i = 0; i < array.Length; i++) {
                        var item = array.GetValue(i); var isCopied = CopiedHash(item, out var itemHash, out var itemCount, out var itemStride);
                        channels.Add(new { field = name, index = i, digest = isCopied ? itemHash : reader.Capture(item, "projection-channel", expandRoot: true).Sha256,
                            copiedArrayCount = isCopied ? (int?)itemCount : null, copiedArrayStride = isCopied ? (int?)itemStride : null });
                    }
                }
            }
            var empty = new Dictionary<object, List<string>>(new Identity()); var seen = new HashSet<object>(new Identity()); var count = 0;
            void Visit(object? value, string path) {
                if (value == null || value is string || value.GetType().IsPrimitive || value.GetType().IsEnum) return;
                if (++count > 4096) throw new InvalidOperationException("Mesh projection alias bound exceeded.");
                if (value is Array array) {
                    if (array.Length == 0) { if (!empty.TryGetValue(array, out var paths)) empty.Add(array, paths = new List<string>()); paths.Add(path); return; }
                    if (!seen.Add(array)) return;
                    for (var i = 0; i < array.Length; i++) Visit(array.GetValue(i), path + "[" + i + "]");
                    return;
                }
                if (!value.GetType().IsValueType && !seen.Add(value)) return;
                foreach (var field in Fields(value.GetType()))
                    Visit(field.GetValue(value), path + "." + Name(field.Name));
            }
            Visit(snapshot, "projection");
            return new { hideFlags, fields = values.ToArray(), channels = channels.ToArray(),
                emptyArrayAliasGroups = empty.Values.Select(paths => paths.OrderBy(path => path, StringComparer.Ordinal).ToArray()).OrderBy(paths => paths[0], StringComparer.Ordinal).ToArray(),
                independentPerField = true, copiedExistingProjection = true };
        }
        public void Dispose() => reader.Dispose();
    }
}
