using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HollowKnightTAS.Companion.Automation;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.ViewModels;
using HollowKnightTAS.Core.Ipc;
using HollowKnightTAS.Core.Movie;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Companion.Tests
{
    [TestClass]
    public sealed class InputGridFollowTests
    {
        [TestMethod]
        public void NativeFaultWithoutFrameAdvanceRefreshesPlaybackControls()
        {
            WpfTestApplication.Run(() =>
            {
                using var sessions = new SessionRegistry("grid-fault-test");
                using var broker = new AutomationBroker(sessions);
                using var boot = new StartupBootController();
                var vm = new MainViewModel(sessions, new MovieEditorService(),
                    new CapabilityBroker(), new NativeHostLauncher(AppContext.BaseDirectory),
                    broker, startupBoot: boot);
                var gate = boot.BeginV2();
                using var mapping = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(
                    "Local\\HKTAS.Boot." + gate.Token + ".V2State");
                using var view = mapping.CreateViewAccessor();
                boot.Refresh();
                var changes = 0;
                vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.PlayPauseLabel)) changes++; };
                view.Write(88, 41);
                view.Write(76, 3);
                boot.Refresh();
                Assert.AreEqual(1, changes, "A fault must notify even when frames and pending state do not change.");
                StringAssert.Contains(vm.PlayPauseLabel, "需重启");
                StringAssert.Contains(vm.Status, "41");
                Assert.IsFalse(vm.TogglePauseCommand.CanExecute(null));
                Assert.IsFalse(vm.StepCommand.CanExecute(null));
            });
        }

        [TestMethod]
        public void FullRunGridFollowsMovieFrameAcrossPagesAndCanBePausedForInspection()
        {
            using var sessions = new SessionRegistry("grid-follow-test");
            using var broker = new AutomationBroker(sessions);
            using var boot = new StartupBootController();
            var vm = new MainViewModel(sessions, new MovieEditorService(),
                new CapabilityBroker(), new NativeHostLauncher(AppContext.BaseDirectory),
                broker, startupBoot: boot);
            boot.BeginV2();
            var header = new MovieV2Header("game", "api", "mod", "profile",
                MovieProtocolV2.ActionSchemaId, false, new string('a', 64), 800, 450);
            vm.MovieText = new MovieV2Codec().WriteCanonical(new MovieV2Document(
                "grid-follow.hktas", header, new[]
                {
                    new NativeFrameRun(1200, Array.Empty<GameInputSample>(),
                        new MovieSourceSpan("grid-follow.hktas", 1, 1, 1))
                }));
            vm.RefreshGridCommand.Execute(null);
            Assert.AreEqual(1200, vm.InputRows.Count);

            var scrolledTo = -1L;
            vm.InputGridPositionChanged += frame => scrolledTo = frame;
            ReceiveFrame(vm, 620, 740);
            Assert.AreEqual(0L, vm.InputRows[0].Tick);
            Assert.AreEqual("▶", vm.InputRows.Single(row => row.Tick == 620).Current);
            Assert.AreEqual(620L, scrolledTo);
            StringAssert.Contains(vm.FrameCounterText, "Movie frame: 620");

            vm.GridStart = "1000";
            vm.GoToGridFrameCommand.Execute(null);
            Assert.AreEqual(1000L, scrolledTo);
            ReceiveFrame(vm, 620, 740);
            Assert.AreEqual(1000L, scrolledTo, "Unchanged paused frame must not pull a user back from manual navigation.");
            vm.ShowCurrentGridFrame();
            vm.AutoFollowGrid = false;
            ReceiveFrame(vm, 1150, 1300);
            Assert.AreEqual(0L, vm.InputRows[0].Tick);
            Assert.AreEqual(620L, scrolledTo);
            StringAssert.Contains(vm.FrameCounterText, "Movie frame: 1150");
            vm.FollowGridFrameCommand.Execute(null);
            Assert.AreEqual(0L, vm.InputRows[0].Tick);
            Assert.AreEqual("▶", vm.InputRows[1150].Current);
            Assert.AreEqual(1150L, scrolledTo);

            vm.AutoFollowGrid = true;
            ReceiveFrame(vm, 1200, 1350);
            Assert.AreEqual("▶", vm.InputRows.Single(row => row.Tick == 1199).Current);
            Assert.AreEqual(1199L, scrolledTo);
        }

        [TestMethod]
        public void CoreAudioEnumerationAcceptsInstalledWindowsInterfaces()
        {
            var type = typeof(RestorePresentation).Assembly.GetType("HollowKnightTAS.Companion.Services.ProcessAudioMute")!;
            using var mute = (IDisposable)Activator.CreateInstance(type, new object[] { int.MaxValue })!;
            type.GetMethod("MuteNewSessions")!.Invoke(mute, null);
        }

        [TestMethod]
        public async System.Threading.Tasks.Task FailedRestartAlwaysReleasesDisplayAndGrid()
        {
            using var sessions = new SessionRegistry("restore-failure-test");
            using var broker = new AutomationBroker(sessions);
            var released = false;
            var vm = new MainViewModel(sessions, new MovieEditorService(), new CapabilityBroker(),
                new NativeHostLauncher(AppContext.BaseDirectory), broker,
                restartProtectedGame: () => throw new InvalidOperationException("injected launch failure"),
                finishRestorePresentation: success =>
                {
                    Assert.IsFalse(success);
                    released = true;
                    return System.Threading.Tasks.Task.CompletedTask;
                });
            var header = new MovieV2Header("game", "api", "mod", "profile",
                MovieProtocolV2.ActionSchemaId, false, new string('a', 64), 800, 450);
            vm.MovieText = new MovieV2Codec().WriteCanonical(new MovieV2Document("failure.hktas", header,
                new[] { new NativeFrameRun(100, Array.Empty<GameInputSample>(), new MovieSourceSpan("failure.hktas", 1, 1, 1)) }));
            vm.RefreshGridCommand.Execute(null);
            var method = typeof(MainViewModel).GetMethod("RestartDraftAtAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            try
            {
                await (System.Threading.Tasks.Task)method.Invoke(vm, new object?[] { 10L, null,
                    System.Threading.CancellationToken.None, false, false, null, true })!;
                Assert.Fail("Expected launch failure.");
            }
            catch (InvalidOperationException e) { Assert.AreEqual("injected launch failure", e.Message); }
            Assert.IsTrue(released);
            Assert.IsTrue(vm.IsInputGridInteractive);
            Assert.AreEqual(0d, vm.RestoreProgress);
            Assert.AreEqual(100, vm.InputRows.Count);
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void RestoreRetainsRowsSelectionAndMarkerThenPublishesOnlyTarget(bool follow)
        {
            using var sessions = new SessionRegistry("restore-freeze-test");
            using var broker = new AutomationBroker(sessions);
            using var boot = new StartupBootController();
            var vm = new MainViewModel(sessions, new MovieEditorService(), new CapabilityBroker(),
                new NativeHostLauncher(AppContext.BaseDirectory), broker, startupBoot: boot);
            boot.BeginV2();
            var header = new MovieV2Header("game", "api", "mod", "profile",
                MovieProtocolV2.ActionSchemaId, false, new string('a', 64), 800, 450);
            string Movie(int frames) => new MovieV2Codec().WriteCanonical(new MovieV2Document(
                "freeze.hktas", header, new[] { new NativeFrameRun(frames, Array.Empty<GameInputSample>(),
                    new MovieSourceSpan("freeze.hktas", 1, 1, 1)) }));
            vm.MovieText = Movie(1200);
            vm.RefreshGridCommand.Execute(null);
            ReceiveFrame(vm, 900, 1100);
            vm.GridStart = "710";
            vm.GridCount = "3";
            vm.AutoFollowGrid = follow;
            var rows = vm.InputRows;
            var scrolls = new List<long>();
            vm.InputGridPositionChanged += scrolls.Add;
            var freeze = typeof(MainViewModel).GetMethod("SetRestorePresentationFrozen", BindingFlags.Instance | BindingFlags.NonPublic)!;
            freeze.Invoke(vm, new object[] { true });
            typeof(MainViewModel).GetField("restoreTargetFrame", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, 600L);
            ReceiveFrame(vm, 900, 1100);
            Assert.AreEqual(0d, vm.RestoreProgress, "The departing game's progress must not fill the new replay bar.");
            typeof(MainViewModel).GetField("restoreReplayStarted", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, true);
            vm.MovieText = Movie(1500); // Loading a different world's Movie must not clear the display.
            vm.RefreshGridCommand.Execute(null);
            ReceiveFrame(vm, 0, 0);
            ReceiveFrame(vm, 300, 500);
            Assert.AreEqual(0.5d, vm.RestoreProgress, "Use Movie frames, not native/loading frames.");
            vm.ShowCurrentGridFrame();
            Assert.AreSame(rows, vm.InputRows);
            Assert.AreEqual("▶", vm.InputRows[900].Current);
            Assert.AreEqual(0, scrolls.Count);
            Assert.IsFalse(vm.IsInputGridInteractive);
            ReceiveFrame(vm, 600, 800);
            Assert.AreEqual(1d, vm.RestoreProgress);
            freeze.Invoke(vm, new object[] { false });
            Assert.AreEqual(1500, vm.InputRows.Count);
            Assert.AreEqual("▶", vm.InputRows[600].Current);
            Assert.AreEqual(follow, vm.AutoFollowGrid);
            Assert.AreEqual("710", vm.GridStart);
            Assert.AreEqual("3", vm.GridCount);
            Assert.IsTrue(vm.IsInputGridInteractive);
            CollectionAssert.AreEqual(follow ? new long[] { 600 } : Array.Empty<long>(), scrolls.ToArray());
            freeze.Invoke(vm, new object[] { true });
            Assert.AreEqual(0d, vm.RestoreProgress, "A subsequent restore starts empty.");
            typeof(MainViewModel).GetField("restoreTargetFrame", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, 0L);
            typeof(MainViewModel).GetField("restoreReplayStarted", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, true);
            ReceiveFrame(vm, 0, 0);
            Assert.AreEqual(0d, vm.RestoreProgress, "Frame-zero preparation must not divide by zero or pretend to be complete.");
            freeze.Invoke(vm, new object[] { false });
        }

        private static void ReceiveFrame(MainViewModel vm, long movieFrame, long nativeFrame)
        {
            var handle = typeof(MainViewModel).GetMethod("HandleTypedEvent",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            handle.Invoke(vm, new object[]
            {
                IpcMessageTypes.FullRunState,
                new Dictionary<string, string>
                {
                    ["movieFrame"] = movieFrame.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["nativeFrame"] = nativeFrame.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }
            });
        }
    }
}
