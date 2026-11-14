using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Movie;
using Newtonsoft.Json;

namespace HollowKnightTAS.Runtime.FullRun
{
    public sealed partial class RuntimeFullRunSession
    {
        private CanonicalSealedSource<ResidentTransferEnvelope>? residentExport;
        private bool residentTransferApplied;
        private LoadingSerialSceneProbe.ResidentLoaderBoundary? residentImportLoader;
        private long residentParkedNative = -1;
        private sealed class ResidentTransferEnvelope
        {
            internal readonly byte[] Packet;
            internal readonly long Native, Movie, ConsumedSamples;
            internal readonly int Ordinal;
            internal readonly string Source, Target;
            internal ResidentTransferEnvelope(byte[] packet, long native, long movie, long consumedSamples, int ordinal, string source, string target)
            { Packet = packet; Native = native; Movie = movie; ConsumedSamples = consumedSamples; Ordinal = ordinal; Source = source; Target = target; }
            internal string Metadata() => JsonConvert.SerializeObject(new { Native, Movie, ConsumedSamples, Ordinal, Source, Target, length = Packet.Length });
        }
        internal bool TryParkResidentImport(long native, int ordinal, string source, string target)
        {
            if (!CanonicalActualDExperiment.Enabled || CanonicalActualDExperiment.CaptureSource || residentParkedNative >= 0
                || ordinal != ResidentTargetOrdinal()) return false;
            RequireResidentSession();
            if (mode != "Replay" || native != clock.CurrentFrameIndex || residentImportLoader != null)
                throw ResidentError("import parking requires the independently replayed target loader at the current native frame");
            var loader = LoadingSerialSceneProbe.CaptureResidentBoundary(); loader.RequireComplete();
            if (loader.Ordinal != ordinal || loader.Source != source || loader.Target != target)
                throw ResidentError("actual target boundary changed before parking");
            residentBoundaryOrdinal = ordinal; residentBoundarySource = source; residentBoundaryTarget = target;
            residentBoundaryPacket = null; residentTraceOrigin = null;
            residentImportLoader = loader; residentParkedNative = native;
            input.SetNativeFrameActive(false); mode = "DTargetPaused"; clock.RequestPause();
            Modding.Logger.Log("[HKTAS] Resident import parked before common Canon: ordinal=" + ordinal + ";native=" + native);
            return true;
        }
        private bool TryParkResidentSource(long native)
        {
            if (!CanonicalActualDExperiment.Enabled || !CanonicalActualDExperiment.CaptureSource || residentExport != null
                || residentParkedNative >= 0 || residentBoundaryOrdinal != CanonicalActualDExperiment.SelectedOrdinal) return false;
            var packet = residentBoundaryPacket ?? throw ResidentError("common S0 did not publish a complete boundary packet");
            if (packet.Length == 0 || packet.Length > CanonicalActualDWire.MaximumBytes || native != clock.CurrentFrameIndex)
                throw ResidentError("boundary packet length or native frame differs");
            var envelope = new ResidentTransferEnvelope(packet, native, movieFrame, input.ConsumedSamples, residentBoundaryOrdinal, residentBoundarySource, residentBoundaryTarget);
            residentExport = new CanonicalSealedSource<ResidentTransferEnvelope>(envelope, value => value.Packet, value => value.Metadata());
            residentParkedNative = native; input.SetNativeFrameActive(false);
            mode = "DSourcePaused";
            clock.RequestPause();
            Modding.Logger.Log("[HKTAS] Resident common S0 parked: role=" + mode + ";ordinal=" + residentBoundaryOrdinal + ";movie=" + movieFrame
                + ";native=" + native + ";packetBytes=" + packet.Length + ";restoreQualified=false");
            return true;
        }
        private static int ResidentTargetOrdinal()
        {
            var text = Environment.GetEnvironmentVariable("HKTAS_LOADING_ACTUAL_D_TARGET_ORDINAL");
            if (string.IsNullOrEmpty(text)) return 1;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1 || value > 10000)
                throw ResidentError("target boundary ordinal is invalid");
            return value;
        }
        private Dictionary<string, string> ActualDExperimentCommand(IReadOnlyDictionary<string, string> fields)
        {
            if (!CanonicalActualDExperiment.Enabled || !fields.TryGetValue("phase", out var phase) || !fields.TryGetValue("requestId", out var request))
                throw ResidentError("resident transfer command is disabled or malformed");
            return observationQueue.Invoke(frame => {
                try {
                    RequireResidentSession();
                    if (!clock.IsPaused || clock.IsFinished || residentParkedNative < 0 || frame != clock.CurrentFrameIndex
                        || !residentTransferApplied && frame != residentParkedNative || mode != "DSourcePaused" && mode != "DTargetPaused")
                        throw ResidentError("transfer is outside its one paused native boundary");
                    (residentImportLoader ?? LoadingSerialSceneProbe.CaptureResidentBoundary()).RequireComplete();
                    string Get(string key) => fields.TryGetValue(key, out var value) ? value : throw ResidentError("missing transfer field: " + key);
                    void Count(int count) { if (fields.Count != count) throw ResidentError("transfer field set differs"); }
                    var reply = new Dictionary<string, string> { ["requestId"] = request, ["schema"] = ResidentBoundaryCoordinator.Schema,
                        ["restoreQualified"] = "false", ["completeStateCoverage"] = "false" };
                    if (phase == "d-exp-source-info" || phase == "d-exp-source-chunk" || phase == "d-exp-source-resume") {
                        if (mode != "DSourcePaused" || residentExport == null || residentBoundaryPacket == null) throw ResidentError("sealed resident source is absent");
                        ResidentTransferEnvelope source;
                        if (phase == "d-exp-source-chunk") {
                            Count(3); if (!int.TryParse(Get("offset"), NumberStyles.None, CultureInfo.InvariantCulture, out var offset)) throw ResidentError("source chunk offset is invalid");
                            source = residentExport.ReadChunk(residentBoundaryPacket, offset, out var chunk); reply["data"] = Convert.ToBase64String(chunk);
                        } else source = phase == "d-exp-source-resume" ? residentExport.RequireCompleted(residentBoundaryPacket) : residentExport.Require(residentBoundaryPacket);
                        if (source.Native != frame || source.Movie != movieFrame || source.ConsumedSamples != input.ConsumedSamples) throw ResidentError("sealed source cursor changed");
                        if (phase == "d-exp-source-info") {
                            Count(2); reply["length"] = source.Packet.Length.ToString(CultureInfo.InvariantCulture);
                            reply["sha256"] = residentExport.Hash;
                        }
                        if (phase == "d-exp-source-resume") {
                            Count(3); ResumeResidentSuffix("source", Get("endMovie"), frame); reply["status"] = "ResumingOriginalSourceSuffix";
                        }
                        reply["movieFrame"] = source.Movie.ToString(CultureInfo.InvariantCulture); reply["nativeFrame"] = source.Native.ToString(CultureInfo.InvariantCulture);
                        reply["sourceOrdinal"] = source.Ordinal.ToString(CultureInfo.InvariantCulture); reply["sourceScene"] = source.Source; reply["targetScene"] = source.Target;
                        reply["consumedSamples"] = source.ConsumedSamples.ToString(CultureInfo.InvariantCulture);
                        return reply;
                    }
                    if (mode != "DTargetPaused" || CanonicalActualDExperiment.CaptureSource) throw ResidentError("command requires the independently parked target");
                    if (phase == "d-exp-target-info") {
                        Count(2); reply["status"] = "ResidentTargetPaused";
                        reply["canonPending"] = (residentImportLoader != null && !residentTransferApplied) ? "true" : "false";
                        reply["targetOrdinal"] = residentBoundaryOrdinal.ToString(CultureInfo.InvariantCulture);
                        reply["sourceScene"] = residentBoundarySource; reply["targetScene"] = residentBoundaryTarget;
                    } else if (phase == "d-exp-import-begin") {
                        Count(4);
                        if (actualDImport != null || actualDApplicationStarted || residentTransferApplied
                            || !int.TryParse(Get("length"), NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 1 || length > CanonicalActualDWire.MaximumBytes
                            || !MovieProtocolV1.IsLowerSha256(Get("sha256"))) throw ResidentError("bounded source packet header differs");
                        actualDImport = new byte[length]; actualDImportOffset = 0; actualDImportHash = Get("sha256");
                    } else if (phase == "d-exp-import-chunk") {
                        Count(4);
                        if (actualDImport == null || actualDApplicationStarted || !int.TryParse(Get("offset"), NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
                            || offset != actualDImportOffset || offset < 0 || offset >= actualDImport.Length) throw ResidentError("source chunks must be contiguous and single-use");
                        var chunk = Convert.FromBase64String(Get("data"));
                        if (chunk.Length != Math.Min(CanonicalActualDExperiment.ChunkBytes, actualDImport.Length - offset)) throw ResidentError("source chunk length differs");
                        Buffer.BlockCopy(chunk, 0, actualDImport, offset, chunk.Length); actualDImportOffset += chunk.Length;
                    } else if (phase == "d-exp-import-finish") {
                        Count(2); RequireResidentImportedPacket(); reply["status"] = "ResidentPacketReceived";
                    } else if (phase == "d-exp-apply") {
                        Count(2); RequireResidentImportedPacket();
                        if (actualDApplicationStarted || residentTransferApplied) throw ResidentError("target already entered restoration");
                        actualDApplicationStarted = true;
                        var elapsed = RestoreResidentBoundary(actualDImport!); residentTransferApplied = true;
                        reply["status"] = "ResidentDAppliedPaused";
                        reply["packetSha256"] = actualDImportHash;
                        reply["packetBytes"] = actualDImport!.Length.ToString(CultureInfo.InvariantCulture);
                        reply["d1Passed"] = "true"; reply["d2Passed"] = "true";
                        reply["elapsedMilliseconds"] = elapsed.ToString(CultureInfo.InvariantCulture);
                        // Native epoch admission continuously checks completed
                        // PlayerLoops and command sequence; import is the only
                        // admitted timeline change before the verified commit.
                        reply["noMovieTickDuringApply"] = "true";
                    } else if (phase == "d-exp-suffix") {
                        Count(3); if (!residentTransferApplied) throw ResidentError("target D has not been independently verified");
                        ResumeResidentSuffix("target", Get("endMovie"), clock.CurrentFrameIndex); reply["status"] = "ResumingResidentSuffix";
                    } else throw ResidentError("unknown resident transfer phase");
                    reply["received"] = actualDImportOffset.ToString(CultureInfo.InvariantCulture);
                    reply["movieFrame"] = movieFrame.ToString(CultureInfo.InvariantCulture); reply["nativeFrame"] = clock.CurrentFrameIndex.ToString(CultureInfo.InvariantCulture);
                    reply["consumedSamples"] = input.ConsumedSamples.ToString(CultureInfo.InvariantCulture);
                    return reply;
                } catch (Exception error) {
                    residentExport?.Fault(); residentExport = null;
                    if (actualDImport != null) Array.Clear(actualDImport, 0, actualDImport.Length);
                    actualDImport = null; FaultResidentSession(error); throw;
                }
            }, timeoutMilliseconds: phase == "d-exp-apply" ? 360000 : 30000);
        }
        private void RequireResidentImportedPacket()
        {
            if (actualDImport == null || actualDImportOffset != actualDImport.Length || Sha256Utility.ComputeHex(actualDImport) != actualDImportHash)
                throw ResidentError("complete in-memory source packet length/hash differs");
        }
        private void ResumeResidentSuffix(string role, string text, long native)
        {
            ResidentBoundaryProcessState.s_CachedFrameCatalog = null;
            ResidentBoundaryCoordinator.ClearFrameCaches();
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var end) || end <= movieFrame || end > replayLength)
                throw ResidentError("suffix endpoint is outside the exact continuation Movie");
            var origin = residentTraceOrigin ?? throw ResidentError("verified source boundary identity is absent");
            var packet = (role == "source" ? residentBoundaryPacket : actualDImport) ?? throw ResidentError("sealed source packet is absent");
            if (residentContinuationTrace != null || origin.MovieFrame != movieFrame || origin.InitialSaves != checkpointInitialSavesSha256
                || native != clock.CurrentFrameIndex || replayMovie == null || checkpointExecutionFiles == null || checkpointExecutionFiles.Count == 0)
                throw ResidentError("continuation trace does not begin at the verified original boundary");
            var traceOutput = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT");
            if (string.IsNullOrEmpty(traceOutput) || !Directory.Exists(traceOutput)) throw ResidentError("owned continuation evidence directory is absent");
            residentContinuationTrace = ResidentContinuationTrace.Open(Path.Combine(traceOutput, "resident-continuation.jsonl"), new ResidentContinuationTrace.Identity {
                Role = role, PacketSha256 = Sha256Utility.ComputeHex(packet), PacketBytes = packet.Length,
                MovieSha256 = Sha256Utility.ComputeUtf8Hex(new MovieV2Codec().WriteCanonical(replayMovie)), SaveSha256 = origin.InitialSaves,
                BuildSha256 = Sha256Utility.ComputeUtf8Hex(JsonConvert.SerializeObject(new { origin.Runtime, origin.Core, origin.Clock,
                    files = checkpointExecutionFiles.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray() })), BoundaryOrdinal = origin.BoundaryOrdinal,
                SourceScene = origin.SourceScene, TargetScene = origin.TargetScene, StartNative = native, StartMovie = movieFrame,
                StartConsumed = input.ConsumedSamples, EndMovie = end, MovieLength = replayLength });
            residentTraceEnd = end; SetPauseTarget(end, native); mode = "Replay";
        }
        private void ObserveResidentContinuation(int phase, long native)
        {
            var trace = residentContinuationTrace; if (trace == null) return;
            var thread = Thread.CurrentThread.ManagedThreadId; var movie = movieFrame; var consumed = input.ConsumedSamples;
            var ready = frameInputEnabled; var boundary = frameBoundary; var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            void RequireFrame() {
                if (thread != Thread.CurrentThread.ManagedThreadId || mode != "Replay" || !inputReady || disposed || error.Length != 0
                    || !ReferenceEquals(trace, residentContinuationTrace) || native != clock.CurrentFrameIndex || movie != movieFrame
                    || consumed != input.ConsumedSamples || ready != frameInputEnabled || boundary != frameBoundary
                    || scene.handle != UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle)
                    throw ResidentError("observed frame, input cursor or native phase changed");
            }
            ResidentBoundaryProcessState? process = null;
            try
            {
                (checkpointSaves ?? throw ResidentError("frame observer has no protected save context")).AssertActive();
                RequireFrame();
                process = ResidentBoundaryProcessState.OpenFrameObservation(this, phase, RequireFrame);
                var coordinator = new ResidentBoundaryCoordinator(CreateResidentOwnership(), ClassifyResidentModule, process);
                var groups = coordinator.ObserveContinuationFrame(phase);
                process.Complete();
                RequireFrame();
                trace.Sample(phase == 1 ? "before" : "after", native, movie, consumed, ready, boundary, scene.name, groups);
                if (phase == 3 && movie == residentTraceEnd) {
                    trace.Seal(native, movie, consumed); residentContinuationTrace = null;
                }
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(Path.Combine(sessionDirectory, "resident-continuation-fault.txt"),
                        $"phase={phase};native={native};movie={movie};consumed={consumed};ready={ready};boundary={boundary}\n{ex}\n");
                }
                catch { }
                if (process != null)
                {
                    try { process.Fault(ex); } catch { }
                }
                throw;
            }
            finally
            {
                process?.Dispose();
            }
        }
    }
}
