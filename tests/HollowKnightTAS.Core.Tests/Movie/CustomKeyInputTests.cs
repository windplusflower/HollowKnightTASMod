using System;
using System.IO;
using System.Linq;
using HollowKnightTAS.Core.Movie;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace HollowKnightTAS.Core.Tests.Movie;
[TestClass]
public class CustomKeyInputTests
{
    private static MovieV2Document Movie(params GameInputSample[] samples) => new("test",
        new MovieV2Header("game", "api", "mod", MovieProtocolV2.NativeProfileId, MovieProtocolV2.ActionSchemaId,
            false, "none", 0, 0, new short[] { 282, 306 }),
        new[] { new NativeFrameRun(8, samples, new MovieSourceSpan("test", 1, 1, 1), 59.94m) });
    [TestMethod]
    public void RoundTripPaintPrefixAndDeletePreserveConfiguration()
    {
        var original = Movie();
        var painted = MovieV2RangeEditor.Paint(original, 3, 2, "Key:282", true);
        Assert.IsTrue(MovieV2Prefix.Matches(original, painted, 3));
        var codec = new MovieV2Codec();
        var text = codec.WriteCanonical(painted);
        var parsed = codec.Parse(new StringReader(text), "roundtrip");
        Assert.IsTrue(parsed.Success);
        Assert.AreEqual(text, codec.WriteCanonical(parsed.Document!));
        Assert.AreEqual(59.94m, parsed.Document!.Runs[1].FramesPerSecond);
        var deleted = new MovieV2TimelineEditor().DeleteFrames(parsed.Document, 3, 2);
        Assert.IsTrue(deleted.Success);
        CollectionAssert.AreEqual(new short[] {282,306}, deleted.Movie.Header.CustomKeys.ToArray());
    }
    [TestMethod]
    public void IdleCustomKeysDoNotChangeExecutedPrefix()
    {
        var original = new MovieV2Document("test", Movie().Header.WithCustomKeys(Array.Empty<short>()), Movie().Runs);
        var added = Movie();
        Assert.IsTrue(MovieV2Prefix.Matches(original, added, 0));
        Assert.IsTrue(MovieV2Prefix.Matches(original, added, 8));
        var held = MovieV2RangeEditor.Paint(added, 3, 2, "Key:282", true);
        Assert.IsTrue(MovieV2Prefix.Matches(original, held, 3));
        Assert.IsFalse(MovieV2Prefix.Matches(original, held, 4));
    }
    [TestMethod]
    public void MenuHeroActionsPaintTheirNativeIndices()
    {
        var expected = new[] { ("QuickMap", 18), ("OpenInventory", 22), ("PaneRight", 23), ("PaneLeft", 24), ("Pause", 25) };
        foreach (var (action, index) in expected)
        {
            var painted = MovieV2RangeEditor.Paint(Movie(), 0, 1, action, true);
            var values = painted.Runs[0].Samples.Single(s => s.Channel == GameInputChannel.Hero).Values;
            Assert.AreEqual(short.MaxValue, values[index], action);
            Assert.AreEqual(1, values.Count(v => v != 0), action);
        }
    }
    [TestMethod]
    public void RejectInvalidDuplicateUnconfiguredKeysAndMasks()
    {
        var codec = new MovieV2Codec();
        foreach (var samples in new[] {
            new[] {CustomKeyInput.Sample(999, true)}, new[] {CustomKeyInput.Sample(283, true)},
            new[] {CustomKeyInput.Sample(282, true), CustomKeyInput.Sample(282, false)},
            new[] {new GameInputSample(GameInputChannel.CustomKey, new short[]{282,1}, null)},
            new[] {new GameInputSample(GameInputChannel.CustomKey, new short[]{282,0}, null, 1)} })
            Assert.ThrowsExactly<InvalidDataException>(() => codec.WriteCanonical(Movie(samples)));
        var text = codec.WriteCanonical(Movie(CustomKeyInput.Sample(282, true)));
        Assert.IsFalse(codec.Parse(new StringReader(text.Replace("[282,32767]", "[999,32767]")), "bad").Success);
        Assert.IsFalse(codec.Parse(new StringReader(text.Replace("[282,306]", "[306,282]")), "bad").Success);
    }
    [TestMethod]
    public void EdgesAreStableWithinFrameAndReleaseOnceAcrossFrames()
    {
        var state = new CustomKeyState();
        state.Prepare(new[]{CustomKeyInput.Sample(282,true),CustomKeyInput.Sample(306,true)});
        for(var i=0;i<3;i++) { Assert.IsTrue(state.Held(282)); Assert.IsTrue(state.Down(282)); Assert.IsFalse(state.Up(282)); }
        state.Complete(); state.Prepare(new[]{CustomKeyInput.Sample(282,true)});
        Assert.IsFalse(state.Down(282)); Assert.IsTrue(state.Up(306));
        state.Complete(); state.Prepare(Array.Empty<GameInputSample>());
        Assert.IsTrue(state.Up(282)); Assert.IsFalse(state.Up(306));
        state.Complete(); state.Prepare(Array.Empty<GameInputSample>());
        Assert.IsFalse(state.Up(282));
        state.Prepare(new[]{CustomKeyInput.Sample(282,true)});
        Assert.IsTrue(state.Down(282));
    }
}
