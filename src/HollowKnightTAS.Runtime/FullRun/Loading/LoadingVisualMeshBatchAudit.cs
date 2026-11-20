using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // One bounded read of the ENTIRE outstanding B2 batch. No refresh, Mesh
    // write, D schema, equality exemption or inferred owner for an unpaired asset.
    internal static class LoadingVisualMeshBatchAudit
    {
        private static readonly FieldInfo Pointer = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)!;
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr read);

        private static byte[] Read(IntPtr address, int size)
        {
            var bytes = new byte[size];
            if (address == IntPtr.Zero || !ReadProcessMemory(GetCurrentProcess(), address, bytes, new UIntPtr((uint)size), out var read)
                || read.ToUInt64() != (ulong)size) throw new InvalidOperationException("Visual batch native read failed.");
            return bytes;
        }
        private static long MeshData(UnityEngine.Object owner, int offset)
        {
            var bytes = Read((IntPtr)Pointer.GetValue(owner)!, offset + 8);
            if (BitConverter.ToInt32(bytes, 8) != owner.GetInstanceID()) throw new InvalidOperationException("Visual batch native owner changed.");
            return BitConverter.ToInt64(bytes, offset);
        }
        private static Transform Find(Transform parent, string path)
        {
            foreach (var name in path.Split('/')) {
                var matches = parent.Cast<Transform>().Where(child => child.name == name).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("Visual batch exact owner absent or ambiguous: " + path);
                parent = matches[0];
            }
            return parent;
        }

        internal static int[] PoseBits(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            // Never hand Unity structs to Json.NET's property walker (Vector3's
            // normalized property recurses). Preserve -0 and NaN payloads too.
            return new[] { position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w,
                scale.x, scale.y, scale.z }.Select(value => BitConverter.ToInt32(BitConverter.GetBytes(value), 0)).ToArray();
        }

        internal static object Capture(Func<UnityEngine.Object, string> identity, IEnumerable<object> references)
        {
            LoadingEngineClockProbe.RequirePinnedEngine();
            var hero = HeroController.SilentInstance ?? throw new InvalidOperationException("Visual batch Hero is absent.");
            var cameras = GameCameras.instance ?? throw new InvalidOperationException("Visual batch cameras are absent.");
            var hud = Find(cameras.transform, "HudCamera");
            var nodes = new SortedDictionary<string, Transform>(StringComparer.Ordinal) {
                ["Hero"] = hero.transform,
                ["Hero/Effects/Shadow Recharge"] = Find(hero.transform, "Effects/Shadow Recharge"),
                ["HUD/Notches"] = Find(hud, "Inventory/Charms/Equipped Charms/Notches/Text Notches"),
                ["HUD/Overcharmed"] = Find(hud, "Inventory/Charms/Equipped Charms/Text Overcharmed"),
                ["HUD/First Map Label"] = Find(hud, "Prompts/First Map/Button/Label")
            };
            var referenceRows = JArray.FromObject(references.ToArray());
            var meshAliases = referenceRows.GroupBy(row => (string)row["asset"]!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => string.Join("\n", group.Select(row => (string)row["root"]! + "\t" + (string)row["path"]!)
                    .OrderBy(value => value, StringComparer.Ordinal)), StringComparer.Ordinal);
            var meshes = new Dictionary<Mesh, string>();
            string? Reference(object value)
            {
                if (!(value is UnityEngine.Object unity)) return null;
                var id = identity(unity);
                if (unity is Mesh mesh && unity != null) {
                    meshes[mesh] = id;
                    // Full observed slot sets retain alias identity without
                    // borrowing the other process's anonymous Mesh ordinal.
                    return meshAliases.TryGetValue(id, out var slots) ? "observed-Mesh-slots:" + slots : "unobserved-Mesh:" + id;
                }
                return id;
            }
            var owners = new List<object>();
            using (var reader = new LoadingFieldDigest(Reference, type => type == typeof(MonoBehaviour) || type == typeof(UnityEngine.Object))) {
                foreach (var pair in nodes) {
                    var type = pair.Key.StartsWith("Hero", StringComparison.Ordinal) ? typeof(tk2dSprite) : typeof(TextMeshPro);
                    var components = pair.Value.GetComponents(type);
                    if (components.Length != 1 || components[0].GetType() != type)
                        throw new InvalidOperationException("Visual batch original component shape differs: " + pair.Key);
                    var roots = new List<Component> { components[0] };
                    foreach (Transform child in pair.Value) {
                        var sub = child.GetComponents<TMP_SubMesh>();
                        if (sub.Length > 1) throw new InvalidOperationException("Visual batch TMP submesh owner is ambiguous.");
                        if (sub.Length == 1) roots.Add(sub[0]);
                    }
                    if (roots.Count > 9) throw new InvalidOperationException("Visual batch TMP child count exceeds its bound.");
                    foreach (var owner in roots) {
                        var id = identity(owner);
                        var full = reader.Capture(owner, id, expandRoot: true);
                        var fields = reader.Fields(owner.GetType()).Select(field => {
                            var digest = reader.CaptureFields(owner, id, field.Name);
                            return new { name = field.DeclaringType!.FullName + "." + field.Name, type = field.FieldType.FullName,
                                digest = digest.Sha256, nodes = digest.Nodes, unsupported = digest.Unsupported };
                        }).ToArray();
                        var filter = owner.GetComponent<MeshFilter>();
                        if (filter != null && filter.sharedMesh != null) Reference(filter.sharedMesh);
                        var renderer = owner.GetComponent<MeshRenderer>();
                        owners.Add(new { key = pair.Key, id, type = owner.GetType().FullName,
                            completeDigest = full.Sha256, nodes = full.Nodes, unsupported = full.Unsupported, fields,
                            enabled = ((Behaviour)owner).enabled, owner.gameObject.activeSelf, owner.gameObject.activeInHierarchy,
                            poseBits = PoseBits(owner.transform.localPosition, owner.transform.localRotation, owner.transform.localScale),
                            components = owner.GetComponents<Component>().Select(component => component.GetType().FullName).ToArray(),
                            filterMesh = filter == null || filter.sharedMesh == null ? null : identity(filter.sharedMesh),
                            rendererEnabled = renderer == null ? (bool?)null : renderer.enabled,
                            materials = renderer == null ? null : renderer.sharedMaterials.Select(material => material == null ? null : identity(material)).ToArray() });
                    }
                }
            }
            return new { diagnosticOnly = true, ownerCount = nodes.Count, owners = owners.ToArray(),
                nativeConsumers = Consumers(meshes, identity), equalityRulesUnchanged = true, restoreQualified = false };
        }

        private static object Consumers(Dictionary<Mesh, string> meshes, Func<UnityEngine.Object, string> identity)
        {
            var data = meshes.ToDictionary(pair => pair.Key, pair => MeshData(pair.Key, 56));
            if (data.Values.Any(value => value == 0)) throw new InvalidOperationException("Visual batch SharedMeshData is absent.");
            var rows = new List<object>(); var componentCount = 0; var particleSlots = 0;
            void Slot(Component owner, string slot, Mesh? mesh)
            {
                if (mesh != null && meshes.TryGetValue(mesh, out var asset)) rows.Add(new { asset, owner = identity(owner), slot });
            }
            void Visit<T>(Action<T> visit) where T : Component
            {
                var all = Resources.FindObjectsOfTypeAll<T>();
                componentCount = checked(componentCount + all.Length);
                if (componentCount > 100000 || all.Any(component => component == null)) throw new InvalidOperationException("Visual batch consumer bound or liveness differs.");
                foreach (var component in all) visit(component);
            }
            Visit<MeshFilter>(owner => Slot(owner, "sharedMesh", owner.sharedMesh));
            Visit<MeshCollider>(owner => Slot(owner, "sharedMesh", owner.sharedMesh));
            Visit<SkinnedMeshRenderer>(owner => Slot(owner, "sharedMesh", owner.sharedMesh));
            Visit<CanvasRenderer>(owner => {
                var shared = MeshData(owner, 72);
                foreach (var pair in data) if (pair.Value == shared) rows.Add(new { asset = meshes[pair.Key], owner = identity(owner), slot = "SharedMeshData" });
            });
            Visit<MeshRenderer>(owner => { Slot(owner, "additionalVertexStreams", owner.additionalVertexStreams); Slot(owner, "enlightenVertexStream", owner.enlightenVertexStream); });
            Visit<ParticleSystemRenderer>(owner => {
                var count = owner.meshCount;
                if (count < 0 || count > 100000 - particleSlots) throw new InvalidOperationException("Visual batch particle slot bound exceeded.");
                particleSlots += count;
                var values = new Mesh[count];
                if (owner.GetMeshes(values) != count || owner.meshCount != count) throw new InvalidOperationException("Visual batch particle slots changed.");
                Slot(owner, "mesh", owner.mesh);
                for (var index = 0; index < count; index++) Slot(owner, "GetMeshes[" + index + "]", values[index]);
            });
            Visit<ParticleSystem>(owner => {
                var shape = owner.shape;
                Slot(owner, "shape.mesh", shape.mesh);
                if (shape.meshRenderer != null) Slot(owner, "shape.meshRenderer", shape.meshRenderer.GetComponent<MeshFilter>()?.sharedMesh);
                if (shape.skinnedMeshRenderer != null) Slot(owner, "shape.skinnedMeshRenderer", shape.skinnedMeshRenderer.sharedMesh);
            });
            return new { componentCount, particleSlots, rows = rows.ToArray(),
                meshData = meshes.Select(pair => new { asset = pair.Value, retainedReferences = BitConverter.ToInt32(Read(new IntPtr(data[pair.Key]), 8), 4) }).ToArray(),
                includesInactiveAndUnloadedResources = true, deferredNativeConsumersNotProvenByThisInventory = true };
        }
    }
}
