using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.Runtime.FullRun
{
    public sealed class FullRunFrameJournal
    {
        private const int SegmentFrames = 128;
        private const int MaximumSegmentBytes = 8 * 1024 * 1024;
        private readonly string directory;
        private readonly List<FrameRecord> pending = new List<FrameRecord>();
        private readonly List<string> segments = new List<string>();
        private readonly List<GameInputSample> active = new List<GameInputSample>();
        private readonly List<ulong> activeTicks = new List<ulong>();
        private long nextFrame;
        private bool failed;

        public FullRunFrameJournal(string sessionDirectory)
        {
            if (string.IsNullOrWhiteSpace(sessionDirectory))
                throw new ArgumentException("Session directory is required.", nameof(sessionDirectory));
            directory = Path.Combine(Path.GetFullPath(sessionDirectory), "full-run", "segments");
            Directory.CreateDirectory(directory);
        }

        public long CompletedFrames => nextFrame;
        public bool Failed => failed;

        public void Append(long frameIndex, GameInputSample sample, ulong inputTick)
        {
            RequireActive(frameIndex);
            if (sample == null) throw new ArgumentNullException(nameof(sample));
            if (active.Count >= MovieProtocolV2.MaximumSamplesPerFrame)
                Fail("Native frame input sample limit exceeded.");
            active.Add(sample);
            activeTicks.Add(inputTick);
        }

        public void CompleteFrame(long frameIndex, decimal framesPerSecond = 50)
        {
            RequireActive(frameIndex);
            if (!MovieFrameRate.IsValid(framesPerSecond)) throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
            pending.Add(new FrameRecord(frameIndex, active.ToArray(), activeTicks.ToArray(), framesPerSecond));
            active.Clear();
            activeTicks.Clear();
            nextFrame = checked(nextFrame + 1);
            if (pending.Count >= SegmentFrames) FlushSegment();
        }

        public MovieV2Document Freeze(MovieV2Header header, long completedFrames)
        {
            if (header == null) throw new ArgumentNullException(nameof(header));
            if (failed || active.Count != 0 || completedFrames != nextFrame)
                throw new InvalidOperationException("Full-run journal is not at a complete frame boundary.");
            if (pending.Count != 0) FlushSegment();
            return ReadSnapshot(header, completedFrames);
        }

        // Boundary observation must not flush pending records or mutate the
        // recording journal merely to establish the Movie-prefix identity.
        internal MovieV2Document ReadSnapshot(MovieV2Header header, long completedFrames)
        {
            if (header == null) throw new ArgumentNullException(nameof(header));
            if (failed || active.Count != 0 || completedFrames != nextFrame)
                throw new InvalidOperationException("Full-run journal is not at a complete frame boundary.");
            var runs = new List<NativeFrameRun>();
            IReadOnlyList<GameInputSample>? previous = null;
            long repeat = 0;
            decimal previousFps = 50;
            long readFrames = 0;
            foreach (var frame in ReadRecordedFrames())
            {
                    if (frame.FrameIndex != readFrames)
                        Fail("Native frame segment index is missing or duplicated.");
                    if (previous != null && previousFps == frame.FramesPerSecond && Same(previous, frame.Samples)) repeat++;
                    else
                    {
                        if (previous != null)
                            runs.Add(new NativeFrameRun(repeat, previous,
                                new MovieSourceSpan("<recording>", 1, 1, 1), previousFps));
                        previous = frame.Samples;
                        previousFps = frame.FramesPerSecond;
                        repeat = 1;
                    }
                    readFrames++;
            }
            if (previous != null)
                runs.Add(new NativeFrameRun(repeat, previous,
                    new MovieSourceSpan("<recording>", 1, 1, 1), previousFps));
            if (readFrames != completedFrames)
                Fail("Full-run journal frame count differs from its segments.");
            var movie = new MovieV2Document("<recording>", header, runs);
            // The codec is the final bounded format gate; no truncated movie
            // is returned when disk or source limits are exceeded.
            new MovieV2Codec().WriteCanonical(movie);
            return movie;
        }

        private IEnumerable<FrameRecord> ReadRecordedFrames()
        {
            foreach (var segment in segments) foreach (var frame in ReadSegment(segment)) yield return frame;
            foreach (var frame in pending) yield return frame;
        }

        private void FlushSegment()
        {
            if (pending.Count == 0) return;
            var first = pending[0].FrameIndex;
            var final = Path.Combine(directory, first.ToString("D12") + ".seg");
            var temporary = final + ".new";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(0x344a5448); // HTJ4: rational FPS
                    writer.Write(first);
                    writer.Write(pending.Count);
                    foreach (var frame in pending)
                    {
                        writer.Write(frame.FrameIndex);
                        MovieFrameRate.ToRatio(frame.FramesPerSecond, out var numerator, out var denominator);
                        writer.Write(numerator);
                        writer.Write(denominator);
                        writer.Write(frame.Samples.Length);
                        for (var index = 0; index < frame.Samples.Length; index++)
                        {
                            var sample = frame.Samples[index];
                            writer.Write((byte)sample.Channel);
                            writer.Write(frame.Ticks[index]);
                            writer.Write((byte)sample.Values.Count);
                            foreach (var value in sample.Values) writer.Write(value);
                            writer.Write(sample.PressedMask);
                            writer.Write(sample.ReleasedMask);
                            writer.Write(sample.Mouse != null);
                            if (sample.Mouse != null)
                            {
                                writer.Write(sample.Mouse.XQ16);
                                writer.Write(sample.Mouse.YQ16);
                                writer.Write(sample.Mouse.DeltaXQ15);
                                writer.Write(sample.Mouse.DeltaYQ15);
                                writer.Write(sample.Mouse.Buttons);
                                writer.Write(sample.Mouse.WheelQ15);
                            }
                        }
                    }
                    writer.Flush();
                    if (stream.Length > MaximumSegmentBytes)
                        throw new InvalidDataException("Full-run segment exceeded its size limit.");
                    stream.Flush(true);
                }
                File.Move(temporary, final);
                segments.Add(final);
                pending.Clear();
            }
            catch (Exception exception)
            {
                failed = true;
                throw new IOException("Full-run input segment could not be persisted: "
                    + exception.Message, exception);
            }
        }

        private static IEnumerable<FrameRecord> ReadSegment(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream))
            {
                var magic = reader.ReadInt32();
                if (stream.Length > MaximumSegmentBytes || (magic != 0x334a5448 && magic != 0x344a5448))
                    throw new InvalidDataException("Full-run input segment is invalid.");
                var first = reader.ReadInt64();
                var count = reader.ReadInt32();
                if (count < 1 || count > SegmentFrames)
                    throw new InvalidDataException("Full-run segment frame count is invalid.");
                for (var frameIndex = 0; frameIndex < count; frameIndex++)
                {
                    var frame = reader.ReadInt64();
                    var numerator = reader.ReadInt32();
                    var denominator = magic == 0x344a5448 ? reader.ReadInt32() : 1;
                    var fps = MovieFrameRate.FromRatio(numerator, denominator);
                    var sampleCount = reader.ReadInt32();
                    if (frame != first + frameIndex || sampleCount < 0
                        || sampleCount > MovieProtocolV2.MaximumSamplesPerFrame)
                        throw new InvalidDataException("Full-run segment frame is invalid.");
                    var samples = new GameInputSample[sampleCount];
                    var ticks = new ulong[sampleCount];
                    for (var index = 0; index < sampleCount; index++)
                    {
                        var channel = (GameInputChannel)reader.ReadByte();
                        ticks[index] = reader.ReadUInt64();
                        var valueCount = reader.ReadByte();
                        if (valueCount != MovieProtocolV2.ExpectedValueCount(channel))
                            throw new InvalidDataException("Full-run segment action count is invalid.");
                        var values = new short[valueCount];
                        for (var valueIndex = 0; valueIndex < values.Length; valueIndex++)
                            values[valueIndex] = reader.ReadInt16();
                        var pressedMask = reader.ReadUInt64();
                        var releasedMask = reader.ReadUInt64();
                        MouseFrameState? mouse = null;
                        if (reader.ReadBoolean())
                            mouse = new MouseFrameState(reader.ReadInt32(), reader.ReadInt32(),
                                reader.ReadInt16(), reader.ReadInt16(), reader.ReadUInt32(),
                                reader.ReadInt16());
                        samples[index] = new GameInputSample(channel, values, mouse,
                            pressedMask, releasedMask);
                    }
                    yield return new FrameRecord(frame, samples, ticks, fps);
                }
                if (stream.Position != stream.Length)
                    throw new InvalidDataException("Full-run segment has trailing bytes.");
            }
        }

        private static bool Same(IReadOnlyList<GameInputSample> left,
            IReadOnlyList<GameInputSample> right)
        {
            if (left.Count != right.Count) return false;
            for (var index = 0; index < left.Count; index++)
            {
                if (left[index].Channel != right[index].Channel
                    || left[index].Values.Count != right[index].Values.Count
                    || left[index].PressedMask != right[index].PressedMask
                    || left[index].ReleasedMask != right[index].ReleasedMask)
                    return false;
                for (var value = 0; value < left[index].Values.Count; value++)
                    if (left[index].Values[value] != right[index].Values[value]) return false;
                var a = left[index].Mouse;
                var b = right[index].Mouse;
                if ((a == null) != (b == null)) return false;
                if (a != null && b != null
                    && (a.XQ16 != b.XQ16 || a.YQ16 != b.YQ16
                        || a.DeltaXQ15 != b.DeltaXQ15 || a.DeltaYQ15 != b.DeltaYQ15
                        || a.Buttons != b.Buttons || a.WheelQ15 != b.WheelQ15)) return false;
            }
            return true;
        }

        private void RequireActive(long frameIndex)
        {
            if (failed || frameIndex != nextFrame || frameIndex < 0
                || frameIndex >= MovieProtocolV2.MaximumExpandedFrames)
                throw new InvalidOperationException("Full-run journal frame is unavailable.");
        }

        private void Fail(string message)
        {
            failed = true;
            throw new InvalidDataException(message);
        }

        private sealed class FrameRecord
        {
            public FrameRecord(long frameIndex, GameInputSample[] samples, ulong[] ticks, decimal framesPerSecond)
            {
                FramesPerSecond = framesPerSecond;
                FrameIndex = frameIndex;
                Samples = samples;
                Ticks = ticks;
            }
            public decimal FramesPerSecond { get; }
            public long FrameIndex { get; }
            public GameInputSample[] Samples { get; }
            public ulong[] Ticks { get; }
        }
    }
}
