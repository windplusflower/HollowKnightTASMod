using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Core.Automation;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Ipc;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Core.ReplaySave;

namespace HollowKnightTAS.Companion.Automation
{
    public sealed partial class AutomationBroker : IDisposable
    {
        private const int TimelineCapacity = 5000;
        private const int IdempotencyCapacity = 2048;
        private const int IdempotencyHistoryCapacity = 65536;
        private const long TimelinePayloadByteCapacity =
            8L * 1024 * 1024;
        private const long IdempotencyResultByteCapacity =
            8L * 1024 * 1024;
        private const int MovieChunkBytes = 384 * 1024;
        private const long StateFreshnessBudgetMilliseconds = 5000;
        private static readonly TimeSpan RuntimeTimeout =
            TimeSpan.FromSeconds(10);

        private readonly object sync = new object();
        private readonly SessionRegistry sessions;
        private readonly FullRunMovieCoordinator? fullRunMovies;
        private readonly ColdRestoreSupervisor? coldRestoreSupervisor;
        private readonly ControlLeaseManager leases =
            new ControlLeaseManager();
        private readonly Dictionary<string, PendingRuntimeRequest> pending =
            new Dictionary<string, PendingRuntimeRequest>(
                StringComparer.Ordinal);
        private readonly Dictionary<string, CachedResult> idempotency =
            new Dictionary<string, CachedResult>(
                StringComparer.Ordinal);
        private readonly Queue<string> idempotencyOrder =
            new Queue<string>();
        private readonly HashSet<string> expiredIdempotency =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<TimelineItem> timeline =
            new Queue<TimelineItem>();
        private TimelineCursor? lastEvictedTimelineItem;
        private long idempotencyRetainedBytes;
        private long timelineRetainedPayloadBytes;
        private readonly Dictionary<string, CachedEnvelope> latest =
            new Dictionary<string, CachedEnvelope>(
                StringComparer.Ordinal);
        private readonly byte[] brokerToken;
        private readonly CancellationTokenSource shutdown =
            new CancellationTokenSource();
        private readonly SemaphoreSlim humanCommandGate =
            new SemaphoreSlim(1, 1);
        private readonly string automationRoot;
        private readonly InputBatchTransactionWorkspace inputBatches =
            new InputBatchTransactionWorkspace();
        private RuntimeSessionClient? boundSession;
        private AutomationCapabilityCatalog? capabilityCatalog;
        private AutomationAuditSink? audit;
        private MoviePatchWorkspace? movieWorkspace;
        private AutomationPipeServer? pipeServer;
        private AutomationBootstrapDescriptor? bootstrap;
        private AutomationBootstrapDescriptor? fullRunBootstrap;
        private string currentMovieId = string.Empty;
        private byte[]? currentMovieBytes;
        private MovieLifecycleExportData? currentLifecycleSource;
        private MovieLifecycleExport? currentLifecycleExport;
        private bool disposed;

        private readonly string isolatedPipeNamespace;

        public AutomationBroker(
            SessionRegistry sessions,
            ColdRestoreSupervisor? coldRestoreSupervisor = null,
            string? automationDirectory = null,
            FullRunMovieCoordinator? fullRunMovies = null)
        {
            this.sessions = sessions
                            ?? throw new ArgumentNullException(
                                nameof(sessions));
            this.coldRestoreSupervisor = coldRestoreSupervisor;
            this.fullRunMovies = fullRunMovies;
            brokerToken = new byte[32];
            using (var random =
                   System.Security.Cryptography
                       .RandomNumberGenerator.Create())
            {
                random.GetBytes(brokerToken);
            }

            automationRoot = automationDirectory == null ? System.IO.Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "HollowKnightTAS",
                "automation") : System.IO.Path.GetFullPath(automationDirectory);
            isolatedPipeNamespace = automationDirectory == null ? string.Empty : "\n" + automationRoot;
            System.IO.Directory.CreateDirectory(automationRoot);
            sessions.EnvelopeReceived += OnEnvelopeReceived;
            sessions.SessionsChanged += OnSessionsChanged;
            if (coldRestoreSupervisor != null)
            {
                coldRestoreSupervisor.ActivityChanged += OnColdRestoreActivityChanged;
                coldRestoreSupervisor.OperationChanged += OnColdRestoreOperationChanged;
                coldRestoreSupervisor.SlotRecoveryChanged += OnSlotRecoveryChanged;
            }
            RefreshBinding();
        }

        public string ActiveColdRestoreOperationId => coldRestoreSupervisor?.ActiveRestoreOperationId ?? string.Empty;
        public SlotRecoveryNotice? LatestSlotRecovery => coldRestoreSupervisor?.LatestSlotRecovery;
        public event EventHandler? SlotRecoveryChanged;
        public event EventHandler<ColdRestoreOperationChangedEventArgs>? ColdRestoreChanged;
        private void OnColdRestoreOperationChanged(object? sender, ColdRestoreOperationChangedEventArgs args)
            => ColdRestoreChanged?.Invoke(this, args);
        private void OnSlotRecoveryChanged(object? sender, EventArgs args) => SlotRecoveryChanged?.Invoke(this, args);
        public string ActiveRecordingRestartOperationId => coldRestoreSupervisor?.LatestRecordingRestart is { } restart
            && restart.Phase != RecordingRestartPhase.Ready && restart.Phase != RecordingRestartPhase.Cancelled
            && restart.Phase != RecordingRestartPhase.Failed ? restart.OperationId : string.Empty;

        public AutomationBootstrapDescriptor? Bootstrap
        {
            get
            {
                lock (sync)
                {
                    return bootstrap;
                }
            }
        }

        public string BootstrapPath => System.IO.Path.Combine(
            automationRoot,
            AutomationProtocol.BootstrapFileName);

        public void BindFullRunEndpoint(string gateToken,
            string startupProfileSha256, AutomationMode mode)
        {
            lock (bindingSync)
            {
                if (disposed || fullRunMovies?.IsPending != true
                    || fullRunMovies.Gate?.Token != gateToken)
                    throw new InvalidOperationException("Full-run gate is unavailable for automation.");
                if (mode == AutomationMode.Disabled)
                {
                    EndFullRunEndpointCore();
                    return;
                }
                if (fullRunBootstrap != null)
                    throw new InvalidOperationException("Full-run automation is already bound.");
                var suffix = Sha256Utility.ComputeUtf8Hex(
                        Environment.UserDomainName + "\\" + Environment.UserName
                        + isolatedPipeNamespace).Substring(0, 16);
                var pipeName = "HollowKnightTAS.Automation." + suffix;
                var descriptor = new AutomationBootstrapDescriptor(pipeName,
                    gateToken, startupProfileSha256, mode, brokerToken);
                var evidenceRoot = Path.Combine(automationRoot, "artifacts");
                var newAudit = new AutomationAuditSink(evidenceRoot,
                    gateToken, brokerToken);
                var workspace = new MoviePatchWorkspace(evidenceRoot,
                    gateToken, Path.Combine(automationRoot, "movie-library"));
                var server = pipeServer ?? new AutomationPipeServer(pipeName,
                    this, new AutomationSessionAuthenticator(brokerToken));
                WriteBootstrap(descriptor);
                lock (sync)
                {
                    fullRunBootstrap = descriptor;
                    bootstrap = descriptor;
                    boundSession = null;
                    capabilityCatalog = new AutomationCapabilityCatalog(mode, false,
                        fullRunOnly: true);
                    audit = newAudit;
                    movieWorkspace = workspace;
                    pipeServer = server;
                    latest.Clear();
                    idempotency.Clear();
                    idempotencyOrder.Clear();
                    expiredIdempotency.Clear();
                    idempotencyRetainedBytes = 0;
                }
                leases.Revoke();
                server.Start();
            }
        }

        public void EndFullRunEndpoint()
        {
            lock (bindingSync)
            {
                EndFullRunEndpointCore();
                if (!disposed) RefreshBindingCore();
            }
        }

        private void EndFullRunEndpointCore()
        {
            if (fullRunBootstrap == null) return;
            lock (sync)
            {
                fullRunBootstrap = null;
                bootstrap = null;
                boundSession = null;
                capabilityCatalog = null;
                audit = null;
                movieWorkspace = null;
            }
            leases.Revoke();
            DeleteBootstrap();
        }

        public AutomationSessionAuthenticator? Authenticator =>
            pipeServer?.Authenticator;

        public string? AuditPath => audit?.Path;

        public int TimelineCount
        {
            get
            {
                lock (sync)
                {
                    return timeline.Count;
                }
            }
        }

        public long TimelineRetainedPayloadBytes
        {
            get
            {
                lock (sync)
                {
                    return timelineRetainedPayloadBytes;
                }
            }
        }

        public int IdempotencyResultCount
        {
            get
            {
                lock (sync)
                {
                    return idempotency.Count;
                }
            }
        }

        public long IdempotencyRetainedBytes
        {
            get
            {
                lock (sync)
                {
                    return idempotencyRetainedBytes;
                }
            }
        }

        public int ExpiredIdempotencyKeyCount
        {
            get
            {
                lock (sync)
                {
                    return expiredIdempotency.Count;
                }
            }
        }

        public async Task<AutomationResultEnvelope> ExecuteAsync(
            string authenticatedClientId,
            string connectionId,
            AutomationCommandEnvelope command,
            CancellationToken cancellationToken)
        {
            var fullRun = GetFullRunBinding();
            var session = fullRun == null ? GetBoundSession() : null;
            if (session == null && fullRun == null)
            {
                return UnboundResult(command.RequestId);
            }

            if (!string.Equals(
                    authenticatedClientId,
                    command.ClientId,
                    StringComparison.Ordinal)
                || command.SessionId != (fullRun?.SessionId ?? session!.SessionId))
            {
                return Result(
                    command,
                    false,
                    "BindingMismatch",
                    "Command client/session binding is invalid.");
            }

            var expectedManifestSha256 =
                fullRun?.ManifestSha256 ?? session!.EnvironmentManifestSha256;
            if (!string.Equals(
                    command.ManifestSha256,
                    expectedManifestSha256,
                    StringComparison.Ordinal))
            {
                Trace.TraceWarning(
                    $"AutomationBroker.ExecuteAsync: command ManifestSha256 '{command.ManifestSha256}' differs from active '{expectedManifestSha256}'; continuing.");
            }

            var requestHash = Sha256Utility.ComputeHex(
                command.ToPayload());
            var cacheKey = command.ClientId
                           + ":"
                           + command.IdempotencyKey;
            var retainIdempotency =
                ShouldRetainIdempotencyResult(command.CommandId);
            lock (sync)
            {
                if (idempotency.TryGetValue(
                        cacheKey,
                        out var cached))
                {
                    if (cached.RequestSha256 != requestHash)
                    {
                        return Result(
                            command,
                            false,
                            "IdempotencyConflict",
                            "Idempotency key was reused for a different request.");
                    }

                    Audit(
                        command,
                        cached.Result,
                        "idempotent replay");
                    return cached.Result;
                }

                if (expiredIdempotency.Contains(cacheKey))
                {
                    return Result(
                        command,
                        false,
                        "IdempotencyExpired",
                        "Idempotency key is outside the retained result window.");
                }

                if (retainIdempotency
                    && idempotency.Count
                    + expiredIdempotency.Count
                    >= IdempotencyHistoryCapacity)
                {
                    return Result(
                        command,
                        false,
                        "IdempotencyCapacity",
                        "Idempotency history is full for this Runtime session.");
                }
            }

            AutomationResultEnvelope result;
            try
            {
                result = fullRun == null
                    ? await RouteAsync(session!, authenticatedClientId,
                        connectionId, command, cancellationToken)
                    : await RouteFullRunAsync(fullRun, authenticatedClientId,
                        connectionId, command, cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ColdRestoreCommandRejectedException rejection)
            {
                result = Result(command, false, rejection.ErrorCode, Sanitize(rejection.Message),
                    rejection.ErrorCode == "ColdRestoreSlotApprovalRequired"
                        ? Fields("state", "AwaitingOverwriteApproval", "requiresOverwriteApproval", "true")
                        : null);
            }
            catch (Exception exception)
            {
                result = Result(
                    command,
                    false,
                    "BrokerFault",
                    Sanitize(exception.Message));
            }

            if (retainIdempotency)
            {
                var cachedResult = new CachedResult(
                    requestHash,
                    result,
                    result.ToPayload().LongLength);
                lock (sync)
                {
                    idempotency[cacheKey] = cachedResult;
                    idempotencyOrder.Enqueue(cacheKey);
                    idempotencyRetainedBytes +=
                        cachedResult.RetainedBytes;
                    while (idempotencyOrder.Count
                           > IdempotencyCapacity
                           || idempotencyRetainedBytes
                           > IdempotencyResultByteCapacity)
                    {
                        var expiredKey =
                            idempotencyOrder.Dequeue();
                        if (!idempotency.Remove(
                                expiredKey,
                                out var expiredResult))
                        {
                            continue;
                        }

                        idempotencyRetainedBytes -=
                            expiredResult.RetainedBytes;
                        expiredIdempotency.Add(expiredKey);
                    }
                }
            }

            Audit(
                command,
                result,
                result.Success ? "typed operation" : "none");
            return result;
        }

        public void ReleaseConnection(string connectionId)
        {
            inputBatches.ReleaseConnection(connectionId);
            leases.ReleaseConnection(connectionId);
        }

        public string RevokeControlLeaseByUser()
        {
            var active = leases.Active;
            leases.Revoke();
            if (active == null)
            {
                return "No external control lease was active.";
            }

            var requestId =
                "user-revoke-" + Guid.NewGuid().ToString("N");
            var sink = audit;
            sink?.Write(
                requestId,
                "companion-ui",
                requestId,
                requestId,
                active.SessionId,
                active.ManifestSha256,
                "revokeControlLease",
                string.Join(",", active.Scopes),
                active.LeaseId,
                CurrentMovieTick(),
                CurrentMovieTick(),
                CurrentMovieTick(),
                "RevokedByUser",
                "user revoked external control lease",
                Sha256Utility.ComputeUtf8Hex(
                    "RevokedByUser:" + active.SessionId));
            return "External control lease revoked by the user.";
        }

        public async Task<AutomationResultEnvelope> ExecuteHumanAsync(
            string commandId,
            string requiredScope,
            IReadOnlyDictionary<string, string>? arguments,
            string expectedRuntimeMode,
            long? expectedMovieTick,
            CancellationToken cancellationToken)
        {
            const string clientId = "companion-ui";
            const string connectionId = "companion-ui-inproc";
            await humanCommandGate.WaitAsync(cancellationToken);
            string leaseId = string.Empty;
            try
            {
                var fullRun = GetFullRunBinding();
                var session = fullRun == null ? GetBoundSession() : null;
                if (session == null && fullRun == null)
                {
                    return UnboundResult(
                        "ui-" + Guid.NewGuid().ToString("N"));
                }

                var requestId =
                    "ui-" + Guid.NewGuid().ToString("N");
                var sessionId = fullRun?.SessionId ?? session!.SessionId;
                var manifestSha256 = fullRun?.ManifestSha256
                    ?? session!.EnvironmentManifestSha256;
                var mode = fullRun?.Mode ?? session!.AutomationMode;
                var canonicalArguments = IpcPayloadCodec.Serialize(
                    arguments
                    ?? new Dictionary<string, string>(
                        StringComparer.Ordinal));
                var command = new AutomationCommandEnvelope(
                    requestId,
                    requestId,
                    clientId,
                    sessionId,
                    manifestSha256,
                    commandId,
                    requiredScope,
                    string.Empty,
                    expectedRuntimeMode ?? string.Empty,
                    expectedMovieTick,
                    canonicalArguments);
                var catalog = capabilityCatalog
                              ?? new AutomationCapabilityCatalog(
                                  mode,
                                  session?.DebugMutationEnabled == true);
                if (catalog.TryGet(commandId, out var capability)
                    && capability.RequiresLease)
                {
                    var acquired = leases.Acquire(
                        clientId,
                        connectionId,
                        new[] { requiredScope },
                        ControlLeaseManager.DefaultTtl,
                        sessionId,
                        manifestSha256,
                        mode);
                    if (!acquired.Success || acquired.Lease == null)
                    {
                        return Result(
                            command,
                            false,
                            acquired.Code,
                            acquired.Detail);
                    }

                    leaseId = acquired.Lease.LeaseId;
                    command = new AutomationCommandEnvelope(
                        requestId,
                        requestId,
                        clientId,
                        sessionId,
                        manifestSha256,
                        commandId,
                        requiredScope,
                        leaseId,
                        expectedRuntimeMode ?? string.Empty,
                        expectedMovieTick,
                        canonicalArguments);
                }

                return await ExecuteAsync(
                    clientId,
                    connectionId,
                    command,
                    cancellationToken);
            }
            finally
            {
                if (leaseId.Length != 0)
                {
                    leases.Release(
                        clientId,
                        connectionId,
                        leaseId);
                }

                humanCommandGate.Release();
            }
        }

        private readonly object bindingSync = new object();

        public void Dispose()
        {
            lock (bindingSync) { DisposeCore(); }
        }

        private void DisposeCore()
        {
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                currentLifecycleExport = null;
                currentLifecycleSource = null;
            }

            sessions.EnvelopeReceived -= OnEnvelopeReceived;
            sessions.SessionsChanged -= OnSessionsChanged;
            if (coldRestoreSupervisor != null)
            {
                coldRestoreSupervisor.ActivityChanged -= OnColdRestoreActivityChanged;
                coldRestoreSupervisor.OperationChanged -= OnColdRestoreOperationChanged;
                coldRestoreSupervisor.SlotRecoveryChanged -= OnSlotRecoveryChanged;
            }
            leases.Revoke();
            inputBatches.Clear();
            shutdown.Cancel();
            pipeServer?.Dispose();
            pipeServer = null;
            DeleteBootstrap();
            lock (sync)
            {
                foreach (var waiter in pending.Values)
                {
                    waiter.Completion.TrySetException(
                        new ObjectDisposedException(
                            nameof(AutomationBroker)));
                }

                pending.Clear();
            }
            shutdown.Dispose();
            humanCommandGate.Dispose();
        }

        private async Task<AutomationResultEnvelope> RouteAsync(
            RuntimeSessionClient session,
            string clientId,
            string connectionId,
            AutomationCommandEnvelope command,
            CancellationToken cancellationToken)
        {
            var mode = session.AutomationMode;
            if (mode == AutomationMode.Disabled)
            {
                return Result(
                    command,
                    false,
                    "AutomationDisabled",
                    "Automation endpoint is disabled.");
            }

            var argumentError = ValidateArgumentShape(command);
            if (argumentError != null)
            {
                return Result(
                    command,
                    false,
                    "InvalidArguments",
                    argumentError);
            }

            var argumentValueError =
                ValidateArgumentValues(command);
            if (argumentValueError != null)
            {
                return Result(
                    command,
                    false,
                    "InvalidArguments",
                    argumentValueError);
            }

            if (command.CommandId == AutomationCommandIds.StartVideoExport
                && (command.Arguments.ContainsKey("endMovieFrame") || command.Arguments.ContainsKey("infoOverlay")))
                return Result(command, false, "Unsupported", "A video range or information overlay requires a full-run v2 session.");

            if (command.CommandId == AutomationCommandIds.GetWorldSnapshot
                || command.CommandId == AutomationCommandIds.GetObjectDetails)
            {
                return Result(command, false, "Unsupported",
                    "This observation is available only in a full-run v2 session.");
            }

            if (!session.IsConnected && coldRestoreSupervisor?.IsActive != true
                && command.CommandId != AutomationCommandIds.GetStatus
                && command.CommandId != AutomationCommandIds.GetCapabilities
                && command.CommandId != AutomationCommandIds.ReleaseControl)
                return Result(command, false, "RuntimeDisconnected", "The last operation outcome remains available through status; no Runtime is connected.");

            if (coldRestoreSupervisor?.IsActive == true
                && command.CommandId != AutomationCommandIds.AcquireControl
                && command.CommandId != AutomationCommandIds.RenewControl
                && command.CommandId != AutomationCommandIds.ReleaseControl
                && command.CommandId != AutomationCommandIds.GetStatus
                && command.CommandId != AutomationCommandIds.GetCapabilities
                && command.CommandId != AutomationCommandIds.CancelReplaySaveRestore
                && command.CommandId != AutomationCommandIds.CancelRecordingRestart)
                return Result(command, false, "ColdRestoreInProgress",
                    "Only status, leases and operation-bound cancellation are available during cold restore.");

            if (command.CommandId == AutomationCommandIds.AcquireControl)
            {
                return Acquire(
                    session,
                    clientId,
                    connectionId,
                    command);
            }

            if (command.CommandId == AutomationCommandIds.RenewControl)
            {
                return Renew(clientId, connectionId, command);
            }

            if (command.CommandId == AutomationCommandIds.ReleaseControl)
            {
                return Release(clientId, connectionId, command);
            }

            var catalog = capabilityCatalog
                          ?? new AutomationCapabilityCatalog(
                              mode,
                              session.DebugMutationEnabled);
            if (!catalog.TryGet(
                    command.CommandId,
                    out var capability)
                || capability.Scope != command.RequiredScope)
            {
                return Result(
                    command,
                    false,
                    "CapabilityMismatch",
                    "Command scope is not registered for this operation.");
            }

            if (capability.Availability == "disabled")
            {
                return Result(
                    command,
                    false,
                    "CapabilityDisabled",
                    "Capability is disabled by the user policy.");
            }

            if (capability.Availability == "unsupported")
            {
                return Result(
                    command,
                    false,
                    "Unsupported",
                    "This observation is available only in a full-run v2 session.");
            }

            if (capability.RequiresLease
                && !leases.Validate(
                    clientId,
                    connectionId,
                    command.LeaseId,
                    command.RequiredScope,
                    session.SessionId,
                    session.EnvironmentManifestSha256))
            {
                return Result(
                    command,
                    false,
                    "LeaseRequired",
                    "A matching unexpired exclusive control lease is required.");
            }

            if (command.CommandId == AutomationCommandIds.FullRunStatus)
                return await ForwardAsync(session, command, IpcMessageTypes.FullRunStatus,
                    Fields("requestId", command.RequestId), IpcMessageTypes.FullRunState,
                    cancellationToken);
            if (command.CommandId == AutomationCommandIds.FullRunMovie)
                return await ForwardAsync(session, command, IpcMessageTypes.FullRunMovie,
                    Fields("requestId", command.RequestId), IpcMessageTypes.FullRunMovieDocument,
                    cancellationToken);
            if (command.CommandId == AutomationCommandIds.FullRunStop)
            {
                if (command.ExpectedRuntimeMode != "Paused")
                    return Result(command, false, "PreconditionFailed",
                        "Full-run stop requires expectedRuntimeMode=Paused.");
                return await ForwardAsync(session, command, IpcMessageTypes.FullRunStop,
                    Fields("requestId", command.RequestId, "expectedNativeFrame",
                        command.Arguments["expectedNativeFrame"]),
                    IpcMessageTypes.FullRunMovieDocument, cancellationToken);
            }

            if (command.CommandId == AutomationCommandIds.CancelRecordingRestart)
            {
                if (coldRestoreSupervisor == null) return Result(command, false, "Unavailable", "Session supervisor is unavailable.");
                var restart = await coldRestoreSupervisor.CancelRecordingRestartAsync(command.Arguments["operationId"]);
                return Result(command, restart.Phase == RecordingRestartPhase.Cancelled,
                    "RecordingRestart" + restart.Phase, restart.Detail,
                    Fields("operationId", restart.OperationId, "phase", restart.Phase.ToString()));
            }

            if (command.CommandId == AutomationCommandIds.CancelReplaySaveRestore
                && coldRestoreSupervisor != null
                && (coldRestoreSupervisor.IsActive || command.Arguments.ContainsKey("operationId")))
            {
                if (!command.Arguments.TryGetValue("operationId", out var operationId)
                    || string.IsNullOrWhiteSpace(operationId))
                    return Result(command, false, "InvalidArguments", "Cold restore cancellation requires operationId.");
                var cancelled = await coldRestoreSupervisor.CancelAsync(operationId);
                return Result(command, cancelled.Latest.State == ColdRestoreOperationState.Cancelled,
                    "ColdRestore" + cancelled.Latest.State, "Cold restore ended as " + cancelled.Latest.State + ".",
                    Fields("operationId", operationId, "state", cancelled.Latest.State.ToString()));
            }

            if (capability.RequiresLease
                && command.CommandId != AutomationCommandIds.FinishVideoExport
                && command.CommandId != AutomationCommandIds.CancelVideoExport)
            {
                if (string.IsNullOrEmpty(
                        command.ExpectedRuntimeMode))
                {
                    return Result(
                        command,
                        false,
                        "MissingPrecondition",
                        "A write command requires expectedRuntimeMode.");
                }

                if ((command.CommandId
                     == AutomationCommandIds.SetHeroPose
                     || command.CommandId
                     == AutomationCommandIds.SetPlayerResources
                     || command.CommandId
                      == AutomationCommandIds.StepWithInput
                     || command.CommandId
                      == AutomationCommandIds.QueueInputBatch
                     || command.CommandId
                      == AutomationCommandIds.BeginInputBatch
                     || command.CommandId
                      == AutomationCommandIds.AppendInputBatch
                     || command.CommandId
                      == AutomationCommandIds.CommitInputBatch
                     || command.CommandId
                      == AutomationCommandIds.CancelInputBatch
                     || command.CommandId
                      == AutomationCommandIds.SeekMovieTick
                     || command.CommandId
                      == AutomationCommandIds.ApplyBranchAndSeek)
                    && !string.Equals(
                        command.ExpectedRuntimeMode,
                        "Paused",
                        StringComparison.Ordinal))
                {
                    return Result(
                        command,
                        false,
                        "PreconditionFailed",
                        "Frame input and typed mutation require expectedRuntimeMode=Paused.");
                }

                var precondition = await CheckRuntimePreconditionsAsync(
                    session,
                    command,
                    cancellationToken);
                if (precondition != null)
                {
                    return precondition;
                }
            }

            switch (command.CommandId)
            {
                case AutomationCommandIds.GetStatus:
                    return GetStatus(command, session);
                case AutomationCommandIds.GetStartupProfile:
                    return await ForwardAsync(
                        session, command,
                        IpcMessageTypes.RequestStartupProfileAttestation,
                        Fields("requestId", command.RequestId),
                        IpcMessageTypes.StartupProfileAttestation,
                        cancellationToken);
                case AutomationCommandIds.GetCapabilities:
                    return Result(
                        command,
                        true,
                        "Ok",
                        "Capability catalog returned.",
                        Fields(
                            "catalog",
                            catalog.Serialize(),
                            "catalogJson",
                            catalog.SerializeJson(),
                            "mode",
                            mode.ToString(),
                            "debugMutationEnabled",
                            session.DebugMutationEnabled
                                ? "true"
                                : "false"));
                case AutomationCommandIds.GetState:
                    return await GetStateAsync(
                        session,
                        command,
                        cancellationToken);
                case AutomationCommandIds.GetCombatState:
                    return await GetCombatStateAsync(session, command, cancellationToken);
                case AutomationCommandIds.GetWorldSnapshot:
                case AutomationCommandIds.GetObjectDetails:
                    return Result(command, false, "Unsupported",
                        "This observation is available only in a full-run v2 session.");
                case AutomationCommandIds.GetTimeline:
                    return GetTimeline(command);
                case AutomationCommandIds.GetDesync:
                    return GetLatest(
                        command,
                        IpcMessageTypes.Desync,
                        "NoDesync",
                        "No desync has been observed.");
                case AutomationCommandIds.GetReplaySaves:
                    return await ForwardAsync(
                        session,
                        command,
                        IpcMessageTypes.ListReplaySaves,
                        Fields("requestId", command.RequestId),
                        IpcMessageTypes.ReplaySaveCatalog,
                        cancellationToken);
                case AutomationCommandIds.GetMovie:
                    return await GetMovieAsync(
                        session,
                        command,
                        cancellationToken);
                case AutomationCommandIds.GetRestoreStrategy:
                    return await ForwardAsync(
                        session,
                        command,
                        IpcMessageTypes.RequestCapabilityCatalog,
                        Fields("requestId", command.RequestId),
                        IpcMessageTypes.RestoreAccelerationStatus,
                        cancellationToken);
                case AutomationCommandIds.ProposeMoviePatch:
                    return ProposeMovie(command);
                case AutomationCommandIds.ValidateMoviePatch:
                    return ValidateMovie(command);
                case AutomationCommandIds.ApplyMovieBranch:
                    return await ApplyMovieBranchAsync(
                        session,
                        command,
                        cancellationToken);
                case AutomationCommandIds.ApplyBranchAndSeek:
                    return await ApplyBranchAndSeekAsync(
                        session,
                        clientId,
                        command,
                        cancellationToken);
                case AutomationCommandIds.ReplaceInputRange:
                    return await EditMovieTimelineAsync(
                        session,
                        command,
                        TimelineEditKind.Replace,
                        cancellationToken);
                case AutomationCommandIds.InsertInputRange:
                    return await EditMovieTimelineAsync(
                        session,
                        command,
                        TimelineEditKind.Insert,
                        cancellationToken);
                case AutomationCommandIds.DeleteInputRange:
                    return await EditMovieTimelineAsync(
                        session,
                        command,
                        TimelineEditKind.Delete,
                        cancellationToken);
                case AutomationCommandIds.Pause:
                    return await SimpleRuntimeAsync(
                        session,
                        command,
                        IpcMessageTypes.Pause,
                        cancellationToken);
                case AutomationCommandIds.QuitGame:
                    return await ForwardAsync(session, command, IpcMessageTypes.QuitGame,
                        Fields("requestId", command.RequestId), IpcMessageTypes.CommandAccepted, cancellationToken);
                case AutomationCommandIds.LoadGameSlot:
                    return await ForwardAsync(session, command, IpcMessageTypes.LoadGameSlot,
                        Fields("requestId", command.RequestId, "slot", command.Arguments["slot"]),
                        IpcMessageTypes.CommandAccepted, cancellationToken);
                case AutomationCommandIds.ReloadGameSlot:
                    return await ForwardAsync(session, command, IpcMessageTypes.ReloadGameSlot,
                        Fields("requestId", command.RequestId, "slot", command.Arguments["slot"]),
                        IpcMessageTypes.CommandAccepted, cancellationToken);
                case AutomationCommandIds.RestartRecordingSession:
                    if (coldRestoreSupervisor == null) return Result(command, false, "Unavailable", "Session supervisor is unavailable.");
                    var restartId = coldRestoreSupervisor.BeginRecordingRestart(session,
                        int.Parse(command.Arguments["slot"], CultureInfo.InvariantCulture));
                    return Result(command, true, "RecordingRestartAccepted", "Query status for the recording restart outcome.",
                        Fields("operationId", restartId));
                case AutomationCommandIds.Resume:
                    return await SimpleRuntimeAsync(
                        session,
                        command,
                        IpcMessageTypes.Resume,
                        cancellationToken);
                case AutomationCommandIds.StartReplay:
                case AutomationCommandIds.StartVideoExport:
                case AutomationCommandIds.FinishVideoExport:
                case AutomationCommandIds.CancelVideoExport:
                    if (command.CommandId != AutomationCommandIds.StartReplay)
                    {
                        var videoFields = new Dictionary<string, string>(command.Arguments, StringComparer.Ordinal)
                        { ["requestId"] = command.RequestId };
                        if (command.CommandId == AutomationCommandIds.StartVideoExport)
                            BundledFfmpeg.ApplyDefault(videoFields);
                        return await ForwardAsync(session, command, command.CommandId, videoFields,
                            IpcMessageTypes.CommandAccepted, cancellationToken);
                    }
                    return await SimpleRuntimeAsync(
                        session,
                        command,
                        IpcMessageTypes.StartReplay,
                        cancellationToken);
                case AutomationCommandIds.StopReplay:
                    return await SimpleRuntimeAsync(
                        session,
                        command,
                        IpcMessageTypes.StopReplay,
                        cancellationToken);
                case AutomationCommandIds.StartRecording:
                    return await SimpleRuntimeAsync(
                        session,
                        command,
                        IpcMessageTypes.StartRecording,
                        cancellationToken);
                case AutomationCommandIds.StopRecording:
                    return await StopRecordingAsync(
                        session,
                        command,
                        cancellationToken);
                case AutomationCommandIds.Step:
                    return await ForwardAsync(
                        session,
                        command,
                        IpcMessageTypes.Step,
                        Fields(
                            "boundary",
                            "movieTick",
                            "count",
                            RequiredArgument(command, "count"),
                            "requestId",
                            command.RequestId),
                        IpcMessageTypes.CommandAccepted,
                        cancellationToken);
                case AutomationCommandIds.StepWithInput:
                case AutomationCommandIds.QueueInputBatch:
                    return await RunInputBatchAsync(
                        session,
                        command,
                        cancellationToken);
                case AutomationCommandIds.BeginInputBatch:
                    return BeginInputBatch(
                        session,
                        clientId,
                        connectionId,
                        command);
                case AutomationCommandIds.AppendInputBatch:
                    return AppendInputBatch(
                        clientId,
                        connectionId,
                        command);
                case AutomationCommandIds.CommitInputBatch:
                    return await CommitInputBatchAsync(
                        session,
                        clientId,
                        connectionId,
                        command,
                        cancellationToken);
                case AutomationCommandIds.CancelInputBatch:
                    return CancelInputBatch(
                        clientId,
                        connectionId,
                        command);
                case AutomationCommandIds.RunUntil:
                    return await ForwardAsync(
                        session,
                        command,
                        IpcMessageTypes.RunUntil,
                        Fields(
                            "requestId",
                            command.RequestId,
                            "targetMovieTick",
                            RequiredArgument(
                                command,
                                "targetMovieTick")),
                        IpcMessageTypes.CommandAccepted,
                        cancellationToken);
                case AutomationCommandIds.CreateReplaySave:
                    return await ForwardAsync(
                        session,
                        command,
                        IpcMessageTypes.CreateReplaySave,
                        Fields(
                            "label",
                            RequiredArgument(command, "label"),
                            "requestId",
                            command.RequestId),
                        IpcMessageTypes.CommandAccepted,
                        cancellationToken);
                case AutomationCommandIds.SetAutoSavePolicy:
                    return await ForwardAsync(session, command, IpcMessageTypes.SetAutoSavePolicy,
                        Fields("requestId", command.RequestId,
                            "enabled", RequiredArgument(command, "enabled"),
                            "intervalMovieTicks", RequiredArgument(command, "intervalMovieTicks"),
                            "retentionCount", RequiredArgument(command, "retentionCount")),
                        IpcMessageTypes.CommandAccepted, cancellationToken);
                case AutomationCommandIds.RestoreReplaySave:
                    return await BeginColdRestoreAsync(
                        session,
                        clientId,
                        command,
                        cancellationToken);
                case AutomationCommandIds.SeekMovieTick:
                    return await SeekMovieTickAsync(
                        session,
                        clientId,
                        command,
                        ColdRestoreOperationKind.SeekMovieTick,
                        cancellationToken);
                case AutomationCommandIds.ApproveReplaySaveOverwrite:
                    return await ForwardAsync(
                        session,
                        command,
                        IpcMessageTypes.ApproveReplaySaveOverwrite,
                        Fields(
                            "approved",
                            RequiredArgument(command, "approved"),
                            "requestId",
                            command.RequestId),
                        IpcMessageTypes.CommandAccepted,
                        cancellationToken);
                case AutomationCommandIds.CancelReplaySaveRestore:
                    return await ForwardAsync(
                        session,
                        command,
                        IpcMessageTypes.CancelReplaySaveRestore,
                        Fields("requestId", command.RequestId),
                        IpcMessageTypes.CommandAccepted,
                        cancellationToken);
                case AutomationCommandIds.ResumeReplaySaveRestore:
                    return await ForwardAsync(
                        session,
                        command,
                        IpcMessageTypes.ResumeReplaySaveRestore,
                        Fields("requestId", command.RequestId),
                        IpcMessageTypes.CommandAccepted,
                        cancellationToken);
                case AutomationCommandIds.SetHeroPose:
                    return await ForwardMutationAsync(
                        session,
                        command,
                        IpcMessageTypes.SetHeroPose,
                        new[]
                        {
                            "expectedMovieTick",
                            "expectedSnapshotSha256",
                            "positionX",
                            "positionY",
                            "velocityX",
                            "velocityY"
                        },
                        cancellationToken);
                case AutomationCommandIds.SetPlayerResources:
                    return await ForwardMutationAsync(
                        session,
                        command,
                        IpcMessageTypes.SetPlayerResources,
                        new[]
                        {
                            "expectedMovieTick",
                            "expectedSnapshotSha256",
                            "health",
                            "soul"
                        },
                        cancellationToken);
                default:
                    return Result(
                        command,
                        false,
                        "NotImplemented",
                        "Registered operation is not implemented.");
            }
        }

        private AutomationResultEnvelope Acquire(
            RuntimeSessionClient session,
            string clientId,
            string connectionId,
            AutomationCommandEnvelope command)
            => AcquireBound(session.SessionId,
                session.EnvironmentManifestSha256, session.AutomationMode,
                clientId, connectionId, command);

        private AutomationResultEnvelope AcquireBound(string sessionId,
            string manifestSha256, AutomationMode mode, string clientId,
            string connectionId, AutomationCommandEnvelope command)
        {
            var scopeText = RequiredArgument(command, "scopes");
            if (scopeText.Length > 1024)
            {
                return Result(
                    command,
                    false,
                    "InvalidScope",
                    "Requested scope list is too long.");
            }

            var scopes = scopeText
                .Split(
                    new[] { ',' },
                    StringSplitOptions.RemoveEmptyEntries);
            if (scopes.Length == 0
                || scopes.Length > AutomationScope.All.Count
                || scopes.Distinct(
                        StringComparer.Ordinal).Count()
                   != scopes.Length
                || scopes.Any(
                    scope => !AutomationScope.IsKnown(scope)
                             || AutomationScope.IsReadOnly(
                                 scope)))
            {
                return Result(
                    command,
                    false,
                    "InvalidScope",
                    "Every requested scope must be a unique "
                    + "registered write scope.");
            }

            if (!scopes.Contains(
                    command.RequiredScope,
                    StringComparer.Ordinal))
            {
                return Result(
                    command,
                    false,
                    "InvalidScope",
                    "requiredScope must be included in requested scopes.");
            }

            var ttl = ParseTtl(command);
            var lease = leases.Acquire(
                clientId,
                connectionId,
                scopes,
                ttl,
                sessionId,
                manifestSha256,
                mode);
            return LeaseResult(command, lease);
        }

        private AutomationResultEnvelope Renew(
            string clientId,
            string connectionId,
            AutomationCommandEnvelope command)
        {
            return LeaseResult(
                command,
                leases.Renew(
                    clientId,
                    connectionId,
                    command.LeaseId,
                    ParseTtl(command)));
        }

        private AutomationResultEnvelope Release(
            string clientId,
            string connectionId,
            AutomationCommandEnvelope command)
        {
            return LeaseResult(
                command,
                leases.Release(
                    clientId,
                    connectionId,
                    command.LeaseId));
        }

        private AutomationResultEnvelope LeaseResult(
            AutomationCommandEnvelope command,
            LeaseOperationResult operation)
        {
            var fields = new Dictionary<string, string>(
                StringComparer.Ordinal);
            if (operation.Lease != null)
            {
                fields["leaseId"] = operation.Lease.LeaseId;
                fields["scopes"] = string.Join(
                    ",",
                    operation.Lease.Scopes);
                fields["issuedAtUtc"] =
                    operation.Lease.IssuedAtUtc.ToString(
                        "O",
                        CultureInfo.InvariantCulture);
                fields["expiresAtUtc"] =
                    operation.Lease.ExpiresAtUtc.ToString(
                        "O",
                        CultureInfo.InvariantCulture);
            }

            return Result(
                command,
                operation.Success,
                operation.Code,
                operation.Detail,
                fields);
        }

        private async Task<AutomationResultEnvelope?>
            CheckRuntimePreconditionsAsync(
                RuntimeSessionClient session,
                AutomationCommandEnvelope command,
                CancellationToken cancellationToken)
        {
            var probeId = "precondition-"
                          + Guid.NewGuid().ToString("N");
            // Operational save preferences are valid at the title menu too.
            // Keep the same authenticated request and mode/tick CAS checks;
            // only omit the unrelated Hero semantic capture.
            var probeFields = command.CommandId == AutomationCommandIds.SetAutoSavePolicy
                || command.CommandId == AutomationCommandIds.RestoreReplaySave
                || command.CommandId == AutomationCommandIds.QuitGame
                || command.CommandId == AutomationCommandIds.LoadGameSlot
                || command.CommandId == AutomationCommandIds.ReloadGameSlot
                || command.CommandId == AutomationCommandIds.RestartRecordingSession
                || command.CommandId == AutomationCommandIds.ApproveReplaySaveOverwrite
                || command.CommandId == AutomationCommandIds.CancelReplaySaveRestore
                ? Fields("requestId", probeId, "statusOnly", "true")
                : Fields("requestId", probeId);
            var envelope = await SendRuntimeAsync(
                session,
                IpcMessageTypes.RequestSnapshot,
                probeFields,
                probeId,
                IpcMessageTypes.RuntimeStatus,
                cancellationToken);
            var decoded = Decode(envelope);
            if (decoded.TryGetValue(
                    "errorCode",
                    out var runtimeError))
            {
                return Result(
                    command,
                    false,
                    runtimeError,
                    decoded.TryGetValue("detail", out var detail)
                        ? detail
                        : "Runtime precondition probe failed.");
            }

            if (!string.IsNullOrEmpty(command.ExpectedRuntimeMode)
                && (!decoded.TryGetValue(
                        "controlMode",
                        out var mode)
                    || mode != command.ExpectedRuntimeMode))
            {
                return Result(
                    command,
                    false,
                    "PreconditionFailed",
                    "Expected Runtime control mode does not match.");
            }

            if (command.ExpectedMovieTick.HasValue
                && (!decoded.TryGetValue(
                        "movieTick",
                        out var tickText)
                    || !long.TryParse(
                        tickText,
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture,
                        out var tick)
                    || tick != command.ExpectedMovieTick.Value))
            {
                return Result(
                    command,
                    false,
                    "PreconditionFailed",
                    "Expected movie tick does not match.");
            }

            return null;
        }

        private AutomationResultEnvelope GetStatus(
            AutomationCommandEnvelope command,
            RuntimeSessionClient session)
        {
            var fields = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["automationMode"] =
                    session.AutomationMode.ToString(),
                ["connected"] =
                    session.IsConnected ? "true" : "false",
                ["debugMutationEnabled"] =
                    session.DebugMutationEnabled ? "true" : "false",
                ["manifestSha256"] =
                    session.EnvironmentManifestSha256,
                ["sessionId"] = session.SessionId
            };
            lock (sync)
            {
                if (latest.TryGetValue(
                        IpcMessageTypes.RuntimeStatus,
                        out var status))
                {
                    // Keep the complete Runtime payload in one field. Flattening every
                    // diagnostic plus supervisor fields exceeds the IPC field budget.
                    fields["runtimeStatusJson"] = Encoding.UTF8.GetString(IpcPayloadCodec.Serialize(status.Fields));
                    foreach (var pair in status.Fields)
                    {
                        if (pair.Key == "controlMode" || pair.Key == "movieTick"
                            || pair.Key == "playbackMode" || pair.Key == "sceneEpoch"
                            || pair.Key == "recordingOriginStatus" || pair.Key == "lastPlaybackStopReason"
                            || pair.Key == "lastPlaybackFault" || pair.Key == "controlFault"
                            || pair.Key.StartsWith("videoExport.", StringComparison.Ordinal))
                            fields["runtime." + pair.Key] = pair.Value;
                    }

                    fields["ageMilliseconds"] =
                        Math.Max(
                                0,
                                (long)(DateTimeOffset.UtcNow
                                       - status.ReceivedAtUtc)
                                .TotalMilliseconds)
                            .ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    fields["ageMilliseconds"] = "-1";
                }
            }

            var restore = coldRestoreSupervisor?.LatestSnapshot;
            if (restore != null)
            {
                fields["coldRestore.operationId"] = restore.Intent.OperationId;
                fields["coldRestore.state"] = restore.Latest.State.ToString();
                fields["coldRestore.sequence"] = restore.Latest.Sequence.ToString(CultureInfo.InvariantCulture);
                fields["coldRestore.detail"] = restore.Latest.DetailCode;
                fields["coldRestore.targetMovieTick"] = restore.Intent.TargetMovieTick.ToString(CultureInfo.InvariantCulture);
            }
            var recovery = LatestSlotRecovery;
            if (recovery != null)
            {
                fields["slotRecovery.operationId"] = recovery.OperationId;
                fields["slotRecovery.status"] = recovery.Status;
                fields["slotRecovery.detail"] = recovery.Detail;
            }
            var restart = coldRestoreSupervisor?.LatestRecordingRestart;
            if (restart != null)
            {
                fields["recordingRestart.operationId"] = restart.OperationId;
                fields["recordingRestart.phase"] = restart.Phase.ToString();
                fields["recordingRestart.slot"] = restart.Slot.ToString(CultureInfo.InvariantCulture);
                fields["recordingRestart.detail"] = restart.Detail;
            }
            return Result(command, true, "Ok", "Automation status returned.", fields);
        }

        private AutomationResultEnvelope GetTimeline(
            AutomationCommandEnvelope command)
        {
            var arguments = command.Arguments;
            var from = 0L;
            var count = 100;
            long? afterSequence = null;
            if (arguments.TryGetValue(
                    "fromMovieTick",
                    out var fromText))
            {
                if (string.Equals(
                        fromText,
                        "-1",
                        StringComparison.Ordinal))
                {
                    from = -1;
                }
                else if (!long.TryParse(
                             fromText,
                             NumberStyles.None,
                             CultureInfo.InvariantCulture,
                             out from))
                {
                    return Result(
                        command,
                        false,
                        "InvalidArgument",
                        "fromMovieTick must be an Int64 greater than or equal to -1.");
                }
            }

            if (arguments.TryGetValue("count", out var countText)
                && (!int.TryParse(
                        countText,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out count)
                    || count < 1
                    || count > 200))
            {
                return Result(
                    command,
                    false,
                    "InvalidArgument",
                    "count must be in [1,200].");
            }

            if (arguments.TryGetValue(
                    "afterSequence",
                    out var sequenceText))
            {
                if (!long.TryParse(
                        sequenceText,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var parsedSequence)
                    || parsedSequence < 0)
                {
                    return Result(
                        command,
                        false,
                        "InvalidArgument",
                        "afterSequence must be a non-negative Int64.");
                }

                afterSequence = parsedSequence;
            }

            TimelineItem[] items;
            TimelineItem[] window;
            TimelineCursor? lastEvicted;
            lock (sync)
            {
                window = timeline.ToArray();
                lastEvicted = lastEvictedTimelineItem;
                items = window
                    .Where(
                        item => item.MovieTick >= from
                                && (!afterSequence.HasValue
                                    || item.Sequence
                                    > afterSequence.Value))
                    .Take(count)
                    .ToArray();
            }

            var entries = string.Join(
                "\n",
                items.Select(
                    item =>
                        item.Sequence.ToString(
                            CultureInfo.InvariantCulture)
                        + "|"
                        + item.MessageType
                        + "|"
                        + item.MovieTick.ToString(
                            CultureInfo.InvariantCulture)
                        + "|"
                        + Convert.ToBase64String(
                            item.PayloadUtf8)));
            var gap = false;
            if (lastEvicted != null)
            {
                gap = afterSequence.HasValue
                    ? afterSequence.Value
                      <= lastEvicted.Sequence
                      && from <= lastEvicted.MovieTick
                    : from <= lastEvicted.MovieTick;
            }

            var nextAfterSequence = items.Length > 0
                ? items[items.Length - 1].Sequence
                : afterSequence ?? 0;

            return Result(
                command,
                true,
                "Ok",
                "Bounded timeline returned.",
                Fields(
                    "count",
                    items.Length.ToString(
                        CultureInfo.InvariantCulture),
                    "entries",
                    entries,
                    "gapBeforeWindow",
                    gap ? "true" : "false",
                    "fromMovieTick",
                    from.ToString(CultureInfo.InvariantCulture),
                    "nextAfterSequence",
                    nextAfterSequence.ToString(
                        CultureInfo.InvariantCulture)));
        }

        private async Task<AutomationResultEnvelope> GetCombatStateAsync(
            RuntimeSessionClient session,
            AutomationCommandEnvelope command,
            CancellationToken cancellationToken)
        {
            var envelope = await SendRuntimeAsync(session,
                IpcMessageTypes.RequestSnapshot,
                Fields("requestId", command.RequestId, "includeCombatState", "true"),
                command.RequestId, IpcMessageTypes.RuntimeStatus, cancellationToken);
            var fields = Decode(envelope);
            if (!fields.TryGetValue("combatStateJson", out var json)
                || !fields.TryGetValue("combatStateSequence", out var sequence)
                || !fields.TryGetValue("movieTick", out var movieTick)
                || !fields.TryGetValue("capturedAtUtc", out var capturedText)
                || !DateTimeOffset.TryParse(capturedText, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var captured))
                return Result(command, false, "NoCombatState",
                    "Runtime did not return a fresh requested combat snapshot.");

            var age = Math.Max(0, (DateTimeOffset.UtcNow - captured).TotalMilliseconds);
            var data = Fields("json", json, "sequence", sequence, "movieTick", movieTick,
                "capturedAtUtc", capturedText,
                "ageMilliseconds", age.ToString("F0", CultureInfo.InvariantCulture));
            return age > StateFreshnessBudgetMilliseconds
                ? Result(command, false, "StaleState", "Combat snapshot exceeded the freshness budget.", data)
                : Result(command, true, "Ok", "Fresh combat snapshot returned; inspect provider failures for coverage.", data);
        }

        private async Task<AutomationResultEnvelope> GetStateAsync(
            RuntimeSessionClient session,
            AutomationCommandEnvelope command,
            CancellationToken cancellationToken)
        {
            var statusOnly = command.Arguments.TryGetValue("statusOnly", out var statusFlag)
                && statusFlag == "true";
            var envelope = await SendRuntimeAsync(
                session,
                IpcMessageTypes.RequestSnapshot,
                statusOnly ? Fields("requestId", command.RequestId, "statusOnly", "true")
                    : Fields("requestId", command.RequestId),
                command.RequestId,
                IpcMessageTypes.RuntimeStatus,
                cancellationToken);
            var runtimeFields = Decode(envelope);
            if (statusOnly)
            {
                if (!runtimeFields.TryGetValue("capturedAtUtc", out var capturedText)
                    || !DateTimeOffset.TryParseExact(capturedText, "O", CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var captured))
                    return Result(command, false, "InvalidRuntimeState", "Runtime status timestamp is missing or invalid.");
                var age = (DateTimeOffset.UtcNow - captured).TotalMilliseconds;
                if (age < 0 || age > StateFreshnessBudgetMilliseconds)
                    return Result(command, false, "StaleState", "Runtime status exceeded the freshness budget.");
                var data = new Dictionary<string, string>(runtimeFields, StringComparer.Ordinal)
                {
                    ["availability.semanticSnapshot"] = "not-requested",
                    ["ageMilliseconds"] = age.ToString("F0", CultureInfo.InvariantCulture)
                };
                return Result(command, true, "Ok", "Fresh operational status; no gameplay snapshot requested.", data);
            }
            if (!TryBuildAutomationState(
                    session,
                    runtimeFields,
                    out var state,
                    out var error))
            {
                return Result(
                    command,
                    false,
                    "InvalidRuntimeState",
                    error);
            }

            var resultFields = new Dictionary<string, string>(
                runtimeFields,
                StringComparer.Ordinal)
            {
                ["stateJson"] =
                    AutomationStateCodec.Serialize(state!),
                ["ageMilliseconds"] =
                    state!.AgeMilliseconds.ToString(
                        CultureInfo.InvariantCulture)
            };
            if (state.AgeMilliseconds
                > StateFreshnessBudgetMilliseconds)
            {
                return Result(
                    command,
                    false,
                    "StaleState",
                    "Runtime state exceeds the 5000 ms freshness budget.",
                    resultFields);
            }

            return Result(
                command,
                true,
                "Ok",
                "Fresh canonical semantic state returned.",
                resultFields);
        }

        private bool TryBuildAutomationState(
            RuntimeSessionClient session,
            IReadOnlyDictionary<string, string> runtimeFields,
            out AutomationStateEnvelope? state,
            out string error)
        {
            state = null;
            error = string.Empty;
            if (!runtimeFields.TryGetValue(
                    "capturedAtUtc",
                    out var capturedText)
                || !DateTimeOffset.TryParseExact(
                    capturedText,
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var capturedAtUtc))
            {
                error = "Runtime capture timestamp is missing or invalid.";
                return false;
            }

            if (!runtimeFields.TryGetValue(
                    "movieTick",
                    out var movieTickText)
                || !long.TryParse(
                    movieTickText,
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var movieTick)
                || movieTick < -1)
            {
                error = "Runtime movie tick is missing or invalid.";
                return false;
            }

            if (!TryRequiredRuntimeField(
                    runtimeFields,
                    "controlMode",
                    out var controlMode)
                || !TryRequiredRuntimeField(
                    runtimeFields,
                    "phase",
                    out var phase)
                || !TryRequiredRuntimeField(
                    runtimeFields,
                    "sha256",
                    out var snapshotSha256)
                || !TryRequiredRuntimeField(
                    runtimeFields,
                    "json",
                    out var snapshotJson))
            {
                error =
                    "Runtime semantic snapshot fields are incomplete.";
                return false;
            }

            var fields = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["availability.controlMode"] = "available",
                ["availability.playbackMode"] =
                    RuntimeAvailability(
                        runtimeFields,
                        "playbackMode"),
                ["availability.sceneEpoch"] =
                    RuntimeAvailability(
                        runtimeFields,
                        "sceneEpoch"),
                ["availability.replaySaveRestore"] =
                    RuntimeAvailability(
                        runtimeFields,
                        "replaySaveRestoreActive"),
                ["availability.semanticSnapshot"] =
                    "available",
                ["availability.verificationEligibility"] =
                    RuntimeAvailability(
                        runtimeFields,
                        "verificationEligibility"),
                ["controlMode"] = controlMode!,
                ["semanticSnapshotJson"] = snapshotJson!,
                ["verificationEligibility"] =
                    OptionalRuntimeField(
                        runtimeFields,
                        "verificationEligibility"),
                ["playbackMode"] =
                    OptionalRuntimeField(
                        runtimeFields,
                        "playbackMode")
            };
            foreach (var optional in new[]
                     {
                         "sceneEpoch",
                          "mutationTransactionPending",
                          "recordingActive",
                          "runUntilMovieTick",
                          "movieTickSource",
                          "lastReplayMovieTick",
                           "lastPlaybackStopReason",
                           "lastPlaybackFault",
                           "controlFault",
                           "lastControlAbortReason",
                           "lastStepInterruptionReason",
                           "lastInterruptedStepRequestedTicks",
                           "lastInterruptedStepCommittedTicks",
                           "disconnectCleanupPending",
                           "lastBindingRestoreEquivalent",
                           "pauseLeaseReassertionCount",
                           "pausedBoundaryWaitCount",
                           "pausedBoundaryPumpCount",
                           "sceneTransitionPassThroughCount",
                           "sceneTransitionPassThroughActive",
                           "controlGateStrategyId",
                           "usesCompletedFrameBoundaryGate",
                           "deferredRecordingArmEnabled",
                           "deferredRecordingArmRunId",
                           "deferredRecordingArmReleaseConsumed",
                            "deferredRecordingArmPauseArmed",
                            "deferredRecordingArmReplayArmed",
                            "deferredRecordingArmNeutralPreRollCompletedFrameCount",
                            "deferredRecordingArmActivationAttempted",
                           "deferredRecordingArmActivationSucceeded",
                           "deferredRecordingArmActivationCount",
                           "deferredRecordingArmActivationError",
                           "frozenHeroActionUpdateCount",
                           "advancedHeroActionUpdateCount",
                           "heroActionPhaseRejectionCount",
                           "heroActionUpdateObserved",
                           "lastHeroActionUpdateAdvanced",
                           "lastHeroActionUpdateTick",
                           "ignoredFocusLossCount",
                          "replayObservationCount",
                          "replaySuspendedRawInputTickCount",
                          "replayMismatchCount",
                          "firstReplayMismatchMovieTick",
                          "firstReplayMismatchExpected",
                          "firstReplayMismatchActual",
                          "lastReplayMismatchMovieTick",
                          "lastReplayMismatchExpected",
                          "lastReplayMismatchActual",
                          "replayPhysicalNoiseDetected",
                          "replayDeterministicRngEnabled",
                          "replayDeterministicRngSeed",
                          "replayDeterministicRngProfile",
                          "replayDeterministicRngStatus",
                          "replayDeterministicRngResetCount",
                          "lastReplayRngBeforeSha256",
                          "lastReplayRngStateSha256",
                          "lastReplayRngAppliedSeed",
                          "lastReplayRngBoundary",
                          "lastReplayRngScene",
                          "replaySaveRestoreActive",
                          "replaySaveRestorePhase",
                          "replaySaveRestoreStatus",
                          "replaySaveRestoreCurrentMovieTick",
                          "replaySaveRestoreTargetMovieTick",
                          "replaySaveRestoreNextMovieTick",
                          "replaySaveRestoreFraction",
                          "replaySaveRestoreRequiresOverwriteApproval",
                          "replaySaveRestoreDetail",
                          "replaySaveRestoreExpectedSemanticSha256",
                          "replaySaveRestoreActualSemanticSha256",
                          "replaySaveRestoreBindingEquivalent",
                          "replaySaveRestoreSettingsEquivalent",
                          "replaySaveRestoreSemanticProjectionId",
                          "replaySaveRestoreExpectedVerificationSha256",
                          "replaySaveRestoreActualVerificationSha256",
                          "replaySaveRestoreStrictSemanticEquivalent"
                      })
            {
                if (runtimeFields.TryGetValue(
                        optional,
                        out var value))
                {
                    fields[optional] = value;
                }
            }

            AutomationCapabilityCatalog catalog;
            lock (sync)
            {
                catalog = capabilityCatalog
                          ?? new AutomationCapabilityCatalog(
                              session.AutomationMode,
                              session.DebugMutationEnabled);
            }

            var activeCapabilities = catalog.Items
                .Where(
                    item => !string.Equals(
                        item.Availability,
                        "disabled",
                        StringComparison.Ordinal))
                .Select(item => item.CommandId)
                .ToArray();
            state = new AutomationStateEnvelope(
                session.SessionId,
                session.EnvironmentManifestSha256,
                controlMode!,
                movieTick,
                phase!,
                capturedAtUtc,
                Math.Max(
                    0,
                    (long)(DateTimeOffset.UtcNow - capturedAtUtc)
                    .TotalMilliseconds),
                snapshotSha256!,
                fields,
                activeCapabilities);
            return true;
        }

        private static bool TryRequiredRuntimeField(
            IReadOnlyDictionary<string, string> fields,
            string name,
            out string? value)
        {
            return fields.TryGetValue(name, out value)
                   && !string.IsNullOrWhiteSpace(value);
        }

        private static string OptionalRuntimeField(
            IReadOnlyDictionary<string, string> fields,
            string name)
        {
            return fields.TryGetValue(name, out var value)
                ? value
                : string.Empty;
        }

        private static string RuntimeAvailability(
            IReadOnlyDictionary<string, string> fields,
            string name)
        {
            return fields.TryGetValue(name, out var value)
                   && !string.IsNullOrEmpty(value)
                ? "available"
                : "unsupported";
        }

        private AutomationResultEnvelope GetLatest(
            AutomationCommandEnvelope command,
            string messageType,
            string emptyCode,
            string emptyDetail)
        {
            lock (sync)
            {
                if (latest.TryGetValue(
                        messageType,
                        out var cached))
                {
                    var data = new Dictionary<string, string>(
                        cached.Fields,
                        StringComparer.Ordinal)
                    {
                        ["ageMilliseconds"] =
                            Math.Max(
                                    0,
                                    (long)(DateTimeOffset.UtcNow
                                           - cached.ReceivedAtUtc)
                                    .TotalMilliseconds)
                                .ToString(
                                    CultureInfo.InvariantCulture)
                    };
                    return Result(
                        command,
                        true,
                        "Ok",
                        "Latest event returned.",
                        data);
                }
            }

            return Result(
                command,
                true,
                emptyCode,
                emptyDetail,
                Fields("available", "false"));
        }

        private async Task<AutomationResultEnvelope> GetMovieAsync(
            RuntimeSessionClient session,
            AutomationCommandEnvelope command,
            CancellationToken cancellationToken)
        {
            if (command.Arguments.ContainsKey("listBranches") || command.Arguments.ContainsKey("branchOffset"))
            {
                if (!command.Arguments.TryGetValue("listBranches", out var list) || list != "true"
                    || command.Arguments.Keys.Any(key => key != "listBranches" && key != "branchOffset")
                    || !int.TryParse(command.Arguments.TryGetValue("branchOffset", out var offsetText) ? offsetText : "0",
                        NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset > int.MaxValue - 50)
                    return Result(command, false, "InvalidArguments", "Branch listing requires listBranches=true and a non-negative branchOffset.");
                var entries = RequireWorkspace().ListBranches(offset, 51);
                return Result(command, true, "Ok", "Branch metadata; contents are validated when selected for use.",
                    Fields("branchesJson", System.Text.Json.JsonSerializer.Serialize(entries.Take(50)),
                        "nextOffset", entries.Count > 50 ? (offset + 50).ToString(CultureInfo.InvariantCulture) : "",
                        "verification", "NotChecked"));
            }
            if (command.Arguments.ContainsKey("exportId") || command.Arguments.ContainsKey("exportChunk"))
            {
                if (!command.Arguments.TryGetValue("includeLifecycle", out var lifecycle) || lifecycle != "true"
                    || !command.Arguments.TryGetValue("exportId", out var exportId) || !MovieProtocolV1.IsLowerSha256(exportId)
                    || !command.Arguments.TryGetValue("exportChunk", out var chunkText)
                    || !int.TryParse(chunkText, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                    return Result(command, false, "InvalidArguments", "Chunk reads require includeLifecycle=true, exportId and exportChunk.");
                lock (sync)
                {
                    var export = currentLifecycleExport;
                    if (boundSession != session || export == null || export.Sha256 != exportId)
                        return Result(command, false, "ExportExpired", "Export is no longer cached; request a new complete export.");
                    if (index < 0 || index >= export.ChunkCount)
                        return Result(command, false, "InvalidArguments", "Export chunk is outside snapshot bounds.");
                    return Result(command, true, "Ok", "Immutable lifecycle export chunk.",
                        Fields("exportId", exportId, "exportChunk", index.ToString(CultureInfo.InvariantCulture),
                            "exportBytes", export.Length.ToString(CultureInfo.InvariantCulture),
                            "exportChunks", export.ChunkCount.ToString(CultureInfo.InvariantCulture),
                            "chunkBase64", Convert.ToBase64String(export.GetChunk(index))));
                }
            }
            if (command.Arguments.TryGetValue("includeLifecycle", out var includeLifecycle))
            {
                if (includeLifecycle != "true") return Result(command, false, "InvalidArguments", "includeLifecycle must be true.");
                return await GetLifecycleMovieAsync(session, command, cancellationToken);
            }
            var result = await ForwardAsync(
                session,
                command,
                IpcMessageTypes.RequestMovie,
                Fields("requestId", command.RequestId),
                IpcMessageTypes.MovieDocument,
                cancellationToken);
            if (result.Success
                && result.Data.TryGetValue(
                    "available",
                    out var available)
                && available == "true"
                && result.Data.TryGetValue(
                    "movieBase64",
                    out var base64)
                && result.Data.TryGetValue(
                    "movieId",
                    out var id))
            {
                try
                {
                    var bytes = Convert.FromBase64String(base64);
                    lock (sync)
                    {
                        currentMovieBytes = bytes;
                        currentMovieId = id;
                        currentLifecycleSource = null;
                        currentLifecycleExport = null;
                    }
                }
                catch (FormatException)
                {
                    return Result(
                        command,
                        false,
                        "InvalidRuntimeResponse",
                        "Runtime movie encoding is invalid.");
                }
            }

            return result;
        }

        private async Task<AutomationResultEnvelope> GetLifecycleMovieAsync(RuntimeSessionClient session,
            AutomationCommandEnvelope command, CancellationToken cancellationToken)
        {
            var snapshot = await ForwardAsync(session, command, IpcMessageTypes.RequestMovie,
                Fields("requestId", command.RequestId, "includeLifecycle", "true"),
                IpcMessageTypes.MovieDocument, cancellationToken);
            if (!snapshot.Success) return snapshot;
            try
            {
                var id = snapshot.Data["exportId"];
                var length = int.Parse(snapshot.Data["exportBytes"], CultureInfo.InvariantCulture);
                var chunks = int.Parse(snapshot.Data["exportChunks"], CultureInfo.InvariantCulture);
                if (!MovieProtocolV1.IsLowerSha256(id) || length <= 0 || length > MovieLifecycleExport.MaximumBytes
                    || chunks != (length + MovieLifecycleExport.ChunkBytes - 1) / MovieLifecycleExport.ChunkBytes)
                    throw new InvalidDataException("Invalid lifecycle export dimensions.");
                using var stream = new MemoryStream(length);
                for (var index = 0; index < chunks; index++)
                {
                    var response = await ForwardAsync(session, command, IpcMessageTypes.RequestMovie,
                        Fields("requestId", command.RequestId, "exportId", id,
                            "exportChunk", index.ToString(CultureInfo.InvariantCulture)),
                        IpcMessageTypes.MovieDocument, cancellationToken);
                    if (!response.Success) return response;
                    if (response.Data["exportId"] != id || response.Data["exportChunk"] != index.ToString(CultureInfo.InvariantCulture))
                        throw new InvalidDataException("Lifecycle export chunk identity mismatch.");
                    var part = Convert.FromBase64String(response.Data["chunkBase64"]);
                    if (part.Length != Math.Min(MovieLifecycleExport.ChunkBytes, length - (int)stream.Length))
                        throw new InvalidDataException("Lifecycle export chunk length mismatch.");
                    stream.Write(part, 0, part.Length);
                }
                var decoded = MovieLifecycleExport.Decode(stream.ToArray(), id);
                var externalExport = MovieLifecycleExport.Create(decoded.Movie, decoded.CopyBaselineBytes(),
                    decoded.Lifecycle, decoded.CopySlotObjects());
                if (externalExport.Sha256 != id) throw new InvalidDataException("Reconstructed export identity mismatch.");
                if (decoded.Movie.Header.ManifestSha256 != session.EnvironmentManifestSha256)
                {
                    Trace.TraceWarning(
                        $"AutomationBroker.GetLifecycleMovieAsync: lifecycle export ManifestSha256 '{decoded.Movie.Header.ManifestSha256}' differs from session '{session.EnvironmentManifestSha256}'; continuing.");
                }
                var bytes = new MovieCanonicalWriter().WriteUtf8(decoded.Movie);
                var sourceId = RequireWorkspace().GetLifecycleSourceId(bytes, decoded.Lifecycle);
                lock (sync)
                {
                    if (boundSession != session) throw new InvalidDataException("Runtime session changed during export.");
                    currentMovieBytes = bytes;
                    currentMovieId = sourceId;
                    currentLifecycleSource = decoded;
                    currentLifecycleExport = externalExport;
                }
                var base64 = Convert.ToBase64String(bytes);
                var data = Fields("available", "true", "source", "journal", "movieId", sourceId,
                    "exportId", id, "lifecycleOperations", decoded.Lifecycle.Records.Count.ToString(CultureInfo.InvariantCulture),
                    "exportBytes", length.ToString(CultureInfo.InvariantCulture),
                    "exportChunks", chunks.ToString(CultureInfo.InvariantCulture),
                    "exportChunkBytes", MovieLifecycleExport.ChunkBytes.ToString(CultureInfo.InvariantCulture),
                    "exportFormat", "HKLE-v1",
                    "effectiveMovieTick", snapshot.Data["effectiveMovieTick"],
                    "movieBase64", base64.Length <= IpcPayloadCodec.MaximumFieldValueCharacters ? base64 : string.Empty,
                    "movieInline", base64.Length <= IpcPayloadCodec.MaximumFieldValueCharacters ? "true" : "false");
                return Result(command, true, "Ok", "Complete journal snapshot verified and available for lifecycle editing.", data);
            }
            catch (Exception exception) when (exception is InvalidDataException || exception is FormatException
                || exception is KeyNotFoundException || exception is OverflowException || exception is ArgumentException)
            { return Result(command, false, "InvalidRuntimeResponse", exception.Message); }
        }

        private async Task<AutomationResultEnvelope> StopRecordingAsync(
            RuntimeSessionClient session,
            AutomationCommandEnvelope command,
            CancellationToken cancellationToken)
        {
            var result = await ForwardAsync(
                session,
                command,
                IpcMessageTypes.StopRecording,
                Fields("requestId", command.RequestId),
                IpcMessageTypes.MovieDocument,
                cancellationToken);
            if (!result.Success)
            {
                return result;
            }

            if (!result.Data.TryGetValue("available", out var available)
                || available != "true"
                || !result.Data.TryGetValue("movieBase64", out var base64)
                || !result.Data.TryGetValue("movieId", out var movieId))
            {
                return Result(
                    command,
                    false,
                    "InvalidRuntimeResponse",
                    "stopRecording did not publish the recorded canonical movie.");
            }

            try
            {
                var bytes = Convert.FromBase64String(base64);
                var validation = RequireWorkspace().Validate(bytes);
                if (!validation.Success
                    || validation.CanonicalMovieUtf8 == null
                    || validation.BranchMovieId != movieId)
                {
                    return Result(
                        command,
                        false,
                        "InvalidRuntimeResponse",
                        "Recorded movie failed canonical content validation.");
                }

                lock (sync)
                {
                    currentMovieId = movieId;
                    currentMovieBytes = validation.CanonicalMovieUtf8;
                }
            }
            catch (FormatException)
            {
                return Result(
                    command,
                    false,
                    "InvalidRuntimeResponse",
                    "Recorded movie encoding is invalid.");
            }

            return result;
        }

        private AutomationResultEnvelope ValidateMovie(
            AutomationCommandEnvelope command)
        {
            if (!TryCandidate(
                    command,
                    out var bytes,
                    out var error))
            {
                return error!;
            }

            var validated = RequireWorkspace().Validate(bytes!);
            return MovieBranchEnvelope(command, validated);
        }

        private AutomationResultEnvelope ProposeMovie(
            AutomationCommandEnvelope command)
        {
            if (!TryCandidate(
                    command,
                    out var bytes,
                    out var error))
            {
                return error!;
            }

            var baseMovieId = RequiredArgument(
                command,
                "baseMovieId");
            string current;
            lock (sync)
            {
                current = currentMovieId;
            }

            var proposed = RequireWorkspace().Propose(
                baseMovieId,
                current,
                bytes!);
            return MovieBranchEnvelope(command, proposed);
        }

        private async Task<AutomationResultEnvelope>
            ApplyMovieBranchAsync(
                RuntimeSessionClient session,
                AutomationCommandEnvelope command,
                CancellationToken cancellationToken)
        {
            var branchId = RequiredArgument(
                command,
                "branchMovieId");
            var bytes = RequireWorkspace().ReadBranch(branchId);
            var result = await UploadMovieAsync(
                session,
                command,
                branchId,
                bytes,
                cancellationToken);
            if (result.Success)
            {
                lock (sync)
                {
                    currentMovieId = branchId;
                    currentMovieBytes = bytes;
                }
            }

            return result;
        }

        private async Task<AutomationResultEnvelope>
            ApplyBranchAndSeekAsync(
                RuntimeSessionClient session,
                string clientId,
                AutomationCommandEnvelope command,
                CancellationToken cancellationToken)
        {
            var branchId = RequiredArgument(command, "branchMovieId");
            if (RequireWorkspace().HasLifecycleBranch(branchId))
            {
                var plan = RequireWorkspace().ReadLifecycleBranch(branchId).CreateExecutionPlan();
                var planBytes = plan.Serialize();
                var planHash = Sha256Utility.ComputeHex(planBytes);
                var staged = await UploadMovieAsync(session, command, planHash, planBytes, cancellationToken, lifecyclePlan: true);
                if (!staged.Success) return staged;
                var started = await SeekMovieTickAsync(session, clientId, command,
                    ColdRestoreOperationKind.ApplyBranchAndSeek, cancellationToken, planHash);
                return started;
            }
            var bytes = RequireWorkspace().ReadBranch(branchId);
            var upload = await UploadMovieAsync(
                session,
                command,
                branchId,
                bytes,
                cancellationToken);
            if (!upload.Success)
            {
                return upload;
            }

            lock (sync)
            {
                currentMovieId = branchId;
                currentMovieBytes = bytes;
            }

            return await SeekMovieTickAsync(
                session,
                clientId,
                command,
                ColdRestoreOperationKind.ApplyBranchAndSeek,
                cancellationToken);
        }

        private async Task<AutomationResultEnvelope> SeekMovieTickAsync(
            RuntimeSessionClient session,
            string clientId,
            AutomationCommandEnvelope command,
            ColdRestoreOperationKind operationKind,
            CancellationToken cancellationToken, string lifecyclePlanHash = "")
        {
            if (coldRestoreSupervisor == null)
            {
                return Result(
                    command,
                    false,
                    "ColdRestoreUnavailable",
                    "Vanilla-equivalent cold movie seek is not configured in this Companion instance.");
            }

            if (!long.TryParse(
                    RequiredArgument(command, "targetMovieTick"),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var targetMovieTick)
                || !int.TryParse(
                    RequiredArgument(command, "expectedSceneEpoch"),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var expectedSceneEpoch)
                || targetMovieTick < 0
                || expectedSceneEpoch < 0
                || !command.ExpectedMovieTick.HasValue
                || command.ExpectedMovieTick.Value < 0)
            {
                return Result(
                    command,
                    false,
                    "InvalidArguments",
                    "Cold movie seek requires non-negative target, movie-tick CAS, and scene-epoch CAS values.");
            }

            var started = await coldRestoreSupervisor.BeginMovieSeekAsync(
                session,
                operationKind,
                targetMovieTick,
                command.ExpectedMovieTick.Value,
                expectedSceneEpoch,
                string.Equals(
                    clientId,
                    "companion-ui",
                    StringComparison.Ordinal)
                    ? "studio"
                    : "automation",
                cancellationToken, lifecyclePlanHash);
            return Result(
                command,
                true,
                "ColdRestorePrepared",
                "Vanilla-equivalent cold movie seek was persisted and started.",
                Fields(
                    "branchMovieId",
                    lifecyclePlanHash.Length == 0 ? currentMovieId : RequiredArgument(command, "branchMovieId"),
                    "equivalenceClass",
                    "VanillaEquivalent",
                    "intentSha256",
                    started.IntentSha256,
                    "operationId",
                    started.OperationId,
                    "operationKind",
                    operationKind.ToString(),
                    "restoreStrategy",
                    "VanillaEquivalentColdReplay",
                    "state",
                    started.Snapshot.Latest.State.ToString(),
                    "targetMovieTick",
                    RequiredArgument(command, "targetMovieTick"),
                    "scheduled",
                    "true"));
        }

        private async Task<AutomationResultEnvelope>
            RunInputBatchAsync(
                RuntimeSessionClient session,
                AutomationCommandEnvelope command,
                CancellationToken cancellationToken)
        {
            if (!TryCandidate(command, out var suppliedBytes, out var error))
            {
                return error!;
            }

            var validated = RequireWorkspace().Validate(suppliedBytes!);
            if (!validated.Success
                || validated.CanonicalMovieUtf8 == null)
            {
                return MovieBranchEnvelope(command, validated);
            }

            if (validated.ExpandedTicks < 1
                || validated.ExpandedTicks
                   > MovieProtocolV1.DefaultMaxExpandedTicks)
            {
                return Result(
                    command,
                    false,
                    "BatchSize",
                    "A committed input batch must contain 1 to "
                    + MovieProtocolV1.DefaultMaxExpandedTicks.ToString(
                        CultureInfo.InvariantCulture)
                    + " input ticks.");
            }

            if (command.CommandId == AutomationCommandIds.StepWithInput
                && validated.ExpandedTicks != 1)
            {
                return Result(
                    command,
                    false,
                    "SingleFrameRequired",
                    "stepWithInput requires exactly one expanded input tick.");
            }

            return await ScheduleInputBatchAsync(
                session,
                command,
                validated,
                null,
                cancellationToken);
        }

        private async Task<AutomationResultEnvelope>
            ScheduleInputBatchAsync(
                RuntimeSessionClient session,
                AutomationCommandEnvelope command,
                MovieBranchResult validated,
                InputBatchTransactionResult? transaction,
                CancellationToken cancellationToken)
        {
            var upload = await UploadMovieAsync(
                session,
                command,
                validated.BranchMovieId,
                validated.CanonicalMovieUtf8!,
                cancellationToken);
            if (!upload.Success)
            {
                return upload;
            }

            var runtime = await SendRuntimeAsync(
                session,
                IpcMessageTypes.RunInputBatch,
                Fields(
                    "batchMovieId",
                    validated.BranchMovieId,
                    "expandedTicks",
                    validated.ExpandedTicks.ToString(
                        CultureInfo.InvariantCulture),
                    "expectedMovieTick",
                    command.ExpectedMovieTick!.Value.ToString(
                        CultureInfo.InvariantCulture),
                    "expectedSceneEpoch",
                    RequiredArgument(command, "expectedSceneEpoch"),
                    "requestId",
                    command.RequestId),
                command.RequestId,
                IpcMessageTypes.CommandAccepted,
                cancellationToken);
            var accepted = RuntimeEnvelopeResult(command, runtime);
            if (!accepted.Success)
            {
                return accepted;
            }

            lock (sync)
            {
                currentMovieId = validated.BranchMovieId;
                currentMovieBytes = validated.CanonicalMovieUtf8;
            }

            var fields = new Dictionary<string, string>(
                Fields(
                    "batchMovieId",
                    validated.BranchMovieId,
                    "expandedTicks",
                    validated.ExpandedTicks.ToString(
                        CultureInfo.InvariantCulture),
                    "expectedSceneEpoch",
                    RequiredArgument(command, "expectedSceneEpoch"),
                    "scheduled",
                    "true"),
                StringComparer.Ordinal);
            if (transaction != null)
            {
                fields["transactionId"] = transaction.TransactionId;
                fields["transactionSha256"] =
                    validated.BranchMovieId;
                fields["chunkCount"] = transaction.ChunkCount.ToString(
                    CultureInfo.InvariantCulture);
                fields["chunkSha256s"] = string.Join(
                    ",",
                    transaction.ChunkSha256s);
            }

            return Result(
                command,
                true,
                "Ok",
                command.CommandId == AutomationCommandIds.StepWithInput
                    ? "Single-frame input was scheduled atomically with one movie-tick step."
                    : "Input batch was scheduled atomically with canonical movie-tick stepping.",
                fields);
        }

        private AutomationResultEnvelope BeginInputBatch(
            RuntimeSessionClient session,
            string clientId,
            string connectionId,
            AutomationCommandEnvelope command)
        {
            var transaction = inputBatches.Begin(
                clientId,
                connectionId,
                command.LeaseId!,
                session.SessionId,
                session.EnvironmentManifestSha256,
                command.ExpectedMovieTick!.Value,
                ParseExpectedSceneEpoch(command));
            return InputBatchTransactionEnvelope(command, transaction);
        }

        private AutomationResultEnvelope AppendInputBatch(
            string clientId,
            string connectionId,
            AutomationCommandEnvelope command)
        {
            if (!TryDecodeMovieArgument(
                    command,
                    "candidateMovieBase64",
                    out var bytes,
                    out var decodeError))
            {
                return decodeError!;
            }

            var transaction = inputBatches.Append(
                RequiredArgument(command, "transactionId"),
                clientId,
                connectionId,
                command.LeaseId!,
                command.ExpectedMovieTick!.Value,
                ParseExpectedSceneEpoch(command),
                int.Parse(
                    RequiredArgument(command, "chunkIndex"),
                    CultureInfo.InvariantCulture),
                bytes!);
            return InputBatchTransactionEnvelope(command, transaction);
        }

        private async Task<AutomationResultEnvelope>
            CommitInputBatchAsync(
                RuntimeSessionClient session,
                string clientId,
                string connectionId,
                AutomationCommandEnvelope command,
                CancellationToken cancellationToken)
        {
            var transaction = inputBatches.Commit(
                RequiredArgument(command, "transactionId"),
                clientId,
                connectionId,
                command.LeaseId!,
                command.ExpectedMovieTick!.Value,
                ParseExpectedSceneEpoch(command));
            if (!transaction.Success
                || transaction.CanonicalMovieUtf8 == null)
            {
                return InputBatchTransactionEnvelope(
                    command,
                    transaction);
            }

            var validated = new MovieBranchResult(
                true,
                "Valid",
                transaction.Detail,
                transaction.MovieId,
                transaction.CanonicalMovieUtf8,
                transaction.ExpandedTicks);
            var scheduled = await ScheduleInputBatchAsync(
                session,
                command,
                validated,
                transaction,
                cancellationToken);
            if (scheduled.Success)
            {
                inputBatches.Complete(transaction.TransactionId);
            }

            return scheduled;
        }

        private AutomationResultEnvelope CancelInputBatch(
            string clientId,
            string connectionId,
            AutomationCommandEnvelope command)
        {
            var transaction = inputBatches.Cancel(
                RequiredArgument(command, "transactionId"),
                clientId,
                connectionId,
                command.LeaseId!,
                command.ExpectedMovieTick!.Value,
                ParseExpectedSceneEpoch(command));
            return InputBatchTransactionEnvelope(command, transaction);
        }

        private static int ParseExpectedSceneEpoch(
            AutomationCommandEnvelope command)
        {
            return int.Parse(
                RequiredArgument(command, "expectedSceneEpoch"),
                CultureInfo.InvariantCulture);
        }

        private AutomationResultEnvelope InputBatchTransactionEnvelope(
            AutomationCommandEnvelope command,
            InputBatchTransactionResult transaction)
        {
            return Result(
                command,
                transaction.Success,
                transaction.Code,
                transaction.Detail,
                Fields(
                    "transactionId",
                    transaction.TransactionId,
                    "chunkCount",
                    transaction.ChunkCount.ToString(
                        CultureInfo.InvariantCulture),
                    "expandedTicks",
                    transaction.ExpandedTicks.ToString(
                        CultureInfo.InvariantCulture),
                    "chunkSha256s",
                    string.Join(",", transaction.ChunkSha256s),
                    "expectedMovieTick",
                    transaction.ExpectedMovieTick.ToString(
                        CultureInfo.InvariantCulture),
                    "expectedSceneEpoch",
                    transaction.ExpectedSceneEpoch.ToString(
                        CultureInfo.InvariantCulture)));
        }

        private async Task<AutomationResultEnvelope>
            EditMovieTimelineAsync(
                RuntimeSessionClient session,
                AutomationCommandEnvelope command,
                TimelineEditKind kind,
                CancellationToken cancellationToken)
        {
            var requestedBase = RequiredArgument(command, "baseMovieId");
            if (command.Arguments.TryGetValue("includeLifecycle", out var editLifecycle) && editLifecycle == "true"
                && RequireWorkspace().HasLifecycleBranch(requestedBase))
            {
                byte[]? branchReplacement = null;
                if (kind != TimelineEditKind.Delete && !TryDecodeMovieArgument(command,
                    "replacementMovieBase64", out branchReplacement, out var branchDecodeError)) return branchDecodeError!;
                try
                {
                    var parent = RequireWorkspace().ReadLifecycleBranch(requestedBase);
                    if (parent.Plan.InputEdit.Movie.Header.ManifestSha256 != session.EnvironmentManifestSha256)
                    {
                        Trace.TraceWarning(
                            $"AutomationBroker.EditMovieTimelineAsync: branch ManifestSha256 '{parent.Plan.InputEdit.Movie.Header.ManifestSha256}' differs from session '{session.EnvironmentManifestSha256}'; continuing.");
                    }
                    branchReplacement ??= new MovieCanonicalWriter().WriteUtf8(new MovieDocument("empty-replacement",
                        parent.Plan.InputEdit.Movie.Header, Array.Empty<MovieCommand>()));
                    var branch = RequireWorkspace().EditLifecycleBranch(requestedBase, kind,
                        long.Parse(RequiredArgument(command, "startTick"), CultureInfo.InvariantCulture),
                        kind == TimelineEditKind.Insert ? 0 : long.Parse(RequiredArgument(command,
                            kind == TimelineEditKind.Delete ? "count" : "deleteCount"), CultureInfo.InvariantCulture),
                        branchReplacement);
                    return Result(command, true, "LifecycleBranchStored", "Branch edit stored; native replay validation is still required.",
                        Fields("branchMovieId", branch.BranchId, "branchFormat", "hklbranch-v3",
                            "parentMovieId", requestedBase,
                            "undoBranchMovieId", requestedBase,
                            "expandedTicks", branch.Plan.InputEdit.ExpandedTicks.ToString(CultureInfo.InvariantCulture),
                            "lifecycleOperations", branch.Plan.Operations.Count.ToString(CultureInfo.InvariantCulture),
                            "requiresLifecycleReplay", "true"));
                }
                catch (Exception exception) when (exception is InvalidOperationException || exception is ArgumentException
                    || exception is InvalidDataException || exception is OverflowException)
                { return Result(command, false, "InvalidLifecycleEdit", exception.Message); }
            }
            // Always refresh the Runtime movie. A preceding input batch loads
            // a short execution movie, while StopRecording can replace it
            // with the complete authoritative journal movie.
            var refreshed = await GetMovieAsync(
                session,
                command,
                cancellationToken);
            if (!refreshed.Success)
            {
                return refreshed;
            }

            string currentId;
            byte[]? currentBytes;
            lock (sync)
            {
                currentId = currentMovieId;
                currentBytes = currentMovieBytes == null
                    ? null
                    : (byte[])currentMovieBytes.Clone();
            }

            byte[]? replacement = null;
            if (kind != TimelineEditKind.Delete
                && !TryDecodeMovieArgument(
                    command,
                    "replacementMovieBase64",
                    out replacement,
                    out var decodeError))
            {
                return decodeError!;
            }

            var startTick = long.Parse(
                RequiredArgument(command, "startTick"),
                CultureInfo.InvariantCulture);
            var deleteCount = kind == TimelineEditKind.Insert
                ? 0L
                : long.Parse(
                    RequiredArgument(
                        command,
                        kind == TimelineEditKind.Delete
                            ? "count"
                            : "deleteCount"),
                    CultureInfo.InvariantCulture);
            if (command.Arguments.TryGetValue("includeLifecycle", out var lifecycleFlag) && lifecycleFlag == "true")
            {
                MovieLifecycleExportData? lifecycleSource;
                lock (sync) { lifecycleSource = currentLifecycleSource; }
                if (lifecycleSource == null || currentBytes == null)
                    return Result(command, false, "LifecycleSourceUnavailable", "Refresh the complete journal before editing.");
                if (replacement == null)
                    replacement = new MovieCanonicalWriter().WriteUtf8(new MovieDocument("empty-replacement",
                        lifecycleSource.Movie.Header, Array.Empty<MovieCommand>()));
                try
                {
                    var unedited = RequireWorkspace().EditLifecycleTimeline(
                        RequiredArgument(command, "baseMovieId"), currentBytes, lifecycleSource.Lifecycle,
                        lifecycleSource.CopySlotObjects(), TimelineEditKind.Replace, 0, 0,
                        new MovieCanonicalWriter().WriteUtf8(new MovieDocument("unedited-source",
                            lifecycleSource.Movie.Header, Array.Empty<MovieCommand>())),
                        lifecycleSource.CopyBaselineBytes());
                    var branch = RequireWorkspace().EditLifecycleTimeline(
                        RequiredArgument(command, "baseMovieId"), currentBytes, lifecycleSource.Lifecycle,
                        lifecycleSource.CopySlotObjects(), kind, startTick, deleteCount, replacement,
                        lifecycleSource.CopyBaselineBytes());
                    return Result(command, true, "LifecycleBranchStored", "Lifecycle edit stored; native replay validation is still required.",
                        Fields("branchMovieId", branch.BranchId, "branchFormat", "hklbranch-v2",
                            "parentMovieId", currentId,
                            "undoBranchMovieId", unedited.BranchId,
                            "expandedTicks", branch.Plan.InputEdit.ExpandedTicks.ToString(CultureInfo.InvariantCulture),
                            "lifecycleOperations", branch.Plan.Operations.Count.ToString(CultureInfo.InvariantCulture),
                            "requiresLifecycleReplay", "true"));
                }
                catch (Exception exception) when (exception is InvalidOperationException || exception is ArgumentException
                    || exception is InvalidDataException || exception is OverflowException)
                { return Result(command, false, "InvalidLifecycleEdit", exception.Message); }
            }
            var edited = RequireWorkspace().EditTimeline(
                kind,
                RequiredArgument(command, "baseMovieId"),
                currentId,
                currentBytes,
                startTick,
                deleteCount,
                replacement);
            return Result(
                command,
                edited.Success,
                edited.Code,
                edited.Detail,
                Fields(
                    "branchMovieId",
                    edited.BranchMovieId,
                    "canonicalMovieBase64",
                    edited.CanonicalMovieUtf8 == null
                        ? string.Empty
                        : Convert.ToBase64String(
                            edited.CanonicalMovieUtf8),
                    "deletedTicks",
                    edited.DeletedTicks.ToString(
                        CultureInfo.InvariantCulture),
                    "expandedTicks",
                    edited.ExpandedTicks.ToString(
                        CultureInfo.InvariantCulture),
                    "insertedTicks",
                    edited.InsertedTicks.ToString(
                        CultureInfo.InvariantCulture),
                    "invalidatedCheckpoints",
                    string.Join(
                        ",",
                        edited.InvalidatedCheckpoints),
                    "kind",
                    edited.Kind.ToString(),
                    "parentMovieId",
                    edited.ParentMovieId,
                    "previousExpandedTicks",
                    edited.PreviousExpandedTicks.ToString(
                        CultureInfo.InvariantCulture),
                    "startTick",
                    edited.StartTick.ToString(
                        CultureInfo.InvariantCulture),
                    "tickDelta",
                    edited.TickDelta.ToString(
                        CultureInfo.InvariantCulture)));
        }

        private async Task<AutomationResultEnvelope> UploadMovieAsync(
            RuntimeSessionClient session,
            AutomationCommandEnvelope command,
            string movieId,
            byte[] bytes,
            CancellationToken cancellationToken, bool lifecyclePlan = false)
        {
            var runtimeRequest = "upload-"
                                 + Guid.NewGuid().ToString("N");
            var chunks = Math.Max(
                1,
                (bytes.Length + MovieChunkBytes - 1)
                / MovieChunkBytes);
            var beginFields = new Dictionary<string, string> {
                ["chunkCount"] = chunks.ToString(CultureInfo.InvariantCulture), ["movieId"] = movieId,
                ["requestId"] = runtimeRequest, ["totalBytes"] = bytes.Length.ToString(CultureInfo.InvariantCulture)
            };
            if (lifecyclePlan) beginFields["payloadKind"] = "lifecyclePlan";
            var begin = await SendRuntimeAsync(
                session,
                IpcMessageTypes.UploadMovieBegin,
                beginFields,
                runtimeRequest,
                IpcMessageTypes.CommandAccepted,
                cancellationToken);
            if (begin.MessageType == IpcMessageTypes.CommandRejected)
            {
                return RuntimeEnvelopeResult(command, begin);
            }

            for (var index = 0; index < chunks; index++)
            {
                var offset = index * MovieChunkBytes;
                var length = Math.Min(
                    MovieChunkBytes,
                    bytes.Length - offset);
                var chunk = new byte[length];
                Buffer.BlockCopy(bytes, offset, chunk, 0, length);
                var response = await SendRuntimeAsync(
                    session,
                    IpcMessageTypes.UploadMovieChunk,
                    Fields(
                        "base64",
                        Convert.ToBase64String(chunk),
                        "index",
                        index.ToString(
                            CultureInfo.InvariantCulture),
                        "requestId",
                        runtimeRequest),
                    runtimeRequest,
                    IpcMessageTypes.CommandAccepted,
                    cancellationToken);
                if (response.MessageType
                    == IpcMessageTypes.CommandRejected)
                {
                    return RuntimeEnvelopeResult(command, response);
                }
            }

            var end = await SendRuntimeAsync(
                session,
                IpcMessageTypes.UploadMovieEnd,
                Fields("requestId", runtimeRequest),
                runtimeRequest,
                IpcMessageTypes.CommandAccepted,
                cancellationToken);
            var result = RuntimeEnvelopeResult(command, end);
            return result.Success
                ? Result(
                    command,
                    true,
                    "Ok",
                    "Movie branch explicitly applied.",
                    Fields(
                        "branchMovieId",
                        movieId,
                        "bytes",
                        bytes.Length.ToString(
                            CultureInfo.InvariantCulture)))
                : result;
        }

        private Task<AutomationResultEnvelope> SimpleRuntimeAsync(
            RuntimeSessionClient session,
            AutomationCommandEnvelope command,
            string messageType,
            CancellationToken cancellationToken)
        {
            return ForwardAsync(
                session,
                command,
                messageType,
                Fields("requestId", command.RequestId),
                IpcMessageTypes.CommandAccepted,
                cancellationToken);
        }

        private async Task<AutomationResultEnvelope>
            ForwardMutationAsync(
            RuntimeSessionClient session,
            AutomationCommandEnvelope command,
            string messageType,
            IEnumerable<string> argumentNames,
            CancellationToken cancellationToken)
        {
            var fields = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["requestId"] = command.RequestId
            };
            foreach (var name in argumentNames)
            {
                fields[name] = RequiredArgument(command, name);
            }

            var mutation = await SendRuntimeAsync(
                session,
                messageType,
                fields,
                command.RequestId,
                IpcMessageTypes.StateMutationResult,
                cancellationToken);
            if (mutation.MessageType
                == IpcMessageTypes.CommandRejected)
            {
                return RuntimeEnvelopeResult(command, mutation);
            }

            var mutationFields = Decode(mutation);
            if (!mutationFields.TryGetValue(
                    "transactionId",
                    out var transactionId)
                || !IpcIdentifier.IsValid(transactionId, 128))
            {
                return Result(
                    command,
                    false,
                    "InvalidRuntimeResponse",
                    "Runtime mutation transaction ID is missing or invalid.");
            }

            var commitRequest =
                "commit-" + Guid.NewGuid().ToString("N");
            var commit = await SendRuntimeAsync(
                session,
                IpcMessageTypes.CommitStateMutation,
                Fields(
                    "requestId",
                    commitRequest,
                    "transactionId",
                    transactionId),
                commitRequest,
                IpcMessageTypes.CommandAccepted,
                cancellationToken);
            if (commit.MessageType
                == IpcMessageTypes.CommandRejected)
            {
                return RuntimeEnvelopeResult(command, commit);
            }

            var committed = new Dictionary<string, string>(
                mutationFields,
                StringComparer.Ordinal)
            {
                ["committed"] = "true",
                ["verificationEligibility"] =
                    "NonVerifiableDebugMutation"
            };
            return Result(
                command,
                true,
                "Ok",
                "Typed debug mutation committed.",
                committed);
        }

        private async Task<AutomationResultEnvelope> ForwardAsync(
            RuntimeSessionClient session,
            AutomationCommandEnvelope command,
            string runtimeMessageType,
            IReadOnlyDictionary<string, string> fields,
            string expectedEventType,
            CancellationToken cancellationToken)
        {
            var envelope = await SendRuntimeAsync(
                session,
                runtimeMessageType,
                fields,
                command.RequestId,
                expectedEventType,
                cancellationToken);
            return RuntimeEnvelopeResult(command, envelope);
        }

        private async Task<AutomationResultEnvelope> BeginColdRestoreAsync(
            RuntimeSessionClient session,
            string clientId,
            AutomationCommandEnvelope command,
            CancellationToken cancellationToken)
        {
            if (coldRestoreSupervisor == null)
            {
                return Result(
                    command,
                    false,
                    "ColdRestoreUnavailable",
                    "Vanilla-equivalent cold restore is not configured in this Companion instance.");
            }

            var started = await coldRestoreSupervisor
                .BeginReplaySaveRestoreAsync(
                    session,
                    RequiredArgument(command, "replaySaveId"),
                    string.Equals(
                        clientId,
                        "companion-ui",
                        StringComparison.Ordinal)
                        ? "studio"
                        : "automation",
                    cancellationToken);
            return Result(
                command,
                true,
                "ColdRestorePrepared",
                "Vanilla-equivalent cold restore was persisted and started.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["equivalenceClass"] = "VanillaEquivalent",
                    ["intentSha256"] = started.IntentSha256,
                    ["operationId"] = started.OperationId,
                    ["restoreStrategy"] =
                        "VanillaEquivalentColdReplay",
                    ["state"] = started.Snapshot.Latest.State.ToString()
                });
        }

        private async Task<IpcEnvelope> SendRuntimeAsync(
            RuntimeSessionClient session,
            string messageType,
            IReadOnlyDictionary<string, string> fields,
            string requestId,
            string expectedEventType,
            CancellationToken cancellationToken)
        {
            var waiter = new PendingRuntimeRequest(expectedEventType);
            lock (sync)
            {
                if (pending.ContainsKey(requestId))
                {
                    throw new InvalidOperationException(
                        "Runtime correlation ID is already pending.");
                }

                pending.Add(requestId, waiter);
            }

            try
            {
                await session.SendCommandAsync(
                    messageType,
                    fields,
                    cancellationToken);
                using (var timeout =
                       CancellationTokenSource
                           .CreateLinkedTokenSource(
                               cancellationToken))
                {
                    timeout.CancelAfter(RuntimeTimeout);
                    return await waiter.Completion.Task.WaitAsync(
                        timeout.Token);
                }
            }
            finally
            {
                lock (sync)
                {
                    pending.Remove(requestId);
                }
            }
        }

        private AutomationResultEnvelope RuntimeEnvelopeResult(
            AutomationCommandEnvelope command,
            IpcEnvelope envelope)
        {
            var fields = Decode(envelope);
            if (envelope.MessageType
                == IpcMessageTypes.CommandRejected)
            {
                return Result(
                    command,
                    false,
                    fields.TryGetValue(
                        "errorCode",
                        out var code)
                        ? SafeCode(code)
                        : "RuntimeRejected",
                    fields.TryGetValue("detail", out var detail)
                        ? detail
                        : "Runtime command rejected.",
                    fields);
            }

            return Result(
                command,
                true,
                "Ok",
                "Runtime operation completed.",
                fields);
        }

        private bool TryCandidate(
            AutomationCommandEnvelope command,
            out byte[]? bytes,
            out AutomationResultEnvelope? error)
        {
            bytes = null;
            error = null;
            try
            {
                bytes = Convert.FromBase64String(
                    RequiredArgument(
                        command,
                        "candidateMovieBase64"));
                return true;
            }
            catch (FormatException)
            {
                error = Result(
                    command,
                    false,
                    "InvalidArgument",
                    "candidateMovieBase64 is invalid.");
                return false;
            }
        }

        private AutomationResultEnvelope MovieBranchEnvelope(
            AutomationCommandEnvelope command,
            MovieBranchResult branch)
        {
            return Result(
                command,
                branch.Success,
                branch.Code,
                branch.Detail,
                Fields(
                    "branchMovieId",
                    branch.BranchMovieId,
                    "expandedTicks",
                    branch.ExpandedTicks.ToString(
                        CultureInfo.InvariantCulture),
                    "stored",
                    branch.Success
                    && command.CommandId
                    == AutomationCommandIds.ProposeMoviePatch
                        ? "true"
                        : "false"));
        }

        private MoviePatchWorkspace RequireWorkspace()
        {
            lock (sync)
            {
                return movieWorkspace
                       ?? throw new InvalidOperationException(
                           "Movie workspace is unavailable.");
            }
        }

        private static string RequiredArgument(
            AutomationCommandEnvelope command,
            string name)
        {
            if (!command.Arguments.TryGetValue(
                    name,
                    out var value))
            {
                throw new InvalidOperationException(
                    "Missing argument: " + name + ".");
            }

            return value;
        }

        private bool TryDecodeMovieArgument(
            AutomationCommandEnvelope command,
            string name,
            out byte[]? bytes,
            out AutomationResultEnvelope? error)
        {
            bytes = null;
            error = null;
            try
            {
                bytes = Convert.FromBase64String(
                    RequiredArgument(command, name));
                if (bytes.Length == 0
                    || bytes.Length > 32 * 1024 * 1024)
                {
                    error = Result(
                        command,
                        false,
                        "InvalidArgument",
                        name + " size is outside protocol bounds.");
                    bytes = null;
                    return false;
                }

                return true;
            }
            catch (FormatException)
            {
                error = Result(
                    command,
                    false,
                    "InvalidArgument",
                    name + " is invalid Base64.");
                return false;
            }
        }

        private static string? ValidateArgumentShape(
            AutomationCommandEnvelope command)
        {
            string[] required;
            string[] optional = Array.Empty<string>();
            switch (command.CommandId)
            {
                case AutomationCommandIds.StartVideoExport:
                    required = new[] { "outputPath", "maximumFrames" };
                    optional = new[] { "ffmpegPath", "replayLoadedMovie", "endMovieFrame", "infoOverlay" };
                    break;
                case AutomationCommandIds.FinishVideoExport:
                case AutomationCommandIds.CancelVideoExport:
                    required = new[] { "operationId" };
                    break;
                case AutomationCommandIds.GetStatus:
                case AutomationCommandIds.GetCapabilities:
                case AutomationCommandIds.GetStartupProfile:
                case AutomationCommandIds.GetCombatState:
                    required = Array.Empty<string>();
                    break;
                case AutomationCommandIds.GetWorldSnapshot:
                    required = Array.Empty<string>();
                    optional = new[] { "snapshotId", "view", "includeInactive", "offset", "limit", "watches" };
                    break;
                case AutomationCommandIds.GetObjectDetails:
                    required = new[] { "objectId" };
                    optional = new[] { "expectedNativeFrame", "detailsId", "cursor", "maxCharacters" };
                    break;
                case AutomationCommandIds.GetDesync:
                case AutomationCommandIds.GetReplaySaves:
                case AutomationCommandIds.GetRestoreStrategy:
                case AutomationCommandIds.ReleaseControl:
                case AutomationCommandIds.Pause:
                case AutomationCommandIds.QuitGame:
                case AutomationCommandIds.Resume:
                case AutomationCommandIds.StartRecording:
                case AutomationCommandIds.StopRecording:
                case AutomationCommandIds.FullRunStatus:
                case AutomationCommandIds.FullRunSnapshot:
                case AutomationCommandIds.FullRunMovie:
                case AutomationCommandIds.StartReplay:
                case AutomationCommandIds.StopReplay:
                case AutomationCommandIds.ResumeReplaySaveRestore:
                    required = Array.Empty<string>();
                    break;
                case AutomationCommandIds.CancelReplaySaveRestore:
                    required = Array.Empty<string>();
                    optional = new[] { "operationId" };
                    break;
                case AutomationCommandIds.GetState:
                    required = Array.Empty<string>();
                    optional = new[] { "statusOnly" };
                    break;
                case AutomationCommandIds.GetMovie:
                    required = Array.Empty<string>();
                    optional = new[] { "includeLifecycle", "exportId", "exportChunk", "listBranches", "branchOffset" };
                    break;
                case AutomationCommandIds.LoadGameSlot:
                case AutomationCommandIds.RestartRecordingSession:
                case AutomationCommandIds.ReloadGameSlot:
                    required = new[] { "slot" };
                    break;
                case AutomationCommandIds.FullRunUpdateMovie:
                    required = new[] { "expectedNativeFrame", "moviePath" };
                    break;
                case AutomationCommandIds.FullRunSeek:
                    required = new[] { "expectedNativeFrame", "targetFrame" };
                    break;
                case AutomationCommandIds.FullRunStop:
                case AutomationCommandIds.FullRunStep:
                case AutomationCommandIds.FullRunPlay:
                case AutomationCommandIds.FullRunPause:
                    required = new[] { "expectedNativeFrame" };
                    break;
                case AutomationCommandIds.BeginFullRunRecording:
                    required = new[] { "expectedNativeFrame", "mouseEnabled" };
                    optional = new[] { "fps" };
                    break;
                case AutomationCommandIds.BeginFullRunReplay:
                    required = new[] { "expectedNativeFrame" };
                    optional = new[] { "movieBase64", "pauseAtFrame" };
                    break;
                case AutomationCommandIds.StudioEditInputs:
                    required = new[] { "operation" };
                    optional = new[] { "startFrame", "endFrame", "count", "minimumTotal", "action", "held", "runs", "fps", "rngSeed" };
                    break;
                case AutomationCommandIds.StudioGetMovie:
                    required = Array.Empty<string>();
                    optional = new[] { "startFrame", "frameCount", "includeFullText" };
                    break;
                case AutomationCommandIds.StudioFrameAction:
                    required = new[] { "targetFrame" };
                    optional = new[] { "action", "expectedNativeFrame" };
                    break;
                case AutomationCommandIds.StudioGetTimeline:
                    required = Array.Empty<string>();
                    break;
                case AutomationCommandIds.StudioSaveTimelineNode:
                    required = Array.Empty<string>();
                    optional = new[] { "frame" };
                    break;
                case AutomationCommandIds.StudioRestoreTimelineNode:
                    required = Array.Empty<string>();
                    optional = new[] { "nodeId", "frame", "worldlineId", "treeId" };
                    break;
                case AutomationCommandIds.StudioSwitchWorldline:
                    required = new[] { "leafId" };
                    optional = new[] { "treeId" };
                    break;
                case AutomationCommandIds.StudioDeleteTimelineNode:
                    required = new[] { "nodeId" };
                    break;
                case AutomationCommandIds.StudioSaveSequence:
                case AutomationCommandIds.StudioOpenSequence:
                    required = new[] { "path" };
                    break;
                case AutomationCommandIds.CancelRecordingRestart:
                    required = new[] { "operationId" };
                    break;
                case AutomationCommandIds.GetTimeline:
                    required = Array.Empty<string>();
                    optional = new[]
                    {
                        "afterSequence",
                        "count",
                        "fromMovieTick"
                    };
                    break;
                case AutomationCommandIds.AcquireControl:
                    required = new[] { "scopes" };
                    optional = new[] { "ttlSeconds" };
                    break;
                case AutomationCommandIds.RenewControl:
                    required = Array.Empty<string>();
                    optional = new[] { "ttlSeconds" };
                    break;
                case AutomationCommandIds.Step:
                    required = new[] { "count" };
                    break;
                case AutomationCommandIds.StepWithInput:
                case AutomationCommandIds.QueueInputBatch:
                    required = new[]
                    {
                        "candidateMovieBase64",
                        "expectedSceneEpoch"
                    };
                    break;
                case AutomationCommandIds.BeginInputBatch:
                    required = new[] { "expectedSceneEpoch" };
                    break;
                case AutomationCommandIds.AppendInputBatch:
                    required = new[]
                    {
                        "transactionId",
                        "chunkIndex",
                        "candidateMovieBase64",
                        "expectedSceneEpoch"
                    };
                    break;
                case AutomationCommandIds.CommitInputBatch:
                case AutomationCommandIds.CancelInputBatch:
                    required = new[]
                    {
                        "transactionId",
                        "expectedSceneEpoch"
                    };
                    break;
                case AutomationCommandIds.RunUntil:
                    required = new[] { "targetMovieTick" };
                    break;
                case AutomationCommandIds.CreateReplaySave:
                    required = new[] { "label" };
                    break;
                case AutomationCommandIds.SetAutoSavePolicy:
                    required = new[] { "enabled", "intervalMovieTicks", "retentionCount" };
                    break;
                case AutomationCommandIds.RestoreReplaySave:
                    required = new[] { "replaySaveId" };
                    break;
                case AutomationCommandIds.SeekMovieTick:
                    required = new[]
                    {
                        "targetMovieTick",
                        "expectedSceneEpoch"
                    };
                    break;
                case AutomationCommandIds.ApproveReplaySaveOverwrite:
                    required = new[] { "approved" };
                    break;
                case AutomationCommandIds.ValidateMoviePatch:
                    required = new[] { "candidateMovieBase64" };
                    break;
                case AutomationCommandIds.ProposeMoviePatch:
                    required = new[]
                    {
                        "baseMovieId",
                        "candidateMovieBase64",
                        "expectedMilestone",
                        "reason"
                    };
                    break;
                case AutomationCommandIds.ApplyMovieBranch:
                    required = new[] { "branchMovieId" };
                    break;
                case AutomationCommandIds.ApplyBranchAndSeek:
                    required = new[]
                    {
                        "branchMovieId",
                        "targetMovieTick",
                        "expectedSceneEpoch"
                    };
                    break;
                case AutomationCommandIds.ReplaceInputRange:
                    optional = new[] { "includeLifecycle" };
                    required = new[]
                    {
                        "baseMovieId",
                        "startTick",
                        "deleteCount",
                        "replacementMovieBase64"
                    };
                    break;
                case AutomationCommandIds.InsertInputRange:
                    optional = new[] { "includeLifecycle" };
                    required = new[]
                    {
                        "baseMovieId",
                        "startTick",
                        "replacementMovieBase64"
                    };
                    break;
                case AutomationCommandIds.DeleteInputRange:
                    optional = new[] { "includeLifecycle" };
                    required = new[]
                    {
                        "baseMovieId",
                        "startTick",
                        "count"
                    };
                    break;
                case AutomationCommandIds.SetHeroPose:
                    required = new[]
                    {
                        "expectedMovieTick",
                        "expectedSnapshotSha256",
                        "positionX",
                        "positionY",
                        "velocityX",
                        "velocityY"
                    };
                    break;
                case AutomationCommandIds.SetPlayerResources:
                    required = new[]
                    {
                        "expectedMovieTick",
                        "expectedSnapshotSha256",
                        "health",
                        "soul"
                    };
                    break;
                default:
                    return "Command is not registered.";
            }

            var allowed = new HashSet<string>(
                required.Concat(optional),
                StringComparer.Ordinal);
            if (command.Arguments.Keys.Any(
                    key => !allowed.Contains(key)))
            {
                return "Arguments contain an unknown field.";
            }

            if (required.Any(
                    key => !command.Arguments.ContainsKey(key)))
            {
                return "Arguments are missing a required field.";
            }

            return null;
        }

        private static string? ValidateArgumentValues(
            AutomationCommandEnvelope command)
        {
            var arguments = command.Arguments;
            switch (command.CommandId)
            {
                case AutomationCommandIds.StartVideoExport:
                    if (arguments.TryGetValue("infoOverlay", out var overlayJson) && !string.IsNullOrEmpty(overlayJson))
                    {
                        if (overlayJson.Length > 65536) return "Video overlay settings are too large.";
                        try
                        {
                            var overlay = System.Text.Json.JsonSerializer.Deserialize<HollowKnightTAS.Core.Media.InfoOverlayVideoSettings>(overlayJson)
                                ?? throw new InvalidDataException("Video overlay settings are missing.");
                            overlay.Validate();
                        }
                        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidDataException or FormatException)
                        { return "Invalid video overlay: " + error.Message; }
                    }
                    if (arguments.TryGetValue("endMovieFrame", out var videoEnd)
                        && !TryInt64(videoEnd, 1, MovieProtocolV2.MaximumExpandedFrames, out _))
                        return "endMovieFrame must be a positive Movie frame within the supported limit.";
                    break;
                case AutomationCommandIds.BeginFullRunRecording:
                case AutomationCommandIds.BeginFullRunReplay:
                case AutomationCommandIds.FullRunStep:
                case AutomationCommandIds.FullRunPlay:
                case AutomationCommandIds.FullRunPause:
                case AutomationCommandIds.FullRunStop:
                    if (!TryInt64(arguments["expectedNativeFrame"], 0,
                            long.MaxValue, out _))
                        return "expectedNativeFrame must be a non-negative Int64.";
                    if (command.CommandId == AutomationCommandIds.BeginFullRunRecording
                        && arguments["mouseEnabled"] != "true"
                        && arguments["mouseEnabled"] != "false")
                        return "mouseEnabled must be true or false.";
                    if (command.CommandId == AutomationCommandIds.BeginFullRunReplay
                        && arguments.TryGetValue("movieBase64", out var replayMovieBase64)
                        && (replayMovieBase64.Length == 0
                            || replayMovieBase64.Length
                                > MovieProtocolV2.MaximumSourceUtf8Bytes * 4 / 3 + 8))
                        return "movieBase64 is empty or too large.";
                    break;
                case AutomationCommandIds.StudioEditInputs:
                    if (arguments["operation"] is not ("paint" or "set_runs" or "insert_frames" or "delete_frames"
                        or "append_blank_frames" or "set_fps" or "set_rng_seed" or "undo" or "redo"))
                        return "operation is invalid.";
                    if (arguments.TryGetValue("startFrame", out var editStart)
                        && !TryInt64(editStart, 0, MovieProtocolV2.MaximumExpandedFrames, out _))
                        return "startFrame is out of bounds.";
                    if (arguments.TryGetValue("endFrame", out var editEnd)
                        && !TryInt64(editEnd, 0, MovieProtocolV2.MaximumExpandedFrames, out _))
                        return "endFrame is out of bounds.";
                    if (arguments.TryGetValue("count", out var editCount)
                        && !TryInt64(editCount, 1, MovieProtocolV2.MaximumExpandedFrames, out _))
                        return "count is out of bounds.";
                    if (arguments.TryGetValue("minimumTotal", out var editMin)
                        && !TryInt64(editMin, 1, MovieProtocolV2.MaximumExpandedFrames, out _))
                        return "minimumTotal is out of bounds.";
                    if (arguments.TryGetValue("held", out var editHeld) && editHeld != "true" && editHeld != "false")
                        return "held must be true or false.";
                    if (arguments.TryGetValue("runs", out var editRuns) && (editRuns.Length == 0 || editRuns.Length > 900000))
                        return "runs is invalid.";
                    if (arguments.TryGetValue("fps", out var editFps) && !MovieFrameRate.TryParse(editFps, out _))
                        return "fps is invalid.";
                    break;
                case AutomationCommandIds.StudioGetMovie:
                    if (arguments.TryGetValue("startFrame", out var movieStart)
                        && !TryInt64(movieStart, 0, MovieProtocolV2.MaximumExpandedFrames, out _))
                        return "startFrame is out of bounds.";
                    if (arguments.TryGetValue("frameCount", out var movieCount)
                        && !TryInt64(movieCount, 1, 10000, out _))
                        return "frameCount must be in [1,10000].";
                    if (arguments.TryGetValue("includeFullText", out var includeFull)
                        && includeFull != "true" && includeFull != "false")
                        return "includeFullText must be true or false.";
                    break;
                case AutomationCommandIds.StudioFrameAction:
                    if (!TryInt64(arguments["targetFrame"], 0, MovieProtocolV2.MaximumExpandedFrames, out _))
                        return "targetFrame is out of bounds.";
                    if (arguments.TryGetValue("action", out var frameAction)
                        && frameAction is not ("auto" or "seek" or "restore" or "rebuild" or "load"))
                        return "action must be auto, seek, restore, rebuild, or load.";
                    if (arguments.TryGetValue("expectedNativeFrame", out var frameExpectedNative)
                        && !TryInt64(frameExpectedNative, 0, long.MaxValue, out _))
                        return "expectedNativeFrame must be a non-negative Int64.";
                    break;
                case AutomationCommandIds.StudioSaveTimelineNode:
                    if (arguments.TryGetValue("frame", out var saveFrame)
                        && saveFrame != "-1"
                        && !TryInt64(saveFrame, 0, MovieProtocolV2.MaximumExpandedFrames, out _))
                        return "frame is out of bounds.";
                    break;
                case AutomationCommandIds.StudioRestoreTimelineNode:
                    if (arguments.TryGetValue("nodeId", out var restoreNodeId) && !TryInt64(restoreNodeId, 0, int.MaxValue, out _))
                        return "nodeId is invalid.";
                    if (arguments.TryGetValue("frame", out var restoreFrame)
                        && !TryInt64(restoreFrame, 0, MovieProtocolV2.MaximumExpandedFrames, out _))
                        return "frame is out of bounds.";
                    if (arguments.TryGetValue("worldlineId", out var restoreLeaf) && !TryInt64(restoreLeaf, 0, int.MaxValue, out _))
                        return "worldlineId is invalid.";
                    if (arguments.TryGetValue("treeId", out var restoreTree) && !IpcIdentifier.IsValid(restoreTree, 64))
                        return "treeId is invalid.";
                    break;
                case AutomationCommandIds.StudioSwitchWorldline:
                    if (!TryInt64(arguments["leafId"], 0, int.MaxValue, out _))
                        return "leafId is invalid.";
                    if (arguments.TryGetValue("treeId", out var switchTree) && !IpcIdentifier.IsValid(switchTree, 64))
                        return "treeId is invalid.";
                    break;
                case AutomationCommandIds.StudioDeleteTimelineNode:
                    if (!TryInt64(arguments["nodeId"], 0, int.MaxValue, out _))
                        return "nodeId is invalid.";
                    break;
                case AutomationCommandIds.StudioSaveSequence:
                case AutomationCommandIds.StudioOpenSequence:
                    if (string.IsNullOrWhiteSpace(arguments["path"])
                        || arguments["path"].Length > 1024
                        || !Path.IsPathFullyQualified(arguments["path"]))
                        return "path must be an absolute path of at most 1024 characters.";
                    break;
                case AutomationCommandIds.GetState:
                    if (arguments.TryGetValue("statusOnly", out var statusOnly) && statusOnly != "true")
                        return "statusOnly must be true when supplied.";
                    break;
                case AutomationCommandIds.GetWorldSnapshot:
                    if (arguments.TryGetValue("snapshotId", out var snapshotId)
                        && snapshotId.Length != 0
                        && !IpcIdentifier.IsValid(snapshotId, 96))
                        return "snapshotId is invalid.";
                    if (arguments.TryGetValue("view", out var view)
                        && view != "world"
                        && view != "all"
                        && view != "colliders"
                        && view != "fsmCatalog"
                        && view != "fsms"
                        && view != "info")
                        return "view must be world, all, colliders, fsmCatalog, fsms, or info.";
                    if (arguments.TryGetValue("watches", out var watches) && watches.Length > 20000)
                        return "watches must be at most 20000 characters.";
                    if (arguments.TryGetValue("includeInactive", out var includeInactive)
                        && includeInactive != "true"
                        && includeInactive != "false")
                        return "includeInactive must be true or false.";
                    if (arguments.TryGetValue("offset", out var offset)
                        && !TryInt64(offset, 0, int.MaxValue, out _))
                        return "offset must be a non-negative Int32.";
                    if (arguments.TryGetValue("limit", out var limit)
                        && !TryInt64(limit, 1, 128, out _))
                        return "limit must be in [1,128].";
                    break;
                case AutomationCommandIds.GetObjectDetails:
                    if (!IpcIdentifier.IsValid(arguments["objectId"], 128))
                        return "objectId is invalid.";
                    if (arguments.TryGetValue("expectedNativeFrame", out var expectedNativeFrame)
                        && !TryInt64(expectedNativeFrame, 0, long.MaxValue, out _))
                        return "expectedNativeFrame must be a non-negative Int64.";
                    if (arguments.TryGetValue("detailsId", out var detailsId)
                        && detailsId.Length != 0
                        && !IpcIdentifier.IsValid(detailsId, 96))
                        return "detailsId is invalid.";
                    if (arguments.TryGetValue("cursor", out var cursor)
                        && !TryInt64(cursor, 0, int.MaxValue, out _))
                        return "cursor must be a non-negative Int32.";
                    if (arguments.TryGetValue("maxCharacters", out var maxCharacters)
                        && !TryInt64(maxCharacters, 1024, 200000, out _))
                        return "maxCharacters must be in [1024,200000].";
                    break;
                case AutomationCommandIds.Step:
                    if (!TryInt64(
                            arguments["count"],
                            1,
                            10000,
                            out _))
                    {
                        return "count must be in [1,10000].";
                    }

                    break;
                case AutomationCommandIds.StepWithInput:
                case AutomationCommandIds.QueueInputBatch:
                    if (arguments["candidateMovieBase64"].Length == 0)
                    {
                        return "candidateMovieBase64 cannot be empty.";
                    }

                    if (!TryInt64(
                            arguments["expectedSceneEpoch"],
                            0,
                            int.MaxValue,
                            out _)
                        || !command.ExpectedMovieTick.HasValue)
                    {
                        return "Frame input requires expectedSceneEpoch and envelope expectedMovieTick.";
                    }

                    break;
                case AutomationCommandIds.BeginInputBatch:
                case AutomationCommandIds.AppendInputBatch:
                case AutomationCommandIds.CommitInputBatch:
                case AutomationCommandIds.CancelInputBatch:
                    if (!TryInt64(
                            arguments["expectedSceneEpoch"],
                            0,
                            int.MaxValue,
                            out _)
                        || !command.ExpectedMovieTick.HasValue)
                    {
                        return "Input-batch transactions require expectedSceneEpoch and envelope expectedMovieTick.";
                    }

                    if (command.CommandId
                        != AutomationCommandIds.BeginInputBatch
                        && !IpcIdentifier.IsValid(
                            arguments["transactionId"],
                            128))
                    {
                        return "transactionId is invalid.";
                    }

                    if (command.CommandId
                        == AutomationCommandIds.AppendInputBatch
                        && (!TryInt64(
                                arguments["chunkIndex"],
                                0,
                                int.MaxValue,
                                out _)
                            || arguments["candidateMovieBase64"].Length
                            == 0))
                    {
                        return "appendInputBatch requires a zero-based chunkIndex and non-empty candidateMovieBase64.";
                    }

                    break;
                case AutomationCommandIds.RunUntil:
                    if (!TryInt64(
                            arguments["targetMovieTick"],
                            0,
                            long.MaxValue,
                            out _))
                    {
                        return "targetMovieTick must be a non-negative Int64.";
                    }

                    break;
                case AutomationCommandIds.CreateReplaySave:
                    if (!IsBoundedLine(
                            arguments["label"],
                            1,
                            128))
                    {
                        return "label must be a non-empty single line of at most 128 characters.";
                    }

                    break;
                case AutomationCommandIds.SetAutoSavePolicy:
                    if ((arguments["enabled"] != "true" && arguments["enabled"] != "false")
                        || !TryInt64(arguments["intervalMovieTicks"], 1,
                            HollowKnightTAS.Core.ReplaySave.AutoSavePolicy.MaximumIntervalMovieTicks, out _)
                        || !TryInt64(arguments["retentionCount"], 1,
                            HollowKnightTAS.Core.ReplaySave.AutoSavePolicy.MaximumRetentionCount, out _))
                        return "Auto-save requires enabled=true/false, intervalMovieTicks in [1,1000000000], retentionCount in [1,1000].";
                    break;
                case AutomationCommandIds.LoadGameSlot:
                    if (!TryInt64(arguments["slot"], 1, 4, out _))
                        return "slot must be in [1,4].";
                    if (command.ExpectedRuntimeMode != "Running")
                        return "loadGameSlot requires expectedRuntimeMode=Running at the title menu.";
                    break;
                case AutomationCommandIds.ReloadGameSlot:
                    if (!TryInt64(arguments["slot"], 1, 4, out _)) return "slot must be in [1,4].";
                    if (command.ExpectedRuntimeMode != "Paused" || !command.ExpectedMovieTick.HasValue)
                        return "reloadGameSlot requires expectedRuntimeMode=Paused and an expected movie tick.";
                    break;
                case AutomationCommandIds.RestartRecordingSession:
                    if (!TryInt64(arguments["slot"], 1, 4, out _)) return "slot must be in [1,4].";
                    break;
                case AutomationCommandIds.CancelRecordingRestart:
                    if (!IpcIdentifier.IsValid(arguments["operationId"], 96)) return "operationId is invalid.";
                    break;
                case AutomationCommandIds.RestoreReplaySave:
                    if (!IpcIdentifier.IsValid(
                            arguments["replaySaveId"],
                            128))
                    {
                        return "replaySaveId is invalid.";
                    }

                    break;
                case AutomationCommandIds.SeekMovieTick:
                    if (!TryInt64(
                            arguments["targetMovieTick"],
                            0,
                            long.MaxValue,
                            out _)
                        || !TryInt64(
                            arguments["expectedSceneEpoch"],
                            0,
                            int.MaxValue,
                            out _)
                        || !command.ExpectedMovieTick.HasValue)
                    {
                        return "seekMovieTick requires non-negative target/tick/scene CAS values.";
                    }

                    break;
                case AutomationCommandIds.ApproveReplaySaveOverwrite:
                    if (!string.Equals(
                            arguments["approved"],
                            "true",
                            StringComparison.Ordinal)
                        && !string.Equals(
                            arguments["approved"],
                            "false",
                            StringComparison.Ordinal))
                    {
                        return "approved must be true or false.";
                    }

                    break;
                case AutomationCommandIds.ValidateMoviePatch:
                    if (arguments["candidateMovieBase64"].Length
                        == 0)
                    {
                        return "candidateMovieBase64 cannot be empty.";
                    }

                    break;
                case AutomationCommandIds.ProposeMoviePatch:
                    if (arguments["baseMovieId"] != "none"
                        && !IsLowerSha256(
                            arguments["baseMovieId"]))
                    {
                        return "baseMovieId must be none or lowercase SHA-256.";
                    }

                    if (!IsBoundedLine(
                            arguments["reason"],
                            1,
                            512)
                        || !IsBoundedLine(
                            arguments["expectedMilestone"],
                            1,
                            256))
                    {
                        return "reason and expectedMilestone must be bounded single lines.";
                    }

                    break;
                case AutomationCommandIds.ApplyMovieBranch:
                    if (!IsLowerSha256(
                            arguments["branchMovieId"]))
                    {
                        return "branchMovieId must be lowercase SHA-256.";
                    }

                    break;
                case AutomationCommandIds.ApplyBranchAndSeek:
                    if (!IsLowerSha256(arguments["branchMovieId"])
                        || !TryInt64(
                            arguments["targetMovieTick"],
                            0,
                            long.MaxValue,
                            out _)
                        || !TryInt64(
                            arguments["expectedSceneEpoch"],
                            0,
                            int.MaxValue,
                            out _)
                        || !command.ExpectedMovieTick.HasValue)
                    {
                        return "applyBranchAndSeek requires a branch ID and non-negative target/tick/scene CAS values.";
                    }

                    break;
                case AutomationCommandIds.ReplaceInputRange:
                case AutomationCommandIds.InsertInputRange:
                case AutomationCommandIds.DeleteInputRange:
                    if (!IsLowerSha256(arguments["baseMovieId"])
                        || !TryInt64(
                            arguments["startTick"],
                            0,
                            long.MaxValue,
                            out _))
                    {
                        return "Timeline edits require a lowercase baseMovieId and non-negative startTick.";
                    }

                    if (command.CommandId
                        == AutomationCommandIds.ReplaceInputRange
                        && (!TryInt64(
                                arguments["deleteCount"],
                                0,
                                long.MaxValue,
                                out _)
                            || arguments["replacementMovieBase64"].Length
                            == 0))
                    {
                        return "replaceInputRange requires non-negative deleteCount and a replacement movie.";
                    }

                    if (command.CommandId
                        == AutomationCommandIds.InsertInputRange
                        && arguments["replacementMovieBase64"].Length
                        == 0)
                    {
                        return "insertInputRange requires a replacement movie.";
                    }

                    if (command.CommandId
                        == AutomationCommandIds.DeleteInputRange
                        && !TryInt64(
                            arguments["count"],
                            1,
                            long.MaxValue,
                            out _))
                    {
                        return "deleteInputRange count must be positive.";
                    }

                    break;
                case AutomationCommandIds.SetHeroPose:
                    if (!ValidateMutationBinding(
                            command,
                            arguments,
                            out var bindingError))
                    {
                        return bindingError;
                    }

                    foreach (var name in new[]
                             {
                                 "positionX",
                                 "positionY",
                                 "velocityX",
                                 "velocityY"
                             })
                    {
                        if (!float.TryParse(
                                arguments[name],
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out var value)
                            || float.IsNaN(value)
                            || float.IsInfinity(value))
                        {
                            return name + " must be a finite float32 value.";
                        }

                        var maximum =
                            name.StartsWith(
                                "position",
                                StringComparison.Ordinal)
                                ? StateMutationBounds
                                    .MaximumAbsolutePosition
                                : StateMutationBounds
                                    .MaximumAbsoluteVelocity;
                        if (Math.Abs(value) > maximum)
                        {
                            return name + " exceeds protocol v1 bounds.";
                        }
                    }

                    break;
                case AutomationCommandIds.SetPlayerResources:
                    if (!ValidateMutationBinding(
                            command,
                            arguments,
                            out var resourcesBindingError))
                    {
                        return resourcesBindingError;
                    }

                    if (!int.TryParse(
                            arguments["health"],
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var health)
                        || health < 1
                        || !int.TryParse(
                            arguments["soul"],
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var soul)
                        || soul < 0)
                    {
                        return "health and soul must be non-negative bounded Int32 values.";
                    }

                    break;
            }

            return null;
        }

        private static bool ValidateMutationBinding(
            AutomationCommandEnvelope command,
            IReadOnlyDictionary<string, string> arguments,
            out string error)
        {
            error = string.Empty;
            if (!IsLowerSha256(
                    arguments["expectedSnapshotSha256"]))
            {
                error =
                    "expectedSnapshotSha256 must be lowercase SHA-256.";
                return false;
            }

            if (!long.TryParse(
                    arguments["expectedMovieTick"],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var argumentTick)
                || argumentTick < 0
                || !command.ExpectedMovieTick.HasValue
                || command.ExpectedMovieTick.Value != argumentTick)
            {
                error =
                    "Mutation argument/envelope movie ticks must match.";
                return false;
            }

            return true;
        }

        private static bool TryInt64(
            string text,
            long minimum,
            long maximum,
            out long value)
        {
            return long.TryParse(
                       text,
                       NumberStyles.None,
                       CultureInfo.InvariantCulture,
                       out value)
                   && value >= minimum
                   && value <= maximum;
        }

        private static bool IsBoundedLine(
            string value,
            int minimumLength,
            int maximumLength)
        {
            return value != null
                   && value.Length >= minimumLength
                   && value.Length <= maximumLength
                   && value.IndexOfAny(
                       new[] { '\r', '\n', '\0' }) < 0;
        }

        private static bool IsLowerSha256(string value)
        {
            return value != null
                   && value.Length == 64
                   && value.All(
                       character =>
                           character >= '0'
                           && character <= '9'
                           || character >= 'a'
                           && character <= 'f');
        }

        private static TimeSpan ParseTtl(
            AutomationCommandEnvelope command)
        {
            if (!command.Arguments.TryGetValue(
                    "ttlSeconds",
                    out var text))
            {
                return ControlLeaseManager.DefaultTtl;
            }

            if (!int.TryParse(
                    text,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var seconds)
                || seconds < 1
                || seconds > 300)
            {
                throw new InvalidOperationException(
                    "ttlSeconds must be in [1,300].");
            }

            return TimeSpan.FromSeconds(seconds);
        }

        private AutomationResultEnvelope Result(
            AutomationCommandEnvelope command,
            bool success,
            string code,
            string detail,
            IReadOnlyDictionary<string, string>? data = null)
        {
            return new AutomationResultEnvelope(
                command.RequestId,
                success,
                SafeCode(code),
                Sanitize(detail),
                command.SessionId,
                command.ManifestSha256,
                CurrentMovieTick(),
                IpcPayloadCodec.Serialize(
                    data
                    ?? new Dictionary<string, string>(
                        StringComparer.Ordinal)));
        }

        private AutomationResultEnvelope UnboundResult(string requestId)
        {
            var zero =
                "0000000000000000000000000000000000000000000000000000000000000000";
            return new AutomationResultEnvelope(
                requestId,
                false,
                "NoSession",
                "No eligible Runtime session is connected.",
                "unbound",
                zero,
                -1,
                IpcPayloadCodec.Serialize(
                    new Dictionary<string, string>(
                        StringComparer.Ordinal)));
        }

        private void Audit(
            AutomationCommandEnvelope command,
            AutomationResultEnvelope result,
            string sideEffect)
        {
            var sink = audit;
            if (sink == null)
            {
                return;
            }

            sink.Write(
                command.RequestId,
                command.ClientId,
                command.RequestId,
                command.IdempotencyKey,
                command.SessionId,
                command.ManifestSha256,
                command.CommandId,
                command.RequiredScope,
                command.LeaseId,
                command.ExpectedMovieTick ?? -1,
                result.AcceptedAtMovieTick,
                CurrentMovieTick(),
                result.ResultCode,
                sideEffect,
                Sha256Utility.ComputeHex(result.ToPayload()));
        }

        private long CurrentMovieTick()
        {
            if (fullRunBootstrap != null && fullRunMovies?.IsPending == true)
                return fullRunMovies.Gate?.NativeCompletedFrames ?? -1;
            lock (sync)
            {
                if (latest.TryGetValue(
                        IpcMessageTypes.RuntimeStatus,
                        out var status)
                    && status.Fields.TryGetValue(
                        "movieTick",
                        out var value)
                    && long.TryParse(
                        value,
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture,
                        out var tick))
                {
                    return tick;
                }
            }

            return -1;
        }

        private RuntimeSessionClient? GetBoundSession()
        {
            lock (sync)
            {
                return boundSession != null && (boundSession.IsConnected || coldRestoreSupervisor?.IsActive == true
                    || coldRestoreSupervisor?.LatestRecordingRestart != null || coldRestoreSupervisor?.LatestSnapshot != null || LatestSlotRecovery != null)
                    ? boundSession
                    : null;
            }
        }

        private AutomationBootstrapDescriptor? GetFullRunBinding()
        {
            lock (sync)
                return fullRunMovies?.IsPending == true ? fullRunBootstrap : null;
        }

        private void OnEnvelopeReceived(
            object? sender,
            SessionEnvelopeEventArgs eventArgs)
        {
            // Restorable chunks are private to the checkpoint client, not general automation events.
            if (eventArgs.Envelope.MessageType == IpcMessageTypes.SceneCheckpointData) return;
            RuntimeSessionClient? current;
            lock (sync)
            {
                current = boundSession;
            }

            if (current != eventArgs.Session)
            {
                return;
            }

            var fields = Decode(eventArgs.Envelope);
            var cached = new CachedEnvelope(
                fields,
                DateTimeOffset.UtcNow);
            lock (sync)
            {
                latest[eventArgs.Envelope.MessageType] = cached;
                if (IsTimelineEvent(eventArgs.Envelope.MessageType))
                {
                    var item = new TimelineItem(
                        eventArgs.Envelope.Sequence,
                        eventArgs.Envelope.MessageType,
                        ReadMovieTick(fields),
                        eventArgs.Envelope.PayloadUtf8);
                    timeline.Enqueue(item);
                    timelineRetainedPayloadBytes +=
                        item.PayloadUtf8.LongLength;
                    while (timeline.Count > TimelineCapacity
                           || timelineRetainedPayloadBytes
                           > TimelinePayloadByteCapacity)
                    {
                        var evicted = timeline.Dequeue();
                        timelineRetainedPayloadBytes -=
                            evicted.PayloadUtf8.LongLength;
                        lastEvictedTimelineItem =
                            new TimelineCursor(
                                evicted.Sequence,
                                evicted.MovieTick);
                    }
                }

                if (fields.TryGetValue(
                        "requestId",
                        out var requestId)
                    && pending.TryGetValue(
                        requestId,
                        out var waiter)
                    && (eventArgs.Envelope.MessageType
                        == waiter.ExpectedMessageType
                        || eventArgs.Envelope.MessageType
                        == IpcMessageTypes.CommandRejected))
                {
                    waiter.Completion.TrySetResult(
                        eventArgs.Envelope);
                }
            }
        }

        private void OnSessionsChanged(
            object? sender,
            EventArgs eventArgs)
        {
            RefreshBinding();
        }

        private void OnColdRestoreActivityChanged(object? sender, EventArgs args)
        {
            if (coldRestoreSupervisor?.IsActive != true) RefreshBinding();
        }

        private void RefreshBinding()
        {
            lock (bindingSync)
            {
                if (disposed) return;
                RefreshBindingCore();
            }
        }

        private void RefreshBindingCore()
        {
            // Keep the authenticated source endpoint alive through handoff.
            // RouteAsync rejects gameplay commands while this binding is held.
            if (coldRestoreSupervisor?.IsActive == true) return;
            var candidate = sessions.Sessions
                .Where(session => session.IsConnected)
                .OrderByDescending(
                    session =>
                        session.GameProcessStartTimeUtcTicks)
                .ThenBy(
                    session => session.SessionId,
                    StringComparer.Ordinal)
                .FirstOrDefault();
            if (fullRunBootstrap != null && fullRunMovies?.IsPending == true)
            {
                RuntimeSessionClient? prior;
                lock (sync)
                {
                    prior = boundSession;
                    boundSession = candidate;
                }
                if (candidate != null && !ReferenceEquals(prior, candidate))
                    _ = PrimeRuntimeStreamsAsync(candidate);
                return;
            }
            RuntimeSessionClient? previous;
            lock (sync)
            {
                previous = boundSession;
                // Preserve the authenticated read-only status endpoint when a
                // failed/cancelled restart leaves no target Runtime to bind.
                if (candidate == null && previous != null
                    && (coldRestoreSupervisor?.LatestRecordingRestart != null || coldRestoreSupervisor?.LatestSnapshot != null || LatestSlotRecovery != null))
                    return;
                if (ReferenceEquals(previous, candidate))
                {
                    return;
                }

                boundSession = candidate;
                latest.Clear();
                timeline.Clear();
                lastEvictedTimelineItem = null;
                timelineRetainedPayloadBytes = 0;
                currentMovieId = string.Empty;
                currentMovieBytes = null;
                currentLifecycleSource = null;
                currentLifecycleExport = null;
                idempotency.Clear();
                idempotencyOrder.Clear();
                expiredIdempotency.Clear();
                idempotencyRetainedBytes = 0;
                foreach (var waiter in pending.Values)
                {
                    waiter.Completion.TrySetException(
                        new InvalidOperationException(
                            "Runtime session changed."));
                }

                pending.Clear();
            }

            leases.Revoke();
            inputBatches.Clear();
            DeleteBootstrap();
            if (candidate == null
                || candidate.AutomationMode
                   == AutomationMode.Disabled)
            {
                lock (sync)
                {
                    bootstrap = null;
                    capabilityCatalog = null;
                    audit = null;
                    movieWorkspace = null;
                }

                return;
            }

            var suffix = Sha256Utility.ComputeUtf8Hex(
                    Environment.UserDomainName
                    + "\\"
                    + Environment.UserName + isolatedPipeNamespace)
                .Substring(0, 16);
            var pipeName = "HollowKnightTAS.Automation."
                           + suffix;
            var descriptor = new AutomationBootstrapDescriptor(
                pipeName,
                candidate.SessionId,
                candidate.EnvironmentManifestSha256,
                candidate.AutomationMode,
                brokerToken);
            var evidenceRoot = System.IO.Path.Combine(
                automationRoot,
                "artifacts");
            var newAudit = new AutomationAuditSink(
                evidenceRoot,
                candidate.SessionId,
                brokerToken);
            var workspace = new MoviePatchWorkspace(
                evidenceRoot,
                candidate.SessionId,
                Path.Combine(automationRoot, "movie-library"));
            var server = pipeServer ?? new AutomationPipeServer(
                pipeName,
                this,
                new AutomationSessionAuthenticator(brokerToken));
            WriteBootstrap(descriptor);
            lock (sync)
            {
                bootstrap = descriptor;
                capabilityCatalog =
                    new AutomationCapabilityCatalog(
                        candidate.AutomationMode,
                        candidate.DebugMutationEnabled);
                audit = newAudit;
                movieWorkspace = workspace;
                pipeServer = server;
            }

            server.Start();
            _ = PrimeRuntimeStreamsAsync(candidate);
        }

        private async Task PrimeRuntimeStreamsAsync(
            RuntimeSessionClient session)
        {
            try
            {
                if (fullRunMovies?.IsPending == true)
                {
                    var requestId = "automation-full-run-prime-"
                        + Guid.NewGuid().ToString("N");
                    await SendRuntimeAsync(session, IpcMessageTypes.FullRunStatus,
                        Fields("requestId", requestId), requestId,
                        IpcMessageTypes.FullRunState, shutdown.Token);
                    return;
                }
                foreach (var stream in new[] { "watch", "ledger" })
                {
                    var requestId = "automation-subscribe-"
                                    + stream
                                    + "-"
                                    + Guid.NewGuid().ToString("N");
                    await SendRuntimeAsync(
                        session,
                        IpcMessageTypes.Subscribe,
                        Fields(
                            "requestId",
                            requestId,
                            "stream",
                            stream),
                        requestId,
                        IpcMessageTypes.CommandAccepted,
                        shutdown.Token);
                }

                var snapshotId = "automation-prime-"
                                 + Guid.NewGuid().ToString("N");
                await SendRuntimeAsync(
                    session,
                    IpcMessageTypes.RequestSnapshot,
                    Fields("requestId", snapshotId),
                    snapshotId,
                    IpcMessageTypes.RuntimeStatus,
                    shutdown.Token);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    "AutomationPrimeFailed:"
                    + exception.GetType().Name
                    + ":"
                    + Sanitize(exception.Message));
            }
        }

        private void WriteBootstrap(
            AutomationBootstrapDescriptor descriptor)
        {
            var temporary = System.IO.Path.Combine(
                automationRoot,
                ".bootstrap-"
                + Guid.NewGuid().ToString("N"));
            try
            {
                var bytes = descriptor.ToBytes();
                using (var stream = new System.IO.FileStream(
                           temporary,
                           System.IO.FileMode.CreateNew,
                           System.IO.FileAccess.Write,
                           System.IO.FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                if (System.IO.File.Exists(BootstrapPath))
                {
                    System.IO.File.Replace(
                        temporary,
                        BootstrapPath,
                        null);
                }
                else
                {
                    System.IO.File.Move(
                        temporary,
                        BootstrapPath);
                }

                RestrictBootstrapAcl(BootstrapPath);
            }
            finally
            {
                if (System.IO.File.Exists(temporary))
                {
                    System.IO.File.Delete(temporary);
                }
            }
        }

        private void DeleteBootstrap()
        {
            try
            {
                if (System.IO.File.Exists(BootstrapPath))
                {
                    System.IO.File.Delete(BootstrapPath);
                }
            }
            catch (System.IO.IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void RestrictBootstrapAcl(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "Automation bootstrap ACL requires Windows.");
            }

            var identity = WindowsIdentity.GetCurrent();
            var user = identity.User
                       ?? throw new InvalidOperationException(
                           "Current Windows SID is unavailable.");
            var security = new FileSecurity();
            security.SetOwner(user);
            security.SetAccessRuleProtection(
                isProtected: true,
                preserveInheritance: false);
            security.AddAccessRule(
                new FileSystemAccessRule(
                    user,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
            new System.IO.FileInfo(path).SetAccessControl(security);
        }

        private static IReadOnlyDictionary<string, string> Decode(
            IpcEnvelope envelope)
        {
            var decoded = IpcPayloadCodec.TryDeserialize(
                envelope.PayloadUtf8);
            if (!decoded.Success || decoded.Fields == null)
            {
                throw new InvalidOperationException(
                    "Runtime sent an invalid canonical payload.");
            }

            return decoded.Fields;
        }

        private static long ReadMovieTick(
            IReadOnlyDictionary<string, string> fields)
        {
            return fields.TryGetValue(
                       "movieTick",
                       out var value)
                   && long.TryParse(
                       value,
                       NumberStyles.AllowLeadingSign,
                       CultureInfo.InvariantCulture,
                       out var tick)
                ? tick
                : -1;
        }

        private static bool IsTimelineEvent(string messageType)
        {
            return messageType == IpcMessageTypes.TickLedger
                   || messageType == IpcMessageTypes.WatchFrame
                   || messageType == IpcMessageTypes.Milestone
                   || messageType == IpcMessageTypes.Desync
                   || messageType
                   == IpcMessageTypes.RuntimeModeChanged
                   || messageType
                    == IpcMessageTypes
                        .ReplaySaveRestoreProgress
                   || messageType
                    == IpcMessageTypes.MovieSeekProgress
                   || messageType
                   == IpcMessageTypes.StateMutationResult;
        }

        private static bool ShouldRetainIdempotencyResult(
            string commandId)
        {
            switch (commandId)
            {
                case AutomationCommandIds.GetStatus:
                case AutomationCommandIds.GetCapabilities:
                case AutomationCommandIds.GetStartupProfile:
                case AutomationCommandIds.GetState:
                case AutomationCommandIds.GetCombatState:
                case AutomationCommandIds.GetWorldSnapshot:
                case AutomationCommandIds.GetObjectDetails:
                case AutomationCommandIds.GetTimeline:
                case AutomationCommandIds.GetDesync:
                case AutomationCommandIds.GetReplaySaves:
                case AutomationCommandIds.GetMovie:
                case AutomationCommandIds.GetRestoreStrategy:
                case AutomationCommandIds.FullRunStatus:
                case AutomationCommandIds.FullRunSnapshot:
                case AutomationCommandIds.FullRunMovie:
                case AutomationCommandIds.ValidateMoviePatch:
                    return false;
                default:
                    return true;
            }
        }

        private static IReadOnlyDictionary<string, string> Fields(
            params string[] values)
        {
            if (values.Length % 2 != 0)
            {
                throw new ArgumentException(
                    "Field values must be key/value pairs.");
            }

            var result = new Dictionary<string, string>(
                StringComparer.Ordinal);
            for (var index = 0; index < values.Length; index += 2)
            {
                result.Add(values[index], values[index + 1]);
            }

            return result;
        }

        private static string SafeCode(string value)
        {
            if (IpcIdentifier.IsValid(value, 64))
            {
                return value;
            }

            return "RuntimeRejected";
        }

        private static string Sanitize(string value)
        {
            var result = (value ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\\', '/');
            return result.Substring(0, Math.Min(512, result.Length));
        }

        private sealed class PendingRuntimeRequest
        {
            public PendingRuntimeRequest(string expectedMessageType)
            {
                ExpectedMessageType = expectedMessageType;
                Completion =
                    new TaskCompletionSource<IpcEnvelope>(
                        TaskCreationOptions
                            .RunContinuationsAsynchronously);
            }

            public string ExpectedMessageType { get; }
            public TaskCompletionSource<IpcEnvelope> Completion { get; }
        }

        private sealed class CachedResult
        {
            public CachedResult(
                string requestSha256,
                AutomationResultEnvelope result,
                long retainedBytes)
            {
                RequestSha256 = requestSha256;
                Result = result;
                RetainedBytes = retainedBytes;
            }

            public string RequestSha256 { get; }
            public AutomationResultEnvelope Result { get; }
            public long RetainedBytes { get; }
        }

        private sealed class CachedEnvelope
        {
            public CachedEnvelope(
                IReadOnlyDictionary<string, string> fields,
                DateTimeOffset receivedAtUtc)
            {
                Fields = fields;
                ReceivedAtUtc = receivedAtUtc;
            }

            public IReadOnlyDictionary<string, string> Fields { get; }
            public DateTimeOffset ReceivedAtUtc { get; }
        }

        private sealed class TimelineItem
        {
            public TimelineItem(
                long sequence,
                string messageType,
                long movieTick,
                byte[] payloadUtf8)
            {
                Sequence = sequence;
                MessageType = messageType;
                MovieTick = movieTick;
                PayloadUtf8 = payloadUtf8;
            }

            public long Sequence { get; }
            public string MessageType { get; }
            public long MovieTick { get; }
            public byte[] PayloadUtf8 { get; }
        }

        private sealed class TimelineCursor
        {
            public TimelineCursor(long sequence, long movieTick)
            {
                Sequence = sequence;
                MovieTick = movieTick;
            }

            public long Sequence { get; }
            public long MovieTick { get; }
        }
    }
}
