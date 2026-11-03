using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentParticleSystemState
    {
        internal sealed class ElementBinding
        {
            public string Key = "";
            public bool Missing;
        }
        private enum ElementRole { Plain, Force, Collision, Sprite, Field, Plane, Collider, Emitter }
        private sealed partial class Reader
        {
            private readonly Dictionary<IntPtr, int> allocations = new Dictionary<IntPtr, int>();
            internal int Allocation(IntPtr pointer)
            {
                if (pointer == IntPtr.Zero) return 0;
                if (!allocations.TryGetValue(pointer, out var id)) allocations.Add(pointer, id = allocations.Count + 1);
                return id;
            }
            private readonly Dictionary<int,IntPtr> registered = new Dictionary<int,IntPtr>();
            private readonly List<Action> idChecks = new List<Action>();
            internal IntPtr Registered(int id)
            {
                if (id == 0) return IntPtr.Zero;
                if (!registered.TryGetValue(id,out var value)) {
                    value = ResidentLoadedAssetIndex.LookupPointer(id,out var recheck);
                    registered.Add(id,value); idChecks.Add(recheck);
                }
                return value;
            }
            internal ElementBinding Element(byte[] bytes, int at, ElementRole role, int member)
            {
                var pointer = role == ElementRole.Force ? M.Ptr(bytes, at) : IntPtr.Zero;
                var result = new ElementBinding();
                if (role == ElementRole.Force && pointer == IntPtr.Zero) return result;
                int id = role == ElementRole.Force ? M.I32(Read(IntPtr.Add(pointer, 8), 4), 0) : M.I32(bytes, at);
                if (id == 0 && role != ElementRole.Force) return result;
                var value = Registered(id);
                string? key = null;
                if (value != IntPtr.Zero && AllowedElement(role,member,value) && (pointer == IntPtr.Zero || value == pointer)) key = ResidentNativeAssetReference.BindPointer(value,typeof(NativeObject),bind);
                result.Key = key ?? ""; result.Missing = key == null; return result;
            }
        }
        private static bool AllowedElement(ElementRole role,int member,IntPtr value)
        {
            bool Is(Type type) => ResidentNativeAssetReference.IsFamily(value,type);
            switch (role) {
                case ElementRole.Force: return Is(typeof(Rigidbody)) || Is(typeof(Rigidbody2D));
                case ElementRole.Collision: return Is(typeof(Collider)) || Is(typeof(Collider2D)) || member == 1 && (Is(typeof(Rigidbody)) || Is(typeof(Rigidbody2D)));
                case ElementRole.Sprite: return Is(typeof(NativeObject)); // PPtr<Object>
                case ElementRole.Field: return Is(typeof(ParticleSystemForceField));
                case ElementRole.Plane: return Is(typeof(Transform));
                case ElementRole.Collider: return Is(typeof(Component));
                case ElementRole.Emitter: return ResidentNativeAssetReference.NativeClass(value,0x1949cb0) == 0x1949cb0;
                default: throw Error("array contains an unknown binding role");
            }
        }
        private static int Members(ElementRole role) => role == ElementRole.Plain ? 0 : role == ElementRole.Collision ? 2 : 1;
        private static int MemberOffset(ElementRole role, int member) => role == ElementRole.Force ? 24 : role == ElementRole.Collision ? 36 + 4 * member : 0;
        private static int MemberWidth(ElementRole role) => role == ElementRole.Force ? 8 : 4;
        private static IEnumerable<int> OwnedAllocations(Data data)
        {
            yield return data.Registry.ActiveStorage.Allocation; yield return data.Registry.StoppingStorage.Allocation;
            foreach (var row in data.Rows) {
                var body = row.Simulation;
                if (body == null) continue;
                foreach (var id in body.RootAllocations) yield return id;
                foreach (var array in body.Arrays) yield return array.Allocation;
                foreach (var bits in body.BitSets) yield return bits.Allocation;
            }
        }
        private static bool IsNumericStream(ArrayField field) => field.Part == 2 && (field.Offset < 1312 || field.Offset >= 1680);
        private static bool IsReplay(ArrayField field) => field.Part == 1 && field.Offset == 440;
        private static ArrayState CaptureField(Reader reader, byte[] bytes, ArrayField field)
        {
            var result = CaptureArray(reader, bytes, field.Offset);
            var contents = reader.Read(M.Ptr(bytes, field.Offset), checked(result.Count * field.Stride));
            if (IsNumericStream(field) && !Foreign(result) && result.Count < result.Capacity) {
                // ParticleSystemFloatArray/IntArray/Color32Array are owned
                // 16-aligned, 4-byte value lanes. Preserve all capacity lanes
                // including SIMD tails as D. No pointers, IDs or event records.
                var spare = checked((result.Capacity - result.Count) * field.Stride);
                result.SpareNumeric = reader.Read(IntPtr.Add(M.Ptr(bytes, field.Offset), contents.Length), spare);
            }
            var members = Members(field.Role);
            if (members == 0 && !IsReplay(field)) { result.Bytes = contents; return result; }
            result.Bytes = (byte[])contents.Clone(); // Preserve Reader's raw stability witness.
            if (members != 0 && result.Count != 0) result.Refs = new ElementBinding[checked(members * result.Count)];
            for (var i = 0; i < result.Count; i++) {
                if (IsReplay(field)) Array.Clear(result.Bytes, i * field.Stride + 20, 4);
                for (var member = 0; member < members; member++) {
                    var offset = i * field.Stride + MemberOffset(field.Role, member);
                    result.Refs![i * members + member] = reader.Element(contents, offset, field.Role, member);
                    Array.Clear(result.Bytes, offset, MemberWidth(field.Role));
                }
            }
            return result;
        }
        private static void ValidateField(ArrayField field, ArrayState value)
        {
            ValidateArray(value, field.Stride);
            var spare = IsNumericStream(field) && !Foreign(value) ? (long)(value.Capacity - value.Count) * field.Stride : 0;
            if (spare == 0 ? value.SpareNumeric != null : value.SpareNumeric == null || value.SpareNumeric.LongLength != spare)
                throw Error("owned spare SIMD lanes are absent or in a reference-bearing array");
            var members = Members(field.Role);
            var count = (long)value.Count * members;
            if (count == 0 && value.Refs != null || count != 0 && (value.Refs == null || value.Refs.LongLength != count)) throw Error("array reference directory differs");
            for (var i = 0; i < value.Count && (members != 0 || IsReplay(field)); i++) {
                var start = i * field.Stride;
                if (IsReplay(field) && BitConverter.ToInt32(value.Bytes, start + 20) != 0) throw Error("emission replay padding carries untyped bytes");
                for (var member = 0; member < members; member++) {
                    var binding = value.Refs![i * members + member];
                    if (binding == null || binding.Key == null || binding.Key.Length > 262144 || binding.Missing && binding.Key.Length != 0)
                        throw Error("array element binding differs");
                    var offset = start + MemberOffset(field.Role, member);
                    if (MemberWidth(field.Role) == 8 ? BitConverter.ToInt64(value.Bytes, offset) != 0 : BitConverter.ToInt32(value.Bytes, offset) != 0)
                        throw Error("array payload contains a native pointer or instance ID");
                }
            }
        }
        private static void CheckFieldBindings(ArrayState value, ArrayField field, Func<string,NativeObject> resolve)
        {
            if (value.Refs == null) return;
            var members = Members(field.Role);
            for (var i = 0; i < value.Refs.Length; i++) {
                var reference = value.Refs[i];
                if (reference.Missing || reference.Key.Length != 0 && !AllowedElement(field.Role, i % members, ResidentNativeAssetReference.ResolveTyped(reference.Key,typeof(NativeObject),resolve)))
                    throw Error("target array dependency has another type or is missing");
            }
        }
        private static bool SameArray(ArrayState first, ArrayState second)
        {
            if (first.Count != second.Count || first.Capacity != second.Capacity || first.Allocated != second.Allocated || first.External != second.External
                || !first.Bytes.SequenceEqual(second.Bytes) || (first.SpareNumeric == null) != (second.SpareNumeric == null)
                || first.SpareNumeric != null && !first.SpareNumeric.SequenceEqual(second.SpareNumeric!)
                || (first.Refs == null) != (second.Refs == null)) return false;
            if (first.Refs != null) for (var i = 0; i < first.Refs.Length; i++) if (first.Refs[i].Key != second.Refs![i].Key) return false;
            return true;
        }
        private static byte[] BindField(ArrayField field, ArrayState value, Func<string,NativeObject> resolve)
        {
            if (value.Refs == null) return value.Bytes;
            var result = (byte[])value.Bytes.Clone();
            var members = Members(field.Role);
            for (var i = 0; i < value.Refs.Length; i++) {
                var key = value.Refs[i].Key; if (key.Length == 0) continue;
                var owner = ResidentNativeAssetReference.ResolveTyped(key,typeof(NativeObject),resolve);
                var local = field.Role == ElementRole.Force ? BitConverter.GetBytes(owner.ToInt64()) : M.Read(IntPtr.Add(owner,8),4);
                Buffer.BlockCopy(local, 0, result, i / members * field.Stride + MemberOffset(field.Role, i % members), local.Length);
            }
            return result;
        }
        private static void PrepareStorage(IntPtr address, ArrayState source, int stride, int alignment, Action admitted)
        {
            var target = M.Read(address, 32);
            var targetCapacity = M.U64(target, 24) >> 1;
            if (M.I32(target, 8) != source.Label || targetCapacity != 0 && (M.U64(target, 24) & 1) != 0 || Foreign(source))
                throw Error("cannot reallocate foreign array storage");
            if (targetCapacity != (ulong)source.Capacity) {
                // All represented element types are POD, non-owning PPtr or
                // borrowed rigidbody references. No element destructor is skipped.
                admitted(); M.Write(IntPtr.Add(address, 16), BitConverter.GetBytes(0UL));
                M.Reserve(address, source.Capacity, stride, alignment, admitted);
                target = M.Read(address, 32);
            }
            if (M.U64(target, 24) >> 1 != (ulong)source.Capacity || (M.Ptr(target, 0) != IntPtr.Zero) != source.Allocated)
                throw Error("target allocation extent differs from the native source");
            // Unity's empty dynamic_array constructor uses capacity=0/flag=1,
            // reserve(0) uses flag=0. Reproduce either state only for null data.
            if (!source.Allocated && (M.U64(target, 24) & 1) != (source.External ? 1UL : 0UL))
                M.Write(IntPtr.Add(address, 24), BitConverter.GetBytes(source.External ? 1UL : 0UL));
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ResizeBits(IntPtr target, UIntPtr count, [MarshalAs(UnmanagedType.I1)] bool fill);
        private static void ApplyBits(IntPtr address, Bits source, Action admitted)
        {
            var header = M.Read(address, 32);
            if (M.I32(header, 0) != source.Label) throw Error("bitset target allocator differs");
            if (M.U64(header, 16) != (ulong)source.Count) {
                admitted(); Entry<ResizeBits>(0x1c79a0, "40535641544883ec40450fb6e0488bf2488bd948")(address, new UIntPtr((uint)source.Count), false);
                header = M.Read(address, 32);
            }
            if (M.U64(header,16) != (ulong)source.Count || M.U64(header,24) != (ulong)source.Blocks
                || (M.Ptr(header,8) != IntPtr.Zero) != (source.Blocks != 0)) throw Error("resized target bitset has another extent");
            M.Write(M.Ptr(header,8), source.Words); admitted();
        }
    }
}
