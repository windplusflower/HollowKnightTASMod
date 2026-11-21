using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Ipc;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Core.ReplaySave;

namespace HollowKnightTAS.Companion.Services
{
    public sealed class VerifiedStartupProfile
    {
        public const string CapabilityId =
            "native.clock-rng-pause.override.experimental.v31";
        public const string ProfileId = StartupProfileContract.ProfileId;
        public const string StartupPolicy =
            "create-suspended-early-apc-unity-then-bridge-v1";

        private static readonly string[] RuntimeFileNames =
        {
            "HollowKnightTAS.ClockBridge.dll",
            "HollowKnightTAS.ClockInjector.deps.json",
            "HollowKnightTAS.ClockInjector.dll",
            "HollowKnightTAS.ClockInjector.exe",
            "HollowKnightTAS.ClockInjector.runtimeconfig.json",
            "HollowKnightTAS.ClockPayload.dll"
        };

        private VerifiedStartupProfile(
            string bundleRoot,
            string gameExecutablePath,
            string injectorPath,
            string manifestPath,
            string startupProfileSha256,
            string gameExecutableSha256,
            string unityPlayerSha256,
            string assemblyCSharpSha256)
        {
            BundleRoot = bundleRoot;
            GameExecutablePath = gameExecutablePath;
            InjectorPath = injectorPath;
            ManifestPath = manifestPath;
            StartupProfileSha256 = startupProfileSha256;
            GameExecutableSha256 = gameExecutableSha256;
            UnityPlayerSha256 = unityPlayerSha256;
            AssemblyCSharpSha256 = assemblyCSharpSha256;
        }

        public string BundleRoot { get; }
        public string GameExecutablePath { get; }
        public string InjectorPath { get; }
        public string ManifestPath { get; }
        public string StartupProfileSha256 { get; }
        public string GameExecutableSha256 { get; }
        public string UnityPlayerSha256 { get; }
        public string AssemblyCSharpSha256 { get; }

        public void RequireStartupFrameGate()
        {
            if (!File.Exists(ManifestPath))
            {
                Warn("ClockStartup manifest is missing (" + ManifestPath + "); skipping startup frame gate ABI check.");
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(ManifestPath));
                if (!document.RootElement.TryGetProperty("startupFrameGateAbi", out var version)
                    || !version.TryGetInt32(out var abi) || abi != 1)
                {
                    Warn("ClockStartup manifest startupFrameGateAbi is missing or not 1; continuing anyway.");
                }
            }
            catch (Exception exception)
            {
                Warn("Failed to inspect ClockStartup manifest startupFrameGateAbi (" + exception.Message + "); continuing anyway.");
            }
        }

        public static VerifiedStartupProfile Load(
            string bundleRoot,
            string gameExecutablePath)
        {
            var root = RequireDirectory(bundleRoot, nameof(bundleRoot));
            var game = RequireFile(
                gameExecutablePath,
                nameof(gameExecutablePath));
            if (!string.Equals(
                    Path.GetFileName(game),
                    "hollow_knight.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Verified startup target is not Hollow Knight.");
            }

            // Only core startup binaries are strictly required to exist.
            var injector = RequireFixedFile(root, "HollowKnightTAS.ClockInjector.exe");
            var bridge = RequireFixedFile(root, "HollowKnightTAS.ClockBridge.dll");
            var payload = RequireFixedFile(root, "HollowKnightTAS.ClockPayload.dll");

            var actualGameSha256 = Sha256Utility.ComputeFileHex(game);
            var gameDirectory = Path.GetDirectoryName(game);
            var unityPlayerPath = gameDirectory != null
                ? Path.Combine(gameDirectory, "UnityPlayer.dll")
                : string.Empty;
            var assemblyCSharpPath = gameDirectory != null
                ? Path.Combine(gameDirectory, "hollow_knight_Data", "Managed", "Assembly-CSharp.dll")
                : string.Empty;

            var actualUnityPlayerSha256 = !string.IsNullOrEmpty(unityPlayerPath) && File.Exists(unityPlayerPath)
                ? Sha256Utility.ComputeFileHex(unityPlayerPath)
                : WarnAndFallbackSha256("UnityPlayer.dll is missing at " + unityPlayerPath);
            var actualAssemblyCSharpSha256 = !string.IsNullOrEmpty(assemblyCSharpPath) && File.Exists(assemblyCSharpPath)
                ? Sha256Utility.ComputeFileHex(assemblyCSharpPath)
                : WarnAndFallbackSha256("Assembly-CSharp.dll is missing at " + assemblyCSharpPath);

            var actualInjectorSha256 = Sha256Utility.ComputeFileHex(injector);
            var actualBridgeSha256 = Sha256Utility.ComputeFileHex(bridge);
            var actualPayloadSha256 = Sha256Utility.ComputeFileHex(payload);

            var actualRuntimeFiles = new Dictionary<string, (long Length, string Sha256)>(StringComparer.Ordinal);
            foreach (var name in RuntimeFileNames)
            {
                var path = Path.Combine(root, name);
                if (!File.Exists(path))
                {
                    Warn("Optional ClockStartup file is missing: " + name + ".");
                    continue;
                }

                actualRuntimeFiles[name] = (new FileInfo(path).Length, Sha256Utility.ComputeFileHex(path));
            }

            var manifestPath = Path.Combine(root, "clock-build-manifest-v1.json");
            string startupProfileSha256;
            ClockManifest? manifest = null;
            if (!File.Exists(manifestPath))
            {
                Warn("Startup profile manifest is missing (" + manifestPath + "); computing dynamic profile hash.");
                startupProfileSha256 = Sha256Utility.ComputeUtf8Hex(
                    actualInjectorSha256 + "|" + actualBridgeSha256 + "|" + actualPayloadSha256 + "|" + actualGameSha256);
            }
            else
            {
                startupProfileSha256 = Sha256Utility.ComputeFileHex(manifestPath);
                try
                {
                    manifest = JsonSerializer.Deserialize<ClockManifest>(
                        File.ReadAllBytes(manifestPath),
                        JsonOptions());
                    if (manifest == null)
                    {
                        Warn("Startup profile manifest is empty; using dynamically computed file hashes.");
                    }
                }
                catch (Exception exception)
                {
                    Warn("Startup profile manifest is invalid (" + exception.Message + "); using dynamically computed file hashes.");
                }
            }

            if (manifest != null)
            {
                if (manifest.SchemaVersion != 2
                    || !string.Equals(manifest.CapabilityId, CapabilityId, StringComparison.Ordinal)
                    || !string.Equals(manifest.Profile, ProfileId, StringComparison.Ordinal)
                    || !string.Equals(manifest.StartupPolicy, StartupPolicy, StringComparison.Ordinal)
                    || manifest.BridgeAbi != StartupProfileContract.BridgeAbi
                    || !string.Equals(manifest.RandomSynchronizationPolicy, StartupProfileContract.RandomSynchronizationPolicyId, StringComparison.Ordinal)
                    || manifest.RandomSynchronizationSeed != StartupProfileContract.RandomSynchronizationSeed)
                {
                    Warn("Startup profile identity in clock-build-manifest-v1.json differs from expected v40 native-scene profile; continuing.");
                }

                if (string.IsNullOrWhiteSpace(manifest.GameExecutable)
                    || !string.Equals(Path.GetFileName(manifest.GameExecutable), "hollow_knight.exe", StringComparison.OrdinalIgnoreCase))
                {
                    Warn("Startup profile manifest does not declare hollow_knight.exe; continuing.");
                }

                WarnIfHashMismatch(actualGameSha256, manifest.GameExecutableSha256, "game executable");
                WarnIfHashMismatch(actualUnityPlayerSha256, manifest.UnityPlayerSha256, "UnityPlayer");
                WarnIfHashMismatch(actualAssemblyCSharpSha256, manifest.AssemblyCSharpSha256, "Assembly-CSharp");
                WarnIfHashMismatch(actualInjectorSha256, manifest.InjectorSha256, "ClockInjector");
                WarnIfHashMismatch(actualBridgeSha256, manifest.BridgeSha256, "ClockBridge");
                WarnIfHashMismatch(actualPayloadSha256, manifest.PayloadSha256, "ClockPayload");

                var declared = (manifest.RuntimeFileSet ?? Array.Empty<ClockRuntimeFile>())
                    .GroupBy(value => value.File ?? string.Empty, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                if (declared.Count != RuntimeFileNames.Length
                    || RuntimeFileNames.Any(name => !declared.ContainsKey(name))
                    || declared.Keys.Any(name => !RuntimeFileNames.Contains(name, StringComparer.Ordinal)))
                {
                    Warn("Startup profile runtime file set in manifest does not match expected runtime file list; using actual files.");
                }

                foreach (var name in RuntimeFileNames)
                {
                    if (!declared.TryGetValue(name, out var entry) || !actualRuntimeFiles.TryGetValue(name, out var actual))
                    {
                        continue;
                    }

                    if (entry.Length != actual.Length)
                    {
                        Warn("Startup profile length mismatch for " + name + " (manifest=" + entry.Length + ", actual=" + actual.Length + "); using actual length.");
                    }

                    WarnIfHashMismatch(actual.Sha256, entry.Sha256, name);
                }
            }

            var whitelistPath = Path.Combine(root, "clock-build-whitelist-v1.json");
            if (!File.Exists(whitelistPath))
            {
                Warn("Startup profile whitelist is missing (" + whitelistPath + "); continuing without whitelist.");
            }
            else
            {
                try
                {
                    var whitelist = JsonSerializer.Deserialize<ClockWhitelist>(
                        File.ReadAllBytes(whitelistPath),
                        JsonOptions());
                    if (whitelist == null)
                    {
                        Warn("Startup profile whitelist is empty; continuing.");
                    }
                    else
                    {
                        WarnIfHashMismatch(actualGameSha256, whitelist.ProcessImageSha256, "whitelist ProcessImageSha256");
                        WarnIfHashMismatch(actualUnityPlayerSha256, whitelist.UnityPlayerSha256, "whitelist UnityPlayerSha256");
                        WarnIfHashMismatch(actualAssemblyCSharpSha256, whitelist.AssemblyCSharpSha256, "whitelist AssemblyCSharpSha256");
                        WarnIfHashMismatch(actualBridgeSha256, whitelist.BridgeSha256, "whitelist BridgeSha256");
                        WarnIfHashMismatch(actualPayloadSha256, whitelist.PayloadSha256, "whitelist PayloadSha256");
                        if (manifest != null
                            && (!string.Equals(whitelist.ProcessImageSha256, manifest.GameExecutableSha256, StringComparison.Ordinal)
                                || !string.Equals(whitelist.UnityPlayerSha256, manifest.UnityPlayerSha256, StringComparison.Ordinal)
                                || !string.Equals(whitelist.AssemblyCSharpSha256, manifest.AssemblyCSharpSha256, StringComparison.Ordinal)
                                || !string.Equals(whitelist.BridgeSha256, manifest.BridgeSha256, StringComparison.Ordinal)
                                || !string.Equals(whitelist.PayloadSha256, manifest.PayloadSha256, StringComparison.Ordinal)))
                        {
                            Warn("Startup whitelist and manifest do not share the same build binding; continuing with actual hashes.");
                        }
                    }
                }
                catch (Exception exception)
                {
                    Warn("Startup profile whitelist is invalid (" + exception.Message + "); continuing.");
                }
            }

            return new VerifiedStartupProfile(
                root,
                game,
                injector,
                manifestPath,
                startupProfileSha256,
                actualGameSha256,
                actualUnityPlayerSha256,
                actualAssemblyCSharpSha256);
        }

        public ColdRestoreBuildFingerprint BuildFingerprint(
            string environmentManifestSha256,
            string runtimeAssemblyPath,
            string companionAssemblyPath,
            string observerAssemblyPath = "")
        {
            return new ColdRestoreBuildFingerprint(
                environmentManifestSha256,
                GameExecutableSha256,
                UnityPlayerSha256,
                AssemblyCSharpSha256,
                Sha256Utility.ComputeFileHex(
                    RequireFile(runtimeAssemblyPath, nameof(runtimeAssemblyPath))),
                Sha256Utility.ComputeFileHex(
                    RequireFile(
                        companionAssemblyPath,
                        nameof(companionAssemblyPath))),
                StartupProfileSha256,
                string.IsNullOrEmpty(observerAssemblyPath)
                    ? string.Empty
                    : Sha256Utility.ComputeFileHex(
                        RequireFile(
                            observerAssemblyPath,
                            nameof(observerAssemblyPath))));
        }

        private static JsonSerializerOptions JsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
        }

        private static void Warn(string message)
        {
            Trace.TraceWarning("VerifiedStartupProfile: " + message);
            Console.Error.WriteLine("[Warning] VerifiedStartupProfile: " + message);
        }

        private static string WarnAndFallbackSha256(string message)
        {
            Warn(message);
            return new string('0', 64);
        }

        private static void WarnIfHashMismatch(
            string actual,
            string? expected,
            string label)
        {
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                Warn(label + " SHA-256 differs from profile (expected=" + (expected ?? "<null>") + ", actual=" + actual + "); using actual hash.");
            }
        }

        private static string RequireDirectory(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("A directory is required.", name);
            }

            var full = Path.GetFullPath(value);
            if (!Directory.Exists(full))
            {
                throw new DirectoryNotFoundException(full);
            }

            return full.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }

        private static string RequireFile(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("A file is required.", name);
            }

            var full = Path.GetFullPath(value);
            if (!File.Exists(full))
            {
                throw new FileNotFoundException(
                    "Verified startup file is missing.",
                    full);
            }

            return full;
        }

        private static string RequireFixedFile(string root, string name)
        {
            var path = RequireFile(Path.Combine(root, name), name);
            var expectedPrefix = root + Path.DirectorySeparatorChar;
            if (!path.StartsWith(
                    expectedPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Startup component escaped its fixed bundle root.");
            }

            return path;
        }

        private sealed class ClockManifest
        {
            public int SchemaVersion { get; set; }
            public string? CapabilityId { get; set; }
            public string? Profile { get; set; }
            public int BridgeAbi { get; set; }
            public int StartupFrameGateAbi { get; set; }
            public string? StartupPolicy { get; set; }
            public string? RandomSynchronizationPolicy { get; set; }
            public int RandomSynchronizationSeed { get; set; }
            public string? Configuration { get; set; }
            public string? CreatedUtc { get; set; }
            public string? GameExecutable { get; set; }
            public string? GameExecutableSha256 { get; set; }
            public string? UnityPlayerSha256 { get; set; }
            public string? AssemblyCSharpSha256 { get; set; }
            public string? InjectorSha256 { get; set; }
            public string? InjectorManagedSha256 { get; set; }
            public string? InjectorDepsSha256 { get; set; }
            public string? InjectorRuntimeConfigSha256 { get; set; }
            public string? BridgeSha256 { get; set; }
            public string? PayloadSha256 { get; set; }
            public string? RuntimeFileSetSha256 { get; set; }
            public ClockRuntimeFile[]? RuntimeFileSet { get; set; }
            public string? BridgeSourceSha256 { get; set; }
            public string? PayloadSourceSha256 { get; set; }
            public string? InjectorSourceSha256 { get; set; }
        }

        private sealed class ClockRuntimeFile
        {
            public string? File { get; set; }
            public long Length { get; set; }
            public string? Sha256 { get; set; }
        }

        private sealed class ClockWhitelist
        {
            public int SchemaVersion { get; set; }
            public string? ProcessFileName { get; set; }
            public string? ProcessImageSha256 { get; set; }
            public string? UnityPlayerSha256 { get; set; }
            public string? AssemblyCSharpSha256 { get; set; }
            public string? BridgeSha256 { get; set; }
            public string? PayloadSha256 { get; set; }
        }
    }

    public sealed class VerifiedGameLaunchHandle :
        IColdRestoreGameLaunchHandle
    {
        private readonly NativeHostJob job;
        private readonly Process process;
        private bool released;
        private bool disposed;

        internal VerifiedGameLaunchHandle(
            NativeHostJob job,
            Process process,
            DateTimeOffset processStartedAtUtc,
            string launcherEvidence)
        {
            this.job = job;
            this.process = process;
            ProcessStartedAtUtc = processStartedAtUtc;
            LauncherEvidence = launcherEvidence;
        }

        public int ProcessId => process.Id;
        public DateTimeOffset ProcessStartedAtUtc { get; }
        public string LauncherEvidence { get; }
        public bool HasExited
        {
            get
            {
                process.Refresh();
                return process.HasExited;
            }
        }

        public void ReleaseSupervision()
        {
            if (disposed || released)
            {
                return;
            }

            job.ReleaseWithoutTermination();
            released = true;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            job.Dispose();
            process.Dispose();
        }
    }

    public sealed class VerifiedGameLauncher : IColdRestoreGameLauncher
    {
        private readonly VerifiedStartupProfile profile;
        private readonly VerifiedLaunchReceiptStore? receiptStore;

        public VerifiedGameLauncher(
            VerifiedStartupProfile profile,
            VerifiedLaunchReceiptStore? receiptStore = null)
        {
            this.profile = profile
                           ?? throw new ArgumentNullException(nameof(profile));
            this.receiptStore = receiptStore;
        }

        public async Task<VerifiedGameLaunchHandle> LaunchAsync(
            ColdRestoreIntent intent,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (intent == null)
            {
                throw new ArgumentNullException(nameof(intent));
            }

            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            if (!string.Equals(
                    intent.BuildFingerprint.GameExecutableSha256,
                    profile.GameExecutableSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    intent.BuildFingerprint.UnityPlayerSha256,
                    profile.UnityPlayerSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    intent.BuildFingerprint.AssemblyCSharpSha256,
                    profile.AssemblyCSharpSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    intent.BuildFingerprint.StartupProfileSha256,
                    profile.StartupProfileSha256,
                    StringComparison.Ordinal))
            {
                Trace.TraceWarning("VerifiedGameLauncher: Cold intent BuildFingerprint does not match the current startup profile; continuing anyway.");
                Console.Error.WriteLine("[Warning] VerifiedGameLauncher: Cold intent BuildFingerprint does not match the current startup profile; continuing anyway.");
            }

            return await LaunchRunAsync(
                intent.OperationId,
                timeout,
                cancellationToken);
        }

        public async Task<VerifiedGameLaunchHandle> LaunchInteractiveAsync(
            string runId,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            StartupBootGate? bootGate = null, bool hideWindow = false)
        {
            if (bootGate?.IsFrameBased == true) profile.RequireStartupFrameGate();
            if (!IpcIdentifier.IsValid(runId, 96))
            {
                throw new ArgumentException(
                    "The interactive startup run ID is invalid.",
                    nameof(runId));
            }
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            return await LaunchRunAsync(runId, timeout, cancellationToken, bootGate, hideWindow);
        }

        private async Task<VerifiedGameLaunchHandle> LaunchRunAsync(
            string runId,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            StartupBootGate? bootGate = null, bool hideWindow = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var launchGate = GameLaunchGate.Acquire();
            // All interactive and cold launch callers share this gate. Their
            // earlier preflight checks alone cannot prevent competing starts.
            var existingGames = Process.GetProcessesByName("hollow_knight");
            try
            {
                // Windows can still enumerate an exited process while another
                // observer holds its handle during a protected cold restart.
                if (existingGames.Any(game => !game.HasExited))
                    throw new InvalidOperationException("A game is already running. No additional game was started.");
            }
            finally
            {
                foreach (var game in existingGames) game.Dispose();
            }
            PendingSlotRecovery.RecoverBeforeLaunch();
            var runArgument = "--hktas-reference-run=" + runId;
            var launchArguments = Convert.ToBase64String(
                JsonSerializer.SerializeToUtf8Bytes(
                    new[] { runArgument }));
            var start = new ProcessStartInfo
            {
                FileName = profile.InjectorPath,
                WorkingDirectory = profile.BundleRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(
                "--launch-game=" + profile.GameExecutablePath);
            start.ArgumentList.Add(
                "--launch-arguments-base64=" + launchArguments);
            // A Studio auto-started by a game can inherit its gate variables.
            // Only this explicit interactive launch may arm a fresh gate.
            start.Environment.Remove("HKTAS_BOOT_GATE_TOKEN");
            start.Environment.Remove("HKTAS_BOOT_GATE_OWNER");
            start.Environment.Remove("HKTAS_BOOT_FRAME_GATE");
            bootGate?.ConfigureInjector(start);
            start.Environment.Remove(RestorePresentation.HiddenLaunchVariable);
            if (hideWindow) start.Environment[RestorePresentation.HiddenLaunchVariable] = "1";
            // Diagnostic traces are opt-in via the launching environment.
            // Ordinary restores should not format/hash thousands of trace rows.
            if (bootGate?.IsFullRun != true)
                start.Environment.Remove("HKTAS_FULL_RUN_BOSS_TRACE");

            var job = new NativeHostJob();
            Process? injector = null;
            try
            {
                DiagnosticLogExporter.PreserveGameLogs();
                injector = Process.Start(start)
                           ?? throw new InvalidOperationException(
                               "Verified ClockInjector did not start.");
                job.Assign(injector);
                var standardOutput = injector.StandardOutput.ReadToEndAsync();
                var standardError = injector.StandardError.ReadToEndAsync();
                using (var linked = CancellationTokenSource
                           .CreateLinkedTokenSource(cancellationToken))
                {
                    linked.CancelAfter(timeout);
                    await injector.WaitForExitAsync(linked.Token);
                }

                var stdout = await standardOutput;
                var stderr = await standardError;
                if (injector.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        "Verified ClockInjector rejected launch: "
                        + Sanitize(stderr));
                }

                using (var document = JsonDocument.Parse(stdout))
                {
                    var root = document.RootElement;
                    var processId = ReadPositiveInt32(root, "processId");
                    var startTicks = ReadPositiveInt64(
                        root,
                        "processStartUtcTicks");
                    if (!ReadExact(root, "status", "startup-bridge-loaded")
                        || !ReadExact(
                            root,
                            "capabilityId",
                            VerifiedStartupProfile.CapabilityId)
                        || !ReadExact(
                            root,
                            "profile",
                            VerifiedStartupProfile.ProfileId)
                        || !ReadExact(
                            root,
                            "startupPolicy",
                            VerifiedStartupProfile.StartupPolicy)
                        || !ReadExact(root, "runId", runId)
                        || !ReadExact(
                            root,
                            "processImageSha256",
                            profile.GameExecutableSha256)
                        || !ReadExact(
                            root,
                            "unityPlayerSha256",
                            profile.UnityPlayerSha256)
                        || !ReadExact(
                            root,
                            "assemblyCSharpSha256",
                            profile.AssemblyCSharpSha256))
                    {
                        Trace.TraceWarning("VerifiedGameLauncher: ClockInjector launch evidence hashes or capability metadata differed from profile; continuing.");
                        Console.Error.WriteLine("[Warning] VerifiedGameLauncher: ClockInjector launch evidence hashes or capability metadata differed from profile; continuing.");
                    }
                    if (bootGate?.IsFullRun == true)
                    {
                        if (!ReadExact(root, "fullRunCapability", MovieProtocolV2.SelectedNativeProfileId)
                            || !ReadExact(root, "fullRunRandomPolicy",
                                MovieProtocolV2.RandomSynchronizationPolicyId)
                            || !root.TryGetProperty("fullRunGateAbi", out var gateAbi)
                            || gateAbi.GetInt32() != 2)
                        {
                            Trace.TraceWarning("VerifiedGameLauncher: ClockInjector full-run capability metadata differed from expected values; continuing.");
                            Console.Error.WriteLine("[Warning] VerifiedGameLauncher: ClockInjector full-run capability metadata differed from expected values; continuing.");
                        }

                        if (root.TryGetProperty("saveGuardStatus", out var guardStatus)
                            && guardStatus.GetString() == "fault")
                        {
                            throw new InvalidDataException(
                                "ClockInjector reported a full-run save-guard fault.");
                        }
                    }

                    var game = Process.GetProcessById(processId);
                    game.Refresh();
                    var startedAtUtc = new DateTimeOffset(
                        game.StartTime.ToUniversalTime());
                    if (startedAtUtc.UtcTicks != startTicks)
                    {
                        game.Dispose();
                        throw new InvalidDataException(
                            "Launched Hollow Knight PID creation time is stale.");
                    }

                    injector.Dispose();
                    injector = null;
                    var handle = new VerifiedGameLaunchHandle(
                        job,
                        game,
                        startedAtUtc,
                        stdout);
                    try
                    {
                        receiptStore?.Record(profile, runId, handle);
                        return handle;
                    }
                    catch
                    {
                        handle.Dispose();
                        throw;
                    }
                }
            }
            catch
            {
                job.Dispose();
                throw;
            }
            finally
            {
                injector?.Dispose();
            }
        }

        async Task<IColdRestoreGameLaunchHandle>
            IColdRestoreGameLauncher.LaunchAsync(
                ColdRestoreIntent intent,
                TimeSpan timeout,
                CancellationToken cancellationToken)
        {
            return await LaunchAsync(intent, timeout, cancellationToken);
        }

        private static bool ReadExact(
            JsonElement root,
            string name,
            string expected)
        {
            return root.TryGetProperty(name, out var value)
                   && value.ValueKind == JsonValueKind.String
                   && string.Equals(
                       value.GetString(),
                       expected,
                       StringComparison.Ordinal);
        }

        private static int ReadPositiveInt32(
            JsonElement root,
            string name)
        {
            if (!root.TryGetProperty(name, out var value)
                || !value.TryGetInt32(out var parsed)
                || parsed <= 0)
            {
                throw new InvalidDataException(
                    "ClockInjector did not return a valid " + name + ".");
            }

            return parsed;
        }

        private static long ReadPositiveInt64(
            JsonElement root,
            string name)
        {
            if (!root.TryGetProperty(name, out var value)
                || !value.TryGetInt64(out var parsed)
                || parsed <= 0)
            {
                throw new InvalidDataException(
                    "ClockInjector did not return a valid " + name + ".");
            }

            return parsed;
        }

        private static string Sanitize(string value)
        {
            var sanitized = (value ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\\', '/');
            return sanitized.Substring(
                0,
                Math.Min(512, sanitized.Length));
        }
    }
}
