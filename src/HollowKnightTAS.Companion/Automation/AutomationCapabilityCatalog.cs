using System;
using System.Collections.Generic;
using System.Linq;
using HollowKnightTAS.Core.Automation;

namespace HollowKnightTAS.Companion.Automation
{
    public sealed class AutomationCapabilityCatalog
    {
        private readonly IReadOnlyDictionary<
            string,
            AutomationCapability> capabilities;

        public AutomationCapabilityCatalog(
            AutomationMode mode,
            bool debugMutationEnabled,
            bool fullRunOnly = false)
        {
            // The legacy registration flag is accepted for wire compatibility only.
            // Debug state writers are no longer part of the capability catalog.
            var items = new[]
            {
                Read(
                    AutomationCommandIds.GetStatus,
                    AutomationScope.ObserveStatus),
                Read(
                    AutomationCommandIds.GetStartupProfile,
                    AutomationScope.ObserveStatus),
                Read(
                    AutomationCommandIds.GetCapabilities,
                    AutomationScope.ObserveStatus),
                Read(
                    AutomationCommandIds.GetState,
                    AutomationScope.ObserveStateSummary),
                Read(
                    AutomationCommandIds.GetCombatState,
                    AutomationScope.ObserveStateDeep),
                FullRunObservation(
                    AutomationCommandIds.GetWorldSnapshot,
                    AutomationScope.ObserveStateDeep,
                    fullRunOnly),
                FullRunObservation(
                    AutomationCommandIds.GetObjectDetails,
                    AutomationScope.ObserveStateDeep,
                    fullRunOnly),
                Read(
                    AutomationCommandIds.GetTimeline,
                    AutomationScope.ObserveTimeline),
                Read(
                    AutomationCommandIds.GetDesync,
                    AutomationScope.ObserveDesync),
                Read(
                    AutomationCommandIds.GetReplaySaves,
                    AutomationScope.ObserveReplaySaves),
                Read(
                    AutomationCommandIds.GetMovie,
                    AutomationScope.MovieRead),
                Write(AutomationCommandIds.FullRunUpdateMovie, AutomationScope.ControlPlayback, mode),
                Write(AutomationCommandIds.FullRunSeek, AutomationScope.ControlPlayback, mode),
                Read(AutomationCommandIds.FullRunSnapshot, AutomationScope.MovieRead),
                Read(AutomationCommandIds.FullRunStatus, AutomationScope.ObserveStatus),
                Read(AutomationCommandIds.FullRunMovie, AutomationScope.MovieRead),
                Write(AutomationCommandIds.BeginFullRunRecording,
                    AutomationScope.ControlRecording, mode),
                Write(AutomationCommandIds.BeginFullRunReplay,
                    AutomationScope.ControlPlayback, mode),
                Write(AutomationCommandIds.FullRunStep,
                    AutomationScope.ControlStep, mode),
                Write(AutomationCommandIds.FullRunPlay,
                    AutomationScope.ControlPlayback, mode),
                Write(AutomationCommandIds.FullRunPause,
                    AutomationScope.ControlPlayback, mode),
                Write(AutomationCommandIds.StudioEditInputs,
                    AutomationScope.ControlInput, mode),
                Read(AutomationCommandIds.StudioGetMovie,
                    AutomationScope.MovieRead),
                Write(AutomationCommandIds.StudioFrameAction,
                    AutomationScope.ControlPlayback, mode),
                Read(AutomationCommandIds.StudioGetTimeline,
                    AutomationScope.ObserveStatus),
                Write(AutomationCommandIds.StudioSaveTimelineNode,
                    AutomationScope.ControlReplaySave, mode),
                Write(AutomationCommandIds.StudioRestoreTimelineNode,
                    AutomationScope.ControlReplaySave, mode),
                Write(AutomationCommandIds.StudioSwitchWorldline,
                    AutomationScope.ControlReplaySave, mode),
                Write(AutomationCommandIds.StudioDeleteTimelineNode,
                    AutomationScope.ControlReplaySave, mode),
                Write(AutomationCommandIds.StudioSaveSequence,
                    AutomationScope.ControlPlayback, mode),
                Write(AutomationCommandIds.StudioOpenSequence,
                    AutomationScope.ControlPlayback, mode),
                Read(
                    AutomationCommandIds.GetRestoreStrategy,
                    AutomationScope.ObserveStatus),
                LocalSideEffect(
                    AutomationCommandIds.ProposeMoviePatch,
                    AutomationScope.MoviePropose),
                Read(
                    AutomationCommandIds.ValidateMoviePatch,
                    AutomationScope.MovieValidate),
                Write(
                    AutomationCommandIds.ApplyMovieBranch,
                    AutomationScope.MovieApplyBranch,
                    mode),
                Write(
                    AutomationCommandIds.ApplyBranchAndSeek,
                    AutomationScope.MovieApplyBranch,
                    mode),
                LocalSideEffect(
                    AutomationCommandIds.ReplaceInputRange,
                    AutomationScope.MovieEdit),
                LocalSideEffect(
                    AutomationCommandIds.InsertInputRange,
                    AutomationScope.MovieEdit),
                LocalSideEffect(
                    AutomationCommandIds.DeleteInputRange,
                    AutomationScope.MovieEdit),
                Write(
                    AutomationCommandIds.Pause,
                    AutomationScope.ControlPlayback,
                    mode),
                Write(
                    AutomationCommandIds.Resume,
                    AutomationScope.ControlPlayback,
                    mode),
                Write(AutomationCommandIds.QuitGame, AutomationScope.ControlPlayback, mode),
                Write(AutomationCommandIds.FullRunStop, AutomationScope.ControlPlayback, mode),
                Write(AutomationCommandIds.StartVideoExport, AutomationScope.ControlPlayback, mode),
                Write(AutomationCommandIds.FinishVideoExport, AutomationScope.ControlPlayback, mode),
                Write(AutomationCommandIds.CancelVideoExport, AutomationScope.ControlPlayback, mode),
                Write(AutomationCommandIds.LoadGameSlot, AutomationScope.ControlPlayback, mode),
                Write(AutomationCommandIds.ReloadGameSlot, AutomationScope.ControlPlayback, mode),
                Write(AutomationCommandIds.RestartRecordingSession, AutomationScope.ControlPlayback, mode),
                Write(AutomationCommandIds.CancelRecordingRestart, AutomationScope.ControlPlayback, mode),
                Write(
                    AutomationCommandIds.StartReplay,
                    AutomationScope.ControlPlayback,
                    mode),
                Write(
                    AutomationCommandIds.StopReplay,
                    AutomationScope.ControlPlayback,
                    mode),
                Write(
                    AutomationCommandIds.Step,
                    AutomationScope.ControlStep,
                    mode),
                Write(
                    AutomationCommandIds.StepWithInput,
                    AutomationScope.ControlInput,
                    mode),
                Write(
                    AutomationCommandIds.QueueInputBatch,
                    AutomationScope.ControlInput,
                    mode),
                Write(
                    AutomationCommandIds.BeginInputBatch,
                    AutomationScope.ControlInput,
                    mode),
                Write(
                    AutomationCommandIds.AppendInputBatch,
                    AutomationScope.ControlInput,
                    mode),
                Write(
                    AutomationCommandIds.CommitInputBatch,
                    AutomationScope.ControlInput,
                    mode),
                Write(
                    AutomationCommandIds.CancelInputBatch,
                    AutomationScope.ControlInput,
                    mode),
                Write(
                    AutomationCommandIds.RunUntil,
                    AutomationScope.ControlRunUntil,
                    mode),
                Write(
                    AutomationCommandIds.StartRecording,
                    AutomationScope.ControlRecording,
                    mode),
                Write(
                    AutomationCommandIds.StopRecording,
                    AutomationScope.ControlRecording,
                    mode),
                Write(
                    AutomationCommandIds.CreateReplaySave,
                    AutomationScope.ControlReplaySave,
                    mode),
                Write(
                    AutomationCommandIds.SetAutoSavePolicy,
                    AutomationScope.ControlReplaySave,
                    mode),
                Write(
                    AutomationCommandIds.RestoreReplaySave,
                    AutomationScope.ControlReplaySave,
                    mode),
                Write(
                    AutomationCommandIds.SeekMovieTick,
                    AutomationScope.ControlReplaySave,
                    mode),
                Write(
                    AutomationCommandIds.ApproveReplaySaveOverwrite,
                    AutomationScope.ControlReplaySave,
                    mode),
                Write(
                    AutomationCommandIds.CancelReplaySaveRestore,
                    AutomationScope.ControlReplaySave,
                    mode),
                Write(
                    AutomationCommandIds.ResumeReplaySaveRestore,
                    AutomationScope.ControlReplaySave,
                    mode)
            };
            var selected = fullRunOnly ? items.Where(item =>
                item.CommandId == AutomationCommandIds.GetStatus
                || item.CommandId == AutomationCommandIds.GetCapabilities
                || item.CommandId == AutomationCommandIds.FullRunUpdateMovie
                || item.CommandId == AutomationCommandIds.FullRunSeek
                || item.CommandId == AutomationCommandIds.FullRunSnapshot
                || item.CommandId == AutomationCommandIds.FullRunStatus
                || item.CommandId == AutomationCommandIds.GetWorldSnapshot
                || item.CommandId == AutomationCommandIds.GetObjectDetails
                || item.CommandId == AutomationCommandIds.FullRunMovie
                || item.CommandId == AutomationCommandIds.BeginFullRunRecording
                || item.CommandId == AutomationCommandIds.BeginFullRunReplay
                || item.CommandId == AutomationCommandIds.FullRunStep
                || item.CommandId == AutomationCommandIds.FullRunPlay
                || item.CommandId == AutomationCommandIds.FullRunPause
                || item.CommandId == AutomationCommandIds.FullRunStop
                || item.CommandId == AutomationCommandIds.StartVideoExport
                || item.CommandId == AutomationCommandIds.CancelVideoExport
                || item.CommandId == AutomationCommandIds.QuitGame
                || item.CommandId == AutomationCommandIds.StudioEditInputs
                || item.CommandId == AutomationCommandIds.StudioGetMovie
                || item.CommandId == AutomationCommandIds.StudioFrameAction
                || item.CommandId == AutomationCommandIds.StudioGetTimeline
                || item.CommandId == AutomationCommandIds.StudioSaveTimelineNode
                || item.CommandId == AutomationCommandIds.StudioRestoreTimelineNode
                || item.CommandId == AutomationCommandIds.StudioSwitchWorldline
                || item.CommandId == AutomationCommandIds.StudioDeleteTimelineNode
                || item.CommandId == AutomationCommandIds.StudioSaveSequence
                || item.CommandId == AutomationCommandIds.StudioOpenSequence) : items;
            capabilities = selected.ToDictionary(
                item => item.CommandId,
                StringComparer.Ordinal);
        }

        public IReadOnlyCollection<AutomationCapability> Items =>
            capabilities.Values
                .OrderBy(
                    item => item.CommandId,
                    StringComparer.Ordinal)
                .ToArray();

        public bool TryGet(
            string commandId,
            out AutomationCapability capability)
        {
            return capabilities.TryGetValue(
                commandId,
                out capability!);
        }

        public string Serialize()
        {
            return string.Join(
                "\n",
                Items.Select(
                    item =>
                        item.CommandId
                        + "|scope="
                        + item.Scope
                        + "|readOnly="
                        + (item.ReadOnly ? "true" : "false")
                        + "|requiresLease="
                        + (item.RequiresLease ? "true" : "false")
                        + "|availability="
                        + item.Availability
                        + "|preconditions="
                        + item.Preconditions
                        + "|sideEffects="
                        + item.SideEffects));
        }

        public string SerializeJson()
        {
            return AutomationCapabilityCodec.SerializeArray(Items);
        }

        private static AutomationCapability Read(
            string command,
            string scope)
        {
            return new AutomationCapability(
                command,
                scope,
                true,
                false,
                "available",
                "authenticated current-user session; freshness enforced",
                "none");
        }

        private static AutomationCapability Write(
            string command,
            string scope,
            AutomationMode mode)
        {
            return new AutomationCapability(
                command,
                scope,
                false,
                true,
                mode == AutomationMode.ApprovedControl
                    ? "available"
                    : "disabled",
                "ApprovedControl; exclusive lease; expected mode/tick",
                "typed deterministic Runtime control");
        }

        private static AutomationCapability FullRunObservation(
            string command,
            string scope,
            bool fullRunOnly)
        {
            return new AutomationCapability(
                command,
                scope,
                true,
                false,
                fullRunOnly ? "available" : "unsupported",
                fullRunOnly
                    ? "authenticated current-user session; immutable full-run snapshot"
                    : "full-run v2 session only",
                "none");
        }

        private static AutomationCapability LocalSideEffect(
            string command,
            string scope)
        {
            return new AutomationCapability(
                command,
                scope,
                false,
                false,
                "available",
                "authenticated current-user session; exact base movie",
                "content-addressed isolated proposal branch only");
        }

    }
}
