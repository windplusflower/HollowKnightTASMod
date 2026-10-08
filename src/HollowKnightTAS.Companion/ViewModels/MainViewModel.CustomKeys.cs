using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.Companion.ViewModels;

public sealed partial class MainViewModel
{
    public IReadOnlyDictionary<short, string> AvailableCustomKeys { get; } =
        CustomKeyInput.Names.ToDictionary(pair => pair.Key, pair => KeyLabel.Compact(pair.Value));
    public short SelectedCustomKey { get; set; } = 282;
    public IReadOnlyList<short> CustomKeys => movieEditor.ValidateAny(MovieText).V2Document is { } movie
        ? CustomKeyInput.Keys(movie).ToArray() : Array.Empty<short>();
    public ICommand AddCustomKeyCommand => new RelayCommand(() =>
    {
        try
        {
            if (!IsInputGridInteractive) return;
            var movie = GridAny().V2Document ?? throw new InvalidOperationException("自定义按键需要 Movie v2 序列。");
            if (CustomKeyInput.Keys(movie).Contains(SelectedCustomKey)) return;
            // A new key is released on every existing frame, so executed input is unchanged:
            // no replay is needed and the earliest edited frame stays where it was.
            var earliest = earliestGridEdit;
            var keys = movie.Header.CustomKeys.Concat(new[] { SelectedCustomKey }).OrderBy(k => k);
            EditGridDocument(new MovieV2Document(movie.SourceName, movie.Header.WithCustomKeys(keys), movie.Runs));
            earliestGridEdit = earliest;
            GridStatus = "已添加自定义键；未按下前不影响已有帧。";
        }
        catch (Exception ex) { GridStatus = ex.Message; }
    });
    public ICommand RemoveCustomKeyCommand => new RelayCommand(() =>
    {
        try
        {
            if (!IsInputGridInteractive) return;
            var movie = GridAny().V2Document ?? throw new InvalidOperationException("自定义按键需要 Movie v2 序列。");
            if (!CustomKeyInput.Keys(movie).Contains(SelectedCustomKey)) return;
            GridStart = "0"; GridCount = "1";
            var runs = movie.Runs.Select(r => new NativeFrameRun(r.RepeatCount,
                r.Samples.Where(s => s.Channel != GameInputChannel.CustomKey || s.Values[0] != SelectedCustomKey).ToArray(),
                r.Span, r.FramesPerSecond, true, r.RngSeed));
            EditGridDocument(new MovieV2Document(movie.SourceName, movie.Header.WithCustomKeys(movie.Header.CustomKeys.Where(k => k != SelectedCustomKey)), runs));
            draftRequiresRestart = true;
        }
        catch (Exception ex) { GridStatus = ex.Message; }
    });
}
