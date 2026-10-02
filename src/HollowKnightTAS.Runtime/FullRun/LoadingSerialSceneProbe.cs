using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HollowKnightTAS.Core.Movie;
using MonoMod.RuntimeDetour;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;
using USceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Explicit V6 feasibility experiment. No checkpoint payload or restoration.
    // The original SceneLoad still owns target activation and all entry callbacks.
    internal sealed class LoadingSerialSceneProbe : IDisposable
    {
        private const string StagingName = "__HKTAS_SerialBoundary_v6";
        private delegate IEnumerator OriginalBegin(SceneLoad self);
        private delegate IEnumerator OriginalTransition(GameManager self, GameManager.SceneLoadInfo info);
        private delegate bool OriginalUnload(string name);
        private readonly Hook beginHook;
        private readonly Hook unloadHook;
        private readonly Hook transitionHook;
        private readonly string report;
        private static LoadingSerialSceneProbe? instance;
        private SceneLoad? transition;
        private Scene source;
        private string sourceName = "";
        private Scene staging;
        private bool sourceUnloaded;
        private bool awaitingBoundary;
        private bool observedBoundary;
        private int ordinal;
        private int rows;
        private string? failure;

        internal LoadingSerialSceneProbe()
        {
            if (NativeExecutionProfile.SelectedProfileId != NativeExecutionProfile.SerialSceneProfileId)
                throw new InvalidOperationException("Serial scenes require their own V6 execution profile.");
            if (instance != null) throw new InvalidOperationException("Serial scene policy is already installed.");
            report = Path.Combine(Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT")
                ?? throw new InvalidOperationException("Serial scene diagnostics need an output directory."),
                "serial-scenes-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".jsonl");
            var begin = typeof(SceneLoad).GetMethod("BeginRoutine", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("SceneLoad.BeginRoutine");
            var unload = typeof(USceneManager).GetMethod("UnloadScene", new[] { typeof(string) })
                ?? throw new MissingMethodException("SceneManager.UnloadScene(string)");
            var start = typeof(GameManager).GetMethod("BeginSceneTransitionRoutine", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("GameManager.BeginSceneTransitionRoutine");
            beginHook = new Hook(begin, (Func<OriginalBegin, SceneLoad, IEnumerator>)Begin);
            unloadHook = new Hook(unload, (Func<OriginalUnload, string, bool>)Unload);
            transitionHook = new Hook(start, (Func<OriginalTransition, GameManager, GameManager.SceneLoadInfo, IEnumerator>)Start);
            USceneManager.sceneLoaded += TargetLoaded;
            instance = this;
            Write("installed", null);
            Modding.Logger.Log("[HKTAS] V6 serial scene experiment installed.");
        }

        private IEnumerator Start(OriginalTransition original, GameManager manager, GameManager.SceneLoadInfo info)
        {
            // For example StagTravel gates activation on FetchComplete. Waiting
            // for activation before fetch would deadlock; it needs its own adapter.
            // Refuse before any destruction rather than silently using another policy.
            if (info.GetType() != typeof(GameManager.SceneLoadInfo))
            {
                Write("unsupported-load-info", new { type = info.GetType().FullName });
                throw Fail("V6 serial prototype requires a load-info adapter: " + info.GetType().FullName);
            }
            return original(manager, info);
        }

        private IEnumerator Begin(OriginalBegin original, SceneLoad load)
        {
            if (transition != null) throw Fail("Serial scene transition reentered.");
            transition = load;
            ordinal++;
            sourceUnloaded = false;
            observedBoundary = false;
            try
            {
                // Finish speculative work before starting destruction. This routine
                // is also called by the original loader; the second call sees no pending work.
                yield return ScenePreloader.FinishPendingOperations();
                while (!load.IsFetchAllowed || !load.IsActivationAllowed) yield return null;
                source = USceneManager.GetActiveScene();
                sourceName = source.name;
                if (!source.IsValid() || !source.isLoaded || source.name == StagingName)
                    throw Fail("Serial scene source is invalid.");
                if (!staging.IsValid() || !staging.isLoaded) staging = USceneManager.CreateScene(StagingName);
                Inventory("before-unload");
                if (!USceneManager.SetActiveScene(staging)) throw Fail("Cannot activate staging scene.");
                Write("unload-requested", new { source = sourceName, target = load.TargetSceneName });
                var operation = USceneManager.UnloadSceneAsync(source);
                if (operation == null) throw Fail("Source unload operation was not created.");
                yield return operation;
                if (source.isLoaded) throw Fail("Source scene remained loaded.");
                operation = null;
                sourceUnloaded = true;
                // Return through PlayerLoop once so destruction callbacks unwind.
                yield return null;
                awaitingBoundary = true;
                while (!observedBoundary) yield return null;
                awaitingBoundary = false;
                Write("target-load-start", new { source = sourceName, target = load.TargetSceneName });
                var routine = original(load);
                var activated = false;
                try
                {
                    while (routine.MoveNext())
                    {
                        var wait = routine.Current;
                        // In the pinned SceneLoad routine the first direct async
                        // yield is target activation. FinishPendingOperations is
                        // yielded as a Coroutine; later asset cleanup is kept intact.
                        if (!activated && wait is AsyncOperation activation)
                        {
                            LoadingSynchronousFetchProbe.CompleteSerialActivation(activation);
                            var target = USceneManager.GetSceneByName(load.TargetSceneName);
                            if (!activation.isDone || !target.IsValid() || !target.isLoaded)
                                throw Fail("Synchronous scene activation did not finish the target.");
                            activated = true;
                            Write("target-activation-synchronized", new { target = load.TargetSceneName });
                            // Fixed continuation phase instead of a variable number
                            // of PlayerLoops waiting for async activation completion.
                            yield return null;
                        }
                        else yield return wait;
                    }
                }
                finally { (routine as IDisposable)?.Dispose(); }
                if (!activated) throw Fail("Serial loader did not observe target activation.");
                if (USceneManager.GetActiveScene().name != load.TargetSceneName || staging.rootCount != 0)
                    throw Fail("Target did not replace the staging scene.");
                Inventory("target-finished");
                Write("complete", new { source = sourceName, target = load.TargetSceneName });
            }
            finally
            {
                awaitingBoundary = false;
                transition = null;
            }
        }

        private bool Unload(OriginalUnload original, string name)
        {
            // GameManager's ActivationComplete subscriber still runs its tilemap,
            // asset/GC and social callbacks. Only its already-completed unload is suppressed.
            if (transition != null && sourceUnloaded && name == sourceName)
            {
                Write("duplicate-source-unload-suppressed", new { source = name });
                // Vanilla's removal of the old active scene also selects the target.
                // Suppressing only that removal leaves staging active, causing future
                // objects (and the next transition) to bind to staging instead.
                var target = USceneManager.GetSceneByName(transition.TargetSceneName);
                if (!target.IsValid() || !target.isLoaded
                    || (USceneManager.GetActiveScene() != target && !USceneManager.SetActiveScene(target)))
                    throw Fail("Cannot select the activated serial target scene.");
                // Keep one empty carrier scene. CreateScene does not emit sceneLoaded;
                // unloading it sends an unmatched sceneUnloaded event to Mod listeners.
                // Only activation-time temporary objects need the old source lifetime.
                if (!staging.IsValid() || !staging.isLoaded)
                    throw Fail("Serial staging scene is unavailable.");
                var temporaryRoots = staging.GetRootGameObjects();
                foreach (var root in temporaryRoots) UnityEngine.Object.Destroy(root);
                Write("staging-roots-cleared", new { target = target.name, count = temporaryRoots.Length });
                return true;
            }
            return original(name);
        }

        internal static void ObserveNativeBoundary(long movie, long native)
        {
            var self = instance;
            if (self?.failure != null) throw new InvalidOperationException(self.failure);
            if (self == null || !self.awaitingBoundary || self.observedBoundary) return;
            if (self.source.isLoaded || self.transition == null)
                throw new InvalidOperationException("Serial native boundary still has a source scene.");
            var target = USceneManager.GetSceneByName(self.transition.TargetSceneName);
            if (target.IsValid() && target.isLoaded)
                throw new InvalidOperationException("Serial native boundary already has the target scene.");
            self.Inventory("native-empty-boundary", movie, native);
            self.observedBoundary = true;
        }

        private void Inventory(string phase, long? movie = null, long? native = null)
        {
            var objects = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(go => go != null && go.scene.IsValid() && go.scene.isLoaded).ToArray();
            if (objects.Length > 100000) throw new InvalidOperationException("Serial inventory exceeded object budget.");
            var components = objects.SelectMany(go => go.GetComponents<Component>()).Where(c => c != null).ToArray();
            var groups = objects.GroupBy(go => go.scene.name).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new { scene = g.Key, objects = g.Count(), roots = g.Count(go => go.transform.parent == null) }).ToArray();
            var types = components.GroupBy(c => c.GetType().FullName).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new { type = g.Key, count = g.Count() }).ToArray();
            // Keep this first experiment independent of native layout recovery.
            // Counts establish domain reduction; they do not certify Box2D caches are empty.
            var physicsComponents = new { bodies = components.OfType<Rigidbody2D>().Count(),
                colliders = components.OfType<Collider2D>().Count(), joints = components.OfType<Joint2D>().Count(),
                activeSimulatedBodies = components.OfType<Rigidbody2D>().Count(c => c.gameObject.activeInHierarchy && c.simulated),
                activeEnabledColliders = components.OfType<Collider2D>().Count(c => c.gameObject.activeInHierarchy && c.enabled) };
            // Aggregate equal-named DDOL roots: Unity can report sibling index zero
            // for several hidden roots, so their display paths are not unique IDs.
            var rootGroups = objects.GroupBy(go => go.scene.name + "/" + go.transform.root.name)
                .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new { rootName = g.Key,
                    roots = g.Count(go => go.transform.parent == null), objects = g.Count(),
                    activeObjects = g.Count(go => go.activeInHierarchy) }).ToArray();
            Write(phase, new { movie, native, activeScene = USceneManager.GetActiveScene().name,
                source = sourceName, target = transition?.TargetSceneName,
                objects = objects.Length, activeObjects = objects.Count(go => go.activeInHierarchy),
                components = components.Length, scenes = groups, componentTypes = types, rootGroups,
                roots = objects.Where(go => go.transform.parent == null).Select(Identity).OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                physicsComponents, nativePhysicsStateCovered = false,
                completeStateCoverage = false, restorable = false });
        }

        private static string Identity(UnityEngine.Object value)
        {
            var component = value as Component;
            var go = value as GameObject ?? component?.gameObject;
            if (go == null) return value.GetType().FullName + ":" + value.name;
            var segments = new List<string>();
            for (var node = go.transform; node != null; node = node.parent)
                segments.Add(node.name + "[" + node.GetSiblingIndex() + "]");
            segments.Reverse();
            return go.scene.name + "/" + string.Join("/", segments)
                + (component == null ? "" : ":" + component.GetType().FullName + "["
                    + Array.IndexOf(go.GetComponents(component.GetType()), component) + "]");
        }

        private void TargetLoaded(Scene scene, LoadSceneMode mode)
        {
            if (transition == null || !sourceUnloaded || scene.name != transition.TargetSceneName) return;
            // Select the target and refresh its dimensions before Start builds
            // scene borders from GameManager tilemap bounds.
            if (!USceneManager.SetActiveScene(scene)) throw Fail("Cannot select serial target before Start.");
            // SceneManager.Start builds the four black scene borders from the
            // GameManager tilemap dimensions. The serialized activation yields
            // before ActivationComplete, so publish the target dimensions now,
            // before Start can use the smaller source-scene bounds.
            var manager = GameManager.UnsafeInstance;
            if (manager == null) throw Fail("Serial target has no GameManager.");
            manager.RefreshTilemapInfo(scene.name);
        }

        private void Write(string phase, object? data)
        {
            if (++rows > 256) throw new InvalidOperationException("Serial scene diagnostic budget exceeded.");
            File.AppendAllText(report, JsonConvert.SerializeObject(new { phase, ordinal,
                engineFrame = Time.frameCount, data }) + "\n");
        }

        private InvalidOperationException Fail(string message)
        {
            failure = message;
            Write("failed", new { message });
            return new InvalidOperationException(message);
        }

        public void Dispose()
        {
            instance = null;
            USceneManager.sceneLoaded -= TargetLoaded;
            transitionHook.Dispose();
            beginHook.Dispose();
            unloadHook.Dispose();
        }
    }
}
