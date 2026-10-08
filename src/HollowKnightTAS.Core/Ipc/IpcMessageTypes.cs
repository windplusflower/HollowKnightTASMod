using System;
using System.Collections.Generic;

namespace HollowKnightTAS.Core.Ipc
{
    public static class IpcMessageTypes
    {
        public const string Hello = "hello";
        public const string HelloAck = "helloAck";
        public const string RegisterSession = "registerSession";
        public const string RegisterSessionAck = "registerSessionAck";
        public const string ControlAck = "controlAck";
        public const string ShutdownSession = "shutdownSession";
        public const string RequestCompanionExit = "requestCompanionExit";
        public const string UploadMovieBegin = "uploadMovieBegin";
        public const string UploadMovieChunk = "uploadMovieChunk";
        public const string UploadMovieEnd = "uploadMovieEnd";
        public const string StartReplay = "startReplay";
        public const string StartVideoExport = "startVideoExport";
        public const string FinishVideoExport = "finishVideoExport";
        public const string CancelVideoExport = "cancelVideoExport";
        public const string StopReplay = "stopReplay";
        public const string Pause = "pause";
        public const string Step = "step";
        public const string RunInputBatch = "runInputBatch";
        public const string Resume = "resume";
        public const string QuitGame = "quitGame";
        public const string StartupHandoff = "startupHandoff";
        public const string LoadGameSlot = "loadGameSlot";
        public const string ReloadGameSlot = "reloadGameSlot";
        public const string Subscribe = "subscribe";
        public const string Unsubscribe = "unsubscribe";
        public const string RequestSnapshot = "requestSnapshot";
        public const string RequestMovie = "requestMovie";
        public const string RunUntil = "runUntil";
        public const string StartRecording = "startRecording";
        public const string StopRecording = "stopRecording";
        public const string FullRunUpdateMovie = "fullRunUpdateMovie";
        public const string FullRunSeek = "fullRunSeek";
        public const string FullRunSnapshot = "fullRunSnapshot";
        public const string FullRunStatus = "fullRunStatus";
        public const string GetWorldSnapshot = "getWorldSnapshot";
        public const string GetObjectDetails = "getObjectDetails";
        public const string FullRunStop = "fullRunStop";
        public const string FullRunMovie = "fullRunMovie";
        public const string SceneCheckpoint = "sceneCheckpoint";
        public const string SceneCheckpointData = "sceneCheckpointData";
        public const string FullRunState = "fullRunState";
        public const string FullRunMovieDocument = "fullRunMovieDocument";
        public const string WorldSnapshot = "worldSnapshot";
        public const string ObjectDetails = "objectDetails";
        public const string SetHeroPose = "setHeroPose";
        public const string SetPlayerResources =
            "setPlayerResources";
        public const string CommitStateMutation =
            "commitStateMutation";
        public const string CreateReplaySave = "createReplaySave";
        public const string ListReplaySaves = "listReplaySaves";
        public const string RestoreReplaySave = "restoreReplaySave";
        public const string SeekMovieTick = "seekMovieTick";
        public const string BeginColdRestore = "beginColdRestore";
        public const string ReleaseColdRestoreBaseline =
            "releaseColdRestoreBaseline";
        public const string PrepareColdRestore = "prepareColdRestore";
        public const string QuiesceColdRestoreSource =
            "quiesceColdRestoreSource";
        public const string ExitColdRestoreSource =
            "exitColdRestoreSource";
        public const string CancelColdRestoreSource =
            "cancelColdRestoreSource";
        public const string ApproveReplaySaveOverwrite =
            "approveReplaySaveOverwrite";
        public const string CancelReplaySaveRestore =
            "cancelReplaySaveRestore";
        public const string ResumeReplaySaveRestore =
            "resumeReplaySaveRestore";
        public const string SetAutoSavePolicy = "setAutoSavePolicy";
        public const string RequestCapabilityCatalog =
            "requestCapabilityCatalog";
        public const string RequestStartupProfileAttestation =
            "requestStartupProfileAttestation";
        public const string ReportNativeEvidence =
            "reportNativeEvidence";
        public const string Ping = "ping";
        public const string Pong = "pong";
        public const string CommandAccepted = "commandAccepted";
        public const string CommandRejected = "commandRejected";
        public const string RuntimeModeChanged = "runtimeModeChanged";
        public const string TickLedger = "tickLedger";
        public const string WatchFrame = "watchFrame";
        public const string Milestone = "milestone";
        public const string Desync = "desync";
        public const string ReplaySaveCreated = "replaySaveCreated";
        public const string ReplaySaveCatalog = "replaySaveCatalog";
        public const string ReplaySaveRestoreProgress =
            "replaySaveRestoreProgress";
        public const string MovieSeekProgress = "movieSeekProgress";
        public const string RestoreAccelerationStatus =
            "restoreAccelerationStatus";
        public const string CapabilityCatalog = "capabilityCatalog";
        public const string NativeCapabilityEvidence =
            "nativeCapabilityEvidence";
        public const string Backpressure = "backpressure";
        public const string Fault = "fault";
        public const string RuntimeStatus = "runtimeStatus";
        public const string MovieDocument = "movieDocument";
        public const string StateMutationResult =
            "stateMutationResult";
        public const string ColdRestoreIntentPrepared =
            "coldRestoreIntentPrepared";
        public const string ColdRestoreSourceQuiesced =
            "coldRestoreSourceQuiesced";
        public const string StartupProfileAttestation =
            "startupProfileAttestation";

        private static readonly HashSet<string> RuntimeCommands =
            new HashSet<string>(
                new[]
                {
                    UploadMovieBegin,
                    UploadMovieChunk,
                    UploadMovieEnd,
                    StartReplay,
                    StartVideoExport,
                    FinishVideoExport,
                    CancelVideoExport,
                    StopReplay,
                    Pause,
                    Step,
                    RunInputBatch,
                    Resume,
                    QuitGame,
                    StartupHandoff,
                    LoadGameSlot,
                    ReloadGameSlot,
                    Subscribe,
                    Unsubscribe,
                    RequestSnapshot,
                    RequestMovie,
                    RunUntil,
                    StartRecording,
                    StopRecording,
                    FullRunUpdateMovie,
                    FullRunSeek,
                    FullRunSnapshot,
                    FullRunStatus,
                    GetWorldSnapshot,
                    GetObjectDetails,
                    FullRunStop,
                    FullRunMovie,
                    SceneCheckpoint,
                    SetHeroPose,
                    SetPlayerResources,
                    CommitStateMutation,
                    CreateReplaySave,
                    ListReplaySaves,
                    RestoreReplaySave,
                    SeekMovieTick,
                    BeginColdRestore,
                    ReleaseColdRestoreBaseline,
                    PrepareColdRestore,
                    QuiesceColdRestoreSource,
                    ExitColdRestoreSource,
                    CancelColdRestoreSource,
                    ApproveReplaySaveOverwrite,
                    CancelReplaySaveRestore,
                    ResumeReplaySaveRestore,
                    SetAutoSavePolicy,
                    RequestCapabilityCatalog,
                    RequestStartupProfileAttestation,
                    ReportNativeEvidence,
                    Ping
                },
                StringComparer.Ordinal);

        private static readonly HashSet<string> RuntimeEvents =
            new HashSet<string>(
                new[]
                {
                    HelloAck,
                    CommandAccepted,
                    CommandRejected,
                    RuntimeModeChanged,
                    TickLedger,
                    WatchFrame,
                    Milestone,
                    Desync,
                    ReplaySaveCreated,
                    ReplaySaveCatalog,
                    ReplaySaveRestoreProgress,
                    MovieSeekProgress,
                    RestoreAccelerationStatus,
                    CapabilityCatalog,
                    NativeCapabilityEvidence,
                    Backpressure,
                    Fault,
                    Pong,
                    RuntimeStatus,
                    MovieDocument,
                    FullRunState,
                    FullRunMovieDocument,
                    SceneCheckpointData,
                    WorldSnapshot,
                    ObjectDetails,
                    StateMutationResult,
                    ColdRestoreIntentPrepared,
                    ColdRestoreSourceQuiesced,
                    StartupProfileAttestation
                },
                StringComparer.Ordinal);

        public static bool IsRuntimeCommand(string value)
        {
            return value != null && RuntimeCommands.Contains(value);
        }

        public static bool IsRuntimeEvent(string value)
        {
            return value != null && RuntimeEvents.Contains(value);
        }
    }
}
