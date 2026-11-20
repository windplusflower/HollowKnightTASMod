using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Hash all bits only for registered, recursively blittable, padding-free
    // leaf arrays. Unknown layouts keep the ordinary field traversal. Pins are
    // temporary and released in the same observation; no owners are retained.
    internal sealed class LoadingPackedArrayDigest : IDisposable
    {
        internal sealed class Layout
        {
            internal Layout(int size, int nodes, int depth, string schema)
            { Size = size; Nodes = nodes; Depth = depth; Schema = schema; }
            internal int Size { get; }
            internal int Nodes { get; }
            internal int Depth { get; }
            internal string Schema { get; }
        }
        private readonly Func<Type, bool> registered;
        private readonly Func<Type, FieldInfo[]> fields;
        private readonly Func<Type, string> identity;
        private readonly Dictionary<Type, Layout?> layouts = new Dictionary<Type, Layout?>();
        private readonly SHA256 hash = SHA256.Create();

        internal LoadingPackedArrayDigest(Func<Type, bool> registered,
            Func<Type, FieldInfo[]> fields, Func<Type, string> identity)
        { this.registered = registered; this.fields = fields; this.identity = identity; }

        internal Layout? Describe(Type type)
        {
            if (layouts.TryGetValue(type, out var result)) return result;
            result = Build(type);
            layouts.Add(type, result);
            return result;
        }

        private Layout? Build(Type type)
        {
            var size = PrimitiveSize(type);
            if (size != 0) return new Layout(size, 1, 0, Hash(identity(type) + ":bytes=" + size));
            if (type.IsPrimitive || type == typeof(IntPtr) || type == typeof(UIntPtr)) return null;
            if (!registered(type) || !type.IsValueType || type.IsEnum
                || (!type.IsLayoutSequential && !type.IsExplicitLayout)) return null;
            var members = fields(type);
            if (members.Length == 0 || members.Length > 256) return null;
            var children = new List<Tuple<FieldInfo, int, Layout>>();
            foreach (var field in members)
            {
                var child = Describe(field.FieldType);
                if (child == null) return null;
                var offset = checked((int)Marshal.OffsetOf(type, field.Name));
                children.Add(Tuple.Create(field, offset, child));
            }
            var total = 0;
            var nodes = 1;
            var depth = 0;
            var schema = new StringBuilder(identity(type));
            foreach (var child in children.OrderBy(value => value.Item2))
            {
                // Every byte must belong to a verified leaf field. Registered
                // scalar unions (Color32) may overlap: the full bits plus every
                // field's schema preserve all interpretations of those bytes.
                if (child.Item2 < 0 || child.Item2 > total) return null;
                total = Math.Max(total, checked(child.Item2 + child.Item3.Size));
                nodes = checked(nodes + child.Item3.Nodes);
                depth = Math.Max(depth, child.Item3.Depth + 1);
                schema.Append('|').Append(child.Item1.Name.Length).Append(':').Append(child.Item1.Name)
                    .Append('@').Append(child.Item2.ToString(CultureInfo.InvariantCulture))
                    .Append(':').Append(child.Item3.Size).Append(':').Append(child.Item3.Schema);
            }
            if (total != Marshal.SizeOf(type)) return null;
            return new Layout(total, nodes, depth, Hash(schema.ToString()));
        }

        internal string Capture(Array array, Layout layout)
        {
            if (!BitConverter.IsLittleEndian || array.Rank != 1 || array.GetLowerBound(0) != 0)
                throw new InvalidOperationException("P0 packed leaf array requires a zero-based little-endian layout.");
            var length = checked(array.Length * layout.Size);
            if (length > 2 * 1024 * 1024) throw new InvalidOperationException("P0 packed leaf array byte budget exceeded.");
            var bytes = new byte[length];
            if (length != 0)
            {
                var pin = GCHandle.Alloc(array, GCHandleType.Pinned);
                try { Marshal.Copy(pin.AddrOfPinnedObject(), bytes, 0, length); }
                finally { pin.Free(); }
            }
            return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private string Hash(string value) => BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value)))
            .Replace("-", "").ToLowerInvariant();

        private static int PrimitiveSize(Type type)
        {
            if (type == typeof(byte) || type == typeof(sbyte)) return 1;
            if (type == typeof(short) || type == typeof(ushort)) return 2;
            if (type == typeof(int) || type == typeof(uint) || type == typeof(float)) return 4;
            if (type == typeof(long) || type == typeof(ulong) || type == typeof(double)) return 8;
            // bool/char, enum marshal rules and process pointers are excluded.
            return 0;
        }

        public void Dispose() { layouts.Clear(); hash.Dispose(); }
    }
}
