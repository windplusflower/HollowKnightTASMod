using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Core.SceneCheckpoints;

namespace HollowKnightTAS.Companion.ViewModels
{
    public sealed partial class MainViewModel
    {
        // Lifetime follows Studio, not RuntimeSessionClient or a game-process registration.
        public HollowKnightTAS.Core.SceneCheckpoints.SceneCheckpointStore SceneCheckpoints { get; } = new();
        public SceneCheckpointClient CreateSceneCheckpointClient(RuntimeSessionClient runtime) => new(SceneCheckpoints, runtime);
        public static string FormatBranchId(int leafId) => leafId.ToString("x32");
        private StudioTimelineStore? worldlines;
        private TimelineTree? freshTimeline = new TimelineTree { Name = "当前空序列（未存档）" };
        private TimelineTree? selectedTimelineTree;
        private TimelineNode? selectedWorldline, selectedTimelineNode;
        private bool resettingTimelineSelectors;
        private string timelineTreeStatus = "选择叶子确定世界线，再选择节点恢复。恢复采用从第 0 帧重放。";
        public IReadOnlyList<TimelineTree> TimelineTrees => freshTimeline == null
            ? worldlines?.Library.Trees ?? new List<TimelineTree>()
            : new[] { freshTimeline }.Concat((worldlines?.Library.Trees ?? new List<TimelineTree>())
                .Where(t => t.Nodes.Count > 1 || t.Nodes[0].Movie.Length > 0)).ToArray();
        public string? SelectedTimelineTreeId
        {
            get => resettingTimelineSelectors ? null : selectedTimelineTree?.Id;
            set { if (value != null) SelectedTimelineTree = TimelineTrees.FirstOrDefault(t => t.Id == value); }
        }
        public int? SelectedWorldlineId
        {
            get => resettingTimelineSelectors ? null : selectedWorldline?.Id;
            set { if (value != null) SelectedWorldline = WorldlineLeaves.FirstOrDefault(n => n.Id == value); }
        }
        public TimelineTree? SelectedTimelineTree
        {
            get => selectedTimelineTree;
            set
            {
                if (value == null || gridApplying || value == selectedTimelineTree) return;
                _ = SwitchWorldlineAsync(value.Id, value.Leaves.Last().Id);
            }
        }
        public IReadOnlyList<TimelineNode> WorldlineLeaves => selectedTimelineTree?.Leaves.ToArray() ?? Array.Empty<TimelineNode>();
        public IReadOnlyList<TimelineNode> WorldlinePath => selectedTimelineTree != null && selectedWorldline != null
            ? selectedTimelineTree.PathTo(selectedWorldline.Id) : Array.Empty<TimelineNode>();
        public TimelineNode? SelectedWorldline
        {
            get => selectedWorldline;
            set
            {
                if (value == null || gridApplying || value == selectedWorldline) return;
                if (selectedTimelineTree != null) _ = SwitchWorldlineAsync(selectedTimelineTree.Id, value.Id);
            }
        }
        public TimelineNode? SelectedTimelineNode
        {
            get => selectedTimelineNode;
            set { if (value != null && value != selectedTimelineNode && !gridApplying) SelectTimelineNode(value.Id); }
        }
        public string TimelineTreeStatus { get => timelineTreeStatus; private set => Set(ref timelineTreeStatus, value); }
        public string TimelineNodeDetail => selectedTimelineNode == null ? "" :
            $"{selectedTimelineNode.Label}\n{(selectedTimelineNode.ParentId is int p ? "父节点：" + p : "所有世界线的起点")}\n{selectedTimelineNode.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        public ICommand SaveTimelineNodeCommand => new AsyncRelayCommand(() => FrameMenuAsync("save", -1), () => !gridApplying && fullRunMovies?.IsPending == true && fullRunMovies.Mode != "Unarmed");
        public ICommand RestoreTimelineNodeCommand => new AsyncRelayCommand(RestoreTimelineSelectionAsync, () => !gridApplying && selectedTimelineNode != null && selectedTimelineTree != freshTimeline && fullRunMovies?.IsPending == true);
        public ICommand DeleteTimelineNodeCommand => new RelayCommand(DeleteTimelineSelection, () => !gridApplying && selectedTimelineNode != null && selectedTimelineTree != freshTimeline);
        public ICommand BindTimelineQuickSlotCommand => new RelayCommand(BindSelectedTimelineQuickSlot,
            () => !gridApplying && !quickSlotBusy && SelectedQuickSlot >= 0 && SelectedQuickSlot < 10
                && selectedTimelineTree != freshTimeline && selectedTimelineNode?.Movie.Length > 0);

        private void BindSelectedTimelineQuickSlot()
        {
            try
            {
                if (!BindTimelineQuickSlotCommand.CanExecute(null)) return;
                BindTimelineSlot(SelectedQuickSlot);
                QuickSlotStatus = $"F{SelectedQuickSlot + 1} 已绑定世界线 {selectedWorldline!.Id} · {selectedTimelineNode!.Label}。";
            }
            catch (Exception ex) { QuickSlotStatus = "绑定失败：" + ex.Message; }
        }

        private void BindTimelineSlot(int slot)
        {
            var treeId = selectedTimelineTree!.Id;
            var nodeId = selectedTimelineNode!.Id;
            var leafId = selectedWorldline!.Id;
            worldlines!.Update(library => library.QuickSlots[slot] = $"{treeId}:{nodeId}:{leafId}");
            RefreshWorldlines(treeId, leafId, nodeId);
        }
        public void InitializeWorldlines()
        {
            if (worldlines != null) return;
            try
            {
                worldlines = new StudioTimelineStore();
                if (freshTimeline != null)
                {
                    selectedTimelineTree = freshTimeline;
                    selectedWorldline = freshTimeline.Nodes[0];
                    selectedTimelineNode = selectedWorldline;
                    NotifyWorldlines();
                }
                else RefreshWorldlines(worldlines.Library.ActiveTreeId);
            }
            catch (Exception ex) { worldlines = null; TimelineTreeStatus = "时间线加载失败（原文件保留）：" + ex.Message; }
        }
        private void NotifyWorldlines()
        {
            NotifyVideoRange();
            OnPropertyChanged(nameof(TimelineTrees));
            OnPropertyChanged(nameof(WorldlineLeaves));
            OnPropertyChanged(nameof(WorldlinePath));
            void Selection()
            {
                // A rejected transient null from ItemsSource replacement leaves WPF's
                // source-value cache unchanged. Explicitly reset that cache before rebinding.
                resettingTimelineSelectors = true;
                OnPropertyChanged(nameof(SelectedTimelineTreeId));
                OnPropertyChanged(nameof(SelectedWorldlineId));
                resettingTimelineSelectors = false;
                OnPropertyChanged(nameof(SelectedTimelineTreeId));
                OnPropertyChanged(nameof(SelectedWorldlineId));
                OnPropertyChanged(nameof(SelectedTimelineTree));
                OnPropertyChanged(nameof(SelectedWorldline));
                OnPropertyChanged(nameof(SelectedTimelineNode));
            }
            Selection();
            // ItemsSource replacement can clear a Selector after the first notification.
            if (Application.Current != null) Application.Current.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(Selection));
            OnPropertyChanged(nameof(TimelineNodeDetail));
            OnPropertyChanged(nameof(SaveTimelineNodeCommand)); OnPropertyChanged(nameof(RestoreTimelineNodeCommand));
            OnPropertyChanged(nameof(DeleteTimelineNodeCommand));
            OnPropertyChanged(nameof(BindTimelineQuickSlotCommand));
            if (quickSlots != null) RenderQuickSlots();
        }
        private void RefreshWorldlines(string? treeId = null, int? leafId = null, int? nodeId = null)
        {
            selectedTimelineTree = worldlines!.Library.Trees.FirstOrDefault(t => t.Id == treeId) ?? worldlines.Library.Trees.Last();
            selectedWorldline = selectedTimelineTree.Leaves.FirstOrDefault(n => n.Id == leafId) ?? selectedTimelineTree.Leaves.Last();
            selectedTimelineNode = selectedTimelineTree.PathTo(selectedWorldline.Id).FirstOrDefault(n => n.Id == nodeId) ?? selectedWorldline;
            NotifyWorldlines();
        }
        private void StartTimeline(
            string movie,
            TimelineTree? importedTimeline = null,
            IReadOnlyList<SceneCheckpointPacket>? importedCheckpoints = null)
        {
            InitializeWorldlines();
            if (worldlines == null) throw new InvalidOperationException(TimelineTreeStatus);
            string id = "";
            if (importedTimeline != null)
            {
                worldlines.Update(library =>
                {
                    var clone = JsonSerializer.Deserialize<TimelineTree>(JsonSerializer.Serialize(importedTimeline))
                        ?? throw new InvalidDataException("时间线数据为空。");
                    clone.InitialSavesId = sequenceInitialSaves?.Id ?? clone.InitialSavesId;
                    if (sequenceInitialSaves != null)
                        clone.OriginalHashes = new Dictionary<string, string>(sequenceInitialSaves.Hashes);
                    else if (fullRunMovies?.IsPending == true)
                        clone.OriginalHashes = new Dictionary<string, string>(fullRunMovies.OriginalHashes);

                    var existingIndex = library.Trees.FindIndex(t => t.Id == clone.Id);
                    if (existingIndex >= 0) library.Trees[existingIndex] = clone;
                    else
                    {
                        var emptyIndex = library.Trees.FindIndex(t => t.Nodes.Count == 1 && t.Nodes[0].Movie.Length == 0);
                        if (emptyIndex >= 0) library.Trees[emptyIndex] = clone;
                        else library.Trees.Add(clone);
                    }
                    id = library.ActiveTreeId = clone.Id;
                });
                if (importedCheckpoints != null)
                {
                    foreach (var pkt in importedCheckpoints)
                        _ = SceneCheckpoints.ImportPacket(pkt);
                }
                freshTimeline = null;
                var activeTree = worldlines.Library.Trees.Single(t => t.Id == id);
                var selectedLeaf = activeTree.Leaves.FirstOrDefault(n => n.Movie == movie)
                    ?? activeTree.Leaves.Last();
                activeDraftTree = id;
                activeDraftTip = selectedLeaf.IsBranchTip ? selectedLeaf.Id : null;
                SceneCheckpoints.ActivateBranch(id, FormatBranchId(selectedLeaf.Id));
                RefreshWorldlines(id, selectedLeaf.Id, selectedLeaf.Id);
                lastSavedBranchMovie = null; lastSavedBranchNative = -1;
                return;
            }

            worldlines.Update(library =>
            {
                var tree = library.Trees.FirstOrDefault(t => t.Nodes.Count == 1 && t.Nodes[0].Movie.Length == 0) ?? new TimelineTree();
                if (!library.Trees.Contains(tree)) library.Trees.Add(tree);
                tree.Nodes[0].Movie = movie;
                tree.InitialSavesId = sequenceInitialSaves?.Id ?? "";
                if (sequenceInitialSaves != null) tree.OriginalHashes = new Dictionary<string, string>(sequenceInitialSaves.Hashes);
                else if (fullRunMovies?.IsPending == true) tree.OriginalHashes = new Dictionary<string, string>(fullRunMovies.OriginalHashes);
                id = library.ActiveTreeId = tree.Id;
            });
            freshTimeline = null;
            RefreshWorldlines(id);
            activeDraftTree = id; activeDraftTip = null;
            lastSavedBranchMovie = null; lastSavedBranchNative = -1;
        }
        private void SaveTimelineSnapshot(FrameSave snapshot, FrameSave observed)
        {
            InitializeWorldlines();
            if (worldlines == null) throw new InvalidOperationException(TimelineTreeStatus);
            var id = activeDraftTree ?? worldlines.Library.ActiveTreeId;
            int nodeId = 0;
            int? tipId = activeDraftTip;
            var oldTip = activeDraftTip;
            var candidate = TimelineTree.Parse(snapshot.Movie);
            var actual = TimelineTree.Parse(observed.Movie);
            var verified = !draftRequiresRestart && snapshot.Frame <= observed.Frame
                && HollowKnightTAS.Core.Movie.MovieV2Prefix.Matches(actual, candidate, snapshot.Frame);
            worldlines.Update(library =>
            {
                var tree = library.Trees.FirstOrDefault(t => t.Id == id) ?? library.Trees.Last();
                id = library.ActiveTreeId = tree.Id;
                // Only an observed run can advance the mutable tip. A selected future
                // frame is a bookmark, not evidence that the game reached that frame.
                if (!draftRequiresRestart)
                {
                    var tipMovie = observed.Frame <= InputGridEditor.Count(candidate)
                        && HollowKnightTAS.Core.Movie.MovieV2Prefix.Matches(actual, candidate, observed.Frame)
                        ? snapshot.Movie : observed.Movie;
                    tipId = tree.UpdateTip(observed.Frame, tipMovie, snapshot.OriginalHashes, tipId).Id;
                    SyncTreeCheckpoints(tree, observed.Frame, tipMovie, oldTip, tipId);
                }
                nodeId = tree.Add(snapshot.Frame, snapshot.Movie, snapshot.OriginalHashes, !verified).Id;
                var tip = tree.Nodes.FirstOrDefault(n => n.Id == tipId && n.IsBranchTip);
                if (tip != null) tree.UpdateTip(tip.Frame, tip.Movie, snapshot.OriginalHashes, tip.Id);
                if (verified)
                {
                    SyncTreeCheckpoints(tree, snapshot.Frame, snapshot.Movie, oldTip, tipId ?? nodeId);
                }
            });
            activeDraftTip = tipId;
            activeDraftTree = id;
            var savedTree = worldlines.Library.Trees.Single(t => t.Id == id);
            var visibleLeaf = tipId.HasValue && savedTree.PathTo(tipId.Value).Any(n => n.Id == nodeId)
                ? tipId.Value : savedTree.Leaves.Last(n => savedTree.PathTo(n.Id).Any(p => p.Id == nodeId)).Id;
            RefreshWorldlines(id, visibleLeaf, nodeId);
            GridStatus = TimelineTreeStatus = $"已保存第 {snapshot.Frame} 帧的恢复节点；游戏位置未改变。";
        }
        public void SelectTimelineNode(int id)
        {
            if (gridApplying || selectedTimelineTree == null) return;
            var node = selectedTimelineTree.Nodes.Single(n => n.Id == id);
            if (selectedTimelineTree.Leaves.Any(n => n.Id == id)
                && (selectedWorldline?.Id != id || activeDraftTree != selectedTimelineTree.Id))
            {
                _ = SwitchWorldlineAsync(selectedTimelineTree.Id, id);
                return;
            }
            if (selectedWorldline == null || !selectedTimelineTree.PathTo(selectedWorldline.Id).Any(n => n.Id == id))
                selectedWorldline = selectedTimelineTree.Leaves.Last(n => selectedTimelineTree.PathTo(n.Id).Any(p => p.Id == id));
            selectedTimelineNode = node; NotifyWorldlines();
        }
        private TimelineNode? FindTimelineFrame(long frame)
            => WorldlinePath.LastOrDefault(n => n.Frame == frame && n.Movie.Length != 0);

        private async Task RestoreTimelineSelectionAsync()
        {
            if (gridApplying || selectedTimelineTree == null || selectedTimelineNode == null || selectedWorldline == null) return;
            SetGridApplying(true);
            try { await RestoreTimelineCoreAsync(selectedTimelineTree, selectedWorldline, selectedTimelineNode); }
            catch (Exception ex) { TimelineTreeStatus = Status = ex.Message; }
            finally { SetGridApplying(false); }
        }
        private async Task RestoreTimelineCoreAsync(TimelineTree tree, TimelineNode leaf, TimelineNode node)
        {
            if (fullRunMovies?.IsPending != true) throw new InvalidOperationException("请先从 Studio 启动受控游戏。");
            if (!tree.PathTo(leaf.Id).Any(n => n.Id == node.Id)) throw new InvalidOperationException("节点不属于所选世界线。");
            if (leaf.Movie.Length == 0) throw new InvalidOperationException("此起点尚未关联序列，请先新建 Movie。");
            fullRunMovies.VerifyOriginalSavesUnchanged();
            var initialSaves = GetTimelineInitialSaves(tree);
            var changedTree = activeDraftTree != tree.Id;
            await fullRunMovies.PauseForDocumentChangeAsync(CancellationToken.None);
            await CancelAutoSceneCheckpointCaptureAsync();
            await SaveCurrentBranchAsync(closing: true);

            activeDraftTree = tree.Id;
            activeDraftTip = leaf.IsBranchTip ? leaf.Id : null;
            selectedTimelineTree = tree;
            selectedWorldline = leaf;
            selectedTimelineNode = node;

            var available = FindBestSceneCheckpoint(tree.Id, node.Frame, TimelineTree.Parse(leaf.Movie));

            // Keep the chosen world's future inputs even when restoring an ancestor.
            await RestartDraftAtAsync(node.Frame, leaf.Movie, switchSequence: true, sourceSaves: initialSaves, saveCurrentBranch: false);
            if (changedTree) ResetSequenceSaveTarget(null);
            worldlines!.Update(library => library.ActiveTreeId = tree.Id);
            freshTimeline = null;
            RefreshWorldlines(tree.Id, leaf.Id, node.Id);
            activeDraftTree = tree.Id;
            activeDraftTip = leaf.IsBranchTip ? leaf.Id : null;
            lastSavedBranchMovie = null;
            TimelineTreeStatus = available != null
                ? $"世界线 {leaf.Id} · 已通过场景检查点快速恢复 {node.Label}（K={available.ResumeMovieFrame}）。"
                : $"世界线 {leaf.Id} · 已恢复 {node.Label}。";
        }
        private void DeleteTimelineSelection()
        {
            if (gridApplying || selectedTimelineTree == null || selectedTimelineNode == null) return;
            var tree = selectedTimelineTree; var node = selectedTimelineNode;
            var count = tree.Subtree(node.Id).Count - (node.Id == 0 ? 1 : 0);
            if (MessageBox.Show(UiText.T($"删除 {node.Label} 及其子树？将移除 {count} 个存档节点。此操作不可撤销。\n第 0 帧起点会保留；当前游戏和输入草稿不会改变。"),
                    UiText.T("删除时间线子树"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                IReadOnlyList<string> prunedCheckpoints = Array.Empty<string>();
                worldlines!.Update(library =>
                {
                    var target = library.Trees.Single(t => t.Id == tree.Id);
                    var removed = target.Subtree(node.Id);
                    prunedCheckpoints = target.Delete(node.Id);
                    foreach (var slot in library.QuickSlots.Where(p =>
                    {
                        var parts = p.Value.Split(':');
                        return parts[0] == tree.Id && parts.Skip(1).Any(id => int.TryParse(id, out var n) && removed.Contains(n));
                    }).Select(p => p.Key).ToArray())
                        library.QuickSlots.Remove(slot);
                });
                foreach (var checkpointId in prunedCheckpoints)
                {
                    try { SceneCheckpoints.RemoveCheckpoint(checkpointId); } catch { }
                }
                var removedList = tree.Subtree(node.Id);
                foreach (var removedId in removedList)
                {
                    try { SceneCheckpoints.DeleteBranch(tree.Id, FormatBranchId(removedId)); } catch { }
                }
                RefreshWorldlines(tree.Id); TimelineTreeStatus = $"已删除 {count} 个存档节点；其他世界线保留。";
            }
            catch (Exception ex) { TimelineTreeStatus = ex.Message; }
        }

        private async Task RunTimelineQuickSlotAsync(bool save)
        {
            if (gridApplying || quickSlotBusy) return;
            quickSlotBusy = true;
            try
            {
                InitializeWorldlines();
                if (worldlines == null) throw new InvalidOperationException(TimelineTreeStatus);
                var slot = SelectedQuickSlot;
                if (save)
                {
                    var count = worldlines.Library.Trees.Sum(t => t.Nodes.Count);
                    await FrameMenuAsync("save", -1);
                    if (count == worldlines.Library.Trees.Sum(t => t.Nodes.Count)) throw new InvalidOperationException(GridStatus);
                    BindTimelineSlot(slot);
                }
                else
                {
                    if (!worldlines.Library.QuickSlots.TryGetValue(slot, out var reference)) throw new InvalidOperationException("此快捷槽尚未保存时间线节点。");
                    var parts = reference.Split(':');
                    var tree = worldlines.Library.Trees.Single(t => t.Id == parts[0]);
                    var node = tree.Nodes.Single(n => n.Id == int.Parse(parts[1]));
                    var leaf = parts.Length >= 3
                        ? tree.Nodes.Single(n => n.Id == int.Parse(parts[2]))
                        : tree.Leaves.Last(n => tree.PathTo(n.Id).Any(p => p.Id == node.Id));
                    if (!tree.PathTo(leaf.Id).Any(p => p.Id == node.Id))
                        throw new InvalidOperationException("快捷槽节点已不属于绑定的世界线，请重新绑定。");
                    SetGridApplying(true);
                    try { await RestoreTimelineCoreAsync(tree, leaf, node); }
                    finally { SetGridApplying(false); }
                }
                QuickSlotStatus = $"F{slot + 1} 已{(save ? "保存" : "恢复")}时间线节点 · {selectedTimelineNode!.Label}。";
                RenderQuickSlots();
            }
            catch (Exception ex) { QuickSlotStatus = ex.Message; }
            finally { quickSlotBusy = false; }
        }
    }
}
