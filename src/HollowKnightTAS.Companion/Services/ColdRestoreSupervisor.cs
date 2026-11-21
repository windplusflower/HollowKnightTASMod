using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Ipc;
using HollowKnightTAS.Core.ReplaySave;

namespace HollowKnightTAS.Companion.Services
{
    public sealed class ColdRestoreCommandRejectedException : InvalidOperationException
    {
        public ColdRestoreCommandRejectedException(string code, string detail) : base(detail)
        { ErrorCode = code; }
        public string ErrorCode { get; }
    }

    public sealed class ColdRestoreStartResult
    {
        public ColdRestoreStartResult(
            string operationId,
            string intentSha256,
            ColdRestoreOperationSnapshot snapshot)
        {
            OperationId = operationId;
            IntentSha256 = intentSha256;
            Snapshot = snapshot;
        }

        public string OperationId { get; }
        public string IntentSha256 { get; }
        public ColdRestoreOperationSnapshot Snapshot { get; }
    }

    public sealed class ColdRestoreOperationChangedEventArgs : EventArgs
    {
        public ColdRestoreOperationChangedEventArgs(
            ColdRestoreOperationSnapshot snapshot)
        {
            Snapshot = snapshot;
        }

        public ColdRestoreOperationSnapshot Snapshot { get; }
    }

    public sealed class SlotRecoveryNotice
    {
        public SlotRecoveryNotice(string operationId, string status, string detail)
        { OperationId = operationId; Status = status; Detail = detail; }
        public string OperationId { get; }
        public string Status { get; }
        public string Detail { get; }
    }

    public sealed class ColdRestoreSupervisor : IDisposable
    {
        private static readonly TimeSpan CommandTimeout =
            TimeSpan.FromSeconds(15);
        private static readonly TimeSpan SourceExitTimeout =
            TimeSpan.FromSeconds(30);
        private static readonly TimeSpan LaunchTimeout =
            TimeSpan.FromSeconds(45);
        private static readonly TimeSpan SessionAttachTimeout =
            TimeSpan.FromSeconds(60);
        private static readonly TimeSpan RestoreBoundaryTimeout =
            TimeSpan.FromMinutes(5);

        private readonly object sync = new object();
        private readonly SessionRegistry sessions;
        private readonly ColdRestoreIntentStore store;
        private readonly ColdRestoreSupervisorIdentity identity;
        private readonly IColdRestoreLaunchEnvironmentResolver
            environmentResolver;
        private readonly IColdRestoreProcessMonitor processMonitor;
        private readonly CancellationTokenSource shutdown =
            new CancellationTokenSource();
        private readonly SemaphoreSlim startGate =
            new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim sessionChanges =
            new SemaphoreSlim(0, int.MaxValue);
        private readonly List<EnvelopeWaiter> envelopeWaiters =
            new List<EnvelopeWaiter>();
        private readonly int companionProcessId;
        private readonly DateTimeOffset companionProcessStartedAtUtc;
        private ColdRestoreOperationContext? active;
        private RecordingSessionRestartCoordinator? recordingRestart;
        private ColdRestoreOperationSnapshot? latestSnapshot;
        private bool disposed;
        public SlotRecoveryNotice? LatestSlotRecovery { get; private set; }
        public event EventHandler? SlotRecoveryChanged;
        private void SetSlotRecovery(string operationId, string status, string detail)
        {
            LatestSlotRecovery = new SlotRecoveryNotice(operationId, status, detail);
            SlotRecoveryChanged?.Invoke(this, EventArgs.Empty);
        }

        public ColdRestoreSupervisor(
            SessionRegistry sessions,
            ColdRestoreIntentStore store,
            ColdRestoreSupervisorIdentity identity,
            IColdRestoreLaunchEnvironmentResolver environmentResolver,
            IColdRestoreProcessMonitor? processMonitor = null)
        {
            this.sessions = sessions
                            ?? throw new ArgumentNullException(
                                nameof(sessions));
            this.store = store
                         ?? throw new ArgumentNullException(nameof(store));
            this.identity = identity
                            ?? throw new ArgumentNullException(
                                nameof(identity));
            this.environmentResolver = environmentResolver
                                       ?? throw new ArgumentNullException(
                                           nameof(environmentResolver));
            this.processMonitor = processMonitor
                                  ?? new ExactColdRestoreProcessMonitor();
            if (!string.Equals(
                    sessions.CompanionInstanceId,
                    identity.CompanionInstanceId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "SessionRegistry and cold-restore identity do not match.");
            }

            using (var process = Process.GetCurrentProcess())
            {
                companionProcessId = process.Id;
                companionProcessStartedAtUtc = new DateTimeOffset(
                    process.StartTime.ToUniversalTime());
            }

            sessions.EnvelopeReceived += OnEnvelopeReceived;
            sessions.SessionsChanged += OnSessionsChanged;
        }

        public event EventHandler<ColdRestoreOperationChangedEventArgs>?
            OperationChanged;
        public event EventHandler? ActivityChanged;

        public bool IsActive
        {
            get
            {
                lock (sync)
                {
                    return active != null || recordingRestart?.IsActive == true;
                }
            }
        }

        public string ActiveOperationId
        {
            get
            {
                lock (sync)
                {
                    return active?.OperationId ?? (recordingRestart?.IsActive == true ? recordingRestart.Latest?.OperationId : null) ?? string.Empty;
                }
            }
        }

        public ColdRestoreOperationSnapshot? LatestSnapshot
        {
            get
            {
                lock (sync)
                {
                    return latestSnapshot;
                }
            }
        }

        public RecordingRestartSnapshot? LatestRecordingRestart => recordingRestart?.Latest;
        public string ActiveRestoreOperationId { get { lock (sync) return active?.OperationId ?? string.Empty; } }

        public string BeginRecordingRestart(RuntimeSessionClient source, int slot)
        {
            if (!startGate.Wait(0)) throw new InvalidOperationException("Session operation preparation is busy.");
            try
            {
                lock (sync)
                {
                    ThrowIfDisposed();
                    if (active != null || recordingRestart?.IsActive == true)
                        throw new InvalidOperationException("A session operation is already active.");
                    var coordinator = new RecordingSessionRestartCoordinator(
                        new RuntimeRecordingRestartHost(source, sessions, environmentResolver), shutdown.Token);
                    recordingRestart = coordinator;
                    var id = coordinator.Begin(slot);
                    ActivityChanged?.Invoke(this, EventArgs.Empty);
                    _ = ObserveRecordingRestartAsync(coordinator, id);
                    return id;
                }
            }
            finally { startGate.Release(); }
        }

        private async Task ObserveRecordingRestartAsync(RecordingSessionRestartCoordinator coordinator, string id)
        {
            await coordinator.WaitAsync(id);
            ActivityChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task<RecordingRestartSnapshot> CancelRecordingRestartAsync(string id)
            => (recordingRestart ?? throw new InvalidOperationException("No recording restart exists.")).CancelAsync(id);

        public async Task<ColdRestoreStartResult>
            BeginReplaySaveRestoreAsync(
                RuntimeSessionClient sourceSession,
                string replaySaveId,
                string requesterSurface,
                CancellationToken cancellationToken)
        {
            return await BeginColdOperationAsync(
                sourceSession,
                ColdRestoreOperationKind.RestoreReplaySave,
                replaySaveId,
                -1,
                -1,
                -1,
                requesterSurface,
                cancellationToken);
        }

        public async Task<ColdRestoreStartResult> BeginMovieSeekAsync(
            RuntimeSessionClient sourceSession,
            ColdRestoreOperationKind operationKind,
            long targetMovieTick,
            long expectedMovieTick,
            int expectedSceneEpoch,
            string requesterSurface,
            CancellationToken cancellationToken, string lifecyclePlanHash = "")
        {
            if (operationKind != ColdRestoreOperationKind.SeekMovieTick
                && operationKind
                   != ColdRestoreOperationKind.ApplyBranchAndSeek)
            {
                throw new ArgumentOutOfRangeException(nameof(operationKind));
            }

            return await BeginColdOperationAsync(
                sourceSession,
                operationKind,
                string.Empty,
                targetMovieTick,
                expectedMovieTick,
                expectedSceneEpoch,
                requesterSurface,
                cancellationToken, lifecyclePlanHash);
        }

        private async Task<ColdRestoreStartResult>
            BeginColdOperationAsync(
                RuntimeSessionClient sourceSession,
                ColdRestoreOperationKind operationKind,
                string replaySaveId,
                long targetMovieTick,
                long expectedMovieTick,
                int expectedSceneEpoch,
                string requesterSurface,
                CancellationToken cancellationToken, string lifecyclePlanHash = "")
        {
            if (sourceSession == null)
            {
                throw new ArgumentNullException(nameof(sourceSession));
            }
            if (lifecyclePlanHash.Length != 0 && (operationKind != ColdRestoreOperationKind.ApplyBranchAndSeek
                || !HollowKnightTAS.Core.Movie.MovieProtocolV1.IsLowerSha256(lifecyclePlanHash)))
                throw new ArgumentException("Invalid lifecycle plan binding.", nameof(lifecyclePlanHash));

            if (operationKind
                == ColdRestoreOperationKind.RestoreReplaySave)
            {
                replaySaveId = ColdRestoreIntent.RequireIdentifier(
                    replaySaveId,
                    nameof(replaySaveId));
                if (targetMovieTick != -1)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(targetMovieTick));
                }

                if (expectedMovieTick != -1 || expectedSceneEpoch != -1)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(expectedMovieTick));
                }
            }
            else if ((operationKind
                      != ColdRestoreOperationKind.SeekMovieTick
                      && operationKind
                         != ColdRestoreOperationKind.ApplyBranchAndSeek)
                     || !string.IsNullOrEmpty(replaySaveId)
                     || targetMovieTick < 0
                     || expectedMovieTick < 0
                     || expectedSceneEpoch < 0)
            {
                throw new ArgumentException(
                    "Cold movie seek operation binding is invalid.");
            }

            requesterSurface = ColdRestoreIntent.RequireIdentifier(
                requesterSurface,
                nameof(requesterSurface));
            ThrowIfDisposed();
            await startGate.WaitAsync(cancellationToken);
            ColdRestoreOperationContext? context = null;
            try
            {
                ThrowIfDisposed();
                lock (sync)
                {
                    if (active != null || recordingRestart?.IsActive == true)
                    {
                        throw new InvalidOperationException(
                            "A cold-restore operation is already active: "
                            + ActiveOperationId
                            + ".");
                    }
                }

                if (!sourceSession.IsConnected
                    || !sessions.Sessions.Contains(sourceSession))
                {
                    throw new InvalidOperationException(
                        "The selected source Runtime session is not active.");
                }

                var environment = environmentResolver.Resolve(sourceSession);
                var operationId = "cold-restore-"
                                  + Guid.NewGuid().ToString("N");
                context = new ColdRestoreOperationContext(
                    operationId,
                    "intent-" + Guid.NewGuid().ToString("N"),
                    "claim-" + Guid.NewGuid().ToString("N"),
                    operationKind,
                    replaySaveId,
                    targetMovieTick,
                    expectedMovieTick,
                    expectedSceneEpoch,
                    requesterSurface,
                    sourceSession,
                    environment);
                context.LifecyclePlanHash = lifecyclePlanHash;
                SetActive(context);

                context.SourceStartupAttestation =
                    await RequireStartupProfileAsync(
                        sourceSession,
                        expectedRunId: null,
                        requireRecordingRoot: operationKind != ColdRestoreOperationKind.RestoreReplaySave,
                        cancellationToken);
                context.Environment.SourceLaunchVerifier.RequireTrusted(
                    sourceSession,
                    context.SourceStartupAttestation);

                var prepared = await PrepareSourceAsync(
                    context,
                    cancellationToken);
                context.Intent = prepared.Intent;
                context.IntentSha256 = prepared.IntentSha256;
                context.Snapshot = prepared.Snapshot;
                Publish(prepared.Snapshot);
                context.OperationCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                context.RunTask = ContinueAsync(context, context.OperationCancellation.Token);
                return new ColdRestoreStartResult(
                    operationId,
                    prepared.IntentSha256,
                    prepared.Snapshot);
            }
            catch
            {
                if (context != null)
                {
                    await TryCancelPreparedSourceAsync(context);
                    ClearActive(context);
                }

                throw;
            }
            finally
            {
                startGate.Release();
            }
        }

        public async Task<ColdRestoreOperationSnapshot> CancelAsync(string operationId)
        {
            ColdRestoreOperationContext context;
            Task runTask;
            lock (sync)
            {
                ThrowIfDisposed();
                context = active ?? throw new InvalidOperationException("No cold restore is active.");
                if (!string.Equals(context.OperationId, operationId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Cold-restore operation binding is stale.");
                if (context.Snapshot == null || context.RunTask == null || context.OperationCancellation == null)
                    throw new InvalidOperationException("Cold restore has not finished preparing its operation.");
                if (!ColdRestoreOperationStateMachine.CanTransition(
                        context.Snapshot.Latest.State, ColdRestoreOperationState.Cancelled))
                    throw new InvalidOperationException("The reached restore target must not be cancelled.");
                runTask = context.RunTask;
                context.UserCancellationRequested = true;
                context.OperationCancellation.Cancel();
            }
            await runTask;
            return context.Snapshot ?? throw new InvalidOperationException("Cold-restore result is unavailable.");
        }

        public void Dispose()
        {
            ColdRestoreOperationContext? current;
            List<EnvelopeWaiter> waiters;
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                current = active;
                waiters = envelopeWaiters.ToList();
                envelopeWaiters.Clear();
            }

            sessions.EnvelopeReceived -= OnEnvelopeReceived;
            sessions.SessionsChanged -= OnSessionsChanged;
            shutdown.Cancel();
            foreach (var waiter in waiters)
            {
                waiter.Cancel(shutdown.Token);
            }

            try
            {
                current?.RunTask?.Wait(TimeSpan.FromSeconds(3));
            }
            catch (AggregateException)
            {
            }

            current?.LaunchHandle?.Dispose();
            startGate.Dispose();
            sessionChanges.Dispose();
            shutdown.Dispose();
        }

        private async Task<PreparedSource> PrepareSourceAsync(
            ColdRestoreOperationContext context,
            CancellationToken cancellationToken)
        {
            var requestId = NewRequestId("prepare");
            var envelope = await SendAndWaitForEventAsync(
                context.SourceSession,
                IpcMessageTypes.PrepareColdRestore,
                Fields(
                    "companionAssemblySha256",
                    context.Environment.CompanionAssemblySha256,
                    "expectedMovieTick",
                    context.ExpectedMovieTick.ToString(
                        CultureInfo.InvariantCulture),
                    "expectedSceneEpoch",
                    context.ExpectedSceneEpoch.ToString(
                        CultureInfo.InvariantCulture),
                    "intentId",
                    context.IntentId,
                    "observerAssemblySha256",
                    context.Environment.ObserverAssemblySha256,
                    "operationKind",
                    context.OperationKind.ToString(),
                    "operationId",
                    context.OperationId,
                    "replaySaveId",
                    context.ReplaySaveId,
                    "requesterSurface",
                    context.RequesterSurface,
                    "requestId",
                    requestId,
                    "startupProfileSha256",
                    context.Environment.StartupProfileSha256,
                    "targetMovieTick",
                    context.TargetMovieTick.ToString(
                        CultureInfo.InvariantCulture),
                    "lifecyclePlanObjectSha256", context.LifecyclePlanHash),
                requestId,
                value => IsOperationEvent(
                    value,
                    context.SourceSession,
                    IpcMessageTypes.ColdRestoreIntentPrepared,
                    context.OperationId),
                CommandTimeout,
                cancellationToken);
            var fields = Decode(envelope);
            RequireExactFields(
                fields,
                "intentBase64",
                "intentSha256",
                "operationId",
                "sourceMovieTick",
                "sourceSceneEpoch");
            byte[] intentBytes;
            try
            {
                intentBytes = Convert.FromBase64String(fields["intentBase64"]);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException(
                    "Runtime returned invalid cold-restore intent base64.",
                    exception);
            }

            if (!string.Equals(
                    Convert.ToBase64String(intentBytes),
                    fields["intentBase64"],
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Runtime returned non-canonical cold-restore intent base64.");
            }

            var intentSha256 = Sha256Utility.ComputeHex(intentBytes);
            if (!string.Equals(
                    intentSha256,
                    fields["intentSha256"],
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Runtime cold-restore intent hash does not match its bytes.");
            }

            var intent = ColdRestoreIntentCodec.Deserialize(intentBytes);
            ValidatePreparedIntent(context, intent);
            context.Intent = intent;
            context.IntentSha256 = intentSha256;
            var nowUtc = DateTimeOffset.UtcNow;
            var claimExpiresAtUtc = nowUtc.AddMinutes(5);
            if (claimExpiresAtUtc > intent.ExpiresAtUtc)
            {
                claimExpiresAtUtc = intent.ExpiresAtUtc;
            }

            if (claimExpiresAtUtc <= nowUtc)
            {
                throw new InvalidOperationException(
                    "Cold-restore intent expired before it could be persisted.");
            }

            var snapshot = store.Prepare(
                intent,
                context.ClaimId,
                nowUtc,
                claimExpiresAtUtc);
            return new PreparedSource(intent, intentSha256, snapshot);
        }

        private async Task TryCancelPreparedSourceAsync(
            ColdRestoreOperationContext context)
        {
            if (string.IsNullOrEmpty(context.IntentSha256)
                || context.Snapshot != null
                || !context.SourceSession.IsConnected)
            {
                return;
            }

            try
            {
                var requestId = NewRequestId("cancel-source");
                await SendAndWaitAcceptedAsync(
                    context.SourceSession,
                    IpcMessageTypes.CancelColdRestoreSource,
                    ClaimFields(context, requestId),
                    requestId,
                    CommandTimeout,
                    shutdown.Token);
            }
            catch
            {
                // The original preparation failure remains authoritative.
                // Cancellation is best-effort and never retries or launches.
            }
        }

        private async Task ContinueAsync(
            ColdRestoreOperationContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                await QuiesceSourceAsync(context, cancellationToken);
                await ExitSourceAsync(context, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Transition(
                    context,
                    ColdRestoreOperationState.Launching,
                    ColdRestoreActorRole.Companion,
                    identity.CompanionInstanceId,
                    string.Empty,
                    companionProcessId,
                    companionProcessStartedAtUtc,
                    "launching-once");
                context.LaunchHandle = await context.Environment.Launcher
                    .LaunchAsync(
                        RequireIntent(context),
                        LaunchTimeout,
                        cancellationToken);
                ValidateLaunchIdentity(context);
                context.TargetSession = await WaitForTargetSessionAsync(
                    context,
                    cancellationToken);
                ValidateTargetSession(context);
                context.TargetStartupAttestation =
                    await RequireStartupProfileAsync(
                        context.TargetSession,
                        context.OperationId,
                        requireRecordingRoot: false,
                        cancellationToken);
                Transition(
                    context,
                    ColdRestoreOperationState.NewSessionAttached,
                    ColdRestoreActorRole.Companion,
                    identity.CompanionInstanceId,
                    context.TargetSession.SessionId,
                    context.LaunchHandle.ProcessId,
                    context.LaunchHandle.ProcessStartedAtUtc,
                    "new-session-attached");
                await ClaimAndReplayAsync(context, cancellationToken);
            }
            catch (OperationCanceledException) when (context.UserCancellationRequested)
            {
                try
                {
                    var state = context.Snapshot?.Latest.State;
                    if (state == ColdRestoreOperationState.SourceQuiesced)
                    {
                        // Quiescing transfers source ownership and cannot be undone
                        // as an ordinary gameplay resume. Finish its native exit,
                        // but never launch a replacement for a cancelled operation.
                        await ExitSourceAsync(context, shutdown.Token);
                    }
                    else if (state == ColdRestoreOperationState.Prepared)
                    {
                        var requestId = NewRequestId("cancel-source");
                        await SendAndWaitAcceptedAsync(context.SourceSession,
                            IpcMessageTypes.CancelColdRestoreSource, ClaimFields(context, requestId),
                            requestId, CommandTimeout, shutdown.Token);
                    }
                    // Dispose only the exact launch handle owned by this operation.
                    // No user save objects or unrelated process are removed.
                    await TerminateAndRecoverSlotsAsync(context);
                    Transition(context, ColdRestoreOperationState.Cancelled,
                        ColdRestoreActorRole.Companion, identity.CompanionInstanceId,
                        context.SourceSession.SessionId, companionProcessId,
                        companionProcessStartedAtUtc, "cancelled-by-user");
                    ClearActive(context);
                }
                catch (Exception exception) { await FailAsync(context, exception); }
            }
            catch (Exception exception)
            {
                await FailAsync(context, exception);
            }
            finally
            {
                lock (sync) { context.OperationCancellation?.Dispose(); }
            }
        }

        private async Task QuiesceSourceAsync(
            ColdRestoreOperationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestId = NewRequestId("quiesce");
            var envelope = await SendAndWaitForEventAsync(
                context.SourceSession,
                IpcMessageTypes.QuiesceColdRestoreSource,
                ClaimFields(context, requestId),
                requestId,
                value => IsOperationEvent(
                    value,
                    context.SourceSession,
                    IpcMessageTypes.ColdRestoreSourceQuiesced,
                    context.OperationId),
                CommandTimeout,
                shutdown.Token);
            var fields = Decode(envelope);
            RequireExactFields(
                fields,
                "claimId",
                "intentSha256",
                "operationId",
                "sourceSessionId");
            RequireEqual(fields, "claimId", context.ClaimId);
            RequireEqual(fields, "intentSha256", context.IntentSha256);
            RequireEqual(fields, "operationId", context.OperationId);
            RequireEqual(
                fields,
                "sourceSessionId",
                context.SourceSession.SessionId);
            Transition(
                context,
                ColdRestoreOperationState.SourceQuiesced,
                ColdRestoreActorRole.Runtime,
                context.SourceSession.SessionId,
                context.SourceSession.SessionId,
                context.SourceSession.GameProcessId,
                SourceStartedAtUtc(context),
                "source-quiesced");
        }

        private async Task ExitSourceAsync(
            ColdRestoreOperationContext context,
            CancellationToken cancellationToken)
        {
            // Once exit is committed, finish its acknowledgement/identity wait
            // even if the user cancels. Otherwise a disconnected source could
            // be mistaken for a still-quiesced source requiring an unfreeze.
            lock (sync) { cancellationToken.ThrowIfCancellationRequested(); }
            var requestId = NewRequestId("exit-source");
            await SendAndWaitAcceptedAsync(
                context.SourceSession,
                IpcMessageTypes.ExitColdRestoreSource,
                ClaimFields(context, requestId),
                requestId,
                CommandTimeout,
                shutdown.Token);
            await processMonitor.WaitForExitAsync(
                context.SourceSession.GameProcessId,
                SourceStartedAtUtc(context),
                SourceExitTimeout,
                shutdown.Token);
            Transition(
                context,
                ColdRestoreOperationState.SourceExited,
                ColdRestoreActorRole.Companion,
                identity.CompanionInstanceId,
                context.SourceSession.SessionId,
                context.SourceSession.GameProcessId,
                SourceStartedAtUtc(context),
                "source-exited");
        }

        private async Task ClaimAndReplayAsync(
            ColdRestoreOperationContext context,
            CancellationToken cancellationToken)
        {
            var target = context.TargetSession
                         ?? throw new InvalidOperationException(
                             "Cold-restore target session is unavailable.");
            var snapshot = RequireSnapshot(context);
            using (var stage = CancellationTokenSource
                       .CreateLinkedTokenSource(cancellationToken))
            using (var completionWait = CancellationTokenSource
                       .CreateLinkedTokenSource(cancellationToken))
            {
                stage.CancelAfter(RestoreBoundaryTimeout);
                var baselineTask = WaitForColdProgressAsync(
                    context,
                    "BaselineReady",
                    stage.Token);
                var replayingTask = WaitForColdProgressAsync(
                    context,
                    "ReplayingPrefix",
                    stage.Token);
                var targetTask = WaitForColdProgressAsync(
                    context,
                    "PausedAtTarget",
                    stage.Token);
                var completedTask = WaitForColdProgressAsync(
                    context,
                    "Completed",
                    completionWait.Token);
                try
                {
                    var beginRequestId = NewRequestId("begin-cold");
                    await SendAndWaitAcceptedAsync(
                        target,
                        IpcMessageTypes.BeginColdRestore,
                        Fields(
                            "claimId",
                            context.ClaimId,
                            "companionInstanceId",
                            identity.CompanionInstanceId,
                            "intentBase64",
                            Convert.ToBase64String(
                                ColdRestoreIntentCodec.Serialize(
                                    RequireIntent(context))),
                            "intentSha256",
                            context.IntentSha256,
                            "requestId",
                            beginRequestId),
                        beginRequestId,
                        CommandTimeout,
                        cancellationToken);
                    context.Snapshot = store.Claim(
                        context.OperationId,
                        snapshot.Latest.Sequence,
                        snapshot.Claim.ClaimId,
                        snapshot.Claim.MacSha256,
                        context.Environment.BuildFingerprint(target),
                        DateTimeOffset.UtcNow,
                        target.SessionId,
                        target.SessionId,
                        target.GameProcessId,
                        TargetStartedAtUtc(context));
                    Publish(context.Snapshot);

                    await baselineTask;
                    context.TargetStartupAttestation =
                        await RequireStartupProfileAsync(
                            target,
                            context.OperationId,
                            requireRecordingRoot: true,
                            cancellationToken);
                    TransitionRuntime(
                        context,
                        ColdRestoreOperationState.BaselineReady,
                        "baseline-ready");
                    var releaseRequestId = NewRequestId("release-baseline");
                    await SendAndWaitAcceptedAsync(
                        target,
                        IpcMessageTypes.ReleaseColdRestoreBaseline,
                        Fields(
                            "operationId",
                            context.OperationId,
                            "requestId",
                            releaseRequestId),
                        releaseRequestId,
                        CommandTimeout,
                        cancellationToken);
                    await replayingTask;
                    TransitionRuntime(
                        context,
                        ColdRestoreOperationState.ReplayingPrefix,
                        "replaying-prefix");
                    await targetTask;
                    lock (sync)
                    {
                        // Either cancellation wins while replay is in flight,
                        // or the reached target becomes protected from cancel.
                        cancellationToken.ThrowIfCancellationRequested();
                        TransitionRuntime(context, ColdRestoreOperationState.PausedAtTarget,
                            "paused-at-target");
                    }

                    // This command finalizes ownership transfer while the same
                    // completed-frame guard remains closed. It does not issue
                    // unified-control Resume and therefore advances zero ticks.
                    stage.CancelAfter(Timeout.InfiniteTimeSpan);
                    var handoffRequestId = NewRequestId("paused-handoff");
                    await SendAndWaitAcceptedAsync(
                        target,
                        IpcMessageTypes.ResumeReplaySaveRestore,
                        Fields("requestId", handoffRequestId),
                        handoffRequestId,
                        CommandTimeout,
                        cancellationToken);
                    await completedTask;
                    Transition(
                        context,
                        ColdRestoreOperationState.Completed,
                        ColdRestoreActorRole.Companion,
                        identity.CompanionInstanceId,
                        target.SessionId,
                        target.GameProcessId,
                        TargetStartedAtUtc(context),
                        "completed");
                    context.LaunchHandle?.ReleaseSupervision();
                    context.LaunchHandle?.Dispose();
                    context.LaunchHandle = null;
                    ClearActive(context);
                }
                finally
                {
                    stage.Cancel();
                    completionWait.Cancel();
                }
            }
        }

        private async Task<IpcEnvelope> WaitForColdProgressAsync(
            ColdRestoreOperationContext context,
            string desiredPhase,
            CancellationToken cancellationToken)
        {
            var target = context.TargetSession
                         ?? throw new InvalidOperationException(
                             "Cold-restore target session is unavailable.");
            var eventArgs = await WaitEnvelopeAsync(
                value =>
                {
                    if (!ReferenceEquals(value.Session, target))
                    {
                        return false;
                    }

                    if (string.Equals(
                            value.Envelope.MessageType,
                            IpcMessageTypes.Fault,
                            StringComparison.Ordinal))
                    {
                        return true;
                    }

                    if (!string.Equals(
                            value.Envelope.MessageType,
                            IpcMessageTypes.ReplaySaveRestoreProgress,
                            StringComparison.Ordinal))
                    {
                        return false;
                    }

                    var fields = TryDecode(value.Envelope);
                    return fields != null
                           && fields.TryGetValue(
                               "operationId",
                               out var operationId)
                           && string.Equals(
                               operationId,
                               context.OperationId,
                               StringComparison.Ordinal)
                           && fields.TryGetValue("phase", out var phase)
                           && (string.Equals(
                                   phase,
                                   desiredPhase,
                                   StringComparison.Ordinal)
                               || string.Equals(
                                   phase,
                                   "Failed",
                                   StringComparison.Ordinal)
                               || string.Equals(
                                   phase,
                                   "Cancelled",
                                   StringComparison.Ordinal));
                },
                cancellationToken);
            if (string.Equals(
                    eventArgs.Envelope.MessageType,
                    IpcMessageTypes.Fault,
                    StringComparison.Ordinal))
            {
                var fault = Decode(eventArgs.Envelope);
                throw new InvalidOperationException(
                    fault.TryGetValue("detail", out var detail)
                        ? detail
                        : "Cold Runtime reported an unbound fault.");
            }

            var progress = Decode(eventArgs.Envelope);
            RequireEqual(progress, "operationId", context.OperationId);
            RequireEqual(progress, "claimId", context.ClaimId);
            RequireEqual(
                progress,
                "intentSha256",
                context.IntentSha256);
            RequireEqual(
                progress,
                "restoreStrategy",
                "VanillaEquivalentColdReplay");
            RequireEqual(
                progress,
                "equivalenceClass",
                "VanillaEquivalent");
            if (!progress.TryGetValue("phase", out var phase)
                || string.Equals(phase, "Failed", StringComparison.Ordinal)
                || string.Equals(
                    phase,
                    "Cancelled",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    progress.TryGetValue("detail", out var detail)
                        ? detail
                        : "Cold restore terminated before "
                          + desiredPhase
                          + ".");
            }

            RequireEqual(progress, "phase", desiredPhase);
            if (string.Equals(
                    desiredPhase,
                    "PausedAtTarget",
                    StringComparison.Ordinal))
            {
                var intent = RequireIntent(context);
                RequireEqual(
                    progress,
                    "targetMovieTick",
                    intent.TargetMovieTick.ToString(
                        CultureInfo.InvariantCulture));
                if (intent.RequiresExactTargetSemantic)
                {
                    RequireEqual(
                        progress,
                        "targetVerification",
                        ReplayRestoreTargetVerification
                            .ExactSavedSemantic.ToString());
                    RequireEqual(
                        progress,
                        "expectedSemanticSha256",
                        intent.TargetSemanticSha256);
                    RequireEqual(
                        progress,
                        "actualSemanticSha256",
                        intent.TargetSemanticSha256);
                    RequireEqual(
                        progress,
                        "strictSemanticEquivalent",
                        "true");
                }
                else
                {
                    RequireEqual(
                        progress,
                        "targetVerification",
                        ReplayRestoreTargetVerification
                            .ReconstructedObservation.ToString());
                    RequireEqual(
                        progress,
                        "expectedSemanticSha256",
                        string.Empty);
                    RequireEqual(
                        progress,
                        "expectedVerificationSha256",
                        string.Empty);
                    RequireEqual(
                        progress,
                        "strictSemanticEquivalent",
                        string.Empty);
                    RequireSha256Field(
                        progress,
                        "actualSemanticSha256");
                    RequireSha256Field(
                        progress,
                        "actualVerificationSha256");
                }
            }

            return eventArgs.Envelope;
        }

        private void ValidatePreparedIntent(
            ColdRestoreOperationContext context,
            ColdRestoreIntent intent)
        {
            if (!string.Equals(
                    intent.IntentId,
                    context.IntentId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    intent.OperationId,
                    context.OperationId,
                    StringComparison.Ordinal)
                || intent.OperationKind
                   != context.OperationKind
                || intent.LifecyclePlanObjectSha256 != context.LifecyclePlanHash
                || context.OperationKind
                   == ColdRestoreOperationKind.RestoreReplaySave
                   && !string.Equals(
                       intent.ReplaySaveId,
                       context.ReplaySaveId,
                       StringComparison.Ordinal)
                || context.OperationKind
                   != ColdRestoreOperationKind.RestoreReplaySave
                   && intent.TargetMovieTick != context.TargetMovieTick
                || !string.Equals(
                    intent.RequesterSurface,
                    context.RequesterSurface,
                    StringComparison.Ordinal)
                || !string.Equals(
                    intent.SourceSessionId,
                    context.SourceSession.SessionId,
                    StringComparison.Ordinal)
                || intent.SourceProcessId
                   != context.SourceSession.GameProcessId
                || intent.SourceProcessStartedAtUtc.UtcTicks
                   != context.SourceSession.GameProcessStartTimeUtcTicks)
            {
                throw new InvalidDataException(
                    "Runtime cold-restore intent source binding is invalid.");
            }

            var expectedBuild = context.Environment.BuildFingerprint(
                context.SourceSession);
            if (!intent.IsMenuSource && context.SourceStartupAttestation?.RootStatus
                != StartupRecordingRootStatus.Verified)
                throw new InvalidDataException("A gameplay source still requires its verified recording root.");
            var validation = ColdRestoreIntentValidator.ValidateForClaim(
                intent,
                expectedBuild,
                DateTimeOffset.UtcNow);
            if (!validation.Success)
            {
                if (validation.Code == ColdRestoreIntentValidationCode.BuildMismatch)
                {
                    Trace.TraceWarning(
                        $"ColdRestoreSupervisor.ValidatePreparedIntent: {validation.Detail}; continuing.");
                }
                else
                {
                    throw new InvalidDataException(validation.Detail);
                }
            }
        }

        private void ValidateLaunchIdentity(
            ColdRestoreOperationContext context)
        {
            var launch = context.LaunchHandle
                         ?? throw new InvalidOperationException(
                             "Verified launcher returned no process handle.");
            if (launch.HasExited
                || launch.ProcessId == context.SourceSession.GameProcessId
                   && launch.ProcessStartedAtUtc.UtcTicks
                   == context.SourceSession.GameProcessStartTimeUtcTicks)
            {
                throw new InvalidOperationException(
                    "Verified launcher did not create a fresh live process.");
            }
        }

        private void ValidateTargetSession(
            ColdRestoreOperationContext context)
        {
            var target = context.TargetSession
                         ?? throw new InvalidOperationException(
                             "Cold target Runtime session is unavailable.");
            var launch = context.LaunchHandle
                         ?? throw new InvalidOperationException(
                             "Cold target launch handle is unavailable.");
            if (!target.IsConnected
                || target.GameProcessId != launch.ProcessId
                || target.GameProcessStartTimeUtcTicks
                   != launch.ProcessStartedAtUtc.UtcTicks
                || string.Equals(
                    target.SessionId,
                    context.SourceSession.SessionId,
                    StringComparison.Ordinal)
                || target.NativeCapabilitiesRequested
                   != context.SourceSession.NativeCapabilitiesRequested
                || target.AutomationMode
                   != context.SourceSession.AutomationMode
                || target.DebugMutationEnabled
                   != context.SourceSession.DebugMutationEnabled)
            {
                throw new InvalidDataException(
                    "Fresh Runtime registration does not preserve the source control/build profile.");
            }

            if (!string.Equals(
                    target.CoreAssemblySha256,
                    context.SourceSession.CoreAssemblySha256,
                    StringComparison.Ordinal))
            {
                Trace.TraceWarning(
                    $"ColdRestoreSupervisor.ValidateTargetSession: CoreAssemblySha256 differs (target={target.CoreAssemblySha256}, source={context.SourceSession.CoreAssemblySha256}); continuing.");
            }

            var actualBuild = context.Environment.BuildFingerprint(target);
            var validation = ColdRestoreIntentValidator.ValidateForClaim(
                RequireIntent(context),
                actualBuild,
                DateTimeOffset.UtcNow);
            if (!validation.Success)
            {
                if (validation.Code == ColdRestoreIntentValidationCode.BuildMismatch)
                {
                    Trace.TraceWarning(
                        $"ColdRestoreSupervisor.ValidateTargetSession: {validation.Detail}; continuing.");
                }
                else
                {
                    throw new InvalidDataException(validation.Detail);
                }
            }
        }

        private async Task<RuntimeSessionClient> WaitForTargetSessionAsync(
            ColdRestoreOperationContext context,
            CancellationToken cancellationToken)
        {
            var launch = context.LaunchHandle
                         ?? throw new InvalidOperationException(
                             "Cold target launch handle is unavailable.");
            var deadline = DateTimeOffset.UtcNow + SessionAttachTimeout;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var matches = sessions.Sessions.Where(
                        value => value.IsConnected
                                 && value.GameProcessId == launch.ProcessId
                                 && value.GameProcessStartTimeUtcTicks
                                 == launch.ProcessStartedAtUtc.UtcTicks)
                    .ToArray();
                if (matches.Length == 1)
                {
                    return matches[0];
                }

                if (matches.Length > 1)
                {
                    throw new InvalidDataException(
                        "Multiple Runtime sessions claimed the verified child process.");
                }

                if (launch.HasExited)
                {
                    throw new InvalidOperationException(
                        "Verified Hollow Knight exited before Runtime registration.");
                }

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero
                    || !await sessionChanges.WaitAsync(
                        remaining,
                        cancellationToken))
                {
                    throw new TimeoutException(
                        "Fresh Runtime did not register before the cold-restore deadline.");
                }
            }
        }

        private async Task<IpcEnvelope> SendAndWaitForEventAsync(
            RuntimeSessionClient session,
            string messageType,
            IReadOnlyDictionary<string, string> fields,
            string requestId,
            Func<SessionEnvelopeEventArgs, bool> eventPredicate,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using (var stage = CancellationTokenSource
                       .CreateLinkedTokenSource(cancellationToken))
            {
                stage.CancelAfter(timeout);
                var acknowledgement = WaitEnvelopeAsync(
                    value => IsCommandResult(
                        value,
                        session,
                        requestId),
                    stage.Token);
                var expectedEvent = WaitEnvelopeAsync(
                    eventPredicate,
                    stage.Token);
                try
                {
                    await session.SendCommandAsync(
                        messageType,
                        fields,
                        stage.Token);
                    RequireAccepted((await acknowledgement).Envelope);
                    return (await expectedEvent).Envelope;
                }
                finally
                {
                    stage.Cancel();
                }
            }
        }

        private async Task<StartupProfileAttestation>
            RequireStartupProfileAsync(
                RuntimeSessionClient session,
                string? expectedRunId,
                bool requireRecordingRoot,
                CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow + CommandTimeout;
            StartupProfileAttestation? last = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var requestId = NewRequestId("startup-attestation");
                var envelope = await SendAndWaitForEventAsync(
                    session,
                    IpcMessageTypes.RequestStartupProfileAttestation,
                    Fields("requestId", requestId),
                    requestId,
                    value => IsRequestEvent(
                        value,
                        session,
                        IpcMessageTypes.StartupProfileAttestation,
                        requestId),
                    CommandTimeout,
                    cancellationToken);
                var fields = Decode(envelope);
                if (!StartupProfileAttestation.TryParse(
                        fields,
                        out var parsed,
                        out var error)
                    || parsed == null)
                {
                    throw new InvalidDataException(error);
                }

                last = parsed;
                if (parsed.ProcessId != session.GameProcessId
                    || parsed.ProcessStartTimeUtcTicks
                    != session.GameProcessStartTimeUtcTicks)
                {
                    throw new InvalidDataException(
                        "Startup attestation process binding does not match the Runtime session.");
                }
                if (!string.Equals(
                        parsed.Profile,
                        StartupProfileContract.ProfileId,
                        StringComparison.Ordinal)
                    && parsed.Status
                    != StartupProfileAttestationStatus.Pending)
                {
                    throw new InvalidDataException(
                        "Startup attestation profile is not the frozen cold-restore profile.");
                }
                if (!string.IsNullOrEmpty(expectedRunId)
                    && !string.Equals(
                        parsed.RunId,
                        expectedRunId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Startup attestation run ID does not match the cold operation.");
                }
                if (parsed.Status
                    == StartupProfileAttestationStatus.Verified
                    && (!requireRecordingRoot
                        || parsed.RootStatus
                        == StartupRecordingRootStatus.Verified))
                {
                    return parsed;
                }
                if (parsed.Status
                        == StartupProfileAttestationStatus.Unverified
                    || parsed.Status
                        == StartupProfileAttestationStatus.Faulted
                    || parsed.RootStatus
                        == StartupRecordingRootStatus.Faulted)
                {
                    throw new InvalidOperationException(
                        "Startup profile is not eligible for vanilla-equivalent cold restore: "
                        + parsed.Status
                        + "/"
                        + parsed.RootStatus
                        + ".");
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(100),
                    cancellationToken);
            }

            throw new TimeoutException(
                "Startup profile did not become eligible before the cold-restore deadline: "
                + (last == null
                    ? "no-attestation"
                    : last.Status + "/" + last.RootStatus)
                + ".");
        }

        private async Task SendAndWaitAcceptedAsync(
            RuntimeSessionClient session,
            string messageType,
            IReadOnlyDictionary<string, string> fields,
            string requestId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using (var stage = CancellationTokenSource
                       .CreateLinkedTokenSource(cancellationToken))
            {
                stage.CancelAfter(timeout);
                var acknowledgement = WaitEnvelopeAsync(
                    value => IsCommandResult(
                        value,
                        session,
                        requestId),
                    stage.Token);
                try
                {
                    await session.SendCommandAsync(
                        messageType,
                        fields,
                        stage.Token);
                    RequireAccepted((await acknowledgement).Envelope);
                }
                finally
                {
                    stage.Cancel();
                }
            }
        }

        private Task<SessionEnvelopeEventArgs> WaitEnvelopeAsync(
            Func<SessionEnvelopeEventArgs, bool> predicate,
            CancellationToken cancellationToken)
        {
            var waiter = new EnvelopeWaiter(predicate);
            lock (sync)
            {
                ThrowIfDisposed();
                cancellationToken.ThrowIfCancellationRequested();
                envelopeWaiters.Add(waiter);
            }

            waiter.RegisterCancellation(
                cancellationToken,
                () =>
                {
                    lock (sync)
                    {
                        envelopeWaiters.Remove(waiter);
                    }

                    waiter.Cancel(cancellationToken);
                });
            return waiter.Completion.Task;
        }

        private void OnEnvelopeReceived(
            object? sender,
            SessionEnvelopeEventArgs eventArgs)
        {
            EnvelopeWaiter[] candidates;
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                candidates = envelopeWaiters.ToArray();
            }

            foreach (var waiter in candidates)
            {
                bool matches;
                try
                {
                    matches = waiter.Predicate(eventArgs);
                }
                catch
                {
                    matches = false;
                }

                if (!matches)
                {
                    continue;
                }

                lock (sync)
                {
                    if (!envelopeWaiters.Remove(waiter))
                    {
                        continue;
                    }
                }

                waiter.Complete(eventArgs);
            }
        }

        private void OnSessionsChanged(object? sender, EventArgs eventArgs)
        {
            try
            {
                sessionChanges.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }

        private void TransitionRuntime(
            ColdRestoreOperationContext context,
            ColdRestoreOperationState next,
            string detailCode)
        {
            var target = context.TargetSession
                         ?? throw new InvalidOperationException(
                             "Cold target Runtime session is unavailable.");
            Transition(
                context,
                next,
                ColdRestoreActorRole.Runtime,
                target.SessionId,
                target.SessionId,
                target.GameProcessId,
                TargetStartedAtUtc(context),
                detailCode);
        }

        private void Transition(
            ColdRestoreOperationContext context,
            ColdRestoreOperationState next,
            ColdRestoreActorRole actorRole,
            string actorInstanceId,
            string actorSessionId,
            int processId,
            DateTimeOffset processStartedAtUtc,
            string detailCode)
        {
            var snapshot = RequireSnapshot(context);
            context.Snapshot = store.Transition(
                context.OperationId,
                snapshot.Latest.Sequence,
                next,
                DateTimeOffset.UtcNow,
                actorRole,
                actorInstanceId,
                actorSessionId,
                processId,
                processStartedAtUtc,
                detailCode);
            Publish(context.Snapshot);
        }

        private async Task TerminateAndRecoverSlotsAsync(ColdRestoreOperationContext context)
        {
            var handle = context.LaunchHandle;
            if (handle == null) return;
            var pid = handle.ProcessId;
            var started = handle.ProcessStartedAtUtc;
            SetSlotRecovery(context.OperationId, "WaitingForExit", "Waiting for the target process to exit before slot recovery.");
            try
            {
            // Hold the launch lock until recovery ends so another verified
            // launch cannot read files halfway through restoration.
            using (GameLaunchGate.Acquire())
            {
                handle.Dispose();
                context.LaunchHandle = null;
                await processMonitor.WaitForExitAsync(pid, started, SourceExitTimeout, CancellationToken.None);
                var saveRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData", "LocalLow", "Team Cherry", "Hollow Knight");
                var recovery = new SlotRecoveryStore(Path.Combine(saveRoot, "HollowKnightTAS", "replay-saves", "v1", "slot-recovery"));
                if (recovery.ReadPending(context.OperationId).Count != 0)
                    recovery.RecoverAfterExit(context.OperationId, pid, started, saveRoot, () =>
                {
                    var games = Process.GetProcessesByName("hollow_knight");
                    try { return games.Length == 0; }
                    finally { foreach (var game in games) game.Dispose(); }
                });
            }
                SetSlotRecovery(context.OperationId, "Complete", "Target exited; pending slot recovery completed.");
            }
            catch (Exception exception)
            {
                SetSlotRecovery(context.OperationId, "Pending", exception.Message);
                throw;
            }
        }

        private async Task FailAsync(
            ColdRestoreOperationContext context,
            Exception exception)
        {
            // Persist the actual error BEFORE publishing Failed or disposing
            // the launch handle (which can terminate Runtime immediately).
            try { store.PersistFailureDiagnostic(context.OperationId, exception); }
            catch { /* Diagnostics must not suppress the authoritative failure. */ }
            try
            {
                var snapshot = context.Snapshot;
                if (snapshot != null
                    && !ColdRestoreOperationStateMachine.IsTerminal(
                        snapshot.Latest.State))
                {
                    var processId = context.TargetSession?.GameProcessId
                                    ?? context.LaunchHandle?.ProcessId
                                    ?? companionProcessId;
                    var processStartedAtUtc = context.TargetSession != null
                        ? TargetStartedAtUtc(context)
                        : context.LaunchHandle?.ProcessStartedAtUtc
                          ?? companionProcessStartedAtUtc;
                    context.Snapshot = store.Transition(
                        context.OperationId,
                        snapshot.Latest.Sequence,
                        ColdRestoreOperationState.Failed,
                        DateTimeOffset.UtcNow,
                        ColdRestoreActorRole.Companion,
                        identity.CompanionInstanceId,
                        context.TargetSession?.SessionId
                        ?? context.SourceSession.SessionId,
                        processId,
                        processStartedAtUtc,
                        "failed-"
                        + exception.GetType().Name.ToLowerInvariant());
                    Publish(context.Snapshot);
                }
            }
            catch
            {
                // The original failure remains authoritative. A store failure
                // must not trigger a second launch or relax process ownership.
            }
            finally
            {
                try { await TerminateAndRecoverSlotsAsync(context); }
                catch (Exception recoveryError)
                {
                    try { store.PersistSlotRecoveryDiagnostic(context.OperationId, recoveryError); }
                    catch { /* Pending records remain the recovery source of truth. */ }
                }
                ClearActive(context);
            }
        }

        private void Publish(ColdRestoreOperationSnapshot snapshot)
        {
            lock (sync)
            {
                latestSnapshot = snapshot;
            }

            OperationChanged?.Invoke(
                this,
                new ColdRestoreOperationChangedEventArgs(snapshot));
        }

        private void ClearActive(ColdRestoreOperationContext context)
        {
            var changed = false;
            lock (sync)
            {
                if (ReferenceEquals(active, context))
                {
                    active = null;
                    changed = true;
                }
            }

            if (changed)
            {
                ActivityChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void SetActive(ColdRestoreOperationContext context)
        {
            lock (sync)
            {
                if (active != null)
                {
                    throw new InvalidOperationException(
                        "A cold-restore operation is already active.");
                }

                active = context;
            }

            ActivityChanged?.Invoke(this, EventArgs.Empty);
        }

        private static bool IsOperationEvent(
            SessionEnvelopeEventArgs value,
            RuntimeSessionClient session,
            string messageType,
            string operationId)
        {
            if (!ReferenceEquals(value.Session, session)
                || !string.Equals(
                    value.Envelope.MessageType,
                    messageType,
                    StringComparison.Ordinal))
            {
                return false;
            }

            var fields = TryDecode(value.Envelope);
            return fields != null
                   && fields.TryGetValue("operationId", out var actual)
                   && string.Equals(
                       actual,
                       operationId,
                       StringComparison.Ordinal);
        }

        private static bool IsRequestEvent(
            SessionEnvelopeEventArgs value,
            RuntimeSessionClient session,
            string messageType,
            string requestId)
        {
            if (!ReferenceEquals(value.Session, session)
                || !string.Equals(
                    value.Envelope.MessageType,
                    messageType,
                    StringComparison.Ordinal))
            {
                return false;
            }

            var fields = TryDecode(value.Envelope);
            return fields != null
                   && fields.TryGetValue("requestId", out var actual)
                   && string.Equals(
                       actual,
                       requestId,
                       StringComparison.Ordinal);
        }

        private static bool IsCommandResult(
            SessionEnvelopeEventArgs value,
            RuntimeSessionClient session,
            string requestId)
        {
            if (!ReferenceEquals(value.Session, session)
                || !string.Equals(
                    value.Envelope.MessageType,
                    IpcMessageTypes.CommandAccepted,
                    StringComparison.Ordinal)
                   && !string.Equals(
                       value.Envelope.MessageType,
                       IpcMessageTypes.CommandRejected,
                       StringComparison.Ordinal))
            {
                return false;
            }

            var fields = TryDecode(value.Envelope);
            return fields != null
                   && fields.TryGetValue("requestId", out var actual)
                   && string.Equals(actual, requestId, StringComparison.Ordinal);
        }

        private static void RequireAccepted(IpcEnvelope envelope)
        {
            var fields = Decode(envelope);
            if (string.Equals(
                    envelope.MessageType,
                    IpcMessageTypes.CommandAccepted,
                    StringComparison.Ordinal))
            {
                return;
            }

            throw new ColdRestoreCommandRejectedException(
                fields.TryGetValue("errorCode", out var code) ? code : "RuntimeRejected",
                fields.TryGetValue("detail", out var detail)
                    ? detail
                    : "Runtime rejected the cold-restore command.");
        }

        private static IReadOnlyDictionary<string, string> Decode(
            IpcEnvelope envelope)
        {
            var decoded = IpcPayloadCodec.TryDeserialize(
                envelope.PayloadUtf8);
            if (!decoded.Success || decoded.Fields == null)
            {
                throw new InvalidDataException(
                    "Runtime returned an invalid IPC payload.");
            }

            return decoded.Fields;
        }

        private static IReadOnlyDictionary<string, string>? TryDecode(
            IpcEnvelope envelope)
        {
            var decoded = IpcPayloadCodec.TryDeserialize(
                envelope.PayloadUtf8);
            return decoded.Success ? decoded.Fields : null;
        }

        private static IReadOnlyDictionary<string, string> Fields(
            params string[] pairs)
        {
            if (pairs.Length % 2 != 0)
            {
                throw new ArgumentException("Fields require key/value pairs.");
            }

            var fields = new Dictionary<string, string>(
                StringComparer.Ordinal);
            for (var index = 0; index < pairs.Length; index += 2)
            {
                fields.Add(pairs[index], pairs[index + 1]);
            }

            return fields;
        }

        private IReadOnlyDictionary<string, string> ClaimFields(
            ColdRestoreOperationContext context,
            string requestId)
        {
            return Fields(
                "claimId",
                context.ClaimId,
                "companionInstanceId",
                identity.CompanionInstanceId,
                "intentSha256",
                context.IntentSha256,
                "operationId",
                context.OperationId,
                "requestId",
                requestId);
        }

        private static void RequireExactFields(
            IReadOnlyDictionary<string, string> fields,
            params string[] names)
        {
            if (fields.Count != names.Length
                || names.Any(name => !fields.ContainsKey(name)))
            {
                throw new InvalidDataException(
                    "Runtime cold-restore event shape is invalid.");
            }
        }

        private static void RequireEqual(
            IReadOnlyDictionary<string, string> fields,
            string name,
            string expected)
        {
            if (!fields.TryGetValue(name, out var actual)
                || !string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Runtime cold-restore field does not match: "
                    + name
                    + ".");
            }
        }

        private static void RequireSha256Field(
            IReadOnlyDictionary<string, string> fields,
            string name)
        {
            if (!fields.TryGetValue(name, out var value))
            {
                throw new InvalidDataException(
                    "Runtime cold-restore SHA-256 field is missing: "
                    + name
                    + ".");
            }

            if (value.Length != 64
                || value.Any(
                    character => character < '0'
                                 || character > '9'
                                    && (character < 'a'
                                        || character > 'f')))
            {
                throw new InvalidDataException(
                    "Runtime cold-restore SHA-256 field is invalid: "
                    + name
                    + ".");
            }
        }

        private static string NewRequestId(string prefix)
        {
            return prefix + "-" + Guid.NewGuid().ToString("N");
        }

        private static DateTimeOffset SourceStartedAtUtc(
            ColdRestoreOperationContext context)
        {
            return new DateTimeOffset(
                context.SourceSession.GameProcessStartTimeUtcTicks,
                TimeSpan.Zero);
        }

        private static DateTimeOffset TargetStartedAtUtc(
            ColdRestoreOperationContext context)
        {
            var target = context.TargetSession
                         ?? throw new InvalidOperationException(
                             "Cold target Runtime session is unavailable.");
            return new DateTimeOffset(
                target.GameProcessStartTimeUtcTicks,
                TimeSpan.Zero);
        }

        private static ColdRestoreIntent RequireIntent(
            ColdRestoreOperationContext context)
        {
            return context.Intent
                   ?? throw new InvalidOperationException(
                       "Cold-restore intent is unavailable.");
        }

        private static ColdRestoreOperationSnapshot RequireSnapshot(
            ColdRestoreOperationContext context)
        {
            return context.Snapshot
                   ?? throw new InvalidOperationException(
                       "Cold-restore operation snapshot is unavailable.");
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(
                    nameof(ColdRestoreSupervisor));
            }
        }

        private sealed class PreparedSource
        {
            public PreparedSource(
                ColdRestoreIntent intent,
                string intentSha256,
                ColdRestoreOperationSnapshot snapshot)
            {
                Intent = intent;
                IntentSha256 = intentSha256;
                Snapshot = snapshot;
            }

            public ColdRestoreIntent Intent { get; }
            public string IntentSha256 { get; }
            public ColdRestoreOperationSnapshot Snapshot { get; }
        }

        private sealed class ColdRestoreOperationContext
        {
            public ColdRestoreOperationContext(
                string operationId,
                string intentId,
                string claimId,
                ColdRestoreOperationKind operationKind,
                string replaySaveId,
                long targetMovieTick,
                long expectedMovieTick,
                int expectedSceneEpoch,
                string requesterSurface,
                RuntimeSessionClient sourceSession,
                ColdRestoreLaunchEnvironment environment)
            {
                OperationId = operationId;
                IntentId = intentId;
                ClaimId = claimId;
                OperationKind = operationKind;
                ReplaySaveId = replaySaveId;
                TargetMovieTick = targetMovieTick;
                ExpectedMovieTick = expectedMovieTick;
                ExpectedSceneEpoch = expectedSceneEpoch;
                RequesterSurface = requesterSurface;
                SourceSession = sourceSession;
                Environment = environment;
            }

            public string OperationId { get; }
            public string IntentId { get; }
            public string ClaimId { get; }
            public ColdRestoreOperationKind OperationKind { get; }
            public string ReplaySaveId { get; }
            public long TargetMovieTick { get; }
            public long ExpectedMovieTick { get; }
            public int ExpectedSceneEpoch { get; }
            public string RequesterSurface { get; }
            public RuntimeSessionClient SourceSession { get; }
            public ColdRestoreLaunchEnvironment Environment { get; }
            public string IntentSha256 { get; set; } = string.Empty;
            public string LifecyclePlanHash { get; set; } = string.Empty;
            public ColdRestoreIntent? Intent { get; set; }
            public ColdRestoreOperationSnapshot? Snapshot { get; set; }
            public IColdRestoreGameLaunchHandle? LaunchHandle { get; set; }
            public StartupProfileAttestation?
                SourceStartupAttestation { get; set; }
            public StartupProfileAttestation?
                TargetStartupAttestation { get; set; }
            public RuntimeSessionClient? TargetSession { get; set; }
            public Task? RunTask { get; set; }
            public CancellationTokenSource? OperationCancellation { get; set; }
            public bool UserCancellationRequested { get; set; }
        }

        private sealed class EnvelopeWaiter
        {
            private CancellationTokenRegistration cancellation;

            public EnvelopeWaiter(
                Func<SessionEnvelopeEventArgs, bool> predicate)
            {
                Predicate = predicate
                            ?? throw new ArgumentNullException(
                                nameof(predicate));
                Completion = new TaskCompletionSource<
                    SessionEnvelopeEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public Func<SessionEnvelopeEventArgs, bool> Predicate { get; }
            public TaskCompletionSource<SessionEnvelopeEventArgs>
                Completion { get; }

            public void RegisterCancellation(
                CancellationToken token,
                Action callback)
            {
                cancellation = token.Register(callback);
            }

            public void Complete(SessionEnvelopeEventArgs value)
            {
                cancellation.Dispose();
                Completion.TrySetResult(value);
            }

            public void Cancel(CancellationToken token)
            {
                Completion.TrySetCanceled(token);
            }
        }
    }
}
