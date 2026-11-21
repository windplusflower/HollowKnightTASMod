using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Core.Ipc;

namespace HollowKnightTAS.Companion.Services
{
    public sealed class SessionRegistry : IDisposable
    {
        private readonly object sync = new object();
        private readonly Dictionary<string, RuntimeSessionClient> clients =
            new Dictionary<string, RuntimeSessionClient>(
                StringComparer.Ordinal);
        private readonly string companionInstanceId;
        private bool disposed;

        public SessionRegistry(string companionInstanceId)
        {
            if (!IpcIdentifier.IsValid(companionInstanceId, 128))
            {
                throw new ArgumentException(
                    "Companion instance ID is invalid.",
                    nameof(companionInstanceId));
            }

            this.companionInstanceId = companionInstanceId;
        }

        public event EventHandler? SessionsChanged;
        public event EventHandler<SessionEnvelopeEventArgs>?
            EnvelopeReceived;

        public string CompanionInstanceId => companionInstanceId;
        public string LastRegistrationError { get; private set; } =
            string.Empty;

        public int ConnectedCount
        {
            get
            {
                lock (sync)
                {
                    return clients.Values.Count(
                        value => value.IsConnected);
                }
            }
        }

        public IReadOnlyList<RuntimeSessionClient> Sessions
        {
            get
            {
                lock (sync)
                {
                    return clients.Values
                        .OrderBy(
                            value => value.SessionId,
                            StringComparer.Ordinal)
                        .ToArray();
                }
            }
        }

        public async Task<bool> RegisterAsync(
            CompanionSessionRegistration registration,
            CancellationToken cancellationToken)
        {
            RuntimeSessionClient? old = null;
            lock (sync)
            {
                ThrowIfDisposed();
                if (clients.TryGetValue(
                        registration.SessionId,
                        out var existing))
                {
                    if (existing.IsConnected
                        && existing.GameProcessId
                        == registration.GameProcessId)
                    {
                        return true;
                    }

                    old = existing;
                    clients.Remove(registration.SessionId);
                }
            }

            old?.Dispose();
            var client = new RuntimeSessionClient(
                registration,
                companionInstanceId);
            client.EnvelopeReceived += OnEnvelopeReceived;
            client.ConnectionChanged += OnConnectionChanged;
            try
            {
                await client.ConnectAsync(
                    TimeSpan.FromSeconds(10),
                    cancellationToken);
            }
            catch (Exception exception)
            {
                LastRegistrationError =
                    exception.GetType().Name
                    + ":"
                    + Sanitize(exception.Message);
                client.Dispose();
                return false;
            }

            lock (sync)
            {
                if (disposed)
                {
                    client.Dispose();
                    return false;
                }

                clients[registration.SessionId] = client;
            }

            LastRegistrationError = string.Empty;
            SessionsChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public bool Remove(
            CompanionSessionRegistration registration)
        {
            RuntimeSessionClient? client = null;
            lock (sync)
            {
                ThrowIfDisposed();
                if (!clients.TryGetValue(
                        registration.SessionId,
                        out var existing))
                {
                    return false;
                }

                if (!existing.MatchesRegistration(
                        registration))
                {
                    Trace.TraceWarning(
                        $"SessionRegistry.Remove rejected mismatched registration for session '{registration.SessionId}'.");
                    return false;
                }

                clients.Remove(registration.SessionId);
                client = existing;
            }

            client.EnvelopeReceived -= OnEnvelopeReceived;
            client.ConnectionChanged -= OnConnectionChanged;
            client.Dispose();
            SessionsChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public int RemoveSessionsForProcess(int processId)
        {
            var removed = new List<RuntimeSessionClient>();
            lock (sync)
            {
                if (disposed) return 0;
                foreach (var pair in clients.Where(p => p.Value.GameProcessId == processId || !p.Value.IsConnected).ToArray())
                {
                    clients.Remove(pair.Key);
                    removed.Add(pair.Value);
                }
            }

            foreach (var client in removed)
            {
                client.EnvelopeReceived -= OnEnvelopeReceived;
                client.ConnectionChanged -= OnConnectionChanged;
                client.Dispose();
            }

            if (removed.Count > 0) SessionsChanged?.Invoke(this, EventArgs.Empty);
            return removed.Count;
        }

        public void Dispose()
        {
            RuntimeSessionClient[] values;
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                values = clients.Values.ToArray();
                clients.Clear();
            }

            foreach (var value in values)
            {
                value.EnvelopeReceived -= OnEnvelopeReceived;
                value.ConnectionChanged -= OnConnectionChanged;
                value.Dispose();
            }
        }

        private void OnEnvelopeReceived(
            object? sender,
            IpcEnvelope envelope)
        {
            if (sender is RuntimeSessionClient session)
            {
                EnvelopeReceived?.Invoke(
                    this,
                    new SessionEnvelopeEventArgs(
                        session,
                        envelope));
            }
        }

        private void OnConnectionChanged(
            object? sender,
            EventArgs eventArgs)
        {
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(
                    nameof(SessionRegistry));
            }
        }

        private static string Sanitize(string value)
        {
            var sanitized = (value ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\\', '/');
            return sanitized.Substring(
                0,
                Math.Min(256, sanitized.Length));
        }
    }

    public sealed class SessionEnvelopeEventArgs : EventArgs
    {
        public SessionEnvelopeEventArgs(
            RuntimeSessionClient session,
            IpcEnvelope envelope)
        {
            Session = session;
            Envelope = envelope;
        }

        public RuntimeSessionClient Session { get; }
        public IpcEnvelope Envelope { get; }
    }
}
