using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using HollowKnightTAS.Core.Input;
using HollowKnightTAS.Core.Ledger;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.Core.Playback
{
    public sealed class RecordingContext
    {
        public RecordingContext(string baselineId, string baselineSha256)
        {
            BaselineId = Require(baselineId, nameof(baselineId));
            BaselineSha256 = Require(baselineSha256, nameof(baselineSha256));
        }

        public string BaselineId { get; }
        public string BaselineSha256 { get; }

        private static string Require(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("A non-empty value is required.", name);
            }

            return value;
        }
    }

    public sealed class PlaybackContext
    {
        public PlaybackContext(
            string manifestSha256,
            string baselineId,
            string baselineSha256,
            int sceneEpoch,
            bool allowSceneTransitions = false)
            : this(manifestSha256, baselineId, baselineSha256, sceneEpoch,
                allowSceneTransitions, TasAction.None)
        {
        }

        public PlaybackContext(
            string manifestSha256,
            string baselineId,
            string baselineSha256,
            int sceneEpoch,
            bool allowSceneTransitions,
            TasAction initialHeld)
        {
            ManifestSha256 = manifestSha256
                             ?? throw new ArgumentNullException(nameof(manifestSha256));
            BaselineId = baselineId
                         ?? throw new ArgumentNullException(nameof(baselineId));
            BaselineSha256 = baselineSha256
                             ?? throw new ArgumentNullException(nameof(baselineSha256));
            if (sceneEpoch < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sceneEpoch));
            }

            SceneEpoch = sceneEpoch;
            AllowSceneTransitions = allowSceneTransitions;
            InitialHeld = initialHeld;
        }

        public string ManifestSha256 { get; }
        public string BaselineId { get; }
        public string BaselineSha256 { get; }
        public int SceneEpoch { get; }
        public bool AllowSceneTransitions { get; }
        public TasAction InitialHeld { get; }
    }

    public sealed class PlaybackStartResult
    {
        public PlaybackStartResult(
            bool success,
            string error,
            InputSample? firstInput,
            IReadOnlyList<PlaybackEvent> events)
        {
            Success = success;
            Error = error;
            FirstInput = firstInput;
            Events = events;
        }

        public bool Success { get; }
        public string Error { get; }
        public InputSample? FirstInput { get; }
        public IReadOnlyList<PlaybackEvent> Events { get; }
    }

    public sealed class PlaybackTickResult
    {
        public PlaybackTickResult(
            bool success,
            string error,
            InputSample consumedInput,
            InputSample nextInput,
            bool releaseBoundary,
            IReadOnlyList<PlaybackEvent> events)
        {
            Success = success;
            Error = error;
            ConsumedInput = consumedInput;
            NextInput = nextInput;
            ReleaseBoundary = releaseBoundary;
            Events = events;
        }

        public bool Success { get; }
        public string Error { get; }
        public InputSample ConsumedInput { get; }
        public InputSample NextInput { get; }
        public bool ReleaseBoundary { get; }
        public IReadOnlyList<PlaybackEvent> Events { get; }
    }

    public sealed class PlaybackStopResult
    {
        public PlaybackStopResult(
            bool success,
            PlaybackStopReason reason,
            InputSample releaseInput,
            string error)
        {
            Success = success;
            Reason = reason;
            ReleaseInput = releaseInput;
            Error = error;
        }

        public bool Success { get; }
        public PlaybackStopReason Reason { get; }
        public InputSample ReleaseInput { get; }
        public string Error { get; }
    }

    public sealed class PlaybackStateMachine
    {
        private MovieCursor? cursor;
        private PlaybackContext? context;
        private InputSample preparedInput;
        private ulong lastRawInputTick;
        private bool hasRawInputTick;
        private bool rawInputContinuitySuspended;
        private int lastSceneEpoch;

        public PlaybackMode Mode { get; private set; } = PlaybackMode.Idle;
        public PlaybackStopReason? StopReason { get; private set; }
        public string FaultMessage { get; private set; } = string.Empty;

        public PlaybackStartResult StartRecording(RecordingContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (Mode != PlaybackMode.Idle)
            {
                return StartFailure("Only Idle can enter Recording.");
            }

            Mode = PlaybackMode.Recording;
            return new PlaybackStartResult(
                true,
                string.Empty,
                null,
                Array.Empty<PlaybackEvent>());
        }

        public PlaybackStartResult StartReplay(
            MovieDocument movie,
            PlaybackContext context)
        {
            if (movie == null)
            {
                throw new ArgumentNullException(nameof(movie));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (Mode != PlaybackMode.Idle)
            {
                return StartFailure("Only Idle can enter Replaying.");
            }

            if (!string.Equals(
                    movie.Header.ManifestSha256,
                    context.ManifestSha256,
                    StringComparison.Ordinal))
            {
                Trace.TraceWarning(
                    $"PlaybackStateMachine.StartReplay: movie manifest SHA-256 ({movie.Header.ManifestSha256}) differs from runtime ({context.ManifestSha256}); continuing anyway.");
            }

            if (!string.Equals(
                    movie.Header.BaselineId,
                    context.BaselineId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    movie.Header.BaselineSha256,
                    context.BaselineSha256,
                    StringComparison.Ordinal))
            {
                Trace.TraceWarning(
                    $"PlaybackStateMachine.StartReplay: movie baseline ({movie.Header.BaselineId}/{movie.Header.BaselineSha256}) differs from runtime ({context.BaselineId}/{context.BaselineSha256}); continuing anyway.");
            }

            this.context = context;
            cursor = new MovieCursor(movie, context.InitialHeld);
            hasRawInputTick = false;
            rawInputContinuitySuspended = false;
            lastSceneEpoch = context.SceneEpoch;
            StopReason = null;
            FaultMessage = string.Empty;
            if (!cursor.MoveNext())
            {
                return StartFailure("Movie contains no input samples.");
            }

            preparedInput = cursor.CurrentInput;
            Mode = PlaybackMode.Replaying;
            return new PlaybackStartResult(
                true,
                string.Empty,
                preparedInput,
                CopyEvents(cursor.PendingEvents));
        }

        public PlaybackTickResult Advance(TickStamp stamp)
        {
            if (Mode != PlaybackMode.Replaying || cursor == null || context == null)
            {
                return TickFailure("Playback is not replaying.");
            }

            if (stamp.Phase != TickPhase.InControlCommitted)
            {
                return Fault(
                    PlaybackStopReason.RuntimeFault,
                    "Playback advances only at InControlCommitted.");
            }

            if ((!context.AllowSceneTransitions
                 && stamp.SceneEpoch != context.SceneEpoch)
                || (context.AllowSceneTransitions
                    && stamp.SceneEpoch < lastSceneEpoch))
            {
                return Fault(
                    PlaybackStopReason.SceneChanged,
                    context.AllowSceneTransitions
                        ? "Scene epoch moved backwards during replay."
                        : "Scene epoch changed during v1 replay.");
            }

            lastSceneEpoch = stamp.SceneEpoch;

            if (hasRawInputTick
                && (!rawInputContinuitySuspended
                    && stamp.InputTick != lastRawInputTick + 1
                    || rawInputContinuitySuspended
                    && stamp.InputTick <= lastRawInputTick))
            {
                return Fault(
                    PlaybackStopReason.RuntimeFault,
                    "Raw InControl ticks are not contiguous.");
            }

            hasRawInputTick = true;
            lastRawInputTick = stamp.InputTick;
            rawInputContinuitySuspended = false;
            var consumed = preparedInput;
            if (cursor.MoveNext())
            {
                preparedInput = cursor.CurrentInput;
                return new PlaybackTickResult(
                    true,
                    string.Empty,
                    consumed,
                    preparedInput,
                    false,
                    CopyEvents(cursor.PendingEvents));
            }

            preparedInput = cursor.CurrentInput;
            Mode = PlaybackMode.Stopping;
            StopReason = PlaybackStopReason.Completed;
            return new PlaybackTickResult(
                true,
                string.Empty,
                consumed,
                preparedInput,
                true,
                CopyEvents(cursor.PendingEvents));
        }

        public PlaybackStopResult Stop(PlaybackStopReason reason)
        {
            if (Mode == PlaybackMode.Idle)
            {
                return new PlaybackStopResult(
                    false,
                    reason,
                    NeutralAfter(preparedInput),
                    "Playback is already idle.");
            }

            StopReason = reason;
            Mode = PlaybackMode.Stopping;
            preparedInput = NeutralAfter(preparedInput);
            return new PlaybackStopResult(
                true,
                reason,
                preparedInput,
                string.Empty);
        }

        public bool DeclareRawInputSuspension()
        {
            if (Mode != PlaybackMode.Replaying)
            {
                return false;
            }

            rawInputContinuitySuspended = true;
            return true;
        }

        public void CompleteCleanup(bool bindingEquivalent, string error)
        {
            cursor = null;
            context = null;
            hasRawInputTick = false;
            rawInputContinuitySuspended = false;
            lastSceneEpoch = 0;
            if (bindingEquivalent && string.IsNullOrEmpty(error))
            {
                Mode = PlaybackMode.Idle;
                FaultMessage = string.Empty;
                return;
            }

            Mode = PlaybackMode.Faulted;
            FaultMessage = string.IsNullOrEmpty(error)
                ? "Input binding cleanup was not equivalent."
                : error;
        }

        public bool ResetFault()
        {
            if (Mode != PlaybackMode.Faulted)
            {
                return false;
            }

            Mode = PlaybackMode.Idle;
            StopReason = null;
            FaultMessage = string.Empty;
            preparedInput = default;
            rawInputContinuitySuspended = false;
            return true;
        }

        private PlaybackTickResult Fault(
            PlaybackStopReason reason,
            string message)
        {
            StopReason = reason;
            FaultMessage = message;
            Mode = PlaybackMode.Stopping;
            preparedInput = NeutralAfter(preparedInput);
            return new PlaybackTickResult(
                false,
                message,
                default,
                preparedInput,
                true,
                Array.Empty<PlaybackEvent>());
        }

        private static PlaybackStartResult StartFailure(string error)
        {
            return new PlaybackStartResult(
                false,
                error,
                null,
                Array.Empty<PlaybackEvent>());
        }

        private static PlaybackTickResult TickFailure(string error)
        {
            return new PlaybackTickResult(
                false,
                error,
                default,
                default,
                false,
                Array.Empty<PlaybackEvent>());
        }

        private static InputSample NeutralAfter(InputSample previous)
        {
            return InputSample.FromHeld(
                checked(previous.InputTick + 1),
                TasAction.None,
                previous.Held);
        }

        private static IReadOnlyList<PlaybackEvent> CopyEvents(
            IReadOnlyList<PlaybackEvent> events)
        {
            return new ReadOnlyCollection<PlaybackEvent>(
                new List<PlaybackEvent>(events));
        }
    }
}
