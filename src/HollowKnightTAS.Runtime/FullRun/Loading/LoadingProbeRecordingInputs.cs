using System;
using System.IO;
using System.Linq;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.Runtime.FullRun
{
    public sealed partial class RuntimeFullRunSession
    {
        // P0 supplies only authored button values through the regular input
        // adapter. The session remains Recording and its journal captures the
        // actual native sample order and edges, without reusing old assertions.
        private MovieV2Document? ReadLoadingProbeRecordingInputs()
        {
            var path = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_RECORD_INPUTS");
            if (string.IsNullOrEmpty(path)) return null;
            if (Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE") != "1"
                || !NativeExecutionProfile.IsCanonicalStartupProfile(MovieProtocolV2.SelectedNativeProfileId))
                throw new InvalidOperationException("Scripted probe recording requires an explicit P0 canonical startup profile.");
            var output = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT");
            if (string.IsNullOrWhiteSpace(output)) throw new InvalidDataException("Probe output root is missing.");
            var root = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            path = Path.GetFullPath(path);
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || new FileInfo(path).Length > MovieProtocolV2.MaximumSourceUtf8Bytes)
                throw new InvalidDataException("Recording input script must be bounded inside the probe output.");
            MovieV2Document script;
            using (var reader = File.OpenText(path))
                script = new MovieV2Codec().Parse(reader, path).Document
                    ?? throw new InvalidDataException("Invalid recording input script.");
            var validation = new MovieV2Validator().Validate(script, MovieV2ValidationContext.CreateDefault());
            if (!validation.Success || validation.ExpandedFrames == 0
                || script.Header.NativeProfileId != MovieProtocolV2.SelectedNativeProfileId
                || script.Header.MouseEnabled != mouseEnabled
                || script.Runs.Any(run => !run.Authored || run.RngSeed != null
                    || run.FramesPerSecond != recordingFrameRate))
                throw new InvalidDataException("Probe recording accepts only authored inputs at the selected recording FPS, without RNG seeds.");
            return script;
        }
    }
}
