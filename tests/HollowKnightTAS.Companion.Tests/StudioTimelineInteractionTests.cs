using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Windows.Data;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Core.Movie;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Companion.Tests
{
    [TestClass]
    public sealed class StudioTimelineInteractionTests
    {
        private static MovieV2Document Movie(long count = 10_000_000) => new("test",
            new MovieV2Header("game", "api", "mod", MovieProtocolV2.NativeProfileId,
                MovieProtocolV2.ActionSchemaId, false, "none", 0, 0),
            new[] { new NativeFrameRun(count, Array.Empty<GameInputSample>(), new MovieSourceSpan("test", 1, 1, 1)) });

        [TestMethod]
        public void DragPreviewUpdatesImmediatelyAndCancelsWithoutEditingMovie()
        {
            var movie = Movie(5000);
            var rows = new VirtualInputRows(movie, 0);
            var old = rows[100];
            rows.Preview(100, 110, "Left", true);
            Assert.IsTrue(old.Left);
            Assert.IsTrue(rows[110].Left);
            Assert.IsFalse(rows[111].Left);
            rows.Preview(100, 103, "Left", true);
            Assert.IsFalse(rows[110].Left);
            rows.Preview(0, 0, null, false);
            Assert.IsFalse(old.Left);
            Assert.AreEqual(0, movie.Runs[0].Samples.Count);
        }

        [TestMethod]
        public void LiveUpdateAcceptsNextFrameAndRejectsAlreadyExecutedInputOrTimingChanges()
        {
            var movie = Movie(500);
            Assert.IsTrue(MovieV2Prefix.Matches(movie, MovieV2RangeEditor.Paint(movie, 10, 1, "Left", true), 10));
            Assert.IsFalse(MovieV2Prefix.Matches(movie, MovieV2RangeEditor.Paint(movie, 9, 1, "Left", true), 10));
            Assert.IsFalse(MovieV2Prefix.Matches(movie, MovieV2RangeEditor.SetFrameRate(movie, 9, 1, 100), 10));
            Assert.AreEqual(0, MovieV2Prefix.Take(movie, 0).Runs.Count);
        }

        [TestMethod]
        public void EvictedVisibleRowStillLosesCurrentArrow()
        {
            var rows = new VirtualInputRows(Movie(5000), 5);
            var shown = rows[5];
            Assert.AreEqual("▶", shown.Current);
            for (int i = 0; i < 3000; i++) _ = rows[1000 + i];
            rows.UpdateCurrent(6);
            Assert.AreEqual("", shown.Current);
            Assert.AreEqual("▶", rows[6].Current);
            GC.KeepAlive(shown);
        }

        [TestMethod]
        public void MenuActionsPaintAndDisplayInGridRows()
        {
            var movie = Movie(10);
            foreach (var action in InputGridRow.MenuActions)
            {
                var painted = MovieV2RangeEditor.Paint(movie, 2, 1, action, true);
                var rows = new VirtualInputRows(painted, 0);
                Assert.IsTrue(rows[2][action], action);
                Assert.IsFalse(rows[1][action], action);
                Assert.IsFalse(InputGridRow.MenuActions.Where(a => a != action).Any(a => rows[2][a]), action);
            }
        }

        [TestMethod]
        public void VirtualRowsCoverTenMillionFramesWithBoundedCache()
        {
            var rows = new VirtualInputRows(Movie(), 0);
            var view = new ListCollectionView((IList)rows);
            Assert.AreEqual(10_000_000, view.Count);
            Assert.AreEqual(9_999_999L, ((InputGridRow)view.GetItemAt(9_999_999)).Tick);
            var first = rows[0];
            for (int i = 0; i < 10000; i++) _ = rows[i * 997];
            Assert.IsTrue(rows.CachedCount <= 2048);
            Assert.AreEqual(0, view.IndexOf(first));
            Assert.AreEqual(first, rows[0]);
        }

        [TestMethod]
        public void PaintRangeAndFrameRateRemainCompressedAndRoundTrip()
        {
            var movie = MovieV2RangeEditor.Paint(Movie(), 100, 11, "Left", true);
            movie = MovieV2RangeEditor.SetFrameRate(movie, 105, 3, 120);
            var codec = new MovieV2Codec();
            var text = codec.WriteCanonical(movie);
            StringAssert.Contains(text, "\"fps\":120");
            StringAssert.Contains(text, "\"authored\":true");
            var parsed = codec.Parse(new StringReader(text), "test").Document!;
            var rows = new VirtualInputRows(parsed, 0);
            Assert.IsFalse(rows[99].Left);
            Assert.IsTrue(rows[100].Left);
            Assert.IsTrue(rows[110].Left);
            Assert.IsFalse(rows[111].Left);
            Assert.AreEqual(120, rows[105].FramesPerSecond);
            Assert.AreEqual(50, rows[108].FramesPerSecond);
            Assert.IsTrue(parsed.Runs.Count < 10);
            Assert.AreEqual(text, codec.WriteCanonical(parsed));
            var deleted = new MovieV2TimelineEditor().DeleteFrames(parsed, 0, 105);
            Assert.IsTrue(deleted.Success);
            Assert.AreEqual(120, deleted.Movie.Runs[0].FramesPerSecond);
            Assert.IsTrue(deleted.Movie.Runs[0].Authored);
        }

        [TestMethod]
        public void LegacyCanonicalDoesNotGainOptionalFields()
        {
            var text = new MovieV2Codec().WriteCanonical(Movie(5));
            Assert.IsFalse(text.Contains("\"fps\""));
            Assert.IsFalse(text.Contains("\"authored\""));
            var parsed = new MovieV2Codec().Parse(new StringReader(text), "test");
            Assert.AreEqual(50, parsed.Document!.Runs[0].FramesPerSecond);
            Assert.IsFalse(parsed.Document.Runs[0].Authored);
        }

        [TestMethod]
        public void InvalidFrameRatesAreRejectedWithoutChangingMovie()
        {
            var movie = Movie(50);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MovieV2RangeEditor.SetFrameRate(movie, 0, 1, 0));
            Assert.AreEqual(50, movie.Runs[0].FramesPerSecond);
        }
    }
}
