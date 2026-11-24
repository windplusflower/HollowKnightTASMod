using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;
using USceneManager = UnityEngine.SceneManagement.SceneManager;
using UObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.Observation
{
    public sealed class WorldCapture
    {
        public WorldCapture(string metadataJson, IReadOnlyList<ObservedObject> objects) { MetadataJson = metadataJson; Objects = objects; }
        public string MetadataJson { get; }
        public IReadOnlyList<ObservedObject> Objects { get; }
    }

    public sealed class ObservedObject
    {
        public ObservedObject(string id, string kind, string json) { Id = id; Kind = kind; Json = json; }
        public string Id { get; }
        public string Kind { get; }
        public string Json { get; }
    }

    /// <summary>
    /// On-demand, synchronous observation. Construct and call only on Unity's main thread at the
    /// caller's completed-frame boundary. It installs no hooks and never advances a frame.
    /// </summary>
    public sealed partial class RuntimeWorldObserver
    {
        private readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;
        private readonly string sessionId = Guid.NewGuid().ToString("N");
        private readonly Dictionary<string, GameObject> objects = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private readonly ObservationValues values;
        private readonly ObservationFsm fsms;
        private readonly ObservationColliders geometry = new ObservationColliders();
        private long epoch;
        private long captureSequence;
        private string sceneSignature = string.Empty;

        public RuntimeWorldObserver()
        {
            values = new ObservationValues(Reference);
            fsms = new ObservationFsm(values);
        }

        public WorldCapture Capture(long nativeFrame, long movieFrame, string view = "world", bool includeInactive = false)
        {
            RequireMainThread();
            if (view == "fsmCatalog") return CaptureFsmCatalog(nativeFrame, movieFrame);
            if (view != "world" && view != "all" && view != "colliders") throw new ArgumentException("view must be world, all, or colliders.", nameof(view));
            var errors = new List<object>();
            var hero = Existing<HeroController>();
            var manager = Existing<GameManager>();
            var ui = Existing<UIManager>();
            var gameCameras = Existing<GameCameras>();
            var player = Existing<PlayerData>();
            var cameras = Camera.allCameras.Where(camera => camera != null).ToArray();
            Camera? camera = Camera.main;
            if (camera == null) camera = cameras.Where(value => value.enabled && value.gameObject.activeInHierarchy).OrderBy(value => value.depth).FirstOrDefault();
            var scenes = Scenes(hero, manager, ui, gameCameras, cameras, errors);
            UpdateEpoch(scenes);
            captureSequence++;
            PruneObjects();
            var results = new List<ObservedObject>();
            var sceneRecords = new List<object>();
            int total = 0, inactiveFiltered = 0, viewFiltered = 0, failed = 0;
            var seen = new HashSet<int>();
            var byScene = CollectAliveSceneObjects(scenes).ToLookup(go => go.scene.handle);
            foreach (var scene in scenes)
            {
                int sceneObjects = 0, sceneReturned = 0;
                foreach (var go in byScene[scene.handle])
                {
                    if (go == null || go.GetInstanceID() == 0 || !seen.Add(go.GetInstanceID())) continue;
                    total++; sceneObjects++;
                    string id = Id(go);
                    objects[id] = go;
                    if (!go.activeInHierarchy && (!includeInactive || view == "colliders")) { inactiveFiltered++; continue; }
                    try
                    {
                        if (view == "colliders")
                        {
                            var colliders = go.GetComponents<Collider2D>();
                            if (!colliders.Any(collider => collider != null && ObservationColliders.Visible(collider, camera))) { viewFiltered++; continue; }
                            var data = BaseObject(go, "collider");
                            var allComponents = go.GetComponents<Component>();
                            data["colliders"] = colliders.Where(collider => collider != null && ObservationColliders.Visible(collider, camera))
                                .Select(collider => geometry.Capture(collider, Array.IndexOf(allComponents, collider), camera, Classify(collider, hero))).ToArray();
                            results.Add(new ObservedObject(id, "collider", ObservationData.Json(data)));
                        }
                        else
                        {
                            var components = go.GetComponents<Component>();
                            if (view == "world" && !Gameplay(components)) { viewFiltered++; continue; }
                            string kind = Kind(components);
                            var overview = CaptureOverview(go, components, kind, hero, player, camera);
                            if (overview["errors"] is List<object> componentErrors && componentErrors.Count > 0)
                                errors.Add(ObservationData.Map("scope", "objectComponents", "objectId", id, "errors", componentErrors));
                            results.Add(new ObservedObject(id, kind, ObservationData.Json(overview)));
                        }
                        sceneReturned++;
                    }
                    catch (Exception error)
                    {
                        failed++;
                        var failure = BaseObject(go, "error");
                        failure["error"] = ObservationData.Error(error);
                        results.Add(new ObservedObject(id, "error", ObservationData.Json(failure)));
                        errors.Add(ObservationData.Map("scope", "object", "objectId", id, "error", ObservationData.Error(error)));
                        sceneReturned++;
                    }
                }
                var descriptor = SceneData(scene);
                descriptor["rootCount"] = scene.rootCount;
                descriptor["objectCount"] = sceneObjects;
                descriptor["returnedCount"] = sceneReturned;
                sceneRecords.Add(descriptor);
            }
            var metadata = Metadata(nativeFrame, movieFrame);
            metadata["view"] = view;
            metadata["includeInactive"] = includeInactive;
            metadata["effectiveIncludeInactive"] = includeInactive && view != "colliders";
            metadata["activeScene"] = SceneData(USceneManager.GetActiveScene());
            metadata["scenes"] = sceneRecords;
            metadata["scope"] = "All loaded SceneManager scenes plus scenes of existing HeroController/GameManager/UIManager/GameCameras singletons and live cameras (including discovered DontDestroyOnLoad scene). No assets or unloaded scenes.";
            metadata["filter"] = view == "all" ? "All scene GameObjects after active filter."
                : view == "world" ? "GameObjects with Collider2D, Rigidbody2D, HealthManager, DamageHero, PlayMakerFSM, HeroController, or any non-Unity MonoBehaviour."
                : "Active enabled Collider2D intersecting selected camera viewport; excludes source colliders consumed by CompositeCollider2D and unsimulated rigidbodies.";
            metadata["counts"] = ObservationData.Map("enumeratedObjects", total, "returnedObjects", results.Count,
                "inactiveFiltered", inactiveFiltered, "viewFiltered", viewFiltered, "failedObjects", failed, "truncatedObjects", 0);
            metadata["enumerationComplete"] = errors.Count == 0;
            metadata["semanticCompleteness"] = "Not guaranteed for arbitrary Mods; inspect omissions, limits, geometry approximations and component errors.";
            metadata["errors"] = errors;
            metadata["gameManager"] = manager == null ? null : ObservationData.Map("reference", Reference(manager), "state", KnownFields(manager, "gameState", "isPaused", "sceneName", "nextSceneName", "entryGateName"));
            metadata["uiManager"] = ui == null ? null : ObservationData.Map("reference", Reference(ui), "state", KnownFields(ui, "uiState", "menuState", "inventoryFSM", "pauseMenuFSM"));
            metadata["hero"] = hero == null ? null : Reference(hero);
            metadata["singletonStatus"] = SingletonStatus();
            metadata["viewport"] = ObservationData.Map("width", Screen.width, "height", Screen.height, "screenOrigin", "top-left", "normalized", true);
            metadata["camera"] = camera == null ? null : CaptureCamera(camera);
            metadata["cameras"] = cameras.Select(CaptureCamera).ToArray();
            metadata["time"] = ObservationData.Map("time", Time.time, "fixedTime", Time.fixedTime, "deltaTime", Time.deltaTime,
                "fixedDeltaTime", Time.fixedDeltaTime, "timeScale", Time.timeScale);
            metadata["physics2D"] = CapturePhysics();
            metadata["limits"] = ObservationData.Map("objects", "unbounded-no-silent-truncation", "collectionItems", ObservationValues.CollectionLimit,
                "stringCharacters", ObservationValues.StringLimit, "componentValueBudget", ObservationValues.ValueBudget,
                "rawFieldPolicy", "Instance fields only; arbitrary getters/ToString/object graphs are never evaluated. Unsupported or bounded values include omission reason/count.");
            return new WorldCapture(ObservationData.Json(metadata), results);
        }

        public string CaptureObjectDetails(string objectId, long nativeFrame, long movieFrame)
        {
            RequireMainThread();
            var detailsScenes = Scenes(Existing<HeroController>(), Existing<GameManager>(), Existing<UIManager>(), Existing<GameCameras>(),
                Camera.allCameras, new List<object>());
            UpdateEpoch(detailsScenes);
            captureSequence++;
            var result = Metadata(nativeFrame, movieFrame);
            result["objectId"] = objectId;
            if (!objects.TryGetValue(objectId, out var go) || go == null || Id(go) != objectId)
            {
                result["found"] = false;
                result["reason"] = "Object was not captured in this observer session, was destroyed, or changed scenes. Capture the current scene again to obtain a current ID.";
                return ObservationData.Json(result);
            }
            result["found"] = true;
            var components = go.GetComponents<Component>();
            // A paginated overview may replace an unusually large record with detailsRequired.
            // Details must therefore carry the full native overview, not depend on that omitted record.
            result["object"] = CaptureOverview(go, components, Kind(components), Existing<HeroController>(), Existing<PlayerData>(), Camera.main);
            var items = new List<object>();
            for (int i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component == null) { items.Add(ObservationData.Map("index", i, "missingScript", true)); continue; }
                var item = ComponentInfo(component, i);
                try
                {
                    if (component is MonoBehaviour) item["data"] = values.CaptureFields(component);
                    else item["data"] = ObservationData.Omitted("nativeComponentFieldsAreInOverview");
                    if (component is PlayMakerFSM fsm) item["fsm"] = fsms.Capture(fsm, i, true);
                }
                catch (Exception error) { item["error"] = ObservationData.Error(error); }
                items.Add(item);
            }
            result["componentCount"] = components.Length;
            result["components"] = items;
            result["readPolicy"] = "All public/private instance fields including base classes; bounded arrays/List<T>; known Unity structs and references. No property getter, custom enumerator, arbitrary ToString, or recursive private object graph.";
            return ObservationData.Json(result);
        }

        private Dictionary<string, object?> Metadata(long nativeFrame, long movieFrame) => ObservationData.Map(
            "schema", "hktas.world-observation.v1", "sessionId", sessionId, "epoch", epoch,
            "captureSequence", captureSequence, "nativeFrame", nativeFrame, "movieFrame", movieFrame,
            "unityFrameCount", Time.frameCount, "phase", "caller-completed-frame-boundary", "activeScene", SceneData(USceneManager.GetActiveScene()));

        private void RequireMainThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != ownerThread)
                throw new InvalidOperationException("RuntimeWorldObserver must be constructed and called on the same Unity main thread.");
        }

        private T? Existing<T>() where T : class => values.ReadStatic(typeof(T), "_instance") as T;

        private object SingletonStatus()
        {
            return new[] { typeof(HeroController), typeof(GameManager), typeof(UIManager), typeof(GameCameras), typeof(PlayerData) }
                .Select(type => ObservationData.Map("type", type.FullName, "backingField", "_instance",
                    "fieldAvailable", values.HasField(type, "_instance", true),
                    "valuePresent", values.ReadStatic(type, "_instance") != null,
                    "policy", "Reads existing static field without calling singleton getter or constructing an instance.")).ToArray();
        }

        private List<Scene> Scenes(HeroController? hero, GameManager? manager, UIManager? ui, GameCameras? gameCameras,
            Camera[] cameras, List<object> errors)
        {
            var scenes = new Dictionary<int, Scene>();
            void Add(Scene scene) { if (scene.IsValid() && scene.isLoaded) scenes[scene.handle] = scene; }
            for (int i = 0; i < USceneManager.sceneCount; i++)
            {
                try { Add(USceneManager.GetSceneAt(i)); }
                catch (Exception error) { errors.Add(ObservationData.Map("scope", "sceneAt", "index", i, "error", ObservationData.Error(error))); }
            }
            foreach (var component in new Component?[] { hero, manager, ui, gameCameras }.Concat(cameras))
                if (component != null) Add(component.gameObject.scene);
            return scenes.Values.OrderBy(scene => scene.handle).ToList();
        }

        private void UpdateEpoch(List<Scene> scenes)
        {
            string signature = USceneManager.GetActiveScene().handle.ToString(CultureInfo.InvariantCulture) + ":" + string.Join(",", scenes.Select(scene => scene.handle.ToString(CultureInfo.InvariantCulture)));
            if (!string.Equals(signature, sceneSignature, StringComparison.Ordinal)) { epoch++; sceneSignature = signature; }
        }

        private void PruneObjects()
        {
            foreach (var id in objects.Where(pair => pair.Value == null || pair.Value.GetInstanceID() == 0 || Id(pair.Value) != pair.Key).Select(pair => pair.Key).ToArray()) objects.Remove(id);
        }

        private static List<GameObject> CollectAliveSceneObjects(IReadOnlyCollection<Scene> scenes)
        {
            var validHandles = new HashSet<int>();
            foreach (var scene in scenes)
            {
                if (scene.IsValid() && scene.isLoaded) validHandles.Add(scene.handle);
            }

            if (validHandles.Count == 0) return new List<GameObject>();

            var collected = new List<(GameObject Go, int Handle, string Path, int SiblingIndex, int InstanceId)>();
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null) continue;
                int instanceId = go.GetInstanceID();
                if (instanceId == 0) continue;
                var scene = go.scene;
                if (!scene.IsValid() || !scene.isLoaded || !validHandles.Contains(scene.handle)) continue;
                var transform = go.transform;
                collected.Add((go, scene.handle, ObservationData.Path(transform), transform.GetSiblingIndex(), instanceId));
            }

            collected.Sort((a, b) =>
            {
                int cmp = a.Handle.CompareTo(b.Handle);
                if (cmp != 0) return cmp;
                cmp = string.CompareOrdinal(a.Path, b.Path);
                if (cmp != 0) return cmp;
                cmp = a.SiblingIndex.CompareTo(b.SiblingIndex);
                if (cmp != 0) return cmp;
                return a.InstanceId.CompareTo(b.InstanceId);
            });

            var result = new List<GameObject>(collected.Count);
            foreach (var item in collected)
            {
                result.Add(item.Go);
            }
            return result;
        }

        private string Id(GameObject go) => sessionId + ":" + go.scene.handle.ToString(CultureInfo.InvariantCulture) + ":" + go.GetInstanceID().ToString(CultureInfo.InvariantCulture);

        private object Reference(UObject value)
        {
            GameObject? go = value as GameObject;
            if (value is Component component) go = component.gameObject;
            if (go != null)
            {
                string id = Id(go);
                objects[id] = go;
                return ObservationData.Map("objectId", id, "instanceId", value.GetInstanceID(), "type", value.GetType().FullName,
                    "componentType", value is Component ? value.GetType().FullName : null, "name", value.name);
            }
            return ObservationData.Map("objectId", null, "instanceId", value.GetInstanceID(), "type", value.GetType().FullName, "name", value.name,
                "scope", "nonSceneUnityObject");
        }

        private static Dictionary<string, object?> SceneData(Scene scene) => ObservationData.Map("handle", scene.handle, "name", scene.name,
            "path", scene.path, "isLoaded", scene.isLoaded, "buildIndex", scene.buildIndex);

        private Dictionary<string, object?> BaseObject(GameObject go, string kind) => ObservationData.Map(
            "id", Id(go), "kind", kind, "name", go.name, "path", ObservationData.Path(go.transform),
            "parentId", go.transform.parent == null ? null : Id(go.transform.parent.gameObject), "siblingIndex", go.transform.GetSiblingIndex(),
            "scene", SceneData(go.scene), "activeSelf", go.activeSelf, "activeInHierarchy", go.activeInHierarchy,
            "layer", go.layer, "layerName", LayerMask.LayerToName(go.layer), "tag", go.tag);

        private static bool Gameplay(Component[] components) => components.Any(component => component is Collider2D || component is Rigidbody2D
            || component is HealthManager || component is DamageHero || component is PlayMakerFSM || component is HeroController
            || (component is MonoBehaviour && !(component.GetType().Namespace ?? string.Empty).StartsWith("UnityEngine", StringComparison.Ordinal)));

        private static string Kind(Component[] components)
        {
            if (components.Any(component => component is HeroController)) return "hero";
            if (components.Any(component => component is HealthManager)) return "enemy";
            if (components.Any(component => component is DamageHero)) return "hazard";
            if (components.Any(component => component is Collider2D)) return "collider";
            if (components.Any(component => component is PlayMakerFSM)) return "fsm";
            if (components.Any(component => component is MonoBehaviour)) return "behaviour";
            return "object";
        }

        private Dictionary<string, object?> CaptureOverview(GameObject go, Component[] components, string kind,
            HeroController? hero, PlayerData? player, Camera? camera)
        {
            var result = BaseObject(go, kind);
            var transform = go.transform;
            result["transform"] = ObservationData.Map("position", ObservationData.Vector(transform.position), "localPosition", ObservationData.Vector(transform.localPosition),
                "rotation", ObservationData.Quaternion(transform.rotation), "eulerAngles", ObservationData.Vector(transform.eulerAngles),
                "localRotation", ObservationData.Quaternion(transform.localRotation), "localScale", ObservationData.Vector(transform.localScale), "lossyScale", ObservationData.Vector(transform.lossyScale));
            result["components"] = components.Select((component, index) => component == null
                ? ObservationData.Map("index", index, "missingScript", true) : ComponentInfo(component, index)).ToArray();
            var renderers = new List<object>(); var bodies = new List<object>(); var health = new List<object>();
            var damage = new List<object>(); var colliders = new List<object>(); var machines = new List<object>();
            var particles = new List<object>();
            var errors = new List<object>();
            for (int index = 0; index < components.Length; index++)
            {
                var component = components[index];
                if (component == null) continue;
                try
                {
                    if (component is Renderer renderer) renderers.Add(ObservationData.Map("componentIndex", index, "enabled", renderer.enabled,
                        "bounds", ObservationData.Bounds(renderer.bounds), "sortingLayerId", renderer.sortingLayerID, "sortingOrder", renderer.sortingOrder));
                    if (component is Rigidbody2D body) bodies.Add(ObservationData.Map("componentIndex", index, "position", ObservationData.Vector(body.position),
                        "rotation", body.rotation, "velocity", ObservationData.Vector(body.velocity), "angularVelocity", body.angularVelocity,
                        "gravityScale", body.gravityScale, "bodyType", Enum.GetName(typeof(RigidbodyType2D), body.bodyType), "simulated", body.simulated,
                        "isKinematic", body.isKinematic, "collisionDetectionMode", Enum.GetName(typeof(CollisionDetectionMode2D), body.collisionDetectionMode),
                        "constraints", values.Encode(body.constraints), "mass", body.mass, "drag", body.drag, "angularDrag", body.angularDrag, "sleeping", body.IsSleeping()));
                    if (component is HealthManager manager) health.Add(ObservationData.Map("componentIndex", index, "enabled", manager.enabled,
                        "hp", manager.hp, "isDead", manager.isDead, "isInvincible", manager.IsInvincible, "invincibleFromDirection", manager.InvincibleFromDirection,
                        "knownFields", KnownFields(manager, "damageOverride", "ignoreInvincible", "invincibleTimer", "isBoss", "enemyType")));
                    if (component is DamageHero hazard) damage.Add(ObservationData.Map("componentIndex", index, "enabled", hazard.enabled,
                        "damageDealt", hazard.damageDealt, "hazardType", hazard.hazardType, "shadowDashHazard", hazard.shadowDashHazard));
                    if (component is Collider2D collider) colliders.Add(geometry.Capture(collider, index, camera, Classify(collider, hero)));
                    if (component is PlayMakerFSM fsm) machines.Add(fsms.Capture(fsm, index, false));
                    if (component is ParticleSystem particle)
                    {
                        // Known read-only native getters; never simulate particles or
                        // replace their seed while inspecting a paused boundary.
                        var main = particle.main;
                        var collision = particle.collision;
                        particles.Add(ObservationData.Map("componentIndex", index,
                            "particleCount", particle.particleCount, "isPlaying", particle.isPlaying,
                            "randomSeed", particle.randomSeed, "useAutoRandomSeed", particle.useAutoRandomSeed,
                            "cullingMode", main.cullingMode.ToString(), "simulationSpace", main.simulationSpace.ToString(),
                            "collisionEnabled", collision.enabled, "collisionType", collision.type.ToString(),
                            "collisionMode", collision.mode.ToString(), "collidesWith", collision.collidesWith.value,
                            "sendCollisionMessages", collision.sendCollisionMessages, "colliderForce", collision.colliderForce,
                            "triggerEnabled", particle.trigger.enabled, "subEmitterCount", particle.subEmitters.subEmittersCount));
                    }
                }
                catch (Exception error) { errors.Add(ObservationData.Map("componentIndex", index, "type", component.GetType().FullName, "error", ObservationData.Error(error))); }
            }
            result["renderers"] = renderers; result["rigidbodies"] = bodies; result["health"] = health;
            result["damageHero"] = damage; result["colliders"] = colliders; result["fsms"] = machines; result["errors"] = errors;
            result["particles"] = particles;
            if (hero != null && go == hero.gameObject) result["hero"] = CaptureHero(hero, player);
            return result;
        }

        private static Dictionary<string, object?> ComponentInfo(Component component, int index) => ObservationData.Map(
            "index", index, "instanceId", component.GetInstanceID(), "type", component.GetType().FullName,
            "assembly", component.GetType().Assembly.GetName().Name, "enabled", component is Behaviour behaviour ? (bool?)behaviour.enabled : null);

        private string Classify(Collider2D collider, HeroController? hero)
        {
            if (hero != null && (collider.gameObject == hero.gameObject || collider.transform.IsChildOf(hero.transform)))
                return collider.gameObject == hero.gameObject ? "hero" : "hurtbox";
            if (collider.GetComponentInParent<HealthManager>() != null) return "enemy";
            if (collider.GetComponentInParent<DamageHero>() != null) return "hazard";
            if (collider.isTrigger) return "trigger";
            return "terrain";
        }

        private object CaptureHero(HeroController hero, PlayerData? player) => ObservationData.Map(
            "cState", hero.cState == null ? null : values.CaptureFields(hero.cState, field => field.FieldType == typeof(bool)),
            "actor", KnownFields(hero, "hero_state", "transitionState", "currentRunSpeed", "move_input", "vertical_input", "acceptingInput", "controlReqlinquished"),
            "cooldowns", values.CaptureFields(hero, field => field.FieldType == typeof(float) &&
                (field.Name.IndexOf("cooldown", StringComparison.OrdinalIgnoreCase) >= 0 || field.Name.IndexOf("timer", StringComparison.OrdinalIgnoreCase) >= 0)),
            "resources", KnownFields(player, "health", "maxHealth", "maxHealthBase", "healthBlue", "MPCharge", "MPReserve", "MPReserveMax", "maxMP", "geo", "nailDamage", "atBench"),
            "charms", KnownFields(player, "equippedCharms", "charmSlots", "charmSlotsFilled", "overcharmed", "canOvercharm"),
            "abilities", player == null ? null : values.CaptureFields(player, field => field.FieldType == typeof(bool) &&
                (field.Name.StartsWith("has", StringComparison.Ordinal) || field.Name.StartsWith("gotCharm_", StringComparison.Ordinal) || field.Name.StartsWith("equippedCharm_", StringComparison.Ordinal))),
            "spellLevels", KnownFields(player, "fireballLevel", "quakeLevel", "screamLevel", "nailSmithUpgrades"));

        private object KnownFields(object? owner, params string[] names)
        {
            if (owner == null) return null!;
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            var fields = values.Fields(owner.GetType());
            foreach (string name in names)
            {
                var field = fields.FirstOrDefault(candidate => candidate.Name == name || candidate.Name == "<" + name + ">k__BackingField");
                result[name] = field == null ? ObservationData.Omitted("fieldUnavailableInInstalledBuild") : values.Encode(field.GetValue(owner));
            }
            return result;
        }

        private object CaptureCamera(Camera camera) => ObservationData.Map("reference", Reference(camera), "enabled", camera.enabled,
            "activeInHierarchy", camera.gameObject.activeInHierarchy, "rect", ObservationData.Rect(camera.rect), "pixelRect", ObservationData.Rect(camera.pixelRect),
            "position", ObservationData.Vector(camera.transform.position), "rotation", ObservationData.Quaternion(camera.transform.rotation),
            "orthographic", camera.orthographic, "orthographicSize", camera.orthographicSize, "fieldOfView", camera.fieldOfView,
            "aspect", camera.aspect, "nearClipPlane", camera.nearClipPlane, "farClipPlane", camera.farClipPlane, "depth", camera.depth,
            "cullingMask", camera.cullingMask, "targetDisplay", camera.targetDisplay);

        private static object CapturePhysics()
        {
            var layers = new List<object>();
            var collisionMatrix = new bool[32][];
            for (int layer = 0; layer < 32; layer++)
            {
                layers.Add(ObservationData.Map("index", layer, "name", LayerMask.LayerToName(layer)));
                collisionMatrix[layer] = new bool[32];
                for (int other = 0; other < 32; other++) collisionMatrix[layer][other] = !Physics2D.GetIgnoreLayerCollision(layer, other);
            }
            return ObservationData.Map("gravity", ObservationData.Vector(Physics2D.gravity), "layers", layers, "layerCollisionEnabled", collisionMatrix,
                "pairwiseIgnoreCollisionCoverage", "notEnumerated: per-collider-pair IgnoreCollision rules are not represented by the layer matrix",
                "contactCoverage", "Geometry and layer policy only; no live contacts/solver manifolds or forced physics synchronization.");
        }
    }
}
