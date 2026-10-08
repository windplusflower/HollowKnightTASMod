using System;
using System.Collections.Generic;
using System.Linq;

namespace HollowKnightTAS.Core.Movie
{
    public static class MovieV2Prefix
    {
        public static MovieV2Document Take(MovieV2Document movie, long count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            var runs = new List<NativeFrameRun>();
            foreach (var run in movie.Runs)
            {
                if (count == 0) break;
                var length = Math.Min(count, run.RepeatCount);
                runs.Add(new NativeFrameRun(length, run.Samples, run.Span, run.FramesPerSecond, run.Authored, run.RngSeed));
                count -= length;
            }
            if (count != 0) throw new ArgumentOutOfRangeException(nameof(count));
            return new MovieV2Document(movie.SourceName, movie.Header, runs);
        }

        public static bool Matches(MovieV2Document original, MovieV2Document updated, long frame)
        {
            var codec = new MovieV2Codec();
            return codec.WriteCanonical(WithoutIdleCustomKeys(Take(original, frame)))
                == codec.WriteCanonical(WithoutIdleCustomKeys(Take(updated, frame)));
        }

        // A declared custom key that is never held reads as released, the same as an
        // undeclared key, so adding or removing it does not change executed input.
        private static MovieV2Document WithoutIdleCustomKeys(MovieV2Document movie)
        {
            var runs = movie.Runs.Select(run => new NativeFrameRun(run.RepeatCount,
                run.Samples.Where(s => s.Channel != GameInputChannel.CustomKey || s.Values[1] != 0).ToArray(),
                run.Span, run.FramesPerSecond, run.Authored, run.RngSeed)).ToArray();
            return new MovieV2Document(movie.SourceName, CustomKeyInput.WithUsedKeys(movie.Header, Array.Empty<short>(), runs), runs);
        }
    }
}
