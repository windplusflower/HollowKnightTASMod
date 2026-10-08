using System;
using System.Collections.Generic;
using System.Linq;

namespace HollowKnightTAS.Core.Movie
{
    public static class MovieV2RangeEditor
    {
        public static MovieV2Document SetRngSeed(MovieV2Document movie, long frame, int? seed)
        {
            long position = 0;
            foreach (var run in movie.Runs)
            {
                if (frame >= position && frame < position + run.RepeatCount && run.RngSeed == seed)
                    return movie;
                position += run.RepeatCount;
            }
            return Transform(movie, frame, 1, r => new NativeFrameRun(1, r.Samples, r.Span,
                r.FramesPerSecond, r.Authored, seed));
        }

        // After a causal edit, the old suffix is input intent, not evidence of
        // the new world's action-set order or press/release edges.
        public static MovieV2Document AuthorFrom(MovieV2Document movie, long frame)
        {
            var total = movie.Runs.Sum(r => r.RepeatCount);
            if (frame < 0 || frame > total) throw new ArgumentOutOfRangeException(nameof(frame));
            if (frame == total) return movie;
            return Transform(movie, frame, total - frame, r => r);
        }

        public static MovieV2Document SetFrameRate(MovieV2Document movie, long start, long count, decimal fps)
        {
            if (!MovieFrameRate.IsValid(fps)) throw new ArgumentOutOfRangeException(nameof(fps));
            return Transform(movie, start, count, r => new NativeFrameRun(r.RepeatCount, r.Samples, r.Span, fps, r.Authored, r.RngSeed));
        }

        // Authored runs express held input intent; InControl computes edges through its
        // normal update. Recorded runs retain their exact ordered sample assertions.
        public static MovieV2Document Paint(MovieV2Document movie, long start, long count, string action, bool held)
        {
            return Transform(movie, start, count, run =>
            {
                var samples = run.Samples.ToList();
                if (CustomKeyInput.TryAction(action, out var key))
                {
                    samples.RemoveAll(s => s.Channel == GameInputChannel.CustomKey && s.Values[0] == key);
                    samples.Add(CustomKeyInput.Sample(key, held));
                }
                foreach (var channel in new[] { GameInputChannel.Hero, GameInputChannel.PreMenu, GameInputChannel.Binder })
                {
                    int index = ActionIndex(channel, action);
                    if (index < 0) continue;
                    if (!samples.Any(s => s.Channel == channel))
                        samples.Add(new GameInputSample(channel, new short[MovieProtocolV2.ExpectedValueCount(channel)], null));
                    for (int i = 0; i < samples.Count; i++)
                    {
                        var sample = samples[i];
                        if (sample.Channel != channel) continue;
                        var values = sample.Values.ToArray();
                        values[index] = held ? short.MaxValue : (short)0;
                        samples[i] = new GameInputSample(channel, values, sample.Mouse);
                    }
                }
                return new NativeFrameRun(run.RepeatCount, samples, run.Span, run.FramesPerSecond, true, run.RngSeed);
            });
        }

        public static int ActionIndex(GameInputChannel channel, string action)
        {
            if (channel == GameInputChannel.Hero)
            {
                switch (action)
                {
                    case "Left": return 0; case "Right": return 1; case "Up": return 2; case "Down": return 3;
                    case "Submit": return 8; case "Cancel": return 9; case "Jump": return 10;
                    case "Dash": return 12; case "SuperDash": return 13; case "DreamNail": return 14;
                    case "Attack": return 15; case "Cast": return 16; case "QuickMap": return 18;
                    case "QuickCast": return 19; case "OpenInventory": return 22; case "PaneRight": return 23;
                    case "PaneLeft": return 24; case "Pause": return 25;
                }
            }
            if (channel == GameInputChannel.PreMenu || channel == GameInputChannel.Binder)
            {
                switch (action)
                { case "Submit": return 0; case "Cancel": return 1; case "Left": return 2;
                    case "Right": return 3; case "Up": return 4; case "Down": return 5; }
            }
            return -1;
        }

        private static MovieV2Document Transform(MovieV2Document movie, long start, long count, Func<NativeFrameRun, NativeFrameRun> edit)
        {
            var total = movie.Runs.Sum(r => r.RepeatCount);
            if (start < 0 || count < 1 || count > total - start) throw new ArgumentOutOfRangeException(nameof(count));
            var result = new List<NativeFrameRun>();
            long position = 0, end = start + count;
            foreach (var run in movie.Runs)
            {
                var next = position + run.RepeatCount;
                var a = Math.Max(start, position); var b = Math.Min(end, next);
                NativeFrameRun Slice(long n) => new NativeFrameRun(n, run.Samples, run.Span, run.FramesPerSecond, run.Authored, run.RngSeed);
                if (a >= b) result.Add(run);
                else
                {
                    if (a > position) result.Add(Slice(a - position));
                    result.Add(edit(Slice(b - a)));
                    if (b < next) result.Add(Slice(next - b));
                }
                position = next;
            }
            position = 0;
            for (var i = 0; i < result.Count; i++)
            {
                var run = result[i];
                if (position >= start)
                    result[i] = new NativeFrameRun(run.RepeatCount, run.Samples, run.Span,
                        run.FramesPerSecond, true, run.RngSeed);
                position += run.RepeatCount;
            }
            return new MovieV2Document(movie.SourceName, movie.Header, result);
        }
    }
}
