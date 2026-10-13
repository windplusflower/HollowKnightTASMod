using System;
using System.Linq;
using System.Reflection;
using UnityEngine.UI;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalCheckpointIconPolicy
    {
        internal static object ResetIdleTimer()
        {
            var ui = UIManager.instance ?? throw new InvalidOperationException("Canonical checkpoint icon UI owner is absent.");
            var icon = ui.checkpointSprite ?? throw new InvalidOperationException("Canonical checkpoint icon is absent.");
            var fields = typeof(CheckpointSprite).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();
            if (!fields.Select(field => field.Name).SequenceEqual(new[] { "audioSource", "endSprites", "frameTimer", "framesPerSecond",
                "image", "isShowing", "loopSprites", "startSprites", "state" }))
                throw new InvalidOperationException("Original checkpoint icon field layout differs.");
            var image = fields[4].GetValue(icon) as Image;
            if (image == null || image.gameObject != icon.gameObject || fields[2].FieldType != typeof(float)
                || fields[5].FieldType != typeof(bool) || !fields[8].FieldType.IsEnum)
                throw new InvalidOperationException("Original checkpoint icon bindings differ.");
            var state = Convert.ToInt32(fields[8].GetValue(icon)); var showing = (bool)fields[5].GetValue(icon);
            var before = (float)fields[2].GetValue(icon);
            if (float.IsNaN(before) || float.IsInfinity(before)) throw new InvalidOperationException("Checkpoint icon timer is not finite.");
            // Original Update accumulates this timer even while NotStarted,
            // but Show resets it before starting an animation. Normalize only
            // that idle counter; preserve active save/display progress and all
            // sprite/audio configuration, references and scene save counters.
            var idle = state == 0 && !showing && !image.enabled;
            if (idle)
            {
                fields[2].SetValue(icon, 0f);
                if (BitConverter.ToInt32(BitConverter.GetBytes((float)fields[2].GetValue(icon)),0) != 0)
                    throw new InvalidOperationException("Checkpoint icon idle timer readback differs.");
            }
            return new { idleTimerReset = idle, state, showing, timerBeforeBits = BitConverter.ToInt32(BitConverter.GetBytes(before),0) };
        }
    }
}
