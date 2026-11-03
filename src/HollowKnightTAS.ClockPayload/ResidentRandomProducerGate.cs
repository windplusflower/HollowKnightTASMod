using System;
using System.Threading;

namespace HollowKnightTAS.ClockPayload
{
    // One authority for every in-repository RNG request producer. The injected
    // guards make the state machine independently testable without a PlayerLoop.
    internal sealed class ResidentRandomProducerGate
    {
        private readonly object sync = new object();
        private readonly Action requireThread;
        private readonly Action<ulong> requireBoundary;
        private ulong activeToken, lastToken;
        private int depth;
        private bool failed;

        internal ResidentRandomProducerGate(Action requireThread, Action<ulong> requireBoundary)
        {
            this.requireThread = requireThread ?? throw new ArgumentNullException(nameof(requireThread));
            this.requireBoundary = requireBoundary ?? throw new ArgumentNullException(nameof(requireBoundary));
        }
        internal void Request(Action signal)
        {
            if (signal == null) throw new ArgumentNullException(nameof(signal));
            try {
                requireThread();
                lock (sync) {
                    RequireHealthy();
                    if (activeToken != 0 || depth != 0) throw Error("request attempted inside a resident fence or another producer");
                    depth = 1;
                    try { signal(); RequireHealthy(); }
                    finally { depth = 0; }
                }
            } catch { Fault(); throw; }
        }
        internal void Open(ulong token)
        {
            try {
                requireThread(); requireBoundary(token);
                lock (sync) {
                    RequireHealthy();
                    if (token == 0 || token <= lastToken || activeToken != 0 || depth != 0) throw Error("fence is stale, nested or raced an active producer");
                    Volatile.Write(ref lastToken, token); Volatile.Write(ref activeToken, token);
                }
                requireBoundary(token);
            } catch { Fault(); throw; }
        }
        internal void Require(ulong token)
        {
            try {
                requireThread(); requireBoundary(token);
                lock (sync) {
                    RequireHealthy();
                    if (token == 0 || activeToken != token || lastToken != token || depth != 0) throw Error("current producer fence identity differs");
                }
                requireBoundary(token);
            } catch { Fault(); throw; }
        }
        internal void Close(ulong token)
        {
            try {
                Require(token);
                lock (sync) { RequireHealthy(); Volatile.Write(ref activeToken, 0UL); }
            } catch { Fault(); throw; }
        }
        internal void Abort(ulong token)
        {
            try {
                requireThread();
                if (token == 0 || Volatile.Read(ref activeToken) != token && Volatile.Read(ref lastToken) != token)
                    throw Error("aborted producer fence is not the current or last epoch");
            } finally { Fault(); }
        }
        private void RequireHealthy() { if (Volatile.Read(ref failed)) throw Error("producer authority is permanently faulted; retire this process"); }
        internal void Fault() => Volatile.Write(ref failed, true);
        private static InvalidOperationException Error(string message) => new InvalidOperationException("Resident random producer: " + message);
    }
}
