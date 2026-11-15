using System;
using HollowKnightTAS.Companion.Automation;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.ViewModels;
using HollowKnightTAS.Core.Movie;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Companion.Tests
{
    [TestClass]
    public sealed class FractionalFrameRateEditingTests
    {
        [TestMethod]
        public void DialogAcceptsDecimalAndRejectsExcessPrecision()
        {
            WpfTestApplication.Run(() =>
            {
                FrameRateWindow? window = null;
                try
                {
                    window = new FrameRateWindow("设置默认帧率", "50")
                    { ShowInTaskbar = false, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
                    var panel = (System.Windows.Controls.StackPanel)window.Content;
                    var input = (System.Windows.Controls.TextBox)panel.Children[1];
                    var error = (System.Windows.Controls.TextBlock)panel.Children[2];
                    var buttons = (System.Windows.Controls.StackPanel)panel.Children[3];
                    var ok = (System.Windows.Controls.Button)buttons.Children[0];
                    Exception? dialogFailure = null;
                    window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            input.Text = "99.9990001";
                            ok.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                            Assert.IsFalse(string.IsNullOrEmpty(error.Text));
                            Assert.IsNull(window.DialogResult);
                            input.Text = "99.999";
                            ok.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        }
                        catch (Exception ex) { dialogFailure = ex; window.Close(); }
                    }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Assert.IsTrue(window.ShowDialog());
                    if (dialogFailure != null) throw new AssertFailedException(dialogFailure.ToString(), dialogFailure);
                    Assert.AreEqual("99.999", window.Value);
                }
                finally { window?.Close(); }
            }, TimeSpan.FromSeconds(15), loadStudioTheme: true);
        }

        [TestMethod]
        public void FractionalSelectionDisplaysAndSurvivesUndoRedoAndCopy()
        {
            using var sessions = new SessionRegistry("fractional-fps-test");
            using var broker = new AutomationBroker(sessions);
            var vm = new MainViewModel(sessions, new MovieEditorService(), new CapabilityBroker(),
                new NativeHostLauncher(AppContext.BaseDirectory), broker);
            var movie = new MovieV2Document("fps", new MovieV2Header("game", "api", "mod",
                MovieProtocolV2.NativeProfileId, MovieProtocolV2.ActionSchemaId, false, new string('a', 64), 800, 450),
                new[] { new NativeFrameRun(20, Array.Empty<GameInputSample>(), new MovieSourceSpan("fps", 1, 1, 1)) });
            vm.MovieText = new MovieV2Codec().WriteCanonical(movie);
            vm.RefreshGridCommand.Execute(null);
            var original = vm.MovieText;
            vm.GridStart = "4"; vm.GridCount = "2";
            vm.SelectedFrameRate = "99.999"; vm.SetFrameRateCommand.Execute(null);
            Assert.AreEqual(99.999m, vm.InputRows[4].FramesPerSecond);
            Assert.AreEqual(99.999m, vm.InputRows[5].FramesPerSecond);
            Assert.AreEqual(50m, vm.InputRows[6].FramesPerSecond);
            var edited = vm.MovieText;
            vm.UndoGridCommand.Execute(null); Assert.AreEqual(original, vm.MovieText);
            vm.RedoGridCommand.Execute(null); Assert.AreEqual(edited, vm.MovieText);
            vm.SelectedFrameRate = "99.9990001"; vm.SetFrameRateCommand.Execute(null);
            Assert.AreEqual(edited, vm.MovieText);
            var slice = (MovieV2Document)typeof(MainViewModel).GetMethod("SliceV2",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, new object[] { TimelineTree.Parse(vm.MovieText), 4L, 2L })!;
            Assert.AreEqual(99.999m, slice.Runs[0].FramesPerSecond);
            var pasted = new MovieV2TimelineEditor().InsertFrames(movie, 8, slice.Runs);
            Assert.AreEqual(99.999m, new VirtualInputRows(pasted.Movie, 0)[8].FramesPerSecond);
        }
    }
}
