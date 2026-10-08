using System;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Core.Input;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.Companion.ViewModels
{
    public sealed partial class MainViewModel
    {
        private string defaultFrameRate = "50", selectedFrameRate = "50";
        private bool gridHasUserEdits, recordingGridSync;
        private long earliestGridEdit = long.MaxValue;
        public long[] SelectedGridFrames { get; set; } = Array.Empty<long>();
        public bool TrySetGridRngSeed(long frame, string text)
        {
            try
            {
                int? seed = null;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (!int.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
                        throw new InvalidOperationException("RNG 种子必须是 -2147483648 至 2147483647 的整数；留空清除。");
                    seed = parsed;
                }
                var movie = GridAny().V2Document ?? throw new InvalidOperationException("RNG 种子仅支持 Movie v2。");
                var candidate = MovieV2RangeEditor.SetRngSeed(movie, frame, seed);
                if (new MovieV2Codec().WriteCanonical(candidate) == MovieText) return true;
                GridStart = frame.ToString(CultureInfo.InvariantCulture);
                GridCount = "1";
                EditGridDocument(candidate);
                return true;
            }
            catch (Exception ex) { GridStatus = ex.Message; return false; }
        }
        private long recordingGridNativeFrame = -1;
        public string DefaultFrameRate
        {
            get => defaultFrameRate;
            set
            {
                var valid = MovieFrameRate.TryParse(value, out var fps);
                if (valid) value = fps.ToString("0.######", CultureInfo.InvariantCulture);
                Set(ref defaultFrameRate, value);
                if (valid)
                {
                    try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FrameRateSettingPath)!); System.IO.File.WriteAllText(FrameRateSettingPath, value); }
                    catch (Exception ex) { GridStatus = "默认 FPS 保存失败：" + ex.Message; }
                }
            }
        }
        private static string FrameRateSettingPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HollowKnightTAS", "studio-default-fps.txt");
        private void LoadGridPreferences()
        {
            try
            {
                if (System.IO.File.Exists(FrameRateSettingPath) && new System.IO.FileInfo(FrameRateSettingPath).Length < 16)
                    defaultFrameRate = ParseFrameRate(System.IO.File.ReadAllText(FrameRateSettingPath).Trim()).ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception ex) { GridStatus = "序列偏好读取失败：" + ex.Message; }
        }
        public string SelectedFrameRate { get => selectedFrameRate; set => Set(ref selectedFrameRate, value); }
        public ICommand SetFrameRateCommand => new RelayCommand(() =>
        {
            try
            {
                var movie = GridAny().V2Document ?? throw new InvalidOperationException("逐帧 FPS 需要 v2 Movie。");
                var fps = ParseFrameRate(SelectedFrameRate);
                if (GridCount == "非连续选区")
                {
                    foreach (var frame in SelectedGridFrames) movie = MovieV2RangeEditor.SetFrameRate(movie, frame, 1, fps);
                    EditGridDocument(movie);
                }
                else EditGridDocument(MovieV2RangeEditor.SetFrameRate(movie, GridIndex(), GridLength(), fps));
            }
            catch (Exception ex) { GridStatus = ex.Message; }
        });
        private static decimal ParseFrameRate(string text)
        {
            return MovieFrameRate.Parse(text);
        }
        private void EditGridDocument(MovieV2Document movie)
        {
            if (gridApplying) throw new InvalidOperationException("正在应用序列，请稍候。");
            gridHasUserEdits = true;
            earliestGridEdit = Math.Min(earliestGridEdit, GridCount == "非连续选区"
                ? SelectedGridFrames.Min() : GridIndex());
            var text = new MovieV2Codec().WriteCanonical(movie);
            var result = movieEditor.ValidateAny(text);
            if (!result.Success) throw new InvalidOperationException(result.Diagnostics[0].Message);
            PushGridHistory(gridUndo, MovieText);
            gridRedo.Clear();
            MovieText = gridSource = text;
            RefreshInputGrid("草稿已修改；未来帧下次播放／逐帧自动同步，过去帧需重放。Ctrl+Z 撤销。");
        }
        public void PaintGrid(long start, long end, string action, bool held)
        {
            try
            {
                if (gridApplying) throw new InvalidOperationException("正在应用草稿，请稍候。");
                var first = Math.Min(start, end); var count = Math.Abs(end - start) + 1;
                GridStart = first.ToString(CultureInfo.InvariantCulture); GridCount = count.ToString(CultureInfo.InvariantCulture);
                var source = GridAny();
                if (source.V2Document != null)
                    EditGridDocument(MovieV2RangeEditor.Paint(source.V2Document, first, count, action, held));
                else if (Enum.TryParse<TasAction>(action, out var parsed))
                {
                    var runs = MovieInputSlice.Extract(source.V1Document!, first, count).Commands.OfType<FrameRunCommand>();
                    EditGrid(MovieTimelineEditor.Replace(source.V1Document!, first, count, runs.Select(r =>
                        new FrameRunCommand(r.FrameCount, held ? r.HeldActions | parsed : r.HeldActions & ~parsed,
                            r.AxisX, r.AxisY, r.HasAnalogAxes, r.Span))).Movie);
                }
            }
            catch (Exception ex) { GridStatus = ex.Message; }
        }
        private async System.Threading.Tasks.Task SyncRecordingGridAsync()
        {
            if (recordingGridSync || gridApplying || draftRequiresRestart || fullRunMovies?.Mode != "Recording"
                || startupBoot?.IsWaiting != true || startupBoot.NativeCompletedFrames == 0
                || recordingGridNativeFrame == startupBoot.NativeCompletedFrames) return;
            recordingGridSync = true;
            var previousText = MovieText;
            try
            {
                var snapshot = await ReadFrameSnapshotAsync();
                if (previousText != MovieText || gridApplying) return;
                var recorded = movieEditor.ValidateAny(snapshot.Movie).V2Document!;
                var draft = GridAny().V2Document!;
                var runs = draft.Runs.ToArray();
                if (!gridHasUserEdits)
                {
                    var total = InputGridEditor.Count(draft);
                    runs = recorded.Runs.Concat(total > snapshot.Frame
                        ? SliceV2(draft, snapshot.Frame, total - snapshot.Frame).Runs
                        : new[] { new NativeFrameRun(500, Array.Empty<GameInputSample>(), new MovieSourceSpan("<blank>", 1, 1, 1), ParseFrameRate(DefaultFrameRate), true) }).ToArray();
                }
                MovieText = gridSource = new MovieV2Codec().WriteCanonical(new MovieV2Document(draft.SourceName, CustomKeyInput.WithUsedKeys(recorded.Header, draft.Header.CustomKeys, runs), runs));
                recordingGridNativeFrame = startupBoot.NativeCompletedFrames;
                RefreshInputGrid();
                TrackGridFrame(snapshot.Frame, true);
            }
            catch (Exception ex) { GridStatus = "录制表格同步：" + ex.Message; }
            finally { recordingGridSync = false; }
        }

        private async System.Threading.Tasks.Task ApplyPendingInputsAsync(bool withinGridOperation = false)
        {
            if (fullRunMovies?.IsPending != true) return;
            if (draftRequiresRestart)
            {
                SetGridApplying(true);
                try { await RestartDraftAtAsync(0); }
                finally { if (!withinGridOperation) SetGridApplying(false); }
                return;
            }
            var completed = fullRunMovies.Mode == "Completed";
            if (!gridHasUserEdits && !completed)
            {
                if (startupBoot!.NativeCompletedFrames == 0) return;
                var boundary = await ReadReadyFrameSnapshotAsync();
                if (boundary.Frame < InputGridEditor.Count(GridAny().V2Document!)) return;
                AppendGridBlankFrames(boundary.Frame + 500, withinGridOperation);
            }
            if (gridApplying && !withinGridOperation) throw new InvalidOperationException("正在同步序列，请稍候。");
            SetGridApplying(true);
            try
            {
                if (completed)
                {
                    // The native Finished gate is terminal. Rebuild the unchanged
                    // executed prefix through the normal protected replay path.
                    var snapshot = await ReadReadyFrameSnapshotAsync();
                    var original = movieEditor.ValidateAny(snapshot.Movie).V2Document!;
                    var draft = GridAny().V2Document!;
                    if (!MovieV2Prefix.Matches(original, draft, snapshot.Frame))
                        throw new InvalidOperationException("已修改过去的帧，请先点击回放到当前帧。");
                    await RestartDraftAtAsync(snapshot.Frame);
                }
                else if (startupBoot!.NativeCompletedFrames == 0)
                {
                    await RestartDraftAtAsync(0);
                }
                else
                {
                    var snapshot = await ReadReadyFrameSnapshotAsync();
                    var original = movieEditor.ValidateAny(snapshot.Movie).V2Document!;
                    var draft = GridAny().V2Document!;
                    var total = InputGridEditor.Count(draft);
                    if (total <= snapshot.Frame) throw new InvalidOperationException("当前帧之后没有输入，请先补充空帧。");
                    if (earliestGridEdit >= snapshot.Frame)
                    {
                        var runs = MovieV2Prefix.Take(original, snapshot.Frame).Runs.Concat(SliceV2(draft, snapshot.Frame, total - snapshot.Frame).Runs).ToArray();
                        draft = new MovieV2Document(draft.SourceName, CustomKeyInput.WithUsedKeys(original.Header, draft.Header.CustomKeys, runs), runs);
                    }
                    if (!MovieV2Prefix.Matches(original, draft, snapshot.Frame))
                        throw new InvalidOperationException("已修改过去的帧，请先点击回放到当前帧；未来帧修改会自动生效。");
                    var text = new MovieV2Codec().WriteCanonical(draft);
                    var directory = System.IO.Path.Combine(fullRunMovies.ShadowRoot, "HollowKnightTAS");
                    System.IO.Directory.CreateDirectory(directory);
                    var path = System.IO.Path.Combine(directory, "live-" + Guid.NewGuid().ToString("N") + ".hktas");
                    await System.IO.File.WriteAllTextAsync(path, text, new System.Text.UTF8Encoding(false));
                    var result = await automationBroker.ExecuteHumanAsync(
                        HollowKnightTAS.Core.Automation.AutomationCommandIds.FullRunUpdateMovie,
                        HollowKnightTAS.Core.Automation.AutomationScope.ControlPlayback,
                        new System.Collections.Generic.Dictionary<string, string>
                        {
                            ["expectedNativeFrame"] = startupBoot.NativeCompletedFrames.ToString(CultureInfo.InvariantCulture),
                            ["moviePath"] = path
                        },
                        "Paused", null, System.Threading.CancellationToken.None);
                    RequireAutomationSuccess(result);
                    MovieText = gridSource = text;
                    currentFullRunMovieFrame = snapshot.Frame;
                    RefreshInputGrid();
                }
                gridHasUserEdits = false;
                earliestGridEdit = long.MaxValue;
                GridStatus = "未来帧输入已同步，将按表格执行。";
            }
            finally { if (!withinGridOperation) SetGridApplying(false); }
        }
        public void AppendGridBlankFrames(long minimumTotal = 0, bool withinGridOperation = false)
        {
            try
            {
                if (gridApplying && !withinGridOperation) return;
                var source = GridAny();
                if (source.V2Document == null) return;
                var total = InputGridEditor.Count(source.V2Document);
                var count = Math.Min(Math.Max(500, minimumTotal - total), MovieProtocolV2.MaximumExpandedFrames - total);
                if (count == 0) return;
                var wasEdited = gridHasUserEdits;
                var previousEarliest = earliestGridEdit;
                EditGrid(new MovieV2TimelineEditor().InsertFrames(source.V2Document, total,
                    new[] { new NativeFrameRun(count, Array.Empty<GameInputSample>(), new MovieSourceSpan("<blank>", 1, 1, 1), ParseFrameRate(DefaultFrameRate), true) }));
                var replay = fullRunMovies?.Mode == "Replay" || fullRunMovies?.Mode == "Completed";
                gridHasUserEdits = wasEdited || replay;
                earliestGridEdit = replay ? Math.Min(previousEarliest, total) : previousEarliest;
            }
            catch (Exception ex) { GridStatus = ex.Message; }
        }
    }
}
