using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentManagedNativeWrappers
    {
        private const int MaximumSheetStorage = 1024 * 1024;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ReadPropertyName(ref int name);

        // Native IDs and cached hashes are process-local dependencies. Names
        // retain identity; the two hash witnesses below come from an independent
        // XXH32 of the actual native arrays, never from a serialized assertion.
        internal sealed class SheetStorageData
        {
            public int MemoryLabel = 0x39;
            public ArrayStorage[] Arrays = { new ArrayStorage(), new ArrayStorage(), new ArrayStorage() };
            public byte[] Padding = new byte[11];
            public int[] TypeStarts = new int[7];
            public string[] Names = Array.Empty<string>();
            public uint[] Descriptors = Array.Empty<uint>();
            public bool PackingIsCanonical = true, HashIsCurrent = true, LayoutHashIsCurrent = true;
        }
        internal sealed class ArrayStorage
        {
            public int MemoryLabel = 0x39, Count, Capacity, Flags;
            public bool Allocated;
            public byte[] Padding = new byte[4], UnusedBytes = Array.Empty<byte>();
        }

        private static SheetStorageData ReadSheetStorage(ResidentShaderPropertySheet.Data sheet, byte[] header,
            byte[] names, byte[] descriptors, byte[] values)
        {
            var result = new SheetStorageData { MemoryLabel = M.I32(header, 8),
                Padding = M.Slice(header, 44, 4).Concat(M.Slice(header, 153, 7)).ToArray(),
                TypeStarts = Enumerable.Range(0, 7).Select(index => M.I32(header, 16 + index * 4)).ToArray() };
            var bytes = new[] { names, descriptors, values };
            for (var index = 0; index < 3; index++) {
                var at = 48 + index * 32; var count = checked((int)M.U64(header, at + 16)); var capacity = checked((int)(M.U64(header, at + 24) >> 1));
                var stride = index == 2 ? 1 : 4;
                result.Arrays[index] = new ArrayStorage { MemoryLabel = M.I32(header, at + 8), Count = count, Capacity = capacity,
                    Flags = (int)(M.U64(header, at + 24) & 1), Allocated = M.Ptr(header, at) != IntPtr.Zero,
                    Padding = M.Slice(header, at + 12, 4), UnusedBytes = M.Slice(bytes[index], count * stride, (capacity - count) * stride) };
            }
            var nameCount = result.Arrays[0].Count; var namesUsed = M.Slice(names, 0, nameCount * 4);
            var descriptorsUsed = M.Slice(descriptors, 0, result.Arrays[1].Count * 4); var valuesUsed = M.Slice(values, 0, result.Arrays[2].Count);
            var reader = Function<ReadPropertyName>(0x7d59e0, "48895c2408574883ec20");
            var nameIds = new Dictionary<string, int>(StringComparer.Ordinal);
            var resources = new Dictionary<string, int>(StringComparer.Ordinal);
            var actualNames = new string[nameCount];
            for (var index = 0; index < nameCount; index++) {
                var id = M.I32(namesUsed, index * 4); actualNames[index] = PropertyName(reader, id); AddIdentity(nameIds, actualNames[index], id);
            }
            result.Names = actualNames; result.Descriptors = Enumerable.Range(0, nameCount).Select(index => M.U32(descriptorsUsed, index * 4)).ToArray();
            var layoutHash = nameCount == 0 ? 0U : SheetHash(descriptorsUsed, SheetHash(namesUsed, 0x8f37154b));
            result.LayoutHashIsCurrent = M.U32(header, 148) == layoutHash;
            result.HashIsCurrent = M.U32(header, 144) == (nameCount == 0 ? 0U : SheetHash(valuesUsed, layoutHash));
            result.PackingIsCanonical = false;
            if (sheet.CoverageGaps.Length == 0) {
                var byName = sheet.Properties.ToDictionary(row => row.Kind + ":" + row.Name, StringComparer.Ordinal);
                for (var index = result.TypeStarts[3]; index < result.TypeStarts[4]; index++) {
                    var property = byName["3:" + actualNames[index]]; var offset = (int)(result.Descriptors[index] & 0xfffff);
                    for (var element = 0; element < property.Textures.Length; element++) {
                        var value = property.Textures[element]; var at = offset + element * 36;
                        var resource = M.I32(valuesUsed, at); var defaultId = M.I32(valuesUsed, at + 20);
                        if (value.Binding.Length != 0) AddIdentity(resources, value.Binding, resource);
                        else if (resource != 0) throw Error("unbound texture resource is nonzero");
                        if (value.DefaultName.Length != 0) AddIdentity(nameIds, value.DefaultName, defaultId);
                        else if (defaultId != -1) throw Error("unbound texture default-name identity differs");
                    }
                }
                var expected = ResidentShaderPropertySheet.BuildLayout(sheet,
                    name => nameIds.TryGetValue(name, out var id) ? id : throw Error("property packing has an unresolved native name"),
                    binding => resources.TryGetValue(binding, out var id) ? id : throw Error("property packing has an unresolved texture resource"));
                result.PackingIsCanonical = result.TypeStarts.SequenceEqual(expected.Starts)
                    && namesUsed.SequenceEqual(expected.NameIds.SelectMany(BitConverter.GetBytes))
                    && result.Descriptors.SequenceEqual(expected.Descriptors) && valuesUsed.SequenceEqual(expected.Values);
            }
            ValidateSheetStorage(sheet, result); return result;
        }
        private static void AddIdentity(Dictionary<string, int> map, string key, int value)
        {
            if (map.TryGetValue(key, out var old) && old != value) throw Error("one property identity has multiple native IDs"); map[key] = value;
        }
        private static string PropertyName(ReadPropertyName reader, int id)
        {
            if (id == -1) throw Error("native property name is missing"); var pointer = reader(ref id);
            if (pointer == IntPtr.Zero) throw Error("native property name storage is null");
            var bytes = new List<byte>();
            for (var index = 0; index < 16384; index++) {
                var value = M.Read(IntPtr.Add(pointer, index), 1)[0];
                if (value == 0) return bytes.Count != 0 ? new UTF8Encoding(false, true).GetString(bytes.ToArray()) : throw Error("native property name is empty");
                bytes.Add(value);
            }
            throw Error("native property name exceeds its bound");
        }
        private static IEnumerable<string> SheetStorageGaps(SheetStorageData data, string key)
        {
            for (var index = 0; index < data.Arrays.Length; index++)
                if (data.Arrays[index].Flags != 0 && data.Arrays[index].Capacity != 0) yield return "property-sheet-borrowed-storage:" + key + "/" + index;
            var ranges = new List<Tuple<int, int>>();
            for (var kind = 0; kind < 4; kind++) for (var index = data.TypeStarts[kind]; index < data.TypeStarts[kind + 1]; index++) {
                var descriptor = data.Descriptors[index]; var offset = (int)(descriptor & 0xfffff);
                var size = (int)((descriptor >> 20) & 0x3ff) * (kind == 0 ? 4 : kind == 1 ? 16 : kind == 2 ? 64 : 36);
                ranges.Add(Tuple.Create(offset, offset + size));
            }
            var previousEnd = 0;
            foreach (var range in ranges.OrderBy(range => range.Item1)) {
                if (range.Item1 < previousEnd) { yield return "property-sheet-overlapping-value-storage:" + key; break; }
                previousEnd = range.Item2;
            }
        }
        private static ResidentShaderPropertySheet.NativeLayout PortableSheetLayout(ResidentShaderPropertySheet.Data sheet)
        {
            var names = new Dictionary<string, int>(StringComparer.Ordinal); var resources = new Dictionary<string, int>(StringComparer.Ordinal);
            int Identity(Dictionary<string, int> map, string value) { if (!map.TryGetValue(value, out var id)) { id = map.Count + 1; map.Add(value, id); } return id; }
            return ResidentShaderPropertySheet.BuildLayout(sheet, name => Identity(names, name), binding => Identity(resources, binding));
        }
        private static void NormalizeSheetStorage(ResidentShaderPropertySheet.Data sheet, SheetStorageData data)
        {
            var layout = PortableSheetLayout(sheet); var counts = new[] { sheet.Properties.Length, sheet.Properties.Length, layout.Values.Length };
            data.MemoryLabel = 0x39; data.Padding = new byte[11]; data.TypeStarts = layout.Starts;
            data.Names = sheet.Properties.Select(property => property.Name).ToArray(); data.Descriptors = layout.Descriptors;
            data.PackingIsCanonical = data.HashIsCurrent = data.LayoutHashIsCurrent = true;
            data.Arrays = counts.Select(count => new ArrayStorage { Count = count, Capacity = count, Allocated = count != 0 }).ToArray();
        }
        private static void CanonicalizeSheetStorage(IntPtr pointer, Action admitted)
        {
            for (var index = 0; index < 3; index++) {
                admitted(); var storage = IntPtr.Add(pointer, 48 + index * 32); var header = M.Read(storage, 32);
                if (M.I32(header, 8) != 0x39) throw Error("new property sheet has an unexpected allocator category");
                M.Reserve(storage, checked((int)M.U64(header, 16)), index == 2 ? 1 : 4, index == 2 ? 16 : 4, admitted);
                M.Write(IntPtr.Add(storage, 12), new byte[4]);
            }
            M.Write(IntPtr.Add(pointer, 44), new byte[4]); M.Write(IntPtr.Add(pointer, 153), new byte[7]); admitted();
        }
        private static void ValidateSheetStorage(ResidentShaderPropertySheet.Data sheet, SheetStorageData data)
        {
            if (data == null || data.Arrays == null || data.Arrays.Length != 3 || data.Padding == null || data.Padding.Length != 11
                || data.TypeStarts == null || data.TypeStarts.Length != 7 || data.Names == null || data.Descriptors == null) throw Error("property-sheet storage shape differs");
            for (var index = 0; index < 3; index++) {
                var array = data.Arrays[index]; var stride = index == 2 ? 1 : 4;
                if (array == null || array.Count < 0 || array.Capacity < array.Count || array.Capacity > MaximumSheetStorage || array.Flags < 0 || array.Flags > 1
                    || array.Capacity != 0 && !array.Allocated || array.Padding == null || array.Padding.Length != 4 || array.UnusedBytes == null
                    || array.UnusedBytes.Length != (array.Capacity - array.Count) * stride) throw Error("property-sheet native array storage differs");
            }
            var count = sheet.Properties.Length;
            if (data.Arrays[0].Count != count || data.Arrays[1].Count != count || data.Names.Length != count || data.Descriptors.Length != count
                || data.TypeStarts[0] != 0 || data.TypeStarts[6] != count || data.TypeStarts.Any(value => value < 0 || value > count)
                || !data.TypeStarts.SequenceEqual(data.TypeStarts.OrderBy(value => value))) throw Error("property-sheet native type ranges differ");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var byName = sheet.Properties.ToDictionary(row => row.Kind + ":" + row.Name, StringComparer.Ordinal);
            for (var kind = 0; kind < 6; kind++) for (var index = data.TypeStarts[kind]; index < data.TypeStarts[kind + 1]; index++) {
                var name = data.Names[index];
                if (string.IsNullOrEmpty(name) || name.Length > 16384 || !seen.Add(kind + ":" + name) || !byName.TryGetValue(kind + ":" + name, out var property)) throw Error("property-sheet physical names differ from the semantic values");
                var descriptor = data.Descriptors[index]; var countInDescriptor = (int)((descriptor >> 20) & 0x3ff);
                var stride = kind == 0 ? 4 : kind == 1 ? 16 : kind == 2 ? 64 : kind == 3 ? 36 : 8;
                if (countInDescriptor != property.Elements || descriptor >> 30 != property.Flags
                    || (long)(descriptor & 0xfffff) + (kind < 4 ? (long)countInDescriptor * stride : 0) > data.Arrays[2].Count)
                    throw Error("property-sheet physical descriptor exceeds its semantic value range");
            }
        }
        private static void RequireCanonicalSheetStorage(ResidentShaderPropertySheet.Data sheet, SheetStorageData data)
        {
            ValidateSheetStorage(sheet, data); var expected = JsonConvert.DeserializeObject<SheetStorageData>(JsonConvert.SerializeObject(data))!;
            NormalizeSheetStorage(sheet, expected);
            if (JsonConvert.SerializeObject(expected) != JsonConvert.SerializeObject(data)) throw Error("property-sheet native storage or independently read dependencies are not canonical");
        }

        // XXH32 and XXH32_finalize, RVA edcc80/edcd60. ComputeHash at 799f10
        // hashes names(seed 8f37154b), then descriptors, then values; the first
        // two hashes form layoutHash. Empty property sets force both to zero.
        internal static uint SheetHash(byte[] bytes, uint seed)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            unchecked {
                const uint p1 = 0x9e3779b1, p2 = 0x85ebca77, p3 = 0xc2b2ae3d, p4 = 0x27d4eb2f, p5 = 0x165667b1;
                uint Rotate(uint value, int count) => value << count | value >> (32 - count);
                uint Round(uint value, uint word) => Rotate(value + word * p2, 13) * p1;
                uint hash; var index = 0;
                if (bytes.Length >= 16) {
                    var first = seed + p1 + p2; var second = seed + p2; var third = seed; var fourth = seed - p1;
                    do {
                        first = Round(first, M.U32(bytes, index)); second = Round(second, M.U32(bytes, index + 4));
                        third = Round(third, M.U32(bytes, index + 8)); fourth = Round(fourth, M.U32(bytes, index + 12)); index += 16;
                    } while (index <= bytes.Length - 16);
                    hash = Rotate(first, 1) + Rotate(second, 7) + Rotate(third, 12) + Rotate(fourth, 18);
                } else hash = seed + p5;
                hash += (uint)bytes.Length;
                while (index <= bytes.Length - 4) { hash = Rotate(hash + M.U32(bytes, index) * p3, 17) * p4; index += 4; }
                while (index < bytes.Length) { hash = Rotate(hash + bytes[index++] * p5, 11) * p1; }
                hash ^= hash >> 15; hash *= p2; hash ^= hash >> 13; hash *= p3; hash ^= hash >> 16; return hash;
            }
        }
    }
}
