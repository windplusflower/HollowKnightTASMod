using System;

namespace HollowKnightTAS.Runtime.FullRun
{
    /// <summary>Captures native frames after the start boundary through the inclusive Movie end boundary.</summary>
    public sealed class FullRunVideoExportRange
    {
        private long lastNativeFrame;
        private long lastMovieFrame;

        public FullRunVideoExportRange(long nativeStartFrame, long movieStartFrame,
            long movieLength, long requestedEndMovieFrame, int maximumFrames)
        {
            if (nativeStartFrame < 0 || movieStartFrame < 0 || movieLength < 1)
                throw new ArgumentOutOfRangeException(nameof(movieStartFrame));
            if (requestedEndMovieFrame < -1)
                throw new ArgumentOutOfRangeException(nameof(requestedEndMovieFrame));
            var end = requestedEndMovieFrame == -1 ? movieLength : requestedEndMovieFrame;
            if (end <= movieStartFrame || end > movieLength)
                throw new ArgumentOutOfRangeException(nameof(requestedEndMovieFrame),
                    "Video range must end after the current Movie frame and within the loaded Movie.");
            if (maximumFrames <= end - movieStartFrame)
                throw new ArgumentOutOfRangeException(nameof(maximumFrames),
                    "Video safety limit must exceed the input range to allow loading frames.");
            StartNativeFrame = lastNativeFrame = nativeStartFrame;
            StartMovieFrame = lastMovieFrame = movieStartFrame;
            EndMovieFrame = end;
        }

        public long StartNativeFrame { get; }
        public long StartMovieFrame { get; }
        public long EndMovieFrame { get; }
        public bool IsComplete { get; private set; }

        /// <returns>True only for the native frame that completes the requested Movie range.</returns>
        public bool CompleteNativeFrame(long completedNativeFrame, long completedMovieFrame)
        {
            if (IsComplete)
                throw new InvalidOperationException("Video range has already completed.");
            if (completedNativeFrame != lastNativeFrame + 1)
                throw new InvalidOperationException("Video native frame boundary was not sequential.");
            if (completedMovieFrame < lastMovieFrame || completedMovieFrame > lastMovieFrame + 1
                || completedMovieFrame > EndMovieFrame)
                throw new InvalidOperationException("Video Movie frame escaped the selected range.");
            lastNativeFrame = completedNativeFrame;
            lastMovieFrame = completedMovieFrame;
            return IsComplete = completedMovieFrame == EndMovieFrame;
        }
    }
}
