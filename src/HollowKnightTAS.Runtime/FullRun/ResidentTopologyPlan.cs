using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using NativeObject = UnityEngine.Object;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowKnightTAS.Runtime.FullRun
{
    // One bounded, single-use topology transaction. Object and component names
    // come only from the source snapshot; all behavioural callbacks are owned
    // by the shared mutation scope, never by a per-object warmup sequence.
    internal sealed class ResidentTopologyPlan
    {
        private readonly ResidentObjectDirectory.Snapshot source;
        private readonly ResidentObjectDirectory target;
        private readonly Func<bool> admitted;
        private readonly ResidentMutationScope scope;
        private readonly ResidentExecutionOwnership.TopologyGuard execution;
        private readonly Dictionary<string, Type> types = new Dictionary<string, Type>(StringComparer.Ordinal);
        private readonly Dictionary<string, GameObject> paired = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private readonly HashSet<string> recreate = new HashSet<string>(StringComparer.Ordinal);
        private ResidentObjectDirectory? current;
        private bool attempted, objectStateApplied;
        internal int CreatedObjects { get; private set; }
        internal int CreatedComponents { get; private set; }
        internal int RetiredObjects { get; private set; }
        internal int RetiredComponents { get; private set; }

        private ResidentTopologyPlan(ResidentObjectDirectory.Snapshot source, ResidentObjectDirectory target,
            Func<bool> admitted, ResidentMutationScope scope)
        {
            ResidentObjectDirectory.Validate(source);
            this.source = source.Copy(); this.target = target ?? throw new ArgumentNullException(nameof(target));
            this.admitted = admitted ?? throw new ArgumentNullException(nameof(admitted));
            this.scope = scope ?? throw new ArgumentNullException(nameof(scope));
            if (!ReferenceEquals(scope.ExecutionOwnership, target.Ownership)) throw Error("topology scope is not bound to the current execution ownership directory");
            if (!admitted()) throw Error("topology preparation is outside its restore transaction");
            var targetSnapshot = target.CopySnapshot();
            RequireScenes(this.source, targetSnapshot);
            execution = ResidentExecutionOwnership.PrepareTopology(this.source, target);
            foreach (var component in this.source.Objects.SelectMany(owner => owner.Components)) {
                if (types.ContainsKey(component.Type.Key)) continue;
                var type = component.Type.Resolve();
                if (!typeof(Component).IsAssignableFrom(type)) throw Error("source component type is not a Component: " + type.FullName);
                types.Add(component.Type.Key, type);
            }
            foreach (var owner in this.source.Objects) {
                if (owner.Components.Count(component => typeof(Transform).IsAssignableFrom(types[component.Type.Key])) != 1
                    || types[owner.Components[0].Type.Key] != typeof(Transform) && types[owner.Components[0].Type.Key] != typeof(RectTransform))
                    throw Error("source GameObject has an unsupported native transform type or order: " + owner.Key);
            }
            var targetRows = targetSnapshot.Objects.ToDictionary(owner => owner.Key, StringComparer.Ordinal);
            foreach (var owner in this.source.Objects) {
                if (owner.Parent.Length != 0 && recreate.Contains(owner.Parent)) { recreate.Add(owner.Key); continue; }
                if (!targetRows.TryGetValue(owner.Key, out var actual)) continue;
                // Unity has no public component-list reorder operation. A
                // non-prefix layout is rebuilt under the same generic rules.
                var wantedCounts = owner.Components.GroupBy(component => component.Type.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                var retained = actual.Components.Where(component => wantedCounts.TryGetValue(component.Type.Key, out var count) && component.Ordinal < count).ToArray();
                if (actual.Components[0].Type.Key != owner.Components[0].Type.Key
                    || !retained.Select(component => component.Type.Key).SequenceEqual(owner.Components.Take(retained.Length).Select(component => component.Type.Key)))
                    recreate.Add(owner.Key);
            }
            execution.RejectStructuralMutation(recreate);
        }
        internal static ResidentTopologyPlan Prepare(ResidentObjectDirectory.Snapshot source, ResidentObjectDirectory target,
            Func<bool> admitted, ResidentMutationScope scope) => new ResidentTopologyPlan(source, target, admitted, scope);

        internal ResidentObjectDirectory ApplyStructure()
        {
            if (attempted) throw Error("topology application is single use");
            attempted = true; Require(); target.RequireCurrent();
            var before = target.CopySnapshot(); var sourceKeys = new HashSet<string>(source.Objects.Select(owner => owner.Key), StringComparer.Ordinal);
            var retire = new HashSet<string>(before.Objects.Where(owner => !sourceKeys.Contains(owner.Key) || recreate.Contains(owner.Key)).Select(owner => owner.Key), StringComparer.Ordinal);
            execution.RejectStructuralMutation(retire); execution.RequireCurrent(target);
            scope.Mutate(() => {
                foreach (var owner in before.Objects) scope.Track((GameObject)target.Resolve(owner.Key));
                // Park only game branches. Execution anchors and every parent
                // remain active/current; no ancestor toggle can disable IPC or
                // the driver that owns this very transaction.
                foreach (var owner in before.Objects.Where(owner => !execution.IsProtected(owner.Key)
                    && (owner.Parent.Length == 0 || execution.IsProtected(owner.Parent)))) ((GameObject)target.Resolve(owner.Key)).SetActive(false);
                foreach (var owner in before.Objects.Where(owner => retire.Contains(owner.Key) && (owner.Parent.Length == 0 || !retire.Contains(owner.Parent)))) {
                    var value = (GameObject)target.Resolve(owner.Key);
                    RetiredObjects += value.GetComponentsInChildren<Transform>(true).Length;
                    NativeObject.DestroyImmediate(value);
                }
                foreach (var row in source.Objects) {
                    Require();
                    GameObject owner;
                    if (!recreate.Contains(row.Key) && target.NativeBindings.TryGetValue(row.Key, out var existing) && ResidentObjectDirectory.IsLive(existing))
                        owner = (GameObject)existing;
                    else {
                        owner = new GameObject(row.Name, types[row.Components[0].Type.Key]);
                        scope.Track(owner); owner.SetActive(false); CreatedObjects++;
                    }
                    paired.Add(row.Key, owner);
                    if (!execution.IsProtected(row.Key)) {
                        if (row.Parent.Length != 0) owner.transform.SetParent(paired[row.Parent].transform, false);
                        else if (!target.TryGetScene(row.Scene, out var scene)) throw Error("source resident scene is unavailable after preflight");
                        else if (owner.scene != scene) MoveRoot(owner, scene, source.Scenes.Single(value => value.Key == row.Scene));
                        owner.name = row.Name; owner.SetActive(false);
                    }
                    ReconcileComponents(owner, row);
                }
                // Parents exist before children. SetSiblingIndex then restores
                // the complete source order, including duplicate pool names.
                foreach (var row in source.Objects.Where(row => row.Parent.Length != 0 && !execution.IsProtected(row.Key)))
                    paired[row.Key].transform.SetSiblingIndex(row.SiblingIndex);
                var rootGroups = source.Objects.Where(row => row.Parent.Length == 0).ToLookup(row => row.Scene, StringComparer.Ordinal);
                foreach (var scene in source.Scenes)
                    ResidentSceneRootOrder.Apply(target.SceneFor(scene.Key), rootGroups[scene.Key].OrderBy(row => row.SiblingIndex)
                        .Select(row => paired[row.Key]).ToArray(), Require);
            });
            Require(); current = ResidentObjectDirectory.Capture(target.Ownership);
            execution.RequireCurrent(current);
            ResidentObjectDirectory.RequireSame(source, current.CopySnapshot(), includeObjectState: false);
            RequirePairedIdentity(current);
            return current;
        }

        internal void ApplyObjectState()
        {
            Require();
            if (current == null || objectStateApplied) throw Error("object state has no unapplied topology plan");
            objectStateApplied = true;
            scope.Mutate(() => {
                foreach (var row in source.Objects) {
                    var owner = paired[row.Key];
                    if (!ResidentObjectDirectory.IsLive(owner)) throw Error("paired GameObject was retired before its source state");
                    if (!execution.IsProtected(row.Key)) {
                        owner.name = row.Name; owner.tag = row.Tag; owner.layer = row.Layer;
                        owner.hideFlags = (HideFlags)row.HideFlags; owner.isStatic = row.IsStatic;
                    }
                    var components = owner.GetComponents<Component>();
                    if (components.Length != row.Components.Length) throw Error("component count changed before source native metadata");
                    for (var index = 0; index < components.Length; index++)
                        if (row.Components[index].Ownership == ResidentComponentOwnership.Gameplay) components[index].hideFlags = (HideFlags)row.Components[index].HideFlags;
                }
                // Populate child activeSelf while ancestors remain parked.
                // Native registration/lifecycle and physics adapters run under
                // the same scope and verify the final complete source state.
                for (var index = source.Objects.Length - 1; index >= 0; index--) {
                    var row = source.Objects[index];
                    if (!execution.IsProtected(row.Key)) {
                        var owner = paired[row.Key];
                        if (owner.GetComponent<ParticleSystem>() is ParticleSystem ps && ps.useAutoRandomSeed) {
                            if (ps.isPlaying || ps.particleCount > 0)
                                ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                            ps.useAutoRandomSeed = false;
                        }
                        owner.SetActive(row.ActiveSelf);
                    }
                }
            });
            Verify();
        }
        internal void Verify()
        {
            Require();
            if (current == null || !objectStateApplied) throw Error("complete object-state verification precedes application");
            var observed = ResidentObjectDirectory.Capture(target.Ownership);
            execution.RequireCurrent(observed);
            ResidentObjectDirectory.RequireSame(source, observed.CopySnapshot()); RequirePairedIdentity(observed);
            if (current.NativeBindings.Count != observed.NativeBindings.Count
                || current.NativeBindings.Any(pair => !observed.NativeBindings.TryGetValue(pair.Key, out var value) || !ReferenceEquals(pair.Value, value)))
                throw Error("native component identities changed after topology alignment");
            current = observed;
        }
        internal ResidentObjectDirectory Directory => current ?? throw Error("topology has not been applied");
        private static string TypeMatchKey(Type type, string fallbackKey)
        {
            var asm = type.Assembly.GetName().Name;
            if (asm == "HollowKnightTAS" || asm == "HollowKnightTAS.Core" || asm == "HollowKnightTAS.ClockPayload")
                return "cp:" + type.FullName;
            return fallbackKey;
        }
        private static string RowTypeMatchKey(ResidentObjectDirectory.ComponentRecord component)
        {
            var asm = component.Type.Assembly;
            if (asm == "HollowKnightTAS" || asm == "HollowKnightTAS.Core" || asm == "HollowKnightTAS.ClockPayload")
                return "cp:" + component.Type.Name;
            return component.Type.Key;
        }
        private void ReconcileComponents(GameObject owner, ResidentObjectDirectory.ObjectRecord row)
        {
            var wanted = row.Components.GroupBy(RowTypeMatchKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var old = owner.GetComponents<Component>(); var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var excess = new List<Component>();
            foreach (var component in old) {
                if (!ResidentObjectDirectory.IsLive(component)) throw Error("target component list contains a missing script");
                var t = component.GetType();
                var key = TypeMatchKey(t, ResidentObjectDirectory.TypeRecord.Capture(t).Key);
                counts.TryGetValue(key, out var ordinal); counts[key] = ordinal + 1;
                if (!wanted.TryGetValue(key, out var count) || ordinal >= count) excess.Add(component);
            }
            // Delete dependants in reverse native component order. Any Unity
            // refusal remains a mismatch and cannot silently drop source state.
            for (var index = excess.Count - 1; index >= 0; index--) {
                execution.RejectComponentRetirement(excess[index]);
                if (excess[index] is Transform) throw Error("native Transform replacement escaped generic object rebuild");
                if (target.Ownership.IsExecution(excess[index])) throw Error("component reconciliation would retire current execution infrastructure");
                NativeObject.DestroyImmediate(excess[index]); RetiredComponents++;
            }
            var actual = owner.GetComponents<Component>();
            RequirePrefix(actual, row);
            while (actual.Length < row.Components.Length) {
                var expected = types[row.Components[actual.Length].Type.Key]; var previous = actual.Length;
                using (scope.Construction(owner, expected)) {
                    var component = owner.AddComponent(expected);
                    if (!ResidentObjectDirectory.IsLive(component)) throw Error("Unity did not create the source component type: " + expected.FullName);
                    scope.Track(component);
                }
                actual = owner.GetComponents<Component>();
                foreach (var component in actual) scope.Track(component);
                if (actual.Length <= previous) throw Error("component construction made no progress");
                CreatedComponents += actual.Length - previous;
                RequirePrefix(actual, row);
            }
            foreach (var component in actual) scope.Track(component);
        }
        private void RequirePrefix(Component[] actual, ResidentObjectDirectory.ObjectRecord row)
        {
            if (actual.Length > row.Components.Length) throw Error("RequireComponent produced an extra source-unmatched component: " + row.Key);
            for (var index = 0; index < actual.Length; index++) {
                var expectedType = types[row.Components[index].Type.Key];
                if (!ResidentObjectDirectory.IsLive(actual[index]) || (actual[index].GetType() != expectedType && actual[index].GetType().FullName != expectedType.FullName))
                    throw Error("Unity native component order cannot represent the captured source order: " + row.Key + "/" + index);
            }
        }
        private static void MoveRoot(GameObject owner, Scene target, ResidentObjectDirectory.SceneRecord record)
        {
            if (owner.transform.parent != null) throw Error("scene transfer requires a root GameObject");
            if (record.Unlisted) NativeObject.DontDestroyOnLoad(owner);
            else UnitySceneManager.MoveGameObjectToScene(owner, target);
            if (owner.scene != target) throw Error("native resident scene transfer did not read back: " + record.Key);
        }
        private static void RequireScenes(ResidentObjectDirectory.Snapshot source, ResidentObjectDirectory.Snapshot target)
        {
            if (source.Scenes.Length != target.Scenes.Length) throw Error("loaded resident scene directory differs before topology preparation");
            for (var index = 0; index < source.Scenes.Length; index++) {
                var first = source.Scenes[index]; var second = target.Scenes[index];
                if (first.Key != second.Key || first.Unlisted != second.Unlisted) throw Error("loaded resident scene identity differs before topology preparation");
            }
        }
        private void RequirePairedIdentity(ResidentObjectDirectory directory)
        {
            if (paired.Count != source.Objects.Length || paired.Any(pair => !ReferenceEquals(directory.Resolve(pair.Key), pair.Value)))
                throw Error("source/target GameObject pairing lost an identity");
        }
        private void Require()
        { scope.Require(); if (!admitted()) throw Error("topology transaction lost paused restore admission"); }
        private static InvalidDataException Error(string message) => new InvalidDataException("Resident topology: " + message);
    }
}
