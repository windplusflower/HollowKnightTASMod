using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace HollowKnightTAS.ClockPayload
{
    public static partial class ClockController
    {
        [DllImport("HollowKnightTAS.ClockBridge.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_RequireResidentBoundary(ulong token, ulong expectedFrame);
        [DllImport("HollowKnightTAS.ClockBridge.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_RequireResidentWritableBoundary(ulong token, ulong expectedFrame);

        internal static void RequireResidentClockBoundary(ulong token, bool importing)
        {
            if (token == 0 || !fullRunRenderIsolationEnabled || fullRunRenderEntered
                || GetCurrentThreadId() != (uint)StartupHookThreadId)
                throw new InvalidOperationException("Resident clock operation is outside its owned native boundary.");
            var frame = HktasClockBridge_GetCompletedPlayerLoops();
            if (HktasClockBridge_RequireResidentBoundary(token, frame) != 1
                || importing && HktasClockBridge_RequireResidentWritableBoundary(token, frame) != 1)
                throw new InvalidOperationException("Native resident epoch does not admit this clock operation.");
            RequireClockObserverHealth(importing);
        }

        private static readonly ResidentRandomProducerGate residentRandomProducerGate = new ResidentRandomProducerGate(
            RequireResidentRandomProducerThread, RequireResidentRandomProducerBoundary);

        public static int GetResidentRandomProducerAbi() => 1;
        private static void RequireResidentRandomProducerThread()
        {
            if (Volatile.Read(ref registered) != 1 || StartupHookThreadId == 0 || GetCurrentThreadId() != StartupHookThreadId)
                throw new InvalidOperationException("Random requests require the registered startup engine thread.");
        }
        private static void RequireResidentRandomProducerBoundary(ulong token)
        {
            // Runs on each admitted field/owner check. The gate also checks
            // the startup thread. Full PlayerLoop/clock-observer health is
            // checked at Open, Close and every existing packet export/import.
            if (token == 0 || !fullRunRenderIsolationEnabled || fullRunRenderEntered
                || HktasClockBridge_RequireResidentBoundary(token, HktasClockBridge_GetCompletedPlayerLoops()) != 1)
                throw new InvalidOperationException("Random producer is outside its owned native resident epoch.");
        }
        public static void OpenResidentRandomProducerFence(ulong token)
        {
            try { RequireResidentRandomProducerThread(); RequireResidentClockBoundary(token, false); residentRandomProducerGate.Open(token); }
            catch { residentRandomProducerGate.Fault(); throw; }
        }
        public static void RequireResidentRandomProducerFence(ulong token) => residentRandomProducerGate.Require(token);
        public static void CloseResidentRandomProducerFence(ulong token)
        {
            try { RequireResidentRandomProducerThread(); RequireResidentClockBoundary(token, false); residentRandomProducerGate.Close(token); }
            catch { residentRandomProducerGate.Fault(); throw; }
        }
        public static void AbortResidentRandomProducerFence(ulong token) => residentRandomProducerGate.Abort(token);

        // 0: original root, 1: recording root, 2: full-run scene. The old
        // consumers and named events are retained. No frame or RNG draw occurs.
        public static void RequestResidentRandom(int kind)
        {
            residentRandomProducerGate.Request(() => {
                EventWaitHandle? request;
                switch (kind) {
                    case 0: request = randomSynchronizationRequest; break;
                    case 1: request = recordingRandomSynchronizationRequest; break;
                    case 2:
                        if (!fullRunRenderIsolationEnabled || fullRunRandomAcknowledged == null || fullRunRandomRequest == null)
                            throw new InvalidOperationException("Full-run random request has no actual acknowledgement owner.");
                        if (!fullRunRandomAcknowledged.Reset()) throw new InvalidOperationException("Full-run random acknowledgement could not be reset.");
                        request = fullRunRandomRequest; break;
                    default: throw new ArgumentOutOfRangeException(nameof(kind));
                }
                if (request == null || !request.Set()) throw new InvalidOperationException("Actual random synchronization request is unavailable.");
            });
        }
    }
}
