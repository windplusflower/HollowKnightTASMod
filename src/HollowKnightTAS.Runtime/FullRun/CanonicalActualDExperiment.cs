using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Protected transfer only: the packet lives in process/IPC memory. It is
    // never a product checkpoint and never grants qualification or resume.
    internal sealed class CanonicalActualDExperiment
    {
        internal const int MaximumBytes = 32 * 1024 * 1024, ChunkBytes = 262144;
        internal static bool Enabled => Environment.GetEnvironmentVariable("HKTAS_LOADING_ACTUAL_D_EXPERIMENT") == "1";
        internal static bool CaptureSource => Enabled && Environment.GetEnvironmentVariable("HKTAS_LOADING_ACTUAL_D_SOURCE") == "1";
        internal static int SelectedOrdinal {
            get {
                var text = Environment.GetEnvironmentVariable("HKTAS_LOADING_ACTUAL_D_ORDINAL");
                return CanonicalActualDRoute.SelectOrdinal(text, Enabled);
            }
        }
        public int Schema = 17, Ordinal;
        public string OriginalUiAssetOrigins = "", OriginalHudPresentationAssetOrigins = "";
        public string OriginalBossAssetOrigins = "";
        public CanonicalActualDRoute Route = new CanonicalActualDRoute();
        public long MovieFrame, NativeFrame;
        public byte[] Main = Array.Empty<byte>(), Input = Array.Empty<byte>(), Geo = Array.Empty<byte>(), Map = Array.Empty<byte>(), Marker = Array.Empty<byte>(), MapKeyText = Array.Empty<byte>(), MapKeyParents = Array.Empty<byte>();
        public byte[] HudPresentation = Array.Empty<byte>(), BorderInputs = Array.Empty<byte>(), Soul = Array.Empty<byte>(), PersistentSprites = Array.Empty<byte>();
        public byte[] Physics = Array.Empty<byte>();
        public CanonicalClockApplyExperiment Clock = new CanonicalClockApplyExperiment();
        public CanonicalRegisteredS0Contract RegisteredS0 = new CanonicalRegisteredS0Contract();
        public SessionCarry? Session;
        internal sealed class SessionCarry
        {
            public string MoviePrefixSha256 = "", InitialSavesSha256 = "";
            public Dictionary<string, string> ExecutionFiles = new Dictionary<string, string>();
            public long SkippedLoadingFrames, TimingRunStart, LastGameUpdateNativeFrame;
            public int TimingRunIndex;
            public decimal ActiveFrameRate, RecordingFrameRate;
            public bool TitleReadySeen, FirstLevelActivationPending;
            public string RandomSeedScene = "";
            public long GameUpdateDeltaBits, GameUpdateTimeBits, ExecutedMovieSecondsBits;
            public byte[] UnityRandom = Array.Empty<byte>();
            public uint[] ParticleManagerRandom = Array.Empty<uint>();
        }
        internal byte[] Encode()
        {
            Validate(); var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(this));
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("Actual D transfer exceeds its fixed memory bound.");
            return bytes;
        }
        internal static CanonicalActualDExperiment Decode(byte[] bytes)
        {
            if (!Enabled || bytes == null || bytes.Length == 0 || bytes.Length > MaximumBytes)
                throw new InvalidDataException("Actual D transfer is disabled or oversized.");
            var value = JsonConvert.DeserializeObject<CanonicalActualDExperiment>(Encoding.UTF8.GetString(bytes))
                ?? throw new InvalidDataException("Actual D transfer is absent.");
            value.Validate(); return value;
        }
        private void Validate()
        {
            var needsSoul = HollowKnightTAS.Core.Movie.NativeExecutionProfile.UsesCanonicalBossChallengeContext(
                HollowKnightTAS.Core.Movie.NativeExecutionProfile.SelectedProfileId);
            if (!Enabled || Schema != 17 || !HollowKnightTAS.Core.Movie.NativeExecutionProfile.SupportsSceneCheckpoints(
                HollowKnightTAS.Core.Movie.NativeExecutionProfile.SelectedProfileId) || Ordinal != SelectedOrdinal || MovieFrame < 0 || NativeFrame < 0
                || Main == null || Input == null || Geo == null || Map == null || Map.Length == 0 || Main.Length == 0 || Input.Length == 0 || Geo.Length == 0
                || Marker == null || Marker.Length == 0 || MapKeyText == null || MapKeyText.Length == 0 || MapKeyParents == null || MapKeyParents.Length == 0
                || BorderInputs == null || BorderInputs.Length == 0 || HudPresentation == null || HudPresentation.Length == 0 || Soul == null || (needsSoul ? Soul.Length == 0 : Soul.Length != 0)
                || PersistentSprites == null || (needsSoul ? PersistentSprites.Length == 0 : PersistentSprites.Length != 0) || Physics == null || Physics.Length == 0
                || (long)Main.Length + Input.Length + Geo.Length + Map.Length + Marker.Length + MapKeyText.Length + MapKeyParents.Length + HudPresentation.Length + BorderInputs.Length + Soul.Length + PersistentSprites.Length + Physics.Length > MaximumBytes || Clock == null || Clock.NativeFrame != NativeFrame)
                throw new InvalidDataException("Actual D source schema, epoch or graph bounds differ.");
            if (OriginalHudPresentationAssetOrigins == null || OriginalHudPresentationAssetOrigins.Length != 64
                || OriginalHudPresentationAssetOrigins.Any(value => !Uri.IsHexDigit(value)))
                throw new InvalidDataException("HUD presentation source asset manifest is absent.");
            if (OriginalBossAssetOrigins == null || OriginalBossAssetOrigins.Length != 129 || OriginalBossAssetOrigins[64] != ':'
                || OriginalBossAssetOrigins.Where((value, index) => index != 64).Any(value => !Uri.IsHexDigit(value)))
                throw new InvalidDataException("Actual D boss asset provenance manifest differs.");
            if (Route == null) throw new InvalidDataException("Source boundary route is absent.");
            Route.Validate(SelectedOrdinal, MovieFrame);
            if (OriginalUiAssetOrigins == null || OriginalUiAssetOrigins.Length != 64 || OriginalUiAssetOrigins.Any(value => !Uri.IsHexDigit(value)))
                throw new InvalidDataException("Original UI asset origin manifest is absent.");
            if (RegisteredS0 == null) throw new InvalidDataException("Registered S0 source contract is absent.");
            RegisteredS0.Validate();
            Clock.Clock.Validate(NativeFrame); CanonicalFsmTimeCarry.Validate(Clock.FsmTime);
        }
    }
}
