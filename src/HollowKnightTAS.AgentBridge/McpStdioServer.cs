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
        private AutomationClient automation =
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
        private int? activeLeaseTtlSeconds;

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
                        ["version"] = "0.2.0",
                        ["description"] =
                            "Local FullRun v2 & Studio TAS observation and typed control."
                    },
                    ["instructions"] =
                        "Read status and capabilities first. "
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

            string resourceCommandId;
            string resourceScope;
            IReadOnlyDictionary<string, string>? resourceArgs = null;
            switch (uri)
            {
                case "hktas://session/current/status":
                case "hktas://session/current/manifest":
                    resourceCommandId = AutomationCommandIds.FullRunStatus;
                    resourceScope = AutomationScope.ObserveStatus;
                    break;
                case "hktas://session/current/capabilities":
                    resourceCommandId = AutomationCommandIds.GetCapabilities;
                    resourceScope = AutomationScope.ObserveStatus;
                    break;
                case "hktas://session/current/world":
                    resourceCommandId = AutomationCommandIds.GetWorldSnapshot;
                    resourceScope = AutomationScope.ObserveStateDeep;
                    resourceArgs = new Dictionary<string, string>(StringComparer.Ordinal) { ["view"] = "info" };
                    break;
                case "hktas://session/current/timeline":
                    resourceCommandId = AutomationCommandIds.StudioGetTimeline;
                    resourceScope = AutomationScope.ObserveStatus;
                    break;
                case "hktas://session/current/movie":
                    resourceCommandId = AutomationCommandIds.StudioGetMovie;
                    resourceScope = AutomationScope.MovieRead;
                    break;
                default:
                    await WriteErrorAsync(
                        id,
                        -32602,
                        "Unknown or unsafe hktas resource URI.");
                    return;
            }

            var result = await ExecuteWithRebindAsync(
                client => client.CreateCommand(resourceCommandId, resourceScope, resourceArgs),
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
                case "hktas_get_status":
                    return await ReadCommandAsync(
                        AutomationCommandIds.FullRunStatus,
                        AutomationScope.ObserveStatus,
                        Empty(),
                        cancellationToken);
                case "hktas_get_capabilities":
                    return await ReadCommandAsync(
                        AutomationCommandIds.GetCapabilities,
                        AutomationScope.ObserveStatus,
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
                case "hktas_get_movie":
                    return await ReadCommandAsync(
                        AutomationCommandIds.StudioGetMovie,
                        AutomationScope.MovieRead,
                        FlatArguments(arguments),
                        cancellationToken);
                case "hktas_get_timeline":
                    return await ReadCommandAsync(
                        AutomationCommandIds.StudioGetTimeline,
                        AutomationScope.ObserveStatus,
                        Empty(),
                        cancellationToken);
                case "hktas_acquire_control":
                    return await AcquireAsync(
                        arguments,
                        cancellationToken);
                case "hktas_renew_control":
                    return await RenewAsync(
                        arguments,
                        cancellationToken);
                case "hktas_release_control":
                    return await ReleaseAsync(cancellationToken);
                case "hktas_begin_recording":
                {
                    RequireLease(AutomationScope.ControlRecording);
                    var expectedNative = await ResolveNativeFrameAsync(arguments, cancellationToken);
                    var cmdArgs = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["expectedNativeFrame"] = expectedNative.ToString(CultureInfo.InvariantCulture),
                        ["mouseEnabled"] = arguments.TryGetProperty("mouseEnabled", out var mouseEl) && mouseEl.GetBoolean()
                            ? "true"
                            : "false"
                    };
                    if (arguments.TryGetProperty("fps", out var fpsEl))
                        cmdArgs["fps"] = fpsEl.GetRawText();
                    return await ExecuteWithRebindAsync(
                        client => client.CreateCommand(
                            AutomationCommandIds.BeginFullRunRecording,
                            AutomationScope.ControlRecording,
                            cmdArgs,
                            activeLeaseId,
                            "Paused"),
                        cancellationToken);
                }
                case "hktas_begin_replay":
                {
                    RequireLease(AutomationScope.ControlPlayback);
                    var expectedNative = await ResolveNativeFrameAsync(arguments, cancellationToken);
                    var cmdArgs = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["expectedNativeFrame"] = expectedNative.ToString(CultureInfo.InvariantCulture)
                    };
                    if (arguments.TryGetProperty("movieBase64", out var base64El))
                        cmdArgs["movieBase64"] = base64El.GetString()!;
                    if (arguments.TryGetProperty("pauseAtFrame", out var pauseEl))
                        cmdArgs["pauseAtFrame"] = pauseEl.GetInt64().ToString(CultureInfo.InvariantCulture);
                    return await ExecuteWithRebindAsync(
                        client => client.CreateCommand(
                            AutomationCommandIds.BeginFullRunReplay,
                            AutomationScope.ControlPlayback,
                            cmdArgs,
                            activeLeaseId,
                            "Paused"),
                        cancellationToken);
                }
                case "hktas_step":
                {
                    RequireLease(AutomationScope.ControlStep);
                    var expectedNative = await ResolveNativeFrameAsync(arguments, cancellationToken);
                    var count = arguments.TryGetProperty("count", out var countEl)
                        ? countEl.GetInt64()
                        : 1L;
                    AutomationResultEnvelope? lastResult = null;
                    for (var i = 0L; i < count; i++)
                    {
                        var cmdArgs = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["expectedNativeFrame"] = expectedNative.ToString(CultureInfo.InvariantCulture)
                        };
                        lastResult = await ExecuteWithRebindAsync(
                            client => client.CreateCommand(
                                AutomationCommandIds.FullRunStep,
                                AutomationScope.ControlStep,
                                cmdArgs,
                                activeLeaseId,
                                "Paused"),
                            cancellationToken);
                        if (!lastResult.Success)
                            return lastResult;
                        if (lastResult.Data.TryGetValue("nativeFrame", out var nextText)
                            && long.TryParse(nextText, NumberStyles.None, CultureInfo.InvariantCulture, out var nextNative))
                            expectedNative = nextNative;
                        else
                            expectedNative++;
                    }
                    return lastResult!;
                }
                case "hktas_play":
                    return await ExecuteFullRunWithNativeAsync(
                        AutomationCommandIds.FullRunPlay,
                        AutomationScope.ControlPlayback,
                        arguments,
                        "Paused",
                        cancellationToken);
                case "hktas_pause":
                    return await ExecuteFullRunWithNativeAsync(
                        AutomationCommandIds.FullRunPause,
                        AutomationScope.ControlPlayback,
                        arguments,
                        "Running",
                        cancellationToken);
                case "hktas_stop":
                    return await ExecuteFullRunWithNativeAsync(
                        AutomationCommandIds.FullRunStop,
                        AutomationScope.ControlPlayback,
                        arguments,
                        "Paused",
                        cancellationToken);
                case "hktas_seek_frame":
                    return await ExecuteWriteFlatAsync(
                        AutomationCommandIds.StudioFrameAction,
                        AutomationScope.ControlPlayback,
                        arguments,
                        "Paused",
                        cancellationToken);
                case "hktas_quit_game":
                    return await ExecuteWriteFlatAsync(
                        AutomationCommandIds.QuitGame,
                        AutomationScope.ControlPlayback,
                        arguments,
                        "Paused",
                        cancellationToken);
                case "hktas_edit_inputs":
                    return await ExecuteWriteFlatAsync(
                        AutomationCommandIds.StudioEditInputs,
                        AutomationScope.ControlInput,
                        arguments,
                        "Paused",
                        cancellationToken);
                case "hktas_update_movie":
                {
                    RequireLease(AutomationScope.ControlPlayback);
                    var expectedNative = await ResolveNativeFrameAsync(arguments, cancellationToken);
                    var cmdArgs = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["moviePath"] = RequiredString(arguments, "moviePath"),
                        ["expectedNativeFrame"] = expectedNative.ToString(CultureInfo.InvariantCulture)
                    };
                    return await ExecuteWithRebindAsync(
                        client => client.CreateCommand(
                            AutomationCommandIds.FullRunUpdateMovie,
                            AutomationScope.ControlPlayback,
                            cmdArgs,
                            activeLeaseId,
                            "Paused"),
                        cancellationToken);
                }
                case "hktas_save_timeline_node":
                    return await ExecuteWriteFlatAsync(
                        AutomationCommandIds.StudioSaveTimelineNode,
                        AutomationScope.ControlReplaySave,
                        arguments,
                        "Paused",
                        cancellationToken);
                case "hktas_restore_timeline_node":
                    return await ExecuteWriteFlatAsync(
                        AutomationCommandIds.StudioRestoreTimelineNode,
                        AutomationScope.ControlReplaySave,
                        arguments,
                        "Paused",
                        cancellationToken);
                case "hktas_switch_worldline":
                    return await ExecuteWriteFlatAsync(
                        AutomationCommandIds.StudioSwitchWorldline,
                        AutomationScope.ControlReplaySave,
                        arguments,
                        "Paused",
                        cancellationToken);
                case "hktas_delete_timeline_node":
                    return await ExecuteWriteFlatAsync(
                        AutomationCommandIds.StudioDeleteTimelineNode,
                        AutomationScope.ControlReplaySave,
                        arguments,
                        "Paused",
                        cancellationToken);
                case "hktas_save_sequence":
                    return await ExecuteWriteFlatAsync(
                        AutomationCommandIds.StudioSaveSequence,
                        AutomationScope.ControlPlayback,
                        arguments,
                        "Paused",
                        cancellationToken);
                case "hktas_open_sequence":
                    return await ExecuteWriteFlatAsync(
                        AutomationCommandIds.StudioOpenSequence,
                        AutomationScope.ControlPlayback,
                        arguments,
                        "Paused",
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
                case "hktas_cancel_video_export":
                    return await WriteCommandAsync(
                        AutomationCommandIds.CancelVideoExport,
                        AutomationScope.ControlPlayback,
                        Select(arguments, "operationId"),
                        arguments,
                        cancellationToken);
                default:
                    throw new ToolExecutionException(
                        "Tool is not mapped.");
            }
        }

        private async Task<long> ResolveNativeFrameAsync(
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            if (arguments.TryGetProperty("expectedNativeFrame", out var frameEl))
                return frameEl.GetInt64();
            var status = await ReadCommandAsync(
                AutomationCommandIds.FullRunStatus,
                AutomationScope.ObserveStatus,
                Empty(),
                cancellationToken);
            if (!status.Success
                || !status.Data.TryGetValue("nativeFrame", out var text)
                || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                throw new ToolExecutionException(
                    "Unable to query current nativeFrame from fullRunStatus.");
            }
            return parsed;
        }

        private async Task<AutomationResultEnvelope> ExecuteFullRunWithNativeAsync(
            string commandId,
            string scope,
            JsonElement arguments,
            string expectedRuntimeMode,
            CancellationToken cancellationToken)
        {
            RequireLease(scope);
            var expectedNative = await ResolveNativeFrameAsync(arguments, cancellationToken);
            var cmdArgs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["expectedNativeFrame"] = expectedNative.ToString(CultureInfo.InvariantCulture)
            };
            return await ExecuteWithRebindAsync(
                client => client.CreateCommand(
                    commandId,
                    scope,
                    cmdArgs,
                    activeLeaseId,
                    expectedRuntimeMode),
                cancellationToken);
        }

        private Task<AutomationResultEnvelope> ExecuteWriteFlatAsync(
            string commandId,
            string scope,
            JsonElement arguments,
            string expectedRuntimeMode,
            CancellationToken cancellationToken)
        {
            RequireLease(scope);
            var flatArgs = FlatArguments(arguments);
            return ExecuteWithRebindAsync(
                client => client.CreateCommand(
                    commandId,
                    scope,
                    flatArgs,
                    activeLeaseId,
                    expectedRuntimeMode),
                cancellationToken);
        }

        private Task<AutomationResultEnvelope> ReadCommandAsync(
            string commandId,
            string scope,
            IReadOnlyDictionary<string, string> arguments,
            CancellationToken cancellationToken)
        {
            return ExecuteWithRebindAsync(
                client => client.CreateCommand(
                    commandId,
                    scope,
                    arguments),
                cancellationToken);
        }

        private async Task<AutomationResultEnvelope> ExecuteWithRebindAsync(
            Func<AutomationClient, AutomationCommandEnvelope> buildCommand,
            CancellationToken cancellationToken)
        {
            var result = await automation.ExecuteAsync(buildCommand(automation), cancellationToken);
            if (!result.Success && result.ResultCode == "BindingMismatch")
            {
                await automation.DisposeAsync();
                automation = new AutomationClient();
                handshake = await automation.ConnectAsync(connectOptions, cancellationToken);
                if (activeLeaseScopes.Length > 0)
                {
                    var leaseArgs = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["scopes"] = string.Join(",", activeLeaseScopes)
                    };
                    if (activeLeaseTtlSeconds.HasValue)
                        leaseArgs["ttlSeconds"] = activeLeaseTtlSeconds.Value.ToString(CultureInfo.InvariantCulture);
                    var reacquired = await automation.ExecuteAsync(
                        automation.CreateCommand(
                            AutomationCommandIds.AcquireControl,
                            activeLeaseScopes[0],
                            leaseArgs),
                        cancellationToken);
                    if (reacquired.Success && reacquired.Data.TryGetValue("leaseId", out var newLeaseId))
                        activeLeaseId = newLeaseId;
                }
                result = await automation.ExecuteAsync(buildCommand(automation), cancellationToken);
            }
            return result;
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
            int? parsedTtl = null;
            if (arguments.TryGetProperty(
                    "ttlSeconds",
                    out var ttl))
            {
                flat["ttlSeconds"] = ttl.GetRawText();
                parsedTtl = ttl.GetInt32();
            }

            if (!string.IsNullOrEmpty(activeLeaseId))
            {
                var sameScopes = scopes.OrderBy(s => s, StringComparer.Ordinal)
                    .SequenceEqual(activeLeaseScopes.OrderBy(s => s, StringComparer.Ordinal), StringComparer.Ordinal);
                if (sameScopes)
                {
                    var renewArgs = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (parsedTtl.HasValue)
                        renewArgs["ttlSeconds"] = parsedTtl.Value.ToString(CultureInfo.InvariantCulture);
                    else if (activeLeaseTtlSeconds.HasValue)
                        renewArgs["ttlSeconds"] = activeLeaseTtlSeconds.Value.ToString(CultureInfo.InvariantCulture);
                    var renewed = await ExecuteWithRebindAsync(
                        client => client.CreateCommand(
                            AutomationCommandIds.RenewControl,
                            scopes[0],
                            renewArgs,
                            activeLeaseId),
                        cancellationToken);
                    if (renewed.Success)
                    {
                        if (renewed.Data.TryGetValue("leaseId", out var renewedLeaseId))
                            activeLeaseId = renewedLeaseId;
                        if (parsedTtl.HasValue)
                            activeLeaseTtlSeconds = parsedTtl;
                        return renewed;
                    }
                    activeLeaseId = string.Empty;
                    activeLeaseScopes = Array.Empty<string>();
                    activeLeaseTtlSeconds = null;
                }
                else
                {
                    await ReleaseAsync(cancellationToken);
                    activeLeaseId = string.Empty;
                    activeLeaseScopes = Array.Empty<string>();
                    activeLeaseTtlSeconds = null;
                }
            }

            var result = await ExecuteWithRebindAsync(
                client => client.CreateCommand(
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
                activeLeaseTtlSeconds = parsedTtl;
            }

            return result;
        }

        private async Task<AutomationResultEnvelope> RenewAsync(
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            RequireLease(
                activeLeaseScopes.FirstOrDefault()
                ?? AutomationScope.ControlPlayback);
            var flat = new Dictionary<string, string>(StringComparer.Ordinal);
            int? parsedTtl = null;
            if (arguments.TryGetProperty("ttlSeconds", out var ttl))
            {
                flat["ttlSeconds"] = ttl.GetRawText();
                parsedTtl = ttl.GetInt32();
            }
            else if (activeLeaseTtlSeconds.HasValue)
            {
                flat["ttlSeconds"] = activeLeaseTtlSeconds.Value.ToString(CultureInfo.InvariantCulture);
            }

            var result = await ExecuteWithRebindAsync(
                client => client.CreateCommand(
                    AutomationCommandIds.RenewControl,
                    activeLeaseScopes[0],
                    flat,
                    activeLeaseId),
                cancellationToken);
            if (result.Success)
            {
                if (result.Data.TryGetValue("leaseId", out var renewedLeaseId))
                    activeLeaseId = renewedLeaseId;
                if (parsedTtl.HasValue)
                    activeLeaseTtlSeconds = parsedTtl;
            }
            else if (result.ResultCode == "LeaseExpired" || result.ResultCode == "LeaseRequired")
            {
                activeLeaseId = string.Empty;
                activeLeaseScopes = Array.Empty<string>();
                activeLeaseTtlSeconds = null;
            }

            return result;
        }

        private async Task<AutomationResultEnvelope> ReleaseAsync(
            CancellationToken cancellationToken)
        {
            RequireLease(
                activeLeaseScopes.FirstOrDefault()
                ?? AutomationScope.ControlPlayback);
            var result = await ExecuteWithRebindAsync(
                client => client.CreateCommand(
                    AutomationCommandIds.ReleaseControl,
                    activeLeaseScopes[0],
                    Empty(),
                    activeLeaseId),
                cancellationToken);
            if (result.Success)
            {
                activeLeaseId = string.Empty;
                activeLeaseScopes = Array.Empty<string>();
                activeLeaseTtlSeconds = null;
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
            var operationCancellation = commandId == AutomationCommandIds.CancelVideoExport;
            var mode = operationCancellation ? string.Empty : RequiredString(
                toolArguments,
                "expectedRuntimeMode");
            long? tick = toolArguments.TryGetProperty(
                    "expectedMovieTick",
                    out var tickElement)
                ? tickElement.GetInt64()
                : null;
            return ExecuteWithRebindAsync(
                client => client.CreateCommand(
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
                && string.Equals(toolName, "hktas_get_timeline", StringComparison.Ordinal)
                && result.Data.TryGetValue("timelineJson", out var timelineJson))
            {
                try { structured["timeline"] = JsonNode.Parse(timelineJson); }
                catch (JsonException) { }
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
