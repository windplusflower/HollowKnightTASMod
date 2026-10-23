using System;
using System.Linq;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalPhaseOneTraceTests
{
    [TestMethod]
    public void TraceRejectsEachMissingObservationAndOutput()
    {
        var required = new[] { "HKTAS_LOADING_CANONICAL_BOUNDARY", "HKTAS_LOADING_GAMEPLAY_ORACLE",
            "HKTAS_LOADING_NATIVE_SCHEDULER", "HKTAS_LOADING_NATIVE_PHYSICS", "HKTAS_LOADING_SERIAL_BOUNDARY_ONLY",
            "HKTAS_LOADING_PROBE_OUTPUT" };
        string? Read(string name) => name == "HKTAS_LOADING_PROBE_OUTPUT" ? "protected-probe"
            : name == CanonicalPhaseOneTrace.Variable || required.Contains(name) ? "1" : null;
        CanonicalPhaseOneTrace.RequireContext(Read);
        foreach (var missing in required)
            Assert.ThrowsExactly<InvalidOperationException>(() => CanonicalPhaseOneTrace.RequireContext(
                name => name == missing ? null : Read(name)), missing);
        CanonicalPhaseOneTrace.RequireContext(_ => null);
    }

    [TestMethod]
    public void TraceAlwaysRejectsCheckpointOperations()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => CanonicalPhaseOneTrace.RequireCheckpointAllowed(true));
        CanonicalPhaseOneTrace.RequireCheckpointAllowed(false);
    }

    [TestMethod]
    public void D0RouteRejectsPreparationAndMutationModesButPreservesDefaultTrace()
    {
        string? Read(string name) => name == "HKTAS_LOADING_PROBE_OUTPUT" ? "protected-probe"
            : name == CanonicalPhaseOneTrace.D0RouteVariable || name == CanonicalPhaseOneTrace.Variable
                || new[] { "HKTAS_LOADING_CANONICAL_BOUNDARY", "HKTAS_LOADING_GAMEPLAY_ORACLE", "HKTAS_LOADING_NATIVE_SCHEDULER",
                    "HKTAS_LOADING_NATIVE_PHYSICS", "HKTAS_LOADING_SERIAL_BOUNDARY_ONLY" }.Contains(name) ? "1" : null;
        CanonicalPhaseOneTrace.RequireContext(Read);
        Assert.ThrowsExactly<InvalidOperationException>(() => CanonicalPhaseOneTrace.RequireContext(
            name => name == CanonicalPhaseOneTrace.Variable ? null : Read(name)));
        foreach (var forbidden in new[] { "HKTAS_LOADING_S0_OBSERVING", "HKTAS_LOADING_ACTUAL_D_EXPERIMENT", "HKTAS_LOADING_CLOCK_APPLY_EXPERIMENT" }) {
            Assert.ThrowsExactly<InvalidOperationException>(() => CanonicalPhaseOneTrace.RequireContext(name => name == forbidden ? "1" : Read(name)), forbidden);
            CanonicalPhaseOneTrace.RequireContext(name => name == CanonicalPhaseOneTrace.D0RouteVariable ? null : name == forbidden ? "1" : Read(name));
        }
    }
}
