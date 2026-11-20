using System;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Route observation is not D0 acceptance or permission to capture/restore.
    // It is deliberately restricted to the protected loading-probe harness.
    internal static class CanonicalPhaseOneTrace
    {
        internal const string Variable = "HKTAS_LOADING_CANONICAL_PHASE1_TRACE";
        internal const string D0RouteVariable = "HKTAS_LOADING_CANONICAL_D0_ROUTE";
        internal const string DiscoveryVariable = "HKTAS_LOADING_CANONICAL_ROUTE_DISCOVERY";
        internal static bool Enabled => Environment.GetEnvironmentVariable(Variable) == "1";
        internal static bool D0RouteEnabled => Environment.GetEnvironmentVariable(D0RouteVariable) == "1";
        internal static bool DiscoveryEnabled => Environment.GetEnvironmentVariable(DiscoveryVariable) == "1";

        internal static void RequireContext(Func<string, string?> read)
        {
            var d0Route = read(D0RouteVariable) == "1";
            var discovery = read(DiscoveryVariable) == "1";
            if (d0Route && read(Variable) != "1") throw new InvalidOperationException("D0 route requires its complete Phase 1 observer.");
            if (discovery && (read(Variable) != "1" || d0Route))
                throw new InvalidOperationException("Input route authoring requires its explicit unqualified Phase 1 context.");
            if (read(Variable) != "1") return;
            foreach (var name in new[] { "HKTAS_LOADING_CANONICAL_BOUNDARY", "HKTAS_LOADING_GAMEPLAY_ORACLE",
                "HKTAS_LOADING_NATIVE_SCHEDULER", "HKTAS_LOADING_NATIVE_PHYSICS", "HKTAS_LOADING_SERIAL_BOUNDARY_ONLY" })
                if (read(name) != "1") throw new InvalidOperationException("Phase 1 route trace requires " + name + ".");
            if (string.IsNullOrWhiteSpace(read("HKTAS_LOADING_PROBE_OUTPUT")))
                throw new InvalidOperationException("Phase 1 route trace requires its protected diagnostic directory.");
            if (d0Route || discovery)
                foreach (var name in new[] { "HKTAS_LOADING_S0_OBSERVING", "HKTAS_LOADING_ACTUAL_D_EXPERIMENT", "HKTAS_LOADING_CLOCK_APPLY_EXPERIMENT" })
                    if (read(name) == "1") throw new InvalidOperationException("D0 route cannot run with " + name + ".");
        }

        internal static void RequireCheckpointAllowed(bool routeTrace)
        {
            if (routeTrace) throw new InvalidOperationException("Phase 1 route trace cannot capture, import or restore a checkpoint.");
        }
    }
}
