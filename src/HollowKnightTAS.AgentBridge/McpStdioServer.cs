using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Automation.Client;
using HollowKnightTAS.Core.Automation;

namespace HollowKnightTAS.AgentBridge
{
    internal sealed class McpStdioServer : IAsyncDisposable
    {
        private const string McpProtocolVersion = "2025-11-25";
        private const int MaximumLineCharacters = 1024 * 1024;
        private readonly BoundedTextLineReader input =
            new BoundedTextLineReader(Console.In);
        private readonly AutomationClient automation =
            new AutomationClient();
        private readonly AutomationConnectOptions connectOptions =
            new AutomationConnectOptions
            {
                ClientId =
                    "mcp-" + Guid.NewGuid().ToString("N")
            };
        private AutomationHandshake? handshake;
        private bool initialized;
        private string activeLeaseId = string.Empty;
        private string[] activeLeaseScopes = Array.Empty<string>();

        public McpStdioServer(string[] args)
        {
            var bootstrap = args.FirstOrDefault(
                value => value.StartsWith(
                    "--bootstrap=",
                    StringComparison.Ordinal));
            if (bootstrap != null)
            {
                connectOptions.BootstrapPath = Path.GetFullPath(
                    bootstrap.Substring("--bootstrap=".Length));
            }

            if (args.Any(
                    value =>
                        !value.StartsWith(
                            "--bootstrap=",
                            StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    "Only --bootstrap=<path> is supported.");
            }
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await input.ReadAsync(
                    MaximumLineCharacters,
                    cancellationToken);
                if (read.IsEndOfStream)
                {
                    return;
                }

                if (read.IsOversized
                    || string.IsNullOrEmpty(read.Text))
                {
                    await WriteErrorAsync(
                        null,
                        -32700,
                        "Invalid or oversized JSON-RPC line.");
                    continue;
                }

                JsonDocument? document = null;
                try
                {
                    document = JsonDocument.Parse(
                        read.Text,
                        new JsonDocumentOptions
                        {
                            AllowTrailingCommas = false,
                            CommentHandling =
                                JsonCommentHandling.Disallow,
                            MaxDepth = 32
                        });
                    await HandleAsync(
                        document.RootElement,
                        cancellationToken);
                }
                catch (JsonException exception)
                {
                    Console.Error.WriteLine(
                        "McpParseError:" + exception.Message);
                    await WriteErrorAsync(
                        null,
                        -32700,
                        "Parse error.");
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(
                        "McpRequestError:"
                        + exception.GetType().Name);
                    await WriteErrorAsync(
                        TryReadId(document?.RootElement),
                        -32603,
                        "Internal error.");
                }
                finally
                {
                    document?.Dispose();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await automation.DisposeAsync();
        }

        private async Task HandleAsync(
            JsonElement root,
            CancellationToken cancellationToken)
        {
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(
                    "jsonrpc",
                    out var jsonrpc)
                || jsonrpc.ValueKind != JsonValueKind.String
                || jsonrpc.GetString() != "2.0"
                || !root.TryGetProperty("method", out var methodElement)
                || methodElement.ValueKind != JsonValueKind.String)
            {
                await WriteErrorAsync(
                    TryReadId(root),
                    -32600,
                    "Invalid Request.");
                return;
            }

            var method = methodElement.GetString()!;
            var hasId = root.TryGetProperty("id", out var id);
            if (method == "notifications/initialized")
            {
                if (handshake != null)
                {
                    initialized = true;
                }

                return;
            }

            if (method == "notifications/cancelled")
            {
                return;
            }

            if (!hasId)
            {
                return;
            }

            if (method == "initialize")
            {
                await InitializeAsync(root, id, cancellationToken);
                return;
            }

            if (handshake == null)
            {
                await WriteErrorAsync(
                    id,
                    -32002,
                    "Server is not initialized.");
                return;
            }

            if (method == "ping")
            {
                await WriteResultAsync(id, new JsonObject());
                return;
            }

            if (!initialized)
            {
                await WriteErrorAsync(
                    id,
                    -32002,
                    "Awaiting notifications/initialized.");
                return;
            }

            switch (method)
            {
                case "resources/list":
                    await WriteResultAsync(
                        id,
                        new JsonObject
                        {
                            ["resources"] = new JsonArray(
                                McpCatalog.Resources
                                    .Select(
                                        resource =>
                                            resource.DeepClone())
                                    .ToArray())
                        });
                    return;
                case "resources/templates/list":
                    await WriteResultAsync(
                        id,
                        new JsonObject
                        {
                            ["resourceTemplates"] =
                                new JsonArray()
                        });
                    return;
                case "resources/read":
                    await ReadResourceAsync(
                        root,
                        id,
                        cancellationToken);
                    return;
                case "tools/list":
                    await ListToolsAsync(id);
                    return;
                case "tools/call":
                    await CallToolAsync(
                        root,
                        id,
                        cancellationToken);
                    return;
                default:
                    await WriteErrorAsync(
                        id,
                        -32601,
                        "Method not found.");
                    return;
            }
        }

        private async Task InitializeAsync(
            JsonElement request,
            JsonElement id,
            CancellationToken cancellationToken)
        {
            if (handshake != null
                || !request.TryGetProperty(
                    "params",
                    out var parameters)
                || parameters.ValueKind != JsonValueKind.Object
                || !parameters.TryGetProperty(
                    "protocolVersion",
                    out var protocol)
                || protocol.ValueKind != JsonValueKind.String
                || protocol.GetString() != McpProtocolVersion)
            {
                await WriteErrorAsync(
                    id,
                    -32602,
                    "Only MCP protocol 2025-11-25 is supported.");
                return;
            }

            handshake = await automation.ConnectAsync(
                connectOptions,
                cancellationToken);
            await WriteResultAsync(
                id,
                new JsonObject
                {
                    ["protocolVersion"] = McpProtocolVersion,
                    ["capabilities"] = new JsonObject
                    {
                        ["resources"] = new JsonObject(),
                        ["tools"] = new JsonObject()
                    },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] =
                            "HollowKnightTAS.AgentBridge",
                        ["title"] =
                            "Hollow Knight TAS Agent Bridge",
                        ["version"] = "0.1.8",
                        ["description"] =
                            "Local non-visual TAS observation and typed control."
                    },
                    ["instructions"] =
                        "Read state and capabilities first. "
                        + "Write tools require ApprovedControl and an "
                        + "explicit short lease."
                });
        }

        private async Task ListToolsAsync(JsonElement id)
        {
            var approved = handshake?.Mode
                           == nameof(
                               AutomationMode.ApprovedControl);
            var tools = McpCatalog.Tools
                .Where(
                    tool =>
                        !tool.RequiresApprovedControl || approved)
                .Select(tool => tool.ToJson())
                .ToArray();
            await WriteResultAsync(
                id,
                new JsonObject
                {
                    ["tools"] = new JsonArray(tools)
                });
        }

        private async Task ReadResourceAsync(
            JsonElement request,
            JsonElement id,
            CancellationToken cancellationToken)
        {
            if (!TryGetObjectParameter(
                    request,
                    out var parameters)
                || !TryGetString(
                    parameters,
                    "uri",
                    out var uri)
                || parameters.EnumerateObject().Count() != 1)
            {
                await WriteErrorAsync(
                    id,
                    -32602,
                    "resources/read requires one uri.");
                return;
            }

            AutomationCommandEnvelope command;
            switch (uri)
            {
                case "hktas://session/current/status":
                case "hktas://session/current/manifest":
                    command = automation.CreateCommand(
                        AutomationCommandIds.GetStatus,
                        AutomationScope.ObserveStatus);
                    break;
                case "hktas://session/current/capabilities":
                    command = automation.CreateCommand(
                        AutomationCommandIds.GetCapabilities,
                        AutomationScope.ObserveStatus);
                    break;
                case "hktas://session/current/state/summary":
                    command = automation.CreateCommand(
                        AutomationCommandIds.GetState,
                        AutomationScope.ObserveStateSummary);
                    break;
                case "hktas://session/current/state/combat":
                    command = automation.CreateCommand(
                        AutomationCommandIds.GetCombatState,
                        AutomationScope.ObserveStateDeep);
                    break;
                case "hktas://session/current/timeline":
                    command = automation.CreateCommand(
                        AutomationCommandIds.GetTimeline,
                        AutomationScope.ObserveTimeline);
                    break;
                case "hktas://session/current/desync/latest":
                    command = automation.CreateCommand(
                        AutomationCommandIds.GetDesync,
                        AutomationScope.ObserveDesync);
                    break;
                case "hktas://session/current/replay-saves":
                    command = automation.CreateCommand(
                        AutomationCommandIds.GetReplaySaves,
                        AutomationScope.ObserveReplaySaves);
                    break;
                case "hktas://session/current/movie":
                    command = automation.CreateCommand(
                        AutomationCommandIds.GetMovie,
                        AutomationScope.MovieRead);
                    break;
                case "hktas://session/current/restore-strategy":
                    command = automation.CreateCommand(
                        AutomationCommandIds.GetRestoreStrategy,
                        AutomationScope.ObserveStatus);
                    break;
                default:
                    await WriteErrorAsync(
                        id,
                        -32602,
                        "Unknown or unsafe hktas resource URI.");
                    return;
            }

            var result = await automation.ExecuteAsync(
                command,
                cancellationToken);
            var text = new UTF8Encoding(false, true).GetString(
                result.ToPayload());
            await WriteResultAsync(
                id,
                new JsonObject
                {
                    ["contents"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["uri"] = uri,
                            ["mimeType"] = "application/json",
                            ["text"] = text
                        }
                    }
                });
        }

        private async Task CallToolAsync(
            JsonElement request,
            JsonElement id,
            CancellationToken cancellationToken)
        {
            if (!TryGetObjectParameter(
                    request,
                    out var parameters)
                || !TryGetString(
                    parameters,
                    "name",
                    out var name)
                || !parameters.TryGetProperty(
                    "arguments",
                    out var arguments)
                || arguments.ValueKind != JsonValueKind.Object
                || parameters.EnumerateObject().Count() != 2
                || !McpCatalog.TryGetTool(name, out var tool))
            {
                await WriteErrorAsync(
                    id,
                    -32602,
                    "Unknown tool or malformed tools/call request.");
                return;
            }

            if (!ValidateArguments(tool, arguments, out var error))
            {
                await WriteToolErrorAsync(id, error);
                return;
            }

            try
            {
                var result = await ExecuteToolAsync(
                    name,
                    arguments,
                    cancellationToken);
                await WriteToolResultAsync(id, name, result);
            }
            catch (ToolExecutionException exception)
            {
                await WriteToolErrorAsync(id, exception.Message);
            }
        }

        private async Task<AutomationResultEnvelope> ExecuteToolAsync(
            string name,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            switch (name)
            {
                case "hktas_get_state":
                    return await ReadCommandAsync(
                        AutomationCommandIds.GetState,
                        AutomationScope.ObserveStateSummary,
                        FlatArguments(arguments),
                        cancellationToken);
                case "hktas_get_combat_state":
                    return await ReadCommandAsync(
                        AutomationCommandIds.GetCombatState,
                        AutomationScope.ObserveStateDeep,
                        Empty(),
                        cancellationToken);
                case "hktas_get_world_snapshot":
                    return await ReadCommandAsync(
                        AutomationCommandIds.GetWorldSnapshot,
                        AutomationScope.ObserveStateDeep,
                        FlatArguments(arguments),
                        cancellationToken);
                case "hktas_get_object_details":
                    return await ReadCommandAsync(
                        AutomationCommandIds.GetObjectDetails,
                        AutomationScope.ObserveStateDeep,
                        FlatArguments(arguments),
                        cancellationToken);
                case "hktas_get_timeline":
                    return await ReadCommandAsync(
                        AutomationCommandIds.GetTimeline,
                        AutomationScope.ObserveTimeline,
                        FlatArguments(arguments),
                        cancellationToken);
                case "hktas_get_desync":
                    return await ReadCommandAsync(
                        AutomationCommandIds.GetDesync,
                        AutomationScope.ObserveDesync,
                        Empty(),
                        cancellationToken);
                case "hktas_get_replay_saves":
                    return await ReadCommandAsync(
                        AutomationCommandIds.GetReplaySaves,
                        AutomationScope.ObserveReplaySaves,
                        Empty(),
                        cancellationToken);
                case "hktas_get_movie":
                    return await ReadCommandAsync(
                        AutomationCommandIds.GetMovie,
                        AutomationScope.MovieRead,
                        FlatArguments(arguments),
                        cancellationToken);
                case "hktas_propose_movie_patch":
                    return await ReadCommandAsync(
                        AutomationCommandIds.ProposeMoviePatch,
                        AutomationScope.MoviePropose,
                        FlatArguments(arguments),
                        cancellationToken);
                case "hktas_validate_movie_patch":
                    return await ReadCommandAsync(
                        AutomationCommandIds.ValidateMoviePatch,
                        AutomationScope.MovieValidate,
                        FlatArguments(arguments),
                        cancellationToken);
                case "hktas_acquire_control":
                    return await AcquireAsync(
                        arguments,
                        cancellationToken);
                case "hktas_release_control":
                    return await ReleaseAsync(cancellationToken);
                case "hktas_pause":
                    return await WriteCommandAsync(
                        AutomationCommandIds.Pause,
                        AutomationScope.ControlPlayback,
                        Empty(),
                        arguments,
                        cancellationToken);
                case "hktas_resume":
                    return await WriteCommandAsync(
                        AutomationCommandIds.Resume,
                        AutomationScope.ControlPlayback,
                        Empty(),
                        arguments,
                        cancellationToken);
                case "hktas_quit_game":
                    return await WriteCommandAsync(AutomationCommandIds.QuitGame,
                        AutomationScope.ControlPlayback, Empty(), arguments, cancellationToken);
                case "hktas_load_game_slot":
                    return await WriteCommandAsync(AutomationCommandIds.LoadGameSlot,
                        AutomationScope.ControlPlayback, Select(arguments, "slot"), arguments, cancellationToken);
                case "hktas_reload_game_slot":
                    return await WriteCommandAsync(AutomationCommandIds.ReloadGameSlot,
                        AutomationScope.ControlPlayback, Select(arguments, "slot"), arguments, cancellationToken);
                case "hktas_restart_recording_session":
                    return await WriteCommandAsync(AutomationCommandIds.RestartRecordingSession,
                        AutomationScope.ControlPlayback, Select(arguments, "slot"), arguments, cancellationToken);
                case "hktas_cancel_recording_restart":
                    return await WriteCommandAsync(AutomationCommandIds.CancelRecordingRestart,
                        AutomationScope.ControlPlayback, Select(arguments, "operationId"), arguments, cancellationToken);
                case "hktas_step":
                    return await WriteCommandAsync(
                        AutomationCommandIds.Step,
                        AutomationScope.ControlStep,
                        Select(arguments, "count"),
                        arguments,
                        cancellationToken);
                case "hktas_step_with_input":
                    return await WriteCommandAsync(
                        AutomationCommandIds.StepWithInput,
                        AutomationScope.ControlInput,
                        Select(
                            arguments,
                            "candidateMovieBase64",
                            "expectedSceneEpoch"),
                        arguments,
                        cancellationToken);
                case "hktas_queue_input_batch":
                    return await WriteCommandAsync(
                        AutomationCommandIds.QueueInputBatch,
                        AutomationScope.ControlInput,
                        Select(
                            arguments,
                            "candidateMovieBase64",
                            "expectedSceneEpoch"),
                        arguments,
                        cancellationToken);
                case "hktas_begin_input_batch":
                    return await WriteCommandAsync(
                        AutomationCommandIds.BeginInputBatch,
                        AutomationScope.ControlInput,
                        Select(arguments, "expectedSceneEpoch"),
                        arguments,
                        cancellationToken);
                case "hktas_append_input_batch":
                    return await WriteCommandAsync(
                        AutomationCommandIds.AppendInputBatch,
                        AutomationScope.ControlInput,
                        Select(
                            arguments,
                            "transactionId",
                            "chunkIndex",
                            "candidateMovieBase64",
                            "expectedSceneEpoch"),
                        arguments,
                        cancellationToken);
                case "hktas_commit_input_batch":
                    return await WriteCommandAsync(
                        AutomationCommandIds.CommitInputBatch,
                        AutomationScope.ControlInput,
                        Select(
                            arguments,
                            "transactionId",
                            "expectedSceneEpoch"),
                        arguments,
                        cancellationToken);
                case "hktas_cancel_input_batch":
                    return await WriteCommandAsync(
                        AutomationCommandIds.CancelInputBatch,
                        AutomationScope.ControlInput,
                        Select(
                            arguments,
                            "transactionId",
                            "expectedSceneEpoch"),
                        arguments,
                        cancellationToken);
                case "hktas_run_until":
                    return await WriteCommandAsync(
                        AutomationCommandIds.RunUntil,
                        AutomationScope.ControlRunUntil,
                        Select(arguments, "targetMovieTick"),
                        arguments,
                        cancellationToken);
                case "hktas_start_recording":
                    return await WriteCommandAsync(
                        AutomationCommandIds.StartRecording,
                        AutomationScope.ControlRecording,
                        Empty(),
                        arguments,
                        cancellationToken);
                case "hktas_stop_recording":
                    return await WriteCommandAsync(
                        AutomationCommandIds.StopRecording,
                        AutomationScope.ControlRecording,
                        Empty(),
                        arguments,
                        cancellationToken);
                case "hktas_start_video_export":
                    return await WriteCommandAsync(
                        AutomationCommandIds.StartVideoExport,
                        AutomationScope.ControlPlayback,
                        SelectOptional(
                            arguments,
                            "ffmpegPath",
                            "outputPath",
                            "maximumFrames",
                            "replayLoadedMovie",
                            "endMovieFrame"),
                        arguments,
                        cancellationToken);
                case "hktas_finish_video_export":
                    return await WriteCommandAsync(
                        AutomationCommandIds.FinishVideoExport,
                        AutomationScope.ControlPlayback,
                        Select(arguments, "operationId"),
                        arguments,
                        cancellationToken);
                case "hktas_cancel_video_export":
                    return await WriteCommandAsync(
                        AutomationCommandIds.CancelVideoExport,
                        AutomationScope.ControlPlayback,
                        Select(arguments, "operationId"),
                        arguments,
                        cancellationToken);
                case "hktas_start_replay":
                    return await WriteCommandAsync(
                        AutomationCommandIds.StartReplay,
                        AutomationScope.ControlPlayback,
                        Empty(),
                        arguments,
                        cancellationToken);
                case "hktas_stop_replay":
                    return await WriteCommandAsync(
                        AutomationCommandIds.StopReplay,
                        AutomationScope.ControlPlayback,
                        Empty(),
                        arguments,
                        cancellationToken);
                case "hktas_create_replay_save":
                    return await WriteCommandAsync(
                        AutomationCommandIds.CreateReplaySave,
                        AutomationScope.ControlReplaySave,
                        Select(arguments, "label"),
                        arguments,
                        cancellationToken);
                case "hktas_set_auto_save_policy":
                    return await WriteCommandAsync(AutomationCommandIds.SetAutoSavePolicy,
                        AutomationScope.ControlReplaySave,
                        Select(arguments, "enabled", "intervalMovieTicks", "retentionCount"),
                        arguments, cancellationToken);
                case "hktas_restore_replay_save":
                    return await WriteCommandAsync(
                        AutomationCommandIds.RestoreReplaySave,
                        AutomationScope.ControlReplaySave,
                        Select(arguments, "replaySaveId"),
                        arguments,
                        cancellationToken);
                case "hktas_seek_movie_tick":
                    return await WriteCommandAsync(
                        AutomationCommandIds.SeekMovieTick,
                        AutomationScope.ControlReplaySave,
                        Select(
                            arguments,
                            "targetMovieTick",
                            "expectedSceneEpoch"),
                        arguments,
                        cancellationToken);
                case "hktas_approve_replay_save_overwrite":
                    return await WriteCommandAsync(
                        AutomationCommandIds
                            .ApproveReplaySaveOverwrite,
                        AutomationScope.ControlReplaySave,
                        Select(arguments, "approved"),
                        arguments,
                        cancellationToken);
                case "hktas_cancel_replay_save_restore":
                    return await WriteCommandAsync(
                        AutomationCommandIds
                            .CancelReplaySaveRestore,
                        AutomationScope.ControlReplaySave,
                        Select(arguments, "operationId"),
                        arguments,
                        cancellationToken);
                case "hktas_resume_replay_save_restore":
                    return await WriteCommandAsync(
                        AutomationCommandIds
                            .ResumeReplaySaveRestore,
                        AutomationScope.ControlReplaySave,
                        Empty(),
                        arguments,
                        cancellationToken);
                case "hktas_apply_movie_branch":
                    return await WriteCommandAsync(
                        AutomationCommandIds.ApplyMovieBranch,
                        AutomationScope.MovieApplyBranch,
                        Select(arguments, "branchMovieId"),
                        arguments,
                        cancellationToken);
                case "hktas_apply_branch_and_seek":
                    return await WriteCommandAsync(
                        AutomationCommandIds.ApplyBranchAndSeek,
                        AutomationScope.MovieApplyBranch,
                        Select(
                            arguments,
                            "branchMovieId",
                            "targetMovieTick",
                            "expectedSceneEpoch"),
                        arguments,
                        cancellationToken);
                case "hktas_replace_input_range":
                    return await ReadCommandAsync(
                        AutomationCommandIds.ReplaceInputRange,
                        AutomationScope.MovieEdit,
                        SelectOptionalLifecycle(
                            arguments,
                            "includeLifecycle",
                            "baseMovieId",
                            "startTick",
                            "deleteCount",
                            "replacementMovieBase64"),
                        cancellationToken);
                case "hktas_insert_input_range":
                    return await ReadCommandAsync(
                        AutomationCommandIds.InsertInputRange,
                        AutomationScope.MovieEdit,
                        SelectOptionalLifecycle(
                            arguments,
                            "includeLifecycle",
                            "baseMovieId",
                            "startTick",
                            "replacementMovieBase64"),
                        cancellationToken);
                case "hktas_delete_input_range":
                    return await ReadCommandAsync(
                        AutomationCommandIds.DeleteInputRange,
                        AutomationScope.MovieEdit,
                        SelectOptionalLifecycle(
                            arguments,
                            "includeLifecycle",
                            "baseMovieId",
                            "startTick",
                            "count"),
                        cancellationToken);
                default:
                    throw new ToolExecutionException(
                        "Tool is not mapped.");
            }
        }

        private Task<AutomationResultEnvelope> ReadCommandAsync(
            string commandId,
            string scope,
            IReadOnlyDictionary<string, string> arguments,
            CancellationToken cancellationToken)
        {
            return automation.ExecuteAsync(
                automation.CreateCommand(
                    commandId,
                    scope,
                    arguments),
                cancellationToken);
        }

        private async Task<AutomationResultEnvelope> AcquireAsync(
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            var scopesElement = arguments.GetProperty("scopes");
            var scopes = scopesElement.EnumerateArray()
                .Select(value => value.GetString()!)
                .ToArray();
            if (scopes.Length == 0
                || scopes.Any(
                    scope =>
                        !AutomationScope.IsKnown(scope)
                        || AutomationScope.IsReadOnly(scope)))
            {
                throw new ToolExecutionException(
                    "scopes must contain registered write scopes.");
            }

            var flat = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["scopes"] = string.Join(",", scopes)
            };
            if (arguments.TryGetProperty(
                    "ttlSeconds",
                    out var ttl))
            {
                flat["ttlSeconds"] = ttl.GetRawText();
            }

            var result = await automation.ExecuteAsync(
                automation.CreateCommand(
                    AutomationCommandIds.AcquireControl,
                    scopes[0],
                    flat),
                cancellationToken);
            if (result.Success
                && result.Data.TryGetValue(
                    "leaseId",
                    out var leaseId))
            {
                activeLeaseId = leaseId;
                activeLeaseScopes = scopes;
            }

            return result;
        }

        private async Task<AutomationResultEnvelope> ReleaseAsync(
            CancellationToken cancellationToken)
        {
            RequireLease(
                activeLeaseScopes.FirstOrDefault()
                ?? AutomationScope.ControlPlayback);
            var result = await automation.ExecuteAsync(
                automation.CreateCommand(
                    AutomationCommandIds.ReleaseControl,
                    activeLeaseScopes[0],
                    Empty(),
                    activeLeaseId),
                cancellationToken);
            if (result.Success)
            {
                activeLeaseId = string.Empty;
                activeLeaseScopes = Array.Empty<string>();
            }

            return result;
        }

        private Task<AutomationResultEnvelope> WriteCommandAsync(
            string commandId,
            string scope,
            IReadOnlyDictionary<string, string> commandArguments,
            JsonElement toolArguments,
            CancellationToken cancellationToken)
        {
            RequireLease(scope);
            var operationCancellation = commandId == AutomationCommandIds.CancelRecordingRestart
                || commandId == AutomationCommandIds.CancelReplaySaveRestore
                || commandId == AutomationCommandIds.FinishVideoExport
                || commandId == AutomationCommandIds.CancelVideoExport;
            var mode = operationCancellation ? string.Empty : RequiredString(
                toolArguments,
                "expectedRuntimeMode");
            long? tick = toolArguments.TryGetProperty(
                    "expectedMovieTick",
                    out var tickElement)
                ? tickElement.GetInt64()
                : null;
            return automation.ExecuteAsync(
                automation.CreateCommand(
                    commandId,
                    scope,
                    commandArguments,
                    activeLeaseId,
                    mode,
                    tick),
                cancellationToken);
        }

        private void RequireLease(string scope)
        {
            if (string.IsNullOrEmpty(activeLeaseId)
                || !activeLeaseScopes.Contains(
                    scope,
                    StringComparer.Ordinal))
            {
                throw new ToolExecutionException(
                    "Acquire a control lease containing scope "
                    + scope
                    + " first.");
            }
        }

        internal static bool ValidateArguments(
            McpToolDefinition tool,
            JsonElement arguments,
            out string error)
        {
            var properties =
                (JsonObject)tool.InputSchema["properties"]!;
            foreach (var supplied in arguments.EnumerateObject())
            {
                if (!properties.ContainsKey(supplied.Name))
                {
                    error =
                        "Unknown argument: " + supplied.Name + ".";
                    return false;
                }
            }

            if (tool.InputSchema.TryGetPropertyValue(
                    "required",
                    out var requiredNode)
                && requiredNode is JsonArray required)
            {
                foreach (var item in required)
                {
                    var name = item!.GetValue<string>();
                    if (!arguments.TryGetProperty(name, out _))
                    {
                        error =
                            "Missing required argument: " + name + ".";
                        return false;
                    }
                }
            }

            foreach (var supplied in arguments.EnumerateObject())
            {
                var schema = (JsonObject)properties[supplied.Name]!;
                var type = schema["type"]!.GetValue<string>();
                if (type == "string"
                    && supplied.Value.ValueKind
                    != JsonValueKind.String
                    || type == "boolean"
                    && supplied.Value.ValueKind
                    != JsonValueKind.True
                    && supplied.Value.ValueKind
                    != JsonValueKind.False
                    || type == "integer"
                    && supplied.Value.ValueKind
                    != JsonValueKind.Number
                    || type == "number"
                    && supplied.Value.ValueKind
                    != JsonValueKind.Number
                    || type == "array"
                    && supplied.Value.ValueKind
                    != JsonValueKind.Array)
                {
                    error =
                        "Argument type is invalid: "
                        + supplied.Name
                        + ".";
                    return false;
                }

                if (type == "integer"
                    && (!supplied.Value.TryGetInt64(out var integer)
                        || schema.TryGetPropertyValue(
                               "minimum",
                               out var minimum)
                           && integer
                           < minimum!.GetValue<long>()
                        || schema.TryGetPropertyValue(
                               "maximum",
                               out var maximum)
                           && integer
                           > maximum!.GetValue<long>()))
                {
                    error =
                        "Integer argument is out of bounds: "
                        + supplied.Name
                        + ".";
                    return false;
                }

                if (type == "string"
                    && schema.TryGetPropertyValue(
                        "maxLength",
                        out var maximumLength)
                    && supplied.Value.GetString()!.Length
                    > maximumLength!.GetValue<int>())
                {
                    error =
                        "String argument is too long: "
                        + supplied.Name
                        + ".";
                    return false;
                }

                if (type == "number"
                    && (!supplied.Value.TryGetDouble(out var number)
                        || double.IsNaN(number)
                        || double.IsInfinity(number)
                        || schema.TryGetPropertyValue(
                               "minimum",
                               out var numberMinimum)
                           && number
                           < numberMinimum!.GetValue<double>()
                        || schema.TryGetPropertyValue(
                               "maximum",
                               out var numberMaximum)
                           && number
                           > numberMaximum!.GetValue<double>()))
                {
                    error =
                        "Number argument is invalid or out of bounds: "
                        + supplied.Name
                        + ".";
                    return false;
                }

                if (type == "array")
                {
                    var values = supplied.Value
                        .EnumerateArray()
                        .ToArray();
                    if (values.Any(
                            value =>
                                value.ValueKind
                                != JsonValueKind.String))
                    {
                        error =
                            "Array argument must contain strings: "
                            + supplied.Name
                            + ".";
                        return false;
                    }

                    if (schema.TryGetPropertyValue(
                            "items",
                            out var itemNode)
                        && itemNode is JsonObject itemSchema
                        && itemSchema.TryGetPropertyValue(
                            "maxLength",
                            out var itemMaximumLength)
                        && values.Any(
                            value =>
                                value.GetString()!.Length
                                > itemMaximumLength!.GetValue<int>()))
                    {
                        error =
                            "Array argument contains an oversized item: "
                            + supplied.Name
                            + ".";
                        return false;
                    }

                    if (schema.TryGetPropertyValue(
                            "minItems",
                            out var minimumItems)
                        && values.Length
                        < minimumItems!.GetValue<int>()
                        || schema.TryGetPropertyValue(
                            "maxItems",
                            out var maximumItems)
                        && values.Length
                        > maximumItems!.GetValue<int>())
                    {
                        error =
                            "Array argument item count is out of bounds: "
                            + supplied.Name
                            + ".";
                        return false;
                    }

                    if (schema.TryGetPropertyValue(
                            "uniqueItems",
                            out var uniqueItems)
                        && uniqueItems!.GetValue<bool>()
                        && values.Select(value => value.GetString()!)
                            .Distinct(StringComparer.Ordinal)
                            .Count()
                        != values.Length)
                    {
                        error =
                            "Array argument items must be unique: "
                            + supplied.Name
                            + ".";
                        return false;
                    }
                }
            }

            error = string.Empty;
            return true;
        }

        private static IReadOnlyDictionary<string, string>
            FlatArguments(JsonElement arguments)
        {
            var result = new Dictionary<string, string>(
                StringComparer.Ordinal);
            foreach (var property in arguments.EnumerateObject())
            {
                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        result[property.Name] =
                            property.Value.GetString()!;
                        break;
                    case JsonValueKind.Number:
                        result[property.Name] =
                            property.Value.GetRawText();
                        break;
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        result[property.Name] =
                            property.Value.GetBoolean()
                                ? "true"
                                : "false";
                        break;
                    default:
                        throw new ToolExecutionException(
                            "Only flat string/number arguments are allowed.");
                }
            }

            return result;
        }

        private static IReadOnlyDictionary<string, string> Select(
            JsonElement arguments,
            params string[] names)
        {
            var all = FlatArguments(arguments);
            return names.ToDictionary(
                name => name,
                name => all[name],
                StringComparer.Ordinal);
        }

        private static IReadOnlyDictionary<string, string> SelectOptional(
            JsonElement arguments,
            params string[] names)
        {
            var all = FlatArguments(arguments);
            return names.Where(all.ContainsKey)
                .ToDictionary(name => name, name => all[name], StringComparer.Ordinal);
        }

        private static IReadOnlyDictionary<string, string> SelectOptionalLifecycle(JsonElement arguments, params string[] names)
        {
            var all = FlatArguments(arguments);
            return names.Where(name => name != "includeLifecycle" || all.ContainsKey(name))
                .ToDictionary(name => name, name => all[name], StringComparer.Ordinal);
        }

        private static IReadOnlyDictionary<string, string> Empty()
        {
            return new Dictionary<string, string>(
                StringComparer.Ordinal);
        }

        private static string RequiredString(
            JsonElement value,
            string name)
        {
            if (!TryGetString(value, name, out var result))
            {
                throw new ToolExecutionException(
                    "Missing string argument: " + name + ".");
            }

            return result;
        }

        private static bool TryGetString(
            JsonElement value,
            string name,
            out string result)
        {
            if (value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.String)
            {
                result = property.GetString()!;
                return true;
            }

            result = string.Empty;
            return false;
        }

        private static bool TryGetObjectParameter(
            JsonElement request,
            out JsonElement parameters)
        {
            return request.TryGetProperty(
                       "params",
                       out parameters)
                   && parameters.ValueKind == JsonValueKind.Object;
        }

        private static async Task WriteToolResultAsync(
            JsonElement id,
            string toolName,
            AutomationResultEnvelope result)
        {
            var structured = new JsonObject
            {
                ["success"] = result.Success,
                ["resultCode"] = result.ResultCode,
                ["detail"] = result.Detail,
                ["sessionId"] = result.SessionId,
                ["manifestSha256"] = result.ManifestSha256,
                ["acceptedAtMovieTick"] =
                    result.AcceptedAtMovieTick
            };
            var data = new JsonObject();
            foreach (var pair in result.Data)
            {
                data[pair.Key] = pair.Value;
            }

            structured["data"] = data;
            if (result.Success
                && string.Equals(
                    toolName,
                    "hktas_get_state",
                    StringComparison.Ordinal)
                && result.Data.TryGetValue(
                    "stateJson",
                    out var stateJson))
            {
                try
                {
                    var friendly =
                        AutomationSemanticState.Parse(
                            stateJson);
                    var friendlyNode = JsonNode.Parse(
                                           friendly.ToFriendlyJson())
                                       ?.AsObject()
                                       ?? throw new InvalidDataException(
                                           "Friendly state JSON is empty.");
                    structured["state"] =
                        friendlyNode["state"]?.DeepClone();
                    structured["semanticValues"] =
                        friendlyNode["semanticValues"]?.DeepClone();
                }
                catch (Exception exception) when (
                    exception is JsonException
                    || exception is InvalidDataException
                    || exception is FormatException
                    || exception is OverflowException)
                {
                    throw new ToolExecutionException(
                        "Canonical automation state is invalid: "
                        + exception.Message);
                }
            }

            var text = structured.ToJsonString();
            await WriteResultAsync(
                id,
                new JsonObject
                {
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = text
                        }
                    },
                    ["structuredContent"] =
                        structured.DeepClone(),
                    ["isError"] = !result.Success
                });
        }

        private static Task WriteToolErrorAsync(
            JsonElement id,
            string error)
        {
            var structured = new JsonObject
            {
                ["success"] = false,
                ["resultCode"] = "InvalidToolInput",
                ["detail"] = Sanitize(error)
            };
            return WriteResultAsync(
                id,
                new JsonObject
                {
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = structured.ToJsonString()
                        }
                    },
                    ["structuredContent"] = structured,
                    ["isError"] = true
                });
        }

        private static Task WriteResultAsync(
            JsonElement id,
            JsonObject result)
        {
            return WriteAsync(
                new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = CloneId(id),
                    ["result"] = result
                });
        }

        private static Task WriteErrorAsync(
            JsonElement? id,
            int code,
            string message)
        {
            return WriteAsync(
                new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id.HasValue
                        ? CloneId(id.Value)
                        : null,
                    ["error"] = new JsonObject
                    {
                        ["code"] = code,
                        ["message"] = Sanitize(message)
                    }
                });
        }

        private static async Task WriteAsync(JsonObject message)
        {
            await Console.Out.WriteLineAsync(
                message.ToJsonString(
                    new JsonSerializerOptions
                    {
                        WriteIndented = false
                    }));
            await Console.Out.FlushAsync();
        }

        private static JsonNode? CloneId(JsonElement id)
        {
            return JsonNode.Parse(id.GetRawText());
        }

        private static JsonElement? TryReadId(
            JsonElement? root)
        {
            if (root.HasValue
                && root.Value.ValueKind == JsonValueKind.Object
                && root.Value.TryGetProperty(
                    "id",
                    out var id))
            {
                return id.Clone();
            }

            return null;
        }

        private static string Sanitize(string value)
        {
            var result = (value ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\\', '/');
            return result.Substring(0, Math.Min(512, result.Length));
        }

        private sealed class ToolExecutionException : Exception
        {
            public ToolExecutionException(string message)
                : base(message)
            {
            }
        }
    }
}
