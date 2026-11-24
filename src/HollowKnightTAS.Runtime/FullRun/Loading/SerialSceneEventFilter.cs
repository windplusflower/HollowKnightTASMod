using System;

namespace HollowKnightTAS.Runtime.FullRun
{
    /// <summary>
    /// Filters and coalesces Unity SceneManager native callbacks so internal serial
    /// boundary staging scenes remain invisible to game and mod listeners while preserving
    /// vanilla callback ordering (sceneLoaded with active target -> activeSceneChanged).
    /// </summary>
    internal sealed class SerialSceneEventFilter<TScene>
    {
        private readonly Func<TScene, bool> isStagingScene;
        private readonly Func<TScene, string?> getSceneName;

        private bool hasPendingPreviousActiveScene;
        private TScene pendingPreviousActiveScene = default!;
        private bool deferringTargetActivationChange;
        private bool hasDeferredActiveSceneChange;
        private TScene deferredPreviousActiveScene = default!;
        private TScene deferredNewActiveScene = default!;

        internal SerialSceneEventFilter(
            Func<TScene, bool> isStagingScene,
            Func<TScene, string?> getSceneName)
        {
            this.isStagingScene = isStagingScene ?? throw new ArgumentNullException(nameof(isStagingScene));
            this.getSceneName = getSceneName ?? throw new ArgumentNullException(nameof(getSceneName));
        }

        internal bool HasPendingPreviousActiveScene => hasPendingPreviousActiveScene;
        internal bool IsDeferringTargetActivationChange => deferringTargetActivationChange;
        internal bool HasDeferredActiveSceneChange => hasDeferredActiveSceneChange;

        internal void Reset()
        {
            hasPendingPreviousActiveScene = false;
            pendingPreviousActiveScene = default!;
            deferringTargetActivationChange = false;
            hasDeferredActiveSceneChange = false;
            deferredPreviousActiveScene = default!;
            deferredNewActiveScene = default!;
        }

        internal void HandleActiveSceneChanged(
            TScene previousActiveScene,
            TScene newActiveScene,
            Action<TScene, TScene> invokeOriginal)
        {
            if (invokeOriginal == null) throw new ArgumentNullException(nameof(invokeOriginal));

            bool previousIsStaging = isStagingScene(previousActiveScene);
            bool newIsStaging = isStagingScene(newActiveScene);

            if (previousIsStaging && newIsStaging)
            {
                return;
            }

            if (!previousIsStaging && newIsStaging)
            {
                pendingPreviousActiveScene = previousActiveScene;
                hasPendingPreviousActiveScene = true;
                return;
            }

            if (previousIsStaging && !newIsStaging)
            {
                TScene resolvedPrevious = hasPendingPreviousActiveScene
                    ? pendingPreviousActiveScene
                    : previousActiveScene;
                pendingPreviousActiveScene = default!;
                hasPendingPreviousActiveScene = false;

                if (deferringTargetActivationChange)
                {
                    deferredPreviousActiveScene = resolvedPrevious;
                    deferredNewActiveScene = newActiveScene;
                    hasDeferredActiveSceneChange = true;
                    return;
                }

                invokeOriginal(resolvedPrevious, newActiveScene);
                return;
            }

            invokeOriginal(previousActiveScene, newActiveScene);
        }

        internal void HandleSceneLoaded(
            TScene scene,
            bool isSingleMode,
            string? serialTargetSceneName,
            Action<TScene> activateAndRefreshTarget,
            Action invokeOriginal,
            Action<TScene, TScene> invokeOriginalActiveSceneChanged)
        {
            if (activateAndRefreshTarget == null) throw new ArgumentNullException(nameof(activateAndRefreshTarget));
            if (invokeOriginal == null) throw new ArgumentNullException(nameof(invokeOriginal));
            if (invokeOriginalActiveSceneChanged == null) throw new ArgumentNullException(nameof(invokeOriginalActiveSceneChanged));

            if (isStagingScene(scene))
            {
                return;
            }

            if (isSingleMode)
            {
                pendingPreviousActiveScene = default!;
                hasPendingPreviousActiveScene = false;
            }

            if (serialTargetSceneName != null
                && string.Equals(getSceneName(scene), serialTargetSceneName, StringComparison.Ordinal))
            {
                try
                {
                    deferringTargetActivationChange = true;
                    try
                    {
                        activateAndRefreshTarget(scene);
                    }
                    finally
                    {
                        deferringTargetActivationChange = false;
                    }

                    invokeOriginal();
                }
                finally
                {
                    if (hasDeferredActiveSceneChange)
                    {
                        TScene deferredPrevious = deferredPreviousActiveScene;
                        TScene deferredNew = deferredNewActiveScene;
                        deferredPreviousActiveScene = default!;
                        deferredNewActiveScene = default!;
                        hasDeferredActiveSceneChange = false;
                        invokeOriginalActiveSceneChanged(deferredPrevious, deferredNew);
                    }
                }

                return;
            }

            invokeOriginal();
        }

        internal void HandleSceneUnloaded(TScene scene, Action<TScene> invokeOriginal)
        {
            if (invokeOriginal == null) throw new ArgumentNullException(nameof(invokeOriginal));

            if (isStagingScene(scene))
            {
                return;
            }

            invokeOriginal(scene);
        }
    }
}
