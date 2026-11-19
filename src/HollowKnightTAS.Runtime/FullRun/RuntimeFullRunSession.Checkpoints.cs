using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Core.SceneCheckpoints;
using HollowKnightTAS.Runtime.Rng;
using HollowKnightTAS.Runtime.Input;
using System.Reflection;
using HollowKnightTAS.Runtime.ReplaySave;
using Newtonsoft.Json;
using System.Text;

namespace HollowKnightTAS.Runtime.FullRun
{
    public sealed partial class RuntimeFullRunSession
    {
        private readonly object checkpointSync = new object();
        private CheckpointArm? checkpointArm;
        private FrozenCheckpoint? frozenCheckpoint;
        private Task<SceneCheckpointPacket>? checkpointEncoding;
        private SceneCheckpointReceiver? checkpointImport;
        private SceneCheckpointPacket? stagedCheckpoint;
        private LeanS0Packet? stagedLeanS0Packet;
        private string checkpointFailure = "";
        private string checkpointCaptureId = "";
        private string checkpointRestoreActiveId = "";
        private bool checkpointRestoreApplied;
        private bool checkpointRestoreCancelled;
        private Dictionary<string, string>? checkpointExecutionFiles;
        private ProtectedSaveRedirector? checkpointSaves;
        private string checkpointInitialSavesSha256 = "";
        internal void BindCheckpointSaveContext(ProtectedSaveRedirector saves)
        {
            if (checkpointSaves != null) throw new InvalidOperationException("Checkpoint save context is already bound.");
            saves.AssertActive();
            // Install verified the complete shadow file set before any game input.
            // Bind that immutable descriptor, never a caller-supplied current hash.
            checkpointInitialSavesSha256 = SceneCheckpointBinding.HashInitialSaves(saves.Descriptor.OriginalFileSha256);
            if (NativeExecutionProfile.SupportsSceneCheckpoints(NativeExecutionProfile.SelectedProfileId)
                || Environment.GetEnvironmentVariable("HKTAS_SCENE_CHECKPOINT_EXPERIMENT") == "1"
                || CanonicalActualDExperiment.Enabled)
            {
                checkpointExecutionFiles = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["runtime"] = Sha256Utility.ComputeHex(File.ReadAllBytes(typeof(RuntimeFullRunSession).Assembly.Location)),
                    ["core"] = Sha256Utility.ComputeHex(File.ReadAllBytes(typeof(SceneCheckpointPacket).Assembly.Location)),
                    ["clockPayload"] = Sha256Utility.ComputeHex(File.ReadAllBytes(CheckpointClockMethod("ExportCheckpointRandomState").DeclaringType!.Assembly.Location)),
                    ["clockBridge"] = clock.CheckpointBridgeSha256
                };
            }
            checkpointSaves = saves;
        }
        private static MethodInfo CheckpointClockMethod(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Single(assembly => assembly.GetName().Name == "HollowKnightTAS.ClockPayload")
            .GetType("HollowKnightTAS.ClockPayload.ClockController", true)!.GetMethod(name, BindingFlags.Static | BindingFlags.Public)
            ?? throw new MissingMethodException("Clock payload continuation method is unavailable: " + name);
        private void RequireCheckpointExecutionFiles(IReadOnlyDictionary<string, string>? executionFiles)
        {
            if (checkpointExecutionFiles == null || executionFiles == null || executionFiles.Count != checkpointExecutionFiles.Count
                || checkpointExecutionFiles.Any(file => !executionFiles.TryGetValue(file.Key, out var actual) || actual != file.Value))
                throw new InvalidDataException("Checkpoint execution assemblies or native bridge differ from the replacement.");
        }

        public Dictionary<string, string> CheckpointCommand(IReadOnlyDictionary<string, string> fields, string environmentSha256)
        {
            if (fields.TryGetValue("phase", out var leanS0Phase) && leanS0Phase.StartsWith("lean-s0-", StringComparison.Ordinal))
                return LeanS0Command(fields);
            if (fields.TryGetValue("phase", out var observationPhase) && observationPhase.StartsWith("s0-", StringComparison.Ordinal))
                return S0ObservationCommand(fields);
            CanonicalPhaseOneTrace.RequireCheckpointAllowed(CanonicalPhaseOneTrace.Enabled);
            if (!NativeExecutionProfile.SupportsSceneCheckpoints(NativeExecutionProfile.SelectedProfileId))
                throw new InvalidOperationException("This recorded execution profile supports full replay only; scene checkpoints require the canonical boundary profile.");
            string Get(string key) => fields.TryGetValue(key, out var value) ? value : throw new InvalidDataException("Missing checkpoint command field: " + key);
            void Require(params string[] keys)
            {
                if (fields.Count != keys.Length + 2 || keys.Any(k => !fields.ContainsKey(k)))
                    throw new InvalidDataException("Unexpected checkpoint command fields.");
            }
            string requestedPhase = Get("phase");
            if (requestedPhase == "restore-start" || requestedPhase == "restore-apply" || requestedPhase == "restore-cancel")
            {
                int timeoutMs = requestedPhase == "restore-apply" ? 120000 : 30000;
                return observationQueue.Invoke(frame =>
                {
                    var restoreReply = new Dictionary<string, string>(StringComparer.Ordinal) { ["requestId"] = Get("requestId") };
                    lock (checkpointSync)
                    {
                        if (disposed) throw new ObjectDisposedException(nameof(RuntimeFullRunSession));
                        switch (requestedPhase)
                        {
                            case "restore-start":
                            {
                                bool hasMoviePath = fields.ContainsKey("moviePath");
                                if (hasMoviePath) Require("checkpointId", "moviePath");
                                else Require("checkpointId");
                                string checkpointId = Get("checkpointId");
                                var packet = stagedLeanS0Packet;
                                if (stagedCheckpoint == null || stagedCheckpoint.Header.CheckpointId != checkpointId || packet == null)
                                    throw new InvalidOperationException("Target LeanS0 checkpoint must be staged before restore-start.");
                                if ((mode != "Replay" && mode != "Recording") || !inputReady || !clock.IsPaused || clock.IsFinished)
                                    throw new InvalidOperationException("Checkpoint restore requires a paused Replay or Recording process.");
                                if (hasMoviePath)
                                {
                                    var root = Path.GetFullPath(Path.Combine(sessionDirectory, "..", "..")) + Path.DirectorySeparatorChar;
                                    var moviePath = Path.GetFullPath(Get("moviePath"));
                                    if (!moviePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                                        || !File.Exists(moviePath)
                                        || new FileInfo(moviePath).Length > MovieProtocolV2.MaximumSourceUtf8Bytes)
                                        throw new InvalidDataException("Checkpoint restore moviePath must be inside the protected session.");
                                    MovieV2Document candidate;
                                    using (var reader = File.OpenText(moviePath))
                                        candidate = new MovieV2Codec().Parse(reader, moviePath).Document
                                            ?? throw new InvalidDataException("Invalid checkpoint restore Movie.");
                                    var validation = new MovieV2Validator().Validate(candidate, MovieV2ValidationContext.CreateDefault());
                                    if (!validation.Success)
                                        throw new InvalidDataException("Invalid checkpoint restore Movie.");
                                    if (candidate.Header.NativeProfileId != NativeExecutionProfile.SelectedProfileId)
                                        throw new InvalidDataException("Checkpoint restore Movie execution environment mismatch.");
                                    long candidateLength = CountFrames(candidate);
                                    if (candidateLength < stagedCheckpoint.Header.ResumeMovieFrame)
                                        throw new InvalidDataException("Checkpoint restore Movie is shorter than the checkpoint resume frame.");
                                    if (mode == "Recording")
                                        input.Sampled -= OnSampled;
                                    input.SwitchToReplayForInProcessRestore(candidate);
                                    mouse.UseReplayInputs();
                                    replayMovie = candidate;
                                    editableReplay = true;
                                    replayLength = candidateLength;
                                    mode = "Replay";
                                }
                                else
                                {
                                    if (mode != "Replay" || (movieFrame == 0 && input.ConsumedSamples != 0))
                                        throw new InvalidOperationException("Checkpoint restore requires a Replay process at a valid boundary.");
                                }
                                ClearCheckpointCapture();
                                restoreDrawing.Dispose();
                                pauseAtMovieFrame = -1;
                                int slot = ExtractSaveSlotFromPlayerDataJson(packet.C1_PlayerDataJson);
                                checkpointRestoreActiveId = checkpointId;
                                checkpointRestoreApplied = false;
                                checkpointRestoreCancelled = false;
                                BeginLeanS0TargetSaveLoad(packet, frame, slot);
                                restoreReply["status"] = "LoadingInitialSave";
                                restoreReply["checkpointId"] = checkpointId;
                                restoreReply["restoreQualified"] = "true";
                                return restoreReply;
                            }
                            case "restore-apply":
                            {
                                string checkpointId = Get("checkpointId");
                                bool hasTarget = fields.ContainsKey("targetMovieFrame");
                                if (hasTarget) Require("checkpointId", "targetMovieFrame");
                                else Require("checkpointId");
                                if (stagedCheckpoint == null || stagedCheckpoint.Header.CheckpointId != checkpointId || leanS0TargetPacket == null)
                                    throw new InvalidOperationException("Target LeanS0 checkpoint is absent for restore-apply.");
                                if (mode != "LeanS0TargetPaused" || !clock.IsPaused || clock.IsFinished)
                                    throw new InvalidOperationException("Target process must be parked at target S0 before restore-apply.");
                                var packet = leanS0TargetPacket;
                                bool wasInProcess = leanS0TargetInProcess;
                                ApplyLeanS0PacketAtTargetS0(packet, frame);
                                long targetMovie = hasTarget ? long.Parse(Get("targetMovieFrame"), NumberStyles.None, CultureInfo.InvariantCulture) : movieFrame;
                                if (targetMovie > movieFrame)
                                {
                                    SetPauseTarget(targetMovie, packet.C12_ControlPlane.NativeFrame);
                                    leanS0TargetResumeNativeFrame = -1;
                                }
                                else if (targetMovie == movieFrame)
                                {
                                    leanS0TargetResumeNativeFrame = stagedCheckpoint.Header.ResumeNativeFrame;
                                }
                                else
                                {
                                    throw new InvalidDataException($"Checkpoint target movie frame {targetMovie} precedes checkpoint frame {movieFrame}.");
                                }
                                leanS0TargetPacket = null;
                                checkpointRestoreApplied = true;
                                LoadingSerialSceneProbe.MarkLeanS0BoundaryObserved(reschedule: wasInProcess);
                                mode = "Replay";
                                restoreReply["status"] = "RebuildingCToK";
                                restoreReply["checkpointId"] = checkpointId;
                                restoreReply["restoreQualified"] = "true";
                                restoreReply["stateApplied"] = "true";
                                restoreReply["resumeMovieFrame"] = movieFrame.ToString(CultureInfo.InvariantCulture);
                                restoreReply["resumeNativeFrame"] = stagedCheckpoint.Header.ResumeNativeFrame.ToString(CultureInfo.InvariantCulture);
                                restoreReply["appliedNativeFrame"] = packet.C12_ControlPlane.NativeFrame.ToString(CultureInfo.InvariantCulture);
                                return restoreReply;
                            }
                            case "restore-cancel":
                            {
                                Require("checkpointId");
                                string checkpointId = Get("checkpointId");
                                if (stagedCheckpoint?.Header.CheckpointId == checkpointId || checkpointRestoreActiveId == checkpointId)
                                {
                                    stagedCheckpoint = null;
                                    stagedLeanS0Packet = null;
                                    leanS0TargetPacket = null;
                                    leanS0TargetArmed = false;
                                    leanS0TargetParked = false;
                                    leanS0TargetInProcess = false;
                                    leanS0TargetResumeNativeFrame = -1;
                                    leanS0TargetJob = null;
                                    checkpointRestoreActiveId = "";
                                    checkpointRestoreCancelled = true;
                                    DetachLeanS0TargetHooks();
                                }
                                restoreReply["status"] = "Cancelled";
                                restoreReply["checkpointId"] = checkpointId;
                                return restoreReply;
                            }
                        }
                    }
                    return restoreReply;
                }, timeoutMilliseconds: timeoutMs);
            }
            var reply = new Dictionary<string, string>(StringComparer.Ordinal) { ["requestId"] = Get("requestId") };
            lock (checkpointSync)
            {
                if (disposed) throw new ObjectDisposedException(nameof(RuntimeFullRunSession));
                switch (requestedPhase)
                {
                    case "arm":
                        Require("captureId", "studioSessionId", "treeId", "branchId", "generation", "environmentSha256", "initialSavesSha256", "nativeProfileId", "trigger");
                        if ((mode != "Replay" && mode != "Recording") || !NativeExecutionProfile.UsesSerialScenes(NativeExecutionProfile.SelectedProfileId))
                            throw new InvalidOperationException("Checkpoint capture requires an active serial-scene Movie session.");
                        if (checkpointArm != null || frozenCheckpoint != null || checkpointEncoding != null)
                            throw new InvalidOperationException("Release or cancel the previous checkpoint capture first.");
                        var scope = new SceneCheckpointScope(Get("studioSessionId"), Get("treeId"), Get("branchId"),
                            long.Parse(Get("generation"), NumberStyles.None, CultureInfo.InvariantCulture));
                        var binding = new SceneCheckpointBinding(Get("environmentSha256"), Get("initialSavesSha256"), Get("nativeProfileId"));
                        if (binding.EnvironmentSha256 != environmentSha256 || binding.NativeProfileId != NativeExecutionProfile.SelectedProfileId
                            || binding.InitialSavesSha256 != checkpointInitialSavesSha256)
                            throw new InvalidDataException("Checkpoint execution environment differs from the active Runtime.");
                        if (!Enum.TryParse<SceneCheckpointTrigger>(Get("trigger"), out var trigger) || trigger.ToString() != Get("trigger")
                            || !Enum.IsDefined(typeof(SceneCheckpointTrigger), trigger)) throw new InvalidDataException("Unknown checkpoint capture trigger.");
                        var captureId = Get("captureId");
                        if (!Guid.TryParseExact(captureId, "N", out _) || captureId != captureId.ToLowerInvariant()) throw new InvalidDataException("Invalid checkpoint capture ID.");
                        checkpointCaptureId = captureId;
                        checkpointArm = new CheckpointArm(scope, binding, trigger, captureId); checkpointFailure = "";
                        reply["status"] = "Pending"; break;
                    case "status":
                        Require("captureId"); RequireCheckpointCapture(Get("captureId"));
                        if (checkpointFailure.Length != 0) { reply["status"] = "Rejected"; reply["detail"] = checkpointFailure; }
                        else if (checkpointEncoding?.IsFaulted == true)
                        { reply["status"] = "Rejected"; reply["detail"] = checkpointEncoding.Exception!.GetBaseException().GetType().Name; }
                        else if (checkpointEncoding?.Status == TaskStatus.RanToCompletion)
                        { reply = checkpointEncoding.Result.Header.ToFields(); reply["requestId"] = Get("requestId"); reply["status"] = "Captured"; }
                        else reply["status"] = checkpointArm != null || frozenCheckpoint != null || checkpointEncoding != null ? "Pending" : "Empty";
                        break;
                    case "chunk":
                        Require("checkpointId", "index"); RequireCheckpointCapture(Get("checkpointId"));
                        var packet = CapturedCheckpoint(Get("checkpointId"));
                        var index = int.Parse(Get("index"), NumberStyles.None, CultureInfo.InvariantCulture);
                        reply["status"] = "Chunk"; reply["checkpointId"] = packet.Header.CheckpointId;
                        reply["index"] = index.ToString(CultureInfo.InvariantCulture); reply["data"] = Convert.ToBase64String(packet.ReadChunk(index)); break;
                    case "release":
                        Require("checkpointId"); CapturedCheckpoint(Get("checkpointId"));
                        ClearCheckpointCapture(); reply["status"] = "Released"; break;
                    case "cancel":
                        Require("captureId"); if (checkpointCaptureId == Get("captureId")) ClearCheckpointCapture();
                        reply["status"] = "Cancelled"; break;
                    case "import-begin":
                        var header = SceneCheckpointHeader.FromFields(fields);
                        Require(header.ToFields().Keys.ToArray());
                        if (!header.RestoreQualified || header.Schema != SceneCheckpointHeader.LeanS0SchemaVersion)
                            throw new InvalidDataException("Only qualified Lean S0 scene checkpoints can be staged for restore.");
                        if (checkpointImport != null) throw new InvalidOperationException("A checkpoint import is already present.");
                        if (stagedCheckpoint != null)
                        {
                            if (mode == "LeanS0TargetLoading" || mode == "LeanS0TargetPaused")
                                throw new InvalidOperationException("A checkpoint import is already active.");
                            stagedCheckpoint = null;
                            stagedLeanS0Packet = null;
                            leanS0TargetPacket = null;
                            leanS0TargetJob = null;
                            checkpointRestoreActiveId = "";
                            checkpointRestoreApplied = false;
                            checkpointRestoreCancelled = false;
                        }
                        if (header.Binding.EnvironmentSha256 != environmentSha256 || header.Binding.NativeProfileId != NativeExecutionProfile.SelectedProfileId
                            || header.Binding.InitialSavesSha256 != checkpointInitialSavesSha256)
                            throw new InvalidDataException("Checkpoint target execution environment mismatch.");
                        checkpointSaves!.AssertActive();
                        checkpointImport = new SceneCheckpointReceiver(header, header.Scope, header.Binding);
                        reply["status"] = "ImportPending"; break;
                    case "import-chunk":
                        Require("checkpointId", "index", "data");
                        if (checkpointImport == null || checkpointImport.Header.CheckpointId != Get("checkpointId")) throw new InvalidDataException("Checkpoint import ID differs.");
                        if (Get("data").Length > ((SceneCheckpointPacket.ChunkBytes + 2) / 3) * 4) throw new InvalidDataException("Checkpoint chunk exceeds its wire bound.");
                        try { (checkpointImport ?? throw new InvalidOperationException("No checkpoint import is pending.")).Append(
                            int.Parse(Get("index"), NumberStyles.None, CultureInfo.InvariantCulture), Convert.FromBase64String(Get("data"))); }
                        catch { checkpointImport?.Dispose(); checkpointImport = null; throw; }
                        reply["status"] = "ImportPending"; break;
                    case "import-end":
                        Require("checkpointId");
                        var receiver = checkpointImport ?? throw new InvalidOperationException("No checkpoint import is pending.");
                        if (receiver.Header.CheckpointId != Get("checkpointId")) throw new InvalidDataException("Checkpoint import ID differs.");
                        try
                        {
                            var imported = receiver.Complete(); var sections = imported.CopySections();
                            if (!imported.Header.RestoreQualified || imported.Header.Schema != SceneCheckpointHeader.LeanS0SchemaVersion)
                                throw new InvalidDataException("Unsupported checkpoint schema for Lean S0 restore.");
                            var leanS0 = LeanS0PacketCodec.Decode(sections["leanS0"]);
                            if (leanS0.C12_ControlPlane.MovieFrame != imported.Header.CaptureMovieFrame
                                || leanS0.C12_ControlPlane.NativeFrame != imported.Header.CaptureNativeFrame)
                                throw new InvalidDataException("Checkpoint Lean S0 cursor mismatches header.");
                            _ = ExtractSaveSlotFromPlayerDataJson(leanS0.C1_PlayerDataJson);
                            var executionFiles = JsonConvert.DeserializeObject<Dictionary<string, string>>(Encoding.UTF8.GetString(sections["executionFiles"]));
                            RequireCheckpointExecutionFiles(executionFiles);
                            stagedCheckpoint = imported;
                            stagedLeanS0Packet = leanS0;
                            checkpointRestoreActiveId = imported.Header.CheckpointId;
                            checkpointRestoreApplied = false;
                            checkpointRestoreCancelled = false;
                            reply["status"] = "Staged"; reply["checkpointId"] = imported.Header.CheckpointId;
                            reply["restoreQualified"] = "true";
                        }
                        finally { receiver.Dispose(); checkpointImport = null; }
                        break;
                    case "import-cancel":
                        Require("checkpointId");
                        if (checkpointImport?.Header.CheckpointId == Get("checkpointId")) { checkpointImport.Dispose(); checkpointImport = null; }
                        if (stagedCheckpoint?.Header.CheckpointId == Get("checkpointId"))
                        { stagedCheckpoint = null; stagedLeanS0Packet = null; }
                        reply["status"] = "ImportCancelled"; break;
                    case "restore-status":
                    {
                        Require("checkpointId");
                        string checkpointId = Get("checkpointId");
                        if (checkpointRestoreActiveId != checkpointId && stagedCheckpoint?.Header.CheckpointId != checkpointId)
                            throw new InvalidDataException("Checkpoint restore ID differs.");
                        reply["checkpointId"] = checkpointId;
                        reply["restoreQualified"] = (stagedCheckpoint?.Header.RestoreQualified ?? false) ? "true" : "false";
                        reply["stateApplied"] = checkpointRestoreApplied ? "true" : "false";
                        if (error.Length != 0 || leanS0TargetJob?.Phase == "Failed")
                        {
                            reply["status"] = "Failed";
                            reply["detail"] = error.Length != 0 ? error : leanS0TargetJob?.Error ?? string.Empty;
                        }
                        else if (checkpointRestoreCancelled) reply["status"] = "Cancelled";
                        else if (mode == "LeanS0TargetPaused" || leanS0TargetJob?.Phase == "PausedAtTargetS0") reply["status"] = "AwaitingApplyAtC";
                        else if (mode == "LeanS0TargetLoading") reply["status"] = "LoadingInitialSave";
                        else if (checkpointRestoreApplied) reply["status"] = "RebuildingCToK";
                        else reply["status"] = "Staged";
                        break;
                    }
                    default: throw new InvalidDataException("Unknown checkpoint command phase.");
                }
            }
            return reply;
        }

        private SceneCheckpointPacket CapturedCheckpoint(string id)
        {
            if (checkpointEncoding?.Status != TaskStatus.RanToCompletion || checkpointEncoding.Result.Header.CheckpointId != id)
                throw new InvalidOperationException("Checkpoint is not completely captured or its ID differs.");
            return checkpointEncoding.Result;
        }
        private void RequireCheckpointCapture(string id)
        { if (checkpointCaptureId != id) throw new InvalidDataException("Checkpoint capture ID differs."); }
        private void CaptureSceneCheckpointAtBoundary(long nativeFrame)
        {
            lock (checkpointSync)
            {
                if (checkpointArm == null || frozenCheckpoint != null || checkpointEncoding != null || checkpointFailure.Length != 0) return;
                try
                {
                    var boundary = LoadingSerialSceneProbe.GetCheckpointBoundary();
                    if (boundary.Item2.IsFirstLevelForPlayer || boundary.Item3 == "Menu_Title" || HeroController.SilentInstance == null) return;
                    if (checkpointExecutionFiles == null) throw new InvalidOperationException("Checkpoint execution identities are absent.");
                    loadingBoundaryProbe?.Sample("checkpoint-c", boundary.Item2.SceneName);
                    loadingBoundaryProbe?.IncludeCheckpointK(movieFrame);
                    checkpointSaves!.AssertActive();
                    var captureElapsed = System.Diagnostics.Stopwatch.StartNew();
                    var ctx = BuildLeanS0CaptureContext(boundary.Item1, boundary.Item3, boundary.Item2.SceneName, boundary.Item2, nativeFrame);
                    LeanS0Packet packet = LeanS0SnapshotRuntime.Capture(ctx);
                    byte[] leanS0Bytes = LeanS0PacketCodec.Encode(packet);
                    byte[] executionBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(checkpointExecutionFiles));
                    var sections = new Dictionary<string, byte[]>(StringComparer.Ordinal)
                    {
                        ["leanS0"] = leanS0Bytes,
                        ["executionFiles"] = executionBytes
                    };
                    frozenCheckpoint = new FrozenCheckpoint(checkpointArm, boundary.Item1, movieFrame, nativeFrame, sections);
                    checkpointArm = null;
                    Modding.Logger.Log("[HKTAS] LeanS0 checkpoint C data frozen: wallMs=" + captureElapsed.ElapsedMilliseconds
                        + " leanS0Bytes=" + leanS0Bytes.Length);
                    Modding.Logger.LogDebug("[HKTAS] LeanS0 checkpoint data frozen at C: Movie=" + movieFrame + " native=" + nativeFrame + " transition=" + boundary.Item1);
                }
                catch (Exception exception)
                {
                    checkpointFailure = exception.GetBaseException().GetType().Name + ": " + exception.GetBaseException().Message;
                    checkpointArm = null; frozenCheckpoint = null;
                    Modding.Logger.LogWarn("[HKTAS] Checkpoint capture rejected: " + checkpointFailure);
                }
            }
        }
        private void CompleteSceneCheckpointBoundary(long nativeFrame)
        {
            lock (checkpointSync)
            {
                if (frozenCheckpoint == null || checkpointEncoding != null) return;
                var frozen = frozenCheckpoint; var resumeFrame = movieFrame;
                if (frozen.MovieFrame != resumeFrame)
                { checkpointFailure = "Movie input was consumed between C and K."; checkpointArm = null; frozenCheckpoint = null; return; }
                checkpointEncoding = Task.Run(() =>
                {
                    var bytes = SceneCheckpointPacket.EncodeSections(frozen.Sections, SceneCheckpointHeader.LeanS0SchemaVersion);
                    var header = SceneCheckpointHeader.CreateLeanS0(frozen.Arm.CaptureId, frozen.Arm.Scope, frozen.Arm.Binding,
                        frozen.Arm.Trigger, frozen.TransitionId, frozen.MovieFrame, frozen.NativeFrame, resumeFrame, nativeFrame,
                        bytes.Length, Sha256Utility.ComputeHex(bytes));
                    return new SceneCheckpointPacket(header, bytes);
                });
                frozenCheckpoint = null;
            }
        }
        private void ClearCheckpointCapture()
        { checkpointArm = null; frozenCheckpoint = null; checkpointEncoding = null; checkpointFailure = ""; checkpointCaptureId = ""; }
        private void DisposeSceneCheckpoints()
        {
            On.GameManager.FadeSceneInWithDelay -= OnS0BootstrapFade;
            On.HeroController.Respawn -= OnS0BootstrapRespawn;
            DetachLeanS0TargetHooks();
            lock (checkpointSync) { ClearCheckpointCapture(); checkpointImport?.Dispose(); checkpointImport = null; stagedCheckpoint = null; stagedLeanS0Packet = null; }
        }
        internal void DiscardCheckpointTransfersOnDisconnect()
        {
            if (s0Observation != null && s0Observation.Phase != "Failed")
            { s0Observation.Phase = "Failed"; s0Observation.Error = "S0 observer disconnected; retire process."; FaultCanonical(47); }
            DisposeSceneCheckpoints();
        }
        private sealed class CheckpointArm
        {
            internal CheckpointArm(SceneCheckpointScope scope, SceneCheckpointBinding binding, SceneCheckpointTrigger trigger, string captureId)
            { Scope = scope; Binding = binding; Trigger = trigger; CaptureId = captureId; }
            internal SceneCheckpointScope Scope { get; }
            internal SceneCheckpointBinding Binding { get; }
            internal SceneCheckpointTrigger Trigger { get; }
            internal string CaptureId { get; }
        }
        private sealed class FrozenCheckpoint
        {
            internal FrozenCheckpoint(CheckpointArm arm, int transitionId, long movieFrame, long nativeFrame, Dictionary<string, byte[]> sections)
            { Arm = arm; TransitionId = transitionId; MovieFrame = movieFrame; NativeFrame = nativeFrame; Sections = sections; }
            internal CheckpointArm Arm { get; }
            internal int TransitionId { get; }
            internal long MovieFrame { get; }
            internal long NativeFrame { get; }
            internal Dictionary<string, byte[]> Sections { get; }
        }
    }
}
