using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAnimatorState
    {
        private static bool Text(string key) => key != null && key.Length <= 262144;
        private static bool Keys(string[] keys) => keys != null && keys.Length <= MaximumEntries * 2 && keys.All(Text);
        internal static void Validate(Data data)
        {
            if (data == null || data.Version != 1 || data.Rows == null || data.Rows.Length > MaximumEntries || data.Playables == null
                || data.Playables.Length > MaximumEntries || data.Assets == null || data.Assets.Length > MaximumEntries || data.Registry == null)
                throw Error("Animator domain shape differs");
            var used = new HashSet<int>(); var graphs = new HashSet<int>(); string? previous = null;
            for (var i = 0; i < data.Rows.Length; i++) {
                var row = data.Rows[i];
                if (row == null || !Text(row.Owner) || row.Owner.Length == 0 || StringComparer.Ordinal.Compare(previous, row.Owner) >= 0
                    || !Text(row.Controller) || !Text(row.Root) || !Text(row.Avatar) || !Text(row.BinderClip)
                    || !Keys(row.Renderers) || !Keys(row.Clearing) || !Keys(row.CachedClips)
                    || row.Fields == null || row.Fields.Length != Sizes.Length || row.Fields.Where((part, index) => part == null || part.Length != Sizes[index]).Any()
                    || row.Padding == null || row.Padding.Length != PaddingSlots.Length || row.Cached == null || row.Cached.Length > MaximumEntries)
                    throw Error("Animator owner, native field range or resource shape differs");
                var motion = row.Fields[4];
                ValidateHandles(row.Binder);
                if (row.Evaluation == null || row.Evaluation.Length != 2) throw Error("main/sampling evaluation domain differs");
                foreach (var value in row.Evaluation) ValidateEvaluation(value);

                previous = row.Owner;
                foreach (var playable in new[] { row.Primary }.Concat(row.Cached)) {
                    if (playable < -1 || playable >= data.Playables.Length) throw Error("controller playable alias is outside its typed directory");
                    if (playable >= 0) {
                        if (!used.Contains(playable) && playable != used.Count) throw Error("controller playable first-use order differs");
                        used.Add(playable);
                    }
                }
                if (row.Graph < -1 || row.Graph > i || row.StaleGraph && row.Graph >= 0) throw Error("graph alias/version differs");
                if (row.Graph == i) graphs.Add(i);
                if (row.Graph >= 0 && !graphs.Contains(row.Graph)) throw Error("graph alias has no first owner");
                if (row.Output != -1 && (row.Output != i || row.StaleOutput || row.Graph < 0)) throw Error("Animator internal output binding differs");
            }
            if (used.Count != data.Playables.Length) throw Error("unowned controller playable record");
            var assets = new HashSet<string>(StringComparer.Ordinal); previous = null;
            foreach (var asset in data.Assets) {
                if (asset == null || !Text(asset.Binding) || asset.Binding.Length == 0 || asset.Binding == Unbound
                    || StringComparer.Ordinal.Compare(previous, asset.Binding) >= 0 || !assets.Add(asset.Binding)
                    || asset.Kind != "controller" && asset.Kind != "override" && asset.Kind != "unknown"
                    || !Text(asset.Base) || !Keys(asset.ObserverPeers) || asset.ObserverPeers.Length > MaximumEntries || !Keys(asset.Clips) || !Keys(asset.OriginalClips) || !Keys(asset.OverridePairs)
                    || asset.OverridePairs.Length % 2 != 0 || asset.Kind != "override" && (asset.Base.Length != 0 || asset.OriginalClips.Length + asset.OverridePairs.Length != 0))
                    throw Error("controller asset binding directory differs");
                previous = asset.Binding;
            }
            foreach (var player in data.Playables) {
                if (player == null || player.Clock == null || player.Clock.Length != 58 || !Text(player.Controller)) throw Error("playable clock/controller shape differs");
                if (player.Memory != null) ValidateMemory(player.Memory);
                ValidateRuntime(player.Runtime);
                ValidateControllerDomains(player.Runtime,player.Memory);
            }
            var roots = data.Rows.Select(row => row.Controller).Concat(data.Playables.Select(player => player.Controller));
            var reachable = new HashSet<string>(StringComparer.Ordinal);
            var byKey = data.Assets.ToDictionary(asset => asset.Binding, StringComparer.Ordinal);
            foreach (var root in roots) {
                var key = root;
                while (key.Length > 0 && key != Unbound && reachable.Add(key)) {
                    if (!byKey.TryGetValue(key, out var asset)) throw Error("referenced controller asset absent");
                    key = asset.Base;
                }
            }
            if (reachable.Count != assets.Count) throw Error("unreferenced controller asset record");
            var registered = new HashSet<int>();
            foreach (var list in new[] { data.Registry.Playing, data.Registry.Stopped }) {
                if (list == null || list.Length > MaximumEntries || !data.Registry.Present && list.Length != 0) throw Error("director list shape differs");
                foreach (var owner in list)
                    if (owner != -1 && (!graphs.Contains(owner) || !registered.Add(owner))) throw Error("graph appears in the wrong native registration directory");
            }
            if (data.Rows.Length == 0 && data.Registry.Present) throw Error("director observed outside Animator domain");
            if(data.Registry.Producers!=null) {if(!data.Registry.Present) throw Error("Director producer storage has no manager");ValidateProducers(data.Registry.Producers);}
            ValidateResidual(data); ValidateAvatar(data); ValidateQueues(data); _ = ControllerOwnedAvatarValues(data);
            ValidateUsers(data.Users);
            if (data.Graphs==null || data.Graphs.Length>data.Rows.Length) throw Error("owned playable graph directory differs");
            var owners = new HashSet<int>();
            foreach (var graph in data.Graphs) {
                ValidateTopology(graph);
                if (graph.Owner>=data.Rows.Length || data.Rows[graph.Owner].Graph!=graph.Owner || !owners.Add(graph.Owner)) throw Error("typed graph has no unique Animator owner");
                foreach (var node in graph.Nodes)
                    if (node.Controller>=data.Playables.Length || node.Controller>=0 && (node.Vtable!=PlayableVtable || !node.Clock.SequenceEqual(data.Playables[node.Controller].Clock)
                            || !node.AnimationFlags.SequenceEqual(data.Playables[node.Controller].Runtime.AnimationFlags)))
                        throw Error("playable controller alias/type/time mismatch");
            }
            ValidateControllerDependencyOwners(data);
        }
        internal static void ValidateOwners(Data data, ResidentObjectDirectory.Snapshot directory)
        {
            Validate(data);
            var actual = ResidentObjectDirectory.TypeRecord.Capture(typeof(Animator)).Key;
            var expected = directory.Objects.SelectMany(row => row.Components).Where(row => row.Ownership == ResidentComponentOwnership.Gameplay && row.Type.Key == actual)
                .Select(row => row.Key).OrderBy(key => key, StringComparer.Ordinal);
            if (!data.Rows.Select(row => row.Owner).SequenceEqual(expected)) throw Error("actual Animator directory coverage differs");
        }
        internal static string[] RequiredCoverageGaps(Data data)
        {
            Validate(data);
            var gaps = new SortedSet<string>(ScalarGaps(data),StringComparer.Ordinal);
            // Even a disabled Animator retains binder, evaluation, user-list
            // and manager state. Absence of a public current state cannot
            // establish that these domains are cold.
            foreach (var row in data.Rows) foreach (var gap in ResidualGaps(row.Retained,row)) gaps.Add(gap);
            if (data.Graphs.Length!=data.Rows.Where((r,i)=>r.Graph==i).Count()) gaps.Add("actual-graph-topology-directory-incomplete");
            foreach (var graph in data.Graphs) foreach (var gap in GraphGaps(graph)) gaps.Add(gap);
            var userSlots = new HashSet<string>(data.Users.Select(s => s.Key),StringComparer.Ordinal);
            if (data.Rows.Any(row => AnimatorUserOffsets.Any(offset => !userSlots.Contains(UserKey(row.Owner,offset))))) gaps.Add("animator-user-list-directory-incomplete");
            if (data.Users.Any(s => s.Entries.Any(e => e.Peer == Unbound))) gaps.Add("user-list-peer-outside-verified-rtti-field-directory");
            if (data.Users.Any(s=>!s.Single && !OwnedStorage(s.Storage))) gaps.Add("user-list-vector-owner");
            if (data.Users.Any(s=>s.EntryPadding || !s.Single && !Compact(s.Storage,s.Entries.Length))) gaps.Add("user-list-storage-requires-common-canon");
            foreach (var row in data.Rows) foreach (var value in row.Evaluation) foreach (var gap in EvaluationGaps(value)) gaps.Add(gap);
            foreach (var row in data.Rows) foreach (var gap in HandleGaps(row.Binder)) gaps.Add(gap);
            if (data.AvatarNodes.Any(node => node.References.Contains(Unbound))) gaps.Add("avatar-binding-without-resident-native-reference");
            var controllerOwned = ControllerOwnedAvatarValues(data);
            if (data.AvatarNodes.Where((node,i) => node.Kind == AvatarKind.Values && node.Extents[4] != 0 && node.Integers == null && !controllerOwned.Contains(i)).Any()) gaps.Add("avatar-integer-and-object-channel-remap");
            foreach (var gap in ValueChannelGaps(data)) gaps.Add(gap);
            if (data.Rows.Any(row => row.Inputs.Any(input => input.Root == Unbound))) gaps.Add("evaluation-transform-access-without-resident-binding");
            if (data.Rows.Any(row => row.Padding.Any(value => value != 0))) gaps.Add("animator-scalar-padding-requires-common-canon");
            foreach (var player in data.Playables)
                foreach (var gap in ControllerDependencyGaps(player)) gaps.Add(gap);
            foreach (var player in data.Playables) {
                var runtime = player.Runtime;
                if (!KnownStateIndices(player)) gaps.Add("controller-state-index-outside-constant-directory");
                if (runtime.Input != null && (runtime.Input.LayerMixer == UnknownEdge || runtime.Input.BehaviourPlayer == UnknownEdge)
                    || runtime.Workspace != null && runtime.Workspace.Machines.Any(machine => machine.PlayableInstance == UnknownEdge || machine.TriggerSlot == UnknownEdge || machine.ValueConstants == UnknownEdge))
                    gaps.Add("unbound-controller-input-or-workspace-edge");
                if (runtime.Workspace != null && runtime.Workspace.Machines.Any(machine => machine.Blends != null
                    && machine.Blends.Any(blend => blend.Constant == UnknownEdge || blend.Values == UnknownEdge)))
                    gaps.Add("unbound-blend-input-value-bank");
            }
            foreach(var gap in ProducerGaps(data)) gaps.Add(gap);
            var queues = data.Registry.Queues;
            if (queues.Flags[2] != 0 || queues.Flags[3] != 0) gaps.Add("director-active-stage-or-state-change");
            if (queues.Commands.Length != 0) gaps.Add("director-pending-atomic-command-stack");
            if (queues.Notifications.Concat(queues.Deferred).Concat(queues.Commands).Any(h => h.State >= 2 || h.State == 1 && h.Graph < 0))
                gaps.Add("director-queued-handle-without-live-owned-graph");
            if (queues.Stages.Any(s => s.Parallel.Concat(s.Serial).Any(j => j.Graph < 0) || s.Groups.Any(g => g.CallbackRva < 0 || g.Outputs.Any(o => o.Animator < 0))))
                gaps.Add("director-external-stage-graph-output-or-callback");
            if (data.Registry.Playing.Concat(data.Registry.Stopped).Contains(-1)) gaps.Add("external-director-graph-identity");
            if (data.Rows.Any(row => row.StaleGraph)) gaps.Add("stale-playable-graph-handle");
            if (data.Rows.Any(row => row.StaleOutput)) gaps.Add("unresolved-internal-animation-output-handle-or-owner");
            if (data.Rows.Any(row => row.Graph >= 0) && !data.Registry.Present) gaps.Add("graph-directory-without-director");
            if (data.Assets.Any(asset=>!asset.ObserverDirectoryCaptured)) gaps.Add("controller-asset-observer-directory-not-captured");
            if (data.Assets.Any(asset=>asset.ObserverPeers.Contains(Unbound))) gaps.Add("controller-asset-observer-directory-external-owner");
            if (data.Assets.Any(asset=>asset.Kind == "unknown")) gaps.Add("controller-asset-unknown-native-type");
            if (data.Rows.SelectMany(row => new[] {row.Avatar, row.Controller, row.Root, row.BinderClip}
                    .Concat(row.Renderers).Concat(row.Clearing).Concat(row.CachedClips)).Concat(data.Playables.Select(player => player.Controller))
                .Concat(data.Assets.SelectMany(asset => new[] {asset.Base}.Concat(asset.Clips).Concat(asset.OriginalClips).Concat(asset.OverridePairs))).Contains(Unbound))
                gaps.Add("native-resource-without-loaded-binding");
            return gaps.ToArray();
        }
        private static void RequireTargetControllerAssets(Data source, Data target, Func<string, NativeObject> resolve, Func<object, string?> bind)
        {
            var beforeByKey = target.Assets.ToDictionary(asset => asset.Binding, StringComparer.Ordinal);
            foreach (var asset in source.Assets) {
                var ctrlPtr = ResidentNativeAssetReference.ResolveTyped(asset.Binding, typeof(RuntimeAnimatorController), resolve);
                if (ResidentNativeAssetReference.BindPointer(ctrlPtr, typeof(RuntimeAnimatorController), bind) != asset.Binding)
                    throw Error("target controller binding failed reverse lookup");
                if (asset.Base.Length != 0) {
                    var basePtr = ResidentNativeAssetReference.ResolveTyped(asset.Base, typeof(RuntimeAnimatorController), resolve);
                    if (ResidentNativeAssetReference.BindPointer(basePtr, typeof(RuntimeAnimatorController), bind) != asset.Base)
                        throw Error("target controller base failed reverse lookup");
                }
                foreach (var clipKey in asset.Clips.Concat(asset.OriginalClips).Concat(asset.OverridePairs)) {
                    if (clipKey.Length == 0) continue;
                    var clipPtr = ResidentNativeAssetReference.ResolveTyped(clipKey, typeof(AnimationClip), resolve);
                    if (ResidentNativeAssetReference.BindPointer(clipPtr, typeof(AnimationClip), bind) != clipKey)
                        throw Error("target controller clip failed reverse lookup");
                }
                if (beforeByKey.TryGetValue(asset.Binding, out var existing)
                    && (asset.Kind != existing.Kind || asset.Base != existing.Base || !asset.OverridePairs.SequenceEqual(existing.OverridePairs)))
                    throw Error("target controller base, clip directory or mutable override aliases differ");
            }
        }
        internal static void RequireControllerAssetEquivalence(Data source,Data target)
        {
            Validate(source); Validate(target);
            var (leftAssets, rightAssets) = AlignLazyOverrideCaches(source.Assets, target.Assets);
            var left = Json(leftAssets);
            var right = Json(rightAssets);
            if (left != right)
                throw Error("target controller base, clip directory or mutable override aliases differ: " + FirstJsonDiff(left, right));
        }
        internal static void RequireCompleteCoverage(Data data) => Reject(RequiredCoverageGaps(data));
        internal static void RequireCanonicalizableCoverage(Data data)
            => Reject(RequiredCoverageGaps(data).Where(gap => gap != "animator-scalar-padding-requires-common-canon" && gap != "binder-empty-capacity-requires-common-canon"
                && gap!="user-list-storage-requires-common-canon" && gap!="retained-vector-requires-common-canon" && gap!="recorder-frame-storage-requires-common-canon"
                && gap!="graph-root-retirement-vector-storage" && gap!="playable-vector-padding-requires-common-canon"));
        private static void Reject(IEnumerable<string> items)
        {
            var gaps = items.ToArray();
            if (gaps.Length != 0) throw Error("incomplete native Animator coverage: " + string.Join(", ", gaps));
        }
        private static string Json(object value) => JsonConvert.SerializeObject(value);
        private static Data Copy(Data data) => JsonConvert.DeserializeObject<Data>(Json(data)) ?? throw Error("Animator snapshot copy failed");
        private static Data WithAssets(Data data, ControllerAsset[] assets) => new Data {
            Version = data.Version, Rows = data.Rows, Playables = data.Playables, Assets = assets,
            AvatarNodes = data.AvatarNodes, Registry = data.Registry, Users = data.Users,
            ExtraMemories = data.ExtraMemories, Graphs = data.Graphs
        };
        private static (ControllerAsset[] Left, ControllerAsset[] Right) AlignLazyOverrideCaches(ControllerAsset[] source, ControllerAsset[] target)
        {
            if (source.Length != target.Length) return (source, target);
            var any = false;
            for (var i = 0; i < source.Length; i++) {
                var s = source[i]; var t = target[i];
                if (s.Kind == "override" && t.Kind == "override" && ((s.Clips.Length == 0) != (t.Clips.Length == 0) || (s.OriginalClips.Length == 0) != (t.OriginalClips.Length == 0))) {
                    any = true; break;
                }
            }
            if (!any) return (source, target);
            var srcByKey = source.ToDictionary(a => a.Binding, StringComparer.Ordinal);
            var dstByKey = target.ToDictionary(a => a.Binding, StringComparer.Ordinal);
            var left = (ControllerAsset[])source.Clone();
            var right = (ControllerAsset[])target.Clone();
            for (var i = 0; i < source.Length; i++) {
                var s = source[i]; var t = target[i];
                if (s.Kind != "override" || t.Kind != "override" || s.Binding != t.Binding || s.Base != t.Base
                    || s.ObserverDirectoryCaptured != t.ObserverDirectoryCaptured
                    || !s.ObserverPeers.SequenceEqual(t.ObserverPeers) || !s.OverridePairs.SequenceEqual(t.OverridePairs)) continue;
                var alignClips = false;
                if ((s.Clips.Length == 0) != (t.Clips.Length == 0)) {
                    var populated = s.Clips.Length != 0 ? s.Clips : t.Clips;
                    var expected = ExpectedOverrideClips(s.Clips.Length != 0 ? s : t, s.Clips.Length != 0 ? srcByKey : dstByKey);
                    if (expected != null && populated.SequenceEqual(expected)) alignClips = true;
                }
                var alignOriginal = false;
                if ((s.OriginalClips.Length == 0) != (t.OriginalClips.Length == 0)) {
                    var populated = s.OriginalClips.Length != 0 ? s.OriginalClips : t.OriginalClips;
                    var baseClips = BaseControllerClips(s.OriginalClips.Length != 0 ? s : t, s.OriginalClips.Length != 0 ? srcByKey : dstByKey);
                    if (baseClips != null && populated.Length == baseClips.Distinct(StringComparer.Ordinal).Count()
                        && new HashSet<string>(populated, StringComparer.Ordinal).SetEquals(baseClips)) alignOriginal = true;
                }
                if (!alignClips && !alignOriginal) continue;
                left[i] = CloneAsset(s, alignClips ? Array.Empty<string>() : s.Clips, alignOriginal ? Array.Empty<string>() : s.OriginalClips);
                right[i] = CloneAsset(t, alignClips ? Array.Empty<string>() : t.Clips, alignOriginal ? Array.Empty<string>() : t.OriginalClips);
            }
            return (left, right);
        }
        private static ControllerAsset CloneAsset(ControllerAsset asset, string[] clips, string[] originalClips) => new ControllerAsset {
            Binding = asset.Binding, Kind = asset.Kind, Base = asset.Base,
            Clips = clips, OriginalClips = originalClips, OverridePairs = asset.OverridePairs,
            ObserverDirectoryCaptured = asset.ObserverDirectoryCaptured, ObserverPeers = asset.ObserverPeers
        };
        private static string[]? BaseControllerClips(ControllerAsset asset, Dictionary<string, ControllerAsset> byKey, HashSet<string>? seen = null)
        {
            if (asset.Kind == "controller") return asset.Clips;
            if (asset.Base.Length == 0 || !byKey.TryGetValue(asset.Base, out var parent)) return null;
            seen ??= new HashSet<string>(StringComparer.Ordinal);
            if (!seen.Add(asset.Binding)) return null;
            if (parent.Kind == "controller") return parent.Clips;
            return parent.Clips.Length != 0 ? parent.Clips : ExpectedOverrideClips(parent, byKey, seen);
        }
        private static string[]? ExpectedOverrideClips(ControllerAsset asset, Dictionary<string, ControllerAsset> byKey, HashSet<string>? seen = null)
        {
            var baseClips = BaseControllerClips(asset, byKey, seen);
            if (baseClips == null) return null;
            var result = new string[baseClips.Length];
            for (var i = 0; i < baseClips.Length; i++) {
                var clip = baseClips[i];
                var replacement = clip;
                for (var j = 0; j < asset.OverridePairs.Length; j += 2) {
                    if (asset.OverridePairs[j] != clip) continue;
                    if (asset.OverridePairs[j + 1].Length != 0) replacement = asset.OverridePairs[j + 1];
                    break;
                }
                result[i] = replacement;
            }
            return result;
        }
        internal static void RequireEqual(Data source, Data target)
        {
            Validate(source); Validate(target);
            var (leftAssets, rightAssets) = AlignLazyOverrideCaches(source.Assets, target.Assets);
            var left = Json(ReferenceEquals(leftAssets, source.Assets) ? source : WithAssets(source, leftAssets));
            var right = Json(ReferenceEquals(rightAssets, target.Assets) ? target : WithAssets(target, rightAssets));
            if (left != right) throw Error("Animator independent readback differs: " + FirstJsonDiff(left, right));
        }
        private static string FirstJsonDiff(string leftJson, string rightJson)
        {
            var a = Newtonsoft.Json.Linq.JToken.Parse(leftJson);
            var b = Newtonsoft.Json.Linq.JToken.Parse(rightJson);
            string? Walk(Newtonsoft.Json.Linq.JToken x, Newtonsoft.Json.Linq.JToken y, string path) {
                if (x.Type != y.Type) return $"{path}: type {x.Type}!={y.Type}";
                if (x is Newtonsoft.Json.Linq.JObject ox && y is Newtonsoft.Json.Linq.JObject oy) {
                    foreach (var p in ox.Properties()) {
                        if (p.Name == "$id" || p.Name == "$ref") continue;
                        if (!oy.TryGetValue(p.Name, out var yp)) return $"{path}.{p.Name}: missing in target";
                        var r = Walk(p.Value, yp, $"{path}.{p.Name}"); if (r != null) return r;
                    }
                    foreach (var p in oy.Properties()) if (!ox.ContainsKey(p.Name) && p.Name != "$id" && p.Name != "$ref") return $"{path}.{p.Name}: extra in target";
                    return null;
                }
                if (x is Newtonsoft.Json.Linq.JArray ax && y is Newtonsoft.Json.Linq.JArray ay) {
                    if (ax.Count != ay.Count) return $"{path}: len {ax.Count}!={ay.Count}";
                    for (int i = 0; i < ax.Count; i++) { var r = Walk(ax[i], ay[i], $"{path}[{i}]"); if (r != null) return r; }
                    return null;
                }
                return Newtonsoft.Json.Linq.JToken.DeepEquals(x, y) ? null : $"{path}: {x.ToString(Formatting.None)}!={y.ToString(Formatting.None)}";
            }
            return Walk(a, b, "$") ?? "ref/id layout";
        }
        internal static Action Canon(ResidentObjectDirectory directory, ResidentMutationScope scope, Func<object, string?> bind, Action admitted)
        {
            scope.Require(); admitted(); var live=Observe(directory,bind); var data=live.Data; RequireCanonicalizableCoverage(data);
            var owners = data.Rows.Select(row => Owned(row.Owner, directory, scope)).ToArray();
            try {
                scope.Mutate(() => {
                    live.RequireFrozen(); live.Users?.Canon(admitted,key=>RequireUserOwner(directory,scope,key));
                    foreach (var graph in live.Topologies) graph.Canon(admitted, live.Data.Playables);
                    for (var index = 0; index < owners.Length; index++) {
                        admitted(); var pointer = M.Owner(owners[index]);
                        foreach (var offset in PaddingSlots) M.Write(IntPtr.Add(pointer, offset), new byte[1]);
                        data.Rows[index].Padding = new byte[PaddingSlots.Length];
                        CanonEmptyHandles(data.Rows[index].Binder,pointer,admitted);
                        CanonResidual(data.Rows[index].Retained!,data.Rows[index],pointer,admitted);
                    }
                });
                Action verify = () => { scope.Require(); admitted(); RequireEqual(data, Capture(directory, bind)); admitted(); };
                verify(); return verify;
            } catch (Exception error) { scope.Fault(error); throw; }
        }
        private static Animator Owned(string key, ResidentObjectDirectory directory, ResidentMutationScope scope)
        {
            if (!(directory.Resolve(key) is Animator owner) || owner.GetType() != typeof(Animator) || !scope.Owns(owner))
                throw Error("native Animator write outside owned directory");
            return owner;
        }
        private static void RequireUserOwner(ResidentObjectDirectory directory,ResidentMutationScope scope,string key)
        {
            scope.Require();
            if(directory.NativeBindings.TryGetValue(key,out var value) && value is Component component && !scope.Owns(component))
                throw Error("user-list user outside owned resident mutation scope");
        }
        internal static Plan Prepare(Data data, ResidentObjectDirectory directory, ResidentMutationScope scope,
            Func<string, NativeObject> resolve, Func<object, string?> bind, Action admitted) => new Plan(data, directory, scope, resolve, bind, admitted);
        internal sealed class Plan
        {
            private readonly Data source, before;
            private readonly ResidentObjectDirectory directory;
            private readonly ResidentMutationScope scope;
            private readonly Func<object, string?> bind;
            private readonly Action admitted;
            private readonly Action requireFrozen;
            private readonly Action restoreUsers;
            private readonly List<Action> restoreBound = new List<Action>();
            private readonly List<PendingWrite> writes = new List<PendingWrite>();
            private bool applied;
            internal Plan(Data data, ResidentObjectDirectory directory, ResidentMutationScope scope, Func<string, NativeObject> resolve, Func<object, string?> bind, Action admitted)
            {
                this.directory = directory; this.scope = scope; this.bind = bind; this.admitted = admitted;
                scope.Require(); admitted(); ValidateOwners(data, directory.CopySnapshot()); RequireCompleteCoverage(data);
                source = Copy(data);
                var live = Observe(directory, bind);
                EnsureTargetGraphs(source, ref live, directory, scope, resolve, bind, admitted);
                if (RequiredCoverageGaps(live.Data).Any())
                {
                    Canon(directory, scope, bind, admitted);
                    live = Observe(directory, bind);
                }
                before = live.Data; requireFrozen = live.RequireFrozen; RequireCompleteCoverage(before);
                var expected = Copy(before);
                if (source.Graphs.Length!=live.Topologies.Count) throw Error("target graph count differs");
                for (int i=0;i<source.Graphs.Length;i++) live.Topologies[i].Prepare(source.Graphs[i],writes);
                expected.Graphs=source.Graphs;
                if (source.ExtraMemories.Length!=before.ExtraMemories.Length) throw Error("recorder/sampling memory allocation shape differs");
                for (int i=0;i<source.ExtraMemories.Length;i++) PrepareMemory(source.ExtraMemories[i],live.Memories!.Pointers[i],writes);
                expected.ExtraMemories=source.ExtraMemories;
                restoreUsers=live.Users?.Prepare(source.Users,admitted,key=>RequireUserOwner(directory,scope,key)) ?? (()=>{}); expected.Users = source.Users;
                if (source.Rows.Length != expected.Rows.Length || source.Playables.Length != expected.Playables.Length) throw Error("target Animator topology differs");
                for (var index = 0; index < source.Rows.Length; index++) {
                    var row = source.Rows[index]; var target = expected.Rows[index];
                    var retained=row.Retained!; var actualRetained=target.Retained!;
                    restoreBound.Add(live.Retained[index].Prepare(retained,writes,admitted)); actualRetained.Bound=retained.Bound;actualRetained.Arrays[0]=retained.Arrays[0];
                    for (int i=0;i<retained.Frames.Length;i++) actualRetained.Frames[i].TimeBits=retained.Frames[i].TimeBits;
                    for (int i=0;i<retained.Jobs.Length;i++) actualRetained.Jobs[i].Version=retained.Jobs[i].Version;
                    actualRetained.Pose.Version=retained.Pose.Version;
                    var owner = Owned(row.Owner, directory, scope);
                    void HandleFlag(int offset,bool flag) {
                        var at = IntPtr.Add(M.Owner(owner),offset); var old = M.Read(at,4);
                        writes.Add(new PendingWrite(at,old,BitConverter.GetBytes((M.U32(old,0) & ~1U) | (flag ? 1U : 0U))));
                    }
                    HandleFlag(1544,row.GraphFlag); HandleFlag(1560,row.OutputFlag); target.GraphFlag = row.GraphFlag; target.OutputFlag = row.OutputFlag;
                    PrepareHandles(row.Binder,target.Binder,M.Owner(owner),resolve,bind,live.FloatObject,writes); target.Binder = row.Binder;
                    for (int domain = 0; domain < row.Evaluation.Length; domain++) {
                        live.Evaluation[index][domain].Prepare(row.Evaluation[domain],resolve,bind,writes);
                        target.Evaluation[domain].Cache = row.Evaluation[domain].Cache;
                    }
                    IntPtr Reference(string key,Type type) {
                        if (key.Length == 0) return IntPtr.Zero;
                        var pointer = ResidentNativeAssetReference.ResolveTyped(key,type,resolve);
                        if (ResidentNativeAssetReference.BindPointer(pointer,type,bind) != key) throw Error("target native Animator binding failed reverse lookup");
                        return pointer;
                    }
                    void SyncInstanceId(int offset, string wantedKey, string beforeKey, Type type, Action<string> assign) {
                        var ptr = Reference(wantedKey, type);
                        if (wantedKey == beforeKey) return;
                        int wantedId = ptr == IntPtr.Zero ? 0 : M.I32(M.Read(IntPtr.Add(ptr, 8), 4), 0);
                        var at = IntPtr.Add(M.Owner(owner), offset);
                        writes.Add(new PendingWrite(at, M.Read(at, 4), BitConverter.GetBytes(wantedId)));
                        assign(wantedKey);
                    }
                    SyncInstanceId(412, row.Controller, target.Controller, typeof(RuntimeAnimatorController), v => target.Controller = v);
                    SyncInstanceId(408, row.Avatar, target.Avatar, typeof(Avatar), v => target.Avatar = v);
                    SyncInstanceId(1568, row.Root, target.Root, typeof(Transform), v => target.Root = v);
                    Reference(row.BinderClip,typeof(AnimationClip));
                    foreach (var key in row.Renderers.Concat(row.Clearing)) Reference(key,typeof(Renderer));
                    foreach (var key in row.CachedClips) Reference(key,typeof(AnimationClip));
                    for (var field = 0; field < Sizes.Length; field++)
                        writes.Add(new PendingWrite(IntPtr.Add(M.Owner(owner), Offsets[field]), target.Fields[field], row.Fields[field]));
                    target.Fields = row.Fields;
                    for (int set = 0; set < row.Inputs.Length; set++) {
                        var wanted = row.Inputs[set]; var actual = target.Inputs[set];
                        if (wanted.Dormant != actual.Dormant) throw Error("input cache lifetime differs");
                        if (wanted.Dormant) continue;
                        int offset = set == 0 ? 1576 : 896;
                        var native = IntPtr.Add(M.Owner(owner), offset);
                        int[] flags = {0,24,40,41};
                        for (int slot = 0; slot < flags.Length; slot++)
                            writes.Add(new PendingWrite(IntPtr.Add(native, flags[slot]), new[] {actual.Flags[slot]}, new[] {wanted.Flags[slot]}));
                        writes.Add(new PendingWrite(IntPtr.Add(native,80), actual.Versions,wanted.Versions));
                        int[] links = {8,16,32,48,56};
                        foreach (int edgeIndex in new[] {1, 3, 4}) {
                            if (wanted.Edges[edgeIndex] == actual.Edges[edgeIndex]) continue;
                            var ptr = live.Avatars!.ResolveBorrowedEdge(wanted.Edges[edgeIndex], InputEdges[edgeIndex]);
                            var at = IntPtr.Add(native, links[edgeIndex]);
                            writes.Add(new PendingWrite(at, M.Read(at, 8), BitConverter.GetBytes(ptr.ToInt64())));
                            actual.Edges[edgeIndex] = wanted.Edges[edgeIndex];
                        }
                        if (wanted.Root != actual.Root || wanted.NullRootIndex != actual.NullRootIndex) {
                            byte[] access;
                            if (wanted.Root.Length != 0) {
                                var member = Reference(wanted.Root, typeof(Transform));
                                access = M.Read(IntPtr.Add(member, 56), 12);
                                if (M.Ptr(access, 0) == IntPtr.Zero) throw Error("evaluation input root hierarchy absent on target");
                            } else {
                                access = new byte[12];
                                Buffer.BlockCopy(BitConverter.GetBytes(wanted.NullRootIndex), 0, access, 8, 4);
                            }
                            var rootAt = IntPtr.Add(native, 64);
                            writes.Add(new PendingWrite(rootAt, M.Read(rootAt, 12), access));
                            actual.Root = wanted.Root; actual.NullRootIndex = wanted.NullRootIndex;
                        }
                        actual.Flags = wanted.Flags; actual.Versions = wanted.Versions;
                    }
                }
                for (var index = 0; index < source.Playables.Length; index++) {
                    var player = source.Playables[index]; var target = expected.Playables[index]; var pointer = live.Playables[index];
                    RequireControllerDependenciesMatch(player,target);
                    writes.Add(new PendingWrite(IntPtr.Add(pointer, 80), target.Clock, player.Clock)); target.Clock = player.Clock;
                    if ((player.Memory == null) != (target.Memory == null)) throw Error("target controller memory topology differs");
                    if (player.Memory != null) PrepareMemory(player.Memory, M.Ptr(IntPtr.Add(pointer, 312)), writes);
                    target.Memory = player.Memory;
                    PrepareRuntime(player.Runtime, pointer, writes); target.Runtime = player.Runtime;
                }
                // Reorder only within an identical registered set. Moving a
                // live graph between Play/Stop lists also changes commands,
                // stage queues and play state, which must be handled together.
                if (source.Registry.Present && before.Registry.Present) {
                    if(Json(source.Registry.Producers!)!=Json(before.Registry.Producers!)) throw Error("target Director service dependency or legacy producer state differs");
                    live.Queues!.Prepare(source.Registry.Queues,writes);
                    var pointers = live.Graphs.ToDictionary(pair => pair.Value, pair => pair.Key);
                    PrepareOrder(IntPtr.Add(live.Manager, 808), pointers, before.Registry.Playing, source.Registry.Playing, writes);
                    PrepareOrder(IntPtr.Add(live.Manager, 824), pointers, before.Registry.Stopped, source.Registry.Stopped, writes);
                    expected.Registry = source.Registry;
                }
                live.Avatars?.Prepare(source.AvatarNodes,writes,(key,isRootGameObject) => {
                    var type = isRootGameObject ? typeof(GameObject) : typeof(Transform);
                    var pointer = ResidentNativeAssetReference.ResolveTyped(key,type,resolve);
                    if (ResidentNativeAssetReference.BindPointer(pointer,type,bind) != key) throw Error("avatar binding target type or reverse identity differs");
                    return pointer;
                },ControllerOwnedAvatarValues(source), key => {
                    var pointer = ResidentNativeAssetReference.ResolveTyped(key,typeof(NativeObject),resolve);
                    if (ResidentNativeAssetReference.BindPointer(pointer,typeof(NativeObject),bind) != key) throw Error("avatar Object channel reverse identity differs");
                    return M.I32(M.Read(IntPtr.Add(pointer,8),4),0);
                }); expected.AvatarNodes = source.AvatarNodes;
                RequireTargetControllerAssets(source, before, resolve, bind);
                expected.Assets = source.Assets;
                RequireEqual(source, expected); admitted();
            }
            internal void Apply()
            {
                if (applied) throw Error("Animator restore is single-use");
                scope.Require(); admitted(); RequireEqual(before, Capture(directory, bind)); requireFrozen();
                try {
                    scope.Mutate(() => { foreach (var write in writes) { admitted(); write.Apply(); } foreach(var apply in restoreBound) apply(); restoreUsers(); });
                    applied = true; Verify();
                } catch (Exception error) { scope.Fault(error); throw; }
            }
            internal void Verify()
            {
                if (!applied) throw Error("Animator verification precedes apply");
                scope.Require(); admitted(); RequireEqual(source, Capture(directory, bind)); admitted();
            }
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NativeAnimatorCreateGraph(IntPtr animator);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NativeDirectorProcessCommands(IntPtr manager);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NativeDirectorRebuildPrepareFrameJobs(IntPtr manager);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NativeDirectorRegisterProcessJobs(IntPtr manager, IntPtr graph);

        private static void EnsureTargetGraphs(Data source, ref Live live, ResidentObjectDirectory directory,
            ResidentMutationScope scope, Func<string, NativeObject> resolve, Func<object, string?> bind, Action admitted)
        {
            var before = live.Data;
            if (source.Rows.Length != before.Rows.Length) return;
            var sourceGraphOwners = source.Graphs.Select(g => g.Owner).ToArray();
            var targetGraphOwners = live.Topologies.Select(t => t.Data.Owner).ToArray();
            if (sourceGraphOwners.SequenceEqual(targetGraphOwners))
            {
                if (live.Data.Registry.Present && (live.Data.Registry.Queues.Commands.Length != 0
                    || live.Data.Registry.Queues.Flags[5] != 0 || live.Data.Registry.Queues.Flags[6] != 0))
                    DrainDirectorCommands(ref live, directory, scope, bind, admitted);
                return;
            }

            var engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
            var createGraph = Marshal.GetDelegateForFunctionPointer<NativeAnimatorCreateGraph>(IntPtr.Add(engine, 0xdbcf0));
            var sourceGraphsByOwner = source.Graphs.ToDictionary(g => g.Owner);
            var targetSet = new HashSet<int>(targetGraphOwners);
            var sourceSet = new HashSet<int>(sourceGraphOwners);
            var missing = sourceGraphOwners.Where(o => !targetSet.Contains(o)).ToArray();
            var extra = targetGraphOwners.Where(o => !sourceSet.Contains(o)).ToArray();
            Modding.Logger.Log($"[HKTAS-ANIM] Graph topology mismatch: src={sourceGraphOwners.Length}, tgt={targetGraphOwners.Length}, missing={missing.Length}, extra={extra.Length}");

            int createdStopped = 0;
            foreach (var index in missing) {
                var sRow = source.Rows[index];
                if (!sourceGraphsByOwner.TryGetValue(index, out var sGraph)) continue;
                if (sGraph.Nodes.Length != 0 || string.IsNullOrEmpty(sRow.Controller)) {
                    Modding.Logger.Log($"[HKTAS-ANIM] Missing non-empty or controller-less graph row[{index}] owner={sRow.Owner} nodes={sGraph.Nodes.Length} ctrl={!string.IsNullOrEmpty(sRow.Controller)}");
                    continue;
                }
                var owner = Owned(sRow.Owner, directory, scope);
                var nativeAnimator = M.Owner(owner);
                if (M.Ptr(M.Read(nativeAnimator, 8), 0) != IntPtr.Add(engine, AnimatorVtable))
                    throw Error("native Animator vtable differs before graph initialization");
                var ctrlPtr = ResidentNativeAssetReference.ResolveTyped(sRow.Controller, typeof(RuntimeAnimatorController), resolve);
                var ctrlId = M.I32(M.Read(IntPtr.Add(ctrlPtr, 8), 4), 0);
                scope.Mutate(() => {
                    admitted();
                    if (M.I32(M.Read(IntPtr.Add(nativeAnimator, 412), 4), 0) != ctrlId)
                        M.Write(IntPtr.Add(nativeAnimator, 412), BitConverter.GetBytes(ctrlId));
                    M.Write(IntPtr.Add(nativeAnimator, Offsets[3]), sRow.Fields[3]);
                    createGraph(nativeAnimator);
                });
                createdStopped++;
            }
            if (createdStopped > 0 || (live.Data.Registry.Present && (live.Data.Registry.Queues.Commands.Length != 0
                || live.Data.Registry.Queues.Flags[5] != 0 || live.Data.Registry.Queues.Flags[6] != 0))) {
                DrainDirectorCommands(ref live, directory, scope, bind, admitted);
                Modding.Logger.Log($"[HKTAS-ANIM] Created {createdStopped} stopped PlayableGraphs; after observe tgt={live.Topologies.Count} (Playing=[{string.Join(",", live.Data.Registry.Playing)}], StoppedCount={live.Data.Registry.Stopped.Length})");
            }
        }

        private static void DrainDirectorCommands(ref Live live, ResidentObjectDirectory directory,
            ResidentMutationScope scope, Func<object, string?> bind, Action admitted)
        {
            var engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
            ResidentLoadedAssetIndex.RequireCode(engine, 0x899217, 57, "976393a424a3ca05e1f2eb448c66daf82e934dde398fe532ad36698fc072ad9a");
            ResidentLoadedAssetIndex.RequireCode(engine, 0x8998fb, 53, "6dad554502d685d9281b2c79f9ef0d7ffc6e03d5f6b964fca4c7cac5538a920a");
            ResidentLoadedAssetIndex.RequireCode(engine, 0x899b3b, 53, "6dd4d76ef941bb58f053720a19ba1638e654cf35c61e073122949086ba6500bd");
            var destroyFn = IntPtr.Add(engine, 0xb32f0);
            if (BitConverter.ToString(M.Read(destroyFn, 16)).Replace("-", "").ToLowerInvariant() != "40534883ec20488bd9488b094885c974")
                throw Error("native dynamic_array destructor ABI differs");
            var head = M.Read(IntPtr.Add(engine, 0x899210), 7);
            var hooked = head[0] == 0xe9 && head[5] == 0x48 && head[6] == 0x08;
            var unhooked = head[0] == 0x48 && head[1] == 0x8b && head[2] == 0xc4 && head[3] == 0x48 && head[4] == 0x89 && head[5] == 0x48 && head[6] == 0x08;
            if (!hooked && !unhooked) throw Error("native DirectorManager::ProcessCommands prologue differs");
            var rebuildHead = M.Read(IntPtr.Add(engine, 0x8998f0), 11);
            var rebuildTail = rebuildHead[5] == 0x81 && rebuildHead[6] == 0xec
                && rebuildHead[7] == 0x90 && rebuildHead[8] == 0 && rebuildHead[9] == 0 && rebuildHead[10] == 0;
            var rebuildHooked = rebuildHead[0] == 0xe9 && rebuildTail;
            var rebuildUnhooked = rebuildHead[0] == 0x48 && rebuildHead[1] == 0x8b && rebuildHead[2] == 0xc4
                && rebuildHead[3] == 0x55 && rebuildHead[4] == 0x48 && rebuildTail;
            if (!rebuildHooked && !rebuildUnhooked) throw Error("native DirectorManager::RebuildPrepareFrameJobs prologue differs");
            var regHead = M.Read(IntPtr.Add(engine, 0x899b30), 11);
            var regTail = regHead[5] == 0x81 && regHead[6] == 0xec
                && regHead[7] == 0x98 && regHead[8] == 0 && regHead[9] == 0 && regHead[10] == 0;
            var regHooked = regHead[0] == 0xe9 && regTail;
            var regUnhooked = regHead[0] == 0x40 && regHead[1] == 0x53 && regHead[2] == 0x41
                && regHead[3] == 0x55 && regHead[4] == 0x48 && regTail;
            if (!regHooked && !regUnhooked) throw Error("native DirectorManager::RegisterProcessJobs prologue differs");
            var manager = M.Ptr(M.Read(IntPtr.Add(engine, 0x196f0c0), 8), 0);
            if (manager != IntPtr.Zero) {
                var processCommands = Marshal.GetDelegateForFunctionPointer<NativeDirectorProcessCommands>(IntPtr.Add(engine, 0x899210));
                var rebuildJobs = Marshal.GetDelegateForFunctionPointer<NativeDirectorRebuildPrepareFrameJobs>(IntPtr.Add(engine, 0x8998f0));
                var registerProcessJobs = Marshal.GetDelegateForFunctionPointer<NativeDirectorRegisterProcessJobs>(IntPtr.Add(engine, 0x899b30));
                var destroyArray = (DestroyBinderArray)Marshal.GetDelegateForFunctionPointer(destroyFn, typeof(DestroyBinderArray));
                scope.Mutate(() => {
                    admitted();
                    processCommands(manager);
                    rebuildJobs(manager);
                    if (M.Read(IntPtr.Add(manager, 990), 1)[0] != 0) {
                        for (int stage = 0; stage < 7; stage++) {
                            var vec = IntPtr.Add(manager, 592 + stage * 24);
                            var first = M.Ptr(vec);
                            var last = M.Ptr(IntPtr.Add(vec, 8));
                            for (var elem = first; elem != last; elem = IntPtr.Add(elem, 40))
                                destroyArray(IntPtr.Add(elem, 8));
                            if (last != first) M.Write(IntPtr.Add(vec, 8), BitConverter.GetBytes(first.ToInt64()));
                        }
                        var playHead = IntPtr.Add(manager, 808);
                        for (var cur = M.Ptr(IntPtr.Add(playHead, 8)); cur != playHead; cur = M.Ptr(IntPtr.Add(cur, 8)))
                            registerProcessJobs(manager, cur);
                        M.Write(IntPtr.Add(manager, 990), new byte[] { 0 });
                    }
                });
            }
            live = Observe(directory, bind);
        }

        private static void PrepareOrder(IntPtr head, IDictionary<int,IntPtr> pointers, int[] before, int[] desired, List<PendingWrite> writes)
        {
            if (before.Contains(-1) || !before.OrderBy(key => key).SequenceEqual(desired.OrderBy(key => key)))
                throw Error("director graph membership requires native graph migration");
            if (before.SequenceEqual(desired)) return;
            var ordered = new[] {head}.Concat(desired.Select(key => pointers[key])).ToArray();
            for (var i = 0; i < ordered.Length; i++) {
                var prior = ordered[(i + ordered.Length - 1) % ordered.Length]; var next = ordered[(i + 1) % ordered.Length];
                var links = BitConverter.GetBytes(prior.ToInt64()).Concat(BitConverter.GetBytes(next.ToInt64())).ToArray();
                writes.Add(new PendingWrite(ordered[i], M.Read(ordered[i], 16), links));
            }
        }
    }
}
