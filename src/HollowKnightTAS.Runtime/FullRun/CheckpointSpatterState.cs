using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // An explicit pool family, not a general Unity-object serializer. Only
    // already-started, root-level orange spatters from a startup pool qualify.
    [Serializable]
    internal sealed class CheckpointSpatterState
    {
        public int Version = 1;
        public Actor[] Actors = null!;
        [Serializable] internal sealed class Actor
        {
            public int StartupIndex, SpriteIndex;
            public string PrefabName = "", SpriteNames = "";
            public string? BuildTypeGuard;
            public Dictionary<string, string> Fields = null!;
            public Vector3 Position, Scale;
            public Quaternion Rotation;
            public Vector2 BodyPosition, Velocity;
            public float Gravity, AngularVelocity, Drag, AngularDrag;
            public RigidbodyType2D BodyType;
            public bool Simulated, ColliderEnabled, ColliderTrigger, RendererEnabled;
            public float[] Color = null!;
        }
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo PoolInstance = typeof(ObjectPool).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        private static readonly FieldInfo Spawned = typeof(ObjectPool).GetField("spawnedObjects", Instance)!;
        private static readonly FieldInfo Stash = typeof(ObjectPool).GetField("activeStashLocation", BindingFlags.Static | BindingFlags.NonPublic)!;
        private static readonly FieldInfo ScaleModifier = typeof(SpatterOrange).GetField("scaleModifier", Instance)!;
        private static readonly FieldInfo BuildTypes = typeof(DeactivateGameObjectPerBuildType).GetField("buildTypes", Instance)!;
        private static readonly FieldInfo[] Scalars = CheckpointScalarFields.Declared(typeof(SpatterOrange)).Where(f => f.FieldType.IsPrimitive).ToArray();
        private static ObjectPool Pool() => (ObjectPool?)PoolInstance.GetValue(null)
            ?? throw new InvalidOperationException("Checkpoint's existing object pool is absent.");
        private static Dictionary<GameObject, GameObject> Live(ObjectPool pool) => (Dictionary<GameObject, GameObject>)Spawned.GetValue(pool);
        private static string Sprites(SpatterOrange actor) => string.Join("\n", actor.sprites.Select(s => s == null ? "null" : s.name));
        private static string? Guard(SpatterOrange actor)
        {
            var guard = actor.GetComponent<DeactivateGameObjectPerBuildType>();
            if (guard == null) return null;
            var values = BuildTypes.GetValue(guard) as Array;
            if (values == null || values.Length > 32) throw new InvalidOperationException("Spatter build-type guard differs.");
            return guard.enabled + ":" + string.Join(",", values.Cast<object>().Select(Convert.ToInt32));
        }
        private static void Bind(SpatterOrange actor)
        {
            if (actor == null || actor.transform.parent != null || actor.gameObject.scene.name != "DontDestroyOnLoad"
                || actor.rb2d == null || actor.circleCollider == null || actor.spriteRenderer == null
                || !ReferenceEquals(actor.rb2d, actor.GetComponent<Rigidbody2D>())
                || !ReferenceEquals(actor.circleCollider, actor.GetComponent<CircleCollider2D>())
                || !ReferenceEquals(actor.spriteRenderer, actor.GetComponent<SpriteRenderer>())
                || actor.sprites == null || actor.sprites.Length == 0 || actor.sprites.Length > 32
                || actor.GetComponents<Component>().Any(c => !(c is Transform) && !(c is Rigidbody2D)
                    && !(c is CircleCollider2D) && !(c is SpriteRenderer) && !(c is SpatterOrange)
                    && !(c is DeactivateGameObjectPerBuildType)))
                throw new InvalidOperationException("Orange spatter has an unsupported component or pool binding: "
                    + (actor == null ? "missing" : actor.gameObject.scene.name + "/" + actor.name + "; components="
                        + string.Join(",", actor.GetComponents<Component>().Select(c => c.GetType().FullName))));
        }
        internal static CheckpointSpatterState Capture()
        {
            var pool = Pool(); var physics = new LoadingRigidbodyObservation();
            var actors = Live(pool).Where(pair => pair.Key != null && pair.Key.activeInHierarchy
                && pair.Key.GetComponent<SpatterOrange>() != null).ToArray();
            if (actors.Length > 64) throw new InvalidOperationException("Orange spatter exceeds its checkpoint bound.");
            return new CheckpointSpatterState { Actors = actors.Select(pair =>
            {
                var actor = pair.Key.GetComponent<SpatterOrange>(); Bind(actor);
                var index = Array.FindIndex(pool.startupPools, p => ReferenceEquals(p.prefab, pair.Value));
                var sprite = Array.FindIndex(actor.sprites, s => ReferenceEquals(s, actor.spriteRenderer.sprite));
                // Start sets this field exactly once. Reject a still-pending Start;
                // preparation must not silently turn that state into already-started.
                var modifier = (float)ScaleModifier.GetValue(actor);
                if (index < 0 || sprite < 0 || modifier <= 0 || float.IsNaN(modifier) || !actor.enabled)
                    throw new InvalidOperationException("Orange spatter startup pool, sprite, or Start state is unsupported.");
                var body = actor.rb2d; var values = physics.CapturePrimary(body, actor.transform.position);
                var color = actor.spriteRenderer.color;
                return new Actor { StartupIndex = index, PrefabName = pair.Value.name, SpriteNames = Sprites(actor), SpriteIndex = sprite,
                    BuildTypeGuard = Guard(actor),
                    Fields = CheckpointScalarFields.Capture(actor, Scalars), Position = actor.transform.position,
                    Rotation = actor.transform.rotation, Scale = actor.transform.localScale,
                    BodyPosition = new Vector2(values.X, values.Y), Velocity = new Vector2(values.Vx, values.Vy),
                    AngularVelocity = physics.CaptureCheckpointAngularVelocity(body), Gravity = body.gravityScale,
                    BodyType = body.bodyType, Simulated = body.simulated, Drag = body.drag, AngularDrag = body.angularDrag,
                    ColliderEnabled = actor.circleCollider.enabled, ColliderTrigger = actor.circleCollider.isTrigger,
                    RendererEnabled = actor.spriteRenderer.enabled, Color = new[] { color.r, color.g, color.b, color.a } };
            }).ToArray() };
        }
        private GameObject Prefab(ObjectPool pool, Actor saved)
        {
            if (Version != 1 || Actors == null || Actors.Length > 64 || saved == null
                || saved.StartupIndex < 0 || saved.StartupIndex >= pool.startupPools.Length
                || !Enum.IsDefined(typeof(RigidbodyType2D), saved.BodyType)
                || saved.Color == null || saved.Color.Length != 4
                || new[] { saved.Position.x, saved.Position.y, saved.Position.z, saved.Scale.x, saved.Scale.y, saved.Scale.z,
                    saved.Rotation.x, saved.Rotation.y, saved.Rotation.z, saved.Rotation.w, saved.BodyPosition.x, saved.BodyPosition.y,
                    saved.Velocity.x, saved.Velocity.y, saved.AngularVelocity, saved.Gravity, saved.Drag, saved.AngularDrag }
                    .Concat(saved.Color).Any(v => float.IsNaN(v) || float.IsInfinity(v)))
                throw new InvalidDataException("Orange spatter checkpoint data is invalid.");
            var prefab = pool.startupPools[saved.StartupIndex].prefab;
            var actor = prefab == null ? null : prefab.GetComponent<SpatterOrange>();
            if (actor == null || prefab!.name != saved.PrefabName || Sprites(actor) != saved.SpriteNames || Guard(actor) != saved.BuildTypeGuard
                || saved.SpriteIndex < 0 || saved.SpriteIndex >= actor.sprites.Length)
                throw new InvalidDataException("Orange spatter startup prefab differs.");
            return prefab!;
        }
        internal Preparation PrepareActors()
        {
            if (Version != 1 || Actors == null || Actors.Length > 64) throw new InvalidDataException("Orange spatter schema differs.");
            var pool = Pool(); var prefabs = Actors.Select(a => Prefab(pool, a)).ToArray();
            if (Live(pool).Keys.Any(go => go != null && go.activeInHierarchy && go.GetComponent<SpatterOrange>() != null))
                throw new InvalidOperationException("Preparation already has active orange spatters.");
            var result = new Preparation();
            foreach (var prefab in prefabs)
            {
                // Use the game's pool bookkeeping. Original Start runs once in
                // preparation, then its observer parks this actor before C.
                var stash = (Vector2)Stash.GetValue(null);
                var go = ObjectPool.Spawn(prefab, null, new Vector3(stash.x, stash.y, 0f), Quaternion.identity);
                var actor = go.GetComponent<SpatterOrange>(); Bind(actor);
                if (!actor.enabled) throw new InvalidOperationException("Preparation spatter's original Start is disabled.");
                actor.rb2d.simulated = false; actor.circleCollider.enabled = false;
                result.Actors.Add(actor);
                // A reused pool member may already have completed Start. Its
                // positive private modifier is written only by original Start.
                var modifier = (float)ScaleModifier.GetValue(actor);
                if (modifier > 0 && !float.IsNaN(modifier) && !float.IsInfinity(modifier)) result.ObserveStarted(actor);
            }
            return result;
        }
        internal sealed class Preparation
        {
            internal readonly List<SpatterOrange> Actors = new List<SpatterOrange>();
            private readonly HashSet<SpatterOrange> started = new HashSet<SpatterOrange>();
            internal bool Ready => started.Count == Actors.Count;
            internal void ObserveStarted(SpatterOrange actor)
            {
                if (!Actors.Contains(actor)) return;
                actor.enabled = false; started.Add(actor);
            }
        }
        internal Action PrepareApply(Preparation prepared)
        {
            if (prepared == null || !prepared.Ready || Actors == null || prepared.Actors.Count != Actors.Length)
                throw new InvalidOperationException("Orange spatter preparation is incomplete.");
            var pool = Pool(); var writes = new List<Action>();
            for (var i = 0; i < Actors.Length; i++)
            {
                var saved = Actors[i]; var actor = prepared.Actors[i]; Bind(actor); var prefab = Prefab(pool, saved);
                if (!Live(pool).TryGetValue(actor.gameObject, out var bound) || !ReferenceEquals(bound, prefab)
                    || actor.enabled || actor.rb2d.simulated || actor.circleCollider.enabled)
                    throw new InvalidOperationException("Orange spatter's parked pool actor differs.");
                var fields = CheckpointScalarFields.PrepareApply(actor, Scalars, saved.Fields);
                writes.Add(() =>
                {
                    var body = actor.rb2d;
                    actor.transform.position = saved.Position; actor.transform.rotation = saved.Rotation; actor.transform.localScale = saved.Scale;
                    body.bodyType = saved.BodyType; body.gravityScale = saved.Gravity; body.drag = saved.Drag; body.angularDrag = saved.AngularDrag;
                    body.position = saved.BodyPosition; body.velocity = saved.Velocity; body.angularVelocity = saved.AngularVelocity;
                    body.simulated = saved.Simulated; actor.circleCollider.isTrigger = saved.ColliderTrigger;
                    actor.circleCollider.enabled = saved.ColliderEnabled;
                    actor.spriteRenderer.sprite = actor.sprites[saved.SpriteIndex];
                    actor.spriteRenderer.color = new Color(saved.Color[0], saved.Color[1], saved.Color[2], saved.Color[3]);
                    actor.spriteRenderer.enabled = saved.RendererEnabled; fields(); actor.enabled = true;
                });
            }
            return () => { foreach (var write in writes) write(); };
        }
    }
}
