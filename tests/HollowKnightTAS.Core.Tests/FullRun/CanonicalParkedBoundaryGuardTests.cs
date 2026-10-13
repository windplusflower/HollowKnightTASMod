using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HollowKnightTAS.Runtime.FullRun;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    public sealed class CanonicalParkedBoundaryGuardTests
    {
        [TestMethod]
        public void SameOriginalParkedBoundaryIsAccepted()
        { var owner = new object(); new CanonicalParkedBoundaryGuard(42, 2, owner, "A", "B", "bits").Require(42, true, 2, owner, "A", "B", "bits"); }
        [TestMethod]
        public void StepThenRepauseCannotReuseBoundary()
        { var owner = new object(); var guard = new CanonicalParkedBoundaryGuard(42, 2, owner, "A", "B", "bits"); Assert.ThrowsExactly<InvalidOperationException>(() => guard.Require(43, true, 2, owner, "A", "B", "bits")); }
        [TestMethod]
        public void ResumeCannotReuseSameCursor()
        { var owner = new object(); var guard = new CanonicalParkedBoundaryGuard(42, 2, owner, "A", "B", "bits"); Assert.ThrowsExactly<InvalidOperationException>(() => guard.Require(42, false, 2, owner, "A", "B", "bits")); }
        [TestMethod]
        public void AnotherLoadOwnerCannotReuseCursor()
        { var owner = new object(); var guard = new CanonicalParkedBoundaryGuard(42, 2, owner, "A", "B", "bits"); Assert.ThrowsExactly<InvalidOperationException>(() => guard.Require(42, true, 2, new object(), "A", "B", "bits")); }
        [TestMethod]
        public void MutatedLoadFieldsCannotReuseBoundary()
        { var owner = new object(); var guard = new CanonicalParkedBoundaryGuard(42, 2, owner, "A", "B", "bits"); Assert.ThrowsExactly<InvalidOperationException>(() => guard.Require(42, true, 2, owner, "A", "B", "changed")); }
        [TestMethod]
        public void AnotherSceneOrOrdinalCannotReuseBoundary()
        { var owner = new object(); var guard = new CanonicalParkedBoundaryGuard(42, 2, owner, "A", "B", "bits"); Assert.ThrowsExactly<InvalidOperationException>(() => guard.Require(42, true, 3, owner, "A", "C", "bits")); }
        [TestMethod]
        public void ParkedJobAlwaysRejectsPlayerLoop()
        { Assert.ThrowsExactly<InvalidOperationException>(CanonicalParkedBoundaryGuard.RejectPlayerLoopAdvance); }
        private enum Direction { Zero, Right }
        [TestMethod]
        public void OriginalNullableEnumKeepsNullAndZeroDistinct()
        { Assert.AreNotEqual(CanonicalParkedBoundaryGuard.EncodeScalarValue(null, typeof(Direction?)), CanonicalParkedBoundaryGuard.EncodeScalarValue(Direction.Zero, typeof(Direction?))); }
        [TestMethod]
        public void FloatLoadDelayKeepsSignedZeroBits()
        { Assert.AreNotEqual(CanonicalParkedBoundaryGuard.EncodeScalarValue(0f, typeof(float)), CanonicalParkedBoundaryGuard.EncodeScalarValue(-0f, typeof(float))); }
        [TestMethod]
        public void NullableNonEnumDoesNotBroadenOriginalContract()
        { Assert.ThrowsExactly<ArgumentException>(() => CanonicalParkedBoundaryGuard.EncodeScalarValue(1f, typeof(float?))); }
    }
}
