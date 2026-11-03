using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;

namespace HollowKnightTAS.ClockPayload
{
    public static partial class ClockController
    {
        [DllImport("HollowKnightTAS.ClockBridge.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_RequireResidentFrameObservation(ulong token, ulong expectedFrame);

        private static ulong RequireFrameObservation(ulong token)
        {
            RequireResidentRandomProducerThread();
            var frame = HktasClockBridge_GetCompletedPlayerLoops();
            if (token == 0 || HktasClockBridge_RequireResidentFrameObservation(token, frame) != 1)
                throw new InvalidOperationException("Clock observation requires the current read-only native frame phase.");
            return frame;
        }
        public static void OpenResidentFrameProducerFence(ulong token)
        {
            try { RequireFrameObservation(token); RequireOriginalTimeUpdateHook(); residentRandomProducerGate.Open(token); }
            catch { residentRandomProducerGate.Fault(); throw; }
        }
        public static void CloseResidentFrameProducerFence(ulong token)
        {
            try { RequireFrameObservation(token); RequireOriginalTimeUpdateHook(); residentRandomProducerGate.Close(token); }
            catch { residentRandomProducerGate.Fault(); throw; }
        }
        public static byte[] ExportResidentFrameRandomState(ulong token)
        {
            RequireFrameObservation(token); residentRandomProducerGate.Require(token);
            var bytes = CapturePortableRandomState();
            RequireFrameObservation(token); return bytes;
        }
        public static byte[] ExportResidentFrameClockObserver(ulong token)
        {
            var frame = RequireFrameObservation(token); residentRandomProducerGate.Require(token);
            var state = CaptureClockObserver(); var native = new byte[1192];
            if (frame > long.MaxValue || HktasClockBridge_ExportResidentFrameClock(token, native, (uint)native.Length) != 1)
                throw new InvalidDataException("Native clock cannot be observed at this frame.");
            state.RequireFrameNativeReadback(native, (long)frame, Time.frameCount);
            state.RequireReadback(CaptureClockObserver()); RequireFrameObservation(token); return state.Encode();
        }
        [DllImport("HollowKnightTAS.ClockBridge.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_ExportResidentFrameClock(ulong token, [Out] byte[] output, uint bytes);
        public static uint[] ExportResidentFrameParticleRandomState(ulong token)
        {
            RequireFrameObservation(token); residentRandomProducerGate.Require(token);
            var words = EarlyModWallClockBootstrap.CaptureParticleRandomState();
            if (words.Length != 4 || words.All(word => word == 0)) throw new InvalidDataException("Actual particle RNG state is unavailable.");
            RequireFrameObservation(token); return words;
        }
    }
}
