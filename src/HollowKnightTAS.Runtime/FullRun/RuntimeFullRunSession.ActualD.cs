using System;
using System.IO;
using HollowKnightTAS.Runtime.Rng;

namespace HollowKnightTAS.Runtime.FullRun
{
    public sealed partial class RuntimeFullRunSession
    {
        private void StartActualDSuffixOracle(string role, long end)
        {
            if (actualDSuffixOracle != null || !movieInputNativeOrigin.HasValue)
                throw new InvalidOperationException("Actual D suffix observer requires the original input origin and single admission.");
            var rng = UnityRandomStateCodec_1_5_78_11833.Resolve().Codec
                ?? throw new InvalidOperationException("Actual D suffix RNG observer is unavailable.");
            actualDSuffixOracle = LoadingGameplayOracle.CreateActualDSuffix(
                Path.Combine(Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT")!, "actual-d-" + role + "-suffix-oracle"),
                sessionDirectory, movieFrame, Math.Min(end, movieFrame + 3), movieInputNativeOrigin.Value, () => movieFrame, () => clock.CurrentFrameIndex,
                () => rng.CaptureCurrent().Sha256);
            actualDSuffixEnd = end; actualDSuffixRole = role;
        }
        private long actualDSuffixEnd;
        private string actualDSuffixRole = "";
    }
}
