using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Win32;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Core.SceneCheckpoints;

namespace HollowKnightTAS.Companion.ViewModels
{
    public sealed partial class MainViewModel
    {
        private string sequenceDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HollowKnightTAS", "Sequences");
        private const int DefaultSequenceAutoSaveSeconds = 300;
        private string sequenceAutoSaveSeconds = DefaultSequenceAutoSaveSeconds.ToString();
        private string sequenceSaveStatus = "";
        private string activeSequenceDirectory = "";
        private int activeAutoSaveSeconds = DefaultSequenceAutoSaveSeconds;
        private string? sequenceSavePath;
        private string autoSaveName = Guid.NewGuid().ToString("N");
        private string lastAutoSaveText = "";
        private DateTime nextSequenceAutoSave = DateTime.UtcNow.AddSeconds(DefaultSequenceAutoSaveSeconds);
        private bool sequenceSaving;
        private static string SequenceSettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HollowKnightTAS", "studio-sequence-settings.json");
        public string SequenceDirectory { get => sequenceDirectory; set => Set(ref sequenceDirectory, value); }
        public string SequenceAutoSaveSeconds { get => sequenceAutoSaveSeconds; set => Set(ref sequenceAutoSaveSeconds, value); }
        public string SequenceSaveStatus { get => sequenceSaveStatus; private set => Set(ref sequenceSaveStatus, value); }
        public ICommand SaveSequenceSettingsCommand { get; private set; } = null!;
        public ICommand BrowseSequenceDirectoryCommand { get; private set; } = null!;

        private void InitializeSequenceSettings()
        {
            activeSequenceDirectory = SequenceDirectory;
            try
            {
                if (File.Exists(SequenceSettingsPath))
                {
                    var settings = JsonSerializer.Deserialize<SequenceSettings>(File.ReadAllText(SequenceSettingsPath)) ?? throw new InvalidDataException("序列设置无效。");
                    ValidateSequenceSettings(settings.Directory, settings.Seconds);
                    SequenceDirectory = activeSequenceDirectory = settings.Directory;
                    SequenceAutoSaveSeconds = settings.Seconds.ToString();
                    activeAutoSaveSeconds = settings.Seconds;
                }
            }
            catch (Exception ex) { SequenceSaveStatus = "序列设置读取失败：" + ex.Message; }
            SaveSequenceSettingsCommand = new RelayCommand(() =>
            {
                try
                {
                    if (!int.TryParse(SequenceAutoSaveSeconds, out var seconds)) throw new InvalidDataException("自动保存间隔必须为整数秒。");
                    ValidateSequenceSettings(SequenceDirectory, seconds);
                    var directory = Path.GetFullPath(SequenceDirectory);
                    Directory.CreateDirectory(directory);
                    Directory.CreateDirectory(Path.GetDirectoryName(SequenceSettingsPath)!);
                    File.WriteAllText(SequenceSettingsPath + ".new", JsonSerializer.Serialize(new SequenceSettings { Directory = directory, Seconds = seconds }));
                    File.Move(SequenceSettingsPath + ".new", SequenceSettingsPath, true);
                    activeSequenceDirectory = SequenceDirectory = directory;
                    activeAutoSaveSeconds = seconds;
                    nextSequenceAutoSave = DateTime.UtcNow.AddSeconds(seconds);
                    lastAutoSaveText = "";
                    SequenceSaveStatus = "序列保存设置已应用。";
                }
                catch (Exception ex) { SequenceSaveStatus = "序列保存设置失败：" + ex.Message; }
            });
            BrowseSequenceDirectoryCommand = new RelayCommand(() =>
            {
                var dialog = new OpenFolderDialog();
                if (dialog.ShowDialog() == true) SequenceDirectory = dialog.FolderName;
            });
        }

        internal static void ValidateSequenceSettings(string directory, int seconds)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) throw new InvalidDataException("请选择绝对保存路径。");
            if (seconds < 0 || seconds > 86400) throw new InvalidDataException("自动保存间隔为 0–86400 秒，0 表示关闭。");
            _ = Path.GetFullPath(directory);
        }

        private void ResetSequenceSaveTarget(string? path)
        {
            sequenceSavePath = path;
            autoSaveName = Guid.NewGuid().ToString("N");
            lastAutoSaveText = "";
            nextSequenceAutoSave = DateTime.UtcNow.AddSeconds(activeAutoSaveSeconds);
        }

        private async Task SaveSequenceAsync(bool saveAs)
        {
            if (sequenceSaving || gridApplying) return;
            sequenceSaving = true;
            try
            {
                var path = sequenceSavePath;
                if (saveAs || path == null)
                {
                    Directory.CreateDirectory(activeSequenceDirectory);
                    var extension = sequenceInitialSaves == null ? ".hktas" : SequencePackage.Extension;
                    var dialog = new SaveFileDialog { Filter = sequenceInitialSaves == null ? "HK-TAS Movie (*.hktas)|*.hktas" : "HK-TAS Sequence (*.hktaspack)|*.hktaspack", AddExtension = true, DefaultExt = extension, InitialDirectory = activeSequenceDirectory, FileName = path == null ? "sequence" + extension : Path.GetFileName(path) };
                    if (dialog.ShowDialog() != true) return;
                    path = dialog.FileName;
                }
                await SaveSequenceToPathAsync(path);
                SequenceSaveStatus = Status = "序列已保存：" + path;
            }
            catch (Exception ex) { SequenceSaveStatus = Status = "序列保存失败：" + ex.Message; }
            finally { sequenceSaving = false; }
        }

        public async Task AutoSaveSequenceAsync()
        {
            if (sequenceSaving || gridApplying || activeAutoSaveSeconds == 0 || DateTime.UtcNow < nextSequenceAutoSave) return;
            nextSequenceAutoSave = DateTime.UtcNow.AddSeconds(activeAutoSaveSeconds);
            var text = MovieText;
            if (string.IsNullOrWhiteSpace(text) || text == lastAutoSaveText) return;
            sequenceSaving = true;
            try
            {
                var directory = Path.Combine(activeSequenceDirectory, "Autosave");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "sequence-" + autoSaveName + (sequenceInitialSaves == null ? ".hktas" : SequencePackage.Extension));
                var (timeline, checkpoints) = sequenceInitialSaves != null
                    ? GetPersistedTimelineAndCheckpoints(text)
                    : (null, null);
                await SequencePackage.WriteAsync(path, text, sequenceInitialSaves, timeline, checkpoints);
                lastAutoSaveText = text;
                SequenceSaveStatus = "序列自动保存：" + path;
            }
            catch (Exception ex) { SequenceSaveStatus = "序列自动保存失败：" + ex.Message; }
            finally { sequenceSaving = false; }
        }

        internal async Task SaveSequenceToPathAsync(string path)
        {
            var original = MovieText;
            var saved = WithCurrentExecutionProfile(original);
            var (timeline, checkpoints) = sequenceInitialSaves != null
                ? GetPersistedTimelineAndCheckpoints(saved)
                : (null, null);
            await SequencePackage.WriteAsync(path, saved, sequenceInitialSaves, timeline, checkpoints);
            sequenceSavePath = path;
            // Export current execution metadata without changing the live timeline's
            // header identity, input rows, undo history, or concurrent edits.
        }

        private (TimelineTree? Timeline, IReadOnlyList<SceneCheckpointPacket>? Checkpoints) GetPersistedTimelineAndCheckpoints(string savedMovie)
        {
            if (sequenceInitialSaves == null || worldlines == null || activeDraftTree == null || SceneCheckpoints == null)
                return (null, null);
            var tree = worldlines.Library.Trees.FirstOrDefault(t => t.Id == activeDraftTree);
            if (tree == null) return (null, null);
            var clone = JsonSerializer.Deserialize<TimelineTree>(JsonSerializer.Serialize(tree));
            if (clone == null) return (null, null);
            clone.InitialSavesId = sequenceInitialSaves.Id;
            clone.OriginalHashes = new Dictionary<string, string>(sequenceInitialSaves.Hashes);
            if (clone.Nodes.Count == 1 && clone.Nodes[0].Movie.Length == 0)
                clone.Nodes[0].Movie = savedMovie;
            var packets = SceneCheckpoints.GetTreePackets(tree.Id);
            return (clone, packets);
        }

        private static string WithCurrentExecutionProfile(string source)
        {
            var parsed = new MovieAnyCodec().Parse(new StringReader(source), "<manual-save>");
            var movie = parsed.V2Document;
            if (!parsed.Success || movie == null
                || movie.Header.NativeProfileId == MovieProtocolV2.SelectedNativeProfileId
                || NativeExecutionProfile.SupportsSceneCheckpoints(movie.Header.NativeProfileId)) return source;
            var old = movie.Header;
            var header = new MovieV2Header(old.GameVersion, old.ApiVersion, old.ModVersion,
                MovieProtocolV2.SelectedNativeProfileId, old.ActionSchemaId, old.MouseEnabled,
                old.EnvironmentSha256, old.ViewportWidth, old.ViewportHeight, old.CustomKeys);
            return new MovieV2Codec().WriteCanonical(new MovieV2Document(movie.SourceName, header, movie.Runs));
        }

        private sealed class SequenceSettings
        {
            public string Directory { get; set; } = "";
            public int Seconds { get; set; }
        }
    }
}
