using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using HutongGames.PlayMaker;

namespace HollowKnightTAS.Runtime.FullRun
{
    // One original global-pool family. Do not repeat source initialization or
    // impact effects. Only original iterator state and its queued wait resume.
    [Serializable]
    internal sealed class CheckpointProjectileState
    {
        public int Version = 1;
        public Actor[] Actors = null!;
        [Serializable] internal sealed class Actor
        {
            public string Layout = "", Library = "", Collection = "", LightSprite = "";
            public int SpriteId, ClipIndex;
            public Dictionary<string, string>[] Fields = null!;
            public Dictionary<string, string> AnimationGlobals = null!;
            public bool[] Enabled = null!;
            public Vector3 Position, Scale, LightPosition, LightScale;
            public Quaternion Rotation, LightRotation;
            public Vector2 BodyPosition, Velocity;
            public float AngularVelocity, Gravity, Drag, AngularDrag;
            public RigidbodyType2D BodyType;
            public bool Simulated, ColliderEnabled, ColliderTrigger, RendererEnabled, LightActive, LightEnabled;
            public float[] Color = null!, LightColor = null!;
            public WaitData[] Waits = null!;
        }
        [Serializable] internal sealed class WaitData
        {
            public string Iterator = "", Callback = "";
            public Dictionary<string, string> Fields = null!;
            public int ImpactClip = -1, Mode, Rate;
            public float? Seconds;
            public double WakeTime;
            public long Frame;
            public byte Repeat;
            public bool Flag128;
        }
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo PoolInstance = typeof(ObjectPool).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        private static readonly FieldInfo Spawned = typeof(ObjectPool).GetField("spawnedObjects", Instance)!;
        private static readonly FieldInfo Stash = typeof(ObjectPool).GetField("activeStashLocation", BindingFlags.Static | BindingFlags.NonPublic)!;
        private static FieldInfo Field(Type type, string name) => type.GetField(name, Instance)!;
        private static FieldInfo[] Scalars(Type type) => CheckpointScalarFields.Declared(type).Where(f =>
            f.FieldType.IsPrimitive && f.FieldType != typeof(IntPtr) && f.FieldType != typeof(UIntPtr)
            || f.FieldType.IsEnum || f.FieldType == typeof(int?) || f.FieldType == typeof(Vector2)).ToArray();
        private static readonly Type[] BehaviourTypes = { typeof(EnemyBullet), typeof(SetZ), typeof(DamageHero), typeof(NonBouncer),
            typeof(tk2dSpriteAnimator), typeof(PlayMakerCollisionEnter2D), typeof(PlayMakerFixedUpdate) };
        private static readonly FieldInfo[][] BehaviourFields = BehaviourTypes.Take(5).Select(Scalars).ToArray();
        private static readonly FieldInfo[] AnimGlobals = CheckpointScalarFields.Declared(typeof(tk2dSpriteAnimator), true).Where(f => f.FieldType.IsEnum).ToArray();
        private static readonly Type CollisionIterator = typeof(EnemyBullet).GetNestedTypes(BindingFlags.NonPublic).Single(t => t.Name.StartsWith("<Collision>", StringComparison.Ordinal));
        private static readonly Type ZIterator = typeof(SetZ).GetNestedTypes(BindingFlags.NonPublic).Single(t => t.Name.StartsWith("<SetPosition>", StringComparison.Ordinal));
        private static readonly FieldInfo ImpactClipField = CheckpointScalarFields.Declared(CollisionIterator).Single(f => f.FieldType == typeof(tk2dSpriteAnimationClip));
        private static readonly FieldInfo Clip = Field(typeof(tk2dSpriteAnimator), "currentClip"), SecondsField = Field(typeof(WaitForSeconds), "m_Seconds");
        private static ObjectPool Pool() => (ObjectPool?)PoolInstance.GetValue(null) ?? throw new InvalidOperationException("Projectile pool is absent.");
        private static Dictionary<GameObject, GameObject> Live(ObjectPool pool) => (Dictionary<GameObject, GameObject>)Spawned.GetValue(pool);
        internal static GameObject[] LiveActors() => Live(Pool()).Keys.Where(go => go != null && go.activeInHierarchy && go.GetComponent<EnemyBullet>() != null).ToArray();
        private static GameObject Prefab()
        {
            // "Global" in this action means the game's global object pool,
            // not a named FsmVariables.GlobalVariables entry. At C use the
            // pool's actual source binding; preparation uses original Drip.
            var actors = LiveActors();
            GameObject[] prefabs;
            if (actors.Length != 0) prefabs = actors.Select(go => Live(Pool())[go]).Distinct().ToArray();
            else prefabs = Resources.FindObjectsOfTypeAll<PlayMakerFSM>()
                .Where(f => f != null && f.gameObject.scene.IsValid() && f.gameObject.scene.isLoaded && f.FsmName == "Drip")
                .SelectMany(f => f.FsmStates).SelectMany(s => s.Actions)
                .OfType<HutongGames.PlayMaker.Actions.SpawnObjectFromGlobalPool>()
                .Select(a => a.gameObject.Value).Where(go => go != null && go.GetComponent<EnemyBullet>() != null).Distinct().ToArray();
            if (prefabs.Length != 1 || prefabs[0].name != "Shot Mawlek" || prefabs[0].scene.IsValid())
                throw new InvalidOperationException("Original Shot Mawlek pool/Drip prefab binding differs.");
            return prefabs[0];
        }
        private static string Layout(GameObject go) => string.Join(";", go.GetComponentsInChildren<Transform>(true).Select(t =>
            (t == go.transform ? "root" : t.name) + ":" + string.Join(",", t.GetComponents<Component>().Select(c => c.GetType().FullName))));
        private static SpriteRenderer Light(GameObject go) => go.transform.GetChild(0).GetComponent<SpriteRenderer>();
        private static float[] ColorData(Color c) => new[] { c.r, c.g, c.b, c.a };
        private static Color ColorValue(float[] c)
        {
            if (c == null || c.Length != 4 || c.Any(v => float.IsNaN(v) || float.IsInfinity(v))) throw new InvalidDataException("Projectile color differs.");
            return new Color(c[0], c[1], c[2], c[3]);
        }
        private static IEnumerator Original(IEnumerator iterator) => iterator is ResumeRoutine r ? r.Original : iterator;
        private static MonoBehaviour Owner(IEnumerator iterator)
        { iterator = Original(iterator); return (MonoBehaviour)Field(iterator.GetType(), "<>4__this").GetValue(iterator); }
        private static bool Bound(IEnumerator iterator, GameObject[] actors)
        {
            var type = Original(iterator).GetType();
            return (type == CollisionIterator || type == ZIterator) && actors.Contains(Owner(iterator).gameObject);
        }
        private static void Bind(GameObject go)
        {
            var bullet = go.GetComponent<EnemyBullet>(); var body = go.GetComponent<Rigidbody2D>(); var anim = go.GetComponent<tk2dSpriteAnimator>();
            if (go.transform.parent != null || go.scene.name != "DontDestroyOnLoad" || bullet == null || body == null || anim == null
                || go.GetComponent<tk2dSprite>() == null || go.GetComponent<BoxCollider2D>() == null
                || !ReferenceEquals(Field(typeof(EnemyBullet), "body").GetValue(bullet), body)
                || !ReferenceEquals(Field(typeof(EnemyBullet), "anim").GetValue(bullet), anim)
                || !ReferenceEquals(Field(typeof(EnemyBullet), "col").GetValue(bullet), go.GetComponent<BoxCollider2D>())
                || anim.AnimationCompleted != null || anim.AnimationEventTriggered != null
                || go.transform.childCount != 1 || go.transform.GetChild(0).name != "HeroLight"
                || Light(go) == null || go.transform.GetChild(0).childCount != 0
                || go.transform.GetChild(0).GetComponents<Component>().Any(c => !(c is Transform) && !(c is SpriteRenderer))
                || go.GetComponent<AudioSource>() == null || go.GetComponent<AudioSource>().isPlaying)
                throw new InvalidOperationException("Projectile binding or animation/audio continuation differs: " + Layout(go)
                    + "; scene=" + go.scene.name + "; parent=" + (go.transform.parent != null)
                    + "; bodyAlias=" + ReferenceEquals(Field(typeof(EnemyBullet), "body").GetValue(bullet), body)
                    + "; animAlias=" + ReferenceEquals(Field(typeof(EnemyBullet), "anim").GetValue(bullet), anim)
                    + "; colAlias=" + ReferenceEquals(Field(typeof(EnemyBullet), "col").GetValue(bullet), go.GetComponent<BoxCollider2D>())
                    + "; auto=" + anim?.playAutomatically + "; audio=" + go.GetComponent<AudioSource>()?.isPlaying
                    + "; callbacks=" + (anim?.AnimationCompleted != null || anim?.AnimationEventTriggered != null));
            foreach (var c in go.GetComponents<Component>())
                if (!(c is Transform) && !(c is MeshFilter) && !(c is MeshRenderer) && !(c is tk2dSprite) && !(c is Rigidbody2D)
                    && !(c is BoxCollider2D) && !(c is AudioSource) && !BehaviourTypes.Contains(c.GetType()))
                    throw new InvalidOperationException("Projectile component layout differs: " + Layout(go));
            if (BehaviourTypes.Any(t => go.GetComponents(t).Length != 1)) throw new InvalidOperationException("Projectile component count differs.");
            foreach (var proxy in go.GetComponents<PlayMakerProxyBase>())
                if (proxy.TargetFSMs.Count != 0 || CheckpointScalarFields.Declared(typeof(PlayMakerProxyBase)).Any(f => typeof(Delegate).IsAssignableFrom(f.FieldType) && f.GetValue(proxy) != null))
                    throw new InvalidOperationException("Projectile PlayMaker forwarding needs explicit FSM bindings.");
        }
        internal static CheckpointProjectileState Capture(LoadingRigidbodyObservation physics, LoadingNativeSchedulerDigest scheduler)
        {
            var actors = LiveActors();
            if (actors.Length > 64) throw new InvalidOperationException("Projectile actor bound exceeded.");
            if (actors.Length == 0) return new CheckpointProjectileState { Actors = Array.Empty<Actor>() };
            var prefab = Prefab(); var pool = Pool();
            var waits = scheduler.CaptureWaits(i => Bound(i, actors), 128, "EnemyBullet/SetZ");
            return new CheckpointProjectileState { Actors = actors.Select(go =>
            {
                Bind(go);
                if (!ReferenceEquals(Live(pool)[go], prefab) || !go.GetComponent<EnemyBullet>().enabled || !go.GetComponent<SetZ>().enabled)
                    throw new InvalidOperationException("Projectile global prefab or enabled binding differs.");
                var body = go.GetComponent<Rigidbody2D>(); var anim = go.GetComponent<tk2dSpriteAnimator>(); var sprite = go.GetComponent<tk2dSprite>();
                var light = Light(go); var values = physics.CapturePrimary(body, go.transform.position);
                var actorWaits = waits.Where(w => ReferenceEquals(Owner(w.Enumerator).gameObject, go)).Select(w => CaptureWait(w, anim)).ToArray();
                if (actorWaits.Count(w => w.Iterator == CollisionIterator.FullName) != ((bool)Field(typeof(EnemyBullet), "active").GetValue(go.GetComponent<EnemyBullet>()) ? 0 : 1)
                    || actorWaits.Count(w => w.Iterator == ZIterator.FullName) > 1)
                    throw new InvalidOperationException("Projectile queued continuation count differs.");
                Modding.Logger.Log("[HKTAS] Captured Shot Mawlek continuation: actor=" + Array.IndexOf(actors, go) + " waits=" + actorWaits.Length);
                return new Actor { Layout = Layout(go), Library = anim.Library.name, Collection = sprite.Collection.name,
                    SpriteId = sprite.spriteId, ClipIndex = Array.FindIndex(anim.Library.clips, c => ReferenceEquals(c, Clip.GetValue(anim))),
                    Fields = BehaviourFields.Select((f, i) => CheckpointScalarFields.Capture(go.GetComponent(BehaviourTypes[i]), f)).ToArray(),
                    AnimationGlobals = CheckpointScalarFields.Capture(null, AnimGlobals),
                    Enabled = BehaviourTypes.Select(t => ((Behaviour)go.GetComponent(t)).enabled).ToArray(),
                    Position = go.transform.position, Rotation = go.transform.rotation, Scale = go.transform.localScale,
                    BodyPosition = new Vector2(values.X, values.Y), Velocity = new Vector2(values.Vx, values.Vy),
                    AngularVelocity = physics.CaptureCheckpointAngularVelocity(body), Gravity = body.gravityScale, Drag = body.drag, AngularDrag = body.angularDrag,
                    BodyType = body.bodyType, Simulated = body.simulated, ColliderEnabled = go.GetComponent<BoxCollider2D>().enabled,
                    ColliderTrigger = go.GetComponent<BoxCollider2D>().isTrigger, RendererEnabled = go.GetComponent<Renderer>().enabled, Color = ColorData(sprite.color),
                    LightPosition = light.transform.localPosition, LightRotation = light.transform.localRotation, LightScale = light.transform.localScale,
                    LightSprite = light.sprite == null ? "null" : light.sprite.name, LightActive = light.gameObject.activeSelf,
                    LightEnabled = light.enabled, LightColor = ColorData(light.color), Waits = actorWaits };
            }).ToArray() };
        }
        private static WaitData CaptureWait(LoadingNativeSchedulerDigest.InvulnerabilityWait w, tk2dSpriteAnimator anim)
        {
            var iterator = Original(w.Enumerator); var type = iterator.GetType(); var current = Field(type, "<>2__current").GetValue(iterator);
            var state = (int)Field(type, "<>1__state").GetValue(iterator);
            if (type == ZIterator && (state != 1 || !(current is WaitForSeconds))
                || type == CollisionIterator && (state == 1 ? current != null : state != 2 || !(current is WaitForSeconds)))
                throw new InvalidOperationException("Projectile is not on an original supported yield.");
            var impact = type == CollisionIterator ? Array.FindIndex(anim.Library.clips, c => ReferenceEquals(c, ImpactClipField.GetValue(iterator))) : -1;
            if (type == CollisionIterator && impact < 0) throw new InvalidOperationException("Projectile Impact clip binding differs.");
            return new WaitData { Iterator = type.FullName!, Callback = w.Call, Fields = CheckpointScalarFields.Capture(iterator, Scalars(type)),
                ImpactClip = impact, Seconds = current is WaitForSeconds seconds ? (float?)SecondsField.GetValue(seconds) : null,
                WakeTime = w.WakeTime, Frame = w.Frame, Mode = w.Mode, Rate = w.Rate, Repeat = w.Repeat, Flag128 = w.Flag128 };
        }
        private void Validate(Actor saved, GameObject prefab)
        {
            var anim = prefab.GetComponent<tk2dSpriteAnimator>(); var sprite = prefab.GetComponent<tk2dSprite>();
            if (Version != 1 || Actors == null || Actors.Length > 64 || saved == null || saved.Layout != Layout(prefab)
                || anim == null || sprite == null || saved.Library != anim.Library.name || saved.Collection != sprite.Collection.name
                || saved.ClipIndex < 0 || saved.ClipIndex >= anim.Library.clips.Length || saved.SpriteId < 0 || saved.SpriteId >= sprite.Collection.spriteDefinitions.Length
                || saved.Enabled == null || saved.Enabled.Length != BehaviourTypes.Length || !saved.Enabled[0] || !saved.Enabled[1]
                || saved.Fields == null || saved.Fields.Length != BehaviourFields.Length
                || !Enum.IsDefined(typeof(RigidbodyType2D), saved.BodyType) || saved.Waits == null || saved.Waits.Length > 2
                || saved.LightSprite != (Light(prefab).sprite == null ? "null" : Light(prefab).sprite.name)
                || new[] { saved.Position.x, saved.Position.y, saved.Position.z, saved.Scale.x, saved.Scale.y, saved.Scale.z,
                    saved.Rotation.x, saved.Rotation.y, saved.Rotation.z, saved.Rotation.w, saved.BodyPosition.x, saved.BodyPosition.y,
                    saved.Velocity.x, saved.Velocity.y, saved.AngularVelocity, saved.Gravity, saved.Drag, saved.AngularDrag,
                    saved.LightPosition.x, saved.LightPosition.y, saved.LightPosition.z, saved.LightScale.x, saved.LightScale.y, saved.LightScale.z,
                    saved.LightRotation.x, saved.LightRotation.y, saved.LightRotation.z, saved.LightRotation.w }.Any(v => float.IsNaN(v) || float.IsInfinity(v)))
                throw new InvalidDataException("Projectile checkpoint layout or values differ.");
            ColorValue(saved.Color); ColorValue(saved.LightColor);
        }
        internal Preparation PrepareActors(ISet<tk2dSpriteAnimator> started)
        {
            if (Version != 1 || Actors == null || Actors.Length > 64) throw new InvalidDataException("Projectile preparation differs.");
            var existing = LiveActors();
            if (existing.Length > 64) throw new InvalidOperationException("Preparation projectile bound exceeded.");
            var result = new Preparation(); if (Actors.Length == 0 && existing.Length == 0) return result;
            var prefab = Prefab(); foreach (var saved in Actors) Validate(saved, prefab);
            foreach (var go in existing)
            {
                Bind(go);
                if (!ReferenceEquals(Live(Pool())[go], prefab)) throw new InvalidOperationException("Preparation projectile prefab differs.");
            }
            // These are effects of source-scene preparation at Movie zero.
            // Reuse the game's existing pool members; retire only surplus
            // preparation members through its original recycling path.
            foreach (var extra in existing.Skip(Actors.Length)) ObjectPool.Recycle(extra);
            var scheduler = new LoadingNativeSchedulerDigest();
            for (var i = 0; i < Actors.Length; i++)
            {
                var stash = (Vector2)Stash.GetValue(null);
                var go = i < existing.Length ? existing[i] : ObjectPool.Spawn(prefab, null, new Vector3(stash.x, stash.y, 0f), Quaternion.identity); Bind(go);
                // Only this preparation's known family waits are removed. No game
                // continuation is stopped and no source effect is repeated.
                var queued = scheduler.CaptureWaits(iterator => Bound(iterator, new[] { go }), 2, "Preparation projectile");
                foreach (var wait in queued)
                {
                    Owner(wait.Enumerator).StopCoroutine(wait.Enumerator);
                }
                go.GetComponent<EnemyBullet>().enabled = false; go.GetComponent<SetZ>().enabled = false;
                go.GetComponent<Rigidbody2D>().simulated = false; go.GetComponent<BoxCollider2D>().enabled = false;
                result.Actors.Add(go);
                if (started.Contains(go.GetComponent<tk2dSpriteAnimator>())) result.ObserveStarted(go.GetComponent<tk2dSpriteAnimator>());
            }
            return result;
        }
        internal sealed class Preparation
        {
            internal readonly List<GameObject> Actors = new List<GameObject>();
            private readonly HashSet<tk2dSpriteAnimator> started = new HashSet<tk2dSpriteAnimator>();
            internal bool Ready => started.Count == Actors.Count;
            internal void ObserveStarted(tk2dSpriteAnimator anim)
            {
                if (!Actors.Contains(anim.gameObject)) return;
                // Original automatic Play updates the tk2d collider in Start
                // and later animation updates. Park after that original Start;
                // C restores the animation fields and enabled flag together.
                anim.enabled = false; anim.GetComponent<BoxCollider2D>().enabled = false;
                started.Add(anim);
            }
        }
        internal Action PrepareApply(Preparation preparation)
        {
            if (preparation == null || preparation.Actors.Count != Actors.Length) throw new InvalidOperationException("Projectile preparation is incomplete.");
            if (Actors.Length == 0) return () => { };
            var prefab = Prefab(); var writes = new List<Action>();
            for (var i = 0; i < Actors.Length; i++)
            {
                var saved = Actors[i]; var go = preparation.Actors[i]; Validate(saved, prefab); Bind(go);
                var bullet = go.GetComponent<EnemyBullet>(); var z = go.GetComponent<SetZ>(); var body = go.GetComponent<Rigidbody2D>();
                if (!Live(Pool()).TryGetValue(go, out var original) || !ReferenceEquals(original, prefab) || bullet.enabled || z.enabled || body.simulated || go.GetComponent<BoxCollider2D>().enabled)
                    throw new InvalidOperationException("Projectile parked binding differs: actor=" + i + " pool=" + (original == null ? "missing" : original.name)
                        + " prefab=" + ReferenceEquals(original, prefab) + " bullet=" + bullet.enabled + " z=" + z.enabled
                        + " simulated=" + body.simulated + " collider=" + go.GetComponent<BoxCollider2D>().enabled);
                var anim = go.GetComponent<tk2dSpriteAnimator>(); var sprite = go.GetComponent<tk2dSprite>(); var light = Light(go);
                var fields = BehaviourFields.Select((f, j) => CheckpointScalarFields.PrepareApply(go.GetComponent(BehaviourTypes[j]), f, saved.Fields[j])).ToArray();
                var globals = CheckpointScalarFields.PrepareApply(null, AnimGlobals, saved.AnimationGlobals);
                writes.Add(() =>
                {
                    go.transform.position = saved.Position; go.transform.rotation = saved.Rotation; go.transform.localScale = saved.Scale;
                    body.bodyType = saved.BodyType; body.gravityScale = saved.Gravity; body.drag = saved.Drag; body.angularDrag = saved.AngularDrag;
                    body.position = saved.BodyPosition; body.velocity = saved.Velocity; body.angularVelocity = saved.AngularVelocity; body.simulated = saved.Simulated;
                    var col = go.GetComponent<BoxCollider2D>(); col.isTrigger = saved.ColliderTrigger; col.enabled = saved.ColliderEnabled;
                    sprite.SetSprite(saved.SpriteId); sprite.color = ColorValue(saved.Color); go.GetComponent<Renderer>().enabled = saved.RendererEnabled;
                    light.transform.localPosition = saved.LightPosition; light.transform.localRotation = saved.LightRotation; light.transform.localScale = saved.LightScale;
                    light.gameObject.SetActive(saved.LightActive); light.enabled = saved.LightEnabled; light.color = ColorValue(saved.LightColor);
                    for (var j = 2; j < BehaviourTypes.Length; j++) ((Behaviour)go.GetComponent(BehaviourTypes[j])).enabled = saved.Enabled[j];
                    Clip.SetValue(anim, anim.Library.clips[saved.ClipIndex]); foreach (var field in fields) field(); globals();
                });
            }
            return () => { foreach (var write in writes) write();
                CheckpointBehaviourEnable.ResumeMany(preparation.Actors.Select(go => go.GetComponent<EnemyBullet>()));
                CheckpointBehaviourEnable.ResumeMany(preparation.Actors.Select(go => go.GetComponent<SetZ>())); };
        }
        internal Action PrepareResume(Preparation preparation)
        {
            var work = new List<Tuple<MonoBehaviour, WaitData, IEnumerator>>();
            for (var i = 0; i < Actors.Length; i++)
            {
                var go = preparation.Actors[i]; var anim = go.GetComponent<tk2dSpriteAnimator>();
                foreach (var saved in Actors[i].Waits)
                {
                    var type = saved.Iterator == CollisionIterator.FullName ? CollisionIterator : saved.Iterator == ZIterator.FullName ? ZIterator
                        : throw new InvalidDataException("Projectile iterator type differs.");
                    if (saved.Repeat != 0 || saved.Callback != "UnityPlayer:8a6c70" || saved.Rate != 0 || saved.Mode != (saved.Seconds.HasValue ? 10 : 8)
                        || saved.Frame < 1 || double.IsNaN(saved.WakeTime) || double.IsInfinity(saved.WakeTime)
                        || saved.Seconds.HasValue && (saved.Seconds.Value < 0 || float.IsNaN(saved.Seconds.Value) || float.IsInfinity(saved.Seconds.Value)))
                        throw new InvalidDataException("Projectile native wait values differ.");
                    var iterator = (IEnumerator)type.GetConstructor(Instance, null, new[] { typeof(int) }, null)!.Invoke(new object[] { 0 });
                    var owner = type == CollisionIterator ? (MonoBehaviour)go.GetComponent<EnemyBullet>() : go.GetComponent<SetZ>();
                    Field(type, "<>4__this").SetValue(iterator, owner); CheckpointScalarFields.PrepareApply(iterator, Scalars(type), saved.Fields)();
                    var state = (int)Field(type, "<>1__state").GetValue(iterator);
                    if (type == ZIterator && (state != 1 || !saved.Seconds.HasValue)
                        || type == CollisionIterator && (state == 1 ? saved.Seconds.HasValue : state != 2 || !saved.Seconds.HasValue))
                        throw new InvalidDataException("Projectile iterator yield state differs.");
                    if (type == CollisionIterator)
                    {
                        if (saved.ImpactClip < 0 || saved.ImpactClip >= anim.Library.clips.Length || anim.Library.clips[saved.ImpactClip].name != "Impact")
                            throw new InvalidDataException("Projectile impact clip alias differs.");
                        ImpactClipField.SetValue(iterator, anim.Library.clips[saved.ImpactClip]);
                    }
                    Field(type, "<>2__current").SetValue(iterator, saved.Seconds.HasValue ? new WaitForSeconds(saved.Seconds.Value) : null);
                    work.Add(Tuple.Create(owner, saved, iterator));
                }
            }
            if (work.Count == 0) return () => { };
            var native = new LoadingNativeSchedulerDigest();
            var scheduling = new LoadingEngineClockProbe.CoroutineScheduler();
            return () =>
            {
                foreach (var item in work)
                {
                    var saved = item.Item2; var resume = new ResumeRoutine(item.Item3, saved.Seconds);
                    scheduling.WithTime(saved.WakeTime - (double)(saved.Seconds ?? 0f), saved.Frame - 1, saved.Mode,
                        () => item.Item1.StartCoroutine(resume));
                    var queued = native.CaptureWaits(i => ReferenceEquals(i, resume), 1, "Resumed projectile").Single();
                    if (BitConverter.DoubleToInt64Bits(queued.WakeTime) != BitConverter.DoubleToInt64Bits(saved.WakeTime) || queued.Frame != saved.Frame
                        || queued.Mode != saved.Mode || queued.Rate != saved.Rate || queued.Repeat != saved.Repeat || queued.Call != saved.Callback || queued.Flag128 != saved.Flag128)
                        throw new InvalidOperationException("Restored projectile native wait differs.");
                    Modding.Logger.Log("[HKTAS] Restored Shot Mawlek wait: iterator=" + saved.Iterator + " wake=" + BitConverter.DoubleToInt64Bits(saved.WakeTime).ToString("x16"));
                }
            };
        }
        private sealed class ResumeRoutine : IEnumerator, IDisposable
        {
            internal readonly IEnumerator Original;
            private readonly float? seconds;
            private bool started;
            private object? current;
            internal ResumeRoutine(IEnumerator original, float? seconds) { Original = original; this.seconds = seconds; }
            public object? Current => current;
            public bool MoveNext()
            {
                if (!started) { started = true; current = seconds.HasValue ? new WaitForSeconds(seconds.Value) : null; return true; }
                var moved = Original.MoveNext(); current = moved ? Original.Current : null; return moved;
            }
            public void Reset() => throw new NotSupportedException();
            public void Dispose() => (Original as IDisposable)?.Dispose();
        }
        internal static Dictionary<string, string> Observe(LoadingRigidbodyObservation physics)
        {
            var result = new Dictionary<string, string>(); var actors = LiveActors(); result["projectile.active-count"] = actors.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var states = new List<string>();
            for (var i = 0; i < actors.Length; i++)
            {
                var go = actors[i]; var values = physics.CapturePrimary(go.GetComponent<Rigidbody2D>(), go.transform.position);
                Func<float, string> bits = v => BitConverter.ToUInt32(BitConverter.GetBytes(v), 0).ToString("x8");
                var data = new { x = bits(values.X), y = bits(values.Y), vx = bits(values.Vx), vy = bits(values.Vy),
                    fields = BehaviourFields.Select((f, j) => CheckpointScalarFields.Capture(go.GetComponent(BehaviourTypes[j]), f)).ToArray(),
                    sprite = go.GetComponent<tk2dSprite>().spriteId };
                states.Add(HollowKnightTAS.Core.Cryptography.Sha256Utility.ComputeHex(
                    System.Text.Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(data))));
            }
            // Spawned dictionary free-slot order is not an actor identity. Keep
            // every complete member state and multiplicity in canonical order;
            // capture/apply and native proxy bindings retain their own indices.
            states.Sort(StringComparer.Ordinal);
            for (var i = 0; i < states.Count; i++) result["projectile.actor-" + i] = states[i];
            return result;
        }
    }
}
