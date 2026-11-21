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
        private static CanonicalParticleRandomPolicy? particles;

        public static void Install()
        {
            if (!NativeExecutionProfile.UsesModWallClock(NativeExecutionProfile.SelectedProfileId)
                || Environment.GetEnvironmentVariable("HKTAS_FULL_RUN_V2") != "1")
            {
                UnityEngine.Debug.LogWarning("[HKTAS] Early Mod wall clocks skipped: requires V5+ full-run profile.");
                return;
            }
            if (Interlocked.Exchange(ref installed, 1) != 0)
            {
                UnityEngine.Debug.LogWarning("[HKTAS] Early Mod wall clocks were already installed; skipping duplicate install.");
                return;
            }
            try { InstallPolicies(); }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning("[HKTAS] Early policy installation encountered an error and was skipped: "
                    + exception.GetType().FullName + ": " + exception.Message);
            }
        }

        private static void InstallPolicies()
        {
            if (NativeExecutionProfile.UsesCanonicalParticles(NativeExecutionProfile.SelectedProfileId))
                particles = new CanonicalParticleRandomPolicy();
            if (NativeExecutionProfile.UsesIsolatedOnline(NativeExecutionProfile.SelectedProfileId))
                online = new IsolatedOnlinePolicy();
            policy = new CustomKnightModWallClockPolicy();
        }

        public static void Verify()
        {
            if (policy == null)
            {
                UnityEngine.Debug.LogWarning("[HKTAS] Early Mod wall clocks were not installed; skipping Verify().");
            }
            else
            {
                policy.Verify();
            }
            online?.Verify();
        }

        public static string[] CaptureSessionAchievements()
            => online?.Capture() ?? Array.Empty<string>();

        public static void VerifySessionOnline() => online?.Verify();

        public static uint[] CaptureParticleRandomState() => particles?.Capture() ?? Array.Empty<uint>();

        internal static void RestoreCheckpointParticleRandomState(uint[] source)
        {
            if (particles == null || !particles.IsSupported)
            {
                UnityEngine.Debug.LogWarning("[HKTAS] Skipping particle random checkpoint restore: particle policy is unavailable or passthrough/unsupported.");
                return;
            }
            particles.RestoreCheckpoint(source);
        }

        public static void TestSessionOnline()
        {
            if (Environment.GetEnvironmentVariable("HKTAS_LOADING_VERIFY_ONLINE") != "1") return;
            if (online == null)
            {
                UnityEngine.Debug.LogWarning("[HKTAS] Online isolation probe skipped because online policy is not installed.");
                return;
            }
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
