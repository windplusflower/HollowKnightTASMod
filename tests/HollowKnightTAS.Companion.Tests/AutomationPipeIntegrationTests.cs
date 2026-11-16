using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Automation.Client;
using HollowKnightTAS.Companion.Automation;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Core.Automation;
using HollowKnightTAS.Core.Input;
using HollowKnightTAS.Core.Ipc;
using HollowKnightTAS.Core.Movie;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Companion.Tests
{
    [TestClass]
    [DoNotParallelize]
    public sealed class AutomationPipeIntegrationTests
    {
        private readonly string isolatedAutomationRoot = Path.Combine(Path.GetTempPath(),
            "HollowKnightTAS.Tests", "automation-" + Guid.NewGuid().ToString("N"));

        private AutomationBroker CreateBroker(SessionRegistry sessions) =>
            new AutomationBroker(sessions, automationDirectory: isolatedAutomationRoot);

        private static readonly string SemanticHash =
            new string('d', 64);

        [TestMethod]
        [Timeout(30000)]
        public async Task VideoExportFinalizationUsesOperationIdentityWithoutModePrecondition()
        {
            var fixture = CreateFixture();
            fixture.ControlMode = "Stepping";
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var runtime = RunFakeRuntimeAsync(fixture, cancellation.Token);
            using var sessions = new SessionRegistry("video-operation-integration");
            Assert.IsTrue(await sessions.RegisterAsync(fixture.Registration, cancellation.Token));
            using var broker = CreateBroker(sessions);
            try
            {
                foreach (var command in new[] { AutomationCommandIds.FinishVideoExport, AutomationCommandIds.CancelVideoExport })
                {
                    var result = await broker.ExecuteHumanAsync(command, AutomationScope.ControlPlayback,
                        new Dictionary<string, string> { ["operationId"] = "video-test" },
                        string.Empty, null, cancellation.Token);
                    Assert.IsTrue(result.Success, result.ResultCode + ": " + result.Detail);
                    Assert.AreEqual(command, result.Data["command"]);
                    var missing = await broker.ExecuteHumanAsync(command, AutomationScope.ControlPlayback,
                        new Dictionary<string, string>(), string.Empty, null, cancellation.Token);
                    Assert.AreEqual("InvalidArguments", missing.ResultCode);
                }
            }
            finally
            {
                cancellation.Cancel();
                try { await runtime; } catch (OperationCanceledException) { }
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public async Task NativeSlotLoadValidatesArgumentsAndUsesMenuSafePreflight()
        {
            var fixture = CreateFixture();
            fixture.OperationalMovieTick = "-1";
            fixture.ControlMode = "Running";
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var runtime = RunFakeRuntimeAsync(fixture, cancellation.Token);
            using var sessions = new SessionRegistry("load-slot-integration");
            Assert.IsTrue(await sessions.RegisterAsync(fixture.Registration, cancellation.Token));
            using var broker = CreateBroker(sessions);
            try
            {
                foreach (var slot in new[] { "0", "5", "-1", "2.5", "../2" })
                {
                    var invalid = await broker.ExecuteHumanAsync(AutomationCommandIds.LoadGameSlot,
                        AutomationScope.ControlPlayback, new Dictionary<string, string> { ["slot"] = slot },
                        "Running", null, cancellation.Token);
                    Assert.IsFalse(invalid.Success, slot);
                    Assert.AreEqual("InvalidArguments", invalid.ResultCode);
                }
                var paused = await broker.ExecuteHumanAsync(AutomationCommandIds.LoadGameSlot,
                    AutomationScope.ControlPlayback, new Dictionary<string, string> { ["slot"] = "2" },
                    "Paused", null, cancellation.Token);
                Assert.IsFalse(paused.Success);
                var accepted = await broker.ExecuteHumanAsync(AutomationCommandIds.LoadGameSlot,
                    AutomationScope.ControlPlayback, new Dictionary<string, string> { ["slot"] = "2" },
                    "Running", null, cancellation.Token);
                Assert.IsTrue(accepted.Success, accepted.Detail);
                Assert.AreEqual("2", fixture.LoadedSlot);
            }
            finally
            {
                cancellation.Cancel();
                try { await runtime; } catch (OperationCanceledException) { }
            }
        }
        private static readonly string AfterSemanticHash =
            new string('e', 64);
        private static readonly string SemanticJson =
            "{\"schemaVersion\":1,\"sha256\":\""
            + SemanticHash
            + "\",\"values\":[{\"key\":\"fixture.value\","
            + "\"kind\":\"Int32\",\"canonicalHex\":\"0000002a\","
            + "\"displayValue\":\"42\"}]}";

        [TestMethod]
        [Timeout(30000)]
        public async Task OperationalStatusUsesFreshMenuSafeRequestAcrossUiAndSdk()
        {
            var fixture = CreateFixture();
            fixture.OperationalMovieTick = "-1";
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var runtime = RunFakeRuntimeAsync(fixture, cancellation.Token);
            using var sessions = new SessionRegistry("operational-status-integration");
            Assert.IsTrue(await sessions.RegisterAsync(fixture.Registration, cancellation.Token));
            using var broker = CreateBroker(sessions);
            try
            {
                // Let the initial background stream prime finish so its unrequested full snapshot cannot race with statusOnly=true.
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    var initialStatus = await broker.ExecuteHumanAsync(AutomationCommandIds.GetStatus,
                        AutomationScope.ObserveStatus, new Dictionary<string, string>(),
                        string.Empty, null, cancellation.Token);
                    if (initialStatus.Success && initialStatus.Data.ContainsKey("runtimeStatusJson"))
                        break;
                    await Task.Delay(20, cancellation.Token);
                }

                var human = await broker.ExecuteHumanAsync(AutomationCommandIds.GetState,
                    AutomationScope.ObserveStateSummary,
                    new Dictionary<string, string> { ["statusOnly"] = "true" },
                    string.Empty, null, cancellation.Token);
                Assert.IsTrue(human.Success, human.Detail);
                Assert.AreEqual("false", human.Data["autoSaveEnabled"]);
                Assert.IsFalse(human.Data.ContainsKey("stateJson"));
                var aggregate = await broker.ExecuteHumanAsync(AutomationCommandIds.GetStatus,
                    AutomationScope.ObserveStatus, new Dictionary<string, string>(),
                    string.Empty, null, cancellation.Token);
                Assert.IsTrue(aggregate.Success, aggregate.Detail);
                Assert.IsTrue(aggregate.Data.Count < IpcPayloadCodec.MaximumFieldCount);
                using var runtimeStatus = JsonDocument.Parse(aggregate.Data["runtimeStatusJson"]);
                Assert.AreEqual("-1", runtimeStatus.RootElement.GetProperty("movieTick").GetString());
                Assert.AreEqual("-1", aggregate.Data["runtime.movieTick"]);
                await using var client = new AutomationClient();
                await client.ConnectAsync(new AutomationConnectOptions
                {
                    ClientId = "operational-sdk", BootstrapPath = broker.BootstrapPath
                }, cancellation.Token);
                var status = await client.GetOperationalStatusAsync(cancellation.Token);
                Assert.IsTrue(status.Success, status.Detail);
                Assert.AreEqual("not-requested", status.Data["availability.semanticSnapshot"]);
                Assert.IsFalse(status.Data.ContainsKey("sha256"));
                var invalid = await client.ExecuteAsync(client.CreateCommand(
                    AutomationCommandIds.GetState, AutomationScope.ObserveStateSummary,
                    new Dictionary<string, string> { ["statusOnly"] = "false" }), cancellation.Token);
                Assert.IsFalse(invalid.Success);
                Assert.AreEqual("InvalidArguments", invalid.ResultCode);
                // Default request retains its full semantic contract.
                var full = await client.GetStateAsync(cancellation.Token);
                Assert.AreEqual(42L, full.MovieTick);
                Assert.AreEqual("companion-disconnected", full.Fields["lastStepInterruptionReason"]);
                Assert.AreEqual("10000", full.Fields["lastInterruptedStepRequestedTicks"]);
                Assert.AreEqual("9", full.Fields["lastInterruptedStepCommittedTicks"]);
                Assert.AreEqual("false", full.Fields["disconnectCleanupPending"]);
            }
            finally
            {
                cancellation.Cancel();
                try { await runtime; } catch (OperationCanceledException) { }
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public async Task SdkLeasePreconditionAndIdempotencyUseBroker()
        {
            var fixture = CreateFixture();
            using var runtimeCancellation =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(20));
            var fakeRuntime = RunFakeRuntimeAsync(
                fixture,
                runtimeCancellation.Token);
            using var sessions =
                new SessionRegistry("automation-integration");
            Assert.IsTrue(
                await sessions.RegisterAsync(
                    fixture.Registration,
                    runtimeCancellation.Token));
            using var broker = CreateBroker(sessions);
            AssertBootstrapAclIsCurrentUserOnly(
                broker.BootstrapPath);

            var humanState = await broker.ExecuteHumanAsync(
                AutomationCommandIds.GetState,
                AutomationScope.ObserveStateSummary,
                new Dictionary<string, string>(),
                string.Empty,
                null,
                runtimeCancellation.Token);
            Assert.IsTrue(humanState.Success, humanState.Detail);
            Assert.AreEqual("42", humanState.Data["movieTick"]);

            await using (var client = new AutomationClient())
            {
                var handshake = await client.ConnectAsync(
                    new AutomationConnectOptions
                    {
                        ClientId = "sdk-integration",
                        BootstrapPath = broker.BootstrapPath
                    },
                    runtimeCancellation.Token);
                Assert.AreEqual(
                    nameof(AutomationMode.ApprovedControl),
                    handshake.Mode);

                var state = await client.ExecuteAsync(
                    client.CreateCommand(
                        AutomationCommandIds.GetState,
                        AutomationScope.ObserveStateSummary),
                    runtimeCancellation.Token);
                Assert.IsTrue(state.Success);
                Assert.AreEqual("42", state.Data["movieTick"]);
                Assert.AreEqual(
                    SemanticHash,
                    state.Data["sha256"]);
                using (var canonicalState = JsonDocument.Parse(
                           state.Data["stateJson"]))
                {
                    Assert.AreEqual(
                        1,
                        canonicalState.RootElement
                            .GetProperty("schemaVersion")
                            .GetInt32());
                    Assert.AreEqual(
                        "Running",
                        canonicalState.RootElement
                            .GetProperty("runtimeMode")
                            .GetString());
                    Assert.AreEqual(
                        "Idle",
                        canonicalState.RootElement
                            .GetProperty("fields")
                            .GetProperty("playbackMode")
                            .GetString());
                    Assert.IsTrue(
                        canonicalState.RootElement
                            .GetProperty("activeCapabilities")
                            .EnumerateArray()
                            .Any(
                                item => item.GetString()
                                        == AutomationCommandIds.Pause));
                }
                var typedState =
                    await client.GetStateAsync(
                        runtimeCancellation.Token);
                Assert.AreEqual(42L, typedState.MovieTick);
                Assert.AreEqual(
                    SemanticHash,
                    typedState.SemanticSnapshotSha256);
                Assert.AreEqual(
                    "Idle",
                    typedState.Fields["playbackMode"]);
                Assert.AreEqual(
                    "existing-manual-reset-same-end-of-frame-guard-v1",
                    typedState.Fields["controlGateStrategyId"]);
                Assert.AreEqual(
                    "true",
                    typedState.Fields["usesCompletedFrameBoundaryGate"]);
                Assert.AreEqual(
                    "true",
                    typedState.Fields["deferredRecordingArmEnabled"]);
                Assert.AreEqual(
                    "candidate-test",
                    typedState.Fields["deferredRecordingArmRunId"]);
                Assert.AreEqual(
                    "true",
                    typedState.Fields["deferredRecordingArmPauseArmed"]);
                Assert.AreEqual(
                    "false",
                    typedState.Fields["deferredRecordingArmReplayArmed"]);
                Assert.AreEqual(
                    "false",
                    typedState.Fields["deferredRecordingArmReleaseConsumed"]);
                Assert.AreEqual(
                    "0",
                    typedState.Fields[
                        "deferredRecordingArmNeutralPreRollCompletedFrameCount"]);
                var semanticState =
                    await client.GetSemanticStateAsync(
                        runtimeCancellation.Token);
                Assert.IsTrue(
                    semanticState.Snapshot.TryGetInt32(
                        "fixture.value",
                        out var fixtureValue));
                Assert.AreEqual(42, fixtureValue);

                var firstTimelinePage =
                    await client.ExecuteAsync(
                        client.CreateCommand(
                            AutomationCommandIds.GetTimeline,
                            AutomationScope.ObserveTimeline,
                            new Dictionary<string, string>
                            {
                                ["count"] = "1",
                                ["fromMovieTick"] = "42"
                            }),
                        runtimeCancellation.Token);
                Assert.IsTrue(firstTimelinePage.Success);
                Assert.AreEqual(
                    "1",
                    firstTimelinePage.Data["count"]);
                var firstEntry =
                    firstTimelinePage.Data["entries"];
                var secondTimelinePage =
                    await client.ExecuteAsync(
                        client.CreateCommand(
                            AutomationCommandIds.GetTimeline,
                            AutomationScope.ObserveTimeline,
                            new Dictionary<string, string>
                            {
                                ["afterSequence"] =
                                    firstTimelinePage.Data[
                                        "nextAfterSequence"],
                                ["count"] = "1",
                                ["fromMovieTick"] = "42"
                            }),
                        runtimeCancellation.Token);
                Assert.IsTrue(secondTimelinePage.Success);
                Assert.AreEqual(
                    "1",
                    secondTimelinePage.Data["count"]);
                Assert.AreNotEqual(
                    firstEntry,
                    secondTimelinePage.Data["entries"]);

                var rejected = await client.ExecuteAsync(
                    client.CreateCommand(
                        AutomationCommandIds.Pause,
                        AutomationScope.ControlPlayback,
                        expectedRuntimeMode: "Running",
                        expectedMovieTick: 42),
                    runtimeCancellation.Token);
                Assert.AreEqual(
                    "LeaseRequired",
                    rejected.ResultCode);

                var acquired = await client.ExecuteAsync(
                    client.CreateCommand(
                        AutomationCommandIds.AcquireControl,
                        AutomationScope.ControlPlayback,
                        new Dictionary<string, string>
                        {
                            ["scopes"] =
                                AutomationScope.ControlPlayback
                                + ","
                                + AutomationScope.ControlReplaySave,
                            ["ttlSeconds"] = "30"
                        }),
                    runtimeCancellation.Token);
                Assert.IsTrue(acquired.Success);
                var leaseId = acquired.Data["leaseId"];
                var humanBlocked = await broker.ExecuteHumanAsync(
                    AutomationCommandIds.Pause,
                    AutomationScope.ControlPlayback,
                    new Dictionary<string, string>(),
                    "Running",
                    42,
                    runtimeCancellation.Token);
                Assert.AreEqual(
                    "LeaseBusy",
                    humanBlocked.ResultCode,
                    "Human UI must obey the same exclusive lease as AI clients.");
                var replaySaveLifecycle = new[]
                {
                    (
                        AutomationCommandIds
                            .ApproveReplaySaveOverwrite,
                        (IReadOnlyDictionary<string, string>)
                        new Dictionary<string, string>
                        {
                            ["approved"] = "true"
                        }),
                    (
                        AutomationCommandIds
                            .CancelReplaySaveRestore,
                        (IReadOnlyDictionary<string, string>)
                        new Dictionary<string, string>()),
                    (
                        AutomationCommandIds
                            .ResumeReplaySaveRestore,
                        (IReadOnlyDictionary<string, string>)
                        new Dictionary<string, string>())
                };
                foreach (var (commandId, arguments) in
                         replaySaveLifecycle)
                {
                    var lifecycleResult =
                        await client.ExecuteAsync(
                            client.CreateCommand(
                                commandId,
                                AutomationScope.ControlReplaySave,
                                arguments,
                                leaseId,
                                "Running",
                                42),
                            runtimeCancellation.Token);
                    Assert.IsTrue(
                        lifecycleResult.Success,
                        lifecycleResult.Detail);
                    Assert.AreEqual(
                        commandId,
                        lifecycleResult.Data["command"]);
                }

                var pause = client.CreateCommand(
                    AutomationCommandIds.Pause,
                    AutomationScope.ControlPlayback,
                    leaseId: leaseId,
                    expectedRuntimeMode: "Running",
                    expectedMovieTick: 42,
                    requestId: "pause-integration",
                    idempotencyKey: "pause-key");
                var first = await client.ExecuteAsync(
                    pause,
                    runtimeCancellation.Token);
                var replay = await client.ExecuteAsync(
                    pause,
                    runtimeCancellation.Token);
                Assert.IsTrue(first.Success);
                CollectionAssert.AreEqual(
                    first.ToPayload(),
                    replay.ToPayload());
                Assert.AreEqual(
                    "External control lease revoked by the user.",
                    broker.RevokeControlLeaseByUser());
                var humanPause = await broker.ExecuteHumanAsync(
                    AutomationCommandIds.Pause,
                    AutomationScope.ControlPlayback,
                    new Dictionary<string, string>(),
                    "Running",
                    42,
                    runtimeCancellation.Token);
                Assert.IsTrue(humanPause.Success, humanPause.Detail);
                Assert.AreEqual(
                    AutomationCommandIds.Pause,
                    humanPause.Data["command"]);
                var revoked = await client.ExecuteAsync(
                    client.CreateCommand(
                        AutomationCommandIds.Resume,
                        AutomationScope.ControlPlayback,
                        leaseId: leaseId,
                        expectedRuntimeMode: "Running",
                        expectedMovieTick: 42),
                    runtimeCancellation.Token);
                Assert.AreEqual(
                    "LeaseRequired",
                    revoked.ResultCode);
                Assert.IsTrue(
                    AutomationBootstrapDescriptor.TryParse(
                        File.ReadAllBytes(
                            broker.BootstrapPath),
                        out var bootstrap,
                        out var bootstrapError),
                    bootstrapError);
                var auditText = File.ReadAllText(
                    broker.AuditPath
                    ?? throw new InvalidOperationException(
                        "Audit path is unavailable."));
                Assert.IsFalse(
                    auditText.Contains(
                        "sdk-integration",
                        StringComparison.Ordinal));
                Assert.IsFalse(
                    auditText.Contains(
                        leaseId,
                        StringComparison.Ordinal));
                Assert.IsFalse(
                    auditText.Contains(
                        Convert.ToBase64String(
                            bootstrap!.Token),
                        StringComparison.Ordinal));
                Assert.IsFalse(
                    auditText.Contains(
                        Environment.UserName,
                        StringComparison.OrdinalIgnoreCase));
            }

            await using (var second = new AutomationClient())
            {
                await second.ConnectAsync(
                    new AutomationConnectOptions
                    {
                        ClientId = "sdk-integration-2",
                        BootstrapPath = broker.BootstrapPath
                    },
                    runtimeCancellation.Token);
                var replacement = await second.ExecuteAsync(
                    second.CreateCommand(
                        AutomationCommandIds.AcquireControl,
                        AutomationScope.ControlPlayback,
                        new Dictionary<string, string>
                        {
                            ["scopes"] =
                                AutomationScope.ControlPlayback
                        }),
                    runtimeCancellation.Token);
                Assert.IsTrue(
                    replacement.Success,
                    replacement.Detail);
            }

            runtimeCancellation.Cancel();
            try
            {
                await fakeRuntime;
            }
            catch (OperationCanceledException)
            {
            }

            Assert.AreEqual(
                2,
                fixture.PauseCount,
                "One external idempotent pause and one human pause should reach Runtime exactly once each.");
        }

        [TestMethod]
        [Timeout(30000)]
        public async Task CliAndMcpUseTheSameBrokerAndNonVisualState()
        {
            var fixture = CreateFixture();
            using var runtimeCancellation =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(20));
            var fakeRuntime = RunFakeRuntimeAsync(
                fixture,
                runtimeCancellation.Token);
            using var sessions =
                new SessionRegistry("automation-cli-mcp");
            Assert.IsTrue(
                await sessions.RegisterAsync(
                    fixture.Registration,
                    runtimeCancellation.Token));
            using var broker = CreateBroker(sessions);

            var bridgeAssembly =
                typeof(HollowKnightTAS.AgentBridge.McpStdioServer)
                    .Assembly.Location;
            var bridgeStart = DotnetStart(bridgeAssembly);
            bridgeStart.ArgumentList.Add(
                "--bootstrap=" + broker.BootstrapPath);
            using (var bridge = Process.Start(bridgeStart)
                                ?? throw new InvalidOperationException(
                                    "AgentBridge did not start."))
            {
                await bridge.StandardInput.WriteLineAsync(
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\","
                    + "\"params\":{\"protocolVersion\":\"2025-11-25\","
                    + "\"capabilities\":{},\"clientInfo\":{\"name\":\"test\","
                    + "\"version\":\"1\"}}}");
                await bridge.StandardInput.WriteLineAsync(
                    "{\"jsonrpc\":\"2.0\",\"method\":"
                    + "\"notifications/initialized\"}");
                await bridge.StandardInput.WriteLineAsync(
                    "{\"jsonrpc\":\"2.0\",\"id\":2,"
                    + "\"method\":\"tools/list\",\"params\":{}}");
                await bridge.StandardInput.WriteLineAsync(
                    "{\"jsonrpc\":\"2.0\",\"id\":3,"
                    + "\"method\":\"resources/read\",\"params\":{\"uri\":"
                    + "\"hktas://session/current/capabilities\"}}");
                await bridge.StandardInput.WriteLineAsync(
                    "{\"jsonrpc\":\"2.0\",\"id\":4,"
                    + "\"method\":\"resources/read\",\"params\":{\"uri\":"
                    + "\"file:///C:/Windows/win.ini\"}}");
                await bridge.StandardInput.WriteLineAsync(
                    "{\"jsonrpc\":\"2.0\",\"id\":5,"
                    + "\"method\":\"tools/call\",\"params\":{\"name\":"
                    + "\"hktas_pause\",\"arguments\":{"
                    + "\"shell\":\"powershell\"}}}");
                await bridge.StandardInput.WriteLineAsync(
                    "{\"jsonrpc\":\"2.0\",\"id\":6,"
                    + "\"method\":\"tools/call\",\"params\":{\"name\":"
                    + "\"hktas_get_capabilities\",\"arguments\":{}}}");
                bridge.StandardInput.Close();
                var stdout =
                    await bridge.StandardOutput.ReadToEndAsync();
                var stderr =
                    await bridge.StandardError.ReadToEndAsync();
                await bridge.WaitForExitAsync(
                    runtimeCancellation.Token);
                Assert.AreEqual(0, bridge.ExitCode, stderr);
                var lines = stdout.Split(
                    new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries);
                Assert.AreEqual(6, lines.Length);
                var documents = lines
                    .Select(line => JsonDocument.Parse(line))
                    .ToArray();
                try
                {
                    Assert.AreEqual(
                        "2025-11-25",
                        documents[0].RootElement
                            .GetProperty("result")
                            .GetProperty("protocolVersion")
                            .GetString());
                    Assert.IsTrue(
                        documents[1].RootElement
                            .GetProperty("result")
                            .GetProperty("tools")
                            .EnumerateArray()
                            .Any(
                                tool =>
                                    tool.GetProperty("name")
                                        .GetString()
                                    == "hktas_pause"));
                    var resourceText = documents[2].RootElement
                        .GetProperty("result")
                        .GetProperty("contents")[0]
                        .GetProperty("text")
                        .GetString()!;
                    using var resource =
                        JsonDocument.Parse(resourceText);
                    Assert.AreEqual(
                        "true",
                        resource.RootElement
                            .GetProperty("success")
                            .GetString());
                    Assert.AreEqual(
                        -32602,
                        documents[3].RootElement
                            .GetProperty("error")
                            .GetProperty("code")
                            .GetInt32());
                    Assert.IsTrue(
                        documents[4].RootElement
                            .GetProperty("result")
                            .GetProperty("isError")
                            .GetBoolean());
                    Assert.IsTrue(
                        documents[5].RootElement
                            .GetProperty("result")
                            .GetProperty("structuredContent")
                            .GetProperty("success")
                            .GetBoolean());
                }
                finally
                {
                    foreach (var document in documents)
                    {
                        document.Dispose();
                    }
                }
            }

            var cliAssembly =
                typeof(HollowKnightTAS.Cli.Program)
                    .Assembly.Location;
            var cliStart = DotnetStart(cliAssembly);
            cliStart.ArgumentList.Add("automation");
            cliStart.ArgumentList.Add("state");
            cliStart.ArgumentList.Add(
                "--bootstrap=" + broker.BootstrapPath);
            using (var cli = Process.Start(cliStart)
                             ?? throw new InvalidOperationException(
                                 "CLI did not start."))
            {
                var stdout = await cli.StandardOutput.ReadToEndAsync();
                var stderr = await cli.StandardError.ReadToEndAsync();
                await cli.WaitForExitAsync(
                    runtimeCancellation.Token);
                Assert.AreEqual(0, cli.ExitCode, stderr);
                using var result = JsonDocument.Parse(stdout);
                Assert.AreEqual(
                    42,
                    result.RootElement
                        .GetProperty("semanticValues")
                        .GetProperty("fixture.value")
                        .GetProperty("value")
                        .GetInt32());
            }

            runtimeCancellation.Cancel();
            try
            {
                await fakeRuntime;
            }
            catch (OperationCanceledException)
            {
            }
        }

        [TestMethod]
        [Timeout(60000)]
        public async Task UiSdkCliAndMcpAppendCanonicalInputThroughOneRuntimePath()
        {
            var fixture = CreateFixture();
            fixture.ControlMode = "Paused";
            using var cancellation =
                new CancellationTokenSource(TimeSpan.FromSeconds(50));
            var fakeRuntime = RunFakeRuntimeAsync(
                fixture,
                cancellation.Token);
            using var sessions =
                new SessionRegistry("automation-surface-ledger");
            Assert.IsTrue(
                await sessions.RegisterAsync(
                    fixture.Registration,
                    cancellation.Token));
            using var broker = CreateBroker(sessions);

            var snapshot = await broker.ExecuteHumanAsync(
                AutomationCommandIds.GetState,
                AutomationScope.ObserveStateSummary,
                null,
                string.Empty,
                null,
                cancellation.Token);
            Assert.IsTrue(snapshot.Success, snapshot.Detail);

            var humanMovie = CreateInputMovie("right");
            var human = await broker.ExecuteHumanAsync(
                AutomationCommandIds.StepWithInput,
                AutomationScope.ControlInput,
                new Dictionary<string, string>
                {
                    ["candidateMovieBase64"] =
                        Convert.ToBase64String(humanMovie),
                    ["expectedSceneEpoch"] = "0"
                },
                "Paused",
                42,
                cancellation.Token);
            Assert.IsTrue(human.Success, human.Detail);

            var sdkMovie = CreateInputMovie("left");
            await using (var sdk = new AutomationClient())
            {
                await sdk.ConnectAsync(
                    new AutomationConnectOptions
                    {
                        ClientId = "surface-sdk",
                        BootstrapPath = broker.BootstrapPath
                    },
                    cancellation.Token);
                var acquired = await sdk.ExecuteAsync(
                    sdk.CreateCommand(
                        AutomationCommandIds.AcquireControl,
                        AutomationScope.ControlInput,
                        new Dictionary<string, string>
                        {
                            ["scopes"] = AutomationScope.ControlInput
                        }),
                    cancellation.Token);
                Assert.IsTrue(acquired.Success, acquired.Detail);
                var sdkResult = await sdk.StepWithInputAsync(
                    sdkMovie,
                    0,
                    acquired.Data["leaseId"],
                    42,
                    cancellation.Token);
                Assert.IsTrue(sdkResult.Success, sdkResult.Detail);
                var released = await sdk.ExecuteAsync(
                    sdk.CreateCommand(
                        AutomationCommandIds.ReleaseControl,
                        AutomationScope.ControlInput,
                        leaseId: acquired.Data["leaseId"]),
                    cancellation.Token);
                Assert.IsTrue(released.Success, released.Detail);
            }

            var cliMovie = CreateInputMovie("jump");
            var cliAssembly =
                typeof(HollowKnightTAS.Cli.Program).Assembly.Location;
            var cliStart = DotnetStart(cliAssembly);
            foreach (var argument in new[]
                     {
                         "automation",
                         "call",
                         AutomationCommandIds.StepWithInput,
                         AutomationScope.ControlInput,
                         "candidateMovieBase64="
                         + Convert.ToBase64String(cliMovie),
                         "expectedSceneEpoch=0",
                         "--expected-mode=Paused",
                         "--expected-tick=42",
                         "--bootstrap=" + broker.BootstrapPath
                     })
            {
                cliStart.ArgumentList.Add(argument);
            }

            using (var cli = Process.Start(cliStart)
                             ?? throw new InvalidOperationException(
                                 "CLI surface did not start."))
            {
                var stdout = await cli.StandardOutput.ReadToEndAsync();
                var stderr = await cli.StandardError.ReadToEndAsync();
                await cli.WaitForExitAsync(cancellation.Token);
                Assert.AreEqual(0, cli.ExitCode, stderr);
                using var result = JsonDocument.Parse(stdout);
                Assert.AreEqual(
                    "true",
                    result.RootElement
                        .GetProperty("success")
                        .GetString());
            }

            var mcpMovie = CreateInputMovie("attack");
            await using (var fourth = new AutomationClient())
            {
                await fourth.ConnectAsync(
                    new AutomationConnectOptions
                    {
                        ClientId = "sdk-fourth",
                        BootstrapPath = broker.BootstrapPath
                    },
                    cancellation.Token);
                var acquired = await fourth.ExecuteAsync(
                    fourth.CreateCommand(
                        AutomationCommandIds.AcquireControl,
                        AutomationScope.ControlInput,
                        new Dictionary<string, string>(
                            StringComparer.Ordinal)
                        {
                            ["scopes"] = AutomationScope.ControlInput
                        }),
                    cancellation.Token);
                Assert.IsTrue(acquired.Success, acquired.Detail);
                var fourthResult = await fourth.StepWithInputAsync(
                    mcpMovie,
                    0,
                    acquired.Data["leaseId"],
                    42,
                    cancellation.Token);
                Assert.IsTrue(fourthResult.Success, fourthResult.Detail);
            }

            var bridgeAssembly =
                typeof(HollowKnightTAS.AgentBridge.McpStdioServer)
                    .Assembly.Location;
            var bridgeStart = DotnetStart(bridgeAssembly);
            bridgeStart.ArgumentList.Add(
                "--bootstrap=" + broker.BootstrapPath);
            using (var bridge = Process.Start(bridgeStart)
                                ?? throw new InvalidOperationException(
                                    "MCP surface did not start."))
            {
                await bridge.StandardInput.WriteLineAsync(
                    "{\"jsonrpc\":\"2.0\",\"id\":1,"
                    + "\"method\":\"initialize\",\"params\":{"
                    + "\"protocolVersion\":\"2025-11-25\","
                    + "\"capabilities\":{},\"clientInfo\":{"
                    + "\"name\":\"surface-ledger\","
                    + "\"version\":\"1\"}}}");
                await bridge.StandardInput.WriteLineAsync(
                    "{\"jsonrpc\":\"2.0\",\"method\":"
                    + "\"notifications/initialized\"}");
                await bridge.StandardInput.WriteLineAsync(
                    "{\"jsonrpc\":\"2.0\",\"id\":2,"
                    + "\"method\":\"tools/call\",\"params\":{"
                    + "\"name\":\"hktas_acquire_control\","
                    + "\"arguments\":{\"scopes\":["
                    + "\"control.input\"]}}}");
                await bridge.StandardInput.WriteLineAsync(
                    "{\"jsonrpc\":\"2.0\",\"id\":3,"
                    + "\"method\":\"tools/call\",\"params\":{"
                    + "\"name\":\"hktas_get_capabilities\","
                    + "\"arguments\":{}}}");
                bridge.StandardInput.Close();
                var stdout =
                    await bridge.StandardOutput.ReadToEndAsync();
                var stderr =
                    await bridge.StandardError.ReadToEndAsync();
                await bridge.WaitForExitAsync(cancellation.Token);
                Assert.AreEqual(0, bridge.ExitCode, stderr);
                var lines = stdout.Split(
                    new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries);
                Assert.AreEqual(3, lines.Length);
                using var result = JsonDocument.Parse(lines[2]);
                Assert.IsFalse(
                    result.RootElement
                        .GetProperty("result")
                        .GetProperty("isError")
                        .GetBoolean());
            }

            Assert.AreEqual(4, fixture.InputBatches.Count);
            var expected = new[]
            {
                TasAction.Right,
                TasAction.Left,
                TasAction.Jump,
                TasAction.Attack
            };
            var actual = fixture.InputBatches
                .Select(
                    (bytes, index) =>
                    {
                        var parsed = new MovieEditorService().Validate(
                            Encoding.UTF8.GetString(bytes),
                            "surface-"
                            + index.ToString(CultureInfo.InvariantCulture)
                            + ".hktas");
                        Assert.IsTrue(parsed.Success);
                        var frame = parsed.Document!.Commands
                            .OfType<FrameRunCommand>()
                            .Single();
                        Assert.AreEqual(1L, frame.FrameCount);
                        return frame.HeldActions;
                    })
                .ToArray();
            CollectionAssert.AreEqual(expected, actual);

            cancellation.Cancel();
            try
            {
                await fakeRuntime;
            }
            catch (OperationCanceledException)
            {
            }
        }

        [TestMethod]
        [Timeout(60000)]
        public async Task ScriptedAgentUsesIsolatedBranchesTenTimes()
        {
            var fixture = CreateFixture();
            using var cancellation =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(50));
            var fakeRuntime = RunFakeRuntimeAsync(
                fixture,
                cancellation.Token);
            using var sessions =
                new SessionRegistry("automation-scripted-agent");
            Assert.IsTrue(
                await sessions.RegisterAsync(
                    fixture.Registration,
                    cancellation.Token));
            using var broker = CreateBroker(sessions);
            await using var client = new AutomationClient();
            await client.ConnectAsync(
                new AutomationConnectOptions
                {
                    ClientId = "scripted-agent",
                    BootstrapPath = broker.BootstrapPath
                },
                cancellation.Token);

            var invalidBytes = Encoding.UTF8.GetBytes(
                "hktas 1\n"
                + "game 1.5.78.11833\n"
                + "api 1.5.78.11833-77\n"
                + "manifest-sha256 "
                + new string('a', 64)
                + "\nbaseline none none\n"
                + "tick-unit input\n---\n"
                + "frames 1 hold=teleport\n");
            for (var iteration = 0;
                 iteration < 10;
                 iteration++)
            {
                var state = await ExecuteReadAsync(
                    AutomationCommandIds.GetState,
                    AutomationScope.ObserveStateSummary);
                Assert.IsTrue(state.Success);
                var movie = await ExecuteReadAsync(
                    AutomationCommandIds.GetMovie,
                    AutomationScope.MovieRead);
                Assert.IsTrue(movie.Success);
                var baseMovieId = movie.Data["movieId"];
                var timeline = await ExecuteReadAsync(
                    AutomationCommandIds.GetTimeline,
                    AutomationScope.ObserveTimeline);
                Assert.IsTrue(timeline.Success);

                var invalid = await client.ExecuteAsync(
                    client.CreateCommand(
                        AutomationCommandIds.ValidateMoviePatch,
                        AutomationScope.MovieValidate,
                        new Dictionary<string, string>
                        {
                            ["candidateMovieBase64"] =
                                Convert.ToBase64String(
                                    invalidBytes)
                        }),
                    cancellation.Token);
                Assert.IsFalse(invalid.Success);
                Assert.AreEqual(
                    "MovieInvalid",
                    invalid.ResultCode);

                var candidate = CreateAgentMovie(
                    iteration + 2,
                    iteration);
                var proposed = await client.ExecuteAsync(
                    client.CreateCommand(
                        AutomationCommandIds.ProposeMoviePatch,
                        AutomationScope.MoviePropose,
                        new Dictionary<string, string>
                        {
                            ["baseMovieId"] = baseMovieId,
                            ["candidateMovieBase64"] =
                                Convert.ToBase64String(
                                    candidate),
                            ["expectedMilestone"] =
                                "fixture-complete",
                            ["reason"] =
                                "deterministic scripted agent iteration "
                                + iteration.ToString(
                                    CultureInfo.InvariantCulture)
                        }),
                    cancellation.Token);
                Assert.IsTrue(
                    proposed.Success,
                    proposed.Detail);
                var branchMovieId =
                    proposed.Data["branchMovieId"];

                var unchanged = await ExecuteReadAsync(
                    AutomationCommandIds.GetMovie,
                    AutomationScope.MovieRead);
                Assert.AreEqual(
                    baseMovieId,
                    unchanged.Data["movieId"],
                    "Proposal silently changed the current movie.");

                var acquired = await client.ExecuteAsync(
                    client.CreateCommand(
                        AutomationCommandIds.AcquireControl,
                        AutomationScope.MovieApplyBranch,
                        new Dictionary<string, string>
                        {
                            ["scopes"] =
                                AutomationScope.MovieApplyBranch
                                + ","
                                + AutomationScope.ControlPlayback
                        }),
                    cancellation.Token);
                Assert.IsTrue(acquired.Success);
                var leaseId = acquired.Data["leaseId"];
                var applied = await client.ExecuteAsync(
                    client.CreateCommand(
                        AutomationCommandIds.ApplyMovieBranch,
                        AutomationScope.MovieApplyBranch,
                        new Dictionary<string, string>
                        {
                            ["branchMovieId"] = branchMovieId
                        },
                        leaseId,
                        "Running",
                        42),
                    cancellation.Token);
                Assert.IsTrue(applied.Success, applied.Detail);
                var current = await ExecuteReadAsync(
                    AutomationCommandIds.GetMovie,
                    AutomationScope.MovieRead);
                Assert.AreEqual(
                    branchMovieId,
                    current.Data["movieId"]);

                var replay = await client.ExecuteAsync(
                    client.CreateCommand(
                        AutomationCommandIds.StartReplay,
                        AutomationScope.ControlPlayback,
                        leaseId: leaseId,
                        expectedRuntimeMode: "Running",
                        expectedMovieTick: 42),
                    cancellation.Token);
                Assert.IsTrue(replay.Success, replay.Detail);
                var comparison = await ExecuteReadAsync(
                    AutomationCommandIds.GetTimeline,
                    AutomationScope.ObserveTimeline);
                Assert.IsTrue(
                    comparison.Data["entries"].Contains(
                        IpcMessageTypes.Milestone,
                        StringComparison.Ordinal));

                var released = await client.ExecuteAsync(
                    client.CreateCommand(
                        AutomationCommandIds.ReleaseControl,
                        AutomationScope.MovieApplyBranch,
                        leaseId: leaseId),
                    cancellation.Token);
                Assert.IsTrue(released.Success);
            }

            Assert.AreEqual(10, fixture.ApplyCount);
            Assert.AreEqual(10, fixture.StartReplayCount);
            cancellation.Cancel();
            try
            {
                await fakeRuntime;
            }
            catch (OperationCanceledException)
            {
            }

            async Task<AutomationResultEnvelope> ExecuteReadAsync(
                string commandId,
                string scope)
            {
                return await client.ExecuteAsync(
                    client.CreateCommand(commandId, scope),
                    cancellation.Token);
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public async Task StopRecordingRefreshesParentForImmediateHistoryEdit()
        {
            var fixture = CreateFixture();
            using var cancellation =
                new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var fakeRuntime = RunFakeRuntimeAsync(
                fixture,
                cancellation.Token);
            using var sessions =
                new SessionRegistry("automation-recording-parent");
            Assert.IsTrue(
                await sessions.RegisterAsync(
                    fixture.Registration,
                    cancellation.Token));
            using var broker = CreateBroker(sessions);
            await using var client = new AutomationClient();
            await client.ConnectAsync(
                new AutomationConnectOptions
                {
                    ClientId = "recording-parent",
                    BootstrapPath = broker.BootstrapPath
                },
                cancellation.Token);

            var before = await client.GetMovieAsync(cancellation.Token);
            Assert.IsTrue(before.Success, before.Detail);
            var initialMovieId = before.Data["movieId"];
            var acquired = await client.ExecuteAsync(
                client.CreateCommand(
                    AutomationCommandIds.AcquireControl,
                    AutomationScope.ControlRecording,
                    new Dictionary<string, string>
                    {
                        ["scopes"] =
                            AutomationScope.ControlRecording
                    }),
                cancellation.Token);
            Assert.IsTrue(acquired.Success, acquired.Detail);
            var leaseId = acquired.Data["leaseId"];
            var started = await client.StartRecordingAsync(
                leaseId,
                "Running",
                42,
                cancellation.Token);
            Assert.IsTrue(started.Success, started.Detail);
            var stopped = await client.StopRecordingAsync(
                leaseId,
                "Running",
                42,
                cancellation.Token);
            Assert.IsTrue(stopped.Success, stopped.Detail);
            var recordedMovieId = stopped.Data["movieId"];
            Assert.AreNotEqual(initialMovieId, recordedMovieId);

            var edited = await client.DeleteInputRangeAsync(
                recordedMovieId,
                3,
                1,
                cancellation.Token);
            Assert.IsTrue(
                edited.Success,
                "Immediate edit must use the newly recorded movie: "
                + edited.ResultCode
                + ": "
                + edited.Detail);
            Assert.AreEqual(recordedMovieId, edited.Data["parentMovieId"]);

            cancellation.Cancel();
            try
            {
                await fakeRuntime;
            }
            catch (OperationCanceledException)
            {
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public async Task DisabledAndReadOnlyModesFailClosed()
        {
            await VerifyDisabledAsync();
            await VerifyReadOnlyAsync();

            async Task VerifyDisabledAsync()
            {
                var fixture = CreateFixture(
                    AutomationMode.Disabled);
                using var cancellation =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(15));
                var fakeRuntime = RunFakeRuntimeAsync(
                    fixture,
                    cancellation.Token);
                using var sessions =
                    new SessionRegistry(
                        "automation-disabled");
                Assert.IsTrue(
                    await sessions.RegisterAsync(
                        fixture.Registration,
                        cancellation.Token));
                using (var broker =
                       CreateBroker(sessions))
                {
                    Assert.IsNull(broker.Bootstrap);
                    Assert.IsFalse(
                        File.Exists(broker.BootstrapPath));
                    Assert.IsTrue(
                        sessions.Sessions.Single()
                            .IsConnected);
                }

                cancellation.Cancel();
                try
                {
                    await fakeRuntime;
                }
                catch (OperationCanceledException)
                {
                }
            }

            async Task VerifyReadOnlyAsync()
            {
                var fixture = CreateFixture(
                    AutomationMode.ReadOnly);
                using var cancellation =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(15));
                var fakeRuntime = RunFakeRuntimeAsync(
                    fixture,
                    cancellation.Token);
                using var sessions =
                    new SessionRegistry(
                        "automation-read-only");
                Assert.IsTrue(
                    await sessions.RegisterAsync(
                        fixture.Registration,
                        cancellation.Token));
                using var broker =
                    CreateBroker(sessions);
                Assert.AreEqual(
                    AutomationMode.ReadOnly,
                    broker.Bootstrap!.Mode);
                await using var client =
                    new AutomationClient();
                await client.ConnectAsync(
                    new AutomationConnectOptions
                    {
                        ClientId = "read-only-client",
                        BootstrapPath =
                            broker.BootstrapPath
                    },
                    cancellation.Token);
                var state = await client.ExecuteAsync(
                    client.CreateCommand(
                        AutomationCommandIds.GetState,
                        AutomationScope.ObserveStateSummary),
                    cancellation.Token);
                Assert.IsTrue(state.Success);
                var acquire = await client.ExecuteAsync(
                    client.CreateCommand(
                        AutomationCommandIds.AcquireControl,
                        AutomationScope.ControlPlayback,
                        new Dictionary<string, string>
                        {
                            ["scopes"] =
                                AutomationScope.ControlPlayback
                        }),
                    cancellation.Token);
                Assert.AreEqual(
                    "ControlNotApproved",
                    acquire.ResultCode);
                var capabilities =
                    await client.ExecuteAsync(
                        client.CreateCommand(
                            AutomationCommandIds.GetCapabilities,
                            AutomationScope.ObserveStatus),
                        cancellation.Token);
                using var catalog = JsonDocument.Parse(
                    capabilities.Data["catalogJson"]);
                Assert.IsTrue(
                    catalog.RootElement
                        .EnumerateArray()
                        .Where(
                            item => item.GetProperty("requiresLease")
                                .GetBoolean())
                        .All(
                            item => item
                                .GetProperty("availability")
                                .GetString()
                                == "disabled"));

                cancellation.Cancel();
                try
                {
                    await fakeRuntime;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        [TestMethod]
        [Timeout(60000)]
        public async Task RemovedMutationsAreRejectedEvenWhenLegacySessionEnablesThem()
        {
            var fixture = CreateFixture(AutomationMode.ApprovedControl, debugMutationEnabled: true);
            fixture.ControlMode = "Paused";
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            var fakeRuntime = RunFakeRuntimeAsync(fixture, cancellation.Token);
            using var sessions = new SessionRegistry("automation-mutation-removed");
            Assert.IsTrue(await sessions.RegisterAsync(fixture.Registration, cancellation.Token));
            using var broker = CreateBroker(sessions);
            await using var client = new AutomationClient();
            try
            {
                await client.ConnectAsync(new AutomationConnectOptions
                {
                    ClientId = "legacy-mutation-sdk", BootstrapPath = broker.BootstrapPath
                }, cancellation.Token);
                foreach (var (command, scope, arguments) in new[]
                {
                    (AutomationCommandIds.SetHeroPose, AutomationScope.DebugStatePose, HeroPoseArguments()),
                    (AutomationCommandIds.SetPlayerResources, AutomationScope.DebugStateResources,
                        new Dictionary<string, string> { ["health"] = "9", ["soul"] = "99",
                            ["expectedMovieTick"] = "42", ["expectedSnapshotSha256"] = SemanticHash })
                })
                {
                    var result = await client.ExecuteAsync(client.CreateCommand(command, scope, arguments,
                        expectedRuntimeMode: "Paused", expectedMovieTick: 42), cancellation.Token);
                    Assert.IsFalse(result.Success);
                    Assert.AreEqual("CapabilityMismatch", result.ResultCode);
                }
                Assert.AreEqual(0, fixture.MutationCommitCount);
            }
            finally
            {
                cancellation.Cancel();
                try { await fakeRuntime; } catch (OperationCanceledException) { }
            }
        }

        [TestMethod]
        [Timeout(60000)]
        public async Task EvictedControlIdempotencyKeyFailsClosed()
        {
            var fixture = CreateFixture();
            using var cancellation =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(50));
            var fakeRuntime = RunFakeRuntimeAsync(
                fixture,
                cancellation.Token);
            using var sessions =
                new SessionRegistry("automation-idempotency-budget");
            Assert.IsTrue(
                await sessions.RegisterAsync(
                    fixture.Registration,
                    cancellation.Token));
            using var broker = CreateBroker(sessions);

            await using (var client = new AutomationClient())
            {
                await client.ConnectAsync(
                    new AutomationConnectOptions
                    {
                        ClientId = "idempotency-budget-client",
                        BootstrapPath = broker.BootstrapPath
                    },
                    cancellation.Token);
                var arguments = new Dictionary<string, string>
                {
                    ["scopes"] =
                        AutomationScope.ControlPlayback
                };
                var first = client.CreateCommand(
                    AutomationCommandIds.AcquireControl,
                    AutomationScope.ControlPlayback,
                    arguments,
                    requestId: "idempotency-budget-0",
                    idempotencyKey: "idempotency-budget-key-0");
                Assert.IsTrue(
                    (await client.ExecuteAsync(
                        first,
                        cancellation.Token)).Success);

                for (var iteration = 1;
                     iteration <= 2048;
                     iteration++)
                {
                    var suffix = iteration.ToString(
                        CultureInfo.InvariantCulture);
                    var result = await client.ExecuteAsync(
                        client.CreateCommand(
                            AutomationCommandIds.AcquireControl,
                            AutomationScope.ControlPlayback,
                            arguments,
                            requestId:
                                "idempotency-budget-" + suffix,
                            idempotencyKey:
                                "idempotency-budget-key-" + suffix),
                        cancellation.Token);
                    Assert.AreEqual(
                        "LeaseBusy",
                        result.ResultCode);
                }

                Assert.IsTrue(
                    broker.IdempotencyResultCount <= 2048);
                Assert.IsTrue(
                    broker.IdempotencyRetainedBytes
                    <= 8L * 1024 * 1024);
                Assert.IsTrue(
                    broker.ExpiredIdempotencyKeyCount > 0);
                var expired = await client.ExecuteAsync(
                    first,
                    cancellation.Token);
                Assert.IsFalse(expired.Success);
                Assert.AreEqual(
                    "IdempotencyExpired",
                    expired.ResultCode);
            }

            cancellation.Cancel();
            try
            {
                await fakeRuntime;
            }
            catch (OperationCanceledException)
            {
            }
        }

        [TestMethod]
        [Timeout(45000)]
        public async Task TimelineRetentionUsesPayloadByteBudgetAndSignalsGap()
        {
            var fixture = CreateFixture();
            fixture.WatchPayloadPadding =
                new string('x', 128 * 1024);
            using var cancellation =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(35));
            var fakeRuntime = RunFakeRuntimeAsync(
                fixture,
                cancellation.Token);
            using var sessions =
                new SessionRegistry("automation-byte-budget");
            Assert.IsTrue(
                await sessions.RegisterAsync(
                    fixture.Registration,
                    cancellation.Token));
            using var broker = CreateBroker(sessions);

            await using (var client = new AutomationClient())
            {
                await client.ConnectAsync(
                    new AutomationConnectOptions
                    {
                        ClientId = "byte-budget-reader",
                        BootstrapPath = broker.BootstrapPath
                    },
                    cancellation.Token);
                for (var iteration = 0;
                     iteration < 48;
                     iteration++)
                {
                    var state = await client.ExecuteAsync(
                        client.CreateCommand(
                            AutomationCommandIds.GetState,
                            AutomationScope
                                .ObserveStateSummary),
                        cancellation.Token);
                    Assert.IsTrue(state.Success);
                }

                Assert.IsTrue(
                    broker.TimelineRetainedPayloadBytes
                    <= 8L * 1024 * 1024);
                Assert.IsTrue(
                    broker.TimelineCount < 96,
                    "The byte budget did not evict large timeline items.");
                Assert.AreEqual(
                    0,
                    broker.IdempotencyResultCount,
                    "Read-only responses must not occupy idempotency storage.");
                Assert.AreEqual(
                    0L,
                    broker.IdempotencyRetainedBytes);

                var timeline = await client.ExecuteAsync(
                    client.CreateCommand(
                        AutomationCommandIds.GetTimeline,
                        AutomationScope.ObserveTimeline,
                        new Dictionary<string, string>
                        {
                            ["afterSequence"] = "0",
                            ["count"] = "1",
                            ["fromMovieTick"] = "0"
                        }),
                    cancellation.Token);
                Assert.IsTrue(timeline.Success);
                Assert.AreEqual(
                    "true",
                    timeline.Data["gapBeforeWindow"]);
            }

            cancellation.Cancel();
            try
            {
                await fakeRuntime;
            }
            catch (OperationCanceledException)
            {
            }
        }

        [TestMethod]
        [TestCategory("T15Soak")]
        [Timeout(180000)]
        public async Task TenThousandReadsAndOneHundredReconnectsStayBounded()
        {
            var fixture = CreateFixture();
            using var cancellation =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(170));
            var fakeRuntime = RunFakeRuntimeAsync(
                fixture,
                cancellation.Token);
            using var sessions =
                new SessionRegistry("automation-soak");
            Assert.IsTrue(
                await sessions.RegisterAsync(
                    fixture.Registration,
                    cancellation.Token));
            using var broker = CreateBroker(sessions);

            await using (var client = new AutomationClient())
            {
                await client.ConnectAsync(
                    new AutomationConnectOptions
                    {
                        ClientId = "soak-reader",
                        BootstrapPath = broker.BootstrapPath
                    },
                    cancellation.Token);
                for (var iteration = 0;
                     iteration < 10000;
                     iteration++)
                {
                    var state = await client.ExecuteAsync(
                        client.CreateCommand(
                            AutomationCommandIds.GetState,
                            AutomationScope
                                .ObserveStateSummary),
                        cancellation.Token);
                    Assert.IsTrue(
                        state.Success,
                        "Read "
                        + iteration.ToString(
                            CultureInfo.InvariantCulture)
                        + " failed: "
                        + state.ResultCode);
                }
            }

            Assert.AreEqual(
                0,
                broker.IdempotencyResultCount,
                "Observation traffic must not retain full responses.");
            Assert.AreEqual(0L, broker.IdempotencyRetainedBytes);
            Assert.AreEqual(5000, broker.TimelineCount);
            Assert.IsTrue(
                broker.TimelineRetainedPayloadBytes
                <= 8L * 1024 * 1024);
            for (var reconnect = 0;
                 reconnect < 100;
                 reconnect++)
            {
                await using var client =
                    new AutomationClient();
                await client.ConnectAsync(
                    new AutomationConnectOptions
                    {
                        ClientId =
                            "reconnect-"
                            + reconnect.ToString(
                                CultureInfo.InvariantCulture),
                        BootstrapPath = broker.BootstrapPath
                    },
                    cancellation.Token);
                var status = await client.ExecuteAsync(
                    client.CreateCommand(
                        AutomationCommandIds.GetStatus,
                        AutomationScope.ObserveStatus),
                    cancellation.Token);
                Assert.IsTrue(status.Success);
            }

            await using (var leaseOwner =
                         new AutomationClient())
            {
                await leaseOwner.ConnectAsync(
                    new AutomationConnectOptions
                    {
                        ClientId = "soak-lease-owner",
                        BootstrapPath = broker.BootstrapPath
                    },
                    cancellation.Token);
                var lease = await leaseOwner.ExecuteAsync(
                    leaseOwner.CreateCommand(
                        AutomationCommandIds.AcquireControl,
                        AutomationScope.ControlPlayback,
                        new Dictionary<string, string>
                        {
                            ["scopes"] =
                                AutomationScope.ControlPlayback
                        }),
                    cancellation.Token);
                Assert.IsTrue(lease.Success);
            }

            await using (var replacement =
                         new AutomationClient())
            {
                await replacement.ConnectAsync(
                    new AutomationConnectOptions
                    {
                        ClientId = "soak-replacement",
                        BootstrapPath = broker.BootstrapPath
                    },
                    cancellation.Token);
                var lease = await replacement.ExecuteAsync(
                    replacement.CreateCommand(
                        AutomationCommandIds.AcquireControl,
                        AutomationScope.ControlPlayback,
                        new Dictionary<string, string>
                        {
                            ["scopes"] =
                                AutomationScope.ControlPlayback
                        }),
                    cancellation.Token);
                Assert.IsTrue(
                    lease.Success,
                    "Disconnect did not release the lease.");
            }

            Assert.IsTrue(
                File.ReadLines(
                        broker.AuditPath
                        ?? throw new InvalidOperationException(
                            "Audit path is unavailable."))
                    .Take(10100)
                    .Count()
                >= 10100);
            cancellation.Cancel();
            try
            {
                await fakeRuntime;
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static Fixture CreateFixture(
            AutomationMode mode =
                AutomationMode.ApprovedControl,
            bool debugMutationEnabled = false)
        {
            var token = new byte[32];
            RandomNumberGenerator.Fill(token);
            var suffix = Guid.NewGuid().ToString("N");
            return new Fixture(
                new CompanionSessionRegistration(
                    "automation-test-" + suffix,
                    Environment.ProcessId,
                    Process.GetCurrentProcess()
                        .StartTime
                        .ToUniversalTime()
                        .Ticks,
                    new string('a', 64),
                    new string('b', 64),
                    new string('c', 64),
                    "HollowKnightTAS.Runtime.AutomationTest."
                    + suffix,
                    CompanionProtocolMetadata.SupportedProtocols,
                    false,
                    token,
                    mode,
                    debugMutationEnabled));
        }

        private static async Task RunFakeRuntimeAsync(
            Fixture fixture,
            CancellationToken token)
        {
            using var server = new NamedPipeServerStream(
                fixture.Registration.PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous
                | PipeOptions.CurrentUserOnly);
            await server.WaitForConnectionAsync(token);
            var hello = await IpcCodec.ReadFrameAsync(
                server,
                token);
            var validation = IpcHandshakeValidator.ValidateHello(
                hello,
                fixture.Registration.SessionId,
                fixture.Registration.GameProcessId,
                fixture.Registration.ProtocolRange,
                fixture.Registration.Token);
            Assert.IsTrue(validation.Success, validation.Error);
            var nonce = new byte[32];
            RandomNumberGenerator.Fill(nonce);
            await IpcCodec.WriteFrameAsync(
                server,
                new IpcEnvelope(
                    1,
                    fixture.Registration.SessionId,
                    0,
                    IpcMessageTypes.HelloAck,
                    IpcPayloadCodec.Serialize(
                        new Dictionary<string, string>
                        {
                            ["companionInstanceId"] =
                                validation.CompanionInstanceId,
                            ["protocol"] = "1",
                            ["runtimeSessionId"] =
                                fixture.Registration.SessionId,
                            ["serverNonce"] =
                                Convert.ToBase64String(nonce)
                        })),
                token);

            long outgoing = 0;
            while (!token.IsCancellationRequested)
            {
                IpcEnvelope command;
                try
                {
                    command = await IpcCodec.ReadFrameAsync(
                        server,
                        token);
                }
                catch (System.IO.EndOfStreamException)
                {
                    return;
                }

                var payload = IpcPayloadCodec.TryDeserialize(
                    command.PayloadUtf8).Fields!;
                var requestId = payload["requestId"];
                if (command.MessageType
                    == IpcMessageTypes.RequestSnapshot)
                {
                    if (payload.TryGetValue("statusOnly", out var statusOnly) && statusOnly == "true")
                    {
                        await SendAsync(IpcMessageTypes.RuntimeStatus, new Dictionary<string, string>
                        {
                            ["requestId"] = requestId,
                            ["capturedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                            ["controlMode"] = fixture.ControlMode,
                            ["movieTick"] = fixture.OperationalMovieTick,
                            ["autoSaveEnabled"] = "false",
                            ["autoSaveIntervalMovieTicks"] = "18000",
                            ["autoSaveRetentionCount"] = "20"
                        });
                        continue;
                    }
                    await SendAsync(
                        IpcMessageTypes.WatchFrame,
                        new Dictionary<string, string>
                        {
                            ["json"] =
                                "{\"watch\":\"snapshot-first\","
                                + "\"padding\":\""
                                + fixture.WatchPayloadPadding
                                + "\"}",
                            ["movieTick"] = "42",
                            ["sequence"] = "3"
                        });
                    await SendAsync(
                        IpcMessageTypes.WatchFrame,
                        new Dictionary<string, string>
                        {
                            ["json"] =
                                "{\"watch\":\"snapshot-second\","
                                + "\"padding\":\""
                                + fixture.WatchPayloadPadding
                                + "\"}",
                            ["movieTick"] = "42",
                            ["sequence"] = "4"
                        });
                    await SendAsync(
                        IpcMessageTypes.RuntimeStatus,
                        new Dictionary<string, string>
                        {
                            ["capturedAtUtc"] =
                                DateTimeOffset.UtcNow.ToString(
                                    "O",
                                    CultureInfo.InvariantCulture),
                            ["controlMode"] =
                                fixture.ControlMode,
                            ["json"] = SemanticJson,
                            ["movieTick"] = "42",
                            ["phase"] = "LateUpdateEnd",
                            ["lastStepInterruptionReason"] = "companion-disconnected",
                            ["lastInterruptedStepRequestedTicks"] = "10000",
                            ["lastInterruptedStepCommittedTicks"] = "9",
                            ["disconnectCleanupPending"] = "false",
                            ["playbackMode"] = "Idle",
                            ["pausedBoundaryWaitCount"] = "0",
                            ["pausedBoundaryPumpCount"] = "0",
                            ["controlGateStrategyId"] =
                                "existing-manual-reset-same-end-of-frame-guard-v1",
                            ["usesCompletedFrameBoundaryGate"] = "true",
                            ["deferredRecordingArmEnabled"] = "true",
                            ["deferredRecordingArmRunId"] =
                                "candidate-test",
                            ["deferredRecordingArmReleaseConsumed"] =
                                "false",
                            ["deferredRecordingArmPauseArmed"] = "true",
                            ["deferredRecordingArmReplayArmed"] = "false",
                            ["deferredRecordingArmNeutralPreRollCompletedFrameCount"] =
                                "0",
                            ["deferredRecordingArmActivationAttempted"] =
                                "false",
                            ["deferredRecordingArmActivationSucceeded"] =
                                "false",
                            ["deferredRecordingArmActivationCount"] = "0",
                            ["deferredRecordingArmActivationError"] =
                                string.Empty,
                            ["requestId"] = requestId,
                            ["sha256"] = SemanticHash,
                            ["verificationEligibility"] =
                                fixture.VerificationEligibility
                        });
                }
                else if (command.MessageType == IpcMessageTypes.LoadGameSlot)
                {
                    fixture.LoadedSlot = payload["slot"];
                    await SendAcceptedAsync(command.MessageType, requestId);
                }
                else if (command.MessageType == IpcMessageTypes.Pause)
                {
                    fixture.PauseCount++;
                    await SendAcceptedAsync(
                        command.MessageType,
                        requestId);
                }
                else if (command.MessageType
                         == IpcMessageTypes.StopRecording)
                {
                    fixture.CurrentMovieBytes = CreateAgentMovie(4, 99);
                    var parsed = new MovieEditorService().Validate(
                        Encoding.UTF8.GetString(
                            fixture.CurrentMovieBytes),
                        "fixture-recorded.hktas");
                    fixture.CurrentMovieId = parsed.MovieId;
                    await SendAsync(
                        IpcMessageTypes.MovieDocument,
                        new Dictionary<string, string>
                        {
                            ["available"] = "true",
                            ["movieBase64"] =
                                Convert.ToBase64String(
                                    fixture.CurrentMovieBytes),
                            ["movieId"] = fixture.CurrentMovieId,
                            ["requestId"] = requestId,
                            ["source"] = "recorded"
                        });
                }
                else if (command.MessageType
                         == IpcMessageTypes.RequestMovie)
                {
                    await SendAsync(
                        IpcMessageTypes.MovieDocument,
                        new Dictionary<string, string>
                        {
                            ["available"] = "true",
                            ["movieBase64"] =
                                Convert.ToBase64String(
                                    fixture.CurrentMovieBytes),
                            ["movieId"] =
                                fixture.CurrentMovieId,
                            ["requestId"] = requestId,
                            ["source"] = "fixture"
                        });
                }
                else if (command.MessageType
                         == IpcMessageTypes.UploadMovieBegin)
                {
                    fixture.PendingUpload =
                        new MemoryStream();
                    fixture.PendingMovieId = payload["movieId"];
                    await SendAcceptedAsync(
                        command.MessageType,
                        requestId);
                }
                else if (command.MessageType
                         == IpcMessageTypes.UploadMovieChunk)
                {
                    var chunk = Convert.FromBase64String(
                        payload["base64"]);
                    fixture.PendingUpload!.Write(
                        chunk,
                        0,
                        chunk.Length);
                    await SendAcceptedAsync(
                        command.MessageType,
                        requestId);
                }
                else if (command.MessageType
                         == IpcMessageTypes.UploadMovieEnd)
                {
                    fixture.CurrentMovieBytes =
                        fixture.PendingUpload!.ToArray();
                    fixture.CurrentMovieId =
                        fixture.PendingMovieId;
                    fixture.PendingUpload.Dispose();
                    fixture.PendingUpload = null;
                    fixture.ApplyCount++;
                    await SendAcceptedAsync(
                        command.MessageType,
                        requestId);
                }
                else if (command.MessageType
                         == IpcMessageTypes.StartReplay)
                {
                    fixture.StartReplayCount++;
                    await SendAsync(
                        IpcMessageTypes.Milestone,
                        new Dictionary<string, string>
                        {
                            ["movieId"] =
                                fixture.CurrentMovieId,
                            ["movieTick"] = "42",
                            ["name"] = "fixture-complete",
                            ["verification"] = "matched"
                        });
                    await SendAcceptedAsync(
                        command.MessageType,
                        requestId);
                }
                else if (command.MessageType
                         == IpcMessageTypes.RunInputBatch)
                {
                    fixture.InputBatches.Add(
                        (byte[])fixture.CurrentMovieBytes.Clone());
                    await SendAcceptedAsync(
                        command.MessageType,
                        requestId);
                }
                else if (command.MessageType
                         == IpcMessageTypes.SetHeroPose
                         || command.MessageType
                         == IpcMessageTypes
                             .SetPlayerResources)
                {
                    if (payload["expectedMovieTick"] != "42"
                        || payload[
                               "expectedSnapshotSha256"]
                           != SemanticHash)
                    {
                        await SendRejectedAsync(
                            command.MessageType,
                            requestId,
                            "PreconditionFailed",
                            "Mutation compare-and-set is stale.");
                        continue;
                    }

                    if (command.MessageType
                        == IpcMessageTypes.SetHeroPose
                        && (!TryFinite(
                                payload["positionX"])
                            || !TryFinite(
                                payload["positionY"])
                            || !TryFinite(
                                payload["velocityX"])
                            || !TryFinite(
                                payload["velocityY"])))
                    {
                        await SendRejectedAsync(
                            command.MessageType,
                            requestId,
                            "InvalidMutationValue",
                            "Pose values must be finite.");
                        continue;
                    }

                    if (command.MessageType
                        == IpcMessageTypes.SetPlayerResources
                        && (!int.TryParse(
                                payload["health"],
                                NumberStyles.None,
                                CultureInfo.InvariantCulture,
                                out var health)
                            || !int.TryParse(
                                payload["soul"],
                                NumberStyles.None,
                                CultureInfo.InvariantCulture,
                                out var soul)
                            || health < 1
                            || health > 9
                            || soul < 0
                            || soul > 99))
                    {
                        await SendRejectedAsync(
                            command.MessageType,
                            requestId,
                            "InvalidMutationValue",
                            "Resource values are out of bounds.");
                        continue;
                    }

                    fixture.PendingMutationId =
                        "mutation-"
                        + Guid.NewGuid().ToString("N");
                    await SendAsync(
                        IpcMessageTypes.StateMutationResult,
                        new Dictionary<string, string>
                        {
                            ["adapterId"] =
                                command.MessageType
                                == IpcMessageTypes.SetHeroPose
                                    ? "hero-pose"
                                    : "player-resources",
                            ["afterSha256"] =
                                AfterSemanticHash,
                            ["beforeSha256"] =
                                SemanticHash,
                            ["movieTick"] = "42",
                            ["requestId"] = requestId,
                            ["transactionId"] =
                                fixture.PendingMutationId,
                            ["typedDiff"] = "fixture-diff",
                            ["verificationEligibility"] =
                                "PendingDebugMutationCommit"
                        });
                }
                else if (command.MessageType
                         == IpcMessageTypes
                             .CommitStateMutation)
                {
                    if (fixture.PendingMutationId
                        != payload["transactionId"])
                    {
                        await SendRejectedAsync(
                            command.MessageType,
                            requestId,
                            "TransactionMismatch",
                            "Mutation transaction is not pending.");
                    }
                    else if (fixture
                             .RejectNextMutationCommit)
                    {
                        fixture.RejectNextMutationCommit =
                            false;
                        fixture.PendingMutationId =
                            string.Empty;
                        fixture.RollbackCount++;
                        await SendRejectedAsync(
                            command.MessageType,
                            requestId,
                            "CommitRejected",
                            "Fixture rolled the mutation back.");
                    }
                    else
                    {
                        fixture.PendingMutationId =
                            string.Empty;
                        fixture.MutationCommitCount++;
                        fixture.VerificationEligibility =
                            "NonVerifiableDebugMutation";
                        await SendAcceptedAsync(
                            command.MessageType,
                            requestId);
                    }
                }
                else if (command.MessageType
                         == IpcMessageTypes.Subscribe)
                {
                    await SendAcceptedAsync(
                        command.MessageType,
                        requestId);
                    if (payload["stream"] == "watch")
                    {
                        await SendAsync(
                            IpcMessageTypes.WatchFrame,
                            new Dictionary<string, string>
                            {
                                ["json"] =
                                    "{\"watch\":\"first\"}",
                                ["movieTick"] = "42",
                                ["sequence"] = "1"
                            });
                        await SendAsync(
                            IpcMessageTypes.WatchFrame,
                            new Dictionary<string, string>
                            {
                                ["json"] =
                                    "{\"watch\":\"second\"}",
                                ["movieTick"] = "42",
                                ["sequence"] = "2"
                            });
                    }
                }
                else
                {
                    await SendAcceptedAsync(
                        command.MessageType,
                        requestId);
                }
            }

            async Task SendAcceptedAsync(
                string commandType,
                string request)
            {
                await SendAsync(
                    IpcMessageTypes.CommandAccepted,
                    new Dictionary<string, string>
                    {
                        ["command"] = commandType,
                        ["detail"] = "accepted",
                        ["requestId"] = request
                    });
            }

            async Task SendRejectedAsync(
                string commandType,
                string request,
                string code,
                string detail)
            {
                await SendAsync(
                    IpcMessageTypes.CommandRejected,
                    new Dictionary<string, string>
                    {
                        ["command"] = commandType,
                        ["detail"] = detail,
                        ["errorCode"] = code,
                        ["requestId"] = request
                    });
            }

            async Task SendAsync(
                string messageType,
                IReadOnlyDictionary<string, string> fields)
            {
                await IpcCodec.WriteFrameAsync(
                    server,
                    new IpcEnvelope(
                        1,
                        fixture.Registration.SessionId,
                        ++outgoing,
                        messageType,
                        IpcPayloadCodec.Serialize(fields)),
                    token);
            }

            static bool TryFinite(string value)
            {
                return float.TryParse(
                           value,
                           NumberStyles.Float,
                           CultureInfo.InvariantCulture,
                           out var parsed)
                       && !float.IsNaN(parsed)
                       && !float.IsInfinity(parsed);
            }
        }

        private static ProcessStartInfo DotnetStart(
            string assembly)
        {
            var result = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            result.ArgumentList.Add(assembly);
            return result;
        }

        private static byte[] CreateAgentMovie(
            int frames,
            int iteration)
        {
            var source =
                "hktas 1\n"
                + "game 1.5.78.11833\n"
                + "api 1.5.78.11833-77\n"
                + "manifest-sha256 "
                + new string('a', 64)
                + "\nbaseline none none\n"
                + "tick-unit input\n---\n"
                + "marker \"agent-"
                + iteration.ToString(CultureInfo.InvariantCulture)
                + "\"\nframes "
                + frames.ToString(CultureInfo.InvariantCulture)
                + " hold=-\n";
            var validation = new MovieEditorService().Validate(
                source,
                "scripted-agent.hktas");
            Assert.IsTrue(
                validation.Success,
                string.Join(
                    "\n",
                    validation.Diagnostics.Select(
                        item => item.Code + ":" + item.Message)));
            return new MovieCanonicalWriter().WriteUtf8(
                validation.Document!);
        }

        private static byte[] CreateInputMovie(string held)
        {
            var source =
                "hktas 1\n"
                + "game 1.5.78.11833\n"
                + "api 1.5.78.11833-77\n"
                + "manifest-sha256 "
                + new string('a', 64)
                + "\nbaseline none none\n"
                + "tick-unit input\n---\n"
                + "frames 1 hold=" + held + "\n";
            var validation = new MovieEditorService().Validate(
                source,
                "surface-input.hktas");
            Assert.IsTrue(
                validation.Success,
                string.Join(
                    "\n",
                    validation.Diagnostics.Select(
                        item => item.Code + ":" + item.Message)));
            return new MovieCanonicalWriter().WriteUtf8(
                validation.Document!);
        }

        private static Dictionary<string, string>
            HeroPoseArguments()
        {
            return new Dictionary<string, string>
            {
                ["expectedMovieTick"] = "42",
                ["expectedSnapshotSha256"] =
                    SemanticHash,
                ["positionX"] = "1.5",
                ["positionY"] = "2.5",
                ["velocityX"] = "0",
                ["velocityY"] = "0"
            };
        }

        private static void AssertBootstrapAclIsCurrentUserOnly(
            string path)
        {
            var current = WindowsIdentity.GetCurrent().User
                          ?? throw new InvalidOperationException(
                              "Current SID is unavailable.");
            var security =
                new FileInfo(path).GetAccessControl();
            Assert.IsTrue(security.AreAccessRulesProtected);
            var owner = security.GetOwner(
                            typeof(SecurityIdentifier))
                        ?? throw new InvalidOperationException(
                            "Bootstrap ACL owner is unavailable.");
            Assert.AreEqual(
                current.Value,
                owner.Value);
            var allowRules = security.GetAccessRules(
                    includeExplicit: true,
                    includeInherited: false,
                    typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Where(
                    rule => rule.AccessControlType
                            == AccessControlType.Allow)
                .ToArray();
            Assert.AreEqual(1, allowRules.Length);
            Assert.AreEqual(
                current.Value,
                ((SecurityIdentifier)allowRules[0]
                    .IdentityReference).Value);
        }

        private sealed class Fixture
        {
            public Fixture(
                CompanionSessionRegistration registration)
            {
                Registration = registration;
                CurrentMovieBytes = CreateAgentMovie(1, -1);
                var parsed = new MovieEditorService().Validate(
                    Encoding.UTF8.GetString(CurrentMovieBytes),
                    "fixture-current.hktas");
                CurrentMovieId = parsed.MovieId;
            }

            public CompanionSessionRegistration Registration { get; }
            public int PauseCount { get; set; }
            public int ApplyCount { get; set; }
            public int StartReplayCount { get; set; }
            public List<byte[]> InputBatches { get; } =
                new List<byte[]>();
            public byte[] CurrentMovieBytes { get; set; }
            public string CurrentMovieId { get; set; }
            public MemoryStream? PendingUpload { get; set; }
            public string PendingMovieId { get; set; } =
                string.Empty;
            public string LoadedSlot { get; set; } = string.Empty;
            public string OperationalMovieTick { get; set; } = "42";
            public string ControlMode { get; set; } =
                "Running";
            public string VerificationEligibility { get; set; } =
                "Eligible";
            public string PendingMutationId { get; set; } =
                string.Empty;
            public string WatchPayloadPadding { get; set; } =
                string.Empty;
            public bool RejectNextMutationCommit { get; set; }
            public int MutationCommitCount { get; set; }
            public int RollbackCount { get; set; }
        }
    }
}
