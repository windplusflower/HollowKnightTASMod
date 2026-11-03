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
        private const int NullLink = -1, UnknownLink = -2, EmitterSelf = -3, BoundObject = -4, CurrentSharedData = -5;
        private static readonly int[] CacheOffsets = { 856, 864, 872 };
        private static class CacheTypes { internal static readonly Type[] Values = { typeof(Mesh), typeof(Sprite), typeof(Texture2D) }; }
        internal sealed class CacheBindings
        {
            public string[] Objects = new[] { "", "", "" };
            public string SharedMesh = "", SharedLight = "";
            public int MeshRetains, LightRetains;
        }
        private static int LightRoot(int offset) => offset >= 4760 && offset <= 4800 ? 4760 + (offset - 4760) / 16 * 16 : -1;
        private sealed partial class Reader
        {
            internal string Cached(IntPtr pointer, Type expected)
            {
                if (pointer == IntPtr.Zero) return "";
                var id = M.I32(Read(IntPtr.Add(pointer,8),4),0);
                if (Registered(id) != pointer) throw Error("cached pointer is not its registered object");
                return ResidentNativeAssetReference.BindPointer(pointer,expected,bind);
            }
        }
        private static CacheBindings CaptureCacheBindings(Reader reader, IntPtr emitter, IntPtr modulesAt, byte[] modules, out int[] links)
        {
            var data = new CacheBindings(); var result = new int[ModuleLinks.Length]; links = result;
            var mesh = IntPtr.Zero;
            for (var i = 0; i < ModuleLinks.Length; i++) {
                var offset = ModuleLinks[i]; var pointer = M.Ptr(modules, offset);
                if (pointer == IntPtr.Zero) { result[i] = NullLink; continue; }
                result[i] = UnknownLink;
                if (offset == 1064 && pointer == emitter) { result[i] = EmitterSelf; continue; }
                var root = LightRoot(offset);
                if (root >= 0 && pointer == IntPtr.Add(modulesAt, root)) { result[i] = root; continue; }
                var cached = Array.IndexOf(CacheOffsets, offset);
                if (cached >= 0) {
                    var key = reader.Cached(pointer,CacheTypes.Values[cached]);
                    if (key.Length != 0) { data.Objects[cached] = key; result[i] = BoundObject; if (cached == 0) mesh = pointer; }
                } else if (offset == 912) {
                    data.MeshRetains = M.I32(reader.Read(IntPtr.Add(pointer, 4), 4), 0);
                    if (mesh != IntPtr.Zero && M.Ptr(reader.Read(IntPtr.Add(mesh, 56), 8), 0) == pointer) {
                        data.SharedMesh = data.Objects[0]; result[i] = CurrentSharedData;
                    }
                } else if (offset == 4808) {
                    data.LightRetains = M.I32(reader.Read(IntPtr.Add(pointer, 20), 4), 0);
                    // Cache() releases the previous SharedLightData even when
                    // prefab resolution fails. A retained historical COW copy
                    // need not equal the Light's current data. Never flatten it.
                    foreach (var id in new[] { M.I32(modules, 4816), M.I32(modules, 4656) }) {
                        var light = reader.Registered(id);
                        if (light == IntPtr.Zero || !ResidentNativeAssetReference.IsFamily(light,typeof(Light)) || M.Ptr(reader.Read(IntPtr.Add(light,80),8),0) != pointer) continue;
                        var key = reader.Reference(typeof(Light), id, out var missing);
                        if (!missing) { data.SharedLight = key; result[i] = CurrentSharedData; break; }
                    }
                }
            }
            return data;
        }
        private static void CacheGaps(Body body, SortedSet<string> gaps)
        {
            for (var index = 0; index < body.ModuleLinkKinds.Length; index++) {
                var offset = ModuleLinks[index];
                if (body.ModuleLinkKinds[index] != UnknownLink) continue;
                string gap = offset == 912 ? "retained-historical-shared-mesh-data" : offset == 4808 ? "retained-historical-shared-light-data"
                    : offset == 1072 ? "skin-mesh-info-job-and-buffers" : offset == 4200 ? "collision-plane-voxel-cache"
                    : offset == 1048 || offset == 1056 ? "mesh-object-users-intrusive-registration"
                    : offset == 1064 ? "mesh-notification-node-data-owner" : LightRoot(offset) >= 0 ? "custom-light-list-nodes-and-shadow-cache"
                    : "cached-asset-outside-binding-closure";
                gaps.Add(gap);
            }
        }
        private static void ValidateCacheBindings(Body body)
        {
            var bindings = body.Caches;
            if (bindings == null || bindings.Objects == null || bindings.Objects.Length != 3
                || bindings.Objects.Concat(new[] { bindings.SharedMesh, bindings.SharedLight }).Any(key => key == null || key.Length > 262144))
                throw Error("cached object binding shape differs");
            for (var index = 0; index < ModuleLinks.Length; index++) {
                var offset = ModuleLinks[index]; var kind = body.ModuleLinkKinds[index]; var cached = Array.IndexOf(CacheOffsets, offset);
                if (cached >= 0 && (kind == BoundObject) != (bindings.Objects[cached].Length != 0)) throw Error("cached object pointer and binding disagree");
                if (offset == 912 || offset == 4808) {
                    var owner = offset == 912 ? bindings.SharedMesh : bindings.SharedLight;
                    var retains = offset == 912 ? bindings.MeshRetains : bindings.LightRetains;
                    if ((kind == CurrentSharedData) != (owner.Length != 0) || (kind != NullLink ? retains < 1 : retains != 0)
                        || offset == 912 && kind == CurrentSharedData && owner != bindings.Objects[0]) throw Error("shared-data retain, owner or storage differs");
                }
                if (kind == NullLink || kind == UnknownLink || kind == EmitterSelf && offset == 1064 || kind == BoundObject && cached >= 0
                    || kind == CurrentSharedData && (offset == 912 || offset == 4808) || kind >= 0 && kind == LightRoot(offset)) continue;
                throw Error("native link kind does not own this field");
            }
            for (var off = 4760; off <= 4792; off += 16) {
                var i = Array.IndexOf(ModuleLinks, off); var prev = body.ModuleLinkKinds[i]; var next = body.ModuleLinkKinds[i + 1];
                if ((prev == off || next == off) && prev != next || (prev == NullLink || next == NullLink) && prev != next)
                    throw Error("light list sentinel is not reciprocal");
            }
        }
        private static void CheckCaches(Body body, Func<string,NativeObject> resolve)
        {
            var data = body.Caches;
            for (var i = 0; i < data.Objects.Length; i++) if (data.Objects[i].Length != 0) _ = ResidentNativeAssetReference.ResolveTyped(data.Objects[i],CacheTypes.Values[i],resolve);
            if (data.SharedLight.Length != 0) _ = ResidentNativeAssetReference.ResolveTyped(data.SharedLight,typeof(Light),resolve);
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AcquireShared(IntPtr owner);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CacheAction(IntPtr owner);
        private static void ApplyCaches(Body body, Body before, IntPtr emitter, IntPtr modules, Func<string,NativeObject> resolve, Action admitted)
        {
            var values = body.Caches; var local = before.Caches;
            for (var i = 0; i < ModuleLinks.Length; i++) {
                int offset = ModuleLinks[i], kind = body.ModuleLinkKinds[i], previous = before.ModuleLinkKinds[i];
                int cache = Array.IndexOf(CacheOffsets, offset);
                if (kind == previous && (cache < 0 || values.Objects[cache] == local.Objects[cache])) continue;
                if (offset == 912 || offset == 4808) continue;
                var pointer = kind == EmitterSelf ? emitter : kind == BoundObject ? ResidentNativeAssetReference.ResolveTyped(values.Objects[cache],CacheTypes.Values[cache],resolve)
                    : kind >= 0 ? IntPtr.Add(modules, kind) : IntPtr.Zero;
                admitted(); M.Write(IntPtr.Add(modules, offset), BitConverter.GetBytes(pointer.ToInt64()));
            }
            if (values.SharedMesh != local.SharedMesh) {
                var slot = IntPtr.Add(modules, 912); var old = M.Ptr(slot); var next = IntPtr.Zero;
                admitted();
                if (values.SharedMesh.Length != 0) {
                    var owner = ResidentNativeAssetReference.ResolveTyped(values.SharedMesh,typeof(Mesh),resolve);
                    next = Entry<AcquireShared>(0x6b95b0, "488b4138f0ff4004488b4138c3")(owner);
                    if (next == IntPtr.Zero || next != M.Ptr(IntPtr.Add(owner,56))) throw Error("target shared mesh acquisition differs");
                }
                M.Write(slot, BitConverter.GetBytes(next.ToInt64()));
                if (old != IntPtr.Zero) Entry<CacheAction>(0xbb370, "40574883ec20488bf9b8fffffffff00fc1410483f801")(old);
            }
            if (values.SharedLight != local.SharedLight) {
                // Cache() is used only with completely represented empty lists.
                // Equalize the temporary prefab and cached ID so it takes the
                // retain/release path and never deletes or instantiates a light.
                var prefab = IntPtr.Add(modules,4656); var cached = IntPtr.Add(modules,4816);
                var originalPrefab = M.Read(prefab,4); var originalCached = M.Read(cached,4);
                var light = ResidentNativeAssetReference.ResolveTyped(values.SharedLight,typeof(Light),resolve);
                var id = light == IntPtr.Zero ? BitConverter.GetBytes(0) : M.Read(IntPtr.Add(light,8),4); admitted();
                M.Write(prefab,id); M.Write(cached,id);
                Entry<CacheAction>(0x205860,"48895c24104889742418574883ec30488bb9a8000000")(IntPtr.Add(modules,4640));
                var expected = light == IntPtr.Zero ? IntPtr.Zero : M.Ptr(IntPtr.Add(light,80));
                if (M.Ptr(IntPtr.Add(modules,4808)) != expected) throw Error("target shared light acquisition differs");
                M.Write(prefab,originalPrefab); M.Write(cached,originalCached);
            }
            admitted();
        }
    }
}
