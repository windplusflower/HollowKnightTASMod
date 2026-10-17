using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Original fixed Wide Map owners. The source supplies both raw FSM graphs
    // and native activation; PlayerData is never used to infer activation.
    internal sealed class CanonicalMapSourceDomain
    {
        internal const string Root = "Map/Source";
        private const string MainNode = "Map/MainNode/", Owner = "Map/Owner/";
        internal static readonly string[] Areas = { "Ancient Basin", "City", "Cliffs", "Crossroads", "Deepnest", "Fog Canyon",
            "Fungal Wastes", "Greenpath", "Hive", "Mines", "Outskirts", "Resting Grounds", "Royal Gardens", "Town", "Waterways" };
        private static readonly FieldInfo RawFsm = typeof(PlayMakerFSM).GetField("fsm", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo ActiveState = typeof(Fsm).GetField("activeState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo StateName = typeof(FsmState).GetField("name", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo Registrations = typeof(PlayMakerFSM).GetField("fsmList", BindingFlags.Static | BindingFlags.NonPublic)!;
        internal static readonly Type[] DataTypes = { typeof(Data), typeof(Fsm[]), typeof(string[]), typeof(bool[]) };
        internal sealed class Data
        {
            public string[] Owners = Array.Empty<string>(), NativeOwners = Array.Empty<string>(), Registered = Array.Empty<string>();
            public Fsm[] Fsms = Array.Empty<Fsm>();
            public bool[] ActiveSelf = Array.Empty<bool>();
            public byte[] Lifecycle = Array.Empty<byte>();
        }
        private readonly CanonicalCarryGraphCodec codec = CanonicalCarryFieldRegistry.Create(mapDomain: true);
        private sealed class Directory
        {
            internal readonly SortedDictionary<string, UnityEngine.Object> Objects = new SortedDictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
            internal readonly GameObject[] Parents = new GameObject[15];
            internal readonly Transform[] ParentTransforms = new Transform[15], LabelTransforms = new Transform[15];
            internal Directory()
            {
                var wide = GameCameras.instance.hudCamera.transform.Find("Inventory/Map/World Map/Wide Map")
                    ?? throw new InvalidDataException("Original fixed Wide Map is absent.");
                for (var i = 0; i < Areas.Length; i++) {
                    var parent = wide.Find(Areas[i]) ?? throw new InvalidDataException("Original Map area is absent: " + Areas[i]);
                    var label = parent.Find("Area Name") ?? throw new InvalidDataException("Original Map label is absent: " + Areas[i]);
                    Parents[i] = parent.gameObject; ParentTransforms[i] = parent; LabelTransforms[i] = label;
                    Objects.Add(Areas[i] + "/GO", parent.gameObject); Objects.Add(Areas[i] + "/Label/GO", label.gameObject);
                    Add(Areas[i], parent, Areas[i] == "Town" ? 2 : Areas[i] == "Hive" ? 4 : 3);
                    Add(Areas[i] + "/Label", label, 1);
                }
                RequireInactive();
            }
            private void Add(string path, Transform owner, int expectedFsms)
            {
                var components = owner.GetComponents<MonoBehaviour>();
                if (components.Any(value => value == null) || components.Count(value => value is PlayMakerFSM) != expectedFsms)
                    throw new InvalidDataException("Original fixed Map component directory differs: " + path);
                var counts = new Dictionary<Type, int>();
                foreach (var value in components) {
                    var type = value.GetType(); counts.TryGetValue(type, out var ordinal); counts[type] = ordinal + 1;
                    Objects.Add(path + "/" + type.FullName + "[" + ordinal + "]", value);
                }
            }
            internal void RequireInactive()
            {
                for (var i = 0; i < Parents.Length; i++) {
                    // The direct ancestor must itself be inactive in hierarchy,
                    // independently of the value about to be written below it.
                    if (Parents[i] == null || ParentTransforms[i].parent == null || ParentTransforms[i].parent.gameObject.activeInHierarchy
                        || Parents[i].activeInHierarchy || LabelTransforms[i].gameObject.activeInHierarchy
                        || !ReferenceEquals(Parents[i].transform, ParentTransforms[i])
                        || !ReferenceEquals(LabelTransforms[i].parent, ParentTransforms[i]))
                        throw new InvalidOperationException("Active Map requires an original lifecycle continuation not yet supported: " + Areas[i]);
                }
            }
            internal Data Read()
            {
                RequireInactive();
                var fsms = Objects.Where(pair => pair.Value is PlayMakerFSM).ToArray();
                var native = Objects.Where(pair => pair.Value is MonoBehaviour).ToArray();
                var registered = Registrations.GetValue(null) as List<PlayMakerFSM>
                    ?? throw new InvalidDataException("Original Map FSM registrations are absent.");
                byte[] lifecycle;
                using (new LoadingEngineClockProbe.CheckpointValidation())
                    lifecycle = native.SelectMany(pair => BoundaryMonoLifecycle.ObserveUnstartedUi((MonoBehaviour)pair.Value)).ToArray();
                if (fsms.Length != 60) throw new InvalidDataException("Original fixed Map FSM count differs.");
                return new Data { Owners = fsms.Select(pair => pair.Key).ToArray(), Fsms = fsms.Select(pair => (Fsm)RawFsm.GetValue(pair.Value)).ToArray(),
                    NativeOwners = native.Select(pair => pair.Key).ToArray(), Lifecycle = lifecycle,
                    Registered = fsms.Where(pair => registered.Any(item => ReferenceEquals(item, pair.Value))).Select(pair => pair.Key).ToArray(),
                    ActiveSelf = Parents.Select(value => value.activeSelf).ToArray() };
            }
        }
        private static SortedDictionary<string, object> Roots(Data data)
        {
            if (data.Owners.Length != 60 || data.Fsms.Length != 60 || data.ActiveSelf.Length != 15 || data.Owners.Distinct().Count() != 60)
                throw new InvalidDataException("Fixed Map source directory differs.");
            var result = new SortedDictionary<string, object>(StringComparer.Ordinal) { [Root] = data };
            for (var i = 0; i < data.Owners.Length; i++) result.Add("Map/Fsm/" + data.Owners[i], data.Fsms[i]);
            return result;
        }
        internal byte[] Capture(IReadOnlyDictionary<object, int> mainNodes, Func<object, string?> bindOther, Action<CanonicalCarryGraphData> auditAssets)
        {
            var directory = new Directory(); var data = directory.Read();
            string? Bind(object value) {
                if (mainNodes.TryGetValue(value, out var id)) return MainNode + id;
                foreach (var pair in directory.Objects) if (ReferenceEquals(pair.Value, value)) return Owner + pair.Key;
                return bindOther(value);
            }
            var graph = codec.Capture(Roots(data), Bind);
            if (graph.Unsupported.Length != 0) throw new InvalidDataException("Fixed Map source graph has unsupported fields: " + string.Join(";", graph.Unsupported));
            auditAssets(graph);
            Modding.Logger.Log("[HKTAS] Map source graph shape: nodes=" + graph.Nodes.Length + ";externals=" + graph.Externals.Length + ";mainNodeBindings=" + graph.Externals.Count(item => item.Binding.StartsWith(MainNode, StringComparison.Ordinal)));
            Modding.Logger.Log("[HKTAS] Map source fixed FSM state names: " + string.Join(";", data.Fsms.Select((fsm, index) => data.Owners[index] + "=" + (ActiveState.GetValue(fsm) is FsmState state ? (string?)StateName.GetValue(state) : "<none>"))));
            Modding.Logger.Log("[HKTAS] Map source captured: originalFsms=60;activeSelf=" + string.Join(",", data.ActiveSelf.Select(value => value ? "1" : "0")) + ";nativeHierarchyInactive=true;completeStateCoverage=false");
            return codec.Encode(graph);
        }
        internal Action Prepare(byte[] bytes, CanonicalCarryGraphCodec.PreparedView main, Action requireMain,
            Func<string, object> resolveOther, Func<bool> admitted, out Action verify)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            requireMain();
            var directory = new Directory(); var before = directory.Read(); Data? source = null;
            var nativeMilliseconds = elapsed.ElapsedMilliseconds;
            var graph = codec.Decode(bytes); var decodeMilliseconds = elapsed.ElapsedMilliseconds;
            object Resolve(string binding) {
                if (binding.StartsWith(MainNode, StringComparison.Ordinal)) {
                    if (!int.TryParse(binding.Substring(MainNode.Length), out var id)) throw new InvalidDataException("Map main-node binding differs.");
                    return main.Node(id);
                }
                if (binding.StartsWith(Owner, StringComparison.Ordinal)) return directory.Objects.TryGetValue(binding.Substring(Owner.Length), out var value)
                    ? value : throw new InvalidDataException("Map original native binding is absent.");
                return resolveOther(binding);
            }
            var plan = codec.PrepareOwnedGraph(graph, Roots(before), Resolve, view => {
                source = new Data(); var root = view.Root(Root);
                foreach (var field in CanonicalCarryGraphCodec.Fields(typeof(Data))) field.SetValue(source, view.Field(root, typeof(Data), field.Name));
                Roots(source);
                if (!source.Owners.SequenceEqual(before.Owners) || !source.NativeOwners.SequenceEqual(before.NativeOwners)
                    || !source.Lifecycle.SequenceEqual(before.Lifecycle) || !source.Registered.SequenceEqual(before.Registered))
                    throw new InvalidOperationException("Map source needs a different original native lifecycle or registration continuation.");
            });
            Modding.Logger.Log("[HKTAS] Map preparation timing: nativeAndMainMs=" + nativeMilliseconds + ";decodeMs=" + (decodeMilliseconds - nativeMilliseconds)
                + ";prepareMs=" + (elapsed.ElapsedMilliseconds - decodeMilliseconds) + ";nodes=" + graph.Nodes.Length + ";externals=" + graph.Externals.Length);
            void Verify() {
                plan.VerifyReadback(); var current = directory.Read();
                if (source == null || !current.ActiveSelf.SequenceEqual(source.ActiveSelf) || !current.Lifecycle.SequenceEqual(source.Lifecycle)
                    || !current.Registered.SequenceEqual(source.Registered)) throw new InvalidOperationException("Map complete final native source readback differs.");
            }
            verify = Verify;
            var consumed = false;
            return () => {
                if (consumed || !admitted() || source == null) throw new InvalidOperationException("Map source lost single-use paused admission.");
                consumed = true; requireMain(); directory.RequireInactive();
                // No PlayerLoop runs in this protected interval. With the real
                // ancestor inactive, SetActive cannot change activeInHierarchy
                // and cannot invoke OnEnable/OnDisable or schedule their work.
                for (var i = 0; i < directory.Parents.Length; i++) {
                    directory.RequireInactive();
                    if (directory.Parents[i].activeSelf != source.ActiveSelf[i]) directory.Parents[i].SetActive(source.ActiveSelf[i]);
                    directory.RequireInactive();
                }
                var after = directory.Read();
                if (!after.ActiveSelf.SequenceEqual(source.ActiveSelf) || !after.Lifecycle.SequenceEqual(before.Lifecycle)
                    || !after.Registered.SequenceEqual(before.Registered)) throw new InvalidOperationException("Map native activation caused an unexpected original lifecycle effect.");
                plan.Apply(); Verify(); requireMain();
                if (!admitted()) throw new InvalidOperationException("Map source lost final paused admission.");
                Modding.Logger.Log("[HKTAS] Map complete source graph/activation readback passed: originalFsms=60;nativeLifecycleRegistrationEqual=true;activeHierarchyRemainedInactive=true;sourceActiveSelfExact=true;mainInputAliasReadbackEqual=true;completeStateCoverage=false");
            };
        }
    }
}
