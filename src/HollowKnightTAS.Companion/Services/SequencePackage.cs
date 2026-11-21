using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.ReplaySave;
using HollowKnightTAS.Core.SceneCheckpoints;

namespace HollowKnightTAS.Companion.Services;

/// <summary>Immutable, complete file set for slots 1–4, including absent files.</summary>
public sealed class InitialSaveSnapshot
{
    private readonly Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> Hashes { get; }
    public string Id { get; }

    public InitialSaveSnapshot(IEnumerable<KeyValuePair<string, byte[]>> source)
    {
        long total = 0;
        foreach (var pair in source)
        {
            var name = pair.Key.ToLowerInvariant();
            if (!ProtectedSaveDescriptor.IsSlotFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || files.ContainsKey(name) || pair.Value == null
                || pair.Value.LongLength > ProtectedSaveSession.MaximumFileBytes
                || (total += pair.Value.LongLength) > ProtectedSaveSession.MaximumTotalBytes
                || files.Count >= ProtectedSaveSession.MaximumFiles)
                throw new InvalidDataException("初始存档文件无效或超过限制。");
            files.Add(name, (byte[])pair.Value.Clone());
        }
        var hashes = files.OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(p => p.Key, p => Sha256Utility.ComputeHex(p.Value), StringComparer.OrdinalIgnoreCase);
        Hashes = new ReadOnlyDictionary<string, string>(hashes);
        Id = Sha256Utility.ComputeHex(JsonSerializer.SerializeToUtf8Bytes(hashes));
    }

    internal IEnumerable<KeyValuePair<string, byte[]>> CopyFiles()
        => files.Select(p => new KeyValuePair<string, byte[]>(p.Key, (byte[])p.Value.Clone()));

    internal void WriteToNewShadow(string directory)
    {
        foreach (var pair in files)
        {
            using var output = new FileStream(Path.Combine(directory, pair.Key), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(pair.Value);
            output.Flush(true);
        }
    }
}

public sealed record SequenceFile(
    string Movie,
    InitialSaveSnapshot? InitialSaves,
    TimelineTree? Timeline = null,
    IReadOnlyList<SceneCheckpointPacket>? Checkpoints = null);

/// <summary>Portable sequence archive; never extracts archive paths to disk.</summary>
public static class SequencePackage
{
    public const string Extension = ".hktaspack";
    public const int MaximumMovieBytes = 16 * 1024 * 1024;
    public const int MaximumTimelineBytes = 32 * 1024 * 1024;
    public const int MaximumCheckpoints = 512;
    private const int MaximumManifestBytes = 512 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static SequenceFile Read(string path)
    {
        using var input = File.OpenRead(path);
        if (!path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            if (input.Length > MaximumMovieBytes) throw new InvalidDataException("序列文件超过 16 MiB。");
            using var reader = new StreamReader(input, Utf8);
            return new SequenceFile(reader.ReadToEnd(), null);
        }
        if (input.Length > ProtectedSaveSession.MaximumTotalBytes + (long)MaximumMovieBytes + SceneCheckpointStore.MaximumStoredBytes + MaximumTimelineBytes + 1024L * 1024)
            throw new InvalidDataException("序列包过大。");
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        if (archive.Entries.Count > ProtectedSaveSession.MaximumFiles + 3 + MaximumCheckpoints)
            throw new InvalidDataException("序列包文件数量过多。");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
            if (!entries.TryAdd(entry.FullName, entry)) throw new InvalidDataException("序列包包含重复文件。");
        byte[] ReadEntry(string name, long maximum)
        {
            if (!entries.Remove(name, out var entry) || entry.Length > maximum || entry.Length < 0)
                throw new InvalidDataException("序列包缺少文件或文件过大：" + name);
            using var stream = entry.Open();
            var bytes = new byte[checked((int)entry.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException("序列包文件长度不匹配。");
            return bytes;
        }
        var manifest = JsonSerializer.Deserialize<Manifest>(ReadEntry("manifest.json", MaximumManifestBytes))
            ?? throw new InvalidDataException("序列包清单无效。");
        if (manifest.Format != "HK-TAS-Sequence" || manifest.Slots == null
            || !manifest.Slots.SequenceEqual(new[] { 1, 2, 3, 4 }) || manifest.Files == null
            || manifest.Files.Count > ProtectedSaveSession.MaximumFiles)
            throw new InvalidDataException("序列包格式或四槽清单无效。");
        if (manifest.Version != 1)
            Trace.TraceWarning($"SequencePackage: manifest version {manifest.Version} differs from expected 1; continuing.");
        var movieBytes = ReadEntry("movie.hktas", MaximumMovieBytes);
        if (Sha256Utility.ComputeHex(movieBytes) != manifest.MovieSha256)
            Trace.TraceWarning("SequencePackage: movie.hktas SHA-256 differs from manifest; continuing.");
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var item in manifest.Files)
        {
            if (item == null || !ProtectedSaveDescriptor.IsSlotFileName(item.Name)
                || item.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || item.Name.Contains('/') || item.Name.Contains('\\') || item.Name.Contains(".."))
                throw new InvalidDataException("初始存档清单无效。");
            var bytes = ReadEntry("initial-saves/" + item.Name, ProtectedSaveSession.MaximumFileBytes);
            if ((total += bytes.LongLength) > ProtectedSaveSession.MaximumTotalBytes)
                throw new InvalidDataException("初始存档总大小超过限制。");
            if (bytes.LongLength != item.Length || Sha256Utility.ComputeHex(bytes) != item.Sha256)
                Trace.TraceWarning($"SequencePackage: initial-saves/{item.Name} length or SHA-256 differs from manifest; continuing.");
            if (!files.TryAdd(item.Name, bytes))
                throw new InvalidDataException("初始存档存在重复条目：" + item.Name);
        }

        TimelineTree? loadedTimeline = null;
        if (manifest.Timeline != null)
        {
            if (manifest.Timeline.Name != "timeline.json")
                throw new InvalidDataException("时间线清单无效。");
            var tBytes = ReadEntry("timeline.json", MaximumTimelineBytes);
            if (tBytes.LongLength != manifest.Timeline.Length
                || Sha256Utility.ComputeHex(tBytes) != manifest.Timeline.Sha256)
                Trace.TraceWarning("SequencePackage: timeline.json length or SHA-256 differs from manifest; continuing.");
            loadedTimeline = JsonSerializer.Deserialize<TimelineTree>(tBytes)
                ?? throw new InvalidDataException("时间线数据为空。");
            StudioTimelineStore.ValidateTree(loadedTimeline);
            if (!string.IsNullOrEmpty(loadedTimeline.InitialSavesId)
                && loadedTimeline.InitialSavesId != manifest.InitialSavesId)
                Trace.TraceWarning("SequencePackage: timeline InitialSavesId differs from manifest; continuing.");
        }

        List<SceneCheckpointPacket>? loadedCheckpoints = null;
        if (manifest.Checkpoints != null)
        {
            if (manifest.Checkpoints.Count > MaximumCheckpoints)
                throw new InvalidDataException("场景快照数量超过限制。");
            loadedCheckpoints = new List<SceneCheckpointPacket>(manifest.Checkpoints.Count);
            var seenCheckpointNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalCheckpointBytes = 0;
            foreach (var item in manifest.Checkpoints)
            {
                if (item == null
                    || string.IsNullOrEmpty(item.Name)
                    || !item.Name.EndsWith(".bin", StringComparison.Ordinal)
                    || item.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    || item.Name.Contains('/') || item.Name.Contains('\\') || item.Name.Contains("..")
                    || !seenCheckpointNames.Add(item.Name))
                    throw new InvalidDataException("场景快照清单无效。");
                var cBytes = ReadEntry("checkpoints/" + item.Name, SceneCheckpointPacket.MaximumPayloadBytes + 64 * 1024);
                if (cBytes.LongLength == 0
                    || (totalCheckpointBytes += cBytes.LongLength) > SceneCheckpointStore.MaximumStoredBytes + (long)MaximumCheckpoints * 64 * 1024)
                    throw new InvalidDataException("场景快照大小超过限制：" + item.Name);
                if (cBytes.LongLength != item.Length
                    || Sha256Utility.ComputeHex(cBytes) != item.Sha256)
                    Trace.TraceWarning($"SequencePackage: checkpoints/{item.Name} length or SHA-256 differs from manifest; continuing.");
                var pkt = SceneCheckpointPacket.FromPortableBytes(cBytes);
                if (pkt.Header.CheckpointId + ".bin" != item.Name)
                    throw new InvalidDataException("场景快照标识不匹配：" + item.Name);
                loadedCheckpoints.Add(pkt);
            }
        }

        if (entries.Count != 0)
            Trace.TraceWarning("SequencePackage: skipping undeclared archive entries: "
                + string.Join(", ", entries.Keys.OrderBy(k => k, StringComparer.Ordinal)));
        var snapshot = new InitialSaveSnapshot(files);
        if (snapshot.Id != manifest.InitialSavesId)
            Trace.TraceWarning($"SequencePackage: computed InitialSavesId ({snapshot.Id}) differs from manifest ({manifest.InitialSavesId}); continuing.");
        return new SequenceFile(Utf8.GetString(movieBytes), snapshot, loadedTimeline, loadedCheckpoints);
    }

    public static async Task WriteAsync(
        string path,
        string movie,
        InitialSaveSnapshot? snapshot,
        TimelineTree? timeline = null,
        IReadOnlyList<SceneCheckpointPacket>? checkpoints = null)
    {
        var isPackage = path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);
        if (isPackage != (snapshot != null)) throw new InvalidDataException("绑定存档的序列必须保存为 .hktaspack；未绑定序列使用 .hktas。");
        var movieBytes = Utf8.GetBytes(movie);
        if (movieBytes.Length > MaximumMovieBytes) throw new InvalidDataException("序列文件超过 16 MiB。");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (snapshot == null) await output.WriteAsync(movieBytes);
                else
                {
                    using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                    void Write(string name, byte[] bytes)
                    {
                        using var entry = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
                        entry.Write(bytes);
                    }
                    var manifest = new Manifest { InitialSavesId = snapshot.Id, MovieSha256 = Sha256Utility.ComputeHex(movieBytes) };
                    foreach (var pair in snapshot.CopyFiles().OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        manifest.Files.Add(new SaveFile { Name = pair.Key, Length = pair.Value.LongLength, Sha256 = snapshot.Hashes[pair.Key] });
                        Write("initial-saves/" + pair.Key, pair.Value);
                    }
                    if (timeline != null)
                    {
                        StudioTimelineStore.ValidateTree(timeline);
                        var timelineBytes = JsonSerializer.SerializeToUtf8Bytes(timeline);
                        if (timelineBytes.LongLength > MaximumTimelineBytes)
                            throw new InvalidDataException("时间线数据超过 32 MiB。");
                        manifest.Timeline = new SaveFile
                        {
                            Name = "timeline.json",
                            Length = timelineBytes.LongLength,
                            Sha256 = Sha256Utility.ComputeHex(timelineBytes)
                        };
                        Write("timeline.json", timelineBytes);
                    }
                    if (checkpoints != null && checkpoints.Count > 0)
                    {
                        if (checkpoints.Count > MaximumCheckpoints)
                            throw new InvalidDataException("场景快照数量超过限制。");
                        manifest.Checkpoints = new List<SaveFile>(checkpoints.Count);
                        foreach (var packet in checkpoints
                            .OrderBy(p => p.Header.ResumeMovieFrame)
                            .ThenBy(p => p.Header.TransitionId)
                            .ThenBy(p => p.Header.CheckpointId, StringComparer.Ordinal))
                        {
                            var bytes = packet.ToPortableBytes();
                            var fileName = packet.Header.CheckpointId + ".bin";
                            manifest.Checkpoints.Add(new SaveFile
                            {
                                Name = fileName,
                                Length = bytes.LongLength,
                                Sha256 = Sha256Utility.ComputeHex(bytes)
                            });
                            Write("checkpoints/" + fileName, bytes);
                        }
                    }
                    Write("movie.hktas", movieBytes);
                    Write("manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest));
                }
                output.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class Manifest
    {
        [JsonRequired] public string Format { get; set; } = "HK-TAS-Sequence";
        [JsonRequired] public int Version { get; set; } = 1;
        [JsonRequired] public int[] Slots { get; set; } = new[] { 1, 2, 3, 4 };
        [JsonRequired] public string InitialSavesId { get; set; } = "";
        [JsonRequired] public string MovieSha256 { get; set; } = "";
        [JsonRequired] public List<SaveFile> Files { get; set; } = new();
        public SaveFile? Timeline { get; set; }
        public List<SaveFile>? Checkpoints { get; set; }
    }
    private sealed class SaveFile
    {
        [JsonRequired] public string Name { get; set; } = "";
        [JsonRequired] public long Length { get; set; }
        [JsonRequired] public string Sha256 { get; set; } = "";
    }
}
