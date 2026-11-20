using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HollowKnightTAS.Core.SceneCheckpoints
{
    public sealed class SceneCheckpointReceiver : IDisposable
    {
        private readonly SceneCheckpointHeader header;
        private byte[]? buffer;
        private int offset;
        private int nextChunk;
        public SceneCheckpointHeader Header => header;
        public SceneCheckpointReceiver(SceneCheckpointHeader header, SceneCheckpointScope scope, SceneCheckpointBinding binding)
        {
            this.header = header ?? throw new ArgumentNullException(nameof(header));
            if (!header.Scope.Equals(scope) || !header.Binding.Equals(binding))
                throw new InvalidDataException("Checkpoint ownership or compatibility mismatch.");
            buffer = new byte[header.PayloadBytes];
        }
        public void Append(int index, byte[] bytes)
        {
            var active = buffer ?? throw new InvalidOperationException("Checkpoint transfer is no longer pending.");
            if (bytes == null || index != nextChunk || bytes.Length != Math.Min(SceneCheckpointPacket.ChunkBytes, active.Length - offset)
                || bytes.Length == 0)
            {
                Dispose(); throw new InvalidDataException("Checkpoint chunk is missing, duplicated, reordered or incorrectly sized.");
            }
            Buffer.BlockCopy(bytes, 0, active, offset, bytes.Length); offset += bytes.Length; nextChunk++;
        }
        public SceneCheckpointPacket Complete()
        {
            var active = buffer ?? throw new InvalidOperationException("Checkpoint transfer is no longer pending.");
            try
            {
                if (offset != active.Length) throw new InvalidDataException("Checkpoint transfer was truncated.");
                return new SceneCheckpointPacket(header, active);
            }
            finally { Dispose(); }
        }
        public void Dispose() { if (buffer != null) Array.Clear(buffer, 0, buffer.Length); buffer = null; }
    }

    public sealed class SceneCheckpointLease
    {
        internal SceneCheckpointLease(SceneCheckpointScope scope, SceneCheckpointBinding binding, SceneCheckpointTrigger trigger)
        { Scope = scope; Binding = binding; Trigger = trigger; }
        public SceneCheckpointScope Scope { get; }
        public SceneCheckpointBinding Binding { get; }
        public SceneCheckpointTrigger Trigger { get; }
        public string CaptureId { get; } = Guid.NewGuid().ToString("N");
        public string State { get; internal set; } = "Pending";
    }

    // Studio owns this object across game-process replacements. There is deliberately no file/path API.
    public sealed class SceneCheckpointStore : IDisposable
    {
        public const int MaximumStoredBytes = 128 * 1024 * 1024;
        private readonly object sync = new object();
        private readonly Dictionary<string, SceneCheckpointPacket> packets = new Dictionary<string, SceneCheckpointPacket>(StringComparer.Ordinal);
        private SceneCheckpointLease? pending;
        private SceneCheckpointScope? active;
        private long generation;
        private int storedBytes;
        private bool disposed;
        public string StudioSessionId { get; } = Guid.NewGuid().ToString("N");
        public SceneCheckpointScope ActivateBranch(string treeId, string branchId)
        {
            lock (sync)
            {
                CheckAlive();
                if (active != null && active.TreeId == treeId && active.BranchId == branchId) return active;
                var next = new SceneCheckpointScope(StudioSessionId, treeId, branchId, checked(generation + 1));
                RejectPending(); generation = next.Generation; return active = next;
            }
        }
        public SceneCheckpointLease BeginCapture(SceneCheckpointScope scope, SceneCheckpointBinding binding, SceneCheckpointTrigger trigger)
        {
            lock (sync)
            {
                RequireActive(scope);
                if (pending != null) throw new InvalidOperationException("A checkpoint capture is already pending.");
                if (!Enum.IsDefined(typeof(SceneCheckpointTrigger), trigger)) throw new ArgumentOutOfRangeException(nameof(trigger));
                return pending = new SceneCheckpointLease(scope, binding ?? throw new ArgumentNullException(nameof(binding)), trigger);
            }
        }
        public SceneCheckpointPacket Publish(SceneCheckpointLease lease, SceneCheckpointPacket packet)
        {
            lock (sync)
            {
                try
                {
                    RequireActive(lease.Scope);
                    if (!ReferenceEquals(pending, lease) || lease.State != "Pending" || packet.Header.CheckpointId != lease.CaptureId || !packet.Header.Scope.Equals(lease.Scope)
                        || !packet.Header.Binding.Equals(lease.Binding) || packet.Header.Trigger != lease.Trigger)
                        throw new InvalidDataException("Stale or mismatched checkpoint capture.");
                    lease.State = "Captured";
                    var duplicate = packets.Values.FirstOrDefault(p => SameBranch(p.Header.Scope, lease.Scope)
                        && p.Header.Binding.Equals(packet.Header.Binding)
                        && p.Header.TransitionId == packet.Header.TransitionId
                        && p.Header.CaptureNativeFrame == packet.Header.CaptureNativeFrame);
                    if (duplicate != null)
                    {
                        if (duplicate.Header.PayloadSha256 != packet.Header.PayloadSha256
                            || duplicate.Header.ResumeMovieFrame != packet.Header.ResumeMovieFrame)
                            throw new InvalidDataException("The same checkpoint boundary produced different data.");
                        lease.State = "Published"; pending = null; return duplicate;
                    }
                    if (packets.ContainsKey(packet.Header.CheckpointId)) throw new InvalidDataException("Checkpoint ID collision.");
                    if (storedBytes > MaximumStoredBytes - packet.Header.PayloadBytes) throw new InvalidOperationException("Studio checkpoint memory budget is full.");
                    packets.Add(packet.Header.CheckpointId, packet); storedBytes += packet.Header.PayloadBytes;
                    lease.State = "Published"; pending = null; return packet;
                }
                catch { if (lease.State != "Published") lease.State = "Rejected"; if (ReferenceEquals(pending, lease)) pending = null; throw; }
            }
        }
        public SceneCheckpointPacket Get(string checkpointId, SceneCheckpointScope scope, SceneCheckpointBinding binding)
        {
            lock (sync)
            {
                RequireActive(scope);
                if (!packets.TryGetValue(checkpointId, out var packet) || !SameBranch(packet.Header.Scope, scope) || !packet.Header.Binding.Equals(binding))
                    throw new InvalidDataException("Checkpoint is absent or belongs to another branch/environment.");
                return packet;
            }
        }
        public IReadOnlyList<SceneCheckpointHeader> Catalog(SceneCheckpointScope scope)
        {
            lock (sync) { RequireActive(scope); return Array.AsReadOnly(packets.Values.Where(p => SameBranch(p.Header.Scope, scope)).Select(p => p.Header).ToArray()); }
        }
        public IReadOnlyList<SceneCheckpointHeader> CatalogTree(string treeId)
        {
            lock (sync)
            {
                CheckAlive();
                SceneCheckpointScope.RequireId(treeId);
                return Array.AsReadOnly(packets.Values
                    .Where(p => p.Header.Scope.StudioSessionId == StudioSessionId && p.Header.Scope.TreeId == treeId)
                    .OrderBy(p => p.Header.ResumeMovieFrame)
                    .ThenBy(p => p.Header.TransitionId)
                    .Select(p => p.Header)
                    .ToArray());
            }
        }
        public IReadOnlyList<SceneCheckpointPacket> GetTreePackets(string treeId)
        {
            lock (sync)
            {
                CheckAlive();
                SceneCheckpointScope.RequireId(treeId);
                return Array.AsReadOnly(packets.Values
                    .Where(p => p.Header.Scope.StudioSessionId == StudioSessionId && p.Header.Scope.TreeId == treeId)
                    .OrderBy(p => p.Header.ResumeMovieFrame)
                    .ThenBy(p => p.Header.TransitionId)
                    .ToArray());
            }
        }
        public SceneCheckpointPacket ImportPacket(SceneCheckpointPacket packet)
        {
            if (packet == null) throw new ArgumentNullException(nameof(packet));
            lock (sync)
            {
                CheckAlive();
                var targetGeneration = Math.Max(generation, 1L);
                var reBound = packet.WithScope(new SceneCheckpointScope(
                    StudioSessionId,
                    packet.Header.Scope.TreeId,
                    packet.Header.Scope.BranchId,
                    targetGeneration));
                if (packets.TryGetValue(reBound.Header.CheckpointId, out var existing))
                {
                    if (existing.Header.PayloadSha256 != reBound.Header.PayloadSha256)
                        throw new InvalidDataException("Imported checkpoint ID collision with different payload.");
                    return existing;
                }
                var duplicate = packets.Values.FirstOrDefault(p =>
                    p.Header.Scope.StudioSessionId == StudioSessionId
                    && p.Header.Scope.TreeId == reBound.Header.Scope.TreeId
                    && p.Header.Scope.BranchId == reBound.Header.Scope.BranchId
                    && p.Header.Binding.Equals(reBound.Header.Binding)
                    && p.Header.TransitionId == reBound.Header.TransitionId
                    && p.Header.CaptureNativeFrame == reBound.Header.CaptureNativeFrame
                    && p.Header.PayloadSha256 == reBound.Header.PayloadSha256);
                if (duplicate != null) return duplicate;
                if (storedBytes > MaximumStoredBytes - reBound.Header.PayloadBytes)
                    throw new InvalidOperationException("Studio checkpoint memory budget is full.");
                packets.Add(reBound.Header.CheckpointId, reBound);
                storedBytes += reBound.Header.PayloadBytes;
                return reBound;
            }
        }
        public bool RemoveCheckpoint(string checkpointId)
        {
            lock (sync)
            {
                CheckAlive();
                if (!packets.TryGetValue(checkpointId, out var packet)) return false;
                packets.Remove(checkpointId);
                storedBytes -= packet.Header.PayloadBytes;
                return true;
            }
        }
        public void Cancel(SceneCheckpointLease lease)
        {
            lock (sync) { if (ReferenceEquals(pending, lease)) RejectPending(); else if (lease.State == "Pending") lease.State = "Rejected"; }
        }
        public void DeleteBranch(string treeId, string branchId)
        {
            lock (sync)
            {
                CheckAlive(); SceneCheckpointScope.RequireId(treeId); SceneCheckpointScope.RequireId(branchId);
                foreach (var packet in packets.Values.Where(p => p.Header.Scope.TreeId == treeId && p.Header.Scope.BranchId == branchId).ToArray())
                { packets.Remove(packet.Header.CheckpointId); storedBytes -= packet.Header.PayloadBytes; }
                if (active?.TreeId == treeId && active.BranchId == branchId) { RejectPending(); active = null; }
            }
        }
        public void Dispose() { lock (sync) { RejectPending(); packets.Clear(); storedBytes = 0; active = null; disposed = true; } }
        private static bool SameBranch(SceneCheckpointScope a, SceneCheckpointScope b) => a.StudioSessionId == b.StudioSessionId && a.BranchKey == b.BranchKey;
        private void RejectPending() { if (pending != null) pending.State = "Rejected"; pending = null; }
        private void CheckAlive() { if (disposed) throw new ObjectDisposedException(nameof(SceneCheckpointStore)); }
        private void RequireActive(SceneCheckpointScope scope) { CheckAlive(); if (active == null || !active.Equals(scope)) throw new InvalidDataException("Checkpoint branch/generation is no longer active."); }
    }
}
