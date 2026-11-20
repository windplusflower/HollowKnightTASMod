using System;
using System.IO;
using System.Linq;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed class CanonicalInputAdapterState
    {
        internal const string Root = "R6/InputTAS-adapter-cursor";
        public int Version = 1;
        public long MovieFrame;
        public bool Suspended;
        public GameInputChannel[] AuthoredEdges = Array.Empty<GameInputChannel>();
        public short[] KeyboardPrevious = Array.Empty<short>(), KeyboardCurrent = Array.Empty<short>(), ConfiguredKeys = Array.Empty<short>();

        internal static CanonicalInputAdapterState FromPrepared(CanonicalCarryGraphCodec.PreparedView view)
        {
            var owner = view.Root(Root); var type = typeof(CanonicalInputAdapterState);
            var result = new CanonicalInputAdapterState {
                Version = (int)view.Field(owner, type, nameof(Version))!, MovieFrame = (long)view.Field(owner, type, nameof(MovieFrame))!,
                Suspended = (bool)view.Field(owner, type, nameof(Suspended))!,
                AuthoredEdges = (GameInputChannel[])view.Field(owner, type, nameof(AuthoredEdges))!,
                KeyboardPrevious = (short[])view.Field(owner, type, nameof(KeyboardPrevious))!,
                KeyboardCurrent = (short[])view.Field(owner, type, nameof(KeyboardCurrent))!,
                ConfiguredKeys = (short[])view.Field(owner, type, nameof(ConfiguredKeys))! };
            return result.Copy();
        }

        internal CanonicalInputAdapterState Copy()
        {
            Validate();
            return new CanonicalInputAdapterState { Version = Version, MovieFrame = MovieFrame, Suspended = Suspended,
                AuthoredEdges = (GameInputChannel[])AuthoredEdges.Clone(), KeyboardPrevious = (short[])KeyboardPrevious.Clone(),
                KeyboardCurrent = (short[])KeyboardCurrent.Clone(), ConfiguredKeys = (short[])ConfiguredKeys.Clone() };
        }
        internal void Validate()
        {
            if (Version != 1 || MovieFrame < 0 || MovieFrame >= MovieProtocolV2.MaximumExpandedFrames || AuthoredEdges == null
                || AuthoredEdges.Length > 3 || !AuthoredEdges.SequenceEqual(AuthoredEdges.Distinct().OrderBy(channel => channel))
                || AuthoredEdges.Any(channel => channel != GameInputChannel.Hero && channel != GameInputChannel.PreMenu && channel != GameInputChannel.Binder)
                || KeyboardPrevious == null || KeyboardCurrent == null || ConfiguredKeys == null || ConfiguredKeys.Length > 1024
                || !ConfiguredKeys.SequenceEqual(ConfiguredKeys.Distinct().OrderBy(key => key))
                || ConfiguredKeys.Any(key => !CustomKeyInput.Names.ContainsKey(key))
                || KeyboardPrevious.Length > ConfiguredKeys.Length || KeyboardCurrent.Length > ConfiguredKeys.Length
                || KeyboardPrevious.Concat(KeyboardCurrent).Any(key => !ConfiguredKeys.Contains(key)))
                throw new InvalidDataException("Canonical input adapter cursor/edge layout differs.");
            var keys = new CustomKeyState(); keys.Restore(KeyboardPrevious, KeyboardCurrent);
        }
        internal void RequireMovie(MovieV2Document movie)
        {
            Validate();
            if (movie == null || movie.Runs.Count == 0 || movie.Runs.Count > 100000
                || movie.Runs.Any(run => run == null || run.RepeatCount <= 0 || run.RepeatCount > MovieProtocolV2.MaximumExpandedFrames)
                || movie.Runs.Sum(run => run.RepeatCount) > MovieProtocolV2.MaximumExpandedFrames
                || MovieFrame >= movie.Runs.Sum(run => run.RepeatCount)
                || !ConfiguredKeys.SequenceEqual(CustomKeyInput.Keys(movie).OrderBy(key => key)))
                throw new InvalidDataException("Canonical input adapter Movie or configured key set differs.");
        }
    }
}
