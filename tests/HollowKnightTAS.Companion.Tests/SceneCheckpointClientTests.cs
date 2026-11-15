using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Ipc;
using HollowKnightTAS.Core.SceneCheckpoints;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Companion.Tests
{
    [TestClass]
    public sealed class SceneCheckpointClientTests
    {
        private static string Id() => Guid.NewGuid().ToString("N");
        private static SceneCheckpointBinding Binding() => new(new string('a', 64), new string('b', 64), "test-profile");
        [TestMethod]
        public async Task CapturePublishAndTransferToReplacementPeerRetainDataInStudio()
        {
            using var store = new SceneCheckpointStore(); var scope = store.ActivateBranch(Id(), Id()); var binding = Binding();
            var source = new MemoryPeer(); var client = new SceneCheckpointClient(store, source.Request);
            var lease = await client.ArmAsync(scope, binding, SceneCheckpointTrigger.ManualNextEligibleLoadBoundary, CancellationToken.None);
            Assert.AreEqual(0, store.Catalog(scope).Count);
            var packet = await client.ReceiveAsync(lease, TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.AreEqual("Published", lease.State); Assert.IsNull(source.Captured);
            var replacement = new MemoryPeer(); var target = new SceneCheckpointClient(store, replacement.Request);
            await target.StageAsync(packet.Header.CheckpointId, scope, binding, CancellationToken.None);
            Assert.IsNotNull(replacement.Staged); CollectionAssert.AreEqual(packet.CopyPayload(), replacement.Staged.CopyPayload());
            Assert.IsTrue(replacement.Staged.Header.RestoreQualified); Assert.AreEqual(1, store.Catalog(scope).Count);
        }
        [TestMethod]
        public async Task CorruptTransferRejectsPublicationAndCancelsRuntimeBuffer()
        {
            using var store = new SceneCheckpointStore(); var scope = store.ActivateBranch(Id(), Id());
            var peer = new MemoryPeer { Corrupt = true }; var client = new SceneCheckpointClient(store, peer.Request);
            var lease = await client.ArmAsync(scope, Binding(), SceneCheckpointTrigger.AutoOnLoadBoundary, CancellationToken.None);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => client.ReceiveAsync(lease, TimeSpan.FromSeconds(2), CancellationToken.None));
            Assert.AreEqual("Rejected", lease.State); Assert.AreEqual(0, store.Catalog(scope).Count); Assert.IsNull(peer.Captured);
        }
        [TestMethod]
        public async Task BranchSwitchDuringTransferDoesNotPublishToEitherOldOrNewGeneration()
        {
            using var store = new SceneCheckpointStore(); var tree = Id(); var branch = Id(); var scope = store.ActivateBranch(tree, branch);
            var current = scope;
            var peer = new MemoryPeer { OnChunk = index => { if (index == 0) { store.ActivateBranch(tree, Id()); current = store.ActivateBranch(tree, branch); } } };
            var client = new SceneCheckpointClient(store, peer.Request);
            var lease = await client.ArmAsync(scope, Binding(), SceneCheckpointTrigger.ManualNextEligibleLoadBoundary, CancellationToken.None);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => client.ReceiveAsync(lease, TimeSpan.FromSeconds(2), CancellationToken.None));
            Assert.AreEqual("Rejected", lease.State); Assert.AreEqual(0, store.Catalog(current).Count); Assert.IsNull(peer.Captured);
        }
        [TestMethod]
        public async Task CancellationAndWrongSaveBindingCannotStageData()
        {
            using var store = new SceneCheckpointStore(); var scope = store.ActivateBranch(Id(), Id()); var peer = new MemoryPeer();
            var client = new SceneCheckpointClient(store, peer.Request);
            var lease = await client.ArmAsync(scope, Binding(), SceneCheckpointTrigger.ManualNextEligibleLoadBoundary, CancellationToken.None);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => client.ReceiveAsync(lease, TimeSpan.FromSeconds(2), cancel.Token));
            Assert.AreEqual(0, store.Catalog(scope).Count); Assert.AreEqual("Rejected", lease.State);
            lease = await client.ArmAsync(scope, Binding(), SceneCheckpointTrigger.ManualNextEligibleLoadBoundary, CancellationToken.None);
            var packet = await client.ReceiveAsync(lease, TimeSpan.FromSeconds(2), CancellationToken.None);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => client.StageAsync(packet.Header.CheckpointId, scope,
                new SceneCheckpointBinding(Binding().EnvironmentSha256, new string('c', 64), Binding().NativeProfileId), CancellationToken.None));
            Assert.IsNull(peer.Staged);
        }
        [TestMethod]
        public async Task LateCancellationOfOldLeaseDoesNotCancelNewCapture()
        {
            using var store = new SceneCheckpointStore(); var scope = store.ActivateBranch(Id(), Id()); var peer = new MemoryPeer();
            var client = new SceneCheckpointClient(store, peer.Request);
            var old = await client.ArmAsync(scope, Binding(), SceneCheckpointTrigger.ManualNextEligibleLoadBoundary, CancellationToken.None);
            await client.CancelAsync(old, CancellationToken.None);
            var current = await client.ArmAsync(scope, Binding(), SceneCheckpointTrigger.ManualNextEligibleLoadBoundary, CancellationToken.None);
            await client.CancelAsync(old, CancellationToken.None);
            var packet = await client.ReceiveAsync(current, TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.AreEqual(current.CaptureId, packet.Header.CheckpointId); Assert.AreEqual("Published", current.State);
        }
        [TestMethod]
        public async Task ExperimentalRestoreSeparatesStagingApplicationAndStatus()
        {
            using var store = new SceneCheckpointStore(); var scope = store.ActivateBranch(Id(), Id()); var binding = Binding();
            var peer = new MemoryPeer(); var client = new SceneCheckpointClient(store, peer.Request);
            var lease = await client.ArmAsync(scope, binding, SceneCheckpointTrigger.ManualNextEligibleLoadBoundary, CancellationToken.None);
            var packet = await client.ReceiveAsync(lease, TimeSpan.FromSeconds(2), CancellationToken.None);
            await client.StageAsync(packet.Header.CheckpointId, scope, binding, CancellationToken.None);
            Assert.IsFalse(peer.Applied);
            await client.StartRestorePrototypeAsync(packet.Header.CheckpointId, scope, binding, CancellationToken.None);
            Assert.IsFalse(peer.Applied);
            await client.ApplyRestorePrototypeAsync(packet.Header.CheckpointId, scope, binding, CancellationToken.None);
            var status = await client.RestoreStatusAsync(packet.Header.CheckpointId, CancellationToken.None);
            Assert.IsTrue(peer.Applied); Assert.AreEqual("true", status["restoreQualified"]);
            Assert.AreEqual(packet.Header.CheckpointId, status["checkpointId"]);
        }
        [TestMethod]
        public async Task InvalidRestoreReceiptOrBranchSwitchRetiresTheReplacement()
        {
            using var store = new SceneCheckpointStore(); var tree = Id(); var branch = Id(); var scope = store.ActivateBranch(tree, branch); var binding = Binding();
            var peer = new MemoryPeer(); var client = new SceneCheckpointClient(store, peer.Request);
            var lease = await client.ArmAsync(scope, binding, SceneCheckpointTrigger.ManualNextEligibleLoadBoundary, CancellationToken.None);
            var packet = await client.ReceiveAsync(lease, TimeSpan.FromSeconds(2), CancellationToken.None);
            await client.StageAsync(packet.Header.CheckpointId, scope, binding, CancellationToken.None);
            peer.InvalidRestoreReceipt = true;
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => client.ApplyRestorePrototypeAsync(packet.Header.CheckpointId, scope, binding, CancellationToken.None));
            Assert.IsTrue(peer.RestoreCancelled);
            peer.InvalidRestoreReceipt = false; peer.RestoreCancelled = false;
            peer.OnRestore = () => store.ActivateBranch(tree, Id());
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => client.StartRestorePrototypeAsync(packet.Header.CheckpointId, scope, binding, CancellationToken.None));
            Assert.IsTrue(peer.RestoreCancelled);
        }
        private sealed class MemoryPeer
        {
            internal SceneCheckpointPacket? Captured;
            internal SceneCheckpointPacket? Staged;
            internal bool Corrupt;
            internal Action<int>? OnChunk;
            internal Action? OnRestore;
            internal bool Applied, InvalidRestoreReceipt, RestoreCancelled;
            private SceneCheckpointReceiver? import;
            private readonly string session = Id();
            private long sequence;
            internal Task<IReadOnlyDictionary<string, string>> Request(IReadOnlyDictionary<string, string> fields, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); fields = Wire(fields, IpcMessageTypes.SceneCheckpoint);
                var reply = new Dictionary<string, string> { ["requestId"] = fields["requestId"] };
                switch (fields["phase"])
                {
                    case "arm":
                        var scope = new SceneCheckpointScope(fields["studioSessionId"], fields["treeId"], fields["branchId"], long.Parse(fields["generation"], CultureInfo.InvariantCulture));
                        var binding = new SceneCheckpointBinding(fields["environmentSha256"], fields["initialSavesSha256"], fields["nativeProfileId"]);
                        var payload = SceneCheckpointPacket.EncodeSections(new Dictionary<string, byte[]>
                        { ["leanS0"] = Enumerable.Range(0, 100000).Select(n => (byte)n).ToArray(), ["executionFiles"] = new byte[] { 4 } },
                            SceneCheckpointHeader.LeanS0SchemaVersion);
                        Captured = new SceneCheckpointPacket(SceneCheckpointHeader.CreateLeanS0(fields["captureId"], scope, binding,
                            Enum.Parse<SceneCheckpointTrigger>(fields["trigger"]), 1, 4, 8, 4, 12, payload.Length, Sha256Utility.ComputeHex(payload)), payload);
                        reply["status"] = "Pending"; break;
                    case "status": reply = Captured!.Header.ToFields(); reply["status"] = "Captured"; reply["requestId"] = fields["requestId"]; break;
                    case "chunk":
                        var index = int.Parse(fields["index"], CultureInfo.InvariantCulture); var chunk = Captured!.ReadChunk(index);
                        if (Corrupt && index == 1) chunk[0] ^= 1;
                        reply["status"] = "Chunk"; reply["index"] = fields["index"]; reply["checkpointId"] = Captured.Header.CheckpointId;
                        reply["data"] = Convert.ToBase64String(chunk); OnChunk?.Invoke(index); break;
                    case "release": Captured = null; reply["status"] = "Released"; break;
                    case "cancel": if (Captured?.Header.CheckpointId == fields["captureId"]) Captured = null; reply["status"] = "Cancelled"; break;
                    case "import-begin": var header = SceneCheckpointHeader.FromFields(fields); import = new SceneCheckpointReceiver(header, header.Scope, header.Binding); reply["status"] = "ImportPending"; break;
                    case "import-chunk": import!.Append(int.Parse(fields["index"], CultureInfo.InvariantCulture), Convert.FromBase64String(fields["data"])); reply["status"] = "ImportPending"; break;
                    case "import-end": Staged = import!.Complete(); import = null; reply["status"] = "Staged"; reply["checkpointId"] = Staged.Header.CheckpointId; reply["restoreQualified"] = Staged.Header.RestoreQualified ? "true" : "false"; break;
                    case "import-cancel": import?.Dispose(); import = null; Staged = null; reply["status"] = "ImportCancelled"; break;
                    case "restore-start":
                    case "restore-apply":
                    case "restore-status":
                        reply["checkpointId"] = Staged!.Header.CheckpointId;
                        reply["restoreQualified"] = InvalidRestoreReceipt ? "invalid" : (Staged.Header.RestoreQualified ? "true" : "false");
                        reply["status"] = fields["phase"] == "restore-start" ? "LoadingInitialSave" : "RebuildingCToK";
                        if (fields["phase"] == "restore-apply") Applied = true;
                        reply["stateApplied"] = Applied ? "true" : "false";
                        OnRestore?.Invoke(); break;
                    case "restore-cancel": RestoreCancelled = true; reply["status"] = "Cancelled"; break;
                    default: throw new InvalidOperationException("Unexpected checkpoint phase.");
                }
                return Task.FromResult(Wire(reply, IpcMessageTypes.SceneCheckpointData));
            }
            private IReadOnlyDictionary<string, string> Wire(IReadOnlyDictionary<string, string> fields, string type)
            {
                var bytes = IpcCodec.SerializeEnvelope(new IpcEnvelope(1, session, ++sequence, type, IpcPayloadCodec.Serialize(fields)));
                Assert.IsTrue(bytes.Length < IpcEnvelope.MaximumMessageBytes);
                return IpcPayloadCodec.TryDeserialize(IpcCodec.TryDeserializeEnvelope(bytes).Envelope!.PayloadUtf8).Fields!;
            }
        }
    }
}
