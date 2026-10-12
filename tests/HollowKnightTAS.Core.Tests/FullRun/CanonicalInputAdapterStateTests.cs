using System;
using System.Collections.Generic;
using System.IO;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalInputAdapterStateTests
{
    private static MovieV2Document Movie(long frames = 8, params short[] keys) => new("input-restore-test",
        new MovieV2Header("game", "api", "mod", MovieProtocolV2.NativeProfileId, MovieProtocolV2.ActionSchemaId,
            false, "none", 0, 0, keys),
        new[] { new NativeFrameRun(frames, Array.Empty<GameInputSample>(), new MovieSourceSpan("test", 1, 1, 1)) });
    [TestMethod]
    public void Boundary_before_future_key_input_preserves_both_held_edges_without_consuming_a_movie_frame()
    {
        var source = new CanonicalInputAdapterState { MovieFrame = 3, ConfiguredKeys = new short[] { 97, 98 },
            KeyboardPrevious = new short[] { 97 }, KeyboardCurrent = new short[] { 98 },
            AuthoredEdges = new[] { GameInputChannel.Hero, GameInputChannel.Binder } };
        source.RequireMovie(Movie(8, 97, 98)); var frozen = source.Copy();
        source.KeyboardCurrent[0] = 97; source.AuthoredEdges[0] = GameInputChannel.PreMenu;
        var restoredKeys = new CustomKeyState(); restoredKeys.Restore(frozen.KeyboardPrevious, frozen.KeyboardCurrent);
        Assert.IsTrue(restoredKeys.Up(97)); Assert.IsTrue(restoredKeys.Down(98)); Assert.IsTrue(restoredKeys.Held(98));
        Assert.AreEqual(3L, frozen.MovieFrame); Assert.AreEqual(GameInputChannel.Hero, frozen.AuthoredEdges[0]);
    }
    [TestMethod]
    public void Prepared_cursor_reads_source_state_instead_of_zero_input_preparation_state()
    {
        var source = new CanonicalInputAdapterState { MovieFrame = 7, Suspended = true, ConfiguredKeys = new short[] { 97 }, KeyboardCurrent = new short[] { 97 } };
        var target = new CanonicalInputAdapterState { MovieFrame = 1 };
        var codec = new CanonicalCarryGraphCodec(new[] { typeof(CanonicalInputAdapterState), typeof(GameInputChannel), typeof(GameInputChannel[]), typeof(short[]) }, "adapter-v1");
        var graph = codec.Capture(new Dictionary<string, object> { [CanonicalInputAdapterState.Root] = source }, _ => null);
        CanonicalInputAdapterState? frozen = null;
        codec.PrepareApply(graph, new Dictionary<string, object> { [CanonicalInputAdapterState.Root] = target },
            _ => throw new InvalidDataException(), view => frozen = CanonicalInputAdapterState.FromPrepared(view));
        Assert.AreEqual(1L, target.MovieFrame); Assert.IsFalse(target.Suspended);
        Assert.AreEqual(7L, frozen!.MovieFrame); Assert.IsTrue(frozen.Suspended); Assert.AreEqual((short)97, frozen.KeyboardCurrent[0]);
    }
    [TestMethod]
    public void Different_hook_key_set_and_movie_endpoint_are_rejected()
    {
        var state = new CanonicalInputAdapterState { MovieFrame = 7, ConfiguredKeys = new short[] { 97 } };
        state.RequireMovie(Movie(8, 97));
        Assert.ThrowsExactly<InvalidDataException>(() => state.RequireMovie(Movie(8, 98)));
        Assert.ThrowsExactly<InvalidDataException>(() => state.RequireMovie(Movie(7, 97)));
        Assert.ThrowsExactly<InvalidDataException>(() => state.RequireMovie(Movie(-1, 97)));
    }
    [TestMethod]
    [DataRow("duplicate-key")]
    [DataRow("unknown-key")]
    [DataRow("foreign-held-key")]
    [DataRow("unordered-edges")]
    [DataRow("mouse-edge")]
    public void Unsupported_or_ambiguous_edge_state_is_rejected(string fault)
    {
        var state = new CanonicalInputAdapterState { MovieFrame = 0, ConfiguredKeys = new short[] { 97 } };
        if (fault == "duplicate-key") state.ConfiguredKeys = new short[] { 97, 97 };
        if (fault == "unknown-key") state.ConfiguredKeys = new short[] { -1 };
        if (fault == "foreign-held-key") state.KeyboardPrevious = new short[] { 98 };
        if (fault == "unordered-edges") state.AuthoredEdges = new[] { GameInputChannel.Binder, GameInputChannel.Hero };
        if (fault == "mouse-edge") state.AuthoredEdges = new[] { GameInputChannel.MouseInControl };
        Assert.ThrowsExactly<InvalidDataException>(() => state.Validate());
    }
}
