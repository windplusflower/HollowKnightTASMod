using System.Windows;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System;
using System.Linq;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Core.Input;
using HollowKnightTAS.Companion.ViewModels;

namespace HollowKnightTAS.Companion
{
    public partial class MainWindow : Window
    {
        private void OpenSequenceSaves(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            if (vm.SequenceInitialSaves == null || vm.SequenceInitialSaves.Hashes.Count == 0)
            {
                MessageBox.Show(this, UiText.T("当前序列没有关联存档。"), UiText.T("编辑存档"));
                return;
            }
            new SequenceSavesWindow(vm) { Owner = this }.ShowDialog();
        }

        private GlobalStudioHotkeys? globalHotkeys;
        private bool restoringGridSelection;
        private double? restoreScrollOffset;
        private bool closeSaved;
        private readonly DispatcherTimer gridFollowTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };

        public MainWindow()
        {
            InitializeComponent();
            PropertyChangedEventManager.AddHandler(UiText.Current, OnLanguageChanged, string.Empty);
            SourceInitialized += (_, _) =>
            {
                EnableDarkTitleBar();
                globalHotkeys = new GlobalStudioHotkeys(new WindowInteropHelper(this).Handle,
                    () => !OwnedWindows.Cast<Window>().Any(w => w.IsVisible), ExecuteGlobalHotkey,
                    message => { if (DataContext is MainViewModel vm) vm.ReportGlobalHotkeys(message); });
                ConfigureGlobalHotkeys();
            };
            Activated += (_, _) => globalHotkeys?.Refresh();
            Deactivated += (_, _) => Dispatcher.BeginInvoke(new Action(() => globalHotkeys?.Refresh()));
            DataContextChanged += OnStudioDataContextChanged;
            gridFollowTimer.Tick += async (_, _) =>
            {
                if (DataContext is MainViewModel vm)
                {
                    await vm.PollInputGridProgressAsync();
                    await vm.AutoSaveSequenceAsync();
                }
            };
            Loaded += (_, _) => gridFollowTimer.Start();
            Closing += async (_, e) =>
            {
                if (closeSaved || DataContext is not MainViewModel vm) return;
                e.Cancel = true;
                gridFollowTimer.Stop();
                globalHotkeys?.Configure(false, vm.ConfiguredPause, vm.ConfiguredAdvance, vm.ConfiguredPlay);
                try
                {
                    await vm.CancelVideoExportAndWaitAsync();
                    await vm.SaveCurrentBranchAsync(closing: true);
                    closeSaved = true;
                    await Dispatcher.InvokeAsync(Close);
                }
                catch (Exception exception)
                {
                    MessageBox.Show(this, UiText.T("序列保存失败，Studio 保持打开：" + exception.Message), UiText.T("保存失败"));
                    ConfigureGlobalHotkeys();
                    gridFollowTimer.Start();
                }
            };
            Closed += (_, _) =>
            {
                globalHotkeys?.Dispose();
                gridFollowTimer.Stop();
                if (DataContext is MainViewModel vm)
                {
                    vm.DisposeInfoOverlay();
                    vm.InputGridRefreshed -= RestoreGridSelection;
                    vm.InputGridPositionChanged -= ScrollGridToFrame;
                    vm.PropertyChanged -= OnInputBindingsChanged;
                }
            };
        }

        private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (DataContext is MainViewModel vm) RefreshInputBindingHeaders(vm);
        }

        private void EnableDarkTitleBar()
        {
            var enabled = 1;
            var handle = new WindowInteropHelper(this).Handle;
            // Windows 10 1809 used attribute 19; current Windows uses 20.
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute,
            ref int value, int valueSize);

        private void OnStudioDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is MainViewModel oldVm)
            {
                oldVm.InputGridRefreshed -= RestoreGridSelection;
                oldVm.InputGridPositionChanged -= ScrollGridToFrame;
                oldVm.PropertyChanged -= OnInputBindingsChanged;
            }
            if (e.NewValue is MainViewModel vm)
            {
                ConfigureGlobalHotkeys();
                vm.InputGridRefreshed += RestoreGridSelection;
                vm.InputGridPositionChanged += ScrollGridToFrame;
                vm.PropertyChanged += OnInputBindingsChanged;
                RefreshInputBindingHeaders(vm);
                RestoreGridSelection(vm, EventArgs.Empty);
            }
        }

        private void ScrollGridToFrame(long frame)
        {
            if (MainTabs.SelectedItem != InputGridTab || DataContext is not MainViewModel vm)
                return;
            var row = frame >= 0 && frame < vm.InputRows.Count ? vm.InputRows[(int)frame] : null;
            if (row != null) InputGrid.ScrollIntoView(row);
        }

        private void OnMainTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source == MainTabs && MainTabs.SelectedItem == WorldlinesTab
                && DataContext is MainViewModel savesVm)
                savesVm.InitializeWorldlines();
            if (e.Source == MainTabs && MainTabs.SelectedItem == InputGridTab
                && DataContext is MainViewModel { AutoFollowGrid: true } vm)
                vm.ShowCurrentGridFrame();
        }

        private void RestoreGridSelection(object? sender, EventArgs e)
        {
            if (DataContext is MainViewModel customVm) RefreshCustomKeyColumns(customVm);
            if (DataContext is not MainViewModel vm
                || !long.TryParse(vm.GridStart, out var start) || start < 0
                || !long.TryParse(vm.GridCount, out var count) || count < 1) return;
            restoringGridSelection = true;
            try
            {
                InputGrid.SelectedItems.Clear();
                // Keep large range edits in the range fields instead of materializing millions of selected rows.
                for (long i = start; i < Math.Min(vm.InputRows.Count, start + Math.Min(count, 2048)); i++)
                    InputGrid.SelectedItems.Add(vm.InputRows[(int)i]);
            }
            finally { restoringGridSelection = false; }
            if (restoreScrollOffset is double offset && !vm.IsRestorePresentationFrozen)
            {
                restoreScrollOffset = null;
                if (!vm.AutoFollowGrid)
                {
                    InputGrid.UpdateLayout();
                    FindGridScroll(InputGrid)?.ScrollToVerticalOffset(offset);
                }
            }
        }

        private static ScrollViewer? FindGridScroll(DependencyObject parent)
        {
            if (parent is ScrollViewer scroll) return scroll;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                if (FindGridScroll(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
            return null;
        }

        private void OnInputGridSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (restoringGridSelection || DataContext is not MainViewModel vm || InputGrid.SelectedItems.Count == 0) return;
            var rows = InputGrid.SelectedItems.Cast<InputGridRow>().OrderBy(r => r.Tick).ToArray();
            vm.SelectedGridFrames = rows.Select(r => r.Tick).ToArray();
            // Selection gaps are not silently converted into edits of unselected rows.
            vm.GridStart = rows[0].Tick.ToString(System.Globalization.CultureInfo.InvariantCulture);
            vm.GridCount = rows.Length == rows[^1].Tick - rows[0].Tick + 1 ? rows.Length.ToString() : "非连续选区";
        }

        private void OnInputGridClick(object sender, MouseButtonEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.None || DataContext is not MainViewModel vm) return;
            var element = e.OriginalSource as DependencyObject;
            while (element != null && element is not DataGridCell)
                element = VisualTreeHelper.GetParent(element);
            if (element is not DataGridCell cell || cell.DataContext is not InputGridRow row) return;
            // This preview handler consumes button cells before DataGrid can move focus.
            // Keep subsequent transport/editor shortcuts on the grid, not the last text box.
            cell.Focus();
            if (cell.Column.SortMemberPath == "RngSeed" && e.ClickCount == 2 && row.IsV2)
            {
                InputGrid.SelectedItem = row;
                new RngSeedWindow(vm, row.Tick, row.RngSeed) { Owner = this }.ShowDialog();
                e.Handled = true;
                return;
            }
            if (cell.Column.SortMemberPath == "Axes" && e.ClickCount == 2)
            {
                InputGrid.SelectedItem = row;
                vm.GridStart = row.Tick.ToString(System.Globalization.CultureInfo.InvariantCulture);
                vm.GridCount = "1";
                new AxisEditorWindow(vm, row.Input) { Owner = this }.ShowDialog();
                e.Handled = true;
                return;
            }
            var action = cell.Column.SortMemberPath;
            if (action != "Submit" && action != "Cancel" && !HollowKnightTAS.Core.Movie.CustomKeyInput.TryAction(action, out _) && !InputGridRow.MenuActions.Contains(action) && !Enum.TryParse<TasAction>(action, out _)) return;
            InputGrid.SelectedItem = row;
            paintStart = paintEnd = row.Tick;
            paintAction = action;
            paintHeld = !row.HasAction(action);
            vm.InputRows.Preview(paintStart, paintEnd, paintAction, paintHeld);
            InputGrid.CaptureMouse();
            e.Handled = true;
        }

        private long paintStart, paintEnd;
        private string? paintAction;
        private bool paintHeld;
        private static T? Ancestor<T>(DependencyObject? item) where T : DependencyObject
        {
            while (item != null && item is not T) item = VisualTreeHelper.GetParent(item);
            return item as T;
        }
        private void OnInputGridMouseMove(object sender, MouseEventArgs e)
        {
            if (paintAction == null || e.LeftButton != MouseButtonState.Pressed) return;
            var position = e.GetPosition(InputGrid);
            var hit = InputGrid.InputHitTest(position) as DependencyObject;
            var row = Ancestor<DataGridRow>(hit)?.Item as InputGridRow;
            if (row != null) paintEnd = row.Tick;
            if (DataContext is MainViewModel vm)
            {
                vm.GridStart = Math.Min(paintStart, paintEnd).ToString();
                vm.GridCount = (Math.Abs(paintEnd - paintStart) + 1).ToString();
                if (position.Y < 24 && paintEnd > 0) ScrollGridToFrame(--paintEnd);
                else if (position.Y > InputGrid.ActualHeight - 24 && paintEnd + 1 < vm.InputRows.Count) ScrollGridToFrame(++paintEnd);
                vm.GridStart = Math.Min(paintStart, paintEnd).ToString();
                vm.GridCount = (Math.Abs(paintEnd - paintStart) + 1).ToString();
                RestoreGridSelection(vm, EventArgs.Empty);
                vm.InputRows.Preview(paintStart, paintEnd, paintAction, paintHeld);
            }
            e.Handled = true;
        }
        private void OnInputGridMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (paintAction == null) return;
            var action = paintAction; paintAction = null;
            InputGrid.ReleaseMouseCapture();
            if (DataContext is MainViewModel vm) vm.PaintGrid(paintStart, paintEnd, action, paintHeld);
            InputGrid.Focus(); e.Handled = true;
        }
        private void OnInputGridLostCapture(object sender, MouseEventArgs e)
        {
            paintAction = null;
            if (DataContext is MainViewModel vm) vm.InputRows.Preview(0, 0, null, false);
        }

        private void OnInputBindingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MainViewModel.GlobalHotkeysEnabled) or nameof(MainViewModel.ConfiguredPause) or nameof(MainViewModel.ConfiguredAdvance) or nameof(MainViewModel.ConfiguredPlay) or nameof(MainViewModel.ConfiguredInfoKey))
                ConfigureGlobalHotkeys();
            if (e.PropertyName == nameof(MainViewModel.IsRestorePresentationFrozen)
                && sender is MainViewModel { IsRestorePresentationFrozen: true })
                restoreScrollOffset = FindGridScroll(InputGrid)?.VerticalOffset;
            if (e.PropertyName == nameof(MainViewModel.InputBindingLabels) && sender is MainViewModel vm)
                RefreshInputBindingHeaders(vm);
        }

        private void RefreshInputBindingHeaders(MainViewModel vm)
        {
            foreach (var column in InputGrid.Columns.OfType<DataGridCheckBoxColumn>())
            {
                var action = column.SortMemberPath;
                if (HollowKnightTAS.Core.Movie.CustomKeyInput.TryAction(action, out _)) continue;
                var known = vm.InputBindingLabels.TryGetValue(action, out var keys);
                var label = known ? (string.IsNullOrWhiteSpace(keys) ? "—" : keys!) : "?";
                var header = new TextBlock { Text = string.Join("/", label.Split(new[] { " / " }, StringSplitOptions.None).Select(KeyLabel.Compact)),
                    FontFamily = InputGrid.FontFamily, FontSize = InputGrid.FontSize,
                    ToolTip = UiText.T(action) + " · " + (known ? (label == "—" ? UiText.T("未绑定") : keys) : UiText.T("等待游戏按键设置")) };
                header.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                column.Header = header;
                column.MinWidth = 32;
                // SizeToHeader retains WPF's measured width across recycled headers.
                // Recompute a pixel width from the key label so reloads cannot grow it.
                column.Width = new DataGridLength(Math.Max(32, Math.Ceiling(header.DesiredSize.Width) + 20));
            }
        }

        private void RefreshCustomKeyColumns(MainViewModel vm)
        {
            var keys = vm.CustomKeys;
            var existing = InputGrid.Columns.Where(c => c.SortMemberPath.StartsWith("Key:", StringComparison.Ordinal)).ToArray();
            var actions = keys.Select(HollowKnightTAS.Core.Movie.CustomKeyInput.Action).ToArray();
            if (existing.Select(c => c.SortMemberPath).SequenceEqual(actions)) return;
            foreach (var column in existing) InputGrid.Columns.Remove(column);
            foreach (var key in keys)
            {
                var action = HollowKnightTAS.Core.Movie.CustomKeyInput.Action(key);
                InputGrid.Columns.Add(new DataGridCheckBoxColumn
                {
                    Header = KeyLabel.Compact(HollowKnightTAS.Core.Movie.CustomKeyInput.Names[key]), SortMemberPath = action,
                    Binding = new System.Windows.Data.Binding("[" + action + "]") { Mode = System.Windows.Data.BindingMode.OneWay },
                    Width = 75, ElementStyle = (Style)FindResource("TasGridCheckBoxStyle")
                });
            }
        }

        private void OnInputGridWheel(object sender, MouseWheelEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            if (e.Delta >= 0) return;
            var scroll = FindScroll(InputGrid);
            if (scroll != null && scroll.VerticalOffset >= scroll.ScrollableHeight - 1)
            {
                var offset = scroll.VerticalOffset;
                vm.AppendGridBlankFrames();
                Dispatcher.BeginInvoke(new Action(() => scroll.ScrollToVerticalOffset(offset + 3)));
                e.Handled = true;
            }
        }
        private static ScrollViewer? FindScroll(DependencyObject item)
        {
            if (item is ScrollViewer scroll) return scroll;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++)
            {
                var child = FindScroll(VisualTreeHelper.GetChild(item, i));
                if (child != null) return child;
            }
            return null;
        }

        private void OnInputGridRightClick(object sender, MouseButtonEventArgs e)
        {
            var row = Ancestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item as InputGridRow;
            if (row == null || DataContext is not MainViewModel vm) return;
            if (!InputGrid.SelectedItems.Contains(row)) InputGrid.SelectedItem = row;
            var menu = new ContextMenu();
            void FrameItem(string title, string action, bool enabled = true)
            {
                var item = new MenuItem { Header = UiText.T(title), IsEnabled = enabled };
                item.Click += async (_, _) => await vm.FrameMenuAsync(action, row.Tick);
                menu.Items.Add(item);
            }
            FrameItem($"播放到第 {row.Tick} 帧并暂停", "seek", vm.CanNavigateFrame(row.Tick, true));
            FrameItem($"恢复到第 {row.Tick} 帧并暂停", "restore", vm.CanNavigateFrame(row.Tick, false));
            FrameItem($"保存第 {row.Tick} 帧", "save");
            menu.Items.Add(new Separator());
            foreach (var entry in new[] { ("复制选区", vm.CopyGridCommand), ("粘贴到选区", vm.PasteGridCommand),
                ("插入空帧", vm.InsertGridCommand), ("删除选区", vm.DeleteGridCommand) })
                menu.Items.Add(new MenuItem { Header = UiText.T(entry.Item1), Command = entry.Item2 });
            var rate = new MenuItem { Header = UiText.T("修改选区帧率…") };
            rate.Click += (_, _) => EditFrameRate(true);
            menu.Items.Add(rate);
            var rng = new MenuItem { Header = UiText.T("修改当前帧 RNG 种子…"), IsEnabled = row.IsV2 };
            rng.Click += (_, _) => new RngSeedWindow(vm, row.Tick, row.RngSeed) { Owner = this }.ShowDialog();
            menu.Items.Add(rng);
            menu.PlacementTarget = InputGrid; menu.IsOpen = true;
            e.Handled = true;
        }

        private void OnDefaultFrameRate(object sender, RoutedEventArgs e) => EditFrameRate(false);
        private void EditFrameRate(bool selection)
        {
            if (DataContext is not MainViewModel vm) return;
            var dialog = new FrameRateWindow(selection ? "修改选区帧率" : "设置默认帧率",
                selection ? vm.SelectedFrameRate : vm.DefaultFrameRate) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            if (selection) { vm.SelectedFrameRate = dialog.Value; vm.SetFrameRateCommand.Execute(null); }
            else vm.DefaultFrameRate = dialog.Value;
        }

        private void OnEditGridAxes(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            var first = int.TryParse(vm.GridStart, out var index) && index >= 0 && index < vm.InputRows.Count ? vm.InputRows[index] : null;
            if (first?.IsV2 == true) return;
            new AxisEditorWindow(vm, first?.Input) { Owner = this }.ShowDialog();
        }

        private void OnInputGridKeyDown(object sender, KeyEventArgs e)
        {
            if (OwnedWindows.Cast<Window>().Any(w => w.IsVisible)) { e.Handled = true; return; }
            if (DataContext is not MainViewModel vm) return;
            ICommand? command = Keyboard.Modifiers == ModifierKeys.Control ? e.Key switch
            {
                Key.C => vm.CopyGridCommand, Key.V => vm.PasteGridCommand,
                Key.Z => vm.UndoGridCommand, Key.Y => vm.RedoGridCommand, _ => null
            } : Keyboard.Modifiers == ModifierKeys.None ? e.Key switch
            {
                Key.Insert => vm.InsertGridCommand, Key.Delete => vm.DeleteGridCommand, _ => null
            } : null;
            if (command == null) return;
            e.Handled = true;
            if (!e.IsRepeat && command.CanExecute(null)) command.Execute(null);
        }

        private void ConfigureGlobalHotkeys()
        {
            if (DataContext is MainViewModel vm)
                globalHotkeys?.Configure(vm.GlobalHotkeysEnabled, vm.ConfiguredPause, vm.ConfiguredAdvance, vm.ConfiguredPlay, vm.ConfiguredInfoKey);
        }

        private void ExecuteGlobalHotkey(Key key, ModifierKeys modifiers)
        {
            if (DataContext is not MainViewModel vm) return;
            if (key == vm.ConfiguredInfoKey && key != Key.None && modifiers == ModifierKeys.None) { vm.InfoSettings.Enabled = !vm.InfoSettings.Enabled; return; }
            var slot = StudioHotkeys.QuickSlotIndex(key, modifiers);
            if (slot >= 0)
            {
                vm.SelectedQuickSlot = slot;
                var command = modifiers == ModifierKeys.Shift ? vm.SaveQuickSlotCommand : vm.LoadQuickSlotCommand;
                if (command.CanExecute(null)) command.Execute(null);
                return;
            }
            var transport = key == vm.ConfiguredAdvance ? vm.StepCommand : key == vm.ConfiguredPlay ? vm.PlayCommand : vm.TogglePauseCommand;
            if (transport == vm.StepCommand) { vm.ExecuteStepShortcut(); return; }
            if (transport.CanExecute(null)) transport.Execute(null);
        }

        private void OnStudioKeyDown(object sender, KeyEventArgs e)
        {
            if (OwnedWindows.Cast<Window>().Any(w => w.IsVisible)) { e.Handled = true; return; }
            if (DataContext is not MainViewModel viewModel) return;
            var key = e.Key switch
            {
                Key.System => e.SystemKey,
                Key.ImeProcessed => e.ImeProcessedKey,
                Key.DeadCharProcessed => e.DeadCharProcessedKey,
                _ => e.Key
            };
            if (key == viewModel.ConfiguredInfoKey && key != Key.None && Keyboard.Modifiers == ModifierKeys.None)
            { e.Handled = true; if (!e.IsRepeat) viewModel.InfoSettings.Enabled = !viewModel.InfoSettings.Enabled; return; }
            var quickSlot = StudioHotkeys.QuickSlotIndex(key, Keyboard.Modifiers);
            if (quickSlot >= 0)
            {
                e.Handled = true;
                if (e.IsRepeat) return;
                viewModel.SelectedQuickSlot = quickSlot;
                MainTabs.SelectedItem = WorldlinesTab;
                var slotCommand = Keyboard.Modifiers == ModifierKeys.Shift
                    ? viewModel.SaveQuickSlotCommand : viewModel.LoadQuickSlotCommand;
                if (slotCommand.CanExecute(null)) slotCommand.Execute(null);
                return;
            }
            var editingText = Keyboard.FocusedElement is TextBoxBase or PasswordBox
                || Keyboard.FocusedElement is ComboBox;
            ICommand? command = StudioHotkeys.Resolve(key, Keyboard.Modifiers, editingText,
                viewModel.ConfiguredPause, viewModel.ConfiguredAdvance, viewModel.ConfiguredPlay) switch
            {
                StudioShortcut.OpenMovie => viewModel.OpenMovieCommand,
                StudioShortcut.SaveMovie => viewModel.SaveMovieCommand,
                StudioShortcut.Play => viewModel.PlayCommand,
                StudioShortcut.PlayPause => viewModel.TogglePauseCommand,
                StudioShortcut.FrameAdvance => viewModel.StepCommand,
                _ => null
            };
            if (command == null) return;
            e.Handled = true;
            if (command == viewModel.StepCommand) { viewModel.ExecuteStepShortcut(); return; }
            if ((!e.IsRepeat || command == viewModel.StepCommand) && command.CanExecute(null)) command.Execute(null);
        }
    }
}
