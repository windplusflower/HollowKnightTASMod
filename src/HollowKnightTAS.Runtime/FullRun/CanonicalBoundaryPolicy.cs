using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;

namespace HollowKnightTAS.Runtime.FullRun
{
    // One policy instance owns startup and every unloaded boundary for all
    // execution paths. A partial implementation cannot release the loader or
    // capture a checkpoint: the remaining registry closes here explicitly.
    internal sealed class CanonicalBoundaryPolicy : IDisposable
    {
        private readonly CanonicalHeroStartupWarmup startup;
        private readonly CanonicalBoundaryPoolPolicy pools = new CanonicalBoundaryPoolPolicy();
        private readonly CanonicalVisualFsmTemplates visualTemplates = new CanonicalVisualFsmTemplates();
        private readonly CanonicalHeroAssetTemplates assetTemplates = new CanonicalHeroAssetTemplates();
        private LoadingGameplayOracle? observer;
        private string? observerSession;
        private long? observerInputOrigin;
        private CanonicalCarryCapture? carry;
        private bool inputOriginRestored;
        internal CanonicalBoundaryPolicy() { startup = new CanonicalHeroStartupWarmup(assetTemplates.BeforeHeroStart); }
        internal bool AdvanceStartup() => startup.Advance();

        internal void Apply(int ordinal, string source, string target, long movie, long native, string sessionDirectory, long movieInputNativeOrigin,
            CanonicalInputEpoch inputControlOrigin, HollowKnightTAS.Runtime.Input.FullRunActionSetAdapter input, HollowKnightTAS.Runtime.Input.FullRunMouseBridge mouse)
        {
            if (movieInputNativeOrigin < 0 || native < movieInputNativeOrigin || observerInputOrigin.HasValue && observerInputOrigin.Value != movieInputNativeOrigin)
                throw new InvalidOperationException("Canonical boundary input frame origin is absent, changed or ahead of the native cursor.");
            observerInputOrigin = movieInputNativeOrigin;
            if (!startup.CompleteFor(HeroController.SilentInstance))
                throw new InvalidOperationException("Canonical boundary preceded registered Hero warmup.");
            var elapsed = Stopwatch.StartNew();
            var directory = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT")
                ?? throw new InvalidOperationException("Canonical boundary diagnostic directory is absent.");
            void Progress(string phase, object detail) => File.AppendAllText(Path.Combine(directory, "canonical-boundary-progress.jsonl"),
                JsonConvert.SerializeObject(new { ordinal, source, target, movie, native, phase, detail,
                    elapsedMilliseconds = elapsed.ElapsedMilliseconds, completeStateCoverage = false }) + "\n");
            Progress("begin", new { movieInputNativeOrigin, controlledNativeFrame = checked(native - movieInputNativeOrigin) });
            try { carry ??= new CanonicalCarryCapture(assetTemplates, visualTemplates); }
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
            var stopped = CanonicalBoundaryVisualPolicy.Reset(visualTemplates);
            Progress("R2-registered-visual-stop", new { stopped });
            var identities = new BoundaryObjectIdentity();
            var carryFsmBindings = CanonicalHeroFsmBindings.Validate(identities);
            Progress("D-Hero-FSM-bindings-validated", new { carryFsmBindings, canonicalVisualBindings = 2 });
            var assetOrigins = assetTemplates.Validate(identities);
            File.WriteAllText(Path.Combine(directory, "canonical-asset-origins.json"), JsonConvert.SerializeObject(new {
                schema = 1, diagnosticOnly = true, completeStateCoverage = false, origins = assetOrigins }));
            Progress("D-Hero-asset-origins-validated", new { assets = assetOrigins.Length });
            var broadphase = CheckpointPhysicsBroadphaseState.Canonicalize(identities);
            Progress("R4-broadphase-readback", new { broadphase.Count, capacity = broadphase.Nodes.Length, broadphase.ProxyCount });
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
            if (observer == null)
            {
                observer = LoadingGameplayOracle.CreateBoundaryObserver(sessionDirectory, movieInputNativeOrigin);
                observerSession = sessionDirectory;
            }
            if (!StringComparer.Ordinal.Equals(observerSession, sessionDirectory))
                throw new InvalidOperationException("Canonical observer cannot change its protected session binding.");
            bool OwnsCanonicalStart(int instance) => pools.OwnsNewPoolStart(instance) || visualTemplates.OwnsResetStart(instance);
            var digest = observer.CaptureBoundaryFrame(movie, native, OwnsCanonicalStart);
            File.WriteAllText(Path.Combine(directory, "boundary-digest-" + ordinal + ".json"), JsonConvert.SerializeObject(digest));
            File.WriteAllText(Path.Combine(directory, "boundary-coverage-catalog.json"), JsonConvert.SerializeObject(observer.BoundaryCoverageCatalog()));
            File.WriteAllText(Path.Combine(directory, "boundary-coverage-reader-audit.json"), JsonConvert.SerializeObject(observer.BoundaryCoverageReadAudit()));
            var fieldSchema = JsonConvert.SerializeObject(new { schema = 1, diagnosticOnly = true, ordinal,
                completeStateCoverage = false, fields = observer.BoundaryFieldSchema() });
            if (System.Text.Encoding.UTF8.GetByteCount(fieldSchema) > 16 * 1024 * 1024)
                throw new InvalidOperationException("Boundary field schema exceeds 16 MiB.");
            File.WriteAllText(Path.Combine(directory, "boundary-field-schema.json"), fieldSchema);
            Progress("boundary-digest-captured", new { digest.FullSha256, roots = digest.Roots.Length, unsupported = digest.Unsupported.Length });
            if (digest.CanonicalWaitIssues.Length != 0)
                throw new InvalidOperationException("Canonical scheduler registry is incomplete: " + string.Join(", ", digest.CanonicalWaitIssues));
            var inputEpoch = CanonicalInputEpochRuntime.Capture(movieInputNativeOrigin, native, inputControlOrigin);
            inputEpoch.RequireControlOrigin();
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
                inputRoots = carry.InputGraph!.Roots.Length, inputNodes = carry.InputGraph.Nodes.Length,
                inputUnsupported = carry.InputGraph.Unsupported, inputBytes = inputBytes.Length });
            if (ordinal == 1)
            {
                // Audit that the full reader and retained reflection caches do
                // not change the frozen world. No Canon, game loop or input
                // runs between these captures. The D reader/encoder also runs
                // inside this audit, including its first reflection binding.
                // This is not cold-replay D0.
                var repeated = observer.CaptureBoundaryFrame(movie, native, OwnsCanonicalStart);
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
        public void Dispose() { observer?.Dispose(); visualTemplates.Dispose(); assetTemplates.Dispose(); startup.Dispose(); }
    }
}
