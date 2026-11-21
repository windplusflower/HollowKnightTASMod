using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.Core.ReplaySave
{
    // A portable plan, not evidence that any requested operation has completed.
    public sealed class ReplayLifecycleExecutionPlan
    {
        public const int MaximumBytes = 256 * 1024 * 1024;
        private readonly byte[] baseline;
        private readonly byte[] movieBytes;
        private readonly SortedDictionary<string, byte[]> objects;

        private ReplayLifecycleExecutionPlan(string branchId, byte[] movieBytes, byte[] baseline,
            IEnumerable<ReplayLifecycleRecord> requests, IReadOnlyDictionary<string, byte[]> objects)
        {
            SourceBranchId = ReplaySaveDescriptor.RequireSha256(branchId, nameof(branchId));
            if (movieBytes.Length > 32 * 1024 * 1024 || baseline.Length > BaselineBundle.MaximumBundleBytes)
                throw new InvalidDataException("Execution plan exceeds movie or baseline bounds.");
            this.movieBytes = (byte[])movieBytes.Clone();
            this.baseline = (byte[])baseline.Clone();
            var parsed = new MovieParser().Parse(new StringReader(new UTF8Encoding(false, true).GetString(this.movieBytes)), "lifecycle-plan");
            if (!parsed.Success || parsed.Document == null
                || !new MovieValidator().Validate(parsed.Document, MovieValidationContext.CreateDefault()).Success
                || !new MovieCanonicalWriter().WriteUtf8(parsed.Document).SequenceEqual(this.movieBytes))
                throw new InvalidDataException("Execution plan movie must be canonical and valid.");
            Movie = parsed.Document;
            var bundle = BaselineBundleCodec.Deserialize(this.baseline);
            if (Movie.Header.BaselineId != bundle.BaselineId || Movie.Header.BaselineSha256 != bundle.BaselineSemanticSha256)
                throw new InvalidDataException("Execution plan movie and baseline disagree.");
            BaselineObjectSha256 = Sha256Utility.ComputeHex(this.baseline);
            var copied = new List<ReplayLifecycleRecord>();
            long boundary = -1;
            foreach (var request in requests)
            {
                if (request == null || copied.Count >= ReplayLifecycleLog.MaximumRecords || request.Sequence != copied.Count
                    || request.AfterMovieTick < boundary || request.Outcome != ReplayLifecycleOutcome.Waiting
                    || request.NativeFrameCount != 0 || request.Failure.Length != 0
                    || (request.Kind == ReplayLifecycleKind.LoadSlot && request.ModdedSlotObjectSha256 == null)
                    || request.InputPrefixSha256 != MoviePrefixIdentity.ComputeSha256(Movie, request.AfterMovieTick + 1))
                    throw new InvalidDataException("Execution plan has an invalid operation or input boundary.");
                copied.Add(request);
                boundary = request.AfterMovieTick;
            }
            Requests = new ReadOnlyCollection<ReplayLifecycleRecord>(copied);
            this.objects = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            long total = 0;
            foreach (var hash in copied.Where(x => x.Kind == ReplayLifecycleKind.LoadSlot)
                .SelectMany(x => new[] { x.SlotObjectSha256, x.ModdedSlotObjectSha256 })
                .Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).Distinct(StringComparer.Ordinal))
            {
                if (!objects.TryGetValue(hash, out var value) || value == null || value.Length > ReplayLifecycleLog.MaximumSlotBytes)
                    throw new InvalidDataException("Execution plan slot object unavailable.");
                total += value.Length;
                if (total > 64L * 1024 * 1024) throw new InvalidDataException("Execution plan object budget exceeded.");
                var bytes = (byte[])value.Clone();
                if (Sha256Utility.ComputeHex(bytes) != hash) throw new InvalidDataException("Execution plan slot hash mismatch.");
                this.objects.Add(hash, bytes);
            }
            if (copied.Any(x => x.Kind == ReplayLifecycleKind.LoadSlot && this.objects[x.SlotObjectSha256].Length == 0))
                throw new InvalidDataException("Execution plan contains an empty native save.");
        }

        public string SourceBranchId { get; }
        public string BaselineObjectSha256 { get; }
        public MovieDocument Movie { get; }
        public IReadOnlyList<ReplayLifecycleRecord> Requests { get; }
        // This validates an execution request, not proof of a restored target.
        // Call before persisting source checkpoints or changing any save slot.
        public BaselineBundle ValidateSeek(string manifestSha256, long targetMovieTick)
        {
            ReplaySaveDescriptor.RequireSha256(manifestSha256, nameof(manifestSha256));
            if (Movie.Header.ManifestSha256 != manifestSha256)
                Trace.TraceWarning(
                    $"ReplayLifecycleExecutionPlan.ValidateSeek: movie manifest SHA-256 ({Movie.Header.ManifestSha256}) differs from current execution build ({manifestSha256}); continuing anyway.");
            long count = 0;
            foreach (var command in Movie.Commands)
            {
                if (!(command is FrameRunCommand run))
                    throw new InvalidDataException("Execution plan must contain only native input frames.");
                count = checked(count + run.FrameCount);
            }
            if (targetMovieTick < 0 || targetMovieTick >= count)
                throw new InvalidDataException("Execution plan target is outside its input timeline.");
            if (Requests.Any(request => request.AfterMovieTick >= count))
                throw new InvalidDataException("Execution plan operation is outside its input timeline.");
            return BaselineBundleCodec.Deserialize(baseline);
        }

        public byte[] CopyBaseline() => (byte[])baseline.Clone();
        public IReadOnlyDictionary<string, byte[]> CopySlotObjects() =>
            objects.ToDictionary(x => x.Key, x => (byte[])x.Value.Clone(), StringComparer.Ordinal);

        public static ReplayLifecycleExecutionPlan FromEdit(string branchId, ReplayLifecycleTimelineEditResult edit,
            byte[] baseline, IReadOnlyDictionary<string, byte[]> objects)
        {
            if (Sha256Utility.ComputeHex(baseline) != edit.Source.RootBaselineSha256)
                throw new InvalidDataException("Edited branch root baseline changed.");
            return new ReplayLifecycleExecutionPlan(branchId, new MovieCanonicalWriter().WriteUtf8(edit.InputEdit.Movie), baseline,
                edit.Operations.Select(x => new ReplayLifecycleRecord(x.Source.Sequence, x.AfterMovieTick,
                    x.InputPrefixSha256, x.Source.Kind, x.Source.Slot, x.Source.SlotObjectSha256,
                    ReplayLifecycleOutcome.Waiting, 0, string.Empty, x.Source.ModdedSlotObjectSha256)), objects);
        }

        public byte[] Serialize()
        {
            using var writer = new ReplaySaveBinaryWriter();
            writer.WriteMagic("HKLP"); writer.WriteInt32(1);
            writer.WriteString(SourceBranchId, 64);
            writer.WriteBytes(movieBytes, 32 * 1024 * 1024);
            writer.WriteBytes(baseline, BaselineBundle.MaximumBundleBytes);
            writer.WriteInt32(Requests.Count);
            foreach (var r in Requests)
            {
                writer.WriteInt32(r.Sequence); writer.WriteInt64(r.AfterMovieTick);
                writer.WriteString(r.InputPrefixSha256, 64); writer.WriteByte((byte)r.Kind); writer.WriteInt32(r.Slot);
                writer.WriteString(r.SlotObjectSha256, 64);
                writer.WriteString(r.ModdedSlotObjectSha256 ?? string.Empty, 64);
            }
            writer.WriteInt32(objects.Count);
            foreach (var item in objects) { writer.WriteString(item.Key, 64); writer.WriteBytes(item.Value, ReplayLifecycleLog.MaximumSlotBytes); }
            var bytes = writer.ToArray();
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("Execution plan exceeds byte limit.");
            return bytes;
        }

        public static ReplayLifecycleExecutionPlan Deserialize(byte[] bytes, string expectedSha256)
        {
            if (bytes.Length > MaximumBytes || Sha256Utility.ComputeHex(bytes) != expectedSha256)
                throw new InvalidDataException("Execution plan hash or size mismatch.");
            var reader = new ReplaySaveBinaryReader(bytes, MaximumBytes);
            reader.RequireMagic("HKLP");
            if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported execution plan schema.");
            var branch = reader.ReadString(64, "branch");
            var movie = reader.ReadBytes(32 * 1024 * 1024, "movie");
            var baseline = reader.ReadBytes(BaselineBundle.MaximumBundleBytes, "baseline");
            var count = reader.ReadInt32();
            if (count < 0 || count > ReplayLifecycleLog.MaximumRecords) throw new InvalidDataException("Invalid execution operation count.");
            var requests = new List<ReplayLifecycleRecord>();
            for (var i = 0; i < count; i++)
            {
                var sequence = reader.ReadInt32(); var tick = reader.ReadInt64(); var prefix = reader.ReadString(64, "prefix");
                var kind = (ReplayLifecycleKind)reader.ReadByte(); var slot = reader.ReadInt32();
                var hash = reader.ReadString(64, "slot"); var modded = reader.ReadString(64, "modded");
                requests.Add(new ReplayLifecycleRecord(sequence, tick, prefix, kind, slot, hash,
                    ReplayLifecycleOutcome.Waiting, 0, string.Empty, kind == ReplayLifecycleKind.ReturnToMenu ? null : modded));
                if (kind == ReplayLifecycleKind.ReturnToMenu && modded.Length != 0) throw new InvalidDataException("Menu request contains settings.");
            }
            count = reader.ReadInt32();
            if (count < 0 || count > ReplayLifecycleLog.MaximumRecords * 2) throw new InvalidDataException("Invalid execution object count.");
            var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            long total = 0;
            for (var i = 0; i < count; i++)
            {
                var hash = reader.ReadString(64, "object"); var value = reader.ReadBytes(ReplayLifecycleLog.MaximumSlotBytes, "bytes");
                total += value.Length;
                if (total > 64L * 1024 * 1024 || objects.ContainsKey(hash)) throw new InvalidDataException("Invalid execution object budget or duplicate.");
                objects.Add(hash, value);
            }
            reader.RequireEnd();
            var result = new ReplayLifecycleExecutionPlan(branch, movie, baseline, requests, objects);
            if (!result.Serialize().SequenceEqual(bytes)) throw new InvalidDataException("Noncanonical execution plan.");
            return result;
        }
    }
}
