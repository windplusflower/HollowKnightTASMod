using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Core.Ipc;
using HollowKnightTAS.Core.Automation;

namespace HollowKnightTAS.Companion.Services
{
    public sealed class RuntimeSessionClient : IDisposable
    {
        private static readonly ProtocolRange SupportedProtocols =
            CompanionProtocolMetadata.SupportedProtocols;

        private readonly CompanionSessionRegistration registration;
        private readonly string companionInstanceId;
        private readonly CancellationTokenSource cancellation =
            new CancellationTokenSource();
        private readonly SemaphoreSlim writeLock =
            new SemaphoreSlim(1, 1);
        private NamedPipeClientStream? pipe;
        private Task? readLoop;
        private long outgoingSequence;
        private IpcSequenceValidator? incomingSequence;
        private int disposed;

        public RuntimeSessionClient(
            CompanionSessionRegistration registration,
            string companionInstanceId)
        {
            this.registration = registration;
            this.companionInstanceId = companionInstanceId;
        }

        public event EventHandler<IpcEnvelope>? EnvelopeReceived;
        public event EventHandler? ConnectionChanged;

        public string SessionId => registration.SessionId;
        public int GameProcessId => registration.GameProcessId;
        public long GameProcessStartTimeUtcTicks =>
            registration.GameProcessStartTimeUtcTicks;
        public string EnvironmentManifestSha256 =>
            registration.EnvironmentManifestSha256;
        public string RuntimeAssemblySha256 =>
            registration.RuntimeAssemblySha256;
        public string CoreAssemblySha256 =>
            registration.CoreAssemblySha256;
        public bool NativeCapabilitiesRequested =>
            registration.NativeCapabilitiesRequested;
        public AutomationMode AutomationMode =>
            registration.AutomationMode;
        public bool DebugMutationEnabled =>
            registration.DebugMutationEnabled;
        public bool IsConnected { get; private set; }
        public string LastError { get; private set; } = string.Empty;
        public int NegotiatedProtocol { get; private set; }

        public bool MatchesRegistration(
            CompanionSessionRegistration candidate)
        {
            if (candidate == null
                || !string.Equals(
                    registration.SessionId,
                    candidate.SessionId,
                    StringComparison.Ordinal)
                || registration.GameProcessId
                != candidate.GameProcessId
                || registration.GameProcessStartTimeUtcTicks
                != candidate.GameProcessStartTimeUtcTicks
                || !string.Equals(
                    registration.PipeName,
                    candidate.PipeName,
                    StringComparison.Ordinal)
                || registration.NativeCapabilitiesRequested
                != candidate.NativeCapabilitiesRequested
                || registration.AutomationMode
                != candidate.AutomationMode
                || registration.DebugMutationEnabled
                != candidate.DebugMutationEnabled)
            {
                return false;
            }

            if (!string.Equals(
                    registration.EnvironmentManifestSha256,
                    candidate.EnvironmentManifestSha256,
                    StringComparison.Ordinal))
            {
                Trace.TraceWarning(
                    $"MatchesRegistration: EnvironmentManifestSha256 differs for session '{registration.SessionId}'; continuing match.");
            }

            if (!string.Equals(
                    registration.RuntimeAssemblySha256,
                    candidate.RuntimeAssemblySha256,
                    StringComparison.Ordinal))
            {
                Trace.TraceWarning(
                    $"MatchesRegistration: RuntimeAssemblySha256 differs for session '{registration.SessionId}'; continuing match.");
            }

            if (!string.Equals(
                    registration.CoreAssemblySha256,
                    candidate.CoreAssemblySha256,
                    StringComparison.Ordinal))
            {
                Trace.TraceWarning(
                    $"MatchesRegistration: CoreAssemblySha256 differs for session '{registration.SessionId}'; continuing match.");
            }

            if (!registration.ProtocolRange.Equals(candidate.ProtocolRange))
            {
                Trace.TraceWarning(
                    $"MatchesRegistration: ProtocolRange differs for session '{registration.SessionId}' ({registration.ProtocolRange} vs {candidate.ProtocolRange}); continuing match.");
            }

            var expected = registration.Token;
            var supplied = candidate.Token;
            var difference = expected.Length ^ supplied.Length;
            var length = Math.Min(
                expected.Length,
                supplied.Length);
            for (var index = 0; index < length; index++)
            {
                difference |= expected[index] ^ supplied[index];
            }

            return difference == 0;
        }

        public async Task ConnectAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (IsConnected)
            {
                return;
            }

            if (!SupportedProtocols.TryNegotiate(
                    registration.ProtocolRange,
                    out var protocol))
            {
                protocol = CompanionProtocolMetadata.ProtocolMaximum;
                Trace.TraceWarning(
                    $"RuntimeSessionClient.ConnectAsync: protocol range mismatch (companion={SupportedProtocols}, runtime={registration.ProtocolRange}); falling back to protocol {protocol}.");
            }

            using (var linked =
                   CancellationTokenSource.CreateLinkedTokenSource(
                       cancellationToken,
                       cancellation.Token))
            {
                linked.CancelAfter(timeout);
                var client = new NamedPipeClientStream(
                    ".",
                    registration.PipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                try
                {
                    await client.ConnectAsync(linked.Token);
                    var clientNonce = new byte[32];
                    using (var random =
                           RandomNumberGenerator.Create())
                    {
                        random.GetBytes(clientNonce);
                    }

                    await IpcCodec.WriteFrameAsync(
                        client,
                        new IpcEnvelope(
                            protocol,
                            registration.SessionId,
                            0,
                            IpcMessageTypes.Hello,
                            IpcHandshakeValidator.CreateHelloPayload(
                                CompanionProtocolMetadata.Version,
                                SupportedProtocols,
                                registration.GameProcessId,
                                registration.SessionId,
                                companionInstanceId,
                                clientNonce,
                                registration.Token)),
                        linked.Token);
                    var ack = await IpcCodec.ReadFrameAsync(
                        client,
                        linked.Token);
                    ValidateHelloAck(ack, protocol);
                    pipe = client;
                    client = null!;
                    NegotiatedProtocol = protocol;
                    incomingSequence = new IpcSequenceValidator(0);
                    outgoingSequence = 0;
                    LastError = string.Empty;
                    IsConnected = true;
                    ConnectionChanged?.Invoke(this, EventArgs.Empty);
                    readLoop = Task.Run(
                        () => ReadLoopAsync(cancellation.Token));
                }
                finally
                {
                    client?.Dispose();
                }
            }
        }

        public async Task SendCommandAsync(
            string messageType,
            IReadOnlyDictionary<string, string> fields,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!IpcMessageTypes.IsRuntimeCommand(messageType))
            {
                throw new ArgumentException(
                    "Message is not a Runtime command.",
                    nameof(messageType));
            }

            var active = pipe;
            if (!IsConnected || active == null)
            {
                throw new InvalidOperationException(
                    "Runtime session is not connected.");
            }

            var payload = IpcPayloadCodec.Serialize(fields);
            await writeLock.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                active = pipe;
                if (!IsConnected || active == null)
                {
                    throw new InvalidOperationException(
                        "Runtime session is not connected.");
                }

                var sequence = checked(outgoingSequence + 1);
                var envelope = new IpcEnvelope(
                    NegotiatedProtocol,
                    registration.SessionId,
                    sequence,
                    messageType,
                    payload);
                // Once we enter the critical section and assign a sequence number, bind the frame
                // write to the connection lifetime token so caller timeouts never leave a half-written
                // frame or an unconsumed sequence gap on the named pipe.
                await IpcCodec.WriteFrameAsync(
                    active,
                    envelope,
                    cancellation.Token);
                outgoingSequence = sequence;
            }
            finally
            {
                writeLock.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            cancellation.Cancel();
            pipe?.Dispose();
            pipe = null;
            IsConnected = false;
            try
            {
                readLoop?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }

            writeLock.Dispose();
            cancellation.Dispose();
        }

        private async Task ReadLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && pipe != null)
                {
                    var envelope = await IpcCodec.ReadFrameAsync(
                        pipe,
                        token);
                    if (!string.Equals(
                            envelope.SessionId,
                            registration.SessionId,
                            StringComparison.Ordinal))
                    {
                        throw new IpcProtocolException(
                            "InvalidRuntimeEvent",
                            "Runtime event binding is invalid.");
                    }

                    if (envelope.ProtocolVersion != NegotiatedProtocol)
                    {
                        Trace.TraceWarning(
                            $"RuntimeSessionClient.ReadLoopAsync: event ProtocolVersion {envelope.ProtocolVersion} differs from negotiated {NegotiatedProtocol}; continuing.");
                    }

                    if (incomingSequence == null)
                    {
                        throw new IpcProtocolException(
                            "InvalidRuntimeEvent",
                            "Runtime event sequence validator is unavailable.");
                    }

                    if (!incomingSequence.TryAccept(
                            envelope.Sequence,
                            out var sequenceError))
                    {
                        throw new IpcProtocolException(
                            "InvalidRuntimeEvent",
                            sequenceError);
                    }

                    if (!IpcMessageTypes.IsRuntimeEvent(
                            envelope.MessageType))
                    {
                        throw new IpcProtocolException(
                            "InvalidRuntimeEvent",
                            "Runtime event type is not allowed.");
                    }

                    EnvelopeReceived?.Invoke(this, envelope);
                }
            }
            catch (OperationCanceledException)
                when (token.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                LastError = exception.GetType().Name
                            + ": "
                            + exception.Message;
            }
            finally
            {
                IsConnected = false;
                pipe?.Dispose();
                pipe = null;
                ConnectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void ValidateHelloAck(
            IpcEnvelope ack,
            int protocol)
        {
            if (!string.Equals(
                    ack.MessageType,
                    IpcMessageTypes.HelloAck,
                    StringComparison.Ordinal)
                || ack.Sequence != 0
                || !string.Equals(
                    ack.SessionId,
                    registration.SessionId,
                    StringComparison.Ordinal))
            {
                throw new IpcProtocolException(
                    "InvalidHelloAck",
                    "Runtime hello acknowledgement is invalid.");
            }

            if (ack.ProtocolVersion != protocol)
            {
                Trace.TraceWarning(
                    $"RuntimeSessionClient.ValidateHelloAck: envelope ProtocolVersion {ack.ProtocolVersion} differs from expected {protocol}; continuing.");
            }

            var payload = IpcPayloadCodec.TryDeserialize(
                ack.PayloadUtf8);
            if (!payload.Success
                || payload.Fields == null
                || payload.Fields.Count != 4
                || !payload.Fields.TryGetValue(
                    "companionInstanceId",
                    out var instance)
                || !string.Equals(
                    instance,
                    companionInstanceId,
                    StringComparison.Ordinal)
                || !payload.Fields.TryGetValue(
                    "runtimeSessionId",
                    out var session)
                || !string.Equals(
                    session,
                    registration.SessionId,
                    StringComparison.Ordinal)
                || !payload.Fields.TryGetValue(
                    "protocol",
                    out var protocolText)
                || !int.TryParse(
                    protocolText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var acknowledgedProtocol)
                || !payload.Fields.TryGetValue(
                    "serverNonce",
                    out var nonce)
                || !TryValidateNonce(nonce))
            {
                throw new IpcProtocolException(
                    "InvalidHelloAck",
                    "Runtime hello acknowledgement payload is invalid.");
            }

            if (acknowledgedProtocol != protocol)
            {
                Trace.TraceWarning(
                    $"RuntimeSessionClient.ValidateHelloAck: payload protocol {acknowledgedProtocol} differs from expected {protocol}; continuing.");
            }
        }

        private static bool TryValidateNonce(string value)
        {
            try
            {
                return Convert.FromBase64String(value).Length == 32;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(RuntimeSessionClient));
            }
        }
    }
}
