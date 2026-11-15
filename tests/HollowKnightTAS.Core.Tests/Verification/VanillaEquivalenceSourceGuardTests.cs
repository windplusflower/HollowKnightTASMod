using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.Verification
{
    [TestClass]
    public sealed class VanillaEquivalenceSourceGuardTests
    {
        [TestMethod]
        public void OfflineReferenceAdmissionRequiresNativeSceneLifecycleTelemetry()
        {
            var envelope = ReadScript("Measure-T24ReferenceEnvelope.ps1");
            var catalog = ReadScript("New-T24SupplementalReferenceCatalog.ps1");
            foreach (var source in new[] { envelope, catalog })
            {
                StringAssert.Contains(source, ". (Join-Path $PSScriptRoot 'T24ClockHarness.ps1')");
                var read = source.IndexOf("$result = Get-Content -LiteralPath $resultPath", StringComparison.Ordinal);
                var gate = source.IndexOf("if (-not (Test-T24NativeSceneLifecycleContract -Telemetry $result))", read, StringComparison.Ordinal);
                Assert.IsTrue(read >= 0 && gate > read && gate - read < 150);
            }
            StringAssert.Contains(envelope, "Add-Violation -Category 'native-scene-lifecycle'");
            StringAssert.Contains(catalog, "throw \"Supplemental reference violates the native scene lifecycle contract:");
        }

        [TestMethod]
        public void StaticAuditIncludesExternalClockPayloadAndDeclaresNativeLimit()
        {
            var audit = ReadScript("Invoke-T24StaticMutationAudit.ps1");
            var roots = ExtractMethod(audit, "$requiredScanRoots = @(", "$allowedReachability = @(");
            StringAssert.Contains(roots, "src/HollowKnightTAS.ClockPayload");
            StringAssert.Contains(audit, "Native ClockStartup/injector and third-party binary behavior require separate review.");
        }

        [TestMethod]
        public void StaticAuditRecognizesSceneActivationAndReflectiveFinishReplacement()
        {
            var audit = ReadScript("Invoke-T24StaticMutationAudit.ps1");
            var activation = ReadAuditRegex(audit, "sceneActivationRegex");
            Assert.IsTrue(activation.IsMatch("sceneLoad.IsActivationAllowed = false;"));
            Assert.IsTrue(activation.IsMatch("operation.allowSceneActivation = true;"));
            Assert.IsFalse(activation.IsMatch("if (sceneLoad.IsActivationAllowed == false)"));
            var field = ReadAuditRegex(audit, "reflectionFieldWriterRegex");
            Assert.IsTrue(field.IsMatch("SceneLoadFinishEventField.SetValue(sceneLoad, replacement);"));
            Assert.IsTrue(field.IsMatch("SceneLoadFinishEventField?.SetValue(sceneLoad, original);"));
            Assert.IsFalse(field.IsMatch("SceneLoadFinishEventField.GetValue(sceneLoad);"));
            var time = ReadAuditRegex(audit, "timeWriterRegex");
            Assert.IsTrue(time.IsMatch("Time.captureDeltaTime = fixedDeltaTime;"));
            Assert.IsFalse(time.IsMatch("if (Time.captureDeltaTime == fixedDeltaTime)"));
        }

        private static System.Text.RegularExpressions.Regex ReadAuditRegex(string script, string variable)
        {
            var definition = System.Text.RegularExpressions.Regex.Match(script,
                @"\$" + variable + @" = \[regex\]::new\(\s*""([^""]+)""\)");
            Assert.IsTrue(definition.Success, "Missing audit expression: " + variable);
            return new System.Text.RegularExpressions.Regex(definition.Groups[1].Value);
        }

        [TestMethod]
        public void PausedWindowExitUsesSharedSafetyGateWithoutReleasingGameplay()
        {
            var source = ReadRuntimeSource("Ipc", "RuntimeCommandDispatcher.cs");
            var pump = ExtractMethod(source, "private void PumpPausedBoundaryCommands()",
                "private void PumpColdRestoreBoundaryCommands()");
            Assert.IsTrue(pump.IndexOf("PumpPausedPersistence()", StringComparison.Ordinal)
                < pump.IndexOf("TryExitFromPausedWindowShortcut()", StringComparison.Ordinal));
            Assert.IsTrue(pump.IndexOf("TryExitFromPausedWindowShortcut()", StringComparison.Ordinal)
                < pump.IndexOf("commands.WaitForActivity", StringComparison.Ordinal));
            StringAssert.Contains(pump, "if (controls.ControlMode != SimulationControlMode.Paused)\n                    pausedWindowExitPending = false;");
            var shortcut = ExtractMethod(source, "private bool TryExitFromPausedWindowShortcut()",
                "private string RequestPausedGameExit()");
            Assert.IsTrue(shortcut.IndexOf("RequestPausedGameExit();", StringComparison.Ordinal)
                < shortcut.IndexOf("ExitApprovedGameProcess();", StringComparison.Ordinal));
            Assert.IsFalse(shortcut.Contains("ReleaseBoundaryForApplicationQuit();"));
            Assert.IsFalse(shortcut.Contains(".Resume("));
            Assert.IsFalse(shortcut.Contains(".Step("));
            var native = ReadRuntimeSource("Ipc", "PausedWindowExitShortcut.cs");
            Assert.IsFalse(native.Contains("PeekMessage("));
            Assert.IsFalse(native.Contains("DispatchMessage("));
            Assert.IsFalse(native.Contains("UnityEngine.Input"));
        }

        [TestMethod]
        public void ColdRestoreRejectsUnapprovedSlotBeforePublishingRestartIntent()
        {
            var manager = ReadRuntimeSource("ReplaySave", "RuntimeReplaySaveManager.cs");
            var prepare = ExtractMethod(manager, "public ColdRestoreIntent PrepareColdRestoreIntent(",
                "return new ColdRestoreIntent(");
            StringAssert.Contains(prepare, "ValidateColdBaselineBeforeHandoff(rootBaseline)");
            Assert.IsTrue(prepare.IndexOf("ValidateColdBaselineBeforeHandoff", StringComparison.Ordinal)
                < prepare.IndexOf("journal.FreezeForReplaySave(", StringComparison.Ordinal));
            var coordinator = ReadRuntimeSource("ReplaySave", "RuntimeReplayRestoreCoordinator.cs");
            var preflight = ExtractMethod(coordinator, "public void ValidateColdBaselineBeforeHandoff(",
                "private BaselineInstallPlan? pendingSourceSlotPlan;");
            StringAssert.Contains(preflight, "plan.RequiresOverwriteApproval");
            Assert.IsFalse(preflight.Contains(".Install("));
            Assert.IsFalse(preflight.Contains("UserOverwriteApproval.Approved"));
        }

        [TestMethod]
        public void SourceSlotApprovalIsBoundedCancellableAndRechecksBytesBeforeWrites()
        {
            var coordinator = ReadRuntimeSource("ReplaySave", "RuntimeReplayRestoreCoordinator.cs");
            var resolve = ExtractMethod(coordinator, "public string ResolveSourceSlotApproval(",
                "public ReplayRestoreHandle Begin(");
            Assert.IsTrue(resolve.IndexOf("if (!approved)", StringComparison.Ordinal)
                < resolve.IndexOf("baselineProvider.Install", StringComparison.Ordinal));
            StringAssert.Contains(resolve, "pendingSourceSlotPlan = null");
            StringAssert.Contains(resolve, "DateTimeOffset.UtcNow > sourceSlotProposalExpiresAt");
            var provider = ReadRuntimeSource("ReplaySave", "DesktopSaveSlotBaselineProvider.cs");
            var install = ExtractMethod(provider, "public BaselineInstallResult Install(",
                "internal void RestoreOriginal(");
            Assert.IsTrue(install.IndexOf("PlanInstall(plan.Bundle", StringComparison.Ordinal)
                < install.IndexOf("CreateBackup(plan)", StringComparison.Ordinal));
            StringAssert.Contains(install, "plan.CurrentModdedSaveData, current.CurrentModdedSaveData");
            StringAssert.Contains(install, "plan.CurrentSaveData, current.CurrentSaveData");
            var dispatcher = ReadRuntimeSource("Ipc", "RuntimeCommandDispatcher.cs");
            StringAssert.Contains(dispatcher, "ResolveSourceSlotApproval(approved)");
            StringAssert.Contains(dispatcher, "ResolveSourceSlotApproval(false)");
        }

        [TestMethod]
        public void CombatObservationIncludesTriggerHazardsAndReadOnlyHealthComponents()
        {
            var source = ReadRuntimeSource("Inspector", "BossPracticeWatchProvider.cs");
            var hazards = ExtractMethod(source, "private string BuildHazardCatalog(",
                "private static string Facing(");
            StringAssert.Contains(hazards, "includeTriggers: true");
            StringAssert.Contains(hazards, "\"colliderAvailable\"");
            StringAssert.Contains(hazards, "\"Unavailable\"");
            StringAssert.Contains(hazards, "BuildColliderCatalog(hazard, heroCollider, heroPosition)");
            var health = ExtractMethod(source, "private static string BuildHealthCatalog(",
                "private string BuildHazardCatalog(");
            StringAssert.Contains(health, "GetComponentsInChildren<HealthManager>(true)");
            StringAssert.Contains(health, "health.IsInvincible");
            StringAssert.Contains(health, "health.InvincibleFromDirection");
            foreach (var mutation in new[] { ".Hit(", ".Die(", ".SendEvent(", ".hp =", ".IsInvincible =" })
                Assert.IsFalse(health.Contains(mutation), mutation);
            StringAssert.Contains(source, "\"hero.collidersJson\"");
            StringAssert.Contains(source, "\"primaryHeroCollider\", collider == heroCollider");
        }

        [TestMethod]
        public void InitialSlotLoadUsesNativeMenuWithoutStateOrFileWrites()
        {
            var dispatcher = ReadRuntimeSource("Ipc", "RuntimeCommandDispatcher.cs");
            var load = ExtractMethod(dispatcher, "private string LoadExistingGameSlot(",
                "private string QuiesceColdRestoreSource(");
            foreach (var guard in new[] { "slot < 1 || slot > 4", "SimulationControlMode.Running",
                "RuntimePauseController.IsStableTitleMenu()", "journal.CanLoadInitialGameSlot",
                "gameSlotLoadRequested", "saves.IsRestoreActive", "File.Exists(path)",
                "GameManager.instance.LoadGameFromUI(slot)" })
                StringAssert.Contains(load, guard);
            foreach (var forbidden in new[] { "File.Write", "File.Copy", "File.Delete", "PlayerData",
                "HeroController", "ChangeScene", ".Resume(", ".Step(" })
                Assert.IsFalse(load.Contains(forbidden), forbidden);
            var journal = ReadRuntimeSource("Playback", "RuntimeReplayJournal.cs");
            StringAssert.Contains(journal, "!sourceRecordingRoot.PreparationStarted");
            StringAssert.Contains(journal, "currentSaveSlot = manager.profileID");
            StringAssert.Contains(journal.Replace("\r\n", "\n"), "currentSaveSlot,\n                capture,");
        }

        [TestMethod]
        public void ExplicitQuitRequiresIdlePausedPersistenceAndDoesNotResume()
        {
            var dispatcher = ReadRuntimeSource("Ipc", "RuntimeCommandDispatcher.cs");
            var quit = ExtractMethod(dispatcher, "private string RequestPausedGameExit()",
                "private string BeginSourceLifecycleReload(");
            StringAssert.Contains(quit, "controls.ControlMode != SimulationControlMode.Paused");
            StringAssert.Contains(quit, "controls.PlaybackMode != PlaybackMode.Idle");
            StringAssert.Contains(quit, "saves.PendingCount != 0 || saves.IsRestoreActive");
            StringAssert.Contains(quit, "gameExitRequested = true");
            Assert.IsFalse(quit.Contains(".Resume("));
            Assert.IsFalse(quit.Contains(".Step("));
            var pump = ExtractMethod(dispatcher, "private void PumpPausedBoundaryCommands()",
                "private void PumpColdRestoreBoundaryCommands()");
            StringAssert.Contains(pump, "if (coldSourceExitRequested)");
            StringAssert.Contains(pump, "if (gameExitRequested)");
            Assert.IsTrue(pump.IndexOf("Dispatch(command, atCompletedFrameBoundary: true)", StringComparison.Ordinal)
                < pump.IndexOf("ExitApprovedGameProcess();", StringComparison.Ordinal));
            StringAssert.Contains(pump, "controls.ReleaseBoundaryForApplicationQuit()");
            var exit = ExtractMethod(dispatcher, "private void ExitApprovedGameProcess()",
                "private bool pausedWindowExitPending;");
            StringAssert.Contains(exit, "if (!startupHandoffExitRequested)");
            StringAssert.Contains(exit, "shutdownCompanionForExit?.Invoke()");
            Assert.IsTrue(exit.IndexOf("shutdownCompanionForExit?.Invoke()", StringComparison.Ordinal)
                < exit.IndexOf("ExitProcess(0);", StringComparison.Ordinal));
        }

        [TestMethod]
        public void ColdHandoffTransfersBackgroundAuthoringPolicyWithoutAdvancing()
        {
            var controls = ReadRuntimeSource("Companion", "RuntimeControlService.cs");
            var adopt = ExtractMethod(controls, "public ControlResult AdoptRestoredPauseBoundary(",
                "public ControlResult PrepareForReplayRestore()");
            StringAssert.Contains(adopt, "pauseBoundaryCommandPump, abortOnFocusLoss: false");
            Assert.IsFalse(adopt.Contains(".Resume("));
            Assert.IsFalse(adopt.Contains(".Step("));
            var pause = ReadRuntimeSource("Control", "RuntimePauseController.cs");
            var transfer = ExtractMethod(pause,
                "public void ReplacePausedBoundaryCommandPump(Action replacement, bool abortOnFocusLoss)",
                "public bool TryTakeCompletedStep(");
            StringAssert.Contains(transfer, "machine.Mode != SimulationControlMode.Paused");
            StringAssert.Contains(transfer, "this.abortOnFocusLoss = abortOnFocusLoss;");
        }

        [TestMethod]
        public void OperationalStatusRequestDoesNotCaptureHeroOrAdvance()
        {
            var dispatcher = ReadRuntimeSource("Ipc", "RuntimeCommandDispatcher.cs");
            var branch = ExtractMethod(dispatcher, "if (command.Fields.ContainsKey(\"statusOnly\"))",
                "PublishSnapshot(");
            StringAssert.Contains(branch, "RequireFields(command.Fields, \"requestId\", \"statusOnly\")");
            StringAssert.Contains(branch, "PublishRuntimeStatus(command.Fields[\"requestId\"])");
            Assert.IsFalse(branch.Contains(".Step("));
            Assert.IsFalse(branch.Contains(".Resume("));
        }

        [TestMethod]
        public void InputBatchCursorInheritsJournalHeldWithoutWritingHeroState()
        {
            var controls = ReadRuntimeSource("Companion", "RuntimeControlService.cs");
            StringAssert.Contains(controls, "initialHeld: inputBatchActive");
            StringAssert.Contains(controls, "journal.LastCommittedSample?.Held ?? TasAction.None");
            var playback = ReadRuntimeSource("Playback", "RuntimePlaybackController.cs");
            StringAssert.Contains(playback, "context.InitialHeld");
        }

        [TestMethod]
        public void InputBatchCleanupPrecedesPausedCommandsWithoutAnExtraFrame()
        {
            var controls = ReadRuntimeSource("Companion", "RuntimeControlService.cs");
            var complete = ExtractMethod(controls,
                "internal bool CompletePlaybackAtPausedBoundary()",
                "public ControlResult StartInputBatch(");
            StringAssert.Contains(complete, "SimulationControlMode.Paused");
            StringAssert.Contains(complete, "playback?.CompleteAtPausedBoundary()");
            Assert.IsFalse(complete.Contains(".Step("));
            Assert.IsFalse(complete.Contains(".Resume("));
            var dispatcher = ReadRuntimeSource("Ipc", "RuntimeCommandDispatcher.cs");
            var pump = ExtractMethod(dispatcher, "private void PumpPausedBoundaryCommands()",
                "private void PumpColdRestoreBoundaryCommands()");
            var cleanup = pump.IndexOf("controls.CompletePlaybackAtPausedBoundary()", StringComparison.Ordinal);
            Assert.IsTrue(cleanup >= 0);
            Assert.IsTrue(cleanup < pump.IndexOf("PumpPausedPersistence()", StringComparison.Ordinal));
            Assert.IsTrue(cleanup < pump.IndexOf("commands.WaitForActivity(", StringComparison.Ordinal));
        }

        [TestMethod]
        public void ManualBatchStopAlsoInterruptsItsFrameQuota()
        {
            var service = ReadRuntimeSource("Companion", "RuntimeControlService.cs");
            var stop = ExtractMethod(service, "public PlaybackStopResult StopReplay()", "public ControlResult Pause()");
            StringAssert.Contains(stop, "inputBatchActive");
            StringAssert.Contains(stop, "SimulationControlMode.Stepping");
            StringAssert.Contains(stop, "result.Success && interruptInputBatch");
            StringAssert.Contains(stop, "InterruptStepAfterCurrentFrame(\"manual-stop\")");
            Assert.IsFalse(stop.Contains(".Resume("));
        }

        [TestMethod]
        public void ColdTargetCompletesBindingCleanupWithoutAdvancingAnotherInputFrame()
        {
            var replayer = ReadRuntimeSource("Playback", "HeroActionReplayer.cs");
            var complete = ExtractMethod(replayer, "internal bool CompleteAtPausedBoundary()",
                "private void Cleanup(");
            StringAssert.Contains(complete, "machine.Mode != PlaybackMode.Stopping");
            StringAssert.Contains(complete, "machine.StopReason != PlaybackStopReason.Completed");
            StringAssert.Contains(complete, "machine.StopReason != PlaybackStopReason.Manual");
            StringAssert.Contains(complete, "Cleanup(machine.StopReason.Value, string.Empty)");
            Assert.IsFalse(complete.Contains("machine.Advance("));
            Assert.IsFalse(complete.Contains("OnInputManagerUpdated("));
            var restore = ReadRuntimeSource("ReplaySave", "RuntimeReplayRestoreCoordinator.cs");
            var pump = ExtractMethod(restore, "private void PumpColdRestoreBoundary()",
                "private void StartFullReplay(");
            StringAssert.Contains(pump, "SimulationControlMode.Paused");
            StringAssert.Contains(pump, "playbackController?.CompleteAtPausedBoundary()");
            Assert.IsTrue(pump.IndexOf("CaptureAndVerifyTarget()", StringComparison.Ordinal)
                < pump.IndexOf("CompleteAtPausedBoundary()", StringComparison.Ordinal));
            Assert.IsFalse(pump.Contains(".Resume("));
            Assert.IsFalse(pump.Contains(".Step("));
            var replay = ExtractMethod(restore, "private void AdvanceReplay()", "private void CaptureAndVerifyTarget()");
            StringAssert.Contains(replay, "if (restoreStrategy != ReplayRestoreStrategy.VanillaEquivalentColdReplay)");
        }

        [TestMethod]
        public void ColdJournalReanchorsOnlyAfterExactBoundaryAndPreservesSavedBundle()
        {
            var journal = ReadRuntimeSource("Playback", "RuntimeReplayJournal.cs");
            var reanchor = ExtractMethod(journal, "internal void ReanchorColdBaseline(",
                "private void EstablishBaseline(");
            StringAssert.Contains(reanchor, "saved.RecordingOrigin.CompareBoundary(");
            StringAssert.Contains(reanchor, "saved.BaselineSemanticSha256, capture.Sha256");
            StringAssert.Contains(reanchor, "EstablishBaseline(capture, saved)");
            Assert.IsTrue(reanchor.IndexOf("coldRecordingRootVerified = true", StringComparison.Ordinal)
                > reanchor.IndexOf("Cold journal origin could not be established.", StringComparison.Ordinal));
            StringAssert.Contains(journal, "public string RecordingOriginStatus => sourceRecordingRootError.Length == 0");
            StringAssert.Contains(journal, "? (IsAvailable ? \"Ready\" : \"Faulted\")");
            var invalidate = ExtractMethod(journal, "private void InvalidateSourceRecordingRootForLoad()",
                "private void ReleaseSourcePreparationInput()");
            StringAssert.Contains(invalidate, "var hadVerifiedColdRoot = coldRecordingRootVerified;");
            StringAssert.Contains(invalidate, "!hadVerifiedColdRoot && (sourceRecordingRoot == null");
            Assert.IsTrue(invalidate.IndexOf("var hadVerifiedColdRoot", StringComparison.Ordinal)
                < invalidate.IndexOf("coldRecordingRootVerified = false", StringComparison.Ordinal));
            Assert.IsTrue(invalidate.IndexOf("coldRecordingRootVerified = false", StringComparison.Ordinal)
                < invalidate.IndexOf("if (sourceRecordingRootError.Length", StringComparison.Ordinal));
            var status = ExtractMethod(journal, "public string RecordingOriginStatus =>",
                "public string RecordingOriginDetail =>");
            Assert.IsTrue(status.IndexOf("sourceRecordingRootError.Length", StringComparison.Ordinal)
                < status.IndexOf("sourceRecordingRoot == null", StringComparison.Ordinal));
            StringAssert.Contains(journal, "replayBaseline?.BaselineId");
            StringAssert.Contains(journal, "BaselineCaptureResult.Succeeded(replayBaseline, BaselineBundleCodec.Serialize(replayBaseline))");
            var establish = ExtractMethod(journal, "private void EstablishBaseline(",
                "private void OnInputManagerUpdated(");
            StringAssert.Contains(establish, "journal = new ReplayJournal(baselineId, capture.Sha256!)");
            StringAssert.Contains(establish, "coldRecordingRootVerified = false");
            StringAssert.Contains(establish, "persistedSegments.Clear()");
            StringAssert.Contains(establish, "ReplayJournalSegment.GenesisPreviousSha256");
            var restore = ReadRuntimeSource("ReplaySave", "RuntimeReplayRestoreCoordinator.cs");
            var align = ExtractMethod(restore, "private void AdvanceBaselineAlignment()",
                "private bool AdvanceColdRecordingRoot()");
            Assert.IsTrue(align.IndexOf("journal.ReanchorColdBaseline", StringComparison.Ordinal)
                < align.IndexOf("EnterColdBaselineBoundary()", StringComparison.Ordinal));
        }

        [TestMethod]
        public void PlaybackToRawRecordingInheritsLastCommittedHeldState()
        {
            var journal = ReadRuntimeSource("Playback", "RuntimeReplayJournal.cs");
            var end = ExtractMethod(journal, "public void EndPlaybackCapture()",
                "public bool RecordForcedSceneTransition(");
            StringAssert.Contains(end, "recorder.Reset(LastCommittedSample?.Held ?? TasAction.None)");
            Assert.IsTrue(end.IndexOf("recorder.Reset(", StringComparison.Ordinal)
                < end.IndexOf("playbackCaptureActive = false", StringComparison.Ordinal));
            var recorder = ReadRuntimeSource("Playback", "HeroActionRecorder.cs");
            StringAssert.Contains(recorder, "Reset(TasAction initialHeld = TasAction.None)");
            StringAssert.Contains(recorder, "previousHeld = initialHeld;");
            Assert.IsFalse(end.Contains(".inputActions"));
        }

        [TestMethod]
        public void ColdBoundaryAllowsReadOnlyRootAttestationBeforeRelease()
        {
            var dispatcher = ReadRuntimeSource("Ipc", "RuntimeCommandDispatcher.cs");
            var allowed = ExtractMethod(dispatcher, "private bool IsAllowedColdBoundaryCommand(string messageType)",
                "private void OnWatchFrame(");
            StringAssert.Contains(allowed, "messageType == IpcMessageTypes.RequestStartupProfileAttestation");
            Assert.IsTrue(allowed.IndexOf("RequestStartupProfileAttestation", StringComparison.Ordinal)
                < allowed.IndexOf("ReplayRestorePhase.BaselineReady", StringComparison.Ordinal));
            Assert.IsFalse(allowed.Contains("IpcMessageTypes.Step"));
            Assert.IsFalse(allowed.Contains("IpcMessageTypes.CommitStateMutation"));
            var handler = ExtractMethod(dispatcher, "case IpcMessageTypes.RequestStartupProfileAttestation:",
                "case IpcMessageTypes.ReportNativeEvidence:");
            StringAssert.Contains(handler, "startupAttestor.Capture().ToFields(");
            Assert.IsFalse(handler.Contains("Resume("));
            Assert.IsFalse(handler.Contains("Step("));
        }

        [TestMethod]
        public void InitialRespawnPreparationNeverInsertsFramesIntoNativeRoutine()
        {
            var source = ReadRuntimeSource("Companion", "RuntimeInitialRespawnPreparation.cs");
            Assert.IsTrue(source.IndexOf("BeginRecordingClockCalibration", StringComparison.Ordinal)
                < source.IndexOf("var routine = original(hero)", StringComparison.Ordinal));
            var arm = ExtractMethod(source, "internal void Arm()", "private IEnumerator OnRespawn(");
            StringAssert.Contains(arm, "BeginRecordingClockCalibration");
            StringAssert.Contains(arm, "if (controller == null) return;");
            StringAssert.Contains(arm, "original(manager);");
            StringAssert.Contains(source, "On.GameManager.Update -= OnGameManagerUpdate;");
            var respawn = ExtractMethod(source, "private IEnumerator OnRespawn(", "private static void Invoke(");
            Assert.IsFalse(respawn.Contains("BeginRecordingClockCalibration"));
            Assert.IsFalse(respawn.Contains("yield return null"));
            Assert.IsFalse(respawn.Contains("for ("));
            StringAssert.Contains(source, "public int InsertedWaitFrames => 0;");
            StringAssert.Contains(source, "CancelRecordingClockCalibration");
            StringAssert.Contains(source, "while (routine.MoveNext()) yield return routine.Current");
            StringAssert.Contains(source, "(routine as IDisposable)?.Dispose()");
            StringAssert.Contains(source, "armed && !consumed");
            StringAssert.Contains(source, "var preparingThisRespawn = armed && !consumed;");
            StringAssert.Contains(respawn, "the native coroutine was not delayed");
            StringAssert.Contains(respawn, "EndPreparation();");
            StringAssert.Contains(source, "if (preparingThisRespawn) NativeStartFrame = Time.frameCount;");
            StringAssert.Contains(source, "if (preparingThisRespawn) nativeCompleted = true;");
            var dispatcher = ReadRuntimeSource("Ipc", "RuntimeCommandDispatcher.cs");
            foreach (var field in new[] { "initialRespawnPreparationStatus", "initialRespawnPreparationError",
                "initialRespawnInsertedWaitFrames", "initialRespawnPreparationStartFrame", "initialRespawnNativeStartFrame" })
                StringAssert.Contains(dispatcher, "fields[\"" + field + "\"]");
            StringAssert.Contains(source, "On.HeroController.Respawn -= OnRespawn");
            Assert.IsFalse(source.Contains("hero.transform"));
            Assert.IsFalse(source.Contains("hero.rb2d"));
            Assert.IsFalse(source.Contains("SendEvent("));
        }

        [TestMethod]
        public void InitialRespawnPreparationIsSharedAndRestoreFailsClosed()
        {
            var journal = ReadRuntimeSource("Playback", "RuntimeReplayJournal.cs");
            var restore = ReadRuntimeSource("ReplaySave", "RuntimeReplayRestoreCoordinator.cs");
            StringAssert.Contains(journal, "if (sourceRecordingRoot != null) PrepareInitialRespawn();");
            var begin = ExtractMethod(restore, "private void BeginBaselineLoad(GameManager manager)", "private void AdvanceLoad()");
            StringAssert.Contains(begin, "RestoreBaseline.RecordingOrigin != null");
            Assert.IsTrue(begin.IndexOf("journal.PrepareInitialRespawn()", StringComparison.Ordinal)
                < begin.IndexOf("manager.LoadGame(", StringComparison.Ordinal));
            StringAssert.Contains(restore, "Fail(ReplaySaveStatus.Failed, journal.InitialRespawnPreparationError)");
        }

        [TestMethod]
        public void RestoreFailureIsBoundedAndDurableBeforePublishingFailure()
        {
            var journal = ReadRuntimeSource("Playback", "RuntimeReplayJournal.cs");
            var restore = ReadRuntimeSource("ReplaySave", "RuntimeReplayRestoreCoordinator.cs");
            var fail = ExtractMethod(restore, "private void Fail(ReplaySaveStatus failureStatus, string reason)",
                "private void CleanupFailedRestore()");
            Assert.IsTrue(fail.IndexOf("PersistRestoreFailure", StringComparison.Ordinal)
                < fail.IndexOf("phase = ReplayRestorePhase.Failed", StringComparison.Ordinal));
            StringAssert.Contains(journal, "last-restore-failure.txt");
            StringAssert.Contains(journal, "text.Length > 16384");
            StringAssert.Contains(journal, "stream.Flush(true)");
        }

        [TestMethod]
        public void StepQuotaDefersTimeScaleCloseUntilEndOfFrameGuard()
        {
            var source = ReadRuntimeSource(
                "Control",
                "RuntimePauseController.cs");
            var committed = ExtractMethod(
                source,
                "public void OnMovieTickCommitted(",
                "public void TriggerControlledFault(");
            var endGuard = ExtractMethod(
                source,
                "internal void OnEndOfFrameGuard()",
                "internal void OnApplicationFocus(");

            AssertDoesNotContain(committed, "strategy.Close(");
            AssertDoesNotContain(committed, "strategy?.Close(");
            StringAssert.Contains(committed, "step-close-deferred");
            StringAssert.Contains(endGuard, "EnforceClosedPauseLease(");
        }

        [TestMethod]
        public void CompletedFrameMovieTickStepPassesThroughVanillaSceneTransitions()
        {
            var pause = ReadRuntimeSource(
                "Control",
                "RuntimePauseController.cs");
            var controls = ReadRuntimeSource(
                "Companion",
                "RuntimeControlService.cs");
            var dispatcher = ReadRuntimeSource(
                "Ipc",
                "RuntimeCommandDispatcher.cs");
            var broker = ReadCompanionSource(
                "Automation",
                "AutomationBroker.cs");
            var update = ExtractMethod(
                pause,
                "internal void OnUpdate()",
                "internal void OnFixedUpdate()");
            var committed = ExtractMethod(
                pause,
                "public void OnMovieTickCommitted(",
                "public void TriggerControlledFault(");
            var sceneChanged = ExtractMethod(
                pause,
                "private void OnActiveSceneChanged(",
                "private string OnBeforeSceneLoad(");
            var beforeSceneLoad = ExtractMethod(
                pause,
                "private string OnBeforeSceneLoad(",
                "private bool CanBeginCompletedFrameSceneTransitionPassThrough()");
            var beginPassThrough = ExtractMethod(
                pause,
                "private bool CanBeginCompletedFrameSceneTransitionPassThrough()",
                "private bool IsCompletedFrameSceneTransitionPassThroughActive()");
            var activePassThrough = ExtractMethod(
                pause,
                "private bool IsCompletedFrameSceneTransitionPassThroughActive()",
                "private void EnforceClosedPauseLease(");
            var abort = ExtractMethod(
                pause,
                "private void Abort(",
                "private void EndVirtualClockPause()");

            StringAssert.Contains(
                beginPassThrough,
                "useCompletedFrameBoundaryGate");
            StringAssert.Contains(
                beginPassThrough,
                "machine.Mode == SimulationControlMode.Stepping");
            StringAssert.Contains(beginPassThrough, "machine.ActiveRequest");
            StringAssert.Contains(
                beginPassThrough,
                "request.Value.Boundary == StepBoundary.MovieTick");
            StringAssert.Contains(
                beforeSceneLoad,
                "CanBeginCompletedFrameSceneTransitionPassThrough()");
            StringAssert.Contains(
                beforeSceneLoad,
                "completedFrameSceneTransitionPassThroughActive = true;");
            StringAssert.Contains(
                beforeSceneLoad,
                "CompletedFrameSceneTransitionPassThroughCount++;");
            StringAssert.Contains(
                sceneChanged,
                "!IsCompletedFrameSceneTransitionPassThroughActive()");
            StringAssert.Contains(
                update,
                "!IsCompletedFrameSceneTransitionPassThroughActive()");
            StringAssert.Contains(
                activePassThrough,
                "completedFrameSceneTransitionPassThroughActive");
            StringAssert.Contains(
                activePassThrough,
                "machine.Mode == SimulationControlMode.Stepping");
            StringAssert.Contains(
                activePassThrough,
                "machine.Mode == SimulationControlMode.Paused");
            StringAssert.Contains(
                committed,
                "completedFrameSceneTransitionPassThroughActive = false;");
            StringAssert.Contains(
                abort,
                "completedFrameSceneTransitionPassThroughActive = false;");
            StringAssert.Contains(
                controls,
                "CompletedFrameSceneTransitionPassThroughActive");
            StringAssert.Contains(
                controls,
                "CompletedFrameSceneTransitionPassThroughCount");
            StringAssert.Contains(
                dispatcher,
                "sceneTransitionPassThroughActive");
            StringAssert.Contains(
                dispatcher,
                "sceneTransitionPassThroughCount");
            StringAssert.Contains(
                broker,
                "\"sceneTransitionPassThroughActive\"");
            StringAssert.Contains(
                broker,
                "\"sceneTransitionPassThroughCount\"");

            var transitionMethods = update
                                    + committed
                                    + sceneChanged
                                    + beforeSceneLoad
                                    + beginPassThrough
                                    + activePassThrough;
            AssertDoesNotContain(transitionMethods, "Time.timeScale =");
            AssertDoesNotContain(transitionMethods, "Physics2D.Simulate(");
            AssertDoesNotContain(transitionMethods, "transform.position =");
            AssertDoesNotContain(transitionMethods, "body.position =");
            AssertDoesNotContain(transitionMethods, "body.velocity =");
        }

        [TestMethod]
        public void PausedCompanionBlocksAtCompletedFrameBoundary()
        {
            var pause = ReadRuntimeSource(
                "Control",
                "RuntimePauseController.cs");
            var dispatcher = ReadRuntimeSource(
                "Ipc",
                "RuntimeCommandDispatcher.cs");
            var queue = ReadRuntimeSource(
                "Ipc",
                "RuntimeCommandQueue.cs");
            var endGuard = ExtractMethod(
                pause,
                "internal void OnEndOfFrameGuard()",
                "internal void OnApplicationFocus(");
            var leaseGuardStart = pause.IndexOf(
                "internal sealed class RuntimePauseLeaseGuard",
                StringComparison.Ordinal);
            Assert.IsTrue(
                leaseGuardStart >= 0,
                "Missing RuntimePauseLeaseGuard source.");
            var leaseGuard = pause.Substring(leaseGuardStart);
            var pump = ExtractMethod(
                dispatcher,
                "private void PumpPausedBoundaryCommands()",
                "private void Dispatch(");

            StringAssert.Contains(
                endGuard,
                "machine.CompletePause()");
            StringAssert.Contains(
                endGuard,
                "machine.Mode == SimulationControlMode.Paused");
            StringAssert.Contains(
                endGuard,
                "pausedBoundaryCommandPump();");
            var boundaryPublish = endGuard.IndexOf(
                "CompletedFrameBoundarySignal.Publish();",
                StringComparison.Ordinal);
            var boundaryPump = endGuard.IndexOf(
                "pausedBoundaryCommandPump();",
                StringComparison.Ordinal);
            Assert.IsTrue(boundaryPublish >= 0);
            Assert.IsTrue(boundaryPublish < boundaryPump);
            StringAssert.Contains(
                pump,
                "commands.WaitForActivity(");
            StringAssert.Contains(pump, "Dispatch(command);");
            StringAssert.Contains(queue, "activity.Set();");
            AssertDoesNotContain(endGuard, "transform.position =");
            AssertDoesNotContain(endGuard, "body.velocity =");
            StringAssert.Contains(leaseGuard, "new WaitForEndOfFrame()");
            StringAssert.Contains(
                leaseGuard,
                "yield return endOfFrame;");
            StringAssert.Contains(
                leaseGuard,
                "owner?.OnEndOfFrameGuard();");
            AssertDoesNotContain(leaseGuard, "private void LateUpdate()");
        }

        [TestMethod]
        public void ReferenceSamplingCommitsAtTheCompletedFrameBoundary()
        {
            var observer = ReadReferenceObserverSource(
                "HollowKnightTASReferenceObserverMod.cs");
            var signal = ReadCoreSource(
                "Control",
                "CompletedFrameBoundarySignal.cs");
            var lateUpdate = ExtractMethod(
                observer,
                "public void OnLateUpdate()",
                "public void OnApplicationQuit()");
            var completedFrame = ExtractMethod(
                observer,
                "private void OnCompletedFrameBoundary()",
                "private void WriteBaseline(");
            var runnerStart = observer.IndexOf(
                "internal sealed class ReferenceObserverCompletedFrameRunner",
                StringComparison.Ordinal);
            Assert.IsTrue(runnerStart >= 0);
            var runner = observer.Substring(runnerStart);

            AssertDoesNotContain(lateUpdate, "sampler.Capture(");
            StringAssert.Contains(lateUpdate, "ArmCompletedFrameCapture(");
            StringAssert.Contains(
                observer,
                "TryArmCaptureAfterCommittedHeroActions(");
            StringAssert.Contains(
                observer,
                "VanillaEquivalenceSampler.CaptureInput(actions!)");
            StringAssert.Contains(
                observer,
                "Committed Hero input sample is unavailable.");
            StringAssert.Contains(
                observer,
                "pendingCaptureVisualTick == visualTick");
            StringAssert.Contains(observer, "frameCapturePending = true;");
            StringAssert.Contains(completedFrame, "WriteBaseline(");
            StringAssert.Contains(completedFrame, "sampler.Capture(");
            StringAssert.Contains(
                observer,
                "builder.Append(\"{\\\"schemaVersion\\\":2,\\\"runId\\\":\");");
            StringAssert.Contains(observer, "\\\"timeRaw\\\"");
            StringAssert.Contains(observer, "\\\"fixedTimeRaw\\\"");
            StringAssert.Contains(
                observer,
                "CompletedFrameBoundarySignal.Reached +=");
            StringAssert.Contains(
                observer,
                "CompletedFrameBoundarySignal.Reached -=");
            StringAssert.Contains(runner, "new WaitForEndOfFrame()");
            StringAssert.Contains(runner, "yield return endOfFrame;");
            StringAssert.Contains(
                runner,
                "CompletedFrameBoundarySignal.Publish();");
            StringAssert.Contains(
                observer,
                "CompletedFrameBoundarySignal.BoundaryId");
            StringAssert.Contains(
                signal,
                "post-render-completed-frame-sampling-v1");
            AssertDoesNotContain(signal, "UnityEngine");
            AssertDoesNotContain(completedFrame, "transform.position =");
            AssertDoesNotContain(completedFrame, "body.position =");
            AssertDoesNotContain(completedFrame, "body.velocity =");
            AssertDoesNotContain(completedFrame, "Time.timeScale =");
            AssertDoesNotContain(completedFrame, "Physics2D.Simulate(");
        }

        [TestMethod]
        public void AbsoluteUnityTimeIsAComparableExecutionInput()
        {
            var sampler = ReadGameObservationSource(
                "VanillaEquivalenceSampler.cs");

            StringAssert.Contains(sampler, "var timeRaw = Time.time;");
            StringAssert.Contains(sampler, "var fixedTimeRaw = Time.fixedTime;");
            StringAssert.Contains(sampler, "\"time.raw\", timeRaw");
            StringAssert.Contains(sampler, "\"time.fixedRaw\", fixedTimeRaw");
            StringAssert.Contains(sampler, "timeRaw - fixedTimeRaw");
            AssertDoesNotContain(
                sampler,
                "\"time.raw\", timeRaw, comparable: false");
            AssertDoesNotContain(
                sampler,
                "\"time.fixedRaw\", fixedTimeRaw, comparable: false");
        }

        [TestMethod]
        public void CompanionBoundaryGateDoesNotToggleUnityTimeScale()
        {
            var pause = ReadRuntimeSource(
                "Control",
                "RuntimePauseController.cs");
            var controls = ReadRuntimeSource(
                "Companion",
                "RuntimeControlService.cs");
            var dispatcher = ReadRuntimeSource(
                "Ipc",
                "RuntimeCommandDispatcher.cs");
            var pauseMethod = ExtractMethod(
                pause,
                "public ControlResult Pause()",
                "public ControlResult Step(");
            var stepMethod = ExtractMethod(
                pause,
                "public ControlResult Step(",
                "public ControlResult Resume()");
            var enforce = ExtractMethod(
                pause,
                "private void EnforceClosedPauseLease(",
                "private void Abort(");

            StringAssert.Contains(
                pause,
                "post-render-end-of-frame-boundary-v2");
            StringAssert.Contains(
                pauseMethod,
                "if (!useCompletedFrameBoundaryGate)");
            StringAssert.Contains(pauseMethod, "strategy.Close();");
            StringAssert.Contains(
                stepMethod,
                "if (!useCompletedFrameBoundaryGate)");
            StringAssert.Contains(stepMethod, "strategy.OpenStepWindow();");
            StringAssert.Contains(
                enforce,
                "if (useCompletedFrameBoundaryGate");
            StringAssert.Contains(
                controls,
                "UsesCompletedFrameBoundaryGate");
            StringAssert.Contains(
                dispatcher,
                "usesCompletedFrameBoundaryGate");
            StringAssert.Contains(dispatcher, "controlGateStrategyId");
            AssertDoesNotContain(pause, "Physics2D.Simulate(");
            AssertDoesNotContain(pause, "transform.position =");
            AssertDoesNotContain(pause, "body.position =");
        }

        [TestMethod]
        public void ExternalPhysicalInputHandshakeIsDormantAndReadOnly()
        {
            var source = ReadReferenceObserverSource(
                "HollowKnightTASReferenceObserverMod.cs");
            var earlyUpdate = ExtractMethod(
                source,
                "public void OnEarlyUpdate()",
                "public void OnEarlyFixedUpdate()");
            var inputManagerUpdate = ExtractMethod(
                source,
                "private void OnInputManagerUpdateInternal(",
                "private bool IsReferenceGameplayTickOpen()");
            var actionUpdate = ExtractMethod(
                source,
                "private void OnPlayerActionSetUpdate(",
                "private void OnListenForRightUpdate(");
            var lateUpdate = ExtractMethod(
                source,
                "public void OnLateUpdate()",
                "public void OnApplicationQuit()");
            var options = ReadReferenceObserverSource(
                "ReferenceObserverOptions.cs");
            var reference = ReadScript("Invoke-T24ReferenceSmoke.ps1");

            StringAssert.Contains(
                earlyUpdate,
                "inputSyncStart?.WaitOne(0) == true");
            StringAssert.Contains(
                inputManagerUpdate,
                "&& inputSyncActive");
            StringAssert.Contains(
                inputManagerUpdate,
                "&& ready");
            StringAssert.Contains(
                inputManagerUpdate,
                "!inputSyncHandshakeCompletedThisVisualUpdate");
            StringAssert.Contains(
                inputManagerUpdate,
                "inputSyncFrameReady?.Set();");
            StringAssert.Contains(
                inputManagerUpdate,
                "inputSyncApplied.WaitOne(");
            StringAssert.Contains(
                lateUpdate,
                "options.RequireExternalInputSync");
            StringAssert.Contains(
                lateUpdate,
                "!inputSyncCaptureEnabled");
            StringAssert.Contains(
                options,
                "--hktas-reference-require-external-input-sync");
            StringAssert.Contains(options, "case \"tas-manual-ui\":");
            StringAssert.Contains(
                reference,
                "$arguments += '--hktas-reference-require-external-input-sync'");
            StringAssert.Contains(
                reference,
                "externalInputSynchronizationRequired");
            StringAssert.Contains(
                actionUpdate,
                "original(self, updateTick, deltaTime);");
            AssertDoesNotContain(source, "SendInput(");
            AssertDoesNotContain(source, "keybd_event(");
            AssertDoesNotContain(source, "Random.InitState(");
            AssertDoesNotContain(source, "transform.position =");
            AssertDoesNotContain(source, "body.position =");
            AssertDoesNotContain(source, "body.velocity =");
            AssertDoesNotContain(source, "Time.timeScale =");
        }

        [TestMethod]
        public void FixtureReadinessUsesSemanticIdleBoundaryNotTransformBits()
        {
            var source = ReadReferenceObserverSource(
                "HollowKnightTASReferenceObserverMod.cs");
            var readiness = ExtractMethod(
                source,
                "private void ObserveFixtureReadiness()",
                "private void WriteBaseline(");

            StringAssert.Contains(readiness, "hero-accepting-input");
            StringAssert.Contains(readiness, "hero-control-not-relinquished");
            StringAssert.Contains(readiness, "hero-animation-control-enabled");
            StringAssert.Contains(readiness, "rigidbody-moving");
            StringAssert.Contains(
                readiness,
                "stabilizing-semantic-fixture");
            StringAssert.Contains(
                source,
                "RequiredSemanticStableUpdates = 10");
            StringAssert.Contains(
                source,
                "fixtureReadinessBoundary");
            StringAssert.Contains(
                source,
                "fixtureReadinessRequiredUpdates");
            StringAssert.Contains(
                source,
                "RequiredAbsoluteTimeTarget = 512f");
            StringAssert.Contains(
                readiness,
                "waiting-for-absolute-time-target");
            StringAssert.Contains(
                readiness,
                "absolute-time-target-missed");
            AssertDoesNotContain(readiness, "position != stablePosition");
            AssertDoesNotContain(
                readiness,
                "rigidbodyPosition != stableRigidbodyPosition");
            AssertDoesNotContain(
                readiness,
                "hero-rigidbody-position-not-synchronized");
            AssertDoesNotContain(readiness, "Float32BitsEqual(");
            AssertDoesNotContain(readiness, "transform.position =");
            AssertDoesNotContain(readiness, "body.velocity =");
            AssertDoesNotContain(readiness, "Time.time =");
            AssertDoesNotContain(readiness, "Time.fixedTime =");
        }

        [TestMethod]
        public void RecordingStartsOnlyAfterExactAbsoluteTimeHostArm()
        {
            var source = ReadReferenceObserverSource(
                "HollowKnightTASReferenceObserverMod.cs");
            var sampler = ReadGameObservationSource(
                "VanillaEquivalenceSampler.cs");
            var lateUpdate = ExtractMethod(
                source,
                "public void OnLateUpdate()",
                "public void OnApplicationQuit()");
            var reference = ReadScript("Invoke-T24ReferenceSmoke.ps1");
            var envelope = ReadScript("Measure-T24ReferenceEnvelope.ps1");
            var synchronizedInput = ExtractMethod(
                reference,
                "function Invoke-SynchronizedPhysicalRegressionInput {",
                "function Get-CanonicalPhysicalControlTimeline {");

            StringAssert.Contains(
                source,
                "RequiredRecordingAbsoluteTimeTarget = 768f");
            StringAssert.Contains(
                source,
                "HollowKnightTAS.T24.RecordingArm.");
            StringAssert.Contains(source, "Time.timeAsDouble");
            StringAssert.Contains(source, "Time.fixedTimeAsDouble");
            StringAssert.Contains(
                source,
                "Time.realtimeSinceStartupAsDouble");
            StringAssert.Contains(
                sampler,
                "diagnostic.time.rawDoubleBits");
            StringAssert.Contains(
                lateUpdate,
                "waiting-for-recording-time-target");
            StringAssert.Contains(
                lateUpdate,
                "recording-absolute-time-target-missed");
            StringAssert.Contains(
                lateUpdate,
                "recording-fixed-time-target-mismatch");
            StringAssert.Contains(
                lateUpdate,
                "waiting-for-recording-arm-release");
            StringAssert.Contains(
                lateUpdate,
                "recordingArmRelease.WaitOne(");
            StringAssert.Contains(
                lateUpdate,
                "recordingArmReleased = true;");
            Assert.IsTrue(
                lateUpdate.IndexOf(
                    "recordingArmReleased = true;",
                    StringComparison.Ordinal)
                < lateUpdate.IndexOf(
                    "options.RequireExternalInputSync",
                    StringComparison.Ordinal));
            AssertDoesNotContain(lateUpdate, "Time.time =");
            AssertDoesNotContain(lateUpdate, "Time.fixedTime =");
            AssertDoesNotContain(lateUpdate, "transform.position =");
            AssertDoesNotContain(lateUpdate, "body.velocity =");

            var release = synchronizedInput.IndexOf(
                "$recordingArmRelease.Set()",
                StringComparison.Ordinal);
            var inputStart = synchronizedInput.IndexOf(
                "$inputSyncStart.Set()",
                release,
                StringComparison.Ordinal);
            Assert.IsTrue(release >= 0);
            Assert.IsTrue(release < inputStart);
            StringAssert.Contains(
                reference,
                "exact-absolute-time-post-root-request-first-global-phase-host-release-v11");
            StringAssert.Contains(
                envelope,
                "-eq 'waiting-for-recording-arm-release'");
            AssertDoesNotContain(
                envelope,
                "$clockProfile.phase -eq 'ready-for-input'");
        }

        [TestMethod]
        public void T24EvidenceChainUsesStrictHashCompleteReferenceCohort()
        {
            var matrix = ReadScript("Invoke-T24ReferenceColdMatrix.ps1");
            var reference = ReadScript("Invoke-T24ReferenceSmoke.ps1");
            var envelope = ReadScript("Measure-T24ReferenceEnvelope.ps1");
            var candidate = ReadScript("Invoke-T24CandidateSmoke.ps1");

            StringAssert.Contains(matrix, "strictFirstAttemptCohort = $true");
            StringAssert.Contains(matrix, "phaseSha256");
            StringAssert.Contains(matrix, "resultSha256");
            StringAssert.Contains(matrix, "matrixGeneratorScriptSha256");
            StringAssert.Contains(matrix, "captureScriptSha256");
            StringAssert.Contains(
                reference,
                "[ValidateRange(60, 300)]");
            StringAssert.Contains(
                reference,
                "[ValidateRange(5, 300)][int]$TimeoutSeconds");
            StringAssert.Contains(envelope, "schemaVersion -ne 2");
            StringAssert.Contains(envelope, "actualPhaseHash");
            StringAssert.Contains(envelope, "actualResultHash");
            var measurementSignature = ExtractMethod(
                envelope,
                "function Get-AuthoritativeBaselineSignature {",
                "function Get-AssemblyFingerprint {");
            StringAssert.Contains(measurementSignature, "heroPositionX");
            StringAssert.Contains(measurementSignature, "heroPositionY");
            AssertDoesNotContain(
                measurementSignature,
                "rigidbodyPositionX");
            AssertDoesNotContain(
                measurementSignature,
                "rigidbodyPositionY");
            StringAssert.Contains(envelope, "$exactGroups |");
            StringAssert.Contains(
                envelope,
                "measurementCohortExcludesOnlyHeroRenderTransformXY = $true");
            StringAssert.Contains(candidate, "sourceMatrix.strictFirstAttemptCohort");
            StringAssert.Contains(candidate, "expectedAttempt.phaseSha256");
            StringAssert.Contains(candidate, "expectedAttempt.resultSha256");
            StringAssert.Contains(candidate, "referenceBaselineCatalogMatch");
        }

        [TestMethod]
        public void T24TraceConsumersRecomputeEveryFrameHash()
        {
            var envelope = ReadScript("Measure-T24ReferenceEnvelope.ps1");
            var candidate = ReadScript("Invoke-T24CandidateSmoke.ps1");

            StringAssert.Contains(envelope, "Read-T24ValidatedTrace");
            StringAssert.Contains(candidate, "Assert-T24TraceContract");
            StringAssert.Contains(envelope, "frame['sequence']");
            StringAssert.Contains(envelope, "frame['logicalTick']");
            StringAssert.Contains(candidate, "frame.sequence");
            StringAssert.Contains(candidate, "frame.logicalTick");
            foreach (var source in new[] { envelope, candidate })
            {
                StringAssert.Contains(source, "StringComparer]::Ordinal");
                StringAssert.Contains(source, "fieldMap.TryAdd");
                StringAssert.Contains(
                    source,
                    "comparison SHA-256 does not match its fields");
            }
        }

        [TestMethod]
        public void T24ReferenceEnvelopeCompactsTracesWithoutDroppingExactFields()
        {
            var envelope = ReadScript("Measure-T24ReferenceEnvelope.ps1");
            var traceReader = ExtractMethod(
                envelope,
                "function Read-T24ValidatedTrace {",
                "function Get-CommonInputPrefix {");
            var toleranceCandidates = ExtractMethod(
                envelope,
                "$toleranceCandidateKeys = @(",
                "$inputKeys = @(");

            AssertDoesNotContain(traceReader, "[IO.File]::ReadAllLines($Path)");
            StringAssert.Contains(traceReader, "[IO.File]::ReadLines($Path)");
            StringAssert.Contains(traceReader, "authoritativeFrameId");
            StringAssert.Contains(
                envelope,
                "SHA-256 collision while interning authoritative trace fields");
            StringAssert.Contains(
                envelope,
                "[int]$leftFrame.authoritativeFrameId");
            StringAssert.Contains(
                envelope,
                "foreach ($key in $toleranceCandidateKeys)");
            AssertDoesNotContain(
                toleranceCandidates,
                "'time.unscaledDeltaTime'");
            AssertDoesNotContain(
                toleranceCandidates,
                "'time.unscaledRelative'");
            StringAssert.Contains(
                envelope,
                "fixtureReadinessMaximumPhaseErrorFractionOfFixedStep");
            StringAssert.Contains(
                envelope,
                "externalVirtualClockPauseCount");
            StringAssert.Contains(
                envelope,
                "Pure vanilla runs differ in a field that must remain bitwise exact");
        }

        [TestMethod]
        public void CandidateCleanupMergesPartialMovesAndAuditsRestoration()
        {
            var candidate = ReadScript("Invoke-T24CandidateSmoke.ps1");
            var matrix = ReadScript("Invoke-T24CandidateColdMatrix.ps1");
            var isolation = ReadScript("T24FilesystemIsolation.ps1");
            var selfTest = ReadScript("Test-T24FilesystemIsolation.ps1");
            var clockContract = ExtractMethod(
                candidate,
                "function Get-T24ClockContractSha256 {",
                "function Convert-T24Float32Hex {");
            var helperDiscovery = ExtractMethod(
                candidate,
                "function Get-T24CandidateRunHelperProcesses {",
                "function Close-RunProcesses {");

            StringAssert.Contains(isolation, "function Move-T24PathNode");
            StringAssert.Contains(isolation, "Get-FileHash");
            StringAssert.Contains(isolation, "found conflicting files");
            StringAssert.Contains(isolation, "do not follow reparse points");
            StringAssert.Contains(isolation, "Start-Sleep -Milliseconds 100");
            StringAssert.Contains(
                candidate,
                ". $filesystemIsolationScriptPath");
            StringAssert.Contains(
                candidate,
                "-Source $isolatedTas");
            StringAssert.Contains(
                candidate,
                "-Source $modsBackup");
            StringAssert.Contains(
                candidate,
                "-Source $automationBackup");
            StringAssert.Contains(candidate, "cleanup-audit.json");
            StringAssert.Contains(candidate, "modsTopLevelEquivalent");
            StringAssert.Contains(candidate, "tasInstallEquivalent");
            StringAssert.Contains(candidate, "nestedTasDirectoryCount");
            StringAssert.Contains(candidate, "deferred-pause-arm-state.json");
            StringAssert.Contains(candidate, "$pauseArmChecks.Values");
            StringAssert.Contains(clockContract, "startupPolicy");
            StringAssert.Contains(candidate, "$script:beforeHelperPids");
            StringAssert.Contains(helperDiscovery, "$process.Path");
            StringAssert.Contains(helperDiscovery, "$tasInstallPrefix");
            StringAssert.Contains(
                helperDiscovery,
                "outside the isolated TAS ");
            StringAssert.Contains(helperDiscovery, "install: $processFull");
            StringAssert.Contains(matrix, "cleanupAuditSha256");
            StringAssert.Contains(matrix, "cleanupAudit.verdict");
            StringAssert.Contains(selfTest, "partial-destination");
            StringAssert.Contains(selfTest, "conflicting-file");
        }

        [TestMethod]
        public void SupplementalReferenceCatalogRemainsReadOnlyAndExact()
        {
            var generator = ReadScript(
                "New-T24SupplementalReferenceCatalog.ps1");
            var candidate = ReadScript("Invoke-T24CandidateSmoke.ps1");
            var matrix = ReadScript("Invoke-T24CandidateColdMatrix.ps1");

            StringAssert.Contains(
                generator,
                "complete-source-matrix-exact-recording-phase-readiness-input-phase-contract-v6");
            StringAssert.Contains(
                generator,
                "t24-supplemental-reference-catalog-v7-exact-recording-phase");
            StringAssert.Contains(generator, "$SourceMatrixPath");
            StringAssert.Contains(
                generator,
                "t24-vanilla-synchronized-negative-control-v13-exact-post-root-request-first-phase");
            StringAssert.Contains(
                generator,
                "physicalInputSynchronizationPrimeFrames");
            StringAssert.Contains(
                generator,
                "fixtureReadinessMaximumPhaseErrorFractionOfFixedStep");
            StringAssert.Contains(
                generator,
                "recordingPhaseContractId");
            StringAssert.Contains(
                generator,
                "recordingPhaseNormalizationMaximumHoldFrames");
            StringAssert.Contains(
                generator,
                "Test-T24RecordingPhaseContract");
            StringAssert.Contains(
                generator,
                "fixture-readiness-phase-outside-contract");
            StringAssert.Contains(
                generator,
                "primary frame-clock fields must remain bitwise exact");
            StringAssert.Contains(generator, "$rejectedMembers");
            StringAssert.Contains(
                generator,
                "captured-input-timeline-mismatch");
            StringAssert.Contains(candidate, "$catalog.rejectedMembers");
            StringAssert.Contains(
                candidate,
                "Test-T24SupplementalReadinessPhase");
            StringAssert.Contains(
                candidate,
                "Test-T24RecordingPhaseContract");
            StringAssert.Contains(
                candidate,
                "A rejected supplemental readiness profile changed.");
            StringAssert.Contains(
                candidate,
                "source ordinals are not exhaustive");
            StringAssert.Contains(generator, "Read-T24ValidatedTrace");
            StringAssert.Contains(generator, "Get-CommonInputPrefix");
            StringAssert.Contains(generator, "Get-OrAddCanonicalValueId");
            StringAssert.Contains(generator, "Get-OrAddAuthoritativeFrameId");
            StringAssert.Contains(generator, "Get-OrAddTraceSchema");
            StringAssert.Contains(generator, "$toleranceCandidateKeySet");
            StringAssert.Contains(generator, "$script:traceSchemaIds");
            StringAssert.Contains(generator, "$script:canonicalValueIds");
            StringAssert.Contains(generator, "$script:authoritativeFrameIds");
            StringAssert.Contains(generator, "inputInjected");
            StringAssert.Contains(generator, "timeWritten");
            StringAssert.Contains(generator, "gameplayStateWritten");
            StringAssert.Contains(
                generator,
                "pre-clock-menu-dwell.json");
            StringAssert.Contains(
                generator,
                "wall-clock-wait-in-menu-title-v1");
            StringAssert.Contains(
                generator,
                "preClockMenuDwellAuditSha256");
            StringAssert.Contains(
                candidate,
                "Import-T24SupplementalReferenceCatalog");
            StringAssert.Contains(
                candidate,
                "Get-T24BaselineSignature -Path $CandidateBaselinePath");
            StringAssert.Contains(
                candidate,
                "Candidate fixture baseline has no exact no-Mod catalog match.");
            StringAssert.Contains(candidate, "gameplayStarted = $false");
            Assert.IsTrue(
                candidate.LastIndexOf(
                    "$matchedReference = Resolve-T24ReferenceForBaseline",
                    StringComparison.Ordinal)
                < candidate.IndexOf(
                    "$bootstrapWaitStartedUtc = [DateTimeOffset]::UtcNow",
                    StringComparison.Ordinal),
                "Live candidate baseline matching must precede SDK control and replay.");
            StringAssert.Contains(
                candidate,
                "authoritativePhysicsRemainsBitwiseExact");
            StringAssert.Contains(
                matrix,
                "-SupplementalReferenceCatalogPath");
            AssertDoesNotContain(generator, "transform.position =");
            AssertDoesNotContain(generator, "body.position =");
            AssertDoesNotContain(generator, "body.velocity =");
            AssertDoesNotContain(generator, "Time.timeScale =");
        }

        [TestMethod]
        public void MenuTitleDwellNegativeControlIsPrecommittedReadOnlyAndUnfiltered()
        {
            var reference = ReadScript("Invoke-T24ReferenceSmoke.ps1");
            var matrix = ReadScript(
                "Invoke-T24ReferencePreludeDwellMatrix.ps1");
            var root = FindRepositoryRoot();
            var schedulePath = Path.Combine(
                root,
                "fixtures",
                "t24",
                "t24.menu-title-preclock-dwell-grid.v1.json");
            Assert.IsTrue(File.Exists(schedulePath));
            var schedule = File.ReadAllText(schedulePath);

            StringAssert.Contains(
                schedule,
                "all-scheduled-points-no-filter");
            StringAssert.Contains(schedule, "\"requiredRuns\": 12");
            StringAssert.Contains(
                matrix,
                "Write-DwellMatrixArtifact -Verdict 'PRECOMMITTED'");
            StringAssert.Contains(
                matrix,
                "selectionPolicy = 'all-scheduled-points-no-filter'");
            StringAssert.Contains(
                matrix,
                "stoppedAtBaselineMatch = $false");
            StringAssert.Contains(
                matrix,
                "$index -lt $dwellTargets.Count");
            StringAssert.Contains(matrix, "tasRuntimeAbsent");
            StringAssert.Contains(matrix, "observerOnlyManagedMod");
            StringAssert.Contains(matrix, "inputInjected");
            StringAssert.Contains(matrix, "timeWritten");
            StringAssert.Contains(matrix, "gameplayStateWritten");
            StringAssert.Contains(matrix, "visualRecognitionUsed");
            StringAssert.Contains(
                reference,
                "wall-clock-wait-in-menu-title-v1");
            StringAssert.Contains(
                reference,
                "Start-Sleep -Milliseconds $PreClockMenuDwellMilliseconds");
            AssertDoesNotContain(schedule, "42146709");
            AssertDoesNotContain(matrix, "42146709");
            AssertDoesNotContain(matrix, "transform.position =");
            AssertDoesNotContain(matrix, "body.position =");
            AssertDoesNotContain(matrix, "body.velocity =");
            AssertDoesNotContain(matrix, "Time.timeScale =");
        }

        [TestMethod]
        public void BaselineNormalizationRequiresObservedComponentsAndExactDeltas()
        {
            var candidate = ReadScript("Invoke-T24CandidateSmoke.ps1");
            var matrix = ReadScript("Invoke-T24CandidateColdMatrix.ps1");
            var catalogFailClosed = ReadScript(
                "Test-T24BaselineCatalogFailClosed.ps1");
            var heroZVanillaAudit = ReadScript(
                "Test-T24HeroZVanillaContract.ps1");
            var heroZAssetInspector = ReadScript(
                "Inspect-T24HeroSetZAsset.py");
            var strictSignature = ExtractMethod(
                candidate,
                "function Get-T24BaselineSignature {",
                "function Get-T24AuthoritativeBaselineSignature {");
            var authoritativeSignature = ExtractMethod(
                candidate,
                "function Get-T24AuthoritativeBaselineSignature {",
                "function Get-T24SemanticBaselineSignature {");
            var semanticSignature = ExtractMethod(
                candidate,
                "function Get-T24SemanticBaselineSignature {",
                "function Get-T24BaselineFloatHex {");
            var heroZContract = ExtractMethod(
                candidate,
                "function Test-T24BaselineHeroZVanillaContract {",
                "function Test-T24BaselineRenderTransformEnvelope {");
            var renderEnvelope = ExtractMethod(
                candidate,
                "function Test-T24BaselineRenderTransformEnvelope {",
                "function Test-T24BaselineRigidbodyComponentCatalog {");
            var componentCatalog = ExtractMethod(
                candidate,
                "function Test-T24BaselineRigidbodyComponentCatalog {",
                "function Resolve-T24ReferenceForBaseline {");
            var resolver = ExtractMethod(
                candidate,
                "function Resolve-T24ReferenceForBaseline {",
                "function Compare-T24Baselines {");
            var traceComparison = ExtractMethod(
                candidate,
                "function Compare-T24TracesWithEnvelope {",
                "function Close-RunProcesses {");
            var axisWitnessValidation = ExtractMethod(
                candidate,
                "function Test-T24RigidbodyAxisWitnessTrace {",
                "function Test-T24DerivedRenderDeltaNormalization {");

            StringAssert.Contains(authoritativeSignature, "heroPositionX");
            StringAssert.Contains(authoritativeSignature, "heroPositionY");
            StringAssert.Contains(authoritativeSignature, "heroPositionZ");
            AssertDoesNotContain(strictSignature, "heroPositionZ");
            AssertDoesNotContain(
                authoritativeSignature,
                "rigidbodyPositionX");
            AssertDoesNotContain(
                authoritativeSignature,
                "rigidbodyPositionY");
            StringAssert.Contains(semanticSignature, "heroPositionX");
            StringAssert.Contains(semanticSignature, "heroPositionY");
            StringAssert.Contains(semanticSignature, "heroPositionZ");
            StringAssert.Contains(semanticSignature, "rigidbodyPositionX");
            StringAssert.Contains(semanticSignature, "rigidbodyPositionY");
            StringAssert.Contains(
                heroZContract,
                "hero-z-vanilla-setz-random-v1");
            StringAssert.Contains(heroZContract, "3b83126f");
            StringAssert.Contains(heroZContract, "3ba3d634");
            StringAssert.Contains(heroZContract, "non-finite-float32");
            StringAssert.Contains(
                heroZContract,
                "outside-vanilla-setz-range");
            StringAssert.Contains(
                heroZContract,
                "Random.Range(z, z + 0.0009999f)");
            StringAssert.Contains(renderEnvelope, "hero.position.x");
            StringAssert.Contains(renderEnvelope, "hero.position.y");
            AssertDoesNotContain(renderEnvelope, "hero.rigidbody.position");
            StringAssert.Contains(
                componentCatalog,
                "$observedX -contains $candidateX");
            StringAssert.Contains(
                componentCatalog,
                "$observedY -contains $candidateY");
            StringAssert.Contains(
                componentCatalog,
                "foreach ($semanticReference in $semanticReferences)");
            StringAssert.Contains(
                componentCatalog,
                "T24 baseline reference catalog integrity failure");
            StringAssert.Contains(
                componentCatalog,
                "\\A[0-9a-f]{8}\\z");
            AssertDoesNotContain(
                componentCatalog,
                "$semanticReferences.rigidbodyPositionXCanonicalHex");
            AssertDoesNotContain(
                componentCatalog,
                "$semanticReferences.rigidbodyPositionYCanonicalHex");
            AssertDoesNotContain(componentCatalog, "maxAbsoluteDifference");
            AssertDoesNotContain(componentCatalog, "maxUlpDistance");
            StringAssert.Contains(
                catalogFailClosed,
                "$null -eq $emptyResolution");
            StringAssert.Contains(
                catalogFailClosed,
                "T24 Synthetic Semantic Miss");
            StringAssert.Contains(
                catalogFailClosed,
                "candidateHeroZVanillaVisualRandom");
            StringAssert.Contains(
                catalogFailClosed,
                "outOfRangeHeroZ");
            StringAssert.Contains(
                catalogFailClosed,
                "missingComponentRejected");
            StringAssert.Contains(
                catalogFailClosed,
                "malformedCanonicalHexRejected");
            StringAssert.Contains(
                catalogFailClosed,
                "knownEligibleBaseline");
            StringAssert.Contains(
                heroZVanillaAudit,
                "5944411bd93830369390a4b51766ee68c4ab26195b299e25a07b5e7d0e00086d");
            StringAssert.Contains(
                heroZVanillaAudit,
                "f2a9851f0424d94c0231b2689aeb51327fc7054f284bd77db2e3293d80b9386c");
            StringAssert.Contains(
                heroZVanillaAudit,
                "\\b(heroObject|hero)\\.transform\\.position\\.z\\b");
            StringAssert.Contains(
                heroZVanillaAudit,
                "heroZReadsExcludeGameplayTokens");
            StringAssert.Contains(
                heroZAssetInspector,
                "item.path_id == 3895");
            StringAssert.Contains(
                heroZAssetInspector,
                "item.path_id == 22445");
            StringAssert.Contains(
                heroZAssetInspector,
                "read_u32(raw, 32) == 0x3B83126F");
            StringAssert.Contains(
                heroZAssetInspector,
                "read_u32(raw, 44) == 0x3F000000");
            StringAssert.Contains(
                resolver,
                "authoritative-exact-render-envelope");
            StringAssert.Contains(
                resolver,
                "semantic-exact-rigidbody-components-observed-render-envelope");
            StringAssert.Contains(
                resolver,
                "rigidbodyPositionXCanonicalHex");
            StringAssert.Contains(
                resolver,
                "rigidbodyPositionYCanonicalHex");
            StringAssert.Contains(
                resolver,
                "rigidbodyXWitnessTraceSha256");
            StringAssert.Contains(
                resolver,
                "rigidbodyYWitnessTraceSha256");
            StringAssert.Contains(
                resolver,
                "heroZVanillaVisualRandomEquivalent");
            StringAssert.Contains(
                resolver,
                "heroZVanillaVisualRandomExact");
            StringAssert.Contains(
                resolver,
                "heroZVanillaVisualRandomPolicyId");
            StringAssert.Contains(
                resolver,
                "hero-z-vanilla-visual-random");
            StringAssert.Contains(
                traceComparison,
                "hero.rigidbody.positionDelta.x");
            StringAssert.Contains(
                traceComparison,
                "hero.rigidbody.positionDelta.y");
            StringAssert.Contains(
                axisWitnessValidation,
                "rigidbody-axis-witness-bitwise-mismatch");
            StringAssert.Contains(
                axisWitnessValidation,
                "$CandidateLines.Count -ne $WitnessLines.Count");
            StringAssert.Contains(
                traceComparison,
                "$axisFullTraceBitwiseExact");
            StringAssert.Contains(
                traceComparison,
                "Test-T24RigidbodyAxisWitnessTrace");
            StringAssert.Contains(
                traceComparison,
                "rigidbody-axis-witness-mismatch");
            StringAssert.Contains(traceComparison, "axisWitnesses");
            StringAssert.Contains(
                traceComparison,
                "each-rigidbody-axis-position-and-positionDelta-must-bitwise-match-a-full-no-mod-trace-with-the-same-baseline-component");
            StringAssert.Contains(
                matrix,
                "baselineSemanticExactEquivalent");
            StringAssert.Contains(
                matrix,
                "baselineHeroZVanillaVisualRandomEquivalent");
            StringAssert.Contains(
                matrix,
                "baselineHeroZVanillaVisualRandomExact");
            StringAssert.Contains(
                matrix,
                "baselineHeroZVanillaVisualRandomPolicyId");
            StringAssert.Contains(
                matrix,
                "baselineRigidbodyComponentsObserved");
            StringAssert.Contains(
                matrix,
                "baselineRenderTransformEnvelopeEquivalent");
            StringAssert.Contains(
                matrix,
                "baselineNormalizedRigidbodyEquivalent");
            StringAssert.Contains(matrix, "rigidbodyAxisWitnessesAvailable");
            StringAssert.Contains(matrix, "rigidbodyAxisWitnessEquivalent");
            StringAssert.Contains(matrix, "rigidbodyXWitnessTraceSha256");
            StringAssert.Contains(matrix, "rigidbodyYWitnessTraceSha256");
            StringAssert.Contains(matrix, "baselineEquivalent");
        }

        [TestMethod]
        public void RepresentationNormalizationsAreDerivedAndFailClosed()
        {
            var candidate = ReadScript("Invoke-T24CandidateSmoke.ps1");
            var negativeTests = ReadScript(
                "Test-T24RepresentationNormalization.ps1");
            var renderNormalization = ExtractMethod(
                candidate,
                "function Test-T24DerivedRenderDeltaNormalization {",
                "function Test-T24ProcessAgeClockResidualNormalization {");
            var clockNormalization = ExtractMethod(
                candidate,
                "function Test-T24ProcessAgeClockResidualNormalization {",
                "function Import-T24NegativeControlEnvelope {");
            var traceComparison = ExtractMethod(
                candidate,
                "function Compare-T24TracesWithEnvelope {",
                "function Close-RunProcesses {");

            StringAssert.Contains(
                renderNormalization,
                "recorded-render-delta-is-not-bitwise-derived");
            StringAssert.Contains(
                renderNormalization,
                "authoritative-rigidbody-axis-is-not-bitwise-exact");
            StringAssert.Contains(
                renderNormalization,
                "authoritative-rigidbody-axis-witness-does-not-match");
            StringAssert.Contains(
                renderNormalization,
                "full-no-mod-axis-witness");
            StringAssert.Contains(
                renderNormalization,
                "absolute-render-position-is-outside-no-mod-bound");
            StringAssert.Contains(
                clockNormalization,
                "raw-clock-distance-exceeds-one-ulp");
            StringAssert.Contains(
                clockNormalization,
                "recorded-clock-residual-is-not-bitwise-derived");
            StringAssert.Contains(
                clockNormalization,
                "logical-time-minus-fixed-is-nonzero");
            StringAssert.Contains(
                clockNormalization,
                "required-raw-clock-field-missing");
            StringAssert.Contains(
                traceComparison,
                "Test-T24DerivedRenderDeltaNormalization");
            StringAssert.Contains(
                traceComparison,
                "Test-T24ProcessAgeClockResidualNormalization");
            StringAssert.Contains(
                traceComparison,
                "conditionalAcceptanceRules");
            StringAssert.Contains(
                negativeTests,
                "authoritativeRigidbodyDivergence");
            StringAssert.Contains(
                negativeTests,
                "mixedRigidbodyAxisWitnessTrace");
            StringAssert.Contains(
                negativeTests,
                "authoritativeRigidbodyWitnessCurrentMismatch");
            StringAssert.Contains(negativeTests, "rawClockTwoUlp");
            StringAssert.Contains(negativeTests, "tamperedClockResidual");
            StringAssert.Contains(negativeTests, "logicalClockPhaseNonzero");
            StringAssert.Contains(negativeTests, "rawClockFieldMissing");
        }

        [TestMethod]
        public void CapturedCandidateOfflineRecomparisonIsHashBoundAndDiagnosticOnly()
        {
            var offline = ReadScript(
                "Test-T24CapturedCandidateOffline.ps1");

            StringAssert.Contains(
                offline,
                "Assert-T24RecordedHash");
            StringAssert.Contains(
                offline,
                "candidateTraceSha256");
            StringAssert.Contains(
                offline,
                "candidateBaselineSha256");
            StringAssert.Contains(
                offline,
                "referenceTraceSha256");
            StringAssert.Contains(
                offline,
                "referenceBaselineSha256");
            StringAssert.Contains(
                offline,
                "Compare-T24TracesWithEnvelope");
            StringAssert.Contains(
                offline,
                "Test-T24RigidbodyAxisWitnessTrace");
            StringAssert.Contains(
                offline,
                "OFFLINE_RECOMPUTED_DIAGNOSTIC");
            StringAssert.Contains(
                offline,
                "eligibleForFreshMatrix = $false");
            AssertDoesNotContain(offline, "Start-Process");
            AssertDoesNotContain(offline, "Stop-Process");
            AssertDoesNotContain(offline, "Time.timeScale");
            AssertDoesNotContain(offline, "Physics2D.Simulate");
        }

        [TestMethod]
        public void CandidateUsesStartupClockAndAtomicDeferredRecordingArm()
        {
            var candidate = ReadScript("Invoke-T24CandidateSmoke.ps1");
            var reference = ReadScript("Invoke-T24ReferenceSmoke.ps1");
            var pauseController = ReadRuntimeSource(
                "Control",
                "RuntimePauseController.cs");
            var controlService = ReadRuntimeSource(
                "Companion",
                "RuntimeControlService.cs");
            var recordingArmGate = ReadRuntimeSource(
                "Control",
                "T24DeferredRecordingArmGate.cs");
            var startupLaunch = candidate.LastIndexOf(
                "$startupLaunch = Start-T24StartupClockedGame",
                StringComparison.Ordinal);
            var lease = candidate.LastIndexOf(
                "$leaseId = Acquire-SdkLease",
                StringComparison.Ordinal);
            var validate = candidate.IndexOf(
                "-CommandId 'validateMoviePatch'",
                StringComparison.Ordinal);
            var propose = candidate.IndexOf(
                "-CommandId 'proposeMoviePatch'",
                StringComparison.Ordinal);
            var controlledDwell = candidate.LastIndexOf(
                "Start-Sleep -Seconds 3",
                StringComparison.Ordinal);
            var fixtureLoad = candidate.LastIndexOf(
                "Invoke-ExternalUiSlotLoad",
                StringComparison.Ordinal);
            var fixtureReady = candidate.LastIndexOf(
                "$fixtureReadyStatus = Wait-ObserverPhase",
                StringComparison.Ordinal);
            var bootstrapWait = candidate.LastIndexOf(
                "$bootstrapWaitStartedUtc = [DateTimeOffset]::UtcNow",
                StringComparison.Ordinal);
            var connect = candidate.LastIndexOf(
                "$client.ConnectAsync(",
                StringComparison.Ordinal);
            var pause = candidate.IndexOf(
                "-CommandId 'pause'",
                StringComparison.Ordinal);
            var apply = candidate.IndexOf(
                "-CommandId 'applyMovieBranch'",
                StringComparison.Ordinal);
            var start = candidate.IndexOf(
                "-CommandId 'startReplay'",
                StringComparison.Ordinal);
            var release = candidate.IndexOf(
                "$recordingArmEvidence = Complete-T24DeferredRecordingArm",
                start,
                StringComparison.Ordinal);
            var resume = candidate.IndexOf(
                "-CommandId 'resume'",
                StringComparison.Ordinal);

            Assert.IsTrue(startupLaunch >= 0);
            Assert.IsTrue(lease >= 0);
            Assert.IsTrue(validate >= 0);
            Assert.IsTrue(startupLaunch < controlledDwell);
            Assert.IsTrue(controlledDwell < fixtureLoad);
            Assert.IsTrue(fixtureLoad < fixtureReady);
            Assert.IsTrue(fixtureReady < bootstrapWait);
            Assert.IsTrue(bootstrapWait < connect);
            Assert.IsTrue(connect < lease);
            Assert.IsTrue(lease < pause);
            Assert.IsTrue(pause < validate);
            Assert.IsTrue(validate < propose);
            Assert.IsTrue(pause < apply);
            Assert.IsTrue(propose < apply);
            Assert.IsTrue(apply < start);
            Assert.IsTrue(start < release);
            Assert.IsTrue(release < resume);
            Assert.IsTrue(start < resume);
            AssertDoesNotContain(candidate, "Invoke-T24ClockInjection");
            AssertDoesNotContain(candidate, "'-applaunch'");
            AssertDoesNotContain(candidate, "$SteamExecutable");
            StringAssert.Contains(
                candidate,
                "--hktas-t24-deferred-recording-arm=$runId");
            StringAssert.Contains(
                candidate.Substring(start, release - start),
                "-ExpectedRuntimeMode 'Running'");
            StringAssert.Contains(
                candidate,
                "$recordingArmRelease.Set()");
            StringAssert.Contains(
                candidate,
                "existing-manual-reset-one-neutral-completed-frame-preroll-v2");
            StringAssert.Contains(
                candidate,
                "deferredRecordingArmNeutralPreRollCompletedFrameCount'] -ne 1");
            StringAssert.Contains(
                candidate,
                "$pausedState = Wait-SdkState");
            StringAssert.Contains(
                candidate,
                "$recordingArmControlClean = $null -ne $recordingArmEvidence");
            StringAssert.Contains(
                candidate,
                "-and $recordingArmControlClean `");
            StringAssert.Contains(
                candidate,
                "recordingArmAuditSha256 = $recordingArmAuditSha256");
            StringAssert.Contains(
                candidate,
                "externalDoublePhaseFinalResidualBits");
            StringAssert.Contains(
                candidate,
                "externalStartupClockPrimaryThreadMatched");
            var menuGate = candidate.LastIndexOf(
                "if ($null -ne $script:game",
                StringComparison.Ordinal);
            var menuGateAccepted = candidate.IndexOf(
                "$menuReadinessConfirmed = $true",
                menuGate,
                StringComparison.Ordinal);
            Assert.IsTrue(menuGate >= 0);
            Assert.IsTrue(menuGate < menuGateAccepted);
            var menuGateText = candidate.Substring(
                menuGate,
                menuGateAccepted - menuGate);
            StringAssert.Contains(
                menuGateText,
                "[string]$status.scene -eq 'Menu_Title'");
            StringAssert.Contains(
                menuGateText,
                "Test-ObserverMenuControlsReady -Status $status");
            StringAssert.Contains(
                candidate,
                "$Status.PSObject.Properties[$name]");
            StringAssert.Contains(
                reference,
                "$Status.PSObject.Properties[$name]");
            AssertDoesNotContain(menuGateText, "$bootstrapPath");
            AssertDoesNotContain(menuGateText, "Test-Path");
            AssertDoesNotContain(menuGateText, "ConnectAsync");
            StringAssert.Contains(
                candidate,
                "fixture-baseline-before-sdk-connect-v1");
            StringAssert.Contains(
                candidate,
                "menuReadinessRequiresBootstrap = $false");
            StringAssert.Contains(
                candidate,
                "startupOrderVerified = $startupOrderVerified");
            StringAssert.Contains(
                candidate,
                "startupOrderAuditSha256");
            var referenceClockInjection = reference.LastIndexOf(
                "$startupLaunch = Start-T24StartupClockedGame",
                StringComparison.Ordinal);
            var referenceControlledDwell = reference.LastIndexOf(
                "Start-Sleep -Seconds 3",
                StringComparison.Ordinal);
            var referenceFixtureLoad = reference.LastIndexOf(
                "Invoke-ExternalUiSlotLoad",
                StringComparison.Ordinal);
            Assert.IsTrue(referenceClockInjection >= 0);
            Assert.IsTrue(referenceClockInjection < referenceControlledDwell);
            Assert.IsTrue(referenceControlledDwell < referenceFixtureLoad);
            StringAssert.Contains(
                candidate.Substring(
                    resume,
                    Math.Min(500, candidate.Length - resume)),
                "-ExpectedRuntimeMode 'Paused'");
            StringAssert.Contains(
                recordingArmGate,
                "EventResetMode.ManualReset");
            StringAssert.Contains(
                recordingArmGate,
                "if (consumed || !active.WaitOne(0))");
            StringAssert.Contains(
                controlService,
                "return ArmDeferredPause();");
            StringAssert.Contains(
                controlService,
                "return ArmDeferredReplay();");
            StringAssert.Contains(
                controlService,
                "var pauseResult = activePause.Pause();");
            StringAssert.Contains(
                controlService,
                "var replayResult = StartReplayNow();");
            var releasePending = controlService.IndexOf(
                "if (!gate.ReleaseConsumed)",
                StringComparison.Ordinal);
            var consumeRelease = controlService.IndexOf(
                "if (!gate.TryConsumeRelease())",
                releasePending,
                StringComparison.Ordinal);
            var activationAttempt = controlService.IndexOf(
                "deferredActivationAttempted = true;",
                consumeRelease,
                StringComparison.Ordinal);
            var preRollCount = controlService.IndexOf(
                "deferredNeutralPreRollCompletedFrameCount++;",
                activationAttempt,
                StringComparison.Ordinal);
            Assert.IsTrue(releasePending >= 0);
            Assert.IsTrue(releasePending < consumeRelease);
            Assert.IsTrue(consumeRelease < activationAttempt);
            StringAssert.Contains(
                controlService.Substring(
                    consumeRelease,
                    activationAttempt - consumeRelease),
                "return;");
            Assert.IsTrue(activationAttempt < preRollCount);
            StringAssert.Contains(
                controlService,
                "deferredNeutralPreRollCompletedFrameCount != 1");
            var activation = pauseController.IndexOf(
                "completedFrameBoundaryActivation?.Invoke();",
                StringComparison.Ordinal);
            var completedFrame = pauseController.IndexOf(
                "CompletedFrameBoundarySignal.Publish();",
                activation,
                StringComparison.Ordinal);
            var completePause = pauseController.IndexOf(
                "machine.CompletePause();",
                completedFrame,
                StringComparison.Ordinal);
            Assert.IsTrue(activation >= 0);
            Assert.IsTrue(activation < completedFrame);
            Assert.IsTrue(completedFrame < completePause);
        }

        [TestMethod]
        public void CompanionControlHooksAreLazyAndDisconnectPreservesAnExistingPause()
        {
            var source = ReadRuntimeSource(
                "Companion",
                "RuntimeControlService.cs");
            var constructor = ExtractMethod(
                source,
                "public RuntimeControlService(",
                "public MovieDocument? Movie");
            var startReplay = ExtractMethod(
                source,
                "public PlaybackStartResult StartReplay()",
                "public PlaybackStopResult StopReplay()");
            var cleanup = ExtractMethod(
                source,
                "public void CleanupForDisconnect()",
                "public void Dispose()");
            var requirePause = ExtractMethod(
                source,
                "private RuntimePauseController RequirePause()",
                "private void ThrowIfDisposed()");

            AssertDoesNotContain(constructor, "CreateControllers();");
            StringAssert.Contains(startReplay, "EnsureControllers();");
            StringAssert.Contains(
                requirePause,
                "pause = CreatePauseController();");
            AssertDoesNotContain(
                requirePause,
                "CreatePlaybackController();");
            StringAssert.Contains(cleanup, "DisposeControllers();");
            AssertDoesNotContain(cleanup, "CreateControllers();");
            var pausedCleanup = ExtractMethod(cleanup.Replace("\r\n", "\n"),
                "if (pause?.Mode == SimulationControlMode.Paused)",
                "            try\n            {\n                if (playback?.Mode");
            StringAssert.Contains(pausedCleanup, "playback?.Dispose()");
            StringAssert.Contains(pausedCleanup, "EndJournalPlaybackCapture()");
            StringAssert.Contains(pausedCleanup, "companion-disconnected-pause-retained");
            foreach (var forbidden in new[] { "DisposeControllers()", "pause.Dispose", "pause?.Dispose",
                "adoptedRestoreSettingsLease", ".Resume(", ".Step(", "pause = null" })
                AssertDoesNotContain(pausedCleanup, forbidden);
        }

        [TestMethod]
        public void DisconnectInterruptsBatchOnlyAtCompletedFrameBeforeInputCleanup()
        {
            var control = ReadRuntimeSource("Companion", "RuntimeControlService.cs");
            var cleanup = ExtractMethod(control, "public void CleanupForDisconnect()", "public void Dispose()");
            var stepping = ExtractMethod(cleanup, "if (pause?.Mode == SimulationControlMode.Stepping)",
                "if (pause?.Mode == SimulationControlMode.Pausing)");
            StringAssert.Contains(stepping, "disconnectCleanupPending = true");
            StringAssert.Contains(stepping, "pause.InterruptStepAfterCurrentFrame");
            AssertDoesNotContain(stepping, "Dispose");
            var pump = ExtractMethod(control, "internal bool CompletePlaybackAtPausedBoundary()",
                "public ControlResult StartInputBatch(");
            StringAssert.Contains(pump, "ControlMode == SimulationControlMode.Paused && disconnectCleanupPending");
            StringAssert.Contains(pump, "CleanupForDisconnect()");
            var pause = ReadRuntimeSource("Control", "RuntimePauseController.cs");
            var boundary = ExtractMethod(pause, "internal void OnEndOfFrameGuard()", "private void OnApplicationFocus(");
            StringAssert.Contains(boundary, "machine.InterruptStepAtCompletedBoundary()");
            Assert.IsTrue(boundary.IndexOf("CompletedFrameBoundarySignal.Publish()", StringComparison.Ordinal)
                < boundary.IndexOf("machine.InterruptStepAtCompletedBoundary()", StringComparison.Ordinal));
            Assert.IsTrue(boundary.IndexOf("machine.InterruptStepAtCompletedBoundary()", StringComparison.Ordinal)
                < boundary.IndexOf("RuntimeVirtualClockBoundary.BeginPause()", StringComparison.Ordinal));
        }

        [TestMethod]
        public void ColdRestoreTargetHandoffAdvancesZeroGameplayTicks()
        {
            var control = ReadRuntimeSource(
                "Companion",
                "RuntimeControlService.cs");
            var restore = ReadRuntimeSource(
                "ReplaySave",
                "RuntimeReplayRestoreCoordinator.cs");
            var dispatcher = ReadRuntimeSource(
                "Ipc",
                "RuntimeCommandDispatcher.cs");
            var supervisor = ReadCompanionSource(
                "Services",
                "ColdRestoreSupervisor.cs");
            var handoff = ExtractMethod(
                restore,
                "private ReplayRestoreResult CompleteColdTargetHandoff(",
                "public ReplayRestoreResult ReleaseColdBaseline(");
            var adopt = ExtractMethod(
                control,
                "public ControlResult AdoptRestoredPauseBoundary(",
                "public ControlResult PrepareForReplayRestore()");

            AssertDoesNotContain(handoff, "pauseController.Resume(");
            AssertDoesNotContain(handoff, "controls.Resume(");
            StringAssert.Contains(handoff, "coldTargetHandoff(");
            StringAssert.Contains(handoff, "pauseController = null;");
            StringAssert.Contains(handoff, "settingsLease = null;");
            StringAssert.Contains(
                handoff,
                "phase = ReplayRestorePhase.Completed;");

            StringAssert.Contains(
                adopt,
                "restoredPause.ReplacePausedBoundaryCommandPump(");
            StringAssert.Contains(adopt, "pause = restoredPause;");
            StringAssert.Contains(
                adopt,
                "adoptedRestoreSettingsLease = restoredSettings;");
            StringAssert.Contains(adopt, "movie = restoredMovie;");
            AssertDoesNotContain(adopt, "CreatePlaybackController(");
            AssertDoesNotContain(adopt, ".Resume(");
            AssertNoHeroGameplayWriter(adopt);

            StringAssert.Contains(
                dispatcher,
                "controls.AdoptRestoredPauseBoundary");
            StringAssert.Contains(
                supervisor,
                "IpcMessageTypes.ResumeReplaySaveRestore");
            StringAssert.Contains(
                supervisor,
                "await completedTask;");
        }

        [TestMethod]
        public void InteractiveRecordingCannotReuseProcessRootAfterSaveLoad()
        {
            var journal = ReadRuntimeSource("Playback", "RuntimeReplayJournal.cs");
            foreach (var pair in new[]
            {
                new[] { "private void OnSavegameLoad(int slot)", "private void OnAfterSavegameLoad" },
                new[] { "private void OnAfterSavegameLoad(SaveGameData data)", "private void OnNewGame" },
                new[] { "private void OnNewGame()", "private void InvalidateSourceRecordingRootForLoad" }
            })
            {
                StringAssert.Contains(ExtractMethod(journal, pair[0], pair[1]),
                    "InvalidateSourceRecordingRootForLoad();");
            }
            var invalidation = ExtractMethod(journal,
                "private void InvalidateSourceRecordingRootForLoad()",
                "private void EstablishBaseline(");
            StringAssert.Contains(invalidation, "sourceRecordingRoot == null");
            StringAssert.Contains(invalidation, "!sourceRecordingRoot.PreparationStarted && !sourceRecordingRootReady");
            StringAssert.Contains(invalidation, "sourceRecordingRootReady = false;");
            StringAssert.Contains(invalidation, "sourceRecordingRootError =");
            StringAssert.Contains(ExtractMethod(journal,
                "public bool IsAvailable =>", "public bool HasGap =>"),
                "sourceRecordingRootError.Length == 0");
            AssertNoHeroGameplayWriter(invalidation);
        }

        [TestMethod]
        public void SourcePreparationDeadlineIncludesPostHandshakeStabilization()
        {
            var journal = ReadRuntimeSource("Playback", "RuntimeReplayJournal.cs");
            var prepare = ExtractMethod(journal,
                "private void TryEstablishBaselineAtCompletedFrameBoundary()",
                "private bool AdvanceSourceRecordingRoot()");
            var deadline = prepare.IndexOf("sourcePreparationElapsed?.Elapsed.TotalSeconds >= 120d",
                StringComparison.Ordinal);
            Assert.IsTrue(deadline >= 0);
            Assert.IsTrue(deadline < prepare.IndexOf("sourceRecordingRoot?.PreparationStarted", StringComparison.Ordinal));
            Assert.IsTrue(deadline < prepare.IndexOf("IsSupportedBaselineAnchor()", StringComparison.Ordinal));
            StringAssert.Contains(prepare.Substring(deadline, 510), "ReleaseSourcePreparationInput();");
            StringAssert.Contains(journal, "sourcePreparationElapsed = System.Diagnostics.Stopwatch.StartNew();");
            var cleanup = ExtractMethod(journal, "private void ReleaseSourcePreparationInput()",
                "internal void ReanchorColdBaseline(");
            StringAssert.Contains(cleanup, "sourcePreparationElapsed?.Stop();");
            StringAssert.Contains(cleanup, "sourcePreparationElapsed = null;");
        }

        [TestMethod]
        public void InputAttachmentPublishesOnlyAfterAcquisitionAndReportsRollbackFailure()
        {
            var adapter = ReadRuntimeSource("Input", "IHeroInputAdapter.cs");
            var attach = ExtractMethod(adapter, "public void Attach(HeroActions heroActions)",
                "public void Prepare(InputSample sample)");
            Assert.IsTrue(attach.IndexOf("lease = BindingLease.Acquire(", StringComparison.Ordinal)
                < attach.IndexOf("actions = heroActions;", StringComparison.Ordinal));
            var bindingLease = ReadRuntimeSource("Input", "BindingLease.cs");
            var acquire = ExtractMethod(bindingLease, "public static BindingLease Acquire(",
                "public IReadOnlyDictionary<TasAction, float> ReadOriginalValues(");
            StringAssert.Contains(acquire, "catch (Exception attachException)");
            StringAssert.Contains(acquire, "var restored = lease.Restore();");
            StringAssert.Contains(acquire, "if (!restored.Equivalent)");
            StringAssert.Contains(acquire, "attachException);");
        }

        [TestMethod]
        public void RecordingPreparationUsesNativeBindingsAndReleasesBeforeReplay()
        {
            var root = ReadRuntimeSource("Companion", "RuntimeRecordingRootCoordinator.cs");
            StringAssert.Contains(root, "preparationInput.Attach(actions)");
            StringAssert.Contains(root, "input.DetachAndRestore()");
            StringAssert.Contains(root, "if (!restored.Equivalent)");
            Assert.IsTrue(root.IndexOf("preparationInput.Attach(actions)", StringComparison.Ordinal)
                < root.IndexOf("if (!PrepareClockPhase()) return false", StringComparison.Ordinal));
            Assert.IsTrue(root.IndexOf("if (!PrepareClockPhase()) return false", StringComparison.Ordinal)
                < root.IndexOf("ConfigurePayloadRoot();", StringComparison.Ordinal));
            StringAssert.Contains(root, "CancelRecordingClockCalibration");
            var cleanup = ExtractMethod(root, "public void Dispose()", "private void RestorePreparationInput()");
            Assert.IsTrue(cleanup.IndexOf("finally", StringComparison.Ordinal)
                < cleanup.IndexOf("RestorePreparationInput();", StringComparison.Ordinal));
            var controls = ReadRuntimeSource("Companion", "RuntimeControlService.cs");
            foreach (var signature in new[] { "public PlaybackStartResult StartReplay()",
                         "public ControlResult Pause()", "public ControlResult Step(int count)",
                         "public ControlResult StartInputBatch(int count)" })
            {
                var start = controls.IndexOf(signature, StringComparison.Ordinal);
                Assert.IsTrue(start >= 0);
                StringAssert.Contains(controls.Substring(start, Math.Min(180, controls.Length - start)),
                    "RequireRecordingOriginReady();");
            }
            var restore = ReadRuntimeSource("ReplaySave", "RuntimeReplayRestoreCoordinator.cs");
            var release = ExtractMethod(restore, "public ReplayRestoreResult ReleaseColdBaseline(", "public void Dispose()");
            Assert.IsTrue(release.IndexOf("recordingRootCoordinator?.Dispose();", StringComparison.Ordinal)
                < release.IndexOf("StartFullReplay(reusePauseController: true)", StringComparison.Ordinal));
            var journal = ReadRuntimeSource("Playback", "RuntimeReplayJournal.cs");
            StringAssert.Contains(journal, "ReleaseSourcePreparationInput();");
            var menu = ReadRuntimeSource("ReplaySave", "ReplaySaveMenuController.cs");
            StringAssert.Contains(menu, "|| HasOriginNotice)");
            StringAssert.Contains(menu, "if (HasOriginNotice)");
        }

        [TestMethod]
        public void ColdRestoreRequiresSymmetricStartupAttestationBeforeBaselineRelease()
        {
            var attestor = ReadRuntimeSource(
                "Companion",
                "RuntimeStartupProfileAttestor.cs");
            var restore = ReadRuntimeSource(
                "ReplaySave",
                "RuntimeReplayRestoreCoordinator.cs");
            var dispatcher = ReadRuntimeSource(
                "Ipc",
                "RuntimeCommandDispatcher.cs");
            var supervisor = ReadCompanionSource(
                "Services",
                "ColdRestoreSupervisor.cs");

            StringAssert.Contains(
                attestor,
                "StartupProfileContract.PayloadReadyEventPrefix");
            StringAssert.Contains(
                attestor,
                "RuntimeVirtualClockRegistered");
            StringAssert.Contains(
                attestor,
                "TimeUpdateResumeBoundaryInstalled");
            StringAssert.Contains(
                attestor,
                "StartupProfileAttestationStatus.Verified");
            AssertDoesNotContain(attestor, "Time.time =");
            AssertDoesNotContain(attestor, "UnityEngine.Random.InitState(");
            AssertNoHeroGameplayWriter(attestor);

            StringAssert.Contains(
                dispatcher,
                "sourceStartup.RootStatus\n"
                + "                    != StartupRecordingRootStatus.Verified");
            StringAssert.Contains(dispatcher, "\"StartupUnverified\"");
            StringAssert.Contains(
                dispatcher,
                "targetStartup.RunId,\n"
                + "                    intent.OperationId");

            var baseline = ExtractMethod(
                restore,
                "private void AdvanceBaselineAlignment()",
                "private bool AdvanceColdRecordingRoot()");
            StringAssert.Contains(
                baseline,
                "if (!coldOriginReady && !AdvanceColdRecordingRoot())");
            StringAssert.Contains(restore, "savedOrigin.CompareBoundary(elapsed,");
            var rootGate = ExtractMethod(
                ReadRuntimeSource("Companion", "RuntimeRecordingRootCoordinator.cs"),
                "public bool Advance()",
                "private void SignalColdRootEvent(string suffix)");
            StringAssert.Contains(restore, "recordingRootCoordinator.Advance()");
            StringAssert.Contains(restore, "Application.onBeforeRender += OnBeforeRender;");
            StringAssert.Contains(restore, "Application.onBeforeRender -= OnBeforeRender;");
            var completed = ExtractMethod(restore,
                "internal void OnCompletedFrame()", "private void AdvanceBaselineAlignment()");
            StringAssert.Contains(completed, "lastColdBaselineFrame == Time.frameCount");
            StringAssert.Contains(completed, "AdvanceBaselineAlignment();");
            StringAssert.Contains(rootGate, "SignalColdRootEvent(\".request\")");
            StringAssert.Contains(
                rootGate,
                "SignalColdRootEvent(\".recording-request\")");
            StringAssert.Contains(
                rootGate,
                "(float)RootBoundarySeconds");
            StringAssert.Contains(rootGate, "ConfigurePayloadRoot();");
            StringAssert.Contains(restore, "rootBoundarySeconds: origin?.RootBoundarySeconds");
            AssertDoesNotContain(rootGate, "Time.time =");
            AssertDoesNotContain(rootGate, "Time.fixedTime =");
            AssertDoesNotContain(rootGate, "UnityEngine.Random.InitState(");
            AssertNoHeroGameplayWriter(rootGate);

            var baselineReady = supervisor.IndexOf(
                "await baselineTask;",
                StringComparison.Ordinal);
            var targetRoot = supervisor.IndexOf(
                "requireRecordingRoot: true",
                baselineReady,
                StringComparison.Ordinal);
            var release = supervisor.IndexOf(
                "IpcMessageTypes.ReleaseColdRestoreBaseline",
                targetRoot,
                StringComparison.Ordinal);
            Assert.IsTrue(baselineReady >= 0);
            Assert.IsTrue(baselineReady < targetRoot);
            Assert.IsTrue(targetRoot < release);
        }

        [TestMethod]
        public void MovieSeekAndPastInputEditsUsePersistedColdTargetOnly()
        {
            var manager = ReadRuntimeSource(
                "ReplaySave",
                "RuntimeReplaySaveManager.cs");
            var restore = ReadRuntimeSource(
                "ReplaySave",
                "RuntimeReplayRestoreCoordinator.cs");
            var dispatcher = ReadRuntimeSource(
                "Ipc",
                "RuntimeCommandDispatcher.cs");
            var broker = ReadCompanionSource(
                "Automation",
                "AutomationBroker.cs");
            var supervisor = ReadCompanionSource(
                "Services",
                "ColdRestoreSupervisor.cs");
            var intent = ReadCoreSource(
                "ReplaySave",
                "ColdRestoreIntent.cs");

            StringAssert.Contains(manager, "store.PublishMovieObject(");
            StringAssert.Contains(
                manager,
                "FindNearestCompatible(");
            StringAssert.Contains(restore, "store.LoadMovieObject(");
            StringAssert.Contains(restore, "CreateReplayPrefix(");
            StringAssert.Contains(
                restore,
                ".ReconstructedObservation");
            StringAssert.Contains(dispatcher, "\"ColdRestoreRequired\"");
            AssertDoesNotContain(
                dispatcher,
                "StartMovieSeek(command.Fields);");
            StringAssert.Contains(broker, ".BeginMovieSeekAsync(");
            AssertDoesNotContain(
                broker,
                "IpcMessageTypes.SeekMovieTick,");
            StringAssert.Contains(
                supervisor,
                "ColdRestoreOperationKind.SeekMovieTick");
            StringAssert.Contains(
                intent,
                "must not claim a pre-existing semantic hash");
            AssertNoHeroGameplayWriter(restore);
        }

        [TestMethod]
        public void ReplaySavePumpsAreLazyAndScopedToActiveWork()
        {
            var manager = ReadRuntimeSource(
                "ReplaySave",
                "RuntimeReplaySaveManager.cs");
            var restore = ReadRuntimeSource(
                "ReplaySave",
                "RuntimeReplayRestoreCoordinator.cs");
            var managerStart = ExtractMethod(
                manager,
                "public void Start()",
                "public ReplaySaveRequestResult RequestManualSave(");
            var managerRequest = ExtractMethod(
                manager,
                "public ReplaySaveRequestResult RequestManualSave(",
                "public ReplaySaveRequestResult RequestMovieCheckpointSave(");
            var restoreConstructor = ExtractMethod(
                restore,
                "public RuntimeReplayRestoreCoordinator(",
                "public bool IsActive");
            var restoreBegin = ExtractMethod(
                restore,
                "public ReplayRestoreHandle Begin(",
                "public ReplayRestoreProgress Poll(");

            AssertDoesNotContain(managerStart, "new GameObject(");
            StringAssert.Contains(managerRequest, "EnsureRuntimePump();");
            StringAssert.Contains(
                manager,
                "current?.StopRuntimePumpIfIdle();");
            AssertDoesNotContain(restoreConstructor, "new GameObject(");
            AssertDoesNotContain(
                restoreConstructor,
                "ModHooks.BeforeSceneLoadHook +=");
            StringAssert.Contains(restoreBegin, "EnsureRuntimePump();");
            StringAssert.Contains(restore, "private void StopRuntimePump()");
            StringAssert.Contains(restore, "current.Clear();");
        }

        [TestMethod]
        public void CandidateColdMatrixAllowsSteamToReleaseBetweenAttempts()
        {
            var matrix = ReadScript("Invoke-T24CandidateColdMatrix.ps1");

            StringAssert.Contains(matrix, "InterAttemptCooldownSeconds");
            StringAssert.Contains(
                matrix,
                "Start-Sleep -Seconds $InterAttemptCooldownSeconds");
            StringAssert.Contains(matrix, "stoppedAtFirstFailure = $true");
            StringAssert.Contains(
                matrix,
                "existing-manual-reset-one-neutral-completed-frame-preroll-v2");
            StringAssert.Contains(
                matrix,
                "neutralPreRollCompletedFrameCountAfterHost -eq 1");
        }

        [TestMethod]
        public void MovieTickRequiresVanillaHeroActionsUpdateInSameRawTick()
        {
            var source = ReadRuntimeSource(
                "Control",
                "RuntimePauseController.cs");
            var authorize = ExtractMethod(
                source,
                "public bool TryAuthorizeMovieTick(",
                "public void OnMovieTickSkipped(");
            var update = ExtractMethod(
                source,
                "private void OnPlayerActionSetUpdate(",
                "private bool HeroActionsMayAdvance(");

            StringAssert.Contains(
                authorize,
                "lastHeroActionUpdateTick != rawInputTick");
            StringAssert.Contains(
                authorize,
                "!lastHeroActionUpdateAdvanced");
            StringAssert.Contains(
                update,
                "ReferenceEquals(");
            StringAssert.Contains(
                update,
                "input.inputActions");
            StringAssert.Contains(
                update,
                "orig(self, updateTick, deltaTime);");
            AssertDoesNotContain(update, ".RegainControl(");
            AssertDoesNotContain(update, ".RelinquishControl(");
            AssertDoesNotContain(update, "transform.position =");
            AssertDoesNotContain(update, "body.velocity =");
        }

        [TestMethod]
        public void ReplayDoesNotSkipExtraWarmupFramesAfterSceneLoad()
        {
            var replayer = ReadRuntimeSource("Playback", "HeroActionReplayer.cs");
            AssertDoesNotContain(replayer, "sceneInputWarmupTicks");
            StringAssert.Contains(replayer, "!movieTickGate.TryAuthorizeMovieTick(inputTick)");
        }

        [TestMethod]
        public void TerminalReleaseCannotBypassPauseOrStepGate()
        {
            var replayer = ReadRuntimeSource(
                "Playback",
                "HeroActionReplayer.cs");
            var playback = ReadRuntimeSource(
                "Playback",
                "RuntimePlaybackController.cs");

            AssertDoesNotContain(
                replayer,
                "if (!IsTerminalRelease(preparedInput))");
            AssertDoesNotContain(
                replayer,
                "ITransitionReleaseMovieTickGate");
            StringAssert.Contains(replayer, "movieTickGate.OnMovieTickSkipped(inputTick)");
            StringAssert.Contains(
                playback,
                "lastRejectionWasGameplayTransition");
            StringAssert.Contains(
                playback,
                "inner?.TryAuthorizeMovieTick(rawInputTick)");
        }

        [TestMethod]
        public void FirstMovieInputIsPrimedAtVanillaHeroActionsUpdate()
        {
            var source = ReadRuntimeSource(
                "Playback",
                "HeroActionReplayer.cs");
            var start = ExtractMethod(
                source,
                "public PlaybackStartResult Start(",
                "public PlaybackStopResult RequestStop(");
            var prime = ExtractMethod(
                source,
                "private void OnPlayerActionSetUpdating(",
                "private void CompleteFirstInputPriming(");

            StringAssert.Contains(start, "TasAction.None");
            StringAssert.Contains(start, "firstInputPending = true;");
            StringAssert.Contains(
                start,
                "On.InControl.PlayerActionSet.Update +=");
            StringAssert.Contains(prime, "ReferenceEquals(self, attachedActions)");
            StringAssert.Contains(prime, "adapter.Prepare(preparedInput);");
            StringAssert.Contains(
                prime,
                "original(self, updateTick, deltaTime);");
            AssertDoesNotContain(prime, ".RegainControl(");
            AssertDoesNotContain(prime, "transform.position =");
            AssertDoesNotContain(prime, "body.velocity =");
        }

        [TestMethod]
        public void NormalPlaybackNeverWritesHeroControlState()
        {
            var source = ReadRuntimeSource(
                "Playback",
                "RuntimePlaybackController.cs");

            AssertDoesNotContain(source, ".RegainControl(");
            AssertDoesNotContain(source, ".RelinquishControl(");
        }

        [TestMethod]
        public void ProductReplayNeverReseedsUnityRandom()
        {
            var source = ReadRuntimeSource(
                "Companion",
                "RuntimeControlService.cs");

            AssertDoesNotContain(source, "Random.InitState(");
            AssertDoesNotContain(source, "Random.state =");
            StringAssert.Contains(
                source,
                "blocked-by-vanilla-equivalence");
        }

        [TestMethod]
        public void ReplaySaveCaptureAndRestoreDoNotWriteHeroPoseOrControl()
        {
            var capture = ReadRuntimeSource(
                "ReplaySave",
                "ReplaySaveSnapshotCapture.cs");
            var restore = ReadRuntimeSource(
                "ReplaySave",
                "RuntimeReplayRestoreCoordinator.cs");

            AssertNoHeroGameplayWriter(capture);
            AssertNoHeroGameplayWriter(restore);
        }

        [TestMethod]
        public void ReplaySaveSnapshotHashDoesNotQuantizeHeroCoordinates()
        {
            var capture = ReadRuntimeSource(
                "ReplaySave",
                "ReplaySaveSnapshotCapture.cs");

            StringAssert.Contains(
                capture,
                "var position = hero.gameObject.transform.position;");
            AssertDoesNotContain(capture, "CanonicalizeStableSeated");
            AssertDoesNotContain(capture, "Math.Round(");
        }

        [TestMethod]
        public void RuntimeHostDoesNotApplyLegacyDeterministicTimingProfile()
        {
            var host = ReadRuntimeSource(
                "Runtime",
                "TasRuntimeHost.cs");
            var settings = ReadRuntimeSource(
                "Settings",
                "TasGlobalSettings.cs");

            AssertDoesNotContain(
                host,
                "new ReplayDeterministicTimingLease(");
            StringAssert.Contains(
                host,
                "effective profile=none");
            StringAssert.Contains(
                settings,
                "public bool ReplaySaveDeterministicTimingEnabled;");
            StringAssert.Contains(
                settings,
                "public bool ReplayDeterministicRngEnabled;");
        }

        [TestMethod]
        public void LegacyReplayTimingTypeCannotWriteUnityClock()
        {
            var source = ReadRuntimeSource(
                "ReplaySave",
                "ReplayDeterministicTimingLease.cs");

            StringAssert.Contains(
                source,
                "public static class ReplayDeterministicTimingLease");
            AssertDoesNotContain(source, "Time.captureDeltaTime =");
            AssertDoesNotContain(source, "Time.fixedDeltaTime =");
            AssertDoesNotContain(source, "Time.timeScale =");
            AssertDoesNotContain(source, "Application.targetFrameRate =");
            AssertDoesNotContain(source, "QualitySettings.vSyncCount =");
        }

        [TestMethod]
        public void OptionalControlPumpsDoNotAllocateBeforeActualUse()
        {
            var dispatcher = ReadRuntimeSource(
                "Ipc",
                "RuntimeCommandDispatcher.cs");
            var companion = ReadRuntimeSource(
                "Companion",
                "RuntimeCompanionService.cs");
            var menu = ReadRuntimeSource(
                "ReplaySave",
                "ReplaySaveMenuController.cs");
            var journal = ReadRuntimeSource(
                "Playback",
                "RuntimeReplayJournal.cs");
            var rng = ReadRuntimeSource(
                "Rng",
                "RuntimeRngProbe.cs");
            var inspector = ReadRuntimeSource(
                "Inspector",
                "RuntimeInspector.cs");
            var dispatcherConstructor = ExtractMethod(
                dispatcher,
                "public RuntimeCommandDispatcher(",
                "public RuntimeControlService Controls");
            var companionStart = ExtractMethod(
                companion,
                "public void Start()",
                "public void Dispose()");
            var menuConstructor = ExtractMethod(
                menu,
                "public ReplaySaveMenuController(",
                "public bool Visible");
            var journalStart = ExtractMethod(
                journal,
                "public void Start()",
                "public void Flush()");
            var rngConstructor = ExtractMethod(
                rng,
                "public RuntimeRngProbe(",
                "public RuntimeRngCapabilityResolution Capability");
            var rngDiagnostics = ExtractMethod(
                rng,
                "internal void EnableDiagnostics()",
                "public void DisableHooksForDiagnosticControl()");
            var inspectorConstructor = ExtractMethod(
                inspector,
                "public RuntimeInspector(",
                "public bool Enabled");

            AssertDoesNotContain(dispatcherConstructor, "new GameObject(");
            AssertDoesNotContain(companionStart, "new GameObject(");
            AssertDoesNotContain(menuConstructor, "new GameObject(");
            AssertDoesNotContain(journalStart, "new GameObject(");
            AssertDoesNotContain(
                journalStart,
                "Application.onBeforeRender +=");
            AssertDoesNotContain(journal, "RuntimeReplayJournalRunner");
            AssertDoesNotContain(rngConstructor, "new GameObject(");
            AssertDoesNotContain(rngConstructor, "hooks.Attach();");
            AssertDoesNotContain(rng, "RuntimeRngProbeRunner");
            AssertDoesNotContain(inspectorConstructor, "new GameObject(");
            StringAssert.Contains(
                dispatcherConstructor,
                "On.GameManager.Update += OnGameManagerUpdate;");
            StringAssert.Contains(
                companionStart,
                "On.GameManager.Update += OnGameManagerUpdate;");
            StringAssert.Contains(
                menuConstructor,
                "On.GameManager.Update += OnGameManagerUpdate;");
            StringAssert.Contains(
                dispatcher,
                "controls.RuntimePumpRequired");
            StringAssert.Contains(
                dispatcher,
                "OnLateUpdate();");
            StringAssert.Contains(
                companion,
                "if (!settings.CompanionOverlayEnabled)");
            StringAssert.Contains(
                companion,
                "OverlayStableGameplayUpdateCount = 30");
            StringAssert.Contains(
                menu,
                "visible || activeRestore.HasValue");
            StringAssert.Contains(
                journalStart,
                "InputManager.OnUpdate += OnInputManagerUpdated;");
            StringAssert.Contains(
                journal,
                "TryEstablishBaselineAtCompletedFrameBoundary();");
            StringAssert.Contains(
                journal,
                "Application.onBeforeRender += OnBeforeRender;");
            StringAssert.Contains(
                journal,
                "Application.onBeforeRender -= OnBeforeRender;");
            StringAssert.Contains(
                journal,
                "TickPhase.LateUpdateEnd");
            StringAssert.Contains(rngDiagnostics, "hooks.Attach();");
            StringAssert.Contains(
                inspectorConstructor,
                "journal.BaselineEstablished += OnBaselineEstablished;");
            StringAssert.Contains(
                inspectorConstructor,
                "runtimePumpRequested && journal.IsAvailable");
            StringAssert.Contains(
                ReadRuntimeSource(
                    "Companion",
                    "RuntimeControlService.cs"),
                "public bool RuntimePumpRequired =>");
        }

        [TestMethod]
        public void StaticMutationAuditIsFrozenAndFailClosed()
        {
            var audit = ReadScript("Invoke-T24StaticMutationAudit.ps1");
            var selfTest = ReadScript("Test-T24StaticMutationAudit.ps1");
            var root = FindRepositoryRoot();
            var allowlistPath = Path.Combine(
                root,
                "fixtures",
                "t24",
                "runtime-hook-mutation-allowlist.v1.json");
            Assert.IsTrue(
                File.Exists(allowlistPath),
                "T24 static mutation allowlist not found: " + allowlistPath);
            var allowlist = File.ReadAllText(allowlistPath);

            StringAssert.Contains(audit, "unexpectedFindings");
            StringAssert.Contains(audit, "missingFindings");
            StringAssert.Contains(audit, "countMismatches");
            StringAssert.Contains(audit, "hashMismatches");
            StringAssert.Contains(audit, "gateFailures");
            StringAssert.Contains(
                audit,
                "normalTasGameplayWriterCount must be zero");
            StringAssert.Contains(audit, "hero-transform-writer");
            StringAssert.Contains(audit, "player-resource-writer");
            StringAssert.Contains(audit, "random-state-writer");
            StringAssert.Contains(audit, "time-settings-writer");
            StringAssert.Contains(audit, "input-binding-writer");

            StringAssert.Contains(allowlist, "\"reachability\": \"normal-tas\"");
            StringAssert.Contains(allowlist, "\"reachability\": \"verification-only\"");
            StringAssert.Contains(allowlist, "\"reachability\": \"debug-only\"");
            StringAssert.Contains(
                allowlist,
                "Automation/Mutation/HeroPoseMutationAdapter.cs");
            StringAssert.Contains(
                allowlist,
                "canonical-player-action-input");
            StringAssert.Contains(
                allowlist,
                "complete-frame-time-boundary");
            StringAssert.Contains(
                allowlist,
                "rollback-environment-restore");

            StringAssert.Contains(selfTest, "negative-missing-finding");
            StringAssert.Contains(selfTest, "negative-source-hash");
            StringAssert.Contains(
                selfTest,
                "negative-normal-gameplay-writer");
            StringAssert.Contains(selfTest, "unexpected-finding");
            StringAssert.Contains(selfTest, "source-hash-mismatch");
            StringAssert.Contains(selfTest, "policy-failure");
        }

        [TestMethod]
        public void DynamicScenarioSamplerIsReadOnlyAndMechanismComplete()
        {
            var sampler = ReadGameObservationSource(
                "VanillaEquivalenceSampler.cs");

            StringAssert.Contains(
                sampler,
                "world.rng.unity.synchronizedOrigin.s0");
            StringAssert.Contains(
                sampler,
                "world.rng.unity.synchronizedOrigin.s3");
            StringAssert.Contains(
                sampler,
                "diagnostic.world.rng.unity.current.s0");
            StringAssert.Contains(sampler, "currentS0");
            StringAssert.Contains(sampler, "comparable: false");
            StringAssert.Contains(
                sampler,
                "SetUnityRandomSynchronizationOrigin(");
            StringAssert.Contains(
                sampler,
                "Unity RNG synchronization origin was not supplied");
            StringAssert.Contains(
                sampler,
                "world.falseKnight.fsm.state");
            StringAssert.Contains(
                sampler,
                "world.falseKnight.hitter.damage");
            StringAssert.Contains(
                sampler,
                "hero.state.nailRecoiling");
            StringAssert.Contains(
                sampler,
                "hero.doubleJump.wings");
            StringAssert.Contains(sampler, "hero.slash.executing");
            StringAssert.Contains(sampler, "time.unscaledDeltaTime");
            StringAssert.Contains(sampler, "time.unscaledRelative");
            StringAssert.Contains(
                sampler,
                "diagnostic.time.realtimeSinceStartup");
            StringAssert.Contains(
                sampler,
                "FindObjectsOfType<HealthManager>()");

            AssertDoesNotContain(sampler, ".SendEvent(");
            AssertDoesNotContain(sampler, "Random.InitState(");
            AssertDoesNotContain(sampler, "Random.state =");
            AssertDoesNotContain(sampler, "transform.position =");
            AssertDoesNotContain(sampler, "body.position =");
            AssertDoesNotContain(sampler, "body.velocity =");
            AssertDoesNotContain(sampler, ".TakeHealth(");
            AssertDoesNotContain(sampler, ".SetState(");
        }

        [TestMethod]
        public void ScenarioCoverageIsStrictFailClosedAndMatrixGated()
        {
            var validator = ReadScript("Test-T24ScenarioCoverage.ps1");
            var selfTest = ReadScript(
                "Test-T24ScenarioCoverageSelfTest.ps1");
            var reference = ReadScript("Invoke-T24ReferenceSmoke.ps1");
            var candidate = ReadScript("Invoke-T24CandidateSmoke.ps1");
            var referenceMatrix = ReadScript(
                "Invoke-T24ReferenceColdMatrix.ps1");
            var candidateMatrix = ReadScript(
                "Invoke-T24CandidateColdMatrix.ps1");
            var inputSchedule = ReadScript(
                "New-T24PhysicalInputSchedule.ps1");
            var root = FindRepositoryRoot();
            var heroContractPath = Path.Combine(
                root,
                "fixtures",
                "t24",
                "scenarios",
                "t24.hero-action-correlation.v1.json");
            var bossContractPath = Path.Combine(
                root,
                "fixtures",
                "t24",
                "scenarios",
                "t24.false-knight-combat.v1.json");
            Assert.IsTrue(File.Exists(heroContractPath));
            Assert.IsTrue(File.Exists(bossContractPath));
            var heroContract = File.ReadAllText(heroContractPath);
            var bossContract = File.ReadAllText(bossContractPath);

            foreach (var assertionType in new[]
                     {
                         "'seen'",
                         "'coincident'",
                         "'transition'",
                         "'delta'",
                         "'ordered-match'",
                         "'all-when'"
                     })
            {
                StringAssert.Contains(validator, assertionType);
            }
            StringAssert.Contains(validator, "Assert-T24ExactKeys");
            StringAssert.Contains(validator, "comparisonSha256");
            StringAssert.Contains(
                validator,
                "lacks required field");
            StringAssert.Contains(
                validator,
                "Frozen movie SHA-256 mismatch");
            StringAssert.Contains(
                validator,
                "requiredModes must include");
            StringAssert.Contains(
                validator,
                "[AllowEmptyString()][string]$Hex");
            StringAssert.Contains(
                selfTest,
                "-Key 'empty' -Kind Utf8String -Value ''");

            StringAssert.Contains(
                selfTest,
                "missing-required-field");
            StringAssert.Contains(selfTest, "changed-movie-hash");
            StringAssert.Contains(
                selfTest,
                "equal-but-mechanism-absent");
            StringAssert.Contains(
                selfTest,
                "unknown-assertion-type");
            StringAssert.Contains(
                selfTest,
                "unknown-contract-property");
            StringAssert.Contains(
                validator,
                "\\.v[1-9][0-9]*\\z");
            StringAssert.Contains(
                selfTest,
                "positive-versioned-id");
            StringAssert.Contains(
                selfTest,
                "invalid-scenario-version");

            StringAssert.Contains(reference, "$ScenarioContractPath");
            StringAssert.Contains(candidate, "scenarioCoverageClean");
            StringAssert.Contains(
                reference,
                "Mono-safe atomic path limit");
            StringAssert.Contains(
                candidate,
                "Mono-safe atomic path limit");
            StringAssert.Contains(candidate, "[AllowEmptyString()]");
            StringAssert.Contains(
                candidate,
                "Live tas-passive capture must be produced by");
            StringAssert.Contains(
                candidate,
                "t24-vanilla-synchronized-negative-control-v13-exact-post-root-request-first-phase");
            StringAssert.Contains(
                candidate,
                "Frame-clock and absolute-time fields must remain bitwise exact.");
            StringAssert.Contains(
                candidate,
                "physicalInputSynchronizationPrimeFrames");
            StringAssert.Contains(
                candidate,
                "$expectedRule = 'absolute-only-ulp-diagnostic'");
            StringAssert.Contains(
                candidate,
                "candidateScenarioCoverageVerdict");
            StringAssert.Contains(
                referenceMatrix,
                "-ScenarioContractPath");
            StringAssert.Contains(
                referenceMatrix,
                "stoppedAtFirstFailure");
            StringAssert.Contains(
                referenceMatrix,
                "Mono-safe atomic path limit");
            StringAssert.Contains(
                candidateMatrix,
                "scenarioCoverageClean");
            StringAssert.Contains(
                candidateMatrix,
                "Mono-safe atomic path limit");
            StringAssert.Contains(
                inputSchedule,
                "New-T24InputField -Key 'input.axisX' -Value $axisX");
            StringAssert.Contains(
                inputSchedule,
                "New-T24InputField -Key 'input.axisY' -Value $axisY");
            StringAssert.Contains(
                inputSchedule,
                "axisX=10000*(right-left);axisY=10000*(up-down)");

            StringAssert.Contains(
                heroContract,
                "slash-drives-original-hero-action");
            StringAssert.Contains(
                heroContract,
                "wings-never-outlive-original-airborne-physics");
            StringAssert.Contains(
                heroContract,
                "double-jump-state-drives-original-hero-and-wings");
            StringAssert.Contains(
                bossContract,
                "damage-recoil-is-original");
            StringAssert.Contains(
                bossContract,
                "nail-hit-produces-recoil");
            StringAssert.Contains(
                bossContract,
                "boss-full-attack-cycle");
        }

        [TestMethod]
        public void ManualTasUsesSemanticStudioUiAndFailsClosedOnAudit()
        {
            var root = FindRepositoryRoot();
            var xaml = File.ReadAllText(
                Path.Combine(
                    root,
                    "src",
                    "HollowKnightTAS.Companion",
                    "MainWindow.xaml"));
            var viewModel = File.ReadAllText(
                Path.Combine(
                    root,
                    "src",
                    "HollowKnightTAS.Companion",
                    "ViewModels",
                    "MainViewModel.cs"));
            var ui = ReadScript("T24CompanionUiAutomation.ps1");
            var candidate = ReadScript("Invoke-T24CandidateSmoke.ps1");
            var matrix = ReadScript("Invoke-T24CandidateColdMatrix.ps1");
            var manualControl = ExtractMethod(
                ui,
                "function Invoke-T24ManualUiControl {",
                "function Get-T24HmacSha256 {");
            var manualAuditStart = ui.IndexOf(
                "function Export-T24ManualUiAudit {",
                StringComparison.Ordinal);
            Assert.IsTrue(
                manualAuditStart >= 0,
                "Missing manual UI audit function.");
            var manualAudit = ui.Substring(manualAuditStart);

            foreach (var automationId in new[]
                     {
                         "HktasStudio.MainWindow",
                         "HktasStudio.StatusText",
                         "HktasStudio.ConnectionBadge",
                         "HktasStudio.PlayPauseButton",
                         "HktasStudio.FrameAdvanceButton",
                         "HktasStudio.InputGridTab",
                         "HktasStudio.InputGrid"
                     })
            {
                StringAssert.Contains(xaml, automationId);
            }

            foreach (var automationId in new[]
                     {
                         "HktasStudio.MainWindow",
                         "HktasStudio.StatusText",
                         "HktasStudio.ConnectionBadge",
                         "HktasStudio.MovieEditorTab",
                         "HktasStudio.MovieText",
                         "HktasStudio.MovieValidateButton",
                         "HktasStudio.MovieValidationOutput",
                         "HktasStudio.MovieUploadButton",
                         "HktasStudio.MovieStartReplayButton",
                         "HktasStudio.ControlStateTab",
                         "HktasStudio.ControlPauseButton",
                         "HktasStudio.ControlStepButton"
                     })
            {
                StringAssert.Contains(ui, automationId);
            }

            StringAssert.Contains(candidate, "'tas-manual-ui'");
            StringAssert.Contains(candidate, "'tas-manual-ui' { 'ManualTas' }");
            StringAssert.Contains(
                candidate,
                "Invoke-T24ManualUiControl");
            StringAssert.Contains(
                candidate,
                "Export-T24ManualUiAudit");
            StringAssert.Contains(candidate, "$controlSurfaceClean");
            StringAssert.Contains(matrix, "'tas-manual-ui'");
            StringAssert.Contains(matrix, "controlSurfaceClean");
            StringAssert.Contains(
                matrix,
                "manualUiExternalWriteCommandCount");
            StringAssert.Contains(
                viewModel,
                "var expectedMovieTick = expectedModeRequired");
            StringAssert.Contains(viewModel, "\"Paused\"");
            StringAssert.Contains(
                viewModel,
                "expectedMovieTick,");
            StringAssert.Contains(
                viewModel,
                "copy.Remove(\"requestId\")");
            StringAssert.Contains(
                viewModel,
                "automationArguments,");

            StringAssert.Contains(
                manualControl,
                "interaction = 'semantic-windows-ui-automation'");
            StringAssert.Contains(
                manualControl,
                "visualRecognitionUsed = $false");
            StringAssert.Contains(
                manualControl,
                "aiWriteCommandsUsed = $false");
            StringAssert.Contains(
                manualControl,
                "Manual UI single-frame step was not exact");
            AssertDoesNotContain(manualControl, "Invoke-SdkCommand");
            AssertDoesNotContain(manualControl, "Acquire-SdkLease");
            AssertDoesNotContain(manualControl, "SetForegroundWindow");
            AssertDoesNotContain(manualControl, "keybd_event");

            StringAssert.Contains(manualAudit, "-Value 'companion-ui'");
            StringAssert.Contains(
                manualAudit,
                "Manual UI run used a non-UI write command");
            StringAssert.Contains(
                manualAudit,
                "'getCapabilities'");
            StringAssert.Contains(manualAudit, "'getState'");
            StringAssert.Contains(
                manualAudit,
                "externalWriteCommandCount = 0");
            StringAssert.Contains(
                manualAudit,
                "Manual UI audit command mismatch at index");
            StringAssert.Contains(
                manualAudit,
                "@('proposeMoviePatch', 'Valid')");
            StringAssert.Contains(
                manualAudit,
                "Manual UI audit result-code mismatch at index");
        }

        [TestMethod]
        public void HumanAiParityUsesDirectCanonicalAndAuthoritativeEvidence()
        {
            var parity = ReadScript("Test-T24HumanAiParity.ps1");
            var runEligibility = ExtractMethod(
                parity,
                "function Read-RunRecord {",
                "function Get-CachedTraceSummary");

            StringAssert.Contains(
                parity,
                "t24-human-ai-authoritative-parity-v1");
            StringAssert.Contains(
                parity,
                "distinct-runs-with-identical-no-mod-reference-trace-and-authoritative-baseline-signature");
            StringAssert.Contains(
                parity,
                "canonicalInputLedger = 'bitwise-exact-every-logical-tick'");
            StringAssert.Contains(
                parity,
                "bitwise-exact-for-all-comparable-fields-except-frozen-no-mod-tolerated-fields");
            StringAssert.Contains(
                parity,
                "canonicalInputPhaseSha256");
            StringAssert.Contains(
                parity,
                "$ToleratedKeys.Contains($key)");
            StringAssert.Contains(
                parity,
                "withinFrozenNoModBound");
            StringAssert.Contains(
                parity,
                "[AllowEmptyCollection()]");
            StringAssert.Contains(
                parity,
                "field-policy-not-exact-by-default");
            StringAssert.Contains(
                parity,
                "duplicate-tolerated-field");
            StringAssert.Contains(
                parity,
                "invalid-tolerated-field-bound");
            StringAssert.Contains(
                parity,
                "manual-control-surface-mismatch");
            StringAssert.Contains(
                parity,
                "ai-control-surface-mismatch");
            StringAssert.Contains(
                parity,
                "visual-recognition-used");
            StringAssert.Contains(
                parity,
                "$usedAi.Contains($key)");
            StringAssert.Contains(
                parity,
                "firstUnacceptedDifferenceTick");
            StringAssert.Contains(
                runEligibility,
                "'referenceBaselineCatalogMatch'");
            StringAssert.Contains(
                runEligibility,
                "'baselineSemanticExactEquivalent'");
            StringAssert.Contains(
                runEligibility,
                "'baselineRigidbodyComponentsObserved'");
            StringAssert.Contains(
                runEligibility,
                "'baselineRenderTransformEnvelopeEquivalent'");
            AssertDoesNotContain(
                runEligibility,
                "'baselineAuthoritativeExactEquivalent'");
        }

        [TestMethod]
        public void SynchronizedNeutralTickStartsAtExternalHandshake()
        {
            var observer = ReadReferenceObserverSource(
                "HollowKnightTASReferenceObserverMod.cs");
            var earlyUpdate = ExtractMethod(
                observer,
                "public void OnEarlyUpdate()",
                "public void OnEarlyFixedUpdate()");
            var lateUpdate = ExtractMethod(
                observer,
                "public void OnLateUpdate()",
                "public void OnApplicationQuit()");
            var reference = ReadScript("Invoke-T24ReferenceSmoke.ps1");
            var synchronizedInput = ExtractMethod(
                reference,
                "function Invoke-SynchronizedPhysicalRegressionInput",
                "function Get-CanonicalPhysicalControlTimeline");
            var synchronizedSchedule = ExtractMethod(
                reference,
                "function Get-SynchronizedHeldSchedule",
                "function Invoke-SynchronizedPhysicalRegressionInput");

            StringAssert.Contains(lateUpdate, "if (!inputSyncActive");
            StringAssert.Contains(
                lateUpdate,
                "!VanillaEquivalenceSampler.HasGameplayInput()");
            StringAssert.Contains(
                lateUpdate,
                "options.RequireExternalInputSync");
            StringAssert.Contains(reference, "supportedActionMask = 0x7ff");
            StringAssert.Contains(reference, "dashBindings");
            StringAssert.Contains(reference, "castBindings");
            StringAssert.Contains(reference, "quickCastBindings");
            StringAssert.Contains(reference, "superDashBindings");
            StringAssert.Contains(reference, "dreamNailBindings");
            StringAssert.Contains(
                reference,
                "captured-vs-source-physical-control-signal-v2");
            StringAssert.Contains(
                reference,
                "committed-by-original-player-action-not-source-authoritative-v2");
            StringAssert.Contains(
                reference,
                "Get-CanonicalPhysicalControlTimeline");
            StringAssert.Contains(
                reference,
                "committed-by-original-player-action-and-compared-in-reference-candidate-trace-v1");
            StringAssert.Contains(
                reference,
                "Captured physical input differs from the source timeline");
            StringAssert.Contains(
                reference,
                "external-os-keyboard-frame-handshake-preroll-v2");
            StringAssert.Contains(reference, "primeFrameCount = 1");
            StringAssert.Contains(synchronizedSchedule, "'input.axisX'");
            StringAssert.Contains(synchronizedSchedule, "'input.axisY'");
            StringAssert.Contains(synchronizedSchedule, "'input.pressed'");
            StringAssert.Contains(synchronizedSchedule, "'input.released'");
            AssertDoesNotContain(synchronizedSchedule, "$expectedPressed");
            AssertDoesNotContain(synchronizedSchedule, "$expectedReleased");
            StringAssert.Contains(reference, "-StatusSnapshot $clockStatus");
            StringAssert.Contains(synchronizedInput, "[object]$StatusSnapshot");
            AssertDoesNotContain(synchronizedInput, "Get-ObserverStatus");
            StringAssert.Contains(
                observer,
                "externalInputSynchronizationPrimeFrames");
            StringAssert.Contains(
                observer,
                "On.InControl.InputManager.UpdateInternal +=");
            StringAssert.Contains(
                observer,
                "private void OnInputManagerUpdateInternal(");
            StringAssert.Contains(
                observer,
                "inputSyncHandshakeCompletedThisVisualUpdate = false;");
            StringAssert.Contains(
                observer,
                "&& !inputSyncHandshakeCompletedThisVisualUpdate");
            StringAssert.Contains(
                observer,
                "inputSyncHandshakeCompletedThisVisualUpdate = true;");
            StringAssert.Contains(earlyUpdate, "var captureThisFrame = inputSyncPrimed;");
            StringAssert.Contains(earlyUpdate, "inputSyncPrimeCount++;");
            Assert.IsTrue(
                synchronizedInput.IndexOf(
                    "$inputSyncStart.Set()",
                    StringComparison.Ordinal)
                < synchronizedInput.IndexOf(
                    "[HktasPhysicalKeyboard]::Down",
                    StringComparison.Ordinal),
                "Logical tick zero must be posted only inside the explicit "
                + "unrecorded pre-roll handshake.");
            StringAssert.Contains(
                earlyUpdate,
                "frameOpenAtUpdateBegin = IsReferenceGameplayTickOpen();");
            StringAssert.Contains(
                earlyUpdate,
                "&& frameOpenAtUpdateBegin");
            Assert.IsTrue(
                earlyUpdate.IndexOf(
                    "frameOpenAtUpdateBegin = IsReferenceGameplayTickOpen();",
                    StringComparison.Ordinal)
                < earlyUpdate.IndexOf(
                    "inputSyncFrameReady?.Set();",
                    StringComparison.Ordinal),
                "Input synchronization must not consume a movie tick before "
                + "the frame is known to be sampleable.");
        }

        [TestMethod]
        public void ReplayDrivesRawControlSignalAndObservesVanillaCommittedEdges()
        {
            var adapter = ReadRuntimeSource(
                "Input",
                "IHeroInputAdapter.cs");
            var replayer = ReadRuntimeSource(
                "Playback",
                "HeroActionReplayer.cs");

            StringAssert.Contains(adapter, "MatchesControlSignal");
            StringAssert.Contains(adapter, "CommittedEdgesMatch");
            StringAssert.Contains(
                replayer,
                "actual.MatchesControlSignal(preparedInput)");
            StringAssert.Contains(
                replayer,
                "actual.CommittedEdgesMatch(preparedInput)");
            StringAssert.Contains(
                replayer,
                "edges as an observed vanilla result");
            AssertDoesNotContain(
                replayer,
                "var matches = actual.Matches(preparedInput);");
        }

        [TestMethod]
        public void ReferenceInputHandshakeUsesTheVanillaRuntimeTransitionGate()
        {
            var observer = ReadReferenceObserverSource(
                "HollowKnightTASReferenceObserverMod.cs");
            var referenceGate = ExtractMethod(
                observer,
                "private bool IsReferenceGameplayTickOpen()",
                "public void OnEarlyFixedUpdate()");
            var playback = ReadRuntimeSource(
                "Playback",
                "RuntimePlaybackController.cs");
            var replayer = ReadRuntimeSource(
                "Playback",
                "HeroActionReplayer.cs");
            var replayerInputUpdate = ExtractMethod(
                replayer,
                "private void OnInputManagerUpdated(",
                "private void Cleanup(");
            var runtimeGate = ExtractMethod(
                playback,
                "public bool TryAuthorizeMovieTick(ulong rawInputTick)",
                "public bool TryAuthorizeTransitionRelease(");
            var runtimeReacquire = ExtractMethod(
                playback,
                "private void TryReacquireHeroControlAfterScene()",
                "private void EnsureSceneTransitionFinishedSubscription()");
            var runtimeEarlyUpdate = ExtractMethod(
                playback,
                "internal void OnUpdate()",
                "internal void OnFixedUpdate()");
            var runtimeTransitionGate = ExtractMethod(
                playback,
                "private bool IsTransitionInputBlocked(ulong rawInputTick)",
                "private static bool IsVanillaGameplayTickOpenAtUpdateBegin()");
            var runtimeFrameStartGate = ExtractMethod(
                playback,
                "private static bool IsVanillaGameplayTickOpenAtUpdateBegin()",
                "private bool TryApplyReplaySceneBoundary(");

            StringAssert.Contains(
                observer,
                "frameOpenAtUpdateBegin = IsReferenceGameplayTickOpen();");
            foreach (var required in new[]
            {
                "manager.gameState != GameState.PLAYING",
                "manager.IsInSceneTransition",
                "hero.gameObject.activeInHierarchy",
                "input.inputActions == null",
                "!scene.IsValid()",
                "!scene.isLoaded",
                "hero.cState.transitioning"
            })
            {
                StringAssert.Contains(referenceGate, required);
                StringAssert.Contains(runtimeGate, required);
                StringAssert.Contains(runtimeFrameStartGate, required);
            }
            StringAssert.Contains(referenceGate, "\"Challenge Start\"");
            StringAssert.Contains(playback, "\"Challenge Start\"");
            StringAssert.Contains(
                runtimeFrameStartGate,
                "\"Challenge Start\"");
            StringAssert.Contains(referenceGate, "Time.timeScale <= 0f");
            StringAssert.Contains(
                runtimeEarlyUpdate,
                "frameOpenAtUpdateBegin =");
            StringAssert.Contains(
                runtimeEarlyUpdate,
                "IsVanillaGameplayTickOpenAtUpdateBegin();");
            StringAssert.Contains(
                runtimeTransitionGate,
                "if (!frameOpenAtUpdateBegin)");
            StringAssert.Contains(
                referenceGate,
                "sceneInputWarmupUpdates = 2;");
            StringAssert.Contains(
                referenceGate,
                "sceneInputWarmupUpdates--;");
            StringAssert.Contains(
                playback,
                "pendingHeroControlReacquire = true;");
            AssertDoesNotContain(runtimeReacquire, "hero.acceptingInput");
            StringAssert.Contains(
                runtimeReacquire,
                "pendingHeroControlReacquire = false;");
            AssertDoesNotContain(replayer, "sceneInputWarmupTicks");
            var runtimeAuthorization = replayerInputUpdate.IndexOf(
                "!movieTickGate.TryAuthorizeMovieTick(inputTick)",
                StringComparison.Ordinal);
            Assert.IsTrue(runtimeAuthorization >= 0);
        }

        [TestMethod]
        public void ReferenceMenuGateCarriesTheAcceptedAtomicSnapshot()
        {
            var reference = ReadScript("Invoke-T24ReferenceSmoke.ps1");
            var observer = ReadReferenceObserverSource(
                "HollowKnightTASReferenceObserverMod.cs");
            var menuSelection = ExtractMethod(
                observer,
                "private static void CaptureMenuSelection(",
                "private static bool HasMenuInputEdge()");
            var referenceMatrix = ReadScript(
                "Invoke-T24ReferenceColdMatrix.ps1");
            var referenceDwellMatrix = ReadScript(
                "Invoke-T24ReferencePreludeDwellMatrix.ps1");
            var candidate = ReadScript("Invoke-T24CandidateSmoke.ps1");
            var candidateMatrix = ReadScript(
                "Invoke-T24CandidateColdMatrix.ps1");
            var accepted = reference.IndexOf(
                "$menuReadyStatus = $status",
                StringComparison.Ordinal);
            var reused = reference.IndexOf(
                "$status = $menuReadyStatus",
                accepted,
                StringComparison.Ordinal);

            Assert.IsTrue(accepted >= 0);
            Assert.IsTrue(reused > accepted);
            AssertDoesNotContain(
                reference.Substring(accepted, reused - accepted),
                "Get-ObserverStatus");
            StringAssert.Contains(menuSelection, "if (eventSystem == null)");
            StringAssert.Contains(menuSelection, "if (selected == null)");
            StringAssert.Contains(
                menuSelection,
                "catch (MissingReferenceException)");
            StringAssert.Contains(
                menuSelection,
                "catch (NullReferenceException)");
            AssertDoesNotContain(menuSelection, "selected?.name");
            StringAssert.Contains(
                reference,
                "$statusAfterDwell = Wait-ObserverMenuControlsStatus");
            StringAssert.Contains(
                reference,
                "$initialStatus = Wait-ObserverMenuControlsStatus");
            StringAssert.Contains(
                reference,
                "if ($null -ne $script:game -and $script:game.HasExited)");
            foreach (var script in new[]
            {
                reference,
                referenceMatrix,
                referenceDwellMatrix,
                candidate,
                candidateMatrix
            })
            {
                StringAssert.Contains(
                    script,
                    "[int]$FixtureReadyTimeoutSeconds = 240");
            }
        }

        [TestMethod]
        public void ExternalRngSynchronizationIsRootOnlyAndSceneLifecycleIsNative()
        {
            var payload = ReadClockPayloadSource("ClockController.cs");
            var observer = ReadReferenceObserverSource(
                "HollowKnightTASReferenceObserverMod.cs");
            var reference = ReadScript("Invoke-T24ReferenceSmoke.ps1");
            var candidate = ReadScript("Invoke-T24CandidateSmoke.ps1");
            var harness = ReadScript("T24ClockHarness.ps1");
            var envelope = ReadScript("Measure-T24ReferenceEnvelope.ps1");

            StringAssert.Contains(
                payload,
                "external-unity-startup-continuous-clock-v40-native-scene-lifecycle");
            StringAssert.Contains(
                payload,
                "unity-init-state-at-root-only-native-scene-lifecycle-v19");
            StringAssert.Contains(
                payload,
                "UnityEngine.Random.InitState(appliedSeed);");
            AssertDoesNotContain(payload, "GameManager.SceneTransitionBegan +=");
            AssertDoesNotContain(payload, ".IsActivationAllowed =");
            AssertDoesNotContain(payload, "SceneLoadFinishEventField");
            AssertDoesNotContain(payload, "TryBeginSceneClockExclusion");
            AssertDoesNotContain(payload, "SceneRngSeedDerivation.Derive(");
            AssertDoesNotContain(payload, "\"transition-start\"");
            AssertDoesNotContain(payload, "\"gameplay-ready\"");
            Assert.AreEqual(4, payload.Split("ApplyRandomSynchronization(",
                StringSplitOptions.None).Length - 1,
                "Only the root/full-run handshakes and the helper declaration may remain.");
            StringAssert.Contains(
                payload,
                "On.InControl.PlayerActionSet.Update +=");
            StringAssert.Contains(
                payload,
                "ReferenceEquals(InputHandler.Instance?.inputActions, self)");
            Assert.AreEqual(
                1,
                payload.Split(
                    "UnityEngine.Random.InitState(",
                    StringSplitOptions.None).Length - 1);
            StringAssert.Contains(payload, "EventResetMode.ManualReset");
            StringAssert.Contains(
                payload,
                "HollowKnightTAS.T24.RngSync.");
            StringAssert.Contains(
                payload,
                "randomSynchronizationRequest?.WaitOne(0) == true");
            StringAssert.Contains(payload, "TrySynchronizeRecordingRandom();");
            StringAssert.Contains(payload, "TryNormalizeRecordingBoundaryPhase();");
            StringAssert.Contains(payload, "\"recording-root\"");
            StringAssert.Contains(payload, "prefix + \".recording-request\"");
            StringAssert.Contains(
                payload,
                "recordingRootRequestObservedFrameCount - 1");
            StringAssert.Contains(
                payload,
                "RecordingPhaseNormalizationCaptureDeltaTime");
            StringAssert.Contains(
                payload,
                "recordingRootFramePhase != SceneFramePhaseTarget");
            StringAssert.Contains(
                payload,
                "Time.captureDeltaTime =\n"
                + "                RecordingPhaseNormalizationCaptureDeltaTime;");
            AssertDoesNotContain(payload, "private static void TryAlignSceneActivation()");
            AssertDoesNotContain(payload, "private static void TryAlignSceneFinish()");
            StringAssert.Contains(
                payload,
                "recordingPhaseNormalizationHeldTimeDoubleBits == 0L");
            StringAssert.Contains(
                payload,
                "observedTimeDoubleBits");
            StringAssert.Contains(
                payload,
                "-remainingNormalFrames,");
            AssertDoesNotContain(
                payload,
                "-(remainingNormalFrames + 1)");
            StringAssert.Contains(
                payload,
                "Time.captureDeltaTime = recordingPhaseNormalizationActive");
            var recordingRootSync = payload.IndexOf(
                "TrySynchronizeRecordingRandom();",
                StringComparison.Ordinal);
            var recordingRootPhaseAudit = payload.IndexOf(
                "TryNormalizeRecordingBoundaryPhase();",
                StringComparison.Ordinal);
            Assert.IsTrue(recordingRootSync >= 0);
            Assert.IsTrue(recordingRootSync < recordingRootPhaseAudit);
            StringAssert.Contains(
                payload,
                "RecordingRandomSynchronizationApplied =>");
            AssertDoesNotContain(payload, "inputSynchronizationStart");
            StringAssert.Contains(
                payload,
                "RandomSynchronizationSnapshotAvailable");
            StringAssert.Contains(
                payload,
                "randomSynchronizationStateS0");
            StringAssert.Contains(
                payload,
                "object boxed = UnityEngine.Random.state;");

            AssertDoesNotContain(observer, "UnityEngine.Random.InitState(");
            StringAssert.Contains(observer, "SignalRandomRequest(0);");
            StringAssert.Contains(
                observer,
                "SignalRandomRequest(1);");
            StringAssert.Contains(
                observer,
                "RequiredRecordingFramePhaseModulo = 4");
            AssertDoesNotContain(observer, "waiting-for-recording-frame-phase");
            StringAssert.Contains(
                observer,
                "externalRecordingRootRequestObservedFrameCount");
            StringAssert.Contains(
                observer,
                "externalRecordingRootFramePhase");
            StringAssert.Contains(
                observer,
                "externalRecordingPhaseNormalizationHoldFrameCount");
            StringAssert.Contains(
                observer,
                "recording-frame-phase-normalization-failed");
            StringAssert.Contains(
                candidate,
                "exact-recording-root-global-phase-zero-v1");
            StringAssert.Contains(
                envelope,
                "recordingRootRequestFrameOffset = 1");
            StringAssert.Contains(observer, "externalRngSynchronized");
            StringAssert.Contains(
                observer,
                "CaptureExternalRandomSynchronizationOrigin();");
            StringAssert.Contains(
                observer,
                "sampler.SetUnityRandomSynchronizationOrigin(");
            StringAssert.Contains(
                observer,
                "externalRngSynchronizationOriginCaptured");
            StringAssert.Contains(observer, "externalRngTransitionStartCount");
            StringAssert.Contains(observer, "externalRngGameplayReadyCount");
            StringAssert.Contains(observer, "externalSceneRngPending");
            StringAssert.Contains(
                observer,
                "externalSceneClockExclusionActive");
            StringAssert.Contains(
                observer,
                "externalSceneClockExclusionBeginCount");
            StringAssert.Contains(
                observer,
                "externalSceneClockExclusionFinishCount");
            StringAssert.Contains(
                observer,
                "externalSceneClockExclusionFrozenTimeUpdateCount");
            StringAssert.Contains(
                observer,
                "externalSceneClockExclusionFaultCode");
            StringAssert.Contains(
                observer,
                "externalSceneClockExclusionPreSynchronizationCount");
            StringAssert.Contains(
                observer,
                "EventResetMode.ManualReset");
            AssertDoesNotContain(
                observer,
                "hero-rigidbody-position-not-synchronized");
            AssertDoesNotContain(observer, "Float32BitsEqual(");
            StringAssert.Contains(
                reference,
                "[Threading.EventResetMode]::ManualReset");
            StringAssert.Contains(reference, "externalRngSynchronizationPolicy");
            StringAssert.Contains(reference, "Get-TraceSceneTimelineAudit");
            StringAssert.Contains(reference, "$externalRngBoundaryBalanced");
            StringAssert.Contains(
                reference,
                "externalSceneClockExclusionFrozenTimeUpdateCount");
            StringAssert.Contains(
                reference,
                "externalSceneClockExclusionPreSynchronizationCount");
            StringAssert.Contains(
                reference,
                "externalRngFirstGameplayReadyFramePhase");
            StringAssert.Contains(candidate, "$rngSynchronizationArmed");
            StringAssert.Contains(candidate, "replayDeterministicRngResetCount");
            StringAssert.Contains(
                candidate,
                "externalSceneClockExclusionFrozenTimeUpdateCount");
            StringAssert.Contains(
                candidate,
                "externalSceneClockExclusionPreSynchronizationCount");
            StringAssert.Contains(
                harness,
                "native.clock-rng-pause.override.experimental.v31");
        }

        [TestMethod]
        public void ExternalClockCalibratesDoublePhaseWithoutWritingGameState()
        {
            var payload = ReadClockPayloadSource("ClockController.cs");
            var observer = ReadReferenceObserverSource(
                "HollowKnightTASReferenceObserverMod.cs");
            var harness = ReadScript("T24ClockHarness.ps1");
            var reference = ReadScript("Invoke-T24ReferenceSmoke.ps1");
            var envelope = ReadScript("Measure-T24ReferenceEnvelope.ps1");

            StringAssert.Contains(
                payload,
                "MaximumDoublePhaseCalibrationAttempts = 128");
            StringAssert.Contains(payload, "Time.timeAsDouble");
            StringAssert.Contains(payload, "Time.fixedTimeAsDouble");
            StringAssert.Contains(
                payload,
                "doublePhaseFinalResidualBits != 0L");
            StringAssert.Contains(
                payload,
                "Time.captureDeltaTime = correctionFloat;");
            StringAssert.Contains(payload, "DoublePhaseFinalResidualBits");
            StringAssert.Contains(
                payload,
                "DoublePhaseDownwardQuantizationCount");
            StringAssert.Contains(
                payload,
                "DoublePhaseLastCorrectionBits");
            var correction = ReadClockPayloadSource(
                "DoublePhaseCorrection.cs");
            StringAssert.Contains(
                correction,
                "candidateDouble > target");
            StringAssert.Contains(
                correction,
                "SingleBits.ToSingle(candidateBits - 1)");
            AssertDoesNotContain(payload, "Time.time =");
            AssertDoesNotContain(payload, "Time.fixedTime =");
            AssertDoesNotContain(payload, "transform.position =");
            AssertDoesNotContain(payload, "body.velocity =");
            AssertDoesNotContain(payload, "Animator.Play(");
            StringAssert.Contains(
                observer,
                "externalDoublePhaseCalibrationApplied");
            StringAssert.Contains(
                observer,
                "RefreshExternalClockTelemetry(\n"
                + "                refreshDoublePhaseCalibration: true);");
            StringAssert.Contains(
                observer,
                "RefreshExternalClockTelemetry(\n"
                + "                refreshDoublePhaseCalibration: false);");
            StringAssert.Contains(
                observer,
                "if (refreshDoublePhaseCalibration)");
            StringAssert.Contains(
                harness,
                "externalDoublePhaseFinalResidualBits");
            StringAssert.Contains(reference, "clockHarnessScriptSha256");
            StringAssert.Contains(
                envelope,
                "doublePhaseMaximumAbsoluteResidualSeconds = 0d");
            AssertDoesNotContain(envelope, "-le 1e-12");
        }

        [TestMethod]
        public void ExactDoublePhaseCorrectionConvergesForObservedLattices()
        {
            var observedResidualBits = new[]
            {
                // Ordinary source run, not the original GG_Workshop fixture.
                BitConverter.DoubleToInt64Bits(203.06939740983887d - 203.05999546125531d),
                unchecked((long)0x3f87891738db0000UL),
                unchecked((long)0x3f882b9eb77b0000UL),
                unchecked((long)0x3f87b308a3db0000UL),
                unchecked((long)0x3dded80000000000UL)
            };
            var fixedDeltaTime = (float)0.02;
            var fixedDeltaTimeDouble = (double)fixedDeltaTime;

            foreach (var bits in observedResidualBits)
            {
                var residual = BitConverter.Int64BitsToDouble(bits);
                var correctionCount = 0;
                while (BitConverter.DoubleToInt64Bits(residual) != 0L
                    && correctionCount < 3)
                {
                    Assert.IsTrue(
                        HollowKnightTAS.ClockPayload.DoublePhaseCorrection
                            .TryCalculate(
                                residual,
                                fixedDeltaTime,
                                out var correction,
                                out _,
                                out var faultCode),
                        $"Correction failed with fault {faultCode} for {bits:x16}.");
                    residual += (double)correction;
                    if (residual >= fixedDeltaTimeDouble)
                    {
                        residual -= fixedDeltaTimeDouble;
                    }
                    correctionCount++;
                }

                Assert.AreEqual(
                    0L,
                    BitConverter.DoubleToInt64Bits(residual),
                    $"Residual {bits:x16} did not converge exactly.");
            }

            var failedResidual = BitConverter.Int64BitsToDouble(
                unchecked((long)0x3dded80000000000UL));
            Assert.IsTrue(
                HollowKnightTAS.ClockPayload.DoublePhaseCorrection.TryCalculate(
                    failedResidual,
                    fixedDeltaTime,
                    out var coarseCorrection,
                    out var coarseAdjusted,
                    out _));
            Assert.IsTrue(coarseAdjusted);
            Assert.AreEqual(
                unchecked((int)0x3ca3d709U),
                BitConverter.SingleToInt32Bits(coarseCorrection));

            var fineResidual = failedResidual + (double)coarseCorrection;
            Assert.IsTrue(
                HollowKnightTAS.ClockPayload.DoublePhaseCorrection.TryCalculate(
                    fineResidual,
                    fixedDeltaTime,
                    out var fineCorrection,
                    out var fineAdjusted,
                    out _));
            Assert.IsFalse(fineAdjusted);
            Assert.AreEqual(
                unchecked((int)0x30f09400U),
                BitConverter.SingleToInt32Bits(fineCorrection));
            Assert.AreEqual(
                0L,
                BitConverter.DoubleToInt64Bits(
                    fineResidual
                    + (double)fineCorrection
                    - fixedDeltaTimeDouble));
        }

        [TestMethod]
        public void CompletedFramePauseRequiresExternalWallClockExclusion()
        {
            var controller = ReadRuntimeSource(
                "Control",
                "RuntimePauseController.cs");
            var boundary = ReadRuntimeSource(
                "Control",
                "RuntimeVirtualClockBoundary.cs");
            var payload = ReadClockPayloadSource("ClockController.cs");
            var bridge = ReadNativeSource(
                "HollowKnightTAS.ClockBridge",
                "clock_bridge.c");

            StringAssert.Contains(
                controller,
                "!RuntimeVirtualClockBoundary.IsAvailable");
            StringAssert.Contains(
                controller,
                "RuntimeVirtualClockBoundary.BeginPause();");
            StringAssert.Contains(
                controller,
                "RuntimeVirtualClockBoundary.EndPause();");
            StringAssert.Contains(
                boundary,
                "native.clock.pause-wall-time-exclusion.experimental.v5");
            StringAssert.Contains(
                payload,
                "HktasClockBridge_BeginMainThreadPause");
            StringAssert.Contains(
                payload,
                "HktasClockBridge_EndMainThreadPause");
            StringAssert.Contains(
                payload,
                "HktasClockBridge_CommitMainThreadResume");
            StringAssert.Contains(
                payload,
                "HktasClockBridge_EnableDeterministicMainThreadClock");
            StringAssert.Contains(
                payload,
                "HktasClockBridge_AdvanceDeterministicFrameClock");
            StringAssert.Contains(
                payload,
                "HktasClockBridge_GetAbi() != 10u");
            StringAssert.Contains(payload, "if (bridgeStatus != 2)");
            StringAssert.Contains(payload, "if (result == -1)");
            StringAssert.Contains(payload, "result != 1 && result != 2");
            StringAssert.Contains(
                payload,
                "WaitForLastPresentationAndUpdateTime");
            StringAssert.Contains(
                payload,
                "TimeUpdateResumeBoundaryMarker");
            StringAssert.Contains(
                payload,
                "OnBeforeUnityTimeUpdate");
            StringAssert.Contains(
                payload,
                "UPlayerLoop.SetPlayerLoop(loop);");
            StringAssert.Contains(
                payload,
                "assembly.GetName().Name");
            StringAssert.Contains(payload, "\"HollowKnightTAS\"");
            AssertDoesNotContain(
                payload,
                "\"HollowKnightTAS.Runtime\",");
            StringAssert.Contains(
                bridge,
                "install_query_performance_counter_hook");
            StringAssert.Contains(
                bridge,
                "current_thread = GetCurrentThreadId();");
            StringAssert.Contains(
                bridge,
                "g_virtual_clock_main_thread_id != 0u");
            StringAssert.Contains(
                bridge,
                "current_thread == g_virtual_clock_main_thread_id");
            StringAssert.Contains(
                bridge,
                "g_virtual_clock_paused_ticks +=");
            StringAssert.Contains(
                bridge,
                "g_virtual_clock_resume_pending");
            StringAssert.Contains(
                bridge,
                "HktasClockBridge_CommitMainThreadResume");
            StringAssert.Contains(
                bridge,
                "TimeUpdate.WaitForLastPresentationAndUpdateTime");
            StringAssert.Contains(
                bridge,
                "g_virtual_clock_resume_request_count");
            StringAssert.Contains(
                bridge,
                "#define HKTAS_CLOCK_BRIDGE_ABI 10u");
            StringAssert.Contains(bridge, "configure_startup_latch");
            StringAssert.Contains(
                bridge,
                "HollowKnightTAS.T24.ClockPayloadReady.");
            StringAssert.Contains(
                bridge,
                "g_startup_handoff_adopt_count");
            StringAssert.Contains(
                bridge,
                "g_deterministic_clock_anchor.QuadPart +=");
            StringAssert.Contains(
                bridge,
                "g_deterministic_clock_frame_advance_count");
            AssertDoesNotContain(
                bridge,
                "HKTAS_CLOCK_RESUME_DEBOUNCE_MILLISECONDS");
            AssertDoesNotContain(
                bridge,
                "g_virtual_clock_resume_not_before");
            AssertDoesNotContain(controller, "Time.unscaledDeltaTime =");
            AssertDoesNotContain(payload, "Time.unscaledDeltaTime =");
        }

        [TestMethod]
        public void StartupClockStaysContinuousAndRootRealtimeConvergesExactly()
        {
            var bridge = ReadNativeSource(
                "HollowKnightTAS.ClockBridge",
                "clock_bridge.c");
            var injector = ReadClockInjectorSource("Program.cs");
            var payload = ReadClockPayloadSource("ClockController.cs");
            var observer = ReadReferenceObserverSource(
                "HollowKnightTASReferenceObserverMod.cs");
            var build = ReadScript("Build-T24ClockPrototype.ps1");
            var harness = ReadScript("T24ClockHarness.ps1");
            var reference = ReadScript("Invoke-T24ReferenceSmoke.ps1");
            var candidate = ReadScript("Invoke-T24CandidateSmoke.ps1");
            var envelope = ReadScript("Measure-T24ReferenceEnvelope.ps1");

            StringAssert.Contains(
                bridge,
                "g_deterministic_clock_anchor = now;");
            StringAssert.Contains(
                bridge,
                "g_deterministic_clock_enabled = TRUE;");
            AssertDoesNotContain(bridge, "HKTAS_CLOCK_STARTUP_ANCHOR_TICKS");
            AssertDoesNotContain(injector, "startup-anchor-ticks");
            AssertDoesNotContain(build, "startupDeterministicAnchorTicks");
            AssertDoesNotContain(harness, "startupDeterministicAnchorTicks");
            StringAssert.Contains(
                payload,
                "root-game-minus-rounded-startup-offset-qpc-grid-v3");
            StringAssert.Contains(
                payload,
                "MaximumRealtimeEpochNormalizationAttempts = 32");
            StringAssert.Contains(
                payload,
                "unquantizedTarget * frequency");
            StringAssert.Contains(
                payload,
                "targetClockTicks / (double)frequency");
            StringAssert.Contains(
                payload,
                "targetClockTicks - realtimeClockTicks");
            StringAssert.Contains(
                payload,
                "private static bool randomSynchronizationRequested;");
            StringAssert.Contains(
                payload,
                "randomSynchronizationRequested = true;");
            StringAssert.Contains(
                payload,
                "public static bool RandomSynchronizationRequested =>");
            StringAssert.Contains(
                payload,
                "|| !randomSynchronizationRequested)");
            AssertDoesNotContain(payload, "var explicitlyRequested =");
            StringAssert.Contains(
                observer,
                "private Type? TryResolveExternalClockControllerType()");
            StringAssert.Contains(
                observer,
                "\\\"externalRngSynchronizationRequested\\\"");
            StringAssert.Contains(
                payload,
                "RealtimeEpochNormalizationAfterBits");
            StringAssert.Contains(
                payload,
                "realtimeEpochNormalizationAfterBits\n"
                + "                    == realtimeEpochNormalizationTargetBits");
            StringAssert.Contains(
                payload,
                "deltaTicks = target > realtimeBefore ? 1L : -1L;");
            StringAssert.Contains(
                observer,
                "externalRealtimeEpochNormalizationAfterBits");
            StringAssert.Contains(
                reference,
                "$result.externalRealtimeEpochNormalizationAfterBits");
            StringAssert.Contains(
                candidate,
                "external-unity-startup-continuous-clock-v40-native-scene-lifecycle");
            StringAssert.Contains(
                envelope,
                "root-game-minus-rounded-startup-offset-qpc-grid-v3");
        }

        [TestMethod]
        public void SequentialPauseDwellUsesExternalWallClockWithoutAdvancingTicks()
        {
            var candidate = ReadScript("Invoke-T24CandidateSmoke.ps1");
            var sequential = ExtractMethod(
                candidate,
                "        if ($Mode -eq 'tas-sequential') {",
                "        elseif ($Mode -eq 'tas-batch') {");

            StringAssert.Contains(candidate, "[ValidateRange(0, 5000)]");
            StringAssert.Contains(
                sequential,
                "Start-Sleep -Milliseconds $PauseDwellMilliseconds");
            StringAssert.Contains(sequential, "$afterDwellState = Get-SdkState");
            StringAssert.Contains(
                sequential,
                "-eq $expectedTick");
            StringAssert.Contains(
                sequential,
                "external-wall-clock-dwell-with-lease-renewal-before-each-sequential-step-v2");
            StringAssert.Contains(sequential, "Renew-SdkLease");
            StringAssert.Contains(sequential, "renewalThresholdSeconds = 60");
            StringAssert.Contains(sequential, "minimumRequiredRenewalCount");
            StringAssert.Contains(candidate, "-CommandId 'renewControl'");
            StringAssert.Contains(candidate, "-and $pauseDwellClean");
            StringAssert.Contains(candidate, "pauseDwellAuditSha256");
            AssertDoesNotContain(sequential, "HktasCandidatePhysicalKeyboard");
            AssertDoesNotContain(sequential, "Time.timeScale");
            AssertDoesNotContain(sequential, "Time.unscaledDeltaTime");
        }

        [TestMethod]
        public void FrozenSequentialRegressionMovieContainsExactly120Ticks()
        {
            var path = Path.Combine(
                FindRepositoryRoot(),
                "fixtures",
                "t24",
                "attack-double-jump-120-v1.hktas");
            var text = File.ReadAllText(path);
            long ticks = 0;
            var commandCount = 0;
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("frames ", StringComparison.Ordinal))
                {
                    continue;
                }

                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                ticks += long.Parse(parts[1]);
                commandCount++;
            }

            Assert.AreEqual(120L, ticks);
            Assert.AreEqual(11, commandCount);
            StringAssert.Contains(
                text,
                "T24 input copied from committed no-TAS HeroActions");
        }

        [TestMethod]
        public void ClockBundleContractHashesEveryLoadedRuntimeFile()
        {
            var build = ReadScript("Build-T24ClockPrototype.ps1");
            var harness = ReadScript("T24ClockHarness.ps1");
            var reference = ReadScript("Measure-T24ReferenceEnvelope.ps1");
            var candidate = ReadScript("Invoke-T24CandidateSmoke.ps1");

            StringAssert.Contains(build, "schemaVersion = 2");
            StringAssert.Contains(build, "injectorManagedSha256");
            StringAssert.Contains(build, "injectorDepsSha256");
            StringAssert.Contains(build, "injectorRuntimeConfigSha256");
            StringAssert.Contains(build, "runtimeFileSetSha256");
            StringAssert.Contains(
                build,
                "HollowKnightTAS.ClockInjector.dll");
            StringAssert.Contains(
                harness,
                "[int]$manifest.schemaVersion -ne 2");
            StringAssert.Contains(harness, "$actualRuntimeFileSetSha256");
            StringAssert.Contains(harness, "$runtimeFileSetExact");
            StringAssert.Contains(harness, "injectorManagedSha256");
            StringAssert.Contains(reference, "injectorManagedSha256");
            StringAssert.Contains(reference, "runtimeFileSetSha256");
            StringAssert.Contains(candidate, "injectorManagedSha256");
            StringAssert.Contains(candidate, "runtimeFileSetSha256");
            StringAssert.Contains(candidate, "bridgeAbi = [int]$ClockAudit.bridgeAbi");
            StringAssert.Contains(reference, "bridgeAbi = [int]$clock.bridgeAbi");
        }

        [TestMethod]
        public void StartupClockLauncherIsOrderedHashBoundAndFailClosed()
        {
            var injector = ReadClockInjectorSource("Program.cs");
            var harness = ReadScript("T24ClockHarness.ps1");
            var startup = ExtractMethod(
                harness,
                "function Start-T24StartupClockedGame",
                "function Invoke-T24ClockInjection");

            StringAssert.Contains(injector, "CreateSuspended | CreateUnicodeEnvironment");
            StringAssert.Contains(injector, "QueueUserAPC(");
            Assert.IsTrue(
                injector.IndexOf("new[] { unityPlayerPath, bridgePath }", StringComparison.Ordinal)
                < injector.IndexOf("ResumeThread(created.Thread)", StringComparison.Ordinal));
            StringAssert.Contains(
                injector,
                "create-suspended-early-apc-unity-then-bridge-v1");
            StringAssert.Contains(injector, "SteamAppId");
            StringAssert.Contains(injector, "TerminateProcess(created.Process");
            StringAssert.Contains(startup, "$launcher.WaitForExit(15000)");
            StringAssert.Contains(startup, "loadLibraryAddressEquivalent");
            AssertDoesNotContain(startup, "-Wait `");
        }

        [TestMethod]
        public void PhaseCanonicalizationIgnoresOnlyObserverFrameBucket()
        {
            var reference = ReadScript("Measure-T24ReferenceEnvelope.ps1");
            var candidate = ReadScript("Invoke-T24CandidateSmoke.ps1");
            var parity = ReadScript("Test-T24HumanAiParity.ps1");
            var selfTest = ReadScript("Test-T24PhaseNormalization.ps1");

            AssertDoesNotContain(
                reference,
                "frameIndex = [int]$observation.frameIndex");
            AssertDoesNotContain(
                candidate,
                "frameIndex = [int]$left.frameIndex");
            AssertDoesNotContain(
                candidate,
                "frameIndex = [int]$right.frameIndex");
            AssertDoesNotContain(
                parity,
                "frameIndex = [int]$item.frameIndex");
            StringAssert.Contains(selfTest, "derivedFrameIndexEquivalent");
            StringAssert.Contains(selfTest, "visualTickMutationRejected");
            StringAssert.Contains(selfTest, "phaseMutationRejected");
            StringAssert.Contains(selfTest, "inputEdgeMutationRejected");
        }

        private static void AssertNoHeroGameplayWriter(string source)
        {
            AssertDoesNotContain(source, ".RegainControl(");
            AssertDoesNotContain(source, ".RelinquishControl(");
            AssertDoesNotContain(source, "body.position =");
            AssertDoesNotContain(source, "transform.position =");
            AssertDoesNotContain(source, "body.velocity =");
            AssertDoesNotContain(source, "Random.InitState(");
        }

        private static void AssertDoesNotContain(
            string source,
            string forbidden)
        {
            Assert.IsFalse(
                source.Contains(forbidden, StringComparison.Ordinal),
                "Unexpected forbidden source fragment: " + forbidden);
        }

        private static string ReadRuntimeSource(
            string directory,
            string fileName)
        {
            var root = FindRepositoryRoot();
            var path = Path.Combine(
                root,
                "src",
                "HollowKnightTAS.Runtime",
                directory,
                fileName);
            Assert.IsTrue(File.Exists(path), "Runtime source not found: " + path);
            return File.ReadAllText(path);
        }

        private static string ReadReferenceObserverSource(string fileName)
        {
            var root = FindRepositoryRoot();
            var path = Path.Combine(
                root,
                "src",
                "HollowKnightTAS.ReferenceObserver",
                fileName);
            Assert.IsTrue(
                File.Exists(path),
                "Reference observer source not found: " + path);
            return File.ReadAllText(path);
        }

        private static string ReadCompanionSource(
            string directory,
            string fileName)
        {
            var root = FindRepositoryRoot();
            var path = Path.Combine(
                root,
                "src",
                "HollowKnightTAS.Companion",
                directory,
                fileName);
            Assert.IsTrue(
                File.Exists(path),
                "Companion source not found: " + path);
            return File.ReadAllText(path);
        }

        private static string ReadCoreSource(
            string directory,
            string fileName)
        {
            var root = FindRepositoryRoot();
            var path = Path.Combine(
                root,
                "src",
                "HollowKnightTAS.Core",
                directory,
                fileName);
            Assert.IsTrue(File.Exists(path), "Core source not found: " + path);
            return File.ReadAllText(path);
        }

        private static string ReadGameObservationSource(string fileName)
        {
            var root = FindRepositoryRoot();
            var path = Path.Combine(
                root,
                "src",
                "HollowKnightTAS.GameObservation",
                fileName);
            Assert.IsTrue(
                File.Exists(path),
                "Game observation source not found: " + path);
            return File.ReadAllText(path);
        }

        private static string ReadClockPayloadSource(string fileName)
        {
            var root = FindRepositoryRoot();
            var path = Path.Combine(
                root,
                "src",
                "HollowKnightTAS.ClockPayload",
                fileName);
            Assert.IsTrue(
                File.Exists(path),
                "Clock payload source not found: " + path);
            return File.ReadAllText(path);
        }

        private static string ReadClockInjectorSource(string fileName)
        {
            var root = FindRepositoryRoot();
            var path = Path.Combine(
                root,
                "src",
                "HollowKnightTAS.ClockInjector",
                fileName);
            Assert.IsTrue(
                File.Exists(path),
                "Clock injector source not found: " + path);
            return File.ReadAllText(path);
        }

        private static string ReadNativeSource(
            string directory,
            string fileName)
        {
            var root = FindRepositoryRoot();
            var path = Path.Combine(
                root,
                "native",
                directory,
                fileName);
            Assert.IsTrue(
                File.Exists(path),
                "Native source not found: " + path);
            return File.ReadAllText(path);
        }

        private static string ReadScript(string fileName)
        {
            var root = FindRepositoryRoot();
            var path = Path.Combine(root, "scripts", fileName);
            Assert.IsTrue(File.Exists(path), "Script not found: " + path);
            return File.ReadAllText(path);
        }

        private static string FindRepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "HollowKnightTAS.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            Assert.Fail(
                "Could not locate HollowKnightTAS.sln from "
                + AppContext.BaseDirectory);
            return string.Empty;
        }

        private static string ExtractMethod(
            string source,
            string startMarker,
            string endMarker)
        {
            var start = source.IndexOf(startMarker, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "Missing method marker: " + startMarker);
            var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
            Assert.IsTrue(end > start, "Missing method end marker: " + endMarker);
            return source.Substring(start, end - start);
        }
    }
}
