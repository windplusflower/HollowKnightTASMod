using System;
using System.Collections.Generic;
using System.Reflection;
using HollowKnightTAS.Core.Movie;
using InControl;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class LoadingInputCounterObservation
    {
        internal static void Log(string boundary)
        {
            if (!NativeExecutionProfile.UsesSerialScenes(NativeExecutionProfile.SelectedProfileId)) return;
            var list = typeof(InputManager).GetField("activeDevices", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetValue(null) as List<InputDevice>
                ?? throw new InvalidOperationException("P0 active input device list is unavailable.");
            var version = typeof(List<InputDevice>).GetField("_version", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(list);
            Modding.Logger.Log("[HKTAS] Input counter boundary=" + boundary + " tick=" + InputManager.CurrentTick
                + " active=" + list.Count + " version=" + version);
        }
    }
}
