using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Core.SceneCheckpoints;
using USceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowKnightTAS.Runtime.FullRun
{
    public sealed partial class RuntimeFullRunSession
    {
        private CheckpointRestoreJob? checkpointRestore;
        private bool checkpointPreparationHooked;

        private void HookCheckpointPreparation()
        {
            if (checkpointPreparationHooked) return;
            On.GameManager.FadeSceneInWithDelay += OnCheckpointBootstrapFade;
            On.HeroController.Respawn += OnCheckpointBootstrapRespawn;
            checkpointPreparationHooked = true;
        }
        private void UnhookCheckpointPreparation()
        {
            if (!checkpointPreparationHooked) return;
            On.GameManager.FadeSceneInWithDelay -= OnCheckpointBootstrapFade;
            On.HeroController.Respawn -= OnCheckpointBootstrapRespawn;
            checkpointPreparationHooked = false;
        }
        private IEnumerator OnCheckpointBootstrapFade(On.GameManager.orig_FadeSceneInWithDelay original, GameManager self, float delay)
        {
            var routine = original(self, delay);
            var job = checkpointRestore;
            return job != null && mode == "CheckpointPreparing" && job.Phase == "LoadingInitialSave"
                ? TrackCheckpointBootstrap(job, routine, true) : routine;
        }
        private IEnumerator OnCheckpointBootstrapRespawn(On.HeroController.orig_Respawn original, HeroController self)
        {
            var routine = original(self);
            var job = checkpointRestore;
            return job != null && mode == "CheckpointPreparing" && !job.Applied
                ? TrackCheckpointBootstrap(job, routine, false) : routine;
        }
        private IEnumerator TrackCheckpointBootstrap(CheckpointRestoreJob job, IEnumerator routine, bool initialFade)
        {
            job.PendingBootstrapCoroutines++;
            var finished = false;
            try
            {
                while (true)
                {
                    bool moved;
                    try { moved = routine.MoveNext(); }
                    catch (Exception exception) { RejectCheckpointRestore(job, exception); throw; }
                    if (!moved) { finished = true; break; }
                    yield return routine.Current;
                }
            }
            finally
            {
                (routine as IDisposable)?.Dispose();
                job.PendingBootstrapCoroutines--;
                if (initialFade && finished) job.InitialFadeFinished = true;
                Modding.Logger.Log("[HKTAS] Checkpoint bootstrap coroutine finished: fade=" + initialFade
                    + " pending=" + job.PendingBootstrapCoroutines + " Movie=" + movieFrame);
            }
        }
        private Dictionary<string, string> CheckpointRestoreCommand(IReadOnlyDictionary<string, string> fields)
        {
            if (fields.Count != 3 || !fields.TryGetValue("checkpointId", out var id)) throw new InvalidDataException("Invalid checkpoint restore command fields.");
            var phase = fields["phase"];
            if (phase == "restore-cancel")
            {
                lock (checkpointSync)
                {
                    if (checkpointRestore?.Packet.Header.CheckpointId == id)
                    { checkpointRestore.Phase = "Cancelled"; checkpointRestore.Error = "Restore cancelled; retire this process."; clock.Fault(47); }
                }
                return new Dictionary<string, string> { ["requestId"] = fields["requestId"], ["checkpointId"] = id, ["status"] = "Cancelled" };
            }
            if (phase == "restore-status")
            {
                lock (checkpointSync)
                {
                    var job = checkpointRestore;
                    if (job == null || job.Packet.Header.CheckpointId != id) throw new InvalidDataException("Restore checkpoint ID differs.");
                    if (!job.Applied && job.Elapsed.ElapsedMilliseconds > 240000 && job.Phase != "Failed" && job.Phase != "Cancelled")
                    { job.Phase = "Failed"; job.Error = "Checkpoint reconstruction exceeded the four-minute deadline."; clock.Fault(47); }
                    return new Dictionary<string, string>
                    {
                        ["requestId"] = fields["requestId"], ["checkpointId"] = id, ["status"] = job.Phase,
                        ["restoreQualified"] = "false", ["stateApplied"] = job.Applied ? "true" : "false",
                        ["movieFrame"] = movieFrame.ToString(CultureInfo.InvariantCulture),
                        ["nativeFrame"] = clock.CurrentFrameIndex.ToString(CultureInfo.InvariantCulture),
                        ["firstConsumedMovieFrame"] = job.FirstConsumed.ToString(CultureInfo.InvariantCulture),
                        ["consumedMovieFrames"] = job.Consumed.ToString(CultureInfo.InvariantCulture), ["detail"] = job.Error
                    };
                }
            }
            SceneCheckpointPacket packet;
            lock (checkpointSync)
                packet = stagedCheckpoint?.Header.CheckpointId == id ? stagedCheckpoint : throw new InvalidDataException("No matching staged checkpoint.");
            // The worker never writes Unity. Native observation services the
            // transaction on the engine thread while PlayerLoop is parked.
            return observationQueue.Invoke(frame =>
            {
                checkpointSaves!.AssertActive();
                if (!clock.IsPaused || clock.IsFinished || mode == "Failed") throw new InvalidOperationException("Checkpoint restore requires a paused live replay.");
                if (phase == "restore-start")
                {
                    if (checkpointRestore != null || mode != "Replay" || !inputReady || movieFrame != 0)
                        throw new InvalidOperationException("Checkpoint preparation requires a fresh replay at Movie 0; executed history cannot substitute for restore.");
                    var sections = packet.CopySections();
                    var data = CheckpointCodec.Decode<CheckpointContinuationData>(sections["continuation"]);
                    data.Validate(packet.Header.CaptureMovieFrame, packet.Header.CaptureNativeFrame);
                    RequireCheckpointExecutionIdentity(data);
                    var transition = CheckpointCodec.Decode<CheckpointTransitionData>(sections["transition"]);
                    var player = CheckpointCodec.Decode<PlayerData>(sections["playerData"]);
                    var scene = CheckpointCodec.Decode<SceneData>(sections["sceneData"]);
                    var prefix = Sha256Utility.ComputeHex(System.Text.Encoding.UTF8.GetBytes(new MovieV2Codec().WriteCanonical(MovieV2Prefix.Take(replayMovie!, data.MovieFrame))));
                    if (prefix != data.MoviePrefixSha256 || string.IsNullOrEmpty(transition.SourceScene)
                        || transition.SourceScene == "Menu_Title" || transition.LoadInfo.IsFirstLevelForPlayer
                        || packet.Header.CaptureMovieFrame != packet.Header.ResumeMovieFrame)
                        throw new InvalidDataException("This restore prototype requires a matching Movie and an existing-game scene transition.");
                    var manager = GameManager.instance ?? throw new InvalidOperationException("Restore GameManager is absent.");
                    var profileKey = typeof(GameManager).FullName + "/profileID";
                    if (!data.ManagedWorld.GameManager.TryGetValue(profileKey, out var profileText)
                        || !profileText.StartsWith("System.Int32:", StringComparison.Ordinal)
                        || !int.TryParse(profileText.Substring(13), out var slot) || slot < 1 || slot > 4
                        || !checkpointSaves.Descriptor.OriginalFileSha256.Keys.Any(name => name.StartsWith("user" + slot + ".", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("Restore prototype needs a protected initial save slot.");
                    var job = new CheckpointRestoreJob(packet, data, transition, player, scene);
                    checkpointRestore = job; mode = "CheckpointPreparing"; frameInputEnabled = false;
                    input.SetFrameInputEnabled(false); mouse.SetFrameInputEnabled(false); input.SetNativeFrameActive(false);
                    HookCheckpointPreparation();
                    try { manager.LoadGameFromUI(slot); }
                    catch (Exception exception) { RejectCheckpointRestore(job, exception); throw; }
                    return new Dictionary<string, string> { ["requestId"] = fields["requestId"], ["checkpointId"] = id,
                        ["status"] = job.Phase, ["restoreQualified"] = "false" };
                }
                if (phase != "restore-apply") throw new InvalidDataException("Unknown checkpoint restore phase.");
                var pending = checkpointRestore;
                if (pending == null || pending.Packet.Header.CheckpointId != id || pending.Phase != "AwaitingApplyAtC" || pending.Applied)
                    throw new InvalidOperationException("Checkpoint restore has not parked at its reconstructed C.");
                try
                {
                    var boundary = LoadingSerialSceneProbe.GetCheckpointBoundary();
                    if (boundary.Item3 != pending.Transition.SourceScene
                        || !CheckpointCodec.Encode(boundary.Item2).SequenceEqual(CheckpointCodec.Encode(pending.Transition.LoadInfo)))
                        throw new InvalidDataException("Reconstructed transition differs from the checkpoint.");
                    var applyWorld = pending.Data.ManagedWorld.PrepareApply(pending.Player, pending.Scene);
                    var applyBoss = pending.Data.ManagedWorld.Boss.PrepareApply(pending.BossTargets
                        ?? throw new InvalidOperationException("Boss setup objects were not bound before source unloading."), pending.Player);
                    var applyLoad = LoadingSerialSceneProbe.PrepareCheckpointLoadState(pending.Data.SceneLoad);
                    var heroScale = HeroController.SilentInstance.transform.localScale;
                    loadingBoundaryProbe?.IncludeCheckpointK(pending.Data.MovieFrame);
                    Modding.Logger.Log("[HKTAS] Checkpoint Hero scale before apply=" + heroScale
                        + " captured=" + pending.Data.ManagedWorld.HeroLocalScale);
                    // Failure after the first live write is terminal. No cold
                    // replay fallback or continuation of a partial transaction.
                    applyWorld(); applyBoss(); applyLoad(); ApplyCheckpointContinuation(pending.Data, frame);
                    pending.Applied = true; pending.Phase = "RebuildingCToK";
                    Modding.Logger.Log("[HKTAS] Experimental checkpoint state applied: id=" + id
                        + " C=" + movieFrame + "/" + expectedNativeStart + " qualified=false");
                    return new Dictionary<string, string> { ["requestId"] = fields["requestId"], ["checkpointId"] = id,
                        ["status"] = pending.Phase, ["stateApplied"] = "true", ["restoreQualified"] = "false" };
                }
                catch (Exception exception) { RejectCheckpointRestore(pending, exception); throw; }
            });
        }
        private void ApplyCheckpointContinuation(CheckpointContinuationData data, long expectedNative)
        {
            data.Validate(data.MovieFrame, data.NativeFrame);
            var start = 0L; var index = 0;
            while (index + 1 < replayMovie!.Runs.Count && data.MovieFrame >= start + replayMovie.Runs[index].RepeatCount)
                start += replayMovie.Runs[index++].RepeatCount;
            input.ImportCheckpoint(data.Input, replayMovie); mouse.ImportCheckpoint(data.Mouse);
            CheckpointClockMethod("ImportCheckpointRandomState").Invoke(null, new object[] { data.Random, expectedNative });
            clock.ImportContinuation(data.NativeClock, expectedNative);
            movieFrame = data.MovieFrame; expectedNativeStart = data.NativeFrame; skippedLoadFrames = data.SkippedLoadingFrames;
            timingRunIndex = index; timingRunStart = start; activeFrameRate = data.ActiveFrameRate; recordingFrameRate = data.RecordingFrameRate;
            titleReadySeen = data.TitleReadySeen; firstLevelActivationPending = data.FirstLevelActivationPending;
            randomSeedSceneName = data.RandomSeedScene; randomSeedSceneHandle = -1; randomSeedHandshakePending = false;
            randomSeedRequest?.Dispose(); randomSeedAcknowledged?.Dispose(); randomSeedRequest = null; randomSeedAcknowledged = null;
            lastGameUpdateNativeFrame = data.LastGameUpdateNativeFrame; gameUpdateDeltaSeconds = data.GameUpdateDeltaSeconds;
            gameUpdateTimeSeconds = data.GameUpdateTimeSeconds; executedMovieSeconds = data.ExecutedMovieSeconds;
            frameInputEnabled = false; frameBoundary = "CheckpointC"; pauseAtMovieFrame = -1; mode = "Replay";
        }
        private bool RunCheckpointPreparationFrame(long nativeFrame)
        {
            var job = checkpointRestore;
            if (job == null || mode != "CheckpointPreparing") return false;
            try
            {
                if (job.Elapsed.ElapsedMilliseconds > 240000) throw new TimeoutException("Checkpoint reconstruction exceeded the four-minute deadline.");
                clock.SetFrameRate(50); input.SetNativeFrameActive(false); frameInputEnabled = false;
                var observed = LoadingSerialSceneProbe.ObserveNativeBoundary(0, nativeFrame);
                if (job.Phase == "AwaitingTargetC" && observed)
                {
                    var boundary = LoadingSerialSceneProbe.GetCheckpointBoundary();
                    if (boundary.Item3 != job.Transition.SourceScene || boundary.Item2.SceneName != job.Transition.LoadInfo.SceneName)
                        throw new InvalidDataException("Restore reached another scene boundary.");
                    job.Phase = "AwaitingApplyAtC"; clock.RequestPause(); return true;
                }
                var manager = GameManager.instance;
                if (manager == null || manager.IsInSceneTransition || firstLevelActivationPending) return true;
                var hero = HeroController.SilentInstance;
                if (hero == null || !hero.gameObject.activeInHierarchy || manager.gameState != GlobalEnums.GameState.PLAYING) return true;
                if (job.Phase == "LoadingInitialSave")
                {
                    // PLAYING is set before the initial delayed fade and Hero
                    // respawn finish. Let their original continuations end;
                    // otherwise they can mutate the reconstructed C after apply.
                    if (!job.InitialFadeFinished || job.PendingBootstrapCoroutines != 0
                        || GameCameras.instance.cameraFadeFSM.ActiveStateName != "Normal") return true;
                    if (USceneManager.GetActiveScene().name == job.Transition.SourceScene) job.Phase = "SourceReady";
                    else
                    {
                        job.Phase = "LoadingSourceScene";
                        manager.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = job.Transition.SourceScene,
                            EntryGateName = "", IsFirstLevelForPlayer = false });
                        return true;
                    }
                }
                if (job.Phase == "LoadingSourceScene" && USceneManager.GetActiveScene().name == job.Transition.SourceScene
                    && job.PendingBootstrapCoroutines == 0) job.Phase = "SourceReady";
                if (job.Phase == "SourceReady")
                { job.BossTargets = job.Data.ManagedWorld.Boss.BindBeforeUnload(); job.Phase = "AwaitingTargetC"; manager.BeginSceneTransition(job.Transition.LoadInfo); }
            }
            catch (Exception exception) { RejectCheckpointRestore(job, exception); }
            return true;
        }
        private bool ParkCheckpointAtK(long nativeFrame)
        {
            var job = checkpointRestore;
            if (job == null || job.Phase != "RebuildingCToK") return false;
            if (nativeFrame != job.Packet.Header.ResumeNativeFrame || movieFrame != job.Packet.Header.ResumeMovieFrame)
            { RejectCheckpointRestore(job, new InvalidDataException("Restored K Movie/native boundary differs from the capture.")); return true; }
            job.Phase = "PausedAtK"; frameInputEnabled = false; input.SetNativeFrameActive(false); clock.RequestPause();
            return true;
        }
        private void RecordCheckpointInputConsumption(long frame)
        {
            var job = checkpointRestore;
            if (job == null || !job.Applied) return;
            if (job.FirstConsumed < 0)
            {
                if (frame != job.Packet.Header.ResumeMovieFrame) throw new InvalidDataException("First checkpoint Movie input differs from K.");
                job.FirstConsumed = frame; job.Phase = "ReplayingSuffix";
                Modding.Logger.Log("[HKTAS] Checkpoint first Movie input: id=" + job.Packet.Header.CheckpointId + " frame=" + frame);
            }
            if (frame != job.FirstConsumed + job.Consumed) throw new InvalidDataException("Checkpoint suffix input was not sequential.");
            job.Consumed++;
        }
        private void RejectCheckpointRestore(CheckpointRestoreJob job, Exception exception)
        {
            job.Error = exception.GetBaseException().Message; job.Phase = "Failed";
            Fail("CheckpointRestoreFault: " + job.Error);
        }
        private sealed class CheckpointRestoreJob
        {
            internal CheckpointRestoreJob(SceneCheckpointPacket packet, CheckpointContinuationData data, CheckpointTransitionData transition, PlayerData player, SceneData scene)
            { Packet = packet; Data = data; Transition = transition; Player = player; Scene = scene; }
            internal readonly SceneCheckpointPacket Packet;
            internal readonly CheckpointContinuationData Data;
            internal readonly CheckpointTransitionData Transition;
            internal readonly PlayerData Player;
            internal readonly SceneData Scene;
            internal readonly Stopwatch Elapsed = Stopwatch.StartNew();
            internal volatile string Phase = "LoadingInitialSave", Error = "";
            internal volatile bool Applied;
            internal int PendingBootstrapCoroutines;
            internal bool InitialFadeFinished;
            internal long FirstConsumed = -1, Consumed;
            internal CheckpointBossContext.Targets? BossTargets;
        }
    }
}
