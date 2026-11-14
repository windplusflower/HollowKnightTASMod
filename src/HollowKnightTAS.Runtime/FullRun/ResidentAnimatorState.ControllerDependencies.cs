using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAnimatorState
    {
        internal sealed class ControllerDependency
        {
            public Storage Behaviours = new Storage { Label = 74 };
            public string[] BehaviourObjects = Array.Empty<string>();
            public string PlayerObject = "", ObservedAsset = "", Asset = "";
            public int PlayerSelf = -1, Sender = -1, SenderVtable, LayerMixer = -1;
            public bool ConstantAssetAlias;
            public int HeapVtable, BlockVtable, MemoryVtable, HeapLabel, BlockLabel;
            public bool BlockStorage;
            public bool BindingsAlive, AdditionalPresent, InstancesPresent, ObserverSelf;
            public int[] AdditionalIndices = Array.Empty<int>();
            public Generic[] AdditionalBindings = Array.Empty<Generic>();
            public int ObserverCallback, ObserverSlot = -1;
            public Storage ObserverStorage = new Storage();
            public string[] ObserverPeers = Array.Empty<string>();
            public ControllerInstanceDependency[] Instances = Array.Empty<ControllerInstanceDependency>();
        }
        internal sealed class ControllerInstanceDependency
        {
            public bool Present;
            public int[] Nodes = Array.Empty<int>();
        }

        private static string ControllerObserverKey(int owner, int node, int offset)
            => owner + "/playable/" + node + "/observer/" + offset;

        private static ControllerDependency CaptureControllerDependency(Reader reader, GraphView graph, int nodeIndex, Playable player)
        {
            var owner = graph.Nodes[nodeIndex];
            var bytes = reader.Memory.Read(owner,456);
            int Index(IntPtr pointer) => pointer == IntPtr.Zero ? -1
                : graph.Indices.TryGetValue(pointer,out var index) ? index : UnknownEdge;
            var result = new ControllerDependency {
                Behaviours = StorageAt(bytes,336),
                BehaviourObjects = Decode(reader.ArrayBytes(bytes,336,4),4,(array,at) => reader.Id<NativeObject>(M.I32(array,at))),
                PlayerObject = reader.Key<NativeObject>(M.Ptr(bytes,368)),
                Asset = reader.Id<NativeObject>(M.I32(bytes,264)),
                PlayerSelf = Edge(M.Ptr(bytes,376),owner),
                Sender = Edge(M.Ptr(bytes,384),IntPtr.Add(owner,272)),
                SenderVtable = reader.Rva(M.Ptr(bytes,272)),
                BindingsAlive = bytes[260] == 1,
                AdditionalPresent = M.Ptr(bytes,392) != IntPtr.Zero,
                LayerMixer = Index(M.Ptr(bytes,408)),
                InstancesPresent = M.Ptr(bytes,440) != IntPtr.Zero
            };
            result.HeapVtable = reader.Rva(M.Ptr(bytes,192));
            result.BlockVtable = reader.Rva(M.Ptr(bytes,208));
            result.MemoryVtable = reader.Rva(M.Ptr(bytes,288));
            result.HeapLabel = M.I32(bytes,200);
            result.BlockLabel = M.I32(bytes,248);
            result.BlockStorage = M.Ptr(bytes,216) != IntPtr.Zero || M.Ptr(bytes,224) != IntPtr.Zero || M.U64(bytes,240) != 0;
            var controller = M.Ptr(bytes,280);
            result.ConstantAssetAlias = controller == IntPtr.Zero
                ? M.Ptr(bytes,296) == IntPtr.Zero
                : ControllerProgramPointer(reader,controller) == M.Ptr(bytes,296);
            if (result.BindingsAlive) {
                var map = BindingBuffer(reader,M.Ptr(bytes,392),player.Runtime.Parameters.Length/12,4);
                result.AdditionalIndices = Enumerable.Range(0,map.Length/4).Select(i => M.I32(map,i*4)).ToArray();
                result.AdditionalBindings = reader.Live.Data.Rows[graph.Data.Owner].Evaluation[0].Backing.Tables[0];
            }
            int instanceCount = Count(M.U32(bytes,448));
            var directory = BindingBuffer(reader,M.Ptr(bytes,440),instanceCount,16);
            result.Instances = Decode(directory,16,(array,at) => {
                var pointer = M.Ptr(array,at);
                int count = Count(M.U32(array,at+8));
                return new ControllerInstanceDependency {
                    Present = pointer != IntPtr.Zero,
                    Nodes = Decode(BindingBuffer(reader,pointer,count,8),8,(slots,index) => Index(M.Ptr(slots,index)))
                };
            });
            var asset = M.Ptr(bytes,416);
            result.ObservedAsset = asset == IntPtr.Zero ? "" : reader.Key<RuntimeAnimatorController>(IntPtr.Subtract(asset,136));
            result.ObserverSelf = M.Ptr(bytes,424) == (asset == IntPtr.Zero ? IntPtr.Zero : owner);
            result.ObserverCallback = reader.Rva(M.Ptr(bytes,432));
            if (asset != IntPtr.Zero) {
                var header = reader.Memory.Read(asset,32);
                result.ObserverStorage = StorageAt(header,0);
                var peers = reader.ArrayBytes(header,0,8);
                var known = new Dictionary<IntPtr,string>();
                foreach (var topology in reader.Live.Topologies) {
                    for (int index = 0; index < topology.Nodes.Count; index++) {
                        int offset = topology.Data.Nodes[index].Vtable == PlayableVtable ? 416
                            : topology.Data.Nodes[index].Vtable == 0x15d8b80 ? 472 : -1;
                        if (offset >= 0)
                            known.Add(IntPtr.Add(topology.Nodes[index],offset),ControllerObserverKey(topology.Data.Owner,index,offset));
                    }
                }
                result.ObserverPeers = Decode(peers,8,(array,at) => {
                    var pointer = M.Ptr(array,at);
                    return known.TryGetValue(pointer,out var key) ? key : Unbound;
                });
                var ownKey = ControllerObserverKey(graph.Data.Owner,nodeIndex,416);
                result.ObserverSlot = Array.IndexOf(result.ObserverPeers,ownKey);
                if (result.ObserverPeers.Count(key => key == ownKey) != 1)
                    result.ObserverSlot = UnknownEdge;
            }
            return result;
        }

        private static IntPtr ControllerProgramPointer(Reader reader, IntPtr controller)
        {
            // Override controllers borrow their base controller's serialized
            // program. Walk only typed identities; never scan the program body.
            var seen = new HashSet<IntPtr>();
            while (controller != IntPtr.Zero) {
                if (!seen.Add(controller)) throw Error("controller program ownership cycle");
                var vt = M.Ptr(reader.Memory.Read(controller,8),0);
                if (reader.Rva(vt) == 0x171e6d0)
                    return M.Ptr(reader.Memory.Read(controller,272),256);
                if (reader.Rva(vt) != 0x15d6778) return new IntPtr(-1);
                int id = M.I32(reader.Memory.Read(controller,172),168);
                if (id == 0) return IntPtr.Zero;
                // Resolve through the already pinned ID directory only.
                if (!reader.ObjectPointer(id,out controller)) return new IntPtr(-1);
            }
            return IntPtr.Zero;
        }

        private static void CaptureControllerDependencies(Reader reader)
        {
            var observed = new HashSet<int>();
            foreach (var graph in reader.Live.Topologies) {
                for (int index = 0; index < graph.Data.Nodes.Length; index++) {
                    var node = graph.Data.Nodes[index];
                    if (node.Controller < 0) continue;
                    if (!observed.Add(node.Controller)) throw Error("controller playable belongs to multiple graph nodes");
                    var player = reader.Live.Data.Playables[node.Controller];
                    player.Dependency = CaptureControllerDependency(reader,graph,index,player);
                }
            }
        }

        private static void ValidateControllerDependency(Playable player)
        {
            var value = player.Dependency;
            if (value == null) return;
            if (value.Behaviours == null || !Keys(value.BehaviourObjects) || !Text(value.PlayerObject) || !Text(value.ObservedAsset) || !Text(value.Asset)
                || value.BehaviourObjects.Length > MaximumEntries || value.Behaviours.Capacity < value.BehaviourObjects.Length
                || value.Behaviours.Capacity > MaximumEntries || value.Behaviours.Capacity != 0 && !value.Behaviours.Allocated
                || value.PlayerSelf < UnknownEdge || value.PlayerSelf > 0 || value.Sender < UnknownEdge || value.Sender > 0
                || value.LayerMixer < UnknownEdge || value.AdditionalIndices == null || value.AdditionalBindings == null
                || value.AdditionalBindings.Any(binding => binding == null || !Text(binding.Script))
                || value.Instances == null || value.Instances.Length != player.Runtime.PlayableInstances
                || value.Instances.Any(instance => instance == null || instance.Nodes == null || instance.Nodes.Length > MaximumEntries
                    || instance.Nodes.Any(index => index < UnknownEdge) || instance.Nodes.Length != 0 && !instance.Present)
                || value.Instances.Length != 0 && !value.InstancesPresent
                || value.ObserverStorage == null || !Keys(value.ObserverPeers) || value.ObserverPeers.Length > MaximumEntries
                || value.ObserverStorage.Capacity < value.ObserverPeers.Length || value.ObserverStorage.Capacity > MaximumEntries
                || value.ObserverStorage.Capacity != 0 && !value.ObserverStorage.Allocated
                || value.ObserverSlot < UnknownEdge || value.ObserverSlot >= value.ObserverPeers.Length && value.ObserverSlot != -1)
                throw Error("controller dependency shape differs");
            if (value.AdditionalIndices.Length != (value.BindingsAlive ? player.Runtime.Parameters.Length/12 : 0)
                || !value.BindingsAlive && value.AdditionalBindings.Length != 0
                || value.BindingsAlive && value.AdditionalPresent != (value.AdditionalIndices.Length != 0))
                throw Error("controller additional mapping lifecycle differs");
            foreach (var index in value.AdditionalIndices) {
                if (index < -1 || index >= value.AdditionalBindings.Length)
                    throw Error("controller parameter mapping lies outside animation bindings");
            }
            for (int parameter = 0; parameter < value.AdditionalIndices.Length; parameter++) {
                int index = value.AdditionalIndices[parameter];
                if (index >= 0 && (value.AdditionalBindings[index].Custom != 0
                    || value.AdditionalBindings[index].Type != 95
                    || value.AdditionalBindings[index].Attribute != M.U32(player.Runtime.Parameters,parameter*12)))
                    throw Error("controller parameter mapping does not match its logical Animator binding");
            }
            if (value.ObservedAsset.Length == 0 && (value.ObserverPeers.Length != 0 || value.ObserverSlot != -1))
                throw Error("absent controller observer retains registrations");
        }

        private static void ValidateControllerDependencyOwners(Data data)
        {
            foreach (var player in data.Playables) ValidateControllerDependency(player);
            var owners = new HashSet<int>();
            var observers = new Dictionary<string,string>(StringComparer.Ordinal);
            foreach (var graph in data.Graphs) {
                for (int index = 0; index < graph.Nodes.Length; index++) {
                    var node = graph.Nodes[index];
                    if (node.Controller >= 0 && data.Playables[node.Controller].Dependency is ControllerDependency dependency)
                        observers.Add(ControllerObserverKey(graph.Owner,index,416),dependency.ObservedAsset);
                    if (node.Clip != null)
                        observers.Add(ControllerObserverKey(graph.Owner,index,472),node.Clip.Observed);
                }
            }
            foreach (var graph in data.Graphs) {
                for (int nodeIndex = 0; nodeIndex < graph.Nodes.Length; nodeIndex++) {
                    var node = graph.Nodes[nodeIndex];
                    if (node.Controller < 0) continue;
                    if (!owners.Add(node.Controller)) throw Error("controller dependency has duplicate graph owner");
                    var player = data.Playables[node.Controller];
                    var value = player.Dependency;
                    if (value == null) continue;
                    if (value.BindingsAlive != (node.AnimationFlags[4] == 1)
                        || value.LayerMixer >= graph.Nodes.Length
                        || value.LayerMixer >= 0 && graph.Nodes[value.LayerMixer].Vtable != 0x15d9380
                        || value.Instances.Any(instance => instance.Nodes.Any(index => index >= graph.Nodes.Length)))
                        throw Error("controller dependency graph aliases differ");
                    if (value.Instances.Length != player.Runtime.SynchronizedLayers.Length)
                        throw Error("controller instance directory machine count differs");
                    for (int index = 0; index < value.Instances.Length; index++) {
                        if (value.Instances[index].Nodes.Length != player.Runtime.SynchronizedLayers[index])
                            throw Error("controller instance layer directory differs");
                    }
                    if (value.BindingsAlive && Json(value.AdditionalBindings) != Json(data.Rows[graph.Owner].Evaluation[0].Backing.Tables[0]))
                        throw Error("controller mapping animation domain differs");
                    if (value.ObserverSlot >= 0 && value.ObserverPeers[value.ObserverSlot] != ControllerObserverKey(graph.Owner,nodeIndex,416))
                        throw Error("controller observer reciprocal slot differs");
                    foreach (var peer in value.ObserverPeers) {
                        if (peer != Unbound && (!observers.TryGetValue(peer,out var asset) || asset != value.ObservedAsset))
                            throw Error("controller asset observer has no reciprocal typed owner");
                    }
                    if (value.ObservedAsset.Length != 0 && value.ObservedAsset != Unbound
                        && new[] {56,96}.Any(offset => !data.Users.Any(slot => slot.Owner == value.ObservedAsset && slot.Offset == offset)))
                        throw Error("observed controller lacks its typed UserList ownership closure");
                }
            }
            if (data.Playables.Where((player,index) => player.Dependency != null && !owners.Contains(index)).Any())
                throw Error("controller dependency has no graph owner");
        }

        private static IEnumerable<string> ControllerDependencyGaps(Playable player)
        {
            var value = player.Dependency;
            if (value == null) {
                yield return "controller-playable-mixer-and-behaviours";
                yield break;
            }
            if (value.BehaviourObjects.Length != 0) yield return "controller-nonempty-behaviour-clone-lifecycle";
            if (!value.ConstantAssetAlias) yield return "controller-program-without-asset-owner";
            if (value.HeapVtable != 0x15d5b60 || value.BlockVtable != 0x15d6cf8 || value.MemoryVtable != 0x15d8f00
                || value.HeapLabel != 30 || value.BlockLabel != 30 || value.BlockStorage)
                yield return "controller-native-allocator-dependency";
            if (value.Behaviours.Label != 74 || (value.Behaviours.Capacity != 0 && value.Behaviours.Padding)) yield return "controller-behaviour-storage-policy";
            if (value.Behaviours.Capacity == 0 && value.Behaviours.Padding) yield return "playable-vector-padding-requires-common-canon";
            if (value.Sender != 0 || value.SenderVtable != 0x15d9030 || value.PlayerSelf == UnknownEdge)
                yield return "controller-behaviour-player-sender-or-self";
            if (value.PlayerObject == Unbound || value.ObservedAsset == Unbound || value.Asset == Unbound) yield return "controller-unbound-object-dependency";
            if (!value.BindingsAlive && value.AdditionalPresent) yield return "controller-additional-mapping-lifecycle";
            if (value.LayerMixer == UnknownEdge || value.Instances.Any(instance => instance.Nodes.Contains(UnknownEdge)))
                yield return "controller-unbound-owned-graph-node";
            if (!value.ObserverSelf || value.ObserverCallback != (value.ObservedAsset.Length == 0 ? 0 : 0x1138e0)
                || value.ObservedAsset.Length != 0 && value.ObserverSlot < 0 || value.ObserverPeers.Contains(Unbound))
                yield return "controller-asset-observer-registration";
        }

        private static void RequireControllerDependenciesMatch(Playable source, Playable target)
        {
            ValidateControllerDependency(source);
            ValidateControllerDependency(target);
            Reject(ControllerDependencyGaps(source).Concat(ControllerDependencyGaps(target)));
            if (source.Dependency == null || target.Dependency == null || Json(source.Dependency) != Json(target.Dependency))
                throw Error("target controller dependency ownership or logical bindings differ");
        }
    }
}
