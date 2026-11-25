using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using GlobalEnums;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Core.SceneCheckpoints;
using HollowKnightTAS.Runtime.Rng;
using Newtonsoft.Json;

namespace HollowKnightTAS.Runtime.FullRun
{
    public sealed partial class RuntimeFullRunSession
    {
        private const int LeanS0ChunkBytes = 262144;

        private bool leanS0RandomSeededForTargetScene;
        private string leanS0SeededTargetSceneName = string.Empty;
        private LeanS0GateReceipt? lastLeanS0GateReceipt;
        private bool leanS0SourceParked;
        private bool leanS0TargetArmed;
        private bool leanS0TargetParked;
        private bool leanS0TargetInProcess;
        private string leanS0ExpectedTargetScene = string.Empty;
        private long leanS0ParkedNativeFrame = -1;
        private int leanS0ParkedOrdinal;
        private string leanS0ParkedSourceScene = string.Empty;
        private string leanS0ParkedTargetScene = string.Empty;
        private GameManager.SceneLoadInfo? leanS0ParkedLoadInfo;

        private byte[]? leanS0ExportedBytes;
        private string leanS0ExportedHash = string.Empty;
        private LeanS0Packet? leanS0ExportedPacket;
        private long leanS0CaptureElapsedMs;
        private long leanS0RestoreElapsedMs;

        private byte[]? leanS0UploadBuffer;
        private int leanS0UploadOffset;
        private string leanS0UploadExpectedHash = string.Empty;
        private LeanS0Packet? leanS0TargetPacket;
        private LeanS0TargetJob? leanS0TargetJob;
        private long leanS0TargetResumeNativeFrame = -1;
        private LeanS0K3TraceWriter? leanS0K3Trace;
        private long leanS0K3StartMovie = -1;
        private long leanS0K3EndMovie = -1;
        private string leanS0K3Role = string.Empty;

        private sealed class LeanS0TargetJob
        {
            internal string Phase = "LoadingInitialSave";
            internal string Error = string.Empty;
            internal long LastNative;
            internal int PendingBootstrap;
            internal bool FadeFinished;
            internal bool HooksAttached;
            internal readonly Stopwatch Elapsed = Stopwatch.StartNew();

            internal LeanS0TargetJob(long lastNative)
            {
                LastNative = lastNative;
            }
        }

        internal bool TryEnterLeanS0Boundary(
            int ordinal,
            string sourceScene,
            string targetScene,
            GameManager.SceneLoadInfo? loadInfo,
            long movie,
            long native,
            out bool parkedForTransfer)
        {
            parkedForTransfer = false;
            bool serialProfile = NativeExecutionProfile.UsesSerialScenes(NativeExecutionProfile.SelectedProfileId);
            if (!serialProfile && !LeanS0Mode.Enabled)
            {
                return false;
            }

            if (leanS0TargetArmed && !leanS0TargetParked)
            {
                if (!string.Equals(targetScene, leanS0ExpectedTargetScene, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"LeanS0 target boundary mismatch: expected '{leanS0ExpectedTargetScene}', got '{targetScene}'.");
                }

                leanS0TargetArmed = false;
                leanS0TargetParked = true;
                leanS0ParkedNativeFrame = native;
                leanS0ParkedOrdinal = ordinal;
                leanS0ParkedSourceScene = sourceScene;
                leanS0ParkedTargetScene = targetScene;
                leanS0ParkedLoadInfo = loadInfo;
                DetachLeanS0TargetHooks();
                if (leanS0TargetJob != null)
                {
                    leanS0TargetJob.Phase = "PausedAtTargetS0";
                }

                input.SetNativeFrameActive(false);
                mode = "LeanS0TargetPaused";
                clock.RequestPause();
                Modding.Logger.Log(
                    $"[HKTAS] LeanS0 target parked at S0 before scene load: ordinal={ordinal};source={sourceScene};target={targetScene};movie={movie};native={native}");
                parkedForTransfer = true;
                return true;
            }

            string entryGateName = loadInfo?.EntryGateName ?? string.Empty;
            lastLeanS0GateReceipt = LeanS0Gate.Execute(
                ordinal,
                targetScene,
                entryGateName,
                applyUnityRuntime: true,
                rngDigestReader: ReadCurrentRngSha256Safe,
                boundaryEnvironmentReset: LeanS0EnvironmentParity.ResetBoundaryEnvironment);
            leanS0RandomSeededForTargetScene = true;
            leanS0SeededTargetSceneName = targetScene;

            Modding.Logger.Log(
                $"[HKTAS] LeanS0Gate applied at S0: ordinal={ordinal};source={sourceScene};target={targetScene};gate={entryGateName};seed={lastLeanS0GateReceipt.Seed};rng={lastLeanS0GateReceipt.RngSha256}");
            MaybeStartAcceptanceRngDiag(targetScene, "s0-boundary", ordinal, native);

            if (LeanS0Mode.IsSourceRole && !leanS0SourceParked && ordinal == LeanS0Mode.SelectedCaptureOrdinal)
            {
                leanS0SourceParked = true;
                leanS0ParkedNativeFrame = native;
                leanS0ParkedOrdinal = ordinal;
                leanS0ParkedSourceScene = sourceScene;
                leanS0ParkedTargetScene = targetScene;
                leanS0ParkedLoadInfo = loadInfo;
                CaptureLeanS0SourceAtBoundary(ordinal, sourceScene, targetScene, loadInfo, movie, native);
                parkedForTransfer = true;
                return true;
            }

            return true;
        }

        private bool ShouldPauseBeforeNextNativeFrameLeanS0(long completed)
        {
            return (leanS0SourceParked && mode == "LeanS0SourcePaused")
                || (leanS0TargetParked && mode == "LeanS0TargetPaused");
        }

        private bool ShouldRequestExternalRandomSeedHandshake(int sceneHandle, string sceneName, bool ready)
        {
            return LeanS0RandomSeedSync.ShouldRequestExternalHandshake(
                LeanS0Mode.Enabled,
                ready,
                sceneHandle,
                sceneName,
                leanS0SeededTargetSceneName,
                ref randomSeedSceneHandle,
                ref randomSeedSceneName,
                ref randomSeedHandshakePending,
                ref leanS0RandomSeededForTargetScene);
        }

        private string ReadCurrentRngSha256Safe()
        {
            try
            {
                var codec = bossTraceRngCodec ?? UnityRandomStateCodec_1_5_78_11833.Resolve().Codec!;
                bossTraceRngCodec = codec;
                return codec.CaptureCurrent().Sha256;
            }
            catch
            {
                return string.Empty;
            }
        }

        private LeanS0CaptureContext BuildLeanS0CaptureContext(
            int ordinal,
            string sourceScene,
            string targetScene,
            GameManager.SceneLoadInfo? loadInfo,
            long native)
        {
            MovieV2Document sourceMovie = mode == "Recording"
                ? journal!.Freeze(recordingHeader!, movieFrame)
                : replayMovie ?? throw new InvalidOperationException("Source replay Movie is absent at S0.");

            string prefixCanonical = new MovieV2Codec().WriteCanonical(MovieV2Prefix.Take(sourceMovie, movieFrame));
            string prefixSha256 = Sha256Utility.ComputeHex(Encoding.UTF8.GetBytes(prefixCanonical));
            var receipt = lastLeanS0GateReceipt ?? throw new InvalidOperationException("LeanS0Gate receipt is missing at capture.");

            return new LeanS0CaptureContext
            {
                Ordinal = ordinal,
                SourceSceneName = sourceScene,
                TargetSceneName = targetScene,
                LoadInfo = loadInfo,
                RngSeed = receipt.Seed,
                RngSha256 = receipt.RngSha256,
                Movie = sourceMovie,
                MovieId = new MovieV2Codec().ComputeMovieId(sourceMovie),
                MovieFrame = movieFrame,
                NativeFrame = native,
                ConsumedSamples = input.ConsumedSamples,
                SkippedLoadingFrames = skippedLoadFrames,
                TimingRunIndex = timingRunIndex,
                TimingRunStart = timingRunStart,
                ActiveFrameRate = activeFrameRate,
                RecordingFrameRate = recordingFrameRate,
                TitleReadySeen = titleReadySeen,
                FirstLevelActivationPending = firstLevelActivationPending,
                RandomSeedScene = randomSeedSceneName,
                LastGameUpdateNativeFrame = lastGameUpdateNativeFrame,
                GameUpdateDeltaSeconds = gameUpdateDeltaSeconds,
                GameUpdateTimeSeconds = gameUpdateTimeSeconds,
                ExecutedMovieSeconds = executedMovieSeconds,
                MovieInputNativeOrigin = movieInputNativeOrigin ?? throw new InvalidOperationException("movieInputNativeOrigin is null."),
                MovieInputControlOrigin = movieInputControlOrigin,
                Clock = clock,
                Input = input,
                Mouse = mouse,
                ExportRandomBytes = () => (byte[])CheckpointClockMethod("ExportCheckpointRandomState").Invoke(null, null)!,
                ExportClockObserverBytes = () => (byte[])CheckpointClockMethod("ExportCheckpointClockObserver").Invoke(null, null)!,
                MoviePrefixSha256 = prefixSha256
            };
        }

        private void CaptureLeanS0SourceAtBoundary(
            int ordinal,
            string sourceScene,
            string targetScene,
            GameManager.SceneLoadInfo? loadInfo,
            long movie,
            long native)
        {
            var sw = Stopwatch.StartNew();
            LeanS0CaptureContext ctx = BuildLeanS0CaptureContext(ordinal, sourceScene, targetScene, loadInfo, native);
            var receipt = lastLeanS0GateReceipt!;

            input.SetNativeFrameActive(false);
            LeanS0Packet packet = LeanS0SnapshotRuntime.Capture(ctx);
            byte[] encoded = LeanS0PacketCodec.Encode(packet);
            sw.Stop();

            leanS0ExportedPacket = packet;
            leanS0ExportedBytes = encoded;
            leanS0ExportedHash = Sha256Utility.ComputeHex(encoded);
            leanS0CaptureElapsedMs = sw.ElapsedMilliseconds;

            string? outDir = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT");
            if (!string.IsNullOrEmpty(outDir) && Directory.Exists(outDir))
            {
                File.WriteAllBytes(Path.Combine(outDir, "lean-s0-packet.bin"), encoded);
                File.WriteAllText(
                    Path.Combine(outDir, "lean-s0-capture-summary.json"),
                    JsonConvert.SerializeObject(new
                    {
                        schema = packet.Schema,
                        ordinal,
                        sourceScene,
                        targetScene,
                        entryGate = packet.EntryGateName,
                        movieFrame = packet.C12_ControlPlane.MovieFrame,
                        nativeFrame = packet.C12_ControlPlane.NativeFrame,
                        consumedSamples = packet.C12_ControlPlane.ConsumedSamples,
                        skippedLoadingFrames = packet.C12_ControlPlane.SkippedLoadingFrames,
                        rngSeed = packet.RngSeed,
                        rngSha256 = packet.RngSha256,
                        packetBytes = encoded.Length,
                        packetSha256 = leanS0ExportedHash,
                        captureElapsedMs = leanS0CaptureElapsedMs,
                        gateReceipt = receipt
                    }, Formatting.Indented),
                    new UTF8Encoding(false));
            }

            mode = "LeanS0SourcePaused";
            clock.RequestPause();
            Modding.Logger.Log(
                $"[HKTAS] LeanS0 source captured & parked: ordinal={ordinal};movie={movieFrame};native={native};bytes={encoded.Length};elapsedMs={leanS0CaptureElapsedMs};sha256={leanS0ExportedHash}");
        }

        private bool RunLeanS0TargetLoadingFrame(long native)
        {
            var job = leanS0TargetJob;
            if (job == null || mode != "LeanS0TargetLoading")
            {
                return false;
            }

            try
            {
                if (job.Elapsed.ElapsedMilliseconds > 240000)
                {
                    throw new TimeoutException("LeanS0 target bootstrap exceeded four minutes.");
                }
                if (native < job.LastNative || native > job.LastNative + 1)
                {
                    throw new InvalidDataException("LeanS0 target loading native cursor was not sequential.");
                }
                job.LastNative = native;

                clock.SetFrameRate(50);
                input.SetNativeFrameActive(true);

                if (LoadingSerialSceneProbe.ObserveNativeBoundary(this, 0, native))
                {
                    if (leanS0TargetParked)
                    {
                        return true;
                    }
                    LoadingSerialSceneProbe.MarkLeanS0BoundaryObserved();
                    return true;
                }

                if (job.Phase != "LoadingInitialSave")
                {
                    return true;
                }

                if (leanS0TargetInProcess)
                {
                    var inProcPacket = leanS0TargetPacket ?? throw new InvalidOperationException("LeanS0 target packet is absent.");
                    GameManager inProcManager = GameManager.instance ?? throw new InvalidOperationException("GameManager is null.");
                    job.Phase = "AwaitingTargetS0";
                    leanS0TargetArmed = true;
                    leanS0ExpectedTargetScene = inProcPacket.TargetSceneName;

                    GameManager.SceneLoadInfo inProcLoadInfo = LeanS0SnapshotRuntime.ToGameSceneLoadInfo(inProcPacket.C6_TransitionInfo);
                    inProcLoadInfo.WaitForSceneTransitionCameraFade = false;
                    inProcLoadInfo.PreventCameraFadeOut = true;
                    // Clear stale target Godhome transition flags before BeginSceneTransition so they
                    // cannot override C6_TransitionInfo.AlwaysUnloadUnusedAssets prior to target S0.
                    BossStatueLoadManager.Clear();
                    if (BossSequenceController.IsInSequence)
                    {
                        BossSequenceController.Reset();
                    }
                    Modding.Logger.Log(
                        $"[HKTAS] LeanS0 in-process target starting transition to {inProcLoadInfo.SceneName} (gate={inProcLoadInfo.EntryGateName}, vis={inProcLoadInfo.Visualization})");
                    inProcManager.BeginSceneTransition(inProcLoadInfo);
                    return true;
                }

                GameManager? manager = GameManager.instance;
                HeroController? hero = HeroController.SilentInstance;
                if (manager == null || manager.IsInSceneTransition || firstLevelActivationPending || hero == null
                    || !hero.gameObject.activeInHierarchy || manager.gameState != GameState.PLAYING
                    || !job.FadeFinished || job.PendingBootstrap != 0
                    || GameCameras.instance == null || GameCameras.instance.cameraFadeFSM.ActiveStateName != "Normal")
                {
                    return true;
                }

                if (!CanonicalOriginalAudioBootstrapObservation.Complete(manager, out _))
                {
                    return true;
                }

                var packet = leanS0TargetPacket ?? throw new InvalidOperationException("LeanS0 target packet is absent.");
                job.Phase = "AwaitingTargetS0";
                leanS0TargetArmed = true;
                leanS0ExpectedTargetScene = packet.TargetSceneName;

                GameManager.SceneLoadInfo loadInfo = LeanS0SnapshotRuntime.ToGameSceneLoadInfo(packet.C6_TransitionInfo);
                Modding.Logger.Log(
                    $"[HKTAS] LeanS0 target starting transition to {loadInfo.SceneName} (gate={loadInfo.EntryGateName}, vis={loadInfo.Visualization})");
                manager.BeginSceneTransition(loadInfo);
                return true;
            }
            catch (Exception error)
            {
                job.Phase = "Failed";
                job.Error = error.ToString();
                DetachLeanS0TargetHooks();
                Fail("LeanS0TargetLoadingFault: " + error.Message);
                return true;
            }
        }

        private void AttachLeanS0TargetHooks()
        {
            if (leanS0TargetJob == null || leanS0TargetJob.HooksAttached) return;
            leanS0TargetJob.HooksAttached = true;
            On.GameManager.FadeSceneInWithDelay += OnLeanS0TargetBootstrapFade;
            On.HeroController.Respawn += OnLeanS0TargetBootstrapRespawn;
        }

        private void DetachLeanS0TargetHooks()
        {
            if (leanS0TargetJob == null || !leanS0TargetJob.HooksAttached) return;
            leanS0TargetJob.HooksAttached = false;
            On.GameManager.FadeSceneInWithDelay -= OnLeanS0TargetBootstrapFade;
            On.HeroController.Respawn -= OnLeanS0TargetBootstrapRespawn;
        }

        private IEnumerator OnLeanS0TargetBootstrapFade(On.GameManager.orig_FadeSceneInWithDelay original, GameManager self, float delay)
            => TrackLeanS0TargetBootstrap(original(self, delay), fade: true);

        private IEnumerator OnLeanS0TargetBootstrapRespawn(On.HeroController.orig_Respawn original, HeroController self)
            => TrackLeanS0TargetBootstrap(original(self), fade: false);

        private IEnumerator TrackLeanS0TargetBootstrap(IEnumerator routine, bool fade)
        {
            var job = leanS0TargetJob;
            if (job == null)
            {
                yield break;
            }
            job.PendingBootstrap++;
            bool complete = false;
            try
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = routine.MoveNext();
                    }
                    catch (Exception error)
                    {
                        job.Phase = "Failed";
                        job.Error = error.ToString();
                        throw;
                    }
                    if (!moved)
                    {
                        complete = true;
                        break;
                    }
                    yield return routine.Current;
                }
            }
            finally
            {
                (routine as IDisposable)?.Dispose();
                job.PendingBootstrap--;
                if (fade && complete)
                {
                    job.FadeFinished = true;
                }
            }
        }

        private static int ExtractSaveSlotFromPlayerDataJson(string playerDataJson, bool allowUnassignedNewGameSlot = false)
        {
            if (string.IsNullOrWhiteSpace(playerDataJson))
            {
                throw new InvalidDataException("LeanS0 PlayerData JSON is missing.");
            }
            var parsed = JsonConvert.DeserializeObject<Dictionary<string, object>>(playerDataJson);
            if (parsed == null || !parsed.TryGetValue("profileID", out var rawSlot) || rawSlot == null)
            {
                throw new InvalidDataException("LeanS0 PlayerData JSON is missing profileID.");
            }
            int slot = Convert.ToInt32(rawSlot, CultureInfo.InvariantCulture);
            if (slot >= 1 && slot <= 4)
            {
                return slot;
            }
            if (slot == 0)
            {
                if (allowUnassignedNewGameSlot)
                {
                    return 0;
                }
                throw new InvalidDataException(
                    "LeanS0 profileID is 0 (new-game checkpoint requires an in-game process; falling back to cold replay).");
            }
            throw new InvalidDataException("LeanS0 profileID is outside valid save slots 1..4: " + slot);
        }

        private static readonly System.Reflection.FieldInfo[] UiMenuScreenFields = typeof(UIManager)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Where(field => field.FieldType == typeof(MenuScreen))
            .ToArray();
        private static readonly System.Reflection.FieldInfo? UiIsFadingMenuField = typeof(UIManager).GetField("isFadingMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        private static readonly System.Reflection.FieldInfo? UiIgnoreUnpauseField = typeof(UIManager).GetField("ignoreUnpause", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        private static readonly System.Reflection.FieldInfo? UiMenuAnimationCounterField = typeof(UIManager).GetField("menuAnimationCounter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        private static readonly System.Reflection.FieldInfo? UiGoToPauseMenuCoField = typeof(UIManager).GetField("goToPauseMenuCo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        private static readonly System.Reflection.FieldInfo? UiTogglePauseCoField = typeof(UIManager).GetField("togglePauseCo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        private static readonly System.Reflection.FieldInfo? UiLeavePauseMenuCoField = typeof(UIManager).GetField("leavePauseMenuCo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        private bool CanDirectTransitionForInProcessLeanS0()
        {
            GameManager? currentGm = GameManager.instance;
            HeroController? currentHero = HeroController.SilentInstance;
            return currentGm != null
                && (currentGm.gameState == GameState.PLAYING || currentGm.gameState == GameState.PAUSED)
                && !currentGm.IsInSceneTransition
                && !firstLevelActivationPending
                && currentHero != null
                && currentHero.gameObject.activeInHierarchy;
        }

        private void BeginLeanS0TargetSaveLoad(LeanS0Packet packet, long frame, int slot)
        {
            GameManager? currentGm = GameManager.instance;
            bool canDirectTransition = CanDirectTransitionForInProcessLeanS0();
            bool hasDiskSave = false;

            if (!canDirectTransition)
            {
                hasDiskSave = slot >= 1 && slot <= 4
                    && (checkpointSaves == null || File.Exists(checkpointSaves.GetSlotPath(slot, ".dat")));
            }
            leanS0TargetInProcess = canDirectTransition;
            leanS0TargetPacket = packet;
            leanS0TargetResumeNativeFrame = -1;
            leanS0TargetJob = new LeanS0TargetJob(frame);
            mode = "LeanS0TargetLoading";
            input.SetNativeFrameActive(false);
            frameInputEnabled = false;
            input.SetFrameInputEnabled(false);
            mouse.SetFrameInputEnabled(false);
            if (canDirectTransition)
            {
                if (currentGm != null
                    && (currentGm.gameState == GameState.PAUSED
                        || currentGm.isPaused
                        || (currentGm.ui != null && currentGm.ui.uiState == UIState.PAUSED)))
                {
                    ClearInProcessPauseStateBeforeDirectTransition(currentGm);
                }
                LoadingAudioLifetimePolicy.ResetForInProcessRestore();
                UnityEngine.Time.timeScale = 1f;
            }
            else if (hasDiskSave)
            {
                AttachLeanS0TargetHooks();
                (currentGm ?? throw new InvalidOperationException("GameManager is null.")).LoadGameFromUI(slot);
            }
            else
            {
                BeginInMemoryContinueGameBootstrap(
                    currentGm ?? throw new InvalidOperationException("GameManager is null."),
                    packet,
                    slot);
            }
        }

        private static void ClearInProcessPauseStateBeforeDirectTransition(GameManager currentGm)
        {
            GameCameras.instance?.ResumeCameraShake();
            currentGm.actorSnapshotUnpaused?.TransitionTo(0f);
            currentGm.isPaused = false;
            currentGm.SetState(GameState.PLAYING);
            TimeController.GenericTimeScale = 1f;
            UnityEngine.Time.timeScale = 1f;

            UIManager? ui = currentGm.ui;
            if (ui != null)
            {
                ui.StopAllCoroutines();
                UiIsFadingMenuField?.SetValue(ui, false);
                UiIgnoreUnpauseField?.SetValue(ui, false);
                UiMenuAnimationCounterField?.SetValue(ui, 0);
                UiGoToPauseMenuCoField?.SetValue(ui, null);
                UiTogglePauseCoField?.SetValue(ui, null);
                UiLeavePauseMenuCoField?.SetValue(ui, null);

                foreach (System.Reflection.FieldInfo field in UiMenuScreenFields)
                {
                    if (field.GetValue(ui) is MenuScreen screen
                        && screen != null
                        && screen.gameObject != null
                        && screen.gameObject.activeInHierarchy
                        && screen.screenCanvasGroup != null)
                    {
                        ui.HideMenuInstant(screen);
                    }
                }

                if (ui.modalDimmer != null)
                {
                    ui.HideCanvasGroup(ui.modalDimmer);
                }

                ui.uiState = UIState.PLAYING;
                ui.menuState = MainMenuState.MAIN_MENU;
                ui.AudioGoToGameplay(0f);
            }

            MenuButtonList.ClearAllLastSelected();
            HeroController.SilentInstance?.UnPause();
            currentGm.inputHandler?.AllowPause();
        }

        private void BeginInMemoryContinueGameBootstrap(GameManager gm, LeanS0Packet packet, int packetSlot)
        {
            PlayerData.instance.Reset();
            SceneData.instance.Reset();
            gm.playerData = PlayerData.instance;
            gm.sceneData = SceneData.instance;
            int memSlot = (packet.GameManagerProfileId >= 1 && packet.GameManagerProfileId <= 4)
                ? packet.GameManagerProfileId
                : 1;
            gm.profileID = memSlot;
            gm.inputHandler?.RefreshPlayerData();
            AttachLeanS0TargetHooks();
            gm.ui?.ContinueGame();
            gm.ContinueGame();
            Modding.Logger.Log(
                $"[HKTAS] LeanS0 target using in-memory ContinueGame bootstrap: packetSlot={packetSlot};gmProfile={memSlot}");
        }

        private LeanS0GateReceipt ApplyLeanS0PacketAtTargetS0(LeanS0Packet packet, long currentNativeFrame)
        {
            LeanS0SnapshotRuntime.ApplyAtTargetS0(
                packet,
                currentNativeFrame,
                clock,
                input,
                mouse,
                replayMovie ?? throw new InvalidOperationException("Target replayMovie is null."),
                (bytes, cursor) => CheckpointClockMethod("ImportCheckpointRandomState").Invoke(null, new object[] { bytes, cursor }),
                (bytes, cursor) => CheckpointClockMethod("ImportCheckpointClockObserver").Invoke(null, new object[] { bytes, cursor }),
                out CanonicalInputEpoch restoredOrigin);

            LeanS0EnvironmentParity.ResetInControlPersistentTicks(InControl.InputManager.CurrentTick);

            // Execute the identical S0Gate after restoring state so target & source share the exact same S0 invariant.
            LeanS0GateReceipt targetReceipt = LeanS0Gate.Execute(
                packet.BoundaryOrdinal,
                packet.TargetSceneName,
                packet.EntryGateName,
                applyUnityRuntime: true,
                rngDigestReader: ReadCurrentRngSha256Safe,
                boundaryEnvironmentReset: LeanS0EnvironmentParity.ResetBoundaryEnvironment);

            if (targetReceipt.Seed != packet.RngSeed
                || !string.Equals(targetReceipt.RngSha256, packet.RngSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"LeanS0 target RNG seed/digest mismatch after S0Gate: source=({packet.RngSeed},{packet.RngSha256}) target=({targetReceipt.Seed},{targetReceipt.RngSha256}).");
            }

            var c12 = packet.C12_ControlPlane;
            movieFrame = c12.MovieFrame;
            skippedLoadFrames = c12.SkippedLoadingFrames;
            expectedNativeStart = c12.NativeFrame;
            timingRunIndex = c12.TimingRunIndex;
            timingRunStart = c12.TimingRunStart;
            activeFrameRate = c12.ActiveFrameRate;
            recordingFrameRate = c12.RecordingFrameRate;
            titleReadySeen = c12.TitleReadySeen;
            firstLevelActivationPending = c12.FirstLevelActivationPending;
            randomSeedSceneName = c12.RandomSeedScene;
            randomSeedSceneHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            leanS0RandomSeededForTargetScene = true;
            leanS0SeededTargetSceneName = packet.TargetSceneName ?? string.Empty;
            lastGameUpdateNativeFrame = c12.LastGameUpdateNativeFrame;
            gameUpdateDeltaSeconds = c12.GameUpdateDeltaSeconds;
            gameUpdateTimeSeconds = c12.GameUpdateTimeSeconds;
            executedMovieSeconds = c12.ExecutedMovieSeconds;
            movieInputNativeOrigin = c12.MovieInputNativeOrigin;
            movieInputControlOrigin = restoredOrigin;
            frameInputEnabled = false;
            returningToMainMenu = false;
            randomSeedHandshakePending = false;
            bossSceneEntered = false;
            bossDeathObserved = false;
            bossesDeadObserved = false;
            bossSceneCompleteObserved = false;
            bossDeathFrame = -1;
            bossSceneEntryMovieFrame = -1;
            UnbindBoss();
            leanS0TargetInProcess = false;
            lastLeanS0GateReceipt = targetReceipt;
            leanS0ParkedOrdinal = packet.BoundaryOrdinal;
            leanS0TargetParked = false;
            LoadingSerialSceneProbe.SetLeanS0Ordinal(packet.BoundaryOrdinal);
            infoRng.SyncBaseline();
            MaybeStartAcceptanceRngDiag(packet.TargetSceneName, "s0-restore-apply", packet.BoundaryOrdinal, currentNativeFrame);
            return targetReceipt;
        }

        private Dictionary<string, string> LeanS0Command(IReadOnlyDictionary<string, string> fields)
        {
            if (!LeanS0Mode.Enabled)
            {
                throw new InvalidOperationException("LeanS0 mode is disabled (set HKTAS_LEAN_S0=1).");
            }
            if (!fields.TryGetValue("phase", out var phase) || !fields.TryGetValue("requestId", out var requestId))
            {
                throw new InvalidDataException("LeanS0 command requires phase and requestId.");
            }

            string Get(string key) => fields.TryGetValue(key, out var val)
                ? val
                : throw new InvalidDataException("Missing LeanS0 command field: " + key);

            int timeoutMs = phase == "lean-s0-target-apply-and-resume" ? 120000 : 30000;
            return observationQueue.Invoke(frame =>
            {
                var reply = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["requestId"] = requestId,
                    ["schema"] = LeanS0PacketCodec.SchemaVersion
                };

                switch (phase)
                {
                    case "lean-s0-source-info":
                        if (mode != "LeanS0SourcePaused" || leanS0ExportedBytes == null || leanS0ExportedPacket == null)
                        {
                            throw new InvalidOperationException("LeanS0 source is not parked at S0.");
                        }
                        reply["status"] = "LeanS0SourcePaused";
                        reply["length"] = leanS0ExportedBytes.Length.ToString(CultureInfo.InvariantCulture);
                        reply["sha256"] = leanS0ExportedHash;
                        reply["ordinal"] = leanS0ExportedPacket.BoundaryOrdinal.ToString(CultureInfo.InvariantCulture);
                        reply["sourceScene"] = leanS0ExportedPacket.SourceSceneName;
                        reply["targetScene"] = leanS0ExportedPacket.TargetSceneName;
                        reply["entryGate"] = leanS0ExportedPacket.EntryGateName;
                        reply["movieFrame"] = leanS0ExportedPacket.C12_ControlPlane.MovieFrame.ToString(CultureInfo.InvariantCulture);
                        reply["nativeFrame"] = leanS0ExportedPacket.C12_ControlPlane.NativeFrame.ToString(CultureInfo.InvariantCulture);
                        reply["consumedSamples"] = leanS0ExportedPacket.C12_ControlPlane.ConsumedSamples.ToString(CultureInfo.InvariantCulture);
                        reply["rngSeed"] = leanS0ExportedPacket.RngSeed.ToString(CultureInfo.InvariantCulture);
                        reply["rngSha256"] = leanS0ExportedPacket.RngSha256;
                        reply["captureElapsedMs"] = leanS0CaptureElapsedMs.ToString(CultureInfo.InvariantCulture);
                        break;

                    case "lean-s0-source-chunk":
                    {
                        if (mode != "LeanS0SourcePaused" || leanS0ExportedBytes == null)
                        {
                            throw new InvalidOperationException("LeanS0 source packet is unavailable.");
                        }
                        int offset = int.Parse(Get("offset"), NumberStyles.None, CultureInfo.InvariantCulture);
                        if (offset < 0 || offset >= leanS0ExportedBytes.Length)
                        {
                            throw new ArgumentOutOfRangeException(nameof(offset));
                        }
                        int len = Math.Min(LeanS0ChunkBytes, leanS0ExportedBytes.Length - offset);
                        byte[] chunk = new byte[len];
                        Buffer.BlockCopy(leanS0ExportedBytes, offset, chunk, 0, len);
                        reply["data"] = Convert.ToBase64String(chunk);
                        reply["offset"] = offset.ToString(CultureInfo.InvariantCulture);
                        reply["length"] = len.ToString(CultureInfo.InvariantCulture);
                        break;
                    }

                    case "lean-s0-source-resume":
                    {
                        if (mode != "LeanS0SourcePaused" || !clock.IsPaused || leanS0ExportedPacket == null)
                        {
                            throw new InvalidOperationException("LeanS0 source is not parked for suffix resume.");
                        }
                        long endMovie = ParseLeanS0EndMovie(Get("endMovie"));
                        OpenLeanS0K3Trace(
                            "source",
                            leanS0ExportedPacket.BoundaryOrdinal,
                            leanS0ExportedPacket.SourceSceneName,
                            leanS0ExportedPacket.TargetSceneName,
                            leanS0ExportedPacket.EntryGateName,
                            movieFrame,
                            endMovie,
                            leanS0ExportedBytes!.Length,
                            leanS0ExportedHash);
                        SetPauseTarget(endMovie, clock.CurrentFrameIndex);
                        LoadingSerialSceneProbe.MarkLeanS0BoundaryObserved();
                        mode = "Replay";
                        reply["status"] = "ResumingLeanS0SourceSuffix";
                        reply["startMovie"] = movieFrame.ToString(CultureInfo.InvariantCulture);
                        reply["endMovie"] = endMovie.ToString(CultureInfo.InvariantCulture);
                        reply["nativeFrame"] = clock.CurrentFrameIndex.ToString(CultureInfo.InvariantCulture);
                        break;
                    }

                    case "lean-s0-target-upload-init":
                    {
                        int length = int.Parse(Get("length"), NumberStyles.None, CultureInfo.InvariantCulture);
                        string sha = Get("sha256");
                        if (length <= 0 || length > LeanS0Mode.MaxPacketBytes || !MovieProtocolV1.IsLowerSha256(sha))
                        {
                            throw new InvalidDataException("Invalid LeanS0 packet upload metadata.");
                        }
                        leanS0UploadBuffer = new byte[length];
                        leanS0UploadOffset = 0;
                        leanS0UploadExpectedHash = sha;
                        reply["status"] = "UploadInitialized";
                        break;
                    }

                    case "lean-s0-target-upload-chunk":
                    {
                        if (leanS0UploadBuffer == null)
                        {
                            throw new InvalidOperationException("Upload buffer is not initialized.");
                        }
                        int offset = int.Parse(Get("offset"), NumberStyles.None, CultureInfo.InvariantCulture);
                        if (offset != leanS0UploadOffset || offset < 0 || offset >= leanS0UploadBuffer.Length)
                        {
                            throw new InvalidDataException("LeanS0 upload chunk offset mismatch.");
                        }
                        byte[] chunk = Convert.FromBase64String(Get("data"));
                        if (chunk.Length != Math.Min(LeanS0ChunkBytes, leanS0UploadBuffer.Length - offset))
                        {
                            throw new InvalidDataException("LeanS0 upload chunk length mismatch.");
                        }
                        Buffer.BlockCopy(chunk, 0, leanS0UploadBuffer, offset, chunk.Length);
                        leanS0UploadOffset += chunk.Length;
                        reply["received"] = leanS0UploadOffset.ToString(CultureInfo.InvariantCulture);
                        break;
                    }

                    case "lean-s0-target-start":
                    {
                        if (!LeanS0Mode.IsTargetRole || mode != "Replay" || !inputReady || movieFrame != 0
                            || !clock.IsPaused || clock.IsFinished || input.ConsumedSamples != 0)
                        {
                            throw new InvalidOperationException("LeanS0 target start requires fresh paused Movie 0 in Replay mode.");
                        }

                        int slot = int.Parse(Get("slot"), NumberStyles.None, CultureInfo.InvariantCulture);
                        if (slot < 1 || slot > 4)
                        {
                            throw new InvalidDataException("Invalid save slot for LeanS0 target start.");
                        }

                        byte[] rawBytes;
                        if (fields.TryGetValue("packetPath", out var packetPath) && !string.IsNullOrWhiteSpace(packetPath))
                        {
                            rawBytes = File.ReadAllBytes(packetPath);
                        }
                        else if (leanS0UploadBuffer != null && leanS0UploadOffset == leanS0UploadBuffer.Length)
                        {
                            string actualHash = Sha256Utility.ComputeHex(leanS0UploadBuffer);
                            if (!string.Equals(actualHash, leanS0UploadExpectedHash, StringComparison.Ordinal))
                            {
                                throw new InvalidDataException("Uploaded LeanS0 packet SHA-256 mismatch.");
                            }
                            rawBytes = leanS0UploadBuffer;
                        }
                        else
                        {
                            throw new InvalidOperationException("No LeanS0 packet provided via packetPath or upload buffer.");
                        }

                        LeanS0Packet packet = LeanS0PacketCodec.Decode(rawBytes);
                        leanS0ExportedBytes = rawBytes;
                        leanS0ExportedHash = Sha256Utility.ComputeHex(rawBytes);
                        BeginLeanS0TargetSaveLoad(packet, frame, slot);

                        reply["status"] = "LeanS0TargetLoadingStarted";
                        reply["targetScene"] = packet.TargetSceneName;
                        reply["entryGate"] = packet.EntryGateName;
                        reply["packetBytes"] = rawBytes.Length.ToString(CultureInfo.InvariantCulture);
                        reply["packetSha256"] = leanS0ExportedHash;
                        break;
                    }

                    case "lean-s0-target-status":
                        reply["status"] = mode;
                        reply["jobPhase"] = leanS0TargetJob?.Phase ?? string.Empty;
                        reply["nativeFrame"] = clock.CurrentFrameIndex.ToString(CultureInfo.InvariantCulture);
                        reply["movieFrame"] = movieFrame.ToString(CultureInfo.InvariantCulture);
                        reply["error"] = error;
                        break;

                    case "lean-s0-target-apply-and-resume":
                    {
                        if (mode != "LeanS0TargetPaused" || !clock.IsPaused || clock.IsFinished || leanS0TargetPacket == null)
                        {
                            throw new InvalidOperationException("LeanS0 target must be parked in LeanS0TargetPaused state.");
                        }

                        LeanS0Packet packet = leanS0TargetPacket;
                        long endMovie = ParseLeanS0EndMovie(Get("endMovie"));
                        var sw = Stopwatch.StartNew();

                        LeanS0GateReceipt targetReceipt = ApplyLeanS0PacketAtTargetS0(packet, frame);
                        var c12 = packet.C12_ControlPlane;

                        if (endMovie <= movieFrame || endMovie > replayLength)
                        {
                            throw new InvalidDataException($"Invalid LeanS0 target endMovie {endMovie} for start {movieFrame} (len {replayLength}).");
                        }

                        sw.Stop();
                        leanS0RestoreElapsedMs = sw.ElapsedMilliseconds;

                        int packetLen = leanS0ExportedBytes?.Length ?? 0;
                        OpenLeanS0K3Trace(
                            "target",
                            packet.BoundaryOrdinal,
                            packet.SourceSceneName,
                            packet.TargetSceneName,
                            packet.EntryGateName,
                            movieFrame,
                            endMovie,
                            packetLen,
                            leanS0ExportedHash);
                        SetPauseTarget(endMovie, c12.NativeFrame);
                        LoadingSerialSceneProbe.MarkLeanS0BoundaryObserved();
                        mode = "Replay";

                        string? outDir = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT");
                        if (!string.IsNullOrEmpty(outDir) && Directory.Exists(outDir))
                        {
                            File.WriteAllText(
                                Path.Combine(outDir, "lean-s0-restore-summary.json"),
                                JsonConvert.SerializeObject(new
                                {
                                    schema = packet.Schema,
                                    ordinal = packet.BoundaryOrdinal,
                                    sourceScene = packet.SourceSceneName,
                                    targetScene = packet.TargetSceneName,
                                    entryGate = packet.EntryGateName,
                                    startMovie = movieFrame,
                                    endMovie,
                                    restoredNativeFrame = c12.NativeFrame,
                                    rngSeed = targetReceipt.Seed,
                                    rngSha256 = targetReceipt.RngSha256,
                                    packetBytes = packetLen,
                                    packetSha256 = leanS0ExportedHash,
                                    restoreElapsedMs = leanS0RestoreElapsedMs,
                                    gateReceipt = targetReceipt
                                }, Formatting.Indented),
                                new UTF8Encoding(false));
                        }

                        reply["status"] = "ResumingLeanS0TargetSuffix";
                        reply["startMovie"] = movieFrame.ToString(CultureInfo.InvariantCulture);
                        reply["endMovie"] = endMovie.ToString(CultureInfo.InvariantCulture);
                        reply["nativeFrame"] = c12.NativeFrame.ToString(CultureInfo.InvariantCulture);
                        reply["restoreElapsedMs"] = leanS0RestoreElapsedMs.ToString(CultureInfo.InvariantCulture);
                        reply["rngSeed"] = targetReceipt.Seed.ToString(CultureInfo.InvariantCulture);
                        reply["rngSha256"] = targetReceipt.RngSha256;
                        break;
                    }

                    default:
                        throw new InvalidDataException("Unknown LeanS0 phase: " + phase);
                }

                return reply;
            }, timeoutMilliseconds: timeoutMs);
        }

        private long ParseLeanS0EndMovie(string raw)
        {
            if (string.Equals(raw, "end", StringComparison.OrdinalIgnoreCase))
            {
                return replayLength;
            }
            if (long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out long endMovie)
                && endMovie > movieFrame && endMovie <= replayLength)
            {
                return endMovie;
            }
            throw new InvalidDataException($"Invalid LeanS0 endMovie '{raw}' (current={movieFrame}, length={replayLength}).");
        }

        private StreamWriter? leanS0LoadingDiagWriter;
        private LoadingRandomCallTrace? leanS0LoadingRngTrace;
        private MonoMod.RuntimeDetour.Hook? leanS0ITweenHook;

        private void InstallLeanS0RngDiagHooks()
        {
            DisposeLeanS0RngDiagHooks();
            bool diagIncludeGameplay = string.Equals(Environment.GetEnvironmentVariable("HKTAS_LEAN_S0_DIAG"), "2", StringComparison.Ordinal);
            leanS0LoadingRngTrace = new LoadingRandomCallTrace((phase, site, self) =>
            {
                if (leanS0LoadingDiagWriter == null || (!diagIncludeGameplay && frameInputEnabled) || phase != "rng-after") return;
                leanS0LoadingDiagWriter.WriteLine(JsonConvert.SerializeObject(new
                {
                    phase = "rng-call",
                    site,
                    owner = LoadingRandomCallTrace.Describe(self),
                    nativeFrame = clock.CurrentFrameIndex,
                    engineFrame = UnityEngine.Time.frameCount,
                    rngSha256 = ReadCurrentRngSha256Safe()
                }));
            });
            var doiTween = typeof(HutongGames.PlayMaker.Actions.iTweenMoveTo).GetMethod("DoiTween", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (doiTween != null)
            {
                leanS0ITweenHook = new MonoMod.RuntimeDetour.Hook(doiTween, (Action<Action<HutongGames.PlayMaker.Actions.iTweenMoveTo>, HutongGames.PlayMaker.Actions.iTweenMoveTo>)((orig, self) =>
                {
                    if (leanS0LoadingDiagWriter != null && !frameInputEnabled)
                    {
                        leanS0LoadingDiagWriter.WriteLine(JsonConvert.SerializeObject(new
                        {
                            phase = "itween-fsm",
                            nativeFrame = clock.CurrentFrameIndex,
                            engineFrame = UnityEngine.Time.frameCount,
                            owner = self.Owner != null ? self.Owner.name : "",
                            fsm = self.Fsm != null ? self.Fsm.Name : "",
                            state = self.State != null ? self.State.Name : "",
                            prevState = self.Fsm?.PreviousActiveState?.Name ?? ""
                        }));
                    }
                    orig(self);
                }));
            }
        }

        private void DisposeLeanS0RngDiagHooks()
        {
            leanS0ITweenHook?.Dispose();
            leanS0ITweenHook = null;
            leanS0LoadingRngTrace?.Dispose();
            leanS0LoadingRngTrace = null;
        }

        private void OpenLeanS0K3Trace(
            string role,
            int ordinal,
            string sourceScene,
            string targetScene,
            string entryGate,
            long startMovie,
            long endMovie,
            int packetBytes,
            string packetSha256)
        {
            leanS0K3Trace?.Dispose();
            leanS0LoadingDiagWriter?.Dispose();
            leanS0K3Role = role;
            leanS0K3StartMovie = startMovie;
            leanS0K3EndMovie = endMovie;

            string outDir = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT") ?? sessionDirectory;
            string path = Path.Combine(outDir, "lean-s0-k3.jsonl");
            leanS0K3Trace = new LeanS0K3TraceWriter(path);
            leanS0K3Trace.WriteHeader(new
            {
                role,
                ordinal,
                sourceScene,
                targetScene,
                entryGate,
                startMovie,
                endMovie,
                startNative = clock.CurrentFrameIndex,
                startSkippedLoadFrames = skippedLoadFrames,
                rngSeed = lastLeanS0GateReceipt?.Seed ?? 0,
                rngSha256 = lastLeanS0GateReceipt?.RngSha256 ?? string.Empty,
                packetBytes,
                packetSha256
            });
            string? diagMode = Environment.GetEnvironmentVariable("HKTAS_LEAN_S0_DIAG");
            if (string.Equals(diagMode, "1", StringComparison.Ordinal) || string.Equals(diagMode, "2", StringComparison.Ordinal))
            {
                string diagPath = Path.Combine(outDir, "lean-s0-loading-diag.jsonl");
                leanS0LoadingDiagWriter = new StreamWriter(diagPath, append: false, new UTF8Encoding(false));
                InstallLeanS0RngDiagHooks();
            }
        }

        private void SampleLeanS0LoadingDiag(long nativeFrame, string phase, string boundary)
        {
            if (leanS0LoadingDiagWriter == null) return;
            GameManager? gm = GameManager.instance;
            HeroController? hero = HeroController.SilentInstance;
            string scene = gm != null ? (gm.sceneName ?? string.Empty) : string.Empty;
            string gs = gm != null ? gm.gameState.ToString() : "Unknown";
            string ts = hero != null ? hero.transitionState.ToString() : "None";
            string hs = hero != null ? hero.hero_state.ToString() : "None";
            string rng = ReadCurrentRngSha256Safe();
            leanS0LoadingDiagWriter.WriteLine(JsonConvert.SerializeObject(new
            {
                phase,
                nativeFrame,
                engineFrame = UnityEngine.Time.frameCount,
                time = UnityEngine.Time.time,
                timeSinceLevelLoad = UnityEngine.Time.timeSinceLevelLoad,
                deltaTime = UnityEngine.Time.deltaTime,
                scene,
                gameState = gs,
                boundary,
                heroTransition = ts,
                heroState = hs,
                rngSha256 = rng
            }));
        }

        private void FinishLeanS0K3TraceIfActive()
        {
            DisposeLeanS0RngDiagHooks();
            if (leanS0LoadingDiagWriter != null)
            {
                try { leanS0LoadingDiagWriter.Flush(); leanS0LoadingDiagWriter.Dispose(); } catch { }
                leanS0LoadingDiagWriter = null;
            }
            if (leanS0K3Trace == null) return;
            try
            {
                int rows = leanS0K3Trace.RowsWritten;
                string traceFile = leanS0K3Trace.FilePath;
                leanS0K3Trace.Dispose();
                leanS0K3Trace = null;

                string outDir = Path.GetDirectoryName(traceFile)!;
                File.WriteAllText(
                    Path.Combine(outDir, "lean-s0-k3-summary.json"),
                    JsonConvert.SerializeObject(new
                    {
                        role = leanS0K3Role,
                        startMovie = leanS0K3StartMovie,
                        endMovie = leanS0K3EndMovie,
                        finalMovieFrame = movieFrame,
                        finalNativeFrame = clock.CurrentFrameIndex,
                        finalSkippedLoadFrames = skippedLoadFrames,
                        consumedSamples = input.ConsumedSamples,
                        mismatchCount,
                        error,
                        rowsWritten = rows,
                        captureElapsedMs = leanS0CaptureElapsedMs,
                        restoreElapsedMs = leanS0RestoreElapsedMs
                    }, Formatting.Indented),
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Modding.Logger.LogError("[HKTAS] Failed to finalize LeanS0 K3 trace: " + ex);
            }
        }

        private readonly string? acceptanceRngDiagTargetScene = Environment.GetEnvironmentVariable("HKTAS_ACCEPTANCE_RNG_DIAG_SCENE");
        private readonly HashSet<long> acceptanceRngDiagFrames = ParseAcceptanceRngDiagFrames(Environment.GetEnvironmentVariable("HKTAS_ACCEPTANCE_RNG_DIAG_FRAMES"));
        private StreamWriter? acceptanceRngDiagWriter;
        private LoadingRandomCallTrace? acceptanceRngDiagTrace;
        private MonoMod.RuntimeDetour.Hook? acceptanceITweenLaunchHook;
        private bool acceptanceRngDiagActive;
        private bool acceptanceRngDiagFrameMatched;
        private int acceptanceRngDiagGameplayFramesRemaining;
        private int acceptanceRngDiagEventIndex;

        private static HashSet<long> ParseAcceptanceRngDiagFrames(string? spec)
        {
            var result = new HashSet<long>();
            if (string.IsNullOrWhiteSpace(spec))
            {
                return result;
            }

            foreach (var rawToken in spec!.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string token = rawToken.Trim();
                if (token.Length == 0) continue;
                int dash = token.IndexOf('-');
                if (dash > 0 && dash < token.Length - 1)
                {
                    if (long.TryParse(token.Substring(0, dash).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long start)
                        && long.TryParse(token.Substring(dash + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long end)
                        && start <= end
                        && end - start <= 100000)
                    {
                        for (long f = start; f <= end; f++)
                        {
                            result.Add(f);
                        }
                    }
                }
                else if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long single))
                {
                    result.Add(single);
                }
            }
            return result;
        }

        private static string FormatAcceptanceDiagStack()
        {
            var st = new StackTrace(1, false);
            var frames = new List<string>(6);
            int count = st.FrameCount;
            for (int i = 0; i < count && frames.Count < 6; i++)
            {
                var method = st.GetFrame(i)?.GetMethod();
                if (method == null) continue;
                string fullType = method.DeclaringType?.FullName ?? string.Empty;
                if (fullType.IndexOf("LoadingRandomCallTrace", StringComparison.Ordinal) >= 0
                    || fullType.IndexOf("RuntimeFullRunSession", StringComparison.Ordinal) >= 0
                    || fullType.StartsWith("MonoMod.", StringComparison.Ordinal))
                {
                    continue;
                }
                string typeName = method.DeclaringType?.Name ?? string.Empty;
                string methodName = method.Name ?? string.Empty;
                frames.Add(string.IsNullOrEmpty(typeName) ? methodName : (typeName + "." + methodName));
            }
            return string.Join(" <- ", frames);
        }

        private static string DescribeAcceptanceTargetPath(UnityEngine.GameObject? target)
        {
            if (target == null) return "<null>";
            var names = new List<string>();
            var cur = target.transform;
            while (cur != null)
            {
                names.Add(cur.name);
                cur = cur.parent;
            }
            names.Reverse();
            return string.Join("/", names);
        }

        private void EnsureAcceptanceRngDiagInitialized()
        {
            if (acceptanceRngDiagWriter == null)
            {
                string diagPath = Path.Combine(sessionDirectory, "acceptance-rng-diag.jsonl");
                acceptanceRngDiagWriter = new StreamWriter(diagPath, append: false, new UTF8Encoding(false));
            }

            if (acceptanceRngDiagTrace == null)
            {
                acceptanceRngDiagTrace = new LoadingRandomCallTrace((phase, site, self) =>
                {
                    if (!acceptanceRngDiagActive || acceptanceRngDiagWriter == null || phase != "rng-after")
                    {
                        return;
                    }
                    acceptanceRngDiagEventIndex++;
                    string? stack = acceptanceRngDiagFrameMatched ? FormatAcceptanceDiagStack() : null;
                    acceptanceRngDiagWriter.WriteLine(JsonConvert.SerializeObject(new
                    {
                        segment = acceptanceTraceSegment,
                        eventType = "rng-call",
                        idx = acceptanceRngDiagEventIndex,
                        nativeFrame = clock.CurrentFrameIndex,
                        movieFrame,
                        frameInputEnabled,
                        engineFrame = UnityEngine.Time.frameCount,
                        site,
                        owner = LoadingRandomCallTrace.Describe(self),
                        rngSha256 = ReadCurrentRngSha256Safe(),
                        stack
                    }));
                });
            }

            if (acceptanceITweenLaunchHook == null)
            {
                var launchMethod = typeof(iTween).GetMethod(
                    "Launch",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                    null,
                    new[] { typeof(UnityEngine.GameObject), typeof(Hashtable) },
                    null);
                if (launchMethod != null)
                {
                    acceptanceITweenLaunchHook = new MonoMod.RuntimeDetour.Hook(
                        launchMethod,
                        (Action<Action<UnityEngine.GameObject, Hashtable?>, UnityEngine.GameObject, Hashtable?>)((orig, target, args) =>
                        {
                            if (acceptanceRngDiagActive && acceptanceRngDiagWriter != null)
                            {
                                acceptanceRngDiagWriter.WriteLine(JsonConvert.SerializeObject(new
                                {
                                    segment = acceptanceTraceSegment,
                                    eventType = "itween-launch",
                                    nativeFrame = clock.CurrentFrameIndex,
                                    movieFrame,
                                    frameInputEnabled,
                                    engineFrame = UnityEngine.Time.frameCount,
                                    target = DescribeAcceptanceTargetPath(target),
                                    hasId = args != null && args.Contains("id"),
                                    type = args?["type"]?.ToString() ?? "",
                                    method = args?["method"]?.ToString() ?? "",
                                    name = args?["name"]?.ToString() ?? "",
                                    stack = Environment.StackTrace
                                }));
                            }
                            orig(target, args);
                        }));
                }
            }
        }

        private void UpdateAcceptanceRngDiagForFrame(long currentMovieFrame, bool inputEnabled)
        {
            if (acceptanceRngDiagFrames.Count == 0)
            {
                return;
            }

            if (inputEnabled && acceptanceRngDiagFrames.Contains(currentMovieFrame))
            {
                EnsureAcceptanceRngDiagInitialized();
                acceptanceRngDiagFrameMatched = true;
                acceptanceRngDiagActive = true;
                acceptanceRngDiagEventIndex = 0;
                acceptanceRngDiagWriter!.WriteLine(JsonConvert.SerializeObject(new
                {
                    segment = acceptanceTraceSegment,
                    eventType = "frame-start",
                    nativeFrame = clock.CurrentFrameIndex,
                    movieFrame = currentMovieFrame,
                    scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? string.Empty,
                    rngSha256 = ReadCurrentRngSha256Safe()
                }));
                return;
            }

            acceptanceRngDiagFrameMatched = false;
            if (acceptanceRngDiagGameplayFramesRemaining <= 0 && acceptanceRngDiagActive)
            {
                acceptanceRngDiagActive = false;
                acceptanceRngDiagWriter?.Flush();
            }
        }

        private void MaybeStartAcceptanceRngDiag(string? targetScene, string trigger, int ordinal, long nativeFrame)
        {
            if (string.IsNullOrEmpty(acceptanceRngDiagTargetScene)
                || !string.Equals(targetScene, acceptanceRngDiagTargetScene, StringComparison.Ordinal))
            {
                return;
            }

            EnsureAcceptanceRngDiagInitialized();
            acceptanceRngDiagActive = true;
            acceptanceRngDiagGameplayFramesRemaining = 2;
            acceptanceRngDiagEventIndex = 0;
            acceptanceRngDiagWriter!.WriteLine(JsonConvert.SerializeObject(new
            {
                segment = acceptanceTraceSegment,
                eventType = "diag-start",
                trigger,
                ordinal,
                targetScene,
                nativeFrame,
                movieFrame,
                rngSha256 = ReadCurrentRngSha256Safe()
            }));
            WriteAcceptanceHudVesselDiag(includeFullGraph: string.Equals(acceptanceTraceSegment, "baseline", StringComparison.Ordinal));
            acceptanceRngDiagWriter.Flush();
        }

        private void WriteAcceptanceHudVesselDiag(bool includeFullGraph)
        {
            if (acceptanceRngDiagWriter == null) return;
            var pd = PlayerData.instance;
            var vessel1 = GameCameras.instance?.hudCamera?.transform?.Find("Hud Canvas/Soul Orb/Vessels/Vessel 1");
            var fsms = vessel1 != null
                ? vessel1.GetComponents<PlayMakerFSM>().Select(fsm => new
                {
                    fsmName = fsm.FsmName,
                    activeStateName = fsm.ActiveStateName,
                    previousActiveState = fsm.Fsm?.PreviousActiveState?.Name ?? "",
                    initialized = fsm.Fsm?.Initialized ?? false,
                    started = fsm.Fsm?.Started ?? false,
                    intVars = fsm.FsmVariables?.IntVariables?.ToDictionary(v => v.Name, v => v.Value) ?? new Dictionary<string, int>(),
                    boolVars = fsm.FsmVariables?.BoolVariables?.ToDictionary(v => v.Name, v => v.Value) ?? new Dictionary<string, bool>(),
                    states = includeFullGraph && fsm.FsmStates != null
                        ? fsm.FsmStates.Select(st => new
                        {
                            name = st.Name,
                            transitions = st.Transitions?.Select(tr => new { @event = tr.EventName, toState = tr.ToState }).ToArray(),
                            actions = st.Actions?.Select(a => a?.GetType().Name ?? "<null>").ToArray()
                        }).ToArray()
                        : null
                }).ToArray()
                : null;
            acceptanceRngDiagWriter.WriteLine(JsonConvert.SerializeObject(new
            {
                segment = acceptanceTraceSegment,
                eventType = "hud-vessel-diag",
                mpCharge = pd?.GetInt("MPCharge"),
                mpReserve = pd?.GetInt("MPReserve"),
                mpReserveMax = pd?.GetInt("MPReserveMax"),
                soulLimited = pd?.GetBool("soulLimited"),
                inBossSequence = BossSequenceController.IsInSequence,
                boundSoul = BossSequenceController.IsInSequence && BossSequenceController.BoundSoul,
                vesselScale = vessel1 != null ? vessel1.localScale.ToString() : "",
                fsms
            }));
        }

        private void StepAcceptanceRngDiagFrame(long nativeFrame, long executedMovieFrame)
        {
            if (!acceptanceRngDiagActive || acceptanceRngDiagWriter == null || !frameInputEnabled || executedMovieFrame < 0)
            {
                return;
            }

            var hero = HeroController.SilentInstance;
            var body = hero != null ? hero.GetComponent<UnityEngine.Rigidbody2D>() : null;
            var pos = hero != null ? hero.transform.position : default(UnityEngine.Vector3);
            var bpos = body != null ? body.position : default(UnityEngine.Vector2);
            var bvel = body != null ? body.velocity : default(UnityEngine.Vector2);
            var c = hero?.cState;
            string cState = c != null
                ? $"g={(c.onGround ? 1 : 0)},d={(c.dashing ? 1 : 0)},sd={(c.shadowDashing ? 1 : 0)},j={(c.jumping ? 1 : 0)},f={(c.falling ? 1 : 0)},a={(c.attacking ? 1 : 0)},r={(c.recoiling ? 1 : 0)},ws={(c.wallSliding ? 1 : 0)},hl={(c.willHardLand ? 1 : 0)},fr={(c.facingRight ? 1 : 0)},dc={(c.dashCooldown ? 1 : 0)}"
                : "";
            var bounces = UnityEngine.Object.FindObjectsOfType<ObjectBounce>()
                .Where(ob => ob != null && ob.gameObject.activeInHierarchy)
                .OrderBy(ob => ob.transform.position.x)
                .ThenBy(ob => ob.transform.position.y)
                .Select(ob =>
                {
                    var rb = ob.GetComponent<UnityEngine.Rigidbody2D>();
                    var p = ob.transform.position;
                    var v = rb != null ? rb.velocity : default(UnityEngine.Vector2);
                    return new
                    {
                        n = ob.gameObject.name,
                        x = p.x.ToString("R", CultureInfo.InvariantCulture),
                        y = p.y.ToString("R", CultureInfo.InvariantCulture),
                        vx = rb != null ? v.x.ToString("R", CultureInfo.InvariantCulture) : "",
                        vy = rb != null ? v.y.ToString("R", CultureInfo.InvariantCulture) : ""
                    };
                })
                .ToArray();
            acceptanceRngDiagWriter.WriteLine(JsonConvert.SerializeObject(new
            {
                segment = acceptanceTraceSegment,
                eventType = "frame-end",
                nativeFrame,
                movieFrame = executedMovieFrame,
                scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? string.Empty,
                rngSha256 = ReadCurrentRngSha256Safe(),
                hx = hero != null ? pos.x.ToString("R", CultureInfo.InvariantCulture) : "",
                hy = hero != null ? pos.y.ToString("R", CultureInfo.InvariantCulture) : "",
                bx = body != null ? bpos.x.ToString("R", CultureInfo.InvariantCulture) : "",
                by = body != null ? bpos.y.ToString("R", CultureInfo.InvariantCulture) : "",
                vx = body != null ? bvel.x.ToString("R", CultureInfo.InvariantCulture) : "",
                vy = body != null ? bvel.y.ToString("R", CultureInfo.InvariantCulture) : "",
                cState,
                bounces
            }));
            if (acceptanceRngDiagGameplayFramesRemaining > 0)
            {
                acceptanceRngDiagGameplayFramesRemaining--;
            }
            if (acceptanceRngDiagGameplayFramesRemaining <= 0)
            {
                acceptanceRngDiagActive = false;
                acceptanceRngDiagFrameMatched = false;
                acceptanceRngDiagWriter.Flush();
            }
        }

        private void DisposeAcceptanceRngDiag()
        {
            acceptanceRngDiagActive = false;
            acceptanceRngDiagFrameMatched = false;
            acceptanceITweenLaunchHook?.Dispose();
            acceptanceITweenLaunchHook = null;
            acceptanceRngDiagTrace?.Dispose();
            acceptanceRngDiagTrace = null;
            if (acceptanceRngDiagWriter != null)
            {
                try { acceptanceRngDiagWriter.Flush(); acceptanceRngDiagWriter.Dispose(); } catch { }
                acceptanceRngDiagWriter = null;
            }
        }
    }
}
