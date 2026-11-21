using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using HollowKnightTAS.Core.Input;

namespace HollowKnightTAS.Core.Movie
{
    public sealed class MovieValidationContext
    {
        private readonly HashSet<string> allowedSemanticPaths;

        public MovieValidationContext(
            long maxExpandedTicks,
            ISet<string> allowedSemanticPaths,
            string? expectedManifestSha256 = null,
            string? expectedBaselineSha256 = null)
        {
            if (maxExpandedTicks <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxExpandedTicks),
                    maxExpandedTicks,
                    "Expanded tick limit must be positive.");
            }

            if (allowedSemanticPaths == null)
            {
                throw new ArgumentNullException(nameof(allowedSemanticPaths));
            }

            MaxExpandedTicks = maxExpandedTicks;
            this.allowedSemanticPaths = new HashSet<string>(
                allowedSemanticPaths,
                StringComparer.Ordinal);
            ExpectedManifestSha256 = expectedManifestSha256;
            ExpectedBaselineSha256 = expectedBaselineSha256;
        }

        public long MaxExpandedTicks { get; }
        public ISet<string> AllowedSemanticPaths =>
            new HashSet<string>(allowedSemanticPaths, StringComparer.Ordinal);
        public string? ExpectedManifestSha256 { get; }
        public string? ExpectedBaselineSha256 { get; }

        internal bool IsSemanticPathAllowed(string value)
        {
            return allowedSemanticPaths.Contains(value);
        }

        public static MovieValidationContext CreateDefault()
        {
            return new MovieValidationContext(
                MovieProtocolV1.DefaultMaxExpandedTicks,
                MovieProtocolV1.DefaultSemanticPaths);
        }
    }

    public sealed class MovieValidationReport
    {
        private readonly MovieDiagnostic[] errors;
        private readonly MovieDiagnostic[] warnings;
        private readonly MovieDiagnostic[] diagnostics;

        internal MovieValidationReport(
            IEnumerable<MovieDiagnostic> diagnostics,
            long expandedTickCount)
            : this(diagnostics, Array.Empty<MovieDiagnostic>(), expandedTickCount)
        {
        }

        internal MovieValidationReport(
            IEnumerable<MovieDiagnostic> errors,
            IEnumerable<MovieDiagnostic> warnings,
            long expandedTickCount)
        {
            this.errors = new List<MovieDiagnostic>(errors).ToArray();
            this.warnings = new List<MovieDiagnostic>(warnings).ToArray();
            var combined = new List<MovieDiagnostic>(this.errors.Length + this.warnings.Length);
            combined.AddRange(this.errors);
            combined.AddRange(this.warnings);
            diagnostics = combined.ToArray();
            ExpandedTickCount = expandedTickCount;
        }

        public IReadOnlyList<MovieDiagnostic> Diagnostics => diagnostics;
        public IReadOnlyList<MovieDiagnostic> Errors => errors;
        public IReadOnlyList<MovieDiagnostic> Warnings => warnings;
        public long ExpandedTickCount { get; }
        public bool HasErrors => errors.Length > 0;
        public bool Success => errors.Length == 0;
    }

    public sealed class MovieValidator
    {
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public MovieValidationReport Validate(
            MovieDocument movie,
            MovieValidationContext context)
        {
            if (movie == null)
            {
                throw new ArgumentNullException(nameof(movie));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var diagnostics = new List<MovieDiagnostic>();
            var warnings = new List<MovieDiagnostic>();
            ValidateHeader(movie, context, diagnostics, warnings);

            long expandedTicks = 0;
            var expandedLimitReported = false;
            foreach (var command in movie.Commands)
            {
                if (command == null)
                {
                    diagnostics.Add(
                        new MovieDiagnostic(
                            MovieDiagnosticCodes.InvalidCommand,
                            new MovieSourceSpan(movie.SourceName, 1, 1, 1),
                            "Movie contains a null command.",
                            "Remove the null command from the AST."));
                    continue;
                }

                if (command is FrameRunCommand frames)
                {
                    ValidateFrames(frames, diagnostics);
                    if (frames.FrameCount > 0)
                    {
                        if (expandedTicks > long.MaxValue - frames.FrameCount)
                        {
                            expandedTicks = long.MaxValue;
                            if (!expandedLimitReported)
                            {
                                AddExpandedLimit(frames, context, diagnostics);
                                expandedLimitReported = true;
                            }
                        }
                        else
                        {
                            expandedTicks += frames.FrameCount;
                            if (expandedTicks > context.MaxExpandedTicks
                                && !expandedLimitReported)
                            {
                                AddExpandedLimit(frames, context, diagnostics);
                                expandedLimitReported = true;
                            }
                        }
                    }
                }
                else if (command is MarkerCommand marker)
                {
                    ValidateMarker(marker, diagnostics);
                }
                else if (command is CheckpointCommand checkpoint)
                {
                    if (!MovieProtocolV1.IsIdentifier(checkpoint.Identifier))
                    {
                        diagnostics.Add(
                            new MovieDiagnostic(
                                MovieDiagnosticCodes.InvalidIdentifier,
                                checkpoint.Span,
                                "Checkpoint identifier is not valid ASCII.",
                                "Use only A-Z, a-z, 0-9, '.', '_' or '-'."));
                    }
                }
                else if (command is AssertCommand assertion)
                {
                    ValidateAssert(assertion, context, diagnostics);
                }
                else
                {
                    diagnostics.Add(
                        new MovieDiagnostic(
                            MovieDiagnosticCodes.InvalidCommand,
                            command.Span,
                            "AST command type is not supported by Movie v1.",
                            "Use frames, marker, checkpoint or assert."));
                }
            }

            return new MovieValidationReport(diagnostics, warnings, expandedTicks);
        }

        private static void ValidateHeader(
            MovieDocument movie,
            MovieValidationContext context,
            ICollection<MovieDiagnostic> diagnostics,
            ICollection<MovieDiagnostic> warnings)
        {
            var header = movie.Header;
            if (header.ProtocolVersion != MovieProtocolV1.Version)
            {
                diagnostics.Add(
                    new MovieDiagnostic(
                        MovieDiagnosticCodes.UnsupportedVersion,
                        header.GetSpan("hktas", movie.SourceName),
                        "Unsupported movie protocol version '"
                        + header.ProtocolVersion
                        + "'.",
                        "Use HK-TAS Movie v1."));
            }

            ValidateIdentifier(
                header.GameVersion,
                "game version",
                header.GetSpan("game", movie.SourceName),
                diagnostics);
            ValidateIdentifier(
                header.ApiVersion,
                "API version",
                header.GetSpan("api", movie.SourceName),
                diagnostics);

            if (!MovieProtocolV1.IsLowerSha256(header.ManifestSha256))
            {
                diagnostics.Add(
                    new MovieDiagnostic(
                        MovieDiagnosticCodes.InvalidHash,
                        header.GetSpan("manifest-sha256", movie.SourceName),
                        "manifest-sha256 must be 64 lowercase hexadecimal characters.",
                        "Write the canonical lowercase SHA-256 digest."));
            }

            if (!MovieProtocolV1.IsIdentifier(header.BaselineId))
            {
                diagnostics.Add(
                    new MovieDiagnostic(
                        MovieDiagnosticCodes.InvalidIdentifier,
                        header.GetSpan("baseline", movie.SourceName),
                        "Baseline ID is not a valid ASCII identifier.",
                        "Use only A-Z, a-z, 0-9, '.', '_' or '-'."));
            }

            var baselineHashIsNone = string.Equals(
                header.BaselineSha256,
                "none",
                StringComparison.Ordinal);
            if (!baselineHashIsNone
                && !MovieProtocolV1.IsLowerSha256(header.BaselineSha256))
            {
                diagnostics.Add(
                    new MovieDiagnostic(
                        MovieDiagnosticCodes.InvalidHash,
                        header.GetSpan("baseline-sha256", movie.SourceName),
                        "Baseline hash must be 'none' or 64 lowercase hexadecimal characters.",
                        "Write 'none' or the canonical lowercase SHA-256 digest."));
            }

            var baselineIdIsNone = string.Equals(
                header.BaselineId,
                "none",
                StringComparison.Ordinal);
            if (baselineIdIsNone != baselineHashIsNone)
            {
                diagnostics.Add(
                    new MovieDiagnostic(
                        MovieDiagnosticCodes.InvalidBaseline,
                        header.GetSpan("baseline", movie.SourceName),
                        "Baseline ID and hash must either both be 'none' or both be present.",
                        "Use 'baseline none none' or provide both ID and SHA-256."));
            }

            if (!string.Equals(
                    header.TickUnit,
                    MovieProtocolV1.TickUnit,
                    StringComparison.Ordinal))
            {
                diagnostics.Add(
                    new MovieDiagnostic(
                        MovieDiagnosticCodes.InvalidTickUnit,
                        header.GetSpan("tick-unit", movie.SourceName),
                        "tick-unit must be 'input' in Movie v1.",
                        "Use 'tick-unit input'."));
            }

            if (context.ExpectedManifestSha256 != null
                && !string.Equals(
                    header.ManifestSha256,
                    context.ExpectedManifestSha256,
                    StringComparison.Ordinal))
            {
                Trace.TraceWarning(
                    "MovieValidator: movie manifest SHA-256 differs from expected environment; continuing with warning.");
                warnings.Add(
                    new MovieDiagnostic(
                        MovieDiagnosticCodes.ManifestMismatch,
                        header.GetSpan("manifest-sha256", movie.SourceName),
                        "Movie manifest SHA-256 does not match the expected environment.",
                        "Load the matching manifest or regenerate the movie explicitly."));
            }

            if (context.ExpectedBaselineSha256 != null
                && !string.Equals(
                    header.BaselineSha256,
                    context.ExpectedBaselineSha256,
                    StringComparison.Ordinal))
            {
                Trace.TraceWarning(
                    "MovieValidator: movie baseline SHA-256 differs from expected baseline; continuing with warning.");
                warnings.Add(
                    new MovieDiagnostic(
                        MovieDiagnosticCodes.BaselineMismatch,
                        header.GetSpan("baseline-sha256", movie.SourceName),
                        "Movie baseline SHA-256 does not match the expected baseline.",
                        "Select the matching baseline before runtime playback."));
            }
        }

        private static void ValidateFrames(
            FrameRunCommand frames,
            ICollection<MovieDiagnostic> diagnostics)
        {
            if (frames.FrameCount <= 0)
            {
                diagnostics.Add(
                    new MovieDiagnostic(
                        MovieDiagnosticCodes.InvalidCommand,
                        frames.Span,
                        "Frame count must be positive.",
                        "Use a frame count of at least one."));
            }

            if ((frames.HeldActions & ~TasAction.AllGameplay) != TasAction.None)
            {
                diagnostics.Add(
                    new MovieDiagnostic(
                        MovieDiagnosticCodes.UnknownAction,
                        frames.Span,
                        "Frame run contains unknown action bits.",
                        "Use only HK-TAS Movie v1 actions."));
            }

            if ((frames.HeldActions & (TasAction.Left | TasAction.Right))
                == (TasAction.Left | TasAction.Right)
                || (frames.HeldActions & (TasAction.Up | TasAction.Down))
                == (TasAction.Up | TasAction.Down))
            {
                diagnostics.Add(
                    new MovieDiagnostic(
                        MovieDiagnosticCodes.DirectionConflict,
                        frames.Span,
                        "Opposite digital directions are held in one frame run.",
                        "Remove one of left/right or up/down."));
            }

            if (frames.AxisX < -10000
                || frames.AxisX > 10000
                || frames.AxisY < -10000
                || frames.AxisY > 10000)
            {
                diagnostics.Add(
                    new MovieDiagnostic(
                        MovieDiagnosticCodes.AxisRange,
                        frames.Span,
                        "Analog axes must be in [-10000, 10000].",
                        "Clamp x and y to the protocol range."));
            }

            var hasHorizontalDirection =
                (frames.HeldActions & (TasAction.Left | TasAction.Right))
                != TasAction.None;
            var hasVerticalDirection =
                (frames.HeldActions & (TasAction.Up | TasAction.Down))
                != TasAction.None;
            if (frames.HasAnalogAxes
                && (hasHorizontalDirection || hasVerticalDirection))
            {
                diagnostics.Add(
                    new MovieDiagnostic(
                        MovieDiagnosticCodes.AxisDirectionMix,
                        frames.Span,
                        "Digital directions cannot be combined with x/y fields.",
                        "Use either digital directions or analog axes."));
            }
        }

        private static void ValidateMarker(
            MarkerCommand marker,
            ICollection<MovieDiagnostic> diagnostics)
        {
            if (!MovieProtocolV1.HasValidUtf16(marker.Text))
            {
                diagnostics.Add(
                    new MovieDiagnostic(
                        MovieDiagnosticCodes.InvalidCharacter,
                        marker.Span,
                        "Marker contains an unpaired UTF-16 surrogate.",
                        "Replace it with valid Unicode text."));
                return;
            }

            var byteCount = StrictUtf8.GetByteCount(marker.Text);
            if (byteCount > MovieProtocolV1.MaximumMarkerUtf8Bytes)
            {
                diagnostics.Add(
                    new MovieDiagnostic(
                        MovieDiagnosticCodes.MarkerTooLarge,
                        marker.Span,
                        "Marker text is "
                        + byteCount
                        + " UTF-8 bytes; maximum is "
                        + MovieProtocolV1.MaximumMarkerUtf8Bytes
                        + ".",
                        "Shorten the marker text."));
            }
        }

        private static void ValidateAssert(
            AssertCommand assertion,
            MovieValidationContext context,
            ICollection<MovieDiagnostic> diagnostics)
        {
            if (!MovieProtocolV1.IsIdentifier(assertion.SemanticPath))
            {
                diagnostics.Add(
                    new MovieDiagnostic(
                        MovieDiagnosticCodes.InvalidIdentifier,
                        assertion.Span,
                        "Assert semantic path is not a valid ASCII identifier.",
                        "Use a registered dotted semantic path."));
            }
            else if (!context.IsSemanticPathAllowed(assertion.SemanticPath))
            {
                diagnostics.Add(
                    new MovieDiagnostic(
                        MovieDiagnosticCodes.UnknownSemanticPath,
                        assertion.Span,
                        "Semantic path '" + assertion.SemanticPath + "' is not registered.",
                        "Use a v1 registry path or enable it explicitly in the validation context."));
            }

            if (!MovieProtocolV1.IsOperator(assertion.Operator))
            {
                diagnostics.Add(
                    new MovieDiagnostic(
                        MovieDiagnosticCodes.InvalidCommand,
                        assertion.Span,
                        "Assert operator '" + assertion.Operator + "' is not supported.",
                        "Use ==, !=, <, <=, > or >=."));
            }

            if (assertion.ValueIsQuoted)
            {
                if (!MovieProtocolV1.HasValidUtf16(assertion.Value))
                {
                    diagnostics.Add(
                        new MovieDiagnostic(
                            MovieDiagnosticCodes.InvalidCharacter,
                            assertion.Span,
                            "Assert string contains an unpaired UTF-16 surrogate.",
                            "Replace it with valid Unicode text."));
                }
            }
            else if (!MovieProtocolV1.IsCanonicalAssertLiteral(assertion.Value))
            {
                diagnostics.Add(
                    new MovieDiagnostic(
                        MovieDiagnosticCodes.InvalidAssertValue,
                        assertion.Span,
                        "Assert literal is not canonical.",
                        "Use true, false, null or a canonical non-exponent decimal."));
            }
        }

        private static void ValidateIdentifier(
            string value,
            string label,
            MovieSourceSpan span,
            ICollection<MovieDiagnostic> diagnostics)
        {
            if (!MovieProtocolV1.IsIdentifier(value))
            {
                diagnostics.Add(
                    new MovieDiagnostic(
                        MovieDiagnosticCodes.InvalidIdentifier,
                        span,
                        label + " is not a valid ASCII identifier.",
                        "Use only A-Z, a-z, 0-9, '.', '_' or '-'."));
            }
        }

        private static void AddExpandedLimit(
            FrameRunCommand frames,
            MovieValidationContext context,
            ICollection<MovieDiagnostic> diagnostics)
        {
            diagnostics.Add(
                new MovieDiagnostic(
                    MovieDiagnosticCodes.ExpandedTickLimit,
                    frames.Span,
                    "Expanded movie length exceeds "
                    + context.MaxExpandedTicks
                    + " input ticks.",
                    "Reduce frame counts or raise the explicit validation limit."));
        }
    }
}
