using System;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Route observation is not D0 acceptance or permission to capture/restore.
    // It is deliberately restricted to the protected loading-probe harness.
    internal static class CanonicalPhaseOneTrace
    {
        internal const string Variable = "HKTAS_LOADING_CANONICAL_PHASE1_TRACE";
        internal static bool Enabled => Environment.GetEnvironmentVariable(Variable) == "1";

        internal static void RequireContext(Func<string, string?> read)
        {
            if (read(Variable) != "1") return;
            foreach (var name in new[] { "HKTAS_LOADING_CANONICAL_BOUNDARY", "HKTAS_LOADING_GAMEPLAY_ORACLE",
                "HKTAS_LOADING_NATIVE_SCHEDULER", "HKTAS_LOADING_NATIVE_PHYSICS", "HKTAS_LOADING_SERIAL_BOUNDARY_ONLY" })
                if (read(name) != "1") throw new InvalidOperationException("Phase 1 route trace requires " + name + ".");
            if (string.IsNullOrWhiteSpace(read("HKTAS_LOADING_PROBE_OUTPUT")))
                throw new InvalidOperationException("Phase 1 route trace requires its protected diagnostic directory.");
        }

        internal static void RequireCheckpointAllowed(bool routeTrace)
        {
            if (routeTrace) throw new InvalidOperationException("Phase 1 route trace cannot capture, import or restore a checkpoint.");
        }
    }
}
