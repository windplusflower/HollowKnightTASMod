using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using UnityEngine;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal enum ResidentComponentOwnership { Gameplay, Execution, ExecutionDependency }

    // The coordinator supplies actual type / current driver identity decisions.
    // A role is an execution identity, not a GameObject name or hierarchy rule.
    // The complete directory still contains these owners; they are external to
    // the game graph and cannot overwrite the transaction currently running.
    internal sealed class ResidentExecutionOwnership
    {
        private readonly Func<Component, string?> classifyRole;
        private readonly Dictionary<Component, string> observedRoles = new Dictionary<Component, string>(ResidentObjectDirectory.ReferenceComparer.Instance);
        private readonly Dictionary<NativeObject, string> external = new Dictionary<NativeObject, string>(ResidentObjectDirectory.ReferenceComparer.Instance);
        private readonly HashSet<GameObject> protectedObjects = new HashSet<GameObject>(ResidentObjectDirectory.ReferenceComparer.Instance);
        private readonly HashSet<GameObject> executionAnchors = new HashSet<GameObject>(ResidentObjectDirectory.ReferenceComparer.Instance);
        private readonly Dictionary<string, GameObject> protectedDependencies = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private readonly Dictionary<string, Component> protectedComponentDependencies = new Dictionary<string, Component>(StringComparer.Ordinal);
        internal ResidentExecutionOwnership(Func<Component, string?> classifyRole)
        { this.classifyRole = classifyRole ?? throw new ArgumentNullException(nameof(classifyRole)); }

        internal sealed class Entry
        {
            public string ObjectKey = "", ComponentKey = "", Role = "";
            public ResidentObjectDirectory.TypeRecord Type = new ResidentObjectDirectory.TypeRecord();
            internal Entry Copy() => new Entry { ObjectKey = ObjectKey, ComponentKey = ComponentKey, Role = Role, Type = Type.Copy() };
        }
        internal sealed class Manifest
        {
            public int Version = 2;
            public Entry[] Entries = Array.Empty<Entry>();
            public DependencyEntry[] Dependencies = Array.Empty<DependencyEntry>();
            internal Manifest Copy() => new Manifest { Version = Version, Entries = Entries.Select(value => value.Copy()).ToArray(), Dependencies = Dependencies.Select(value => value.Copy()).ToArray() };
        }
        internal sealed class DependencyEntry
        {
            public string ObjectKey = "", ComponentKey = "", Role = "";
            public ResidentObjectDirectory.TypeRecord? Type;
            public int ComponentOrder = -1;
            internal DependencyEntry Copy() => new DependencyEntry { ObjectKey = ObjectKey, ComponentKey = ComponentKey, Role = Role, Type = Type?.Copy(), ComponentOrder = ComponentOrder };
        }
        // A running native loader can live on a gameplay component. Protect its
        // actual GameObject from SetActive/retirement without moving that
        // component's fields out of the game graph. Roles are supplied from the
        // admitted coroutine/native owner, never inferred from object names.
        internal void RegisterProtectedDependency(string role, GameObject owner)
        {
            CheckRole(role);
            if (!ResidentObjectDirectory.IsLive(owner)) throw Error("execution dependency has no live native GameObject");
            if (protectedDependencies.TryGetValue(role, out var previous) && !ReferenceEquals(previous, owner))
                throw Error("execution dependency role changed native identity within its resident epoch");
            if (protectedDependencies.Count >= ResidentObjectDirectory.MaximumObjects && !protectedDependencies.ContainsKey(role))
                throw Error("execution dependency directory exceeds its bound");
            protectedDependencies[role] = owner;
        }
        internal void RegisterProtectedComponentDependency(string role, Component owner)
        {
            CheckRole(role);
            if (!ResidentObjectDirectory.IsLive(owner) || owner is Transform) throw Error("execution component dependency needs a live gameplay component");
            if (protectedComponentDependencies.TryGetValue(role, out var previous) && !ReferenceEquals(previous, owner))
                throw Error("execution component dependency role changed native identity within its resident epoch");
            RegisterProtectedDependency(role, owner.gameObject); protectedComponentDependencies[role] = owner;
        }
        internal DependencyEntry[] CaptureDependencies(IReadOnlyDictionary<string, NativeObject> bindings)
        {
            var gameObjects = bindings.Where(pair => pair.Value is GameObject).ToDictionary(pair => (GameObject)pair.Value, pair => pair.Key, ResidentObjectDirectory.ReferenceComparer.Instance);
            var components = bindings.Where(pair => pair.Value is Component).ToDictionary(pair => (Component)pair.Value, pair => pair.Key, ResidentObjectDirectory.ReferenceComparer.Instance);
            return protectedDependencies.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => {
                if (!ResidentObjectDirectory.IsLive(pair.Value) || !gameObjects.TryGetValue(pair.Value, out var key))
                    throw Error("current execution dependency died or is absent from the complete native directory");
                var entry = new DependencyEntry { Role = pair.Key, ObjectKey = key };
                if (protectedComponentDependencies.TryGetValue(pair.Key, out var component)) {
                    if (!ResidentObjectDirectory.IsLive(component) || !components.TryGetValue(component, out var componentKey)
                        || !ReferenceEquals(component.gameObject, pair.Value)) throw Error("current loader component died, disappeared or changed its native GameObject");
                    var actual = pair.Value.GetComponents<Component>(); var order = Array.FindIndex(actual, value => ReferenceEquals(value, component));
                    if (order < 0) throw Error("current loader component is absent from its actual native component order");
                    entry.ComponentKey = componentKey; entry.Type = ResidentObjectDirectory.TypeRecord.Capture(component.GetType()); entry.ComponentOrder = order;
                }
                return entry;
            }).ToArray();
        }
        internal string? Classify(Component component)
        {
            if (!ResidentObjectDirectory.IsLive(component)) throw Error("execution classification received an absent component");
            var role = classifyRole(component); if (role != null) CheckRole(role);
            if (observedRoles.TryGetValue(component, out var previous) && previous != role)
                throw Error("the current component changed execution role within its resident epoch");
            // Retaining every classified gameplay component would keep dead
            // wrappers alive across scene boundaries. Only current execution
            // anchors persist; their disappearance is an identity failure.
            if (role != null) observedRoles[component] = role;
            return role;
        }
        internal bool IsExecution(Component component) => !ReferenceEquals(component, null) && external.ContainsKey(component);
        internal bool IsProtected(GameObject owner) => !ReferenceEquals(owner, null) && protectedObjects.Contains(owner);
        internal bool IsExecutionAnchor(GameObject owner) => !ReferenceEquals(owner, null) && executionAnchors.Contains(owner);
        internal bool Owns(object value) => value is NativeObject owner && external.ContainsKey(owner);
        internal string? Bind(object value) => value is NativeObject owner && external.TryGetValue(owner, out var key) ? key : null;
        internal static bool IsExecutionBinding(string key) => key != null && key.StartsWith("resident-execution/", StringComparison.Ordinal);
        internal static string Binding(ResidentObjectDirectory.TypeRecord type, string role)
        {
            if (type == null) throw Error("execution identity has no actual type"); type.Validate(); CheckRole(role);
            return "resident-execution/" + ResidentObjectDirectory.TextKey(type.Key) + ResidentObjectDirectory.TextKey(role);
        }
        private static void CheckRole(string role)
        { if (string.IsNullOrEmpty(role) || role.Length > 1024 || role.IndexOf('\0') >= 0) throw Error("execution role identity is invalid"); }

        internal static Manifest CreateManifest(ResidentObjectDirectory.ObjectRecord[] objects)
            => new Manifest { Entries = objects.SelectMany(owner => owner.Components.Where(component => component.Ownership == ResidentComponentOwnership.Execution)
                .Select(component => new Entry { ObjectKey = owner.Key, ComponentKey = component.Key, Role = component.ExecutionRole, Type = component.Type.Copy() }))
                .OrderBy(entry => entry.ComponentKey, StringComparer.Ordinal).ToArray() };
        internal static HashSet<string> ProtectedKeys(ResidentObjectDirectory.Snapshot snapshot)
        {
            var rows = snapshot.Objects.ToDictionary(owner => owner.Key, StringComparer.Ordinal); var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var objectKey in snapshot.Execution.Entries.Select(entry => entry.ObjectKey).Concat(snapshot.Execution.Dependencies.Select(entry => entry.ObjectKey))) {
                var key = objectKey;
                while (key.Length != 0) {
                    if (!rows.TryGetValue(key, out var owner)) throw Error("execution owner or ancestor is absent from the complete directory");
                    if (!result.Add(key)) break; key = owner.Parent;
                }
            }
            return result;
        }
        internal static void ValidateManifest(ResidentObjectDirectory.Snapshot snapshot)
        {
            var manifest = snapshot.Execution;
            if (manifest == null || manifest.Version != 2 || manifest.Entries == null || manifest.Entries.Length > ResidentObjectDirectory.MaximumComponents
                || manifest.Dependencies == null || manifest.Dependencies.Length > ResidentObjectDirectory.MaximumObjects)
                throw Error("execution manifest is absent or exceeds its bound");
            var expected = CreateManifest(snapshot.Objects);
            if (manifest.Entries.Length != expected.Entries.Length) throw Error("execution manifest omits or invents a classified component");
            for (var index = 0; index < expected.Entries.Length; index++) {
                var actual = manifest.Entries[index]; var wanted = expected.Entries[index];
                if (actual == null || actual.Type == null) throw Error("execution identity row is absent");
                actual.Type.Validate(); CheckRole(actual.Role);
                if (actual.ObjectKey != wanted.ObjectKey || actual.ComponentKey != wanted.ComponentKey || actual.Role != wanted.Role
                    || actual.Type.Key != wanted.Type.Key || actual.ComponentKey != Binding(actual.Type, actual.Role))
                    throw Error("execution manifest identity differs from its actual directory component");
            }
            var objectKeys = new HashSet<string>(snapshot.Objects.Select(value => value.Key), StringComparer.Ordinal); var previousRole = "";
            foreach (var entry in manifest.Dependencies) {
                if (entry == null || !objectKeys.Contains(entry.ObjectKey)) throw Error("execution dependency object is absent from the complete directory");
                CheckRole(entry.Role);
                if (StringComparer.Ordinal.Compare(previousRole, entry.Role) >= 0) throw Error("execution dependency roles repeat or are not ordered");
                previousRole = entry.Role;
                if (entry.ComponentKey == null) throw Error("execution component dependency identity is absent");
                if (entry.ComponentKey.Length == 0) {
                    if (entry.Type != null || entry.ComponentOrder != -1) throw Error("GameObject-only dependency invents a component identity");
                } else {
                    if (entry.Type == null) throw Error("execution component dependency lacks its actual type"); entry.Type.Validate();
                    var component = snapshot.Objects.Single(value => value.Key == entry.ObjectKey).Components.SingleOrDefault(value => value.Key == entry.ComponentKey);
                    if (component == null || component.Type.Key != entry.Type.Key || component.Order != entry.ComponentOrder
                        || component.Ownership != ResidentComponentOwnership.Gameplay)
                        throw Error("execution component dependency changed actual type/order or left gameplay ownership");
                }
            }
            var protectedKeys = ProtectedKeys(snapshot);
            foreach (var owner in snapshot.Objects) foreach (var component in owner.Components) {
                var isTransform = component.Order == 0;
                if (component.Ownership == ResidentComponentOwnership.ExecutionDependency && (!protectedKeys.Contains(owner.Key) || !isTransform)
                    || protectedKeys.Contains(owner.Key) && isTransform && component.Ownership == ResidentComponentOwnership.Gameplay)
                    throw Error("execution hierarchy Transform dependency is missing or misclassified");
            }
        }
        internal void ObserveDirectory(ResidentObjectDirectory.Snapshot snapshot, IReadOnlyDictionary<string, NativeObject> bindings)
        {
            ValidateManifest(snapshot); var protectedKeys = ProtectedKeys(snapshot);
            var wantedDependencies = CaptureDependencies(bindings);
            if (snapshot.Execution.Dependencies.Length != wantedDependencies.Length || snapshot.Execution.Dependencies.Where((entry, index) =>
                !SameDependency(entry, wantedDependencies[index])).Any())
                throw Error("execution dependency manifest does not name the current local native owners");
            var next = new Dictionary<NativeObject, string>(ResidentObjectDirectory.ReferenceComparer.Instance);
            var nextProtected = new HashSet<GameObject>(ResidentObjectDirectory.ReferenceComparer.Instance);
            var nextAnchors = new HashSet<GameObject>(ResidentObjectDirectory.ReferenceComparer.Instance);
            var anchorKeys = new HashSet<string>(snapshot.Execution.Entries.Select(entry => entry.ObjectKey), StringComparer.Ordinal);
            foreach (var owner in snapshot.Objects) {
                if (protectedKeys.Contains(owner.Key)) {
                    var value = (GameObject)bindings[owner.Key]; nextProtected.Add(value);
                    // A loader's gameplay GO is only structurally protected.
                    // Its native base/event state still belongs to the world.
                    if (anchorKeys.Contains(owner.Key)) { nextAnchors.Add(value); next.Add(value, owner.Key); }
                }
                foreach (var component in owner.Components.Where(component => component.Ownership != ResidentComponentOwnership.Gameplay)) {
                    var value = (Component)bindings[component.Key];
                    if (component.Ownership == ResidentComponentOwnership.ExecutionDependency && !(value is Transform))
                        throw Error("execution hierarchy dependency is not the actual native Transform");
                    next.Add(value, component.Key);
                }
            }
            foreach (var pair in observedRoles)
                if (!ResidentObjectDirectory.IsLive(pair.Key) || !next.TryGetValue(pair.Key, out var key)
                    || key != Binding(ResidentObjectDirectory.TypeRecord.Capture(pair.Key.GetType()), pair.Value))
                    throw Error("a current execution anchor died, disappeared or changed its stable binding");
            external.Clear(); foreach (var pair in next) external.Add(pair.Key, pair.Value);
            protectedObjects.Clear(); foreach (var owner in nextProtected) protectedObjects.Add(owner);
            executionAnchors.Clear(); foreach (var owner in nextAnchors) executionAnchors.Add(owner);
        }

        internal static TopologyGuard PrepareTopology(ResidentObjectDirectory.Snapshot source, ResidentObjectDirectory target)
            => new TopologyGuard(source, target);
        internal static void RequireSameManifest(Manifest expected, Manifest actual)
        {
            if (expected.Version != actual.Version || expected.Entries.Length != actual.Entries.Length
                || expected.Entries.Where((entry, index) => entry.ComponentKey != actual.Entries[index].ComponentKey
                    || entry.ObjectKey != actual.Entries[index].ObjectKey || entry.Type.Key != actual.Entries[index].Type.Key || entry.Role != actual.Entries[index].Role).Any()
                || expected.Dependencies.Length != actual.Dependencies.Length || expected.Dependencies.Where((entry, index) =>
                    !SameDependency(entry, actual.Dependencies[index])).Any())
                throw Error("source execution identities or dependencies do not map to the current admitted process");
        }
        private static bool SameDependency(DependencyEntry left, DependencyEntry right)
            => left.ObjectKey == right.ObjectKey && left.Role == right.Role && left.ComponentKey == right.ComponentKey
                && left.ComponentOrder == right.ComponentOrder && left.Type?.Key == right.Type?.Key;
        internal sealed class TopologyGuard
        {
            private readonly ResidentObjectDirectory.Snapshot baseline;
            private readonly HashSet<string> protectedKeys;
            private readonly Dictionary<string, NativeObject> identities = new Dictionary<string, NativeObject>(StringComparer.Ordinal);
            internal IReadOnlyCollection<string> ProtectedObjectKeys { get; }
            internal TopologyGuard(ResidentObjectDirectory.Snapshot source, ResidentObjectDirectory target)
            {
                baseline = target.CopySnapshot(); ValidateManifest(source); ValidateManifest(baseline);
                RequireSameManifest(source.Execution, baseline.Execution);
                protectedKeys = ProtectedKeys(baseline);
                if (!protectedKeys.SetEquals(ProtectedKeys(source))) throw Error("execution ancestor identity differs");
                ProtectedObjectKeys = new ReadOnlyCollection<string>(protectedKeys.OrderBy(value => value, StringComparer.Ordinal).ToArray());
                var sourceObjects = source.Objects.ToDictionary(value => value.Key, StringComparer.Ordinal);
                foreach (var owner in baseline.Objects.Where(value => protectedKeys.Contains(value.Key))) {
                    RequireProtectedObject(sourceObjects[owner.Key], owner); identities.Add(owner.Key, target.Resolve(owner.Key));
                    foreach (var component in owner.Components.Where(value => value.Ownership != ResidentComponentOwnership.Gameplay))
                        identities.Add(component.Key, target.Resolve(component.Key));
                }
                foreach (var entry in baseline.Execution.Dependencies.Where(entry => entry.ComponentKey.Length != 0))
                    if (!identities.ContainsKey(entry.ComponentKey)) identities.Add(entry.ComponentKey, target.Resolve(entry.ComponentKey));
            }
            internal bool IsProtected(string objectKey) => protectedKeys.Contains(objectKey);
            internal void RejectStructuralMutation(IEnumerable<string> keys)
            { if (keys.Any(protectedKeys.Contains)) throw Error("topology would retire or recreate an execution node or ancestor"); }
            internal void RejectComponentRetirement(Component component)
            {
                if (baseline.Execution.Dependencies.Where(entry => entry.ComponentKey.Length != 0)
                    .Any(entry => ReferenceEquals(identities[entry.ComponentKey], component)))
                    throw Error("topology would retire the gameplay component hosting the current execution stack");
            }
            internal void RequireCurrent(ResidentObjectDirectory observed)
            {
                var actual = observed.CopySnapshot(); var rows = actual.Objects.ToDictionary(value => value.Key, StringComparer.Ordinal);
                RequireSameManifest(baseline.Execution, actual.Execution);
                if (!protectedKeys.SetEquals(ProtectedKeys(actual))) throw Error("execution ancestor ownership changed during topology alignment");
                foreach (var owner in baseline.Objects.Where(value => protectedKeys.Contains(value.Key))) {
                    if (!rows.TryGetValue(owner.Key, out var row)) throw Error("protected execution object disappeared");
                    RequireProtectedObject(owner, row);
                }
                if (identities.Any(pair => !ReferenceEquals(observed.Resolve(pair.Key), pair.Value))) throw Error("current execution native identity was replaced");
            }
            private static void RequireProtectedObject(ResidentObjectDirectory.ObjectRecord expected, ResidentObjectDirectory.ObjectRecord actual)
            {
                if (expected.Key != actual.Key || expected.Scene != actual.Scene || expected.Parent != actual.Parent || expected.Name != actual.Name
                    || expected.Ordinal != actual.Ordinal || expected.SiblingIndex != actual.SiblingIndex || expected.Tag != actual.Tag || expected.Layer != actual.Layer
                    || expected.HideFlags != actual.HideFlags || expected.ActiveSelf != actual.ActiveSelf || expected.IsStatic != actual.IsStatic)
                    throw Error("execution GameObject topology or state would require a write");
                var left = expected.Components.Where(value => value.Ownership != ResidentComponentOwnership.Gameplay).ToArray();
                var right = actual.Components.Where(value => value.Ownership != ResidentComponentOwnership.Gameplay).ToArray();
                if (left.Length != right.Length || left.Where((component, index) => component.Key != right[index].Key || component.Type.Key != right[index].Type.Key
                    || component.Ownership != right[index].Ownership || component.ExecutionRole != right[index].ExecutionRole || component.Ordinal != right[index].Ordinal
                    || component.Order != right[index].Order || component.HideFlags != right[index].HideFlags).Any())
                    throw Error("execution component identity, order or metadata would require a write");
            }
        }
        private static InvalidDataException Error(string reason) => new InvalidDataException("Resident execution ownership: " + reason);
    }
}
