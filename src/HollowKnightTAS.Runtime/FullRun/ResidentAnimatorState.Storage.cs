using System;
using System.Runtime.InteropServices;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAnimatorState
    {
        private static bool OwnedStorage(Storage s) => !(s.Borrowed && s.Allocated) && !(s.Allocated && s.Capacity==0);
        private static bool Compact(Storage s,int count) => OwnedStorage(s) && s.Capacity==count && s.Allocated==(count!=0) && s.Borrowed==(count==0) && !s.Padding;
        private static void ResizePod(IntPtr header,int count,int stride,int alignment,int label,Action admitted)
        {
            admitted(); var bytes=M.Read(header,32);var old=StorageAt(bytes,0);var previous=M.U64(bytes,16);
            if (!OwnedStorage(old) || old.Label!=label || previous>(ulong)old.Capacity || count<0 || count>MaximumEntries) throw Error("POD vector ownership/size differs");
            if (count==0) {
                if (old.Allocated) {
                    var function=IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress,0xb32f0);
                    if (BitConverter.ToString(M.Read(function,16)).Replace("-","").ToLowerInvariant()!="40534883ec20488bd9488b094885c974") throw Error("POD vector deallocator ABI differs");
                    ((DestroyBinderArray)Marshal.GetDelegateForFunctionPointer(function,typeof(DestroyBinderArray)))(header);
                    if (M.Ptr(header)!=IntPtr.Zero) throw Error("POD vector allocation did not retire");
                }
                var empty=new byte[32];BitConverter.GetBytes(label).CopyTo(empty,8);empty[24]=1;M.Write(header,empty);
            } else {
                if (previous>(ulong)count) M.Write(IntPtr.Add(header,16),BitConverter.GetBytes((ulong)count));
                if (old.Capacity!=count || old.Borrowed) M.Reserve(header,count,stride,alignment,admitted);
                M.Write(IntPtr.Add(header,12),new byte[4]); M.Write(IntPtr.Add(header,16),BitConverter.GetBytes((ulong)count));
                if (!Compact(StorageAt(M.Read(header,32),0),count)) throw Error("POD vector did not reach canonical extent");
            }
            admitted();
        }
        private static void CanonResidual(Residual r,Row row,IntPtr owner,Action admitted)
        {
            var lengths=ResidualCounts(r,row);
            for (int i=0;i<lengths.Length;i++) if (!Compact(r.Arrays[i],lengths[i])) {
                ResizePod(IntPtr.Add(owner,ResidualArrays[i]),lengths[i],ResidualStrides[i],Math.Min(8,ResidualStrides[i]),r.Arrays[i].Label,admitted);
                SetCompact(r.Arrays[i],lengths[i]);
            }
            CanonFrames(r,IntPtr.Add(owner,1416),admitted);
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ResizeFrameVector(IntPtr vector,UIntPtr count,IntPtr valueInitTag);
        private static void CanonFrames(Residual data,IntPtr header,Action admitted)
        {
            int count=data.Frames.Length;
            if(data.FrameCapacity==count && !data.FramePadding) return;
            admitted();var before=M.Read(header,24);var first=M.Ptr(before,0);var storage=M.Read(first,checked(count*24));
            if(M.Ptr(before,8).ToInt64()-first.ToInt64()!=storage.Length || M.Ptr(before,16).ToInt64()-first.ToInt64()!=data.FrameCapacity*24L)
                throw Error("recorder vector changed before Canon");
            for(int i=20;i<storage.Length;i+=24) Array.Clear(storage,i,4);
            if(data.FrameCapacity==count) M.Write(first,storage);
            else {
                // AvatarPlayback::Clear destroys both blob allocations in
                // every live frame. The vector destructor alone frees just
                // the backing vector, so transfer the local blob pointers
                // into a fresh exact-sized vector before retiring the old one.
                if(data.FrameCapacity*24L>=4096) {
                    var allocation=M.Ptr(IntPtr.Subtract(first,8));long gap=first.ToInt64()-allocation.ToInt64()-8;
                    if(gap<0 || gap>31 || (first.ToInt64()&31)!=0) throw Error("MSVC frame vector has invalid aligned allocation metadata");
                }
                var engine=LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
                var resize=IntPtr.Add(engine,0xbe3a0);var destroy=IntPtr.Add(engine,0xbf050);
                string Prefix(IntPtr x)=>BitConverter.ToString(M.Read(x,16)).Replace("-","").ToLowerInvariant();
                if(Prefix(resize)!="48895c24104889742418574883ec204c" || Prefix(destroy)!="40534883ec20488bd9488b094885c974") throw Error("recorder vector allocator ABI differs");
                var dt=(DestroyBinderArray)Marshal.GetDelegateForFunctionPointer(destroy,typeof(DestroyBinderArray));
                var temp=Marshal.AllocHGlobal(32);bool transferStarted=false,initialized=false;
                try {
                    M.Write(temp,new byte[32]);initialized=true;
                    if(count!=0) ((ResizeFrameVector)Marshal.GetDelegateForFunctionPointer(resize,typeof(ResizeFrameVector)))(temp,new UIntPtr((uint)count),IntPtr.Add(temp,24));
                    var next=M.Read(temp,24);var dest=M.Ptr(next,0);
                    if(M.Ptr(next,8)!=M.Ptr(next,16) || M.Ptr(next,8).ToInt64()-dest.ToInt64()!=storage.Length || dest==first && count!=0)
                        throw Error("new native recorder vector lacks exact capacity");
                    M.Write(dest,storage);admitted();transferStarted=true;M.Write(header,next);M.Write(temp,before);dt(temp);
                } finally {
                    // After publication begins the outer scope must retire
                    // the process on a failed write, never free a live blob.
                    if(initialized && !transferStarted) dt(temp); Marshal.FreeHGlobal(temp);
                }
            }
            data.FrameAllocated=count!=0;data.FrameCapacity=count;data.FramePadding=false;admitted();
        }
        private static void SetCompact(Storage s,int count)
        { s.Capacity=count; s.Borrowed=count==0; s.Allocated=count!=0; s.Padding=false; }
    }
}
