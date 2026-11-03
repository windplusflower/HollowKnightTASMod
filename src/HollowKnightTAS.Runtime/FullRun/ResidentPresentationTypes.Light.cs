using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentPresentationTypes
    {
        internal sealed class LightRegistry
        {
            public bool Present;
            public string[] Active=Array.Empty<string>(), Realtime=Array.Empty<string>();
            public int[] ListenerVtables=Array.Empty<int>();
        }
        private static readonly int[][] LightSharedRanges={new[]{28,102},new[]{140,44},new[]{184,5},new[]{192,96},new[]{296,64},new[]{368,20},new[]{464,156}};
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void OwnerCall(IntPtr owner);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr HashInsert(IntPtr table,IntPtr result,IntPtr key);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ShadowDistancesCall(IntPtr owner,IntPtr values);
        private static IntPtr LightManager()=>M.Ptr(IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress,0x19d81b8));
        private static IntPtr LightFunction(int rva,string prefix)
        {
            var pointer=IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress,rva);
            if(BitConverter.ToString(M.Read(pointer,prefix.Length/2)).Replace("-","").ToLowerInvariant()!=prefix) throw Error("Light native function ABI differs");
            return pointer;
        }
        private static void MakeLightUnique(IntPtr owner,Action admitted)
        {
            admitted(); var call=LightFunction(0x59c430,"40564883ec30488b4150488bf18b401483f801");
            ((OwnerCall)Marshal.GetDelegateForFunctionPointer(call,typeof(OwnerCall)))(owner);
            var shared=M.Ptr(IntPtr.Add(owner,80));
            if(shared==IntPtr.Zero || M.I32(M.Read(IntPtr.Add(shared,20),4),0)!=1) throw Error("Light COW failed to make unique data");
            admitted();
        }
        private static void CaptureLightShared(Row row,IntPtr shared,Func<IntPtr,int,byte[]> read,Func<int,string> textureId,Func<object,string?> bind)
        {
            var bytes=read(shared,624); row.LightReferences=M.I32(bytes,20); row.Cookie=textureId(M.I32(bytes,132));
            row.Scalars=row.Scalars.Concat(LightSharedRanges.Select(range=>M.Slice(bytes,range[0],range[1]))).ToArray();
            var nativeTexture=M.U32(bytes,136);
            if(nativeTexture!=0) {
                row.CookieNativeTexture=ResidentNativeAssetReference.BindTexture(unchecked((int)nativeTexture),bind);
            }
            row.CookieResource=nativeTexture!=0;
            var distances=M.Ptr(bytes,288); row.ShadowDistances=distances==IntPtr.Zero?Array.Empty<byte>():read(distances,128);
            var buffers=ArrayInfo(bytes,392,32); var masks=ArrayInfo(bytes,424,32);
            var bufferHeaders=read(M.Ptr(bytes,392),buffers.Item1*32); var maskHeaders=read(M.Ptr(bytes,424),masks.Item1*32);
            row.LightEventCounts=Enumerable.Range(0,buffers.Item1).Select(index=>ArrayInfo(bufferHeaders,index*32,8).Item1).ToArray();
            row.LightEventMasks=Enumerable.Range(0,masks.Item1).Select(index=> {
                var entry=ArrayInfo(maskHeaders,index*32,4); return read(M.Ptr(maskHeaders,index*32),entry.Item1*4);
            }).ToArray();
            row.RenderEvents=row.LightEventCounts.Any(count=>count!=0) || row.LightEventMasks.Any(values=>values.Length!=0);
        }
        private static LightRegistry CaptureLightRegistry(Row[] rows,ResidentObjectDirectory directory,Func<IntPtr,int,byte[]> read)
        {
            var result=new LightRegistry(); var manager=LightManager(); result.Present=manager!=IntPtr.Zero;
            if(!result.Present) return result;
            var reverse=directory.NativeBindings.Where(pair=>pair.Value.GetType()==typeof(Light)).ToDictionary(pair=>M.Owner(pair.Value),pair=>pair.Key);
            string Owner(IntPtr pointer)=>reverse.TryGetValue(pointer,out var key)?key:"@unsupported";
            var body=read(manager,80); var root=manager; var current=M.Ptr(body,8); var previous=root; var seen=new HashSet<IntPtr>(); var active=new List<string>();
            while(current!=root) {
                if(current==IntPtr.Zero || !seen.Add(current) || seen.Count>100000) throw Error("Light manager list is invalid");
                var node=read(current,16); if(M.Ptr(node,0)!=previous) throw Error("Light manager list backlink differs");
                active.Add(Owner(IntPtr.Subtract(current,64))); previous=current; current=M.Ptr(node,8);
            }
            if(M.Ptr(body,0)!=previous) throw Error("Light manager list tail differs");
            foreach(var row in rows.Where(row=>row.Kind=="light")) {
                var owner=M.Owner(directory.Resolve(row.Owner)); var node=read(IntPtr.Add(owner,64),16);
                var linked=M.Ptr(node,0)!=IntPtr.Zero || M.Ptr(node,8)!=IntPtr.Zero;
                if(linked!=active.Contains(row.Owner)) throw Error("Light intrusive node is outside complete manager list");
            }
            result.Active=active.ToArray();
            var mask=M.U32(body,24); var count=M.U32(body,28);
            if((mask&15)!=0 || mask>100000*16 || (((mask>>4)+1)&(mask>>4))!=0 || count>100000) throw Error("Light realtime hash shape differs");
            var nodes=read(M.Ptr(body,16),checked((int)mask+16)); var realtime=new List<string>();
            for(var at=0;at<nodes.Length;at+=16) {
                var hash=M.U32(nodes,at); if(hash>=0xfffffffeU) continue;
                var pointer=M.Ptr(nodes,at+8);
                if(unchecked((uint)pointer.ToInt64()*0x5497fdb5U & 0xfffffffcU)!=hash) throw Error("Light realtime native pointer hash differs");
                realtime.Add(Owner(pointer));
            }
            if(realtime.Count!=count) throw Error("Light realtime hash count differs");
            result.Realtime=realtime.OrderBy(key=>key,StringComparer.Ordinal).ToArray();
            var listeners=ArrayInfo(body,48,8); var listenerBytes=read(M.Ptr(body,48),listeners.Item1*8);
            var engine=LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress.ToInt64();
            result.ListenerVtables=Enumerable.Range(0,listeners.Item1).Select(index=> {
                var pointer=M.Ptr(listenerBytes,index*8); if(pointer==IntPtr.Zero) throw Error("Light listener is null");
                var vtable=M.Ptr(read(pointer,8),0).ToInt64()-engine; return vtable>0 && vtable<0x4000000?(int)vtable:-1;
            }).ToArray();
            return result;
        }
        private static void ApplyLightShared(Row row,IntPtr owner,Func<string,UnityEngine.Object> resolve,Action admitted)
        {
            MakeLightUnique(owner,admitted); var shared=M.Ptr(IntPtr.Add(owner,80));
            M.Write(IntPtr.Add(owner,88),BitConverter.GetBytes(ResidentNativeAssetReference.ResolveId(row.Flare,typeof(Flare),resolve)));
            M.Write(IntPtr.Add(shared,132),BitConverter.GetBytes(ResidentNativeAssetReference.ResolveId(row.Cookie,typeof(Texture),resolve)));
            var texture=unchecked((uint)ResidentNativeAssetReference.ResolveTexture(row.CookieNativeTexture,resolve));
            M.Write(IntPtr.Add(shared,136),BitConverter.GetBytes(texture));
            for(var index=0;index<LightSharedRanges.Length;index++) M.Write(IntPtr.Add(shared,LightSharedRanges[index][0]),row.Scalars[index+2]);
            var function=LightFunction(0x5bfbb0,"48895c2408574883ec30488bfa488bd94885d2");
            var storage=IntPtr.Zero;
            try {
                if(row.ShadowDistances.Length!=0) { storage=Marshal.AllocHGlobal(128); Marshal.Copy(row.ShadowDistances,0,storage,128); }
                ((ShadowDistancesCall)Marshal.GetDelegateForFunctionPointer(function,typeof(ShadowDistancesCall)))(shared,storage);
            } finally { if(storage!=IntPtr.Zero) Marshal.FreeHGlobal(storage); }
            admitted();
        }
        private static void ApplyLightRegistry(Data data,ResidentObjectDirectory directory,Func<string,UnityEngine.Object> resolve,Action admitted)
        {
            var registry=data.Lights; var manager=LightManager();
            if((manager!=IntPtr.Zero)!=registry.Present) throw Error("Light manager presence changed");
            if(!registry.Present) return;
            foreach(var row in data.Rows.Where(row=>row.Kind=="light")) M.Write(IntPtr.Add(M.Owner(resolve(row.Owner)),64),new byte[16]);
            var nodes=registry.Active.Select(key=>IntPtr.Add(M.Owner(resolve(key)),64)).ToArray();
            M.Write(manager,BitConverter.GetBytes((nodes.Length==0?manager:nodes[nodes.Length-1]).ToInt64())
                .Concat(BitConverter.GetBytes((nodes.Length==0?manager:nodes[0]).ToInt64())).ToArray());
            for(var index=0;index<nodes.Length;index++) M.Write(nodes[index],BitConverter.GetBytes((index==0?manager:nodes[index-1]).ToInt64())
                .Concat(BitConverter.GetBytes((index+1==nodes.Length?manager:nodes[index+1]).ToInt64())).ToArray());
            var table=IntPtr.Add(manager,16); var header=M.Read(table,32); var mask=M.U32(header,8);
            if(mask!=0) {
                var bytes=new byte[checked((int)mask+16)];
                for(var at=0;at<bytes.Length;at+=16) Buffer.BlockCopy(BitConverter.GetBytes(uint.MaxValue),0,bytes,at,4);
                M.Write(M.Ptr(header,0),bytes);
            }
            M.Write(IntPtr.Add(table,12),BitConverter.GetBytes(0U));
            M.Write(IntPtr.Add(table,20),BitConverter.GetBytes(mask==0?0U:((mask>>4)+1)*2/3));
            var insert=LightFunction(0x48ae40,"48895c240848896c2410488974241857415641574883ec30");
            var storage=Marshal.AllocHGlobal(32);
            try {
                foreach(var key in registry.Realtime) {
                    admitted(); Marshal.WriteInt64(storage,M.Owner(resolve(key)).ToInt64());
                    ((HashInsert)Marshal.GetDelegateForFunctionPointer(insert,typeof(HashInsert)))(table,IntPtr.Add(storage,8),storage);
                }
            } finally { Marshal.FreeHGlobal(storage); }
            admitted();
        }
        private static IEnumerable<string> LightGaps(Row row)
        {
            if(!row.SharedLight) yield return "light/shared-data-absent";
            else if(row.LightReferences!=1) yield return "light/shared-data-nonunique-references";
            if(row.RenderEvents) yield return "light/retained-render-command-buffers";
            if(row.CookieNativeTexture=="@unsupported" || row.CookieNativeTexture==ResidentNativeAssetReference.Missing) yield return "light/unbound-cookie-texture-id";
            if(M.I32(row.Scalars[1],4)!=0) yield return "light/live-halo-handle";
            if(M.I32(row.Scalars[1],8)!=-1) yield return "light/live-flare-handle";
        }
        private static IEnumerable<string> LightRegistryGaps(LightRegistry data)
        {
            if(data.Active.Concat(data.Realtime).Contains("@unsupported")) yield return "light/nonresident-manager-owner";
            if(data.ListenerVtables.Length!=0) yield return "light/manager-listener-native-context";
        }
        private static void ValidateLights(Data data)
        {
            var registry=data.Lights;
            if(registry.Active==null || registry.Realtime==null || registry.ListenerVtables==null || registry.Active.Length>100000 || registry.Realtime.Length>100000 || registry.ListenerVtables.Length>100000
                || registry.Active.Distinct(StringComparer.Ordinal).Count()!=registry.Active.Length || registry.Realtime.Distinct(StringComparer.Ordinal).Count()!=registry.Realtime.Length) throw Error("Light manager schema differs");
            if(!registry.Present && (registry.Active.Length!=0 || registry.Realtime.Length!=0 || registry.ListenerVtables.Length!=0)) throw Error("absent Light manager has retained state");
            foreach(var key in registry.Active.Concat(registry.Realtime).Where(key=>key!="@unsupported"))
                if(!data.Rows.Any(row=>row.Kind=="light" && row.Owner==key)) throw Error("Light manager owner is not actual Light");
            foreach(var row in data.Rows) {
                if(row.CookieNativeTexture==null || row.ShadowDistances==null || row.LightEventCounts==null || row.LightEventMasks==null || row.LightEventMasks.Any(bytes=>bytes==null || bytes.Length%4!=0) || row.LightEventCounts.Any(count=>count<0 || count>100000)) throw Error("Light resource shape differs");
                if(row.ShadowDistances.Length!=0 && row.ShadowDistances.Length!=128 || row.CookieResource!=(row.CookieNativeTexture.Length!=0)
                    || row.RenderEvents!=(row.LightEventCounts.Any(count=>count!=0) || row.LightEventMasks.Any(bytes=>bytes.Length!=0))) throw Error("Light resource observation differs");
                if(row.Kind!="light" && (row.CookieNativeTexture.Length!=0 || row.ShadowDistances.Length!=0 || row.LightEventCounts.Length!=0 || row.LightEventMasks.Length!=0)) throw Error("Light resources on another actual type");
                if(row.Kind=="light" && (!registry.Present || row.LightReferences<1)) {
                    if(row.SharedLight && row.LightReferences<1) throw Error("Light shared retain count is invalid");
                }
            }
        }
    }
}
