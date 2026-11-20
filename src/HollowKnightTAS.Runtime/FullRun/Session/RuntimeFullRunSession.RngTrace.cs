using System;
using System.Globalization;
using System.IO;
using System.Text;
using HollowKnightTAS.Runtime.Rng;

namespace HollowKnightTAS.Runtime.FullRun
{
    public sealed partial class RuntimeFullRunSession
    {
        // Opt-in, bounded diagnostics for replay verification; never read or mutate RNG
        // from an IPC thread. Ordinary sessions do not collect these fingerprints.
        private bool movieRngTraceEnabled = Environment.GetEnvironmentVariable("HKTAS_FULL_RUN_RNG_TRACE") == "1";
        private int movieRngTraceRows;
        private UnityRandomStateCodec_1_5_78_11833? movieRngTraceCodec;

        private void TraceMovieRng(string phase, int? seed)
        {
            if (!movieRngTraceEnabled || movieRngTraceRows >= 1024) return;
            try
            {
                movieRngTraceCodec = movieRngTraceCodec ?? UnityRandomStateCodec_1_5_78_11833.Resolve().Codec
                    ?? throw new InvalidOperationException("RNG state codec unavailable.");
                var path = Path.Combine(sessionDirectory, "movie-rng-trace.csv");
                if (movieRngTraceRows == 0) File.WriteAllText(path, "movieFrame,phase,seed,rngSha256\n", new UTF8Encoding(false));
                File.AppendAllText(path, movieFrame.ToString(CultureInfo.InvariantCulture) + "," + phase + ","
                    + (seed?.ToString(CultureInfo.InvariantCulture) ?? "") + ","
                    + movieRngTraceCodec.CaptureCurrent().Sha256 + "\n", new UTF8Encoding(false));
                movieRngTraceRows++;
            }
            catch (Exception exception)
            {
                movieRngTraceEnabled = false;
                Modding.Logger.LogWarn("[HKTAS] Movie RNG trace disabled: " + exception.Message);
            }
        }
    }
}
