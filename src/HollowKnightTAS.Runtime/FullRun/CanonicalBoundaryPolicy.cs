using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Newtonsoft.Json;

namespace HollowKnightTAS.Runtime.FullRun
{
    // One policy instance owns startup and every unloaded boundary for all
        // execution paths. The protected Phase 1 route trace observes the same
        // Canon without performing Phase 2 preparation or qualifying a checkpoint.
    internal sealed class CanonicalBoundaryPolicy : IDisposable
    {
        private readonly CanonicalHeroStartupWarmup startup;
        private readonly CanonicalHudStartupWarmup hudStartup = new CanonicalHudStartupWarmup();
        private readonly CanonicalHudParticlePolicy hudParticles = new CanonicalHudParticlePolicy();
        private readonly CanonicalHudJournalPolicy journal = new CanonicalHudJournalPolicy();
        private readonly CanonicalHudCharmPolicy charms = new CanonicalHudCharmPolicy();
        private readonly CanonicalBoundaryPoolPolicy pools = new CanonicalBoundaryPoolPolicy();
        private readonly CanonicalVisualFsmTemplates visualTemplates = new CanonicalVisualFsmTemplates();
        private readonly CanonicalHeroAssetTemplates assetTemplates = new CanonicalHeroAssetTemplates();
        private LoadingGameplayOracle? observer;
        private string? observerSession;
        private long? observerInputOrigin;
        private CanonicalCarryCapture? carry;
        private bool inputOriginRestored;
        private readonly bool phaseOneTrace;
        private readonly CanonicalRetiredMeshPolicy retiredMeshes;
        private LoadingGeoIncomingReferences? pendingGeoIncoming;
        private readonly Dictionary<int, string> retiringGeo = new Dictionary<int, string>();
        internal string ActualTargetStage => carry?.ActualTargetStage ?? "no-carry";
        internal byte[]? ExperimentalSource { get; private set; }
        internal CanonicalSealedSource<CanonicalActualDExperiment> SealSourceSession(CanonicalActualDExperiment.SessionCarry session)
        {
            var source = CanonicalActualDExperiment.Decode(ExperimentalSource ?? throw new InvalidOperationException("Missing actual D source."));
            if (source.Session != null) throw new InvalidOperationException("Actual D source was already sealed.");
            source.Session = session;
            var sealedSource = new CanonicalSealedSource<CanonicalActualDExperiment>(source, value => value.Encode(), value =>
                JsonConvert.SerializeObject(new { value.Schema, value.Ordinal, value.MovieFrame, value.NativeFrame, value.Clock, value.Session }));
            ExperimentalSource = sealedSource.Bytes;
            return sealedSource;
        }
        internal Action PrepareActualTargetD(CanonicalActualDExperiment source, HollowKnightTAS.Core.Movie.MovieV2Document movie,
            long targetNative, long origin, CanonicalInputEpoch controlOrigin,
            HollowKnightTAS.Runtime.Input.FullRunActionSetAdapter input, HollowKnightTAS.Runtime.Input.FullRunMouseBridge mouse,
            Func<bool> admission, Action<CanonicalInputEpoch> restoreEpoch, PlayerData originalBootstrapPlayer,
            GameManager.EnterSceneEvent? originalTitleCallback)
        {
            if (!CanonicalActualDExperiment.Enabled || observer == null || carry == null || !admission())
                throw new InvalidOperationException("Actual D target has no completed original Canon.");
            var prepareTimer = Stopwatch.StartNew();
            long prepareMark = 0;
            void PreparedPhase(string phase) {
                var now = prepareTimer.ElapsedMilliseconds;
                Modding.Logger.Log("[HKTAS] Actual D target preparation timing: phase=" + phase + ";elapsedMilliseconds=" + (now - prepareMark));
                prepareMark = now;
            }
            pendingGeoIncoming = observer.FreezeGeoIncoming();
            var epoch = CanonicalInputEpochRuntime.Capture(origin, targetNative, controlOrigin);
            var identities = new BoundaryObjectIdentity();
            PreparedPhase("frozen-owner-context");
            var sourceGraph = carry.DecodeActualMainForOwnerContext(source);
            PreparedPhase("decode-source-main");
            var detached = Array.Exists(sourceGraph.Roots, root => root.Binding == CanonicalSceneMappingCarry.DetachedPlayerRoot);
            carry.Capture(identities, epoch, input, mouse, observer.BoundarySchedulerCarry(epoch), observer.BoundaryHeroInvulnerabilityCarry(epoch),
                targetDirectoryOnly: true, originalBootstrapPlayer: originalBootstrapPlayer, sourceRequiresDetachedPlayer: detached,
                retainedTargetTitleCallback: originalTitleCallback);
            PreparedPhase("target-owner-bindings");
            var apply = carry.PrepareActualRestore(source, movie, admission, data => observer.PrepareActualSchedulerVerification(data, admission));
            PreparedPhase("prepare-main-and-input");
            return () => {
                apply();
                var savedEpoch = carry.PreparedRestoreEpoch ?? throw new InvalidOperationException("Actual D source epoch is absent.");
                restoreEpoch(savedEpoch);
                savedEpoch.RequireReadback(CanonicalInputEpochRuntime.Capture(savedEpoch.OriginNative, source.NativeFrame, savedEpoch), source.NativeFrame);
            };
        }
        private void RestoreHudSourceDependencies(CanonicalSchedulerCarry sourceScheduler, Func<bool> admitted)
        {
            void Require() { if (!admitted()) throw new InvalidOperationException("HUD derivation lost paused source admission."); }
            Require();
            var current = observer ?? throw new InvalidOperationException("HUD derivation has no original observer.");
            var sourceSubset = CanonicalSchedulerOwnedStartPartition.WithoutOwnedStarts(sourceScheduler, charms.SourceStartPrefix());
            var retired = current.PrepareOwnedHudLifecycle(charms.OldOwners(), charms.OwnsNewStart, out var retireStarts);
            LoadingSerialSceneProbe.DeriveProtectedSourceCharmDirectory(charms, retiredMeshes, retireStarts, () => {
                current.RecaptureOwnedHudScheduler(Require, retired);
                current.PrepareActualScheduler(sourceSubset, admitted)();
                current.RecaptureHudSchedulerAfterScalarWrite(Require);
                var born = current.PrepareOwnedHudLifecycle(charms.OldOwners(), charms.OwnsNewStart, out var noOldStarts);
                noOldStarts();
                charms.BuildCurrent();
                current.RecaptureOwnedHudScheduler(Require, born);
            }, Require);
            Require();
        }
        private void RestoreGeoSourceDependencies(CanonicalSchedulerCarry sourceScheduler, Func<bool> admitted,
            Func<IReadOnlyDictionary<string, object>, IReadOnlyDictionary<string, object>, IReadOnlyDictionary<string, object>> rebindGraphs,
            Action<Action> applyGeoSource, Action beginOriginalLifecycle, Action requireGeoNative)
        {
            void Require() { if (!admitted()) throw new InvalidOperationException("Geo producer lost its original paused admission."); }
            LoadingSerialSceneProbe.DeriveProtectedSourceGeoDirectory(() => {
            Require();
            var current = observer ?? throw new InvalidOperationException("Geo producer has no original observer.");
            var incoming = pendingGeoIncoming ?? throw new InvalidOperationException("Geo producer has no frozen full incoming reader.");
            pendingGeoIncoming = null;
            var old = GameCameras.instance.geoCounter;
            void Stage(string phase) { carry!.ActualTargetStage = phase; Require(); }
            Stage("geo-old-owner-directory");
            var oldDirectory = CanonicalGeoColdTemplate.ManagedDirectory(old);
            var owners = old.GetComponentsInChildren<MonoBehaviour>(true).ToDictionary(owner => owner.GetInstanceID());
            GeoCounter? created = null;
            bool OwnsNew(int id) => created != null && created.GetComponentsInChildren<MonoBehaviour>(true).Any(owner => owner.GetInstanceID() == id);
            Stage("geo-owned-lifecycle-admission");
            var transition = current.PrepareOwnedHudLifecycle(owners, OwnsNew, out var retireStarts);
            Stage("geo-original-inactive-producer");
            created = hudStartup.GeoTemplate.ProduceInactive();
            Stage("geo-new-owner-directory");
            var newDirectory = rebindGraphs(oldDirectory, CanonicalGeoColdTemplate.ManagedDirectory(created));
            Stage("geo-incoming-source-aliases");
            var incomingPlan = incoming.Prepare(newDirectory);
            incomingPlan.Apply();
            retiringGeo.Clear();
            foreach (var pair in CanonicalGeoColdTemplate.Directory(old.gameObject))
                if (pair.Value is Component component) retiringGeo.Add(component.GetInstanceID(), "canonical-geo-retirement/" + pair.Key);
            Stage("geo-original-old-owner-retirement");
            beginOriginalLifecycle();
            UnityEngine.Object.DestroyImmediate(old.gameObject);
            retireStarts();
            retiredMeshes.Flush(identity => identity.StartsWith("canonical-geo-retirement/", StringComparison.Ordinal));
            Stage("geo-original-produced-activation");
            hudStartup.GeoTemplate.ActivateProduced(created);
            Stage("geo-independent-scheduler-readback");
            current.RecaptureOwnedHudScheduler(Require, transition);
            if (!ReferenceEquals(GameCameras.instance.geoCounter, created) || !ReferenceEquals(HeroController.SilentInstance.geoCounter, created))
                throw new InvalidOperationException("Geo producer real camera/Hero entry alias differs after original lifecycle.");
            applyGeoSource(incomingPlan.VerifyReadback);
            Stage("geo-original-source-empty-start-queue");
            var producedOwners = created.GetComponentsInChildren<MonoBehaviour>(true).ToDictionary(owner => owner.GetInstanceID());
            if (producedOwners.Keys.Any(owners.ContainsKey)) throw new InvalidOperationException("Geo producer reused an old lifecycle owner.");
            var ownerNames = current.PrepareProducedGeoSchedulerNames(created, producedOwners);
            var queueRestored = current.RestoreAbsentProducedGeoStarts(sourceScheduler, producedOwners,
                ownerNames.Prefix, ownerNames.Names, ownerNames.Guard, Require, requireGeoNative);
            current.RecaptureHudSchedulerAfterQueueWrite(Require, queueRestored);
            // Original OnAfterDeserialize/InitData legitimately populated the
            // registered metadata caches after main D. Return these Canon-owned
            // containers to the same verified empty form used by every Canon.
            Stage("geo-canonical-action-metadata");
            var metadata = CanonicalBoundaryStaticPolicy.ResetActionMetadata();
            incomingPlan.VerifyReadback();
            Require();
            Modding.Logger.Log("[HKTAS] Geo post-main source installation readback passed: finalPreparedIncomingAliases=true;"
                + "originalLifecycleSchedulerRetirement=true;metadataRecomputedEqual=" + metadata.RecomputedEqual
                + ";metadataAssembliesEqual=" + metadata.LoadedAssembliesEqual + ";completeStateCoverage=false");
            }, Require);
        }
        internal CanonicalBoundaryPolicy()
        {
            CanonicalPhaseOneTrace.RequireContext(Environment.GetEnvironmentVariable);
            phaseOneTrace = CanonicalPhaseOneTrace.Enabled;
            retiredMeshes = new CanonicalRetiredMeshPolicy(id => pools.RetiredOwner(id) ?? journal.RetiringOwner(id) ?? charms.RetiringOwner(id)
                ?? (retiringGeo.TryGetValue(id, out var geoOwner) ? geoOwner : null) ?? LoadingSerialSceneProbe.RetiringSourceOwner(id));
            startup = new CanonicalHeroStartupWarmup(assetTemplates.BeforeHeroStart);
        }
        internal bool AdvanceStartup() => startup.Advance() && hudStartup.Advance();
        internal void RebindProtectedClock(int ordinal, Action admission)
        {
            if (!phaseOneTrace || !CanonicalClockApplyExperiment.Enabled || observer == null)
                throw new InvalidOperationException("Clock binding requires its actual observed Canon.");
            admission();
            var elapsed = Stopwatch.StartNew();
            var frozenQueue = observer.RequireFrozenS0Scheduler();
            var before = observer.RecaptureBoundaryS0();
            var beforeMilliseconds = elapsed.ElapsedMilliseconds;
            visualTemplates.RebindEnteredLightClock(admission);
            journal.RebindEnteredClock(admission);
            frozenQueue(); admission();
            var after = observer.RecaptureBoundaryS0();
            var afterMilliseconds = elapsed.ElapsedMilliseconds;
            var comparison = CanonicalS0Comparison.Compare(before, after);
            var directory = Path.Combine(Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT")!, "clock-rebound");
            Directory.CreateDirectory(directory);
            CanonicalJsonEvidence.Write(Path.Combine(directory, "s0-clock-binding-before.json"), before);
            CanonicalJsonEvidence.Write(Path.Combine(directory, "s0-observation-" + ordinal + ".json"), after);
            File.WriteAllText(Path.Combine(directory, "s0-clock-binding-audit.json"), JsonConvert.SerializeObject(new {
                comparison, originalQueueBytesUnchanged = true, derivedFromImportedClock = true, restoreQualified = false }));
            Modding.Logger.Log("[HKTAS] Actual D clock binding timing: beforeReadMilliseconds=" + beforeMilliseconds
                + ";bindingAndAfterReadMilliseconds=" + (afterMilliseconds - beforeMilliseconds)
                + ";comparisonAndEvidenceMilliseconds=" + (elapsed.ElapsedMilliseconds - afterMilliseconds));
            if (!comparison.ObservedS0Equal || comparison.ClockDependencyDifferences.Length > 3
                || after.Unclassified.Length != 0 || after.Unread.Length != 0 || after.InvariantFailures.Length != 0)
                throw new InvalidOperationException("Clock binding changed another registered S0 field or its contract.");
        }

        internal void Apply(int ordinal, string source, string target, long movie, long native, string sessionDirectory, long movieInputNativeOrigin,
            CanonicalInputEpoch inputControlOrigin, HollowKnightTAS.Runtime.Input.FullRunActionSetAdapter input, HollowKnightTAS.Runtime.Input.FullRunMouseBridge mouse,
            string? evidenceStage = null)
        {
            if (movieInputNativeOrigin < 0 || native < movieInputNativeOrigin || observerInputOrigin.HasValue && observerInputOrigin.Value != movieInputNativeOrigin)
                throw new InvalidOperationException("Canonical boundary input frame origin is absent, changed or ahead of the native cursor.");
            observerInputOrigin = movieInputNativeOrigin;
            if (!startup.CompleteFor(HeroController.SilentInstance))
                throw new InvalidOperationException("Canonical boundary preceded registered Hero warmup.");
            var elapsed = Stopwatch.StartNew();
            var directory = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT")
                ?? throw new InvalidOperationException("Canonical boundary diagnostic directory is absent.");
            if (evidenceStage != null) {
                if (evidenceStage != "clock-rebound" || !phaseOneTrace || !CanonicalClockApplyExperiment.Enabled)
                    throw new InvalidOperationException("Unknown protected Canon evidence stage.");
                directory = Path.Combine(directory, evidenceStage); Directory.CreateDirectory(directory);
            }
            void Progress(string phase, object detail) => File.AppendAllText(Path.Combine(directory, "canonical-boundary-progress.jsonl"),
                JsonConvert.SerializeObject(new { ordinal, source, target, movie, native, phase, detail,
                    elapsedMilliseconds = elapsed.ElapsedMilliseconds, completeStateCoverage = false }) + "\n");
            Progress("begin", new { movieInputNativeOrigin, controlledNativeFrame = checked(native - movieInputNativeOrigin) });
            Progress("original-Geo-lifecycle-before-Canon", LoadingGeoLifecycleObservation.Capture());
            Progress("original-Map-lifecycle-before-Canon", LoadingMapLifecycleObservation.Capture());
            try { carry ??= new CanonicalCarryCapture(assetTemplates, visualTemplates, journal, RestoreHudSourceDependencies, RestoreGeoSourceDependencies); }
            catch (Exception error) {
                Progress("D-field-registry-rejected", new { type = error.GetType().FullName, detail = error.Message });
                throw;
            }
            CanonicalDataPreservationGuard preservation;
            try { preservation = carry.PrepareDataPreservationGuard(); }
            catch (Exception error) {
                Progress("D-preservation-preparation-rejected", new { type = error.GetType().FullName, detail = error.Message });
                throw;
            }
            var destroyed = pools.Reset();
            Progress("R1-pools-rebuilt", new { destroyed });
            Progress("R1-original-journal-directory-rebuilt", journal.Reset());
            Progress("R1-source-and-pool-meshes-retired", new { removedDelayedDestroyCallbacks = retiredMeshes.Flush() });
            var stopped = CanonicalBoundaryVisualPolicy.Reset(visualTemplates, out var removedTweenDestroyCallbacks);
            Progress("R2-registered-visual-stop", new { stopped, removedTweenDestroyCallbacks });
            // Source-dependent generated HUD comes after the registered visual
            // reset Starts on every path, including actual D re-derivation.
            Progress("R2-original-equipped-charms-derived", charms.Reset());
            Progress("R2-equipped-charm-meshes-retired", new { removedDelayedDestroyCallbacks = retiredMeshes.Flush(
                owner => owner.StartsWith("canonical-equipped-charm-retirement/", StringComparison.Ordinal)) });
            Progress("R2-six-owned-HUD-particles-cleared-and-armed", new { count = hudParticles.Reset() });
            Progress("R2-original-Hero-Fall-Trail-stopped-and-cleared", CanonicalHeroFallTrailPolicy.Reset());
            Progress("R2-checkpoint-icon-idle-timer", CanonicalCheckpointIconPolicy.ResetIdleTimer());
            var identities = new BoundaryObjectIdentity();
            var carryFsmBindings = CanonicalHeroFsmBindings.Validate(identities);
            Progress("D-Hero-FSM-bindings-validated", new { carryFsmBindings, canonicalVisualBindings = 2 });
            var assetOrigins = assetTemplates.Validate(identities);
            File.WriteAllText(Path.Combine(directory, "canonical-asset-origins.json"), JsonConvert.SerializeObject(new {
                schema = 1, diagnosticOnly = true, completeStateCoverage = false, origins = assetOrigins }));
            Progress("D-Hero-asset-origins-validated", new { assets = assetOrigins.Length });
            var broadphase = CheckpointPhysicsBroadphaseState.Canonicalize(identities);
            Progress("R4-broadphase-readback", new { broadphase.Count, capacity = broadphase.Nodes.Length, broadphase.ProxyCount });
            Progress("R4-unused-polygon-capacity-readback", new { polygons = CheckpointPhysicsBroadphaseState.CanonicalizePolygonCapacity() });
            CanonicalBoundaryVisualPolicy.ResetFsmOrder(identities);
            CanonicalBoundaryStaticPolicy.ResetExecutionStack();
            Progress("R6-FSM-execution-stack-reset", new { });
            var metadataAudit = CanonicalBoundaryStaticPolicy.ResetActionMetadata();
            Progress("R6-action-metadata-caches-reset", metadataAudit);
            Progress("R6-SDK-device-type-table-reset", CanonicalBoundaryInputTablePolicy.Reset());
            try { preservation.Verify(); }
            catch (Exception error) {
                Progress("D-preservation-readback-rejected", new { type = error.GetType().FullName, detail = error.Message });
                throw;
            }
            Progress("D-gameplay-preserved-by-Canon", new { playerData = true, sceneData = true, heroState = true,
                heroScalarsPoseBodyAndHistory = true, fullNativeStateQualified = false });
            var canonMilliseconds = elapsed.Elapsed.TotalMilliseconds;
            if (observer == null)
            {
                observer = LoadingGameplayOracle.CreateBoundaryObserver(sessionDirectory, movieInputNativeOrigin);
                observerSession = sessionDirectory;
            }
            if (!StringComparer.Ordinal.Equals(observerSession, sessionDirectory))
                throw new InvalidOperationException("Canonical observer cannot change its protected session binding.");
            bool OwnsCanonicalStart(int instance) => pools.OwnsNewPoolStart(instance) || visualTemplates.OwnsResetStart(instance) || journal.OwnsNewStart(instance) || charms.OwnsNewStart(instance);
            string? RetiredOwner(int instance) => pools.RetiredOwner(instance) ?? LoadingSerialSceneProbe.RetiredSourceOwner(instance);
            var inputEpoch = CanonicalInputEpochRuntime.Capture(movieInputNativeOrigin, native, inputControlOrigin);
            inputEpoch.RequireControlOrigin();
            var digest = observer.CaptureBoundaryFrame(movie, native, OwnsCanonicalStart, RetiredOwner);
            File.WriteAllText(Path.Combine(directory, "boundary-digest-" + ordinal + ".json"), JsonConvert.SerializeObject(digest));
            var s0 = observer.BoundaryS0Observation();
            File.WriteAllText(Path.Combine(directory, "s0-observation-" + ordinal + ".json"), JsonConvert.SerializeObject(s0));
            if (CanonicalClockApplyExperiment.Enabled)
                File.WriteAllText(Path.Combine(directory, "clock-d-source-" + ordinal + ".json"),
                    JsonConvert.SerializeObject(CanonicalClockApplyExperiment.Capture(native)));
            File.WriteAllText(Path.Combine(directory, "boundary-coverage-catalog.json"), JsonConvert.SerializeObject(observer.BoundaryCoverageCatalog()));
            File.WriteAllText(Path.Combine(directory, "boundary-coverage-reader-audit.json"), JsonConvert.SerializeObject(observer.BoundaryCoverageReadAudit()));
            var fieldSchema = JsonConvert.SerializeObject(new { schema = 1, diagnosticOnly = true, ordinal,
                completeStateCoverage = false, fields = observer.BoundaryFieldSchema() });
            if (System.Text.Encoding.UTF8.GetByteCount(fieldSchema) > 16 * 1024 * 1024)
                throw new InvalidOperationException("Boundary field schema exceeds 16 MiB.");
            File.WriteAllText(Path.Combine(directory, "boundary-field-schema.json"), fieldSchema);
            Progress("boundary-digest-captured", new { digest.FullSha256, roots = digest.Roots.Length, unsupported = digest.Unsupported.Length });
            File.WriteAllText(Path.Combine(directory, "boundary-detached-fsm-" + ordinal + ".json"),
                JsonConvert.SerializeObject(observer.BoundaryDetachedFsmDiagnostic()));
            if (digest.CanonicalWaitIssues.Length != 0)
                throw new InvalidOperationException("Canonical scheduler registry is incomplete: " + string.Join(", ", digest.CanonicalWaitIssues));
            // Retain all raw data. A separate, explicitly D0-only projection
            // compares process cursor/stamp coordinates relative to StartInputAt.
            File.WriteAllText(Path.Combine(directory, "boundary-epoch-" + ordinal + ".json"), JsonConvert.SerializeObject(new {
                schema = 1, diagnosticOnly = true, d2ProjectionAllowed = false, epoch = inputEpoch }));
            File.WriteAllText(Path.Combine(directory, "boundary-logical-digest-" + ordinal + ".json"),
                JsonConvert.SerializeObject(CanonicalBoundaryEpochObservation.Project(digest, inputEpoch)));
            if (phaseOneTrace)
            {
                if (CanonicalActualDExperiment.CaptureSource && ordinal == 1)
                {
                    var data = carry.Capture(identities, inputEpoch, input, mouse,
                        observer.BoundarySchedulerCarry(inputEpoch), observer.BoundaryHeroInvulnerabilityCarry(inputEpoch));
                    if (data.Unsupported.Length != 0 || carry.InputGraph!.Unsupported.Length != 0)
                        throw new InvalidOperationException("Actual D source has unread or unsupported managed data: "
                            + string.Join(", ", data.Unsupported));
                    ExperimentalSource = new CanonicalActualDExperiment { Ordinal = ordinal, MovieFrame = movie, NativeFrame = native,
                        Main = carry.Encode(data), Input = carry.EncodeInput(), Geo = carry.GeoGraph, Map = carry.MapGraph, Clock = CanonicalClockApplyExperiment.Capture(native) }.Encode();
                    Progress("actual-D-source-captured-in-memory", new { bytes = ExperimentalSource.Length, heroAnimationCallbacks = carry.HeroAnimationCallbackAudit, restoreQualified = false,
                        blockingQualifications = carry.BlockingQualifications });
                }
                // Keep all observed coverage gaps. This permits route discovery,
                // never a full-state PASS, a payload application or a snapshot.
                // Actual Canon/preservation/wait/readback failures still throw.
                if (ordinal == 1)
                {
                    var repeated = observer.CaptureBoundaryFrame(movie, native, OwnsCanonicalStart, RetiredOwner);
                    var repeatedS0 = observer.BoundaryS0Observation();
                    var s0Comparison = CanonicalS0Comparison.Compare(s0, repeatedS0);
                    File.WriteAllText(Path.Combine(directory, "s0-observation-" + ordinal + "-repeat.json"), JsonConvert.SerializeObject(repeatedS0));
                    File.WriteAllText(Path.Combine(directory, "s0-observer-audit.json"), JsonConvert.SerializeObject(s0Comparison));
                    if (!s0Comparison.ObservedEqual) throw new InvalidOperationException("S0 reader changed frozen state.");
                    var comparison = BoundaryDigestComparison.Compare(digest, repeated);
                    File.WriteAllText(Path.Combine(directory, "boundary-digest-" + ordinal + "-repeat.json"), JsonConvert.SerializeObject(repeated));
                    File.WriteAllText(Path.Combine(directory, "boundary-observer-audit.json"), JsonConvert.SerializeObject(comparison));
                    if (!comparison.ObservedEqual) throw new InvalidOperationException("Boundary reader changed frozen observed state: " + comparison.FirstDifference);
                }
                Progress("phase1-route-observed-unqualified", new { canonMilliseconds, observerMilliseconds = elapsed.Elapsed.TotalMilliseconds - canonMilliseconds,
                    diagnosticOnly = true, phase2PreparationExecuted = false, d0Passed = false, restoreQualified = false,
                    coverageUnsupported = digest.Unsupported, pending = new[] { "R2-remaining-visual-FSM-fields", "R6-static-canonical-or-carry-fields",
                        "BoundaryDigest-native-and-asset-coverage", "closed-D-field-registry" } });
                return;
            }
            CanonicalCarryGraphData candidate;
            try { candidate = carry.Capture(identities, inputEpoch, input, mouse, observer.BoundarySchedulerCarry(inputEpoch), observer.BoundaryHeroInvulnerabilityCarry(inputEpoch)); }
            catch (Exception error) {
                Progress("D-candidate-data-rejected", new { type = error.GetType().FullName, detail = error.Message });
                throw;
            }
            finally {
                // Retain completed domain audits when a later graph/layout
                // guard rejects. Audit availability never implies full D.
                File.WriteAllText(Path.Combine(directory, "canonical-clock-carry-audit.json"), JsonConvert.SerializeObject(new {
                    schema = 1, diagnosticOnly = true, completeStateCoverage = false, clock = carry.ClockCarryAudit }));
                File.WriteAllText(Path.Combine(directory, "canonical-input-carry-audit.json"), JsonConvert.SerializeObject(new {
                    schema = 1, diagnosticOnly = true, completeStateCoverage = false, input = carry.InputCarryAudit,
                    preparedLeaseActions = carry.InputPreparedLeaseActions, preparedCursor = carry.InputPreparedCursor,
                    preparedManagerScalars = carry.InputPreparedManagerScalars,
                    preparedCallbacks = carry.InputPreparedCallbacks,
                    applicationExecuted = false }));
            }
            File.WriteAllText(Path.Combine(directory, "canonical-input-epoch.json"), JsonConvert.SerializeObject(new {
                schema = 1, diagnosticOnly = true, completeStateCoverage = false, inputEpoch,
                controlledNativeFrame = inputEpoch.NativeFramesFromOrigin,
                tickAge = inputEpoch.TickAge(inputEpoch.OriginTick), schedulerAge = inputEpoch.SchedulerAge(inputEpoch.OriginSchedulerTimestamp) }));
            var candidateBytes = carry.Encode(candidate);
            var inputBytes = carry.EncodeInput();
            if ((long)candidateBytes.Length + inputBytes.Length > 32 * 1024 * 1024)
                throw new InvalidOperationException("Combined canonical D exceeds its 32 MiB bound.");
            File.WriteAllBytes(Path.Combine(directory, "canonical-carry-" + ordinal + ".bin"), candidateBytes);
            File.WriteAllBytes(Path.Combine(directory, "canonical-input-carry-" + ordinal + ".bin"), inputBytes);
            File.WriteAllText(Path.Combine(directory, "canonical-event-binding-audit.json"), JsonConvert.SerializeObject(new {
                schema = 1, diagnosticOnly = true, completeStateCoverage = false, events = carry.EventBindingAudit }));
            File.WriteAllText(Path.Combine(directory, "canonical-event-globals.json"), JsonConvert.SerializeObject(new {
                schema = 1, diagnosticOnly = true, completeStateCoverage = false, events = carry.EventGlobalsAudit,
                contract = carry.EventGraphAudit }));
            File.WriteAllText(Path.Combine(directory, "canonical-event-data-audit.json"), JsonConvert.SerializeObject(new {
                schema = 1, diagnosticOnly = true, completeStateCoverage = false, eventData = carry.EventDataAudit,
                canonicalSenders = carry.CanonicalSenderAudit }));
            File.WriteAllText(Path.Combine(directory, "canonical-clock-carry-audit.json"), JsonConvert.SerializeObject(new {
                schema = 1, diagnosticOnly = true, completeStateCoverage = false, clock = carry.ClockCarryAudit }));
            File.WriteAllText(Path.Combine(directory, "canonical-input-carry-audit.json"), JsonConvert.SerializeObject(new {
                schema = 1, diagnosticOnly = true, completeStateCoverage = false, input = carry.InputCarryAudit,
                preparedLeaseActions = carry.InputPreparedLeaseActions, preparedCursor = carry.InputPreparedCursor,
                preparedManagerScalars = carry.InputPreparedManagerScalars,
                preparedCallbacks = carry.InputPreparedCallbacks,
                applicationExecuted = false }));
            Progress("D-candidate-data-captured", new { roots = candidate.Roots.Length, nodes = candidate.Nodes.Length,
                externalBindings = candidate.Externals.Length, unsupported = candidate.Unsupported, bytes = candidateBytes.Length,
                sceneLoadScalarsPrepared = carry.SceneLoadScalarsPrepared, sceneLoadApplicationExecuted = false,
                nativeSchedulerPrepared = observer.BoundarySchedulerRestorePrepared, nativeSchedulerApplicationExecuted = false,
                mainGraphPrepared = carry.MainGraphPrepared, mainGraphApplicationExecuted = false,
                gameplayPrepared = carry.GameplayPrepared, gameplayApplicationExecuted = false,
                eventStaticSlotsGuarded = carry.EventStaticSlotsGuarded,
                invulnerabilityPrepared = carry.InvulnerabilityPrepared, invulnerabilityApplicationExecuted = false,
                sceneMappingPrepared = carry.SceneMappingPrepared, sceneMappingCallbacks = carry.SceneMappingCallbacks, sceneMappingApplicationExecuted = false,
                detachedMappingPlayerCarried = Array.Exists(candidate.Roots, root => root.Binding == CanonicalSceneMappingCarry.DetachedPlayerRoot),
                mousePrepared = carry.MousePrepared, mouseApplicationExecuted = false,
                deviceManagersPrepared = carry.DeviceManagersPrepared, deviceManagerCount = carry.DeviceManagerCount, deviceManagerApplicationExecuted = false,
                graphPairPrepared = carry.GraphPairPrepared, graphPairApplicationExecuted = false,
                sceneLoadCallbacksPrepared = carry.SceneLoadCallbacksPrepared, sceneLoadCallbacksApplicationExecuted = false,
                inputRoots = carry.InputGraph!.Roots.Length, inputNodes = carry.InputGraph.Nodes.Length,
                inputUnsupported = carry.InputGraph.Unsupported, inputBytes = inputBytes.Length,
                blockingQualifications = carry.BlockingQualifications });
            if (ordinal == 1)
            {
                // Audit that the full reader and retained reflection caches do
                // not change the frozen world. No Canon, game loop or input
                // runs between these captures. The D reader/encoder also runs
                // inside this audit, including its first reflection binding.
                // This is not cold-replay D0.
                var repeated = observer.CaptureBoundaryFrame(movie, native, OwnsCanonicalStart, RetiredOwner);
                var comparison = BoundaryDigestComparison.Compare(digest, repeated);
                File.WriteAllText(Path.Combine(directory, "boundary-digest-" + ordinal + "-repeat.json"), JsonConvert.SerializeObject(repeated));
                File.WriteAllText(Path.Combine(directory, "boundary-observer-audit.json"), JsonConvert.SerializeObject(comparison));
                File.WriteAllText(Path.Combine(directory, "boundary-coverage-reader-audit.json"), JsonConvert.SerializeObject(observer.BoundaryCoverageReadAudit()));
                Progress("boundary-observer-audit", new { comparison.ObservedEqual, comparison.FirstDifference });
                if (!comparison.ObservedEqual) throw new InvalidOperationException("Boundary reader changed frozen observed state: " + comparison.FirstDifference);
            }
            var pending = new[] { "R2-remaining-visual-FSM-fields", "R6-static-canonical-or-carry-fields", "BoundaryDigest-native-and-asset-coverage", "closed-D-field-registry" };
            File.AppendAllText(Path.Combine(directory, "canonical-boundary-progress.jsonl"), JsonConvert.SerializeObject(new {
                ordinal, source, target, movie, native, destroyedPoolActors = destroyed, stoppedTweens = stopped,
                broadphaseNodes = broadphase.Count, broadphaseCapacity = broadphase.Nodes.Length, broadphaseProxies = broadphase.ProxyCount,
                elapsedMilliseconds = elapsed.ElapsedMilliseconds, pending, completeStateCoverage = false }) + "\n");
            throw new InvalidOperationException("Canonical boundary registry is incomplete: " + string.Join(", ", pending));
        }
        internal void RestoreInputOrigin(CanonicalInputEpoch source)
        {
            source.RequireControlOrigin();
            if (inputOriginRestored || observer == null || observerSession == null || !observerInputOrigin.HasValue)
                throw new InvalidOperationException("Canonical observer origin restore requires its single prepared boundary.");
            inputOriginRestored = true;
            observer.Dispose(); observer = null;
            observerInputOrigin = source.OriginNative;
            observer = LoadingGameplayOracle.CreateBoundaryObserver(observerSession, source.OriginNative!.Value);
        }
        public void Dispose() { hudStartup.Dispose(); hudParticles.Dispose(); journal.Dispose(); observer?.Dispose(); retiredMeshes.Dispose(); visualTemplates.Dispose(); assetTemplates.Dispose(); startup.Dispose(); }
    }
}
