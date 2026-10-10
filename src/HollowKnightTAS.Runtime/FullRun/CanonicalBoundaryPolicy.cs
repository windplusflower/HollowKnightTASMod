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
        private LoadingGameplayOracle? observer;
        private string? observerSession;
        private CanonicalCarryCapture? carry;
        internal CanonicalBoundaryPolicy() { startup = new CanonicalHeroStartupWarmup(); }
        internal bool AdvanceStartup() => startup.Advance();

        internal void Apply(int ordinal, string source, string target, long movie, long native, string sessionDirectory)
        {
            if (!startup.CompleteFor(HeroController.SilentInstance))
                throw new InvalidOperationException("Canonical boundary preceded registered Hero warmup.");
            var elapsed = Stopwatch.StartNew();
            var directory = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT")
                ?? throw new InvalidOperationException("Canonical boundary diagnostic directory is absent.");
            void Progress(string phase, object detail) => File.AppendAllText(Path.Combine(directory, "canonical-boundary-progress.jsonl"),
                JsonConvert.SerializeObject(new { ordinal, source, target, movie, native, phase, detail,
                    elapsedMilliseconds = elapsed.ElapsedMilliseconds, completeStateCoverage = false }) + "\n");
            Progress("begin", new { });
            var destroyed = pools.Reset();
            Progress("R1-pools-rebuilt", new { destroyed });
            var stopped = CanonicalBoundaryVisualPolicy.Reset(visualTemplates);
            Progress("R2-registered-visual-stop", new { stopped });
            var identities = new BoundaryObjectIdentity();
            var carryFsmBindings = CanonicalHeroFsmBindings.Validate(identities);
            Progress("D-Hero-FSM-bindings-validated", new { carryFsmBindings, canonicalVisualBindings = 2 });
            var broadphase = CheckpointPhysicsBroadphaseState.Canonicalize(identities);
            Progress("R4-broadphase-readback", new { broadphase.Count, capacity = broadphase.Nodes.Length, broadphase.ProxyCount });
            CanonicalBoundaryVisualPolicy.ResetFsmOrder(identities);
            CanonicalBoundaryStaticPolicy.ResetExecutionStack();
            Progress("R6-FSM-execution-stack-reset", new { });
            if (observer == null)
            {
                observer = LoadingGameplayOracle.CreateBoundaryObserver(sessionDirectory);
                observerSession = sessionDirectory;
            }
            if (!StringComparer.Ordinal.Equals(observerSession, sessionDirectory))
                throw new InvalidOperationException("Canonical observer cannot change its protected session binding.");
            bool OwnsCanonicalStart(int instance) => pools.OwnsNewPoolStart(instance) || visualTemplates.OwnsResetStart(instance);
            var digest = observer.CaptureBoundaryFrame(movie, native, OwnsCanonicalStart);
            File.WriteAllText(Path.Combine(directory, "boundary-digest-" + ordinal + ".json"), JsonConvert.SerializeObject(digest));
            var fieldSchema = JsonConvert.SerializeObject(new { schema = 1, diagnosticOnly = true, ordinal,
                completeStateCoverage = false, fields = observer.BoundaryFieldSchema() });
            if (System.Text.Encoding.UTF8.GetByteCount(fieldSchema) > 16 * 1024 * 1024)
                throw new InvalidOperationException("Boundary field schema exceeds 16 MiB.");
            File.WriteAllText(Path.Combine(directory, "boundary-field-schema.json"), fieldSchema);
            Progress("boundary-digest-captured", new { digest.FullSha256, roots = digest.Roots.Length, unsupported = digest.Unsupported.Length });
            if (digest.CanonicalWaitIssues.Length != 0)
                throw new InvalidOperationException("Canonical scheduler registry is incomplete: " + string.Join(", ", digest.CanonicalWaitIssues));
            carry ??= new CanonicalCarryCapture();
            var candidate = carry.Capture(identities);
            var candidateBytes = carry.Encode(candidate);
            File.WriteAllBytes(Path.Combine(directory, "canonical-carry-" + ordinal + ".bin"), candidateBytes);
            Progress("D-candidate-data-captured", new { roots = candidate.Roots.Length, nodes = candidate.Nodes.Length,
                externalBindings = candidate.Externals.Length, unsupported = candidate.Unsupported, bytes = candidateBytes.Length });
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
        public void Dispose() { observer?.Dispose(); visualTemplates.Dispose(); startup.Dispose(); }
    }
}
