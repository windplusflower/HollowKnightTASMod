using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace HollowKnightTAS.Runtime.FullRun
{
    public sealed partial class RuntimeFullRunSession
    {
        private S0ObservationJob? s0Observation;
        private sealed class S0ObservationJob
        {
            internal string Phase = "LoadingInitialSave", Error = "";
            internal readonly string Target;
            internal readonly long Movie, Origin;
            internal readonly int Samples;
            internal long LastNative;
            internal readonly Stopwatch Elapsed = Stopwatch.StartNew();
            internal int PendingBootstrap, Boundaries;
            internal bool FadeFinished;
            internal bool ClockApplicationStarted;
            internal bool ClockApplicationCompleted;
            internal CanonicalParkedBoundaryGuard? Parked;
            internal S0ObservationJob(string target, long movie, long origin, int samples, long native)
            { Target = target; Movie = movie; Origin = origin; Samples = samples; LastNative = native; }
        }

        private Dictionary<string, string> S0ObservationCommand(IReadOnlyDictionary<string, string> fields)
        {
            CanonicalPhaseOneTrace.RequireContext(Environment.GetEnvironmentVariable);
            if (!CanonicalPhaseOneTrace.Enabled || Environment.GetEnvironmentVariable("HKTAS_LOADING_S0_OBSERVING") != "1")
                throw new InvalidOperationException("S0 observation requires the protected canonical harness.");
            if (!fields.TryGetValue("requestId", out var requestId) || !fields.TryGetValue("phase", out var phase))
                throw new InvalidDataException("Invalid S0 observation command.");
            return observationQueue.Invoke(frame => {
                checkpointSaves!.AssertActive();
                if (phase == "s0-start")
                {
                    if (fields.Count != 4 || !fields.TryGetValue("slot", out var text) || !int.TryParse(text, out var slot)
                        || slot < 1 || slot > 4 || !fields.TryGetValue("target", out var target)
                        || target == "Menu_Title" || !UnityEngine.Application.CanStreamedLevelBeLoaded(target)
                        || !checkpointSaves.Descriptor.OriginalFileSha256.Keys.Any(name => name.StartsWith("user" + slot + ".", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("S0 observation needs a protected real save slot and a loadable world scene.");
                    if (s0Observation != null || checkpointRestore != null || mode != "Replay" || !inputReady || movieFrame != 0
                        || !clock.IsPaused || clock.IsFinished || input.ConsumedSamples != 0 || !movieInputNativeOrigin.HasValue || movieInputControlOrigin == null)
                        throw new InvalidOperationException("S0 observation requires fresh paused Movie zero with its actual StartInputAt origin.");
                    var job = new S0ObservationJob(target, movieFrame, movieInputNativeOrigin.Value, input.ConsumedSamples, frame);
                    s0Observation = job; mode = "S0Observing";
                    try {
                        input.SetNativeFrameActive(false); frameInputEnabled = false;
                        input.SetFrameInputEnabled(false); mouse.SetFrameInputEnabled(false);
                        On.GameManager.FadeSceneInWithDelay += OnS0BootstrapFade;
                        On.HeroController.Respawn += OnS0BootstrapRespawn;
                        WriteS0Preparation(job, "start"); (GameManager.instance ?? throw new InvalidOperationException("GameManager is absent.")).LoadGameFromUI(slot);
                    }
                    catch (Exception error) { FailS0Observation(job, error); throw; }
                }
                else if (phase == "s0-clock-apply")
                {
                    var job = s0Observation ?? throw new InvalidOperationException("No parked zero-Movie target.");
                    try {
                        if (fields.Count != 2 || !CanonicalClockApplyExperiment.Enabled || job.Phase != "PausedAtB"
                            || job.ClockApplicationStarted || !clock.IsPaused || clock.IsFinished)
                            throw new InvalidOperationException("Limited clock D application requires a fresh protected parked B.");
                        AssertS0NoMovieConsumption(job);
                        var boundary = LoadingSerialSceneProbe.GetProtectedObservationBoundary();
                        (job.Parked ?? throw new InvalidOperationException("No frozen parked B identity.")).Require(frame, clock.IsPaused,
                            boundary.Item1, boundary.Item2, boundary.Item3, boundary.Item2.SceneName, S0LoadInfoFields(boundary.Item2));
                        var source = CanonicalClockApplyExperiment.Read(Environment.GetEnvironmentVariable("HKTAS_LOADING_CLOCK_APPLY_SOURCE")
                            ?? throw new InvalidOperationException("No harness-bound clock D source."));
                        var before = CanonicalClockApplyExperiment.Capture(frame);
                        job.ClockApplicationStarted = true;
                        source.Apply(clock, frame,
                            (bytes, cursor) => CheckpointClockMethod("ImportCheckpointRandomState").Invoke(null, new object[] { bytes, cursor }),
                            (bytes, cursor) => CheckpointClockMethod("ImportCheckpointClockObserver").Invoke(null, new object[] { bytes, cursor }));
                        job.ClockApplicationCompleted = true;
                        job.Phase = "ClockAppliedPaused";
                        File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT")!, "clock-d-application.json"),
                            JsonConvert.SerializeObject(new { schema = 1, applicationExecuted = true, rawReadbackEqual = true,
                                before, source, after = CanonicalClockApplyExperiment.Capture(clock.CurrentFrameIndex),
                                movieFrame, consumedSamples = input.ConsumedSamples, nativePaused = clock.IsPaused,
                                otherDomainsApplied = false, d1Passed = false, d2Passed = false, d3Passed = false,
                                restoreQualified = false, completeStateCoverage = false }));
                    }
                    catch (Exception error) { FailS0Observation(job, error); throw; }
                }
                else if (phase == "s0-cancel")
                {
                    if (fields.Count != 2 || s0Observation == null) throw new InvalidDataException("No S0 observation to cancel.");
                    FailS0Observation(s0Observation, new OperationCanceledException("S0 observation cancelled; retire process."));
                }
                else if (phase != "s0-status" || fields.Count != 2) throw new InvalidDataException("Unknown S0 observation phase.");
                var pending = s0Observation ?? throw new InvalidOperationException("S0 observation is not started.");
                AssertS0NoMovieConsumption(pending);
                return new Dictionary<string, string> { ["requestId"] = requestId, ["status"] = pending.Phase,
                    ["movieFrame"] = movieFrame.ToString(CultureInfo.InvariantCulture),
                    ["consumedSamplesBefore"] = pending.Samples.ToString(CultureInfo.InvariantCulture),
                    ["consumedSamplesAfter"] = input.ConsumedSamples.ToString(CultureInfo.InvariantCulture),
                    ["inputNativeOrigin"] = pending.Origin.ToString(CultureInfo.InvariantCulture),
                    ["boundaries"] = pending.Boundaries.ToString(CultureInfo.InvariantCulture),
                    ["d1Passed"] = "false", ["restoreQualified"] = "false", ["detail"] = pending.Error };
            });
        }

        private void AssertS0NoMovieConsumption(S0ObservationJob job)
        {
            if (movieFrame != job.Movie || movieFrame != 0 || input.ConsumedSamples != job.Samples
                || movieInputNativeOrigin != job.Origin || frameInputEnabled)
                throw new InvalidOperationException("S0 preparation consumed Movie input or changed its StartInputAt origin.");
        }
        private bool RunS0ObservationFrame(long native)
        {
            var job = s0Observation;
            if (job == null) return false;
            if (mode != "S0Observing") return mode == "Failed";
            try
            {
                if (job.Phase == "PausedAtB" || job.Phase == "ClockAppliedPaused") CanonicalParkedBoundaryGuard.RejectPlayerLoopAdvance();
                AssertS0NoMovieConsumption(job);
                if (job.Elapsed.ElapsedMilliseconds > 240000) throw new TimeoutException("S0 preparation exceeded four minutes.");
                if (native < job.LastNative || native > job.LastNative + 1) throw new InvalidDataException("S0 native cursor was not sequential.");
                job.LastNative = native;
                // Same neutral loading path as ordinary replay: suspended hooks
                // attach zero leases to newly rebound action sets and call the
                // original update, while never consuming the prepared Movie.
                clock.SetFrameRate(50); input.SetNativeFrameActive(true);
                if (LoadingSerialSceneProbe.ObserveNativeBoundary(0, native, sessionDirectory, movieInputNativeOrigin, movieInputControlOrigin, input, mouse))
                {
                    job.Boundaries++;
                    WriteS0Preparation(job, "canonical-boundary");
                    if (job.Phase == "AwaitingArbitraryB")
                    {
                        var boundary = LoadingSerialSceneProbe.GetProtectedObservationBoundary();
                        job.Parked = new CanonicalParkedBoundaryGuard(native, boundary.Item1, boundary.Item2,
                            boundary.Item3, boundary.Item2.SceneName, S0LoadInfoFields(boundary.Item2));
                        File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT")!, "s0-neutral-input.json"),
                            JsonConvert.SerializeObject(input.ObserveSuspendedActionCaches()));
                        job.Phase = "PausedAtB"; input.SetNativeFrameActive(false); clock.RequestPause();
                        WriteS0Preparation(job, "paused-at-B"); return true;
                    }
                }
                if (job.Phase != "LoadingInitialSave") return true;
                var manager = GameManager.instance;
                var hero = HeroController.SilentInstance;
                if (manager == null || manager.IsInSceneTransition || firstLevelActivationPending || hero == null
                    || !hero.gameObject.activeInHierarchy || manager.gameState != GlobalEnums.GameState.PLAYING
                    || !job.FadeFinished || job.PendingBootstrap != 0 || GameCameras.instance.cameraFadeFSM.ActiveStateName != "Normal") return true;
                if (!CanonicalOriginalAudioBootstrapObservation.Complete(manager, out var audio)) return true;
                File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT")!, "s0-bootstrap-audio-ready.json"),
                    JsonConvert.SerializeObject(audio));
                job.Phase = "AwaitingArbitraryB";
                WriteS0Preparation(job, "initial-save-ready");
                manager.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = job.Target,
                    EntryGateName = "", IsFirstLevelForPlayer = false });
            }
            catch (Exception error) { FailS0Observation(job, error); }
            return true;
        }
        private IEnumerator OnS0BootstrapFade(On.GameManager.orig_FadeSceneInWithDelay original, GameManager self, float delay)
            => TrackS0Bootstrap(original(self, delay), true);
        private static string S0LoadInfoFields(GameManager.SceneLoadInfo info)
        {
            var fields = typeof(GameManager.SceneLoadInfo).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly).OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();
            var expected = new Dictionary<string, Type>(StringComparer.Ordinal) {
                ["IsFirstLevelForPlayer"] = typeof(bool), ["SceneName"] = typeof(string),
                ["HeroLeaveDirection"] = typeof(GlobalEnums.GatePosition?), ["EntryGateName"] = typeof(string),
                ["EntryDelay"] = typeof(float), ["PreventCameraFadeOut"] = typeof(bool),
                ["WaitForSceneTransitionCameraFade"] = typeof(bool), ["Visualization"] = typeof(GameManager.SceneLoadVisualizations),
                ["AlwaysUnloadUnusedAssets"] = typeof(bool), ["forceWaitFetch"] = typeof(bool) };
            if (fields.Length != expected.Count || fields.Any(field => !expected.TryGetValue(field.Name, out var type) || field.FieldType != type))
                throw new InvalidDataException("Original SceneLoadInfo ten scalar fields differ.");
            return JsonConvert.SerializeObject(fields.Select(field => new { field.Name, Type = field.FieldType.FullName,
                Value = CanonicalParkedBoundaryGuard.EncodeScalarValue(field.GetValue(info), field.FieldType) }));
        }
        private IEnumerator OnS0BootstrapRespawn(On.HeroController.orig_Respawn original, HeroController self)
            => TrackS0Bootstrap(original(self), false);
        private IEnumerator TrackS0Bootstrap(IEnumerator routine, bool fade)
        {
            var job = s0Observation!; job.PendingBootstrap++;
            var complete = false;
            try {
                while (true) {
                    bool moved;
                    try { moved = routine.MoveNext(); }
                    catch (Exception error) { FailS0Observation(job, error); throw; }
                    if (!moved) { complete = true; break; }
                    yield return routine.Current;
                }
            }
            finally { (routine as IDisposable)?.Dispose(); job.PendingBootstrap--; if (fade && complete) job.FadeFinished = true; }
        }
        private void FailS0Observation(S0ObservationJob job, Exception error)
        {
            job.Phase = "Failed"; job.Error = error.ToString();
            frameInputEnabled = false;
            try {
                input.SetNativeFrameActive(false); input.SetFrameInputEnabled(false); mouse.SetFrameInputEnabled(false);
                WriteS0Preparation(job, "failed");
            }
            finally { Fail("S0ObservationFault: " + error.Message); }
        }
        private void WriteS0Preparation(S0ObservationJob job, string phase)
        {
            var directory = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT")!;
            File.AppendAllText(Path.Combine(directory, "s0-preparation.jsonl"), JsonConvert.SerializeObject(new {
                phase, job.Phase, job.Target, job.Boundaries, movieBefore = job.Movie, movieAfter = movieFrame,
                samplesBefore = job.Samples, samplesAfter = input.ConsumedSamples, inputOriginBefore = job.Origin,
                inputOriginAfter = movieInputNativeOrigin, native = clock.CurrentFrameIndex, job.Error,
                diagnosticOnly = true, d1Passed = false, restoreQualified = false,
                clockApplicationAttempted = job.ClockApplicationStarted, clockReadbackCompleted = job.ClockApplicationCompleted,
                applicationExecuted = job.ClockApplicationCompleted ? (bool?)true : job.ClockApplicationStarted ? null : false }) + "\n");
        }
    }
}
