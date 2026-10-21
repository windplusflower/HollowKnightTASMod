using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Core.Ipc;

namespace HollowKnightTAS.Runtime.Ipc
{
    public sealed class NamedPipeRuntimeServer : IDisposable
    {
        private static readonly ProtocolRange RuntimeProtocols =
            new ProtocolRange(1, 1);

        private readonly string sessionId;
        private readonly int gameProcessId;
        private readonly byte[] token;
        private readonly RuntimeCommandQueue commands;
        private readonly BoundedIpcQueue<OutboundEvent> outbound;
        private readonly SemaphoreSlim outboundSignal =
            new SemaphoreSlim(0);
        private readonly CancellationTokenSource cancellation =
            new CancellationTokenSource();
        private readonly TaskCompletionSource<bool> firstAuthentication =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Action<string> logInfo;
        private readonly Action<string> logWarning;
        private readonly Action<string> logError;
        private readonly object connectionSync = new object();
        private Task? serverLoop;
        private NativeNamedPipeServer? currentServer;
        private int connected;
        private int disconnectPending;
        private int localDisconnectRequested;
        private int disposed;
        private long connectionGeneration;
        private string? authenticatedCompanionInstanceId;

        public NamedPipeRuntimeServer(
            string sessionId,
            int gameProcessId,
            string pipeName,
            byte[] token,
            RuntimeCommandQueue commands,
            int outboundCapacity,
            Action<string> logInfo,
            Action<string> logWarning,
            Action<string> logError)
        {
            if (!IpcIdentifier.IsValid(sessionId, 128))
            {
                throw new ArgumentException(
                    "Runtime session ID is invalid.",
                    nameof(sessionId));
            }

            if (gameProcessId <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(gameProcessId));
            }

            if (!IpcIdentifier.IsValid(pipeName, 240))
            {
                throw new ArgumentException(
                    "Runtime pipe name is invalid.",
                    nameof(pipeName));
            }

            if (token == null || token.Length != 32)
            {
                throw new ArgumentException(
                    "Session token must contain 32 bytes.",
                    nameof(token));
            }

            this.sessionId = sessionId;
            this.gameProcessId = gameProcessId;
            PipeName = pipeName;
            this.token = (byte[])token.Clone();
            this.commands = commands;
            outbound = new BoundedIpcQueue<OutboundEvent>(
                outboundCapacity);
            this.logInfo = logInfo;
            this.logWarning = logWarning;
            this.logError = logError;
        }

        public string PipeName { get; }
        public bool IsConnected => Volatile.Read(ref connected) != 0;
        public long ConnectionGeneration =>
            Interlocked.Read(ref connectionGeneration);
        public long OutboundRejectedCount => outbound.RejectedCount;
        public Task FirstAuthentication => firstAuthentication.Task;
        public string AuthenticatedCompanionInstanceId =>
            Volatile.Read(ref authenticatedCompanionInstanceId)
            ?? string.Empty;

        public event Action? Authenticated;

        public void Start()
        {
            ThrowIfDisposed();
            if (serverLoop != null)
            {
                return;
            }

            serverLoop = Task.Run(
                () => RunServerLoopAsync(cancellation.Token));
        }

        public bool TryPublish(
            string messageType,
            IReadOnlyDictionary<string, string> fields)
        {
            if (!IsConnected
                || !IpcMessageTypes.IsRuntimeEvent(messageType))
            {
                return false;
            }

            byte[] payload;
            var headerTiming = messageType == IpcMessageTypes.SceneCheckpointData
                ? FullRun.CanonicalSourceHeaderTiming.Request(fields) : null;
            try
            {
                FullRun.CanonicalSourceHeaderTiming.Mark(headerTiming, "before-reply-serialization");
                payload = IpcPayloadCodec.Serialize(fields);
                FullRun.CanonicalSourceHeaderTiming.Mark(headerTiming, "after-reply-serialization");
            }
            catch (Exception exception)
            {
                logWarning(
                    "T12 outbound event rejected before queue: "
                    + exception.Message);
                return false;
            }

            if (!outbound.TryEnqueue(
                    new OutboundEvent(messageType, payload, headerTiming)))
            {
                return false;
            }

            outboundSignal.Release();
            FullRun.CanonicalSourceHeaderTiming.Mark(headerTiming, "reply-enqueued");
            return true;
        }

        public bool ConsumeDisconnectPending()
        {
            return Interlocked.Exchange(
                       ref disconnectPending,
                       0)
                   != 0;
        }

        public void DisconnectCurrent()
        {
            Interlocked.Exchange(
                ref localDisconnectRequested,
                1);
            lock (connectionSync)
            {
                currentServer?.Dispose();
            }
        }

        public void BeginShutdown()
        {
            cancellation.Cancel();
            DisconnectCurrent();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            BeginShutdown();
            lock (connectionSync)
            {
                currentServer?.Dispose();
            }

            try
            {
                serverLoop?.Wait(TimeSpan.FromSeconds(3));
            }
            catch (AggregateException)
            {
            }

            commands.Clear();
            outbound.Clear();
            outboundSignal.Dispose();
            cancellation.Dispose();
        }

        private async Task RunServerLoopAsync(
            CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                NativeNamedPipeServer? server = null;
                try
                {
                    server = CreateServerInstance();
                    lock (connectionSync)
                    {
                        currentServer = server;
                    }

                    server.WaitForConnection();
                    await HandleConnectionAsync(
                            server.ReadStream,
                            server.WriteStream,
                            token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException)
                    when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (IOException)
                    when (token.IsCancellationRequested
                          || Interlocked.Exchange(
                                 ref localDisconnectRequested,
                                 0) != 0)
                {
                }
                catch (Exception exception)
                {
                    logWarning(
                        "T12 Runtime pipe connection fault: "
                        + exception);
                }
                finally
                {
                    lock (connectionSync)
                    {
                        if (ReferenceEquals(currentServer, server))
                        {
                            currentServer = null;
                        }
                    }

                    server?.Dispose();
                    if (Interlocked.Exchange(ref connected, 0) != 0)
                    {
                        Interlocked.Exchange(
                            ref disconnectPending,
                            1);
                        outbound.Clear();
                        commands.Clear();
                    }
                }

                if (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(100, token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }

        private async Task HandleConnectionAsync(
            Stream readerStream,
            Stream writerStream,
            CancellationToken serverToken)
        {
            using (var connection =
                   CancellationTokenSource.CreateLinkedTokenSource(
                       serverToken))
            using (var handshake =
                   CancellationTokenSource.CreateLinkedTokenSource(
                       connection.Token))
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(10));
                var hello = await IpcCodec.ReadFrameAsync(
                        readerStream,
                        handshake.Token)
                    .ConfigureAwait(false);
                var validation =
                    IpcHandshakeValidator.ValidateHello(
                        hello,
                        sessionId,
                        gameProcessId,
                        RuntimeProtocols,
                        token);
                if (!validation.Success)
                {
                    throw new IpcProtocolException(
                        validation.ErrorCode,
                        validation.Error);
                }

                var serverNonce = new byte[32];
                using (var random =
                       RandomNumberGenerator.Create())
                {
                    random.GetBytes(serverNonce);
                }

                await IpcCodec.WriteFrameAsync(
                        writerStream,
                        new IpcEnvelope(
                            validation.NegotiatedProtocol,
                            sessionId,
                            0,
                            IpcMessageTypes.HelloAck,
                            IpcPayloadCodec.Serialize(
                                new Dictionary<string, string>(
                                    StringComparer.Ordinal)
                                {
                                    ["companionInstanceId"] =
                                        validation.CompanionInstanceId,
                                    ["protocol"] =
                                        validation.NegotiatedProtocol
                                            .ToString(
                                                System.Globalization
                                                    .CultureInfo
                                                    .InvariantCulture),
                                    ["runtimeSessionId"] = sessionId,
                                    ["serverNonce"] =
                                        Convert.ToBase64String(
                                            serverNonce)
                                })),
                        handshake.Token)
                    .ConfigureAwait(false);

                var generation =
                    Interlocked.Increment(
                        ref connectionGeneration);
                Interlocked.Exchange(ref connected, 1);
                Volatile.Write(
                    ref authenticatedCompanionInstanceId,
                    validation.CompanionInstanceId);
                firstAuthentication.TrySetResult(true);
                logInfo(
                    "T12 Companion authenticated generation="
                    + generation);
                try
                {
                    Authenticated?.Invoke();
                }
                catch (Exception exception)
                {
                    logWarning(
                        "T12 authentication observer fault: "
                        + exception.Message);
                }

                var writer = Task.Run(
                    () => WriteLoopAsync(
                        writerStream,
                        validation.NegotiatedProtocol,
                        connection.Token));
                var sequence = new IpcSequenceValidator(0);
                try
                {
                    while (!connection.Token.IsCancellationRequested)
                    {
                        var envelope =
                            await IpcCodec.ReadFrameAsync(
                                    readerStream,
                                    connection.Token)
                                .ConfigureAwait(false);
                        if (envelope.ProtocolVersion
                            != validation.NegotiatedProtocol
                            || !string.Equals(
                                envelope.SessionId,
                                sessionId,
                                StringComparison.Ordinal))
                        {
                            throw new IpcProtocolException(
                                "InvalidSequenceOrBinding",
                                "Runtime command binding is invalid.");
                        }

                        if (!sequence.TryAccept(
                                envelope.Sequence,
                                out var sequenceError))
                        {
                            throw new IpcProtocolException(
                                "InvalidSequenceOrBinding",
                                sequenceError);
                        }

                        if (!IpcMessageTypes.IsRuntimeCommand(
                                envelope.MessageType))
                        {
                            throw new IpcProtocolException(
                                "UnknownCommand",
                                "Runtime command is not whitelisted.");
                        }

                        var payload =
                            IpcPayloadCodec.TryDeserialize(
                                envelope.PayloadUtf8);
                        if (!payload.Success
                            || payload.Fields == null)
                        {
                            throw new IpcProtocolException(
                                "InvalidPayload",
                                payload.ErrorCode
                                + ": "
                                + payload.Error);
                        }

                        FullRun.CanonicalSourceHeaderTiming.Begin(payload.Fields);
                        var accepted = commands.TryEnqueue(
                                new ValidatedRuntimeCommand(
                                    envelope.Sequence,
                                    envelope.MessageType,
                                    payload.Fields));
                        FullRun.CanonicalSourceHeaderTiming.Mark(FullRun.CanonicalSourceHeaderTiming.Request(payload.Fields),
                            accepted ? "ipc-command-enqueued" : "ipc-command-queue-rejected");
                        if (!accepted)
                        {
                            TryPublish(
                                IpcMessageTypes.Backpressure,
                                new Dictionary<string, string>(
                                    StringComparer.Ordinal)
                                {
                                    ["detail"] =
                                        "Runtime command queue is full.",
                                    ["rejectedCount"] =
                                        commands.RejectedCount.ToString(
                                            System.Globalization
                                                .CultureInfo
                                                .InvariantCulture)
                                });
                        }
                    }
                }
                finally
                {
                    connection.Cancel();
                    try
                    {
                        await writer.ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        if (!serverToken
                                .IsCancellationRequested
                            && Volatile.Read(ref disposed) == 0
                            && Volatile.Read(
                                   ref localDisconnectRequested)
                               == 0)
                        {
                            logWarning(
                                "T12 Runtime pipe writer stopped: "
                                + exception.Message);
                        }
                    }
                    Volatile.Write(
                        ref authenticatedCompanionInstanceId,
                        null);
                }
            }
        }

        private async Task WriteLoopAsync(
            Stream stream,
            int protocol,
            CancellationToken token)
        {
            long sequence = 0;
            while (!token.IsCancellationRequested)
            {
                await outboundSignal.WaitAsync(token)
                    .ConfigureAwait(false);
                while (outbound.TryDequeue(out var item))
                {
                    FullRun.CanonicalSourceHeaderTiming.Mark(item.HeaderTiming, "before-pipe-write");
                    try {
                    await IpcCodec.WriteFrameAsync(
                            stream,
                            new IpcEnvelope(
                                protocol,
                                sessionId,
                                checked(++sequence),
                                item.MessageType,
                                item.Payload),
                            token)
                        .ConfigureAwait(false);
                    FullRun.CanonicalSourceHeaderTiming.Mark(item.HeaderTiming, "after-pipe-write");
                    } catch {
                        FullRun.CanonicalSourceHeaderTiming.Mark(item.HeaderTiming, "pipe-write-failed"); throw;
                    }
                }
            }
        }

        private NativeNamedPipeServer CreateServerInstance()
        {
            return new NativeNamedPipeServer(
                PipeName,
                PipeDirection.InOut);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(NamedPipeRuntimeServer));
            }
        }

        private sealed class OutboundEvent
        {
            public OutboundEvent(
                string messageType,
                byte[] payload, string? headerTiming = null)
            {
                MessageType = messageType;
                Payload = payload;
                HeaderTiming = headerTiming;
            }

            public string MessageType { get; }
            public byte[] Payload { get; }
            public string? HeaderTiming { get; }
        }
    }
}
