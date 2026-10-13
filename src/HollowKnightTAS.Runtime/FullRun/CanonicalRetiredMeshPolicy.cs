using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Only original renderer-owned meshes retired by source unload/R1 qualify.
    // Observe the original OnDestroy before its fields disappear; never treat
    // an arbitrary anonymous Mesh or an unknown delayed call as disposable.
    internal sealed class CanonicalRetiredMeshPolicy : IDisposable
    {
        private readonly List<IDisposable> hooks = new List<IDisposable>();
        private readonly Dictionary<int, Tuple<Mesh, Component, string>> retired = new Dictionary<int, Tuple<Mesh, Component, string>>();
        private readonly Func<int, string?> retiringOwner;
        internal CanonicalRetiredMeshPolicy(Func<int, string?> retiringOwner)
        {
            this.retiringOwner = retiringOwner;
            Register<tk2dSprite>("mesh", "meshColliderMesh");
            Register<tk2dClippedSprite>("mesh"); Register<tk2dSlicedSprite>("mesh"); Register<tk2dTiledSprite>("mesh");
            Register<tk2dStaticSpriteBatcher>("mesh", "colliderMesh"); Register<tk2dTextMesh>("mesh");
        }
        private void Register<T>(params string[] names) where T : Component
        {
            var type = typeof(T);
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var method = type.GetMethod("OnDestroy", flags) ?? throw new MissingMethodException(type.FullName, "OnDestroy");
            var fields = names.Select(name => type.GetField(name, flags)
                ?? (type == typeof(tk2dSprite) && name == "meshColliderMesh" ? typeof(tk2dBaseSprite).GetField(name, flags) : null)
                ?? throw new MissingFieldException(type.FullName, name)).ToArray();
            if (method.GetParameters().Length != 0 || method.ReturnType != typeof(void) || fields.Any(field => field.FieldType != typeof(Mesh)))
                throw new InvalidOperationException("Original retired mesh lifecycle layout differs.");
            hooks.Add(new Hook(method, (Action<Action<T>, T>)((original, owner) => {
                var identity = retiringOwner(owner.GetInstanceID());
                var meshes = identity == null ? Array.Empty<Mesh>() : fields.Select(field => field.GetValue(owner) as Mesh)
                    .Where(mesh => !ReferenceEquals(mesh, null)).Cast<Mesh>().ToArray();
                original(owner);
                foreach (var mesh in meshes)
                {
                    var id = mesh.GetInstanceID();
                    if (retired.TryGetValue(id, out var previous) && !ReferenceEquals(previous.Item1, mesh))
                        throw new InvalidOperationException("Retired mesh instance identity was reused.");
                    retired[id] = Tuple.Create(mesh, (Component)owner, identity! + "/" + type.FullName);
                }
                if (retired.Count > 16000) throw new InvalidOperationException("Retired mesh lifecycle inventory exceeds its bound.");
            })));
        }
        internal int Flush()
        {
            var reader = new LoadingNativeSchedulerDigest();
            try
            {
                var result = reader.CancelRetiredObjectDestroys(id => {
                    if (!retired.TryGetValue(id, out var record)) return null;
                    if (record.Item2 != null) throw new InvalidOperationException("Retired mesh still has its original renderer owner.");
                    return () => {
                        if (record.Item1 != null) UnityEngine.Object.DestroyImmediate(record.Item1);
                        if (record.Item1 != null) throw new InvalidOperationException("Original retired mesh destruction did not complete.");
                    };
                });
                foreach (var id in retired.Where(pair => pair.Value.Item1 == null).Select(pair => pair.Key).ToArray()) retired.Remove(id);
                return result;
            }
            finally { reader.Release(); }
        }
        public void Dispose() { foreach (var hook in hooks) hook.Dispose(); retired.Clear(); }
    }
}
