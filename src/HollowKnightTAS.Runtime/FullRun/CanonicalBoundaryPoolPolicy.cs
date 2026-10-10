using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // R1 clears runtime-created pools. Only the original, ordered startup
    // definitions are rebuilt, including repeated entries for the same prefab.
    internal sealed class CanonicalBoundaryPoolPolicy
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
        private static readonly FieldInfo Free = Required("pooledObjects", Instance);
        private static readonly FieldInfo Live = Required("spawnedObjects", Instance);
        private static readonly FieldInfo Created = Required("startupPoolsCreated", Instance);
        private static readonly FieldInfo Temp = Required("tempList", Static);
        private static readonly FieldInfo Destroy = Required("destroyList", Static);
        private static readonly FieldInfo Recycling = Required("isRecycling", Static);
        private static readonly FieldInfo Stash = Required("activeStashLocation", Static);
        private static FieldInfo Required(string name, BindingFlags flags) => typeof(ObjectPool).GetField(name, flags)
            ?? throw new MissingFieldException(typeof(ObjectPool).FullName, name);
        private ObjectPool? owner;
        private GameObject[] prefabs = Array.Empty<GameObject>();
        private int[] sizes = Array.Empty<int>();
        private readonly Dictionary<int, MonoBehaviour> newPoolStartOwners = new Dictionary<int, MonoBehaviour>();

        internal bool OwnsNewPoolStart(int instanceId)
        {
            if (!newPoolStartOwners.TryGetValue(instanceId, out var component) || component == null
                || !component.gameObject.activeInHierarchy || component.transform.parent != owner!.transform)
                return false;
            var lifecycle = BoundaryMonoLifecycle.Read(component);
            return lifecycle[0] == 1 && lifecycle[1] == 0 && lifecycle[3] == 0 && lifecycle[4] == 1;
        }

        internal int Reset()
        {
            newPoolStartOwners.Clear();
            var pools = Resources.FindObjectsOfTypeAll<ObjectPool>().Where(pool => pool != null
                && pool.gameObject.scene.IsValid() && pool.gameObject.scene.isLoaded).ToArray();
            if (pools.Length != 1) throw new InvalidOperationException("Canonical boundary requires one loaded global ObjectPool.");
            var pool = pools[0];
            if ((bool)Recycling.GetValue(null)) throw new InvalidOperationException("Canonical boundary interrupted pool recycling.");
            var definitions = pool.startupPools;
            if (definitions == null || definitions.Length > 1024 || definitions.Any(d => d == null || d.prefab == null || d.size < 0)
                || definitions.Sum(d => (long)d.size) > 100000)
                throw new InvalidOperationException("Canonical startup pool definitions exceed their fixed contract.");
            if (owner == null)
            {
                owner = pool;
                prefabs = definitions.Select(d => d.prefab).ToArray();
                sizes = definitions.Select(d => d.size).ToArray();
            }
            else if (owner != pool || !prefabs.SequenceEqual(definitions.Select(d => d.prefab))
                || !sizes.SequenceEqual(definitions.Select(d => d.size)))
                throw new InvalidOperationException("Canonical startup pool definitions changed after registration.");
            var free = (Dictionary<GameObject, List<GameObject>>)Free.GetValue(pool);
            var live = (Dictionary<GameObject, GameObject>)Live.GetValue(pool);
            if (free == null || live == null || free.Count > 4096 || live.Count > 100000
                || free.Values.Any(list => list == null || list.Count > 100000))
                throw new InvalidOperationException("Canonical pool inventory exceeds its bounds.");
            var actors = free.Values.SelectMany(list => list).Concat(live.Keys)
                .Concat((List<GameObject>)Destroy.GetValue(null)).Where(go => go != null).Distinct().ToArray();
            if (actors.Length > 100000 || actors.Any(go => go == pool.gameObject || prefabs.Contains(go)
                || !go.scene.IsValid() || !go.scene.isLoaded))
                throw new InvalidOperationException("Canonical pool actor ownership differs.");
            var owned = new HashSet<GameObject>(actors);
            for (var child = 0; child < pool.transform.childCount; child++)
                if (!owned.Contains(pool.transform.GetChild(child).gameObject))
                    throw new InvalidOperationException("Canonical pool contains an unregistered direct child.");
            // Reset the tables before OnDisable/OnDestroy can recycle a doomed
            // actor into them. This runs at the parked native callback, outside
            // component updates and after the source unload has completed.
            Free.SetValue(pool, new Dictionary<GameObject, List<GameObject>>());
            Live.SetValue(pool, new Dictionary<GameObject, GameObject>());
            foreach (var actor in actors) if (actor != null) UnityEngine.Object.DestroyImmediate(actor);
            if (actors.Any(go => go != null) || pool.transform.childCount != 0)
                throw new InvalidOperationException("Canonical pool destruction left an owned actor alive.");
            Temp.SetValue(null, new List<GameObject>());
            Destroy.SetValue(null, new List<GameObject>());
            Recycling.SetValue(null, false);
            Stash.SetValue(null, new Vector2(-20f, -20f));
            Created.SetValue(pool, false);
            // Pool Awake/OnEnable must start from the same RNG state at every
            // B. Carry the gameplay stream through this isolated allocation
            // scope; never reset the sequence's stream to a constant at B.
            var rng = HollowKnightTAS.Runtime.Rng.UnityRandomStateCodec_1_5_78_11833.Resolve().Codec
                ?? throw new InvalidOperationException("Canonical pool RNG codec is unavailable.");
            var rngDigest = rng.CaptureCurrent().Sha256;
            var savedRng = UnityEngine.Random.state;
            try
            {
                UnityEngine.Random.InitState(0x484b5441);
                ObjectPool.CreateStartupPools();
            }
            finally { UnityEngine.Random.state = savedRng; }
            if (rng.CaptureCurrent().Sha256 != rngDigest)
                throw new InvalidOperationException("Canonical pool allocation changed the gameplay RNG stream.");
            var expected = definitions.GroupBy(d => d.prefab).ToDictionary(group => group.Key, group => group.Sum(d => d.size));
            free = (Dictionary<GameObject, List<GameObject>>)Free.GetValue(pool);
            live = (Dictionary<GameObject, GameObject>)Live.GetValue(pool);
            if (!(bool)Created.GetValue(pool) || live.Count != 0 || free.Count != expected.Count
                || expected.Any(pair => !free.TryGetValue(pair.Key, out var list) || list.Count != pair.Value
                    || list.Any(go => go == null || go.transform.parent != pool.transform))
                || pool.transform.childCount != expected.Sum(pair => pair.Value))
                throw new InvalidOperationException("Canonical pool rebuild differs from startup definitions.");
            foreach (var pair in free)
                if ((pair.Key.name == "Slash Impact R" || pair.Key.name == "Strike Nail R")
                    && pair.Key.GetComponent<ActiveRecycler>() != null)
                    foreach (var actor in pair.Value)
                        foreach (var component in actor.GetComponents<MonoBehaviour>())
                            if (component is PlayMakerFSM || component is tk2dSpriteAnimator)
                                newPoolStartOwners.Add(component.GetInstanceID(), component);
            return actors.Length;
        }
    }
}
