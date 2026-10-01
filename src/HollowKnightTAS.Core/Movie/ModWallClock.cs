using System;

namespace HollowKnightTAS.Core.Movie
{
    // V5 only. Registered Mod call sites use the same engine realtime in
    // recording and replay; IPC, watchdogs, logging and file dates remain real.
    public static class ModWallClock
    {
        public const string PolicyId = "registered-mod-local-date-2000-engine-realtime-100ns-v1";
        private static readonly DateTime Origin = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Local);

        public static DateTime LocalNow(double realtimeSeconds)
        {
            if (double.IsNaN(realtimeSeconds) || double.IsInfinity(realtimeSeconds) || realtimeSeconds < 0)
                throw new ArgumentOutOfRangeException(nameof(realtimeSeconds));
            var ticks = Math.Round(realtimeSeconds * TimeSpan.TicksPerSecond, MidpointRounding.ToEven);
            if (ticks > DateTime.MaxValue.Ticks - Origin.Ticks)
                throw new ArgumentOutOfRangeException(nameof(realtimeSeconds));
            return Origin.AddTicks(checked((long)ticks));
        }
    }
}
