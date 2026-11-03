using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    // One property definition for Material and every Renderer property block.
    // D is the complete name/type/value set. Interned IDs, packing, capacities
    // and hashes are rebuilt as dependencies; no source address is serialized.
    internal static class ResidentShaderPropertySheet
    {
        private const int MaximumProperties = 4096, MaximumValues = 1024 * 1024;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateSheet();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void UnarySheet(IntPtr sheet);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CopySheet(IntPtr destination, IntPtr source);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr PropertyName(ref int id);
        internal sealed class Data
        {
            public int Version = 1;
            public bool AllowAddProperty;
            public Property[] Properties = Array.Empty<Property>();
            public string[] CoverageGaps = Array.Empty<string>();
        }
        internal sealed class Property
        {
            public string Name = "";
            public int Kind, Elements;
            public uint Flags;
            public byte[] Values = Array.Empty<byte>();
            public TextureValue[] Textures = Array.Empty<TextureValue>();
        }
        internal sealed class TextureValue
        {
            public string Binding = "", DefaultName = "", ScaleOffset = "", TexelSize = "", HdrDecode = "";
            public int AssignedDimension, RequiredDimension, VrUsage;
            public uint BindMs;
        }
        internal sealed class NativeLayout
        {
            public int[] Starts = Array.Empty<int>(), NameIds = Array.Empty<int>();
            public uint[] Descriptors = Array.Empty<uint>();
            public byte[] Values = Array.Empty<byte>();
        }
        internal sealed class Prepared : IDisposable
        {
            private IntPtr pointer;
            private readonly Data data;
            private readonly Action admission;
            private readonly Func<object, string?> bind;
            internal IntPtr Pointer { get { admission(); return pointer != IntPtr.Zero ? pointer : throw Error("prepared sheet is disposed"); } }
            internal Prepared(Data source, Func<string, UnityEngine.Object> resolve, Func<object, string?> bind, Action admitted)
            {
                RequireCompleteCoverage(source); data = Copy(source); this.bind = bind ?? throw new ArgumentNullException(nameof(bind));
                admission = admitted ?? throw new ArgumentNullException(nameof(admitted)); admission();
                var engine = LoadingEngineClockProbe.RequirePinnedEngine();
                pointer = ((CreateSheet)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine.BaseAddress, 0x661700), typeof(CreateSheet)))();
                if (pointer == IntPtr.Zero) throw Error("native sheet allocation failed");
                try { Populate(pointer, data, resolve, admission); Verify(); }
                catch { Dispose(); throw; }
            }
            internal void Verify() => RequireEqual(data, Capture(Pointer, bind));
            internal void CopyIntoUniqueSheet(IntPtr destination)
            {
                admission(); var header = M.Read(destination, 160);
                if (M.I32(header, 12) != 1) throw Error("property-sheet destination is shared; its owner must detach through its native COW path first");
                var engine = LoadingEngineClockProbe.RequirePinnedEngine();
                ((CopySheet)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine.BaseAddress, 0x79a2b0), typeof(CopySheet)))(destination, Pointer);
                RequireEqual(data, Capture(destination, bind)); admission();
            }
            public void Dispose()
            {
                if (pointer == IntPtr.Zero) return;
                var old = pointer; pointer = IntPtr.Zero; var engine = LoadingEngineClockProbe.RequirePinnedEngine();
                ((UnarySheet)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine.BaseAddress, 0x661910), typeof(UnarySheet)))(old);
            }
        }
        internal static Prepared Prepare(Data source, Func<string, UnityEngine.Object> resolve, Func<object, string?> bind, Action admitted)
            => new Prepared(source, resolve, bind, admitted);
        internal static Data Capture(IntPtr pointer, Func<object, string?> bind)
        {
            if (bind == null) throw new ArgumentNullException(nameof(bind));
            var engine = LoadingEngineClockProbe.RequirePinnedEngine(); var header = M.Read(pointer, 160);
            if (M.Ptr(header, 0) != IntPtr.Add(engine.BaseAddress, 0x15df4c8) || M.I32(header, 12) < 1 || header[152] > 1)
                throw Error("native property-sheet identity differs");
            var count = Count(header, 64, MaximumProperties); var valueCount = Count(header, 128, MaximumValues);
            if (Count(header, 96, MaximumProperties) != count) throw Error("native property-name and descriptor counts differ");
            foreach (var offset in new[] { 48, 80, 112 }) {
                var size = M.U64(header, offset + 16); var capacity = M.U64(header, offset + 24) >> 1;
                if (size > capacity || capacity > MaximumValues || size != 0 && M.Ptr(header, offset) == IntPtr.Zero) throw Error("native property-array shape differs");
            }
            var starts = Enumerable.Range(0, 7).Select(index => M.I32(header, 16 + index * 4)).ToArray();
            if (starts[0] != 0 || starts[6] != count || starts.Any(value => value < 0 || value > count)
                || !starts.SequenceEqual(starts.OrderBy(value => value))) throw Error("native property type ranges differ");
            var nameBytes = M.Read(M.Ptr(header, 48), count * 4); var descriptors = M.Read(M.Ptr(header, 80), count * 4);
            var values = M.Read(M.Ptr(header, 112), valueCount);
            var nameReader = (PropertyName)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine.BaseAddress, 0x7d59e0), typeof(PropertyName));
            var names = Enumerable.Range(0, count).Select(index => Name(nameReader, M.I32(nameBytes, index * 4), false)).ToArray();
            string VectorName(int relative) {
                if (relative == -1) return "";
                if (relative < 0 || relative >= starts[2] - starts[1]) throw Error("texture auxiliary vector index differs");
                return names[starts[1] + relative];
            }
            var properties = new List<Property>(); var gaps = new List<string>();
            for (var kind = 0; kind < 6; kind++) for (var index = starts[kind]; index < starts[kind + 1]; index++) {
                var descriptor = M.U32(descriptors, index * 4); var offset = (int)(descriptor & 0xfffff); var elements = (int)((descriptor >> 20) & 0x3ff);
                var row = new Property { Name = names[index], Kind = kind, Elements = elements, Flags = descriptor >> 30 };
                if (elements < 1) throw Error("native property has no elements");
                if (kind >= 4) { gaps.Add("native-buffer-property:" + kind + ":" + row.Name); properties.Add(row); continue; }
                var stride = kind == 0 ? 4 : kind == 1 ? 16 : kind == 2 ? 64 : 36; var length = checked(elements * stride);
                if (offset > values.Length - length) throw Error("native property value exceeds buffer");
                if (kind < 3) row.Values = M.Slice(values, offset, length);
                else {
                    row.Textures = new TextureValue[elements];
                    for (var element = 0; element < elements; element++) {
                        var at = offset + element * stride;
                        var textureKey = ResidentNativeAssetReference.BindTexture(M.I32(values, at),bind);
                        if (textureKey == ResidentNativeAssetReference.Missing) gaps.Add("unbound-native-texture:" + row.Name + ":" + element);
                        row.Textures[element] = new TextureValue { Binding = textureKey, AssignedDimension = M.I32(values, at + 4),
                            RequiredDimension = M.I32(values, at + 8), VrUsage = M.I32(values, at + 12), BindMs = M.U32(values, at + 16),
                            DefaultName = Name(nameReader, M.I32(values, at + 20), true), ScaleOffset = VectorName(M.I32(values, at + 24)),
                            TexelSize = VectorName(M.I32(values, at + 28)), HdrDecode = VectorName(M.I32(values, at + 32)) };
                    }
                }
                properties.Add(row);
            }
            if (!M.Read(pointer, 160).SequenceEqual(header) || !M.Read(M.Ptr(header, 48), nameBytes.Length).SequenceEqual(nameBytes)
                || !M.Read(M.Ptr(header, 80), descriptors.Length).SequenceEqual(descriptors) || !M.Read(M.Ptr(header, 112), values.Length).SequenceEqual(values))
                throw Error("native property sheet changed during capture");
            var result = new Data { AllowAddProperty = header[152] != 0, Properties = properties.OrderBy(row => row.Kind).ThenBy(row => row.Name, StringComparer.Ordinal).ToArray(),
                CoverageGaps = gaps.OrderBy(value => value, StringComparer.Ordinal).ToArray() };
            Validate(result); return result;
        }
        private static void Populate(IntPtr sheet, Data data, Func<string, UnityEngine.Object> resolve, Action admitted)
        {
            if (resolve == null) throw new ArgumentNullException(nameof(resolve));
            var layout = BuildLayout(data, name => { admitted(); return Shader.PropertyToID(name); }, binding => {
                admitted(); return ResidentNativeAssetReference.ResolveTexture(binding,resolve);
            });
            WriteArray(IntPtr.Add(sheet, 48), layout.NameIds.SelectMany(BitConverter.GetBytes).ToArray(), 4, 4, admitted);
            WriteArray(IntPtr.Add(sheet, 80), layout.Descriptors.SelectMany(BitConverter.GetBytes).ToArray(), 4, 4, admitted);
            WriteArray(IntPtr.Add(sheet, 112), layout.Values, 1, 16, admitted);
            M.Write(IntPtr.Add(sheet, 16), layout.Starts.SelectMany(BitConverter.GetBytes).ToArray());
            M.Write(IntPtr.Add(sheet, 152), new[] { data.AllowAddProperty ? (byte)1 : (byte)0 });
            var engine = LoadingEngineClockProbe.RequirePinnedEngine();
            ((UnarySheet)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine.BaseAddress, 0x799f10), typeof(UnarySheet)))(sheet);
            admitted();
        }
        internal static NativeLayout BuildLayout(Data data, Func<string, int> propertyId, Func<string, int> textureId)
        {
            RequireCompleteCoverage(data);
            if (propertyId == null || textureId == null) throw new ArgumentNullException();
            var properties = data.Properties; var starts = new int[7]; var names = new int[properties.Length]; var descriptors = new uint[properties.Length];
            var vectors = properties.Where(row => row.Kind == 1).Select((row, index) => new { row.Name, index }).ToDictionary(row => row.Name, row => row.index, StringComparer.Ordinal);
            var intern = new Dictionary<string, int>(StringComparer.Ordinal);
            var idNames = new Dictionary<int, string>();
            int Id(string name) {
                if (name.Length == 0) return -1;
                if (!intern.TryGetValue(name, out var id)) {
                    id = propertyId(name);
                    if (id == -1 || idNames.TryGetValue(id, out var other) && other != name) throw Error("property name interning collapsed distinct identities");
                    intern.Add(name, id); idNames[id] = name;
                }
                return id;
            }
            int Auxiliary(string name) => name.Length == 0 ? -1 : vectors[name];
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            var at = 0;
            for (var kind = 0; kind < 6; kind++) {
                starts[kind] = at;
                while (at < properties.Length && properties[at].Kind == kind) {
                    var property = properties[at]; names[at] = Id(property.Name);
                    if (stream.Length > 0xfffff) throw Error("rebuilt native property buffer exceeds descriptor offset bounds");
                    descriptors[at] = checked((uint)stream.Length) | checked((uint)property.Elements << 20) | property.Flags << 30;
                    if (kind < 3) writer.Write(property.Values);
                    else if (kind == 3) foreach (var texture in property.Textures) {
                        var resource = 0;
                        if (texture.Binding.Length != 0) {
                            resource = textureId(texture.Binding);
                            if (resource == 0) throw Error("bound texture has no renderer resource identity");
                        }
                        writer.Write(resource); writer.Write(texture.AssignedDimension); writer.Write(texture.RequiredDimension); writer.Write(texture.VrUsage);
                        writer.Write(texture.BindMs); writer.Write(Id(texture.DefaultName)); writer.Write(Auxiliary(texture.ScaleOffset));
                        writer.Write(Auxiliary(texture.TexelSize)); writer.Write(Auxiliary(texture.HdrDecode));
                    } else throw Error("unresolved buffer resource reached property rebuilding");
                    at++;
                }
            }
            starts[6] = at; writer.Flush(); var valueBytes = stream.ToArray();
            return new NativeLayout { Starts = starts, NameIds = names, Descriptors = descriptors, Values = valueBytes };
        }
        private static void WriteArray(IntPtr header, byte[] values, int stride, int alignment, Action admitted)
        {
            if (values.Length % stride != 0) throw Error("property array stride differs");
            var length = values.Length / stride; M.Reserve(header, length, stride, alignment, admitted);
            M.Write(M.Ptr(header), values); M.Write(IntPtr.Add(header, 16), BitConverter.GetBytes((ulong)length));
        }
        private static int Count(byte[] bytes, int offset, int maximum)
        { var value = M.U64(bytes, offset); return value <= (ulong)maximum ? (int)value : throw Error("property count exceeds bounds"); }
        private static string Name(PropertyName get, int id, bool optional)
        {
            if (id == -1) return optional ? "" : throw Error("property name identity is absent");
            var pointer = get(ref id); var bytes = new List<byte>();
            for (var index = 0; index < 16384; index++) {
                var value = M.Read(IntPtr.Add(pointer, index), 1)[0]; if (value == 0) {
                    var name = new UTF8Encoding(false, true).GetString(bytes.ToArray());
                    if (name.Length == 0) throw Error("native property has an empty name"); return name;
                }
                bytes.Add(value);
            }
            throw Error("native property name exceeds bounds");
        }
        internal static void Validate(Data data)
        {
            if (data == null || data.Version != 1 || data.Properties == null || data.Properties.Length > MaximumProperties || data.CoverageGaps == null
                || data.CoverageGaps.Length > MaximumProperties || data.CoverageGaps.Any(value => string.IsNullOrEmpty(value) || value.Length > 32768))
                throw Error("property snapshot shape differs");
            var keys = new HashSet<string>(StringComparer.Ordinal); var vectors = new HashSet<string>(data.Properties.Where(row => row != null && row.Kind == 1).Select(row => row.Name), StringComparer.Ordinal);
            var bytes = 0; var previousKind = -1; var previousName = "";
            foreach (var row in data.Properties) {
                if (row == null || string.IsNullOrEmpty(row.Name) || row.Name.Length > 16384 || row.Name.IndexOf('\0') >= 0 || row.Kind < 0 || row.Kind >= 6 || row.Elements < 1 || row.Elements > 1023
                    || row.Flags > 3 || row.Values == null || row.Textures == null || !keys.Add(row.Kind + ":" + row.Name)
                    || row.Kind < previousKind || row.Kind == previousKind && StringComparer.Ordinal.Compare(previousName, row.Name) >= 0) throw Error("property definition differs");
                previousKind = row.Kind; previousName = row.Name;
                if (row.Kind < 3) {
                    var stride = row.Kind == 0 ? 4 : row.Kind == 1 ? 16 : 64;
                    if (row.Values.Length != row.Elements * stride || row.Textures.Length != 0) throw Error("property scalar or array value differs");
                    bytes = checked(bytes + row.Values.Length);
                } else if (row.Kind == 3) {
                    if (row.Values.Length != 0 || row.Textures.Length != row.Elements) throw Error("texture property shape differs");
                    foreach (var value in row.Textures) {
                        if (value == null || new[] { value.Binding, value.DefaultName, value.ScaleOffset, value.TexelSize, value.HdrDecode }.Any(text => text == null || text.Length > 16384)
                            || new[] { value.ScaleOffset, value.TexelSize, value.HdrDecode }.Any(text => text.Length != 0 && !vectors.Contains(text)))
                            throw Error("texture binding or auxiliary-vector alias differs");
                    }
                    bytes = checked(bytes + row.Elements * 36);
                } else if (row.Values.Length != 0 || row.Textures.Length != 0) throw Error("unresolved native buffer has invented values");
            }
            if (bytes > MaximumValues) throw Error("property payload exceeds bounds");
        }
        internal static void RequireCompleteCoverage(Data data)
        {
            Validate(data);
            if (data.CoverageGaps.Length != 0 || data.Properties.Any(row => row.Kind >= 4 || row.Textures.Any(t => t.Binding == ResidentNativeAssetReference.Missing)))
                throw Error("property-sheet native resources are not covered");
        }
        internal static void RequireEqual(Data expected, Data actual)
        {
            Validate(expected); Validate(actual);
            if (Newtonsoft.Json.JsonConvert.SerializeObject(expected) != Newtonsoft.Json.JsonConvert.SerializeObject(actual)) throw Error("independent property-sheet readback differs");
        }
        internal static Data Copy(Data data)
        {
            Validate(data); return Newtonsoft.Json.JsonConvert.DeserializeObject<Data>(Newtonsoft.Json.JsonConvert.SerializeObject(data))
                ?? throw Error("property snapshot copy is absent");
        }
        private static InvalidDataException Error(string reason) => new InvalidDataException("Resident shader properties: " + reason);
    }
}
