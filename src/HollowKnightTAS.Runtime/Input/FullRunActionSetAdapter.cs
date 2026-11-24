using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HollowKnightTAS.Core.Movie;
using InControl;

namespace HollowKnightTAS.Runtime.Input
{
    public sealed partial class FullRunActionSetAdapter : IDisposable
    {
        private static readonly FieldInfo OneAxisOwnerEnabledField = typeof(OneAxisInputControl).GetField("ownerEnabled", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(OneAxisInputControl).FullName, "ownerEnabled");
        private static readonly FieldInfo OneAxisPendingTickField = typeof(OneAxisInputControl).GetField("pendingTick", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(OneAxisInputControl).FullName, "pendingTick");
        private static readonly FieldInfo OneAxisPendingCommitField = typeof(OneAxisInputControl).GetField("pendingCommit", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(OneAxisInputControl).FullName, "pendingCommit");
        private static readonly FieldInfo OneAxisClearInputStateField = typeof(OneAxisInputControl).GetField("clearInputState", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(OneAxisInputControl).FullName, "clearInputState");
        private static readonly FieldInfo OneAxisLastStateField = typeof(OneAxisInputControl).GetField("lastState", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(OneAxisInputControl).FullName, "lastState");
        private static readonly FieldInfo OneAxisNextStateField = typeof(OneAxisInputControl).GetField("nextState", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(OneAxisInputControl).FullName, "nextState");
        private static readonly FieldInfo OneAxisThisStateField = typeof(OneAxisInputControl).GetField("thisState", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(OneAxisInputControl).FullName, "thisState");
        private static readonly FieldInfo PlayerActionSetOneAxisActionsField = typeof(PlayerActionSet).GetField("oneAxisActions", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(PlayerActionSet).FullName, "oneAxisActions");
        private static readonly FieldInfo PlayerActionSetTwoAxisActionsField = typeof(PlayerActionSet).GetField("twoAxisActions", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(PlayerActionSet).FullName, "twoAxisActions");
        private static readonly FieldInfo PlayerActionSetUpdateTickField = typeof(PlayerActionSet).GetField("<UpdateTick>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(PlayerActionSet).FullName, "<UpdateTick>k__BackingField");
        private static readonly Action<PlayerOneAxisAction, ulong, float> PlayerOneAxisActionUpdate = (Action<PlayerOneAxisAction, ulong, float>)Delegate.CreateDelegate(
            typeof(Action<PlayerOneAxisAction, ulong, float>),
            typeof(PlayerOneAxisAction).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(ulong), typeof(float) }, null)
                ?? throw new MissingMethodException(typeof(PlayerOneAxisAction).FullName, "Update"));
        private static readonly Action<PlayerTwoAxisAction, ulong, float> PlayerTwoAxisActionUpdate = (Action<PlayerTwoAxisAction, ulong, float>)Delegate.CreateDelegate(
            typeof(Action<PlayerTwoAxisAction, ulong, float>),
            typeof(PlayerTwoAxisAction).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(ulong), typeof(float) }, null)
                ?? throw new MissingMethodException(typeof(PlayerTwoAxisAction).FullName, "Update"));

        private readonly List<ActionSetLease> leases = new List<ActionSetLease>();
        private readonly CustomKnightInputGuard customKnightInputs = new CustomKnightInputGuard();
        private readonly List<int> includeScratch = new List<int>(8);
        private readonly List<int> excludeScratch = new List<int>(8);
        private MovieV2Document? movie;
        private FullRunKeyboardBridge? keyboard;
        private IReadOnlyList<GameInputSample> customSamples = Array.Empty<GameInputSample>();
        private void PrepareSamples(IReadOnlyList<GameInputSample> samples)
        {
            customSamples = samples.Where(s => s.Channel == GameInputChannel.CustomKey).ToArray();
            expected = samples.Where(s => s.Channel != GameInputChannel.CustomKey).ToArray();
            keyboard?.Prepare(customSamples);
        }
        private long[]? runStarts;
        private IReadOnlyList<GameInputSample> expected = Array.Empty<GameInputSample>();
        private long currentFrame = -1;
        private int sampleIndex;
        private bool authored;
        private readonly HashSet<GameInputChannel> authoredEdgeChannels = new HashSet<GameInputChannel>();
        private readonly HashSet<GameInputChannel> transitionedEdgeChannels = new HashSet<GameInputChannel>();
        private readonly Dictionary<GameInputChannel, int> authoredSampleCounts = new Dictionary<GameInputChannel, int>();
        private bool recording;
        private bool replaying;
        private bool hooked;
        private bool suspended = true;
        private bool nativeFrameActive;
        private Func<bool>? nativeFrameRunning;
        private bool disposed;
        private FullRunCutsceneInput? cutsceneInput;
        private string fault = string.Empty;

        public event Action<long, GameInputSample, ulong>? Sampled;
        public event Action<long, GameInputSample, ulong, ulong>? EdgesObserved;
        public event Action<string>? Faulted;

        public string Fault => fault;
        public int ConsumedSamples => sampleIndex;
        public int ExpectedSamples => expected.Count;
        public IReadOnlyDictionary<string, string> ReadBindingLabels()
        {
            var result = new Dictionary<string, string>();
            var hero = InputHandler.Instance?.inputActions;
            if (hero == null) return result;
            var actions = GetActions(hero, GameInputChannel.Hero);
            var names = new[] { "Left", "Right", "Up", "Down", "", "", "", "",
                "Submit", "Cancel", "Jump", "", "Dash", "SuperDash", "DreamNail",
                "Attack", "Cast", "", "QuickMap", "QuickCast", "", "",
                "OpenInventory", "PaneRight", "PaneLeft", "Pause" };
            var lease = leases.FirstOrDefault(value => ReferenceEquals(value.Set, hero));
            for (var index = 0; index < names.Length; index++)
            {
                if (names[index].Length == 0) continue;
                Add(names[index], index);
            }
            return result;

            void Add(string name, int index)
            {
                // Playback leases replace live bindings; publish the originals, never TAS sources.
                var bindings = lease == null ? actions[index].UnfilteredBindings.ToArray()
                    : lease.OriginalBindings(index);
                var keyboard = bindings.OfType<KeyBindingSource>().Select(value => value.Name).Distinct().ToArray();
                // Machine-readable keyboard-only combos; labels may be localized or controller names.
                result["inputKeyboard." + name] = string.Join(";", bindings.OfType<KeyBindingSource>().Select(value =>
                    string.Join(",", Enumerable.Range(0, value.Control.IncludeCount).Select(i => value.Control.GetInclude(i).ToString())
                        .Concat(Enumerable.Range(0, value.Control.ExcludeCount).Select(i => "!" + value.Control.GetExclude(i))))));
                result["inputBinding." + name] = string.Join(" / ", keyboard.Length != 0
                    ? keyboard : bindings.Select(value => value.Name).Distinct().ToArray());
            }
        }
        public bool IsFrameInputEnabled => !suspended;
        public bool IsNativeFrameActive => nativeFrameActive && (nativeFrameRunning?.Invoke() ?? true);

        public void SetNativeFrameActive(bool active) => nativeFrameActive = active;
        public void SetNativeFrameRunning(Func<bool> running)
            => nativeFrameRunning = running ?? throw new ArgumentNullException(nameof(running));

        public void SetFrameInputEnabled(bool enabled)
        {
            if (!recording && !replaying)
                throw new InvalidOperationException("Full-run input adapter has not started.");
            if (suspended == !enabled) return;
            suspended = !enabled;
            if (suspended)
            {
                foreach (var lease in leases)
                    lease.Prepare(new short[MovieProtocolV2.ExpectedValueCount(lease.Channel)]);
            }
            else if (recording)
            {
                foreach (var lease in leases) lease.Restore();
                leases.Clear();
            }
        }

        public GameInputSample? PeekMouse(GameInputChannel channel)
        {
            if (!MovieProtocolV2.IsMouseChannel(channel) || currentFrame < 0)
                throw new InvalidOperationException("Mouse input is outside a prepared native frame.");
            if (!replaying) return null;
            if (authored)
                return expected.LastOrDefault(s => s.Channel == channel)
                    ?? new GameInputSample(channel, Array.Empty<short>(), new MouseFrameState(0, 0, 0, 0, 0, 0));
            SkipRedundantSamplesBefore(channel);
            if (sampleIndex >= expected.Count || expected[sampleIndex].Channel != channel
                || expected[sampleIndex].Mouse == null)
                throw new InvalidDataException("Mouse channel/order mismatch at native frame "
                    + currentFrame + ", sample " + sampleIndex + ".");
            return expected[sampleIndex];
        }

        public void CompleteMouse(GameInputChannel channel, MouseFrameState state, ulong updateTick)
        {
            if (!MovieProtocolV2.IsMouseChannel(channel) || currentFrame < 0 || state == null)
                throw new InvalidOperationException("Mouse input is outside a prepared native frame.");
            var actual = new GameInputSample(channel, Array.Empty<short>(), state);
            if (replaying)
            {
                var desired = PeekMouse(channel)!.Mouse!;
                if (state.XQ16 != desired.XQ16 || state.YQ16 != desired.YQ16
                    || state.DeltaXQ15 != desired.DeltaXQ15
                    || state.DeltaYQ15 != desired.DeltaYQ15
                    || state.Buttons != desired.Buttons || state.WheelQ15 != desired.WheelQ15)
                    throw new InvalidDataException("Mouse state mismatch at native frame "
                        + currentFrame + ", sample " + sampleIndex + ".");
            }
            Sampled?.Invoke(currentFrame, actual, updateTick);
            sampleIndex++;
        }

        public void ReportExternalFault(string message) => Fail(message);

        public void StartRecording()
        {
            RequireFresh();
            recording = true;
            Hook();
        }

        public void StartReplay(MovieV2Document movie)
        {
            RequireFresh();
            SetReplayMovie(movie);
            Hook();
        }

        public void ReplaceFutureMovie(MovieV2Document movie, long frame)
        {
            if (disposed || !hooked || currentFrame != frame || sampleIndex != 0)
                throw new InvalidOperationException("Input replacement requires an unconsumed frame boundary.");
            SetReplayMovie(movie);
            authoredSampleCounts.Clear();
            var run = RunAt(frame);
            authored = run.Authored;
            PrepareSamples(run.Samples);
        }

        public void SwitchToReplayForInProcessRestore(MovieV2Document movie)
        {
            if (disposed) throw new ObjectDisposedException(nameof(FullRunActionSetAdapter));
            SetReplayMovie(movie);
            authoredSampleCounts.Clear();
            sampleIndex = 0;
        }

        private void SetReplayMovie(MovieV2Document movie)
        {
            this.movie = movie ?? throw new ArgumentNullException(nameof(movie));
            keyboard ??= new FullRunKeyboardBridge(() => !suspended && IsNativeFrameActive);
            keyboard.Configure(movie);
            var starts = new long[movie.Runs.Count];
            long total = 0;
            for (var index = 0; index < starts.Length; index++)
            {
                starts[index] = total;
                total = checked(total + movie.Runs[index].RepeatCount);
            }
            runStarts = starts;
            recording = false;
            replaying = true;
        }

        public void PrepareFrame(long frameIndex, bool atReplayEnd = false)
        {
            if (!recording && !replaying)
                throw new InvalidOperationException("Full-run input adapter has not started.");
            if (frameIndex < 0 || (currentFrame >= 0 && frameIndex != currentFrame + 1))
                throw new InvalidOperationException("Native frame preparation is not sequential.");
            currentFrame = frameIndex;
            sampleIndex = 0;
            authoredSampleCounts.Clear();
            var run = replaying && !atReplayEnd ? RunAt(frameIndex) : null;
            authored = run?.Authored == true;
            PrepareSamples(run?.Samples ?? Array.Empty<GameInputSample>());
        }

        public void CompleteFrame(long frameIndex)
        {
            foreach (var sample in customSamples) Sampled?.Invoke(frameIndex, sample, 0);
            keyboard?.Complete();
            // InControl can report the same edge on multiple updates in one frame.
            // Retire an authored-to-recorded transition only after the whole frame.
            foreach (var channel in transitionedEdgeChannels) authoredEdgeChannels.Remove(channel);
            transitionedEdgeChannels.Clear();
            if (frameIndex != currentFrame)
            {
                Fail("Native frame completion differs from prepared input frame.");
                return;
            }
            if (replaying && !authored)
                while (sampleIndex < expected.Count && IsRedundant(sampleIndex)) sampleIndex++;
            if (replaying && !authored && sampleIndex != expected.Count)
                Fail("Input sample count mismatch at native frame " + frameIndex
                    + ": expected=" + expected.Count + ";actual=" + sampleIndex);
        }

        private void Hook()
        {
            cutsceneInput = new FullRunCutsceneInput(() => IsNativeFrameActive && IsFrameInputEnabled
                && InputHandler.Instance?.inputActions is HeroActions actions
                && GetActions(actions, GameInputChannel.Hero).Any(action => action.WasPressed));
            On.InControl.PlayerActionSet.Update += OnPlayerActionSetUpdate;
            hooked = true;
        }

        private void RequireFresh()
        {
            if (disposed || hooked || recording || replaying)
                throw new InvalidOperationException("Full-run input adapter is already active.");
        }

        private NativeFrameRun RunAt(long frame)
        {
            var starts = runStarts ?? throw new InvalidOperationException("Replay index is unavailable.");
            var runs = movie?.Runs ?? throw new InvalidOperationException("Replay movie is unavailable.");
            var low = 0;
            var high = starts.Length - 1;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                var start = starts[middle];
                var end = start + runs[middle].RepeatCount;
                if (frame < start) high = middle - 1;
                else if (frame >= end) low = middle + 1;
                else return runs[middle];
            }
            throw new InvalidOperationException("Movie has no input frame at native frame " + frame + ".");
        }

        private void OnPlayerActionSetUpdate(On.InControl.PlayerActionSet.orig_Update original,
            PlayerActionSet self, ulong updateTick, float deltaTime)
        {
            // Window messages can update InControl after the PlayerLoop has completed.
            // They belong to no Movie frame and must not consume the prepared next frame.
            if (!IsNativeFrameActive) return;
            var originalCalled = false;
            var isThirdParty = false;
            try
            {
                // Cosmetic hotkeys are deliberately neutral during both recording and replay.
                // They are not gameplay channels and must not consume Movie input samples.
                if (customKnightInputs.TryPrepare(self))
                {
                    originalCalled = true;
                    original(self, updateTick, deltaTime);
                    return;
                }
                if (!TryIdentify(self, out var channel))
                {
                    isThirdParty = true;
                    UpdateThirdPartyActionSet(self, updateTick, deltaTime);
                    return;
                }
                if (fault.Length != 0 || currentFrame < 0)
                {
                    originalCalled = true;
                    original(self, updateTick, deltaTime);
                    return;
                }
                var actions = GetActions(self, channel);
                if (suspended)
                {
                    FindOrAttach(self, channel, actions).Prepare(new short[actions.Length]);
                    originalCalled = true;
                    original(self, updateTick, deltaTime);
                    return;
                }
                GameInputSample? desired = null;
                var redundantExtra = false;
                if (replaying)
                {
                    if (authored)
                    {
                        var channelSamples = expected.Where(s => s.Channel == channel).ToArray();
                        authoredSampleCounts.TryGetValue(channel, out var ordinal);
                        desired = channelSamples.Length == 0 ? new GameInputSample(channel, new short[actions.Length], null)
                            : channelSamples[Math.Min(ordinal, channelSamples.Length - 1)];
                        authoredSampleCounts[channel] = ordinal + 1;
                        authoredEdgeChannels.Add(channel);
                    }
                    else
                    {
                    SkipRedundantSamplesBefore(channel);
                    if (sampleIndex >= expected.Count)
                    {
                        // InControl may update an unchanged action set an extra time in
                        // the same PlayerLoop. Reuse the last edge-free state, then
                        // verify the actual values and edges below.
                        var last = expected.Count == 0 ? null : expected[expected.Count - 1];
                        if (last == null || last.Channel != channel
                            || last.PressedMask != 0 || last.ReleasedMask != 0)
                            throw new InvalidDataException("Input channel/order mismatch at native frame "
                                + currentFrame + ", sample " + sampleIndex + ".");
                        desired = last;
                        redundantExtra = true;
                    }
                    else
                    {
                        if (expected[sampleIndex].Channel != channel)
                            throw new InvalidDataException("Input channel/order mismatch at native frame "
                                + currentFrame + ", sample " + sampleIndex + ".");
                        desired = expected[sampleIndex];
                    }
                    }
                    var lease = FindOrAttach(self, channel, actions);
                    lease.Prepare(desired.Values);
                }
                originalCalled = true;
                original(self, updateTick, deltaTime);
                var values = new short[actions.Length];
                ulong pressed = 0;
                ulong released = 0;
                for (var index = 0; index < actions.Length; index++)
                {
                    values[index] = Quantize(actions[index].Value);
                    if (actions[index].WasPressed) pressed |= 1UL << index;
                    if (actions[index].WasReleased) released |= 1UL << index;
                }
                var observed = new GameInputSample(channel, values, null, pressed, released);
                if (desired != null)
                {
                    for (var index = 0; index < values.Length; index++)
                        if (Math.Abs(values[index] - desired.Values[index]) > 1)
                            throw new InvalidDataException("Input value mismatch at native frame "
                                + currentFrame + ", sample " + sampleIndex + ", action " + index + ".");
                    if (!authored && authoredEdgeChannels.Contains(channel)) transitionedEdgeChannels.Add(channel);
                    if (!authored && !authoredEdgeChannels.Contains(channel) && (pressed != desired.PressedMask || released != desired.ReleasedMask))
                        throw new InvalidDataException("Input edge mismatch at Movie frame "
                            + currentFrame + ", sample " + sampleIndex + ", channel=" + channel
                            + ", tick=" + updateTick + ", expectedPressed=" + desired.PressedMask
                            + ", actualPressed=" + pressed + ", expectedReleased=" + desired.ReleasedMask
                            + ", actualReleased=" + released + ".");
                }
                if (!redundantExtra)
                {
                    Sampled?.Invoke(currentFrame, observed, updateTick);
                    EdgesObserved?.Invoke(currentFrame, observed, pressed, released);
                    sampleIndex++;
                }
            }
            catch (Exception exception)
            {
                Fail(exception.GetType().Name + ": " + exception.Message);
                if (!originalCalled && !isThirdParty) original(self, updateTick, deltaTime);
            }
        }

        private void UpdateThirdPartyActionSet(PlayerActionSet self, ulong updateTick, float deltaTime)
        {
            var allowCustomKeys = replaying && !suspended && IsNativeFrameActive
                && fault.Length == 0 && currentFrame >= 0 && self.Enabled && keyboard != null;
            Func<short, bool>? isKeyCodeHeld = allowCustomKeys ? keyboard!.IsVirtualKeyHeld : null;

            var actions = self.Actions;
            for (var index = 0; index < actions.Count; index++)
            {
                var action = actions[index];
                if (action == null) continue;
                var pressed = allowCustomKeys && EvaluateActionPressed(action, isKeyCodeHeld!, includeScratch, excludeScratch);
                OneAxisOwnerEnabledField.SetValue(action, self.Enabled);
                SanitizePendingTick(action, updateTick);
                action.UpdateWithState(pressed, updateTick, deltaTime);
                action.Commit();
            }

            if (PlayerActionSetOneAxisActionsField.GetValue(self) is List<PlayerOneAxisAction> oneAxisList)
            {
                for (var index = 0; index < oneAxisList.Count; index++)
                {
                    var oneAxis = oneAxisList[index];
                    if (oneAxis == null) continue;
                    SanitizePendingTick(oneAxis, updateTick);
                    PlayerOneAxisActionUpdate(oneAxis, updateTick, deltaTime);
                }
            }

            if (PlayerActionSetTwoAxisActionsField.GetValue(self) is List<PlayerTwoAxisAction> twoAxisList)
            {
                for (var index = 0; index < twoAxisList.Count; index++)
                {
                    var twoAxis = twoAxisList[index];
                    if (twoAxis == null) continue;
                    SanitizePendingTick(twoAxis.Left, updateTick);
                    SanitizePendingTick(twoAxis.Right, updateTick);
                    SanitizePendingTick(twoAxis.Up, updateTick);
                    SanitizePendingTick(twoAxis.Down, updateTick);
                    PlayerTwoAxisActionUpdate(twoAxis, updateTick, deltaTime);
                }
            }

            if (self.UpdateTick < updateTick)
                PlayerActionSetUpdateTickField.SetValue(self, updateTick);
        }

        private static bool EvaluateActionPressed(
            PlayerAction action,
            Func<short, bool> isKeyCodeHeld,
            List<int> includes,
            List<int> excludes)
        {
            var bindings = action.UnfilteredBindings;
            for (var index = 0; index < bindings.Count; index++)
            {
                if (bindings[index] is not KeyBindingSource keyBinding) continue;
                var combo = keyBinding.Control;
                var includeCount = combo.IncludeCount;
                if (includeCount == 0) continue;

                includes.Clear();
                for (var i = 0; i < includeCount; i++)
                    includes.Add((int)combo.GetInclude(i));

                excludes.Clear();
                var excludeCount = combo.ExcludeCount;
                for (var i = 0; i < excludeCount; i++)
                    excludes.Add((int)combo.GetExclude(i));

                if (InControlKeyComboEvaluator.EvaluateCombo(includes, excludes, isKeyCodeHeld))
                    return true;
            }
            return false;
        }

        private static void SanitizePendingTick(OneAxisInputControl control, ulong updateTick)
        {
            var pendingTick = (ulong)OneAxisPendingTickField.GetValue(control)!;
            if (pendingTick > updateTick)
            {
                OneAxisPendingTickField.SetValue(control, 0UL);
                OneAxisPendingCommitField.SetValue(control, false);
            }
            else if ((bool)OneAxisPendingCommitField.GetValue(control)! && pendingTick != updateTick)
            {
                OneAxisPendingCommitField.SetValue(control, false);
            }
        }

        private static bool TryIdentify(PlayerActionSet set, out GameInputChannel channel)
        {
            if (set is HeroActions) { channel = GameInputChannel.Hero; return true; }
            if (set is PreMenuInputModuleActionAdaptor.PreMenuInputModuleActions)
            {
                channel = GameInputChannel.PreMenu;
                return true;
            }
            if (set is InputModuleBinder.MyActionSet) { channel = GameInputChannel.Binder; return true; }
            channel = default;
            return false;
        }

        private static GameInputChannel Identify(PlayerActionSet set)
            => TryIdentify(set, out var channel)
                ? channel
                : throw new InvalidDataException("Unknown PlayerActionSet entered the full-run input path: "
                    + set.GetType().FullName);

        private static PlayerAction[] GetActions(PlayerActionSet set, GameInputChannel channel)
        {
            if (channel == GameInputChannel.Hero)
            {
                var a = (HeroActions)set;
                return new[] { a.left, a.right, a.up, a.down,
                    a.rs_left, a.rs_right, a.rs_up, a.rs_down,
                    a.menuSubmit, a.menuCancel, a.jump, a.evade, a.dash,
                    a.superDash, a.dreamNail, a.attack, a.cast, a.focus,
                    a.quickMap, a.quickCast, a.textSpeedup, a.skipCutscene,
                    a.openInventory, a.paneRight, a.paneLeft, a.pause };
            }
            if (channel == GameInputChannel.PreMenu)
            {
                var a = (PreMenuInputModuleActionAdaptor.PreMenuInputModuleActions)set;
                return new[] { a.Submit, a.Cancel, a.Left, a.Right, a.Up, a.Down };
            }
            if (channel == GameInputChannel.Binder)
            {
                var a = (InputModuleBinder.MyActionSet)set;
                return new[] { a.Submit, a.Cancel, a.Left, a.Right, a.Up, a.Down };
            }
            throw new InvalidDataException("Unsupported action channel.");
        }

        private ActionSetLease FindOrAttach(PlayerActionSet set, GameInputChannel channel,
            PlayerAction[] actions)
        {
            foreach (var lease in leases)
                if (ReferenceEquals(lease.Set, set)) return lease;
            for (var index = leases.Count - 1; index >= 0; index--)
                if (leases[index].Channel == channel)
                {
                    leases[index].Restore();
                    leases.RemoveAt(index);
                }
            var attached = ActionSetLease.Attach(set, channel, actions);
            leases.Add(attached);
            return attached;
        }

        private void SkipRedundantSamplesBefore(GameInputChannel channel)
        {
            while (sampleIndex < expected.Count
                && expected[sampleIndex].Channel != channel
                && IsRedundant(sampleIndex))
                sampleIndex++;
        }

        private bool IsRedundant(int index)
        {
            var sample = expected[index];
            for (var previous = index - 1; previous >= 0; previous--)
            {
                var candidate = expected[previous];
                if (candidate.Channel != sample.Channel) continue;
                if (candidate.PressedMask != sample.PressedMask
                    || candidate.ReleasedMask != sample.ReleasedMask
                    || candidate.Values.Count != sample.Values.Count) return false;
                for (var value = 0; value < sample.Values.Count; value++)
                    if (candidate.Values[value] != sample.Values[value]) return false;
                var left = candidate.Mouse;
                var right = sample.Mouse;
                return left == null && right == null || left != null && right != null
                    && left.XQ16 == right.XQ16 && left.YQ16 == right.YQ16
                    && left.DeltaXQ15 == right.DeltaXQ15
                    && left.DeltaYQ15 == right.DeltaYQ15
                    && left.Buttons == right.Buttons && left.WheelQ15 == right.WheelQ15;
            }
            return false;
        }

        private static short Quantize(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < -1.001f || value > 1.001f)
                throw new InvalidDataException("Game action value is outside the input schema.");
            return checked((short)Math.Round(Math.Max(-1f, Math.Min(1f, value)) * short.MaxValue));
        }

        private void Fail(string message)
        {
            if (fault.Length != 0) return;
            fault = message;
            Faulted?.Invoke(message);
        }

        public void Dispose()
        {
            cutsceneInput?.Dispose();
            if (disposed) return;
            disposed = true;
            if (hooked) On.InControl.PlayerActionSet.Update -= OnPlayerActionSetUpdate;
            var errors = new List<string>();
            try { keyboard?.Dispose(); }
            catch (Exception exception) { errors.Add(exception.Message); }
            try { customKnightInputs.Dispose(); }
            catch (Exception exception) { errors.Add(exception.Message); }
            foreach (var lease in leases)
                try { lease.Restore(); }
                catch (Exception exception) { errors.Add(exception.Message); }
            leases.Clear();
            if (errors.Count != 0)
                throw new InvalidOperationException("Full-run bindings were not restored: "
                    + string.Join("; ", errors));
        }

        private sealed class ActionSetLease
        {
            private readonly PlayerAction[] actions;
            private readonly BindingSource[][] originals;
            private readonly FullRunBindingSource[] injected;
            private bool restored;

            private ActionSetLease(PlayerActionSet set, GameInputChannel channel,
                PlayerAction[] actions, BindingSource[][] originals,
                FullRunBindingSource[] injected)
            {
                Set = set; Channel = channel;
                this.actions = actions; this.originals = originals; this.injected = injected;
            }

            public PlayerActionSet Set { get; }
            public GameInputChannel Channel { get; }
            public BindingSource[] OriginalBindings(int index) => originals[index];
            internal int ValidateCanonicalBindings(HollowKnightTAS.Runtime.FullRun.CanonicalCarryGraphCodec.PreparedView view, string binding)
            {
                if (restored) throw new InvalidDataException("Canonical lease has already restored its original bindings.");
                return HollowKnightTAS.Runtime.FullRun.CanonicalInputLeaseBindings.Validate(view, binding, Set, Channel, actions, injected, originals);
            }
            internal void AddCanonicalRoots(IDictionary<string, object> roots, string binding)
            {
                if (restored || injected.Length != actions.Length || originals.Length != actions.Length || actions.Length > 1024)
                    throw new InvalidDataException("Canonical lease sources are incomplete or already restored.");
                for (var index = 0; index < injected.Length; index++) {
                    if (injected[index] == null || originals[index] == null || originals[index].Length > 256)
                        throw new InvalidDataException("Canonical action source slots exceed their bound.");
                    roots.Add("R6/InputTAS-source/" + binding + "/" + index, injected[index]);
                    for (var slot = 0; slot < originals[index].Length; slot++) {
                        if (originals[index][slot] == null) throw new InvalidDataException("Canonical original input binding is absent.");
                        roots.Add("R6/InputOriginal-source/" + binding + "/" + index + "/" + slot, originals[index][slot]);
                    }
                }
            }

            public static ActionSetLease Attach(PlayerActionSet set, GameInputChannel channel,
                PlayerAction[] actions)
            {
                var originals = actions.Select(action => action.UnfilteredBindings.ToArray()).ToArray();
                var injected = new FullRunBindingSource[actions.Length];
                var lease = new ActionSetLease(set, channel, actions, originals, injected);
                try
                {
                    for (var index = 0; index < actions.Length; index++)
                    {
                        injected[index] = new FullRunBindingSource(channel, index);
                        actions[index].ClearBindings();
                        if (!actions[index].AddBinding(injected[index]))
                            throw new InvalidOperationException("Synthetic action binding was rejected.");
                    }
                    return lease;
                }
                catch
                {
                    lease.Restore();
                    throw;
                }
            }

            public void Prepare(IReadOnlyList<short> values)
            {
                if (values.Count != injected.Length)
                    throw new InvalidDataException("Replay action count differs from the input schema.");
                for (var index = 0; index < injected.Length; index++)
                    injected[index].SetValue(values[index] / (float)short.MaxValue);
            }

            public void Restore()
            {
                if (restored) return;
                restored = true;
                for (var index = 0; index < actions.Length; index++)
                {
                    actions[index].ClearBindings();
                    foreach (var binding in originals[index])
                        if (!actions[index].AddBinding(binding))
                            throw new InvalidOperationException("Original action binding was rejected.");
                    var actual = actions[index].UnfilteredBindings;
                    if (actual.Count != originals[index].Length)
                        throw new InvalidOperationException("Original action binding count changed.");
                    for (var item = 0; item < actual.Count; item++)
                        if (!ReferenceEquals(actual[item], originals[index][item]))
                            throw new InvalidOperationException("Original action binding order changed.");
                }
            }
        }

        private sealed class FullRunBindingSource : BindingSource
        {
            private readonly GameInputChannel channel;
            private readonly int index;
            private float value;

            public FullRunBindingSource(GameInputChannel channel, int index)
            {
                this.channel = channel; this.index = index;
            }

            public override string Name => "HKTAS v2 " + channel + " " + index;
            public override string DeviceName => "HollowKnightTAS";
            public override InputDeviceClass DeviceClass => InputDeviceClass.Unknown;
            public override InputDeviceStyle DeviceStyle => InputDeviceStyle.Unknown;
            public override BindingSourceType BindingSourceType => BindingSourceType.UnknownDeviceBindingSource;
            public void SetValue(float current) => value = Math.Max(-1f, Math.Min(1f, current));
            public override float GetValue(InputDevice inputDevice) => value;
            public override bool GetState(InputDevice inputDevice) => value > 0.5f;
            public override bool Equals(BindingSource other) => ReferenceEquals(this, other);
            public override int GetHashCode() => base.GetHashCode();
            public override void Save(BinaryWriter writer) => writer.Write((ushort)index);
            public override void Load(BinaryReader reader, ushort dataFormatVersion) => value = 0f;
        }
    }
}
