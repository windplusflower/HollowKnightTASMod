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

        internal void EnterResidentBoundary()
        {
            RequireResidentSession();
            using var process = new ResidentBoundaryProcessState(this);
            var coordinator = new ResidentBoundaryCoordinator(CreateResidentOwnership(), ClassifyResidentModule, process);
            var packet = coordinator.EnterAndCapture(); process.Complete();
            residentTraceOrigin = process.LocalIdentity ?? throw ResidentError("boundary packet lacks its observed source identity");
            residentBoundaryPacket = packet; residentBoundaryOrdinal = residentTraceOrigin.BoundaryOrdinal;
            residentBoundarySource = residentTraceOrigin.SourceScene; residentBoundaryTarget = residentTraceOrigin.TargetScene;
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
                var error = ResidentError("complete restoration including process commit exceeded 9 seconds");
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
            if (assembly == typeof(RuntimeFullRunSession).Assembly || assembly == typeof(MovieV2Document).Assembly
                || assembly == typeof(MonoMod.RuntimeDetour.Hook).Assembly || assembly == typeof(Mono.Cecil.AssemblyDefinition).Assembly
                || assembly == typeof(JsonConvert).Assembly || assembly.GetName().Name == "HollowKnightTAS.ClockPayload")
                return ResidentStaticRoots.ModuleOwnership.ControlPlane;
            // Compare actual platform assemblies, never a namespace/name
            // prefix which could silently hide a gameplay/mod assembly.
            if (assembly == typeof(object).Assembly || assembly == typeof(Enumerable).Assembly || assembly == typeof(Uri).Assembly
                || assembly == typeof(System.Xml.XmlDocument).Assembly || assembly == typeof(System.Runtime.Serialization.DataContractAttribute).Assembly)
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
            if (source.Version != 2 || source.BoundaryOrdinal < 1 || string.IsNullOrEmpty(source.SourceScene) || string.IsNullOrEmpty(source.TargetScene)
                || source.InitialSaves != checkpointInitialSavesSha256 || source.Clock != backend
                || source.Runtime != Sha256Utility.ComputeHex(File.ReadAllBytes(typeof(RuntimeFullRunSession).Assembly.Location))
                || source.Core != Sha256Utility.ComputeHex(File.ReadAllBytes(typeof(MovieV2Document).Assembly.Location))
                || source.MovieFrame < 0 || source.MoviePrefix != ResidentPrefix(ResidentMovie(), source.MovieFrame))
                throw ResidentError("source save, Movie prefix or executable identity differs");
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
