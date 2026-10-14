using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Runtime.Rng;
using Newtonsoft.Json;

namespace HollowKnightTAS.Runtime.FullRun
{
    public sealed partial class RuntimeFullRunSession
    {
        private byte[]? actualDImport;
        private int actualDImportOffset;
        private string actualDImportHash = "";
        private CanonicalActualDExperiment? actualDSource;
        private bool actualDApplicationStarted;
        private byte[]? actualDExportBytes;
        private CanonicalActualDExperiment? actualDExportSource;
        private CanonicalActualDExperiment.SessionCarry CaptureActualSessionCarry(long native)
        {
            if (randomSeedHandshakePending || replayMovie == null || checkpointExecutionFiles == null)
                throw new InvalidOperationException("Actual D capture requires its initialized strict replay and execution identity.");
            var rng = UnityRandomStateCodec_1_5_78_11833.Resolve();
            if (!rng.Ready || rng.Codec == null) throw new InvalidOperationException("Actual D Unity RNG codec is unavailable.");
            return new CanonicalActualDExperiment.SessionCarry {
                MoviePrefixSha256 = Sha256Utility.ComputeHex(Encoding.UTF8.GetBytes(new MovieV2Codec().WriteCanonical(MovieV2Prefix.Take(replayMovie, movieFrame)))),
                InitialSavesSha256 = checkpointInitialSavesSha256,
                ExecutionFiles = new Dictionary<string, string>(checkpointExecutionFiles), SkippedLoadingFrames = skippedLoadFrames,
                TimingRunIndex = timingRunIndex, TimingRunStart = timingRunStart, ActiveFrameRate = activeFrameRate,
                RecordingFrameRate = recordingFrameRate, TitleReadySeen = titleReadySeen, FirstLevelActivationPending = firstLevelActivationPending,
                RandomSeedScene = randomSeedSceneName, LastGameUpdateNativeFrame = lastGameUpdateNativeFrame,
                GameUpdateDeltaBits = BitConverter.DoubleToInt64Bits(gameUpdateDeltaSeconds),
                GameUpdateTimeBits = BitConverter.DoubleToInt64Bits(gameUpdateTimeSeconds),
                ExecutedMovieSecondsBits = BitConverter.DoubleToInt64Bits(executedMovieSeconds), UnityRandom = rng.Codec.Encode(UnityEngine.Random.state), ParticleManagerRandom = LoadingGameplayOracle.CaptureParticleRandomState() };
        }
        private Dictionary<string, string> ActualDExperimentCommand(IReadOnlyDictionary<string, string> fields)
        {
            CanonicalPhaseOneTrace.RequireContext(Environment.GetEnvironmentVariable);
            if (!CanonicalActualDExperiment.Enabled || !fields.TryGetValue("phase", out var phase)
                || !fields.TryGetValue("requestId", out var requestId)) throw new InvalidDataException("Actual D experiment is disabled or malformed.");
            return observationQueue.Invoke(frame => {
                checkpointSaves!.AssertActive();
                string Get(string key) => fields.TryGetValue(key, out var value) ? value : throw new InvalidDataException("Missing actual D field: " + key);
                var reply = new Dictionary<string, string> { ["requestId"] = requestId, ["restoreQualified"] = "false" };
                if (phase == "d-exp-source-info" || phase == "d-exp-source-chunk" || phase == "d-exp-source-resume") {
                    var bytes = LoadingSerialSceneProbe.ExperimentalSource ?? throw new InvalidOperationException("No sealed in-memory source D.");
                    if (actualDExportSource == null) { actualDExportSource = CanonicalActualDExperiment.Decode(bytes); actualDExportBytes = bytes; }
                    if (!ReferenceEquals(actualDExportBytes, bytes)) throw new InvalidOperationException("Sealed source D changed during export.");
                    var source = actualDExportSource;
                    if (mode != "DSourcePaused" || !clock.IsPaused || clock.IsFinished || frame != source.NativeFrame || source.Session == null)
                        throw new InvalidOperationException("Source D is not sealed at its original paused boundary.");
                    if (phase == "d-exp-source-resume") {
                        if (fields.Count != 3 || !long.TryParse(Get("endMovie"), out var end) || end <= movieFrame
                            || end > Math.Min(replayLength, movieFrame + 64)) throw new InvalidDataException("Source suffix endpoint is not bounded.");
                        source.Clock.RequireReadback(clock); StartActualDSuffixOracle("source", end); SetPauseTarget(end, frame); mode = "Replay";
                        reply["status"] = "ResumingOriginalSourceSuffix"; return reply;
                    }
                    if (phase == "d-exp-source-info") {
                        if (fields.Count != 2) throw new InvalidDataException("Invalid source-info fields.");
                        reply["length"] = bytes.Length.ToString(CultureInfo.InvariantCulture); reply["sha256"] = Sha256Utility.ComputeHex(bytes);
                        reply["movieFrame"] = source.MovieFrame.ToString(CultureInfo.InvariantCulture); reply["nativeFrame"] = source.NativeFrame.ToString(CultureInfo.InvariantCulture);
                    } else {
                        if (fields.Count != 3 || !int.TryParse(Get("offset"), out var offset) || offset < 0
                            || offset >= bytes.Length || offset % CanonicalActualDExperiment.ChunkBytes != 0) throw new InvalidDataException("Invalid source chunk offset.");
                        reply["data"] = Convert.ToBase64String(bytes, offset, Math.Min(CanonicalActualDExperiment.ChunkBytes, bytes.Length - offset));
                    }
                    return reply;
                }
                var job = s0Observation ?? throw new InvalidOperationException("No zero-Movie target for actual D.");
                try {
                    if (phase == "d-exp-resume") {
                        if (fields.Count != 3 || job.Phase != "ActualDAppliedPaused" || actualDSource == null
                            || !clock.IsPaused || clock.IsFinished || frame != actualDSource.NativeFrame
                            || movieFrame != actualDSource.MovieFrame || input.ConsumedSamples != 0
                            || !long.TryParse(Get("endMovie"), out var end) || end <= movieFrame
                            || end > Math.Min(replayLength, movieFrame + 64))
                            throw new InvalidOperationException("Actual D suffix requires the single applied paused boundary and a bounded future endpoint.");
                        actualDSource.Clock.RequireReadback(clock);
                        LoadingSerialSceneProbe.GetProtectedObservationBoundary();
                        StartActualDSuffixOracle("target", end); SetPauseTarget(end, frame);
                        On.GameManager.FadeSceneInWithDelay -= OnS0BootstrapFade;
                        On.HeroController.Respawn -= OnS0BootstrapRespawn;
                        s0Observation = null; mode = "Replay"; job.Phase = "ResumingActualDSuffix";
                        reply["status"] = job.Phase; reply["endMovie"] = end.ToString(CultureInfo.InvariantCulture);
                        return reply;
                    }
                    if (job.Phase != "PausedAtB" || !clock.IsPaused || clock.IsFinished || actualDApplicationStarted)
                        throw new InvalidOperationException("Actual D requires its fresh zero-Movie paused target.");
                    AssertS0NoMovieConsumption(job);
                    var boundary = LoadingSerialSceneProbe.GetProtectedObservationBoundary();
                    job.Parked!.Require(frame, clock.IsPaused, boundary.Item1, boundary.Item2, boundary.Item3, boundary.Item2.SceneName, S0LoadInfoFields(boundary.Item2));
                    if (phase == "d-exp-import-begin") {
                        if (fields.Count != 4 || actualDImport != null || !int.TryParse(Get("length"), out var length)
                            || length < 1 || length > CanonicalActualDExperiment.MaximumBytes || !MovieProtocolV1.IsLowerSha256(Get("sha256")))
                            throw new InvalidDataException("Invalid bounded D import header.");
                        actualDImport = new byte[length]; actualDImportHash = Get("sha256");
                    } else if (phase == "d-exp-import-chunk") {
                        if (fields.Count != 4 || actualDImport == null || !int.TryParse(Get("offset"), out var offset)
                            || offset != actualDImportOffset) throw new InvalidDataException("Actual D chunks must be sequential.");
                        var chunk = Convert.FromBase64String(Get("data"));
                        if (chunk.Length != Math.Min(CanonicalActualDExperiment.ChunkBytes, actualDImport.Length - offset) || chunk.Length == 0)
                            throw new InvalidDataException("Actual D chunk length differs.");
                        Buffer.BlockCopy(chunk, 0, actualDImport, offset, chunk.Length); actualDImportOffset += chunk.Length;
                    } else if (phase == "d-exp-import-finish") {
                        if (fields.Count != 2 || actualDImport == null || actualDSource != null || actualDImportOffset != actualDImport.Length
                            || Sha256Utility.ComputeHex(actualDImport) != actualDImportHash) throw new InvalidDataException("Actual D import hash or complete length differs.");
                        actualDSource = CanonicalActualDExperiment.Decode(actualDImport); actualDImport = null;
                    } else if (phase == "d-exp-apply") {
                        if (fields.Count != 2 || actualDSource == null) throw new InvalidDataException("No sealed source D to apply.");
                        ApplyActualD(job, actualDSource, frame);
                    } else throw new InvalidDataException("Unknown actual D phase.");
                    reply["status"] = job.Phase; reply["received"] = actualDImportOffset.ToString(CultureInfo.InvariantCulture);
                    reply["movieFrame"] = movieFrame.ToString(CultureInfo.InvariantCulture); reply["consumedSamples"] = input.ConsumedSamples.ToString(CultureInfo.InvariantCulture);
                    return reply;
                } catch (Exception error) { FailS0Observation(job, error); throw; }
            });
        }
        private void StartActualDSuffixOracle(string role, long end)
        {
            if (actualDSuffixOracle != null || !movieInputNativeOrigin.HasValue)
                throw new InvalidOperationException("Actual D suffix observer requires the original input origin and single admission.");
            var rng = UnityRandomStateCodec_1_5_78_11833.Resolve().Codec
                ?? throw new InvalidOperationException("Actual D suffix RNG observer is unavailable.");
            actualDSuffixOracle = LoadingGameplayOracle.CreateActualDSuffix(
                Path.Combine(Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT")!, "actual-d-" + role + "-suffix-oracle"),
                sessionDirectory, movieFrame, Math.Min(end, movieFrame + 3), movieInputNativeOrigin.Value, () => movieFrame, () => clock.CurrentFrameIndex,
                () => rng.CaptureCurrent().Sha256);
        }
        private void ApplyActualD(S0ObservationJob job, CanonicalActualDExperiment source, long targetNative)
        {
            var session = source.Session ?? throw new InvalidDataException("Actual D source has no TAS continuation.");
            if (replayMovie == null || session.InitialSavesSha256 != checkpointInitialSavesSha256 || checkpointExecutionFiles == null
                || session.ExecutionFiles.Count != checkpointExecutionFiles.Count || checkpointExecutionFiles.Any(p =>
                    !session.ExecutionFiles.TryGetValue(p.Key, out var value) || value != p.Value)
                || session.MoviePrefixSha256 != Sha256Utility.ComputeHex(Encoding.UTF8.GetBytes(new MovieV2Codec().WriteCanonical(MovieV2Prefix.Take(replayMovie, source.MovieFrame))))
                || session.UnityRandom == null || session.UnityRandom.Length != 16
                || session.ParticleManagerRandom == null || session.ParticleManagerRandom.Length != 4 || session.ParticleManagerRandom.All(word => word == 0))
                throw new InvalidDataException("Actual D source Movie, original saves, execution identity or Unity RNG differs.");
            var seconds = new[] { BitConverter.Int64BitsToDouble(session.GameUpdateDeltaBits), BitConverter.Int64BitsToDouble(session.GameUpdateTimeBits),
                BitConverter.Int64BitsToDouble(session.ExecutedMovieSecondsBits) };
            if (session.SkippedLoadingFrames < 0 || session.TimingRunIndex < 0 || session.TimingRunIndex >= replayMovie.Runs.Count
                || session.TimingRunStart < 0 || session.TimingRunStart > source.MovieFrame
                || !MovieFrameRate.IsValid(session.ActiveFrameRate) || !MovieFrameRate.IsValid(session.RecordingFrameRate)
                || seconds.Any(value => double.IsNaN(value) || double.IsInfinity(value)) || seconds[0] < 0 || seconds[2] < 0
                || session.LastGameUpdateNativeFrame < -1 || session.LastGameUpdateNativeFrame > source.NativeFrame)
                throw new InvalidDataException("Actual D TAS cursor or raw timing fields differ.");
            var ownerAdmission = LoadingSerialSceneProbe.FreezeExperimentalOwnerAdmission();
            var applicationBudget = System.Diagnostics.Stopwatch.StartNew();
            var applicationStage = "prepare-target";
            var imported = false; var expectedOrigin = movieInputNativeOrigin;
            bool Admission() {
                ownerAdmission();
                if (applicationBudget.ElapsedMilliseconds >= 9000)
                    throw new TimeoutException("Actual D transaction exceeded its original bounded command budget at " + applicationStage + "; retire process.");
                return job.Phase == "ApplyingActualD" && clock.IsPaused && !clock.IsFinished && movieFrame == 0
                    && input.ConsumedSamples == 0 && movieInputNativeOrigin == expectedOrigin
                    && clock.CurrentFrameIndex == (imported ? source.NativeFrame : targetNative);
            }
            actualDApplicationStarted = true; job.Phase = "ApplyingActualD";
            var apply = LoadingSerialSceneProbe.PrepareExperimentalTarget(source, replayMovie, targetNative,
                job.Origin, movieInputControlOrigin!, input, mouse, Admission, epoch => {
                    LoadingSerialSceneProbe.RestoreExperimentalInputOrigin(epoch);
                    movieInputNativeOrigin = epoch.OriginNative; movieInputControlOrigin = epoch.Copy(); expectedOrigin = epoch.OriginNative;
                }, job.OriginalBootstrapPlayer, job.OriginalTitleMappingCallback);
            if (!Admission()) throw new InvalidOperationException("Actual D preparation lost its write admission.");
            applicationStage = "import-clock";
            source.Clock.Apply(clock, targetNative,
                (bytes, cursor) => CheckpointClockMethod("ImportCheckpointRandomState").Invoke(null, new object[] { bytes, cursor }),
                (bytes, cursor) => CheckpointClockMethod("ImportCheckpointClockObserver").Invoke(null, new object[] { bytes, cursor }));
            imported = true; job.AppliedClock = source.Clock; job.Parked = job.Parked!.WithVerifiedImportedClock(source.NativeFrame);
            applicationStage = "clock-binding";
            LoadingSerialSceneProbe.ReapplyProtectedClockCanon(0, source.NativeFrame, sessionDirectory, job.Origin,
                movieInputControlOrigin!, input, mouse, () => { if (!Admission()) throw new InvalidOperationException("Actual D clock binding lost paused admission."); });
            applicationStage = "apply-D";
            apply(); source.Clock.RequireReadback(clock);
            // The source manager Rand is residual D. Keep the actual target owner,
            // write only its four words after managed/derived construction and read back.
            CheckpointClockMethod("ImportCheckpointParticleRandomState").Invoke(null, new object[] { session.ParticleManagerRandom, source.NativeFrame });
            if (!LoadingGameplayOracle.CaptureParticleRandomState().SequenceEqual(session.ParticleManagerRandom))
                throw new InvalidOperationException("Actual D particle manager Rand raw readback differs.");
            var rng = UnityRandomStateCodec_1_5_78_11833.Resolve();
            if (!rng.Ready || rng.Codec == null) throw new InvalidOperationException("Actual D Unity RNG codec changed.");
            object state = default(UnityEngine.Random.State);
            for (var index = 0; index < 4; index++) {
                var offset = index * 4;
                var bits = unchecked((int)((uint)session.UnityRandom[offset] << 24 | (uint)session.UnityRandom[offset + 1] << 16
                    | (uint)session.UnityRandom[offset + 2] << 8 | session.UnityRandom[offset + 3]));
                typeof(UnityEngine.Random.State).GetField("s" + index, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(state, bits);
            }
            UnityEngine.Random.state = (UnityEngine.Random.State)state;
            if (!rng.Codec.Encode(UnityEngine.Random.state).SequenceEqual(session.UnityRandom)) throw new InvalidOperationException("Actual D Unity RNG raw readback differs.");
            movieFrame = source.MovieFrame; expectedNativeStart = source.NativeFrame; skippedLoadFrames = session.SkippedLoadingFrames;
            timingRunIndex = session.TimingRunIndex; timingRunStart = session.TimingRunStart; activeFrameRate = session.ActiveFrameRate;
            recordingFrameRate = session.RecordingFrameRate; titleReadySeen = session.TitleReadySeen; firstLevelActivationPending = session.FirstLevelActivationPending;
            randomSeedSceneName = session.RandomSeedScene; randomSeedSceneHandle = -1; randomSeedHandshakePending = false;
            randomSeedRequest?.Dispose(); randomSeedAcknowledged?.Dispose(); randomSeedRequest = null; randomSeedAcknowledged = null;
            lastGameUpdateNativeFrame = session.LastGameUpdateNativeFrame; gameUpdateDeltaSeconds = BitConverter.Int64BitsToDouble(session.GameUpdateDeltaBits);
            gameUpdateTimeSeconds = BitConverter.Int64BitsToDouble(session.GameUpdateTimeBits); executedMovieSeconds = BitConverter.Int64BitsToDouble(session.ExecutedMovieSecondsBits);
            if (JsonConvert.SerializeObject(CaptureActualSessionCarry(clock.CurrentFrameIndex)) != JsonConvert.SerializeObject(session))
                throw new InvalidOperationException("Actual D TAS continuation complete raw readback differs.");
            job.Phase = "ActualDAppliedPaused";
            File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT")!, "actual-d-application.json"),
                JsonConvert.SerializeObject(new { applicationExecuted = true, source.MovieFrame, source.NativeFrame,
                    consumedSamples = input.ConsumedSamples, sourcePrefix = session.MoviePrefixSha256, sourceDBytes = source.Main.Length + source.Input.Length,
                    rawClockReadbackEqual = true, unityRandomRawReadbackEqual = true, particleManagerRandomRawReadbackEqual = true, particleComponentStateQualified = false, restoreQualified = false, completeStateCoverage = false, d2Passed = false, d3Passed = false }));
        }
    }
}
