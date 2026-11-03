using System;
using System.IO;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    public sealed class FullRunFrameJournalTests
    {
        [TestMethod]
        public void ReadSnapshotIncludesPendingFramesWithoutFlushingOrChangingJournal()
        {
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "HKTAS-FullRunJournalTests"));
            var root = Path.GetFullPath(Path.Combine(parent, Guid.NewGuid().ToString("N")));
            try {
                var journal = new FullRunFrameJournal(root);
                var header = new MovieV2Header("game", "api", "mod", MovieProtocolV2.NativeProfileId,
                    MovieProtocolV2.ActionSchemaId, false, "none", 800, 450);
                for (var frame = 0; frame < 129; frame++) journal.CompleteFrame(frame, frame == 128 ? 100 : 50);
                var directory = Path.Combine(root, "full-run", "segments");
                var before = Directory.GetFiles(directory); Assert.AreEqual(1, before.Length);
                var bytes = File.ReadAllBytes(before[0]);
                var first = journal.ReadSnapshot(header, 129);
                var second = journal.ReadSnapshot(header, 129);
                var codec = new MovieV2Codec();
                Assert.AreEqual(codec.WriteCanonical(first), codec.WriteCanonical(second));
                CollectionAssert.AreEqual(before, Directory.GetFiles(directory));
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(before[0]));
                Assert.AreEqual(2, first.Runs.Count); Assert.AreEqual(128L, first.Runs[0].RepeatCount);
                journal.CompleteFrame(129, 100);
                var final = journal.Freeze(header, 130);
                Assert.AreEqual(2L, final.Runs[1].RepeatCount);
            } finally {
                if (!root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected temporary test path.");
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public void SnapshotCanContinueAndDoesNotMergeDifferentFrameRates()
        {
            var root = Path.Combine(Path.GetTempPath(), "HKTAS-FullRunJournalTests", Guid.NewGuid().ToString("N"));
            var journal = new FullRunFrameJournal(root);
            var header = new MovieV2Header("game", "api", "mod", MovieProtocolV2.NativeProfileId,
                MovieProtocolV2.ActionSchemaId, false, "none", 800, 450);
            journal.CompleteFrame(0, 50);
            var first = journal.Freeze(header, 1);
            journal.CompleteFrame(1, 100);
            var second = journal.Freeze(header, 2);
            Assert.AreEqual(1, first.Runs.Count);
            Assert.AreEqual(2, second.Runs.Count);
            Assert.AreEqual(50, second.Runs[0].FramesPerSecond);
            Assert.AreEqual(100, second.Runs[1].FramesPerSecond);
        }

        [TestMethod]
        public void Freeze_PreservesZeroSamplePrefixAndMergesRepeatedMenuFrames()
        {
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
                "HKTAS-FullRunJournalTests"));
            var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            try
            {
                var journal = new FullRunFrameJournal(root);
                journal.CompleteFrame(0);
                var values = new short[MovieProtocolV2.PreMenuActionNames.Count];
                values[0] = short.MaxValue;
                var submit = new GameInputSample(GameInputChannel.PreMenu, values, null);
                journal.Append(1, submit, 1);
                journal.CompleteFrame(1);
                journal.Append(2, submit, 2);
                journal.CompleteFrame(2);
                Assert.Throws<InvalidOperationException>(() => journal.CompleteFrame(4));
                var header = new MovieV2Header("game", "api", "mod",
                    MovieProtocolV2.NativeProfileId, MovieProtocolV2.ActionSchemaId,
                    false, "none", 800, 450);
                var movie = journal.Freeze(header, 3);
                Assert.AreEqual(2, movie.Runs.Count);
                Assert.AreEqual(1L, movie.Runs[0].RepeatCount);
                Assert.AreEqual(0, movie.Runs[0].Samples.Count);
                Assert.AreEqual(2L, movie.Runs[1].RepeatCount);
                Assert.AreEqual(GameInputChannel.PreMenu, movie.Runs[1].Samples[0].Channel);
                var canonical = new MovieV2Codec().WriteCanonical(movie);
                Assert.IsTrue(new MovieV2Codec().Parse(new StringReader(canonical), "recorded.hktas").Success);
            }
            finally
            {
                var full = Path.GetFullPath(root);
                if (!full.StartsWith(parent + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unexpected temporary test path.");
                if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
            }
        }
    }
}
