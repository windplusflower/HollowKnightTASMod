using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using Wrappers = HollowKnightTAS.Runtime.FullRun.ResidentManagedNativeWrappers;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentParticleSystemState
    {
        // Slots are in stable directory, startDelay, module-field and min/max
        // order. Zero is null; a positive ID is the one-based first-use ordinal.
        // MinMaxCurve::~MinMaxCurve (1affd0) and MinMaxGradient::~MinMaxGradient
        // (1a0b70) free BOTH non-null slots independently. Keep multiply owned
        // nodes in observations but never mutate or release an ambiguous owner.
        internal sealed class ValueNode
        {
            public Wrappers.CurveData? Curve;
            public byte[] Optimized = Array.Empty<byte>(), Padding = Array.Empty<byte>(), Gradient = Array.Empty<byte>();
        }
        private sealed partial class Reader
        {
            internal readonly List<ValueNode> Values = new List<ValueNode>();
            private readonly Dictionary<IntPtr, int> valueIds = new Dictionary<IntPtr, int>();
            internal int Value(byte[] owner, int offset, bool gradient)
            {
                var pointer = M.Ptr(owner, offset);
                if (pointer == IntPtr.Zero) return 0;
                if (valueIds.TryGetValue(pointer, out var previous)) {
                    if ((Values[previous - 1].Curve == null) != gradient) throw Error("one slot aliases a differently typed curve/gradient");
                    return previous;
                }
                if (Values.Count >= 100000) throw Error("too many module value nodes");
                var node = new ValueNode();
                if (gradient) node.Gradient = Read(pointer, 168);
                else {
                    var bytes = Read(pointer, 160);
                    var capacity = Bounded(M.U64(bytes, 80) >> 1);
                    Read(M.Ptr(bytes, 56), checked(capacity * 28));
                    node.Curve = Wrappers.CaptureCurveNative(pointer);
                    node.Optimized = M.Slice(bytes, 112, 40);
                    node.Padding = M.Slice(bytes, 104, 8).Concat(M.Slice(bytes, 152, 8)).ToArray();
                }
                Values.Add(node); valueIds.Add(pointer, Values.Count);
                return Values.Count;
            }
        }
        private const string FlagStorageCanon = "module-minmax-flag-padding-requires-common-canon";
        private static int PackedOffset(int offset, int[] ranges)
        {
            var packed = 0;
            for (var i = 0; i < ranges.Length; i += 2) {
                if (offset >= ranges[i] && offset < ranges[i] + ranges[i + 1]) return packed + offset - ranges[i];
                packed += ranges[i + 1];
            }
            throw Error("field is absent from its fixed packed layout");
        }
        private static class FlagLayout
        {
            internal static readonly int Delay = PackedOffset(14, ReadOnlyRanges);
            internal static readonly int[] Modules = ModuleFlagWords.Select(offset => PackedOffset(offset, ModuleScalarRanges)).ToArray();
            internal static readonly int[] Booleans = ModuleBooleans.Select(offset => PackedOffset(offset, ModuleScalarRanges)).ToArray();
        }
        private static bool HasReservedBits(byte[] bytes, int i) => (bytes[i] & 0xfe) != 0 || bytes[i + 1] != 0;
        private static void NormalizeFlag(byte[] bytes, int i) { bytes[i] &= 1; bytes[i + 1] = 0; }
        private const string ValueStorageCanon = "curve-gradient-key-storage-and-padding-requires-common-canon";
        private static void ValueGaps(Data data, SortedSet<string> gaps)
        {
            var used = new HashSet<int>();
            foreach (var row in data.Rows) {
                var body = row.Simulation;
                if (body == null) continue;
                if (HasReservedBits(body.ReadOnly, FlagLayout.Delay) || FlagLayout.Modules.Any(offset => HasReservedBits(body.Module, offset))) gaps.Add(FlagStorageCanon);
                foreach (var id in body.Curves.Concat(body.Gradients)) if (id != 0 && !used.Add(id)) gaps.Add("multiply-owned-curve-or-gradient-allocation");
            }
            foreach (var node in data.Values) {
                var curve = node.Curve;
                if (curve == null) {
                    if (node.Gradient[162] != 0 || node.Gradient[163] != 0) gaps.Add(ValueStorageCanon);
                } else {
                    if (curve.Capacity != 0 && curve.KeyStorageFlags != 0) gaps.Add("borrowed-curve-key-allocation");
                    if (curve.Capacity != curve.KeyCount || curve.KeyStorageFlags != (curve.KeyCount == 0 ? 1 : 0)
                        || curve.StoragePadding.Any(value => value != 0) || node.Padding.Any(value => value != 0)) gaps.Add(ValueStorageCanon);
                }
            }
        }
        private static void ValidateValues(Data data)
        {
            var seen = new HashSet<int>();
            foreach (var row in data.Rows) {
                var body = row.Simulation;
                if (body == null) continue;
                void Slots(int[] ids, bool gradient) {
                    foreach (var id in ids) {
                        if (id == 0) continue;
                        if (id < 1 || id > data.Values.Length || data.Values[id - 1] == null || (data.Values[id - 1].Curve == null) != gradient)
                            throw Error("value owner, ID or kind differs");
                        if (seen.Add(id) && id != seen.Count) throw Error("module values are not in first-use order");
                    }
                }
                Slots(body.Curves, false); Slots(body.Gradients, true);
            }
            if (seen.Count != data.Values.Length) throw Error("unreferenced module value node");
            foreach (var node in data.Values) {
                if (node.Optimized == null || node.Padding == null || node.Gradient == null) throw Error("module value storage is absent");
                if (node.Curve == null) {
                    if (node.Gradient.Length != 168 || node.Optimized.Length != 0 || node.Padding.Length != 0
                        || node.Gradient[160] > 8 || node.Gradient[161] > 8 || BitConverter.ToInt32(node.Gradient, 164) < 0 || BitConverter.ToInt32(node.Gradient, 164) > 1)
                        throw Error("module gradient keys/mode or native layout differs");
                } else {
                    Wrappers.ValidateCurve(node.Curve);
                    if (node.Optimized.Length != 40 || node.Padding.Length != 16 || node.Gradient.Length != 0) throw Error("module curve polynomial layout differs");
                }
            }
        }
        internal static void RequireCanonicalizableCoverage(Data data)
        {
            Validate(data); RequireLivePublication(data);
            var gaps = RequiredCoverageGaps(data).Where(value => value != ValueStorageCanon && value != FlagStorageCanon).ToArray();
            if (gaps.Length != 0) throw Error("native coverage is incomplete: " + string.Join(", ", gaps));
            foreach (var node in data.Values) if (node.Curve != null) Wrappers.RequireCanonicalizableCurveStorage(node.Curve);
        }
        private static Data NormalizeValues(Data source)
        {
            var result = JsonConvert.DeserializeObject<Data>(JsonConvert.SerializeObject(source)) ?? throw Error("canonical copy failed");
            foreach (var node in result.Values) {
                if (node.Curve == null) Array.Clear(node.Gradient, 162, 2);
                else { Wrappers.NormalizeCurveStorage(node.Curve); Array.Clear(node.Padding, 0, node.Padding.Length); }
            }
            foreach (var row in result.Rows) {
                var body = row.Simulation ?? throw Error("missing simulation flags");
                NormalizeFlag(body.ReadOnly, FlagLayout.Delay);
                foreach (var offset in FlagLayout.Modules) NormalizeFlag(body.Module, offset);
            }
            result.CoverageGaps = RequiredCoverageGaps(result); RequireCompleteCoverage(result); return result;
        }
        private static void CanonValues(Data data, ResidentObjectDirectory directory, Action admitted)
        {
            foreach (var row in data.Rows) {
                var body = row.Simulation ?? throw Error("cannot canonicalize missing simulation");
                var owner = (UnityEngine.ParticleSystem)directory.Resolve(row.Owner);
                var header = M.Read(M.Owner(owner), 152);
                // For all slots in one emitter read the native headers once.
                var ro = M.Read(M.Ptr(header, 64), 96); var modules = M.Read(M.Ptr(header, 80), 5248);
                void Flag(byte[] bytes, IntPtr address, int offset) {
                    if (!HasReservedBits(bytes, offset)) return;
                    admitted(); M.Write(IntPtr.Add(address, offset), new byte[] { (byte)(bytes[offset] & 1), 0 });
                }
                Flag(ro, M.Ptr(header, 64), 14);
                foreach (var offset in ModuleFlagWords) Flag(modules, M.Ptr(header, 80), offset);
                void Value(byte[] bytes, int offset, int id) {
                    if (id == 0) return;
                    admitted();
                    var pointer = M.Ptr(bytes, offset); var node = data.Values[id - 1];
                    if (node.Curve == null) {
                        if (node.Gradient[162] != 0 || node.Gradient[163] != 0) M.Write(IntPtr.Add(pointer, 162), new byte[2]);
                    } else {
                        var curve = node.Curve;
                        if (curve.Capacity != curve.KeyCount || curve.KeyStorageFlags != (curve.KeyCount == 0 ? 1 : 0) || curve.StoragePadding.Any(value => value != 0))
                            Wrappers.CanonicalizeCurveNative(pointer, admitted);
                        if (node.Padding.Any(value => value != 0)) {
                            M.Write(IntPtr.Add(pointer, 104), new byte[8]); M.Write(IntPtr.Add(pointer, 152), new byte[8]);
                        }
                    }
                }
                Value(ro, 24, body.Curves[0]); Value(ro, 32, body.Curves[1]);
                for (var i = 0; i < ModuleCurves.Length; i++) Value(modules, ModuleCurves[i], body.Curves[i + 2]);
                for (var i = 0; i < ModuleGradients.Length; i++) Value(modules, ModuleGradients[i], body.Gradients[i]);
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr WritableValue(IntPtr minMax);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReleasePair(IntPtr minMax);
        private static T Entry<T>(int rva, string prefix) where T : class
        {
            var address = IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, rva);
            if (BitConverter.ToString(M.Read(address, prefix.Length / 2)).Replace("-", "").ToLowerInvariant() != prefix)
                throw Error("curve/gradient ownership ABI differs");
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }
        private static void ApplyValues(Data source, Row row, Data previous, Row targetRow, IntPtr system, Action admitted)
        {
            var body = row.Simulation ?? throw Error("missing simulation for value restore");
            var before = targetRow.Simulation ?? throw Error("target module value graph is absent");
            var header = M.Read(system, 152);
            var ro = M.Ptr(header, 64); var modules = M.Ptr(header, 80);
            var moduleBytes = M.Read(modules, 5248);
            Pair(IntPtr.Add(ro, 8), M.Read(ro, 96), 24, false, body.Curves[0], body.Curves[1], before.Curves[0], before.Curves[1]);
            for (var i = 0; i < ModuleCurves.Length; i += 2)
                Pair(IntPtr.Add(modules, ModuleCurves[i] - 16), moduleBytes, ModuleCurves[i], false, body.Curves[i + 2], body.Curves[i + 3], before.Curves[i + 2], before.Curves[i + 3]);
            for (var i = 0; i < ModuleGradients.Length; i += 2)
                Pair(IntPtr.Add(modules, ModuleGradients[i] - 8), moduleBytes, ModuleGradients[i], true, body.Gradients[i], body.Gradients[i + 1], before.Gradients[i], before.Gradients[i + 1]);

            void Pair(IntPtr target, byte[] native, int firstSlot, bool gradient, int min, int max, int oldMin, int oldMax) {
                var currentMin = M.Ptr(native, firstSlot); var currentMax = M.Ptr(native, firstSlot + 8);
                if (min == 0 && max == 0 && currentMin == IntPtr.Zero && currentMax == IntPtr.Zero) return;
                admitted(); var start = gradient ? 8 : 16;
                if (min == 0 && currentMin != IntPtr.Zero || max == 0 && currentMax != IntPtr.Zero)
                    RetireSlots(target, gradient, min == 0, max == 0, admitted);
                Set(min, oldMin, currentMin, false); Set(max, oldMax, currentMax, true);
                void Set(int id, int previousId, IntPtr pointer, bool maximum) {
                    if (id == 0) return;
                    if (previousId != 0 && JsonConvert.SerializeObject(source.Values[id - 1]) == JsonConvert.SerializeObject(previous.Values[previousId - 1])) return;
                    admitted();
                    if (pointer == IntPtr.Zero) {
                        WritableValue allocate;
                        if (gradient) allocate = maximum ? Entry<WritableValue>(0x1a49e0, "40534883ec304883791000488bd97542") : Entry<WritableValue>(0x1a4a40, "40534883ec304883790800488bd97542");
                        else allocate = maximum ? Entry<WritableValue>(0x1b8950, "40534883ec304883791800488bd90f859100") : Entry<WritableValue>(0x1b8a00, "40534883ec304883791000488bd90f859100");
                        pointer = allocate(target);
                    }
                    if (pointer == IntPtr.Zero || pointer != M.Ptr(IntPtr.Add(target, start + (maximum ? 8 : 0)))) throw Error("native module value allocation did not bind to its owner");
                    var value = source.Values[id - 1];
                    if (gradient) M.Write(pointer, value.Gradient);
                    else {
                        Wrappers.ApplyCurveNative(value.Curve!, pointer, admitted);
                        M.Write(IntPtr.Add(pointer, 104), new byte[8]); M.Write(IntPtr.Add(pointer, 112), value.Optimized); M.Write(IntPtr.Add(pointer, 152), new byte[8]);
                    }
                }
            }
        }
        private static void RetireSlots(IntPtr target, bool gradient, bool removeMin, bool removeMax, Action admitted)
        {
            var width = gradient ? 64 : 32; var offset = gradient ? 8 : 16;
            var release = gradient ? Entry<ReleasePair>(0x1a0b70, "48895c24084889742410574883ec20488b7108")
                : Entry<ReleasePair>(0x1affd0, "48895c24084889742410574883ec20488b7110");
            // Move only retired allocations into a local copy of the actual
            // target owner. Its native destructor uses the correct target label.
            // No managed allocation frees Unity pointers.
            var copy = M.Read(target, width); var kept = M.Slice(copy, offset, 16);
            if (removeMin) Array.Clear(kept, 0, 8); else Array.Clear(copy, offset, 8);
            if (removeMax) Array.Clear(kept, 8, 8); else Array.Clear(copy, offset + 8, 8);
            var temporary = Marshal.AllocHGlobal(width);
            try {
                Marshal.Copy(copy, 0, temporary, width); admitted();
                M.Write(IntPtr.Add(target, offset), kept);
                release(temporary); admitted();
            } finally { Marshal.FreeHGlobal(temporary); }
        }
    }
}
