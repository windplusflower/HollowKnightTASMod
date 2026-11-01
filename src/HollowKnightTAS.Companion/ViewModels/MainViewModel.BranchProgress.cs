using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.Companion.ViewModels
{
    public sealed partial class MainViewModel
    {
        private bool savingBranch;
        private string? lastSavedBranchMovie;
        private long lastSavedBranchNative = -1;
        private string? activeDraftTree;
        private int? activeDraftTip;

        // Pause/close/file-switch persistence. A mutable tip stores the furthest
        // executed boundary; explicit saves stay immutable.
        public async Task SaveCurrentBranchAsync(bool closing = false)
        {
            if (closing) while (savingBranch) await Task.Delay(25);
            if (savingBranch || IsRestorePresentationFrozen || (gridApplying && !closing)
                || activeDraftTree == null || worldlines == null) return;
            savingBranch = true;
            try
            {
                if (closing && fullRunMovies?.IsPending == true)
                    await fullRunMovies.PauseForDocumentChangeAsync(CancellationToken.None);
                var terminal = fullRunMovies?.IsTerminal == true;
                if (fullRunMovies?.IsPending == true && startupBoot?.IsWaiting != true && !terminal)
                {
                    if (closing) throw new InvalidOperationException("未能暂停到安全帧，序列尚未保存。");
                    return;
                }
                var native = startupBoot?.NativeCompletedFrames ?? -1;
                if (lastSavedBranchMovie == MovieText && lastSavedBranchNative == native) return;
                var draft = movieEditor.ValidateAny(MovieText).V2Document;
                if (draft == null) return;
                var tree = worldlines.Library.Trees.Single(t => t.Id == activeDraftTree);
                var frame = tree.MatchingSavedFrame(MovieText);
                if (!terminal && !draftRequiresRestart && fullRunMovies?.IsPending == true && native > 0)
                {
                    var snapshot = await ReadReadyFrameSnapshotAsync();
                    var actual = TimelineTree.Parse(snapshot.Movie);
                    if (fullRunMovies.Mode == "Recording" && !gridHasUserEdits)
                    {
                        var total = InputGridEditor.Count(draft);
                        draft = new MovieV2Document(draft.SourceName, actual.Header,
                            actual.Runs.Concat(total > snapshot.Frame ? SliceV2(draft, snapshot.Frame, total - snapshot.Frame).Runs : Array.Empty<NativeFrameRun>()));
                        MovieText = gridSource = new MovieV2Codec().WriteCanonical(draft);
                        RefreshInputGrid();
                    }
                    if (snapshot.Frame <= InputGridEditor.Count(draft)
                        && MovieV2Prefix.Matches(actual, draft, snapshot.Frame)) frame = snapshot.Frame;
                    else
                    {
                        // Editing past input does not make the current game an
                        // execution of that edit. Keep its original reached tip.
                        StoreBranchTip(snapshot.Frame, snapshot.Movie);
                        activeDraftTip = null;
                    }
                }
                StoreBranchTip(frame, MovieText);
                lastSavedBranchMovie = MovieText;
                lastSavedBranchNative = native;
            }
            finally { savingBranch = false; }
        }

        private void StoreBranchTip(long frame, string movie)
        {
            var id = activeDraftTree!;
            var oldTip = activeDraftTip;
            int tipId = 0;
            worldlines!.Update(library =>
            {
                var tree = library.Trees.Single(t => t.Id == id);
                var hashes = fullRunMovies?.IsPending == true
                    ? fullRunMovies.OriginalHashes : tree.OriginalHashes;
                tipId = tree.UpdateTip(frame, movie, hashes, oldTip).Id;
                library.ActiveTreeId = id;
            });
            activeDraftTip = tipId;
            if (selectedTimelineTree?.Id == id)
                RefreshWorldlines(id, tipId, selectedTimelineNode?.Id);
        }

        private void UseSelectedWorldline()
        {
            if (selectedTimelineTree == null || selectedWorldline?.Movie.Length is not > 0) return;
            var initialSaves = GetTimelineInitialSaves(selectedTimelineTree);
            var changedTree = activeDraftTree != selectedTimelineTree.Id;
            SetSequenceInitialSaves(initialSaves);
            if (changedTree) ResetSequenceSaveTarget(null);
            var movie = selectedWorldline.Movie;
            activeDraftTree = selectedTimelineTree.Id;
            activeDraftTip = selectedWorldline.IsBranchTip ? selectedWorldline.Id : null;
            if (MovieText == movie && !changedTree) return;
            MovieText = gridSource = movie;
            gridHasUserEdits = false; earliestGridEdit = long.MaxValue;
            draftRequiresRestart = true;
            lastSavedBranchMovie = null;
            RefreshInputGrid();
            Status = "已切换世界线；播放从起点开始，恢复节点可回到该位置。";
        }

        private async Task SwitchWorldlineAsync(string treeId, int leafId)
        {
            if (gridApplying || sequenceSaving) return;
            SetGridApplying(true);
            try
            {
                await SaveCurrentBranchAsync(closing: true);
                selectedTimelineTree = TimelineTrees.Single(t => t.Id == treeId);
                selectedWorldline = selectedTimelineTree.Nodes.Single(n => n.Id == leafId);
                selectedTimelineNode = selectedWorldline;
                UseSelectedWorldline();
                SceneCheckpoints.ActivateBranch(treeId, leafId.ToString());
                NotifyWorldlines();
            }
            catch (Exception exception) { Status = TimelineTreeStatus = "切换世界线失败：" + exception.Message; }
            finally { SetGridApplying(false); }
        }
    }
}
