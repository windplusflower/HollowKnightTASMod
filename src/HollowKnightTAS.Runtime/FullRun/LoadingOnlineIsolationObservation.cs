using System;
using System.Linq;
using System.Reflection;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class LoadingOnlineIsolationObservation
    {
        private static Action? verify;
        internal static void CheckFailure()
        {
            if (NativeExecutionProfile.SelectedProfileId != NativeExecutionProfile.IsolatedOnlineProfileId) return;
            if (verify == null)
            {
                var payload = AppDomain.CurrentDomain.GetAssemblies()
                    .Single(assembly => assembly.GetName().Name == "HollowKnightTAS.ClockPayload");
                var method = payload.GetType("HollowKnightTAS.ClockPayload.EarlyModWallClockBootstrap", true)!
                    .GetMethod("VerifySessionOnline", BindingFlags.Public | BindingFlags.Static)
                    ?? throw new MissingMethodException("V10 online verifier is unavailable.");
                verify = (Action)Delegate.CreateDelegate(typeof(Action), method);
            }
            verify();
        }
    }
}
