using System;
using System.Collections.Generic;
using System.IO;

namespace HollowKnightTAS.Core.Movie
{
    public sealed class MovieV2EditResult
    {
        private readonly IReadOnlyList<MovieDiagnostic> diagnostics;
        internal MovieV2EditResult(bool success, MovieV2Document movie, string canonicalText,
            string movieId, IEnumerable<MovieDiagnostic> diagnostics)
        {
            Success = success;
            Movie = movie;
            CanonicalText = canonicalText;
            MovieId = movieId;
            this.diagnostics = Array.AsReadOnly(new List<MovieDiagnostic>(diagnostics).ToArray());
        }
        public bool Success { get; }
        public MovieV2Document Movie { get; }
        public string CanonicalText { get; }
        public string MovieId { get; }
        public IReadOnlyList<MovieDiagnostic> Diagnostics => diagnostics;
    }

    public sealed class MovieV2TimelineEditor
    {
        private readonly MovieV2Codec codec = new MovieV2Codec();
        private readonly MovieV2Validator validator = new MovieV2Validator();

        public MovieV2EditResult ReplaceFrame(MovieV2Document movie, long nativeFrame,
            IReadOnlyList<GameInputSample> samples)
        {
            if (movie == null) throw new ArgumentNullException(nameof(movie));
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            var baseline = Baseline(movie);
            if (!baseline.Success) return baseline;
            var count = Count(movie);
            if (nativeFrame < 0 || nativeFrame >= count)
                return Failure(baseline, nativeFrame, "Replacement native frame is outside the movie.");
            var runs = Slice(movie, 0, nativeFrame);
            runs.Add(new NativeFrameRun(1, samples,
                new MovieSourceSpan(movie.SourceName, 1, 1, 1), Slice(movie, nativeFrame, 1)[0].FramesPerSecond, Slice(movie, nativeFrame, 1)[0].Authored, Slice(movie, nativeFrame, 1)[0].RngSeed));
            runs.AddRange(Slice(movie, nativeFrame + 1, count - nativeFrame - 1));
            return Commit(movie, runs, baseline, nativeFrame);
        }

        public MovieV2EditResult InsertFrames(MovieV2Document movie, long nativeFrame,
            IReadOnlyList<NativeFrameRun> runs)
        {
            if (movie == null) throw new ArgumentNullException(nameof(movie));
            if (runs == null) throw new ArgumentNullException(nameof(runs));
            var baseline = Baseline(movie);
            if (!baseline.Success) return baseline;
            var count = Count(movie);
            if (nativeFrame < 0 || nativeFrame > count)
                return Failure(baseline, nativeFrame, "Insertion native frame is outside the movie.");
            if (runs.Count == 0)
                return Failure(baseline, nativeFrame, "Insertion requires at least one native frame.");
            var merged = Slice(movie, 0, nativeFrame);
            foreach (var run in runs)
            {
                if (run == null)
                    return Failure(baseline, nativeFrame, "Insertion contains a null run.");
                merged.Add(run);
            }
            merged.AddRange(Slice(movie, nativeFrame, count - nativeFrame));
            return Commit(movie, merged, baseline, nativeFrame);
        }

        public MovieV2EditResult DeleteFrames(MovieV2Document movie, long nativeFrame, long count)
        {
            if (movie == null) throw new ArgumentNullException(nameof(movie));
            var baseline = Baseline(movie);
            if (!baseline.Success) return baseline;
            var total = Count(movie);
            if (nativeFrame < 0 || count < 1 || nativeFrame > total || count > total - nativeFrame)
                return Failure(baseline, nativeFrame, "Deletion range is outside the movie.");
            var runs = Slice(movie, 0, nativeFrame);
            runs.AddRange(Slice(movie, nativeFrame + count, total - nativeFrame - count));
            return Commit(movie, runs, baseline, nativeFrame);
        }

        public MovieV2EditResult ReplaceFrames(MovieV2Document movie, long nativeFrame, long replaceCount,
            IReadOnlyList<NativeFrameRun> replacementRuns)
        {
            if (movie == null) throw new ArgumentNullException(nameof(movie));
            if (replacementRuns == null) throw new ArgumentNullException(nameof(replacementRuns));
            var baseline = Baseline(movie);
            if (!baseline.Success) return baseline;
            var total = Count(movie);
            if (nativeFrame < 0 || nativeFrame > total)
                return Failure(baseline, nativeFrame, "Replacement native frame is outside the movie.");
            if (replaceCount < 0 || replaceCount > total - nativeFrame)
                return Failure(baseline, nativeFrame, "Replacement range is outside the movie.");
            if (replacementRuns.Count == 0 && replaceCount == 0)
                return baseline;
            var runs = Slice(movie, 0, nativeFrame);
            foreach (var run in replacementRuns)
            {
                if (run == null)
                    return Failure(baseline, nativeFrame, "Replacement contains a null run.");
                runs.Add(run);
            }
            if (nativeFrame + replaceCount < total)
                runs.AddRange(Slice(movie, nativeFrame + replaceCount, total - nativeFrame - replaceCount));
            return Commit(movie, runs, baseline, nativeFrame);
        }

        private MovieV2EditResult Baseline(MovieV2Document movie)
        {
            var report = validator.Validate(movie, MovieV2ValidationContext.CreateDefault());
            if (!report.Success) return new MovieV2EditResult(false, movie, string.Empty, string.Empty, report.Diagnostics);
            return new MovieV2EditResult(true, movie, codec.WriteCanonical(movie), codec.ComputeMovieId(movie),
                Array.Empty<MovieDiagnostic>());
        }

        private MovieV2EditResult Commit(MovieV2Document source, IReadOnlyList<NativeFrameRun> runs,
            MovieV2EditResult baseline, long editedFrame)
        {
            MovieV2Document candidate;
            try { candidate = new MovieV2Document(source.SourceName, source.Header, runs); }
            catch (ArgumentException error) { return Failure(baseline, 0, error.Message); }
            var report = validator.Validate(candidate, MovieV2ValidationContext.CreateDefault());
            if (!report.Success)
                return new MovieV2EditResult(false, source, baseline.CanonicalText, baseline.MovieId, report.Diagnostics);
            candidate = MovieV2RangeEditor.AuthorFrom(candidate, editedFrame);
            var text = codec.WriteCanonical(candidate);
            var normalized = codec.Parse(new StringReader(text), source.SourceName);
            if (!normalized.Success || normalized.Document == null)
                return new MovieV2EditResult(false, source, baseline.CanonicalText, baseline.MovieId,
                    normalized.Diagnostics);
            return new MovieV2EditResult(true, normalized.Document, text, codec.ComputeMovieId(normalized.Document),
                Array.Empty<MovieDiagnostic>());
        }

        private static MovieV2EditResult Failure(MovieV2EditResult baseline, long frame, string message)
            => new MovieV2EditResult(false, baseline.Movie, baseline.CanonicalText, baseline.MovieId,
                new[] { new MovieDiagnostic(MovieDiagnosticCodes.InvalidCommand,
                    new MovieSourceSpan(baseline.Movie.SourceName, 1, 1, 1),
                    "Native frame " + frame + ": " + message, "Choose a valid edit range or sample sequence.") });

        private static long Count(MovieV2Document movie)
        {
            long count = 0;
            foreach (var run in movie.Runs) count += run.RepeatCount;
            return count;
        }

        private static List<NativeFrameRun> Slice(MovieV2Document movie, long start, long count)
        {
            var output = new List<NativeFrameRun>();
            if (count == 0) return output;
            var end = start + count;
            long position = 0;
            foreach (var run in movie.Runs)
            {
                var runEnd = position + run.RepeatCount;
                var overlapStart = Math.Max(start, position);
                var overlapEnd = Math.Min(end, runEnd);
                if (overlapEnd > overlapStart)
                    output.Add(new NativeFrameRun(overlapEnd - overlapStart, run.Samples, run.Span, run.FramesPerSecond, run.Authored, run.RngSeed));
                position = runEnd;
                if (position >= end) break;
            }
            return output;
        }
    }
}
