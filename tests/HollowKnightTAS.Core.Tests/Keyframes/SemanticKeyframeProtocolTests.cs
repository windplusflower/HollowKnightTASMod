using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Keyframes;
using HollowKnightTAS.Core.ReplaySave;
using HollowKnightTAS.Core.Tests.ReplaySave;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.Keyframes
{
    [TestClass]
    public sealed class SemanticKeyframeProtocolTests
    {
        [TestMethod]
        public void CanonicalDocuments_AreStableForOneHundredPasses()
        {
            var fixture = KeyframeTestFactory.CreateFixture();
            var descriptorBytes =
                SemanticKeyframeDescriptorCodec.Serialize(
                    fixture.Descriptor);
            var manifestBytes =
                KeyframeAdapterManifestCodec.Serialize(
                    fixture.Manifest);
            var index = new KeyframeArtifactIndex(
                KeyframeArtifactIndex.CurrentSchemaVersion,
                new[]
                {
                    new KeyframeIndexEntry(
                        "save-a",
                        fixture.Descriptor.KeyframeId,
                        fixture.Descriptor.CaptureMovieTick,
                        300)
                });
            var indexBytes = KeyframeArtifactIndexCodec.Serialize(index);

            for (var pass = 0; pass < 100; pass++)
            {
                CollectionAssert.AreEqual(
                    descriptorBytes,
                    SemanticKeyframeDescriptorCodec.Serialize(
                        SemanticKeyframeDescriptorCodec.Deserialize(
                            descriptorBytes)));
                CollectionAssert.AreEqual(
                    manifestBytes,
                    KeyframeAdapterManifestCodec.Serialize(
                        KeyframeAdapterManifestCodec.Deserialize(
                            manifestBytes)));
                CollectionAssert.AreEqual(
                    indexBytes,
                    KeyframeArtifactIndexCodec.Serialize(
                        KeyframeArtifactIndexCodec.Deserialize(
                            indexBytes)));
            }
        }

        [TestMethod]
        public void NonCanonicalOrInvalidDocuments_FailClosed()
        {
            var fixture = KeyframeTestFactory.CreateFixture();
            var bytes = SemanticKeyframeDescriptorCodec.Serialize(
                fixture.Descriptor);
            var json = Encoding.UTF8.GetString(bytes);

            Assert.ThrowsExactly<InvalidDataException>(
                () => SemanticKeyframeDescriptorCodec.Deserialize(
                    Encoding.UTF8.GetBytes(
                        json.Substring(0, json.Length - 1)
                        + ",\"unknown\":true}")));
            Assert.ThrowsExactly<InvalidDataException>(
                () => SemanticKeyframeDescriptorCodec.Deserialize(
                    Encoding.UTF8.GetBytes(json + " ")));
            Assert.ThrowsExactly<ArgumentException>(
                () => new SemanticKeyframeDescriptor(
                    SemanticKeyframeDescriptor.CurrentSchemaVersion,
                    "duplicate-adapter",
                    KeyframeSupportTier.RoomEntry,
                    KeyframeStatus.Ready,
                    10,
                    "Room_Test",
                    "room-entry",
                    1,
                    KeyframeTestFactory.Hash("game"),
                    KeyframeTestFactory.Hash("manifest"),
                    KeyframeTestFactory.Hash("baseline"),
                    KeyframeTestFactory.Hash("journal"),
                    fixture.Descriptor.AdapterManifestSha256,
                    KeyframeTestFactory.Hash("semantic"),
                    KeyframeTestFactory.Hash("rng"),
                    new[]
                    {
                        fixture.Descriptor.Adapters[0],
                        fixture.Descriptor.Adapters[0]
                    }));
            Assert.ThrowsExactly<ArgumentException>(
                () => new KeyframeAdapterEnvelope(
                    "bad-hash",
                    1,
                    true,
                    "ABC",
                    3));
        }

        [TestMethod]
        public void Compatibility_IsExactAndUnknownRequiredAdapterFails()
        {
            var fixture = KeyframeTestFactory.CreateFixture();
            var context = KeyframeTestFactory.Context(
                fixture,
                KeyframeSupportTier.RoomEntry);
            var compatible = KeyframeCompatibility.Evaluate(
                fixture.Descriptor,
                context);
            Assert.IsTrue(compatible.IsCompatible);
            Assert.AreEqual("compatible", compatible.ReasonCode);

            var replayOnly = KeyframeCompatibility.Evaluate(
                fixture.Descriptor,
                KeyframeTestFactory.Context(
                    fixture,
                    KeyframeSupportTier.ReplayOnly));
            Assert.AreEqual(
                KeyframeCompatibilityStatus.ReplayOnly,
                replayOnly.Status);

            var mismatch = KeyframeCompatibility.Evaluate(
                fixture.Descriptor,
                new KeyframeCompatibilityContext(
                    KeyframeSupportTier.RoomEntry,
                    300,
                    KeyframeTestFactory.Hash("different-game"),
                    KeyframeTestFactory.Hash("different-manifest"),
                    fixture.Descriptor.BaselineObjectSha256,
                    new[] { fixture.Descriptor.JournalHeadSha256 },
                    KeyframeTestFactory.Hash("different-adapter-manifest"),
                    fixture.Manifest,
                    true));
            Assert.AreEqual(
                KeyframeCompatibilityStatus.Compatible,
                mismatch.Status);
            Assert.IsTrue(mismatch.IsCompatible);
            Assert.AreEqual(
                "compatible",
                mismatch.ReasonCode);

            var baselineMismatch = KeyframeCompatibility.Evaluate(
                fixture.Descriptor,
                new KeyframeCompatibilityContext(
                    KeyframeSupportTier.RoomEntry,
                    300,
                    fixture.Descriptor.GameBuildSha256,
                    fixture.Descriptor.ManifestSha256,
                    KeyframeTestFactory.Hash("different-baseline"),
                    new[] { fixture.Descriptor.JournalHeadSha256 },
                    fixture.Descriptor.AdapterManifestSha256,
                    fixture.Manifest,
                    false));
            Assert.AreEqual(
                KeyframeCompatibilityStatus.Incompatible,
                baselineMismatch.Status);
            Assert.AreEqual(
                "baseline-mismatch",
                baselineMismatch.ReasonCode);

            var journalMismatch = KeyframeCompatibility.Evaluate(
                fixture.Descriptor,
                new KeyframeCompatibilityContext(
                    KeyframeSupportTier.RoomEntry,
                    300,
                    fixture.Descriptor.GameBuildSha256,
                    fixture.Descriptor.ManifestSha256,
                    fixture.Descriptor.BaselineObjectSha256,
                    new[]
                    {
                        KeyframeTestFactory.Hash(
                            "unrelated-journal-head")
                    },
                    fixture.Descriptor.AdapterManifestSha256,
                    fixture.Manifest,
                    false));
            Assert.AreEqual(
                "journal-chain-mismatch",
                journalMismatch.ReasonCode);

            var unknownManifest = new KeyframeAdapterManifest(
                KeyframeAdapterManifest.CurrentSchemaVersion,
                Array.Empty<KeyframeAdapterManifestEntry>());
            var unsupported = KeyframeCompatibility.Evaluate(
                fixture.Descriptor,
                new KeyframeCompatibilityContext(
                    KeyframeSupportTier.RoomEntry,
                    300,
                    fixture.Descriptor.GameBuildSha256,
                    fixture.Descriptor.ManifestSha256,
                    fixture.Descriptor.BaselineObjectSha256,
                    new[] { fixture.Descriptor.JournalHeadSha256 },
                    fixture.Descriptor.AdapterManifestSha256,
                    unknownManifest,
                    false));
            Assert.AreEqual(
                KeyframeCompatibilityStatus.UnsupportedAdapter,
                unsupported.Status);
            Assert.AreEqual(
                "unknown-required-adapter",
                unsupported.ReasonCode);
        }

        [TestMethod]
        public void ReplayOnlyAccelerator_CanNeverApplyState()
        {
            var accelerator = new ReplayOnlyRestoreAccelerator(
                new[]
                {
                    "rng-coverage-partial",
                    "no-verified-room-entry-gates"
                });
            var save = ReplaySaveTestFactory.Commit().Descriptor;
            var plan = accelerator.TryPlan(save);

            Assert.AreEqual(
                ReplayRestoreAccelerationStatus.Unavailable,
                plan.Status);
            Assert.Contains(
                "no-verified-room-entry-gates",
                plan.Detail);
            var policyAttempt =
                ReplayRestoreAccelerationPolicy.TryRestore(
                    accelerator,
                    plan,
                    CancellationToken.None);
            Assert.IsFalse(policyAttempt.Attempted);

            var defensiveResult = accelerator.TryRestore(
                new ReplayRestoreAccelerationPlan(
                    ReplayRestoreAccelerationStatus.Ready,
                    10,
                    "invalid external call"),
                CancellationToken.None);
            Assert.IsFalse(defensiveResult.Success);
            Assert.AreEqual(0, defensiveResult.ResumeMovieTick);
        }
    }

    internal sealed class KeyframeFixture
    {
        public KeyframeFixture(
            SemanticKeyframeDescriptor descriptor,
            KeyframeAdapterManifest manifest,
            byte[] baselineBytes,
            System.Collections.Generic.IReadOnlyDictionary<
                string,
                byte[]> objects)
        {
            Descriptor = descriptor;
            Manifest = manifest;
            BaselineBytes = baselineBytes;
            Objects = objects;
        }

        public SemanticKeyframeDescriptor Descriptor { get; }
        public KeyframeAdapterManifest Manifest { get; }
        public byte[] BaselineBytes { get; }
        public System.Collections.Generic.IReadOnlyDictionary<
            string,
            byte[]> Objects { get; }
    }

    internal static class KeyframeTestFactory
    {
        public static KeyframeFixture CreateFixture(
            string keyframeId = "keyframe-a",
            long captureTick = 120)
        {
            var baseline = Encoding.UTF8.GetBytes("baseline-v1");
            var manifest = new KeyframeAdapterManifest(
                KeyframeAdapterManifest.CurrentSchemaVersion,
                new[]
                {
                    new KeyframeAdapterManifestEntry(
                        "core.player-data",
                        1,
                        true,
                        KeyframeSupportTier.RoomEntry,
                        "hk-1-5-78-11833")
                });
            var manifestBytes =
                KeyframeAdapterManifestCodec.Serialize(manifest);
            var semantic = Encoding.UTF8.GetBytes("semantic-state-v1");
            var rng = Encoding.UTF8.GetBytes("rng-state-v1");
            var payload = Encoding.UTF8.GetBytes("player-data-v1");
            var envelope = new KeyframeAdapterEnvelope(
                "core.player-data",
                1,
                true,
                Sha256Utility.ComputeHex(payload),
                payload.Length);
            var descriptor = new SemanticKeyframeDescriptor(
                SemanticKeyframeDescriptor.CurrentSchemaVersion,
                keyframeId,
                KeyframeSupportTier.RoomEntry,
                KeyframeStatus.Ready,
                captureTick,
                "Room_Test",
                "room-entry",
                2,
                Hash("game"),
                Hash("runtime-manifest"),
                Sha256Utility.ComputeHex(baseline),
                Hash("journal-head"),
                Sha256Utility.ComputeHex(manifestBytes),
                Sha256Utility.ComputeHex(semantic),
                Sha256Utility.ComputeHex(rng),
                new[] { envelope });
            var objects =
                new System.Collections.Generic.Dictionary<
                    string,
                    byte[]>
                {
                    [Sha256Utility.ComputeHex(manifestBytes)] =
                        manifestBytes,
                    [Sha256Utility.ComputeHex(semantic)] = semantic,
                    [Sha256Utility.ComputeHex(rng)] = rng,
                    [Sha256Utility.ComputeHex(payload)] = payload
                };
            return new KeyframeFixture(
                descriptor,
                manifest,
                baseline,
                objects);
        }

        public static string Hash(string value)
        {
            return Sha256Utility.ComputeUtf8Hex(value);
        }

        public static KeyframeCompatibilityContext Context(
            KeyframeFixture fixture,
            KeyframeSupportTier tier)
        {
            return new KeyframeCompatibilityContext(
                tier,
                300,
                fixture.Descriptor.GameBuildSha256,
                fixture.Descriptor.ManifestSha256,
                fixture.Descriptor.BaselineObjectSha256,
                new[] { fixture.Descriptor.JournalHeadSha256 },
                fixture.Descriptor.AdapterManifestSha256,
                fixture.Manifest,
                false);
        }

        public static void PublishBaseline(
            string root,
            KeyframeFixture fixture)
        {
            var hash = fixture.Descriptor.BaselineObjectSha256;
            var directory = Path.Combine(
                root,
                "objects",
                "sha256",
                hash.Substring(0, 2));
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(
                Path.Combine(directory, hash),
                fixture.BaselineBytes);
        }
    }
}
