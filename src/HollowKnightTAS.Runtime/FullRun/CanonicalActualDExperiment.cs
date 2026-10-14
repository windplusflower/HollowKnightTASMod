using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Protected transfer only: the packet lives in process/IPC memory. It is
    // never a product checkpoint and never grants qualification or resume.
    internal sealed class CanonicalActualDExperiment
    {
        internal const int MaximumBytes = 32 * 1024 * 1024, ChunkBytes = 262144;
        internal static bool Enabled => Environment.GetEnvironmentVariable("HKTAS_LOADING_ACTUAL_D_EXPERIMENT") == "1";
        internal static bool CaptureSource => Enabled && Environment.GetEnvironmentVariable("HKTAS_LOADING_ACTUAL_D_SOURCE") == "1";
        public int Schema = 1, Ordinal;
        public long MovieFrame, NativeFrame;
        public byte[] Main = Array.Empty<byte>(), Input = Array.Empty<byte>();
        public CanonicalClockApplyExperiment Clock = new CanonicalClockApplyExperiment();
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
            if (!Enabled || Schema != 1 || Ordinal != 1 || MovieFrame < 0 || NativeFrame < 0
                || Main == null || Input == null || Main.Length == 0 || Input.Length == 0
                || (long)Main.Length + Input.Length > MaximumBytes || Clock == null || Clock.NativeFrame != NativeFrame)
                throw new InvalidDataException("Actual D source schema, epoch or graph bounds differ.");
            Clock.Clock.Validate(NativeFrame); CanonicalFsmTimeCarry.Validate(Clock.FsmTime);
        }
    }
}
