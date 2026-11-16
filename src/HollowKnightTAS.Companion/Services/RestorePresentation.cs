using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HollowKnightTAS.Companion.Services
{
    /// <summary>Owns a still image independently of both game processes. UI-thread only.</summary>
    public sealed class RestorePresentation : IDisposable
    {
        private Window? cover;
        private IntPtr target;
        private RectI bounds;
        private RectI imageBounds;
        private IntPtr targetExtendedStyle;
        private readonly DispatcherTimer audioTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
        private ProcessAudioMute? audio;
        public bool IsActive => cover != null;
        public const string HiddenLaunchVariable = "HKTAS_RESTORE_HIDDEN_WINDOW";

        public async Task BeginAsync(Process source)
        {
            if (IsActive) throw new InvalidOperationException("回档显示已被占用。");
            source.Refresh();
            var window = source.MainWindowHandle;
            if (window == IntPtr.Zero || IsIconic(window) || !GetWindowRect(window, out bounds))
                throw new InvalidOperationException("请先还原游戏窗口，再执行回档。");
            imageBounds = bounds;
            if (DwmGetWindowAttribute(window, 9, out var extended, Marshal.SizeOf<RectI>()) == 0)
                imageBounds = extended;
            var previousWindow = GetWindow(window, 3); // GW_HWNDPREV: preserve the source's stacking order.
            // A still image is cosmetic. Unsupported/unready WGC falls back to a black cover.
            var bitmap = await TryCaptureAsync(() => GameWindowCapture.CaptureAsync(window));
            cover = new Window
            {
                Title = "Hollow Knight · 恢复中", WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
                Background = Brushes.Black,
                Content = new Image { Source = bitmap, Stretch = Stretch.Fill },
                Width = imageBounds.Right - imageBounds.Left, Height = imageBounds.Bottom - imageBounds.Top
            };
            var handle = new WindowInteropHelper(cover).EnsureHandle();
            SetWindowLongPtr(handle, -20, new IntPtr(GetWindowLongPtr(handle, -20).ToInt64() | 0x08000000L));
            cover.Show();
            SetWindowPos(handle, previousWindow, imageBounds.Left, imageBounds.Top,
                imageBounds.Right - imageBounds.Left, imageBounds.Bottom - imageBounds.Top, 0x0010);
            await FinishVisualAsync(PaintAsync, () => { });
            Trace.WriteLine("RestorePresentation: cover painted; source image=" + (bitmap != null));
        }

        public void AttachTarget(Process process)
        {
            if (!IsActive) return;
            target = FindWindow(process.Id);
            if (target == IntPtr.Zero) throw new InvalidOperationException("新游戏没有可接管的显示窗口。");
            // Keep the game drawable under the still image; never minimize it or skip render work.
            targetExtendedStyle = GetWindowLongPtr(target, -20);
            SetWindowLongPtr(target, -20, new IntPtr(targetExtendedStyle.ToInt64() | 0x08000000L));
            audioTimer.Stop();
            audioTimer.Tick -= MuteAudio;
            audio?.Dispose();
            audio = new ProcessAudioMute(process.Id);
            audio.MuteNewSessions();
            audioTimer.Tick += MuteAudio;
            audioTimer.Start();
            ShowWindow(target, 4);
            SetWindowPos(target, new WindowInteropHelper(cover!).Handle, bounds.Left, bounds.Top,
                bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0x0010);
            Trace.WriteLine("RestorePresentation: target attached " + process.Id);
        }

        public async Task CompleteAsync()
        {
            if (!IsActive) return;
            if (target == IntPtr.Zero || !IsWindow(target))
                throw new InvalidOperationException("恢复后的游戏窗口已退出。");
            // Startup frame zero precedes Unity initialization. The native gate may keep
            // this window uncapturable indefinitely: neither capture nor retries belong here.
            // Reveal the live window underneath; presentation must not fail a valid restore.
            await FinishVisualAsync(PaintAsync, Dispose);
            Trace.WriteLine("RestorePresentation: target revealed");
        }

        internal static async Task<BitmapSource?> TryCaptureAsync(Func<Task<BitmapSource>> capture)
        {
            try { return await capture(); }
            catch (Exception exception)
            {
                Trace.WriteLine("RestorePresentation: source capture unavailable: " + exception);
                return null;
            }
        }

        internal static async Task FinishVisualAsync(Func<Task> paint, Action release)
        {
            try { await paint(); }
            catch (Exception exception)
            { Trace.WriteLine("RestorePresentation: compositor update unavailable: " + exception); }
            finally { release(); }
        }

        private void MuteAudio(object? sender, EventArgs e) => audio?.MuteNewSessions();
        private static async Task PaintAsync()
        {
            await Dispatcher.Yield(DispatcherPriority.Render);
            var result = DwmFlush();
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            await Task.Delay(50);
            result = DwmFlush();
            if (result < 0) Marshal.ThrowExceptionForHR(result);
        }

        public void Dispose()
        {
            audioTimer.Stop();
            audioTimer.Tick -= MuteAudio;
            audio?.Dispose();
            audio = null;
            if (target != IntPtr.Zero && IsWindow(target))
            {
                SetProp(target, "HKTAS.RestorePresentationReady", new IntPtr(1));
                SetWindowLongPtr(target, -20, targetExtendedStyle);
            }
            cover?.Close();
            cover = null;
            target = IntPtr.Zero;
        }

        // A faulted native gate may no longer service synchronous window messages.
        // Do not restore its style from the Studio UI thread; that process will
        // be replaced on the next explicit replay. Still release our cover/audio.
        public void AbandonFaultedTarget()
        {
            target = IntPtr.Zero;
            Dispose();
        }

        private static IntPtr FindWindow(int pid)
        {
            var result = IntPtr.Zero;
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var owner);
                var name = new System.Text.StringBuilder(128);
                GetClassName(window, name, name.Capacity);
                if (owner != pid || name.ToString() != "UnityWndClass") return true;
                result = window;
                return false;
            }, IntPtr.Zero);
            return result;
        }

        [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
        private delegate bool EnumWindowProc(IntPtr window, IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RectI rect);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetProp(IntPtr window, string name, IntPtr value);
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out RectI value, int size);
    }
}
