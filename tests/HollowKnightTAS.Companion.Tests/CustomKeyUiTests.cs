using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using HollowKnightTAS.Companion.Automation;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.ViewModels;
using HollowKnightTAS.Core.Movie;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace HollowKnightTAS.Companion.Tests;
[TestClass]
public class CustomKeyUiTests
{
    [TestMethod]
    public void AddPaintUndoRemoveReopenAndRenderOffline()
    {
        WpfTestApplication.Run(() =>
        {
            MainWindow? window = null;
            try
            {
                using var sessions = new SessionRegistry("custom-key-ui");
                using var broker = new AutomationBroker(sessions, automationDirectory: Path.Combine(AppContext.BaseDirectory, "custom-key-ui"));
                var vm = new MainViewModel(sessions, new MovieEditorService(), new CapabilityBroker(), new NativeHostLauncher(AppContext.BaseDirectory), broker);
                var movie = new MovieV2Document("keys", new MovieV2Header("game","api","mod", MovieProtocolV2.NativeProfileId,
                    MovieProtocolV2.ActionSchemaId,false,"none",0,0), new[]{new NativeFrameRun(20, Array.Empty<GameInputSample>(),new MovieSourceSpan("keys",1,1,1))});
                vm.MovieText = new MovieV2Codec().WriteCanonical(movie);
                vm.RefreshGridCommand.Execute(null);
                window = new MainWindow { DataContext = vm };
                var grid = (DataGrid)window.FindName("InputGrid");
                vm.SelectedCustomKey = 282; vm.AddCustomKeyCommand.Execute(null);
                Assert.AreEqual(1,grid.Columns.Count(c => c.SortMemberPath == "Key:282"), vm.GridStatus);
                vm.AddCustomKeyCommand.Execute(null);
                Assert.AreEqual(1,vm.CustomKeys.Count);
                vm.PaintGrid(1,3,"Key:282",true);
                Assert.IsTrue(vm.InputRows[2].HasAction("Key:282"));
                Assert.IsFalse(vm.InputRows[0].HasAction("Key:282"));
                vm.UndoGridCommand.Execute(null);
                Assert.IsFalse(vm.InputRows[2].HasAction("Key:282"));
                vm.RedoGridCommand.Execute(null);
                var saved = vm.MovieText;
                vm.RemoveCustomKeyCommand.Execute(null);
                Assert.AreEqual(0,vm.CustomKeys.Count);
                vm.UndoGridCommand.Execute(null);
                Assert.AreEqual(saved,vm.MovieText);
                vm.MovieText = saved; vm.RefreshGridCommand.Execute(null);
                window.Width = 1200; window.Height = 700;
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(1200,700)); root.Arrange(new Rect(0,0,1200,700)); root.UpdateLayout();
                window.Dispatcher.Invoke(() => {}, DispatcherPriority.ApplicationIdle);
                var column = grid.Columns.Single(c=>c.SortMemberPath == "Key:282");
                grid.ScrollIntoView(vm.InputRows[2],column); grid.UpdateLayout();
                var cell = column.GetCellContent(vm.InputRows[2]) as CheckBox;
                Assert.IsNotNull(cell,"Custom column must realize a checkbox.");
                Assert.IsTrue(cell.IsChecked == true,"Indexer binding must render the held state.");
                var bitmap = new RenderTargetBitmap(1200,700,96,96,PixelFormats.Pbgra32); bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(AppContext.BaseDirectory,"custom-keys-ui.png")); encoder.Save(output);
                Assert.AreEqual(0,sessions.ConnectedCount);
            }
            finally { if (window != null) { window.DataContext = null; window.Close(); } }
        }, TimeSpan.FromSeconds(30), loadStudioTheme: true);
    }
}
