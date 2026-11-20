using System;
using System.Collections.Generic;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Experimental restore-only draw reduction. PlayerLoop, render callbacks,
    // physics, input and clocks all continue normally. Never used for export.
    internal sealed class ReplayRenderSuppression : IDisposable
    {
        private readonly Dictionary<Camera, int> masks = new Dictionary<Camera, int>();
        private readonly bool enabled = Environment.GetEnvironmentVariable("HKTAS_RESTORE_REDUCE_DRAW") == "1"
            && Environment.GetEnvironmentVariable("HKTAS_RESTORE_HIDDEN_WINDOW") == "1";
        public bool Active { get; set; }

        public ReplayRenderSuppression()
        {
            if (!enabled) return;
            Camera.onPreCull += BeforeCull;
            Camera.onPostRender += AfterRender;
        }

        private void BeforeCull(Camera camera)
        {
            if (!Active || camera == null || masks.ContainsKey(camera)) return;
            masks.Add(camera, camera.cullingMask);
            camera.cullingMask = 0;
        }

        private void AfterRender(Camera camera)
        {
            if (camera == null || !masks.TryGetValue(camera, out var mask)) return;
            camera.cullingMask = mask;
            masks.Remove(camera);
        }

        public void Dispose()
        {
            Active = false;
            if (!enabled) return;
            Camera.onPreCull -= BeforeCull;
            Camera.onPostRender -= AfterRender;
            EndFrame();
        }

        // A camera may not deliver onPostRender if another callback aborts it.
        // Never carry that camera's temporary mask across a frame boundary.
        public void EndFrame()
        {
            foreach (var pair in masks)
                if (pair.Key != null) pair.Key.cullingMask = pair.Value;
            masks.Clear();
        }
    }
}
