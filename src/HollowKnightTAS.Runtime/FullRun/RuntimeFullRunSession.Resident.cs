using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Runtime.Input;
using HollowKnightTAS.Runtime.Timing;
using Newtonsoft.Json;

namespace HollowKnightTAS.Runtime.FullRun
{
    public sealed partial class RuntimeFullRunSession
    {
        private byte[]? residentBoundaryPacket;
        private int residentBoundaryOrdinal;
        private string residentBoundarySource = "", residentBoundaryTarget = "";
        private ResidentIdentity? residentTraceOrigin;
        private ResidentContinuationTrace? residentContinuationTrace;
        private long residentTraceEnd;
        // This is a field contract for the one execution type, not a scene or
        // object whitelist. Any new actual instance field is an admission gap.
        internal static readonly IReadOnlyDictionary<string, string> ResidentFieldOwnership = ResidentProcessSlots.Ownership(
            "D:bootstrapFrame,movieFrame,skippedLoadFrames,expectedNativeStart,movieInputNativeOrigin,movieInputControlOrigin,frameInputEnabled,titleReadySeen,returningToMainMenu,firstLevelActivationPending,frameBoundary,uiManager,inputReady,boundBossController,boundBosses,bossSceneEntered,bossDeathObserved,bossesDeadObserved,bossSceneCompleteObserved,bossDeathFrame,bossSceneEntryMovieFrame,randomSeedRequestNativeFrame,randomSeedSceneName,recordingFrameRate,activeFrameRate,timingRunIndex,timingRunStart,lastGameUpdateNativeFrame,gameUpdateDeltaSeconds,gameUpdateTimeSeconds,executedMovieSeconds",
            "Dependency:checkpointExecutionFiles,checkpointInitialSavesSha256,replayMovie,editableReplay,recordingHeader,recordedMovie,recordedCanonical,replayLength,mouseEnabled,randomSeedSceneHandle",
            "Execution:actualDImport,actualDImportOffset,actualDImportHash,actualDApplicationStarted,actualDSuffixEnd,actualDSuffixRole,checkpointSync,checkpointArm,frozenCheckpoint,checkpointEncoding,checkpointImport,stagedCheckpoint,checkpointFailure,checkpointCaptureId,checkpointSaves,clock,input,mouse,sessionDirectory,observationQueue,worldObserver,observationCache,infoTiming,journal,loadingBoundaryProbe,loadingGameplayOracle,actualDSuffixOracle,returnToMainMenuHooked,mismatchCount,mode,error,disposed,bossTraceEnabled,bossTrace,bossTraceRows,bossTraceRngCodec,randomSeedRequest,randomSeedAcknowledged,randomSeedHandshakePending,restoreDrawing,pauseAtMovieFrame,observedWorld,observedBindings,observedFrameTiming,nextBindingsRefresh,observationFaultLogged,movieRngTraceEnabled,movieRngTraceRows,movieRngTraceCodec,s0Observation,replayStateTraceEnabled,replayStateTrace,videoCapture,videoRange,residentBoundaryPacket,residentBoundaryOrdinal,residentBoundarySource,residentBoundaryTarget,residentExport,residentTransferApplied,residentImportLoader,residentParkedNative,residentTraceOrigin,residentContinuationTrace,residentTraceEnd");

        internal bool EnterResidentBoundary(int ordinal, long movie, long native)
        {
            if (!CanonicalActualDExperiment.CaptureSource || ordinal != CanonicalActualDExperiment.SelectedOrdinal) {
                var consumed = input.ConsumedSamples;
                (checkpointSaves ?? throw ResidentError("protected transit save context is absent")).AssertActive();
                if (disposed || clock.IsFinished || error.Length != 0) throw ResidentError("transit session is closed or faulted");
                var parked = LoadingSerialSceneProbe.CaptureResidentBoundary();
                parked.Require();
                var tree = ResidentObjectDirectory.Capture(CreateResidentOwnership());
                tree.RequireCurrent();
                parked.RequireUnchanged();
                residentBoundaryPacket = null;
                if (movieFrame != movie || clock.CurrentFrameIndex != native || input.ConsumedSamples != consumed)
                    throw ResidentError("transit verification advanced a cursor");
                return false;
            }
            RequireResidentSession();
            using var process = new ResidentBoundaryProcessState(this);
            var coordinator = new ResidentBoundaryCoordinator(CreateResidentOwnership(), ClassifyResidentModule, process);
            var packet = coordinator.EnterAndCapture(); process.Complete();
            residentTraceOrigin = process.LocalIdentity ?? throw ResidentError("boundary packet lacks its observed source identity");
            residentBoundaryPacket = packet; residentBoundaryOrdinal = residentTraceOrigin.BoundaryOrdinal;
            residentBoundarySource = residentTraceOrigin.SourceScene; residentBoundaryTarget = residentTraceOrigin.TargetScene;
            return true;
        }
        internal long RestoreResidentBoundary(byte[] packet)
        {
            var started = ResidentNativeMemory.WallMilliseconds();
            RequireResidentSession();
            var loader = residentImportLoader ?? throw ResidentError("restoration did not park before the common Canon");
            if (!clock.IsPaused) throw ResidentError("restoration requires the paused native observation callback");
            using var process = new ResidentBoundaryProcessState(this, loader);
            var coordinator = new ResidentBoundaryCoordinator(CreateResidentOwnership(), ClassifyResidentModule, process);
            coordinator.Restore(packet); process.Complete();
            residentTraceOrigin = process.VerifiedSourceIdentity ?? throw ResidentError("verified packet lacks its source identity");
            loader.ReleaseAfterRestore(); residentImportLoader = null;
            var elapsed = ResidentNativeMemory.WallMilliseconds() - started;
            if (elapsed >= ResidentBoundaryCoordinator.MaximumRestoreMilliseconds) {
                var error = ResidentError("complete restoration including process commit exceeded timeout");
                FaultResidentSession(error); throw error;
            }
            return checked((long)elapsed);
        }
        private static ResidentExecutionOwnership CreateResidentOwnership()
        {
            var own = typeof(RuntimeFullRunSession).Assembly;
            var ownership = new ResidentExecutionOwnership(component => {
                var type = component.GetType();
                // All native drivers defined in this Runtime assembly belong
                // to execution, identified by actual compiled type/module.
                return type.Assembly == own ? "runtime-driver/" + type.Module.ModuleVersionId.ToString("D") + "/" + type.MetadataToken.ToString("x8") : null;
            });
            var manager = GameManager.UnsafeInstance ?? throw ResidentError("active loader GameManager is absent");
            ownership.RegisterProtectedComponentDependency("native-scene-loader-owner", manager);
            return ownership;
        }
        private static ResidentStaticRoots.ModuleOwnership ClassifyResidentModule(Assembly assembly)
        {
            var name = assembly.GetName().Name ?? "";
            if (assembly == typeof(RuntimeFullRunSession).Assembly || assembly == typeof(MovieV2Document).Assembly
                || name == "HollowKnightTAS" || name == "HollowKnightTAS.Core"
                || name.StartsWith("MonoMod", StringComparison.Ordinal)
                || name.StartsWith("Mono.Cecil", StringComparison.Ordinal)
                || assembly == typeof(JsonConvert).Assembly || name == "HollowKnightTAS.ClockPayload")
                return ResidentStaticRoots.ModuleOwnership.ControlPlane;
            // Compare actual platform assemblies, never a namespace/name
            // prefix which could silently hide a gameplay/mod assembly.
            if (assembly == typeof(object).Assembly || assembly == typeof(Enumerable).Assembly || assembly == typeof(Uri).Assembly
                || assembly == typeof(System.Xml.XmlDocument).Assembly || assembly == typeof(System.Runtime.Serialization.DataContractAttribute).Assembly
                || typeof(System.Runtime.Serialization.DataContractAttribute).Assembly.GetReferencedAssemblies().Any(reference => reference.FullName == assembly.FullName))
                return ResidentStaticRoots.ModuleOwnership.Runtime;
            return ResidentStaticRoots.ModuleOwnership.Gameplay;
        }

        internal NativeFullRunFrameClock ResidentClock => clock;
        internal FullRunActionSetAdapter ResidentInput => input;
        internal FullRunMouseBridge ResidentMouse => mouse;
        internal MovieV2Document ResidentMovie()
        {
            RequireResidentSession();
            return mode == "Recording" ? (journal ?? throw ResidentError("recording journal is absent")).ReadSnapshot(
                recordingHeader ?? throw ResidentError("recording header is absent"), movieFrame)
                : replayMovie ?? recordedMovie ?? throw ResidentError("Movie continuation is absent");
        }
        internal void RequireResidentSession()
        {
            if (disposed || clock.IsFinished || error.Length != 0 || randomSeedHandshakePending || !inputReady
                || returningToMainMenu || !movieInputNativeOrigin.HasValue || movieInputControlOrigin == null)
                throw ResidentError("session is not at an admitted complete input/loading boundary");
            (checkpointSaves ?? throw ResidentError("protected save context is absent")).AssertActive();
            if (checkpointInitialSavesSha256.Length != 64) throw ResidentError("initial protected save identity is absent");
        }
        internal ResidentProcessSlots CaptureResidentSlots(Action admitted)
        { RequireResidentSession(); return new ResidentProcessSlots("session", this, ResidentFieldOwnership, admitted); }

        internal ResidentControlPlaneState.CaptureResult CaptureResidentControlPlane(ulong token, Action admitted, Action requirePayloadInitialized, bool frameObservation = false)
        {
            if (!frameObservation) RequireResidentSession(); admitted();
            var owners = Modding.ModHooks.GetAllMods(onlyEnabled: false, allowLoadError: true)
                .OfType<HollowKnightTASMod>().Where(mod => mod.OwnsResidentSession(this)).ToArray();
            if (owners.Length != 1) throw ResidentError("actual protected Mod/startup owner is absent or ambiguous");
            var mod = owners[0];
            return frameObservation ? ResidentControlPlaneState.CaptureFrame(mod, mod.RequireResidentStartup(this), token, admitted, requirePayloadInitialized)
                : ResidentControlPlaneState.Capture(mod, mod.RequireResidentStartup(this), token, admitted, requirePayloadInitialized);
        }

        internal sealed class ResidentIdentity
        {
            public int Version = 2, BoundaryOrdinal;
            public string InitialSaves = "", MoviePrefix = "", Runtime = "", Core = "", Clock = "";
            public string SourceScene = "", TargetScene = "";
            public long MovieFrame;
        }
        internal ResidentIdentity CaptureResidentIdentity(string backend, LoadingSerialSceneProbe.ResidentLoaderBoundary loader)
        {
            RequireResidentSession(); var movie = ResidentMovie();
            return new ResidentIdentity {
                BoundaryOrdinal = loader.Ordinal, SourceScene = loader.Source, TargetScene = loader.Target,
                InitialSaves = checkpointInitialSavesSha256, MovieFrame = movieFrame,
                MoviePrefix = ResidentPrefix(movie, movieFrame), Clock = backend,
                Runtime = Sha256Utility.ComputeHex(File.ReadAllBytes(typeof(RuntimeFullRunSession).Assembly.Location)),
                Core = Sha256Utility.ComputeHex(File.ReadAllBytes(typeof(MovieV2Document).Assembly.Location)) };
        }
        internal ResidentIdentity RequireResidentIdentity(byte[] bytes, string backend)
        {
            RequireResidentSession();
            if (bytes == null || bytes.Length == 0 || bytes.Length > 8192) throw ResidentError("identity packet length differs");
            var source = JsonConvert.DeserializeObject<ResidentIdentity>(new UTF8Encoding(false, true).GetString(bytes))
                ?? throw ResidentError("identity packet is absent");
            var currentPrefix = ResidentPrefix(ResidentMovie(), source.MovieFrame);
            var clockMatch = source.Clock == backend || (source.Clock != null && backend != null
                && source.Clock.StartsWith("resident-clock-v3/", StringComparison.Ordinal)
                && backend.StartsWith("resident-clock-v3/", StringComparison.Ordinal));
            if (source.Version != 2 || source.BoundaryOrdinal < 1 || string.IsNullOrEmpty(source.SourceScene) || string.IsNullOrEmpty(source.TargetScene)
                || source.InitialSaves != checkpointInitialSavesSha256 || !clockMatch
                || source.Runtime == null || source.Runtime.Length != 64
                || source.MovieFrame < 0 || source.MoviePrefix != currentPrefix)
            {
                var detail = source.InitialSaves != checkpointInitialSavesSha256 ? ("initialSaves: source=" + source.InitialSaves + " target=" + checkpointInitialSavesSha256)
                    : !clockMatch ? ("clock: source=" + source.Clock + " target=" + backend)
                    : source.MoviePrefix != currentPrefix ? "moviePrefix" : "identity";
                throw ResidentError("source save, Movie prefix or executable identity differs: " + detail);
            }
            var currentCoreHash = Sha256Utility.ComputeHex(File.ReadAllBytes(typeof(MovieV2Document).Assembly.Location));
            if (source.Core != currentCoreHash) {
                Modding.Logger.LogWarn("[HKTAS] Restoring across Core compilation build: source=" + source.Core + ", current=" + currentCoreHash);
            }
            var currentRuntimeHash = Sha256Utility.ComputeHex(File.ReadAllBytes(typeof(RuntimeFullRunSession).Assembly.Location));
            if (source.Runtime != currentRuntimeHash) {
                Modding.Logger.LogWarn("[HKTAS] Restoring across Runtime compilation build: source=" + source.Runtime + ", current=" + currentRuntimeHash);
            }
            if (source.Clock != backend) {
                Modding.Logger.LogWarn("[HKTAS] Restoring across Clock compilation build: source=" + source.Clock + ", current=" + backend);
            }
            return source;
        }
        private static string ResidentPrefix(MovieV2Document movie, long frame)
            => Sha256Utility.ComputeHex(Encoding.UTF8.GetBytes(new MovieV2Codec().WriteCanonical(MovieV2Prefix.Take(movie, frame))));
        internal void FaultResidentSession(Exception failure)
        {
            error = "Resident boundary transaction failed: " + failure.Message;
            mode = "Faulted"; clock.Fault(241);
            Modding.Logger.LogError("[HKTAS] " + error);
        }
        private static InvalidOperationException ResidentError(string message) => new InvalidOperationException("Resident session: " + message);
    }
}
