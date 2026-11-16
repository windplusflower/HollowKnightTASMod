using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Core.Input;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.Companion.ViewModels
{
    public sealed partial class MainViewModel
    {
        private readonly Stack<string> gridUndo = new Stack<string>();
        private readonly Stack<string> gridRedo = new Stack<string>();
        private string gridSource = string.Empty;
        private string gridStart = "0", gridCount = "1", gridStatus = "打开序列或播放以开始编辑。";
        private string gridInsertCount = "1";
        private long gridPageStart;
        private long gridTotalFrames;
        private long lastFollowedGridFrame = -1;
        private long currentFullRunMovieFrame = -1;
        private bool autoFollowGrid = true;
        private string? gridProgressRequestId;
        private readonly RuntimePollRequestGate gridProgressPollGate = new();
        private DateTime gridProgressRequestedUtc;
        private TasAction gridAction = TasAction.Attack;
        private bool gridApplying;
        private bool draftRequiresRestart;
        private bool isRestorePresentationFrozen;
        private long restoreTargetFrame;
        private bool restoreReplayStarted;
        private double restoreProgress;
        public double RestoreProgress => restoreProgress;
        private void SetRestoreProgress(double value)
        {
            if (restoreProgress == value) return;
            restoreProgress = value;
            OnPropertyChanged(nameof(RestoreProgress));
        }

        private void UpdateRestoreProgress(long frame)
        {
            if (IsRestorePresentationFrozen && restoreReplayStarted && restoreTargetFrame > 0)
                SetRestoreProgress(Math.Clamp((double)frame / restoreTargetFrame, 0, 1));
        }
        private string frozenFrameCounter = string.Empty;
        public bool IsRestorePresentationFrozen => isRestorePresentationFrozen;
        public bool IsInputGridInteractive => !isRestorePresentationFrozen && !gridApplying;
        private void SetRestorePresentationFrozen(bool value)
        {
            if (isRestorePresentationFrozen == value) return;
            if (value)
            {
                frozenFrameCounter = FrameCounterText;
                restoreReplayStarted = false;
                SetRestoreProgress(0);
            }
            isRestorePresentationFrozen = value;
            OnPropertyChanged(nameof(IsRestorePresentationFrozen));
            OnPropertyChanged(nameof(RuntimeSummary));
            OnPropertyChanged(nameof(IsInputGridInteractive));
            OnPropertyChanged(nameof(FrameCounterText));
            if (!value)
            {
                var completionStatus = GridStatus;
                RefreshInputGrid();
                TrackGridFrame(CurrentGridFrame, true);
                GridStatus = completionStatus;
            }
        }
        private void SetGridApplying(bool value)
        {
            gridApplying = value;
            OnPropertyChanged(nameof(IsInputGridInteractive));
            // A native pause can arrive while the operation still owns the UI.
            // Publish again after releasing it, even when the gate has not changed.
            startupBoot?.Refresh();
            OnPropertyChanged(nameof(FrameCounterText));
            OnPropertyChanged(nameof(PlaybackStateText));
            OnPropertyChanged(nameof(PlayPauseLabel));
            foreach (var command in runtimeCommands) command.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(SaveTimelineNodeCommand));
            OnPropertyChanged(nameof(RestoreTimelineNodeCommand));
            OnPropertyChanged(nameof(DeleteTimelineNodeCommand));
        }
        public event EventHandler? InputGridRefreshed;
        public event Action<long>? InputGridPositionChanged;
        internal bool SuppressBulkGridSelection { get; private set; }
        public VirtualInputRows InputRows { get; private set; } = new VirtualInputRows();
        public IReadOnlyList<TasAction> GridActions { get; } = Enum.GetValues<TasAction>()
            .Where(a => a != TasAction.None && a != TasAction.AllGameplay).ToArray();
        public string GridStart { get => gridStart; set => Set(ref gridStart, value); }
        public string GridCount { get => gridCount; set => Set(ref gridCount, value); }
        public string GridInsertCount { get => gridInsertCount; set => Set(ref gridInsertCount, value); }
        public string GridStatus { get => gridStatus; private set => Set(ref gridStatus, value); }
        public TasAction GridAction { get => gridAction; set => Set(ref gridAction, value); }
        public bool AutoFollowGrid
        {
            get => autoFollowGrid;
            set
            {
                if (autoFollowGrid == value) return;
                Set(ref autoFollowGrid, value);
                if (value) TrackGridFrame(CurrentGridFrame, true);
            }
        }
        public ICommand RefreshGridCommand { get; private set; } = null!;
        public ICommand PreviousGridPageCommand { get; private set; } = null!;
        public ICommand NextGridPageCommand { get; private set; } = null!;
        public ICommand GoToGridFrameCommand { get; private set; } = null!;
        public ICommand FollowGridFrameCommand { get; private set; } = null!;
        public ICommand ToggleGridCommand { get; private set; } = null!;
        public ICommand InsertGridCommand { get; private set; } = null!;
        public ICommand DeleteGridCommand { get; private set; } = null!;
        public ICommand CopyGridCommand { get; private set; } = null!;
        public ICommand PasteGridCommand { get; private set; } = null!;
        public ICommand UndoGridCommand { get; private set; } = null!;
        public ICommand RedoGridCommand { get; private set; } = null!;
        public ICommand ApplyGridCommand { get; private set; } = null!;
        public ICommand ApplyGridAndSeekCommand { get; private set; } = null!;

        private void InitializeInputGrid()
        {
            LoadGridPreferences();
            ICommand Local(Action action) => new RelayCommand(() =>
            {
                try
                {
                    if (gridApplying) throw new InvalidOperationException("正在提交表格分支，请等待完成。");
                    action();
                }
                catch (Exception ex) { GridStatus = ex.Message; }
            });
            RefreshGridCommand = Local(() => RefreshInputGrid());
            PreviousGridPageCommand = Local(() =>
            {
                gridPageStart = Math.Max(0, gridPageStart - InputGridEditor.PageSize);
                ScrollGridToFrame(gridPageStart);
            });
            NextGridPageCommand = Local(() =>
            {
                var source = GridAny();
                var total = source.V2Document != null ? InputGridEditor.Count(source.V2Document)
                    : InputGridEditor.Count(source.V1Document!);
                if (gridPageStart + InputGridEditor.PageSize < total)
                    gridPageStart += InputGridEditor.PageSize;
                ScrollGridToFrame(gridPageStart);
            });
            GoToGridFrameCommand = Local(() => ScrollGridToFrame(GridIndex()));
            FollowGridFrameCommand = Local(() =>
            {
                gridPageStart = Math.Max(0, CurrentGridFrame);
                RefreshInputGrid();
                ScrollGridToFrame(CurrentGridFrame);
            });
            ToggleGridCommand = Local(() =>
            {
                if (GridAny().V2Document != null)
                {
                    var index = GridIndex();
                    PaintGrid(index, index + GridLength() - 1, GridAction.ToString(), !InputRows[(int)index].HasAction(GridAction.ToString()));
                    return;
                }
                EditGrid(InputGridEditor.Toggle(GridMovie(), GridIndex(), GridLength(), GridAction));
            });
            InsertGridCommand = Local(() =>
            {
                var source = GridAny();
                var count = ParseCount(GridInsertCount, 1, MovieProtocolV2.MaximumExpandedFrames, "插入帧数");
                if (source.V2Document != null)
                    EditGrid(new MovieV2TimelineEditor().InsertFrames(source.V2Document,
                        GridIndex(), new[] { new NativeFrameRun(count,
                            Array.Empty<GameInputSample>(),
                            new MovieSourceSpan("<input-grid>", 1, 1, 1), ParseFrameRate(DefaultFrameRate), true) }));
                else EditGrid(MovieTimelineEditor.Insert(source.V1Document!, GridIndex(),
                    new[] { new FrameRunCommand(count, TasAction.None, 0, 0, false,
                        new MovieSourceSpan("<input-grid>", 1, 1, 1)) }).Movie);
            });
            DeleteGridCommand = Local(() =>
            {
                var source = GridAny();
                if (source.V2Document != null)
                    EditGrid(new MovieV2TimelineEditor().DeleteFrames(source.V2Document,
                        GridIndex(), GridLength()));
                else EditGrid(MovieTimelineEditor.Delete(source.V1Document!,
                    GridIndex(), GridLength()).Movie);
            });
            CopyGridCommand = Local(() =>
            {
                var source = GridAny();
                Clipboard.SetText(source.V2Document != null
                    ? new MovieV2Codec().WriteCanonical(SliceV2(source.V2Document,
                        GridIndex(), GridLength()))
                    : new MovieCanonicalWriter().WriteToString(MovieInputSlice.Extract(
                        source.V1Document!, GridIndex(), GridLength())));
                GridStatus = "已复制所选输入帧。";
            });
            PasteGridCommand = Local(() =>
            {
                var target = GridAny();
                var pasted = movieEditor.ValidateAny(Clipboard.GetText());
                if (!pasted.Success || pasted.Version != target.Version)
                    throw new InvalidOperationException("剪贴板不是同版本的有效 HK-TAS 输入片段。");
                if (target.V2Document != null)
                {
                    var editor = new MovieV2TimelineEditor();
                    var shortened = editor.DeleteFrames(target.V2Document,
                        GridIndex(), GridLength());
                    if (!shortened.Success) throw new InvalidOperationException(
                        shortened.Diagnostics[0].Message);
                    EditGrid(editor.InsertFrames(shortened.Movie, GridIndex(),
                        pasted.V2Document!.Runs));
                }
                else EditGrid(InputGridEditor.Paste(target.V1Document!,
                    GridIndex(), GridLength(), pasted.V1Document!));
            });
            UndoGridCommand = Local(() => RestoreGridHistory(gridUndo, gridRedo));
            RedoGridCommand = Local(() => RestoreGridHistory(gridRedo, gridUndo));
            ApplyGridCommand = Command(() => ApplyGridAsync(false), allowStartupContinue: true, allowCompletedReplay: true);
            ApplyGridAndSeekCommand = Command(() => ApplyGridAsync(true), allowStartupContinue: true, allowCompletedReplay: true);
        }

        private MovieAnyEditorResult GridAny()
        {
            if (TimelineIncludeLifecycle)
                throw new InvalidOperationException(
                    "完整生命周期分支请使用 Advanced Authoring；表格编辑当前文本序列。");
            var result = movieEditor.ValidateAny(MovieText);
            if (!result.Success)
                throw new InvalidOperationException(
                    "序列无效，请打开有效的 .hktas 文件。");
            if (gridSource != MovieText)
            {
                gridUndo.Clear();
                gridRedo.Clear();
                gridSource = MovieText;
            }
            return result;
        }

        private MovieDocument GridMovie() => GridAny().V1Document
            ?? throw new InvalidOperationException("当前表格是 v2 原生帧。");
        private long GridIndex() => ParseCount(GridStart, 0,
            MovieProtocolV2.MaximumExpandedFrames, "frame");
        private long GridLength() => ParseCount(GridCount, 1,
            MovieProtocolV2.MaximumExpandedFrames, "selected frame count");

        private long CurrentGridFrame => startupBoot?.IsPending == true
            ? currentFullRunMovieFrame >= 0 ? currentFullRunMovieFrame : 0
            : currentMovieTick;

        public void ShowCurrentGridFrame() => TrackGridFrame(CurrentGridFrame, true);

        public async Task PollInputGridProgressAsync()
        {
            var session = SelectedSession?.Client;
            if (session?.IsConnected != true
                || (startupBoot?.IsPending != true && InputRows.Count == 0))
                return;
            if (!IsRestorePresentationFrozen)
            {
                EnsureAutoSceneCheckpointCapture(session);
                await SyncRecordingGridAsync();
                try { await SaveCurrentBranchAsync(); }
                catch (Exception exception) { GridStatus = "分支保存失败：" + exception.Message; }
            }
            var now = DateTime.UtcNow;
            var interval = startupBoot?.IsWaiting == true || currentControlMode == "Paused"
                ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(200);
            if (now - gridProgressRequestedUtc < interval) return;
            if (gridProgressPollGate.IsPending(session)) return;
            var requestId = "studio-grid-follow-" + Guid.NewGuid().ToString("N");
            gridProgressRequestId = requestId;
            gridProgressRequestedUtc = now;
            long pollEpoch = 0;
            try
            {
                var fullRun = startupBoot?.IsPending == true;
                pollEpoch = gridProgressPollGate.Begin(session, requestId, fullRun
                    ? HollowKnightTAS.Core.Ipc.IpcMessageTypes.FullRunState : HollowKnightTAS.Core.Ipc.IpcMessageTypes.RuntimeStatus);
                await session.SendCommandAsync(fullRun
                        ? HollowKnightTAS.Core.Ipc.IpcMessageTypes.FullRunStatus
                        : HollowKnightTAS.Core.Ipc.IpcMessageTypes.RequestSnapshot,
                    fullRun ? Fields("requestId", requestId)
                        : Fields("requestId", requestId, "statusOnly", "true"),
                    System.Threading.CancellationToken.None);
            }
            catch
            {
                // A disconnected session may disappear between the timer tick and the write.
                if (gridProgressRequestId == requestId) gridProgressRequestId = null;
                gridProgressPollGate.Abandon(session, requestId, pollEpoch);
            }
        }

        internal void DisposeInputGridPolling() => gridProgressPollGate.Dispose();

        private void TrackGridFrame(long frame, bool forceScroll = false)
        {
            if (IsRestorePresentationFrozen) return;
            if (frame >= gridTotalFrames && fullRunMovies?.Mode == "Recording" && !gridApplying && !recordingGridSync)
                AppendGridBlankFrames(frame + 500);
            if (frame < 0 || InputRows.Count == 0 || gridTotalFrames == 0) return;
            var visibleFrame = Math.Min(frame, gridTotalFrames - 1);
            var changed = visibleFrame != lastFollowedGridFrame;
            lastFollowedGridFrame = visibleFrame;
            InputRows.UpdateCurrent(visibleFrame);
            if (AutoFollowGrid && (forceScroll || changed))
                ScrollGridToFrame(visibleFrame);
        }

        private void ScrollGridToFrame(long frame)
        {
            if (IsRestorePresentationFrozen) return;
            if (gridTotalFrames == 0) return;
            var visibleFrame = Math.Min(Math.Max(frame, 0), gridTotalFrames - 1);
            InputGridPositionChanged?.Invoke(visibleFrame);
        }

        public bool TryEditGridAxes(string start, string count, bool enabled, int x, int y)
        {
            try
            {
                if (gridApplying) throw new InvalidOperationException("正在提交表格分支，请等待完成。");
                if (GridAny().V2Document != null)
                    throw new InvalidOperationException("当前序列不支持模拟轴弹框编辑。");
                EditGrid(InputGridEditor.SetAxes(GridMovie(),
                    ParseCount(start, 0, MovieProtocolV1.DefaultMaxExpandedTicks, "frame"),
                    ParseCount(count, 1, MovieProtocolV1.DefaultMaxExpandedTicks,
                        "selected frame count"), enabled, x, y));
                return true;
            }
            catch (Exception e) { GridStatus = e.Message; return false; }
        }

        private void RefreshInputGrid(string? message = null)
        {
            if (IsRestorePresentationFrozen) return;
            var source = GridAny();
            var total = source.V2Document != null ? InputGridEditor.Count(source.V2Document)
                : InputGridEditor.Count(source.V1Document!);
            gridTotalFrames = total;
            gridPageStart = Math.Min(gridPageStart, Math.Max(0, total - 1));
            InputRows = source.V2Document != null
                ? new VirtualInputRows(source.V2Document, CurrentGridFrame)
                : new VirtualInputRows(source.V1Document!, CurrentGridFrame);
            OnPropertyChanged(nameof(InputRows));
            GridStatus = $"共 {total} 帧。" + (message ?? "滚轮浏览完整序列；按住按键列拖动以连续编辑。未来帧下次播放／逐帧自动同步；过去帧需重放。");
            InputGridRefreshed?.Invoke(this, EventArgs.Empty);
        }

        private void EditGrid(MovieDocument candidate)
        {
            var text = new MovieCanonicalWriter().WriteToString(candidate);
            var validated = movieEditor.Validate(text);
            if (!validated.Success)
                throw new InvalidOperationException("编辑结果超出序列限制，草稿未改变。");
            PushGridHistory(gridUndo, MovieText);
            gridRedo.Clear();
            MovieText = gridSource = validated.CanonicalText;
            RefreshInputGrid("草稿已修改，游戏状态未改变。");
        }

        private void EditGrid(MovieV2EditResult result)
        {
            gridHasUserEdits = true;
            earliestGridEdit = Math.Min(earliestGridEdit, GridIndex());
            if (!result.Success)
                throw new InvalidOperationException(result.Diagnostics.Count > 0
                    ? result.Diagnostics[0].Message : "v2 编辑失败，草稿未改变。");
            PushGridHistory(gridUndo, MovieText);
            gridRedo.Clear();
            MovieText = gridSource = result.CanonicalText;
            RefreshInputGrid("v2 草稿已修改；未来帧下次播放／逐帧自动同步，过去帧需重放。");
        }

        private static MovieV2Document SliceV2(MovieV2Document movie, long start, long count)
        {
            var total = InputGridEditor.Count(movie);
            if (count < 1 || start < 0 || start > total || count > total - start)
                throw new ArgumentOutOfRangeException(nameof(count),
                    "所选原生帧超出 Movie。");
            var end = start + count;
            var position = 0L;
            var runs = new List<NativeFrameRun>();
            foreach (var run in movie.Runs)
            {
                var runEnd = position + run.RepeatCount;
                var overlapStart = Math.Max(start, position);
                var overlapEnd = Math.Min(end, runEnd);
                if (overlapEnd > overlapStart)
                    runs.Add(new NativeFrameRun(overlapEnd - overlapStart,
                        run.Samples, run.Span, run.FramesPerSecond, run.Authored, run.RngSeed));
                position = runEnd;
                if (position >= end) break;
            }
            return new MovieV2Document("<grid-copy>", movie.Header, runs);
        }

        private static void PushGridHistory(Stack<string> history, string text)
        {
            if (history.Count >= 100)
            {
                var newest = history.Take(99).Reverse().ToArray();
                history.Clear();
                foreach (var entry in newest) history.Push(entry);
            }
            history.Push(text);
        }

        private void RestoreGridHistory(Stack<string> source, Stack<string> target)
        {
            GridAny();
            if (source.Count == 0)
            {
                GridStatus = "没有可撤销/重做的表格编辑。";
                return;
            }
            PushGridHistory(target, MovieText);
            MovieText = gridSource = source.Pop();
            gridHasUserEdits = true;
            earliestGridEdit = 0;
            RefreshInputGrid("草稿历史已恢复，游戏状态未改变。");
        }

        private async Task ApplyGridAsync(bool seek)
        {
            if (gridApplying) throw new InvalidOperationException("表格分支提交正在进行。");
            if (GridAny().V2Document != null)
            {
                await FrameMenuAsync("rebuild", seek ? GridIndex() : CurrentGridFrame);
                return;
            }
            GridMovie();
            var target = GridIndex();
            if (target > InputGridEditor.Count(GridMovie()))
                throw new InvalidOperationException("目标帧超出序列。");
            var submittedText = MovieText;
            SetGridApplying(true);
            try
            {
                await UploadMovieAsync();
                if (MovieText != submittedText)
                    throw new InvalidOperationException(
                        "提交期间文本已改变；刚才提交的是旧草稿，新草稿尚未应用，未发起重放。");
                if (seek)
                {
                    SeekTargetTick = target.ToString(CultureInfo.InvariantCulture);
                    await ApplyBranchAndSeekAsync();
                }
                GridStatus = seek
                    ? "分支已提交并请求重放；以恢复状态完成为准。"
                    : "分支已应用；游戏位置未回退。修改过去输入后请重放到目标帧。";
            }
            catch (Exception ex) { GridStatus = ex.Message; throw; }
            finally { SetGridApplying(false); }
        }
    }
}
