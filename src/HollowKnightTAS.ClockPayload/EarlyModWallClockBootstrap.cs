using System;
using System.Threading;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.ClockPayload
{
    // Native startup calls this on the engine thread before its first PlayerLoop.
    // The later ClockController bootstrap intentionally waits for Menu_Title.
    public static class EarlyModWallClockBootstrap
    {
        private static int installed;
        private static CustomKnightModWallClockPolicy? policy;
        private static IsolatedOnlinePolicy? online;

        public static void Install()
        {
            if (!NativeExecutionProfile.UsesModWallClock(NativeExecutionProfile.SelectedProfileId)
                || Environment.GetEnvironmentVariable("HKTAS_FULL_RUN_V2") != "1")
                throw new InvalidOperationException("Early Mod wall clocks require the V5 full-run profile.");
            if (Interlocked.Exchange(ref installed, 1) != 0)
                throw new InvalidOperationException("Early Mod wall clocks were installed more than once.");
            if (NativeExecutionProfile.SelectedProfileId == NativeExecutionProfile.IsolatedOnlineProfileId)
                online = new IsolatedOnlinePolicy();
            policy = new CustomKnightModWallClockPolicy();
        }

        public static void Verify()
        {
            if (policy == null) throw new InvalidOperationException("Early Mod wall clocks are unavailable.");
            policy.Verify();
            online?.Verify();
        }

        public static string[] CaptureSessionAchievements()
            => online?.Capture() ?? Array.Empty<string>();

        public static void VerifySessionOnline() => online?.Verify();

        public static void TestSessionOnline()
        {
            if (Environment.GetEnvironmentVariable("HKTAS_LOADING_VERIFY_ONLINE") != "1") return;
            if (online == null) throw new InvalidOperationException("Online isolation probe requires V10.");
            online.SelfTest();
        }

        internal static void Dispose()
        {
            policy?.Dispose();
            policy = null;
            online?.Dispose();
            online = null;
        }
    }
}
