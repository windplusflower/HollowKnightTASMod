using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.SceneManagement;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    // The public all-GameObject query constructs scripting wrappers even for
    // prefab assets that are then discarded by IsLoaded. Read the native GO
    // set and use the exact two read-only leaves used by get_scene_Injected.
    // Only the hierarchy API below visits managed owners in loaded scenes.
    internal static class ResidentSceneMembership
    {
        private const int GameObjectClass = 0x194b2d0, TransformClass = 0x19491a0;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ComponentQuery(IntPtr go, IntPtr rtti);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr SceneQuery(IntPtr transform);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool MainThread();
        [StructLayout(LayoutKind.Explicit)] private struct SceneValue
        { [FieldOffset(0)] public int Handle; [FieldOffset(0)] public Scene Scene; }
        private static readonly FieldInfo[] SceneFields = typeof(Scene).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        internal sealed class Membership
        {
            internal readonly Dictionary<int,Scene> Loaded = new Dictionary<int,Scene>();
            internal readonly Dictionary<int,int> Objects = new Dictionary<int,int>(); // native ID -> actual scene handle
            internal void RequireSame(IReadOnlyDictionary<int,int> actual)
            {
                if (Objects.Count != actual.Count || Objects.Any(p => !actual.TryGetValue(p.Key, out var scene) || scene != p.Value))
                    throw Error("managed traversal differs from the registered native GameObjects and scenes");
            }
        }
        internal static Membership Read()
        {
            if (SceneFields.Length != 1 || SceneFields[0].Name != "m_Handle" || SceneFields[0].FieldType != typeof(int) || Marshal.SizeOf<Scene>() != 4)
                throw Error("managed Scene handle ABI differs");
            var module = LoadingEngineClockProbe.RequirePinnedEngine(); var engine = module.BaseAddress;
            T Entry<T>(int rva, int size, string hash) where T : Delegate {
                var address = IntPtr.Add(engine,rva); using var sha = SHA256.Create();
                if (BitConverter.ToString(sha.ComputeHash(M.Read(address,size))).Replace("-","").ToLowerInvariant() != hash) throw Error("native scene lookup ABI differs");
                return (T)Marshal.GetDelegateForFunctionPointer(address,typeof(T));
            }
            if (!Entry<MainThread>(0x81c590,30,"730b0ebf0c4d9a6bec6ff34df66477641fc299b8a008a553b195d5283b05556d")()) throw Error("off the engine main thread");
            var query = Entry<ComponentQuery>(0x580130,58,"88c12a257d8d4affce118c5121493a7530ce07391be806aa8d6170a0156c2853");
            var getScene = Entry<SceneQuery>(0x7e6a60,87,"35de8f205df5c58bd435dd56b9b0fb2e66de43e4440984ef9f60bffae6f949e1");
            var go = IntPtr.Add(engine,GameObjectClass); var transform = IntPtr.Add(engine,TransformClass);
            var table = IntPtr.Add(engine,0x19d59f0); var index = M.I32(M.Read(IntPtr.Add(go,48),4),0);
            if (index < 0 || index >= M.U32(M.Read(table,4),0) || index >= 1024 || M.Ptr(IntPtr.Add(table,8+index*8)) != go)
                throw Error("native GameObject class registration differs");
            var sets = M.Ptr(IntPtr.Add(engine,0x19d7bf0)); if (sets == IntPtr.Zero) throw Error("native object sets absent");
            var slot = IntPtr.Add(sets,index*32); var header = M.Read(slot,32);
            var address = M.Ptr(header,0); var slots = M.Read(address,ResidentNativeGlobalObjects.HashByteLength(header,false));
            var result = new Membership(); var scenes = new Dictionary<int,Scene>(); var ids = new HashSet<int>();
            foreach (var row in ResidentNativeGlobalObjects.DecodeHashRows(header,slots,false)) {
                var owner = M.Read(row.Pointer,16); var id = M.I32(owner,8);
                if (id == 0 || (M.U32(owner,12)>>21) != index || !ids.Add(id)) throw Error("GameObject set contains a different type or repeats an identity");
                var tr = query(row.Pointer,transform); var ptr = tr == IntPtr.Zero ? IntPtr.Zero : getScene(tr);
                if (ptr == IntPtr.Zero) continue;
                var handle = M.I32(M.Read(IntPtr.Add(ptr,8),4),0);
                if (!scenes.TryGetValue(handle,out var scene)) {
                    scene = new SceneValue { Handle = handle }.Scene; scenes.Add(handle,scene);
                    if (scene.IsValid() && scene.isLoaded) result.Loaded.Add(handle,scene);
                }
                if (result.Loaded.ContainsKey(handle)) result.Objects.Add(id,handle);
            }
            if (M.Ptr(IntPtr.Add(engine,0x19d7bf0)) != sets || !M.Read(slot,32).SequenceEqual(header) || !M.Read(address,slots.Length).SequenceEqual(slots))
                throw Error("GameObject native membership changed during observation");
            return result;
        }
        internal static GameObject[] LoadedGameObjects()
        {
            var membership = Read(); var result = new List<GameObject>(); var seen = new Dictionary<int,int>();
            foreach (var scene in membership.Loaded.Values) {
                foreach (var root in scene.GetRootGameObjects()) {
                    foreach (var transform in root.GetComponentsInChildren<Transform>(true)) {
                        var owner = transform.gameObject; seen.Add(owner.GetInstanceID(),owner.scene.handle); result.Add(owner);
                    }
                }
            }
            membership.RequireSame(seen); return result.ToArray();
        }
        private static InvalidDataException Error(string reason) => new InvalidDataException("Resident scene membership: " + reason);
    }
}
