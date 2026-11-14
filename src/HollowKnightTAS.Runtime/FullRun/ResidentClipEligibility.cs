using System;
using System.Runtime.InteropServices;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class ResidentClipEligibility
    {
        [DllImport("HollowKnightTAS.ClockBridge.dll",CallingConvention=CallingConvention.Cdecl,ExactSpelling=true,EntryPoint="HktasClockBridge_QueryResidentClipCurveEligibility")]
        private static extern int QueryResidentClipCurveEligibility(ulong token,IntPtr clip,int instanceId,IntPtr receipt,uint bytes);
        internal static bool IsClean(AnimationClip owner)
        {
            ulong token = ResidentClockBackend.RequireClipToken();
            var pointer = M.Owner(owner);
            int id = owner.GetInstanceID();
            if (ResidentLoadedAssetIndex.RegisteredPointer(id,out var verify) != pointer) return false;
            var receipt = new byte[64];
            var pinned = GCHandle.Alloc(receipt,GCHandleType.Pinned);
            try {
                int result = QueryResidentClipCurveEligibility(token,pointer,id,pinned.AddrOfPinnedObject(),64);
                verify();
                return IsCleanReceipt(result,receipt) && M.U64(receipt,24) == token;
            } finally { pinned.Free(); }
        }
        internal static bool IsCleanReceipt(int result,byte[] receipt)
        {
            return result == 1 && receipt != null && receipt.Length == 64
                && M.U32(receipt,0) == 0x314c4343 && M.U32(receipt,4) == 1 && M.U32(receipt,8) == 64
                && M.U32(receipt,12) == 7 && M.U64(receipt,16) != 0 && M.U64(receipt,24) != 0
                && M.U64(receipt,32) == M.U64(receipt,40) && M.I32(receipt,48) == 0
                && M.U32(receipt,52) == 0 && M.U32(receipt,56) == 1 && M.U32(receipt,60) == 1;
        }
    }
}
