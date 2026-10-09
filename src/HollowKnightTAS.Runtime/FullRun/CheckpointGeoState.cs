using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using HollowKnightTAS.Core.Cryptography;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Already-started, uncollected Geo from the game's startup pool. Collection
    // and Getter waits require their own continuation and are rejected here.
    [Serializable]
    internal sealed class CheckpointGeoState
    {
        public int Version = 1;
        public Actor[] Actors = null!;
        [Serializable] internal sealed class Actor
        {
            public int StartupIndex, SpriteId, ClipIndex;
            public string PrefabName = "", Layout = "", Library = "", Collection = "";
            public Dictionary<string, string> Geo = null!, Animation = null!, Flash = null!, Bounce = null!, Conveyor = null!, Recycle = null!, AnimationGlobals = null!;
            public Vector3 Position, Scale;
            public Quaternion Rotation;
            public Vector2 BodyPosition, Velocity;
            public float AngularVelocity, Gravity, Drag, AngularDrag;
            public RigidbodyType2D BodyType;
            public bool Simulated, ColliderEnabled, ColliderTrigger, RendererEnabled;
            public float[] Color = null!;
        }
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo PoolInstance = typeof(ObjectPool).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        private static readonly FieldInfo Spawned = typeof(ObjectPool).GetField("spawnedObjects", Instance)!;
        private static readonly FieldInfo Stash = typeof(ObjectPool).GetField("activeStashLocation", BindingFlags.Static | BindingFlags.NonPublic)!;
        private static FieldInfo Field(Type type, string name) => type.GetField(name, Instance)!;
        private static FieldInfo[] Scalars(Type type) => CheckpointScalarFields.Declared(type).Where(f =>
            f.FieldType.IsPrimitive && f.FieldType != typeof(IntPtr) && f.FieldType != typeof(UIntPtr)
            || f.FieldType.IsEnum || f.FieldType == typeof(string) || f.FieldType == typeof(Vector2)
            || f.FieldType == typeof(GeoControl.Size) || f.FieldType == typeof(Color)).ToArray();
        private static readonly FieldInfo[] GeoFields = Scalars(typeof(GeoControl)), AnimFields = Scalars(typeof(tk2dSpriteAnimator)),
            FlashFields = Scalars(typeof(SpriteFlash)), BounceFields = Scalars(typeof(ObjectBounce)),
            ConveyorFields = Scalars(typeof(ConveyorMovement)), RecycleFields = Scalars(typeof(AutoRecycleSelf));
        private static readonly FieldInfo[] AnimationGlobals = CheckpointScalarFields.Declared(typeof(tk2dSpriteAnimator), true).Where(f => f.FieldType.IsEnum).ToArray();
        private static readonly FieldInfo Clip = Field(typeof(tk2dSpriteAnimator), "currentClip");
        private static ObjectPool Pool() => (ObjectPool?)PoolInstance.GetValue(null) ?? throw new InvalidOperationException("Geo pool is absent.");
        private static Dictionary<GameObject, GameObject> Live(ObjectPool pool) => (Dictionary<GameObject, GameObject>)Spawned.GetValue(pool);
        internal static GameObject[] LiveActors() => Live(Pool()).Keys.Where(go => go != null && go.activeInHierarchy && go.GetComponent<GeoControl>() != null).ToArray();
        internal static Dictionary<string, string> Observe(LoadingRigidbodyObservation physics)
        {
            var result = new Dictionary<string, string>(); var actors = LiveActors();
            result["geo.active-count"] = actors.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            for (var i = 0; i < actors.Length; i++)
            {
                var go = actors[i]; var body = go.GetComponent<Rigidbody2D>(); var position = go.transform.position;
                var values = physics.CapturePrimary(body, position); var anim = go.GetComponent<tk2dSpriteAnimator>();
                Func<float, string> bits = value => BitConverter.ToUInt32(BitConverter.GetBytes(value), 0).ToString("x8");
                var state = new { x = bits(values.X), y = bits(values.Y), vx = bits(values.Vx), vy = bits(values.Vy),
                    geo = CheckpointScalarFields.Capture(go.GetComponent<GeoControl>(), GeoFields),
                    bounce = CheckpointScalarFields.Capture(go.GetComponent<ObjectBounce>(), BounceFields),
                    conveyor = CheckpointScalarFields.Capture(go.GetComponent<ConveyorMovement>(), ConveyorFields),
                    recycle = CheckpointScalarFields.Capture(go.GetComponent<AutoRecycleSelf>(), RecycleFields),
                    animation = CheckpointScalarFields.Capture(anim, AnimFields), clip = anim.CurrentClip?.name,
                    sprite = go.GetComponent<tk2dSprite>().spriteId };
                result["geo.actor-" + i] = Sha256Utility.ComputeHex(System.Text.Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(state)));
            }
            return result;
        }
        private static string Layout(GeoControl actor) => string.Join("\n", actor.GetComponentsInChildren<Transform>(true).Select(t =>
            (t == actor.transform ? "root" : t.name) + ":" + string.Join(",", t.GetComponents<Component>().Select(c => c.GetType().FullName))));
        private static void Bind(GeoControl actor, bool started)
        {
            var go = actor.gameObject; var body = go.GetComponent<Rigidbody2D>(); var sprite = go.GetComponent<tk2dSprite>();
            var anim = go.GetComponent<tk2dSpriteAnimator>(); var flash = go.GetComponent<SpriteFlash>();
            var bounce = go.GetComponent<ObjectBounce>();
            var recycle = go.GetComponent<AutoRecycleSelf>();
            if (actor.transform.parent != null || go.scene.name != "DontDestroyOnLoad" || body == null || sprite == null || anim == null || flash == null
                || !ReferenceEquals(Field(typeof(GeoControl), "body").GetValue(actor), body)
                || !ReferenceEquals(Field(typeof(GeoControl), "anim").GetValue(actor), anim)
                || !ReferenceEquals(Field(typeof(GeoControl), "boxCollider").GetValue(actor), go.GetComponent<BoxCollider2D>())
                || !ReferenceEquals(Field(typeof(GeoControl), "spriteFlash").GetValue(actor), flash)
                || anim.AnimationCompleted != null || anim.AnimationEventTriggered != null
                || (bool)Field(typeof(GeoControl), "activated").GetValue(actor)
                || Field(typeof(GeoControl), "getterRoutine").GetValue(actor) != null
                || Field(typeof(GeoControl), "player").GetValue(actor) != null
                || actor.getterBug != null && actor.getterBug.activeSelf || actor.acidEffect != null && actor.acidEffect.gameObject.activeSelf
                || started && !ReferenceEquals(Field(typeof(GeoControl), "hero").GetValue(actor), HeroController.SilentInstance))
                throw new InvalidOperationException("Geo binding or collection/Getter continuation is unsupported: " + go.name);
            if (bounce == null || bounce.sendFSMEvent || Field(typeof(ObjectBounce), "OnBounce").GetValue(bounce) != null
                || started && (!ReferenceEquals(Field(typeof(ObjectBounce), "rb").GetValue(bounce), body)
                    || !ReferenceEquals(Field(typeof(ObjectBounce), "animator").GetValue(bounce), anim)
                    || !ReferenceEquals(Field(typeof(ObjectBounce), "audio").GetValue(bounce), go.GetComponent<AudioSource>())))
                throw new InvalidOperationException("Geo bounce aliases, callbacks or Start state differ.");
            if (go.GetComponent<ConveyorMovement>() == null || recycle == null
                || recycle.afterEvent == GlobalEnums.AfterEvent.TIME && recycle.timeToWait > 0
                || (bool)Field(typeof(AutoRecycleSelf), "validAudioSource").GetValue(recycle))
                throw new InvalidOperationException("Geo timed/audio recycle needs its continuation: " + recycle?.afterEvent);
            foreach (var c in go.GetComponents<Component>())
                if (!(c is Transform) && !(c is Rigidbody2D) && !(c is BoxCollider2D) && !(c is tk2dSprite)
                    && !(c is tk2dSpriteAnimator) && !(c is SpriteFlash) && !(c is MeshFilter) && !(c is MeshRenderer)
                    && !(c is AudioSource) && !(c is GeoControl) && !(c is ObjectBounce) && !(c is ConveyorMovement) && !(c is AutoRecycleSelf))
                    throw new InvalidOperationException("Geo has unsupported components: " + string.Join(",", go.GetComponents<Component>().Select(item => item.GetType().FullName)));
            if ((int)Field(typeof(SpriteFlash), "flashingState").GetValue(flash) != 0
                || (bool)Field(typeof(SpriteFlash), "geoFlash").GetValue(flash)
                || (bool)Field(typeof(SpriteFlash), "repeatFlash").GetValue(flash)
                || (bool)Field(typeof(SpriteFlash), "cancelFlash").GetValue(flash))
                throw new InvalidOperationException("Active Geo flashing needs its material continuation.");
        }
        internal static CheckpointGeoState Capture()
        {
            var pool = Pool(); var physics = new LoadingRigidbodyObservation(); var actors = LiveActors();
            if (actors.Length > 64) throw new InvalidOperationException("Geo actor bound exceeded.");
            return new CheckpointGeoState { Actors = actors.Select(go =>
            {
                var actor = go.GetComponent<GeoControl>(); Bind(actor, true);
                var body = go.GetComponent<Rigidbody2D>(); var sprite = go.GetComponent<tk2dSprite>(); var anim = go.GetComponent<tk2dSpriteAnimator>();
                var prefab = Live(pool)[go]; var index = Array.FindIndex(pool.startupPools, p => ReferenceEquals(p.prefab, prefab));
                var clipIndex = Array.FindIndex(anim.Library.clips, c => ReferenceEquals(c, Clip.GetValue(anim)));
                if (index < 0 || clipIndex < 0 || !actor.enabled) throw new InvalidOperationException("Geo startup pool, clip or enabled state differs.");
                var values = physics.CapturePrimary(body, actor.transform.position);
                Modding.Logger.Log("[HKTAS] Captured active Geo: pool=" + index + " prefab=" + prefab.name + " clip=" + anim.Library.clips[clipIndex].name);
                return new Actor { StartupIndex = index, PrefabName = prefab.name, Layout = Layout(actor),
                    Geo = CheckpointScalarFields.Capture(actor, GeoFields), Animation = CheckpointScalarFields.Capture(anim, AnimFields),
                    Flash = CheckpointScalarFields.Capture(go.GetComponent<SpriteFlash>(), FlashFields),
                    Bounce = CheckpointScalarFields.Capture(go.GetComponent<ObjectBounce>(), BounceFields),
                    Conveyor = CheckpointScalarFields.Capture(go.GetComponent<ConveyorMovement>(), ConveyorFields),
                    Recycle = CheckpointScalarFields.Capture(go.GetComponent<AutoRecycleSelf>(), RecycleFields),
                    AnimationGlobals = CheckpointScalarFields.Capture(null, AnimationGlobals),
                    Library = anim.Library.name, Collection = sprite.Collection.name, SpriteId = sprite.spriteId, ClipIndex = clipIndex,
                    Color = new[] { sprite.color.r, sprite.color.g, sprite.color.b, sprite.color.a },
                    Position = actor.transform.position, Rotation = actor.transform.rotation, Scale = actor.transform.localScale,
                    BodyPosition = new Vector2(values.X, values.Y), Velocity = new Vector2(values.Vx, values.Vy),
                    AngularVelocity = physics.CaptureCheckpointAngularVelocity(body), Gravity = body.gravityScale, Drag = body.drag, AngularDrag = body.angularDrag,
                    BodyType = body.bodyType, Simulated = body.simulated, ColliderEnabled = go.GetComponent<BoxCollider2D>().enabled,
                    ColliderTrigger = go.GetComponent<BoxCollider2D>().isTrigger, RendererEnabled = go.GetComponent<Renderer>().enabled };
            }).ToArray() };
        }
        private GameObject Prefab(Actor saved)
        {
            var pool = Pool();
            if (Version != 1 || Actors == null || Actors.Length > 64 || saved.StartupIndex < 0 || saved.StartupIndex >= pool.startupPools.Length
                || !Enum.IsDefined(typeof(RigidbodyType2D), saved.BodyType) || saved.Color == null || saved.Color.Length != 4
                || new[] { saved.Position.x, saved.Position.y, saved.Position.z, saved.Scale.x, saved.Scale.y, saved.Scale.z,
                    saved.Rotation.x, saved.Rotation.y, saved.Rotation.z, saved.Rotation.w, saved.BodyPosition.x, saved.BodyPosition.y,
                    saved.Velocity.x, saved.Velocity.y, saved.AngularVelocity, saved.Gravity, saved.Drag, saved.AngularDrag }
                    .Concat(saved.Color).Any(v => float.IsNaN(v) || float.IsInfinity(v)))
                throw new InvalidDataException("Geo checkpoint schema differs.");
            var prefab = pool.startupPools[saved.StartupIndex].prefab; var sprite = prefab.GetComponent<tk2dSprite>(); var anim = prefab.GetComponent<tk2dSpriteAnimator>();
            if (prefab.name != saved.PrefabName || sprite == null || anim == null || Layout(prefab.GetComponent<GeoControl>()) != saved.Layout
                || sprite.Collection.name != saved.Collection || anim.Library.name != saved.Library || saved.SpriteId < 0 || saved.SpriteId >= sprite.Collection.spriteDefinitions.Length
                || saved.ClipIndex < 0 || saved.ClipIndex >= anim.Library.clips.Length)
                throw new InvalidDataException("Geo startup prefab layout differs.");
            return prefab;
        }
        internal Preparation PrepareActors()
        {
            if (Version != 1 || Actors == null || Actors.Length > 64) throw new InvalidDataException("Geo schema differs.");
            if (LiveActors().Length != 0 || PlayerData.instance.equippedCharm_1)
                throw new InvalidOperationException("Geo preparation has existing actors or an active Getter charm.");
            var prefabs = Actors.Select(Prefab).ToArray(); var result = new Preparation();
            foreach (var prefab in prefabs)
            {
                var stash = (Vector2)Stash.GetValue(null); var go = ObjectPool.Spawn(prefab, null, new Vector3(stash.x, stash.y, 0f), Quaternion.identity);
                var actor = go.GetComponent<GeoControl>(); Bind(actor, false);
                go.GetComponent<Rigidbody2D>().simulated = false; go.GetComponent<BoxCollider2D>().enabled = false;
                result.Actors.Add(actor);
                if (ReferenceEquals(Field(typeof(GeoControl), "hero").GetValue(actor), HeroController.SilentInstance)) result.ObserveStarted(actor);
            }
            return result;
        }
        internal sealed class Preparation
        {
            internal readonly List<GeoControl> Actors = new List<GeoControl>();
            private readonly HashSet<GeoControl> started = new HashSet<GeoControl>();
            internal bool Ready => started.Count == Actors.Count && Actors.All(a =>
                Field(typeof(ObjectBounce), "rb").GetValue(a.GetComponent<ObjectBounce>()) != null
                && Field(typeof(SpriteFlash), "block").GetValue(a.GetComponent<SpriteFlash>()) != null);
            internal void ObserveStarted(GeoControl actor) { if (Actors.Contains(actor)) { actor.enabled = false; started.Add(actor); } }
        }
        internal Action PrepareApply(Preparation prepared)
        {
            if (prepared == null || !prepared.Ready || prepared.Actors.Count != Actors.Length) throw new InvalidOperationException("Geo preparation is incomplete.");
            var writes = new List<Action>();
            for (var i = 0; i < Actors.Length; i++)
            {
                var saved = Actors[i]; var actor = prepared.Actors[i]; Bind(actor, true); var prefab = Prefab(saved);
                var go = actor.gameObject; var body = go.GetComponent<Rigidbody2D>(); var collider = go.GetComponent<BoxCollider2D>();
                var sprite = go.GetComponent<tk2dSprite>(); var anim = go.GetComponent<tk2dSpriteAnimator>();
                if (!Live(Pool()).TryGetValue(go, out var bound) || !ReferenceEquals(bound, prefab) || actor.enabled || body.simulated || collider.enabled)
                    throw new InvalidOperationException("Parked Geo pool binding differs.");
                var geo = CheckpointScalarFields.PrepareApply(actor, GeoFields, saved.Geo);
                var animation = CheckpointScalarFields.PrepareApply(anim, AnimFields, saved.Animation);
                var flash = CheckpointScalarFields.PrepareApply(go.GetComponent<SpriteFlash>(), FlashFields, saved.Flash);
                var bounce = CheckpointScalarFields.PrepareApply(go.GetComponent<ObjectBounce>(), BounceFields, saved.Bounce);
                var conveyor = CheckpointScalarFields.PrepareApply(go.GetComponent<ConveyorMovement>(), ConveyorFields, saved.Conveyor);
                var recycle = CheckpointScalarFields.PrepareApply(go.GetComponent<AutoRecycleSelf>(), RecycleFields, saved.Recycle);
                var globals = CheckpointScalarFields.PrepareApply(null, AnimationGlobals, saved.AnimationGlobals);
                writes.Add(() =>
                {
                    actor.transform.position = saved.Position; actor.transform.rotation = saved.Rotation; actor.transform.localScale = saved.Scale;
                    body.bodyType = saved.BodyType; body.gravityScale = saved.Gravity; body.drag = saved.Drag; body.angularDrag = saved.AngularDrag;
                    body.position = saved.BodyPosition; body.velocity = saved.Velocity; body.angularVelocity = saved.AngularVelocity;
                    body.simulated = saved.Simulated; collider.isTrigger = saved.ColliderTrigger; collider.enabled = saved.ColliderEnabled;
                    sprite.SetSprite(saved.SpriteId); sprite.color = new Color(saved.Color[0], saved.Color[1], saved.Color[2], saved.Color[3]);
                    go.GetComponent<Renderer>().enabled = saved.RendererEnabled;
                    Clip.SetValue(anim, anim.Library.clips[saved.ClipIndex]); animation(); globals(); flash(); bounce(); conveyor(); recycle(); geo();
                    CheckpointBehaviourEnable.Resume(actor);
                });
            }
            return () => { foreach (var write in writes) write(); };
        }
    }
}
