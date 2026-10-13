using System;
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
        string? Read(string name) => name == "HKTAS_LOADING_PROBE_OUTPUT" ? "protected-probe" : "1";
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
}
