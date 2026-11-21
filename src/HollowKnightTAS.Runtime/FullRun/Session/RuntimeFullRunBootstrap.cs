using System;
using System.IO;
using System.Text;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.FullRun;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Runtime.Input;
using HollowKnightTAS.Runtime.ReplaySave;
using HollowKnightTAS.Runtime.Timing;
using InControl;
using Modding;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    public static class RuntimeFullRunBootstrap
    {
        public static RuntimeFullRunSession Attach(string gateToken,
            NativeFullRunFrameClock clock, ProtectedSaveRedirector saves,
            Action<string> log)
        {
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            if (saves == null) throw new ArgumentNullException(nameof(saves));
            if (log == null) throw new ArgumentNullException(nameof(log));
            try
            {
                saves.AssertActive();
                if (!FullRunBootDescriptor.IsGateToken(gateToken)
                    || InputManager.CurrentTick != 0)
                    throw new InvalidOperationException(
                        "BootstrapInputGap: input system advanced before full-run attachment.");
                var nativeHash = clock.ReadArmedDescriptorHash();
                var localRoot = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                var directory = Path.Combine(localRoot, "HollowKnightTAS",
                    "full-run-bootstrap", gateToken);
                var descriptorPath = Path.Combine(directory, "boot.json");
                var descriptorBytes = ReadBounded(descriptorPath,
                    FullRunBootDescriptor.MaximumBytes);
                var actualDescriptorSha256 = Sha256Utility.ComputeHex(descriptorBytes);
                var expectedDescriptorSha256 = BitConverter.ToString(nativeHash).Replace("-", string.Empty)
                    .ToLowerInvariant();
                if (actualDescriptorSha256 != expectedDescriptorSha256)
                {
                    Modding.Logger.LogWarn("[HKTAS] Full-run boot descriptor hash differs from the armed gate ("
                        + actualDescriptorSha256 + " != " + expectedDescriptorSha256 + "); continuing anyway.");
                }
                var descriptor = FullRunBootDescriptor.Parse(descriptorBytes);
                if (descriptor.GateToken != gateToken
                    || descriptor.RunId != saves.Descriptor.RunId)
                    throw new InvalidDataException("Full-run bootstrap identity differs from the save session.");
                var sessionDirectory = saves.GetTasDataPath("sessions",
                    "full-run-" + descriptor.RunId);
                LoadingInputCounterObservation.Log("full-run-attach");
                var session = new RuntimeFullRunSession(clock,
                    new FullRunActionSetAdapter(), new FullRunMouseBridge(),
                    sessionDirectory);
                session.BindCheckpointSaveContext(saves);
                FullRunResult result;
                session.ConfigureTiming(descriptor.FramesPerSecond, descriptor.PauseAtFrame);
                if (descriptor.Mode == "Record")
                {
                    result = session.BeginRecording(descriptor.MouseEnabled,
                        clock.CurrentFrameIndex);
                }
                else
                {
                    var expectedMoviePath = Path.Combine(directory, "movie.hktas");
                    if (!string.Equals(descriptor.MoviePath, expectedMoviePath,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Replay movie path escaped the private bootstrap directory.");
                    var movieBytes = ReadBounded(descriptor.MoviePath,
                        MovieProtocolV2.MaximumSourceUtf8Bytes);
                    var actualMovieSha256 = Sha256Utility.ComputeHex(movieBytes);
                    if (actualMovieSha256 != descriptor.MovieSha256)
                    {
                        Modding.Logger.LogWarn("[HKTAS] Staged replay movie hash differs from its descriptor ("
                            + actualMovieSha256 + " != " + descriptor.MovieSha256 + "); continuing anyway.");
                    }
                    var source = new UTF8Encoding(false, true).GetString(movieBytes);
                    var codec = new MovieV2Codec();
                    var parsed = codec.Parse(new StringReader(source), descriptor.MoviePath);
                    if (!parsed.Success || parsed.Document == null)
                        throw new InvalidDataException("Staged replay movie failed to parse.");
                    if (codec.WriteCanonical(parsed.Document) != source)
                        Modding.Logger.LogWarn("[HKTAS] Staged replay movie is noncanonical; continuing anyway.");
                    var movie = parsed.Document;
                    if (movie.Header.MouseEnabled != descriptor.MouseEnabled)
                        throw new InvalidDataException("Replay mouse mode differs from the boot descriptor.");
                    var validation = new MovieV2Validator().Validate(movie,
                        new MovieV2ValidationContext(MovieProtocolV2.MaximumExpandedFrames,
                            MovieProtocolV2.MaximumSamplesPerFrame));
                    if (!validation.Success)
                        throw new InvalidDataException("Staged replay movie failed v2 validation.");
                    var capabilities = new MovieV2RuntimeCapabilities(
                        MovieProtocolV2.SelectedNativeProfileId, MovieProtocolV2.ActionSchemaId,
                        true, true, InputManager.MouseProvider != null,
                        Screen.width, Screen.height, "none",
                        Application.version,
                        typeof(Mod).Assembly.GetName().Version?.ToString() ?? "unknown",
                        HollowKnightTASMod.Version);
                    var compatibility = new MovieV2Compatibility().Evaluate(movie.Header,
                        capabilities);
                    if (!compatibility.Allowed)
                        throw new InvalidDataException("V2 replay compatibility gate rejected the session: "
                            + compatibility.Errors[0].Message);
                    foreach (var warning in compatibility.Warnings)
                        Modding.Logger.LogWarn("[HKTAS] V2 replay compatibility warning: " + warning.Message);
                    result = session.BeginReplay(movie, compatibility,
                        clock.CurrentFrameIndex);
                }
                if (!result.Success)
                    throw new InvalidOperationException(result.Error);
                log("Full-run input attached at native frame " + result.FrameIndex
                    + "; mode=" + descriptor.Mode + ".");
                return session;
            }
            catch (Exception exception)
            {
                try { clock.Fault(42); } catch { }
                log("Full-run bootstrap fault: " + exception.Message);
                throw;
            }
        }

        private static byte[] ReadBounded(string path, long maximum)
        {
            var file = new FileInfo(path);
            if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0
                || file.Length <= 0 || file.Length > maximum)
                throw new InvalidDataException("Private bootstrap file is missing, linked, or outside its size limit.");
            return File.ReadAllBytes(path);
        }
    }
}
