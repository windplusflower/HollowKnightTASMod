using System;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAnimatorState
    {
        private const int InvalidateBoundCurve = 0x11b6f0, InvalidateSceneTransform = 0x11b760;
        internal static bool IsBinderCallback(int rva) => rva == InvalidateBoundCurve || rva == InvalidateSceneTransform;
        internal static bool IsBinderOwnerVtable(int rva) => rva == AnimatorVtable;
        internal static bool TryBindBinderUserData(int callback, IntPtr receiver, IntPtr userData, Func<IntPtr,string?> identifyAnimator,
            Func<IntPtr,int,byte[]> read, out string key)
        {
            key = "";
            if (!IsBinderCallback(callback) || receiver == IntPtr.Zero || userData.ToInt64() <= 64) return false;
            // Subtracting the inline offset is NOT an identity witness. The
            // caller requires the exact native vtable and live Animator in
            // the directory before this method reads any binder storage.
            var owner = identifyAnimator(IntPtr.Subtract(userData,64));
            if (string.IsNullOrEmpty(owner) || !HasBinderReceiver(callback,receiver,userData,read)) return false;
            key = owner!; return true;
        }
        internal static bool TryResolveBinderUserData(int callback, IntPtr receiver, string key, Func<string,IntPtr> resolveAnimator,
            Func<IntPtr,int,byte[]> read, out IntPtr userData)
        {
            userData = IntPtr.Zero;
            if (!IsBinderCallback(callback) || receiver == IntPtr.Zero || string.IsNullOrEmpty(key)) return false;
            var animator = resolveAnimator(key);
            if (animator == IntPtr.Zero) return false;
            var binder = IntPtr.Add(animator,64);
            if (!HasBinderReceiver(callback,receiver,binder,read)) return false;
            userData = binder; return true;
        }
        private static bool HasBinderReceiver(int callback, IntPtr receiver, IntPtr binder, Func<IntPtr,int,byte[]> read)
        {
            bool Contains(int offset, int stride, int receiverSlot) {
                var header = read(IntPtr.Add(binder,offset),32);
                var count = M.U64(header,16); var capacity = M.U64(header,24) >> 1; var data = M.Ptr(header,0);
                if (count > capacity || capacity > MaximumEntries || capacity != 0 && data == IntPtr.Zero) throw Error("scene handle registry array differs");
                var bytes = read(data,checked((int)count * stride));
                for (int i = 0; i < (int)count; i++) if (M.Ptr(bytes,i * stride + receiverSlot) == receiver) return true;
                return false;
            }
            // Verified against both callback dispatch bodies and UnregisterAnimationSceneHandles:
            // property bound targetObject is +48, the owning scene Transform is +56.
            return callback == InvalidateSceneTransform ? Contains(32,24,0) || Contains(64,72,56) : Contains(64,72,48);
        }
    }
}
