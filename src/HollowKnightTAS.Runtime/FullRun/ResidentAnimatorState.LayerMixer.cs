using System;
using System.Collections.Generic;
using System.Linq;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAnimatorState
    {
        internal sealed class LayerMixerState
        {
            public string Asset = "";
            public int HeapVtable, BlockVtable, HeapLabel, BlockLabel;
            public bool BlockStorage;
            public int[] States = Array.Empty<int>(), Masks = Array.Empty<int>();
            public int LastValues = -1;
            public byte[] RootMotion = Array.Empty<byte>();
            public Storage StateStorage = new Storage(), ParameterStorage = new Storage();
            public LayerParameter[] Parameters = Array.Empty<LayerParameter>();
        }
        internal sealed class LayerParameter
        {
            public int Skeleton = -1;
            // Human mask, weight bits, root-motion mask, additive flag.
            public byte[] Fields = new byte[18];
        }

        private static LayerMixerState CaptureLayerMixer(IntPtr pointer, Node node, Reader reader)
        {
            var body = reader.Memory.Read(IntPtr.Add(pointer,192),176);
            var result = new LayerMixerState {
                Asset = reader.Id<UnityEngine.Object>(M.I32(body,72)),
                HeapVtable = reader.Rva(M.Ptr(body,0)), BlockVtable = reader.Rva(M.Ptr(body,16)),
                HeapLabel = M.I32(body,8), BlockLabel = M.I32(body,56),
                BlockStorage = M.Ptr(body,24) != IntPtr.Zero || M.Ptr(body,32) != IntPtr.Zero || M.U64(body,48) != 0,
                ParameterStorage = StorageAt(body,144),
                Parameters = Decode(reader.ArrayBytes(body,144,32),32,(bytes,at) => new LayerParameter {
                    Skeleton = reader.Live.Avatars!.Retained(M.Ptr(bytes,at),AvatarKind.SkeletonMask),
                    Fields = M.Slice(bytes,at+8,18)
                })
            };
            // The constructor leaves these slots uninitialized. Deallocation
            // clears the owners, but leaves maskCount behind. Only +260 owns them.
            if (node.AnimationFlags[4] != 1) return result;
            result.StateStorage = StorageAt(body,80);
            result.States = Decode(reader.ArrayBytes(body,80,8),8,(bytes,at) =>
                reader.Live.Avatars!.Retained(M.Ptr(bytes,at),AvatarKind.NodeState));
            int count = Count(M.U32(body,120));
            result.Masks = Decode(DirectArray(reader.Memory,M.Ptr(body,112),count,8),8,(bytes,at) =>
                reader.Live.Avatars!.Retained(M.Ptr(bytes,at),AvatarKind.Mask));
            result.RootMotion = DirectArray(reader.Memory,M.Ptr(body,128),count,1);
            result.LastValues = reader.Live.Avatars!.Retained(M.Ptr(body,136),AvatarKind.Values);
            return result;
        }

        private static void ValidateLayerMixer(Node node)
        {
            var layer = node.Layer!;
            if (node.Vtable != 0x15d9380 || !Text(layer.Asset) || layer.States == null || layer.Masks == null
                || layer.RootMotion == null || layer.Parameters == null || layer.Parameters.Length > MaximumEntries
                || layer.States.Length > MaximumEntries || layer.States.Any(index => index < 0) || layer.Masks.Any(index => index < 0)
                || layer.LastValues < -1 || layer.States.Length != layer.Masks.Length || layer.Masks.Length != layer.RootMotion.Length)
                throw Error("layer mixer typed body differs");
            void Storage(Storage value, int count) {
                if (value == null || count > value.Capacity || value.Capacity > MaximumEntries || value.Capacity != 0 && !value.Allocated)
                    throw Error("layer mixer vector extent differs");
            }
            Storage(layer.StateStorage,layer.States.Length);
            Storage(layer.ParameterStorage,layer.Parameters.Length);
            foreach (var parameter in layer.Parameters)
                if (parameter == null || parameter.Skeleton < -1 || parameter.Fields == null || parameter.Fields.Length != 18)
                    throw Error("layer mixer parameter differs");
            if (node.AnimationFlags[4] == 1) {
                if (layer.LastValues == -1 || layer.States.Length != (node.Ports?.Inputs.Length ?? 0)
                    || layer.Parameters.Length != layer.States.Length) throw Error("layer mixer binding extent differs");
            } else if (layer.LastValues != -1 || layer.States.Length != 0 || Json(layer.StateStorage) != Json(new Storage()))
                throw Error("unallocated layer mixer retains owned state");
        }

        private static IEnumerable<string> LayerMixerGaps(LayerMixerState layer)
        {
            if (layer.Asset == Unbound) yield return "layer-mixer-asset-without-binding";
            if (layer.HeapVtable != 0x15d5b60 || layer.BlockVtable != 0x15d6cf8 || layer.HeapLabel != 30
                || layer.BlockLabel != 30 || layer.BlockStorage) yield return "layer-mixer-native-allocator-dependency";
            if (!OwnedStorage(layer.StateStorage) || !OwnedStorage(layer.ParameterStorage)) yield return "layer-mixer-vector-storage-owner";
            if (layer.StateStorage.Padding || layer.ParameterStorage.Padding) yield return "playable-vector-padding-requires-common-canon";
            foreach (var gap in BooleanRange(layer.RootMotion,0,layer.RootMotion.Length,"LayerMixer.RootMotionMask",320)) yield return gap;
            foreach (var parameter in layer.Parameters)
                foreach (var gap in BooleanRange(parameter.Fields,16,2,"LayerMixer.LayerParameters",24)) yield return gap;
        }

        private static void PrepareLayerMixer(IntPtr pointer, LayerMixerState source, LayerMixerState target, List<PendingWrite> writes)
        {
            Reject(LayerMixerGaps(source).Concat(LayerMixerGaps(target)));
            var diffs = new List<string>();
            if (source.Asset != target.Asset) diffs.Add($"Asset({source.Asset}!={target.Asset})");
            if (source.HeapVtable != target.HeapVtable) diffs.Add($"HeapVtable({source.HeapVtable:x}!={target.HeapVtable:x})");
            if (source.BlockVtable != target.BlockVtable) diffs.Add($"BlockVtable({source.BlockVtable:x}!={target.BlockVtable:x})");
            if (source.HeapLabel != target.HeapLabel) diffs.Add($"HeapLabel({source.HeapLabel}!={target.HeapLabel})");
            if (source.BlockLabel != target.BlockLabel) diffs.Add($"BlockLabel({source.BlockLabel}!={target.BlockLabel})");
            if (!source.States.SequenceEqual(target.States)) diffs.Add($"States([{string.Join(",",source.States)}]!=[{string.Join(",",target.States)}])");
            if (!source.Masks.SequenceEqual(target.Masks)) diffs.Add($"Masks([{string.Join(",",source.Masks)}]!=[{string.Join(",",target.Masks)}])");
            if (source.LastValues != target.LastValues) diffs.Add($"LastValues({source.LastValues}!={target.LastValues})");
            if (Json(source.StateStorage) != Json(target.StateStorage)) diffs.Add($"StateStorage({Json(source.StateStorage)}!={Json(target.StateStorage)})");
            if (Json(source.ParameterStorage) != Json(target.ParameterStorage)) diffs.Add($"ParameterStorage({Json(source.ParameterStorage)}!={Json(target.ParameterStorage)})");
            if (source.Parameters.Length != target.Parameters.Length) diffs.Add($"ParamsLen({source.Parameters.Length}!={target.Parameters.Length})");
            if (source.RootMotion.Length != target.RootMotion.Length) diffs.Add($"RootMotionLen({source.RootMotion.Length}!={target.RootMotion.Length})");
            if (source.Parameters.Length == target.Parameters.Length && source.Parameters.Where((parameter,index) => parameter.Skeleton != target.Parameters[index].Skeleton).Any())
                diffs.Add("ParamSkeleton");
            if (diffs.Count != 0)
                throw Error($"layer mixer native allocation or alias differs: {string.Join("; ", diffs)}");
            void Bits(IntPtr at, byte[] value) => writes.Add(new PendingWrite(at,M.Read(at,value.Length),value));
            // Dormant slots can contain stale pointers. They have no write cells.
            if (source.RootMotion.Length != 0) Bits(M.Ptr(IntPtr.Add(pointer,320)),source.RootMotion);
            if (source.Parameters.Length == 0) return;
            var parameters = M.Ptr(IntPtr.Add(pointer,336));
            for (int index = 0; index < source.Parameters.Length; index++)
                Bits(IntPtr.Add(parameters,index*32+8),source.Parameters[index].Fields);
        }
    }
}
