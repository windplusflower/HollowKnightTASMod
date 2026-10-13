using System;
using System.IO;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    public sealed class CanonicalFsmTimeCarryTests
    {
        private static CanonicalFsmTimeCarry.Data State(bool first = true, float offset = 2, float last = 4, int frame = 10) => new CanonicalFsmTimeCarry.Data {
            ModuleVersionId = "29eb59fa-5c99-43cd-8f33-6db074533d31", FirstUpdateHasHappened = first,
            TotalEditorPlayerPausedTimeBits = CanonicalFsmTimeCarry.Bits(offset), RealtimeLastUpdateBits = CanonicalFsmTimeCarry.Bits(last), FrameCountLastUpdate = frame };
        [TestMethod]
        public void PureRealtimeMatchesBothOriginalGetterBranchesWithoutChangingState()
        {
            var state = State(offset: 5);
            Assert.AreEqual(CanonicalFsmTimeCarry.Bits(3), CanonicalFsmTimeCarry.ComputeRealtimeBits(state, 8));
            Assert.AreEqual(CanonicalFsmTimeCarry.Bits(3), CanonicalFsmTimeCarry.ComputeRealtimeBits(state, 3));
            Assert.AreEqual(CanonicalFsmTimeCarry.Bits(5), state.TotalEditorPlayerPausedTimeBits);
            state.FirstUpdateHasHappened = false;
            Assert.AreEqual(0u, CanonicalFsmTimeCarry.ComputeRealtimeBits(state, 8));
            Assert.AreEqual(CanonicalFsmTimeCarry.Bits(5), state.TotalEditorPlayerPausedTimeBits);
        }
        [TestMethod]
        public void RawSignedZeroAndSingleFloatBitsRoundTripExactly()
        {
            var source = State(offset: -0.0f); var target = State();
            source.RealtimeLastUpdateBits = 0x00000001;
            var prepared = CanonicalFsmTimeCarry.PrepareApply(source, () => target, value => target = value, () => true);
            Assert.IsFalse(prepared.ApplicationExecuted); prepared.Apply();
            Assert.IsTrue(CanonicalFsmTimeCarry.Equal(source, target)); Assert.IsTrue(prepared.ReadBackMatches);
            Assert.AreEqual(0x80000000u, target.TotalEditorPlayerPausedTimeBits); Assert.IsFalse(prepared.RestoreBindingQualified);
        }
        [TestMethod]
        public void NativeClockImportAndParkedGuardMustHoldBeforeAnyWrite()
        {
            var target = State(); var writes = 0;
            var prepared = CanonicalFsmTimeCarry.PrepareApply(State(offset: 9), () => target, value => { writes++; target = value; }, () => false);
            Assert.ThrowsExactly<InvalidOperationException>(() => prepared.Apply()); Assert.AreEqual(0, writes);
            Assert.IsTrue(prepared.Faulted); Assert.IsFalse(prepared.ApplicationExecuted);
            Assert.ThrowsExactly<InvalidOperationException>(() => prepared.Apply());
        }
        [TestMethod]
        public void SourceDataIsFrozenAndTargetDriftRejectsApplication()
        {
            var source = State(offset: 7); var target = State();
            var prepared = CanonicalFsmTimeCarry.PrepareApply(source, () => target, value => target = value, () => true);
            source.TotalEditorPlayerPausedTimeBits = CanonicalFsmTimeCarry.Bits(99);
            prepared.Apply(); Assert.AreEqual(CanonicalFsmTimeCarry.Bits(7), target.TotalEditorPlayerPausedTimeBits);
            Assert.ThrowsExactly<InvalidOperationException>(() => prepared.Apply());
            var old = State(); var targetChanged = old;
            var drift = CanonicalFsmTimeCarry.PrepareApply(State(), () => targetChanged, value => targetChanged = value, () => true);
            targetChanged = State(frame: 11);
            Assert.ThrowsExactly<InvalidDataException>(() => drift.Apply()); Assert.IsFalse(drift.ApplicationExecuted);
        }
        [TestMethod]
        public void PartialWriteFailureRestoresAllOriginalBitsAndCannotBeRetried()
        {
            var old = State(offset: -0.0f); var target = old; var writes = 0;
            var prepared = CanonicalFsmTimeCarry.PrepareApply(State(offset: 7), () => target, value => {
                writes++; target = value;
                if (writes == 1) throw new InvalidOperationException("partial raw write");
            }, () => true);
            Assert.ThrowsExactly<InvalidOperationException>(() => prepared.Apply());
            Assert.IsTrue(CanonicalFsmTimeCarry.Equal(old, target)); Assert.AreEqual(2, writes);
            Assert.IsTrue(prepared.ApplicationExecuted); Assert.IsTrue(prepared.Faulted); Assert.IsFalse(prepared.ReadBackMatches);
            Assert.ThrowsExactly<InvalidOperationException>(() => prepared.Apply());
        }
        [TestMethod]
        public void SourceReadbackMismatchRollsBackRatherThanReportingSuccess()
        {
            var old = State(); var target = old; var writes = 0;
            var prepared = CanonicalFsmTimeCarry.PrepareApply(State(offset: 7), () => target, value => {
                writes++; target = writes == 1 ? State(offset: 8) : value;
            }, () => true);
            Assert.ThrowsExactly<InvalidDataException>(() => prepared.Apply());
            Assert.IsTrue(CanonicalFsmTimeCarry.Equal(old, target)); Assert.IsFalse(prepared.ReadBackMatches);
        }
        [TestMethod]
        public void GuardLossAfterWriteRestoresTargetAndPermanentlyFaults()
        {
            var old = State(); var target = old; var checks = 0;
            var prepared = CanonicalFsmTimeCarry.PrepareApply(State(offset: 7), () => target, value => target = value, () => ++checks == 1);
            Assert.ThrowsExactly<InvalidOperationException>(() => prepared.Apply());
            Assert.IsTrue(CanonicalFsmTimeCarry.Equal(old, target)); Assert.IsTrue(prepared.Faulted);
        }
        [TestMethod]
        public void RollbackFailureIsExplicitAndDoesNotClaimQualification()
        {
            var target = State();
            var prepared = CanonicalFsmTimeCarry.PrepareApply(State(offset: 7), () => target,
                value => throw new InvalidOperationException("write failure"), () => true);
            Assert.ThrowsExactly<AggregateException>(() => prepared.Apply());
            Assert.IsTrue(prepared.Faulted); Assert.IsFalse(prepared.RestoreBindingQualified);
        }
        [TestMethod]
        public void NonfiniteSourceAndWrongOriginalModuleAreRejectedBeforePrepare()
        {
            var source = State(); source.TotalEditorPlayerPausedTimeBits = 0x7fc00001;
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalFsmTimeCarry.PrepareApply(source, () => State(), value => { }, () => true));
            source = State(); source.RealtimeLastUpdateBits = 0x7f800000;
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalFsmTimeCarry.Validate(source));
            source = State(); source.ModuleVersionId = Guid.NewGuid().ToString("D");
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalFsmTimeCarry.Validate(source));
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalFsmTimeCarry.ComputeRealtimeBits(State(), float.NaN));
        }
        [TestMethod]
        public void FiniteStoredValuesAndEverySignedFrameBitArePreserved()
        {
            var source = State(offset: -2, last: -4, frame: int.MinValue); var target = State();
            var prepared = CanonicalFsmTimeCarry.PrepareApply(source, () => target, value => target = value, () => true);
            prepared.Apply(); Assert.IsTrue(CanonicalFsmTimeCarry.Equal(source, target));
            Assert.AreEqual(CanonicalFsmTimeCarry.Bits(5), CanonicalFsmTimeCarry.ComputeRealtimeBits(source, 3));
        }
        [TestMethod]
        public void RawWriterCannotMutateFrozenExpectedDataToFakeReadback()
        {
            var old = State(); var target = old; var writes = 0;
            var prepared = CanonicalFsmTimeCarry.PrepareApply(State(offset: 7), () => target, value => {
                writes++;
                if (writes == 1) value.TotalEditorPlayerPausedTimeBits = CanonicalFsmTimeCarry.Bits(8);
                target = value;
            }, () => true);
            Assert.ThrowsExactly<InvalidDataException>(() => prepared.Apply());
            Assert.IsTrue(CanonicalFsmTimeCarry.Equal(old, target)); Assert.IsFalse(prepared.ReadBackMatches);
        }
    }
}
