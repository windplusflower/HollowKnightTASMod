using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Core.Ipc;
using HollowKnightTAS.Core.SceneCheckpoints;

namespace HollowKnightTAS.Companion.Services
{
    public sealed class SceneCheckpointClient
    {
        private readonly SceneCheckpointStore store;
        private readonly Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<IReadOnlyDictionary<string, string>>> request;
        public SceneCheckpointClient(SceneCheckpointStore store, RuntimeSessionClient runtime)
            : this(store, (fields, token) => RequestAsync(runtime, fields, token)) { }
        public SceneCheckpointClient(SceneCheckpointStore store,
            Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<IReadOnlyDictionary<string, string>>> request)
        { this.store = store ?? throw new ArgumentNullException(nameof(store)); this.request = request ?? throw new ArgumentNullException(nameof(request)); }

        public async Task<SceneCheckpointLease> ArmAsync(SceneCheckpointScope scope, SceneCheckpointBinding binding,
            SceneCheckpointTrigger trigger, CancellationToken token)
        {
            var lease = store.BeginCapture(scope, binding, trigger);
            var fields = new Dictionary<string, string>
            {
                ["phase"] = "arm", ["captureId"] = lease.CaptureId, ["studioSessionId"] = scope.StudioSessionId, ["treeId"] = scope.TreeId, ["branchId"] = scope.BranchId,
                ["generation"] = scope.Generation.ToString(CultureInfo.InvariantCulture), ["trigger"] = trigger.ToString(),
                ["environmentSha256"] = binding.EnvironmentSha256, ["initialSavesSha256"] = binding.InitialSavesSha256, ["nativeProfileId"] = binding.NativeProfileId
            };
            try { Expect(await Call(fields, token), "Pending"); return lease; }
            catch { store.Cancel(lease); await Cleanup("cancel", "captureId", lease.CaptureId); throw; }
        }
        public async Task<SceneCheckpointPacket> ReceiveAsync(SceneCheckpointLease lease, TimeSpan timeout, CancellationToken token)
        {
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(4)) throw new ArgumentOutOfRangeException(nameof(timeout));
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
            bounded.CancelAfter(timeout);
            try
            {
                IReadOnlyDictionary<string, string> response;
                while (true)
                {
                    _ = store.Catalog(lease.Scope); // A -> B -> A does not revive the old asynchronous capture.
                    response = await Call(new Dictionary<string, string> { ["phase"] = "status", ["captureId"] = lease.CaptureId }, bounded.Token);
                    if (response["status"] == "Captured") break;
                    Expect(response, "Pending"); await Task.Delay(50, bounded.Token);
                }
                var header = SceneCheckpointHeader.FromFields(response);
                if (header.Trigger != lease.Trigger || header.CheckpointId != lease.CaptureId) throw new InvalidDataException("Checkpoint capture identity changed.");
                using var receiver = new SceneCheckpointReceiver(header, lease.Scope, lease.Binding);
                var chunks = (header.PayloadBytes + SceneCheckpointPacket.ChunkBytes - 1) / SceneCheckpointPacket.ChunkBytes;
                for (var index = 0; index < chunks; index++)
                {
                    _ = store.Catalog(lease.Scope);
                    var chunk = await Call(new Dictionary<string, string> { ["phase"] = "chunk", ["checkpointId"] = header.CheckpointId,
                        ["index"] = index.ToString(CultureInfo.InvariantCulture) }, bounded.Token);
                    Expect(chunk, "Chunk");
                    if (chunk["checkpointId"] != header.CheckpointId || chunk["index"] != index.ToString(CultureInfo.InvariantCulture)
                        || chunk["data"].Length > ((SceneCheckpointPacket.ChunkBytes + 2) / 3) * 4)
                        throw new InvalidDataException("Checkpoint chunk identity or wire size mismatch.");
                    receiver.Append(index, Convert.FromBase64String(chunk["data"]));
                }
                var packet = receiver.Complete();
                // Retire Runtime's temporary buffer before publishing the complete Studio-owned packet.
                Expect(await Call(new Dictionary<string, string> { ["phase"] = "release", ["checkpointId"] = header.CheckpointId }, bounded.Token), "Released");
                return store.Publish(lease, packet);
            }
            catch { store.Cancel(lease); await Cleanup("cancel", "captureId", lease.CaptureId); throw; }
        }
        public async Task CancelAsync(SceneCheckpointLease lease, CancellationToken token)
        {
            store.Cancel(lease); Expect(await Call(new Dictionary<string, string> { ["phase"] = "cancel", ["captureId"] = lease.CaptureId }, token), "Cancelled");
        }
        // Copies data to a replacement process. Staging is not world application or a successful restore.
        public async Task StageAsync(string checkpointId, SceneCheckpointScope scope, SceneCheckpointBinding binding, CancellationToken token)
        {
            var packet = store.Get(checkpointId, scope, binding);
            try
            {
                var begin = packet.Header.ToFields(); begin["phase"] = "import-begin";
                Expect(await Call(begin, token), "ImportPending");
                for (var index = 0; index < packet.ChunkCount; index++)
                {
                    _ = store.Get(checkpointId, scope, binding);
                    Expect(await Call(new Dictionary<string, string> { ["phase"] = "import-chunk", ["checkpointId"] = checkpointId, ["index"] = index.ToString(CultureInfo.InvariantCulture),
                        ["data"] = Convert.ToBase64String(packet.ReadChunk(index)) }, token), "ImportPending");
                }
                _ = store.Get(checkpointId, scope, binding);
                var result = await Call(new Dictionary<string, string> { ["phase"] = "import-end", ["checkpointId"] = checkpointId }, token);
                Expect(result, "Staged");
                if (result["checkpointId"] != checkpointId || (result["restoreQualified"] != "false" && result["restoreQualified"] != "true"))
                    throw new InvalidDataException("Checkpoint staging reply is invalid.");
                _ = store.Get(checkpointId, scope, binding);
            }
            catch { await Cleanup("import-cancel", "checkpointId", checkpointId); throw; }
        }
        // Restoring an engine scene consumes no Movie history until resume.
        public Task StartRestoreAsync(string checkpointId, SceneCheckpointScope scope, SceneCheckpointBinding binding, CancellationToken token)
            => StartRestorePrototypeAsync(checkpointId, scope, binding, token);
        public Task ApplyRestoreAsync(string checkpointId, SceneCheckpointScope scope, SceneCheckpointBinding binding, CancellationToken token)
            => ApplyRestorePrototypeAsync(checkpointId, scope, binding, token);
        public Task CancelRestoreAsync(string checkpointId, CancellationToken token)
            => CancelRestorePrototypeAsync(checkpointId, token);

        public async Task StartRestorePrototypeAsync(string checkpointId, SceneCheckpointScope scope, SceneCheckpointBinding binding, CancellationToken token)
        {
            _ = store.Get(checkpointId, scope, binding);
            try
            {
                var result = await Call(new Dictionary<string, string> { ["phase"] = "restore-start", ["checkpointId"] = checkpointId }, token);
                Expect(result, "LoadingInitialSave"); ValidateRestoreReply(result, checkpointId);
                _ = store.Get(checkpointId, scope, binding);
            }
            catch { await Cleanup("restore-cancel", "checkpointId", checkpointId); throw; }
        }
        public async Task<IReadOnlyDictionary<string, string>> RestoreStatusAsync(string checkpointId, CancellationToken token)
        {
            var result = await Call(new Dictionary<string, string> { ["phase"] = "restore-status", ["checkpointId"] = checkpointId }, token);
            ValidateRestoreReply(result, checkpointId); return result;
        }
        public async Task ApplyRestorePrototypeAsync(string checkpointId, SceneCheckpointScope scope, SceneCheckpointBinding binding, CancellationToken token)
        {
            _ = store.Get(checkpointId, scope, binding);
            try
            {
                var result = await Call(new Dictionary<string, string> { ["phase"] = "restore-apply", ["checkpointId"] = checkpointId }, token);
                Expect(result, "RebuildingCToK"); ValidateRestoreReply(result, checkpointId);
                if (!result.TryGetValue("stateApplied", out var applied) || applied != "true") throw new InvalidDataException("Invalid checkpoint application receipt.");
                _ = store.Get(checkpointId, scope, binding);
            }
            catch { await Cleanup("restore-cancel", "checkpointId", checkpointId); throw; }
        }
        public Task CancelRestorePrototypeAsync(string checkpointId, CancellationToken token)
            => Call(new Dictionary<string, string> { ["phase"] = "restore-cancel", ["checkpointId"] = checkpointId }, token);
        private static void ValidateRestoreReply(IReadOnlyDictionary<string, string> result, string checkpointId)
        {
            if (!result.TryGetValue("checkpointId", out var id) || id != checkpointId
                || !result.TryGetValue("restoreQualified", out var qualified) || (qualified != "false" && qualified != "true"))
                throw new InvalidDataException("Invalid checkpoint restore identity or qualification.");
        }
        private Task<IReadOnlyDictionary<string, string>> Call(Dictionary<string, string> fields, CancellationToken token)
        { fields["requestId"] = "checkpoint-" + Guid.NewGuid().ToString("N"); return request(fields, token); }
        private async Task Cleanup(string phase, string identityKey, string identity)
        { try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await Call(new Dictionary<string, string> { ["phase"] = phase, [identityKey] = identity }, timeout.Token); } catch { } }
        private static void Expect(IReadOnlyDictionary<string, string> reply, string status)
        {
            if (!reply.TryGetValue("status", out var actual) || actual != status)
                throw new InvalidDataException("Checkpoint command failed: " + (reply.TryGetValue("detail", out var detail) ? detail : actual));
        }
        private static async Task<IReadOnlyDictionary<string, string>> RequestAsync(RuntimeSessionClient runtime,
            IReadOnlyDictionary<string, string> fields, CancellationToken token)
        {
            var completion = new TaskCompletionSource<IReadOnlyDictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Received(object? sender, IpcEnvelope envelope)
            {
                if (envelope.MessageType != IpcMessageTypes.SceneCheckpointData && envelope.MessageType != IpcMessageTypes.CommandRejected) return;
                var decoded = IpcPayloadCodec.TryDeserialize(envelope.PayloadUtf8);
                if (!decoded.Success || decoded.Fields == null || !decoded.Fields.TryGetValue("requestId", out var id) || id != fields["requestId"]) return;
                if (envelope.MessageType == IpcMessageTypes.SceneCheckpointData) completion.TrySetResult(decoded.Fields);
                else completion.TrySetException(new InvalidOperationException(decoded.Fields.TryGetValue("detail", out var detail) ? detail : "Checkpoint command rejected."));
            }
            runtime.EnvelopeReceived += Received;
            try
            {
                await runtime.SendCommandAsync(IpcMessageTypes.SceneCheckpoint, fields, token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                // This protected command executes Canon in the parked native
                // observation queue (already bounded to ten seconds). Keep its
                // reply bound aligned; the four-minute job/270s host stay fixed.
                var clockCanon = fields.TryGetValue("phase", out var phase) && phase == "s0-clock-recanon"
                    && Environment.GetEnvironmentVariable("HKTAS_LOADING_CLOCK_APPLY_EXPERIMENT") == "1";
                var actualD = fields.TryGetValue("phase", out var actualPhase) && actualPhase == "d-exp-apply"
                    && Environment.GetEnvironmentVariable("HKTAS_LOADING_ACTUAL_D_EXPERIMENT") == "1";
                timeout.CancelAfter(TimeSpan.FromSeconds(clockCanon || actualD ? 10 : 2));
                return await completion.Task.WaitAsync(timeout.Token);
            }
            finally { runtime.EnvelopeReceived -= Received; }
        }
    }
}
