using System;
using System.Collections.Generic;

namespace HollowKnightTAS.Core.Automation
{
    public static class AutomationCommandIds
    {
        public const string GetStatus = "getStatus";
        public const string StartVideoExport = "startVideoExport";
        public const string FinishVideoExport = "finishVideoExport";
        public const string CancelVideoExport = "cancelVideoExport";
        public const string GetStartupProfile = "getStartupProfile";
        public const string GetCapabilities = "getCapabilities";
        public const string GetState = "getState";
        public const string GetCombatState = "getCombatState";
        public const string GetWorldSnapshot = "getWorldSnapshot";
        public const string GetObjectDetails = "getObjectDetails";
        public const string GetTimeline = "getTimeline";
        public const string GetDesync = "getDesync";
        public const string GetReplaySaves = "getReplaySaves";
        public const string GetMovie = "getMovie";
        public const string GetRestoreStrategy = "getRestoreStrategy";
        public const string AcquireControl = "acquireControl";
        public const string RenewControl = "renewControl";
        public const string ReleaseControl = "releaseControl";
        public const string Pause = "pause";
        public const string Resume = "resume";
        public const string QuitGame = "quitGame";
        public const string LoadGameSlot = "loadGameSlot";
        public const string ReloadGameSlot = "reloadGameSlot";
        public const string RestartRecordingSession = "restartRecordingSession";
        public const string CancelRecordingRestart = "cancelRecordingRestart";
        public const string Step = "step";
        public const string StepWithInput = "stepWithInput";
        public const string QueueInputBatch = "queueInputBatch";
        public const string BeginInputBatch = "beginInputBatch";
        public const string AppendInputBatch = "appendInputBatch";
        public const string CommitInputBatch = "commitInputBatch";
        public const string CancelInputBatch = "cancelInputBatch";
        public const string RunUntil = "runUntil";
        public const string StartRecording = "startRecording";
        public const string StopRecording = "stopRecording";
        public const string FullRunUpdateMovie = "fullRunUpdateMovie";
        public const string FullRunSeek = "fullRunSeek";
        public const string FullRunSnapshot = "fullRunSnapshot";
        public const string FullRunStatus = "fullRunStatus";
        public const string BeginFullRunRecording = "beginFullRunRecording";
        public const string BeginFullRunReplay = "beginFullRunReplay";
        public const string FullRunStep = "fullRunStep";
        public const string FullRunPlay = "fullRunPlay";
        public const string FullRunPause = "fullRunPause";
        public const string FullRunStop = "fullRunStop";
        public const string FullRunMovie = "fullRunMovie";
        public const string StudioEditInputs = "studioEditInputs";
        public const string StudioGetMovie = "studioGetMovie";
        public const string StudioFrameAction = "studioFrameAction";
        public const string StudioGetTimeline = "studioGetTimeline";
        public const string StudioSaveTimelineNode = "studioSaveTimelineNode";
        public const string StudioRestoreTimelineNode = "studioRestoreTimelineNode";
        public const string StudioSwitchWorldline = "studioSwitchWorldline";
        public const string StudioDeleteTimelineNode = "studioDeleteTimelineNode";
        public const string StudioSaveSequence = "studioSaveSequence";
        public const string StudioOpenSequence = "studioOpenSequence";
        public const string StartReplay = "startReplay";
        public const string StopReplay = "stopReplay";
        public const string CreateReplaySave = "createReplaySave";
        public const string SetAutoSavePolicy = "setAutoSavePolicy";
        public const string RestoreReplaySave = "restoreReplaySave";
        public const string SeekMovieTick = "seekMovieTick";
        public const string ApproveReplaySaveOverwrite =
            "approveReplaySaveOverwrite";
        public const string CancelReplaySaveRestore =
            "cancelReplaySaveRestore";
        public const string ResumeReplaySaveRestore =
            "resumeReplaySaveRestore";
        public const string ProposeMoviePatch = "proposeMoviePatch";
        public const string ValidateMoviePatch =
            "validateMoviePatch";
        public const string ApplyMovieBranch = "applyMovieBranch";
        public const string ApplyBranchAndSeek = "applyBranchAndSeek";
        public const string ReplaceInputRange = "replaceInputRange";
        public const string InsertInputRange = "insertInputRange";
        public const string DeleteInputRange = "deleteInputRange";
        public const string SetHeroPose = "setHeroPose";
        public const string SetPlayerResources =
            "setPlayerResources";

        private static readonly HashSet<string> Known =
            new HashSet<string>(
                new[]
                {
                    GetStatus,
                    StartVideoExport,
                    FinishVideoExport,
                    CancelVideoExport,
                    GetStartupProfile,
                    GetCapabilities,
                    GetState,
                    GetCombatState,
                    GetWorldSnapshot,
                    GetObjectDetails,
                    GetTimeline,
                    GetDesync,
                    GetReplaySaves,
                    GetMovie,
                    GetRestoreStrategy,
                    AcquireControl,
                    RenewControl,
                    ReleaseControl,
                    Pause,
                    Resume,
                    QuitGame,
                    LoadGameSlot,
                    ReloadGameSlot,
                    RestartRecordingSession,
                    CancelRecordingRestart,
                    Step,
                    StepWithInput,
                    QueueInputBatch,
                    BeginInputBatch,
                    AppendInputBatch,
                    CommitInputBatch,
                    CancelInputBatch,
                    RunUntil,
                    StartRecording,
                    StopRecording,
                    FullRunUpdateMovie,
                    FullRunSeek,
                    FullRunSnapshot,
                    FullRunStatus,
                    BeginFullRunRecording,
                    BeginFullRunReplay,
                    FullRunStep,
                    FullRunPlay,
                    FullRunPause,
                    FullRunStop,
                    FullRunMovie,
                    StudioEditInputs,
                    StudioGetMovie,
                    StudioFrameAction,
                    StudioGetTimeline,
                    StudioSaveTimelineNode,
                    StudioRestoreTimelineNode,
                    StudioSwitchWorldline,
                    StudioDeleteTimelineNode,
                    StudioSaveSequence,
                    StudioOpenSequence,
                    StartReplay,
                    StopReplay,
                    CreateReplaySave,
                    SetAutoSavePolicy,
                    RestoreReplaySave,
                    SeekMovieTick,
                    ApproveReplaySaveOverwrite,
                    CancelReplaySaveRestore,
                    ResumeReplaySaveRestore,
                    ProposeMoviePatch,
                    ValidateMoviePatch,
                    ApplyMovieBranch,
                    ApplyBranchAndSeek,
                    ReplaceInputRange,
                    InsertInputRange,
                    DeleteInputRange,
                    SetHeroPose,
                    SetPlayerResources
                },
                StringComparer.Ordinal);

        public static bool IsKnown(string? value)
        {
            return value != null && Known.Contains(value);
        }
    }
}
