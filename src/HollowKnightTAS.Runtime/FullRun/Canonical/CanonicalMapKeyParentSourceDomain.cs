using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Actual dependency D, never a replay of Control actions or inferred pins.
    // Preserve all three real FadeGroup array containers and carry every value.
    internal sealed class CanonicalMapKeyParentSourceDomain
    {
        internal const string Root = "MapKeyParents/Source", FsmRoot = "MapKeyParents/ControlFsm";
        private const string MainNode = "MapKeyParents/MainNode/", Owner = "MapKeyParents/Owner/", Leaf = "MapKeyParents/Leaf/";
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo Registrations = typeof(PlayMakerFSM).GetField("fsmList", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly CanonicalCarryGraphCodec codec = CanonicalCarryFieldRegistry.Create(mapKeyParentDomain: true);
        internal static readonly Type[] DataTypes = { typeof(Data), typeof(object[]), typeof(object[][]), typeof(string[][]), typeof(Vector3[]), typeof(Quaternion[]),
            typeof(TMPro.TextMeshPro[]), typeof(SpriteRenderer[]), typeof(InvAnimateUpAndDown[]) };
        internal sealed class Data
        {
            public string Schema = "";
            public string[] ParentNames = Array.Empty<string>(), FieldNames = Array.Empty<string>();
            public bool[] ActiveSelf = Array.Empty<bool>(), HasChanged = Array.Empty<bool>(), AncestorSelf = Array.Empty<bool>(), AncestorHierarchy = Array.Empty<bool>();
            public Vector3[] Positions = Array.Empty<Vector3>(), Scales = Array.Empty<Vector3>();
            public Quaternion[] Rotations = Array.Empty<Quaternion>();
            public byte[] Native = Array.Empty<byte>();
            public bool Registered;
            public object?[][] Fields = Array.Empty<object?[]>();
            public object[] ArraySnapshots = Array.Empty<object>();
            public string[][] ArrayIncomingPaths = Array.Empty<string[]>();
        }
        internal static FieldInfo[] ManagedFields(Type type)
        {
            if (type == typeof(PlayMakerFSM)) return CanonicalMarkerCarry.ManagedFields(type);
            if (type != typeof(FadeGroup))
                throw new InvalidDataException("Map Key dependency owner module/type differs.");
            if (type.Module.ModuleVersionId != new Guid("2b54cdaf-7a93-4f0e-b297-d50c4090ea9f"))
                CanonicalSceneMappingCarry.WarnOnce("MapKeyParentSource:FadeMvid:" + type.Module.ModuleVersionId,
                    "[HKTAS] FadeGroup ModuleVersionId differs from pinned original (continuing): actual=" + type.Module.ModuleVersionId.ToString("D"));
            var fields = CanonicalCarryGraphCodec.Fields(type).Where(field => field.DeclaringType != typeof(UnityEngine.Object)
                && field.DeclaringType != typeof(Component) && field.DeclaringType != typeof(Behaviour) && field.DeclaringType != typeof(MonoBehaviour)).ToArray();
            if (fields.Length != 16 || fields.Any(field => field.IsInitOnly || field.FieldType == typeof(IntPtr) || field.FieldType == typeof(UIntPtr)
                || field.FieldType.IsPointer || field.FieldType.IsByRef)) throw new InvalidDataException("Map Key complete FadeGroup field ABI differs.");
            return fields;
        }
        internal static Type[] OriginalActionTypes()
        {
            var action = typeof(HutongGames.PlayMaker.Actions.SetCameraCullingMask);
            var cache = typeof(HutongGames.PlayMaker.Actions.ComponentAction<Camera>);
            var fields = CanonicalCarryGraphCodec.Fields(action);
            var expected = new[] {
                Tuple.Create(cache, "cachedGameObject", typeof(GameObject)), Tuple.Create(cache, "component", typeof(Camera)),
                Tuple.Create(action, "cullingMask", typeof(FsmInt[])), Tuple.Create(action, "everyFrame", typeof(bool)),
                Tuple.Create(action, "gameObject", typeof(FsmOwnerDefault)), Tuple.Create(action, "invertMask", typeof(FsmBool)),
                Tuple.Create(typeof(FsmStateAction), "<DisplayName>k__BackingField", typeof(string)), Tuple.Create(typeof(FsmStateAction), "<Entered>k__BackingField", typeof(bool)),
                Tuple.Create(typeof(FsmStateAction), "active", typeof(bool)), Tuple.Create(typeof(FsmStateAction), "autoName", typeof(bool)),
                Tuple.Create(typeof(FsmStateAction), "enabled", typeof(bool)), Tuple.Create(typeof(FsmStateAction), "finished", typeof(bool)),
                Tuple.Create(typeof(FsmStateAction), "fsm", typeof(Fsm)), Tuple.Create(typeof(FsmStateAction), "fsmComponent", typeof(PlayMakerFSM)),
                Tuple.Create(typeof(FsmStateAction), "fsmState", typeof(FsmState)), Tuple.Create(typeof(FsmStateAction), "isOpen", typeof(bool)),
                Tuple.Create(typeof(FsmStateAction), "name", typeof(string)), Tuple.Create(typeof(FsmStateAction), "owner", typeof(GameObject)) };
            if (action.Module.ModuleVersionId != new Guid("2b54cdaf-7a93-4f0e-b297-d50c4090ea9f")
                || typeof(FsmStateAction).Module.ModuleVersionId != new Guid("29eb59fa-5c99-43cd-8f33-6db074533d31"))
                CanonicalSceneMappingCarry.WarnOnce("MapKeyParentSource:ActionMvid:" + action.Module.ModuleVersionId + ":" + typeof(FsmStateAction).Module.ModuleVersionId,
                    "[HKTAS] SetCameraCullingMask/FsmStateAction ModuleVersionId differs from pinned original (continuing).");
            if (fields.Length != expected.Length || fields.Where((field, index) => field.DeclaringType != expected[index].Item1
                    || field.Name != expected[index].Item2 || field.FieldType != expected[index].Item3 || field.IsInitOnly).Any())
                throw new InvalidDataException("Original Map Key camera action/cache complete field ABI differs.");
            // Camera and cached GameObject remain exact native externals;
            // neither is registered as an allocatable managed node.
            return new[] { action };
        }
        internal static string SchemaIdentity => "map-key-nine-parent-dependencies-v1-fixed-three-containers:"
            + string.Join(";", new[] { typeof(PlayMakerFSM), typeof(FadeGroup) }.SelectMany(type => ManagedFields(type))
                .Select(field => field.DeclaringType!.FullName + "/" + field.Name + "=" + field.FieldType.FullName))
            + ";" + string.Join(";", OriginalActionTypes().SelectMany(CanonicalCarryGraphCodec.Fields)
                .Select(field => field.DeclaringType!.FullName + "/" + field.Name + "=" + field.FieldType.FullName));
        private static Transform[] Ancestors(CanonicalMapKeyParentDirectory directory)
        {
            var result = new List<Transform>(); var current = directory.Keys;
            while (current != null) { result.Add(current); current = current.parent; }
            if (result.Count != 5) throw new InvalidDataException("Actual Map Key dependency ancestry differs.");
            return result.ToArray();
        }
        private static byte[] Native(CanonicalMapKeyParentDirectory directory)
        {
            using var validation = new LoadingEngineClockProbe.CheckpointValidation();
            return new MonoBehaviour[] { directory.Control, directory.Fade }.SelectMany(BoundaryMonoLifecycle.ObserveUnstartedUi).ToArray();
        }
        private static bool Registered(CanonicalMapKeyParentDirectory directory)
            => (Registrations.GetValue(null) as IList ?? throw new InvalidDataException("Map Key native FSM registration is absent.")).Cast<object>()
                .Any(value => ReferenceEquals(value, directory.Control));
        private static Data Read(CanonicalMapKeyParentDirectory directory, LoadingGeoIncomingReferences incoming)
        {
            directory.RequireQuiet(); var ancestors = Ancestors(directory);
            var owners = new object[] { directory.Control, directory.Fade };
            return new Data { Schema = SchemaIdentity, ParentNames = (string[])CanonicalMapKeyTextDirectory.Keys.Clone(),
                ActiveSelf = directory.Parents.Select(value => value.gameObject.activeSelf).ToArray(), HasChanged = directory.Parents.Select(value => value.hasChanged).ToArray(),
                Positions = directory.Parents.Select(value => value.localPosition).ToArray(), Rotations = directory.Parents.Select(value => value.localRotation).ToArray(),
                Scales = directory.Parents.Select(value => value.localScale).ToArray(), AncestorSelf = ancestors.Select(value => value.gameObject.activeSelf).ToArray(),
                AncestorHierarchy = ancestors.Select(value => value.gameObject.activeInHierarchy).ToArray(), Native = Native(directory), Registered = Registered(directory),
                Fields = owners.Select(value => ManagedFields(value.GetType()).Select(field => field.GetValue(value)).ToArray()).ToArray(),
                FieldNames = owners.SelectMany(value => ManagedFields(value.GetType())).Select(field => field.DeclaringType!.FullName + "/" + field.Name).ToArray(),
                ArraySnapshots = directory.Containers.Select(value => (object)value.Clone()).ToArray(),
                ArrayIncomingPaths = CanonicalMapKeyParentDirectory.ContainerNames.Select(name => incoming.PathsForIdentity(CanonicalMapKeyParentDirectory.ContainerPrefix + name)).ToArray() };
        }
        private static void Validate(Data data)
        {
            if (data.Schema != SchemaIdentity || !data.ParentNames.SequenceEqual(CanonicalMapKeyTextDirectory.Keys) || data.ActiveSelf.Length != 9 || data.HasChanged.Length != 9
                || data.Positions.Length != 9 || data.Rotations.Length != 9 || data.Scales.Length != 9 || data.AncestorSelf.Length != 5 || data.AncestorHierarchy.Length != 5
                || data.Native.Length != 10 || data.Native.Any(value => value > 1) || data.Fields.Length != 2 || data.Fields[0]?.Length != 4 || data.Fields[1]?.Length != 16
                || !data.FieldNames.SequenceEqual(new[] { typeof(PlayMakerFSM), typeof(FadeGroup) }.SelectMany(ManagedFields).Select(field => field.DeclaringType!.FullName + "/" + field.Name))
                || data.ArraySnapshots.Length != 3 || data.ArrayIncomingPaths.Length != 3 || data.ArrayIncomingPaths.Any(paths => paths == null || paths.Length > 4096
                    || !paths.SequenceEqual(paths.OrderBy(path => path, StringComparer.Ordinal)) || paths.Distinct(StringComparer.Ordinal).Count() != paths.Length)
                || !(data.ArraySnapshots[0] is InvAnimateUpAndDown[] animators) || animators.Length != 0
                || !(data.ArraySnapshots[1] is SpriteRenderer[] sprites) || sprites.Length != 11 || !(data.ArraySnapshots[2] is TMPro.TextMeshPro[] texts) || texts.Length != 11)
                throw new InvalidDataException("Map Key complete parent source schema, arrays or native directory differs.");
        }
        // Decoded source references have already resolved to the prepared
        // target owners. Every index must match the actual constructor array
        // before any source field or native activation can be written.
        internal static void RequirePreparedContainerOwners(object[] snapshots, Array[] actual)
        {
            if (snapshots == null || actual == null || snapshots.Length != 3 || actual.Length != 3)
                throw new InvalidDataException("Map Key prepared container directory differs.");
            for (var array = 0; array < 3; array++) {
                if (!(snapshots[array] is Array source) || actual[array] == null || source.GetType() != actual[array].GetType()
                    || source.Rank != 1 || source.GetLowerBound(0) != 0 || source.Length != actual[array].Length
                    || ReferenceEquals(source, actual[array]))
                    throw new InvalidDataException("Map Key prepared snapshot is not a distinct complete source container.");
                for (var index = 0; index < source.Length; index++)
                    if (!ReferenceEquals(source.GetValue(index), actual[array].GetValue(index)))
                        throw new InvalidDataException("Map Key prepared source container owner/index differs: " + array + "/" + index);
            }
        }
        private static SortedDictionary<string, object> Roots(Data data, Fsm fsm)
        { Validate(data); return new SortedDictionary<string, object>(StringComparer.Ordinal) { [Root] = data, [FsmRoot] = fsm }; }
        internal static Action ReadCurrentDependency(Transform keys)
        {
            var directory = new CanonicalMapKeyParentDirectory(keys);
            var active = directory.Parents.Select(value => value.gameObject.activeSelf).ToArray();
            var positions = directory.Parents.Select(value => value.localPosition).ToArray();
            var rotations = directory.Parents.Select(value => value.localRotation).ToArray();
            var scales = directory.Parents.Select(value => value.localScale).ToArray();
            bool Same(float left, float right) => BitConverter.ToInt32(BitConverter.GetBytes(left), 0) == BitConverter.ToInt32(BitConverter.GetBytes(right), 0);
            return () => {
                directory.RequireQuiet();
                for (var index = 0; index < 9; index++) {
                    var parent = directory.Parents[index]; var position = parent.localPosition; var rotation = parent.localRotation; var scale = parent.localScale;
                    if (parent.gameObject.activeInHierarchy || parent.gameObject.activeSelf != active[index]
                        || !Same(position.x, positions[index].x) || !Same(position.y, positions[index].y) || !Same(position.z, positions[index].z)
                        || !Same(rotation.x, rotations[index].x) || !Same(rotation.y, rotations[index].y) || !Same(rotation.z, rotations[index].z) || !Same(rotation.w, rotations[index].w)
                        || !Same(scale.x, scales[index].x) || !Same(scale.y, scales[index].y) || !Same(scale.z, scales[index].z))
                        throw new InvalidOperationException("Current actual Key parent D dependency is not ready for the shared inactive-leaf Canon.");
                }
            };
        }
        internal byte[] Capture(Transform keys, LoadingGeoIncomingReferences incoming, IReadOnlyDictionary<object, int> mainNodes,
            Func<object, string?> bindOther, Action<CanonicalCarryGraphData> auditAssets)
        {
            LoadingSerialSceneProbe.RequireCanonicalMutationBoundary();
            var directory = new CanonicalMapKeyParentDirectory(keys); var data = Read(directory, incoming);
            if (directory.Parents.Any(value => value.gameObject.activeInHierarchy)) throw new InvalidOperationException("Source Key dependency needs a future active-leaf Canon.");
            var leaves = LoadingGeoIncomingReferences.BuildMapKeyDirectory(keys);
            string? Bind(object value) {
                if (ReferenceEquals(value, directory.Fsm)) return null;
                for (var index = 0; index < directory.Containers.Length; index++)
                    if (ReferenceEquals(value, directory.Containers[index])) return Owner + CanonicalMapKeyParentDirectory.ContainerPrefix + CanonicalMapKeyParentDirectory.ContainerNames[index];
                foreach (var pair in directory.Native) if (ReferenceEquals(value, pair.Value)) return Owner + pair.Key;
                foreach (var pair in leaves) if (ReferenceEquals(value, pair.Value)) return Leaf + pair.Key;
                if (mainNodes.TryGetValue(value, out var node)) return MainNode + node;
                return bindOther(value);
            }
            var graph = codec.Capture(Roots(data, directory.Fsm), Bind);
            if (graph.Unsupported.Length != 0) throw new InvalidDataException("Map Key complete parent source graph has unsupported fields: " + string.Join(";", graph.Unsupported));
            auditAssets(graph); return codec.Encode(graph);
        }
        internal Prepared Prepare(byte[] bytes, Transform keys, LoadingGeoIncomingReferences incoming, CanonicalCarryGraphCodec.PreparedView main,
            Action requireMain, Func<string, object> resolveOther, Func<bool> admitted)
            => new Prepared(this, bytes, keys, incoming, main, requireMain, resolveOther, admitted);
        internal sealed class Prepared
        {
            private readonly CanonicalMapKeyParentSourceDomain owner;
            private readonly CanonicalMapKeyParentDirectory directory;
            private readonly CanonicalPreparedWritePlan plan;
            private readonly Func<bool> admitted;
            private readonly Action requireMain;
            private readonly Data source;
            private readonly CanonicalCarryGraphData graph;
            private readonly IReadOnlyDictionary<string, object> oldLeaves;
            private readonly CanonicalCarryGraphCodec.ExternalRebinding rebinding;
            private readonly IReadOnlyDictionary<string, object> finalDirectory;
            private int state; private readonly int thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            internal IReadOnlyDictionary<string, object> FinalDirectory => finalDirectory;
            internal IReadOnlyDictionary<string, object> OldLeaves => oldLeaves;
            internal Transform Keys => directory.Keys;
            internal Prepared(CanonicalMapKeyParentSourceDomain owner, byte[] bytes, Transform keys, LoadingGeoIncomingReferences incoming,
                CanonicalCarryGraphCodec.PreparedView main, Action requireMain, Func<string, object> resolveOther, Func<bool> admitted)
            {
                this.owner = owner; this.admitted = admitted; this.requireMain = requireMain;
                requireMain(); RequireAdmission(); directory = new CanonicalMapKeyParentDirectory(keys); oldLeaves = LoadingGeoIncomingReferences.BuildMapKeyDirectory(keys);
                var before = Read(directory, incoming); graph = owner.codec.Decode(bytes);
                object Resolve(string binding) {
                    if (binding.StartsWith(MainNode, StringComparison.Ordinal)) {
                        if (!int.TryParse(binding.Substring(MainNode.Length), out var index)) throw new InvalidDataException("Map Key parent main-node binding differs.");
                        return main.Node(index);
                    }
                    if (binding.StartsWith(Leaf, StringComparison.Ordinal)) return oldLeaves.TryGetValue(binding.Substring(Leaf.Length), out var value)
                        ? value : throw new InvalidDataException("Map Key parent placeholder leaf is absent.");
                    if (binding.StartsWith(Owner, StringComparison.Ordinal)) {
                        var key = binding.Substring(Owner.Length);
                        if (key.StartsWith(CanonicalMapKeyParentDirectory.ContainerPrefix, StringComparison.Ordinal)) {
                            var index = Array.IndexOf(CanonicalMapKeyParentDirectory.ContainerNames, key.Substring(CanonicalMapKeyParentDirectory.ContainerPrefix.Length));
                            return index < 0 ? throw new InvalidDataException("Map Key parent source container is unknown.") : directory.Containers[index];
                        }
                        return directory.Native.TryGetValue(key, out var value) ? value : throw new InvalidDataException("Map Key parent native owner is absent.");
                    }
                    return resolveOther(binding);
                }
                Data? saved = null; IReadOnlyDictionary<string, object>? final = null; CanonicalCarryGraphCodec.ExternalRebinding? external = null;
                plan = owner.codec.PrepareOwnedGraph(graph, Roots(before, directory.Fsm), Resolve, view => {
                    saved = new Data(); var root = view.Root(Root);
                    foreach (var field in CanonicalCarryGraphCodec.Fields(typeof(Data))) field.SetValue(saved, view.Field(root, typeof(Data), field.Name));
                    Validate(saved);
                    if (!saved.Native.SequenceEqual(before.Native) || saved.Registered != before.Registered || !saved.AncestorSelf.SequenceEqual(before.AncestorSelf)
                        || !saved.AncestorHierarchy.SequenceEqual(before.AncestorHierarchy)
                        || Enumerable.Range(0, 3).Any(index => !saved.ArrayIncomingPaths[index].SequenceEqual(before.ArrayIncomingPaths[index])))
                        throw new InvalidOperationException("Map Key source parent needs another ancestor/native/container alias continuation.");
                    RequirePreparedContainerOwners(saved.ArraySnapshots, directory.Containers);
                    var fields = ManagedFields(typeof(FadeGroup));
                    if ((int)saved.Fields[1][Array.FindIndex(fields, field => field.Name == "state")]! != 0)
                        throw new InvalidOperationException("Active source FadeGroup continuation is not supported.");
                    for (var index = 0; index < 3; index++) {
                        var field = Array.FindIndex(fields, field => field.Name == CanonicalMapKeyParentDirectory.ContainerNames[index]);
                        if (!ReferenceEquals(saved.Fields[1][field], directory.Containers[index])) throw new InvalidDataException("Map Key source container does not bind the actual constructor owner.");
                    }
                    var result = directory.Native.ToDictionary(pair => pair.Key, pair => (object)pair.Value, StringComparer.Ordinal);
                    for (var index = 0; index < 3; index++) result.Add(CanonicalMapKeyParentDirectory.ContainerPrefix + CanonicalMapKeyParentDirectory.ContainerNames[index], directory.Containers[index]);
                    var fsm = (Fsm)view.Root(FsmRoot); var prefix = "$:PlayMakerFSM[0]/fsm"; result.Add(prefix, fsm);
                    var states = (FsmState[]?)view.Field(fsm, typeof(Fsm), "states") ?? throw new InvalidDataException("Map Key prepared Control states are absent.");
                    for (var index = 0; index < states.Length; index++) {
                        var statePath = prefix + "/state[" + index + "]";
                        if (!result.Values.Any(value => ReferenceEquals(value, states[index]))) result.Add(statePath, states[index]);
                        var actions = view.Field(states[index], typeof(FsmState), "actions") as FsmStateAction[];
                        if (actions != null) for (var action = 0; action < actions.Length; action++) if (actions[action] != null && !result.Values.Any(value => ReferenceEquals(value, actions[action]))) result.Add(statePath + "/action[" + action + "]", actions[action]);
                    }
                    var active = view.Field(fsm, typeof(Fsm), "activeState") as FsmState;
                    var activeName = active == null ? null : (string?)view.Field(active, typeof(FsmState), "name");
                    if (activeName != "Inactive" && activeName != "No Pins") throw new InvalidOperationException("Active source Control continuation is not supported.");
                    final = result;
                }, exposeExternalRebinding: value => external = value);
                source = saved ?? throw new InvalidDataException("Map Key prepared parent source is absent.");
                finalDirectory = final ?? throw new InvalidDataException("Map Key prepared parent alias directory is absent.");
                rebinding = external ?? throw new InvalidDataException("Map Key prepared parent external controller is absent.");
            }
            internal void Fault() => state = -1;
            private void RequireAdmission()
            {
                if (state < 0 || !admitted() || thread != System.Threading.Thread.CurrentThread.ManagedThreadId)
                    throw new InvalidOperationException("Map Key parent transaction is faulted, expired or on another thread.");
            }
            private void Run(int expected, int next, Action action)
            {
                try { RequireAdmission(); if (state != expected) throw new InvalidOperationException("Map Key parent dependency phase is consumed or out of order."); action(); RequireAdmission(); state = next; }
                catch { state = -1; throw; }
            }
            internal void ApplyDependencies() => Run(0, 1, () => {
                LoadingSerialSceneProbe.RequireCanonicalMutationBoundary(); requireMain(); directory.RequireQuiet();
                // Raw source dependencies are installed directly; no FSM action,
                // event, preference write or source-derived gameplay is replayed.
                plan.Apply();
                var liveOwners = new object[] { directory.Control, directory.Fade };
                for (var index = 0; index < liveOwners.Length; index++) {
                    var fields = ManagedFields(liveOwners[index].GetType());
                    for (var field = 0; field < fields.Length; field++) fields[field].SetValue(liveOwners[index], source.Fields[index][field]);
                }
                CopyContainers();
                for (var index = 0; index < 9; index++) {
                    var parent = directory.Parents[index];
                    // Source self/pose, never inferred from PlayerData. Native
                    // deactivation invokes the original nine-leaf OnDisable.
                    if (parent.gameObject.activeSelf != source.ActiveSelf[index]) parent.gameObject.SetActive(source.ActiveSelf[index]);
                    if (!owner.codec.SameRawSlot(parent.localPosition, source.Positions[index], typeof(Vector3), 0)) parent.localPosition = source.Positions[index];
                    if (!owner.codec.SameRawSlot(parent.localRotation, source.Rotations[index], typeof(Quaternion), 0)) parent.localRotation = source.Rotations[index];
                    if (!owner.codec.SameRawSlot(parent.localScale, source.Scales[index], typeof(Vector3), 0)) parent.localScale = source.Scales[index];
                    if (parent.hasChanged != source.HasChanged[index]) parent.hasChanged = source.HasChanged[index];
                }
                RequireReadyCore(); VerifyCore(); requireMain();
            });
            private void CopyContainers()
            {
                for (var array = 0; array < 3; array++) {
                    var snapshot = (Array)source.ArraySnapshots[array]; var current = directory.Containers[array];
                    for (var index = 0; index < current.Length; index++) current.SetValue(snapshot.GetValue(index), index);
                }
            }
            internal void RequireReady()
            {
                try { RequireAdmission(); if (state != 1) throw new InvalidOperationException("Map Key cold Canon has no applied actual parent D dependency."); RequireReadyCore(); }
                catch { state = -1; throw; }
            }
            private void RequireReadyCore()
            {
                directory.RequireQuiet();
                if (directory.Parents.Any(value => value.gameObject.activeInHierarchy)) throw new InvalidOperationException("Actual source Key parents still need an active-leaf continuation.");
                VerifyPose();
            }
            internal void CompleteColdCanon() => Run(1, 2, () => { RequireReadyCore(); });
            internal void RebindLeaves(IReadOnlyDictionary<string, object> current) => Run(2, 3, () => {
                var replacements = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var item in graph.Externals) if (item.Binding.StartsWith(Leaf, StringComparison.Ordinal)) {
                    var path = item.Binding.Substring(Leaf.Length);
                    if (!current.TryGetValue(path, out var next) || !oldLeaves.TryGetValue(path, out var old)) throw new InvalidDataException("Map Key prepared source leaf binding is absent.");
                    if (!ReferenceEquals(old, next)) replacements.Add(item.Binding, next);
                }
                if (replacements.Count != 0) rebinding.Prepare(replacements).Apply();
                CopyContainers(); VerifyCore();
            });
            internal void CompleteTextSource() => Run(3, 4, VerifyCore);
            private void VerifyPose()
            {
                var ancestors = Ancestors(directory);
                if (!source.AncestorSelf.SequenceEqual(ancestors.Select(value => value.gameObject.activeSelf))
                    || !source.AncestorHierarchy.SequenceEqual(ancestors.Select(value => value.gameObject.activeInHierarchy)))
                    throw new InvalidOperationException("Map Key unowned ancestor activation changed.");
                for (var index = 0; index < 9; index++) {
                    var parent = directory.Parents[index];
                    if (parent.gameObject.activeSelf != source.ActiveSelf[index] || parent.hasChanged != source.HasChanged[index]
                        || !owner.codec.SameRawSlot(parent.localPosition, source.Positions[index], typeof(Vector3), 0)
                        || !owner.codec.SameRawSlot(parent.localRotation, source.Rotations[index], typeof(Quaternion), 0)
                        || !owner.codec.SameRawSlot(parent.localScale, source.Scales[index], typeof(Vector3), 0))
                        throw new InvalidOperationException("Map Key actual source parent active/pose/transform cache readback differs.");
                }
            }
            private void VerifyCore()
            {
                plan.VerifyReadback(); VerifyPose(); directory.RequireQuiet();
                if (!source.Native.SequenceEqual(Native(directory)) || source.Registered != Registered(directory))
                    throw new InvalidOperationException("Map Key parent native lifecycle or registration changed.");
                var live = new object[] { directory.Control, directory.Fade };
                for (var index = 0; index < live.Length; index++) {
                    var fields = ManagedFields(live[index].GetType());
                    for (var field = 0; field < fields.Length; field++) if (!owner.codec.SameRawSlot(source.Fields[index][field], fields[field].GetValue(live[index]), fields[field].FieldType, 0))
                        throw new InvalidOperationException("Map Key parent complete managed source field readback differs: " + fields[field].Name);
                }
                for (var array = 0; array < 3; array++) {
                    var snapshot = (Array)source.ArraySnapshots[array]; var actual = directory.Containers[array];
                    if (!ReferenceEquals(actual, typeof(FadeGroup).GetField(CanonicalMapKeyParentDirectory.ContainerNames[array])!.GetValue(directory.Fade)))
                        throw new InvalidOperationException("Map Key final array is detached from the real current FadeGroup.");
                    for (var index = 0; index < actual.Length; index++) if (!ReferenceEquals(actual.GetValue(index), snapshot.GetValue(index)))
                        throw new InvalidOperationException("Map Key final prepared source array index differs from the actual current container.");
                }
            }
            internal void Verify()
            {
                try { RequireAdmission(); if (state != 4) throw new InvalidOperationException("Map Key parent source phases are incomplete."); VerifyCore(); requireMain(); }
                catch { state = -1; throw; }
            }
        }
    }
}
