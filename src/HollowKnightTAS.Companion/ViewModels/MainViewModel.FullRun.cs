using System;
using System.IO;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.Companion.ViewModels
{
    public sealed partial class MainViewModel
    {
        private bool gameMouseEnabled;

        private static string GameMouseSettingPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HollowKnightTAS", "studio-game-mouse-v2.txt");

        public bool GameMouseEnabled
        {
            get => gameMouseEnabled;
            set
            {
                if (fullRunMovies?.IsArmed == true)
                {
                    Status = "本次 Movie 的游戏鼠标模式已固定；从第 0 帧开始新会话才能修改。";
                    OnPropertyChanged();
                    return;
                }
                if (gameMouseEnabled == value) return;
                try
                {
                    var path = GameMouseSettingPath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var temporary = path + ".new";
                    File.WriteAllText(temporary, value ? "enabled\n" : "disabled\n");
                    File.Move(temporary, path, true);
                    gameMouseEnabled = value;
                    OnPropertyChanged();
                    Status = value ? "新 v2 Movie 将启用游戏鼠标。" : "新 v2 Movie 将关闭游戏鼠标。";
                }
                catch (Exception exception)
                {
                    Status = "游戏鼠标设置保存失败：" + exception.Message;
                    OnPropertyChanged();
                }
            }
        }

        private void InitializeFullRunSettings()
        {
            try
            {
                var path = GameMouseSettingPath;
                if (!File.Exists(path)) return;
                if (new FileInfo(path).Length > 32)
                    throw new InvalidDataException("游戏鼠标设置过大。");
                var text = File.ReadAllText(path).Trim();
                if (text != "enabled" && text != "disabled")
                    throw new InvalidDataException("游戏鼠标设置无效。");
                gameMouseEnabled = text == "enabled";
            }
            catch (Exception exception)
            {
                gameMouseEnabled = false;
                Status = "游戏鼠标设置读取失败；本次默认关闭：" + exception.Message;
            }
        }

        private void NewFullRunMovie()
        {
            try
            {
                var coordinator = fullRunMovies
                    ?? throw new InvalidOperationException("全流程启动器不可用。");
                if (coordinator.Mode != "Unarmed" || startupBoot?.IsWaiting != true || startupBoot.NativeCompletedFrames != 0)
                    throw new InvalidOperationException("请在受控启动第 0 帧新建序列。");
                SetSequenceInitialSaves(coordinator.SessionInitialSaves
                    ?? throw new InvalidOperationException("初始存档快照不可用。"));
                coordinator.ArmRecording(GameMouseEnabled, ParseFrameRate(DefaultFrameRate));
                ResetSequenceSaveTarget(null);
                var header = new MovieV2Header("unknown", "unknown", "unknown",
                    MovieProtocolV2.SelectedNativeProfileId, MovieProtocolV2.ActionSchemaId,
                    GameMouseEnabled, "none", 0, 0);
                MovieText = new MovieV2Codec().WriteCanonical(new MovieV2Document(
                    "<studio-draft>", header, Array.Empty<NativeFrameRun>()));
                AppendGridBlankFrames();
                gridHasUserEdits = false;
                earliestGridEdit = long.MaxValue;
                recordingGridNativeFrame = -1;
                StartTimeline(MovieText);
                ValidationOutput = "v2 recording armed at startup frame 0; Movie frame 0 begins at the first input-ready PlayerLoop.";
                OnPropertyChanged(nameof(PlaybackStateText));
                foreach (var command in runtimeCommands) command.RaiseCanExecuteChanged();
                Status = "全流程录制已预置。启动加载不计 Movie 帧；标题菜单可输入时开始第 0 帧。";
            }
            catch (Exception exception)
            {
                Status = "新建全流程 Movie 失败：" + exception.Message;
            }
        }
    }
}
