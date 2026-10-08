using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Ipc;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.Core.SceneCheckpoints
{
    public enum SceneCheckpointTrigger { ManualNextEligibleLoadBoundary, AutoOnLoadBoundary }

    public sealed class SceneCheckpointScope : IEquatable<SceneCheckpointScope>
    {
        public SceneCheckpointScope(string studioSessionId, string treeId, string branchId, long generation)
        {
            StudioSessionId = RequireId(studioSessionId); TreeId = RequireId(treeId); BranchId = RequireId(branchId);
            if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
            Generation = generation;
        }
        public string StudioSessionId { get; }
        public string TreeId { get; }
        public string BranchId { get; }
        public long Generation { get; }
        internal string BranchKey => TreeId + "/" + BranchId;
        public bool Equals(SceneCheckpointScope? other) => other != null
            && StudioSessionId == other.StudioSessionId && TreeId == other.TreeId
            && BranchId == other.BranchId && Generation == other.Generation;
        public override bool Equals(object? obj) => Equals(obj as SceneCheckpointScope);
        public override int GetHashCode() => (StudioSessionId + BranchKey + "/" + Generation).GetHashCode();
        internal static string RequireId(string id)
        {
            if (id == null || id.Length != 32 || !Guid.TryParseExact(id, "N", out _) || id != id.ToLowerInvariant())
                throw new InvalidDataException("Checkpoint identities must be lowercase GUIDs in N format.");
            return id;
        }
    }

    public sealed class SceneCheckpointBinding : IEquatable<SceneCheckpointBinding>
    {
        public SceneCheckpointBinding(string environmentSha256, string initialSavesSha256, string nativeProfileId)
        {
            if (!MovieProtocolV1.IsLowerSha256(environmentSha256) || !MovieProtocolV1.IsLowerSha256(initialSavesSha256))
                throw new InvalidDataException("Checkpoint environment and initial-save hashes are required.");
            if (string.IsNullOrWhiteSpace(nativeProfileId) || nativeProfileId.Length > 128)
                throw new InvalidDataException("Checkpoint native profile is invalid.");
            EnvironmentSha256 = environmentSha256; InitialSavesSha256 = initialSavesSha256; NativeProfileId = nativeProfileId;
        }
        public string EnvironmentSha256 { get; }
        public string InitialSavesSha256 { get; }
        public string NativeProfileId { get; }
        public bool Equals(SceneCheckpointBinding? other) => other != null && EnvironmentSha256 == other.EnvironmentSha256
            && InitialSavesSha256 == other.InitialSavesSha256 && NativeProfileId == other.NativeProfileId;
        public override bool Equals(object? obj) => Equals(obj as SceneCheckpointBinding);
        public override int GetHashCode() => (EnvironmentSha256 + InitialSavesSha256 + NativeProfileId).GetHashCode();
    }

    public sealed class SceneCheckpointHeader
    {
        public const string AdapterId = "hk-managed-loading-data-v1";
        public const int SchemaVersion = 1;
        public SceneCheckpointHeader(string checkpointId, SceneCheckpointScope scope, SceneCheckpointBinding binding,
            SceneCheckpointTrigger trigger, long transitionId, long captureMovieFrame, long captureNativeFrame,
            long resumeMovieFrame, long resumeNativeFrame, int payloadBytes, string payloadSha256)
        {
            CheckpointId = SceneCheckpointScope.RequireId(checkpointId);
            Scope = scope ?? throw new ArgumentNullException(nameof(scope));
            Binding = binding ?? throw new ArgumentNullException(nameof(binding));
            if (!Enum.IsDefined(typeof(SceneCheckpointTrigger), trigger) || transitionId < 1 || captureMovieFrame < 0
                || captureNativeFrame < 0 || resumeMovieFrame < captureMovieFrame || resumeNativeFrame < captureNativeFrame)
                throw new InvalidDataException("Checkpoint boundary or trigger is invalid.");
            if (payloadBytes < 1 || payloadBytes > SceneCheckpointPacket.MaximumPayloadBytes
                || !MovieProtocolV1.IsLowerSha256(payloadSha256)) throw new InvalidDataException("Checkpoint payload declaration is invalid.");
            Trigger = trigger; TransitionId = transitionId; CaptureMovieFrame = captureMovieFrame; CaptureNativeFrame = captureNativeFrame;
            ResumeMovieFrame = resumeMovieFrame; ResumeNativeFrame = resumeNativeFrame; PayloadBytes = payloadBytes; PayloadSha256 = payloadSha256;
        }
        public string CheckpointId { get; }
        public SceneCheckpointScope Scope { get; }
        public SceneCheckpointBinding Binding { get; }
        public SceneCheckpointTrigger Trigger { get; }
        public long TransitionId { get; }
        public long CaptureMovieFrame { get; }
        public long CaptureNativeFrame { get; }
        public long ResumeMovieFrame { get; }
        public long ResumeNativeFrame { get; }
        public int PayloadBytes { get; }
        public string PayloadSha256 { get; }
        // This first adapter contains data, not a complete world/continuation image.
        public bool RestoreQualified => false;

        public Dictionary<string, string> ToFields()
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
            ["schema"] = "1", ["adapter"] = AdapterId, ["checkpointId"] = CheckpointId,
            ["studioSessionId"] = Scope.StudioSessionId, ["treeId"] = Scope.TreeId, ["branchId"] = Scope.BranchId,
            ["generation"] = Number(Scope.Generation), ["environmentSha256"] = Binding.EnvironmentSha256,
            ["initialSavesSha256"] = Binding.InitialSavesSha256, ["nativeProfileId"] = Binding.NativeProfileId,
            ["trigger"] = Trigger.ToString(), ["transitionId"] = Number(TransitionId),
            ["captureMovieFrame"] = Number(CaptureMovieFrame), ["captureNativeFrame"] = Number(CaptureNativeFrame),
            ["resumeMovieFrame"] = Number(ResumeMovieFrame), ["resumeNativeFrame"] = Number(ResumeNativeFrame),
            ["payloadBytes"] = Number(PayloadBytes), ["payloadSha256"] = PayloadSha256, ["restoreQualified"] = "false"
            };
            fields["headerSha256"] = Sha256Utility.ComputeHex(IpcPayloadCodec.Serialize(fields));
            return fields;
        }
        public static SceneCheckpointHeader FromFields(IReadOnlyDictionary<string, string> fields)
        {
            string Get(string key) => fields.TryGetValue(key, out var value) ? value : throw new InvalidDataException("Missing checkpoint field: " + key);
            long Read(string key) => long.TryParse(Get(key), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                ? n : throw new InvalidDataException("Invalid checkpoint integer: " + key);
            if (Get("schema") != "1" || Get("adapter") != AdapterId || Get("restoreQualified") != "false")
                throw new InvalidDataException("Unsupported checkpoint schema, adapter or qualification.");
            if (!Enum.TryParse<SceneCheckpointTrigger>(Get("trigger"), out var trigger) || trigger.ToString() != Get("trigger"))
                throw new InvalidDataException("Unknown checkpoint trigger.");
            var size = Read("payloadBytes");
            if (size > SceneCheckpointPacket.MaximumPayloadBytes) throw new InvalidDataException("Checkpoint payload exceeds its bound.");
            var header = new SceneCheckpointHeader(Get("checkpointId"),
                new SceneCheckpointScope(Get("studioSessionId"), Get("treeId"), Get("branchId"), Read("generation")),
                new SceneCheckpointBinding(Get("environmentSha256"), Get("initialSavesSha256"), Get("nativeProfileId")), trigger,
                Read("transitionId"), Read("captureMovieFrame"), Read("captureNativeFrame"), Read("resumeMovieFrame"), Read("resumeNativeFrame"),
                checked((int)size), Get("payloadSha256"));
            var expected = header.ToFields();
            foreach (var pair in expected)
                if (Get(pair.Key) != pair.Value) throw new InvalidDataException("Checkpoint metadata is noncanonical or its hash differs.");
            foreach (var key in fields.Keys)
                if (!expected.ContainsKey(key) && key != "requestId" && key != "phase" && key != "status")
                    throw new InvalidDataException("Unknown checkpoint metadata field: " + key);
            return header;
        }
        private static string Number(long n) => n.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class SceneCheckpointPacket
    {
        public const int MaximumPayloadBytes = 32 * 1024 * 1024;
        public const int ChunkBytes = 48 * 1024;
        private static readonly string[] Sections = { "playerData", "sceneData", "transition", "unityRng" };
        private readonly byte[] payload;
        public SceneCheckpointPacket(SceneCheckpointHeader header, byte[] payload)
        {
            Header = header ?? throw new ArgumentNullException(nameof(header));
            if (payload == null || payload.Length != header.PayloadBytes)
                throw new InvalidDataException("Checkpoint payload length or hash mismatch.");
            this.payload = (byte[])payload.Clone();
            if (Sha256Utility.ComputeHex(this.payload) != header.PayloadSha256) throw new InvalidDataException("Checkpoint payload hash mismatch.");
            DecodeSections(this.payload);
        }
        public SceneCheckpointHeader Header { get; }
        public int ChunkCount => (payload.Length + ChunkBytes - 1) / ChunkBytes;
        public byte[] CopyPayload() => (byte[])payload.Clone();
        public byte[] ReadChunk(int index)
        {
            if (index < 0 || index >= ChunkCount) throw new ArgumentOutOfRangeException(nameof(index));
            var offset = checked(index * ChunkBytes);
            var chunk = new byte[Math.Min(ChunkBytes, payload.Length - offset)];
            Buffer.BlockCopy(payload, offset, chunk, 0, chunk.Length); return chunk;
        }
        public IReadOnlyDictionary<string, byte[]> CopySections() => DecodeSections(payload);
        public static byte[] EncodeSections(IReadOnlyDictionary<string, byte[]> sections)
        {
            if (sections == null || sections.Count != Sections.Length) throw new InvalidDataException("Checkpoint data sections are incomplete.");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
            {
                writer.Write(SceneCheckpointHeader.SchemaVersion);
                foreach (var name in Sections)
                {
                    if (!sections.TryGetValue(name, out var bytes) || bytes == null || bytes.Length < 1
                        || bytes.Length > MaximumPayloadBytes || stream.Length + 4 + bytes.Length > MaximumPayloadBytes)
                        throw new InvalidDataException("Checkpoint section is missing or exceeds its bound: " + name);
                    if (name == "unityRng" && bytes.Length != 16) throw new InvalidDataException("Unity RNG section must contain all four words.");
                    writer.Write(bytes.Length); writer.Write(bytes);
                }
                return stream.ToArray();
            }
        }
        private static IReadOnlyDictionary<string, byte[]> DecodeSections(byte[] bytes)
        {
            if (bytes.Length < 4) throw new InvalidDataException("Truncated checkpoint data schema.");
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
            {
                if (reader.ReadInt32() != SceneCheckpointHeader.SchemaVersion) throw new InvalidDataException("Unknown checkpoint data schema.");
                var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var name in Sections)
                {
                    if (stream.Length - stream.Position < 4) throw new InvalidDataException("Truncated checkpoint section header.");
                    var length = reader.ReadInt32();
                    if (length < 1 || length > stream.Length - stream.Position || (name == "unityRng" && length != 16))
                        throw new InvalidDataException("Invalid checkpoint section length: " + name);
                    result.Add(name, reader.ReadBytes(length));
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing checkpoint data.");
                return new ReadOnlyDictionary<string, byte[]>(result);
            }
        }
    }
}
