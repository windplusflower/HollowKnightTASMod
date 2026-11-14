using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAudioState
    {
        private sealed class WeakSlot
        {
            internal IntPtr Address;
            internal string Alias = "";
        }
        private sealed class WeakBackend
        {
            internal IntPtr Vtable;
            internal Func<IntPtr> Create = () => throw new InvalidOperationException();
            internal Action<IntPtr,IntPtr> Assign = (_,__) => throw new InvalidOperationException();
            internal Action<IntPtr> Release = _ => throw new InvalidOperationException();
            internal Func<IntPtr,int,byte[]> Read = M.Read;
            internal Action Require = () => { };
            internal Action<IntPtr,byte[]> Write = M.Write;
            internal Action<IntPtr,int> Reserve = (_,__) => throw new InvalidOperationException();
            internal Action<IntPtr> ClearArray = _ => throw new InvalidOperationException();
            internal Func<int,int,int,IntPtr> AllocateStorage = (_,__,___) => throw new InvalidOperationException();
            internal Action<IntPtr,int> FreeStorage = (_,__) => throw new InvalidOperationException();
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr WeakAllocate(ulong size,int label,ulong alignment,IntPtr file,int line);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr WeakConstruct(IntPtr block,int label);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr WeakAssign(IntPtr destination,IntPtr source);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void WeakRelease(IntPtr slot);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void WeakFree(IntPtr block,int label,IntPtr file,int line);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void WeakClearArray(IntPtr header);

        private static WeakBackend NativeWeakBackend(Action admitted)
        {
            var engine=LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
            // All code and the deleting-dtor slot are preflighted before allocating or publishing.
            var entries=new[] {
                (Rva:0x5680a0,Hash:"1f832b11054143bd938785eb1d38637af72269e88a71466af28264f3fc1e7615"),
                (Rva:0x7ff0c0,Hash:"97282027c575be8ceb146afd72de93a545c11654c085fae4c448c6bf17789a0b"),
                (Rva:0xa8b3f0,Hash:"2231afbec01e5cf6676eb45fa33647f3b9b59aa5ca7b131aa991cd8f57497d18"),
                (Rva:0xa8b2d0,Hash:"97a7832b6a2bb0272ae517a71b1f255841f8f4684fecbd036b6cdad8ffd30237"),
                (Rva:0xaaacb0,Hash:"c99325ae804fa9d34ab8de4c5608f5b099a7c81600d199c60da7f72e9ebf36f2"),
                (Rva:0x56b640,Hash:"e8d8bd0ce1a7ed5934e637d9a01168c8a4ca656bdc030f2bd19eb0390a73425c"),
                (Rva:0xaa96e0,Hash:"7c02784633e9ee20efdfb1cad60c72a1a04cc2d59f76cae69d9c68020f8f9b2e")
            };
            admitted();
            foreach(var entry in entries) ResidentLoadedAssetIndex.RequireCode(engine,entry.Rva,24,entry.Hash);
            const string reserveCode="48895c240848896c24104889742418574883ec400fb641184d8bd1f6d0498be8488bfa488bd9";
            if(Hex(M.Read(IntPtr.Add(engine,0x7feeb0),reserveCode.Length/2))!=reserveCode) throw Error("expired weak array reserve ABI differs");
            if(M.I32(M.Read(IntPtr.Add(engine,0x16de604),4),0)!=31) throw Error("one-shot allocation label differs");
            var vtable=IntPtr.Add(engine,0x16e2108);
            if (M.Ptr(vtable)!=IntPtr.Add(engine,0xaaacb0)) throw Error("expired weak deleting destructor differs");
            var allocate=(WeakAllocate)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine,0x5680a0),typeof(WeakAllocate));
            var construct=(WeakConstruct)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine,0x7ff0c0),typeof(WeakConstruct));
            var assign=(WeakAssign)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine,0xa8b3f0),typeof(WeakAssign));
            var release=(WeakRelease)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine,0xa8b2d0),typeof(WeakRelease));
            var free=(WeakFree)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine,0x56b640),typeof(WeakFree));
            var clearArray=(WeakClearArray)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine,0xaa96e0),typeof(WeakClearArray));
            var file=IntPtr.Add(engine,0x15c6004);
            var backend=new WeakBackend {Vtable=vtable,Assign=(destination,source)=>assign(destination,source),Release=slot=>release(slot),Require=()=>{
                admitted();
                if(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress!=engine) throw Error("expired weak engine identity changed");
            }};
            backend.Reserve=(header,capacity)=>M.Reserve(header,capacity,8,8,admitted);
            backend.ClearArray=header=>clearArray(header);
            backend.AllocateStorage=(bytes,label,line)=>allocate((ulong)bytes,label,8,file,line);
            backend.FreeStorage=(pointer,label)=>free(pointer,label,file,817);
            backend.Create=()=>{
                admitted();var block=allocate(24,31,8,file,77);
                if(block==IntPtr.Zero) throw Error("expired weak allocation failed");
                try {
                    if((block.ToInt64()&7)!=0) throw Error("expired weak allocation alignment differs");
                    construct(block,31);
                    // The base constructor writes VT/label/count (16B), not the typed pointer.
                    // Complete the exact typed 24B object while it is still unpublished.
                    M.Write(block,ExpiredWeakBody(vtable));
                    return block;
                } catch {
                    // No weak slot has adopted this allocation. Its only fields are scalar;
                    // a partial base/typed initialization owns no subordinate resources.
                    try {free(block,31,file,77);}
                    catch(Exception cleanup) {ResidentFailureEvidence.Mark("unpublished expired weak cleanup failed: "+cleanup.GetType().Name);}
                    throw;
                }
            };
            return backend;
        }
        private static byte[] ExpiredWeakBody(IntPtr vtable)
            => BitConverter.GetBytes(vtable.ToInt64()).Concat(BitConverter.GetBytes(31)).Concat(BitConverter.GetBytes(1)).Concat(new byte[8]).ToArray();

        private static ExpiredTopologyPlan PrepareExpiredWeak(Data source,Data before,Func<string,IntPtr> owner,Action admitted)
        {
            RequireExpiredWeakShapes(source,before);
            return PrepareExpiredTopology(source,before,owner,NativeWeakBackend(admitted));
        }
        private static void RequireExpiredWeakShapes(Data source,Data before)
        {
            foreach(var data in new[] {source,before}) {

                if(AllChannels(data).Any(channel=>channel.WeakOwner) && data.Version<4) throw Error("expired weak restoration requires a typed voice schema");
            }
            if(!source.Sources.Select(row=>row.Owner).SequenceEqual(before.Sources.Select(row=>row.Owner))) throw Error("expired weak source directory differs");
        }
        private sealed class ExpiredWeakPlan : IDisposable
        {
            private readonly WeakSlot[] slots;
            private readonly WeakBackend backend;
            private readonly IntPtr[] before;
            private readonly Dictionary<string,IntPtr> anchors=new Dictionary<string,IntPtr>(StringComparer.Ordinal);
            private readonly string[] additionalAliases;
            private bool allocated,published,disposed;
            internal ExpiredWeakPlan(WeakSlot[] slots,WeakBackend backend)
                : this(slots,backend,Array.Empty<string>()) { }
            internal ExpiredWeakPlan(WeakSlot[] slots,WeakBackend backend,string[] additionalAliases)
            {
                this.slots=slots;this.backend=backend;this.additionalAliases=additionalAliases;
                if(slots.Length>Maximum || slots.Any(slot=>slot==null || slot.Address.ToInt64()<=0 || slot.Address.ToInt64()>long.MaxValue-8
                    || (slot.Address.ToInt64()&7)!=0 || !Text(slot.Alias))
                    || slots.Select(slot=>slot.Address).Distinct().Count()!=slots.Length) throw Error("expired weak slots overlap or differ");
                backend.Require();
                before=slots.Select(slot=>M.Ptr(backend.Read(slot.Address,8),0)).ToArray();
                var blocks=before.Where(pointer=>pointer!=IntPtr.Zero).Distinct().OrderBy(pointer=>pointer.ToInt64()).ToArray();
                for(int index=0;index<blocks.Length;index++) {
                    long start=blocks[index].ToInt64();
                    if((start&7)!=0 || start<=0 || start>long.MaxValue-24 || index!=0 && blocks[index-1].ToInt64()+24>start
                        || slots.Any(slot=>slot.Address.ToInt64()<start+24 && slot.Address.ToInt64()+8>start))
                        throw Error("expired weak allocation/slot ownership overlaps");
                }
                foreach(var group in before.Where(pointer=>pointer!=IntPtr.Zero).GroupBy(pointer=>pointer)) {
                    var body=backend.Read(group.Key,24);
                    if(M.Ptr(body,0)!=backend.Vtable || M.I32(body,8)!=31 || M.I32(body,12)<group.Count())
                        throw Error("target expired weak control-block ownership differs");
                }
            }
            private void RequireBefore()
            {
                backend.Require();
                for(int index=0;index<slots.Length;index++) {
                    if(M.Ptr(backend.Read(slots[index].Address,8),0)!=before[index]) throw Error("expired weak slot changed before publication");
                    if(before[index]!=IntPtr.Zero) {
                        var body=backend.Read(before[index],24);
                        if(M.Ptr(body,0)!=backend.Vtable || M.I32(body,8)!=31 || M.I32(body,12)<=0)
                            throw Error("expired weak became live or changed owner");
                    }
                }
            }
            internal void Allocate()
            {
                if(allocated || disposed) throw Error("expired weak allocation is single-use");
                RequireBefore();allocated=true;
                foreach(var alias in slots.Select(slot=>slot.Alias).Concat(additionalAliases).Distinct(StringComparer.Ordinal)) {
                    var anchor=Marshal.AllocHGlobal(8);Marshal.WriteIntPtr(anchor,IntPtr.Zero);anchors.Add(alias,anchor);
                    if(alias.Length==0) continue;
                    backend.Require();var block=backend.Create();
                    // The new count=1 is the temporary anchor's local ownership. Never source count.
                    // Adopt before any potentially failing verification; cleanup uses normal dtor.
                    Marshal.WriteIntPtr(anchor,block);
                    var body=backend.Read(block,24);
                    if(!body.SequenceEqual(ExpiredWeakBody(backend.Vtable))) throw Error("new expired weak construction differs");
                }
            }
            internal void Publish()
            {
                if(!allocated || published || disposed) throw Error("expired weak publication is out of order");
                RequireBefore();published=true;
                foreach(var slot in slots) {
                    AssignPrepared(slot.Address,slot.Alias);
                }
            }
            internal void AssignPrepared(IntPtr destination,string alias)
            {
                    if(!allocated || disposed) throw Error("expired weak assignment precedes allocation");
                    backend.Require();var anchor=anchors[alias];
                    // Assign retains the destination's independent reference before releasing its old one.
                    // An exception/readback failure after this call must only release the anchor.
                    backend.Assign(destination,anchor);
                    if(M.Ptr(backend.Read(destination,8),0)!=Marshal.ReadIntPtr(anchor)) throw Error("expired weak assignment readback differs");
            }
            internal IntPtr Expected(string alias) => Marshal.ReadIntPtr(anchors[alias]);
            internal IntPtr[] Blocks => anchors.Values.Select(pointer=>Marshal.ReadIntPtr(pointer)).Where(pointer=>pointer!=IntPtr.Zero).Distinct().ToArray();
            public void Dispose() => ReleaseAnchors(null);
            internal void ReleaseAnchors(Exception? primary)
            {
                if(disposed) return;disposed=true;Exception? cleanup=null;
                foreach(var anchor in anchors.Values) {
                    try {backend.Release(anchor);} catch(Exception error) {if(cleanup==null) cleanup=error;}
                    finally {Marshal.FreeHGlobal(anchor);}
                }
                anchors.Clear();
                if(cleanup!=null) {
                    if(primary!=null) ResidentFailureEvidence.Mark("expired weak anchor cleanup failed: "+cleanup.GetType().Name);
                    else throw Error("expired weak anchor cleanup failed: "+cleanup.Message);
                }
            }
        }
    }
}
