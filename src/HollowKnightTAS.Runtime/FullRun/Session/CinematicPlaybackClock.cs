using System;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed class CinematicPlaybackClock
    {
        private readonly double duration;
        private long lastFrame;
        private double compensation;
        public double Elapsed { get; private set; }
        public bool Playing { get; private set; }

        public CinematicPlaybackClock(double duration)
        {
            if (double.IsNaN(duration) || double.IsInfinity(duration) || duration <= 0)
                throw new ArgumentOutOfRangeException(nameof(duration));
            this.duration = duration;
        }

        public void Play(long frame)
        {
            if (Playing) return;
            Elapsed = 0;
            compensation = 0;
            lastFrame = frame;
            Playing = true;
        }

        public void Stop() => Playing = false;

        public void Update(long frame, double delta, bool looping)
        {
            if (!Playing || frame == lastFrame) return;
            if (frame < lastFrame || double.IsNaN(delta) || double.IsInfinity(delta) || delta < 0)
                throw new ArgumentOutOfRangeException(nameof(frame));
            lastFrame = frame;
            var adjusted = delta - compensation;
            var next = Elapsed + adjusted;
            compensation = (next - Elapsed) - adjusted;
            Elapsed = next;
            if (Elapsed < duration) return;
            if (looping) Elapsed %= duration;
            else { Elapsed = duration; Playing = false; }
        }
    }
}
