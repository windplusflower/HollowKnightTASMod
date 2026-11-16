using System;
using System.Linq;
using HollowKnightTAS.AgentBridge;
using HollowKnightTAS.Companion.Automation;
using HollowKnightTAS.Companion.ViewModels;
using HollowKnightTAS.Core.Automation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Companion.Tests
{
    [TestClass]
    public sealed class AuthoringSurfaceParityTests
    {
        [TestMethod]
        public void FullRunV2AndStudioOperationsHaveCatalogAndMcpAdapters()
        {
            var rows = new[]
            {
                Row(AutomationCommandIds.FullRunStatus, "hktas_get_status"),
                Row(AutomationCommandIds.GetCapabilities, "hktas_get_capabilities"),
                Row(AutomationCommandIds.GetWorldSnapshot, "hktas_get_world_snapshot"),
                Row(AutomationCommandIds.GetObjectDetails, "hktas_get_object_details"),
                Row(AutomationCommandIds.StudioGetMovie, "hktas_get_movie"),
                Row(AutomationCommandIds.StudioGetTimeline, "hktas_get_timeline"),
                Row(AutomationCommandIds.BeginFullRunRecording, "hktas_begin_recording"),
                Row(AutomationCommandIds.BeginFullRunReplay, "hktas_begin_replay"),
                Row(AutomationCommandIds.FullRunStep, "hktas_step"),
                Row(AutomationCommandIds.FullRunPlay, "hktas_play"),
                Row(AutomationCommandIds.FullRunPause, "hktas_pause"),
                Row(AutomationCommandIds.FullRunStop, "hktas_stop"),
                Row(AutomationCommandIds.StudioFrameAction, "hktas_seek_frame"),
                Row(AutomationCommandIds.QuitGame, "hktas_quit_game"),
                Row(AutomationCommandIds.StudioEditInputs, "hktas_edit_inputs"),
                Row(AutomationCommandIds.FullRunUpdateMovie, "hktas_update_movie"),
                Row(AutomationCommandIds.StudioSaveTimelineNode, "hktas_save_timeline_node"),
                Row(AutomationCommandIds.StudioRestoreTimelineNode, "hktas_restore_timeline_node"),
                Row(AutomationCommandIds.StudioSwitchWorldline, "hktas_switch_worldline"),
                Row(AutomationCommandIds.StudioDeleteTimelineNode, "hktas_delete_timeline_node"),
                Row(AutomationCommandIds.StudioSaveSequence, "hktas_save_sequence"),
                Row(AutomationCommandIds.StudioOpenSequence, "hktas_open_sequence"),
                Row(AutomationCommandIds.StartVideoExport, "hktas_start_video_export"),
                Row(AutomationCommandIds.CancelVideoExport, "hktas_cancel_video_export")
            };

            var catalog = new AutomationCapabilityCatalog(AutomationMode.ApprovedControl, false, fullRunOnly: true);
            foreach (var row in rows)
            {
                Assert.IsTrue(
                    AutomationCommandIds.IsKnown(row.CommandId),
                    "Core: " + row.CommandId);
                Assert.IsTrue(
                    catalog.TryGet(row.CommandId, out var cap) && cap.Availability == "available",
                    "Catalog(fullRunOnly): " + row.CommandId);
                Assert.IsTrue(
                    McpCatalog.TryGetTool(row.McpTool, out _),
                    "MCP: " + row.McpTool);
            }

            var uiProperties = typeof(MainViewModel)
                .GetProperties()
                .Select(value => value.Name)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var prop in new[]
                     {
                         "PlayCommand", "PauseCommand", "StepCommand", "StopReplayCommand",
                         "QuitGameCommand", "SaveTimelineNodeCommand", "RestoreTimelineNodeCommand",
                         "DeleteTimelineNodeCommand", "StartVideoExportCommand", "CancelVideoExportCommand"
                     })
            {
                Assert.IsTrue(uiProperties.Contains(prop), "UI: " + prop);
            }
        }

        private static SurfaceRow Row(string commandId, string mcpTool)
            => new SurfaceRow(commandId, mcpTool);

        private sealed class SurfaceRow
        {
            public SurfaceRow(string commandId, string mcpTool)
            {
                CommandId = commandId;
                McpTool = mcpTool;
            }

            public string CommandId { get; }
            public string McpTool { get; }
        }
    }
}
