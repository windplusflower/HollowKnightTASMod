using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using HollowKnightTAS.Core.Input;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.Companion.Services
{
    public sealed class InputGridRow : INotifyPropertyChanged
    {
        public InputGridRow(long tick, FrameRunCommand input, long currentTick)
        { Tick = tick; Input = input; Current = tick == currentTick ? "▶" : ""; }
        public InputGridRow(V2InputGridRow row)
        {
            Tick = row.NativeFrame;
            FramesPerSecond = row.FramesPerSecond;
            RngSeed = row.RngSeed;
            Current = row.Current;
            IsV2 = true;
            Channels = row.Channels;
            SampleCount = row.Samples.Count;
            var held = TasAction.None;
            foreach (var sample in row.Samples)
            {
                var values = sample.Values;
                if (sample.Channel == GameInputChannel.CustomKey && values[1] != 0) customKeys.Add(values[0]);
                if (sample.Channel == GameInputChannel.Hero)
                {
                    if (values[0] > 0) held |= TasAction.Left;
                    if (values[1] > 0) held |= TasAction.Right;
                    if (values[2] > 0) held |= TasAction.Up;
                    if (values[3] > 0) held |= TasAction.Down;
                    if (values[10] > 0) held |= TasAction.Jump;
                    if (values[15] > 0) held |= TasAction.Attack;
                    if (values[12] > 0) held |= TasAction.Dash;
                    if (values[16] > 0) held |= TasAction.Cast;
                    if (values[19] > 0) held |= TasAction.QuickCast;
                    if (values[13] > 0) held |= TasAction.SuperDash;
                    if (values[14] > 0) held |= TasAction.DreamNail;
                    if (values[8] > 0) Submit = true;
                    if (values[9] > 0) Cancel = true;
                    foreach (var action in MenuActions)
                        if (values[MovieV2RangeEditor.ActionIndex(GameInputChannel.Hero, action)] > 0) menuActions.Add(action);
                }
                else if (sample.Channel == GameInputChannel.PreMenu
                    || sample.Channel == GameInputChannel.Binder)
                {
                    if (values[2] > 0) held |= TasAction.Left;
                    if (values[3] > 0) held |= TasAction.Right;
                    if (values[4] > 0) held |= TasAction.Up;
                    if (values[5] > 0) held |= TasAction.Down;
                    if (values[0] > 0) Submit = true;
                    if (values[1] > 0) Cancel = true;
                }
            }
            Input = new FrameRunCommand(1, held, 0, 0, false,
                new MovieSourceSpan("<v2-grid>", 1, 1, 1));
        }
        public override bool Equals(object? obj) => obj is InputGridRow row && row.Tick == Tick;
        public override int GetHashCode() => Tick.GetHashCode();
        public decimal FramesPerSecond { get; } = 50;
        public int? RngSeed { get; }
        private readonly HashSet<short> customKeys = new();
        // Hero actions outside TasAction; only Movie v2 rows can carry them.
        public static readonly string[] MenuActions = { "QuickMap", "OpenInventory", "PaneLeft", "PaneRight", "Pause" };
        private readonly HashSet<string> menuActions = new();
        public bool this[string action] => HasAction(action);
        public bool HasAction(string action) => previewAction == action ? previewHeld : CustomKeyInput.TryAction(action, out var key) ? customKeys.Contains(key) : action == "Submit" ? Submit : action == "Cancel" ? Cancel : MenuActions.Contains(action) ? menuActions.Contains(action) : Enum.TryParse<TasAction>(action, out var parsed) && Has(parsed);
        public long Tick { get; }
        public FrameRunCommand Input { get; }
        public bool IsV2 { get; }
        public string Channels { get; } = "—";
        public int SampleCount { get; }
        private bool submit, cancel;
        private string? previewAction;
        private bool previewHeld;
        public bool Submit { get => previewAction == "Submit" ? previewHeld : submit; private set => submit = value; }
        public bool Cancel { get => previewAction == "Cancel" ? previewHeld : cancel; private set => cancel = value; }
        public void PreviewAction(string? action, bool held)
        {
            if (previewAction == action && previewHeld == held) return;
            previewAction = action; previewHeld = held;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
        public string Current { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged;
        public void UpdateCurrent(long tick)
        {
            var value = Tick == tick ? "▶" : "";
            if (value == Current) return;
            Current = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
        }
        public bool Left => Has(TasAction.Left);
        public bool Right => Has(TasAction.Right);
        public bool Up => Has(TasAction.Up);
        public bool Down => Has(TasAction.Down);
        public bool Jump => Has(TasAction.Jump);
        public bool Attack => Has(TasAction.Attack);
        public bool Dash => Has(TasAction.Dash);
        public bool Cast => Has(TasAction.Cast);
        public bool QuickCast => Has(TasAction.QuickCast);
        public bool SuperDash => Has(TasAction.SuperDash);
        public bool DreamNail => Has(TasAction.DreamNail);
        public string Axes => Input.HasAnalogAxes ? $"{Input.AxisX}, {Input.AxisY}" : "—";
        private bool Has(TasAction action) => previewAction == action.ToString() ? previewHeld : (Input.HeldActions & action) != 0;
    }

    public sealed class V2InputGridRow
    {
        public V2InputGridRow(long nativeFrame, IReadOnlyList<GameInputSample> samples, long currentFrame, decimal framesPerSecond = 50, int? rngSeed = null)
        {
            RngSeed = rngSeed;
            FramesPerSecond = framesPerSecond;
            NativeFrame = nativeFrame;
            Samples = samples;
            Current = nativeFrame == currentFrame ? "▶" : "";
        }
        public decimal FramesPerSecond { get; }
        public int? RngSeed { get; }
        public long NativeFrame { get; }
        public IReadOnlyList<GameInputSample> Samples { get; }
        public string Current { get; }
        public bool IsEmpty => Samples.Count == 0;
        public string Channels => Samples.Count == 0
            ? "—"
            : string.Join(", ", Samples.Select(sample => MovieProtocolV2.GetChannelName(sample.Channel)));
    }

    // Only the visible page is expanded. Editing remains run-based and preserves analog data.
    public static class InputGridEditor
    {
        public const int PageSize = 500;
        public static long Count(MovieV2Document movie)
        {
            if (movie == null) throw new ArgumentNullException(nameof(movie));
            long count = 0;
            foreach (var run in movie.Runs) count = checked(count + run.RepeatCount);
            return count;
        }

        public static IReadOnlyList<V2InputGridRow> Page(MovieV2Document movie, long start, long currentFrame)
        {
            var total = Count(movie);
            if (start < 0 || start > total) throw new ArgumentOutOfRangeException(nameof(start));
            var end = Math.Min(total, start + PageSize);
            var rows = new List<V2InputGridRow>((int)(end - start));
            long position = 0;
            foreach (var run in movie.Runs)
            {
                var runEnd = position + run.RepeatCount;
                for (var frame = Math.Max(start, position); frame < Math.Min(end, runEnd); frame++)
                    rows.Add(new V2InputGridRow(frame, run.Samples, currentFrame, run.FramesPerSecond, run.RngSeed));
                position = runEnd;
                if (position >= end) break;
            }
            return rows;
        }

        public static MovieV2EditResult ReplaceFrame(MovieV2Document movie, long nativeFrame,
            IReadOnlyList<GameInputSample> samples)
            => new MovieV2TimelineEditor().ReplaceFrame(movie, nativeFrame, samples);

        public static MovieV2EditResult InsertFrames(MovieV2Document movie, long nativeFrame,
            IReadOnlyList<NativeFrameRun> runs)
            => new MovieV2TimelineEditor().InsertFrames(movie, nativeFrame, runs);

        public static MovieV2EditResult DeleteFrames(MovieV2Document movie, long nativeFrame, long count)
            => new MovieV2TimelineEditor().DeleteFrames(movie, nativeFrame, count);

        public static long Count(MovieDocument movie) => movie.Commands.OfType<FrameRunCommand>().Sum(r => r.FrameCount);

        public static IReadOnlyList<InputGridRow> Page(MovieDocument movie, long start, long currentTick)
        {
            if (start < 0 || start > Count(movie)) throw new ArgumentOutOfRangeException(nameof(start));
            var count = Math.Min(PageSize, Count(movie) - start);
            var slice = MovieInputSlice.Extract(movie, start, count);
            var rows = new List<InputGridRow>((int)count);
            foreach (var run in slice.Commands.OfType<FrameRunCommand>())
                for (long i = 0; i < run.FrameCount; i++) rows.Add(new InputGridRow(start + rows.Count, run, currentTick));
            return rows;
        }

        public static MovieDocument Toggle(MovieDocument movie, long start, long count, TasAction action)
        {
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
            var bits = (int)action;
            if (bits == 0 || (bits & (bits - 1)) != 0 || (action & TasAction.AllGameplay) != action)
                throw new ArgumentOutOfRangeException(nameof(action));
            var runs = MovieInputSlice.Extract(movie, start, count).Commands.OfType<FrameRunCommand>().ToArray();
            // Like painting a binary input column: all set -> clear; mixed/clear -> set.
            var clear = runs.All(r => (r.HeldActions & action) != 0);
            var replacement = runs.Select(r => new FrameRunCommand(r.FrameCount,
                clear ? r.HeldActions & ~action : r.HeldActions | action, r.AxisX, r.AxisY, r.HasAnalogAxes, r.Span));
            return MovieTimelineEditor.Replace(movie, start, count, replacement).Movie;
        }

        public static MovieDocument Paste(MovieDocument movie, long start, long deleteCount, MovieDocument clipboard)
        {
            if (clipboard.Commands.Any(c => c is not FrameRunCommand))
                throw new ArgumentException("粘贴内容只能包含输入帧；不会复制检查点或生命周期事件。", nameof(clipboard));
            var runs = clipboard.Commands.OfType<FrameRunCommand>().ToArray();
            if (runs.Length == 0) throw new ArgumentException("剪贴板没有输入帧。", nameof(clipboard));
            return MovieTimelineEditor.Replace(movie, start, deleteCount, runs).Movie;
        }

        public static MovieDocument SetAxes(MovieDocument movie, long start, long count, bool enabled, int x, int y)
        {
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
            if (x < -10000 || x > 10000 || y < -10000 || y > 10000)
                throw new ArgumentOutOfRangeException(nameof(x), "模拟轴范围为 -10000…10000。");
            var runs = MovieInputSlice.Extract(movie, start, count).Commands.OfType<FrameRunCommand>().ToArray();
            var directions = TasAction.Left | TasAction.Right | TasAction.Up | TasAction.Down;
            if (enabled && runs.Any(r => (r.HeldActions & directions) != 0))
                throw new ArgumentException("模拟轴与方向键不能同时使用；请先清除选区方向键。");
            return MovieTimelineEditor.Replace(movie, start, count, runs.Select(r => new FrameRunCommand(
                r.FrameCount, r.HeldActions, enabled ? x : 0, enabled ? y : 0, enabled, r.Span))).Movie;
        }
    }
}
