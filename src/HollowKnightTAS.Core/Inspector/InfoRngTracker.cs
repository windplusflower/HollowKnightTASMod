using System;
using System.Globalization;

namespace HollowKnightTAS.Core.Inspector
{
    /// <summary>Tracks Unity Xorshift128 RNG advancement across frames without mutating Random.state.</summary>
    public sealed class InfoRngTracker
    {
        public const int DefaultMaxStepsPerFrame = 65536;

        private uint lastS0;
        private uint lastS1;
        private uint lastS2;
        private uint lastS3;

        public bool HasValue { get; private set; }
        public long RollTimes { get; private set; }
        public int LastDelta { get; private set; }
        public bool LastStepOverflowed { get; private set; }

        public string? Formatted => HasValue ? Format(RollTimes, LastDelta) : null;

        public static void StepXorshift128(ref uint s0, ref uint s1, ref uint s2, ref uint s3)
        {
            uint t = s0 ^ (s0 << 11);
            s0 = s1;
            s1 = s2;
            s2 = s3;
            s3 = (s3 ^ (s3 >> 19)) ^ (t ^ (t >> 8));
        }

        public static string Format(long rollTimes, int delta)
            => rollTimes.ToString(CultureInfo.InvariantCulture) + " +" + delta.ToString(CultureInfo.InvariantCulture);

        public void SyncBaseline(uint s0, uint s1, uint s2, uint s3)
        {
            lastS0 = s0;
            lastS1 = s1;
            lastS2 = s2;
            lastS3 = s3;
            HasValue = true;
            LastDelta = 0;
            LastStepOverflowed = false;
        }

        public void Observe(uint s0, uint s1, uint s2, uint s3, int maxSteps = DefaultMaxStepsPerFrame)
        {
            if (maxSteps <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxSteps));
            if (!HasValue)
            {
                SyncBaseline(s0, s1, s2, s3);
                return;
            }
            if (s0 == lastS0 && s1 == lastS1 && s2 == lastS2 && s3 == lastS3)
            {
                LastDelta = 0;
                LastStepOverflowed = false;
                return;
            }

            uint c0 = lastS0;
            uint c1 = lastS1;
            uint c2 = lastS2;
            uint c3 = lastS3;
            for (int step = 1; step <= maxSteps; step++)
            {
                StepXorshift128(ref c0, ref c1, ref c2, ref c3);
                if (c0 == s0 && c1 == s1 && c2 == s2 && c3 == s3)
                {
                    RollTimes += step;
                    LastDelta = step;
                    LastStepOverflowed = false;
                    lastS0 = s0;
                    lastS1 = s1;
                    lastS2 = s2;
                    lastS3 = s3;
                    return;
                }
            }

            // Re-seed or non-sequential boundary jump: resync cleanly without hanging.
            LastDelta = 0;
            LastStepOverflowed = true;
            lastS0 = s0;
            lastS1 = s1;
            lastS2 = s2;
            lastS3 = s3;
        }
    }
}
