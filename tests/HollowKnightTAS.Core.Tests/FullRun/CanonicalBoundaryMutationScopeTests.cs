using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HollowKnightTAS.Runtime.FullRun;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    public sealed class CanonicalBoundaryMutationScopeTests
    {
        [TestMethod]
        public void FirstMutationBeforeAdmissionIsRejected()
        { var calls = 0; using var scope = new CanonicalBoundaryMutationScope(() => calls++); Assert.ThrowsExactly<InvalidOperationException>(scope.Require); Assert.AreEqual(0, calls); }
        [TestMethod]
        public void EnterAndEveryMutationRevalidateActualBoundary()
        { var native = 42; using var scope = new CanonicalBoundaryMutationScope(() => { if (native != 42) throw new InvalidOperationException(); }); scope.Enter(); scope.Require(); native++; Assert.ThrowsExactly<InvalidOperationException>(scope.Require); native = 42; Assert.ThrowsExactly<InvalidOperationException>(scope.Require); }
        [TestMethod]
        public void ReentryAndRetiredScopeCannotBeReused()
        { var scope = new CanonicalBoundaryMutationScope(() => { }); scope.Enter(); Assert.ThrowsExactly<InvalidOperationException>(scope.Enter); scope.Dispose(); Assert.ThrowsExactly<InvalidOperationException>(scope.Require); Assert.ThrowsExactly<InvalidOperationException>(scope.Enter); }
        [TestMethod]
        public void FailedInitialAdmissionNeverOpensMutation()
        { using var scope = new CanonicalBoundaryMutationScope(() => throw new InvalidOperationException()); Assert.ThrowsExactly<InvalidOperationException>(scope.Enter); Assert.ThrowsExactly<InvalidOperationException>(scope.Require); }
    }
}
