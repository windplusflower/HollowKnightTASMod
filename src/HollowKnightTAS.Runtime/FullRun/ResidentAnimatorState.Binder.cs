using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAnimatorState
    {
        internal sealed class Handles
        {
            public Storage[] Storage = {new Storage(),new Storage(),new Storage()};
            public StreamHandle[] Streams = Array.Empty<StreamHandle>();
            public SceneTransform[] Transforms = Array.Empty<SceneTransform>();
            public SceneProperty[] Properties = Array.Empty<SceneProperty>();
        }
        internal sealed class Storage
        {
            public int Label = 30, Capacity;
            public bool Borrowed = true, Allocated, Padding;
        }
        internal sealed class Generic
        {
            public uint Path, Attribute;
            public string Script = "";
            public int Type;
            public byte Custom, ObjectCurve, IntCurve;
        }
        internal sealed class StreamHandle
        {
            public Generic Binding = new Generic();
            public string Path = "", Attribute = "";
            public int Type, ValueIndex, PoseIndex;
            public int[] StringLabels = new int[2], StringCapacity = new int[2];
        }
        internal sealed class SceneTransform
        {
            public string Owner = "", Access = "";
            public int NullAccessIndex;
        }
        internal sealed class SceneProperty
        {
            public Generic Binding = new Generic();
            public string Transform = "", BoundObject = "", ObjectValue = "";
            public uint TargetType;
            public int FloatBits;
            public bool HasTargetPointer, HasCustomBinding;
        }
        private static readonly int[] HandleStrides = {120,24,72};
        internal static Dictionary<int,int> IndexFloatObjectIds(IEnumerable<int> ids)
        {
            var result = new Dictionary<int,int>();
            foreach (var id in ids) {
                if (id == 0) throw Error("zero is not a registered Unity instance ID");
                var key = BitConverter.ToInt32(BitConverter.GetBytes((float)id),0);
                result[key] = result.ContainsKey(key) ? 0 : id;
            }
            return result;
        }
        private static Handles CaptureHandles(Reader reader, byte[] owner)
        {
            var result = new Handles(); var arrays = new byte[3][];
            for (int i = 0; i < 3; i++) {
                int offset = 64 + 32 * i; var capacityFlags = M.U64(owner,offset + 24);
                arrays[i] = reader.ArrayBytes(owner,offset,HandleStrides[i]);
                result.Storage[i] = new Storage { Label = M.I32(owner,offset+8), Capacity = (int)(capacityFlags >> 1), Borrowed = (capacityFlags & 1) != 0,
                    Allocated = M.Ptr(owner,offset) != IntPtr.Zero, Padding = M.U32(owner,offset+12) != 0 };
            }
            string ReadText(byte[] bytes,int field,int index,StreamHandle row) {
                var count = M.U64(bytes,field+24); var external = M.Ptr(bytes,field);
                ulong capacity = external == IntPtr.Zero ? 15UL : M.U64(bytes,field+8);
                if (count > 16384 || capacity > 1048576 || external == IntPtr.Zero && count > 15 || external != IntPtr.Zero && capacity != 0 && count > capacity)
                    throw Error("animation stream definition string has invalid extent");
                row.StringLabels[index] = M.I32(bytes,field+32); row.StringCapacity[index] = external == IntPtr.Zero ? -1 : (int)capacity;
                var content = external == IntPtr.Zero ? M.Slice(bytes,field+8,(int)count+1) : reader.Memory.Read(external,(int)count+1);
                if (content[(int)count] != 0 || Array.IndexOf(content,(byte)0) != (int)count) throw Error("stream definition string has invalid terminator");
                return new UTF8Encoding(false,true).GetString(content,0,(int)count);
            }
            result.Streams = Decode(arrays[0],120,(bytes,at) => {
                var row = new StreamHandle { Binding = GenericBinding(reader,bytes,at),Type = M.I32(bytes,at+104), ValueIndex = M.I32(bytes,at+108), PoseIndex = M.I32(bytes,at+112) };
                row.Path = ReadText(bytes,at+24,0,row); row.Attribute = ReadText(bytes,at+64,1,row); return row;
            });
            result.Transforms = Decode(arrays[1],24,(bytes,at) => {
                var hierarchy = M.Ptr(bytes,at+8); var index = M.I32(bytes,at+16);
                return new SceneTransform { Owner = reader.Key<Transform>(M.Ptr(bytes,at)), Access = hierarchy == IntPtr.Zero ? "" : reader.TransformAccess(hierarchy,index),
                    NullAccessIndex = hierarchy == IntPtr.Zero ? index : 0 };
            });
            result.Properties = Decode(arrays[2],72,(bytes,at) => {
                var binding = GenericBinding(reader,bytes,at); var bits = M.I32(bytes,at+64);
                return new SceneProperty { Binding = binding, Transform = reader.Key<Transform>(M.Ptr(bytes,at+56)), BoundObject = reader.Key<NativeObject>(M.Ptr(bytes,at+48)),
                    TargetType = M.U32(bytes,at+32), HasTargetPointer = M.Ptr(bytes,at+24) != IntPtr.Zero, HasCustomBinding = M.Ptr(bytes,at+40) != IntPtr.Zero,
                    FloatBits = binding.ObjectCurve == 1 ? 0 : bits, ObjectValue = binding.ObjectCurve == 1 ? reader.FloatObjectId(bits) : "" };
            });
            return result;
        }
        private static int[] HandleCounts(Handles h) => new[] {h.Streams.Length,h.Transforms.Length,h.Properties.Length};
        private static void ValidateHandles(Handles h)
        {
            if (h == null || h.Storage == null || h.Storage.Length != 3 || h.Streams == null || h.Transforms == null || h.Properties == null) throw Error("binder capture shape differs");
            var counts = HandleCounts(h);
            for (int i = 0; i < 3; i++) {
                var s = h.Storage[i];
                if (s == null || s.Capacity < counts[i] || s.Capacity > MaximumEntries || s.Capacity != 0 && !s.Allocated)
                    throw Error("binder allocation shape differs");
            }
            void Binding(Generic b) {
                if (b == null || !Text(b.Script)) throw Error("generic animation binding shape differs");
            }
            foreach (var stream in h.Streams) {
                if (stream == null || stream.StringLabels == null || stream.StringLabels.Length != 2 || stream.StringCapacity == null || stream.StringCapacity.Length != 2)
                    throw Error("stream handle string storage shape differs");
                var names = new[] {stream.Path,stream.Attribute};
                for (int i = 0; i < 2; i++) {
                    var text = names[i];
                    if (text == null || text.IndexOf('\0') >= 0) throw Error("stream handle name differs");
                    var size = Encoding.UTF8.GetByteCount(text); var cap = stream.StringCapacity[i];
                    if (size > 16384 || cap < -1 || cap > 1048576 || cap == -1 && size > 15 || cap > 0 && cap < size) throw Error("stream handle string extent differs");
                }
                Binding(stream.Binding);
            }
            foreach (var t in h.Transforms) if (t == null || !Text(t.Owner) || !Text(t.Access) || t.Access.Length != 0 && t.NullAccessIndex != 0) throw Error("scene Transform handle shape differs");
            foreach (var p in h.Properties) {
                if (p == null || !Text(p.Transform) || !Text(p.BoundObject) || !Text(p.ObjectValue)) throw Error("scene property binding shape differs");
                Binding(p.Binding);
                if (p.Binding.ObjectCurve == 1 && p.FloatBits != 0 || p.Binding.ObjectCurve != 1 && p.ObjectValue.Length != 0) throw Error("scene property object/scalar encoding differs");
            }
        }
        private static IEnumerable<string> HandleGaps(Handles h)
        {
            foreach (var gap in HandleScalarGaps(h)) yield return gap;
            var counts = HandleCounts(h);
            for (int i = 0; i < 3; i++) {
                var s = h.Storage[i];
                if (s.Label != 30 || s.Borrowed && s.Allocated || s.Allocated && s.Capacity == 0) yield return "binder-storage-owner-or-allocator";
                if (counts[i] == 0 && (s.Allocated || !s.Borrowed || s.Padding)) yield return "binder-empty-capacity-requires-common-canon";
                if (counts[i] != 0 && (s.Capacity != counts[i] || s.Padding)) yield return "binder-live-array-storage";
            }
            if (h.Streams.Length != 0) yield return "binder-stream-definition-string-storage-and-reconstruction";
            if (h.Properties.Any(p => p.HasTargetPointer || p.HasCustomBinding)) yield return "binder-native-bound-property-target-or-custom-implementation";
            if (h.Streams.Select(s => s.Binding.Script).Concat(h.Transforms.SelectMany(t => new[] {t.Owner,t.Access}))
                .Concat(h.Properties.SelectMany(p => new[] {p.Binding.Script,p.Transform,p.BoundObject,p.ObjectValue})).Contains(Unbound)) yield return "binder-unresolved-semantic-reference";
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyBinderArray(IntPtr header);
        private static void CanonEmptyHandles(Handles data, IntPtr owner, Action admitted)
        {
            var counts = HandleCounts(data);
            for (int i = 0; i < 3; i++) {
                var storage = data.Storage[i];
                if (counts[i] != 0 || !storage.Allocated && storage.Borrowed && !storage.Padding) continue;
                if (storage.Label != 30 || storage.Borrowed && storage.Allocated) throw Error("cannot release borrowed empty binder capacity");
                admitted(); var header = IntPtr.Add(owner,64+i*32); var bytes = M.Read(header,32);
                if (M.U64(bytes,16) != 0 || (M.Ptr(bytes,0) != IntPtr.Zero) != storage.Allocated || M.I32(bytes,8) != storage.Label)
                    throw Error("binder empty array changed before Canon");
                if (storage.Allocated) {
                    var function = IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress,i == 0 ? 0xd8430 : 0xb32f0);
                    var prefix = i == 0 ? "48895c2410564883ec20488b19488bf1" : "40534883ec20488bd9488b094885c974";
                    if (BitConverter.ToString(M.Read(function,16)).Replace("-","").ToLowerInvariant() != prefix) throw Error("binder array destructor ABI differs");
                    ((DestroyBinderArray)Marshal.GetDelegateForFunctionPointer(function,typeof(DestroyBinderArray)))(header);
                    if (M.Ptr(header) != IntPtr.Zero) throw Error("owned empty binder allocation did not retire");
                }
                var canonical = new byte[32]; BitConverter.GetBytes(storage.Label).CopyTo(canonical,8); canonical[24] = 1;
                M.Write(header,canonical); data.Storage[i] = new Storage { Label = storage.Label }; admitted();
            }
        }
        private static void PrepareHandles(Handles source, Handles target, IntPtr owner, Func<string,NativeObject> resolve,
            Func<object,string?> bind, Func<int,string> floatObject, List<PendingWrite> writes)
        {
            Reject(HandleScalarGaps(source).Concat(HandleScalarGaps(target)));
            if (!HandleCounts(source).SequenceEqual(HandleCounts(target)) || Json(source.Storage) != Json(target.Storage) || Json(source.Streams) != Json(target.Streams))
                throw Error("native handle storage requires a different allocation/stream definition");
            // Native Object events are restored after component bodies. An
            // existing listener must remain decodable through the binder
            // throughout that handoff, even when definitions change order.
            IEnumerable<string> Receivers(Handles h) => h.Transforms.Select(t => t.Owner).Concat(h.Properties.Select(p => p.Transform)).Where(s => s.Length != 0);
            if (!new HashSet<string>(Receivers(source),StringComparer.Ordinal).SetEquals(Receivers(target))
                || !new HashSet<string>(source.Properties.Select(p => p.BoundObject).Where(s => s.Length != 0),StringComparer.Ordinal)
                    .SetEquals(target.Properties.Select(p => p.BoundObject).Where(s => s.Length != 0)))
                throw Error("binder receiver membership requires joint Object event migration");
            IntPtr Pointer(string key,bool transform) {
                if (key.Length == 0) return IntPtr.Zero;
                if (key == Unbound) throw Error("handle reference is unbound");
                var type = transform ? typeof(Transform) : typeof(NativeObject);
                var pointer = ResidentNativeAssetReference.ResolveTyped(key,type,resolve);
                if (ResidentNativeAssetReference.BindPointer(pointer,type,bind) != key) throw Error("handle reference failed reverse lookup");
                return pointer;
            }
            int Id(string key) => ResidentNativeAssetReference.ResolveId(key,typeof(NativeObject),resolve);
            void Write(IntPtr at, byte[] value) => writes.Add(new PendingWrite(at,M.Read(at,value.Length),value));
            var transforms = M.Ptr(IntPtr.Add(owner,96)); var props = M.Ptr(IntPtr.Add(owner,128));
            for (int i = 0; i < source.Transforms.Length; i++) {
                var row = source.Transforms[i]; var slot = IntPtr.Add(transforms,i*24);
                Write(slot,BitConverter.GetBytes(Pointer(row.Owner,true).ToInt64()));
                var access = row.Access.Length == 0 ? new byte[12] : M.Read(IntPtr.Add(Pointer(row.Access,true),56),12);
                if (row.Access.Length == 0) BitConverter.GetBytes(row.NullAccessIndex).CopyTo(access,8);
                else if (M.Ptr(access,0) == IntPtr.Zero) throw Error("target scene handle Transform has no live hierarchy");
                Write(IntPtr.Add(slot,8),access);
            }
            for (int i = 0; i < source.Properties.Length; i++) {
                var row = source.Properties[i]; var slot = IntPtr.Add(props,i*72); var b = row.Binding;
                if (row.HasCustomBinding || row.HasTargetPointer || target.Properties[i].HasCustomBinding || target.Properties[i].HasTargetPointer)
                    throw Error("bound custom/native property needs its own target binding resolver");
                var fields = new byte[19]; BitConverter.GetBytes(b.Path).CopyTo(fields,0); BitConverter.GetBytes(b.Attribute).CopyTo(fields,4);
                BitConverter.GetBytes(Id(b.Script)).CopyTo(fields,8); BitConverter.GetBytes(b.Type).CopyTo(fields,12);
                fields[16] = b.Custom; fields[17] = b.ObjectCurve; fields[18] = b.IntCurve; Write(slot,fields);
                Write(IntPtr.Add(slot,32),BitConverter.GetBytes(row.TargetType)); Write(IntPtr.Add(slot,48),BitConverter.GetBytes(Pointer(row.BoundObject,false).ToInt64()));
                Write(IntPtr.Add(slot,56),BitConverter.GetBytes(Pointer(row.Transform,true).ToInt64()));
                int bits = row.FloatBits;
                if (b.ObjectCurve != 0) {
                    bits = BitConverter.ToInt32(BitConverter.GetBytes((float)Id(row.ObjectValue)),0);
                    if (floatObject(bits) != row.ObjectValue) throw Error("target float-cast object ID is ambiguous");
                }
                Write(IntPtr.Add(slot,64),BitConverter.GetBytes(bits));
            }
        }
    }
}
