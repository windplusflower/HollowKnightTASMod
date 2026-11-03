using System;
using System.IO;
using HollowKnightTAS.Runtime.FullRun;
using System.Runtime.InteropServices;

namespace HollowKnightTAS.ClockPayload
{
    public static partial class ClockController
    {
        [DllImport("HollowKnightTAS.ClockBridge.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_VerifyResidentDirectorQueues(ulong token);

        [DllImport("HollowKnightTAS.ClockBridge.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_CopyResidentDirectorFenceStatus([Out] byte[] bytes, uint count);

        // Process-local admission evidence only. The epoch, thread IDs and
        // counters must never enter a portable world digest or a source D.
        public static byte[] InspectResidentDirectorFence(ulong token)
        {
            RequireResidentClockBoundary(token, false);
            if (HktasClockBridge_VerifyResidentDirectorQueues(token) != 1)
                throw new InvalidDataException("Director queue or native admission evidence changed within this epoch.");
            var status = new byte[ResidentDirectorFence.Bytes];
            if (HktasClockBridge_CopyResidentDirectorFenceStatus(status, (uint)status.Length) != status.Length)
                throw new InvalidDataException("Director producer status is unavailable.");
            _ = ResidentDirectorFence.Read(status, token, null, GetCurrentThreadId(), out _);
            RequireResidentClockBoundary(token, false);
            return status;
        }
    }
}
