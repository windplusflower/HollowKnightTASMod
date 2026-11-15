using System;
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

        private void EnsureAutoSceneCheckpointCapture(RuntimeSessionClient session)
        {
            if (IsRestorePresentationFrozen || gridApplying || sequenceSaving
                || fullRunMovies?.IsPending != true
                || (fullRunMovies.Mode != "Recording" && fullRunMovies.Mode != "Replay")
                || activeDraftTree == null || activeDraftTip == null || activeDraftTree.Length != 32
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
            try
            {
                var treeId = activeDraftTree;
                var tipId = activeDraftTip;
                var movies = fullRunMovies;
                if (treeId == null || tipId == null || treeId.Length != 32 || movies?.IsPending != true)
                {
                    return;
                }

                var scope = SceneCheckpoints.ActivateBranch(treeId, FormatBranchId(tipId.Value));
                var binding = new SceneCheckpointBinding(
                    session.EnvironmentManifestSha256,
                    SceneCheckpointBinding.HashInitialSaves(movies.OriginalHashes),
                    MovieProtocolV2.SelectedNativeProfileId);
                var client = CreateSceneCheckpointClient(session);
                var lease = await client.ArmAsync(scope, binding, SceneCheckpointTrigger.AutoOnLoadBoundary, token);
                await client.ReceiveAsync(lease, TimeSpan.FromMinutes(3), token);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                // Auto-capture is best-effort; branch changes, disconnects, or timeouts retry cleanly on the next poll tick.
            }
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
