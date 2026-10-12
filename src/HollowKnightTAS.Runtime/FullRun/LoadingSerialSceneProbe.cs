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
    // Explicit serial loading policy; experimental checkpoint code uses its C.
    // The original SceneLoad still owns target activation and all entry callbacks.
    internal sealed partial class LoadingSerialSceneProbe : IDisposable
    {
        private const string StagingName = "__HKTAS_SerialBoundary_v6";
        private delegate IEnumerator OriginalBegin(SceneLoad self);
        private delegate IEnumerator OriginalTransition(GameManager self, GameManager.SceneLoadInfo info);
        private delegate bool OriginalUnload(string name);
        private readonly Hook beginHook;
        private readonly Hook unloadHook;
        private readonly Hook transitionHook;
        private readonly Hook lightingHook;
        private readonly string report;
        private readonly CanonicalBoundaryPolicy? canonical;
        private static LoadingSerialSceneProbe? instance;
        private SceneLoad? transition;
        private GameManager.SceneLoadInfo? checkpointLoadInfo;
        private Scene source;
        private string sourceName = "";
        private Scene staging;
        private bool sourceUnloaded;
        private bool awaitingBoundary;
        private bool observedBoundary;
        private int ordinal;
        private int rows;
        private string? failure;
        private static readonly long visualFrame = long.TryParse(
            Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_END"), out var visualEnd) ? visualEnd - 1 : -1;

        internal LoadingSerialSceneProbe()
        {
            if (!NativeExecutionProfile.UsesSerialScenes(NativeExecutionProfile.SelectedProfileId))
                throw new InvalidOperationException("Serial scenes require their own V6 execution profile.");
            if (NativeExecutionProfile.UsesCanonicalBoundary(NativeExecutionProfile.SelectedProfileId))
            {
                HollowKnightTAS.Core.SceneCheckpoints.SceneCheckpointEnvironmentPolicy.ValidateMods(
                    NativeExecutionProfile.SelectedProfileId, Modding.ModHooks.GetAllMods(onlyEnabled: false, allowLoadError: true)
                        .Select(mod => mod.GetName()));
                canonical = new CanonicalBoundaryPolicy();
            }
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
            var sceneStart = typeof(global::SceneManager).GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("SceneManager.Start");
            lightingHook = new Hook(sceneStart, (Action<Action<global::SceneManager>, global::SceneManager>)SceneStart);
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
            checkpointLoadInfo = info;
            // Bounded route metadata identifies the actual entry family; scene
            // names alone do not establish the gate or leaving direction.
            File.AppendAllText(report + ".transitions.jsonl", JsonConvert.SerializeObject(new {
                ordinal = ordinal + 1, engineFrame = Time.frameCount,
                source = USceneManager.GetActiveScene().name, target = info.SceneName,
                entryGate = info.EntryGateName, leaveDirection = info.HeroLeaveDirection?.ToString(),
                visualization = info.Visualization.ToString(), firstLevel = info.IsFirstLevelForPlayer }) + "\n");
            if (checkpointTransitionRoutine != null) throw Fail("Scene transition continuation reentered.");
            return TrackCheckpointTransition(original(manager, info));
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
                while (canonical != null && !canonical.AdvanceStartup()) yield return null;
                source = USceneManager.GetActiveScene();
                sourceName = source.name;
                if (Environment.GetEnvironmentVariable("HKTAS_SCENE_CHECKPOINT_EXPERIMENT") == "1")
                    checkpointBossBindings = CheckpointBossContext.CaptureBindingsBeforeUnload();
                if (!source.IsValid() || !source.isLoaded || source.name == StagingName)
                    throw Fail("Serial scene source is invalid.");
                if (!staging.IsValid() || !staging.isLoaded) staging = USceneManager.CreateScene(StagingName);
                Inventory("before-unload");
                if (!USceneManager.SetActiveScene(staging)) throw Fail("Cannot activate staging scene.");
                Write("unload-requested", new { source = sourceName, target = load.TargetSceneName });
                var operation = USceneManager.UnloadSceneAsync(source);
                if (operation == null) throw Fail("Source unload operation was not created.");
                // Do not leave the completed native AsyncOperation retained by
                // Unity's coroutine scheduler at the checkpoint boundary.
                // Poll completion through the same PlayerLoop instead.
                while (!operation.isDone) yield return null;
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
                checkpointLoadInfo = null;
                checkpointTransitionRoutine = null;
                checkpointTransitionFinished = false;
                checkpointBossBindings = null;
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
                RenderState("before-late-select");
                if (!target.IsValid() || !target.isLoaded
                    || (USceneManager.GetActiveScene() != target && !USceneManager.SetActiveScene(target)))
                    throw Fail("Cannot select the activated serial target scene.");
                RenderState("after-late-select");
                // Keep one empty carrier scene. CreateScene does not emit sceneLoaded;
                // unloading it sends an unmatched sceneUnloaded event to Mod listeners.
                // Only activation-time temporary objects need the old source lifetime.
                if (!staging.IsValid() || !staging.isLoaded)
                    throw Fail("Serial staging scene is unavailable.");
                var temporaryRoots = staging.GetRootGameObjects();
                File.AppendAllText(report + ".staging.jsonl", JsonConvert.SerializeObject(new {
                    ordinal, target = target.name, roots = temporaryRoots.Select(root => new {
                        root.name, objects = root.GetComponentsInChildren<Transform>(true).Select(t => new {
                            id = Identity(t.gameObject), t.gameObject.activeSelf, t.gameObject.activeInHierarchy,
                            components = t.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().FullName).ToArray()
                        }).ToArray()
                    }).ToArray()
                }) + "\n");
                foreach (var root in temporaryRoots) UnityEngine.Object.Destroy(root);
                Write("staging-roots-cleared", new { target = target.name, count = temporaryRoots.Length });
                return true;
            }
            return original(name);
        }

        internal static bool ObserveNativeBoundary(long movie, long native, string? sessionDirectory = null, long? movieInputNativeOrigin = null,
            CanonicalInputEpoch? inputControlOrigin = null, HollowKnightTAS.Runtime.Input.FullRunActionSetAdapter? input = null)
        {
            var self = instance;
            if (self?.failure != null) throw new InvalidOperationException(self.failure);
            if (self == null || !self.awaitingBoundary || self.observedBoundary) return false;
            if (self.source.isLoaded || self.transition == null)
                throw new InvalidOperationException("Serial native boundary still has a source scene.");
            var target = USceneManager.GetSceneByName(self.transition.TargetSceneName);
            if (target.IsValid() && target.isLoaded)
                throw new InvalidOperationException("Serial native boundary already has the target scene.");
            self.canonical?.Apply(self.ordinal, self.sourceName, self.transition.TargetSceneName, movie, native,
                sessionDirectory ?? throw new InvalidOperationException("Canonical boundary needs its protected Runtime session binding."),
                movieInputNativeOrigin ?? throw new InvalidOperationException("Canonical boundary requires the actual StartInputAt frame origin."),
                inputControlOrigin ?? throw new InvalidOperationException("Canonical boundary requires the actual input tick/scheduler origin."),
                input ?? throw new InvalidOperationException("Canonical boundary requires its actual input adapter."));
            self.Inventory("native-empty-boundary", movie, native);
            LoadingBoundaryInventory.Capture(self.ordinal, self.sourceName, self.transition.TargetSceneName, movie, native);
            self.observedBoundary = true;
            return true;
        }

        internal static bool OwnsBoundaryIterator(IEnumerator iterator)
        {
            var type = iterator.GetType();
            if (type.DeclaringType != typeof(LoadingSerialSceneProbe) || !type.Name.StartsWith("<Begin>", StringComparison.Ordinal)) return false;
            var owner = type.GetField("<>4__this", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return instance != null && instance.awaitingBoundary && ReferenceEquals(owner?.GetValue(iterator), instance);
        }
        internal static void RestoreCanonicalInputOrigin(CanonicalInputEpoch source)
        {
            GetCheckpointBoundary();
            var policy = instance!.canonical ?? throw new InvalidOperationException("Canonical input restore policy is absent.");
            policy.RestoreInputOrigin(source);
        }

        internal static Tuple<int, GameManager.SceneLoadInfo, string> GetCheckpointBoundary()
        {
            var self = instance;
            if (self == null || !self.observedBoundary || self.source.isLoaded || self.transition == null || self.checkpointLoadInfo == null)
                throw new InvalidOperationException("No serial source-unloaded checkpoint boundary is active.");
            var target = USceneManager.GetSceneByName(self.transition.TargetSceneName);
            if ((target.IsValid() && target.isLoaded) || self.checkpointLoadInfo.SceneName != self.transition.TargetSceneName)
                throw new InvalidOperationException("Checkpoint target load has already started or its entry parameters differ.");
            return Tuple.Create(self.ordinal, self.checkpointLoadInfo, self.sourceName);
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
            var audio = components.OfType<AudioSource>()
                .Where(c => c.gameObject.name.StartsWith("Audio Player", StringComparison.Ordinal))
                .OrderBy(Identity, StringComparer.Ordinal).Select(c => new {
                    id = Identity(c), active = c.gameObject.activeInHierarchy, c.enabled,
                    c.isPlaying, c.timeSamples, c.pitch, c.loop,
                    clip = c.clip == null ? null : c.clip.name,
                    behaviours = c.GetComponents<MonoBehaviour>().Where(b => b != null)
                        .Select(b => b.GetType().FullName).ToArray()
                }).ToArray();
            // Audio playback state is diagnostic evidence, not removed from the
            // state contract just because it can progress independently of TAS.
            File.AppendAllText(report + ".audio.jsonl", JsonConvert.SerializeObject(new {
                phase, ordinal, engineFrame = Time.frameCount, dspTime = AudioSettings.dspTime, audio
            }) + "\n");
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

        private void SceneStart(Action<global::SceneManager> original, global::SceneManager manager)
        {
            RenderState("scene-manager-before-start:" + manager.gameObject.scene.name);
            original(manager);
            RenderState("scene-manager-start:" + manager.gameObject.scene.name);
        }

        private void TargetLoaded(Scene scene, LoadSceneMode mode)
        {
            if (transition == null || !sourceUnloaded || scene.name != transition.TargetSceneName) return;
            // Select the target and refresh its dimensions before Start builds
            // scene borders from GameManager tilemap bounds.
            RenderState("before-early-select");
            if (!USceneManager.SetActiveScene(scene)) throw Fail("Cannot select serial target before Start.");
            // SceneManager.Start builds the four black scene borders from the
            // GameManager tilemap dimensions. The serialized activation yields
            // before ActivationComplete, so publish the target dimensions now,
            // before Start can use the smaller source-scene bounds.
            var manager = GameManager.UnsafeInstance;
            if (manager == null) throw Fail("Serial target has no GameManager.");
            manager.RefreshTilemapInfo(scene.name);
            RenderState("after-early-select");
        }

        private void RenderState(string phase)
        {
            var color = RenderSettings.ambientLight;
            File.AppendAllText(report + ".render.jsonl", JsonConvert.SerializeObject(new {
                phase, ordinal, engineFrame = Time.frameCount, activeScene = USceneManager.GetActiveScene().name,
                sceneWidth = GameManager.UnsafeInstance == null ? -1f : GameManager.UnsafeInstance.sceneWidth,
                sceneHeight = GameManager.UnsafeInstance == null ? -1f : GameManager.UnsafeInstance.sceneHeight,
                ambient = new[] { color.r, color.g, color.b, color.a }, RenderSettings.ambientIntensity,
                mode = RenderSettings.ambientMode.ToString()
            }) + "\n");
        }

        internal static void CaptureVisualState(long movie)
        {
            var output = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT");
            if (output == null || movie != visualFrame) return;
            var path = Path.Combine(output, "visual-state-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".json");
            if (File.Exists(path)) return;
            var color = RenderSettings.ambientLight;
            File.WriteAllText(path, JsonConvert.SerializeObject(new {
                movie, activeScene = USceneManager.GetActiveScene().name,
                ambient = new[] { color.r, color.g, color.b }, mode = RenderSettings.ambientMode.ToString(),
                pixelLights = QualitySettings.pixelLightCount,
                gates = TransitionPoint.TransitionPoints.Where(c => c != null && c.gameObject.scene.IsValid())
                    .Select(c => new { id = Identity(c), scene = c.gameObject.scene.name,
                        c.gameObject.activeInHierarchy, c.isADoor, c.targetScene, c.entryPoint,
                        c.alwaysEnterLeft, c.alwaysEnterRight, c.hardLandOnExit,
                        visualization = c.sceneLoadVisualization.ToString(),
                        position = new[] { c.transform.position.x, c.transform.position.y },
                        entryOffset = new[] { c.entryOffset.x, c.entryOffset.y }
                    }).ToArray(),
                sprites = Resources.FindObjectsOfTypeAll<SpriteRenderer>().Where(c => c.gameObject.scene.IsValid()
                    && c.gameObject.activeInHierarchy && c.enabled
                    && (c.bounds.size.y > 20 || c.gameObject.name == "Knight" || Identity(c).Contains("Marmu")))
                    .Select(c => new { id = Identity(c), c.forceRenderingOff, c.sortingLayerName, c.sortingOrder,
                        color = new[] { c.color.r, c.color.g, c.color.b, c.color.a },
                        sprite = c.sprite == null ? null : c.sprite.name,
                        texture = c.sprite == null || c.sprite.texture == null ? null : c.sprite.texture.name,
                        position = new[] { c.transform.position.x, c.transform.position.y, c.transform.position.z },
                        size = new[] { c.bounds.size.x, c.bounds.size.y, c.bounds.size.z }
                    }).ToArray(),
                cameras = Resources.FindObjectsOfTypeAll<Camera>().Where(c => c.gameObject.scene.IsValid()).Select(c => new {
                    id = Identity(c), c.enabled, c.gameObject.activeInHierarchy, c.cullingMask,
                    renderingPath = c.actualRenderingPath.ToString(), c.depth, c.nearClipPlane, c.farClipPlane,
                    position = new[] { c.transform.position.x, c.transform.position.y, c.transform.position.z }
                }).ToArray(),
                lights = Resources.FindObjectsOfTypeAll<Light>().Where(c => c.gameObject.scene.IsValid()).Select(c => new {
                    id = Identity(c), c.enabled, c.gameObject.activeInHierarchy, c.intensity, c.range,
                    c.cullingMask, type = c.type.ToString(), color = new[] { c.color.r, c.color.g, c.color.b },
                    position = new[] { c.transform.position.x, c.transform.position.y, c.transform.position.z }
                }).ToArray(),
                renderers = Resources.FindObjectsOfTypeAll<Renderer>().Where(c => c.gameObject.scene.name == "GG_Workshop"
                    && c.gameObject.activeInHierarchy && Identity(c).Contains("Statue")).Select(c => new {
                    id = Identity(c), c.enabled, c.gameObject.layer, c.lightmapIndex,
                    materials = c.sharedMaterials.Select(m => m == null ? null : new {
                        m.name, shader = m.shader == null ? null : m.shader.name,
                        texture = m.mainTexture == null ? null : m.mainTexture.name
                    }).ToArray()
                }).ToArray()
            }));
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
            canonical?.Dispose();
            USceneManager.sceneLoaded -= TargetLoaded;
            lightingHook.Dispose();
            transitionHook.Dispose();
            beginHook.Dispose();
            unloadHook.Dispose();
        }
    }
}
