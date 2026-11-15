using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using HollowKnightTAS.Companion.Automation;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Companion.Tests;

[TestClass]
public sealed class DiagnosticSettingsUiTests
{
    [TestMethod]
    public void ExportIsBoundAndVisibleAtBottomOfSettings()
    {
        WpfTestApplication.Run(() =>
        {
            MainWindow? window = null;
            try
            {
                using var sessions = new SessionRegistry("diagnostic-ui-test");
                using var broker = new AutomationBroker(sessions, automationDirectory: Path.Combine(AppContext.BaseDirectory, "diagnostic-ui-test"));
                var vm = new MainViewModel(sessions, new MovieEditorService(), new CapabilityBroker(), new NativeHostLauncher(AppContext.BaseDirectory), broker);
                window = new MainWindow { DataContext = vm, Width = 900, Height = 600 };
                window.Show();
                var tab = (TabItem)window.FindName("ShortcutSettingsTab");
                tab.IsSelected = true;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var scroll = (ScrollViewer)tab.Content;
                var panel = (StackPanel)scroll.Content;
                var export = panel.Children.OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "HktasStudio.ExportDiagnosticLogs");
                Assert.AreSame(vm.ExportDiagnosticLogsCommand, export.Command);
                Assert.AreEqual(UiText.Current["exportDiagnostics"], export.Content);
                Assert.IsTrue(export.IsEnabled);
                scroll.ScrollToBottom();
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var bounds = export.TransformToAncestor(scroll).TransformBounds(new Rect(export.RenderSize));
                Assert.IsTrue(bounds.Top >= 0 && bounds.Bottom <= scroll.ActualHeight);
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(AppContext.BaseDirectory, "diagnostic-settings.png"));
                encoder.Save(output);
            }
            finally { if (window != null) { window.DataContext = null; window.Close(); } }
        }, TimeSpan.FromSeconds(30), loadStudioTheme: true);
    }
}
