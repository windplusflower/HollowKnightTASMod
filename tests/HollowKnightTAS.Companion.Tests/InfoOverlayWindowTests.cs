using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HollowKnightTAS.Companion.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Companion.Tests
{
    [TestClass]
    public sealed class InfoOverlayWindowTests
    {
        [TestMethod]
        public void GeometryFollowsMovingOwnerWithoutAnyNewDataSnapshot()
        {
            WpfTestApplication.Run(() =>
            {
                Window? owner = null; InfoOverlayWindow? overlay = null;
                try
                {
                    void Pump()
                    {
                        var frame = new DispatcherFrame();
                        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                        timer.Start(); Dispatcher.PushFrame(frame);
                    }
                    owner = new Window { Left = 100, Top = 100, Width = 640, Height = 400, ShowActivated = false, ShowInTaskbar = false };
                    owner.Show(); Pump();
                    var ownerHandle = new WindowInteropHelper(owner).Handle;
                    overlay = new InfoOverlayWindow(); overlay.SetOwner(ownerHandle); overlay.ShowOverlay(); Pump();
                    overlay.Update(InfoOverlaySettings.Defaults(), InfoOverlayModel.Decode("{\"schemaVersion\":1,\"values\":{\"frame\":73}}"));
                    NotifyWinEvent(0xA, ownerHandle, 0, 0); Pump();
                    Assert.IsTrue(overlay.IsOwnerMoving);
                    // No new snapshot or controller poll: dragging must still update every position.
                    for (var i = 0; i < 5; i++)
                    {
                        owner.Left += 31; owner.Top += 13; Pump();
                        Assert.IsTrue(GetClientRect(ownerHandle, out var client));
                        var origin = new PointI(); Assert.IsTrue(ClientToScreen(ownerHandle, ref origin));
                        client.Right += origin.X; client.Bottom += origin.Y; client.Left = origin.X; client.Top = origin.Y;
                        Assert.IsTrue(GetWindowRect(new WindowInteropHelper(overlay).Handle, out var bounds));
                        Assert.AreEqual(client.Left, bounds.Left); Assert.AreEqual(client.Top, bounds.Top);
                        Assert.AreEqual(client.Right, bounds.Right); Assert.AreEqual(client.Bottom, bounds.Bottom);
                    }
                    NotifyWinEvent(0xB, ownerHandle, 0, 0); Pump(); Assert.IsFalse(overlay.IsOwnerMoving);
                }
                finally { overlay?.Close(); owner?.Close(); }
            }, TimeSpan.FromSeconds(15));
        }
        [TestMethod]
        public void OverlayRendersClampsAllAnchorsAndOnlyAcceptsMouseWhileAdjusting()
        {
            WpfTestApplication.Run(() =>
            {
                InfoOverlayWindow? window = null;
                try
                {
                    window = new InfoOverlayWindow();
                    var handle = new WindowInteropHelper(window).EnsureHandle();
                    Assert.AreNotEqual(0L, GetWindowLongPtr(handle, -20).ToInt64() & 0x20L);
                    window.SetAdjusting(true);
                    Assert.AreEqual(0L, GetWindowLongPtr(handle, -20).ToInt64() & 0x20L);
                    Assert.AreNotEqual(0L, GetWindowLongPtr(handle, -20).ToInt64() & 0x08000000L);
                    window.SetAdjusting(false);
                    Assert.AreNotEqual(0L, GetWindowLongPtr(handle, -20).ToInt64() & 0x20L);
                    var canvas = (Canvas)window.Content;
                    var settings = InfoOverlaySettings.Defaults();
                    var values = InfoOverlayModel.Decode("{\"schemaVersion\":1,\"values\":{\"frame\":1256,\"room\":\"GG_Workshop\",\"x\":24.382,\"y\":8.15,\"vx\":0,\"vy\":-12.4,\"dash\":0,\"shade\":0.82,\"health\":5,\"maxHealth\":9,\"soul\":66}}");
                    foreach (var anchor in InfoOverlaySettings.Anchors)
                    {
                        canvas.Measure(new Size(800, 450)); canvas.Arrange(new Rect(0, 0, 800, 450));
                        settings.Anchor = anchor; settings.MarginX = 10000; settings.MarginY = 10000;
                        window.Update(settings, values); canvas.UpdateLayout();
                        var panel = (Border)canvas.Children[0];
                        Assert.IsTrue(Canvas.GetLeft(panel) >= 0 && Canvas.GetTop(panel) >= 0);
                        Assert.IsTrue(Canvas.GetLeft(panel) + panel.ActualWidth <= 801);
                        Assert.IsTrue(Canvas.GetTop(panel) + panel.ActualHeight <= 451);
                    }
                    settings.Anchor = "左上"; settings.MarginX = 16; settings.MarginY = 140;
                    window.Update(settings, values); canvas.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(800, 450, 96, 96, PixelFormats.Pbgra32); bitmap.Render(canvas);
                    var root = new DirectoryInfo(AppContext.BaseDirectory);
                    while (root != null && !Directory.Exists(Path.Combine(root.FullName, ".git"))
                        && !File.Exists(Path.Combine(root.FullName, ".git"))) root = root.Parent;
                    var path = Path.Combine(root!.FullName, "artifacts", "info-overlay", "overlay-offline.png");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var stream = File.Create(path)) encoder.Save(stream);
                }
                finally { window?.Close(); }
            }, TimeSpan.FromSeconds(20));
        }
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct PointI { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RectI bounds);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out RectI bounds);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref PointI point);
        [DllImport("user32.dll")] private static extern void NotifyWinEvent(uint id, IntPtr window, int objectId, int childId);
    }
}
