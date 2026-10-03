using System;
using System.Collections.Generic;

namespace HollowKnightTAS.Core.Movie
{
    // Logical lifetime only; actual audio playback/rendering remains Unity-owned.
    public sealed class PooledAudioLifetime
    {
        private readonly List<Voice> voices = new List<Voice>();
        private double lastTime;
        private bool sourcePaused;
        public PooledAudioLifetime(double now) { lastTime = now; }

        public void Advance(double now, double pitch, bool paused)
        {
            if (double.IsNaN(now) || double.IsInfinity(now) || now < lastTime
                || double.IsNaN(pitch) || double.IsInfinity(pitch))
                throw new InvalidOperationException("Invalid virtual pooled-audio clock.");
            var elapsed = now - lastTime;
            lastTime = now;
            if (paused || sourcePaused) return;
            for (var index = voices.Count - 1; index >= 0; index--)
            {
                var voice = voices[index];
                if (voice.Loop) continue;
                voice.Remaining -= elapsed * Math.Abs(pitch);
                if (voice.Remaining <= 0) voices.RemoveAt(index);
            }
        }

        public void Play(double clipSeconds, bool loop, bool oneShot)
        {
            if (double.IsNaN(clipSeconds) || double.IsInfinity(clipSeconds) || clipSeconds < 0)
                throw new ArgumentOutOfRangeException(nameof(clipSeconds));
            if (!oneShot) voices.RemoveAll(voice => !voice.OneShot);
            sourcePaused = false;
            if (clipSeconds > 0 || loop)
                voices.Add(new Voice { Remaining = clipSeconds, Loop = loop, OneShot = oneShot });
            if (voices.Count > 256) throw new InvalidOperationException("Pooled audio overlap budget exceeded.");
        }

        public bool IsPlaying => !sourcePaused && voices.Count != 0;
        public void SetPaused(bool value) => sourcePaused = value;
        public void Stop() { voices.Clear(); sourcePaused = false; }

        // Copy every mutable field; observing state must not advance its clock.
        public Snapshot Capture() => new Snapshot(lastTime, sourcePaused,
            voices.ConvertAll(v => new VoiceSnapshot(v.Remaining, v.Loop, v.OneShot)).ToArray());

        public sealed class Snapshot
        {
            public double LastTime { get; }
            public bool SourcePaused { get; }
            public VoiceSnapshot[] Voices { get; }
            internal Snapshot(double time, bool paused, VoiceSnapshot[] voices)
            { LastTime = time; SourcePaused = paused; Voices = voices; }
        }
        public sealed class VoiceSnapshot
        {
            public double Remaining { get; }
            public bool Loop { get; }
            public bool OneShot { get; }
            internal VoiceSnapshot(double remaining, bool loop, bool oneShot)
            { Remaining = remaining; Loop = loop; OneShot = oneShot; }
        }

        private sealed class Voice
        {
            internal double Remaining;
            internal bool Loop, OneShot;
        }
    }
}
