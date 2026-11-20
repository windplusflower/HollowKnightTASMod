using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Only the original generated Equipped Charms folder is retired. The
    // builder reads the installed current PD; no temporary or synthetic PD.
    internal sealed class CanonicalHudCharmPolicy
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly MethodInfo Build = typeof(BuildEquippedCharms).GetMethod("BuildCharmList", Flags)!;
        private static readonly FieldInfo Player = typeof(BuildEquippedCharms).GetField("pd", Flags)!;
        private static readonly FieldInfo Manager = typeof(BuildEquippedCharms).GetField("gm", Flags)!;
        private BuildEquippedCharms? owner;
        private readonly Dictionary<int, string> retiring = new Dictionary<int, string>();
        private readonly Dictionary<int, MonoBehaviour> born = new Dictionary<int, MonoBehaviour>();
        internal string? RetiringOwner(int id) => retiring.TryGetValue(id, out var value) ? value : null;
        internal bool OwnsNewStart(int id) => born.TryGetValue(id, out var value) && value != null && owner != null
            && value.transform.IsChildOf(owner.charmsFolder.transform);
        private void Bind()
        {
            var cameras = typeof(GameCameras).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) as GameCameras
                ?? throw new InvalidOperationException("Original charm GameCameras owner is absent.");
            var inventory = cameras.hudCamera.transform.Find("Inventory/Charms")
                ?? throw new InvalidOperationException("Original Charms owner directory is absent.");
            var owners = inventory.GetComponentsInChildren<BuildEquippedCharms>(true);
            if (owners.Length != 1 || owner != null && !ReferenceEquals(owner, owners[0]))
                throw new InvalidOperationException("Original equipped charm builder owner is ambiguous or changed.");
            owner = owners[0];
            if (owner.gameObject.activeInHierarchy || owner.charmsFolder == null || owner.charmsFolder.activeInHierarchy
                || !owner.charmsFolder.transform.IsChildOf(inventory) || owner.nextDot == null
                || owner.nextDot.transform.IsChildOf(owner.charmsFolder.transform))
                throw new InvalidOperationException("Canonical equipped charm directory is not its original closed generated folder.");
            if (Build == null || Build.ReturnType != typeof(void) || Build.GetParameters().Length != 0)
                throw new InvalidOperationException("Original charm builder ABI differs.");
            // Original story recording methods are empty on this pinned HK.
            // Reject a different body rather than silently suppress a hook.
            foreach (var name in new[] { "StoryRecord_charmsChanged", "StoryRecord_charmEquipped" }) {
                var method = typeof(GameManager).GetMethod(name, Flags);
                var bytes = method?.GetMethodBody()?.GetILAsByteArray();
                if (bytes == null || bytes.Length != 1 || bytes[0] != 0x2a)
                    throw new InvalidOperationException("Original charm story method is not the registered empty body: " + name);
            }
        }
        internal IReadOnlyDictionary<int, MonoBehaviour> OldOwners()
        {
            Bind(); RequireOldDirectory();
            return owner!.charmsFolder.GetComponentsInChildren<MonoBehaviour>(true)
                .Where(item => item.transform != owner.charmsFolder.transform).ToDictionary(item => item.GetInstanceID());
        }
        private GameObject[] RequireOldDirectory()
        {
            var children = owner!.charmsFolder.transform.Cast<Transform>().Select(item => item.gameObject).ToArray();
            var instances = owner.instanceList?.Where(item => !ReferenceEquals(item, owner.nextDot)).ToArray() ?? Array.Empty<GameObject>();
            if (children.Length > 40 || children.Length != instances.Length || instances.Any(item => item == null)
                || instances.Distinct().Count() != instances.Length || children.Any(item => !instances.Any(value => ReferenceEquals(value, item))))
                throw new InvalidOperationException("Equipped charm folder has an unowned or duplicate generated child.");
            return children;
        }
        private List<int> Selected()
        {
            var selected = PlayerData.instance.GetVariable<List<int>>("equippedCharms");
            if (selected == null || selected.Count > 40 || selected.Distinct().Count() != selected.Count
                || selected.Any(id => id < 1 || owner!.gameObjectList == null || id > owner.gameObjectList.Count || owner.gameObjectList[id - 1] == null))
                throw new InvalidOperationException("Actual equipped charm PD/prefab directory differs.");
            return selected;
        }
        internal string SourceStartPrefix() { Bind(); return new BoundaryObjectIdentity().Get(owner!.charmsFolder) + "/"; }
        internal int Retire()
        {
            Bind(); Selected(); var old = RequireOldDirectory(); retiring.Clear(); born.Clear();
            foreach (var item in old) {
                foreach (var component in item.GetComponentsInChildren<Component>(true))
                    retiring.Add(component.GetInstanceID(), "canonical-equipped-charm-retirement/" + item.name + "/" + component.GetType().FullName);
                UnityEngine.Object.DestroyImmediate(item);
            }
            owner!.instanceList = new List<GameObject>(); owner.uiItems = 0;
            return old.Length;
        }
        internal int BuildCurrent()
        {
            Bind(); if (RequireOldDirectory().Length != 0) throw new InvalidOperationException("Original charm birth still has an old generated directory.");
            var selected = Selected(); var player = PlayerData.instance; var manager = GameManager.instance;
            Player.SetValue(owner, player); Manager.SetValue(owner, manager);
            Build.Invoke(owner, null);
            var created = RequireOldDirectory();
            if (!ReferenceEquals(Player.GetValue(owner), player) || !ReferenceEquals(Manager.GetValue(owner), manager)
                || !ReferenceEquals(owner!.equippedCharms, selected) || created.Length != selected.Count
                || !created.Select(item => item.name).SequenceEqual(selected.Select(id => id.ToString())))
                throw new InvalidOperationException("Original equipped charm source dependency readback differs.");
            foreach (var item in created)
                foreach (var component in item.GetComponentsInChildren<MonoBehaviour>(true)) born.Add(component.GetInstanceID(), component);
            return created.Length;
        }
        internal object Reset()
        {
            var removed = Retire(); var generated = BuildCurrent();
            return new { removedEntries = removed, generatedEntries = generated, originalBuilder = true,
                actualCurrentPlayerAlias = true, completeStateCoverage = false, charmBindingQualified = false };
        }
    }
}
