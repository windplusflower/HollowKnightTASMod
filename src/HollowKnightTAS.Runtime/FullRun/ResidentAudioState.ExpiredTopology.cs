using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAudioState
    {
        private sealed class WeakArrayInput
        {
            internal IntPtr Header;
            internal byte[] Before = Array.Empty<byte>();
            internal string[] Aliases = Array.Empty<string>();
            internal int Capacity;
        }
        private sealed class WeakListInput
        {
            internal IntPtr Head;
            internal byte[] Before = Array.Empty<byte>();
            internal Node[] Old = Array.Empty<Node>();
            internal string[] Aliases = Array.Empty<string>();
        }
        private static ExpiredTopologyPlan PrepareExpiredTopology(Data source,Data before,Func<string,IntPtr> owner,WeakBackend backend)
        {
            if(source.Sources.Sum(row=>1L+row.Cached.Length+row.Providers.Length+row.OneShots.Length)>Maximum
                || before.Sources.Sum(row=>1L+row.Cached.Length+row.Providers.Length+row.OneShots.Length)>Maximum)
                throw Error("expired topology aggregate slot count exceeds bounds");
            var main=new List<WeakSlot>();var arrays=new List<WeakArrayInput>();var lists=new List<WeakListInput>();
            for(int index=0;index<source.Sources.Length;index++) {
                var desired=source.Sources[index];var actual=before.Sources[index];var pointer=owner(desired.Owner);
                main.Add(new WeakSlot {Address=IntPtr.Add(pointer,856),Alias=desired.Main.Alias});
                void ArrayInput(int offset,Channel[] wanted,int capacity,Channel[] old,int oldCapacity) {
                    var header=IntPtr.Add(pointer,offset);var body=backend.Read(header,32);
                    if(M.U64(body,16)!=(ulong)old.Length || M.U64(body,24)>>1!=(ulong)oldCapacity) throw Error("expired array changed after capture");
                    arrays.Add(new WeakArrayInput {Header=header,Before=body,Aliases=wanted.Select(channel=>channel.Alias).ToArray(),Capacity=capacity});
                }
                ArrayInput(760,desired.Cached,desired.CachedCapacity,actual.Cached,actual.CachedCapacity);
                ArrayInput(976,desired.Providers,desired.ProviderCapacity,actual.Providers,actual.ProviderCapacity);
                var head=IntPtr.Add(pointer,792);var headBody=backend.Read(head,16);var current=M.Ptr(headBody,8);var previous=head;var nodes=new List<Node>();var seen=new HashSet<IntPtr>();
                for(int nodeIndex=0;nodeIndex<actual.OneShots.Length;nodeIndex++) {
                    if(current==IntPtr.Zero || current==head || !seen.Add(current)) throw Error("expired one-shot list shape changed");
                    var body=backend.Read(current,32);
                    if(M.Ptr(body,0)!=previous || M.Ptr(body,16)!=current) throw Error("expired one-shot list self/back link differs");
                    nodes.Add(new Node {Pointer=current,Bytes=body});previous=current;current=M.Ptr(body,8);
                }
                if(current!=head || M.Ptr(headBody,0)!=previous) throw Error("expired one-shot list closure differs");
                lists.Add(new WeakListInput {Head=head,Before=headBody,Old=nodes.ToArray(),Aliases=desired.OneShots.Select(channel=>channel.Alias).ToArray()});
            }
            return new ExpiredTopologyPlan(main.ToArray(),arrays.ToArray(),lists.ToArray(),backend);
        }
        private sealed class ExpiredTopologyPlan
        {
            private readonly WeakSlot[] main;
            private readonly WeakArrayInput[] arrays;
            private readonly WeakListInput[] lists;
            private readonly WeakBackend backend;
            private readonly ExpiredWeakPlan values;
            private readonly List<(long Start,long End)> oldRanges=new List<(long,long)>();
            private readonly List<(long Start,long End)> owners=new List<(long,long)>();
            private readonly List<(long Start,long End)> pendingRanges=new List<(long,long)>();
            private readonly List<PendingWeakArray> pendingArrays=new List<PendingWeakArray>();
            private readonly List<PendingWeakList> pendingLists=new List<PendingWeakList>();
            private bool allocated,published,disposed;
            internal ExpiredTopologyPlan(WeakSlot[] main,WeakArrayInput[] arrays,WeakListInput[] lists,WeakBackend backend)
            {
                this.main=main;this.arrays=arrays;this.lists=lists;this.backend=backend;
                var oldSlots=new List<WeakSlot>(main);
                foreach(var slot in main) {AddRange(owners,IntPtr.Subtract(slot.Address,856),1016);AddRange(oldRanges,slot.Address,8);}
                if(arrays.Sum(input=>(long)input.Capacity)*8>32*1024*1024) throw Error("expired topology total reserve exceeds native bounds");
                foreach(var input in arrays) {
                    if(input.Before.Length!=32 || input.Aliases.Length>input.Capacity || input.Capacity<0 || input.Capacity>Maximum) throw Error("expired array desired bounds differ");
                    AddRange(oldRanges,input.Header,32);
                    ulong size=M.U64(input.Before,16),flags=M.U64(input.Before,24),capacity=flags>>1;var storage=M.Ptr(input.Before,0);
                    if(size>capacity || capacity>Maximum || capacity!=0 && storage==IntPtr.Zero || (flags&1)!=0 && (size!=0 || capacity!=0))
                        throw Error("expired target array is borrowed or has invalid ownership");
                    if(storage!=IntPtr.Zero && ((flags&1)==0 || capacity!=0)) AddBacking(storage,checked((int)Math.Max(1UL,capacity)*8));
                    for(int index=0;index<(int)size;index++) oldSlots.Add(new WeakSlot {Address=IntPtr.Add(storage,index*8)});
                }
                foreach(var input in lists) {
                    if(input.Before.Length!=16 || input.Old.Length>Maximum || input.Aliases.Length>Maximum) throw Error("expired one-shot bounds differ");
                    AddRange(oldRanges,input.Head,16);
                    foreach(var node in input.Old) {AddBacking(node.Pointer,32);oldSlots.Add(new WeakSlot {Address=IntPtr.Add(node.Pointer,24)});}
                }
                if(oldSlots.Count>Maximum || arrays.Sum(input=>(long)input.Aliases.Length)+lists.Sum(input=>(long)input.Aliases.Length)+main.Length>Maximum)
                    throw Error("expired topology aggregate exceeds bounds");
                // Reuse the same full block/slot/count preflight over every current owner.
                _=new ExpiredWeakPlan(oldSlots.ToArray(),backend);
                foreach(var pointer in oldSlots.Select(slot=>M.Ptr(backend.Read(slot.Address,8),0)).Where(pointer=>pointer!=IntPtr.Zero).Distinct()) AddBacking(pointer,24);
                var aliases=arrays.SelectMany(input=>input.Aliases).Concat(lists.SelectMany(input=>input.Aliases)).ToArray();
                values=new ExpiredWeakPlan(main,backend,aliases);
                RequireBefore();
            }
            private void AddBacking(IntPtr pointer,int bytes)
            {
                long start=pointer.ToInt64();
                if(owners.Any(range=>start<range.End && start+bytes>range.Start)) throw Error("expired backing storage overlaps a source owner");
                AddRange(oldRanges,pointer,bytes);
            }
            private static void AddRange(List<(long Start,long End)> ranges,IntPtr pointer,int bytes)
            {
                long start=pointer.ToInt64();
                if(bytes<0 || start<=0 || (start&7)!=0 || start>long.MaxValue-bytes || ranges.Any(range=>start<range.End && start+bytes>range.Start))
                    throw Error("expired container ownership ranges overlap or differ");
                if(bytes!=0) ranges.Add((start,start+bytes));
            }
            private void AdmitPending(IntPtr pointer,int bytes)
            {
                long start=pointer.ToInt64();
                if(oldRanges.Concat(owners).Any(range=>start<range.End && start+bytes>range.Start)) throw Error("new expired storage overlaps an existing owner");
                AddRange(pendingRanges,pointer,bytes);
            }
            private void RequireBefore()
            {
                backend.Require();
                foreach(var input in arrays) if(!backend.Read(input.Header,32).SequenceEqual(input.Before)) throw Error("expired array header changed before publication");
                foreach(var input in lists) {
                    if(!backend.Read(input.Head,16).SequenceEqual(input.Before)) throw Error("expired one-shot head changed before publication");
                    foreach(var node in input.Old) if(!backend.Read(node.Pointer,32).SequenceEqual(node.Bytes)) throw Error("expired one-shot node changed before publication");
                }
            }
            internal void Allocate()
            {
                if(allocated || disposed) throw Error("expired topology allocation is single-use");
                RequireBefore();allocated=true;values.Allocate();
                foreach(var pointer in values.Blocks) AdmitPending(pointer,24);
                foreach(var input in arrays) {
                    var pending=new PendingWeakArray(input,backend,values,AdmitPending);pendingArrays.Add(pending);pending.Allocate();
                }
                foreach(var input in lists) {
                    var pending=new PendingWeakList(input,backend,values,AdmitPending);pendingLists.Add(pending);pending.Allocate();
                }
            }
            internal void Publish()
            {
                if(!allocated || published || disposed) throw Error("expired topology publication is out of order");
                RequireBefore();published=true;
                values.Publish();
                foreach(var pending in pendingArrays) pending.Publish();
                foreach(var pending in pendingLists) pending.Publish();
            }
            internal void ReleaseAnchors(Exception? primary)
            {
                if(disposed)return;disposed=true;Exception? cleanup=null;
                void Cleanup(Action action) {try {action();}catch(Exception error){if(cleanup==null)cleanup=error;}}
                foreach(var pending in pendingLists) Cleanup(pending.Dispose);
                foreach(var pending in pendingArrays) Cleanup(pending.Dispose);
                Cleanup(values.Dispose);
                if(cleanup!=null) {
                    if(primary!=null) ResidentFailureEvidence.Mark("expired topology cleanup failed: "+cleanup.GetType().Name);
                    else throw Error("expired topology cleanup failed: "+cleanup.Message);
                }
            }
        }
        private sealed class PendingWeakArray : IDisposable
        {
            private readonly WeakArrayInput input;
            private readonly WeakBackend backend;
            private readonly ExpiredWeakPlan values;
            private readonly Action<IntPtr,int> admit;
            private IntPtr temporary;
            private IntPtr storage;
            private bool adopted;
            private bool cleanupSafe;
            internal PendingWeakArray(WeakArrayInput input,WeakBackend backend,ExpiredWeakPlan values,Action<IntPtr,int> admit)
            {this.input=input;this.backend=backend;this.values=values;this.admit=admit;}
            internal void Allocate()
            {
                temporary=Marshal.AllocHGlobal(32);
                var header=new byte[32];Array.Copy(input.Before,8,header,8,8);BitConverter.GetBytes(1UL).CopyTo(header,24);
                backend.Write(temporary,header);backend.Require();backend.Reserve(temporary,input.Capacity);
                header=backend.Read(temporary,32);
                if(M.U64(header,16)!=0 || M.U64(header,24)>>1!=(ulong)input.Capacity || M.I32(header,8)!=M.I32(input.Before,8)) throw Error("pending expired array reservation differs");
                var data=M.Ptr(header,0);storage=data;
                if(input.Capacity!=0) {
                    if((M.U64(header,24)&1)!=0)throw Error("pending expired array does not own its data");
                    admit(data,checked(input.Capacity*8));cleanupSafe=true;backend.Write(data,new byte[input.Capacity*8]);
                }
                // Every logical slot is already a constructed null WeakPtr before size becomes visible to cleanup.
                cleanupSafe=false;backend.Write(IntPtr.Add(temporary,16),BitConverter.GetBytes((ulong)input.Aliases.Length));cleanupSafe=true;
                for(int index=0;index<input.Aliases.Length;index++) values.AssignPrepared(IntPtr.Add(data,index*8),input.Aliases[index]);
            }
            internal void Publish()
            {
                backend.Require();var body=backend.Read(temporary,32);
                if(M.Ptr(body,0)!=storage || M.U64(body,16)!=(ulong)input.Aliases.Length || M.U64(body,24)>>1!=(ulong)input.Capacity
                    || M.I32(body,8)!=M.I32(input.Before,8))throw Error("pending expired array changed before publication");
                for(int index=0;index<input.Aliases.Length;index++)
                    if(M.Ptr(backend.Read(IntPtr.Add(storage,index*8),8),0)!=values.Expected(input.Aliases[index]))throw Error("pending array weak alias differs");
                if(!backend.Read(input.Header,32).SequenceEqual(input.Before))throw Error("expired array changed at publication");
                // M.Write may have published the pointer before a failing readback. Never free it after this boundary.
                adopted=true;backend.Write(input.Header,body);
                if(!backend.Read(input.Header,32).SequenceEqual(body))throw Error("expired array publication differs");
                var retired=Marshal.AllocHGlobal(32);
                try {Marshal.Copy(input.Before,0,retired,32);backend.ClearArray(retired);}
                finally {Marshal.FreeHGlobal(retired);}
            }
            public void Dispose()
            {
                if(temporary==IntPtr.Zero)return;
                var pointer=temporary;temporary=IntPtr.Zero;
                try {if(!adopted && cleanupSafe)backend.ClearArray(pointer);}
                finally {Marshal.FreeHGlobal(pointer);}
            }
        }
        private sealed class PendingWeakList : IDisposable
        {
            private readonly WeakListInput input;
            private readonly WeakBackend backend;
            private readonly ExpiredWeakPlan values;
            private readonly Action<IntPtr,int> admit;
            private readonly List<IntPtr> nodes=new List<IntPtr>();
            private readonly HashSet<IntPtr> initialized=new HashSet<IntPtr>();
            private bool adopted;
            internal PendingWeakList(WeakListInput input,WeakBackend backend,ExpiredWeakPlan values,Action<IntPtr,int> admit)
            {this.input=input;this.backend=backend;this.values=values;this.admit=admit;}
            internal void Allocate()
            {
                foreach(var alias in input.Aliases) {
                    backend.Require();var pointer=backend.AllocateStorage(32,31,2255);
                    if(pointer==IntPtr.Zero)throw Error("pending one-shot allocation failed");
                    admit(pointer,32);nodes.Add(pointer);
                    var body=new byte[32];BitConverter.GetBytes(pointer.ToInt64()).CopyTo(body,16);backend.Write(pointer,body);
                    initialized.Add(pointer);
                    values.AssignPrepared(IntPtr.Add(pointer,24),alias);
                }
                for(int index=0;index<nodes.Count;index++) backend.Write(nodes[index],Links(index==0?input.Head:nodes[index-1],index+1==nodes.Count?input.Head:nodes[index+1]));
                RequirePending();
            }
            private static byte[] Links(IntPtr previous,IntPtr next)=>BitConverter.GetBytes(previous.ToInt64()).Concat(BitConverter.GetBytes(next.ToInt64())).ToArray();
            private void RequirePending()
            {
                for(int index=0;index<nodes.Count;index++) {
                    var body=backend.Read(nodes[index],32);
                    if(M.Ptr(body,0)!=(index==0?input.Head:nodes[index-1]) || M.Ptr(body,8)!=(index+1==nodes.Count?input.Head:nodes[index+1]) || M.Ptr(body,16)!=nodes[index])
                        throw Error("pending one-shot node closure differs");
                    var weak=M.Ptr(body,24);
                    if(weak!=values.Expected(input.Aliases[index])) throw Error("pending one-shot weak alias differs");
                    if(weak!=IntPtr.Zero) {
                        var control=backend.Read(weak,24);
                        if(M.Ptr(control,0)!=backend.Vtable || M.Ptr(control,16)!=IntPtr.Zero || M.I32(control,8)!=31 || M.I32(control,12)<=0)
                            throw Error("pending one-shot weak type/lifetime differs");
                    }
                }
            }
            internal void Publish()
            {
                backend.Require();RequirePending();
                if(!backend.Read(input.Head,16).SequenceEqual(input.Before))throw Error("one-shot owner head changed at publication");
                var expected=Links(nodes.Count==0?input.Head:nodes[nodes.Count-1],nodes.Count==0?input.Head:nodes[0]);
                adopted=true;backend.Write(input.Head,expected);
                if(!backend.Read(input.Head,16).SequenceEqual(expected))throw Error("one-shot owner publication differs");
                RequirePending();
                // The complete old chain is detached. Never unlink through its old head links:
                // those would overwrite the newly published sentinel. Retire captured nodes once.
                foreach(var node in input.Old) {backend.Release(IntPtr.Add(node.Pointer,24));backend.FreeStorage(node.Pointer,31);}
            }
            public void Dispose()
            {
                if(adopted)return;Exception? failure=null;
                foreach(var pointer in nodes) {
                    try {if(initialized.Contains(pointer))backend.Release(IntPtr.Add(pointer,24));backend.FreeStorage(pointer,31);}
                    catch(Exception error) {if(failure==null)failure=error;}
                }
                nodes.Clear();if(failure!=null)throw failure;
            }
        }
    }
}
