using System;
using System.Globalization;
using System.Linq;
using HollowKnightTAS.Core.ReplaySave;
using HollowKnightTAS.Runtime.Localization;
using UnityEngine;

namespace HollowKnightTAS.Runtime.ReplaySave
{
    public sealed class ReplaySaveMenuController : IDisposable
    {
        private readonly RuntimeReplaySaveManager manager;
        private readonly Action<string> logWarning;
        private ReplaySaveMenuRunner? runner;
        private ReplayRestoreHandle? activeRestore;
        private ReplayRestoreProgress? restoreProgress;
        private bool visible;
        private bool disposed;
        private bool gameManagerUpdateHookRegistered;
        private int selectedIndex;

        public ReplaySaveMenuController(
            RuntimeReplaySaveManager manager,
            Action<string> logWarning)
        {
            this.manager = manager
                           ?? throw new ArgumentNullException(nameof(manager));
            this.logWarning = logWarning
                              ?? throw new ArgumentNullException(nameof(logWarning));
            On.GameManager.Update += OnGameManagerUpdate;
            gameManagerUpdateHookRegistered = true;
        }

        public bool Visible => visible;
        private bool HasOriginNotice => manager.RecordingOriginStatus == "Preparing"
            || manager.RecordingOriginStatus == "Faulted";

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (gameManagerUpdateHookRegistered)
            {
                On.GameManager.Update -= OnGameManagerUpdate;
                gameManagerUpdateHookRegistered = false;
            }

            if (runner != null)
            {
                UnityEngine.Object.Destroy(runner.gameObject);
                runner = null;
            }
        }

        private void OnGameManagerUpdate(
            On.GameManager.orig_Update original,
            GameManager self)
        {
            original(self);
            if (disposed || runner != null)
            {
                return;
            }

            OnUpdate();
            if ((visible || activeRestore.HasValue || HasOriginNotice)
                && self != null
                && self.gameState == GlobalEnums.GameState.PLAYING
                && !self.IsInSceneTransition
                && HeroController.SilentInstance != null)
            {
                EnsureRuntimePump();
            }
        }

        private void EnsureRuntimePump()
        {
            if (disposed || runner != null)
            {
                return;
            }

            var gameObject = new GameObject(
                "HollowKnightTAS.ReplaySaveMenu");
            UnityEngine.Object.DontDestroyOnLoad(gameObject);
            runner = gameObject.AddComponent<ReplaySaveMenuRunner>();
            runner.Initialize(this);
        }

        internal void OnUpdate()
        {
            if (disposed)
            {
                return;
            }

            try
            {
                if (UnityEngine.Input.GetKeyDown(KeyCode.F8))
                {
                    visible = !visible;
                }

                if (UnityEngine.Input.GetKeyDown(KeyCode.F5))
                {
                    var result = manager.RequestManualSave(string.Empty);
                    if (!result.Accepted)
                    {
                        logWarning(
                            "T09 manual save request rejected: "
                            + result.Error);
                    }
                }

                PollRestore();
                if (!visible)
                {
                    return;
                }

                var entries = manager.Inspect();
                if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow))
                {
                    selectedIndex = Math.Max(0, selectedIndex - 1);
                }
                else if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow))
                {
                    selectedIndex = Math.Min(
                        Math.Max(0, entries.Count - 1),
                        selectedIndex + 1);
                }

                if (restoreProgress?.Phase
                    == ReplayRestorePhase.AwaitingOverwriteApproval)
                {
                    if (UnityEngine.Input.GetKeyDown(KeyCode.Y))
                    {
                        manager.ApproveRestoreOverwrite(
                            activeRestore!.Value,
                            true);
                    }
                    else if (UnityEngine.Input.GetKeyDown(KeyCode.N)
                             || UnityEngine.Input.GetKeyDown(KeyCode.Escape))
                    {
                        manager.ApproveRestoreOverwrite(
                            activeRestore!.Value,
                            false);
                    }

                    return;
                }

                if (restoreProgress?.Phase == ReplayRestorePhase.Paused)
                {
                    if (UnityEngine.Input.GetKeyDown(KeyCode.F9)
                        || UnityEngine.Input.GetKeyDown(KeyCode.Return))
                    {
                        manager.ResumeRestore(activeRestore!.Value);
                    }

                    return;
                }

                if (activeRestore.HasValue
                    && restoreProgress != null
                    && !restoreProgress.IsTerminal)
                {
                    if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
                    {
                        manager.CancelRestore(activeRestore.Value);
                    }

                    return;
                }

                if (UnityEngine.Input.GetKeyDown(KeyCode.T))
                {
                    var current = manager.AutoSavePolicy;
                    manager.SetAutoSavePolicy(
                        new AutoSavePolicy(
                            !current.Enabled,
                            current.IntervalMovieTicks,
                            current.RetentionCount));
                }

                if (entries.Count == 0)
                {
                    return;
                }

                selectedIndex = Math.Max(
                    0,
                    Math.Min(selectedIndex, entries.Count - 1));
                var selected = entries[selectedIndex];
                if (UnityEngine.Input.GetKeyDown(KeyCode.P)
                    && selected.Descriptor.Reason
                       == ReplaySaveReason.AutomaticInterval)
                {
                    manager.PinAsManual(
                        selected.Descriptor.ReplaySaveId);
                }

                if (UnityEngine.Input.GetKeyDown(KeyCode.R)
                    || UnityEngine.Input.GetKeyDown(KeyCode.Return))
                {
                    activeRestore = manager.BeginRestore(
                        selected.Descriptor.ReplaySaveId);
                    restoreProgress = manager.Poll(activeRestore.Value);
                }
            }
            catch (Exception exception)
            {
                logWarning(
                    "T09 replay-save menu action failed: "
                    + exception.GetType().Name
                    + ": "
                    + exception.Message);
            }
        }

        internal void OnGui()
        {
            if (disposed)
            {
                return;
            }

            if (HasOriginNotice)
            {
                var heading = manager.RecordingOriginStatus == "Faulted"
                    ? RuntimeUiText.T(
                        "TAS 录制准备失败：本次录制不可用，请查看详情。",
                        "TAS recording setup failed: recording is unavailable. See details.")
                    : RuntimeUiText.T(
                        "TAS 正在准备录制起点：准备期间游戏操作暂不可用，请等待。",
                        "TAS is preparing the recording origin: game input is temporarily unavailable. Please wait.");
                GUI.Box(new Rect(20f, Screen.height - 105f, Math.Min(900f, Screen.width - 40f), 85f),
                    heading + "\n" + manager.RecordingOriginDetail);
            }
            if (!visible) return;

            var entries = manager.Inspect();
            var width = Math.Min(780f, Screen.width - 40f);
            var height = Math.Min(520f, Screen.height - 40f);
            var area = new Rect(20f, 20f, width, height);
            GUI.Box(area, RuntimeUiText.T("HollowKnightTAS 回放存档", "HollowKnightTAS Replay Saves"));
            GUILayout.BeginArea(
                new Rect(
                    area.x + 12f,
                    area.y + 28f,
                    area.width - 24f,
                    area.height - 36f));
            GUILayout.Label(
                RuntimeUiText.T(
                    "F5 立即保存 | F8 关闭 | T 自动保存开/关 | 上/下 选择 | R/Enter 恢复 | P 固定自动存档",
                    "F5 save now | F8 close | T auto on/off | Up/Down select | R/Enter restore | P pin auto"));
            var policy = manager.AutoSavePolicy;
            var intervalText = policy.IntervalMovieTicks.ToString(CultureInfo.InvariantCulture);
            var retentionText = policy.RetentionCount.ToString(CultureInfo.InvariantCulture);
            var pendingText = manager.PendingCount.ToString(CultureInfo.InvariantCulture);
            GUILayout.Label(
                RuntimeUiText.T(
                    "自动保存: "
                    + (policy.Enabled ? "开" : "关")
                    + " 每 "
                    + intervalText
                    + " 电影帧，保留 "
                    + retentionText
                    + " | 待处理="
                    + pendingText,
                    "Auto: "
                    + (policy.Enabled ? "ON" : "OFF")
                    + " every "
                    + intervalText
                    + " movie ticks, keep "
                    + retentionText
                    + " | pending="
                    + pendingText));

            if (restoreProgress != null)
            {
                GUILayout.Label(
                    RuntimeUiText.T("恢复: ", "Restore: ")
                    + restoreProgress.Phase
                    + " "
                    + (restoreProgress.Fraction * 100d).ToString(
                        "F1",
                        CultureInfo.InvariantCulture)
                    + "% | "
                    + restoreProgress.Detail);
                if (restoreProgress.Phase
                    == ReplayRestorePhase.AwaitingOverwriteApproval)
                {
                    GUILayout.Label(
                        RuntimeUiText.T(
                            "专用 TAS 存档槽不一致。Y = 备份并覆盖；N/Esc = 取消且不写入。",
                            "Dedicated TAS slot differs. Y = backup and overwrite; N/Esc = cancel without writing."));
                }
                else if (restoreProgress.Phase
                         == ReplayRestorePhase.Paused)
                {
                    GUILayout.Label(
                        RuntimeUiText.T(
                            "目标哈希已校验并暂停。F9/Enter 从下一帧继续。",
                            "Target hash verified and paused. F9/Enter resumes at next tick."));
                }
            }

            GUILayout.Space(8f);
            if (entries.Count == 0)
            {
                GUILayout.Label(RuntimeUiText.T("当前版本暂无回放存档。", "No replay saves for this build."));
            }
            else
            {
                selectedIndex = Math.Max(
                    0,
                    Math.Min(selectedIndex, entries.Count - 1));
                foreach (var pair in entries
                             .Select((entry, index) => new { entry, index })
                             .Skip(Math.Max(0, selectedIndex - 5))
                             .Take(12))
                {
                    var descriptor = pair.entry.Descriptor;
                    GUILayout.Label(
                        (pair.index == selectedIndex ? "> " : "  ")
                        + descriptor.Label
                        + " | "
                        + descriptor.Reason
                        + " | tick "
                        + descriptor.EffectiveMovieTick.ToString(
                            CultureInfo.InvariantCulture)
                        + " | "
                        + descriptor.SceneName
                        + " | "
                        + pair.entry.Status);
                }
            }

            var last = manager.LastOperation;
            if (last != null)
            {
                GUILayout.Space(8f);
                GUILayout.Label(
                    RuntimeUiText.T("最近保存: ", "Last save: ")
                    + last.Status
                    + " "
                    + (string.IsNullOrEmpty(last.ReplaySaveId)
                        ? last.RequestId
                        : last.ReplaySaveId)
                    + (string.IsNullOrEmpty(last.Error)
                        ? string.Empty
                        : " | " + last.Error));
            }

            GUILayout.EndArea();
        }

        private void PollRestore()
        {
            if (!activeRestore.HasValue)
            {
                return;
            }

            restoreProgress = manager.Poll(activeRestore.Value);
        }
    }

    [DefaultExecutionOrder(-100)]
    internal sealed class ReplaySaveMenuRunner : MonoBehaviour
    {
        private ReplaySaveMenuController? owner;

        internal void Initialize(ReplaySaveMenuController value)
        {
            owner = value;
        }

        private void Update()
        {
            owner?.OnUpdate();
        }

        private void OnGUI()
        {
            if (Media.RuntimeVideoCapture.HideTasOverlays) return;
            owner?.OnGui();
        }
    }
}
