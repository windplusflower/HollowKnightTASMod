using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using HollowKnightTAS.Core.Movie;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.AgentBridge.Tests
{
    [TestClass]
    public sealed class McpProtocolTests
    {
        [TestMethod]
        public void VideoExportSchemasRequireOperationIdentityAndTypedReplayOption()
        {
            Assert.IsTrue(McpCatalog.TryGetTool("hktas_cancel_video_export", out var cancelTool));
            using (var request = JsonDocument.Parse("{\"operationId\":\"video-test\"}"))
            {
                Assert.IsTrue(McpStdioServer.ValidateArguments(cancelTool, request.RootElement, out var error), error);
            }
            using (var missing = JsonDocument.Parse("{}"))
            {
                Assert.IsFalse(McpStdioServer.ValidateArguments(cancelTool, missing.RootElement, out _));
            }
            Assert.IsTrue(cancelTool.RequiresApprovedControl);

            Assert.IsTrue(McpCatalog.TryGetTool("hktas_start_video_export", out var start));
            using var valid = JsonDocument.Parse("{\"ffmpegPath\":\"ffmpeg.exe\",\"outputPath\":\"movie.mp4\",\"maximumFrames\":1000,\"replayLoadedMovie\":true,\"expectedRuntimeMode\":\"Paused\"}");
            Assert.IsTrue(McpStdioServer.ValidateArguments(start, valid.RootElement, out var validation), validation);
            using var invalid = JsonDocument.Parse(valid.RootElement.GetRawText().Replace("true", "\"true\""));
            Assert.IsFalse(McpStdioServer.ValidateArguments(start, invalid.RootElement, out _));
        }

        [TestMethod]
        public void VideoExportEndFrameIsOptionalBoundedAndForwardedWithoutChangingOldRequests()
        {
            Assert.IsTrue(McpCatalog.TryGetTool("hktas_start_video_export", out var tool));
            var property = tool.InputSchema["properties"]!["endMovieFrame"]!;
            Assert.AreEqual("integer", property["type"]!.GetValue<string>());
            Assert.AreEqual(1L, property["minimum"]!.GetValue<long>());
            Assert.AreEqual(MovieProtocolV2.MaximumExpandedFrames, property["maximum"]!.GetValue<long>());
            Assert.IsFalse(tool.InputSchema["required"]!.AsArray().Any(value => value!.GetValue<string>() == "endMovieFrame"));
            const string required = "\"ffmpegPath\":\"ffmpeg.exe\",\"outputPath\":\"movie.mp4\",\"maximumFrames\":1000,\"expectedRuntimeMode\":\"Paused\"";
            using var oldRequest = JsonDocument.Parse("{" + required + "}");
            using var newRequest = JsonDocument.Parse("{" + required + ",\"endMovieFrame\":3700}");
            using var upperBound = JsonDocument.Parse("{" + required + ",\"endMovieFrame\":" + MovieProtocolV2.MaximumExpandedFrames + "}");
            foreach (var request in new[] { oldRequest, newRequest, upperBound })
                Assert.IsTrue(McpStdioServer.ValidateArguments(tool, request.RootElement, out var error), error);
            foreach (var value in new[] { "0", "-1", "10000001", "1.5", "\"3700\"", "\"invalid\"", "null" })
                AssertRejects(tool, "{" + required + ",\"endMovieFrame\":" + value + "}");

            var select = typeof(McpStdioServer).GetMethod("SelectOptional",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var names = new[] { "ffmpegPath", "outputPath", "maximumFrames", "replayLoadedMovie", "endMovieFrame" };
            var oldFields = (System.Collections.Generic.IReadOnlyDictionary<string, string>)select.Invoke(null,
                new object[] { oldRequest.RootElement, names })!;
            var newFields = (System.Collections.Generic.IReadOnlyDictionary<string, string>)select.Invoke(null,
                new object[] { newRequest.RootElement, names })!;
            Assert.IsFalse(oldFields.ContainsKey("endMovieFrame"));
            Assert.IsFalse(oldFields.ContainsKey("replayLoadedMovie"));
            Assert.AreEqual("3700", newFields["endMovieFrame"]);
            Assert.AreEqual(oldFields["ffmpegPath"], newFields["ffmpegPath"]);
            Assert.AreEqual(oldFields["outputPath"], newFields["outputPath"]);
            Assert.AreEqual(oldFields["maximumFrames"], newFields["maximumFrames"]);
        }

        [TestMethod]
        public void EveryToolHasClosedObjectSchemaAndUniqueName()
        {
            var names = McpCatalog.Tools
                .Select(tool => tool.Name)
                .ToArray();
            Assert.AreEqual(
                names.Length,
                names.Distinct(StringComparer.Ordinal).Count());
            foreach (var tool in McpCatalog.Tools)
            {
                Assert.AreEqual(
                    "object",
                    tool.InputSchema["type"]!.GetValue<string>());
                Assert.IsFalse(
                    tool.InputSchema["additionalProperties"]!
                        .GetValue<bool>());
                Assert.IsTrue(
                    tool.Name.All(
                        character =>
                            char.IsAsciiLetterOrDigit(character)
                            || character == '_'
                            || character == '-'
                            || character == '.'));
            }
        }

        [TestMethod]
        public void ResourceCatalogContainsFullRunV2AndStudioUris()
        {
            CollectionAssert.AreEquivalent(
                new[]
                {
                    "hktas://session/current/status",
                    "hktas://session/current/manifest",
                    "hktas://session/current/capabilities",
                    "hktas://session/current/world",
                    "hktas://session/current/timeline",
                    "hktas://session/current/movie"
                },
                McpCatalog.Resources.Select(r => r["uri"]!.GetValue<string>()).ToArray());
        }

        [TestMethod]
        public void FullRunV2AndStudioToolsArePublishedWithTypedSchemas()
        {
            foreach (var readTool in new[]
                     {
                         "hktas_get_status",
                         "hktas_get_capabilities",
                         "hktas_get_world_snapshot",
                         "hktas_get_object_details",
                         "hktas_get_movie",
                         "hktas_get_timeline"
                     })
            {
                Assert.IsTrue(McpCatalog.TryGetTool(readTool, out var tool), readTool);
                Assert.IsTrue(tool.ReadOnly, readTool);
                Assert.IsFalse(tool.RequiresApprovedControl, readTool);
            }

            foreach (var writeTool in new[]
                     {
                         "hktas_acquire_control",
                         "hktas_renew_control",
                         "hktas_release_control",
                         "hktas_begin_recording",
                         "hktas_begin_replay",
                         "hktas_step",
                         "hktas_play",
                         "hktas_pause",
                         "hktas_stop",
                         "hktas_seek_frame",
                         "hktas_quit_game",
                         "hktas_edit_inputs",
                         "hktas_update_movie",
                         "hktas_save_timeline_node",
                         "hktas_restore_timeline_node",
                         "hktas_switch_worldline",
                         "hktas_delete_timeline_node",
                         "hktas_save_sequence",
                         "hktas_open_sequence",
                         "hktas_start_video_export",
                         "hktas_cancel_video_export"
                     })
            {
                Assert.IsTrue(McpCatalog.TryGetTool(writeTool, out var tool), writeTool);
                Assert.IsFalse(tool.ReadOnly, writeTool);
                Assert.IsTrue(tool.RequiresApprovedControl, writeTool);
            }

            Assert.IsTrue(McpCatalog.TryGetTool("hktas_seek_frame", out var seek));
            using (var validSeek = JsonDocument.Parse("{\"targetFrame\":120,\"action\":\"auto\"}"))
                Assert.IsTrue(McpStdioServer.ValidateArguments(seek, validSeek.RootElement, out var seekErr), seekErr);
            AssertRejects(seek, "{\"targetFrame\":-1}");
            AssertRejects(seek, "{}");

            Assert.IsTrue(McpCatalog.TryGetTool("hktas_edit_inputs", out var edit));
            using (var validEdit = JsonDocument.Parse("{\"operation\":\"paint\",\"startFrame\":10,\"endFrame\":15,\"action\":\"Jump\",\"held\":true}"))
                Assert.IsTrue(McpStdioServer.ValidateArguments(edit, validEdit.RootElement, out var editErr), editErr);
            using (var validRuns = JsonDocument.Parse("{\"operation\":\"set_runs\",\"startFrame\":0,\"runs\":\"[{\\\"frames\\\":5,\\\"actions\\\":[\\\"Right\\\"]}]\"}"))
                Assert.IsTrue(McpStdioServer.ValidateArguments(edit, validRuns.RootElement, out var runsErr), runsErr);
            AssertRejects(edit, "{\"startFrame\":0}");

            Assert.IsTrue(McpCatalog.TryGetTool("hktas_step", out var step));
            using (var validStep = JsonDocument.Parse("{\"count\":10}"))
                Assert.IsTrue(McpStdioServer.ValidateArguments(step, validStep.RootElement, out var stepErr), stepErr);
            AssertRejects(step, "{\"count\":0}");
            AssertRejects(step, "{\"count\":10001}");

            Assert.IsTrue(McpCatalog.TryGetTool("hktas_begin_recording", out var rec));
            using (var validRec = JsonDocument.Parse("{\"mouseEnabled\":false,\"fps\":50}"))
                Assert.IsTrue(McpStdioServer.ValidateArguments(rec, validRec.RootElement, out var recErr), recErr);
            AssertRejects(rec, "{\"fps\":0}");

            Assert.IsTrue(McpCatalog.TryGetTool("hktas_switch_worldline", out var sw));
            using (var validSwitch = JsonDocument.Parse("{\"leafId\":2}"))
                Assert.IsTrue(McpStdioServer.ValidateArguments(sw, validSwitch.RootElement, out var swErr), swErr);
            AssertRejects(sw, "{}");

            // Legacy v1 tools must no longer be exposed.
            Assert.IsFalse(McpCatalog.TryGetTool("hktas_get_state", out _));
            Assert.IsFalse(McpCatalog.TryGetTool("hktas_step_with_input", out _));
            Assert.IsFalse(McpCatalog.TryGetTool("hktas_create_replay_save", out _));
            Assert.IsFalse(McpCatalog.TryGetTool("hktas_set_hero_pose", out _));
            Assert.IsFalse(McpCatalog.TryGetTool("hktas_set_player_resources", out _));
        }

        [TestMethod]
        public void DeepObservationToolsPublishClosedBoundedSchemasWithWatches()
        {
            Assert.IsTrue(McpCatalog.TryGetTool(
                "hktas_get_world_snapshot", out var world));
            Assert.IsTrue(world.ReadOnly);
            Assert.IsFalse(world.RequiresApprovedControl);
            using var validWorld = JsonDocument.Parse(
                "{\"snapshotId\":\"snap-1\",\"view\":\"colliders\","
                + "\"includeInactive\":true,\"offset\":0,\"limit\":128}");
            Assert.IsTrue(McpStdioServer.ValidateArguments(
                world, validWorld.RootElement, out var worldError), worldError);
            using var validInfo = JsonDocument.Parse(
                "{\"view\":\"info\",\"watches\":\"[\\\"PlayerData.health\\\",\\\"HeroController.cState.onGround\\\"]\"}");
            Assert.IsTrue(McpStdioServer.ValidateArguments(
                world, validInfo.RootElement, out var infoError), infoError);
            using var validFsms = JsonDocument.Parse("{\"view\":\"fsms\"}");
            Assert.IsTrue(McpStdioServer.ValidateArguments(
                world, validFsms.RootElement, out var fsmsError), fsmsError);
            using var invalidWorld = JsonDocument.Parse("{\"limit\":129}");
            Assert.IsFalse(McpStdioServer.ValidateArguments(
                world, invalidWorld.RootElement, out _));

            Assert.IsTrue(McpCatalog.TryGetTool(
                "hktas_get_object_details", out var details));
            var required = details.InputSchema["required"]!
                .AsArray()
                .Select(value => value!.GetValue<string>())
                .ToArray();
            CollectionAssert.AreEqual(new[] { "objectId" }, required);
            using var validDetails = JsonDocument.Parse(
                "{\"objectId\":\"enemy-1\",\"expectedNativeFrame\":4,"
                + "\"cursor\":0,\"maxCharacters\":200000}");
            Assert.IsTrue(McpStdioServer.ValidateArguments(
                details, validDetails.RootElement, out var detailsError), detailsError);
            using var invalidDetails = JsonDocument.Parse(
                "{\"objectId\":\"enemy-1\",\"maxCharacters\":1023}");
            Assert.IsFalse(McpStdioServer.ValidateArguments(
                details, invalidDetails.RootElement, out _));
        }

        [TestMethod]
        public async Task BoundedLineReaderDrainsOversizedInputAndRecovers()
        {
            var input = new string('x', 33)
                        + "\r\n"
                        + "{\"jsonrpc\":\"2.0\"}\r\n";
            var reader = new BoundedTextLineReader(
                new StringReader(input));

            var oversized = await reader.ReadAsync(
                32,
                default);
            Assert.IsTrue(oversized.IsOversized);
            Assert.IsFalse(oversized.IsEndOfStream);
            Assert.IsNull(oversized.Text);

            var recovered = await reader.ReadAsync(
                32,
                default);
            Assert.IsFalse(recovered.IsOversized);
            Assert.IsFalse(recovered.IsEndOfStream);
            Assert.AreEqual(
                "{\"jsonrpc\":\"2.0\"}",
                recovered.Text);

            var end = await reader.ReadAsync(32, default);
            Assert.IsTrue(end.IsEndOfStream);
        }

        [TestMethod]
        public void ToolValidatorEnforcesPublishedBounds()
        {
            Assert.IsTrue(
                McpCatalog.TryGetTool(
                    "hktas_acquire_control",
                    out var acquire));
            AssertRejects(acquire, "{\"scopes\":[]}");
            AssertRejects(
                acquire,
                "{\"scopes\":[\"control.playback\","
                + "\"control.playback\"]}");
            AssertRejects(
                acquire,
                "{\"scopes\":["
                + string.Join(
                    ",",
                    Enumerable.Range(0, 17)
                        .Select(
                            index =>
                                JsonSerializer.Serialize(
                                    "scope-" + index)))
                + "]}");
            AssertRejects(
                acquire,
                "{\"scopes\":["
                + JsonSerializer.Serialize(
                    new string('s', 129))
                + "]}");
            AssertRejects(
                acquire,
                "{\"scopes\":[\"control.playback\"],"
                + "\"ttlSeconds\":301}");
            AssertRejects(
                acquire,
                "{\"scopes\":[\"control.playback\"],"
                + "\"shell\":\"powershell\"}");

            using var valid = JsonDocument.Parse(
                "{\"scopes\":[\"control.playback\"],"
                + "\"ttlSeconds\":30}");
            Assert.IsTrue(
                McpStdioServer.ValidateArguments(
                    acquire,
                    valid.RootElement,
                    out var error),
                error);

            Assert.IsTrue(
                McpCatalog.TryGetTool(
                    "hktas_renew_control",
                    out var renew));
            using var validRenewEmpty = JsonDocument.Parse("{}");
            Assert.IsTrue(
                McpStdioServer.ValidateArguments(
                    renew,
                    validRenewEmpty.RootElement,
                    out var renewEmptyErr),
                renewEmptyErr);
            using var validRenewTtl = JsonDocument.Parse("{\"ttlSeconds\":120}");
            Assert.IsTrue(
                McpStdioServer.ValidateArguments(
                    renew,
                    validRenewTtl.RootElement,
                    out var renewTtlErr),
                renewTtlErr);
            AssertRejects(renew, "{\"ttlSeconds\":301}");
            AssertRejects(renew, "{\"ttlSeconds\":0}");
        }

        [TestMethod]
        [Timeout(30000)]
        public async Task StdioNeverWritesNonJsonToStdout()
        {
            var assembly = typeof(McpStdioServer)
                .Assembly.Location;
            var start = new ProcessStartInfo(
                "dotnet",
                "\"" + assembly + "\"")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(start)
                                ?? throw new InvalidOperationException(
                                    "AgentBridge process did not start.");
            await process.StandardInput.WriteLineAsync("not-json");
            process.StandardInput.Close();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.AreEqual(0, process.ExitCode);
            Assert.IsFalse(string.IsNullOrWhiteSpace(stderr));
            var lines = stdout.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            Assert.AreEqual(1, lines.Length);
            using var parsed = JsonDocument.Parse(lines[0]);
            Assert.AreEqual(
                "2.0",
                parsed.RootElement
                    .GetProperty("jsonrpc")
                    .GetString());
            Assert.AreEqual(
                -32700,
                parsed.RootElement
                    .GetProperty("error")
                    .GetProperty("code")
                    .GetInt32());
        }

        private static void AssertRejects(
            McpToolDefinition tool,
            string json)
        {
            using var document = JsonDocument.Parse(json);
            Assert.IsFalse(
                McpStdioServer.ValidateArguments(
                    tool,
                    document.RootElement,
                    out var error));
            Assert.IsFalse(string.IsNullOrWhiteSpace(error));
        }
    }
}
