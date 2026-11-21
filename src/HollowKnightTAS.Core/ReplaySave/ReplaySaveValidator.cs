using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Input;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Core.Recording;
using HollowKnightTAS.Core.State;

namespace HollowKnightTAS.Core.ReplaySave
{
    public delegate bool ReplaySaveObjectResolver(
        string sha256,
        out byte[]? bytes);

    public sealed class ReplaySavePackage
    {
        internal ReplaySavePackage(
            ReplaySaveDescriptor descriptor,
            BaselineBundle baseline,
            MovieDocument movie,
            SemanticSnapshot targetSnapshot,
            IEnumerable<ReplayJournalSegment> segments,
            IReadOnlyDictionary<string, byte[]> objects,
            ReplayLifecycleLog? lifecycle = null)
        {
            Descriptor = descriptor;
            Lifecycle = lifecycle;
            Baseline = baseline;
            Movie = movie;
            TargetSnapshot = targetSnapshot;
            Segments = new ReadOnlyCollection<ReplayJournalSegment>(
                new List<ReplayJournalSegment>(segments));
            Objects = new ReadOnlyDictionary<string, byte[]>(
                objects.ToDictionary(
                    item => item.Key,
                    item => (byte[])item.Value.Clone(),
                    StringComparer.Ordinal));
        }

        public ReplaySaveDescriptor Descriptor { get; }
        public ReplayLifecycleLog? Lifecycle { get; }
        public BaselineBundle Baseline { get; }
        public MovieDocument Movie { get; }
        public SemanticSnapshot TargetSnapshot { get; }
        public IReadOnlyList<ReplayJournalSegment> Segments { get; }
        public IReadOnlyDictionary<string, byte[]> Objects { get; }
        public long NextMovieTick =>
            checked(Descriptor.EffectiveMovieTick + 1);
    }

    public sealed class ReplaySaveValidationResult
    {
        private ReplaySaveValidationResult(
            ReplaySaveStatus status,
            string detail,
            ReplaySavePackage? package)
        {
            Status = status;
            Detail = detail ?? string.Empty;
            Package = package;
        }

        public bool Success =>
            Status == ReplaySaveStatus.Ready && Package != null;
        public ReplaySaveStatus Status { get; }
        public string Detail { get; }
        public ReplaySavePackage? Package { get; }

        internal static ReplaySaveValidationResult Ready(
            ReplaySavePackage package)
        {
            return new ReplaySaveValidationResult(
                ReplaySaveStatus.Ready,
                string.Empty,
                package);
        }

        internal static ReplaySaveValidationResult Failure(
            ReplaySaveStatus status,
            string detail)
        {
            return new ReplaySaveValidationResult(status, detail, null);
        }
    }

    public static class ReplaySaveValidator
    {
        public static ReplaySaveValidationResult Validate(
            ReplaySaveDescriptor descriptor,
            ReplaySaveObjectResolver resolver,
            string? expectedManifestSha256 = null)
        {
            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            if (resolver == null)
            {
                throw new ArgumentNullException(nameof(resolver));
            }

            if (descriptor.SchemaVersion
                != ReplaySaveDescriptor.CurrentSchemaVersion
                && descriptor.SchemaVersion != ReplaySaveDescriptor.LifecycleSchemaVersion)
            {
                return Failure(
                    ReplaySaveStatus.Incompatible,
                    "descriptor-schema",
                    "Unsupported descriptor schema "
                    + descriptor.SchemaVersion
                    + ".");
            }

            if (expectedManifestSha256 != null
                && !string.Equals(
                    descriptor.ManifestSha256,
                    expectedManifestSha256,
                    StringComparison.Ordinal))
            {
                Trace.TraceWarning(
                    $"ReplaySaveValidator: descriptor manifest SHA-256 ({descriptor.ManifestSha256}) differs from current runtime ({expectedManifestSha256}); continuing anyway.");
            }

            var loaded = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (!TryLoad(
                    descriptor.ManifestSha256,
                    "manifest",
                    ReplaySaveStatus.Corrupt,
                    resolver,
                    loaded,
                    out var failure))
            {
                return failure!;
            }

            if (!TryLoad(
                    descriptor.BaselineObjectSha256,
                    "baseline",
                    ReplaySaveStatus.Corrupt,
                    resolver,
                    loaded,
                    out failure))
            {
                return failure!;
            }

            if (!TryLoad(
                    descriptor.MovieObjectSha256,
                    "movie",
                    ReplaySaveStatus.Corrupt,
                    resolver,
                    loaded,
                    out failure))
            {
                return failure!;
            }

            if (!TryLoad(
                    descriptor.SemanticSnapshotSha256,
                    "semantic-snapshot",
                    ReplaySaveStatus.Corrupt,
                    resolver,
                    loaded,
                    out failure))
            {
                return failure!;
            }

            if (!TryLoad(
                    descriptor.LedgerSummarySha256,
                    "ledger-summary",
                    ReplaySaveStatus.Corrupt,
                    resolver,
                    loaded,
                    out failure))
            {
                return failure!;
            }

            var segments = new List<ReplayJournalSegment>();
            var previousHash = ReplayJournalSegment.GenesisPreviousSha256;
            var expectedSequence = 0;
            var expectedMovieTick = 0L;
            foreach (var segmentHash in descriptor.JournalSegmentObjectSha256s)
            {
                if (!TryLoad(
                        segmentHash,
                        "journal-segment",
                        ReplaySaveStatus.JournalGap,
                        resolver,
                        loaded,
                        out failure))
                {
                    return failure!;
                }

                ReplayJournalSegment segment;
                try
                {
                    segment = ReplayJournalSegmentCodec.Deserialize(
                        loaded[segmentHash]);
                }
                catch (Exception exception) when (
                    exception is InvalidDataException
                    || exception is ArgumentException)
                {
                    return Failure(
                        ReplaySaveStatus.JournalGap,
                        "journal-segment-invalid",
                        exception.Message);
                }

                if (segment.Sequence != expectedSequence
                    || segment.FirstMovieTick != expectedMovieTick
                    || !string.Equals(
                        segment.PreviousObjectSha256,
                        previousHash,
                        StringComparison.Ordinal))
                {
                    return Failure(
                        ReplaySaveStatus.JournalGap,
                        "journal-chain",
                        "Journal segment chain is missing, unordered, or discontinuous.");
                }

                segments.Add(segment);
                previousHash = segmentHash;
                expectedSequence = checked(expectedSequence + 1);
                expectedMovieTick = checked(segment.LastMovieTick + 1);
            }

            if (!string.Equals(
                    previousHash,
                    descriptor.JournalHeadSha256,
                    StringComparison.Ordinal)
                || expectedMovieTick - 1 != descriptor.EffectiveMovieTick)
            {
                return Failure(
                    ReplaySaveStatus.JournalGap,
                    "journal-head",
                    "Journal head or final movie tick does not match the descriptor.");
            }

            BaselineBundle baseline;
            try
            {
                baseline = BaselineBundleCodec.Deserialize(
                    loaded[descriptor.BaselineObjectSha256]);
            }
            catch (Exception exception) when (
                exception is InvalidDataException
                || exception is ArgumentException)
            {
                return Failure(
                    ReplaySaveStatus.Corrupt,
                    "baseline-invalid",
                    exception.Message);
            }

            if (!string.Equals(
                    baseline.BaselineId,
                    descriptor.BaselineId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    baseline.BaselineSemanticSha256,
                    descriptor.BaselineSemanticSha256,
                    StringComparison.Ordinal))
            {
                return Failure(
                    ReplaySaveStatus.Incompatible,
                    "baseline-mismatch",
                    "Baseline identity does not match the descriptor.");
            }

            SemanticSnapshot targetSnapshot;
            try
            {
                targetSnapshot = SemanticSnapshotCanonicalizer.Deserialize(
                    loaded[descriptor.SemanticSnapshotSha256]);
            }
            catch (InvalidDataException exception)
            {
                return Failure(
                    ReplaySaveStatus.Corrupt,
                    "semantic-snapshot-invalid",
                    exception.Message);
            }

            if (!targetSnapshot.Values.TryGetValue(
                    "scene.name",
                    out var sceneValue)
                || !string.Equals(
                    sceneValue.DisplayValue,
                    descriptor.SceneName,
                    StringComparison.Ordinal))
            {
                return Failure(
                    ReplaySaveStatus.Corrupt,
                    "scene-mismatch",
                    "Target semantic snapshot scene does not match the descriptor.");
            }

            MovieDocument movie;
            try
            {
                if (loaded[descriptor.MovieObjectSha256].Length > ReplaySaveCommit.MaximumMovieBytes)
                    return Failure(ReplaySaveStatus.Corrupt, "movie-size", "Movie exceeds size limit.");
                var movieText = new UTF8Encoding(false, true).GetString(
                    loaded[descriptor.MovieObjectSha256]);
                var parse = new MovieParser().Parse(
                    new StringReader(movieText),
                    descriptor.ReplaySaveId + ".hktas");
                if (!parse.Success || parse.Document == null)
                {
                    return Failure(
                        ReplaySaveStatus.Corrupt,
                        "movie-parse",
                        string.Join(
                            "; ",
                            parse.Diagnostics.Select(value => value.ToString())));
                }

                movie = parse.Document;
            }
            catch (DecoderFallbackException exception)
            {
                return Failure(
                    ReplaySaveStatus.Corrupt,
                    "movie-utf8",
                    exception.Message);
            }

            var validation = new MovieValidator().Validate(
                movie,
                new MovieValidationContext(
                    Math.Max(
                        MovieProtocolV1.DefaultMaxExpandedTicks,
                        checked(descriptor.EffectiveMovieTick + 1)),
                    MovieProtocolV1.DefaultSemanticPaths,
                    descriptor.ManifestSha256,
                    descriptor.BaselineSemanticSha256));
            if (!validation.Success
                || validation.ExpandedTickCount
                != descriptor.EffectiveMovieTick + 1
                || !string.Equals(
                    movie.Header.BaselineId,
                    descriptor.BaselineId,
                    StringComparison.Ordinal))
            {
                return Failure(
                    ReplaySaveStatus.Incompatible,
                    "movie-mismatch",
                    "Movie header or expanded tick count does not match the descriptor.");
            }

            var journalRecords = segments
                .SelectMany(value => value.Records)
                .ToList();
            var cursor = new Playback.MovieCursor(movie);
            for (var index = 0; index < journalRecords.Count; index++)
            {
                if (!cursor.MoveNext())
                {
                    return Failure(
                        ReplaySaveStatus.Corrupt,
                        "movie-short",
                        "Movie ended before its journal prefix.");
                }

                var expected = journalRecords[index].Sample;
                var actual = cursor.CurrentInput;
                if (expected.Held != actual.Held
                    || expected.Pressed != actual.Pressed
                    || expected.Released != actual.Released
                    || expected.AxisX != actual.AxisX
                    || expected.AxisY != actual.AxisY)
                {
                    return Failure(
                        ReplaySaveStatus.Corrupt,
                        "movie-journal-input",
                        "Movie input differs from the journal at movie tick "
                        + index
                        + ".");
                }
            }

            if (cursor.MoveNext())
            {
                return Failure(
                    ReplaySaveStatus.Corrupt,
                    "movie-long",
                    "Movie contains more input ticks than the journal prefix.");
            }

            ReplayLifecycleLog? lifecycle = null;
            if (descriptor.SchemaVersion == ReplaySaveDescriptor.LifecycleSchemaVersion)
            {
                var lifecycleFailure = ValidateLifecycleReferences(descriptor, resolver, loaded, movie, out lifecycle);
                if (lifecycleFailure != null) return lifecycleFailure;
            }

            return ReplaySaveValidationResult.Ready(
                new ReplaySavePackage(
                    descriptor,
                    baseline,
                    movie,
                    targetSnapshot,
                    segments,
                    loaded, lifecycle));
        }

        private static ReplaySaveValidationResult? ValidateLifecycleReferences(
            ReplaySaveDescriptor descriptor, ReplaySaveObjectResolver resolver,
            IDictionary<string, byte[]> loaded, MovieDocument movie, out ReplayLifecycleLog? lifecycle)
        {
            lifecycle = null;
            if (!TryLoad(descriptor.LifecycleObjectSha256!, "lifecycle", ReplaySaveStatus.Corrupt,
                    resolver, loaded, out var failure)) return failure!;
            try
            {
                var log = ReplayLifecycleLog.Deserialize(loaded[descriptor.LifecycleObjectSha256!]);
                if (log.RootBaselineSha256 != descriptor.BaselineObjectSha256 || !log.IsCompleted
                    || log.Records.Any(record => record.AfterMovieTick > descriptor.EffectiveMovieTick))
                    return Failure(ReplaySaveStatus.Corrupt, "lifecycle-boundary", "Lifecycle root, outcome or target boundary is invalid.");
                log.VerifySlotObjects(hash =>
                {
                    if (!TryLoad(hash, "lifecycle-slot", ReplaySaveStatus.Corrupt,
                            resolver, loaded, out var slotFailure))
                        throw new InvalidDataException(slotFailure!.Detail);
                    return loaded[hash];
                });
                log.VerifyInputPrefixes(movie);
                if (log.Records.Any(record => record.Kind == ReplayLifecycleKind.LoadSlot
                    && record.ModdedSlotObjectSha256 == null))
                    return Failure(ReplaySaveStatus.Incompatible, "lifecycle-settings-unknown",
                        "Legacy lifecycle did not capture modded slot settings.");
                lifecycle = log;
            }
            catch (Exception exception) when (exception is ArgumentException
                || exception is IOException || exception is InvalidDataException)
            {
                return Failure(ReplaySaveStatus.Corrupt, "lifecycle-content", exception.Message);
            }
            return null;
        }

        private static bool TryLoad(
            string hash,
            string kind,
            ReplaySaveStatus missingStatus,
            ReplaySaveObjectResolver resolver,
            IDictionary<string, byte[]> loaded,
            out ReplaySaveValidationResult? failure)
        {
            if (loaded.ContainsKey(hash))
            {
                failure = null;
                return true;
            }

            if (!resolver(hash, out var bytes) || bytes == null)
            {
                failure = Failure(
                    missingStatus,
                    kind + "-missing",
                    "Required " + kind + " object is missing.");
                return false;
            }

            var actual = Sha256Utility.ComputeHex(bytes);
            if (!string.Equals(actual, hash, StringComparison.Ordinal))
            {
                failure = Failure(
                    missingStatus,
                    kind + "-hash",
                    "Required " + kind + " object failed SHA-256 verification.");
                return false;
            }

            loaded.Add(hash, (byte[])bytes.Clone());
            failure = null;
            return true;
        }

        private static ReplaySaveValidationResult Failure(
            ReplaySaveStatus status,
            string code,
            string detail)
        {
            return ReplaySaveValidationResult.Failure(
                status,
                code + ": " + detail);
        }
    }
}
