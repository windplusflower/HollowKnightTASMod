using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using HollowKnightTAS.Core.Ipc;

namespace HollowKnightTAS.Companion.Services
{
    public sealed class ColliderOverlayController : IDisposable
    {
        public const int PageSize = 128;
        private readonly Func<RuntimeSessionClient?> sessionProvider;
        private readonly Func<RuntimeSessionClient, IReadOnlyDictionary<string, string>,
            CancellationToken, Task<IReadOnlyDictionary<string, string>>> request;
        private readonly Action<string>? status;
        private readonly DispatcherTimer pollTimer;
        private readonly CancellationTokenSource cancellation = new();
        private ColliderOverlayWindow? window;
        private ColliderOverlaySnapshot? snapshot;
        private bool enabled;
        private int requestInFlight;
        private long generation;
        private int disposed;
        private bool studioWasVisible;

        public ColliderOverlayController(
            Func<RuntimeSessionClient?> sessionProvider,
            Func<RuntimeSessionClient, IReadOnlyDictionary<string, string>,
                CancellationToken, Task<IReadOnlyDictionary<string, string>>> request,
            Action<string>? status = null,
            Dispatcher? dispatcher = null)
        {
            this.sessionProvider = sessionProvider;
            this.request = request;
            this.status = status;
            pollTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150),
                DispatcherPriority.Background, OnPoll, dispatcher ?? Dispatcher.CurrentDispatcher);
            // The event-handler constructor starts the timer immediately; the setting is opt-in.
            pollTimer.Stop();
            var application = System.Windows.Application.Current;
            if (application != null && application.Dispatcher.CheckAccess())
                application.Exit += OnApplicationExit;
        }

        public void SetEnabled(bool value)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            if (enabled == value) return;
            enabled = value;
            Interlocked.Increment(ref generation);
            if (!value)
            {
                pollTimer.Stop();
                snapshot = null;
                window?.SetSnapshot(null);
                window?.Hide();
                status?.Invoke(UiText.T("碰撞箱显示已关闭。"));
                return;
            }
            pollTimer.Start();
            status?.Invoke(UiText.T("正在等待游戏画面…"));
            _ = PollAsync();
        }

        private void OnPoll(object? sender, EventArgs args) => _ = PollAsync();

        private async Task PollAsync()
        {
            if (!enabled || Volatile.Read(ref disposed) != 0
                || Interlocked.Exchange(ref requestInFlight, 1) != 0) return;
            var studio = System.Windows.Application.Current?.MainWindow;
            if (studio?.IsVisible == true) studioWasVisible = true;
            if (studioWasVisible && (studio == null || !studio.IsVisible))
            {
                Dispose();
                Interlocked.Exchange(ref requestInFlight, 0);
                return;
            }
            var localGeneration = Volatile.Read(ref generation);
            var session = sessionProvider();
            if (session == null || !session.IsConnected)
            {
                Hide(UiText.T("等待 Runtime 连接…"));
                Interlocked.Exchange(ref requestInFlight, 0);
                return;
            }

            try
            {
                var windowHandle = FindMainWindow(session.GameProcessId);
                if (!TryGetClientBounds(windowHandle, out var bounds, out var dpi))
                {
                    Hide(UiText.T("游戏窗口不可见或已最小化。"));
                    return;
                }
                EnsureWindow();
                window!.SetOwner(windowHandle);
                window!.SetBounds(bounds.Left, bounds.Top, bounds.Right - bounds.Left,
                    bounds.Bottom - bounds.Top, dpi);

                var combined = await FetchSnapshotAsync(session, localGeneration);
                if (combined == null || !CanApply(session, localGeneration)) return;
                if (combined.Objects.Count == 0)
                {
                    Hide(UiText.T("当前场景没有可显示的碰撞箱。"));
                    return;
                }
                snapshot = combined;
                window.SetSnapshot(snapshot);
                window.ShowOverlay();
                status?.Invoke(UiText.T(string.Format(CultureInfo.InvariantCulture,
                    "碰撞箱 {0} 个对象。", snapshot.Objects.Count)));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                if (CanApply(session, localGeneration))
                    Hide(UiText.T(string.Format(CultureInfo.InvariantCulture,
                        "碰撞箱读取失败：{0}", exception.Message)));
            }
            finally
            {
                Interlocked.Exchange(ref requestInFlight, 0);
            }
        }

        private async Task<ColliderOverlaySnapshot?> FetchSnapshotAsync(
            RuntimeSessionClient session, long localGeneration)
        {
            var offset = 0;
            string? snapshotId = null;
            ColliderOverlaySnapshot? combined = null;
            do
            {
                if (!CanApply(session, localGeneration)) return null;
                var fields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["requestId"] = "studio-collider-" + Guid.NewGuid().ToString("N"),
                    ["view"] = "colliders",
                    ["includeInactive"] = "false",
                    ["offset"] = offset.ToString(CultureInfo.InvariantCulture),
                    ["limit"] = PageSize.ToString(CultureInfo.InvariantCulture)
                };
                if (!string.IsNullOrEmpty(snapshotId)) fields["snapshotId"] = snapshotId!;
                var response = await request(session, fields, cancellation.Token);
                if (!response.TryGetValue("snapshotId", out var responseId)
                    || string.IsNullOrWhiteSpace(responseId)
                    || !response.TryGetValue("snapshotJson", out var json))
                    throw new FormatException("worldSnapshot response is incomplete.");
                snapshotId ??= responseId;
                if (!string.Equals(snapshotId, responseId, StringComparison.Ordinal))
                    throw new FormatException("worldSnapshot pagination changed snapshotId.");
                var page = ColliderOverlayDecoder.Decode(snapshotId, json);
                if (page.NextOffset == null
                    && response.TryGetValue("nextOffset", out var nextText)
                    && int.TryParse(nextText, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var responseNext)
                    && responseNext >= 0)
                    page = new ColliderOverlaySnapshot(snapshotId, responseNext, page.Objects);
                combined = combined == null ? page : combined.Append(page);
                if (page.NextOffset is not int next) break;
                if (next <= offset)
                    throw new FormatException("worldSnapshot nextOffset did not advance.");
                offset = next;
            } while (true);
            return combined;
        }

        private bool CanApply(RuntimeSessionClient session, long localGeneration)
            => enabled && Volatile.Read(ref disposed) == 0
                && localGeneration == Volatile.Read(ref generation)
                && ReferenceEquals(sessionProvider(), session)
                && session.IsConnected;

        private void EnsureWindow()
        {
            if (window != null) return;
            window = new ColliderOverlayWindow();
            window.SetSnapshot(snapshot);
        }

        private void Hide(string detail)
        {
            snapshot = null;
            window?.SetSnapshot(null);
            window?.Hide();
            status?.Invoke(detail);
        }

        internal static IntPtr FindMainWindow(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                process.Refresh();
                return process.MainWindowHandle;
            }
            catch (ArgumentException) { return IntPtr.Zero; }
            catch (InvalidOperationException) { return IntPtr.Zero; }
        }

        internal static bool TryGetClientBounds(IntPtr window, out NativeRect bounds, out uint dpi)
        {
            bounds = default;
            dpi = 96;
            if (window == IntPtr.Zero || !IsWindow(window) || !IsWindowVisible(window) || IsIconic(window)
                || !GetClientRect(window, out var client) || client.Right <= 0 || client.Bottom <= 0)
                return false;
            var topLeft = new PointI { X = client.Left, Y = client.Top };
            var bottomRight = new PointI { X = client.Right, Y = client.Bottom };
            if (!ClientToScreen(window, ref topLeft) || !ClientToScreen(window, ref bottomRight)
                || bottomRight.X <= topLeft.X || bottomRight.Y <= topLeft.Y)
                return false;
            bounds = new NativeRect(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
            dpi = GetDpiForWindow(window);
            if (dpi == 0) dpi = 96;
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            cancellation.Cancel();
            pollTimer.Stop();
            pollTimer.Tick -= OnPoll;
            var application = System.Windows.Application.Current;
            if (application != null && application.Dispatcher.CheckAccess())
                application.Exit -= OnApplicationExit;
            window?.SetSnapshot(null);
            window?.Close();
            window = null;
            cancellation.Dispose();
        }

        private void OnApplicationExit(object? sender, System.Windows.ExitEventArgs args) => Dispose();

        internal readonly record struct NativeRect(int Left, int Top, int Right, int Bottom);

        [StructLayout(LayoutKind.Sequential)] private struct PointI { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct ClientRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out ClientRect rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref PointI point);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    }
}
