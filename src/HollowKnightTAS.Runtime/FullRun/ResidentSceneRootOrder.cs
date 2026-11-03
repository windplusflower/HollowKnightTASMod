using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Pinned PDB: UnityScene +176: List<ListNode<Transform>> sentinel;
    // Transform +152: { prev, next, Transform*, UnityScene* }. SetSiblingIndex
    // returns without writing when the Transform has no parent.
    internal static class ResidentSceneRootOrder
    {
        internal static void Apply(Scene scene, GameObject[] desired, Action require)
        {
            require();
            if (!scene.IsValid() || !scene.isLoaded) throw Error("target scene is not live");
            var before = scene.GetRootGameObjects();
            if (before.Length != desired.Length || before.Length != scene.rootCount) throw Error("target root count differs");
            if (before.Length == 0) return;
            _ = LoadingEngineClockProbe.RequirePinnedEngine();
            var transforms = before.Select(owner => {
                if (!ResidentObjectDirectory.IsLive(owner) || owner.scene.handle != scene.handle || owner.transform.parent != null)
                    throw Error("target root is destroyed, parented or foreign");
                return M.Owner(owner.transform);
            }).ToArray();
            var wanted = desired.Select(owner => M.Owner(owner.transform)).ToArray();
            Reorder(scene.handle, transforms, wanted, require);
            var actual = scene.GetRootGameObjects();
            if (actual.Length != desired.Length || actual.Where((owner, index) => !ReferenceEquals(owner, desired[index])).Any())
                throw Error("independent scene enumeration differs after restoration");
            require();
        }

        internal static void Reorder(int handle, IntPtr[] current, IntPtr[] desired, Action require)
        {
            require();
            var members = new HashSet<IntPtr>(current);
            if (current.Length == 0 || current.Length > ResidentObjectDirectory.MaximumObjects || members.Count != current.Length
                || members.Contains(IntPtr.Zero) || current.Length != desired.Length || !members.SetEquals(desired))
                throw Error("source order is not a permutation of the actual target roots");
            var scene = M.Ptr(IntPtr.Add(current[0], 176));
            if (scene == IntPtr.Zero || M.I32(M.Read(IntPtr.Add(scene, 8), 4), 0) != handle)
                throw Error("native UnityScene handle differs");
            var sentinel = IntPtr.Add(scene, 176);
            void Check(IntPtr[] order)
            {
                var node = sentinel; var prior = IntPtr.Add(order[order.Length - 1], 152);
                for (var i = 0; i <= order.Length; i++) {
                    var next = i == order.Length ? sentinel : IntPtr.Add(order[i], 152);
                    var data = M.Read(node, i == 0 ? 16 : 32);
                    if (M.Ptr(data, 0) != prior || M.Ptr(data, 8) != next
                        || i != 0 && (M.Ptr(data, 16) != order[i - 1] || M.Ptr(data, 24) != scene))
                        throw Error("native scene root links/Transform/scene back-reference differ");
                    prior = node; node = next;
                }
            }
            Check(current); require();
            if (current.SequenceEqual(desired)) return;
            // All writes refer only to prevalidated nodes in the same local
            // scene. Ownership, parent, registration and active state stay live.
            var links = new byte[16];
            for (var i = 0; i <= desired.Length; i++) {
                var node = i == 0 ? sentinel : IntPtr.Add(desired[i - 1], 152);
                var prev = i == 0 ? IntPtr.Add(desired[desired.Length - 1], 152) : i == 1 ? sentinel : IntPtr.Add(desired[i - 2], 152);
                var next = i == desired.Length ? sentinel : IntPtr.Add(desired[i], 152);
                Buffer.BlockCopy(BitConverter.GetBytes(prev.ToInt64()), 0, links, 0, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(next.ToInt64()), 0, links, 8, 8);
                M.Write(node, links);
            }
            Check(desired); require();
        }
        private static InvalidDataException Error(string message) => new InvalidDataException("Resident scene roots: " + message);
    }
}
