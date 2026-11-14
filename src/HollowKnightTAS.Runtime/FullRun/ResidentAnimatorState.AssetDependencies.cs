using System;
using System.Collections.Generic;
using System.Linq;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAnimatorState
    {
        internal static IEnumerable<string> ClipAssetReferences(Data data)
        {
            foreach (var row in data.Rows) {
                yield return row.BinderClip;
                foreach (var key in row.CachedClips) yield return key;
                foreach (var evaluation in row.Evaluation)
                    foreach (var key in evaluation.Backing.Clips) yield return key;
            }
            foreach (var graph in data.Graphs) {
                foreach (var node in graph.Nodes) {
                    if (node.Clip == null) continue;
                    yield return node.Clip.Asset;
                    yield return node.Clip.Clip;
                    yield return node.Clip.Observed;
                    yield return node.Clip.Constant;
                    // Clip.Binding is a local binding-table key, not an asset.
                }
            }
            foreach (var asset in data.Assets)
                foreach (var key in asset.Clips.Concat(asset.OriginalClips).Concat(asset.OverridePairs)) yield return key;
        }
        private static void CaptureControllerAssetObservers(Reader reader,Dictionary<IntPtr,ControllerAsset> assets)
        {
            var known = new Dictionary<IntPtr,string>();
            foreach (var graph in reader.Live.Topologies) {
                for (int index=0; index<graph.Nodes.Count; index++) {
                    int offset = graph.Data.Nodes[index].Vtable == PlayableVtable ? 416
                        : graph.Data.Nodes[index].Vtable == 0x15d8b80 ? 472 : -1;
                    if (offset >= 0)
                        known.Add(IntPtr.Add(graph.Nodes[index],offset),ControllerObserverKey(graph.Data.Owner,index,offset));
                }
            }
            foreach (var pair in assets) {
                var row = pair.Value;
                if (row.Kind == "unknown") continue;
                // RuntimeAnimatorController::m_AnimationAsset is inherited by
                // both concrete controller kinds. UserList56/96 is separate.
                var header = reader.Memory.Read(IntPtr.Add(pair.Key,136),32);
                var peers = reader.ArrayBytes(header,0,8);
                row.ObserverPeers = Decode(peers,8,(bytes,offset)=>known.TryGetValue(M.Ptr(bytes,offset),out var key) ? key : Unbound);
                row.ObserverDirectoryCaptured = true;
            }
        }
        internal static void ValidateControllerAssetObservers(Data data)
        {
            var expected = new Dictionary<string,List<string>>(StringComparer.Ordinal);
            var byKey = data.Assets.ToDictionary(asset=>asset.Binding,StringComparer.Ordinal);
            foreach (var asset in data.Assets) expected.Add(asset.Binding,new List<string>());
            foreach (var graph in data.Graphs) {
                for (int index=0; index<graph.Nodes.Length; index++) {
                    var node = graph.Nodes[index];
                    if (node.Controller < 0) continue;
                    var dependency = data.Playables[node.Controller].Dependency;
                    if (dependency == null || dependency.ObservedAsset.Length == 0) continue;
                    if (!expected.TryGetValue(dependency.ObservedAsset,out var peers)) throw Error("controller observer refers to an absent asset");
                    string key = ControllerObserverKey(graph.Owner,index,416);
                    peers.Add(key);
                    var asset = byKey[dependency.ObservedAsset];
                    if (!dependency.ObserverPeers.SequenceEqual(asset.ObserverPeers))
                        throw Error("controller observer asset and playable directories differ");
                }
            }
            foreach (var asset in data.Assets) {
                if (!asset.ObserverDirectoryCaptured || asset.ObserverPeers == null
                    || asset.ObserverPeers.Distinct(StringComparer.Ordinal).Count()!=asset.ObserverPeers.Length
                    || !new HashSet<string>(asset.ObserverPeers,StringComparer.Ordinal).SetEquals(expected[asset.Binding]))
                    throw Error("controller asset lacks a complete reciprocal observer directory");
            }
        }
    }
}
