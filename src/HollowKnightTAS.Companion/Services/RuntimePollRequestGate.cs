using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Core.Ipc;

namespace HollowKnightTAS.Companion.Services
{
    // One automatic poll may remain in the Runtime FIFO after the UI's normal
    // two-second response timeout. Retain only its identity, not another timer
    // or retry, until its actual reply/disconnect. Explicit commands are unchanged.
    internal sealed class RuntimePollRequestGate : IDisposable
    {
        private readonly object sync = new();
        private RuntimeSessionClient? pending;
        private string? requestId;
        private EventHandler<IpcEnvelope>? received;
        private EventHandler? changed;
        private long generation;
        private bool disposed;
        internal bool IsPending(RuntimeSessionClient? selected)
        {
            lock (sync) {
                if (disposed) return true;
                if (pending != null && (!ReferenceEquals(pending, selected) || !pending.IsConnected)) Clear();
                return pending != null;
            }
        }
        internal long Begin(RuntimeSessionClient client, string id, string replyType)
        {
            lock (sync) {
                if (disposed) throw new ObjectDisposedException(nameof(RuntimePollRequestGate));
                if (pending != null && (!ReferenceEquals(pending, client) || !pending.IsConnected)) Clear();
                if (pending != null) throw new InvalidOperationException("An automatic Runtime poll is still pending.");
                if (!client.IsConnected || string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Automatic poll has no connected session/request identity.");
                pending = client; requestId = id; var epoch = ++generation;
                received = (sender, envelope) => {
                    if (!ReferenceEquals(sender, client) || envelope.MessageType != replyType
                        && envelope.MessageType != IpcMessageTypes.CommandRejected && envelope.MessageType != IpcMessageTypes.Fault) return;
                    var payload = IpcPayloadCodec.TryDeserialize(envelope.PayloadUtf8);
                    if (!payload.Success || payload.Fields == null || !payload.Fields.TryGetValue("requestId", out var responseId) || responseId != id) return;
                    lock (sync) if (epoch == generation && ReferenceEquals(pending, client) && requestId == id) Clear();
                };
                changed = (sender, _) => {
                    lock (sync) if (ReferenceEquals(sender, client) && epoch == generation && ReferenceEquals(pending, client) && !client.IsConnected) Clear();
                };
                client.EnvelopeReceived += received; client.ConnectionChanged += changed;
                // Close the disconnect race between initial admission and subscription.
                if (!client.IsConnected) Clear();
                return epoch;
            }
        }
        internal void Abandon(RuntimeSessionClient client, string id, long epoch)
        { lock (sync) if (ReferenceEquals(pending, client) && requestId == id && epoch == generation) Clear(); }
        internal void Reset() { lock (sync) Clear(); }
        internal async Task<IReadOnlyDictionary<string, string>> RequestAsync(RuntimeSessionClient client, IReadOnlyDictionary<string, string> fields,
            Func<RuntimeSessionClient, IReadOnlyDictionary<string, string>, CancellationToken, Task<IReadOnlyDictionary<string, string>>> request,
            CancellationToken token)
        {
            var id = fields["requestId"]; var epoch = Begin(client, id, IpcMessageTypes.WorldSnapshot);
            try { var response = await request(client, fields, token); Abandon(client, id, epoch); return response; }
            catch (TimeoutException) { throw; }
            catch { Abandon(client, id, epoch); throw; }
        }
        private void Clear()
        {
            var client = pending; var oldReceived = received; var oldChanged = changed;
            pending = null; requestId = null; received = null; changed = null; generation++;
            if (client != null) { client.EnvelopeReceived -= oldReceived; client.ConnectionChanged -= oldChanged; }
        }
        public void Dispose() { lock (sync) { if (disposed) return; disposed = true; Clear(); } }
    }
}
