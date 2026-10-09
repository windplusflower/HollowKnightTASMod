using System;
using System.Reflection;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CheckpointBehaviourEnable
    {
        private delegate void OriginalEnable<T>(T self);
        // These actors completed OnEnable in the source. Register their prepared
        // behaviours without repeating that initialization over captured fields.
        // The temporary hook is scoped to this instance and synchronous write.
        internal static void Resume<T>(T actor) where T : MonoBehaviour
        {
            if (typeof(T) != typeof(GeoControl) && typeof(T) != typeof(SpatterOrange) || actor == null || actor.enabled)
                throw new InvalidOperationException("Checkpoint behaviour enable binding differs.");
            var method = typeof(T).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Checkpoint actor has no original OnEnable.");
            using (var hook = new Hook(method, (Action<OriginalEnable<T>, T>)((original, self) =>
            { if (!ReferenceEquals(self, actor)) original(self); }))) actor.enabled = true;
        }
    }
}
