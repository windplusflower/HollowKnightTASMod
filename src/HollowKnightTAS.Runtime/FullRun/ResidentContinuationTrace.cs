using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Movie;
using Newtonsoft.Json;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed class ResidentContinuationTrace : IDisposable
    {
        internal const string Schema = "resident-continuation-trace-v1";
        private const int MaximumLine = 1024 * 1024, MaximumKeys = 1000000;
        private const long MaximumFile = 2L * 1024 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly string[] Names = { "admission", "assetManifest", "assetValues", "clock", "componentNative", "directory", "input", "managedGraph", "objectNative", "process", "rng", "scheduler", "staticRoots", "typeManifest" };
        internal static IReadOnlyList<string> RequiredGroups => Array.AsReadOnly(Names);

        internal sealed class GroupMeta
        {
            internal readonly string ExpectedHash, ObservedHash;
            internal readonly int ExpectedCount, ObservedCount;
            internal GroupMeta(string expHash, int expCount, string obsHash, int obsCount)
            {
                ExpectedHash = expHash; ExpectedCount = expCount; ObservedHash = obsHash; ObservedCount = obsCount;
            }
        }

        internal sealed class Identity
        {
            public string Role = "", PacketSha256 = "", MovieSha256 = "", SaveSha256 = "", BuildSha256 = "", SourceScene = "", TargetScene = "";
            public int PacketBytes, BoundaryOrdinal;
            public long StartNative, StartMovie, StartConsumed, EndMovie, MovieLength;
        }

        // An adapter supplies two independently obtained directories: discovered
        // owner/slot keys and the keys represented by its full portable encoding.
        // Aliases and field layouts remain part of that encoding. Empty domains
        // still have a typed payload. Missing observations carry explicit gaps.
        internal sealed class Group
        {
            internal readonly string Name, PayloadHash, ExpectedHash, ObservedHash;
            internal readonly int PayloadBytes, ExpectedCount, ObservedCount;
            private readonly string[] gaps;
            internal bool Complete => PayloadBytes > 0 && gaps.Length == 0 && ExpectedCount == ObservedCount && ExpectedHash == ObservedHash;

            private Group(string name, byte[] payload, IEnumerable<string> expected, IEnumerable<string> observed, IEnumerable<string> unsupported)
            {
                if (!Names.Contains(name, StringComparer.Ordinal)) throw Error("unknown observation domain");
                Name = name; PayloadBytes = payload.Length; PayloadHash = Sha256Utility.ComputeHex(payload);
                var expectedKeys = Sorted(expected, MaximumKeys, "expected key");
                var observedKeys = Sorted(observed, MaximumKeys, "observed key");
                ExpectedCount = expectedKeys.Length; ObservedCount = observedKeys.Length;
                ExpectedHash = KeyHash(expectedKeys); ObservedHash = KeyHash(observedKeys);
                gaps = Sorted(unsupported, 4096, "coverage gap");
                if (PayloadBytes == 0 && gaps.Length == 0) throw Error("empty payload requires a missing-domain reason");
            }
            internal static Group Capture(string name, byte[] portableBytes, IEnumerable<string> expectedKeys, IEnumerable<string> observedKeys, IEnumerable<string> gaps)
                => new Group(name, portableBytes ?? throw new ArgumentNullException(nameof(portableBytes)), expectedKeys, observedKeys, gaps);
            internal static Group CaptureWithMeta(string name, byte[] portableBytes, IEnumerable<string> expectedKeys, IEnumerable<string> observedKeys, IEnumerable<string> gaps, out GroupMeta meta)
            {
                var g = Capture(name, portableBytes, expectedKeys, observedKeys, gaps);
                meta = new GroupMeta(g.ExpectedHash, g.ExpectedCount, g.ObservedHash, g.ObservedCount);
                return g;
            }
            internal static Group CaptureTrusted(string name, byte[] portableBytes, string expectedHash, int expectedCount, string observedHash, int observedCount, string[] gaps)
                => new Group(name, portableBytes ?? throw new ArgumentNullException(nameof(portableBytes)), expectedHash, expectedCount, observedHash, observedCount, gaps ?? Array.Empty<string>());
            private Group(string name, byte[] payload, string expectedHash, int expectedCount, string observedHash, int observedCount, string[] gaps)
            {
                if (!Names.Contains(name, StringComparer.Ordinal)) throw Error("unknown observation domain");
                Name = name; PayloadBytes = payload.Length; PayloadHash = Sha256Utility.ComputeHex(payload);
                ExpectedCount = expectedCount; ExpectedHash = expectedHash;
                ObservedCount = observedCount; ObservedHash = observedHash;
                this.gaps = gaps;
                if (PayloadBytes == 0 && gaps.Length == 0) throw Error("empty payload requires a missing-domain reason");
            }
            internal static Group Missing(string name, string reason)
                => new Group(name, Array.Empty<byte>(), Array.Empty<string>(), Array.Empty<string>(), new[] { reason });
            internal object Record() => new { name = Name, bytes = PayloadBytes, sha256 = PayloadHash, expectedCount = ExpectedCount,
                expectedKeysSha256 = ExpectedHash, observedCount = ObservedCount, observedKeysSha256 = ObservedHash, gaps };
            internal void AppendProof(BinaryWriter writer)
            {
                WriteText(writer, Name); writer.Write(PayloadBytes); WriteText(writer, PayloadHash);
                writer.Write(ExpectedCount); WriteText(writer, ExpectedHash); writer.Write(ObservedCount); WriteText(writer, ObservedHash);
                writer.Write(gaps.Length); foreach (var gap in gaps) WriteText(writer, gap);
            }
        }

        private readonly FileStream stream;
        private readonly SHA256 bodyHash = SHA256.Create();
        private readonly string finalPath, partialPath, traceId, headerHash;
        private readonly long endMovie, movieLength, startNative, startMovie;
        private long native, movie, consumed, nextBeforeConsumed, pendingConsumed, rows, loadingFrames, inputFrames;
        private bool awaitingAfter, pendingReady, failed, closed, allComplete = true;
        private string pendingBoundary = "";

        internal static ResidentContinuationTrace Open(string path, Identity identity) => new ResidentContinuationTrace(path, identity);
        private ResidentContinuationTrace(string path, Identity value)
        {
            if (value == null || value.Role != "source" && value.Role != "target" || value.PacketBytes <= 0 || value.PacketBytes > CanonicalActualDWire.MaximumBytes
                || value.BoundaryOrdinal <= 0 || value.StartNative < 0 || value.StartMovie < 0 || value.StartConsumed < 0
                || value.EndMovie <= value.StartMovie || value.MovieLength < value.EndMovie) throw Error("invalid boundary identity or suffix interval");
            foreach (var digest in new[] { value.PacketSha256, value.MovieSha256, value.SaveSha256, value.BuildSha256 })
                if (!MovieProtocolV1.IsLowerSha256(digest)) throw Error("an identity does not contain a lowercase SHA256");
            ValidateText(value.SourceScene, "source scene"); ValidateText(value.TargetScene, "target scene");
            finalPath = Path.GetFullPath(path); if (File.Exists(finalPath)) throw Error("trace destination already exists");
            traceId = Guid.NewGuid().ToString("N"); partialPath = finalPath + ".partial-" + traceId;
            native = startNative = value.StartNative; movie = startMovie = value.StartMovie; consumed = nextBeforeConsumed = value.StartConsumed;
            endMovie = value.EndMovie; movieLength = value.MovieLength;
            using (var process = Process.GetCurrentProcess()) {
                var pid = process.Id; var started = process.StartTime.ToUniversalTime().Ticks;
                var processNonce = Sha256Utility.ComputeUtf8Hex("resident-process-v1\n" + pid.ToString(CultureInfo.InvariantCulture) + "\n" + started.ToString(CultureInfo.InvariantCulture));
                var header = new { kind = "header", schema = Schema, traceId, role = value.Role, processId = pid, processStartUtcTicks = started, processNonce,
                    packetSchema = ResidentBoundaryCoordinator.Schema, packetSha256 = value.PacketSha256, packetBytes = value.PacketBytes,
                    movieSha256 = value.MovieSha256, saveSha256 = value.SaveSha256, buildSha256 = value.BuildSha256, boundaryOrdinal = value.BoundaryOrdinal,
                    sourceScene = value.SourceScene, targetScene = value.TargetScene, startNative, startMovie, startConsumed = consumed, endMovie, movieLength,
                    consumedSemantics = "current-frame-sample-index-before-next-prepare-v1", groups = Names };
                var bytes = Line(header); headerHash = Sha256Utility.ComputeHex(bytes);
                stream = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536);
                try { AppendBody(bytes); } catch { Dispose(); throw; }
            }
        }

        internal void Sample(string phase, long completedNative, long completedMovie, long consumedSamples, bool inputReady,
            string boundary, string scene, IEnumerable<Group> groups)
        {
            if (failed || closed) throw Error("trace is faulted or closed");
            try {
                ValidateText(boundary, "frame boundary"); ValidateText(scene, "active scene", true);
                var after = phase == "after";
                if (phase != (awaitingAfter ? "after" : "before") || movie >= endMovie) throw Error("phase order or requested endpoint differs");
                if (after) {
                    if (completedNative != checked(native + 1) || completedMovie != checked(movie + (inputReady ? 1 : 0))
                        || inputReady != pendingReady || boundary != pendingBoundary || consumedSamples < pendingConsumed || !inputReady && consumedSamples != pendingConsumed)
                        throw Error("native/Movie/input cursor or the preceding before-frame admission differs");
                } else if (completedNative != native || completedMovie != movie || consumedSamples != nextBeforeConsumed) throw Error("before frame differs from the previous completed cursors or prepared input sample index");
                var ordered = (groups ?? throw Error("observation directory is absent")).Take(Names.Length + 1).ToArray();
                if (ordered.Length != Names.Length || ordered.Any(group => group == null)) throw Error("observation directory is incomplete");
                Array.Sort(ordered, (a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
                if (!ordered.Select(group => group.Name).SequenceEqual(Names, StringComparer.Ordinal)) throw Error("observation group membership differs");
                var complete = ordered.All(group => group.Complete);
                var marker = after && inputReady && inputFrames < 3 ? checked((int)inputFrames + 1) : 0;
                var row = new { kind = "sample", sequence = rows, phase, completedNative, completedMovie, consumedSamples, inputReady, boundary, scene,
                    k = marker, samplesComplete = complete, worldSha256 = WorldHash(ordered), groups = ordered.Select(group => group.Record()).ToArray() };
                AppendBody(Line(row)); rows = checked(rows + 1); allComplete &= complete;
                if (after) {
                    native = completedNative; movie = completedMovie; consumed = consumedSamples;
                    // ConsumedSamples is the current Movie frame's sampleIndex.
                    // A ready after point precedes PrepareFrame(next), which
                    // resets the next before point to zero. Loading preserves it.
                    nextBeforeConsumed = inputReady ? 0 : consumed;
                    if (inputReady) ++inputFrames; else ++loadingFrames;
                    pendingBoundary = "";
                } else { pendingReady = inputReady; pendingBoundary = boundary; pendingConsumed = consumedSamples; }
                awaitingAfter = !after;
            } catch { failed = true; Dispose(); throw; }
        }

        internal void Seal(long completedNative, long completedMovie, long consumedSamples)
        {
            if (failed || closed) throw Error("trace is faulted or closed");
            try {
                if (awaitingAfter || rows == 0 || completedNative != native || completedMovie != movie || consumedSamples != consumed || movie != endMovie
                    || rows != checked(2 * (native - startNative)) || inputFrames != endMovie - startMovie) throw Error("cannot seal an incomplete native or Movie interval");
                bodyHash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var footer = new { kind = "footer", schema = Schema, traceId, headerSha256 = headerHash, bodySha256 = Hex(bodyHash.Hash!),
                    rows, pairs = native - startNative, loadingFrames, inputFrames, kCount = Math.Min(3L, inputFrames), samplesComplete = allComplete,
                    endNative = native, endMovie = movie, endConsumed = consumed, movieLength };
                var bytes = Line(footer); if (stream.Position > MaximumFile - bytes.Length) throw Error("trace exceeds the bounded file size");
                stream.Write(bytes, 0, bytes.Length); stream.Flush(true); Dispose(); File.Move(partialPath, finalPath);
            } catch { failed = true; Dispose(); throw; }
        }
        public void Dispose()
        {
            if (closed) return; closed = true;
            try { stream.Dispose(); } finally { bodyHash.Dispose(); }
        }
        private void AppendBody(byte[] bytes)
        {
            if (stream.Position > MaximumFile - bytes.Length) throw Error("trace exceeds the bounded file size");
            stream.Write(bytes, 0, bytes.Length); bodyHash.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
            stream.Flush();
        }
        private static byte[] Line(object record)
        {
            var bytes = Utf8.GetBytes(JsonConvert.SerializeObject(record, Formatting.None) + "\n");
            if (bytes.Length > MaximumLine) throw Error("trace record exceeds the bounded line size"); return bytes;
        }
        private static string WorldHash(Group[] groups)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, Utf8, true)) {
                WriteText(writer, "resident-world-v1"); writer.Write(groups.Length); foreach (var group in groups) group.AppendProof(writer);
                writer.Flush(); return Sha256Utility.ComputeHex(stream.ToArray());
            }
        }
        private static string KeyHash(string[] keys)
        {
            using (var algorithm = SHA256.Create()) using (var sink = new CryptoStream(Stream.Null, algorithm, CryptoStreamMode.Write)) {
                using (var writer = new BinaryWriter(sink, Utf8, true)) {
                    WriteText(writer, "resident-keys-v1"); writer.Write(keys.Length); foreach (var key in keys) WriteText(writer, key);
                }
                sink.FlushFinalBlock(); return Hex(algorithm.Hash!);
            }
        }
        private static void WriteText(BinaryWriter writer, string text)
        { var bytes = Utf8.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes); }
        private static void ValidateText(string text, string label, bool emptyAllowed = false)
        {
            if (text == null || !emptyAllowed && string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl) || Utf8.GetByteCount(text) > 16384)
                throw Error("invalid " + label);
        }
        private static string[] Sorted(IEnumerable<string> values, int limit, string label)
        {
            if (values == null) throw Error(label + " directory is absent");
            var result = values.Take(limit + 1).ToArray(); if (result.Length > limit) throw Error(label + " directory exceeds its bound");
            foreach (var item in result) ValidateText(item, label);
            Array.Sort(result, StringComparer.Ordinal);
            for (var i = 1; i < result.Length; i++) if (result[i] == result[i - 1]) throw Error("duplicate " + label);
            return result;
        }
        private static string Hex(byte[] value) => BitConverter.ToString(value).Replace("-", "").ToLowerInvariant();
        private static InvalidDataException Error(string message) => new InvalidDataException("Resident continuation trace: " + message);
    }
}
