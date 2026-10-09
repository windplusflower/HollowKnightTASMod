using System;
using System.Collections.Generic;
using System.Linq;
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
            => ResumeMany(new[] { actor });
        internal static void ResumeMany<T>(IEnumerable<T> actors) where T : MonoBehaviour
        {
            var values = actors.ToArray();
            if (typeof(T) != typeof(GeoControl) && typeof(T) != typeof(SpatterOrange)
                && typeof(T) != typeof(EnemyBullet) && typeof(T) != typeof(SetZ)
                || values.Length > 64 || values.Any(actor => actor == null || actor.enabled) || values.Distinct().Count() != values.Length)
                throw new InvalidOperationException("Checkpoint behaviour enable binding differs.");
            if (values.Length == 0) return;
            var method = typeof(T).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Checkpoint actor has no original OnEnable.");
            using (var hook = new Hook(method, (Action<OriginalEnable<T>, T>)((original, self) =>
            { if (!values.Any(actor => ReferenceEquals(self, actor))) original(self); })))
                foreach (var actor in values) actor.enabled = true;
        }
    }
}
