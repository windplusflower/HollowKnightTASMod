using System;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Companion.Automation;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Companion.Tests
{
    [TestClass]
    public sealed class FrameMenuCommandStateTests
    {
        [TestMethod]
        public void FrameNavigationSeparatesPastFutureAndCurrent()
        {
            using var sessions = new SessionRegistry("frame-direction-test");
            using var boot = new StartupBootController();
            var coordinator = new FullRunMovieCoordinator(boot);
            using var broker = new AutomationBroker(sessions, fullRunMovies: coordinator);
            var vm = new MainViewModel(sessions, new MovieEditorService(), new CapabilityBroker(),
                new NativeHostLauncher(AppContext.BaseDirectory), broker, startupBoot: boot, fullRunMovies: coordinator);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var gate = boot.BeginV2();
            typeof(FullRunMovieCoordinator).GetField("gate", flags)!.SetValue(coordinator, gate);
            typeof(FullRunMovieCoordinator).GetField("mode", flags)!.SetValue(coordinator, "Replay");
            typeof(MainViewModel).GetField("currentFullRunMovieFrame", flags)!.SetValue(vm, 10L);
            typeof(MainViewModel).GetField("gridTotalFrames", flags)!.SetValue(vm, 20L);
            Assert.IsTrue(vm.CanNavigateFrame(11, true));
            Assert.IsTrue(vm.CanNavigateFrame(9, false));
            Assert.IsFalse(vm.CanNavigateFrame(9, true));
            Assert.IsFalse(vm.CanNavigateFrame(11, false));
            Assert.IsFalse(vm.CanNavigateFrame(10, true));
            Assert.IsFalse(vm.CanNavigateFrame(10, false));
            Assert.IsFalse(vm.CanNavigateFrame(21, true));
            Assert.IsFalse(vm.CanNavigateFrame(-1, false));
        }

        [TestMethod]
        public void CompletedReplayOffersRecoveryButNotTerminalStop()
        {
            using var sessions = new SessionRegistry("completed-controls-test");
            using var boot = new StartupBootController();
            var coordinator = new FullRunMovieCoordinator(boot);
            using var broker = new AutomationBroker(sessions, fullRunMovies: coordinator);
            var vm = new MainViewModel(sessions, new MovieEditorService(),
                new CapabilityBroker(), new NativeHostLauncher(AppContext.BaseDirectory),
                broker, startupBoot: boot, fullRunMovies: coordinator,
                restartProtectedGame: () => Task.CompletedTask);
            var gate = boot.BeginV2();
            typeof(FullRunMovieCoordinator).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(coordinator, gate);
            typeof(FullRunMovieCoordinator).GetField("mode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(coordinator, "Replay");
            using var mapping = MemoryMappedFile.OpenExisting("Local\\HKTAS.Boot." + gate.Token + ".V2State");
            using var view = mapping.CreateViewAccessor();
            using var ready = EventWaitHandle.OpenExisting("Local\\HKTAS.Boot." + gate.Token + ".Ready");
            view.Write(92, 1); view.Write(76, 4); view.Write(40, 562L);
            ready.Set(); boot.Refresh();
            Assert.AreEqual("Completed", coordinator.Mode);
            Assert.IsTrue(vm.TogglePauseCommand.CanExecute(null));
            Assert.IsTrue(vm.StepCommand.CanExecute(null));
            Assert.IsTrue(vm.ApplyGridAndSeekCommand.CanExecute(null));
            Assert.IsFalse(vm.StopReplayCommand.CanExecute(null));
            var header = new HollowKnightTAS.Core.Movie.MovieV2Header("game", "api", "mod", "profile",
                HollowKnightTAS.Core.Movie.MovieProtocolV2.ActionSchemaId, false, new string('a', 64), 800, 450);
            vm.MovieText = new HollowKnightTAS.Core.Movie.MovieV2Codec().WriteCanonical(
                new HollowKnightTAS.Core.Movie.MovieV2Document("end.hktas", header, new[] {
                    new HollowKnightTAS.Core.Movie.NativeFrameRun(500,
                        Array.Empty<HollowKnightTAS.Core.Movie.GameInputSample>(),
                        new HollowKnightTAS.Core.Movie.MovieSourceSpan("end.hktas", 1, 1, 1)) }));
            vm.RefreshGridCommand.Execute(null);
            vm.AppendGridBlankFrames();
            Assert.AreEqual(1000, vm.InputRows.Count);
            Assert.IsTrue((bool)typeof(MainViewModel).GetField("gridHasUserEdits", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!,
                "Appending replay frames must schedule synchronization before the next Play/Step.");
            Assert.AreEqual(500L, typeof(MainViewModel).GetField("earliestGridEdit", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm));
            view.Write(88, 41); boot.Refresh();
            Assert.IsFalse(vm.TogglePauseCommand.CanExecute(null));
            Assert.IsFalse(vm.StepCommand.CanExecute(null));
        }

        [TestMethod]
        public async Task NativePauseDuringSeekPublishesEnabledControlsAfterOperationEnds()
        {
            using var sessions = new SessionRegistry("seek-controls-test");
            using var boot = new StartupBootController();
            var coordinator = new FullRunMovieCoordinator(boot);
            using var broker = new AutomationBroker(sessions, fullRunMovies: coordinator);
            var vm = new MainViewModel(sessions, new MovieEditorService(),
                new CapabilityBroker(), new NativeHostLauncher(AppContext.BaseDirectory),
                broker, startupBoot: boot, fullRunMovies: coordinator);
            var gate = boot.BeginV2();
            typeof(FullRunMovieCoordinator).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(coordinator, gate);
            typeof(FullRunMovieCoordinator).GetField("mode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(coordinator, "Replay");
            using var mapping = MemoryMappedFile.OpenExisting("Local\\HKTAS.Boot." + gate.Token + ".V2State");
            using var view = mapping.CreateViewAccessor();
            using var ready = EventWaitHandle.OpenExisting("Local\\HKTAS.Boot." + gate.Token + ".Ready");
            view.Write(92, 1);
            view.Write(76, 2); // Running during seek.
            boot.Refresh();
            var setBusy = typeof(MainViewModel).GetMethod("SetGridApplying", BindingFlags.Instance | BindingFlags.NonPublic)!;
            setBusy.Invoke(vm, new object[] { true });
            bool publishedPlay = false, publishedStep = false;
            string publishedLabel = vm.PlayPauseLabel;
            vm.TogglePauseCommand.CanExecuteChanged += (_, _) => publishedPlay = vm.TogglePauseCommand.CanExecute(null);
            vm.StepCommand.CanExecuteChanged += (_, _) => publishedStep = vm.StepCommand.CanExecute(null);
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.PlayPauseLabel)) publishedLabel = vm.PlayPauseLabel; };

            view.Write(40, 66L);
            view.Write(76, 0); // Runtime automatically pauses at the target.
            ready.Set();
            boot.Refresh();
            Assert.IsFalse(publishedPlay);
            Assert.IsFalse(publishedStep);
            setBusy.Invoke(vm, new object[] { false });
            Assert.IsTrue(publishedPlay, "Bindings must receive enabled state after seek releases the UI.");
            Assert.IsTrue(publishedStep);
            Assert.AreEqual("Play 继续", publishedLabel);

            // The same finally path must also recover controls on seek validation errors.
            await vm.FrameMenuAsync("seek", -1);
            StringAssert.Contains(vm.GridStatus, "目标帧超出序列");
            Assert.IsTrue(publishedPlay);
            Assert.IsTrue(publishedStep);
            Assert.AreEqual("Play 继续", publishedLabel);
        }

        [TestMethod]
        public void FrameNavigationAllowsForwardSeekAtFrameZeroAfterOpeningSequence()
        {
            using var sessions = new SessionRegistry("frame-zero-nav-test");
            using var boot = new StartupBootController();
            var coordinator = new FullRunMovieCoordinator(boot);
            using var broker = new AutomationBroker(sessions, fullRunMovies: coordinator);
            var vm = new MainViewModel(sessions, new MovieEditorService(),
                new CapabilityBroker(), new NativeHostLauncher(AppContext.BaseDirectory),
                broker, startupBoot: boot, fullRunMovies: coordinator,
                restartProtectedGame: () => Task.CompletedTask);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var gate = boot.BeginV2();
            typeof(FullRunMovieCoordinator).GetField("gate", flags)!.SetValue(coordinator, gate);
            using var mapping = MemoryMappedFile.OpenExisting("Local\\HKTAS.Boot." + gate.Token + ".V2State");
            using var view = mapping.CreateViewAccessor();
            using var ready = EventWaitHandle.OpenExisting("Local\\HKTAS.Boot." + gate.Token + ".Ready");

            // Wait at native frame 0 before any FullRunState update arrives (currentFullRunMovieFrame == -1).
            view.Write(92, 1); view.Write(76, 0); view.Write(40, 0L);
            ready.Set();
            boot.Refresh();

            typeof(MainViewModel).GetField("currentFullRunMovieFrame", flags)!.SetValue(vm, -1L);
            typeof(MainViewModel).GetField("gridTotalFrames", flags)!.SetValue(vm, 500L);

            // Scenario 1: Opened .hktas without initial saves -> Replay armed at frame 0.
            typeof(FullRunMovieCoordinator).GetField("mode", flags)!.SetValue(coordinator, "Replay");
            typeof(MainViewModel).GetField("draftRequiresRestart", flags)!.SetValue(vm, false);
            Assert.IsTrue(vm.CanNavigateFrame(1, true));
            Assert.IsTrue(vm.CanNavigateFrame(500, true));
            Assert.IsFalse(vm.CanNavigateFrame(0, true));
            Assert.IsFalse(vm.CanNavigateFrame(1, false));

            // Scenario 2: Opened .hktaspack with initial saves -> Unarmed, draftRequiresRestart == true.
            typeof(FullRunMovieCoordinator).GetField("mode", flags)!.SetValue(coordinator, "Unarmed");
            typeof(MainViewModel).GetField("draftRequiresRestart", flags)!.SetValue(vm, true);
            Assert.IsTrue(vm.CanNavigateFrame(1, true));
            Assert.IsTrue(vm.CanNavigateFrame(500, true));
            Assert.IsFalse(vm.CanNavigateFrame(0, true));
            Assert.IsFalse(vm.CanNavigateFrame(10, false));

            // Scenario 3: Native frame 0 with no sequence opened yet (Unarmed, draftRequiresRestart == false).
            typeof(MainViewModel).GetField("draftRequiresRestart", flags)!.SetValue(vm, false);
            Assert.IsFalse(vm.CanNavigateFrame(10, true));

            // Scenario 4: Switched to a new sequence after a previous session reached F300 and Completed.
            view.Write(76, 4); view.Write(40, 300L);
            boot.Refresh();
            typeof(FullRunMovieCoordinator).GetField("mode", flags)!.SetValue(coordinator, "Replay");
            typeof(MainViewModel).GetField("currentFullRunMovieFrame", flags)!.SetValue(vm, 300L);
            typeof(MainViewModel).GetField("draftRequiresRestart", flags)!.SetValue(vm, true);
            Assert.AreEqual("Completed", coordinator.Mode);
            Assert.IsTrue(vm.CanNavigateFrame(100, true));
            Assert.IsTrue(vm.CanNavigateFrame(400, true));
            Assert.IsFalse(vm.CanNavigateFrame(100, false));

            // Scenario 5: Completed replay on the same sequence at F500 of 1000 allows forward and backward navigation.
            view.Write(40, 500L);
            boot.Refresh();
            typeof(MainViewModel).GetField("currentFullRunMovieFrame", flags)!.SetValue(vm, 500L);
            typeof(MainViewModel).GetField("gridTotalFrames", flags)!.SetValue(vm, 1000L);
            typeof(MainViewModel).GetField("draftRequiresRestart", flags)!.SetValue(vm, false);
            Assert.AreEqual("Completed", coordinator.Mode);
            Assert.IsTrue(vm.CanNavigateFrame(600, true));
            Assert.IsTrue(vm.CanNavigateFrame(400, false));
            Assert.IsFalse(vm.CanNavigateFrame(500, true));
        }

        [TestMethod]
        public void NonRetryableCheckpointFailureDetectsDeterministicRejections()
        {
            Assert.IsTrue(MainViewModel.IsNonRetryableCheckpointFailure(
                new InvalidOperationException("LeanS0 profileID is outside valid save slots 1..4: 0")));
            Assert.IsTrue(MainViewModel.IsNonRetryableCheckpointFailure(
                new InvalidOperationException("LeanS0 profileID is 0 (new-game checkpoint requires an in-game process; falling back to cold replay).")));
            Assert.IsTrue(MainViewModel.IsNonRetryableCheckpointFailure(
                new InvalidOperationException("LeanS0 target save slot 3 has no initial save on disk (new-game path); falling back to cold replay.")));
            Assert.IsTrue(MainViewModel.IsNonRetryableCheckpointFailure(
                new InvalidOperationException("Checkpoint target execution environment mismatch.")));
            Assert.IsTrue(MainViewModel.IsNonRetryableCheckpointFailure(
                new System.IO.InvalidDataException("Invalid checkpoint payload")));

            Assert.IsFalse(MainViewModel.IsNonRetryableCheckpointFailure(
                new TimeoutException("检查点替换实例启动超时。")));
            Assert.IsFalse(MainViewModel.IsNonRetryableCheckpointFailure(
                new InvalidOperationException("Pipe disconnected while reading checkpoint status.")));
        }
    }
}
