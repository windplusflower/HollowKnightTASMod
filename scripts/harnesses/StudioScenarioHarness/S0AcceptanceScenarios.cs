using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Companion.Automation;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.ViewModels;
using HollowKnightTAS.Core.Automation;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Core.SceneCheckpoints;

internal static partial class StudioScenarioHarness
{
    private sealed class S0AcceptanceRunRecord
    {
        public int Index { get; set; }
        public string SegmentName { get; set; } = "";
        public string CheckpointId { get; set; } = "";
        public long TransitionId { get; set; }
        public long CaptureMovieFrame { get; set; }
        public long ResumeMovieFrame { get; set; }
        public long CaptureNativeFrame { get; set; }
        public long ResumeNativeFrame { get; set; }
        public long TargetMovieFrame { get; set; }
        public bool InProcess { get; set; } = true;
        public bool Attempted { get; set; }
        public bool Succeeded { get; set; }
        public bool Skipped { get; set; }
        public string Error { get; set; } = "";
        public double ElapsedMs { get; set; }
    }

    static async Task RunS0AcceptanceAsync(string[] args)
    {
        string Arg(string name) => args.Single(a => a.StartsWith(name + "=", StringComparison.Ordinal)).Split('=', 2)[1];
        var packagePath = Path.GetFullPath(Arg("--acceptance-package"));
        var suiteName = args.SingleOrDefault(a => a.StartsWith("--acceptance-suite=", StringComparison.Ordinal))?.Split('=', 2)[1]
            ?? Path.GetFileNameWithoutExtension(packagePath);
        Require(File.Exists(packagePath), "acceptance package exists: " + packagePath);

        Environment.SetEnvironmentVariable("HKTAS_ACCEPTANCE_FRAME_TRACE", "1");

        var vm = (MainViewModel)app.MainWindow.DataContext;
        var boot = Field<StartupBootController>(app, "startupBoot");
        var movies = Field<FullRunMovieCoordinator>(app, "fullRunMovies");
        const BindingFlags instanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
        void SetVmField(string name, object? value) => typeof(MainViewModel).GetField(name, instanceNonPublic)!.SetValue(vm, value);
        var ensureAutoCapture = typeof(MainViewModel).GetMethod("EnsureAutoSceneCheckpointCapture", instanceNonPublic)!;
        var cancelAutoCapture = typeof(MainViewModel).GetMethod("CancelAutoSceneCheckpointCaptureAsync", instanceNonPublic)!;
        var rememberManifest = typeof(MainViewModel).GetMethod("RememberRuntimeEnvironmentManifestSha256", instanceNonPublic)!;
        var setRestoreFrozen = typeof(MainViewModel).GetMethod("SetRestorePresentationFrozen", instanceNonPublic)!;
        var setGridApplying = typeof(MainViewModel).GetMethod("SetGridApplying", instanceNonPublic)!;

        SetVmField("worldlines", new StudioTimelineStore(Path.Combine(output, "timelines.json")));
        SetVmField("initialSaveCacheRoot", Path.Combine(output, "initial-saves"));
        SetVmField("activeSequenceDirectory", output);
        SetVmField("activeAutoSaveSeconds", 0);

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        var package = SequencePackage.Read(packagePath);
        var originalDoc = new MovieV2Codec().Parse(new StringReader(package.Movie), packagePath).Document
            ?? throw new InvalidDataException("Invalid acceptance Movie: " + packagePath);
        var originalTotalFrames = originalDoc.Runs.Sum(r => r.RepeatCount);
        Require(originalTotalFrames > 0, "acceptance Movie has positive frame count");

        // Append 1 trailing neutral frame so pausing at stopFrame == originalTotalFrames
        // executes every frame [0, originalTotalFrames - 1] without entering terminal Completed mode.
        var tailFps = originalDoc.Runs.Count > 0 ? originalDoc.Runs[^1].FramesPerSecond : 50m;
        var bufferedDoc = new MovieV2Document(
            originalDoc.SourceName,
            originalDoc.Header,
            originalDoc.Runs.Concat(new[]
            {
                new NativeFrameRun(
                    1,
                    Array.Empty<GameInputSample>(),
                    new MovieSourceSpan("<acceptance-tail>", 1, 1, 1),
                    tailFps,
                    true)
            }).ToArray());
        var bufferedCanonical = new MovieV2Codec().WriteCanonical(bufferedDoc);
        var stopFrame = originalTotalFrames;

        var strippedPath = Path.Combine(output, "acceptance-input.hktaspack");
        var initialSaves = package.InitialSaves ?? new InitialSaveSnapshot(new Dictionary<string, byte[]>());
        await SequencePackage.WriteAsync(strippedPath, bufferedCanonical, initialSaves);
        await vm.OpenMovieFileAsync(strippedPath);

        var treeId = Field<string?>(vm, "activeDraftTree")
            ?? throw new InvalidOperationException("Acceptance timeline tree was not initialized.");
        var readySessions = Field<HashSet<string>>(vm, "readySessions");

        var runRecords = new List<S0AcceptanceRunRecord>();
        IReadOnlyList<SceneCheckpointHeader> allCheckpoints = Array.Empty<SceneCheckpointHeader>();
        string baselineStageError = "";

        async Task WaitGamePausedAsync(Process game, string phase, int timeoutSeconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (true)
            {
                if (game.HasExited)
                    throw new InvalidOperationException($"{phase}: owned game process {game.Id} exited unexpectedly (code={game.ExitCode}).");
                boot.Refresh();
                if (boot.FullRunFaultCode != 0)
                    throw new InvalidOperationException($"{phase}: native fault={boot.FullRunFaultCode}");
                if (vm.SelectedSession?.Client is { IsConnected: true } client && readySessions.Contains(client.SessionId))
                {
                    rememberManifest.Invoke(vm, new object?[] { client.EnvironmentManifestSha256 });
                    ensureAutoCapture.Invoke(vm, new object[] { client });
                }
                if (boot.IsWaiting) return;
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException($"{phase}: timed out after {timeoutSeconds}s waiting for pause boundary.");
                await Task.Delay(100);
            }
        }

        async Task SeekAsync(long target)
        {
            boot.Refresh();
            var result = await Field<AutomationBroker>(vm, "automationBroker").ExecuteHumanAsync(
                AutomationCommandIds.FullRunSeek,
                AutomationScope.ControlPlayback,
                new Dictionary<string, string>
                {
                    ["targetFrame"] = target.ToString(CultureInfo.InvariantCulture),
                    ["expectedNativeFrame"] = boot.NativeCompletedFrames.ToString(CultureInfo.InvariantCulture)
                },
                "Paused",
                null,
                CancellationToken.None);
            Require(result.Success, $"seek to frame {target} accepted: {result.Detail}");
        }

        void ExportTraceAndMetadata()
        {
            try
            {
                if (!string.IsNullOrEmpty(movies.ShadowRoot))
                {
                    var sessionsDir = Path.Combine(movies.ShadowRoot, "HollowKnightTAS", "sessions");
                    if (Directory.Exists(sessionsDir))
                    {
                        var traces = Directory.GetFiles(sessionsDir, "acceptance-frame-trace.csv", SearchOption.AllDirectories);
                        if (traces.Length > 0)
                        {
                            var latest = traces.OrderByDescending(File.GetLastWriteTimeUtc).First();
                            File.Copy(latest, Path.Combine(output, "acceptance-frame-trace.csv"), true);
                        }
                        var rngDiags = Directory.GetFiles(sessionsDir, "acceptance-rng-diag.jsonl", SearchOption.AllDirectories);
                        if (rngDiags.Length > 0)
                        {
                            var latestDiag = rngDiags.OrderByDescending(File.GetLastWriteTimeUtc).First();
                            File.Copy(latestDiag, Path.Combine(output, "acceptance-rng-diag.jsonl"), true);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("WARN failed to copy acceptance-frame-trace.csv: " + ex.Message);
            }

            try
            {
                var cpList = allCheckpoints.Select(h => new
                {
                    checkpointId = h.CheckpointId,
                    transitionId = h.TransitionId,
                    captureMovieFrame = h.CaptureMovieFrame,
                    resumeMovieFrame = h.ResumeMovieFrame,
                    captureNativeFrame = h.CaptureNativeFrame,
                    resumeNativeFrame = h.ResumeNativeFrame,
                    payloadBytes = h.PayloadBytes,
                    restoreQualified = h.RestoreQualified,
                    treeId = h.Scope.TreeId,
                    branchId = h.Scope.BranchId
                }).ToList();
                File.WriteAllText(
                    Path.Combine(output, "baseline-checkpoints.json"),
                    JsonSerializer.Serialize(new
                    {
                        suite = suiteName,
                        packagePath,
                        originalTotalFrames,
                        stopFrame,
                        capturedCount = cpList.Count,
                        qualifiedCount = cpList.Count(c => c.restoreQualified),
                        checkpoints = cpList
                    }, jsonOptions),
                    new UTF8Encoding(false));

                File.WriteAllText(
                    Path.Combine(output, "s0-runs.json"),
                    JsonSerializer.Serialize(new
                    {
                        suite = suiteName,
                        packagePath,
                        originalTotalFrames,
                        stopFrame,
                        baselineError = baselineStageError,
                        capturedCount = cpList.Count,
                        qualifiedCount = cpList.Count(c => c.restoreQualified),
                        runs = runRecords
                    }, jsonOptions),
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log("WARN failed to persist acceptance metadata: " + ex.Message);
            }
        }

        try
        {
            Log($"S0-ACCEPTANCE START suite={suiteName} totalFrames={originalTotalFrames} stopFrame={stopFrame}");
            await Field<Func<string, Task>>(vm, "launchGame")(@"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe");
            Require(boot.IsWaiting && boot.NativeCompletedFrames == 0, "acceptance game started at protected native zero");
            var gameProcess = Field<Process>(app, "startupGame");

            // Step A1: reach movieFrame 0 so Runtime is connected and auto-capture is armed before any scene transition.
            movies.ArmReplay(bufferedDoc, 0);
            SetVmField("draftRequiresRestart", false);
            var bootStart = await movies.RunAsync(0, CancellationToken.None);
            Require(bootStart.Mode != "Fault", "acceptance bootstrap RunAsync failed: " + bootStart.Error);
            await WaitGamePausedAsync(gameProcess, "bootstrap-zero", 120);
            await Until(() => vm.SelectedSession?.Client.IsConnected == true
                && readySessions.Contains(vm.SelectedSession.Client.SessionId),
                "acceptance runtime connected", 60);
            var zeroState = await VideoRuntimeState(vm);
            Require(zeroState["movieFrame"] == "0", "acceptance bootstrap parked at movieFrame 0");
            ensureAutoCapture.Invoke(vm, new object[] { vm.SelectedSession!.Client });
            await Task.Delay(200);

            // Step A2: replay full sequence [0, stopFrame) and capture all S0 boundaries along the way.
            await SeekAsync(stopFrame);
            boot.Refresh();
            var baselineStart = await movies.RunAsync(boot.NativeCompletedFrames, CancellationToken.None);
            Require(baselineStart.Mode != "Fault", "baseline RunAsync failed: " + baselineStart.Error);
            int baselineTimeoutSec = Math.Max(900, (int)Math.Ceiling(originalTotalFrames / 15.0) + 120);
            await WaitGamePausedAsync(gameProcess, "baseline-full-run", baselineTimeoutSec);

            var baselineEndState = await VideoRuntimeState(vm);
            Require(baselineEndState["movieFrame"] == stopFrame.ToString(CultureInfo.InvariantCulture)
                && baselineEndState["faultCode"] == "0"
                && baselineEndState["mismatchCount"] == "0",
                $"baseline completed to stopFrame={stopFrame}: movie={baselineEndState["movieFrame"]} fault={baselineEndState["faultCode"]} mismatch={baselineEndState["mismatchCount"]}");

            // Allow any in-flight auto-capture transfer at the end of baseline to finish before cancelling.
            int lastCount = -1;
            int stableTicks = 0;
            for (int i = 0; i < 40; i++)
            {
                int currentCount = vm.SceneCheckpoints.CatalogTree(treeId).Count;
                if (currentCount == lastCount)
                {
                    stableTicks++;
                    if (stableTicks >= 12) break;
                }
                else
                {
                    lastCount = currentCount;
                    stableTicks = 0;
                }
                await Task.Delay(100);
            }

            await (Task)cancelAutoCapture.Invoke(vm, Array.Empty<object>())!;
            allCheckpoints = vm.SceneCheckpoints.CatalogTree(treeId)
                .OrderBy(h => h.ResumeMovieFrame)
                .ThenBy(h => h.TransitionId)
                .ToList();
            var qualified = allCheckpoints.Where(h => h.RestoreQualified).ToList();
            Log($"S0-ACCEPTANCE BASELINE COMPLETE captured={allCheckpoints.Count} qualified={qualified.Count}");

            // Stage B: in-process manual restore to each qualified S0 checkpoint and replay to stopFrame.
            var tasRoot = Path.Combine(movies.ShadowRoot, "HollowKnightTAS");
            Directory.CreateDirectory(tasRoot);
            var tempMoviePath = Path.Combine(tasRoot, "inprocess-restore-pending.hktas");
            await File.WriteAllTextAsync(tempMoviePath, bufferedCanonical, new UTF8Encoding(false));

            bool processUnusable = false;
            string fatalReason = "";

            // Freeze Studio background grid polling / auto-capture / branch saves during Stage B
            // to match MainViewModel.FrameMenuAsync -> RestartDraftAtAsync isolation.
            setGridApplying.Invoke(vm, new object[] { true });
            setRestoreFrozen.Invoke(vm, new object[] { true });
            try
            {
            for (int i = 0; i < qualified.Count; i++)
            {
                var cp = qualified[i];
                var record = new S0AcceptanceRunRecord
                {
                    Index = i + 1,
                    SegmentName = $"s0-restore-{i + 1}:{cp.CheckpointId}",
                    CheckpointId = cp.CheckpointId,
                    TransitionId = cp.TransitionId,
                    CaptureMovieFrame = cp.CaptureMovieFrame,
                    ResumeMovieFrame = cp.ResumeMovieFrame,
                    CaptureNativeFrame = cp.CaptureNativeFrame,
                    ResumeNativeFrame = cp.ResumeNativeFrame,
                    TargetMovieFrame = Math.Max(stopFrame, cp.ResumeMovieFrame),
                    InProcess = true
                };
                runRecords.Add(record);

                if (processUnusable)
                {
                    record.Attempted = false;
                    record.Succeeded = false;
                    record.Skipped = true;
                    record.Error = "Skipped after prior fatal restore error: " + fatalReason;
                    continue;
                }

                var sw = Stopwatch.StartNew();
                record.Attempted = true;
                var client = vm.CreateSceneCheckpointClient(vm.SelectedSession!.Client);
                try
                {
                    boot.Refresh();
                    if (gameProcess.HasExited || !boot.IsWaiting || boot.FullRunFaultCode != 0 || movies.IsTerminal)
                        throw new InvalidOperationException($"Game process is not in a paused resumable state before restore (exited={gameProcess.HasExited}, waiting={boot.IsWaiting}, fault={boot.FullRunFaultCode}, terminal={movies.IsTerminal}).");

                    var scope = vm.SceneCheckpoints.ActivateBranch(cp.Scope.TreeId, cp.Scope.BranchId);
                    await client.StageAsync(cp.CheckpointId, scope, cp.Binding, CancellationToken.None);
                    await client.StartRestoreAsync(cp.CheckpointId, scope, cp.Binding, CancellationToken.None, tempMoviePath);

                    boot.Refresh();
                    var loadStarted = await movies.RunAsync(boot.NativeCompletedFrames, CancellationToken.None);
                    if (loadStarted.Mode == "Fault")
                        throw new InvalidOperationException("StartRestore RunAsync fault: " + loadStarted.Error);

                    var cDeadline = DateTime.UtcNow.AddSeconds(120);
                    while (true)
                    {
                        if (gameProcess.HasExited)
                            throw new InvalidOperationException($"Game exited while loading S0 {cp.CheckpointId}.");
                        boot.Refresh();
                        if (boot.FullRunFaultCode != 0)
                            throw new InvalidOperationException($"Native fault {boot.FullRunFaultCode} while loading S0 {cp.CheckpointId}.");
                        var status = await client.RestoreStatusAsync(cp.CheckpointId, CancellationToken.None);
                        if (status.TryGetValue("status", out var s))
                        {
                            if (s == "AwaitingApplyAtC" && boot.IsWaiting) break;
                            if (s == "Failed" || s == "Cancelled")
                            {
                                var detail = status.TryGetValue("detail", out var d) ? d : "";
                                throw new InvalidOperationException($"RestoreStatus={s} {detail}".Trim());
                            }
                        }
                        if (DateTime.UtcNow >= cDeadline)
                            throw new TimeoutException($"Timed out waiting for AwaitingApplyAtC on S0 {cp.CheckpointId}.");
                        await Task.Delay(50);
                    }

                    await client.ApplyRestoreAsync(cp.CheckpointId, scope, cp.Binding, CancellationToken.None, targetMovieFrame: record.TargetMovieFrame);
                    movies.MarkLiveReplay();
                    boot.Refresh();

                    var resumeStarted = await movies.RunAsync(boot.NativeCompletedFrames, CancellationToken.None);
                    if (resumeStarted.Mode == "Fault")
                        throw new InvalidOperationException("Post-apply RunAsync fault: " + resumeStarted.Error);

                    int suffixTimeoutSec = Math.Max(600, (int)Math.Ceiling(Math.Max(0, record.TargetMovieFrame - cp.ResumeMovieFrame) / 15.0) + 120);
                    var suffixDeadline = DateTime.UtcNow.AddSeconds(suffixTimeoutSec);
                    while (true)
                    {
                        if (gameProcess.HasExited)
                            throw new InvalidOperationException($"Game exited during S0 {cp.CheckpointId} suffix replay.");
                        boot.Refresh();
                        if (boot.FullRunFaultCode != 0)
                            throw new InvalidOperationException($"Native fault {boot.FullRunFaultCode} during S0 {cp.CheckpointId} suffix replay.");
                        if (boot.IsWaiting) break;
                        if (DateTime.UtcNow >= suffixDeadline)
                            throw new TimeoutException($"Timed out waiting for S0 {cp.CheckpointId} suffix replay to reach {record.TargetMovieFrame}.");
                        await Task.Delay(100);
                    }

                    var endState = await VideoRuntimeState(vm);
                    if (endState["movieFrame"] != record.TargetMovieFrame.ToString(CultureInfo.InvariantCulture)
                        || endState["faultCode"] != "0"
                        || endState["mismatchCount"] != "0")
                    {
                        throw new InvalidOperationException(
                            $"S0 {cp.CheckpointId} ended at movie={endState["movieFrame"]} (expected {record.TargetMovieFrame}), fault={endState["faultCode"]}, mismatch={endState["mismatchCount"]}, error={endState.GetValueOrDefault("error", "")}");
                    }

                    record.Succeeded = true;
                    Log($"S0-ACCEPTANCE RESTORE #{record.Index} PASS id={cp.CheckpointId} resume={cp.ResumeMovieFrame} elapsedMs={sw.Elapsed.TotalMilliseconds:F0}");
                }
                catch (Exception ex)
                {
                    record.Succeeded = false;
                    record.Error = ex.Message;
                    Log($"S0-ACCEPTANCE RESTORE #{record.Index} FAIL id={cp.CheckpointId} resume={cp.ResumeMovieFrame}: {ex.Message}");
                    try { await client.CancelRestoreAsync(cp.CheckpointId, CancellationToken.None); } catch { }
                    try { boot.Refresh(); } catch { }
                    if (gameProcess.HasExited || !boot.IsWaiting || boot.FullRunFaultCode != 0 || movies.IsTerminal)
                    {
                        processUnusable = true;
                        fatalReason = ex.Message;
                    }
                }
                finally
                {
                    sw.Stop();
                    record.ElapsedMs = sw.Elapsed.TotalMilliseconds;
                }
            }
            }
            finally
            {
                setRestoreFrozen.Invoke(vm, new object[] { false });
                setGridApplying.Invoke(vm, new object[] { false });
            }

            movies.VerifyOriginalSavesUnchanged();
            Log($"S0-ACCEPTANCE COMPLETE suite={suiteName} qualified={qualified.Count} succeeded={runRecords.Count(r => r.Succeeded)}");
        }
        catch (Exception ex)
        {
            baselineStageError = ex.Message;
            Log("S0-ACCEPTANCE STAGE ERROR: " + ex);
            throw;
        }
        finally
        {
            ExportTraceAndMetadata();
        }
    }
}
