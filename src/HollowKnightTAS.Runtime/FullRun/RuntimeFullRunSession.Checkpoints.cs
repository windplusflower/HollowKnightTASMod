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
        private string checkpointFailure = "";
        private string checkpointCaptureId = "";
        private static readonly CheckpointDataCodec CheckpointCodec = new CheckpointDataCodec(
            new[] { typeof(PlayerData), typeof(SceneData), typeof(CheckpointTransitionData), typeof(GameManager.SceneLoadInfo) }, new[] { typeof(UnityEngine.Vector3) });

        public Dictionary<string, string> CheckpointCommand(IReadOnlyDictionary<string, string> fields, string environmentSha256)
        {
            if (Environment.GetEnvironmentVariable("HKTAS_SCENE_CHECKPOINT_EXPERIMENT") != "1")
                throw new InvalidOperationException("Scene checkpoint development is disabled.");
            string Get(string key) => fields.TryGetValue(key, out var value) ? value : throw new InvalidDataException("Missing checkpoint command field: " + key);
            void Require(params string[] keys)
            {
                if (fields.Count != keys.Length + 2 || keys.Any(k => !fields.ContainsKey(k)))
                    throw new InvalidDataException("Unexpected checkpoint command fields.");
            }
            var reply = new Dictionary<string, string>(StringComparer.Ordinal) { ["requestId"] = Get("requestId") };
            lock (checkpointSync)
            {
                if (disposed) throw new ObjectDisposedException(nameof(RuntimeFullRunSession));
                switch (Get("phase"))
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
                        if (binding.EnvironmentSha256 != environmentSha256 || binding.NativeProfileId != NativeExecutionProfile.SelectedProfileId)
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
                        if (checkpointImport != null || stagedCheckpoint != null) throw new InvalidOperationException("A checkpoint import is already present.");
                        if (header.Binding.EnvironmentSha256 != environmentSha256 || header.Binding.NativeProfileId != NativeExecutionProfile.SelectedProfileId)
                            throw new InvalidDataException("Checkpoint target execution environment mismatch.");
                        // P1 only stages data. P3 must additionally attest the protected initial-save context before applying it.
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
                            CheckpointCodec.Decode<PlayerData>(sections["playerData"]); CheckpointCodec.Decode<SceneData>(sections["sceneData"]);
                            CheckpointCodec.Decode<CheckpointTransitionData>(sections["transition"]);
                            stagedCheckpoint = imported; reply["status"] = "Staged"; reply["checkpointId"] = imported.Header.CheckpointId;
                            reply["restoreQualified"] = "false";
                        }
                        finally { receiver.Dispose(); checkpointImport = null; }
                        break;
                    case "import-cancel":
                        Require("checkpointId");
                        if (checkpointImport?.Header.CheckpointId == Get("checkpointId")) { checkpointImport.Dispose(); checkpointImport = null; }
                        if (stagedCheckpoint?.Header.CheckpointId == Get("checkpointId")) stagedCheckpoint = null;
                        reply["status"] = "ImportCancelled"; break;
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
                    var rng = UnityRandomStateCodec_1_5_78_11833.Resolve();
                    if (!rng.Ready || rng.Codec == null) throw new InvalidDataException("Unity RNG checkpoint codec is unavailable.");
                    // All live reads end here, on C's game-thread boundary. The worker receives only owned byte arrays.
                    var sections = new Dictionary<string, byte[]>(StringComparer.Ordinal)
                    {
                        ["playerData"] = CheckpointCodec.Encode(PlayerData.instance ?? throw new InvalidDataException("PlayerData is missing.")),
                        ["sceneData"] = CheckpointCodec.Encode(GameManager.instance?.sceneData ?? throw new InvalidDataException("SceneData is missing.")),
                        ["transition"] = CheckpointCodec.Encode(new CheckpointTransitionData { SourceScene = boundary.Item3, LoadInfo = boundary.Item2 }),
                        ["unityRng"] = rng.Codec.Encode(UnityEngine.Random.state)
                    };
                    frozenCheckpoint = new FrozenCheckpoint(checkpointArm, boundary.Item1, movieFrame, nativeFrame, sections);
                    // Both policies submit a single eligible-boundary request. P4 owns automatic re-arming.
                    checkpointArm = null;
                    Modding.Logger.LogDebug("[HKTAS] Checkpoint data frozen at C: Movie=" + movieFrame + " native=" + nativeFrame + " transition=" + boundary.Item1);
                }
                catch (Exception exception)
                {
                    checkpointFailure = exception.GetType().Name; checkpointArm = null; frozenCheckpoint = null;
                    Modding.Logger.LogWarn("[HKTAS] Checkpoint capture rejected: " + exception.GetType().Name);
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
                    var bytes = SceneCheckpointPacket.EncodeSections(frozen.Sections);
                    var header = new SceneCheckpointHeader(frozen.Arm.CaptureId, frozen.Arm.Scope, frozen.Arm.Binding,
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
        { lock (checkpointSync) { ClearCheckpointCapture(); checkpointImport?.Dispose(); checkpointImport = null; stagedCheckpoint = null; } }
        internal void DiscardCheckpointTransfersOnDisconnect() => DisposeSceneCheckpoints();
        private sealed class CheckpointArm
        {
            internal CheckpointArm(SceneCheckpointScope scope, SceneCheckpointBinding binding, SceneCheckpointTrigger trigger, string captureId)
            { Scope = scope; Binding = binding; Trigger = trigger; CaptureId = captureId; }
            internal SceneCheckpointScope Scope { get; }
            internal SceneCheckpointBinding Binding { get; }
            internal SceneCheckpointTrigger Trigger { get; }
            internal string CaptureId { get; }
        }
        [Serializable]
        private sealed class CheckpointTransitionData
        {
            public string SourceScene = "";
            public GameManager.SceneLoadInfo LoadInfo = null!;
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
