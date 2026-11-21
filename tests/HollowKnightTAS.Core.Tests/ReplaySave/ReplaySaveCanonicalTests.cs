using System;
using System.Collections.Generic;
using System.Linq;
using HollowKnightTAS.Core.ReplaySave;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.ReplaySave
{
    [TestClass]
    public sealed class ReplaySaveCanonicalTests
    {
        [TestMethod]
        public void DynamicRoot_PreservesObservedSubFixedStepRemainder()
        {
            Assert.IsTrue(HollowKnightTAS.Core.Ipc.RecordingRootConfiguration.IsValidPhysicsPhase(
                203.06939740983887, 203.05999546125531, 0.02f));
            Assert.IsTrue(HollowKnightTAS.Core.Ipc.RecordingRootConfiguration.IsValidPhysicsPhase(10, 10, 0.02));
            Assert.IsFalse(HollowKnightTAS.Core.Ipc.RecordingRootConfiguration.IsValidPhysicsPhase(10, 10.01, 0.02));
            Assert.IsFalse(HollowKnightTAS.Core.Ipc.RecordingRootConfiguration.IsValidPhysicsPhase(10, 9.97, 0.02));
            Assert.IsFalse(HollowKnightTAS.Core.Ipc.RecordingRootConfiguration.IsValidPhysicsPhase(double.NaN, 10, 0.02));
        }

        [TestMethod]
        public void DynamicRoot_ChoosesReachableClockLatticeAndRejectsLossyValues()
        {
            foreach (var start in new[] { 0d, 800.25d, 3600d, 124.01337d })
            {
                var target = HollowKnightTAS.Core.Ipc.RecordingRootConfiguration.Choose(start);
                var frames = Math.Round((target - start) / 0.02f);
                Assert.AreEqual(target, (double)(float)(start + frames * (double)0.02f));
                Assert.IsTrue(target >= start + 30d);
            }
            // Whole-second rounding can land outside this clock lattice.
            const double offsetStart = 124.01337d;
            var oldTarget = Math.Ceiling(offsetStart + 30d);
            Assert.AreNotEqual((float)oldTarget,
                (float)(offsetStart + Math.Round((oldTarget - offsetStart) / 0.02f) * (double)0.02f));
            foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, -1d, 0d, 768.1, 1e20 })
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                    HollowKnightTAS.Core.Ipc.RecordingRootConfiguration.Validate(invalid));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                HollowKnightTAS.Core.Ipc.RecordingRootConfiguration.Choose(-1));
        }

        [TestMethod]
        public void RecordingOriginAlignment_WaitsThenRequiresExactFrameAndClockBits()
        {
            var origin = new RecordingOrigin("profile", 768, 3, 768.06, 768.06);
            Assert.AreEqual(RecordingOriginAlignment.Waiting, origin.CompareBoundary(2, 768.04, 768.04));
            Assert.AreEqual(RecordingOriginAlignment.Matched, origin.CompareBoundary(3, 768.06, 768.06));
            Assert.AreEqual(RecordingOriginAlignment.Mismatch, origin.CompareBoundary(4, 768.06, 768.06));
            var nextBit = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(768.06) + 1);
            Assert.AreEqual(RecordingOriginAlignment.Mismatch, origin.CompareBoundary(3, nextBit, 768.06));
            Assert.AreEqual(RecordingOriginAlignment.Mismatch, origin.CompareBoundary(3, 768.06, nextBit));
        }

        [TestMethod]
        public void RecordingOrigin_IsHashBoundAndLegacyBytesRemainUnchanged()
        {
            var commit = ReplaySaveTestFactory.Commit();
            var legacyBytes = commit.Objects[commit.Descriptor.BaselineObjectSha256];
            var legacy = BaselineBundleCodec.Deserialize(legacyBytes);
            Assert.AreEqual(1, legacy.SchemaVersion);
            Assert.IsNull(legacy.RecordingOrigin);
            CollectionAssert.AreEqual(legacyBytes, BaselineBundleCodec.Serialize(legacy));

            var stamped = legacy.WithRecordingOrigin(new RecordingOrigin(
                "test-profile", 768, 2, 768.04, 768.04));
            var bytes = BaselineBundleCodec.Serialize(stamped);
            var decoded = BaselineBundleCodec.Deserialize(bytes);
            Assert.AreEqual(2, decoded.SchemaVersion);
            Assert.AreEqual("test-profile", decoded.RecordingOrigin!.ProfileId);
            Assert.AreEqual(768d, decoded.RecordingOrigin.RootBoundarySeconds);
            Assert.AreEqual(2, decoded.RecordingOrigin.FramesAfterRootBoundary);
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(768.04),
                BitConverter.DoubleToInt64Bits(decoded.RecordingOrigin.GameTimeSeconds));
            CollectionAssert.AreEqual(bytes, BaselineBundleCodec.Serialize(decoded));
            CollectionAssert.AreEqual(legacy.SaveData, decoded.SaveData);
            var changed = legacy.WithRecordingOrigin(new RecordingOrigin(
                "test-profile", 768, 3, 768.04, 768.04));
            Assert.AreNotEqual(
                HollowKnightTAS.Core.Cryptography.Sha256Utility.ComputeHex(bytes),
                HollowKnightTAS.Core.Cryptography.Sha256Utility.ComputeHex(BaselineBundleCodec.Serialize(changed)));
        }

        [TestMethod]
        public void RecordingOrigin_RejectsInvalidTimesAndMalformedPayloads()
        {
            foreach (var bad in new[] { double.NaN, double.PositiveInfinity, -1d,
                         BitConverter.Int64BitsToDouble(long.MinValue) })
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                    new RecordingOrigin("profile", bad, 0, 768, 768));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                new RecordingOrigin("profile", 768, -1, 768, 768));
            Assert.ThrowsExactly<ArgumentException>(() =>
                new RecordingOrigin("profile", 768, 0, 767, 768));
            var commit = ReplaySaveTestFactory.Commit();
            var legacy = BaselineBundleCodec.Deserialize(commit.Objects[commit.Descriptor.BaselineObjectSha256]);
            var v2 = new BaselineBundle(2, legacy.BaselineId, legacy.BaselineSemanticSha256,
                legacy.OriginalSlot, legacy.CapturedAtUtc, legacy.SaveData,
                legacy.ModdedSaveData, legacy.SemanticSnapshotBytes);
            var bytes = BaselineBundleCodec.Serialize(v2);
            Assert.IsNull(BaselineBundleCodec.Deserialize(bytes).RecordingOrigin);
            bytes[bytes.Length - 1] = 2;
            Assert.ThrowsExactly<System.IO.InvalidDataException>(() => BaselineBundleCodec.Deserialize(bytes));
            var stamped = BaselineBundleCodec.Serialize(legacy.WithRecordingOrigin(
                new RecordingOrigin("profile", 768, 1, 768.02, 768.02)));
            Assert.ThrowsExactly<System.IO.InvalidDataException>(() =>
                BaselineBundleCodec.Deserialize(stamped.Take(stamped.Length - 1).ToArray()));
        }

        [TestMethod]
        public void DescriptorAndCatalog_AreByteStableAcrossOneHundredRoundTrips()
        {
            var descriptor = ReplaySaveTestFactory.Commit().Descriptor;
            var descriptorBytes = ReplaySaveDescriptorCodec.Serialize(descriptor);
            for (var iteration = 0; iteration < 100; iteration++)
            {
                descriptor = ReplaySaveDescriptorCodec.Deserialize(
                    descriptorBytes);
                CollectionAssert.AreEqual(
                    descriptorBytes,
                    ReplaySaveDescriptorCodec.Serialize(descriptor));
            }

            var catalog = new ReplaySaveCatalog();
            catalog.Upsert(
                new ReplaySaveCatalogEntry(
                    descriptor,
                    ReplaySaveStatus.Ready,
                    string.Empty));
            var catalogBytes = ReplaySaveCatalogCodec.Serialize(catalog);
            for (var iteration = 0; iteration < 100; iteration++)
            {
                catalog = ReplaySaveCatalogCodec.Deserialize(catalogBytes);
                CollectionAssert.AreEqual(
                    catalogBytes,
                    ReplaySaveCatalogCodec.Serialize(catalog));
            }
        }

        [TestMethod]
        public void BaselineAndJournal_RoundTripWithoutChangingObjectHash()
        {
            var commit = ReplaySaveTestFactory.Commit();
            var baselineBytes = commit.Objects[
                commit.Descriptor.BaselineObjectSha256];
            var baseline = BaselineBundleCodec.Deserialize(baselineBytes);
            CollectionAssert.AreEqual(
                baselineBytes,
                BaselineBundleCodec.Serialize(baseline));

            foreach (var hash in commit.Descriptor.JournalSegmentObjectSha256s)
            {
                var bytes = commit.Objects[hash];
                var segment = ReplayJournalSegmentCodec.Deserialize(bytes);
                CollectionAssert.AreEqual(
                    bytes,
                    ReplayJournalSegmentCodec.Serialize(segment));
            }
        }

        [TestMethod]
        public void Validator_RejectsJournalGapManifestMismatchAndWrongSchema()
        {
            var commit = ReplaySaveTestFactory.Commit();
            var missingSegment = new Dictionary<string, byte[]>(
                commit.Objects,
                StringComparer.Ordinal);
            missingSegment.Remove(
                commit.Descriptor.JournalSegmentObjectSha256s[1]);
            var gap = ReplaySaveValidator.Validate(
                commit.Descriptor,
                missingSegment.TryGetValue);
            Assert.AreEqual(ReplaySaveStatus.JournalGap, gap.Status);

            var mismatch = ReplaySaveValidator.Validate(
                commit.Descriptor,
                commit.TryGetObject,
                new string('a', 64));
            Assert.AreEqual(ReplaySaveStatus.Ready, mismatch.Status);
            Assert.IsTrue(mismatch.Success, mismatch.Detail);

            var source = commit.Descriptor;
            var incompatible = new ReplaySaveDescriptor(
                99,
                source.ReplaySaveId,
                source.Label,
                source.Reason,
                source.RequestedAtUtc,
                source.CreatedAtUtc,
                source.RequestedAtMovieTick,
                source.EffectiveMovieTick,
                source.ManifestSha256,
                source.BaselineObjectSha256,
                source.BaselineId,
                source.BaselineSemanticSha256,
                source.MovieObjectSha256,
                source.JournalHeadSha256,
                source.JournalSegmentObjectSha256s,
                source.SemanticSnapshotSha256,
                source.LedgerSummarySha256,
                source.SceneName,
                source.SceneEpoch,
                source.AutoRetentionCount);
            var schema = ReplaySaveValidator.Validate(
                incompatible,
                commit.TryGetObject);
            Assert.AreEqual(ReplaySaveStatus.Incompatible, schema.Status);
        }

        [TestMethod]
        public void MovieDerivedFromJournal_ReproducesEveryCommittedInput()
        {
            var commit = ReplaySaveTestFactory.Commit(recordCount: 31);
            var validation = ReplaySaveValidator.Validate(
                commit.Descriptor,
                commit.TryGetObject,
                commit.Descriptor.ManifestSha256);

            Assert.IsTrue(validation.Success, validation.Detail);
            Assert.IsNotNull(validation.Package);
            Assert.AreEqual(31, validation.Package.Segments.Sum(
                value => value.Records.Count));
            Assert.AreEqual(31, validation.Package.NextMovieTick);
        }
    }
}
