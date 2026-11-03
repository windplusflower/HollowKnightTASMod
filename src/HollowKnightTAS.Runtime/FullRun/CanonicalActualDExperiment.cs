using System;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Configuration only for the protected resident transfer experiment.
    // Packet encoding/validation belongs to the shared resident wire layer.
    internal static class CanonicalActualDExperiment
    {
        internal const int MaximumBytes = CanonicalActualDWire.MaximumBytes, ChunkBytes = 262144;
        internal static bool Enabled => Environment.GetEnvironmentVariable("HKTAS_LOADING_ACTUAL_D_EXPERIMENT") == "1";
        internal static bool CaptureSource => Enabled && Environment.GetEnvironmentVariable("HKTAS_LOADING_ACTUAL_D_SOURCE") == "1";
        internal static int SelectedOrdinal {
            get {
                var text = Environment.GetEnvironmentVariable("HKTAS_LOADING_ACTUAL_D_ORDINAL");
                return CanonicalActualDRoute.SelectOrdinal(text, Enabled);
            }
        }
    }
}
