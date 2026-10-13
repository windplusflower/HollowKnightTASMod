using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed partial class CheckpointPhysicsBroadphaseState
    {
        internal static int CanonicalizePolygonCapacity()
        {
            LoadingSerialSceneProbe.RequireCanonicalMutationBoundary();
            using var validation = new LoadingEngineClockProbe.CheckpointValidation();
            var module = LoadingEngineClockProbe.RequirePinnedEngine();
            var colliders = Resources.FindObjectsOfTypeAll<Collider2D>().Where(collider => collider != null
                && collider.gameObject.scene.IsValid() && collider.gameObject.scene.isLoaded).ToArray();
            if (colliders.Length > 100000) throw new InvalidOperationException("Canonical polygon collider budget exceeded.");
            var targets = new Dictionary<IntPtr, Tuple<byte[], byte[]>>();
            var owners = new List<Tuple<IntPtr, byte[]>>();
            foreach (var collider in colliders)
            {
                var pointer = Native(collider); if (pointer == IntPtr.Zero) continue;
                var owner = Read(pointer, 224); owners.Add(Tuple.Create(pointer, owner));
                var count = Bound(BitConverter.ToInt64(owner, 104), 4096, "polygon fixture count");
                var fixtures = count == 0 ? Array.Empty<byte>() : Read(Pointer(owner, 88), count * 8);
                for (var i=0; i<count; i++)
                {
                    var fixture = Pointer(fixtures, i*8); var binding = Read(fixture, 88);
                    owners.Add(Tuple.Create(fixture, binding));
                    var shape = Pointer(binding, 40); var header = Read(shape, 16);
                    if (Int(header, 8) != 2 || targets.ContainsKey(shape)) continue;
                    var table = Pointer(header, 0).ToInt64();
                    if (table < module.BaseAddress.ToInt64() || table >= module.BaseAddress.ToInt64() + module.ModuleMemorySize)
                        throw new InvalidOperationException("Canonical polygon vtable is outside the pinned engine.");
                    var before = Read(shape, 160);
                    targets.Add(shape, Tuple.Create(before, CanonicalPolygonCapacity.Prepare(before)));
                    if (targets.Count > 100000) throw new InvalidOperationException("Canonical polygon shape budget exceeded.");
                }
            }
            if (owners.Any(item => !Read(item.Item1, item.Item2.Length).SequenceEqual(item.Item2))
                || targets.Any(item => !Read(item.Key, 160).SequenceEqual(item.Value.Item1)))
                throw new InvalidOperationException("Canonical polygon preparation became stale.");
            foreach (var target in targets)
            {
                var count = Int(target.Value.Item1, 152); var length = (8-count)*8;
                if (length != 0)
                {
                    Write(IntPtr.Add(target.Key, 24 + count*8), new byte[length]);
                    Write(IntPtr.Add(target.Key, 88 + count*8), new byte[length]);
                }
            }
            if (targets.Any(item => !Read(item.Key, 160).SequenceEqual(item.Value.Item2))
                || owners.Any(item => !Read(item.Item1, item.Item2.Length).SequenceEqual(item.Item2)))
                throw new InvalidOperationException("Canonical polygon full readback changed geometry or fixture binding.");
            return targets.Count;
        }
    }
}
