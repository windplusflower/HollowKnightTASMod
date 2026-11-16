using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HollowKnightTAS.Companion.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Companion.Tests;

[TestClass]
public sealed class RestorePresentationFailureTests
{
    [TestMethod]
    public void CompleteReleasesCoverForAnUnshownWindowWithoutRequestingCapture()
    {
        Exception? failure = null;
        bool active = true;
        IntPtr expectedStyle = IntPtr.Zero, observedStyle = IntPtr.Zero, ready = IntPtr.Zero;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                var target = new Window { ShowActivated = false };
                var cover = new Window { ShowActivated = false };
                using var presentation = new RestorePresentation();
                try
                {
                    // A valid HWND with no displayed surface models the startup gate's
                    // window. Completion must not require WGC to produce a frame.
                    var handle = new WindowInteropHelper(target).EnsureHandle();
                    var style = GetWindowLongPtr(handle, -20);
                    void Set(string name, object value) => typeof(RestorePresentation)
                        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(presentation, value);
                    Set("cover", cover);
                    Set("target", handle);
                    Set("targetExtendedStyle", style);
                    SetWindowLongPtr(handle, -20, new IntPtr(style.ToInt64() | 0x08000000L));
                    await presentation.CompleteAsync();
                    active = presentation.IsActive;
                    expectedStyle = style;
                    observedStyle = GetWindowLongPtr(handle, -20);
                    ready = GetProp(handle, "HKTAS.RestorePresentationReady");
                }
                catch (Exception ex) { failure = ex; }
                finally { cover.Close(); target.Close(); dispatcher.InvokeShutdown(); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "Restore completion waited for a capture frame.");
        if (failure != null) throw new AssertFailedException("Window completion failed", failure);
        Assert.IsFalse(active);
        Assert.AreEqual(expectedStyle, observedStyle);
        Assert.AreEqual(new IntPtr(1), ready);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr window, string name);

    [TestMethod]
    public async Task CaptureInvalidArgumentAndTimeoutFallBackWithoutFailingRestore()
    {
        Assert.IsNull(await RestorePresentation.TryCaptureAsync(() => throw new ArgumentException("E_INVALIDARG")));
        Assert.IsNull(await RestorePresentation.TryCaptureAsync(() => Task.FromException<BitmapSource>(new TimeoutException())));
        Assert.IsNull(await RestorePresentation.TryCaptureAsync(() => throw new COMException("capture", unchecked((int)0x80070057))));
    }

    [TestMethod]
    public async Task CompositorFailureStillReleasesCoverAndAllowsCallerToContinue()
    {
        var releases = 0;
        await RestorePresentation.FinishVisualAsync(() => Task.FromException(new COMException("DwmFlush")), () => releases++);
        Assert.AreEqual(1, releases);
        await RestorePresentation.FinishVisualAsync(() => Task.CompletedTask, () => releases++);
        Assert.AreEqual(2, releases);
    }

    [TestMethod]
    public void AttachTargetCanSafelyReattachCurrentProcess()
    {
        using var presentation = new RestorePresentation();
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        presentation.AttachTarget(self);
        presentation.AttachTarget(self);
        Assert.IsFalse(presentation.IsActive);
    }

    [TestMethod]
    public void StudioExceptionIsFlushedAndIncludedInExportWhileLoggerIsOpen()
    {
        var root = Path.Combine(Path.GetTempPath(), "hktas-studio-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var log = new StudioApplicationLog(root);
            log.WriteLine(new ArgumentException("capture failure").ToString());
            var result = DiagnosticLogExporter.Export(root, root, Path.Combine(root, "missing-game"));
            using var zip = ZipFile.OpenRead(result.Path);
            var entry = zip.Entries.Single(e => e.FullName.StartsWith("studio/application/", StringComparison.Ordinal));
            using var reader = new StreamReader(entry.Open());
            StringAssert.Contains(reader.ReadToEnd(), "System.ArgumentException: capture failure");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
