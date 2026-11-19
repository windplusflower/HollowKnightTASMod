using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Runtime.FullRun;
using InControl;

namespace HollowKnightTAS.Runtime.Input
{
    [Serializable]
    internal sealed class CheckpointActionSetState
    {
        public GameInputChannel Channel;
        public int RegistrationOrdinal;
        public bool BindingActive;
        public Dictionary<string, string> Set = null!;
        public Dictionary<string, string>[] Actions = null!;
    }
    [Serializable]
    internal sealed class CheckpointInputState
    {
        public long MovieFrame;
        public bool Suspended;
        public GameInputChannel[] AuthoredEdges = null!;
        public short[] KeyboardPrevious = null!, KeyboardCurrent = null!;
        public Dictionary<string, string> Manager = null!;
        public CheckpointActionSetState[] Sets = null!;
    }
    public sealed partial class FullRunActionSetAdapter
    {
        private static readonly FieldInfo[] CheckpointActionFields = CheckpointScalarFields.Declared(typeof(OneAxisInputControl))
            .Concat(CheckpointScalarFields.Declared(typeof(PlayerAction)).Where(f => f.FieldType.IsPrimitive || f.FieldType.IsEnum)).ToArray();
        private static readonly FieldInfo[] CheckpointSetFields = CheckpointScalarFields.Declared(typeof(PlayerActionSet))
            .Where(f => f.FieldType.IsPrimitive || f.FieldType.IsEnum).ToArray();
        private static readonly FieldInfo[] CheckpointManagerFields = new[] { "initialTime", "currentTime", "lastUpdateTime", "currentTick", "<CommandWasPressed>k__BackingField" }
            .Select(name => typeof(InputManager).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(typeof(InputManager).FullName, name)).ToArray();
        private bool checkpointInputImported;
        private static PlayerActionSet[] CheckpointRegisteredSets() =>
            (CheckpointScalarFields.Declared(typeof(InputManager), true).Single(f => f.Name == "playerActionSets").GetValue(null)
                as IEnumerable<PlayerActionSet> ?? throw new InvalidOperationException("InControl action sets are unavailable."))
            .Where(set => set is HeroActions || set is InputModuleBinder.MyActionSet
                || set is PreMenuInputModuleActionAdaptor.PreMenuInputModuleActions).ToArray();
        internal CheckpointInputState CaptureCheckpoint()
        {
            if (disposed || !hooked || currentFrame < 0 || nativeFrameActive || sampleIndex != 0
                || authoredSampleCounts.Count != 0 || transitionedEdgeChannels.Count != 0 || fault.Length != 0)
                throw new InvalidOperationException("Input checkpoint requires an unconsumed Movie frame boundary.");
            return new CheckpointInputState
            {
                MovieFrame = currentFrame, Suspended = suspended,
                AuthoredEdges = authoredEdgeChannels.OrderBy(c => c).ToArray(),
                KeyboardPrevious = keyboard?.CapturePrevious() ?? Array.Empty<short>(),
                KeyboardCurrent = keyboard?.CaptureCurrent() ?? Array.Empty<short>(),
                Manager = CheckpointScalarFields.Capture(null, CheckpointManagerFields),
                Sets = CheckpointRegisteredSets().GroupBy(Identify).OrderBy(group => group.Key).SelectMany(group => group.Select((set, ordinal) => new CheckpointActionSetState
                {
                    Channel = group.Key, RegistrationOrdinal = ordinal, BindingActive = leases.Any(lease => ReferenceEquals(lease.Set, set)),
                    Set = CheckpointScalarFields.Capture(set, CheckpointSetFields),
                    Actions = GetActions(set, group.Key).Select(action => CheckpointScalarFields.Capture(action, CheckpointActionFields)).ToArray()
                })).ToArray()
            };
        }
        internal void ImportCheckpoint(CheckpointInputState state, MovieV2Document nextMovie)
        {
            if (disposed || !replaying || !hooked || nativeFrameActive || fault.Length != 0)
                throw new InvalidOperationException("Input continuation requires a paused replay adapter.");
            if (state.MovieFrame < 0 || state.MovieFrame >= nextMovie.Runs.Sum(r => r.RepeatCount)
                || state.Sets.Select(s => Tuple.Create(s.Channel, s.RegistrationOrdinal)).Distinct().Count() != state.Sets.Length
                || state.Sets.Where(s => s.BindingActive).Select(s => s.Channel).Distinct().Count() != state.Sets.Count(s => s.BindingActive)
                || state.AuthoredEdges.Distinct().Count() != state.AuthoredEdges.Length
                || state.AuthoredEdges.Any(c => !state.Sets.Any(s => s.Channel == c)))
                throw new InvalidDataException("Invalid input continuation frame or channel layout.");
            var channels = CheckpointRegisteredSets();
            if (channels.Length != state.Sets.Length) throw new InvalidDataException("Input continuation registered set count differs.");
            var applications = new List<Action>();
            foreach (var saved in state.Sets)
            {
                var candidates = channels.Where(set => Identify(set) == saved.Channel).ToArray();
                var savedCount = state.Sets.Count(set => set.Channel == saved.Channel);
                if (candidates.Length != savedCount || saved.RegistrationOrdinal < 0 || saved.RegistrationOrdinal >= savedCount)
                    throw new InvalidDataException("Input continuation action-set registration layout differs: " + saved.Channel);
                var target = candidates[saved.RegistrationOrdinal];
                var actions = GetActions(target, saved.Channel);
                if (actions.Length != saved.Actions.Length) throw new InvalidDataException("Input continuation action count differs.");
                applications.Add(CheckpointScalarFields.PrepareApply(target, CheckpointSetFields, saved.Set));
                for (var i = 0; i < actions.Length; i++) applications.Add(CheckpointScalarFields.PrepareApply(actions[i], CheckpointActionFields, saved.Actions[i]));
            }
            applications.Add(CheckpointScalarFields.PrepareApply(null, CheckpointManagerFields, state.Manager));
            // Validate key edges before touching the live adapter. PrepareFrame
            // computes the next run's held state from the restored previous keys.
            var keys = new CustomKeyState(); keys.Restore(state.KeyboardPrevious, state.KeyboardCurrent);
            if (state.KeyboardPrevious.Concat(state.KeyboardCurrent).Any(key => !CustomKeyInput.Keys(nextMovie).Contains(key)))
                throw new InvalidDataException("Continuation keys differ from the replay Movie.");
            SetReplayMovie(nextMovie); currentFrame = state.MovieFrame - 1;
            PrepareFrame(state.MovieFrame);
            keyboard!.Restore(state.KeyboardPrevious, state.KeyboardCurrent);
            suspended = state.Suspended; authoredEdgeChannels.Clear();
            foreach (var channel in state.AuthoredEdges) authoredEdgeChannels.Add(channel);
            foreach (var lease in leases) lease.Restore(); leases.Clear();
            foreach (var saved in state.Sets.Where(saved => saved.BindingActive))
            {
                var set = channels.Where(value => Identify(value) == saved.Channel).ElementAt(saved.RegistrationOrdinal);
                var lease = FindOrAttach(set, saved.Channel, GetActions(set, saved.Channel));
                lease.Prepare(suspended ? new short[saved.Actions.Length] : GetActions(set, saved.Channel).Select(a => Quantize(a.Value)).ToArray());
            }
            foreach (var application in applications) application();
            checkpointInputImported = true;
        }
    }
}
