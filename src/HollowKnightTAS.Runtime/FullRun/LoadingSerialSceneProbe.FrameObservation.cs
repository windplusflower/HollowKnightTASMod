using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using UnityEngine.SceneManagement;
using USceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed partial class LoadingSerialSceneProbe
    {
        // A K frame may be anywhere in the original load. Do not require the
        // parked B iterator or advance it to make it observable. In-flight
        // iterators, yield objects and load info join the single world graph.
        internal sealed class FrameObservation
        {
            private readonly LoadingSerialSceneProbe owner;
            private readonly Action admitted;
            private readonly ResidentProcessSlots slots;
            private readonly int thread = Thread.CurrentThread.ManagedThreadId;
            internal readonly ResidentLoaderData Snapshot;
            internal IReadOnlyDictionary<string, object> ManagedRoots => slots.ManagedRoots;
            internal FrameObservation(Action requireFrame)
            {
                owner = instance ?? throw new InvalidOperationException("Resident frame observer has no installed serial loader");
                admitted = requireFrame;
                var roles = ResidentFieldOwnership.ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
                foreach (var field in new[] { "transition", "checkpointLoadInfo", "checkpointTransitionRoutine", "residentBeginRoutine",
                    "sourceUnloaded", "awaitingBoundary", "observedBoundary", "checkpointTransitionFinished" }) roles[field] = "D";
                slots = new ResidentProcessSlots("frame-loader", owner, roles, Require);
                Snapshot = new ResidentLoaderData { Owner = slots.Snapshot, FrameObservation = true,
                    SceneDependencies = DescribeScenes(),
                    CoverageGaps = slots.Snapshot.CoverageGaps.Concat(new[] { "loader/native-scene-activation-and-async-request-queue-not-observed" }).ToArray() };
                RequireUnchanged();
            }
            // Native Scene handles are local dependencies. Locate each by the
            // actual SceneManager order, including duplicate scene instances.
            private string DescribeScenes()
            {
                object Describe(Scene scene) {
                    var valid = scene.IsValid(); if (!valid) return new { valid };
                    var index = -1;
                    for (var i = 0; i < USceneManager.sceneCount; i++) if (USceneManager.GetSceneAt(i).handle == scene.handle) { index = i; break; }
                    return new { valid, index, scene.isLoaded, scene.path, scene.name, scene.buildIndex, active = USceneManager.GetActiveScene().handle == scene.handle };
                }
                return JsonConvert.SerializeObject(new { source = Describe(owner.source), staging = Describe(owner.staging), active = Describe(USceneManager.GetActiveScene()) });
            }
            internal void Require()
            {
                admitted();
                if (thread != Thread.CurrentThread.ManagedThreadId || !ReferenceEquals(instance, owner) || owner.failure != null
                    || !owner.beginHook.IsValid || !owner.beginHook.IsApplied || !ReferenceEquals(owner.beginHook.DelegateTarget, owner))
                    throw new InvalidOperationException("Resident frame's original loader hook, thread or owner changed");
            }
            internal void RequireUnchanged()
            {
                Require(); slots.RequireUnchanged();
                if (Snapshot.SceneDependencies != DescribeScenes()) throw new InvalidOperationException("Native scene membership changed inside a single frame observation");
            }
            internal string? BindExternal(object ownerValue) => slots.BindExternal(ownerValue);
            internal object? ResolveExternal(string key) => slots.ResolveExternal(key);
        }
    }
}
