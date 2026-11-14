using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // The native value is projected into graph roots. In particular, Font and
    // background Texture2D go through the ordinary referenced-asset resolver.
    internal sealed class ResidentGuiValues
    {
        internal sealed class Row { public int Kind, Parent = -1, Slot = -1; public bool Disposed; }
        internal sealed class Body { public object?[] Native = Array.Empty<object?>(), Managed = Array.Empty<object?>(); }
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly Type[] Types = { typeof(GUIStyle), typeof(GUIStyleState), typeof(RectOffset), typeof(TextGenerator) };
        private static readonly string[][] Properties = {
            new[] { "rawName", "font", "imagePosition", "alignment", "wordWrap", "clipping", "contentOffset", "Internal_clipOffset",
                "fixedWidth", "fixedHeight", "stretchWidth", "stretchHeight", "fontSize", "fontStyle", "richText" },
            new[] { "background", "textColor" }, new[] { "left", "right", "top", "bottom" }, Array.Empty<string>()
        };
        private static readonly PropertyInfo[][] Access = Properties.Select((names, kind) =>
            names.Select(name => Types[kind].GetProperty(name, Instance) ?? throw Error("native property missing: " + name)).ToArray()).ToArray();
        private static readonly FieldInfo[][] Fields = Types.Select(type => CanonicalCarryGraphCodec.Fields(type)
            .Where(field => field.Name != "m_Ptr" && field.Name != "m_SourceStyle").ToArray()).ToArray();
        private static readonly FieldInfo[] Pointers = Types.Select(type => type.GetField("m_Ptr", Instance)!).ToArray();
        private static readonly MethodInfo[] SubPointers = { typeof(GUIStyle).GetMethod("GetStyleStatePtr", Instance)!, typeof(GUIStyle).GetMethod("GetRectOffsetPtr", Instance)! };
        private static readonly ConstructorInfo[] Borrow = { typeof(GUIStyleState).GetConstructor(Instance, null, new[]{typeof(GUIStyle), typeof(IntPtr)}, null)!,
            typeof(RectOffset).GetConstructor(Instance, null, new[]{typeof(object), typeof(IntPtr)}, null)! };
        private readonly List<Row> rows = new List<Row>();
        private readonly List<object> owners = new List<object>();
        private readonly List<IntPtr> addresses = new List<IntPtr>();
        private readonly HashSet<IntPtr> ownedStorage = new HashSet<IntPtr>();
        private readonly Dictionary<object, int> indices = new Dictionary<object, int>(ReferenceComparer.Instance);
        private readonly Dictionary<string, object> roots = new Dictionary<string, object>(StringComparer.Ordinal);
        internal IReadOnlyDictionary<string, object> Roots => roots;
        internal Row[] Rows => rows.ToArray();
        private static string Key(int index) => "resident-gui/" + index.ToString(CultureInfo.InvariantCulture);
        private static int Count(int kind) => kind == 1 ? 8 : 4;
        private static IntPtr Address(object owner, int kind) => (IntPtr)Pointers[kind].GetValue(owner)!;
        private static IntPtr Sub(object style, int kind, int slot) => (IntPtr)SubPointers[kind-1].Invoke(style, new object[] { slot })!;
        private static object BorrowAt(object style, int kind, int slot) => Borrow[kind-1].Invoke(new object[] { style, Sub(style, kind, slot) });
        private static IEnumerable<(object Owner, PropertyInfo Property)> Members(object owner, int kind)
        {
            foreach (var property in Access[kind]) yield return (owner, property);
            if (kind != 0) yield break;
            for (var part = 1; part <= 2; part++)
                for (var slot = 0; slot < Count(part); slot++) {
                    var borrowed = BorrowAt(owner, part, slot);
                    foreach (var property in Access[part]) yield return (borrowed, property);
                }
        }
        private static Body Read(object owner, int kind) => new Body {
            Native = owner is TextGenerator generator ? Address(generator, 3) == IntPtr.Zero ? Array.Empty<object?>() : ReadGenerator(generator)
                : Members(owner, kind).Select(member => member.Property.GetValue(member.Owner, null)).ToArray(),
            Managed = Fields[kind].Select(field => field.GetValue(owner)).ToArray()
        };
        private static readonly int GeneratorString = GeneratorField("m_LastString"), GeneratorSettings = GeneratorField("m_LastSettings"),
            GeneratorGenerated = GeneratorField("m_HasGenerated");
        private static int GeneratorField(string name)
        {
            var index = Array.FindIndex(Fields[3], field => field.Name == name);
            return index >= 0 ? index : throw Error("text generator field absent: " + name);
        }
        // The array getters copy native results without touching the wrapper's lazy
        // managed lists. Keep exact bits for the three pinned, blittable Unity structs.
        private static byte[] Bytes<T>(T[] values) where T : struct
        {
            var bytes = new byte[checked(values.Length * Marshal.SizeOf(typeof(T)))];
            if (bytes.Length == 0) return bytes;
            var handle = GCHandle.Alloc(values, GCHandleType.Pinned);
            try { Marshal.Copy(handle.AddrOfPinnedObject(), bytes, 0, bytes.Length); }
            finally { handle.Free(); }
            return bytes;
        }
        private static object?[] ReadGenerator(TextGenerator generator)
        {
            if ((uint)generator.vertexCount > 65536 || (uint)generator.characterCount > 16384 || (uint)generator.lineCount > 16384)
                throw Error("text generator exceeds bounded native output");
            return new object?[] { generator.vertexCount, generator.characterCount, generator.lineCount, generator.fontSizeUsedForBestFit,
                Bytes(new[] { generator.rectExtents }), Bytes(generator.GetVerticesArray()), Bytes(generator.GetCharactersArray()), Bytes(generator.GetLinesArray()) };
        }
        private static bool VerticesMatch(byte[] expBytes, byte[] actBytes)
        {
            if (expBytes.Length != actBytes.Length) return false;
            var size = Marshal.SizeOf(typeof(UIVertex));
            if (size <= 0 || expBytes.Length % size != 0) return false;
            int count = expBytes.Length / size;
            for (int i = 0; i < count; i++) {
                int offset = i * size;
                for (int b = 0; b < 44; b++) {
                    if (expBytes[offset + b] != actBytes[offset + b]) return false;
                }
            }
            return true;
        }

        private static bool GeneratorEqual(object?[] expected, object?[] current, out string? diffDetail)
        {
            diffDetail = null;
            if (expected.Length != current.Length) {
                diffDetail = "length mismatch exp=" + expected.Length + " cur=" + current.Length;
                return false;
            }
            for (int i = 0; i < expected.Length; i++) {
                if (expected[i] is byte[] a && current[i] is byte[] b) {
                    if (a.Length != b.Length) {
                        diffDetail = "slot=" + i + " byte length mismatch exp=" + a.Length + " cur=" + b.Length;
                        return false;
                    }
                    if (i == 5) {
                        if (!VerticesMatch(a, b)) {
                            diffDetail = "slot=5 vertex geometry mismatch";
                            return false;
                        }
                    } else if (!a.SequenceEqual(b)) {
                        diffDetail = "slot=" + i + " byte content mismatch";
                        return false;
                    }
                } else if (!Equals(expected[i], current[i])) {
                    diffDetail = "slot=" + i + " mismatch exp=" + expected[i] + " act=" + current[i];
                    return false;
                }
            }
            return true;
        }
        internal string? Discover(object owner, Action<string, object> addRoot)
        {
            var kind = Array.IndexOf(Types, owner.GetType());
            if (kind < 0) return null;
            if (indices.TryGetValue(owner, out var id)) return Key(id);
            var address = Address(owner, kind);
            if (address == IntPtr.Zero && owner is not TextGenerator) throw Error("native wrapper has no live storage");
            var row = new Row { Kind = kind, Disposed = address == IntPtr.Zero };
            var parent = Types[kind].GetField("m_SourceStyle", Instance)?.GetValue(owner);
            if (parent != null) {
                if (parent.GetType() != typeof(GUIStyle) || kind == 0) throw Error("unrecognized native storage owner");
                Discover(parent, addRoot); row.Parent = indices[parent];
                row.Slot = Enumerable.Range(0, Count(kind)).Single(slot => Sub(parent, kind, slot) == address);
            }
            id = rows.Count; Register(row, owner, Read(owner, kind));
            addRoot(Key(id) + "/body", roots[Key(id) + "/body"]); return Key(id);
        }
        private void Register(Row row, object owner, Body body)
        {
            if (rows.Count >= 65536) throw Error("too many GUI values");
            var address = Address(owner, row.Kind);
            if (address == IntPtr.Zero ? !row.Disposed : row.Parent < 0 && !ownedStorage.Add(address))
                throw Error("absent or multiply owned GUI storage");
            indices.Add(owner, rows.Count); roots.Add(Key(rows.Count) + "/body", body);
            rows.Add(row); owners.Add(owner); addresses.Add(address);
        }
        internal string? Bind(object value) => indices.TryGetValue(value, out var index) ? Key(index) : null;
        internal object? Resolve(string key)
        {
            const string prefix = "resident-gui/";
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) return null;
            if (!int.TryParse(key.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || index < 0 || index >= owners.Count || Key(index) != key) throw Error("unbound GUI identity");
            return owners[index];
        }
        internal static void Validate(Row[] rows)
        {
            if (rows == null || rows.Length > 65536) throw Error("invalid GUI directory");
            for (var index = 0; index < rows.Length; index++) {
                var row = rows[index];
                if (row == null || row.Kind < 0 || row.Kind >= Types.Length || row.Parent < -1 || row.Parent >= index
                    || row.Disposed && row.Kind != 3 || row.Kind == 3 && row.Parent >= 0
                    || (row.Parent < 0 ? row.Slot != -1 : row.Kind == 0 || rows[row.Parent].Kind != 0 || row.Slot < 0 || row.Slot >= Count(row.Kind)))
                    throw Error("invalid GUI storage relationship");
            }
        }
        internal static ResidentGuiValues Prepare(Row[] rows)
        {
            Validate(rows);
            var result = new ResidentGuiValues();
            foreach (var row in rows) {
                var owner = row.Parent < 0 ? Activator.CreateInstance(Types[row.Kind])! : BorrowAt(result.owners[row.Parent], row.Kind, row.Slot);
                if (row.Disposed) ((IDisposable)owner).Dispose();
                result.Register(row, owner, new Body());
            }
            return result;
        }
        internal void Apply()
        {
            for (var i = 0; i < rows.Count; i++) {
                var owner = owners[i]; var kind = rows[i].Kind; var body = (Body)roots[Key(i) + "/body"];
                var members = Members(owner, kind).ToArray();
                if ((kind != 3 && body.Native.Length != members.Length) || body.Managed.Length != Fields[kind].Length) throw Error("GUI body layout differs");
                if (owner is TextGenerator generator && !rows[i].Disposed) {
                    var text = (string?)body.Managed[GeneratorString];
                    if (text != null || (bool)body.Managed[GeneratorGenerated]!)
                        generator.Populate(text, (TextGenerationSettings)body.Managed[GeneratorSettings]!);
                }
                for (var j = 0; j < members.Length; j++) members[j].Property.SetValue(members[j].Owner, body.Native[j], null);
                for (var j = 0; j < Fields[kind].Length; j++) Fields[kind][j].SetValue(owner, body.Managed[j]);
            }
            Verify();
        }
        internal void Verify()
        {
            for (var i = 0; i < rows.Count; i++) {
                var row = rows[i]; var expected = (Body)roots[Key(i) + "/body"];
                if (Address(owners[i], row.Kind) != addresses[i] || row.Parent >= 0 && addresses[i] != Sub(owners[row.Parent], row.Kind, row.Slot))
                    throw Error("GUI storage identity changed");
                var current = Read(owners[i], row.Kind);
                if (row.Kind == 3) {
                    if (!GeneratorEqual(expected.Native, current.Native, out var genDiff)) {
                        var genText = expected.Managed.Length > GeneratorString ? expected.Managed[GeneratorString] : "n/a";
                        throw Error($"GUI native readback differs: row={i}, kind=TextGenerator, text='{genText}', diff={genDiff}, exp={DescribeArray(expected.Native)}, act={DescribeArray(current.Native)}");
                    }
                } else {
                    var members = Members(owners[i], row.Kind).ToArray();
                    for (var j = 0; j < expected.Native.Length; j++) {
                        var expVal = j < expected.Native.Length ? expected.Native[j] : null;
                        var actVal = j < current.Native.Length ? current.Native[j] : null;
                        if (!SameItem(expVal, actVal)) {
                            var memberName = j < members.Length ? $"{members[j].Owner.GetType().Name}.{members[j].Property.Name}" : $"native[{j}]";
                            throw Error($"GUI native readback differs: row={i}, kind={Types[row.Kind].Name}, member={memberName}, exp={DescribeVal(expVal)}, act={DescribeVal(actVal)}");
                        }
                    }
                }
                for (var j = 0; j < expected.Managed.Length; j++) {
                    var expVal = j < expected.Managed.Length ? expected.Managed[j] : null;
                    var actVal = j < current.Managed.Length ? current.Managed[j] : null;
                    if (!SameItem(expVal, actVal)) {
                        var fieldName = j < Fields[row.Kind].Length ? Fields[row.Kind][j].Name : $"field[{j}]";
                        throw Error($"GUI managed readback differs: row={i}, kind={Types[row.Kind].Name}, field={fieldName}, exp={DescribeVal(expVal)}, act={DescribeVal(actVal)}");
                    }
                }
            }
        }
        private static bool SameItem(object? a, object? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is float f && b is float g) return SameFloat(f, g);
            if (a is Vector2 v && b is Vector2 w) return SameFloat(v.x, w.x) && SameFloat(v.y, w.y);
            if (a is Color c && b is Color d)
                return SameFloat(c.r, d.r) && SameFloat(c.g, d.g) && SameFloat(c.b, d.b) && SameFloat(c.a, d.a);
            if (a == null || !(a is string || a.GetType().IsValueType) || !a.Equals(b)) return false;
            return true;
        }
        private static string DescribeVal(object? val)
        {
            if (val == null) return "null";
            if (val is byte[] bytes) return $"bytes[{bytes.Length}]";
            return val.ToString();
        }
        private static string DescribeArray(object?[] arr)
        {
            if (arr == null) return "null";
            return $"[{string.Join(", ", arr.Select(DescribeVal))}]";
        }
        private static bool Same(object?[] a, object?[] b)
        {
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
                if (!SameItem(a[i], b[i])) return false;
            return true;
        }
        private static bool SameFloat(float x, float y) => BitConverter.ToInt32(BitConverter.GetBytes(x), 0) == BitConverter.ToInt32(BitConverter.GetBytes(y), 0);
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
        }
        private static InvalidDataException Error(string detail) => new InvalidDataException("Resident GUI: " + detail);
    }
}
