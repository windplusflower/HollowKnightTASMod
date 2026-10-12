using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Runtime.FullRun;

namespace HollowKnightTAS.Runtime.Input
{
    public sealed partial class FullRunActionSetAdapter
    {
        internal static Type[] CanonicalSourceTypes => new[] { typeof(FullRunBindingSource), typeof(GameInputChannel) };
        internal static string CanonicalSourceSchema => string.Join(";", CanonicalCarryGraphCodec.Fields(typeof(FullRunBindingSource))
            .Select(field => field.Name + "=" + CanonicalCarryGraphCodec.Identity(field.FieldType)));
        internal void AddCanonicalSourceRoots(IDictionary<string, object> roots)
        {
            if (disposed || !hooked || nativeFrameActive || sampleIndex != 0 || fault.Length != 0 || leases.Count > 64)
                throw new InvalidOperationException("Canonical sources require the original parked input adapter.");
            foreach (var lease in leases) {
                var names = roots.Where(pair => pair.Key.StartsWith("R6/InputActionSet/", StringComparison.Ordinal)
                    && ReferenceEquals(pair.Value, lease.Set)).Select(pair => pair.Key).ToArray();
                if (names.Length != 1) throw new InvalidDataException("Canonical injected sources have no exact original action-set root.");
                lease.AddCanonicalRoots(roots, names[0]);
            }
            if (currentFrame < 0 || authoredSampleCounts.Count != 0 || transitionedEdgeChannels.Count != 0)
                throw new InvalidOperationException("Canonical adapter state is outside an unconsumed Movie boundary.");
            var state = new CanonicalInputAdapterState { MovieFrame = currentFrame, Suspended = suspended,
                AuthoredEdges = authoredEdgeChannels.OrderBy(channel => channel).ToArray(),
                KeyboardPrevious = keyboard?.CapturePrevious() ?? Array.Empty<short>(),
                KeyboardCurrent = keyboard?.CaptureCurrent() ?? Array.Empty<short>(),
                ConfiguredKeys = keyboard?.CaptureConfigured() ?? Array.Empty<short>() };
            state.Validate(); roots.Add(CanonicalInputAdapterState.Root, state);
        }

        // New restore consumes the prepared SDK graph without detaching its
        // existing leases. The old ImportCheckpoint tears down/re-attaches
        // those sources and must not be used after applying this graph.
        internal Action PrepareCanonicalContinuation(CanonicalInputAdapterState saved, MovieV2Document nextMovie)
        {
            if (disposed || !hooked || !replaying || nativeFrameActive || sampleIndex != 0 || checkpointInputImported
                || authoredSampleCounts.Count != 0 || transitionedEdgeChannels.Count != 0 || fault.Length != 0 || keyboard == null)
                throw new InvalidOperationException("Canonical input continuation requires a fresh parked replay adapter.");
            var state = saved.Copy(); state.RequireMovie(nextMovie);
            var starts = new long[nextMovie.Runs.Count]; long total = 0; NativeFrameRun? run = null;
            for (var index = 0; index < starts.Length; index++) {
                starts[index] = total; total = checked(total + nextMovie.Runs[index].RepeatCount);
                if (state.MovieFrame >= starts[index] && state.MovieFrame < total) run = nextMovie.Runs[index];
            }
            if (run == null || run.Samples.Count > MovieProtocolV2.MaximumSamplesPerFrame)
                throw new InvalidDataException("Canonical input continuation has no bounded pending Movie run.");
            var custom = run.Samples.Where(sample => sample.Channel == GameInputChannel.CustomKey).ToArray();
            var pending = run.Samples.Where(sample => sample.Channel != GameInputChannel.CustomKey).ToArray();
            var restoreKeys = keyboard.PrepareCanonicalRestore(state.KeyboardPrevious, state.KeyboardCurrent, state.ConfiguredKeys);
            var nextAuthored = run.Authored;
            var preparationFrame = currentFrame; var preparationMovie = movie; var preparationKeyboard = keyboard;
            var preparationLeases = leases.ToArray();
            return () => {
                if (checkpointInputImported || disposed || nativeFrameActive || sampleIndex != 0 || fault.Length != 0
                    || currentFrame != preparationFrame || !ReferenceEquals(movie, preparationMovie)
                    || !ReferenceEquals(keyboard, preparationKeyboard) || leases.Count != preparationLeases.Length
                    || leases.Where((lease, index) => !ReferenceEquals(lease, preparationLeases[index])).Any())
                    throw new InvalidOperationException("Canonical input continuation is stale or already applied.");
                checkpointInputImported = true;
                movie = nextMovie; runStarts = starts; recording = false; replaying = true;
                currentFrame = state.MovieFrame; sampleIndex = 0; authored = nextAuthored; suspended = state.Suspended;
                expected = pending; customSamples = custom;
                authoredEdgeChannels.Clear(); foreach (var channel in state.AuthoredEdges) authoredEdgeChannels.Add(channel);
                restoreKeys();
            };
        }

        internal int ValidateCanonicalSourceBindings(CanonicalCarryGraphCodec.PreparedView view,
            IReadOnlyDictionary<string, object> roots)
        {
            if (disposed || !hooked || nativeFrameActive || sampleIndex != 0 || fault.Length != 0 || leases.Count > 64)
                throw new InvalidOperationException("Canonical source binding requires the parked input adapter.");
            var count = 0;
            foreach (var lease in leases) {
                var names = roots.Where(pair => pair.Key.StartsWith("R6/InputActionSet/", StringComparison.Ordinal)
                    && ReferenceEquals(pair.Value, lease.Set)).Select(pair => pair.Key).ToArray();
                if (names.Length != 1) throw new InvalidDataException("Canonical source binding has no exact action-set root.");
                count = checked(count + lease.ValidateCanonicalBindings(view, names[0]));
            }
            if (roots.Keys.Count(key => key.StartsWith("R6/InputTAS-source/", StringComparison.Ordinal)) != count)
                throw new InvalidDataException("Canonical graph includes injected sources outside the actual leases.");
            return count;
        }

        internal bool PrepareCanonicalCursorAudit(CanonicalCarryGraphCodec.PreparedView view)
        {
            var state = CanonicalInputAdapterState.FromPrepared(view);
            // Manual recording has no future Movie until its recording is
            // finalized. It still carries its actual cursor and edges; this
            // read-only source audit must not invent future samples.
            if (!replaying || movie == null) return false;
            PrepareCanonicalContinuation(state, movie);
            return true;
        }
    }
}
