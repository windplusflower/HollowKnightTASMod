using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using HollowKnightTAS.Companion.Automation;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Core.Automation;
using HollowKnightTAS.Core.Input;
using HollowKnightTAS.Core.Ipc;
using HollowKnightTAS.Core.Movie;
using Microsoft.Win32;

namespace HollowKnightTAS.Companion.ViewModels
{
    public sealed class SessionViewItem
    {
        public SessionViewItem(RuntimeSessionClient client)
        {
            Client = client;
        }

        public RuntimeSessionClient Client { get; }
        public string DisplayName =>
            "PID "
            + Client.GameProcessId.ToString(
                CultureInfo.InvariantCulture)
            + " · "
            + Client.SessionId.Substring(
                0,
                Math.Min(12, Client.SessionId.Length));
    }

    public sealed partial class MainViewModel : INotifyPropertyChanged
    {
        private const int MaximumTimelineItems = 5000;
        private readonly SessionRegistry registry;
        private readonly MovieEditorService movieEditor;
        private readonly CapabilityBroker capabilityBroker;
        private readonly NativeHostLauncher nativeHostLauncher;
        private readonly AutomationBroker automationBroker;
        private readonly Func<string, Task>? launchGame;
        private readonly Action? exitProtectedGameProcess;
        private readonly Func<Task>? restartProtectedGame;
        private readonly Func<bool, Task>? finishRestorePresentation;
        private readonly StartupBootController? startupBoot;
        private readonly FullRunMovieCoordinator? fullRunMovies;
        private readonly HashSet<string> warmedSessions =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> readySessions =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> nativeObservedSessions =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly object nativeObserveSync = new object();
        private readonly List<AsyncRelayCommand> runtimeCommands = new List<AsyncRelayCommand>();
        private SessionViewItem? selectedSession;
        private string movieText = string.Empty;
        private string validationOutput =
            "Open or paste an HK-TAS Movie v1 document.";
        private string canonicalDiff =
            "Validate a movie to compare it with canonical HK-TAS Movie v1 text.";
        private string firstDifference =
            "No desync or first-difference event has been received.";
        private string status = "Waiting for a Runtime session.";
        private string connectionBadge = "DISCONNECTED";
        private string runtimeReadinessBadge = "NOT READY";
        private string runtimeSummary = "No Runtime status yet.";
        private string latestState = string.Empty;
        private string videoExportStatus = "未开始视频导出。导出期间游戏扬声器会静音。";
        private string videoExportOperationId = string.Empty;
        private bool autoSaveEnabled = true;
        private string autoSaveInterval = "18000";
        private string autoSaveRetention = "20";
        private string? selectedReplaySave;
        private string restoreStatus =
            "Replay-save catalog has not been requested.";
        private string nativeEvidence =
            "Native process observation has not been requested.";
        private string automationControlStatus =
            "External automation is read-only by default. "
            + "Approved control requires the in-game setting, restart, "
            + "and a short exclusive lease.";
        private string frameInputHold = "-";
        private string frameInputCount = "1";
        private string timelineStartTick = "0";
        private bool timelineIncludeLifecycle;
        private bool timelineEditStoredBranch;
        private readonly LifecycleBranchHistory lifecycleBranchHistory = new LifecycleBranchHistory();
        private string timelineDeleteCount = "1";
        private string timelineReplacementHold = "-";
        private string timelineReplacementCount = "1";
        private string seekTargetTick = "0";
        private string branchMovieId = string.Empty;
        private string runUntilTargetTick = "0";
        private string authoringStatus =
            "Pause first, then submit one frame or a bounded input batch.";
        private readonly Stack<string> timelineUndo =
            new Stack<string>();
        private readonly Stack<string> timelineRedo =
            new Stack<string>();
        private long currentMovieTick = -1;
        private int currentSceneEpoch = -1;
        private string currentControlMode = string.Empty;

        public MainViewModel(
            SessionRegistry registry,
            MovieEditorService movieEditor,
            CapabilityBroker capabilityBroker,
            NativeHostLauncher nativeHostLauncher,
            AutomationBroker automationBroker,
            Func<string, Task>? launchGame = null,
            StartupBootController? startupBoot = null,
            FullRunMovieCoordinator? fullRunMovies = null,
            Action? exitProtectedGameProcess = null, Func<Task>? restartProtectedGame = null,
            Func<bool, Task>? finishRestorePresentation = null)
        {
            this.registry = registry;
            this.movieEditor = movieEditor;
            this.capabilityBroker = capabilityBroker;
            this.nativeHostLauncher = nativeHostLauncher;
            this.automationBroker = automationBroker;
            this.exitProtectedGameProcess = exitProtectedGameProcess;
            this.restartProtectedGame = restartProtectedGame;
            this.finishRestorePresentation = finishRestorePresentation;
            automationBroker.ColdRestoreChanged += (_, args) => Dispatch(() =>
            {
                var record = args.Snapshot.Latest;
                var progress = "恢复 " + record.State + " · " + record.DetailCode + " · " + record.OperationId;
                RestoreStatus = progress;
                QuickSlotStatus = progress;
                Status = progress;
            });
            automationBroker.SlotRecoveryChanged += (_, _) => Dispatch(() =>
            {
                var recovery = automationBroker.LatestSlotRecovery;
                if (recovery != null)
                    RestoreStatus = "槽恢复 " + recovery.Status + " · " + recovery.OperationId + " · " + recovery.Detail;
            });
            this.launchGame = launchGame;
            this.startupBoot = startupBoot;
            this.fullRunMovies = fullRunMovies;
            automationBroker.StudioFullRunCommand = DispatchStudioFullRunCommandAsync;
            if (startupBoot != null) startupBoot.Changed += (_, _) => Dispatch(() =>
            {
                OnPropertyChanged(nameof(CanLaunchStandaloneGame));
                OnPropertyChanged(nameof(FrameCounterText));
                OnPropertyChanged(nameof(PlaybackStateText));
                OnPropertyChanged(nameof(PlayPauseLabel));
                OnPropertyChanged(nameof(SaveTimelineNodeCommand));
                OnPropertyChanged(nameof(RestoreTimelineNodeCommand));
                foreach (var command in runtimeCommands) command.RaiseCanExecuteChanged();
                Status = startupBoot.FullRunFaultCode != 0
                    ? "全流程已失败（" + startupBoot.FullRunFaultCode + "）；请重新启动会话。"
                    : startupBoot.IsWaiting
                    ? fullRunMovies?.IsArmed == true
                        ? "全流程 Movie 已就绪；可按原生帧步进或播放。"
                        : fullRunMovies?.Mode == "Completed"
                            ? "序列已到末尾；Play／逐帧将重放恢复到末尾后接续草稿。"
                            : startupBoot.NativeCompletedFrames == 0
                                ? "已停在第 0 帧；直接播放或逐帧会自动新建 Movie，也可打开已有序列。"
                                : "全流程 Movie 已在第 " + startupBoot.NativeCompletedFrames
                                    + " 帧停止。"
                    : startupBoot.IsPending ? "等待下一启动帧边界…" : "启动门闩已释放；等待 Runtime 连接。";
            });
            InitializeInputGrid();
            InitializeQuickSlots();
            InitializeShortcutSettings();
            InitializeSequenceSettings();
            ExportDiagnosticLogsCommand = new AsyncRelayCommand(ExportDiagnosticLogsAsync);
            InitializeFullRunSettings();
            InitializeColliderOverlay();
            InitializeInfoOverlay();
            LaunchGameCommand = new AsyncRelayCommand(LaunchGameAsync, () => this.launchGame != null);
            NewFullRunMovieCommand = new RelayCommand(NewFullRunMovie);
            OpenMovieCommand = new AsyncRelayCommand(OpenMovieAsync);
            SaveMovieCommand = new AsyncRelayCommand(() => SaveSequenceAsync(false));
            SaveMovieAsCommand = new AsyncRelayCommand(() => SaveSequenceAsync(true));
            ValidateMovieCommand =
                new RelayCommand(() => ValidateMovie(false));
            FormatMovieCommand =
                new RelayCommand(() => ValidateMovie(true));
            UploadMovieCommand =
                Command(UploadMovieAsync);
            RefreshRuntimeMovieCommand =
                Command(RefreshRuntimeMovieAsync);
            StartReplayCommand =
                Command(
                    () => ExecuteHumanAsync(
                        AutomationCommandIds.StartReplay,
                        AutomationScope.ControlPlayback));
            StopReplayCommand =
                Command(async () =>
                {
                    if (fullRunMovies?.IsPending == true)
                    {
                        if (!fullRunMovies.IsArmed)
                            throw new InvalidOperationException("第 0 帧尚未选择 Movie。");
                        if (startupBoot?.IsWaiting != true)
                        {
                            var paused = await fullRunMovies.PauseAsync(CancellationToken.None);
                            if (paused.Mode == "Fault")
                                throw new InvalidOperationException(paused.Error);
                        }
                        var frame = startupBoot!.NativeCompletedFrames;
                        var result = await automationBroker.ExecuteHumanAsync(
                            AutomationCommandIds.FullRunStop,
                            AutomationScope.ControlPlayback,
                            new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["expectedNativeFrame"] = frame.ToString(CultureInfo.InvariantCulture)
                            },
                            "Paused", null, CancellationToken.None);
                        RequireAutomationSuccess(result);
                        fullRunMovies.MarkStopped();
                        OnPropertyChanged(nameof(PlaybackStateText));
                        foreach (var runtimeCommand in runtimeCommands)
                            runtimeCommand.RaiseCanExecuteChanged();
                        if (result.Data.TryGetValue("available", out var available)
                            && available == "true" && result.Data.TryGetValue("path", out var path))
                        {
                            var absolute = Path.GetFullPath(path);
                            var shadow = Path.GetFullPath(fullRunMovies.ShadowRoot);
                            if (!absolute.StartsWith(shadow + Path.DirectorySeparatorChar,
                                    StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("Recorded movie path escaped the protected session.");
                            MovieText = await File.ReadAllTextAsync(absolute, new UTF8Encoding(false, true));
                            ValidateMovie(false);
                            Status = "全流程 Movie 已停止并读入 Studio。";
                        }
                        else Status = "全流程回放已停止。";
                        return;
                    }
                    await ExecuteHumanAsync(AutomationCommandIds.StopReplay,
                        AutomationScope.ControlPlayback);
                }, allowStartupContinue: true);
            StartVideoExportCommand = new AsyncRelayCommand(async () =>
            {
                try { await StartVideoExportAsync(); }
                catch (Exception exception) { VideoExportStatus = Status = "MP4 导出失败：" + exception.Message; }
            }, () => CanStartStudioVideo() || (!gridApplying && !videoExportBusy
                && startupBoot?.IsPending != true && SelectedSession?.Client.IsConnected == true));
            CancelVideoExportCommand = new AsyncRelayCommand(async () =>
            {
                try { await CancelVideoExportAsync(); }
                catch (Exception exception) { VideoExportStatus = Status = exception.Message; }
            }, () => videoExportBusy || !string.IsNullOrEmpty(videoExportOperationId));
            runtimeCommands.Add((AsyncRelayCommand)StartVideoExportCommand);
            runtimeCommands.Add((AsyncRelayCommand)CancelVideoExportCommand);
            PauseCommand =
                Command(
                    () => ExecuteHumanAsync(
                        AutomationCommandIds.Pause,
                        AutomationScope.ControlPlayback));
            TogglePauseCommand = Command(async () =>
            {
                if (videoExportBusy) { await ToggleVideoPauseAsync(); return; }
                if (this.startupBoot?.IsWaiting == true || CanRestartSelectedDraft)
                {
                    if (fullRunMovies?.IsPending == true)
                    {
                        if (fullRunMovies.Mode == "Unarmed" && !draftRequiresRestart) NewFullRunMovie();
                        await ApplyPendingInputsAsync();
                        var boundary = await fullRunMovies.RunAsync(
                            this.startupBoot!.NativeCompletedFrames, CancellationToken.None);
                        if (boundary.Mode == "Fault") throw new InvalidOperationException(boundary.Error);
                    }
                    else this.startupBoot!.Continue();
                    return;
                }
                if (fullRunMovies?.IsPending == true)
                {
                    var boundary = await fullRunMovies.PauseAsync(CancellationToken.None);
                    if (boundary.Mode == "Fault") throw new InvalidOperationException(boundary.Error);
                    this.startupBoot?.Refresh();
                    return;
                }
                if (currentControlMode != "Paused" && currentControlMode != "Running" && currentControlMode != "Stepping")
                    throw new InvalidOperationException("等待最新运行状态后再切换播放/暂停。");
                await ExecuteHumanAsync(currentControlMode == "Paused"
                    ? AutomationCommandIds.Resume : AutomationCommandIds.Pause, AutomationScope.ControlPlayback);
            }, allowStartupContinue: true, allowCompletedReplay: true, allowDuringVideoExport: true);
            PlayCommand = new RelayCommand(() =>
            {
                if (CanContinuePlayback && TogglePauseCommand.CanExecute(null)) TogglePauseCommand.Execute(null);
            });
            StepCommand =
                Command(
                    async () =>
                    {
                        var keyboardFrame = pendingKeyboardFrame;
                        pendingKeyboardFrame = null;
                        if (this.startupBoot?.IsPending == true)
                        {
                            if (fullRunMovies?.IsPending == true)
                            {
                                if (fullRunMovies.Mode == "Unarmed" && !draftRequiresRestart) NewFullRunMovie();
                                if (keyboardFrame != null) await WriteKeyboardFrameAsync(keyboardFrame);
                                await ApplyPendingInputsAsync();
                                var boundary = await fullRunMovies.StepAsync(
                                    this.startupBoot.NativeCompletedFrames, CancellationToken.None);
                                if (boundary.Mode == "Fault") throw new InvalidOperationException(boundary.Error);
                            }
                            else this.startupBoot.Step();
                            this.startupBoot.Refresh();
                            return;
                        }
                        await ExecuteHumanAsync(
                        AutomationCommandIds.Step,
                        AutomationScope.ControlStep,
                        Fields(
                            "count",
                            "1"));
                    }, allowStartupStep: true, allowCompletedReplay: true);
            ResumeCommand =
                Command(
                    () => ExecuteHumanAsync(
                        AutomationCommandIds.Resume,
                        AutomationScope.ControlPlayback));
            QuitGameCommand = new AsyncRelayCommand(async () =>
            {
                try { await QuitGameAsync(); }
                catch (Exception exception) { Status = exception.Message; }
            }, () => fullRunMovies?.IsPending == true
                || (startupBoot?.IsPending != true
                    && SelectedSession?.Client.IsConnected == true));
            runtimeCommands.Add((AsyncRelayCommand)QuitGameCommand);
            LoadGameSlotCommand = Command(() => ExecuteHumanAsync(
                AutomationCommandIds.LoadGameSlot, AutomationScope.ControlPlayback,
                Fields("slot", ParseCount(GameSlot, 1, 4, "game slot").ToString(CultureInfo.InvariantCulture))));
            RestartRecordingSessionCommand = Command(RestartRecordingSessionAsync);
            ReloadGameSlotCommand = Command(() => ExecuteHumanAsync(
                AutomationCommandIds.ReloadGameSlot, AutomationScope.ControlPlayback,
                Fields("slot", ParseCount(GameSlot, 1, 4, "game slot").ToString(CultureInfo.InvariantCulture))));
            CancelRecordingRestartCommand = Command(() => ExecuteHumanAsync(
                AutomationCommandIds.CancelRecordingRestart, AutomationScope.ControlPlayback,
                Fields("operationId", automationBroker.ActiveRecordingRestartOperationId), expectedModeRequired: false),
                requireConnected: false);
            SnapshotCommand =
                Command(RefreshStructuredStateAsync);
            RefreshCombatStateCommand =
                Command(RefreshCombatStateAsync);
            SubscribeWatchCommand =
                Command(
                    () => SendAsync(
                        IpcMessageTypes.Subscribe,
                        Fields("stream", "watch")));
            SubscribeLedgerCommand =
                Command(
                    () => SendAsync(
                        IpcMessageTypes.Subscribe,
                        Fields("stream", "ledger")));
            ClearTimelineCommand =
                new RelayCommand(Timeline.Clear);
            RefreshReplaySavesCommand =
                Command(
                    () => ExecuteHumanAsync(
                        AutomationCommandIds.GetReplaySaves,
                        AutomationScope.ObserveReplaySaves,
                        expectedModeRequired: false));
            CreateReplaySaveCommand =
                Command(
                    () => ExecuteHumanAsync(
                        AutomationCommandIds.CreateReplaySave,
                        AutomationScope.ControlReplaySave,
                        Fields(
                            "label",
                            "Studio "
                            + DateTime.Now.ToString(
                                "yyyy-MM-dd HH:mm:ss",
                                CultureInfo.InvariantCulture))));
            RestoreReplaySaveCommand =
                Command(RestoreSelectedAsync);
            SetAutoSavePolicyCommand = Command(() => ExecuteHumanAsync(
                AutomationCommandIds.SetAutoSavePolicy, AutomationScope.ControlReplaySave,
                Fields("enabled", AutoSaveEnabled ? "true" : "false",
                    "intervalMovieTicks", AutoSaveInterval,
                    "retentionCount", AutoSaveRetention)));
            RefreshAutoSavePolicyCommand = Command(RefreshAutoSavePolicyAsync);
            ApproveReplaySaveOverwriteCommand =
                Command(
                    () => ExecuteHumanAsync(
                        AutomationCommandIds
                            .ApproveReplaySaveOverwrite,
                        AutomationScope.ControlReplaySave,
                        Fields("approved", "true")));
            DenyReplaySaveOverwriteCommand =
                Command(
                    () => ExecuteHumanAsync(
                        AutomationCommandIds
                            .ApproveReplaySaveOverwrite,
                        AutomationScope.ControlReplaySave,
                        Fields("approved", "false")));
            ResumeReplaySaveRestoreCommand =
                Command(
                    () => ExecuteHumanAsync(
                        AutomationCommandIds
                            .ResumeReplaySaveRestore,
                        AutomationScope.ControlReplaySave));
            CancelReplaySaveRestoreCommand =
                Command(CancelReplayRestoreAsync, requireConnected: false);
            RefreshCapabilitiesCommand =
                Command(
                    () => SendSimpleAsync(
                        IpcMessageTypes.RequestCapabilityCatalog));
            CaptureNativeObserveCommand =
                Command(
                    () => CaptureNativeObserveAsync(null));
            RevokeAutomationLeaseCommand =
                new RelayCommand(
                    () => AutomationControlStatus =
                        this.automationBroker
                            .RevokeControlLeaseByUser());
            StepWithInputCommand =
                Command(() => SendFrameInputAsync(singleFrame: true));
            RunInputBatchCommand =
                Command(() => SendFrameInputAsync(singleFrame: false));
            ReplaceInputRangeCommand =
                Command(
                    () => ApplyTimelineEditAsync(
                        TimelineEditKind.Replace));
            InsertInputRangeCommand =
                Command(
                    () => ApplyTimelineEditAsync(
                        TimelineEditKind.Insert));
            DeleteInputRangeCommand =
                Command(
                    () => ApplyTimelineEditAsync(
                        TimelineEditKind.Delete));
            UndoTimelineEditCommand =
                Command(UndoTimelineEditAsync);
            RedoTimelineEditCommand =
                Command(RedoTimelineEditAsync);
            SeekMovieTickCommand = Command(SeekMovieTickAsync);
            ApplyMovieBranchCommand = Command(ApplyMovieBranchAsync);
            ApplyBranchAndSeekCommand =
                Command(ApplyBranchAndSeekAsync);
            RefreshBranchesCommand = Command(() => LoadBranchesAsync(false));
            MoreBranchesCommand = Command(() => LoadBranchesAsync(true));
            StartRecordingCommand =
                Command(
                    () => ExecuteHumanAsync(
                        AutomationCommandIds.StartRecording,
                        AutomationScope.ControlRecording));
            StopRecordingCommand =
                Command(
                    () => ExecuteHumanAsync(
                        AutomationCommandIds.StopRecording,
                        AutomationScope.ControlRecording));
            RunUntilCommand = Command(RunUntilAsync);

            registry.SessionsChanged += OnSessionsChanged;
            registry.EnvelopeReceived += OnEnvelopeReceived;
            RefreshSessions();
            RefreshCapabilityView();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<SessionViewItem> Sessions { get; } =
            new ObservableCollection<SessionViewItem>();
        public ObservableCollection<string> Timeline { get; } =
            new ObservableCollection<string>();
        public ObservableCollection<string> ReplaySaves { get; } =
            new ObservableCollection<string>();
        public ObservableCollection<string> Capabilities { get; } =
            new ObservableCollection<string>();

        public SessionViewItem? SelectedSession
        {
            get => selectedSession;
            set
            {
                if (ReferenceEquals(selectedSession, value))
                {
                    return;
                }

                selectedSession = value;
                UpdateInputBindingLabels(new Dictionary<string, string>());
                currentMovieTick = -1;
                currentFullRunMovieFrame = -1;
                gridProgressRequestId = null;
                gridProgressPollGate.Reset();
                currentControlMode = string.Empty;
                currentSceneEpoch = -1;
                quickSlotCatalog = "[]";
                RenderQuickSlots();
                if (!IsRestorePresentationFrozen) InputRows.UpdateCurrent(-1);
                OnPropertyChanged(nameof(FrameCounterText));
                OnPropertyChanged(nameof(PlaybackStateText));
                OnPropertyChanged(nameof(PlayPauseLabel));
                OnPropertyChanged();
                UpdateConnectionStatus();
            }
        }

        public string MovieText
        {
            get => movieText;
            set
            {
                if (movieText == value) return;
                Set(ref movieText, value);
                if (gridSource != value && !IsRestorePresentationFrozen)
                {
                    InputRows = new VirtualInputRows();
                    OnPropertyChanged(nameof(InputRows));
                    GridStatus = "正在更新序列。";
                }
            }
        }

        public string ValidationOutput
        {
            get => validationOutput;
            private set => Set(ref validationOutput, value);
        }

        public string CanonicalDiff
        {
            get => canonicalDiff;
            private set => Set(ref canonicalDiff, value);
        }

        public string FirstDifference
        {
            get => firstDifference;
            private set => Set(ref firstDifference, value);
        }

        internal void ReportStartupStatus(string detail)
        {
            Status = detail;
            AddTimeline("Startup: " + detail);
            Console.Error.WriteLine("Startup: " + detail);
        }

        public string Status
        {
            get => status;
            private set => Set(ref status, value);
        }

        public string ConnectionBadge
        {
            get => connectionBadge;
            private set => Set(ref connectionBadge, value);
        }

        public string RuntimeReadinessBadge
        {
            get => runtimeReadinessBadge;
            private set => Set(ref runtimeReadinessBadge, value);
        }

        public string RuntimeSummary
        {
            get => IsRestorePresentationFrozen ? "正在恢复，请稍候…" : runtimeSummary;
            private set => Set(ref runtimeSummary, value);
        }

        public bool CanLaunchStandaloneGame => startupBoot?.IsPending != true && SelectedSession?.Client.IsConnected != true;

        public string FrameCounterText => IsRestorePresentationFrozen ? frozenFrameCounter : startupBoot?.IsPending == true
            ? "Movie frame: " + (currentFullRunMovieFrame < 0 ? "—"
                : currentFullRunMovieFrame.ToString(CultureInfo.InvariantCulture))
                + " · Native frame: " + (startupBoot.NativeCompletedFrames < 0 ? "—"
                    : startupBoot.NativeCompletedFrames.ToString(CultureInfo.InvariantCulture))
            : currentMovieTick < 0 ? "Frame: —"
                : "Frame: " + currentMovieTick.ToString(CultureInfo.InvariantCulture);
        public string PlaybackStateText => startupBoot?.IsPending == true
            ? startupBoot.FullRunFaultCode != 0 ? "Full-run fault " + startupBoot.FullRunFaultCode
                : startupBoot.IsCommandPending ? "等待游戏确认命令…"
                : startupBoot.IsWaiting ? "Native frame paused · " + (fullRunMovies?.Mode ?? "Unarmed")
                : "Native frame running"
            : string.IsNullOrEmpty(currentControlMode) ? "No runtime" : currentControlMode;
        public string PlayPauseLabel => startupBoot?.IsPending == true
            ? CanRestartSelectedDraft ? "Play 从起点开始"
                : startupBoot.FullRunFaultCode != 0 ? "已失败 · 需重启"
                : startupBoot.IsCommandPending ? "等待确认…"
                : startupBoot.IsWaiting ? "Play 继续" : "Pause 暂停"
            : currentControlMode == "Paused" ? "Play 继续" : "Pause 暂停";

        public string LatestState
        {
            get => latestState;
            private set => Set(ref latestState, value);
        }

        public string VideoExportStatus
        {
            get => videoExportStatus;
            private set => Set(ref videoExportStatus, value);
        }

        public string? SelectedReplaySave
        {
            get => selectedReplaySave;
            set => Set(ref selectedReplaySave, value);
        }

        public string RestoreStatus
        {
            get => restoreStatus;
            private set => Set(ref restoreStatus, value);
        }

        public string NativeEvidence
        {
            get => nativeEvidence;
            private set => Set(ref nativeEvidence, value);
        }

        public string AutomationControlStatus
        {
            get => automationControlStatus;
            private set => Set(ref automationControlStatus, value);
        }

        public string FrameInputHold
        {
            get => frameInputHold;
            set => Set(ref frameInputHold, value);
        }

        public bool AutoSaveEnabled
        {
            get => autoSaveEnabled;
            set => Set(ref autoSaveEnabled, value);
        }
        public string AutoSaveInterval
        {
            get => autoSaveInterval;
            set => Set(ref autoSaveInterval, value);
        }
        public string AutoSaveRetention
        {
            get => autoSaveRetention;
            set => Set(ref autoSaveRetention, value);
        }

        public string FrameInputCount
        {
            get => frameInputCount;
            set => Set(ref frameInputCount, value);
        }

        public string TimelineStartTick
        {
            get => timelineStartTick;
            set => Set(ref timelineStartTick, value);
        }

        public bool TimelineIncludeLifecycle
        {
            get => timelineIncludeLifecycle;
            set => Set(ref timelineIncludeLifecycle, value);
        }

        public bool TimelineEditStoredBranch
        {
            get => timelineEditStoredBranch;
            set => Set(ref timelineEditStoredBranch, value);
        }

        public string TimelineDeleteCount
        {
            get => timelineDeleteCount;
            set => Set(ref timelineDeleteCount, value);
        }

        public string TimelineReplacementHold
        {
            get => timelineReplacementHold;
            set => Set(ref timelineReplacementHold, value);
        }

        public string TimelineReplacementCount
        {
            get => timelineReplacementCount;
            set => Set(ref timelineReplacementCount, value);
        }

        public string SeekTargetTick
        {
            get => seekTargetTick;
            set => Set(ref seekTargetTick, value);
        }

        public string BranchMovieId
        {
            get => branchMovieId;
            set => Set(ref branchMovieId, value);
        }

        public ObservableCollection<MovieBranchCatalogItem> SavedBranches { get; } = new ObservableCollection<MovieBranchCatalogItem>();
        private MovieBranchCatalogItem? selectedSavedBranch;
        private string nextBranchOffset = string.Empty;
        public MovieBranchCatalogItem? SelectedSavedBranch
        {
            get => selectedSavedBranch;
            set
            {
                Set(ref selectedSavedBranch, value);
                if (value == null) return;
                BranchMovieId = value.BranchId;
                TimelineIncludeLifecycle = value.IncludesLifecycle;
                TimelineEditStoredBranch = value.IncludesLifecycle;
                AuthoringStatus = "已选择分支，游戏未改变。归档内容与执行环境将在应用时校验。";
            }
        }
        public ICommand RefreshBranchesCommand { get; }
        public ICommand MoreBranchesCommand { get; }

        private async Task LoadBranchesAsync(bool more)
        {
            if (more && nextBranchOffset.Length == 0) return;
            var result = await ExecuteHumanResultAsync(AutomationCommandIds.GetMovie, AutomationScope.MovieRead,
                Fields("listBranches", "true", "branchOffset", more ? nextBranchOffset : "0"), expectedModeRequired: false);
            RequireAutomationSuccess(result);
            var entries = System.Text.Json.JsonSerializer.Deserialize<MovieBranchCatalogItem[]>(result.Data["branchesJson"])
                ?? throw new InvalidDataException("Branch catalog is missing.");
            if (!more) SavedBranches.Clear();
            foreach (var entry in entries)
                if (!SavedBranches.Any(x => x.BranchId == entry.BranchId && x.IncludesLifecycle == entry.IncludesLifecycle))
                    SavedBranches.Add(entry);
            nextBranchOffset = result.Data["nextOffset"];
            AuthoringStatus = $"已列出 {SavedBranches.Count} 个分支（目录信息，未校验回放）。"
                + (nextBranchOffset.Length == 0 ? "已到末页。" : "点击更多分支继续读取。");
        }

        public string RunUntilTargetTick
        {
            get => runUntilTargetTick;
            set => Set(ref runUntilTargetTick, value);
        }

        public string AuthoringStatus
        {
            get => authoringStatus;
            private set => Set(ref authoringStatus, value);
        }

        public ICommand OpenMovieCommand { get; }
        public ICommand NewFullRunMovieCommand { get; }
        public ICommand LaunchGameCommand { get; }
        public ICommand SaveMovieCommand { get; }
        public ICommand SaveMovieAsCommand { get; }
        public ICommand PlayCommand { get; }
        private bool CanRestartSelectedDraft => draftRequiresRestart && fullRunMovies?.IsPending == true && restartProtectedGame != null;
        private bool CanContinuePlayback => CanRestartSelectedDraft || (startupBoot?.IsPending == true ? startupBoot.IsWaiting : currentControlMode == "Paused");
        public ICommand ValidateMovieCommand { get; }
        public ICommand FormatMovieCommand { get; }
        public ICommand UploadMovieCommand { get; }
        public ICommand RefreshRuntimeMovieCommand { get; }
        public ICommand StartReplayCommand { get; }
        public ICommand StopReplayCommand { get; }
        public ICommand StartVideoExportCommand { get; }
        public ICommand CancelVideoExportCommand { get; }
        public ICommand PauseCommand { get; }
        public ICommand TogglePauseCommand { get; }
        public ICommand StepCommand { get; }
        public ICommand ResumeCommand { get; }
        public ICommand QuitGameCommand { get; }
        public ICommand LoadGameSlotCommand { get; }
        public ICommand ReloadGameSlotCommand { get; }
        public ICommand RestartRecordingSessionCommand { get; }
        public ICommand CancelRecordingRestartCommand { get; }
        private string gameSlot = "2";
        public string GameSlot
        {
            get => gameSlot;
            set => Set(ref gameSlot, value);
        }
        public ICommand SnapshotCommand { get; }
        public ICommand RefreshCombatStateCommand { get; }
        public ICommand SubscribeWatchCommand { get; }
        public ICommand SubscribeLedgerCommand { get; }
        public ICommand ClearTimelineCommand { get; }
        public ICommand RefreshReplaySavesCommand { get; }
        public ICommand CreateReplaySaveCommand { get; }
        public ICommand SetAutoSavePolicyCommand { get; }
        public ICommand RefreshAutoSavePolicyCommand { get; }
        public ICommand RestoreReplaySaveCommand { get; }
        public ICommand ApproveReplaySaveOverwriteCommand { get; }
        public ICommand DenyReplaySaveOverwriteCommand { get; }
        public ICommand ResumeReplaySaveRestoreCommand { get; }
        public ICommand CancelReplaySaveRestoreCommand { get; }
        public ICommand RefreshCapabilitiesCommand { get; }
        public ICommand CaptureNativeObserveCommand { get; }
        public ICommand RevokeAutomationLeaseCommand { get; }
        public ICommand StepWithInputCommand { get; }
        public ICommand RunInputBatchCommand { get; }
        public ICommand ReplaceInputRangeCommand { get; }
        public ICommand InsertInputRangeCommand { get; }
        public ICommand DeleteInputRangeCommand { get; }
        public ICommand UndoTimelineEditCommand { get; }
        public ICommand RedoTimelineEditCommand { get; }
        public ICommand SeekMovieTickCommand { get; }
        public ICommand ApplyMovieBranchCommand { get; }
        public ICommand ApplyBranchAndSeekCommand { get; }
        public ICommand StartRecordingCommand { get; }
        public ICommand StopRecordingCommand { get; }
        public ICommand RunUntilCommand { get; }

        private async Task StartLegacyVideoExportAsync()
        {
            if (InfoSettings.IncludeInVideo) throw new InvalidOperationException(UiText.T("信息录入视频需要v2序列。"));
            if (!string.Equals(currentControlMode, "Paused", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "请先暂停游戏，并获取最新 Runtime 状态后再导出视频。");
            }

            var candidate = movieEditor.Validate(MovieText);
            if (!candidate.Success || candidate.ExpandedTicks < 1)
            {
                ValidateMovie(false);
                throw new InvalidDataException("当前 MovieText 无法导出。");
            }

            var ffmpegPath = BundledFfmpeg.Resolve();

            var outputDialog = new SaveFileDialog
            {
                Title = UiText.T("选择 MP4 导出文件（不会覆盖已有文件）"),
                Filter = UiText.T("MP4 视频|*.mp4"),
                DefaultExt = ".mp4",
                AddExtension = true,
                OverwritePrompt = false,
                FileName = "hktas-export.mp4"
            };
            if (outputDialog.ShowDialog() != true)
            {
                return;
            }

            var outputPath = outputDialog.FileName;
            if (File.Exists(outputPath))
            {
                throw new IOException("导出文件已存在；为防止覆盖，请选择新的 .mp4 文件。");
            }

            // Upload first so the Runtime receives the exact current MovieText.
            await UploadMovieAsync();
            var maximumFrames = Math.Min(
                int.MaxValue,
                checked(candidate.ExpandedTicks * 4L + 10000L));
            var result = await ExecuteHumanResultAsync(
                AutomationCommandIds.StartVideoExport,
                AutomationScope.ControlPlayback,
                Fields(
                    "ffmpegPath", ffmpegPath,
                    "outputPath", outputPath,
                    "maximumFrames", maximumFrames.ToString(CultureInfo.InvariantCulture),
                    "replayLoadedMovie", "true"));
            RequireAutomationSuccess(result);
            videoExportOperationId = RequireResultField(result, "detail");
            VideoExportStatus = "已开始 · 阶段=Capturing · 帧数=0 · 输出=" + outputPath
                                + "（导出期间游戏扬声器会静音）";
            Status = "视频导出已开始。";
        }

        private async Task CancelLegacyVideoExportAsync()
        {
            if (string.IsNullOrEmpty(videoExportOperationId))
            {
                throw new InvalidOperationException("当前没有可取消的视频导出。");
            }

            var operationId = videoExportOperationId;
            var result = await ExecuteHumanResultAsync(
                AutomationCommandIds.CancelVideoExport,
                AutomationScope.ControlPlayback,
                Fields("operationId", operationId),
                expectedModeRequired: false);
            RequireAutomationSuccess(result);
            VideoExportStatus = "已请求取消 · operationId=" + operationId;
            videoExportOperationId = string.Empty;
            Status = "视频导出取消请求已发送。";
        }

        private AsyncRelayCommand Command(Func<Task> action, bool requireConnected = true, bool allowStartupContinue = false, bool allowStartupStep = false, bool allowCompletedReplay = false, bool allowDuringVideoExport = false)
        {
            var command = new AsyncRelayCommand(
                async () =>
                {
                    try
                    {
                        await action();
                    }
                    catch (Exception exception)
                    {
                        Status = exception.Message;
                    }
                },
                () => (!gridApplying || (allowDuringVideoExport && videoCaptureStarted)) &&
                    ((CanRestartSelectedDraft && (allowStartupContinue || allowStartupStep)) ||
                    (startupBoot?.FullRunFaultCode ?? 0) == 0 && (startupBoot?.IsPending == true
                    ? fullRunMovies?.IsPending == true
                        ? (fullRunMovies.IsArmed || fullRunMovies.Mode == "Unarmed"
                            || (allowCompletedReplay && fullRunMovies.Mode == "Completed" && restartProtectedGame != null)) && !startupBoot.IsCommandPending && (allowStartupContinue
                            || (allowStartupStep && startupBoot.CanStep))
                        : (allowStartupContinue && startupBoot.IsWaiting)
                            || (allowStartupStep && startupBoot.CanStep)
                    : !requireConnected || SelectedSession?.Client.IsConnected == true)));
            runtimeCommands.Add(command);
            return command;
        }

        private async Task QuitGameAsync()
        {
            await CancelVideoExportAndWaitAsync();
            if (fullRunMovies?.IsPending != true)
            {
                await ExecuteHumanAsync(AutomationCommandIds.QuitGame,
                    AutomationScope.ControlPlayback);
                return;
            }

            startupBoot?.Refresh();
            if (startupBoot?.FullRunFaultCode != 0)
            {
                ExitProtectedGameProcess();
                return;
            }
            if (startupBoot?.IsWaiting != true && fullRunMovies.IsArmed)
            {
                NativeFrameBoundary? boundary = null;
                try { boundary = await fullRunMovies.PauseAsync(CancellationToken.None); }
                catch (InvalidOperationException) when (fullRunMovies.Gate?.IsFullRunFinished == true)
                {
                    // Playback completed while the pause request was being prepared.
                }
                if (boundary?.Mode == "Fault")
                {
                    if (startupBoot?.FullRunFaultCode != 0)
                    {
                        ExitProtectedGameProcess();
                        return;
                    }
                    throw new InvalidOperationException(boundary.Error);
                }
                startupBoot?.Refresh();
            }
            if (startupBoot?.IsWaiting != true)
                throw new InvalidOperationException("等待游戏停在原生帧边界后再退出。");

            // Runtime is not connected at frame 0. The verified process is still
            // owned by App, so the UI can close that exact protected process.
            if (fullRunMovies.Mode == "Unarmed")
            {
                ExitProtectedGameProcess();
                return;
            }

            var result = await automationBroker.ExecuteHumanAsync(
                AutomationCommandIds.QuitGame, AutomationScope.ControlPlayback,
                null, "Paused", null, CancellationToken.None);
            if (!result.Success && result.ResultCode == "RuntimeNotReady")
            {
                ExitProtectedGameProcess();
                return;
            }
            RequireAutomationSuccess(result);
            Status = "游戏退出请求已发送。";
        }

        private void ExitProtectedGameProcess()
        {
            if (exitProtectedGameProcess == null)
                throw new InvalidOperationException("受控游戏退出入口尚未就绪。");
            exitProtectedGameProcess();
            Status = "正在退出受保护的游戏进程。";
        }

        private async Task LaunchGameAsync()
        {
            if (launchGame == null) return;
            var dialog = new OpenFileDialog
            {
                Title = UiText.T("选择 Hollow Knight 游戏程序"),
                Filter = UiText.T("Hollow Knight (hollow_knight.exe)|hollow_knight.exe"),
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                Status = "正在校验组件并启动游戏…";
                await launchGame(dialog.FileName);
                Status = startupBoot?.IsWaiting == true
                    ? "已停在原生启动门闩；点击 Play 继续初始化。"
                    : "游戏已启动；等待 Runtime 连接。回退可用性以启动与录制根校验为准。";
            }
            catch (Exception exception)
            {
                Status = "启动失败：" + exception.Message;
            }
        }

        private async Task OpenMovieAsync()
        {
            var dialog = new OpenFileDialog
            {
                Filter =
                    "HK-TAS Sequence (*.hktaspack;*.hktas)|*.hktaspack;*.hktas|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            await OpenMovieFileAsync(dialog.FileName);
        }

        public async Task OpenMovieFileAsync(string path)
        {
            if (gridApplying || sequenceSaving) throw new InvalidOperationException("请等待当前恢复或保存完成。");
            await ApplySequenceAsync(SequencePackage.Read(path), path);
        }

        private async Task ApplySequenceAsync(SequenceFile sequence, string? path)
        {
            if (gridApplying || sequenceSaving) throw new InvalidOperationException("请等待当前恢复或保存完成。");
            var text = sequence.Movie;
            var candidate = movieEditor.ValidateAny(text, path ?? "<save-editor>");
            if (!candidate.Success) throw new InvalidDataException("序列文件无效，当前草稿未改变。");
            if (sequence.InitialSaves != null && candidate.V2Document == null)
                throw new InvalidDataException("绑定存档的序列包需要 v2 全流程序列。");
            if (fullRunMovies?.IsPending == true && candidate.V2Document == null)
                throw new InvalidDataException("当前会话需要 v2 全流程序列，当前草稿未改变。");
            SetGridApplying(true);
            try
            {
                if (fullRunMovies?.IsPending == true)
                    await fullRunMovies.PauseForDocumentChangeAsync(CancellationToken.None);
                await SaveCurrentBranchAsync(closing: true);
                var replayProfileBefore = MovieProtocolV2.SelectedNativeProfileId;
                if (candidate.V2Document != null)
                    NativeExecutionProfile.ConfigureReplayEnvironment(candidate.V2Document.Header.NativeProfileId,
                        Environment.SetEnvironmentVariable);
                var replayProfileChanged = replayProfileBefore != MovieProtocolV2.SelectedNativeProfileId;
                SetSequenceInitialSaves(sequence.InitialSaves);
                ResetSequenceSaveTarget(path);
                MovieText = gridSource = text;
                gridHasUserEdits = false; earliestGridEdit = long.MaxValue;
                recordingGridNativeFrame = -1;
                draftRequiresRestart = candidate.V2Document != null;
                RefreshInputGrid();
                if (candidate.V2Document != null) StartTimeline(MovieText);
                if (!replayProfileChanged && sequence.InitialSaves == null && fullRunMovies?.IsPending == true && fullRunMovies.Mode == "Unarmed"
                    && startupBoot?.IsWaiting == true && startupBoot.NativeCompletedFrames == 0)
                {
                    fullRunMovies.ArmReplay(candidate.V2Document!);
                    draftRequiresRestart = false;
                }
                foreach (var command in runtimeCommands) command.RaiseCanExecuteChanged();
                Status = draftRequiresRestart ? "序列已切换；播放将从起点开始，也可右键恢复到指定帧。" : "序列已打开。";
                OnPropertyChanged(nameof(PlayPauseLabel));
            }
            finally { SetGridApplying(false); }
        }

        private void ValidateMovie(bool applyFormat)
        {
            var result = movieEditor.ValidateAny(MovieText);
            if (!result.Success)
            {
                ValidationOutput = string.Join(
                    Environment.NewLine,
                    result.Diagnostics.Select(
                        diagnostic => diagnostic.ToString()));
                CanonicalDiff =
                    "Canonical comparison is unavailable until validation succeeds.";
                Status = "Movie validation failed.";
                return;
            }

            CanonicalDiff = BuildCanonicalDiff(
                MovieText,
                result.CanonicalText);
            if (applyFormat)
            {
                MovieText = result.CanonicalText;
                CanonicalDiff =
                    "No difference. The editor now contains canonical text.";
            }

            ValidationOutput =
                "VALID · movieId="
                + result.MovieId
                + " · expandedTicks="
                + result.ExpandedFrames.ToString(
                    CultureInfo.InvariantCulture);
            Status = applyFormat
                ? "Movie formatted canonically."
                : "Movie is valid.";
        }

        private async Task UploadMovieAsync()
        {
            var candidate = movieEditor.Validate(MovieText);
            if (!candidate.Success)
            {
                ValidateMovie(false);
                return;
            }

            var current = await ExecuteHumanResultAsync(
                AutomationCommandIds.GetMovie,
                AutomationScope.MovieRead,
                expectedModeRequired: false);
            RequireAutomationSuccess(current);
            var baseMovieId = current.Data.TryGetValue(
                                  "available",
                                  out var available)
                              && available == "true"
                              && current.Data.TryGetValue(
                                  "movieId",
                                  out var loadedMovieId)
                ? loadedMovieId
                : "none";
            var proposed = await ExecuteHumanResultAsync(
                AutomationCommandIds.ProposeMoviePatch,
                AutomationScope.MoviePropose,
                Fields(
                    "baseMovieId",
                    baseMovieId,
                    "candidateMovieBase64",
                    Convert.ToBase64String(
                        new UTF8Encoding(false).GetBytes(
                            candidate.CanonicalText)),
                    "expectedMilestone",
                    "ui-upload",
                    "reason",
                    "human selected Upload in Studio"),
                expectedModeRequired: false);
            RequireAutomationSuccess(proposed);
            var branchId = RequireResultField(
                proposed,
                "branchMovieId");
            await ApplyMovieBranchByIdAsync(branchId);
            BranchMovieId = branchId;
            Status = "Canonical movie proposed and applied through the shared authoring service.";
        }

        private async Task SendFrameInputAsync(bool singleFrame)
        {
            if (!string.Equals(
                    currentControlMode,
                    "Paused",
                    StringComparison.Ordinal)
                || currentMovieTick < 0
                || currentSceneEpoch < 0)
            {
                throw new InvalidOperationException(
                    "A fresh Paused Runtime status with movie tick and scene epoch is required. Click Pause and Snapshot first.");
            }

            var count = singleFrame
                ? 1
                : ParseCount(
                    FrameInputCount,
                    1,
                    MovieProtocolV1.DefaultMaxExpandedTicks,
                    "frame count");
            var inputMovie = CreateInputMovie(
                FrameInputHold,
                count,
                "studio-input-batch.hktas");
            var result = await ExecuteHumanResultAsync(
                singleFrame
                    ? AutomationCommandIds.StepWithInput
                    : AutomationCommandIds.QueueInputBatch,
                AutomationScope.ControlInput,
                Fields(
                    "candidateMovieBase64",
                    Convert.ToBase64String(
                        new UTF8Encoding(false).GetBytes(
                            inputMovie.CanonicalText)),
                    "expectedSceneEpoch",
                    currentSceneEpoch.ToString(
                        CultureInfo.InvariantCulture)));
            RequireAutomationSuccess(result);
            if (result.Data.TryGetValue(
                    "batchMovieId",
                    out var batchMovieId))
            {
                BranchMovieId = batchMovieId;
            }

            AuthoringStatus = singleFrame
                ? "One input tick scheduled. Runtime will return to Paused."
                : count.ToString(CultureInfo.InvariantCulture)
                  + " input ticks scheduled. Runtime will return to Paused.";
        }

        private async Task ApplyTimelineEditAsync(TimelineEditKind kind)
        {
            var current = movieEditor.Validate(MovieText);
            if (!TimelineIncludeLifecycle && (!current.Success || current.Document == null))
            {
                ValidateMovie(false);
                throw new InvalidDataException(
                    "Validate and upload the current movie before editing its timeline.");
            }

            var start = ParseCount(
                TimelineStartTick,
                0,
                MovieProtocolV1.DefaultMaxExpandedTicks,
                "start tick");
            var arguments = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["baseMovieId"] = current.MovieId,
                ["startTick"] = start.ToString(
                    CultureInfo.InvariantCulture)
            };
            if (TimelineIncludeLifecycle)
            {
                if (TimelineEditStoredBranch)
                {
                    if (!MovieProtocolV1.IsLowerSha256(BranchMovieId))
                        throw new InvalidDataException("先选择要继续编辑的生命周期分支 ID。");
                    arguments["baseMovieId"] = BranchMovieId;
                }
                else
                {
                    var source = await ExecuteHumanResultAsync(AutomationCommandIds.GetMovie, AutomationScope.MovieRead,
                        Fields("includeLifecycle", "true"), expectedModeRequired: false);
                    RequireAutomationSuccess(source);
                    arguments["baseMovieId"] = RequireResultField(source, "movieId");
                }
                arguments["includeLifecycle"] = "true";
            }
            var commandId = kind switch
            {
                TimelineEditKind.Replace =>
                    AutomationCommandIds.ReplaceInputRange,
                TimelineEditKind.Insert =>
                    AutomationCommandIds.InsertInputRange,
                TimelineEditKind.Delete =>
                    AutomationCommandIds.DeleteInputRange,
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            if (kind == TimelineEditKind.Delete)
            {
                arguments["count"] = ParseCount(
                        TimelineDeleteCount,
                        1,
                        MovieProtocolV1.DefaultMaxExpandedTicks,
                        "delete count")
                    .ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                if (kind == TimelineEditKind.Replace)
                {
                    arguments["deleteCount"] = ParseCount(
                            TimelineDeleteCount,
                            0,
                            MovieProtocolV1.DefaultMaxExpandedTicks,
                            "delete count")
                        .ToString(CultureInfo.InvariantCulture);
                }

                var replacement = CreateInputMovie(
                    TimelineReplacementHold,
                    ParseCount(
                        TimelineReplacementCount,
                        1,
                        MovieProtocolV1.DefaultMaxExpandedTicks,
                        "replacement count"),
                    "studio-timeline-edit.hktas");
                arguments["replacementMovieBase64"] =
                    Convert.ToBase64String(
                        new UTF8Encoding(false).GetBytes(
                            replacement.CanonicalText));
            }

            var edited = await ExecuteHumanResultAsync(
                commandId,
                AutomationScope.MovieEdit,
                arguments,
                expectedModeRequired: false);
            RequireAutomationSuccess(edited);
            if (edited.Data.TryGetValue("branchFormat", out var branchFormat)
                && (branchFormat == "hklbranch-v2" || branchFormat == "hklbranch-v3"))
            {
                var lifecycleBranchId = RequireResultField(edited, "branchMovieId");
                if (edited.Data.TryGetValue("undoBranchMovieId", out var undoBranchId))
                    lifecycleBranchHistory.Record(undoBranchId, lifecycleBranchId);
                BranchMovieId = lifecycleBranchId;
                AuthoringStatus = "生命周期分支已保存，待执行重放验证；原始记录保留。";
                return;
            }
            var branchId = RequireResultField(
                edited,
                "branchMovieId");
            var canonicalText = DecodeMovieResult(
                edited,
                "canonicalMovieBase64");
            await ApplyMovieBranchByIdAsync(branchId);
            RememberTimelineUndo(MovieText);
            MovieText = canonicalText;
            BranchMovieId = branchId;
            timelineRedo.Clear();
            ValidationOutput =
                "VALID · typed "
                + kind.ToString().ToLowerInvariant()
                + " · branch="
                + branchId;
            AuthoringStatus =
                "Timeline edit stored and applied through the shared authoring service. Use Apply Branch + Seek to re-execute changed history.";
        }

        private async Task UndoTimelineEditAsync()
        {
            if (TimelineIncludeLifecycle)
            {
                if (lifecycleBranchHistory.TryUndo(BranchMovieId, out var branch))
                {
                    BranchMovieId = branch;
                    TimelineEditStoredBranch = true;
                    AuthoringStatus = "已选回上一分支；游戏状态未改变。执行 Apply Branch + Seek 后重放。";
                }
                else AuthoringStatus = "当前分支没有可撤销的编辑。";
                return;
            }
            if (timelineUndo.Count == 0)
            {
                AuthoringStatus = "No timeline edit to undo.";
                return;
            }

            var previous = timelineUndo.Peek();
            await ProposeAndApplyMovieTextAsync(previous, "ui-undo");
            timelineRedo.Push(MovieText);
            MovieText = timelineUndo.Pop();
            AuthoringStatus =
                "Timeline edit undone through a content-addressed child branch.";
        }

        private async Task RedoTimelineEditAsync()
        {
            if (TimelineIncludeLifecycle)
            {
                if (lifecycleBranchHistory.TryRedo(BranchMovieId, out var branch))
                {
                    BranchMovieId = branch;
                    TimelineEditStoredBranch = true;
                    AuthoringStatus = "已选回下一分支；游戏状态未改变。执行 Apply Branch + Seek 后重放。";
                }
                else AuthoringStatus = "当前分支没有可重做的编辑。";
                return;
            }
            if (timelineRedo.Count == 0)
            {
                AuthoringStatus = "No timeline edit to redo.";
                return;
            }

            var next = timelineRedo.Peek();
            await ProposeAndApplyMovieTextAsync(next, "ui-redo");
            RememberTimelineUndo(MovieText);
            MovieText = timelineRedo.Pop();
            AuthoringStatus =
                "Timeline edit redone through a content-addressed child branch.";
        }

        private void RememberTimelineUndo(string value)
        {
            if (timelineUndo.Count >= 100)
            {
                var retained = timelineUndo.Reverse()
                    .Take(99)
                    .Reverse()
                    .ToArray();
                timelineUndo.Clear();
                foreach (var item in retained)
                {
                    timelineUndo.Push(item);
                }
            }

            timelineUndo.Push(value);
        }

        private async Task ProposeAndApplyMovieTextAsync(
            string candidateText,
            string reason)
        {
            var current = movieEditor.Validate(MovieText);
            var candidate = movieEditor.Validate(candidateText);
            if (!current.Success || !candidate.Success)
            {
                throw new InvalidDataException(
                    "Undo/redo requires canonical current and candidate movies.");
            }

            var proposed = await ExecuteHumanResultAsync(
                AutomationCommandIds.ProposeMoviePatch,
                AutomationScope.MoviePropose,
                Fields(
                    "baseMovieId",
                    current.MovieId,
                    "candidateMovieBase64",
                    Convert.ToBase64String(
                        new UTF8Encoding(false).GetBytes(
                            candidate.CanonicalText)),
                    "expectedMilestone",
                    "ui-history",
                    "reason",
                    reason),
                expectedModeRequired: false);
            RequireAutomationSuccess(proposed);
            await ApplyMovieBranchByIdAsync(
                RequireResultField(proposed, "branchMovieId"));
        }

        private Task ApplyMovieBranchAsync()
        {
            if (!MovieProtocolV1.IsLowerSha256(BranchMovieId))
            {
                throw new InvalidDataException(
                    "Enter a lowercase content-addressed branch movie ID.");
            }

            return ApplyMovieBranchByIdAsync(BranchMovieId);
        }

        private async Task ApplyMovieBranchByIdAsync(string branchId)
        {
            var applied = await ExecuteHumanResultAsync(
                AutomationCommandIds.ApplyMovieBranch,
                AutomationScope.MovieApplyBranch,
                Fields("branchMovieId", branchId));
            RequireAutomationSuccess(applied);
            BranchMovieId = branchId;
        }

        private async Task SeekMovieTickAsync()
        {
            RequireFreshSceneEpoch();
            var target = ParseCount(
                SeekTargetTick,
                0,
                MovieProtocolV1.DefaultMaxExpandedTicks - 1,
                "seek target tick");
            var result = await ExecuteHumanResultAsync(
                AutomationCommandIds.SeekMovieTick,
                AutomationScope.ControlReplaySave,
                Fields(
                    "targetMovieTick",
                    target.ToString(CultureInfo.InvariantCulture),
                    "expectedSceneEpoch",
                    currentSceneEpoch.ToString(
                        CultureInfo.InvariantCulture)));
            RequireAutomationSuccess(result);
            AuthoringStatus =
                "Seek accepted. Follow MovieSeekProgress until Completed or Failed.";
        }

        private async Task ApplyBranchAndSeekAsync()
        {
            RequireFreshSceneEpoch();
            if (!MovieProtocolV1.IsLowerSha256(BranchMovieId))
            {
                throw new InvalidDataException(
                    "Enter a lowercase content-addressed branch movie ID.");
            }

            var target = ParseCount(
                SeekTargetTick,
                0,
                MovieProtocolV1.DefaultMaxExpandedTicks - 1,
                "seek target tick");
            var result = await ExecuteHumanResultAsync(
                AutomationCommandIds.ApplyBranchAndSeek,
                AutomationScope.MovieApplyBranch,
                Fields(
                    "branchMovieId",
                    BranchMovieId,
                    "targetMovieTick",
                    target.ToString(CultureInfo.InvariantCulture),
                    "expectedSceneEpoch",
                    currentSceneEpoch.ToString(
                        CultureInfo.InvariantCulture)));
            RequireAutomationSuccess(result);
            AuthoringStatus =
                "Branch apply + seek accepted. Follow MovieSeekProgress until terminal.";
        }

        private async Task RunUntilAsync()
        {
            var target = ParseCount(
                RunUntilTargetTick,
                0,
                MovieProtocolV1.DefaultMaxExpandedTicks - 1,
                "run-until target tick");
            var result = await ExecuteHumanResultAsync(
                AutomationCommandIds.RunUntil,
                AutomationScope.ControlRunUntil,
                Fields(
                    "targetMovieTick",
                    target.ToString(CultureInfo.InvariantCulture)));
            RequireAutomationSuccess(result);
        }

        private async Task RefreshStructuredStateAsync()
        {
            var result = await ExecuteHumanResultAsync(
                AutomationCommandIds.GetState,
                AutomationScope.ObserveStateSummary,
                expectedModeRequired: false);
            RequireAutomationSuccess(result);
            LatestState = RequireResultField(result, "stateJson");
        }

        private async Task RefreshRuntimeMovieAsync()
        {
            var result = await ExecuteHumanResultAsync(
                AutomationCommandIds.GetMovie,
                AutomationScope.MovieRead,
                expectedModeRequired: false);
            RequireAutomationSuccess(result);
            if (!result.Data.TryGetValue("available", out var available)
                || available != "true")
            {
                throw new InvalidOperationException(
                    "Runtime has no loaded canonical movie.");
            }

            MovieText = DecodeMovieResult(result, "movieBase64");
            ValidateMovie(false);
        }

        private async Task RefreshAutoSavePolicyAsync()
        {
            var result = await ExecuteHumanResultAsync(AutomationCommandIds.GetState,
                AutomationScope.ObserveStateSummary,
                new Dictionary<string, string> { ["statusOnly"] = "true" },
                expectedModeRequired: false);
            RequireAutomationSuccess(result);
            if (!result.Data.TryGetValue("autoSaveEnabled", out var enabled)
                || !result.Data.TryGetValue("autoSaveIntervalMovieTicks", out var interval)
                || !result.Data.TryGetValue("autoSaveRetentionCount", out var retention))
                throw new InvalidOperationException("Runtime did not report an auto-save policy.");
            AutoSaveEnabled = enabled == "true";
            AutoSaveInterval = interval;
            AutoSaveRetention = retention;
        }

        private async Task RefreshCombatStateAsync()
        {
            var result = await ExecuteHumanResultAsync(
                AutomationCommandIds.GetCombatState,
                AutomationScope.ObserveStateDeep,
                expectedModeRequired: false);
            RequireAutomationSuccess(result);
            LatestState = result.Data.TryGetValue("json", out var json)
                ? json
                : new UTF8Encoding(false, true).GetString(
                    result.CanonicalDataUtf8);
        }

        private Task CancelReplayRestoreAsync()
        {
            var operationId = automationBroker.ActiveColdRestoreOperationId;
            return ExecuteHumanAsync(AutomationCommandIds.CancelReplaySaveRestore,
                AutomationScope.ControlReplaySave,
                string.IsNullOrEmpty(operationId) ? null : Fields("operationId", operationId),
                expectedModeRequired: string.IsNullOrEmpty(operationId));
        }

        private async Task RestartRecordingSessionAsync()
        {
            var started = await ExecuteHumanResultAsync(AutomationCommandIds.RestartRecordingSession,
                AutomationScope.ControlPlayback,
                Fields("slot", ParseCount(GameSlot, 1, 4, "game slot").ToString(CultureInfo.InvariantCulture)));
            RequireAutomationSuccess(started);
            var id = started.Data["operationId"];
            var deadline = DateTimeOffset.UtcNow.AddMinutes(4);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var result = await ExecuteHumanResultAsync(AutomationCommandIds.GetStatus,
                    AutomationScope.ObserveStatus, expectedModeRequired: false);
                RequireAutomationSuccess(result);
                if (!result.Data.TryGetValue("recordingRestart.operationId", out var currentId) || currentId != id)
                    throw new InvalidOperationException("重启状态与本次操作不匹配：" + id);
                var phase = result.Data["recordingRestart.phase"];
                Status = "重新开始录制：" + phase + " — " + result.Data["recordingRestart.detail"];
                if (phase == "Ready" || phase == "Failed" || phase == "Cancelled") return;
                await Task.Delay(250);
            }
            Status = "重启状态观察超时；操作可能仍在进行，请查询状态。操作 ID：" + id;
        }

        private Task ExecuteHumanAsync(
            string commandId,
            string scope,
            IReadOnlyDictionary<string, string>? arguments = null,
            bool expectedModeRequired = true)
        {
            return ExecuteHumanAndUpdateStatusAsync(
                commandId,
                scope,
                arguments,
                expectedModeRequired);
        }

        private async Task ExecuteHumanAndUpdateStatusAsync(
            string commandId,
            string scope,
            IReadOnlyDictionary<string, string>? arguments,
            bool expectedModeRequired)
        {
            var result = await ExecuteHumanResultAsync(
                commandId,
                scope,
                arguments,
                expectedModeRequired);
            RequireAutomationSuccess(result);
            Status = commandId + ": " + result.Detail;
        }

        private Task<AutomationResultEnvelope> ExecuteHumanResultAsync(
            string commandId,
            string scope,
            IReadOnlyDictionary<string, string>? arguments = null,
            bool expectedModeRequired = true)
        {
            var mode = expectedModeRequired
                ? RequireCurrentControlMode()
                : string.Empty;
            // A Running movie tick advances while the UI command is in flight,
            // so pinning it makes Pause and other live commands inherently
            // stale. Paused ticks are stable and remain exact preconditions for
            // single-frame stepping, edits, branch application, and rollback.
            var expectedMovieTick = expectedModeRequired
                                    && string.Equals(
                                        mode,
                                        "Paused",
                                        StringComparison.Ordinal)
                                    && currentMovieTick >= 0
                ? currentMovieTick
                : (long?)null;
            IReadOnlyDictionary<string, string>? automationArguments =
                arguments;
            if (arguments != null && arguments.ContainsKey("requestId"))
            {
                // Fields() also serves raw Runtime IPC, where requestId belongs
                // in the payload. Automation owns requestId in its envelope, so
                // forwarding the legacy payload field violates the strict
                // per-command argument schema.
                var copy = new SortedDictionary<string, string>(
                    StringComparer.Ordinal);
                foreach (var pair in arguments)
                {
                    copy.Add(pair.Key, pair.Value);
                }
                copy.Remove("requestId");
                automationArguments = copy;
            }
            return automationBroker.ExecuteHumanAsync(
                commandId,
                scope,
                automationArguments,
                mode,
                expectedMovieTick,
                CancellationToken.None);
        }

        private string RequireCurrentControlMode()
        {
            if (string.IsNullOrEmpty(currentControlMode))
            {
                throw new InvalidOperationException(
                    "Request a fresh Runtime Snapshot before issuing a TAS write command.");
            }

            return currentControlMode;
        }

        private void RequireFreshSceneEpoch()
        {
            if (currentSceneEpoch < 0 || currentMovieTick < 0)
            {
                throw new InvalidOperationException(
                    "A fresh Runtime movie tick and scene epoch are required.");
            }
        }

        private static void RequireAutomationSuccess(
            AutomationResultEnvelope result)
        {
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    result.ResultCode + ": " + result.Detail);
            }
        }

        private static string RequireResultField(
            AutomationResultEnvelope result,
            string name)
        {
            if (!result.Data.TryGetValue(name, out var value)
                || string.IsNullOrEmpty(value))
            {
                throw new InvalidDataException(
                    "Automation result is missing " + name + ".");
            }

            return value;
        }

        private static string DecodeMovieResult(
            AutomationResultEnvelope result,
            string name)
        {
            var base64 = RequireResultField(result, name);
            try
            {
                return new UTF8Encoding(false, true).GetString(
                    Convert.FromBase64String(base64));
            }
            catch (Exception exception)
                when (exception is FormatException
                      || exception is DecoderFallbackException)
            {
                throw new InvalidDataException(
                    "Automation returned a malformed canonical movie.",
                    exception);
            }
        }

        private MovieEditorResult CreateInputMovie(
            string hold,
            long count,
            string sourceName)
        {
            var session = SelectedSession?.Client
                          ?? throw new InvalidOperationException(
                              "Select a Runtime session.");
            var current = movieEditor.Validate(MovieText);
            var header = current.Success && current.Document != null
                ? current.Document.Header
                : new MovieHeader(
                    MovieProtocolV1.Version,
                    "1.5.78.11833",
                    "1.5.78.11833-77",
                    session.EnvironmentManifestSha256,
                    "none",
                    "none",
                    MovieProtocolV1.TickUnit);
            if (!string.Equals(
                    header.ManifestSha256,
                    session.EnvironmentManifestSha256,
                    StringComparison.Ordinal))
            {
                header = new MovieHeader(
                    header.ProtocolVersion,
                    header.GameVersion,
                    header.ApiVersion,
                    session.EnvironmentManifestSha256,
                    header.BaselineId,
                    header.BaselineSha256,
                    header.TickUnit);
            }

            var document = new MovieDocument(
                sourceName,
                header,
                new[] { CreateFrameRun(hold, count, sourceName) });
            var canonical = new MovieCanonicalWriter().WriteToString(
                document);
            var validated = movieEditor.Validate(canonical, sourceName);
            if (!validated.Success)
            {
                throw new InvalidDataException(
                    string.Join(
                        Environment.NewLine,
                        validated.Diagnostics.Select(
                            value => value.ToString())));
            }

            return validated;
        }

        private static FrameRunCommand CreateFrameRun(
            string hold,
            long count,
            string sourceName)
        {
            var actions = TasAction.None;
            var value = (hold ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Length != 0 && value != "-")
            {
                foreach (var name in value.Split(','))
                {
                    var trimmed = name.Trim();
                    if (!MovieProtocolV1.TryParseAction(
                            trimmed,
                            out var action))
                    {
                        throw new InvalidDataException(
                            "Unknown TAS action: " + trimmed + ".");
                    }

                    actions |= action;
                }
            }

            return new FrameRunCommand(
                count,
                actions,
                0,
                0,
                false,
                new MovieSourceSpan(sourceName, 1, 1, 1));
        }

        private static long ParseCount(
            string text,
            long minimum,
            long maximum,
            string label)
        {
            if (!long.TryParse(
                    text,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var result)
                || result < minimum
                || result > maximum)
            {
                throw new InvalidDataException(
                    label
                    + " must be in ["
                    + minimum.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + maximum.ToString(CultureInfo.InvariantCulture)
                    + "].");
            }

            return result;
        }

        private Task SendSimpleAsync(string messageType)
        {
            return SendAsync(
                messageType,
                Fields(
                    "requestId",
                    "request-"
                    + Guid.NewGuid().ToString("N")));
        }

        private async Task SendAsync(
            string messageType,
            IReadOnlyDictionary<string, string> fields)
        {
            var session = SelectedSession?.Client
                          ?? throw new InvalidOperationException(
                              "Select a Runtime session.");
            IReadOnlyDictionary<string, string> outbound = fields;
            if (!fields.ContainsKey("requestId"))
            {
                var copy = new Dictionary<string, string>(
                    fields,
                    StringComparer.Ordinal)
                {
                    ["requestId"] =
                        "studio-" + Guid.NewGuid().ToString("N")
                };
                outbound = copy;
            }

            await session.SendCommandAsync(
                messageType,
                outbound,
                CancellationToken.None);
            Status = messageType + " sent.";
        }

        private Task RestoreSelectedAsync()
        {
            if (string.IsNullOrWhiteSpace(SelectedReplaySave))
            {
                throw new InvalidOperationException(
                    "Select a replay save.");
            }

            var separator = SelectedReplaySave.IndexOf(
                " · ",
                StringComparison.Ordinal);
            var replaySaveId = separator < 0
                ? SelectedReplaySave
                : SelectedReplaySave.Substring(0, separator);
            return ExecuteHumanAsync(
                AutomationCommandIds.RestoreReplaySave,
                AutomationScope.ControlReplaySave,
                Fields("replaySaveId", replaySaveId));
        }

        private void OnSessionsChanged(
            object? sender,
            EventArgs eventArgs)
        {
            Dispatch(RefreshSessions);
        }

        private void RefreshSessions()
        {
            var selectedId = SelectedSession?.Client.SessionId;
            Sessions.Clear();
            foreach (var client in registry.Sessions)
            {
                Sessions.Add(new SessionViewItem(client));
            }

            SelectedSession = Sessions.FirstOrDefault(
                                  item => item.Client.IsConnected && string.Equals(
                                      item.Client.SessionId,
                                      selectedId,
                                      StringComparison.Ordinal))
                              ?? Sessions.Where(item => item.Client.IsConnected)
                                  .OrderByDescending(item => item.Client.GameProcessStartTimeUtcTicks).FirstOrDefault()
                              ?? Sessions.FirstOrDefault();
            UpdateConnectionStatus();
            foreach (var item in Sessions)
            {
                if (fullRunMovies?.IsPending != true && item.Client.IsConnected
                    && item.Client.NativeCapabilitiesRequested)
                {
                    _ = CaptureNativeObserveAsync(item.Client);
                }
            }

            var selected = SelectedSession?.Client;
            if (selected?.IsConnected == true
                && warmedSessions.Add(selected.SessionId))
            {
                UpdateConnectionStatus();
                _ = WarmSessionAsync(selected);
            }
        }

        private async Task WarmSessionAsync(
            RuntimeSessionClient session)
        {
            // A menu-requested restart endpoint is not a gameplay Runtime.
            if (StartupActivationPolicy.IsManualRequest(session.SessionId)) return;
            try
            {
                if (fullRunMovies?.IsPending == true)
                {
                    await session.SendCommandAsync(IpcMessageTypes.FullRunStatus,
                        Fields("requestId", "studio-full-run-status-" + Guid.NewGuid().ToString("N")),
                        CancellationToken.None);
                    Dispatch(() =>
                    {
                        readySessions.Add(session.SessionId);
                        UpdateConnectionStatus();
                        Status = "全流程 Runtime 已连接。";
                    });
                    return;
                }
                var fieldsSent = 0;
                foreach (var messageType in new[]
                         {
                             IpcMessageTypes.Ping,
                             IpcMessageTypes.Subscribe,
                             IpcMessageTypes.Subscribe,
                             IpcMessageTypes.RequestSnapshot,
                             IpcMessageTypes.ListReplaySaves,
                             IpcMessageTypes
                                 .RequestCapabilityCatalog
                         })
                {
                    IReadOnlyDictionary<string, string> fields;
                    if (string.Equals(
                            messageType,
                            IpcMessageTypes.Subscribe,
                            StringComparison.Ordinal))
                    {
                        var stream = fieldsSent == 0
                            ? "watch"
                            : "ledger";
                        fields = Fields("stream", stream);
                        fieldsSent++;
                    }
                    else
                    {
                        fields = Fields();
                    }

                    await session.SendCommandAsync(
                        messageType,
                        fields,
                        CancellationToken.None);
                }

                await Task.Delay(
                    TimeSpan.FromSeconds(8));
                if (session.IsConnected)
                {
                    await session.SendCommandAsync(
                        IpcMessageTypes.RequestSnapshot,
                        Fields(),
                        CancellationToken.None);
                    await session.SendCommandAsync(
                        IpcMessageTypes.RequestCapabilityCatalog,
                        Fields(),
                        CancellationToken.None);
                }

                Dispatch(
                    () =>
                    {
                        readySessions.Add(session.SessionId);
                        UpdateConnectionStatus();
                        Status =
                            "Runtime state and capability catalog initialized.";
                    });
            }
            catch (Exception exception)
            {
                Dispatch(
                    () =>
                    {
                        warmedSessions.Remove(session.SessionId);
                        readySessions.Remove(session.SessionId);
                        UpdateConnectionStatus();
                        Status =
                            "Runtime initialization failed: "
                            + exception.Message;
                    });
            }
        }

        private readonly LatestUiUpdate runtimeProgressUpdate = new();

        private void OnEnvelopeReceived(
            object? sender,
            SessionEnvelopeEventArgs eventArgs)
        {
            // Checkpoint clients consume these directly. Never put restorable bytes
            // (including a truncated base64 preview) in Studio's timeline/diagnostic history.
            if (eventArgs.Envelope.MessageType == IpcMessageTypes.SceneCheckpointData) return;
            var payload =
                IpcPayloadCodec.TryDeserialize(
                    eventArgs.Envelope.PayloadUtf8);
            if (eventArgs.Envelope.MessageType == IpcMessageTypes.FullRunState
                && payload.Success && payload.Fields != null
                && payload.Fields.TryGetValue("requestId", out var progressRequest)
                && progressRequest == "runtime-progress")
            {
                runtimeProgressUpdate.Post(() =>
                {
                    if (ReferenceEquals(SelectedSession?.Client, eventArgs.Session))
                        HandleTypedEvent(eventArgs.Envelope.MessageType, payload.Fields);
                }, Dispatch);
                return;
            }
            if (string.Equals(
                    eventArgs.Envelope.MessageType,
                    IpcMessageTypes.CapabilityCatalog,
                    StringComparison.Ordinal)
                && payload.Success
                && payload.Fields != null
                && payload.Fields.TryGetValue(
                    "catalog",
                    out var capabilityCatalog)
                && capabilityCatalog.IndexOf(
                    "native.process.observe.v1=requested",
                    StringComparison.Ordinal) >= 0)
            {
                _ = CaptureNativeObserveAsync(
                    eventArgs.Session);
            }

            Dispatch(
                () =>
                {
                    var isGridProgressPoll = payload.Success && payload.Fields != null
                        && payload.Fields.TryGetValue("requestId", out var requestId)
                        && requestId.StartsWith("studio-grid-follow-", StringComparison.Ordinal);
                    var summary = payload.Success
                                  && payload.Fields != null
                        ? string.Join(
                            " ",
                            payload.Fields.Select(
                                pair => pair.Key
                                        + "="
                                        + Truncate(
                                            pair.Value,
                                            180)))
                        : "invalid-payload";
                    if (!isGridProgressPoll)
                        AddTimeline(
                            eventArgs.Envelope.Sequence.ToString(
                                CultureInfo.InvariantCulture)
                            + " "
                            + eventArgs.Envelope.MessageType
                            + " "
                            + summary);
                    if (ReferenceEquals(SelectedSession?.Client, eventArgs.Session))
                        HandleTypedEvent(
                            eventArgs.Envelope.MessageType,
                            payload.Fields);
                });
        }

        private void HandleTypedEvent(
            string messageType,
            IReadOnlyDictionary<string, string>? fields)
        {
            if (fields == null)
            {
                return;
            }

            if (fields.TryGetValue("requestId", out var requestId)
                && requestId == gridProgressRequestId
                && (messageType == IpcMessageTypes.FullRunState
                    || messageType == IpcMessageTypes.RuntimeStatus
                    || messageType == IpcMessageTypes.CommandRejected
                    || messageType == IpcMessageTypes.Fault))
                gridProgressRequestId = null;

            if (fields.Keys.Any(key => key.StartsWith("videoExport.", StringComparison.Ordinal)))
            {
                var statusOperationId = fields.TryGetValue("videoExport.operationId", out var operationId)
                    ? operationId
                    : string.Empty;
                var previousOperationId = videoExportOperationId;
                if (!string.IsNullOrEmpty(statusOperationId))
                {
                    videoExportOperationId = statusOperationId;
                }
                var phase = fields.TryGetValue("videoExport.state", out var state)
                    ? state
                    : "未知";
                var detail = fields.TryGetValue("videoExport.detail", out var exportDetail)
                    ? exportDetail
                    : string.Empty;
                var frames = fields.TryGetValue("videoExport.frames", out var frameCount)
                    ? frameCount
                    : "未记录";
                var output = fields.TryGetValue("videoExport.outputPath", out var exportPath)
                    ? exportPath
                    : "未记录";
                VideoExportStatus = "阶段=" + phase + " · 帧数=" + frames + " · 输出=" + output
                                    + (string.IsNullOrEmpty(detail) ? string.Empty : " · " + detail);
                if (string.Equals(phase, "Completed", StringComparison.Ordinal)
                    || string.Equals(phase, "Cancelled", StringComparison.Ordinal)
                    || string.Equals(phase, "Failed", StringComparison.Ordinal))
                {
                    if (string.Equals(videoExportOperationId, statusOperationId, StringComparison.Ordinal)
                        && (string.IsNullOrEmpty(previousOperationId)
                            || string.Equals(previousOperationId, statusOperationId, StringComparison.Ordinal)))
                    {
                        videoExportOperationId = string.Empty;
                    }
                }
            }

            if (fields.TryGetValue("movieTick", out var movieTickText)
                && long.TryParse(
                    movieTickText,
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var movieTick))
            {
                currentMovieTick = movieTick;
                OnPropertyChanged(nameof(FrameCounterText));
                if (startupBoot?.IsPending != true) TrackGridFrame(currentMovieTick);
            }

            if (fields.TryGetValue("sceneEpoch", out var epochText)
                && int.TryParse(
                    epochText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var epoch))
            {
                currentSceneEpoch = epoch;
            }

            if (fields.TryGetValue(
                    "controlMode",
                    out var controlMode))
            {
                currentControlMode = controlMode;
                OnPropertyChanged(nameof(PlaybackStateText));
                OnPropertyChanged(nameof(PlayPauseLabel));
            }

            if (string.Equals(messageType, IpcMessageTypes.FullRunState,
                    StringComparison.Ordinal))
            {
                UpdateInputBindingLabels(fields);
                if (fields.TryGetValue("movieFrame", out var frameText)
                    && long.TryParse(frameText, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var frame))
                {
                    currentFullRunMovieFrame = frame;
                    UpdateRestoreProgress(frame);
                    OnPropertyChanged(nameof(FrameCounterText));
                    TrackGridFrame(frame);
                }
                LatestState = string.Join(Environment.NewLine,
                    fields.Select(pair => pair.Key + " = " + pair.Value));
                RuntimeSummary = "全流程 · "
                    + (fields.TryGetValue("mode", out var fullRunMode) ? fullRunMode : "unknown")
                    + " · Movie 帧 "
                    + (fields.TryGetValue("movieFrame", out var movieFrame) ? movieFrame : "?")
                    + " · 原生帧 "
                    + (fields.TryGetValue("nativeFrame", out var fullRunFrame) ? fullRunFrame : "?")
                    + " · "
                    + (fields.TryGetValue("frameBoundary", out var frameBoundary)
                        ? frameBoundary : "等待 Runtime");
            }

            if (string.Equals(
                    messageType,
                    IpcMessageTypes.WatchFrame,
                    StringComparison.Ordinal)
                || string.Equals(
                    messageType,
                    IpcMessageTypes.RuntimeStatus,
                    StringComparison.Ordinal))
            {
                LatestState = fields.TryGetValue(
                    "json",
                    out var json)
                    ? json
                    : string.Join(
                        Environment.NewLine,
                        fields.Select(
                            pair => pair.Key + " = " + pair.Value));
            }

            if (string.Equals(
                    messageType,
                    IpcMessageTypes.RuntimeModeChanged,
                    StringComparison.Ordinal))
            {
                RuntimeSummary = string.Join(
                    " · ",
                    fields.Select(
                        pair => pair.Key + "=" + pair.Value));
            }

            if (string.Equals(
                    messageType,
                    IpcMessageTypes.ReplaySaveCatalog,
                    StringComparison.Ordinal)
                && fields.TryGetValue("entries", out var entries))
            {
                ReplaySaves.Clear();
                if (fields.TryGetValue("entriesJson", out var quickCatalog)) UpdateQuickSlotCatalog(quickCatalog);
                foreach (var line in entries.Split(
                             new[] { '\n' },
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    ReplaySaves.Add(line);
                }

                RestoreStatus =
                    ReplaySaves.Count.ToString(
                        CultureInfo.InvariantCulture)
                    + " replay saves.";
            }

            if (string.Equals(
                    messageType,
                    IpcMessageTypes.ReplaySaveRestoreProgress,
                    StringComparison.Ordinal)
                || string.Equals(
                    messageType,
                    IpcMessageTypes.RestoreAccelerationStatus,
                    StringComparison.Ordinal))
            {
                RestoreStatus = string.Join(
                    Environment.NewLine,
                    fields.Select(
                        pair => pair.Key + " = " + pair.Value));
            }

            if (string.Equals(
                    messageType,
                    IpcMessageTypes.CapabilityCatalog,
                    StringComparison.Ordinal))
            {
                capabilityBroker.ApplyRuntimeCatalog(
                    fields.TryGetValue("catalog", out var catalog)
                        ? catalog
                        : string.Empty);
                RefreshCapabilityView();
            }

            if (string.Equals(
                    messageType,
                    IpcMessageTypes.CommandRejected,
                    StringComparison.Ordinal)
                || string.Equals(
                    messageType,
                    IpcMessageTypes.Fault,
                    StringComparison.Ordinal))
            {
                Status = fields.TryGetValue("detail", out var detail)
                    ? detail
                    : messageType;
                if (messageType == IpcMessageTypes.Fault
                    && !string.IsNullOrEmpty(automationBroker.ActiveColdRestoreOperationId)
                    && Status.StartsWith("EndOfStreamException:", StringComparison.Ordinal))
                    Status = "恢复正在切换游戏进程；请查看存档恢复进度。";
            }

            if (string.Equals(
                    messageType,
                    IpcMessageTypes.Desync,
                    StringComparison.Ordinal))
            {
                FirstDifference = string.Join(
                    Environment.NewLine,
                    fields.Select(
                        pair => pair.Key + " = " + pair.Value));
            }
        }

        private void AddTimeline(string value)
        {
            while (Timeline.Count >= MaximumTimelineItems)
            {
                Timeline.RemoveAt(0);
            }

            Timeline.Add(value);
        }

        private void RefreshCapabilityView()
        {
            Capabilities.Clear();
            foreach (var value in capabilityBroker.Snapshot())
            {
                Capabilities.Add(value);
            }
        }

        private async Task CaptureNativeObserveAsync(
            RuntimeSessionClient? requestedSession)
        {
            var session = requestedSession
                          ?? SelectedSession?.Client
                          ?? throw new InvalidOperationException(
                              "Select a Runtime session.");
            lock (nativeObserveSync)
            {
                if (!nativeObservedSessions.Add(
                        session.SessionId))
                {
                    return;
                }
            }
            try
            {
                await session.SendCommandAsync(
                    IpcMessageTypes.ReportNativeEvidence,
                    Fields(
                        "capabilityId",
                        "native.process.observe.v1",
                        "fallback",
                        "runtime-t09",
                        "status",
                        "started"),
                    CancellationToken.None);
                Dispatch(
                    () => Status =
                        "Capturing signed NativeHost evidence.");
                var result =
                    await nativeHostLauncher.CaptureObserveAsync(
                        session,
                        CancellationToken.None);
                capabilityBroker.ApplyNativeEvidence(
                    result.Fields);
                await session.SendCommandAsync(
                    IpcMessageTypes.ReportNativeEvidence,
                    Fields(
                        "assemblyCSharpSha256",
                        result.Fields["assemblyCSharpSha256"],
                        "attachCyclesCompleted",
                        result.Fields["attachCyclesCompleted"],
                        "buildWhitelistId",
                        result.Fields["buildWhitelistId"],
                        "capabilityId",
                        "native.process.observe.v1",
                        "capabilityVersion",
                        "1",
                        "checkpointStatus",
                        "unsupported",
                        "coreAssemblySha256",
                        result.Fields["coreAssemblySha256"],
                        "environmentManifestSha256",
                        result.Fields[
                            "environmentManifestSha256"],
                        "evidenceVersion",
                        "1",
                        "fallback",
                        "none",
                        "imageSha256",
                        result.Fields["imageSha256"],
                        "moduleMapSha256",
                        result.Fields["moduleMapSha256"],
                        "parentProcessVerified",
                        result.Fields["parentProcessVerified"],
                        "pssCaptureApiAvailable",
                        result.Fields["pssCaptureApiAvailable"],
                        "rawPagesPersisted",
                        "false",
                        "runtimeAssemblySha256",
                        result.Fields[
                            "runtimeAssemblySha256"],
                        "status",
                        "verified",
                        "targetFingerprint",
                        result.Fields["targetFingerprint"],
                        "threadSetSha256",
                        result.Fields["threadSetSha256"]),
                    CancellationToken.None);
                Dispatch(
                    () =>
                    {
                        NativeEvidence =
                            result.ToDisplayText();
                        RefreshCapabilityView();
                        Status =
                            "Native process observation verified; raw pages were not persisted.";
                    });
            }
            catch (Exception exception)
            {
                lock (nativeObserveSync)
                {
                    nativeObservedSessions.Remove(
                        session.SessionId);
                }
                try
                {
                    await session.SendCommandAsync(
                        IpcMessageTypes.ReportNativeEvidence,
                        Fields(
                            "errorCode",
                            exception.GetType().Name,
                            "fallback",
                            "runtime-t09",
                            "status",
                            "faulted"),
                        CancellationToken.None);
                }
                catch
                {
                    // Runtime disconnect is already represented by the
                    // local session state; do not mask the native failure.
                }
                Dispatch(
                    () =>
                    {
                        NativeEvidence =
                            "Native observation failed: "
                            + exception.Message;
                        Status = NativeEvidence;
                    });
            }
        }

        private void UpdateConnectionStatus()
        {
            OnPropertyChanged(nameof(CanLaunchStandaloneGame));
            var client = SelectedSession?.Client;
            foreach (var command in runtimeCommands) command.RaiseCanExecuteChanged();
            ConnectionBadge = client?.IsConnected == true
                ? "CONNECTED · IPC v"
                  + client.NegotiatedProtocol.ToString(
                      CultureInfo.InvariantCulture)
                : "DISCONNECTED";
            RuntimeReadinessBadge = client?.IsConnected != true
                ? "NOT READY"
                : readySessions.Contains(client.SessionId)
                    ? "READY"
                    : warmedSessions.Contains(client.SessionId)
                        ? "WARMING"
                        : "NOT READY";
            if (client != null && !client.IsConnected)
            {
                Status = string.IsNullOrEmpty(client.LastError)
                    ? "Runtime session is disconnected."
                    : client.LastError;
            }
        }

        private static SortedDictionary<string, string> Fields(
            params string[] values)
        {
            if (values.Length % 2 != 0)
            {
                throw new ArgumentException(
                    "Fields require name/value pairs.",
                    nameof(values));
            }

            var result = new SortedDictionary<string, string>(
                StringComparer.Ordinal);
            for (var index = 0; index < values.Length; index += 2)
            {
                result.Add(values[index], values[index + 1]);
            }

            if (!result.ContainsKey("requestId"))
            {
                result.Add(
                    "requestId",
                    "request-" + Guid.NewGuid().ToString("N"));
            }

            return result;
        }

        private static string Truncate(
            string value,
            int maximum)
        {
            return value.Length <= maximum
                ? value
                : value.Substring(0, maximum) + "…";
        }

        private static string BuildCanonicalDiff(
            string source,
            string canonical)
        {
            if (string.Equals(
                    source,
                    canonical,
                    StringComparison.Ordinal))
            {
                return "No difference. The source is canonical.";
            }

            var sourceLines = NormalizeLines(source);
            var canonicalLines = NormalizeLines(canonical);
            var common = Math.Min(
                sourceLines.Length,
                canonicalLines.Length);
            var first = 0;
            while (first < common
                   && string.Equals(
                       sourceLines[first],
                       canonicalLines[first],
                       StringComparison.Ordinal))
            {
                first++;
            }

            var lineNumber = first + 1;
            var sourceLine = first < sourceLines.Length
                ? sourceLines[first]
                : "<end-of-file>";
            var canonicalLine = first < canonicalLines.Length
                ? canonicalLines[first]
                : "<end-of-file>";
            return "First canonical difference at line "
                   + lineNumber.ToString(
                       CultureInfo.InvariantCulture)
                   + Environment.NewLine
                   + "- source: "
                   + sourceLine
                   + Environment.NewLine
                   + "+ canonical: "
                   + canonicalLine
                   + Environment.NewLine
                   + Environment.NewLine
                   + "Canonical preview:"
                   + Environment.NewLine
                   + canonical;
        }

        private static string[] NormalizeLines(string value)
        {
            return value.Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split(new[] { '\n' });
        }

        private static void Dispatch(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.BeginInvoke(action);
            }
        }

        private void Set<T>(
            ref T field,
            T value,
            [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            OnPropertyChanged(name);
        }

        private void OnPropertyChanged(
            [CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(name));
        }
    }
}
