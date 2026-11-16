using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.AgentBridge
{
    internal sealed class McpToolDefinition
    {
        public McpToolDefinition(
            string name,
            string description,
            JsonObject inputSchema,
            bool readOnly,
            bool requiresApprovedControl)
        {
            Name = name;
            Description = description;
            InputSchema = inputSchema;
            ReadOnly = readOnly;
            RequiresApprovedControl = requiresApprovedControl;
        }

        public string Name { get; }
        public string Description { get; }
        public JsonObject InputSchema { get; }
        public bool ReadOnly { get; }
        public bool RequiresApprovedControl { get; }

        public JsonObject ToJson()
        {
            return new JsonObject
            {
                ["name"] = Name,
                ["description"] = Description,
                ["inputSchema"] = InputSchema.DeepClone(),
                ["annotations"] = new JsonObject
                {
                    ["readOnlyHint"] = ReadOnly,
                    ["destructiveHint"] = false,
                    ["idempotentHint"] = ReadOnly,
                    ["openWorldHint"] = false
                }
            };
        }
    }

    internal static class McpCatalog
    {
        public static readonly IReadOnlyList<McpToolDefinition> Tools =
            new[]
            {
                Tool(
                    "hktas_get_status",
                    "Read FullRun v2 runtime, gate, and Studio draft/timeline status.",
                    Schema(),
                    true),
                Tool(
                    "hktas_get_capabilities",
                    "Read the current session automation capability catalog.",
                    Schema(),
                    true),
                Tool(
                    "hktas_get_world_snapshot",
                    "Read a bounded immutable FullRun v2 world/FSM/info snapshot (view: world, all, colliders, fsmCatalog, fsms, info).",
                    Schema(
                        Props(
                            ("snapshotId", String(96)),
                            ("view", String(16)),
                            ("includeInactive", Boolean()),
                            ("offset", Integer(0)),
                            ("limit", Integer(1, 128)),
                            ("watches", String(20000)))),
                    true),
                Tool(
                    "hktas_get_object_details",
                    "Read immutable FullRun v2 details for one object, with bounded text pagination.",
                    Schema(
                        Props(
                            ("objectId", String(128)),
                            ("expectedNativeFrame", Integer(0)),
                            ("detailsId", String(96)),
                            ("cursor", Integer(0)),
                            ("maxCharacters", Integer(1024, 200000))),
                        new[] { "objectId" }),
                    true),
                Tool(
                    "hktas_get_movie",
                    "Read Studio's current v2 Movie (syncing latest recorded frames first when paused in Recording mode).",
                    Schema(
                        Props(
                            ("startFrame", Integer(0, MovieProtocolV2.MaximumExpandedFrames)),
                            ("frameCount", Integer(1, 10000)),
                            ("includeFullText", Boolean()))),
                    true),
                Tool(
                    "hktas_get_timeline",
                    "Read Studio's TimelineTree (nodes, worldline leaves, active path, and Lean S0 scene checkpoints).",
                    Schema(),
                    true),
                Tool(
                    "hktas_acquire_control",
                    "Acquire an exclusive control lease for explicit write scopes.",
                    Schema(
                        Props(
                            ("scopes", StringArray()),
                            ("ttlSeconds", Integer(1, 300))),
                        new[] { "scopes" }),
                    false),
                Tool(
                    "hktas_renew_control",
                    "Renew the bridge-owned control lease before expiration.",
                    Schema(
                        Props(
                            ("ttlSeconds", Integer(1, 300)))),
                    false),
                Tool(
                    "hktas_release_control",
                    "Release the bridge-owned control lease.",
                    Schema(),
                    false),
                Tool(
                    "hktas_begin_recording",
                    "Arm FullRun v2 recording at native frame 0 and initialize Studio's input grid and timeline.",
                    Schema(
                        Props(
                            ("mouseEnabled", Boolean()),
                            ("fps", Number(1, 1000)),
                            ("expectedNativeFrame", Integer(0)))),
                    false),
                Tool(
                    "hktas_begin_replay",
                    "Arm FullRun v2 replay at native frame 0 from movieBase64 or Studio's currently loaded v2 Movie.",
                    Schema(
                        Props(
                            ("movieBase64", String(900000)),
                            ("pauseAtFrame", Integer(-1, MovieProtocolV2.MaximumExpandedFrames)),
                            ("expectedNativeFrame", Integer(0)))),
                    false),
                Tool(
                    "hktas_step",
                    "Advance a paused FullRun v2 session by one or more native frames (applying pending Studio future edits first).",
                    Schema(
                        Props(
                            ("count", Integer(1, 10000)),
                            ("expectedNativeFrame", Integer(0)))),
                    false),
                Tool(
                    "hktas_play",
                    "Resume or start FullRun v2 playback/recording from a paused boundary (applying pending Studio future edits first).",
                    Schema(
                        Props(
                            ("expectedNativeFrame", Integer(0)))),
                    false),
                Tool(
                    "hktas_pause",
                    "Pause a running FullRun v2 session at a safe native frame boundary.",
                    Schema(
                        Props(
                            ("expectedNativeFrame", Integer(0)))),
                    false),
                Tool(
                    "hktas_stop",
                    "Stop an active FullRun v2 recording/replay at a paused boundary and load the recorded movie into Studio.",
                    Schema(
                        Props(
                            ("expectedNativeFrame", Integer(0)))),
                    false),
                Tool(
                    "hktas_seek_frame",
                    "Seek forward or restore/rebuild backward to targetFrame using Studio's exact FrameMenuAsync logic and Lean S0 checkpoints.",
                    Schema(
                        Props(
                            ("targetFrame", Integer(0, MovieProtocolV2.MaximumExpandedFrames)),
                            ("action", String(16)),
                            ("expectedNativeFrame", Integer(0))),
                        new[] { "targetFrame" }),
                    false),
                Tool(
                    "hktas_quit_game",
                    "Cleanly exit the protected game session at any paused boundary (including unarmed frame 0).",
                    Schema(
                        Props(
                            ("expectedNativeFrame", Integer(0)))),
                    false),
                Tool(
                    "hktas_edit_inputs",
                    "Edit Studio's v2 InputGrid (operations: paint, set_runs, insert_frames, delete_frames, append_blank_frames, set_fps, set_rng_seed, undo, redo).",
                    Schema(
                        Props(
                            ("operation", String(32)),
                            ("startFrame", Integer(0, MovieProtocolV2.MaximumExpandedFrames)),
                            ("endFrame", Integer(0, MovieProtocolV2.MaximumExpandedFrames)),
                            ("count", Integer(1, MovieProtocolV2.MaximumExpandedFrames)),
                            ("minimumTotal", Integer(1, MovieProtocolV2.MaximumExpandedFrames)),
                            ("action", String(64)),
                            ("held", Boolean()),
                            ("runs", String(900000)),
                            ("fps", Number(1, 1000)),
                            ("rngSeed", String(32))),
                        new[] { "operation" }),
                    false),
                Tool(
                    "hktas_update_movie",
                    "Push a canonical v2 movie file (inside ShadowRoot) directly to Runtime and Studio at a paused boundary.",
                    Schema(
                        Props(
                            ("moviePath", String(1024)),
                            ("expectedNativeFrame", Integer(0))),
                        new[] { "moviePath" }),
                    false),
                Tool(
                    "hktas_save_timeline_node",
                    "Save a TimelineTree node at the current paused frame (or specified frame) without moving the game.",
                    Schema(
                        Props(
                            ("frame", Integer(-1, MovieProtocolV2.MaximumExpandedFrames)))),
                    false),
                Tool(
                    "hktas_restore_timeline_node",
                    "Restore a TimelineTree node (automatically activating its branch and fast-restoring via nearest Lean S0 scene checkpoint).",
                    Schema(
                        Props(
                            ("nodeId", Integer(0, int.MaxValue)),
                            ("frame", Integer(0, MovieProtocolV2.MaximumExpandedFrames)),
                            ("worldlineId", Integer(0, int.MaxValue)),
                            ("treeId", String(64)))),
                    false),
                Tool(
                    "hktas_switch_worldline",
                    "Switch Studio's active TimelineTree worldline leaf and checkpoint branch scope.",
                    Schema(
                        Props(
                            ("leafId", Integer(0, int.MaxValue)),
                            ("treeId", String(64))),
                        new[] { "leafId" }),
                    false),
                Tool(
                    "hktas_delete_timeline_node",
                    "Delete a TimelineTree node subtree and prune orphaned Lean S0 scene checkpoints.",
                    Schema(
                        Props(
                            ("nodeId", Integer(0, int.MaxValue))),
                        new[] { "nodeId" }),
                    false),
                Tool(
                    "hktas_save_sequence",
                    "Persist the active sequence, initial saves, TimelineTree, and Lean S0 scene checkpoints to a .hktaspack or .hktas file.",
                    Schema(
                        Props(
                            ("path", String(1024))),
                        new[] { "path" }),
                    false),
                Tool(
                    "hktas_open_sequence",
                    "Open a .hktaspack or .hktas sequence in Studio, rebinding its TimelineTree and Lean S0 scene checkpoints.",
                    Schema(
                        Props(
                            ("path", String(1024))),
                        new[] { "path" }),
                    false),
                Tool(
                    "hktas_start_video_export",
                    "Start exporting gameplay frames and audio using the bundled encoder (ffmpegPath is an optional override).",
                    Schema(
                        Props(
                            ("ffmpegPath", String(1024)),
                            ("outputPath", String(1024)),
                            ("maximumFrames", Integer(1)),
                            ("replayLoadedMovie", Boolean()),
                            ("endMovieFrame", Integer(1, MovieProtocolV2.MaximumExpandedFrames)),
                            ("expectedRuntimeMode", String(32)),
                            ("expectedMovieTick", Integer(0))),
                        new[]
                        {
                            "outputPath",
                            "maximumFrames",
                            "expectedRuntimeMode"
                        }),
                    false),
                Tool(
                    "hktas_cancel_video_export",
                    "Cancel an active video export identified by operationId.",
                    Schema(
                        Props(("operationId", String(96))),
                        new[] { "operationId" }),
                    false)
            };

        public static readonly IReadOnlyList<JsonObject> Resources =
            new[]
            {
                Resource(
                    "hktas://session/current/status",
                    "FullRun v2 runtime, gate, and Studio status"),
                Resource(
                    "hktas://session/current/manifest",
                    "Environment manifest binding"),
                Resource(
                    "hktas://session/current/capabilities",
                    "Automation capability catalog"),
                Resource(
                    "hktas://session/current/world",
                    "Non-visual FullRun v2 world info snapshot"),
                Resource(
                    "hktas://session/current/timeline",
                    "Studio TimelineTree and Lean S0 checkpoints"),
                Resource(
                    "hktas://session/current/movie",
                    "Studio canonical v2 Movie")
            };

        public static bool TryGetTool(
            string name,
            out McpToolDefinition tool)
        {
            tool = Tools.FirstOrDefault(
                value => value.Name == name)!;
            return tool != null;
        }

        private static McpToolDefinition Tool(
            string name,
            string description,
            JsonObject schema,
            bool readOnly,
            bool? requiresApprovedControl = null)
        {
            return new McpToolDefinition(
                name,
                description,
                schema,
                readOnly,
                requiresApprovedControl ?? !readOnly);
        }

        private static JsonObject Resource(
            string uri,
            string name)
        {
            return new JsonObject
            {
                ["uri"] = uri,
                ["name"] = name,
                ["description"] =
                    "HollowKnightTAS FullRun v2 non-visual automation resource.",
                ["mimeType"] = "application/json"
            };
        }

        private static JsonObject Schema(
            JsonObject? properties = null,
            IEnumerable<string>? required = null)
        {
            var result = new JsonObject
            {
                ["type"] = "object",
                ["properties"] =
                    properties ?? new JsonObject(),
                ["additionalProperties"] = false
            };
            var requiredArray = new JsonArray();
            foreach (var value in required ?? Array.Empty<string>())
            {
                requiredArray.Add(value);
            }

            if (requiredArray.Count > 0)
            {
                result["required"] = requiredArray;
            }

            return result;
        }

        private static JsonObject Props(
            params (string Name, JsonObject Schema)[] items)
        {
            var result = new JsonObject();
            foreach (var item in items)
            {
                result[item.Name] = item.Schema;
            }

            return result;
        }

        private static JsonObject String(int maximumLength)
        {
            return new JsonObject
            {
                ["type"] = "string",
                ["maxLength"] = maximumLength
            };
        }

        private static JsonObject StringArray()
        {
            return new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "string",
                    ["maxLength"] = 128
                },
                ["minItems"] = 1,
                ["maxItems"] = 16,
                ["uniqueItems"] = true
            };
        }

        private static JsonObject Integer(
            long minimum,
            long? maximum = null)
        {
            var result = new JsonObject
            {
                ["type"] = "integer",
                ["minimum"] = minimum
            };
            if (maximum.HasValue)
            {
                result["maximum"] = maximum.Value;
            }

            return result;
        }

        private static JsonObject Number(
            double minimum,
            double maximum)
        {
            return new JsonObject
            {
                ["type"] = "number",
                ["minimum"] = minimum,
                ["maximum"] = maximum
            };
        }

        private static JsonObject Boolean()
        {
            return new JsonObject
            {
                ["type"] = "boolean"
            };
        }
    }
}
