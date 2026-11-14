using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.SceneManagement;
using NativeObject = UnityEngine.Object;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Paths are identities generated from the complete current tree. No path,
    // object name, component count or resource identity is an admission rule.
    internal sealed class ResidentObjectDirectory
    {
        internal const int MaximumObjects = 100000, MaximumComponents = 100000, MaximumDepth = 128;
        private const int MaximumText = 262144;
        private static readonly FieldInfo CachedPointer = typeof(NativeObject).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly Dictionary<string, NativeObject> bindings;
        private readonly Dictionary<NativeObject, string> reverse = new Dictionary<NativeObject, string>(ReferenceComparer.Instance);
        private readonly Dictionary<int, NativeObject> instances = new Dictionary<int, NativeObject>();
        private readonly Dictionary<string, Scene> scenes;
        private readonly Snapshot frozen;
        private ReadObservation? readObservation;
        internal IReadOnlyDictionary<string, NativeObject> NativeBindings { get; }
        internal IReadOnlyDictionary<string, object> ManagedRoots { get; }
        internal IReadOnlyDictionary<string, object> ExecutionRoots { get; }
        internal IReadOnlyCollection<string> ProtectedObjectKeys { get; }
        internal ResidentExecutionOwnership Ownership { get; }

        internal sealed class TypeRecord
        {
            public string Assembly = "", Module = "", Name = "";
            public int Token;
            public TypeRecord[] Arguments = Array.Empty<TypeRecord>();
            internal TypeRecord Copy() => new TypeRecord { Assembly = Assembly, Module = Module, Name = Name, Token = Token,
                Arguments = Arguments.Select(argument => argument.Copy()).ToArray() };
            internal void Validate() => ValidateTypeRecord(this, 0);
            internal string Key => TextKey(Assembly) + TextKey(Module) + Token.ToString("x8", CultureInfo.InvariantCulture) + TextKey(Name)
                + Arguments.Length.ToString(CultureInfo.InvariantCulture) + ":" + string.Concat(Arguments.Select(argument => TextKey(argument.Key)));
            internal static TypeRecord Capture(Type type)
            {
                if (type == null || type.IsArray || type.IsPointer || type.IsByRef || type.ContainsGenericParameters || type.FullName == null)
                    throw Error("native type is not a closed named runtime type");
                return new TypeRecord { Assembly = type.Assembly.GetName().Name!, Module = type.Module.ModuleVersionId.ToString("D"),
                    Token = type.MetadataToken, Name = type.FullName,
                    Arguments = type.IsGenericType ? type.GetGenericArguments().Select(Capture).ToArray() : Array.Empty<TypeRecord>() };
            }
            internal Type Resolve(bool concrete = true, ILookup<string, System.Reflection.Assembly>? loaded = null)
            {
                Validate();
                var named = loaded == null ? AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name == Assembly) : loaded[Assembly];
                var allModules = named.SelectMany(assembly => assembly.GetModules()).ToArray();
                var candidates = allModules.Where(module => module.ModuleVersionId.ToString("D") == Module).ToArray();
                if (candidates.Length == 0 && (Assembly == "HollowKnightTAS" || Assembly == "HollowKnightTAS.Core" || Assembly == "HollowKnightTAS.ClockPayload") && allModules.Length == 1)
                    candidates = allModules;
                if (candidates.Length != 1) throw Error("native type module is absent or ambiguous: " + Name);
                Type? type = null;
                var isControlPlane = Assembly == "HollowKnightTAS" || Assembly == "HollowKnightTAS.Core" || Assembly == "HollowKnightTAS.ClockPayload";
                try {
                    var candidate = candidates[0].ResolveType(Token);
                    if (!isControlPlane || candidate.FullName == Name) type = candidate;
                } catch (Exception error) when (!(error is InvalidDataException)) {
                    if (!isControlPlane) throw new InvalidDataException("Resident directory type token cannot be resolved: " + Name, error);
                }
                if (type == null && isControlPlane) {
                    type = candidates[0].Assembly.GetType(Name, false)
                        ?? candidates[0].GetTypes().FirstOrDefault(t => t.FullName == Name)
                        ?? throw Error("native concrete type cannot be found: " + Name);
                }
                if (type == null) throw Error("native concrete type identity differs: " + Name);
                try {
                    if (Arguments.Length != 0) {
                        if (!type.IsGenericTypeDefinition || type.GetGenericArguments().Length != Arguments.Length)
                            throw Error("native generic type definition differs: " + Name);
                        type = type.MakeGenericType(Arguments.Select(argument => argument.Resolve(false, loaded)).ToArray());
                    }
                } catch (Exception error) when (!(error is InvalidDataException)) { throw new InvalidDataException("Resident directory type token cannot be resolved: " + Name, error); }
                if (type.FullName != Name || type.ContainsGenericParameters || concrete && (type.IsAbstract || type.IsInterface) || type.IsArray
                    || type.IsPointer || type.IsByRef || (type.Module.ModuleVersionId.ToString("D") != Module && Assembly != "HollowKnightTAS" && Assembly != "HollowKnightTAS.Core" && Assembly != "HollowKnightTAS.ClockPayload"))
                    throw Error("native concrete type identity differs: " + Name);
                return type;
            }
        }
        internal sealed class SceneRecord
        {
            public string Key = "", Name = "", Path = "";
            public int BuildIndex, Ordinal;
            public bool Unlisted;
            internal SceneRecord Copy() => (SceneRecord)MemberwiseClone();
        }
        internal sealed class ComponentRecord
        {
            public string Key = "", ExecutionRole = "";
            public TypeRecord Type = new TypeRecord();
            public int Ordinal, Order, HideFlags;
            public ResidentComponentOwnership Ownership;
            internal ComponentRecord Copy(Func<TypeRecord,TypeRecord> copyType) => new ComponentRecord { Key = Key, Type = copyType(Type), Ordinal = Ordinal, Order = Order, HideFlags = HideFlags,
                Ownership = Ownership, ExecutionRole = ExecutionRole };
        }
        internal sealed class ObjectRecord
        {
            public string Key = "", Scene = "", Parent = "", Name = "", Tag = "";
            public int Ordinal, SiblingIndex, Layer, HideFlags;
            public bool ActiveSelf, IsStatic;
            public ComponentRecord[] Components = Array.Empty<ComponentRecord>();
            internal ObjectRecord Copy(Func<TypeRecord,TypeRecord> copyType) => new ObjectRecord { Key = Key, Scene = Scene, Parent = Parent, Name = Name, Tag = Tag,
                Ordinal = Ordinal, SiblingIndex = SiblingIndex, Layer = Layer, HideFlags = HideFlags, ActiveSelf = ActiveSelf,
                IsStatic = IsStatic, Components = Components.Select(component => component.Copy(copyType)).ToArray() };
        }
        internal sealed class Snapshot
        {
            public int Version = 1;
            public SceneRecord[] Scenes = Array.Empty<SceneRecord>();
            public ObjectRecord[] Objects = Array.Empty<ObjectRecord>();
            public ResidentExecutionOwnership.Manifest Execution = new ResidentExecutionOwnership.Manifest();
            internal Snapshot Copy()
            {
                // Keep the snapshot independently mutable, but copy a shared
                // type description once per actual type, not once per instance.
                var types = new Dictionary<TypeRecord,TypeRecord>();
                TypeRecord CopyType(TypeRecord type) {
                    if (!types.TryGetValue(type,out var copy)) types.Add(type,copy=type.Copy());
                    return copy;
                }
                return new Snapshot { Version = Version, Scenes = Scenes.Select(scene => scene.Copy()).ToArray(),
                    Objects = Objects.Select(owner => owner.Copy(CopyType)).ToArray(), Execution = Execution.Copy() };
            }
        }

        private ResidentObjectDirectory(Snapshot snapshot, Dictionary<string, NativeObject> owners, Dictionary<string, Scene> sceneOwners, ResidentExecutionOwnership ownership)
        {
            Validate(snapshot);
            frozen = snapshot.Copy(); bindings = owners; scenes = sceneOwners; Ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
            foreach (var pair in bindings) {
                if (!IsLive(pair.Value) || reverse.ContainsKey(pair.Value)) throw Error("native owner is dead or bound more than once");
                reverse.Add(pair.Value, pair.Key);
                if (instances.ContainsKey(pair.Value.GetInstanceID())) throw Error("native instance has multiple managed wrappers in the directory");
                instances.Add(pair.Value.GetInstanceID(), pair.Value);
            }
            NativeBindings = new ReadOnlyDictionary<string, NativeObject>(bindings);
            ManagedRoots = new ReadOnlyDictionary<string, object>(frozen.Objects.SelectMany(owner => owner.Components).Where(component => component.Ownership == ResidentComponentOwnership.Gameplay)
                .ToDictionary(component => component.Key, component => (object)bindings[component.Key], StringComparer.Ordinal));
            ExecutionRoots = new ReadOnlyDictionary<string, object>(frozen.Objects.SelectMany(owner => owner.Components).Where(component => component.Ownership != ResidentComponentOwnership.Gameplay)
                .ToDictionary(component => component.Key, component => (object)bindings[component.Key], StringComparer.Ordinal));
            ProtectedObjectKeys = new ReadOnlyCollection<string>(ResidentExecutionOwnership.ProtectedKeys(frozen).OrderBy(value => value, StringComparer.Ordinal).ToArray());
            Ownership.ObserveDirectory(frozen, NativeBindings);
        }
        internal Snapshot CopySnapshot() => frozen.Copy();
        internal Snapshot CurrentSnapshot => frozen;
        internal string StructureFingerprint => $"{frozen.Objects.Length}:{NativeBindings.Count}:{(frozen.Objects.Length > 0 ? frozen.Objects[0].Key : "")}:{(frozen.Objects.Length > 0 ? frozen.Objects[frozen.Objects.Length - 1].Key : "")}";
        internal Scene SceneFor(string key) => scenes.TryGetValue(key, out var scene) ? scene : throw Error("resident scene is absent: " + key);
        internal bool TryGetScene(string key, out Scene scene) => scenes.TryGetValue(key, out scene);
        internal NativeObject Resolve(string key) => bindings.TryGetValue(key, out var owner) && IsLive(owner)
            ? owner : throw Error("resident native binding is absent or destroyed: " + key);
        internal string? Bind(object value)
        {
            if (!(value is NativeObject owner) || !IsLive(owner)) return null;
            if (reverse.TryGetValue(owner, out var key)) return key;
            // Never collapse two managed wrappers merely because Unity equality
            // says that they designate the same native instance.
            var instance = owner.GetInstanceID();
            if (instances.ContainsKey(instance))
                throw Error("a live native instance has a second managed wrapper");
            return null;
        }
        internal static bool IsLive(NativeObject? value) => !ReferenceEquals(value, null) && (IntPtr)CachedPointer.GetValue(value)! != IntPtr.Zero;

        internal static ResidentObjectDirectory Capture(ResidentExecutionOwnership ownership)
        {
            if (ownership == null) throw new ArgumentNullException(nameof(ownership));
            var nativeScenes = ResidentSceneMembership.LoadedScenes();
            var listed = new HashSet<int>();
            for (var index = 0; index < UnitySceneManager.sceneCount; index++)
                listed.Add(UnitySceneManager.GetSceneAt(index).handle);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var sceneRows = new List<SceneRecord>(); var sceneOwners = new Dictionary<string, Scene>(StringComparer.Ordinal);
            foreach (var scene in nativeScenes) {
                var identity = TextKey(scene.path) + TextKey(scene.name) + scene.buildIndex.ToString(CultureInfo.InvariantCulture);
                counts.TryGetValue(identity, out var ordinal); counts[identity] = ordinal + 1;
                if (!listed.Contains(scene.handle) && ordinal != 0) throw Error("unlisted resident scene identity is ambiguous");
                var row = new SceneRecord { Name = scene.name, Path = scene.path, BuildIndex = scene.buildIndex, Ordinal = ordinal,
                    Unlisted = !listed.Contains(scene.handle), Key = SceneKey(scene.path, scene.name, scene.buildIndex, ordinal) };
                sceneRows.Add(row); sceneOwners.Add(row.Key, scene);
            }
            var rows = new List<ObjectRecord>(); var owners = new Dictionary<string, NativeObject>(StringComparer.Ordinal);
            var visited = new Dictionary<int,int>(); var typeRecords = new Dictionary<Type, TypeRecord>(); var componentTotal = 0;
            void Visit(GameObject owner, string sceneKey, string parentKey, int ordinal, int sibling, int depth)
            {
                if (depth > MaximumDepth || rows.Count >= MaximumObjects || !IsLive(owner) || visited.ContainsKey(owner.GetInstanceID()))
                    throw Error("resident hierarchy is cyclic, destroyed or exceeds its bound");
                visited.Add(owner.GetInstanceID(),owner.scene.handle);
                var key = ObjectKey(sceneKey, parentKey, owner.name, ordinal);
                var components = owner.GetComponents<Component>();
                if (components.Length == 0 || components.Any(component => !IsLive(component)) || componentTotal + components.Length > MaximumComponents)
                    throw Error("resident component directory is missing a script or exceeds its bound: " + key);
                componentTotal += components.Length;
                var componentCounts = new Dictionary<Type, int>(); var componentRows = new ComponentRecord[components.Length];
                for (var index = 0; index < components.Length; index++) {
                    var component = components[index]; var type = component.GetType();
                    if (!typeRecords.TryGetValue(type, out var typeRecord)) typeRecords.Add(type, typeRecord = TypeRecord.Capture(type));
                    componentCounts.TryGetValue(type, out var componentOrdinal); componentCounts[type] = componentOrdinal + 1;
                    var executionRole = ownership.Classify(component);
                    var componentKey = executionRole == null ? ComponentKey(key, typeRecord, componentOrdinal) : ResidentExecutionOwnership.Binding(typeRecord, executionRole);
                    componentRows[index] = new ComponentRecord { Key = componentKey, Type = typeRecord, Ordinal = componentOrdinal,
                        Order = index, HideFlags = (int)component.hideFlags, ExecutionRole = executionRole ?? "",
                        Ownership = executionRole == null ? ResidentComponentOwnership.Gameplay : ResidentComponentOwnership.Execution };
                    owners.Add(componentKey, component);
                }
                var row = new ObjectRecord { Key = key, Scene = sceneKey, Parent = parentKey, Name = owner.name, Ordinal = ordinal,
                    SiblingIndex = sibling, Tag = owner.tag, Layer = owner.layer, HideFlags = (int)owner.hideFlags,
                    IsStatic = owner.isStatic, ActiveSelf = owner.activeSelf, Components = componentRows };
                rows.Add(row); owners.Add(key, owner);
                var childCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var index = 0; index < owner.transform.childCount; index++) {
                    var child = owner.transform.GetChild(index).gameObject;
                    childCounts.TryGetValue(child.name, out var childOrdinal); childCounts[child.name] = childOrdinal + 1;
                    Visit(child, sceneKey, key, childOrdinal, index, depth + 1);
                }
            }
            foreach (var row in sceneRows.OrderBy(scene => scene.Key, StringComparer.Ordinal)) {
                var rootCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                // Transform::GetSiblingIndex returns zero for every parentless
                // Transform in this player. Scene roots instead occupy positions
                // in UnityScene::m_Roots, exactly the order returned by this API.
                var roots = sceneOwners[row.Key].GetRootGameObjects();
                for (var index = 0; index < roots.Length; index++) {
                    var root = roots[index];
                    if (root.scene.handle != sceneOwners[row.Key].handle || root.transform.parent != null)
                        throw Error("scene root iterator returned a foreign or parented object");
                    rootCounts.TryGetValue(root.name, out var ordinal); rootCounts[root.name] = ordinal + 1;
                    Visit(root, row.Key, "", ordinal, index, 0);
                }
            }
            if (!nativeScenes.SequenceEqual(ResidentSceneMembership.LoadedScenes())) throw Error("loaded scenes changed during directory capture");
            var snapshot = new Snapshot { Scenes = sceneRows.OrderBy(scene => scene.Key, StringComparer.Ordinal).ToArray(), Objects = rows.ToArray() };
            snapshot.Execution = ResidentExecutionOwnership.CreateManifest(snapshot.Objects);
            snapshot.Execution.Dependencies = ownership.CaptureDependencies(owners);
            var protectedKeys = ResidentExecutionOwnership.ProtectedKeys(snapshot);
            foreach (var owner in snapshot.Objects.Where(owner => protectedKeys.Contains(owner.Key)))
                foreach (var component in owner.Components.Where(component => component.Ownership == ResidentComponentOwnership.Gameplay))
                    if (owners[component.Key] is Transform) component.Ownership = ResidentComponentOwnership.ExecutionDependency;
            return new ResidentObjectDirectory(snapshot, owners, sceneOwners, ownership);
        }

        internal void RequireCurrent()
        {
            if (readObservation != null) { readObservation.RequireThread(); return; }
            RequireCurrentCore();
        }
        // An observation performs only queries on the parked game thread. Validate
        // its complete directory at both ends, rather than rescan the same tree for
        // every adapter's unchanged-owner assertion. Mutation/restore uses fresh scopes.
        internal IDisposable BeginReadObservation() => new ReadObservation(this);
        private sealed class ReadObservation : IDisposable
        {
            private readonly ResidentObjectDirectory directory;
            private readonly int thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            private int checks;
            internal ReadObservation(ResidentObjectDirectory directory)
            {
                if (directory.readObservation != null) throw Error("directory observation is nested");
                this.directory = directory; directory.RequireCurrentCore(); directory.readObservation = this;
            }
            internal void RequireThread()
            {
                if (System.Threading.Thread.CurrentThread.ManagedThreadId != thread || !ReferenceEquals(directory.readObservation, this))
                    throw Error("directory observation changed thread or scope");
                checks++;
            }
            public void Dispose()
            {
                RequireThread(); directory.readObservation = null;
                directory.RequireCurrentCore();
                ResidentFailureEvidence.Mark("read-only directory guards coalesced=" + checks);
            }
        }
        private void RequireCurrentCore()
        {
            // Compare the live tree directly against the frozen pre-order. Building
            // another directory here used to regenerate every key and type manifest
            // for each unchanged-owner guard in the component adapters.
            var nativeScenes = ResidentSceneMembership.LoadedScenes();
            if (nativeScenes.Length != scenes.Count) throw Error("resident scene count changed");
            var ordinals = new Dictionary<(string, string, int), int>();
            foreach (var scene in nativeScenes) {
                var identity = (scene.path, scene.name, scene.buildIndex);
                ordinals.TryGetValue(identity, out var ordinal);
                var key = SceneKey(scene.path, scene.name, scene.buildIndex, ordinal);
                if (!scenes.TryGetValue(key, out var original) || original != scene) throw Error("resident scene identity or order changed");
                ordinals[identity] = ordinal + 1;
            }
            var next = 0;
            void Visit(GameObject owner, string parent, int sibling, string scene)
            {
                if (next >= frozen.Objects.Length) throw Error("resident object added");
                var row = frozen.Objects[next++];
                if (!ReferenceEquals(owner, bindings[row.Key]) || row.Parent != parent || row.SiblingIndex != sibling ||
                    row.Scene != scene || owner.name != row.Name || owner.tag != row.Tag || owner.layer != row.Layer ||
                    (int)owner.hideFlags != row.HideFlags || owner.activeSelf != row.ActiveSelf || owner.isStatic != row.IsStatic)
                    throw Error("resident object changed: " + row.Key);
                var components = owner.GetComponents<Component>();
                if (components.Length != row.Components.Length) throw Error("resident component count changed: " + row.Key);
                for (var i = 0; i < components.Length; i++) {
                    var record = row.Components[i]; var component = components[i];
                    if (!ReferenceEquals(component, bindings[record.Key]) || (int)component.hideFlags != record.HideFlags ||
                        (Ownership.Classify(component) ?? "") != record.ExecutionRole)
                        throw Error("resident component changed: " + record.Key);
                }
                var transform = owner.transform;
                for (var i = 0; i < transform.childCount; i++)
                    Visit(transform.GetChild(i).gameObject, row.Key, i, scene);
            }
            foreach (var record in frozen.Scenes) {
                var scene = scenes[record.Key];
                var roots = scene.GetRootGameObjects();
                for (var i = 0; i < roots.Length; i++) Visit(roots[i], "", i, record.Key);
            }
            if (next != frozen.Objects.Length) throw Error("resident object removed");
            ResidentExecutionOwnership.RequireSameManifest(frozen.Execution, new ResidentExecutionOwnership.Manifest {
                Entries = frozen.Execution.Entries, Dependencies = Ownership.CaptureDependencies(bindings)
            });
        }
        private static bool SameComponentType(TypeRecord left, TypeRecord right)
        {
            if (left == right) return true;
            if (left == null || right == null) return false;
            if (left.Assembly == right.Assembly && (left.Assembly == "HollowKnightTAS" || left.Assembly == "HollowKnightTAS.Core" || left.Assembly == "HollowKnightTAS.ClockPayload"))
                return left.Name == right.Name && left.Arguments.Length == right.Arguments.Length;
            return left.Key == right.Key;
        }
        private static bool SameComponentIdentity(string left, string right)
        {
            if (left == right) return true;
            if (left != null && right != null && left.Contains("HollowKnightTAS") && right.Contains("HollowKnightTAS")) {
                var l = NormalizeComponentKey(left);
                var r = NormalizeComponentKey(right);
                return l == r;
            }
            return false;
        }
        private static string NormalizeComponentKey(string key)
        {
            var s = System.Text.RegularExpressions.Regex.Replace(key, @"runtime-driver/[0-9a-fA-F\-]+(?:/[0-9a-fA-F]+)?", "runtime-driver/*");
            return System.Text.RegularExpressions.Regex.Replace(s, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}(?:[0-9a-fA-F]{8})?", "*");
        }
        private static bool SameRole(string left, string right)
        {
            if (left == right) return true;
            if (left != null && right != null && left.StartsWith("runtime-driver/", StringComparison.Ordinal) && right.StartsWith("runtime-driver/", StringComparison.Ordinal)) {
                return true;
            }
            return false;
        }

        internal static void RequireSame(Snapshot expected, Snapshot actual, bool includeObjectState = true)
        {
            Validate(expected); Validate(actual);
            ResidentExecutionOwnership.RequireSameManifest(expected.Execution, actual.Execution);
            if (expected.Scenes.Length != actual.Scenes.Length || expected.Objects.Length != actual.Objects.Length) throw Error("resident topology count differs");
            for (var index = 0; index < expected.Scenes.Length; index++) {
                var first = expected.Scenes[index]; var second = actual.Scenes[index];
                if (first.Key != second.Key || first.Name != second.Name || first.Path != second.Path || first.Ordinal != second.Ordinal
                    || first.BuildIndex != second.BuildIndex || first.Unlisted != second.Unlisted) throw Error("resident scene identity differs");
            }
            for (var index = 0; index < expected.Objects.Length; index++) {
                var first = expected.Objects[index]; var second = actual.Objects[index];
                if (first.Key != second.Key || first.Scene != second.Scene || first.Parent != second.Parent || first.Name != second.Name
                    || first.Ordinal != second.Ordinal || first.SiblingIndex != second.SiblingIndex || first.Components.Length != second.Components.Length)
                    throw Error("resident object topology differs: " + first.Key);
                if (includeObjectState && (first.Tag != second.Tag || first.Layer != second.Layer || first.HideFlags != second.HideFlags
                    || first.ActiveSelf != second.ActiveSelf || first.IsStatic != second.IsStatic)) throw Error("resident GameObject state differs: " + first.Key);
                for (var component = 0; component < first.Components.Length; component++) {
                    var left = first.Components[component]; var right = second.Components[component];
                    if (!SameComponentIdentity(left.Key, right.Key) || !SameComponentType(left.Type, right.Type) || left.Ordinal != right.Ordinal || left.Order != right.Order
                        || left.Ownership != right.Ownership || !SameRole(left.ExecutionRole, right.ExecutionRole)
                        || includeObjectState && left.HideFlags != right.HideFlags) throw Error("resident component topology/state differs: " + left.Key);
                }
            }
        }
        internal static void Validate(Snapshot snapshot)
        {
            if (snapshot == null || snapshot.Version != 1 || snapshot.Scenes == null || snapshot.Objects == null
                || snapshot.Scenes.Length > 256 || snapshot.Objects.Length > MaximumObjects) throw Error("resident manifest header differs");
            var scenes = new Dictionary<string, SceneRecord>(StringComparer.Ordinal);
            foreach (var scene in snapshot.Scenes) {
                if (scene == null) throw Error("resident scene row is absent");
                CheckText(scene.Name, true); CheckText(scene.Path, true); CheckText(scene.Key);
                if (scene.Ordinal < 0 || scene.BuildIndex < -1 || scene.Key != SceneKey(scene.Path, scene.Name, scene.BuildIndex, scene.Ordinal)
                    || scenes.ContainsKey(scene.Key)) throw Error("resident scene row identity differs");
                scenes.Add(scene.Key, scene);
            }
            if (!snapshot.Scenes.Select(scene => scene.Key).SequenceEqual(snapshot.Scenes.Select(scene => scene.Key).OrderBy(key => key, StringComparer.Ordinal)))
                throw Error("resident scene rows are not canonical");
            foreach (var group in snapshot.Scenes.GroupBy(scene => TextKey(scene.Path) + TextKey(scene.Name) + scene.BuildIndex.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal))
                if (!group.Select(scene => scene.Ordinal).OrderBy(value => value).SequenceEqual(Enumerable.Range(0, group.Count()))
                    || group.Any(scene => scene.Unlisted) && group.Count() != 1) throw Error("resident scene ordinals are incomplete or ambiguous");
            var objects = new Dictionary<string, ObjectRecord>(StringComparer.Ordinal); var depths = new Dictionary<string, int>(StringComparer.Ordinal);
            var allKeys = new HashSet<string>(StringComparer.Ordinal); var componentTotal = 0;
            var knownTypes = new Dictionary<TypeRecord,string>();
            foreach (var owner in snapshot.Objects) {
                if (owner == null || owner.Components == null || owner.Components.Length == 0) throw Error("resident object/components row is absent");
                CheckText(owner.Key); CheckText(owner.Scene); CheckText(owner.Parent, true); CheckText(owner.Name, true); CheckText(owner.Tag);
                if (!scenes.ContainsKey(owner.Scene) || owner.Ordinal < 0 || owner.SiblingIndex < 0 || owner.Layer < 0 || owner.Layer > 31
                    || owner.Key != ObjectKey(owner.Scene, owner.Parent, owner.Name, owner.Ordinal) || !allKeys.Add(owner.Key))
                    throw Error("resident object row identity differs");
                var depth = 0;
                if (owner.Parent.Length != 0) {
                    if (!objects.TryGetValue(owner.Parent, out var parent) || parent.Scene != owner.Scene) throw Error("resident parent is absent, unordered or in another scene");
                    depth = depths[owner.Parent] + 1;
                }
                if (depth > MaximumDepth) throw Error("resident manifest hierarchy exceeds its depth bound");
                objects.Add(owner.Key, owner); depths.Add(owner.Key, depth);
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var index = 0; index < owner.Components.Length; index++) {
                    var component = owner.Components[index];
                    if (component == null || ++componentTotal > MaximumComponents) throw Error("resident component row is absent or exceeds its bound");
                    if (component.Type == null) throw Error("component type is absent");
                    if (!knownTypes.TryGetValue(component.Type,out var typeKey)) {
                        ValidateTypeRecord(component.Type, 0); knownTypes.Add(component.Type,typeKey=component.Type.Key);
                    }
                    CheckText(component.Key); CheckText(component.ExecutionRole, true);
                    counts.TryGetValue(typeKey, out var ordinal); counts[typeKey] = ordinal + 1;
                    if (!Enum.IsDefined(typeof(ResidentComponentOwnership), component.Ownership)
                        || component.Ownership != ResidentComponentOwnership.Execution && component.ExecutionRole.Length != 0)
                        throw Error("resident component execution ownership differs");
                    var expectedKey = component.Ownership == ResidentComponentOwnership.Execution
                        ? ResidentExecutionOwnership.Binding(component.Type, component.ExecutionRole) : ComponentKeyFromTypeKey(owner.Key, typeKey, ordinal);
                    if (component.Ordinal != ordinal || component.Order != index || component.Key != expectedKey
                        || !allKeys.Add(component.Key)) throw Error("resident exact-type component ordinal differs");
                }
            }
            foreach (var siblings in snapshot.Objects.GroupBy(owner => owner.Scene + "\0" + owner.Parent, StringComparer.Ordinal)) {
                var counts = new Dictionary<string, int>(StringComparer.Ordinal); var index = 0;
                foreach (var owner in siblings.OrderBy(owner => owner.SiblingIndex)) {
                    counts.TryGetValue(owner.Name, out var ordinal); counts[owner.Name] = ordinal + 1;
                    if (owner.SiblingIndex != index++ || owner.Ordinal != ordinal) throw Error("resident sibling order/name ordinal differs");
                }
            }
            // The payload has one canonical pre-order; an array reorder cannot
            // silently change allocation or dynamic-asset discovery order.
            var ordered = new List<string>();
            var children = snapshot.Objects.GroupBy(row => row.Scene + "\0" + row.Parent, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.OrderBy(row => row.SiblingIndex).ToArray(), StringComparer.Ordinal);
            void AddChildren(string scene, string parent) {
                if (!children.TryGetValue(scene + "\0" + parent, out var rows)) return;
                foreach (var owner in rows) {
                    ordered.Add(owner.Key); AddChildren(scene, owner.Key);
                }
            }
            foreach (var scene in snapshot.Scenes) AddChildren(scene.Key, "");
            if (!ordered.SequenceEqual(snapshot.Objects.Select(owner => owner.Key))) throw Error("resident object rows are not canonical pre-order");
            ResidentExecutionOwnership.ValidateManifest(snapshot);
        }
        private static void ValidateTypeRecord(TypeRecord type, int depth)
        {
            if (type == null || type.Arguments == null || type.Arguments.Length > 32 || depth > 32) throw Error("native type manifest is absent or recursive");
            CheckText(type.Assembly); CheckText(type.Name); CheckText(type.Module);
            if (!Guid.TryParseExact(type.Module, "D", out _) || (type.Token & unchecked((int)0xff000000)) != 0x02000000)
                throw Error("native type module/token manifest differs");
            foreach (var argument in type.Arguments) ValidateTypeRecord(argument, depth + 1);
        }
        internal static string SceneKey(string path, string name, int buildIndex, int ordinal)
            => "resident/scene/" + TextKey(path) + TextKey(name) + buildIndex.ToString(CultureInfo.InvariantCulture) + "/" + ordinal.ToString(CultureInfo.InvariantCulture);
        internal static string ObjectKey(string scene, string parent, string name, int ordinal)
            => (parent.Length == 0 ? scene : parent) + "/go/" + TextKey(name) + ordinal.ToString(CultureInfo.InvariantCulture);
        internal static string ComponentKey(string owner, TypeRecord type, int ordinal)
            => ComponentKeyFromTypeKey(owner,type.Key,ordinal);
        internal static string ComponentKeyFromTypeKey(string owner, string typeKey, int ordinal)
            => owner + "/component/" + TextKey(typeKey) + ordinal.ToString(CultureInfo.InvariantCulture);
        internal static string TextKey(string value) => value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;
        private static void CheckText(string value, bool empty = false)
        { if (value == null || !empty && value.Length == 0 || value.Length > MaximumText || value.IndexOf('\0') >= 0) throw Error("resident identity text is invalid"); }
        private static InvalidDataException Error(string message) => new InvalidDataException("Resident directory: " + message);
        internal sealed class ReferenceComparer : IEqualityComparer<NativeObject>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(NativeObject? first, NativeObject? second) => ReferenceEquals(first, second);
            public int GetHashCode(NativeObject value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
