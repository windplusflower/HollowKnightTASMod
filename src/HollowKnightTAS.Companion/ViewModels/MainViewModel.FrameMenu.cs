using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Core.Automation;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.Companion.ViewModels
{
    public sealed partial class MainViewModel
    {
        private static string FrameSaveRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HollowKnightTAS", "studio-frame-saves");
        public bool HasFrameSave(long frame) { InitializeWorldlines(); return FindTimelineFrame(frame) != null; }
        private sealed class FrameSave
        {
            public long Frame { get; set; }
            public string Movie { get; set; } = "";
            public Dictionary<string, string> OriginalHashes { get; set; } = new();
        }

        internal const long ForwardSeekCheckpointMinFrameSavings = 150;

        private long EffectiveNavigationFrame()
            => draftRequiresRestart ? 0
                : currentFullRunMovieFrame >= 0 ? currentFullRunMovieFrame
                : startupBoot?.NativeCompletedFrames == 0 ? 0
                : -1;

        public bool CanNavigateFrame(long frame, bool forward)
        {
            if (gridApplying || fullRunMovies?.IsPending != true || frame < 0 || frame > gridTotalFrames)
                return false;
            var current = EffectiveNavigationFrame();
            if (current < 0)
                return false;
            if (!forward)
                return frame < current;
            if (frame <= current)
                return false;
            if (draftRequiresRestart)
                return restartProtectedGame != null;
            if (fullRunMovies.Mode == "Unarmed" || fullRunMovies.Mode == "Stopped")
                return false;
            if (fullRunMovies.Mode == "Completed")
                return restartProtectedGame != null;
            return true;
        }

        private string? lastRestoredCheckpointId;
        private long lastRestoredCheckpointFrame = -1;

        public async Task FrameMenuAsync(string action, long frame)
        {
            if (gridApplying) return;
            SetGridApplying(true);
            try { await FrameMenuCoreAsync(action, frame); }
            catch (Exception ex) { GridStatus = Status = ex.Message; }
            finally { SetGridApplying(false); }
        }

        private async Task FrameMenuCoreAsync(string action, long frame)
        {
            if (fullRunMovies?.IsPending != true)
                throw new InvalidOperationException("帧存档与定位需要从 Studio 启动全流程会话。");
            if (action == "load" || action == "rebuild"
                || (action == "seek" && (draftRequiresRestart || fullRunMovies.Mode == "Completed")))
                await fullRunMovies.PauseForDocumentChangeAsync(CancellationToken.None);
            else if (startupBoot?.IsWaiting != true)
                await fullRunMovies.PauseAsync(CancellationToken.None);
            if (action == "load")
            {
                InitializeWorldlines();
                var node = FindTimelineFrame(frame) ?? throw new InvalidOperationException("所选世界线此帧尚未存档，请到时间线选择其他分支。");
                await RestoreTimelineCoreAsync(selectedTimelineTree!, selectedWorldline!, node);
            }
            else if (action == "rebuild")
            {
                if (frame < 0 || frame > gridTotalFrames)
                    throw new InvalidOperationException("目标帧超出序列。");
                await RestartDraftAtAsync(frame);
            }
            else if (action == "seek" || action == "restore")
            {
                if (frame < 0 || frame > gridTotalFrames)
                    throw new InvalidOperationException("目标帧超出序列。");
                if (action == "seek" && draftRequiresRestart)
                {
                    if (frame <= 0)
                        throw new InvalidOperationException("播放到帧只能选择当前帧之后的未来帧。");
                    await RestartDraftAtAsync(frame);
                }
                else
                {
                    var snapshot = await ReadReadyFrameSnapshotAsync();
                    if (action == "seek" ? frame <= snapshot.Frame : frame >= snapshot.Frame)
                        throw new InvalidOperationException(action == "seek"
                            ? "播放到帧只能选择当前帧之后的未来帧。" : "恢复到帧只能选择当前帧之前的过去帧。");
                    if (action == "seek") await PlayForwardToFrameAsync(frame, snapshot);
                    else await RestartDraftAtAsync(frame);
                }
            }
            else if (action == "save")
            {
                // Saving records a replay target. It never navigates the game to that target.
                var observed = await ReadReadyFrameSnapshotAsync();
                if (frame == -1) frame = observed.Frame;
                var draft = movieEditor.ValidateAny(MovieText).V2Document
                    ?? throw new InvalidOperationException("序列无效，请打开有效的 .hktas 文件。");
                var actual = TimelineTree.Parse(observed.Movie);
                if (!draftRequiresRestart && startupBoot?.NativeCompletedFrames > 0
                    && fullRunMovies.Mode == "Recording" && !gridHasUserEdits)
                {
                    var total = InputGridEditor.Count(draft);
                    draft = new MovieV2Document(draft.SourceName, actual.Header,
                        actual.Runs.Concat(total > observed.Frame
                            ? SliceV2(draft, observed.Frame, total - observed.Frame).Runs
                            : Array.Empty<NativeFrameRun>()));
                }
                else if (!draftRequiresRestart && draft.Header.EnvironmentSha256 == "none")
                    draft = new MovieV2Document(draft.SourceName, actual.Header, draft.Runs);
                if (frame < 0 || frame > InputGridEditor.Count(draft))
                    throw new InvalidOperationException("目标帧超出序列。");
                var snapshot = new FrameSave { Frame = frame,
                    Movie = new MovieV2Codec().WriteCanonical(draft),
                    OriginalHashes = new Dictionary<string, string>(fullRunMovies.OriginalHashes) };
                SaveTimelineSnapshot(snapshot, observed);
            }
            else
            {
                throw new InvalidOperationException("不支持的帧菜单操作：" + action);
            }
        }

        private async Task<FrameSave> ReadFrameSnapshotAsync()
        {
            if (startupBoot?.NativeCompletedFrames == 0) return new FrameSave { Frame = 0, Movie = MovieText };
            var result = await ExecuteStudioOrHumanAsync(AutomationCommandIds.FullRunSnapshot,
                AutomationScope.MovieRead, null, "Paused", null, CancellationToken.None);
            RequireAutomationSuccess(result);
            var path = Path.GetFullPath(result.Data["path"]);
            if (!path.StartsWith(Path.GetFullPath(fullRunMovies!.ShadowRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || new FileInfo(path).Length > MovieProtocolV2.MaximumSourceUtf8Bytes)
                throw new InvalidDataException("存档 Movie 路径或大小无效。");
            var movie = await File.ReadAllTextAsync(path, new UTF8Encoding(false, true));
            var candidate = movieEditor.ValidateAny(movie);
            if (!candidate.Success || candidate.V2Document == null) throw new InvalidDataException("存档 Movie 无效。");
            return new FrameSave { Frame = long.Parse(result.Data["movieFrame"], CultureInfo.InvariantCulture), Movie = movie };
        }

        private async Task<FrameSave> ReadReadyFrameSnapshotAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (true)
            {
                try { return await ReadFrameSnapshotAsync(); }
                catch (InvalidOperationException) when (DateTime.UtcNow < deadline) { await Task.Delay(200); }
            }
        }

        internal HollowKnightTAS.Core.SceneCheckpoints.SceneCheckpointHeader? FindForwardSeekSceneCheckpoint(
            long currentFrame,
            long targetFrame,
            MovieV2Document? candidateMovie)
        {
            if (candidateMovie == null
                || activeDraftTree == null
                || activeDraftTree.Length != 32
                || targetFrame <= currentFrame)
                return null;
            var checkpoint = FindBestSceneCheckpoint(activeDraftTree, targetFrame, candidateMovie);
            if (checkpoint == null)
                return null;
            if (checkpoint.ResumeMovieFrame <= currentFrame)
                return null;
            if (checkpoint.ResumeMovieFrame - currentFrame < ForwardSeekCheckpointMinFrameSavings)
                return null;
            return checkpoint;
        }

        private bool TryBuildForwardSeekCandidate(
            FrameSave snapshot,
            MovieV2Document draft,
            out MovieV2Document candidate,
            out bool modifiedFromDraft)
        {
            candidate = draft;
            modifiedFromDraft = false;
            var original = movieEditor.ValidateAny(snapshot.Movie).V2Document;
            if (original == null) return false;
            var total = InputGridEditor.Count(draft);
            if (total < snapshot.Frame) return false;

            if (gridHasUserEdits)
            {
                if (earliestGridEdit >= snapshot.Frame && total > snapshot.Frame)
                {
                    var runs = MovieV2Prefix.Take(original, snapshot.Frame).Runs
                        .Concat(SliceV2(draft, snapshot.Frame, total - snapshot.Frame).Runs)
                        .ToArray();
                    candidate = new MovieV2Document(draft.SourceName,
                        CustomKeyInput.WithUsedKeys(original.Header, draft.Header.CustomKeys, runs), runs);
                    modifiedFromDraft = true;
                }
            }
            else if (fullRunMovies?.Mode == "Recording")
            {
                var runs = original.Runs
                    .Concat(total > snapshot.Frame
                        ? SliceV2(draft, snapshot.Frame, total - snapshot.Frame).Runs
                        : Array.Empty<NativeFrameRun>())
                    .ToArray();
                candidate = new MovieV2Document(draft.SourceName,
                    CustomKeyInput.WithUsedKeys(original.Header, draft.Header.CustomKeys, runs), runs);
                modifiedFromDraft = true;
            }
            else if (draft.Header.EnvironmentSha256 == "none")
            {
                candidate = new MovieV2Document(draft.SourceName,
                    original.Header.WithCustomKeys(draft.Header.CustomKeys), draft.Runs);
                modifiedFromDraft = true;
            }

            return MovieV2Prefix.Matches(original, candidate, snapshot.Frame);
        }

        private async Task WaitForLiveSessionReadyForInProcessRestoreAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                var live = SelectedSession?.Client;
                if (live is not { IsConnected: true } || readySessions.Contains(live.SessionId))
                    return;
                await Task.Delay(25);
            }
        }

        private async Task PlayForwardToFrameAsync(long frame, FrameSave? currentSnapshot = null)
        {
            if (startupBoot!.NativeCompletedFrames == 0)
            {
                if (gridHasUserEdits && earliestGridEdit == 0)
                    throw new InvalidOperationException("启动第 0 帧的输入已修改，请先应用草稿；播放到未来帧不会自动重启。");
                var first = await fullRunMovies!.StepAsync(0, CancellationToken.None);
                if (first.Mode == "Fault") throw new InvalidOperationException(first.Error);
                var initialized = await ReadReadyFrameSnapshotAsync();
                if (initialized.Frame == frame)
                {
                    currentFullRunMovieFrame = frame;
                    TrackGridFrame(frame, true);
                    GridStatus = $"已从当前进度播放到第 {frame} 帧并暂停。";
                    return;
                }
                currentSnapshot = initialized;
            }
            if (fullRunMovies!.Mode == "Completed")
            {
                var current = currentSnapshot ?? await ReadReadyFrameSnapshotAsync();
                var original = movieEditor.ValidateAny(current.Movie).V2Document!;
                var draft = GridAny().V2Document!;
                if (!MovieV2Prefix.Matches(original, draft, current.Frame))
                    throw new InvalidOperationException("已修改过去的帧，请先点击回放到当前帧。");
                await RestartDraftAtAsync(frame);
                return;
            }
            if (restartProtectedGame != null)
            {
                var snapshot = currentSnapshot ?? await ReadReadyFrameSnapshotAsync();
                var draft = GridAny().V2Document;
                if (draft != null
                    && snapshot.Frame < frame
                    && TryBuildForwardSeekCandidate(snapshot, draft, out var candidate, out var modifiedFromDraft))
                {
                    var forwardCheckpoint = FindForwardSeekSceneCheckpoint(snapshot.Frame, frame, candidate);
                    if (forwardCheckpoint != null)
                    {
                        await WaitForLiveSessionReadyForInProcessRestoreAsync();
                        var sourceMovie = modifiedFromDraft
                            ? new MovieV2Codec().WriteCanonical(candidate)
                            : null;
                        await RestartDraftAtAsync(frame, sourceMovie: sourceMovie);
                        return;
                    }
                }
            }
            await ApplyPendingInputsAsync(withinGridOperation: true);
            var result = await ExecuteStudioOrHumanAsync(AutomationCommandIds.FullRunSeek,
                AutomationScope.ControlPlayback, new Dictionary<string, string>
                {
                    ["targetFrame"] = frame.ToString(CultureInfo.InvariantCulture),
                    ["expectedNativeFrame"] = startupBoot!.NativeCompletedFrames.ToString(CultureInfo.InvariantCulture)
                }, "Paused", null, CancellationToken.None);
            RequireAutomationSuccess(result);
            if (SelectedSession?.Client is { IsConnected: true } client)
                EnsureAutoSceneCheckpointCapture(client);
            var started = await fullRunMovies.RunAsync(startupBoot.NativeCompletedFrames, CancellationToken.None);
            if (started.Mode == "Fault") throw new InvalidOperationException(started.Error);
            var deadline = DateTime.UtcNow.AddMinutes(10);
            while (true)
            {
                startupBoot.Refresh();
                if (startupBoot.FullRunFaultCode != 0) throw new InvalidOperationException("播放遇到原生错误：" + startupBoot.FullRunFaultCode);
                if (startupBoot.IsWaiting) break;
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("播放到指定帧超时。");
                await Task.Delay(100);
            }
            var finalSnapshot = await ReadReadyFrameSnapshotAsync();
            if (finalSnapshot.Frame != frame) throw new InvalidOperationException($"播放停在 {finalSnapshot.Frame}，未到达目标 {frame}。");
            currentFullRunMovieFrame = frame;
            TrackGridFrame(frame, true);
            GridStatus = $"已从当前进度播放到第 {frame} 帧并暂停。";
        }

        private async Task RestartDraftAtAsync(long frame, string? sourceMovie = null, CancellationToken cancellationToken = default, bool pauseWhenInputReadyZero = false,
            bool switchSequence = false, InitialSaveSnapshot? sourceSaves = null, bool saveCurrentBranch = true)
        {
            using var timing = new ReplayPerformanceTrace("restore");
            if (saveCurrentBranch) await SaveCurrentBranchAsync(closing: true);
            timing.Mark("branch-saved");
            if (switchSequence)
            {
                SetSequenceInitialSaves(sourceSaves);
            }
            SetRestorePresentationFrozen(true);
            restoreTargetFrame = frame;
            var completed = false;
            try
            {
                if (sourceMovie != null) MovieText = sourceMovie;
                await RestartDraftCoreAsync(frame, cancellationToken, pauseWhenInputReadyZero, timing);
                SetRestoreProgress(1);
                completed = true;
            }
            finally
            {
                try { if (finishRestorePresentation != null) await finishRestorePresentation(completed); }
                finally { SetRestorePresentationFrozen(false); }
            }
            timing.Complete();
        }

        private async Task RestartDraftCoreAsync(long frame, CancellationToken cancellationToken = default, bool pauseWhenInputReadyZero = false,
            ReplayPerformanceTrace? timing = null)
        {
            var candidate = GridAny().V2Document ?? throw new InvalidOperationException("需要有效的 v2 Movie。");
            if (candidate.Header.EnvironmentSha256 == "none" && sequenceInitialSaves != null
                && sequenceInitialSaves.Id != fullRunMovies?.SessionInitialSaves?.Id)
            {
                if (restartProtectedGame == null) throw new InvalidOperationException("受控重启入口不可用。");
                await restartProtectedGame();
            }
            if (candidate.Header.EnvironmentSha256 == "none" && startupBoot?.NativeCompletedFrames == 0)
            {
                if (fullRunMovies!.Mode == "Unarmed") fullRunMovies.ArmRecording(candidate.Header.MouseEnabled, ParseFrameRate(DefaultFrameRate));
                if (fullRunMovies.Mode != "Recording") throw new InvalidOperationException("草稿环境尚未初始化，请新建录制后重试。");
                await fullRunMovies.StepAsync(0, cancellationToken);
            }
            if (candidate.Header.EnvironmentSha256 == "none" && startupBoot?.NativeCompletedFrames > 0)
            {
                var snapshot = await ReadReadyFrameSnapshotAsync();
                var recorded = movieEditor.ValidateAny(snapshot.Movie).V2Document!;
                candidate = new MovieV2Document(candidate.SourceName, recorded.Header.WithCustomKeys(candidate.Header.CustomKeys), candidate.Runs);
                MovieText = gridSource = new MovieV2Codec().WriteCanonical(candidate);
            }
            if (frame > InputGridEditor.Count(candidate)) throw new InvalidOperationException("存档目标超出 Movie。");
            if (frame == InputGridEditor.Count(candidate))
                candidate = new MovieV2Document(candidate.SourceName, candidate.Header, candidate.Runs.Concat(new[] {
                    new NativeFrameRun(500, Array.Empty<GameInputSample>(), new MovieSourceSpan("<resume>", 1, 1, 1), ParseFrameRate(DefaultFrameRate), true) }));
            MovieText = gridSource = new MovieV2Codec().WriteCanonical(candidate);
            if (restartProtectedGame == null) throw new InvalidOperationException("受控重启入口不可用。");
            GridStatus = "正在恢复，请稍候…";
            cancellationToken.ThrowIfCancellationRequested();
            timing?.Mark("movie-prepared");

            // 1. 查找当前活跃世界线分支下是否具备适用的已验证场景快照
            lastRestoredCheckpointId = null;
            lastRestoredCheckpointFrame = -1;
            HollowKnightTAS.Core.SceneCheckpoints.SceneCheckpointHeader? availableCheckpoint = null;
            if (activeDraftTree != null && activeDraftTree.Length == 32)
            {
                try
                {
                    availableCheckpoint = FindBestSceneCheckpoint(activeDraftTree, frame, candidate);
                }
                catch { }
            }

            await CancelAutoSceneCheckpointCaptureAsync();
            var restoredViaCheckpoint = false;

            var liveClient = SelectedSession?.Client;
            var canRestoreInProcess = availableCheckpoint != null
                && liveClient is { IsConnected: true }
                && startupBoot is { IsPending: true, FullRunFaultCode: 0 }
                && startupBoot.NativeCompletedFrames > 0
                && fullRunMovies is { IsArmed: true, IsTerminal: false }
                && !string.IsNullOrEmpty(fullRunMovies.ShadowRoot)
                && readySessions.Contains(liveClient.SessionId)
                && (sequenceInitialSaves == null || sequenceInitialSaves.Id == fullRunMovies.SessionInitialSaves?.Id);

            if (canRestoreInProcess)
            {
                var inProcessRestoreAttempted = false;
                try
                {
                    if (!startupBoot!.IsWaiting)
                    {
                        await fullRunMovies!.PauseForDocumentChangeAsync(cancellationToken);
                    }
                    startupBoot.Refresh();
                    liveClient = SelectedSession?.Client;
                    if (!startupBoot.IsWaiting || startupBoot.FullRunFaultCode != 0 || fullRunMovies!.IsTerminal
                        || liveClient is not { IsConnected: true } readyLiveClient
                        || !readySessions.Contains(readyLiveClient.SessionId))
                    {
                        throw new InvalidOperationException("In-process checkpoint restore prerequisites changed while pausing.");
                    }

                    var tasRoot = Path.Combine(fullRunMovies.ShadowRoot, "HollowKnightTAS");
                    Directory.CreateDirectory(tasRoot);
                    var tempMoviePath = Path.Combine(tasRoot, "inprocess-restore-pending.hktas");
                    File.WriteAllText(tempMoviePath, new MovieV2Codec().WriteCanonical(candidate), new UTF8Encoding(false));

                    timing?.Mark("inprocess-checkpoint-restore-start");
                    inProcessRestoreAttempted = true;
                    await ExecuteCheckpointRestoreSequenceAsync(
                        readyLiveClient,
                        availableCheckpoint!,
                        frame,
                        tempMoviePath,
                        markLiveReplay: true,
                        timing,
                        cancellationToken);
                    restoredViaCheckpoint = true;
                    timing?.Mark("checkpoint-restore-completed");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine("In-process checkpoint restore failed: " + ex.Message);
                    timing?.Mark("inprocess-checkpoint-restore-fallback");
                    restoredViaCheckpoint = false;
                    if (inProcessRestoreAttempted || IsNonRetryableCheckpointFailure(ex))
                    {
                        if (inProcessRestoreAttempted && !IsPreFlightCleanStartRejection(ex))
                            InvalidateSceneCheckpoint(activeDraftTree, availableCheckpoint?.CheckpointId);
                        availableCheckpoint = null;
                    }
                }
            }

            if (!restoredViaCheckpoint)
            {
                var oldSessionId = SelectedSession?.Client.SessionId;
                await restartProtectedGame();
                timing?.Mark("restart-ready");
                cancellationToken.ThrowIfCancellationRequested();

                var coldReplayAlreadyBootedAtFrameZero = false;
                if (availableCheckpoint != null)
                {
                    var restoreStarted = false;
                    try
                    {
                        timing?.Mark("checkpoint-restore-start");
                        // 先将新游戏实例推进并停在 Movie 0，使 Runtime 会话与受保护存档上下文完成初始化。
                        fullRunMovies!.ArmReplay(candidate, 0);
                        var bootStarted = await fullRunMovies.RunAsync(0, cancellationToken);
                        if (bootStarted.Mode == "Fault") throw new InvalidOperationException(bootStarted.Error);
                        var bootDeadline = DateTime.UtcNow.AddSeconds(60);
                        RuntimeSessionClient bootClient;
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            startupBoot!.Refresh();
                            if (startupBoot.FullRunFaultCode != 0) throw new InvalidOperationException("检查点启动遇到原生错误：" + startupBoot.FullRunFaultCode);
                            if (startupBoot.NativeCompletedFrames > 0
                                && startupBoot.IsWaiting
                                && SelectedSession?.Client is { IsConnected: true } readyClient
                                && readyClient.SessionId != oldSessionId
                                && readySessions.Contains(readyClient.SessionId))
                            {
                                bootClient = readyClient;
                                break;
                            }
                            if (DateTime.UtcNow >= bootDeadline) throw new TimeoutException("检查点替换实例启动超时。");
                            await Task.Delay(50, cancellationToken);
                        }

                        RememberRuntimeEnvironmentManifestSha256(bootClient.EnvironmentManifestSha256);
                        if (!IsCheckpointBindingCompatible(availableCheckpoint, candidate, bootClient.EnvironmentManifestSha256))
                        {
                            System.Diagnostics.Trace.WriteLine(
                                $"Checkpoint {availableCheckpoint.CheckpointId} binding mismatch for environment {bootClient.EnvironmentManifestSha256}; continuing cold replay on current process.");
                            timing?.Mark("checkpoint-environment-mismatch-continue-cold");
                            availableCheckpoint = null;
                            coldReplayAlreadyBootedAtFrameZero = true;
                        }
                        else
                        {
                            await ExecuteCheckpointRestoreSequenceAsync(
                                bootClient,
                                availableCheckpoint,
                                frame,
                                moviePath: null,
                                markLiveReplay: false,
                                timing,
                                cancellationToken,
                                onRestoreStarted: () => restoreStarted = true);
                            restoredViaCheckpoint = true;
                            timing?.Mark("checkpoint-restore-completed");
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine("Checkpoint restore failed: " + ex.Message);
                        if (!IsPreFlightCleanStartRejection(ex))
                        {
                            InvalidateSceneCheckpoint(activeDraftTree, availableCheckpoint?.CheckpointId);
                        }
                        if (!restoreStarted || IsPreFlightCleanStartRejection(ex))
                        {
                            timing?.Mark("checkpoint-stage-failed-continue-cold");
                            restoredViaCheckpoint = false;
                            availableCheckpoint = null;
                            coldReplayAlreadyBootedAtFrameZero = true;
                        }
                        else
                        {
                            // 恢复异常安全回退红线：安全回退到从第 0 帧完整冷重播！
                            timing?.Mark("checkpoint-restore-failed-fallback");
                            restoredViaCheckpoint = false;
                            availableCheckpoint = null;
                            startupBoot?.Refresh();
                            if (startupBoot != null && !startupBoot.IsWaiting && fullRunMovies != null && !fullRunMovies.IsTerminal)
                            {
                                try { await fullRunMovies.PauseForDocumentChangeAsync(CancellationToken.None); }
                                catch { }
                                startupBoot.Refresh();
                            }
                            await restartProtectedGame();
                        }
                    }
                }

                if (!restoredViaCheckpoint)
                {
                    if (coldReplayAlreadyBootedAtFrameZero)
                    {
                        timing?.Mark("replay-armed");
                        restoreReplayStarted = true;
                        currentFullRunMovieFrame = 0;
                        RefreshInputGrid();
                        if (frame > 0)
                        {
                            allowAutoCheckpointCaptureDuringColdReplay = true;
                            try
                            {
                                if (SelectedSession?.Client is { IsConnected: true } coldClient)
                                    EnsureAutoSceneCheckpointCapture(coldClient);
                                startupBoot!.Refresh();
                                var seekResult = await ExecuteStudioOrHumanAsync(
                                    AutomationCommandIds.FullRunSeek,
                                    AutomationScope.ControlPlayback,
                                    new Dictionary<string, string>
                                    {
                                        ["targetFrame"] = frame.ToString(CultureInfo.InvariantCulture),
                                        ["expectedNativeFrame"] = startupBoot.NativeCompletedFrames.ToString(CultureInfo.InvariantCulture)
                                    },
                                    "Paused", null, cancellationToken);
                                RequireAutomationSuccess(seekResult);
                                var started = await fullRunMovies!.RunAsync(startupBoot.NativeCompletedFrames, cancellationToken);
                                if (started.Mode == "Fault") throw new InvalidOperationException(started.Error);
                                await WaitForColdReplayBoundaryAsync(timing, cancellationToken);
                            }
                            finally
                            {
                                allowAutoCheckpointCaptureDuringColdReplay = false;
                            }
                        }
                    }
                    else
                    {
                        fullRunMovies!.ArmReplay(candidate, frame == 0 && !pauseWhenInputReadyZero ? -1 : frame);
                        timing?.Mark("replay-armed");
                        restoreReplayStarted = true;
                        currentFullRunMovieFrame = 0;
                        RefreshInputGrid();
                        if (frame > 0)
                        {
                            allowAutoCheckpointCaptureDuringColdReplay = true;
                            try
                            {
                                var started = await fullRunMovies.RunAsync(0, cancellationToken);
                                if (started.Mode == "Fault") throw new InvalidOperationException(started.Error);
                                await WaitForColdReplayBoundaryAsync(timing, cancellationToken);
                            }
                            finally
                            {
                                allowAutoCheckpointCaptureDuringColdReplay = false;
                            }
                        }
                    }
                }
            }

            if (frame > 0)
            {
                var observed = await ReadReadyFrameSnapshotAsync();
                if (observed.Frame != frame) throw new InvalidOperationException($"重放停在 {observed.Frame}，未到达目标 {frame}。");
            }
            timing?.Mark("target-verified");
            lastRestoredCheckpointId = restoredViaCheckpoint ? availableCheckpoint!.CheckpointId : null;
            lastRestoredCheckpointFrame = restoredViaCheckpoint ? availableCheckpoint!.ResumeMovieFrame : -1;
            currentFullRunMovieFrame = frame;
            gridHasUserEdits = false;
            draftRequiresRestart = false;
            earliestGridEdit = long.MaxValue;
            TrackGridFrame(frame, true);
            GridStatus = restoredViaCheckpoint
                ? $"已通过场景检查点快速恢复并停在第 {frame} 帧边界（K={availableCheckpoint!.ResumeMovieFrame}）。"
                : $"已停在第 {frame} 帧边界。";
        }

        internal static bool IsNonRetryableCheckpointFailure(Exception ex)
            => ex is InvalidDataException
                || IsPreFlightCleanStartRejection(ex)
                || ex.Message.Contains("new-game", StringComparison.OrdinalIgnoreCase);

        private static bool IsPreFlightCleanStartRejection(Exception ex)
            => ex.Message.Contains("requires an in-game process", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("has no initial save on disk", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("outside valid save slots", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("environment mismatch", StringComparison.OrdinalIgnoreCase);

        private async Task WaitForColdReplayBoundaryAsync(ReplayPerformanceTrace? timing, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow.AddMinutes(10);
            var progressObserved = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                startupBoot!.Refresh();
                if (SelectedSession?.Client is { IsConnected: true } coldClient && readySessions.Contains(coldClient.SessionId))
                {
                    RememberRuntimeEnvironmentManifestSha256(coldClient.EnvironmentManifestSha256);
                    EnsureAutoSceneCheckpointCapture(coldClient);
                }
                if (!progressObserved && currentFullRunMovieFrame > 0)
                {
                    timing?.Mark("first-movie-progress-observed");
                    progressObserved = true;
                }
                if (startupBoot.FullRunFaultCode != 0) throw new InvalidOperationException("重放遇到原生错误：" + startupBoot.FullRunFaultCode);
                if (startupBoot.IsWaiting) break;
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("重放等待超时，可手动暂停检查状态。");
                await Task.Delay(100, cancellationToken);
            }
        }

        private async Task ExecuteCheckpointRestoreSequenceAsync(
            RuntimeSessionClient session,
            HollowKnightTAS.Core.SceneCheckpoints.SceneCheckpointHeader checkpoint,
            long targetFrame,
            string? moviePath,
            bool markLiveReplay,
            ReplayPerformanceTrace? timing,
            CancellationToken cancellationToken,
            Action? onRestoreStarted = null)
        {
            var checkpointClient = CreateSceneCheckpointClient(session);
            var scope = SceneCheckpoints!.ActivateBranch(checkpoint.Scope.TreeId, checkpoint.Scope.BranchId);
            var binding = checkpoint.Binding;
            var checkpointId = checkpoint.CheckpointId;

            await checkpointClient.StageAsync(checkpointId, scope, binding, cancellationToken);
            onRestoreStarted?.Invoke();
            await checkpointClient.StartRestoreAsync(checkpointId, scope, binding, cancellationToken, moviePath);

            // 放行原生时钟推进目标存档加载（或进程内直接切换场景），直到目标到达对应 S0 边界并重新暂停。
            startupBoot!.Refresh();
            var loadStarted = await fullRunMovies!.RunAsync(startupBoot.NativeCompletedFrames, cancellationToken);
            if (loadStarted.Mode == "Fault") throw new InvalidOperationException(loadStarted.Error);

            var cDeadline = DateTime.UtcNow.AddSeconds(60);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                startupBoot.Refresh();
                if (startupBoot.FullRunFaultCode != 0) throw new InvalidOperationException("检查点加载阶段遇到原生错误：" + startupBoot.FullRunFaultCode);
                var status = await checkpointClient.RestoreStatusAsync(checkpointId, cancellationToken);
                if (status.TryGetValue("status", out var s) && s == "AwaitingApplyAtC" && startupBoot.IsWaiting) break;
                if (s == "Failed" || s == "Cancelled") throw new InvalidOperationException("检查点准备阶段失败: " + s);
                if (DateTime.UtcNow >= cDeadline) throw new TimeoutException("等待目标 S0 边界超时。");
                await Task.Delay(50, cancellationToken);
            }

            await checkpointClient.ApplyRestoreAsync(checkpointId, scope, binding, cancellationToken, targetMovieFrame: targetFrame);
            if (markLiveReplay)
            {
                fullRunMovies.MarkLiveReplay();
            }
            startupBoot.Refresh();
            timing?.Mark("replay-armed");
            restoreReplayStarted = true;
            currentFullRunMovieFrame = checkpoint.ResumeMovieFrame;
            RefreshInputGrid();

            // 从 S0 推进经过 K 并精确停在目标帧（K 或 targetFrame）。
            var resumeStarted = await fullRunMovies.RunAsync(startupBoot.NativeCompletedFrames, cancellationToken);
            if (resumeStarted.Mode == "Fault") throw new InvalidOperationException(resumeStarted.Error);
            var deadline = DateTime.UtcNow.AddMinutes(10);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                startupBoot.Refresh();
                if (startupBoot.FullRunFaultCode != 0) throw new InvalidOperationException("检查点补跑遇到原生错误：" + startupBoot.FullRunFaultCode);
                if (startupBoot.IsWaiting) break;
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("重放等待超时，可手动暂停检查状态。");
                await Task.Delay(100, cancellationToken);
            }
        }
    }
}
