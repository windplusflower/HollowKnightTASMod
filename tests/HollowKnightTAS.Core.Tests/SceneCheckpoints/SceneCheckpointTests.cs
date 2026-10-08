using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Ipc;
using HollowKnightTAS.Core.SceneCheckpoints;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.SceneCheckpoints
{
    [TestClass]
    public sealed class SceneCheckpointTests
    {
        private static readonly SceneCheckpointBinding Binding = new SceneCheckpointBinding(new string('a', 64), new string('b', 64), "test-profile");
        private static string Id() => Guid.NewGuid().ToString("N");
        private static SceneCheckpointPacket Packet(SceneCheckpointScope scope, int dataSize = 100000, SceneCheckpointTrigger trigger = SceneCheckpointTrigger.ManualNextEligibleLoadBoundary, string? checkpointId = null)
        {
            var payload = SceneCheckpointPacket.EncodeSections(new Dictionary<string, byte[]>
            {
                ["playerData"] = Enumerable.Range(0, dataSize).Select(n => (byte)n).ToArray(), ["sceneData"] = new byte[] { 2 },
                ["transition"] = new byte[] { 3 }, ["unityRng"] = Enumerable.Range(0, 16).Select(n => (byte)n).ToArray()
            });
            return new SceneCheckpointPacket(new SceneCheckpointHeader(checkpointId ?? Id(), scope, Binding, trigger, 1, 1421, 1500, 1421, 1520,
                payload.Length, Sha256Utility.ComputeHex(payload)), payload);
        }
        [TestMethod]
        public void PacketAndMetadataRoundTripThroughRealIpcFramesWithoutMutableAliases()
        {
            var scope = new SceneCheckpointScope(Id(), Id(), Id(), 1); var packet = Packet(scope);
            var fields = packet.Header.ToFields(); fields["requestId"] = "read-1"; fields["status"] = "Captured";
            var envelope = new IpcEnvelope(1, Id(), 1, IpcMessageTypes.SceneCheckpointData, IpcPayloadCodec.Serialize(fields));
            var decoded = IpcCodec.TryDeserializeEnvelope(IpcCodec.SerializeEnvelope(envelope)); Assert.IsTrue(decoded.Success);
            var header = SceneCheckpointHeader.FromFields(IpcPayloadCodec.TryDeserialize(decoded.Envelope!.PayloadUtf8).Fields!);
            using var receiver = new SceneCheckpointReceiver(header, scope, Binding);
            for (var index = 0; index < packet.ChunkCount; index++) receiver.Append(index, packet.ReadChunk(index));
            var received = receiver.Complete(); CollectionAssert.AreEqual(packet.CopyPayload(), received.CopyPayload());
            var copy = received.CopyPayload(); copy[5] ^= 1;
            var sections = received.CopySections(); sections["unityRng"][0] ^= 1;
            CollectionAssert.AreEqual(packet.CopyPayload(), received.CopyPayload()); Assert.IsFalse(received.Header.RestoreQualified);
        }
        [TestMethod]
        public void TruncatedReorderedDuplicateAndCorruptedTransfersNeverComplete()
        {
            var scope = new SceneCheckpointScope(Id(), Id(), Id(), 1); var packet = Packet(scope);
            using (var truncated = new SceneCheckpointReceiver(packet.Header, scope, Binding))
            { truncated.Append(0, packet.ReadChunk(0)); Assert.ThrowsExactly<InvalidDataException>(() => truncated.Complete()); }
            using (var reordered = new SceneCheckpointReceiver(packet.Header, scope, Binding))
            { Assert.ThrowsExactly<InvalidDataException>(() => reordered.Append(1, packet.ReadChunk(1))); Assert.ThrowsExactly<InvalidOperationException>(() => reordered.Complete()); }
            using (var duplicate = new SceneCheckpointReceiver(packet.Header, scope, Binding))
            { duplicate.Append(0, packet.ReadChunk(0)); Assert.ThrowsExactly<InvalidDataException>(() => duplicate.Append(0, packet.ReadChunk(0))); }
            using (var corrupt = new SceneCheckpointReceiver(packet.Header, scope, Binding))
            {
                for (var i = 0; i < packet.ChunkCount; i++) { var chunk = packet.ReadChunk(i); if (i == 1) chunk[0] ^= 1; corrupt.Append(i, chunk); }
                Assert.ThrowsExactly<InvalidDataException>(() => corrupt.Complete());
            }
        }
        [TestMethod]
        public void WrongBranchSaveEnvironmentProfileSchemaAndSizeAreRejectedBeforeAllocation()
        {
            var scope = new SceneCheckpointScope(Id(), Id(), Id(), 1); var packet = Packet(scope);
            Assert.ThrowsExactly<InvalidDataException>(() => new SceneCheckpointReceiver(packet.Header, new SceneCheckpointScope(scope.StudioSessionId, scope.TreeId, Id(), 1), Binding));
            Assert.ThrowsExactly<InvalidDataException>(() => new SceneCheckpointReceiver(packet.Header, scope, new SceneCheckpointBinding(new string('c', 64), Binding.InitialSavesSha256, Binding.NativeProfileId)));
            Assert.ThrowsExactly<InvalidDataException>(() => new SceneCheckpointReceiver(packet.Header, scope, new SceneCheckpointBinding(Binding.EnvironmentSha256, new string('c', 64), Binding.NativeProfileId)));
            Assert.ThrowsExactly<InvalidDataException>(() => new SceneCheckpointReceiver(packet.Header, scope, new SceneCheckpointBinding(Binding.EnvironmentSha256, Binding.InitialSavesSha256, "other-profile")));
            foreach (var key in new[] { "schema", "adapter", "restoreQualified", "payloadBytes" })
            {
                var fields = packet.Header.ToFields(); fields[key] = key == "payloadBytes" ? (SceneCheckpointPacket.MaximumPayloadBytes + 1).ToString() : "unknown";
                Assert.ThrowsExactly<InvalidDataException>(() => SceneCheckpointHeader.FromFields(fields));
            }
            var shifted = packet.Header.ToFields(); shifted["resumeNativeFrame"] = "1521";
            Assert.ThrowsExactly<InvalidDataException>(() => SceneCheckpointHeader.FromFields(shifted));
            var extra = packet.Header.ToFields(); extra["unexpected"] = "1";
            Assert.ThrowsExactly<InvalidDataException>(() => SceneCheckpointHeader.FromFields(extra));
        }
        [TestMethod]
        public void SectionLengthsCoverageAndTrailingDataAreValidatedEvenWithCorrectHash()
        {
            var scope = new SceneCheckpointScope(Id(), Id(), Id(), 1); var packet = Packet(scope); var payload = packet.CopyPayload();
            foreach (var invalid in new[] { payload.Concat(new byte[] { 0 }).ToArray(), payload.Take(payload.Length - 1).ToArray(), new byte[] { 1, 0, 0, 0, 255, 255, 255, 127 } })
            {
                var header = new SceneCheckpointHeader(Id(), scope, Binding, packet.Header.Trigger, 1, 1, 1, 1, 2, invalid.Length, Sha256Utility.ComputeHex(invalid));
                Assert.ThrowsExactly<InvalidDataException>(() => new SceneCheckpointPacket(header, invalid));
            }
            Assert.ThrowsExactly<InvalidDataException>(() => SceneCheckpointPacket.EncodeSections(new Dictionary<string, byte[]> { ["playerData"] = new byte[] { 1 } }));
        }
        [TestMethod]
        public void SwitchingAwayAndBackRejectsAnInFlightCaptureButRetainsPublishedBranchData()
        {
            using var store = new SceneCheckpointStore(); var tree = Id(); var a = Id(); var b = Id();
            var firstScope = store.ActivateBranch(tree, a); var first = store.BeginCapture(firstScope, Binding, SceneCheckpointTrigger.ManualNextEligibleLoadBoundary);
            var packet = store.Publish(first, Packet(firstScope, checkpointId: first.CaptureId)); Assert.AreEqual("Published", first.State);
            var stale = store.BeginCapture(firstScope, Binding, SceneCheckpointTrigger.ManualNextEligibleLoadBoundary);
            var scopeB = store.ActivateBranch(tree, b); Assert.AreEqual(0, store.Catalog(scopeB).Count);
            var scopeA = store.ActivateBranch(tree, a); Assert.IsTrue(scopeA.Generation > firstScope.Generation);
            Assert.ThrowsExactly<InvalidDataException>(() => store.Publish(stale, Packet(firstScope, checkpointId: stale.CaptureId))); Assert.AreEqual("Rejected", stale.State);
            Assert.AreSame(packet, store.Get(packet.Header.CheckpointId, scopeA, Binding));
            Assert.ThrowsExactly<InvalidDataException>(() => store.Get(packet.Header.CheckpointId, firstScope, Binding));
        }
        [TestMethod]
        public void DeletionCancellationAndStudioExitInvalidateCaptureAndLookup()
        {
            var store = new SceneCheckpointStore(); var tree = Id(); var branch = Id(); var scope = store.ActivateBranch(tree, branch);
            var capture = store.BeginCapture(scope, Binding, SceneCheckpointTrigger.AutoOnLoadBoundary);
            var packet = store.Publish(capture, Packet(scope, trigger: SceneCheckpointTrigger.AutoOnLoadBoundary, checkpointId: capture.CaptureId));
            var pending = store.BeginCapture(scope, Binding, SceneCheckpointTrigger.ManualNextEligibleLoadBoundary); store.Cancel(pending);
            Assert.ThrowsExactly<InvalidDataException>(() => store.Publish(pending, Packet(scope)));
            store.DeleteBranch(tree, branch); var fresh = store.ActivateBranch(tree, branch); Assert.AreEqual(0, store.Catalog(fresh).Count);
            Assert.ThrowsExactly<InvalidDataException>(() => store.Get(packet.Header.CheckpointId, fresh, Binding));
            store.Dispose(); Assert.ThrowsExactly<ObjectDisposedException>(() => store.Catalog(fresh));
        }
        [TestMethod]
        public void DuplicateBoundaryDeduplicatesDataAndConflictingDataFails()
        {
            using var store = new SceneCheckpointStore(); var scope = store.ActivateBranch(Id(), Id());
            var capture = store.BeginCapture(scope, Binding, SceneCheckpointTrigger.ManualNextEligibleLoadBoundary);
            var first = store.Publish(capture, Packet(scope, 20, checkpointId: capture.CaptureId));
            capture = store.BeginCapture(scope, Binding, SceneCheckpointTrigger.ManualNextEligibleLoadBoundary);
            var duplicate = store.Publish(capture, Packet(scope, 20, checkpointId: capture.CaptureId));
            Assert.AreSame(first, duplicate); Assert.AreEqual(1, store.Catalog(scope).Count);
            var conflict = store.BeginCapture(scope, Binding, SceneCheckpointTrigger.ManualNextEligibleLoadBoundary);
            Assert.ThrowsExactly<InvalidDataException>(() => store.Publish(conflict, Packet(scope, 21, checkpointId: conflict.CaptureId))); Assert.AreEqual("Rejected", conflict.State);
        }
        [TestMethod]
        public void OlderCaptureInSameBranchCannotSatisfyANewerLease()
        {
            using var store = new SceneCheckpointStore(); var scope = store.ActivateBranch(Id(), Id());
            var old = store.BeginCapture(scope, Binding, SceneCheckpointTrigger.ManualNextEligibleLoadBoundary);
            var oldPacket = Packet(scope, checkpointId: old.CaptureId); store.Cancel(old);
            var current = store.BeginCapture(scope, Binding, SceneCheckpointTrigger.ManualNextEligibleLoadBoundary);
            Assert.ThrowsExactly<InvalidDataException>(() => store.Publish(current, oldPacket));
            Assert.AreEqual("Rejected", current.State); Assert.AreEqual(0, store.Catalog(scope).Count);
        }
    }
}
