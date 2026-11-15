using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using HollowKnightTAS.Companion.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Companion.Tests;

[TestClass]
public sealed class OverlayStackingTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RefreshAndReshowKeepGameBelowOtherWindows(bool information)
    {
        WpfTestApplication.Run(() =>
        {
            Window? game = null, studio = null, overlay = null;
            try
            {
                game = new Window { Width = 320, Height = 240, ShowActivated = false, ShowInTaskbar = false };
                studio = new Window { Width = 320, Height = 240, ShowActivated = false, ShowInTaskbar = false };
                game.Show(); studio.Show();
                var gameHandle = new WindowInteropHelper(game).Handle;
                var studioHandle = new WindowInteropHelper(studio).Handle;
                overlay = information ? new InfoOverlayWindow() : new ColliderOverlayWindow();
                void Show()
                {
                    if (overlay is InfoOverlayWindow info) { info.SetOwner(gameHandle); info.SetBounds(100, 100, 300, 200, 96); info.ShowOverlay(); }
                    else { var collider = (ColliderOverlayWindow)overlay; collider.SetOwner(gameHandle); collider.SetBounds(100, 100, 300, 200, 96); collider.ShowOverlay(); }
                }
                Assert.IsTrue(SetWindowPos(studioHandle, IntPtr.Zero, 0, 0, 0, 0, 0x13));
                Show(); Pump();
                var overlayHandle = new WindowInteropHelper(overlay).Handle;
                AssertAbove(studioHandle, gameHandle);
                AssertAbove(studioHandle, overlayHandle);
                // Simulate switching to Studio without stealing the user's keyboard focus.
                Assert.IsTrue(SetWindowPos(studioHandle, IntPtr.Zero, 0, 0, 0, 0, 0x13));
                Pump();
                var foreground = GetForegroundWindow();
                AssertAbove(studioHandle, gameHandle);
                for (var i = 0; i < 5; i++)
                {
                    game.Left += 10; game.Top += 5; game.Width += 2;
                    Show(); Pump();
                    AssertAbove(studioHandle, gameHandle);
                    AssertAbove(studioHandle, overlayHandle);
                    AssertAbove(overlayHandle, gameHandle);
                    Assert.AreEqual(foreground, GetForegroundWindow(), "Overlay stole keyboard focus");
                }
                overlay.Hide(); Pump();
                AssertAbove(studioHandle, gameHandle);
                Show(); Pump();
                AssertAbove(studioHandle, gameHandle);
                AssertAbove(studioHandle, overlayHandle);
                AssertAbove(overlayHandle, gameHandle);
                Assert.AreEqual(foreground, GetForegroundWindow());
            }
            finally { overlay?.Close(); studio?.Close(); game?.Close(); }
        }, TimeSpan.FromSeconds(20));
    }

    private static void AssertAbove(IntPtr above, IntPtr below)
    {
        for (var window = GetWindow(above, 2); window != IntPtr.Zero; window = GetWindow(window, 2))
            if (window == below) return;
        Assert.Fail($"Window {above} is no longer above {below}");
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
