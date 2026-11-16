using System;
using System.Linq;
using HollowKnightTAS.Core.Movie;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.Movie
{
    [TestClass]
    public sealed class MovieV2TimelineEditorTests
    {
        [TestMethod]
        public void ReplaceInsertDelete_PreserveFrameZeroAndMenuInputOrder()
        {
            var original = Movie();
            var editor = new MovieV2TimelineEditor();
            var menu = MenuSample();
            var replacement = editor.ReplaceFrame(original, 0, new[] { menu });
            Assert.IsTrue(replacement.Success);
            Assert.AreEqual(6L, Count(replacement.Movie));
            Assert.AreEqual(GameInputChannel.PreMenu, Frame(replacement.Movie, 0)[0].Channel);
            Assert.AreEqual(0, Frame(original, 0).Count);

            var inserted = editor.InsertFrames(replacement.Movie, 1,
                new[] { new NativeFrameRun(2, Array.Empty<GameInputSample>(), Span()) });
            Assert.IsTrue(inserted.Success);
            Assert.AreEqual(8L, Count(inserted.Movie));
            Assert.AreEqual(0, Frame(inserted.Movie, 1).Count);
            Assert.AreEqual(0, Frame(inserted.Movie, 2).Count);
            Assert.AreEqual(GameInputChannel.PreMenu, Frame(inserted.Movie, 5)[0].Channel);

            var deleted = editor.DeleteFrames(inserted.Movie, 1, 2);
            Assert.IsTrue(deleted.Success);
            Assert.AreEqual(replacement.CanonicalText, deleted.CanonicalText);
            Assert.AreEqual(replacement.MovieId, deleted.MovieId);
            Assert.AreNotEqual(new MovieV2Codec().ComputeMovieId(original), replacement.MovieId);
        }

        [TestMethod]
        public void InvalidEdits_KeepOriginalDocumentTextAndIdentity()
        {
            var movie = Movie();
            var editor = new MovieV2TimelineEditor();
            var text = new MovieV2Codec().WriteCanonical(movie);
            var id = new MovieV2Codec().ComputeMovieId(movie);
            var badRange = editor.DeleteFrames(movie, 5, 2);
            Assert.IsFalse(badRange.Success);
            Assert.AreSame(movie, badRange.Movie);
            Assert.AreEqual(text, badRange.CanonicalText);
            Assert.AreEqual(id, badRange.MovieId);

            var badSample = new GameInputSample(GameInputChannel.PreMenu, new short[5], null);
            var badValues = editor.ReplaceFrame(movie, 0, new[] { badSample });
            Assert.IsFalse(badValues.Success);
            Assert.AreSame(movie, badValues.Movie);
            Assert.AreEqual(id, badValues.MovieId);

            var mouse = new GameInputSample(GameInputChannel.MouseInControl, Array.Empty<short>(),
                new MouseFrameState(10, 10, 0, 0, 0, 0));
            var badMouse = editor.ReplaceFrame(movie, 0, new[] { mouse });
            Assert.IsFalse(badMouse.Success);
            Assert.AreSame(movie, badMouse.Movie);
        }

        [TestMethod]
        public void ReplaceFrames_ReplacesRangeAndExtendsLengthInSinglePass()
        {
            var movie = Movie();
            var editor = new MovieV2TimelineEditor();
            var menu = MenuSample();
            var result = editor.ReplaceFrames(movie, 2, 3, new[]
            {
                new NativeFrameRun(5, new[] { menu }, Span())
            });
            Assert.IsTrue(result.Success);
            Assert.AreEqual(8L, Count(result.Movie));
            Assert.AreEqual(0, Frame(result.Movie, 1).Count);
            Assert.AreEqual(GameInputChannel.PreMenu, Frame(result.Movie, 2)[0].Channel);
            Assert.AreEqual(GameInputChannel.PreMenu, Frame(result.Movie, 6)[0].Channel);
            Assert.AreEqual(GameInputChannel.Hero, Frame(result.Movie, 7)[0].Channel);

            var extended = editor.ReplaceFrames(movie, 4, 2, new[]
            {
                new NativeFrameRun(4, Array.Empty<GameInputSample>(), Span()),
                new NativeFrameRun(2, new[] { menu }, Span())
            });
            Assert.IsTrue(extended.Success);
            Assert.AreEqual(10L, Count(extended.Movie));
            Assert.AreEqual(GameInputChannel.PreMenu, Frame(extended.Movie, 3)[0].Channel);
            Assert.AreEqual(0, Frame(extended.Movie, 4).Count);
            Assert.AreEqual(GameInputChannel.PreMenu, Frame(extended.Movie, 9)[0].Channel);
        }

        private static MovieV2Document Movie()
        {
            var header = new MovieV2Header("game", "api", "mod", "profile",
                MovieProtocolV2.ActionSchemaId, false, new string('a', 64), 800, 450);
            var hero = new GameInputSample(GameInputChannel.Hero,
                new short[MovieProtocolV2.HeroActionNames.Count], null);
            return new MovieV2Document("editor.hktas", header, new[]
            {
                new NativeFrameRun(3, Array.Empty<GameInputSample>(), Span()),
                new NativeFrameRun(2, new[] { MenuSample() }, Span()),
                new NativeFrameRun(1, new[] { hero }, Span())
            });
        }

        private static GameInputSample MenuSample()
        {
            var values = new short[MovieProtocolV2.PreMenuActionNames.Count];
            values[0] = short.MaxValue;
            return new GameInputSample(GameInputChannel.PreMenu, values, null);
        }

        private static MovieSourceSpan Span() => new MovieSourceSpan("editor.hktas", 2, 1, 1);
        private static long Count(MovieV2Document movie) => movie.Runs.Sum(run => run.RepeatCount);
        private static System.Collections.Generic.IReadOnlyList<GameInputSample> Frame(MovieV2Document movie, long frame)
        {
            foreach (var run in movie.Runs)
            {
                if (frame < run.RepeatCount) return run.Samples;
                frame -= run.RepeatCount;
            }
            throw new ArgumentOutOfRangeException(nameof(frame));
        }
    }
}
