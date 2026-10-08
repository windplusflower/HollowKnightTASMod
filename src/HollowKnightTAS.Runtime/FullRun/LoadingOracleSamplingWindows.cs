using System;
using System.Collections.Generic;
using System.Globalization;

namespace HollowKnightTAS.Runtime.FullRun
{
    // A declaration of observed frames, never a stride or a post-capture filter.
    // Frames inside each interval still require both before and after samples.
    internal sealed class LoadingOracleSamplingWindows
    {
        private readonly List<Window> windows;

        private LoadingOracleSamplingWindows(List<Window> windows, bool explicitWindows)
        {
            this.windows = windows;
            ExplicitWindows = explicitWindows;
            foreach (var window in windows) GameplayFrames += window.end - window.first;
        }

        internal bool ExplicitWindows { get; }
        internal long GameplayFrames { get; }
        internal Window[] Snapshot() => windows.ToArray();

        internal static LoadingOracleSamplingWindows Parse(string? text, long first, long end)
        {
            if (first < 0 || end <= first || end - first > 10000)
                throw new ArgumentException("Invalid bounded oracle run.");
            if (text == null)
                return new LoadingOracleSamplingWindows(new List<Window> { new Window(first, end) }, false);
            if (text.Length == 0)
                throw new ArgumentException("Oracle windows cannot be empty.");
            var result = new List<Window>();
            var priorEnd = first;
            foreach (var interval in text.Split(','))
            {
                var bounds = interval.Split(':');
                if (bounds.Length != 2
                    || !long.TryParse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture, out var start)
                    || !long.TryParse(bounds[1], NumberStyles.None, CultureInfo.InvariantCulture, out var stop)
                    || start < first || stop > end || start >= stop || start < priorEnd)
                    throw new ArgumentException("Oracle windows must be ordered, non-overlapping first:end intervals within the run.");
                result.Add(new Window(start, stop));
                priorEnd = stop;
            }
            return new LoadingOracleSamplingWindows(result, true);
        }

        internal bool Contains(long frame)
        {
            foreach (var window in windows)
            {
                if (frame < window.first) return false;
                if (frame < window.end) return true;
            }
            return false;
        }

        internal bool IsEndpoint(long frame, string phase)
        {
            foreach (var window in windows)
                if ((phase == "before" && frame == window.first)
                    || (phase == "after" && frame == window.end - 1)) return true;
            return false;
        }

        internal sealed class Window
        {
            internal Window(long first, long end) { this.first = first; this.end = end; }
            public long first { get; }
            public long end { get; }
        }
    }
}
