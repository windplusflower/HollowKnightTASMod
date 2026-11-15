using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Ipc;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Core.ReplaySave;
using System.Linq;

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
        public static string HashInitialSaves(IReadOnlyDictionary<string, string> hashes)
        {
            if (hashes == null || hashes.Count > 128) throw new InvalidDataException("Invalid checkpoint initial-save set.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
            {
                writer.Write(1); writer.Write(hashes.Count);
                foreach (var pair in hashes.OrderBy(p => p.Key.ToLowerInvariant(), StringComparer.Ordinal))
                {
                    if (!ProtectedSaveDescriptor.IsSlotFileName(pair.Key) || !MovieProtocolV1.IsLowerSha256(pair.Value) || !names.Add(pair.Key))
                        throw new InvalidDataException("Invalid checkpoint initial-save file identity.");
                    writer.Write(pair.Key.ToLowerInvariant()); writer.Write(pair.Value);
                }
                writer.Flush(); return Sha256Utility.ComputeHex(stream.ToArray());
            }
        }
        public bool Equals(SceneCheckpointBinding? other) => other != null && EnvironmentSha256 == other.EnvironmentSha256
            && InitialSavesSha256 == other.InitialSavesSha256 && NativeProfileId == other.NativeProfileId;
        public override bool Equals(object? obj) => Equals(obj as SceneCheckpointBinding);
        public override int GetHashCode() => (EnvironmentSha256 + InitialSavesSha256 + NativeProfileId).GetHashCode();
    }

    public sealed class SceneCheckpointHeader
    {
        public const string AdapterId = "hk-managed-loading-continuation-v2";
        public const int SchemaVersion = 2;
        public const string LeanS0AdapterId = "hk-lean-s0-v1";
        public const int LeanS0SchemaVersion = 3;
        public SceneCheckpointHeader(string checkpointId, SceneCheckpointScope scope, SceneCheckpointBinding binding,
            SceneCheckpointTrigger trigger, long transitionId, long captureMovieFrame, long captureNativeFrame,
            long resumeMovieFrame, long resumeNativeFrame, int payloadBytes, string payloadSha256,
            int schemaVersion = SchemaVersion, string adapter = AdapterId, bool restoreQualified = false)
        {
            CheckpointId = SceneCheckpointScope.RequireId(checkpointId);
            Scope = scope ?? throw new ArgumentNullException(nameof(scope));
            Binding = binding ?? throw new ArgumentNullException(nameof(binding));
            if (!Enum.IsDefined(typeof(SceneCheckpointTrigger), trigger) || transitionId < 1 || captureMovieFrame < 0
                || captureNativeFrame < 0 || resumeMovieFrame < captureMovieFrame || resumeNativeFrame < captureNativeFrame)
                throw new InvalidDataException("Checkpoint boundary or trigger is invalid.");
            if (payloadBytes < 1 || payloadBytes > SceneCheckpointPacket.MaximumPayloadBytes
                || !MovieProtocolV1.IsLowerSha256(payloadSha256)) throw new InvalidDataException("Checkpoint payload declaration is invalid.");
            bool isLegacy = schemaVersion == SchemaVersion && adapter == AdapterId && !restoreQualified;
            bool isLeanS0 = schemaVersion == LeanS0SchemaVersion && adapter == LeanS0AdapterId && restoreQualified;
            if (!isLegacy && !isLeanS0)
                throw new InvalidDataException("Unsupported checkpoint schema, adapter or qualification.");
            Schema = schemaVersion;
            Adapter = adapter;
            RestoreQualified = restoreQualified;
            Trigger = trigger; TransitionId = transitionId; CaptureMovieFrame = captureMovieFrame; CaptureNativeFrame = captureNativeFrame;
            ResumeMovieFrame = resumeMovieFrame; ResumeNativeFrame = resumeNativeFrame; PayloadBytes = payloadBytes; PayloadSha256 = payloadSha256;
        }
        public static SceneCheckpointHeader CreateLeanS0(string checkpointId, SceneCheckpointScope scope, SceneCheckpointBinding binding,
            SceneCheckpointTrigger trigger, long transitionId, long captureMovieFrame, long captureNativeFrame,
            long resumeMovieFrame, long resumeNativeFrame, int payloadBytes, string payloadSha256)
            => new SceneCheckpointHeader(checkpointId, scope, binding, trigger, transitionId, captureMovieFrame, captureNativeFrame,
                resumeMovieFrame, resumeNativeFrame, payloadBytes, payloadSha256,
                LeanS0SchemaVersion, LeanS0AdapterId, restoreQualified: true);
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
        public int Schema { get; }
        public string Adapter { get; }
        public bool RestoreQualified { get; }

        public Dictionary<string, string> ToFields()
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
            ["schema"] = Number(Schema), ["adapter"] = Adapter, ["checkpointId"] = CheckpointId,
            ["studioSessionId"] = Scope.StudioSessionId, ["treeId"] = Scope.TreeId, ["branchId"] = Scope.BranchId,
            ["generation"] = Number(Scope.Generation), ["environmentSha256"] = Binding.EnvironmentSha256,
            ["initialSavesSha256"] = Binding.InitialSavesSha256, ["nativeProfileId"] = Binding.NativeProfileId,
            ["trigger"] = Trigger.ToString(), ["transitionId"] = Number(TransitionId),
            ["captureMovieFrame"] = Number(CaptureMovieFrame), ["captureNativeFrame"] = Number(CaptureNativeFrame),
            ["resumeMovieFrame"] = Number(ResumeMovieFrame), ["resumeNativeFrame"] = Number(ResumeNativeFrame),
            ["payloadBytes"] = Number(PayloadBytes), ["payloadSha256"] = PayloadSha256,
            ["restoreQualified"] = RestoreQualified ? "true" : "false"
            };
            fields["headerSha256"] = Sha256Utility.ComputeHex(IpcPayloadCodec.Serialize(fields));
            return fields;
        }
        public static SceneCheckpointHeader FromFields(IReadOnlyDictionary<string, string> fields)
        {
            string Get(string key) => fields.TryGetValue(key, out var value) ? value : throw new InvalidDataException("Missing checkpoint field: " + key);
            long Read(string key) => long.TryParse(Get(key), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                ? n : throw new InvalidDataException("Invalid checkpoint integer: " + key);
            var schemaText = Get("schema");
            var adapter = Get("adapter");
            var qualified = Get("restoreQualified");
            int schema;
            bool restoreQualified;
            if (schemaText == Number(SchemaVersion) && adapter == AdapterId && qualified == "false")
            {
                schema = SchemaVersion;
                restoreQualified = false;
            }
            else if (schemaText == Number(LeanS0SchemaVersion) && adapter == LeanS0AdapterId && qualified == "true")
            {
                schema = LeanS0SchemaVersion;
                restoreQualified = true;
            }
            else
            {
                throw new InvalidDataException("Unsupported checkpoint schema, adapter or qualification.");
            }
            if (!Enum.TryParse<SceneCheckpointTrigger>(Get("trigger"), out var trigger) || trigger.ToString() != Get("trigger"))
                throw new InvalidDataException("Unknown checkpoint trigger.");
            var size = Read("payloadBytes");
            if (size > SceneCheckpointPacket.MaximumPayloadBytes) throw new InvalidDataException("Checkpoint payload exceeds its bound.");
            var header = new SceneCheckpointHeader(Get("checkpointId"),
                new SceneCheckpointScope(Get("studioSessionId"), Get("treeId"), Get("branchId"), Read("generation")),
                new SceneCheckpointBinding(Get("environmentSha256"), Get("initialSavesSha256"), Get("nativeProfileId")), trigger,
                Read("transitionId"), Read("captureMovieFrame"), Read("captureNativeFrame"), Read("resumeMovieFrame"), Read("resumeNativeFrame"),
                checked((int)size), Get("payloadSha256"), schema, adapter, restoreQualified);
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
        public const int MaximumPayloadBytes = 256 * 1024 * 1024;
        public const int ChunkBytes = 48 * 1024;
        private static readonly string[] Sections = { "playerData", "sceneData", "transition", "unityRng", "continuation" };
        private static readonly string[] LeanS0Sections = { "leanS0", "executionFiles" };
        private readonly byte[] payload;
        public SceneCheckpointPacket(SceneCheckpointHeader header, byte[] payload)
        {
            Header = header ?? throw new ArgumentNullException(nameof(header));
            if (payload == null || payload.Length != header.PayloadBytes)
                throw new InvalidDataException("Checkpoint payload length or hash mismatch.");
            this.payload = (byte[])payload.Clone();
            if (Sha256Utility.ComputeHex(this.payload) != header.PayloadSha256) throw new InvalidDataException("Checkpoint payload hash mismatch.");
            var (decodedSchema, _) = DecodeSectionsWithSchema(this.payload);
            if (decodedSchema != header.Schema) throw new InvalidDataException("Checkpoint payload schema mismatches header.");
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
        public IReadOnlyDictionary<string, byte[]> CopySections() => DecodeSectionsWithSchema(payload).Sections;
        public static byte[] EncodeSections(IReadOnlyDictionary<string, byte[]> sections, int schemaVersion = SceneCheckpointHeader.SchemaVersion)
        {
            string[] expectedSections;
            if (schemaVersion == SceneCheckpointHeader.SchemaVersion) expectedSections = Sections;
            else if (schemaVersion == SceneCheckpointHeader.LeanS0SchemaVersion) expectedSections = LeanS0Sections;
            else throw new InvalidDataException("Unknown checkpoint data schema.");
            if (sections == null || sections.Count != expectedSections.Length) throw new InvalidDataException("Checkpoint data sections are incomplete.");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
            {
                writer.Write(schemaVersion);
                foreach (var name in expectedSections)
                {
                    if (!sections.TryGetValue(name, out var bytes) || bytes == null || bytes.Length < 1
                        || bytes.Length > MaximumPayloadBytes || stream.Length + 4 + bytes.Length > MaximumPayloadBytes)
                        throw new InvalidDataException("Checkpoint section is missing or exceeds its bound: " + name);
                    if (schemaVersion == SceneCheckpointHeader.SchemaVersion && name == "unityRng" && bytes.Length != 16)
                        throw new InvalidDataException("Unity RNG section must contain all four words.");
                    writer.Write(bytes.Length); writer.Write(bytes);
                }
                return stream.ToArray();
            }
        }
        private static (int Schema, IReadOnlyDictionary<string, byte[]> Sections) DecodeSectionsWithSchema(byte[] bytes)
        {
            if (bytes.Length < 4) throw new InvalidDataException("Truncated checkpoint data schema.");
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
            {
                var schema = reader.ReadInt32();
                string[] expectedSections;
                if (schema == SceneCheckpointHeader.SchemaVersion) expectedSections = Sections;
                else if (schema == SceneCheckpointHeader.LeanS0SchemaVersion) expectedSections = LeanS0Sections;
                else throw new InvalidDataException("Unknown checkpoint data schema.");
                var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var name in expectedSections)
                {
                    if (stream.Length - stream.Position < 4) throw new InvalidDataException("Truncated checkpoint section header.");
                    var length = reader.ReadInt32();
                    if (length < 1 || length > stream.Length - stream.Position
                        || (schema == SceneCheckpointHeader.SchemaVersion && name == "unityRng" && length != 16))
                        throw new InvalidDataException("Invalid checkpoint section length: " + name);
                    result.Add(name, reader.ReadBytes(length));
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing checkpoint data.");
                return (schema, new ReadOnlyDictionary<string, byte[]>(result));
            }
        }
    }
}
