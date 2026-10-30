using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
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

        internal object Capture(Mesh mesh, object snapshot, bool heroSubmesh = false) {
            var result = JObject.FromObject(CaptureProjectionWithDetails(snapshot, (int)mesh.hideFlags, heroSubmesh));
            result["registeredPolicy"] = CanonicalRegisteredVisualMeshCachePolicy.Owns(mesh) ? CanonicalRegisteredVisualMeshCachePolicy.Id : null;
            if (heroSubmesh) result["rawHeroSubmeshBounds"] = JArray.FromObject(Enumerable.Range(0, mesh.subMeshCount).Select(index => {
                var bounds = mesh.GetSubMesh(index).bounds; var center = bounds.center; var size = bounds.extents;
                return new { index, bits = new[] { center.x, center.y, center.z, size.x, size.y, size.z }
                    .Select(value => BitConverter.ToInt32(BitConverter.GetBytes(value), 0)).ToArray() };
            }).ToArray());
            return result;
        }
        internal object CaptureProjection(object snapshot, int hideFlags) => CaptureProjectionWithDetails(snapshot, hideFlags, false);
        internal object CaptureProjectionWithDetails(object snapshot, int hideFlags, bool heroSubmesh)
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
                heroSubmeshDetails = heroSubmesh ? SubmeshDetails(snapshot) : null,
                emptyArrayAliasGroups = empty.Values.Select(paths => paths.OrderBy(path => path, StringComparer.Ordinal).ToArray()).OrderBy(paths => paths[0], StringComparer.Ordinal).ToArray(),
                independentPerField = true, copiedExistingProjection = true };
        }
        private object[] SubmeshDetails(object snapshot)
        {
            var field = Fields(snapshot.GetType()).Single(f => Name(f.Name) == "submeshes");
            var array = (Array)field.GetValue(snapshot);
            if (array.Length > 256) throw new InvalidOperationException("Hero submesh diagnostic bound exceeded.");
            var result = new List<object>();
            for (var index = 0; index < array.Length; index++) {
                var item = array.GetValue(index)!;
                var description = Fields(item.GetType()).Single(f => Name(f.Name) == "description").GetValue(item);
                var indices = Fields(item.GetType()).Single(f => Name(f.Name) == "indices").GetValue(item);
                if (!CopiedHash(indices, out var digest, out var count, out var stride))
                    throw new InvalidOperationException("Hero copied index projection missing.");
                var scalars = new List<object>();
                void Walk(object value, string path, int depth) {
                    if (depth > 4 || scalars.Count >= 64) throw new InvalidOperationException("Hero descriptor scalar bound exceeded.");
                    var type = value.GetType();
                    if (type.IsPrimitive || type.IsEnum) {
                        scalars.Add(new { path, type = type.FullName, value,
                            floatBits = value is float number ? (int?)BitConverter.ToInt32(BitConverter.GetBytes(number), 0) : null });
                        return;
                    }
                    if (!type.IsValueType) throw new InvalidOperationException("Hero descriptor must contain only copied value fields.");
                    foreach (var child in Fields(type)) Walk(child.GetValue(value)!, path + "." + child.Name, depth + 1);
                }
                Walk(description!, "description", 0);
                result.Add(new { index, descriptor = scalars.ToArray(), indices = new { count, stride, sha256 = digest } });
            }
            return result.ToArray();
        }
        public void Dispose() => reader.Dispose();
    }
}
