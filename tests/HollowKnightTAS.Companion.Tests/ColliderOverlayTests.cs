using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HollowKnightTAS.Companion.Automation;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HollowKnightTAS.Companion.Tests
{
    [TestClass]
    public sealed class ColliderOverlayTests
    {
        [TestMethod]
        public void DecoderReadsNestedScreenPathsAndClassification()
        {
            var json = "{\"nextOffset\":128,\"objects\":[{\"id\":\"hero\",\"kind\":\"player\",\"colliders\":[{\"classification\":\"knight\",\"screenPaths\":[{\"closed\":true,\"points\":[{\"x\":0.1,\"y\":0.2,\"z\":0},{\"x\":0.3,\"y\":0.2,\"z\":0}]}]}]}]}";
            var snapshot = ColliderOverlayDecoder.Decode("s1", json);

            Assert.AreEqual("s1", snapshot.SnapshotId);
            Assert.AreEqual(128, snapshot.NextOffset);
            Assert.AreEqual(1, snapshot.Objects.Count);
            Assert.AreEqual("knight", snapshot.Objects[0].Classification);
            Assert.IsTrue(snapshot.Objects[0].Paths[0].Closed);
            Assert.AreEqual(0.3, snapshot.Objects[0].Paths[0].Points[1].X, 0.00001);
        }

        [TestMethod]
        public void DecoderDoesNotUseWorldPathsFallback()
        {
            var json = "{\"objects\":[{\"id\":\"enemy\",\"classification\":\"enemy\",\"colliders\":[{\"worldPaths\":[{\"closed\":true,\"points\":[{\"x\":0,\"y\":0,\"z\":0},{\"x\":1,\"y\":1,\"z\":0}]}]}]}]}";
            var snapshot = ColliderOverlayDecoder.Decode("s2", json);

            Assert.AreEqual(0, snapshot.Objects.Count);
        }

        [TestMethod]
        public void DecoderPreservesFiniteScreenCoordinatesForSurfaceClipping()
        {
            var json = "{\"objects\":[{\"id\":\"enemy\",\"classification\":\"enemy\",\"colliders\":[{\"screenPaths\":[{\"points\":[{\"x\":-1,\"y\":2},{\"x\":1.5,\"y\":0}]} ,{\"points\":[{\"x\":\"bad\",\"y\":0},{\"x\":0,\"y\":0}]}]}]}]}";
            var snapshot = ColliderOverlayDecoder.Decode("s3", json);

            Assert.AreEqual(1, snapshot.Objects.Count);
            var points = snapshot.Objects.Single().Paths.Single().Points;
            Assert.AreEqual(-1, points[0].X, 0.00001);
            Assert.AreEqual(2, points[0].Y, 0.00001);
            Assert.AreEqual(1.5, points[1].X, 0.00001);
        }

        [TestMethod]
        public void AppendRequiresStableSnapshotId()
        {
            var first = ColliderOverlayDecoder.Decode("s4", "{\"objects\":[]}");
            var second = ColliderOverlayDecoder.Decode("s4", "{\"objects\":[]}");
            Assert.AreEqual(0, first.Append(second).Objects.Count);
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                first.Append(ColliderOverlayDecoder.Decode("other", "{\"objects\":[]}")));
        }

        [TestMethod]
        public void DebugModCategoryColorsAreStable()
        {
            Assert.AreEqual(Colors.Yellow, ColliderOverlayColors.For("knight"));
            Assert.AreEqual(Colors.Red, ColliderOverlayColors.For("enemy"));
            Assert.AreEqual(Colors.Cyan, ColliderOverlayColors.For("attack"));
            Assert.AreEqual(Colors.LimeGreen, ColliderOverlayColors.For("terrain"));
            Assert.AreEqual(Colors.LightBlue, ColliderOverlayColors.For("trigger"));
            Assert.AreEqual(Colors.MediumPurple, ColliderOverlayColors.For("hazard"));
            Assert.AreEqual(Colors.Orange, ColliderOverlayColors.For("unknown"));
        }

        [TestMethod]
        public void DecoderKeepsEachColliderClassificationSeparate()
        {
            var json = "{\"objects\":[{\"id\":\"boss\",\"colliders\":["
                + "{\"componentIndex\":2,\"classification\":\"enemy\",\"screenPaths\":[{\"points\":[{\"x\":0,\"y\":0},{\"x\":0.1,\"y\":0}]}]},"
                + "{\"componentIndex\":3,\"classification\":\"attack\",\"screenPaths\":[{\"points\":[{\"x\":0.2,\"y\":0},{\"x\":0.3,\"y\":0}]}]}]}]}";
            var snapshot = ColliderOverlayDecoder.Decode("s5", json);

            Assert.AreEqual(2, snapshot.Objects.Count);
            Assert.AreEqual("boss:2", snapshot.Objects[0].Id);
            Assert.AreEqual("enemy", snapshot.Objects[0].Classification);
            Assert.AreEqual("boss:3", snapshot.Objects[1].Id);
            Assert.AreEqual("attack", snapshot.Objects[1].Classification);
        }

        [TestMethod]
        public void OverlayRendersNormalizedPathsToOfflineArtifact()
        {
            RunSta(() =>
            {
                var snapshot = new ColliderOverlaySnapshot("offline", null,
                    new[]
                    {
                        Object("knight", "knight", new[] { OverlayPath(0.08, 0.12, 0.24, 0.12, 0.24, 0.44, 0.08, 0.44) }),
                        Object("enemy", "enemy", new[] { OverlayPath(0.42, 0.16, 0.62, 0.16, 0.62, 0.40, 0.42, 0.40) }),
                        Object("attack", "attack", new[] { OverlayPath(0.70, 0.12, 0.94, 0.42) }),
                        Object("terrain", "terrain", new[] { OverlayPath(0.02, 0.88, 0.98, 0.88) }),
                        Object("trigger", "trigger", new[] { OverlayPath(0.30, 0.60, 0.38, 0.76, 0.22, 0.76) }),
                        Object("hazard", "hazard", new[] { OverlayPath(0.72, 0.62, 0.84, 0.76, 0.66, 0.76) }),
                        Object("offscreen", "other", new[] { OverlayPath(-0.2, 0.52, 0.4, 0.52, 1.2, 0.52) })
                    });
                var window = new ColliderOverlayWindow { Width = 640, Height = 360 };
                window.SetSnapshot(snapshot);
                var surface = (UIElement)window.Content!;
                surface.Measure(new Size(640, 360));
                surface.Arrange(new Rect(0, 0, 640, 360));
                surface.UpdateLayout();
                var bitmap = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render((Visual)surface);

                var path = RepositoryPath("artifacts", "world-observation", "overlay-offline.png");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(path)) encoder.Save(output);
                Assert.IsTrue(new FileInfo(path).Length > 100, "Offline overlay PNG was not written.");
                window.Close();
            });
        }

        [TestMethod]
        public void OverlayWindowIsTransparentClickThroughOwnedAndDpiPositioned()
        {
            RunSta(() =>
            {
                var owner = new Window { ShowInTaskbar = false };
                var ownerHandle = new WindowInteropHelper(owner).EnsureHandle();
                var window = new ColliderOverlayWindow();
                var handle = new WindowInteropHelper(window).EnsureHandle();
                window.SetOwner(ownerHandle);
                window.SetBounds(-320, -180, 640, 360, 144);

                Assert.IsFalse(window.Topmost);
                Assert.IsFalse(window.IsHitTestVisible);
                Assert.IsFalse(window.Focusable);
                Assert.IsFalse(window.ShowActivated);
                Assert.AreEqual(ownerHandle, new WindowInteropHelper(window).Owner);
                var style = GetWindowLongPtr(handle, -20).ToInt64();
                Assert.AreNotEqual(0, style & 0x20L, "WS_EX_TRANSPARENT missing.");
                Assert.AreNotEqual(0, style & 0x08000000L, "WS_EX_NOACTIVATE missing.");

                var surface = (FrameworkElement)window.Content!;
                Assert.IsTrue(surface.ClipToBounds);
                window.ShowOverlay();
                Assert.IsTrue(window.IsVisible);
                window.Hide();
                Assert.IsFalse(window.IsVisible);
                window.Close();
                owner.Close();
            });
        }

        [TestMethod]
        public void ControllerStopsTimerAndCleansUpWhenDisposed()
        {
            RunSta(() =>
            {
                var status = new List<string>();
                var controller = new ColliderOverlayController(
                    () => null,
                    (_, _, _) => throw new InvalidOperationException("not expected without a session"),
                    status.Add);
                var timer = (DispatcherTimer)typeof(ColliderOverlayController)
                    .GetField("pollTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!;
                Assert.IsFalse(timer.IsEnabled, "Constructing a disabled overlay must not start polling.");
                controller.SetEnabled(true);
                Assert.IsTrue(timer.IsEnabled);
                controller.Dispose();
                Assert.IsFalse(timer.IsEnabled);
                controller.Dispose();
                Assert.IsTrue(status.Any(value => value.Contains("等待", StringComparison.Ordinal)));
            });
        }

        [TestMethod]
        public void ViewModelSettingDefaultsOffAndPersistsInInjectedPath()
        {
            var root = Path.Combine(Path.GetTempPath(), "hktas-collider-" + Guid.NewGuid().ToString("N"));
            var setting = Path.Combine(root, "studio-collider-overlay.txt");
            var overrideProperty = typeof(MainViewModel).GetProperty(
                "ColliderOverlaySettingPathOverride", BindingFlags.Static | BindingFlags.NonPublic)!;
            overrideProperty.SetValue(null, setting);
            try
            {
                using var sessions = new SessionRegistry("overlay-setting-test");
                using var broker = new AutomationBroker(sessions);
                var first = NewViewModel(sessions, broker);
                Assert.IsFalse(first.ColliderOverlayEnabled);
                first.ColliderOverlayEnabled = true;
                Assert.AreEqual("enabled", File.ReadAllText(setting).Trim());
                DisposeColliderOverlayForTest(first);
                Assert.IsNull(typeof(MainViewModel).GetField("colliderOverlayController",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(first));

                var second = NewViewModel(sessions, broker);
                Assert.IsTrue(second.ColliderOverlayEnabled);
                second.ColliderOverlayEnabled = false;
                Assert.AreEqual("disabled", File.ReadAllText(setting).Trim());
                DisposeColliderOverlayForTest(second);
            }
            finally
            {
                overrideProperty.SetValue(null, null);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public void OverlayStatusTemplatesFollowLanguageSelection()
        {
            var path = Path.Combine(Path.GetTempPath(), "hktas-overlay-language-" + Guid.NewGuid(), "language.txt");
            try
            {
                var text = new UiText(path);
                text.LanguageIndex = 1;
                Assert.AreEqual("Collider overlay: 3 objects.", text.Translate("碰撞箱 3 个对象。"));
                Assert.AreEqual("Could not read colliders: stale snapshot", text.Translate("碰撞箱读取失败：stale snapshot"));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (Directory.Exists(Path.GetDirectoryName(path)!)) Directory.Delete(Path.GetDirectoryName(path)!, true);
            }
        }

        private static MainViewModel NewViewModel(SessionRegistry sessions, AutomationBroker broker)
            => new(sessions, new MovieEditorService(), new CapabilityBroker(),
                new NativeHostLauncher(AppContext.BaseDirectory), broker);

        private static ColliderOverlayObject Object(string id, string classification,
            IReadOnlyList<ColliderOverlayPath> paths)
            => new(id, classification, paths);

        private static ColliderOverlayPath OverlayPath(params double[] values)
        {
            var points = new List<ColliderOverlayPoint>();
            for (var index = 0; index < values.Length; index += 2)
                points.Add(new ColliderOverlayPoint(values[index], values[index + 1]));
            return new ColliderOverlayPath(points, true);
        }

        private static string RepositoryPath(params string[] parts)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "../../../../../");
            foreach (var part in parts) path = Path.Combine(path, part);
            return Path.GetFullPath(path);
        }

        private static void RunSta(Action action) => WpfTestApplication.Run(action);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        // Avoid exposing VM disposal only for a test; normal App Exit also disposes the controller.
        private static void DisposeColliderOverlayForTest(MainViewModel vm)
            => typeof(MainViewModel).GetMethod("DisposeColliderOverlay", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
    }
}
