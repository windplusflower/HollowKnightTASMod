using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.Automation;
using HollowKnightTAS.Automation.Client;
using HollowKnightTAS.Core.Automation;
using HollowKnightTAS.Core.Ipc;
using HollowKnightTAS.Core.ReplaySave;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Companion.Tests
{
    [TestClass]
    public sealed class ColdRestoreSupervisorTests
    {
        [TestMethod]
        public void ColdCommandRejectionPreservesStructuredApprovalCode()
        {
            var envelope = new IpcEnvelope(1, "test-session", 1, IpcMessageTypes.CommandRejected,
                IpcPayloadCodec.Serialize(new Dictionary<string, string>
                {
                    ["errorCode"] = "ColdRestoreSlotApprovalRequired",
                    ["detail"] = "localized user-facing text without an error-code prefix"
                }));
            var method = typeof(ColdRestoreSupervisor).GetMethod("RequireAccepted",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            try
            {
                method.Invoke(null, new object[] { envelope });
                Assert.Fail("A rejected envelope was accepted.");
            }
            catch (System.Reflection.TargetInvocationException wrapper)
            {
                Assert.IsInstanceOfType<ColdRestoreCommandRejectedException>(wrapper.InnerException);
                var rejection = (ColdRestoreCommandRejectedException)wrapper.InnerException!;
                Assert.AreEqual("ColdRestoreSlotApprovalRequired", rejection.ErrorCode);
                Assert.AreEqual("localized user-facing text without an error-code prefix", rejection.Message);
            }
        }

        [TestMethod]
        [DataRow(false, false, false, false, false, false)]
        [DataRow(true, false, false, false, false, false)]
        [DataRow(false, true, false, false, false, false)]
        [DataRow(false, false, true, false, false, false)]
        [DataRow(false, false, false, true, false, false)]
        [DataRow(false, false, false, false, true, false)]
        [DataRow(false, false, false, false, false, true)]
        [Timeout(15000)]
        public async Task ColdOperation_UsesOneFreshProcessAndPersistsFullHandoff(
            bool seekMovie, bool menuSource, bool rejectFirst, bool cancelAtReplay, bool cancelAtExit, bool failLaunch)
            => await RunColdOperation(seekMovie, menuSource, rejectFirst, cancelAtReplay, cancelAtExit, failLaunch);

        [TestMethod]
        [Timeout(15000)]
        public async Task CancelledReplayStillCompletesWithExitRecoveryIntegration()
            => await RunColdOperation(false, false, false, true, false, false);

        [TestMethod]
        [Timeout(15000)]
        public async Task CancelDuringQuiesceExitsSourceWithoutUnfreezingOrLaunching()
            => await RunColdOperation(false, false, false, false, false, false, cancelAtQuiesce: true);

        [TestMethod]
        [Timeout(15000)]
        public async Task LifecycleBranchSeekBindsUploadedPlanAcrossFreshProcessHandoff()
            => await RunColdOperation(true, false, false, false, false, false, true);

        private async Task RunColdOperation(bool seekMovie, bool menuSource, bool rejectFirst,
            bool cancelAtReplay, bool cancelAtExit, bool failLaunch, bool lifecycleBranch = false, bool mcpCancel = false, bool cancelAtQuiesce = false)
        {
            var root = TemporaryDirectory();
            FakeRuntimeEndpoint? sourceEndpoint = null;
            FakeRuntimeEndpoint? targetEndpoint = null;
            try
            {
                var identity = ColdRestoreSupervisorIdentity.LoadOrCreate(
                    Path.Combine(root, "identity"));
                using var sessions = new SessionRegistry(
                    identity.CompanionInstanceId);
                var sourceRegistration = Registration(
                    "source-session",
                    41001,
                    DateTimeOffset.UtcNow.AddMinutes(-3),
                    Hash('a'),
                    Hash('e'),
                    Hash('9'));
                var targetRegistration = Registration(
                    "target-session",
                    41002,
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    Hash('a'),
                    Hash('e'),
                    Hash('9'));
                var processMonitor = new FakeProcessMonitor();
                string operationId = string.Empty;
                string intentSha256 = string.Empty;
                string claimId = string.Empty;
                var expectedOperationKind = lifecycleBranch ? ColdRestoreOperationKind.ApplyBranchAndSeek : seekMovie
                    ? ColdRestoreOperationKind.SeekMovieTick
                    : ColdRestoreOperationKind.RestoreReplaySave;
                var expectedTargetMovieTick = seekMovie ? 12L : 5L;
                var build = new ColdRestoreBuildFingerprint(
                    Hash('a'),
                    Hash('b'),
                    Hash('c'),
                    Hash('d'),
                    Hash('e'),
                    Hash('f'),
                    Hash('1'),
                    string.Empty);

                var rejectNextPreparation = rejectFirst;
                var exitRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var allowExit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                sourceEndpoint = new FakeRuntimeEndpoint(
                    sourceRegistration,
                    async (endpoint, command) =>
                    {
                        var fields = Decode(command);
                        switch (command.MessageType)
                        {
                            case IpcMessageTypes
                                .RequestStartupProfileAttestation:
                                await endpoint.SendAsync(
                                    IpcMessageTypes
                                        .StartupProfileAttestation,
                                    VerifiedStartupAttestation(
                                        sourceRegistration,
                                        "source-run",
                                        fields["requestId"],
                                        menuSource ? StartupRecordingRootStatus.Pending : StartupRecordingRootStatus.Verified));
                                await endpoint.AcceptAsync(command);
                                break;
                            case IpcMessageTypes.PrepareColdRestore:
                            {
                                if (rejectNextPreparation)
                                {
                                    rejectNextPreparation = false;
                                    await endpoint.SendAsync(
                                        IpcMessageTypes.CommandRejected,
                                        Fields("requestId", fields["requestId"],
                                            "errorCode", "ReplaySaveUnavailable",
                                            "detail", "Replay-save entry was not found."));
                                    break;
                                }
                                Assert.AreEqual(
                                    expectedOperationKind.ToString(),
                                    fields["operationKind"]);
                                Assert.AreEqual(
                                    seekMovie ? "12" : "-1",
                                    fields["targetMovieTick"]);
                                Assert.AreEqual(
                                    seekMovie ? "10" : "-1",
                                    fields["expectedMovieTick"]);
                                Assert.AreEqual(
                                    seekMovie ? "2" : "-1",
                                    fields["expectedSceneEpoch"]);
                                operationId = fields["operationId"];
                                Assert.AreEqual(lifecycleBranch ? Hash('8') : string.Empty, fields["lifecyclePlanObjectSha256"]);
                                var created = DateTimeOffset.UtcNow;
                                var intent = new ColdRestoreIntent(
                                    ColdRestoreIntent.CurrentSchemaVersion,
                                    fields["intentId"],
                                    operationId,
                                    expectedOperationKind,
                                    seekMovie
                                        ? "save-selected"
                                        : fields["replaySaveId"],
                                    sourceRegistration.SessionId,
                                    sourceRegistration.GameProcessId,
                                    new DateTimeOffset(
                                        sourceRegistration
                                            .GameProcessStartTimeUtcTicks,
                                        TimeSpan.Zero),
                                    created,
                                    created.AddMinutes(10),
                                    menuSource ? -1 : 10,
                                    menuSource ? 0 : 2,
                                    Hash('2'),
                                    menuSource ? string.Empty : Hash('3'),
                                    Hash('4'),
                                    Hash('5'),
                                    Hash('6'),
                                    expectedTargetMovieTick,
                                    seekMovie ? string.Empty : Hash('7'),
                                    string.Empty,
                                    fields["requesterSurface"],
                                    build, fields["lifecyclePlanObjectSha256"]);
                                var bytes = ColdRestoreIntentCodec.Serialize(
                                    intent);
                                intentSha256 =
                                    ColdRestoreIntentCodec.ComputeSha256(
                                        intent);
                                await endpoint.SendAsync(
                                    IpcMessageTypes
                                        .ColdRestoreIntentPrepared,
                                    Fields(
                                        "intentBase64",
                                        Convert.ToBase64String(bytes),
                                        "intentSha256",
                                        intentSha256,
                                        "operationId",
                                        operationId,
                                        "sourceMovieTick",
                                        menuSource ? "-1" : "10",
                                        "sourceSceneEpoch",
                                        menuSource ? "0" : "2"));
                                await endpoint.AcceptAsync(command);
                                break;
                            }
                            case IpcMessageTypes.QuiesceColdRestoreSource:
                                claimId = fields["claimId"];
                                if (cancelAtQuiesce)
                                {
                                    exitRequested.TrySetResult(true);
                                    await allowExit.Task.WaitAsync(TimeSpan.FromSeconds(5));
                                }
                                await endpoint.SendAsync(
                                    IpcMessageTypes
                                        .ColdRestoreSourceQuiesced,
                                    Fields(
                                        "claimId",
                                        claimId,
                                        "intentSha256",
                                        intentSha256,
                                        "operationId",
                                        operationId,
                                        "sourceSessionId",
                                        sourceRegistration.SessionId));
                                await endpoint.AcceptAsync(command);
                                break;
                            case IpcMessageTypes.ExitColdRestoreSource:
                                if (cancelAtExit)
                                {
                                    exitRequested.TrySetResult(true);
                                    await allowExit.Task.WaitAsync(TimeSpan.FromSeconds(5));
                                }
                                await endpoint.AcceptAsync(command);
                                processMonitor.SignalExit(
                                    sourceRegistration.GameProcessId);
                                break;
                            case IpcMessageTypes.Subscribe:
                                await endpoint.AcceptAsync(command);
                                break;
                            case IpcMessageTypes.RequestSnapshot:
                                await endpoint.SendAsync(IpcMessageTypes.RuntimeStatus,
                                    Fields("requestId", fields["requestId"], "movieTick", "10", "controlMode", "Paused"));
                                break;
                            default:
                                throw new InvalidOperationException(
                                    "Unexpected source command: "
                                    + command.MessageType);
                        }
                    });
                sourceEndpoint.Start();
                Assert.IsTrue(
                    await sessions.RegisterAsync(
                        sourceRegistration,
                        CancellationToken.None));
                var sourceSession = sessions.Sessions.Single();

                targetEndpoint = new FakeRuntimeEndpoint(
                    targetRegistration,
                    async (endpoint, command) =>
                    {
                        var fields = Decode(command);
                        switch (command.MessageType)
                        {
                            case IpcMessageTypes
                                .RequestStartupProfileAttestation:
                                await endpoint.SendAsync(
                                    IpcMessageTypes
                                        .StartupProfileAttestation,
                                    VerifiedStartupAttestation(
                                        targetRegistration,
                                        operationId,
                                        fields["requestId"]));
                                await endpoint.AcceptAsync(command);
                                break;
                            case IpcMessageTypes.BeginColdRestore:
                                claimId = fields["claimId"];
                                intentSha256 = fields["intentSha256"];
                                await endpoint.AcceptAsync(command);
                                await endpoint.ProgressAsync(
                                    operationId,
                                    claimId,
                                    intentSha256,
                                    "BaselineReady",
                                    expectedOperationKind,
                                    expectedTargetMovieTick);
                                break;
                            case IpcMessageTypes
                                .ReleaseColdRestoreBaseline:
                                await endpoint.AcceptAsync(command);
                                await endpoint.ProgressAsync(
                                    operationId,
                                    claimId,
                                    intentSha256,
                                    "ReplayingPrefix",
                                    expectedOperationKind,
                                    expectedTargetMovieTick);
                                if (cancelAtReplay) break;
                                await endpoint.ProgressAsync(
                                    operationId,
                                    claimId,
                                    intentSha256,
                                    "PausedAtTarget",
                                    expectedOperationKind,
                                    expectedTargetMovieTick);
                                break;
                            case IpcMessageTypes.ResumeReplaySaveRestore:
                                await endpoint.AcceptAsync(command);
                                await endpoint.ProgressAsync(
                                    operationId,
                                    claimId,
                                    intentSha256,
                                    "Completed",
                                    expectedOperationKind,
                                    expectedTargetMovieTick);
                                break;
                            case IpcMessageTypes.Subscribe:
                                await endpoint.AcceptAsync(command);
                                break;
                            case IpcMessageTypes.RequestSnapshot:
                                await endpoint.SendAsync(IpcMessageTypes.RuntimeStatus,
                                    Fields("requestId", fields["requestId"], "movieTick", "5", "controlMode", "Paused"));
                                break;
                            default:
                                throw new InvalidOperationException(
                                    "Unexpected target command: "
                                    + command.MessageType);
                        }
                    });
                var launchHandle = new FakeLaunchHandle(
                    targetRegistration.GameProcessId,
                    new DateTimeOffset(
                        targetRegistration.GameProcessStartTimeUtcTicks,
                        TimeSpan.Zero));
                launchHandle.OnTerminated = () => processMonitor.SignalExit(targetRegistration.GameProcessId);
                var launcher = new FakeLauncher(
                    sessions,
                    targetRegistration,
                    targetEndpoint,
                    launchHandle);
                launcher.FailLaunch = failLaunch;
                var sourceLaunchVerifier =
                    new FakeSourceLaunchVerifier();
                var environment = new ColdRestoreLaunchEnvironment(
                    Hash('b'),
                    Hash('c'),
                    Hash('d'),
                    Hash('f'),
                    Hash('1'),
                    string.Empty,
                    sourceLaunchVerifier,
                    launcher);
                var store = new ColdRestoreIntentStore(
                    Path.Combine(root, "store"),
                    identity.CompanionInstanceId,
                    identity.ClaimSecret);
                using var supervisor = new ColdRestoreSupervisor(
                    sessions,
                    store,
                    identity,
                    new FixedEnvironmentResolver(environment),
                    processMonitor);
                using var broker = new AutomationBroker(sessions, supervisor, Path.Combine(root, "automation"));
                var completed = new TaskCompletionSource<
                    ColdRestoreOperationSnapshot>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var replayStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                supervisor.OperationChanged += (_, args) =>
                {
                    if (args.Snapshot.Latest.State == ColdRestoreOperationState.ReplayingPrefix)
                        replayStarted.TrySetResult(true);
                    if (args.Snapshot.Latest.State
                        == ColdRestoreOperationState.Completed
                        || args.Snapshot.Latest.State == ColdRestoreOperationState.Failed)
                    {
                        completed.TrySetResult(args.Snapshot);
                    }
                };

                if (rejectFirst)
                {
                    var rejection = await Assert.ThrowsExactlyAsync<ColdRestoreCommandRejectedException>(
                        () => supervisor.BeginReplaySaveRestoreAsync(
                            sourceSession, "missing-save", "automation", CancellationToken.None));
                    Assert.AreEqual("ReplaySaveUnavailable", rejection.ErrorCode);
                    Assert.IsFalse(supervisor.IsActive);
                    Assert.AreEqual(0, launcher.LaunchCount);
                    Assert.AreEqual(0, processMonitor.WaitCount);
                    Assert.IsFalse(sourceEndpoint.Commands.Contains(IpcMessageTypes.QuiesceColdRestoreSource));
                    Assert.IsFalse(sourceEndpoint.Commands.Contains(IpcMessageTypes.ExitColdRestoreSource));
                }

                var started = seekMovie
                    ? await supervisor.BeginMovieSeekAsync(
                        sourceSession,
                        expectedOperationKind,
                        expectedTargetMovieTick,
                        10,
                        2,
                        "automation",
                        CancellationToken.None, lifecycleBranch ? Hash('8') : string.Empty)
                    : await supervisor.BeginReplaySaveRestoreAsync(
                        sourceSession,
                        "save-0001",
                        "automation",
                        CancellationToken.None);
                Assert.AreEqual(
                    ColdRestoreOperationState.Prepared,
                    started.Snapshot.Latest.State);
                if (cancelAtExit || cancelAtQuiesce)
                {
                    await exitRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var cancellation = broker.ExecuteHumanAsync(AutomationCommandIds.CancelReplaySaveRestore,
                        AutomationScope.ControlReplaySave, Fields("operationId", started.OperationId),
                        string.Empty, null, CancellationToken.None);
                    allowExit.TrySetResult(true);
                    var cancelled = await cancellation.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.IsTrue(cancelled.Success, cancelled.Detail);
                    Assert.AreEqual("ColdRestoreCancelled", cancelled.ResultCode);
                    Assert.AreEqual(1, processMonitor.WaitCount);
                    Assert.AreEqual(0, launcher.LaunchCount);
                    Assert.IsFalse(sourceEndpoint.Commands.Contains(IpcMessageTypes.CancelColdRestoreSource));
                    Assert.IsFalse(supervisor.IsActive);
                    Assert.IsTrue(sessions.Remove(sourceRegistration));
                    Assert.IsNotNull(broker.Bootstrap, "A terminal operation must remain observable without a live game.");
                    await using var terminalClient = new AutomationClient();
                    await terminalClient.ConnectAsync(new AutomationConnectOptions {
                        ClientId = "early-cancel-observer", BootstrapPath = broker.BootstrapPath
                    }, CancellationToken.None);
                    var terminal = await terminalClient.ExecuteAsync(terminalClient.CreateCommand(
                        AutomationCommandIds.GetStatus, AutomationScope.ObserveStatus), CancellationToken.None);
                    Assert.IsTrue(terminal.Success, terminal.Detail);
                    Assert.AreEqual(started.OperationId, terminal.Data["coldRestore.operationId"]);
                    Assert.AreEqual("Cancelled", terminal.Data["coldRestore.state"]);
                    var write = await terminalClient.ExecuteAsync(terminalClient.CreateCommand(
                        AutomationCommandIds.Step, AutomationScope.ControlPlayback,
                        Fields("count", "1"), expectedRuntimeMode: "Paused"), CancellationToken.None);
                    Assert.IsFalse(write.Success);
                    Assert.AreEqual("RuntimeDisconnected", write.ResultCode);
                    return;
                }
                if (cancelAtReplay)
                {
                    await replayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                        () => supervisor.CancelAsync("wrong-operation"));
                    Assert.IsTrue(supervisor.IsActive);
                    Assert.IsTrue(sessions.Remove(sourceRegistration));
                    Assert.AreEqual(sourceRegistration.SessionId, broker.Bootstrap!.SessionId);
                    await using var client = new AutomationClient();
                    await client.ConnectAsync(new AutomationConnectOptions {
                        ClientId = "cold-cancel-test", BootstrapPath = broker.BootstrapPath
                    }, CancellationToken.None);
                    var acquired = await client.ExecuteAsync(client.CreateCommand(
                        AutomationCommandIds.AcquireControl, AutomationScope.ControlReplaySave,
                        new Dictionary<string, string> { ["scopes"] = AutomationScope.ControlReplaySave }), CancellationToken.None);
                    Assert.IsTrue(acquired.Success);
                    var observed = await client.ExecuteAsync(client.CreateCommand(
                        AutomationCommandIds.GetStatus, AutomationScope.ObserveStatus), CancellationToken.None);
                    Assert.IsTrue(observed.Success, observed.Detail);
                    Assert.AreEqual(started.OperationId, observed.Data["coldRestore.operationId"]);
                    Assert.AreEqual("ReplayingPrefix", observed.Data["coldRestore.state"]);
                    Assert.AreEqual(started.Snapshot.Intent.TargetMovieTick.ToString(CultureInfo.InvariantCulture),
                        observed.Data["coldRestore.targetMovieTick"]);
                    AutomationResultEnvelope cancelled;
                    if (mcpCancel)
                    {
                        await client.ExecuteAsync(client.CreateCommand(AutomationCommandIds.ReleaseControl,
                            AutomationScope.ControlReplaySave, leaseId: acquired.Data["leaseId"]), CancellationToken.None);
                        await using var bridge = new HollowKnightTAS.AgentBridge.McpStdioServer(Array.Empty<string>());
                        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                        var bridgeType = bridge.GetType();
                        var transport = (AutomationClient)bridgeType.GetField("automation", flags)!.GetValue(bridge)!;
                        await transport.ConnectAsync(new AutomationConnectOptions {
                            ClientId = "mcp-cold-cancel", BootstrapPath = broker.BootstrapPath
                        }, CancellationToken.None);
                        var lease = await transport.ExecuteAsync(transport.CreateCommand(AutomationCommandIds.AcquireControl,
                            AutomationScope.ControlReplaySave, Fields("scopes", AutomationScope.ControlReplaySave)), CancellationToken.None);
                        Assert.IsTrue(lease.Success, lease.Detail);
                        bridgeType.GetField("activeLeaseId", flags)!.SetValue(bridge, lease.Data["leaseId"]);
                        bridgeType.GetField("activeLeaseScopes", flags)!.SetValue(bridge, new[] { AutomationScope.ControlReplaySave });
                        using var arguments = System.Text.Json.JsonDocument.Parse(
                            System.Text.Json.JsonSerializer.Serialize(new { operationId = started.OperationId }));
                        var execute = bridgeType.GetMethod("ExecuteToolAsync", flags)!;
                        cancelled = await (Task<AutomationResultEnvelope>)execute.Invoke(bridge,
                            new object[] { "hktas_cancel_replay_save_restore", arguments.RootElement, CancellationToken.None })!;
                    }
                    else cancelled = await client.ExecuteAsync(client.CreateCommand(
                        AutomationCommandIds.CancelReplaySaveRestore, AutomationScope.ControlReplaySave,
                        new Dictionary<string, string> { ["operationId"] = started.OperationId },
                        leaseId: acquired.Data["leaseId"]), CancellationToken.None);
                    Assert.IsTrue(cancelled.Success, cancelled.Detail);
                    Assert.AreEqual("ColdRestoreCancelled", cancelled.ResultCode);
                    await using var observer = new AutomationClient();
                    await observer.ConnectAsync(new AutomationConnectOptions {
                        ClientId = "cold-result-observer", BootstrapPath = broker.BootstrapPath
                    }, CancellationToken.None);
                    var ended = await observer.ExecuteAsync(observer.CreateCommand(
                        AutomationCommandIds.GetStatus, AutomationScope.ObserveStatus), CancellationToken.None);
                    Assert.IsTrue(ended.Success, ended.ResultCode + ": " + ended.Detail);
                    Assert.AreEqual(started.OperationId, ended.Data["coldRestore.operationId"]);
                    Assert.AreEqual("Cancelled", ended.Data["coldRestore.state"]);
                    Assert.IsFalse(supervisor.IsActive);
                    Assert.AreEqual(1, launcher.LaunchCount);
                    Assert.IsTrue(launchHandle.Killed);
                    Assert.AreEqual(2, processMonitor.WaitCount, "Source and terminated target must both be confirmed exited.");
                    Assert.AreEqual("Complete", supervisor.LatestSlotRecovery?.Status);
                    Assert.AreEqual(started.OperationId, broker.LatestSlotRecovery?.OperationId);
                    Assert.IsFalse(launchHandle.SupervisionReleased);
                    Assert.IsFalse(targetEndpoint.Commands.Contains(IpcMessageTypes.ResumeReplaySaveRestore));
                    return;
                }
                var final = await completed.Task.WaitAsync(
                    TimeSpan.FromSeconds(5));
                for (var attempt = 0;
                     attempt < 50 && supervisor.IsActive;
                     attempt++)
                {
                    await Task.Delay(10);
                }

                if (failLaunch)
                {
                    Assert.AreEqual(ColdRestoreOperationState.Failed, final.Latest.State);
                    Assert.AreEqual(1, launcher.LaunchCount, "A failed launch must never be retried implicitly.");
                    Assert.AreEqual(1, processMonitor.WaitCount);
                    Assert.IsFalse(supervisor.IsActive);
                    Assert.IsFalse(launchHandle.SupervisionReleased);
                    Assert.IsFalse(targetEndpoint.Commands.Contains(IpcMessageTypes.ResumeReplaySaveRestore));
                    StringAssert.Contains(File.ReadAllText(Path.Combine(root, "store", "operations",
                        started.OperationId, "failure.txt")), "Injected launch failure");
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                        () => supervisor.CancelAsync(started.OperationId));
                    return;
                }

                CollectionAssert.AreEqual(
                    new[]
                    {
                        ColdRestoreOperationState.Prepared,
                        ColdRestoreOperationState.SourceQuiesced,
                        ColdRestoreOperationState.SourceExited,
                        ColdRestoreOperationState.Launching,
                        ColdRestoreOperationState.NewSessionAttached,
                        ColdRestoreOperationState.IntentClaimed,
                        ColdRestoreOperationState.BaselineReady,
                        ColdRestoreOperationState.ReplayingPrefix,
                        ColdRestoreOperationState.PausedAtTarget,
                        ColdRestoreOperationState.Completed
                    },
                    final.Records.Select(value => value.State).ToArray());
                Assert.AreEqual(1, launcher.LaunchCount);
                Assert.AreEqual(rejectFirst ? 2 : 1, sourceLaunchVerifier.VerifyCount);
                Assert.AreEqual(1, processMonitor.WaitCount);
                Assert.IsTrue(launchHandle.SupervisionReleased);
                Assert.IsFalse(launchHandle.Killed);
                Assert.IsFalse(supervisor.IsActive);
                Assert.IsFalse(
                    sourceEndpoint.Commands.Contains(
                        IpcMessageTypes.RestoreReplaySave));
                Assert.IsTrue(
                    targetEndpoint.Commands.Contains(
                        IpcMessageTypes.ResumeReplaySaveRestore));
                Assert.AreEqual(
                    rejectFirst ? 2 : 1,
                    sourceEndpoint.Commands.Count(
                        value => value
                                 == IpcMessageTypes
                                     .RequestStartupProfileAttestation));
                Assert.AreEqual(
                    2,
                    targetEndpoint.Commands.Count(
                        value => value
                                 == IpcMessageTypes
                                     .RequestStartupProfileAttestation));
            }
            finally
            {
                if (targetEndpoint != null)
                {
                    await targetEndpoint.DisposeAsync();
                }

                if (sourceEndpoint != null)
                {
                    await sourceEndpoint.DisposeAsync();
                }

                Directory.Delete(root, true);
            }
        }

        private static CompanionSessionRegistration Registration(
            string sessionId,
            int processId,
            DateTimeOffset startedAtUtc,
            string environmentSha256,
            string runtimeSha256,
            string coreSha256)
        {
            var token = new byte[32];
            RandomNumberGenerator.Fill(token);
            return new CompanionSessionRegistration(
                sessionId,
                processId,
                startedAtUtc.UtcTicks,
                environmentSha256,
                runtimeSha256,
                coreSha256,
                "HollowKnightTAS.Test."
                + Guid.NewGuid().ToString("N"),
                new ProtocolRange(1, 1),
                true,
                token,
                AutomationMode.ApprovedControl,
                false);
        }

        private static IReadOnlyDictionary<string, string> Decode(
            IpcEnvelope envelope)
        {
            var decoded = IpcPayloadCodec.TryDeserialize(
                envelope.PayloadUtf8);
            Assert.IsTrue(decoded.Success);
            Assert.IsNotNull(decoded.Fields);
            return decoded.Fields;
        }

        private static IReadOnlyDictionary<string, string> Fields(
            params string[] pairs)
        {
            var result = new Dictionary<string, string>(
                StringComparer.Ordinal);
            for (var index = 0; index < pairs.Length; index += 2)
            {
                result.Add(pairs[index], pairs[index + 1]);
            }

            return result;
        }

        private static IReadOnlyDictionary<string, string>
            VerifiedStartupAttestation(
                CompanionSessionRegistration registration,
                string runId,
                string requestId,
                StartupRecordingRootStatus rootStatus = StartupRecordingRootStatus.Verified)
        {
            return new StartupProfileAttestation(
                    StartupProfileAttestationStatus.Verified,
                    rootStatus,
                    runId,
                    registration.GameProcessId,
                    registration.GameProcessStartTimeUtcTicks,
                    StartupProfileContract.ProfileId,
                    StartupProfileContract.BridgeAbi,
                    StartupProfileContract.ReadyBridgeStatus,
                    true,
                    true,
                    true,
                    true,
                    true,
                    0,
                    0,
                    0,
                    0,
                    0,
                    string.Empty,
                    true,
                    2,
                    true,
                    true,
                    true,
                    0,
                    0,
                    0)
                .ToFields(requestId);
        }

        private static string Hash(char value)
        {
            return new string(value, 64);
        }

        private static string TemporaryDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "hktas-cold-supervisor-tests-"
                + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private sealed class FixedEnvironmentResolver :
            IColdRestoreLaunchEnvironmentResolver
        {
            private readonly ColdRestoreLaunchEnvironment environment;

            public FixedEnvironmentResolver(
                ColdRestoreLaunchEnvironment environment)
            {
                this.environment = environment;
            }

            public ColdRestoreLaunchEnvironment Resolve(
                RuntimeSessionClient sourceSession)
            {
                return environment;
            }
        }

        private sealed class FakeProcessMonitor : IColdRestoreProcessMonitor
        {
            private readonly HashSet<int> exited = new HashSet<int>();
            private readonly SemaphoreSlim changed = new SemaphoreSlim(0);

            public int WaitCount { get; private set; }

            public void SignalExit(int processId)
            {
                lock (exited)
                {
                    exited.Add(processId);
                }

                changed.Release();
            }

            public async Task WaitForExitAsync(
                int processId,
                DateTimeOffset processStartedAtUtc,
                TimeSpan timeout,
                CancellationToken cancellationToken)
            {
                WaitCount++;
                using var linked = CancellationTokenSource
                    .CreateLinkedTokenSource(cancellationToken);
                linked.CancelAfter(timeout);
                while (true)
                {
                    lock (exited)
                    {
                        if (exited.Contains(processId))
                        {
                            return;
                        }
                    }

                    await changed.WaitAsync(linked.Token);
                }
            }
        }

        private sealed class FakeSourceLaunchVerifier :
            ITrustedSourceLaunchVerifier
        {
            public int VerifyCount { get; private set; }

            public void RequireTrusted(
                RuntimeSessionClient session,
                StartupProfileAttestation attestation)
            {
                Assert.IsNotNull(session);
                Assert.AreEqual(
                    StartupProfileAttestationStatus.Verified,
                    attestation.Status);
                VerifyCount++;
            }
        }

        private sealed class FakeLauncher : IColdRestoreGameLauncher
        {
            private readonly SessionRegistry sessions;
            private readonly CompanionSessionRegistration registration;
            private readonly FakeRuntimeEndpoint endpoint;
            private readonly FakeLaunchHandle handle;

            public FakeLauncher(
                SessionRegistry sessions,
                CompanionSessionRegistration registration,
                FakeRuntimeEndpoint endpoint,
                FakeLaunchHandle handle)
            {
                this.sessions = sessions;
                this.registration = registration;
                this.endpoint = endpoint;
                this.handle = handle;
            }

            public int LaunchCount { get; private set; }
            public bool FailLaunch { get; set; }

            public async Task<IColdRestoreGameLaunchHandle> LaunchAsync(
                ColdRestoreIntent intent,
                TimeSpan timeout,
                CancellationToken cancellationToken)
            {
                LaunchCount++;
                if (FailLaunch) throw new InvalidOperationException("Injected launch failure");
                endpoint.Start();
                if (!await sessions.RegisterAsync(
                        registration,
                        cancellationToken))
                {
                    throw new InvalidOperationException(
                        "Fake target Runtime registration failed.");
                }

                return handle;
            }
        }

        private sealed class FakeLaunchHandle :
            IColdRestoreGameLaunchHandle
        {
            public FakeLaunchHandle(
                int processId,
                DateTimeOffset processStartedAtUtc)
            {
                ProcessId = processId;
                ProcessStartedAtUtc = processStartedAtUtc;
            }

            public int ProcessId { get; }
            public DateTimeOffset ProcessStartedAtUtc { get; }
            public string LauncherEvidence => "fake-launch-evidence";
            public bool HasExited => false;
            public bool SupervisionReleased { get; private set; }
            public bool Killed { get; private set; }
            public Action? OnTerminated { get; set; }

            public void ReleaseSupervision()
            {
                SupervisionReleased = true;
            }

            public void Dispose()
            {
                if (!SupervisionReleased)
                {
                    Killed = true;
                    OnTerminated?.Invoke();
                }
            }
        }

        private sealed class FakeRuntimeEndpoint : IAsyncDisposable
        {
            private readonly CompanionSessionRegistration registration;
            private readonly Func<
                FakeRuntimeEndpoint,
                IpcEnvelope,
                Task> handler;
            private readonly CancellationTokenSource cancellation =
                new CancellationTokenSource();
            private readonly List<string> commands = new List<string>();
            private NamedPipeServerStream? server;
            private Task? runTask;
            private long sequence;

            public FakeRuntimeEndpoint(
                CompanionSessionRegistration registration,
                Func<FakeRuntimeEndpoint, IpcEnvelope, Task> handler)
            {
                this.registration = registration;
                this.handler = handler;
            }

            public IReadOnlyList<string> Commands
            {
                get
                {
                    lock (commands)
                    {
                        return commands.ToArray();
                    }
                }
            }

            public void Start()
            {
                if (runTask != null)
                {
                    return;
                }

                runTask = RunAsync(cancellation.Token);
            }

            public async Task AcceptAsync(IpcEnvelope command)
            {
                var fields = Decode(command);
                await SendAsync(
                    IpcMessageTypes.CommandAccepted,
                    Fields(
                        "command",
                        command.MessageType,
                        "detail",
                        "accepted",
                        "requestId",
                        fields["requestId"]));
            }

            public Task ProgressAsync(
                string operationId,
                string claimId,
                string intentSha256,
                string phase,
                ColdRestoreOperationKind operationKind,
                long targetMovieTick)
            {
                var fields = new Dictionary<string, string>(
                    Fields(
                        "claimId",
                        claimId,
                        "detail",
                        phase,
                        "equivalenceClass",
                        "VanillaEquivalent",
                        "intentSha256",
                        intentSha256,
                        "operationId",
                        operationId,
                        "phase",
                        phase,
                        "restoreStrategy",
                        "VanillaEquivalentColdReplay"),
                    StringComparer.Ordinal);
                if (string.Equals(
                        phase,
                        "PausedAtTarget",
                        StringComparison.Ordinal))
                {
                    fields["actualSemanticSha256"] = Hash('7');
                    fields["actualVerificationSha256"] = Hash('8');
                    fields["expectedSemanticSha256"] = operationKind
                        == ColdRestoreOperationKind.RestoreReplaySave
                            ? Hash('7')
                            : string.Empty;
                    fields["expectedVerificationSha256"] = operationKind
                        == ColdRestoreOperationKind.RestoreReplaySave
                            ? Hash('8')
                            : string.Empty;
                    fields["strictSemanticEquivalent"] = operationKind
                        == ColdRestoreOperationKind.RestoreReplaySave
                            ? "true"
                            : string.Empty;
                    fields["targetMovieTick"] = targetMovieTick.ToString(
                        CultureInfo.InvariantCulture);
                    fields["targetVerification"] = operationKind
                        == ColdRestoreOperationKind.RestoreReplaySave
                            ? ReplayRestoreTargetVerification
                                .ExactSavedSemantic.ToString()
                            : ReplayRestoreTargetVerification
                                .ReconstructedObservation.ToString();
                }

                return SendAsync(
                    IpcMessageTypes.ReplaySaveRestoreProgress,
                    fields);
            }

            public async Task SendAsync(
                string messageType,
                IReadOnlyDictionary<string, string> fields)
            {
                var active = server
                             ?? throw new InvalidOperationException(
                                 "Fake Runtime is not connected.");
                await IpcCodec.WriteFrameAsync(
                    active,
                    new IpcEnvelope(
                        1,
                        registration.SessionId,
                        Interlocked.Increment(ref sequence),
                        messageType,
                        IpcPayloadCodec.Serialize(fields)),
                    cancellation.Token);
            }

            public async ValueTask DisposeAsync()
            {
                cancellation.Cancel();
                server?.Dispose();
                if (runTask != null)
                {
                    try
                    {
                        await runTask;
                    }
                    catch (Exception exception) when (
                        exception is OperationCanceledException
                        || exception is IOException
                        || exception is ObjectDisposedException)
                    {
                    }
                }

                cancellation.Dispose();
            }

            private async Task RunAsync(CancellationToken token)
            {
                server = new NamedPipeServerStream(
                    registration.PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous
                    | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token);
                var hello = await IpcCodec.ReadFrameAsync(server, token);
                var validation = IpcHandshakeValidator.ValidateHello(
                    hello,
                    registration.SessionId,
                    registration.GameProcessId,
                    registration.ProtocolRange,
                    registration.Token);
                if (!validation.Success)
                {
                    throw new InvalidOperationException(validation.Error);
                }

                var nonce = new byte[32];
                RandomNumberGenerator.Fill(nonce);
                await IpcCodec.WriteFrameAsync(
                    server,
                    new IpcEnvelope(
                        1,
                        registration.SessionId,
                        0,
                        IpcMessageTypes.HelloAck,
                        IpcPayloadCodec.Serialize(
                            Fields(
                                "companionInstanceId",
                                validation.CompanionInstanceId,
                                "protocol",
                                "1",
                                "runtimeSessionId",
                                registration.SessionId,
                                "serverNonce",
                                Convert.ToBase64String(nonce)))),
                    token);
                while (!token.IsCancellationRequested)
                {
                    var command = await IpcCodec.ReadFrameAsync(
                        server,
                        token);
                    lock (commands)
                    {
                        commands.Add(command.MessageType);
                    }

                    await handler(this, command);
                }
            }
        }
    }
}
