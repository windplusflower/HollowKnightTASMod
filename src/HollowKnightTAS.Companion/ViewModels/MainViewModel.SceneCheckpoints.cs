using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Core.SceneCheckpoints;

namespace HollowKnightTAS.Companion.ViewModels
{
    public sealed partial class MainViewModel
    {
        private readonly object autoCheckpointSync = new object();
        private CancellationTokenSource? autoCheckpointCts;
        private Task? autoCheckpointTask;
        private RuntimeSessionClient? autoCheckpointSession;

        private string GetActiveCheckpointBranchId() => FormatBranchId(activeDraftTip ?? selectedWorldline?.Id ?? 0);

        private void EnsureAutoSceneCheckpointCapture(RuntimeSessionClient session)
        {
            if (IsRestorePresentationFrozen || sequenceSaving
                || fullRunMovies?.IsPending != true
                || (fullRunMovies.Mode != "Recording" && fullRunMovies.Mode != "Replay")
                || activeDraftTree == null || activeDraftTree.Length != 32
                || !NativeExecutionProfile.SupportsSceneCheckpoints(MovieProtocolV2.SelectedNativeProfileId))
            {
                return;
            }

            lock (autoCheckpointSync)
            {
                if (autoCheckpointTask != null && !autoCheckpointTask.IsCompleted)
                {
                    if (ReferenceEquals(autoCheckpointSession, session)) return;
                    autoCheckpointCts?.Cancel();
                    return;
                }

                var cts = new CancellationTokenSource();
                autoCheckpointCts = cts;
                autoCheckpointSession = session;
                autoCheckpointTask = RunAutoSceneCheckpointCaptureAsync(session, cts.Token);
            }
        }

        private async Task RunAutoSceneCheckpointCaptureAsync(RuntimeSessionClient session, CancellationToken token)
        {
            while (!token.IsCancellationRequested
                && session.IsConnected
                && fullRunMovies?.IsPending == true
                && (fullRunMovies.Mode == "Recording" || fullRunMovies.Mode == "Replay")
                && !IsRestorePresentationFrozen
                && !sequenceSaving)
            {
                try
                {
                    var treeId = activeDraftTree;
                    var movies = fullRunMovies;
                    if (treeId == null || treeId.Length != 32 || movies?.IsPending != true)
                    {
                        return;
                    }

                    var scope = SceneCheckpoints.ActivateBranch(treeId, GetActiveCheckpointBranchId());
                    var binding = new SceneCheckpointBinding(
                        session.EnvironmentManifestSha256,
                        SceneCheckpointBinding.HashInitialSaves(movies.OriginalHashes),
                        MovieProtocolV2.SelectedNativeProfileId);
                    var client = CreateSceneCheckpointClient(session);
                    var lease = await client.ArmAsync(scope, binding, SceneCheckpointTrigger.AutoOnLoadBoundary, token);
                    var packet = await client.ReceiveAsync(lease, TimeSpan.FromMinutes(3), token);
                    RegisterCapturedCheckpointOnTimeline(treeId, packet);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    try { await Task.Delay(250, token); }
                    catch (OperationCanceledException) { return; }
                }
                catch (TimeoutException)
                {
                    try { await Task.Delay(250, token); }
                    catch (OperationCanceledException) { return; }
                }
                catch
                {
                    // Auto-capture is best-effort; branch changes, disconnects, or other errors exit and retry on the next poll tick.
                    return;
                }
            }
        }

        internal void RegisterCapturedCheckpointOnTimeline(string treeId, SceneCheckpointPacket packet)
        {
            InitializeWorldlines();
            if (worldlines == null || packet == null) return;
            var mode = fullRunMovies?.Mode;
            if (mode != "Replay" && gridHasUserEdits) return;
            var text = MovieText;
            if (string.IsNullOrWhiteSpace(text)) return;
            MovieV2Document doc;
            try { doc = TimelineTree.Parse(text); }
            catch { return; }
            if (doc.Runs.Sum(r => r.RepeatCount) < packet.Header.ResumeMovieFrame) return;

            worldlines.Update(library =>
            {
                var tree = library.Trees.FirstOrDefault(t => t.Id == treeId);
                tree?.RecordCheckpoint(packet.Header.CheckpointId, packet.Header.ResumeMovieFrame, packet.Header.TransitionId, text);
            });
        }

        private void SyncTreeCheckpoints(TimelineTree tree, long frame, string movie, int? oldTip, int? newTip)
        {
            if (tree == null || tree.Id.Length != 32 || frame < 0 || string.IsNullOrWhiteSpace(movie) || SceneCheckpoints == null) return;
            var oldBranch = FormatBranchId(oldTip ?? 0);
            var newBranch = newTip.HasValue ? FormatBranchId(newTip.Value) : oldBranch;
            var branchIds = new HashSet<string>(StringComparer.Ordinal) { oldBranch, newBranch };
            var leafId = newTip ?? oldTip ?? 0;
            if (tree.Nodes.Any(n => n.Id == leafId))
            {
                foreach (var ancestor in tree.PathTo(leafId))
                    branchIds.Add(FormatBranchId(ancestor.Id));
            }
            IReadOnlyList<SceneCheckpointHeader> headers;
            try { headers = SceneCheckpoints.CatalogTree(tree.Id); }
            catch { return; }
            foreach (var h in headers)
            {
                if (h.ResumeMovieFrame <= frame && branchIds.Contains(h.Scope.BranchId))
                {
                    tree.RecordCheckpoint(h.CheckpointId, h.ResumeMovieFrame, h.TransitionId, movie);
                }
            }
        }

        internal SceneCheckpointHeader? FindBestSceneCheckpoint(string? treeId, long targetFrame, MovieV2Document candidateMovie)
        {
            if (treeId == null || treeId.Length != 32 || targetFrame <= 0 || SceneCheckpoints == null) return null;
            var headers = SceneCheckpoints.CatalogTree(treeId);
            if (headers.Count == 0) return null;
            var tree = worldlines?.Library.Trees.FirstOrDefault(t => t.Id == treeId);
            return tree != null
                ? tree.FindNearestCheckpoint(targetFrame, candidateMovie, headers, GetActiveCheckpointBranchId())
                : headers.Where(h => h.RestoreQualified && h.ResumeMovieFrame <= targetFrame)
                    .OrderByDescending(h => h.ResumeMovieFrame)
                    .FirstOrDefault();
        }

        private async Task CancelAutoSceneCheckpointCaptureAsync()
        {
            CancellationTokenSource? cts;
            Task? task;
            lock (autoCheckpointSync)
            {
                cts = autoCheckpointCts;
                task = autoCheckpointTask;
                autoCheckpointCts = null;
                autoCheckpointTask = null;
                autoCheckpointSession = null;
            }

            if (cts != null)
            {
                try { cts.Cancel(); }
                catch { }
                cts.Dispose();
            }

            if (task != null)
            {
                try { await task; }
                catch { }
            }
        }
    }
}
